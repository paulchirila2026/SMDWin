using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SMDWin.Services;
using Application         = System.Windows.Application;
using Brush               = System.Windows.Media.Brush;
using Brushes             = System.Windows.Media.Brushes;
using Cursors             = System.Windows.Input.Cursors;
using FontFamily          = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation         = System.Windows.Controls.Orientation;
using VerticalAlignment   = System.Windows.VerticalAlignment;
using WpfColor            = System.Windows.Media.Color;
using ColorConverter      = System.Windows.Media.ColorConverter;
using WpfCanvas           = System.Windows.Controls.Canvas;

namespace SMDWin.Views
{
    /// <summary>
    /// The map in Storage › What uses space: aligned blocks (the "squarified" algorithm — blocks as close to square as
    /// possible, laid out in rows), area = size. In Folders mode, the large folders also show their content (subfolders +
    /// large files); in Largest files mode, the 50 largest files.
    /// </summary>
    public sealed partial class SpaceView
    {
        private sealed class Tile
        {
            public string Label = "";
            public long Size;
            public SpaceDir? Dir;
            public SpaceFile? File;
            public SpaceKind Kind;
            public int Rest;           // "N others" — the elements too small, gathered into a single block
        }

        private const double Gap = 4;

        // ── colours per category (light / dark) ─────────────────────────
        private static bool Light => ThemeManager.IsLight(SettingsService.Current.ThemeName == "Auto" ? ThemeManager.ResolveAuto() : SettingsService.Current.ThemeName);

        private static (string Fill, string Edge, string Inner, string Swatch) Palette(SpaceKind k) => (k, Light) switch
        {
            (SpaceKind.Media, true)      => ("#FDF0E3", "#F0C9A0", "#F8E0C7", "#EE9B55"),
            (SpaceKind.Media, false)     => ("#3A2A1C", "#6E4A2B", "#4A3523", "#C8834A"),
            (SpaceKind.Downloads, true)  => ("#E8EEFB", "#B9C9EE", "#D6E1F8", "#6F8FE0"),
            (SpaceKind.Downloads, false) => ("#1F2A44", "#3C5288", "#283656", "#5D7FD6"),
            (SpaceKind.Images, true)     => ("#E8EEFB", "#B9C9EE", "#D6E1F8", "#6F8FE0"),
            (SpaceKind.Images, false)    => ("#1F2A44", "#3C5288", "#283656", "#5D7FD6"),
            (SpaceKind.Programs, true)   => ("#FDF6DC", "#ECD796", "#F6ECC4", "#D4B44A"),
            (SpaceKind.Programs, false)  => ("#33301B", "#6A5F2C", "#403B22", "#BFA444"),
            (SpaceKind.Games, true)      => ("#EFEAFD", "#CDC1F5", "#E1D8FB", "#9A86EA"),
            (SpaceKind.Games, false)     => ("#2A2444", "#514487", "#352D57", "#8C7AE0"),
            (SpaceKind.Archives, true)   => ("#E2F4F1", "#A6D9CF", "#CDEBE5", "#3FA596"),
            (SpaceKind.Archives, false)  => ("#16312E", "#2C6159", "#1D3E3A", "#3FA596"),
            (SpaceKind.Windows, true)    => ("#F1F2F4", "#D9DCE1", "#E4E6EA", "#9AA1AB"),
            (SpaceKind.Windows, false)   => ("#24272D", "#3A3F48", "#2C3037", "#6B717B"),
            (SpaceKind.Safe, true)       => ("#E6F5EC", "#8FCCA6", "#D3ECDC", "#46A86A"),
            (SpaceKind.Safe, false)      => ("#17301F", "#2F7A4B", "#1E3D28", "#46C07A"),
            (_, true)                    => ("#F4F5F7", "#DDE0E5", "#E8EAEE", "#B9BEC7"),
            _                            => ("#22262C", "#373C45", "#2A2E35", "#5B616B"),
        };

        private static SolidColorBrush Hex(string h)
        {
            var b = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString(h));
            b.Freeze();
            return b;
        }

        /// <summary>The background of a block; "Safe to remove" is hatched (diagonals).</summary>
        private static Brush FillBrush(SpaceKind k, bool inner = false)
        {
            var p = Palette(k);
            if (k != SpaceKind.Safe) return Hex(inner ? p.Inner : p.Fill);
            var a = (WpfColor)ColorConverter.ConvertFromString(p.Fill);
            var b = (WpfColor)ColorConverter.ConvertFromString(p.Inner);
            var g = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute, StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(8, 8),
                SpreadMethod = GradientSpreadMethod.Repeat,
                GradientStops = { new GradientStop(a, 0), new GradientStop(a, 0.5), new GradientStop(b, 0.5), new GradientStop(b, 1) },
            };
            g.Freeze();
            return g;
        }

        internal static Brush SwatchBrush(SpaceKind k) => Hex(Palette(k).Swatch);

        // ── the data for the map ───────────────────────────────────────────────

        private IEnumerable<SpaceFile> TopFiles(int count, string filter)
        {
            if (_scan == null) return Enumerable.Empty<SpaceFile>();
            var limit = DateTime.Now.AddYears(-1);
            return _scan.Root.AllBigFiles()
                .Where(f => filter switch
                {
                    "Media"    => f.Kind == SpaceKind.Media,
                    "Images"   => f.Kind == SpaceKind.Images,
                    "Archives" => f.Kind == SpaceKind.Archives,
                    "Old"      => !f.IsSystem && f.Modified > DateTime.MinValue.AddYears(1) && f.Modified < limit,
                    _          => true,
                })
                .OrderByDescending(f => f.Size).Take(count);
        }

        /// <summary>The content of a folder: the subfolders and large files, from large to small; the rest (too small to see)
        /// in a single "other …" block.</summary>
        private static List<Tile> TilesOf(SpaceDir d, int max)
        {
            var all = new List<Tile>();
            foreach (var c in d.Dirs) if (c.Size > 0) all.Add(new Tile { Label = c.Name, Size = c.Size, Dir = c, Kind = c.Kind });
            foreach (var f in d.BigFiles) if (f.Size > 0) all.Add(new Tile { Label = f.Name, Size = f.Size, File = f, Kind = f.Kind });
            all.Sort((a, b) => b.Size.CompareTo(a.Size));
            long total = d.Size;
            var shown = new List<Tile>();
            long rest = d.SmallFilesSize; int restN = d.OwnFiles - d.BigFiles.Count;
            foreach (var t in all)
            {
                // below 0.4% of the folder or after "max" blocks: into "other"
                if (shown.Count >= max || (total > 0 && t.Size < total * 0.004)) { rest += t.Size; restN += t.Dir?.Files ?? 1; }
                else shown.Add(t);
            }
            if (rest > 0) shown.Add(new Tile { Label = S("OtherSmall", "Smaller items"), Size = rest, Kind = SpaceKind.Other, Rest = Math.Max(1, restN) });
            shown.Sort((a, b) => b.Size.CompareTo(a.Size));
            return shown;
        }

        // ── the drawing ────────────────────────────────────────────────────────────

        private void DrawMap()
        {
            if (_map == null) return;
            _map.Children.Clear();
            BuildCrumbs();
            bool has = _scan != null && _cur != null;
            _empty.Visibility = has || _cts != null ? Visibility.Collapsed : Visibility.Visible;
            if (!has) return;
            double w = _mapHost.ActualWidth, h = _mapHost.ActualHeight;
            if (w < 60 || h < 60) return;

            if (_filesMode)
            {
                var files = TopFiles(50, _filter).Select(f => new Tile { Label = f.Name, Size = f.Size, File = f, Kind = f.Kind }).ToList();
                if (files.Count == 0)
                {
                    var none = Txt(S("NoFiles", "No files in this group."), Ui.FontS, "TextSecondaryBrush");
                    WpfCanvas.SetLeft(none, 4); WpfCanvas.SetTop(none, 4);
                    _map.Children.Add(none);
                    return;
                }
                Layout(files, new Rect(0, 0, w, h), nested: false);
            }
            else
            {
                Layout(TilesOf(_cur!, 40), new Rect(0, 0, w, h), nested: true);
            }
        }

        private void Layout(List<Tile> tiles, Rect area, bool nested)
        {
            var rects = Squarify(tiles.Select(t => (double)t.Size).ToList(), area);
            for (int i = 0; i < tiles.Count; i++)
            {
                var r = Inset(rects[i], Gap / 2);
                if (r.Width < 3 || r.Height < 3) continue;
                DrawTile(tiles[i], r, depth: 0, nested);
            }
        }

        private static Rect Inset(Rect r, double d) => new(r.X + d, r.Y + d, Math.Max(0, r.Width - 2 * d), Math.Max(0, r.Height - 2 * d));

        private void DrawTile(Tile t, Rect r, int depth, bool nested)
        {
            var p = Palette(t.Kind);
            bool selected = t.File != null && ReferenceEquals(t.File, _selected);
            var box = new Border
            {
                Width = r.Width, Height = r.Height,
                CornerRadius = new CornerRadius(depth == 0 ? 6 : 4),
                BorderThickness = new Thickness(selected ? 2 : 1),
                Background = FillBrush(t.Kind, inner: depth > 0),
                BorderBrush = selected ? (Application.Current.TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue) : Hex(depth > 0 ? p.Inner : p.Edge),
                Cursor = t.Rest > 0 ? Cursors.Arrow : Cursors.Hand,
                ToolTip = TipFor(t),
                Tag = t,
            };
            WpfCanvas.SetLeft(box, r.X); WpfCanvas.SetTop(box, r.Y);
            box.MouseLeftButtonUp += Tile_Click;
            _map.Children.Add(box);

            // a large folder: a header + its content, drawn inside
            bool canNest = nested && depth == 0 && t.Dir != null && r.Width >= 120 && r.Height >= 90 && (t.Dir.Dirs.Count > 0 || t.Dir.BigFiles.Count > 0);
            if (canNest)
            {
                AddLabel(t.Label, r.X + 8, r.Y + 5, r.Width - 80, Ui.FontS, bold: true);
                var sz = Txt(Size(t.Size), Ui.FontS, "TextSecondaryBrush", bold: true);
                if (Application.Current.TryFindResource("NumFont") is FontFamily nf) sz.FontFamily = nf;
                sz.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                WpfCanvas.SetLeft(sz, r.Right - 8 - sz.DesiredSize.Width); WpfCanvas.SetTop(sz, r.Y + 5);
                sz.IsHitTestVisible = false;
                _map.Children.Add(sz);
                var inner = new Rect(r.X + 5, r.Y + 26, Math.Max(0, r.Width - 10), Math.Max(0, r.Height - 31));
                var kids = TilesOf(t.Dir!, 16);
                var rects = Squarify(kids.Select(k => (double)k.Size).ToList(), inner);
                for (int i = 0; i < kids.Count; i++)
                {
                    var kr = Inset(rects[i], 1.5);
                    if (kr.Width < 3 || kr.Height < 3) continue;
                    DrawTile(kids[i], kr, depth: 1, nested);
                }
                return;
            }

            // simple block: the name at the top, the size at the bottom (as far as it fits)
            bool roomName = r.Width >= 46 && r.Height >= 26;
            bool roomSize = r.Width >= 40 && r.Height >= (roomName ? 46 : 22);
            double nameSize = depth == 0 ? Ui.FontS : Ui.FontXS;
            if (roomName)
            {
                string label = t.Rest > 0 ? $"{t.Label} · {t.Rest:N0}" : t.Label;
                AddLabel(label, r.X + 7, r.Y + 4, r.Width - 12, nameSize, bold: depth == 0);
                if (_filesMode && t.File != null && r.Height >= 66 && r.Width >= 90)
                    AddLabel(t.File.Directory, r.X + 7, r.Y + 20, r.Width - 12, Ui.FontXS, bold: false, secondary: true);
            }
            if (roomSize)
            {
                double big = depth == 0 ? (r.Width >= 120 && r.Height >= 90 ? 20 : 14) : 12;
                var s = Txt(Size(t.Size), big, bold: true);
                if (Application.Current.TryFindResource("NumFont") is FontFamily nf2) s.FontFamily = nf2;
                s.IsHitTestVisible = false;
                s.MaxWidth = Math.Max(0, r.Width - 12);
                s.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                WpfCanvas.SetLeft(s, r.X + 7); WpfCanvas.SetTop(s, r.Bottom - 6 - s.DesiredSize.Height);
                _map.Children.Add(s);
            }
        }

        private void AddLabel(string text, double x, double y, double maxW, double size, bool bold, bool secondary = false)
        {
            if (maxW < 12) return;
            var t = Txt(text, size, secondary ? "TextSecondaryBrush" : "TextPrimaryBrush", bold);
            t.MaxWidth = maxW;
            t.IsHitTestVisible = false;
            WpfCanvas.SetLeft(t, x); WpfCanvas.SetTop(t, y);
            _map.Children.Add(t);
        }

        private object TipFor(Tile t)
        {
            if (t.File != null)
                return $"{t.File.Name}\n{t.File.Directory}\n{Size(t.File.Size)} · {KindName(t.Kind)}";
            if (t.Dir != null)
                return $"{t.Dir.Path}\n{Size(t.Dir.Size)} · {string.Format(S("FilesCountFmt", "{0:N0} files"), t.Dir.Files)}\n{S("ClickToOpen", "Click to open")}";
            return $"{t.Label}\n{Size(t.Size)} · {string.Format(S("FilesCountFmt", "{0:N0} files"), t.Rest)}";
        }

        private void Tile_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not Tile t) return;
            e.Handled = true;
            if (t.Dir != null) { _cur = t.Dir; DrawMap(); }
            else if (t.File != null) Select(t.File);
        }

        // ── the path (breadcrumb) ────────────────────────────────────────────────

        private void BuildCrumbs()
        {
            _crumbs.Children.Clear();
            if (_scan == null || _cur == null) return;
            var chain = new List<SpaceDir>();
            for (var d = _cur; d != null; d = d.Parent) chain.Insert(0, d);
            for (int i = 0; i < chain.Count; i++)
            {
                var d = chain[i];
                bool last = i == chain.Count - 1;
                var t = Txt(i == 0 ? d.Path.TrimEnd('\\') : d.Name, Ui.FontS, last ? "TextPrimaryBrush" : "AccentBrush", bold: last);
                if (!last)
                {
                    t.Cursor = Cursors.Hand;
                    var target = d;
                    t.MouseLeftButtonUp += (_, _) => { _cur = target; DrawMap(); };
                }
                _crumbs.Children.Add(t);
                if (!last)
                {
                    var sep = Txt("  ›  ", Ui.FontS, "TextSecondaryBrush");
                    _crumbs.Children.Add(sep);
                }
            }
            var size = Txt("   " + Size(_cur.Size), Ui.FontS, "TextSecondaryBrush", bold: true);
            if (Application.Current.TryFindResource("NumFont") is FontFamily nf) size.FontFamily = nf;
            _crumbs.Children.Add(size);
        }

        // ── the "squarified" algorithm (Bruls, Huizing, van Wijk) ─────────────────

        /// <summary>Splits the rectangle into blocks with areas proportional to the values (given from large to small), as close
        /// to square as possible: the values are placed in rows along the short side, and a row is closed when one more element
        /// would spoil its shape.</summary>
        internal static Rect[] Squarify(IList<double> values, Rect area)
        {
            int n = values.Count;
            var result = new Rect[n];
            double total = values.Sum();
            if (n == 0 || total <= 0 || area.Width <= 0 || area.Height <= 0) return result;
            double scale = area.Width * area.Height / total;
            var a = values.Select(v => Math.Max(0, v) * scale).ToArray();
            var free = area;
            int i = 0;
            while (i < n)
            {
                double side = Math.Min(free.Width, free.Height);
                if (side <= 0) break;
                int j = i; double sum = 0, best = double.MaxValue;
                double mn = double.MaxValue, mx = 0;
                while (j < n)
                {
                    double s2 = sum + a[j], mn2 = Math.Min(mn, a[j]), mx2 = Math.Max(mx, a[j]);
                    double worst = mn2 <= 0 ? double.MaxValue : Math.Max(side * side * mx2 / (s2 * s2), s2 * s2 / (side * side * mn2));
                    if (j > i && worst > best) break;
                    best = worst; sum = s2; mn = mn2; mx = mx2; j++;
                }
                if (sum <= 0) { for (int k = i; k < j; k++) result[k] = new Rect(free.X, free.Y, 0, 0); i = j; continue; }
                double thick = sum / side;
                if (free.Width >= free.Height)
                {
                    // column on the left, the elements one under another
                    double y = free.Y;
                    for (int k = i; k < j; k++) { double hk = a[k] / thick; result[k] = new Rect(free.X, y, thick, hk); y += hk; }
                    free = new Rect(free.X + thick, free.Y, Math.Max(0, free.Width - thick), free.Height);
                }
                else
                {
                    // top row, the elements side by side
                    double x = free.X;
                    for (int k = i; k < j; k++) { double wk = a[k] / thick; result[k] = new Rect(x, free.Y, wk, thick); x += wk; }
                    free = new Rect(free.X, free.Y + thick, free.Width, Math.Max(0, free.Height - thick));
                }
                i = j;
            }
            return result;
        }
    }
}
