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
        // ── RAM Integrity Test ───────────────────────────────────────────────


        /// Show or ✘ badge on each populated module after test finishes.
        /// errorMsg = null → all pass; non-null → all fail (integrity test doesn't report per-module).
        private void SetModuleResults(string? errorMsg)
        {
            // Walk the RamSlotsPanel → motherboard border → slotsRow → slotContainers → stickGrids → overlays
            if (RamSlotsPanel.Children.Count == 0) return;
            if (RamSlotsPanel.Children[0] is not Border mb) return;
            if (mb.Child is not StackPanel row) return;

            bool passed = errorMsg == null;
            foreach (var child in row.Children)
            {
                if (child is not StackPanel slotSp) continue;
                foreach (var sc in slotSp.Children)
                {
                    if (sc is not Grid stickGrid) continue;
                    // Find the result overlay border (second child)
                    if (stickGrid.Children.Count < 2) continue;
                    if (stickGrid.Children[1] is not Border overlay) continue;
                    if (overlay.Child is not TextBlock tb) continue;

                    // Only show on populated modules (first child has non-empty stick)
                    if (stickGrid.Children[0] is Border stick && stick.Opacity > 0.1)
                    {
                        overlay.Visibility = Visibility.Visible;
                        overlay.Background = new SolidColorBrush(passed
                            ? WpfColor.FromArgb(190, 21, 128, 61)
                            : WpfColor.FromArgb(190, 185, 28, 28));
                        tb.Text = passed ? "OK" : "✘ ERR";
                        // Fade out after 4 seconds
                        var fadeTimer = new System.Windows.Threading.DispatcherTimer
                            { Interval = TimeSpan.FromSeconds(4) };
                        fadeTimer.Tick += (_, _) =>
                        {
                            overlay.Visibility = Visibility.Collapsed;
                            fadeTimer.Stop();
                        };
                        fadeTimer.Start();
                    }
                }
            }
        }
    }
}
