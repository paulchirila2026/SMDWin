using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    /// <summary>
    /// Stability history (like the Windows Reliability Monitor) over the last N days, read from the System and Application
    /// logs: blue screens, unexpected shutdowns, applications that closed by themselves / hung, hardware errors (WHEA).
    /// Used on the BSOD → Stability page, so the page is useful even when there is no BSOD (the most common case).
    /// </summary>
    public static class StabilityService
    {
        public enum Kind { BlueScreen, UnexpectedShutdown, AppCrash, AppHang, Hardware }

        public sealed class StabilityEvent
        {
            public DateTime Time { get; set; }
            public Kind Kind { get; set; }
            public string Title { get; set; } = "";
            public string Detail { get; set; } = "";
        }

        private sealed record Q(string Log, string Provider, int[] Ids, Kind Kind);

        private static readonly Q[] Queries =
        {
            new("System", "Microsoft-Windows-WER-SystemErrorReporting", new[] { 1001 }, Kind.BlueScreen),
            new("System", "Microsoft-Windows-Kernel-Power", new[] { 41 }, Kind.UnexpectedShutdown),
            new("System", "Microsoft-Windows-WHEA-Logger", new[] { 17, 18, 19, 47 }, Kind.Hardware),
            new("Application", "Application Error", new[] { 1000 }, Kind.AppCrash),
            new("Application", "Application Hang", new[] { 1002 }, Kind.AppHang),
        };

        /// <summary>"0x1A MEMORY_MANAGEMENT" for the (decimal) code in the Kernel-Power 41 event; hex only for an unknown code.</summary>
        internal static string StopCodeText(string decimalCode)
        {
            if (!uint.TryParse(decimalCode, out uint code)) return decimalCode;
            string name = KernelDumpParser.DescribeBugCheck(code).Name;
            return name.StartsWith("BUGCHECK_0x", StringComparison.Ordinal) ? $"0x{code:X}" : $"0x{code:X} {name}";
        }

        public static Task<List<StabilityEvent>> GetAsync(int days, CancellationToken ct = default) => Task.Run(() =>
        {
            var list = new List<StabilityEvent>();
            long ms = (long)TimeSpan.FromDays(days).TotalMilliseconds;
            foreach (var q in Queries)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    string ids = string.Join(" or ", q.Ids.Select(i => $"EventID={i}"));
                    string xpath = $"*[System[Provider[@Name='{q.Provider}'] and ({ids}) and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
                    var query = new EventLogQuery(q.Log, PathType.LogName, xpath) { ReverseDirection = true };
                    using var reader = new EventLogReader(query);
                    EventRecord? rec;
                    int n = 0;
                    while (!ct.IsCancellationRequested && n < 500 && (rec = reader.ReadEvent()) != null)
                    {
                        using (rec)
                        {
                            n++;
                            var ev = new StabilityEvent { Time = rec.TimeCreated ?? DateTime.Now, Kind = q.Kind };
                            string P(int i) { try { return rec.Properties.Count > i ? rec.Properties[i]?.Value?.ToString() ?? "" : ""; } catch { return ""; } }
                            switch (q.Kind)
                            {
                                case Kind.BlueScreen:
                                    ev.Title = "Blue screen (BSOD)";
                                    ev.Detail = P(0);
                                    break;
                                case Kind.UnexpectedShutdown:
                                    ev.Title = "Unexpected shutdown";
                                    // BugcheckCode (param 0) != 0 → it was actually a BSOD
                                    ev.Detail = P(0) is string bc && bc != "0" && bc.Length > 0
                                        ? "Crash with stop code " + StopCodeText(bc)
                                        : "Power loss, freeze or forced power-off";
                                    break;
                                case Kind.AppCrash:
                                    ev.Title = P(0).Length > 0 ? P(0) : "Application crash";
                                    ev.Detail = P(3).Length > 0 ? $"Faulting module: {P(3)}" : "";
                                    break;
                                case Kind.AppHang:
                                    ev.Title = P(0).Length > 0 ? P(0) : "Application stopped responding";
                                    ev.Detail = "Stopped responding and was closed";
                                    break;
                                case Kind.Hardware:
                                    ev.Title = rec.Id == 18 ? "Fatal hardware error" : "Corrected hardware error";
                                    ev.Detail = rec.Id switch { 47 => "Memory (RAM)", 19 => "CPU / cache", 17 => "PCIe / bus", _ => "CPU / memory / bus" };
                                    break;
                            }
                            list.Add(ev);
                        }
                    }
                }
                catch (EventLogNotFoundException) { }
                catch (Exception ex) { AppLogger.Debug($"StabilityService {q.Provider}: {ex.Message}"); }
            }
            return list.OrderByDescending(e => e.Time).ToList();
        }, ct);
    }
}
