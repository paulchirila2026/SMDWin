using System;
using System.Collections.Generic;

namespace SMDWin.Services
{
    /// <summary>Colour zone of the charge level: below 20 % critical, below 50 % attention, otherwise fine.</summary>
    public enum BatteryZone { Ok, Warn, Crit }

    /// <summary>
    /// One block of the drawn battery: position in the grid (column, row — row 0 is the top), how much of it is lit (0–1),
    /// colour zone and whether it is the block charging now (the one that pulses).
    /// </summary>
    public readonly record struct BatteryBlock(int Col, int Row, double Lit, BatteryZone Zone, bool Charging);

    /// <summary>Shape of a variant: block grid, orientation and how a partly lit block is shown.</summary>
    public readonly record struct BatteryBlocksShape(int Cols, int Rows, bool Portrait, bool PartialByOpacity)
    {
        public int Count => Cols * Rows;
    }

    /// <summary>
    /// Battery drawn from blocks (Power page): four variants, all computed only from the percentage and the charging state,
    /// with no state of their own — the same block list can be drawn at any size (card, preview).
    /// </summary>
    public static class BatteryBlocks
    {
        public const string Cells = "BlockCells";
        public const string Stack = "BlockStack";
        public const string Zones = "BlockZones";
        public const string Pixel = "BlockPixel";

        /// <summary>The default variant of the Power page.</summary>
        public const string Default = Cells;

        public static readonly (string Key, string Label)[] Styles =
        {
            (Cells, "Blocks · cells"),
            (Stack, "Blocks · stack"),
            (Zones, "Blocks · colour zones"),
            (Pixel, "Blocks · 2 × 5 grid"),
        };

        public static bool IsBlockStyle(string? style)
            => style == Cells || style == Stack || style == Zones || style == Pixel;

        /// <summary>An unknown variant has the shape of the default variant.</summary>
        public static BatteryBlocksShape ShapeOf(string? style) => style switch
        {
            Stack => new BatteryBlocksShape(1, 8, Portrait: true, PartialByOpacity: false),
            Pixel => new BatteryBlocksShape(5, 2, Portrait: false, PartialByOpacity: true),
            _     => new BatteryBlocksShape(10, 1, Portrait: false, PartialByOpacity: false),
        };

        public static BatteryZone ZoneOf(double percent)
            => percent < 20 ? BatteryZone.Crit : percent < 50 ? BatteryZone.Warn : BatteryZone.Ok;

        /// <summary>
        /// How much of each block is lit, in fill order: block i covers the range [i/n, (i+1)/n] of the charge.
        /// Sum of the values × 100 / n gives back the percentage.
        /// </summary>
        public static double[] LitLevels(int count, double percent)
        {
            var lit = new double[Math.Max(0, count)];
            double p = Math.Clamp(double.IsNaN(percent) ? 0 : percent, 0, 100) / 100.0 * lit.Length;
            for (int i = 0; i < lit.Length; i++) lit[i] = Math.Clamp(p - i, 0, 1);
            return lit;
        }

        /// <summary>
        /// Blocks of a variant in fill order (the first is the one that lights up first).
        /// With "colour zones" each block takes the colour of its place on the scale; otherwise all take the level colour.
        /// </summary>
        public static IReadOnlyList<BatteryBlock> Blocks(string? style, double percent, bool charging)
        {
            var shape = ShapeOf(style);
            int n = shape.Count;
            var lit = LitLevels(n, percent);
            var level = ZoneOf(percent);

            // the block charging now: the first one that is not full
            int chargingAt = -1;
            if (charging)
                for (int i = 0; i < n; i++)
                    if (lit[i] < 1) { chargingAt = i; break; }

            var list = new List<BatteryBlock>(n);
            for (int i = 0; i < n; i++)
            {
                int col, row;
                if (style == Stack)      { col = 0; row = shape.Rows - 1 - i; }          // bottom to top
                else if (style == Pixel) { col = i / 2; row = i % 2 == 0 ? 1 : 0; }      // in columns: bottom, then top
                else                     { col = i; row = 0; }                           // left to right
                // colour of the place: the block's upper bound (block 2 of 10 ends at 20 % → still red)
                var zone = style == Zones ? ZoneOf((i + 1) * 100.0 / n - 0.001) : level;
                list.Add(new BatteryBlock(col, row, lit[i], zone, i == chargingAt));
            }
            return list;
        }

        /// <summary>Duration of one pulse of the charging block.</summary>
        public const double PulseSeconds = 1.4;

        /// <summary>Opacity of the charging block at the given phase (0–1): rises and falls smoothly, with no jump at the end.</summary>
        public static double PulseOpacity(double phase)
        {
            phase -= Math.Floor(phase);
            return 0.25 + 0.75 * (0.5 - 0.5 * Math.Cos(2 * Math.PI * phase));
        }

        /// <summary>
        /// Splits a length into <paramref name="count"/> equal pieces on whole pixels, with the given gap between them.
        /// What remains after rounding is split between the ends, so the blocks stay centred.
        /// Returns the start of the first piece and the common size; size 0 when they do not fit.
        /// </summary>
        public static (double First, double Size) Split(double length, int count, double gap)
        {
            if (count <= 0 || length <= 0) return (0, 0);
            double size = Math.Floor((length - gap * (count - 1)) / count);
            if (size < 1) return (0, 0);
            double used = size * count + gap * (count - 1);
            return (Math.Floor((length - used) / 2), size);
        }
    }
}
