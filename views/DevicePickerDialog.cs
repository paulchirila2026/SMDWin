using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using WpfColor   = System.Windows.Media.Color;
using WpfBrushes = System.Windows.Media.Brushes;
// ALIAS: avoids CS0104 between System.Windows(.Controls/.Media) and System.Windows.Forms
// (the csproj has UseWindowsForms=true, which brings System.Windows.Forms into scope in ALL files).
using TextBox        = System.Windows.Controls.TextBox;
using Border         = System.Windows.Controls.Border;
using ScrollViewer   = System.Windows.Controls.ScrollViewer;
using StackPanel     = System.Windows.Controls.StackPanel;
using TextBlock      = System.Windows.Controls.TextBlock;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using VerticalAlignment = System.Windows.VerticalAlignment;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Window         = System.Windows.Window;
using Orientation    = System.Windows.Controls.Orientation;
using Cursors        = System.Windows.Input.Cursors;

namespace SMDWin.Views
{
    public class DevicePickerDialog : Window
    {
        /// <summary>The device chosen by the user, or null if the window was closed without choosing.</summary>
        public SMDWin.Models.DeviceManagerEntry? SelectedDevice { get; private set; }

        private readonly List<SMDWin.Models.DeviceManagerEntry> _allDevices;
        private StackPanel _listPanel = null!;
        private TextBox _searchBox = null!;

        private readonly WpfColor _bgColor, _fgColor, _fgSub, _accentClr, _borderClr, _hoverClr;

        public DevicePickerDialog(IEnumerable<SMDWin.Models.DeviceManagerEntry> devices)
        {
            // Only devices with a real Hardware ID — without it there is nothing to block.
            _allDevices = devices
                .Where(d => !string.IsNullOrWhiteSpace(d.HardwareId))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };

            bool isLight = SMDWin.Services.ThemeManager.IsLight(SMDWin.Services.SettingsService.Current.ThemeName) ||
                           (SMDWin.Services.SettingsService.Current.ThemeName == "Auto" &&
                            !SMDWin.Services.ThemeManager.IsDark(SMDWin.Services.ThemeManager.ResolveAuto()));

            WpfColor AppColor(string key, WpfColor fallback) =>
                System.Windows.Application.Current?.TryFindResource(key) is SolidColorBrush b
                    ? b.Color : fallback;

            _bgColor   = AppColor("BgDarkBrush",        isLight ? WpfColor.FromRgb(238,242,247) : WpfColor.FromRgb(19,24,32));
            _fgColor   = AppColor("TextPrimaryBrush",   isLight ? WpfColor.FromRgb(15,23,42)    : WpfColor.FromRgb(255,255,255));
            _fgSub     = AppColor("TextSecondaryBrush", isLight ? WpfColor.FromRgb(71,85,105)   : WpfColor.FromRgb(212,228,247));
            _accentClr = AppColor("AccentBrush",        WpfColor.FromRgb(29,111,232));
            _borderClr = AppColor("CardBorderBrush",    isLight ? WpfColor.FromRgb(203,213,225) : WpfColor.FromRgb(42,51,69));
            _hoverClr  = AppColor("BgCardBrush",         isLight ? WpfColor.FromRgb(255,255,255) : WpfColor.FromRgb(28,36,49));

            Title                 = "Select a device to block";
            SizeToContent         = SizeToContent.Manual;
            Width                 = 560;
            Height                = 520;
            WindowStyle           = WindowStyle.None;
            ResizeMode            = ResizeMode.NoResize;
            AllowsTransparency    = true;
            Background            = new SolidColorBrush(WpfColor.FromArgb(0, 0, 0, 0));
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar         = false;

            Content = BuildUI();
            Loaded += (_, _) => { _searchBox.Focus(); Populate(""); };
        }

        private UIElement BuildUI()
        {
            double radiusCardForWin = (System.Windows.Application.Current.Resources["RadiusCard"] as CornerRadius?)?.TopLeft ?? 8;

            var shadow = new Grid();
            shadow.Children.Add(new Border
            {
                Margin       = new Thickness(12),
                CornerRadius = new CornerRadius(radiusCardForWin),
                Background   = new SolidColorBrush(WpfColor.FromArgb(140, 0, 0, 0)),
                Effect       = new System.Windows.Media.Effects.BlurEffect { Radius = 18 },
                IsHitTestVisible = false,
            });

            var outer = new Border
            {
                Margin          = new Thickness(12),
                CornerRadius    = new CornerRadius(radiusCardForWin),
                Background      = new SolidColorBrush(_bgColor),
                BorderBrush     = new SolidColorBrush(_borderClr),
                BorderThickness = new Thickness(1.5),
                ClipToBounds    = true,
            };
            shadow.Children.Add(outer);

            var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // title
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // subtitle
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // search
            root.RowDefinitions.Add(new RowDefinition());                            // list
            outer.Child = root;

            outer.MouseLeftButtonDown += (sender, ev) => { if (ev.OriginalSource == outer) DragMove(); };

            // ── Title row ────────────────────────────────────────────────────
            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition());
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleTb = new TextBlock
            {
                Text = "Select a device",
                FontSize = Ui.FontXL, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(_fgColor)
            };
            Grid.SetColumn(titleTb, 0);
            var closeBtn = Ui.MakeCloseButton(_fgSub, Close);
            Grid.SetColumn(closeBtn, 1);
            titleGrid.Children.Add(titleTb);
            titleGrid.Children.Add(closeBtn);
            Grid.SetRow(titleGrid, 0);
            root.Children.Add(titleGrid);

            var subtitleTb = new TextBlock
            {
                Text = "Pick the device whose driver Windows Update should stop replacing.",
                FontSize = Ui.FontS, Foreground = new SolidColorBrush(_fgSub),
                Margin = new Thickness(0, 4, 0, 10), TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(subtitleTb, 1);
            root.Children.Add(subtitleTb);

            // ── Search box ───────────────────────────────────────────────────
            var searchBorder = new Border
            {
                Background      = new SolidColorBrush(_hoverClr),
                BorderBrush     = new SolidColorBrush(_borderClr),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(8),
                Padding         = new Thickness(10, 6, 10, 6),
                Margin          = new Thickness(0, 0, 0, 10),
            };
            _searchBox = new TextBox
            {
                Background = WpfBrushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(_fgColor),
                FontSize = Ui.FontM,
                CaretBrush = new SolidColorBrush(_accentClr),
            };
            _searchBox.TextChanged += (_, _) => Populate(_searchBox.Text);
            searchBorder.Child = _searchBox;
            Grid.SetRow(searchBorder, 2);
            root.Children.Add(searchBorder);

            // ── Device list ──────────────────────────────────────────────────
            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _listPanel = new StackPanel();
            sv.Content = _listPanel;
            Grid.SetRow(sv, 3);
            root.Children.Add(sv);

            return shadow;
        }

        private void Populate(string filter)
        {
            _listPanel.Children.Clear();
            filter = (filter ?? "").Trim();

            var matches = string.IsNullOrEmpty(filter)
                ? _allDevices
                : _allDevices.Where(d =>
                    (d.Name ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    (d.Manufacturer ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    (d.DeviceClass ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    (d.HardwareId ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))
                  .ToList();

            if (matches.Count == 0)
            {
                _listPanel.Children.Add(new TextBlock
                {
                    Text = "No matching devices.",
                    Foreground = new SolidColorBrush(_fgSub),
                    FontSize = Ui.FontS,
                    Margin = new Thickness(4, 12, 4, 4),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                });
                return;
            }

            foreach (var d in matches)
                _listPanel.Children.Add(BuildRow(d));
        }

        private Border BuildRow(SMDWin.Models.DeviceManagerEntry d)
        {
            var row = new Border
            {
                CornerRadius    = new CornerRadius(8),
                Padding         = new Thickness(10, 8, 10, 8),
                Margin          = new Thickness(0, 0, 4, 4),
                Background      = new SolidColorBrush(_hoverClr),
                BorderBrush     = new SolidColorBrush(WpfColor.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                Cursor          = System.Windows.Input.Cursors.Hand,
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // icon
            grid.ColumnDefinitions.Add(new ColumnDefinition());                            // text

            var iconTb = new TextBlock
            {
                Text = d.ClassIcon, FontSize = Ui.FontXXL,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            Grid.SetColumn(iconTb, 0);

            var textSp = new StackPanel();
            textSp.Children.Add(new TextBlock
            {
                Text = d.Name, FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(_fgColor), TextWrapping = TextWrapping.Wrap,
            });
            textSp.Children.Add(new TextBlock
            {
                Text = $"{d.DeviceClass}  ·  {d.HardwareId}",
                FontSize = Ui.FontXS, Foreground = new SolidColorBrush(_fgSub),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
            });
            Grid.SetColumn(textSp, 1);

            grid.Children.Add(iconTb);
            grid.Children.Add(textSp);
            row.Child = grid;

            row.MouseEnter += (_, _) => row.BorderBrush = new SolidColorBrush(WpfColor.FromArgb(140, _accentClr.R, _accentClr.G, _accentClr.B));
            row.MouseLeave += (_, _) => row.BorderBrush = new SolidColorBrush(WpfColor.FromArgb(0, 0, 0, 0));
            row.MouseLeftButtonUp += (_, _) =>
            {
                SelectedDevice = d;
                DialogResult = true;
            };

            return row;
        }
    }
}
