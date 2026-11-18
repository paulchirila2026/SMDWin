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
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
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
using WpfPoint         = System.Windows.Point;
using WpfColorConv     = System.Windows.Media.ColorConverter;
using Line             = System.Windows.Shapes.Line;
using Ellipse          = System.Windows.Shapes.Ellipse;
using Rectangle        = System.Windows.Shapes.Rectangle;
using Polygon          = System.Windows.Shapes.Polygon;

namespace SMDWin.Views
{
    public partial class MainWindow
    {

        private async void LoadApps_Click(object s, RoutedEventArgs e) => await LoadAppsInternalAsync();

        // ── Windows Services: the chosen service ─────────────────────────────
        private void ServicesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ServicesGrid.SelectedItem is not WinServiceEntry svc)
            {
                ServiceDetailPanel.Visibility = Visibility.Collapsed;
                ServiceDetailEmpty.Visibility = Visibility.Visible;
                return;
            }
            ServiceDetailEmpty.Visibility = Visibility.Collapsed;
            ServiceDetailPanel.Visibility = Visibility.Visible;

            TxtSvcDetailName.Text = string.IsNullOrEmpty(svc.DisplayName) ? svc.Name : svc.DisplayName;
            TxtSvcDetailId.Text   = svc.Name;
            TxtSvcDetailDesc.Text = string.IsNullOrWhiteSpace(svc.Description) ? "No description provided by Windows." : svc.Description;

            (string advice, string brush) = svc.RiskLevel switch
            {
                "no"   => ("Needed by Windows or by security — keep it as it is.", "StatusErrorBrush"),
                "safe" => ("Safe to set to Manual or Disabled if the feature is not used; it can be turned back on at any time.", "StatusSuccessBrush"),
                _      => ("Change it only if you know this PC does not need it — some features may stop working.", "StatusWarningBrush"),
            };
            TxtSvcDetailAdvice.Text = advice;
            TxtSvcDetRisk.Text = svc.RiskLabel;
            TxtSvcDetRisk.SetResourceReference(TextBlock.ForegroundProperty, brush);
            SvcDetRiskPillBg.SetResourceReference(Border.BackgroundProperty, brush);

            bool running = svc.Status == "Running";
            TxtSvcDetStatus.Text = svc.Status;
            TxtSvcDetStatus.SetResourceReference(TextBlock.ForegroundProperty, running ? "StatusSuccessBrush" : "TextPrimaryBrush");
            TxtSvcDetStart.Text = svc.StartType;
            TxtSvcDetPublisher.Text = string.IsNullOrWhiteSpace(svc.Publisher) ? "—" : svc.Publisher;
            TxtSvcDetailPath.Text = string.IsNullOrWhiteSpace(svc.ImagePath) ? "—" : svc.ImagePath;

            BtnStartService.IsEnabled = !running && svc.StartType != "Disabled";
            BtnStopService.IsEnabled  = running;
            foreach (var (b, type) in new[] { (BtnSetAutomatic, "Automatic"), (BtnSetManual, "Manual"), (BtnDisableService, "Disabled") })
                SetTabButtonActive(b, svc.StartType.StartsWith(type, StringComparison.OrdinalIgnoreCase));

            // Dependencies — read from the Service Control Manager in the background (arrow-key navigation stays smooth)
            TxtSvcDetDependsOn.Text = "…";
            TxtSvcDetNeededBy.Text  = "…";
            string svcName = svc.Name;
            _ = Task.Run(() =>
            {
                string dependsOn = "—", neededBy = "—";
                try
                {
                    using var sc = new System.ServiceProcess.ServiceController(svcName);
                    dependsOn = ListServiceNames(sc.ServicesDependedOn);
                    neededBy  = ListServiceNames(sc.DependentServices);
                }
                catch (Exception ex) { AppLogger.Debug($"Service dependencies {svcName}: {ex.Message}"); }
                Dispatcher.InvokeAsync(() =>
                {
                    if ((ServicesGrid.SelectedItem as WinServiceEntry)?.Name != svcName) return;
                    TxtSvcDetDependsOn.Text = dependsOn; TxtSvcDetDependsOn.ToolTip = dependsOn;
                    TxtSvcDetNeededBy.Text  = neededBy;  TxtSvcDetNeededBy.ToolTip  = neededBy;
                });
            });
        }

        private static string ListServiceNames(System.ServiceProcess.ServiceController[] list)
        {
            if (list == null || list.Length == 0) return "Nothing";
            string first = list[0].DisplayName;
            return list.Length == 1 ? first : $"{first}, {list.Length - 1} more";
        }

        // The active quick filter: All / Large / Recent / Bloat
        private string _appFilter = "All";

        private void AppFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string tag) _appFilter = tag;
            foreach (var b in new[] { BtnAppFilterAll, BtnAppFilterLarge, BtnAppFilterRecent, BtnAppFilterBloat })
                if (b != null) SetTabButtonActive(b, (b.Tag as string) == _appFilter);
            ApplyAppsView();
        }

        /// <summary>Search + quick filter + the top summary + the size bars in the table.</summary>
        private void ApplyAppsView()
        {
            var q = TxtAppSearch?.Text.Trim() ?? "";
            IEnumerable<InstalledApp> v = _allApps;
            if (q.Length > 0)
                v = v.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                 a.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase));
            v = _appFilter switch
            {
                "Large"  => v.Where(a => a.IsLarge),
                "Recent" => v.Where(a => a.IsRecent).OrderByDescending(a => a.InstallDateRaw),
                "Bloat"  => v.Where(a => a.IsBloat),
                _        => v,
            };
            var list = _appFilter == "Recent" ? v.ToList() : v.OrderByDescending(a => a.SizeBytes).ToList();

            long maxBytes = list.Count > 0 ? Math.Max(1, list.Max(a => a.SizeBytes)) : 1;
            foreach (var a in list) a.SizeBarW = a.SizeBytes <= 0 ? 0 : Math.Max(2, 60.0 * a.SizeBytes / maxBytes);

            var selected = AppsGrid.SelectedItem as InstalledApp;
            AppsGrid.ItemsSource = list;
            if (selected != null && list.Contains(selected)) AppsGrid.SelectedItem = selected;

            double totalGb = _allApps.Sum(a => (double)a.SizeBytes) / 1_073_741_824.0;
            if (TxtAppsStatCount  != null) TxtAppsStatCount.Text  = _allApps.Count.ToString();
            if (TxtAppsStatSize   != null) TxtAppsStatSize.Text   = $"{totalGb:F1} GB";
            if (TxtAppsStatRecent != null) TxtAppsStatRecent.Text = _allApps.Count(a => a.IsRecent).ToString();
            if (TxtAppsStatBloat  != null) TxtAppsStatBloat.Text  = _allApps.Count(a => a.IsBloat).ToString();

            double gb = list.Sum(a => (double)a.SizeBytes) / 1_073_741_824.0;
            if (TxtAppsTableSub != null) TxtAppsTableSub.Text = $"{list.Count} programs · {gb:F1} GB";
            if (TxtAppsCount != null)
                TxtAppsCount.Text = _appFilter == "Recent" ? "Newest first" : "Sorted by size";
            if (BtnUninstall != null) BtnUninstall.IsEnabled = AppsGrid.SelectedItem is InstalledApp;
            RefreshAppsSummary();
        }

        private void AppSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyAppsView();

        private void ClearAppSearch_Click(object sender, RoutedEventArgs e) => TxtAppSearch.Clear();

        private void AppsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var app = AppsGrid.SelectedItem as InstalledApp;
            if (BtnUninstall != null) BtnUninstall.IsEnabled = app != null;
            ShowAppDetail(app);
        }

        /// <summary>The "Selected" card: what the program is, how much it takes, whether it starts with Windows, where it is installed.</summary>
        private void ShowAppDetail(InstalledApp? app)
        {
            if (AppDetailPanel == null || AppDetailEmpty == null) return;
            SaveAppNote();   // the note of the program shown until now
            if (app == null)
            {
                _appNoteFor = null;
                AppDetailPanel.Visibility = Visibility.Collapsed;
                AppDetailEmpty.Visibility = Visibility.Visible;
                return;
            }
            AppDetailEmpty.Visibility = Visibility.Collapsed;
            AppDetailPanel.Visibility = Visibility.Visible;

            static string Or(string? x) => string.IsNullOrWhiteSpace(x) || x == "—" ? "—" : x;
            TxtAppDetName.Text = app.Name;
            ImgAppDetIcon.Source = app.Icon;

            if (app.IsBloat)      SetAppDetPill("Possibly unwanted", "StatusWarningBrush");
            else if (app.IsRecent) SetAppDetPill("New", "AccentBrush");
            else                  AppDetPill.Visibility = Visibility.Collapsed;

            var about = new List<string>();
            if (app.IsBloat)
                about.Add("Often preinstalled by the PC maker or bundled with other software. Check whether it is still needed — removing it frees space and background activity.");
            if (app.IsRecent)
                about.Add("Installed in the last 30 days. If the problems started recently, new programs are a good place to look.");
            if (app.IsLarge)
                about.Add("One of the larger programs on this PC.");
            if (about.Count == 0 && app.InstallDateRaw.HasValue)
            {
                var days = (DateTime.Now - app.InstallDateRaw.Value).TotalDays;
                about.Add(days >= 365 ? $"Installed about {(int)(days / 365)} year{((int)(days / 365) == 1 ? "" : "s")} ago."
                                      : $"Installed about {Math.Max(1, (int)(days / 30))} month{((int)(days / 30) <= 1 ? "" : "s")} ago.");
            }
            TxtAppDetAbout.Text = string.Join(" ", about);
            TxtAppDetAbout.Visibility = about.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            TxtAppDetPublisher.Text = Or(app.Publisher);
            TxtAppDetPublisher.ToolTip = app.Publisher;
            TxtAppDetVersion.Text   = Or(app.Version);
            TxtAppDetSize.Text      = Or(app.Size);
            TxtAppDetInstalled.Text = Or(app.InstallDate);
            TxtAppDetScope.Text     = app.Hive == "HKCU" ? "This account" : "All users";
            TxtAppDetLocation.Text  = string.IsNullOrWhiteSpace(app.InstallLocation) ? "Not reported by the installer" : app.InstallLocation;
            BtnAppOpenFolder.IsEnabled = !string.IsNullOrWhiteSpace(app.InstallLocation) && Directory.Exists(app.InstallLocation);

            // The Startup Manager entries that start something from the program folder
            int startupHits = -1;
            _appStartupEntryName = "";
            if (_allStartupEntries != null)
            {
                string loc = app.InstallLocation.TrimEnd('\\');
                var hits = _allStartupEntries.Where(en =>
                    (loc.Length > 3 && en.Command.Contains(loc, StringComparison.OrdinalIgnoreCase)) ||
                    string.Equals(en.Name, app.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                startupHits = hits.Count;
                if (hits.Count > 0) _appStartupEntryName = hits[0].Name;
            }
            // when it starts with Windows, the row is a link to Startup Manager
            TxtAppDetStartup.Cursor = startupHits > 0 ? System.Windows.Input.Cursors.Hand : null;
            TxtAppDetStartup.TextDecorations = startupHits > 0 ? TextDecorations.Underline : null;
            TxtAppDetStartup.ToolTip = startupHits > 0 ? "Click to see it in Startup Manager" : null;
            if (startupHits < 0)
            {
                TxtAppDetStartup.Text = "Open Startup Manager to check";
                TxtAppDetStartup.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            }
            else if (startupHits == 0)
            {
                TxtAppDetStartup.Text = "No";
                TxtAppDetStartup.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            }
            else
            {
                TxtAppDetStartup.Text = startupHits == 1 ? "Yes · 1 entry" : $"Yes · {startupHits} entries";
                TxtAppDetStartup.SetResourceReference(TextBlock.ForegroundProperty, "StatusWarningBrush");
            }

            bool hasUninstaller = !string.IsNullOrWhiteSpace(app.UninstallKey);
            TxtAppDetUninstaller.Text = hasUninstaller ? "found" : "missing";
            TxtAppDetUninstaller.SetResourceReference(TextBlock.ForegroundProperty, hasUninstaller ? "TextPrimaryBrush" : "StatusWarningBrush");
            TxtAppDetHive.Text = app.Hive;
            TxtAppDetHive.ToolTip = app.RegistryPath;
            ShowAppExtras(app);   // fallback icon, Change / Repair, note, background reads (MainWindow.AppDetail.cs)
        }

        private void SetAppDetPill(string text, string brushKey)
        {
            TxtAppDetPill.Text = text;
            TxtAppDetPill.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            AppDetPillBg.SetResourceReference(Border.BackgroundProperty, brushKey);
            AppDetPill.Visibility = Visibility.Visible;
        }

        private void AppOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (AppsGrid.SelectedItem is not InstalledApp app || string.IsNullOrWhiteSpace(app.InstallLocation)) return;
            try
            {
                if (Directory.Exists(app.InstallLocation))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{app.InstallLocation}\"") { UseShellExecute = true });
            }
            catch (Exception ex) { AppLogger.Warning(ex, "AppOpenFolder_Click"); }
        }

        private void AppSearchOnline_Click(object sender, RoutedEventArgs e)
        {
            if (AppsGrid.SelectedItem is not InstalledApp app) return;
            try { SearchGoogle($"\"{app.Name}\" {app.Publisher}"); }
            catch (Exception ex) { AppLogger.Warning(ex, "AppSearchOnline_Click"); }
        }

        private void UninstallApp_Click(object sender, RoutedEventArgs e)
        {
            if (AppsGrid.SelectedItem is not InstalledApp app) { AppDialog.Show(_LS("Apps", "SelectApp")); return; }
            // The bar button stays neutral; red appears only on confirmation, on the action itself
            if (AppDialog.ConfirmDanger(string.Format(_LS("Apps", "ConfirmUninstall"), app.Name), _LS("Common", "Confirm"),
                                        _LS("Apps", "UninstallButton"), _LS("Common", "Cancel")))
            {
                try { _appsService.Uninstall(app); }
                catch (Exception ex) { AppDialog.Show(string.Format(_LS("Common", "ErrorFmt"), ex.Message)); }
            }
        }

        private void OpenAddRemovePrograms_Click(object s, RoutedEventArgs e)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("appwiz.cpl") { UseShellExecute = true });
        }

        // ── SERVICES ──────────────────────────────────────────────────────────

        private List<WinServiceEntry> _servicesList = new();
        private bool _servicesAllView, _servicesThirdView;

        private async Task LoadKeyServicesAsync()
        {
            SetServicesTabActive(keyActive: true);
            _servicesAllView = false; _servicesThirdView = false;
            ShowLoading(_LS("Services", "LoadingServices"));
            try { SetServicesList(await _svcService.GetServicesAsync(onlyKnown: true)); }
            finally { HideLoading(); }
        }

        private async Task LoadAllServicesAsync()
        {
            SetServicesTabActive(keyActive: false);
            _servicesAllView = true; _servicesThirdView = false;
            ShowLoading(_LS("Services", "LoadingAll"));
            try
            {
                var services = await _svcService.GetServicesAsync(onlyKnown: false);
                EnrichServiceDescriptions(services);
                SetServicesList(services);
            }
            finally { HideLoading(); }
        }

        /// <summary>
        /// "Non-Microsoft": the services installed by other programs (not under System32 / not signed by Microsoft) — what
        /// msconfig's "Hide all Microsoft services" leaves, the first place to look when a PC behaves oddly.
        /// </summary>
        private async Task LoadThirdPartyServicesAsync()
        {
            SetServicesTabActive(keyActive: false, third: true);
            _servicesAllView = false; _servicesThirdView = true;
            ShowLoading(_LS("Services", "LoadingAll"));
            try
            {
                var services = (await _svcService.GetServicesAsync(onlyKnown: false)).Where(x => x.IsThirdParty).ToList();
                EnrichServiceDescriptions(services);
                SetServicesList(services);
            }
            finally { HideLoading(); }
        }

        /// <summary>Reloads the current view (Key / Non-Microsoft / All) and keeps the chosen service.</summary>
        private async Task ReloadServicesAsync()
        {
            string? keep = SelectedService?.Name;
            if (_servicesThirdView) await LoadThirdPartyServicesAsync();
            else if (_servicesAllView) await LoadAllServicesAsync(); else await LoadKeyServicesAsync();
            if (keep != null)
                ServicesGrid.SelectedItem = (ServicesGrid.ItemsSource as IEnumerable<WinServiceEntry>)?
                    .FirstOrDefault(x => string.Equals(x.Name, keep, StringComparison.OrdinalIgnoreCase));
        }

        private async void ServicesViewToggle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            string tag = (sender as FrameworkElement)?.Tag as string ?? "";
            if (tag == "All") await LoadAllServicesAsync();
            else if (tag == "Third") await LoadThirdPartyServicesAsync();
            else await LoadKeyServicesAsync();
        }

        private void SetServicesList(IEnumerable<WinServiceEntry> list)
        {
            _servicesList = list.ToList();
            ApplyServicesView();
        }

        private void SvcSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyServicesView();

        private void ApplyServicesView()
        {
            var q = TxtSvcSearch?.Text.Trim() ?? "";
            var v = q.Length == 0 ? _servicesList
                  : _servicesList.Where(x => x.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                             x.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                             x.Description.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            ServicesGrid.ItemsSource = v;
            int running = _servicesList.Count(x => x.Status == "Running");
            if (TxtSvcTableCaption != null) TxtSvcTableCaption.Text = _servicesThirdView ? PT("Non-Microsoft services") : _servicesAllView ? PT("All services") : PT("Key services");
            if (TxtSvcTableSub != null)
                TxtSvcTableSub.Text = (_servicesThirdView ? PT("{0} from other makers", _servicesList.Count)
                                     : _servicesAllView ? $"{_servicesList.Count} services" : $"{_servicesList.Count} services worth knowing")
                                    + $" · {running} running" + (q.Length > 0 ? $" · {v.Count} match" : "");
            RefreshServicesSummary();
        }

        private void SetServicesTabActive(bool keyActive, bool third = false)
        {
            try
            {
                if (ChkServicesView != null) ChkServicesView.IsChecked = !keyActive;
                if (SegServicesKey != null && SegServicesAll != null &&
                    TxtKeyServicesLabel != null && TxtAllServicesLabel != null)
                    SetSegmentedActive(SegServicesKey, SegServicesAll,
                        TxtKeyServicesLabel, TxtAllServicesLabel, keyActive);
                // the middle segment (Non-Microsoft): on only in its view, and then "All" is off
                if (FindName("SegServicesThird") is Border mid && FindName("TxtThirdServicesLabel") is TextBlock midLabel && SegServicesAll != null && TxtAllServicesLabel != null)
                {
                    void Look(Border seg, TextBlock label, bool on)
                    {
                        if (on) { seg.SetResourceReference(Border.BackgroundProperty, "SegOnBrush"); seg.SetResourceReference(Border.BorderBrushProperty, "SelBorderBrush"); }
                        else { seg.Background = System.Windows.Media.Brushes.Transparent; seg.BorderBrush = System.Windows.Media.Brushes.Transparent; }
                        seg.BorderThickness = new Thickness(1);
                        label.SetResourceReference(TextBlock.ForegroundProperty, on ? "TextPrimaryBrush" : "TextSecondaryBrush");
                        label.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                    }
                    Look(mid, midLabel, third);
                    if (third) Look(SegServicesAll, TxtAllServicesLabel, false);
                }
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }
        }

    }
}
