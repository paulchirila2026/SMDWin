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
        // DAY-VIEW: a 24h chart with a continuous coloured bar (Running/Sleep/Offline/Crash) and markers for each event (Boot,
        // Shutdown, Crash, Sleep, Wake etc.)
        private void RenderDayView(DateTime day)
        {
            if (_renderDayInProgress) return;
            _renderDayInProgress = true;
            try
            {
            // In the day view, the top card (_timelinePanel) holds only the day + timeline + nav, and the event table goes into the
            // bottom card (_gridBorder/DataGrid). _gridBorder is re-enabled at the end of the method with the day's events.
            _timelinePanel.Children.Clear();

            var evts = _events.Where(e => e.Time.Date == day.Date)
                              .OrderBy(e => e.Time).ToList();

            // ── Header with prev/next navigation at the top ───────────────────────────────────
            var hdrGrid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // prev
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition());                            // titlu
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // stats
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // next

            // Butoane prev/next zi
            DateTime prevDay = day.AddDays(-1);
            DateTime nextDay = day.AddDays(1);
            bool hasPrev = _events.Any(e => e.Time.Date == prevDay.Date);
            bool hasNext = nextDay <= DateTime.Today && _events.Any(e => e.Time.Date == nextDay.Date);

            var btnPrevDay = MakeNavArrow("‹");
            btnPrevDay.IsEnabled = hasPrev;
            btnPrevDay.ToolTip   = hasPrev ? $"Previous day: {prevDay:ddd dd MMM}" : "No data for previous day";
            btnPrevDay.Click += (_, _) =>
            {
                _filterDay = prevDay;
                _renderDayInProgress = false;
                DrawTimeline();
            };
            Grid.SetColumn(btnPrevDay, 0); hdrGrid.Children.Add(btnPrevDay);

            var dayTitleTb = new TextBlock
            {
                Text = day.ToString("dddd, d MMMM yyyy", _uiCulture),
                FontSize = Ui.FontXL, FontWeight = FontWeights.SemiBold,
                Foreground = Res("TextPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0)
            };
            Grid.SetColumn(dayTitleTb, 1); hdrGrid.Children.Add(dayTitleTb);

            // Summary stats on the right of the header (before next)
            var statsPanel = new StackPanel
            { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            void AddStat(string icon, string val, string color)
            {
                statsPanel.Children.Add(new TextBlock
                {
                    Text = $"{icon} {val}  ",
                    FontSize = Ui.FontXS, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush((WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(color)!)
                });
            }

            // Computes statistics from the events
            int bootCount  = evts.Count(e => e.Kind == EventKind.Boot);
            int crashCount = evts.Count(e => e.IsBad);
            int sleepCount = evts.Count(e => e.IsSleep);

            // Computes the active uptime (between Boot and Shutdown/Sleep, that day)
            TimeSpan activeTime = ComputeActiveTime(evts, day);

            if (bootCount  > 0) AddStat("⚡", $"{bootCount} boot{(bootCount>1?"s":"")}", "#22c55e");
            if (activeTime > TimeSpan.Zero) AddStat("🕐", activeTime.ToString(@"h\hm\m"), "#3b82f6");
            if (sleepCount > 0) AddStat("💤", $"{sleepCount}×", "#f59e0b");
            if (crashCount > 0) AddStat("💥", $"{crashCount}", "#ef4444");

            Grid.SetColumn(statsPanel, 2); hdrGrid.Children.Add(statsPanel);

            var btnNextDay = MakeNavArrow("›");
            btnNextDay.IsEnabled = hasNext;
            btnNextDay.ToolTip   = hasNext ? $"Next day: {nextDay:ddd dd MMM}" : "No data for next day";
            btnNextDay.Click += (_, _) =>
            {
                _filterDay = nextDay;
                _renderDayInProgress = false;
                DrawTimeline();
            };
            Grid.SetColumn(btnNextDay, 3); hdrGrid.Children.Add(btnNextDay);

            _timelinePanel.Children.Add(hdrGrid);

            // ── Legenda ──────────────────────────────────────────────────────
            var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            void AddLegend(string color, string label)
            {
                var c = WpfColor.FromArgb(0, 0, 0, 0);
                try { c = (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(color)!; } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                legend.Children.Add(new Border
                {
                    Width = 12, Height = 12, CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(c), Margin = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                legend.Children.Add(new TextBlock
                {
                    Text = SMDWin.Views.MainWindow.PT(label), FontSize = Ui.FontXS, Margin = new Thickness(0, 0, 14, 0),
                    Foreground = Res("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center
                });
            }
            AddLegend("#22c55e", "Running");
            AddLegend("#f59e0b", "Sleep / Standby");
            AddLegend("#ef4444", "Crash / Unexpected");
            AddLegend("#7d838f", "Unknown (no data)");
            AddLegend("#475569", "Offline");
            _timelinePanel.Children.Add(legend);

            // ── Continuous 24h bar ────────────────────────────────────────────
            var timelineCanvas = new Canvas
            {
                Height = 80, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 8, 0, 8)
            };

            // Bind width to parent when loaded
            timelineCanvas.Loaded += (_, _) =>
            {
                double totalW = timelineCanvas.ActualWidth > 0 ? timelineCanvas.ActualWidth : 600;
                DrawDayTimeline(timelineCanvas, evts, day, totalW);
            };
            timelineCanvas.SizeChanged += (_, e) =>
            {
                timelineCanvas.Children.Clear();
                DrawDayTimeline(timelineCanvas, evts, day, e.NewSize.Width);
            };

            // A wrapper that takes the available width
            var timelineWrapper = new Border
            {
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 4)
            };
            timelineWrapper.Child = timelineCanvas;
            _timelinePanel.Children.Add(timelineWrapper);

            // ── Etichete ore (0, 4, 8, 12, 16, 20, 24) ─────────────────────
            var hourLabels = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            for (int i = 0; i <= 6; i++)
                hourLabels.ColumnDefinitions.Add(new ColumnDefinition());
            int[] labelHours = { 0, 4, 8, 12, 16, 20, 24 };
            for (int i = 0; i < labelHours.Length; i++)
            {
                var lbl = new TextBlock
                {
                    Text = $"{labelHours[i]:00}:00",
                    FontSize = 9, Foreground = Res("TextSecondaryBrush"),
                    Opacity = IsLightTheme ? 0.9 : 0.6,   // the axis: 60 % of the grey text was too faint on Light
                    HorizontalAlignment = i == 0
                        ? System.Windows.HorizontalAlignment.Left
                        : i == labelHours.Length - 1
                            ? System.Windows.HorizontalAlignment.Right
                            : System.Windows.HorizontalAlignment.Center
                };
                Grid.SetColumn(lbl, i);
                hourLabels.Children.Add(lbl);
            }
            _timelinePanel.Children.Add(hourLabels);

            // ── The event list — in the bottom card (_grid/DataGrid) ────
            // The top card (_timelinePanel) stays clean: the day + 24h timeline + nav.
            // The event table is shown in the bottom DataGrid (sortable, has room).
            if (evts.Count == 0)
            {
                _timelinePanel.Children.Add(new TextBlock
                {
                    Text = "No events recorded for this day.",
                    FontSize = Ui.FontM, Foreground = Res("TextSecondaryBrush"),
                    Margin = new Thickness(0, 4, 0, 0), Opacity = 0.7
                });
            }

            // Fill the bottom DataGrid through ApplyDayFilter (one place)
            if (_gridBorder != null)
                _gridBorder.Visibility = System.Windows.Visibility.Visible;
            ApplyDayFilter();

            RenderDayWarnings(day);
            } finally { _renderDayInProgress = false; }
        }

        // ── Warnings for a day — integrated into the DataGrid as Warning rows ──────
        private async void RenderDayWarnings(DateTime day)
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
            var ct = cts.Token;

            var allWarnings = new List<PowerEvent>();

            try
            {
            // 1. SensorLogger — temperatures + throttle (only if SMDWin was running)
            try
            {
                string logDir = SMDWin.Services.AppLogger.LogDirectory;
                string logFile = System.IO.Path.Combine(logDir, $"sensors-{day:yyyy-MM-dd}.csv");
                if (File.Exists(logFile))
                {
                    var sensorWarnings = await Task.Run(() =>
                    {
                        var logger = new SMDWin.Services.SensorLogger();
                        return logger.GetWarningsForDay(day, 85f, 85f);
                    }, ct);
                    foreach (var w in sensorWarnings)
                        allWarnings.Add(new PowerEvent
                        {
                            Time = w.Time, Kind = EventKind.Boot /* placeholder */,
                            Category = w.Category, WarnIcon = w.Category == "CPU" ? "🌡" : "🌡",
                            WarnColor = w.Color, Notes = w.Message, Duration = "—"
                        });
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }

            // 2. CrashService — BSOD / crash dumps
            try
            {
                var crashes = await Task.Run(async () =>
                    await new SMDWin.Services.CrashService().GetCrashesAsync(), ct);
                foreach (var c in crashes.Where(c =>
                    c.CrashTime.Date == day.Date &&
                    !string.IsNullOrEmpty(c.StopCode) && c.StopCode != "—"))
                {
                    allWarnings.Add(new PowerEvent
                    {
                        Time = c.CrashTime, Kind = EventKind.Boot /* placeholder */,
                        Category = "BSOD", WarnIcon = "💥", WarnColor = "#EF4444",
                        Notes = $"BSOD — {c.StopCode}  ({c.FileName})", Duration = "—"
                    });
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }

            // 3. Event Log — critical system errors
            try
            {
                await Task.Run(() =>
                {
                    var query = new System.Diagnostics.Eventing.Reader.EventLogQuery(
                        "System", System.Diagnostics.Eventing.Reader.PathType.LogName,
                        $"*[System[(Level=1 or Level=2) and TimeCreated[@SystemTime>='{day.Date:yyyy-MM-dd}T00:00:00Z' and @SystemTime<'{day.Date.AddDays(1):yyyy-MM-dd}T00:00:00Z']]]");
                    using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);
                    int count = 0;
                    while (reader.ReadEvent() is { } evRecord && count < 20)
                    using (evRecord)
                    {
                        ct.ThrowIfCancellationRequested();
                        count++;
                        string src = evRecord.ProviderName ?? "System";
                        string msg = "";
                        try { msg = evRecord.FormatDescription() ?? ""; } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                        if (msg.Length > 120) msg = msg[..120] + "…";
                        string col = evRecord.Level == 1 ? "#EF4444" : "#F59E0B";
                        lock (allWarnings)
                            allWarnings.Add(new PowerEvent
                            {
                                Time = evRecord.TimeCreated ?? day, Kind = EventKind.Boot,
                                Category = "EventLog", WarnIcon = "⚠", WarnColor = col,
                                Notes = $"[{src}] {(string.IsNullOrEmpty(msg) ? $"Event {evRecord.Id}" : msg)}",
                                Duration = "—"
                            });
                        evRecord.Dispose();
                    }
                }, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }

            if (allWarnings.Count == 0) return;

            // Add the warnings to the DataGrid next to the power events (already populated by ApplyDayFilter)
            if (_grid.ItemsSource is ObservableCollection<PowerEvent> existing)
            {
                foreach (var w in allWarnings.OrderByDescending(w => w.Time))
                    existing.Add(w);
                // Re-sort: the most recent at the top
                var sorted = existing.OrderByDescending(e => e.Time).ToList();
                _grid.ItemsSource = new ObservableCollection<PowerEvent>(sorted);
                _txtStatus.Text = $"Showing {sorted.Count} events + warnings for {day:ddd dd MMM yyyy}.";
            }
            }
            catch (OperationCanceledException) { /* timeout 8s */ }
            catch (Exception ex) { AppLogger.Debug($"RenderDayWarnings: {ex.Message}"); }
        }

        // Computes the real active time of the day (between Boot and the next Shutdown/Sleep)
        private static TimeSpan ComputeActiveTime(List<PowerEvent> evts, DateTime day)
        {
            TimeSpan total = TimeSpan.Zero;
            DateTime? lastBoot = null;
            foreach (var e in evts.OrderBy(x => x.Time))
            {
                if (e.Kind == EventKind.Boot || e.Kind == EventKind.Resume)
                    lastBoot = e.Time;
                else if ((e.Kind == EventKind.Shutdown || e.Kind == EventKind.Sleep ||
                          e.Kind == EventKind.UnexpectedShutdown || e.Kind == EventKind.Crash)
                         && lastBoot.HasValue)
                {
                    total += e.Time - lastBoot.Value;
                    lastBoot = null;
                }
            }
            // PC still on at the end of the day
            if (lastBoot.HasValue)
            {
                var endOfDay = day.Date == DateTime.Today ? DateTime.Now : day.Date.AddDays(1);
                total += endOfDay - lastBoot.Value;
            }
            return total;
        }

        // Draws the continuous 24h bar with coloured blocks
        private void DrawDayTimeline(Canvas cv, List<PowerEvent> evts, DateTime day, double totalW)
        {
            cv.Children.Clear();
            if (totalW < 10) return;

            double h = cv.Height > 0 ? cv.Height : 48;
            double pxPerMin = totalW / (24 * 60);

            // Fundal gri (Offline — PC oprit)
            cv.Children.Add(new Border
            {
                Width = totalW, Height = h, CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(WpfColor.FromArgb(60, 71, 85, 105))
            });

            // Build the state minute by minute
            // State: 0=offline, 1=running, 2=sleep
            var minuteState = new byte[24 * 60]; // default: offline

            int fillLimit = (day.Date == DateTime.Now.Date)
                ? Math.Clamp(DateTime.Now.Hour * 60 + DateTime.Now.Minute + 1, 0, 24 * 60)
                : 24 * 60;

            byte carriedState = 0;
            var lastBeforeToday = _events.Where(e => e.Time.Date < day.Date).OrderByDescending(e => e.Time).FirstOrDefault();
            if (lastBeforeToday != null)
            {
                if (lastBeforeToday.Kind == EventKind.Boot || lastBeforeToday.Kind == EventKind.Resume)
                    carriedState = 1; // running
                else if (lastBeforeToday.Kind == EventKind.Sleep)
                    carriedState = 2; // sleep
                // Shutdown/Logoff/Crash/UnexpectedShutdown => stays 0 (offline), correct.
            }
            if (carriedState != 0)
                for (int i = 0; i < fillLimit && i < minuteState.Length; i++) minuteState[i] = carriedState;

            DateTime? sessionStart = null;

            var ordered = evts.OrderBy(e => e.Time).ToList();
            foreach (var evt in ordered)
            {
                int m = evt.Time.Hour * 60 + evt.Time.Minute;
                m = Math.Clamp(m, 0, 24 * 60 - 1);

                if (evt.Kind == EventKind.Boot)
                {
                    sessionStart = evt.Time;
                    for (int i = m; i < fillLimit; i++) minuteState[i] = 1;
                }
                else if (evt.Kind == EventKind.Resume)
                {
                    if (sessionStart == null) sessionStart = evt.Time;
                    for (int i = m; i < fillLimit; i++) minuteState[i] = 1;
                }
                else if (evt.Kind == EventKind.Sleep)
                {
                    for (int i = m; i < fillLimit; i++) minuteState[i] = 2;
                }
                else if (evt.Kind == EventKind.Shutdown || evt.Kind == EventKind.Logoff)
                {
                    sessionStart = null;
                    for (int i = m; i < fillLimit; i++) minuteState[i] = 0;
                }
                else if (evt.IsBad)
                {
                    sessionStart = null;
                    for (int i = m; i < fillLimit; i++) minuteState[i] = 0;
                }
            }

            // Disegna blocchi contigui dello stesso stato
            WpfColor StateColor(byte s) => s switch
            {
                1 => WpfColor.FromArgb(180, 34, 197, 94),   // green — running
                2 => WpfColor.FromArgb(180, 245, 158, 11),  // giallo — sleep
                _ => WpfColor.FromArgb(0, 0, 0, 0)           // trasparente — offline (usa sfondo)
            };

            int start = 0;
            while (start < minuteState.Length)
            {
                byte state = minuteState[start];
                int end = start + 1;
                while (end < minuteState.Length && minuteState[end] == state) end++;

                if (state != 0) // offline = trasparente, non disegna sopra lo sfondo
                {
                    double x = start * pxPerMin;
                    double w = (end - start) * pxPerMin;
                    var block = new Border
                    {
                        Width = Math.Max(1, w), Height = h,
                        Background = new SolidColorBrush(StateColor(state)),
                        CornerRadius = new CornerRadius(
                            start == 0 ? 6 : 0, end == minuteState.Length ? 6 : 0,
                            end == minuteState.Length ? 6 : 0, start == 0 ? 6 : 0)
                    };
                    Canvas.SetLeft(block, x);
                    Canvas.SetTop(block, 0);
                    cv.Children.Add(block);
                }
                start = end;
            }

            // Vertical lines per hour (every 4 hours)
            for (int hour = 0; hour <= 24; hour += 4)
            {
                double x = hour * 60 * pxPerMin;
                cv.Children.Add(new Border
                {
                    Width = 1, Height = h,
                    Background = new SolidColorBrush(GuideColor(40))
                });
                Canvas.SetLeft(cv.Children[cv.Children.Count - 1] as UIElement ?? new Border(), x);
            }

            // Markers eventi (triangolino in alto)
            foreach (var evt in ordered)
            {
                int m = evt.Time.Hour * 60 + evt.Time.Minute;
                double x = m * pxPerMin;
                WpfColor mc;
                try { mc = (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(evt.KindColor)!; }
                catch { mc = WpfColor.FromRgb(255, 255, 255); }

                // Linea verticale marker
                var marker = new Border
                {
                    Width = 2, Height = h,
                    Background = new SolidColorBrush(WpfColor.FromArgb(200, mc.R, mc.G, mc.B))
                };
                Canvas.SetLeft(marker, x - 1);
                Canvas.SetTop(marker, 0);
                cv.Children.Add(marker);

                // Icona evento
                var icon = new TextBlock
                {
                    Text = evt.KindIcon, FontSize = Ui.FontXS,
                    VerticalAlignment = VerticalAlignment.Top
                };
                Canvas.SetLeft(icon, x - 6);
                Canvas.SetTop(icon, 2);
                cv.Children.Add(icon);
            }

            // A "now" line if the day is today
            if (day.Date == DateTime.Today)
            {
                double nowX = (DateTime.Now.Hour * 60 + DateTime.Now.Minute) * pxPerMin;
                var nowLine = new Border
                {
                    Width = 2, Height = h,
                    Background = new SolidColorBrush(GuideColor(200))
                };
                Canvas.SetLeft(nowLine, nowX - 1);
                Canvas.SetTop(nowLine, 0);
                cv.Children.Add(nowLine);
            }
        }
    }
}
