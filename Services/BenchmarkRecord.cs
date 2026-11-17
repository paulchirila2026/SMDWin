using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SMDWin.Services
{
    /// <summary>
    /// Everything a Benchmark run measured, kept for tuning the marks later: every raw value with its unit, the
    /// scale it was marked with, the details of each test (disk passes and warnings, GPU batch statistics, CPU sub-tests),
    /// the time of each step and a sensor timeline (one row per second: temperatures, clocks, load, power).
    /// Written to the app log and to Data\Benchmark\benchmark-*.txt (the last 10 runs); the self-test zip takes that folder.
    /// The page itself shows nothing from earlier runs.
    /// </summary>
    public sealed class BenchmarkRecord
    {
        public sealed record TestLine(string Component, string Test, double Value, string Unit, double? Mark, BenchmarkScore.Scale? Scale, string Extra);

        public DateTime Started { get; } = DateTime.Now;
        public string Pc { get; set; } = "";
        public List<TestLine> Tests { get; } = new();
        public Dictionary<string, double?> ComponentMarks { get; } = new();
        public List<string> Details { get; } = new();
        public List<(string Step, double Seconds)> Durations { get; } = new();
        public List<string> Timeline { get; } = new();
        public double? General { get; set; }
        public bool Cancelled { get; set; }
        public int CalcErrors { get; set; }
        public TimeSpan Took { get; set; }
        public string Notes { get; set; } = "";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public void Add(string component, string test, double value, string unit, double? mark, BenchmarkScore.Scale? scale, string extra = "")
            => Tests.Add(new TestLine(component, test, value, unit, mark, scale, extra));

        public const string TimelineHeader = "t_s,step,cpu_c,cpu_mhz,cpu_load,cpu_w,gpu_c,gpu_mhz,gpu_load,gpu_w";

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"SMD Win benchmark — {Started:yyyy-MM-dd HH:mm:ss} · build {AppVersionInfo.Build}");
            if (Pc.Length > 0) sb.AppendLine(Pc);
            string gen = General is double g ? $"{g.ToString("0.0", Inv)} ({BenchmarkScore.TierOf(g).Name})" : "—";
            sb.AppendLine($"General mark: {gen} · took {Took:m\\:ss} · cancelled: {(Cancelled ? "yes" : "no")} · calculation errors: {CalcErrors}");
            if (Notes.Length > 0) sb.AppendLine(Notes);
            sb.AppendLine("Scale: mark = 6 + log(value / reference) / log(step), clamped to 1–10 (latency: reference / value)");
            sb.AppendLine();
            foreach (var comp in Tests.Select(t => t.Component).Distinct())
            {
                string cm = ComponentMarks.TryGetValue(comp, out var m) && m is double mv ? mv.ToString("0.0", Inv) : "—";
                sb.AppendLine($"[{comp}] mark {cm}");
                foreach (var t in Tests.Where(x => x.Component == comp))
                {
                    string mark = t.Mark is double tm ? tm.ToString("0.0", Inv) : "—";
                    string scale = t.Scale == null ? "" : $"  (ref {t.Scale.Reference.ToString("0.###", Inv)}, step {t.Scale.Step.ToString("0.###", Inv)}{(t.Scale.LowerIsBetter ? ", lower is better" : "")})";
                    sb.AppendLine($"  {t.Test,-18} {t.Value.ToString("0.###", Inv),14} {t.Unit,-10} → {mark}{scale}");
                    if (t.Extra.Length > 0) sb.AppendLine($"  {"",-18} {t.Extra}");
                }
            }
            if (Details.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Details:");
                foreach (var d in Details) sb.AppendLine("  " + d);
            }
            if (Durations.Count > 0)
                sb.AppendLine().AppendLine("Step times: " + string.Join(" · ", Durations.Select(d => $"{d.Step} {d.Seconds.ToString("0.0", Inv)} s")));
            if (Timeline.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Sensor timeline (1 s):");
                sb.AppendLine(TimelineHeader);
                foreach (var row in Timeline) sb.AppendLine(row);
            }
            return sb.ToString();
        }

        /// <summary>Writes the record to Data\Benchmark (keeps the last 10 runs) and returns the file path, or null.</summary>
        public string? Save()
        {
            try
            {
                string dir = AppPaths.Data("Benchmark");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"benchmark-{Started:yyyyMMdd-HHmmss}.txt");
                File.WriteAllText(path, ToText(), new UTF8Encoding(false));
                foreach (var old in new DirectoryInfo(dir).GetFiles("benchmark-*.txt").OrderByDescending(f => f.Name).Skip(10))
                {
                    try { old.Delete(); } catch (Exception ex) { AppLogger.Debug($"Benchmark prune: {ex.Message}"); }
                }
                return path;
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "BenchmarkRecord.Save");
                return null;
            }
        }

        public static string Num(float? v, string fmt = "0") => v is float f && float.IsFinite(f) ? f.ToString(fmt, Inv) : "";
    }
}
