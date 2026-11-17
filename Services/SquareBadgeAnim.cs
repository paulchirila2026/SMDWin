using System;
using System.Collections.Generic;

namespace SMDWin.Services
{
    /// <summary>One square of the sidebar "test running" indicator, in the coordinates of the 12 × 12 box.
    /// <paramref name="Outline"/> = outline only (no fill).</summary>
    public readonly record struct BadgeCell(double X, double Y, double W, double H, double Opacity, bool Outline = false);

    /// <summary>
    /// Square animations of the "test running" indicator (sidebar: Temp &amp; Stress, Storage, RAM Memory).
    /// Each frame is computed only from the phase (0–1), with no state of its own: a skipped tick never leaves the animation
    /// "stuck", and the same frames can be drawn at any size (12 px sidebar, larger preview).
    /// </summary>
    public static class SquareBadgeAnim
    {
        /// <summary>Side of the box the coordinates are given in.</summary>
        public const double Box = 12;

        public const string Orbit  = "SquareOrbit";
        public const string Wave   = "SquareWave";
        public const string Fill   = "SquareFill";
        public const string Mosaic = "SquareMosaic";

        /// <summary>The default style of the indicator.</summary>
        public const string Default = Orbit;

        public static readonly (string Key, string Label)[] Styles =
        {
            (Orbit,  "Squares · orbit"),
            (Wave,   "Squares · wave"),
            (Fill,   "Square · filling up"),
            (Mosaic, "Squares · mosaic"),
        };

        public static bool IsSquareStyle(string? style)
            => style == Orbit || style == Wave || style == Fill || style == Mosaic;

        /// <summary>The duration of a full cycle.</summary>
        public static double PeriodSeconds(string? style) => style switch
        {
            Fill   => 1.9,
            Mosaic => 1.6,
            Wave   => 1.3,
            _      => 1.2,
        };

        /// <summary>Phase (0–1) at a given moment, measured in milliseconds from an arbitrary reference.</summary>
        public static double PhaseAt(string? style, long elapsedMs)
        {
            double periodMs = PeriodSeconds(style) * 1000.0;
            double p = (elapsedMs % periodMs) / periodMs;
            return p < 0 ? p + 1 : p;
        }

        /// <summary>The squares of one frame. An unknown style is drawn as <see cref="Default"/>.</summary>
        public static IReadOnlyList<BadgeCell> Cells(string? style, double phase)
        {
            phase -= Math.Floor(phase);
            return style switch
            {
                Wave   => WaveCells(phase),
                Fill   => FillCells(phase),
                Mosaic => MosaicCells(phase),
                _      => OrbitCells(phase),
            };
        }

        // 2 × 2: the light moves from one square to the next, clockwise, with a fading "tail"
        private static BadgeCell[] OrbitCells(double phase)
        {
            const double size = 5, far = Box - size;
            (double x, double y)[] pos = { (0, 0), (far, 0), (far, far), (0, far) };
            var cells = new BadgeCell[4];
            for (int i = 0; i < 4; i++)
            {
                double d = phase * 4 - i;            // how long since the light passed square i (in steps)
                d -= Math.Floor(d / 4) * 4;          // 0–4
                // lit when the light reaches it, fades in two steps, stays off one step, lights up again smoothly
                double op = d < 2 ? 1 - 0.8 * d / 2 : d < 3 ? 0.2 : 0.2 + 0.8 * (d - 3);
                cells[i] = new BadgeCell(pos[i].x, pos[i].y, size, size, op);
            }
            return cells;
        }

        // 3 squares in a row: they jump in turn, like a wave
        private static BadgeCell[] WaveCells(double phase)
        {
            const double size = 3, gap = 1.5, rest = 6.5, lift = 4.5;
            var cells = new BadgeCell[3];
            for (int i = 0; i < 3; i++)
            {
                double up = Math.Max(0, Math.Sin(2 * Math.PI * (phase - i * 0.16)));
                cells[i] = new BadgeCell(i * (size + gap), rest - lift * up, size, size, 0.45 + 0.55 * up);
            }
            return cells;
        }

        // one (outlined) square that fills from the bottom up, stays full for a moment, then empties
        private static BadgeCell[] FillCells(double phase)
        {
            const double edge = 0.75, pad = 2.6, inner = Box - 2 * pad;
            double level = phase < 0.7 ? Smooth(phase / 0.7) : 1;
            double op = phase < 0.85 ? 1 : (1 - phase) / 0.15;
            double h = inner * level;
            return new[]
            {
                new BadgeCell(edge, edge, Box - 2 * edge, Box - 2 * edge, 0.6, Outline: true),
                new BadgeCell(pad, pad + inner - h, inner, h, op),
            };
        }

        // 3 × 3: a diagonal wave of light
        private static BadgeCell[] MosaicCells(double phase)
        {
            const double size = 3.2, gap = 1.2;
            var cells = new BadgeCell[9];
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                {
                    double wave = 0.5 + 0.5 * Math.Cos(2 * Math.PI * (phase - (r + c) * 0.125));
                    cells[r * 3 + c] = new BadgeCell(c * (size + gap), r * (size + gap), size, size, 0.2 + 0.8 * wave);
                }
            return cells;
        }

        private static double Smooth(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
    }
}
