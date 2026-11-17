using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SMDWin.Services;
using Application         = System.Windows.Application;
using FontFamily          = System.Windows.Media.FontFamily;
using Brush               = System.Windows.Media.Brush;
using Brushes             = System.Windows.Media.Brushes;
using Button              = System.Windows.Controls.Button;
using CheckBox            = System.Windows.Controls.CheckBox;
using ComboBox            = System.Windows.Controls.ComboBox;
using Cursors             = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation         = System.Windows.Controls.Orientation;
using UserControl         = System.Windows.Controls.UserControl;
using VerticalAlignment   = System.Windows.VerticalAlignment;
using WpfColor            = System.Windows.Media.Color;
using WpfCanvas           = System.Windows.Controls.Canvas;

namespace SMDWin.Views
{
    /// <summary>
    /// Storage › What uses space: the totals at the top, the disk map on the left (aligned blocks, by category; "Folders" mode
    /// with the subfolders and large files in each folder, "Largest files" mode with the 50 largest files), the chosen file
    /// and the recommendations with checkboxes on the right. The colours follow the app theme.
    /// The map drawing: SpaceView.Map.cs.
    /// </summary>
    public sealed partial class SpaceView : UserControl
    {
        private static string S(string key, string en) => LanguageService.S("Space", key, en);

        private ComboBox _drive = null!;
        private Button _btnScan = null!;
        private TextBlock _status = null!;
        private TextBlock _kFree = null!, _kFreeAdd = null!, _kSafe = null!, _kSafeN = null!, _kReview = null!, _kReviewN = null!, _k4Label = null!, _k4 = null!, _k4Sub = null!;
        private TextBlock _mapTitle = null!, _mapSub = null!;
        private Border _pillFolders = null!, _pillFiles = null!;
        private WrapPanel _crumbs = null!, _filters = null!, _legend = null!;
        private Border _filtersTrack = null!;   // the filters on the grey band
        private WpfCanvas _map = null!;
        private Border _mapHost = null!, _empty = null!, _scanning = null!;
        private System.Windows.Controls.ProgressBar _scanBar = null!;
        private TextBlock _scanText = null!;
        private Border _selCard = null!;
        private TextBlock _selName = null!, _selDir = null!, _selSize = null!, _selType = null!, _selDate = null!, _selHint = null!;
        private Button _selRecycle = null!;
        private StackPanel _recList = null!;
        private TextBlock _recSum = null!;
        private Button _btnClean = null!;

        private CancellationTokenSource? _cts;
        private SpaceScan? _scan;
        private SpaceDir? _cur;
        private bool _filesMode;
        private string _filter = "All";
        private SpaceFile? _selected;
        private List<SpaceCleanup.Rec> _recs = new();
        private readonly Dictionary<string, CheckBox> _recChecks = new();
        private readonly System.Windows.Threading.DispatcherTimer _redraw;

        public SpaceView()
        {
            Content = BuildUI();
            _redraw = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _redraw.Tick += (_, _) => { _redraw.Stop(); DrawMap(); };
            Loaded += async (_, _) =>
            {
                ThemeManager.ThemeChanged += OnTheme;
                if (_drive.Items.Count == 0) await FillDrivesAsync();
                if (_recs.Count == 0) await LoadRecsAsync();
            };
            Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnTheme;
        }

        private void OnTheme() => Dispatcher.BeginInvoke(new Action(() => { BuildLegend(); DrawMap(); }));

        // ── Interface ───────────────────────────────────────────────────────────

        private static TextBlock Txt(string text, double size, string brush = "TextPrimaryBrush", bool bold = false)
        {
            var t = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }

        private static TextBlock Num(string text, double size)
        {
            var t = Txt(text, size, bold: true);
            if (Application.Current.TryFindResource("NumFont") is FontFamily nf) t.FontFamily = nf;
            return t;
        }

        private static Border Card(UIElement child, Thickness pad)
        {
            var b = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = pad, Child = child };
            b.SetResourceReference(Border.BackgroundProperty, "CardSurfaceBrush");
            b.SetResourceReference(Border.BorderBrushProperty, "CardEdgeBrush");
            return b;
        }

        private static Border Pill(string text, Action onClick)
        {
            var tb = Txt(text, Ui.FontS);
            var b = new Border { Padding = new Thickness(11, 4, 11, 4), CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand, Child = tb, Background = Brushes.Transparent, BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent };
            b.MouseLeftButtonUp += (_, _) => onClick();
            return b;
        }

        private static void SetPill(Border b, bool on)
        {
            if (on)
            {
                b.SetResourceReference(Border.BackgroundProperty, "SelBtnBg");
                b.SetResourceReference(Border.BorderBrushProperty, "SelBtnBorderBrush");
                b.ClearValue(UIElement.EffectProperty);   // no shadow: it would blur the text
            }
            else { b.Background = Brushes.Transparent; b.BorderBrush = Brushes.Transparent; b.ClearValue(UIElement.EffectProperty); }
            if (b.Child is TextBlock t)
            {
                t.SetResourceReference(TextBlock.ForegroundProperty, on ? "SelBtnFg" : "TextSecondaryBrush");
                t.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        private static Border Track(params Border[] pills)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var p in pills) { p.Margin = new Thickness(0, 0, 2, 0); sp.Children.Add(p); }
            var t = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(3), Child = sp, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            t.SetResourceReference(Border.BackgroundProperty, "SegTrackBrush");
            return t;
        }

        private UIElement BuildUI()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // ── the bar: the disk, Scan, the state ──
            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = true };
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(right, Dock.Right);
            var lbl = Txt(S("Drive", "Drive"), Ui.FontS, "TextSecondaryBrush");
            lbl.Margin = new Thickness(0, 0, 8, 0);
            right.Children.Add(lbl);
            _drive = new ComboBox { MinWidth = 210, VerticalAlignment = VerticalAlignment.Center };
            if (Application.Current.TryFindResource("VanillaComboBoxStyle") is Style cs) _drive.Style = cs;
            _drive.SelectionChanged += (_, _) => OnDriveChanged();
            right.Children.Add(_drive);
            _btnScan = Net.NetWindowHelper.SecondaryButton(S("Scan", "Scan"));
            _btnScan.Margin = new Thickness(8, 0, 0, 0);
            _btnScan.Click += async (_, _) => await ScanOrStopAsync();
            right.Children.Add(_btnScan);
            bar.Children.Add(right);
            _status = Txt("", Ui.FontS, "TextSecondaryBrush");
            bar.Children.Add(_status);
            Grid.SetRow(bar, 0); root.Children.Add(bar);

            // ── totals ──
            var kpi = new Grid();
            for (int i = 0; i < 4; i++) kpi.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            FrameworkElement Kpi(int col, string label, string? dot, out TextBlock big, out TextBlock small, out TextBlock cap)
            {
                var sp = new StackPanel { Margin = new Thickness(col == 0 ? 0 : 18, 0, 0, 0) };
                var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                if (dot != null)
                {
                    var e = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                    e.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dot);
                    head.Children.Add(e);
                }
                cap = Txt(label, Ui.FontS, "TextSecondaryBrush");
                head.Children.Add(cap);
                sp.Children.Add(head);
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                big = Num("—", 26);
                small = Txt("", Ui.FontS, "TextSecondaryBrush");
                small.Margin = new Thickness(8, 6, 0, 0);
                line.Children.Add(big); line.Children.Add(small);
                sp.Children.Add(line);
                var holder = new Border { Child = sp, BorderThickness = new Thickness(col == 0 ? 0 : 1, 0, 0, 0) };
                holder.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                Grid.SetColumn(holder, col);
                return holder;
            }
            kpi.Children.Add(Kpi(0, S("KpiFree", "Free after cleanup"), null, out _kFree, out _kFreeAdd, out _));
            kpi.Children.Add(Kpi(1, S("KpiSafe", "Safe to remove"), "StatusGoodBrush", out _kSafe, out _kSafeN, out _));
            kpi.Children.Add(Kpi(2, S("KpiReview", "Check first"), "StatusCheckBrush", out _kReview, out _kReviewN, out _));
            kpi.Children.Add(Kpi(3, S("KpiScanned", "Scanned"), null, out _k4, out _k4Sub, out _k4Label));
            _kFreeAdd.SetResourceReference(TextBlock.ForegroundProperty, "StatusGoodBrush");
            var kpiCard = Card(kpi, new Thickness(18, 12, 18, 14));
            kpiCard.Margin = new Thickness(0, 0, 0, 12);
            Grid.SetRow(kpiCard, 1); root.Children.Add(kpiCard);

            // ── map | right ──
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 380 });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
            Grid.SetRow(body, 2); root.Children.Add(body);

            var mapGrid = new Grid();
            mapGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mapGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            mapGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 260 });
            mapGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var head2 = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            _pillFolders = Pill(S("ModeFolders", "Folders"), () => SetMode(false));
            _pillFiles = Pill(S("ModeFiles", "Largest files"), () => SetMode(true));
            var trk = Track(_pillFolders, _pillFiles);
            DockPanel.SetDock(trk, Dock.Right);
            head2.Children.Add(trk);
            var titles = new StackPanel();
            _mapTitle = Txt(S("FoldersTitle", "Folders"), Ui.FontXL, bold: true);
            _mapSub = Txt(S("FoldersSub", "Sized by space used. Click a block to open it."), Ui.FontS, "TextSecondaryBrush");
            titles.Children.Add(_mapTitle); titles.Children.Add(_mapSub);
            head2.Children.Add(titles);
            Grid.SetRow(head2, 0); mapGrid.Children.Add(head2);

            var nav = new StackPanel();
            _crumbs = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            _filters = new WrapPanel();
            _filtersTrack = new Border { Child = _filters, Tag = "SegTrack", Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
            _filtersTrack.SetResourceReference(FrameworkElement.StyleProperty, "SegmentTrackStyle");
            nav.Children.Add(_crumbs); nav.Children.Add(_filtersTrack);
            Grid.SetRow(nav, 1); mapGrid.Children.Add(nav);

            _map = new WpfCanvas { ClipToBounds = true, Background = Brushes.Transparent };
            _mapHost = new Border { Child = _map };
            _mapHost.SizeChanged += (_, _) => { _redraw.Stop(); _redraw.Start(); };
            Grid.SetRow(_mapHost, 2); mapGrid.Children.Add(_mapHost);
            _empty = BuildEmptyState();
            Grid.SetRow(_empty, 2); mapGrid.Children.Add(_empty);
            _scanning = BuildScanningState();
            _scanning.Visibility = Visibility.Collapsed;
            Grid.SetRow(_scanning, 2); mapGrid.Children.Add(_scanning);

            _legend = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            Grid.SetRow(_legend, 3); mapGrid.Children.Add(_legend);
            var mapCard = Card(mapGrid, new Thickness(16, 14, 16, 14));
            Grid.SetColumn(mapCard, 0); body.Children.Add(mapCard);

            var side = new Grid();
            side.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            side.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _selCard = BuildSelectedCard();
            _selCard.Visibility = Visibility.Collapsed;
            _selCard.Margin = new Thickness(0, 0, 0, 12);
            Grid.SetRow(_selCard, 0); side.Children.Add(_selCard);
            var recCard = BuildRecCard();
            Grid.SetRow(recCard, 1); side.Children.Add(recCard);
            Grid.SetColumn(side, 2); body.Children.Add(side);

            BuildFilters();
            BuildLegend();
            SetMode(false);
            return root;
        }

        /// <summary>
        /// Shown in the map card while a scan runs: what is happening, a progress bar and the current step, large enough to
        /// see — the small status line at the top alone made the page look frozen.
        /// </summary>
        private Border BuildScanningState()
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Width = 380 };
            var icon = FluentIcons.Box("search", 28, "AccentBrush");
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.Margin = new Thickness(0, 0, 0, 10);
            var t = Txt(S("ScanningTitle", "Scanning the drive…"), Ui.FontXL, bold: true);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            _scanBar = new System.Windows.Controls.ProgressBar { Height = 6, Minimum = 0, Maximum = 100, IsIndeterminate = true, Margin = new Thickness(0, 14, 0, 10) };
            _scanBar.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "AccentBrush");
            _scanText = new TextBlock { FontSize = Ui.FontS, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
            _scanText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            var hint = new TextBlock { Text = S("ScanningHint", "On a large drive this can take a minute. The page keeps working; Stop ends the scan."),
                                       FontSize = Ui.FontXS, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            sp.Children.Add(icon); sp.Children.Add(t); sp.Children.Add(_scanBar); sp.Children.Add(_scanText); sp.Children.Add(hint);
            return new Border { Child = sp, Background = Brushes.Transparent };
        }

        /// <summary>Shows the scan state in the map card; a "NN %" in the step text drives the bar.</summary>
        private void ShowScanning(bool on, string step = "")
        {
            _scanning.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on) return;
            _mapHost.Visibility = Visibility.Collapsed;
            _scanText.Text = step;
            var m = System.Text.RegularExpressions.Regex.Match(step, @"(\d{1,3})\s*%");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int pct))
            {
                _scanBar.IsIndeterminate = false;
                _scanBar.Value = Math.Clamp(pct, 0, 100);
            }
            else _scanBar.IsIndeterminate = true;
        }

        private Border BuildEmptyState()
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 380 };
            var t = Txt(S("EmptyTitle", "See what fills the drive"), Ui.FontXL, bold: true);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            var d = new TextBlock { Text = S("EmptyText", "Scan the drive to see which folders and files use the most space. On NTFS drives it takes a few seconds."), FontSize = Ui.FontS, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 6, 0, 14) };
            d.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            var b = Net.NetWindowHelper.PrimaryButton(S("ScanDrive", "Scan drive"));
            b.HorizontalAlignment = HorizontalAlignment.Center;
            b.Click += async (_, _) => await ScanOrStopAsync();
            sp.Children.Add(t); sp.Children.Add(d); sp.Children.Add(b);
            return new Border { Child = sp, Background = Brushes.Transparent };
        }

        private Border BuildSelectedCard()
        {
            var sp = new StackPanel();
            sp.Children.Add(Txt(S("SelectedTitle", "Selected file"), Ui.FontXL, bold: true));
            _selName = Txt("", Ui.FontL, bold: true); _selName.Margin = new Thickness(0, 10, 0, 0);
            _selDir = Txt("", Ui.FontXS, "TextSecondaryBrush");
            if (Application.Current.TryFindResource("MonoFont") is FontFamily mf) _selDir.FontFamily = mf;
            sp.Children.Add(_selName); sp.Children.Add(_selDir);
            var g = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            for (int i = 0; i < 3; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            StackPanel Fact(int col, string label, out TextBlock val)
            {
                var f = new StackPanel();
                f.Children.Add(Txt(label, Ui.FontXS, "TextSecondaryBrush"));
                val = Txt("", Ui.FontM, bold: true);
                f.Children.Add(val);
                Grid.SetColumn(f, col);
                return f;
            }
            g.Children.Add(Fact(0, S("SelSize", "Size"), out _selSize));
            g.Children.Add(Fact(1, S("SelType", "Type"), out _selType));
            g.Children.Add(Fact(2, S("SelChanged", "Last changed"), out _selDate));
            sp.Children.Add(g);
            _selHint = new TextBlock { FontSize = Ui.FontS, TextWrapping = TextWrapping.Wrap };
            _selHint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            var hintBox = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 10, 0, 0), Child = _selHint };
            hintBox.SetResourceReference(Border.BackgroundProperty, "SegTrackBrush");
            sp.Children.Add(hintBox);
            var btns = new WrapPanel { Margin = new Thickness(-4, 12, 0, 0) };
            var open = Net.NetWindowHelper.SecondaryButton(S("OpenFolder", "Open folder"));
            open.Click += (_, _) => { if (_selected != null) RevealInExplorer(_selected.FullPath); };
            var copy = Net.NetWindowHelper.SecondaryButton(S("CopyPath", "Copy path"));
            copy.Click += (_, _) => { if (_selected != null) try { System.Windows.Clipboard.SetText(_selected.FullPath); } catch (Exception ex) { AppLogger.Debug("Copy path: " + ex.Message); } };
            _selRecycle = Net.NetWindowHelper.SecondaryButton(S("ToRecycle", "Move to Recycle Bin"));
            _selRecycle.Click += (_, _) => RecycleSelected();
            btns.Children.Add(open); btns.Children.Add(copy); btns.Children.Add(_selRecycle);
            sp.Children.Add(btns);
            return Card(sp, new Thickness(16, 14, 16, 14));
        }

        private Border BuildRecCard()
        {
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var head = new StackPanel { Margin = new Thickness(16, 14, 16, 8) };
            head.Children.Add(Txt(S("RecTitle", "Recommendations"), Ui.FontXL, bold: true));
            var sub = new TextBlock { Text = S("RecSub", "Caches are deleted; your own files go to the Recycle Bin."), FontSize = Ui.FontS, TextWrapping = TextWrapping.Wrap };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            head.Children.Add(sub);
            Grid.SetRow(head, 0); g.Children.Add(head);
            _recList = new StackPanel();
            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _recList };
            Grid.SetRow(sv, 1); g.Children.Add(sv);
            var foot = new DockPanel { Margin = new Thickness(16, 10, 12, 12), LastChildFill = true };
            var fb = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(fb, Dock.Right);
            var selSafe = Net.NetWindowHelper.SecondaryButton(S("SelectSafe", "Select safe"));
            selSafe.Click += (_, _) => { foreach (var r in _recs) if (_recChecks.TryGetValue(r.Key, out var c)) c.IsChecked = r.Safe; UpdateTotals(); };
            _btnClean = Net.NetWindowHelper.PrimaryButton(S("CleanUp", "Clean up"));
            _btnClean.Click += async (_, _) => await CleanSelectedAsync();
            fb.Children.Add(selSafe); fb.Children.Add(_btnClean);
            foot.Children.Add(fb);
            _recSum = Txt("", Ui.FontS);
            foot.Children.Add(_recSum);
            var footBorder = new Border { Child = foot, BorderThickness = new Thickness(0, 1, 0, 0) };
            footBorder.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            Grid.SetRow(footBorder, 2); g.Children.Add(footBorder);
            return Card(g, new Thickness(0));
        }

        private void BuildFilters()
        {
            _filters.Children.Clear();
            foreach (var (key, text) in new[]
            {
                ("All", S("FilterAll", "All")), ("Media", S("FilterMedia", "Videos & media")), ("Images", S("FilterImages", "Disk images & installers")),
                ("Archives", S("FilterArchives", "Archives")), ("Old", S("FilterOld", "Older than 1 year")),
            })
            {
                var b = Net.NetWindowHelper.SecondaryButton(text);
                b.Padding = new Thickness(10, 4, 10, 4);
                b.Margin = new Thickness(0, 0, 2, 0);
                b.Tag = key;
                b.Click += (_, _) => { _filter = key; RefreshFilters(); DrawMap(); };
                _filters.Children.Add(b);
            }
            RefreshFilters();
        }

        private void RefreshFilters()
        {
            foreach (var c in _filters.Children.OfType<Wpf.Ui.Controls.Button>())
                MainWindow.SetTabButtonActive(c, (string)c.Tag == _filter);
        }

        private void BuildLegend()
        {
            _legend.Children.Clear();
            var kinds = _filesMode
                ? new[] { SpaceKind.Media, SpaceKind.Images, SpaceKind.Archives, SpaceKind.Games, SpaceKind.Windows, SpaceKind.Other }
                : new[] { SpaceKind.Media, SpaceKind.Downloads, SpaceKind.Programs, SpaceKind.Games, SpaceKind.Windows, SpaceKind.Safe, SpaceKind.Other };
            foreach (var k in kinds)
            {
                var sw = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0), Background = SwatchBrush(k), VerticalAlignment = VerticalAlignment.Center };
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 4) };
                sp.Children.Add(sw);
                sp.Children.Add(Txt(KindName(k), Ui.FontXS, "TextSecondaryBrush"));
                _legend.Children.Add(sp);
            }
        }

        internal static string KindName(SpaceKind k) => k switch
        {
            SpaceKind.Media     => S("KindMedia", "Documents & media"),
            SpaceKind.Downloads => S("KindDownloads", "Downloads"),
            SpaceKind.Programs  => S("KindPrograms", "Programs & app data"),
            SpaceKind.Games     => S("KindGames", "Games"),
            SpaceKind.Windows   => S("KindWindows", "Windows"),
            SpaceKind.Safe      => S("KindSafe", "Safe to remove"),
            SpaceKind.Archives  => S("KindArchives", "Archives"),
            SpaceKind.Images    => S("KindImages", "Disk images & installers"),
            _                   => S("KindOther", "Other"),
        };

        private void SetMode(bool files)
        {
            _filesMode = files;
            SetPill(_pillFolders, !files);
            SetPill(_pillFiles, files);
            _mapTitle.Text = files ? S("FilesTitle", "Largest files") : S("FoldersTitle", "Folders");
            _mapSub.Text = files ? S("FilesSub", "The 50 largest files, sized by space. Click a block to select it.")
                                 : S("FoldersSub", "Sized by space used. Click a block to open it.");
            _filtersTrack.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
            _crumbs.Visibility = files ? Visibility.Collapsed : Visibility.Visible;
            BuildLegend();
            UpdateTotals();
            DrawMap();
        }

        // ── Disks and scanning ─────────────────────────────────────────────────

        private async Task FillDrivesAsync()
        {
            try
            {
                var drives = await DriveSnapshot.ReadyDrivesAsync();
                string sys = System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                _drive.Items.Clear();
                foreach (var d in drives.Where(d => d.Type is System.IO.DriveType.Fixed or System.IO.DriveType.Removable).OrderBy(d => d.Letter))
                {
                    string label = string.IsNullOrWhiteSpace(d.Label) ? (d.Root.Equals(sys, StringComparison.OrdinalIgnoreCase) ? "Windows" : d.Format) : d.Label;
                    _drive.Items.Add(new ComboBoxItem { Content = $"{d.Letter} {label} · {DiskSize(d.TotalBytes)}", Tag = d });
                }
                var pick = _drive.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ((DriveSnapshot)i.Tag).Root.Equals(sys, StringComparison.OrdinalIgnoreCase))
                           ?? _drive.Items.OfType<ComboBoxItem>().FirstOrDefault();
                if (pick != null) _drive.SelectedItem = pick;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "SpaceView.FillDrives"); }
        }

        private DriveSnapshot? SelectedDrive => (_drive.SelectedItem as ComboBoxItem)?.Tag as DriveSnapshot;

        private bool IsSystemDrive => SelectedDrive?.Root.Equals(System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", StringComparison.OrdinalIgnoreCase) == true;

        private void OnDriveChanged()
        {
            if (_scan != null && SelectedDrive != null && !_scan.Root.Path.StartsWith(SelectedDrive.Root, StringComparison.OrdinalIgnoreCase))
            {
                _scan = null; _cur = null; _selected = null;
                _selCard.Visibility = Visibility.Collapsed;
            }
            UpdateTotals();
            DrawMap();
        }

        // each scan has a number; Stop changes it, so the result of a stopped scan is ignored when it arrives — the window
        // returns immediately, without waiting for the scan thread.
        private int _scanGen;

        /// <summary>If the window stops responding for this many milliseconds during the scan, the scan stops by itself.</summary>
        private const int StallLimitMs = 8000;

        private async Task ScanOrStopAsync()
        {
            if (_cts != null) { StopScan(); return; }
            var drive = SelectedDrive;
            if (drive == null) return;
            int gen = ++_scanGen;
            var cts = new CancellationTokenSource();
            _cts = cts;
            var ct = cts.Token;
            _btnScan.Content = S("Stop", "Stop");
            _empty.Visibility = Visibility.Collapsed;

            // memorie libera putina: scanarea folder cu folder (lenta, dar cere putina memorie) in locul indexului NTFS
            long freeMB = RamFullScanService.GetAvailablePhysicalMB();
            bool lowMemory = freeMB > 0 && freeMB < SpaceScanner.LowMemoryMB;
            string slowNote = lowMemory
                ? string.Format(S("LowMemoryNote", "little free memory ({0:F1} GB), slower scan used"), freeMB / 1024.0)
                : "";
            _status.Text = lowMemory ? S("Scanning", "Scanning…") + " · " + slowNote : S("Scanning", "Scanning…");
            ShowScanning(true, _status.Text);
            var progress = new Progress<string>(s =>
            {
                if (gen != _scanGen) return;
                _status.Text = lowMemory ? s + " · " + slowNote : s;
                ShowScanning(true, _status.Text);
            });

            // window blocked > 8 s (PC out of memory, busy disk): the scan stops, to free the memory
            bool stalled = false;
            var watchdog = new UiStallWatchdog(Dispatcher, StallLimitMs, () =>
            {
                stalled = true;
                AppLogger.Warning($"SpaceView: the window did not respond for {StallLimitMs / 1000} s during the scan — scan stopped (free memory {RamFullScanService.GetAvailablePhysicalMB():N0} MB)");
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            });

            SpaceScan? scan = null;
            bool failed = false;
            try
            {
                scan = await SpaceScanner.ScanInBackgroundAsync(drive.Root, lowMemory, ct, progress);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "SpaceView.Scan");
                failed = true;
            }
            finally
            {
                watchdog.Dispose();
                cts.Dispose();
            }

            if (gen != _scanGen) return;     // stopped with Stop: the window has already returned (StopScan)
            _cts = null;
            ShowScanning(false);
            _mapHost.Visibility = Visibility.Visible;
            if (scan != null)
            {
                _scan = scan; _cur = scan.Root; _selected = null;
                _selCard.Visibility = Visibility.Collapsed;
                _status.Text = string.Format(S("ScanDone", "{0} files · {1:F1} s"), scan.Root.Files.ToString("N0"), scan.Seconds)
                             + (scan.Fast ? " · " + S("FastScan", "NTFS index") : "")
                             + (lowMemory ? " · " + slowNote : "");
            }
            else
                _status.Text = stalled ? S("ScanStalled", "Scan stopped: the PC stopped responding (very little free memory). Close large programs and scan again.")
                             : failed  ? S("ScanError", "The scan stopped because of an error. Details are in the log.")
                             : S("ScanStopped", "Scan stopped.");
            _btnScan.Content = _scan != null ? S("ScanAgain", "Scan again") : S("Scan", "Scan");
            if (_recs.Count == 0) await LoadRecsAsync();
            UpdateTotals();
            DrawMap();
        }

        /// <summary>Stop: cancels the scan and brings the window back immediately. The scan thread stops by itself at the next
        /// check (at most a few seconds) and frees its memory; its result is ignored.</summary>
        private void StopScan()
        {
            var cts = _cts;
            _cts = null;
            _scanGen++;
            try { cts?.Cancel(); } catch (ObjectDisposedException) { }
            ShowScanning(false);
            _mapHost.Visibility = Visibility.Visible;
            if (_scan == null) _empty.Visibility = Visibility.Visible;
            _status.Text = S("ScanStopped", "Scan stopped.");
            _btnScan.Content = _scan != null ? S("ScanAgain", "Scan again") : S("Scan", "Scan");
            UpdateTotals();
            DrawMap();
        }

        /// <summary>
        /// The window watchdog: every half second, from a separate thread, it sends a "signal" through the window.
        /// If the signals stop arriving for <c>limitMs</c> (window blocked), it calls <c>onStall</c> once, also from the separate
        /// thread — so it works even when the window does not respond.
        /// </summary>
        private sealed class UiStallWatchdog : IDisposable
        {
            private readonly System.Threading.Timer _timer;
            private long _lastBeat = Environment.TickCount64;
            private int _fired;

            public UiStallWatchdog(System.Windows.Threading.Dispatcher dispatcher, int limitMs, Action onStall)
            {
                _timer = new System.Threading.Timer(_ =>
                {
                    try
                    {
                        dispatcher.BeginInvoke(new Action(() => Interlocked.Exchange(ref _lastBeat, Environment.TickCount64)),
                                               System.Windows.Threading.DispatcherPriority.Input);
                        if (Environment.TickCount64 - Interlocked.Read(ref _lastBeat) > limitMs && Interlocked.Exchange(ref _fired, 1) == 0)
                            onStall();
                    }
                    catch (Exception ex) { AppLogger.Debug("UiStallWatchdog: " + ex.Message); }
                }, null, 500, 500);
            }

            public void Dispose() => _timer.Dispose();
        }

        // ── Recomandari ────────────────────────────────────────────────────────

        // the last estimate, shared by every SpaceView: the view is rebuilt on a language change, and the list stayed empty
        // until the new estimate finished
        private static List<SpaceCleanup.Rec>? _lastRecs;

        private async Task LoadRecsAsync()
        {
            if (_recs.Count == 0 && _lastRecs is { Count: > 0 } cached)
                ShowRecs(cached);              // at once, from the last estimate…
            try
            {
                var fresh = await Task.Run(SpaceCleanup.Estimate);
                _lastRecs = fresh;
                ShowRecs(fresh);               // …then refreshed
            }
            catch (Exception ex) { AppLogger.Warning(ex, "SpaceView.LoadRecs"); }
        }

        private void ShowRecs(List<SpaceCleanup.Rec> recs)
        {
            try
            {
                var keep = _recChecks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToHashSet();
                bool first = _recChecks.Count == 0;
                _recs = recs;
                _recList.Children.Clear();
                _recChecks.Clear();
                foreach (bool safe in new[] { true, false })
                {
                    var group = _recs.Where(r => r.Safe == safe).ToList();
                    if (group.Count == 0) continue;
                    var gh = new DockPanel { Margin = new Thickness(16, 10, 16, 4) };
                    var tagText = Txt(safe ? S("GroupSafe", "Safe to remove") : S("GroupReview", "Check first"), Ui.FontXS, safe ? "StatusGoodBrush" : "StatusCheckBrush", bold: true);
                    var total = Num(Size(group.Sum(r => r.Size)), Ui.FontS);
                    total.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    DockPanel.SetDock(total, Dock.Right);
                    gh.Children.Add(total); gh.Children.Add(tagText);
                    _recList.Children.Add(gh);
                    foreach (var r in group)
                    {
                        var row = new DockPanel { Margin = new Thickness(16, 0, 16, 0) };
                        var size = Num(Size(r.Size), Ui.FontM);
                        DockPanel.SetDock(size, Dock.Right);
                        var cb = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), IsChecked = first ? r.Safe : keep.Contains(r.Key) };
                        if (Application.Current.TryFindResource("VanillaCheckBoxStyle") is Style cbs) cb.Style = cbs;
                        cb.Checked += (_, _) => UpdateTotals();
                        cb.Unchecked += (_, _) => UpdateTotals();
                        _recChecks[r.Key] = cb;
                        DockPanel.SetDock(cb, Dock.Left);
                        var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                        txt.Children.Add(Txt(r.Name + (r.Count > 0 ? $" · {r.Count:N0}" : ""), Ui.FontM, bold: true));
                        var where = Txt(r.Where, Ui.FontXS, "TextSecondaryBrush");
                        if (Application.Current.TryFindResource("MonoFont") is FontFamily mf) where.FontFamily = mf;
                        txt.Children.Add(where);
                        row.Children.Add(size); row.Children.Add(cb); row.Children.Add(txt);
                        var wrap = new Border { Child = row, Padding = new Thickness(0, 8, 0, 8), BorderThickness = new Thickness(0, 1, 0, 0), Cursor = Cursors.Hand };
                        wrap.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                        wrap.MouseLeftButtonUp += (_, e) => { if (!IsInside(e.OriginalSource as DependencyObject, cb)) cb.IsChecked = cb.IsChecked != true; };
                        _recList.Children.Add(wrap);
                    }
                }
                if (_recs.Count == 0)
                {
                    var none = Txt(S("RecNone", "Nothing to clean right now."), Ui.FontS, "TextSecondaryBrush");
                    none.Margin = new Thickness(16, 10, 16, 10);
                    _recList.Children.Add(none);
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "SpaceView.ShowRecs"); }
            UpdateTotals();
        }

        /// <summary>The click came from the checkbox itself (which checks itself) or from the rest of the row.</summary>
        private static bool IsInside(DependencyObject? node, DependencyObject target)
        {
            for (int i = 0; node != null && i < 30; i++)
            {
                if (ReferenceEquals(node, target)) return true;
                node = node is Visual || node is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
            }
            return false;
        }

        private List<SpaceCleanup.Rec> ChosenRecs() =>
            _recs.Where(r => _recChecks.TryGetValue(r.Key, out var c) && c.IsChecked == true).ToList();

        private async Task CleanSelectedAsync()
        {
            var chosen = ChosenRecs();
            if (chosen.Count == 0) return;
            long total = chosen.Sum(r => r.Size);
            string list = string.Join("\n", chosen.Select(r => $"• {r.Name} — {Size(r.Size)}"));
            string msg = string.Format(S("CleanConfirm", "Clean up {0}?\n\n{1}\n\nCaches are deleted. Downloads and installers go to the Recycle Bin."), Size(total), list);
            if (!AppDialog.ConfirmDanger(msg, S("RecTitle", "Recommendations"), S("CleanUp", "Clean up"), LanguageService.S("Common", "Cancel"), Window.GetWindow(this))) return;
            _btnClean.IsEnabled = false;
            _status.Text = S("Cleaning", "Cleaning…");
            try
            {
                long freed = await Task.Run(() => SpaceCleanup.Clean(chosen));
                _status.Text = string.Format(S("Freed", "{0} freed. Scan again to update the map."), Size(freed));
            }
            catch (Exception ex) { AppLogger.Warning(ex, "SpaceView.Clean"); }
            finally { _btnClean.IsEnabled = true; }
            await LoadRecsAsync();
        }

        // ── Totals ───────────────────────────────────────────────────────────

        private void UpdateTotals()
        {
            long safe = _recs.Where(r => r.Safe).Sum(r => r.Size), review = _recs.Where(r => !r.Safe).Sum(r => r.Size);
            _kSafe.Text = _recs.Count == 0 ? "—" : Size(safe);
            _kSafeN.Text = string.Format(S("ItemsFmt", "{0} items"), _recs.Count(r => r.Safe));
            _kReview.Text = _recs.Count == 0 ? "—" : Size(review);
            _kReviewN.Text = string.Format(S("ItemsFmt", "{0} items"), _recs.Count(r => !r.Safe));
            var chosen = ChosenRecs();
            long sel = chosen.Sum(r => r.Size);
            _recSum.Text = string.Format(S("SelectedFmt", "{0} selected · {1} items"), Size(sel), chosen.Count);
            _btnClean.IsEnabled = chosen.Count > 0;

            var d = SelectedDrive;
            long free = _scan?.DriveFree > 0 ? _scan.DriveFree : d?.FreeBytes ?? 0;
            long add = IsSystemDrive ? sel : 0;
            _kFree.Text = free > 0 ? Size(free + add) : "—";
            _kFreeAdd.Text = add > 0 ? "+" + Size(add) : "";

            if (_scan == null)
            {
                // before the first scan: say so instead of "—" + "0 items" (read as values that failed, 16.11.2026 self-test)
                _k4Label.Text = S("KpiScanned", "Scanned");
                _k4.Text = S("NotScanned", "not yet");
                _k4Sub.Text = d != null ? string.Format(S("UsedOfFmt", "{0} used"), DiskSize(d.TotalBytes - d.FreeBytes)) : "";
                _kSafeN.Text = _kReviewN.Text = S("AfterScan", "after the scan");
            }
            else if (_filesMode)
            {
                var top = TopFiles(int.MaxValue, "All").Take(50).ToList();
                long sum = top.Sum(f => f.Size);
                long used = Math.Max(1, _scan.DriveTotal - _scan.DriveFree);
                _k4Label.Text = S("KpiTop50", "50 largest files");
                _k4.Text = Size(sum);
                _k4Sub.Text = string.Format(S("PctOfUsedFmt", "{0:P0} of used space"), (double)sum / used);
            }
            else
            {
                long used = Math.Max(0, _scan.DriveTotal - _scan.DriveFree);
                _k4Label.Text = S("KpiScanned", "Scanned");
                _k4.Text = Size(_scan.Root.Size);
                _k4Sub.Text = string.Format(S("ScannedSubFmt", "of {0} used · {1:F0} s"), DiskSize(used), Math.Max(1, _scan.Seconds));
            }
        }

        // ── The chosen file ──────────────────────────────────────────────────────

        private void Select(SpaceFile f)
        {
            _selected = f;
            _selCard.Visibility = Visibility.Visible;
            _selName.Text = f.Name;
            _selDir.Text = f.Directory;
            _selSize.Text = Size(f.Size);
            var k = f.Kind;
            _selType.Text = KindName(k);
            _selDate.Text = f.Modified > DateTime.MinValue.AddYears(1) ? AgeText(f.Modified) : "—";
            _selDate.SetResourceReference(TextBlock.ForegroundProperty, f.Modified < DateTime.Now.AddYears(-1) && f.Modified > DateTime.MinValue.AddYears(1) ? "StatusCheckBrush" : "TextPrimaryBrush");
            _selHint.Text = k switch
            {
                SpaceKind.Windows when f.IsSystem => S("HintSystem", "Used by Windows (virtual memory or hibernation). It cannot be removed from here."),
                SpaceKind.Images   => S("HintImages", "An installation file or disk image. Usually not needed after the program or Windows is installed."),
                SpaceKind.Archives => S("HintArchives", "A compressed archive — often a backup or a download that was already unpacked."),
                SpaceKind.Media    => S("HintMedia", "A video, photo or music file. Copy it to an external drive before removing it."),
                SpaceKind.Games    => S("HintGames", "Game data. Remove the game from its launcher (Steam, Epic…), not the file."),
                SpaceKind.Programs => S("HintPrograms", "Part of an installed program. Uninstall the program instead of removing the file."),
                SpaceKind.Safe     => S("HintSafe", "Temporary or cache data — safe to remove."),
                _                  => S("HintOther", "Check what it is before removing it."),
            };
            _selRecycle.IsEnabled = !f.IsSystem;
            DrawMap();
        }

        private void RecycleSelected()
        {
            var f = _selected;
            if (f == null || f.IsSystem) return;
            string warn = f.Kind is SpaceKind.Programs or SpaceKind.Games ? "\n\n" + S("RecycleWarnApp", "This file belongs to a program or game — it may stop working.") : "";
            string msg = string.Format(S("RecycleConfirm", "Move {0} ({1}) to the Recycle Bin?"), f.Name, Size(f.Size)) + warn;
            if (!AppDialog.ConfirmDanger(msg, S("ToRecycle", "Move to Recycle Bin"), S("ToRecycle", "Move to Recycle Bin"), LanguageService.S("Common", "Cancel"), Window.GetWindow(this))) return;
            long freed = SpaceCleanup.ToRecycleBin(f.FullPath);
            if (freed <= 0) { _status.Text = S("RecycleFailed", "The file could not be moved (in use or no access)."); return; }
            f.Parent?.RemoveFile(f);
            _selected = null;
            _selCard.Visibility = Visibility.Collapsed;
            if (_scan != null) _scan.DriveFree += freed;
            _status.Text = string.Format(S("Freed", "{0} freed. Scan again to update the map."), Size(freed));
            UpdateTotals();
            DrawMap();
        }

        private static void RevealInExplorer(string path)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
            catch (Exception ex) { AppLogger.Warning(ex, "SpaceView.Reveal"); }
        }

        // ── Formatare ──────────────────────────────────────────────────────────

        internal static string Size(long b)
        {
            if (b <= 0) return "0 GB";
            double gb = b / 1073741824.0;
            if (gb >= 100) return $"{gb:F0} GB";
            if (gb >= 1) return $"{gb:F1} GB";
            double mb = b / 1048576.0;
            return mb >= 10 ? $"{mb:F0} MB" : $"{mb:F1} MB";
        }

        private static string DiskSize(long b) => $"{b / 1073741824.0:F0} GB";

        private static string AgeText(DateTime t)
        {
            var span = DateTime.Now - t;
            if (span.TotalDays < 1) return S("AgeToday", "today");
            if (span.TotalDays < 60) return string.Format(S("AgeDays", "{0} days ago"), (int)span.TotalDays);
            if (span.TotalDays < 730) return string.Format(S("AgeMonths", "{0} months ago"), (int)(span.TotalDays / 30.4));
            return string.Format(S("AgeYears", "{0} years ago"), (int)(span.TotalDays / 365.25));
        }
    }
}
