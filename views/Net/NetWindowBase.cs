// Shared helpers for all Net/* tool windows
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;

using Application          = System.Windows.Application;
using Brush                = System.Windows.Media.Brush;
using Brushes              = System.Windows.Media.Brushes;
using Button               = System.Windows.Controls.Button;
using Canvas               = System.Windows.Controls.Canvas;
using CheckBox             = System.Windows.Controls.CheckBox;
using ColorConverter       = System.Windows.Media.ColorConverter;
using ComboBox             = System.Windows.Controls.ComboBox;
using Cursors              = System.Windows.Input.Cursors;
using FontFamily           = System.Windows.Media.FontFamily;
using HorizontalAlignment  = System.Windows.HorizontalAlignment;
using MenuItem             = System.Windows.Controls.MenuItem;
using MessageBox           = System.Windows.MessageBox;
using Orientation          = System.Windows.Controls.Orientation;
using Panel                = System.Windows.Controls.Panel;
using Point                = System.Windows.Point;
using TextBox              = System.Windows.Controls.TextBox;
using VerticalAlignment    = System.Windows.VerticalAlignment;
using WpfColor             = System.Windows.Media.Color;

namespace SMDWin.Views.Net
{
    internal static class NetWindowHelper
    {
        internal static double CurrentRadiusCard()
            => (System.Windows.Application.Current.Resources["RadiusCard"] as CornerRadius?)?.TopLeft ?? 8;

        // no dark layer behind the dialog (on a maximized window it came out offset). The name stays so the calls do not change.
        public static void ShowModalWithOverlay(Window dialog, Window owner)
        {
            if (dialog.Owner == null) dialog.Owner = owner;
            try
            {
                dialog.ShowDialog();
            }
            finally
            {
                try { if (owner.IsVisible && owner.WindowState != WindowState.Minimized) owner.Activate(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            }
        }

        // ── The page of a tool (embedded in the Network page) ───────────────

        public static void SetupWindow(NetToolPage p, string title, double width, double height)
        {
            p.Title = title;
            System.Windows.Media.TextOptions.SetTextFormattingMode(p, System.Windows.Media.TextFormattingMode.Display);
            System.Windows.Media.TextOptions.SetTextRenderingMode(p, System.Windows.Media.TextRenderingMode.Grayscale);
            p.UseLayoutRounding   = true;
            p.SnapsToDevicePixels = true;
            p.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch;
            p.VerticalContentAlignment   = VerticalAlignment.Stretch;

            p.Loaded += (_, _) =>
            {
                p.Foreground = Res("TextPrimaryBrush");
                try { RefreshPathStrokes(p); } catch (Exception ex) { AppLogger.Debug($"NetToolPage strokes: {ex.Message}"); }
            };
            // the page stays alive while the app runs: it subscribes only once
            ThemeManager.ThemeChanged += () =>   // [app-lifetime]
            {
                try { p.Dispatcher.InvokeAsync(() => { p.Foreground = Res("TextPrimaryBrush"); RefreshPathStrokes(p); }); }
                catch (Exception ex) { AppLogger.Debug($"NetToolPage theme: {ex.Message}"); }
            };
        }

        /// <summary>The page has no close button: the tab is changed from the Network page menu.</summary>
        public static void AddCloseButton(NetToolPage p, StackPanel actionPanel) { }

        /// <summary>The page does not move: nothing to do.</summary>
        public static void MakeDraggable(UIElement element, NetToolPage owner) { }

        public static void ShowInfo(string message, string title, FrameworkElement owner)
            => ShowInfo(message, title, owner as Window ?? Window.GetWindow(owner));

        // ── Window setup ─────────────────────────────────────────────────────

        public static void SetupWindow(Window w, string title, double width, double height)
        {
            w.Title                 = title;
            w.Width                 = width;
            w.Height                = height;
            w.MinWidth              = Math.Max(480, width * 0.65);
            w.MinHeight             = Math.Max(360, height * 0.6);
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.WindowStyle           = WindowStyle.None;
            w.ResizeMode            = ResizeMode.CanResize;
            w.ShowInTaskbar         = true;
            w.AllowsTransparency    = true;
            w.Background            = Brushes.Transparent;
            System.Windows.Media.TextOptions.SetTextFormattingMode(w, System.Windows.Media.TextFormattingMode.Display);
            System.Windows.Media.TextOptions.SetTextRenderingMode(w, System.Windows.Media.TextRenderingMode.Grayscale);
            w.UseLayoutRounding     = true;
            w.SnapsToDevicePixels   = true;

            if (Application.Current.Resources.Contains("AppFont"))
                w.FontFamily = (FontFamily)Application.Current.Resources["AppFont"];

            // WindowChrome — CaptionHeight=0, resize handled by us
            System.Windows.Shell.WindowChrome.SetWindowChrome(w, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight         = 0,
                ResizeBorderThickness = new Thickness(6),
                CornerRadius          = new CornerRadius(0),
                GlassFrameThickness   = new Thickness(0),
                UseAeroCaptionButtons = false,
            });

            w.Loaded += (_, _) =>
            {
                w.Foreground = Res("TextPrimaryBrush");
                void OnThemeChanged() { try { RefreshPathStrokes(w); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); } }
                ThemeManager.ThemeChanged += OnThemeChanged;
                w.Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
                ApplyTitleBar(w);
                PolishTitles(w);
            };

            w.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape) w.Close();
            };
        }

        /// <summary>
        /// A wrapper with rounded corners — called from each window instead of setting Content directly. Replaces the background
        /// Border.
        /// </summary>
        public static Border WindowShell(UIElement content)
        {
            var outerGrid = new Grid();

            // Read the theme right now — do not wait for Loaded (avoids a black flash).
            // BgDarkBrush = the right key for the page background (injected by ThemeManager)
            var initialBg = Application.Current?.Resources["BgDarkBrush"] as Brush
                            ?? Application.Current?.Resources["BgPanelBrush"] as Brush
                            ?? new SolidColorBrush(WpfColor.FromRgb(18, 18, 24));
            var initialBorder = Application.Current?.Resources["BorderBrush2"] as Brush
                                ?? new SolidColorBrush(WpfColor.FromArgb(60, 180, 180, 200));

            var border = new Border
            {
                Background      = initialBg,
                BorderBrush     = initialBorder,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(CurrentRadiusCard()),
                ClipToBounds    = true,
                Child           = content
            };

            void OnThemeChangedShell()
            {
                if (Application.Current?.Resources["BgDarkBrush"] is Brush bg)
                    border.Background = bg;
                else if (Application.Current?.Resources["BgPanelBrush"] is Brush bg2)
                    border.Background = bg2;
                if (Application.Current?.Resources["BorderBrush2"] is Brush bb)
                    border.BorderBrush = bb;
            }
            ThemeManager.ThemeChanged += OnThemeChangedShell;
            border.Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnThemeChangedShell;

            // Also update on Loaded (extra safety)
            border.Loaded += (_, _) =>
            {
                if (Application.Current?.Resources["BgDarkBrush"] is Brush bg)
                    border.Background = bg;
                else if (Application.Current?.Resources["BgPanelBrush"] is Brush bg2)
                    border.Background = bg2;
                if (Application.Current?.Resources["BorderBrush2"] is Brush bb)
                    border.BorderBrush = bb;
            };

            outerGrid.Children.Add(border);

            // Resize grip
            var grip = new System.Windows.Controls.Primitives.ResizeGrip
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Bottom,
                Width = 16, Height = 16,
                Opacity = 0.4,
                Cursor  = Cursors.SizeNWSE,
            };
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(grip, true);
            outerGrid.Children.Add(grip);

            var innerClip = new Border
            {
                CornerRadius = new CornerRadius(CurrentRadiusCard()),
                ClipToBounds = true,
                Child        = outerGrid
            };

            // the shadow on an empty Border, behind the content (a shadow placed directly on the content blurs it)
            var shadow = new Border
            {
                CornerRadius = new CornerRadius(CurrentRadiusCard()),
                IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 20, ShadowDepth = 6, Direction = 270,
                    Color = WpfColor.FromRgb(0, 0, 0), Opacity = 0.30,
                    RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
                },
            };
            shadow.SetResourceReference(Border.BackgroundProperty, "BgDarkBrush");
            var layers = new Grid { Margin = new Thickness(10) };
            layers.Children.Add(shadow);
            layers.Children.Add(innerClip);
            return new Border { Child = layers };
        }

        // ── Common style (the network windows) ──────────────────────────
        // Removes the symbols / emoji at the start of a text ("▶ Start" → "Start").
        public static string Clean(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            int i = 0;
            while (i < text.Length && !char.IsLetterOrDigit(text[i])) i++;
            return i == 0 ? text : text.Substring(i);
        }

        // The window title = the same as the page titles (NumFont, SemiBold, 24); found by the size all windows use for the title
        // (FontXL + Bold), in the first 90 px.
        private static void PolishTitles(Window w)
        {
            foreach (var tb in Descendants<TextBlock>(w))
            {
                if (tb.FontWeight != FontWeights.Bold || System.Math.Abs(tb.FontSize - Ui.FontXL) > 0.1) continue;
                try
                {
                    var pt = tb.TranslatePoint(new Point(0, 0), w);
                    if (pt.Y > 90) continue;
                }
                catch { continue; }
                tb.Text = Clean(tb.Text);
                if (Application.Current.Resources["NumFont"] is FontFamily nf) tb.FontFamily = nf;
                tb.FontSize = 24;
                tb.FontWeight = FontWeights.SemiBold;
            }
        }

        public static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (c is T t) yield return t;
                foreach (var d in Descendants<T>(c)) yield return d;
            }
        }

        /// <summary>A card header (as on the pages): icon + SMALL LABEL + name. iconPath = 24×24 SVG data.</summary>
        public static FrameworkElement CardHead(string caption, string? name = null, string? iconPath = null, UIElement? right = null)
        {
            var dock = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 10) };
            if (right != null) { DockPanel.SetDock(right, Dock.Right); dock.Children.Add(right); }
            if (iconPath != null)
            {
                var path = new System.Windows.Shapes.Path { Width = 24, Height = 24 };
                SMDWin.Services.FluentIcons.SetIcon(path, iconPath);   // Fluent name, or older path data
                var vb = new Viewbox { Width = 19, Height = 19, Margin = new Thickness(0, 0, 11, 0), VerticalAlignment = VerticalAlignment.Center, Child = path };
                DockPanel.SetDock(vb, Dock.Left); dock.Children.Add(vb);
            }
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            // a single-line header: the title (or, without a title, the card type in "Sentence case"); the type stays in the tooltip
            string shown = string.IsNullOrEmpty(name) ? SMDWin.Services.DisplayFormat.SentenceCase(caption) : name!;
            var nm = new TextBlock { Text = shown, FontSize = Ui.FontL, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                                     ToolTip = string.IsNullOrEmpty(name) ? null : $"{SMDWin.Services.DisplayFormat.SentenceCase(caption)} · {name}" };
            nm.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            sp.Children.Add(nm);
            DockPanel.SetDock(sp, Dock.Left); dock.Children.Add(sp);
            return dock;
        }

        /// <summary>Card with a header: ContentCard(StackPanel/Grid[head, body]).</summary>
        public static Border CardWithHead(string caption, string? name, UIElement body, string? iconPath = null, UIElement? right = null)
        {
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var head = CardHead(caption, name, iconPath, right);
            g.Children.Add(head);
            Grid.SetRow(body, 1); g.Children.Add(body);
            return ContentCard(g);
        }

        /// Walks the visual tree and re-applies TextPrimaryBrush to all Path.Stroke.
        private static void RefreshPathStrokes(DependencyObject root)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is System.Windows.Shapes.Path path)
                {
                    if (Application.Current.Resources["TextPrimaryBrush"] is Brush br)
                        path.Stroke = br;
                }
                RefreshPathStrokes(child);
            }
        }

        public static void MakeDraggable(UIElement element, Window owner)
        {
            // With AllowsTransparency=true, DragMove() must be called immediately in MouseLeftButtonDown, without extra checks that
            // may fail
            element.MouseLeftButtonDown += (_, _) =>
            {
                try { owner.DragMove(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            };
        }

        public static void ApplyTitleBar(Window w)
        {
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(w);
                string res = ThemeManager.Normalize(SettingsService.Current.ThemeName);
                ThemeManager.ApplyTitleBarColor(helper.Handle, res);
            }
            catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
        }

        // ── Resource helpers ─────────────────────────────────────────────────

        public static Brush Res(string key) =>
            (Brush)Application.Current.Resources[key];


        public static Grid BuildWindowRoot(
            string title,
            string subtitle,
            out Grid headerGrid)
        {
            var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };

            headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 0), Background = Brushes.Transparent };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition());
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock
            {
                Text       = Clean(title),
                FontSize   = Ui.FontXL,
                FontWeight = FontWeights.Bold,
                Foreground = Res("TextPrimaryBrush")
            });
            if (!string.IsNullOrEmpty(subtitle))
                titleStack.Children.Add(new TextBlock
                {
                    Text        = subtitle,
                    FontSize    = Ui.FontS,
                    Foreground  = Res("TextSecondaryBrush"),
                    Margin      = new Thickness(0, 2, 0, 0),
                    Opacity     = 0.65
                });
            Grid.SetColumn(titleStack, 0);
            headerGrid.Children.Add(titleStack);

            var actionPanel = new StackPanel
            {
                Orientation       = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Tag               = "ActionPanel"
            };
            Grid.SetColumn(actionPanel, 1);
            headerGrid.Children.Add(actionPanel);

            // Separator vizual sub header
            var sep = new Border
            {
                Height          = 0,
                Margin          = new Thickness(0, 6, 0, 8),
            };

            // Stack: header + separator — Background=Transparent, for the same reason as above (otherwise the empty area between the
            // title and the separator would not be draggable).
            var headerStack = new StackPanel { Background = Brushes.Transparent };
            headerStack.Children.Add(headerGrid);
            headerStack.Children.Add(sep);

            // headerGrid becomes the whole stack for row placement — this is the Grid each window passes to
            // MakeDraggable(headerGrid, this).
            headerGrid = new Grid { Background = Brushes.Transparent };
            headerGrid.Children.Add(headerStack);

            return root;
        }

        public static StackPanel GetActionPanel(Grid headerGrid)
        {
            // headerGrid is now a wrapper Grid that contains a StackPanel.
            // StackPanel[0] = innerHeaderGrid (Grid with the title + actionPanel).
            // Search recursively for a StackPanel with Tag="ActionPanel"
            return FindActionPanel(headerGrid)
                ?? throw new InvalidOperationException("ActionPanel not found in header");
        }

        private static StackPanel? FindActionPanel(DependencyObject parent)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            // Search logical children first (before visual tree is built)
            if (parent is System.Windows.Controls.Panel panel)
            {
                foreach (UIElement child in panel.Children)
                {
                    if (child is StackPanel sp && sp.Tag?.ToString() == "ActionPanel")
                        return sp;
                    var found = FindActionPanel(child);
                    if (found != null) return found;
                }
            }
            if (parent is ContentControl cc && cc.Content is DependencyObject ccd)
            {
                var found = FindActionPanel(ccd);
                if (found != null) return found;
            }
            return null;
        }

        public static Button CloseIconButton(double size = 32, double fontSize = -1)
        {
            var btn = new Button
            {
                Content = "✕", Width = size, Height = size, Padding = new Thickness(0),
                Cursor = Cursors.Hand, ToolTip = "Close",
            };
            if (fontSize > 0) btn.FontSize = fontSize;
            if (Application.Current.Resources["CaptionCloseBtnStyle"] is Style s)
                btn.Style = s;
            else
            {
                btn.Background  = Brushes.Transparent;
                btn.BorderBrush = Brushes.Transparent;
                btn.Foreground  = Res("TextSecondaryBrush");
            }
            return btn;
        }

        public static void AddCloseButton(Window w, StackPanel actionPanel)
        {
            var btn = CloseIconButton(32);
            btn.Margin = new Thickness(8, 0, 0, 0);
            btn.Click += (_, _) => w.Close();
            actionPanel.Children.Add(btn);
        }

        /// <summary>The main action of the window (Scan / Start / Run Test / Lookup / Trace): coloured with the accent, only one
        /// per window. For reloads/secondary actions SecondaryButton is used.</summary>
        public static Button PrimaryButton(string text, string? tooltip = null)
        {
            var b = new Wpf.Ui.Controls.Button
            {
                Content = Clean(text), Padding = new Thickness(14, 7, 14, 7),
                Cursor = Cursors.Hand, Margin = new Thickness(4, 0, 0, 0),
                Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            };
            NetButtonLook.Apply(b);
            if (tooltip != null) b.ToolTip = tooltip;
            return b;
        }

        public static Button SecondaryButton(string text, string? tooltip = null)
        {
            var b = new Wpf.Ui.Controls.Button
            {
                Content = Clean(text), Padding = new Thickness(14, 7, 14, 7),
                Cursor = Cursors.Hand, Margin = new Thickness(4, 0, 0, 0),
                Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            };
            NetButtonLook.Apply(b);
            if (tooltip != null) b.ToolTip = tooltip;
            return b;
        }

        public static void ShowInfo(string message, string title = "SMD Win", Window? owner = null)
        {
            var dlg = new Window
            {
                Title = title,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = owner != null
                    ? WindowStartupLocation.CenterOwner
                    : WindowStartupLocation.CenterScreen,
                Owner = owner ?? Application.Current.Windows
                    .OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow,
            };

            var content = new StackPanel { Margin = new Thickness(4) };
            content.Children.Add(new TextBlock
            {
                Text = title, FontSize = Ui.FontXL, FontWeight = FontWeights.Bold,
                Foreground = Res("TextPrimaryBrush"), Margin = new Thickness(0, 0, 0, 10),
            });
            content.Children.Add(new TextBlock
            {
                Text = message, FontSize = Ui.FontM, TextWrapping = TextWrapping.Wrap,
                MaxWidth = 360, Foreground = Res("TextSecondaryBrush"), Margin = new Thickness(0, 0, 0, 16),
            });
            var btnOk = SecondaryButton("OK");
            btnOk.HorizontalAlignment = HorizontalAlignment.Right;
            btnOk.Click += (_, _) => dlg.Close();
            content.Children.Add(btnOk);

            var card = ContentCard(content);
            var cardShadow = new Border
            {
                CornerRadius = new CornerRadius(10), IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 24, ShadowDepth = 4, Direction = 270,
                    Color = Colors.Black, Opacity = 0.35, RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality,
                },
            };
            cardShadow.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
            var cardLayers = new Grid { Margin = new Thickness(12) };
            cardLayers.Children.Add(cardShadow);
            cardLayers.Children.Add(card);
            dlg.Content = cardLayers;

            dlg.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.Escape)
                    dlg.Close();
            };
            btnOk.Loaded += (_, _) => btnOk.Focus();

            dlg.ShowDialog();
        }

        // ── Common UI pieces ─────────────────────────────────────────────────

        public static Border ContentCard(UIElement child, double cornerRadius = -1)
        {
            double radius = cornerRadius >= 0 ? cornerRadius : 10;
            var b = new Border
            {
                CornerRadius    = new CornerRadius(radius),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(16, 12, 16, 12),
                Child           = child
            };
            b.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
            b.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            return b;
        }


        public static TextBlock StatusText(string initial = "")
        {
            return new TextBlock
            {
                Text = initial, FontSize = Ui.FontS, Foreground = Res("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,8)
            };
        }

        /// <summary>
        /// Placeholder shown when the DataGrid is empty.
        /// Call ShowPlaceholder(grid, true/false) when the ItemsSource changes.
        /// </summary>
        /// <summary>
        /// Shows <paramref name="placeholder"/> over the list while it has no rows (before the first run, or a run that found
        /// nothing), so the card does not stay a large empty box.
        /// </summary>
        public static void ShowWhenEmpty(DataGrid grid, Border placeholder)
        {
            void Sync() => placeholder.Visibility = grid.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ((System.Collections.Specialized.INotifyCollectionChanged)grid.Items).CollectionChanged += (_, _) => Sync();
            System.ComponentModel.DependencyPropertyDescriptor
                .FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid))?.AddValueChanged(grid, (_, _) => Sync());
            placeholder.IsHitTestVisible = false;
            Sync();
        }

        /// <summary>A short grey line that says what will appear in an empty card (removed when the card is filled).</summary>
        public static TextBlock Hint(string text)
        {
            var tb = new TextBlock { Text = text, FontSize = Ui.FontS, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Opacity = 0.85 };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            return tb;
        }

        public static Border MakePlaceholder(string message = "No data", string icon = "○")
        {
            var sp = new StackPanel
            {
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 32, 0, 32)
            };
            var msg = new TextBlock
            {
                Text                = message,
                FontSize            = Ui.FontL,
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity             = 0.8,
                // a long hint (Traceroute, Ports) wraps on a narrow window instead of being cut by a few pixels
                TextWrapping        = TextWrapping.Wrap,
                TextAlignment       = TextAlignment.Center,
                MaxWidth            = 640,
            };
            msg.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            sp.Children.Add(msg);
            return new Border
            {
                Visibility          = Visibility.Collapsed,
                VerticalAlignment   = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child               = sp
            };
        }


        public static Button DangerButton(string text, string? tooltip = null)
        {
            var b = new Wpf.Ui.Controls.Button
            {
                Content = Clean(text), Padding = new Thickness(14, 7, 14, 7),
                Cursor = Cursors.Hand, Margin = new Thickness(4, 0, 0, 0),
                Appearance = Wpf.Ui.Controls.ControlAppearance.Danger,
            };
            NetButtonLook.Apply(b);
            if (tooltip != null) b.ToolTip = tooltip;
            return b;
        }

        public static Button MakeStartStopToggle(Button start, Button stop)
        {
            var toggle = new Wpf.Ui.Controls.Button
            {
                Padding = new Thickness(14, 7, 14, 7),
                Cursor  = Cursors.Hand,
                Margin  = new Thickness(4, 0, 0, 0),
            };
            void Sync()
            {
                bool running = stop.IsEnabled; // true = running now (Stop active)
                var src = running ? stop : start;
                // the two source buttons are never shown, so the page translation never reaches them: the text is translated here
                toggle.Content = src.Content is string sc ? SMDWin.Views.MainWindow.PT(sc) : src.Content;
                toggle.ToolTip = src.ToolTip is string st ? SMDWin.Views.MainWindow.PT(st) : src.ToolTip;
                toggle.Style   = src.Style;
                if (src is Wpf.Ui.Controls.Button srcUi)
                    toggle.Appearance = srcUi.Appearance;
            }
            start.IsEnabledChanged += (_, _) => Sync();
            stop.IsEnabledChanged  += (_, _) => Sync();
            toggle.Click += (_, _) =>
            {
                if (stop.IsEnabled) stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else                start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            NetButtonLook.Apply(toggle);
            Sync();
            return toggle;
        }

        /// <summary>
        /// The standard header of a tool: the title and description on the left, and on the right, in the same order on all
        /// pages, the input fields, the secondary actions and, last, the main action (Start / Stop).
        /// </summary>
        public static Grid ToolHeader(string title, string description, out StackPanel actions)
        {
            var hdr = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160 });
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock { Text = Clean(title), FontSize = Ui.FontXL, FontWeight = FontWeights.Bold,
                Foreground = Res("TextPrimaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(description))
                titleStack.Children.Add(new TextBlock { Text = description, FontSize = Ui.FontS, Foreground = Res("TextSecondaryBrush"),
                    Margin = new Thickness(0, 2, 0, 0), Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(titleStack, 0); hdr.Children.Add(titleStack);
            actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right, Tag = "ActionPanel" };
            Grid.SetColumn(actions, 1); hdr.Children.Add(actions);
            return hdr;
        }

        /// <summary>The small label before a field in the header.</summary>
        public static TextBlock HeaderLabel(string text) => new()
        {
            Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = Ui.FontS,
            Foreground = Res("TextSecondaryBrush"), Margin = new Thickness(0, 0, 6, 0),
        };

        /// <summary>The text field in the header (the app style, the same height on all pages).</summary>
        public static TextBox HeaderBox(string text, double width, string? tooltip = null) => new()
        {
            Style = (Style)Application.Current.Resources["VanillaTextBoxStyle"], Text = text, Width = width,
            Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tooltip,
        };

        /// <summary>The default style of the rows in the app's tables (hover, selection), as a base for custom styles.</summary>
        public static Style DefaultRowStyle()
        {
            var style = new Style(typeof(DataGridRow));
            if (Application.Current.Resources["VanillaDataGridStyle"] is Style grid
                && grid.Setters.OfType<Setter>().FirstOrDefault(x => x.Property == DataGrid.RowStyleProperty)?.Value is Style row)
                style.BasedOn = row;
            return style;
        }

        /// <summary>Right click selects the row under the cursor (otherwise the menu would apply to the old row).</summary>
        public static void EnableRightClickSelect(DataGrid grid)
        {
            grid.PreviewMouseRightButtonDown += (_, e) =>
            {
                DependencyObject? d = e.OriginalSource as DependencyObject;
                while (d != null && d is not DataGridRow) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
                if (d is DataGridRow row) { row.IsSelected = true; row.Focus(); }
            };
        }

        public static DataGrid StandardDataGrid()
        {
            return new DataGrid
            { Style = (Style)System.Windows.Application.Current.Resources["VanillaDataGridStyle"],
                AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
                HeadersVisibility   = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.None,
                RowHeaderWidth      = 0, BorderThickness = new Thickness(0),
                FontSize = Ui.FontS, RowHeight = 32, ColumnHeaderHeight = 38,
                Background = Brushes.Transparent,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
        }
    }

    /// <summary>Converts a hex color string to SolidColorBrush for XAML bindings.</summary>
    internal class NetStringToBrushConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            try
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value?.ToString() ?? "#888888");
                if (parameter as string == "tint") color = System.Windows.Media.Color.FromArgb(46, color.R, color.G, color.B);
                else color = ReadableColor.ForText(color);   // a text colour: darker on Light so it reads on white
                return new System.Windows.Media.SolidColorBrush(color);
            }
            catch { return System.Windows.Media.Brushes.Gray; }
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }

    internal class NetBoolToOpacityConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => (value is bool b && b) ? 1.0 : 0.45;
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotImplementedException();
    }
}
