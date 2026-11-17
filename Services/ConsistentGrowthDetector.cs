namespace SMDWin.Services
{
    public static class ConsistentGrowthDetector
    {
        /// <param name="oldest">Value from the oldest run (of the 3).</param>
        /// <param name="middle">Value from the middle run.</param>
        /// <param name="newest">Current value (most recent).</param>
        /// <param name="minAbsoluteGrowth">Total growth (newest - oldest) must exceed this threshold to count — avoids false
        /// positives on small measurement noise that happens to increase monotonically.</param>
        public static bool IsConsistentGrowth(double oldest, double middle, double newest, double minAbsoluteGrowth)
        {
            bool monotonic = oldest < middle && middle < newest;
            bool aboveThreshold = (newest - oldest) > minAbsoluteGrowth;
            return monotonic && aboveThreshold;
        }
    }
}
