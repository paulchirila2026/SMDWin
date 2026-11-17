using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using Brushes     = System.Windows.Media.Brushes;
using Button      = System.Windows.Controls.Button;
using CheckBox    = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;
using ProgressBar = System.Windows.Controls.ProgressBar;
using WpfColor    = System.Windows.Media.Color;

namespace SMDWin.Views
{
    /// <summary>
    /// Auto-Clean dialog: DialogKit (theme colours), the app's icon system, categories grouped by risk
    /// ("Safe to clean" / "Review first"), confirmation with a red button (AppDialog.ConfirmDanger) that names explicitly
    /// what will be lost. The public API (ScanAction / CleanAction / FormatBytes, the constructor) is unchanged.
    /// </summary>
    public class AutoCleanDialog : Window
    {
        // Callbacks supplied by caller
        public Func<Task<Dictionary<string, long>>>? ScanAction  { get; set; }
        public Func<Dictionary<string, bool>, Task<long>>? CleanAction { get; set; }
        public Func<long, string>? FormatBytes { get; set; }

        private sealed record Cat(string Key, string IconKey, string Label, string Desc, bool DefaultOn, bool Review, string Tip);

        private static string SL(string key) => SMDWin.Services.LanguageService.S("AutoClean", key);

        // Prefetch: Windows rebuilds it by itself, and emptying it slows down the first start of applications — minimal real
        // gain, so it goes to "Review first" and is not ticked by default.
        private static List<Cat> GetCategories() => new()
        {
            new("Temp",       "Icon.Folder",   SL("CatTempLabel"),       SL("CatTempDesc"),       true,  false, ""),
            new("Thumb",      "Icon.Display",  SL("CatThumbLabel"),      SL("CatThumbDesc"),      true,  false, ""),
            new("WinUpdate",  "Icon.Download", SL("CatWinUpdateLabel"),  SL("CatWinUpdateDesc"),  false, false, ""),
            new("Prefetch",   "Icon.Startup",  SL("CatPrefetchLabel"),   SL("CatPrefetchDesc"),   false, true,  SL("TipPrefetch")),
            new("EventLog",   "Icon.Log",      SL("CatEventLogLabel"),   SL("CatEventLogDesc"),   false, true,  SL("TipEventLog")),
            new("RecycleBin", "Icon.Delete",   SL("CatRecycleBinLabel"), SL("CatRecycleBinDesc"), false, true,  SL("TipRecycleBin")),
        };

        private readonly List<Cat> _cats;
        private Dictionary<string, long> _sizes = new();
        private readonly Dictionary<string, CheckBox>  _cbs      = new();
        private readonly Dictionary<string, TextBlock> _sizeTxts = new();
        private TextBlock _totalTxt = null!, _statusTxt = null!;
        private Button _scanBtn = null!, _cleanBtn = null!;
        private ProgressBar _busyBar = null!;
        private bool _busy;

        public AutoCleanDialog(bool isLightTheme)
        {
            _cats = GetCategories();
            DialogKit.SetupDialogWindow(this, SL("Title"), 620);
            Content = DialogKit.Shell(BuildUI());
            Loaded += async (_, _) => await DoScan();   // the scan starts by itself
        }

        private UIElement BuildUI()
        {
            var root = new StackPanel();
            root.Children.Add(DialogKit.Header(this, "Icon.Delete", SL("DialogTitle"), SL("SubtitleV2"), out _));

            root.Children.Add(DialogKit.SectionLabel(SL("GroupSafe"), topMargin: 0));
            foreach (var c in _cats.Where(c => !c.Review)) root.Children.Add(BuildRow(c));
            root.Children.Add(DialogKit.SectionLabel(SL("GroupReview"), topMargin: 8));
            foreach (var c in _cats.Where(c => c.Review)) root.Children.Add(BuildRow(c));

            // Subsol: total selectat + stare, Rescan (secundar) + Clean (principal)
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(DialogKit.Text(SL("SelectedLabel"), Ui.FontXS, secondary: true));
            _totalTxt = DialogKit.Text("—", Ui.FontXL, semibold: true);
            left.Children.Add(_totalTxt);
            _statusTxt = DialogKit.Text("", Ui.FontXS, secondary: true);
            _statusTxt.Margin = new Thickness(0, 2, 0, 0);
            left.Children.Add(_statusTxt);

            _scanBtn = Net.NetWindowHelper.SecondaryButton(SL("RescanButton"));
            _scanBtn.Click += async (_, _) => await DoScan();
            _cleanBtn = Net.NetWindowHelper.PrimaryButton(SL("CleanNowButton"));
            _cleanBtn.Margin = new Thickness(8, 0, 0, 0);
            _cleanBtn.IsEnabled = false;
            _cleanBtn.Click += async (_, _) => await DoClean();
            root.Children.Add(DialogKit.Footer(left, _scanBtn, _cleanBtn));

            _busyBar = new ProgressBar { IsIndeterminate = true, Height = 3, Margin = new Thickness(8, 6, 8, 0), Visibility = Visibility.Hidden, BorderThickness = new Thickness(0) };
            _busyBar.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "AccentBrush");
            _busyBar.Background = Brushes.Transparent;
            root.Children.Add(_busyBar);
            return root;
        }

        private Border BuildRow(Cat c)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // checkbox
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // iconita
            g.ColumnDefinitions.Add(new ColumnDefinition());                           // text
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // cip risc
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // marime

            var cb = new CheckBox { IsChecked = c.DefaultOn, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) };
            cb.Checked   += (_, _) => UpdateTotal();
            cb.Unchecked += (_, _) => UpdateTotal();
            _cbs[c.Key] = cb;
            g.Children.Add(cb);

            var icon = DialogKit.Icon(c.IconKey, 18, 0.8);
            icon.Margin = new Thickness(2, 0, 12, 0);
            Grid.SetColumn(icon, 1); g.Children.Add(icon);

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(DialogKit.Text(c.Label, Ui.FontM, semibold: true));
            texts.Children.Add(DialogKit.Text(c.Desc, Ui.FontXS, secondary: true));
            Grid.SetColumn(texts, 2); g.Children.Add(texts);

            if (c.Review)
            {
                var chip = DialogKit.Chip(SL("ReviewChip"), DialogKit.Amber, c.Tip);
                chip.Margin = new Thickness(10, 0, 0, 0);
                Grid.SetColumn(chip, 3); g.Children.Add(chip);
            }

            var size = DialogKit.Text("…", Ui.FontM, semibold: true);
            size.MinWidth = 86; size.TextAlignment = TextAlignment.Right; size.VerticalAlignment = VerticalAlignment.Center;
            size.Margin = new Thickness(12, 0, 0, 0);
            _sizeTxts[c.Key] = size;
            Grid.SetColumn(size, 4); g.Children.Add(size);

            var row = DialogKit.RowCard(g, () => cb.IsChecked = !(cb.IsChecked == true));
            if (!string.IsNullOrEmpty(c.Tip)) row.ToolTip = c.Tip;
            return row;
        }

        private string Fmt(long b) => FormatBytes != null ? FormatBytes(b) : $"{b} B";

        private void SetBusy(bool busy, string status)
        {
            _busy = busy;
            _busyBar.Visibility = busy ? Visibility.Visible : Visibility.Hidden;
            _scanBtn.IsEnabled  = !busy;
            _statusTxt.Text     = status;
            _statusTxt.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            UpdateTotal();
        }

        private async Task DoScan()
        {
            if (ScanAction == null || _busy) return;
            foreach (var tb in _sizeTxts.Values) { tb.Text = "…"; tb.Opacity = 0.6; }
            SetBusy(true, SL("ScanningStatus"));
            try
            {
                _sizes = await ScanAction();
                foreach (var c in _cats)
                {
                    long sz = _sizes.GetValueOrDefault(c.Key);
                    var tb = _sizeTxts[c.Key];
                    tb.Text = sz > 0 ? Fmt(sz) : SL("Empty");
                    tb.Opacity = sz > 0 ? 1.0 : 0.5;
                }
                SetBusy(false, SL("ScanCompleteStatus"));
            }
            catch (Exception ex) { SetBusy(false, string.Format(SL("ScanErrorStatus"), ex.Message)); }
        }

        private async Task DoClean()
        {
            if (CleanAction == null || _busy) return;
            var selected = _cats.ToDictionary(c => c.Key, c => _cbs[c.Key].IsChecked == true);
            if (!selected.Values.Any(v => v)) { _statusTxt.Text = SL("NoCategorySelected"); return; }

            long totalBytes = _cats.Where(c => selected[c.Key]).Sum(c => _sizes.GetValueOrDefault(c.Key));
            var review = _cats.Where(c => c.Review && selected[c.Key]).ToList();
            string msg = review.Count == 0
                ? string.Format(SL("ConfirmDeleteMessage"), Fmt(totalBytes))
                : string.Format(SL("ReviewConfirmFmt"), Fmt(totalBytes),
                                string.Join("\n", review.Select(c => "• " + c.Label + " — " + c.Tip)));
            if (!AppDialog.ConfirmDanger(msg, SL("Title"), SL("CleanNowButton"),
                                         SMDWin.Services.LanguageService.S("Common", "Cancel"), this)) return;

            SetBusy(true, SL("CleaningStatus"));
            _cleanBtn.IsEnabled = false;
            try
            {
                long freed = await CleanAction(selected);
                SetBusy(false, "");
                await DoScan();   // the real sizes after cleaning, with no manual step
                _statusTxt.Text = string.Format(SL("FreedFmt"), Fmt(freed));
                _statusTxt.Foreground = new SolidColorBrush(DialogKit.Green);
            }
            catch (Exception ex) { SetBusy(false, string.Format(SL("ErrorStatus"), ex.Message)); }
        }

        private void UpdateTotal()
        {
            if (_totalTxt == null || _cleanBtn == null) return;
            long total = _cats.Where(c => _cbs.TryGetValue(c.Key, out var cb) && cb.IsChecked == true)
                              .Sum(c => _sizes.GetValueOrDefault(c.Key));
            int n = _cats.Count(c => _cbs.TryGetValue(c.Key, out var cb) && cb.IsChecked == true);
            _totalTxt.Text = n == 0 ? SL("NothingSelected")
                           : _sizes.Count == 0 ? "…"
                           : string.Format(SL("ToFreeFmt"), Fmt(total));
            _cleanBtn.IsEnabled = !_busy && n > 0 && total > 0;
        }
    }
}
