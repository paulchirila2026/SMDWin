using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Application = System.Windows.Application;
using Window = System.Windows.Window;

namespace SMDWin.Views
{
    public partial class NotificationCenterWindow : Window
    {
        private static NotificationCenterWindow? _openInstance;

        // When it was last closed because of a click outside it. A click on the bell with the window open closes it (it loses
        // focus) and only then reaches the bell; without this marker, the same click would reopen it.
        private static DateTime _closedByClickOutsideUtc = DateTime.MinValue;
        private const double ReopenGuardMs = 500;
        private bool _closing;

        public NotificationCenterWindow()
        {
            InitializeComponent();
            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
            // Closes on a click outside it (like any notification panel), not only with Esc / ✕
            Closing += (_, _) => _closing = true;
            Deactivated += (_, _) => CloseBecauseOfClickOutside();

            Title = TxtNotifTitle.Text = SMDWin.Services.LanguageService.S("NotificationCenter", "Title");
            BtnClearAll.Content = SMDWin.Services.LanguageService.S("NotificationCenter", "ClearAll");
            TxtEmptyState.Text = SMDWin.Services.LanguageService.S("NotificationCenter", "Empty");

            // Copy the live theme resources (the same pattern as AppDialog) — the window must look identical to the current theme
            // (Dark/Light/Auto + accent).
            if (Application.Current?.Resources != null)
            {
                foreach (var key in Application.Current.Resources.Keys)
                {
                    try { Resources[key] = Application.Current.Resources[key]; } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                }
                foreach (var rd in Application.Current.Resources.MergedDictionaries)
                    if (!Resources.MergedDictionaries.Contains(rd))
                        Resources.MergedDictionaries.Add(rd);
            }

            ListEntries.ItemsSource = SMDWin.Services.NotificationCenter.Entries;
            // grouped by days (Today / Older); new entries are inserted at the front, so "Today" stays first
            try
            {
                var view = System.Windows.Data.CollectionViewSource.GetDefaultView(SMDWin.Services.NotificationCenter.Entries);
                if (view != null && view.GroupDescriptions.Count == 0)
                    view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("DayGroup"));
            }
            catch (Exception ex) { SMDWin.Services.AppLogger.Debug($"NotificationCenterWindow grouping: {ex.Message}"); }
            SMDWin.Services.NotificationCenter.Entries.CollectionChanged += (_, _) => RefreshEmptyState();
            RefreshEmptyState();

            Closed += (_, _) => { if (_openInstance == this) _openInstance = null; };
        }

        private void CloseBecauseOfClickOutside()
        {
            if (_closing) return;
            _closedByClickOutsideUtc = DateTime.UtcNow;
            // Closing happens after the current event has finished: Close() called right from Deactivated throws if the window is
            // already closing (e.g. closed with ✕ at the same moment).
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closing) return;
                try { Close(); }
                catch (InvalidOperationException ex) { SMDWin.Services.AppLogger.Debug($"NotificationCenterWindow close: {ex.Message}"); }
            }));
        }

        private void RefreshEmptyState()
        {
            TxtEmptyState.Visibility = SMDWin.Services.NotificationCenter.Entries.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnClearAll_Click(object sender, RoutedEventArgs e)
            => SMDWin.Services.NotificationCenter.ClearAll();

        private void BtnClearOne_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button b && b.Tag is Guid id)
                SMDWin.Services.NotificationCenter.Remove(id);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>Opens the popup anchored under an element (the bell button in the title bar).
        /// A second call (while it is already open) closes it.</summary>
        public static void ShowNear(FrameworkElement anchor, Window owner)
        {
            // the click that just closed the window (a click outside = on the bell) does not reopen it
            if ((DateTime.UtcNow - _closedByClickOutsideUtc).TotalMilliseconds < ReopenGuardMs)
            {
                _closedByClickOutsideUtc = DateTime.MinValue;
                return;
            }
            if (_openInstance != null)
            {
                try { _openInstance.Close(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                _openInstance = null;
                return;
            }

            try
            {
                var win = new NotificationCenterWindow { Owner = owner };
                _openInstance = win;
                SMDWin.Services.NotificationCenter.MarkAllRead();

                double left = owner.Left + owner.ActualWidth - win.Width - 16;
                double top  = owner.Top + 46;

                var workArea = System.Windows.SystemParameters.WorkArea;
                if (left + win.Width  > workArea.Right)  left = workArea.Right  - win.Width  - 8;
                if (left < workArea.Left)                left = workArea.Left  + 8;
                if (top + win.Height  > workArea.Bottom) top  = workArea.Bottom - win.Height - 8;
                if (top < workArea.Top)                  top  = workArea.Top   + 8;

                win.Left = left;
                win.Top  = top;

                SMDWin.Services.AppLogger.Debug($"NotificationCenterWindow.ShowNear: Left={left:F0} Top={top:F0} " +
                    $"ownerLeft={owner.Left:F0} ownerTop={owner.Top:F0} ownerW={owner.ActualWidth:F0} " +
                    $"workArea={workArea.Left:F0},{workArea.Top:F0},{workArea.Right:F0},{workArea.Bottom:F0}");

                win.Show();
            }
            catch (Exception ex)
            {
                _openInstance = null;
                SMDWin.Services.AppLogger.Warning(ex, "NotificationCenterWindow.ShowNear");
            }
        }

        /// <summary>A short summary by severity for the bell tooltip (e.g. "4 Warning · 1 Critical") — called from
        /// UpdateNotifBadge in MainWindow.cs.</summary>
        public static string BuildTooltipSummary()
        {
            var entries = SMDWin.Services.NotificationCenter.Entries;
            if (entries.Count == 0) return "No notifications";

            var byLevel = entries
                .GroupBy(en => en.Severity)
                .OrderByDescending(g => g.Key) // Critical before Warning before Info
                .Select(g => $"{g.Count()} {g.Key}");
            return string.Join(" · ", byLevel);
        }
    }
}
