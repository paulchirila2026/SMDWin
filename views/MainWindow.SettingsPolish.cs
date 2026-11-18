using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SMDWin.Services;
using Brush       = System.Windows.Media.Brush;
using Brushes     = System.Windows.Media.Brushes;
using Color       = System.Windows.Media.Color;
using Colors      = System.Windows.Media.Colors;
using Cursors     = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;
using Path        = System.Windows.Shapes.Path;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment   = System.Windows.VerticalAlignment;

namespace SMDWin.Views
{
    /// <summary>
    /// Settings, the polish on top of the XAML rows: an icon tile on every row, a "Recommended" badge on a few,
    /// and for every option that differs from its default a small dot after the title plus a "Default" link that puts it back.
    /// The rows themselves (and their handlers) stay in SettingsPage.xaml; this only decorates them once.
    /// </summary>
    public partial class MainWindow
    {
        private const string IcoMonitor = "F1 M6.75,22 A0.75,0.75 0 0 1 6.65,20.5 L8.5,20.5 L8.5,18 L4.25,18 C3.05,18 2.08,17.08 2.01,15.9 L2,15.76 L2,5.25 C2,4.05 2.93,3.08 4.1,3.01 L4.25,3 L19.75,3 C20.94,3 21.91,3.93 21.99,5.1 L21.99,15.75 C21.99,16.95 21.07,17.92 19.9,18 L15.5,18 L15.5,20.5 L17.25,20.5 A0.75,0.75 0 0 1 17.35,22 L6.75,22 M14,18 L10,18 L10,20.5 L14,20.5 Z M19.75,4.5 L4.25,4.5 C3.87,4.5 3.55,4.78 3.51,5.15 L3.5,5.25 L3.5,15.75 C3.5,16.13 3.78,16.45 4.15,16.5 L19.75,16.5 C20.13,16.5 20.45,16.22 20.49,15.85 L20.49,5.25 C20.49,4.87 20.22,4.55 19.85,4.51 Z";
        private const string IcoThermo = "F1 M12,2 A3.25,3.25 0 0 1 15.24,5.07 L15.25,5.25 L15.25,13.2 L15.33,13.27 A5,5 0 0 1 16.97,16.49 L16.99,16.75 L17,17 A5,5 0 1 1 8.49,13.44 L8.67,13.27 L8.75,13.2 L8.75,5.25 A3.25,3.25 0 0 1 11.63,2.02 L11.82,2.01 Z M12,3.5 C11.08,3.5 10.33,4.2 10.26,5.1 L10.25,5.25 L10.25,13.95 L9.95,14.17 A3.5,3.5 0 1 0 14.22,14.3 L14.06,14.17 L13.76,13.95 L13.75,5.25 C13.75,4.28 12.97,3.5 12,3.5 M12,8 C12.41,8 12.75,8.34 12.75,8.75 L12.75,14.61 A2.5,2.5 0 1 1 11.25,14.61 L11.25,8.75 C11.25,8.34 11.59,8 12,8";
        private const string IcoGauge = "F1 M7.93,16.07 A0.75,0.75 0 1 1 6.87,17.13 A7.25,7.25 0 0 1 13.67,4.94 A0.75,0.75 0 1 1 13.33,6.4 A5.75,5.75 0 0 0 7.93,16.07 M17.89,9.14 C18.28,9 18.71,9.21 18.84,9.6 A7.3,7.3 0 0 1 17.13,17.13 A0.75,0.75 0 1 1 16.07,16.07 A5.8,5.8 0 0 0 17.43,10.1 A0.75,0.75 0 0 1 17.89,9.14 M15.88,6.67 A0.63,0.63 0 0 1 16.84,7.43 L16.71,7.68 A355,355 0 0 1 13.69,13.35 C13.57,13.55 13.45,13.75 13.37,13.85 A1.88,1.88 0 0 1 10.43,11.52 Q10.57,11.36 10.84,11.1 L11.52,10.47 A154,154 0 0 1 15.67,6.85 Z M22,12 A10,10 0 0 1 2,12 A10,10 0 0 1 22,12 M3.5,12 A8.5,8.5 0 0 0 20.5,12 A8.5,8.5 0 0 0 3.5,12";
        private const string IcoDoc = "F1 M8.75,11.5 A0.75,0.75 0 0 0 8.75,13 L15.25,13 A0.75,0.75 0 0 0 15.25,11.5 Z M8.75,14.25 A0.75,0.75 0 0 0 8.75,15.75 L15.25,15.75 A0.75,0.75 0 0 0 15.25,14.25 Z M8.75,17 A0.75,0.75 0 0 0 8.75,18.5 L15.25,18.5 A0.75,0.75 0 0 0 15.25,17 Z M13.59,2.59 L19.4,8.4 A2,2 0 0 1 20,9.83 L20,20 A2,2 0 0 1 18,22 L6,22 A2,2 0 0 1 4,20 L4,4 C4,2.9 4.9,2 6,2 L12.17,2 C12.69,2 13.22,2.22 13.59,2.59 M18,20.5 A0.5,0.5 0 0 0 18.5,20 L18.5,10 L14,10 A2,2 0 0 1 12,8 L12,3.5 L6,3.5 A0.5,0.5 0 0 0 5.5,4 L5.5,20 C5.5,20.27 5.72,20.5 6,20.5 Z M17.38,8.5 L13.5,4.62 L13.5,8 C13.5,8.28 13.72,8.5 14,8.5 Z";
        private const string IcoBell = "F1 M12,2 A7.5,7.5 0 0 1 19.5,9.25 L19.5,13.6 L20.88,16.75 A1.25,1.25 0 0 1 19.73,18.5 L15,18.5 A3,3 0 0 1 9,18.68 L9,18.5 L4.27,18.5 A1.25,1.25 0 0 1 3.13,16.75 L4.5,13.6 L4.5,9.5 C4.5,5.35 7.85,2 12,2 M13.5,18.5 L10.5,18.5 A1.5,1.5 0 0 0 13.5,18.65 Z M12,3.5 C8.68,3.5 6,6.17 6,9.5 L6,13.9 L4.66,17 L19.36,17 L18,13.9 L18,9.29 A6,6 0 0 0 12,3.51";

        /// <summary>One option with a known default: a switch (Default = on/off) or a group of chips (Default = one chip).</summary>
        private sealed record SetDefault(string Control, bool? Toggle = null, string? Radio = null, string[]? Others = null);

        private static readonly SetDefault[] SettingsDefaults =
        {
            new("ChkStartWithWindows", Toggle: false),
            new("RbCloseQuit", Radio: "RbCloseQuit", Others: new[] { "RbCloseTray" }),
            new("RbTempUnitC", Radio: "RbTempUnitC", Others: new[] { "RbTempUnitF" }),
            new("ChkEnableNotifications", Toggle: true),
            new("RbCpuLimAuto", Radio: "RbCpuLimAuto", Others: new[] { "RbCpuLim85", "RbCpuLim90", "RbCpuLim95", "RbCpuLim100" }),
            new("RbGpuLimAuto", Radio: "RbGpuLimAuto", Others: new[] { "RbGpuLim85", "RbGpuLim90", "RbGpuLim95", "RbGpuLim100" }),
            new("RbThemeAuto", Radio: "RbThemeAuto", Others: new[] { "RbThemeLight", "RbThemeDark" }),
            new("RbDensityAuto", Radio: "RbDensityAuto", Others: new[] { "RbDensityCompact", "RbDensityNormal" }),
            new("ChkAnimations", Toggle: true),
            new("ChkAuroraAlert", Toggle: true),
            new("RbBgRecFull", Radio: "RbBgRecFull", Others: new[] { "RbBgRecReduced", "RbBgRecOff" }),
            new("ChkAutoSnapshotStress", Toggle: true),
            new("ChkWidgetEnabled", Toggle: false),
            new("ChkTraySensors", Toggle: true),
            new("RbSpeedNormal", Radio: "RbSpeedNormal", Others: new[] { "RbSpeedSaver", "RbSpeedFast" }),
            new("ChkSlowerOnBattery", Toggle: true),
            new("TglSensorLogger", Toggle: false),
            new("ChkExportSensorsReg", Toggle: false),
            new("RbDriverSearchGoogle", Radio: "RbDriverSearchGoogle", Others: new[] { "RbDriverSearchDrp" }),
            new("ChkWorkshopMode", Toggle: false),
        };

        // the options most people should leave as they are
        private static readonly HashSet<string> SettingsRecommended = new() { "ChkEnableNotifications", "ChkSlowerOnBattery" };

        private static readonly Dictionary<string, string> SettingsRowIcons = new()
        {
            ["ChkStartWithWindows"] = IcoMonitor,
            ["RbTempUnitC"] = IcoThermo, ["RbCpuLimAuto"] = IcoThermo, ["RbGpuLimAuto"] = IcoThermo,
            ["ChkEnableNotifications"] = IcoBell,
            ["ChkAnimations"] = IcoGauge, ["RbBgRecFull"] = IcoGauge, ["RbSpeedNormal"] = IcoGauge,
            ["ChkAutoSnapshotStress"] = IcoDoc,
        };

        private bool _settingsDecorated;
        private readonly List<(SetDefault Spec, FrameworkElement Dot, FrameworkElement Link)> _settingsMarks = new();

        /// <summary>Runs once, the first time the Settings page is shown.</summary>
        private void DecorateSettingsRows()
        {
            if (_settingsDecorated) return;
            _settingsDecorated = true;
            try
            {
                var rowStyle = TryFindResource("SetRow") as Style;
                var iconStyle = TryFindResource("SetRowIcon") as Style;
                var titleStyle = TryFindResource("SetRowTitle") as Style;
                var controlStyle = TryFindResource("SetRowControl") as Style;

                foreach (var tag in SettingsTabTags)
                {
                    if (FindName("SetPanel" + tag) is not FrameworkElement panel) continue;
                    string? tabIcon = (FindName("BtnSetTab" + tag) as DependencyObject) is DependencyObject tb
                        ? LogicalDescendants(tb).OfType<Path>().FirstOrDefault()?.Data?.ToString() : null;
                    foreach (var row in LogicalDescendants(panel).OfType<Border>().Where(b => rowStyle != null && b.Style == rowStyle).ToList())
                    {
                        if (row.Visibility != Visibility.Visible && row.Name == "PnlWorkshop") continue;
                        var spec = SettingsDefaults.FirstOrDefault(d => LogicalDescendants(row).OfType<FrameworkElement>().Any(f => f.Name == d.Control));
                        DecorateRow(row, spec, tabIcon, iconStyle, titleStyle, controlStyle);
                    }
                }
                var page = FindName("PanelSettings") as UIElement;
                if (page != null)
                {
                    RoutedEventHandler h = (_, _) => Dispatcher.BeginInvoke(new Action(RefreshSettingsMarks), System.Windows.Threading.DispatcherPriority.Background);
                    page.AddHandler(ToggleButton.CheckedEvent, h, true);
                    page.AddHandler(ToggleButton.UncheckedEvent, h, true);
                    page.AddHandler(ButtonBase.ClickEvent, h, true);
                }
                RefreshSettingsMarks();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "DecorateSettingsRows"); }
        }

        private void DecorateRow(Border row, SetDefault? spec, string? tabIcon, Style? iconStyle, Style? titleStyle, Style? controlStyle)
        {
            var dock = row.Child as DockPanel ?? (row.Child as StackPanel)?.Children.OfType<DockPanel>().FirstOrDefault();

            // ① the icon tile: the row's own icon when it has one, otherwise one for the option, otherwise the tab's icon
            if (dock != null && !dock.Children.OfType<System.Windows.Shapes.Ellipse>().Any())
            {
                string? data = null;
                var old = dock.Children.OfType<Viewbox>().FirstOrDefault(v => iconStyle != null && v.Style == iconStyle);
                if (old != null) { data = LogicalDescendants(old).OfType<Path>().FirstOrDefault()?.Data?.ToString(); dock.Children.Remove(old); }
                if (data == null && spec != null) SettingsRowIcons.TryGetValue(spec.Control, out data);
                data ??= tabIcon;
                if (data != null)
                {
                    var tile = new Border
                    {
                        Width = 34, Height = 34, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 14, 0),
                        VerticalAlignment = VerticalAlignment.Center, Background = SummaryTint("AccentBrush", 24),
                    };
                    DockPanel.SetDock(tile, Dock.Left);
                    var p = new Path { Data = Geometry.Parse(data), Stretch = Stretch.Uniform, Width = 16, Height = 16 };
                    p.SetResourceReference(Shape.FillProperty, "AccentBrush");
                    tile.Child = p;
                    dock.Children.Insert(0, tile);
                }
            }

            // ② the title line: title · dot · badge
            var title = LogicalDescendants(row).OfType<TextBlock>().FirstOrDefault(t => titleStyle != null && t.Style == titleStyle);
            if (title == null || title.Parent is not System.Windows.Controls.Panel holder) return;
            bool recommended = spec != null && SettingsRecommended.Contains(spec.Control);
            if (spec == null && !recommended) return;

            int at = holder.Children.IndexOf(title);
            holder.Children.RemoveAt(at);
            var line = new WrapPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(title);
            title.VerticalAlignment = VerticalAlignment.Center;
            holder.Children.Insert(at, line);

            if (recommended)
            {
                var badge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(8, 0, 0, 0),
                                         VerticalAlignment = VerticalAlignment.Center, Background = SummaryTint("StatusOkBrush", 34) };
                var bt = new TextBlock { Text = PT("Recommended"), FontSize = 11, FontWeight = FontWeights.SemiBold };
                bt.SetResourceReference(TextBlock.ForegroundProperty, "StatusOkBrush");
                badge.Child = bt;
                line.Children.Add(badge);
            }
            if (spec == null) return;

            var dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(7, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
                                    ToolTip = PT("Changed from the default") };
            dot.SetResourceReference(Shape.FillProperty, "AccentBrush");
            line.Children.Insert(1, dot);

            var link = new TextBlock { Text = PT("Default"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand,
                                       VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
                                       ToolTip = PT("Put this option back to its default") };
            link.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            link.MouseLeftButtonUp += (_, e) => { e.Handled = true; ResetSettingToDefault(spec); };

            // the link goes right of the control (rows with a control column), otherwise after the title
            var ctl = dock?.Children.OfType<Border>().FirstOrDefault(b => controlStyle != null && b.Style == controlStyle);
            if (ctl?.Child is UIElement inner)
            {
                ctl.Child = null;
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(inner);
                link.Margin = new Thickness(12, 0, 0, 0);
                sp.Children.Add(link);
                ctl.Child = sp;
            }
            else { link.Margin = new Thickness(12, 0, 0, 0); line.Children.Add(link); }

            _settingsMarks.Add((spec, dot, link));
        }

        private bool IsSettingAtDefault(SetDefault spec)
        {
            if (spec.Toggle is bool def)
                return (FindName(spec.Control) as ToggleButton)?.IsChecked == def;
            if (spec.Radio != null)
            {
                if ((FindName(spec.Radio) as ToggleButton)?.IsChecked == true) return true;
                // nothing chosen in the group (not loaded yet) counts as default
                return spec.Others?.All(o => (FindName(o) as ToggleButton)?.IsChecked != true) ?? true;
            }
            return true;
        }

        private void RefreshSettingsMarks()
        {
            foreach (var (spec, dot, link) in _settingsMarks)
            {
                bool changed = !IsSettingAtDefault(spec);
                dot.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
                link.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void ResetSettingToDefault(SetDefault spec)
        {
            try
            {
                if (spec.Toggle is bool def && FindName(spec.Control) is ToggleButton t)
                {
                    t.IsChecked = def;
                    // some switches save on Click, not on Checked
                    t.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, t));
                }
                else if (spec.Radio != null && FindName(spec.Radio) is ToggleButton r)
                    r.IsChecked = true;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ResetSettingToDefault " + spec.Control); }
            RefreshSettingsMarks();
        }

        // ── About tab ────────────────────────────────────────────────────────────

        private bool _settingsAboutBuilt;

        private void BuildSettingsAbout()
        {
            if (_settingsAboutBuilt || FindName("SetAboutHost") is not Border host) return;
            _settingsAboutBuilt = true;
            var rowStyle = TryFindResource("SetRow") as Style;
            var titleStyle = TryFindResource("SetRowTitle") as Style;
            var descStyle = TryFindResource("SetRowDesc") as Style;
            var list = new StackPanel();

            void Row(string title, string desc, string button, Action act)
            {
                var b = new Border { Style = rowStyle };
                var d = new DockPanel();
                var btn = new Wpf.Ui.Controls.Button { Content = button, Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary, Padding = new Thickness(14, 5, 14, 5),
                                                       VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
                btn.Click += (_, _) => act();
                DockPanel.SetDock(btn, Dock.Right);
                d.Children.Add(btn);
                var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(new TextBlock { Text = title, Style = titleStyle });
                sp.Children.Add(new TextBlock { Text = desc, Style = descStyle });
                d.Children.Add(sp);
                b.Child = d;
                list.Children.Add(b);
            }

            int build = AppVersionInfo.Build;
            string ver = build > 0 ? $"{build}{(AppVersionInfo.DevBuild ? " dev" : "")}" : "dev";
            Row("SMD Win", PT("Build") + " " + ver + " · GPL-3.0 · Paul Chirilă", PT("Details"), () => BtnAbout_Click(this, new RoutedEventArgs()));
            Row(PT("Updates"), PT("Look for a newer version on GitHub"), PT("Check now"), () => _ = CheckUpdatesFromSettingsAsync());
            Row(PT("Changes in each version"), PT("The list of what changed, on GitHub"), PT("Open"),
                () => OpenSettingsUrl("https://github.com/paulchirila2026/SMDWin/blob/main/CHANGELOG.md"));
            Row(PT("Source code and problem reports"), "github.com/paulchirila2026/SMDWin", PT("Open"),
                () => OpenSettingsUrl("https://github.com/paulchirila2026/SMDWin"));
            host.Child = list;
        }

        private static void OpenSettingsUrl(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { AppLogger.Warning(ex, "OpenSettingsUrl"); }
        }

        private async System.Threading.Tasks.Task CheckUpdatesFromSettingsAsync()
        {
            try
            {
                var info = await new AutoUpdateService().CheckForUpdateAsync();
                if (!string.IsNullOrEmpty(info.Error))
                    AppDialog.Show(string.Format(PT("Update check failed: {0}"), info.Error), PT("Updates"), AppDialog.Kind.Warning, this);
                else if (info.Available)
                {
                    if (AppDialog.Confirm(string.Format(PT("Version {0} is available (this one is {1}). Open the download page?"), info.LatestVersion, info.CurrentVersion), PT("Updates"), this))
                        OpenSettingsUrl(string.IsNullOrEmpty(info.HtmlUrl) ? "https://github.com/paulchirila2026/SMDWin/releases" : info.HtmlUrl);
                }
                else
                    AppDialog.Show(string.Format(PT("This is the latest version ({0})."), info.CurrentVersion), PT("Updates"), AppDialog.Kind.Info, this);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "CheckUpdatesFromSettings"); }
        }
    }
}
