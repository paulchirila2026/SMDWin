using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using SMDWin.Services;
using Button      = System.Windows.Controls.Button;
using CheckBox    = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;
using HAlign      = System.Windows.HorizontalAlignment;
using VAlign      = System.Windows.VerticalAlignment;
using Brush       = System.Windows.Media.Brush;
using Ellipse     = System.Windows.Shapes.Ellipse;
using WpfPanel    = System.Windows.Controls.Panel;
using WpfPoint    = System.Windows.Point;
using WpfCursors  = System.Windows.Input.Cursors;
using Polyline    = System.Windows.Shapes.Polyline;
using Polygon     = System.Windows.Shapes.Polygon;
using RadioButton = System.Windows.Controls.RadioButton;

namespace SMDWin.Views
{
    /// <summary>
    /// The Power page, redesigned (mockup v3 approved 11.11.2026):
    ///  • a PC has one page: Power plan (block dial + needle, plans, processor) · When the PC is not used (one slider per time,
    ///    power button) · what keeps the PC awake (three lists side by side) · Start-up and shut-down; on a large screen also
    ///    Recent starts and stops, Power use now, and the advanced processor settings open in the plan card;
    ///  • a laptop has two tabs: Battery (charge ring, figures, health, battery saver, apps using the battery, power use) and
    ///    Power &amp; sleep (the PC page, with separate values for plugged in / on battery, the lid, and a plan per source).
    /// The cards are built here; the older page parts that still work (processor steps, advanced settings, verdict line,
    /// app list, start-up tiles, UPS) are moved into the new cards, so their existing code keeps filling them.
    /// </summary>
    public partial class MainWindow
    {
        private const string PlanSaverGuid = "a1841308-3541-4fab-bc81-f71556f20b4a";
        private const string PlanBalancedGuid = "381b4222-f694-41f0-9685-ff5bb260df2e";
        private const string PlanHighGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

        private bool _prInit;
        private string _prTab = "bat";      // laptop: "bat" (Battery) or "sleep" (Power & sleep)
        private string _prSrc = "ac";       // laptop: the power source the plan card shows and changes
        private bool _prBig;                // a large screen: more cards, advanced settings open
        private bool _prShort;              // a short screen: everything a little tighter
        private bool? _prLaidOutLaptop;
        private bool _prLaidOutBig, _prLaidOutHuge;
        private string _prLaidOutTab = "";
        private bool _prSync;
        private List<string> _prPlanGuids = new();
        private string? _prActivePlan;

        // the cards
        private Border? _prPlanCard, _prIdleCard, _prAwakeCard, _prStartupCard, _prEventsCard, _prUseCard;
        private Border? _prBatCard, _prHealthCard, _prSaverCard, _prAppsCard;
        // parts that change
        private PlanDial? _prDial;
        private TextBlock? _prPlanName, _prPlanDesc, _prCpuCaption, _prUsePcLine;
        private WrapPanel? _prPills;
        private StackPanel? _prSrcSwitch;
        private Border? _prAdvInline;
        private DockPanel? _prCpuHead;
        private StackPanel? _prIdleBody, _prAwakeBody, _prEventsBody, _prBatBody, _prHealthBody, _prSaverBody;
        private DockPanel? _prUseBody;   // the number on top, the bars and the monthly line at the bottom, the chart fills the rest
        private TextBlock? _prAwakeTitle;
        private int _prAwakeTab = -1;   // the open tab of "What keeps the PC awake" (kept across refreshes)
        private Border? _prAwakeBadge;
        private Border? _prSaverChip, _prHealthChip;

        // values read with the other settings (RefreshPowerSimpleAsync)
        private int? _idleHibAc, _idleHibDc, _bsThreshold;
        private bool? _bsDims;
        private int? _psCpuMinDc, _psCpuMaxDc;
        private PowerAdvancedService.BoostMode? _psCpuBoostDc;

        // power use (Power use now): the last 10 minutes and the average of this session
        private readonly List<(DateTime T, double W)> _prUseHist = new();
        // on battery, when the battery does not report its power: the charge level over time, to work the power out
        private readonly List<(DateTime T, int Pct)> _prPctHist = new();

        /// <summary>
        /// The laptop's power worked out from how fast the charge drops (for batteries that report no power reading): needs a
        /// drop of at least 2 % to be meaningful, so it appears a few minutes after unplugging.
        /// </summary>
        private double? PowerFromChargeDrop(SMDWin.Models.BatteryInfo bi)
        {
            if (bi.IsAC || bi.FullCapacityMWh <= 0 || _prPctHist.Count < 2) return null;
            var (t0, p0) = _prPctHist[0];
            var (t1, p1) = _prPctHist[^1];
            double h = (t1 - t0).TotalHours;
            if (p0 - p1 < 2 || h <= 0) return null;
            double w = (p0 - p1) / 100.0 * bi.FullCapacityMWh / 1000.0 / h;
            return w is > 0.5 and < 300 ? w : null;
        }
        private double _prUseSum;
        private int _prUseCount;
        private double? _prCpuW, _prGpuW, _prRestW, _prTotalW;
        private bool _prUseMeasured;
        private List<PowerEventsService.PowerEvent>? _prEvents;
        private double? _prHoursPerDay;
        private DateTime _prEventsRead;
        private System.Windows.Threading.DispatcherTimer? _prTimer;

        private static readonly int[] PrIdleSteps = { 1, 2, 3, 5, 10, 15, 20, 30, 45, 60, 90, 120, 180, 240, 300, 0 };

        // ════════════════════════════════════════════════════════════════════════════════════════
        //  Set-up (once)
        // ════════════════════════════════════════════════════════════════════════════════════════

        private void InitPowerRedesign()
        {
            if (_prInit) return;
            _prInit = true;
            try
            {
                if (FindName("PowerLegacyHost") is FrameworkElement legacy) legacy.Visibility = Visibility.Collapsed;
                if (FindName("IconPowerDefaults") is System.Windows.Shapes.Path ip) ip.Data = FluentIcons.Get("arrow_counterclockwise");
                if (FindName("TxtBtnPowerDefaults") is TextBlock td) td.Text = PSim("Defaults", "Reset to defaults");
                if (FindName("BtnPowerDefaults") is Button bd)
                {
                    bd.ToolTip = PSim("DefaultsTip", "Balanced plan, Normal processor and the recommended times — the settings that suit most PCs");
                    bd.Click += async (_, _) => await ResetPowerDefaultsAsync();
                }
                if (FindName("BtnPowerTabBattery") is Button tb) tb.Click += (_, _) => SetPowerTab("bat");
                if (FindName("BtnPowerTabSleep") is Button ts) ts.Click += (_, _) => SetPowerTab("sleep");
                if (FindName("BtnPowerCheck") is FrameworkElement pc)
                    pc.ToolTip = PSim("CheckTip", "Looks for settings and devices that waste power or stop sleep, and explains each one");
                if (FindName("TxtPowerSleepTitle") is FrameworkElement st) st.ToolTip = null;   // the old card, hidden

                BuildPowerCards();

                if (FindName("PanelBattery") is ScrollViewer sv)
                    sv.SizeChanged += (_, _) => LayoutPowerCards();

                _prTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                _prTimer.Tick += (_, _) => PowerUseTick();
                _prTimer.Start();
                _ = ReadPowerEventsAsync();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "InitPowerRedesign"); }
        }

        /// <summary>Takes an element out of its current parent, so it can be placed in a new card.</summary>
        private static T Detach<T>(T el) where T : FrameworkElement
        {
            switch (el.Parent)
            {
                case WpfPanel p: p.Children.Remove(el); break;
                case Decorator d: d.Child = null; break;
                case ContentControl c: c.Content = null; break;
                case Popup pp: pp.Child = null; break;
            }
            return el;
        }

        /// <summary>A card: icon + title on one line (extra items on the right), the body below.</summary>
        private Border PrCard(string icon, string title, out DockPanel head, out Grid body, out TextBlock titleBlock)
        {
            var card = new Border { Padding = new Thickness(18, 14, 18, 14), Margin = new Thickness(0), Cursor = WpfCursors.Arrow };
            if (TryFindResource("CompCardStyle") is Style st) card.Style = st;
            card.Padding = new Thickness(18, 14, 18, 14);
            card.Cursor = WpfCursors.Arrow;
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            head = new DockPanel { Margin = new Thickness(0, 0, 0, 10), LastChildFill = true };
            var ic = FluentIcons.Box(icon, 18, "TextPrimaryBrush");
            ic.Margin = new Thickness(0, 0, 10, 0);
            ic.VerticalAlignment = VAlign.Center;
            DockPanel.SetDock(ic, Dock.Left);
            head.Children.Add(ic);
            titleBlock = new TextBlock { Text = title, VerticalAlignment = VAlign.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            if (TryFindResource("DashRowName") is Style ts) titleBlock.Style = ts;
            root.Children.Add(head);
            body = new Grid();
            Grid.SetRow(body, 1);
            root.Children.Add(body);
            card.Child = root;
            return card;
        }

        /// <summary>Puts the title last, so the items docked right before it keep their place.</summary>
        private static void PrFinishHead(DockPanel head, TextBlock title) => head.Children.Add(title);

        private static void PrDockRight(DockPanel head, UIElement e)
        {
            if (e is FrameworkElement fe) { fe.VerticalAlignment = VAlign.Center; fe.Margin = new Thickness(8, 0, 0, 0); }
            DockPanel.SetDock(e, Dock.Right);
            head.Children.Add(e);
        }

        /// <summary>A choice button like the app's tabs (Storage, Events): the selected one raised, the others flat.</summary>
        private Wpf.Ui.Controls.Button PrSeg(object content, bool selected, Action click, string? tip = null)
        {
            var b = new Wpf.Ui.Controls.Button
            {
                Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary, Content = content, ToolTip = tip,
                BorderThickness = new Thickness(1), Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 2, 0),
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, FontSize = Ui.FontS,
            };
            if (selected)
            {
                b.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "SelBtnBg");
                b.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "SelBtnBorderBrush");
            }
            else
            {
                b.Background = System.Windows.Media.Brushes.Transparent;
                b.BorderBrush = System.Windows.Media.Brushes.Transparent;
            }
            b.Click += (_, _) => click();
            return b;
        }

        private Border PrTrack(UIElement content)
        {
            var t = new Border { Child = content, Tag = "SegTrack", HorizontalAlignment = HAlign.Left };
            if (TryFindResource("SegmentTrackStyle") is Style st) t.Style = st;
            return t;
        }

        private static StackPanel PrIconText(string icon, string? text, double size = 13)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var vb = new Viewbox { Width = size, Height = size, VerticalAlignment = VAlign.Center };
            var path = new System.Windows.Shapes.Path { Data = FluentIcons.Get(icon), Width = 24, Height = 24 };
            // the icon takes the text colour of the button it sits in
            path.SetBinding(System.Windows.Shapes.Shape.FillProperty, new System.Windows.Data.Binding
            {
                Path = new PropertyPath("(0)", TextElement.ForegroundProperty),
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.Self),
            });
            vb.Child = path;
            sp.Children.Add(vb);
            if (!string.IsNullOrEmpty(text)) sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VAlign.Center });
            return sp;
        }

        private TextBlock PrText(string text, string brush = "TextPrimaryBrush", double? size = null, bool bold = false, bool wrap = false)
        {
            var t = new TextBlock
            {
                Text = text, FontSize = size ?? Ui.FontS, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                VerticalAlignment = VAlign.Center,
            };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }

        private Border PrSurface(UIElement child, Thickness? pad = null)
        {
            var b = new Border { Child = child, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = pad ?? new Thickness(12, 8, 12, 8) };
            b.SetResourceReference(Border.BackgroundProperty, "CardSurfaceBrush");
            b.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            return b;
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        //  The card shells and the parts moved in from the older page
        // ════════════════════════════════════════════════════════════════════════════════════════

        private void BuildPowerCards()
        {
            // Power plan
            _prPlanCard = PrCard("flash", PSim("PlanTitle", "Power plan"), out var ph, out var pb, out var pt);
            _prPlanCard.SizeChanged += (_, _) => FitEventsColumn();
            _prSrcSwitch = new StackPanel { Orientation = Orientation.Horizontal };
            var srcTrack = PrTrack(_prSrcSwitch);
            srcTrack.Visibility = Visibility.Collapsed;
            srcTrack.Tag = "SegTrack";
            srcTrack.Name = "PrSrcTrack";
            PrDockRight(ph, srcTrack);
            PrFinishHead(ph, pt);
            var plan = new StackPanel();
            _prDial = new PlanDial { Height = 150, Width = 255, HorizontalAlignment = HAlign.Center, Margin = new Thickness(0, 0, 0, 2),
                                     LeftLabel = PSim("DialSaver", "Saver"), RightLabel = PSim("DialMax", "Max") };
            plan.Children.Add(_prDial);
            _prPlanName = PrText("—", "TextPrimaryBrush", Ui.FontTitle, bold: true);
            _prPlanName.HorizontalAlignment = HAlign.Center;
            if (TryFindResource("AppFontDisplay") is System.Windows.Media.FontFamily disp) _prPlanName.FontFamily = disp;
            plan.Children.Add(_prPlanName);
            _prPlanDesc = PrText("", "TextSecondaryBrush", Ui.FontS, wrap: true);
            _prPlanDesc.TextAlignment = TextAlignment.Center;
            _prPlanDesc.HorizontalAlignment = HAlign.Center;
            _prPlanDesc.Margin = new Thickness(0, 2, 0, 8);
            plan.Children.Add(_prPlanDesc);
            _prPills = new WrapPanel { HorizontalAlignment = HAlign.Center };
            plan.Children.Add(PrTrackCentered(_prPills));
            _prCpuHead = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
            _prCpuCaption = PrText(PSim("CpuTitle", "Processor"), "TextPrimaryBrush", Ui.FontM, bold: true);
            plan.Children.Add(_prCpuHead);
            // the processor steps, their explanation and "Advanced settings" (MainWindow.PowerSimple.cs / PowerAdvanced.cs)
            if (FindName("BtnPowerAdvanced") is FrameworkElement advBtn && advBtn.Parent is Grid advGrid)
            {
                Detach(advGrid);
                advGrid.HorizontalAlignment = HAlign.Right;
                DockPanel.SetDock(advGrid, Dock.Right);
                _prCpuHead.Children.Add(advGrid);
            }
            _prCpuHead.Children.Add(_prCpuCaption);
            if (FindName("CpuModeSeg") is FrameworkElement seg) plan.Children.Add(Detach(seg));
            if (FindName("TxtCpuModeExplain") is TextBlock ex) { Detach(ex); ex.Margin = new Thickness(0, 6, 0, 0); plan.Children.Add(ex); }
            _prAdvInline = new Border { Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
            plan.Children.Add(_prAdvInline);
            _prUsePcLine = PrText("", "TextSecondaryBrush", Ui.FontS, wrap: true);
            _prUsePcLine.Margin = new Thickness(0, 10, 0, 0);
            _prUsePcLine.Visibility = Visibility.Collapsed;
            plan.Children.Add(_prUsePcLine);
            if (FindName("TxtUltimateRow") is FrameworkElement ult) ult.Visibility = Visibility.Collapsed;
            pb.Children.Add(plan);

            // When the PC is not used
            _prIdleCard = PrCard("weather_moon", PSim("IdleTitle", "When the PC is not used"), out var ih, out var ib, out var it);
            PrDockRight(ih, PrText(PSim("DragHint", "drag a slider"), "TextSecondaryBrush", Ui.FontXS));
            PrFinishHead(ih, it);
            _prIdleBody = new StackPanel();
            ib.Children.Add(_prIdleBody);
            _prIdleCard.SizeChanged += (_, _) => FitEventsColumn();

            // What keeps the PC awake
            _prAwakeCard = PrCard("weather_moon", PSim("AwakeTitle", "What keeps the PC awake"), out var ah, out var ab, out var at);
            _prAwakeTitle = at;
            var refresh = new Wpf.Ui.Controls.Button { Content = PSim("Refresh", "Refresh"), Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary, Padding = new Thickness(10, 3, 10, 3) };
            refresh.Click += (s, e) => PowerDiagRefresh_Click(s ?? refresh, e);
            PrDockRight(ah, refresh);
            // the count badge replaces the moon icon when something keeps the PC awake
            _prAwakeBadge = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 10, 0), Visibility = Visibility.Collapsed };
            DockPanel.SetDock(_prAwakeBadge, Dock.Left);
            ah.Children.Insert(1, _prAwakeBadge);
            PrFinishHead(ah, at);
            _prAwakeBody = new StackPanel();
            ab.Children.Add(_prAwakeBody);

            // Start-up and shut-down: the tiles (BuildStartupTiles), as a list
            _prStartupCard = PrCard("power", PSim("StartupTitle", "Start-up and shut-down"), out var sh, out var sb, out var stt);
            PrFinishHead(sh, stt);
            if (FindName("PowerStartupHost") is UniformGrid sth) { Detach(sth); sth.VerticalAlignment = VAlign.Top; sb.Children.Add(sth); }

            // Recent starts and stops (large screens)
            _prEventsCard = PrCard("history", PSim("EventsTitle", "Recent starts and stops"), out var eh, out var eb, out var ett);
            PrFinishHead(eh, ett);
            _prEventsBody = new StackPanel();
            eb.Children.Add(new ScrollViewer { Content = _prEventsBody, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            // Power use now
            _prUseCard = PrCard("flash", PSim("UseTitle", "Power use now"), out var uh, out var ub, out var utt);
            PrFinishHead(uh, utt);
            _prUseBody = new DockPanel { LastChildFill = true };
            ub.Children.Add(_prUseBody);

            // Battery
            _prBatCard = PrCard("battery_5", PSim("BatTitle", "Battery"), out var bh, out var bb, out var btt);
            PrFinishHead(bh, btt);
            _prBatBody = new StackPanel();
            bb.Children.Add(_prBatBody);
            if (FindName("BatVerdictBox") is FrameworkElement verdict) Detach(verdict);

            // Battery health
            _prHealthCard = PrCard("shield_checkmark", PSim("HealthTitle", "Battery health"), out var hh, out var hb, out var htt);
            _prHealthChip = new Border();
            PrDockRight(hh, _prHealthChip);
            PrFinishHead(hh, htt);
            _prHealthBody = new StackPanel();
            hb.Children.Add(_prHealthBody);

            // Battery saver
            _prSaverCard = PrCard("leaf_one", PSim("SaverTitle", "Battery saver"), out var vh, out var vb, out var vtt);
            _prSaverChip = new Border();
            PrDockRight(vh, _prSaverChip);
            PrFinishHead(vh, vtt);
            _prSaverBody = new StackPanel();
            vb.Children.Add(_prSaverBody);

            // Apps using the battery: the existing list (UpdatePowerTopList), in a grid whose second row it measures
            _prAppsCard = PrCard("apps_list", PSim("AppsTitle", "Apps using the battery"), out var ah2, out var ab2, out var att2);
            PrDockRight(ah2, PrText(PSim("AppsSub", "by processor use, now"), "TextSecondaryBrush", Ui.FontXS));
            PrFinishHead(ah2, att2);
            if (FindName("PowerTopList") is StackPanel top)
            {
                Detach(top);
                var g = new Grid();
                g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
                g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                Grid.SetRow(top, 1);
                top.Margin = new Thickness(0);
                g.Children.Add(top);
                ab2.Children.Add(g);
            }

            // the UPS card (shown by LoadUpsInfoAsync when Windows reports one)
            if (FindName("UpsCard") is Border ups && FindName("PowerUpsHost") is Border upsHost)
            {
                Detach(ups);
                ups.Margin = new Thickness(0, 0, 0, 12);
                upsHost.Child = ups;
            }
        }

        private Border PrTrackCentered(WpfPanel content)
        {
            var t = PrTrack(content);
            t.HorizontalAlignment = HAlign.Center;
            t.Margin = new Thickness(0, 0, 0, 12);
            return t;
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        //  Layout: PC / laptop tab, small / large screen
        // ════════════════════════════════════════════════════════════════════════════════════════

        private void SetPowerTab(string tab)
        {
            _prTab = tab;
            LayoutPowerCards(force: true);
        }

        private void LayoutPowerCards(bool force = false)
        {
            try
            {
                if (!_prInit || FindName("PowerNewGrid") is not Grid grid) return;
                double w = (FindName("PanelBattery") as FrameworkElement)?.ActualWidth ?? 0;
                double h = (FindName("PanelBattery") as FrameworkElement)?.ActualHeight ?? 0;
                if (w <= 0) return;
                // "big": room for the recent events, the labels on every choice and the extra tiles (a 1080p screen);
                // "huge": also the large dial and the advanced settings open in the plan card (a 27" screen)
                bool big = w >= 1400 && h >= 760;
                bool huge = big && h >= 1000;
                // a short screen (1366 × 768, ~700 high): smaller dial, no explanation under the times
                _prShort = h < 720;
                bool laptop = _psLaptop;
                if (!force && _prLaidOutLaptop == laptop && _prLaidOutBig == big && _prLaidOutHuge == huge && _prLaidOutTab == _prTab) return;
                _prLaidOutLaptop = laptop; _prLaidOutBig = big; _prLaidOutHuge = huge; _prLaidOutTab = _prTab;
                _prBig = big;

                if (FindName("PowerTabsBar") is FrameworkElement bar) bar.Visibility = laptop ? Visibility.Visible : Visibility.Collapsed;
                if (FindName("TxtPowerSubtitle") is TextBlock sub)
                    sub.Text = laptop ? PSim("SubtitleLaptop", "Battery, power plan and sleep") : PSim("Subtitle", "How the PC saves energy, and what stops it from sleeping");
                UpdatePowerTabs();

                foreach (var c in new FrameworkElement?[] { _prPlanCard, _prIdleCard, _prAwakeCard, _prStartupCard, _prEventsCard, _prUseCard, _prBatCard, _prHealthCard, _prSaverCard, _prAppsCard })
                    if (c != null) Detach(c);
                grid.Children.Clear();
                grid.RowDefinitions.Clear();
                grid.ColumnDefinitions.Clear();
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                Grid Row(int r, params double[] stars)
                {
                    var g = new Grid { Margin = new Thickness(0, r == 0 ? 0 : 12, 0, 0) };
                    for (int i = 0; i < stars.Length; i++)
                    {
                        if (i > 0) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(stars[i], GridUnitType.Star) });
                    }
                    Grid.SetRow(g, r);
                    grid.Children.Add(g);
                    return g;
                }
                static void Put(Grid g, int col, FrameworkElement? e) { if (e == null) return; Grid.SetColumn(e, col * 2); g.Children.Add(e); }
                FrameworkElement IdleWithEvents()
                {
                    var s = new Grid();
                    _prRightStack = s;
                    s.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    s.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    s.Children.Add(_prIdleCard!);
                    _prEventsCard!.Margin = new Thickness(0, 12, 0, 0);
                    Grid.SetRow(_prEventsCard, 1);
                    s.Children.Add(_prEventsCard);
                    return s;
                }

                if (_prEventsCard != null) _prEventsCard.Margin = new Thickness(0);
                _prRightStack = null;
                bool sleepPage = !laptop || _prTab == "sleep";
                if (sleepPage)
                {
                    var r0 = Row(0, 1, 1.4);
                    Put(r0, 0, _prPlanCard);
                    Put(r0, 1, big ? IdleWithEvents() : _prIdleCard);
                    // 14.11.2026: the start-up rows are only title + control now: the awake list gets the width
                    var r1 = !laptop && big ? Row(1, 2.3, 0.85, 1) : Row(1, laptop && big ? 2.0 : 2.2, 1);
                    Put(r1, 0, _prAwakeCard);
                    Put(r1, 1, _prStartupCard);
                    if (!laptop && big) Put(r1, 2, _prUseCard);
                }
                else
                {
                    var r0 = Row(0, 1.45, 1);
                    Put(r0, 0, _prBatCard);
                    Put(r0, 1, _prHealthCard);
                    var r1 = Row(1, 1, 1, 1);
                    Put(r1, 0, _prSaverCard);
                    Put(r1, 1, _prAppsCard);
                    Put(r1, 2, _prUseCard);
                }

                if (_prDial != null) { _prDial.Height = huge ? 200 : _prShort ? 118 : 150; _prDial.Width = huge ? 340 : _prShort ? 200 : 255; }
                if (_prUsePcLine != null) _prUsePcLine.Visibility = !laptop && !big ? Visibility.Visible : Visibility.Collapsed;
                PlaceAdvancedPanel(huge);
                RenderPowerCards();
                FitPowerCards();
                Dispatcher.BeginInvoke(new Action(FitEventsColumn), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "LayoutPowerCards"); }
        }

        private Grid? _prRightStack;   // "When the PC is not used" + "Recent starts and stops", next to the plan card

        /// <summary>
        /// The recent events must not make the top row taller than the plan card (the page would no longer fit): the column gets
        /// the plan card's natural height (or what the idle card needs plus a few event rows) and the list scrolls inside.
        /// </summary>
        private void FitEventsColumn()
        {
            try
            {
                if (_prRightStack == null || _prPlanCard == null || _prIdleCard == null || _prRightStack.Parent == null) return;
                double plan = _prPlanCard.DesiredSize.Height, idle = _prIdleCard.DesiredSize.Height;
                if (plan <= 0 || idle <= 0) return;
                double h = Math.Max(plan, idle + 12 + 130);
                if (double.IsNaN(_prRightStack.Height) || Math.Abs(_prRightStack.Height - h) > 1) _prRightStack.Height = h;
            }
            catch (Exception ex) { AppLogger.Debug($"FitEventsColumn: {ex.Message}"); }
        }

        private Thickness? _advMargin;
        private double _advWidth = double.NaN;

        /// <summary>The advanced processor settings: open in the plan card on a large screen, a panel under the button otherwise.</summary>
        private void PlaceAdvancedPanel(bool inline)
        {
            if (FindName("PowerAdvancedPanel") is not Grid panel || FindName("PopupPowerAdvanced") is not Popup popup || _prAdvInline == null) return;
            _advMargin ??= panel.Margin;
            if (double.IsNaN(_advWidth)) _advWidth = panel.Width;
            var shadow = panel.Children.OfType<Border>().FirstOrDefault();
            if (inline)
            {
                if (panel.Parent != _prAdvInline) { Detach(panel); _prAdvInline.Child = panel; }
                panel.Width = double.NaN;
                panel.Margin = new Thickness(0);
                if (shadow != null) shadow.Visibility = Visibility.Collapsed;
                _prAdvInline.Visibility = Visibility.Visible;
                if (FindName("BtnPowerAdvanced") is FrameworkElement btn && btn.Parent is FrameworkElement g) g.Visibility = Visibility.Collapsed;
            }
            else
            {
                if (panel.Parent != popup) { Detach(panel); popup.Child = panel; }
                panel.Width = _advWidth;
                panel.Margin = _advMargin.Value;
                if (shadow != null) shadow.Visibility = Visibility.Visible;
                _prAdvInline.Visibility = Visibility.Collapsed;
                if (FindName("BtnPowerAdvanced") is FrameworkElement btn && btn.Parent is FrameworkElement g) g.Visibility = Visibility.Visible;
            }
            // the "Plugged in / On battery" row of the panel follows the switch in the plan card
            if (FindName("PowerProfileRow") is FrameworkElement pr) pr.Visibility = Visibility.Collapsed;
        }

        /// <summary>Everything drawn from the values already read (called after each read and on layout changes).</summary>
        private void RenderPowerCards()
        {
            if (!_prInit) return;
            RenderPlanCard();
            RenderIdleCard();
            RenderAwakeCard();
            RenderEventsCard();
            RenderPowerUse();
            if (_lastBatteryInfo is { Present: true } bi) RenderBattery(bi);
            RenderSaverCard();
            UpdatePowerTabs();
        }

        private void UpdatePowerTabs()
        {
            if (FindName("BtnPowerTabBattery") is not Wpf.Ui.Controls.Button bat || FindName("BtnPowerTabSleep") is not Wpf.Ui.Controls.Button slp) return;
            void Mark(Wpf.Ui.Controls.Button b, bool on)
            {
                b.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                if (on)
                {
                    b.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "SelBtnBg");
                    b.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "SelBtnBorderBrush");
                }
                else { b.Background = System.Windows.Media.Brushes.Transparent; b.BorderBrush = System.Windows.Media.Brushes.Transparent; }
            }
            Mark(bat, _prTab == "bat");
            Mark(slp, _prTab == "sleep");

            var b1 = PrIconText("battery_5", PSim("TabBattery", "Battery"));
            if (_lastBatteryInfo is { Present: true } info)
            {
                int? health = info.WearPct is int w ? 100 - w : null;
                string txt = health is int hp ? string.Format(PSim("TabBatteryBadge", "{0} % · health {1} %"), info.ChargePercent, hp) : $"{info.ChargePercent} %";
                var chip = DiagChip(txt, health is < 70 ? "StatusCheckBrush" : "TextSecondaryBrush");
                chip.Margin = new Thickness(8, 0, 0, 0);
                b1.Children.Add(chip);
            }
            bat.Content = b1;
            var b2 = PrIconText("flash", PSim("TabSleep", "Power & sleep"));
            int issues = PowerIssueCount();
            if (issues > 0)
            {
                var chip = DiagChip(issues.ToString(), "StatusCheckBrush");
                chip.Margin = new Thickness(8, 0, 0, 0);
                b2.Children.Add(chip);
            }
            slp.Content = b2;
        }

        private int PowerIssueCount()
        {
            int bad = _pdBlockers?.Count(IsBadBlocker) ?? 0;
            int timers = _pdWakeTimers is null or not 0 ? _pdTimers?.Count ?? 0 : 0;
            return bad + timers;
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        //  Power plan
        // ════════════════════════════════════════════════════════════════════════════════════════

        private static double PlanLit(string? guid, string? ultimate) => guid?.ToLowerInvariant() switch
        {
            PlanSaverGuid => 4,
            PlanBalancedGuid => 7,
            PlanHighGuid => 10,
            null => 0,
            var g when g == ultimate?.ToLowerInvariant() => 14,
            _ => 7,
        };

        private string PlanTitle(string? guid)
        {
            string? g = guid?.ToLowerInvariant();
            if (g == PlanSaverGuid) return PSim("PlanSaver", "Power Saver");
            if (g == PlanBalancedGuid) return PSim("PlanBalanced", "Balanced");
            if (g == PlanHighGuid) return PSim("PlanHigh", "High Performance");
            if (g != null && g == SettingsService.Current.UltimatePlanGuid?.ToLowerInvariant()) return PSim("PlanUltimate", "Ultimate Performance");
            if (g != null && g == _prActivePlan && FindName("TxtActivePowerPlan") is TextBlock t && t.Text.Length > 1) return t.Text;
            return PSim("PlanCustom", "Custom plan");
        }

        private string PlanExplain(string? guid)
        {
            string? g = guid?.ToLowerInvariant();
            if (g == PlanSaverGuid) return PSim("PlanSaverSub", "Slower, but the longest battery life and the least heat");
            if (g == PlanBalancedGuid) return PSim("PlanBalancedSub", "Fast when needed, saves energy when idle");
            if (g == PlanHighGuid) return PSim("PlanHighSub", "Always ready for speed; uses more energy");
            if (g != null && g == SettingsService.Current.UltimatePlanGuid?.ToLowerInvariant()) return PSim("PlanUltimateSub", "No power saving at all — for desktops that do heavy work");
            return PSim("PlanCustomSub", "A plan made on this PC");
        }

        /// <summary>The plan used on the given source: the one chosen for it (laptop), otherwise the active plan.</summary>
        private string? PlanFor(string src)
        {
            if (!_psLaptop) return _prActivePlan;
            string? chosen = src == "ac" ? SettingsService.Current.PowerPlanAc : SettingsService.Current.PowerPlanDc;
            if (chosen != null && _prPlanGuids.Count > 0 && !_prPlanGuids.Contains(chosen.ToLowerInvariant())) chosen = null;
            return chosen?.ToLowerInvariant() ?? _prActivePlan;
        }

        private void RenderPlanCard()
        {
            if (_prDial == null || _prPills == null || _prSrcSwitch == null) return;
            _prActivePlan = PowerAdvancedService.ActiveSchemeGuid()?.ToLowerInvariant() ?? _prActivePlan;
            string? ult = SettingsService.Current.UltimatePlanGuid?.ToLowerInvariant();
            if (ult != null && _prPlanGuids.Count > 0 && !_prPlanGuids.Contains(ult)) ult = null;
            string? shown = PlanFor(_prSrc);
            _prDial.MoveTo(PlanLit(shown, ult));
            _prPlanName!.Text = PlanTitle(shown);
            _prPlanDesc!.Text = _psLaptop
                ? string.Format(PSim("PlanPerSource", "Plugged in: {0} · On battery: {1}"), PlanTitle(PlanFor("ac")), PlanTitle(PlanFor("dc")))
                  + (SettingsService.Current.PowerPlanAc != null && SettingsService.Current.PowerPlanAc != SettingsService.Current.PowerPlanDc
                     ? " — " + PSim("PlanSwitches", "switches by itself") : "")
                : PlanExplain(shown);
            _prPlanDesc.ToolTip = _psLaptop ? PSim("PlanSwitchTip", "SMD Win changes the plan when the charger is plugged in or out, while it runs. The processor and the times below are kept by Windows itself for each source.") : null;

            // the source switch (laptop)
            _prSrcSwitch.Children.Clear();
            if (_prSrcSwitch.Parent is FrameworkElement track) track.Visibility = _psLaptop && (_prTab == "sleep") ? Visibility.Visible : Visibility.Collapsed;
            if (_psLaptop)
            {
                _prSrcSwitch.Children.Add(PrSeg(PrIconText("plug_connected", PSim("PluggedIn", "Plugged in"), 12), _prSrc == "ac", () => SetPlanSource("ac")));
                _prSrcSwitch.Children.Add(PrSeg(PrIconText("battery_5", PSim("OnBattery", "On battery"), 12), _prSrc == "dc", () => SetPlanSource("dc")));
            }

            // the plans
            _prPills.Children.Clear();
            var plans = new List<(string Guid, string Name)> { (PlanSaverGuid, PlanTitle(PlanSaverGuid)), (PlanBalancedGuid, PlanTitle(PlanBalancedGuid)), (PlanHighGuid, PlanTitle(PlanHighGuid)) };
            foreach (var (guid, name) in plans)
            {
                string g = guid;
                _prPills.Children.Add(PrSeg(name, g == shown, () => _ = PickPlanAsync(g)));
            }
            if (!_psLaptop)
                _prPills.Children.Add(PrSeg(PSim("PlanUltimateShort", "Ultimate"), ult != null && ult == shown, () => _ = PickUltimateAsync(ult),
                    PSim("UltTip", "Windows' hidden plan for desktops: no power saving at all")));
            bool custom = shown != null && !plans.Any(p => p.Guid == shown) && shown != ult;
            if (custom) _prPills.Children.Add(PrSeg(PlanTitle(shown), true, () => { }));

            if (_prCpuCaption != null)
                _prCpuCaption.Text = _psLaptop
                    ? PSim("CpuTitle", "Processor") + " · " + (_prSrc == "ac" ? PSim("PluggedInLower", "plugged in") : PSim("OnBatteryLower", "on battery"))
                    : PSim("CpuTitle", "Processor");
        }

        private void SetPlanSource(string src)
        {
            _prSrc = src;
            // the advanced panel shows and changes the same source
            if (FindName(src == "ac" ? "RbPowerProfileAC" : "RbPowerProfileDC") is RadioButton rb) rb.IsChecked = true;
            SyncCpuModeButtons();
            RenderPlanCard();
        }

        private async Task PickPlanAsync(string guid)
        {
            try
            {
                if (!_psLaptop) { await ActivatePlanAsync(guid); return; }
                var s = SettingsService.Current;
                // the first choice for one source keeps the current plan for the other one, so nothing changes there
                if (s.PowerPlanAc == null && s.PowerPlanDc == null) { s.PowerPlanAc = _prActivePlan; s.PowerPlanDc = _prActivePlan; }
                if (_prSrc == "ac") s.PowerPlanAc = guid; else s.PowerPlanDc = guid;
                SettingsService.Save();
                bool onAc = PowerAdvancedService.IsOnAcPower() ?? true;
                ToolActivityLog.Add("Power", $"Plan {(_prSrc == "ac" ? "plugged in" : "on battery")}: {PlanTitle(guid)}", "info");
                if ((_prSrc == "ac") == onAc) await ActivatePlanAsync(guid);
                else
                {
                    ShowToastSuccess(string.Format(_prSrc == "dc" ? PSim("PlanLaterDc", "{0} on battery — used when the charger is unplugged") : PSim("PlanLaterAc", "{0} plugged in — used when the charger is connected"), PlanTitle(guid)));
                    RenderPlanCard();
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "PickPlanAsync"); }
        }

        private async Task PickUltimateAsync(string? ult)
        {
            if (ult != null) { await ActivatePlanAsync(ult); return; }
            string? g = await Task.Run(PowerAdvancedService.AddUltimatePlan);
            if (g == null) { AppDialog.Show(PSim("UltFailed", "Windows did not create the Ultimate Performance plan on this PC."), "SMD Win", AppDialog.Kind.Warning); return; }
            SettingsService.Current.UltimatePlanGuid = g;
            SettingsService.Save();
            _prPlanGuids.Add(g.ToLowerInvariant());
            await ActivatePlanAsync(g);
        }

        private bool _planPerSourceHooked;

        /// <summary>Laptop with a plan chosen per source: switches the plan when the charger is plugged in or out (and once now).</summary>
        private void StartPlanPerSource()
        {
            if (_planPerSourceHooked) return;
            _planPerSourceHooked = true;
            try
            {
                Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
                {
                    if (e.Mode == Microsoft.Win32.PowerModes.StatusChange)
                        Dispatcher.BeginInvoke(new Action(() => _ = ApplyPlanForSourceAsync()));
                };
                Closed += (_, _) => _planPerSourceHooked = false;
                _ = ApplyPlanForSourceAsync();
            }
            catch (Exception ex) { AppLogger.Debug($"StartPlanPerSource: {ex.Message}"); }
        }

        private async Task ApplyPlanForSourceAsync()
        {
            try
            {
                var s = SettingsService.Current;
                if (s.PowerPlanAc == null && s.PowerPlanDc == null) return;
                bool? onAc = PowerAdvancedService.IsOnAcPower();
                if (onAc == null) return;
                string? want = onAc.Value ? s.PowerPlanAc : s.PowerPlanDc;
                if (want == null) return;
                var (active, plans) = await Task.Run(() => (PowerAdvancedService.ActiveSchemeGuid()?.ToLowerInvariant(), PowerAdvancedService.ListPlanGuids()));
                if (string.Equals(active, want, StringComparison.OrdinalIgnoreCase)) return;
                if (plans.Count > 0 && !plans.Contains(want.ToLowerInvariant())) return;   // the plan was deleted meanwhile
                await Task.Run(() => PowerAdvancedService.SetActivePlan(want));
                AppLogger.Info($"Power plan switched for {(onAc.Value ? "plugged in" : "on battery")}: {want}");
                ToolActivityLog.Add("Power", $"Plan switched ({(onAc.Value ? "plugged in" : "on battery")})", "info");
                if (FindName("PanelBattery") is FrameworkElement page && page.IsVisible) { LoadActivePowerPlan(); await RefreshPowerSimpleAsync(); }
            }
            catch (Exception ex) { AppLogger.Debug($"ApplyPlanForSourceAsync: {ex.Message}"); }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        //  When the PC is not used: one slider per time, plugged in / on battery side by side
        // ════════════════════════════════════════════════════════════════════════════════════════

        private void RenderIdleCard()
        {
            if (_prIdleBody == null) return;
            _prSync = true;
            try
            {
                _prIdleBody.Children.Clear();
                bool lap = _psLaptop;
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(lap ? 1.15 : 0.9, GridUnitType.Star), MinWidth = 150 });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (lap) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                int row = 0;
                void Add(UIElement e, int c) { Grid.SetRow(e, row); Grid.SetColumn(e, c); g.Children.Add(e); }
                void NewRow() => g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                if (lap)
                {
                    NewRow();
                    var h1 = PrIconText("plug_connected", PSim("PluggedIn", "Plugged in").ToUpperInvariant(), 11);
                    var h2 = PrIconText("battery_5", PSim("OnBattery", "On battery").ToUpperInvariant(), 11);
                    h1.Margin = h2.Margin = new Thickness(12, 0, 0, 4);
                    TextElement.SetFontSize(h1, Ui.FontXS); TextElement.SetFontSize(h2, Ui.FontXS);
                    h1.SetResourceReference(TextElement.ForegroundProperty, "TextSecondaryBrush");
                    h2.SetResourceReference(TextElement.ForegroundProperty, "TextSecondaryBrush");
                    Add(h1, 1); Add(h2, 2);
                    row++;
                }

                bool canSleep = _pdStates == null || _pdStates.HasS3 || _pdStates.HasModernStandby;
                bool hibOn = _pdStates?.HasHibernate == true;
                void AddSlider(string icon, string what, string title, string sub, int? ac, int? dc)
                {
                    if (ac is null && dc is null) return;
                    NewRow();
                    Add(IdleLabel(icon, title, sub), 0);
                    Add(PrIdleSlider(what, true, ac, title), 1);
                    if (lap) Add(PrIdleSlider(what, false, dc, title), 2);
                    row++;
                }
                void AddActions(string icon, string what, string title, int? ac, int? dc)
                {
                    if (ac is null && dc is null) return;
                    NewRow();
                    Add(IdleLabel(icon, title, ""), 0);
                    Add(PrActionChoice(what, true, ac, title, canSleep, hibOn), 1);
                    if (lap) Add(PrActionChoice(what, false, dc, title, canSleep, hibOn), 2);
                    row++;
                }

                AddSlider("desktop", "screen", PSim("ScreenOff", "Turn off the screen"), PSim("ScreenOffSub", "after no mouse or keyboard"), _idleScreenAc, _idleScreenDc);
                if (canSleep) AddSlider("weather_moon", "sleep", PSim("GoSleep", "Go to sleep"), PSim("GoSleepSub2", "pauses, wakes in seconds"), _idleSleepAc, _idleSleepDc);
                if (lap && hibOn && canSleep) AddSlider("document", "hib", PSim("ThenHibernate", "Then hibernate"), PSim("ThenHibernateSub", "saves to disk, turns off"), _idleHibAc, _idleHibDc);
                else AddSlider("hard_drive", "disk", PSim("DiskOff", "Stop the hard disks"), PSim("DiskOffSub", "only for HDDs, not SSDs"), _idleDiskAc, _idleDiskDc);
                AddActions("power", "button", PSim("PowerButtonShort", "Power button"), _btnAc, _btnDc);
                if (lap) AddActions("desktop", "lid", PSim("Lid", "Closing the lid"), _lidAc, _lidDc);

                if (g.RowDefinitions.Count == 0)
                {
                    _prIdleBody.Children.Add(PrText(PW("NowUnreadable", "The plan settings could not be read."), "TextSecondaryBrush", Ui.FontS, wrap: true));
                    return;
                }
                _prIdleBody.Children.Add(g);

                // one sentence under the settings
                var note = new TextBlock { FontSize = Ui.FontS, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
                note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                if (lap)
                {
                    var b1 = new Run(PSim("SleepWord", "Sleep")) { FontWeight = FontWeights.SemiBold };
                    b1.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");
                    note.Inlines.Add(b1);
                    note.Inlines.Add(new Run(" " + PSim("SleepExplain", "pauses the laptop with everything open in memory: it wakes in seconds but still uses a little battery. ")));
                    var b2 = new Run(PSim("HibernateWord", "Hibernate")) { FontWeight = FontWeights.SemiBold };
                    b2.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");
                    note.Inlines.Add(b2);
                    note.Inlines.Add(new Run(" " + PSim("HibernateExplain", "saves everything to the disk and turns off completely: no battery used, it takes longer to start, and nothing is lost if the battery runs out.")));
                }
                else
                {
                    string scr = IdleText(_idleScreenAc), slp = IdleText(_idleSleepAc);
                    note.Text = !canSleep ? string.Format(PSim("ExplainNoSleep", "The screen goes off after {0}. This PC has no sleep state."), scr)
                              : _idleSleepAc is 0 or null ? string.Format(PSim("ExplainNever2", "The screen turns off after {0}; the PC never sleeps on its own."), scr)
                              : string.Format(PSim("ExplainSleep2", "The screen turns off after {0} and the PC sleeps after {1} without use."), scr, slp);
                }
                if (!_prShort) _prIdleBody.Children.Add(note);
            }
            catch (Exception ex) { AppLogger.Debug($"RenderIdleCard: {ex.Message}"); }
            finally { _prSync = false; }
        }

        private static int IdleIndex(int? seconds)
        {
            if (seconds is null || seconds <= 0) return PrIdleSteps.Length - 1;
            double min = seconds.Value / 60.0;
            int best = 0;
            for (int i = 0; i < PrIdleSteps.Length - 1; i++)
                if (Math.Abs(PrIdleSteps[i] - min) < Math.Abs(PrIdleSteps[best] - min)) best = i;
            return best;
        }

        /// <summary>A slider over the usual times (1 min … 5 h, Never); the value is written when the slider stops moving.</summary>
        private FrameworkElement PrIdleSlider(string what, bool ac, int? seconds, string title)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            var s = new Slider
            {
                Minimum = 0, Maximum = PrIdleSteps.Length - 1, TickFrequency = 1, IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 2,
                Value = IdleIndex(seconds), VerticalAlignment = VAlign.Center, IsEnabled = seconds.HasValue,
            };
            var val = PrText(seconds.HasValue ? IdleText(PrIdleSteps[(int)s.Value] * 60) : "—", "TextPrimaryBrush", Ui.FontS, bold: true);
            val.HorizontalAlignment = HAlign.Right;
            Grid.SetColumn(val, 1);
            g.Children.Add(s);
            g.Children.Add(val);
            var debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            debounce.Tick += async (_, _) =>
            {
                debounce.Stop();
                int minutes = PrIdleSteps[(int)Math.Round(s.Value)];
                await PrWriteIdleAsync(what, minutes, ac, title);
            };
            s.ValueChanged += (_, _) =>
            {
                int minutes = PrIdleSteps[(int)Math.Round(s.Value)];
                val.Text = IdleText(minutes * 60);
                val.SetResourceReference(TextBlock.ForegroundProperty, minutes == 0 ? "TextSecondaryBrush" : "TextPrimaryBrush");
                if (_prSync) return;
                debounce.Stop(); debounce.Start();
            };
            if (seconds is 0) val.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            var box = PrSurface(g, new Thickness(10, 3, 10, 3));
            box.Margin = new Thickness(12, 3, 0, 3);
            return box;
        }

        private async Task PrWriteIdleAsync(string what, int minutes, bool ac, string title)
        {
            try
            {
                await Task.Run(() => PowerAdvancedService.SetIdleMinutes(what, minutes, ac));
                int sec = minutes * 60;
                switch (what)
                {
                    case "screen": if (ac) _idleScreenAc = sec; else _idleScreenDc = sec; break;
                    case "sleep": if (ac) _idleSleepAc = sec; else _idleSleepDc = sec; break;
                    case "hib": if (ac) _idleHibAc = sec; else _idleHibDc = sec; break;
                    case "disk": if (ac) _idleDiskAc = sec; else _idleDiskDc = sec; break;
                }
                ToolActivityLog.Add("Power", $"{title} ({(ac ? "plugged in" : "battery")}): {IdleText(sec)}", "info");
                ShowToastSuccess(string.Format(PSim("Saved", "Saved — {0}: {1}"), title, IdleText(sec)));
            }
            catch (Exception ex) { AppDialog.Show(string.Format(LanguageService.S("Msg", "AdminRequiredFmt"), ex.Message), "SMD Win"); await RefreshPowerSimpleAsync(); }
        }

        /// <summary>Power button / lid: Do nothing · Sleep · Hibernate · Shut down (+ Turn off the screen when that is the setting).</summary>
        private FrameworkElement PrActionChoice(string what, bool ac, int? current, string title, bool canSleep, bool hibOn)
        {
            // the choices share the width of the column (all of them always visible), each in proportion to its label (16.11.2026:
            // equal widths cut "Hibernate" and "Shut down" to "Hibern…" / "Shut d…" while "Sleep" had room to spare); a label
            // that still does not fit is shortened with "…" and the full name is in the tooltip
            var row = new Grid();
            var opts = new List<(int Value, string Icon)> { (0, "dismiss_circle") };
            if (canSleep || current == 1) opts.Add((1, "weather_moon"));
            if (hibOn || current == 2) opts.Add((2, "document"));
            opts.Add((3, "power"));
            if (current == 4) opts.Add((4, "desktop"));
            foreach (var (v, icon) in opts)
            {
                bool sel = current == v;
                int value = v;
                // every choice has its label; the icons only on a very large screen
                object content = _prLaidOutHuge ? PrIconText(icon, ActionShort(v), 12)
                                                : new TextBlock { Text = ActionShort(v), TextTrimming = TextTrimming.CharacterEllipsis };
                var b = PrSeg(content, sel, () => _ = PrWriteActionAsync(what, value, ac, title), ActionText(v));
                b.HorizontalAlignment = HAlign.Stretch;
                b.HorizontalContentAlignment = HAlign.Center;
                b.Padding = new Thickness(4, 3, 4, 3);
                b.Margin = new Thickness(0, 0, 1, 0);
                b.MinWidth = 0;
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ActionShort(v).Length + 3, GridUnitType.Star) });
                Grid.SetColumn(b, row.ColumnDefinitions.Count - 1);
                row.Children.Add(b);
            }
            var track = PrTrack(row);
            track.Margin = new Thickness(12, 3, 0, 3);
            track.HorizontalAlignment = HAlign.Stretch;
            return track;
        }

        private static string ActionShort(int a) => a switch
        {
            0 => PSim("ActNothingShort", "Nothing"), 1 => PSim("ActSleep", "Sleep"), 2 => PSim("ActHibernate", "Hibernate"),
            3 => PSim("ActShutDown", "Shut down"), 4 => PSim("ActScreenOffShort", "Screen off"), _ => "?",
        };

        private async Task PrWriteActionAsync(string what, int action, bool ac, string title)
        {
            try
            {
                await Task.Run(() => PowerAdvancedService.SetButtonAction(what, action, ac));
                if (what == "lid") { if (ac) _lidAc = action; else _lidDc = action; }
                else { if (ac) _btnAc = action; else _btnDc = action; }
                ToolActivityLog.Add("Power", $"{title} ({(ac ? "plugged in" : "battery")}): {ActionText(action)}", "info");
                ShowToastSuccess(string.Format(PSim("Saved", "Saved — {0}: {1}"), title, ActionText(action)));
            }
            catch (Exception ex) { AppDialog.Show(string.Format(LanguageService.S("Msg", "AdminRequiredFmt"), ex.Message), "SMD Win"); }
            RenderIdleCard();
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        //  What keeps the PC awake: three lists side by side
        // ════════════════════════════════════════════════════════════════════════════════════════

        private void RenderAwakeCard()
        {
            if (_prAwakeBody == null || _prAwakeTitle == null || _prAwakeBadge == null) return;
            try
            {
                _prAwakeBody.Children.Clear();
                int bad = _pdBlockers?.Count(IsBadBlocker) ?? 0;
                bool timersOn = _pdWakeTimers is null or not 0;
                int timers = timersOn ? _pdTimers?.Count ?? 0 : 0;
                int armed = _pdArmed?.Count ?? 0;
                int issues = bad + timers;

                if (_pdBlockers == null && _pdTimers == null && _pdArmed == null)
                {
                    _prAwakeTitle.Text = PSim("AwakeTitle", "What keeps the PC awake");
                    _prAwakeBadge.Visibility = Visibility.Collapsed;
                    _prAwakeBody.Children.Add(PrText(LanguageService.S("Power", "SleepReading", "Reading what keeps the PC awake…"), "TextSecondaryBrush", Ui.FontS));
                    return;
                }
                _prAwakeTitle.Text = issues == 0 ? PSim("AwakeNone", "Nothing keeps the PC awake")
                                   : issues == 1 ? PSim("AwakeOne", "1 thing keeps the PC awake or wakes it")
                                   : string.Format(PSim("AwakeMany", "{0} things keep the PC awake or wake it"), issues);
                _prAwakeBadge.Visibility = issues > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (issues > 0)
                {
                    var c = (TryFindResource("StatusCheckBrush") as SolidColorBrush)?.Color ?? Colors.Orange;
                    _prAwakeBadge.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, c.R, c.G, c.B));
                    var n = PrText(issues.ToString(), "StatusCheckBrush", Ui.FontS, bold: true);
                    n.HorizontalAlignment = HAlign.Center;
                    _prAwakeBadge.Child = n;
                }
                // the moon icon of the header gives its place to the badge
                if (_prAwakeBadge.Parent is DockPanel head && head.Children.Count > 0 && head.Children[0] is Viewbox moon)
                    moon.Visibility = issues > 0 ? Visibility.Collapsed : Visibility.Visible;

                // 14.11.2026: three tabs and one list over the full width (the three narrow columns cut the names)
                var tabs = new (string Dot, string Caption, Action<StackPanel> Fill)[]
                {
                    (bad > 0 ? "StatusCheckBrush" : "StatusGoodBrush", string.Format(PSim("ColStop", "Stop sleep · {0}"), bad), FillBlockers),
                    (timers > 0 ? "StatusCheckBrush" : "StatusGoodBrush", string.Format(PSim("ColWake", "Wake the PC · {0}"), timers), FillTimers),
                    ("TextSecondaryBrush", string.Format(PSim("ColCanWake", "Can wake it · {0}"), armed), FillDevices),
                };
                // first time: open the tab that has something to fix
                if (_prAwakeTab < 0) _prAwakeTab = bad > 0 ? 0 : timers > 0 ? 1 : 0;
                var seg = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(3), BorderThickness = new Thickness(1), HorizontalAlignment = HAlign.Left, Margin = new Thickness(0, 0, 0, 8) };
                seg.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
                seg.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                var segRow = new StackPanel { Orientation = Orientation.Horizontal };
                seg.Child = segRow;
                var list = new StackPanel();
                var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = _prBig ? 300 : 210 };
                var tabBorders = new List<Border>();
                void Show(int idx)
                {
                    _prAwakeTab = idx;
                    for (int k = 0; k < tabBorders.Count; k++)
                    {
                        if (k == idx) tabBorders[k].SetResourceReference(Border.BackgroundProperty, "CardSurfaceBrush");
                        else tabBorders[k].Background = System.Windows.Media.Brushes.Transparent;
                        if (tabBorders[k].Child is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock tt)
                            tt.SetResourceReference(TextBlock.ForegroundProperty, k == idx ? "TextPrimaryBrush" : "TextSecondaryBrush");
                    }
                    list.Children.Clear();
                    tabs[idx].Fill(list);
                    scroll.ScrollToTop();
                }
                for (int i = 0; i < tabs.Length; i++)
                {
                    int idx = i;
                    var sp = new StackPanel { Orientation = Orientation.Horizontal };
                    var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VAlign.Center };
                    dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, tabs[i].Dot);
                    sp.Children.Add(dot);
                    sp.Children.Add(PrText(tabs[i].Caption, "TextSecondaryBrush", Ui.FontS, bold: true));
                    var tb = new Border { Child = sp, CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 5, 12, 5), Cursor = System.Windows.Input.Cursors.Hand, Background = System.Windows.Media.Brushes.Transparent };
                    tb.MouseLeftButtonUp += (_, _) => Show(idx);
                    tabBorders.Add(tb);
                    segRow.Children.Add(tb);
                }
                _prAwakeBody.Children.Add(seg);
                _prAwakeBody.Children.Add(scroll);
                Show(Math.Clamp(_prAwakeTab, 0, tabs.Length - 1));

                if (_pdLastWake != null)
                {
                    var t = new TextBlock { FontSize = Ui.FontS, Margin = new Thickness(0, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = $"{_pdLastWake.When:dd.MM.yyyy HH:mm} · {_pdLastWake.Source}" };
                    t.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    t.Inlines.Add(new Run(string.Format(PSim("LastWake", "Last wake-up: {0} · "), Ago(_pdLastWake.When))));
                    var src = new Run(ShortWakeSource(_pdLastWake.Source)) { FontWeight = FontWeights.SemiBold };
                    src.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");
                    t.Inlines.Add(src);
                    _prAwakeBody.Children.Add(t);
                }
                else AppLogger.Debug("Power: no last wake-up event (Power-Troubleshooter 1) to show");
            }
            catch (Exception ex) { AppLogger.Debug($"RenderAwakeCard: {ex.Message}"); }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        //  Recent starts and stops (large screens) and the hours a day the PC is on
        // ════════════════════════════════════════════════════════════════════════════════════════

        private async Task ReadPowerEventsAsync()
        {
            if ((DateTime.Now - _prEventsRead).TotalMinutes < 5 && _prEvents != null) return;
            _prEventsRead = DateTime.Now;
            try
            {
                var ev = await Task.Run(() => PowerEventsService.Read(14));
                _prEvents = ev;
                _prHoursPerDay = PowerEventsService.HoursOnPerDay(ev);
                RenderEventsCard();
                RenderPowerUse();
            }
            catch (Exception ex) { AppLogger.Debug($"ReadPowerEventsAsync: {ex.Message}"); }
        }

        private void RenderEventsCard()
        {
            if (_prEventsBody == null) return;
            _prEventsBody.Children.Clear();
            if (_prEvents == null) { _prEventsBody.Children.Add(PrText(PSim("EventsReading", "Reading the Windows log…"), "TextSecondaryBrush")); return; }
            if (_prEvents.Count == 0) { _prEventsBody.Children.Add(PrText(PSim("EventsNone", "No starts or stops in the Windows log for the last two weeks."), "TextSecondaryBrush", wrap: true)); return; }
            foreach (var e in _prEvents.Take(_prBig ? 9 : 6))
            {
                var (icon, text, warn) = e.Kind switch
                {
                    PowerEventsService.Kind.Start => ("play", PSim("EvStart", "Started (full start)"), false),
                    PowerEventsService.Kind.FastStart => ("play", PSim("EvFastStart", "Started (Fast Startup)"), false),
                    PowerEventsService.Kind.Resume => ("play", PSim("EvResume", "Started from hibernate"), false),
                    PowerEventsService.Kind.ShutDown => ("power", PSim("EvShutDown", "Shut down"), false),
                    PowerEventsService.Kind.Unexpected => ("warning", PSim("EvUnexpected", "Unexpected shut-down — power loss or freeze"), true),
                    PowerEventsService.Kind.Sleep => ("weather_moon", e.Detail == "hibernate" ? PSim("EvHibernate", "Hibernated") : PSim("EvSleep", "Went to sleep"), false),
                    _ => ("weather_moon", e.Detail.Length > 0 ? string.Format(PSim("EvWakeBy", "Woke from sleep — {0}"), ShortWakeSource(e.Detail)) : PSim("EvWake", "Woke from sleep"), false),
                };
                var g = new DockPanel { LastChildFill = true };
                var when = PrText(WhenText(e.When), "TextSecondaryBrush", Ui.FontXS);
                when.Margin = new Thickness(10, 0, 0, 0);
                DockPanel.SetDock(when, Dock.Right);
                g.Children.Add(when);
                var ic = FluentIcons.Box(icon, 14, warn ? "StatusCheckBrush" : "TextSecondaryBrush");
                ic.Margin = new Thickness(0, 0, 8, 0);
                DockPanel.SetDock(ic, Dock.Left);
                g.Children.Add(ic);
                var t = PrText(text, "TextPrimaryBrush", Ui.FontS);
                t.ToolTip = $"{e.When:dd.MM.yyyy HH:mm:ss}";
                g.Children.Add(t);
                var row = PrSurface(g, new Thickness(10, 5, 10, 5));
                row.Margin = new Thickness(0, 0, 0, 5);
                if (warn) row.SetResourceReference(Border.BorderBrushProperty, "StatusCheckBrush");
                _prEventsBody.Children.Add(row);
            }
        }

        private static string WhenText(DateTime when)
        {
            var d = when.Date;
            if (d == DateTime.Today) return string.Format(PSim("WhenToday", "today {0:HH:mm}"), when);
            if (d == DateTime.Today.AddDays(-1)) return string.Format(PSim("WhenYesterday", "yesterday {0:HH:mm}"), when);
            return when.ToString("dd.MM HH:mm");
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        //  Power use now
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Every few seconds while the page is open: on battery the battery's own reading is the whole laptop (measured); on
        /// the charger or on a PC the processor and graphics card are measured and the rest of the PC is estimated.
        /// </summary>
        private void PowerUseTick()
        {
            try
            {
                if (FindName("PanelBattery") is not FrameworkElement page || !page.IsVisible) return;
                var bi = _lastBatteryInfo;
                double? cpu = _lastTempSnap?.CpuPowerW is float c && c > 0.3f ? (double?)c : null;
                double? gpu = _lastTempSnap?.GpuPowerW is float g && g > 0.3f ? (double?)g : null;
                bool laptop = _psLaptop;
                if (bi is { Present: true })
                {
                    if (bi.IsAC) _prPctHist.Clear();
                    else
                    {
                        if (_prPctHist.Count == 0 || _prPctHist[^1].Pct != bi.ChargePercent) _prPctHist.Add((DateTime.Now, bi.ChargePercent));
                        _prPctHist.RemoveAll(p => p.T < DateTime.Now.AddMinutes(-20));
                    }
                }
                double? total;
                if (laptop && bi is { Present: true, IsCharging: false, IsAC: false } && bi.DischargePowerW > 0.2)
                {
                    total = bi.DischargePowerW;
                    _prUseMeasured = true;
                    _prRestW = null;
                }
                else if (laptop && bi is { Present: true, IsAC: false } && PowerFromChargeDrop(bi) is double wd)
                {
                    // the battery reports no power: worked out from the charge drop (still the whole laptop)
                    total = wd;
                    _prUseMeasured = true;
                    _prRestW = null;
                }
                else if (cpu.HasValue || gpu.HasValue)
                {
                    // the rest: board, memory, disks, fans, screen (laptop) — a typical figure, written as an estimate
                    double rest = laptop ? 9 : 28;
                    total = (cpu ?? 0) + (gpu ?? 0) + rest;
                    _prUseMeasured = false;
                    _prRestW = rest;
                }
                else total = null;
                (_prCpuW, _prGpuW, _prTotalW) = (cpu, gpu, total);
                if (total.HasValue)
                {
                    _prUseHist.Add((DateTime.Now, total.Value));
                    _prUseHist.RemoveAll(p => p.T < DateTime.Now.AddMinutes(-10));
                    _prUseSum += total.Value;
                    _prUseCount++;
                }
                RenderPowerUse();
                if (laptop && _prTab == "bat" && bi is { Present: true }) RenderBatteryChartOnly();
            }
            catch (Exception ex) { AppLogger.Debug($"PowerUseTick: {ex.Message}"); }
        }

        private void RenderPowerUse()
        {
            try
            {
                double? avg = _prUseCount > 0 ? _prUseSum / _prUseCount : null;
                double hpd = _prHoursPerDay ?? 8;
                double price = SettingsService.Current.ElectricityPricePerKwh;
                string cur = PSim("Currency", "lei");
                if (_prUsePcLine != null)
                    _prUsePcLine.Text = _prTotalW.HasValue
                        ? string.Format(PSim("UseLine", "Using now about {0:F0} W — processor + graphics measured, the rest estimated"), _prTotalW.Value)
                        : "";
                if (_prUseBody == null) return;
                _prUseBody.Children.Clear();
                if (!_prTotalW.HasValue)
                {
                    _prUseBody.Children.Add(PrText(PSim("UseNone", "The power use cannot be read on this PC right now (no sensor reading)."), "TextSecondaryBrush", Ui.FontS, wrap: true));
                    return;
                }
                var top = new StackPanel { Orientation = Orientation.Horizontal };
                var big = PrText($"{_prTotalW.Value:F0}", "TextPrimaryBrush", 34, bold: true);
                if (TryFindResource("NumFont") is System.Windows.Media.FontFamily nf) big.FontFamily = nf;
                top.Children.Add(big);
                var unit = PrText(" W", "TextSecondaryBrush", Ui.FontL);
                unit.VerticalAlignment = VAlign.Bottom; unit.Margin = new Thickness(2, 0, 10, 7);
                top.Children.Add(unit);
                var note = PrText(_prUseMeasured ? PSim("UseMeasuredBat", "measured by the battery: the whole laptop")
                                                 : PSim("UseEstimated", "≈ total: processor and graphics measured, the rest estimated"), "TextSecondaryBrush", Ui.FontXS, wrap: true);
                note.VerticalAlignment = VAlign.Bottom; note.Margin = new Thickness(0, 0, 0, 8); note.MaxWidth = 210;
                top.Children.Add(note);
                DockPanel.SetDock(top, Dock.Top);
                _prUseBody.Children.Add(top);
                var bars = new StackPanel();

                void Bar(string label, double w, string key)
                {
                    var g = new Grid { Margin = new Thickness(0, 6, 0, 0) };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
                    g.Children.Add(PrText(label, "TextSecondaryBrush", Ui.FontXS));
                    var track = new Grid { Height = 5, VerticalAlignment = VAlign.Center, Margin = new Thickness(6, 0, 8, 0) };
                    double f = Math.Clamp(w / Math.Max(1, _prTotalW!.Value), 0, 1);
                    track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(f, GridUnitType.Star) });
                    track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - f, GridUnitType.Star) });
                    var bg = new Border { CornerRadius = new CornerRadius(3) };
                    bg.SetResourceReference(Border.BackgroundProperty, "GaugeTrackBrush");
                    Grid.SetColumnSpan(bg, 2);
                    track.Children.Add(bg);
                    var fg = new Border { CornerRadius = new CornerRadius(3) };
                    fg.SetResourceReference(Border.BackgroundProperty, key);
                    track.Children.Add(fg);
                    Grid.SetColumn(track, 1);
                    g.Children.Add(track);
                    var v = PrText($"{w:F0} W", "TextPrimaryBrush", Ui.FontXS, bold: true);
                    v.HorizontalAlignment = HAlign.Right;
                    Grid.SetColumn(v, 2);
                    g.Children.Add(v);
                    bars.Children.Add(g);
                }
                if (_prUseMeasured) Bar(PSim("UseWholeLaptop", "Whole laptop"), _prTotalW.Value, "DataBrush");
                else
                {
                    if (_prCpuW.HasValue) Bar(PSim("UseCpu", "Processor"), _prCpuW.Value, "DataBrush");
                    if (_prGpuW.HasValue) Bar(PSim("UseGpu", "Graphics card"), _prGpuW.Value, "StatusCheckBrush");
                    if (_prRestW.HasValue) Bar(_psLaptop ? PSim("UseRestLaptop", "Rest of the laptop (est.)") : PSim("UseRest", "Rest of the PC (est.)"), _prRestW.Value, "TextSecondaryBrush");
                }

                if (avg.HasValue)
                {
                    double kwh = avg.Value * hpd * 30 / 1000.0;
                    // small amounts keep a decimal (12 W × 1 h a day is 0.4 kWh, not 0)
                    static string Num(double x) => x < 10 ? x.ToString("0.0") : x.ToString("F0");
                    var foot = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
                    var right = new TextBlock { FontSize = Ui.FontXS, VerticalAlignment = VAlign.Center };
                    right.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    right.Inlines.Add(new Run("≈ "));
                    right.Inlines.Add(new Run($"{Num(kwh)} kWh ≈ {Num(kwh * price)} {cur}") { FontWeight = FontWeights.SemiBold });
                    right.Inlines.Add(new Run(PSim("PerMonth", "/month")));
                    right.ToolTip = string.Format(PSim("CostTip", "{0:F0} W on average × {1:F1} h a day × 30 days, at {2:F2} {3}/kWh"), avg.Value, hpd, price, cur);
                    DockPanel.SetDock(right, Dock.Right);
                    foot.Children.Add(right);
                    var left = new TextBlock { FontSize = Ui.FontXS, VerticalAlignment = VAlign.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                    left.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    left.Inlines.Add(new Run(PSim("AvgWhileOn", "Average while on: ")));
                    left.Inlines.Add(new Run($"{avg.Value:F0} W · {hpd:0.#} h") { FontWeight = FontWeights.SemiBold });
                    left.Inlines.Add(new Run(_prHoursPerDay.HasValue ? PSim("PerDay", "/day") : PSim("PerDayAssumed", "/day (assumed)")));
                    foot.Children.Add(left);
                    DockPanel.SetDock(foot, Dock.Bottom);
                    _prUseBody.Children.Add(foot);
                }
                DockPanel.SetDock(bars, Dock.Bottom);
                _prUseBody.Children.Add(bars);
                // the chart takes the height that is left in the card
                _prUseBody.Children.Add(PrSparkline(_prUseHist.Select(p => p.W).ToList(), 0, "DataBrush"));
            }
            catch (Exception ex) { AppLogger.Debug($"RenderPowerUse: {ex.Message}"); }
        }

        /// <summary>A small line chart with a fading fill (the last values, oldest first); <paramref name="fixedHeight"/> 0 = it
        /// takes the height its parent gives it.</summary>
        private FrameworkElement PrSparkline(List<double> values, double fixedHeight, string brushKey)
        {
            var canvas = new Canvas { ClipToBounds = true, Margin = new Thickness(0, 8, 0, 6), Background = System.Windows.Media.Brushes.Transparent, MinHeight = 46 };
            if (fixedHeight > 0) canvas.Height = fixedHeight;
            void Draw()
            {
                canvas.Children.Clear();
                double w = canvas.ActualWidth, height = canvas.ActualHeight;
                if (w <= 0 || height <= 0 || values.Count < 2) return;
                double max = Math.Max(1, values.Max()) * 1.15, min = 0;
                var pts = new PointCollection();
                for (int i = 0; i < values.Count; i++)
                    pts.Add(new WpfPoint(i * w / (values.Count - 1), height - (values[i] - min) / (max - min) * (height - 2) - 1));
                var line = new Polyline { Points = pts, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
                line.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brushKey);
                var fillPts = new PointCollection(pts) { new WpfPoint(w, height), new WpfPoint(0, height) };
                var fill = new Polygon { Points = fillPts, Opacity = 0.18 };
                fill.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brushKey);
                canvas.Children.Add(fill);
                canvas.Children.Add(line);
            }
            canvas.SizeChanged += (_, _) => Draw();
            canvas.Loaded += (_, _) => Draw();
            if (values.Count < 2)
                return new Grid { Children = { canvas, new TextBlock { Text = PSim("Collecting", "Collecting data…"), FontSize = Ui.FontXS, VerticalAlignment = VAlign.Center, HorizontalAlignment = HAlign.Center, Opacity = 0.7 } } };
            return canvas;
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        //  Battery tab
        // ════════════════════════════════════════════════════════════════════════════════════════

        private SegmentRing? _prChargeRing, _prHealthRing;
        private TextBlock? _prChargeState;
        private StackPanel? _prBatChartHost;

        private void RenderBattery(SMDWin.Models.BatteryInfo info)
        {
            if (!_prInit || _prBatBody == null || _prHealthBody == null) return;
            try
            {
                RenderBatteryCard(info);
                RenderHealthCard(info);
                UpdatePowerTabs();
            }
            catch (Exception ex) { AppLogger.Debug($"RenderBattery: {ex.Message}"); }
        }

        private void RenderBatteryCard(SMDWin.Models.BatteryInfo info)
        {
            _prBatBody!.Children.Clear();
            // the battery name next to the title
            if (_prBatCard?.Child is Grid root && root.Children[0] is DockPanel head)
            {
                var old = head.Children.OfType<TextBlock>().FirstOrDefault(t => (t.Tag as string) == "name");
                if (old != null) head.Children.Remove(old);
                // generic names say nothing ("Primary", "Battery", "Default")
                string bn = info.Name?.Trim() ?? "";
                if (bn.Length > 0 && bn.ToLowerInvariant() is not ("primary" or "battery" or "default" or "internal battery" or "system battery"))
                {
                    var n = PrText(bn, "TextSecondaryBrush", Ui.FontXS);
                    n.Tag = "name"; n.Margin = new Thickness(10, 0, 0, 0);
                    DockPanel.SetDock(n, Dock.Right);
                    head.Children.Insert(head.Children.Count - 1, n);
                    n.HorizontalAlignment = HAlign.Left;
                }
            }

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // the charge ring with the state under it
            var left = new StackPanel { Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VAlign.Top };
            double ringSize = _prBig ? 148 : 124;
            _prChargeRing ??= new SegmentRing();
            Detach(_prChargeRing);
            _prChargeRing.Width = _prChargeRing.Height = ringSize;
            string zone = BatteryBlocks.ZoneOf(info.ChargePercent) switch { BatteryZone.Crit => "StatusProblemBrush", BatteryZone.Warn => "StatusCheckBrush", _ => "StatusGoodBrush" };
            _prChargeRing.Show(info.ChargePercent, zone);
            left.Children.Add(_prChargeRing);
            _prChargeState = PrText(info.IsCharging ? "⚡ " + PSim("StateCharging", "Charging")
                                    : info.IsFull ? PSim("StateFull", "Full")
                                    : info.IsAC ? PSim("StatePlugged", "Plugged in, not charging")
                                    : PSim("StateOnBattery", "On battery"),
                                    info.IsCharging || info.IsFull ? "StatusGoodBrush" : "TextSecondaryBrush", Ui.FontS, bold: true);
            _prChargeState.HorizontalAlignment = HAlign.Center;
            _prChargeState.Margin = new Thickness(0, 8, 0, 0);
            left.Children.Add(_prChargeState);
            g.Children.Add(left);

            // the figures
            var right = new StackPanel();
            var tiles = new UniformGrid { Columns = _prBig ? 3 : 2 };
            void Tile(string label, string value, string sub)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                var sp = new StackPanel();
                var l = PrText(label.ToUpperInvariant(), "TextSecondaryBrush", Ui.FontXS);
                sp.Children.Add(l);
                var v = PrText(value, "TextPrimaryBrush", Ui.FontXL, bold: true);
                if (TryFindResource("NumFont") is System.Windows.Media.FontFamily nf) v.FontFamily = nf;
                sp.Children.Add(v);
                if (sub.Length > 0) sp.Children.Add(PrText(sub, "TextSecondaryBrush", Ui.FontXS));
                var b = PrSurface(sp, new Thickness(10, 6, 10, 6));
                b.Margin = new Thickness(0, 0, 8, 8);
                tiles.Children.Add(b);
            }
            string Legacy(string name) => FindName(name) is TextBlock t ? t.Text.Trim() : "";
            if (info.IsCharging && info.ChargePowerW > 0) Tile(PSim("TChargingAt", "Charging at"), $"{info.ChargePowerW:F0} W", PSim("TFromCharger", "from the charger"));
            else if (!info.IsAC && info.DischargePowerW > 0) Tile(PSim("TUsingNow", "Using now"), $"{info.DischargePowerW:F1} W", PSim("TWholeLaptop", "the whole laptop"));
            else if (!info.IsAC && PowerFromChargeDrop(info) is double wd)
                Tile(PSim("TUsingNow", "Using now"), $"≈ {wd:F1} W", PSim("TFromDrop", "from the charge drop"));
            else if (!info.IsAC)
                Tile(PSim("TUsingNow", "Using now"), "…", PSim("TWaitDrop", "the battery reports no power; worked out in a few minutes"));
            if (info.IsCharging) Tile(PSim("TTimeToFull", "Time to full"), Legacy("TxtBatTimeToFull"), PSim("TAtThisRate", "at this rate"));
            else if (!info.IsAC)
            {
                // the battery's estimate, else Windows' own, else the older page's text
                string timeLeft = info.EstimatedRuntime is > 0 and < 999 ? FmtHours(info.EstimatedRuntime / 60.0)
                                : PowerAdvancedService.GetBatteryLifeSeconds() is int sec ? FmtHours(sec / 3600.0)
                                : Legacy("TxtBatRuntime");
                Tile(PSim("TTimeLeft", "Time left"), timeLeft.Length > 0 ? timeLeft : "…", PSim("TAtThisRate", "at this rate"));
            }
            if (info.CycleCount > 0) Tile(PSim("TCycles", "Charge cycles"), info.CycleCount.ToString(), PSim("TCyclesSub", "usually 500–1000"));
            if (info.VoltageV > 0) Tile(PSim("TVoltage", "Voltage"), $"{info.VoltageV:F1} V", info.Chemistry ?? "");
            if (_prBig && info.FullCapacityMWh > 0)
                Tile(PSim("TCapacityNow", "Capacity now"), $"{info.FullCapacityMWh * info.ChargePercent / 100000.0:F1} Wh", string.Format(PSim("TOfWh", "of {0:F1} Wh"), info.FullCapacityMWh / 1000.0));
            if (_prBig) Tile(PSim("TPlanOnBattery", "Plan on battery"), PlanTitle(PlanFor("dc")), PSim("TPlanTab", "Power & sleep tab"));
            right.Children.Add(tiles);
            if (FindName("BatVerdictBox") is FrameworkElement verdict)
            {
                Detach(verdict);
                verdict.Margin = new Thickness(0, 0, 8, 0);
                right.Children.Add(verdict);
            }
            Grid.SetColumn(right, 1);
            g.Children.Add(right);
            _prBatBody.Children.Add(g);

            // large screen: the power over the last 10 minutes
            _prBatChartHost = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Visibility = _prBig ? Visibility.Visible : Visibility.Collapsed };
            _prBatBody.Children.Add(_prBatChartHost);
            RenderBatteryChartOnly();
        }

        private void RenderBatteryChartOnly()
        {
            if (_prBatChartHost == null || !_prBig) return;
            _prBatChartHost.Children.Clear();
            var head = new DockPanel { LastChildFill = true };
            if (_batDrawHist.Count >= 2)
            {
                var stats = PrText(string.Format(PSim("DrawStats", "min {0:F0} W · avg {1:F0} W · max {2:F0} W"),
                    _batDrawHist.Min(p => p.W), _batDrawHist.Average(p => p.W), _batDrawHist.Max(p => p.W)), "TextSecondaryBrush", Ui.FontXS);
                DockPanel.SetDock(stats, Dock.Right);
                head.Children.Add(stats);
            }
            head.Children.Add(PrText(_lastBatteryInfo?.IsCharging == true ? PSim("DrawTitleCharge", "Charging power · last 10 min") : PSim("DrawTitle", "Power draw · last 10 min"), "TextPrimaryBrush", Ui.FontS, bold: true));
            _prBatChartHost.Children.Add(head);
            _prBatChartHost.Children.Add(PrSparkline(_batDrawHist.Select(p => p.W).ToList(), 54, "DataBrush"));
        }

        private void RenderHealthCard(SMDWin.Models.BatteryInfo info)
        {
            _prHealthBody!.Children.Clear();
            int? health = info.WearPct is int w ? Math.Clamp(100 - w, 0, 100) : null;
            string key = health is null ? "TextSecondaryBrush" : health >= 80 ? "StatusGoodBrush" : health >= 40 ? "StatusCheckBrush" : "StatusProblemBrush";
            if (_prHealthChip != null)
            {
                _prHealthChip.Child = health is null ? null
                    : DiagChip(health >= 80 ? PSim("HealthGood", "Good") : health >= 40 ? PSim("HealthWorn", "Worn") : PSim("HealthReplace", "Replace soon"), key);
            }
            var top = new Grid { Margin = new Thickness(0, 4, 0, 12) };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _prHealthRing ??= new SegmentRing();
            Detach(_prHealthRing);
            _prHealthRing.Width = _prHealthRing.Height = _prBig ? 104 : 88;
            if (health is int hv) _prHealthRing.Show(hv, key); else { _prHealthRing.HasValue = false; _prHealthRing.InvalidateVisual(); }
            _prHealthRing.Margin = new Thickness(0, 0, 16, 0);
            top.Children.Add(_prHealthRing);

            var col = new StackPanel { VerticalAlignment = VAlign.Center };
            double fullWh = info.FullCapacityMWh / 1000.0, designWh = info.DesignCapacityMWh / 1000.0;
            if (fullWh > 0 && designWh > 0)
            {
                var r1 = new DockPanel();
                var v1 = PrText($"{fullWh:F1} Wh", "TextPrimaryBrush", Ui.FontS, bold: true); DockPanel.SetDock(v1, Dock.Right); r1.Children.Add(v1);
                r1.Children.Add(PrText(PSim("HoldsNow", "Holds now"), "TextSecondaryBrush", Ui.FontS));
                col.Children.Add(r1);
                var bar = new Grid { Height = 8, Margin = new Thickness(0, 4, 0, 4) };
                double f = Math.Clamp(fullWh / designWh, 0, 1);
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(f, GridUnitType.Star) });
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - f), GridUnitType.Star) });
                var bg = new Border { CornerRadius = new CornerRadius(4) }; bg.SetResourceReference(Border.BackgroundProperty, "GaugeTrackBrush"); Grid.SetColumnSpan(bg, 2); bar.Children.Add(bg);
                var fg = new Border { CornerRadius = new CornerRadius(4) }; fg.SetResourceReference(Border.BackgroundProperty, key); bar.Children.Add(fg);
                col.Children.Add(bar);
                var r2 = new DockPanel();
                var v2 = PrText($"{designWh:F1} Wh", "TextSecondaryBrush", Ui.FontS); DockPanel.SetDock(v2, Dock.Right); r2.Children.Add(v2);
                r2.Children.Add(PrText(PSim("WhenNew", "When new"), "TextSecondaryBrush", Ui.FontS));
                col.Children.Add(r2);
            }
            // the health is in the ring; the wear would say the same thing again — only the cycles here
            if (info.WearPct is int)
            {
                if (info.CycleCount > 0) col.Children.Add(PrText(string.Format(PSim("CyclesLine", "{0} cycles"), info.CycleCount), "TextSecondaryBrush", Ui.FontXS));
            }
            else col.Children.Add(PrText(designWh <= 0 && fullWh > 0
                    ? PSim("NoDesign", "The battery does not report its capacity when new, so its wear cannot be worked out.")
                    : PSim("NoWear", "The battery does not report its capacity, so its health cannot be worked out."), "TextSecondaryBrush", Ui.FontXS, wrap: true));
            Grid.SetColumn(col, 1);
            top.Children.Add(col);
            _prHealthBody.Children.Add(top);

            // the details
            var rows = new List<(string K, string V)>
            {
                (PSim("DMaker", "Maker"), info.Manufacturer?.Trim() ?? ""),
                (PSim("DMade", "Made"), info.ManufactureDate?.Trim() ?? ""),
                (PSim("DChemistry", "Chemistry"), info.Chemistry?.Trim() ?? ""),
            };
            if (designWh > 0) rows.Add((PSim("DDesign", "Design capacity"), $"{designWh:F1} Wh"));
            if (fullWh > 0) rows.Add((PSim("DFull", "Full charge now"), $"{fullWh:F1} Wh"));
            if (info.CycleCount > 0) rows.Add((PSim("TCycles", "Charge cycles"), info.CycleCount.ToString()));
            rows.Add((PSim("DSerial", "Serial"), info.SerialNumber?.Trim() ?? ""));
            foreach (var (k, v) in rows.Where(r => r.V.Length > 0))
            {
                var d = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var vv = PrText(v, "TextPrimaryBrush", Ui.FontS, bold: true); DockPanel.SetDock(vv, Dock.Right); d.Children.Add(vv);
                d.Children.Add(PrText(k, "TextSecondaryBrush", Ui.FontS));
                _prHealthBody.Children.Add(d);
            }
        }

        private void RenderSaverCard()
        {
            if (_prSaverBody == null) return;
            _prSync = true;
            try
            {
                _prSaverBody.Children.Clear();
                bool? on = PowerAdvancedService.IsBatterySaverOn();
                if (_prSaverChip != null) _prSaverChip.Child = on is null ? null : DiagChip(on.Value ? PSim("SaverOnNow", "On now") : PSim("SaverOffNow", "Off now"), on.Value ? "StatusGoodBrush" : "TextSecondaryBrush");
                if (_bsThreshold is not int thr)
                {
                    _prSaverBody.Children.Add(PrText(PSim("SaverUnread", "The battery saver setting could not be read on this laptop."), "TextSecondaryBrush", Ui.FontS, wrap: true));
                    return;
                }
                var sentence = new TextBlock { FontSize = Ui.FontS, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
                sentence.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                void Sentence(int t)
                {
                    sentence.Inlines.Clear();
                    if (t <= 0) { sentence.Inlines.Add(new Run(PSim("SaverNever", "Never turns on by itself — only from the Windows quick settings."))); return; }
                    sentence.Inlines.Add(new Run(PSim("SaverAt1", "Turns on by itself when the battery drops to ")));
                    var b = new Run($"{t} %") { FontWeight = FontWeights.SemiBold };
                    b.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");
                    sentence.Inlines.Add(b);
                    sentence.Inlines.Add(new Run(_bsDims == true ? PSim("SaverAt2Dim", ": less background activity, lower brightness.") : PSim("SaverAt2", ": less background activity.")));
                }
                Sentence(thr);
                _prSaverBody.Children.Add(sentence);

                var slider = new Slider { Minimum = 0, Maximum = 100, TickFrequency = 5, IsSnapToTickEnabled = true, SmallChange = 5, LargeChange = 10, Value = thr, Margin = new Thickness(0, 0, 0, 8) };
                _prSaverBody.Children.Add(slider);
                var presets = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (int p in new[] { 10, 20, 30, 50, 0 })
                {
                    int val = p;
                    presets.Children.Add(PrSeg(p == 0 ? PSim("Never", "Never") : $"{p} %", thr == p, () => slider.Value = val));
                }
                var track = PrTrack(presets);
                track.Margin = new Thickness(0, 0, 0, 12);
                _prSaverBody.Children.Add(track);

                var debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                debounce.Tick += async (_, _) =>
                {
                    debounce.Stop();
                    int v = (int)Math.Round(slider.Value);
                    try
                    {
                        await Task.Run(() => PowerAdvancedService.SetBatterySaverThreshold(v));
                        _bsThreshold = v;
                        ToolActivityLog.Add("Power", $"Battery saver turns on at {v} %", "info");
                        ShowToastSuccess(v == 0 ? PSim("SaverSavedNever", "Battery saver: never by itself") : string.Format(PSim("SaverSaved", "Battery saver turns on at {0} %"), v));
                    }
                    catch (Exception ex) { AppDialog.Show(string.Format(LanguageService.S("Msg", "AdminRequiredFmt"), ex.Message), "SMD Win"); }
                    RenderSaverCard();
                };
                slider.ValueChanged += (_, _) =>
                {
                    Sentence((int)Math.Round(slider.Value));
                    if (_prSync) return;
                    debounce.Stop(); debounce.Start();
                };

                if (_bsDims is bool dims)
                {
                    var row = new DockPanel();
                    var tg = Toggle(dims, async d =>
                    {
                        try { await Task.Run(() => PowerAdvancedService.SetBatterySaverDims(d)); _bsDims = d; ToolActivityLog.Add("Power", $"Battery saver lowers brightness: {(d ? "yes" : "no")}", "info"); }
                        catch (Exception ex) { AppDialog.Show(string.Format(LanguageService.S("Msg", "AdminRequiredFmt"), ex.Message), "SMD Win"); }
                        RenderSaverCard();
                    });
                    DockPanel.SetDock(tg, Dock.Right);
                    row.Children.Add(tg);
                    row.Children.Add(PrText(PSim("SaverDim", "Lower the brightness on battery saver"), "TextPrimaryBrush", Ui.FontS));
                    _prSaverBody.Children.Add(row);
                }

                // what battery saver does, in short
                var what = PrText(PSim("SaverWhatTitle", "When battery saver is on"), "TextPrimaryBrush", Ui.FontS, bold: true);
                what.Margin = new Thickness(0, 16, 0, 6);
                _prSaverBody.Children.Add(what);
                void Point(string icon, string text, bool muted = false)
                {
                    var d = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                    var ic = FluentIcons.Box(icon, 14, muted ? "TextSecondaryBrush" : "DataBrush");
                    ic.Margin = new Thickness(0, 0, 10, 0);
                    ic.VerticalAlignment = VAlign.Top;
                    DockPanel.SetDock(ic, Dock.Left);
                    d.Children.Add(ic);
                    d.Children.Add(PrText(text, muted ? "TextSecondaryBrush" : "TextPrimaryBrush", Ui.FontS, wrap: true));
                    _prSaverBody.Children.Add(d);
                }
                Point("apps_list", PSim("SaverWhatApps", "Apps in the background are paused or slowed down"));
                Point("arrow_sync", PSim("SaverWhatSync", "Mail, calendar and other sync happen less often"));
                Point("arrow_download", PSim("SaverWhatUpdates", "Some Windows updates wait until it turns off"));
                Point("desktop", _bsDims == true ? PSim("SaverWhatDim", "The screen gets a little darker")
                                                 : PSim("SaverWhatNoDim", "The screen brightness stays as it is (switch above)"), muted: _bsDims != true);
                Point("plug_connected", PSim("SaverWhatOff", "It turns off by itself when the charger is plugged in"), muted: true);
            }
            catch (Exception ex) { AppLogger.Debug($"RenderSaverCard: {ex.Message}"); }
            finally { _prSync = false; }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        //  Reset to defaults
        // ════════════════════════════════════════════════════════════════════════════════════════

        private async Task ResetPowerDefaultsAsync()
        {
            bool lap = _psLaptop;
            string msg = lap
                ? PSim("DefaultsConfirmLaptop", "Go back to the recommended settings?\n\n• Balanced plan, processor Normal (plugged in and on battery)\n• Screen off after 10 min plugged in / 5 min on battery\n• Sleep after 30 min / 15 min, hibernate after 3 h on battery\n• Power button and lid: Sleep\n• Battery saver at 20 %")
                : PSim("DefaultsConfirm", "Go back to the recommended settings?\n\n• Balanced plan, processor Normal\n• Screen off after 10 min, sleep after 30 min\n• Hard disks stop after 20 min\n• Power button: Shut down");
            if (!AppDialog.Confirm(msg, PSim("Defaults", "Reset to defaults"))) return;
            bool canSleep = _pdStates == null || _pdStates.HasS3 || _pdStates.HasModernStandby;
            bool hib = _pdStates?.HasHibernate == true;
            try
            {
                await Task.Run(() =>
                {
                    void Try(string what, Action a) { try { a(); } catch (Exception ex) { AppLogger.Debug($"Power defaults {what}: {ex.Message}"); } }
                    Try("plan", () => PowerAdvancedService.SetActivePlan(PlanBalancedGuid));
                    Try("cpu", () =>
                    {
                        PowerAdvancedService.SetBoostMode(PowerAdvancedService.BoostMode.Enabled, applyToAc: true, applyToDc: true);
                        PowerAdvancedService.SetMinProcessorState(5, applyToAc: true, applyToDc: true);
                        PowerAdvancedService.SetMaxProcessorState(100, applyToAc: true, applyToDc: true);
                    });
                    Try("screen", () => { PowerAdvancedService.SetIdleMinutes("screen", 10, true); if (lap) PowerAdvancedService.SetIdleMinutes("screen", 5, false); });
                    if (canSleep) Try("sleep", () => { PowerAdvancedService.SetIdleMinutes("sleep", 30, true); if (lap) PowerAdvancedService.SetIdleMinutes("sleep", 15, false); });
                    Try("disk", () => { PowerAdvancedService.SetIdleMinutes("disk", 20, true); if (lap) PowerAdvancedService.SetIdleMinutes("disk", 10, false); });
                    if (lap && hib) Try("hib", () => { PowerAdvancedService.SetIdleMinutes("hib", 0, true); PowerAdvancedService.SetIdleMinutes("hib", 180, false); });
                    int btn = lap && canSleep ? 1 : 3;
                    Try("button", () => { PowerAdvancedService.SetButtonAction("button", btn, true); if (lap) PowerAdvancedService.SetButtonAction("button", btn, false); });
                    if (lap)
                    {
                        Try("lid", () => { PowerAdvancedService.SetButtonAction("lid", canSleep ? 1 : 0, true); PowerAdvancedService.SetButtonAction("lid", canSleep ? 1 : 0, false); });
                        Try("saver", () => { PowerAdvancedService.SetBatterySaverThreshold(20); PowerAdvancedService.SetBatterySaverDims(true); });
                    }
                });
                SettingsService.Current.PowerPlanAc = null;
                SettingsService.Current.PowerPlanDc = null;
                SettingsService.Save();
                ToolActivityLog.Add("Power", "Power settings reset to the recommended defaults", "info");
                ShowToastSuccess(PSim("DefaultsDone", "Back to the recommended settings"));
            }
            catch (Exception ex) { AppDialog.Show(string.Format(LanguageService.S("Msg", "AdminRequiredFmt"), ex.Message), "SMD Win"); }
            LoadActivePowerPlan();
            LoadAdvancedPowerSection();
            await RefreshPowerSimpleAsync();
        }
    }
}
