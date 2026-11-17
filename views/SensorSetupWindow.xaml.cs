using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SMDWin.Services;

namespace SMDWin.Views
{
    /// <summary>
    /// The welcome window that offers the sensor driver (PawnIO). Steps: ask → installing (a moving line and the current step)
    /// → done ("Done — sensors active", closes by itself) or failed (the reason, "Try again"). If Windows' permission prompt is
    /// refused, it goes back to the question with a short note. "Not now" / ✕ / Esc close it; the title-bar banner
    /// ("Sensors unavailable — click to retry") stays in the main window, so the offer can be found again.
    /// The install itself is passed in by the main window (download, install, then start the sensors), so the window holds
    /// no driver logic.
    /// </summary>
    public partial class SensorSetupWindow : Window
    {
        private readonly Func<IProgress<string>, Task<bool>> _install;
        private bool _busy;

        /// <summary>True when the driver was installed from this window.</summary>
        public bool Installed { get; private set; }

        private bool _sensorsAnswer = true;
        /// <summary>Set by the install step: false when the driver is in place but no processor temperature arrived yet.</summary>
        public void SetSensorsAnswer(bool answer) => _sensorsAnswer = answer;

        public SensorSetupWindow(Func<IProgress<string>, Task<bool>> install)
        {
            _install = install;
            InitializeComponent();
            if (SettingsService.Current.EnableAnimations) Opacity = 0;
            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape && !_busy) Close(); };
            Closing += (_, e) => { if (_busy) e.Cancel = true; };   // the install must finish (or fail) first

            string theme = SettingsService.Current.ThemeName == "Auto" ? ThemeManager.ResolveAuto() : SettingsService.Current.ThemeName;
            SourceInitialized += (_, _) =>
            {
                var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                ThemeManager.ApplyTitleBarColor(handle, theme);
                if (ThemeManager.Themes.TryGetValue(ThemeManager.Normalize(theme), out var t))
                    ThemeManager.SetCaptionColor(handle, t["BgDark"]);
            };
            ThemeManager.Apply(theme, Resources);

            try
            {
                ApplyLocalizedText();
                ApplyButtons();
                LoadLogo();
                if (ThemeManager.IsLight(ThemeManager.Normalize(theme))) SoftenGlowForLight();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "SensorSetupWindow init"); }

            Loaded += (_, _) => Opening();
            Closed += (_, _) => { try { AboutWindow.StopLogoAnimation(SetupLogo); } catch (Exception ex) { AppLogger.Debug($"SensorSetup logo stop: {ex.Message}"); } };
        }

        private static string S(string key, string en) => LanguageService.S("SensorSetup", key, en);

        private void ApplyLocalizedText()
        {
            TxtTitle.Text = S("Welcome", "Welcome");
            TxtSubtitle.Text = S("Subtitle2", "One small step and the sensors come alive");
            TxtPoint1.Text = S("Chip1", "Temperatures");
            TxtPoint2.Text = S("Chip2", "Fan speeds");
            TxtPoint3.Text = S("Chip3", "Voltages & power");
            TxtCardTitle.Text = S("CardTitle2", "PawnIO sensor driver");
            TxtCardText.Text = S("CardText2", "Digitally signed · open source · installed once, Windows asks for permission");
            TxtWhatIs.Text = S("WhatIs", "What is this?");
            TxtDetails.Text = DetailsText();
        }

        private static string DetailsText() => S("Details", "The driver is called PawnIO. It is digitally signed and lets SMD Win read the processor and motherboard sensors safely. It replaces the older WinRing0 driver, which antivirus programs flag as a known weakness (CVE-2020-14979). Without it, SMD Win still works, but temperatures come from a limited source and voltages and fans are not shown. You can install it later from Settings.");

        private void ApplyButtons()
        {
            BtnNotNow.Content = S("NotNow", "Not now");
            BtnInstall.Content = S("Install", "Install (recommended)");
            BtnClose.ToolTip = LanguageService.S("About", "Close", "Close") + " (Esc)";
        }

        /// <summary>The live logo: the About animation when animations are on, otherwise drawn full and still.</summary>
        private void LoadLogo()
        {
            if (SettingsService.Current.EnableAnimations) return;   // started in Opening()
            SetupLogo.Level = SmdLogo.SegmentCount;
            SetupLogo.PulseProgress = 1;
        }

        /// <summary>On Light the glow at the top is kept at about half, as in About.</summary>
        private void SoftenGlowForLight()
        {
            var mask = new RadialGradientBrush();
            mask.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0x20, 0, 0, 0), 0));
            mask.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0x12, 0, 0, 0), 0.35));
            mask.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0x07, 0, 0, 0), 0.65));
            mask.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0x00, 0, 0, 0), 1));
            mask.Freeze();
            HeroGlow.OpacityMask = mask;
        }

        /// <summary>A short fade and a slight growth when it opens (only with animations on).</summary>
        private void Opening()
        {
            if (!SettingsService.Current.EnableAnimations) return;
            try { AboutWindow.PlayLogoAnimation(SetupLogo, "Heartbeat"); }
            catch (Exception ex) { AppLogger.Debug($"SensorSetup logo animation: {ex.Message}"); }
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var grow = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease };
            CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        }

        // ── Steps ─────────────────────────────────────────────────────────────────────────────────────────────

        private enum Step { Ask, Installing, Done, Failed }

        private void ShowStep(Step step, string? text = null, string? sub = null)
        {
            bool ask = step == Step.Ask;
            PanelChecks.Visibility = ask ? Visibility.Visible : Visibility.Collapsed;
            PanelProgress.Visibility = ask ? Visibility.Collapsed : Visibility.Visible;
            ProgressTrack.Visibility = step == Step.Installing ? Visibility.Visible : Visibility.Collapsed;
            DoneIcon.Visibility = step == Step.Done ? Visibility.Visible : Visibility.Collapsed;
            if (text != null) TxtProgress.Text = text;
            TxtProgress.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                step == Step.Failed ? "StatusCheckBrush" : "TextPrimaryBrush");
            TxtProgressSub.Text = sub ?? "";
            TxtProgressSub.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;

            bool busy = step == Step.Installing;
            BtnInstall.Visibility = step is Step.Ask or Step.Failed ? Visibility.Visible : Visibility.Collapsed;
            BtnInstall.Content = step == Step.Failed ? S("TryAgain", "Try again") : S("Install", "Install (recommended)");
            BtnNotNow.Content = step is Step.Done or Step.Failed ? S("Close", "Close") : S("NotNow", "Not now");
            BtnNotNow.IsEnabled = !busy;
            BtnClose.IsEnabled = !busy;
            BtnWhatIs.Visibility = busy || step == Step.Done ? Visibility.Hidden : Visibility.Visible;
            if (busy) StartMovingLine(); else StopMovingLine();
        }

        /// <summary>The slim line that moves left → right while the install runs (the install reports steps, not percents).</summary>
        private void StartMovingLine()
        {
            double w = Math.Max(ProgressTrack.ActualWidth, 300);
            if (!SettingsService.Current.EnableAnimations)
            {
                ProgressFill.Width = w;
                ProgressMove.X = 0;
                return;
            }
            var move = new DoubleAnimation(-ProgressFill.Width, w, TimeSpan.FromMilliseconds(1300))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            ProgressMove.BeginAnimation(TranslateTransform.XProperty, move);
        }

        private void StopMovingLine() => ProgressMove.BeginAnimation(TranslateTransform.XProperty, null);

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            TxtDetails.Visibility = Visibility.Collapsed;
            ShowStep(Step.Installing, LanguageService.S("PawnIo", "Installing", "Installing…"));
            var progress = new Progress<string>(msg => { if (_busy && !string.IsNullOrWhiteSpace(msg)) TxtProgress.Text = msg; });
            try
            {
                bool ok = await _install(progress);
                _busy = false;
                if (ok)
                {
                    Installed = true;
                    if (_sensorsAnswer)
                    {
                        ShowStep(Step.Done, S("Done", "Done — sensors active"),
                            S("DoneSub", "Temperatures, voltages and fans are now read directly."));
                        await Task.Delay(1800);
                        if (IsLoaded) Close();
                    }
                    else
                    {
                        // the driver is in place but nothing arrives yet: said plainly, the window stays open to be read
                        ShowStep(Step.Done, S("DoneNoSensors", "Driver installed — the sensors do not answer yet"),
                            S("DoneNoSensorsSub", "Restart Windows and open SMD Win again. Until then, values that cannot be read are shown as \u201c—\u201d."));
                    }
                }
                else
                {
                    string why = PawnIOService.LastFailureReason;
                    ShowStep(Step.Failed, S("Failed", "The driver could not be installed."),
                        string.IsNullOrEmpty(why) ? S("LaterNote", "You can install it later from Settings.") : why);
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Windows' permission prompt was refused: back to the question, with a short note
                _busy = false;
                ShowStep(Step.Ask);
                TxtDetails.Text = S("Cancelled", "Installation was cancelled. You can try again anytime.");
                TxtDetails.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                _busy = false;
                AppLogger.Warning(ex, "SensorSetupWindow install");
                ShowStep(Step.Failed, S("Failed", "The driver could not be installed."), ex.Message);
            }
        }

        private void BtnNotNow_Click(object sender, RoutedEventArgs e) { if (!_busy) Close(); }

        private void BtnWhatIs_Click(object sender, RoutedEventArgs e)
        {
            TxtDetails.Text = DetailsText();
            TxtDetails.Visibility = TxtDetails.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }

        private void TitleBar_DragMove(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }
    }
}
