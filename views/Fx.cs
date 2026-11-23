using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SMDWin.Services;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;

namespace SMDWin.Views
{
    /// <summary>
    /// Small finishing touches: a camera flash and a thumbnail for a screenshot, a tick that draws itself when a test ends,
    /// a "Copied ✓" moment on copy buttons, and three quiet sounds (shutter, done, warning). All of it is short (under ~0.3 s
    /// of motion), costs nothing while idle, and obeys Settings › Appearance: "Animations" (also off when Windows has its own
    /// animation effects turned off) and "Sounds".
    /// </summary>
    internal static class Fx
    {
        /// <summary>Motion allowed: the app setting and Windows' "Animation effects".</summary>
        public static bool Motion => SettingsService.Current?.EnableAnimations != false && SystemParameters.ClientAreaAnimation;

        // ── sounds: made here, so they sound the same on every computer (no Windows sound scheme involved) ──
        public enum Sound { Shutter, Done, Warning }
        private static byte[]? _shutter, _done, _warning;

        public static void Play(Sound s)
        {
            if (SettingsService.Current?.PlaySounds == false) return;
            try
            {
                byte[] wav = s switch
                {
                    Sound.Shutter => _shutter ??= MakeShutter(),
                    Sound.Done    => _done ??= MakeTones((880, 0.09), (1318.5, 0.18)),
                    _             => _warning ??= MakeTones((660, 0.11), (0, 0.06), (523, 0.16)),
                };
                var player = new System.Media.SoundPlayer(new MemoryStream(wav));
                player.Play();   // asynchronous: returns at once
            }
            catch (Exception ex) { AppLogger.Debug($"Fx sound {s}: {ex.Message}"); }
        }

        private const int Rate = 22050;
        private const double Volume = 0.16;   // quiet: a hint, not an alarm

        /// <summary>Soft sine notes one after another (frequency 0 = a pause), each with a gentle attack and a fading tail.</summary>
        private static byte[] MakeTones(params (double Hz, double Sec)[] notes)
        {
            int total = 0; foreach (var n in notes) total += (int)(n.Sec * Rate);
            var pcm = new short[total];
            int at = 0;
            foreach (var (hz, sec) in notes)
            {
                int len = (int)(sec * Rate);
                for (int i = 0; i < len; i++)
                {
                    if (hz <= 0) continue;
                    double t = i / (double)Rate;
                    double env = Math.Min(1, i / (Rate * 0.006)) * Math.Exp(-3.2 * i / (double)len);
                    double v = Math.Sin(2 * Math.PI * hz * t) + 0.25 * Math.Sin(4 * Math.PI * hz * t);
                    pcm[at + i] = (short)(v / 1.25 * env * Volume * short.MaxValue);
                }
                at += len;
            }
            return Wav(pcm);
        }

        /// <summary>A camera shutter: two short clicks of filtered noise.</summary>
        private static byte[] MakeShutter()
        {
            int len = (int)(0.12 * Rate);
            var pcm = new short[len];
            var rnd = new Random(7);
            double lp = 0;
            void Click(int start, double strength, double decayMs)
            {
                for (int i = start; i < len; i++)
                {
                    double t = (i - start) / (double)Rate * 1000;
                    double env = Math.Exp(-t / decayMs);
                    if (env < 0.002) break;
                    lp += ((rnd.NextDouble() * 2 - 1) - lp) * 0.35;   // a little low-pass: less hiss
                    pcm[i] = (short)Math.Clamp(pcm[i] + lp * env * strength * Volume * 2.2 * short.MaxValue, short.MinValue, short.MaxValue);
                }
            }
            Click(0, 1.0, 7);
            Click((int)(0.055 * Rate), 0.7, 9);
            return Wav(pcm);
        }

        private static byte[] Wav(short[] pcm)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            int data = pcm.Length * 2;
            w.Write("RIFF"u8.ToArray()); w.Write(36 + data); w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data"u8.ToArray()); w.Write(data);
            foreach (var s in pcm) w.Write(s);
            w.Flush();
            return ms.ToArray();
        }

        // ── overlays on the main window ──
        private static Grid? Host(Window? w) => (w ?? Application.Current?.MainWindow)?.FindName("RootGrid") as Grid;

        /// <summary>
        /// The card colour made solid: in the Dark theme the card surface is semi-transparent (it lets the background glow through),
        /// which made the pop-ups look washed out over the page — it is blended here over the page background.
        /// </summary>
        private static Brush OpaqueSurface()
        {
            var app = Application.Current;
            Color card = (app?.TryFindResource("CardSurfaceBrush") as SolidColorBrush)?.Color ?? Colors.White;
            Color bg = (app?.TryFindResource("BgDarkBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x11, 0x14, 0x18);
            double a = card.A / 255.0;
            byte Mix(byte c, byte b) => (byte)Math.Round(c * a + b * (1 - a));
            var solid = new SolidColorBrush(Color.FromRgb(Mix(card.R, bg.R), Mix(card.G, bg.G), Mix(card.B, bg.B)));
            solid.Freeze();
            return solid;
        }

        /// <summary>A short white flash over the window, like a camera.</summary>
        public static void Flash(Window w)
        {
            if (!Motion || Host(w) is not Grid host) return;
            var r = new Border { Background = Brushes.White, Opacity = 0, IsHitTestVisible = false };
            Grid.SetRowSpan(r, 99); Grid.SetColumnSpan(r, 99); Panel.SetZIndex(r, 9999);
            host.Children.Add(r);
            var a = new DoubleAnimationUsingKeyFrames();
            a.KeyFrames.Add(new LinearDoubleKeyFrame(0.45, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(40))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            a.Completed += (_, _) => host.Children.Remove(r);
            r.BeginAnimation(UIElement.OpacityProperty, a);
        }

        private static Border? _thumb;

        /// <summary>The screenshot as a small picture sliding into the bottom-right corner, with "Saved · Open folder".</summary>
        public static void Thumbnail(Window w, BitmapSource bmp, string savedText, string openText, Action openFolder)
        {
            if (Host(w) is not Grid host) return;
            if (_thumb != null) host.Children.Remove(_thumb);
            var img = new Image { Source = bmp, Width = 168, Stretch = Stretch.Uniform };
            var imgFrame = new Border { CornerRadius = new CornerRadius(5), ClipToBounds = true, Child = img, Margin = new Thickness(0, 0, 0, 8) };
            imgFrame.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            imgFrame.BorderThickness = new Thickness(1);
            var saved = new TextBlock { Text = savedText, FontSize = 12, FontWeight = FontWeights.SemiBold };
            saved.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var open = new TextBlock { Text = openText, FontSize = 12, Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 2, 0, 0) };
            open.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            open.MouseLeftButtonUp += (_, _) => openFolder();
            var sp = new StackPanel(); sp.Children.Add(imgFrame); sp.Children.Add(saved); sp.Children.Add(open);
            var card = new Border
            {
                Child = sp, Padding = new Thickness(10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 22, 22),
                RenderTransform = new TranslateTransform(Motion ? 40 : 0, 0), Opacity = Motion ? 0 : 1,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.25 },
            };
            card.Background = OpaqueSurface();
            card.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            Grid.SetRowSpan(card, 99); Grid.SetColumnSpan(card, 99); Panel.SetZIndex(card, 9998);
            host.Children.Add(card);
            _thumb = card;
            if (Motion)
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
                card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            }
            var hide = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            hide.Tick += (_, _) =>
            {
                hide.Stop();
                if (card.IsMouseOver) { hide.Start(); return; }   // stays while the pointer is on it
                if (!Motion) { host.Children.Remove(card); return; }
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
                fade.Completed += (_, _) => { host.Children.Remove(card); if (_thumb == card) _thumb = null; };
                card.BeginAnimation(UIElement.OpacityProperty, fade);
            };
            hide.Start();
        }

        /// <summary>
        /// The end of a test: a ring and a tick that draws itself, with a short line under it, at the top of the window for
        /// ~1.8 s; plus the "done" sound (or the warning sound when the test found problems).
        /// </summary>
        public static void Done(Window w, string text, bool problems = false)
        {
            Play(problems ? Sound.Warning : Sound.Done);
            if (Host(w) is not Grid host) return;
            string key = problems ? "StatusCheckBrush" : "StatusGoodBrush";
            var ring = new Ellipse { Width = 34, Height = 34, StrokeThickness = 2.2 };
            ring.SetResourceReference(Shape.StrokeProperty, key);
            var tick = new System.Windows.Shapes.Path { Data = Geometry.Parse(problems ? "M17,9 V19 M17,24 V25" : "M10,17.5 L15,22.5 L24.5,12"), StrokeThickness = 2.6,
                                  StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
            tick.SetResourceReference(Shape.StrokeProperty, key);
            var mark = new Grid { Width = 34, Height = 34, Margin = new Thickness(0, 0, 12, 0) };
            mark.Children.Add(ring); mark.Children.Add(tick);
            var t = new TextBlock { Text = text, FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 460, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var sp = new StackPanel { Orientation = Orientation.Horizontal }; sp.Children.Add(mark); sp.Children.Add(t);
            var card = new Border
            {
                Child = sp, Padding = new Thickness(14, 10, 18, 10), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 64, 0, 0),
                IsHitTestVisible = false, Opacity = Motion ? 0 : 1, RenderTransform = new TranslateTransform(0, Motion ? -10 : 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 3, Opacity = 0.22 },
            };
            card.Background = OpaqueSurface();
            card.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            Grid.SetRowSpan(card, 99); Grid.SetColumnSpan(card, 99); Panel.SetZIndex(card, 9997);
            host.Children.Add(card);
            if (Motion)
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
                ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-10, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
                // the ring closes, then the tick draws itself (dash offset from its length to 0)
                ring.StrokeDashArray = new DoubleCollection { 60, 60 };
                ring.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(60, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
                tick.StrokeDashArray = new DoubleCollection { 12, 12 };
                tick.StrokeDashOffset = 12;
                tick.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(260)) { BeginTime = TimeSpan.FromMilliseconds(260), EasingFunction = ease });
            }
            var hide = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
            hide.Tick += (_, _) =>
            {
                hide.Stop();
                if (!Motion) { host.Children.Remove(card); return; }
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260));
                fade.Completed += (_, _) => host.Children.Remove(card);
                card.BeginAnimation(UIElement.OpacityProperty, fade);
            };
            hide.Start();
        }

        /// <summary>"✓ Copied" on the button for a moment, then its label again.</summary>
        private static readonly System.Collections.Generic.HashSet<ContentControl> _copying = new();
        public static void Copied(ContentControl? btn)
        {
            if (btn == null || !_copying.Add(btn)) return;
            var old = btn.Content;
            btn.Content = "✓  " + MainWindow.PT("Copied");
            var back = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
            back.Tick += (_, _) => { back.Stop(); btn.Content = old; _copying.Remove(btn); };
            back.Start();
        }
    }
}
