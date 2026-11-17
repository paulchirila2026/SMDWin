// MainWindow.UX.cs — Sidebar collapse, search, breadcrumb, status chips
// New functionality added in Round 2 refactor
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WpfColor  = System.Windows.Media.Color;
using WpfBrush  = System.Windows.Media.Brush;
using WpfButton = System.Windows.Controls.Button;
using WpfToolTip = System.Windows.Controls.ToolTip;
using Forms = System.Windows.Forms;
using SMDWin.Services;

namespace SMDWin.Views
{
    public partial class MainWindow
    {
        // ══════════════════════════════════════════════════════════════════════
        // SIDEBAR COLLAPSE / EXPAND
        // ══════════════════════════════════════════════════════════════════════
        private bool _sidebarCollapsed = false;
        private const double SidebarExpandedWidth  = 232;
        private const double SidebarCollapsedWidth = 80; // 72 cuts the Dashboard icon, 80 is comfortable
        // The 36 px button centred on the menu icons' column (10.11.2026: it was centred on the collapsed width, 40 px, while the
        // icons sit at 36 px — 10 panel margin + 6 button margin + 10 padding + 10 to the icon's centre — so it looked off to one side)
        private const double SidebarIconCenter = 36;
        private const double SidebarToggleLeft = SidebarIconCenter - 36 / 2.0;

        private void BtnSidebarCollapse_Click(object s, RoutedEventArgs e)
        {
            _sidebarCollapsed = !_sidebarCollapsed;
            AnimateSidebar(_sidebarCollapsed);
            // clicked with the mouse: no focus frame left on the button (it stays for keyboard use — Tab + Space / Enter)
            if (System.Windows.Input.InputManager.Current.MostRecentInputDevice is System.Windows.Input.MouseDevice)
                System.Windows.Input.Keyboard.ClearFocus();
        }

        private void AnimateSidebar(bool collapse)
        {
            double targetW = collapse ? SidebarCollapsedWidth : SidebarExpandedWidth;

            // Animate the SidebarBorder width directly (ColumnDefinition is Auto)
            if (SidebarBorder != null)
            {
                var anim = new DoubleAnimation
                {
                    To       = targetW,
                    Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                };
                SidebarBorder.BeginAnimation(FrameworkElement.WidthProperty, anim);
            }

            SetNavTextVisibility(!collapse);

            // Hide logo title text when collapsed; icon stays visible
            if (BtnAbout != null)
            {
                var titleTb = BtnAbout.Template?.FindName("TxtSidebarTitle", BtnAbout) as FrameworkElement;
                if (titleTb != null) titleTb.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
            }

            // Hide subtitle/search when collapsed; hamburger button always stays visible
            if (SidebarHeaderExtra != null)
                SidebarHeaderExtra.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
            // Hidden, not Collapsed: the header keeps its height, so the rows below do not move
            if (SidebarSearchBox != null)
                SidebarSearchBox.Visibility = collapse ? Visibility.Hidden : Visibility.Visible;
            // Center hamburger when search is hidden; adjust padding for narrower collapsed width
            if (BtnSidebarCollapse != null)
                BtnSidebarCollapse.Margin = collapse ? new Thickness(0) : new Thickness(0, 0, 6, 0);
            if (BtnSidebarCollapse?.Parent is Grid hdrGrid)
            {
                // the button stays in the same place in both states (the centre of the icon column in collapsed mode), so it can be
                // pressed again without moving the mouse
                hdrGrid.HorizontalAlignment = collapse
                    ? System.Windows.HorizontalAlignment.Left
                    : System.Windows.HorizontalAlignment.Stretch;
                hdrGrid.Margin = new Thickness(SidebarToggleLeft, 0, collapse ? 0 : 10, 0);
            }

            // The category separators: Hidden (not Collapsed) to keep the height, so the buttons stay in the same positions
            // collapsed/expanded
            foreach (var name in new[] { "NavCatPrincipal","NavCatHw","NavCatSys","NavCatDiag","NavCatCfg" })
            {
                if (FindName(name) is FrameworkElement el && el.Parent is Grid pg)
                {
                    pg.Visibility = collapse ? Visibility.Hidden : Visibility.Visible;
                    if (!collapse && SettingsService.Current?.EnableAnimations != false)
                        pg.BeginAnimation(UIElement.OpacityProperty,
                            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { BeginTime = TimeSpan.FromMilliseconds(110) });
                    else pg.BeginAnimation(UIElement.OpacityProperty, null);
                }
            }

            // The badges are handled below in the repositioning block (allBadgeNames)

            // Update collapse icon: hamburger ↔ chevron-right
            if (BtnSidebarCollapse != null)
            {
                var p = FindVisualChild<System.Windows.Shapes.Path>(BtnSidebarCollapse, "CollapseIcon");
                if (p != null)
                    p.Data = SMDWin.Services.FluentIcons.Get(collapse
                        ? "chevron_right"         // click to expand
                        : "line_horizontal_3");   // hamburger = click to collapse
            }

            // Status chips opacity
            foreach (var chipName in new[] {"ChipDrivers","ChipStorage","ChipRam","ChipBattery","ChipNetwork","ChipEvents","ChipCrash"})
            {
                if (FindName(chipName) is TextBlock chip && chip.Visibility != Visibility.Collapsed)
                    chip.Opacity = collapse ? 0 : 1;
            }

            var badgeExpanded  = new Thickness(0, -5, -8, 0);
            var badgeCollapsed = new Thickness(0, -3, 0, 0);
            var allBadgeNames = new[] { "BadgeDrivers", "BadgeEvents", "BadgeCrash", "BadgeTemp", "BadgeDisk", "BadgeBattery" };
            var badgeTxts  = new[] { "TxtBadgeDrivers", "TxtBadgeEvents", "TxtBadgeCrash", "TxtBadgeTemp", "TxtBadgeDisk", "TxtBadgeBattery" };
            for (int i = 0; i < allBadgeNames.Length; i++)
            {
                if (FindName(allBadgeNames[i]) is Border badge)
                {
                    badge.Opacity = 1; // always visible — the position is what changes
                    badge.Margin = collapse ? badgeCollapsed : badgeExpanded;
                    badge.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
                    System.Windows.Controls.Panel.SetZIndex(badge, collapse ? 10 : 0);
                    // Collapsed: shows a simple "!"; expanded: the original numeric text
                    if (FindName(badgeTxts[i]) is TextBlock tb && badge.Visibility == Visibility.Visible)
                    {
                        if (collapse)
                        {
                            if (tb.Text != "!")
                                badge.Tag = tb.Text;
                            tb.Text = "!";
                        }
                        else
                        {
                            if (badge.Tag is string savedText && savedText.Length > 0)
                                tb.Text = savedText;
                        }
                    }
                }
            }
        }

        // Map tag → short name for the collapsed tooltip
        private static readonly Dictionary<string, string> _navShortNames = new()
        {
            ["Dashboard"] = "Dashboard",    ["SysInfo"]   = "System Info",
            ["Stress"]    = "Temp & Stress", ["Benchmark"] = "Benchmark",
            ["Disk"]      = "Storage",       ["Ram"]       = "RAM",
            ["Battery"]   = "Battery",       ["Network"]   = "Network",
            ["Apps"]      = "Applications",  ["Services"]  = "Services",
            ["Processes"] = "Processes",     ["Startup"]   = "Startup",
            ["Events"]    = "Event Viewer",  ["Crash"]     = "BSOD / Crash",
            ["Drivers"]   = "Drivers",       ["Settings"]  = "Settings",
            ["Tools"]     = "Tools",         ["PowerShell"]= "PowerShell",
        };

        /// <summary>The menu buttons: the page list and Settings, pinned at the bottom.</summary>
        private IEnumerable<WpfButton> AllNavButtons()
        {
            if (NavStackPanel != null)
                foreach (var b in FindVisualChildren<WpfButton>(NavStackPanel)) yield return b;
            if (NavBottomPanel != null)
                foreach (var b in FindVisualChildren<WpfButton>(NavBottomPanel)) yield return b;
        }

        private void SetNavTextVisibility(bool visible)
        {
            if (NavStackPanel == null) return;
            // expanding: the labels fade in once the menu has mostly opened (they do not pop in while it is still narrow)
            bool fadeIn = visible && SettingsService.Current?.EnableAnimations != false && IsLoaded;
            var labelIn = fadeIn
                ? new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { BeginTime = TimeSpan.FromMilliseconds(110) }
                : null;
            void Show(TextBlock tb)
            {
                tb.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (labelIn != null) tb.BeginAnimation(UIElement.OpacityProperty, labelIn);
                else tb.BeginAnimation(UIElement.OpacityProperty, null);
            }
            foreach (var btn in AllNavButtons())
            {
                // Skip BtnToggleWidget — its label is managed separately
                if (btn.Name == "BtnToggleWidget") continue;

                if (btn.Content is StackPanel sp)
                {
                    foreach (var tb in sp.Children.OfType<TextBlock>()) Show(tb);
                }
                else if (btn.Content is Grid bg)
                {
                    foreach (var isp in FindVisualChildren<StackPanel>(bg))
                        foreach (var tb in isp.Children.OfType<TextBlock>()) Show(tb);
                    // the page label is a direct child of the button grid (column 1): without this it stayed visible and one or two letters
                    // showed stuck to the icon
                    foreach (var tb in bg.Children.OfType<TextBlock>()) Show(tb);

                    if (bg.ColumnDefinitions.Count > 0)
                    {
                        var iconCol = bg.Children.OfType<FrameworkElement>()
                            .FirstOrDefault(c => System.Windows.Controls.Grid.GetColumn(c) == 0);
                        if (iconCol is Grid iconGrid)
                        {
                            // the icon has the same fixed position (from the left) on all buttons: it is not centred in the animating width, so it
                            // does not slide differently from one button to another while collapsing / expanding
                            iconGrid.Margin = visible ? new Thickness(0, 0, 8, 0) : new Thickness(4, 0, 0, 0);
                            iconGrid.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;

                            var img = iconGrid.Children.OfType<System.Windows.Controls.Image>().FirstOrDefault();
                            if (img != null)
                                img.Margin = visible ? new Thickness(0, 0, 8, 0) : new Thickness(0);
                            // the icons (Viewbox) also have 8 px on the right — collapsed, the icon stayed further left than the selected button's background
                            foreach (var vb in iconGrid.Children.OfType<System.Windows.Controls.Viewbox>())
                            {
                                vb.Margin = visible ? new Thickness(0, 0, 8, 0) : new Thickness(0);
                                vb.HorizontalAlignment = visible ? System.Windows.HorizontalAlignment.Stretch : System.Windows.HorizontalAlignment.Center;
                                // the icons are larger when the label is missing
                                if (vb.Name.StartsWith("Ico")) vb.Width = vb.Height = visible ? 18 : 20;
                            }
                            if (!visible) { if (iconGrid.Tag is not double) iconGrid.Tag = iconGrid.Width; iconGrid.Width = 20; }
                            else if (iconGrid.Tag is double w0) iconGrid.Width = w0;
                        }
                        bg.HorizontalAlignment = visible
                            ? System.Windows.HorizontalAlignment.Stretch
                            : System.Windows.HorizontalAlignment.Left;
                    }
                }

                string tag = btn.Tag is object[] existing && existing.Length >= 1
                    ? existing[0]?.ToString() ?? ""
                    : btn.Tag?.ToString() ?? "";

                if (!string.IsNullOrEmpty(tag) && _navShortNames.TryGetValue(tag, out var shortName))
                {
                    if (!visible)
                    {
                        // Save the original tooltip only if not already saved (avoid double wrapping)
                        if (!(btn.Tag is object[]))
                        {
                            var orig = btn.ToolTip;
                            if (orig != null && !(orig is string os && os == shortName))
                                btn.Tag = new object[] { tag, orig };
                        }
                        btn.ToolTip = shortName;
                        ToolTipService.SetShowOnDisabled(btn, true);
                        ToolTipService.SetPlacement(btn, System.Windows.Controls.Primitives.PlacementMode.Right);
                    }
                    else
                    {
                        // Restore the original tag (string) and the original tooltip
                        if (btn.Tag is object[] saved && saved.Length == 2)
                        {
                            btn.Tag     = saved[0]; // restaurare string original
                            btn.ToolTip = saved[1];
                        }
                        ToolTipService.SetPlacement(btn, System.Windows.Controls.Primitives.PlacementMode.Mouse);
                    }
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // SIDEBAR SEARCH
        // ══════════════════════════════════════════════════════════════════════
        private static readonly Dictionary<string, string[]> _pageSearchTerms = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Dashboard"]  = new[]{ "dashboard","system summary","overview","health","score" },
            ["Stress"]     = new[]{ "stress","temperature","temp","cpu","gpu","heat" },
            ["Benchmark"]  = new[]{ "benchmark","score","mark","speed test","performance","index" },
            ["Disk"]       = new[]{ "disk","storage","hdd","ssd","smart","nvme","scan","benchmark" },
            ["Ram"]        = new[]{ "ram","memory","dimm","slot","bandwidth" },
            ["Battery"]    = new[]{ "battery","charge","power","voltage","runtime" },
            ["Network"]    = new[]{ "network","wifi","ping","dns","traceroute","port","lan","speed" },
            ["Apps"]       = new[]{ "apps","applications","installed","software" },
            ["Services"]   = new[]{ "services","windows service","sysMain","wsl","print","svchost" },
            ["Processes"]  = new[]{ "process","processes","task","cpu usage","memory usage","kill" },
            ["Startup"]    = new[]{ "startup","boot","autorun","autostart" },
            ["Tools"]      = new[]{ "tools","timer","shutdown","hibernate","optimize","usb","powershell","commands","cleanup" },
            ["Events"]     = new[]{ "events","event viewer","errors","warnings","log","bsod" },
            ["Crash"]      = new[]{ "crash","bsod","blue screen","dump","stop code" },
            ["Drivers"]    = new[]{ "drivers","driver","device","unsigned","outdated" },
            ["Settings"]   = new[]{ "settings","theme","language","refresh","interval","dark","light" },
        };

        private void SidebarSearch_TextChanged(object s, TextChangedEventArgs e)
        {
            var query = TxtSidebarSearch?.Text?.Trim() ?? "";
            if (SidebarSearchHint != null)
                SidebarSearchHint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (BtnSidebarSearchClear != null)
                BtnSidebarSearchClear.Visibility = query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (NavStackPanel == null) return;

            if (query.Length == 0)
            {
                // Restore all nav buttons and category separators
                foreach (var btn in AllNavButtons())
                    btn.Visibility = Visibility.Visible;
                foreach (var name in new[] {"NavCatPrincipal","NavCatHw","NavCatSys","NavCatDiag","NavCatCfg"})
                    if (FindName(name) is FrameworkElement el && el.Parent is Grid pg)
                        pg.Visibility = Visibility.Visible;
                return;
            }

            // Match: show only buttons whose tag matches the query
            var matchedTags = _pageSearchTerms
                .Where(kv => kv.Value.Any(term => term.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .Select(kv => kv.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var btn in AllNavButtons())
            {
                var tag = btn.Tag?.ToString() ?? "";
                if (string.IsNullOrEmpty(tag)) continue; // skip non-nav buttons
                btn.Visibility = matchedTags.Contains(tag) ? Visibility.Visible : Visibility.Collapsed;
            }

            // Hide category separators (they look odd when all children are hidden)
            foreach (var name in new[] {"NavCatPrincipal","NavCatHw","NavCatSys","NavCatDiag","NavCatCfg"})
                if (FindName(name) is FrameworkElement el && el.Parent is Grid pg)
                    pg.Visibility = Visibility.Collapsed;
        }

        private void SidebarSearchClear_Click(object s, RoutedEventArgs e)
        {
            if (TxtSidebarSearch != null) TxtSidebarSearch.Text = "";
        }

        // ── The search box gives the focus back ───────────────────────────────
        // The menu buttons do not take the keyboard focus, so once the search box had it, it kept it (and its highlighted
        // border) for good. Now a click anywhere outside the box, or Esc, releases it.
        private bool _sidebarSearchFocusHooked;

        private void SidebarSearch_GotKeyboardFocus(object s, System.Windows.Input.KeyboardFocusChangedEventArgs e)
        {
            if (_sidebarSearchFocusHooked) return;
            _sidebarSearchFocusHooked = true;
            PreviewMouseDown += (_, me) =>
            {
                if (TxtSidebarSearch == null || !TxtSidebarSearch.IsKeyboardFocusWithin) return;
                if (me.OriginalSource is DependencyObject d && SidebarSearchBox != null && IsInsideElement(d, SidebarSearchBox)) return;
                ReleaseSidebarSearchFocus();
            };
        }

        private void SidebarSearch_PreviewKeyDown(object s, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            if (TxtSidebarSearch != null) TxtSidebarSearch.Text = "";
            ReleaseSidebarSearchFocus();
            e.Handled = true;
        }

        private void ReleaseSidebarSearchFocus()
        {
            try
            {
                if (TxtSidebarSearch == null) return;
                var scope = System.Windows.Input.FocusManager.GetFocusScope(TxtSidebarSearch);
                if (scope != null) System.Windows.Input.FocusManager.SetFocusedElement(scope, null);
                System.Windows.Input.Keyboard.ClearFocus();
            }
            catch (Exception ex) { AppLogger.Debug($"ReleaseSidebarSearchFocus: {ex.Message}"); }
        }

        private static bool IsInsideElement(DependencyObject d, DependencyObject container)
        {
            for (var cur = d; cur != null; )
            {
                if (ReferenceEquals(cur, container)) return true;
                cur = cur is Visual || cur is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(cur)
                    : LogicalTreeHelper.GetParent(cur);
            }
            return false;
        }

        // ══════════════════════════════════════════════════════════════════════
        // BREADCRUMB — updated from NavigateTo
        // ══════════════════════════════════════════════════════════════════════
        private static readonly Dictionary<string, (string Page, string Desc)> _breadcrumbMap = new()
        {
            ["Dashboard"]  = ("Dashboard", "Overview of your system health"),
            ["SysInfo"]    = ("System Information", "Hardware and Windows details, component by component"),
            ["Stress"]     = ("Temp & Stress",  "CPU/GPU temperatures and stress testing"),
            ["Benchmark"]  = ("Benchmark",      "Marks from 0 to 10 for the processor, memory, storage and graphics card"),
            ["Disk"]       = ("Storage",         "Disk health, SMART data and benchmarks"),
            ["Ram"]        = ("RAM Memory",      "Module info, slots and bandwidth"),
            ["Battery"]    = ("Battery",         "Charge level, health and power usage"),
            ["Network"]    = ("Network",         "Adapters, ping, DNS and port scan"),
            ["Apps"]       = ("Applications",    "Installed software and versions"),
            ["Services"]   = ("Windows Services","Start, stop and manage services"),
            ["Processes"]  = ("Process Monitor", "Live CPU and RAM per process"),
            ["Startup"]    = ("Startup Manager", "Programs that run at boot"),
            ["Tools"]      = ("Tools",           "Timer, cleanup, optimize and PowerShell"),
            ["Events"]     = ("Event Viewer",    "Windows errors, warnings and system logs"),
            ["Crash"]      = ("BSOD / Crash",    "Blue screen dumps and stop codes"),
            ["Drivers"]    = ("Drivers",         "Device drivers — missing, unsigned or failing"),
            ["Settings"]   = ("Settings",        "Theme, language and refresh intervals"),
        };

        private void UpdateBreadcrumb(string panel)
        {
            if (!_breadcrumbMap.TryGetValue(panel, out var bc)) return;
            if (BreadcrumbPage != null) BreadcrumbPage.Text = bc.Page;
            if (BreadcrumbDesc != null) BreadcrumbDesc.Text = bc.Desc;
            if (BreadcrumbSub  != null) BreadcrumbSub.Text  = "";
        }


        // ══════════════════════════════════════════════════════════════════════
        // PROGRESS BAR — dynamic width based on parent (replaced hardcoded 200px)
        // ══════════════════════════════════════════════════════════════════════
        private double GetShutdownProgressMaxWidth()
        {
            if (ShutdownProgressBar?.Parent is Border track)
                return Math.Max(0, track.ActualWidth - 2);
            return 400; // safe fallback wider than before
        }

        public void UpdateShutdownProgress(double pct)
        {
            // the block clock (Tools): the lit blocks = the remaining time; off when the timer is not running
            if (FindName("ShutdownGauge") is PercentGauge g)
            {
                bool running = _shutdownTotalSeconds > 0 && pct < 1;
                g.HasValue = running;
                g.Value = running ? (1 - Math.Clamp(pct, 0, 1)) * 100 : 0;
                g.InvalidateVisual();
            }
            if (ShutdownProgressBar == null) return;
            double maxW = GetShutdownProgressMaxWidth();
            ShutdownProgressBar.Width = Math.Clamp(pct, 0, 1) * maxW;
        }

        // ══════════════════════════════════════════════════════════════════════
        // RICH TOOLTIPS — attach to key buttons on load
        // ══════════════════════════════════════════════════════════════════════
        private void AttachRichTooltips()
        {
            AttachTooltip("BtnCmdSfc",
                "SFC /scannow",
                "Scans all protected Windows system files and replaces corrupted files with cached copies.",
                "Risk: None", "Reversible: N/A", "Duration: 5–20 min");

            AttachTooltip("BtnCmdDism",
                "DISM /RestoreHealth",
                "Repairs the Windows component store using Windows Update as source. Run after SFC if it fails.",
                "Risk: None", "Reversible: N/A", "Duration: 10–30 min");

            AttachTooltip("BtnCmdChkdsk",
                "chkdsk C: /f /r",
                "Schedules a disk check on next reboot. Finds and fixes file system errors and bad sectors.",
                "Risk: Low", "Reversible: N/A", "Duration: 30–90 min at boot");

            AttachTooltip("BtnOptimizePerf",
                "Performance Optimizer",
                "Selectively disable telemetry, animations, background services, and startup items.",
                "Risk: Low–Medium", "Reversible: Yes — each tweak can be undone", "12 tweaks available");

            AttachTooltip("BtnToggleHibernate",
                "Hibernate Toggle",
                "Disabling hibernate removes hiberfil.sys, freeing space equal to your installed RAM.",
                "Risk: Low", "Reversible: Yes", "Typical saving: 8–32 GB");

            AttachTooltip("BtnFullBatteryReport",
                "Full Battery Report",
                "Runs powercfg /batteryreport to generate a detailed HTML report with charge/discharge history.",
                "Risk: None", "Requires: No special permissions", "Output: HTML file");

            AttachTooltip("BtnStartShutdown",
                "Shutdown Timer",
                "Schedules a clean shutdown via shutdown /s /f /t. Cancel at any time with the Stop button.",
                "Risk: Unsaved work will be lost", "Cancel with: Stop Timer button", "");
        }

        private void AttachTooltip(string btnName, string title, string desc, string line1 = "", string line2 = "", string line3 = "")
        {
            if (FindName(btnName) is not System.Windows.Controls.Button btn) return;
            var sp = new StackPanel { MaxWidth = 260 };
            sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = Ui.FontM, Margin = new Thickness(0,0,0,4) });
            sp.Children.Add(new TextBlock { Text = desc, TextWrapping = TextWrapping.Wrap, FontSize = Ui.FontXS, Margin = new Thickness(0,0,0,4), Opacity = 0.85 });
            foreach (var line in new[]{line1,line2,line3}.Where(l => l.Length > 0))
            {
                sp.Children.Add(new TextBlock { Text = line, FontSize = Ui.FontXS, Opacity = 0.65, Margin = new Thickness(0,1,0,0) });
            }
            var tt = new WpfToolTip { Content = sp };
            System.Windows.Controls.ToolTipService.SetInitialShowDelay(btn, 800);
            System.Windows.Controls.ToolTipService.SetShowDuration(btn, 8000);
            btn.ToolTip = tt;
        }

        // Visual tree helpers are defined in MainWindow.xaml.cs
    }

    // ══════════════════════════════════════════════════════════════════════════
    // GridLength animation helper (WPF doesn't have this built-in)
    // ══════════════════════════════════════════════════════════════════════════
    public class GridLengthAnimation : AnimationTimeline
    {
        public GridLength From { get; set; }
        public GridLength To   { get; set; }
        public IEasingFunction? EasingFunction { get; set; }

        public override Type TargetPropertyType => typeof(GridLength);

        protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

        public override object GetCurrentValue(object defaultOriginValue,
            object defaultDestinationValue, AnimationClock animationClock)
        {
            double progress = animationClock.CurrentProgress ?? 0;
            if (EasingFunction != null) progress = EasingFunction.Ease(progress);
            return new GridLength(From.Value + (To.Value - From.Value) * progress);
        }
    }
}
