using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Models;
using SMDWin.Services;
using Border      = System.Windows.Controls.Border;
using TextBlock   = System.Windows.Controls.TextBlock;
using FontFamily  = System.Windows.Media.FontFamily;
using HAlign      = System.Windows.HorizontalAlignment;
using VAlign      = System.Windows.VerticalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using UGrid       = System.Windows.Controls.Primitives.UniformGrid;
using WColor      = System.Windows.Media.Color;
using Brush       = System.Windows.Media.Brush;
using Brushes     = System.Windows.Media.Brushes;

namespace SMDWin.Views
{
    /// <summary>
    /// RAM page › Modules for memory soldered on the board (LPDDR4X / LPDDR5 / LPDDR5X). There are no slots and no
    /// sticks: Windows reports each memory channel as a "device" ("DIMM 0 #1", "Controller0-ChannelA"), so "4 of 4 slots
    /// populated" and four drawn sticks were wrong. Shown instead: one block with the chips on the board (one per reported
    /// channel, with its size), the total and the speed, plus the "Memory is soldered" block.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>true when the installed memory is soldered: LPDDR, or SMBIOS reports the form factor "Row of chips".</summary>
        internal static bool IsSolderedRam(IEnumerable<RamEntry> modules)
        {
            var filled = modules.Where(m => !m.IsEmpty).ToList();
            return filled.Count > 0 && filled.Any(m =>
                (m.MemoryType ?? "").StartsWith("LPDDR", StringComparison.OrdinalIgnoreCase)
                || (m.FormFactor ?? "").Contains("chip", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Fills the Modules card for soldered memory. Returns false if the memory is not soldered (normal slots).</summary>
        private bool BuildRamSolderedTiles(UGrid host, List<RamEntry> ordered)
        {
            if (!IsSolderedRam(ordered)) return false;
            var chips = ordered.Where(m => !m.IsEmpty).ToList();
            var first = chips[0];

            host.Columns = 1; host.Rows = 1;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
            var board = MakeRamBoardTile(chips);
            board.Margin = new Thickness(0, 0, 12, 0);
            row.Children.Add(board);
            var upgrade = MakeRamUpgradeTile(chips.Count, first);
            Grid.SetColumn(upgrade, 1);
            row.Children.Add(upgrade);
            host.Children.Add(row);

            if (FindName("TxtRamModulesSummary") is TextBlock sum)
                sum.Text = PT("on the board · {0} channels", chips.Count);
            if (FindName("TxtRamModulesRight") is TextBlock right)
                right.Text = (first.MemoryType ?? "").Trim();
            return true;
        }

        /// <summary>The block with the chips on the board: one chip per reported channel, the size on it; under them the total,
        /// the type and the speed, the maker and the part number.</summary>
        private Border MakeRamBoardTile(List<RamEntry> chips)
        {
            var tile = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12, 16, 12) };
            tile.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            var sp = new StackPanel { VerticalAlignment = VAlign.Center };
            tile.Child = sp;

            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var count = RamTxt(PT("{0} channels", chips.Count), Ui.FontXS, "TextSecondaryBrush", 0.8);
            DockPanel.SetDock(count, Dock.Right);
            top.Children.Add(count);
            var title = RamTxt(PT("Soldered on the board"), Ui.FontS, "TextSecondaryBrush");
            title.FontWeight = FontWeights.Bold;
            top.Children.Add(title);
            sp.Children.Add(top);

            // the chips: at most 8 per row (some boards report 8 sixteen-bit channels)
            var chipRow = new UGrid { Rows = (int)Math.Ceiling(chips.Count / 8.0), Columns = Math.Min(8, chips.Count),
                                      HorizontalAlignment = HAlign.Left, Margin = new Thickness(0, 0, 0, 14) };
            for (int i = 0; i < chips.Count; i++) chipRow.Children.Add(MakeRamChip(chips[i], i));
            sp.Children.Add(chipRow);

            double totalGb = chips.Sum(m => (double)m.CapacityBytes) / 1_073_741_824.0;
            var first = chips[0];
            var size = new TextBlock { FontFamily = TryFindResource("NumFont") as FontFamily, FontSize = 22, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            size.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            size.Inlines.Add(new System.Windows.Documents.Run(totalGb > 0 ? $"{totalGb:0.#} GB" : first.Capacity));
            string spd = first.ConfiguredMHz > 0 ? $"{first.ConfiguredMHz} MHz" : first.Speed;
            var det = new System.Windows.Documents.Run("  " + string.Join(" · ", new[] { first.MemoryType, spd }.Where(x => !string.IsNullOrWhiteSpace(x))))
                { FontSize = Ui.FontM, FontWeight = FontWeights.Normal };
            det.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextSecondaryBrush");
            size.Inlines.Add(det);
            sp.Children.Add(size);
            string maker = string.Join(" · ", new[] { first.Manufacturer, first.PartNumber?.Trim() }.Where(x => !string.IsNullOrWhiteSpace(x) && x != "—"));
            if (maker.Length > 0) sp.Children.Add(RamTxt(maker, Ui.FontS, "TextSecondaryBrush", 0.8));

            var tip = new List<string> { PT("The memory chips are soldered on the motherboard; Windows reports each channel separately.") };
            if (first.RatedMHz > 0 && first.ConfiguredMHz > 0 && first.RatedMHz != first.ConfiguredMHz)
                tip.Add(PT("Runs at {0} MHz · the chips report {1} MHz", first.ConfiguredMHz, first.RatedMHz));
            if (first.VoltageV > 0) tip.Add($"{PT("Voltage")} {first.VoltageV:F2} V");
            tile.ToolTip = string.Join("\n", tip);
            return tile;
        }

        /// <summary>One chip: a dark package with pins on two sides, the size written on it, the channel number under it.</summary>
        private FrameworkElement MakeRamChip(RamEntry m, int index)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 10, 4), Width = 64 };
            var pinBrush = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray;

            UGrid Pins()
            {
                var pins = new UGrid { Rows = 1, Columns = 6, Height = 4, Margin = new Thickness(7, 0, 7, 0) };
                for (int p = 0; p < 6; p++)
                    pins.Children.Add(new Border { Background = pinBrush, Opacity = 0.45, Margin = new Thickness(1.5, 0, 1.5, 0), CornerRadius = new CornerRadius(1) });
                return pins;
            }

            sp.Children.Add(Pins());
            // the package keeps its dark colour on both themes (real chips are black); a small dot marks pin 1
            var package = new Border
            {
                Height = 40, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(WColor.FromRgb(0x23, 0x2A, 0x36)),
                BorderBrush = new SolidColorBrush(WColor.FromRgb(0x3B, 0x45, 0x57)),
            };
            var inner = new Grid();
            inner.Children.Add(new System.Windows.Shapes.Ellipse { Width = 4, Height = 4, Fill = new SolidColorBrush(WColor.FromRgb(0x6B, 0x77, 0x8C)),
                                                                   HorizontalAlignment = HAlign.Left, VerticalAlignment = VAlign.Top, Margin = new Thickness(5, 5, 0, 0) });
            var gb = new TextBlock
            {
                Text = m.CapacityBytes > 0 ? $"{m.CapacityBytes / 1_073_741_824.0:0.#} GB" : m.Capacity,
                FontFamily = TryFindResource("NumFont") as FontFamily, FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(WColor.FromRgb(0xE6, 0xEB, 0xF2)),
                HorizontalAlignment = HAlign.Center, VerticalAlignment = VAlign.Center,
            };
            inner.Children.Add(gb);
            package.Child = inner;
            sp.Children.Add(package);
            sp.Children.Add(Pins());

            var ch = RamTxt(PT("Channel {0}", index + 1), Ui.FontXS, "TextSecondaryBrush", 0.75);
            ch.HorizontalAlignment = HAlign.Center; ch.Margin = new Thickness(0, 4, 0, 0);
            sp.Children.Add(ch);
            string bios = (m.SlotLabel ?? "").Trim();
            sp.ToolTip = string.Join("\n", new[] { $"{PT("Channel {0}", index + 1)}: {m.Capacity} {m.MemoryType}", bios, m.BankLabel?.Trim() ?? "" }
                                           .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
            return sp;
        }
    }
}
