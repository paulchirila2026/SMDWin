using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;

// WPF type aliases — resolve ambiguity with System.Drawing / System.Windows.Forms
using Application          = System.Windows.Application;
using Brush                = System.Windows.Media.Brush;
using Brushes              = System.Windows.Media.Brushes;
using Button               = System.Windows.Controls.Button;
using Canvas               = System.Windows.Controls.Canvas;
using CheckBox             = System.Windows.Controls.CheckBox;
using ColorConverter       = System.Windows.Media.ColorConverter;
using ComboBox             = System.Windows.Controls.ComboBox;
using Cursors              = System.Windows.Input.Cursors;
using FontFamily           = System.Windows.Media.FontFamily;
using HorizontalAlignment  = System.Windows.HorizontalAlignment;
using MenuItem             = System.Windows.Controls.MenuItem;
using MessageBox           = System.Windows.MessageBox;
using Orientation          = System.Windows.Controls.Orientation;
using Panel                = System.Windows.Controls.Panel;
using Point                = System.Windows.Point;
using TextBox              = System.Windows.Controls.TextBox;
using VerticalAlignment    = System.Windows.VerticalAlignment;

namespace SMDWin.Views.Net
{
    public class NetFirewallPage : NetToolPage
    {
        private readonly FirewallService _firewallSvc = new();
        private List<FirewallRule>? _allRules;

        private TextBox   _txtFilter = null!;
        private TextBlock _txtStatus = null!;
        private DataGrid  _grid      = null!;
        private Button    _btnLoad   = null!;

        public NetFirewallPage()
        {
            NetWindowHelper.SetupWindow(this, "Firewall Rules — SMDWin", 1180, 620);
            Content = BuildUI();
            Loaded += OnFirstLoaded;
                    }

        private UIElement BuildUI()
        {
            var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Unified header
            var hdr = new Grid { Margin = new Thickness(0,0,0,10) };
            hdr.ColumnDefinitions.Add(new ColumnDefinition());
            hdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var hdrSp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            hdrSp.Children.Add(new TextBlock { Text = "Firewall Rules", FontSize = Ui.FontXL, FontWeight = FontWeights.Bold,
                Foreground = NetWindowHelper.Res("TextPrimaryBrush") });
            hdrSp.Children.Add(new TextBlock { Text = "Windows Defender Firewall — inbound & outbound",
                FontSize = Ui.FontS, Foreground = NetWindowHelper.Res("TextSecondaryBrush"), Margin = new Thickness(0,2,0,0), Opacity = 0.7 });
            Grid.SetColumn(hdrSp, 0); hdr.Children.Add(hdrSp);
            var hdrRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            hdrRight.Children.Add(new TextBlock { Text = "Filter:", VerticalAlignment = VerticalAlignment.Center,
                Foreground = NetWindowHelper.Res("TextSecondaryBrush"), Margin = new Thickness(0,0,8,0) });
            _txtFilter = new TextBox { Style = (Style)System.Windows.Application.Current.Resources["VanillaTextBoxStyle"], Width = 180, Margin = new Thickness(0,0,10,0), ToolTip = "Filter by name, direction, action…" };
            _txtFilter.TextChanged += (_, _) => ApplyFilter();
            hdrRight.Children.Add(_txtFilter);
            _btnLoad = NetWindowHelper.SecondaryButton("↻ Refresh", "Reload current Windows Firewall rules");
            _btnLoad.Click += LoadFirewallRules_Click;
            hdrRight.Children.Add(_btnLoad);
            NetWindowHelper.AddCloseButton(this, hdrRight);
            Grid.SetColumn(hdrRight, 1); hdr.Children.Add(hdrRight);
            Grid.SetRow(hdr, 0); root.Children.Add(hdr);
            NetWindowHelper.MakeDraggable(hdr, this);

            _txtStatus = new TextBlock { Text = "Loading firewall rules…",
                FontSize = Ui.FontS, Foreground = NetWindowHelper.Res("TextSecondaryBrush"), Margin = new Thickness(0,0,0,8) };
            Grid.SetRow(_txtStatus, 1); root.Children.Add(_txtStatus);

            // DataGrid
            _grid = NetWindowHelper.StandardDataGrid();
            _grid.RowHeight = 34;
            NetWindowHelper.EnableRightClickSelect(_grid);

            // Context menu
            var ctxMenu = new ContextMenu();
            var miAllow = new MenuItem { Header = "Allow this rule" };
            miAllow.Click += FirewallAllow_ContextClick;
            var miDeny  = new MenuItem { Header = "Block this rule" };
            miDeny.Click += FirewallDeny_ContextClick;
            var miReload = new MenuItem { Header = "Reload rules" };
            miReload.Click += LoadFirewallRules_Click;
            ctxMenu.Items.Add(miAllow); ctxMenu.Items.Add(miDeny);
            ctxMenu.Items.Add(new Separator()); ctxMenu.Items.Add(miReload);
            _grid.ContextMenu = ctxMenu;

            // Direction col
            var dirCol = new DataGridTemplateColumn { Header = "Dir", Width = new DataGridLength(56) };
            var dirTpl = new DataTemplate();
            var dirFef = new FrameworkElementFactory(typeof(TextBlock));
            dirFef.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("DirectionIcon"));
            dirFef.SetValue(TextBlock.FontSizeProperty, 14.0);
            dirFef.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            dirFef.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            dirTpl.VisualTree = dirFef; dirCol.CellTemplate = dirTpl;
            _grid.Columns.Add(dirCol);

            var nameCol = new DataGridTextColumn { Header = "Rule Name", Binding = new System.Windows.Data.Binding("DisplayName"), Width = new DataGridLength(1.4, DataGridLengthUnitType.Star) };
            nameCol.ElementStyle = new Style(typeof(TextBlock));
            nameCol.ElementStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            nameCol.ElementStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("Name")));
            _grid.Columns.Add(nameCol);

            var appCol = new DataGridTextColumn { Header = "App", Binding = new System.Windows.Data.Binding("AppDisplayName"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) };
            appCol.ElementStyle = new Style(typeof(TextBlock));
            appCol.ElementStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            appCol.ElementStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("AppDisplayName")));
            _grid.Columns.Add(appCol);
            _grid.Columns.Add(new DataGridTextColumn { Header = "Protocol",  Binding = new System.Windows.Data.Binding("Protocol"), Width = new DataGridLength(90) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Port", Binding = new System.Windows.Data.Binding("LocalPorts"), Width = new DataGridLength(90) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Profile",   Binding = new System.Windows.Data.Binding("Profile"), Width = new DataGridLength(150) });

            var statusCol = new DataGridTemplateColumn { Header = "Action", Width = new DataGridLength(120) };
            var statusTpl = new DataTemplate();
            var statusFef = new FrameworkElementFactory(typeof(Button));
            // a flat status chip (rounded, tinted, a coloured dot and the word) instead of the stock button with a coloured
            // border — that one looked like a square outlined box with an emoji
            statusFef.SetValue(Button.TemplateProperty, StatusChipTemplate());
            statusFef.SetBinding(Button.ContentProperty, new System.Windows.Data.Binding("ActionText"));
            statusFef.SetBinding(Button.ToolTipProperty, new System.Windows.Data.Binding("ActionTip"));
            statusFef.SetBinding(Button.TagProperty, new System.Windows.Data.Binding("."));
            statusFef.SetBinding(Button.OpacityProperty, new System.Windows.Data.Binding("Enabled") { Converter = new NetBoolToOpacityConverter() });
            statusFef.SetValue(Button.PaddingProperty, new Thickness(9,0,11,0));
            statusFef.SetValue(Button.FontSizeProperty, 11.5);
            statusFef.SetValue(Button.FontWeightProperty, FontWeights.SemiBold);
            statusFef.SetValue(Button.HeightProperty, 22.0);
            statusFef.SetValue(Button.MinWidthProperty, 72.0);
            statusFef.SetValue(Button.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            statusFef.SetValue(Button.CursorProperty, System.Windows.Input.Cursors.Hand);
            statusFef.SetBinding(Button.ForegroundProperty, new System.Windows.Data.Binding("ActionColor") { Converter = new NetStringToBrushConverter() });
            // coloured background (green = allowed, red = blocked), so the state shows at a glance
            statusFef.SetBinding(Button.BackgroundProperty, new System.Windows.Data.Binding("ActionColor") { Converter = new NetStringToBrushConverter(), ConverterParameter = "tint" });
            statusFef.AddHandler(Button.ClickEvent, new RoutedEventHandler(FirewallStatus_Click));
            statusTpl.VisualTree = statusFef; statusCol.CellTemplate = statusTpl;
            _grid.Columns.Add(statusCol);

            var _phFw = NetWindowHelper.MakePlaceholder("No firewall rules found", "🛡");
            var _fwWrap = new Grid();
            _fwWrap.Children.Add(_grid);
            _fwWrap.Children.Add(_phFw);
            Grid.SetRow(_fwWrap, 2); root.Children.Add(_fwWrap);
            return root;
        }

        private bool _autoLoadDone;
        private void OnFirstLoaded(object sender, RoutedEventArgs e)
        {
            if (_autoLoadDone) return;
            _autoLoadDone = true;
            LoadFirewallRules_Click(this, new RoutedEventArgs());
        }

        private async void LoadFirewallRules_Click(object s, RoutedEventArgs e)
        {
            _txtStatus.Text = LanguageService.S("Network", "LoadingFirewallRules");
            _btnLoad.IsEnabled = false;
            try
            {
                var rules = await _firewallSvc.GetRulesAsync("");
                _allRules = rules;
                ApplyFilter();
                _txtStatus.Text = $"{rules.Count} rules loaded";
            }
            catch (Exception ex) { _txtStatus.Text = LanguageService.S("Common", "ErrorPrefix") + ex.Message; }
            finally { _btnLoad.IsEnabled = true; }
        }

        private void ApplyFilter()
        {
            if (_allRules == null) return;
            string filter = _txtFilter.Text.Trim();
            var filtered = string.IsNullOrEmpty(filter)
                ? _allRules
                : _allRules.Where(r =>
                    (r.Name?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
                    (r.Direction?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
                    (r.Action?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true) ||
                    (r.Profile?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true)).ToList();
            _grid.ItemsSource = filtered;
            _txtStatus.Text = string.IsNullOrEmpty(filter)
                ? $"{_allRules.Count} rules loaded"
                : $"{filtered.Count} / {_allRules.Count} rules (filter: \"{filter}\")";
        }

        private async void FirewallStatus_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not FirewallRule rule) return;
            bool wasAllow = rule.Action == "Allow";
            bool ok = await _firewallSvc.SetRuleActionAsync(rule.Name, allow: !wasAllow);
            if (ok) { rule.Action = wasAllow ? "Block" : "Allow"; _grid.Items.Refresh(); }
            else NetWindowHelper.ShowInfo("Could not change rule — try running as Administrator.", "Firewall", this);
        }

        /// <summary>Rounded tinted chip: a dot in the text colour, then the word; slightly dimmer on hover, dimmer still when pressed.</summary>
        private static ControlTemplate StatusChipTemplate()
        {
            var tpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border), "Bd");
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
            var sp = new FrameworkElementFactory(typeof(StackPanel));
            sp.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            sp.SetValue(StackPanel.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            sp.SetValue(StackPanel.VerticalAlignmentProperty, VerticalAlignment.Center);
            var dot = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
            dot.SetValue(FrameworkElement.WidthProperty, 6.0);
            dot.SetValue(FrameworkElement.HeightProperty, 6.0);
            dot.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
            dot.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            dot.SetValue(System.Windows.Shapes.Shape.FillProperty, new TemplateBindingExtension(Button.ForegroundProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            sp.AppendChild(dot); sp.AppendChild(cp);
            bd.AppendChild(sp);
            tpl.VisualTree = bd;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.82, "Bd"));
            var press = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
            press.Setters.Add(new Setter(UIElement.OpacityProperty, 0.65, "Bd"));
            tpl.Triggers.Add(hover); tpl.Triggers.Add(press);
            return tpl;
        }

        private async void FirewallAllow_ContextClick(object sender, RoutedEventArgs e)
        {
            if (_grid.SelectedItem is not FirewallRule rule) return;
            bool ok = await _firewallSvc.SetRuleActionAsync(rule.Name, allow: true);
            if (ok) { rule.Action = "Allow"; _grid.Items.Refresh(); }
            else NetWindowHelper.ShowInfo("Could not change rule — run as Administrator.", "Firewall", this);
        }

        private async void FirewallDeny_ContextClick(object sender, RoutedEventArgs e)
        {
            if (_grid.SelectedItem is not FirewallRule rule) return;
            bool ok = await _firewallSvc.SetRuleActionAsync(rule.Name, allow: false);
            if (ok) { rule.Action = "Block"; _grid.Items.Refresh(); }
            else NetWindowHelper.ShowInfo("Could not change rule — run as Administrator.", "Firewall", this);
        }
    }
}
