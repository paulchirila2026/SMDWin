using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;

using WpfColor         = System.Windows.Media.Color;
using WpfBrush         = System.Windows.Media.Brush;
using WpfSolidBrush    = System.Windows.Media.SolidColorBrush;
using WpfFontFamily    = System.Windows.Media.FontFamily;
using Button           = System.Windows.Controls.Button;
using CheckBox         = System.Windows.Controls.CheckBox;
using TextBox          = System.Windows.Controls.TextBox;
using TextBlock        = System.Windows.Controls.TextBlock;
using ScrollViewer     = System.Windows.Controls.ScrollViewer;
using StackPanel       = System.Windows.Controls.StackPanel;
using Border           = System.Windows.Controls.Border;
using Orientation      = System.Windows.Controls.Orientation;
using ComboBox         = System.Windows.Controls.ComboBox;
using ComboBoxItem     = System.Windows.Controls.ComboBoxItem;
using Brushes          = System.Windows.Media.Brushes;
using ColorConverter   = System.Windows.Media.ColorConverter;
using WpfPoint         = System.Windows.Point;
using WpfClipboard     = System.Windows.Clipboard;

namespace SMDWin.Views
{
    public partial class DebugInfoWindow : Window
    {
        // ── Navigation ───────────────────────────────────────────────────────
        private const int TabCount = 7;
        private readonly Button[]    _navBtns  = new Button[TabCount];
        private readonly Border[]    _navLines = new Border[TabCount];
        private readonly UIElement[] _pages    = new UIElement[TabCount];

        // ── Page controls ────────────────────────────────────────────────────
        private StackPanel   _checkPanel  = new();
        private TextBox?     _settingsTxt;
        private TextBox      _logBox      = new();
        private ScrollViewer _logScroll   = new();

        // ── Self-Test page controls ──────────────────────────────────────────
        private TextBox       _selfTestBox      = new();
        private ScrollViewer  _selfTestScroll    = new();
        private Button        _selfTestRunBtn    = new();
        private TextBlock     _selfTestStatus    = new();
        private System.Threading.CancellationTokenSource? _selfTestCts;
        private bool _selfTestRunning = false;

        public DebugInfoWindow()
        {
            Title  = "SMDWin — Developer Panel";
            Width  = 860; Height = 640;
            MinWidth = 720; MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode  = ResizeMode.CanResizeWithGrip;
            FontFamily  = new WpfFontFamily("Segoe UI Variable Text, Segoe UI");
            UseLayoutRounding   = true;
            SnapsToDevicePixels = true;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = System.Windows.Media.Brushes.Transparent;
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                CornerRadius = new CornerRadius(0),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false,
            });

            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };

            Loaded += (_, _) =>
            {
                try
                {
                    var helper   = new System.Windows.Interop.WindowInteropHelper(this);
                    var resolved = ThemeManager.Normalize(SettingsService.Current.ThemeName);
                    ThemeManager.ApplyTitleBarColor(helper.Handle, resolved);
                    if (ThemeManager.Themes.TryGetValue(resolved, out var t))
                        ThemeManager.SetCaptionColor(helper.Handle, t["BgDark"]);
                    ThemeManager.Apply(SettingsService.Current.ThemeName, Resources);
                }
                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }

                void OnThemeChanged()
                {
                    try
                    {
                        var helper2   = new System.Windows.Interop.WindowInteropHelper(this);
                        var resolved2 = ThemeManager.Normalize(SettingsService.Current.ThemeName);
                        ThemeManager.ApplyTitleBarColor(helper2.Handle, resolved2);
                        if (ThemeManager.Themes.TryGetValue(resolved2, out var t2))
                            ThemeManager.SetCaptionColor(helper2.Handle, t2["BgDark"]);
                        ThemeManager.Apply(SettingsService.Current.ThemeName, Resources);
                    }
                    catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                }
                ThemeManager.ThemeChanged += OnThemeChanged;
                Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
                SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
                RefreshAll();
            };

            double radiusCardForWin = (System.Windows.Application.Current.Resources["RadiusCard"] as CornerRadius?)?.TopLeft ?? 8;
            var roundedContent = new Border
            {
                CornerRadius = new CornerRadius(radiusCardForWin),
                ClipToBounds = true,
            };
            roundedContent.SetResourceReference(Border.BackgroundProperty, "BgDarkBrush");
            roundedContent.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            roundedContent.BorderThickness = new Thickness(1);
            roundedContent.Child = BuildShell();
            // the shadow on its own layer (an effect on the content would blur all the text in the window)
            var shadowWrap = Ui.ShadowLayer(roundedContent, new CornerRadius(radiusCardForWin), new Thickness(12),
                new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 24, ShadowDepth = 6, Direction = 270,
                    Color = System.Windows.Media.Colors.Black, Opacity = 0.35,
                }, "BgDarkBrush");
            Content = shadowWrap;
            PolishButtons(roundedContent);
        }

        // ══════════════════════════════════════════════════════════════════════
        // SHELL
        // ══════════════════════════════════════════════════════════════════════
        UIElement BuildShell()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ── Header ───────────────────────────────────────────────────────
            var header = new Border { Padding = new Thickness(24, 14, 24, 14) };
            header.SetResourceReference(Border.BackgroundProperty, "BgDarkBrush");
            header.MouseLeftButtonDown += (_, me) => { if (me.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };

            var hg = new Grid();
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel();
            var titleTb = new TextBlock { Text = "Developer Panel", FontSize = 20, FontWeight = FontWeights.SemiBold };
            if (System.Windows.Application.Current.Resources["NumFont"] is WpfFontFamily numFont) titleTb.FontFamily = numFont;
            titleTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

            var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            void AddMeta(string s)
            {
                var tb = new TextBlock { Text = s, FontSize = Ui.FontS, Opacity = 0.45, VerticalAlignment = VerticalAlignment.Center };
                tb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                meta.Children.Add(tb);
            }
            AddMeta("SMDWin  ·  ");
            var __dbgAsmVer = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            string __dbgVerStr = SMDWin.Services.AppVersionInfo.Short;
            AddMeta($"{SMDWin.Services.DateFormatHelper.FormatDateTime(DateTime.Now)}  ·  {__dbgVerStr}  ·  ");

            bool admin = IsAdmin();
            meta.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Background = admin
                    ? new WpfSolidBrush(WpfColor.FromArgb(45, 34, 197, 94))
                    : new WpfSolidBrush(WpfColor.FromArgb(45, 239, 68, 68)),
                Child = new TextBlock
                {
                    Text = admin ? "● Admin" : "● User",
                    FontSize = Ui.FontXS, FontWeight = FontWeights.SemiBold,
                    Foreground = admin
                        ? new WpfSolidBrush(WpfColor.FromRgb(34, 197, 94))
                        : new WpfSolidBrush(WpfColor.FromRgb(239, 68, 68))
                }
            });

            titleStack.Children.Add(titleTb);
            titleStack.Children.Add(meta);
            Grid.SetColumn(titleStack, 0);

            var btnRefresh = Net.NetWindowHelper.SecondaryButton("Refresh", "Re-read status, settings and log");
            btnRefresh.VerticalAlignment = VerticalAlignment.Center;
            btnRefresh.Click += (_, _) => RefreshAll();
            Grid.SetColumn(btnRefresh, 1);

            var btnCloseDbg = Net.NetWindowHelper.CloseIconButton(32);
            btnCloseDbg.VerticalAlignment = VerticalAlignment.Center;
            btnCloseDbg.Margin = new Thickness(6, 0, 0, 0);
            btnCloseDbg.Click += (_, _) => Close();
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(btnCloseDbg, true);
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(btnRefresh, true);
            Grid.SetColumn(btnCloseDbg, 2);

            hg.Children.Add(titleStack);
            hg.Children.Add(btnRefresh);
            hg.Children.Add(btnCloseDbg);
            header.Child = hg;
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // ── Nav bar ──────────────────────────────────────────────────────
            var navBar = new Border
            {
                Padding = new Thickness(12, 0, 12, 0),
                BorderThickness = new Thickness(0, 0, 0, 1),
            };
            navBar.SetResourceReference(Border.BackgroundProperty, "BgDarkBrush");
            navBar.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2");

            var navRow = new StackPanel { Orientation = Orientation.Horizontal };
            string[] labels = { "Status", "Settings", "Log", "Self-Test", "Design", "Experimental", "Databases" };

            for (int i = 0; i < TabCount; i++)
            {
                int idx = i;

                // Container: button on top, accent line on bottom
                var container = new Grid { Margin = new Thickness(2, 0, 2, 0) };
                container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                container.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2) });

                var btn = new Button
                {
                    Content = labels[i],
                    FontSize = Ui.FontM, FontWeight = FontWeights.Normal,
                    Padding = new Thickness(14, 10, 14, 10),
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    FocusVisualStyle = null,
                };
                btn.SetResourceReference(Button.ForegroundProperty, "TextSecondaryBrush");
                btn.Click += (_, _) => SwitchTab(idx);
                // Suppress default WPF button chrome
                var template = new ControlTemplate(typeof(Button));
                var factory = new FrameworkElementFactory(typeof(Border));
                factory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
                factory.SetValue(Border.PaddingProperty, new Thickness(14, 10, 14, 10));
                var cp = new FrameworkElementFactory(typeof(ContentPresenter));
                cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
                cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                factory.AppendChild(cp);
                template.VisualTree = factory;
                btn.Template = template;
                Grid.SetRow(btn, 0);

                var line = new Border
                {
                    Height = 2, Background = Brushes.Transparent,
                    CornerRadius = new CornerRadius(1, 1, 0, 0),
                    Margin = new Thickness(8, 0, 8, 0),
                };
                Grid.SetRow(line, 1);

                container.Children.Add(btn);
                container.Children.Add(line);
                navRow.Children.Add(container);

                _navBtns[i]  = btn;
                _navLines[i] = line;
            }

            navBar.Child = navRow;
            Grid.SetRow(navBar, 1);
            root.Children.Add(navBar);

            // ── Page host ────────────────────────────────────────────────────
            _pages[0] = BuildStatusPage();
            _pages[1] = BuildSettingsPage();
            _pages[2] = BuildLogPage();
            _pages[3] = BuildSelfTestPage();
            _pages[4] = BuildDesignPage();
            _pages[5] = BuildExperimentalPage();
            _pages[6] = BuildDatabasesPage();

            var pageGrid = new Grid();
            foreach (var p in _pages)
            {
                p.Visibility = Visibility.Collapsed;
                pageGrid.Children.Add(p);
            }
            Grid.SetRow(pageGrid, 2);
            root.Children.Add(pageGrid);

            // ── Footer ───────────────────────────────────────────────────────
            var footer = new Border
            {
                Padding = new Thickness(24, 7, 24, 7),
                BorderThickness = new Thickness(0, 1, 0, 0),
            };
            footer.SetResourceReference(Border.BackgroundProperty, "BgDarkBrush");
            footer.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2");

            var fg = new Grid();
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ftL = new TextBlock { Text = "Esc to close  ·  This panel opens only through the hidden trigger", FontSize = Ui.FontXS, Opacity = 0.35 };
            ftL.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            var ftR = new TextBlock
            {
                Text = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                FontSize = Ui.FontXS, Opacity = 0.35, HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            ftR.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Grid.SetColumn(ftL, 0); Grid.SetColumn(ftR, 1);
            fg.Children.Add(ftL); fg.Children.Add(ftR);
            footer.Child = fg;
            Grid.SetRow(footer, 3);
            root.Children.Add(footer);

            SwitchTab(0);
            return root;
        }


        void SwitchTab(int idx)
        {
            for (int i = 0; i < TabCount; i++)
            {
                bool active = i == idx;
                _pages[i].Visibility = active ? Visibility.Visible : Visibility.Collapsed;

                if (_navBtns[i] != null)
                {
                    _navBtns[i].FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
                    _navBtns[i].Opacity    = active ? 1.0 : 0.65;
                    if (active)
                        _navBtns[i].SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                    else
                        _navBtns[i].SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                }

                if (_navLines[i] != null)
                {
                    if (active)
                        _navLines[i].SetResourceReference(Border.BackgroundProperty, "AccentBrush");
                    else
                        _navLines[i].Background = Brushes.Transparent;
                }
            }
        }

        /// <summary>
        /// The panel's buttons look like the rest of the app: no emoji / symbols before the text, the same style (outline),
        /// the same text size and padding. Walks the logical tree, so hidden pages too.
        /// </summary>
        void PolishButtons(DependencyObject root)
        {
            try
            {
                foreach (object child in LogicalTreeHelper.GetChildren(root))
                {
                    if (child is not DependencyObject d) continue;
                    if (d is Button b && Array.IndexOf(_navBtns, b) < 0 && b.Content is string text)
                    {
                        string cleaned = Net.NetWindowHelper.Clean(text);
                        if (cleaned.Length < 2) { PolishButtons(d); continue; }   // symbol-only button (close, "+"): stays as it is
                        b.Content = cleaned;
                        if (b is not Wpf.Ui.Controls.Button)
                        {
                            if (b.ReadLocalValue(StyleProperty) == DependencyProperty.UnsetValue)
                                b.SetResourceReference(StyleProperty, "OutlineButtonStyle");
                            b.Padding = new Thickness(14, 7, 14, 7);
                            b.FontSize = Ui.FontM;
                        }
                    }
                    PolishButtons(d);
                }
            }
            catch (Exception ex) { AppLogger.Debug($"DebugInfoWindow.PolishButtons: {ex.Message}"); }
        }

        // HELPERS
        // ══════════════════════════════════════════════════════════════════════
        void RefreshAll()
        {
            BuildChecklist();
            BuildSettingsContent();
            BuildLogContent();
        }

        static bool IsAdmin()
        {
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}
