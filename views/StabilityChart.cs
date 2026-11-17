using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Brush   = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color   = System.Windows.Media.Color;
using Point   = System.Windows.Point;
using Pen     = System.Windows.Media.Pen;
using FontFamily = System.Windows.Media.FontFamily;
using SMDWin.Services;

namespace SMDWin.Views
{
    /// <summary>
    /// A stability chart by days (30 by default): for each day, a column with segments coloured by event type (BSOD red,
    /// unexpected shutdown orange, hardware purple, app crash yellow, app hang grey). Days without problems have a discreet
    /// green dot at the base — the stable period vs. the problem days shows at a glance.
    /// </summary>
    public sealed class StabilityChart : FrameworkElement
    {
        public int Days { get; set; } = 30;
        private List<StabilityService.StabilityEvent> _events = new();

        public static readonly (StabilityService.Kind kind, Color color, string label)[] Legend =
        {
            (StabilityService.Kind.BlueScreen,         Color.FromRgb(239, 68, 68),  "Blue screens"),
            (StabilityService.Kind.UnexpectedShutdown, Color.FromRgb(249, 115, 22), "Unexpected shutdowns"),
            (StabilityService.Kind.Hardware,           Color.FromRgb(139, 92, 246), "Hardware errors"),
            (StabilityService.Kind.AppCrash,           Color.FromRgb(245, 158, 11), "App crashes"),
            (StabilityService.Kind.AppHang,            Color.FromRgb(148, 163, 184), "App freezes"),
        };

        public void SetData(IEnumerable<StabilityService.StabilityEvent> events)
        {
            _events = events.ToList();
            InvalidateVisual();
        }

        private const double LeftPad = 4, BottomPad = 16, TopPad = 4;

        private DateTime FirstDay => DateTime.Today.AddDays(-(Days - 1));

        public string? DescribeAt(Point p)
        {
            double colW = (ActualWidth - LeftPad) / Days;
            if (colW <= 0) return null;
            int d = (int)((p.X - LeftPad) / colW);
            if (d < 0 || d >= Days) return null;
            var day = FirstDay.AddDays(d);
            var evs = _events.Where(e => e.Time.Date == day).ToList();
            if (evs.Count == 0) return $"{day:dd MMM} · no problems";
            var parts = Legend.Select(l => (l.label, n: evs.Count(e => e.Kind == l.kind))).Where(x => x.n > 0)
                              .Select(x => $"{x.n} {x.label.ToLowerInvariant()}");
            return $"{day:dd MMM} · " + string.Join(", ", parts);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));
            if (W < 60 || H < 40) return;

            var text2 = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;
            var grid  = TryFindResource("BorderBrush2") as Brush ?? Brushes.LightGray;
            var ok    = TryFindResource("StatusOkBrush") as Brush ?? Brushes.ForestGreen;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var tf = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            double plotH = H - BottomPad - TopPad, colW = (W - LeftPad) / Days;
            double barW = Math.Max(3, Math.Min(18, colW * 0.62));
            var byDay = _events.Where(e => e.Time.Date >= FirstDay).GroupBy(e => (e.Time.Date - FirstDay).Days)
                               .ToDictionary(g => g.Key, g => g.ToList());
            int maxCount = Math.Max(3, byDay.Values.Select(v => v.Count).DefaultIfEmpty(0).Max());
            double unit = plotH / maxCount;

            var basePen = new Pen(grid, 1); basePen.Freeze();
            double baseY = Math.Round(TopPad + plotH) + 0.5;
            dc.DrawLine(basePen, new Point(LeftPad, baseY), new Point(W, baseY));

            for (int d = 0; d < Days; d++)
            {
                double cx = LeftPad + colW * (d + 0.5);
                if (!byDay.TryGetValue(d, out var evs) || evs.Count == 0)
                {
                    dc.DrawEllipse(ok, null, new Point(cx, baseY - 4), 2, 2);
                    continue;
                }
                double y = baseY - 1;
                foreach (var (kind, color, _) in Legend)
                {
                    int n = evs.Count(e => e.Kind == kind);
                    if (n == 0) continue;
                    double h = Math.Max(3, n * unit);
                    var br = new SolidColorBrush(color); br.Freeze();
                    dc.DrawRoundedRectangle(br, null, new Rect(cx - barW / 2, y - h, barW, Math.Max(1, h - 1)), 2, 2);
                    y -= h;
                }
            }

            // Date labels: the first day, every 7 days, today
            for (int d = 0; d < Days; d += 7)
            {
                var ft = new FormattedText(FirstDay.AddDays(d).ToString("dd MMM", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                                           System.Windows.FlowDirection.LeftToRight, tf, 10, text2, dpi);
                dc.DrawText(ft, new Point(LeftPad + colW * d, H - ft.Height));
            }
            var today = new FormattedText("today", CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight, tf, 10, text2, dpi);
            dc.DrawText(today, new Point(W - today.Width, H - today.Height));
        }
    }
}
