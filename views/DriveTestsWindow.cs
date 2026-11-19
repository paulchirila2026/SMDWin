using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SMDWin.Services;
using Brushes     = System.Windows.Media.Brushes;
using ComboBox    = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Point       = System.Windows.Point;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment   = System.Windows.VerticalAlignment;

namespace SMDWin.Views
{
    /// <summary>
    /// The window of the three extra drive tests (Storage → Benchmark → More drive tests): real capacity, sustained write and
    /// access time. Each one: what it does, the drive, Start / Stop, progress, and the result in plain words.
    /// The engine is in <see cref="DriveTests"/>.
    /// </summary>
    internal sealed class DriveTestsWindow : Window
    {
        public enum Mode { Capacity, Sustained, Access }

        public sealed record PhysicalDisk(string Label, string Path, ulong Size, bool Hdd);

        private readonly Mode _mode;
        private readonly ComboBox _pick = new() { MinWidth = 320, Margin = new Thickness(0, 4, 0, 0) };
        private readonly ProgressBar _bar = new() { Height = 6, Maximum = 100, Margin = new Thickness(0, 12, 0, 4), Visibility = Visibility.Collapsed };
        private readonly TextBlock _status, _verdict, _detail;
        private readonly Canvas _chart = new() { Height = 120, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, ClipToBounds = true };
        private readonly Wpf.Ui.Controls.Button _start, _close;
        private readonly List<(double GB, double MBps)> _live = new();
        private CancellationTokenSource? _cts;

        private static string PT(string en, params object?[] a) => MainWindow.PT(en, a);

        public DriveTestsWindow(Mode mode, IReadOnlyList<PhysicalDisk>? disks = null)
        {
            _mode = mode;
            string title = mode switch
            {
                Mode.Capacity => PT("Real capacity check"),
                Mode.Sustained => PT("Sustained write"),
                _ => PT("Access time"),
            };
            string what = mode switch
            {
                Mode.Capacity => PT("Fills the free space with known data, reads it back and checks it. A fake USB stick or memory card reports more space than it really has — this finds where its real space ends."),
                Mode.Sustained => PT("Writes one large file and measures the speed every half second. Many SSDs are fast only until their cache fills up, then much slower — this shows how big the fast part is and the speed after it."),
                _ => PT("Reads small blocks at random places all over the disk. For a hard disk this is how long the head needs to reach any place: worn mechanics show up here even when SMART says everything is fine. It only reads."),
            };
            DialogKit.SetupDialogWindow(this, title, 640);
            var root = new StackPanel();
            root.Children.Add(DialogKit.Header(this, "Icon.Disk", title, what, out _));

            root.Children.Add(DialogKit.SectionLabel(mode == Mode.Access ? PT("Disk") : PT("Drive"), topMargin: 0));
            FillPicker(disks);
            root.Children.Add(_pick);

            string warn = mode switch
            {
                Mode.Capacity => PT("⚠ The test fills all the free space and deletes its files at the end; the existing files stay. On a fake stick, files can already be damaged — copy them elsewhere first. A 64 GB stick at USB 2.0 speed takes about 2 hours."),
                Mode.Sustained => PT("ⓘ Writes up to 80 GB (always leaving 10% of the drive free) for at most 4 minutes, then deletes the file. For an SSD this is a tiny part of its lifetime writes."),
                _ => PT("ⓘ About 300 reads, 10–20 seconds. Safe on any disk."),
            };
            var w = DialogKit.Text(warn, Ui.FontXS, secondary: true); w.Margin = new Thickness(2, 8, 2, 0);
            root.Children.Add(w);

            root.Children.Add(_bar);
            _status = DialogKit.Text("", Ui.FontXS, secondary: true);
            root.Children.Add(_status);
            root.Children.Add(_chart);
            _verdict = DialogKit.Text("", Ui.FontL, semibold: true); _verdict.Margin = new Thickness(0, 12, 0, 2);
            _detail = DialogKit.Text("", Ui.FontS, secondary: true);
            root.Children.Add(_verdict); root.Children.Add(_detail);

            var left = new StackPanel();
            _close = new Wpf.Ui.Controls.Button { Content = PT("Close"), Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary, Margin = new Thickness(0, 0, 8, 0) };
            _close.Click += (_, _) => Close();
            _start = new Wpf.Ui.Controls.Button { Content = PT("Start"), Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, IsEnabled = _pick.Items.Count > 0 };
            _start.Click += async (_, _) => { if (_cts != null) _cts.Cancel(); else await RunAsync(); };
            root.Children.Add(DialogKit.Footer(left, _close, _start));

            Content = DialogKit.Shell(root);
            Closing += (_, e) => { if (_cts != null) { _cts.Cancel(); e.Cancel = true; _status.Text = PT("Stopping…"); } };
        }

        private void FillPicker(IReadOnlyList<PhysicalDisk>? disks)
        {
            if (_mode == Mode.Access)
            {
                foreach (var d in disks ?? Array.Empty<PhysicalDisk>())
                    _pick.Items.Add(new ComboBoxItem { Content = d.Label, Tag = d });
                int hdd = (disks ?? Array.Empty<PhysicalDisk>()).ToList().FindIndex(d => d.Hdd);
                _pick.SelectedIndex = hdd >= 0 ? hdd : (_pick.Items.Count > 0 ? 0 : -1);
                return;
            }
            string sys = System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            foreach (var di in DriveInfo.GetDrives())
            {
                try
                {
                    if (!di.IsReady || (di.DriveType != DriveType.Removable && di.DriveType != DriveType.Fixed)) continue;
                    bool isSys = di.Name.Equals(sys, StringComparison.OrdinalIgnoreCase);
                    if (_mode == Mode.Capacity && isSys) continue;   // never fill the Windows drive
                    string label = $"{di.Name.TrimEnd('\\')}  {di.VolumeLabel}  ·  {DisplayFormat.SizeGb(di.AvailableFreeSpace / 1e9)} {PT("free of")} {DisplayFormat.SizeGb(di.TotalSize / 1e9)}"
                                 + (di.DriveType == DriveType.Removable ? "  ·  USB / card" : "") + (isSys ? "  ·  Windows" : "");
                    _pick.Items.Add(new ComboBoxItem { Content = label, Tag = di.Name });
                    if (_mode == Mode.Capacity && _pick.SelectedIndex < 0 && di.DriveType == DriveType.Removable) _pick.SelectedIndex = _pick.Items.Count - 1;
                }
                catch { }
            }
            if (_pick.SelectedIndex < 0 && _pick.Items.Count > 0) _pick.SelectedIndex = 0;
            if (_pick.Items.Count == 0) _pick.Items.Add(new ComboBoxItem { Content = PT("No suitable drive"), IsEnabled = false });
        }

        private static string Dur(double sec) => sec >= 3600 ? $"{(int)(sec / 3600)} h {(int)(sec % 3600 / 60):00} min" : sec >= 60 ? $"{(int)(sec / 60)} min {(int)(sec % 60):00} s" : $"{sec:0} s";

        private async Task RunAsync()
        {
            if (_pick.SelectedItem is not ComboBoxItem it || it.Tag == null) return;
            _cts = new CancellationTokenSource();
            _start.Content = PT("Stop"); _pick.IsEnabled = false; _close.IsEnabled = false;
            _bar.Visibility = Visibility.Visible; _bar.Value = 0;
            _verdict.Text = ""; _detail.Text = ""; _live.Clear(); _chart.Children.Clear();
            _chart.Visibility = _mode == Mode.Sustained ? Visibility.Visible : Visibility.Collapsed;
            var prog = new Progress<DriveTestProgress>(p =>
            {
                _bar.Value = Math.Clamp(p.Percent, 0, 100);
                _status.Text = _mode switch
                {
                    Mode.Capacity => (p.Phase == "write" ? PT("Writing") : PT("Checking")) + $" · {p.DoneGB:F1} GB · {p.MBps:F0} MB/s · " + PT("about {0} left", Dur(p.EtaSec)),
                    Mode.Sustained => PT("Writing") + $" · {p.DoneGB:F1} GB · {p.MBps:F0} MB/s · {Dur(p.ElapsedSec)}",
                    _ => PT("Reading") + $" · {p.Percent:F0}% · " + PT("average {0} ms", p.MBps.ToString("F1")),
                };
                if (_mode == Mode.Sustained) { _live.Add((p.DoneGB, p.MBps)); DrawChart(); }
            });
            try
            {
                switch (_mode)
                {
                    case Mode.Capacity:
                    {
                        var r = await DriveTests.CapacityCheckAsync((string)it.Tag, prog, _cts.Token);
                        if (r.Error.Length > 0) { Show(PT("The test could not run"), r.Error, "StatusWarnBrush"); break; }
                        if (!r.Completed) { Show(PT("Stopped"), PT("The test files were deleted."), "TextSecondaryBrush"); break; }
                        if (r.Fake)
                            Show(PT("Fake capacity: only {0} work", DisplayFormat.SizeGb(r.GoodGB)),
                                 PT("The data written after {0} came back wrong. Only the first {0} of this drive can be trusted; whatever is written beyond is lost. Do not use it for important files.", DisplayFormat.SizeGb(r.GoodGB)),
                                 "StatusCritBrush");
                        else
                            Show(PT("Real capacity — all {0} checked", DisplayFormat.SizeGb(r.TestedGB)),
                                 PT("Every block came back correct. Write {0} MB/s, read {1} MB/s.", r.WriteMBps.ToString("F0"), r.ReadMBps.ToString("F0")),
                                 "StatusOkBrush");
                        ToolActivityLog.Add("Real capacity check", r.Fake ? $"fake · {r.GoodGB:F1} GB good" : $"OK · {r.TestedGB:F1} GB", r.Fake ? "warn" : "ok");
                        break;
                    }
                    case Mode.Sustained:
                    {
                        var r = await DriveTests.SustainedWriteAsync((string)it.Tag, 80L * 1024 * 1024 * 1024, TimeSpan.FromMinutes(4), prog, _cts.Token);
                        _live.Clear(); _live.AddRange(r.Samples); DrawChart(r.CacheFound ? r.CacheGB : (double?)null);
                        if (r.Error.Length > 0) { Show(PT("The test could not run"), r.Error, "StatusWarnBrush"); break; }
                        if (r.CacheFound)
                            Show(PT("Fast for {0}, then {1} MB/s", DisplayFormat.SizeGb(r.CacheGB), r.SlowMBps.ToString("F0")),
                                 PT("The first {0} were written at about {1} MB/s; after the cache filled up the speed dropped to {2} MB/s. Copies larger than {0} slow down to that speed — normal for many SSDs, very noticeable on cheap (QLC / DRAM-less) ones.",
                                    DisplayFormat.SizeGb(r.CacheGB), r.FastMBps.ToString("F0"), r.SlowMBps.ToString("F0")),
                                 r.SlowMBps < 150 ? "StatusWarnBrush" : "TextPrimaryBrush");
                        else
                            Show(PT("Steady at about {0} MB/s", r.FastMBps.ToString("F0")),
                                 PT("The speed did not drop during the test ({0} written). The fast cache is larger than that, or the drive has none.", DisplayFormat.SizeGb(r.Samples.Count > 0 ? r.Samples[^1].GB : 0)),
                                 "StatusOkBrush");
                        ToolActivityLog.Add("Sustained write", r.CacheFound ? $"cache {r.CacheGB:F0} GB · then {r.SlowMBps:F0} MB/s" : $"steady {r.FastMBps:F0} MB/s", "ok");
                        break;
                    }
                    default:
                    {
                        var d = (PhysicalDisk)it.Tag;
                        var r = await DriveTests.AccessTimeAsync(d.Path, d.Size, 300, prog, _cts.Token);
                        if (r.Error.Length > 0) { Show(PT("The test could not run"), r.Error, "StatusWarnBrush"); break; }
                        if (r.Count == 0) { Show(PT("Stopped"), "", "TextSecondaryBrush"); break; }
                        string extra = r.Over100Ms > 0 ? " " + PT("{0} reads took over 100 ms — the disk had to retry some places.", r.Over100Ms) : "";
                        if (!d.Hdd)
                            Show(PT("Average {0} ms", r.AvgMs.ToString("F2")), PT("An SSD has no moving parts: well under 1 ms is normal.") + extra,
                                 r.AvgMs < 1 ? "StatusOkBrush" : "StatusWarnBrush");
                        else if (r.AvgMs <= 19)
                            Show(PT("Average {0} ms — normal", r.AvgMs.ToString("F1")), PT("Typical for a hard disk: about 12–15 ms at 7200 rpm, 15–19 ms at 5400 rpm.") + extra,
                                 r.Over100Ms > 3 ? "StatusWarnBrush" : "StatusOkBrush");
                        else if (r.AvgMs <= 25)
                            Show(PT("Average {0} ms — slow", r.AvgMs.ToString("F1")), PT("Slower than a healthy hard disk (12–19 ms). Close other programs and repeat; if it stays, the mechanics are wearing.") + extra, "StatusWarnBrush");
                        else
                            Show(PT("Average {0} ms — very slow", r.AvgMs.ToString("F1")), PT("Far slower than a healthy hard disk (12–19 ms): the head needs too long or retries. Back up the data; the disk is likely to fail.") + extra, "StatusCritBrush");
                        ToolActivityLog.Add("Access time", $"{d.Label}: {r.AvgMs:F1} ms", r.AvgMs > 25 ? "warn" : "ok");
                        break;
                    }
                }
            }
            finally
            {
                _cts?.Dispose(); _cts = null;
                _start.Content = PT("Start again"); _pick.IsEnabled = true; _close.IsEnabled = true;
                _bar.Visibility = Visibility.Collapsed;
                _status.Text = "";
            }
        }

        private void Show(string verdict, string detail, string brushKey)
        {
            _verdict.Text = verdict; _detail.Text = detail;
            _verdict.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        }

        /// <summary>Speed (MB/s) along the written GB; the end of the cache as a dashed line.</summary>
        private void DrawChart(double? cacheGB = null)
        {
            _chart.Children.Clear();
            double w = _chart.ActualWidth > 0 ? _chart.ActualWidth : 590, h = _chart.Height;
            if (_live.Count < 2) return;
            double maxGb = Math.Max(_live[^1].GB, 0.1), maxV = Math.Max(_live.Max(x => x.MBps) * 1.1, 1);
            var line = new Polyline { StrokeThickness = 1.8 };
            line.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
            foreach (var (gb, v) in _live) line.Points.Add(new Point(gb / maxGb * w, h - v / maxV * (h - 14)));
            _chart.Children.Add(line);
            var top = new TextBlock { Text = $"{maxV / 1.1:F0} MB/s", FontSize = 10 };
            top.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            _chart.Children.Add(top);
            if (cacheGB is double c)
            {
                double x = c / maxGb * w;
                var dl = new Line { X1 = x, X2 = x, Y1 = 0, Y2 = h, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } };
                dl.SetResourceReference(Shape.StrokeProperty, "StatusWarnBrush");
                _chart.Children.Add(dl);
                var lt = new TextBlock { Text = DisplayFormat.SizeGb(c), FontSize = 10 };
                lt.SetResourceReference(TextBlock.ForegroundProperty, "StatusWarnBrush");
                Canvas.SetLeft(lt, Math.Min(x + 4, w - 50)); Canvas.SetTop(lt, h - 14);
                _chart.Children.Add(lt);
            }
        }
    }
}
