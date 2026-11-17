using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    /// <summary>Evaluating the battery state: what the user is told and when the health figure cannot be trusted.</summary>
    public class BatteryConditionTests
    {
        [Fact]
        public void ZeroPercent_PluggedIn_LowVoltage_IsAFault_AtTheFirstReading()
        {
            // a real workshop case: 0 %, plugged in, not charging, 7.01 V on an 11.4 V battery, "health 100 %"
            var a = BatteryCondition.Assess(0, onAc: true, charging: false, full: false, 7.01, 11.4, confirmed: false);
            Assert.Equal(BatteryVerdict.NotCharging, a.Verdict);
            Assert.Equal(2, a.Severity);
            Assert.True(a.VoltageLow);
            Assert.False(a.HealthTrusted);
        }

        [Fact]
        public void ZeroPercent_PluggedIn_WithoutVoltage_NeedsASecondReading()
        {
            var first = BatteryCondition.Assess(0, true, false, false, 0, 0, confirmed: false);
            Assert.Equal(BatteryVerdict.PluggedIdle, first.Verdict);
            Assert.Equal(0, first.Severity);

            var second = BatteryCondition.Assess(0, true, false, false, 0, 0, confirmed: true);
            Assert.Equal(BatteryVerdict.NotCharging, second.Verdict);
            Assert.Equal(2, second.Severity);
            Assert.False(second.HealthTrusted);
        }

        [Fact]
        public void EmptyBattery_ThatIsCharging_IsNotAFault()
        {
            var a = BatteryCondition.Assess(0, true, charging: true, false, 9.4, 11.4, confirmed: true);
            Assert.Equal(BatteryVerdict.Charging, a.Verdict);
            Assert.Equal(0, a.Severity);
            Assert.True(a.HealthTrusted);
        }

        [Fact]
        public void PluggedIn_NotCharging_AtMidLevel_IsNormal()
        {
            // charge limit (e.g. stops at 80 %): plugged in, not charging — not a fault
            foreach (int p in new[] { 6, 40, 60, 80, 94 })
            {
                var a = BatteryCondition.Assess(p, true, false, false, 11.6, 11.4, confirmed: true);
                Assert.Equal(BatteryVerdict.PluggedIdle, a.Verdict);
                Assert.Equal(0, a.Severity);
                Assert.True(a.HealthTrusted);
            }
        }

        [Fact]
        public void PluggedIn_Full()
        {
            Assert.Equal(BatteryVerdict.Full, BatteryCondition.Assess(100, true, false, true, 12.9, 11.4, true).Verdict);
            Assert.Equal(BatteryVerdict.Full, BatteryCondition.Assess(96, true, false, false, 12.9, 11.4, true).Verdict);
            // the Windows "full" flag takes precedence over the percentage
            Assert.Equal(BatteryVerdict.Full, BatteryCondition.Assess(3, true, false, full: true, 12.9, 11.4, true).Verdict);
        }

        [Fact]
        public void OnBattery_LowLevels()
        {
            Assert.Equal(BatteryVerdict.OnBattery, BatteryCondition.Assess(55, false, false, false, 11.5, 11.4, true).Verdict);
            var low = BatteryCondition.Assess(9, false, false, false, 10.2, 11.4, true);
            Assert.Equal(BatteryVerdict.OnBatteryLow, low.Verdict);
            Assert.Equal(1, low.Severity);
            Assert.Equal(2, BatteryCondition.Assess(4, false, false, false, 9.6, 11.4, true).Severity);
            Assert.True(low.HealthTrusted);
        }

        [Fact]
        public void VoltageLow_NeedsAKnownNominalVoltage()
        {
            Assert.True(BatteryCondition.IsVoltageLow(7.01, 11.4));
            Assert.False(BatteryCondition.IsVoltageLow(9.2, 11.4));     // empty, but within limits (≈ 3.07 V per cell)
            Assert.False(BatteryCondition.IsVoltageLow(11.1, 11.4));
            Assert.False(BatteryCondition.IsVoltageLow(7.01, 0));       // nominal unknown
            Assert.False(BatteryCondition.IsVoltageLow(7.01, 400));     // nominal absurd
            Assert.False(BatteryCondition.IsVoltageLow(0, 11.4));       // tensiune necitita
        }

        [Fact]
        public void LowVoltage_MakesHealthUntrusted_EvenWhenCharging()
        {
            var a = BatteryCondition.Assess(1, true, charging: true, false, 6.8, 11.4, true);
            Assert.Equal(BatteryVerdict.Charging, a.Verdict);
            Assert.False(a.HealthTrusted);
        }
    }
}
