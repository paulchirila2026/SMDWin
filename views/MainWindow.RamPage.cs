using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Models;
using SMDWin.Services;
using Border      = System.Windows.Controls.Border;
using TextBlock   = System.Windows.Controls.TextBlock;
using FontFamily  = System.Windows.Media.FontFamily;
using HAlign      = System.Windows.HorizontalAlignment;
using VAlign      = System.Windows.VerticalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace SMDWin.Views
{
    /// <summary>
    /// The RAM page: the usage card (dial + 2 charts + footer), the slots as blocks, the short process list. The rest
    /// (configuration, tests) are the older TextBlocks, moved into cards and filled by LoadRamPageAsync as before.
    /// </summary>
    public partial class MainWindow
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RamPerfInfo
        {
            public uint cb;
            public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable,
                           SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
            public uint HandleCount, ProcessCount, ThreadCount;
        }
        [DllImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true)]
        private static extern bool RamGetPerformanceInfo(out RamPerfInfo pi, uint cb);

        private System.Windows.Threading.DispatcherTimer? _ramCardsTimer;
        private System.Diagnostics.PerformanceCounter? _ramHardFaults;
        private bool _ramCardsHooked;
        private double _ramInstalledGB;

        /// <summary>Starts sampling for the usage card (1 s) and matches the grid height to the window.</summary>
        private void StartRamCards()
        {
            try
            {
                if (!_ramCardsHooked && FindName("PanelRamScroll") is ScrollViewer sv)
                {
                    _ramCardsHooked = true;
                    // after layout: on IsVisibleChanged the visible area does not have its final height yet
                    sv.SizeChanged += (_, _) =>
                    {
                        FitRamCardsGrid();
                        Dispatcher.BeginInvoke(new Action(FitRamCardsGrid), System.Windows.Threading.DispatcherPriority.Loaded);
                    };
                    sv.IsVisibleChanged += (_, _) => Dispatcher.BeginInvoke(new Action(FitRamCardsGrid), System.Windows.Threading.DispatcherPriority.Loaded);
                    // the "hard faults" counter is slow to create (1-2 s) → in the background
                    _ = System.Threading.Tasks.Task.Run(() =>
                    {
                        try { _ramHardFaults = new System.Diagnostics.PerformanceCounter("Memory", "Pages Input/sec", true); _ramHardFaults.NextValue(); }
                        catch (Exception ex) { AppLogger.Debug($"RAM hard faults counter: {ex.Message}"); }
                    });
                }
                FitRamCardsGrid();
                Dispatcher.BeginInvoke(new Action(FitRamCardsGrid), System.Windows.Threading.DispatcherPriority.Loaded);
                if (_ramCardsTimer == null)
                {
                    _ramCardsTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    _ramCardsTimer.Tick += (_, _) => SampleRamCards();
                }
                SampleRamCards();
                _ramCardsTimer.Start();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "StartRamCards"); }
        }

        /// <summary>The grid takes at least the visible area (no empty space at the bottom); the full process table stays below.</summary>
        private void FitRamCardsGrid()
        {
            try
            {
                if (FindName("PanelRamScroll") is not ScrollViewer sv || FindName("RamCardsGrid") is not Grid g
                    || FindName("PanelRam") is not Grid page || page.RowDefinitions.Count == 0) return;
                var titleRow = page.Children.OfType<FrameworkElement>().FirstOrDefault(c => Grid.GetRow(c) == 0);
                // the title takes its margins too: without them the grid came out a few pixels taller than the visible area and the scroll bar appeared
                double title = (titleRow?.ActualHeight ?? 60) + (titleRow?.Margin.Top ?? 0) + (titleRow?.Margin.Bottom ?? 0);
                // ActualHeight, not ViewportHeight: when restoring from maximized, ViewportHeight still has the old value for a moment
                if (sv.ActualHeight <= 0) return;
                double h = sv.ActualHeight - page.Margin.Top - page.Margin.Bottom - title - g.Margin.Top - g.Margin.Bottom - 14;
                double mh = Math.Max(560, h);
                if (Math.Abs(g.MinHeight - mh) > 1) g.MinHeight = mh;
            }
            catch (Exception ex) { AppLogger.Debug($"FitRamCardsGrid: {ex.Message}"); }
        }

        /// <summary>The memory history (GB used, GB committed), one read per second from start-up, so the RAM page charts
        /// show the last minute as soon as the page opens.</summary>
        private readonly SecondHistory _ramHist = new(2);
        private System.Windows.Threading.DispatcherTimer? _ramHistTimer;

        private void StartRamHistory()
        {
            if (_ramHistTimer != null) return;
            _ramHistTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _ramHistTimer.Tick += (_, _) => RecordRamHistory();
            _ramHistTimer.Start();
        }

        private void RecordRamHistory()
        {
            try
            {
                if (!RamGetPerformanceInfo(out var pi, (uint)Marshal.SizeOf<RamPerfInfo>())) return;
                double page = (double)pi.PageSize.ToUInt64();
                float used = (float)(Math.Max(0, (pi.PhysicalTotal.ToUInt64() - pi.PhysicalAvailable.ToUInt64()) * page) / 1_073_741_824.0);
                float commit = (float)(pi.CommitTotal.ToUInt64() * page / 1_073_741_824.0);
                _ramHist.Push(DateTime.UtcNow, new[] { used, commit });
            }
            catch (Exception ex) { AppLogger.Debug($"RecordRamHistory: {ex.Message}"); }
        }

        private void SampleRamCards()
        {
            try
            {
                if (FindName("PanelRamScroll") is FrameworkElement host && !host.IsVisible) return;
                if (!RamGetPerformanceInfo(out var pi, (uint)Marshal.SizeOf<RamPerfInfo>())) return;
                double page = (double)pi.PageSize.ToUInt64();
                double gb(UIntPtr pages) => pages.ToUInt64() * page / 1_073_741_824.0;
                double total = gb(pi.PhysicalTotal), avail = gb(pi.PhysicalAvailable), used = Math.Max(0, total - avail);
                double commit = gb(pi.CommitTotal), limit = gb(pi.CommitLimit), peak = gb(pi.CommitPeak), cache = gb(pi.SystemCache);
                double pct = total > 0 ? used / total * 100 : 0;
                string G(double v) => v >= 10 ? $"{v:F1} GB" : $"{v:F2} GB";

                if (FindName("RamPageGauge") is PercentGauge g) { g.HasValue = total > 0; g.Value = pct; }
                if (FindName("TxtRamPagePct") is TextBlock tp) tp.Text = $"{pct:F0}";
                if (FindName("TxtRamLiveStatus") is TextBlock st)
                {
                    // the same words as on the Dashboard
                    st.Text = pct >= 90 ? LanguageService.S("Dashboard", "StateCritical", "Critical")
                            : pct >= 75 ? LanguageService.S("Dashboard", "StateHigh", "High")
                            : LanguageService.S("Dashboard", "StateOk", "OK");
                    st.SetResourceReference(TextBlock.ForegroundProperty, pct >= 90 ? "StatusCritBrush" : pct >= 75 ? "StatusWarnBrush" : "StatusOkBrush");
                }
                if (FindName("TxtRamLiveName") is TextBlock nm)
                    nm.Text = _ramInstalledGB > 0 ? PT("{0} GB installed · {1} GB usable", _ramInstalledGB.ToString("0.#"), total.ToString("F2"))
                                                   : PT("{0} GB usable", total.ToString("F2"));

                if (FindName("RamChartInUse") is MiniLineChart c1)
                {
                    c1.Max = total;
                    if (_ramHist.Count >= 2) c1.ShowHistory(_ramHist, 60, 0); else c1.Push(used);
                    if (FindName("TxtRamLiveInUseNote") is TextBlock n1) n1.Text = PT("last minute · max {0}", G(c1.Peak));
                }
                if (FindName("TxtRamLiveInUse") is TextBlock v1) v1.Text = G(used);
                if (FindName("RamChartCommit") is MiniLineChart c2)
                {
                    c2.Max = limit;
                    if (_ramHist.Count >= 2) c2.ShowHistory(_ramHist, 60, 1); else c2.Push(commit);
                    if (FindName("TxtRamLiveCommitNote") is TextBlock n2) n2.Text = PT("RAM + page file · peak {0}", G(peak));
                }
                if (FindName("TxtRamLiveCommit") is TextBlock v2) v2.Text = $"{commit:F1} / {limit:F1} GB";

                if (FindName("TxtRamLiveAvail") is TextBlock a) a.Text = G(avail);
                if (FindName("TxtRamLiveCached") is TextBlock ca) ca.Text = G(cache);
                if (FindName("TxtRamLivePageFile") is TextBlock pf)
                {
                    double pfGB = Math.Max(0, limit - total);
                    pf.Text = pfGB >= 0.05 ? G(pfGB) : "none";
                }
                if (FindName("TxtRamLiveFaults") is TextBlock hf)
                {
                    float v = 0;
                    try { v = _ramHardFaults?.NextValue() ?? -1; } catch (Exception ex) { AppLogger.Debug($"hard faults: {ex.Message}"); v = -1; }
                    hf.Text = v >= 0 ? $"{v:F0} /s" : "";
                }
            }
            catch (Exception ex) { AppLogger.Debug($"SampleRamCards: {ex.Message}"); }
        }

        // ══ The slots as blocks ════════════════════════════════════════════════════
        private void BuildRamSlotTiles(List<RamEntry> modules)
        {
            try
            {
                if (FindName("RamSlotTiles") is not System.Windows.Controls.Primitives.UniformGrid host) return;
                host.Children.Clear();
                var ordered = modules.OrderBy(m => m.SlotIndex).ToList();
                int filled = ordered.Count(m => !m.IsEmpty);
                _ramInstalledGB = ordered.Where(m => !m.IsEmpty).Sum(m => (double)m.CapacityBytes) / 1_073_741_824.0;
                // memory soldered on the board (LPDDR): no slots, no sticks — the chips on the board (MainWindow.RamSoldered.cs)
                if (BuildRamSolderedTiles(host, ordered)) return;
                // The extra block appears only when it says something useful: the memory is soldered on the board (LPDDR), so it cannot
                // be changed. "No free slots left" is not shown — the occupied slots are already visible.
                bool soldered = ordered.Any(m => !m.IsEmpty && (m.MemoryType ?? "").StartsWith("LPDDR", StringComparison.OrdinalIgnoreCase));
                bool upgradeTile = soldered && filled == ordered.Count && ordered.Count < 4;
                int tiles = ordered.Count + (upgradeTile ? 1 : 0);
                host.Columns = Math.Min(4, Math.Max(2, tiles));
                host.Rows = (int)Math.Ceiling(tiles / (double)host.Columns);

                if (FindName("TxtRamModulesSummary") is TextBlock sum)
                    sum.Text = T4($"{filled} of {ordered.Count} slots populated", $"{filled} din {ordered.Count} sloturi ocupate",
                                  $"{filled} von {ordered.Count} Steckplätzen belegt", $"{filled} emplacements occupés sur {ordered.Count}");
                // the empty slot is drawn at the same width as the installed modules (SO-DIMM = short), not across the whole block
                var firstModule = ordered.FirstOrDefault(m => !m.IsEmpty);
                _ramTilesSoDimm = firstModule != null && SMDWin.Services.DimmLayout.IsCompact(firstModule.FormFactor, firstModule.MemoryType);
                _ramTilesMemType = (firstModule?.MemoryType ?? "").Trim();
                _ramTilesFormFactor = firstModule == null ? "" : _ramTilesSoDimm ? "SO-DIMM" : "DIMM";
                if (FindName("TxtRamModulesRight") is TextBlock right)
                {
                    var f = ordered.FirstOrDefault(m => !m.IsEmpty);
                    right.Text = f == null ? "" : string.Join(" · ", new[] { f.MemoryType, f.FormFactor }.Where(x => !string.IsNullOrWhiteSpace(x) && x != "—"));
                }

                for (int i = 0; i < ordered.Count; i++)
                    host.Children.Add(MakeRamSlotTile(ordered[i], i, (i + 1) % host.Columns == 0 || (i == ordered.Count - 1 && !upgradeTile)));
                if (upgradeTile) host.Children.Add(MakeRamUpgradeTile(ordered.Count, ordered.FirstOrDefault(m => !m.IsEmpty)));
            }
            catch (Exception ex) { AppLogger.Warning(ex, "BuildRamSlotTiles"); }
        }

        private Border MakeRamSlotTile(RamEntry m, int index, bool lastInRow)
        {
            var tile = new Border
            {
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, lastInRow ? 0 : 12, 0),
            };
            tile.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            var sp = new StackPanel { VerticalAlignment = VAlign.Center };
            tile.Child = sp;

            // the title is always "Slot 1…4" (in board order); the BIOS label ("DIMM0 #1", "ChannelA-DIMM0") and the bank sit
            // on the right, small
            string slotWord = LanguageService.S("Ram", "SlotWord", "Slot");
            string label = $"{slotWord} {index + 1}";
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            string biosLabel = (m.SlotLabel ?? "").Trim();
            if (biosLabel.StartsWith("Slot", StringComparison.OrdinalIgnoreCase)) biosLabel = "";
            // the bank label only when it says something new (some BIOSes put "Slot 3" as the bank of slot 2 → confusing)
            string bankTxt = (m.BankLabel ?? "").Trim();
            if (bankTxt.StartsWith("Slot", StringComparison.OrdinalIgnoreCase) || bankTxt.Equals(m.SlotLabel ?? "", StringComparison.OrdinalIgnoreCase)) bankTxt = "";
            bankTxt = string.Join(" · ", new[] { biosLabel, bankTxt }.Where(x => x.Length > 0));
            var bank = RamTxt(bankTxt, Ui.FontXS, "TextSecondaryBrush", 0.7);
            DockPanel.SetDock(bank, Dock.Right);
            top.Children.Add(bank);
            var lt = RamTxt(label, Ui.FontS, "TextSecondaryBrush"); lt.FontWeight = FontWeights.Bold;
            top.Children.Add(lt);
            sp.Children.Add(top);

            // each slot is drawn with its socket (DimmSocketPicture): occupied = the module seated, clips closed;
            // empty = the empty socket, clips open. The same height everywhere, so the slots stay aligned.
            if (!m.IsEmpty)
            {
                bool soDimm = DimmLayout.IsCompact(m.FormFactor, m.MemoryType) || _ramTilesSoDimm;
                sp.Children.Add(new DimmSocketPicture { MemoryType = m.MemoryType ?? "", SoDimm = soDimm, Installed = true,
                                                        HorizontalAlignment = soDimm ? HAlign.Left : HAlign.Stretch,
                                                        Margin = new Thickness(0, 0, 0, 12) });
                var size = new TextBlock { FontFamily = TryFindResource("NumFont") as FontFamily, FontSize = 22, FontWeight = FontWeights.SemiBold };
                size.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                size.Inlines.Add(new System.Windows.Documents.Run(m.CapacityBytes > 0 ? $"{m.CapacityBytes / 1_073_741_824.0:0.#} GB" : m.Capacity));
                string spd = m.ConfiguredMHz > 0 ? $"{m.ConfiguredMHz} MHz" : m.Speed;
                var det = new System.Windows.Documents.Run("  " + string.Join(" · ", new[] { m.MemoryType, spd }.Where(x => !string.IsNullOrWhiteSpace(x))))
                    { FontSize = Ui.FontM, FontWeight = FontWeights.Normal };
                det.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextSecondaryBrush");
                size.Inlines.Add(det);
                sp.Children.Add(size);
                sp.Children.Add(RamTxt(string.Join(" · ", new[] { m.Manufacturer, m.FormFactor }.Where(x => !string.IsNullOrWhiteSpace(x) && x != "—")), Ui.FontS, "TextSecondaryBrush"));
                if (!string.IsNullOrWhiteSpace(m.PartNumber))
                {
                    var pn = RamTxt(m.PartNumber.Trim(), Ui.FontS, "TextSecondaryBrush", 0.75);
                    pn.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
                    sp.Children.Add(pn);
                }
                var tip = new List<string> { $"{label}: {m.Capacity} {m.MemoryType}" };
                if (m.RatedMHz > 0) tip.Add($"Rated {m.RatedMHz} MHz · configured {m.ConfiguredMHz} MHz");
                if (m.VoltageV > 0) tip.Add($"Voltage {m.VoltageV:F2} V");
                tip.Add(m.IsEcc ? "ECC" : "Non-ECC");
                tile.ToolTip = string.Join("\n", tip);
            }
            else
            {
                var socket = new DimmSocketPicture { MemoryType = _ramTilesMemType, SoDimm = _ramTilesSoDimm,
                                                     HorizontalAlignment = _ramTilesSoDimm ? HAlign.Left : HAlign.Stretch,
                                                     Margin = new Thickness(0, 0, 0, 12) };
                sp.Children.Add(socket);
                var e = RamTxt(LanguageService.S("Ram", "SlotFree", "Free"), Ui.FontL, "TextSecondaryBrush", 0.9); e.FontWeight = FontWeights.SemiBold;
                e.FontSize = 22;
                sp.Children.Add(e);
                string accepts = string.Join(" ", new[] { _ramTilesFormFactor, _ramTilesMemType }.Where(x => !string.IsNullOrWhiteSpace(x)));
                sp.Children.Add(RamTxt(accepts.Length > 0
                        ? string.Format(LanguageService.S("Ram", "SlotTakes", "Takes a {0} module"), accepts)
                        : LanguageService.S("Ram", "SlotFreeSub", "Free slot"),
                    Ui.FontS, "TextSecondaryBrush", 0.8));
                tile.ToolTip = LanguageService.S("Ram", "SlotEmptyTip", "Empty slot — a memory module can be added here.");
            }
            return tile;
        }

        /// <summary>The "Upgrade" block: all slots are occupied — what can be done and how far.</summary>
        private Border MakeRamUpgradeTile(int slots, RamEntry? first)
        {
            var tile = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(14, 12, 14, 12) };
            tile.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            tile.SetResourceReference(Border.BackgroundProperty, "BgHoverBrush");
            var sp = new StackPanel { VerticalAlignment = VAlign.Center };
            tile.Child = sp;
            var cap = RamTxt(PT("UPGRADE"), Ui.FontXS, "TextSecondaryBrush"); cap.FontWeight = FontWeights.Bold;
            sp.Children.Add(cap);
            bool soldered = (first?.MemoryType ?? "").StartsWith("LPDDR", StringComparison.OrdinalIgnoreCase);
            var head = RamTxt(PT(soldered ? "Memory is soldered" : slots == 1 ? "No free slot" : "No free slots"), Ui.FontL, "TextPrimaryBrush");
            head.FontWeight = FontWeights.SemiBold; head.Margin = new Thickness(0, 4, 0, 6);
            sp.Children.Add(head);
            var why = RamTxt(PT(soldered
                    ? "LPDDR memory is part of the motherboard and cannot be changed."
                    : slots == 1 ? "To add memory, replace this module with a bigger one." : "To add memory, replace a module with a bigger one — ideally all with the same size and speed."),
                Ui.FontS, "TextSecondaryBrush");
            why.TextWrapping = TextWrapping.Wrap; why.TextTrimming = TextTrimming.None;
            sp.Children.Add(why);
            if (!soldered && FindName("TxtRamMaxConf") is TextBlock max && !string.IsNullOrWhiteSpace(max.Text) && max.Text != "—")
            {
                var k = RamTxt(PT("Max supported"), Ui.FontS, "TextSecondaryBrush"); k.Margin = new Thickness(0, 10, 0, 0);
                sp.Children.Add(k);
                var v = RamTxt(max.Text, Ui.FontL, "TextPrimaryBrush"); v.FontWeight = FontWeights.SemiBold;
                sp.Children.Add(v);
            }
            return tile;
        }

        // the slot form factor (SO-DIMM / DIMM) and the memory type, taken from the first installed module
        private bool _ramTilesSoDimm;
        private string _ramTilesMemType = "", _ramTilesFormFactor = "";

        private static TextBlock RamTxt(string text, double size, string brushKey, double opacity = 1)
        {
            var t = new TextBlock { Text = text ?? "", FontSize = size, Opacity = opacity, TextTrimming = TextTrimming.CharacterEllipsis };
            t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return t;
        }

        // ══ The short process list (the "Top processes" card) ═════════════════════════
        private void UpdateRamTopList(List<GroupedProcessEntry> grouped)
        {
            try
            {
                if (FindName("RamTopList") is not StackPanel host) return;
                host.Children.Clear();
                // how many rows fit in the card (~31 px per row), so the list reaches the bottom
                int rows = 7;
                if (host.Parent is Grid pg && pg.RowDefinitions.Count > 1 && pg.RowDefinitions[1].ActualHeight > 0)
                    rows = Math.Clamp((int)((pg.RowDefinitions[1].ActualHeight - 6) / 31), 5, 30);
                var top = grouped.OrderByDescending(g => g.TotalRamMB).Take(rows).ToList();
                if (top.Count == 0) return;
                double max = Math.Max(1, top[0].TotalRamMB);
                foreach (var p in top)
                {
                    var row = new Grid { Margin = new Thickness(0, 0, 0, 9) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                    string exe = string.IsNullOrWhiteSpace(p.ExeName) ? p.DisplayName : p.ExeName;
                    if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = exe[..^4];
                    string name = exe + (p.InstanceCount > 1 ? $" · {p.InstanceCount}" : "");
                    var n = RamTxt(name, Ui.FontM, "TextPrimaryBrush");
                    n.VerticalAlignment = VAlign.Center; n.Margin = new Thickness(0, 0, 10, 0);
                    n.ToolTip = p.DisplayName;
                    row.Children.Add(n);

                    var bar = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = 12, Height = 7, VerticalAlignment = VAlign.Center };
                    int lit = Math.Clamp((int)Math.Round(p.TotalRamMB / max * 12), 1, 12);
                    for (int i = 0; i < 12; i++)
                    {
                        var b = new Border { CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 0, i < 11 ? 2 : 0, 0) };
                        b.SetResourceReference(Border.BackgroundProperty, i < lit ? "AccentBrush" : "BorderBrush2");
                        bar.Children.Add(b);
                    }
                    Grid.SetColumn(bar, 1); row.Children.Add(bar);

                    var v = new TextBlock
                    {
                        Text = p.TotalRamMB >= 1024 ? $"{p.TotalRamMB / 1024:F2} GB" : $"{p.TotalRamMB:F0} MB",
                        FontFamily = TryFindResource("NumFont") as FontFamily, FontSize = 15, FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HAlign.Right, VerticalAlignment = VAlign.Center,
                    };
                    v.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                    Grid.SetColumn(v, 2); row.Children.Add(v);
                    host.Children.Add(row);
                }
            }
            catch (Exception ex) { AppLogger.Debug($"UpdateRamTopList: {ex.Message}"); }
        }
    }
}
