using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SMDWin.Services;
// ALIAS: avoids CS0104 - Button ambiguous between WPF and WinForms (the csproj has UseWindowsForms=true)
using Button = System.Windows.Controls.Button;
using Slider = System.Windows.Controls.Slider;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using CheckBox = System.Windows.Controls.CheckBox;
using TextBox = System.Windows.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SMDWin.Views
{
    public partial class FanControlWindow : Window
    {
        private readonly TempReader _reader;
        private readonly Func<float> _getMaxTemp;   // the current max temp (CPU/GPU) for the thermal guard
        private List<TempReader.FanController> _fans = new();
        private readonly Dictionary<TempReader.FanController, Slider> _sliders = new();
        private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };
        private bool _guardTripped = false;
        private readonly Dictionary<TempReader.FanController, float> _userDuty = new(); // ce a cerut userul

        private readonly Dictionary<string, (DateTime requestedAt, float baselineRpm, float requestedDuty, bool warned)> _fanVerify = new();
        private readonly Dictionary<string, TextBlock> _fanWarnLabels = new();

        public FanControlWindow(TempReader reader, Func<float> getMaxTemp)
        {
            InitializeComponent();
            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
            _reader = reader;
            _getMaxTemp = getMaxTemp;

            // Apply the current theme to this window's resources
            SourceInitialized += (_, _) =>
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                string theme = SettingsService.Current.ThemeName == "Auto"
                    ? ThemeManager.ResolveAuto()
                    : SettingsService.Current.ThemeName;
                ThemeManager.ApplyTitleBarColor(helper.Handle, theme);
                string resolved = ThemeManager.Normalize(theme);
                if (ThemeManager.Themes.TryGetValue(resolved, out var t))
                    ThemeManager.SetCaptionColor(helper.Handle, t["BgDark"]);
            };
            ThemeManager.Apply(
                SettingsService.Current.ThemeName == "Auto" ? ThemeManager.ResolveAuto() : SettingsService.Current.ThemeName,
                this.Resources);

            void OnThemeChanged()
            {
                try
                {
                    var helper2 = new System.Windows.Interop.WindowInteropHelper(this);
                    string theme2 = SettingsService.Current.ThemeName == "Auto"
                        ? ThemeManager.ResolveAuto()
                        : SettingsService.Current.ThemeName;
                    ThemeManager.ApplyTitleBarColor(helper2.Handle, theme2);
                    string resolved2 = ThemeManager.Normalize(theme2);
                    if (ThemeManager.Themes.TryGetValue(resolved2, out var t2))
                        ThemeManager.SetCaptionColor(helper2.Handle, t2["BgDark"]);
                    ThemeManager.Apply(theme2, this.Resources);
                }
                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            }
            ThemeManager.ThemeChanged += OnThemeChanged;
            Closed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;

            Loaded += (_, _) => { BuildFanList(); _refresh.Start(); };
            Closed += (_, _) => _refresh.Stop();
            // when the window closes the fans return to automatic: a fan left slow does not stay that way
            Closed += (_, _) =>
            {
                try
                {
                    foreach (var fan in _fans.Where(f => f.CanControl && (_userDuty.ContainsKey(f) || _guardTripped)))
                        _reader.SetFanAuto(fan);
                    SMDWin.Services.ToolActivityLog.Add("Fan control", "fans returned to automatic when the window closed", "info");
                }
                catch (Exception ex) { AppLogger.Warning(ex, "FanControl.Closed"); }
            };
            _refresh.Tick += (_, _) => { RefreshRpm(); ApplyThermalGuard(); };
        }

        private void BuildFanList()
        {
            FanListPanel.Children.Clear();
            _sliders.Clear();
            _fanWarnLabels.Clear();
            _fanVerify.Clear();
            _fans = _reader.GetFans();

            if (_fans.Count == 0)
            {
                FanListPanel.Children.Add(new TextBlock
                {
                    Text = "No controllable fans found on this system.\n\n" +
                           "This is expected on:\n" +
                           "  •  Apple hardware running Boot Camp (fans are managed by the Apple SMC, not exposed to Windows software)\n" +
                           "  •  Many laptops with proprietary Embedded Controller (EC) fan management\n\n" +
                           "SMDWin uses LibreHardwareMonitor to talk to standard SuperIO/EC chips (common on desktop motherboards) — if your system doesn't use one of those, no software (including this one) can control the fans from Windows.",
                    FontSize = Ui.FontM, TextWrapping = TextWrapping.Wrap,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                    Margin = new Thickness(4, 12, 4, 0)
                });
                return;
            }

            foreach (var fan in _fans)
            {
                var card = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Background = (System.Windows.Media.Brush)FindResource("BgHoverBrush"),
                    Padding = new Thickness(14, 12, 14, 12),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                var g = new Grid();
                g.RowDefinitions.Add(new RowDefinition());
                g.RowDefinitions.Add(new RowDefinition());
                g.RowDefinitions.Add(new RowDefinition());

                // Row 0: name + RPM
                var top = new Grid();
                top.ColumnDefinitions.Add(new ColumnDefinition());
                top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                top.Children.Add(new TextBlock
                {
                    Text = fan.Name, FontSize = Ui.FontM, FontWeight = FontWeights.SemiBold,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                var rpmTb = new TextBlock
                {
                    Text = fan.Rpm.HasValue ? $"{fan.Rpm:F0} RPM" : "— RPM",
                    FontSize = Ui.FontM, FontWeight = FontWeights.Bold,
                    Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                rpmTb.Tag = fan;
                Grid.SetColumn(rpmTb, 1);
                top.Children.Add(rpmTb);
                Grid.SetRow(top, 0);
                g.Children.Add(top);

                // Row 1: slider OR read-only note
                if (fan.CanControl)
                {
                    var sliderGrid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
                    sliderGrid.ColumnDefinitions.Add(new ColumnDefinition());
                    sliderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var slider = new Slider
                    {
                        Minimum = 0, Maximum = 100, Value = fan.DutyPercent,
                        VerticalAlignment = VerticalAlignment.Center,
                        IsSnapToTickEnabled = true, TickFrequency = 5
                    };
                    var pctTb = new TextBlock
                    {
                        Text = $"{fan.DutyPercent:F0}%", FontSize = Ui.FontS, Width = 42,
                        TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                        Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush")
                    };
                    var capturedFan = fan;
                    var warnTb = new TextBlock
                    {
                        Text = "⚠ No RPM response detected — this fan/EC may not support software control.",
                        FontSize = Ui.FontXS, TextWrapping = TextWrapping.Wrap,
                        Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11)),
                        Margin = new Thickness(0, 4, 0, 0),
                        Visibility = Visibility.Collapsed,
                        Tag = capturedFan,
                    };
                    Grid.SetRow(warnTb, 2);
                    g.Children.Add(warnTb);
                    _fanWarnLabels[capturedFan.Name] = warnTb;

                    slider.ValueChanged += (_, e) =>
                    {
                        pctTb.Text = $"{e.NewValue:F0}%";
                        _userDuty[capturedFan] = (float)e.NewValue;
                        if (!_guardTripped) _reader.SetFanDuty(capturedFan, (float)e.NewValue);

                        if (Math.Abs(e.NewValue - capturedFan.DutyPercent) > 15)
                        {
                            warnTb.Visibility = Visibility.Collapsed; // reset an earlier warning
                            _fanVerify[capturedFan.Name] = (DateTime.Now, capturedFan.Rpm ?? -1, (float)e.NewValue, false);
                        }
                    };
                    Grid.SetColumn(pctTb, 1);
                    sliderGrid.Children.Add(slider);
                    sliderGrid.Children.Add(pctTb);
                    Grid.SetRow(sliderGrid, 1);
                    g.Children.Add(sliderGrid);
                    _sliders[fan] = slider;
                    _userDuty[fan] = fan.DutyPercent;
                }
                else
                {
                    var note = new TextBlock
                    {
                        Text = "Read-only — this fan can't be controlled by software.",
                        FontSize = Ui.FontXS, Margin = new Thickness(0, 6, 0, 0),
                        Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                        Opacity = 0.8
                    };
                    Grid.SetRow(note, 1);
                    g.Children.Add(note);
                }

                card.Child = g;
                FanListPanel.Children.Add(card);
            }
        }

        private void RefreshRpm()
        {
            try
            {
                var fresh = _reader.GetFans();
                foreach (var child in FanListPanel.Children.OfType<Border>())
                {
                    if (child.Child is not Grid g) continue;
                    var top = g.Children.OfType<Grid>().FirstOrDefault();
                    var rpmTb = top?.Children.OfType<TextBlock>().FirstOrDefault(t => t.Tag is TempReader.FanController);
                    if (rpmTb?.Tag is TempReader.FanController fan)
                    {
                        var match = fresh.FirstOrDefault(f => f.Name == fan.Name);
                        if (match != null)
                            rpmTb.Text = match.Rpm.HasValue ? $"{match.Rpm:F0} RPM" : "— RPM";

                        if (match != null && _fanVerify.TryGetValue(fan.Name, out var pending) && !pending.warned)
                        {
                            double age = (DateTime.Now - pending.requestedAt).TotalSeconds;
                            if (age >= 5.0)
                            {
                                float curRpm = match.Rpm ?? -1;
                                bool noResponse = pending.baselineRpm >= 0 && curRpm >= 0
                                    ? Math.Abs(curRpm - pending.baselineRpm) < 50   // RPM aproape neschimbat
                                    : curRpm < 0;                                  // or still no RPM reading
                                if (noResponse && _fanWarnLabels.TryGetValue(fan.Name, out var warnTb))
                                    warnTb.Visibility = Visibility.Visible;
                                _fanVerify[fan.Name] = (pending.requestedAt, pending.baselineRpm, pending.requestedDuty, true);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { AppLogger.Debug("FanControl.RefreshRpm: " + ex.Message); }
        }

        // THERMAL-GUARD: if the temperature exceeds the threshold, force 100% on all controllable fans (above the chosen
        // preset), and go back when it drops.
        private void ApplyThermalGuard()
        {
            try
            {
                if (ChkThermalGuard.IsChecked != true) { if (_guardTripped) ReleaseGuard(); return; }
                float limit = 85;
                float.TryParse(TxtGuardLimit.Text, out limit);
                if (limit < 50) limit = 50;
                float t = _getMaxTemp();

                if (t >= limit && !_guardTripped)
                {
                    _guardTripped = true;
                    foreach (var fan in _fans.Where(f => f.CanControl))
                        _reader.SetFanDuty(fan, 100);
                    TxtFooterNote.Text = $"⚠ Thermal safety active: {TempUnit.Fmt(t)} ≥ {TempUnit.Fmt(limit)} — fans forced to 100%.";
                    TxtFooterNote.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(239, 68, 68));
                }
                else if (t < limit - 5 && _guardTripped)
                {
                    ReleaseGuard();
                }
            }
            catch (Exception ex) { AppLogger.Debug("FanControl.ApplyThermalGuard: " + ex.Message); }
        }

        private void ReleaseGuard()
        {
            _guardTripped = false;
            // back to what the user asked for (slider/preset)
            foreach (var fan in _fans.Where(f => f.CanControl))
                if (_userDuty.TryGetValue(fan, out float d)) _reader.SetFanDuty(fan, d);
            TxtFooterNote.Text = LanguageService.S("Fan", "FooterNote");
            TxtFooterNote.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        }

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string tag) return;
            foreach (var fan in _fans.Where(f => f.CanControl))
            {
                if (tag == "auto")
                {
                    _reader.SetFanAuto(fan);
                    if (_sliders.TryGetValue(fan, out var sl)) sl.Value = fan.DutyPercent;
                    continue;
                }
                float pct = tag switch { "silent" => 30, "medium" => 55, "turbo" => 100, _ => 50 };
                _userDuty[fan] = pct;
                if (_sliders.TryGetValue(fan, out var slider)) slider.Value = pct; // triggers SetFanDuty
                else _reader.SetFanDuty(fan, pct);
            }
        }

        private void ThermalGuard_Changed(object sender, RoutedEventArgs e)
        {
            if (ChkThermalGuard.IsChecked != true && _guardTripped) ReleaseGuard();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
        private void TitleBar_DragMove(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }
    }
}
