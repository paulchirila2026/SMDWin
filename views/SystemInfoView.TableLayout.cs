using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Models;
using SMDWin.Services;
using WpfHAlign = System.Windows.HorizontalAlignment;
using WpfVAlign = System.Windows.VerticalAlignment;
using WpfPanel  = System.Windows.Controls.Panel;

namespace SMDWin.Views
{
    /// <summary>
    /// System Information, the default "header + rows" layout: the computer at the top (and, depending on the variant,
    /// Windows, the license, power and "To check"), below it one card per component. All rows have the same columns
    /// (icon · name · three cells · state), each cell with a fixed column for the icon, so the texts are aligned vertically.
    /// Simple drawings in the cells: the cores and the video memory as blocks, the memory slots, the partition map with the
    /// disk health, the screens at their proportions, the battery level, the Windows update timeline.
    /// The rows share the remaining height — no empty space is left.
    /// <list type="bullet">
    /// <item>"Table" (default, D1): a header of three equal boxes, a table with a state column;</item>
    /// <item>"TableGroups" (D2): a header on one band, the problems as labels; the table in two groups, Hardware and Windows
    /// and security, without the state column.</item>
    /// </list>
    /// The cards are built as before (the same data, PDF and "Copy all" unchanged) and taken apart
    /// (<see cref="HeroCards.Dismantle"/>). The texts: the "SysInfo" section in Languages/*.json.
    /// </summary>
    public partial class SystemInfoView
    {
        private static bool UseTableLayout => LayoutChoice is "" or "Table" or "TableGroups";
        private static bool TableGroups => LayoutChoice == "TableGroups";

        private const string TableLayoutTag = "TableLayout";
        private const double TableNarrowBelow = 1150;   // below this width, each row keeps two facts
        private const double TableNameW = 230, TableStatusW = 140, TableIconBox = 40, TableCellGutter = 30, TableRowGap = 8;

        /// <summary>
        /// A cell of a row. The small icon (optional) sits in its own column, of the same width in all cells, so the texts start
        /// at the same X on all rows (LAN under Build, USB under Driver etc.).
        /// </summary>
        private sealed class TableCell
        {
            public FrameworkElement Content = null!;
            public string? IconKey;
            public string IconBrush = "TextSecondaryBrush";
            public int Span = 1;                     // how many cells it spans (Protection: the labels on two cells, so they do not wrap onto two lines)
        }

        private sealed class TableRowData
        {
            public string Tag = "";
            public string IconKey = "System";
            public string Caption = "";
            public FrameworkElement? Title;
            public readonly List<TableCell> Cells = new();
            public FrameworkElement? Wide;           // content across the width of the three cells (Storage: the partition map)
            public string? Status;
            public string StatusBrush = "StatusGoodBrush";
            public bool IsGroupHeader;
            public object? Tip;
        }

        private Grid? _tableLayout;
        private FrameworkElement? _tableHeaderFull, _tableHeaderStrip;
        private Grid? _tableGrid;
        private Border? _tableCard;
        private readonly List<FrameworkElement> _tableThirdCells = new();
        private bool _tableNarrow;

        // ── Construire ───────────────────────────────────────────────────────────────────────────────────────────────
        private void BuildTableLayout(List<Border> cards)
        {
            var host = _allCardsHost;
            if (host == null) return;
            foreach (var old in host.Children.OfType<Grid>().Where(g => g.Tag as string is TableLayoutTag or MachineLayoutTag || g.Tag as string == HeroCards.RowTag).ToList())
            {
                old.Children.Clear();
                host.Children.Remove(old);
            }
            _tableThirdCells.Clear();
            EndFocusHighlight();

            var parts = new Dictionary<string, HeroCards.CardParts>();
            foreach (var card in cards)
                if (card.Tag is string tag && !parts.ContainsKey(tag))
                    parts[tag] = HeroCards.Dismantle(card);

            var rows = new List<TableRowData>();
            void Add(TableRowData? r) { if (r != null) rows.Add(r); }
            if (TableGroups) rows.Add(new TableRowData { IsGroupHeader = true, Caption = SI("GroupHardware", "Hardware") });
            Add(RowCpu(parts));
            Add(RowMemory(parts));
            Add(RowStorage(parts));
            Add(RowGraphics(parts));
            Add(RowDisplays(parts));
            if (!TableGroups) Add(RowPower(parts));
            Add(RowNetwork(parts));
            Add(RowPeripherals(parts));
            if (TableGroups) rows.Add(new TableRowData { IsGroupHeader = true, Caption = SI("GroupWindows", "Windows and security") });
            Add(RowWindows(parts));
            Add(RowProtection(parts));
            if (TableGroups) Add(RowPower(parts));

            var header = TableGroups ? BuildBandHeader(parts) : BuildBoxHeader(parts);
            // on a short window (laptop) the big header gives way to a single line with only what needs attention (FitTableLayout)
            _tableHeaderFull = header;
            _tableHeaderStrip = BuildAttentionStrip();
            _tableHeaderStrip.Visibility = Visibility.Collapsed;
            _tableGrid = BuildTable(rows);
            // the rows are separate cards; their container has no background or border
            _tableCard = new Border { Child = _tableGrid, VerticalAlignment = WpfVAlign.Top };

            var layout = new Grid { Tag = TableLayoutTag, Margin = new Thickness(0, 0, 0, 12) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.Children.Add(header);
            layout.Children.Add(_tableHeaderStrip);
            Grid.SetRow(_tableCard, 2);
            layout.Children.Add(_tableCard);
            host.Children.Insert(0, layout);
            _tableLayout = layout;
            _heroCols = 0;
            ApplyTableNarrow(_tableNarrow);
        }

        /// <summary>The rows: one card per component, with the same columns (icon · name · three cells · state).</summary>
        private Grid BuildTable(List<TableRowData> rows)
        {
            var table = new Grid();
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                bool last = i == rows.Count - 1;

                if (r.IsGroupHeader)
                {
                    var g = HeroText(r.Caption.ToUpperInvariant(), Ui.FontXS, "TextSecondaryBrush", semibold: true);
                    var gh = new Border { Tag = "Group", Child = g, Padding = new Thickness(4, i == 0 ? 0 : 6, 0, 6) };
                    Grid.SetRow(gh, i);
                    table.Children.Add(gh);
                    continue;
                }

                // Tag = the component key: FocusCard (a click from the Dashboard) finds the row
                var card = new Border
                {
                    Tag = r.Tag, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
                    Padding = new Thickness(18, 10, 18, 10), Margin = new Thickness(0, 0, 0, last ? 0 : TableRowGap), ToolTip = r.Tip,
                };
                card.SetResourceReference(Border.BackgroundProperty, "CardSurfaceBrush");
                card.SetResourceReference(Border.BorderBrushProperty, "CardEdgeBrush");
                Grid.SetRow(card, i);
                table.Children.Add(card);

                var row = new Grid { VerticalAlignment = WpfVAlign.Center };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TableIconBox + 16) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TableNameW) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = TableGroups ? new GridLength(0) : new GridLength(TableStatusW) });
                card.Child = row;

                row.Children.Add(TableIcon(r.IconKey));

                var names = new StackPanel { VerticalAlignment = WpfVAlign.Center, Margin = new Thickness(0, 0, 16, 0) };
                if (r.Caption.Length > 0) names.Children.Add(HeroText(r.Caption, Ui.FontXS, "TextSecondaryBrush"));
                if (r.Title != null)
                {
                    if (r.Title is TextBlock tb) { tb.FontSize = Ui.FontL + HeroCards.FontBoost; HeroCards.TwoLines(tb); }
                    names.Children.Add(r.Title);
                }
                Grid.SetColumn(names, 1);
                row.Children.Add(names);

                if (r.Wide != null)
                {
                    r.Wide.Margin = new Thickness(0);   // the content sets its own columns, aligned with the cells
                    r.Wide.VerticalAlignment = WpfVAlign.Center;
                    Grid.SetColumn(r.Wide, 2); Grid.SetColumnSpan(r.Wide, 3);
                    row.Children.Add(r.Wide);
                }
                else
                {
                    // the cells are placed one after another; the one that lands in the third column disappears on a narrow window
                    int col = 0;
                    foreach (var cell in r.Cells)
                    {
                        if (col >= 3) break;
                        int span = Math.Clamp(cell.Span, 1, 3 - col);
                        var holder = CellHolder(cell);
                        Grid.SetColumn(holder, 2 + col);
                        Grid.SetColumnSpan(holder, span);
                        row.Children.Add(holder);
                        if (col == 2) _tableThirdCells.Add(holder);
                        col += span;
                    }
                }

                if (!TableGroups && r.Status != null)
                {
                    var pill = StatusPill(r.Status, r.StatusBrush);
                    Grid.SetColumn(pill, 5);
                    row.Children.Add(pill);
                }
            }
            return table;
        }

        /// <summary>A theme colour, with the given transparency (the background of the labels and icons).</summary>
        private static SolidColorBrush Tinted(string key, double opacity)
        {
            var c = (System.Windows.Application.Current?.TryFindResource(key) as SolidColorBrush)?.Color
                    ?? System.Windows.Media.Color.FromRgb(128, 128, 128);
            var b = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)Math.Round(255 * opacity), c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        /// <summary>The component icon: a simple line in the accent colour, in a small square with a very light accent background.</summary>
        private static FrameworkElement TableIcon(string key)
        {
            var glyph = Glyph(key, "AccentBrush", 22);
            return new Border
            {
                Width = TableIconBox, Height = TableIconBox, CornerRadius = new CornerRadius(10), Child = glyph,
                HorizontalAlignment = WpfHAlign.Left, VerticalAlignment = WpfVAlign.Center, Background = Tinted("AccentBrush", 0.13),
            };
        }

        /// <summary>An icon from the app set (InfoCardBuilder.Icons), coloured with the given brush.</summary>
        private static FrameworkElement Glyph(string key, string brushKey, double size)
        {
            var glyph = SpecSheet.IconGlyph(key, SpecSheet.Hue(key), size);
            if (glyph is Viewbox vb && vb.Child is Canvas cv)
                foreach (var p in cv.Children.OfType<System.Windows.Shapes.Path>())
                    p.SetResourceReference(p.StrokeThickness > 0 ? System.Windows.Shapes.Shape.StrokeProperty   // older line icon
                                                                 : System.Windows.Shapes.Shape.FillProperty, brushKey);
            return glyph;
        }

        /// <summary>Cell: a fixed column for the icon (empty if it has none) and the content.</summary>
        private static FrameworkElement CellHolder(TableCell cell)
        {
            var g = new Grid { VerticalAlignment = WpfVAlign.Center, Margin = new Thickness(0, 0, 14, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TableCellGutter) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = cell.IconKey != null ? Glyph(cell.IconKey, cell.IconBrush, 20) : null;
            if (icon != null)
            {
                icon.HorizontalAlignment = WpfHAlign.Left;
                icon.VerticalAlignment = WpfVAlign.Center;
                icon.Margin = new Thickness(0);
                g.Children.Add(icon);
            }
            cell.Content.VerticalAlignment = WpfVAlign.Center;
            cell.Content.Margin = new Thickness(0);
            Grid.SetColumn(cell.Content, 1);
            g.Children.Add(cell.Content);
            return g;
        }

        /// <summary>The row state as a label: coloured text on a very light background of the same colour.</summary>
        private static FrameworkElement StatusPill(string text, string brushKey)
        {
            var t = HeroText(text, Ui.FontS, brushKey, semibold: true);
            return new Border
            {
                CornerRadius = new CornerRadius(12), Padding = new Thickness(11, 3, 11, 4), Child = t,
                Background = Tinted(brushKey, 0.15), HorizontalAlignment = WpfHAlign.Right, VerticalAlignment = WpfVAlign.Center,
            };
        }

        // ── The header ──────────────────────────────────────────────────────────────────────────────────────────────────
        /// <summary>The short header for a low window: "To check · 3" and the problems as small labels, on one line.</summary>
        private FrameworkElement BuildAttentionStrip()
        {
            var items = GetAttentionItems();
            var row = new WrapPanel { VerticalAlignment = WpfVAlign.Center };
            string title = SI("AttentionTitle", "To check");
            var head = HeroText(items.Count > 0 ? $"{title} · {items.Count}" : title, Ui.FontS, "TextSecondaryBrush", semibold: true);
            head.VerticalAlignment = WpfVAlign.Center;
            head.Margin = new Thickness(0, 3, 10, 3);
            row.Children.Add(head);
            if (items.Count == 0) items.Add((SI("AttentionNone", "Nothing needs attention"), "StatusGoodBrush"));
            foreach (var (t, brush) in items)
            {
                var chip = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(9, 3, 11, 3), Margin = new Thickness(0, 2, 8, 2), BorderThickness = new Thickness(1) };
                chip.SetResourceReference(Border.BorderBrushProperty, brush);
                var sp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = WpfVAlign.Center };
                dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
                sp.Children.Add(dot);
                sp.Children.Add(HeroText(t, Ui.FontS, "TextPrimaryBrush"));
                chip.Child = sp;
                row.Children.Add(chip);
            }
            var box = HeaderBox(row);
            box.Padding = new Thickness(18, 8, 18, 8);
            return box;
        }

        private static Border HeaderBox(UIElement child)
        {
            var b = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(18, 14, 18, 14), Child = child };
            b.SetResourceReference(Border.BackgroundProperty, "CardSurfaceBrush");
            b.SetResourceReference(Border.BorderBrushProperty, "CardEdgeBrush");
            return b;
        }

        /// <summary>The computer: the drawing, the name and, on two columns, the serial, the chassis, the motherboard and the PC name.</summary>
        private FrameworkElement MachineBlock(Dictionary<string, HeroCards.CardParts> parts, double artW, double artH, bool facts)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (!parts.TryGetValue("Motherboard", out var board)) return grid;
            if (board.Picture != null)
            {
                board.Picture.Width = artW; board.Picture.Height = artH;
                board.Picture.Margin = new Thickness(0, 0, 18, 0);
                board.Picture.VerticalAlignment = WpfVAlign.Center;
                grid.Children.Add(board.Picture);
            }
            var text = new StackPanel { VerticalAlignment = WpfVAlign.Center };
            if (board.Title != null) text.Children.Add(board.Title);
            if (facts && board.Facts.Count > 0)
            {
                var list = board.Facts.Count > 4 ? board.Facts.Take(3).Append(board.Facts[^1]).ToList() : board.Facts;
                var two = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, Margin = new Thickness(0, 6, 0, 0) };
                foreach (var f in list) { f.Margin = new Thickness(0, 0, 14, 4); two.Children.Add(f); }
                text.Children.Add(two);
            }
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            return grid;
        }

        private string PowerTitle(Dictionary<string, HeroCards.CardParts> parts)
            => (parts.TryGetValue("Battery", out var bat) ? bat.Title?.Text : parts.TryGetValue("Bolt", out var mains) ? mains.Title?.Text : null) ?? "";

        private static string UptimeText() => DisplayFormat.Uptime(PowerOnClock.SinceOn);

        /// <summary>D1: three equal boxes — the computer · Windows, license, power, booted from · "To check".</summary>
        private FrameworkElement BuildBoxHeader(Dictionary<string, HeroCards.CardParts> parts)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            grid.Children.Add(HeaderBox(MachineBlock(parts, 104, 80, facts: true)));

            var kv = new Grid { VerticalAlignment = WpfVAlign.Center };
            kv.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            kv.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            void Line(string label, string value, string brush = "TextPrimaryBrush")
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                int r = kv.RowDefinitions.Count;
                kv.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var l = HeroText(label, Ui.FontS, "TextSecondaryBrush"); l.Margin = new Thickness(0, 0, 14, 7);
                Grid.SetRow(l, r); kv.Children.Add(l);
                var v = HeroText(value, Ui.FontS, brush, semibold: true); v.Margin = new Thickness(0, 0, 0, 7); v.ToolTip = value;
                Grid.SetRow(v, r); Grid.SetColumn(v, 1); kv.Children.Add(v);
            }
            if (_winInfo is { } w)
            {
                string os = string.Join(" · ", new[] { w.ProductName, w.DisplayVersion, w.Architecture }.Where(x => !string.IsNullOrWhiteSpace(x)));
                Line("Windows", os);
                Line(Ws("SecLicense"), w.Licensed ? Ws("Licensed") : Ws("Unlicensed"), w.Licensed ? "StatusGoodBrush" : "StatusProblemBrush");
            }
            Line(SI("HdrPower", "Power"), PowerTitle(parts));
            Line(SI("HdrUptime", "Running for"), UptimeText());
            var box2 = HeaderBox(kv);
            Grid.SetColumn(box2, 2);
            grid.Children.Add(box2);

            if (GetAttentionItems().Count > 0)   // nothing to check → no third box
            {
                var box3 = HeaderBox(BuildAttentionPanel(withLine: false));
                Grid.SetColumn(box3, 4);
                grid.Children.Add(box3);
            }
            return grid;
        }

        /// <summary>D2: a single band — the computer, the serial and the board on one row, the problems as coloured labels.</summary>
        private FrameworkElement BuildBandHeader(Dictionary<string, HeroCards.CardParts> parts)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var machine = MachineBlock(parts, 86, 66, facts: false);
            if (machine is Grid mg && mg.Children.OfType<StackPanel>().FirstOrDefault() is { } text && parts.TryGetValue("Motherboard", out var board))
            {
                string line = string.Join(" · ", board.Facts.Select(f => (f as StackPanel)?.Children.OfType<TextBlock>().Skip(1).FirstOrDefault()?.Text)
                                                   .Where(x => !string.IsNullOrWhiteSpace(x)).Take(4)
                                                   .Append(string.Format(SI("UptimeShort", "running for {0}"), UptimeText())));
                var sub = HeroText(line, Ui.FontS, "TextSecondaryBrush");
                sub.ToolTip = line;
                text.Children.Add(sub);
            }
            grid.Children.Add(machine);

            var chips = new WrapPanel { VerticalAlignment = WpfVAlign.Center, HorizontalAlignment = WpfHAlign.Right, MaxWidth = 620 };
            var items = GetAttentionItems();
            if (items.Count == 0) items.Add((SI("AttentionNone", "Nothing needs attention"), "StatusGoodBrush"));
            foreach (var (t, brush) in items)
            {
                var chip = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 5, 12, 5), Margin = new Thickness(8, 3, 0, 3), BorderThickness = new Thickness(1) };
                chip.SetResourceReference(Border.BorderBrushProperty, brush);
                chip.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
                var sp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = WpfVAlign.Center };
                dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
                sp.Children.Add(dot);
                sp.Children.Add(HeroText(t, Ui.FontS, "TextPrimaryBrush"));
                chip.Child = sp;
                chips.Children.Add(chip);
            }
            Grid.SetColumn(chips, 1);
            grid.Children.Add(chips);
            return HeaderBox(grid);
        }

        // ── The rows ────────────────────────────────────────────────────────────────────────────────────────────────
        private static TableRowData NewRow(string tag, HeroCards.CardParts p, string iconKey)
            => new() { Tag = tag, IconKey = iconKey, Caption = p.Caption, Title = p.Title };

        private static TableCell Cell(FrameworkElement content, string? iconKey = null, string iconBrush = "TextSecondaryBrush")
            => new() { Content = content, IconKey = iconKey, IconBrush = iconBrush };

        /// <summary>The value (the bold text) of a fact in the card.</summary>
        private static string FactValue(FrameworkElement fact)
            => (fact as StackPanel)?.Children.OfType<TextBlock>().Skip(1).FirstOrDefault()?.Text ?? "";

        /// <summary>The small label above and a drawing below (blocks, slots, screens…).</summary>
        private static FrameworkElement VisualCell(string caption, UIElement visual, string captionBrush = "TextSecondaryBrush")
        {
            var sp = new StackPanel();
            var cap = HeroText(caption, Ui.FontXS, captionBrush);
            cap.Margin = new Thickness(0, 0, 0, 5);
            sp.Children.Add(cap);
            sp.Children.Add(visual);
            sp.ToolTip = caption;
            return sp;
        }

        /// <summary>
        /// Equal blocks on a row (the processor cores, GB of video memory, health in tenths): the first
        /// <paramref name="lit"/> coloured, the rest in the "free" colour. At most 16 blocks.
        /// </summary>
        private static FrameworkElement Blocks(int count, int lit, string fillKey, double height = 8)
        {
            count = Math.Clamp(count, 1, 16);
            // the blocks take the cell width, at most 30 px each, left-aligned: on a narrow cell they all shrink together (they stay
            // whole), the last one is not cut
            var holder = new Grid();
            holder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = count * 30 });
            holder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) });
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = count };
            holder.Children.Add(grid);
            var off = Tinted("TextSecondaryBrush", 0.22);
            for (int i = 0; i < count; i++)
            {
                var b = new Border { Height = height, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 3, 0) };
                if (i < lit) b.SetResourceReference(Border.BackgroundProperty, fillKey); else b.Background = off;
                grid.Children.Add(b);
            }
            return holder;
        }

        /// <summary>The processor: the cores as blocks (like the video memory), then the frequency and the cache.</summary>
        private TableRowData? RowCpu(Dictionary<string, HeroCards.CardParts> parts)
        {
            if (!parts.TryGetValue("Cpu", out var p)) return null;
            var r = NewRow("Cpu", p, "Cpu");
            var s = _tileSummary;
            int cores = 0, threads = s?.CpuThreads ?? 0;
            var nums = System.Text.RegularExpressions.Regex.Matches(s?.CpuCores ?? "", @"\d+");
            if (nums.Count > 0) int.TryParse(nums[0].Value, out cores);
            if (threads <= 0 && nums.Count > 1) int.TryParse(nums[1].Value, out threads);
            if (threads <= 0) threads = Environment.ProcessorCount;
            if (cores > 0)
            {
                // hybrid processors: how many performance and efficiency cores ("6 P + 8 E")
                var hybrid = CpuPackage.HybridCores(s?.Cpu, cores, threads);
                string coresText = hybrid is (int pc, int ec)
                    ? string.Format(SI("CellCoresHybrid", "{0} P + {1} E cores · {2} threads"), pc, ec, threads)
                    : string.Format(SI("CellCores", "{0} cores · {1} threads"), cores, threads);
                var cell = Cell(VisualCell(coresText, Blocks(cores, cores, "AccentBrush")));
                if (hybrid is (int pc2, int ec2))
                    ((FrameworkElement)cell.Content).ToolTip = string.Format(SI("CoresHybridTip", "{0} performance cores and {1} efficiency cores"), pc2, ec2);
                r.Cells.Add(cell);
            }
            // the base frequency with the turbo (from the hardware database, when the processor is in it)
            double? boost = HardwareDatabaseService.LookupCpu(s?.Cpu ?? "")?.BoostClockGhz;
            // the rest of the facts, without the one with the cores (it is already drawn)
            foreach (var f in p.Facts.Where(f => cores == 0 || !FactValue(f).StartsWith($"{cores} ", StringComparison.Ordinal)).Take(3 - r.Cells.Count))
            {
                string v = FactValue(f);
                // Windows' "maximum clock" of an i5-10210U is 2.1 GHz, its base is 1.6 GHz — Intel writes the base in the
                // name ("@ 1.60GHz"), which is the right number to show as the base
                if (v.EndsWith("GHz", StringComparison.OrdinalIgnoreCase) && !v.Contains('–')
                    && System.Text.RegularExpressions.Regex.Match(s?.Cpu ?? "", @"@\s*(\d+(?:\.\d+)?)\s*GHz", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } baseM
                    && double.TryParse(baseM.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double baseGhz))
                    v = baseGhz.ToString("0.0#", System.Globalization.CultureInfo.CurrentCulture) + " GHz";
                if (boost is > 0.5 && v.EndsWith("GHz", StringComparison.OrdinalIgnoreCase) && !v.Contains('–'))
                    r.Cells.Add(Cell(HeroFact("Clock", SpecSheet.Hue("Cpu"), string.Format(SI("CellClockTurbo", "{0} · turbo {1:0.0#} GHz"), v, boost.Value),
                        SI("CellClockTurboLabel", "Base / turbo frequency"))));
                else r.Cells.Add(Cell(f));
            }
            r.Status = SI("StatusOk", "OK");
            return r;
        }

        /// <summary>The memory: the board slots (module = capacity on a solid background, free slot = dotted outline), the speed, the channels.</summary>
        private TableRowData? RowMemory(Dictionary<string, HeroCards.CardParts> parts)
        {
            if (!parts.TryGetValue("Ram", out var p)) return null;
            var r = NewRow("Ram", p, "Ram");
            var slots = _tileRams.Take(8).ToList();
            if (slots.Count > 0)
            {
                var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                foreach (var m in slots)
                {
                    var g = new Grid { Width = 52, Height = 22, Margin = new Thickness(0, 0, 6, 0) };
                    if (!m.IsEmpty)
                    {
                        var fill = new Border { CornerRadius = new CornerRadius(4) };
                        fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
                        g.Children.Add(fill);
                        string cap = m.CapacityBytes > 0 ? $"{m.CapacityBytes / 1_073_741_824.0:0.#} GB" : m.Capacity;
                        g.Children.Add(new TextBlock { Text = cap, FontSize = Ui.FontXS, FontWeight = FontWeights.SemiBold, Foreground = System.Windows.Media.Brushes.White,
                                                       HorizontalAlignment = WpfHAlign.Center, VerticalAlignment = WpfVAlign.Center });
                    }
                    else
                    {
                        var dash = new System.Windows.Shapes.Rectangle { RadiusX = 4, RadiusY = 4, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 2 } };
                        dash.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextSecondaryBrush");
                        g.Children.Add(dash);
                        var free = HeroText(LanguageService.S("Ram", "SlotFree", "Free").ToLowerInvariant(), Ui.FontXS - HeroCards.FontBoost, "TextSecondaryBrush");
                        free.HorizontalAlignment = WpfHAlign.Center; free.VerticalAlignment = WpfVAlign.Center;
                        g.Children.Add(free);
                    }
                    g.ToolTip = string.IsNullOrWhiteSpace(m.SlotLabel) ? m.BankLabel : m.SlotLabel;
                    row.Children.Add(g);
                }
                int used = slots.Count(m => !m.IsEmpty);
                r.Cells.Add(Cell(VisualCell(string.Format(SI("CellSlots", "Slots · {0} of {1} in use"), used, slots.Count), row)));
            }
            // the speed and the channels: the first fact (capacity · type) is already in the name, the last (the slots) is drawn
            var rest = p.Facts.Skip(1).ToList();
            if (slots.Count > 0 && rest.Count > 0) rest.RemoveAt(rest.Count - 1);
            foreach (var f in rest.Take(2)) r.Cells.Add(Cell(f));
            r.Status = SI("StatusOk", "OK");
            try
            {
                if (RamDiagnosticsService.DetectChannelMode(_tileRams).Status == DualChannelStatus.SingleChannel)
                { r.Status = SI("StatusSingleChannel", "Single channel"); r.StatusBrush = "StatusCheckBrush"; }
            }
            catch (Exception ex) { AppLogger.Debug($"SystemInfo table memory status: {ex.Message}"); }
            return r;
        }

        /// <summary>
        /// Storage: one row per disk — the name, the health (coloured label) and the temperature, then the partition map:
        /// each partition has a width by size, is filled as much as it is used, and the label (letter, free space) sits above.
        /// </summary>
        private TableRowData? RowStorage(Dictionary<string, HeroCards.CardParts> parts)
        {
            if (!parts.TryGetValue("Disk", out var p)) return null;
            var r = NewRow("Disk", p, "Disk");
            var disks = _tileDisks.Where(d => d.SizeBytes > 0).ToList();
            if (disks.Count == 0) disks = _tileDisks.ToList();
            double worst = 0; string worstLetter = "";
            int minHealth = 100;

            var list = new StackPanel();
            for (int i = 0; i < Math.Min(3, disks.Count); i++)
            {
                var d = disks[i];
                var line = new Grid { Margin = new Thickness(0, 0, 0, i == Math.Min(3, disks.Count) - 1 ? 0 : 10) };
                // the same columns as the table cells (1.3* · 1* · 1*), so "C:" starts under "Base frequency"
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // left: the name, the type, the health and the temperature
                var left = new StackPanel { VerticalAlignment = WpfVAlign.Center, Margin = new Thickness(TableCellGutter, 0, 14, 0) };
                string kind = d.Kind == "Unknown" ? "" : d.TypeText + (d.UsbConnected.Length > 0 ? " · " + d.UsbConnected.Split(" · ")[0] : "");
                // the model on the first line (cut with "…" if long, full in the tooltip), the type and the size below
                var name = HeroText(string.IsNullOrWhiteSpace(d.Model) ? $"Disk {d.DiskIndex}" : d.Model.Trim(), Ui.FontS, "TextPrimaryBrush", semibold: true);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                name.TextWrapping = TextWrapping.NoWrap;
                name.ToolTip = d.Model;
                left.Children.Add(name);
                string sub = string.Join(" · ", new[] { kind, DisplayFormat.SizeBytes((long)d.SizeBytes) }.Where(x => x.Length > 0));
                if (sub.Length > 0) left.Children.Add(HeroText(sub, Ui.FontXS, "TextSecondaryBrush"));

                var pills = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
                bool noHealth = d.SmartAttributes.Count == 0 && d.NvmeHealthRows.Count == 0;
                if (noHealth)
                    pills.Children.Add(SmallPill(SI("NoHealth", "no health data"), "TextSecondaryBrush", null));
                else
                {
                    int hp = d.HealthPercent;
                    minHealth = Math.Min(minHealth, hp);
                    string hb = hp >= 90 ? "StatusGoodBrush" : hp >= 60 ? "StatusCheckBrush" : "StatusProblemBrush";
                    pills.Children.Add(SmallPill(string.Format(SI("CellHealth", "Health {0}%"), hp), hb, "Shield"));
                }
                var tAttr = d.SmartAttributes.FirstOrDefault(a => a.Id == 0xC2 || a.Id == 0xBE);
                long tC = tAttr != null ? tAttr.RawValue & 0xFF : 0;
                if (tC is > 0 and < 120) pills.Children.Add(SmallPill(TempUnit.Fmt(tC, 0, true), "TextSecondaryBrush", null));
                left.Children.Add(pills);
                line.Children.Add(left);

                // right: the partition map
                var map = new Grid { VerticalAlignment = WpfVAlign.Center };
                map.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                map.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                int col = 0;
                double diskTotalGb = Math.Max(1, d.Partitions.Sum(x => Math.Max(x.TotalGB, 0)));
                foreach (var v in d.Partitions)
                {
                    bool tiny = v.TotalGB < 0.5;
                    map.ColumnDefinitions.Add(new ColumnDefinition { Width = tiny ? new GridLength(14) : new GridLength(Math.Max(v.TotalGB, 1), GridUnitType.Star) });
                    double usedPct = v.TotalGB > 0 ? Math.Clamp((v.TotalGB - v.FreeGB) / v.TotalGB * 100, 0, 100) : 0;
                    string fillKey = StatusRules.DiskBrushKey(usedPct, "AccentBrush");
                    string letter = string.IsNullOrWhiteSpace(v.Letter) ? "" : v.Letter.TrimEnd('\\');
                    if (!tiny && letter.Length > 0 && usedPct > worst) { worst = usedPct; worstLetter = letter; }

                    if (!tiny)
                    {
                        string text = string.Format(SI("VolumeFree", "{0} · {1} free of {2}"), letter.Length > 0 ? letter : "—", DisplayFormat.SizeGb(v.FreeGB), DisplayFormat.SizeGb(v.TotalGB));
                        // a narrow partition shows only the free space (the full text stays in the tooltip), so it is not cut with "…"
                        string shown = v.TotalGB / diskTotalGb < 0.35
                            ? string.Format(SI("VolumeFreeShort", "{0} · {1} free"), letter.Length > 0 ? letter : "—", DisplayFormat.SizeGb(v.FreeGB))
                            : text;
                        var label = HeroText(shown, Ui.FontXS, usedPct >= StatusRules.DiskWarnPct ? fillKey : "TextSecondaryBrush", semibold: usedPct >= StatusRules.DiskWarnPct);
                        label.Margin = new Thickness(0, 0, 4, 3);
                        label.ToolTip = string.IsNullOrWhiteSpace(v.Label) ? text : $"{text}\n{v.Label}";
                        Grid.SetColumn(label, col);
                        map.Children.Add(label);
                    }
                    var track = new Grid { Height = 14, Margin = new Thickness(0, 0, 3, 0), ClipToBounds = true };
                    track.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = Tinted("TextSecondaryBrush", 0.22) });
                    if (!tiny)
                    {
                        var fillGrid = new Grid();
                        fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(usedPct, 0.01), GridUnitType.Star) });
                        fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(100 - usedPct, 0.01), GridUnitType.Star) });
                        var fill = new Border { CornerRadius = new CornerRadius(4) };
                        fill.SetResourceReference(Border.BackgroundProperty, fillKey);
                        fillGrid.Children.Add(fill);
                        track.Children.Add(fillGrid);
                    }
                    Grid.SetRow(track, 1); Grid.SetColumn(track, col);
                    map.Children.Add(track);
                    col++;
                }
                map.Margin = new Thickness(TableCellGutter, 0, 14, 0);
                Grid.SetColumn(map, 1); Grid.SetColumnSpan(map, 2);
                line.Children.Add(map);
                list.Children.Add(line);
            }
            if (disks.Count > 3)
            {
                var more = HeroText($"+ {disks.Count - 3}", Ui.FontXS, "TextSecondaryBrush");
                more.Margin = new Thickness(TableCellGutter, 4, 0, 0);
                list.Children.Add(more);
            }
            r.Wide = list;

            if (minHealth < 60) { r.Status = string.Format(SI("StatusHealth", "Health {0}%"), minHealth); r.StatusBrush = "StatusProblemBrush"; }
            else if (worst >= StatusRules.DiskCritPct) { r.Status = string.Format(SI("StatusFull", "{0} full"), worstLetter); r.StatusBrush = "StatusProblemBrush"; }
            else if (worst >= StatusRules.DiskWarnPct || minHealth < 90) { r.Status = SI("StatusWatch", "Needs attention"); r.StatusBrush = "StatusCheckBrush"; }
            else r.Status = SI("StatusOk", "OK");
            return r;
        }

        /// <summary>The small label (health, temperature): an optional icon and coloured text on a very light background.</summary>
        private static FrameworkElement ProtectionPill(string name, bool on)
        {
            // on: tinted fill + coloured outline + ✓; off: no fill, a grey outline, ✕ and the name struck through
            string key = on ? "StatusGoodBrush" : "StatusCheckBrush";
            var sp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            var mark = HeroText(on ? "✓" : "✕", Ui.FontXS, key, semibold: true);
            mark.Margin = new Thickness(0, 0, 5, 0);
            sp.Children.Add(mark);
            var t = HeroText(name, Ui.FontXS, on ? "TextPrimaryBrush" : "TextSecondaryBrush", semibold: on);
            // off: the grey border, the ✕ and the colour are enough (no strikethrough)
            sp.Children.Add(t);
            var b = new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 9, 3), Margin = new Thickness(0, 0, 6, 0), Child = sp,
                BorderThickness = new Thickness(1.2),
                ToolTip = on ? LanguageService.S("SysInfo", "ProtOn", "On") : LanguageService.S("SysInfo", "ProtOff", "Off"),
            };
            if (on) { b.Background = Tinted(key, 0.18); b.SetResourceReference(Border.BorderBrushProperty, key); }
            else { b.Background = System.Windows.Media.Brushes.Transparent; b.SetResourceReference(Border.BorderBrushProperty, "TextSecondaryBrush"); b.Opacity = 0.9; }
            return b;
        }

        private static FrameworkElement SmallPill(string text, string brushKey, string? iconKey)
        {
            var sp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            if (iconKey != null)
            {
                var ic = Glyph(iconKey, brushKey, 13);
                ic.Margin = new Thickness(0, 0, 5, 0);
                sp.Children.Add(ic);
            }
            sp.Children.Add(HeroText(text, Ui.FontXS, brushKey, semibold: brushKey != "TextSecondaryBrush"));
            return new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 9, 3), Margin = new Thickness(0, 0, 6, 0), Child = sp,
                Background = Tinted(brushKey, brushKey == "TextSecondaryBrush" ? 0.12 : 0.15),
            };
        }

        /// <summary>The graphics card: the video memory as blocks (one per GB), the type and the driver (with the date; coloured when old).</summary>
        private TableRowData? RowGraphics(Dictionary<string, HeroCards.CardParts> parts)
        {
            if (!parts.TryGetValue("Gpu", out var p)) return null;
            var r = NewRow("Gpu", p, "Gpu");
            r.Status = SI("StatusOk", "OK");
            var main = _heroGpus.FirstOrDefault(g => !g.Integrated && !GpuPackage.IsBasicDriver(g.Name)) ?? _heroGpus.FirstOrDefault();
            bool oldDriver = false;
            if (main != null)
            {
                if (GpuPackage.IsBasicDriver(main.Name)) { r.Status = SI("StatusNoDriver", "No driver"); r.StatusBrush = "StatusProblemBrush"; }
                else if (System.Text.RegularExpressions.Regex.Match(main.DriverDate ?? "", @"\b(19|20)\d\d\b") is { Success: true } m
                         && int.TryParse(m.Value, out int year) && year <= DateTime.Now.Year - 4)
                { oldDriver = true; r.Status = SI("StatusOldDriver", "Old driver"); r.StatusBrush = "StatusCheckBrush"; }

                var gbMatch = System.Text.RegularExpressions.Regex.Match(main.Vram ?? "", @"(\d+(?:[.,]\d+)?)\s*GB", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!main.Integrated && gbMatch.Success && double.TryParse(gbMatch.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double gb) && gb >= 0.5)
                {
                    int n = Math.Max(1, (int)Math.Round(gb));
                    r.Cells.Add(Cell(VisualCell(string.Format(SI("CellVram", "Video memory · {0}"), $"{gb:0.#} GB"), Blocks(n, n, "AccentBrush"))));
                }
            }
            if (p.Facts.Count > 0) r.Cells.Add(Cell(p.Facts[0]));   // the type (integrated / dedicated) or "driver not installed"
            if (main != null && !string.IsNullOrWhiteSpace(main.Driver) && !GpuPackage.IsBasicDriver(main.Name))
            {
                string label = SI("CellDriver", "Driver") + (string.IsNullOrWhiteSpace(main.DriverDate) ? "" : $" · {main.DriverDate}");
                r.Cells.Add(Cell(HeroFact("Drivers", SpecSheet.Hue("Gpu"), GpuPackage.DriverText(main.Name, main.Driver), label,
                    oldDriver ? "StatusCheckBrush" : "TextPrimaryBrush")));
            }
            if (r.Cells.Count < 3)
                foreach (var f in p.Facts.Skip(1).Take(3 - r.Cells.Count)) r.Cells.Add(Cell(f));
            return r;
        }

        /// <summary>
        /// The screens, drawn at their proportion and size: the built-in one as a laptop screen (with a base), the external one
        /// as a monitor (with a stand). The main one is in the accent colour; with several screens, their number sits inside.
        /// </summary>
        private TableRowData? RowDisplays(Dictionary<string, HeroCards.CardParts> parts)
        {
            if (!parts.TryGetValue("Monitor", out var p)) return null;
            var r = NewRow("Monitor", p, "Monitor");
            var screens = _heroScreens.OrderBy(s => s.X).Take(4).ToList();
            if (screens.Count > 0)
            {
                double Size(HeroScreen s) => s.WidthCm > 0 ? s.WidthCm : s.Width / 40.0;
                double max = screens.Max(Size);
                var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = WpfVAlign.Bottom };
                foreach (var s in screens)
                {
                    double w = Math.Max(26, 56 * Size(s) / Math.Max(1, max));
                    double h = s.Width > 0 ? w * s.Height / s.Width : w * 9 / 16;
                    string key = s.Primary ? "AccentBrush" : "TextSecondaryBrush";
                    bool builtIn = IsBuiltInScreen(s);
                    var shape = new StackPanel { VerticalAlignment = WpfVAlign.Bottom, Margin = new Thickness(0, 0, 12, 0), Background = System.Windows.Media.Brushes.Transparent };

                    var panel = new Border { Width = w, Height = h, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(builtIn ? 3 : 2) };
                    panel.SetResourceReference(Border.BorderBrushProperty, key);
                    panel.Background = Tinted(key, 0.10);
                    var num = HeroText(screens.Count > 1 ? s.Number.ToString() : "", Ui.FontS, key, semibold: true);
                    num.HorizontalAlignment = WpfHAlign.Center; num.VerticalAlignment = WpfVAlign.Center;
                    panel.Child = num;
                    shape.Children.Add(panel);
                    if (builtIn)
                    {
                        // the laptop base: a plate slightly wider than the screen, with rounded bottom corners
                        var deck = new Border { Width = w + 14, Height = 4, CornerRadius = new CornerRadius(0, 0, 3, 3), HorizontalAlignment = WpfHAlign.Center };
                        deck.SetResourceReference(Border.BackgroundProperty, key);
                        shape.Children.Add(deck);
                    }
                    else
                    {
                        // the monitor: the neck and the foot of the stand
                        var neck = new Border { Width = 4, Height = 5, HorizontalAlignment = WpfHAlign.Center };
                        neck.SetResourceReference(Border.BackgroundProperty, key);
                        var foot = new Border { Width = Math.Max(14, w * 0.38), Height = 3, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = WpfHAlign.Center };
                        foot.SetResourceReference(Border.BackgroundProperty, key);
                        shape.Children.Add(neck);
                        shape.Children.Add(foot);
                    }
                    shape.ToolTip = $"{s.Width} × {s.Height}";
                    row.Children.Add(shape);
                }
                r.Cells.Add(Cell(row));
            }
            foreach (var f in p.Facts.Take(2)) r.Cells.Add(Cell(f));
            return r;
        }

        /// <summary>
        /// Power, on any computer: the title states the source and the power plan ("Plugged in · Balanced"). The battery
        /// percentage is already in the header, so it is not repeated in the title. Laptop: the drawn battery, the health as
        /// blocks and the capacity; desktop: the rest settings (screen off, sleep, fast startup).
        /// </summary>
        private TableRowData? RowPower(Dictionary<string, HeroCards.CardParts> parts)
            => RowBattery(parts) ?? RowMainsPower(parts);

        private static string Capitalized(string t) => t.Length == 0 ? t : char.ToUpper(t[0], LanguageService.UiCulture) + t[1..];

        private TableRowData PowerRowHeader(TableRowData r, bool onBattery)
        {
            string source = Capitalized(onBattery ? SI("BatOnBattery", "on battery") : SI("BatPlugged", "plugged in"));
            string plan = ActivePlanName();
            r.Caption = SI("CaptionPowerPlan", "Power plan");
            r.Title = HeroText(plan.Length > 0 ? $"{source} · {plan}" : source, Ui.FontL, "TextPrimaryBrush", semibold: true);
            return r;
        }

        private TableRowData? RowMainsPower(Dictionary<string, HeroCards.CardParts> parts)
        {
            if (!parts.TryGetValue("Bolt", out var p)) return null;
            var r = PowerRowHeader(NewRow("Bolt", p, "Bolt"), onBattery: false);
            // screen off, sleep, fast startup; "booted from" is already in the header
            foreach (var f in p.Facts.Take(3)) r.Cells.Add(Cell(f));
            return r;
        }

        private TableRowData? RowBattery(Dictionary<string, HeroCards.CardParts> parts)
        {
            if (!parts.TryGetValue("Battery", out var p) || _tileBattery is not { Present: true } bat) return null;
            var r = PowerRowHeader(NewRow("Battery", p, "Battery"), onBattery: !bat.IsAC);
            int pct = Math.Clamp(bat.ChargePercent, 0, 100);
            string state = bat.IsCharging ? SI("BatCharging", "charging") : bat.IsFull || pct >= 99 ? SI("BatFull", "full") : bat.IsAC ? SI("BatPlugged", "plugged in") : SI("BatOnBattery", "on battery");

            var drawing = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            // a slim battery (about 2.3 : 1) with an even gap all round the charge, the terminal on the right
            // no pixel rounding inside the drawing: at 125 % scaling the rounding put the charge 1 px higher than centre
            var shell = new Border { Width = 40, Height = 18, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(4.5), Padding = new Thickness(2),
                                     UseLayoutRounding = false, SnapsToDevicePixels = false, VerticalAlignment = WpfVAlign.Center };
            shell.SetResourceReference(Border.BorderBrushProperty, "TextSecondaryBrush");
            var level = new Grid();
            level.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(pct, 1), GridUnitType.Star) });
            level.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(100 - pct, 0.01), GridUnitType.Star) });
            var fill = new Border { CornerRadius = new CornerRadius(2.5), SnapsToDevicePixels = false, UseLayoutRounding = false };
            fill.SetResourceReference(Border.BackgroundProperty, pct >= 20 ? "StatusGoodBrush" : "StatusProblemBrush");
            level.Children.Add(fill);
            shell.Child = level;
            drawing.Children.Add(shell);
            var nub = new Border { Width = 2.5, Height = 7, CornerRadius = new CornerRadius(0, 1.5, 1.5, 0), VerticalAlignment = WpfVAlign.Center, Margin = new Thickness(1, 0, 0, 0),
                                   UseLayoutRounding = false, SnapsToDevicePixels = false };
            nub.SetResourceReference(Border.BackgroundProperty, "TextSecondaryBrush");
            drawing.Children.Add(nub);
            // the charge written next to the battery (not only above it)
            var pctText = HeroText($"{pct} %", Ui.FontS, "TextPrimaryBrush", semibold: true);
            pctText.Margin = new Thickness(8, 0, 0, 0);
            pctText.VerticalAlignment = WpfVAlign.Center;
            drawing.Children.Add(pctText);
            if (bat.IsCharging)
            {
                var bolt = Glyph("Bolt", "StatusGoodBrush", 16);
                bolt.Margin = new Thickness(8, 0, 0, 0);
                drawing.Children.Add(bolt);
            }
            // the percentage is next to the battery now; above it, only the state ("Full", "Charging")
            r.Cells.Add(Cell(VisualCell(state.Length > 0 ? char.ToUpper(state[0]) + state[1..] : state, drawing)));

            if (bat.WearPct is int wear)
            {
                int health = Math.Clamp(100 - wear, 0, 100);
                string hb = health >= 80 ? "StatusGoodBrush" : health >= 60 ? "StatusCheckBrush" : "StatusProblemBrush";
                // only the health (the wear is the same figure the other way round)
                r.Cells.Add(Cell(VisualCell(string.Format(SI("CellHealth", "Health {0}%"), health),
                    Blocks(10, (int)Math.Round(health / 10.0), hb, 10))));
                r.Status = health >= 80 ? SI("StatusOk", "OK") : string.Format(SI("StatusHealth", "Health {0}%"), health);
                r.StatusBrush = health >= 80 ? "StatusGoodBrush" : hb;
            }
            if (bat.FullCapacityMWh > 0 && bat.DesignCapacityMWh > 0)
                r.Cells.Add(Cell(HeroFact("Battery", SpecSheet.Hue("Battery"), $"{bat.FullCapacityMWh / 1000.0:0.#} / {bat.DesignCapacityMWh / 1000.0:0.#} Wh",
                    bat.CycleCount > 0 ? string.Format(bat.CycleCount == 1 ? SI("BatCycle", "capacity · {0} cycle") : SI("BatCycles", "capacity · {0} cycles"), bat.CycleCount) : SI("BatCapacity", "capacity"))));
            if (r.Cells.Count < 3)
                foreach (var f in p.Facts.Take(3 - r.Cells.Count)) r.Cells.Add(Cell(f));
            return r;
        }

        /// <summary>
        /// The network: one cell per connection (Wi-Fi, LAN, mobile internet, Bluetooth), without icons, so the texts align with
        /// the rest of the table. The state ("connected") is coloured in the label; the card name is shortened (full in the tooltip).
        /// </summary>
        private TableRowData? RowNetwork(Dictionary<string, HeroCards.CardParts> parts)
        {
            if (!parts.TryGetValue("Globe", out var p)) return null;
            var r = NewRow("Globe", p, "Globe");
            if (p.Body is StackPanel list)
                foreach (var line in list.Children.OfType<Grid>().ToList())
                {
                    var text = line.Children.OfType<StackPanel>().FirstOrDefault();
                    if (text == null) continue;
                    line.Children.Remove(text);
                    text.ToolTip = line.ToolTip;
                    text.Background = System.Windows.Media.Brushes.Transparent;
                    var top = text.Children.OfType<TextBlock>().FirstOrDefault();
                    // the connection type is the first text of the row ("Wi-Fi  ·  connected"), written as Runs
                    string kind = top == null ? "" : string.Concat(top.Inlines.OfType<System.Windows.Documents.Run>().Select(x => x.Text));
                    if (text.Children.OfType<TextBlock>().Skip(1).FirstOrDefault() is { } name)
                    {
                        string full = name.Text;
                        string shortName = ShortDeviceName(full);
                        if (kind.StartsWith("Wi-Fi", StringComparison.OrdinalIgnoreCase) && WifiGeneration(full) is { } gen) shortName += $" · {gen}";
                        name.Text = shortName;
                        name.FontSize = TableValueSize;
                        HeroCards.TwoLines(name);
                        name.ToolTip = full;
                    }
                    r.Cells.Add(Cell(text));
                    if (r.Cells.Count == 3) break;
                }
            bool connected = _heroNets.Any(HasIp);
            r.Status = connected ? SI("StatusConnected", "Connected") : SI("StatusOffline", "Not connected");
            r.StatusBrush = connected ? "StatusGoodBrush" : "StatusCheckBrush";
            return r;
        }

        /// <summary>
        /// The peripherals: the first three devices, each with the type above ("Camera", "Audio") and the shortened name below,
        /// like the other cells; without icons. The full name is in the tooltip, the rest of the devices in the row tooltip.
        /// </summary>
        private static TableRowData? RowPeripherals(Dictionary<string, HeroCards.CardParts> parts)
        {
            if (!parts.TryGetValue("Usb", out var p)) return null;
            var r = NewRow("Usb", p, "Usb");
            var all = new List<string>();
            if (p.Body is StackPanel body)
                foreach (var grid in body.Children.OfType<System.Windows.Controls.Primitives.UniformGrid>())
                {
                    var chips = grid.Children.OfType<Grid>().ToList();
                    grid.Children.Clear();
                    foreach (var chip in chips)
                    {
                        string name = chip.ToolTip as string ?? chip.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "";
                        if (name.Length == 0) continue;
                        all.Add(name);
                        if (r.Cells.Count >= 3) continue;
                        var cell = new StackPanel { ToolTip = name, Background = System.Windows.Media.Brushes.Transparent };
                        cell.Children.Add(HeroText(DeviceKind(name), Ui.FontXS, "TextSecondaryBrush"));
                        var value = HeroText(ShortDeviceName(name), Ui.FontL, "TextPrimaryBrush", semibold: true);
                        value.FontSize = TableValueSize;
                        HeroCards.TwoLines(value);
                        cell.Children.Add(value);
                        r.Cells.Add(Cell(cell));
                    }
                }
            if (all.Count > 3) r.Tip = string.Join("\n", all);
            return r;
        }

        /// <summary>The size of the values in the cells (the bold text under the label), the same on all rows.</summary>
        private static double TableValueSize => Ui.FontL + HeroCards.FontBoost;

        /// <summary>The type of a peripheral, by name (the small label above the name).</summary>
        private static string DeviceKind(string name)
        {
            bool Has(string pattern) => System.Text.RegularExpressions.Regex.IsMatch(name, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (Has(@"camera|webcam|\bcam\b")) return SI("DevCamera", "Camera");
            if (Has(@"audio|sound|speaker|headset|headphone|microphone|\bmic\b")) return SI("DevAudio", "Audio");
            if (Has(@"keyboard")) return SI("DevKeyboard", "Keyboard");
            if (Has(@"mouse|touchpad|trackpad")) return SI("DevMouse", "Mouse");
            if (Has(@"printer|scanner")) return SI("DevPrinter", "Printer");
            if (Has(@"disk|flash|storage|card reader")) return SI("DevStorage", "USB storage");
            if (Has(@"gamepad|controller|joystick")) return SI("DevGamepad", "Game controller");
            return SI("DevDevice", "Device");
        }

        /// <summary>
        /// The name of a device, without the parts that do not help recognition: (R), (TM), technical brackets, "Controller",
        /// "Family", "Device", "Wireless LAN 802.11ac PCI-E NIC" ("Realtek PCIe GbE Family Controller" → "Realtek PCIe GbE",
        /// "AMD High Definition Audio Device" → "AMD HD Audio"). If nothing remains, the full name is kept.
        /// </summary>
        internal static string ShortDeviceName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return name;
            const System.Text.RegularExpressions.RegexOptions I = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
            string s = System.Text.RegularExpressions.Regex.Replace(name, @"\((R|TM|C)\)|®|™", "", I);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\([^)]*\)", " ");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"High Definition", "HD", I);
            s = System.Text.RegularExpressions.Regex.Replace(s,
                @"\b(Wireless LAN Card|Wireless LAN|Wireless Network Adapter|Wireless Adapter|Network Adapter|Network Connection|802\.11\s*[a-z/]+|PCI-E|NIC|Family|Controller|Device|Connection|Adapter|Dual Band|\d+\s*MHz)\b", " ", I);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '·', ',');
            return s.Length >= 3 ? s : name.Trim();
        }

        /// <summary>The Wi-Fi generation from the card name ("802.11ac" → "Wi-Fi 5"), when the name does not already say it.</summary>
        internal static string? WifiGeneration(string name)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"Wi-?Fi\s*\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(name, @"802\.11\s*(?:[a-z]+/)*(be|ax|ac|n)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            return m.Groups[1].Value.ToLowerInvariant() switch { "be" => "Wi-Fi 7", "ax" => "Wi-Fi 6", "ac" => "Wi-Fi 5", _ => "Wi-Fi 4" };
        }

        /// <summary>Windows: the timeline (installed → last update → today), the build and the last update; the license is in the header.</summary>
        private TableRowData? RowWindows(Dictionary<string, HeroCards.CardParts> parts)
        {
            if (!parts.TryGetValue("Windows", out var p) || _winInfo is not { } w) return null;
            var r = NewRow("Windows", p, "Windows");
            if (TableGroups)
                r.Caption = $"{p.Caption} · " + (w.Licensed ? Ws("Licensed") : Ws("Unlicensed"));
            r.Status = SI("StatusOk", "OK");
            int days = w.LastUpdateOn is DateTime lu ? Math.Max(0, (int)(DateTime.Now - lu).TotalDays) : -1;
            bool stale = days > UpdateStaleDays;

            if (w.InstalledOn.HasValue || w.LastUpdateOn.HasValue)
                r.Cells.Add(Cell(UpdateTimelineMini(w, stale)));
            var build = p.Facts.Skip(1).FirstOrDefault() ?? p.Facts.FirstOrDefault();
            if (build != null) r.Cells.Add(Cell(build));
            if (w.LastUpdateOn is DateTime last)
            {
                r.Cells.Add(Cell(HeroFact("Clock", SpecSheet.Hue("Windows"),
                    days == 0 ? SI("Today", "today") : string.Format(SI("DaysAgo", "{0} days ago"), days),
                    Ws("LastUpdate"), stale ? "StatusCheckBrush" : "TextPrimaryBrush", SMDWin.Services.DateFormatHelper.FormatDate(last).TrimStart('0'))));
                if (stale) { r.Status = SI("StatusNotUpdated", "Not updated"); r.StatusBrush = "StatusCheckBrush"; }
            }
            if (WindowsFacts.SupportFor(w.BuildNumber, w.Edition) != WindowsSupport.Supported)
            { r.Status = Ws("SupportEnded"); r.StatusBrush = "StatusProblemBrush"; }
            return r;
        }

        /// <summary>The small timeline: dot (installed) — line — dot (last update) — line — dot (today), with the months below.</summary>
        private static FrameworkElement UpdateTimelineMini(WindowsInfo w, bool stale)
        {
            DateTime today = DateTime.Now;
            DateTime start = w.InstalledOn ?? w.LastUpdateOn!.Value;
            DateTime update = w.LastUpdateOn ?? start;
            if (update < start) start = update;
            double total = Math.Max(1, (today - start).TotalDays);
            double first = Math.Clamp((update - start).TotalDays / total, 0.08, 0.92);
            string upKey = stale ? "StatusCheckBrush" : "AccentBrush";

            var sp = new StackPanel();
            var line = new Grid { Height = 12 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(first, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - first, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            void Dot(int col, string key)
            {
                var e = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, VerticalAlignment = WpfVAlign.Center };
                e.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key);
                Grid.SetColumn(e, col); line.Children.Add(e);
            }
            void Bar(int col, string key)
            {
                var b = new Border { Height = 3, VerticalAlignment = WpfVAlign.Center };
                b.SetResourceReference(Border.BackgroundProperty, key);
                Grid.SetColumn(b, col); line.Children.Add(b);
            }
            Dot(0, "AccentBrush"); Bar(1, "AccentBrush"); Dot(2, upKey); Bar(3, upKey); Dot(4, "TextSecondaryBrush");
            sp.Children.Add(line);

            // two labels at half width: the install at the start of the line, the update at its end (the last point is today); the
            // month is written short, and what does not fit is cut with "…" (the full date is in the tooltip)
            var labels = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            string Month(DateTime d) => d.ToString("MMM yyyy", LanguageService.UiCulture).Replace(".", "");
            var a = HeroText(Capitalized(string.Format(SI("CellInstalled", "installed {0}"), Month(start))), Ui.FontXS, "TextSecondaryBrush");
            a.TextTrimming = TextTrimming.CharacterEllipsis;
            var b2 = HeroText(Capitalized(string.Format(SI("CellUpdated", "updated {0}"), Month(update))), Ui.FontXS, stale ? "StatusCheckBrush" : "TextSecondaryBrush", semibold: stale);
            b2.TextTrimming = TextTrimming.CharacterEllipsis;
            b2.HorizontalAlignment = WpfHAlign.Right; Grid.SetColumn(b2, 1);
            labels.Children.Add(a); labels.Children.Add(b2);
            labels.ToolTip = $"{start:d MMM yyyy} → {update:d MMM yyyy}";
            labels.Background = System.Windows.Media.Brushes.Transparent;
            sp.Children.Insert(0, labels);
            return sp;
        }

        /// <summary>Protection: the five protections as labels (on green, off orange), the antivirus and the boot mode.</summary>
        private TableRowData? RowProtection(Dictionary<string, HeroCards.CardParts> parts)
        {
            if (!parts.TryGetValue("Shield", out var p) || _winInfo is not { } w) return null;
            var r = NewRow("Shield", p, "Shield");
            bool? tpm = w.TpmRead ? !string.IsNullOrEmpty(w.TpmVersion) : (bool?)null;
            bool? av = w.AntivirusRead ? w.Antivirus is { Enabled: true, UpToDate: true } : (bool?)null;
            var named = new (string Name, bool? On)[]
            {
                ("Defender / AV", av), ("Firewall", w.Firewall), (Ws("SecureBoot"), w.SecureBoot), (Ws("Tpm"), tpm), ("BitLocker", w.DiskEncrypted),
            };
            if (av == true && w.Antivirus != null && !w.Antivirus.Name.Contains("Defender", StringComparison.OrdinalIgnoreCase)) named[0].Name = Ws("Antivirus");
            else if (av != null) named[0].Name = "Defender";

            // on: a filled label with ✓; off: an outlined label with ✕ and the name struck through — the difference does not
            // depend on telling green from orange (a weak screen, colour blindness)
            var chips = new WrapPanel();
            foreach (var (n, on) in named.Where(x => x.On.HasValue))
                chips.Children.Add(ProtectionPill(n, on == true));
            var chipsCell = Cell(chips);
            chipsCell.Span = 2;
            r.Cells.Add(chipsCell);

            // the third cell: the antivirus (the boot mode stays in Windows → details, PDF and "Copy all")
            var hue = SpecSheet.Hue("Shield");
            if (w.AntivirusRead)
            {
                string avName = w.Antivirus?.Name is { Length: > 0 } nm ? nm : Ws("NoAntivirus");
                r.Cells.Add(Cell(HeroFact("Shield", hue, avName, Ws("Antivirus"), av == true ? "TextPrimaryBrush" : "StatusCheckBrush")));
            }
            else if (Has(w.BootMode)) r.Cells.Add(Cell(HeroFact("Bolt", hue, w.BootMode, Ws("BootMode"))));

            var (active, known) = WindowsFacts.ProtectionScore(named.Select(x => x.On).ToArray());
            r.Status = known > 0 && active < known ? SI("StatusAttention", "Attention") : SI("StatusOk", "OK");
            r.StatusBrush = av == false ? "StatusProblemBrush" : known > 0 && active < known ? "StatusCheckBrush" : "StatusGoodBrush";
            return r;
        }

        // ── Fitting on screen ──────────────────────────────────────────────────────────────────────────────────────
        /// <summary>Narrow window: each row keeps two facts (the third stays in the PDF and "Copy all").</summary>
        private void ApplyTableNarrow(bool narrow)
        {
            _tableNarrow = narrow;
            foreach (var c in _tableThirdCells) c.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Tighter rows (less space above and below the text in each row, a smaller gap between rows).</summary>
        private void SetTableCompact(bool compact)
        {
            if (_tableGrid == null) return;
            var cards = _tableGrid.Children.OfType<Border>().Where(b => b.Tag as string != "Group").ToList();
            for (int i = 0; i < cards.Count; i++)
            {
                cards[i].Padding = compact ? new Thickness(18, 5, 18, 5) : new Thickness(18, 10, 18, 10);
                bool last = Grid.GetRow(cards[i]) == _tableGrid.RowDefinitions.Count - 1;
                cards[i].Margin = new Thickness(0, 0, 0, last ? 0 : compact ? 6 : TableRowGap);
            }
        }

        /// <summary>The big three-box header or the one-line "To check" strip.</summary>
        private void SetHeaderStrip(bool strip)
        {
            if (_tableHeaderFull != null) _tableHeaderFull.Visibility = strip ? Visibility.Collapsed : Visibility.Visible;
            if (_tableHeaderStrip != null) _tableHeaderStrip.Visibility = strip ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// The table takes all the remaining height under the header: each row keeps at least its natural height, and the
        /// remaining space is shared equally. If it does not fit, the rows stay at their natural height and the page scrolls.
        /// </summary>
        private void FitTableLayout(ScrollViewer scroll)
        {
            var host = _allCardsHost;
            if (host == null || _tableLayout == null || _tableGrid == null || _tableCard == null) return;
            host.LayoutTransform = Transform.Identity;
            _currentColumns = 2;
            bool narrow = scroll.ActualWidth < TableNarrowBelow;
            if (narrow != _tableNarrow) ApplyTableNarrow(narrow);

            _tableCard.Height = double.NaN;
            foreach (var rd in _tableGrid.RowDefinitions) { rd.Height = GridLength.Auto; rd.MinHeight = 0; }
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            // Up to three tries, each only if the previous one still does not fit (a laptop screen, a low window):
            // 1) the normal page; 2) tighter rows; 3) the big header replaced by the one-line "To check" strip.
            // Nothing is hidden: the header's facts are also in the rows (Windows, license, uptime …), the problems stay in the strip.
            double natural = 0, available = 0;
            bool fits = false;
            for (int step = 0; step < 3 && !fits; step++)
            {
                SetTableCompact(step >= 1);
                SetHeaderStrip(step >= 2);
                UpdateLayout();
                natural = _tableCard.ActualHeight;
                double headerH = _tableLayout.RowDefinitions[0].ActualHeight + _tableLayout.RowDefinitions[1].ActualHeight;
                double others = host.Children.OfType<FrameworkElement>().Where(c => c != _tableLayout)
                    .Sum(c => c.ActualHeight + c.Margin.Top + c.Margin.Bottom);
                // 16 px the bottom page margin, 10 px reserve for rounding, 12 px the layout margin
                available = scroll.ActualHeight - 16 - 10 - 12 - headerH - others;
                fits = natural > 0 && natural <= available;
            }
            scroll.VerticalScrollBarVisibility = fits ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
            if (!fits) return;

            var rowLines = _tableGrid.Children.OfType<Border>().ToList();
            for (int i = 0; i < _tableGrid.RowDefinitions.Count; i++)
            {
                var rd = _tableGrid.RowDefinitions[i];
                bool group = rowLines.FirstOrDefault(b => Grid.GetRow(b) == i)?.Tag as string == "Group";
                if (group) continue;   // the group titles stay at their height
                rd.MinHeight = rd.ActualHeight;
                rd.Height = new GridLength(1, GridUnitType.Star);
            }
            _tableCard.Height = Math.Floor(available);
        }
    }
}
