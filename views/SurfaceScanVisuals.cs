using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush  = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color  = System.Windows.Media.Color;
using Pen    = System.Windows.Media.Pen;
using Point  = System.Windows.Point;
using Size   = System.Windows.Size;
using FontFamily = System.Windows.Media.FontFamily;

namespace SMDWin.Views
{
    /// <summary>
    /// The "block" sector map for Surface Scan: the disk is split into a few hundred squares (each = an equal piece of the
    /// disk), drawn with space between them, coloured as the scan advances. Replaces the stretched bitmap (Stretch=Fill),
    /// where thousands of 1px cells merged into a thin strip and the rest of the card stayed grey.
    ///
    /// Severities (the same codes as in the scan): 0 = not scanned, 1 = OK, 2-4 = slow, 5 = bad.
    /// The current cell (the scan position) is drawn with the accent; the cell selected in the problem list gets an outline.
    /// </summary>
    public sealed class SectorBlockMap : FrameworkElement
    {
        private byte[] _severity = Array.Empty<byte>();
        private int _cols = 36;

        /// <summary>The index of the cell being scanned now (-1 = none).</summary>
        public int CurrentCell { get; set; } = -1;

        /// <summary>The highlighted cell (click in the problem list), -1 = none.</summary>
        public int HighlightCell { get; set; } = -1;

        public int CellCount => _severity.Length;

        /// <summary>The target size of a square (px) when choosing the number of cells.</summary>
        private const double TargetCellPx = 15;

        /// <summary>
        /// Chooses the number of cells by the available space (~15px squares), so the map fills the card. The number stays fixed
        /// for the duration of the scan; on resize it only scales.
        /// </summary>
        public int ChooseCellCount()
        {
            double w = ActualWidth  > 40 ? ActualWidth  : 900;
            double h = ActualHeight > 40 ? ActualHeight : 320;
            _cols = Math.Clamp((int)(w / TargetCellPx), 16, 120);
            int rows = Math.Clamp((int)(h / TargetCellPx), 6, 60);
            return _cols * rows;
        }

        /// <summary>Binds the map to the scan's severity array (the same object, not a copy).</summary>
        public void Attach(byte[] severity)
        {
            _severity = severity ?? Array.Empty<byte>();
            CurrentCell = -1;
            HighlightCell = -1;
            InvalidateVisual();
        }

        public void Reset()
        {
            _severity = Array.Empty<byte>();
            CurrentCell = -1;
            HighlightCell = -1;
            InvalidateVisual();
        }

        private void Layout(out int cols, out int rows, out double cell, out double ox, out double oy)
        {
            int n = Math.Max(1, _severity.Length);
            double w = Math.Max(1, ActualWidth), h = Math.Max(1, ActualHeight);
            // the number of columns is chosen on every draw by the current card size (the largest square that fits): the number of
            // cells is fixed at the start of the scan, and the card may widen afterwards
            cols = Math.Max(1, Math.Min(_cols, n));
            // only column counts that divide the number of cells exactly, so the last row is always complete (no missing squares
            // at the bottom right); the fallback below is for an array whose length has no usable divisor
            double best = 0;
            for (int pass = 0; pass < 2 && best <= 0; pass++)
            {
                for (int c = 4; c <= Math.Min(n, 400); c++)
                {
                    if (pass == 0 && n % c != 0) continue;
                    int r = (int)Math.Ceiling(n / (double)c);
                    double sz = Math.Min(w / c, h / r);
                    if (sz > best + 0.01) { best = sz; cols = c; }
                }
            }
            rows = (int)Math.Ceiling(n / (double)cols);
            cell = Math.Max(3, Math.Floor(Math.Min(w / cols, h / rows)));
            ox = Math.Floor((w - cell * cols) / 2);
            oy = 0;
        }

        /// <summary>The cell under the mouse, or -1.</summary>
        public int CellAt(Point p)
        {
            if (_severity.Length == 0) return -1;
            Layout(out int cols, out int rows, out double cell, out double ox, out double oy);
            int c = (int)((p.X - ox) / cell), r = (int)((p.Y - oy) / cell);
            if (c < 0 || c >= cols || r < 0 || r >= rows) return -1;
            int idx = r * cols + c;
            return idx < _severity.Length ? idx : -1;
        }

        private Brush Res(string key, Color fallback)
            => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

        protected override void OnRender(DrawingContext dc)
        {
            // Transparent background, but hit-testable (for the tooltip over the whole surface)
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            if (_severity.Length == 0) return;

            Layout(out int cols, out _, out double cell, out double ox, out double oy);
            double gap = cell >= 10 ? 2 : 1;
            double radius = cell >= 10 ? 2.5 : 1;
            double size = cell - gap;

            var untouched = Res("BorderBrush2", Color.FromRgb(225, 228, 233));
            var ok        = Res("StatusOkBrush", Color.FromRgb(34, 197, 94));
            var slow      = Res("YellowBrush",   Color.FromRgb(217, 119, 6));
            var bad       = Res("RedBrush",      Color.FromRgb(239, 68, 68));
            var accent    = Res("AccentBrush",   Color.FromRgb(59, 130, 246));

            for (int i = 0; i < _severity.Length; i++)
            {
                byte s = _severity[i];
                Brush b = s switch
                {
                    0 => untouched,
                    1 => ok,
                    5 => bad,
                    _ => slow,
                };
                if (i == CurrentCell && s == 0) b = accent;
                double x = ox + (i % cols) * cell, y = oy + (i / cols) * cell;
                dc.DrawRoundedRectangle(b, null, new Rect(x, y, size, size), radius, radius);
            }

            if (HighlightCell >= 0 && HighlightCell < _severity.Length)
            {
                double x = ox + (HighlightCell % cols) * cell, y = oy + (HighlightCell / cols) * cell;
                var pen = new Pen(TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.Black, 2);
                dc.DrawRoundedRectangle(null, pen, new Rect(x - 2, y - 2, size + 4, size + 4), radius + 1, radius + 1);
            }
        }
    }

    /// <summary>
    /// The "speed across the disk surface" chart: the average read speed (MB/s) by position on the disk. The data is kept in
    /// 8192 equal intervals of the disk; when drawing they are grouped by the pixel width. The X axis shows only the
    /// ViewStart..ViewEnd window of the disk: during the scan, the area read in the last minutes (a fixed width, the chart
    /// "flows" left), so the whole curve does not bunch up as the scan advances. At the end: the whole disk.
    /// The slow/bad areas are marked with short lines on the bottom edge; the dotted line = the current position.
    /// </summary>
    public sealed class SpeedCurveChart : FrameworkElement
    {
        public const int Bins = 8192;

        private double[] _sum = new double[Bins];
        private int[]    _count = new int[Bins];
        private readonly List<(double pos, bool bad)> _marks = new();

        /// <summary>The current scan position, 0..1 (negative = hidden).</summary>
        public double CurrentPos { get; set; } = -1;

        /// <summary>The right end of the X axis, as a fraction of the disk (0..1]. 1 = the whole disk.</summary>
        public double ViewEnd { get; set; } = 1;

        /// <summary>The left end of the X axis, as a fraction of the disk [0..ViewEnd).</summary>
        public double ViewStart { get; set; }

        private (double s, double e) View()
        {
            double e = Math.Clamp(ViewEnd, 1.0 / Bins, 1);
            double s = Math.Clamp(ViewStart, 0, e - 1.0 / Bins);
            return (s, e);
        }

        /// <summary>The disk capacity (bytes) — for the labels and the tooltip.</summary>
        public double TotalBytes { get; set; }

        public void Reset()
        {
            _sum = new double[Bins];
            _count = new int[Bins];
            _marks.Clear();
            CurrentPos = -1;
            ViewEnd = 1;
            ViewStart = 0;
            InvalidateVisual();
        }

        /// <summary>Copies the snapshot of the intervals (called on the UI thread, once per second).</summary>
        public void SetData(double[] sum, int[] count, IEnumerable<(double pos, bool bad)> marks)
        {
            Array.Copy(sum, _sum, Bins);
            Array.Copy(count, _count, Bins);
            _marks.Clear();
            _marks.AddRange(marks);
            InvalidateVisual();
        }

        private const double LeftAxis = 58, TopPad = 8, BottomPad = 16;

        /// <summary>Groups the visible intervals into "buckets" (at most one per 2 px).</summary>
        private (double[] avg, bool[] has, int n) Aggregate(double plotW)
        {
            var (vs, ve) = View();
            int first = Math.Clamp((int)Math.Floor(vs * Bins), 0, Bins - 1);
            int visBins = Math.Max(1, (int)Math.Ceiling(ve * Bins) - first);
            int n = Math.Max(1, Math.Min(visBins, (int)(plotW / 2)));
            var avg = new double[n];
            var has = new bool[n];
            for (int b = 0; b < n; b++)
            {
                int i0 = first + (int)((long)b * visBins / n), i1 = first + (int)((long)(b + 1) * visBins / n);
                double s = 0; int c = 0;
                for (int i = i0; i < i1 && i < Bins; i++) { s += _sum[i]; c += _count[i]; }
                if (c > 0) { avg[b] = s / c; has[b] = true; }
            }
            return (avg, has, n);
        }

        private static double NiceMax(double[] avg, bool[] has)
        {
            double m = 0;
            for (int i = 0; i < avg.Length; i++) if (has[i]) m = Math.Max(m, avg[i]);
            if (m <= 0) return 100;
            double[] steps = { 25, 50, 100, 200, 250, 500, 750, 1000, 1500, 2000, 3000, 4000, 5000, 7500, 10000 };
            foreach (var s in steps) if (m * 1.08 <= s) return s;
            return Math.Ceiling(m / 1000) * 1000;
        }

        /// <summary>Tooltip text at position X (or null outside the area with data).</summary>
        public string? DescribeAt(Point p)
        {
            double plotW = ActualWidth - LeftAxis;
            if (plotW <= 0 || p.X < LeftAxis) return null;
            var (avg, has, n) = Aggregate(plotW);
            int b = Math.Clamp((int)((p.X - LeftAxis) / plotW * n), 0, n - 1);
            if (!has[b]) return null;
            var (vs, ve) = View();
            double frac = vs + (b + 0.5) / n * (ve - vs);
            double gb = TotalBytes * frac / 1_073_741_824.0;
            return $"{gb:F1} GB  ·  {avg[b]:F0} MB/s";
        }

        private static string Gb(double bytes)
        {
            double gb = bytes / 1_073_741_824.0;
            return gb >= 100 ? $"{gb:F0} GB" : gb >= 10 ? $"{gb:F1} GB" : $"{gb:F2} GB";
        }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));
            if (W < LeftAxis + 20 || H < TopPad + BottomPad + 10) return;

            var textBrush = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
            var accent    = TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
            var slowBrush = TryFindResource("YellowBrush") as Brush ?? Brushes.Orange;
            var badBrush  = TryFindResource("RedBrush") as Brush ?? Brushes.Red;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var font = new Typeface(TryFindResource("MonoFont") as FontFamily ?? new FontFamily("Consolas"),
                                    FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            FormattedText Txt(string t) => new FormattedText(t, CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight, font, 10, textBrush, dpi);

            double plotW = W - LeftAxis, plotH = H - TopPad - BottomPad;
            var (vs, ve) = View();
            double span = ve - vs;
            var (avg, has, n) = Aggregate(plotW);
            double yMax = NiceMax(avg, has);
            double Y(double v) => TopPad + plotH - Math.Clamp(v / yMax, 0, 1) * plotH;
            double X(int b) => LeftAxis + (b + 0.5) / n * plotW;
            double XPos(double pos) => LeftAxis + (pos - vs) / span * plotW;

            // the shared chart grid (thin lines fading towards their ends, no frame); the labels: 0, half, maximum
            ChartGrid.Draw(dc, new Rect(LeftAxis, TopPad, plotW, plotH), ChartGrid.LineColor(this), 4, 0, ChartGrid.Scale(this));
            foreach (double v in new[] { 0.0, yMax / 2, yMax })
            {
                double y = Math.Round(Y(v)) + 0.5;
                var ft = Txt($"{v:F0} MB/s");
                dc.DrawText(ft, new Point(LeftAxis - 6 - ft.Width, y - ft.Height / 2));
            }
            // X axis: the start, the middle and the end of the displayed area (the position on the disk)
            var ftS = Txt(TotalBytes > 0 && vs > 0 ? Gb(TotalBytes * vs) : "0 GB");
            dc.DrawText(ftS, new Point(LeftAxis, H - ftS.Height));
            var ftE = Txt(TotalBytes > 0 ? Gb(TotalBytes * ve) : "end");
            dc.DrawText(ftE, new Point(W - ftE.Width, H - ftE.Height));
            if (TotalBytes > 0)
            {
                var ftM = Txt(Gb(TotalBytes * (vs + ve) / 2));
                dc.DrawText(ftM, new Point(LeftAxis + plotW / 2 - ftM.Width / 2, H - ftM.Height));
            }

            // The curve + the filled area; buckets without data break the line
            var linePen = new Pen(accent, 1.6) { LineJoin = PenLineJoin.Round }; linePen.Freeze();
            Brush fill = accent.CloneCurrentValue();
            fill.Opacity = 0.12;
            double baseY = TopPad + plotH;
            int i0 = 0;
            while (i0 < n)
            {
                while (i0 < n && !has[i0]) i0++;
                if (i0 >= n) break;
                int i1 = i0;
                while (i1 + 1 < n && has[i1 + 1]) i1++;

                var line = new StreamGeometry();
                var area = new StreamGeometry();
                using (var lc = line.Open())
                using (var ac = area.Open())
                {
                    ac.BeginFigure(new Point(X(i0), baseY), true, true);
                    for (int i = i0; i <= i1; i++)
                    {
                        var pt = new Point(X(i), Y(avg[i]));
                        if (i == i0) lc.BeginFigure(pt, false, false); else lc.LineTo(pt, true, true);
                        ac.LineTo(pt, true, true);
                    }
                    ac.LineTo(new Point(X(i1), baseY), true, true);
                }
                area.Freeze(); line.Freeze();
                dc.DrawGeometry(fill, null, area);
                dc.DrawGeometry(null, linePen, line);
                if (i0 == i1) dc.DrawEllipse(accent, null, new Point(X(i0), Y(avg[i0])), 1.5, 1.5);
                i0 = i1 + 1;
            }

            // Markers: slow (orange) and bad (red) areas as short lines on the bottom edge, one per column of points (bad takes
            // priority). Circles on the curve covered the chart when there were dozens of areas.
            var markAt = new System.Collections.Generic.Dictionary<int, bool>();
            foreach (var (pos, bad) in _marks)
            {
                if (pos < vs || pos > ve) continue;
                int b = Math.Clamp((int)((pos - vs) / span * n), 0, n - 1);
                markAt[b] = (markAt.TryGetValue(b, out bool already) && already) || bad;
            }
            if (markAt.Count > 0)
            {
                var slowPen = new Pen(slowBrush, 2); slowPen.Freeze();
                var badPen = new Pen(badBrush, 2.5); badPen.Freeze();
                foreach (var (b, bad) in markAt)
                {
                    double x = Math.Round(X(b)) + 0.5;
                    dc.DrawLine(bad ? badPen : slowPen, new Point(x, baseY - (bad ? 9 : 6)), new Point(x, baseY));
                }
            }

            // Current position
            if (CurrentPos >= vs && CurrentPos <= ve)
            {
                double x = Math.Round(XPos(CurrentPos)) + 0.5;
                var nowPen = new Pen(textBrush, 1) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) };
                dc.DrawLine(nowPen, new Point(x, TopPad), new Point(x, baseY));
            }
        }
    }
}
