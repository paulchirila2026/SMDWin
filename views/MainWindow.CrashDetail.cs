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
        // ══ The "What happened" card for the selected blue screen ═══════════════════
        // The explanation and steps come from BsodAdvisor; the technical details (code, parameters, file) sit on their own rows,
        // with text that wraps over several lines — the parameters are long.

        private string? _crDetPage;

        private void CrashGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CrashGrid.SelectedItem is CrashEntry c) ShowCrashDetail(c);
        }

        private void ShowCrashDetail(CrashEntry c)
        {
            try
            {
                CrashDetailEmpty.Visibility = Visibility.Collapsed;
                CrashDetailPanel.Visibility = Visibility.Visible;
                var kd = c.KernelInfo;
                string name = !string.IsNullOrEmpty(kd?.BugCheckName) ? kd!.BugCheckName! : c.StopCode;
                var adv = BsodAdvisor.Get(kd?.BugCheckName ?? c.StopCode, c.FaultingModuleKind, kd?.CulpritName, kd?.CulpritIsGeneric ?? false);

                TxtCrDetTitle.Text = string.IsNullOrWhiteSpace(adv.Area) ? name : adv.Area;
                TxtCrDetWhen.Text = $"{c.CrashTime:dd.MM.yyyy HH:mm} · {name}";
                string expl = GetBugCheckExplanation(kd);
                TxtCrDetSummary.Text = string.IsNullOrWhiteSpace(expl) ? adv.Summary : $"{adv.Summary} {expl}";
                TxtCrDetSteps.Text = string.Join("\n", adv.Steps.Select((st, i) => $"{i + 1}. {st}"));

                _crDetPage = adv.Page;
                BtnCrDetPage.Visibility = string.IsNullOrEmpty(adv.Page) ? Visibility.Collapsed : Visibility.Visible;
                BtnCrDetPage.Content = adv.PageLabel ?? "Open";

                TxtCrDetCode.Text = string.IsNullOrWhiteSpace(c.StopCode) ? name : c.StopCode;
                TxtCrDetModule.Text = string.IsNullOrWhiteSpace(c.FaultingModule) ? "not identified" : c.FaultingModule;
                TxtCrDetParams.Text = string.IsNullOrWhiteSpace(c.BugcheckParams) ? "—" : c.BugcheckParams.Replace(", ", "\n").Replace(" · ", "\n");
                TxtCrDetFile.Text = c.FileName;

                var all = (CrashGrid.ItemsSource as IEnumerable<CrashEntry>)?.ToList() ?? new List<CrashEntry>();
                var same = all.Where(x => !ReferenceEquals(x, c) &&
                    ((!string.IsNullOrEmpty(c.FaultingModule) && string.Equals(x.FaultingModule, c.FaultingModule, StringComparison.OrdinalIgnoreCase))
                     || string.Equals(x.StopCode, c.StopCode, StringComparison.OrdinalIgnoreCase))).ToList();
                TxtCrDetSame.Text = same.Count == 0 ? "no — the only one like this"
                    : $"yes · {same.Count} more ({string.Join(", ", same.Take(3).Select(x => x.CrashTime.ToString("dd.MM")))})";
            }
            catch (Exception ex) { AppLogger.Warning(ex, "ShowCrashDetail"); }
        }

        private async void CrDetPage_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_crDetPage)) await NavigateTo(_crDetPage);
        }

        private void CrDetSearch_Click(object sender, RoutedEventArgs e)
        {
            if (CrashGrid.SelectedItem is not CrashEntry c) return;
            string name = !string.IsNullOrEmpty(c.KernelInfo?.BugCheckName) ? c.KernelInfo!.BugCheckName! : c.StopCode;
            try { SearchGoogle($"BSOD {name} {c.FaultingModule}".Trim()); }
            catch (Exception ex) { AppLogger.Warning(ex, "CrDetSearch_Click"); }
        }
    }
}
