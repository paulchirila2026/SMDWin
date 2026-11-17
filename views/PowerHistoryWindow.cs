using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SMDWin.Services;

using Application         = System.Windows.Application;
using Brush               = System.Windows.Media.Brush;
using Brushes             = System.Windows.Media.Brushes;
using Button              = System.Windows.Controls.Button;
using Canvas              = System.Windows.Controls.Canvas;
using FontFamily          = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox          = System.Windows.MessageBox;
using Orientation         = System.Windows.Controls.Orientation;
using Rectangle           = System.Windows.Shapes.Rectangle;
using ToolTip             = System.Windows.Controls.ToolTip;
using MouseEventArgs      = System.Windows.Input.MouseEventArgs;
using Cursors             = System.Windows.Input.Cursors;
using VerticalAlignment   = System.Windows.VerticalAlignment;
using WpfColor            = System.Windows.Media.Color;
using WpfPoint            = System.Windows.Point;

namespace SMDWin.Views
{
    public partial class PowerHistoryWindow : Window
    {
        private static readonly CultureInfo _uiCulture =
            LanguageService.UiCulture;

        // ── Models ────────────────────────────────────────────────────────────
        private enum EventKind { Boot, Shutdown, UnexpectedShutdown, Crash, Logon, Logoff, Sleep, Resume }

        private class PowerEvent
        {
            public DateTime  Time      { get; set; }
            public EventKind Kind      { get; set; }
            // For warnings/crashes from external sources (Category != null = a warning row)
            public string?   Category  { get; set; } = null;
            public string?   WarnIcon  { get; set; } = null;
            public string?   WarnColor { get; set; } = null;
            public bool      IsWarning => Category != null;

            public string KindIcon  => WarnIcon ?? Kind switch {
                EventKind.Boot               => "⚡",
                EventKind.Shutdown           => "🔵",
                EventKind.UnexpectedShutdown => "🟠",
                EventKind.Crash              => "🔴",
                EventKind.Logon              => "→",
                EventKind.Logoff             => "←",
                EventKind.Sleep              => "💤",
                EventKind.Resume             => "☀",
                _                            => "•"
            };
            public string KindLabel => Category != null ? Category : Kind switch {
                EventKind.Boot               => "Boot",
                EventKind.Shutdown           => "Shutdown",
                EventKind.UnexpectedShutdown => "Unexpected shutdown",
                EventKind.Crash              => "Crash / Power loss",
                EventKind.Logon              => "User logon",
                EventKind.Logoff             => "User logoff",
                EventKind.Sleep              => "Sleep / Standby",
                EventKind.Resume             => "Resume from sleep",
                _                            => "Unknown"
            };
            public string Duration { get; set; } = "—";
            public string Notes    { get; set; } = "";
            public bool   IsBad    => Category == null && Kind is EventKind.Crash or EventKind.UnexpectedShutdown;
            public bool   IsSleep  => Category == null && Kind == EventKind.Sleep;
            public bool   IsResume => Category == null && Kind == EventKind.Resume;
            public bool   IsLogon  => Category == null && Kind == EventKind.Logon;
            public bool   IsLogoff => Category == null && Kind == EventKind.Logoff;
            public string KindColor => WarnColor ?? (LightColors ? Kind switch {
                // Light: the same hues one or two steps darker — the bright ones (green "Boot", yellow) fade into white
                EventKind.Boot               => "#15803D",
                EventKind.Shutdown           => "#2563EB",
                EventKind.UnexpectedShutdown => "#C2410C",
                EventKind.Crash              => "#DC2626",
                EventKind.Logon              => "#4F46E5",
                EventKind.Logoff             => "#475569",
                EventKind.Sleep              => "#B45309",
                EventKind.Resume             => "#A16207",
                _                            => "#555555"
            } : Kind switch {
                EventKind.Boot               => "#22C55E",
                EventKind.Shutdown           => "#60A5FA",
                EventKind.UnexpectedShutdown => "#F97316",
                EventKind.Crash              => "#EF4444",
                EventKind.Logon              => "#818CF8",
                EventKind.Logoff             => "#94A3B8",
                EventKind.Sleep              => "#F59E0B",
                EventKind.Resume             => "#FCD34D",
                _                            => "#888888"
            });
            /// <summary>Set by the window from the current theme.</summary>
            public static bool LightColors;
        }

        private class DaySummary
        {
            public DateTime Date         { get; set; }
            public List<(double StartFrac, double EndFrac, bool IsBad, bool IsSleep, bool IsUnknown)> Segments { get; set; } = new();
            public double UptimeHours    { get; set; }
            public int    Crashes        { get; set; }
            public bool   HasActivity    => Segments.Any();
        }

        // ── State ─────────────────────────────────────────────────────────────
        private int _days = 30;
        private List<PowerEvent>  _events   = new();
        private List<DaySummary>  _daysSum  = new();
        private DateTime?         _filterDay = null;   // null = all days
        private bool              _renderDayInProgress = false;
        // CALENDAR: the timeline view mode — "week" (hourly bars) or "month" (calendar heatmap)
        private string            _viewMode = "month";
        private DateTime          _calMonth  = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        private DateTime          _weekStart = StartOfWeek(DateTime.Today);
        private string            _kindFilter = "All"; // "All" / "Power" / "Warnings" / "Crashes" / "Logon"

        // ── Controls ──────────────────────────────────────────────────────────
        private Button     _btnMonth    = null!;
        private Button     _btnWeek     = null!;
        private Button     _btnDay      = null!;   // DAY-VIEW: active when the user clicks a day
        private Button     _btnRefresh  = null!;
        private Button     _btnExport   = null!;
        private TextBlock  _txtStatus   = null!;
        private TextBlock  _txtStats    = null!;
        private Border     _crashBadge  = null!;
        private TextBlock  _txtCrash    = null!;
        private StackPanel _timelinePanel = null!;
        private DataGrid   _grid        = null!;
        private Border     _gridBorder  = null!;
        private ToolTip    _hoverTip    = null!;
        private TextBlock  _filterLabel = null!;
        private StackPanel _filterChips = null!; // chips All/Power/Warnings/Crashes/Logon

        // ── Constructor ───────────────────────────────────────────────────────
        /// <summary>Light theme: darker event colours and visible hour lines (the Dark ones are white at low opacity).</summary>
        private static bool IsLightTheme
        {
            get
            {
                string n = SettingsService.Current.ThemeName == "Auto" ? ThemeManager.ResolveAuto() : SettingsService.Current.ThemeName;
                return ThemeManager.IsLight(ThemeManager.Normalize(n));
            }
        }
        private static void UpdateLightFlag() => PowerEvent.LightColors = IsLightTheme;
        /// <summary>Hour lines / "now" line: white on Dark, slate on Light.</summary>
        private static WpfColor GuideColor(byte alphaDark) =>
            IsLightTheme ? WpfColor.FromArgb((byte)Math.Min(255, alphaDark * 2 + 30), 71, 85, 105) : WpfColor.FromArgb(alphaDark, 255, 255, 255);

        public PowerHistoryWindow()
        {
            UpdateLightFlag();
            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
            Title                 = "Power & Session History — SMDWin";
            Width                 = 960;
            Height                = 780;
            MinWidth              = 720;
            MinHeight             = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle           = WindowStyle.None;
            ResizeMode            = ResizeMode.CanResize;
            ShowInTaskbar         = true;
            AllowsTransparency    = true;
            Background            = Brushes.Transparent;
            Foreground            = Res("TextPrimaryBrush");
            if (Application.Current.Resources.Contains("AppFont"))
                FontFamily = (FontFamily)Application.Current.Resources["AppFont"];

            var chrome = new System.Windows.Shell.WindowChrome
            {
                CaptionHeight         = 0,
                ResizeBorderThickness = new Thickness(6),
                CornerRadius          = new CornerRadius(0),
                GlassFrameThickness   = new Thickness(0),
                UseAeroCaptionButtons = false
            };
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, chrome);

            // The content wrapped in the themed shell with rounded corners (like the Net windows)
            Content = SMDWin.Views.Net.NetWindowHelper.WindowShell(BuildUI());
            Loaded += async (_, _) =>
            {
                ApplyTitleBar();
                void OnThemeChanged()
                {
                    try
                    {
                        ApplyTitleBar();
                        Foreground = Res("TextPrimaryBrush");
                        UpdateLightFlag();
                    }
                    catch (Exception ex) { AppLogger.Debug(ex.Message); }
                }
                ThemeManager.ThemeChanged += OnThemeChanged;
                Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;

                await LoadAsync();
            };
        }

        // ── Day filter ────────────────────────────────────────────────────────
        private void ApplyDayFilter()
        {
            List<PowerEvent> filtered;
            string statusText;

            if (_viewMode == "month" && !_filterDay.HasValue)
            {
                filtered = _events.Where(e =>
                    e.Time.Year == _calMonth.Year && e.Time.Month == _calMonth.Month).ToList();
                statusText = $"Showing {{0}} events for {_calMonth:MMMM yyyy}.";
            }
            else if (_viewMode == "week" && !_filterDay.HasValue)
            {
                var weekEnd = _weekStart.AddDays(7);
                filtered = _events.Where(e => e.Time.Date >= _weekStart && e.Time.Date < weekEnd).ToList();
                statusText = $"Showing {{0}} events for week {_weekStart:dd MMM} – {_weekStart.AddDays(6):dd MMM}.";
            }
            else if (_filterDay.HasValue)
            {
                filtered = _events.Where(e => e.Time.Date == _filterDay.Value).ToList();
                statusText = $"Showing {{0}} events for {_filterDay.Value:ddd dd MMM yyyy}.";
            }
            else
            {
                filtered = _events;
                statusText = $"{{0}} events loaded from the last {_days} days.";
            }

            // Applies the event type filter
            filtered = _kindFilter switch
            {
                "Power"    => filtered.Where(e => !e.IsWarning &&
                                  e.Kind is EventKind.Boot or EventKind.Shutdown or
                                            EventKind.UnexpectedShutdown or EventKind.Sleep or
                                            EventKind.Resume).ToList(),
                "Warnings" => filtered.Where(e => e.IsWarning).ToList(),
                "Crashes"  => filtered.Where(e => !e.IsWarning && e.IsBad).ToList(),
                "Logon"    => filtered.Where(e => !e.IsWarning &&
                                  e.Kind is EventKind.Logon or EventKind.Logoff).ToList(),
                _          => filtered // "All"
            };

            _grid.ItemsSource = new ObservableCollection<PowerEvent>(
                filtered.OrderByDescending(e => e.Time));
            _txtStatus.Text = string.Format(statusText, filtered.Count);
        }

        // ── Export CSV ────────────────────────────────────────────────────────
        private void ExportCsv_Click(object s, RoutedEventArgs e)
        {
            if (!_events.Any()) { MessageBox.Show("No data to export.", "SMD Win"); return; }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"PowerHistory_{DateTime.Now:yyyyMMdd_HHmm}.csv",
                Filter   = "CSV files|*.csv",
                InitialDirectory = SMDWin.Services.SettingsService.GetEffectiveReportFolder(),
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var lines = new List<string> { "Date/Time,Event,Duration,Notes" };
                lines.AddRange(_events.OrderByDescending(e2 => e2.Time).Select(e2 =>
                    $"\"{e2.Time:yyyy-MM-dd HH:mm:ss}\",\"{e2.KindLabel}\",\"{e2.Duration}\",\"{e2.Notes}\""));
                File.WriteAllLines(dlg.FileName, lines);
                MessageBox.Show($"Exported {_events.Count} events.", "SMD Win");
            }
            catch (Exception ex) { MessageBox.Show($"Export failed: {ex.Message}", "SMD Win"); }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

        private void OpenEventDetail(PowerEvent ev)
        {
            var allData = _events.Select(e => new EventDetailWindow.EventData(
                e.Time, e.KindLabel, e.KindIcon, e.KindColor, e.Duration, e.Notes, e.IsWarning)).ToList();
            var evData = new EventDetailWindow.EventData(
                ev.Time, ev.KindLabel, ev.KindIcon, ev.KindColor, ev.Duration, ev.Notes, ev.IsWarning);
            new EventDetailWindow(evData, allData) { Owner = this }.ShowDialog();
        }

        // Returns the Monday of the week the given date belongs to (Monday = the first day)
        private static DateTime StartOfWeek(DateTime dt)
        {
            int diff = ((int)dt.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            return dt.AddDays(-diff).Date;
        }

        private void ApplyTitleBar()
        {
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                string res = ThemeManager.Normalize(SettingsService.Current.ThemeName);
                ThemeManager.ApplyTitleBarColor(helper.Handle, res);
                if (ThemeManager.Themes.TryGetValue(res, out var t))
                    ThemeManager.SetCaptionColor(helper.Handle, t["BgDark"]);
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }
        }

        private Button MakeRangeButton(string text, bool active)
        {
            var b = new Button
            {
                Content = text, Padding = new Thickness(14, 7, 14, 7),
                Margin  = new Thickness(0, 0, 4, 0), Cursor = Cursors.Hand
            };
            b.Style = active
                ? (Style)Application.Current.Resources["OutlineButtonStyle"]
                : (Style)Application.Current.Resources["OutlineButtonStyle"];
            return b;
        }

        private Button MakeActionButton(string text)
        {
            var b = new Button { Content = text, Padding = new Thickness(14, 7, 14, 7), Cursor = Cursors.Hand };
            if (Application.Current.Resources["OutlineButtonStyle"] is Style s) b.Style = s;
            return b;
        }

        private static void SetRangeActive(Button active, Button inactive)
        {
            if (Application.Current.Resources["OutlineButtonStyle"]     is Style sa) active.Style   = sa;
            if (Application.Current.Resources["OutlineButtonStyle"] is Style si) inactive.Style = si;
        }

        private static void SetRangeActive(Button active, Button inactive1, Button inactive2)
        {
            SetRangeActive(active, inactive1);
            if (Application.Current.Resources["OutlineButtonStyle"] is Style si) inactive2.Style = si;
        }

        private UIElement LegendItem(WpfColor color, string label)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
            sp.Children.Add(new Rectangle
            {
                Width = 14, Height = 10, RadiusX = 3, RadiusY = 3,
                Fill  = new SolidColorBrush(color),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0)
            });
            sp.Children.Add(new TextBlock
            {
                Text = label, FontSize = Ui.FontXS, Foreground = Res("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            return sp;
        }
    }
}
