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
        // ── Surface Scan tab ─────────────────────────────────────────────────

        private CancellationTokenSource? _surfaceScanCts;
        private double _surfaceSpeedMin      = double.MaxValue;
        private double _surfaceSpeedMax      = 0;
        private long   _surfaceSlowOrange    = 0;  // >150ms/MB
        private long   _surfaceSlowYellow    = 0;  // >80ms/MB
        private readonly System.Collections.Generic.List<double> _surfaceSpeedHistory = new();
        private readonly System.Collections.Generic.List<(long start, long end)> _badSectorRanges = new();
        private readonly System.Collections.Generic.List<long> _badSectorLbasRaw = new();
        private const int BadSectorRangesShownMax = 20;
        private System.Diagnostics.Stopwatch? _badSectorMapRedrawSw;

        // The window of the "Read speed along the drive" chart during the scan: the last N minutes (0 = the whole scanned area).
        // Changed from the chart's right-click menu.
        // the area shown by the speed chart: −1 = the whole disk, 0 = what has been scanned, N = the last N minutes
        private int _surfaceChartWindowMin = SettingsService.Current.SurfaceChartWindowMin;
        private DateTime _surfaceChartT0 = DateTime.Now;

        private byte[] _sectorMapSeverity  = Array.Empty<byte>(); // 0=nescanat,1=ok,2-4=lent,5=defect
        private long   _sectorMapTotalSectors;

        // ── Data for the Surface scan charts ─────────────────────────────
        // Histogram of response times: time per MB read (ms), in 6 intervals:
        // <10 | 10-30 | 30-120 | 120-500 | >500 | read error. The 120/500 thresholds are the same as for "slow" on the map, so
        // the two views say the same thing.
        private static readonly string[] SurfaceHistLabels = { "< 10 ms", "10–30 ms", "30–120 ms", "120–500 ms", "> 500 ms", "Read error" };
        private static readonly string[] SurfaceHistBrushes = { "StatusOkBrush", "StatusOkBrush", "AccentBrush", "StatusWarningBrush", "YellowBrush", "RedBrush" };
        private readonly long[] _surfaceHist = new long[6];
        private System.Windows.Controls.ColumnDefinition[]? _histFillCols, _histRestCols;
        private TextBlock[]? _histVals;

        // Speed across the surface: sum/count over equal intervals of the disk (SpeedCurveChart.Bins).
        private readonly object _surfaceDataLock = new();
        private readonly double[] _surfaceBinSum   = new double[SpeedCurveChart.Bins];
        private readonly int[]    _surfaceBinCount = new int[SpeedCurveChart.Bins];

        // The list of problems: slow / unreadable zones, neighbouring blocks merged into a single zone.
        private sealed class SurfaceProblem
        {
            public long StartLba, EndLba;
            public double PeakMsPerMB;
            public bool Bad;
            public SurfaceProblem Copy() => (SurfaceProblem)MemberwiseClone();
        }
        private const int SurfaceProblemsMax = 1000;
        private readonly List<SurfaceProblem> _surfaceProblems = new();
        private long _surfaceProblemsOverflow;
        private int  _surfaceProblemsRendered = -1;
        private long _surfaceProblemsRenderedEnd = -1;
        private int  _surfaceBytesPerSector = 512;
        private Border? _surfaceSelectedProblemRow;

        /// <summary>Initializes the block map when a new scan starts. The number of squares is chosen by the space in the card
        /// (~15px per square) and stays fixed until the end.</summary>
        private void InitSectorMap(long totalSectors, int bytesPerSector = 512)
        {
            _sectorMapTotalSectors = totalSectors;
            _surfaceBytesPerSector = bytesPerSector;
            _sectorMapSeverity = new byte[SurfaceBlockMap.ChooseCellCount()];
            SurfaceBlockMap.Attach(_sectorMapSeverity);
            SurfaceSpeedChart.TotalBytes = totalSectors * (double)bytesPerSector;
            // How much a square represents — helps reading the map ("a yellow square = ~450 MB slow")
            double cellMb = totalSectors * (double)bytesPerSector / 1048576.0 / Math.Max(1, _sectorMapSeverity.Length);
            TxtSurfaceMapTitle.Text = cellMb >= 1024
                ? $"Sector map  ·  each square ≈ {cellMb / 1024:F1} GB"
                : $"Sector map  ·  each square ≈ {cellMb:F0} MB";
            UpdateSurfaceEmptyState();
        }

        private int CellIndexForSector(long sector)
        {
            if (_sectorMapTotalSectors <= 0 || _sectorMapSeverity.Length == 0) return 0;
            int totalCells = _sectorMapSeverity.Length;
            long idx = (long)((double)sector / _sectorMapTotalSectors * totalCells);
            return (int)Math.Clamp(idx, 0, totalCells - 1);
        }

        internal void ExportBadSectors_Click(object sender, RoutedEventArgs e)
        {
            if (_badSectorLbasRaw.Count == 0) return;
            try
            {
                var dlg = new SaveFileDialog
                {
                    Filter   = "Text file|*.txt",
                    FileName = $"SMDWin_BadSectors_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
                };
                if (dlg.ShowDialog() != true) return;

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"SMDWin — Bad sector report — {DateTime.Now:yyyy-MM-dd HH:mm}");
                sb.AppendLine($"Total bad sectors: {_badSectorLbasRaw.Count:N0}  ·  {_badSectorRanges.Count:N0} range(s)");
                sb.AppendLine();
                sb.AppendLine("Ranges:");
                foreach (var (start, end) in _badSectorRanges)
                {
                    long count = end - start + 1;
                    sb.AppendLine(count == 1 ? $"  LBA {start:N0}" : $"  LBA {start:N0}\u2013{end:N0}  ({count:N0} sectors)");
                }
                sb.AppendLine();
                sb.AppendLine("All LBAs (raw):");
                foreach (var lba in _badSectorLbasRaw) sb.AppendLine(lba.ToString("N0"));

                System.IO.File.WriteAllText(dlg.FileName, sb.ToString());
                AppDialog.Show(LanguageService.S("Msg", "BadSectorsSaved"), "SMD Win", AppDialog.Kind.Success);
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "ExportBadSectors_Click");
                AppDialog.Show(_LS("Common", "ErrorPrefix") + ex.Message, "SMD Win", AppDialog.Kind.Warning);
            }
        }

        /// <summary>Marks a block's cell with the given severity, but never downgrades a cell already marked as worse (bad is
        /// sticky for the duration of the scan).</summary>
        private void MarkSectorCell(long sector, byte severity)
        {
            int idx = CellIndexForSector(sector);
            if (idx < 0 || idx >= _sectorMapSeverity.Length) return;
            if (severity > _sectorMapSeverity[idx]) _sectorMapSeverity[idx] = severity;
        }

        /// <summary>Redraws the block map (once per second, in a batch).</summary>
        private void FillSectorMapBitmap() => SurfaceBlockMap.InvalidateVisual();

        internal void SurfaceBlockMap_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            int cellIdx = SurfaceBlockMap.CellAt(e.GetPosition(SurfaceBlockMap));
            if (cellIdx < 0 || cellIdx >= _sectorMapSeverity.Length || _sectorMapTotalSectors <= 0)
            {
                SurfaceBlockMap.ToolTip = null;
                return;
            }
            int totalCells = _sectorMapSeverity.Length;
            long lbaStart = (long)((double)cellIdx       / totalCells * _sectorMapTotalSectors);
            long lbaEnd   = (long)((double)(cellIdx + 1) / totalCells * _sectorMapTotalSectors) - 1;
            double gb0 = lbaStart * (double)_surfaceBytesPerSector / 1_073_741_824.0;
            double gb1 = (lbaEnd + 1) * (double)_surfaceBytesPerSector / 1_073_741_824.0;
            string state = _sectorMapSeverity[cellIdx] switch
            {
                0 => "not scanned yet",
                1 => "OK",
                5 => "unreadable sectors",
                _ => "slow reads",
            };
            SurfaceBlockMap.ToolTip = $"{gb0:F1} – {gb1:F1} GB  ·  {state}\nLBA {lbaStart:N0} – {lbaEnd:N0}";
        }

        internal void SurfaceSpeedChart_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
            => SurfaceSpeedChart.ToolTip = SurfaceSpeedChart.DescribeAt(e.GetPosition(SurfaceSpeedChart));

        // ── Response time histogram ──────────────────────────────────────
        private void BuildSurfaceHistogram()
        {
            SurfaceHistPanel.Children.Clear();
            _histFillCols = new System.Windows.Controls.ColumnDefinition[6];
            _histRestCols = new System.Windows.Controls.ColumnDefinition[6];
            _histVals     = new TextBlock[6];
            for (int i = 0; i < 6; i++)
            {
                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(76) });
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(58) });

                var lbl = new TextBlock { Text = SurfaceHistLabels[i], FontSize = Ui.FontS, VerticalAlignment = VerticalAlignment.Center };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                row.Children.Add(lbl);

                // Bar: two proportional columns (filled / rest) — the width does not depend on ActualWidth
                var bar = new Grid { Height = 8, VerticalAlignment = VerticalAlignment.Center };
                var fillCol = new System.Windows.Controls.ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
                var restCol = new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
                bar.ColumnDefinitions.Add(fillCol);
                bar.ColumnDefinitions.Add(restCol);
                var track = new Border { CornerRadius = new CornerRadius(4) };
                track.SetResourceReference(Border.BackgroundProperty, "BorderBrush2");
                Grid.SetColumnSpan(track, 2);
                var fill = new Border { CornerRadius = new CornerRadius(4) };
                fill.SetResourceReference(Border.BackgroundProperty, SurfaceHistBrushes[i]);
                bar.Children.Add(track);
                bar.Children.Add(fill);
                Grid.SetColumn(bar, 1);
                row.Children.Add(bar);

                var val = new TextBlock
                {
                    Text = "—", FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                };
                val.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
                val.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                Grid.SetColumn(val, 2);
                row.Children.Add(val);

                _histFillCols[i] = fillCol;
                _histRestCols[i] = restCol;
                _histVals[i] = val;
                SurfaceHistPanel.Children.Add(row);
            }
        }

        private void UpdateSurfaceHistogram(long[] hist, bool final)
        {
            if (_histVals == null) BuildSurfaceHistogram();
            long total = 0;
            foreach (var v in hist) total += v;
            for (int i = 0; i < 6; i++)
            {
                double frac = total > 0 ? hist[i] / (double)total : 0;
                if (hist[i] > 0) frac = Math.Max(frac, 0.015); // a problem must be visible, even if it is 1 in 400,000
                _histFillCols![i].Width = new GridLength(frac, GridUnitType.Star);
                _histRestCols![i].Width = new GridLength(Math.Max(0, 1 - frac), GridUnitType.Star);

                // The "normal" rows show the percentage; the problem rows show the exact number of blocks.
                string text;
                if (total == 0) text = "—";
                else if (i < 3) text = hist[i] == 0 ? "0%" : frac * 100 < 0.1 ? "<0.1%" : $"{hist[i] * 100.0 / total:F1}%";
                else text = hist[i].ToString("N0");
                _histVals![i].Text = text;
                _histVals[i].SetResourceReference(TextBlock.ForegroundProperty,
                    i >= 3 && hist[i] > 0 ? SurfaceHistBrushes[i] : "TextPrimaryBrush");
            }
            long problems = hist[3] + hist[4] + hist[5];
            TxtSurfaceHistNote.Text = total == 0
                ? PT("A healthy drive keeps almost everything in the top rows.")
                : problems == 0
                    ? (final ? "Every block responded normally." : "Every block so far responded normally.")
                    : $"{problems:N0} block{(problems == 1 ? "" : "s")} slow or unreadable — see Problems found below.";
        }

        // ── The list of problems ────────────────────────────────────────────────────
        /// <summary>Adds a slow/unreadable zone; neighbouring blocks of the same type merge. Called from the scan thread.</summary>
        private void AddSurfaceProblem(long startLba, long endLba, double msPerMB, bool bad)
        {
            lock (_surfaceDataLock)
            {
                if (_surfaceProblems.Count > 0)
                {
                    var last = _surfaceProblems[^1];
                    if (last.Bad == bad && startLba <= last.EndLba + 1)
                    {
                        last.EndLba = Math.Max(last.EndLba, endLba);
                        last.PeakMsPerMB = Math.Max(last.PeakMsPerMB, msPerMB);
                        return;
                    }
                }
                if (_surfaceProblems.Count >= SurfaceProblemsMax) { _surfaceProblemsOverflow++; return; }
                _surfaceProblems.Add(new SurfaceProblem { StartLba = startLba, EndLba = endLba, PeakMsPerMB = msPerMB, Bad = bad });
            }
        }

        private bool _surfaceProblemsExpanded;

        /// <summary>The zone list is collapsed by default (the summary in the header is enough); "Show list" opens it.</summary>
        internal void SurfaceProblemsToggle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _surfaceProblemsExpanded = !_surfaceProblemsExpanded;
            if (SurfaceProblemsScroll != null) SurfaceProblemsScroll.Visibility = _surfaceProblemsExpanded ? Visibility.Visible : Visibility.Collapsed;
            if (TxtSurfaceProblemsToggle != null)
                TxtSurfaceProblemsToggle.Text = SMDWin.Services.LiteralTexts.Translate(_surfaceProblemsExpanded ? "Hide list" : "Show list");
        }

        private void RenderSurfaceProblems(List<SurfaceProblem> list, long overflow)
        {
            long lastEnd = list.Count > 0 ? list[^1].EndLba : -1;
            if (list.Count == _surfaceProblemsRendered && lastEnd == _surfaceProblemsRenderedEnd) return;
            _surfaceProblemsRendered = list.Count;
            _surfaceProblemsRenderedEnd = lastEnd;

            SurfaceProblemsCard.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            long total = list.Count + overflow;
            int badAreas = list.Count(p => p.Bad), slowAreas = list.Count - badAreas;
            TxtSurfaceProblemsCount.Text = total == 0 ? ""
                : $"{total:N0} area{(total == 1 ? "" : "s")}" + (badAreas > 0 ? $" · {badAreas:N0} unreadable" : "") + (slowAreas > 0 ? $" · {slowAreas:N0} slow" : "");
            SurfaceProblemsList.Children.Clear();
            _surfaceSelectedProblemRow = null;

            const int shownMax = 200;
            double bps = _surfaceBytesPerSector;
            foreach (var pr in list.Take(shownMax))
            {
                var row = new Border
                {
                    Padding = new Thickness(6, 5, 6, 5), CornerRadius = new CornerRadius(6),
                    Cursor = System.Windows.Input.Cursors.Hand, Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                };
                row.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                var g = new Grid();
                g.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(18) });
                g.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(110) });

                var sq = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
                sq.SetResourceReference(Border.BackgroundProperty, pr.Bad ? "RedBrush" : "YellowBrush");
                g.Children.Add(sq);

                double gb = pr.StartLba * bps / 1_073_741_824.0;
                double sizeMb = (pr.EndLba - pr.StartLba + 1) * bps / 1048576.0;
                string sizeTxt = sizeMb >= 1024 ? $"{sizeMb / 1024:F1} GB" : sizeMb >= 1 ? $"{sizeMb:F0} MB" : $"{(pr.EndLba - pr.StartLba + 1):N0} sectors";
                var what = new TextBlock
                {
                    Text = $"{(pr.Bad ? "Unreadable" : "Slow read")} at {gb:F1} GB  ({sizeTxt})",
                    FontSize = Ui.FontM, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                };
                what.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                Grid.SetColumn(what, 1); g.Children.Add(what);

                var lba = new TextBlock { Text = $"LBA {pr.StartLba:N0}", FontSize = Ui.FontS, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
                lba.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                lba.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
                Grid.SetColumn(lba, 2); g.Children.Add(lba);

                var sev = new TextBlock
                {
                    Text = pr.Bad ? "read error" : $"{pr.PeakMsPerMB:F0} ms / MB",
                    FontSize = Ui.FontM, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                };
                sev.SetResourceReference(TextBlock.ForegroundProperty, pr.Bad ? "RedBrush" : "YellowBrush");
                Grid.SetColumn(sev, 3); g.Children.Add(sev);
                row.Child = g;

                long startCapture = pr.StartLba;
                row.MouseLeftButtonUp += (_, __) =>
                {
                    if (_surfaceSelectedProblemRow != null) _surfaceSelectedProblemRow.Background = System.Windows.Media.Brushes.Transparent;
                    row.SetResourceReference(Border.BackgroundProperty, "BgHoverBrush");
                    _surfaceSelectedProblemRow = row;
                    SurfaceBlockMap.HighlightCell = CellIndexForSector(startCapture);
                    SurfaceBlockMap.InvalidateVisual();
                };
                SurfaceProblemsList.Children.Add(row);
            }
            if (total > shownMax)
            {
                var more = new TextBlock
                {
                    Text = $"+ {total - shownMax:N0} more areas (the full list of unreadable sectors can be exported)",
                    FontSize = Ui.FontS, Margin = new Thickness(6, 6, 0, 0),
                };
                more.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                SurfaceProblemsList.Children.Add(more);
            }
        }

        /// <summary>A snapshot (on the scan thread) of the chart data + applying it to the UI.</summary>
        private Action SnapshotSurfaceVisuals(long processed, long totalSectors, long currentSector, bool final)
        {
            double[] binSum; int[] binCount; List<SurfaceProblem> probs; long overflow;
            lock (_surfaceDataLock)
            {
                binSum   = (double[])_surfaceBinSum.Clone();
                binCount = (int[])_surfaceBinCount.Clone();
                probs    = _surfaceProblems.Select(p => p.Copy()).ToList();
                overflow = _surfaceProblemsOverflow;
            }
            var hist = new long[6];
            for (int i = 0; i < 6; i++) hist[i] = Interlocked.Read(ref _surfaceHist[i]);
            double pos = totalSectors > 0 ? Math.Min(1.0, processed / (double)totalSectors) : 0;

            return () =>
            {
                var marks = probs
                    .OrderByDescending(p => p.Bad)          // defects first, if there are many
                    .Take(300)
                    .Select(p => (totalSectors > 0 ? p.StartLba / (double)totalSectors : 0, p.Bad));
                bool complete = pos >= 0.999;
                var (vs, ve) = SurfaceChartView(pos, complete);
                SurfaceSpeedChart.ViewStart = vs;
                SurfaceSpeedChart.ViewEnd = ve;
                SurfaceSpeedChart.SetData(binSum, binCount, marks);
                SurfaceSpeedChart.CurrentPos = final ? -1 : pos;
                TxtSurfaceSpeedSubtitle.Text = complete ? "  ·  start → end of the drive"
                    : _surfaceChartWindowMin > 0 ? $"  ·  last {_surfaceChartWindowMin} min · right-click to change"
                    : _surfaceChartWindowMin == 0 ? "  ·  everything scanned so far · right-click to change"
                    : PT("  ·  start → end of the drive · right-click to zoom in");
                UpdateSurfaceHistogram(hist, final);
                RenderSurfaceProblems(probs, overflow);
                SurfaceBlockMap.CurrentCell = final ? -1 : CellIndexForSector(currentSector);
            };
        }

        /// <summary>
        /// The disk area shown on the chart. By default the whole disk: the axis goes from 0 to its capacity, and the curve
        /// advances with the scan. The N-minute window has a fixed width: how much the disk reads in N minutes, estimated from
        /// the average speed so far. Until it fills, the curve grows from the left; then it "flows" to the left, with the
        /// current position near the right edge.
        /// </summary>
        private (double start, double end) SurfaceChartView(double pos, bool complete)
        {
            if (complete || _surfaceChartWindowMin < 0) return (0, 1);
            double min = (DateTime.Now - _surfaceChartT0).TotalMinutes;
            if (_surfaceChartWindowMin <= 0 || min < 0.1 || pos <= 0)
                return (0, Math.Clamp(pos * 1.08, 0.002, 1.0));
            double span = Math.Clamp(pos / min * _surfaceChartWindowMin, 0.002, 1.0);
            if (pos <= span * 0.95) return (0, span);
            double end = Math.Min(1.0, pos + span * 0.05);
            return (Math.Max(0, end - span), end);
        }

        internal void SurfaceChartWindow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.MenuItem mi || !int.TryParse(mi.Tag?.ToString(), out int m)) return;
            _surfaceChartWindowMin = m;
            SettingsService.Current.SurfaceChartWindowMin = m;
            try { SettingsService.Save(); } catch (Exception ex) { AppLogger.Warning(ex, "SurfaceChartWindow save"); }
        }

        /// <summary>The menu check is set on open, by the variant currently used.</summary>
        internal void SurfaceChartMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.ContextMenu cm) return;
            foreach (var o in cm.Items.OfType<System.Windows.Controls.MenuItem>())
                o.IsChecked = int.TryParse(o.Tag?.ToString(), out int m) && m == _surfaceChartWindowMin;
        }

        // ── temperature and health of the chosen disk (visible before the scan too) ──

        /// <summary>The temperature from the SMART attributes (0xC2, then 0xBE — "Airflow", the only one reported by some disks,
        /// e.g. NVMe through RAID); −1 = none. Reads only the attribute list, so it works from any thread.</summary>
        private static int SmartTempC(DiskHealthEntry d)
        {
            var attrs = d.SmartAttributes;
            var a = attrs.FirstOrDefault(x => x.Id == 0xC2) ?? attrs.FirstOrDefault(x => x.Id == 0xBE);
            if (a == null) return -1;
            long t = a.RawValue & 0xFF;
            return t > 0 && t < 100 ? (int)t : -1;
        }

        /// <summary>The disk temperature: SMART, otherwise the last smartctl read (NVMe). UI thread only.</summary>
        private int DiskTempC(DiskHealthEntry d)
        {
            int t = SmartTempC(d);
            if (t < 0 && !string.IsNullOrWhiteSpace(d.PhysicalDevicePath)
                && _diskTempCache.TryGetValue(d.PhysicalDevicePath, out double cached) && cached > 0)
                t = (int)Math.Round(cached);
            return t;
        }

        private void ShowSurfaceTemp(int tempC)
        {
            bool known = tempC > 0;
            TxtSurfaceTemp.Text = known ? $"{TempUnit.Convert(tempC):F0}" : "—";
            // without a value the unit is not shown either ("— °C" looks like a broken read)
            if (FindName("TxtSurfaceTempUnit") is FrameworkElement unit) unit.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
            TxtSurfaceTemp.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                !known ? "TextSecondaryBrush" : tempC >= 55 ? "StatusCritBrush" : tempC >= 45 ? "StatusWarnBrush" : "StatusOkBrush");
        }

        /// <summary>The SMART temperature and health of the disk chosen in the list — shown also when no scan is running.</summary>
        private void UpdateSurfaceDriveFacts()
        {
            try
            {
                int idx = CbSurfaceDisk.SelectedIndex;
                if (idx < 0 || idx >= _allDisks.Count) { ShowSurfaceTemp(-1); TxtSurfaceHealth.Text = "—"; return; }
                var d = _allDisks[idx];
                ShowSurfaceTemp(DiskTempC(d));
                TxtSurfaceHealth.Text = DriveVerdict.Of(d).PercentText;
            }
            catch (Exception ex) { AppLogger.Debug($"UpdateSurfaceDriveFacts: {ex.Message}"); }
        }

        private bool _surfaceDiskHooked;

        private bool _surfaceComboStale;

        private void PopulateSurfaceDiskCombo()
        {
            // during a scan the list stays as it is: a USB disk plugged in or pulled out refreshes the disk list, and the selection
            // used to jump to the first disk (the SSD) — the list is refreshed when the scan ends
            if (_surfaceScanCts != null) { _surfaceComboStale = true; return; }
            string? prev = CbSurfaceDisk.SelectedItem as string;
            CbSurfaceDisk.Items.Clear();
            foreach (var d in _allDisks)
                CbSurfaceDisk.Items.Add(DiskComboName(d));
            if (CbSurfaceDisk.Items.Count > 0) RestoreComboSelection(CbSurfaceDisk, prev);
            if (!_surfaceDiskHooked)
            {
                _surfaceDiskHooked = true;
                // another disk chosen (while no scan runs) → its temperature and health
                CbSurfaceDisk.SelectionChanged += (_, _) => { if (_surfaceScanCts == null) UpdateSurfaceDriveFacts(); };
            }
            UpdateSurfaceDriveFacts();
        }

        internal async void RefreshDiskCombos_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _hwService.InvalidateDiskCache();
                SMDWin.Services.SmartctlService.ForgetDeviceStates();
                var fresh = await _hwService.GetDisksAsync();
                if (fresh != null) _allDisks = fresh;

                string? prevSurface = CbSurfaceDisk?.SelectedItem as string;
                string? prevSmart   = CbSmartDisk?.SelectedItem as string;

                if (CbSurfaceDisk != null) { PopulateSurfaceDiskCombo(); RestoreComboSelection(CbSurfaceDisk, prevSurface); }
                if (CbSmartDisk   != null) { PopulateSmartDiskCombo();   RestoreComboSelection(CbSmartDisk, prevSmart); }
                if (CbBenchDrive  != null) { PopulateBenchDriveCombo(); } // keeps the previous selection by itself

                ShowToast(LanguageService.S("Msg", "DriveListRefreshed"), "🔄", 2000);
            }
            catch (Exception ex) { AppLogger.Warning(ex, "RefreshDiskCombos_Click"); }
        }

        private static void RestoreComboSelection(System.Windows.Controls.Primitives.Selector combo, string? prevText)
        {
            if (prevText != null)
            {
                for (int i = 0; i < combo.Items.Count; i++)
                    if (Equals(combo.Items[i], prevText)) { combo.SelectedIndex = i; return; }
            }
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }

        internal async void SurfaceScan_Start_Click(object sender, RoutedEventArgs e)
        {
            // Toggle: if scanning, cancel it
            if (_surfaceScanCts != null && !_surfaceScanCts.IsCancellationRequested)
            {
                _surfaceScanCts.Cancel();
                return;
            }

            int idx = CbSurfaceDisk.SelectedIndex;
            if (idx < 0 || idx >= _allDisks.Count) return;
            var disk = _allDisks[idx];

            string devPath = disk.PhysicalDevicePath;
            if (string.IsNullOrEmpty(devPath))
                devPath = $"\\\\.\\PhysicalDrive{disk.DiskIndex}";

            _surfaceScanCts?.Dispose();
            _surfaceScanCts = new CancellationTokenSource();
            _surfaceDisconnected = false;
            CbSurfaceDisk.IsEnabled = false;   // the scanned disk cannot change in the middle of the scan
            SetNavTaskBadge("TaskBadgeDisk", true);

            BtnSurfaceStart.IsEnabled  = true;
            if (TxtSurfaceStartLabel != null) TxtSurfaceStartLabel.Text = MainWindow.PT("Stop Scan");
            // The main action is coloured (accent) while it waits to start; while running
            // the scan button becomes "Stop Scan", red.
            SetStopLook(BtnSurfaceStart, running: true);
            _sectorMapSeverity = Array.Empty<byte>();
            SurfaceBlockMap.Reset();
            UpdateSurfaceEmptyState();
            // The charts: histogram, speed across the surface, the list of problems
            for (int i = 0; i < _surfaceHist.Length; i++) _surfaceHist[i] = 0;
            lock (_surfaceDataLock)
            {
                Array.Clear(_surfaceBinSum);
                Array.Clear(_surfaceBinCount);
                _surfaceProblems.Clear();
                _surfaceProblemsOverflow = 0;
            }
            _surfaceProblemsRendered = -1;
            _surfaceProblemsRenderedEnd = -1;
            SurfaceSpeedChart.Reset();
            _surfaceChartT0 = DateTime.Now;
            UpdateSurfaceHistogram(new long[6], false);
            RenderSurfaceProblems(new List<SurfaceProblem>(), 0);
            TxtSurfaceRepairInfo.Visibility = Visibility.Collapsed;
            _badSectorRanges.Clear();
            _badSectorLbasRaw.Clear();
            _badSectorMapRedrawSw = System.Diagnostics.Stopwatch.StartNew();
            SurfaceRepairPanel.Visibility = Visibility.Collapsed;
            _surfaceSpeedMin      = double.MaxValue;
            _surfaceSpeedMax      = 0;
            _surfaceSlowOrange    = 0;
            _surfaceSlowYellow    = 0;
            _surfaceSpeedHistory.Clear();
            SurfaceSpeedGraph.Children.Clear();
            TxtSurfaceBad.Text        = "0";
            TxtSurfaceSlow.Text       = "0";
            TxtSlowOrange.Text        = "0";
            TxtSlowYellow.Text        = "0";
            TxtSurfaceSpeed.Text      = "—";
            TxtSurfaceSpeedMin.Text   = "—";
            TxtSurfaceSpeedMax.Text   = "—";
            TxtSurfaceSpeedAvg.Text   = "—";
            TxtSurfaceEta.Text        = "—";
            TxtSurfaceElapsed.Text    = "—";
            TxtSurfaceLba.Text        = "—";
            if (TxtSurfaceScanned != null) TxtSurfaceScanned.Text = "— GB / — GB";
            UpdateSurfaceDriveFacts();
            TxtSurfacePctLabel.Text   = "0%";
            SurfaceProgressFill.Width = 0;
            SurfaceProgressFill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            TxtSurfacePhase.Text      = PT("Scanning…");
            TxtSurfaceEta.Text        = "calculating…";
            SetSurfaceVerdict(0, 0, SurfaceScanState.Running);

            // ── Sector Remap info from SMART ────────────────────────────────────
            // Show how many sectors were already remapped by the disk before the scan.
            // If there are remapped sectors, those are areas that gave problems in the past
            var reallocAttr = disk.SmartAttributes.FirstOrDefault(a => a.Id == 0x05);
            var pendingAttr = disk.SmartAttributes.FirstOrDefault(a => a.Id == 0xC5 || a.Id == 197);
            long reallocCount = reallocAttr?.RawValue ?? 0;
            long pendingCount = pendingAttr?.RawValue ?? 0;

            if (reallocCount > 0 || pendingCount > 0)
            {
                string remapMsg = "";
                if (reallocCount > 0)
                    remapMsg += $"⚠ {reallocCount} sector(e) au fost remapate anterior (SMART 0x05 — Reallocated Sectors). ";
                if (pendingCount > 0)
                    remapMsg += $"⚠ {pendingCount} sector(s) pending reallocation (SMART 0xC5).";

                if (TxtSurfaceStatus != null)
                    TxtSurfaceStatus.Text = remapMsg.Trim();
            }

            try
            {
                await Task.Run(() => RunSurfaceScan(devPath, disk.SizeBytes, disk.MediaType, disk, _surfaceScanCts.Token), _surfaceScanCts.Token);
            }
            catch (OperationCanceledException) { if (TxtSurfaceStatus != null) TxtSurfaceStatus.Text = LanguageService.S("Disk", "ScanStoppedByUser"); }
            catch (UnauthorizedAccessException ex)
            {
                AppDialog.Show(string.Format(LanguageService.S("Msg", "ScanAccessDeniedFmt"), ex.Message), LanguageService.S("Msg", "ScanErrorTitle"), AppDialog.Kind.Warning);
            }
            catch (Exception ex)
            {
                AppDialog.Show(string.Format(LanguageService.S("Msg", "ScanErrorFmt"), ex.Message), LanguageService.S("Msg", "ScanErrorTitle"), AppDialog.Kind.Error);
            }
            finally
            {
                BtnSurfaceStart.IsEnabled  = true;
                if (TxtSurfaceStartLabel != null) TxtSurfaceStartLabel.Text = PT("Start Scan");
                SetStopLook(BtnSurfaceStart, running: false);
                SetNavTaskBadge("TaskBadgeDisk", false);
                // scan finished: without this, the next click on "Start Scan" would be taken as "Stop" (it would cancel the already
                // finished scan) and only the second click would start a new one
                _surfaceScanCts?.Dispose();
                _surfaceScanCts = null;
                CbSurfaceDisk.IsEnabled = true;
                if (_surfaceComboStale) { _surfaceComboStale = false; PopulateSurfaceDiskCombo(); }
            }
        }

        private enum SurfaceScanState { Running, Stopped, Complete }

        private volatile bool _surfaceDisconnected;

        /// <summary>Win32 errors that mean "the device is not there any more" (unplugged, powered off, USB reset).</summary>
        private static bool IsDeviceGoneError(int err) =>
            err is 1167   // ERROR_DEVICE_NOT_CONNECTED
                or 433    // ERROR_NO_SUCH_DEVICE
                or 55     // ERROR_DEV_NOT_EXIST
                or 21     // ERROR_NOT_READY
                or 6      // ERROR_INVALID_HANDLE
                or 1617;  // ERROR_DEVICE_REMOVED

        /// <summary>Opens the disk again without reading: false when Windows no longer has it.</summary>
        private static bool SurfaceDeviceStillThere(string devicePath)
        {
            var h = NativeMethods.CreateFile(devicePath, 0, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                                             IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == NativeMethods.INVALID_HANDLE_VALUE) return false;
            NativeMethods.CloseHandle(h);
            return true;
        }

        /// <summary>The width + colour of the progress strip (accent; red if bad sectors were found) and the large percentage
        /// next to it. A single place, used both during the scan and at the end.</summary>
        private void SetSurfaceProgress(double pct, bool hasBad)
        {
            pct = Math.Clamp(pct, 0, 100);
            if (SurfaceProgressFill.Parent is Border track)
            {
                double trackW = track.ActualWidth > 0 ? track.ActualWidth : 200;
                SurfaceProgressFill.Width = pct <= 0 ? 0 : Math.Max(6, trackW * pct / 100.0);
            }
            if (hasBad) SurfaceProgressFill.Background = new SolidColorBrush(WpfColor.FromRgb(239, 68, 68));
            else        SurfaceProgressFill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            TxtSurfacePctLabel.Text = pct >= 99.95 ? "100%" : $"{pct:F1}%";
        }

        /// <summary>The short answer in the right column ("does the disk have problems?"), coloured.</summary>
        private void SetSurfaceVerdict(long bad, long slow, SurfaceScanState state)
        {
            string text;
            string brushKey;
            if (bad > 0)
            {
                text = state == SurfaceScanState.Running
                    ? $"{bad:N0} bad sector{(bad == 1 ? "" : "s")} found so far"
                    : $"{bad:N0} bad sector{(bad == 1 ? "" : "s")} found";
                brushKey = "StatusErrorBrush";
            }
            else if (slow > 0)
            {
                text = state == SurfaceScanState.Running
                    ? $"No bad sectors so far · {slow:N0} slow"
                    : $"No bad sectors · {slow:N0} slow";
                brushKey = "StatusWarningBrush";
            }
            else
            {
                text = state switch
                {
                    SurfaceScanState.Running  => "No problems so far",
                    SurfaceScanState.Stopped  => "No problems in the scanned area",
                    _                         => "No problems found",
                };
                brushKey = "StatusSuccessBrush";
            }
            TxtSurfaceVerdict.Text = text;
            TxtSurfaceVerdict.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            // Coloured label: the same tone as the text, very transparent
            if (TryFindResource(brushKey) is SolidColorBrush vb)
                SurfaceVerdictPill.Background = System.Windows.Media.Brushes.Transparent; // no pill
            SurfaceVerdictPill.Padding = new Thickness(0);
            TxtSurfaceBad.SetResourceReference(TextBlock.ForegroundProperty, bad > 0 ? "StatusErrorBrush" : "TextPrimaryBrush");
            TxtSurfaceSlow.SetResourceReference(TextBlock.ForegroundProperty, slow > 0 ? "StatusWarningBrush" : "TextPrimaryBrush");
        }

        /// <summary>A readable duration: "1h 05m", "5m 41s", "42s". Minutes are truncated, not rounded (5.7 min → "5m 41s").</summary>
        private static string FormatScanDuration(double seconds)
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m";
            if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m {ts.Seconds:D2}s";
            return $"{ts.Seconds}s";
        }


        private void RunSurfaceScan(string devicePath, ulong knownSizeBytes, string mediaType, DiskHealthEntry disk, CancellationToken ct)
        {
            var scanThread = System.Threading.Thread.CurrentThread;
            var originalPriority = scanThread.Priority;
            scanThread.Priority = System.Threading.ThreadPriority.AboveNormal;
            try
            {
            RunSurfaceScanCore(devicePath, knownSizeBytes, mediaType, disk, ct);
            }
            finally
            {
                scanThread.Priority = originalPriority;
            }
        }

        private void RunSurfaceScanCore(string devicePath, ulong knownSizeBytes, string mediaType, DiskHealthEntry disk, CancellationToken ct)
        {
            // Open the disk with NO_BUFFERING to bypass the cache.
            // FILE_FLAG_SEQUENTIAL_SCAN: a hint for hardware prefetch (faster than RANDOM_ACCESS)
            var handle = NativeMethods.CreateFile(devicePath,
                NativeMethods.GENERIC_READ,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero, NativeMethods.OPEN_EXISTING,
                NativeMethods.FILE_FLAG_NO_BUFFERING | NativeMethods.FILE_FLAG_SEQUENTIAL_SCAN,
                IntPtr.Zero);

            if (handle == NativeMethods.INVALID_HANDLE_VALUE)
                throw new UnauthorizedAccessException(
                    $"Nu se poate deschide {devicePath}.\nCod eroare Win32: {Marshal.GetLastWin32Error()}");

            Dispatcher.Invoke(() => TxtSurfaceStatus.Text = LanguageService.S("Common", "Scanning"));

            var startTime = DateTime.Now;

            // Get the real sector size (512 or 4096 for Advanced Format)
            long totalSectors;
            int  bytesPerSector;
            GetDiskGeometry(handle, knownSizeBytes, out totalSectors, out bytesPerSector);
            Dispatcher.Invoke(() => InitSectorMap(totalSectors, bytesPerSector));

            // The size of the block read at once: 1 MB on HDD / USB (like HD Tune), 4 MB on SSD/NVMe — on an SSD, with a single
            // pending read, 1 MB blocks leave the disk idle for a long time between reads and the measured speed comes out below the
            // real one.
            bool isSolidState = mediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase)
                             || mediaType.Contains("NVMe", StringComparison.OrdinalIgnoreCase);
            int targetBlockBytes = isSolidState ? 4 * 1048576 : 1048576;
            int BLOCK_SECTORS = Math.Max(1, targetBlockBytes / Math.Max(1, bytesPerSector));
            int blockBytes = BLOCK_SECTORS * bytesPerSector;

            long bad = 0, slow = 0, processed = 0, nonMediaIoErrors = 0;

            bool looksLikeSpinningHdd = mediaType.Contains("HDD", StringComparison.OrdinalIgnoreCase);
            double speedOutlierCeilingMBps = looksLikeSpinningHdd ? 250.0 : double.MaxValue;

            // Buffer manually aligned to bytesPerSector (required for NO_BUFFERING)
            IntPtr rawBuf     = Marshal.AllocHGlobal(blockBytes + bytesPerSector);
            long   rawAddr    = rawBuf.ToInt64();
            long   alignedAddr = ((rawAddr + bytesPerSector - 1) / bytesPerSector) * bytesPerSector;
            IntPtr buffer     = new IntPtr(alignedAddr);

            var sw       = new System.Diagnostics.Stopwatch();
            var swReport = System.Diagnostics.Stopwatch.StartNew();
            bool speedLogged = false;

            // Map colour batch — sent to the UI once per second, not per block
            var colorBatch = new System.Collections.Generic.List<(byte severity, long lba, double ms)>(128);

            bool isRealMediaError(int e) => e == 23 || e == 27 || e == 30 || e == 31 || e == 1117;
            // the codes of "non-media" errors, for the log: on a failing disk they may still be real problems
            var nonMediaCodes = new System.Collections.Generic.Dictionary<int, long>();
            long processedReal = 0, processedRealAtLastUi = 0;   // only the blocks actually read (without the skipped ones) — for the speed

            int lastErr = 0, consecutiveSkipped = 0;
            bool disconnected = false;
            bool TryRead(IntPtr buf, uint bytes, out uint bytesReadOut, out bool wasNonMediaError)
            {
                // the disk is gone: no retries, no waiting — the loop stops right after
                if (disconnected) { bytesReadOut = bytes; wasNonMediaError = true; return true; }
                bool okLocal = NativeMethods.ReadFile(handle, buf, bytes, out bytesReadOut, IntPtr.Zero);
                int  err     = okLocal ? 0 : Marshal.GetLastWin32Error();
                if (!okLocal && IsDeviceGoneError(err)) { lastErr = err; disconnected = true; wasNonMediaError = true; bytesReadOut = bytes; return true; }
                wasNonMediaError = false;

                int extraAttempts = looksLikeSpinningHdd ? 2 : 1; // total retries, IN ADDITION to the first read
                while (!okLocal && extraAttempts > 0)
                {
                    extraAttempts--;
                    if (looksLikeSpinningHdd) System.Threading.Thread.Sleep(150);
                    okLocal = NativeMethods.ReadFile(handle, buf, bytes, out bytesReadOut, IntPtr.Zero);
                    err     = okLocal ? 0 : Marshal.GetLastWin32Error();
                }

                lastErr = okLocal ? 0 : err;
                if (!okLocal && !isRealMediaError(err))
                {
                    nonMediaCodes[err] = nonMediaCodes.TryGetValue(err, out long c0) ? c0 + 1 : 1;
                    // Still a non-media code after all retries — probably a driver/alignment quirk at this offset, not a physically bad
                    // sector. It is NOT painted red; the block is treated as "unknown" and the scan continues without inflating the bad
                    // sector counter.
                    wasNonMediaError = true;
                    okLocal      = true;
                    bytesReadOut = bytes;
                }
                return okLocal;
            }

            try
            {
                long sector = 0;

                // SetFilePointerEx ONLY ONCE, at offset 0 — read strictly sequentially.
                // Every extra seek resets the disk driver's prefetch
                NativeMethods.SetFilePointerEx(handle, 0, out _, 0);

                while (sector < totalSectors && !ct.IsCancellationRequested)
                {
                    int toRead      = (int)Math.Min(BLOCK_SECTORS, totalSectors - sector);
                    int toReadBytes = toRead * bytesPerSector;

                    sw.Restart();
                    bool ok = TryRead(buffer, (uint)toReadBytes, out uint bytesRead, out bool wasNonMedia);
                    sw.Stop();

                    if (wasNonMedia)
                    {
                        // a disk that was pulled out (or lost power) answers every read with an error at once: the scan used to "skip"
                        // the rest of the disk in seconds and report it clean. Many skipped blocks in a row → is the disk still there?
                        consecutiveSkipped++;
                        if (!disconnected && consecutiveSkipped >= 24 && !SurfaceDeviceStillThere(devicePath)) disconnected = true;
                        if (disconnected)
                        {
                            AppLogger.Warning($"Surface Scan: the drive stopped answering at LBA {sector} (Win32 {lastErr}) — disconnected or lost power; scan stopped");
                            break;
                        }
                        Interlocked.Increment(ref nonMediaIoErrors);
                        if (nonMediaIoErrors <= 20 || nonMediaIoErrors % 500 == 0)
                            AppLogger.Warning($"Surface Scan: non-media I/O error at LBA {sector} — skipped, NOT counted as a bad sector");
                    }
                    else consecutiveSkipped = 0;

                    double ms = sw.Elapsed.TotalMilliseconds;
                    byte blockSeverity;
                    bool isBadSector = false;
                    long blockStart = sector;
                    double blockMsPerMB = ms / Math.Max(toReadBytes / 1048576.0, 0.001);

                    if (!ok || bytesRead == 0)
                    {
                        const int subChunkSectors = 64;
                        int subFailCount = 0;
                        for (int off = 0; off < toRead; off += subChunkSectors)
                        {
                            int  subCount    = Math.Min(subChunkSectors, toRead - off);
                            int  subBytes    = subCount * bytesPerSector;
                            long subSector   = sector + off;
                            NativeMethods.SetFilePointerEx(handle, subSector * bytesPerSector, out _, 0);
                            bool subOk = TryRead(buffer, (uint)subBytes, out uint subBytesRead, out bool subWasNonMedia);
                            if (subWasNonMedia)
                            {
                                Interlocked.Increment(ref nonMediaIoErrors);
                                if (nonMediaIoErrors <= 20 || nonMediaIoErrors % 500 == 0)
                                    AppLogger.Warning($"Surface Scan: non-media I/O error at LBA {subSector} — skipped, NOT counted as a bad sector");
                            }

                            if (!subOk || subBytesRead == 0)
                            {
                                subFailCount++;
                                Interlocked.Increment(ref bad);
                                MarkSectorCell(subSector, 5);
                                AddSurfaceProblem(subSector, subSector + subCount - 1, 0, bad: true);
                                long lbaCapture = subSector;
                                Dispatcher.InvokeAsync(() =>
                                {
                                    _badSectorLbasRaw.Add(lbaCapture);
                                    if (_badSectorRanges.Count > 0 && _badSectorRanges[^1].end == lbaCapture - 1)
                                        _badSectorRanges[^1] = (_badSectorRanges[^1].start, lbaCapture);
                                    else
                                        _badSectorRanges.Add((lbaCapture, lbaCapture));
                                    if (SurfaceRepairPanel != null) SurfaceRepairPanel.Visibility = Visibility.Visible;
                                    SurfaceProblemsCard.Visibility = Visibility.Visible;
                                    if (_badSectorMapRedrawSw == null || _badSectorMapRedrawSw.ElapsedMilliseconds > 250)
                                    {
                                        FillSectorMapBitmap();
                                        _badSectorMapRedrawSw = System.Diagnostics.Stopwatch.StartNew();
                                    }
                                });
                            }
                            else
                            {
                                MarkSectorCell(subSector, 1); // this small piece really is OK
                            }
                        }

                        isBadSector    = subFailCount > 0;
                        blockSeverity  = isBadSector ? (byte)5 : (byte)1;

                        sector += toRead;
                        NativeMethods.SetFilePointerEx(handle, sector * bytesPerSector, out _, 0);
                    }
                    else
                    {
                        // Normalize the time to ms/MB for consistent thresholds whatever the block size
                        double msPerMB = ms / Math.Max(toReadBytes / 1048576.0, 0.001);
                        // a slow block is read once more: a momentary pause (Windows, another program reading the disk, a SMART query) is not a
                        // weak area of the disk. The second read counts — a weak area stays slow.
                        if (msPerMB > 120 && !wasNonMedia)
                        {
                            NativeMethods.SetFilePointerEx(handle, blockStart * bytesPerSector, out _, 0);
                            sw.Restart();
                            bool again = TryRead(buffer, (uint)toReadBytes, out uint againRead, out bool againNonMedia);
                            sw.Stop();
                            if (again && againRead > 0 && !againNonMedia)
                            {
                                double msAgain = sw.Elapsed.TotalMilliseconds;
                                if (msAgain < ms) { ms = msAgain; blockMsPerMB = msAgain / Math.Max(toReadBytes / 1048576.0, 0.001); msPerMB = blockMsPerMB; }
                            }
                            else
                                NativeMethods.SetFilePointerEx(handle, (blockStart + toRead) * bytesPerSector, out _, 0);
                        }
                        if      (msPerMB > 500) { blockSeverity = 4; Interlocked.Increment(ref slow); Interlocked.Increment(ref _surfaceSlowOrange); }
                        else if (msPerMB > 150) { blockSeverity = 3; Interlocked.Increment(ref slow); Interlocked.Increment(ref _surfaceSlowOrange); }
                        else if (msPerMB > 120) { blockSeverity = 2; Interlocked.Increment(ref slow); Interlocked.Increment(ref _surfaceSlowYellow); }
                        else                    { blockSeverity = 1; }

                        sector += toRead;
                    }

                    processed += toRead;
                    if (!isBadSector) // bad sectors are drawn immediately, not through the batch
                        colorBatch.Add((blockSeverity, blockStart, ms));

                    // Response time histogram (per block)
                    int histIdx = isBadSector          ? 5
                                : blockMsPerMB > 500   ? 4
                                : blockMsPerMB > 120   ? 3
                                : blockMsPerMB > 30    ? 2
                                : blockMsPerMB > 10    ? 1 : 0;
                    Interlocked.Increment(ref _surfaceHist[histIdx]);
                    if (!isBadSector && blockSeverity >= 2)
                        AddSurfaceProblem(blockStart, blockStart + toRead - 1, blockMsPerMB, bad: false);

                    double blockMBps = toReadBytes / 1048576.0 / Math.Max(ms / 1000.0, 0.0001);
                    // a "skipped" block (non-media error) was not read: its time says nothing about the disk speed
                    // (otherwise impossible speeds appear, e.g. 20000 MB/s on a SATA SSD)
                    if (!wasNonMedia && !isBadSector) processedReal += toRead;
                    if (!isBadSector && !wasNonMedia && blockMBps > 1 && blockMBps <= speedOutlierCeilingMBps) // ignore outliers (start + disk cache hits)
                    {
                        if (blockMBps < _surfaceSpeedMin) _surfaceSpeedMin = blockMBps;
                        if (blockMBps > _surfaceSpeedMax) _surfaceSpeedMax = blockMBps;
                        lock (_surfaceSpeedHistory) _surfaceSpeedHistory.Add(blockMBps);
                        int bin = (int)Math.Clamp((double)blockStart / Math.Max(1, totalSectors) * SpeedCurveChart.Bins, 0, SpeedCurveChart.Bins - 1);
                        lock (_surfaceDataLock) { _surfaceBinSum[bin] += blockMBps; _surfaceBinCount[bin]++; }
                    }

                    // UI update: once per second (not per block)
                    if (swReport.ElapsedMilliseconds >= 1000)
                    {
                        double intervalSec = swReport.Elapsed.TotalSeconds;
                        swReport.Restart();
                        // The "now" speed = what was read since the last update.
                        double speedNow = (processedReal - processedRealAtLastUi) * (double)bytesPerSector / 1048576.0 / Math.Max(intervalSec, 0.001);
                        processedRealAtLastUi = processedReal;

                        double pct        = totalSectors > 0 ? processed * 100.0 / totalSectors : 0;
                        double elapsedSec = (DateTime.Now - startTime).TotalSeconds;
                        double speed      = elapsedSec > 0 ? processed * (double)bytesPerSector / 1048576.0 / elapsedSec : 0;
                        long   badSnap    = Interlocked.Read(ref bad);
                        long   slowSnap   = Interlocked.Read(ref slow);
                        long   secSnap    = sector;
                        double minSnap    = _surfaceSpeedMin < double.MaxValue ? _surfaceSpeedMin : 0;
                        double maxSnap    = _surfaceSpeedMax;
                        double avgSnap;
                        lock (_surfaceSpeedHistory)
                            avgSnap = _surfaceSpeedHistory.Count > 0
                                ? _surfaceSpeedHistory.Average() : 0;
                        // ETA from the real throughput since start (the arithmetic mean of the per-block speeds overestimated the speed and
                        // underestimated the remaining time).
                        double etaSpeed   = speed > 0 ? speed : avgSnap;
                        // a line in the log after 30 s, so the scan speed can be compared with the benchmark of the same disk
                        if (!speedLogged && elapsedSec >= 30)
                        {
                            speedLogged = true;
                            AppLogger.Info($"Surface Scan: {mediaType}, block {blockBytes / 1048576} MB, {speed:F0} MB/s average after {elapsedSec:F0} s (min {minSnap:F0}, max {maxSnap:F0})");
                        }
                        double eta        = etaSpeed > 0 ? (totalSectors - processed) * (double)bytesPerSector / 1048576.0 / etaSpeed : 0;

                        // The temperature and health of the scanned disk (not of the one selected now in the list — it may change during the
                        // scan); no synchronous call to the UI thread in the read loop
                        int tempC      = SmartTempC(disk);
                        string healthStr = DriveVerdict.Of(disk).PercentText;

                        // Elapsed formatat
                        var elapsedTs = TimeSpan.FromSeconds(elapsedSec);
                        string elapsedStr = elapsedSec >= 3600
                            ? $"{(int)elapsedTs.TotalHours}h {elapsedTs.Minutes:D2}m"
                            : $"{(int)elapsedTs.TotalMinutes}m {elapsedTs.Seconds:D2}s";

                        // Copy the batch and the chart snapshot
                        var batch       = colorBatch.ToList();
                        colorBatch.Clear();
                        double speedForGraph = speed;
                        var applyVisuals = SnapshotSurfaceVisuals(processed, totalSectors, secSnap, final: false);

                        Dispatcher.InvokeAsync(() =>
                        {
                            if (SurfaceProgressBar != null) SurfaceProgressBar.Value = pct;
                            if (TxtSurfacePct != null) TxtSurfacePct.Text = $"{pct:F1}%";
                            SetTaskbarProgress(pct / 100.0, error: badSnap > 0);   // the real % on the taskbar button (red if bad sectors)
                            // The visual progress bar — grows in proportion to the real %
                            SetSurfaceProgress(pct, badSnap > 0);
                            TxtSurfacePhase.Text = PT("Scanning…");
                            SetSurfaceVerdict(badSnap, slowSnap, SurfaceScanState.Running);
                            // TxtSurfaceStatus does not repeat the percentage/LBA/bad/slow every second — those have their fixed place on screen;
                            // the status message stays the one set at start (e.g. the SMART warning about already remapped sectors).
                            TxtSurfaceBad.Text          = badSnap.ToString();
                            TxtSurfaceSlow.Text         = slowSnap.ToString();
                            TxtSlowOrange.Text          = Interlocked.Read(ref _surfaceSlowOrange).ToString();
                            TxtSlowYellow.Text          = Interlocked.Read(ref _surfaceSlowYellow).ToString();
                            TxtSurfaceSpeed.Text        = $"{speedNow:F0}";
                            TxtSurfaceSpeedMin.Text     = minSnap > 0 ? $"{minSnap:F0}" : "—";
                            TxtSurfaceSpeedMax.Text     = maxSnap > 0 ? $"{maxSnap:F0}" : "—";
                            TxtSurfaceSpeedAvg.Text     = speed > 0 ? $"{speed:F0}" : "—";
                            TxtSurfaceLba.Text          = $"{secSnap:N0}";
                            // In the first seconds the average speed is not stable yet — the ETA would jump around.
                            TxtSurfaceEta.Text          = elapsedSec < 5 || eta <= 0 ? "calculating…" : FormatScanDuration(eta);
                            TxtSurfaceElapsed.Text      = elapsedStr;
                            // NVMe has no SMART attribute 0xC2 — use the last cached smartctl read (the same source as the card on Overview). The
                            // cache is read here, on the UI thread, not on the scan thread (Dictionary is not thread-safe).
                            if (tempC < 0)
                                tempC = DiskTempC(disk);
                            ShowSurfaceTemp(tempC);
                            TxtSurfaceHealth.Text       = healthStr;

                            // Scanned GB / Total GB
                            if (TxtSurfaceScanned != null)
                            {
                                double scannedGB = processed * bytesPerSector / 1_073_741_824.0;
                                double totalGB   = totalSectors * bytesPerSector / 1_073_741_824.0;
                                TxtSurfaceScanned.Text = $"{scannedGB:F1} GB of {totalGB:F0} GB scanned";
                            }

                            // ── Transfer rate chart (sparkline on a Canvas) ─────────────────
                            UpdateSurfaceSpeedGraph(speedForGraph);

                            // ── Sector map: mark the affected cells and render only once ──
                            foreach (var (sev, lba, _) in batch)
                                MarkSectorCell(lba, sev);
                            applyVisuals();
                            FillSectorMapBitmap();
                        });
                    }
                }

                // Flush batch final
                if (colorBatch.Count > 0)
                {
                    var lastBatch = colorBatch.ToList();
                    Dispatcher.InvokeAsync(() =>
                    {
                        foreach (var (sev, lba, _) in lastBatch)
                            MarkSectorCell(lba, sev);
                        FillSectorMapBitmap();
                    });
                }
            }
            finally
            {
                Marshal.FreeHGlobal(rawBuf);
                NativeMethods.CloseHandle(handle);
            }

            long finalBad = Interlocked.Read(ref bad);
            long finalSlow = Interlocked.Read(ref slow);
            long finalNonMediaErrors = Interlocked.Read(ref nonMediaIoErrors);
            if (nonMediaCodes.Count > 0)
                AppLogger.Warning($"Surface Scan: non-media errors by Win32 code — {string.Join(", ", nonMediaCodes.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}×{kv.Value}"))}");
            var applyFinalVisuals = SnapshotSurfaceVisuals(processed, totalSectors, processed, final: true);
            _surfaceDisconnected = disconnected;
            Dispatcher.InvokeAsync(() =>
            {
                applyFinalVisuals();
                FillSectorMapBitmap();
                if (SurfaceProgressBar != null) SurfaceProgressBar.Value = 100;
                if (TxtSurfacePct != null) TxtSurfacePct.Text = "100%";
                TxtSurfaceBad.Text  = finalBad.ToString();
                TxtSurfaceSlow.Text = finalSlow.ToString();
                bool stopped = ct.IsCancellationRequested || disconnected;
                double finalPct = totalSectors > 0 ? Math.Min(100.0, processed * 100.0 / totalSectors) : 0;
                SetSurfaceProgress(stopped ? finalPct : 100.0, finalBad > 0);
                SetTaskbarProgress(null);   // back to the moving band while something else runs, otherwise nothing
                TxtSurfacePhase.Text = disconnected ? PT("Drive disconnected") : stopped ? PT("Stopped") : PT("Complete");
                if (!stopped) Fx.Done(this, finalBad > 0 ? PT("Surface scan finished — {0} unreadable blocks", finalBad) : PT("Surface scan finished — no unreadable blocks"), problems: finalBad > 0);
                TxtSurfaceEta.Text   = stopped ? $"stopped at {finalPct:F0}%" : "done";   // stopped: how much was scanned, not an empty box
                SetSurfaceVerdict(finalBad, finalSlow, stopped ? SurfaceScanState.Stopped : SurfaceScanState.Complete);
                if (TxtSurfaceScanned != null)
                {
                    double scannedGB = processed * (double)bytesPerSector / 1_073_741_824.0;
                    double totalGB   = totalSectors * (double)bytesPerSector / 1_073_741_824.0;
                    TxtSurfaceScanned.Text = $"{scannedGB:F1} GB of {totalGB:F0} GB scanned";
                }
                string nonMediaNote = finalNonMediaErrors > 0
                    ? $" ({finalNonMediaErrors} non-media I/O anomal{(finalNonMediaErrors == 1 ? "y" : "ies")} skipped, not counted as bad)"
                    : "";
                TxtSurfaceStatus.Text = disconnected
                    ? PT("✕ The drive stopped answering at {0} GB (unplugged or lost power) — the scan was stopped. Only the part read before is shown.",
                         (processed * (double)bytesPerSector / 1_073_741_824.0).ToString("F1"))
                    : ct.IsCancellationRequested
                    ? $"Stopped at {processed:N0} sectors. Bad: {finalBad}, Slow: {finalSlow}{nonMediaNote}"
                    : finalBad == 0
                        ? $"✓ Scan complete — no bad sectors found! ({finalSlow} slow){nonMediaNote}"
                        : $"⚠ Scan complete — {finalBad} bad sector(s), {finalSlow} slow{nonMediaNote}";

                if (!stopped && disk != null)
                {
                    // 1) Reconciliation with SMART — an automatic cross-check between the scan and the SMART attributes
                    // (catches false positives).
                    var reallocAttr = disk.SmartAttributes.FirstOrDefault(a => a.Id == 0x05);
                    var pendingAttr = disk.SmartAttributes.FirstOrDefault(a => a.Id == 0xC5 || a.Id == 197);
                    long reallocRaw = reallocAttr?.RawValue ?? 0;
                    long pendingRaw = pendingAttr?.RawValue ?? 0;

                    if (finalBad == 0 && (reallocRaw > 0 || pendingRaw > 0))
                    {
                        ShowToastWarning(
                            $"Surface Scan found no bad sectors, but SMART reports {reallocRaw} reallocated / {pendingRaw} pending sector(s) — likely already remapped by the drive's firmware, or in an area not rewritten yet. Doesn't mean the drive is fully clear.", 5000);
                    }
                    else if (finalBad > 0 && reallocRaw == 0 && pendingRaw == 0)
                    {
                        ShowToastWarning(
                            $"Surface Scan found {finalBad} bad sector(s), but SMART doesn't show them yet as reallocated/pending — firmware sometimes only updates those counters on the next write. Treat the scan result as real.", 5000);
                    }

                    // 2) A specific note for USB disks — a bad cable/adapter can give real CRC errors (code 23) that are still not a
                    // physically bad sector.
                    if (finalBad > 0 && mediaType.Equals("USB", StringComparison.OrdinalIgnoreCase))
                    {
                        TxtSurfaceStatus.Text += "  (External/USB drive — a bad cable or adapter can also cause read errors; try a different cable/port and rescan before assuming the drive itself is failing.)";
                    }

                    // 3) History — save the result of this scan, then check whether the number of bad sectors GROWS compared with the
                    // previous scan (a much more reliable signal than a single isolated scan).
                    SmartHistoryService.SaveSurfaceScanResult(disk.SerialNumber, disk.Model, disk.HealthPercent, finalBad);
                    var updatedHistory = SmartHistoryService.LoadHistory(disk.SerialNumber);
                    if (updatedHistory != null)
                    {
                        var alert = SmartHistoryService.CheckDegradation(updatedHistory);
                        if (alert.HasAlert && alert.Message.Contains("Surface Scan bad sectors"))
                        {
                            if (alert.Severity == "critical") ShowToastError(alert.Message, 6000);
                            else ShowToastWarning(alert.Message, 6000);
                        }
                    }
                }

                // Show repair suggestion when bad sectors found
                if (SurfaceRepairPanel != null)
                {
                    SurfaceRepairPanel.Visibility = finalBad > 0
                        ? System.Windows.Visibility.Visible
                        : System.Windows.Visibility.Collapsed;
                    if (TxtSurfaceRepairInfo != null)
                        TxtSurfaceRepairInfo.Visibility = finalBad > 0 ? Visibility.Visible : Visibility.Collapsed;
                    if (finalBad > 0) SurfaceProblemsCard.Visibility = Visibility.Visible;
                    if (TxtSurfaceRepairInfo != null && finalBad > 0)
                        TxtSurfaceRepairInfo.Text =
                            $"{finalBad} bad sector(s) found. chkdsk /r will attempt to remap them " +
                            $"so Windows never writes data to them again. This requires a reboot and takes 30–90 min.";
                }
            });
        }

        internal async void SurfaceRepair_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            // Determine drive letter from selected disk
            int idx = CbSurfaceDisk.SelectedIndex;
            string driveLetter = "C:";
            if (idx >= 0 && idx < _allDisks.Count)
            {
                var disk = _allDisks[idx];
                // Try to get drive letter from partitions
                var part = disk.Partitions?.FirstOrDefault(p => !string.IsNullOrEmpty(p.Letter));
                if (part != null) driveLetter = part.Letter.TrimEnd('\\');
                else driveLetter = "C:";
            }

            bool confirmed = AppDialog.Confirm(
                $"This will schedule chkdsk /r on {driveLetter} for the next restart (the drive is not forced offline now).\n\n" +
                $"• The scan reads and rewrites every sector\n" +
                $"• Bad sectors are remapped so Windows won't use them again\n" +
                $"• Duration: 30–90 minutes depending on drive size\n" +
                $"• Your data is NOT deleted\n\n" +
                $"Schedule the repair?",
                "Schedule Disk Repair");

            if (!confirmed) return;

            bool scheduled = false;
            string error = "";
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // Schedule chkdsk /r via fsutil or reg key
                    var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                        $"/c echo y | chkdsk {driveLetter} /r")
                    {
                        UseShellExecute      = true,
                        Verb                 = "runas",   // elevate
                        CreateNoWindow       = true,
                        WindowStyle          = System.Diagnostics.ProcessWindowStyle.Hidden,
                    };
                    // without /x: the disk is NOT force-dismounted. If the volume is in use, chkdsk asks "schedule on restart?" and the "y"
                    // answer above schedules it. "Scheduled" is stated only if Windows really has the entry.
                    using var proc = System.Diagnostics.Process.Start(psi);
                    proc?.WaitForExit(8000);
                    scheduled = IsChkdskScheduled(driveLetter);
                    if (!scheduled) error = "Windows did not register the repair for the next restart (it may have been cancelled or the drive could not be locked).";
                }
                catch (Exception ex) { error = ex.Message; }
            });

            if (scheduled)
            {
                AppDialog.Show(
                    $"chkdsk /r scheduled for {driveLetter}.\n\n" +
                    $"If this is the system drive (C:), it will run automatically on the next reboot.\n" +
                    $"Reboot your computer now to start the repair.",
                    "Repair Scheduled", AppDialog.Kind.Success, this);
            }
            else
            {
                AppDialog.Show(
                    $"Could not schedule repair: {error}\n\n" +
                    $"Try running manually as Administrator:\n  chkdsk {driveLetter} /r",
                    "Repair Failed", AppDialog.Kind.Error, this);
            }
        }

        /// <summary>True if Windows has chkdsk scheduled for the drive (BootExecute contains autochk with /r and the letter).</summary>
        private static bool IsChkdskScheduled(string driveLetter)
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
                if (k?.GetValue("BootExecute") is string[] entries)
                {
                    string d = driveLetter.TrimEnd('\\', ':').ToUpperInvariant();
                    return entries.Any(e => e.Contains("autochk", StringComparison.OrdinalIgnoreCase)
                                         && e.Contains("/r", StringComparison.OrdinalIgnoreCase)
                                         && e.ToUpperInvariant().Contains(d + ":"));
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "IsChkdskScheduled"); }
            return false;
        }

        // Returns the total number of sectors and the size of a sector (512 or 4096).
        private static void GetDiskGeometry(IntPtr handle, out long totalSectors, out int bytesPerSector)
        {
            // DISK_GEOMETRY_EX layout:
            //   offset  0: Cylinders          (Int64,  8 bytes)
            //   offset  8: MediaType          (Int32,  4 bytes)
            //   offset 12: TracksPerCylinder  (UInt32, 4 bytes)
            //   offset 16: SectorsPerTrack    (UInt32, 4 bytes)
            //   offset 20: BytesPerSector     (UInt32, 4 bytes)
            //   offset 24: DiskSize           (Int64,  8 bytes)
            const int BufSize = 64;
            totalSectors   = 0;
            bytesPerSector = 512; // default

            var ptr = Marshal.AllocHGlobal(BufSize);
            try
            {
                bool ok = NativeMethods.DeviceIoControl(handle, 0x000700A0,
                    IntPtr.Zero, 0, ptr, BufSize, out uint ret, IntPtr.Zero);
                if (ok && ret >= 32)
                {
                    long diskSize  = Marshal.ReadInt64(ptr, 24); // offset 24: DiskSize
                    int  secSize   = Marshal.ReadInt32(ptr, 20); // offset 20: BytesPerSector
                    if (secSize > 0) bytesPerSector = secSize;
                    totalSectors = diskSize / bytesPerSector;
                }
            }
            finally { Marshal.FreeHGlobal(ptr); }

            if (totalSectors <= 0)
                totalSectors = 200_000_000L; // fallback ~100 GB at 512B/sector
        }

        private static void GetDiskGeometry(IntPtr handle, ulong knownSizeBytes, out long totalSectors, out int bytesPerSector)
        {
            GetDiskGeometry(handle, out totalSectors, out bytesPerSector);

            if (knownSizeBytes == 0) return; // nothing to compare with — keep what IOCTL gave

            long ioctlSizeBytes = totalSectors * bytesPerSector;
            long known = (long)knownSizeBytes;
            // The relative difference from the known value — known is the denominator (it is the more trusted source, it comes
            // from WMI when enumerating the disk).
            double diffFraction = known > 0 ? Math.Abs(ioctlSizeBytes - known) / (double)known : 0;

            if (diffFraction > 0.05)
            {
                AppLogger.Warning(
                    $"Surface Scan: IOCTL geometry ({ioctlSizeBytes:N0} bytes) does not match" +
                    $"the size known from WMI ({known:N0} bytes) — difference {diffFraction:P0}." +
                    $"Using the WMI value so only a fraction of the disk is not scanned (possible driver bug in WinPE/Hirens).");
                totalSectors = known / bytesPerSector;
            }
        }


        // ── Benchmark reference comparison ────────────────────────────────────
        // Called from the UI thread — draws a simple line chart on a Canvas
        private void UpdateSurfaceSpeedGraph(double currentMBps)
        {
            const int MAX_POINTS = 120;

            var pts = SurfaceSpeedGraph.Tag as System.Collections.Generic.List<double>
                      ?? new System.Collections.Generic.List<double>();
            pts.Add(currentMBps);
            if (pts.Count > MAX_POINTS) pts.RemoveAt(0);
            SurfaceSpeedGraph.Tag = pts;

            SurfaceSpeedGraph.Children.Clear();
            if (pts.Count < 2) return;

            double w = SurfaceSpeedGraph.ActualWidth;
            double h = SurfaceSpeedGraph.ActualHeight;
            if (w <= 0 || h <= 0) return;

            double maxV  = Math.Max(1, pts.Max());
            double minV  = Math.Max(0, pts.Min());
            double range = Math.Max(maxV - minV, 1);

            // Average reference line
            double avgV = pts.Average();
            double avgY = h - (avgV - minV) / range * (h - 16) - 8;
            SurfaceSpeedGraph.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0, X2 = w, Y1 = avgY, Y2 = avgY,
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 255, 200, 0)),
                StrokeThickness = 1,
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 4 }
            });

            // Values on the Y axis — min, avg, max
            void AddYLabel(double val, double y, string suffix = "")
            {
                var lbl = new System.Windows.Controls.TextBlock
                {
                    Text       = $"{val:F0}{suffix}",
                    FontSize   = 9,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(120, 200, 200, 200))
                };
                Canvas.SetLeft(lbl, 2);
                Canvas.SetTop(lbl, Math.Clamp(y - 8, 0, h - 14));
                SurfaceSpeedGraph.Children.Add(lbl);
            }

            AddYLabel(maxV, 0, " MB/s");
            AddYLabel(avgV, avgY);
            AddYLabel(minV, h - 8);

            // Polyline
            var poly = new System.Windows.Shapes.Polyline
            {
                StrokeThickness = Ui.ChartStroke,
                Stroke = new SolidColorBrush(
                    currentMBps < 30 ? (WpfColor)WpfColorConv.ConvertFromString("#EF4444")! :
                    currentMBps < 60 ? (WpfColor)WpfColorConv.ConvertFromString("#F59E0B")! :
                                       (WpfColor)WpfColorConv.ConvertFromString("#3B82F6")!),
                StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
            };

            for (int i = 0; i < pts.Count; i++)
            {
                double x = i / (double)(MAX_POINTS - 1) * w;
                double y = h - (pts[i] - minV) / range * (h - 16) - 8;
                poly.Points.Add(new System.Windows.Point(x, Math.Clamp(y, 0, h)));
            }
            SurfaceSpeedGraph.Children.Add(poly);

            // Punct curent + valoare
            double cx = (pts.Count - 1) / (double)(MAX_POINTS - 1) * w;
            double cy = h - (pts[^1] - minV) / range * (h - 16) - 8;
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 6, Height = 6, Fill = poly.Stroke,
            };
            Canvas.SetLeft(dot, cx - 3);
            Canvas.SetTop(dot, Math.Clamp(cy - 3, 0, h - 6));
            SurfaceSpeedGraph.Children.Add(dot);

            // The current value next to the point
            var curLbl = new System.Windows.Controls.TextBlock
            {
                Text     = $"{currentMBps:F0}",
                FontSize = 9, FontWeight = FontWeights.SemiBold,
                Foreground = poly.Stroke
            };
            Canvas.SetLeft(curLbl, Math.Clamp(cx + 5, 0, w - 30));
            Canvas.SetTop(curLbl,  Math.Clamp(cy - 8, 0, h - 14));
            SurfaceSpeedGraph.Children.Add(curLbl);
        }
    }
}
