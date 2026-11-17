using System;
using System.Collections.Generic;

namespace SMDWin.Services
{
    /// <summary>
    /// Splits a series of values into continuous runs. A NaN value means "no reading in that second" (the app was paused,
    /// the sensor did not answer): the chart does not join across it, it leaves a gap.
    /// </summary>
    public static class SeriesRuns
    {
        /// <summary>The [Start, End] intervals (inclusive) of consecutive finite values.</summary>
        public static List<(int Start, int End)> Finite(IReadOnlyList<double> values)
        {
            var runs = new List<(int Start, int End)>();
            int start = -1;
            for (int i = 0; i < values.Count; i++)
            {
                bool ok = double.IsFinite(values[i]);
                if (ok && start < 0) start = i;
                else if (!ok && start >= 0) { runs.Add((start, i - 1)); start = -1; }
            }
            if (start >= 0) runs.Add((start, values.Count - 1));
            return runs;
        }

        /// <summary>The ranges [Start, End] (inclusive) in which the value exceeds <paramref name="threshold"/> —
        /// for the markers under the chart (e.g. how long a test ran: 1 = yes, 0 = no).</summary>
        public static List<(int Start, int End)> Above(IReadOnlyList<double> values, double threshold)
        {
            var runs = new List<(int Start, int End)>();
            int start = -1;
            for (int i = 0; i < values.Count; i++)
            {
                bool on = double.IsFinite(values[i]) && values[i] > threshold;
                if (on && start < 0) start = i;
                else if (!on && start >= 0) { runs.Add((start, i - 1)); start = -1; }
            }
            if (start >= 0) runs.Add((start, values.Count - 1));
            return runs;
        }

        /// <summary>
        /// Reduces a one-point-per-second series to one point per <paramref name="bucketSeconds"/> seconds (the maximum of each
        /// interval, so short peaks stay visible on long windows). Intervals are aligned on the absolute second count
        /// (<paramref name="endIndex"/> = index of the last value), so a finished interval never changes its value on the next
        /// reading — the chart does not "shake". An interval with no finite value stays empty (NaN).
        /// </summary>
        public static List<double> Buckets(IReadOnlyList<double> perSecond, long endIndex, int bucketSeconds)
        {
            var result = new List<double>();
            if (perSecond.Count == 0) return result;
            if (bucketSeconds <= 1) { result.AddRange(perSecond); return result; }
            long first = endIndex - (perSecond.Count - 1);
            long bucket = long.MinValue;
            double max = double.NaN;
            for (int i = 0; i < perSecond.Count; i++)
            {
                long b = FloorDiv(first + i, bucketSeconds);
                if (b != bucket)
                {
                    if (bucket != long.MinValue) result.Add(max);
                    bucket = b; max = double.NaN;
                }
                double v = perSecond[i];
                if (double.IsFinite(v) && (double.IsNaN(max) || v > max)) max = v;
            }
            result.Add(max);
            return result;
        }

        /// <summary>Which <paramref name="bucketSeconds"/>-second interval contains second <paramref name="index"/>.</summary>
        public static long BucketOf(long index, int bucketSeconds) => bucketSeconds <= 1 ? index : FloorDiv(index, bucketSeconds);

        private static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        /// <summary>How many seconds one chart point covers for a <paramref name="windowSeconds"/> window, so the chart has no
        /// more than <paramref name="maxPoints"/> points.</summary>
        public static int BucketSecondsFor(int windowSeconds, int maxPoints = 360)
            => Math.Max(1, (int)Math.Ceiling(windowSeconds / (double)Math.Max(1, maxPoints)));

        /// <summary>Value for step <paramref name="step"/> of <paramref name="steps"/> when a reading covers several seconds:
        /// a straight line from the last value to the new one. Beyond <paramref name="maxInterpolated"/> steps (a long absence)
        /// the interval stays empty, with the real value only on the last step.</summary>
        public static float Fill(float prev, float cur, int step, int steps, int maxInterpolated)
        {
            if (steps <= 1 || step >= steps - 1) return cur;
            if (steps > maxInterpolated) return float.NaN;
            if (float.IsNaN(prev) || float.IsNaN(cur)) return cur;
            return prev + (cur - prev) * (step + 1) / steps;
        }
    }
}
