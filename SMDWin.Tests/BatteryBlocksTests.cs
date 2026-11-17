using System;
using System.Linq;
using SMDWin.Services;
using Xunit;

namespace SMDWin.Tests
{
    /// <summary>The battery drawn from blocks: the blocks are computed only from the percentage and the charging state.</summary>
    public class BatteryBlocksTests
    {
        [Fact]
        public void LitLevels_AddUpToThePercent()
        {
            foreach (int n in new[] { 5, 8, 10 })
                for (int p = 0; p <= 100; p++)
                {
                    var lit = BatteryBlocks.LitLevels(n, p);
                    Assert.Equal(n, lit.Length);
                    Assert.True(Math.Abs(lit.Sum() * 100.0 / n - p) < 0.0001, $"{n} blocuri, {p} %");
                    Assert.True(lit.All(v => v >= 0 && v <= 1));
                }
        }

        [Fact]
        public void LitLevels_FillInOrder_OnlyOnePartialBlock()
        {
            var lit = BatteryBlocks.LitLevels(10, 47);
            Assert.Equal("1|1|1|1|0.7|0|0|0|0|0", string.Join("|", lit.Select(v => Math.Round(v, 3))));
            for (int p = 0; p <= 100; p++)
                Assert.True(BatteryBlocks.LitLevels(10, p).Count(v => v > 0 && v < 1) <= 1);
        }

        [Fact]
        public void LitLevels_EmptyAndFull_AndOutOfRange()
        {
            Assert.True(BatteryBlocks.LitLevels(10, 0).All(v => v == 0));
            Assert.True(BatteryBlocks.LitLevels(10, 100).All(v => v == 1));
            Assert.True(BatteryBlocks.LitLevels(10, 140).All(v => v == 1));
            Assert.True(BatteryBlocks.LitLevels(10, -5).All(v => v == 0));
            Assert.True(BatteryBlocks.LitLevels(10, double.NaN).All(v => v == 0));
            Assert.Empty(BatteryBlocks.LitLevels(0, 50));
        }

        [Fact]
        public void Zones_FollowTheThresholds()
        {
            Assert.Equal(BatteryZone.Crit, BatteryBlocks.ZoneOf(0));
            Assert.Equal(BatteryZone.Crit, BatteryBlocks.ZoneOf(19));
            Assert.Equal(BatteryZone.Warn, BatteryBlocks.ZoneOf(20));
            Assert.Equal(BatteryZone.Warn, BatteryBlocks.ZoneOf(49));
            Assert.Equal(BatteryZone.Ok, BatteryBlocks.ZoneOf(50));
            Assert.Equal(BatteryZone.Ok, BatteryBlocks.ZoneOf(100));
        }

        [Fact]
        public void EveryStyle_HasOneBlockPerGridCell()
        {
            foreach (var (key, _) in BatteryBlocks.Styles)
            {
                var shape = BatteryBlocks.ShapeOf(key);
                var blocks = BatteryBlocks.Blocks(key, 63, false);
                Assert.Equal(shape.Count, blocks.Count);
                Assert.Equal(shape.Count, blocks.Select(b => (b.Col, b.Row)).Distinct().Count());
                Assert.True(blocks.All(b => b.Col >= 0 && b.Col < shape.Cols && b.Row >= 0 && b.Row < shape.Rows), key);
                Assert.True(BatteryBlocks.IsBlockStyle(key));
            }
            Assert.False(BatteryBlocks.IsBlockStyle("Classic"));
            Assert.False(BatteryBlocks.IsBlockStyle(null));
            Assert.True(BatteryBlocks.IsBlockStyle(BatteryBlocks.Default));
        }

        [Fact]
        public void Cells_FillFromTheLeft_InOneColour()
        {
            var blocks = BatteryBlocks.Blocks(BatteryBlocks.Cells, 35, false);
            Assert.Equal("0|1|2|3|4|5|6|7|8|9", string.Join("|", blocks.Select(b => b.Col)));
            Assert.True(blocks.All(b => b.Zone == BatteryZone.Warn));
            Assert.Equal(3, blocks.Count(b => b.Lit == 1));
            Assert.True(Math.Abs(blocks[3].Lit - 0.5) < 0.0001);
        }

        [Fact]
        public void Stack_FillsFromTheBottom()
        {
            var shape = BatteryBlocks.ShapeOf(BatteryBlocks.Stack);
            Assert.True(shape.Portrait);
            var blocks = BatteryBlocks.Blocks(BatteryBlocks.Stack, 25, false);
            Assert.Equal(shape.Rows - 1, blocks[0].Row);          // the first lit block is the bottom one
            Assert.Equal(0, blocks[^1].Row);                      // the last one is the top one
            Assert.Equal(1.0, blocks[0].Lit);
            Assert.Equal(0.0, blocks[^1].Lit);
        }

        [Fact]
        public void Zones_ColourEachBlockByItsPlace()
        {
            var blocks = BatteryBlocks.Blocks(BatteryBlocks.Zones, 100, false);
            Assert.Equal("Crit|Crit|Warn|Warn|Warn|Ok|Ok|Ok|Ok|Ok", string.Join("|", blocks.Select(b => b.Zone)));
            // the slot colour does not depend on the level
            var low = BatteryBlocks.Blocks(BatteryBlocks.Zones, 12, false);
            Assert.Equal(string.Join("|", blocks.Select(b => b.Zone)), string.Join("|", low.Select(b => b.Zone)));
        }

        [Fact]
        public void Pixel_FillsColumnByColumn_BottomFirst()
        {
            var blocks = BatteryBlocks.Blocks(BatteryBlocks.Pixel, 30, false);
            Assert.Equal("0,1|0,0|1,1|1,0|2,1|2,0|3,1|3,0|4,1|4,0", string.Join("|", blocks.Select(b => $"{b.Col},{b.Row}")));
            Assert.Equal(3, blocks.Count(b => b.Lit == 1));
            Assert.True(BatteryBlocks.ShapeOf(BatteryBlocks.Pixel).PartialByOpacity);
        }

        [Fact]
        public void Charging_MarksTheFirstBlockThatIsNotFull()
        {
            foreach (var (key, _) in BatteryBlocks.Styles)
            {
                var blocks = BatteryBlocks.Blocks(key, 42, true);
                Assert.Equal(1, blocks.Count(b => b.Charging));
                int at = blocks.ToList().FindIndex(b => b.Charging);
                Assert.True(blocks.Take(at).All(b => b.Lit == 1), key);
                Assert.True(blocks[at].Lit < 1, key);

                Assert.Equal(0, BatteryBlocks.Blocks(key, 42, false).Count(b => b.Charging));
                Assert.Equal(0, BatteryBlocks.Blocks(key, 100, true).Count(b => b.Charging));   // full: nothing pulses
                Assert.True(BatteryBlocks.Blocks(key, 0, true)[0].Charging);                    // empty: the first block
            }
        }

        [Fact]
        public void UnknownStyle_DrawsAsTheDefault()
        {
            Assert.Equal(BatteryBlocks.ShapeOf(BatteryBlocks.Default), BatteryBlocks.ShapeOf("ceva necunoscut"));
            Assert.Equal(BatteryBlocks.ShapeOf(BatteryBlocks.Default), BatteryBlocks.ShapeOf(null));
        }

        [Fact]
        public void Pulse_StaysVisible_AndLoopsWithoutAJump()
        {
            for (int i = 0; i <= 100; i++)
            {
                double o = BatteryBlocks.PulseOpacity(i / 100.0);
                Assert.True(o >= 0.25 - 0.0001 && o <= 1.0001);
            }
            Assert.True(Math.Abs(BatteryBlocks.PulseOpacity(0) - BatteryBlocks.PulseOpacity(0.9999)) < 0.01);
            Assert.True(Math.Abs(BatteryBlocks.PulseOpacity(0.5) - 1) < 0.0001);
            Assert.True(Math.Abs(BatteryBlocks.PulseOpacity(1.25) - BatteryBlocks.PulseOpacity(0.25)) < 0.0001);
        }

        [Fact]
        public void Split_GivesEqualWholePixelBlocks_Centred()
        {
            var (first, size) = BatteryBlocks.Split(101, 10, 2);
            Assert.Equal(8, size);                       // (101 − 18) / 10 = 8.3 → 8
            Assert.Equal(1, first);                      // used 98, 3 left → 1 on the left
            Assert.True(first + size * 10 + 2 * 9 <= 101);
            Assert.Equal(Math.Floor(size), size);

            Assert.Equal((0, 0), BatteryBlocks.Split(10, 10, 2));   // they do not fit
            Assert.Equal((0, 0), BatteryBlocks.Split(100, 0, 2));
            Assert.Equal((0, 0), BatteryBlocks.Split(0, 5, 2));
        }
    }
}
