using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SMDWin.Services;
using Rectangle = System.Windows.Shapes.Rectangle;
using Orientation = System.Windows.Controls.Orientation;
using VAlign = System.Windows.VerticalAlignment;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;

namespace SMDWin.Views
{
    /// <summary>
    /// Event Viewer › "Last year": a calendar like GitHub's activity chart — weeks in columns, Monday…Sunday in rows, the months
    /// written above. Red = a blue screen that day, orange = serious errors, light orange = small problems, neutral = nothing
    /// worth noting; faint = days the Windows log no longer holds. A click on a day loads that day in the table below.
    /// The data (time / level / source / ID only) is read once, in the background, and kept for 10 minutes.
    /// </summary>
    public partial class MainWindow
    {
        private bool _evCalView = true;              // the card opens on the calendar; "Selected range" shows the bars
        private bool _evCalBuilt, _evCalLoading;
        private EventCalendarRaw? _evCalRaw;
        private Dictionary<DateTime, CalendarDay>? _evCalDays;
        private DateTime _evCalReadAt;
        private List<DateTime> _evCalDumps = new();
        private DateTime? _evCalSelected;
        private Border? _evSegCal, _evSegRange;
        private static readonly SolidColorBrush EvOrange = FreezeBrush(Color.FromRgb(0xF9, 0x73, 0x16));

        private static SolidColorBrush FreezeBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        /// <summary>Called after every scan: builds the switch once, shows the chosen view, reads the year if needed.</summary>
        private void EnsureEventCalendar()
        {
            try
            {
                if (FindName("EvTimelineHead") is not DockPanel head || FindName("EvCalendarCanvas") is not Canvas cv) return;
                if (!_evCalBuilt)
                {
                    _evCalBuilt = true;
                    var seg = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(2), Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VAlign.Center };
                    seg.SetResourceReference(Border.BackgroundProperty, "SegTrackBrush");
                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    seg.Child = row;
                    _evSegCal = SegItem(PT("Last year"), () => SetEventCalendarView(true));
                    _evSegRange = SegItem(PT("Selected range"), () => SetEventCalendarView(false));
                    row.Children.Add(_evSegCal); row.Children.Add(_evSegRange);
                    DockPanel.SetDock(seg, Dock.Right);
                    head.Children.Insert(0, seg);
                    cv.SizeChanged += (_, e) => { if (e.WidthChanged) DrawEventCalendar(); };
                }
                SetEventCalendarView(_evCalView);
            }
            catch (Exception ex) { AppLogger.Debug($"EnsureEventCalendar: {ex.Message}"); }
        }

        private Border SegItem(string text, Action click)
        {
            var b = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 3, 10, 3), Cursor = Cursors.Hand };
            var t = new TextBlock { Text = text, FontSize = Ui.FontS };
            b.Child = t;
            b.MouseLeftButtonUp += (_, _) => click();
            return b;
        }

        private void PaintSeg(Border? b, bool on)
        {
            if (b?.Child is not TextBlock t) return;
            if (on) { b.SetResourceReference(Border.BackgroundProperty, "SelBtnBg"); t.Foreground = Brushes.White; t.FontWeight = FontWeights.SemiBold; }
            else { b.Background = Brushes.Transparent; t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); t.FontWeight = FontWeights.Normal; }
        }

        private void SetEventCalendarView(bool calendar)
        {
            _evCalView = calendar;
            PaintSeg(_evSegCal, calendar);
            PaintSeg(_evSegRange, !calendar);
            var cal = calendar ? Visibility.Visible : Visibility.Collapsed;
            var bars = calendar ? Visibility.Collapsed : Visibility.Visible;
            if (FindName("EvCalendarPanel") is FrameworkElement p) p.Visibility = cal;
            if (FindName("EvTimelineLegend") is FrameworkElement lg) lg.Visibility = bars;
            if (EvTimelineBars != null) EvTimelineBars.Visibility = bars;
            if (EvTimelineLabels != null) EvTimelineLabels.Visibility = bars;
            if (!calendar)
            {
                // the bars' own caption comes back with the next scan; redraw them now from the last scan
                if (_eventScan != null) BuildEventsTimeline(_eventScan.Stamps);
                return;
            }
            TxtEvTimelineCaption.Text = PT("Last year");
            TxtEvTimelineSub.Text = PT("one square = one day · click a day to see its events");
            if (_evCalDays != null && DateTime.Now - _evCalReadAt < TimeSpan.FromMinutes(10)) DrawEventCalendar();
            else _ = LoadEventCalendarAsync();
        }

        private async Task LoadEventCalendarAsync()
        {
            if (_evCalLoading) return;
            _evCalLoading = true;
            try
            {
                if (FindName("EvCalendarFoot") is WrapPanel foot)
                {
                    foot.Children.Clear();
                    foot.Children.Add(FootText(PT("Reading the last year of the event log…"), "TextSecondaryBrush"));
                }
                var from = DateTime.Today.AddDays(-371);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var raw = await EventCalendar.ReadAsync(from, cts.Token);
                // crash dumps on disk also mark blue-screen days
                string folder = _crashService.MinidumpFolder;
                var dumps = await Task.Run(() =>
                {
                    try
                    {
                        return System.IO.Directory.Exists(folder)
                            ? System.IO.Directory.GetFiles(folder, "*.dmp").Select(f => System.IO.File.GetLastWriteTime(f)).Where(t => t >= from).ToList()
                            : new List<DateTime>();
                    }
                    catch (Exception ex) { AppLogger.Debug($"EventCalendar dumps: {ex.Message}"); return new List<DateTime>(); }
                });
                _evCalRaw = raw;
                _evCalDumps = dumps;
                _evCalDays = EventCalendar.Build(raw, dumps, CalendarLevelFilter());
                _evCalReadAt = DateTime.Now;
                if (_evCalView) DrawEventCalendar();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "LoadEventCalendarAsync"); }
            finally { _evCalLoading = false; }
        }

        /// <summary>The level chosen in the toolbar when it narrows the map to one kind of event (otherwise null = the usual map).</summary>
        private string? CalendarLevelFilter() => _selectedEventLevel is "Critical" or "Error" or "Warning" ? _selectedEventLevel : null;

        /// <summary>The level buttons changed: the map shows only that level; rebuilt from the data already read.</summary>
        private void RefreshEventCalendarLevel()
        {
            if (_evCalRaw == null) return;
            _evCalDays = EventCalendar.Build(_evCalRaw, _evCalDumps, CalendarLevelFilter());
            if (_evCalView) DrawEventCalendar();
        }

        private void DrawEventCalendar()
        {
            try
            {
                if (FindName("EvCalendarCanvas") is not Canvas cv || _evCalDays == null) return;
                cv.Children.Clear();
                double width = cv.ActualWidth;
                if (width < 200) return;
                var culture = LanguageCulture();
                var today = DateTime.Today;
                int MondayIdx(DateTime d) => ((int)d.DayOfWeek + 6) % 7;
                var start = today.AddDays(-364);
                start = start.AddDays(-MondayIdx(start));
                int weeks = (today - start).Days / 7 + 1;
                const double left = 30, top = 16, gap = 3;
                double cell = Math.Clamp(Math.Floor((width - left) / weeks - gap), 7, 15);
                cv.Height = top + 7 * (cell + gap);
                DateTime? oldest = _evCalRaw?.OldestSystem?.Date;
                var neutral = TryFindResource("BorderBrush2") as Brush ?? Brushes.Gray;
                var crit = TryFindResource("StatusCritBrush") as Brush ?? Brushes.IndianRed;
                var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.SteelBlue;

                // weekday names on rows 0, 2, 4 (Monday, Wednesday, Friday)
                foreach (var (rowI, dow) in new[] { (0, DayOfWeek.Monday), (2, DayOfWeek.Wednesday), (4, DayOfWeek.Friday) })
                {
                    var wl = SmallLabel(culture.DateTimeFormat.GetAbbreviatedDayName(dow));
                    Canvas.SetLeft(wl, 0); Canvas.SetTop(wl, top + rowI * (cell + gap) + (cell - 13) / 2);
                    cv.Children.Add(wl);
                }
                double lastLabelX = -100;
                for (var d = start; d <= today; d = d.AddDays(1))
                {
                    int col = (d - start).Days / 7, row = MondayIdx(d);
                    double x = left + col * (cell + gap), y = top + row * (cell + gap);
                    // the month name above the column that holds the 1st of the month
                    if (d.Day == 1 && x - lastLabelX > 28)
                    {
                        string mName = culture.DateTimeFormat.GetAbbreviatedMonthName(d.Month).TrimEnd('.');
                        if (d.Month == 1) mName += " " + d.Year;
                        var ml = SmallLabel(mName);
                        Canvas.SetLeft(ml, x); Canvas.SetTop(ml, 0);
                        cv.Children.Add(ml);
                        lastLabelX = x;
                    }
                    _evCalDays.TryGetValue(d, out var day);
                    bool noData = oldest.HasValue && d < oldest.Value && (day == null || day.Level == CalendarDayLevel.None);
                    var lvl = day?.Level ?? CalendarDayLevel.None;
                    var rect = new Rectangle { Width = cell, Height = cell, RadiusX = 2.5, RadiusY = 2.5, Cursor = Cursors.Hand };
                    rect.Fill = lvl switch
                    {
                        CalendarDayLevel.BlueScreen => crit,
                        CalendarDayLevel.Serious    => EvOrange,
                        CalendarDayLevel.Minor      => EvOrange,
                        _                           => neutral,
                    };
                    rect.Opacity = lvl == CalendarDayLevel.Minor ? 0.45 : noData ? 0.35 : 1;
                    if (_evCalSelected == d) { rect.Stroke = accent; rect.StrokeThickness = 2; }
                    rect.ToolTip = DayTip(d, day, noData, culture);
                    ToolTipService.SetInitialShowDelay(rect, 120);
                    var date = d;
                    rect.MouseLeftButtonUp += async (_, _) =>
                    {
                        _evCalSelected = date;
                        _evtFrom = date;
                        _evtTo = date.AddDays(1);
                        DrawEventCalendar();
                        await RunEventScan();
                    };
                    Canvas.SetLeft(rect, x); Canvas.SetTop(rect, y);
                    cv.Children.Add(rect);
                }
                FillEventCalendarFoot(culture);
            }
            catch (Exception ex) { AppLogger.Debug($"DrawEventCalendar: {ex.Message}"); }
        }

        private static CultureInfo LanguageCulture()
        {
            try { return CultureInfo.GetCultureInfo(LanguageService.CurrentCode switch { "ro" => "ro-RO", "de" => "de-DE", "fr" => "fr-FR", _ => "en-GB" }); }
            catch (Exception ex) { AppLogger.Debug($"LanguageCulture: {ex.Message}"); return CultureInfo.InvariantCulture; }
        }

        private TextBlock SmallLabel(string text)
        {
            var t = new TextBlock { Text = text, FontSize = Ui.FontXS };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            return t;
        }

        private static string DayTip(DateTime d, CalendarDay? day, bool noData, CultureInfo culture)
        {
            string s = d.ToString("dddd, d MMMM yyyy", culture);
            if (noData) return s + "\n" + PT("No log data for this day (the Windows log was already overwritten)");
            if (day == null || day.Level == CalendarDayLevel.None) return s + "\n" + PT("No problems");
            var lines = new List<string> { s };
            if (day.BlueScreens > 0) lines.Add(PT("Blue screen"));
            if (day.Serious > 0) lines.Add(PT("{0} serious errors", day.Serious));
            if (day.Minor > 0) lines.Add(PT("{0} small problems", day.Minor));
            lines.Add(PT("Click to see this day's events"));
            return string.Join("\n", lines);
        }

        private TextBlock FootText(string text, string brush, bool bold = false)
        {
            var t = new TextBlock { Text = text, FontSize = Ui.FontS, Margin = new Thickness(0, 2, 18, 2), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VAlign.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }

        private void FillEventCalendarFoot(CultureInfo culture)
        {
            if (FindName("EvCalendarFoot") is not WrapPanel foot || _evCalDays == null) return;
            foot.Children.Clear();
            // the legend
            void Key(Brush fill, double op, string text)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 14, 2) };
                sp.Children.Add(new Rectangle { Width = 10, Height = 10, RadiusX = 2, RadiusY = 2, Fill = fill, Opacity = op, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VAlign.Center });
                sp.Children.Add(FootText(text, "TextSecondaryBrush"));
                ((TextBlock)sp.Children[1]).Margin = new Thickness(0);
                foot.Children.Add(sp);
            }
            Key(TryFindResource("BorderBrush2") as Brush ?? Brushes.Gray, 1, PT("no problems"));
            Key(EvOrange, 0.45, PT("small problems"));
            Key(EvOrange, 1, PT("serious errors"));
            Key(TryFindResource("StatusCritBrush") as Brush ?? Brushes.IndianRed, 1, PT("blue screen"));
            if (CalendarLevelFilter() is string only)
                foot.Children.Add(FootText(PT("Showing only {0} events", PT(only)), "AccentBrush", true));
            foot.Children.Add(new Border { Width = 12 });

            // when the problems started and how often they come back
            string D(DateTime d) => d.ToString("d MMM yyyy", culture);
            var from = DateTime.Today.AddDays(-364);
            var year = _evCalDays.Values.Where(x => x.Date >= from).OrderBy(x => x.Date).ToList();
            var firstSerious = year.FirstOrDefault(x => x.Level >= CalendarDayLevel.Serious);
            foot.Children.Add(firstSerious == null ? FootText(PT("No serious errors in the last year"), "TextSecondaryBrush")
                                                   : FootText(PT("First serious error in the last year: {0}", D(firstSerious.Date)), "TextPrimaryBrush"));
            var bsod = year.Where(x => x.BlueScreens > 0).ToList();
            foot.Children.Add(bsod.Count == 0 ? FootText(PT("Blue screens: 0"), "TextSecondaryBrush")
                                              : FootText(PT("Blue screens: {0} (last: {1})", bsod.Count, D(bsod[^1].Date)), "StatusCritBrush", true));
            int last30 = year.Count(x => x.Date > DateTime.Today.AddDays(-30) && x.Level >= CalendarDayLevel.Serious);
            foot.Children.Add(FootText(PT("Days with serious errors in the last 30 days: {0}", last30), last30 > 0 ? "TextPrimaryBrush" : "TextSecondaryBrush"));
            if (_evCalRaw?.OldestSystem is DateTime old && old > from)
                foot.Children.Add(FootText(PT("The Windows log goes back only to {0}", D(old)), "TextSecondaryBrush"));
            if (_evCalRaw?.Partial == true)
                foot.Children.Add(FootText(PT("Not every event could be read (very large log)"), "TextSecondaryBrush"));
        }
    }
}
