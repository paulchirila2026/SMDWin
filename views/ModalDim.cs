using System;
using System.Windows;
using System.Windows.Interop;

namespace SMDWin.Views
{
    /// <summary>
    /// Dims the owner window while a modal window (opened with ShowDialog) is in front of it, so what is in front stands out
    /// the same way in the Light and Dark themes. AppDialog dims by itself; every other modal window is caught here, through
    /// a class handler on Window.Loaded — no change is needed in the 20-odd places that open one.
    /// </summary>
    internal static class ModalDim
    {
        private static bool _registered;

        public static void Register()
        {
            if (_registered) return;
            _registered = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
        }

        private static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not Window w || ReferenceEquals(e.OriginalSource, w) == false) return;
                if (w is AppDialog) return;                               // dims by itself
                if (w.Owner == null || !ComponentDispatcher.IsThreadModal) return;   // only windows opened with ShowDialog
                var undo = AppDialog.DimOwner(w.Owner);
                if (undo == null) return;
                w.Closed += (_, _) => undo();
            }
            catch (Exception ex) { SMDWin.Services.AppLogger.Debug($"ModalDim: {ex.Message}"); }
        }
    }
}
