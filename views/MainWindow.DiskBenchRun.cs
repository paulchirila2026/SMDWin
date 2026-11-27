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
using System.Windows.Media.Imaging;
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
using WpfPoint         = System.Windows.Point;
using WpfColorConv     = System.Windows.Media.ColorConverter;
using Line             = System.Windows.Shapes.Line;
using Ellipse          = System.Windows.Shapes.Ellipse;
using Rectangle        = System.Windows.Shapes.Rectangle;
using Polygon          = System.Windows.Shapes.Polygon;

namespace SMDWin.Views
{
    public partial class MainWindow
    {
        // ── Benchmark tab ────────────────────────────────────────────────────


        private readonly List<(string Letter, HashSet<string> All)> _benchDrives = new();

        private static string NormDriveLetter(string? raw)
            => string.IsNullOrWhiteSpace(raw) ? "" : raw.Trim().TrimEnd('\\', ':').ToUpperInvariant() + ":";

        /// <summary>The disk's partition with the most free space (ready, with a letter).</summary>
        private static string? PickBenchLetter(DiskHealthEntry disk, IReadOnlyDictionary<string, SMDWin.Services.DriveSnapshot> ready)
        {
            string? best = null;
            long bestFree = -1;
            foreach (var p in disk.Partitions)
            {
                string l = NormDriveLetter(p.Letter);
                if (l == "" || !ready.TryGetValue(l, out var di)) continue;   // no ready volume
                if (di.FreeBytes > bestFree) { bestFree = di.FreeBytes; best = l; }
            }
            return best;
        }

        private int _benchComboVersion;

        /// <summary>Rebuilds the Benchmark list. Drives are read in the background (see <see cref="SMDWin.Services.DriveSnapshot"/>).</summary>
        private async void PopulateBenchDriveCombo()
        {
            try { await PopulateBenchDriveComboAsync(null); }
            catch (Exception ex) { AppLogger.Warning(ex, "PopulateBenchDriveCombo"); }
        }

        private async Task PopulateBenchDriveComboAsync(List<SMDWin.Services.DriveSnapshot>? drives)
        {
            int version = ++_benchComboVersion;
            drives ??= await SMDWin.Services.DriveSnapshot.ReadyDrivesAsync();
            if (version != _benchComboVersion) return;   // a newer refresh started in the meantime
            var ready = new Dictionary<string, SMDWin.Services.DriveSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in drives) ready[d.Letter] = d;

            string? prevSel = CbBenchDrive.SelectedItem as string;
            CbBenchDrive.Items.Clear();
            _benchDrives.Clear();

            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in _allDisks)
            {
                var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in d.Partitions)
                {
                    string l = NormDriveLetter(p.Letter);
                    if (l != "") { all.Add(l); covered.Add(l); }
                }
                string? work = PickBenchLetter(d, ready);
                if (work == null) continue; // disk with no mounted/ready volume — nothing to run the test on

                CbBenchDrive.Items.Add(DiskComboName(d));
                _benchDrives.Add((work, all));
            }

            // Drives seen by Windows that do not belong to any physical disk in the list (network shares, or a disk whose WMI
            // mapping failed) — kept as separate entries, so nothing is lost.
            foreach (var di in drives)
            {
                string l = di.Letter;
                if (l == "" || covered.Contains(l)) continue;
                CbBenchDrive.Items.Add($"{l}  ({di.Type} drive)");
                _benchDrives.Add((l, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { l }));
            }

            if (CbBenchDrive.Items.Count == 0)
            {
                CbBenchDrive.Items.Add("C:");
                _benchDrives.Add(("C:", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "C:" }));
            }

            // Selection: the previous one (on Refresh), otherwise the Windows disk (C:), otherwise the first.
            int sel = -1;
            if (prevSel != null)
                for (int i = 0; i < CbBenchDrive.Items.Count; i++)
                    if (Equals(CbBenchDrive.Items[i], prevSel)) { sel = i; break; }
            if (sel < 0)
            {
                string sys = NormDriveLetter(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:");
                sel = _benchDrives.FindIndex(x => x.All.Contains(sys));
            }
            CbBenchDrive.SelectedIndex = sel >= 0 ? sel : 0;
        }

        /// <summary>Letter of the volume the benchmark runs on, for the disk chosen in the combo.</summary>
        private string GetSelectedBenchLetter()
        {
            int i = CbBenchDrive.SelectedIndex;
            return i >= 0 && i < _benchDrives.Count ? _benchDrives[i].Letter : "C:";
        }

        // ── BENCHMARK STATE ──────────────────────────────────────────────────
        private CancellationTokenSource? _fullBenchCts;
        private double _liveWriteMbps = 0, _liveReadMbps = 0;
        private string _lastBenchPhase = "";
        private double _frozenWriteMbps = -1, _frozenReadMbps = -1;
        private List<double> _writeSamples = new(), _readSamples = new();
        private double _emaWrite = 0, _emaRead = 0;
        private const double BenchEmaAlpha = 0.35;
        // Real EMA for the CHART — set to 0 in the inactive phase, otherwise the chart would show Write and Read at once (the
        // frozen write value would show as a raised line over the whole read phase, even though the disk no longer writes)
        private double _realEmaWrite = 0, _realEmaRead = 0;
        private System.Windows.Threading.DispatcherTimer? _dialAnimTimer;
        private System.Windows.Threading.DispatcherTimer? _chartSampleTimer;
        private Canvas? _liveChartCanvas;
        // The segmented gauges — replace the needle dials from SetupLiveDials
        private SegmentedGauge? _liveWriteGauge, _liveReadGauge;

        // the first step is 200 MB/s: most drives are SSDs, and a hard disk (~100-180 MB/s) clearly shows as slow below it
        private static readonly double[] DiskGaugeStops = { 200, 600, 1000 };
        private TextBlock? _liveWriteValTb;
        private TextBlock? _liveWriteMaxTb, _liveReadMaxTb;   // "max …" under each dial
        private TextBlock? _liveReadValTb;
        private TextBlock? _liveBenchPercentTb;
        private Border?    _liveBenchBarFillEl;
        private TextBlock? _liveBenchPhaseTb;
        private TextBlock? _liveChartReadHeaderTb, _liveChartWriteHeaderTb;
        private readonly List<double> _chartWritePts = new();
        private readonly List<double> _chartReadPts  = new();

        // Sequential test chart: X axis = the data transferred (0 … the phase volume), not time. Write and read go over the same
        // axis one after the other, so they can be compared directly. Speed is averaged over 200 intervals.
        private const int SeqBuckets = 200;
        private readonly double[] _seqWriteSum = new double[SeqBuckets], _seqReadSum = new double[SeqBuckets];
        private readonly int[] _seqWriteCnt = new int[SeqBuckets], _seqReadCnt = new int[SeqBuckets];
        private double _seqTotalMB;
        private bool _seqReadActive;
        private bool _seqLive;          // the sequential test is running right now (the dot at the end of the line)
        // the minimum and maximum speed of the sequential test (shown under the dials: ▼ min  ▲ max)
        private double _seqWriteMin = double.MaxValue, _seqWriteMax, _seqReadMin = double.MaxValue, _seqReadMax;

        // The whole run on one chart: each stage has its own section (sequential write and read wide, then 4K read, 4K write,
        // latency and the queue-depth tests), so the chart fills until the end of the test, not only during the sequential part.
        private double _benchBar;                  // the overall progress, 0–1
        private bool   _benchDone;
        private int    _benchSeqPasses, _benchTotalUnits;
        private string _benchStageKey = "";
        private int    _benchStageIdx = -1;        // the furthest stage reached (index in BenchStageKeys)
        private static readonly string[] BenchStageKeys = { "seqw", "seqr", "r4k", "w4k", "lat", "qd" };
        // the disk throughput measured during the later stages: (place inside the stage 0–1, MB/s, write?)
        private readonly Dictionary<string, List<(double F, double V, bool W)>> _stageSamples = new();
        private readonly Dictionary<string, string> _stageValue = new();

        private void ResetSeqChart()
        {
            _benchBar = 0; _benchDone = false; _benchSeqPasses = 0; _benchTotalUnits = 0;
            _benchStageKey = ""; _benchStageIdx = -1;
            _stageSamples.Clear(); _stageValue.Clear();
            Array.Clear(_seqWriteSum); Array.Clear(_seqReadSum);
            Array.Clear(_seqWriteCnt); Array.Clear(_seqReadCnt);
            _seqTotalMB = 0; _seqReadActive = false; _seqLive = false;
            _seqWriteMin = double.MaxValue; _seqWriteMax = 0; _seqReadMin = double.MaxValue; _seqReadMax = 0;
        }

        private void AddSeqPoint(bool write, double fraction, double mbps, double totalMB)
        {
            if (mbps <= 0 || double.IsNaN(mbps) || double.IsInfinity(mbps)) return;
            int i = Math.Clamp((int)(fraction * SeqBuckets), 0, SeqBuckets - 1);
            if (write) { _seqWriteSum[i] += mbps; _seqWriteCnt[i]++; _seqWriteMin = Math.Min(_seqWriteMin, mbps); _seqWriteMax = Math.Max(_seqWriteMax, mbps); }
            else       { _seqReadSum[i]  += mbps; _seqReadCnt[i]++;  _seqReadMin  = Math.Min(_seqReadMin, mbps);  _seqReadMax  = Math.Max(_seqReadMax, mbps); }
            if (totalMB > 0) _seqTotalMB = totalMB;
            _seqReadActive = !write;
        }

        private static string FmtMbs(double v) =>
            v >= 1000 ? $"{v/1000.0:F2} GB/s" : $"{v:F0} MB/s";


        // ── STOP ─────────────────────────────────────────────────────────────
        internal void StopFullBench_Click(object sender, RoutedEventArgs e)
        {
            _fullBenchCts?.Cancel();
            if (BtnStopFullBench != null) BtnStopFullBench.Visibility = Visibility.Collapsed;
            if (BtnRunFullBench  != null) BtnRunFullBench.IsEnabled   = true;
            if (BtnDiskBench     != null) BtnDiskBench.IsEnabled      = true;
        }

        // ── RUN ──────────────────────────────────────────────────────────────
        internal async void RunFullBench_Click(object sender, RoutedEventArgs e)
        {
            // the same button starts and stops: while a run is in progress, a click only stops it
            if (_fullBenchCts != null)
            {
                _fullBenchCts.Cancel();
                BtnRunFullBench.IsEnabled = false;   // re-enabled when the run has really stopped (finally below)
                return;
            }
            string drive = GetSelectedBenchLetter();
            _fullBenchCts = new CancellationTokenSource();
            var ct = _fullBenchCts.Token;

            BtnRunFullBench.IsEnabled = true;
            SetStopLook(BtnRunFullBench, running: true);
            if (FindName("TxtBenchStartStop") is System.Windows.Controls.TextBlock tss) tss.Text = LanguageService.S("Disk", "BtnStopBenchmark", "Stop");
            if (FindName("IcoBenchStart") is FrameworkElement icoStart) icoStart.Visibility = Visibility.Collapsed;
            if (FindName("IcoBenchStop")  is FrameworkElement icoStop)  icoStop.Visibility  = Visibility.Visible;
            if (BtnDiskBench != null) BtnDiskBench.IsEnabled = false;

            // Live dials
            _liveWriteMbps = 0; _liveReadMbps = 0;
            _emaWrite = 0; _emaRead = 0;
            _realEmaWrite = 0; _realEmaRead = 0;
            _lastBenchPhase = "";
            _frozenWriteMbps = -1; _frozenReadMbps = -1;
            _writeSamples.Clear(); _readSamples.Clear();
            _liveWriteValTb = null; _liveReadValTb = null;
            _chartWritePts.Clear(); _chartReadPts.Clear();
            // a failure in the live display must not leave the button stuck on "Stop": log it and run anyway
            try
            {
                HighlightBenchStage("");
                ResetSeqChart();
                SetupLiveDials(drive);
                UpdateBenchEmptyState();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "Benchmark live display setup"); }

            _dialAnimTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _dialAnimTimer.Tick += (_, _) =>
            {
                GetSystemDiskIO(out float diskReadKBs, out float diskWriteKBs);
                double rawRead  = diskReadKBs  / 1024.0;
                double rawWrite = diskWriteKBs / 1024.0;

                bool writeActive = _lastBenchPhase == "write" || _lastBenchPhase == "iops_write";
                bool readActive  = _lastBenchPhase == "read"  || _lastBenchPhase == "iops_read";

                if (writeActive) _writeSamples.Add(rawWrite);
                if (readActive)  _readSamples.Add(rawRead);

                // Real EMA (for the chart) — set to 0 in the inactive phase.
                // Without it the dial's EMA (fed with the frozen value) "leaks" data into the chart and both lines show for the whole test.
                double realW = writeActive ? rawWrite : 0.0;
                double realR = readActive  ? rawRead  : 0.0;
                _realEmaWrite += BenchEmaAlpha * (realW - _realEmaWrite);
                _realEmaRead  += BenchEmaAlpha * (realR - _realEmaRead);
                if (_benchStageKey is "r4k" or "w4k" or "lat" or "qd" && (writeActive || readActive))
                {
                    if (!_stageSamples.TryGetValue(_benchStageKey, out var list)) _stageSamples[_benchStageKey] = list = new();
                    list.Add((StageFrac(_benchStageKey, _benchBar), writeActive ? realW : realR, writeActive));
                }

                // Values shown on the dials: active phase → real EMA; inactive → frozen average
                // (the user sees "I wrote at X MB/s" even after the phase ends)
                _liveWriteMbps = writeActive ? _realEmaWrite : (_frozenWriteMbps >= 0 ? _frozenWriteMbps : 0);
                _liveReadMbps  = readActive  ? _realEmaRead  : (_frozenReadMbps  >= 0 ? _frozenReadMbps  : 0);

                UpdateLiveDials();
            };
            _dialAnimTimer.Start();

            _chartSampleTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _chartSampleTimer.Tick += (_, _) =>
            {
                _chartWritePts.Add(_realEmaWrite > 0 ? _realEmaWrite : 0);
                _chartReadPts.Add(_realEmaRead  > 0 ? _realEmaRead  : 0);
            };
            _chartSampleTimer.Start();

            var progress = new Progress<SMDWin.Services.BenchmarkProgress>(p => Dispatcher.Invoke(() =>
            {
                if (TxtBenchPhase     != null) TxtBenchPhase.Text     = p.Message;
                _seqLive = p.SeqFraction >= 0;
                UpdateStageStrip(p.Phase, p.Message);
                NoteBenchProgress(p);
                if (p.SeqFraction >= 0) AddSeqPoint(p.Phase == "write", p.SeqFraction, p.CurrentMBps, p.SeqTotalMB);

                int pct = (int)Math.Round(p.BarFraction * 100);
                if (_liveBenchPercentTb != null) _liveBenchPercentTb.Text = $"{pct}%";
                if (_liveBenchPhaseTb != null && !string.IsNullOrWhiteSpace(p.Message))
                    _liveBenchPhaseTb.Text = p.Message;
                if (_liveBenchBarFillEl != null)
                {
                    double liveTrackW = (_liveBenchBarFillEl.Parent as FrameworkElement)?.ActualWidth ?? 220;
                    _liveBenchBarFillEl.Width = Math.Max(4, liveTrackW * p.BarFraction);
                }

                if (p.Phase != _lastBenchPhase)
                {
                    bool wasWriteActive = _lastBenchPhase == "write" || _lastBenchPhase == "iops_write";
                    bool wasReadActive  = _lastBenchPhase == "read"  || _lastBenchPhase == "iops_read";
                    bool nowWriteActive = p.Phase == "write" || p.Phase == "iops_write";
                    bool nowReadActive  = p.Phase == "read"  || p.Phase == "iops_read";

                    // The write/read phase just ended — freeze the dial on the real average
                    if (wasWriteActive && !nowWriteActive)
                    {
                        var vals = _writeSamples.Where(v => v > 0).ToList();
                        if (vals.Count > 0) _frozenWriteMbps = vals.Average();
                        _writeSamples.Clear();
                    }
                    if (wasReadActive && !nowReadActive)
                    {
                        var vals = _readSamples.Where(v => v > 0).ToList();
                        if (vals.Count > 0) _frozenReadMbps = vals.Average();
                        _readSamples.Clear();
                    }
                    // Entering a write/read phase again (e.g. 4K random) — resume the live reading
                    if (nowWriteActive) _frozenWriteMbps = -1;
                    if (nowReadActive)  _frozenReadMbps  = -1;

                    HighlightBenchStage(p.Phase);
                    _lastBenchPhase = p.Phase;
                }
            }));

            SMDWin.Services.BenchmarkResult? result = null;
            SetNavTaskBadge("TaskBadgeDisk", true);
            try
            {
                var svc = new SMDWin.Services.DiskBenchmarkService();
                // Extended checkbox: checked → forces Full even on USB
                // Unchecked (default) → auto (Quick for Removable, Full for internal)
                bool? forceQuick = null;
                if (FindName("ChkBenchExtended") is System.Windows.Controls.CheckBox chk && chk.IsChecked == true)
                    forceQuick = false; // fortat Full
                result = await svc.RunAsync(drive, ct, progress, forceQuick);

                Dispatcher.Invoke(() =>
                {
                    _dialAnimTimer?.Stop(); _dialAnimTimer = null;
                    _chartSampleTimer?.Stop(); _chartSampleTimer = null;
                    AppLogger.Info($"Benchmark chart: {_seqWriteCnt.Count(filled => filled > 0)} intervals with writes, {_seqReadCnt.Count(filled => filled > 0)} with reads (of {SeqBuckets}), volume {_seqTotalMB:0} MB");
                    if (TxtBenchPhase    != null) TxtBenchPhase.Text    = "✓ Benchmark complete";
                    if (TxtBenchAnimSpeed != null) TxtBenchAnimSpeed.Text = "";
                    if (TxtBenchAnimUnit  != null) TxtBenchAnimUnit.Text  = "";

                    // Per-disk history: read the previous run BEFORE saving the current one
                    string diskKey = CbBenchDrive.SelectedItem as string ?? drive;
                    var prevRun = SMDWin.Services.DiskBenchHistory.GetLast(diskKey, result!.IsQuickMode);
                    // a run without any measurement (cancelled / failed) does not enter the history
                    if (result!.ReadAvgMBps > 0 || result!.WriteAvgMBps > 0)
                        SMDWin.Services.DiskBenchHistory.Add(diskKey, result!);

                    // Replaces the live panel with the results card — always a single card
                    BenchResultsPanel.Children.Clear();
                    BenchResultsPanel.Children.Add(BuildBenchResultV2(drive, result!, prevRun));
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppDialog.Show(string.Format(LanguageService.S("Msg", "BenchmarkErrorFmt"), ex.Message));
            }
            finally
            {
                _dialAnimTimer?.Stop(); _dialAnimTimer = null;
                _chartSampleTimer?.Stop(); _chartSampleTimer = null;
                SetStopLook(BtnRunFullBench, running: false);
                _fullBenchCts = null;
                BtnRunFullBench.IsEnabled = true;
                if (FindName("TxtBenchStartStop") is System.Windows.Controls.TextBlock tss2) tss2.Text = LanguageService.S("Disk", "BtnStartBenchmark");
                if (FindName("IcoBenchStart") is FrameworkElement icoStart2) icoStart2.Visibility = Visibility.Visible;
                if (FindName("IcoBenchStop")  is FrameworkElement icoStop2)  icoStop2.Visibility  = Visibility.Collapsed;
                if (BtnDiskBench != null) BtnDiskBench.IsEnabled = true;
                SetNavTaskBadge("TaskBadgeDisk", false);
                UpdateBenchEmptyState(); // covers Stop/error (the final result is already covered above)
            }
        }

        // ── Highlight the pill of the current test stage (Write/Read/Random 4K/Latency) ──
        private void HighlightBenchStage(string phase)
        {
            int active = phase switch
            {
                "write"               => 1,
                "read"                => 2,
                "iops_write" or "iops_read" => 3,
                "latency"             => 4,
                "done"                => 4,
                _                     => 0
            };
            var accent = (Brush)FindResource("AccentBrush");
            var dim    = (Brush)FindResource("TextSecondaryBrush");
            var bgOff  = (Brush)FindResource("BgInputBrush");
            var bgOn   = new SolidColorBrush(WpfColor.FromArgb(35, 59, 130, 246));

            void Set(Border? pill, TextBlock? text, int idx)
            {
                if (pill == null || text == null) return;
                bool on = idx == active;
                pill.Background = on ? bgOn : bgOff;
                text.Foreground = on ? accent : dim;
                text.FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
            }
            Set(FindName("BenchStagePill1") as Border, FindName("BenchStageText1") as TextBlock, 1);
            Set(FindName("BenchStagePill2") as Border, FindName("BenchStageText2") as TextBlock, 2);
            Set(FindName("BenchStagePill3") as Border, FindName("BenchStageText3") as TextBlock, 3);
            Set(FindName("BenchStagePill4") as Border, FindName("BenchStageText4") as TextBlock, 4);
        }

        // ── LIVE DIALS SETUP ─────────────────────────────────────────────────
        // Creates a single card with: live dials (top) + a persistent chart (bottom).
        // The chart stays visible in the final results card too — it is not duplicated.
        private readonly List<(string Key, TextBlock Chip)> _liveStageChips = new();
        private int _liveStageIndex = -1;

        /// <summary>Test stage by the reported phase (and message, for the deep-queue tests).</summary>
        private static string StageKeyFor(string phase, string? message)
        {
            bool qd = message?.Contains("QD", StringComparison.OrdinalIgnoreCase) == true;
            if (qd && phase is "read" or "write" or "iops_read" or "iops_write") return "qd";
            return phase switch
            {
                "calibrate" or "write" => "seqw",
                "read" => "seqr",
                "iops_read" => "r4k",
                "iops_write" => message?.Contains("preparing", StringComparison.OrdinalIgnoreCase) == true ? "r4k" : "w4k",
                "latency" => "lat",
                _ => "",
            };
        }

        private void UpdateStageStrip(string phase, string? message)
        {
            if (_liveStageChips.Count == 0) return;
            if (phase == "done")
            {
                _liveStageIndex = _liveStageChips.Count;
            }
            else
            {
                string key = StageKeyFor(phase, message);
                int idx = _liveStageChips.FindIndex(c => c.Key == key);
                if (idx < 0 || idx < _liveStageIndex) return;
                _liveStageIndex = idx;
            }
            for (int i = 0; i < _liveStageChips.Count; i++)
            {
                var chip = _liveStageChips[i].Chip;
                string baseText = chip.Tag as string ?? chip.Text;
                if (i < _liveStageIndex)
                {
                    chip.Text = "✓ " + baseText; chip.FontWeight = FontWeights.Normal;
                    chip.SetResourceReference(TextBlock.ForegroundProperty, "StatusOkBrush");
                }
                else if (i == _liveStageIndex)
                {
                    chip.Text = "● " + baseText; chip.FontWeight = FontWeights.SemiBold;
                    chip.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                }
                else
                {
                    chip.Text = baseText; chip.FontWeight = FontWeights.Normal;
                    chip.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                }
            }
        }

        private void SetupLiveDials(string drive)
        {
            BenchResultsPanel.Children.Clear();

            var writeColor = WpfColor.FromRgb(249, 115,  22); // #F97316 — Write (ChartOrangeBrush)
            var readColor  = WpfColor.FromRgb( 59, 130, 246); // #3B82F6 — Read (blue; Write is orange, no green anywhere in the benchmark)
            string L(string en, string ro, string de, string fr) => SMDWin.Services.LanguageService.CurrentCode switch
                { "ro" => ro, "de" => de, "fr" => fr, _ => en };

            // Stepped segmented gauges: 100 MB/s at 1/3, 600 at 2/3, 1000 at the end; the scale extends by itself
            // (2.5K, 5K, 10K…) for fast NVMe drives. The value is written in the centre.
            _liveWriteValTb = null;  _liveReadValTb = null;
            SegmentedGauge MakeGauge(WpfColor c) => new SegmentedGauge
            {
                // the same dial as in the rest of the app — the accent colour
                Width = 150, Height = 130, Stops = DiskGaugeStops, Unit = "MB/s",
            };
            _liveWriteGauge = MakeGauge(writeColor);
            _liveReadGauge  = MakeGauge(readColor);

            StackPanel GaugePanel(string label, WpfColor c, SegmentedGauge g)
            {
                var sp = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
                sp.Children.Add(new TextBlock
                {
                    Text = label, FontSize = Ui.FontS, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2)
                });
                ((TextBlock)sp.Children[sp.Children.Count - 1]).SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                sp.Children.Add(g);
                var mx = new TextBlock { Text = "", FontSize = Ui.FontXS, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
                mx.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                sp.Children.Add(mx);
                if (ReferenceEquals(g, _liveWriteGauge)) _liveWriteMaxTb = mx; else _liveReadMaxTb = mx;
                return sp;
            }
            var dialsRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
            // Read first, then Write — the same order as everywhere else
            var rp = GaugePanel(L("READ", "CITIRE", "LESEN", "LECTURE"), readColor, _liveReadGauge);
            rp.Margin = new Thickness(0, 0, 28, 0);
            dialsRow.Children.Add(rp);
            dialsRow.Children.Add(GaugePanel(L("WRITE", "SCRIERE", "SCHREIBEN", "ÉCRITURE"), writeColor, _liveWriteGauge));

            var progressBlock = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            var pctTb = new TextBlock { Text = "0%", FontSize = Ui.FontL, FontWeight = FontWeights.Bold, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
            pctTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            progressBlock.Children.Add(pctTb);
            var barTrack = new Border { Width = 280, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
            barTrack.SetResourceReference(Border.BackgroundProperty, "BgInputBrush");
            var barFill = new Border { Width = 0, Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            barFill.SetResourceReference(Border.BackgroundProperty, "DataBrush");
            barTrack.Child = barFill;
            progressBlock.Children.Add(barTrack);
            var testingTb = new TextBlock { Text = L("Starting…","Se pregătește…","Wird vorbereitet…","Préparation…"), FontSize = Ui.FontS, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
            testingTb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            progressBlock.Children.Add(testingTb);
            _liveBenchPercentTb = pctTb;
            _liveBenchBarFillEl = barFill;
            _liveBenchPhaseTb = testingTb;

            // live chart under the dials, as in the Internet speed test — except the X axis shows the data transferred, not time
            _liveChartCanvas = new Canvas { ClipToBounds = true };
            _liveChartCanvas.SizeChanged += (_, _) => { if (_liveChartCanvas != null) DrawSeqChart(_liveChartCanvas); };
            var chartHost = new Border
            {
                Margin = new Thickness(0, 8, 0, 0), Height = 220, MaxWidth = 1500, CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6, 8, 4), Child = _liveChartCanvas, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            };
            // no box behind the chart: like every other chart, only the grid
            var legend = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
            _liveChartWriteHeaderTb = new TextBlock { FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(writeColor), Text = L("Write ", "Scriere ", "Schreiben ", "Écriture ") + "—" };
            _liveChartReadHeaderTb  = new TextBlock { FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(readColor), Margin = new Thickness(0, 0, 24, 0), Text = L("Read ", "Citire ", "Lesen ", "Lecture ") + "—" };
            legend.Children.Add(_liveChartReadHeaderTb); legend.Children.Add(_liveChartWriteHeaderTb);

            // the test stages after the sequential chart (4K, latency, queue): the chart stops when the write/read ends, and the
            // strip shows where the test is now and what has already finished
            var stageStrip = new System.Windows.Controls.WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            _liveStageChips.Clear();
            var stageNames = new (string Key, string Text)[]
            {
                ("seqw", L("Sequential write", "Scriere secvențială", "Sequenziell schreiben", "Écriture séquentielle")),
                ("seqr", L("Sequential read", "Citire secvențială", "Sequenziell lesen", "Lecture séquentielle")),
                ("r4k",  L("4K random read", "Citire 4K aleatoare", "4K zufällig lesen", "Lecture 4K aléatoire")),
                ("w4k",  L("4K random write", "Scriere 4K aleatoare", "4K zufällig schreiben", "Écriture 4K aléatoire")),
                ("lat",  L("Latency", "Latență", "Latenz", "Latence")),
                ("qd",   L("Queue depth", "Adâncime coadă", "Warteschlangentiefe", "Profondeur de file")),
            };
            foreach (var (key, text) in stageNames)
            {
                var chip = new TextBlock { Text = text, FontSize = Ui.FontS, Margin = new Thickness(8, 2, 8, 2), Tag = text };
                chip.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                stageStrip.Children.Add(chip);
                _liveStageChips.Add((key, chip));
            }
            _liveStageIndex = -1;
            stageStrip.Visibility = Visibility.Collapsed;   // the labels under the chart sections show the stages now

            var inner = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            inner.Children.Add(dialsRow); inner.Children.Add(progressBlock); inner.Children.Add(legend); inner.Children.Add(chartHost); inner.Children.Add(stageStrip);

            var liveBorder = new Border
            {
                Tag             = "LiveDialPanel",
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(8),
                Padding         = new Thickness(16, 14, 16, 14),
                Margin          = new Thickness(0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                Child           = inner
            };
            liveBorder.SetResourceReference(Border.BackgroundProperty, "CardSurfaceBrush");
            liveBorder.SetResourceReference(Border.BorderBrushProperty, "CardEdgeBrush");
            BenchResultsPanel.Children.Add(liveBorder);

            // On large screens the live card fills the page, and the dials grow with it.
            FillBenchViewport(liveBorder);
            liveBorder.SizeChanged += (_, e) =>
            {
                // the chart gets most of the height (it shows the whole run); the dials take what is left. The sum stays under
                // the card height, so the card (MinHeight = the visible area) does not grow from its own resize.
                double H = e.NewSize.Height;
                double h = Math.Clamp((H - 420) * 0.45, 120, 200);
                foreach (var g in new[] { _liveWriteGauge, _liveReadGauge })
                    if (g != null) { g.Height = h; g.Width = h * 1.16; }
                chartHost.Height = Math.Clamp(H - h - 230, 200, 460);
            };
        }

        // ── LIVE DIALS UPDATE ─────────────────────────────────────────────────
        private void UpdateLiveDials()
        {
            // EMA smoothing — removes needle jitter (factor 0.12 = ~2 s visual lag)
            _emaWrite = _emaWrite + BenchEmaAlpha * (_liveWriteMbps - _emaWrite);
            _emaRead  = _emaRead  + BenchEmaAlpha * (_liveReadMbps  - _emaRead);

            // The segmented gauges manage their scale by themselves (log + auto-extension)
            if (_liveWriteGauge != null)
            {
                if (_seqWriteMax > 0) { _liveWriteGauge.RangeMin = _seqWriteMin; _liveWriteGauge.RangeMax = _seqWriteMax; }
                _liveWriteGauge.Value = _emaWrite;
            }
            if (_liveWriteMaxTb != null) _liveWriteMaxTb.Text = _seqWriteMax > 0 ? "max " + FmtMbs(_seqWriteMax) : "";
            if (_liveReadMaxTb != null) _liveReadMaxTb.Text = _seqReadMax > 0 ? "max " + FmtMbs(_seqReadMax) : "";
            if (_liveReadGauge != null)
            {
                if (_seqReadMax > 0) { _liveReadGauge.RangeMin = _seqReadMin; _liveReadGauge.RangeMax = _seqReadMax; }
                _liveReadGauge.Value = _emaRead;
            }

            // Live chart: a 90 s window anchored left (fullWindow:false) → the line grows from left to right, as on the dashboard
            if (_liveChartCanvas != null && _liveChartCanvas.ActualWidth > 10)
                DrawSeqChart(_liveChartCanvas);

            // Direct reference — no visual tree walk per tick
            if (_liveWriteValTb != null)
            {
                _liveWriteValTb.Text       = _emaWrite > 0 ? FmtMbs(_emaWrite) : "—";
                _liveWriteValTb.Foreground = new SolidColorBrush(WpfColor.FromRgb(249, 115, 22));
            }
            if (_liveReadValTb != null)
            {
                _liveReadValTb.Text       = _emaRead > 0 ? FmtMbs(_emaRead) : "—";
                _liveReadValTb.Foreground = new SolidColorBrush(WpfColor.FromRgb(59, 130, 246));
            }

            string LH(string ro, string en, string de, string fr) => SMDWin.Services.LanguageService.CurrentCode switch
                { "ro" => ro, "de" => de, "fr" => fr, _ => en };
            // the same value as in the dial above
            double shownRead  = _emaRead  > 0 ? _emaRead  : _liveReadMbps;
            double shownWrite = _emaWrite > 0 ? _emaWrite : _liveWriteMbps;
            if (_liveChartReadHeaderTb  != null) _liveChartReadHeaderTb.Text  = LH("Citire ", "Read ", "Lesen ", "Lecture ")  + (shownRead  > 0 ? FmtMbs(shownRead)  : "—");
            if (_liveChartWriteHeaderTb != null) _liveChartWriteHeaderTb.Text = LH("Scriere ", "Write ", "Schreiben ", "Écriture ") + (shownWrite > 0 ? FmtMbs(shownWrite) : "—");
        }


        private static TextBlock? FindVisualDescendantByTag(DependencyObject parent, string tag)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is TextBlock tb && tb.Tag?.ToString() == tag) return tb;
                var found = FindVisualDescendantByTag(child, tag);
                if (found != null) return found;
            }
            return null;
        }

        // ── THE SEQUENTIAL TEST CHART ─────────────────────────────────────
        // the axis starts at 200 MB/s and grows by round steps; each top divides into 4 round grid values
        private static readonly double[] SeqTops = { 200, 400, 600, 800, 1000, 1200, 1600, 2000, 3000, 4000, 6000, 8000, 10000, 12000, 16000 };

        private static string FmtDataMB(double mb) => mb >= 1000 ? $"{mb / 1024:0.#} GB" : $"{mb:0} MB";

        private void NoteBenchProgress(SMDWin.Services.BenchmarkProgress p)
        {
            if (p.SeqPasses > 0) _benchSeqPasses = p.SeqPasses;
            if (p.TotalUnits > 0) _benchTotalUnits = p.TotalUnits;
            // preparing the 4K file (written before the 4K read): the 4K read section is shown as next, but empty — the writes of
            // the preparation are not the 4K test, and its progress would fill the section before the test starts
            if (p.Message?.Contains("preparing", StringComparison.OrdinalIgnoreCase) == true)
            {
                _benchStageIdx = Math.Max(_benchStageIdx, 2); _benchStageKey = "";
                return;
            }
            _benchBar = p.BarFraction;
            if (p.Phase == "done") { _benchDone = true; _benchStageIdx = BenchStageKeys.Length; _benchStageKey = ""; return; }
            string key = StageKeyFor(p.Phase, p.Message);
            int idx = Array.IndexOf(BenchStageKeys, key);
            if (idx < 0 || idx < _benchStageIdx) return;
            _benchStageIdx = idx; _benchStageKey = key;

            static string L(string en, string ro, string de, string fr) => SMDWin.Services.LanguageService.CurrentCode switch
                { "ro" => ro, "de" => de, "fr" => fr, _ => en };
            if (key is "r4k" or "w4k" && p.Iops > 0)
                _stageValue[key] = $"{p.Iops:N0} IOPS";
            else if (key == "lat" && p.LatencyUs > 0)
                _stageValue[key] = p.LatencyUs < 1000 ? $"{p.LatencyUs:0} µs" : $"{p.LatencyUs / 1000:0.0} ms";
            else if (key == "qd")
            {
                string m = p.Message ?? "";
                string q = m.Contains("QD32") ? "QD32" : "QD8";
                bool w = p.Phase is "write" or "iops_write";
                _stageValue[key] = q + " " + (w ? L("write", "scriere", "Schreiben", "écriture") : L("read", "citire", "Lesen", "lecture"));
            }
        }

        /// <summary>The sections of the chart: progress units (as the service counts them) and the visual place of each stage.</summary>
        private List<(string Key, double U0, double UL, double X0, double XL)> BenchStages()
        {
            int n = Math.Max(1, _benchSeqPasses);
            bool qd = _benchTotalUnits == 0 || _benchTotalUnits > 2 * n + 3;
            double[] vis   = qd ? new[] { 0.29, 0.29, 0.09, 0.09, 0.08, 0.16 } : new[] { 0.33, 0.33, 0.12, 0.12, 0.10 };
            double[] units = { n, n, 1, 1, 1, 4 };
            var list = new List<(string, double, double, double, double)>();
            double u = 0, x = 0;
            for (int k = 0; k < vis.Length; k++)
            {
                list.Add((BenchStageKeys[k], u, units[k], x, vis[k]));
                u += units[k]; x += vis[k];
            }
            return list;
        }

        /// <summary>How far a stage has got (0–1), from the overall progress.</summary>
        private double StageFrac(string key, double bar)
        {
            int total = _benchTotalUnits > 0 ? _benchTotalUnits : 0;
            if (total == 0) return 0;
            foreach (var st in BenchStages())
                if (st.Key == key) return Math.Clamp((bar * total - st.U0) / st.UL, 0, 1);
            return 0;
        }

        private void DrawSeqChart(Canvas cv)
        {
            cv.Children.Clear();
            double w = cv.ActualWidth, h = cv.ActualHeight;
            if (w < 80 || h < 60) return;

            var writeColor = WpfColor.FromRgb(249, 115, 22);   // the same as the "Write" label
            var readColor  = WpfColor.FromRgb(59, 130, 246);   // the same as the "Read" label
            var labelBrush = (TryFindResource("TextSecondaryBrush") as Brush) ?? System.Windows.Media.Brushes.Gray;
            var okBrush     = (TryFindResource("StatusOkBrush") as Brush) ?? new SolidColorBrush(readColor);
            var accentBrush = (TryFindResource("AccentBrush") as Brush) ?? System.Windows.Media.Brushes.DodgerBlue;
            var gridBrush  = new SolidColorBrush(WpfColor.FromArgb(40, 140, 150, 170));
            var stages = BenchStages();
            (string Key, double U0, double UL, double X0, double XL) St(string key) => stages.First(t => t.Key == key);

            double max = 0;
            for (int i = 0; i < SeqBuckets; i++)
            {
                if (_seqWriteCnt[i] > 0) max = Math.Max(max, _seqWriteSum[i] / _seqWriteCnt[i]);
                if (_seqReadCnt[i]  > 0) max = Math.Max(max, _seqReadSum[i]  / _seqReadCnt[i]);
            }
            foreach (var l in _stageSamples.Values) foreach (var sm in l) max = Math.Max(max, sm.V);
            double top = SeqTops[^1];
            foreach (double t in SeqTops) if (t >= Math.Max(1, max) * 1.12) { top = t; break; }
            if (max > top) top = max * 1.15;

            const double pL = 46, pR = 12, pT = 8, pB = 22;
            static string L(string en, string ro, string de, string fr) => SMDWin.Services.LanguageService.CurrentCode switch
                { "ro" => ro, "de" => de, "fr" => fr, _ => en };
            double cW = w - pL - pR, cH = h - pT - pB;
            double X(string key, double f) { var st = St(key); return pL + cW * (st.X0 + st.XL * Math.Clamp(f, 0, 1)); }
            double Y(double v) => pT + cH * (1 - Math.Clamp(v / top, 0, 1));

            void Label(string text, double x, double y, double width, TextAlignment align)
            {
                var tb = new TextBlock { Text = text, FontSize = Ui.FontXS, Foreground = labelBrush, Width = width, TextAlignment = align };
                Canvas.SetLeft(tb, x); Canvas.SetTop(tb, y);
                cv.Children.Add(tb);
            }

            // the shared chart grid (thin lines fading towards their ends, no frame) behind the plot; its rows match the labels
            if (ChartGrid.Background(cW, cH, ChartGrid.LineColor(cv), 4, 0, ChartGrid.Scale(cv)) is Brush gridBg)
            {
                var gridRect = new System.Windows.Shapes.Rectangle { Width = cW, Height = cH, Fill = gridBg, IsHitTestVisible = false };
                Canvas.SetLeft(gridRect, pL); Canvas.SetTop(gridRect, pT);
                cv.Children.Add(gridRect);
            }
            // Y axis: speed (MB/s) — the same scale for every section
            for (int g = 0; g <= 4; g++)
            {
                double y = pT + cH * (1 - g / 4.0);
                double v = top * g / 4.0;
                if (g > 0) Label(v >= 1000 ? $"{v / 1000:0.#}K" : $"{v:0}", 0, y - 8, pL - 6, TextAlignment.Right);
            }

            // sections: a dotted separator and, under the axis, the stage name (✓ done, accent = now, grey = still to come)
            string data = _seqTotalMB > 0 ? " · " + FmtDataMB(_seqTotalMB) : "";
            var names = new Dictionary<string, string>
            {
                ["seqw"] = L("Write", "Scriere", "Schreiben", "Écriture") + data,
                ["seqr"] = L("Read", "Citire", "Lesen", "Lecture") + data,
                ["r4k"]  = L("4K read", "Citire 4K", "4K lesen", "Lecture 4K"),
                ["w4k"]  = L("4K write", "Scriere 4K", "4K schreiben", "Écriture 4K"),
                ["lat"]  = L("Latency", "Latență", "Latenz", "Latence"),
                ["qd"]   = L("Queue depth", "Adâncime coadă", "Warteschlange", "Profondeur de file"),
            };
            for (int k = 0; k < stages.Count; k++)
            {
                var st = stages[k];
                double x0 = pL + cW * st.X0, sw = cW * st.XL;
                if (k > 0)
                    cv.Children.Add(new Line { X1 = x0, X2 = x0, Y1 = pT, Y2 = pT + cH, Stroke = gridBrush, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } });
                bool done = k < _benchStageIdx || _benchDone, now = !done && k == _benchStageIdx;
                Brush fg = k < 2 && !now ? new SolidColorBrush(k == 0 ? writeColor : readColor) : done ? okBrush : now ? accentBrush : labelBrush;
                var tb = new TextBlock
                {
                    Text = (done && k >= 2 ? "✓ " : "") + names[st.Key], FontSize = Ui.FontXS, Foreground = fg,
                    FontWeight = now ? FontWeights.SemiBold : FontWeights.Normal,
                    Width = sw, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Canvas.SetLeft(tb, x0); Canvas.SetTop(tb, pT + cH + 4);
                cv.Children.Add(tb);
            }

            void Area(PointCollection pts, WpfColor color, bool dot)
            {
                if (pts.Count == 0) return;
                var area = new PointCollection(pts) { new WpfPoint(pts[^1].X, pT + cH) };
                area.Insert(0, new WpfPoint(pts[0].X, pT + cH));
                cv.Children.Add(new Polygon { Points = area, Fill = new SolidColorBrush(WpfColor.FromArgb(30, color.R, color.G, color.B)) });
                cv.Children.Add(new Polyline
                {
                    Points = pts, Stroke = new SolidColorBrush(color), StrokeThickness = 2,
                    StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                });
                if (dot)
                {
                    var e = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(color) };
                    Canvas.SetLeft(e, pts[^1].X - 4); Canvas.SetTop(e, pts[^1].Y - 4);
                    cv.Children.Add(e);
                }
            }

            // sequential write and read: X = the data transferred inside their section
            void Series(double[] sum, int[] cnt, WpfColor color, bool active, string key)
            {
                var pts = new PointCollection();
                for (int i = 0; i < SeqBuckets; i++)
                {
                    if (cnt[i] == 0) continue;
                    pts.Add(new WpfPoint(X(key, (i + 0.5) / SeqBuckets), Y(sum[i] / cnt[i])));
                }
                Area(pts, color, active);
            }
            bool running = _seqLive;
            Series(_seqWriteSum, _seqWriteCnt, writeColor, running && !_seqReadActive, "seqw");
            Series(_seqReadSum,  _seqReadCnt,  readColor,  running && _seqReadActive, "seqr");

            // the later stages: the disk throughput while they ran (read green, write orange) and their live value on top
            foreach (var key in new[] { "r4k", "w4k", "lat", "qd" })
            {
                var st = St(key);
                int k = Array.IndexOf(BenchStageKeys, key);
                if (k > _benchStageIdx && !_benchDone) continue;
                double x0 = pL + cW * st.X0, sw = cW * st.XL;
                bool now = k == _benchStageIdx && !_benchDone;
                if (_stageSamples.TryGetValue(key, out var list) && list.Count > 0)
                {
                    // contiguous runs of the same direction (the queue-depth part switches between read and write)
                    var run = new PointCollection(); bool runW = list[0].W;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var sm = list[i];
                        if (sm.W != runW && run.Count > 0) { Area(run, runW ? writeColor : readColor, false); run = new PointCollection { run[^1] }; runW = sm.W; }
                        run.Add(new WpfPoint(X(key, sm.F), Y(sm.V)));
                    }
                    Area(run, runW ? writeColor : readColor, now);
                }
                if (_stageValue.TryGetValue(key, out var val))
                {
                    var vt = new TextBlock
                    {
                        Text = val, FontSize = Ui.FontXS, FontWeight = FontWeights.SemiBold, Width = sw, TextAlignment = TextAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis, Foreground = now ? accentBrush : labelBrush,
                        Background = TryFindResource("BgCardBrush") as Brush,
                    };
                    Canvas.SetLeft(vt, x0); Canvas.SetTop(vt, pT + 2);
                    cv.Children.Add(vt);
                }
            }

            // the average of each sequential section (dotted line + value) or "no data" when it stayed empty
            void Average(double[] sum, int[] cnt, WpfColor color, string key, string none)
            {
                double total = 0; int n = 0;
                for (int i = 0; i < SeqBuckets; i++) if (cnt[i] > 0) { total += sum[i] / cnt[i]; n++; }
                var st = St(key);
                double left = pL + cW * st.X0, sw = cW * st.XL;
                if (n == 0)
                {
                    if (_seqLive || !_benchDone) return;   // the test is running: this part has not started yet
                    var t = new TextBlock { Text = none, FontSize = Ui.FontS, Foreground = labelBrush, Width = sw, TextAlignment = TextAlignment.Center };
                    Canvas.SetLeft(t, left); Canvas.SetTop(t, pT + cH / 2 - 8); cv.Children.Add(t);
                    return;
                }
                double avg = total / n;
                double y = Y(avg);
                cv.Children.Add(new Line
                {
                    X1 = left + 2, X2 = left + sw - 2, Y1 = y, Y2 = y, StrokeThickness = 1,
                    Stroke = new SolidColorBrush(WpfColor.FromArgb(150, color.R, color.G, color.B)), StrokeDashArray = new DoubleCollection { 4, 3 },
                });
                // the label sits at the top, on the card background — next to the dotted line it overlapped the curve (which passes right there)
                var lab = new TextBlock { Text = L("avg ", "medie ", "Ø ", "moy. ") + FmtMbs(avg), FontSize = Ui.FontXS, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(color),
                                          Padding = new Thickness(3, 0, 3, 0), Background = TryFindResource("BgCardBrush") as Brush };
                Canvas.SetLeft(lab, left + 6); Canvas.SetTop(lab, pT + 1);
                cv.Children.Add(lab);
            }
            Average(_seqWriteSum, _seqWriteCnt, writeColor, "seqw", L("no write data", "fără date de scriere", "keine Schreibdaten", "pas de données d'écriture"));
            Average(_seqReadSum,  _seqReadCnt,  readColor,  "seqr", L("no read data", "fără date de citire", "keine Lesedaten", "pas de données de lecture"));
        }

    }
}
