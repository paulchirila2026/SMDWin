using System;
using SMDWin.Services;

namespace SMDWin.Views
{
    /// <summary>
    /// The start-up window (splash), on a separate UI thread with its own Dispatcher: the animation stays smooth while the
    /// main thread builds the large window. Shown from App.OnStartup, before MainWindow, so the user sees something in the
    /// first seconds; MainWindow then takes it over (<see cref="Current"/>).
    /// </summary>
    internal static class SplashHost
    {
        internal static SplashWindow? Current { get; private set; }

        /// <summary>Shows the splash on its thread (only once). Returns false if the thread did not start.</summary>
        internal static bool ShowOnOwnThread()
        {
            if (Current != null) return true;
            try
            {
                using var shown = new System.Threading.ManualResetEventSlim(false);
                var t = new System.Threading.Thread(() =>
                {
                    try
                    {
                        var sp = new SplashWindow();
                        sp.Closed += (_, _) => sp.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background);
                        Current = sp;
                        sp.Show();
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "Splash on a separate thread"); }
                    finally { try { shown.Set(); } catch (ObjectDisposedException) { } }
                    System.Windows.Threading.Dispatcher.Run();
                })
                { IsBackground = true, Name = "SplashUI" };
                t.SetApartmentState(System.Threading.ApartmentState.STA);
                t.Start();
                // wait for it to appear (max 3 s), so the first reported step finds the splash subscribed
                shown.Wait(3000);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "SplashHost.ShowOnOwnThread"); }
            return Current != null;
        }
    }
}
