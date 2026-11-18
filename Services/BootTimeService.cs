using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;

namespace SMDWin.Services
{
    /// <summary>One boot cycle's timing breakdown, parsed from Event ID 100
    /// (Microsoft-Windows-Diagnostics-Performance/Operational).</summary>
    public class BootTimeEntry
    {
        public DateTime BootDate { get; set; }
        /// <summary>Total time from power-on to desktop-ready, in ms.</summary>
        public int TotalBootMs { get; set; }
        /// <summary>Kernel + driver init + boot-critical services (POST is NOT included —
        /// Windows can't measure firmware time, only its own boot path).</summary>
        public int MainPathBootMs { get; set; }
        /// <summary>Non-critical services and startup apps that continue loading
        /// after the desktop appears (explains "desktop is up but still slow" feeling).</summary>
        public int PostBootMs { get; set; }
        public int BootTimeExclDelayedServicesMs { get; set; }
        /// <summary>How long the sign-in screen waited for the user (already taken out of TotalBootMs / MainPathBootMs).</summary>
        public int LogonWaitMs { get; set; }
        public bool IsDegradation { get; set; }   // event 200 — Windows detected reboot was abnormally slow
        public string BootType { get; set; } = "";  // "Cold" / "Resume" / "Fast" — best-effort label

        public int PostBootDisplay => Math.Max(0, TotalBootMs - MainPathBootMs);
    }

    public static class BootTimeService
    {
        private const string LogName = "Microsoft-Windows-Diagnostics-Performance/Operational";

        /// <summary>Reads the last N boot performance records. Returns empty list (not
        /// an exception) if the log is disabled, empty, or inaccessible — the log is
        /// disabled by default on some minimal/Server installs and on very old Windows
        /// builds, and the caller should show a friendly "not available" state rather
        /// than crash the Startup page.</summary>
        public static async Task<List<BootTimeEntry>> GetRecentBootsAsync(int maxCount = 10)
        {
            return await Task.Run(() =>
            {
                var results = new List<BootTimeEntry>();
                try
                {
                    // Event 100 = "Windows has started up" — fired once per boot, contains
                    // the full timing breakdown as structured XML data (UserData, not Properties —
                    // this provider uses a manifest schema, so FormatDescription/Properties
                    // are empty; we must parse record.ToXml()).
                    string query = "*[System[(EventID=100)]]";
                    var evQuery = new EventLogQuery(LogName, PathType.LogName, query)
                    {
                        ReverseDirection = true
                    };
                    using var reader = new EventLogReader(evQuery);
                    reader.BatchSize = 10;

                    EventRecord? record;
                    int count = 0;
                    while ((record = reader.ReadEvent()) != null && count < maxCount)
                    {
                        using (record)
                        {
                            try
                            {
                                var entry = ParseBootEvent(record);
                                if (entry != null) { results.Add(entry); count++; }
                            }
                            catch (Exception ex) { AppLogger.Debug($"BootTimeService parse: {ex.Message}"); }
                        }
                    }
                }
                catch (EventLogNotFoundException)
                {
                    // Log doesn't exist on this Windows edition/version — not an error,
                    // just means the feature isn't available. Caller shows "not available" UI.
                }
                catch (UnauthorizedAccessException)
                {
                    // Needs admin on some configurations — same "not available" treatment.
                }
                catch (Exception ex) { AppLogger.Debug($"BootTimeService: {ex.Message}"); }

                // Mark degradation flags from Event 200 (fired only when Windows itself
                // detected the boot was abnormally slow vs. the user's own history).
                try { MarkDegradations(results); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); /* best-effort */ }

                return results;
            });
        }

        private static BootTimeEntry? ParseBootEvent(EventRecord record)
        {
            if (!record.TimeCreated.HasValue) return null;

            var doc = new XmlDocument();
            doc.LoadXml(record.ToXml());
            var nsmgr = new XmlNamespaceManager(doc.NameTable);
            nsmgr.AddNamespace("e", "http://schemas.microsoft.com/win/2004/08/events/event");

            int GetInt(string field)
            {
                var node = doc.SelectSingleNode($"//e:EventData/e:Data[@Name='{field}']", nsmgr)
                        ?? doc.SelectSingleNode($"//e:UserData//*[local-name()='{field}']", nsmgr);
                return node != null && int.TryParse(node.InnerText, out int v) ? v : 0;
            }

            int total     = GetInt("BootTime");
            int mainPath  = GetInt("MainPathBootTime");
            int exclDelay = GetInt("BootTimeExcludingDelayedServices");
            // the time the sign-in screen waited for the password is inside BootTime and MainPathBootTime: a PC turned on and
            // left at the sign-in screen for 8 minutes "booted in 8 minutes". It is taken out, so the time is the computer's own.
            int logonWait = Math.Max(0, GetInt("UserLogonWaitDuration"));

            if (total <= 0) return null; // malformed/unrelated record — skip
            if (logonWait > 0 && logonWait < total)
            {
                total -= logonWait;
                if (mainPath > logonWait) mainPath -= logonWait;
                if (exclDelay > logonWait) exclDelay -= logonWait;
            }

            return new BootTimeEntry
            {
                BootDate       = record.TimeCreated.Value,
                TotalBootMs    = total,
                MainPathBootMs = mainPath > 0 ? mainPath : total,
                BootTimeExclDelayedServicesMs = exclDelay,
                LogonWaitMs    = logonWait,
                BootType       = total < 8000 ? "Fast/Resume" : "Cold",
            };
        }

        private static void MarkDegradations(List<BootTimeEntry> entries)
        {
            if (entries.Count == 0) return;
            try
            {
                string query = "*[System[(EventID=200)]]";
                var evQuery = new EventLogQuery(LogName, PathType.LogName, query) { ReverseDirection = true };
                using var reader = new EventLogReader(evQuery);
                reader.BatchSize = 10;
                var degradedTimes = new List<DateTime>();
                EventRecord? record;
                int count = 0;
                while ((record = reader.ReadEvent()) != null && count < 10)
                {
                    using (record)
                    {
                        if (record.TimeCreated.HasValue) degradedTimes.Add(record.TimeCreated.Value);
                        count++;
                    }
                }
                foreach (var e in entries)
                    if (degradedTimes.Any(d => Math.Abs((d - e.BootDate).TotalMinutes) < 5))
                        e.IsDegradation = true;
            }
            catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); /* event 200 is a bonus signal, not critical */ }
        }
    }
}
