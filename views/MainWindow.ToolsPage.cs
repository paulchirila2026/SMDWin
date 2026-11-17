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
        private bool _toolActivityHooked;

        /// <summary>The "Recent activity" card in Tools + "Last export" in the diagnostic package card.</summary>
        private void RefreshToolActivity()
        {
            if (!_toolActivityHooked)
            {
                _toolActivityHooked = true;
                SMDWin.Services.ToolActivityLog.Changed += () => Dispatcher.InvokeAsync(RefreshToolActivity);
            }

            var all = SMDWin.Services.ToolActivityLog.List();
            ToolActivityList.Children.Clear();
            foreach (var en in all.Take(8))
                ToolActivityList.Children.Add(BuildToolActivityRow(en));
            TxtToolActivityNone.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            // with nothing run, the "Recent activity" card (a single sentence in a large card) is hidden and "Snapshots" takes its
            // place too; it appears by itself after the first action run from the page
            if (FindName("ToolsActivityCell") is FrameworkElement actCell && FindName("ToolsSnapshotsCell") is FrameworkElement snapCell)
            {
                bool empty = all.Count == 0;
                actCell.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
                System.Windows.Controls.Grid.SetColumnSpan(snapCell, empty ? 3 : 1);
            }

            var lastExport = all.FirstOrDefault(x => x.Tool == "Diagnostic package" && x.Kind == "ok");
            TxtDiagLastExport.Text = lastExport == null ? PT("never") : SMDWin.Services.DateFormatHelper.FormatDateTime(lastExport.When);
        }

        private static Border BuildToolActivityRow(SMDWin.Services.ToolActivityEntry en)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            var name = new TextBlock { Text = en.Tool, FontWeight = FontWeights.SemiBold, FontSize = Ui.FontM, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            var when = new TextBlock { Text = SMDWin.Services.DateFormatHelper.FormatDateTime(en.When), FontSize = Ui.FontXS };
            when.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            left.Children.Add(name);
            left.Children.Add(when);
            g.Children.Add(left);

            var note = new TextBlock
            {
                Text = en.Note, FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 170, VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
            note.SetResourceReference(TextBlock.ForegroundProperty,
                en.Kind == "ok" ? "StatusSuccessBrush" : en.Kind == "warn" ? "StatusWarningBrush" : "TextSecondaryBrush");
            Grid.SetColumn(note, 1);
            g.Children.Add(note);

            var wrap = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 8, 0, 8), Child = g };
            wrap.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            return wrap;
        }
    }
}
