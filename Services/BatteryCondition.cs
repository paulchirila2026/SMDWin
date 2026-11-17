using System;

namespace SMDWin.Services
{
    /// <summary>What the battery is doing now, in one word (the Power page picks text and colour from it).</summary>
    public enum BatteryVerdict
    {
        Charging,       // in priza, se incarca
        Full,           // in priza, plina
        PluggedIdle,    // plugged in, not charging — normal when a charge limit is set
        NotCharging,    // plugged in, almost empty and receiving no power — faulty
        OnBattery,      // on battery
        OnBatteryLow,   // on battery, almost empty
    }

    /// <summary>
    /// Result of the evaluation. <paramref name="Severity"/>: 0 = fine, 1 = attention, 2 = problem.
    /// <paramref name="HealthTrusted"/> = the health percentage reported by the battery can be trusted.
    /// </summary>
    public readonly record struct BatteryAssessment(BatteryVerdict Verdict, int Severity, bool VoltageLow, bool HealthTrusted);

    /// <summary>
    /// Evaluation of the battery state from the Windows readings. The case it exists for: a battery showing 0 % while
    /// plugged in and not charging, whose internal counter still reports "health 100 %". The health figure comes from the
    /// battery's counter (last learned capacity), not from a measurement — for a battery that no longer receives power
    /// it means nothing, so it is not shown as a verdict.
    /// </summary>
    public static class BatteryCondition
    {
        /// <summary>Below this percentage, plugged in and not charging, the battery is considered "not charging".</summary>
        public const int StuckBelowPercent = 5;

        /// <summary>From this percentage up, plugged in and not charging, the battery is considered full.</summary>
        public const int FullFromPercent = 95;

        /// <summary>
        /// Abnormally low voltage: below three quarters of nominal. A lithium-ion cell is nominally ~3.7 V and is cut off
        /// by its protection around 3.0 V (≈ 0.8 of nominal); below 0.75 it is deeply discharged or faulty.
        /// Without a known nominal voltage nothing can be said.
        /// </summary>
        public static bool IsVoltageLow(double voltageV, double designVoltageV)
            => designVoltageV is > 3 and < 30 && voltageV > 0.5 && voltageV < designVoltageV * 0.75;

        /// <param name="confirmed">The same state was seen in at least two readings in a row (right after the charger is
        /// connected, an empty battery can show as "not charging" for a few seconds).</param>
        public static BatteryAssessment Assess(int percent, bool onAc, bool charging, bool full,
                                               double voltageV, double designVoltageV, bool confirmed)
        {
            bool voltageLow = IsVoltageLow(voltageV, designVoltageV);

            if (charging)
                return new BatteryAssessment(BatteryVerdict.Charging, 0, voltageLow, HealthTrusted: !voltageLow);

            if (!onAc)
            {
                if (percent <= 10)
                    return new BatteryAssessment(BatteryVerdict.OnBatteryLow, percent <= StuckBelowPercent ? 2 : 1, voltageLow, true);
                return new BatteryAssessment(BatteryVerdict.OnBattery, 0, voltageLow, true);
            }

            // plugged in, not charging
            if (percent <= StuckBelowPercent && !full)
            {
                // the second piece of evidence (voltage) stands in for a second reading
                if (confirmed || voltageLow)
                    return new BatteryAssessment(BatteryVerdict.NotCharging, 2, voltageLow, HealthTrusted: false);
                return new BatteryAssessment(BatteryVerdict.PluggedIdle, 0, voltageLow, HealthTrusted: true);
            }
            if (full || percent >= FullFromPercent)
                return new BatteryAssessment(BatteryVerdict.Full, 0, voltageLow, HealthTrusted: !voltageLow);
            return new BatteryAssessment(BatteryVerdict.PluggedIdle, 0, voltageLow, HealthTrusted: !voltageLow);
        }
    }
}
