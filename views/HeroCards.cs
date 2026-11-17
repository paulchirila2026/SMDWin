using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;
using WpfColor   = System.Windows.Media.Color;
using WpfHAlign  = System.Windows.HorizontalAlignment;
using WpfVAlign  = System.Windows.VerticalAlignment;
using WpfPanel   = System.Windows.Controls.Panel;
using WpfCursors = System.Windows.Input.Cursors;

namespace SMDWin.Views
{
    /// <summary>
    /// The large cards in System Information (Hardware and Windows tabs): header (icon, coloured label, the big name) and
    /// body (an illustration and a fixed info grid: small label above, value below, in two columns).
    /// One place for both tabs, so they look the same.
    /// </summary>
    internal static class HeroCards
    {
        /// <summary>Texts in the System Information cards are 1 px larger than the requested size (readability).</summary>
        public const double FontBoost = 1;

        public static TextBlock Text(string text, double size, string brushKey, bool semibold = false, bool wrap = false)
        {
            var tb = new TextBlock
            {
                Text = text, FontSize = size + FontBoost,
                FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return tb;
        }

        /// <summary>
        /// A long text (motherboard model, antivirus, explained free space) moves to a second line instead of being cut after a
        /// few letters; only beyond two lines does "…" appear. The full value stays in the tooltip.
        /// </summary>
        public static void TwoLines(TextBlock tb)
        {
            tb.TextWrapping = TextWrapping.Wrap;
            tb.TextTrimming = TextTrimming.CharacterEllipsis;
            tb.MaxHeight = Math.Ceiling(tb.FontSize * 1.34 * 2) + 2;
        }

        /// <summary>
        /// One piece of information in the card, as a cell of the fixed grid: small label above, the (bold) value below. The full
        /// explanation is also in the tooltip. No icon: the card colour comes from the header. Text that is too long is cut with
        /// "…"; the full value stays in the tooltip.
        /// </summary>
        public static FrameworkElement Fact(string iconKey, WpfColor hue, string value, string label,
                                            string valueBrushKey = "TextPrimaryBrush", string? tip = null)
        {
            // no minimum width: on small screens (1280×1024, 1920×1200 at 150 %) two 150 px cells did not fit in the card and the
            // text was cut without "…"; now the cell narrows and the text moves to a second line
            var cell = new StackPanel { Margin = new Thickness(0, 0, 12, 6), MaxWidth = 230, Background = System.Windows.Media.Brushes.Transparent };
            string caption = string.IsNullOrWhiteSpace(label) ? DefaultCaption(iconKey) : label.Trim();
            if (caption.Length > 0 && char.IsLower(caption[0])) caption = char.ToUpperInvariant(caption[0]) + caption[1..];   // sentence case everywhere
            var cap = Text(caption, Ui.FontXS, "TextSecondaryBrush");
            TwoLines(cap);
            cell.Children.Add(cap);
            var val = Text(value, Ui.FontL, valueBrushKey, semibold: true);
            val.Margin = new Thickness(0, 0, 0, 0);
            TwoLines(val);
            // a model / serial code with a short suffix ("Mac-27ADBB7B4CEE8E 61") no longer breaks over two lines at the space
            // (it would look like two values): it stays on one line, cut with "…", the full value in the tooltip
            if (LooksLikeCode(value)) { val.TextWrapping = TextWrapping.NoWrap; val.MaxHeight = double.PositiveInfinity; }
            cell.Children.Add(val);
            cell.ToolTip = tip ?? (string.IsNullOrWhiteSpace(label) ? value : $"{value}\n{label}");
            return cell;
        }

        /// <summary>A single technical "word" (capitals, digits, dashes), possibly with a short suffix after a space.</summary>
        private static bool LooksLikeCode(string v) =>
            !string.IsNullOrWhiteSpace(v) && v.Length >= 10 &&
            System.Text.RegularExpressions.Regex.IsMatch(v.Trim(), @"^[A-Za-z0-9][A-Za-z0-9\-_.,/]{7,}( [A-Za-z0-9\-_.]{1,6})?$") &&
            v.Any(char.IsDigit);

        // Default label of a cell without an explanation, by the kind of information (the icon key)
        private static string DefaultCaption(string iconKey)
        {
            bool ro = LanguageService.CurrentCode == "ro", de = LanguageService.CurrentCode == "de", fr = LanguageService.CurrentCode == "fr";
            string P(string en, string roT, string deT, string frT) => ro ? roT : de ? deT : fr ? frT : en;
            return iconKey switch
            {
                "Clock"       => P("Frequency", "Frecvență", "Takt", "Fréquence"),
                "Cpu"         => P("Cores", "Nuclee", "Kerne", "Cœurs"),
                "Layers"      => P("Cache", "Cache", "Cache", "Cache"),
                "Bolt"        => P("Power", "Putere", "Leistung", "Puissance"),
                "Socket"      => P("Socket", "Soclu", "Sockel", "Socket"),
                "Gauge"       => P("Mode", "Mod", "Modus", "Mode"),
                "Ram"         => P("Memory", "Memorie", "Speicher", "Mémoire"),
                "Channels"    => P("Channels", "Canale", "Kanäle", "Canaux"),
                "Motherboard" => P("Board", "Placă", "Platine", "Carte"),
                "Drivers"     => P("Driver", "Driver", "Treiber", "Pilote"),
                "Key"         => P("Key", "Cheie", "Schlüssel", "Clé"),
                "Chassis"     => P("Case", "Carcasă", "Gehäuse", "Boîtier"),
                "Shield"      => P("Protection", "Protecție", "Schutz", "Protection"),
                "Usb"         => "USB",
                "Speaker"     => "Audio",
                "Monitor"     => P("Screen", "Ecran", "Bildschirm", "Écran"),
                "Ruler"       => P("Diagonal", "Diagonală", "Diagonale", "Diagonale"),
                "Wifi"        => P("Network", "Rețea", "Netzwerk", "Réseau"),
                "Ethernet"    => P("Network", "Rețea", "Netzwerk", "Réseau"),
                "Bluetooth"   => "Bluetooth",
                "Battery"     => P("Battery", "Baterie", "Akku", "Batterie"),
                "System"      => P("System", "Sistem", "System", "Système"),
                "Windows"     => "Windows",
                _             => "",
            };
        }

        /// <summary>A card's info grid ("spec sheet"): two left-aligned columns, as wide as the widest cell (150–230 px), not stretched over the whole card.</summary>
        public static System.Windows.Controls.Primitives.UniformGrid FactsGrid()
            => new AdaptiveFactsGrid { Columns = 2, VerticalAlignment = WpfVAlign.Top, HorizontalAlignment = WpfHAlign.Stretch };

        /// <summary>The information grid in a single column.</summary>
        public static void SingleColumn(WpfPanel facts)
        {
            if (facts is AdaptiveFactsGrid a) a.Single = true;
            if (facts is System.Windows.Controls.Primitives.UniformGrid u) u.Columns = 1;
        }

        /// <summary>The info grid: two columns, or a single one when the card is too narrow for two readable cells.</summary>
        internal sealed class AdaptiveFactsGrid : System.Windows.Controls.Primitives.UniformGrid
        {
            private const double TwoColumnsMin = 290;
            /// <summary>Always a single column (e.g. the card with several screens).</summary>
            public bool Single { get; set; }
            /// <summary>Maximum number of columns; more than 2 only in list rows (<see cref="ToRow"/>), where there is horizontal room.</summary>
            public int MaxColumns { get; set; } = 2;
            protected override System.Windows.Size MeasureOverride(System.Windows.Size constraint)
            {
                int cols = Single ? 1 : double.IsInfinity(constraint.Width) || constraint.Width >= TwoColumnsMin ? 2 : 1;
                if (!Single && MaxColumns > 2)
                {
                    int fit = double.IsInfinity(constraint.Width) ? MaxColumns : (int)(constraint.Width / 160);
                    cols = Math.Clamp(Math.Min(fit, MaxColumns), 1, Math.Max(1, Children.Count));
                }
                if (Columns != cols) Columns = cols;
                return base.MeasureOverride(constraint);
            }
        }

        /// <summary>The usual card body: the illustration on the left, the information on the right (added to <paramref name="facts"/>).</summary>
        /// <summary>Common size of the illustrations in the cards.</summary>
        public const double ArtW = 96, ArtH = 72;

        public static Grid PictureBody(FrameworkElement picture, out WpfPanel facts)
        {
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (picture is ArtPicture)
            {
                picture.Margin = new Thickness(0, 0, 14, 0);
                picture.VerticalAlignment = WpfVAlign.Top;
                body.Children.Add(picture);
            }
            else
            {
                // fixed slot: every drawing sits in a place of the same size (shrunk if larger), so the text starts at the same X
                picture.HorizontalAlignment = WpfHAlign.Center;
                picture.VerticalAlignment = WpfVAlign.Center;
                var fit = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = picture };
                var slot = new Grid { Width = ArtW, Height = ArtH, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = WpfVAlign.Top };
                slot.Children.Add(fit);
                body.Children.Add(slot);
            }
            facts = FactsGrid();
            Grid.SetColumn(facts, 1); body.Children.Add(facts);
            return body;
        }

        public sealed class CardOptions
        {
            /// <summary>The name tooltip (by default the full name).</summary>
            public string? TitleTip;
            /// <summary>Click on the name (usually an internet search); null = the name is not clickable.</summary>
            public Action? OnTitleClick;
            /// <summary>The name colour when it is a state ("Licensed", "2 of 3 active"); otherwise the usual text.</summary>
            public string TitleBrushKey = "TextPrimaryBrush";
        }

        /// <summary>
        /// The card: header (icon, coloured label, component name) and body. When the body is "drawing + grid" and the grid has
        /// few cells, the drawing grows, so the card is not half empty. The header is always on top, the body centred.
        /// </summary>
        public static Border Card(string iconKey, string caption, string title, UIElement body, CardOptions? options = null)
        {
            var opt = options ?? new CardOptions();
            var hue = SpecSheet.Hue(iconKey);
            // header and body sit at the top (the body starts on the same line in all cards of a row)
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var tile = SpecSheet.IconTile(iconKey, 20);
            tile.Margin = new Thickness(0, 0, 11, 0);
            header.Children.Add(tile);

            var names = new StackPanel { VerticalAlignment = WpfVAlign.Center, Tag = caption };   // Tag: the component type, shown above the name in the list rows (ToRow)
            // single-row header: icon + title. The component type ("Processor") stays in the tooltip and the icon;
            // without a title of its own, the type becomes the title.
            string shownTitle = string.IsNullOrWhiteSpace(title) ? caption : title;
            var name = Text(shownTitle, Ui.FontXL, opt.TitleBrushKey, semibold: true);
            TwoLines(name);
            name.ToolTip = opt.TitleTip ?? (shownTitle == caption ? shownTitle : $"{caption} · {shownTitle}");
            if (opt.OnTitleClick != null)
            {
                name.Cursor = WpfCursors.Hand;
                name.MouseLeftButtonUp += (_, _) => opt.OnTitleClick();
            }
            names.Children.Add(name);
            Grid.SetColumn(names, 1); header.Children.Add(names);
            root.Children.Add(header);
            var bodyHost = new Grid { VerticalAlignment = WpfVAlign.Top };
            bodyHost.Children.Add(body);
            Grid.SetRow(bodyHost, 1); root.Children.Add(bodyHost);

            // the same size for every card's illustration (CPU and GPU are no larger than RAM or SSD)
            if (body is Grid g && g.Children.Count >= 2 && g.Children[0] is ArtPicture art && g.Children[1] is WpfPanel)
            {
                art.Width = ArtW; art.Height = ArtH;
            }

            var card = new Border
            {
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                Padding = new Thickness(16, 8, 16, 6), Margin = new Thickness(0, 0, 12, 6), Child = root,
            };
            card.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            card.Tag = iconKey;   // used by the page to find the card requested from the Dashboard (SystemInfoView.FocusCard)
            return card;
        }

        // ── Layout "computer on the left, components in a list" ─────────────────────────────────────────────────
        /// <summary>Drawing size and name column width, in the list rows.</summary>
        public const double RowArtW = 72, RowArtH = 54, RowNameW = 230;

        /// <summary>
        /// Turns a component card into a list row: the small drawing on the left, the component type and name, then the
        /// information across (up to 4 columns). No frame of its own: rows are separated by a fine line.
        /// A card without a drawing (e.g. Storage, with one drawing per disk) keeps its icon instead of the drawing.
        /// </summary>
        public static void ToRow(Border card)
        {
            if (card.Child is not Grid root) return;
            var header = root.Children.OfType<Grid>().FirstOrDefault(g => Grid.GetRow(g) == 0);
            var bodyHost = root.Children.OfType<Grid>().FirstOrDefault(g => Grid.GetRow(g) == 1);
            if (header == null || bodyHost == null) return;
            var names = header.Children.OfType<StackPanel>().FirstOrDefault();
            var tile = header.Children.OfType<FrameworkElement>().FirstOrDefault(c => c != names);
            var body = bodyHost.Children.Count > 0 ? bodyHost.Children[0] as FrameworkElement : null;

            root.Children.Clear();
            root.RowDefinitions.Clear();
            root.ColumnDefinitions.Clear();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RowArtW + 16) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RowNameW) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var picture = body == null ? null : TakePicture(body);
            if (picture != null)
            {
                picture.Width = RowArtW; picture.Height = RowArtH;
                picture.HorizontalAlignment = WpfHAlign.Left;
            }
            else if (tile != null)
            {
                header.Children.Remove(tile);
                picture = tile;
                picture.HorizontalAlignment = WpfHAlign.Center;
            }
            if (picture != null)
            {
                picture.Margin = new Thickness(0, 0, 16, 0);
                picture.VerticalAlignment = WpfVAlign.Center;
                Grid.SetRow(picture, 0); Grid.SetColumn(picture, 0); Grid.SetColumnSpan(picture, 1); Grid.SetRowSpan(picture, 1);
                root.Children.Add(picture);
            }

            if (names != null)
            {
                header.Children.Remove(names);
                if (names.Tag is string caption && caption.Length > 0)
                    names.Children.Insert(0, Text(caption, Ui.FontXS, "TextSecondaryBrush"));
                names.Margin = new Thickness(0, 0, 16, 0);
                names.VerticalAlignment = WpfVAlign.Center;
                Grid.SetColumn(names, 1);
                root.Children.Add(names);
            }

            bodyHost.VerticalAlignment = WpfVAlign.Center;
            Grid.SetRow(bodyHost, 0); Grid.SetColumn(bodyHost, 2);
            root.Children.Add(bodyHost);
            WidenFacts(bodyHost);

            card.Background = System.Windows.Media.Brushes.Transparent;
            card.BorderThickness = new Thickness(0, 0, 0, 1);
            card.CornerRadius = new CornerRadius(0);
            card.Padding = new Thickness(2, 12, 2, 12);
            card.Margin = new Thickness(0);
        }

        /// <summary>
        /// The drawing of a "drawing + info grid" body (direct or as the first element of the body), taken out of the body;
        /// null for bodies of another shape.
        /// </summary>
        private static FrameworkElement? TakePicture(FrameworkElement body)
        {
            Grid? g = body as Grid;
            if (g == null && body is StackPanel sp && sp.Children.Count > 0) g = sp.Children[0] as Grid;
            if (g == null || !g.Children.OfType<AdaptiveFactsGrid>().Any()) return null;
            var pic = g.Children.OfType<FrameworkElement>().FirstOrDefault(c =>
                c is not AdaptiveFactsGrid && Grid.GetColumn(c) == 0 && Grid.GetRow(c) == 0 && (c is not WpfPanel || c is Grid));
            if (pic == null) return null;
            g.Children.Remove(pic);
            return pic;
        }

        /// <summary>The info grids in a list row may have up to 4 columns.</summary>
        private static void WidenFacts(DependencyObject root)
        {
            if (root is AdaptiveFactsGrid a) a.MaxColumns = 4;
            if (root is WpfPanel p)
                foreach (var child in p.Children.OfType<DependencyObject>()) WidenFacts(child);
            else if (root is Border b && b.Child != null) WidenFacts(b.Child);
            else if (root is Decorator d && d.Child != null) WidenFacts(d.Child);
        }

        /// <summary>
        /// The computer card, tall (left column): the big drawing on top, centred, the information below in a single column.
        /// </summary>
        public static void ToTall(Border card, double artW, double artH)
        {
            if (card.Child is not Grid root) return;
            var bodyHost = root.Children.OfType<Grid>().FirstOrDefault(g => Grid.GetRow(g) == 1);
            if (bodyHost != null && bodyHost.Children.Count > 0 && bodyHost.Children[0] is Grid body
                && body.Children.OfType<AdaptiveFactsGrid>().FirstOrDefault() is { } facts)
            {
                var pic = body.Children.OfType<FrameworkElement>().FirstOrDefault(c => c != facts && Grid.GetColumn(c) == 0);
                body.RowDefinitions.Clear();
                body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                if (pic != null)
                {
                    Grid.SetColumnSpan(pic, 2);
                    pic.Width = artW; pic.Height = artH;
                    pic.HorizontalAlignment = WpfHAlign.Center;
                    pic.Margin = new Thickness(0, 8, 0, 16);
                    if (pic is Grid slot && slot.Children.OfType<Viewbox>().FirstOrDefault() is { } vb) vb.StretchDirection = StretchDirection.Both;
                }
                Grid.SetRow(facts, 1); Grid.SetColumn(facts, 0); Grid.SetColumnSpan(facts, 2);
                SingleColumn(facts);
            }
            card.Margin = new Thickness(0);
            card.Padding = new Thickness(20, 14, 20, 16);
        }

        /// <summary>
        /// Turns a card into a section of the left panel (the "system on the left, hardware on the right" layout): no frame of
        /// its own, separated from the next one by a fine line, the information in two columns.
        /// The drawing is kept (above the information, centred, at the given size) or removed.
        /// </summary>
        public static void ToSection(Border card, bool keepPicture, double artW = 0, double artH = 0)
        {
            if (card.Child is Grid root
                && root.Children.OfType<Grid>().FirstOrDefault(g => Grid.GetRow(g) == 1) is { } bodyHost
                && bodyHost.Children.Count > 0 && bodyHost.Children[0] is FrameworkElement body)
            {
                Grid? g = body as Grid;
                if (g == null && body is StackPanel sp && sp.Children.Count > 0) g = sp.Children[0] as Grid;
                if (g != null && g.Children.OfType<AdaptiveFactsGrid>().FirstOrDefault() is { } facts)
                {
                    var pic = TakePicture(g);
                    g.RowDefinitions.Clear();
                    g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    if (pic != null && keepPicture)
                    {
                        pic.Width = artW; pic.Height = artH;
                        pic.HorizontalAlignment = WpfHAlign.Center;
                        pic.Margin = new Thickness(0, 4, 0, 10);
                        if (pic is Grid slot && slot.Children.OfType<Viewbox>().FirstOrDefault() is { } vb) vb.StretchDirection = StretchDirection.Both;
                        Grid.SetRow(pic, 0); Grid.SetColumn(pic, 0); Grid.SetColumnSpan(pic, 2);
                        g.Children.Add(pic);
                    }
                    Grid.SetRow(facts, 1); Grid.SetColumn(facts, 0); Grid.SetColumnSpan(facts, 2);
                    facts.Single = false;
                    facts.MaxColumns = 2;
                }
            }
            card.Background = System.Windows.Media.Brushes.Transparent;
            card.BorderThickness = new Thickness(0, 0, 0, 1);
            card.CornerRadius = new CornerRadius(0);
            card.Padding = new Thickness(0, 10, 0, 8);
            card.Margin = new Thickness(0);
        }

        /// <summary>The card's info grid (the first one found), or null.</summary>
        public static AdaptiveFactsGrid? FindFacts(DependencyObject root)
        {
            if (root is AdaptiveFactsGrid a) return a;
            IEnumerable<DependencyObject> children = root switch
            {
                WpfPanel p => p.Children.OfType<DependencyObject>(),
                Decorator d when d.Child != null => new DependencyObject[] { d.Child },
                _ => Array.Empty<DependencyObject>(),
            };
            foreach (var c in children)
                if (FindFacts(c) is { } found) return found;
            return null;
        }

        /// <summary>Keeps the first <paramref name="keepFirst"/> and the last <paramref name="keepLast"/> items; the rest stay in the PDF and "Copy all".</summary>
        public static void TrimFacts(WpfPanel facts, int keepFirst, int keepLast = 0)
        {
            int n = facts.Children.Count;
            if (n <= keepFirst + keepLast) return;
            for (int i = n - keepLast - 1; i >= keepFirst; i--) facts.Children.RemoveAt(i);
        }

        /// <summary>The pieces of a taken-apart card (<see cref="Dismantle"/>), ready to be placed elsewhere.</summary>
        public sealed record CardParts(FrameworkElement? Picture, FrameworkElement? Icon, string Caption, TextBlock? Title,
                                       FrameworkElement? Body, List<FrameworkElement> Facts);

        /// <summary>
        /// Takes the card apart (drawing, icon, component type, name, remaining body and information), removed from the card,
        /// so the page can place them in a table. The information comes from the info grid or, for cards without a grid
        /// (e.g. Power), from the body's info panel.
        /// </summary>
        public static CardParts Dismantle(Border card)
        {
            var root = card.Child as Grid;
            var header = root?.Children.OfType<Grid>().FirstOrDefault(g => Grid.GetRow(g) == 0);
            var bodyHost = root?.Children.OfType<Grid>().FirstOrDefault(g => Grid.GetRow(g) == 1);
            var names = header?.Children.OfType<StackPanel>().FirstOrDefault();
            var icon = header?.Children.OfType<FrameworkElement>().FirstOrDefault(c => c != names);
            var title = names?.Children.OfType<TextBlock>().FirstOrDefault();
            string caption = names?.Tag as string ?? "";
            var body = bodyHost?.Children.OfType<FrameworkElement>().FirstOrDefault();
            var picture = body == null ? null : TakePicture(body);

            var facts = new List<FrameworkElement>();
            WpfPanel? factsPanel = body == null ? null : (WpfPanel?)FindFacts(body) ?? body as WrapPanel;
            if (factsPanel != null)
            {
                facts.AddRange(factsPanel.Children.OfType<FrameworkElement>());
                factsPanel.Children.Clear();
                foreach (var f in facts) { f.Margin = new Thickness(0); f.MaxWidth = double.PositiveInfinity; f.VerticalAlignment = WpfVAlign.Center; }
            }
            if (header != null && icon != null) header.Children.Remove(icon);
            if (names != null && title != null) names.Children.Remove(title);
            bodyHost?.Children.Clear();
            card.Child = null;
            return new CardParts(picture, icon, caption, title, body, facts);
        }

        /// <summary>Adds an element at the bottom of the card (under the body, against the bottom edge), e.g. the "To check" list.</summary>
        public static void AddFooter(Border card, UIElement footer)
        {
            if (card.Child is not Grid root) return;
            while (root.RowDefinitions.Count < 2) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(footer, root.RowDefinitions.Count - 1);
            root.Children.Add(footer);
        }

        /// <summary>Tag of a card row, so the page can find it and make it taller on large screens.</summary>
        public const string RowTag = "HeroRow";

        /// <summary>
        /// Widths (in columns) of the cards in a row of <paramref name="count"/> cards on a grid of <paramref name="columns"/>
        /// columns: the columns stay aligned with the rows above, and a row with fewer cards stretches its last cards to the end
        /// (no empty space on the right).
        /// </summary>
        public static int[] RowSpans(int count, int columns)
        {
            int cols = Math.Max(1, columns);
            count = Math.Clamp(count, 1, cols);
            var spans = new int[count];
            int each = cols / count;
            int used = 0;
            for (int i = 0; i < count; i++)
            {
                spans[i] = i == count - 1 ? cols - used : each;
                used += spans[i];
            }
            return spans;
        }

        /// <summary>
        /// Lays the cards out in rows of <paramref name="columns"/>. Cards on the same row have the same height; an incomplete
        /// last row stretches its last card over the remaining columns (<see cref="RowSpans"/>).
        /// </summary>
        public static void AddRows(WpfPanel host, IReadOnlyList<Border> cards, int columns)
        {
            int cols = Math.Max(1, columns);
            for (int start = 0; start < cards.Count; start += cols)
            {
                int count = Math.Min(cols, cards.Count - start);
                var row = new Grid { Margin = new Thickness(0, 0, -12, 0), Tag = RowTag };
                for (int c = 0; c < cols; c++)
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                int[] spans = RowSpans(count, cols);
                int column = 0;
                for (int c = 0; c < count; c++)
                {
                    Grid.SetColumn(cards[start + c], column);
                    Grid.SetColumnSpan(cards[start + c], spans[c]);
                    row.Children.Add(cards[start + c]);
                    column += spans[c];
                }
                host.Children.Add(row);
            }
        }
    }
}
