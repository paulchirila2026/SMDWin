using System;

namespace SMDWin.Services
{
    public static class AppReadinessScoreCalculator
    {
        public readonly struct ScoreResult
        {
            public readonly int Score;              // 0-100
            public readonly double StabilityPercent; // 0-100, from warn/fail
            public readonly double? HwCoveragePercent; // 0-100, null if no hardware was checked
            public readonly string Rating;

            public ScoreResult(int score, double stabilityPercent, double? hwCoveragePercent, string rating)
            {
                Score = score;
                StabilityPercent = stabilityPercent;
                HwCoveragePercent = hwCoveragePercent;
                Rating = rating;
            }
        }

        /// <param name="warnCount">Total warnings in the whole report (SelfTestSignalCounter).</param>
        /// <param name="failCount">Total errors in the whole report.</param>
        /// <param name="hwDbKnownCount">How many checked hardware parts are in the local database.</param>
        /// <param name="hwDbTotalCount">How many hardware parts were checked in total (0 = no check possible on this machine).</param>
        public static ScoreResult Calculate(int warnCount, int failCount, int hwDbKnownCount, int hwDbTotalCount)
        {
            if (warnCount < 0) throw new ArgumentOutOfRangeException(nameof(warnCount));
            if (failCount < 0) throw new ArgumentOutOfRangeException(nameof(failCount));
            if (hwDbKnownCount < 0) throw new ArgumentOutOfRangeException(nameof(hwDbKnownCount));
            if (hwDbTotalCount < 0) throw new ArgumentOutOfRangeException(nameof(hwDbTotalCount));
            if (hwDbKnownCount > hwDbTotalCount) throw new ArgumentException("hwDbKnownCount cannot be greater than hwDbTotalCount");

            // Each ✘ costs more than a ⚠ — a real error counts 3x as much as a warning.
            // Clamped at 0, so many problems never make the score negative.
            double stabilityPercent = Math.Max(0, 100 - failCount * 15 - warnCount * 5);

            double? hwCoveragePercent = hwDbTotalCount > 0
                ? 100.0 * hwDbKnownCount / hwDbTotalCount
                : null;

            // the score is the app's stability only. The hardware database coverage (how many of this PC's parts
            // are in Data/HardwareDB) says nothing about the app working well — it pulled a clean run down to 43/100 on a
            // laptop whose parts were not in the database. It is still returned, and shown next to the score.
            double overall = stabilityPercent;

            int score = (int)Math.Round(Math.Clamp(overall, 0, 100));
            string rating = score >= 90 ? "Excellent" : score >= 75 ? "Good" : score >= 50 ? "Acceptable" : "Needs attention";

            return new ScoreResult(score, stabilityPercent, hwCoveragePercent, rating);
        }
    }
}
