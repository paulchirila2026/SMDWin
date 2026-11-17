using System;

namespace SMDWin.Services
{
    public static class SelfTestSignalCounter
    {
        public readonly struct Counts
        {
            public readonly int WarnCount;
            public readonly int FailCount;
            public Counts(int warnCount, int failCount) { WarnCount = warnCount; FailCount = failCount; }
        }

        public static Counts Count(string fullReportText)
        {
            if (string.IsNullOrEmpty(fullReportText)) return new Counts(0, 0);

            int warn = 0, fail = 0;
            foreach (var rawLine in fullReportText.Split('\n'))
            {
                string line = rawLine.TrimStart();
                if (line.StartsWith("⚠", StringComparison.Ordinal))
                {
                    if (line.Contains("not in hardware database", StringComparison.OrdinalIgnoreCase))
                        continue; // already represented separately, through HwCoveragePercent — not counted here again
                    warn++;
                }
                else if (line.StartsWith("✘", StringComparison.Ordinal)) fail++;
            }
            return new Counts(warn, fail);
        }
    }
}
