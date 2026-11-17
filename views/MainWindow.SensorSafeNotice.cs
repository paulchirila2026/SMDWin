using System;
using System.Threading.Tasks;
using SMDWin.Services;

namespace SMDWin.Views
{
    /// <summary>
    /// The start-up notices, one at a time: the one-time beta notice, then — only in safe sensor mode (SensorStartGuard) —
    /// the explanation that the sensors are off after a start that closed, with the choice to try them again.
    /// The PawnIO offer waits for both.
    /// </summary>
    public partial class MainWindow
    {
        private async Task ShowStartNoticesAsync()
        {
            await ShowBetaNoticeOnceAsync();
            await ShowSensorSafeNoticeAsync();
        }

        private async Task ShowSensorSafeNoticeAsync()
        {
            try
            {
                if (!SensorStartGuard.SafeMode) return;
                for (int i = 0; i < 120 && !IsVisible; i++) await Task.Delay(250);
                await Task.Delay(600);
                string text =
                    (SensorStartGuard.EnteredSafeModeThisStart
                        ? PT("SMD Win closed unexpectedly the last time it started, while it was loading the sensor libraries (temperatures, clocks, fans).")
                        : PT("The sensor libraries are turned off, because SMD Win once closed while loading them.")) + "\n\n" +
                    PT("To let the app open, they are turned off for now: temperatures and fan speeds may be missing, everything else works.") + "\n\n" +
                    PT("This usually comes from a security program that stops unsigned apps reading the hardware, or from a graphics or board driver. If the app closes again after trying, it turns them off again by itself.");
                int choice = AppDialog.Choose(text, PT("Sensors turned off"),
                    PT("Keep them off"), PT("Try them again at the next start"));
                if (choice == 0) SensorStartGuard.TryAgainNextStart();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ShowSensorSafeNoticeAsync"); }
        }
    }
}
