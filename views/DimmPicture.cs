using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SMDWin.Services;
using Brush   = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color   = System.Windows.Media.Color;
using Point   = System.Windows.Point;
using Size    = System.Windows.Size;
using Pen     = System.Windows.Media.Pen;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace SMDWin.Views
{
    /// <summary>
    /// A memory module drawn like a real one: green board, the chips (with the orientation dot and engraved "text"),
    /// the small SPD chip and the memory type in the middle, the trace line, the gold contacts with the notch at the place
    /// specific to the generation and the side cut-outs for the socket clips.
    /// Two forms: DIMM (desktop, long) and SO-DIMM (laptop — about half the length, 4 chips, cut-outs at the bottom).
    /// Drawn directly (OnRender); the contacts all have the same width (<see cref="DimmLayout"/>).
    /// </summary>
    public sealed class DimmPicture : FrameworkElement
    {
        private const double PictureHeight = 62;
        private static readonly Brush LabelBrush = Frozen(Color.FromArgb(204, 255, 255, 255));

        public string MemoryType { get; set; } = "";
        public bool SoDimm { get; set; }

        public DimmPicture()
        {
            ClipToBounds = true;   // the side cut-outs are drawn slightly outside the board
            Loaded   += (_, _) => ThemeManager.ThemeChanged += OnTheme;
            Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnTheme;
        }

        private void OnTheme() => Dispatcher.BeginInvoke(new Action(InvalidateVisual));

        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        protected override Size MeasureOverride(Size available)
        {
            double w = double.IsInfinity(available.Width) ? 220 : available.Width;
            // SO-DIMM / soldered: the real proportions of a laptop module, much shorter than a desktop DIMM
            if (SoDimm || DimmLayout.IsSoldered(MemoryType)) w = DimmLayout.CompactWidth(w, PictureHeight);
            return new Size(w, PictureHeight);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            if (W < 60 || H < 30) return;
            if (DimmLayout.IsSoldered(MemoryType)) DrawSoldered(dc, this, W, H, MemoryType);
            else DrawModule(dc, this, W, H, MemoryType, SoDimm);
        }

        /// <summary>
        /// Soldered memory (LPDDR): a piece of the motherboard with two square memory chips and fine traces between them —
        /// no module, no gold contacts, no socket, because there is nothing to take out.
        /// </summary>
        internal static void DrawSoldered(DrawingContext dc, FrameworkElement host, double W, double H, string memoryType)
        {
            Brush Res(string key, Brush fallback) => host.TryFindResource(key) as Brush ?? fallback;
            var pcb  = Res("DimmPcbBrush", Brushes.SeaGreen);
            var edge = Res("DimmPcbEdgeBrush", Brushes.DarkGreen);
            var chip = Res("DimmChipBrush", Brushes.Black);
            var mark = Res("DimmChipMarkBrush", Brushes.Gray);

            // the board area, with a soft dashed edge: a part of the motherboard, not a separate piece
            var rim = new Pen(edge, 1) { DashStyle = new DashStyle(new double[] { 3, 2 }, 0) }; rim.Freeze();
            dc.DrawRoundedRectangle(pcb, rim, new Rect(0.5, 0.5, W - 1, H - 1), 6, 6);

            // fine traces under the chips
            for (int i = 0; i < 4; i++)
            {
                double y = H * (0.30 + i * 0.13);
                dc.DrawRectangle(edge, null, new Rect(6, y, W - 12, 1.2));
            }

            // two square chips (BGA packages), each with its orientation dot and engraved lines
            double size = Math.Min(H - 22, (W - 30) / 2);
            double top = (H - size) / 2 + 5, gap = W - 2 * size - 24;
            for (int i = 0; i < 2; i++)
            {
                var r = new Rect(12 + i * (size + gap), top, size, size);
                dc.DrawRoundedRectangle(chip, null, r, 2, 2);
                dc.DrawEllipse(mark, null, new Point(r.X + 5, r.Y + 5), 1.6, 1.6);
                dc.DrawRoundedRectangle(mark, null, new Rect(r.X + 6, r.Y + r.Height * 0.52, r.Width - 12, 1.5), 0.7, 0.7);
                dc.DrawRoundedRectangle(mark, null, new Rect(r.X + 6, r.Y + r.Height * 0.52 + 5, (r.Width - 12) * 0.6, 1.5), 0.7, 0.7);
            }

            // the memory type, top centre
            var label = new FormattedText(string.IsNullOrWhiteSpace(memoryType) ? "LPDDR" : memoryType.Trim(),
                CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                new Typeface(System.Windows.Documents.TextElement.GetFontFamily(host), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                8.5, LabelBrush, VisualTreeHelper.GetDpi(host).PixelsPerDip);
            dc.DrawText(label, new Point(W / 2 - label.Width / 2, 4));
        }

        /// <summary>
        /// Draws the module in the (0, 0, W, H) rectangle of <paramref name="host"/> — also used by
        /// <see cref="DimmSocketPicture"/> for the module seated in the socket.
        /// </summary>
        internal static void DrawModule(DrawingContext dc, FrameworkElement host, double W, double H, string memoryType, bool soDimm)
        {
            Brush Res(string key, Brush fallback) => host.TryFindResource(key) as Brush ?? fallback;
            var pin   = Res("DimmPinBrush", Brushes.Goldenrod);
            var card  = Res("BgCardBrush", Brushes.White);

            const double margin = 9, pinArea = 11, traces = 4;
            double pinTop = H - pinArea + 1, tracesTop = H - pinArea - traces;

            // the board: dark green, a light top edge, a few fine traces (the same look as the drive drawings)
            dc.DrawRoundedRectangle(PcbBrush, null, new Rect(0, 0, W, H), 3, 3);
            dc.DrawRectangle(TopEdge, null, new Rect(3, 0.4, W - 6, 0.8));
            var tracePen = new Pen(TraceBrush, 0.6); tracePen.Freeze();
            double ty = tracesTop - 3;
            dc.DrawLine(tracePen, new Point(margin, ty), new Point(W * 0.28, ty));
            dc.DrawLine(tracePen, new Point(W * 0.28, ty), new Point(W * 0.28 + 5, ty - 4));
            dc.DrawLine(tracePen, new Point(W * 0.28 + 5, ty - 4), new Point(W * 0.46, ty - 4));
            dc.DrawLine(tracePen, new Point(W * 0.6, ty), new Point(W - margin, ty));

            // the chips: two groups; between them a white sticker with the memory type (DDR5: the power chip above it)
            bool ddr5 = memoryType.Contains("DDR5", StringComparison.OrdinalIgnoreCase);
            double chipTop = 7, chipBottom = tracesTop - 5, mid = W / 2, midHalf = soDimm ? 15 : 19;
            int perGroup = soDimm ? 2 : 4;
            foreach (var (from, to) in new[] { (margin, mid - midHalf), (mid + midHalf, W - margin) })
            {
                const double gap = 3;
                double cw = (to - from - gap * (perGroup - 1)) / perGroup;
                for (int i = 0; i < perGroup; i++)
                    DrawChip(dc, new Rect(from + i * (cw + gap), chipTop, cw, chipBottom - chipTop));
            }
            var label = new FormattedText(string.IsNullOrWhiteSpace(memoryType) ? "RAM" : memoryType.Trim(),
                CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                new Typeface(System.Windows.Documents.TextElement.GetFontFamily(host), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                7.5, StickerInk, VisualTreeHelper.GetDpi(host).PixelsPerDip);
            double sw = Math.Min(midHalf * 2 - 4, Math.Max(label.Width + 6, 22)), sh = 11;
            double sy = chipBottom - sh;
            if (ddr5)
                dc.DrawRoundedRectangle(ChipFace, null, new Rect(mid - 7, chipTop + 1, 14, 9), 1.5, 1.5);   // the PMIC
            else
                dc.DrawRoundedRectangle(ChipFace, null, new Rect(mid - 4.5, chipTop + 2, 9, 6), 1, 1);       // the SPD chip
            dc.DrawRoundedRectangle(Sticker, null, new Rect(mid - sw / 2, sy, sw, sh), 1.5, 1.5);
            dc.DrawText(label, new Point(mid - label.Width / 2, sy + (sh - label.Height) / 2));

            // the darker band above the contacts
            dc.DrawRectangle(Band, null, new Rect(0, tracesTop, W, traces));

            // the contacts: computed in physical pixels, so they all have exactly the same width at any scaling
            double s = VisualTreeHelper.GetDpi(host).DpiScaleX;
            int px(double v) => Math.Max(1, (int)Math.Round(v * s));
            int widthPx = (int)Math.Floor(W * s), marginPx = px(margin), notchPx = px(7), pitchPx = px(6), pinPx = px(4);
            int notchLeft = DimmLayout.NotchLeft(widthPx, marginPx, notchPx, DimmLayout.NotchPosition(memoryType, soDimm));
            var starts = DimmLayout.PinStarts(widthPx, marginPx, notchLeft, notchPx, pitchPx, pinPx);
            var guides = new GuidelineSet();
            foreach (int x in starts) { guides.GuidelinesX.Add(x / s); guides.GuidelinesX.Add((x + pinPx) / s); }
            dc.PushGuidelineSet(guides);
            foreach (int x in starts)
                dc.DrawRectangle(pin, null, new Rect(x / s, pinTop, pinPx / s, H - pinTop));
            dc.Pop();

            // the notch (cuts the board to the bottom) and the side cut-outs, in the card colour
            double nx = notchLeft / s, nw = notchPx / s;
            dc.DrawRoundedRectangle(card, null, new Rect(nx, H - 12, nw, 12 + 3), 3, 3);
            double cutH = soDimm ? 8 : 10, cutY = soDimm ? tracesTop - cutH - 1 : (H - cutH) / 2 - 3;
            dc.DrawRoundedRectangle(card, null, new Rect(-4, cutY, 8, cutH), 4, 4);
            dc.DrawRoundedRectangle(card, null, new Rect(W - 4, cutY, 8, cutH), 4, 4);
        }

        // the materials (the same in both themes): board, chips, sticker
        private static readonly Brush PcbBrush   = Gradient("#21403A", "#16292A", false);
        private static readonly Brush ChipFace   = Gradient("#2D333D", "#14181E", true);
        private static readonly Brush TopEdge    = Frozen(Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF));
        private static readonly Brush TraceBrush = Frozen(Color.FromArgb(0x24, 0x3F, 0xD0, 0xA0));
        private static readonly Brush Band       = Frozen(Color.FromArgb(0x40, 0x00, 0x00, 0x00));
        private static readonly Brush Sticker    = Frozen(Color.FromArgb(0xE0, 0xE8, 0xEC, 0xF2));
        private static readonly Brush StickerInk = Frozen(Color.FromRgb(0x1B, 0x22, 0x30));
        private static readonly Brush Sheen      = Frozen(Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF));
        private static readonly Brush Dot        = Frozen(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        private static Brush Gradient(string a, string b, bool diagonal)
        {
            var g = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(a), (Color)ColorConverter.ConvertFromString(b),
                                            new Point(0, 0), diagonal ? new Point(1, 1) : new Point(0, 1));
            g.Freeze(); return g;
        }

        /// <summary>A memory chip: black body with a faint sheen along the top and the orientation dot.</summary>
        private static void DrawChip(DrawingContext dc, Rect r)
        {
            if (r.Width < 4 || r.Height < 6) return;
            dc.DrawRoundedRectangle(ChipFace, null, r, 1.5, 1.5);
            dc.DrawRoundedRectangle(Sheen, null, new Rect(r.X + 2, r.Y + 2, Math.Max(1, r.Width - 4), 1.4), 0.6, 0.6);
            dc.DrawEllipse(Dot, null, new Point(r.X + 3.2, r.Bottom - 3.2), 1, 1);
        }
    }

    /// <summary>
    /// The memory slot on the board, drawn like a real one: the black socket at the bottom and the clips at the ends.
    /// Empty: the groove with the gold contacts and the key (notch) at the place of the memory generation, open clips and,
    /// above, the dotted outline of the module that would go there.
    /// Occupied (<see cref="Installed"/>): the module (<see cref="DimmPicture.DrawModule"/>) seated in the socket, clips closed.
    /// All slots have the same height, so modules and empty slots line up.
    /// </summary>
    public sealed class DimmSocketPicture : FrameworkElement
    {
        private const double PictureHeight = 74;

        /// <summary>Memory type (e.g. "DDR3") — moves the key in the groove to its place; empty = DDR4.</summary>
        public string MemoryType { get; set; } = "";
        public bool SoDimm { get; set; }

        /// <summary>Occupied slot: the module is drawn seated in the socket, with the clips closed.</summary>
        public bool Installed { get; set; }

        public DimmSocketPicture()
        {
            Loaded   += (_, _) => ThemeManager.ThemeChanged += OnTheme;
            Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnTheme;
        }

        private void OnTheme() => Dispatcher.BeginInvoke(new Action(InvalidateVisual));

        // the same width as DimmPicture
        protected override Size MeasureOverride(Size available)
        {
            double w = double.IsInfinity(available.Width) ? 220 : available.Width;
            if (SoDimm || DimmLayout.IsSoldered(MemoryType)) w = DimmLayout.CompactWidth(w, PictureHeight);
            return new Size(w, PictureHeight);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            if (W < 60 || H < 30) return;
            Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
            var line  = Res("DimmSocketBrush", Brushes.Gray);
            var body  = Res("DimmSocketBodyBrush", Brushes.Black);
            var pin   = Res("DimmPinBrush", Brushes.Goldenrod);

            // soldered memory has no socket: only the chips on the board, at the same height as the other slots
            if (DimmLayout.IsSoldered(MemoryType))
            {
                DimmPicture.DrawSoldered(dc, this, W, H, MemoryType);
                return;
            }

            const double latchW = 6, housingH = 15;
            double hx = latchW + 1, hw = W - 2 * (latchW + 1), hy = H - housingH;
            var edgePen = new Pen(line, 1); edgePen.Freeze();

            if (Installed)
            {
                // the module: slightly narrower than the socket, with the bottom part (the contacts) inside the socket
                double mx = Math.Round(hx + 3), mw = hw - 6, mh = hy + 7;
                dc.PushTransform(new TranslateTransform(mx, 0));
                DimmPicture.DrawModule(dc, this, mw, mh, MemoryType, SoDimm);
                dc.Pop();
                dc.DrawRoundedRectangle(body, edgePen, new Rect(hx, hy, hw, housingH), 2.5, 2.5);

                // closed clips: straight, with the hook over the edge of the module
                foreach (bool left in new[] { true, false })
                {
                    double x = left ? 0.5 : W - latchW - 0.5;
                    dc.DrawRoundedRectangle(line, null, new Rect(x, H - 30, latchW, 30), 2, 2);
                    dc.DrawRoundedRectangle(line, null, new Rect(left ? x + latchW - 2 : x - 2, H - 30, 4, 5), 1.5, 1.5);
                }
                return;
            }

            // the dotted outline of the module that would go into the slot
            var dash = new Pen(line, 1.2) { DashStyle = new DashStyle(new double[] { 3, 2.5 }, 0) };
            dash.Freeze();
            dc.PushOpacity(0.75);
            dc.DrawRoundedRectangle(null, dash, new Rect(hx + 0.6, 0.6, hw - 1.2, hy - 3), 3, 3);
            dc.Pop();

            // the socket: the dark body, with a fine edge (visible on the dark theme too)
            dc.DrawRoundedRectangle(body, edgePen, new Rect(hx, hy, hw, housingH), 2.5, 2.5);

            // the groove with the contacts, interrupted by the key at the generation's place (the same calculations as for the module)
            double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int px(double v) => Math.Max(1, (int)Math.Round(v * s));
            int widthPx = (int)Math.Floor(hw * s), marginPx = px(3), notchPx = px(7), pitchPx = px(6), pinPx = px(3);
            int notchLeft = DimmLayout.NotchLeft(widthPx, marginPx, notchPx, DimmLayout.NotchPosition(MemoryType, SoDimm));
            double slotY = hy + 5, slotH = 5;
            var groove = new SolidColorBrush(Color.FromArgb(0xFF, 0x0E, 0x10, 0x14)); groove.Freeze();
            double nx = hx + notchLeft / s, nw = notchPx / s;
            dc.DrawRoundedRectangle(groove, null, new Rect(hx + 4, slotY, nx - hx - 5, slotH), 1.5, 1.5);
            dc.DrawRoundedRectangle(groove, null, new Rect(nx + nw + 1, slotY, hx + hw - 4 - (nx + nw + 1), slotH), 1.5, 1.5);
            dc.PushOpacity(0.85);
            foreach (int x in DimmLayout.PinStarts(widthPx, marginPx, notchLeft, notchPx, pitchPx, pinPx))
            {
                double xx = hx + x / s;
                if (xx < hx + 5 || xx > hx + hw - 6) continue;
                dc.DrawRectangle(pin, null, new Rect(xx, slotY + 1.5, pinPx / s, 2));
            }
            dc.Pop();

            // the clips at the ends, open (tilted outwards)
            var latch = Res("DimmSocketBrush", Brushes.Gray);
            foreach (bool left in new[] { true, false })
            {
                double x = left ? 0.5 : W - latchW - 0.5;
                double pivotX = left ? x + latchW : x;
                dc.PushTransform(new RotateTransform(left ? -10 : 10, pivotX, H));
                dc.DrawRoundedRectangle(latch, null, new Rect(x, H - 24, latchW, 24), 2, 2);
                dc.Pop();
            }
        }
    }
}
