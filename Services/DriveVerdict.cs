using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SMDWin.Models;

namespace SMDWin.Services
{
    /// <summary>
    /// The one health verdict of a disk, used by every screen and report (Storage, SMART, Dashboard, System Info, Surface Scan,
    /// Health check, notifications, exports), so the same disk never shows 93 % in one place and 100 % in another.
    ///
    /// The figure: the lower of the SMART score (HardwareService.ComputeSmartHealthScores) and the life left that an SSD reports
    /// (wear attribute, or 100 − NVMe "Percentage Used"). It is labelled "life left" when the wear decides it, "health" otherwise.
    ///
    /// The state: Failing = a critical counter (bad / pending / unreadable sectors, data the drive could not recover, an NVMe
    /// critical warning, spare blocks at the drive's own limit), a score under 60 % or 10 % life or less left. Needs attention =
    /// a score under 90 %, 30 % life or less, or another value out of range. Wear alone (an SSD at 75 %) is not a problem.
    /// The temperature right now and the time the drive once spent hot are not part of it (they have their own place and
    /// were the reason a healthy NVMe showed "100 % — Needs attention").
    /// </summary>
    public sealed class DriveVerdict
    {
        public enum State { Unknown, Good, Watch, Failing }

        public bool HasData { get; init; }
        /// <summary>The figure shown everywhere (0 when there is no data).</summary>
        public int Percent { get; init; }
        /// <summary>true when the figure is the life left an SSD reports (the dial says "life left"), false for the SMART score.</summary>
        public bool IsLifeLeft { get; init; }
        public int? LifeLeft { get; init; }
        public int Score { get; init; }
        public State Level { get; init; }
        /// <summary>The values that set the state (red first).</summary>
        public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
        /// <summary>Values out of the green that do not change the state (temperature history, light thermal throttling).</summary>
        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

        /// <summary>The state in English (the key of the language files): Healthy / Needs attention / Failing / No health data.</summary>
        public string Word => Level switch
        {
            State.Good => "Healthy", State.Watch => "Needs attention", State.Failing => "Failing", _ => "No health data",
        };
        /// <summary>The theme brush of the state.</summary>
        public string BrushKey => Level switch
        {
            State.Good => "StatusOkBrush", State.Watch => "StatusWarnBrush", State.Failing => "StatusCritBrush", _ => "TextSecondaryBrush",
        };
        /// <summary>The drawing's state (LED / label colour).</summary>
        public DriveHealthState DrawingState => Level switch
        {
            State.Good => DriveHealthState.Good, State.Watch => DriveHealthState.Watch, State.Failing => DriveHealthState.Failing, _ => DriveHealthState.Unknown,
        };
        /// <summary>"93 %" or "—".</summary>
        public string PercentText => HasData && Percent > 0 ? Percent.ToString(CultureInfo.InvariantCulture) + "%" : "—";

        private const string Green = "#22C55E", Red = "#EF4444";

        // NVMe rows that describe the heat (now or in the past), not the health of the flash
        private static readonly string[] HeatRows = { "Composite Temperature", "Warning Composite Temperature Time", "Critical Composite Temperature Time", "Thermal Throttling" };
        // NVMe rows whose red means the drive itself is failing
        private static readonly string[] CriticalNvmeRows = { "Critical Warning", "Available Spare", "Media and Data Integrity Errors" };

        public static DriveVerdict Of(DiskHealthEntry? d)
        {
            if (d == null) return new DriveVerdict();
            bool hasData = d.SmartAttributes.Count > 0 || d.NvmeHealthRows.Count > 0;
            if (!hasData) return new DriveVerdict { HasData = false, Level = State.Unknown };

            int score = d.HealthPercent > 0 ? Math.Clamp(d.HealthPercent, 0, 100) : 100;
            int? life = LifeLeftOf(d);
            int percent = life.HasValue ? Math.Min(score, life.Value) : score;
            bool isLife = life.HasValue && life.Value <= score;

            var red = new List<string>();
            var amber = new List<string>();
            var notes = new List<string>();
            foreach (var a in d.SmartAttributes)
            {
                string c = a.TrafficLightColor;
                if (c == Green) continue;
                if (a.Id is 0xC2 or 0xBE) { notes.Add(a.Name); continue; }   // the temperature now
                (c == Red && (a.Id is 0x05 or 0xC5 or 0xC6 or 0xBB || a.IsRuntimeBadBlock || (a.ThresholdValue > 0 && a.CurrentValue <= a.ThresholdValue)) ? red : amber).Add(a.Name);
            }
            foreach (var r in d.NvmeHealthRows)
            {
                string c = r.TrafficLightColor;
                if (c == Green) continue;
                string name = r.Name ?? "";
                if (HeatRows.Any(h => name.Equals(h, StringComparison.OrdinalIgnoreCase))) { notes.Add(name); continue; }
                if (name.Equals("Percentage Used", StringComparison.OrdinalIgnoreCase)) continue;   // wear: judged from the life left below
                (c == Red && CriticalNvmeRows.Any(h => name.Equals(h, StringComparison.OrdinalIgnoreCase)) ? red : amber).Add(name);
            }

            State level;
            if (red.Count > 0 || score < 60 || life is <= 10) level = State.Failing;
            else if (amber.Count > 0 || score < 90 || life is <= 30) level = State.Watch;
            else level = State.Good;

            return new DriveVerdict
            {
                HasData = true, Percent = percent, IsLifeLeft = isLife, LifeLeft = life, Score = score, Level = level,
                Problems = red.Concat(amber).Distinct().ToList(), Notes = notes.Distinct().ToList(),
            };
        }

        /// <summary>The life left an SSD reports: the normalized wear attribute (100 → 0), or 100 − NVMe "Percentage Used". HDD: none.</summary>
        public static int? LifeLeftOf(DiskHealthEntry d)
        {
            bool hdd = d.RotationRpm > 0 || (d.Kind ?? "").Contains("HDD", StringComparison.OrdinalIgnoreCase);
            if (hdd) return null;
            var wear = d.SmartAttributes.FirstOrDefault(x => x.Id == 0xE7) ?? d.SmartAttributes.FirstOrDefault(x => x.Id == 0xE9)
                    ?? d.SmartAttributes.FirstOrDefault(x => x.Id == 0xB1) ?? d.SmartAttributes.FirstOrDefault(x => x.Id == 0xAD);
            // 0xE9 / 0xA9 mean "life" only for some makers: the attribute name must say so (as in the SMART table)
            if (wear != null && wear.CurrentValue is > 0 and <= 100 && (wear.Id is 0xB1 or 0xAD || wear.NameSaysLife)) return wear.CurrentValue;
            var used = d.NvmeHealthRows.FirstOrDefault(r => string.Equals(r.Name, "Percentage Used", StringComparison.OrdinalIgnoreCase));
            if (used != null && FirstInt(used.RawDisplay) is int u && u is >= 0 and <= 100) return 100 - u;
            return null;
        }

        private static int? FirstInt(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var digits = new string(s.SkipWhile(ch => !char.IsDigit(ch)).TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : null;
        }
    }
}
