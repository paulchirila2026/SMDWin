using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using WpfColor       = System.Windows.Media.Color;
using WpfBrushes     = System.Windows.Media.Brushes;
using WpfButton      = System.Windows.Controls.Button;
using WpfHAlign      = System.Windows.HorizontalAlignment;
using WpfVAlign      = System.Windows.VerticalAlignment;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfFontFamily  = System.Windows.Media.FontFamily;
using WpfPath        = System.Windows.Shapes.Path;
using WpfCanvas      = System.Windows.Controls.Canvas;
using WpfViewbox     = System.Windows.Controls.Viewbox;

namespace SMDWin.Views
{
    /// <summary>
    /// The text sections of the System Information and Windows info windows (the full rows from the component cards —
    /// views/HeroCards.cs — and the spec sheet for the sections left without a card):
    ///   • compact sections; in the sheet, laid out in balanced columns (no gaps between sections of different heights)
    ///   • each component has its colour (the icon plate + the label), the same in cards and in sections
    ///   • in the section header: the component label (small, coloured) + its NAME (large) — the main information
    ///   • label / value rows on an aligned label column, values left-aligned; monospace only for serials, versions, IDs;
    ///     only the product key is masked (eye button)
    ///   • long lists (USB, audio, adapters) show the first N rows + "X more"
    /// All base colours come from the app theme (SetResourceReference).
    /// </summary>
    internal static class SpecSheet
    {
        // ── The colour of each component ────────────────────────────────────
        internal static WpfColor Hue(string iconKey) => iconKey switch
        {
            "Cpu"         => WpfColor.FromRgb(0x3B, 0x82, 0xF6),   // albastru
            "Ram"         => WpfColor.FromRgb(0x8B, 0x5C, 0xF6),   // violet
            "Gpu"         => WpfColor.FromRgb(0x22, 0xC5, 0x5E),   // green
            "Disk"        => WpfColor.FromRgb(0xF5, 0x9E, 0x0B),   // chihlimbar
            "Battery"     => WpfColor.FromRgb(0xF9, 0x73, 0x16),   // orange
            "Chassis"     => WpfColor.FromRgb(0x64, 0x74, 0x8B),   // ardezie
            "Motherboard" => WpfColor.FromRgb(0x14, 0xB8, 0xA6),   // teal
            "Monitor"     => WpfColor.FromRgb(0xEC, 0x48, 0x99),   // roz
            "Wifi"        => WpfColor.FromRgb(0x06, 0xB6, 0xD4),   // cyan
            "Mobile"      => WpfColor.FromRgb(0x06, 0xB6, 0xD4),
            "Bluetooth"   => WpfColor.FromRgb(0x63, 0x66, 0xF1),   // indigo
            "Usb"         => WpfColor.FromRgb(0x94, 0xA3, 0xB8),   // gri
            "Speaker"     => WpfColor.FromRgb(0x94, 0xA3, 0xB8),
            "Windows"     => WpfColor.FromRgb(0x0E, 0xA5, 0xE9),   // light blue
            "Clock"       => WpfColor.FromRgb(0x64, 0x74, 0x8B),
            "Key"         => WpfColor.FromRgb(0xEA, 0xB3, 0x08),   // yellow
            "Shield"      => WpfColor.FromRgb(0x22, 0xC5, 0x5E),
            _             => WpfColor.FromRgb(0x63, 0x66, 0xF1),   // System
        };

        internal static readonly WpfColor Green = WpfColor.FromRgb(0x22, 0xC5, 0x5E);
        internal static readonly WpfColor Amber = WpfColor.FromRgb(0xF5, 0x9E, 0x0B);
        internal static readonly WpfColor Red   = WpfColor.FromRgb(0xEF, 0x44, 0x44);

        private static readonly WpfFontFamily Mono =
            (System.Windows.Application.Current?.TryFindResource("MonoFont") as WpfFontFamily)
            ?? new WpfFontFamily("Cascadia Mono, Consolas, Courier New");

        // Values that are identifiers (monospace): serials, versions, IDs, MAC, build, keys
        private static readonly Regex MonoLabel = new(
            @"serial|seri|version|versiun|build|\bid\b|mac|key|cheie|schlüssel|clé|asset|hardware id|device id|bios date|dată bios",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private const string CopyIcon   = "copy";
        private const string SearchIcon = "search";
        private const string EyeIcon    = "eye";
        private const string EyeOffIcon = "eye_off";

        // ── Primitive ────────────────────────────────────────────────────────
        private static TextBlock Txt(string text, double size, string brushKey, bool semibold = false, bool wrap = false)
        {
            var tb = new TextBlock
            {
                Text = text, FontSize = size,
                FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return tb;
        }

        private static SolidColorBrush Tint(WpfColor c, byte a) => new(WpfColor.FromArgb(a, c.R, c.G, c.B));

        internal static FrameworkElement IconGlyph(string iconKey, WpfColor color, double size)
        {
            var vb = new WpfViewbox { Width = size, Height = size, HorizontalAlignment = WpfHAlign.Center, VerticalAlignment = WpfVAlign.Center };
            var cv = new WpfCanvas { Width = 24, Height = 24 };
            // a Fluent icon when the key has one (all the System Information keys do); the older line drawing otherwise
            if (SMDWin.Services.FluentIcons.ForInfoKey.TryGetValue(iconKey, out var fluentName))
            {
                var fp = new WpfPath { Width = 24, Height = 24 };
                SMDWin.Services.FluentIcons.SetIcon(fp, fluentName);
                cv.Children.Add(fp);
                vb.Child = cv;
                return vb;
            }
            var paths = SMDWin.Services.InfoCardBuilder.Icons.TryGetValue(iconKey, out var p) ? p
                      : SMDWin.Services.InfoCardBuilder.Icons["System"];
            foreach (var d in paths)
                cv.Children.Add(new WpfPath
                {
                    Data = Geometry.Parse(d), StrokeThickness = 1.7,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round, Fill = WpfBrushes.Transparent,
                });
            foreach (var child in cv.Children.OfType<WpfPath>())
                child.SetResourceReference(WpfPath.StrokeProperty, "TextSecondaryBrush");
            vb.Child = cv;
            return vb;
        }

        /// <summary>The component icon, without background: a 1.7 px line in the secondary text colour, 20 px (16 px in a small
        /// spot). The slot has the requested size.</summary>
        internal static Border IconTile(string iconKey, double size = 20)
        {
            return new Border
            {
                Width = size, Height = size, VerticalAlignment = WpfVAlign.Center,
                Child = IconGlyph(iconKey, Hue(iconKey), size >= 24 ? 20 : size),
            };
        }

        private static WpfButton IconButton(string path, string tooltip, Action onClick)
        {
            var cv = new WpfCanvas { Width = 24, Height = 24 };
            var pth = new WpfPath();
            SMDWin.Services.FluentIcons.SetIcon(pth, path);
            cv.Children.Add(pth);
            var btn = new WpfButton
            {
                Width = 24, Height = 24, Padding = new Thickness(0), Margin = new Thickness(2, 0, 0, 0),
                Background = WpfBrushes.Transparent, BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand, ToolTip = tooltip, Opacity = 0.55,
                Content = new WpfViewbox { Width = 13, Height = 13, Child = cv },
                Focusable = false,
            };
            // Minimal template: no chrome of the default button (background / outline on hover)
            var tpl = new ControlTemplate(typeof(WpfButton));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, WpfBrushes.Transparent);
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, WpfHAlign.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, WpfVAlign.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            btn.Template = tpl;
            btn.MouseEnter += (_, _) => btn.Opacity = 1.0;
            btn.MouseLeave += (_, _) => btn.Opacity = 0.55;
            btn.Click += (_, _) => onClick();
            return btn;
        }

        /// <summary>The state label (Activated, Secure Boot on, TPM 2.0…).</summary>
        internal static Border Pill(string text, WpfColor color)
        {
            var tb = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(color) };
            return new Border
            {
                CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(0, 0, 6, 0),
                Background = Tint(color, 30), BorderBrush = Tint(color, 80), BorderThickness = new Thickness(1),
                VerticalAlignment = WpfVAlign.Center, Child = tb,
            };
        }

        // ── Sectiune ─────────────────────────────────────────────────────────
        internal sealed class SectionOptions
        {
            public Action? OnCopy;
            public string CopyTip = "Copy";
            public Action? OnSearch;
            public string? SearchTip;
            public HashSet<string>? SensitiveLabels;     // in addition to serials (e.g. the product key)
            public string ShowTip = "Show", HideTip = "Hide";
            public string MoreFmt = "+ {0} more";
            public string LessText = "Show less";
            public int CollapseAfter = 8;
            public IList<UIElement>? HeaderPills;         // ex. stare in antet
            public Func<string, string, WpfColor?>? ValueColor;   // (label, value) → state colour
            /// <summary>Without its own card: the section sits in a column of the <see cref="Sheet"/> (spec sheet).</summary>
            public bool Flat;
        }

        /// <summary>
        /// Compact section: [icon] LABEL / Name  ... [copy][search], then the rows.
        /// Labels starting with two spaces = sub-rows (indented); "1.", "2." = list items.
        /// A value with "\n" = value + a small detail under it.
        /// </summary>
        internal static Border Section(string iconKey, string caption, string title,
            List<(string Label, string Value)> rows, SectionOptions? opt = null)
        {
            opt ??= new SectionOptions();
            var hue = Hue(iconKey);
            var root = new StackPanel();

            // Antet
            if (opt.Flat) root.Children.Add(FlatHeader(iconKey, caption, title, opt, rows.Count > 0));
            var hg = new Grid { Margin = new Thickness(0, 0, 0, rows.Count > 0 ? 8 : 0), Visibility = opt.Flat ? Visibility.Collapsed : Visibility.Visible };
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tile = IconTile(iconKey, 20);
            tile.Margin = new Thickness(0, 0, 11, 0);
            hg.Children.Add(tile);
            var ht = new StackPanel { VerticalAlignment = WpfVAlign.Center };
            // a single-line header (no capitalised label above); the type stays in the tooltip
            var name = Txt(string.IsNullOrWhiteSpace(title) ? caption : title, 13.5, "TextPrimaryBrush", semibold: true);
            name.ToolTip = string.IsNullOrWhiteSpace(title) ? caption : $"{caption} · {title}";
            if (opt.OnSearch != null)
            {
                name.Cursor = System.Windows.Input.Cursors.Hand;
                name.ToolTip = opt.SearchTip ?? title;
                name.MouseLeftButtonUp += (_, _) => opt.OnSearch?.Invoke();
            }
            ht.Children.Add(name);
            if (opt.HeaderPills is { Count: > 0 })
            {
                var pills = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                foreach (var p in opt.HeaderPills) pills.Children.Add(p);
                ht.Children.Add(pills);
            }
            Grid.SetColumn(ht, 1); hg.Children.Add(ht);
            var acts = new StackPanel { Orientation = WpfOrientation.Horizontal, VerticalAlignment = WpfVAlign.Top };
            if (opt.OnSearch != null) acts.Children.Add(IconButton(SearchIcon, opt.SearchTip ?? "Search", opt.OnSearch!));
            if (opt.OnCopy != null) acts.Children.Add(IconButton(CopyIcon, opt.CopyTip, opt.OnCopy!));
            Grid.SetColumn(acts, 2); hg.Children.Add(acts);
            root.Children.Add(hg);

            // Rows: a single Grid per section → the label column is aligned
            var visibleRows = rows.Where(r => !string.IsNullOrWhiteSpace(r.Value)).ToList();
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = 150 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var hidden = new List<UIElement>();
            int limit = visibleRows.Count > opt.CollapseAfter + 2 ? opt.CollapseAfter : int.MaxValue;

            for (int i = 0; i < visibleRows.Count; i++)
            {
                var (rawLabel, value) = visibleRows[i];
                bool isSub  = rawLabel.StartsWith("  ");
                string label = rawLabel.Trim();
                bool isList = Regex.IsMatch(label, @"^\d+\.$");
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                string main = value, detail = "";
                int nl = value.IndexOf('\n');
                if (nl >= 0) { main = value[..nl]; detail = value[(nl + 1)..]; }

                var lbl = Txt(isList ? "•" : label, 12, "TextSecondaryBrush");
                if (isSub) lbl.Opacity = 0.75;
                lbl.Margin = new Thickness(isSub ? 10 : 0, 1, 12, 1);
                lbl.TextTrimming = TextTrimming.CharacterEllipsis;
                if (isList) { lbl.HorizontalAlignment = WpfHAlign.Right; lbl.Margin = new Thickness(0, 1, 8, 1); }
                else lbl.ToolTip = label;
                Grid.SetRow(lbl, i); grid.Children.Add(lbl);

                bool mono = !isList && MonoLabel.IsMatch(label);
                // The serials (disk, monitor, motherboard, chassis) are NOT secret — shown directly.
                // Only what the caller explicitly asks for is masked (e.g. the Windows product key).
                bool sensitive = opt.SensitiveLabels?.Contains(label) ?? false;

                var valSp = new StackPanel { Margin = new Thickness(0, 1, 0, 1) };
                var val = Txt(sensitive ? Mask(main) : main, 12, "TextPrimaryBrush", semibold: !isSub && !isList, wrap: true);
                if (mono) val.FontFamily = Mono;
                var sc = opt.ValueColor?.Invoke(label, main);
                if (sc is WpfColor scc) val.Foreground = new SolidColorBrush(scc);

                if (sensitive && main.Length > 0 && main != "—")
                {
                    var wrapSp = new StackPanel { Orientation = WpfOrientation.Horizontal };
                    wrapSp.Children.Add(val);
                    bool revealed = false;
                    WpfButton? eye = null;
                    eye = IconButton(EyeIcon, opt.ShowTip, () =>
                    {
                        revealed = !revealed;
                        val.Text = revealed ? main : Mask(main);
                        eye!.ToolTip = revealed ? opt.HideTip : opt.ShowTip;
                        if (eye.Content is WpfViewbox vb && vb.Child is WpfCanvas cv && cv.Children[0] is WpfPath pp)
                            pp.Data = SMDWin.Services.FluentIcons.Get(revealed ? EyeOffIcon : EyeIcon);
                    });
                    eye.Margin = new Thickness(6, -3, 0, -3);
                    wrapSp.Children.Add(eye);
                    valSp.Children.Add(wrapSp);
                }
                else valSp.Children.Add(val);

                if (!string.IsNullOrWhiteSpace(detail))
                {
                    var d = Txt(detail, 11, "TextSecondaryBrush", wrap: true);
                    if (mono) d.FontFamily = Mono;
                    valSp.Children.Add(d);
                }
                Grid.SetRow(valSp, i); Grid.SetColumn(valSp, 1); grid.Children.Add(valSp);

                if (i >= limit) { lbl.Visibility = Visibility.Collapsed; valSp.Visibility = Visibility.Collapsed; hidden.Add(lbl); hidden.Add(valSp); }
            }
            root.Children.Add(grid);

            if (hidden.Count > 0)
            {
                int extra = hidden.Count / 2;
                var more = new TextBlock
                {
                    Text = string.Format(opt.MoreFmt, extra), FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0),
                    Foreground = new SolidColorBrush(hue), Cursor = System.Windows.Input.Cursors.Hand,
                };
                bool open = false;
                more.MouseLeftButtonUp += (_, _) =>
                {
                    open = !open;
                    foreach (var h in hidden) h.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                    more.Text = open ? opt.LessText : string.Format(opt.MoreFmt, extra);
                };
                root.Children.Add(more);
            }

            var card = new Border
            {
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(opt.Flat ? 0 : 1),
                Padding = opt.Flat ? new Thickness(0) : new Thickness(12, 10, 12, 10),
                Margin = opt.Flat ? new Thickness(0, 0, 0, 18) : new Thickness(0, 0, 8, 8), Child = root,
                Tag = Math.Min(visibleRows.Count, limit == int.MaxValue ? visibleRows.Count : limit + 1),   // weight for the columns
            };
            if (opt.Flat) card.Background = WpfBrushes.Transparent;
            else
            {
                card.SetResourceReference(Border.BackgroundProperty, "CardSurfaceBrush");
                card.SetResourceReference(Border.BorderBrushProperty, "CardEdgeBrush");
            }
            return card;
        }

        /// <summary>
        /// The header of a spec sheet section: [icon] LABEL ───── [search][copy], below it the large name of the component
        /// (click = search, when available).
        /// </summary>
        private static FrameworkElement FlatHeader(string iconKey, string caption, string title, SectionOptions opt, bool hasRows)
        {
            var hue = Hue(iconKey);
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, hasRows ? 6 : 0) };
            var line1 = new Grid();
            line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var glyph = IconGlyph(iconKey, hue, 16);
            glyph.Margin = new Thickness(0, 0, 8, 0);
            line1.Children.Add(glyph);
            var cap = new TextBlock { Text = SMDWin.Services.DisplayFormat.SentenceCase(caption), FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold, VerticalAlignment = WpfVAlign.Center };
            cap.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Grid.SetColumn(cap, 1); line1.Children.Add(cap);
            var rule = new Border { Height = 1, VerticalAlignment = WpfVAlign.Center, Margin = new Thickness(10, 0, 6, 0) };
            rule.SetResourceReference(Border.BackgroundProperty, "CardBorderBrush");
            Grid.SetColumn(rule, 2); line1.Children.Add(rule);
            var acts = new StackPanel { Orientation = WpfOrientation.Horizontal, VerticalAlignment = WpfVAlign.Center };
            if (opt.OnSearch != null) acts.Children.Add(IconButton(SearchIcon, opt.SearchTip ?? "Search", opt.OnSearch!));
            if (opt.OnCopy != null) acts.Children.Add(IconButton(CopyIcon, opt.CopyTip, opt.OnCopy!));
            Grid.SetColumn(acts, 3); line1.Children.Add(acts);
            sp.Children.Add(line1);

            var name = Txt(string.IsNullOrWhiteSpace(title) ? caption : title, 15, "TextPrimaryBrush", semibold: true);
            name.Margin = new Thickness(0, 3, 0, 0);
            name.ToolTip = title;
            if (opt.OnSearch != null)
            {
                name.Cursor = System.Windows.Input.Cursors.Hand;
                name.ToolTip = opt.SearchTip ?? title;
                name.MouseLeftButtonUp += (_, _) => opt.OnSearch?.Invoke();
            }
            sp.Children.Add(name);
            if (opt.HeaderPills is { Count: > 0 })
            {
                var pills = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                foreach (var p in opt.HeaderPills) pills.Children.Add(p);
                sp.Children.Add(pills);
            }
            return sp;
        }

        // ── Spec sheet: a single card, the sections in columns separated by a line ──
        /// <summary>
        /// Puts the sections (made with <c>Flat = true</c>) in a single card, on <paramref name="columns"/> columns separated by
        /// a vertical line. The order is kept (top to bottom, then the next column), and the cut between columns is chosen by the
        /// number of rows, so the columns come out with close heights.
        /// There are no half-empty cards: each section has exactly the height of its content.
        /// </summary>
        internal static Border Sheet(IList<Border> sections, int columns)
        {
            var list = sections.Where(s => s != null).ToList();
            columns = Math.Max(1, Math.Min(columns, Math.Max(1, list.Count)));
            static double Weight(Border s) => 3.2 + (s.Tag is int r ? r : 4);
            double target = list.Sum(Weight) / columns;

            var groups = new List<List<Border>> { new() };
            double acc = 0;
            foreach (var s in list)
            {
                double w = Weight(s);
                if (groups[^1].Count > 0 && groups.Count < columns && acc + w / 2 > target * groups.Count)
                    groups.Add(new List<Border>());
                groups[^1].Add(s);
                acc += w;
            }

            var g = new Grid();
            for (int c = 0; c < groups.Count; c++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var stack = new StackPanel();
                for (int k = 0; k < groups[c].Count; k++)
                {
                    var s = groups[c][k];
                    if (VisualTreeHelper.GetParent(s) is System.Windows.Controls.Panel old) old.Children.Remove(s);
                    s.VerticalAlignment = WpfVAlign.Top;
                    s.Margin = new Thickness(0, 0, 0, k == groups[c].Count - 1 ? 2 : 18);
                    stack.Children.Add(s);
                }
                var colBorder = new Border
                {
                    Padding = new Thickness(18, 0, 18, 0), Child = stack,
                    BorderThickness = new Thickness(c > 0 ? 1 : 0, 0, 0, 0),
                };
                colBorder.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                Grid.SetColumn(colBorder, c);
                g.Children.Add(colBorder);
            }

            var card = new Border
            {
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                Padding = new Thickness(0, 16, 0, 14), Child = g,
            };
            card.SetResourceReference(Border.BackgroundProperty, "CardSurfaceBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "CardEdgeBrush");
            return card;
        }

        private static string Mask(string real)
        {
            if (string.IsNullOrEmpty(real) || real == "—") return real;
            if (real.Length <= 4) return new string('•', real.Length);
            return new string('•', Math.Min(8, real.Length - 4)) + real[^4..];
        }
    }
}
