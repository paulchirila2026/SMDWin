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

namespace SMDWin.Views
{
    /// <summary>
    /// Storage › Overview: the activity chart inside each disk card (13.11.2026) — read and write of that disk only, with the
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
        private FrameworkElement BuildDiskActivityBlock(DiskHealthEntry d, out DiskActivityUi ui)
        {
            ui = new DiskActivityUi();
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 14), MinHeight = 110 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new Grid();
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            ui.Caption = new TextBlock { Text = PT("Activity"), Style = TryFindResource("CompFootKey") as Style, Margin = new Thickness(0, 0, 0, 4) };
            left.Children.Add(ui.Caption);
            var chart = new MiniLineChart { MinHeight = 64, VerticalAlignment = VAlign.Stretch };
            chart.NiceMax = peak => ChartScale.NiceRateMaxKBs(peak, 1024);   // at least 1 MB/s: background noise does not fill the chart
            chart.ScaleText = ChartScale.FormatRateKBs;
            ChartRangeMenu.Attach(chart, StActWindows, StActWindow, SetStActWindow);
            Grid.SetRow(chart, 1);
            left.Children.Add(chart);
            ui.Chart = chart;
            grid.Children.Add(left);

            var values = new StackPanel { VerticalAlignment = VAlign.Center };
            var side = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(16, 0, 0, 0), Margin = new Thickness(16, 0, 0, 0), Width = 210, Child = values };
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

        /// <summary>The disks one under the other, full width (14.11.2026, mockup v2: two per row cut "Healthy", the
        /// temperature and the power-on time). The UniformGrid shares the free height: one disk gets a tall chart, several
        /// disks share it (the page scrolls when the cards need more).</summary>
        private void ApplyDriveRowsColumns()
        {
            if (FindName("StorageDriveRows") is not System.Windows.Controls.Primitives.UniformGrid host) return;
            int n = host.Children.Count;
            int cols = 1;
            host.Columns = cols;
            for (int i = 0; i < n; i++)
                if (host.Children[i] is Border card)
                {
                    card.Margin = cols == 2 ? new Thickness(i % 2 == 0 ? 0 : 6, 0, i % 2 == 0 ? 6 : 0, 12) : new Thickness(0, 0, 0, 12);
                    (card.Tag as Action<bool>)?.Invoke(cols == 2);
                }
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
