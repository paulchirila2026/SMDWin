// DiagPackageWindow.cs — the module selection window for Generate AI Diagnostic Package
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;

namespace SMDWin.Views
{
    public class DiagPackageWindow : Window
    {
        private static readonly (string Key, string Label, string Desc)[] Modules =
        {
            ("hardware", "Hardware Info",           "CPU, GPU, RAM, motherboard, BIOS details"),
            ("smart",    "S.M.A.R.T. Disk Data",   "Disk health, wear level, SMART attributes"),
            ("eventlog", "Event Log",               "System & Application events, last 3 days"),
            ("crashes",  "BSOD / Crash Dumps",      "Blue screen stop codes and crash records"),
            ("drivers",  "Drivers & Devices",       "Problem devices and installed drivers"),
            ("network",  "Network Info",            "Adapters, IP, DNS, gateway, Wi-Fi"),
            ("startup",  "Startup & Services",      "Startup programs and auto-start services"),
            ("winlogs",  "Windows Logs",            "CBS, DISM, Setup and Windows Update logs"),
            ("smdlogs",  "SMDWin App + Sensor Logs", "Full app log (warnings/errors) + temperature sensor CSV history"),
            ("sysinfo",  "System Summary",          "OS version, license, TPM, Secure Boot"),
            ("reliability",  "Reliability Monitor",  "Win32_ReliabilityRecords via WMI — same data as perfmon /rel"),
            ("criticalevents","Critical Event Providers","WHEA, Kernel-Power, Disk/NTFS/Storage, Display (nvlddmkm/amdkmdag/igfx), USB, WLAN, Service Control Manager — ALL entries, not just latest"),
            ("werreports","Windows Error Reporting","App/system crash reports from WER — even without a full dump"),
            ("livekernel","LiveKernelReports",      "Non-fatal kernel events (GPU timeout, WHEA, PCIe) that don't reach a full BSOD"),
            ("rawcmds",   "Raw Diagnostic Commands", "systeminfo, driverquery, pnputil, bcdedit, fltmc, wmic — unedited output for AI/manual review"),
        };

        private readonly Dictionary<string, System.Windows.Controls.CheckBox> _checks = new();
        private readonly Func<System.Collections.Generic.HashSet<string>, System.Threading.Tasks.Task> _exportCallback;
        private System.Windows.Controls.Button? _btnExport;
        private TextBlock? _txtStatus;

        public DiagPackageWindow(
            Func<System.Collections.Generic.HashSet<string>, System.Threading.Tasks.Task> exportCallback)
        {
            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
            _exportCallback = exportCallback;
            Title  = "Generate Diagnostic Package";
            Width  = 560; Height = 540;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;

            double radiusCardForWin = (System.Windows.Application.Current.Resources["RadiusCard"] as CornerRadius?)?.TopLeft ?? 8;
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 32, CornerRadius = new CornerRadius(radiusCardForWin),
                GlassFrameThickness = new Thickness(-1),
                UseAeroCaptionButtons = false,
                ResizeBorderThickness = new Thickness(0),
            });

            try
            {
                foreach (ResourceDictionary rd in System.Windows.Application.Current.Resources.MergedDictionaries)
                    Resources.MergedDictionaries.Add(rd);
                foreach (var key in System.Windows.Application.Current.Resources.Keys)
                    try { Resources[key] = System.Windows.Application.Current.Resources[key]; } catch (Exception ex) { SMDWin.Services.AppLogger.Debug(ex.Message); }
                if (System.Windows.Application.Current.Resources["BgDarkBrush"] is System.Windows.Media.Brush bgEarly)
                    Background = bgEarly;
            }
            catch (Exception ex) { SMDWin.Services.AppLogger.Debug(ex.Message); }

            SourceInitialized += (_, _) =>
            {
                try
                {
                    var helper = new System.Windows.Interop.WindowInteropHelper(this);
                    var theme  = Services.SettingsService.Current.ThemeName;
                    Services.ThemeManager.ApplyTitleBarColor(helper.Handle, theme);
                }
                catch (Exception ex) { SMDWin.Services.AppLogger.Debug(ex.Message); }
            };

            Content = BuildUI();
        }

        private UIElement BuildUI()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ── Title bar ─────────────────────────────────────────────────────
            var titleBar = new Grid();
            titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleTxt = new TextBlock
            {
                Text = "Generate Diagnostic Package",
                FontSize = Ui.FontL, FontWeight = FontWeights.SemiBold,
                Foreground = GetBrush("TextPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0),
            };
            Grid.SetColumn(titleTxt, 0); titleBar.Children.Add(titleTxt);
            var btnClose = Net.NetWindowHelper.CloseIconButton(32);
            btnClose.Click += (_, _) => Close();
            System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(btnClose, true);
            Grid.SetColumn(btnClose, 1); titleBar.Children.Add(btnClose);
            Grid.SetRow(titleBar, 0); root.Children.Add(titleBar);

            // ── Header ────────────────────────────────────────────────────────
            var header = new Border
            {
                Background = GetBrush("BgHoverBrush"),
                Padding = new Thickness(16, 12, 16, 12),
            };
            var headerSp = new StackPanel();
            headerSp.Children.Add(new TextBlock
            {
                Text = "Select the data to include in the diagnostic ZIP.",
                FontSize = Ui.FontM, Foreground = GetBrush("TextPrimaryBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            headerSp.Children.Add(new TextBlock
            {
                Text = string.Format(LanguageService.S("Diag", "PackageSavedToFmt", "The ZIP is saved in {0}. Upload it to any AI assistant for diagnosis."), SettingsService.GetEffectiveReportFolder()),
                FontSize = Ui.FontS, Foreground = GetBrush("TextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
            });
            header.Child = headerSp;
            Grid.SetRow(header, 1); root.Children.Add(header);

            // ── Module list ───────────────────────────────────────────────────
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            var modulesSp = new StackPanel { Margin = new Thickness(16, 8, 16, 8) };

            // Select All / Deselect All
            var toolSp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            toolSp.Children.Add(MakeSmallBtn("☑  Select All",    () => SetAll(true)));
            toolSp.Children.Add(new Border { Width = 8 });
            toolSp.Children.Add(MakeSmallBtn("☐  Deselect All",  () => SetAll(false)));
            modulesSp.Children.Add(toolSp);

            foreach (var (key, label, desc) in Modules)
            {
                var row = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 0, 0, 4),
                    Background = GetBrush("BgCardBrush"),
                    BorderThickness = new Thickness(1),
                    BorderBrush = GetBrush("CardBorderBrush"),
                };
                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var chk = new System.Windows.Controls.CheckBox
                {
                    IsChecked = true,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0),
                };
                chk.Checked   += (_, _) => UpdateExportButton();
                chk.Unchecked += (_, _) => UpdateExportButton();
                _checks[key] = chk;
                Grid.SetColumn(chk, 0); rowGrid.Children.Add(chk);

                var textSp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                textSp.Children.Add(new TextBlock
                {
                    Text = label, FontSize = Ui.FontM, FontWeight = FontWeights.SemiBold,
                    Foreground = GetBrush("TextPrimaryBrush"),
                });
                textSp.Children.Add(new TextBlock
                {
                    Text = desc, FontSize = Ui.FontXS, Foreground = GetBrush("TextSecondaryBrush"),
                    Margin = new Thickness(0, 1, 0, 0),
                });
                Grid.SetColumn(textSp, 1); rowGrid.Children.Add(textSp);
                row.Child = rowGrid;
                modulesSp.Children.Add(row);
            }

            scroll.Content = modulesSp;
            Grid.SetRow(scroll, 2); root.Children.Add(scroll);

            // ── Status ────────────────────────────────────────────────────────
            _txtStatus = new TextBlock
            {
                FontSize = Ui.FontS, Foreground = GetBrush("TextSecondaryBrush"),
                Margin = new Thickness(16, 6, 16, 2), TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetRow(_txtStatus, 3); root.Children.Add(_txtStatus);

            // ── Footer ────────────────────────────────────────────────────────
            var footer = new Border
            {
                Background = GetBrush("BgHoverBrush"),
                Padding = new Thickness(16, 10, 16, 10),
            };
            var footerSp = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            };
            var btnCancel = new System.Windows.Controls.Button
            {
                Content = "Cancel",
                Padding = new Thickness(20, 7, 20, 7),
                Margin = new Thickness(0, 0, 10, 0),
            };
            try { btnCancel.Style = (Style)FindResource("SecondaryButtonStyle"); } catch (Exception ex) { SMDWin.Services.AppLogger.Debug(ex.Message); }
            btnCancel.Click += (_, _) => Close();

            _btnExport = new System.Windows.Controls.Button
            {
                Content = "Export ZIP →",
                Padding = new Thickness(20, 7, 20, 7),
            };
            try { _btnExport.Style = (Style)FindResource("PrimaryButtonStyle"); } catch (Exception ex) { SMDWin.Services.AppLogger.Debug(ex.Message); }
            _btnExport.Click += async (_, _) => await RunExport();

            footerSp.Children.Add(btnCancel);
            footerSp.Children.Add(_btnExport);
            footer.Child = footerSp;
            Grid.SetRow(footer, 4); root.Children.Add(footer);

            return root;
        }

        private System.Windows.Controls.Button MakeSmallBtn(string text, Action onClick)
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = text, FontSize = Ui.FontS,
                Padding = new Thickness(10, 4, 10, 4),
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(0),
            };
            try { btn.Style = (Style)FindResource("SecondaryButtonStyle"); } catch (Exception ex) { SMDWin.Services.AppLogger.Debug(ex.Message); }
            btn.Click += (_, _) => onClick();
            return btn;
        }

        private void SetAll(bool value)
        {
            foreach (var chk in _checks.Values) chk.IsChecked = value;
            UpdateExportButton();
        }

        private void UpdateExportButton()
        {
            int n = _checks.Values.Count(c => c.IsChecked == true);
            if (_btnExport == null) return;
            _btnExport.IsEnabled = n > 0;
            _btnExport.Content = n > 0
                ? $"Export ZIP ({n} module{(n == 1 ? "" : "s")}) →"
                : "Select at least one module";
        }

        private async System.Threading.Tasks.Task RunExport()
        {
            var selected = new System.Collections.Generic.HashSet<string>(
                _checks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key));
            if (selected.Count == 0) return;

            if (_btnExport != null) _btnExport.IsEnabled = false;
            if (_txtStatus != null) _txtStatus.Text = LanguageService.S("Diag", "BuildingPackage");
            try
            {
                await _exportCallback(selected);
                if (_txtStatus != null) _txtStatus.Text = LanguageService.S("Diag", "PackageDone", "✓ Done — the ZIP is in the reports folder.");
                await System.Threading.Tasks.Task.Delay(1500);
                Close();
            }
            catch (Exception ex)
            {
                if (_txtStatus != null) _txtStatus.Text = $"Error: {ex.Message}";
                if (_btnExport != null) _btnExport.IsEnabled = true;
            }
        }

        private static System.Windows.Media.Brush? GetBrush(string key)
            => System.Windows.Application.Current?.Resources[key] as System.Windows.Media.Brush;
    }
}
