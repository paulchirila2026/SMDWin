using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using SMDWin.Services; // AppLogger

// The project has UseWPF + UseWindowsForms + ImplicitUsings — the WPF variants are fixed explicitly
// (UserControl / Color / Path would otherwise be ambiguous, CS0104).
using UserControl = System.Windows.Controls.UserControl;
using Color       = System.Windows.Media.Color;
using Path        = System.Windows.Shapes.Path;

namespace SMDWin.Views
{
    /// <summary>
    /// The Power page, a system without a battery: the power supply (PSU) → the power rail → the components, each with the
    /// real power setting of the active plan (see <see cref="SetInfo"/>). The dots flowing along the paths are only visual.
    /// Runs only while visible (IsVisibleChanged / Loaded / Unloaded) — when the Power page is hidden or the user unchecks the
    /// option, the storyboards stop.
    /// </summary>
    public partial class PowerFlowAnimation : UserControl
    {
        private readonly System.Collections.Generic.Dictionary<TextBlock, double> _baseFontSize = new();

        /// <summary>
        /// The diagram is drawn at 660 × 300 and enlarged by a Viewbox; <paramref name="scale"/> is its factor.
        /// The text shrinks inversely, so on screen it stays at the normal text size of the app.
        /// </summary>
        public void SetTextScale(double scale)
        {
            double k = scale > 1 ? 1 / scale : 1;
            foreach (var tb in Texts(RootGrid))
            {
                if (!_baseFontSize.TryGetValue(tb, out double b)) { b = tb.FontSize; _baseFontSize[tb] = b; }
                double fs = Math.Max(6, b * k);
                if (Math.Abs(tb.FontSize - fs) > 0.05) tb.FontSize = fs;
            }

            static System.Collections.Generic.IEnumerable<TextBlock> Texts(DependencyObject root)
            {
                int n = VisualTreeHelper.GetChildrenCount(root);
                for (int i = 0; i < n; i++)
                {
                    var c = VisualTreeHelper.GetChild(root, i);
                    if (c is TextBlock t) yield return t;
                    foreach (var d in Texts(c)) yield return d;
                }
            }
        }

        private readonly System.Collections.Generic.List<Storyboard> _running = new();
        // the storyboard of each path — the dot speed adjusts live by the measured consumption
        private readonly System.Collections.Generic.Dictionary<Path, Storyboard> _traceSb = new();
        private readonly System.Collections.Generic.Dictionary<Path, double> _traceSpeed = new();
        private bool _enabled = true;   // set by Start()/Stop() (the checkbox on the page)

        public PowerFlowAnimation()
        {
            InitializeComponent();
            IsVisibleChanged += (_, _) => SyncRunning();
        }

        private void RootGrid_Loaded(object sender, RoutedEventArgs e)
        {
            SyncRunning();
        }

        private void RootGrid_Unloaded(object sender, RoutedEventArgs e) => StopStoryboards();

        /// <summary>Fills in the component texts. What could not be read (null / empty) is NOT shown — the component keeps only
        /// its name, without "—".</summary>
        public void SetInfo(string? psu, string? cpu, string? gpu, string? display, string? ram, string? disk, string? usb)
        {
            void Set(TextBlock tb, string? v)
            {
                ShowText(tb, string.IsNullOrWhiteSpace(v) ? null : v!.Trim());
                tb.ToolTip = string.IsNullOrWhiteSpace(v) ? null : v;
            }
            Set(TxtPsuInfo, psu);
            Set(TxtCpuInfo, cpu);
            Set(TxtGpuInfo, gpu);
            Set(TxtDisplayInfo, display);
            Set(TxtRamInfo, ram);
            Set(TxtDiskInfo, disk);
            Set(TxtUsbInfo, usb);
        }

        /// <summary>
        /// Live measured values. What is missing is not shown at all (no "—" or empty boxes):
        ///   • CPU / GPU: the measured watts in the corner, and the dots on the path flow faster at high consumption;
        ///   • PSU: the measured CPU + GPU sum + the +12 / +5 / +3.3 V voltages plausibly reported by the board;
        ///   • Board: the CMOS battery (yellow below 2.8 V, red below 2.5 V);
        ///   • Fans: the first reported speeds.
        /// </summary>
        public void SetLive(float? cpuW, float? gpuW, float? rail12, float? rail5, float? rail33,
                            float? cmosV, System.Collections.Generic.IList<(string Name, float Rpm)>? fans)
        {
            static bool Ok(float? v) => v.HasValue && float.IsFinite(v.Value) && v.Value > 0;

            ShowText(TxtCpuW, Ok(cpuW) ? $"{cpuW:F0} W" : null);
            ShowText(TxtGpuW, Ok(gpuW) ? $"{gpuW:F0} W" : null);
            SetFlow(TraceCpu, Ok(cpuW) ? cpuW!.Value : (float?)null, 65f);
            SetFlow(TraceGpu, Ok(gpuW) ? gpuW!.Value : (float?)null, 120f);
            float sum = (Ok(cpuW) ? cpuW!.Value : 0f) + (Ok(gpuW) ? gpuW!.Value : 0f);
            SetFlow(TraceRail, sum > 0 ? sum : (float?)null, 160f);
            ShowText(TxtPsuTotal, sum > 0
                ? (Ok(cpuW) && Ok(gpuW) ? $"CPU + GPU: {sum:F0} W" : Ok(cpuW) ? $"CPU: {sum:F0} W" : $"GPU: {sum:F0} W")
                : null);

            var rails = new System.Collections.Generic.List<string>();
            if (Ok(rail12)) rails.Add($"+12V  {rail12:F2}");
            if (Ok(rail5))  rails.Add($"+5V  {rail5:F2}");
            if (Ok(rail33)) rails.Add($"+3.3V  {rail33:F2}");
            ShowText(TxtPsuRails, rails.Count > 0 ? string.Join("\n", rails) : null);
            // ATX tolerance ±5%: outside it, the line is coloured (a classic sign of a tired power supply)
            bool railBad = (Ok(rail12) && Math.Abs(rail12!.Value - 12f) > 0.6f)
                        || (Ok(rail5)  && Math.Abs(rail5!.Value - 5f) > 0.25f)
                        || (Ok(rail33) && Math.Abs(rail33!.Value - 3.3f) > 0.165f);
            if (railBad) TxtPsuRails.SetResourceReference(TextBlock.ForegroundProperty, "StatusWarnBrush");
            else TxtPsuRails.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            TxtPsuRails.ToolTip = railBad ? "A rail is outside the ±5% ATX tolerance — check under load; a tired PSU often shows it on +12 V." : "Within the ±5% ATX tolerance";

            if (Ok(cmosV))
            {
                BoardChip.Visibility = Visibility.Visible;
                TxtCmosInfo.Text = $"CMOS battery {cmosV:F2} V";
                TxtCmosInfo.SetResourceReference(TextBlock.ForegroundProperty,
                    cmosV < 2.5f ? "StatusCritBrush" : cmosV < 2.8f ? "StatusWarnBrush" : "TextSecondaryBrush");
            }
            else BoardChip.Visibility = Visibility.Collapsed;

            var fanList = fans?.Where(f => f.Rpm > 0).Take(2).ToList();
            if (fanList is { Count: > 0 })
            {
                FansChip.Visibility = Visibility.Visible;
                TxtFansInfo.Text = string.Join("\n", fanList.Select(f =>
                {
                    string n = f.Name;
                    int dash = n.LastIndexOf('—');
                    if (dash >= 0 && dash < n.Length - 2) n = n[(dash + 1)..].Trim();
                    return $"{n}: {f.Rpm:F0} RPM";
                }));
            }
            else FansChip.Visibility = Visibility.Collapsed;
        }

        private static void ShowText(TextBlock tb, string? text)
        {
            tb.Text = text ?? "";
            tb.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>The dot speed on a path, proportional to consumption (0.5× at idle … 3× at full load).</summary>
        private void SetFlow(Path trace, float? watts, float fullScaleW)
        {
            double ratio = watts.HasValue ? Math.Clamp(0.5 + 2.5 * watts.Value / fullScaleW, 0.5, 3.0) : 1.0;
            if (_traceSpeed.TryGetValue(trace, out double prev) && Math.Abs(prev - ratio) < 0.1) return;
            _traceSpeed[trace] = ratio;
            if (_traceSb.TryGetValue(trace, out var sb))
                try { sb.SetSpeedRatio(trace, ratio); } catch (Exception ex) { AppLogger.Debug("PowerFlowAnimation.SetFlow: " + ex.Message); }
        }


        public void Start()
        {
            _enabled = true;
            SyncRunning();
        }

        public void Stop()
        {
            _enabled = false;
            StopStoryboards();
        }

        private void SyncRunning()
        {
            bool shouldRun = _enabled && IsLoaded && IsVisible;
            if (shouldRun && _running.Count == 0) StartAnimations();
            else if (!shouldRun && _running.Count > 0) StopStoryboards();
        }

        private void StopStoryboards()
        {
            foreach (var sb in _running)
                try { sb.Stop(); } catch (Exception ex) { AppLogger.Debug("PowerFlowAnimation.Stop: " + ex.Message); }
            foreach (var kv in _traceSb)
                try { kv.Value.Stop(kv.Key); } catch (Exception ex) { AppLogger.Debug("PowerFlowAnimation.Stop trace: " + ex.Message); }
            _running.Clear();
            _traceSb.Clear();
        }

        private void StartAnimations()
        {
            StopStoryboards();

            // Rail: a continuous flow PSU → right; the branches: from the rail to the component (up for the top row, down for the
            // bottom row — the direction is given by Data).
            AnimateTrace(TraceRail,    0.0, 0.9);
            AnimateTrace(TraceCpu,     0.1, 1.0);
            AnimateTrace(TraceGpu,     0.3, 1.1);
            AnimateTrace(TraceDisplay, 0.5, 1.2);
            AnimateTrace(TraceRam,     0.2, 1.05);
            AnimateTrace(TraceDisk,    0.4, 1.15);
            AnimateTrace(TraceUsb,     0.6, 1.25);

            AnimatePulse(PsuChip, 1.4, 0.0);
        }

        /// <summary>"Flowing current": StrokeDashOffset in a loop on a dotted line.
        /// Negative offset = the dots advance in the direction the path is drawn.</summary>
        private void AnimateTrace(Path path, double offsetSeconds, double periodSeconds)
        {
            try
            {
                var anim = new DoubleAnimation
                {
                    From = 0,
                    To = -9,   // a multiple of the line period (1.5 + 3) × 2 → a loop without a jump
                    Duration = TimeSpan.FromSeconds(periodSeconds),
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromSeconds(offsetSeconds),
                };
                var sb = new Storyboard();
                Storyboard.SetTarget(anim, path);
                Storyboard.SetTargetProperty(anim, new PropertyPath(Shape.StrokeDashOffsetProperty));
                sb.Children.Add(anim);
                sb.Begin(path, true);                 // controllable → SetSpeedRatio for the live speed
                _traceSb[path] = sb;
                if (_traceSpeed.TryGetValue(path, out double r) && Math.Abs(r - 1) > 0.01)
                    sb.SetSpeedRatio(path, r);
            }
            catch (Exception ex) { AppLogger.Debug("PowerFlowAnimation.AnimateTrace: " + ex.Message); }
        }

        /// <summary>A light opacity pulse — only on the power supply (the components stay steady, so the settings text reads
        /// without "blinking").</summary>
        private void AnimatePulse(Border chip, double periodSeconds, double offsetSeconds)
        {
            try
            {
                var anim = new DoubleAnimation
                {
                    From = 0.8, To = 1.0,
                    Duration = TimeSpan.FromSeconds(periodSeconds / 2),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromSeconds(offsetSeconds),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                var sb = new Storyboard();
                Storyboard.SetTarget(anim, chip);
                Storyboard.SetTargetProperty(anim, new PropertyPath(UIElement.OpacityProperty));
                sb.Children.Add(anim);
                sb.Begin();
                _running.Add(sb);
            }
            catch (Exception ex) { AppLogger.Debug("PowerFlowAnimation.AnimatePulse: " + ex.Message); }
        }
    }
}
