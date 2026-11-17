using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;
using Border      = System.Windows.Controls.Border;
using TextBlock   = System.Windows.Controls.TextBlock;
using Path        = System.Windows.Shapes.Path;
using Ellipse     = System.Windows.Shapes.Ellipse;
using Rectangle   = System.Windows.Shapes.Rectangle;
using HAlign      = System.Windows.HorizontalAlignment;
using VAlign      = System.Windows.VerticalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Brushes     = System.Windows.Media.Brushes;
using Control     = System.Windows.Controls.Control;

namespace SMDWin.Views
{
    /// <summary>
    /// The Power page:
    ///  • the three plans as three cards with an image (Power saver · Balanced · Performance);
    ///  • on a PC (no battery) the left card shows "What the plan does now" — a short list with icons; the power map appears
    ///    only from Developer › Design experiments ("Power page: old power map");
    ///  • in "Sleep &amp; wake", a thin timeline: last input → screen off → sleep.
    /// The data comes from the same read as the map (RefreshPowerFlowInfoAsync).
    /// </summary>
    public partial class MainWindow
    {
        private static string PW(string key, string en) => LanguageService.S("Power", key, en);

        // ── The plan cards ──────────────────────────────────────────────

        private static readonly (string Name, string Icon)[] PlanIcons =
        {
            ("BtnPlanPowerSaver", "leaf_one"),   // Fluent icon names (Services/FluentIcons.cs)
            ("BtnPlanBalanced",   "scales"),
            ("BtnPlanHighPerf",   "rocket"),
        };

        private string? _activePlanGuid;

        /// <summary>The card content (icon, name, small line). Called on load and on a language change.</summary>
        internal void BuildPowerPlanCards()
        {
            try
            {
                foreach (var (name, icon) in PlanIcons)
                {
                    if (FindName(name) is not Wpf.Ui.Controls.Button b) continue;
                    var (title, sub) = name switch
                    {
                        "BtnPlanPowerSaver" => (PW("PowerSaver", "Power Saver"), PW("PlanSaverSub", "Less heat and noise")),
                        "BtnPlanHighPerf"   => (PW("HighPerformance", "High Performance"), PW("PlanHighSub", "Full speed all the time")),
                        _                   => (PW("Balanced", "Balanced"), PW("PlanBalancedSub", "Speed when it is needed")),
                    };
                    var sp = new StackPanel();
                    var vb = new Viewbox { Width = 26, Height = 26, HorizontalAlignment = HAlign.Left, Margin = new Thickness(0, 0, 0, 8) };
                    var path = new Path { Data = SMDWin.Services.FluentIcons.Get(icon), Width = 24, Height = 24, Tag = "planIcon" };
                    vb.Child = new Canvas { Width = 24, Height = 24, Children = { path } };
                    sp.Children.Add(vb);
                    var t = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = Ui.FontM, TextTrimming = TextTrimming.CharacterEllipsis };
                    t.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                    sp.Children.Add(t);
                    var s = new TextBlock { Text = sub, FontSize = Ui.FontXS, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0), Tag = sub };
                    s.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    sp.Children.Add(s);
                    b.Content = sp;
                    b.ToolTip = title;
                }
                MarkPowerPlanCards(_activePlanGuid);
            }
            catch (Exception ex) { AppLogger.Debug($"BuildPowerPlanCards: {ex.Message}"); }
        }

        /// <summary>The active plan: accent outline (2 px), accent icon and "Active now" under the name.</summary>
        private void MarkPowerPlanCards(string? activeGuid)
        {
            _activePlanGuid = activeGuid;
            foreach (var (name, _) in PlanIcons)
            {
                if (FindName(name) is not Wpf.Ui.Controls.Button b) continue;
                if (b.Content is not StackPanel) { BuildPowerPlanCards(); return; }
                bool on = activeGuid != null && string.Equals(b.Tag as string, activeGuid, StringComparison.OrdinalIgnoreCase);
                b.BorderThickness = new Thickness(on ? 2 : 1);
                b.Padding = on ? new Thickness(9, 8, 9, 8) : new Thickness(10, 9, 10, 9);
                b.SetResourceReference(Control.BorderBrushProperty, on ? "AccentBrush" : "CardBorderBrush");
                b.SetResourceReference(Wpf.Ui.Controls.Button.MouseOverBorderBrushProperty, on ? "AccentBrush" : "CardBorderBrush");
                b.SetResourceReference(Wpf.Ui.Controls.Button.PressedBorderBrushProperty, "AccentBrush");
                b.SetResourceReference(Control.BackgroundProperty, "BgCardBrush");
                if (b.Content is StackPanel sp)
                {
                    foreach (var p in sp.Children.OfType<Viewbox>().Select(v => (v.Child as Canvas)?.Children.OfType<Path>().FirstOrDefault()))
                        p?.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, on ? "AccentBrush" : "TextSecondaryBrush");
                    if (sp.Children.OfType<TextBlock>().LastOrDefault() is { } sub)
                    {
                        sub.Text = on ? PW("PlanActiveNow", "Active now") : sub.Tag as string ?? "";
                        sub.SetResourceReference(TextBlock.ForegroundProperty, on ? "AccentBrush" : "TextSecondaryBrush");
                        sub.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                    }
                }
            }
        }

        // ── "What the plan does now" (PC without battery) ───────────────────────────

        private static readonly Dictionary<string, string> PlanNowIcons = new()
        {
            ["screen"] = "desktop",          // Fluent icon names (Services/FluentIcons.cs)
            ["sleep"]  = "weather_moon",
            ["cpu"]    = "developer_board",
            ["gpu"]    = "gpu",
            ["disk"]   = "hard_drive",
            ["usb"]    = "usb_plug",
        };

        /// <summary>The "What the plan does now" list: one row per component, icon · what · value. The row appears only if the
        /// value could be read.</summary>
        private void UpdatePowerPlanNow(int? pMin, int? pMax, PowerAdvancedService.BoostMode? boost, int? sleep, int? disp, int? disk, int? aspm, bool? usb)
        {
            try
            {
                if (FindName("TxtPowerNowTitle") is TextBlock title)
                    title.Text = SettingsService.Current.DesignExperimentPowerMap ? PW("PowerMapTitle", "Power map") : PW("PlanNowTitle", "What the plan does now");
                if (FindName("PowerPlanNowList") is not StackPanel host) return;
                host.Children.Clear();
                string never = PW("Never", "Never");
                string After(int? sec) => sec is > 0 ? string.Format(PW("AfterFmt", "after {0}"), PowerAdvancedService.FormatIdle(sec, never)) : never;

                var rows = new List<(string icon, string label, string value, string? tip)>();
                if (disp.HasValue) rows.Add(("screen", PW("NowScreen", "Screen turns off"), After(disp), null));
                if (sleep.HasValue) rows.Add(("sleep", PW("NowSleep", "Sleep"), After(sleep), PW("NowSleepTip", "Time without use before the PC goes to sleep.")));
                if (pMin.HasValue && pMax.HasValue)
                {
                    string turbo = boost is null ? "" : boost == PowerAdvancedService.BoostMode.Disabled
                        ? " · " + PW("NowTurboOff", "Turbo off") : " · " + PW("NowTurboOn", "Turbo on");
                    rows.Add(("cpu", PW("NowCpu", "Processor"), $"{pMin}–{pMax} %{turbo}", PW("NowCpuTip", "Lowest and highest speed the plan allows, as a share of the maximum.")));
                }
                if (aspm.HasValue)
                    rows.Add(("gpu", PW("NowPcie", "Graphics card link"), aspm switch
                    {
                        0 => PW("NowPcieOff", "No power saving"),
                        1 => PW("NowPcieModerate", "Moderate saving"),
                        _ => PW("NowPcieMax", "Maximum saving"),
                    }, PW("NowPcieTip", "Power saving on the PCI Express link of the graphics card and other cards.")));
                if (disk.HasValue) rows.Add(("disk", PW("NowDisk", "Hard disk turns off"), After(disk), PW("NowDiskTip", "Only hard disks stop spinning; SSDs are not affected.")));
                if (usb.HasValue) rows.Add(("usb", PW("NowUsb", "Idle USB devices"), usb.Value ? PW("NowUsbSuspended", "Suspended") : PW("NowUsbOn", "Kept on"), null));

                if (rows.Count == 0)
                {
                    host.Children.Add(Txt2(PW("NowUnreadable", "The plan settings could not be read."), "TextSecondaryBrush", Ui.FontS));
                    return;
                }
                for (int i = 0; i < rows.Count; i++)
                {
                    var (icon, label, value, tip) = rows[i];
                    var row = new DockPanel { Margin = new Thickness(0, 0, 0, 0), ToolTip = tip };
                    var line = new Border { Child = row, Padding = new Thickness(0, 9, 0, 9), BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0) };
                    line.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                    var vb = new Viewbox { Width = 18, Height = 18, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VAlign.Center };
                    var path = SMDWin.Services.FluentIcons.Make(PlanNowIcons[icon]);
                    vb.Child = new Canvas { Width = 24, Height = 24, Children = { path } };
                    DockPanel.SetDock(vb, Dock.Left);
                    row.Children.Add(vb);
                    var v = new TextBlock { Text = value, FontSize = Ui.FontM, FontWeight = FontWeights.SemiBold, VerticalAlignment = VAlign.Center, Margin = new Thickness(12, 0, 0, 0) };
                    v.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                    DockPanel.SetDock(v, Dock.Right);
                    row.Children.Add(v);
                    var l = Txt2(label, "TextSecondaryBrush", Ui.FontM);
                    l.VerticalAlignment = VAlign.Center;
                    row.Children.Add(l);
                    host.Children.Add(line);
                }
            }
            catch (Exception ex) { AppLogger.Debug($"UpdatePowerPlanNow: {ex.Message}"); }
        }

        /// <summary>Design experiment: the power map instead of the list (Developer › Design experiments). The animation runs only while visible.</summary>
        public void ApplyPowerMapExperiment()
        {
            try
            {
                bool map = SettingsService.Current.DesignExperimentPowerMap;
                if (FindName("PowerFlowBox") is FrameworkElement box) box.Visibility = map ? Visibility.Visible : Visibility.Collapsed;
                if (FindName("PowerPlanNowList") is FrameworkElement list) list.Visibility = map ? Visibility.Collapsed : Visibility.Visible;
                if (FindName("TxtPowerNowTitle") is TextBlock title)
                    title.Text = map ? PW("PowerMapTitle", "Power map") : LanguageService.S("PowerSimple", "IdleTitle", "When the PC is not used");
                if (PowerFlowWidget != null)
                {
                    if (map && ChkPowerFlowAnimation?.IsChecked == true) { PowerFlowWidget.Visibility = Visibility.Visible; PowerFlowWidget.Start(); }
                    else PowerFlowWidget.Stop();
                }
            }
            catch (Exception ex) { AppLogger.Debug($"ApplyPowerMapExperiment: {ex.Message}"); }
        }

        // ── The sleep timeline ──────────────────────────────────────────

        /// <summary>
        /// Last input → the screen turns off → the PC sleeps, on a thin line (2 px) with three small dots and labels below.
        /// The dots are placed in the order things happen; "never" always sits at the end.
        /// </summary>
        private void UpdateSleepTimeline(int? disp, int? sleep)
        {
            try
            {
                if (FindName("SleepTimeline") is not Grid g) return;
                g.Children.Clear(); g.ColumnDefinitions.Clear(); g.RowDefinitions.Clear();
                if (disp is null && sleep is null) { g.Visibility = Visibility.Collapsed; return; }
                g.Visibility = Visibility.Visible;

                string never = PW("Never", "Never");
                var steps = new List<(string label, int? sec, bool accent)>
                {
                    (PW("TlScreenOff", "Screen off"), disp, true),
                    (PW("TlSleep", "Sleep"), sleep, false),
                };
                // the real order in time; "never" (0 / not read) at the end
                steps = steps.OrderBy(s => s.sec is > 0 ? s.sec!.Value : int.MaxValue).ToList();

                foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
                g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                // dots and segments: the first segment (until the screen turns off) in accent, the rest in fine grey
                AddDot(0, filled: true, "AccentBrush");
                AddSegment(1, "AccentBrush", steps[0].sec is > 0);
                AddDot(2, filled: false, steps[0].sec is > 0 ? "AccentBrush" : "TextSecondaryBrush");
                AddSegment(3, "BorderBrush2", true);
                AddDot(4, filled: false, steps[1].sec is > 0 ? "AccentBrush" : "TextSecondaryBrush");

                AddLabel(0, 2, HAlign.Left, PW("TlLastUse", "Last use"), "");
                AddLabel(1, 3, HAlign.Center, steps[0].label, FmtStep(steps[0].sec));
                AddLabel(3, 2, HAlign.Right, steps[1].label, FmtStep(steps[1].sec));

                string FmtStep(int? sec) => sec is > 0 ? string.Format(PW("AfterFmt", "after {0}"), PowerAdvancedService.FormatIdle(sec, never)) : never.ToLowerInvariant();

                void AddDot(int col, bool filled, string brush)
                {
                    var e = new Ellipse { Width = 8, Height = 8, StrokeThickness = 1.5, VerticalAlignment = VAlign.Center };
                    e.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brush);
                    if (filled) e.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
                    else e.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "BgCardBrush");
                    Grid.SetColumn(e, col); Grid.SetRow(e, 0);
                    g.Children.Add(e);
                }
                void AddSegment(int col, string brush, bool solid)
                {
                    var r = new Rectangle { Height = 2, RadiusX = 1, RadiusY = 1, VerticalAlignment = VAlign.Center, Margin = new Thickness(3, 0, 3, 0), Opacity = solid ? 1 : 0.5 };
                    r.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
                    Grid.SetColumn(r, col); Grid.SetRow(r, 0);
                    g.Children.Add(r);
                }
                void AddLabel(int col, int span, HAlign align, string name, string value)
                {
                    var t = new TextBlock { FontSize = Ui.FontS, HorizontalAlignment = align, Margin = new Thickness(0, 5, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                    t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    t.Inlines.Add(new System.Windows.Documents.Run(name));
                    if (value.Length > 0)
                    {
                        var v = new System.Windows.Documents.Run(" · " + value) { FontWeight = FontWeights.SemiBold };
                        v.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextPrimaryBrush");
                        t.Inlines.Add(v);
                    }
                    Grid.SetColumn(t, col); Grid.SetColumnSpan(t, span); Grid.SetRow(t, 1);
                    g.Children.Add(t);
                }
            }
            catch (Exception ex) { AppLogger.Debug($"UpdateSleepTimeline: {ex.Message}"); }
        }
    }
}
