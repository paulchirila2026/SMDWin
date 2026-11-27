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
        // ══ Drivers B: the NEEDS ATTENTION card ═══════════════════════════════════════════
        // All devices without a driver, with an error or with an unsigned driver (from the whole list, not only the current
        // filter), each with the right action. More than 4 rows → two columns, so 10+ fit.
        private bool _driversAttnExpanded;

        private void UpdateDriversAttention()
        {
            try
            {
                if (DriversAttentionList == null) return;
                DriversAttentionList.Children.Clear();

                var missing  = _allDevices.Where(d => d.IsMissing).ToList();
                var errors   = _allDevices.Where(d => !d.IsMissing && d.ErrorCode != 0).ToList();
                var unsigned = _allDevices.Where(d => !d.IsMissing && d.ErrorCode == 0 && !d.IsSigned).ToList();
                int total = missing.Count + errors.Count + unsigned.Count;
                int needDriver = missing.Count + errors.Count;

                DriversAttentionList.Columns = total > 4 ? 2 : 1;
                if (BtnDriversAttnSearchAll != null)
                {
                    BtnDriversAttnSearchAll.Visibility = needDriver > 0 ? Visibility.Visible : Visibility.Collapsed;
                    if (TxtDriversAttnSearchAll != null)
                        TxtDriversAttnSearchAll.Text = needDriver > 1 ? PT("Search all {0} online", needDriver) : PT("Search online");
                }

                if (total == 0)
                {
                    TxtDriversAttnCaption.Text = MainWindow.PT("All good");
                    TxtDriversAttnSub.Text = _allDevices.Count == 0 ? "Scanning devices…" : $"All {_allDevices.Count} devices have a working, signed driver";
                    DriversAttnIcon.Data = SMDWin.Services.FluentIcons.Get("checkmark_circle");
                    return;
                }
                TxtDriversAttnCaption.Text = PT("Needs attention");
                var parts = new List<string>();
                if (missing.Count > 0)  parts.Add($"{missing.Count} without driver");
                if (errors.Count > 0)   parts.Add($"{errors.Count} with errors");
                if (unsigned.Count > 0) parts.Add($"{unsigned.Count} not signed");
                TxtDriversAttnSub.Text = $"{total} of {_allDevices.Count} devices · " + string.Join(" · ", parts);
                DriversAttnIcon.Data = SMDWin.Services.FluentIcons.Get("warning");

                // a short list by default: with many missing drivers it would fill the page and the installed ones would no longer be visible
                var rowsAll = new List<FrameworkElement>();
                foreach (var d in missing)  rowsAll.Add(AttentionRow(d, "StatusCritBrush", "No driver installed" + WhereFrom(d), true));
                foreach (var d in errors)   rowsAll.Add(AttentionRow(d, "StatusCritBrush", $"{(string.IsNullOrWhiteSpace(d.Status) ? "Error" : d.Status)} · code {d.ErrorCode}", true));
                foreach (var d in unsigned) rowsAll.Add(AttentionRow(d, "StatusWarningBrush", "Driver not signed — works, but Windows can't verify who made it", false));
                const int Collapsed = 6;
                bool tooMany = rowsAll.Count > Collapsed;
                foreach (var r in (_driversAttnExpanded || !tooMany) ? rowsAll : rowsAll.Take(Collapsed)) DriversAttentionList.Children.Add(r);
                DriversAttentionList.Columns = DriversAttentionList.Children.Count > 4 ? 2 : 1;
                if (tooMany)
                {
                    var label = new TextBlock
                    {
                        Text = _driversAttnExpanded ? PT("Show fewer") + " ▲" : PT("Show all ({0})", rowsAll.Count) + " ▼",
                        FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 0),
                    };
                    label.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                    var more = new Border
                    {
                        BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 12), Cursor = System.Windows.Input.Cursors.Hand,
                        Background = System.Windows.Media.Brushes.Transparent, Child = label,
                    };
                    more.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                    more.MouseLeftButtonUp += (_, _) => { _driversAttnExpanded = !_driversAttnExpanded; UpdateDriversAttention(); };
                    DriversAttentionList.Children.Add(more);
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "UpdateDriversAttention"); }
        }

        private static string WhereFrom(DeviceManagerEntry d)
            => string.IsNullOrWhiteSpace(d.DeviceClass) ? "" : $" · {d.DeviceClass}";

        private FrameworkElement AttentionRow(DeviceManagerEntry d, string brushKey, string why, bool searchAction)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 14, 0), MinHeight = 44 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brushKey);
            row.Children.Add(dot);

            var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            var name = new TextBlock { Text = string.IsNullOrWhiteSpace(d.Name) ? "(unknown device)" : d.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = d.HardwareId };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var sub = new TextBlock { Text = why, FontSize = Ui.FontS, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = why };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            txt.Children.Add(name); txt.Children.Add(sub);
            Grid.SetColumn(txt, 1); row.Children.Add(txt);

            var btn = new Wpf.Ui.Controls.Button
            {
                Content = searchAction ? PT("Search online") : PT("Details"),
                Appearance = searchAction ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary,
                Padding = new Thickness(12, 4, 12, 4), VerticalAlignment = VerticalAlignment.Center,
            };
            btn.Click += (_, _) =>
            {
                try { if (searchAction) SearchDriverOnline(d); else ShowDriverDetailCard(d); }
                catch (Exception ex) { AppLogger.Warning(ex, "AttentionRow action"); }
            };
            Grid.SetColumn(btn, 2); row.Children.Add(btn);

            // separator under each row
            var wrap = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 6, 0, 6), Child = row };
            wrap.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            wrap.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase) ShowDriverDetailCard(d); };
            wrap.Cursor = System.Windows.Input.Cursors.Hand;
            return wrap;
        }
    }
}
