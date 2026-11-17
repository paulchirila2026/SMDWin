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
        internal void OpenOptimizer_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "control.exe",
                    Arguments = "/name Microsoft.PowerOptions",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppDialog.Show(ex.Message, "SMD Win", AppDialog.Kind.Error);
            }
        }

        // Optional decorative animation on the Power page, when there is no battery — started/stopped explicitly from
        // code-behind (not only Visibility), so storyboards do not run in the background when the option is unchecked.
        // Checked by default (IsChecked="True" in XAML).
        internal void ChkPowerFlowAnimation_Checked(object sender, System.Windows.RoutedEventArgs e)
        {
            if (PowerFlowWidget == null) return;   // during InitializeComponent, before the widget exists
            // the map (and its animation) only from Developer › Design experiments; the settings read runs anyway, for the
            // "What the plan does now" list and the sleep timeline
            if (SettingsService.Current.DesignExperimentPowerMap)
            {
                PowerFlowWidget.Visibility = System.Windows.Visibility.Visible;
                PowerFlowWidget.Start();
            }
            _ = RefreshPowerFlowInfoAsync();
            EnsurePowerLiveTimer(); // live values (W, voltages, CMOS, fans) on the diagram
        }

        /// <summary>
        /// Reads the power settings of the active plan (on AC) and writes them on the components of the Power page animation.
        /// The registry / powercfg reads run on a background thread.
        /// </summary>
        private async System.Threading.Tasks.Task RefreshPowerFlowInfoAsync()
        {
            if (PowerFlowWidget == null) return;
            string L(string en, string ro, string de, string fr) => SMDWin.Services.LanguageService.CurrentCode switch
            { "ro" => ro, "de" => de, "fr" => fr, _ => en };
            string never = L("Never", "Niciodată", "Nie", "Jamais");
            try
            {
                var r = await System.Threading.Tasks.Task.Run(() =>
                {
                    int? pMin = null, pMax = null, sleep = null, disp = null, disk = null, aspm = null;
                    bool? usb = null;
                    SMDWin.Services.PowerAdvancedService.BoostMode? boost = null;
                    try { pMin = SMDWin.Services.PowerAdvancedService.GetMinProcessorState(true); } catch (Exception ex) { AppLogger.Debug($"PowerFlow pMin: {ex.Message}"); }
                    try { pMax = SMDWin.Services.PowerAdvancedService.GetMaxProcessorState(true); } catch (Exception ex) { AppLogger.Debug($"PowerFlow pMax: {ex.Message}"); }
                    try { boost = SMDWin.Services.PowerAdvancedService.GetBoostMode(true); } catch (Exception ex) { AppLogger.Debug($"PowerFlow boost: {ex.Message}"); }
                    try { sleep = SMDWin.Services.PowerAdvancedService.GetSleepAfterSeconds(true); } catch (Exception ex) { AppLogger.Debug($"PowerFlow sleep: {ex.Message}"); }
                    try { disp = SMDWin.Services.PowerAdvancedService.GetDisplayOffAfterSeconds(true); } catch (Exception ex) { AppLogger.Debug($"PowerFlow display: {ex.Message}"); }
                    try { disk = SMDWin.Services.PowerAdvancedService.GetDiskOffAfterSeconds(true); } catch (Exception ex) { AppLogger.Debug($"PowerFlow disk: {ex.Message}"); }
                    try { aspm = SMDWin.Services.PowerAdvancedService.GetPciExpressAspm(true); } catch (Exception ex) { AppLogger.Debug($"PowerFlow aspm: {ex.Message}"); }
                    try { usb = SMDWin.Services.PowerAdvancedService.GetUsbSelectiveSuspend(true); } catch (Exception ex) { AppLogger.Debug($"PowerFlow usb: {ex.Message}"); }
                    return (pMin, pMax, boost, sleep, disp, disk, aspm, usb);
                });

                string plan = TxtActivePowerPlan?.Text is { Length: > 0 } pl && pl != "Unknown" && pl != "—" ? pl : "";
                string cpu = ((r.pMin.HasValue && r.pMax.HasValue ? $"Min {r.pMin}% · Max {r.pMax}%" : "")
                           + (r.boost.HasValue ? $"\nBoost: {SMDWin.Services.PowerAdvancedService.BoostModeLabel(r.boost.Value)}" : "")).Trim();
                string gpu = r.aspm switch
                {
                    0 => L("Link power saving: Off", "Economie legătură: oprită", "Link-Energiesparen: Aus", "Économie de lien : désactivée"),
                    1 => L("Link power saving: Moderate", "Economie legătură: moderată", "Link-Energiesparen: Mittel", "Économie de lien : modérée"),
                    2 => L("Link power saving: Maximum", "Economie legătură: maximă", "Link-Energiesparen: Maximal", "Économie de lien : maximale"),
                    _ => "",
                };
                // the row appears only if the value could actually be read (no "—")
                string display = r.disp.HasValue ? L("Screen off after ", "Ecran oprit după ", "Bildschirm aus nach ", "Écran éteint après ")
                               + SMDWin.Services.PowerAdvancedService.FormatIdle(r.disp, never) : "";
                string ram = r.sleep.HasValue ? L("Sleep after ", "Sleep după ", "Energiesparen nach ", "Veille après ")
                           + SMDWin.Services.PowerAdvancedService.FormatIdle(r.sleep, never) : "";
                string disk = r.disk.HasValue ? L("Turn off after ", "Oprire după ", "Ausschalten nach ", "Arrêt après ")
                            + SMDWin.Services.PowerAdvancedService.FormatIdle(r.disk, never) : "";
                string usb = r.usb switch
                {
                    true  => L("Selective suspend: On", "Suspendare selectivă: pornită", "Selektives Energiesparen: Ein", "Suspension sélective : activée"),
                    false => L("Selective suspend: Off", "Suspendare selectivă: oprită", "Selektives Energiesparen: Aus", "Suspension sélective : désactivée"),
                    _     => "",
                };
                PowerFlowWidget.SetInfo(plan, cpu, gpu, display, ram, disk, usb);
                // the plan changed / was read: the "When the PC is not used" card and the processor steps follow it
                // (11.11.2026 — replaces the read-only "What the plan does now" list and the sleep timeline)
                _ = RefreshPowerSimpleAsync();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "RefreshPowerFlowInfoAsync"); }
        }

        internal void ChkPowerFlowAnimation_Unchecked(object sender, System.Windows.RoutedEventArgs e)
        {
            StopPowerFlowAnimationIfRunning();
        }

        private void StopPowerFlowAnimationIfRunning()
        {
            if (PowerFlowWidget == null) return;
            PowerFlowWidget.Stop();
            PowerFlowWidget.Visibility = System.Windows.Visibility.Collapsed;
            if (ChkPowerFlowAnimation != null) ChkPowerFlowAnimation.IsChecked = false;
        }
    }
}
