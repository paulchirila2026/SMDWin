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
    public partial class DebugInfoWindow
    {
        // ══════════════════════════════════════════════════════════════════════
        // PAGE 4 — EXPERIMENTAL
        // ══════════════════════════════════════════════════════════════════════
        FrameworkElement BuildExperimentalPage()
        {
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24, 20, 24, 20) };
            var panel  = new StackPanel { Orientation = Orientation.Vertical };
            scroll.Content = panel;

            var warning = new Border
            {
                Background = new SolidColorBrush(WpfColor.FromArgb(30, 245, 158, 11)),
                BorderBrush = new SolidColorBrush(WpfColor.FromArgb(80, 245, 158, 11)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 20)
            };
            var warningTb = new TextBlock
            {
                Text = "Features under development or known to have issues. Use for testing only.",
                TextWrapping = TextWrapping.Wrap, FontSize = Ui.FontS, Opacity = 0.85
            };
            warningTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            warning.Child = warningTb;
            panel.Children.Add(warning);

            void AddExpSection(string title)
            {
                var tb = new TextBlock
                {
                    Text = title.ToUpperInvariant(), FontSize = Ui.FontXS, FontWeight = FontWeights.SemiBold,
                    Opacity = 0.5, Margin = new Thickness(0, 16, 0, 8)
                };
                tb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                panel.Children.Add(tb);
                var sep = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 12), Opacity = 0.2 };
                sep.SetResourceReference(Border.BackgroundProperty, "CardBorderBrush");
                panel.Children.Add(sep);
            }

            Button MakeExpBtn(string icon, string label, string desc, System.Action onClick)
            {
                var btn = new Button
                {
                    Margin = new Thickness(0, 0, 0, 10),
                    HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                    Cursor = System.Windows.Input.Cursors.Hand,
                };
                if (System.Windows.Application.Current.Resources["OutlineButtonStyle"] is Style s) btn.Style = s;

                var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 6, 4, 6) };
                var iconTb  = SpecSheet.IconTile(icon, 30);
                iconTb.Margin = new Thickness(0, 0, 14, 0);
                var texts   = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var labelTb = new TextBlock { Text = label, FontSize = Ui.FontM, FontWeight = FontWeights.SemiBold };
                labelTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                var descTb  = new TextBlock { Text = desc, FontSize = Ui.FontS, Opacity = 0.6, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
                descTb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                texts.Children.Add(labelTb);
                texts.Children.Add(descTb);
                content.Children.Add(iconTb);
                content.Children.Add(texts);
                btn.Content = content;
                btn.Click += (_, _) => { try { onClick(); } catch (Exception ex) { AppLogger.Warning(ex, "Experimental." + label); } };
                return btn;
            }

            // ── Power History ─────────────────────────────────────────────────
            AddExpSection("Boot & Power Timeline");
            panel.Children.Add(MakeExpBtn("Clock", "Power History",
                "Visual timeline of boot/shutdown/crash/sleep events — may display incorrectly on machines with a dead RTC battery or Modern Standby (S0).",
                () => (this.Owner as SMDWin.Views.MainWindow)?.OpenPowerHistoryFromDev()));

            AddExpSection("Network Tools");
            panel.Children.Add(MakeExpBtn("Ethernet", "LAN Speed Test",
                "PC-to-PC transfer speed test over the local network — rarely needed, kept here for the cases where it is.",
                () => SMDWin.Views.Net.NetToolPage.OpenTool("LanSpeed")));

            // ── Hardware Tests ────────────────────────────────────────────────
            AddExpSection("Hardware Tests");
            panel.Children.Add(MakeExpBtn("Speaker", "Sound Test",
                "Test audio channels — left, right, center, subwoofer.",
                () => (this.Owner as SMDWin.Views.MainWindow)?.OpenSoundTestFromDev()));

            panel.Children.Add(MakeExpBtn("Battery", "Battery Test",
                "Live battery discharge/charge test with power and temperature monitoring.",
                () => (this.Owner as SMDWin.Views.MainWindow)?.OpenBatteryTestFromDev()));

            AddExpSection("Stress Tests (Advanced)");
            panel.Children.Add(MakeExpBtn("Bolt", "Max Load Test",
                "Starts CPU Stress + GPU Stress simultaneously for maximum power draw — same test that used to sit on Temp & Stress.",
                () => (this.Owner as SMDWin.Views.MainWindow)?.TriggerMaxLoadFromDev()));


            AddExpSection("Reports");
            panel.Children.Add(MakeExpBtn("Battery", "Full Battery Report",
                "Runs powercfg /batteryreport — detailed charge history, capacity, estimated life.",
                () => (this.Owner as SMDWin.Views.MainWindow)?.TriggerBatteryReportFromDev()));

            AddReportsFolderSettings(panel);

            AddExpSection("LHM Diagnostics");
            AddDiagnosticTools(panel);

            return scroll;
        }

        FrameworkElement BuildDesignPage()
        {
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24, 20, 24, 20) };
            var panel  = new StackPanel { Orientation = Orientation.Vertical };
            scroll.Content = panel;

            // the choices still open (the one kept is the default); the old variants that were decided are gone
            AddAboutAnimationChoice(panel);
            AddTaskBadgeChoice(panel);

            var battLabel = new TextBlock
            {
                Text = "Battery glyph style (Power page)",
                FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 8),
            };
            battLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            panel.Children.Add(battLabel);
            var battWrap = new WrapPanel { Orientation = Orientation.Horizontal };
            var battCards = new System.Collections.Generic.List<Border>();
            // Preview with fixed data (78 %, green, charging) — so the fill and the pulsing block / the bolt show in every variant,
            // whatever the real battery state. The block variants first (the first is the default), then the old ones.
            var previewColor = System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E);
            var battStyles = SMDWin.Services.BatteryBlocks.Styles
                .Select(s => (s.Key, DisplayName: s.Key == SMDWin.Services.BatteryBlocks.Default ? s.Label + " (default)" : s.Label))
                .Concat(SMDWin.Services.BatteryGlyphLibrary.All.Select(s => (s.Key, s.DisplayName)));
            foreach (var (key, displayName) in battStyles)
            {
                bool isSelected = (SettingsService.Current.BatteryGlyphStyle ?? SMDWin.Services.BatteryBlocks.Default) == key;
                var card = new Border
                {
                    Width = 150, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(8),
                    CornerRadius = new CornerRadius(8), Cursor = System.Windows.Input.Cursors.Hand,
                    BorderThickness = new Thickness(isSelected ? 2 : 1),
                };
                card.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
                card.BorderBrush = isSelected
                    ? (WpfBrush)System.Windows.Application.Current.Resources["AccentBrush"]
                    : (WpfBrush)new SolidColorBrush(System.Windows.Media.Color.FromArgb(35, 200, 200, 220));

                var stack = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
                if (SMDWin.Services.BatteryBlocks.IsBlockStyle(key))
                {
                    // common height (that of the vertical variant), so the names sit on the same line in all cards
                    var holder = new System.Windows.Controls.Grid { Height = 108, Margin = new Thickness(0, 0, 0, 6) };
                    holder.Children.Add(new SMDWin.Views.BatteryPicture
                    {
                        Variant = key, Percent = 78, Charging = true,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    });
                    stack.Children.Add(holder);
                }
                else
                {
                    var previewHost = new System.Windows.Controls.Canvas { Width = 130, Height = 60,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0,24,0,30) };
                    try { previewHost.Children.Add(SMDWin.Services.BatteryGlyphLibrary.Build(key, 78, previewColor, true)); }
                    catch (Exception ex) { AppLogger.Debug($"Battery glyph preview {key}: {ex.Message}"); }
                    stack.Children.Add(previewHost);
                }
                var nameTb = new TextBlock { Text = displayName, FontSize = Ui.FontXS, TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center };
                nameTb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                stack.Children.Add(nameTb);
                card.Child = stack;

                card.MouseLeftButtonUp += (_, _) =>
                {
                    SettingsService.Current.BatteryGlyphStyle = key;
                    SettingsService.Save();
                    (this.Owner as SMDWin.Views.MainWindow)?.ApplyBatteryGlyphStyle();   // the card on the Power page redraws immediately
                    foreach (var c in battCards)
                    {
                        bool sel = c == card;
                        c.BorderThickness = new Thickness(sel ? 2 : 1);
                        c.BorderBrush = sel
                            ? (WpfBrush)System.Windows.Application.Current.Resources["AccentBrush"]
                            : (WpfBrush)new SolidColorBrush(System.Windows.Media.Color.FromArgb(35, 200, 200, 220));
                    }
                };
                battCards.Add(card);
                battWrap.Children.Add(card);
            }
            panel.Children.Add(battWrap);

            // ── The processor drawing in System Information ──
            var cpuLabel = new TextBlock
            {
                Text = "Processor picture (System Information)",
                FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 2),
            };
            cpuLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            panel.Children.Add(cpuLabel);
            var cpuHint = new TextBlock
            {
                Text = "Auto picks the picture that matches this PC's processor. Choosing a style forces it; reopen System Information to see it.",
                FontSize = Ui.FontXS, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            };
            cpuHint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            panel.Children.Add(cpuHint);

            var cpuWrap = new WrapPanel { Orientation = Orientation.Horizontal };
            var cpuCards = new System.Collections.Generic.List<(Border Card, string Key)>();
            void MarkCpuCards()
            {
                string current = SettingsService.Current.CpuPictureStyle ?? SMDWin.Services.CpuPackage.Auto;
                foreach (var (c, k) in cpuCards)
                {
                    bool sel = k == current || (k == SMDWin.Services.CpuPackage.Auto && cpuCards.All(x => x.Key != current));
                    c.BorderThickness = new Thickness(sel ? 2 : 1);
                    c.BorderBrush = sel
                        ? (WpfBrush)System.Windows.Application.Current.Resources["AccentBrush"]
                        : (WpfBrush)new SolidColorBrush(System.Windows.Media.Color.FromArgb(35, 200, 200, 220));
                }
            }
            // sample names, so a plausible inscription appears on the lid for each style
            var cpuSamples = new System.Collections.Generic.Dictionary<SMDWin.Services.CpuPackageStyle, string>
            {
                [SMDWin.Services.CpuPackageStyle.LaptopIntel]         = "Intel Core i5-8250U",
                [SMDWin.Services.CpuPackageStyle.LaptopIntelGraphics] = "Intel Core i7-8750H",
                [SMDWin.Services.CpuPackageStyle.LaptopAmd]           = "AMD Ryzen 7 5800H",
                [SMDWin.Services.CpuPackageStyle.DesktopIntel]        = "Intel Core i5-4570",
                [SMDWin.Services.CpuPackageStyle.DesktopAmd]          = "AMD Ryzen 5 5600X",
                [SMDWin.Services.CpuPackageStyle.Server]              = "Intel Xeon E5-2680 v4",
                [SMDWin.Services.CpuPackageStyle.Entry]               = "Intel Celeron N4020",
                [SMDWin.Services.CpuPackageStyle.ArmSoc]              = "Apple M2",
            };
            var cpuOptions = new System.Collections.Generic.List<(string Key, string Label, SMDWin.Services.CpuPackageStyle? Style)>
            {
                (SMDWin.Services.CpuPackage.Auto, "Auto (default)", null),
            };
            foreach (var (style, label) in SMDWin.Services.CpuPackage.Styles) cpuOptions.Add((style.ToString(), label, style));
            foreach (var (key, label, style) in cpuOptions)
            {
                var card = new Border
                {
                    Width = 160, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(8),
                    CornerRadius = new CornerRadius(8), Cursor = System.Windows.Input.Cursors.Hand,
                };
                card.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
                var stack = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
                var holder = new System.Windows.Controls.Grid { Height = 112, Margin = new Thickness(0, 0, 0, 6) };
                if (style.HasValue)
                {
                    try
                    {
                        var picture = SMDWin.Views.HardwareIllustrations.Cpu(style.Value, cpuSamples.TryGetValue(style.Value, out var sample) ? sample : "");
                        picture.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
                        picture.VerticalAlignment = System.Windows.VerticalAlignment.Center;
                        holder.Children.Add(picture);
                    }
                    catch (Exception ex) { AppLogger.Debug($"CPU picture preview {key}: {ex.Message}"); }
                }
                else
                {
                    var auto = new TextBlock { Text = "AUTO", FontSize = Ui.FontXL, FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center };
                    auto.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    holder.Children.Add(auto);
                }
                stack.Children.Add(holder);
                var nameTb = new TextBlock { Text = label, FontSize = Ui.FontXS, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
                nameTb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                stack.Children.Add(nameTb);
                card.Child = stack;
                string chosen = key;
                card.MouseLeftButtonUp += (_, _) =>
                {
                    SettingsService.Current.CpuPictureStyle = chosen;
                    SettingsService.Save();
                    MarkCpuCards();
                };
                cpuCards.Add((card, key));
                cpuWrap.Children.Add(card);
            }
            MarkCpuCards();
            panel.Children.Add(cpuWrap);

            // the gallery: many pictures (and the Benchmark drawings) — built only when asked, so the page opens and scrolls fast
            var galleryHost = new StackPanel();
            var showGallery = new Wpf.Ui.Controls.Button { Content = "Show the illustration gallery", Margin = new Thickness(0, 20, 0, 8),
                Padding = new Thickness(14, 6, 14, 7), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            showGallery.Click += (_, _) =>
            {
                showGallery.Visibility = Visibility.Collapsed;
                try { AddIllustrationGallery(galleryHost); }
                catch (Exception ex) { AppLogger.Warning(ex, "Illustration gallery"); }
            };
            panel.Children.Add(showGallery);
            panel.Children.Add(galleryHost);

            return scroll;
        }

        void AddAboutAnimationChoice(StackPanel panel)
        {
            // About window: the logo animation (5 proposals) — the one kept becomes the final one
            var aboutAnimLabel = new TextBlock
            {
                Text = "About window — logo animation", FontSize = Ui.FontS,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4),
            };
            aboutAnimLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            panel.Children.Add(aboutAnimLabel);
            var anims = SMDWin.Views.AboutWindow.LogoAnimations;
            var aboutAnimRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            var aboutAnimCombo = new ComboBox { Width = 420, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            foreach (var (_, name, _) in anims) aboutAnimCombo.Items.Add(name);
            aboutAnimCombo.SelectedIndex = Math.Max(0, Array.FindIndex(anims, x => x.Key == SettingsService.Current.DesignExperimentAboutAnim));
            var aboutAnimInfo = new TextBlock { FontSize = Ui.FontXS, TextWrapping = TextWrapping.Wrap, MaxWidth = 560,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16) };
            aboutAnimInfo.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            aboutAnimInfo.Text = anims[aboutAnimCombo.SelectedIndex].Info;
            aboutAnimCombo.SelectionChanged += (_, _) =>
            {
                if (aboutAnimCombo.SelectedIndex < 0) return;
                SettingsService.Current.DesignExperimentAboutAnim = anims[aboutAnimCombo.SelectedIndex].Key;
                SettingsService.Save();
                aboutAnimInfo.Text = anims[aboutAnimCombo.SelectedIndex].Info;
            };
            var aboutPreview = new Wpf.Ui.Controls.Button { Content = "Open About", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 4, 12, 5) };
            aboutPreview.Click += (_, _) =>
            {
                try { new SMDWin.Views.AboutWindow { Owner = this }.Show(); }
                catch (Exception ex) { AppLogger.Debug($"About preview: {ex.Message}"); }
            };
            aboutAnimRow.Children.Add(aboutAnimCombo);
            aboutAnimRow.Children.Add(aboutPreview);
            panel.Children.Add(aboutAnimRow);
            panel.Children.Add(aboutAnimInfo);
        }

        void AddTaskBadgeChoice(StackPanel panel)
        {
            var badgeLabel = new TextBlock
            {
                Text = "Sidebar task indicator (Storage / Temp & Stress / RAM Memory)", FontSize = Ui.FontS,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 4),
            };
            badgeLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            panel.Children.Add(badgeLabel);

            var badgeCombo = new ComboBox { Width = 200, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) };
            var badgeOptions = SMDWin.Services.SquareBadgeAnim.Styles
                .Concat(new[] { ("Dot", "Dot (static)"), ("Pulse", "Pulse"), ("SpinRing", "Spinning ring"), ("Hourglass", "Hourglass"), ("Bolt", "Bolt") })
                .ToArray();
            foreach (var (value, label) in badgeOptions) badgeCombo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
            badgeCombo.SelectedIndex = Array.FindIndex(badgeOptions, o => o.Item1 == SettingsService.Current.TaskBadgeStyle);
            if (badgeCombo.SelectedIndex < 0) badgeCombo.SelectedIndex = 0; // the default style (SquareBadgeAnim.Default)

            // animated preview of the square styles: visible without starting a test; click = choose the style
            var squareWrap = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            var squareCards = new System.Collections.Generic.List<(Border card, string key, SMDWin.Views.TaskBadgeSquares glyph)>();
            void MarkSquareCards()
            {
                foreach (var (card, key, _) in squareCards)
                {
                    bool sel = SettingsService.Current.TaskBadgeStyle == key;
                    card.BorderThickness = new Thickness(sel ? 2 : 1);
                    card.SetResourceReference(Border.BorderBrushProperty, sel ? "AccentBrush" : "CardBorderBrush");
                }
            }
            foreach (var (key, label) in SMDWin.Services.SquareBadgeAnim.Styles)
            {
                var card = new Border
                {
                    Width = 118, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(8, 12, 8, 8),
                    CornerRadius = new CornerRadius(8), Cursor = System.Windows.Input.Cursors.Hand,
                };
                card.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
                var stack = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
                // the real sidebar size (12 px) next to an enlarged one, so both the detail and the real effect show
                var glyphRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
                var big = new SMDWin.Views.TaskBadgeSquares { Variant = key, Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Center };
                var real = new SMDWin.Views.TaskBadgeSquares { Variant = key, Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
                glyphRow.Children.Add(big); glyphRow.Children.Add(real);
                stack.Children.Add(glyphRow);
                var nameTb = new TextBlock { Text = label, FontSize = Ui.FontXS, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
                nameTb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                stack.Children.Add(nameTb);
                card.Child = stack;
                string styleKey = key;
                card.MouseLeftButtonUp += (_, _) =>
                {
                    int idx = Array.FindIndex(badgeOptions, o => o.Item1 == styleKey);
                    if (idx >= 0) badgeCombo.SelectedIndex = idx;   // saving and applying happen in SelectionChanged
                };
                squareCards.Add((card, key, big));
                squareCards.Add((card, key, real));
                squareWrap.Children.Add(card);
            }
            MarkSquareCards();
            var squareTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            squareTimer.Tick += (_, _) =>
            {
                long now = Environment.TickCount64;
                foreach (var (_, key, glyph) in squareCards)
                    glyph.Phase = SMDWin.Services.SquareBadgeAnim.PhaseAt(key, now);
            };
            squareWrap.Loaded += (_, _) => squareTimer.Start();
            squareWrap.Unloaded += (_, _) => squareTimer.Stop();

            badgeCombo.SelectionChanged += (_, _) =>
            {
                if (badgeCombo.SelectedItem is ComboBoxItem item && item.Tag is string style)
                {
                    SettingsService.Current.TaskBadgeStyle = style;
                    SettingsService.Save();
                    (this.Owner as SMDWin.Views.MainWindow)?.ApplyTaskBadgeStyle();
                    MarkSquareCards();
                }
            };
            panel.Children.Add(squareWrap);
            panel.Children.Add(badgeCombo);
            var badgeHint = new TextBlock
            {
                Text = "The square styles are previewed above (large and at the real sidebar size). In the sidebar the indicator is only visible while a test/benchmark/scan is actually running on that page.",
                FontSize = Ui.FontXS, TextWrapping = TextWrapping.Wrap, Opacity = 0.65,
                Margin = new Thickness(0, 4, 0, 16),
            };
            badgeHint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            panel.Children.Add(badgeHint);
        }
    }
}
