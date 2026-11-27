using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SMDWin.Views
{
    /// <summary>
    /// The moving parts of an HDD drawing (HardwareIllustrations.Drive): the platter turns fast around the spindle while the
    /// drawing is on screen, and the head arm jumps to a new track when the disk reads or writes — more often the busier the
    /// disk is. The head never goes over the hub (the angle range is worked out from the drawing). With animations switched
    /// off in Settings / Windows nothing moves. Cost: one rotating layer (a render transform) and a timer at 20 Hz only while
    /// the picture is visible.
    /// </summary>
    public sealed class HddMotion
    {
        public RotateTransform Platter = null!;
        public RotateTransform? ArmTurn;
        public Ellipse? Blur;
        public double Cx, Cy, R, Hub;
        private double _minAngle, _maxAngle;
        private float _busy;
        private DispatcherTimer? _timer;
        private DateTime _nextSeek;
        private bool _running;
        private static readonly Random Rng = new();

        /// <summary>Called by the drawing: the arm's transform, its pivot and the head tip — gives the allowed angle range.</summary>
        internal void SetArm(RotateTransform turn, double px, double py, double tipX, double tipY)
        {
            ArmTurn = turn;
            // the head stays on the recording surface: between just outside the hub and just inside the rim
            double inner = Hub + 5, outer = R - 2.5;
            double lo = double.NaN, hi = double.NaN;
            for (double a = -35; a <= 35; a += 0.25)
            {
                double t = a * Math.PI / 180;
                double x = px + (tipX - px) * Math.Cos(t) - (tipY - py) * Math.Sin(t);
                double y = py + (tipX - px) * Math.Sin(t) + (tipY - py) * Math.Cos(t);
                double d = Math.Sqrt((x - Cx) * (x - Cx) + (y - Cy) * (y - Cy));
                if (d < inner || d > outer) continue;
                if (double.IsNaN(lo)) lo = a;
                hi = a;
            }
            _minAngle = double.IsNaN(lo) ? 0 : lo;
            _maxAngle = double.IsNaN(hi) ? 0 : hi;
        }

        /// <summary>How busy the disk is now (0–100 %); drives how often the head jumps.</summary>
        public void SetActivity(float busyPct) => _busy = float.IsFinite(busyPct) ? Math.Clamp(busyPct, 0, 100) : 0;

        /// <summary>Starts / stops with the visibility of the picture that holds the drawing.</summary>
        public void Attach(FrameworkElement host)
        {
            host.IsVisibleChanged += (_, _) => Sync(host);
            host.Loaded += (_, _) => Sync(host);
            host.Unloaded += (_, _) => Stop();
            Sync(host);
        }

        private void Sync(FrameworkElement host)
        {
            if (host.IsVisible && host.IsLoaded && Fx.Motion) Start(); else Stop();
        }

        private void Start()
        {
            if (_running) return;
            _running = true;
            // a real platter turns 90–120 times a second; here one turn in 0.3 s reads as "very fast" without strobing
            var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.3)) { RepeatBehavior = RepeatBehavior.Forever };
            Platter.BeginAnimation(RotateTransform.AngleProperty, spin);
            if (Blur != null) Blur.Opacity = 0.35;
            if (ArmTurn == null) return;
            _timer ??= new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
            _timer.Tick -= OnTick;
            _timer.Tick += OnTick;
            _timer.Start();
        }

        private void Stop()
        {
            if (!_running) return;
            _running = false;
            Platter.BeginAnimation(RotateTransform.AngleProperty, null);
            if (Blur != null) Blur.Opacity = 0;
            _timer?.Stop();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (ArmTurn == null || _maxAngle <= _minAngle) return;
            var now = DateTime.UtcNow;
            if (now < _nextSeek) return;
            if (_busy < 0.5)
            {
                // idle: the head stays where it is; look again a little later
                _nextSeek = now.AddMilliseconds(300);
                return;
            }
            // a seek: a sudden jump to another track with a tiny overshoot, as the voice coil stops the arm
            double target = _minAngle + Rng.NextDouble() * (_maxAngle - _minAngle);
            double from = ArmTurn.Angle;
            double over = target + Math.Sign(target - from) * Math.Min(0.8, Math.Abs(target - from) * 0.12);
            over = Math.Clamp(over, _minAngle, _maxAngle);
            var jump = new DoubleAnimationUsingKeyFrames();
            jump.KeyFrames.Add(new EasingDoubleKeyFrame(over, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(45)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            jump.KeyFrames.Add(new EasingDoubleKeyFrame(target, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
            jump.FillBehavior = FillBehavior.HoldEnd;
            ArmTurn.BeginAnimation(RotateTransform.AngleProperty, jump);
            // the busier the disk, the shorter the pause on a track (≈ 0.9 s at 1 %, ≈ 0.1 s at 100 %)
            double dwell = 900 - 800 * Math.Sqrt(_busy / 100.0);
            _nextSeek = now.AddMilliseconds(dwell * (0.6 + Rng.NextDouble() * 0.8));
        }
    }
}
