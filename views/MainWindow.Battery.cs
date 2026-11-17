using System;
using SMDWin.Views;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SMDWin;
using SMDWin.Models;
using SMDWin.Services;
using Forms = System.Windows.Forms;
using Application      = System.Windows.Application;
using MessageBox       = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage  = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using SaveFileDialog   = Microsoft.Win32.SaveFileDialog;
using ToolTip          = System.Windows.Controls.ToolTip;
using OpenFileDialog   = Microsoft.Win32.OpenFileDialog;
using Button           = System.Windows.Controls.Button;
using Brush            = System.Windows.Media.Brush;
using WpfColor         = System.Windows.Media.Color;
using WpfColorConv     = System.Windows.Media.ColorConverter;

namespace SMDWin.Views
{
    public partial class MainWindow
    {
        // ── BATTERY ───────────────────────────────────────────────────────────

        private async Task LoadBatteryAsync()
        {
            ShowLoading(_LS("Battery", "Loading"));
            try
            {
                // Sleep & wake is read in parallel with the battery and does not depend on it (a battery error does not stop it)
                _ = RefreshPowerDiagAsync();
                var info = await _batterySvc.GetBatteryInfoAsync();
                UpdateBatteryUI(info);
                RenderBattery(info);   // the Battery tab (MainWindow.PowerRedesign.cs)
                LoadActivePowerPlan();
                _ = RefreshPowerSimpleAsync(info.Present);   // idle settings, processor steps, start-up (MainWindow.PowerSimple.cs)
            }
            finally { HideLoading(); }
        }

        private async void OnBatTick(object? s, EventArgs e)
        {
            if (_batTickBusy) return;
            _batTickBusy = true;
            try
            {
                var info = await _batterySvc.GetBatteryInfoAsync();
                UpdateBatteryUI(info);
                RenderBattery(info);
            }
            finally { _batTickBusy = false; }
        }

        private void UpdateBatteryUI(SMDWin.Models.BatteryInfo info)
        {
            bool present = info.Present;
            // No battery (desktop): the "No battery detected" panel + the decorative animation below.
            if (FindName("BatteryNoBatteryPanel") is System.Windows.Controls.Border noBatPanel)
                noBatPanel.Visibility = present ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            if (present) StopPowerFlowAnimationIfRunning(); // bateria a aparut → panoul dispare, animatia n-are rost sa ruleze ascunsa
            if (FindName("BatteryHeroCard") is System.Windows.Controls.Border heroCard)
                heroCard.Visibility = present ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (FindName("BatDrainCard") is System.Windows.Controls.Border drainCard)
                drainCard.Visibility = present ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            // The monitoring/test buttons in the title make no sense without a battery
            if (FindName("BtnBatteryMonStart") is System.Windows.Controls.Button bMon)
                bMon.Visibility = present ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            // the battery test is not offered on the Power page until it is finished
            if (FindName("BtnBatteryTest") is System.Windows.Controls.Button bTest)
                bTest.Visibility = System.Windows.Visibility.Collapsed;
            if (FindName("BtnFullBatteryReport") is System.Windows.Controls.Button bRep)
                bRep.Visibility = present ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

            if (!present)
            {
                if (TxtBatDeviceName != null) TxtBatDeviceName.Text = "";
                return;
            }

            var chargeColor = (WpfColor)WpfColorConv.ConvertFromString(info.ChargeColor)!;

            // Battery device name
            if (TxtBatDeviceName != null)
                TxtBatDeviceName.Text = string.IsNullOrWhiteSpace(info.Name) ? "" : $"{info.Name}";

            _lastBatteryInfo = info;
            TxtBatCharge.Text  = $"{info.ChargePercent}%";
            TxtBatCharge.SetResourceReference(TextBlock.ForegroundProperty, BatteryBlocks.ZoneOf(info.ChargePercent) switch
            {
                BatteryZone.Crit => "StatusCritBrush",
                BatteryZone.Warn => "StatusWarnBrush",
                _                => "StatusOkBrush",
            });
            // the state in one word + the explanation (when there is something to explain) — MainWindow.BatteryCard.cs
            var assessment = AssessBattery(info);
            ShowBatteryVerdict(info, assessment);

            double? liveW = info.IsCharging && info.ChargePowerW > 0 ? info.ChargePowerW
                          : !info.IsCharging && info.DischargePowerW > 0 ? -info.DischargePowerW
                          : (double?)null;
            if (FindName("PanelBatPower") is System.Windows.FrameworkElement panelPower)
                panelPower.Visibility = liveW.HasValue ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (FindName("SepBatPower") is System.Windows.FrameworkElement sepPower)
                sepPower.Visibility = liveW.HasValue ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (liveW.HasValue)
            {
                TxtBatPower.Text = $"{(liveW.Value >= 0 ? "+" : "")}{liveW.Value:F1} W";
                if (FindName("TxtPowerLabel") is System.Windows.Controls.TextBlock powerLabel)
                    powerLabel.Text = info.IsCharging
                        ? LanguageService.S("Battery", "ChargingNowLabel", "CHARGING NOW")
                        : LanguageService.S("Battery", "PowerLabel", "LIVE DRAW");
            }

            if (FindName("PanelBatTimeToFull") is System.Windows.FrameworkElement panelEta
                && FindName("SepBatTimeToFull") is System.Windows.FrameworkElement sepEta
                && FindName("TxtBatTimeToFull") is System.Windows.Controls.TextBlock etaTb)
            {
                bool hasEta = info.IsCharging && info.ChargePowerW > 0 && info.FullCapacityMWh > 0 && info.ChargePercent < 100;
                panelEta.Visibility = sepEta.Visibility = hasEta ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                if (hasEta)
                {
                    double remainingMWh = info.FullCapacityMWh * (100 - info.ChargePercent) / 100.0;
                    double hours = remainingMWh / (info.ChargePowerW * 1000.0);
                    if (hours > 0 && hours < 24)
                    {
                        int hh = (int)hours;
                        int mm = (int)((hours - hh) * 60);
                        etaTb.Text = hh > 0 ? $"~{hh}h {mm:D2}m" : $"~{mm}m";
                    }
                    else etaTb.Text = "—";
                }
            }

            TxtBatVoltage.Text = info.VoltageV > 0 ? $"{info.VoltageV:F2} V" : "—";

            bool hasRuntime = info.EstimatedRuntime > 0 && info.EstimatedRuntime < 999;
            if (FindName("PanelBatTimeRemaining") is System.Windows.FrameworkElement panelTime)
                panelTime.Visibility = hasRuntime ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (hasRuntime)
                TxtBatRuntime.Text = info.RuntimeText;

            // The state dot: red / orange when there is a problem, accent while charging, otherwise the level colour
            if (FindName("BatStatusDot") is System.Windows.Shapes.Ellipse statusDot)
                statusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
                    assessment.Severity >= 2 ? "StatusCritBrush"
                    : assessment.Severity == 1 ? "StatusWarnBrush"
                    : info.IsCharging ? "AccentBrush"
                    : BatteryBlocks.ZoneOf(info.ChargePercent) switch
                    {
                        BatteryZone.Crit => "StatusCritBrush",
                        BatteryZone.Warn => "StatusWarnBrush",
                        _                => "StatusOkBrush",
                    });

            if (FindName("PanelBatCapacityNow") is System.Windows.FrameworkElement colCapNow
                && FindName("TxtBatCapacityNow") is System.Windows.Controls.TextBlock capNowText)
            {
                bool hasCap = info.FullCapacityMWh > 0;
                colCapNow.Visibility = hasCap ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                if (hasCap)
                {
                    double capNowWh = (info.FullCapacityMWh / 1000.0) * info.ChargePercent / 100.0;
                    capNowText.Text = $"{capNowWh:F1} Wh";
                }
            }

            if (FindName("PanelBatChemistry") is System.Windows.FrameworkElement panelChem
                && FindName("TxtBatChemistry") is System.Windows.Controls.TextBlock txtChem
                && FindName("SepBatChemistry") is System.Windows.FrameworkElement sepChem)
            {
                bool hasChem = !string.IsNullOrWhiteSpace(info.Chemistry);
                panelChem.Visibility = sepChem.Visibility = hasChem ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                if (hasChem) txtChem.Text = info.Chemistry;
            }
            if (FindName("PanelBatManufacturer") is System.Windows.FrameworkElement panelMfr
                && FindName("TxtBatManufacturer") is System.Windows.Controls.TextBlock txtMfr
                && FindName("SepBatManufacturer") is System.Windows.FrameworkElement sepMfr)
            {
                bool hasMfr = !string.IsNullOrWhiteSpace(info.Manufacturer);
                panelMfr.Visibility = sepMfr.Visibility = hasMfr ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                if (hasMfr) txtMfr.Text = info.Manufacturer;
            }
            if (FindName("PanelBatMfgDate") is System.Windows.FrameworkElement panelDate
                && FindName("TxtBatMfgDate") is System.Windows.Controls.TextBlock txtDate
                && FindName("SepBatMfgDate") is System.Windows.FrameworkElement sepDate)
            {
                bool hasDate = !string.IsNullOrWhiteSpace(info.ManufactureDate);
                panelDate.Visibility = sepDate.Visibility = hasDate ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                if (hasDate) txtDate.Text = info.ManufactureDate;
            }

            bool hasWear = info.WearPct.HasValue;
            int healthPct = hasWear ? Math.Max(0, 100 - info.WearPct!.Value) : 0;
            var healthBrush = healthPct >= 80 ? _brGreen : healthPct >= 60 ? _brOrange : _brRed;
            var healthColor = healthPct >= 80 ? WpfColor.FromRgb( 34, 197,  94)
                            : healthPct >= 60 ? WpfColor.FromRgb(245, 158,  11)
                            :                   WpfColor.FromRgb(239,  68,  68);
            TxtBatWear.Text       = hasWear ? $"{healthPct}%" : "—";
            TxtBatWear.Foreground = healthBrush;

            if (FindName("BatHealthRing") is System.Windows.Shapes.Ellipse ring)
            {
                const double diameter = 84;   // Width=84 in XAML
                const double thickness = 9;   // the real StrokeThickness from XAML
                var calc = SMDWin.Services.RingProgressCalculator.Calculate(diameter, thickness, healthPct);
                ring.StrokeDashArray = new System.Windows.Media.DoubleCollection { calc.DashUnits, calc.DashUnits };
                ring.Stroke = healthBrush;
                AnimateD(ring, System.Windows.Shapes.Shape.StrokeDashOffsetProperty, calc.OffsetUnits, 0.7,
                    new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut });
            }

            bool hasCycles = info.CycleCount > 0;
            TxtBatCycles.Text = hasCycles ? $"{info.CycleCount}" : "";
            if (FindName("RowBatCycles") is System.Windows.FrameworkElement rowCycles)
                rowCycles.Visibility = hasCycles ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (FindName("SepBatCycles") is System.Windows.FrameworkElement sepCycles)
                sepCycles.Visibility = hasCycles ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            TxtBatCapacity.Text = info.CapacityText;
            UpdateBatteryCardExtras(info, liveW, assessment.HealthTrusted); // estimari, grafic consum, cadran sanatate (MainWindow.BatteryCard.cs)

            // The battery drawing: from blocks (default), the classic outline or one of the old variants
            string glyphStyle = SettingsService.Current.BatteryGlyphStyle ?? BatteryBlocks.Default;
            bool isBlocks = BatteryBlocks.IsBlockStyle(glyphStyle);
            bool isClassic = glyphStyle == "Classic";
            if (FindName("BatBlocks") is BatteryPicture blocks)
            {
                blocks.Visibility = isBlocks ? Visibility.Visible : Visibility.Collapsed;
                if (isBlocks)
                {
                    blocks.Variant = glyphStyle;
                    blocks.HasReading = true;
                    blocks.Percent = info.ChargePercent;
                    blocks.Charging = info.IsCharging;
                }
            }
            if (FindName("BatGlyphClassicHost") is System.Windows.FrameworkElement classicHost)
                classicHost.Visibility = isClassic ? Visibility.Visible : Visibility.Collapsed;
            if (FindName("BatGlyphAltHost") is System.Windows.Controls.Canvas altHost)
            {
                altHost.Visibility = isClassic || isBlocks ? Visibility.Collapsed : Visibility.Visible;
                if (!isClassic && !isBlocks)
                {
                    altHost.Children.Clear();
                    altHost.Children.Add(SMDWin.Services.BatteryGlyphLibrary.Build(
                        glyphStyle, info.ChargePercent, chargeColor, info.IsCharging));
                }
            }

            // The battery glyph fill — animated on Width (0.6 s, EaseOut)
            if (isClassic && FindName("BatGlyphFill") is System.Windows.Controls.Border glyphFill)
            {
                double fullW = 104;
                double targetW = Math.Max(2, fullW * Math.Min(100, info.ChargePercent) / 100.0);
                AnimateD(glyphFill, System.Windows.FrameworkElement.WidthProperty, targetW, 0.6,
                    new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut });
                glyphFill.Background = new System.Windows.Media.LinearGradientBrush(
                    WpfColor.FromArgb(255, chargeColor.R, chargeColor.G, chargeColor.B),
                    Darken(chargeColor, 0.28), 90);
            }

            // The "charging" sweep — started only while the battery really charges and only on the classic outline (the other
            // variants have their own sign); it stops when the variant changes, it does not keep running hidden.
            SetBatterySweepAnimating(isClassic && info.IsCharging);

            if (isClassic && FindName("BatChargingBoltIcon") is System.Windows.FrameworkElement boltIcon)
                boltIcon.Visibility = info.IsCharging ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

            if (isClassic && FindName("BatFullPluggedIcon") is System.Windows.FrameworkElement fullIcon)
            {
                bool isFullyPlugged = info.IsAC && !info.IsCharging && (info.IsFull || info.ChargePercent >= 100);
                fullIcon.Visibility = isFullyPlugged ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            }

            // The state dot — a continuous pulse only while the page is visible and the battery has an "active" state (not a static
            // Full/AC). Started unconditionally here — the cost of an opacity animation on an 8 px Ellipse is negligible.
            if (FindName("BatStatusDot") is System.Windows.Shapes.Ellipse pulseDot)
                AnimateD(pulseDot, System.Windows.UIElement.OpacityProperty, 1.0, 0.9, null, from: 0.35, autoReverseForever: true);
        }


        private static WpfColor Darken(WpfColor c, double amount)
        {
            byte D(byte v) => (byte)Math.Max(0, v * (1 - amount));
            return WpfColor.FromRgb(D(c.R), D(c.G), D(c.B));
        }

        private static void AnimateD(System.Windows.Media.Animation.IAnimatable target, DependencyProperty prop,
            double to, double durationSec, System.Windows.Media.Animation.IEasingFunction? ease = null,
            double? from = null, bool autoReverseForever = false)
        {
            bool animOn = SMDWin.Services.SettingsService.Current.EnableAnimations;
            if (!animOn)
            {
                target.BeginAnimation(prop, null);
                (target as DependencyObject)?.SetValue(prop, to);
                return;
            }
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                To = to,
                From = from,
                Duration = TimeSpan.FromSeconds(durationSec),
                EasingFunction = ease,
            };
            if (autoReverseForever)
            {
                anim.AutoReverse = true;
                anim.RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever;
            }
            target.BeginAnimation(prop, anim);
        }

        /// <summary>The "charging" sweep over the battery glyph fill — started/stopped directly, never left running when the
        /// battery is not actually charging.</summary>
        private void SetBatterySweepAnimating(bool on)
        {
            if (FindName("BatSweepOverlay") is not System.Windows.FrameworkElement overlay) return;
            overlay.Visibility = on ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (!on)
            {
                (overlay.RenderTransform as System.Windows.Media.TranslateTransform)
                    ?.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
                return;
            }
            if (overlay.RenderTransform is not System.Windows.Media.TranslateTransform tt) return;
            if (!SMDWin.Services.SettingsService.Current.EnableAnimations) return;
            var anim = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = -60, To = 300,
                Duration = TimeSpan.FromSeconds(2.8),
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            };
            tt.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, anim);
        }

        private void PushBatterySparkline(float? drawW)
        {
            if (FindName("PanelBatSparkline") is not System.Windows.FrameworkElement col) return;
            var sepVis = drawW.HasValue ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            if (FindName("SepBatSparkline") is System.Windows.FrameworkElement sep) sep.Visibility = sepVis;
            if (!drawW.HasValue)
            {
                col.Visibility = System.Windows.Visibility.Collapsed;
                return;
            }
            col.Visibility = System.Windows.Visibility.Visible;
            _batDrawSparkline.Push(Math.Abs(drawW.Value));
            RedrawBatterySparkline();
        }

        private void RedrawBatterySparkline()
        {
            if (FindName("BatDrawSparkline") is not System.Windows.Shapes.Polyline poly) return;
            if (FindName("BatDrawSparklineDot") is not System.Windows.Shapes.Ellipse dot) return;
            if (_batDrawSparkline.Count < 2) { poly.Points.Clear(); dot.Visibility = System.Windows.Visibility.Collapsed; return; }

            var vals = _batDrawSparkline.ToArray(); // chronological, oldest first
            float min = float.MaxValue, max = float.MinValue;
            foreach (var v in vals) { if (float.IsNaN(v)) continue; if (v < min) min = v; if (v > max) max = v; }
            if (min > max) { poly.Points.Clear(); dot.Visibility = System.Windows.Visibility.Collapsed; return; }
            float range = Math.Max(0.1f, max - min);

            const double W = 200, H = 38, pad = 3;
            var pts = new System.Windows.Media.PointCollection();
            int n = vals.Length;
            for (int i = 0; i < n; i++)
            {
                if (float.IsNaN(vals[i])) continue;
                double x = n <= 1 ? 0 : i * (W / (n - 1));
                double norm = (vals[i] - min) / range; // 0..1
                double y = pad + (1 - norm) * (H - 2 * pad);
                pts.Add(new System.Windows.Point(x, y));
            }
            poly.Points = pts;
            if (pts.Count > 0)
            {
                var last = pts[pts.Count - 1];
                System.Windows.Controls.Canvas.SetLeft(dot, last.X - 3.4);
                System.Windows.Controls.Canvas.SetTop(dot, last.Y - 3.4);
                dot.Visibility = System.Windows.Visibility.Visible;
            }
        }

        private static readonly Dictionary<string, string> _powerPlanNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["a1841308-3541-4fab-bc81-f71556f20b4a"] = "Power Saver",
            ["381b4222-f694-41f0-9685-ff5bb260df2e"] = "Balanced",
            ["8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"] = "High Performance",
        };

        private SMDWin.Models.BatteryInfo? _lastBatteryInfo;

        /// <summary>Redraws the battery card with the latest reading — after the drawing variant changes in Developer › Design
        /// experiments, without waiting for the next read.</summary>
        internal void ApplyBatteryGlyphStyle()
        {
            try { if (_lastBatteryInfo is { Present: true } info) UpdateBatteryUI(info); }
            catch (Exception ex) { AppLogger.Debug($"ApplyBatteryGlyphStyle: {ex.Message}"); }
        }

        /// <summary>Reads the active power plan and updates the card on the Power page: the plan name + its highlighted button.
        /// The read goes through the Windows API (instant); only if the API does not answer is powercfg used, in the background,
        /// so the page does not wait for a process.</summary>
        private void LoadActivePowerPlan()
        {
            string? activeGuid = PowerAdvancedService.ActiveSchemeGuid();
            if (activeGuid != null) { ShowActivePowerPlan(activeGuid); return; }
            _ = LoadActivePowerPlanSlowAsync();
        }

        private async Task LoadActivePowerPlanSlowAsync()
        {
            try { ShowActivePowerPlan(await Task.Run(() => PowerAdvancedService.ActiveSchemeGuidFromPowercfg())); }
            catch (Exception ex) { AppLogger.Warning(ex, "PowerPlan"); }
        }

        private void ShowActivePowerPlan(string? activeGuid)
        {
            string planName = activeGuid != null && _powerPlanNames.TryGetValue(activeGuid, out var n)
                ? n
                : "Unknown";

            if (TxtActivePowerPlan != null) TxtActivePowerPlan.Text = PT(planName);
            if (TxtDashPowerPlan != null) TxtDashPowerPlan.Text = planName == "Unknown" ? "" : PT(planName);
            // The animation on the Power page (desktop) shows the plan name + its settings → refresh
            if (ChkPowerFlowAnimation?.IsChecked == true) _ = RefreshPowerFlowInfoAsync();

            MarkPowerPlanCards(activeGuid?.ToLowerInvariant());   // the active plan card with an accent border
            RenderPlanCard();                                     // the plan dial (MainWindow.PowerRedesign.cs)

            _ = LoadUpsInfoAsync(); // interogarea WMI ruleaza in fundal
        }

        private bool _upsInfoBusy;

        /// <summary>The UPS card: appears only if Windows reports a UPS. The WMI query may take a while, so it runs in the
        /// background; on systems without that class it is tried only once per session.</summary>
        private async Task LoadUpsInfoAsync()
        {
            if (_upsClassAvailable == false)
            {
                if (FindName("UpsCard") is Border upsCardCached)
                    upsCardCached.Visibility = Visibility.Collapsed;
                return;
            }
            if (_upsInfoBusy) return;
            _upsInfoBusy = true;
            try
            {
                var ups = await Task.Run(() =>
                {
                    string? name = null;
                    int? chargePct = null, runtimeMin = null;
                    bool? classAvailable = null;
                    try
                    {
                        using var searcher = SMDWin.Services.WmiHelper.Searcher(
                            "SELECT * FROM Win32_UninterruptiblePowerSupply", System.TimeSpan.FromSeconds(10));
                        using var found = searcher.Get();
                        foreach (System.Management.ManagementObject mo in found)
                        {
                            name = mo["Name"]?.ToString();
                            if (mo["EstimatedChargeRemaining"] is ushort c) chargePct = c;
                            if (mo["EstimatedRunTime"] is ushort r && r < ushort.MaxValue) runtimeMin = r;
                            break; // the first UPS found
                        }
                        classAvailable = true; // the query worked (even if it found no UPS)
                    }
                    catch (System.Management.ManagementException mex)
                        when (mex.ErrorCode == System.Management.ManagementStatus.InvalidClass)
                    {
                        // The class does not exist on this system — permanent, not tried again
                        classAvailable = false;
                        AppLogger.Info("UpsInfo: Win32_UninterruptiblePowerSupply class not available on this system — UPS card hidden for the rest of the session.");
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "UpsInfo"); }
                    return (name, chargePct, runtimeMin, classAvailable);
                });

                if (ups.classAvailable.HasValue) _upsClassAvailable = ups.classAvailable;
                bool hasUps = !string.IsNullOrWhiteSpace(ups.name);
                if (FindName("UpsCard") is Border upsCard)
                    upsCard.Visibility = hasUps ? Visibility.Visible : Visibility.Collapsed;
                if (!hasUps) return;

                if (TxtUpsName != null) TxtUpsName.Text = ups.name;
                if (TxtUpsCharge != null) TxtUpsCharge.Text = ups.chargePct.HasValue ? $"{ups.chargePct}%" : "—";
                if (TxtUpsRuntime != null) TxtUpsRuntime.Text = ups.runtimeMin.HasValue ? $"{ups.runtimeMin} min" : "—";
            }
            catch (Exception ex) { AppLogger.Warning(ex, "UpsInfo"); }
            finally { _upsInfoBusy = false; }
        }

        /// <summary>Switches the active power plan to the GUID in the clicked button's Tag (powercfg /setactive), then reloads
        /// the displayed state.</summary>
        internal void SetPowerPlan_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string guid) return;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("powercfg", $"/setactive {guid}")
                { UseShellExecute = false, CreateNoWindow = true };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(2000);
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "PowerPlan");
                ShowToastWarning(LanguageService.S("Msg", "PowerPlanChangeFail"));
                return;
            }
            LoadActivePowerPlan();
        }
    }
}
