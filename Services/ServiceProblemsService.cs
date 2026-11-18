using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;

namespace SMDWin.Services
{
    /// <summary>
    /// Services that crashed or did not start in the last days, from the System event log (Service Control Manager):
    /// 7031 / 7034 = stopped unexpectedly; 7000 / 7009 / 7011 / 7022 / 7023 / 7024 = did not start or failed with an error.
    /// Grouped per service and kind, newest first.
    /// </summary>
    public static class ServiceProblemsService
    {
        public sealed record Problem(string Service, bool Crashed, DateTime Last, int Count);

        public static List<Problem> Recent(int days = 7, int max = 6)
        {
            var found = new List<(string svc, bool crash, DateTime when)>();
            try
            {
                long ms = (long)TimeSpan.FromDays(days).TotalMilliseconds;
                string xpath = "*[System[Provider[@Name='Service Control Manager'] and " +
                               "(EventID=7000 or EventID=7009 or EventID=7011 or EventID=7022 or EventID=7023 or EventID=7024 or EventID=7031 or EventID=7034) and " +
                               $"TimeCreated[timediff(@SystemTime) <= {ms}]]]";
                using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true });
                int n = 0;
                for (EventRecord? r = reader.ReadEvent(); r != null && n < 400; r = reader.ReadEvent(), n++)
                {
                    using (r)
                    {
                        string svc = r.Properties.Count > 0 ? r.Properties[0].Value?.ToString() ?? "" : "";
                        if (svc.Length == 0 || r.TimeCreated == null) continue;
                        found.Add((svc, r.Id is 7031 or 7034, r.TimeCreated.Value));
                    }
                }
            }
            catch (Exception ex) { AppLogger.Debug($"ServiceProblemsService: {ex.Message}"); }

            return found.GroupBy(f => (f.svc, f.crash))
                        .Select(g => new Problem(g.Key.svc, g.Key.crash, g.Max(x => x.when), g.Count()))
                        .OrderByDescending(p => p.Last)
                        .Take(max)
                        .ToList();
        }
    }
}
