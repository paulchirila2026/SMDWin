using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfSharp.Drawing;

namespace SMDWin.Views
{
    /// <summary>
    /// The shared parts of the PDF reports (Stress, System Info): the "SMD Win" design system colours (no dark backgrounds —
    /// reports get printed), the fonts, the light header, the cards with a fine outline, the status dot and the smooth chart
    /// lines.
    /// </summary>
    internal static class PdfKit
    {
        public static readonly XColor Ink      = XColor.FromArgb(0xFF, 0x15, 0x18, 0x1D);
        public static readonly XColor Ink2     = XColor.FromArgb(0xFF, 0x4B, 0x55, 0x63);
        public static readonly XColor Ink3     = XColor.FromArgb(0xFF, 0x6B, 0x72, 0x80);
        public static readonly XColor Line     = XColor.FromArgb(0xFF, 0xE1, 0xE4, 0xE8);
        public static readonly XColor Grid     = XColor.FromArgb(0xFF, 0xEE, 0xF0, 0xF3);
        public static readonly XColor Accent   = XColor.FromArgb(0xFF, 0x25, 0x63, 0xEB);
        public static readonly XColor Orange   = XColor.FromArgb(0xFF, 0xEA, 0x58, 0x0C);
        public static readonly XColor Good     = XColor.FromArgb(0xFF, 0x15, 0x80, 0x3D);
        public static readonly XColor Check    = XColor.FromArgb(0xFF, 0xB4, 0x53, 0x09);
        public static readonly XColor Problem  = XColor.FromArgb(0xFF, 0xDC, 0x26, 0x26);
        public static readonly XColor Unknown  = XColor.FromArgb(0xFF, 0x6B, 0x72, 0x80);
        public static readonly XColor GoodSoft    = XColor.FromArgb(0xFF, 0xE8, 0xF5, 0xEE);
        public static readonly XColor CheckSoft   = XColor.FromArgb(0xFF, 0xFD, 0xF3, 0xE7);
        public static readonly XColor ProblemSoft = XColor.FromArgb(0xFF, 0xFD, 0xEC, 0xEC);
        public static readonly XColor UnknownSoft = XColor.FromArgb(0xFF, 0xF1, 0xF2, 0xF4);
        public static readonly XColor StressBand  = XColor.FromArgb(0xFF, 0xFD, 0xEE, 0xE0);

        private static readonly string FontsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        private static readonly Lazy<bool> HasSegoe = new(() => File.Exists(Path.Combine(FontsDir, "segoeui.ttf")) && File.Exists(Path.Combine(FontsDir, "segoeuib.ttf")));
        private static readonly Lazy<bool> HasSegoeSemi = new(() => File.Exists(Path.Combine(FontsDir, "seguisb.ttf")));

        /// <summary>Segoe UI (has °, ·, ×, –, ″); Arial on systems without it.</summary>
        public static string Family => HasSegoe.Value ? "Segoe UI" : "Arial";

        public static XFont F(double size, bool bold = false) => new(Family, size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);

        /// <summary>The large figures: Segoe UI Semibold (like Bahnschrift in the app, but also included in Windows 10 LTSC).</summary>
        public static XFont Num(double size) => HasSegoeSemi.Value ? new XFont("Segoe UI Semibold", size, XFontStyleEx.Regular) : F(size, true);

        public static XSolidBrush B(XColor c) => new(c);

        public static void EnsureFontResolver()
        {
            if (PdfSharp.Fonts.GlobalFontSettings.FontResolver == null)
                PdfSharp.Fonts.GlobalFontSettings.FontResolver = new WindowsFontResolver();
        }

        /// <summary>Semnul aplicatiei: trei bare rotunjite, in albastru.</summary>
        public static void Logo(XGraphics g, double x, double y, double h)
        {
            double w = h * 0.26, gap = h * 0.13;
            var br = B(Accent);
            g.DrawRoundedRectangle(br, x, y + h * 0.45, w, h * 0.55, w * 0.4, w * 0.4);
            g.DrawRoundedRectangle(br, x + w + gap, y + h * 0.18, w, h * 0.82, w * 0.4, w * 0.4);
            g.DrawRoundedRectangle(br, x + 2 * (w + gap), y, w, h, w * 0.4, w * 0.4);
        }

        /// <summary>
        /// The light header (printer friendly): mark + small label, the large title, a line of details; on the right up to three
        /// short rows (date, duration, workshop). A fine line under it. Returns the Y under the line.
        /// </summary>
        public static double Header(XGraphics g, double pageW, double m, double top, string kicker, string title, string subtitle, IList<string> right)
        {
            Logo(g, m, top + 1, 11);
            g.DrawString(kicker.ToUpperInvariant(), F(7.5, true), B(Ink3), m + 19, top + 10);
            double rightW = 0;
            var fR = F(8.5);
            foreach (var s in right) rightW = Math.Max(rightW, g.MeasureString(s, fR).Width);
            double titleMaxW = pageW - 2 * m - rightW - 20;
            var fT = F(20, true);
            string t = Fit(g, title, fT, titleMaxW);
            g.DrawString(t, fT, B(Ink), m, top + 38);
            if (!string.IsNullOrWhiteSpace(subtitle))
                g.DrawString(Fit(g, subtitle, F(9.5), titleMaxW), F(9.5), B(Ink2), m, top + 54);
            double ry = top + 10;
            for (int i = 0; i < right.Count; i++)
            {
                var font = i == 0 ? F(8.5, true) : fR;
                double w = g.MeasureString(right[i], font).Width;
                g.DrawString(right[i], font, B(i == 0 ? Ink : Ink2), pageW - m - w, ry);
                ry += 13;
            }
            double ly = top + 66;
            g.DrawLine(new XPen(Line, 0.8), m, ly, pageW - m, ly);
            return ly + 12;
        }

        public static void Card(XGraphics g, double x, double y, double w, double h)
            => g.DrawRoundedRectangle(new XPen(Line, 0.75), XBrushes.White, x, y, w, h, 7, 7);

        public static void Dot(XGraphics g, double cx, double cy, double r, XColor c) => g.DrawEllipse(B(c), cx - r, cy - r, 2 * r, 2 * r);

        /// <summary>A check in a coloured circle (the verdict) — drawn, not a font character, so no "squares" appear.</summary>
        public static void CheckBadge(XGraphics g, double cx, double cy, double r, XColor fg, XColor bg, string kind)
        {
            g.DrawEllipse(B(bg), cx - r, cy - r, 2 * r, 2 * r);
            var pen = new XPen(fg, r * 0.17) { LineCap = XLineCap.Round, LineJoin = XLineJoin.Round };
            if (kind == "ok")
                g.DrawLines(pen, new[] { new XPoint(cx - r * 0.38, cy + r * 0.02), new XPoint(cx - r * 0.1, cy + r * 0.3), new XPoint(cx + r * 0.42, cy - r * 0.28) });
            else if (kind == "x")
            {
                g.DrawLine(pen, cx - r * 0.3, cy - r * 0.3, cx + r * 0.3, cy + r * 0.3);
                g.DrawLine(pen, cx + r * 0.3, cy - r * 0.3, cx - r * 0.3, cy + r * 0.3);
            }
            else
            {
                g.DrawLine(pen, cx, cy - r * 0.38, cx, cy + r * 0.08);
                g.DrawEllipse(B(fg), cx - r * 0.1, cy + r * 0.28, r * 0.2, r * 0.2);
            }
        }

        public static void Footer(XGraphics g, double pageW, double pageH, double m, string left, string right)
        {
            var f = F(7.5);
            g.DrawLine(new XPen(Line, 0.6), m, pageH - 30, pageW - m, pageH - 30);
            g.DrawString(left, f, B(Ink3), m, pageH - 18);
            double w = g.MeasureString(right, f).Width;
            g.DrawString(right, f, B(Ink3), pageW - m - w, pageH - 18);
        }

        /// <summary>The text shortened with "…" when it does not fit in the given width.</summary>
        public static string Fit(XGraphics g, string s, XFont f, double maxW)
        {
            if (string.IsNullOrEmpty(s) || g.MeasureString(s, f).Width <= maxW) return s ?? "";
            while (s.Length > 1 && g.MeasureString(s + "…", f).Width > maxW) s = s[..^1];
            return s.TrimEnd() + "…";
        }

        /// <summary>Lines that fit in the given width; a "\n" in the value starts a new line (words are not glued together).</summary>
        public static List<string> Wrap(XGraphics g, string text, XFont font, double maxWidth)
        {
            var lines = new List<string>();
            foreach (var para in (text ?? "").Replace("\r", "").Split('\n'))
            {
                string p = para.Trim();
                if (p.Length == 0) continue;
                if (g.MeasureString(p, font).Width <= maxWidth) { lines.Add(p); continue; }
                string cur = "";
                foreach (var w in p.Split(' '))
                {
                    string trial = cur.Length == 0 ? w : cur + " " + w;
                    if (g.MeasureString(trial, font).Width <= maxWidth) cur = trial;
                    else
                    {
                        if (cur.Length > 0) lines.Add(cur);
                        cur = g.MeasureString(w, font).Width <= maxWidth ? w : Fit(g, w, font, maxWidth);
                    }
                }
                if (cur.Length > 0) lines.Add(cur);
            }
            if (lines.Count == 0) lines.Add("");
            return lines;
        }

        /// <summary>„Intel(R) Core(TM) i5-4570 CPU @ 3.20GHz” → „Intel Core i5-4570 CPU @ 3.20 GHz”.</summary>
        public static string CleanName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.Replace("(R)", "").Replace("(r)", "").Replace("(TM)", "").Replace("(tm)", "").Replace("®", "").Replace("™", "");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"(\d)GHz", "$1 GHz");
            return System.Text.RegularExpressions.Regex.Replace(s, @"\s{2,}", " ").Trim();
        }

        /// <summary>A smooth line through points (the same curve as in the app: it does not go above / below the real values).</summary>
        public static void SmoothLine(XGraphics g, XPen pen, IList<XPoint> pts)
        {
            if (pts.Count < 2) return;
            if (pts.Count == 2) { g.DrawLine(pen, pts[0], pts[1]); return; }
            var wp = pts.Select(p => new System.Windows.Point(p.X, p.Y)).ToList();
            var m = MiniLineChart.MonotoneSlopes(wp);
            var bz = new List<XPoint> { pts[0] };
            for (int i = 0; i < pts.Count - 1; i++)
            {
                double dx = (pts[i + 1].X - pts[i].X) / 3;
                bz.Add(new XPoint(pts[i].X + dx, pts[i].Y + m[i] * dx));
                bz.Add(new XPoint(pts[i + 1].X - dx, pts[i + 1].Y - m[i + 1] * dx));
                bz.Add(pts[i + 1]);
            }
            g.DrawBeziers(pen, bz.ToArray());
        }
    }
}
