using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Models;
using SMDWin.Services;

namespace SMDWin.Views
{
    /// <summary>
    /// Dashboard in rows. Almost the whole row is bound in XAML (Binding ElementName) to the fields of the older dashboard,
    /// which the existing code keeps filling. Here:
    ///   • the small dials (PercentGauge) — follow the percentage text and update the arc;
    ///   • the chart window fixed at 1 minute;
    ///   • the "Activity" row (top processes by CPU and by memory);
    ///   • the automatic scale of the temperature charts (DashTempRange).
    /// </summary>
    public partial class MainWindow
    {
        private bool _dashRowsHooked;

        internal void DashRows_Loaded(object sender, RoutedEventArgs e)
        {
            if (_dashRowsHooked) return;
            _dashRowsHooked = true;
            try
            {
                // the CPU / GPU / RAM dials are set by SyncDashDials, in the same step in which the charts receive the value.
                // GPU stays bound to the text only as a fallback, when there is no load sensor.
                HookGauge(FindName("TxtDashGpuPct") as TextBlock, FindName("DashGaugeGpu") as PercentGauge);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "DashRows_Loaded: gauges"); }

            ButtonContrastGuard.Install();

            try { ThemeManager.ThemeChanged += () => Dispatcher.BeginInvoke(new Action(RefreshThemedIcons), System.Windows.Threading.DispatcherPriority.Background); }
            catch (Exception ex) { AppLogger.Warning(ex, "DashRows_Loaded: theme icons"); }

            try { InitDashBatteryRow(); }
            catch (Exception ex) { AppLogger.Warning(ex, "DashRows_Loaded: battery row"); }

            // Dashboard charts: the window saved in the settings (1 minute by default; changed from the chart's right-click menu)
            try
            {
                int saved = SettingsService.Current.ChartWindowSeconds is 60 or 300 or 900 or 3600 ? SettingsService.Current.ChartWindowSeconds : 60;
                if (FindName($"BtnChartWindow{saved}") is System.Windows.Controls.RadioButton rbSaved)
                {
                    if (rbSaved.IsChecked == true) ChartWindowBtn_Click(rbSaved, new RoutedEventArgs());
                    else rbSaved.IsChecked = true;   // Checked → ChartWindowBtn_Click
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "DashRows_Loaded: chart window"); }
        }

        // ══ Warning outline on rows ═══════════════════════════════════════
        // UpdateCardAlert decides WHEN a value has been critical for too long (AlertDelay).
        // Several sources may target the same row (e.g. the processor's load AND temperature), so each source's state is kept
        // and the outline stays while at least one is active.
        private readonly System.Collections.Generic.Dictionary<string, bool> _dashAlertState = new();

        private void SetDashRowAlert(Border card, bool active)
        {
            try
            {
                string src = ReferenceEquals(card, DashCpuCard) ? "cpu"
                           : ReferenceEquals(card, DashCpuTempCard) ? "cputemp"
                           : ReferenceEquals(card, DashGpuCard) ? "gpu"
                           : ReferenceEquals(card, DashGpuTempCard) ? "gputemp"
                           : ReferenceEquals(card, DashRamCard) ? "ram"
                           : ReferenceEquals(card, DashPingCard) ? "ping" : "";
                if (src.Length == 0) return;
                if (_dashAlertState.TryGetValue(src, out bool prev) && prev == active) return;
                _dashAlertState[src] = active;

                bool On(string k) => _dashAlertState.TryGetValue(k, out bool v) && v;
                ApplyRowAlert(DashCpuCard, On("cpu") || On("cputemp"));
                ApplyRowAlert(DashGpuCard, On("gpu") || On("gputemp"));
                ApplyRowAlert(DashRamCard, On("ram"));
                ApplyRowAlert(FindName("DashNetRow") as Border, On("ping"));
                UpdateAuroraAlert();   // a lasting high temperature also tints the Dark background
            }
            catch (Exception ex) { AppLogger.Debug($"SetDashRowAlert: {ex.Message}"); }
        }

        private void ApplyRowAlert(Border? row, bool on) => SetCardGlow(row, on ? "StatusCritBrush" : null);

        // ══ Alert glow ══════════════════════════════════════════
        // Critical value → an outline in the state colour + a very discreet coloured shadow around the card.
        // The shadow is NOT put on the card (it would blur the text): an empty Border (CompGlow style) with a DropShadowEffect
        // is placed behind it, in the same grid cell. brushKey = null → remove everything.
        private readonly System.Collections.Generic.Dictionary<Border, Border> _cardGlows = new();
        private readonly System.Collections.Generic.HashSet<System.Windows.Media.Effects.DropShadowEffect> _glowAnimated = new();

        internal void SetCardGlow(Border? card, string? brushKey)
        {
            if (card == null) return;
            try
            {
                _cardGlows.TryGetValue(card, out var glow);
                if (brushKey == null)
                {
                    if (glow != null)
                    {
                        glow.Visibility = Visibility.Collapsed;
                        // no animation while not visible (uses no processor)
                        if (glow.Effect is System.Windows.Media.Effects.DropShadowEffect old && _glowAnimated.Remove(old))
                            old.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, null);
                    }
                    return;
                }
                // no coloured outline — only the discreet shadow
                if (glow == null && card.Parent is Grid host)
                {
                    glow = new Border { Style = TryFindResource("CompGlow") as Style };
                    Grid.SetRow(glow, Grid.GetRow(card));
                    Grid.SetColumn(glow, Grid.GetColumn(card));
                    Grid.SetRowSpan(glow, Grid.GetRowSpan(card));
                    Grid.SetColumnSpan(glow, Grid.GetColumnSpan(card));
                    System.Windows.Controls.Panel.SetZIndex(glow, -1);
                    host.Children.Insert(0, glow);
                    _cardGlows[card] = glow;
                }
                if (glow == null) return;
                var c = (TryFindResource(brushKey) as SolidColorBrush)?.Color ?? System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44);
                // the effect is kept between ticks (otherwise the animation would restart every second); only the colour changes
                if (glow.Effect is not System.Windows.Media.Effects.DropShadowEffect fx)
                {
                    fx = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        ShadowDepth = 0, BlurRadius = 28, Opacity = 0.38,
                        RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
                    };
                    glow.Effect = fx;
                }
                if (fx.Color != c) fx.Color = c;
                // slow breathing: the shadow fades in and out smoothly (with animations off it stays fixed)
                if (SettingsService.Current.EnableAnimations)
                {
                    if (!_glowAnimated.Contains(fx))
                    {
                        var breathe = new System.Windows.Media.Animation.DoubleAnimation(0.12, 0.40, TimeSpan.FromSeconds(2.2))
                        {
                            AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                            EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
                        };
                        // a blurred shadow redrawn at 60 frames a second is costly; a slow breath looks the same at 15
                        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(breathe, 15);
                        fx.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, breathe);
                        _glowAnimated.Add(fx);
                    }
                }
                else if (_glowAnimated.Remove(fx))
                    fx.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, null);
                glow.Visibility = card.Visibility;
            }
            catch (Exception ex) { AppLogger.Debug($"SetCardGlow: {ex.Message}"); }
        }

        // ══ Dashboard height = the visible area (no scroll when it fits) ══════
        // Subtract the margins from ViewportHeight; below MinHeight (very low windows) the scroll appears.
        internal void DashRowsScroll_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!e.HeightChanged || sender is not ScrollViewer sv || FindName("DashRowsGrid") is not Grid g) return;
            double h = sv.ActualHeight - g.Margin.Top - g.Margin.Bottom - sv.Padding.Top - sv.Padding.Bottom - 1;
            g.Height = Math.Max(g.MinHeight, h);
        }

        // ══ Icons after a theme change ═════════════════════════════════════════
        // Image icons with Source="{DynamicResource Icon.*}" were not always reloaded when ThemeManager swapped the
        // IconsLight ⇄ IconsDark dictionary (they stayed drawn for the old theme — e.g. dark icons on a Dark background).
        // After every theme change the resource reference is set again for all images in the open windows → WPF looks again
        // and finds the new theme's variant.
        private static readonly System.Reflection.PropertyInfo? _resRefKeyProp =
            typeof(FrameworkElement).Assembly.GetType("System.Windows.ResourceReferenceExpression")
                ?.GetProperty("ResourceKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

        private void RefreshThemedIcons()
        {
            try
            {
                foreach (Window w in System.Windows.Application.Current.Windows)
                    RefreshThemedIconsIn(w, 0);
            }
            catch (Exception ex) { AppLogger.Debug($"RefreshThemedIcons: {ex.Message}"); }
        }

        /// <summary>
        /// Text, icons and backgrounds to which code assigned a brush from the theme resources once keep the old colour after a
        /// theme change. If the element's value is such an old object, it is bound to the resource it came from: it takes the
        /// new colour now and on later changes.
        /// </summary>
        private static void RebindStaleThemeBrushes(DependencyObject node)
        {
            if (node is not FrameworkElement fe) return;
            void Fix(DependencyProperty dp)
            {
                if (fe.ReadLocalValue(dp) is System.Windows.Media.Brush b && ThemeManager.TryGetStaleBrushKey(b, out string key))
                    fe.SetResourceReference(dp, key);
            }
            switch (fe)
            {
                case System.Windows.Shapes.Shape:
                    Fix(System.Windows.Shapes.Shape.FillProperty); Fix(System.Windows.Shapes.Shape.StrokeProperty); break;
                case System.Windows.Controls.TextBlock:
                    Fix(System.Windows.Controls.TextBlock.ForegroundProperty); Fix(System.Windows.Controls.TextBlock.BackgroundProperty); break;
                case System.Windows.Controls.Border:
                    Fix(System.Windows.Controls.Border.BackgroundProperty); Fix(System.Windows.Controls.Border.BorderBrushProperty); break;
                case System.Windows.Controls.Control:
                    Fix(System.Windows.Controls.Control.ForegroundProperty);
                    Fix(System.Windows.Controls.Control.BackgroundProperty);
                    Fix(System.Windows.Controls.Control.BorderBrushProperty); break;
                case System.Windows.Controls.Panel:
                    Fix(System.Windows.Controls.Panel.BackgroundProperty); break;
            }
        }

        private static void RefreshThemedIconsIn(DependencyObject node, int depth)
        {
            if (depth > 200) return;
            if (ThemeManager.HasStaleBrushes) RebindStaleThemeBrushes(node);
            if (_resRefKeyProp != null && node is System.Windows.Controls.Image img)
            {
                var local = img.ReadLocalValue(System.Windows.Controls.Image.SourceProperty);
                if (local != null && local.GetType().Name == "ResourceReferenceExpression")
                {
                    object? key = _resRefKeyProp!.GetValue(local);
                    if (key != null)
                    {
                        img.ClearValue(System.Windows.Controls.Image.SourceProperty);
                        img.SetResourceReference(System.Windows.Controls.Image.SourceProperty, key);
                    }
                }
            }
            int n = node is Visual || node is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(node) : 0;
            if (n > 0)
                for (int i = 0; i < n; i++) RefreshThemedIconsIn(VisualTreeHelper.GetChild(node, i), depth + 1);
            else
                foreach (var child in LogicalTreeHelper.GetChildren(node))
                    if (child is DependencyObject d) RefreshThemedIconsIn(d, depth + 1);
        }

        // ══ Narrow window: below ~1100 px the DETAILS column is hidden ══════════════
        private bool? _dashNarrow;

        internal void DashRowsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!e.WidthChanged || sender is not Grid host) return;
            bool narrow = e.NewSize.Width < 1100;
            if (_dashNarrow == narrow) return;
            _dashNarrow = narrow;
            try
            {
                // All rows (and the table header) have the same structure: 6 columns, DETAILS = column 3
                foreach (var child in host.Children)
                {
                    Grid? g = child as Grid ?? (child as Border)?.Child as Grid;
                    if (g == null || g.ColumnDefinitions.Count != 6) continue;
                    g.ColumnDefinitions[3].Width = narrow ? new GridLength(0) : new GridLength(360);
                    foreach (UIElement c in g.Children)
                        if (Grid.GetColumn(c) == 3)
                            c.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
                }
            }
            catch (Exception ex) { AppLogger.Debug($"DashRowsGrid_SizeChanged: {ex.Message}"); }
        }

        // ══ Battery row (laptop only) ══════════════════════════════════════════
        private System.Windows.Threading.DispatcherTimer? _dashBatTimer;

        private void InitDashBatteryRow()
        {
            bool hasBattery;
            try { hasBattery = !System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus
                                   .HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery); }
            catch (Exception ex) { AppLogger.Debug($"InitDashBatteryRow: {ex.Message}"); hasBattery = false; }

            if (FindName("DashBatRowDef") is RowDefinition rd)
                rd.Height = hasBattery ? GridLength.Auto : new GridLength(0);
            // the cards stay at least ~210 px tall; below that height scrolling appears (small 14" screens)
            if (FindName("DashRowsGrid") is Grid dg) dg.MinHeight = hasBattery ? 790 : 710;
            if (FindName("DashBatGapDef") is RowDefinition gap)
                gap.Height = new GridLength(hasBattery ? 12 : 0);
            if (FindName("DashBatRow") is Border row)
                row.Visibility = hasBattery ? Visibility.Visible : Visibility.Collapsed;
            if (!hasBattery) return;

            UpdateDashBatteryRow();
            _dashBatTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _dashBatTimer.Tick += (_, _) =>
            {
                // only while the dashboard is visible
                if (FindName("DashRowsScroll") is FrameworkElement fe && fe.IsVisible) UpdateDashBatteryRow();
            };
            _dashBatTimer.Start();
        }

        private void UpdateDashBatteryRow()
        {
            try
            {
                var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                float pct = ps.BatteryLifePercent;            // 0..1 (255 = unknown)
                bool known = pct >= 0 && pct <= 1;
                int p = known ? (int)Math.Round(pct * 100) : -1;
                bool plugged = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
                bool charging = ps.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.Charging);
                int secs = ps.BatteryLifeRemaining;           // -1 = unknown / plugged in

                string left = !plugged && secs > 0
                    ? (secs >= 3600 ? $"{secs / 3600}h {secs % 3600 / 60}m" : $"{secs / 60}m")
                    : "—";
                string lc = LanguageService.CurrentCode;
                string status = charging ? (lc == "ro" ? "Se încarcă" : lc == "de" ? "Lädt" : lc == "fr" ? "En charge" : "Charging")
                    : plugged ? (p >= 99 ? (lc == "ro" ? "Plină" : lc == "de" ? "Voll" : lc == "fr" ? "Pleine" : "Full")
                                         : (lc == "ro" ? "La priză" : lc == "de" ? "Am Netz" : lc == "fr" ? "Sur secteur" : "Plugged in"))
                    : (lc == "ro" ? "Pe baterie" : lc == "de" ? "Akkubetrieb" : lc == "fr" ? "Sur batterie" : "On battery");

                if (FindName("TxtDashBatRowPct") is TextBlock tp)
                {
                    tp.Text = p >= 0 ? $"{p}" : "";   // „%” e desenat separat in cadran (CompDialUnit)
                    if (p >= 0 && p < 15) tp.SetResourceReference(TextBlock.ForegroundProperty, "StatusCritBrush");
                    else if (p >= 0 && p < 30) tp.SetResourceReference(TextBlock.ForegroundProperty, "StatusWarnBrush");
                    else tp.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                }
                // the state appears only once (on the right); under the percentage only the time left, on battery
                if (FindName("TxtDashBatRowTime") is TextBlock tt) tt.Text = !plugged && secs > 0 ? T4($"{left} left", $"{left} rămase", $"{left} übrig", $"{left} restantes") : "";
                if (FindName("TxtDashBatRowLeft") is TextBlock tl) tl.Text = left;
                if (FindName("TxtDashBatRowPlug") is TextBlock tpl) tpl.Text = plugged ? T4("AC adapter", "Încărcător", "Netzteil", "Adaptateur secteur") : T4("Battery", "Baterie", "Akku", "Batterie");
                if (FindName("TxtDashBatRowSource") is TextBlock ts) ts.Text = T4("Battery", "Baterie", "Akku", "Batterie");
                if (FindName("TxtDashBatRowStatus") is TextBlock st)
                {
                    st.Text = status;
                    st.SetResourceReference(TextBlock.ForegroundProperty,
                        charging || (plugged && p >= 99) ? "StatusOkBrush" : !plugged && p >= 0 && p < 20 ? "StatusCritBrush"
                        : !plugged ? "StatusWarnBrush" : "TextSecondaryBrush");
                }
                if (FindName("DashGaugeBat") is PercentGauge g)
                {
                    g.HasValue = p >= 0;
                    g.Value = Math.Max(0, p);
                    g.InvalidateVisual();
                }
                if (FindName("DashBatFillCol") is ColumnDefinition fc && FindName("DashBatRestCol") is ColumnDefinition rc && p >= 0)
                {
                    fc.Width = new GridLength(Math.Max(0.001, p), GridUnitType.Star);
                    rc.Width = new GridLength(Math.Max(0.001, 100 - p), GridUnitType.Star);
                }
                // green while charging; on battery yellow below 20%, red below 10%; otherwise the accent
                if (FindName("DashBatFill") is Border fill)
                    fill.SetResourceReference(Border.BackgroundProperty,
                        charging ? "StatusOkBrush"
                        : p >= 0 && p < 10 ? "StatusCritBrush"
                        : p >= 0 && p < 20 ? "StatusWarnBrush" : "DataBrush");
            }
            catch (Exception ex) { AppLogger.Debug($"UpdateDashBatteryRow: {ex.Message}"); }
        }

        // ══ Live values from the new footers / dials ══════════════════════
        private float _dashNetPeakKBs;

        private static string NoSensorTip() => LanguageService.S("Dashboard", "NoSensorValue",
            "Not reported on this computer. Most values need the sensor driver (Settings › Advanced › Sensors).");

        /// <summary>Called on every dashboard update: read / write / disk temperature in the Storage card footer and the
        /// traffic dial (percent of the network chart scale).</summary>
        private void UpdateDashCardsLive(float diskReadKBs, float diskWriteKBs, float netDownKBs, float netUpKBs)
        {
            try
            {
                // the Storage dial: how busy the disks are now (active time, as in Task Manager), and below read + write together;
                // used space shows in the partition bars next to it
                if (FindName("DashGaugeDisk") is PercentGauge dg)
                {
                    dg.HasValue = true;
                    dg.Value = Math.Clamp(_lastDiskActivePct, 0f, 100f);
                    dg.InvalidateVisual();
                }
                if (FindName("TxtDashDiskUsedPct") is TextBlock dp) dp.Text = $"{Math.Clamp(_lastDiskActivePct, 0f, 100f):F0}";
                if (FindName("DashRangeDisk") is TextBlock dr) dr.Text = FormatKBps(diskReadKBs + diskWriteKBs);
                if (FindName("TxtDashFootDiskRead") is TextBlock r) r.Text = FormatKBps(diskReadKBs);
                if (FindName("TxtDashFootDiskWrite") is TextBlock w) w.Text = FormatKBps(diskWriteKBs);
                if (FindName("TxtDashFootDiskTemp") is TextBlock t)
                {
                    float dt = CurrentDiskTempForChart();
                    t.Text = float.IsFinite(dt) && dt > 0 ? $"{TempUnit.Fmt(dt, 0, true)}" : "";
                }

                // scale: the recent peak (decays slowly), rounded as on the network chart (minimum 1 MB/s)
                float now = Math.Max(0, netDownKBs) + Math.Max(0, netUpKBs);
                _dashNetPeakKBs = Math.Max(now, _dashNetPeakKBs * 0.995f);
                float scale = NiceNetScale(_dashNetPeakKBs);
                if (FindName("DashGaugeNet") is PercentGauge g)
                {
                    g.HasValue = true;
                    g.Value = scale > 0 ? Math.Clamp(now / scale * 100f, 0f, 100f) : 0;
                }
                if (FindName("TxtDashNetScale") is TextBlock sc) sc.Text = "of " + FormatKBps(scale);

                // CPU — footer with live values: current frequency and package power (each appears only when the sensor provides it)
                if (FindName("TxtDashCpuFoot4V") is TextBlock clockNow && FindName("TxtDashCpuFoot5V") is TextBlock powerNow)
                {
                    var sn = _lastTempSnap;
                    clockNow.Text = sn?.CpuFreqMHz is float mhz && mhz > 100 ? $"{mhz / 1000f:F2} GHz" : "—";
                    clockNow.ToolTip = clockNow.Text == "—" ? NoSensorTip() : null;
                    powerNow.Text = sn?.CpuPowerW is float pkgW && pkgW > 0.5f ? $"{pkgW:F0} W" : "—";
                    powerNow.ToolTip = powerNow.Text == "—" ? NoSensorTip() : null;
                }

                // CPU: the hottest core (the top process comes from UpdateDashTopProcesses)
                if (FindName("TxtDashCpuFootHot") is TextBlock hotTb)
                {
                    var ct = _lastTempSnap?.CoreTemps;
                    float hot = ct != null && ct.Count > 0 ? ct.Max() : float.NaN;
                    hotTb.Text = float.IsFinite(hot) && hot > 0 ? TempUnit.Fmt(hot, 0, true) : "—";
                    hotTb.ToolTip = hotTb.Text == "—" ? NoSensorTip() : null;
                }

                // GPU: current frequency, driver, resolution
                if (FindName("TxtDashGpuFootClock") is TextBlock gClk)
                    {
                    // Intel integrated graphics (HD / UHD / Iris) do not report their frequency to Windows: the cell then shows the
                    // graphics power when the sensor has it, instead of a permanent "—" (i3-7100U · HD 620, 13.11.2026)
                    var gs = _lastTempSnap;
                    bool hasClock = gs?.GpuFreqMHz is float gmhz && gmhz > 50;
                    bool showPower = !hasClock && gs?.GpuPowerW is float gw0 && gw0 > 0.05f;
                    if (FindName("TxtDashGpuFootClockKey") is TextBlock gKey) gKey.Text = PT(showPower ? "Power draw" : "Clock");
                    gClk.Text = hasClock ? SMDWin.Services.DisplayFormat.Freq(gs!.GpuFreqMHz!.Value)
                              : showPower ? $"{gs!.GpuPowerW!.Value:0.0} W" : "—";
                    gClk.ToolTip = hasClock ? null
                                 : showPower ? PT("This graphics chip does not report its frequency; its power draw is shown instead.")
                                 : NoSensorTip();
                }
                if (FindName("TxtDashGpuFootDriver") is TextBlock gDrv)
                {
                    // the same version as in System Info (NVIDIA: 426.06, not 25.21.14.2606); the Windows one stays in the tooltip
                    string raw = _summary?.GpuDriverVersion ?? "";
                    string shown = raw.Length > 0 ? SMDWin.Services.GpuPackage.DriverText(_summary?.GpuName ?? "", raw) : "";
                    gDrv.Text = shown.Length > 0 ? shown : "—";
                    gDrv.ToolTip = shown != raw && raw.Length > 0 ? $"Windows: {raw}" : null;
                }
                if (FindName("TxtDashGpuFootDisplay") is TextBlock gDsp)
                    gDsp.Text = string.IsNullOrWhiteSpace(_summary?.DisplayResolution) ? "—" : _summary!.DisplayResolution;

                // RAM: the module speed (the last part of "DDR3 · 2 × 4 GB · 1600 MHz")
                if (FindName("TxtDashRamFootSpeed") is TextBlock rSpd)
                {
                    string rt = _summary?.RamType ?? "";
                    string last = rt.Split('·').LastOrDefault()?.Trim() ?? "";
                    rSpd.Text = last.Contains("MHz", StringComparison.OrdinalIgnoreCase) ? last : "";
                }
            }
            catch (Exception ex) { AppLogger.Debug($"UpdateDashCardsLive: {ex.Message}"); }
        }

        /// <summary>The Wi-Fi signal on the Network row: 4 bars, the "lit" ones in the accent; yellow below 30%.
        /// pct &lt; 0 = not Wi-Fi / could not be read → the bars disappear.</summary>
        private void UpdateDashWifiBars(int pct)
        {
            try
            {
                if (FindName("DashWifiBars") is not StackPanel bars) return;
                if (pct < 0 || pct > 100) { bars.Visibility = Visibility.Collapsed; return; }
                bars.Visibility = Visibility.Visible;
                int lit = pct >= 75 ? 4 : pct >= 50 ? 3 : pct >= 25 ? 2 : pct > 0 ? 1 : 0;
                string onKey = pct < 30 ? "StatusWarnBrush" : "DataBrush";
                for (int i = 0; i < bars.Children.Count; i++)
                    if (bars.Children[i] is Border b)
                        b.SetResourceReference(Border.BackgroundProperty, i < lit ? onKey : "BorderBrush2");
                string q = pct >= 75 ? T4("excellent", "excelent", "ausgezeichnet", "excellent") : pct >= 50 ? T4("good", "bun", "gut", "bon")
                         : pct >= 25 ? T4("fair", "acceptabil", "mittel", "moyen") : T4("weak", "slab", "schwach", "faible");
                bars.ToolTip = T4($"Wi-Fi signal {pct}% ({q})", $"Semnal Wi-Fi {pct}% ({q})", $"WLAN-Signal {pct}% ({q})", $"Signal Wi-Fi {pct}% ({q})");
            }
            catch (Exception ex) { AppLogger.Debug($"UpdateDashWifiBars: {ex.Message}"); }
        }

        // ══ Dials + big values in sync with the charts ══════════════════
        // Called from UpdateDashboardLiveCardsInternal right after PushToQueues: the same values reach the charts (drawn on the
        // next frame, ≤ 100 ms) and the dials / labels → everything moves in a single step.
        private void SyncDashDials(float cpu, float gpu, bool gpuValid, float ram, double ramUsedMB, double ramTotalMB,
                                   float cpuTemp, float gpuTemp, float pingMs, float netDownKBps, float netUpKBps)
        {
            try
            {
                UpdateDashRanges(cpu, gpu, gpuValid, ram, netDownKBps, netUpKBps);
                if (FindName("DashGaugeCpu") is PercentGauge gc) { gc.HasValue = true; gc.Value = cpu; }
                if (FindName("TxtDialCpu") is TextBlock tc) { tc.Text = $"{Math.Round(cpu):F0}"; DialFigureColor(tc, FindName("DashGaugeCpu") as PercentGauge, cpu); }
                if (gpuValid)
                {
                    if (FindName("DashGaugeGpu") is PercentGauge gg) { gg.HasValue = true; gg.Value = gpu; }
                    if (FindName("TxtDialGpu") is TextBlock tg) { tg.Text = $"{Math.Round(gpu):F0}"; DialFigureColor(tg, FindName("DashGaugeGpu") as PercentGauge, gpu); }
                }
                else if (FindName("TxtDialGpu") is TextBlock tg2 && FindName("TxtDashGpuPct") is TextBlock src)
                tg2.Text = src.Text;
                if (ram > 0)
                {
                    if (FindName("DashGaugeRam") is PercentGauge gr) { gr.HasValue = true; gr.Value = ram; }
                    if (FindName("TxtDialRam") is TextBlock tr) { tr.Text = $"{Math.Round(ram):F0}"; DialFigureColor(tr, FindName("DashGaugeRam") as PercentGauge, ram); }
                    if (ramTotalMB > 0 && FindName("TxtDialRamUsed") is TextBlock tu)
                        tu.Text = $"{ramUsedMB / 1024.0:F1} / {Math.Round(ramTotalMB / 1024.0):F0} GB";
                }
                if (float.IsFinite(cpuTemp) && cpuTemp > 0 && FindName("TxtDialCpuTemp") is TextBlock ct) ct.Text = $"{TempUnit.Fmt(cpuTemp, 0, true)}";
                if (float.IsFinite(gpuTemp) && gpuTemp > 0 && FindName("TxtDialGpuTemp") is TextBlock gt) gt.Text = $"{TempUnit.Fmt(gpuTemp, 0, true)}";
                if (float.IsFinite(pingMs) && pingMs > 0 && FindName("TxtDialPing") is TextBlock pt) pt.Text = $"{pingMs:F0} ms";
                // the Network dial: only the big number, the unit and the direction below (upload is in the chart header)
                string rate = FormatKBps(Math.Max(0, netDownKBps));
                int sp = rate.IndexOf(' ');
                if (sp > 0)
                {
                    if (FindName("TxtDialNetNum") is TextBlock nn) nn.Text = rate[..sp];
                    if (FindName("TxtDialNetUnitIn") is TextBlock ni) ni.Text = rate[(sp + 1)..];
                    if (FindName("TxtDialNetUnit") is TextBlock nu) nu.Text = "↓ " + LanguageService.S("Dashboard", "NetDownWord", "download");
                }
            }
            catch (Exception ex) { AppLogger.Debug($"SyncDashDials: {ex.Message}"); }
        }

        /// <summary>The figure in a dial takes the colour of the dial's zone (normal / attention / critical), blending smoothly.</summary>
        private static void DialFigureColor(TextBlock tb, PercentGauge? g, float value)
        {
            double warn = g?.WarnAt ?? 75, crit = g?.CritAt ?? 90;
            ColorFx.Foreground(tb, value >= crit ? "StatusCritBrush" : value >= warn ? "StatusWarnBrush" : "TextPrimaryBrush");
        }

        // ══ Min / max under the dials (the chart interval) ══════════════════
        private readonly DashRangeTracker _rangeCpu = new(), _rangeGpu = new(), _rangeRam = new(), _rangeNetDown = new(), _rangeNetUp = new();

        private int DashRangeSeconds => _chartTimeRangeSeconds > 0 ? Math.Min(_chartTimeRangeSeconds, 3600) : 60;


        /// <summary>"▼ min   ▲ max" as on the Dashboard, for a named TextBlock (used under the dials in Temp &amp; Stress).</summary>
        private void SetArrowRange(string name, string min, string max, bool hot = false)
        {
            if (FindName(name) is not TextBlock tb) return;
            string key = $"{min}|{max}|{hot}";
            if (tb.Tag as string == key && tb.Inlines.Count > 0) return;   // cleared on "Reset Min/Max" → written again
            tb.Tag = key;
            tb.Inlines.Clear();
            // Resource references (not captured brushes): the colour follows the theme even after a Dark/Light change.
            void Part(string text, string brushKey)
            {
                var run = new System.Windows.Documents.Run(text);
                run.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, brushKey);
                tb.Inlines.Add(run);
            }
            Part("▼ ", "AccentBrush"); Part(min, "TextPrimaryBrush");
            Part("   ▲ ", "StatusWarnBrush"); Part(max, "TextPrimaryBrush");
        }

        private void SetDashMaxLabel(string labelName, string baseText, DashRangeTracker tracker, Func<float, string> format)
        {
            if (FindName(labelName) is TextBlock tb && tracker.TryGet(out _, out float max))
                tb.Text = $"{SMDWin.Services.LiteralTexts.Translate(baseText)}  ·  max {format(max)}";
        }

        private void UpdateDashRanges(float cpu, float gpu, bool gpuValid, float ram, float netDownKBps, float netUpKBps)
        {
            int window = DashRangeSeconds;
            _rangeCpu.Add(cpu, window);
            if (gpuValid) _rangeGpu.Add(gpu, window);
            if (ram > 0) _rangeRam.Add(ram, window);
            _rangeNetDown.Add(netDownKBps, window);
            _rangeNetUp.Add(netUpKBps, window);
            // the maximum sits in the chart header ("load · max 25%"), not under the dial
            SetDashMaxLabel("DashLoadLabelCpu", "load", _rangeCpu, v => $"{v:F0}%");
            if (gpuValid) SetDashMaxLabel("DashLoadLabelGpu", "load", _rangeGpu, v => $"{v:F0}%");
            if (ram > 0) SetDashMaxLabel("DashLoadLabelRam", "usage", _rangeRam, v => $"{v:F0}%");
        }

        private static void HookGauge(TextBlock? source, PercentGauge? gauge)
        {
            if (source == null || gauge == null) return;
            void Sync()
            {
                string t = (source.Text ?? "").Replace("%", "").Trim();
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                    || double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
                {
                    gauge.HasValue = true;
                    gauge.Value = v;
                }
                else
                {
                    gauge.HasValue = false;
                }
                gauge.InvalidateVisual();
            }
            var dpd = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            dpd?.AddValueChanged(source, (_, _) => Sync());
            Sync();
        }

        /// <summary>
        /// Automatic scale for the dashboard temperature charts: instead of a fixed 30–105 °C (the line stayed flat in the
        /// middle), the range follows the values in the visible window, with a ~6 °C margin and at least 20 °C between the ends
        /// (small variations do not look dramatic).
        /// </summary>
        private static (float Lo, float Hi) DashTempRange(float[]? arr)
        {
            if (arr == null || arr.Length == 0) return (30f, 105f);
            var slice = SliceLastN(arr, SettingsService.Current.ChartWindowSeconds);
            float mn = float.MaxValue, mx = float.MinValue;
            foreach (var v in slice)
            {
                if (float.IsNaN(v) || v <= 0) continue;
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
            if (mx < mn) return (30f, 105f);
            float lo = (float)Math.Floor((mn - 6f) / 5f) * 5f;
            float hi = (float)Math.Ceiling((mx + 6f) / 5f) * 5f;
            if (hi - lo < 20f) { float mid = (hi + lo) / 2f; lo = mid - 10f; hi = mid + 10f; }
            return (Math.Max(0f, lo), Math.Min(120f, hi));
        }

        /// <summary>The "Activity" row: the top 3 processes by CPU and by memory + the number of processes.</summary>
        private void UpdateDashTopProcesses(ProcessSnapshot snap)
        {
            try
            {
                if (FindName("TxtDashProcCount") is TextBlock cnt)
                    cnt.Text = snap.ProcessCount > 0 ? snap.ProcessCount.ToString(CultureInfo.InvariantCulture)
                             : snap.AllProcesses.Count > 0 ? snap.AllProcesses.Count.ToString(CultureInfo.InvariantCulture) : "—";
                if (FindName("DashTopCpuList") is StackPanel cpuList)
                    FillTopList(cpuList, (snap.TopByCpu ?? new()).Take(3).Select(p => (p.Name, $"{p.CpuPct:F1}%")));
                // the processor card footer: the application using the most processor, grouped as for memory
                if (FindName("TxtDashCpuFootTop") is TextBlock topCpu)
                {
                    var src = snap.AllProcesses is { Count: > 0 } allP ? allP : (snap.TopByCpu ?? new());
                    var g = BuildGrouped(src, snap.UsedRamMB)
                        .Where(x => !string.Equals(x.DisplayName, "Idle", StringComparison.OrdinalIgnoreCase)
                                 && !string.Equals(x.DisplayName, "System Idle Process", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(x => x.TotalCpuPct).FirstOrDefault();
                    if (g == null || g.TotalCpuPct < 0.5f) { topCpu.Text = "—"; topCpu.ToolTip = null; }
                    else
                    {
                        string exe = string.IsNullOrWhiteSpace(g.ExeName) ? g.DisplayName : g.ExeName;
                        if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = exe[..^4];
                        topCpu.Text = $"{exe} · {g.TotalCpuPct:F0}%";
                        topCpu.ToolTip = g.DisplayName;
                    }
                }
                // the Memory card footer: the top 3 applications, grouped as on the RAM page ("msedge · 21"), so the numbers match
                var topRam = BuildGrouped(snap.AllProcesses is { Count: > 0 } all ? all : (snap.TopByRam ?? new()), snap.UsedRamMB)
                    .OrderByDescending(g => g.TotalRamMB).Take(3).ToList();
                for (int i = 1; i <= 3; i++)
                {
                    var p = i <= topRam.Count ? topRam[i - 1] : null;
                    // the key says clearly what it is ("Top process 1"), the value = name · memory
                    if (FindName($"TxtDashRamTop{i}K") is TextBlock k) k.Text = p == null ? "" : string.Format(LanguageService.CurrentCode switch { "ro" => "Proces de top {0}", "de" => "Top-Prozess {0}", "fr" => "Processus n° {0}", _ => "Top process {0}" }, i);
                    if (FindName($"TxtDashRamTop{i}V") is TextBlock v)
                    {
                        string exe = p == null ? "" : string.IsNullOrWhiteSpace(p.ExeName) ? p.DisplayName : p.ExeName;
                        if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = exe[..^4];
                        // name + memory (the process count is in the tooltip: "svchost (95) · 2.17 GB" did not fit in the cell)
                        v.Text = p == null ? "" : $"{exe} · "
                            + (p.TotalRamMB >= 1024 ? $"{p.TotalRamMB / 1024:F1} GB" : $"{p.TotalRamMB:F0} MB");
                        v.ToolTip = p == null ? null : p.InstanceCount > 1 ? $"{p.DisplayName} · {p.InstanceCount} processes" : p.DisplayName;
                    }
                }
                if (FindName("DashRamTopList") is StackPanel ramTop) // the Memory row
                    FillTopList(ramTop, (snap.TopByRam ?? new()).Take(3).Select(p => (p.Name,
                        p.RamMB >= 1024 ? $"{p.RamMB / 1024:F2} GB" : $"{p.RamMB:F0} MB")));
                if (FindName("DashTopRamList") is StackPanel ramList)
                    FillTopList(ramList, (snap.TopByRam ?? new()).Take(3).Select(p => (p.Name,
                        p.RamMB >= 1024 ? $"{p.RamMB / 1024:F2} GB" : $"{p.RamMB:F0} MB")));
            }
            catch (Exception ex) { AppLogger.Debug($"UpdateDashTopProcesses: {ex.Message}"); }
        }

        private static void FillTopList(StackPanel host, System.Collections.Generic.IEnumerable<(string Name, string Value)> items)
        {
            var list = items.ToList();
            // reuse the existing rows (no reallocation every 3 s)
            while (host.Children.Count < list.Count)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 2) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var n = new TextBlock { FontSize = Ui.FontS, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0) };
                n.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                var v = new TextBlock { FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold };
                v.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
                v.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                Grid.SetColumn(v, 1);
                g.Children.Add(n); g.Children.Add(v);
                host.Children.Add(g);
            }
            while (host.Children.Count > list.Count) host.Children.RemoveAt(host.Children.Count - 1);
            for (int i = 0; i < list.Count; i++)
            {
                if (host.Children[i] is Grid g && g.Children.Count == 2
                    && g.Children[0] is TextBlock n && g.Children[1] is TextBlock v)
                {
                    n.Text = list[i].Name;
                    v.Text = list[i].Value;
                }
            }
        }
    }

    /// <summary>
    /// Formatting for the dashboard rows:
    ///   • temperatures: "68°" → "68 °C", "68" → "68 °C", "N/A" stays "N/A";
    ///   • ConverterParameter=cpu: the processor name without ® ™ "CPU" and "@ 3.20GHz"
    ///     ("Intel(R) Core(TM) i5-4570 CPU @ 3.20GHz" → "Intel Core i5-4570").
    /// </summary>
    public sealed class DashTempConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            string s = (value as string ?? "").Trim();
            if (parameter as string == "cpu")
            {
                if (s.Length == 0) return "—";
                string c = Regex.Replace(s, @"\((R|TM|tm|r)\)|®|™", "");
                c = Regex.Replace(c, @"\s*@.*$", "");
                c = Regex.Replace(c, @"\b(CPU|Processor)\b", "", RegexOptions.IgnoreCase);
                c = Regex.Replace(c, @"\s{2,}", " ").Trim();
                return c.Length > 0 ? c : s;
            }
            if (s.Length == 0 || s == "—") return "—";
            // the source (TxtDash*TempBig) is already in the chosen unit — only the symbol is added here
            string num = s.TrimEnd('C', 'c', 'F', 'f').TrimEnd('°', ' ');
            return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                || double.TryParse(num, NumberStyles.Float, CultureInfo.CurrentCulture, out _)
                ? $"{num} {TempUnit.Symbol}" : s;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Dashboard detail rows (e.g. "Socket", "Slots") disappear when the value could not be read (empty, "—", "-",
    /// "N/A", "Unknown") — no dashes.</summary>
    public sealed class DashFactVisibilityConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string s = (value as string ?? "").Trim();
            bool empty = s.Length == 0 || s is "—" or "-" or "–" or "N/A" or "n/a" or "Unknown" or "?" or "Not reported";
            return empty ? Visibility.Collapsed : Visibility.Visible;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Value in the CPU / GPU card footer: "—" when it cannot be read, so each card keeps its 4 cells (a symmetric
    /// footer even on computers without sensors).</summary>
    public sealed class DashDashConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.IsNullOrWhiteSpace(value as string) ? "—" : value!;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>"Gateway · DNS": a single address when the router is also the DNS server (the usual case), otherwise both.</summary>
    public sealed class DashGatewayDnsConverter : System.Windows.Data.IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            string gw = (values.Length > 0 ? values[0] as string : null)?.Trim() ?? "";
            string dns = (values.Length > 1 ? values[1] as string : null)?.Trim() ?? "";
            if (gw is "" or "—") return dns.Length > 0 ? dns : "—";
            if (dns is "" or "—" || string.Equals(gw, dns, StringComparison.OrdinalIgnoreCase)) return gw;
            return $"{gw} · {dns}";
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Short date on the dashboard: "19 Aug 2026" / "2026-08-19" → "19.08.2026". If the text is not a date, it stays as is.</summary>
    /// <summary>The System card: "22H2 (22621)" → the "ver" parameter gives "22H2" (large), "build" gives "build 22621"
    /// (subtitle). With no version shown → the build number is the large value.</summary>
    public sealed class DashOsVersionConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string s = (value as string ?? "").Trim();
            if (s == "—") s = "";
            string ver = s, build = "";
            var m = Regex.Match(s, @"^(.*?)\s*\((\d[\d.]*)\)\s*$");
            if (m.Success) { ver = m.Groups[1].Value.Trim(); build = m.Groups[2].Value; }
            else if (Regex.IsMatch(s, @"^\d[\d.]*$")) { ver = s; build = ""; }
            return (parameter as string) == "build"
                ? (build.Length > 0 ? "build " + build : (ver.Length > 0 ? "Windows build" : ""))
                : ver;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    public sealed class DashShortDateConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string s = (value as string ?? "").Trim();
            if (s.Length == 0) return s;
            string[] fmts = { "d MMM yyyy", "dd MMM yyyy", "d MMMM yyyy", "yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "MM/dd/yyyy", "M/d/yyyy", "dd/MM/yyyy" };
            if (SMDWin.Services.DateFormatHelper.TryParseShown(s, out var d)
                || DateTime.TryParseExact(s, fmts, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out d)
                || DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out d)
                || DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out d))
                return SMDWin.Services.DisplayFormat.Date(d);
            return s;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
