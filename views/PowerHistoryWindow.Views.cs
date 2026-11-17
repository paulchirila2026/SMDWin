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
    public partial class PowerHistoryWindow
    {
        // ── Timeline ──────────────────────────────────────────────────────────
        private const double BarH   = 32;
        private const double RowH   = 48;
        private const double LabelW = 62;
        private const double UptimeW = 94;


        private void DrawTimeline()
        {
            _timelinePanel.Children.Clear();
            // DAY-VIEW: if viewMode==day, show that day (today by default if none is selected)
            if (_viewMode == "day")
            {
                _filterDay ??= DateTime.Today;
                RenderDayView(_filterDay.Value);
                return;
            }
            // When returning from the day view, show the bottom table again
            if (_gridBorder != null) _gridBorder.Visibility = System.Windows.Visibility.Visible;
            // CALENDAR: in "month" mode draw the calendar heatmap; otherwise the weekly hourly bars.
            if (_viewMode == "month") { RenderMonthCalendar(); ApplyDayFilter(); return; }

            // ── WEEK VIEW: Monday→Sunday with navigation ──────────────────────────
            RenderWeekView();
            ApplyDayFilter();
        }

        private void RenderWeekView()
        {
            var today   = DateTime.Today;
            var nowFrac = DateTime.Now.TimeOfDay.TotalSeconds / 86400.0;

            // Header: ‹ Prev week | titlu | Next week ›
            var navGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            navGrid.ColumnDefinitions.Add(new ColumnDefinition());
            navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var weekEnd = _weekStart.AddDays(6);
            var weekLbl = new TextBlock
            {
                Text = $"Week  {_weekStart:dd MMM} – {weekEnd:dd MMM yyyy}",
                FontSize = Ui.FontXL, FontWeight = FontWeights.Bold,
                Foreground = Res("TextPrimaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(weekLbl, 1); navGrid.Children.Add(weekLbl);

            var btnPrev = MakeNavArrow("‹ Previous week");
            btnPrev.Click += (_, _) =>
            {
                _weekStart = _weekStart.AddDays(-7);
                _filterDay = null;
                DrawTimeline();
            };
            Grid.SetColumn(btnPrev, 0); navGrid.Children.Add(btnPrev);

            var btnNext = MakeNavArrow("Next week ›");
            bool canGoNext = _weekStart.AddDays(7) <= today;
            btnNext.IsEnabled = canGoNext;
            btnNext.Click += (_, _) =>
            {
                _weekStart = _weekStart.AddDays(7);
                _filterDay = null;
                DrawTimeline();
            };
            Grid.SetColumn(btnNext, 2); navGrid.Children.Add(btnNext);
            _timelinePanel.Children.Add(navGrid);

            // Show exactly the 7 days of the week (Monday–Sunday)
            var weekDays = Enumerable.Range(0, 7)
                .Select(i => _weekStart.AddDays(i))
                .ToList();

            // Build a quick lookup from _daysSum
            var sumMap = _daysSum.ToDictionary(d => d.Date, d => d);

            foreach (var date in weekDays)
            {
                bool isToday    = date == today;
                bool isFuture   = date > today;
                bool isSelected = _filterDay.HasValue && _filterDay.Value == date;
                sumMap.TryGetValue(date, out var day);

                var row = new Grid
                {
                    Height     = RowH,
                    Background = isSelected
                        ? new SolidColorBrush(WpfColor.FromArgb(18, 96, 165, 250))
                        : Brushes.Transparent,
                    Cursor = isFuture ? Cursors.Arrow : Cursors.Hand,
                    Opacity = isFuture ? 0.4 : 1.0
                };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelW) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(UptimeW) });

                var dayLbl = new TextBlock
                {
                    Text       = date.ToString("ddd dd", _uiCulture),
                    FontSize   = Ui.FontS,
                    FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = isToday ? Res("AccentBrush") : Res("TextSecondaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(dayLbl, 0); row.Children.Add(dayLbl);

                if (!isFuture && day != null)
                {
                    var barCanvas = new Canvas
                    {
                        Height = BarH, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true
                    };
                    var capturedDay = day;
                    barCanvas.SizeChanged += (_, e) => RenderBarCanvas(barCanvas, capturedDay, e.NewSize.Width, isToday, nowFrac);
                    barCanvas.MouseMove   += (_, e) => OnBarMouseMove(barCanvas, capturedDay, e);
                    barCanvas.MouseLeave  += (_, _) => _hoverTip.IsOpen = false;
                    barCanvas.ToolTip      = _hoverTip;
                    Grid.SetColumn(barCanvas, 1); row.Children.Add(barCanvas);

                    double uptimeH = day.UptimeHours;
                    string uptimeTxt = uptimeH < 0.05 ? "offline"
                        : $"{(int)uptimeH}h {(int)((uptimeH % 1) * 60):D2}m";
                    string crashTxt = day.Crashes > 0 ? $" ⚡{day.Crashes}" : "";
                    var uptimeLbl = new TextBlock
                    {
                        Text = uptimeTxt + crashTxt,
                        FontSize = Ui.FontXS,
                        VerticalAlignment   = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Foreground = day.Crashes > 0
                            ? new SolidColorBrush(WpfColor.FromRgb(239, 68, 68))
                            : Res("TextSecondaryBrush"),
                        Margin = new Thickness(8, 0, 0, 0)
                    };
                    Grid.SetColumn(uptimeLbl, 2); row.Children.Add(uptimeLbl);
                }
                else
                {
                    // Empty bar (a day without data or in the future)
                    var emptyBar = new Border
                    {
                        Height = BarH, CornerRadius = new CornerRadius(3),
                        Background = new SolidColorBrush(WpfColor.FromArgb(20, 150, 150, 160)),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(emptyBar, 1); row.Children.Add(emptyBar);
                    var offLbl = new TextBlock
                    {
                        Text = isFuture ? "" : "no data",
                        FontSize = Ui.FontXS, VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Foreground = Res("TextSecondaryBrush"), Margin = new Thickness(8, 0, 0, 0)
                    };
                    Grid.SetColumn(offLbl, 2); row.Children.Add(offLbl);
                }

                if (!isFuture)
                {
                    var capturedDate = date;
                    row.MouseLeftButtonUp += (_, _) =>
                    {
                        bool wasSelected2 = _filterDay.HasValue && _filterDay.Value == capturedDate;
                        _filterDay = wasSelected2 ? (DateTime?)null : capturedDate;
                        _filterLabel.Text = _filterDay.HasValue ? $"Filtered: {_filterDay.Value:ddd dd MMM}" : "";
                        _filterLabel.Visibility = _filterDay.HasValue ? Visibility.Visible : Visibility.Collapsed;
                        if (_filterLabel.Tag is Button cb)
                            cb.Visibility = _filterDay.HasValue ? Visibility.Visible : Visibility.Collapsed;
                        if (_filterDay.HasValue)
                        {
                            _viewMode = "day";
                            SetRangeActive(_btnDay, _btnMonth, _btnWeek);
                        }
                        else
                        {
                            _viewMode = "week";
                            SetRangeActive(_btnWeek, _btnMonth, _btnDay);
                        }
                        DrawTimeline();
                    };
                }

                _timelinePanel.Children.Add(row);
            }
        }

        // ── CALENDAR: heatmap lunar ───────────────────────────────────────────
        private void RenderMonthCalendar()
        {
            var sum = new Dictionary<DateTime, DaySummary>();
            foreach (var d in _daysSum) sum[d.Date] = d;

            // Header luna + navigare
            var navGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            navGrid.ColumnDefinitions.Add(new ColumnDefinition());
            navGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var monthLbl = new TextBlock
            {
                Text = _calMonth.ToString("MMMM yyyy", _uiCulture),
                FontSize = Ui.FontXL, FontWeight = FontWeights.Bold,
                Foreground = Res("TextPrimaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(monthLbl, 1); navGrid.Children.Add(monthLbl);

            var btnPrev = MakeNavArrow("‹");
            var btnNext = MakeNavArrow("›");
            btnPrev.Click += (_, _) => { _calMonth = _calMonth.AddMonths(-1); DrawTimeline(); };
            btnNext.Click += (_, _) => { if (_calMonth < new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)) { _calMonth = _calMonth.AddMonths(1); DrawTimeline(); } };
            Grid.SetColumn(btnPrev, 0); navGrid.Children.Add(btnPrev);
            Grid.SetColumn(btnNext, 2); navGrid.Children.Add(btnNext);
            _timelinePanel.Children.Add(navGrid);

            // Capete zile (Mon..Sun)
            var dowGrid = new Grid();
            for (int i = 0; i < 7; i++) dowGrid.ColumnDefinitions.Add(new ColumnDefinition());
            string[] dows = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
            for (int i = 0; i < 7; i++)
            {
                var dl = new TextBlock
                {
                    Text = dows[i], FontSize = Ui.FontXS, HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = Res("TextSecondaryBrush"), Opacity = IsLightTheme ? 0.9 : 0.6, Margin = new Thickness(0, 0, 0, 4)
                };
                Grid.SetColumn(dl, i); dowGrid.Children.Add(dl);
            }
            _timelinePanel.Children.Add(dowGrid);

            // The day grid
            var firstOfMonth = new DateTime(_calMonth.Year, _calMonth.Month, 1);
            int daysInMonth  = DateTime.DaysInMonth(_calMonth.Year, _calMonth.Month);
            // Luni=0 .. Duminica=6
            int leadBlanks = ((int)firstOfMonth.DayOfWeek + 6) % 7;
            var today = DateTime.Today;

            var calGrid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            for (int i = 0; i < 7; i++) calGrid.ColumnDefinitions.Add(new ColumnDefinition());
            int totalCells = leadBlanks + daysInMonth;
            int rows = (totalCells + 6) / 7;
            for (int r = 0; r < rows; r++) calGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int cell = 0; cell < totalCells; cell++)
            {
                int r = cell / 7, c = cell % 7;
                if (cell < leadBlanks) continue;
                int dayNum = cell - leadBlanks + 1;
                var date = new DateTime(_calMonth.Year, _calMonth.Month, dayNum);
                bool future = date > today;
                sum.TryGetValue(date, out var ds);

                double uptime = ds?.UptimeHours ?? 0;
                int crashes   = ds?.Crashes ?? 0;
                bool isSel    = _filterDay.HasValue && _filterDay.Value == date;

                var cellBorder = new Border
                {
                    Margin = new Thickness(3),
                    CornerRadius = new CornerRadius(4),
                    Background = CalCellColor(uptime, crashes, future),
                    BorderBrush = isSel ? Res("AccentBrush")
                                : date == today ? Res("TextSecondaryBrush") : Brushes.Transparent,
                    BorderThickness = new Thickness(isSel ? 2 : date == today ? 1 : 0),
                    MinHeight = 42,
                    Cursor = future ? Cursors.Arrow : Cursors.Hand
                };

                var cellGrid = new Grid { Margin = new Thickness(6, 4, 6, 4) };
                cellGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                cellGrid.RowDefinitions.Add(new RowDefinition());
                var numTb = new TextBlock
                {
                    Text = dayNum.ToString(), FontSize = Ui.FontS,
                    FontWeight = date == today ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = future ? Res("TextSecondaryBrush")
                               : crashes > 0 ? new SolidColorBrush(WpfColor.FromRgb(239, 68, 68))
                               : Res("TextPrimaryBrush"),
                    Opacity = future ? 0.4 : 1
                };
                Grid.SetRow(numTb, 0); cellGrid.Children.Add(numTb);

                if (!future && uptime >= 0.05)
                {
                    var upTb = new TextBlock
                    {
                        Text = crashes > 0 ? $"⚡{crashes}" : $"{uptime:0.0}h",
                        FontSize = 9,
                        Foreground = crashes > 0 ? new SolidColorBrush(WpfColor.FromRgb(239, 68, 68))
                                   : Res("TextSecondaryBrush"),
                        VerticalAlignment = VerticalAlignment.Bottom,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Opacity = 0.85
                    };
                    Grid.SetRow(upTb, 1); cellGrid.Children.Add(upTb);
                }

                cellBorder.Child = cellGrid;
                if (!future)
                {
                    var captured = date;
                    cellBorder.MouseLeftButtonUp += (_, _) =>
                    {
                        bool wasSelected = _filterDay == captured;
                        _filterDay = wasSelected ? (DateTime?)null : captured;
                        _filterLabel.Text = _filterDay.HasValue ? $"Filtered: {_filterDay.Value:ddd dd MMM}" : "";
                        _filterLabel.Visibility = _filterDay.HasValue ? Visibility.Visible : Visibility.Collapsed;
                        if (_filterLabel.Tag is Button cb)
                            cb.Visibility = _filterDay.HasValue ? Visibility.Visible : Visibility.Collapsed;
                        // DAY-VIEW: click on a day in the calendar => navigate to the day view
                        if (_filterDay.HasValue && !wasSelected)
                        {
                            SetRangeActive(_btnDay, _btnMonth, _btnWeek);
                            _viewMode = "day";
                        }
                        else if (!_filterDay.HasValue)
                        {
                            SetRangeActive(_btnMonth, _btnWeek, _btnDay);
                            _viewMode = "month";
                        }
                        ApplyDayFilter();
                        DrawTimeline();
                    };
                }
                Grid.SetColumn(cellBorder, c); Grid.SetRow(cellBorder, r);
                calGrid.Children.Add(cellBorder);
            }
            _timelinePanel.Children.Add(calGrid);

            // Legenda
            var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            legend.Children.Add(new TextBlock { Text = "Less", FontSize = Ui.FontXS, Foreground = Res("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            foreach (var u in new[] { 1.0, 5.0, 10.0, 18.0 })
            {
                legend.Children.Add(new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(3), Background = CalCellColor(u, 0, false), Margin = new Thickness(2, 0, 2, 0) });
            }
            legend.Children.Add(new TextBlock { Text = "More uptime", FontSize = Ui.FontXS, Foreground = Res("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 14, 0) });
            legend.Children.Add(new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(WpfColor.FromRgb(239, 68, 68)), Margin = new Thickness(0, 0, 6, 0) });
            legend.Children.Add(new TextBlock { Text = "Crash", FontSize = Ui.FontXS, Foreground = Res("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center });
            _timelinePanel.Children.Add(legend);
        }

        // The colour of a day in the heatmap by the uptime hours (graded green) or red if there was a crash.
        private Brush CalCellColor(double uptimeHours, int crashes, bool future)
        {
            if (future) return new SolidColorBrush(WpfColor.FromArgb(10, 128, 128, 128));
            if (crashes > 0) return new SolidColorBrush(WpfColor.FromArgb(60, 239, 68, 68));
            if (uptimeHours < 0.05) return new SolidColorBrush(WpfColor.FromArgb(16, 128, 128, 128));
            // 4 shades of green by uptime
            if (uptimeHours < 3)  return new SolidColorBrush(WpfColor.FromArgb(55, 34, 197, 94));
            if (uptimeHours < 7)  return new SolidColorBrush(WpfColor.FromArgb(95, 34, 197, 94));
            if (uptimeHours < 12) return new SolidColorBrush(WpfColor.FromArgb(150, 34, 197, 94));
            return new SolidColorBrush(WpfColor.FromArgb(210, 34, 197, 94));
        }

        private Button MakeNavArrow(string glyph)
        {
            bool isTextBtn = glyph.Length > 2;
            var b = new Button
            {
                Content = glyph,
                Width = isTextBtn ? double.NaN : 32,
                Height = 30, Cursor = Cursors.Hand,
                FontSize = isTextBtn ? 11 : 15,
                Padding = isTextBtn ? new Thickness(12, 4, 12, 4) : new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            // Use the style defined in the theme — not inline styles that ignore the theme's hover
            if (isTextBtn)
            {
                if (Application.Current.Resources["OutlineButtonStyle"] is Style os) b.Style = os;
            }
            else
            {
                // Simple ‹ › buttons — transparent, no border, no default WPF blue highlight
                b.Background = Brushes.Transparent;
                b.Foreground = Res("TextPrimaryBrush");
                b.BorderThickness = new Thickness(0);
                b.BorderBrush = Brushes.Transparent;
                var plain = new Style(typeof(Button));
                plain.Setters.Add(new Setter(Button.BackgroundProperty, Brushes.Transparent));
                plain.Setters.Add(new Setter(Button.ForegroundProperty, Res("TextPrimaryBrush")));
                plain.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(0)));
                plain.Setters.Add(new Setter(Button.BorderBrushProperty, Brushes.Transparent));
                plain.Setters.Add(new Setter(Button.PaddingProperty, new Thickness(4, 0, 4, 0)));
                plain.Setters.Add(new Setter(Button.CursorProperty, Cursors.Hand));
                var template = new ControlTemplate(typeof(Button));
                var border = new FrameworkElementFactory(typeof(Border));
                border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
                border.SetValue(Border.PaddingProperty, new Thickness(4, 0, 4, 0));
                var content = new FrameworkElementFactory(typeof(ContentPresenter));
                content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                border.AppendChild(content);
                template.VisualTree = border;
                plain.Setters.Add(new Setter(Button.TemplateProperty, template));
                b.Style = plain;
            }
            return b;
        }

        private void RenderBarCanvas(Canvas canvas, DaySummary day, double w, bool isToday, double nowFrac)
        {
            canvas.Children.Clear();
            if (w < 4) return;

            // A more pleasant background track (small corners, not a full pill)
            var bg = new Rectangle
            {
                Fill = new SolidColorBrush(WpfColor.FromArgb(28, 150, 150, 160)),
                RadiusX = 3, RadiusY = 3, Width = w, Height = BarH
            };
            Canvas.SetTop(bg, 0); canvas.Children.Add(bg);

            // Subtle hourly gridlines every 3h + labels
            for (int h = 3; h < 24; h += 3)
            {
                double gx = h / 24.0 * w;
                canvas.Children.Add(new Line
                {
                    X1 = gx, X2 = gx, Y1 = 2, Y2 = BarH - 2,
                    Stroke = new SolidColorBrush(IsLightTheme ? WpfColor.FromArgb(60, 71, 85, 105) : WpfColor.FromArgb(22, 200, 200, 220)),
                    StrokeThickness = 1
                });
            }

            // Uptime segments with rounded corners + glow on crash
            foreach (var seg in day.Segments)
            {
                double x    = seg.StartFrac * w;
                double segW = Math.Max(4, (seg.EndFrac - seg.StartFrac) * w);

                LinearGradientBrush fill;
                if (seg.IsBad)
                    fill = new LinearGradientBrush(WpfColor.FromRgb(220, 38, 38), WpfColor.FromRgb(248, 113, 113), new WpfPoint(0,0), new WpfPoint(0,1));
                else if (seg.IsUnknown)
                    fill = new LinearGradientBrush(WpfColor.FromRgb(100, 106, 118), WpfColor.FromRgb(150, 156, 168), new WpfPoint(0,0), new WpfPoint(0,1));
                else if (seg.IsSleep)
                    fill = new LinearGradientBrush(WpfColor.FromRgb(202, 138, 4), WpfColor.FromRgb(251, 191, 36), new WpfPoint(0,0), new WpfPoint(0,1));
                else
                    fill = new LinearGradientBrush(WpfColor.FromRgb(22, 163, 74), WpfColor.FromRgb(74, 222, 128), new WpfPoint(0,0), new WpfPoint(0,1));

                var rect = new Rectangle
                {
                    Width = segW, Height = BarH, RadiusX = 3, RadiusY = 3, Fill = fill
                };
                if (seg.IsBad)
                    rect.Effect = new System.Windows.Media.Effects.DropShadowEffect
                    { Color = WpfColor.FromRgb(239, 68, 68), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.8 };
                Canvas.SetLeft(rect, x); Canvas.SetTop(rect, 0);
                canvas.Children.Add(rect);
            }

            // Markers for the day's events (boot/shutdown/sleep/crash) — coloured dots positioned at the exact hour, above the bar.
            var dayEvents = _events.Where(ev => ev.Time.Date == day.Date).ToList();
            foreach (var ev in dayEvents)
            {
                double frac = ev.Time.TimeOfDay.TotalSeconds / 86400.0;
                double mx = frac * w;
                var clr = (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(ev.KindColor)!;
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 9, Height = 9,
                    Fill = new SolidColorBrush(clr),
                    Stroke = Res("BgDarkBrush"), StrokeThickness = 1.5,
                    ToolTip = $"{ev.Time:HH:mm}  {ev.KindLabel}"
                };
                Canvas.SetLeft(dot, mx - 4.5);
                Canvas.SetTop(dot, BarH / 2 - 4.5);
                canvas.Children.Add(dot);
            }

            // "now" indicator on the current day
            if (isToday)
            {
                double nowX = nowFrac * w;
                canvas.Children.Add(new Line
                {
                    X1 = nowX, X2 = nowX, Y1 = -3, Y2 = BarH + 3,
                    Stroke = new SolidColorBrush(WpfColor.FromArgb(220, 250, 204, 21)),
                    StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 2 }
                });
            }
        }

        private void OnBarMouseMove(Canvas barCanvas, DaySummary day, MouseEventArgs e)
        {
            double x = e.GetPosition(barCanvas).X;
            double w = barCanvas.ActualWidth;
            if (w < 1) return;

            double frac = x / w;
            var time    = day.Date + TimeSpan.FromHours(frac * 24);

            // Is cursor inside a running segment?
            var seg = day.Segments.FirstOrDefault(s => frac >= s.StartFrac && frac <= s.EndFrac);
            string state = seg != default
                ? (seg.IsBad ? "⚡ Crashed" : seg.IsUnknown ? "❔ Unknown (no data)" : seg.IsSleep ? "💤 Sleep / Standby" : "✅ Running")
                : "— Offline";

            _hoverTip.Content = $"{day.Date:ddd dd MMM}  {time:HH:mm}\n{state}";
            _hoverTip.IsOpen  = true;
        }

        // ── Stats ─────────────────────────────────────────────────────────────
        private void BuildStats(bool hasSecLog)
        {
            int totalBoots  = _events.Count(e => e.Kind == EventKind.Boot);
            int recentCrash = _events.Count(e => e.IsBad && e.Time >= DateTime.Now.AddDays(-7));
            int totalCrash  = _events.Count(e => e.IsBad);

            var bootTimes = _events.Where(e => e.Kind == EventKind.Boot).Select(e => e.Time).ToList();
            string avgBoot = bootTimes.Any()
                ? TimeSpan.FromSeconds(bootTimes.Average(t => t.TimeOfDay.TotalSeconds)).ToString(@"hh\:mm")
                : "—";

            double totalOnline  = _daysSum.Sum(d => d.UptimeHours);
            double totalSleep   = _daysSum.Sum(d => d.Segments.Where(s => s.IsSleep).Sum(s => (s.EndFrac - s.StartFrac) * 24.0));
            double totalOffline = Math.Max(0, _days * 24.0 - totalOnline - totalSleep);
            double avgUptime    = _daysSum.Where(d => d.UptimeHours > 0)
                                          .DefaultIfEmpty().Average(d => d?.UptimeHours ?? 0);

            string secNote = hasSecLog ? "" : "  ·  logon/logoff: Admin required";

            _txtStats.Text =
                $"Uptime avg: {(int)avgUptime}h {(int)((avgUptime % 1) * 60):D2}m   │   " +
                $"Active: {(int)totalOnline}h   Sleep: {(int)totalSleep}h   Offline: {(int)totalOffline}h   │   " +
                $"Boots: {totalBoots}   │   " +
                $"Avg boot hour: {avgBoot}" + secNote;
            _txtStats.ToolTip = $"\"Avg boot hour\" = ora medie la care porneste sistemul (nu durata boot-ului). Calculat din {bootTimes.Count} boot-uri in ultimele {_days} zile.";

            // Crash badge
            if (recentCrash > 0)
            {
                _txtCrash.Text         = $"⚡ {recentCrash} crash{(recentCrash > 1 ? "es" : "")} (7d)";
                _crashBadge.Visibility = Visibility.Visible;
            }
            else
            {
                _crashBadge.Visibility = Visibility.Collapsed;
            }
        }

        // Updates the chip style (active = AppButtonStyle, inactive = OutlineButtonStyle)
        private void UpdateChipStyles()
        {
            foreach (var child in _filterChips.Children.OfType<Button>())
            {
                bool active = child.Tag?.ToString() == _kindFilter;
                child.Style = active
                    ? (Style)Application.Current.Resources["OutlineButtonStyle"]
                    : (Style)Application.Current.Resources["OutlineButtonStyle"];
            }
        }
    }
}
