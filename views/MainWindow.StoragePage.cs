using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SMDWin.Models;
using SMDWin.Services;
using Border      = System.Windows.Controls.Border;
using TextBlock   = System.Windows.Controls.TextBlock;
using FontFamily  = System.Windows.Media.FontFamily;
using HAlign      = System.Windows.HorizontalAlignment;
using VAlign      = System.Windows.VerticalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Rectangle   = System.Windows.Shapes.Rectangle;
using Ellipse     = System.Windows.Shapes.Ellipse;
using Path        = System.Windows.Shapes.Path;
using WColor      = System.Windows.Media.Color;
using UGrid       = System.Windows.Controls.Primitives.UniformGrid;
using Brush       = System.Windows.Media.Brush;
using Brushes     = System.Windows.Media.Brushes;

namespace SMDWin.Views
{
    /// <summary>
    /// The Storage page:
    ///  • the shared "Disk activity" card under the tabs (on all tabs except Large files) + "All drives" on Overview;
    ///  • on Overview one row per disk: an icon by type (SATA SSD / NVMe / HDD / USB / external), health, the partitions as
    ///    block bars and the footer with temperature, hours, power cycles, interface, serial;
    ///  • on SMART: the verdict in words + the important attributes; the NVMe readings go into the full table.
    /// </summary>
    public partial class MainWindow
    {
        // ══ The shared "Disk activity" card ════════════════════════════════════════════
        private static readonly int[] StActWindows = { 60, 300, 900, 3600 };
        // read + write in KB/s: one point per second, 60 minutes, with gaps where there were no reads
        private readonly SecondHistory _stActHist = new(2);
        private bool _stActChartReady;

        private static int StActWindow()
        {
            int w = SettingsService.Current.StorageActWindowSec;
            return Array.IndexOf(StActWindows, w) >= 0 ? w : 60;
        }

        private void SetStActWindow(int sec)
        {
            SettingsService.Current.StorageActWindowSec = sec;
            try { SettingsService.Save(); } catch (Exception ex) { AppLogger.Warning(ex, "StorageActWindow save"); }
            HighlightStActWin();
            DrawStorageActChart();
            UpdatePerDiskCards();
        }

        internal void StorageActWin_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is Border b && int.TryParse(b.Tag?.ToString(), out int sec)) SetStActWindow(sec);
        }

        private void HighlightStActWin()
        {
            int window = StActWindow();
            foreach (int n in StActWindows)
                if (FindName($"StActWin{n}") is Border b)
                {
                    bool on = n == window;
                    b.SetResourceReference(Border.BackgroundProperty, on ? "SelBtnBg" : "BgCardBrush");
                    if (b.Child is TextBlock t)
                    {
                        t.SetResourceReference(TextBlock.ForegroundProperty, on ? "SelBtnFg" : "TextSecondaryBrush");
                        t.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                    }
                }
        }

        /// <summary>The common chart rule: a rounded upper limit + its label on the chart (shows how much a peak means) and the
        /// time window on right click — in addition to the 60s / 5m / 15m / 1h buttons in the header.</summary>
        private void EnsureStActChartSetup(MiniLineChart c)
        {
            if (_stActChartReady) return;
            _stActChartReady = true;
            c.NiceMax = peak => ChartScale.NiceRateMaxKBs(peak, 1024);   // at least 1 MB/s: background noise does not fill the chart
            c.ScaleText = ChartScale.FormatRateKBs;
            ChartRangeMenu.Attach(c, StActWindows, StActWindow, SetStActWindow);
        }

        private void DrawStorageActChart()
        {
            if (FindName("StorageActChart") is not MiniLineChart c) return;
            EnsureStActChartSetup(c);
            c.ShowHistory(_stActHist, StActWindow(), 0, 1);
            // this chart stayed on "Collecting data…" while the disk card below had 5 minutes of data.
            // Not found by reading the code — logged once with what the chart had, to find it from the next log.
            if (!c.HasData && _stActHist.Count > 60 && !_stActEmptyLogged)
            {
                _stActEmptyLogged = true;
                AppLogger.Warning($"Storage › All drives chart empty with {_stActHist.Count} s of history · window {StActWindow()} s · " +
                                  $"last read {_stActHist.Last(0)} · last write {_stActHist.Last(1)} · size {c.ActualWidth:0}×{c.ActualHeight:0} · visible {c.IsVisible}");
            }
        }
        private bool _stActEmptyLogged;

        /// <summary>Called on every sensor tick (OnTempTick). Values in KB/s; <paramref name="steps"/> = 0 when the charts are
        /// paused (background recording off, window hidden): the time stays empty.</summary>
        private void UpdateStorageActivityCard(float readKBs, float writeKBs, float latencyMs, int steps = 1)
        {
            try
            {
                _stActHist.Push(DateTime.UtcNow, new[] { readKBs, writeKBs }, gap: steps <= 0, maxInterpolated: ChartInterpolateMaxSlots);
                if (_uiVisibleCached && _currentPanel == "Disk") DrawStorageActChart();
                if (FindName("TxtStActRead") is TextBlock r) r.Text = FormatKBps(readKBs);
                if (FindName("TxtStActWrite") is TextBlock w) w.Text = FormatKBps(writeKBs);
                if (FindName("TxtStActLat") is TextBlock l) l.Text = float.IsFinite(latencyMs) && latencyMs > 0 ? $"{latencyMs:F1} ms" : PT("idle");   // no transfers = no response time (was "—", read as missing)
                if (FindName("TxtStActScanNow") is TextBlock sn && FindName("TxtSurfaceSpeed") is TextBlock src)
                    sn.Text = string.IsNullOrWhiteSpace(src.Text) || src.Text == "—" ? "—" : src.Text.Contains("MB") ? src.Text : src.Text + " MB/s";
            }
            catch (Exception ex) { AppLogger.Debug($"UpdateStorageActivityCard: {ex.Message}"); }
        }

        /// <summary>What shows under the tabs, depending on the tab (called from StorageTab_Click).</summary>
        private void UpdateStorageTopRow(string tag)
        {
            if (FindName("StorageTopRow") is FrameworkElement row) row.Visibility = Visibility.Collapsed;   // the shared card is no longer shown on any tab
            // the "All drives" card no longer appears (the free space is on each disk's row) — kept only for code
            if (FindName("StorageTotalsCard") is FrameworkElement tot) tot.Visibility = Visibility.Collapsed;
            if (FindName("StActScanStats") is FrameworkElement ss) ss.Visibility = tag == "Surface" ? Visibility.Visible : Visibility.Collapsed;
            if (FindName("TxtStActTitle") is TextBlock t)
                t.Text = tag == "Surface" ? PT("All drives · the scan reads the selected drive")
                       : tag == "Bench" ? PT("All drives · the benchmark uses the selected drive") : PT("All drives");
            HighlightStActWin();
            DrawStorageActChart();
            Dispatcher.BeginInvoke(new Action(UpdatePerDiskCards), System.Windows.Threading.DispatcherPriority.Loaded);   // after the tab is shown
        }

        // ══ Overview: the "Free up space" card ════════════════════════════════════════
        internal void StorageFindLarge_Click(object sender, RoutedEventArgs e)
        {
            if (FindName("BtnStorageTabFiles") is System.Windows.Controls.Button b) StorageTab_Click(b, e);
        }

        internal void StorageCleanmgr_Click(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cleanmgr.exe") { UseShellExecute = true }); }
            catch (Exception ex) { AppLogger.Warning(ex, "StorageCleanmgr_Click"); }
        }

        internal void StorageSettings_Click(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true }); }
            catch (Exception ex) { AppLogger.Warning(ex, "StorageSettings_Click"); }
        }

        // ══ Overview: summary + one row per disk ══════════════════════════════════════
        private void BuildStorageDriveRows(List<DiskHealthEntry> disks)
        {
            try
            {
                if (FindName("StorageDriveRows") is not System.Windows.Controls.Panel host) return;
                host.Children.Clear();
                _perDiskUi.Clear();
                _driveFits.Clear();
                foreach (var d in disks) host.Children.Add(MakeDriveRow(d));
                // from now on the activity of each disk is read (MainWindow.StorageDiskActivity.cs)
                _perDiskOn = true;
                HookDriveRowsResize();
                ApplyDriveRowsColumns();
                HookDriveFit();
                QueueDriveFit();
                UpdatePerDiskCards();
                UpdateStorageTotals(disks);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "BuildStorageDriveRows"); }
        }

        private void UpdateStorageTotals(List<DiskHealthEntry> disks)
        {
            double totalGB = disks.Sum(d => d.SizeBytes / 1e9);
            double freeGB = disks.SelectMany(d => d.Partitions).Sum(p => p.FreeGB);
            double partsGB = disks.SelectMany(d => d.Partitions).Sum(p => p.TotalGB);
            static string G(double gb) => DisplayFormat.SizeGb(gb);
            if (FindName("TxtStTotalsName") is TextBlock n) n.Text = $"{disks.Count} drive{(disks.Count == 1 ? "" : "s")} · {G(totalGB)}";
            if (FindName("TxtStTotalsFree") is TextBlock f) f.Text = G(freeGB);
            if (FindName("TxtStTotalsOf") is TextBlock o) o.Text = $"free of {G(partsGB)}";
            if (FindName("StTotalsBar") is UGrid bar)
                FillBlocks(bar, partsGB > 0 ? (partsGB - freeGB) / partsGB * 100 : 0, 32, null);
            if (FindName("TxtStFreeHint") is TextBlock hint)
            {
                var full = disks.SelectMany(d => d.Partitions).Where(p => p.TotalGB > 1 && p.UsedPct >= 85)
                                .OrderByDescending(p => p.UsedPct).ToList();
                string G1(double gb) => gb >= 1000 ? $"{gb / 1000:F1} TB" : $"{gb:F0} GB";
                string sysLetter = (System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');
                var sys = disks.SelectMany(d => d.Partitions)
                               .FirstOrDefault(p => (p.Letter ?? "").TrimEnd('\\').Equals(sysLetter, StringComparison.OrdinalIgnoreCase));
                hint.Text = full.Count == 0
                    ? (sys != null ? $"{sysLetter} (Windows) has {G1(sys.FreeGB)} free. Nothing to do right now."
                                   : "No drive is close to full. Nothing to do right now.")
                    // a short sentence; the details (the percentages) stay in the tooltip
                    : full.Count == 1 ? PT("{0} is almost full", (full[0].Letter ?? "").TrimEnd('\\')) : PT("{0} drives are almost full", full.Count);
                hint.ToolTip = full.Count == 0 ? null
                    : string.Join("\n", full.Take(5).Select(p => $"{(p.Letter ?? "").TrimEnd('\\')} {p.UsedPct:F0}% full · {DisplayFormat.SizeGb(p.FreeGB)} free"))
                      + "\n" + PT("Windows slows down when the system drive is almost full.");
                hint.SetResourceReference(TextBlock.ForegroundProperty,
                    full.Count == 0 ? "TextSecondaryBrush" : full[0].UsedPct >= 95 ? "StatusCritBrush" : "StatusWarnBrush");
            }
            if (FindName("StTotalsKinds") is UGrid kinds)
            {
                kinds.Children.Clear();
                int ssd = disks.Count(d => d.Kind is "NVMe SSD" or "SATA SSD" or "SSD");
                int hdd = disks.Count(d => d.Kind == "HDD");
                int ext = disks.Count(d => d.Kind is "USB stick" or "External HDD" or "External SSD" or "External drive" or "SD card");
                foreach (var (k, v) in new[] { ("SSD", ssd), ("HDD", hdd), ("External", ext) })
                {
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock { Text = k, Style = TryFindResource("CompFootKey") as Style });
                    sp.Children.Add(new TextBlock { Text = v.ToString(), Style = TryFindResource("CompInfoVal") as Style });
                    kinds.Children.Add(sp);
                }
            }
        }

        /// <summary>The exact type ("M.2 SATA SSD", "2.5\" HDD · 5400 rpm") and, for USB disks, the link ("USB 3.0"), with a
        /// warning when a USB 3 device runs at USB 2 speed.</summary>
        private static string KindLabel(DiskHealthEntry d)
        {
            string usb = d.UsbConnected.Length == 0 ? ""
                       : d.UsbSlowLink ? $" · ⚠ {d.UsbConnected.Split(" · ")[0]} (device supports {d.UsbSupported.Split(" · ")[0]})"
                       : " · " + d.UsbConnected.Split(" · ")[0];
            return d.TypeText + usb;
        }

        /// <summary>The drive's findings as small chips (worst first); the explanation is in the tooltip.</summary>
        private async Task FillDiskFindingsAsync(DiskHealthEntry d, WrapPanel host)
        {
            try
            {
                var list = await Task.Run(() => StorageChecks.Run(d));
                RenderFindingChips(host, list);
            }
            catch (Exception ex) { AppLogger.Debug($"FillDiskFindingsAsync: {ex.Message}"); }
        }

        /// <summary>Findings as small chips, worst first, at most 4 (+N with the rest in its tooltip). Storage and RAM.</summary>
        internal void RenderFindingChips(WrapPanel host, List<DiskFinding> list)
        {
            try
            {
                host.Children.Clear();
                var sorted = list.OrderByDescending(f => f.Severity).ToList();
                foreach (var f in sorted.Take(4))
                {
                    string key = f.Severity switch { DiskFinding.Level.Crit => "StatusCritBrush", DiskFinding.Level.Warn => "StatusWarnBrush", _ => "TextSecondaryBrush" };
                    // the Windows way: a small mark (! / i in a filled circle) and the coloured words, no tinted box
                    var chip = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 4, 14, 0),
                                                Background = System.Windows.Media.Brushes.Transparent, ToolTip = f.Detail, Cursor = System.Windows.Input.Cursors.Help };
                    ToolTipService.SetInitialShowDelay(chip, 150);
                    var mark = new StatusMark { Width = 13, Height = 13, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
                    mark.SetResourceReference(StatusMark.FillProperty, f.Severity == DiskFinding.Level.Info ? "AccentBrush" : key);
                    var t = new TextBlock { Text = f.Short, FontSize = Ui.FontXS, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                    t.SetResourceReference(TextBlock.ForegroundProperty, f.Severity == DiskFinding.Level.Info ? "TextPrimaryBrush" : key);
                    chip.Children.Add(mark); chip.Children.Add(t);
                    host.Children.Add(chip);
                }
                if (sorted.Count > 4)
                {
                    var more = new TextBlock { Text = $"+{sorted.Count - 4}", FontSize = Ui.FontXS, Margin = new Thickness(0, 6, 0, 0),
                                               ToolTip = string.Join("\n\n", sorted.Skip(4).Select(f => f.Short + " — " + f.Detail)) };
                    more.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    host.Children.Add(more);
                }
            }
            catch (Exception ex) { AppLogger.Debug($"RenderFindingChips: {ex.Message}"); }
        }

        private Border MakeDriveRow(DiskHealthEntry d)
        {
            var card = new Border { Style = TryFindResource("CompCardStyle") as Style, Cursor = System.Windows.Input.Cursors.Arrow,
                                    Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(18, 14, 18, 0) };
            // every card is as tall as its content and the activity chart of every disk has the same fixed height
            // (BuildDiskActivityBlock), so two disks look alike whether or not they have a health strip
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            card.Child = root;

            var body = new Grid { Margin = new Thickness(0, 0, 0, 14), VerticalAlignment = VAlign.Top };
            // picture · name · (spacer) · partitions · interface / serial / capacity (the old footer moved up here)
            foreach (var w in new[] { new GridLength(188), new GridLength(1.4, GridUnitType.Star), new GridLength(0), new GridLength(2, GridUnitType.Star), GridLength.Auto })
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(body);

            // 1. the icon
            // without SMART data (USB sticks, some adapters) the health is unknown — do not show an invented 100%
            bool noHealth = d.SmartAttributes.Count == 0 && d.NvmeHealthRows.Count == 0;
            int hp = noHealth ? 0 : d.HealthPercent;
            var pic = new Viewbox { Width = 176, Height = 122, Child = HardwareIllustrations.Drive(DriveForm.PictureKind(d.Kind, d.Model, formFactor: d.FormFactor), DriveHealth.From(hp, !noHealth)), VerticalAlignment = VAlign.Center, HorizontalAlignment = HAlign.Center };
            body.Children.Add(pic);

            // 2. type, name, interface, state
            bool isSys = d.Partitions.Any(p => p.Letter.StartsWith("C", StringComparison.OrdinalIgnoreCase));
            var info = new StackPanel { VerticalAlignment = VAlign.Center, Margin = new Thickness(16, 0, 16, 0) };
            var cap = new TextBlock { Text = KindLabel(d) + (isSys ? " · SYSTEM" : ""), Style = TryFindResource("DashRowCaption") as Style };
            info.Children.Add(cap);
            // the model was cut ("WDC PC SN520 SDAPNUW-…") with room left in the row — a little more width and a
            // second line when needed, the full name in the tooltip
            string modelText = string.IsNullOrWhiteSpace(d.Model) ? $"Disk {d.DiskIndex}" : d.Model.Trim();
            var nameT = new TextBlock { Text = modelText, Style = TryFindResource("DashRowName") as Style, FontSize = 20, Margin = new Thickness(0, 2, 0, 2),
                                        TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 56, ToolTip = modelText };
            info.Children.Add(nameT);
            string style = d.Partitions.Count > 0 ? "" : " · " + PT("no volumes");
            info.Children.Add(Txt2($"{d.Size}{style}", "TextSecondaryBrush", Ui.FontM));
            var (stTxt, stKey) = hp >= 90 ? (DS("Healthy", "Healthy"), "StatusOkBrush") : hp >= 60 ? (DS("Watch", "Needs attention"), "StatusWarnBrush") : hp > 0 ? (DS("Failing", "Failing"), "StatusCritBrush") : (PT("No health data"), "TextSecondaryBrush");
            var stRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            // the Windows-style mark (tick / ! / i in a filled circle); the word stays in the text colour unless it is a warning
            var dot = new StatusMark { Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VAlign.Center };
            dot.SetResourceReference(StatusMark.FillProperty, stKey);
            // the state in words; the figure is in the dial of the health strip below (life left or health)
            var stT = new TextBlock { Text = stTxt, ToolTip = string.IsNullOrWhiteSpace(d.Status) ? null : $"Windows status: {d.Status}", Style = TryFindResource("CompStatusText") as Style };
            stT.SetResourceReference(TextBlock.ForegroundProperty, stKey == "StatusOkBrush" ? "TextPrimaryBrush" : stKey);
            stRow.Children.Add(dot); stRow.Children.Add(stT);
            info.Children.Add(stRow);
            // what else is worth knowing: the link, Windows disk errors, alignment, TRIM, SMR, firmware (StorageChecks)
            var finds = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            info.Children.Add(finds);
            _ = FillDiskFindingsAsync(d, finds);
            Grid.SetColumn(info, 1); body.Children.Add(info);

            // the state is written in words here; its figure is in the dial of the health strip

            // 4. partitiile
            var parts = new StackPanel { VerticalAlignment = VAlign.Center, Margin = new Thickness(18, 0, 0, 0) };
            foreach (var p in d.Partitions.Take(4)) parts.Children.Add(MakePartitionRow(p));
            if (d.Partitions.Count == 0) parts.Children.Add(Txt2(PT("No volumes Windows can read (unformatted or other file system)."), "TextSecondaryBrush", Ui.FontS));
            if (d.Partitions.Count > 4) parts.Children.Add(Txt2($"+{d.Partitions.Count - 4} more", "TextSecondaryBrush", Ui.FontS));
            Grid.SetColumn(parts, 3); body.Children.Add(parts);

            // the activity of this disk: read / write chart + response time, busy, peak (MainWindow.StorageDiskActivity.cs)
            var activity = BuildDiskActivityBlock(d, out var actUi);
            Grid.SetRow(activity, 1); root.Children.Add(activity);
            actUi.ShowChart = on =>
            {
                activity.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                root.RowDefinitions[0].Height = GridLength.Auto;
                root.RowDefinitions[1].Height = GridLength.Auto;
                body.VerticalAlignment = on ? VAlign.Top : VAlign.Center;
                body.Margin = new Thickness(0, 0, 0, on ? 14 : 22);
                QueueDriveFit();
            };
            _perDiskUi[d.DiskIndex] = actUi;

            // tight layout, when two disks have to fit the visible height (FitDriveCards): a smaller picture and name, less spacing
            bool tightOn = false;
            _driveFits.Add(new DriveCardFit
            {
                Card = card, Chart = actUi.Chart,
                ChartShown = () => activity.Visibility == Visibility.Visible,
                SetTight = tight =>
                {
                    if (tight == tightOn) return;
                    tightOn = tight;
                    body.ColumnDefinitions[0].Width = new GridLength(tight ? 124 : 188);
                    pic.Width = tight ? 110 : 176; pic.Height = tight ? 72 : 122;
                    nameT.FontSize = tight ? 17 : 20; nameT.MaxHeight = tight ? 46 : 56;
                    stRow.Margin = new Thickness(0, tight ? 3 : 10, 0, 0);
                    card.Padding = new Thickness(18, tight ? 10 : 14, 18, 0);
                    body.Margin = new Thickness(0, 0, 0, tight ? 6 : 14);
                    actUi.SetTight(tight);
                },
            });

            // (kept for a narrow window) a smaller picture, the partitions under the name instead of next to it
            card.Tag = new Action<bool>(compact =>
            {
                body.ColumnDefinitions[0].Width = new GridLength(compact ? 118 : 188);
                pic.Width = compact ? 110 : 176; pic.Height = compact ? 76 : 122;
                body.ColumnDefinitions[3].Width = compact ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
                Grid.SetRow(parts, compact ? 1 : 0);
                Grid.SetColumn(parts, compact ? 0 : 3);
                Grid.SetColumnSpan(parts, compact ? 2 : 1);
                parts.Margin = compact ? new Thickness(0, 12, 0, 0) : new Thickness(18, 0, 0, 0);
                SetDiskActivityCompact(activity, actUi, compact);
            });

            // the strip with always-available information — health, temperature, SMART in short, usage (MainWindow.StorageHealth.cs)
            var band = MakeDriveHealthBand(d);
            Grid.SetRow(band, 2); root.Children.Add(band);

            // interface · serial · capacity, top right of the card (the cards are one under the other, full width;
            // the separate footer took ~45 px per disk)
            string iface = (string.IsNullOrEmpty(d.Bus) ? d.MediaType : d.Bus) + (d.RotationRpm > 0 ? $" · {d.RotationRpm} RPM" : "");
            var cells = new List<(string, string)>
            {
                (DS("Interface", "Interface"), iface),
                (DS("Serial", "Serial"), (d.SerialNumber ?? "").Trim().TrimEnd('.')),
                (DS("Capacity", "Capacity"), d.Size ?? ""),
            };
            var meta = new Grid { Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VAlign.Center };
            meta.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            meta.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int ci = 0; ci < cells.Count; ci++)
            {
                var (k, v) = cells[ci];
                meta.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var kt = new TextBlock { Text = k, FontSize = Ui.FontS, Margin = new Thickness(0, 1, 12, 1) };
                kt.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                Grid.SetRow(kt, ci); meta.Children.Add(kt);
                var vt = new TextBlock { Text = string.IsNullOrWhiteSpace(v) || v == "—" ? "—" : v, FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 1, 0, 1),
                                         MaxWidth = 280, TextTrimming = TextTrimming.CharacterEllipsis };
                if (vt.Text != "—") vt.ToolTip = vt.Text;
                vt.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                Grid.SetRow(vt, ci); Grid.SetColumn(vt, 1); meta.Children.Add(vt);
            }
            Grid.SetColumn(meta, 4);
            body.Children.Add(meta);
            return card;
        }

        private FrameworkElement MakePartitionRow(PartitionEntry p)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 10) };
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
            static string G(double gb) => DisplayFormat.SizeGb(gb);
            string key = p.UsedPct >= 95 ? "StatusCritBrush" : p.UsedPct >= 90 ? "StatusWarnBrush" : "";
            var free = new TextBlock { Text = PT("{0} free", G(p.FreeGB)), FontSize = Ui.FontL, FontWeight = FontWeights.SemiBold, VerticalAlignment = VAlign.Bottom };
            free.SetResourceReference(TextBlock.ForegroundProperty, key == "" ? "TextPrimaryBrush" : key);
            DockPanel.SetDock(free, Dock.Right);
            line.Children.Add(free);
            var name = new TextBlock { FontSize = Ui.FontL, VerticalAlignment = VAlign.Bottom, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            name.Inlines.Add(new System.Windows.Documents.Run((p.Letter ?? "").TrimEnd('\\') + "  ") { FontWeight = FontWeights.Bold, FontSize = Ui.FontXL });
            name.Inlines.Add(new System.Windows.Documents.Run(string.IsNullOrWhiteSpace(p.Label) || p.Label == "Local Disk" ? PT("Local Disk") : p.Label));
            var sub = new System.Windows.Documents.Run($"   {p.FileSystem} · {G(p.TotalGB)}");
            sub.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextSecondaryBrush");
            name.Inlines.Add(sub);
            line.Children.Add(name);
            sp.Children.Add(line);
            var bar = new UGrid { Rows = 1, Height = 10 };
            FillBlocks(bar, p.UsedPct, 40, key == "" ? null : key);
            sp.Children.Add(bar);
            return sp;
        }

        /// <summary>A thin solid bar with rounded ends (as in Windows Settings › Storage): filled = accent / yellow / red,
        /// the rest = BorderBrush2. (It used to be a row of blocks; <paramref name="n"/> is kept for the callers.)</summary>
        private static void FillBlocks(UGrid bar, double pct, int n, string? key)
        {
            bar.Children.Clear(); bar.Columns = 1;
            double f = Math.Clamp(pct / 100.0, 0, 1);
            double th = Math.Min(6, double.IsNaN(bar.Height) ? 6 : bar.Height), r = th / 2;
            var g = new Grid { Height = th, VerticalAlignment = VAlign.Center, ClipToBounds = true };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(f, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - f, GridUnitType.Star) });
            var track = new Border { CornerRadius = new CornerRadius(r) };
            track.SetResourceReference(Border.BackgroundProperty, "BorderBrush2");
            Grid.SetColumnSpan(track, 2);
            g.Children.Add(track);
            if (f > 0)
            {
                var fill = new Border { CornerRadius = new CornerRadius(r), MinWidth = th };
                fill.SetResourceReference(Border.BackgroundProperty, key ?? "AccentBrush");
                g.Children.Add(fill);
            }
            bar.Children.Add(g);
        }

        /// <summary>A formatted SMART value (ATA by ID, NVMe by name); "" if missing.</summary>
        private static string DiskAttr(DiskHealthEntry d, byte[] ids, string nvmeName, Func<long, string> fmt)
        {
            var a = d.SmartAttributes.FirstOrDefault(x => ids.Contains(x.Id));
            if (a != null) { try { return fmt(a.RawValue); } catch (Exception ex) { AppLogger.Debug($"DiskAttr: {ex.Message}"); } }
            var n = d.NvmeHealthRows.FirstOrDefault(r => (r.Name ?? "").Contains(nvmeName, StringComparison.OrdinalIgnoreCase));
            return n?.RawDisplay ?? "";
        }

        private static TextBlock Txt2(string text, string brushKey, double size)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return t;
        }

        // ══ SMART: the verdict + the important attributes ═══════════════════════════════
        private void UpdateSmartVerdict(DiskHealthEntry disk)
        {
            try
            {
                HardwareService.MarkNvmeDataUnits(disk);
                int hp = disk.HealthPercent;
                if (FindName("SmartVerdictGauge") is PercentGauge g) { g.HasValue = hp > 0; g.Value = hp; }
                if (FindName("TxtSmartVerdictPct") is TextBlock p) p.Text = hp > 0 ? hp.ToString() : "—";
                if (FindName("TxtSmartVerdictDisk") is TextBlock dn) dn.Text = $"{(disk.Model ?? "").Trim()} · {KindLabel(disk)}";

                // warnings: the rows that are not green (ATA or NVMe)
                var warnNames = disk.SmartAttributes.Where(a => a.TrafficLightColor != "#22C55E").Select(a => a.Name)
                    .Concat(disk.NvmeHealthRows.Where(r => r.TrafficLightColor != "#22C55E").Select(r => r.Name)).ToList();
                int count = disk.SmartAttributes.Count + disk.NvmeHealthRows.Count;
                long unsafeOff = disk.SmartAttributes.FirstOrDefault(a => a.Id is 0xC0 or 0xAE)?.RawValue ?? -1;
                string status, key, text;
                if (count == 0) { status = "No data"; key = "TextSecondaryBrush"; text = disk.Bus == "NVMe" || (disk.Kind ?? "").Contains("NVMe", StringComparison.OrdinalIgnoreCase)
                    ? PT("Windows reports this drive as Healthy, but its storage driver does not pass on the detailed counters (temperature, wear, power-on hours). This is common when the laptop uses the Intel RST / VMD driver; the drive maker's own tool (for example SanDisk Dashboard) can read them.")
                    : PT("The drive does not report SMART data here (common for USB sticks and some adapters)."); }
                else if (warnNames.Count == 0 && hp >= 90)
                {
                    status = "Healthy"; key = "StatusOkBrush";
                    text = PT("All {0} values are within limits.", count) + (unsafeOff > 0 ? " " + PT("Worth knowing: {0:N0} unsafe shutdowns (power lost while running).", unsafeOff) : "");
                }
                else
                {
                    status = hp < 60 ? "Failing" : "Needs attention"; key = hp < 60 ? "StatusCritBrush" : "StatusWarnBrush";
                    text = warnNames.Count > 0 ? PT("Values that need attention: {0}. Back up important data.", string.Join(", ", warnNames.Take(3)) + (warnNames.Count > 3 ? "…" : ""))
                                               : PT("The drive reports reduced health. Back up important data.");
                }
                if (FindName("TxtSmartVerdictStatus") is TextBlock st) { st.Text = PT(status); st.SetResourceReference(TextBlock.ForegroundProperty, key); }
                if (FindName("TxtSmartVerdictText") is TextBlock tx) tx.Text = text;
                UpdateSmartLife(disk, disk.NvmeHealthRows);
                if (FindName("TxtSmartAttrCount") is TextBlock ac) ac.Text = count > 0 ? PT("{0} values", count) : PT("Values reported by the drive");

                // the figures that matter (at most 4), as tiles — the explanation of each is in the tooltip
                _smartKeyTiles.Clear();
                // on NVMe the readings have other names and another meaning (e.g. "Available Spare" = the remaining spare, not
                // reallocated sectors) → the tile label comes from the nvName column
                var wanted = new (byte[] ids, string nvme, string name, string nvName, string note)[]
                {
                    (new byte[] { 0xBB, 0xC6 }, "Media and Data Integrity", "Uncorrectable errors", "Media errors", "Data the drive could not read back"),
                    (new byte[] { 0x05 }, "Available Spare", "Reallocated sectors", "Spare left", "Damaged areas replaced by spare ones (NVMe: spare blocks still available)"),
                    (new byte[] { 0xC5 }, "Critical Warning", "Pending sectors", "Critical warning", "Areas waiting to be checked (NVMe: warning flags set by the drive, 0x00 = none)"),
                    (new byte[] { 0xAD, 0xB1, 0xE7, 0xE9, 0xCA }, "Percentage Used", "Wear", "Wear", "How much of the drive's rated life is used"),
                    (new byte[] { 0xC7, 0xA8 }, "Error Information", "Link errors", "Error log entries", "Cable or port problems"),
                    (new byte[] { 0xC0, 0xAE }, "Unsafe Shutdown", "Unsafe shutdowns", "Unsafe shutdowns", "Power lost while the drive was running"),
                };
                int shown = 0;
                foreach (var (ids, nv, ataName, nvName, note) in wanted)
                {
                    if (shown >= 4) break;
                    string raw = ""; string color = "#22C55E"; string name = ataName;
                    var a = disk.SmartAttributes.FirstOrDefault(x => ids.Contains(x.Id));
                    if (a != null) { raw = a.RawDisplay; color = a.TrafficLightColor; }
                    else
                    {
                        var r = disk.NvmeHealthRows.FirstOrDefault(x => (x.Name ?? "").StartsWith(nv, StringComparison.OrdinalIgnoreCase))
                             ?? disk.NvmeHealthRows.FirstOrDefault(x => (x.Name ?? "").Contains(nv, StringComparison.OrdinalIgnoreCase));
                        if (r != null) { raw = r.RawDisplay; color = r.TrafficLightColor; name = nvName; }
                    }
                    if (raw.Length == 0) continue;
                    string tileKey = color == "#22C55E" ? "" : color == "#EF4444" ? "StatusCritBrush" : "StatusWarnBrush";
                    _smartKeyTiles.Add(SmartTile(PT(name), raw, "", PT(note), tileKey));
                    shown++;
                }
                RebuildSmartTiles();
            }
            catch (Exception ex) { AppLogger.Debug($"UpdateSmartVerdict: {ex.Message}"); }
        }

        // The VERDICT card tiles: first the figures that matter, then the disk's life — all in a single 2-column grid
        // (SmartTilesGrid), so no empty places remain between the two groups.
        private readonly List<Border> _smartKeyTiles = new();
        private readonly List<Border> _smartLifeTiles = new();

        /// <summary>Rebuilds the tile grid from the two lists. With an odd number of tiles the last row would be half empty:
        /// the tile marked optional is dropped then (the capacity — already in the card header).</summary>
        private void RebuildSmartTiles()
        {
            if (SmartTilesGrid == null) return;
            SmartTilesGrid.Children.Clear();
            var life = _smartLifeTiles.ToList();
            if ((_smartKeyTiles.Count + life.Count) % 2 == 1)
            {
                int optional = life.FindLastIndex(t => Equals(t.Tag, "optional"));
                if (optional >= 0) life.RemoveAt(optional);
            }
            foreach (var t in _smartKeyTiles) SmartTilesGrid.Children.Add(t);
            foreach (var t in life) SmartTilesGrid.Children.Add(t);
            ScheduleFitSmartVerdict();
        }

        /// <summary>
        /// The disk's life in the VERDICT card: power-on hours, power cycles, temperature, data written / read and capacity,
        /// as tiles — from the ATA attributes or, on NVMe, from the smartctl readings.
        /// </summary>
        private void UpdateSmartLife(DiskHealthEntry disk, IEnumerable<NvmeHealthRow> nvme)
        {
            try
            {
                var nv = nvme.ToList();
                SmartAttributeEntry? A(params byte[] ids) => disk.SmartAttributes.FirstOrDefault(x => ids.Contains(x.Id));
                string? N(string name) => nv.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))?.RawDisplay;

                var tiles = new List<(string key, string val, string sub)>();

                long hours = A(0x09)?.RawValue ?? -1;
                string? nvHours = N("Power-On Hours");
                if (hours >= 0) tiles.Add(("Powered on", $"{hours:N0} h", AgeText(hours)));
                else if (nvHours != null) tiles.Add(("Powered on", nvHours, ""));

                long cycles = A(0x0C)?.RawValue ?? -1;
                string? nvCycles = N("Power Cycles");
                if (cycles >= 0) tiles.Add(("Power cycles", $"{cycles:N0}", "times turned on"));
                else if (nvCycles != null) tiles.Add(("Power cycles", nvCycles, "times turned on"));

                var temp = A(0xC2, 0xBE);
                string? nvTemp = N("Composite Temperature");
                if (temp != null) tiles.Add(("Temperature", temp.RawDisplay, "right now"));
                else if (nvTemp != null) tiles.Add(("Temperature", nvTemp, "right now"));

                var wr = A(0xF1); var rd = A(0xF2);
                string? nvW = N("Data Units Written"), nvR = N("Data Units Read");
                if (wr != null && wr.RawValue > 0) tiles.Add(("Data written", LifetimeData.Format(SsdLifeDetector.AttributeToBytes(wr)), "over its lifetime"));
                else if (nvW != null) tiles.Add(("Data written", nvW, "over its lifetime"));
                if (rd != null && rd.RawValue > 0) tiles.Add(("Data read", LifetimeData.Format(SsdLifeDetector.AttributeToBytes(rd)), "over its lifetime"));
                else if (nvR != null) tiles.Add(("Data read", nvR, "over its lifetime"));

                if (!string.IsNullOrWhiteSpace(disk.Size)) tiles.Add(("Capacity", disk.Size, disk.RotationRpm > 0 ? $"{disk.RotationRpm:N0} rpm" : KindLabel(disk)));

                _smartLifeTiles.Clear();
                foreach (var (key, val, sub) in tiles.Take(6))
                {
                    var tile = SmartTile(key, val, sub, "", "");
                    if (key == "Capacity") tile.Tag = "optional";
                    _smartLifeTiles.Add(tile);
                }
                RebuildSmartTiles();

                if (FindName("TxtSmartLifeSerial") is System.Windows.Controls.TextBox sn) sn.Text = string.IsNullOrWhiteSpace(disk.SerialNumber) ? "—" : disk.SerialNumber.Trim();
                if (FindName("TxtSmartLifeBus") is TextBlock bus) bus.Text = disk.UsbLinkText.Length > 0 ? disk.UsbLinkText : string.IsNullOrWhiteSpace(disk.Bus) ? KindLabel(disk) : disk.Bus;
            }
            catch (Exception ex) { AppLogger.Debug($"UpdateSmartLife: {ex.Message}"); }

            static string AgeText(long h)
            {
                double days = h / 24.0;
                // time with the disk powered on, not the disk's age — the days are stated directly
                if (days < 730) return $"{days:N0} days powered on";
                return $"{days / 365:0.#} years powered on";
            }
        }

        /// <summary>A tile in the VERDICT card: a small label, a large value (coloured only when there is a problem), an optional
        /// small line; the explanation in the tooltip.</summary>
        private Border SmartTile(string key, string val, string sub, string tip, string brushKey)
        {
            var sp = new StackPanel { VerticalAlignment = VAlign.Center };
            sp.Children.Add(new TextBlock { Text = PT(key), Style = TryFindResource("CompFootKey") as Style });
            var v = new TextBlock { Text = val, FontFamily = TryFindResource("NumFont") as FontFamily, FontSize = 18, FontWeight = FontWeights.SemiBold,
                                    TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
            v.SetResourceReference(TextBlock.ForegroundProperty, brushKey.Length > 0 ? brushKey : "TextPrimaryBrush");
            sp.Children.Add(v);
            if (sub.Length > 0)
            {
                var subTb = Txt2(sub, "TextSecondaryBrush", Ui.FontXS);
                subTb.Tag = "sub";   // FitSmartVerdict hides it first when there is no room
                subTb.TextTrimming = TextTrimming.CharacterEllipsis;
                sp.Children.Add(subTb);
            }
            var tile = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(11, 7, 11, 7), Margin = new Thickness(4), Child = sp,
                                    BorderThickness = new Thickness(1), Background = Brushes.Transparent, ToolTip = tip.Length > 0 ? tip : null };
            tile.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            return tile;
        }

        internal void SmartVerdictScroll_SizeChanged(object sender, SizeChangedEventArgs e) => FitSmartVerdict();

        private void ScheduleFitSmartVerdict() =>
            Dispatcher.BeginInvoke(new Action(FitSmartVerdict), System.Windows.Threading.DispatcherPriority.Loaded);

        private bool _fittingSmartVerdict, _fitSmartVerdictAgain;

        /// <summary>
        /// The VERDICT card fits the page height without scrolling and fills it: tiles on 2 columns, stretched equally over the
        /// remaining height. When there is no room it drops, in turn, the small line under the values, shrinks the dial and hides
        /// the life tiles from the end, one row at a time (the values stay in the table on the right).
        /// </summary>
        private void FitSmartVerdict()
        {
            // the fitting itself runs layout passes; if the column size changes during them, the request is not lost: it runs once
            // more, after the current one finishes
            if (_fittingSmartVerdict) { _fitSmartVerdictAgain = true; return; }
            _fittingSmartVerdict = true;
            try
            {
                if (SmartVerdictScroll == null || SmartVerdictCard == null || SmartTilesGrid == null) return;
                double avail = SmartVerdictScroll.ActualHeight, width = SmartVerdictScroll.ActualWidth;
                if (avail <= 0 || width <= 0) return;

                SmartTilesGrid.Columns = 2;   // on 3 columns the tiles look broken — they always stay on 2
                List<Border> tiles = SmartTilesGrid.Children.OfType<Border>().ToList();
                var subs = new List<FrameworkElement>();
                foreach (var t in tiles)
                {
                    t.Visibility = Visibility.Visible;
                    if (t.Child is System.Windows.Controls.StackPanel sp)
                        foreach (var fe in sp.Children.OfType<FrameworkElement>())
                            if (Equals(fe.Tag, "sub")) { fe.Visibility = Visibility.Visible; subs.Add(fe); }
                }
                if (SmartVerdictDial != null) { SmartVerdictDial.Width = 112; SmartVerdictDial.Height = 101; }

                bool Fits()
                {
                    // the changes above (hidden tiles, smaller dial) reach the card only after a layout pass; without it, the measurement
                    // below would return the old height, kept by WPF
                    SmartVerdictCard.UpdateLayout();
                    SmartVerdictCard.Measure(new System.Windows.Size(width, double.PositiveInfinity));
                    return SmartVerdictCard.DesiredSize.Height <= avail - 2;
                }

                if (Fits()) return;

                foreach (var fe in subs) fe.Visibility = Visibility.Collapsed;
                if (Fits()) return;

                if (SmartVerdictDial != null) { SmartVerdictDial.Width = 84; SmartVerdictDial.Height = 76; }
                if (Fits()) return;

                // life tiles from the end (capacity, data read…): hidden until an even number of tiles (whole rows) that fits remains;
                // those with the important figures and the first two life tiles stay
                int keep = Math.Min(tiles.Count, _smartKeyTiles.Count + 2);
                int visible = tiles.Count;
                for (int i = tiles.Count - 1; i >= keep; i--)
                {
                    tiles[i].Visibility = Visibility.Collapsed;
                    visible--;
                    if (visible % 2 == 1 && i > keep) continue;   // half-empty row: its pair is hidden too
                    if (Fits()) return;
                }
            }
            catch (Exception ex) { AppLogger.Debug($"FitSmartVerdict: {ex.Message}"); }
            finally
            {
                _fittingSmartVerdict = false;
                if (_fitSmartVerdictAgain) { _fitSmartVerdictAgain = false; ScheduleFitSmartVerdict(); }
            }
        }

        /// <summary>The NVMe readings (smartctl) go into the "All attributes" table, not into a separate card.</summary>
        private void MergeNvmeRowsIntoSmartGrid(DiskHealthEntry disk, SmartctlReport report)
        {
            try
            {
                if (!ReferenceEquals(_currentSmartDisk, disk) || SmartGrid == null) return;
                if (SmartGrid.ItemsSource is List<NvmeHealthRow>) return;   // these are already the NVMe rows
                var extra = NvmeHealthRow.FromReport(report);
                if (extra.Count == 0) return;
                var merged = new List<object>();
                if (SmartGrid.ItemsSource is System.Collections.IEnumerable src)
                    foreach (var o in src) if (o is not SmartInfoRow) merged.Add(o);
                merged.AddRange(extra);
                SmartGrid.ItemsSource = merged;
                ApplySmartColumns();
                UpdateSmartLife(disk, extra);
            }
            catch (Exception ex) { AppLogger.Debug($"MergeNvmeRowsIntoSmartGrid: {ex.Message}"); }
        }
    }
}
