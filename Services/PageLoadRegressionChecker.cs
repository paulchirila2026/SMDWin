using System;
using System.Collections.Generic;
using System.Linq;

namespace SMDWin.Services
{
    public static class PageLoadRegressionChecker
    {
        public readonly struct RegressionResult
        {
            public readonly bool HasEnoughHistory;
            public readonly double BaselineMedianMs;
            public readonly bool IsRegression;

            public RegressionResult(bool hasEnoughHistory, double baselineMedianMs, bool isRegression)
            {
                HasEnoughHistory = hasEnoughHistory;
                BaselineMedianMs = baselineMedianMs;
                IsRegression = isRegression;
            }
        }

        /// <summary>Median of a list of values. The median (not the mean) is less sensitive to a single old outlier (e.g. an
        /// old run slowed down by an antivirus scanning in the background).</summary>
        public static double Median(IReadOnlyList<double> values)
        {
            if (values == null || values.Count == 0)
                throw new ArgumentException("values nu poate fi gol", nameof(values));

            var sorted = values.OrderBy(v => v).ToList();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 0
                ? (sorted[mid - 1] + sorted[mid]) / 2.0
                : sorted[mid];
        }

        /// <param name="pastElapsedMs">Times (ms) from earlier runs of the SAME page on the SAME machine — order does not matter.</param>
        /// <param name="currentMs">The time measured now.</param>
        /// <param name="minSamples">Below this number of earlier runs, the history is considered too short for a reliable baseline.</param>
        /// <param name="regressionMultiplier">How far above the baseline the current time must be to count as a regression (1.6 = 60% slower).</param>
        /// <param name="minAbsoluteMs">Absolute minimum threshold — avoids false positives on pages that are already very fast,
        /// where a few ms of variation would pass the multiplier but is only normal jitter (e.g. 5 ms -> 9 ms).</param>
        public static RegressionResult Check(
            IReadOnlyList<double> pastElapsedMs,
            double currentMs,
            int minSamples = 3,
            double regressionMultiplier = 1.6,
            double minAbsoluteMs = 800)
        {
            if (pastElapsedMs == null || pastElapsedMs.Count < minSamples)
                return new RegressionResult(hasEnoughHistory: false, baselineMedianMs: 0, isRegression: false);

            double baseline = Median(pastElapsedMs);
            bool isRegression = currentMs > baseline * regressionMultiplier && currentMs > minAbsoluteMs;
            return new RegressionResult(hasEnoughHistory: true, baselineMedianMs: baseline, isRegression: isRegression);
        }
    }
}
