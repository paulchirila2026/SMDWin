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
        // ══════════════════════════════════════════════════════════════════════
        // USB DEVICE HISTORY
        // ══════════════════════════════════════════════════════════════════════
        private record UsbDeviceRecord(string Name, string Manufacturer, string Serial, string DeviceType);
        private List<UsbDeviceRecord> _usbDevices = new();

        private async void UsbScan_Click(object sender, RoutedEventArgs e)
        {
            if (BtnUsbScan != null) { BtnUsbScan.IsEnabled = false; BtnUsbScan.Content = _LS("Common", "ReadingEllipsis"); }
            if (TxtUsbEmpty != null) TxtUsbEmpty.Visibility = Visibility.Collapsed;
            UsbDeviceList?.Children.Clear();
            ShowLoading(_LS("UsbHistory", "Loading"), "");
            try
            {
                var devices = await Task.Run(ReadUsbHistory);
                _usbDevices = devices;
                HideLoading();
                BuildUsbList(devices);
                if (devices.Count == 0 && TxtUsbEmpty != null)
                { TxtUsbEmpty.Text = _LS("UsbHistory", "NoneFound"); TxtUsbEmpty.Visibility = Visibility.Visible; }
            }
            catch (Exception ex)
            {
                HideLoading();
                ShowToastError(_LS("UsbHistory", "ReadErrorPrefix") + " " + ex.Message);
                if (TxtUsbEmpty != null) { TxtUsbEmpty.Text = ex.Message; TxtUsbEmpty.Visibility = Visibility.Visible; }
            }
            finally
            {
                if (BtnUsbScan != null) { BtnUsbScan.IsEnabled = true; BtnUsbScan.Content = _LS("Network", "Scan"); }
            }
        }

        private static List<UsbDeviceRecord> ReadUsbHistory()
        {
            var result = new List<UsbDeviceRecord>();
            var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var root   = Microsoft.Win32.Registry.LocalMachine;

            var hives = new[] {
                (@"SYSTEM\CurrentControlSet\Enum\USBSTOR", "Storage"),
                (@"SYSTEM\CurrentControlSet\Enum\USB","USB"),
                (@"SYSTEM\CurrentControlSet\Enum\HID","HID"),
            };

            foreach (var (hive, devType) in hives)
            {
                using var baseKey = root.OpenSubKey(hive);
                if (baseKey == null) continue;

                foreach (var vidPid in baseKey.GetSubKeyNames())
                {
                    using var vidKey = baseKey.OpenSubKey(vidPid);
                    if (vidKey == null) continue;

                    foreach (var serial in vidKey.GetSubKeyNames())
                    {
                        string key = vidPid + "|" + serial;
                        if (!seen.Add(key)) continue;

                        using var devKey = vidKey.OpenSubKey(serial);
                        if (devKey == null) continue;

                        string name = devKey.GetValue("FriendlyName") as string
                                   ?? devKey.GetValue("DeviceDesc")   as string
                                   ?? vidPid;
                        string mfr  = devKey.GetValue("Mfg") as string ?? "—";
                        string sn   = serial.Length > 30 ? serial[..30] + "…" : serial;

                        // Clean up junk prefixes
                        if (mfr.StartsWith("(Standard") || mfr == "%Mfg%") mfr = "—";
                        if (name.StartsWith("@") || name.StartsWith("%")) name = vidPid;

                        result.Add(new UsbDeviceRecord(name.Trim(), mfr.Trim(), sn, devType));
                    }
                }
            }
            return result.OrderBy(d => d.DeviceType).ThenBy(d => d.Name).ToList();
        }

        private void BuildUsbList(List<UsbDeviceRecord> devices)
        {
            UsbDeviceList?.Children.Clear();
            if (devices.Count == 0) return;

            bool isDark    = ThemeManager.IsDark(SettingsService.Current.ThemeName);
            var clrRow     = isDark ? WpfColor.FromRgb(22, 30, 42)      : WpfColor.FromRgb(255, 255, 255);
            var clrAlt     = isDark ? WpfColor.FromRgb(25, 33, 46)      : WpfColor.FromRgb(245, 248, 252);
            var clrSep     = isDark ? WpfColor.FromArgb(20,255,255,255) : WpfColor.FromArgb(20,0,0,0);
            var clrPrimary = isDark ? WpfColor.FromRgb(203,213,225)     : WpfColor.FromRgb(30,41,59);
            var clrSec     = WpfColor.FromRgb(100,116,139);

            // Group info per type
            var typeInfo = new Dictionary<string,(string icon, WpfColor badgeBg, WpfColor headerAccent)>
            {
                ["Storage"] = ("", WpfColor.FromArgb(40, 59,130,246), WpfColor.FromRgb(59,130,246)),
                ["HID"]     = ("",  WpfColor.FromArgb(40,168, 85,247), WpfColor.FromRgb(168,85,247)),
                ["USB"]     = ("", WpfColor.FromArgb(40, 34,197, 94),  WpfColor.FromRgb(34,197,94)),
            };

            var grouped = devices
                .GroupBy(d => d.DeviceType)
                .OrderBy(g => g.Key == "Storage" ? 0 : g.Key == "HID" ? 1 : 2);

            foreach (var group in grouped)
            {
                var (icon, badgeBg, headerAccent) = typeInfo.TryGetValue(group.Key, out var ti)
                    ? ti : ("", WpfColor.FromArgb(40,150,150,150), WpfColor.FromRgb(150,150,150));

                string typeLabel = group.Key == "Storage" ? _LS("UsbHistory", "TypeStorage")
                                 : group.Key == "HID"      ? _LS("UsbHistory", "TypeHid")
                                 : _LS("UsbHistory", "TypeGeneric");

                // Group header
                var header = new Border
                {
                    Background      = new SolidColorBrush(isDark
                        ? WpfColor.FromArgb(30, headerAccent.R, headerAccent.G, headerAccent.B)
                        : WpfColor.FromArgb(18, headerAccent.R, headerAccent.G, headerAccent.B)),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    BorderBrush     = new SolidColorBrush(WpfColor.FromArgb(60, headerAccent.R, headerAccent.G, headerAccent.B)),
                    Padding         = new Thickness(12, 7, 12, 7),
                    Margin          = new Thickness(0, group == grouped.First() ? 0 : 8, 0, 0),
                };
                var headerRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                headerRow.Children.Add(new TextBlock { Text = icon, FontSize = Ui.FontL, Margin = new Thickness(0,0,7,0), VerticalAlignment = VerticalAlignment.Center });
                headerRow.Children.Add(new TextBlock
                {
                    Text       = typeLabel,
                    FontSize   = Ui.FontS, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(headerAccent),
                    VerticalAlignment = VerticalAlignment.Center
                });
                headerRow.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Background   = new SolidColorBrush(WpfColor.FromArgb(50, headerAccent.R, headerAccent.G, headerAccent.B)),
                    Padding      = new Thickness(7,1,7,1),
                    Margin       = new Thickness(8,0,0,0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = group.Count().ToString(),
                        FontSize = Ui.FontXS, FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(headerAccent)
                    }
                });
                header.Child = headerRow;
                UsbDeviceList?.Children.Add(header);

                // Rows within this group
                int rowIdx = 0;
                foreach (var d in group.OrderBy(x => x.Name))
                {
                    var row = new Border
                    {
                        Background      = new SolidColorBrush(rowIdx % 2 == 1 ? clrAlt : clrRow),
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        BorderBrush     = new SolidColorBrush(clrSep),
                        Padding         = new Thickness(10, 6, 10, 6)
                    };

                    var g = new Grid();
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });

                    // Name
                    var nameTb = new TextBlock { Text = d.Name, FontSize = Ui.FontM, FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(clrPrimary), VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis };
                    Grid.SetColumn(nameTb, 0); g.Children.Add(nameTb);

                    // Manufacturer
                    var mfrTb = new TextBlock { Text = d.Manufacturer, FontSize = Ui.FontXS,
                        Foreground = new SolidColorBrush(clrSec), VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis };
                    Grid.SetColumn(mfrTb, 1); g.Children.Add(mfrTb);

                    // Serial (truncated, with tooltip)
                    var snTb = new TextBlock { Text = d.Serial, FontSize = Ui.FontXS, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                        Foreground = new SolidColorBrush(clrSec), VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = d.Serial };
                    Grid.SetColumn(snTb, 2); g.Children.Add(snTb);

                    row.Child = g;
                    UsbDeviceList?.Children.Add(row);
                    rowIdx++;
                }
            }

            if (TxtUsbEmpty != null) TxtUsbEmpty.Visibility = Visibility.Collapsed;
            ShowToastSuccess($"{devices.Count} " + _LS("UsbHistory", "DevicesFoundSuffix"));
        }

        private void UsbExportCsv_Click(object sender, RoutedEventArgs e)
        {
            if (_usbDevices.Count == 0) { ShowToastWarning(_LS("UsbHistory", "ScanFirstWarning")); return; }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName   = $"usb_history_{System.Environment.MachineName}_{DateTime.Now:yyyyMMdd}",
                DefaultExt = ".csv",
                Filter     = "CSV files|*.csv"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                static string esc(string v) => $"\"{v.Replace("\"", "\"\"")}\"";
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Name,Manufacturer,Serial,Type");
                foreach (var d in _usbDevices)
                    sb.AppendLine(string.Join(",", esc(d.Name), esc(d.Manufacturer), esc(d.Serial), esc(d.DeviceType)));
                System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                ShowToastSuccess("" + _LS("Common", "ExportedPrefix") + " " + System.IO.Path.GetFileName(dlg.FileName));
            }
            catch (Exception ex) { ShowToastError(_LS("Common", "ExportErrorPrefix") + " " + ex.Message); }
        }
    }
}
