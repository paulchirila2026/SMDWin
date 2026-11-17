using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // Covers the summary counting: it must count ⚠/✘ from all sections of the report, not only the backend section. The tests
    // here check the pure counting function with invented report texts, without running the real self-test.
    public class SelfTestSignalCounterTests
    {
        [Fact]
        public void EmptyReport_ReturnsZeroZero()
        {
            var counts = SelfTestSignalCounter.Count("");
            Assert.Equal(0, counts.WarnCount);
            Assert.Equal(0, counts.FailCount);
        }

        [Fact]
        public void CountsWarningsAndFailuresAcrossMultipleSections()
        {
            // Simulates the real scenario: signals scattered across several "sections" of the report (page sweep + backend), not
            // only in one.
            string report =
                "── 1/7  PAGE SWEEP ──\n" +
                "  ⚠ Stress       —   402 ms — 0 rows/items\n" +
                "      ⚠ Zero-size visible elements on Stress: 1\n" +
                "          - StackPanel \"FanRowsContainer\" (623x0)\n" +
                "  ✔ Disk         —   433 ms — 9 rows/items\n" +
                "\n" +
                "── 5/7  LHM vs. APP SENSORS ──\n" +
                "  ✘ LHM reports ZERO temperature sensors but load sensors work fine\n" +
                "\n" +
                "── 7/7  BACKEND CHECKS ──\n" +
                "  ✘ CPU throttle sanity check threw an exception\n" +
                "  ⚠ Disk 0 not in hardware database\n";

            var counts = SelfTestSignalCounter.Count(report);

            // 3 lines with ⚠ that start with the symbol (Stress, Zero-size elements, HW db)
            Assert.Equal(3, counts.WarnCount);
            // 2 lines with ✘ (LHM, CPU throttle)
            Assert.Equal(2, counts.FailCount);
        }

        [Fact]
        public void DetailLines_WithoutLeadingSymbol_AreNotCounted()
        {
            // The detail lines ("- Element name") under a ⚠ do not start with the symbol themselves — they must not be counted
            // separately, otherwise a single warning with 5 listed elements would artificially become "5 warnings".
            string report =
                "  ⚠ Zero-size visible elements on Tools: 2\n" +
                "      - WrapPanel \"PsQuickButtonsPanel\" (1211x0)\n" +
                "      - StackPanel \"SnapshotListPanel\" (1211x0)\n";

            var counts = SelfTestSignalCounter.Count(report);

            Assert.Equal(1, counts.WarnCount);
            Assert.Equal(0, counts.FailCount);
        }

        [Fact]
        public void LeadingWhitespace_DoesNotPreventCounting()
        {
            string report = "        ⚠ indented warning\n            ✘ indented failure\n";
            var counts = SelfTestSignalCounter.Count(report);

            Assert.Equal(1, counts.WarnCount);
            Assert.Equal(1, counts.FailCount);
        }

        [Fact]
        public void OkLines_AreNeverCounted()
        {
            string report = "  ✔ Everything fine\n  ✔ Also fine\n";
            var counts = SelfTestSignalCounter.Count(report);

            Assert.Equal(0, counts.WarnCount);
            Assert.Equal(0, counts.FailCount);
        }
    }
}
