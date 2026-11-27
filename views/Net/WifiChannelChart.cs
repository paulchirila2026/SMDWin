using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SMDWin.Services;
using Brush = System.Windows.Media.Brush;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace SMDWin.Views.Net
{
    /// <summary>
    /// The "hills" chart of Channel analysis: frequency on X (the channels of the band written under it), signal (dBm) on Y,
    /// each access point a hill as wide as the block it uses. The connected one is filled in the accent, the other units of
    /// the own network dashed; the 1 / 6 / 11 guides on 2.4 GHz, the DFS area on 5 GHz, the −67 dBm line, the recommended
    /// channel as a green band. Drawn in OnRender (no visual tree per hill); the tooltip follows the hill under the mouse.
    /// </summary>
    public sealed class WifiChannelChart : FrameworkElement
    {
        private List<WifiAp> _aps = new();
        private string _band = "2.4";
        private WifiAp? _connected;
        private int _best;
        private readonly List<(WifiAp ap, double x0, double x1, double top)> _hits = new();
        private readonly System.Windows.Controls.ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };
        private WifiAp? _hover;

        private static readonly Color[] Palette =
        {
            Color.FromRgb(0xA0, 0x8C, 0xFF), Color.FromRgb(0x38, 0xC7, 0xDA), Color.FromRgb(0xFF, 0x9A, 0x5C), Color.FromRgb(0xEA, 0x6C, 0xC2),
            Color.FromRgb(0x9C, 0xC2, 0x4A), Color.FromRgb(0xD1, 0xA3, 0x5C), Color.FromRgb(0x6F, 0xB2, 0xFF), Color.FromRgb(0xC4, 0x8A, 0xE6),
        };

        public WifiChannelChart()
        {
            ToolTip = _tip;
            System.Windows.Controls.ToolTipService.SetInitialShowDelay(this, 0);
            System.Windows.Controls.ToolTipService.SetShowDuration(this, 60000);
        }

        public void Show(List<WifiAp> aps, string band, WifiAp? connected, int bestChannel)
        {
            _aps = aps; _band = band; _connected = connected; _best = bestChannel;
            InvalidateVisual();
        }

        private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

        private (double f0, double f1) Range()
        {
            var chs = WifiAnalysis.Channels(_band);
            if (_band == "2.4") return (2401, 2495);
            int lo = WifiAnalysis.FrequencyOf(_band, chs[0]) - 12, hi = WifiAnalysis.FrequencyOf(_band, chs[^1]) + 12;
            // on 6 GHz show only the part with networks (or the lower UNII-5 band when nothing is there)
            if (_band == "6")
            {
                var on = _aps.Where(a => a.Band == "6").ToList();
                hi = on.Count > 0 ? Math.Min(hi, Math.Max(on.Max(a => a.HighMHz) + 40, 6425)) : 6425;
            }
            return (lo, hi);
        }

        protected override void OnRender(DrawingContext dc)
        {
            _hits.Clear();
            double W = ActualWidth, H = ActualHeight;
            if (W < 200 || H < 120) return;
            const double pl = 46, pr = 10, pt = 12, pb = 34;
            double cw = W - pl - pr, ch = H - pt - pb;
            var (f0, f1) = Range();
            double X(double f) => pl + cw * (f - f0) / (f1 - f0);
            double Y(double dbm) => pt + ch * (1 - (Math.Clamp(dbm, -95, -30) + 95) / 65.0);
            var fg2 = Res("TextSecondaryBrush", Brushes.Gray);
            var line = Res("BorderBrush2", Brushes.DimGray);
            var ok = Res("StatusOkBrush", Brushes.SeaGreen);
            var crit = Res("StatusCritBrush", Brushes.IndianRed);
            var accent = Res("AccentBrush", Brushes.DodgerBlue);
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var bold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            FormattedText Txt(string s, Brush b, double size = 10, bool strong = false) =>
                new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, strong ? bold : typeface, size, b, dpi);

            // DFS area (5 GHz)
            if (_band == "5")
            {
                double d0 = X(WifiAnalysis.FrequencyOf("5", 52) - 10), d1 = X(WifiAnalysis.FrequencyOf("5", 144) + 10);
                var dfsFill = crit.Clone(); dfsFill.Opacity = 0.07; dfsFill.Freeze();
                dc.DrawRectangle(dfsFill, null, new Rect(d0, pt, d1 - d0, ch));
                var t = Txt("DFS · " + MainWindow.PT("radar"), crit);
                dc.DrawText(t, new Point((d0 + d1 - t.Width) / 2, pt + 2));
            }
            // dBm grid + the −67 line
            var dash = new Pen(line, 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) }; dash.Freeze();
            var solid = new Pen(line, 1); solid.Freeze();
            foreach (int d in new[] { -30, -50, -67, -80, -90 })
            {
                dc.DrawLine(d == -67 ? solid : dash, new Point(pl, Y(d)), new Point(W - pr, Y(d)));
                var t = Txt(d.ToString(CultureInfo.InvariantCulture), fg2);
                dc.DrawText(t, new Point(pl - 6 - t.Width, Y(d) - t.Height / 2));
            }
            var l67 = Txt(MainWindow.PT("−67 dBm · good enough for video calls and roaming"), fg2, 9);
            dc.DrawText(l67, new Point(W - pr - l67.Width, Y(-67) - l67.Height - 1));
            // 1 / 6 / 11
            if (_band == "2.4")
            {
                var guide = new Pen(fg2, 1) { DashStyle = new DashStyle(new double[] { 2, 4 }, 0) }; guide.Freeze();
                foreach (int c in new[] { 1, 6, 11 }) dc.DrawLine(guide, new Point(X(WifiAnalysis.FrequencyOf("2.4", c)), pt), new Point(X(WifiAnalysis.FrequencyOf("2.4", c)), pt + ch));
            }
            // the recommended channel
            if (_best > 0)
            {
                var g = ok.Clone(); g.Opacity = 0.14; g.Freeze();
                double bx = X(WifiAnalysis.FrequencyOf(_band, _best));
                dc.DrawRoundedRectangle(g, null, new Rect(bx - 9, pt, 18, ch), 4, 4);
            }
            // channel numbers
            var chs = WifiAnalysis.Channels(_band).Where(c => { double f = WifiAnalysis.FrequencyOf(_band, c); return f >= f0 && f <= f1; }).ToList();
            double minGap = 22, lastX = -100;
            foreach (int c in chs)
            {
                double x = X(WifiAnalysis.FrequencyOf(_band, c));
                if (x - lastX < minGap && c != _best) continue;
                var col = c == _best ? ok : WifiAnalysis.IsDfs(_band, c) ? crit : fg2;
                var t = Txt(c.ToString(CultureInfo.InvariantCulture), col, 10, c == _best);
                dc.DrawText(t, new Point(x - t.Width / 2, H - pb + 4));
                lastX = x;
            }
            var axis = Txt(MainWindow.PT("channel / frequency (MHz)"), fg2, 9);
            dc.DrawText(axis, new Point(pl + (cw - axis.Width) / 2, H - axis.Height - 1));
            var lf = Txt(f0.ToString(CultureInfo.InvariantCulture), fg2, 9); dc.DrawText(lf, new Point(pl, H - lf.Height - 1));
            var rf = Txt(f1.ToString(CultureInfo.InvariantCulture), fg2, 9); dc.DrawText(rf, new Point(W - pr - rf.Width, H - rf.Height - 1));

            // the hills, faint first so the loud ones (and the connected one) are on top
            int k = 0;
            double baseY = pt + ch;
            foreach (var a in _aps.Where(a => a.Band == _band).OrderBy(a => a.Connected).ThenBy(a => a.Rssi))
            {
                bool own = WifiAnalysis.IsOwn(a, _connected);
                Brush stroke = a.Connected || own ? accent : new SolidColorBrush(Palette[k++ % Palette.Length]);
                double x0 = X(a.LowMHz), x1 = X(a.HighMHz), top = Y(a.Rssi);
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(new Point(x0, baseY), true, false);
                    g.BezierTo(new Point(x0 + (x1 - x0) * 0.16, top), new Point(x1 - (x1 - x0) * 0.16, top), new Point(x1, baseY), true, true);
                }
                geo.Freeze();
                var fill = stroke.Clone(); fill.Opacity = a.Connected ? 0.24 : a == _hover ? 0.22 : 0.09;
                var pen = new Pen(stroke, a.Connected ? 2.6 : a == _hover ? 2.2 : 1.4);
                if (own && !a.Connected) pen.DashStyle = new DashStyle(new double[] { 4, 2 }, 0);
                dc.DrawGeometry(fill, pen, geo);
                var name = Txt(WifiAnalysis.Name(a), stroke, 10, a.Connected);
                dc.DrawText(name, new Point((x0 + x1 - name.Width) / 2, top - name.Height * 0.75 - 2));
                _hits.Add((a, x0, x1, top));
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = e.GetPosition(this);
            // the hill under the mouse: inside its width and below its top; the loudest wins
            WifiAp? hit = null; double bestTop = double.MaxValue;
            foreach (var (ap, x0, x1, top) in _hits)
            {
                if (p.X < x0 || p.X > x1) continue;
                double rel = Math.Abs((p.X - (x0 + x1) / 2) / ((x1 - x0) / 2));
                double curveTop = top + (ActualHeight - 34 - top) * Math.Pow(rel, 6);   // roughly the curve
                if (p.Y >= curveTop - 6 && top < bestTop) { hit = ap; bestTop = top; }
            }
            if (hit != _hover)
            {
                _hover = hit;
                _tip.Content = hit == null ? null : TipText(hit);
                _tip.IsOpen = hit != null;
                InvalidateVisual();
            }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = null; _tip.IsOpen = false; InvalidateVisual();
        }

        internal static string TipText(WifiAp a)
        {
            var lines = new List<string>
            {
                WifiAnalysis.Name(a) + (a.Connected ? "  · " + MainWindow.PT("connected") : ""),
                a.Bssid + (a.Vendor.Length > 0 ? " · " + a.Vendor : ""),
                MainWindow.PT("channel {0} · {1} MHz · {2}–{3} MHz", a.Channel, a.WidthMHz, a.LowMHz, a.HighMHz) + (a.IsDfs ? " · DFS" : ""),
                $"{a.Rssi} dBm · {a.Standard} · {a.Streams}×{a.Streams} · " + MainWindow.PT("max {0} Mb/s", a.MaxRateMbps),
                a.Security + (a.Pmf.Length > 0 ? " · PMF " + MainWindow.PT(a.Pmf) : "") + (a.Wps ? " · WPS" : ""),
            };
            if (a.Clients.HasValue) lines.Add(MainWindow.PT("{0} clients · channel busy {1}%", a.Clients, a.UtilizationPct ?? 0));
            if (a.Country.Length > 0) lines.Add(MainWindow.PT("country: {0}", a.Country));
            return string.Join("\n", lines);
        }
    }
}
