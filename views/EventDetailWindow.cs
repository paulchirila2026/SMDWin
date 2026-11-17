using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SMDWin.Services;

using Application         = System.Windows.Application;
using Brush               = System.Windows.Media.Brush;
using Brushes             = System.Windows.Media.Brushes;
using Button              = System.Windows.Controls.Button;
using Canvas              = System.Windows.Controls.Canvas;
using FontFamily          = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox          = System.Windows.MessageBox;
using Orientation         = System.Windows.Controls.Orientation;
using Rectangle           = System.Windows.Shapes.Rectangle;
using ToolTip             = System.Windows.Controls.ToolTip;
using MouseEventArgs      = System.Windows.Input.MouseEventArgs;
using Cursors             = System.Windows.Input.Cursors;
using VerticalAlignment   = System.Windows.VerticalAlignment;
using WpfColor            = System.Windows.Media.Color;
using WpfPoint            = System.Windows.Point;

namespace SMDWin.Views
{
    // ── Event Detail Window ───────────────────────────────────────────────────
    internal class EventDetailWindow : Window
    {
        private static readonly CultureInfo _uiCulture =
            LanguageService.UiCulture;

        // Data passed from PowerHistoryWindow
        public record EventData(DateTime Time, string KindLabel, string KindIcon, string KindColor,
                                string Duration, string Notes, bool IsWarning);

        public EventDetailWindow(EventData ev, IEnumerable<EventData> allEvents)
        {
            BuildWindow(ev, allEvents.ToList());
            Loaded += (_, _) => SMDWin.Services.Tx.Tree(this);
        }

        private void BuildWindow(EventData ev, List<EventData> all)
        {
            Title = $"Event Details — {ev.Time:dd MMM yyyy HH:mm:ss}";
            Width = 620; Height = 480;
            MinWidth = 480; MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode    = ResizeMode.CanResize;
            WindowStyle   = WindowStyle.None;
            AllowsTransparency = true;
            Background    = Brushes.Transparent;
            ShowInTaskbar = false;

            var chrome = new System.Windows.Shell.WindowChrome
            {
                CaptionHeight         = 0,
                ResizeBorderThickness = new Thickness(6),
                CornerRadius          = new CornerRadius(0),
                GlassFrameThickness   = new Thickness(0),
                UseAeroCaptionButtons = false
            };
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, chrome);

            Brush Res(string k) => (Brush)Application.Current.Resources[k];

            var shell = new Border
            {
                Background      = Res("BgDarkBrush"),
                BorderBrush     = Res("CardBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius((System.Windows.Application.Current.Resources["RadiusCard"] as CornerRadius?)?.TopLeft ?? 8),
            };

            var shellGrid = new Grid();
            shellGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // title bar
            shellGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // content

            // Own title bar
            var titleBar = new Border
            {
                Background = Res("BgCardBrush"),
                Padding    = new Thickness(16, 0, 8, 0),
                Height     = 40
            };
            // Rounded corners only at the top
            titleBar.CornerRadius = new CornerRadius(10, 10, 0, 0);

            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition());
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleTb = new TextBlock
            {
                Text = $"Event Details — {ev.Time:dd MMM yyyy  HH:mm:ss}",
                FontSize = Ui.FontM, FontWeight = FontWeights.SemiBold,
                Foreground = Res("TextPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            titleGrid.Children.Add(titleTb);

            var btnClose2 = Net.NetWindowHelper.CloseIconButton(32, Ui.FontL);
            btnClose2.VerticalAlignment = VerticalAlignment.Center;
            btnClose2.HorizontalAlignment = HorizontalAlignment.Right;
            btnClose2.Click += (_, _) => Close();
            Grid.SetColumn(btnClose2, 1); titleGrid.Children.Add(btnClose2);

            titleBar.Child = titleGrid;
            // The title is the drag zone
            titleBar.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 1) DragMove();
            };
            Grid.SetRow(titleBar, 0); shellGrid.Children.Add(titleBar);

            // ── Content ────────────────────────────────────────────────────────
            var outer = new Grid { Margin = new Thickness(20) };
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Separator sub title bar
            var sep = new Border { Height = 1, Background = Res("CardBorderBrush"), Opacity = 0.5, Margin = new Thickness(0, 0, 0, 16) };
            Grid.SetRow(sep, 0); outer.Children.Add(sep);

            WpfColor accentClr = WpfColor.FromRgb(96, 165, 250);
            try { accentClr = (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(ev.KindColor)!; } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }

            var hdrBorder = new Border
            {
                BorderBrush = new SolidColorBrush(WpfColor.FromArgb(60, accentClr.R, accentClr.G, accentClr.B)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 0, 0, 14), Margin = new Thickness(0, 0, 0, 16)
            };
            var hdrSp = new StackPanel();
            hdrSp.Children.Add(new TextBlock
            {
                Text = $"{ev.KindIcon}  {ev.KindLabel}",
                FontSize = Ui.FontSectionTitle, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(accentClr)
            });
            hdrSp.Children.Add(new TextBlock
            {
                Text = ev.Time.ToString("dddd, d MMMM yyyy  —  HH:mm:ss", _uiCulture),
                FontSize = Ui.FontM, Foreground = Res("TextSecondaryBrush"), Margin = new Thickness(0, 4, 0, 0)
            });
            hdrBorder.Child = hdrSp;
            Grid.SetRow(hdrBorder, 0); outer.Children.Add(hdrBorder);

            // ── Body ─────────────────────────────────────────────────────────
            var bodyScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var body = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

            void AddRow(string label, string value)
            {
                if (string.IsNullOrWhiteSpace(value) || value == "—") return;
                var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new TextBlock
                {
                    Text = label, FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold,
                    Foreground = Res("TextSecondaryBrush"), VerticalAlignment = VerticalAlignment.Top
                });
                var valTb = new TextBlock
                {
                    Text = value, FontSize = Ui.FontS, Foreground = Res("TextPrimaryBrush"),
                    TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top
                };
                Grid.SetColumn(valTb, 1); row.Children.Add(valTb);
                body.Children.Add(row);
            }

            AddRow("Event type:", ev.KindLabel);
            AddRow("Date / Time:", ev.Time.ToString("dd MMM yyyy  HH:mm:ss"));
            AddRow("Duration:", ev.Duration);
            AddRow("Notes:", ev.Notes);

            // Earlier occurrences of the same event type
            var similar = all
                .Where(e => e.KindLabel == ev.KindLabel && e.Time != ev.Time && !e.IsWarning)
                .OrderByDescending(e => e.Time)
                .Take(10)
                .ToList();

            if (similar.Any())
            {
                body.Children.Add(new TextBlock
                {
                    Text = $"Previous occurrences of \"{ev.KindLabel}\" ({similar.Count} shown):",
                    FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold,
                    Foreground = Res("TextSecondaryBrush"), Margin = new Thickness(0, 12, 0, 6)
                });
                foreach (var s in similar)
                {
                    var pill = new Border
                    {
                        Background = new SolidColorBrush(WpfColor.FromArgb(14, 255, 255, 255)),
                        CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 4, 8, 4),
                        Margin = new Thickness(0, 0, 0, 3)
                    };
                    pill.Child = new TextBlock
                    {
                        Text = $"{s.Time:ddd dd MMM yyyy  HH:mm:ss}" + (s.Duration != "—" ? $"   ({s.Duration})" : ""),
                        FontSize = Ui.FontS, Foreground = Res("TextPrimaryBrush")
                    };
                    body.Children.Add(pill);
                }
            }

            bodyScroll.Content = body;
            Grid.SetRow(bodyScroll, 1); outer.Children.Add(bodyScroll);

            // ── Buttons ───────────────────────────────────────────────────────
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };

            Button MakeBtn(string label)
            {
                var b = new Button { Content = label, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand };
                if (Application.Current.Resources["OutlineButtonStyle"] is Style s) b.Style = s;
                return b;
            }

            var btnCopy = MakeBtn("Copy");
            btnCopy.Click += (_, _) =>
            {
                string txt = $"[{ev.Time:dd MMM yyyy HH:mm:ss}]  {ev.KindLabel}";
                if (ev.Duration != "—") txt += $"  ({ev.Duration})";
                if (!string.IsNullOrWhiteSpace(ev.Notes)) txt += $"\n{ev.Notes}";
                System.Windows.Clipboard.SetText(txt);
            };

            var btnGoogle = MakeBtn("Search Google");
            btnGoogle.IsEnabled = !string.IsNullOrWhiteSpace(ev.Notes);
            btnGoogle.Click += (_, _) =>
            {
                string q = Uri.EscapeDataString(ev.Notes.Length > 150 ? ev.Notes[..150] : ev.Notes);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    { FileName = $"https://www.google.com/search?q={q}", UseShellExecute = true });
            };

            var btnClose = MakeBtn("Close");
            btnClose.Click += (_, _) => Close();

            btnRow.Children.Add(btnCopy);
            btnRow.Children.Add(btnGoogle);
            btnRow.Children.Add(btnClose);
            Grid.SetRow(btnRow, 2); outer.Children.Add(btnRow);

            // Wrap outer content in shell row 1
            var contentBorder = new Border { Child = outer };
            Grid.SetRow(contentBorder, 1); shellGrid.Children.Add(contentBorder);

            shell.Child = shellGrid;
            // a subtle shadow on its own layer (an effect on the shell would blur all the text in the window)
            Content = Ui.ShadowLayer(shell, shell.CornerRadius, new Thickness(6),
                new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = WpfColor.FromArgb(80, 0, 0, 0), BlurRadius = 18, ShadowDepth = 4, Opacity = 0.6
                }, "BgDarkBrush");
            Foreground = Res("TextPrimaryBrush");
            if (Application.Current.Resources["AppFont"] is FontFamily ff) FontFamily = ff;
        }
    }
}
