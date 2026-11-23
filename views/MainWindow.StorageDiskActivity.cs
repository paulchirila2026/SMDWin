using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SMDWin.Models;
using SMDWin.Services;
using Border      = System.Windows.Controls.Border;
using TextBlock   = System.Windows.Controls.TextBlock;
using VAlign      = System.Windows.VerticalAlignment;
using HAlign      = System.Windows.HorizontalAlignment;
using UniformGrid = System.Windows.Controls.Primitives.UniformGrid;

namespace SMDWin.Views
{
    /// <summary>
    /// Storage › Overview: the activity chart inside each disk card — read and write of that disk only, with the
    /// response time, how busy the disk is and the peak, over the same time window as the "All drives" chart above
    /// (60 s / 5 min / 15 min / 1 h). An idle disk keeps its chart with the line at zero: it shows that the disk sits idle.
    /// A disk Windows has no counter for (some USB sticks) keeps the card as before, without an empty chart.
    /// </summary>
    public partial class MainWindow
    {
        private readonly PerDiskActivityReader _perDiskReader = new();
        /// <summary>true once the Storage page has built its disk cards: until then nothing is read (no cost for users who
        /// never open the page). From then on the history is kept, like the "All drives" chart, for the 1 h window.</summary>
        private volatile bool _perDiskOn;
        private Dictionary<int, PerDiskActivityReader.Sample>? _perDiskPending;
        // series: 0 read KB/s · 1 write KB/s · 2 response time ms · 3 busy %
        private readonly Dictionary<int, SecondHistory> _perDiskHist = new();
        private readonly Dictionary<int, DiskActivityUi> _perDiskUi = new();

        private sealed class DiskActivityUi
        {
            public MiniLineChart Chart = null!;
            public TextBlock Read = null!, Write = null!, Latency = null!, Busy = null!, Peak = null!, Caption = null!;
            public ColumnDefinition BusyOn = null!, BusyOff = null!;
            public FrameworkElement PeakRow = null!;
            public Action<bool> ShowChart = _ => { };
            /// <summary>Tight (two disks must fit the screen): the values on two columns, no busy bar and no peak row.</summary>
            public Action<bool> SetTight = _ => { };
            public bool Hidden;
        }

        /// <summary>Background thread (OnTempTick, next to GetSystemDiskIO): one read of all disks.</summary>
        private void SamplePerDiskActivity()
        {
            if (_perDiskOn) _perDiskPending = _perDiskReader.Read();
        }

        /// <summary>UI thread, after the read: into the history of each disk, then the cards (only when they can be seen).
        /// <paramref name="steps"/> = 0 while the charts are paused: the time stays empty, as on the other charts.</summary>
        private void PushPerDiskActivity(int steps)
        {
            var samples = _perDiskPending;
            _perDiskPending = null;
            if (samples == null) return;
            var now = DateTime.UtcNow;
            foreach (var (index, s) in samples)
            {
                if (!_perDiskHist.TryGetValue(index, out var h)) _perDiskHist[index] = h = new SecondHistory(4);
                h.Push(now, new[] { s.ReadKBs, s.WriteKBs, s.LatencyMs, s.BusyPct }, gap: steps <= 0, maxInterpolated: ChartInterpolateMaxSlots);
            }
            if (_uiVisibleCached && _currentPanel == "Disk") UpdatePerDiskCards();
        }

        private static string WindowName(int seconds) => seconds switch
        {
            <= 60 => "60 s",
            < 3600 => $"{seconds / 60} min",
            _ => "1 h",
        };

        /// <summary>Redraws the chart and the values of every disk card (also called when the time window changes).</summary>
        private void UpdatePerDiskCards()
        {
            if (_perDiskUi.Count == 0) return;
            if (FindName("StorageTabOverview") is FrameworkElement overview && !overview.IsVisible) return;
            int window = StActWindow();
            foreach (var (index, ui) in _perDiskUi)
            {
                if (!_perDiskHist.TryGetValue(index, out var h))
                {
                    // no counter for this disk after a few reads (or no counters at all): the card goes back to its old layout
                    bool missing = !_perDiskReader.IsAvailable || (_perDiskReader.GoodReads >= 3 && !_perDiskReader.KnownDisks.Contains(index));
                    if (missing && !ui.Hidden) { ui.Hidden = true; ui.ShowChart(false); }
                    continue;
                }
                if (ui.Hidden) { ui.Hidden = false; ui.ShowChart(true); }

                ui.Chart.ShowHistory(h, window, 0, 1);
                float r = h.Last(0), w = h.Last(1), lat = h.Last(2), busy = h.Last(3);
                ui.Read.Text = FormatKBps(float.IsFinite(r) ? r : 0f);
                ui.Write.Text = FormatKBps(float.IsFinite(w) ? w : 0f);
                // no transfers = no response time to show (the counter reads 0)
                ui.Latency.Text = float.IsFinite(lat) && lat > 0 ? (lat < 1 ? "<1 ms" : $"{lat:F1} ms") : PT("idle");
                if (float.IsFinite(busy))
                {
                    ui.Busy.Text = $"{busy:F0} %";
                    ui.BusyOn.Width = new GridLength(Math.Max(busy, 0.5), GridUnitType.Star);
                    ui.BusyOff.Width = new GridLength(Math.Max(100 - busy, 0), GridUnitType.Star);
                }
                else ui.Busy.Text = "—";

                double peak = 0;
                foreach (var v in h.Window(0, window)) if (double.IsFinite(v) && v > peak) peak = v;
                foreach (var v in h.Window(1, window)) if (double.IsFinite(v) && v > peak) peak = v;
                ui.Peak.Text = FormatKBps((float)peak);
                ui.Caption.Text = PT("Activity") + " · " + PT("last {0}", WindowName(window));
            }
        }

        /// <summary>The activity block of one disk card: the chart on the left, the values on the right (as in the "All drives"
        /// card). <paramref name="compact"/> = the card is half width (2 disks per row): a narrower value column, no peak row.</summary>
        /// <summary>The height of the activity chart on every disk card.</summary>
        private const double DiskChartHeight = 96;

        private FrameworkElement BuildDiskActivityBlock(DiskHealthEntry d, out DiskActivityUi ui)
        {
            ui = new DiskActivityUi();
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new Grid();
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            ui.Caption = new TextBlock { Text = PT("Activity"), Style = TryFindResource("CompFootKey") as Style, Margin = new Thickness(0, 0, 0, 4) };
            left.Children.Add(ui.Caption);
            var chart = new MiniLineChart { Height = DiskChartHeight, VerticalAlignment = VAlign.Top };   // the same height on every disk card
            chart.NiceMax = peak => ChartScale.NiceRateMaxKBs(peak, 1024);   // at least 1 MB/s: background noise does not fill the chart
            chart.ScaleText = ChartScale.FormatRateKBs;
            ChartRangeMenu.Attach(chart, StActWindows, StActWindow, SetStActWindow);
            Grid.SetRow(chart, 1);
            left.Children.Add(chart);
            ui.Chart = chart;
            grid.Children.Add(left);

            var values = new StackPanel { VerticalAlignment = VAlign.Center };
            // tight layout: the four main values on two columns (the rows are moved between the two panels)
            var valuesTight = new UniformGrid { Columns = 2, VerticalAlignment = VAlign.Center, Visibility = Visibility.Collapsed };
            var valuesHost = new Grid();
            valuesHost.Children.Add(values); valuesHost.Children.Add(valuesTight);
            var side = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(16, 0, 0, 0), Margin = new Thickness(16, 0, 0, 0), Width = 210, Child = valuesHost };
            side.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            Grid.SetColumn(side, 1);
            grid.Children.Add(side);

            TextBlock Row(string label, double size, string? brush, out DockPanel row)
            {
                row = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
                var val = new TextBlock { Text = "—", Style = TryFindResource("CompChartValue") as Style, FontSize = size };
                if (brush != null) val.SetResourceReference(TextBlock.ForegroundProperty, brush);
                var key = new TextBlock { Text = PT(label), FontSize = Ui.FontS, VerticalAlignment = VAlign.Bottom, TextTrimming = TextTrimming.CharacterEllipsis };
                key.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                row.Children.Add(val);   // docked right by the style
                row.Children.Add(key);
                values.Children.Add(row);
                return val;
            }
            ui.Read = Row("Read", 17, "DataBrush", out _);
            ui.Write = Row("Write", 17, null, out _);
            ui.Latency = Row("Response time", 14, null, out _);
            ui.Busy = Row("Busy", 14, null, out var busyRow);
            busyRow.ToolTip = PT("How much of the time the disk was working");
            var bar = new Grid { Height = 6, Margin = new Thickness(0, 0, 0, 7) };
            ui.BusyOn = new ColumnDefinition { Width = new GridLength(0.5, GridUnitType.Star) };
            ui.BusyOff = new ColumnDefinition { Width = new GridLength(99.5, GridUnitType.Star) };
            bar.ColumnDefinitions.Add(ui.BusyOn); bar.ColumnDefinitions.Add(ui.BusyOff);
            var track = new Border { CornerRadius = new CornerRadius(3) };
            track.SetResourceReference(Border.BackgroundProperty, "BorderBrush2");
            Grid.SetColumnSpan(track, 2);
            var fill = new Border { CornerRadius = new CornerRadius(3) };
            fill.SetResourceReference(Border.BackgroundProperty, "DataBrush");
            bar.Children.Add(track); bar.Children.Add(fill);
            values.Children.Add(bar);
            ui.Peak = Row("Peak", 13, null, out var peakRow);
            ui.PeakRow = peakRow;
            peakRow.ToolTip = PT("The highest read or write speed in the chosen time window");

            var mainRows = values.Children.OfType<DockPanel>().Take(4).ToList();   // read, write, response time, busy
            object? rangeTip = chart.ToolTip;
            bool tightNow = false;
            ui.SetTight = tight =>
            {
                if (tight == tightNow) return;
                tightNow = tight;
                foreach (var r in mainRows) { ((Panel)r.Parent).Children.Remove(r); }
                for (int i = 0; i < mainRows.Count; i++)
                {
                    var r = mainRows[i];
                    r.Margin = tight ? new Thickness(0, 0, i % 2 == 0 ? 18 : 0, 4) : new Thickness(0, 0, 0, 5);
                    if (tight) valuesTight.Children.Add(r); else values.Children.Insert(i, r);
                }
                values.Visibility = tight ? Visibility.Collapsed : Visibility.Visible;
                valuesTight.Visibility = tight ? Visibility.Visible : Visibility.Collapsed;
                side.Width = tight ? 330 : 210;
                grid.Margin = new Thickness(0, 0, 0, tight ? 8 : 14);
                // the peak stays in the chart's tooltip, above the time-range hint
                chart.ToolTip = tight ? PeakTip() : rangeTip;
            };
            string PeakTip() => PT("Peak") + ": " + ui.Peak.Text + (rangeTip is string rt ? "\n" + rt : "");
            chart.ToolTipOpening += (_, _) => { if (tightNow) chart.ToolTip = PeakTip(); };
            return grid;
        }

        /// <summary>The half-width card (2 per row): a narrower value column, without the peak row.</summary>
        private static void SetDiskActivityCompact(FrameworkElement block, DiskActivityUi ui, bool compact)
        {
            if (block is Grid g && g.Children.OfType<Border>().FirstOrDefault() is Border side)
            {
                side.Width = compact ? 172 : 210;
                side.Margin = new Thickness(compact ? 12 : 16, 0, 0, 0);
                side.Padding = new Thickness(compact ? 12 : 16, 0, 0, 0);
            }
            ui.PeakRow.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>The disks one under the other, full width (mockup v2: two per row cut "Healthy", the
        /// temperature and the power-on time). Each card is as tall as its content (the charts have one fixed height).</summary>
        private void ApplyDriveRowsColumns()
        {
            if (FindName("StorageDriveRows") is not System.Windows.Controls.Panel host) return;
            int n = host.Children.Count;
            int cols = 1;
            for (int i = 0; i < n; i++)
                if (host.Children[i] is Border card)
                {
                    card.Margin = cols == 2 ? new Thickness(i % 2 == 0 ? 0 : 6, 0, i % 2 == 0 ? 6 : 0, 12) : new Thickness(0, 0, 0, 12);
                    (card.Tag as Action<bool>)?.Invoke(cols == 2);
                }
        }

        // ── vertical fit: the drive cards share the visible height ─────────────────────────────────────────────
        /// <summary>What the fit pass changes on one card.</summary>
        private sealed class DriveCardFit
        {
            public Border Card = null!;
            public MiniLineChart Chart = null!;
            public Action<bool> SetTight = _ => { };
            public Func<bool> ChartShown = () => true;
        }
        private readonly List<DriveCardFit> _driveFits = new();
        private bool _driveFitPending, _driveFitHooked;

        /// <summary>
        /// The cards fill the visible height without scrolling when they can: one disk — its chart grows into the free space;
        /// two or more — the first two share the height (normal layout with a taller chart when it fits, otherwise the tight
        /// layout: smaller picture, values on two columns, a chart between 48 and 96 px); further disks scroll.
        /// </summary>
        private void QueueDriveFit()
        {
            if (_driveFitPending) return;
            _driveFitPending = true;
            Dispatcher.BeginInvoke(new Action(() => { _driveFitPending = false; FitDriveCards(); }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void HookDriveFit()
        {
            if (_driveFitHooked || FindName("StorageTabOverview") is not ScrollViewer sv) return;
            _driveFitHooked = true;
            // the viewport changes size with the window only (not with the content), so this cannot loop
            sv.SizeChanged += (_, e) => { if (e.HeightChanged || e.WidthChanged) QueueDriveFit(); };
            sv.IsVisibleChanged += (_, _) => { if (sv.IsVisible) QueueDriveFit(); };
        }

        private void FitDriveCards()
        {
            try
            {
                if (_driveFits.Count == 0 || FindName("StorageTabOverview") is not ScrollViewer sv || !sv.IsVisible) return;
                if (FindName("StorageDriveRows") is not FrameworkElement host || host.Parent is not Grid page) return;
                var shown = _driveFits.Where(f => f.Card.Visibility == Visibility.Visible && f.ChartShown()).ToList();
                if (shown.Count == 0) return;
                double above = page.Children.OfType<FrameworkElement>().Where(c => Grid.GetRow(c) == 0).Sum(c => c.ActualHeight);
                double avail = sv.ViewportHeight - above - 4;
                if (avail < 200) return;
                int k = Math.Min(2, shown.Count);
                double target = avail / k;   // a card's measured height includes the space under it
                double width = host.ActualWidth > 0 ? host.ActualWidth : sv.ViewportWidth;

                // the card height without its chart, for the first k cards (the tallest decides, so the charts stay equal)
                double Base(bool tight)
                {
                    double b = 0;
                    foreach (var f in shown.Take(k))
                    {
                        f.SetTight(tight);
                        f.Card.Measure(new Size(width, double.PositiveInfinity));
                        b = Math.Max(b, f.Card.DesiredSize.Height - f.Chart.Height);
                    }
                    return b;
                }
                bool tightMode = false;
                double baseH = Base(false), chartH;
                if (shown.Count == 1 || baseH + 96 <= target)
                    chartH = Math.Clamp(target - baseH, 96, shown.Count == 1 ? 340 : 220);
                else
                {
                    tightMode = true;
                    baseH = Base(true);
                    chartH = Math.Clamp(target - baseH, 48, 96);
                }
                foreach (var f in _driveFits)
                {
                    f.SetTight(tightMode);
                    if (Math.Abs(f.Chart.Height - chartH) > 1) f.Chart.Height = Math.Round(chartH);
                }
            }
            catch (Exception ex) { AppLogger.Debug($"FitDriveCards: {ex.Message}"); }
        }

        private bool _driveRowsSizeHooked;

        private void HookDriveRowsResize()
        {
            if (_driveRowsSizeHooked || FindName("StorageDriveRows") is not FrameworkElement host) return;
            _driveRowsSizeHooked = true;
            host.SizeChanged += (_, e) =>
            {
                // only when the width crosses the limit (the height changes with every card)
                if (e.WidthChanged && (e.PreviousSize.Width >= 1050) != (e.NewSize.Width >= 1050)) ApplyDriveRowsColumns();
            };
        }
    }
}
