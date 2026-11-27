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

        // ── STABILITY ─────────────────────────────────────────────────
        private void StabilityChart_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
            => StabilityChartCtl.ToolTip = StabilityChartCtl.DescribeAt(e.GetPosition(StabilityChartCtl));

        private async Task LoadStabilityAsync()
        {
            List<StabilityService.StabilityEvent> evs;
            try { evs = await StabilityService.GetAsync(30); }
            catch (Exception ex) { AppLogger.Warning(ex, "LoadStabilityAsync"); return; }

            StabilityChartCtl.SetData(evs);

            // Coloured counters (only the types that occurred; "0" for BSOD and shutdowns, as a reference)
            StabilityCounters.Children.Clear();
            foreach (var (kind, color, label) in StabilityChart.Legend)
            {
                int n = evs.Count(x => x.Kind == kind);
                if (n == 0 && kind != StabilityService.Kind.BlueScreen && kind != StabilityService.Kind.UnexpectedShutdown) continue;
                var chip = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6), BorderThickness = new Thickness(1) };
                chip.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2");
                var sp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                sp.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(color), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
                var num = new TextBlock { Text = n.ToString(), FontSize = Ui.FontL, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
                num.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                var lbl = new TextBlock { Text = PT(label), FontSize = Ui.FontS, VerticalAlignment = VerticalAlignment.Center };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                sp.Children.Add(num); sp.Children.Add(lbl);
                chip.Child = sp;
                StabilityCounters.Children.Add(chip);
            }

            // Verdict
            int bsod = evs.Count(x => x.Kind == StabilityService.Kind.BlueScreen);
            int shut = evs.Count(x => x.Kind == StabilityService.Kind.UnexpectedShutdown);
            int hwFatal = evs.Count(x => x.Kind == StabilityService.Kind.Hardware && x.Title.StartsWith("Fatal"));
            int apps = evs.Count(x => x.Kind is StabilityService.Kind.AppCrash or StabilityService.Kind.AppHang);
            string verdict, brushKey;
            if (bsod > 0 || hwFatal > 0) { verdict = bsod > 0 ? string.Format(_LS("Crash", bsod == 1 ? "VerdictBsodOne" : "VerdictBsodMany"), bsod) : _LS("Crash", "VerdictHwFatal"); brushKey = "StatusErrorBrush"; }
            else if (shut > 0)           { verdict = string.Format(_LS("Crash", shut == 1 ? "VerdictShutOne" : "VerdictShutMany"), shut); brushKey = "StatusWarningBrush"; }
            else if (apps > 5)           { verdict = _LS("Crash", "VerdictAppCrashes"); brushKey = "StatusWarningBrush"; }
            else                         { verdict = _LS("Crash", "VerdictStable"); brushKey = "StatusSuccessBrush"; }
            TxtStabilityVerdict.Text = verdict;
            _stabilityBsodCount = bsod;
            UpdateCrashEmptyState();
            TxtStabilityVerdict.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            if (TryFindResource(brushKey) is SolidColorBrush vb)
                StabilityVerdictPill.Background = new SolidColorBrush(WpfColor.FromArgb(34, vb.Color.R, vb.Color.G, vb.Color.B));

            // The most recent problems (max 6) + the applications that crash most often
            StabilityRecentList.Children.Clear();
            // blue screens have their own card (the list from minidumps) — here only the other problems
            var others = evs.Where(x => x.Kind != StabilityService.Kind.BlueScreen).ToList();
            int shown = 0;
            foreach (var ev in others.Take(6))
            {
                // one row: the dot in the type colour · the title, and below the type and the detail · the date on the right
                var legend = StabilityChart.Legend.First(l => l.kind == ev.Kind);
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(legend.color), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(1, 6, 0, 0) });
                var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
                var title = new TextBlock { Text = ev.Title, FontSize = Ui.FontM, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                text.Children.Add(title);
                string sub = string.IsNullOrWhiteSpace(ev.Detail) ? PT(legend.label) : $"{PT(legend.label)} · {ev.Detail}";
                var subTb = new TextBlock { Text = sub, FontSize = Ui.FontS, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0), ToolTip = string.IsNullOrWhiteSpace(ev.Detail) ? null : ev.Detail };
                subTb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                text.Children.Add(subTb);
                Grid.SetColumn(text, 1); row.Children.Add(text);
                var when = new TextBlock { Text = SMDWin.Services.DateFormatHelper.FormatDateTimeSec(ev.Time), FontSize = Ui.FontS, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
                when.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                Grid.SetColumn(when, 2); row.Children.Add(when);
                var line = new Border { Child = row, Padding = new Thickness(0, 9, 0, 9), BorderThickness = new Thickness(0, shown == 0 ? 0 : 1, 0, 0) };
                line.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                StabilityRecentList.Children.Add(line);
                shown++;
            }
            var topApps = evs.Where(x => x.Kind is StabilityService.Kind.AppCrash or StabilityService.Kind.AppHang)
                             .GroupBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                             .OrderByDescending(g => g.Count()).Take(3)
                             .Select(g => $"{g.Key} ({g.Count()}×)").ToList();
            TxtStabilityTopApps.Text = topApps.Count > 0 ? PT("Apps that crash or freeze most often: {0}", string.Join(", ", topApps)) : "";
            StabilityRecentPanel.Visibility = others.Count > 0 || topApps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (TxtOtherProblemsNone != null) TxtOtherProblemsNone.Visibility = others.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private int _crashDumpCount = -1;      // minidumps found (-1 = not read yet)
        private int _stabilityBsodCount;       // blue screens from the stability history (Reliability Monitor)

        /// <summary>
        /// Without minidumps, the "Blue screens" card becomes a single full-width row (no empty table and no "What happened").
        /// The text says the same thing as the verdict on top: if Windows recorded blue screens but did not keep the file, it says so.
        /// </summary>
        private void UpdateCrashEmptyState()
        {
            try
            {
                if (_crashDumpCount < 0) return;
                bool empty = _crashDumpCount == 0;
                if (FindName("CrashEmptyState") is FrameworkElement es) es.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
                CrashGrid.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
                if (FindName("CrashMainRow") is RowDefinition rowDef) rowDef.Height = empty ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
                if (FindName("CrashDetailCard") is FrameworkElement detail) detail.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
                if (FindName("CrashListCard") is FrameworkElement list) Grid.SetColumnSpan(list, empty ? 3 : 1);
                if (!empty) return;

                int n = _stabilityBsodCount;
                if (FindName("TxtCrashEmptyTitle") is TextBlock t)
                    t.Text = n == 0 ? _LS("Crash", "EmptyNoneTitle")
                                    : string.Format(_LS("Crash", n == 1 ? "EmptyNoFileTitleOne" : "EmptyNoFileTitleMany"), n);
                if (FindName("TxtCrashEmptyDetail") is TextBlock d)
                    d.Text = n == 0 ? _LS("Crash", "EmptyNoneDetail") : _LS("Crash", "EmptyNoFileDetail");
                if (FindName("TxtCrashEmptyBtn") is TextBlock b) b.Text = _LS("Crash", "EmptyBtn");
                if (FindName("CrashEmptyDot") is System.Windows.Shapes.Ellipse dot)
                    dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, n == 0 ? "StatusGoodBrush" : "StatusCheckBrush");
            }
            catch (Exception ex) { AppLogger.Warning(ex, "UpdateCrashEmptyState"); }
        }

        /// <summary>Pattern across all BSODs: the same driver / the same area in most of them is the strongest hint (a single
        /// crash may be random).</summary>
        private void UpdateCrashPattern(List<CrashEntry> crashes)
        {
            if (FindName("TxtCrashPattern") is not TextBlock tb) return;
            if (crashes.Count < 2) { tb.Visibility = Visibility.Collapsed; return; }
            var byDriver = crashes.Select(c => c.KernelInfo is { CulpritIsGeneric: false } k ? k.CulpritName : null)
                                  .Where(n => !string.IsNullOrEmpty(n))
                                  .GroupBy(n => n!, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault();
            var byArea = crashes.GroupBy(c => c.AdviceArea).OrderByDescending(g => g.Count()).First();
            string text;
            if (byDriver != null && byDriver.Count() >= 2)
                text = $"Pattern: {byDriver.Key} appears in {byDriver.Count()} of {crashes.Count} blue screens — the most likely cause.";
            else if (byArea.Count() >= 2 && byArea.Key != "Unknown")
                text = $"Pattern: {byArea.Count()} of {crashes.Count} blue screens point to {byArea.Key.ToLowerInvariant()}.";
            else
                text = $"{crashes.Count} blue screens with different causes — often a sign of RAM, power or overheating problems.";
            tb.Text = text;
            tb.Visibility = Visibility.Visible;
        }

        private async Task LoadCrashesInternalAsync()
        {
            _ = LoadStabilityAsync();   // in parallel with the minidumps, does not block the page
            ShowLoading(_LS("Crash", "Loading"));
            try
            {
                var crashes = await _crashService.GetCrashesAsync();
                foreach (var c in crashes)
                {
                    var adv = BsodAdvisor.Get(c.KernelInfo?.BugCheckName ?? c.StopCode, c.FaultingModuleKind,
                                              c.KernelInfo?.CulpritName, c.KernelInfo?.CulpritIsGeneric ?? false);
                    c.AdviceArea = PT(adv.Area);
                    c.AdviceSummary = adv.Summary;
                }
                CrashGrid.ItemsSource = crashes;
                if (TxtCrashListSub != null)
                    TxtCrashListSub.Text = crashes.Count == 0 ? PT("None found in {0}", _crashService.MinidumpFolder)
                        : PT("{0} found in {1} · click one to see what happened", crashes.Count, _crashService.MinidumpFolder);
                if (crashes.Count > 0) CrashGrid.SelectedIndex = 0;
                else { CrashDetailPanel.Visibility = Visibility.Collapsed; CrashDetailEmpty.Visibility = Visibility.Visible; }
                UpdateCrashPattern(crashes);
                int realCrashes = crashes.Count;
                UpdateSidebarBadge("Crash", realCrashes);
                _crashDumpCount = realCrashes;
                UpdateCrashEmptyState();
                if (BtnWinDbg != null) BtnWinDbg.Visibility = Visibility.Visible;
                RefreshWinDbgButton();
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "LoadCrashesInternalAsync");
            }
            finally { HideLoading(); }
        }

        private async void LoadCrashes_Click(object s, RoutedEventArgs e) => await LoadCrashesInternalAsync();
        private void OpenMiniDumpFolder_Click(object s, RoutedEventArgs e) => _crashService.OpenMinidumpFolder();

        private void CrashGrid_DoubleClick(object s, System.Windows.Input.MouseButtonEventArgs e) => ShowCrashMoreInfo();
        private void CrashMoreInfo_Click(object s, RoutedEventArgs e) => ShowCrashMoreInfo();

        private static string GetBugCheckExplanation(SMDWin.Services.KernelDumpInfo? kdInfo)
        {
            if (kdInfo == null) return "";
            if (!string.IsNullOrEmpty(kdInfo.BugCheckName))
            {
                string translated = LanguageService.S("BsodCodes", kdInfo.BugCheckName, "");
                if (!string.IsNullOrEmpty(translated)) return translated;
            }
            return kdInfo.ExplanationRo ?? "";
        }

        private void ShowCrashMoreInfo()
        {
            if (CrashGrid.SelectedItem is not CrashEntry entry)
            {
                ShowToastWarning(_LS("Crash", "SelectFile"));
                return;
            }
            var kd = entry.KernelInfo;

            var win = new Window { Owner = this };
            SMDWin.Views.Net.NetWindowHelper.SetupWindow(win, PT("Crash — {0}", entry.FileName), 760, 680);
            win.WindowStartupLocation = WindowStartupLocation.CenterOwner; // over the CenterScreen from SetupWindow

            var root = SMDWin.Views.Net.NetWindowHelper.BuildWindowRoot(
                _LS("Crash", "AnalysisTitle"), entry.FileName, out var headerGrid);
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 0: header
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 1: content
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 2: butoane

            var actionPanel = SMDWin.Views.Net.NetWindowHelper.GetActionPanel(headerGrid);
            SMDWin.Views.Net.NetWindowHelper.AddCloseButton(win, actionPanel);
            SMDWin.Views.Net.NetWindowHelper.MakeDraggable(headerGrid, win);
            Grid.SetRow(headerGrid, 0);
            root.Children.Add(headerGrid);

            Brush TxtPri() => (Brush)(TryFindResource("TextPrimaryBrush")   ?? System.Windows.Media.Brushes.White);
            Brush TxtSec() => (Brush)(TryFindResource("TextSecondaryBrush") ?? System.Windows.Media.Brushes.Gray);

            var body = new StackPanel();

            void AddHeader(string text) => body.Children.Add(new TextBlock
            {
                Text = text, FontSize = Ui.FontL, FontWeight = FontWeights.Bold,
                Foreground = TxtPri(), Margin = new Thickness(0, 14, 0, 6),
            });
            void AddRow(string label, string value)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                var lbl = new TextBlock { Text = label, FontSize = Ui.FontM, Foreground = TxtSec(), VerticalAlignment = VerticalAlignment.Top };
                var val = new System.Windows.Controls.TextBox
                {
                    Text = value, FontSize = Ui.FontM, IsReadOnly = true,
                    BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent,
                    Foreground = TxtPri(), TextWrapping = TextWrapping.Wrap, Padding = new Thickness(0),
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                };
                Grid.SetColumn(lbl, 0); Grid.SetColumn(val, 1);
                row.Children.Add(lbl); row.Children.Add(val);
                body.Children.Add(row);
            }

            // ── The STOP code, large, with the friendly name + the explanation ──
            body.Children.Add(new TextBlock
            {
                Text = entry.StopCode, FontSize = Ui.FontXL, FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)(TryFindResource("AccentBrush") ?? System.Windows.Media.Brushes.OrangeRed),
            });
            string bugCheckExplanation = GetBugCheckExplanation(kd);
            if (kd != null && !string.IsNullOrEmpty(bugCheckExplanation))
                body.Children.Add(new TextBlock
                {
                    Text = bugCheckExplanation, FontSize = Ui.FontM, TextWrapping = TextWrapping.Wrap,
                    Foreground = TxtSec(), Margin = new Thickness(0, 4, 0, 0),
                });

            // ── What it means + what to do (BsodAdvisor) ──
            {
                var adv = BsodAdvisor.Get(kd?.BugCheckName ?? entry.StopCode, entry.FaultingModuleKind, kd?.CulpritName, kd?.CulpritIsGeneric ?? false);
                var advCard = new Border
                {
                    CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(0, 12, 0, 0),
                    BorderThickness = new Thickness(1),
                };
                advCard.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2");
                advCard.SetResourceReference(Border.BackgroundProperty, "BgHoverBrush");
                var sp = new StackPanel();
                var area = new TextBlock { Text = PT("Likely area: {0}", PT(adv.Area)), FontSize = Ui.FontL, FontWeight = FontWeights.SemiBold, Foreground = TxtPri() };
                sp.Children.Add(area);
                sp.Children.Add(new TextBlock { Text = adv.Summary, FontSize = Ui.FontM, Foreground = TxtSec(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) });
                sp.Children.Add(new TextBlock { Text = PT("What to do"), FontSize = Ui.FontS, FontWeight = FontWeights.Bold, Foreground = TxtSec(), Margin = new Thickness(0, 0, 0, 4) });
                int i = 1;
                foreach (var step in adv.Steps)
                    sp.Children.Add(new TextBlock { Text = $"{i++}.  {step}", FontSize = Ui.FontM, Foreground = TxtPri(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) });
                if (!string.IsNullOrEmpty(adv.Page))
                {
                    var go = new Wpf.Ui.Controls.Button { Content = adv.PageLabel, Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary, Margin = new Thickness(0, 10, 0, 0) };
                    string page = adv.Page!;
                    go.Click += async (_, _) => { win.Close(); await NavigateTo(page); };
                    sp.Children.Add(go);
                }
                advCard.Child = sp;
                body.Children.Add(advCard);
            }

            AddHeader(_LS("Crash", "Details"));
            AddRow(_LS("Crash", "File"), entry.FilePath is { Length: > 0 } ? entry.FilePath : entry.FileName);
            AddRow(_LS("Crash", "CrashDate"), entry.CrashTime.ToString("dd-MM-yyyy HH:mm:ss"));
            if (kd != null)
            {
                AddRow(_LS("Crash", "ArchType"), $"{kd.Architecture} · {kd.DumpTypeName}");
                if (!string.IsNullOrEmpty(entry.BugcheckParams)) AddRow(_LS("Crash", "BugcheckParams"), entry.BugcheckParams);
                if (kd.Rip != 0)              AddRow(_LS("Crash", "RipAtCrash"), $"0x{kd.Rip:X16}");
                if (kd.ExceptionAddress != 0) AddRow(_LS("Crash", "ExceptionAddress"), $"0x{kd.ExceptionAddress:X16}");
            }
            else if (!string.IsNullOrEmpty(entry.BugcheckParams))
                AddRow(_LS("Crash", "BugcheckParams"), entry.BugcheckParams);

            // ── The suspected driver (a card coloured by category) ──
            AddHeader(_LS("Crash", "SuspectedDriver"));
            if (!string.IsNullOrEmpty(entry.FaultingModuleCategory))
            {
                var catColor = entry.FaultingModuleKind switch
                {
                    nameof(MiniDumpParser.DriverKind.Video)   => WpfColor.FromRgb(168, 85, 247),
                    nameof(MiniDumpParser.DriverKind.Network) => WpfColor.FromRgb(59, 130, 246),
                    nameof(MiniDumpParser.DriverKind.Storage) => WpfColor.FromRgb(234, 179, 8),
                    _                                         => WpfColor.FromRgb(148, 163, 184),
                };
                body.Children.Add(new Border
                {
                    Background = new SolidColorBrush(WpfColor.FromArgb(40, catColor.R, catColor.G, catColor.B)),
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 4, 0, 0),
                    Child = new TextBlock
                    {
                        Text = entry.FaultingModule, FontWeight = FontWeights.SemiBold, FontSize = Ui.FontL,
                        Foreground = new SolidColorBrush(catColor), TextWrapping = TextWrapping.Wrap,
                    },
                });
                if (kd?.CulpritSource != null)
                    body.Children.Add(new TextBlock
                    {
                        Text = $"{_LS("Crash", "IdentifiedVia")}: {kd.CulpritSource} — {_LS("Crash", "IdentifiedViaSuffix")}",
                        FontSize = Ui.FontS, Foreground = TxtSec(), TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 4, 0, 0),
                    });
            }
            else
            {
                body.Children.Add(new TextBlock
                {
                    Text = entry.FaultingModule, FontSize = Ui.FontM, TextWrapping = TextWrapping.Wrap,
                    Foreground = TxtPri(), Margin = new Thickness(0, 2, 0, 0),
                });
            }
            body.Children.Add(new TextBlock
            {
                Text = _LS("Crash", "BestEffortNote"),
                FontSize = Ui.FontS, FontStyle = FontStyles.Italic, TextWrapping = TextWrapping.Wrap,
                Foreground = TxtSec(), Opacity = 0.7, Margin = new Thickness(0, 6, 0, 0),
            });

            // ── The list of drivers from the dump ──
            if (kd != null && kd.Drivers.Count > 0)
            {
                AddHeader($"{_LS("Crash", "DriversInDump")} ({kd.Drivers.Count})" + (kd.DriverRangesValid ? "" : $" — {_LS("Crash", "NamesOnlyNoAddresses")}"));
                var listPanel = new StackPanel();
                foreach (var d in kd.Drivers.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    string line = kd.DriverRangesValid && d.Base != 0
                        ? $"{d.Name,-28} 0x{d.Base:X16} +0x{d.Size:X}   {d.Category}"
                        : $"{d.Name,-28} {d.Category}";
                    listPanel.Children.Add(new TextBlock
                    {
                        Text = line, FontSize = Ui.FontS,
                        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                        Foreground = d.Name.Equals(kd.CulpritName, StringComparison.OrdinalIgnoreCase)
                            ? (Brush)(TryFindResource("AccentBrush") ?? System.Windows.Media.Brushes.OrangeRed)
                            : TxtPri(),
                        Margin = new Thickness(0, 1, 0, 1),
                    });
                }
                body.Children.Add(new Border
                {
                    Background = (Brush)(TryFindResource("BgInputBrush") ?? System.Windows.Media.Brushes.Black),
                    CornerRadius = new CornerRadius(4), Padding = new Thickness(8),
                    MaxHeight = 240, Margin = new Thickness(0, 4, 0, 0),
                    Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = listPanel },
                });
            }
            else if (!string.IsNullOrEmpty(entry.FilePath) && File.Exists(entry.FilePath) && kd == null)
            {
                // Non-kernel dump (e.g. user-mode MDMP) — the module reader is used.
                AddHeader(_LS("Crash", "ModulesInDump"));
                var listPanel = new StackPanel();
                try
                {
                    foreach (var m in MiniDumpParser.GetModules(entry.FilePath)
                                                    .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                        listPanel.Children.Add(new TextBlock
                        {
                            Text = $"{m.Name}   —   {m.Category}", FontSize = Ui.FontS,
                            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                            Foreground = TxtPri(), Margin = new Thickness(0, 1, 0, 1),
                        });
                }
                catch (Exception ex)
                {
                    string reason = ex is UnauthorizedAccessException
                        ? _LS("Crash", "AccessDeniedRunAsAdmin")
                        : $"{_LS("Crash", "ReadError")} ({ex.GetType().Name}): {ex.Message}";
                    listPanel.Children.Add(new TextBlock
                    {
                        Text = reason, FontSize = Ui.FontS, TextWrapping = TextWrapping.Wrap,
                        Foreground = (Brush)(TryFindResource("StatusErrorBrush") ?? System.Windows.Media.Brushes.OrangeRed),
                    });
                }
                body.Children.Add(new Border
                {
                    Background = (Brush)(TryFindResource("BgInputBrush") ?? System.Windows.Media.Brushes.Black),
                    CornerRadius = new CornerRadius(4), Padding = new Thickness(8),
                    MaxHeight = 240, Margin = new Thickness(0, 4, 0, 0),
                    Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = listPanel },
                });
            }

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = body,
            };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            // ── Bottom buttons: Copy report + WinDbg (open / install) ──
            var footer = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            var btnCopy = new Button
            {
                Content = _LS("Crash", "CopyReport"), MinWidth = 120, Margin = new Thickness(0, 0, 8, 0),
                Style = (Style)(TryFindResource("OutlineButtonStyle") ?? new Style()),
            };
            btnCopy.Click += (_, _) =>
            {
                try
                {
                    System.Windows.Clipboard.SetText(BuildCrashReport(entry));
                    Fx.Copied(btnCopy);
                    ShowToastSuccess(_LS("Crash", "ReportCopiedToast"));
                }
                catch { ShowToastWarning(_LS("Crash", "ClipboardErrorToast")); }
            };
            footer.Children.Add(btnCopy);

            if (!string.IsNullOrEmpty(entry.FilePath))
            {
                var btnDbg = new Button
                {
                    Content = _crashService.IsWinDbgAvailable() ? _LS("Crash", "OpenInWinDbg") : _LS("Crash", "InstallWinDbg"),
                    MinWidth = 160,
                    Style = (Style)(TryFindResource("GreenButtonStyle") ?? new Style()),
                };
                btnDbg.Click += async (_, _) => await HandleWinDbgAsync(entry.FilePath, btnDbg);
                footer.Children.Add(btnDbg);
            }
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            win.Content = SMDWin.Views.Net.NetWindowHelper.WindowShell(root);
            win.Show();
        }

        private static string BuildCrashReport(CrashEntry entry)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Raport BSOD (SMDWin) ===");
            sb.AppendLine($"Fișier:    {(string.IsNullOrEmpty(entry.FilePath) ? entry.FileName : entry.FilePath)}");
            sb.AppendLine($"Data:      {entry.CrashTime:dd-MM-yyyy HH:mm:ss}");
            sb.AppendLine($"Stop code: {entry.StopCode}");
            if (!string.IsNullOrEmpty(entry.BugcheckParams))
                sb.AppendLine($"Parametri: {entry.BugcheckParams}");
            var kd = entry.KernelInfo;
            if (kd != null)
            {
                sb.AppendLine($"Sistem:    {kd.Architecture} · {kd.DumpTypeName}");
                if (kd.Rip != 0)              sb.AppendLine($"RIP:       0x{kd.Rip:X16}");
                if (kd.ExceptionAddress != 0) sb.AppendLine($"Excepție:  0x{kd.ExceptionAddress:X16}");
                string kdExplanation = GetBugCheckExplanation(kd);
                if (!string.IsNullOrEmpty(kdExplanation)) sb.AppendLine($"Semnificație: {kdExplanation}");
            }
            sb.AppendLine($"Driver suspectat: {entry.FaultingModule}");
            if (kd?.CulpritSource != null) sb.AppendLine($"Sursă identificare: {kd.CulpritSource}");
            if (kd != null && kd.Drivers.Count > 0)
            {
                sb.AppendLine($"Drivere în dump ({kd.Drivers.Count}):");
                foreach (var d in kd.Drivers.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    sb.AppendLine(kd.DriverRangesValid && d.Base != 0
                        ? $"  {d.Name,-28} 0x{d.Base:X16} +0x{d.Size:X}"
                        : $"  {d.Name}");
            }
            return sb.ToString();
        }

        private async Task HandleWinDbgAsync(string filePath, Button? sourceBtn = null)
        {
            if (_crashService.IsWinDbgAvailable())
            {
                _crashService.OpenWithWinDbg(filePath);
                return;
            }
            if (!AppDialog.Confirm(LanguageService.S("Msg", "WinDbgConfirm"), LanguageService.S("Msg", "WinDbgTitle"))) return;
            ToolActivityLog.Add("Crash", "Install WinDbg (winget)", "info");
            ShowToastInfo(_LS("Crash", "InstallingToast"));
            if (sourceBtn != null) { sourceBtn.IsEnabled = false; sourceBtn.Content = _LS("Crash", "InstallingWinDbg"); }
            if (BtnWinDbg != null) BtnWinDbg.IsEnabled = false;

            bool ok = await _crashService.InstallWinDbgAsync();

            if (sourceBtn != null) sourceBtn.IsEnabled = true;
            if (BtnWinDbg != null) BtnWinDbg.IsEnabled = true;
            RefreshWinDbgButton();

            if (ok)
            {
                ShowToastSuccess(_LS("Crash", "InstalledOpeningToast"));
                if (sourceBtn != null) sourceBtn.Content = _LS("Crash", "OpenInWinDbg");
                _crashService.OpenWithWinDbg(filePath);
            }
            else
            {
                if (sourceBtn != null) sourceBtn.Content = _LS("Crash", "InstallWinDbg");
                string detail = _crashService.LastInstallError;
                string msg = !string.IsNullOrWhiteSpace(detail)
                    ? $"{_LS("Crash", "InstallFailedMessage")}\n\n{detail}"
                    : _LS("Crash", "InstallFailedMessage");
                SMDWin.Views.AppDialog.Show(msg, "WinDbg", SMDWin.Views.AppDialog.Kind.Info);
            }
        }

        private void RefreshWinDbgButton()
        {
            if (TxtBtnWinDbg != null)
                TxtBtnWinDbg.Text = _crashService.IsWinDbgAvailable()
                    ? PT("Analyze with WinDbg")
                    : PT("Install WinDbg (winget)");
        }

        private async void OpenWinDbg_Click(object s, RoutedEventArgs e)
        {
            if (CrashGrid.SelectedItem is CrashEntry entry && !string.IsNullOrEmpty(entry.FilePath))
                await HandleWinDbgAsync(entry.FilePath);
            else
                ShowToastWarning(_LS("Crash", "SelectFile"));
        }
    }
}
