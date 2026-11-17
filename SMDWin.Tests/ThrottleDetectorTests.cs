using System;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    // The ThrottleDetector classification logic (Evaluate), without performance counters: frequency, temperature, load and
    // the power state are given directly.
    public class ThrottleDetectorTests
    {
        private const float BaseMHz = 3200f;
        private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        private static ThrottleSnapshot Eval(ThrottleDetector d, float freqMHz, float temp, float load,
            bool plugged = true, bool saver = false, bool battery = true, int atSecond = 0)
            => d.Evaluate(freqMHz, freqMHz / BaseMHz * 100f, temp, load, plugged, saver, battery, T0.AddSeconds(atSecond));

        [Fact]
        public void NoFrequencyReading_IsNotThrottling()
        {
            var d = new ThrottleDetector(BaseMHz);
            var s = d.Evaluate(0f, 0f, 60f, 100f, true, false, true, T0);
            Assert.Equal(ThrottleReason.None, s.Reason);
            Assert.False(s.IsThrottled);
            Assert.False(s.IsSevere);
            Assert.False(s.HasFrequency);
        }

        [Fact]
        public void NoFrequencyReading_ForTwentySeconds_NeverBecomesFixedFrequency()
        {
            var d = new ThrottleDetector(BaseMHz);
            ThrottleSnapshot s = null!;
            for (int t = 0; t <= 30; t++) s = d.Evaluate(0f, 0f, 60f, 100f, true, false, true, T0.AddSeconds(t));
            Assert.Equal(ThrottleReason.None, s.Reason);
        }

        [Fact]
        public void NoFrequencyReading_AtThermalLimit_IsStillThermal()
        {
            var d = new ThrottleDetector(BaseMHz);
            var s = d.Evaluate(0f, 0f, 100f, 100f, true, false, true, T0);
            Assert.Equal(ThrottleReason.Thermal, s.Reason);
        }

        [Fact]
        public void NoFrequencyReading_JustBelowLimit_IsNotThermal()
        {
            var d = new ThrottleDetector(BaseMHz);
            Assert.Equal(ThrottleReason.None, d.Evaluate(0f, 0f, 98f, 100f, true, false, true, T0).Reason);
        }

        [Fact]
        public void FullSpeed_UnderLoad_IsOk()
        {
            var d = new ThrottleDetector(BaseMHz);
            Assert.Equal(ThrottleReason.None, Eval(d, 3200f, 70f, 100f).Reason);
        }

        [Fact]
        public void SevereDrop_PluggedLaptop_CoolCpu_IsBdProchot()
        {
            var d = new ThrottleDetector(BaseMHz);
            var s = Eval(d, 400f, 55f, 90f, plugged: true, battery: true);
            Assert.Equal(ThrottleReason.BdProchot, s.Reason);
            Assert.True(s.IsSevere);
        }

        [Fact]
        public void SevereDrop_OnDesktop_IsNotBdProchot()
        {
            var d = new ThrottleDetector(BaseMHz);
            var s = Eval(d, 400f, 55f, 90f, plugged: true, battery: false);
            Assert.Equal(ThrottleReason.Unknown, s.Reason);
        }

        [Fact]
        public void LowFrequency_AtIdle_IsOk()
        {
            var d = new ThrottleDetector(BaseMHz);
            Assert.Equal(ThrottleReason.None, Eval(d, 400f, 40f, 5f).Reason);
            Assert.Equal(ThrottleReason.None, Eval(d, 1200f, 40f, 10f).Reason);
        }

        [Fact]
        public void Hot_ButFullFrequency_IsNotThermalThrottle()
        {
            var d = new ThrottleDetector(BaseMHz);   // TjMax implicit 100
            Assert.Equal(ThrottleReason.None, Eval(d, 3200f, 92f, 100f).Reason);
        }

        [Fact]
        public void Hot_WithFrequencyDrop_IsThermalThrottle()
        {
            var d = new ThrottleDetector(BaseMHz);
            Assert.Equal(ThrottleReason.Thermal, Eval(d, 2400f, 92f, 100f).Reason);   // 75% of max at 92°C
        }

        [Fact]
        public void NearThermalLimit_AtFullFrequency_IsNotThrottle()
        {
            // Ryzen under load: 88 °C with a 90 °C limit and the maximum frequency kept = normal operation
            var d = new ThrottleDetector(BaseMHz) { TjMaxC = 90f };
            Assert.Equal(ThrottleReason.None, Eval(d, 3200f, 88f, 100f).Reason);
        }

        [Fact]
        public void NearThermalLimit_WithSmallFrequencyDrop_IsThermal()
        {
            var d = new ThrottleDetector(BaseMHz) { TjMaxC = 90f };
            Assert.Equal(ThrottleReason.Thermal, Eval(d, 2800f, 88f, 100f).Reason);   // 87 % of max, 2 °C below the limit
        }

        [Fact]
        public void AtTjMax_IsThermal_EvenAtFullFrequency()
        {
            var d = new ThrottleDetector(BaseMHz) { TjMaxC = 90f };
            Assert.Equal(ThrottleReason.Thermal, Eval(d, 3200f, 90f, 100f).Reason);
        }

        [Fact]
        public void ThermalThresholds_FollowTjMax()
        {
            var amd = new ThrottleDetector(BaseMHz) { TjMaxC = 90f };
            Assert.Equal(ThrottleReason.Thermal, Eval(amd, 2600f, 84f, 100f).Reason);    // zona fierbinte + scadere clara
            var intel = new ThrottleDetector(BaseMHz) { TjMaxC = 105f };
            Assert.Equal(ThrottleReason.None, Eval(intel, 2600f, 84f, 100f).Reason);    // the same read, far from the limit
        }

        [Fact]
        public void TjMax_OutOfRange_IsIgnored()
        {
            var d = new ThrottleDetector(BaseMHz) { TjMaxC = 20f };
            Assert.Equal(100f, d.TjMaxC);
        }

        [Fact]
        public void HalfFrequency_UnderLoad_BecomesPowerLimit_OnlyAfterEightSeconds()
        {
            var d = new ThrottleDetector(BaseMHz);
            Assert.Equal(ThrottleReason.None, Eval(d, 1400f, 70f, 95f, atSecond: 0).Reason);
            Assert.Equal(ThrottleReason.None, Eval(d, 1450f, 70f, 95f, atSecond: 4).Reason);
            Assert.Equal(ThrottleReason.PowerLimit, Eval(d, 1400f, 70f, 95f, atSecond: 9).Reason);
        }

        [Fact]
        public void PowerLimitTimer_Resets_WhenFrequencyRecovers()
        {
            var d = new ThrottleDetector(BaseMHz);
            Eval(d, 1400f, 70f, 95f, atSecond: 0);
            Eval(d, 3200f, 70f, 95f, atSecond: 5);    // back to the normal clock
            Assert.Equal(ThrottleReason.None, Eval(d, 1400f, 70f, 95f, atSecond: 10).Reason);
        }

        [Fact]
        public void BatterySaver_WithLowFrequency_UnderLoad_IsBatterySaver()
        {
            var d = new ThrottleDetector(BaseMHz);
            Assert.Equal(ThrottleReason.BatterySaver, Eval(d, 1200f, 60f, 90f, plugged: false, saver: true).Reason);
        }

        [Fact]
        public void BatterySaver_WithNormalFrequency_IsOk()
        {
            var d = new ThrottleDetector(BaseMHz);
            Assert.Equal(ThrottleReason.None, Eval(d, 3000f, 60f, 90f, plugged: false, saver: true).Reason);
        }

        [Fact]
        public void FixedLowFrequency_UnderLoad_ForTwentySeconds_IsFixedFrequency()
        {
            var d = new ThrottleDetector(BaseMHz);
            ThrottleSnapshot s = null!;
            for (int t = 0; t <= 22; t++) s = Eval(d, 2000f, 70f, 100f, atSecond: t);   // 62% of the maximum, no variation
            Assert.Equal(ThrottleReason.FixedFrequency, s.Reason);
        }
    }
}
