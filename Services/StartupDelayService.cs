using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Text.RegularExpressions;

namespace SMDWin.Services
{
    /// <summary>
    /// What Windows itself measured as slowing the start, from Diagnostics-Performance/Operational:
    /// 101 = an application, 103 = a service, 102 = a driver "took longer than usual to start" — with the extra time
    /// (DegradationTime, ms). Per file name: the largest delay of the last 30 days, how often, and when last.
    /// </summary>
    public static class StartupDelayService
    {
        public sealed record Delay(string File, string Friendly, int DelayMs, int Count, DateTime Last, int Kind);

        private const string LogName = "Microsoft-Windows-Diagnostics-Performance/Operational";

        public static List<Delay> Recent(int days = 30)
        {
            var rows = new List<(string file, string friendly, int ms, DateTime when, int id)>();
            try
            {
                long span = (long)TimeSpan.FromDays(days).TotalMilliseconds;
                string xpath = $"*[System[(EventID=101 or EventID=102 or EventID=103) and TimeCreated[timediff(@SystemTime) <= {span}]]]";
                using var reader = new EventLogReader(new EventLogQuery(LogName, PathType.LogName, xpath) { ReverseDirection = true });
                int n = 0;
                for (EventRecord? r = reader.ReadEvent(); r != null && n < 300; r = reader.ReadEvent(), n++)
                {
                    using (r)
                    {
                        string xml = r.ToXml();
                        string file = Field(xml, "Name");
                        string friendly = Field(xml, "FriendlyName");
                        int.TryParse(Field(xml, "DegradationTime"), out int ms);
                        if (file.Length == 0 || ms <= 0 || r.TimeCreated == null) continue;
                        rows.Add((System.IO.Path.GetFileName(file), friendly, ms, r.TimeCreated.Value, r.Id));
                    }
                }
            }
            catch (Exception ex) { AppLogger.Debug($"StartupDelayService: {ex.Message}"); }

            return rows.GroupBy(x => x.file, StringComparer.OrdinalIgnoreCase)
                       .Select(g => new Delay(g.Key, g.Select(x => x.friendly).FirstOrDefault(f => f.Length > 0) ?? g.Key,
                                              g.Max(x => x.ms), g.Count(), g.Max(x => x.when), g.First().id))
                       .OrderByDescending(d => d.DelayMs)
                       .ToList();
        }

        private static string Field(string xml, string name)
        {
            var m = Regex.Match(xml, $"<Data Name=['\"]{name}['\"]>([^<]*)</Data>");
            return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : "";
        }
    }
}
