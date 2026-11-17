using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

using Button      = System.Windows.Controls.Button;
using CheckBox    = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;
using WpfColor    = System.Windows.Media.Color;

namespace SMDWin.Views
{
    /// <summary>O optimizare din dialogul „Optimize for Low-End PC”.</summary>
    public sealed class OptimizeItem
    {
        public string IconKey   { get; init; } = "Icon.Optimize";
        public string Label     { get; init; } = "";
        public string Desc      { get; init; } = "";
        public string Group     { get; init; } = "";
        public bool   NeedsAdmin{ get; init; }
        public Action Apply     { get; init; } = () => { };
        public Action Revert    { get; init; } = () => { };
        public Func<bool> IsApplied { get; init; } = () => false;
    }

    /// <summary>
    /// Optimize: DialogKit, groups (Performance / Privacy / Interface) with "x / n applied", a discreet status chip per row,
    /// an "Admin" chip when needed and the app does not run as administrator, the result directly in the footer. The logic
    /// (Apply / Revert / reading the state) stays in MainWindow and comes through OptimizeItem.
    /// </summary>
    public class OptimizeDialog : Window
    {
        private static string OL(string key) => SMDWin.Services.LanguageService.S("Optimize", key);

        private readonly IReadOnlyList<OptimizeItem> _items;
        private readonly List<CheckBox> _cbs = new();
        private readonly List<Border> _stateChips = new();
        private readonly Dictionary<string, TextBlock> _groupCounts = new();
        private readonly bool _isAdmin;
        private TextBlock _subtitle = null!, _selCount = null!, _status = null!;
        private CheckBox _selectAll = null!;
        private Button _applyBtn = null!, _revertBtn = null!;
        private bool _syncingSelectAll;

        public OptimizeDialog(IReadOnlyList<OptimizeItem> items)
        {
            _items = items;
            try { _isAdmin = SMDWin.Services.PermissionHelper.IsRunningAsAdmin(); } catch { _isAdmin = false; }
            // wide enough for the descriptions to fit on at most two lines (on narrow screens: as much as fits)
            DialogKit.SetupDialogWindow(this, OL("WindowTitle"), Math.Min(880, SystemParameters.WorkArea.Width - 40));
            MaxHeight = SystemParameters.WorkArea.Height - 20;
            Content = DialogKit.Shell(BuildUI());
            RefreshStates();
        }

        private UIElement BuildUI()
        {
            var root = new DockPanel { LastChildFill = true };

            var top = new StackPanel();
            top.Children.Add(DialogKit.Header(this, "Icon.Optimize", OL("DialogTitle"), "", out _subtitle));
            if (!_isAdmin && _items.Any(i => i.NeedsAdmin))
            {
                var bannerText = DialogKit.Text(OL("AdminBanner"), Ui.FontS);
                var banner = new Border
                {
                    CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 10),
                    BorderThickness = new Thickness(1), Child = bannerText,
                    Background  = new SolidColorBrush(WpfColor.FromArgb(24, DialogKit.Amber.R, DialogKit.Amber.G, DialogKit.Amber.B)),
                    BorderBrush = new SolidColorBrush(WpfColor.FromArgb(80, DialogKit.Amber.R, DialogKit.Amber.G, DialogKit.Amber.B)),
                };
                top.Children.Add(banner);
            }
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            var footer = BuildFooter();
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            // List: groups, 2 cards per row
            var list = new StackPanel();
            bool firstGroup = true;
            foreach (var grp in _items.Select(i => i.Group).Distinct().ToList())
            {
                var label = DialogKit.SectionLabel(grp, " ", firstGroup ? 0 : 8);
                if (label.Children.Count > 1 && label.Children[1] is TextBlock countTb) _groupCounts[grp] = countTb;
                list.Children.Add(label);
                firstGroup = false;

                var grid = new UniformGrid { Columns = 2 };
                foreach (var it in _items.Where(i => i.Group == grp))
                {
                    var card = BuildRow(it);
                    card.Margin = new Thickness(0, 0, 6, 5);
                    card.Padding = new Thickness(12, 7, 12, 7);
                    grid.Children.Add(card);
                }
                list.Children.Add(grid);
            }
            root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, -6, 0) });
            return root;
        }

        private Border BuildRow(OptimizeItem it)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var cb = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) };
            cb.Checked   += (_, _) => OnSelectionChanged();
            cb.Unchecked += (_, _) => OnSelectionChanged();
            _cbs.Add(cb);
            g.Children.Add(cb);

            var icon = DialogKit.Icon(it.IconKey, 18, 0.8);
            icon.Margin = new Thickness(2, 0, 10, 0);
            Grid.SetColumn(icon, 1); g.Children.Add(icon);

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            texts.Children.Add(DialogKit.Text(it.Label, Ui.FontS, semibold: true));
            // The description: at most two lines (a single one on low screens), the rest in the tooltip. With 3–4 line descriptions
            // the list exceeded the screen height and a scroll bar appeared.
            var desc = DialogKit.Text(it.Desc, Ui.FontXS, secondary: true);
            double lineHeight = Math.Ceiling(Ui.FontXS * 1.36);
            desc.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            desc.LineHeight = lineHeight;
            desc.MaxHeight = lineHeight * (SystemParameters.WorkArea.Height < 800 ? 1 : 2);
            desc.TextTrimming = TextTrimming.CharacterEllipsis;
            texts.Children.Add(desc);
            Grid.SetColumn(texts, 2); g.Children.Add(texts);

            var chips = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            var state = DialogKit.Chip("—", DialogKit.Slate);
            _stateChips.Add(state);
            chips.Children.Add(state);
            if (it.NeedsAdmin && !_isAdmin)
            {
                var admin = DialogKit.Chip(OL("AdminChip"), DialogKit.Amber, OL("AdminBanner"));
                admin.Margin = new Thickness(0, 4, 0, 0);
                admin.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
                chips.Children.Add(admin);
            }
            Grid.SetColumn(chips, 3); g.Children.Add(chips);

            var row = DialogKit.RowCard(g, () => cb.IsChecked = !(cb.IsChecked == true));
            row.ToolTip = it.Label + "\n" + it.Desc;
            return row;
        }

        private Border BuildFooter()
        {
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            _selectAll = new CheckBox { Content = OL("SelectDeselectAll"), IsThreeState = true, IsChecked = false, VerticalAlignment = VerticalAlignment.Center, FontSize = Ui.FontS };
            _selectAll.Click += (_, _) =>
            {
                // Click: if not all were checked -> check all; otherwise -> uncheck all
                bool all = _cbs.All(c => c.IsChecked == true);
                _syncingSelectAll = true;
                foreach (var c in _cbs) c.IsChecked = !all;
                _syncingSelectAll = false;
                OnSelectionChanged();
            };
            row.Children.Add(_selectAll);
            _selCount = DialogKit.Text("", Ui.FontXS, secondary: true);
            _selCount.VerticalAlignment = VerticalAlignment.Center;
            _selCount.Margin = new Thickness(12, 0, 0, 0);
            row.Children.Add(_selCount);
            left.Children.Add(row);
            _status = DialogKit.Text("", Ui.FontXS, secondary: true);
            _status.Margin = new Thickness(0, 4, 8, 0);
            left.Children.Add(_status);

            _revertBtn = Net.NetWindowHelper.SecondaryButton(OL("RevertSelectedV2"), OL("RevertTip"));
            _revertBtn.Click += (_, _) => RunSelected(revert: true);
            _applyBtn = Net.NetWindowHelper.PrimaryButton(OL("ApplySelected"));
            _applyBtn.Margin = new Thickness(8, 0, 0, 0);
            _applyBtn.Click += (_, _) => RunSelected(revert: false);
            var footer = DialogKit.Footer(left, _revertBtn, _applyBtn);
            OnSelectionChanged();
            return footer;
        }

        private void OnSelectionChanged()
        {
            if (_applyBtn == null || _selectAll == null) return;
            int n = _cbs.Count(c => c.IsChecked == true);
            _applyBtn.Content   = n > 0 ? string.Format(OL("ApplySelectedCount"), n) : OL("ApplySelected");
            _applyBtn.IsEnabled = n > 0;
            _revertBtn.IsEnabled = n > 0;
            _selCount.Text = n > 0 ? string.Format(OL("SelectedCountFmt"), n) : "";
            if (!_syncingSelectAll)
                _selectAll.IsChecked = n == 0 ? false : n == _cbs.Count ? true : (bool?)null;
        }

        private void RefreshStates()
        {
            int applied = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                bool on;
                try { on = _items[i].IsApplied(); } catch { on = false; }
                if (on) applied++;
                DialogKit.SetChip(_stateChips[i], on ? OL("AppliedBadge") : OL("NotAppliedBadge"), on ? DialogKit.Green : DialogKit.Slate);
            }
            foreach (var (grp, tb) in _groupCounts)
            {
                var idx = Enumerable.Range(0, _items.Count).Where(i => _items[i].Group == grp).ToList();
                int a = idx.Count(i => _stateChips[i].Child is TextBlock t && t.Text == OL("AppliedBadge"));
                tb.Text = string.Format(OL("GroupAppliedFmt"), a, idx.Count);
            }
            _subtitle.Text = string.Format(OL("SummaryFmt"), applied, _items.Count);
        }

        private void RunSelected(bool revert)
        {
            var sel = Enumerable.Range(0, _items.Count).Where(i => _cbs[i].IsChecked == true).ToList();
            if (sel.Count == 0) return;
            // confirmation: the exact list of the settings about to change
            string list = string.Join("\n", sel.Select(i => "• " + _items[i].Label));
            string ask = revert
                ? "Restore the original value for these settings?\n\n" + list + "\n\nThe values saved before the first change are put back."
                : "Change these Windows settings?\n\n" + list + "\n\nThe current values are saved first, so Revert can restore them. Every change is written to the Activity log.";
            if (!AppDialog.Confirm(ask, revert ? "Revert settings" : "Apply settings", this)) return;
            int ok = 0, failed = 0, noEffect = 0;
            var prevCursor = System.Windows.Input.Mouse.OverrideCursor;
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try
            {
                foreach (int i in sel)
                {
                    var it = _items[i];
                    bool before = false;
                    try { before = it.IsApplied(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
                    try
                    {
                        if (revert) it.Revert(); else it.Apply();
                        ok++;
                        SMDWin.Services.ToolActivityLog.Add("Optimize", (revert ? "Reverted: " : "Applied: ") + it.Label, "ok");
                        if (!revert && !before && !SafeApplied(it)) noEffect++;
                    }
                    catch (Exception ex) { failed++; SMDWin.Services.ToolActivityLog.Add("Optimize", "Failed: " + it.Label + " — " + ex.Message, "error"); }
                }
            }
            finally { System.Windows.Input.Mouse.OverrideCursor = prevCursor; }

            RefreshStates();
            _syncingSelectAll = true;
            foreach (var c in _cbs) c.IsChecked = false;
            _syncingSelectAll = false;
            OnSelectionChanged();

            string msg = revert
                ? (failed == 0 ? string.Format(OL("RevertResultOk"), ok) : string.Format(OL("RevertResultPartial"), ok, failed))
                : (failed == 0 ? string.Format(OL("ApplyResultOk"), ok) : string.Format(OL("ApplyResultPartial"), ok, failed));
            if (!revert && noEffect > 0) msg += " " + string.Format(OL("NoEffectShortFmt"), noEffect);
            _status.Text = msg;
            _status.Foreground = new SolidColorBrush(failed == 0 && noEffect == 0 ? DialogKit.Green : DialogKit.Amber);
        }

        private static bool SafeApplied(OptimizeItem it) { try { return it.IsApplied(); } catch { return false; } }
    }
}
