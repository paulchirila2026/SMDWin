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
        // ── Fan Speeds card ───────────────────────────────────────────────────
        // FAN-CONTROL: opens the control window. We pass the TempReader and a callback for the current max temperature
        // (CPU/GPU) used by the thermal guard.
        private FanControlWindow? _fanWindow;
        internal void OpenFanControl_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_tempReader == null)
                {
                    AppDialog.Show(_LS("Common", "SensorNotReady"), _LS("Common", "FanControl"));
                    return;
                }
                if (_fanWindow != null && _fanWindow.IsLoaded)
                {
                    _fanWindow.Activate();
                    return;
                }
                _fanWindow = new FanControlWindow(_tempReader, () =>
                {
                    float c = _lastTempSnap?.CpuTemp ?? -1f;
                    float g = _lastTempSnap?.GpuTemp ?? -1f;
                    return Math.Max(c, g);
                })
                {
                    Owner = this,
                    ShowInTaskbar = true   // apare in taskbar → close nu minimizeaza owner
                };
                Ui.FixOwnedWindowMinimize(_fanWindow, this);
                SMDWin.Services.ThemeManager.Apply(
                    SMDWin.Services.SettingsService.Current.ThemeName == "Auto"
                        ? SMDWin.Services.ThemeManager.ResolveAuto()
                        : SMDWin.Services.SettingsService.Current.ThemeName,
                    _fanWindow.Resources);
                _fanWindow.Show();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "OpenFanControl_Click"); }
        }

        private void UpdateFanSpeedsCard(SMDWin.Services.TempSnapshot snap)
        {
            if (FanSpeedsCard == null || FanSpeedsList == null) return;

            if (snap.FanSpeeds == null || snap.FanSpeeds.Count == 0)
            {
                FanSpeedsCard.Visibility = Visibility.Collapsed;
                return;
            }

            FanSpeedsCard.Visibility = Visibility.Visible;

            if (TxtFanSourceLabel != null)
                TxtFanSourceLabel.Text = $"via {snap.Backend ?? "LHM"}";

            // Shorten names: "Nuvoton NCT6795D — Fan #1" → "Fan #1"
            var items = snap.FanSpeeds.Select(f =>
            {
                string shortName = f.Name;
                int dashIdx = f.Name.LastIndexOf('—');
                if (dashIdx >= 0 && dashIdx < f.Name.Length - 2)
                    shortName = f.Name[(dashIdx + 1)..].Trim();
                return new FanEntry { Name = shortName, RpmText = $"{f.Rpm:F0} RPM" };
            }).ToList();

            FanSpeedsList.ItemsSource = items;
        }


        /// <summary>The same hue, with the lightness (HSL) increased by <paramref name="amount"/> (0..1).</summary>
        private static WpfColor LightenHsl(WpfColor c, double amount)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double h = 0, sat = 0, l = (max + min) / 2;
            double d = max - min;
            if (d > 1e-9)
            {
                sat = l > 0.5 ? d / (2 - max - min) : d / (max + min);
                if (max == r)      h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
                else if (max == g) h = ((b - r) / d + 2) / 6;
                else               h = ((r - g) / d + 4) / 6;
            }
            l = Math.Min(0.95, l + amount);
            double Hue2Rgb(double p, double q, double t)
            {
                if (t < 0) t += 1; if (t > 1) t -= 1;
                if (t < 1.0 / 6) return p + (q - p) * 6 * t;
                if (t < 1.0 / 2) return q;
                if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
                return p;
            }
            double q2 = l < 0.5 ? l * (1 + sat) : l + sat - l * sat;
            double p2 = 2 * l - q2;
            byte To(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
            return WpfColor.FromArgb(c.A, To(Hue2Rgb(p2, q2, h + 1.0 / 3)), To(Hue2Rgb(p2, q2, h)), To(Hue2Rgb(p2, q2, h - 1.0 / 3)));
        }
    }
}
