// Ui.cs — aliases for Application/FontFamily/Brush (ambiguous WinForms+WPF)
using System;
using System.Windows;
using System.Windows.Media;

// UseWindowsForms=true makes System.Drawing imported implicitly in the whole project, which conflicts with
// System.Windows.Media (WPF) for FontFamily and Brush. Explicit aliases, the same convention as in SystemInfoView.cs.
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfBrush      = System.Windows.Media.Brush;
using WpfBrushes    = System.Windows.Media.Brushes;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfColor      = System.Windows.Media.Color;
using WpfApplication = System.Windows.Application;

namespace SMDWin.Views
{
    public static class Ui
    {
        /// <summary>
        /// Puts a drop shadow behind <paramref name="content"/> on its own layer: an empty rounded Border with the same
        /// shape casts the shadow, and the content sits above it without any effect. An Effect placed on an element that
        /// contains text renders that text blurry, so window and card shadows go through this helper.
        /// </summary>
        public static System.Windows.Controls.Grid ShadowLayer(UIElement content, CornerRadius radius, Thickness margin,
            System.Windows.Media.Effects.Effect effect, string backgroundKey = "BgCardBrush")
        {
            var shadow = new System.Windows.Controls.Border { CornerRadius = radius, Effect = effect, IsHitTestVisible = false };
            shadow.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, backgroundKey);
            var layers = new System.Windows.Controls.Grid { Margin = margin };
            layers.Children.Add(shadow);
            layers.Children.Add(content);
            return layers;
        }

        /// <summary>
        /// Re-applies the app's own scroll bar (AppScrollBarStyle / AppScrollViewerStyle from Styles.xaml) and the white text on
        /// filled buttons, on an element on which ApplicationThemeManager.Apply() was called.
        /// WPF-UI adds implicit styles for ScrollViewer and ScrollBar to the element's resources, which win over those in
        /// Styles.xaml — the result was a large dark grey bar over the content. Entries placed directly in Resources take priority
        /// over the merged dictionaries (MergedDictionaries) added by WPF-UI, so it is enough to put them here, after every Apply().
        /// </summary>
        public static void UseAppScrollBars(FrameworkElement? fe)
        {
            if (fe == null) return;
            try
            {
                var app = WpfApplication.Current;
                if (app == null) return;
                if (app.TryFindResource("AppScrollBarStyle") is Style sb)
                    fe.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = sb;
                if (app.TryFindResource("AppScrollViewerStyle") is Style sv)
                    fe.Resources[typeof(System.Windows.Controls.ScrollViewer)] = sv;
                // the same for the text of filled buttons (Primary, the chosen tabs): the WPF-UI Dark theme makes it black on blue, and
                // the dictionary set by Apply() on the element wins over the white value in the app resources — the text stayed black
                // until ButtonContrastGuard corrected it (2–3 s). White on accent, on both themes.
                var white = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White);
                white.Freeze();
                foreach (string key in new[] { "AccentButtonForeground", "AccentButtonForegroundPointerOver", "AccentButtonForegroundPressed",
                                               "AccentButtonForegroundDisabled", "TextOnAccentFillColorPrimaryBrush" })
                    fe.Resources[key] = white;
                // the usual buttons: a neutral background, no outline (the same values as in the app resources, the current theme)
                foreach (var (key, src) in SMDWin.Services.ThemeManager.ButtonResourceMap)
                {
                    object? v = src == "" ? System.Windows.Media.Brushes.Transparent : app.TryFindResource(src);
                    if (v != null) fe.Resources[key] = v;
                }
            }
            catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); /* purely cosmetic — the UI is never blocked for a scroll bar */ }
        }

        // ───────────────────────────── Resource helpers ─────────────────────────────

        private static T Res<T>(string key, T fallback)
        {
            try
            {
                if (WpfApplication.Current?.Resources[key] is T value)
                    return value;
            }
            catch
            {
                // Ignored — fall back (e.g. at design time, without Application.Current)
            }
            return fallback;
        }

        // ───────────────────────────── Typography ─────────────────────────────

        public static double FontXS    => Res("FontSizeXS", 11.0);
        public static double FontS     => Res("FontSizeS", 12.0);
        public static double FontM     => Res("FontSizeM", 13.0);
        public static double FontL     => Res("FontSizeL", 14.0);
        public static double FontXL    => Res("FontSizeXL", 15.0);
        public static double FontXXL   => Res("FontSizeXXL", 17.0);
        public static double FontSectionTitle => Res("FontSizeSectionTitle", 18.0);
        public static double FontTitle        => Res("FontSizeTitle", 20.0);
        public static double FontTitleLg      => Res("FontSizeTitleLg", 22.0);
        public static double FontHero         => Res("FontSizeHero", 28.0);
        public static double FontHeroLg       => Res("FontSizeHeroLg", 40.0);

        public static WpfFontFamily AppFont        => Res("AppFont", new WpfFontFamily("Segoe UI Variable Text, Segoe UI, sans-serif"));
        public static WpfFontFamily AppFontDisplay => Res("AppFontDisplay", new WpfFontFamily("Segoe UI Variable Display, Segoe UI Semibold, Segoe UI, sans-serif"));
        public static WpfFontFamily MonoFont       => Res("MonoFont", new WpfFontFamily("Cascadia Mono, Consolas, Courier New"));

        public static FontWeight WeightTitle  => FontWeights.SemiBold;
        public static FontWeight WeightBody   => FontWeights.Normal;

        // ───────────────────────────── Status colors ─────────────────────────────
        // The same 4 shades used across the project.

        public static WpfBrush Ok    => Res<WpfBrush>("StatusOkBrush", new WpfSolidColorBrush(WpfColor.FromRgb(0x22, 0xC5, 0x5E)));
        public static WpfBrush Warn  => Res<WpfBrush>("StatusWarnBrush", new WpfSolidColorBrush(WpfColor.FromRgb(0xF5, 0x9E, 0x0B)));
        public static WpfBrush Error => Res<WpfBrush>("StatusCritBrush", new WpfSolidColorBrush(WpfColor.FromRgb(0xEF, 0x44, 0x44)));
        public static WpfBrush Info  => Res<WpfBrush>("StatusInfoBrush", new WpfSolidColorBrush(WpfColor.FromRgb(0x3B, 0x82, 0xF6)));
        public static WpfBrush Na    => Res<WpfBrush>("StatusNaBrush", new WpfSolidColorBrush(WpfColor.FromRgb(0x4B, 0x55, 0x63)));

        // ───────────────────────────── Surface / text colors ─────────────────────────────

        public static WpfBrush TextPrimary   => Res<WpfBrush>("TextPrimaryBrush", WpfBrushes.White);
        public static WpfBrush TextSecondary => Res<WpfBrush>("TextSecondaryBrush", WpfBrushes.Gray);
        public static WpfBrush Accent        => Res<WpfBrush>("AccentBrush", new WpfSolidColorBrush(WpfColor.FromRgb(0x3B, 0x82, 0xF6)));
        public static WpfBrush CardBg        => Res<WpfBrush>("BgCardBrush", WpfBrushes.Transparent);
        public static WpfBrush CardBorder    => Res<WpfBrush>("CardBorderBrush", WpfBrushes.Gray);
        public static WpfBrush InputBg       => Res<WpfBrush>("BgInputBrush", WpfBrushes.Transparent);

        // ───────────────────────────── Radius / spacing ─────────────────────────────

        public static CornerRadius RadiusControl => Res("RadiusControl", new CornerRadius(4));
        public static CornerRadius RadiusCard    => Res("RadiusCard", new CornerRadius(8));
        public static CornerRadius RadiusDialog  => Res("RadiusDialog", new CornerRadius(8));
        public static CornerRadius RadiusPill    => Res("RadiusPill", new CornerRadius(999));

        public static Thickness CardPadding   => Res("CardPadding", new Thickness(16, 14, 16, 14));
        public static Thickness CardPaddingSm => Res("CardPaddingSm", new Thickness(12, 10, 12, 10));
        public static Thickness ControlPadding => Res("ControlPadding", new Thickness(14, 8, 14, 8));
        public static Thickness ChipPadding    => Res("ChipPadding", new Thickness(10, 4, 10, 4));

        // ───────────────────────────── Charts ─────────────────────────────
        // A single standard for all the chart implementations
        // (PerfmonSparkline, RamLivePanel, MainWindow.Monitor/.Hardware, WidgetWindow, BatteryTest).

        public static double ChartStroke       => Res("ChartStrokeThickness", 1.5);
        public static double ChartFillOpacity  => Res("ChartFillOpacity", 0.18);
        public static double ChartGridOpacity  => Res("ChartGridOpacity", 0.4);

        public const double TitleBarHeight = 40;
        public const string CloseGlyph = "✕";

        public static System.Windows.Controls.Button MakeCloseButton(WpfColor fg, System.Action onClick, double size = 28)
        {
            var hoverBrush = new WpfSolidColorBrush(WpfColor.FromArgb(30, fg.R, fg.G, fg.B));
            var bg = new WpfSolidColorBrush(WpfColor.FromArgb(0, fg.R, fg.G, fg.B));
            var btn = new System.Windows.Controls.Button
            {
                Content = CloseGlyph,
                Width = size, Height = size,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = bg,
                Foreground = new WpfSolidColorBrush(fg),
                FontSize = FontL,
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            };
            // CornerRadius on a plain Button does not exist as a direct property — a minimal ControlTemplate is used (a rounded
            // Border + ContentPresenter), without an external Style.
            var tpl = new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.Button));
            var border = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.Border));
            border.Name = "Bd";
            border.SetValue(System.Windows.Controls.Border.BackgroundProperty, new System.Windows.TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
            border.SetValue(System.Windows.Controls.Border.CornerRadiusProperty, RadiusControl);
            var cp = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
            cp.SetValue(System.Windows.Controls.ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
            cp.SetValue(System.Windows.Controls.ContentPresenter.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
            border.AppendChild(cp);
            tpl.VisualTree = border;
            btn.Template = tpl;

            btn.MouseEnter += (_, _) => btn.Background = hoverBrush;
            btn.MouseLeave += (_, _) => btn.Background = bg;
            btn.Click += (_, _) => onClick();
            return btn;
        }

        public static void FixOwnedWindowMinimize(Window child, Window? owner)
        {
            if (owner == null) return;
            WindowState ownerLastState = owner.WindowState;
            child.StateChanged += (_, _) =>
            {
                if (!owner.IsLoaded) return;
                if (child.WindowState == WindowState.Minimized && owner.WindowState == WindowState.Minimized)
                {
                    // The owner was minimized as a side effect of minimizing the child — not because the user pressed minimize on the main
                    // window.
                    owner.WindowState = ownerLastState == WindowState.Minimized ? WindowState.Normal : ownerLastState;
                    owner.Activate();
                }
                else if (owner.WindowState != WindowState.Minimized)
                {
                    ownerLastState = owner.WindowState;
                }
            };
            child.Closing += (_, _) =>
            {
                if (owner.IsLoaded && owner.WindowState != WindowState.Minimized)
                    owner.Activate();
            };
        }
    }
}
