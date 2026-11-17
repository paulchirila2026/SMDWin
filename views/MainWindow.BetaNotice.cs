using System;
using System.Linq;
using System.Threading.Tasks;
using SMDWin.Services;

namespace SMDWin.Views
{
    /// <summary>
    /// The notice shown once, on the first start (before the public release): beta, no warranty, what SMD Win can
    /// change and how to undo it, the stress tests heat the hardware. After it is closed it is not shown again
    /// (Settings.BetaNoticeShown). The PawnIO offer waits for it, so there is one start-up dialog at a time.
    /// </summary>
    public partial class MainWindow
    {
        private Task? _betaNoticeTask;

        private async Task ShowBetaNoticeOnceAsync()
        {
            try
            {
                if (SettingsService.Current.BetaNoticeShown) return;
                for (int i = 0; i < 120 && !IsVisible; i++) await Task.Delay(250);
                for (int i = 0; i < 160 && System.Windows.Application.Current.Windows.OfType<SplashWindow>().Any(w => w.IsVisible); i++) await Task.Delay(250);
                await Task.Delay(600);
                string text =
                    PT("SMD Win is a beta version, free and open source, provided without any warranty.") + "\n\n" +
                    "• " + PT("It changes Windows settings only when a button for that change is pressed. Optimize, services, startup apps and update blocking keep the original values, so they can be reverted.") + "\n" +
                    "• " + PT("Auto-Clean deletes files permanently — the total is shown and confirmed first.") + "\n" +
                    "• " + PT("Stress tests and the benchmark run the processor and graphics card at full load: they heat up and the fans get loud. A test can be stopped at any time.") + "\n" +
                    "• " + PT("Logs and reports stay on this PC. They contain serial numbers and network details — check them before sharing them.");
                AppDialog.Show(text, PT("Welcome to SMD Win"), AppDialog.Kind.Info, this);
                SettingsService.Current.BetaNoticeShown = true;
                SettingsService.Save();
                AppLogger.Info("First start: beta notice shown");
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ShowBetaNoticeOnceAsync"); }
        }
    }
}
