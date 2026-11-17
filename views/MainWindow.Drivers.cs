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
using WpfColorConv     = System.Windows.Media.ColorConverter;

namespace SMDWin.Views
{
    public partial class MainWindow
    {

        // ── DRIVERS ──────────────────────────────────────────────────────────

        private string _driverViewMode = "Basic";
        private string _driverBasicFilter = "All";
        private List<DeviceManagerEntry> _allDevices = new();
        private DeviceManagerEntry? _selectedDriverEntry = null;
        private List<DeviceManagerEntry> _filteredDevices = new();

        // ─────────────────────────────────────────────────────────────────────────────
        //  Simplified categories: DeviceClass → displayed category
        // ─────────────────────────────────────────────────────────────────────────────
        private static readonly Dictionary<string, string> _categoryMap = new(StringComparer.OrdinalIgnoreCase)
        {
            // Graphics
            { "Display","Grafică" },
            { "VideoAdaptor","Grafică" },
            // Audio
            { "Media","Audio" },
            { "AudioEndpoint","Audio" },
            // Network
            { "Net","Rețea" },
            { "NetClient","Rețea" },
            { "NetService","Rețea" },
            { "NetTrans","Rețea" },
            { "Bluetooth","Bluetooth" },
            // USB & Input
            { "USB","USB" },
            { "HIDClass","Input / HID" },
            { "Mouse","Input / HID" },
            { "Keyboard","Input / HID" },
            // Storage
            { "DiskDrive","Storage" },
            { "SCSIAdapter","Storage" },
            { "HDC","Storage" },
            { "Volume","Storage" },
            { "VolumeSnapshot","Storage" },
            // Sistem
            { "System","Sistem" },
            { "Computer","Sistem" },
            { "Processor","Sistem" },
            { "Battery","Baterie" },
            // Imprimante & imaging
            { "PrintQueue","Imprimantă" },
            { "Image","Cameră / Imagine" },
            { "Camera","Cameră / Imagine" },
            // Other
            { "SoftwareDevice","Software / Virtual" },
            { "SoftwareComponent","Software / Virtual" },
            { "Extension","Software / Virtual" },
        };

        private static readonly HashSet<string> _importantCategories = new(StringComparer.OrdinalIgnoreCase)
        {
"Grafică", "Audio", "Rețea", "Bluetooth"
        };

        private string GetSimplifiedCategory(DeviceManagerEntry dev)
        {
            if (_categoryMap.TryGetValue(dev.DeviceClass ?? "", out var cat))
                return cat;
            return "Altele";
        }

        // i18n: translates the category name (the internal keys stay Romanian for the existing icon/switch, but the display
        // follows the language). Used only for display, it does not change the logic.
        private string CategoryDisplay(string catKey) => catKey switch
        {
            "Grafică"            => _LS("DriverCat", "Graphics"),
            "Audio"              => "Audio",
            "Rețea"              => _LS("DriverCat", "Network"),
            "Bluetooth"          => "Bluetooth",
            "USB"                => "USB",
            "Input / HID"        => _LS("DriverCat", "InputHid"),
            "Storage"            => _LS("DriverCat", "Storage"),
            "Sistem"             => _LS("DriverCat", "System"),
            "Baterie"            => _LS("DriverCat", "Battery"),
            "Imprimantă"         => _LS("DriverCat", "Printer"),
            "Cameră / Imagine"   => _LS("DriverCat", "Camera"),
            "Software / Virtual" => _LS("DriverCat", "Software"),
            "Altele"             => _LS("DriverCat", "Other"),
            _                    => catKey
        };

        // An icon per simplified category — helps scanning the list visually.
        /// <summary>The Fluent icon name of a driver category (Services/FluentIcons.cs).</summary>
        private static string GetCategoryIconPath(string category) => category switch
        {
            "Grafică"             => "desktop",
            "Audio"               => "speaker_2",
            "Rețea"               => "wifi_1",
            "Bluetooth"           => "bluetooth",
            "USB"                 => "usb_plug",
            "Input / HID"         => "keyboard",
            "Storage"             => "hard_drive",
            "Sistem"              => "developer_board",
            "Baterie"             => "battery_5",
            "Imprimantă"          => "print",
            "Cameră / Imagine"    => "camera",
            "Software / Virtual"  => "document",
            _                     => "info",
        };

        // ─────────────────────────────────────────────────────────────────────────────
        //  Helpers for the driver age
        // ─────────────────────────────────────────────────────────────────────────────
        private static (string label, string color, string glow) GetDriverAgeBadge(string? dateStr)
        {
            if (string.IsNullOrEmpty(dateStr) || dateStr == "n/a")
                return ("—", "#94A3B8", "#94A3B8");   // no date reported (TPM, fingerprint, some audio): a dash, not a question mark

            if (!DateTime.TryParse(dateStr, out var dt))
                return ("—", "#94A3B8", "#94A3B8");   // no date reported (TPM, fingerprint, some audio): a dash, not a question mark

            var age = DateTime.Now - dt;
            int years = (int)(age.TotalDays / 365);
            bool ro = LanguageService.CurrentCode == "ro";

            // The age is informational only: an old driver that works is not a problem, so the chip stays neutral.
            if (age.TotalDays < 365) return (ro ? "< 1 an" : "< 1y", "#94A3B8", "#94A3B8");
            return (ro ? $"{years} ani" : $"{years}y", "#94A3B8", "#94A3B8");
        }

        private static (string color, string glowColor, string message) GetDriverStatusLed(DeviceManagerEntry dev, string? dateStr)
        {
            bool ro = LanguageService.CurrentCode == "ro";
            if (dev.IsMissing)
                return ("#EF4444", "#EF4444", ro ? $"Problemă: {dev.Status}" : $"Issue: {dev.Status}");
            if (dev.IsSigned == false)
                return ("#F59E0B", "#F59E0B", ro ? "Driver nesemnat digital" : "Driver not digitally signed");

            // An old driver on a device without errors is normal (the hardware gets no more updates, and the last version is often
            // the most stable), so it stays green; the date appears only as information.
            if (!string.IsNullOrEmpty(dateStr) && dateStr != "—" && DateTime.TryParse(dateStr, out var dt)
                && (DateTime.Now - dt).TotalDays >= 5 * 365)
                return ("#22C55E", "#22C55E", ro
                    ? $"Driver OK · din {dt.Year}. E normal ca hardware-ul mai vechi să nu mai primească drivere noi."
                    : $"Driver OK · from {dt.Year}. Older hardware often has no newer driver, and that's fine.");
            return ("#22C55E", "#22C55E", ro ? "Driver OK" : "Driver OK");
        }

        private static string GetPowerEstimate(string category, string? deviceName = null)
        {
            bool ro = LanguageService.CurrentCode == "ro";
            if (category == "Grafică")
            {
                bool isGenericDriver = !string.IsNullOrEmpty(deviceName) &&
                    (deviceName.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
                     || deviceName.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)
                     || deviceName.Contains("Generic PnP Monitor", StringComparison.OrdinalIgnoreCase));
                if (isGenericDriver) return "—";

                if (!string.IsNullOrEmpty(deviceName))
                {
                    var dbGpu = SMDWin.Services.HardwareDatabaseService.LookupGpu(deviceName);
                    if (dbGpu != null && dbGpu.TdpW > 0)
                        return ro ? $"{dbGpu.TdpW:0}W (în sarcină, TDP real din baza de date)"
                                  : $"{dbGpu.TdpW:0}W (in load, real TDP from database)";
                }

                bool isMobile = !string.IsNullOrEmpty(deviceName) &&
                    (System.Text.RegularExpressions.Regex.IsMatch(deviceName, @"\bLaptop GPU\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                     || System.Text.RegularExpressions.Regex.IsMatch(deviceName, @"\d{3,4}M(X)?\b")); // ex: "755M", "960MX" da
                if (isMobile)
                    return ro ? "15–80W (mobil, în sarcină)" : "15–80W (mobile, in load)";
                return ro ? "50–350W (în sarcină)" : "50–350W (in load)";
            }
            return category switch
            {
                "Audio"    => "1–5W",
                "Rețea"    => "1–3W",
                "Bluetooth"=> "0.1–1W",
                "USB"      => ro ? "depinde de device" : "depends on device",
                "Storage"  => "2–10W",
                "Sistem"   => "—",
                _          => "—"
            };
        }

        private static string GetPowerProfile(string category)
        {
            bool ro = LanguageService.CurrentCode == "ro";
            return category switch
            {
                "Grafică"  => "High Performance / Auto",
                "Storage"  => ro ? "Echilibrat (spin-down activ)" : "Balanced (spin-down active)",
                "Bluetooth"=> ro ? "Economisire energie activă" : "Power Save active",
                "Rețea"    => ro ? "Echilibrat" : "Balanced",
                _          => "Normal"
            };
        }

        // ─────────────────────────────────────────────────────────────────────────────
        //  LoadAllDriversInternalAsync — main entry point
        // ─────────────────────────────────────────────────────────────────────────────
        // the full driver list (4.5 s) is kept for 3 minutes when simply returning to the page; "Scan" and the reload button read it again
        private System.Collections.IEnumerable? _advDriversCache;
        private DateTime _advDriversAt = DateTime.MinValue;

        // Start-up: reads the device list in the background (if the user has not opened the page yet).
        private async Task WarmDriversInBackgroundAsync()
        {
            if (_allDevices.Count > 0) return;
            var list = await _driverService.GetDeviceManagerDevicesAsync();
            if (_allDevices.Count == 0 && list.Count > 0)
            {
                _allDevices = list;
                UpdateSidebarBadge("Drivers", list.Count(d => d.IsMissing));
            }
        }

        private async Task LoadAllDriversInternalAsync(bool force = false)
        {
            RefreshDriverBlockerUI();
            ApplyDriversLocalization();

            if (_driverViewMode == "Basic")
                await LoadDriversBasicAsync(_driverBasicFilter);
            else
            {
                ShowLoading(_LS("Drivers", "LoadingDrivers"));
                using var reassureCts = new CancellationTokenSource();
                _ = Task.Delay(5000, reassureCts.Token).ContinueWith(t =>
                {
                    if (t.IsCanceled) return;
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (ToastBorder.Visibility == Visibility.Visible)
                            TxtLoadingMsg.Text = _LS("Drivers", "StillReadingDrivers");
                    });
                }, TaskScheduler.Default);
                try
                {
                    if (!force && _advDriversCache != null && (DateTime.UtcNow - _advDriversAt).TotalSeconds < 180)
                        DriversGrid.ItemsSource = _advDriversCache;
                    else
                    {
                        var list = await _driverService.GetDriversAsync();
                        _advDriversCache = list; _advDriversAt = DateTime.UtcNow;
                        DriversGrid.ItemsSource = list;
                    }
                }
                finally { reassureCts.Cancel(); HideLoading(); }
            }
        }

        private void ApplyDriversLocalization()
        {
            string lang = LanguageService.CurrentCode;
            string L4(string en, string ro, string de, string fr) => lang switch
            {
                "ro" => ro, "de" => de, "fr" => fr, _ => en
            };
            if (TxtDriversTitle   != null) TxtDriversTitle.Text   = L4("Drivers", "Drivere", "Treiber", "Pilotes");
            if (TxtDriverBasicAllLabel       != null) TxtDriverBasicAllLabel.Text       = L4("All", "Toate", "Alle", "Tous");
            if (TxtDriverBasicMissingLabel   != null) TxtDriverBasicMissingLabel.Text   = L4("Issues", "Probleme", "Probleme", "Problèmes");
            if (TxtDriverBasicOldLabel       != null) TxtDriverBasicOldLabel.Text       = L4("Not Installed", "Neinstalate", "Nicht installiert", "Non installés");
            if (TxtDriverBasicUnsignedLabel  != null) TxtDriverBasicUnsignedLabel.Text  = L4("Unsigned", "Nesemnate", "Unsigniert", "Non signés");
            if (FindName("TxtDriverBlockMenu") is System.Windows.Controls.TextBlock blk)   // the single "Block updates ▾" button
                blk.Text = LanguageService.S("Drivers", "BlockMenuButton", "Block updates");
            if (TxtDriverBasicImportantLabel != null) TxtDriverBasicImportantLabel.Text = L4("Important", "Importante", "Wichtig", "Importants");
            if (TxtDriverModeBasicLabel      != null) TxtDriverModeBasicLabel.Text      = L4("Basic", "Simplu", "Einfach", "Simple");
            if (TxtDriverModeAdvancedLabel   != null) TxtDriverModeAdvancedLabel.Text   = L4("Advanced", "Avansat", "Erweitert", "Avancé");
        }

        /// <summary>Two-part switch (e.g. "Apps only / With services"): a grey track, the chosen part is a lighter pill
        /// (SegOnBrush) with a very fine outline; no blue.</summary>
        private void SetSegmentedActive(Border leftSeg, Border rightSeg,
            TextBlock leftLabel, TextBlock rightLabel, bool leftActive)
        {
            void Look(Border seg, TextBlock label, bool on)
            {
                if (on) seg.SetResourceReference(Border.BackgroundProperty, "SegOnBrush");
                else seg.Background = System.Windows.Media.Brushes.Transparent;
                if (on) seg.SetResourceReference(Border.BorderBrushProperty, "SelBorderBrush");
                else seg.BorderBrush = System.Windows.Media.Brushes.Transparent;
                seg.BorderThickness = new Thickness(1);
                seg.ClearValue(UIElement.EffectProperty);   // no shadow: it would blur the label
                label.SetResourceReference(TextBlock.ForegroundProperty, on ? "TextPrimaryBrush" : "TextSecondaryBrush");
                label.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
            Look(leftSeg, leftLabel, leftActive);
            Look(rightSeg, rightLabel, !leftActive);
        }

        private void SyncDriverModeToggleVisual(bool basic)
        {
            if (ChkDriverMode != null) ChkDriverMode.IsChecked = !basic;
            if (SegDriverBasic != null && SegDriverAdvanced != null &&
                TxtDriverModeBasicLabel != null && TxtDriverModeAdvancedLabel != null)
                SetSegmentedActive(SegDriverBasic, SegDriverAdvanced,
                    TxtDriverModeBasicLabel, TxtDriverModeAdvancedLabel, basic);
        }

        private void DriverMode_Click(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Guard: the Click event may theoretically arrive before the panels are created.
            if (DriversBasicPanel == null || DriversAdvancedPanel == null ||
                DriversBasicToolbar == null || DriversAdvancedToolbar == null) return;

            bool basic = (s as FrameworkElement)?.Tag as string != "Advanced";
            _driverViewMode = basic ? "Basic" : "Advanced";
            SyncDriverModeToggleVisual(basic);

            DriversBasicPanel.Visibility      = basic ? Visibility.Visible  : Visibility.Collapsed;
            DriversAdvancedPanel.Visibility   = basic ? Visibility.Collapsed : Visibility.Visible;
            DriversBasicToolbar.Visibility    = basic ? Visibility.Visible  : Visibility.Collapsed;
            DriversAdvancedToolbar.Visibility = basic ? Visibility.Collapsed : Visibility.Visible;

            HideDriverDetailCard();
            SettingsService.Current.DriverViewMode = _driverViewMode;
            _ = LoadAllDriversInternalAsync();
        }

        // ─────────────────────────────────────────────────────────────────────────────
        //  DriverBasicFilter_Click — quick filters (All / Problems / Old / etc.)
        // ─────────────────────────────────────────────────────────────────────────────
        private async void DriverBasicFilter_Click(object s, RoutedEventArgs e)
        {
            if (s is Button btn) _driverBasicFilter = btn.Tag?.ToString() ?? "All";

            SetTabButtonActive(BtnDriverBasicAll,       _driverBasicFilter == "All");
            SetTabButtonActive(BtnDriverBasicMissing,   _driverBasicFilter == "Missing");
            SetTabButtonActive(BtnDriverBasicOld,       _driverBasicFilter == "NotInstalled");
            SetTabButtonActive(BtnDriverBasicUnsigned,  _driverBasicFilter == "Unsigned");
            SetTabButtonActive(BtnDriverBasicImportant, _driverBasicFilter == "Important");

            await LoadDriversBasicAsync(_driverBasicFilter);
        }

        // ─────────────────────────────────────────────────────────────────────────────
        //  DriverSearch_TextChanged — live filtering by text
        // ─────────────────────────────────────────────────────────────────────────────
        private void DriverSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_allDevices.Count == 0) return;
            PopulateDriversBasicList(_driverBasicFilter);
        }

        // ─────────────────────────────────────────────────────────────────────────────
        //  DriverScan_Click — rescans and invalidates the cache
        // ─────────────────────────────────────────────────────────────────────────────
        private async void DriverScan_Click(object s, RoutedEventArgs e)
        {
            _allDevices.Clear();
            _advDriversCache = null;
            HideDriverDetailCard();
            await LoadDriversBasicAsync("All");
            _driverBasicFilter = "All";
            DriverBasicFilter_Click(BtnDriverBasicAll, new RoutedEventArgs());
        }

        // ─────────────────────────────────────────────────────────────────────────────
        //  LoadDriversBasicAsync
        // ─────────────────────────────────────────────────────────────────────────────
        private async Task LoadDriversBasicAsync(string filter)
        {
            if (_allDevices.Count > 0)
            {
                PopulateDriversBasicList(filter);
                return;
            }

            ShowLoading(_LS("Drivers", "LoadingDevices"));
            try
            {
                _allDevices = await _driverService.GetDeviceManagerDevicesAsync();
                PopulateDriversBasicList(filter);
                int problemCount = _allDevices.Count(d => d.IsMissing);
                UpdateSidebarBadge("Drivers", problemCount);
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "LoadDriversBasicAsync");
            }
            finally { HideLoading(); }
        }

        // ─────────────────────────────────────────────────────────────────────────────
        //  PopulateDriversBasicList — builds the visual list
        // ─────────────────────────────────────────────────────────────────────────────
        private void PopulateDriversBasicList(string filter)
        {
            string searchText = TxtDriverSearch?.Text?.Trim().ToLowerInvariant() ?? "";

            // Apply the active filter + search
            var list = _allDevices.AsEnumerable();

            switch (filter)
            {
                case "Missing":
                    list = list.Where(d => d.IsMissing || d.ErrorCode != 0);
                    break;
                case "NotInstalled":
                    list = list.Where(d => d.IsMissing);
                    break;
                case "Old":
                    // Drivers older than 2 years (Date string: "YYYY-MM-DD" or "MM/DD/YYYY")
                    list = list.Where(d =>
                    {
                        if (d.IsMissing || string.IsNullOrEmpty(d.Date)) return false;
                        if (!DateTime.TryParse(d.Date, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var dt)) return false;
                        return (DateTime.Now - dt).TotalDays > 730; // > 2 ani
                    });
                    break;
                case "Unsigned":
                    list = list.Where(d => !d.IsSigned);
                    break;
                case "Important":
                    list = list.Where(d => _importantCategories.Contains(GetSimplifiedCategory(d)));
                    break;
            }

            if (!string.IsNullOrEmpty(searchText))
                list = list.Where(d =>
                    d.Name.ToLowerInvariant().Contains(searchText) ||
                    (d.Manufacturer?.ToLowerInvariant().Contains(searchText) ?? false) ||
                    (d.HardwareId?.ToLowerInvariant().Contains(searchText) ?? false) ||
                    (d.DeviceClass?.ToLowerInvariant().Contains(searchText) ?? false));

            var filtered = list.ToList();
            _filteredDevices = filtered;

            DriversBasicList.Children.Clear();
            UpdateDriversAttention();

            // Update the counter
            if (TxtDriverCount != null)
            {
                // Rezumat: dispozitive · probleme · nesemnate
                int issues   = filtered.Count(d => d.IsMissing);
                int unsigned = filtered.Count(d => !d.IsSigned);
                var parts = new List<string> { $"{filtered.Count} device{(filtered.Count != 1 ? "s" : "")}" };
                if (issues > 0)   parts.Add($"{issues} with issues");
                if (unsigned > 0) parts.Add($"{unsigned} unsigned");
                if (issues == 0 && unsigned == 0) parts.Add("no problems");
                TxtDriverCount.Text = string.Join(" · ", parts);
            }

            if (!filtered.Any())
            {
                DriversBasicList.Children.Add(new TextBlock
                {
                    Text       = filter == "Missing" ? _LS("Drivers", "NoProblems") : _LS("Drivers", "NoDevicesFound"),
                    Foreground = _brGreen,
                    FontSize   = Ui.FontL,
                    Margin     = new Thickness(16)
                });
                return;
            }

            // ── Driver categories ────────────────────────────────────────────────
            // Each category = a separate card on the page background (header with icon, name, chips, an arrow that rotates).
            // Colours come from the theme (SetResourceReference). Red appears ONLY on the row with the problem and on the
            // "N issues" chip in the header — not on the whole group. The driver age is a neutral chip.
            var cRed   = WpfColor.FromRgb(239, 68, 68);
            var cAmber = WpfColor.FromRgb(245, 158, 11);
            var cGreen = WpfColor.FromRgb(34, 197, 94);
            SolidColorBrush Tint(WpfColor c, byte a) => new SolidColorBrush(WpfColor.FromArgb(a, c.R, c.G, c.B));

            Border Chip(string text, WpfColor? color, string? tip = null)
            {
                var tb = new TextBlock { Text = text, FontSize = Ui.FontXS, FontWeight = FontWeights.SemiBold };
                var b = new Border
                {
                    CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 1, 7, 1), BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Child = tb, ToolTip = tip,
                };
                if (color is WpfColor c)
                {
                    tb.Foreground = new SolidColorBrush(c);
                    b.Background = Tint(c, 30); b.BorderBrush = Tint(c, 70);
                }
                else
                {
                    tb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    b.SetResourceReference(Border.BackgroundProperty, "BgInputBrush");
                    b.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                }
                return b;
            }

            var groups = filtered
                .GroupBy(d => GetSimplifiedCategory(d))
                .OrderBy(g => g.Any(d => d.IsMissing || d.ErrorCode != 0) ? 0 : 1)
                .ThenBy(g => CategoryDisplay(g.Key));

            foreach (var grp in groups)
            {
                int  total    = grp.Count();
                int  problems = grp.Count(d => d.IsMissing || d.ErrorCode != 0);
                int  unsignedN = grp.Count(d => !d.IsSigned && !(d.IsMissing || d.ErrorCode != 0));
                bool startExpanded = problems > 0;

                // ── Antet categorie ────────────────────────────────────────────────
                var hdr = new Border { Padding = new Thickness(12, 10, 12, 10), Cursor = System.Windows.Input.Cursors.Hand, Background = System.Windows.Media.Brushes.Transparent };
                var hg = new Grid();
                hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var chevron = new System.Windows.Shapes.Path
                {
                    Data = System.Windows.Media.Geometry.Parse("M9,6 L15,12 L9,18"), Width = 10, Height = 10,
                    Stretch = System.Windows.Media.Stretch.Uniform, StrokeThickness = 2,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
                    RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                    RenderTransform = new RotateTransform(startExpanded ? 90 : 0),
                };
                chevron.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextSecondaryBrush");
                hg.Children.Add(chevron);

                var iconTile = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                iconTile.SetResourceReference(Border.BackgroundProperty, "BgInputBrush");
                var catIcon = new System.Windows.Shapes.Path
                {
                    Data = SMDWin.Services.FluentIcons.Get(GetCategoryIconPath(grp.Key)),
                    Width = 16, Height = 16, Stretch = System.Windows.Media.Stretch.Uniform,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                };
                if (problems > 0) catIcon.Fill = new SolidColorBrush(cRed);
                else catIcon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextPrimaryBrush");
                iconTile.Child = catIcon;
                Grid.SetColumn(iconTile, 1); hg.Children.Add(iconTile);

                var catLabel = new TextBlock { Text = CategoryDisplay(grp.Key), FontSize = Ui.FontM, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                catLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                Grid.SetColumn(catLabel, 2); hg.Children.Add(catLabel);

                var chips = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                if (problems > 0) chips.Children.Add(Chip(problems == 1 ? "1 issue" : $"{problems} issues", cRed));
                if (unsignedN > 0) chips.Children.Add(Chip($"{unsignedN} unsigned", cAmber, "Driver without a valid digital signature"));
                chips.Children.Add(Chip(total.ToString(), null, total == 1 ? "1 device" : $"{total} devices"));
                Grid.SetColumn(chips, 3); hg.Children.Add(chips);
                hdr.Child = hg;
                hdr.MouseEnter += (_, _) => hdr.SetResourceReference(Border.BackgroundProperty, "BgHoverBrush");
                hdr.MouseLeave += (_, _) => hdr.Background = System.Windows.Media.Brushes.Transparent;

                // ── The device rows ───────────────────────────────────────
                var devPanel = new StackPanel { Visibility = startExpanded ? Visibility.Visible : Visibility.Collapsed };
                foreach (var dev in grp.OrderBy(d => (d.IsMissing || d.ErrorCode != 0) ? 0 : !d.IsSigned ? 1 : 2).ThenBy(d => d.Name))
                {
                    bool isProblem  = dev.IsMissing || dev.ErrorCode != 0;
                    bool isUnsigned = !dev.IsSigned;

                    var devBorder = new Border
                    {
                        BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(50, 8, 12, 8),
                        Cursor = System.Windows.Input.Cursors.Hand, Tag = dev,
                        Background = isProblem ? Tint(cRed, 22) : System.Windows.Media.Brushes.Transparent,
                    };
                    devBorder.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                    if (!isProblem)
                    {
                        devBorder.MouseEnter += (_, _) => devBorder.SetResourceReference(Border.BackgroundProperty, "BgHoverBrush");
                        devBorder.MouseLeave += (_, _) => devBorder.Background = System.Windows.Media.Brushes.Transparent;
                    }

                    var devRow = new Grid();
                    devRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    devRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    devRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    devRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var led = new Ellipse
                    {
                        Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
                        Fill = new SolidColorBrush(isProblem ? cRed : isUnsigned ? cAmber : cGreen),
                    };
                    devRow.Children.Add(led);

                    var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    var nameTb = new TextBlock
                    {
                        Text = dev.Name, FontSize = Ui.FontS, TextTrimming = TextTrimming.CharacterEllipsis,
                        FontWeight = isProblem ? FontWeights.SemiBold : FontWeights.Normal,
                    };
                    if (isProblem) nameTb.Foreground = new SolidColorBrush(cRed);
                    else nameTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                    infoStack.Children.Add(nameTb);

                    string sub = isProblem ? $"Status: {dev.Status}"
                               : isUnsigned ? _LS("Drivers", "UnsignedPlain") + (string.IsNullOrEmpty(dev.HardwareId) ? "" : " · " + dev.HardwareId)
                               : dev.HardwareId ?? "";
                    if (!string.IsNullOrEmpty(sub))
                    {
                        var subTb = new TextBlock { Text = sub, FontSize = Ui.FontXS, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                        if (isUnsigned && !isProblem) subTb.Foreground = new SolidColorBrush(cAmber);
                        else subTb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                        infoStack.Children.Add(subTb);
                    }
                    Grid.SetColumn(infoStack, 1); devRow.Children.Add(infoStack);

                    // only a date Windows can read gets the age chip ("?" appeared for TPM, fingerprint readers, some audio)
                    if (!string.IsNullOrEmpty(dev.Date) && dev.Date != "n/a" && DateTime.TryParse(dev.Date, out _))
                    {
                        var (ageLabel, ageColor, _) = GetDriverAgeBadge(dev.Date);
                        WpfColor? ageClr = ageColor == "#F59E0B" ? cAmber : (WpfColor?)null;
                        var age = Chip(ageLabel, ageClr, $"Driver date: {dev.Date}");
                        Grid.SetColumn(age, 2); devRow.Children.Add(age);
                    }

                    var chev = new TextBlock { Text = "›", FontSize = Ui.FontL, Opacity = 0.4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                    chev.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    Grid.SetColumn(chev, 3); devRow.Children.Add(chev);

                    devBorder.Child = devRow;
                    devBorder.MouseLeftButtonUp += (sender, _) =>
                    {
                        if (sender is FrameworkElement fe && fe.Tag is DeviceManagerEntry d)
                            ShowDriverDetailCard(d);
                    };
                    devPanel.Children.Add(devBorder);
                }

                hdr.MouseLeftButtonUp += (_, _) =>
                {
                    bool expanded = devPanel.Visibility == Visibility.Visible;
                    devPanel.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
                    ((RotateTransform)chevron.RenderTransform).Angle = expanded ? 0 : 90;
                };

                var inner = new StackPanel();
                inner.Children.Add(hdr);
                inner.Children.Add(devPanel);
                var container = new Border
                {
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(0, 0, 0, 8), Child = inner,
                };
                container.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
                container.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                CardClipBehavior.SetRadii(inner, new CornerRadius(8));
                DriversBasicList.Children.Add(container);
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────
        //  ShowDriverDetailCard — fills and shows the card on the right
        // ─────────────────────────────────────────────────────────────────────────────
        /// <summary>Advanced: the row chosen in the driver table appears in the card on the right, as in Basic.</summary>
        private void DriversGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            try
            {
                if (DriversGrid?.SelectedItem is not DriverEntry d) return;
                ShowDriverDetailCard(new DeviceManagerEntry
                {
                    Name = string.IsNullOrWhiteSpace(d.DisplayName) ? d.Name : d.DisplayName,
                    Manufacturer = d.Provider,
                    Version = string.IsNullOrWhiteSpace(d.Version) ? null : d.Version,
                    Date = string.IsNullOrWhiteSpace(d.Date) ? null : d.Date,
                    DateRaw = d.DateRaw,
                    IsSigned = d.IsSigned,
                    Status = d.Status,
                    DeviceId = d.Name,
                });
            }
            catch (Exception ex) { AppLogger.Debug($"DriversGrid_SelectionChanged: {ex.Message}"); }
        }

        private void ShowDriverDetailCard(DeviceManagerEntry dev)
        {
            _selectedDriverEntry = dev;

            string category = GetSimplifiedCategory(dev);

            PathDetailIcon.Data = SMDWin.Services.FluentIcons.Get(GetCategoryIconPath(category));
            TxtDetailName.Text     = dev.Name;
            TxtDetailCategory.Text = CategoryDisplay(category);

            string version  = dev.Version  ?? "n/a";
            string provider = dev.Manufacturer ?? "n/a";
            string date     = dev.Date     ?? "n/a";

            TxtDetailVersion.Text  = version;
            TxtDetailProvider.Text = provider;
            TxtDetailDate.Text     = date;
            TxtDetailVersion.ToolTip  = version  == "n/a" ? "Not reported by this driver" : null;
            TxtDetailProvider.ToolTip = provider == "n/a" ? "Not reported by this driver" : null;
            TxtDetailDate.ToolTip     = date     == "n/a" ? "Not reported by this driver" : null;

            // Age badge
            var (ageLabel, ageColor, _) = GetDriverAgeBadge(date);
            bool hasKnownDate = date != "n/a" && DateTime.TryParse(date, out _);
            DetailAgeBadge.Visibility = hasKnownDate ? Visibility.Visible : Visibility.Collapsed;
            var ageWpfColor = (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(ageColor)!;
            TxtDetailAge.Text      = ageLabel;
            TxtDetailAge.Foreground = new SolidColorBrush(ageWpfColor);
            DetailAgeBadge.Background = new SolidColorBrush(
                WpfColor.FromArgb(35, ageWpfColor.R, ageWpfColor.G, ageWpfColor.B));

            // Status
            TxtDetailStatus.Text = dev.ErrorCode == 0 ? "OK"
                                 : dev.Status;
            TxtDetailStatus.Foreground = dev.ErrorCode == 0
                ? new SolidColorBrush(WpfColor.FromRgb(34, 197, 94))
                : new SolidColorBrush(WpfColor.FromRgb(239, 68, 68));

            // Semnat
            bool signed = dev.IsSigned;
            TxtDetailSigned.Text = signed ? _LS("Common", "Yes") : _LS("Drivers", "UnsignedPlain");
            TxtDetailSigned.Foreground = signed
                ? new SolidColorBrush(WpfColor.FromRgb(34, 197, 94))
                : new SolidColorBrush(WpfColor.FromRgb(245, 158, 11));

            // Hardware ID
            TxtDetailHwId.Text = string.IsNullOrEmpty(dev.HardwareId) ? "—" : dev.HardwareId;

            // LED status
            var (ledClr, glowClr, msg) = GetDriverStatusLed(dev, date);
            var ledColor = (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(ledClr)!;
            DetailLed.Fill = new SolidColorBrush(ledColor);
            DetailLedGlow.Color = ledColor;
            TxtDetailStatusMsg.Text = msg;
            TxtDetailStatusMsg.Foreground = new SolidColorBrush(ledColor);
            TxtDetailStatusMsg.TextWrapping = TextWrapping.Wrap;
            // the state banner in InfoBar style — background and outline in the state colour
            DetailStatusBanner.Background      = new SolidColorBrush(WpfColor.FromArgb(26, ledColor.R, ledColor.G, ledColor.B));
            DetailStatusBanner.BorderBrush     = new SolidColorBrush(WpfColor.FromArgb(90, ledColor.R, ledColor.G, ledColor.B));
            DetailStatusBanner.BorderThickness = new Thickness(1);

            // Energie
            TxtDetailPowerProfile.Text = GetPowerProfile(category);
            TxtDetailPowerEst.Text     = GetPowerEstimate(category, dev.Name);

            // for a device with a problem the natural next step is searching for the driver — "Search Online" becomes the main
            // (coloured) action; otherwise all stay neutral.
            bool devProblem = dev.IsMissing || dev.ErrorCode != 0;
            BtnDetailSearchOnline.Appearance = devProblem
                ? Wpf.Ui.Controls.ControlAppearance.Primary
                : Wpf.Ui.Controls.ControlAppearance.Secondary;

            // Show the animated card column
            DriverDetailCard.Visibility = Visibility.Visible;
            if (DriverDetailPlaceholder != null) DriverDetailPlaceholder.Visibility = Visibility.Collapsed;
        }

        // ─────────────────────────────────────────────────────────────────────────────
        //  HideDriverDetailCard
        // ─────────────────────────────────────────────────────────────────────────────
        private void HideDriverDetailCard()
        {
            DriverDetailCard.Visibility  = Visibility.Collapsed;
            if (DriverDetailPlaceholder != null) DriverDetailPlaceholder.Visibility = Visibility.Visible;
            _selectedDriverEntry = null;
        }

        // ─────────────────────────────────────────────────────────────────────────────
        //  Helper: online driver search
        // ─────────────────────────────────────────────────────────────────────────────
        private void SearchDriverOnline(DeviceManagerEntry dev)
        {
            string url = BuildDriverSearchUrl(dev);
            string name = dev.Name, hwId = dev.HardwareId ?? "";
            LaunchUrl(url, name, hwId);
        }

        private static void LaunchUrl(string url, string name, string hwId)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, $"SearchDriverOnline: launch failed for '{name}' (hwId='{hwId}')");
                throw; // the caller (BulkSearchMissingDrivers) decides what to do with a per-device failure
            }
        }

        private static string BuildDriverSearchUrl(DeviceManagerEntry dev)
        {
            string site = SettingsService.Current.DriverSearchSite ?? "google";
            string url;
            string hwId = dev.HardwareId ?? "";
            string name = dev.Name;

            if (site == "google")
            {
                string q = string.IsNullOrEmpty(hwId) ? name + " driver" : hwId + " driver download";
                url = $"https://www.google.com/search?q={Uri.EscapeDataString(q)}";
            }
            else
            {
                if (!string.IsNullOrEmpty(hwId))
                {
                    string trimmedHwId = hwId;
                    try
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(
                            hwId, @"^([^\\]+\\VEN_[0-9A-Fa-f]+&DEV_[0-9A-Fa-f]+)",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (m.Success) trimmedHwId = m.Groups[1].Value;
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "MainWindow.Events.SearchDriverOnline"); }
                    url = $"https://driverpack.io/en/search?query={Uri.EscapeDataString(trimmedHwId)}";
                }
                else
                {
                    url = $"https://www.google.com/search?q={Uri.EscapeDataString(name + " driver site:driverpack.io")}";
                }
            }

            return url;
        }

        // ─────────────────────────────────────────────────────────────────────────────
        //  Detail card buttons
        // ─────────────────────────────────────────────────────────────────────────────
        private void DriverDetailClose_Click(object s, RoutedEventArgs e)
            => HideDriverDetailCard();

        private void DetailSearchOnline_Click(object s, RoutedEventArgs e)
        {
            if (_selectedDriverEntry != null)
                SearchDriverOnline(_selectedDriverEntry);
        }

        private void CopyDeviceId_Click(object s, RoutedEventArgs e)
        {
            string hwId = TxtDetailHwId.Text;
            if (!string.IsNullOrEmpty(hwId) && hwId != "—")
            {
                System.Windows.Clipboard.SetText(hwId);
                ShowToastSuccess(_LS("Drivers", "DeviceIdCopied"));
            }
        }

        private void DetailBlockDriver_Click(object s, RoutedEventArgs e)
        {
            if (_selectedDriverEntry == null) return;
            string hwId = _selectedDriverEntry.HardwareId ?? "";
            if (string.IsNullOrEmpty(hwId))
            {
                ShowToastWarning(LanguageService.S("Msg", "NoHardwareId"));
                return;
            }
            try
            {
                // Trim to VEN_&DEV_ for cleaner match
                string trimmed = hwId;
                try
                {
                    var m = System.Text.RegularExpressions.Regex.Match(
                        hwId, @"^([^\\]+\\VEN_[0-9A-Fa-f]+&DEV_[0-9A-Fa-f]+)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success) trimmed = m.Groups[1].Value;
                }
                catch (Exception ex) { AppLogger.Warning(ex, "MainWindow.Events.DetailBlockDriver_Click"); }
                if (!ConfirmBlockDriver(trimmed)) return;
                SMDWin.Services.DriverUpdateBlocker.BlockDevice(trimmed);
                ShowToastSuccess(string.Format(LanguageService.S("Msg", "DriverBlockedFmt"), trimmed.Split('\\').LastOrDefault() ?? trimmed));
                RefreshDriverBlockerUI();
            }
            catch (UnauthorizedAccessException)
            {
                AppDialog.Show(_LS("Drivers", "AdminRequiredBlockMsg"), _LS("Drivers", "BlockDriverTitle"), AppDialog.Kind.Warning);
            }
        }

        // DRIVER-BACKUP-ALL: exports ALL third-party drivers to a folder (for reinstalling).
        // Uses Export-WindowsDriver -Online (the same mechanism as DISM /export-driver).
        private void BackupAllDrivers_Click(object sender, RoutedEventArgs e)
        {
            string dest = System.IO.Path.Combine(
                SMDWin.Services.SettingsService.GetEffectiveReportFolder(),
                $"DriversBackup_{DateTime.Now:yyyy-MM-dd}");

            var confirm = System.Windows.MessageBox.Show(
                string.Format(_LS("Drivers", "BackupAllConfirmFmt"), dest),
                _LS("Drivers", "BackupAllTitle"),
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;

            try
            {
                // Export-WindowsDriver -Online exports all non-inbox drivers.
                string cmd = $"New-Item -ItemType Directory -Force -Path \\\"{dest}\\\" | Out-Null; " +
                             $"Export-WindowsDriver -Online -Destination \\\"{dest}\\\" | Out-Null; " +
                             $"Write-Host 'Done. Drivers exported to {dest}'";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = "powershell.exe",
                    Arguments       = $"-NoProfile -ExecutionPolicy Bypass -Command \"{cmd}\"",
                    UseShellExecute = true,
                    Verb            = "runas",   // needs admin for a full export
                });
                ShowToastSuccess(string.Format(_LS("Drivers", "ExportingToFmt"), dest));
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // utilizatorul a refuzat UAC
                ShowToastError(_LS("Drivers", "ExportAdminRequired"));
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "BackupAllDrivers_Click");
                ShowToastError(_LS("Drivers", "ExportStartFailed"));
            }
        }

        private void DetailBackup_Click(object s, RoutedEventArgs e)
        {
            if (_selectedDriverEntry == null) return;

            string name   = _selectedDriverEntry.Name.Replace(" ", "_").Replace("/", "_");
            string hwId   = (_selectedDriverEntry.HardwareId ?? "").Trim();
            string dest   = System.IO.Path.Combine(
                SMDWin.Services.SettingsService.GetEffectiveReportFolder(),
                $"DriverBackup_{name}");

            // Find InfName (e.g. oem15.inf) through WMI Win32_PnPSignedDriver by HardwareId.
            // Without InfName, Export-WindowsDriver exports ALL drivers (4-5 GB).
            string infName = "";
            try
            {
                if (!string.IsNullOrEmpty(hwId))
                {
                    string safeHwId = hwId.Replace("\\", "\\\\").Replace("'", "\\'");
                    using var searcher = SMDWin.Services.WmiHelper.Searcher(
                        @"root\CIMv2",
                        $"SELECT InfName FROM Win32_PnPSignedDriver WHERE HardWareID='{safeHwId}'", System.TimeSpan.FromSeconds(10));
                    using (var __wmiTmp1 = searcher.Get())
                    {
                        foreach (System.Management.ManagementObject obj in __wmiTmp1)
                    {
                        infName = obj["InfName"]?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(infName)) break;
                    }
                    }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "DetailBackup_Click WMI InfName lookup"); }

            // Build the PowerShell command:
            // With InfName → export only the specific driver (a few MB)
            // Without InfName → fallback: pnputil directly on the HardwareId
            string cmd;
            if (!string.IsNullOrEmpty(infName))
            {
                // Export a single driver by inf name — a few MB, fast
                cmd = $"Export-WindowsDriver -Online -Driver '{infName}' -Destination \"{dest}\"";
            }
            else if (!string.IsNullOrEmpty(hwId))
            {
                // Fallback: pnputil exports the driver by HardwareId
                cmd = $"pnputil /export-driver \"{hwId}\" \"{dest}\"";
            }
            else
            {
                ShowToastError(_LS("Drivers", "NoIdForDevice"));
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = "powershell.exe",
                    Arguments       = $"-NoProfile -Command \"{cmd}\"",
                    UseShellExecute = true,
                    Verb            = "runas"
                });
                ShowToastSuccess(string.Format(LanguageService.S("Msg", "DriverBackupStartedFmt"), System.IO.Path.GetFileName(dest)));
            }
            catch (Exception ex)
            {
                ShowToastError(string.Format(LanguageService.S("Msg", "DriverBackupErrorFmt"), ex.Message));
            }
        }

        private void DetailRepair_Click(object s, RoutedEventArgs e)
        {
            if (_selectedDriverEntry == null) return;
            _driverService.OpenDeviceManager();
        }

        // ─────────────────────────────────────────────────────────────────────────────
        //  Handlers kept from the original
        // ─────────────────────────────────────────────────────────────────────────────

        private async void LoadAllDrivers_Click(object s, RoutedEventArgs e)
            => await LoadAllDriversInternalAsync(force: true);

        private void OpenDeviceManager_Click(object s, RoutedEventArgs e)
            => _driverService.OpenDeviceManager();

        private async void LoadUnsignedDrivers_Click(object s, RoutedEventArgs e)
        {
            ShowLoading(_LS("Drivers", "SearchingUnsigned"));
            try
            {
                var drivers = await _driverService.GetUnsignedDriversAsync();
                DriversGrid.ItemsSource = drivers;
                if (drivers.Count == 0)
                    ShowToastSuccess(_LS("Drivers", "NoUnsignedFound"));
            }
            finally { HideLoading(); }
        }

        // ── Driver Update Blocker ─────────────────────────────────────────────

        private void RefreshDriverBlockerUI()
        {
            // Global block status
            bool globalBlocked = SMDWin.Services.DriverUpdateBlocker.IsGlobalBlocked();
            var greenColor = WpfColor.FromRgb(34, 197, 94);
            var redColor   = WpfColor.FromRgb(239, 68, 68);

            if (TxtGlobalBlockStatus != null)
            {
                TxtGlobalBlockStatus.Text       = globalBlocked ? "BLOCKED by policy" : "Auto-update ON";
                TxtGlobalBlockStatus.Foreground = globalBlocked
                    ? new SolidColorBrush(redColor)
                    : new SolidColorBrush(greenColor);
            }
            // LED indicator
            if (LedBlockStatus != null)
            {
                LedBlockStatus.Fill = globalBlocked
                    ? new SolidColorBrush(redColor)
                    : new SolidColorBrush(greenColor);
                if (LedBlockStatus.Effect is System.Windows.Media.Effects.DropShadowEffect fx)
                    fx.Color = globalBlocked ? redColor : greenColor;
            }
            if (BtnToggleGlobalBlock != null)
            {
                BtnToggleGlobalBlock.Content = globalBlocked ? "Allow driver updates" : "Block driver updates";
                BtnToggleGlobalBlock.ToolTip = globalBlocked
                    ? "Windows Update does not install drivers now — click to allow it again"
                    : "Windows Update installs drivers automatically — click to stop it for all devices";
            }

            // Per-device blocked list
            var blocked = SMDWin.Services.DriverUpdateBlocker.GetBlockedDevices();
            if (TxtBlockedCount != null)
                TxtBlockedCount.Text = blocked.Count == 0
                    ? "0 devices blocked"
                    : $"{blocked.Count} device{(blocked.Count > 1 ? "s" : "")} blocked";

            if (BlockedDevicesList != null)
                BlockedDevicesList.ItemsSource = blocked.Select(x => new BlockedDriverItem
                {
                    Index      = x.index,
                    HardwareId = x.hwid,
                    DeviceName = FriendlyNameForHwId(x.hwid),
                }).ToList();

            // Show/hide blocked devices card
            if (BlockedDevicesCard != null)
                BlockedDevicesCard.Visibility = blocked.Count > 0
                    ? Visibility.Visible : Visibility.Collapsed;

            if (TxtNoBlockedDrivers != null)
                TxtNoBlockedDrivers.Visibility = blocked.Count == 0
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string FriendlyNameForHwId(string hwid)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(hwid)) return hwid;
                string key = hwid.Split('&')[0];   // the prefix (e.g. PCI\VEN_10DE)
                using var s = SMDWin.Services.WmiHelper.Searcher(
                    "SELECT Name, HardwareID FROM Win32_PnPEntity");
                using (var __wmiTmp2 = s.Get())
                {
                    foreach (System.Management.ManagementObject o in __wmiTmp2)
                {
                    if (o["HardwareID"] is string[] ids)
                    {
                        foreach (var id in ids)
                        {
                            if (string.IsNullOrEmpty(id)) continue;
                            if (id.Equals(hwid, StringComparison.OrdinalIgnoreCase)
                                || id.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                                return o["Name"]?.ToString() ?? hwid;
                        }
                    }
                }
                }
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }
            return hwid;
        }

        private void BlockGpuDriver_Click(object s, RoutedEventArgs e)
        {
            var devices = _allDevices ?? new List<DeviceManagerEntry>();
            if (devices.Count == 0)
            {
                AppDialog.Show(
                    _LS("Drivers", "LoadDeviceListFirst"),
"SMD Win");
                return;
            }
            var gpuDevices = devices
                .Where(d => string.Equals(d.DeviceClass, "Display", StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(d.HardwareId)
                         && d.HardwareId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (gpuDevices.Count == 0)
            {
                AppDialog.Show(
                    _LS("Drivers", "NoGpuId"),
"SMD Win");
                return;
            }

            // With several graphics cards (e.g. a laptop with integrated + dedicated GPU) the user chooses — we do not guess
            // which one is "the right one".
            DeviceManagerEntry chosen;
            if (gpuDevices.Count == 1)
            {
                chosen = gpuDevices[0];
            }
            else
            {
                var picker = new DevicePickerDialog(gpuDevices) { Owner = this };
                if (picker.ShowDialog() != true || picker.SelectedDevice == null) return;
                chosen = picker.SelectedDevice;
            }

            string id = chosen.HardwareId;
            if (SMDWin.Services.DriverUpdateBlocker.IsDeviceBlocked(id))
            {
                AppDialog.Show(
                    string.Format(_LS("Drivers", "GpuAlreadyBlockedFmt"), id),
"SMD Win");
                return;
            }

            try
            {
                if (!ConfirmBlockDriver(id)) return;
                bool ok = SMDWin.Services.DriverUpdateBlocker.BlockDevice(id);
                if (ok)
                {
                    AppDialog.Show(
                        string.Format(_LS("Drivers", "GpuBlockedFmt"), id),
"SMD Win");
                    RefreshDriverBlockerUI();
                }
                else
                {
                    AppDialog.Show(
                        _LS("Drivers", "RegistryWriteFailed"),
"SMD Win", AppDialog.Kind.Warning);
                }
            }
            catch (UnauthorizedAccessException)
            {
                AppDialog.Show(
                    _LS("Drivers", "AdminRequiredRightClick"),
"SMD Win", AppDialog.Kind.Warning);
            }
        }

        private void BlockCustomDriver_Click(object s, RoutedEventArgs e)
        {
            var devices = _allDevices ?? new List<DeviceManagerEntry>();
            if (devices.Count == 0)
            {
                AppDialog.Show(
                    _LS("Drivers", "LoadDeviceListFirst"),
"SMD Win");
                return;
            }

            var picker = new DevicePickerDialog(devices) { Owner = this };
            if (picker.ShowDialog() != true || picker.SelectedDevice == null) return;
            var chosen = picker.SelectedDevice;

            if (string.IsNullOrWhiteSpace(chosen.HardwareId))
            {
                AppDialog.Show(
                    string.Format(_LS("Drivers", "NoHwIdFoundFmt"), chosen.Name),
"SMD Win", AppDialog.Kind.Warning);
                return;
            }

            if (SMDWin.Services.DriverUpdateBlocker.IsDeviceBlocked(chosen.HardwareId))
            {
                AppDialog.Show(
                    string.Format(_LS("Drivers", "GpuAlreadyBlockedFmt"), chosen.HardwareId),
"SMD Win");
                return;
            }

            try
            {
                if (!ConfirmBlockDriver(chosen.HardwareId)) return;
                SMDWin.Services.DriverUpdateBlocker.BlockDevice(chosen.HardwareId);
                AppDialog.Show(
                    string.Format(_LS("Drivers", "BlockedHwIdFmt"), chosen.HardwareId),
"SMD Win");
                RefreshDriverBlockerUI();
            }
            catch (UnauthorizedAccessException)
            {
                AppDialog.Show(
                    _LS("Drivers", "AdminRequiredSimple"),
"SMD Win", AppDialog.Kind.Warning);
            }
        }

        /// <summary>Confirmation before blocking driver updates for a device.</summary>
        private bool ConfirmBlockDriver(string hardwareId)
        {
            bool ok = AppDialog.Confirm(
                "Block Windows from installing or updating the driver for this device?\n\n" + hardwareId +
                "\n\nThe driver already installed is NOT removed. You can unblock it later from the same list.",
                "Block driver updates");
            ToolActivityLog.Add("Driver blocker", (ok ? "Blocked: " : "Cancelled: ") + hardwareId, ok ? "ok" : "info");
            return ok;
        }

        private void UnblockDriver_Click(object s, RoutedEventArgs e)
        {
            if (s is Button btn && btn.Tag is string idx)
            {
                try
                {
                    SMDWin.Services.DriverUpdateBlocker.UnblockDevice(idx);
                    RefreshDriverBlockerUI();
                }
                catch (UnauthorizedAccessException)
                {
                    AppDialog.Show(
                        _LS("Drivers", "AdminRequiredSimple"),
"SMD Win", AppDialog.Kind.Warning);
                }
            }
        }

        private void ToggleGlobalDriverBlock_Click(object s, RoutedEventArgs e)
        {
            bool currentlyBlocked = SMDWin.Services.DriverUpdateBlocker.IsGlobalBlocked();
            string msg = currentlyBlocked
                ? _LS("Drivers", "RestoreWuConfirm")
                : _LS("Drivers", "BlockWuConfirm");

            if (!AppDialog.Confirm(msg, "SMD Win"))
                return;

            try
            {
                SMDWin.Services.DriverUpdateBlocker.ToggleGlobalBlock(!currentlyBlocked);
                RefreshDriverBlockerUI();
            }
            catch (UnauthorizedAccessException)
            {
                AppDialog.Show(
                    _LS("Drivers", "AdminRequiredSimple"),
"SMD Win", AppDialog.Kind.Warning);
            }
        }

        internal void RefreshDriverModeButtons()
        {
            if (ChkDriverMode == null) return;
            bool basic = _driverViewMode == "Basic";
            SyncDriverModeToggleVisual(basic);
        }
    }
}
