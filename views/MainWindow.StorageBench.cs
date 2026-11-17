using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;
using Border     = System.Windows.Controls.Border;
using Brush      = System.Windows.Media.Brush;
using TextBlock  = System.Windows.Controls.TextBlock;
using FontFamily = System.Windows.Media.FontFamily;
using HAlign     = System.Windows.HorizontalAlignment;
using VAlign     = System.Windows.VerticalAlignment;
using UGrid      = System.Windows.Controls.Primitives.UniformGrid;

namespace SMDWin.Views
{
    /// <summary>
    /// Storage → Benchmark, the results: two dials (read, write), the per-pass chart, the test table with block bars and the
    /// typical range for the disk type, a footer with the latencies, the previous run and the difference.
    /// </summary>
    public partial class MainWindow
    {
        private UIElement BuildBenchResultV2(string drive, BenchmarkResult r, DiskBenchHistory.Entry? prev)
        {
            // On large screens the result fills the page: the top row (dials + chart) stretches, the table stays as tall as it
            // needs; on small screens the page scrolls.
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            FillBenchViewport(root);
            try
            {
                // the disk type → the "typical" ranges (what is normal for a disk of this kind)
                string kind = r.IsNvme ? "NVMe SSD" : r.IsUsbAttached || r.IsRemovable ? "USB drive" : r.IsSsd ? "SATA SSD" : "HDD";
                (double seqLo, double seqHi, double q32Lo, double q32Hi, double qd1Lo, double qd1Hi) typ = kind switch
                {
                    "NVMe SSD" => (1500, 7000, 200_000, 1_000_000, 8_000, 20_000),
                    "SATA SSD" => (450, 560, 60_000, 100_000, 5_000, 12_000),
                    "USB drive" => (20, 400, 1_000, 20_000, 300, 5_000),
                    _ => (80, 220, 100, 400, 60, 200),
                };

                // ── row 1: the dials + the per-pass chart ──
                var top = new Grid { Margin = new Thickness(0, 0, 0, 12) };
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star) });
                top.Children.Add(GaugeCard("READ", "Sequential read", r.ReadAvgMBps, r.ReadMaxMBps, 0));
                top.Children.Add(GaugeCard("WRITE", "Sequential write", r.WriteAvgMBps, r.WriteMaxMBps, 2));
                // the chart from the test, kept: the write on the left, the read on the right (the same data as on the live screen)
                var passes = BenchCard("M3 12h4l3-8 4 16 3-8h4", "SPEED DURING THE TEST", "Write first, then read — MB/s over time", "");
                FillSeqChartFromResult(r);
                _seqLive = false;
                var chart = new Canvas { MinHeight = 170, Margin = new Thickness(0, 4, 0, 6) };
                chart.SizeChanged += (_, _) => DrawSeqChart(chart);
                var note = new StackPanel();
                var sum = Small($"write {r.WriteMinMBps:F0}–{r.WriteMaxMBps:F0} · read {r.ReadMinMBps:F0}–{r.ReadMaxMBps:F0} MB/s", 0.8, true);
                var hint = Small("The dashed lines are the averages. If the write line drops, the drive's fast cache filled up.", 0.8, true);
                hint.TextWrapping = TextWrapping.Wrap; hint.TextTrimming = TextTrimming.None;
                note.Children.Add(sum); note.Children.Add(hint);
                BenchCardFill(passes, chart, note);
                Grid.SetColumn(passes, 4); top.Children.Add(passes);
                root.Children.Add(top);

                // ── row 2: the results ──
                bool noData = r.ReadAvgMBps <= 0 && r.WriteAvgMBps <= 0;
                // the typical range is for reading with a queue (Q8); a simple read (QD1) is slower by nature,
                // so the verdict uses the best sequential result
                double bestSeq = Math.Max(r.ReadAvgMBps, r.SeqReadQ8MBps);
                string verdict = noData ? "No result — the test could not measure the drive"
                               : bestSeq >= typ.seqLo * 0.9 ? $"As expected for a {kind}" : bestSeq >= typ.seqLo * 0.6 ? $"A bit slow for a {kind}" : $"Slow for a {kind}";
                string vKey = noData ? "StatusWarnBrush"
                            : bestSeq >= typ.seqLo * 0.9 ? "StatusOkBrush" : bestSeq >= typ.seqLo * 0.6 ? "StatusWarnBrush" : "StatusCritBrush";
                var res = BenchCard("M4 6h16 M4 12h16 M4 18h10", "RESULTS",
                    $"{drive} · {DateFormatHelper.FormatDateTime(DateTime.Now)} · {(r.IsQuickMode ? "quick test" : "full test")}", verdict, vKey);
                var sp = (StackPanel)res.Child;
                sp.Children.Add(ResultHeader());
                string Mb(double v) => v > 0 ? $"{v:F0} MB/s" : "—";
                string Io(long v) => v <= 0 ? "—" : v >= 1000 ? $"{v / 1000.0:F1} K" : v.ToString();
                sp.Children.Add(ResultRow("Sequential", Mb(r.ReadAvgMBps), Mb(r.WriteAvgMBps), r.ReadAvgMBps / typ.seqHi, r.WriteAvgMBps / typ.seqHi, r.SeqReadQ8MBps > 0 ? "" : $"{typ.seqLo:F0}–{typ.seqHi:F0} MB/s"));
                if (r.SeqReadQ8MBps > 0)
                    sp.Children.Add(ResultRow("Sequential · Q8", Mb(r.SeqReadQ8MBps), Mb(r.SeqWriteQ8MBps), r.SeqReadQ8MBps / typ.seqHi, r.SeqWriteQ8MBps / typ.seqHi, $"{typ.seqLo:F0}–{typ.seqHi:F0} MB/s"));
                if (r.Rand4kReadQ32Iops > 0)
                    sp.Children.Add(ResultRow("Random 4K · Q32", Io(r.Rand4kReadQ32Iops), Io(r.Rand4kWriteQ32Iops), r.Rand4kReadQ32Iops / typ.q32Hi, r.Rand4kWriteQ32Iops / typ.q32Hi, $"{Io((long)typ.q32Lo)}–{Io((long)typ.q32Hi)} IOPS"));
                if (r.ReadIOPS > 0)
                    sp.Children.Add(ResultRow("Random 4K · QD1", Io(r.ReadIOPS), Io(r.WriteIOPS), r.ReadIOPS / typ.qd1Hi, r.WriteIOPS / typ.qd1Hi, $"{Io((long)typ.qd1Lo)}–{Io((long)typ.qd1Hi)} IOPS"));
                foreach (var w in new[] { r.CacheWarning, r.WriteWarning })
                    if (!string.IsNullOrWhiteSpace(w)) { var t = Small("⚠ " + w, 1); t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(0, 8, 0, 0); t.SetResourceReference(TextBlock.ForegroundProperty, "StatusWarnBrush"); sp.Children.Add(t); }

                // footer: latencies · previous run · difference · rating
                string lat = r.LatencyP50Us > 0 ? $"{r.LatencyP50Us / 1000:F2} / {r.LatencyP95Us / 1000:F2} / {r.LatencyP99Us / 1000:F2} ms" : "—";
                string prevTxt = prev != null ? $"{prev.Date:dd.MM} · {prev.SeqReadMBps:F0} / {prev.SeqWriteMBps:F0} MB/s" : "first run";
                string change = "—"; string chKey = "";
                if (prev != null && prev.SeqReadMBps > 0)
                {
                    double d = (r.ReadAvgMBps - prev.SeqReadMBps) / prev.SeqReadMBps * 100;
                    change = Math.Abs(d) < 5 ? "same speed" : d > 0 ? $"+{d:F0}% faster" : $"{d:F0}% slower";
                    chKey = Math.Abs(d) < 5 || d > 0 ? "StatusOkBrush" : d > -15 ? "StatusWarnBrush" : "StatusCritBrush";
                }
                var foot = new Border { Style = TryFindResource("CompFootBand") as Style, Margin = new Thickness(-18, 12, -18, 0) };
                var fg = new UGrid { Rows = 1 };
                // the typical response time (P50); P95 / P99 stay in the tooltip
                string latShort = r.LatencyP50Us > 0 ? $"{r.LatencyP50Us / 1000:F2} ms typical" : "—";
                foreach (var (k, v, key, first) in new[] { ("Response time", latShort, "", true), ("Previous run", prevTxt, "", false), ("Change", change, chKey, false), ("Rating", SMDWin.Views.Net.NetWindowHelper.Clean(r.Rating), "", false) })   // no emoji: it renders as an empty box in the app font
                {
                    var cb = new Border { Style = TryFindResource(first ? "CompFootCellFirst" : "CompFootCell") as Style };
                    var cs = new StackPanel();
                    cs.Children.Add(new TextBlock { Text = k, Style = TryFindResource("CompFootKey") as Style });
                    var vt = new TextBlock { Text = string.IsNullOrWhiteSpace(v) ? "—" : v, Style = TryFindResource("CompFootVal") as Style };
                    if (first && lat != "—") vt.ToolTip = $"P50 / P95 / P99: {lat}";
                    if (key != "") vt.SetResourceReference(TextBlock.ForegroundProperty, key);
                    cs.Children.Add(vt); cb.Child = cs; fg.Children.Add(cb);
                }
                foot.Child = fg;
                sp.Children.Add(foot);
                res.Padding = new Thickness(18, 14, 18, 0);
                Grid.SetRow(res, 1);
                root.Children.Add(res);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "BuildBenchResultV2"); }
            return root;

            Border GaugeCard(string cap, string name, double avg, double max, int col)
            {
                var c = BenchCard("M13 2L4 14h7l-2 8 11-13h-7z", cap, name, "");
                var g = new SegmentedGauge { MinWidth = 210, MinHeight = 180, MaxHeight = 360, Stops = DiskGaugeStops, Unit = "MB/s", Margin = new Thickness(0, 4, 0, 4) };
                g.Value = max; g.Value = avg;   // the peak stays marked, the value shown = the average
                BenchCardFill(c, g, Small(max > 0 ? "average of all passes" : "no data", 0.8, true));
                Grid.SetColumn(c, col);
                return c;
            }
        }

        /// <summary>If no points arrived from the live stream for the write or read, the chart is filled from the result's
        /// per-pass averages — a half stays empty only when the test really measured nothing.</summary>
        private void FillSeqChartFromResult(BenchmarkResult r)
        {
            static void Fill(double[] sum, int[] cnt, List<double> passes)
            {
                if (passes.Count == 0 || cnt.Any(c => c > 0)) return;
                for (int i = 0; i < sum.Length; i++)
                {
                    sum[i] = passes[Math.Min(passes.Count - 1, i * passes.Count / sum.Length)];
                    cnt[i] = 1;
                }
            }
            Fill(_seqWriteSum, _seqWriteCnt, r.WritePassMBps);
            Fill(_seqReadSum, _seqReadCnt, r.ReadPassMBps);
        }

        /// <summary>Moves the card content into a DockPanel: the header at the top, the note at the bottom, <paramref name="fill"/>
        /// takes the rest of the height — the dial / chart grow with the top row.</summary>
        private static void BenchCardFill(Border card, FrameworkElement fill, FrameworkElement note)
        {
            if (card.Child is not StackPanel sp) return;
            var dock = new DockPanel { LastChildFill = true };
            var kids = sp.Children.Cast<UIElement>().ToList();
            sp.Children.Clear();
            foreach (var k in kids) { DockPanel.SetDock(k, Dock.Top); dock.Children.Add(k); }
            DockPanel.SetDock(note, Dock.Bottom); dock.Children.Add(note);
            dock.Children.Add(fill);
            card.Child = dock;
        }

        /// <summary>The topmost element in Benchmark is at least as tall as the visible page height.</summary>
        private void FillBenchViewport(FrameworkElement fe)
        {
            if (FindName("BenchResultsScroll") is not ScrollViewer sv) return;
            fe.SetBinding(FrameworkElement.MinHeightProperty, new System.Windows.Data.Binding(nameof(ScrollViewer.ViewportHeight))
            {
                Source = sv, Converter = BenchViewportMinus.Instance
            });
        }

        private sealed class BenchViewportMinus : System.Windows.Data.IValueConverter
        {
            public static readonly BenchViewportMinus Instance = new();
            public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c)
                => value is double d && d > 8 ? d - 8 : 0.0;
            public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c)
                => System.Windows.Data.Binding.DoNothing;
        }

        private Border BenchCard(string icon, string cap, string name, string status, string statusKey = "StatusOkBrush")
        {
            var card = new Border { Style = TryFindResource("CompCardStyle") as Style, Cursor = System.Windows.Input.Cursors.Arrow, Padding = new Thickness(18, 14, 18, 14) };
            var sp = new StackPanel();
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = true };
            if (status.Length > 0)
            {
                var st = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VAlign.Center };
                var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VAlign.Center };
                dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, statusKey);
                var tx = new TextBlock { Text = status, Style = TryFindResource("CompStatusText") as Style };
                tx.SetResourceReference(TextBlock.ForegroundProperty, statusKey);
                st.Children.Add(dot); st.Children.Add(tx);
                DockPanel.SetDock(st, Dock.Right); head.Children.Add(st);
            }
            var left = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            var path = new System.Windows.Shapes.Path { Width = 24, Height = 24 };
            SMDWin.Services.FluentIcons.SetIcon(path, icon);
            left.Children.Add(new Viewbox { Width = 19, Height = 19, Child = path, Margin = new Thickness(0, 0, 11, 0), VerticalAlignment = VAlign.Center });
            // a single-line header: the title + the detail next to it ("Results  D: · 12 Oct 2026 12:34")
            var names = new StackPanel { VerticalAlignment = VAlign.Center, Orientation = System.Windows.Controls.Orientation.Horizontal };
            names.Children.Add(new TextBlock { Text = SMDWin.Services.DisplayFormat.SentenceCase(cap), Style = TryFindResource("DashRowName") as Style });
            if (!string.IsNullOrWhiteSpace(name))
                names.Children.Add(new TextBlock { Text = name, Style = TryFindResource("DashRowInlineSub") as Style });
            left.Children.Add(names);
            head.Children.Add(left);
            sp.Children.Add(head);
            card.Child = sp;
            return card;
        }

        private static readonly GridLength[] BenchCols =
        {
            new(170), new(1, GridUnitType.Star), new(96), new(1, GridUnitType.Star), new(96), new(170)
        };

        private Grid ResultHeader()
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            foreach (var w in BenchCols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            string[] h = { "Test", "Read", "", "Write", "", "Typical for this type" };
            for (int i = 0; i < h.Length; i++)
            {
                var t = Small(h[i], 0.75); if (i == 5) t.HorizontalAlignment = HAlign.Right;
                Grid.SetColumn(t, i); g.Children.Add(t);
            }
            return g;
        }

        private Border ResultRow(string name, string rv, string wv, double rf, double wf, string typical)
        {
            var g = new Grid();
            foreach (var w in BenchCols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            var n = new TextBlock { Text = name, FontSize = Ui.FontM, FontWeight = FontWeights.SemiBold, VerticalAlignment = VAlign.Center };
            n.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            g.Children.Add(n);
            void Bar(double f, int col, string key)
            {
                var b = new UGrid { Rows = 1, Height = 8, VerticalAlignment = VAlign.Center, Margin = new Thickness(0, 0, 12, 0) };
                int lit = f <= 0 ? 0 : Math.Clamp((int)Math.Round(Math.Min(1, f) * 24), 1, 24);
                b.Columns = 24;
                for (int i = 0; i < 24; i++)
                {
                    var x = new Border { CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, i < 23 ? 2 : 0, 0) };
                    x.SetResourceReference(Border.BackgroundProperty, i < lit ? key : "BorderBrush2");
                    b.Children.Add(x);
                }
                Grid.SetColumn(b, col); g.Children.Add(b);
            }
            void Val(string v, int col)
            {
                var t = new TextBlock { Text = v, FontFamily = TryFindResource("NumFont") as FontFamily, FontSize = 16, FontWeight = FontWeights.SemiBold,
                                        HorizontalAlignment = HAlign.Right, VerticalAlignment = VAlign.Center, Margin = new Thickness(0, 0, 16, 0) };
                t.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                Grid.SetColumn(t, col); g.Children.Add(t);
            }
            Bar(rf, 1, "AccentBrush"); Val(rv, 2); Bar(wf, 3, "TextSecondaryBrush"); Val(wv, 4);
            var ty = Small(typical, 0.75); ty.HorizontalAlignment = HAlign.Right; ty.VerticalAlignment = VAlign.Center;
            Grid.SetColumn(ty, 5); g.Children.Add(ty);
            var wrap = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 9, 0, 9), Child = g };
            wrap.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            return wrap;
        }

        private static TextBlock Small(string text, double opacity, bool center = false)
        {
            var t = new TextBlock { Text = text, FontSize = Ui.FontS, Opacity = opacity, TextTrimming = TextTrimming.CharacterEllipsis };
            if (center) t.HorizontalAlignment = HAlign.Center;
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            return t;
        }
    }
}
