using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;

using WpfColor         = System.Windows.Media.Color;
using WpfBrush         = System.Windows.Media.Brush;
using WpfSolidBrush    = System.Windows.Media.SolidColorBrush;
using WpfFontFamily    = System.Windows.Media.FontFamily;
using Button           = System.Windows.Controls.Button;
using CheckBox         = System.Windows.Controls.CheckBox;
using TextBox          = System.Windows.Controls.TextBox;
using TextBlock        = System.Windows.Controls.TextBlock;
using ScrollViewer     = System.Windows.Controls.ScrollViewer;
using StackPanel       = System.Windows.Controls.StackPanel;
using Border           = System.Windows.Controls.Border;
using Orientation      = System.Windows.Controls.Orientation;
using ComboBox         = System.Windows.Controls.ComboBox;
using ComboBoxItem     = System.Windows.Controls.ComboBoxItem;
using Brushes          = System.Windows.Media.Brushes;
using ColorConverter   = System.Windows.Media.ColorConverter;
using WpfPoint         = System.Windows.Point;
using WpfClipboard     = System.Windows.Clipboard;

namespace SMDWin.Views
{
    public partial class DebugInfoWindow
    {
        FrameworkElement BuildDatabasesPage()
        {
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24, 20, 24, 20) };
            var panel  = new StackPanel { Orientation = Orientation.Vertical };
            scroll.Content = panel;

            var textPrimary   = (WpfBrush)(TryFindResource("TextPrimaryBrush") ?? Brushes.White);
            var textSecondary = (WpfBrush)(TryFindResource("TextSecondaryBrush") ?? Brushes.Gray);
            var cardBg        = (WpfBrush)(TryFindResource("BgCardBrush") ?? Brushes.Transparent);
            var cardBorder    = (WpfBrush)(TryFindResource("CardBorderBrush") ?? Brushes.Gray);

            var checklistPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
            var checklistLoadingTb = new TextBlock
            {
                Text = "Checking this PC's hardware against the database…",
                FontSize = Ui.FontS, Foreground = textSecondary, Opacity = 0.7,
            };
            checklistPanel.Children.Add(checklistLoadingTb);
            panel.Children.Add(checklistPanel);

            void AddChecklistRow(string label, string detail, bool found)
            {
                var row = new Border
                {
                    Background = cardBg, BorderBrush = cardBorder, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9, 12, 9),
                    Margin = new Thickness(0, 0, 0, 6),
                };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var mark = new TextBlock
                {
                    Text = found ? "✓" : "✗", FontSize = Ui.FontM, FontWeight = FontWeights.Bold,
                    Foreground = found ? (WpfBrush)new SolidColorBrush(WpfColor.FromRgb(34, 197, 94))
                                       : (WpfBrush)new SolidColorBrush(WpfColor.FromRgb(239, 68, 68)),
                    Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(mark, 0);
                var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(new TextBlock { Text = label, FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold, Foreground = textPrimary });
                sp.Children.Add(new TextBlock { Text = detail, FontSize = Ui.FontXS, Foreground = textSecondary, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) });
                Grid.SetColumn(sp, 1);
                g.Children.Add(mark); g.Children.Add(sp);
                row.Child = g;
                checklistPanel.Children.Add(row);
            }

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var hw = new SMDWin.Services.HardwareService();
                    var sum = await hw.GetSystemSummaryAsync();
                    var disks = await hw.GetDisksAsync();
                    var ram = await hw.GetRamAsync();

                    await Dispatcher.InvokeAsync(() =>
                    {
                        checklistPanel.Children.Clear();

                        if (!string.IsNullOrWhiteSpace(sum?.Cpu))
                        {
                            var dbCpu = SMDWin.Services.HardwareDatabaseService.LookupCpu(sum.Cpu);
                            AddChecklistRow("CPU", sum.Cpu, dbCpu != null);
                        }

                        if (!string.IsNullOrWhiteSpace(sum?.GpuName))
                        {
                            var dbGpu = SMDWin.Services.HardwareDatabaseService.LookupGpu(sum.GpuName);
                            AddChecklistRow("GPU", sum.GpuName, dbGpu != null);
                        }

                        int ramIdx = 1;
                        foreach (var m in ram.Where(m => !m.IsEmpty))
                        {
                            string ramLabel = $"{m.Manufacturer} {m.PartNumber}".Trim();
                            bool ramFound = !string.IsNullOrWhiteSpace(m.PartNumber) &&
                                SMDWin.Services.HardwareDatabaseService.Ram.Any(r =>
                                    r.Key.Contains(SMDWin.Services.HardwareDatabaseService.NormalizeKey(m.PartNumber)));
                            AddChecklistRow($"RAM (slot {ramIdx++})", string.IsNullOrWhiteSpace(ramLabel) ? "—" : ramLabel, ramFound);
                        }

                        int diskIdx = 1;
                        foreach (var d in disks)
                        {
                            var dbDisk = SMDWin.Services.HardwareDatabaseService.LookupStorage(d.Model);
                            AddChecklistRow($"Storage {diskIdx++}", d.Model, dbDisk != null);
                        }

                        if (checklistPanel.Children.Count == 0)
                            checklistPanel.Children.Add(new TextBlock { Text = "Could not read the current hardware.", FontSize = Ui.FontS, Foreground = textSecondary, Opacity = 0.7 });
                    });
                }
                catch (Exception ex)
                {
                    AppLogger.Debug($"BuildDatabasesPage checklist: {ex.Message}");
                    await Dispatcher.InvokeAsync(() =>
                    {
                        checklistLoadingTb.Text = "Error while reading the current hardware.";
                    });
                }
            });

            // ── Summary cards, one per category ───────────────────────
            var statsGrid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 20) };
            var countTextBlocks = new Dictionary<string, TextBlock>();
            Border MakeStatCard(string label, int count, string fileName)
            {
                var b = new Border
                {
                    Background = cardBg, BorderBrush = cardBorder, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12, 14, 12),
                    Margin = new Thickness(0, 0, 8, 0),
                };
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), FontSize = Ui.FontXS, FontWeight = FontWeights.SemiBold, Foreground = textSecondary, Opacity = 0.75 });
                var countTb = new TextBlock { Text = count.ToString("N0"), FontSize = Ui.FontTitle, FontWeight = FontWeights.Bold, Foreground = textPrimary, Margin = new Thickness(0, 4, 0, 0) };
                countTextBlocks[label] = countTb;
                sp.Children.Add(countTb);
                string sizeText = "missing";
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    string suffix = "." + fileName;
                    var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                    if (resName != null)
                    {
                        using var s = asm.GetManifestResourceStream(resName);
                        if (s != null) sizeText = $"{s.Length / 1024.0:F0} KB (embedded)";
                    }
                }
                catch (Exception ex) { AppLogger.Debug($"MakeStatCard({fileName}) size check: {ex.Message}"); }
                sp.Children.Add(new TextBlock { Text = sizeText, FontSize = Ui.FontXS, Foreground = textSecondary, Opacity = 0.6, Margin = new Thickness(0, 2, 0, 0) });
                b.Child = sp;
                return b;
            }
            statsGrid.Children.Add(MakeStatCard("CPU", SMDWin.Services.HardwareDatabaseService.CpuCount, "cpu.json"));
            statsGrid.Children.Add(MakeStatCard("GPU", SMDWin.Services.HardwareDatabaseService.GpuCount, "gpu.json"));
            statsGrid.Children.Add(MakeStatCard("RAM", SMDWin.Services.HardwareDatabaseService.RamCount, "ram.json"));
            statsGrid.Children.Add(MakeStatCard("Storage", SMDWin.Services.HardwareDatabaseService.StorageCount, "storage.json"));
            panel.Children.Add(statsGrid);

            // ── Lookup — quickly checks whether a model exists ──────────────────
            panel.Children.Add(new TextBlock
            {
                Text = "LOOK UP A MODEL", FontSize = Ui.FontXS, FontWeight = FontWeights.SemiBold,
                Foreground = textSecondary, Opacity = 0.5, Margin = new Thickness(0, 4, 0, 8),
            });

            var searchRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var searchBox = new TextBox
            {
                FontSize = Ui.FontM, Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 8, 0),
            };
            searchBox.SetResourceReference(TextBox.BackgroundProperty, "BgInputBrush");
            searchBox.SetResourceReference(TextBox.ForegroundProperty, "TextPrimaryBrush");
            Grid.SetColumn(searchBox, 0);
            var searchBtn = new Button { Content = "Search", Padding = new Thickness(16, 7, 16, 7), Style = TryFindResource("OutlineButtonStyle") as Style };
            Grid.SetColumn(searchBtn, 1);
            searchRow.Children.Add(searchBox);
            searchRow.Children.Add(searchBtn);
            panel.Children.Add(searchRow);

            var resultsPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 20) };
            panel.Children.Add(resultsPanel);

            void RunSearch()
            {
                resultsPanel.Children.Clear();
                var q = searchBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(q))
                {
                    resultsPanel.Children.Add(new TextBlock { Text = "Type a model (e.g. \"i5-4570\", \"RX 570\", \"Samsung 980\") and press Search.", Foreground = textSecondary, Opacity = 0.7, FontSize = Ui.FontS });
                    return;
                }
                var key = SMDWin.Services.HardwareDatabaseService.NormalizeKey(q);
                int found = 0;

                void AddResultRow(string category, string name, string detail)
                {
                    found++;
                    var row = new Border { BorderBrush = cardBorder, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 8, 0, 8) };
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock { Text = $"[{category}]  {name}", FontWeight = FontWeights.SemiBold, FontSize = Ui.FontM, Foreground = textPrimary, TextWrapping = TextWrapping.Wrap });
                    sp.Children.Add(new TextBlock { Text = detail, FontSize = Ui.FontS, Foreground = textSecondary, Opacity = 0.8, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
                    row.Child = sp;
                    resultsPanel.Children.Add(row);
                }

                foreach (var c in SMDWin.Services.HardwareDatabaseService.Cpu.Where(x => x.Key.Contains(key)).Take(10))
                    AddResultRow("CPU", c.Name, $"{c.Cores}C/{c.Threads}T · {c.BaseClockGhz}-{c.BoostClockGhz} GHz · TDP {c.TdpW}W · {c.Socket}" + (c.TjMaxC.HasValue ? $" · TjMax {c.TjMaxC}°C" : ""));
                foreach (var g in SMDWin.Services.HardwareDatabaseService.Gpu.Where(x => x.Key.Contains(key)).Take(10))
                    AddResultRow("GPU", g.Name, $"{g.MemoryGb} GB {g.MemoryType} · {g.CoreBaseClockMhz}-{g.CoreBoostClockMhz} MHz · TDP {g.TdpW}W");
                foreach (var r in SMDWin.Services.HardwareDatabaseService.Ram.Where(x => x.Key.Contains(key)).Take(10))
                    AddResultRow("RAM", r.Name, $"{r.CapacityGb} GB {r.RamType} @ {r.SpeedMhz} MHz · CL{r.CasLatency}");
                foreach (var s in SMDWin.Services.HardwareDatabaseService.Storage.Where(x => x.Key.Contains(key)).Take(10))
                    AddResultRow("Storage", s.Name, $"{s.CapacityGb} GB · {s.Type} · {(s.Nvme ? "NVMe" : "non-NVMe")} · {s.Interface}");

                if (found == 0)
                    resultsPanel.Children.Add(new TextBlock { Text = $"No result for \"{q}\" — the model is not in the database (nothing is assumed).", Foreground = textSecondary, Opacity = 0.7, FontSize = Ui.FontS, TextWrapping = TextWrapping.Wrap });
                else
                    resultsPanel.Children.Insert(0, new TextBlock { Text = $"{found} result(s):", FontSize = Ui.FontXS, Foreground = textSecondary, Opacity = 0.6, Margin = new Thickness(0, 0, 0, 6) });
            }
            searchBtn.Click += (_, _) => RunSearch();
            searchBox.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) RunSearch(); };

            // ── Manual reload — useful after editing the JSON files directly ────
            var reloadBtn = new Button
            {
                Content = "Reload databases from disk", Padding = new Thickness(14, 8, 14, 8),
                Style = TryFindResource("OutlineButtonStyle") as Style, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            };
            reloadBtn.Click += (_, _) =>
            {
                SMDWin.Services.HardwareDatabaseService.Reload();
                if (countTextBlocks.TryGetValue("CPU", out var cpuTb)) cpuTb.Text = SMDWin.Services.HardwareDatabaseService.CpuCount.ToString("N0");
                if (countTextBlocks.TryGetValue("GPU", out var gpuTb)) gpuTb.Text = SMDWin.Services.HardwareDatabaseService.GpuCount.ToString("N0");
                if (countTextBlocks.TryGetValue("RAM", out var ramTb)) ramTb.Text = SMDWin.Services.HardwareDatabaseService.RamCount.ToString("N0");
                if (countTextBlocks.TryGetValue("Storage", out var stoTb)) stoTb.Text = SMDWin.Services.HardwareDatabaseService.StorageCount.ToString("N0");
                RunSearch(); // recheck the current search result too, if there is one
            };
            panel.Children.Add(reloadBtn);

            return scroll;
        }
    }
}
