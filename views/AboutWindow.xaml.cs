using System;
using System.Linq;
using System.Windows;

namespace SMDWin.Views
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
            // with animations on, the window fades in when it opens (StartLogoAnimations)
            if (SMDWin.Services.SettingsService.Current.EnableAnimations) Opacity = 0;
            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };

            // Title bar colour consistent with the current theme
            SourceInitialized += (_, _) =>
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                string theme = SMDWin.Services.SettingsService.Current.ThemeName == "Auto"
                    ? SMDWin.Services.ThemeManager.ResolveAuto()
                    : SMDWin.Services.SettingsService.Current.ThemeName;
                SMDWin.Services.ThemeManager.ApplyTitleBarColor(helper.Handle, theme);
                string resolved = SMDWin.Services.ThemeManager.Normalize(theme);
                if (SMDWin.Services.ThemeManager.Themes.TryGetValue(resolved, out var t))
                    SMDWin.Services.ThemeManager.SetCaptionColor(helper.Handle, t["BgDark"]);
            };
            SMDWin.Services.ThemeManager.Apply(
                SMDWin.Services.SettingsService.Current.ThemeName == "Auto"
                    ? SMDWin.Services.ThemeManager.ResolveAuto()
                    : SMDWin.Services.SettingsService.Current.ThemeName,
                this.Resources);

            try
            {
                // "Build 57" + the build date; the version number (0.4) is no longer shown until the public release
                var vi = SMDWin.Services.AppVersionInfo.Build;
                TxtVersion.Text = vi > 0 ? $"{vi}{(SMDWin.Services.AppVersionInfo.DevBuild ? " dev" : "")}" : "dev";
                TxtBuild.Text = BuildDateText() is { Length: > 0 } built ? built : "—";
                RunPlatform.Text = Environment.OSVersion.Version.Major >= 10
                    ? "  ·  Windows 10 / 11 · 64-bit" : "  ·  Windows · 64-bit";
                ApplyLocalizedText();
                LoadLogo();
                if (SMDWin.Services.ThemeManager.IsLight(SMDWin.Services.ThemeManager.Normalize(
                        SMDWin.Services.SettingsService.Current.ThemeName == "Auto"
                            ? SMDWin.Services.ThemeManager.ResolveAuto()
                            : SMDWin.Services.SettingsService.Current.ThemeName)))
                    SoftenGlowForLight();
            }
            catch (Exception ex) { SMDWin.Services.AppLogger.Warning(ex, "AboutWindow init"); }

            Loaded += (_, _) => StartLogoAnimations();
        }

        /// <summary>The logo is drawn by <see cref="SmdLogo"/> (vectors) — nothing to load. Shown full when animations are off.</summary>
        private void LoadLogo()
        {
            AboutLogo.Level = SmdLogo.SegmentCount;
            AboutLogo.PulseProgress = 1;
        }

        /// <summary>
        /// The opening (a short fade and a slight growth), then the logo: the segments light up from left to right, the pulse is
        /// drawn like on a monitor, and afterwards a light dot runs along the pulse every 5 s. Only with animations on in Settings.
        /// </summary>
        private void StartLogoAnimations()
        {
            if (!SMDWin.Services.SettingsService.Current.EnableAnimations) return;
            try
            {
                var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
                var grow = new System.Windows.Media.Animation.DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease };
                CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, grow);
                CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, grow);
                BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));

                PlayLogoAnimation(AboutLogo, SMDWin.Services.SettingsService.Current.DesignExperimentAboutAnim);

                if (TryFindResource("GlowBreathStoryboard") is System.Windows.Media.Animation.Storyboard glow)
                {
                    glow.Begin(this, true);
                    Closed += (_, _) => glow.Stop(this);
                }
                Closed += (_, _) => StopLogoAnimation(AboutLogo);
            }
            catch (Exception ex) { SMDWin.Services.AppLogger.Warning(ex, "AboutWindow animations"); }
        }

        // ── the logo animations (developer window › Design › About logo animation) ──────────────────────────────────

        /// <summary>The choices shown in the developer window: key, name, what it does.</summary>
        internal static readonly (string Key, string Name, string Info)[] LogoAnimations =
        {
            ("Heartbeat", "Heartbeat",  "Segments light up, the pulse is drawn, then it beats like a heart (strong beat, small beat, rest)"),
            ("Rev",       "Rev",        "Segments sweep up, drop back and settle like a revved engine; every 5 s a short rev with a beat"),
            ("Monitor",   "Monitor",    "The pulse is traced and erased continuously like a patient monitor, with a glow on each spike"),
            ("Glint",     "Glint",      "A light runs over the segments every 4 s while the pulse glows softly in and out"),
            ("Live",      "Live gauge", "Starts with the loading spinner, then the segments move like a live reading and the pulse beats at the peaks"),
        };

        private static System.Windows.Media.Animation.EasingDoubleKeyFrame KF(double ms, double v, System.Windows.Media.Animation.IEasingFunction? e = null) =>
            new(v, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), e);

        private static System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames Keys(double cycleMs, double beginMs, bool forever,
            params System.Windows.Media.Animation.EasingDoubleKeyFrame[] frames)
        {
            var a = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
            {
                BeginTime = TimeSpan.FromMilliseconds(beginMs),
                Duration = TimeSpan.FromMilliseconds(cycleMs),
                RepeatBehavior = forever ? System.Windows.Media.Animation.RepeatBehavior.Forever : new System.Windows.Media.Animation.RepeatBehavior(1),
                FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd,
            };
            foreach (var f in frames) a.KeyFrames.Add(f);
            return a;
        }

        /// <summary>The opening shared by most: segments 0 → 14, then the pulse drawn.</summary>
        private static void Intro(SmdLogo logo, double beginMs = 120)
        {
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut };
            logo.Level = 0; logo.PulseProgress = 0;
            logo.BeginAnimation(SmdLogo.LevelProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, SmdLogo.SegmentCount, TimeSpan.FromMilliseconds(800)) { BeginTime = TimeSpan.FromMilliseconds(beginMs) });
            logo.BeginAnimation(SmdLogo.PulseProgressProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(900)) { BeginTime = TimeSpan.FromMilliseconds(beginMs + 880), EasingFunction = ease });
        }

        internal static void PlayLogoAnimation(SmdLogo logo, string? key)
        {
            StopLogoAnimation(logo);
            var up = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            var io = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut };
            switch (key)
            {
                case "Rev":
                {
                    // sweep 0 → 14 fast, drop to 9, back up to 14 and settle — then every 5 s: 14 → 10 → 14 with a beat at the top
                    logo.Level = 0; logo.PulseProgress = 0;
                    logo.BeginAnimation(SmdLogo.LevelProperty, Keys(1500, 100, false,
                        KF(0, 0), KF(420, 14, up), KF(760, 9, io), KF(1150, 14, up), KF(1500, 14)));
                    logo.BeginAnimation(SmdLogo.PulseProgressProperty,
                        new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(700)) { BeginTime = TimeSpan.FromMilliseconds(1300), EasingFunction = io });
                    var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                    t.Tick += (_, _) =>
                    {
                        logo.BeginAnimation(SmdLogo.LevelProperty, Keys(900, 0, false, KF(0, 14), KF(300, 10, io), KF(650, 14, up), KF(900, 14)));
                        var beat = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                        beat.Tick += (_, _) => { beat.Stop(); logo.Beat(); };
                        beat.Start();
                    };
                    t.Start();
                    Timers[logo] = t;
                    break;
                }
                case "Monitor":
                {
                    // segments light up, then the pulse is traced (1.1 s) and erased behind (0.8 s), a short pause, again; a glow when
                    // the trace passes the spike (~45 % of the line)
                    logo.Level = 0;
                    logo.BeginAnimation(SmdLogo.LevelProperty,
                        new System.Windows.Media.Animation.DoubleAnimation(0, SmdLogo.SegmentCount, TimeSpan.FromMilliseconds(800)) { BeginTime = TimeSpan.FromMilliseconds(120) });
                    logo.PulseProgress = 0; logo.PulseStart = 0;
                    const double cyc = 2300, b = 1000;
                    logo.BeginAnimation(SmdLogo.PulseProgressProperty, Keys(cyc, b, true, KF(0, 0), KF(1100, 1), KF(cyc, 1)));
                    logo.BeginAnimation(SmdLogo.PulseStartProperty, Keys(cyc, b, true, KF(0, 0), KF(1100, 0), KF(1900, 1), KF(cyc, 1)));   // both reset to 0 together at the next cycle: nothing shows in the pause
                    logo.BeginAnimation(SmdLogo.PulseGlowProperty, Keys(cyc, b, true, KF(0, 0), KF(380, 0), KF(520, 1, up), KF(900, 0, io), KF(cyc, 0)));
                    break;
                }
                case "Glint":
                {
                    // the full logo; a light runs left → right over the segments every 4 s; the pulse "breathes" (3 s)
                    Intro(logo);
                    logo.BeginAnimation(SmdLogo.GlintProperty, Keys(4000, 2200, true, KF(0, -2), KF(1300, 16, io), KF(4000, 16)));
                    logo.BeginAnimation(SmdLogo.PulseGlowProperty, Keys(3000, 2000, true, KF(0, 0), KF(1500, 0.55, io), KF(3000, 0, io)));
                    break;
                }
                case "Live":
                {
                    // the loading spinner for 1.4 s, then the segments move like a live reading (between 9 and 14), and the
                    // pulse beats every time the reading peaks
                    logo.Level = 0; logo.PulseProgress = 0;
                    logo.SetSpinning(true);
                    var start = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
                    start.Tick += (_, _) =>
                    {
                        start.Stop();
                        logo.SetSpinning(false);
                        logo.Spin = -1;
                        logo.BeginAnimation(SmdLogo.PulseProgressProperty,
                            new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(700)) { EasingFunction = io });
                        // an 8 s "reading" that loops: rises, wobbles, peaks at 14 twice
                        logo.BeginAnimation(SmdLogo.LevelProperty, Keys(8000, 0, true,
                            KF(0, 0), KF(700, 11, up), KF(1500, 10, io), KF(2300, 12.5, io), KF(3000, 14, io), KF(3800, 11.5, io),
                            KF(4700, 10, io), KF(5500, 12, io), KF(6300, 14, io), KF(7200, 12, io), KF(8000, 11, io)));
                        var peaks = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                        var t0 = DateTime.UtcNow;
                        bool beat3 = false, beat6 = false;
                        peaks.Tick += (_, _) =>
                        {
                            double ms = (DateTime.UtcNow - t0).TotalMilliseconds % 8000;
                            if (ms >= 3000 && ms < 3200 && !beat3) { beat3 = true; logo.StartHeartbeatOnce(); }
                            if (ms >= 6300 && ms < 6500 && !beat6) { beat6 = true; logo.StartHeartbeatOnce(); }
                            if (ms < 2900) { beat3 = false; beat6 = false; }
                        };
                        peaks.Start();
                        Timers[logo] = peaks;
                    };
                    start.Start();
                    Timers[logo] = start;
                    break;
                }
                default:   // "Heartbeat"
                    Intro(logo);
                    logo.PulseDot = -1;
                    logo.StartHeartbeat(TimeSpan.FromMilliseconds(2000));
                    break;
            }
        }

        private static readonly System.Collections.Generic.Dictionary<SmdLogo, System.Windows.Threading.DispatcherTimer> Timers = new();

        internal static void StopLogoAnimation(SmdLogo logo)
        {
            if (Timers.TryGetValue(logo, out var t)) { t.Stop(); Timers.Remove(logo); }
            logo.StopHeartbeat();
            logo.SetSpinning(false);
            foreach (var dp in new[] { SmdLogo.LevelProperty, SmdLogo.PulseProgressProperty, SmdLogo.PulseStartProperty,
                                       SmdLogo.PulseGlowProperty, SmdLogo.GlintProperty, SmdLogo.PulseDotProperty })
                logo.BeginAnimation(dp, null);
            logo.Spin = -1; logo.PulseStart = 0; logo.PulseGlow = 0; logo.Glint = -10; logo.PulseDot = -1;
            logo.Level = SmdLogo.SegmentCount; logo.PulseProgress = 1;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>The build date from the assembly metadata (older builds: the exe's write time).</summary>
        private static string BuildDateText()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                var meta = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                    .Cast<System.Reflection.AssemblyMetadataAttribute>()
                    .FirstOrDefault(a => a.Key == "BuildDateUtc");
                DateTime dt;
                if (meta != null && DateTime.TryParse(meta.Value, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                    dt = parsed.ToLocalTime();
                else
                {
                    string exe = Environment.ProcessPath ?? "";
                    if (string.IsNullOrEmpty(exe)) exe = System.IO.Path.Combine(AppContext.BaseDirectory, "SMDWin.exe");
                    dt = System.IO.File.Exists(exe) ? System.IO.File.GetLastWriteTime(exe) : DateTime.Now;
                }
                return SMDWin.Services.DateFormatHelper.FormatDateTime(dt);
            }
            catch (Exception ex) { SMDWin.Services.AppLogger.Debug($"About build date: {ex.Message}"); return ""; }
        }

        /// <summary>On Light the blue-violet glow reads stronger than on Dark: about half as much at the top.</summary>
        private void SoftenGlowForLight()
        {
            var mask = new System.Windows.Media.RadialGradientBrush();
            mask.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(0x20, 0, 0, 0), 0));
            mask.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(0x12, 0, 0, 0), 0.35));
            mask.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(0x07, 0, 0, 0), 0.65));
            mask.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(0x00, 0, 0, 0), 1));
            mask.Freeze();
            HeroGlow.OpacityMask = mask;
        }

        private const string GitHubUrl = "https://github.com/paulchirila2026/SMDWin";   // the public repository

        private static void OpenUrl(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { SMDWin.Services.AppLogger.Warning(ex, "AboutWindow.OpenUrl"); }
        }

        private void BtnGitHub_Click(object sender, RoutedEventArgs e) => OpenUrl(GitHubUrl);

        /// <summary>
        /// Asks GitHub for the latest release (one request, only when this link is clicked) and says whether a newer version
        /// exists; "Yes" opens its release page, where the exe is downloaded. Nothing is downloaded or installed by itself.
        /// </summary>
        private async void BtnUpdates_Click(object sender, RoutedEventArgs e)
        {
            static string S(string key, string en) => SMDWin.Services.LanguageService.S("Update", key, en);
            string label = TxtLinkUpdates.Text;
            try
            {
                BtnUpdates.IsEnabled = false;
                TxtLinkUpdates.Text = SMDWin.Services.LanguageService.S("Common", "Checking", "Checking…");
                var info = await new SMDWin.Services.AutoUpdateService().CheckForUpdateAsync();
                if (!string.IsNullOrEmpty(info.Error))
                {
                    AppDialog.Show(string.Format(S("CheckFailedFmt", "Update check failed: {0}"), info.Error),
                        S("AvailableTitle", "Update Available"), AppDialog.Kind.Warning, this);
                }
                else if (info.Available)
                {
                    bool open = AppDialog.Confirm(
                        string.Format(S("NewVersionShortFmt", "Version {0} is available (this one is {1}).\n\nOpen the download page?"),
                            info.LatestVersion, info.CurrentVersion),
                        S("AvailableTitle", "Update Available"), this);
                    if (open) OpenUrl(string.IsNullOrEmpty(info.HtmlUrl) ? GitHubUrl + "/releases" : info.HtmlUrl);
                }
                else
                {
                    AppDialog.Show(string.Format(S("LatestFmt", "You're running the latest version ({0})."), info.CurrentVersion),
                        S("UpToDateTitle", "Up to date"), AppDialog.Kind.Info, this);
                }
            }
            catch (Exception ex) { SMDWin.Services.AppLogger.Warning(ex, "AboutWindow.CheckUpdates"); }
            finally
            {
                TxtLinkUpdates.Text = label;
                BtnUpdates.IsEnabled = true;
            }
        }

        private void BtnLicenses_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var win = new LicensesWindow { Owner = this };
                win.ShowDialog();
            }
            catch (Exception ex) { SMDWin.Services.AppLogger.Warning(ex, "AboutWindow.OpenLicenses"); }
        }

        private void ApplyLocalizedText()
        {
            static string S(string key, string en) => SMDWin.Services.LanguageService.S("About", key, en);
            TxtLabelVersion.Text = S("Build", "Build");
            TxtLabelBuild.Text = S("BuiltOn", "Built on");
            TxtLabelLicense.Text = S("License", "License");
            TxtLicenseValue.ToolTip = S("LicenseTip", "Free and open source");
            TxtLinkViewLicenses.Text = S("Licenses", "Licenses");
            TxtLinkUpdates.Text = S("CheckUpdates", "Check for updates");
            BtnGitHub.ToolTip = GitHubUrl;
            BtnUpdates.ToolTip = S("CheckUpdatesTip", "Asks GitHub whether a newer version exists — nothing is downloaded by itself");
        }

        private int _debugClickCount = 0;
        private DateTime _debugClickLast = DateTime.MinValue;

        private void TxtVersion_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            SMDWin.Services.AppLogger.Debug($"5-click debug: count={_debugClickCount + 1}");
            var now = DateTime.Now;
            if ((now - _debugClickLast).TotalMilliseconds > 2000) _debugClickCount = 0;
            _debugClickLast = now;
            _debugClickCount++;

            if (_debugClickCount >= 5)
            {
                _debugClickCount = 0;
                try
                {
                    var owner = this.Owner ?? this;
                    var win = new DebugInfoWindow { Owner = owner, ShowInTaskbar = true };
                    if (owner is Window ownerWin)
                        Ui.FixOwnedWindowMinimize(win, ownerWin);
                    win.Show();
                    Dispatcher.BeginInvoke(new Action(() => Close()),
                        System.Windows.Threading.DispatcherPriority.Background);
                }
                catch (Exception ex) { SMDWin.Services.AppLogger.Warning(ex, "AboutWindow.OpenDebugPanel"); }
            }
        }

        private void TitleBar_DragMove(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }

    }
}
