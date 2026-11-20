using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SMDWin.Services;
using WpfPoint = System.Windows.Point;
using WpfBrush = System.Windows.Media.Brush;
using Path = System.Windows.Shapes.Path;

namespace SMDWin.Views
{
    /// <summary>
    /// The battery card on the Power page:
    ///   • left: the charge (the only place it appears);
    ///   • middle: the battery name + details on one row, 3 runtime estimates, the 10-minute power chart;
    ///   • right: health (a block dial) + maximum / design capacity and cycles.
    /// Rule: what cannot be read/computed is not shown.
    /// The battery state is given in one word under the percentage and, when there is something to explain (not charging,
    /// charge limit, almost empty), in a sentence at the top of the middle column — evaluation: Services/BatteryCondition.cs.
    /// </summary>
    public partial class MainWindow
    {
        private readonly List<(DateTime T, double W)> _batDrawHist = new();

        // light background sampling (every 15 s) — the chart is not empty when the Battery page opens.
        // Stops for good if there is no battery (desktop). While the Battery page is open, its own timer feeds it.
        private DispatcherTimer? _batSampler;
        private bool _batSamplerBusy;

        private void StartBatteryPowerSampler()
        {
            if (_batSampler != null) return;
            _batSampler = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _batSampler.Tick += async (_, _) =>
            {
                if (_batSamplerBusy || _batTimer.IsEnabled) return;   // page open: UpdateBatteryCardExtras already adds the points
                _batSamplerBusy = true;
                try
                {
                    var info = await _batterySvc.GetBatteryInfoAsync();
                    if (!info.Present) { _batSampler?.Stop(); return; }
                    double w = info.IsCharging ? info.ChargePowerW : info.DischargePowerW;
                    if (w > 0.05)
                    {
                        _batDrawHist.Add((DateTime.Now, w));
                        var cut = DateTime.Now.AddMinutes(-10);
                        _batDrawHist.RemoveAll(p => p.T < cut);
                    }
                }
                catch (Exception ex) { AppLogger.Debug($"BatSampler: {ex.Message}"); }
                finally { _batSamplerBusy = false; }
            };
            _batSampler.Start();
        }
        private double _batLastDischargeW;   // the last draw on battery (for estimates while plugged in)

        private static string FmtHours(double hours)
        {
            if (!(hours > 0) || hours > 48) return "";
            int h = (int)hours, m = (int)Math.Round((hours - h) * 60);
            if (m == 60) { h++; m = 0; }
            return h > 0 ? $"{h}h {m:D2}m" : $"{m}m";
        }

        private void SetEstTile(int i, string? label, string? value, string? sub, string valueBrush = "TextPrimaryBrush")
        {
            if (FindName($"BatEstTile{i}") is not Border tile) return;
            bool show = !string.IsNullOrEmpty(value);
            tile.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;
            if (FindName($"TxtBatEst{i}Label") is TextBlock l) l.Text = label ?? "";
            if (FindName($"TxtBatEst{i}Value") is TextBlock v)
            {
                v.Text = value!;
                v.SetResourceReference(TextBlock.ForegroundProperty, valueBrush);
            }
            if (FindName($"TxtBatEst{i}Sub") is TextBlock s) s.Text = sub ?? "";
        }

        private DateTime? _batStuckSince;   // since when it is "plugged in, almost empty, not charging"

        /// <summary>Evaluates the battery state. "Not charging" is confirmed only after a few seconds in the same state (or
        /// immediately if the voltage is abnormally low too): right after the charger is connected, an empty battery may briefly
        /// show as not charging.</summary>
        private BatteryAssessment AssessBattery(SMDWin.Models.BatteryInfo info)
        {
            bool stuck = info.IsAC && !info.IsCharging && !info.IsFull && info.ChargePercent <= BatteryCondition.StuckBelowPercent;
            if (!stuck) _batStuckSince = null;
            else _batStuckSince ??= DateTime.Now;
            bool confirmed = _batStuckSince.HasValue && (DateTime.Now - _batStuckSince.Value).TotalSeconds >= 4;
            return BatteryCondition.Assess(info.ChargePercent, info.IsAC, info.IsCharging, info.IsFull,
                                           info.VoltageV, info.DesignVoltageV, confirmed);
        }

        /// <summary>The state under the percentage + the explanatory sentence. The sentence appears only when it adds something
        /// to the state; it has a coloured background only when there is a problem.</summary>
        private void ShowBatteryVerdict(SMDWin.Models.BatteryInfo info, BatteryAssessment a)
        {
            string B(string key, string english) => LanguageService.S("Battery", key, english);

            TxtBatStatus.Text = a.Verdict switch
            {
                BatteryVerdict.Charging     => B("VerdictCharging", "Charging"),
                BatteryVerdict.Full         => B("VerdictFull", "Fully charged"),
                BatteryVerdict.NotCharging  => B("VerdictNotCharging", "Not charging"),
                BatteryVerdict.OnBatteryLow => B("VerdictOnBatteryLow", "Battery low"),
                BatteryVerdict.OnBattery    => B("VerdictOnBattery", "On battery"),
                _                           => B("VerdictPluggedIdle", "Plugged in, not charging"),
            };

            string detail = "";
            switch (a.Verdict)
            {
                case BatteryVerdict.NotCharging:
                    detail = string.Format(B("DetailNotCharging", "The battery shows {0}% while plugged in and takes no charge."), info.ChargePercent) + " "
                           + (a.VoltageLow
                                ? string.Format(B("DetailVoltageLow", "Its voltage is {0:F1} V, far below the normal {1:F1} V — the battery is deeply discharged or faulty."),
                                                info.VoltageV, info.DesignVoltageV)
                                : B("DetailCheckCharger", "Check the charger and its connector first; if they are fine, the battery is most likely faulty."));
                    break;
                // (almost empty and not yet confirmed as "not charging": no explanation until it is clear)
                case BatteryVerdict.PluggedIdle when info.ChargePercent > BatteryCondition.StuckBelowPercent:
                    detail = B("DetailPluggedIdle", "Normal when a charge limit is set: the laptop stops charging before 100% to protect the battery.");
                    break;
                case BatteryVerdict.OnBatteryLow:
                    detail = B("DetailOnBatteryLow", "Plug in the charger.");
                    break;
                case BatteryVerdict.Full:
                    detail = B("DetailFull", "Plugged in and full: the laptop runs from the charger and the battery is resting.");
                    break;
            }

            if (FindName("BatVerdictBox") is not Border box || FindName("TxtBatVerdict") is not TextBlock text) return;
            box.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            text.Text = detail;
            if (a.Severity >= 1)
            {
                box.SetResourceReference(Border.BackgroundProperty, a.Severity >= 2 ? "StatusCritBgBrush" : "StatusWarnBgBrush");
                box.Padding = new Thickness(12, 9, 12, 9);
                text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            }
            else
            {
                box.ClearValue(Border.BackgroundProperty);
                box.Padding = new Thickness(0);
                text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            }
        }

        private void UpdateBatteryCardExtras(SMDWin.Models.BatteryInfo info, double? liveW, bool healthTrusted = true)
        {
            try
            {
                bool ro = LanguageService.CurrentCode == "ro";

                // ── Numele bateriei, in card ──
                if (FindName("TxtBatCardName") is TextBlock nameTb)
                {
                    // "Primary" is the generic name Windows gives the only battery: just "Battery" then
                    string bn = (info.Name ?? "").Trim();
                    bool generic = bn.Length == 0 || bn.Equals("Primary", StringComparison.OrdinalIgnoreCase);
                    bool has = !string.IsNullOrWhiteSpace(info.Name);
                    nameTb.Text = !has ? "" : generic ? PT("Battery") : PT("Battery") + " " + bn;
                    nameTb.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
                }

                // ── Health: a block dial (as in the rest of the app) ──
                // A battery that receives no power: the health figure comes from its counter and means nothing → the dial stays empty,
                // and the reason appears under it. The reported capacity stays visible.
                if (FindName("TxtBatHealthNote") is TextBlock healthNote)
                {
                    bool show = !healthTrusted && info.WearPct.HasValue;
                    healthNote.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    if (show)
                        healthNote.Text = LanguageService.S("Battery", "HealthNotConfirmed",
                            "Not confirmed: this figure is reported by the battery itself, which is not taking charge now.");
                }
                if (FindName("BatHealthGauge") is PercentGauge g)
                {
                    bool hasWear = info.WearPct.HasValue && healthTrusted;
                    int health = hasWear ? Math.Max(0, 100 - info.WearPct!.Value) : 0;
                    g.HasValue = hasWear;
                    g.NormalBrush = (TryFindResource(health >= 80 ? "StatusOkBrush" : health >= 60 ? "StatusWarnBrush" : "StatusCritBrush") as WpfBrush);
                    g.Value = health;
                    g.InvalidateVisual();
                }
                if (FindName("TxtBatHealthFoot") is TextBlock hf)
                {
                    bool hasWear2 = info.WearPct.HasValue;
                    int health2 = hasWear2 ? Math.Max(0, 100 - info.WearPct!.Value) : 0;
                    hf.Text = hasWear2 ? $"{health2}%" : "—";
                    hf.SetResourceReference(TextBlock.ForegroundProperty, !hasWear2 ? "TextPrimaryBrush"
                        : health2 >= 80 ? "StatusOkBrush" : health2 >= 60 ? "StatusWarnBrush" : "StatusCritBrush");
                }
                if (FindName("TxtBatCapacity") is TextBlock capTb && FindName("TxtCapacityRowLabel") is TextBlock capLbl)
                {
                    bool hasCap = info.FullCapacityMWh > 0 && info.DesignCapacityMWh > 0;
                    capTb.Visibility = capLbl.Visibility = hasCap ? Visibility.Visible : Visibility.Collapsed;
                }

                // ── Estimari ──
                double fullWh = info.FullCapacityMWh / 1000.0, designWh = info.DesignCapacityMWh / 1000.0;
                if (!info.IsCharging && info.DischargePowerW > 0.5) _batLastDischargeW = info.DischargePowerW;
                double drawW = !info.IsCharging && info.DischargePowerW > 0.5 ? info.DischargePowerW : _batLastDischargeW;

                // 1. now: on battery → time left; while charging → until full
                bool hasRuntime = info.EstimatedRuntime > 0 && info.EstimatedRuntime < 999 && !info.IsAC;
                bool chargingNow = info.IsCharging && info.ChargePowerW > 0 && fullWh > 0 && info.ChargePercent < 100;
                if (hasRuntime)
                    SetEstTile(1, ro ? "Rămas acum" : "Left now", info.RuntimeText, ro ? "la consumul actual" : "at the current draw");
                else if (chargingNow)
                    SetEstTile(1, ro ? "Până la plin" : "Until full",
                        FmtHours(fullWh * (100 - info.ChargePercent) / 100.0 / info.ChargePowerW),
                        ro ? $"de la {info.ChargePercent}% la 100%" : $"from {info.ChargePercent}% to 100%", "StatusOkBrush");
                else SetEstTile(1, null, null, null);

                string full = drawW > 0.5 && fullWh > 0 ? FmtHours(fullWh / drawW) : "";
                string neu = drawW > 0.5 && designWh > 0 && designWh > fullWh ? FmtHours(designWh / drawW) : "";
                string diff = neu.Length > 0 ? FmtHours(designWh / drawW - fullWh / drawW) : "";
                if (chargingNow)
                {
                    // while charging: the charging power has its own card (average and peak are on the chart), then the runtime at full
                    SetEstTile(2, ro ? "Se încarcă cu" : "Charging at", $"{info.ChargePowerW:F1} W",
                        ro ? "puterea primită acum de baterie" : "power going into the battery now");
                    SetEstTile(3, ro ? "La încărcare completă" : "On a full charge", full.Length > 0 ? "~" + full : "",
                        ro ? $"{fullWh:F0} Wh azi · {drawW:F1} W" : $"{fullWh:F0} Wh today · {drawW:F1} W");
                }
                else
                {
                    // 2. at full charge (today's capacity)
                    SetEstTile(2, ro ? "La încărcare completă" : "On a full charge", full.Length > 0 ? "~" + full : "",
                        ro ? $"{fullWh:F0} Wh azi · {drawW:F1} W" : $"{fullWh:F0} Wh today · {drawW:F1} W");
                    // 3. when it was new (design capacity), with the difference
                    SetEstTile(3, ro ? "Când era nouă" : "When it was new", neu.Length > 0 ? "~" + neu : "",
                        ro ? $"{designWh:F0} Wh · cu {diff} mai mult" : $"{designWh:F0} Wh · {diff} more", "TextSecondaryBrush");
                }

                // ── Power history (the last 10 minutes) ──
                if (liveW.HasValue && Math.Abs(liveW.Value) > 0.05)
                {
                    _batDrawHist.Add((DateTime.Now, Math.Abs(liveW.Value)));
                    var cut = DateTime.Now.AddMinutes(-10);
                    _batDrawHist.RemoveAll(p => p.T < cut);
                }
                if (FindName("TxtBatDrawTitle") is TextBlock title)
                    title.Text = info.IsCharging ? (ro ? "Putere de încărcare" : "Charging power")
                                                 : (ro ? "Consum" : "Power draw");
                if (FindName("TxtBatDrawAxisLeft") is TextBlock axisLeft) axisLeft.Text = ro ? "acum 10 min" : "10 min ago";
                if (FindName("TxtBatDrawAxisRight") is TextBlock axisRight) axisRight.Text = ro ? "acum" : "now";
                DrawBatDrawChart();
            }
            catch (Exception ex) { AppLogger.Debug($"UpdateBatteryCardExtras: {ex.Message}"); }
        }

        internal void BatDrawChart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawBatDrawChart();

        private void DrawBatDrawChart()
        {
            if (FindName("BatDrawChart") is not Canvas c || FindName("BatDrawPanel") is not FrameworkElement panel) return;
            if (_batDrawHist.Count < 2) { panel.Visibility = Visibility.Collapsed; return; }
            panel.Visibility = Visibility.Visible;
            c.Children.Clear();
            double w = c.ActualWidth, h = c.ActualHeight;
            if (w < 10 || h < 10) return;

            double now = _batDrawHist[^1].W, peak = _batDrawHist.Max(p => p.W), avg = _batDrawHist.Average(p => p.W);
            bool ro = LanguageService.CurrentCode == "ro";
            if (FindName("TxtBatDrawStats") is TextBlock st)
                st.Text = ro ? $"acum {now:F1} W  ·  vârf {peak:F1} W  ·  medie {avg:F1} W"
                             : $"now {now:F1} W  ·  peak {peak:F1} W  ·  average {avg:F1} W";

            // fixed time axis: 10 minutes, new data enters from the right (like the other charts)
            double max = Math.Max(5, Math.Ceiling(peak * 1.2 / 5) * 5);
            var end = DateTime.Now; var start = end.AddMinutes(-10);
            ChartGrid.AddTo(c, new Rect(0, 0, w, h), 4);   // the shared chart grid
            var pts = _batDrawHist.Select(p => new WpfPoint(
                Math.Clamp((p.T - start).TotalSeconds / 600.0, 0, 1) * (w - 4),
                h - 2 - Math.Clamp(p.W / max, 0, 1) * (h - 4))).ToList();

            var fill = new StreamGeometry();
            using (var ctx = fill.Open())
            {
                ctx.BeginFigure(new WpfPoint(pts[0].X, h), true, true);
                foreach (var p in pts) ctx.LineTo(p, true, false);
                ctx.LineTo(new WpfPoint(pts[^1].X, h), true, false);
            }
            fill.Freeze();
            var area = new Path { Data = fill, Opacity = 0.14 };
            area.SetResourceReference(Shape.FillProperty, "AccentBrush");
            c.Children.Add(area);

            var line = new Polyline { StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round, Points = new PointCollection(pts) };
            line.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            c.Children.Add(line);

            var dot = new Ellipse { Width = 6, Height = 6 };
            dot.SetResourceReference(Shape.FillProperty, "AccentBrush");
            Canvas.SetLeft(dot, pts[^1].X - 3); Canvas.SetTop(dot, pts[^1].Y - 3);
            c.Children.Add(dot);
        }
    }
}
