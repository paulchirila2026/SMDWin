using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SMDWin.Services;
using Brush   = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color   = System.Windows.Media.Color;
using Point   = System.Windows.Point;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace SMDWin.Views
{
    /// <summary>
    /// The small card shown while the quick self-test runs (bottom-right of the screen, always on top): what it is doing now,
    /// a progress bar with a moving shine, the percentage and the approximate time left. It is a separate window, so it never
    /// appears in the self-test screenshots (they capture only SMD Win's own windows). Code-only, theme colours.
    /// </summary>
    internal sealed class SelfTestProgressWindow : Window
    {
        private readonly TextBlock _step, _pct, _eta;
        private readonly Border _fill, _track, _shine;
        private readonly TranslateTransform _shineMove = new(-80, 0);
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private double _fraction;

        private static string S(string key, string en) => LanguageService.S("Diag", key, en);

        public SelfTestProgressWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Width = 380;
            SizeToContent = SizeToContent.Height;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            string theme = SettingsService.Current.ThemeName == "Auto" ? ThemeManager.ResolveAuto() : SettingsService.Current.ThemeName;
            ThemeManager.Apply(theme, Resources);

            var card = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(18, 14, 18, 14), Margin = new Thickness(12) };
            card.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.35, Color = Colors.Black };

            var sp = new StackPanel();
            var head = new DockPanel();
            _pct = Txt("0 %", 15, "AccentBrush", true);
            DockPanel.SetDock(_pct, Dock.Right);
            head.Children.Add(_pct);
            head.Children.Add(Txt(S("SelfTestProgressTitle", "Self-test running"), 15, "TextPrimaryBrush", true));
            sp.Children.Add(head);

            _step = Txt(S("SelfTestProgressStart", "Starting…"), 12.5, "TextSecondaryBrush");
            _step.Margin = new Thickness(0, 4, 0, 10);
            _step.TextTrimming = TextTrimming.CharacterEllipsis;
            sp.Children.Add(_step);

            // the bar: a track, the filled part (slides to the new value) and a soft shine that keeps moving over it
            _track = new Border { Height = 6, CornerRadius = new CornerRadius(3), ClipToBounds = true };
            _track.SetResourceReference(Border.BackgroundProperty, "GaugeTrackBrush");
            var barGrid = new Grid();
            _fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            _fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            barGrid.Children.Add(_fill);
            var shineBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            shineBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0));
            shineBrush.GradientStops.Add(new GradientStop(Color.FromArgb(110, 255, 255, 255), 0.5));
            shineBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));
            _shine = new Border { Width = 80, Background = shineBrush, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = _shineMove, IsHitTestVisible = false };
            var fillHost = new Grid { HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true };
            fillHost.SetBinding(WidthProperty, new System.Windows.Data.Binding(nameof(Width)) { Source = _fill });
            fillHost.Children.Add(_shine);
            barGrid.Children.Add(fillHost);
            _track.Child = barGrid;
            sp.Children.Add(_track);

            _eta = Txt("", 11.5, "TextSecondaryBrush");
            _eta.Margin = new Thickness(0, 8, 0, 0);
            sp.Children.Add(_eta);
            var note = Txt(S("SelfTestProgressNote", "Please do not use SMD Win until it finishes."), 11.5, "TextSecondaryBrush");
            note.Opacity = 0.8; note.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(note);

            card.Child = sp;
            Content = card;

            Loaded += (_, _) =>
            {
                var wa = SystemParameters.WorkArea;
                Left = wa.Right - ActualWidth - 8;
                Top = wa.Bottom - ActualHeight - 8;
                if (SettingsService.Current.EnableAnimations)
                {
                    var move = new DoubleAnimation(-80, 400, TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever };
                    _shineMove.BeginAnimation(TranslateTransform.XProperty, move);
                }
            };
            Closed += (_, _) => _shineMove.BeginAnimation(TranslateTransform.XProperty, null);
        }

        private static TextBlock Txt(string text, double size, string brush, bool bold = false)
        {
            var t = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }

        /// <summary>Moves the bar to <paramref name="fraction"/> (0–1, never backwards) and shows what runs now.</summary>
        public void Report(double fraction, string? step)
        {
            if (step != null) _step.Text = step;
            fraction = Math.Clamp(fraction, 0, 1);
            if (fraction < _fraction) fraction = _fraction;
            _fraction = fraction;
            _pct.Text = $"{fraction * 100:0} %";

            double full = Math.Max(0, _track.ActualWidth);
            double to = full * fraction;
            if (SettingsService.Current.EnableAnimations)
                _fill.BeginAnimation(WidthProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(500)) { EasingFunction = new QuadraticEase() });
            else { _fill.BeginAnimation(WidthProperty, null); _fill.Width = to; }

            // time left from the pace so far (shown after the first few percent, when the estimate means something)
            double secs = _sw.Elapsed.TotalSeconds;
            if (fraction > 0.04 && fraction < 0.999)
            {
                double left = secs / fraction * (1 - fraction);
                _eta.Text = string.Format(S("SelfTestProgressLeft", "about {0} left · {1} so far"), Clock(left), Clock(secs));
            }
            else _eta.Text = string.Format(S("SelfTestProgressElapsed", "{0} so far"), Clock(secs));
        }

        private static string Clock(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}:{t.Seconds:00} min" : $"{t.Seconds} s";
        }
    }
}
