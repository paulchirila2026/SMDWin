using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    /// <summary>
    /// Result of a scan for the Event Viewer page. The count (<see cref="Stamps"/>) covers the whole range, and the full
    /// text is read only for what is shown: the newest events (<see cref="Entries"/>) and one representative of each
    /// older group (<see cref="GroupHeads"/>).
    /// </summary>
    public sealed class EventScan
    {
        public List<EventLogEntry> Entries { get; } = new();
        public List<EventLogEntry> GroupHeads { get; } = new();
        public List<EventStamp> Stamps { get; } = new();
        /// <summary>A log holds more events than the counting limit.</summary>
        public bool CountCapped { get; set; }
        /// <summary>The time allotted to the scan ran out before the end of the log.</summary>
        public bool Partial { get; set; }
    }

    public class EventLogService
    {
        private static readonly string[] WatchedLogs = { "System", "Application" };

        /// <summary>How many events from a log get their full text (the newest).</summary>
        public const int ListCapPerLog = 200;
        /// <summary>Maximum number of events counted per log.</summary>
        public const int CountCapPerLog = 10_000;
        private static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DescribeBudget = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Scan for the Event Viewer page. Compared with <see cref="GetEventsAsync"/>:
        ///  • it counts ALL events in the range (up to <see cref="CountCapPerLog"/> per log), not only the newest few
        ///    hundred — otherwise the per-day chart and "×N" would show only the end of the range;
        ///  • the message is the real Windows description ("The service terminated unexpectedly…"), not the raw list of
        ///    event values; if the description cannot be obtained, the values remain.
        /// The description is expensive (Windows loads the source's message file), so it is requested only for the events
        /// shown, within a total time budget.
        /// </summary>
        public async Task<EventScan> ScanAsync(DateTime from, DateTime to, string levelFilter = "All",
            string searchText = "", System.Threading.CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                var scan = new EventScan();
                string search = searchText?.Trim() ?? "";   // used only in C#, never in the XPath query
                bool hasSearch = search.Length > 0;
                var total = System.Diagnostics.Stopwatch.StartNew();
                var describing = new System.Diagnostics.Stopwatch();
                var seen = new HashSet<EventGroupKey>();
                var noDescription = new HashSet<(string, long)>();          // sources without a message file: not tried again
                var keyMatches = new Dictionary<EventGroupKey, bool>();     // the search, per group

                string Describe(EventRecord record, string source, long id)
                {
                    if (describing.Elapsed < DescribeBudget && !noDescription.Contains((source, id)))
                    {
                        describing.Start();
                        try
                        {
                            string? text = record.FormatDescription();
                            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                        }
                        catch (Exception ex) { AppLogger.Debug($"EventLogService.Describe {source}/{id}: {ex.Message}"); }
                        finally { describing.Stop(); }
                        noDescription.Add((source, id));
                    }
                    return PropertiesText(record);
                }

                foreach (var logName in WatchedLogs)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        var src = AnalyzedWindows.LogSource(logName);   // this PC, or the .evtx of a Windows on another drive
                        var evQuery = new EventLogQuery(src.path, src.type, BuildQuery(logName, from, to, levelFilter))
                        {
                            ReverseDirection = true     // newest first
                        };
                        using var reader = new EventLogReader(evQuery);
                        reader.BatchSize = 64;

                        int listed = 0, counted = 0;
                        EventRecord? record;
                        while (!ct.IsCancellationRequested && (record = reader.ReadEvent()) != null)
                        {
                            using (record)
                            {
                                if (counted >= CountCapPerLog) { scan.CountCapped = true; break; }
                                if (total.Elapsed > ScanBudget) { scan.Partial = true; break; }
                                try
                                {
                                    string level = GetLevelName(record.Level ?? 0);
                                    string source = record.ProviderName ?? "";
                                    long id = record.Id;
                                    DateTime time = record.TimeCreated ?? DateTime.MinValue;
                                    var key = new EventGroupKey(logName, source, id, level);

                                    string? message = null;
                                    if (hasSearch)
                                    {
                                        // match on source, ID and description (the same for the whole group), then on the event values
                                        if (!keyMatches.TryGetValue(key, out bool groupMatches))
                                        {
                                            message = Describe(record, source, id);
                                            groupMatches = source.Contains(search, StringComparison.OrdinalIgnoreCase)
                                                        || id.ToString().Contains(search, StringComparison.Ordinal)
                                                        || message.Contains(search, StringComparison.OrdinalIgnoreCase);
                                            keyMatches[key] = groupMatches;
                                        }
                                        if (!groupMatches && !PropertiesText(record).Contains(search, StringComparison.OrdinalIgnoreCase))
                                            continue;
                                    }

                                    counted++;
                                    scan.Stamps.Add(new EventStamp(time, level, logName, source, id));

                                    bool newGroup = seen.Add(key);
                                    bool inList = listed < ListCapPerLog;
                                    if (!inList && !newGroup) continue;

                                    message ??= Describe(record, source, id);
                                    var entry = new EventLogEntry
                                    {
                                        TimeCreated = time,
                                        Level       = level,
                                        Source      = source,
                                        EventId     = id,
                                        Message     = message.Length > 1500 ? message[..1500] + "…" : message,
                                        LogName     = logName,
                                    };
                                    if (inList) { scan.Entries.Add(entry); listed++; }
                                    else scan.GroupHeads.Add(entry);
                                }
                                catch (Exception ex) { AppLogger.Debug($"EventLogService.ScanAsync record: {ex.Message}"); }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // inaccessible log: appears as a row in the table, with the reason
                        var now = DateTime.Now;
                        scan.Stamps.Add(new EventStamp(now, "Error", logName, "SMDWin", 0));
                        scan.Entries.Add(new EventLogEntry
                        {
                            TimeCreated = now,
                            Level       = "Error",
                            Source      = "SMDWin",
                            Message     = string.Format(LanguageService.S("Events", "ReadLogErrorFmt"), logName, ex.Message),
                            LogName     = logName,
                        });
                    }
                }

                scan.Entries.Sort((a, b) => b.TimeCreated.CompareTo(a.TimeCreated));
                AppLogger.Info($"EventLogService.ScanAsync: {scan.Stamps.Count} events counted, {scan.Entries.Count + scan.GroupHeads.Count} described " +
                               $"({describing.Elapsed.TotalMilliseconds:F0} ms), total {total.Elapsed.TotalMilliseconds:F0} ms" +
                               (scan.CountCapped ? ", count capped" : "") + (scan.Partial ? ", partial" : ""));
                return scan;
            }, ct);
        }

        /// <summary>The event's raw values on one line — fallback when the description cannot be obtained.</summary>
        private static string PropertiesText(EventRecord record)
        {
            try
            {
                var sb = new System.Text.StringBuilder(128);
                int taken = 0;
                foreach (var p in record.Properties)
                {
                    if (p?.Value is byte[]) continue;
                    var v = p?.Value?.ToString();
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    if (sb.Length > 0) sb.Append("  ·  ");
                    sb.Append(v);
                    if (++taken >= 6 || sb.Length > 400) break;
                }
                return sb.ToString();
            }
            catch (Exception ex) { AppLogger.Debug($"EventLogService.PropertiesText: {ex.Message}"); return ""; }
        }

        public async Task<List<EventLogEntry>> GetEventsAsync(
            DateTime from, DateTime to,
            string levelFilter = "All",
            string searchText = "",
            System.Threading.CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                var results = new List<EventLogEntry>();
                // searchText used in C# filter only — never interpolated into XPath (injection prevention)
                string safeSearch = searchText?.Trim() ?? "";

                int maxPerLog = (to - from).TotalDays > 7 ? 100 : 200;

                foreach (var logName in WatchedLogs)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        var query = BuildQuery(logName, from, to, levelFilter);
                        var src = AnalyzedWindows.LogSource(logName);
                        var evQuery = new EventLogQuery(src.path, src.type, query)
                        {
                            ReverseDirection = true
                        };
                        using var reader = new EventLogReader(evQuery);
                        reader.BatchSize = 20; // reads 20 records at a time instead of 1

                        EventRecord? record;
                        int count = 0;
                        while (!ct.IsCancellationRequested &&
                               (record = reader.ReadEvent()) != null && count < maxPerLog)
                        {
                            using (record)
                            {
                                try
                                {
                                    var level   = GetLevelName(record.Level ?? 0);
                                    var message = "";
                                    try
                                    {
                                        if (record.Properties.Count > 0)
                                        {
                                            var sb = new System.Text.StringBuilder(128);
                                            int taken = 0;
                                            foreach (var p in record.Properties)
                                            {
                                                if (p?.Value is byte[]) continue;
                                                var v = p?.Value?.ToString();
                                                if (string.IsNullOrWhiteSpace(v)) continue;
                                                if (sb.Length > 0) sb.Append("  ·  ");
                                                sb.Append(v);
                                                if (++taken >= 6 || sb.Length > 400) break;
                                            }
                                            message = sb.ToString();

                                            // EventLog 6013: uptime in seconds as the first property — the most useful "binary" case hidden above;
                                            // shown formatted, not empty.
                                            if (record.ProviderName == "EventLog" && record.Id == 6013 &&
                                                record.Properties.Count > 0 &&
                                                record.Properties[0]?.Value is uint upSeconds)
                                            {
                                                var upSpan = TimeSpan.FromSeconds(upSeconds);
                                                message = $"{(int)upSpan.TotalHours}h {upSpan.Minutes:D2}m — {message}".TrimEnd(' ', '·');
                                            }
                                        }
                                        if (string.IsNullOrEmpty(message))
                                            message = record.FormatDescription() ?? "";
                                    }
                                    catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); /* corrupt provider — leave the message empty */ }

                                    // Truncate early to avoid allocating huge strings for search
                                    if (message.Length > 500) message = message[..500] + "…";

                                    // C# filter — no XPath injection risk
                                    if (!string.IsNullOrEmpty(safeSearch) &&
                                        !message.Contains(safeSearch, StringComparison.OrdinalIgnoreCase) &&
                                        !(record.ProviderName?.Contains(safeSearch, StringComparison.OrdinalIgnoreCase) ?? false))
                                        continue;

                                    results.Add(new EventLogEntry
                                    {
                                        TimeCreated = record.TimeCreated ?? DateTime.MinValue,
                                        Level       = level,
                                        Source      = record.ProviderName ?? "",
                                        EventId     = record.Id,
                                        Message     = message.Length > 300 ? message[..300] + "…" : message,
                                        LogName     = logName
                                    });
                                    count++;
                                }
                                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); /* skip malformed records */ }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        results.Add(new EventLogEntry
                        {
                            TimeCreated = DateTime.Now,
                            Level       = "Error",
                            Source      = "SMDWin",
                            Message     = string.Format(LanguageService.S("Events", "ReadLogErrorFmt"), logName, ex.Message),
                            LogName     = logName
                        });
                    }
                }

                results.Sort((a, b) => b.TimeCreated.CompareTo(a.TimeCreated));
                return results;
            }, ct);
        }

        private static string BuildQuery(string logName, DateTime from, DateTime to, string levelFilter)
        {
            var fromStr = from.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var toStr = to.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            var levelXml = levelFilter switch
            {
                "Critical" => " and Level=1",
                "Error"    => " and Level=2",
                "Warning"  => " and Level=3",
                // explicit list: "Level<=3" would also match level 0 (LogAlways), which is neither an error nor a warning
                "Errors & Warnings" => " and (Level=1 or Level=2 or Level=3)",
                _ => ""
            };

            return $"*[System[TimeCreated[@SystemTime>='{fromStr}' and @SystemTime<='{toStr}']{levelXml}]]";
        }

        private static string GetLevelName(byte level) => level switch
        {
            1 => "Critical",
            2 => "Error",
            3 => "Warning",
            4 => "Information",
            5 => "Verbose",
            0 => "Information",
            _ => "Unknown"
        };
    }
}
