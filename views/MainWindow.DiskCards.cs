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

        // Opens a partition (drive letter) directly in Windows Explorer
        private void OpenPartitionInExplorer(string letter)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(letter) || letter == "—") return;
                string path = letter.TrimEnd(':', '\\') + ":\\";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }
        }

        private UIElement BuildDiskCard(DiskHealthEntry disk)
        {
            string L(string en, string ro, string de, string fr) => SMDWin.Services.LanguageService.CurrentCode switch
                { "ro" => ro, "de" => de, "fr" => fr, _ => en };

            // ── The drive type ────────────────────────────────────────────────
            string mediaType = disk.MediaType ?? "";
            string model     = disk.Model ?? "";

            // USB: check DriveType on the partitions OR MediaType="USB" OR the model contains USB keywords
            bool isRemovable = mediaType.Equals("USB", StringComparison.OrdinalIgnoreCase)
              || mediaType.Contains("Removable", StringComparison.OrdinalIgnoreCase)
              || model.Contains("USB", StringComparison.OrdinalIgnoreCase)
              || model.Contains("Flash Drive", StringComparison.OrdinalIgnoreCase)
              || model.Contains("Thumb", StringComparison.OrdinalIgnoreCase)
              || disk.Partitions.Any(p => {
                try {
                    var l = p.Letter?.TrimEnd('\\');
                    if (string.IsNullOrEmpty(l)) return false;
                    var dt = new System.IO.DriveInfo(l).DriveType;
                    return dt == System.IO.DriveType.Removable || dt == System.IO.DriveType.Network;
                } catch { return false; }
            });

            bool isNvme = !isRemovable && (
                            mediaType.Equals("NVMe", StringComparison.OrdinalIgnoreCase)
                         || model.Contains("NVMe", StringComparison.OrdinalIgnoreCase)
                         || model.Contains("M.2",  StringComparison.OrdinalIgnoreCase));
            bool isSsd  = !isRemovable && !isNvme && mediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase);
            bool isHdd  = !isRemovable && !isNvme && !isSsd && mediaType.Equals("HDD", StringComparison.OrdinalIgnoreCase);

            bool looksLikeStick = model.Contains("Flash Drive", StringComparison.OrdinalIgnoreCase)
                                || model.Contains("Thumb",       StringComparison.OrdinalIgnoreCase)
                                || model.Contains("Kingston DataTraveler", StringComparison.OrdinalIgnoreCase)
                                || model.Contains("SanDisk Cruzer", StringComparison.OrdinalIgnoreCase);
            bool externalLooksSsd = mediaType.Contains("SSD", StringComparison.OrdinalIgnoreCase)
                                  || model.Contains("SSD", StringComparison.OrdinalIgnoreCase);

            string badgeText = isRemovable
                ? (looksLikeStick ? "USB Stick" : externalLooksSsd ? "SSD (external)" : "USB Drive (external)")
                : isNvme ? "SSD NVMe"
                : isSsd  ? "SSD SATA"
                : isHdd  ? "HDD SATA"
                : L("Unknown type", "Tip necunoscut", "Unbekannter Typ", "Type inconnu");

            try
            {
                var dbStorage = SMDWin.Services.HardwareDatabaseService.LookupStorage(model);
                if (dbStorage != null && !string.IsNullOrEmpty(dbStorage.Type))
                {
                    string? dbLabel = dbStorage.Nvme ? "SSD NVMe"
                        : dbStorage.Type.Equals("SSD", StringComparison.OrdinalIgnoreCase) ? "SSD SATA"
                        : dbStorage.Type.Equals("HDD", StringComparison.OrdinalIgnoreCase) ? "HDD SATA"
                        : null;
                    if (dbLabel != null)
                        badgeText = isRemovable ? $"{dbLabel.Split(' ')[0]} (external)" : dbLabel;
                }
            }
            catch (Exception ex) { AppLogger.Debug($"Disk DB lookup: {ex.Message}"); }
            string iconColor = isNvme ? "#8B5CF6" : isSsd ? "#3B82F6" : isRemovable ? "#F59E0B" : "#6B7280";
            var    iconWpf   = (WpfColor)WpfColorConv.ConvertFromString(iconColor)!;
            string iconPath  = isRemovable
                // USB stick — Icon.UsbStick from Icons.xaml
                ? "M3.5,8 H14.5 A1.5,1.5 0 0 1 16,9.5 V14.5 A1.5,1.5 0 0 1 14.5,16 H3.5 A1.5,1.5 0 0 1 2,14.5 V9.5 A1.5,1.5 0 0 1 3.5,8 Z M16.5,9.5 H20.5 A0.5,0.5 0 0 1 21,10 V14 A0.5,0.5 0 0 1 20.5,14.5 H16.5 A0.5,0.5 0 0 1 16,14 V10 A0.5,0.5 0 0 1 16.5,9.5 Z M17.5,11 H19.5 V13 H17.5 Z M5 11h4 M11.3,12 A0.7,0.7 0 1 0 12.7,12 A0.7,0.7 0 1 0 11.3,12 Z"
                : isNvme
                // NVMe M.2 — Icon.Nvme from Icons.xaml
                ? "M4,8 H20 A1,1 0 0 1 21,9 V15 A1,1 0 0 1 20,16 H4 A1,1 0 0 1 3,15 V9 A1,1 0 0 1 4,8 Z M5.5,10 H7.5 A0.5,0.5 0 0 1 8,10.5 V13.5 A0.5,0.5 0 0 1 7.5,14 H5.5 A0.5,0.5 0 0 1 5,13.5 V10.5 A0.5,0.5 0 0 1 5.5,10 Z M10.5,10 H12.5 A0.5,0.5 0 0 1 13,10.5 V13.5 A0.5,0.5 0 0 1 12.5,14 H10.5 A0.5,0.5 0 0 1 10,13.5 V10.5 A0.5,0.5 0 0 1 10.5,10 Z M15.5,10 H17.5 A0.5,0.5 0 0 1 18,10.5 V13.5 A0.5,0.5 0 0 1 17.5,14 H15.5 A0.5,0.5 0 0 1 15,13.5 V10.5 A0.5,0.5 0 0 1 15.5,10 Z M3 14h-1M21 14h1"
                : isSsd
                // SATA SSD — Icon.Ssd from Icons.xaml
                ? "M4,5 H20 A2,2 0 0 1 22,7 V17 A2,2 0 0 1 20,19 H4 A2,2 0 0 1 2,17 V7 A2,2 0 0 1 4,5 Z M6,9 H10 A1,1 0 0 1 11,10 V14 A1,1 0 0 1 10,15 H6 A1,1 0 0 1 5,14 V10 A1,1 0 0 1 6,9 Z M14 11h4M14 14h4"
                // HDD — Icon.Hdd from Icons.xaml
                : "M4,5 H20 A2,2 0 0 1 22,7 V17 A2,2 0 0 1 20,19 H4 A2,2 0 0 1 2,17 V7 A2,2 0 0 1 4,5 Z M8,12 A4,4 0 1 0 16,12 A4,4 0 1 0 8,12 Z M11,12 A1,1 0 1 0 13,12 A1,1 0 1 0 11,12 Z";

            // ── SMART: temp + power-on hours + wear ──────────────────────────
            var tempAttr = disk.SmartAttributes.FirstOrDefault(a => a.Id == 0xC2)
                        ?? disk.SmartAttributes.FirstOrDefault(a => a.Id == 0xBE);
            var pohAttr  = disk.SmartAttributes.FirstOrDefault(a => a.Id == 0x09);
            double tempC = -1;
            if (tempAttr != null)
            {
                long tempLsb = tempAttr.RawValue & 0xFF;
                if (tempLsb > 0 && tempLsb < 100) tempC = tempLsb;
            }
            // NVMe (no 0xC2 attribute) or a disk without a temperature attribute: use the last cached smartctl reading, if any.
            if (tempC <= 0 && !string.IsNullOrWhiteSpace(disk.PhysicalDevicePath)
                && _diskTempCache.TryGetValue(disk.PhysicalDevicePath, out double cachedTemp) && cachedTemp > 0)
                tempC = cachedTemp;
            long   poh    = pohAttr  != null ? pohAttr.RawValue  : -1;

            double radiusCardForDisk = (System.Windows.Application.Current.Resources["RadiusCard"] as CornerRadius?)?.TopLeft ?? 8;
            var outer = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(radiusCardForDisk),
                Padding         = new Thickness(20, 16, 20, 16),
                Margin          = new Thickness(0, 0, 0, 14)
            };
            outer.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
            outer.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2");
            outer.MouseEnter += (_, _) =>
            {
                if (!SettingsService.Current.DesignExperimentShadowsOff)
                    outer.SetResourceReference(Border.BackgroundProperty, "BgHoverBrush");
            };
            outer.MouseLeave += (_, _) =>
            {
                // no hover shadow: an effect on the card would blur its text
                outer.SetResourceReference(Border.BackgroundProperty, "BgCardBrush");
            };
            var sp = new StackPanel();

            // ── HEADER: icon + model + badge + health ─────────────────────────
            var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Icon
            var iconVb = new Viewbox { Width = 22, Height = 22, VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            var iconCv = new Canvas { Width = 24, Height = 24 };
            iconCv.Children.Add(new System.Windows.Shapes.Path
            {
                Stroke = new SolidColorBrush(iconWpf), StrokeThickness = 1.6,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeEndLineCap   = System.Windows.Media.PenLineCap.Round,
                StrokeLineJoin     = System.Windows.Media.PenLineJoin.Round,
                Fill = System.Windows.Media.Brushes.Transparent,
                Data = System.Windows.Media.Geometry.Parse(iconPath)
            });
            iconVb.Child = iconCv;
            Grid.SetColumn(iconVb, 0);
            header.Children.Add(iconVb);

            // Model + badge tip
            var titleSp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            titleSp.Children.Add(new TextBlock
            {
                Text = disk.Model ?? "Unknown",
                FontSize = Ui.FontXXL, FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            });
            var typeBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(30, iconWpf.R, iconWpf.G, iconWpf.B))
            };
            typeBadge.Child = new TextBlock { Text = badgeText, FontSize = Ui.FontXS, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(iconWpf) };
            titleSp.Children.Add(typeBadge);
            Grid.SetColumn(titleSp, 1);
            header.Children.Add(titleSp);

            // Health
            var healthClr = (WpfColor)WpfColorConv.ConvertFromString(disk.HealthColor)!;
            var healthSp  = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            healthSp.ToolTip = L("Overall drive status, reported by Windows (S.M.A.R.T. / failure prediction).","Starea generală a discului, raportată de Windows (S.M.A.R.T. / predicție de defecțiune).", "Allgemeiner Laufwerksstatus, gemeldet von Windows (S.M.A.R.T. / Ausfallvorhersage).", "État général du disque, signalé par Windows (S.M.A.R.T. / prédiction de panne).");
            string diskHealthReason = SMDWin.Services.HardwareService.GetDiskHealthReason(disk);
            if (!string.IsNullOrEmpty(diskHealthReason))
                healthSp.ToolTip = (healthSp.ToolTip as string ?? "") + "\n\n⚠ " + diskHealthReason;
            // The compact variant: a large percentage + a small label (a ring would make the header too tall).
            healthSp.Children.Add(new TextBlock
            {
                Text = $"{disk.HealthPercent}%",
                FontSize = Ui.FontXXL, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(healthClr),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            });
            healthSp.Children.Add(new TextBlock
            {
                Text = "Health",
                FontSize = Ui.FontXS, Foreground = (Brush)FindResource("TextSecondaryBrush"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Opacity = 0.7,
            });
            Grid.SetColumn(healthSp, 2);
            header.Children.Add(healthSp);
            sp.Children.Add(header);

            // ── SMART DEGRADATION ALERT (if any) ─────────────────────────
            var history = SmartHistoryService.LoadHistory(disk.SerialNumber ?? "");
            if (history?.Snapshots.Count >= 2)
            {
                var alert = SmartHistoryService.CheckDegradation(history);
                if (alert.HasAlert)
                {
                    string alertHex = alert.Severity == "critical" ? "#EF4444" : "#F59E0B";
                    var alertWpf = (WpfColor)WpfColorConv.ConvertFromString(alertHex)!;
                    var alertBorder = new Border
                    {
                        CornerRadius    = new CornerRadius(8),
                        Padding         = new Thickness(12, 8, 12, 8),
                        Margin          = new Thickness(0, 0, 0, 10),
                        BorderThickness = new Thickness(1),
                        BorderBrush     = new SolidColorBrush(System.Windows.Media.Color.FromArgb(120, alertWpf.R, alertWpf.G, alertWpf.B)),
                        Background      = new SolidColorBrush(System.Windows.Media.Color.FromArgb(20,  alertWpf.R, alertWpf.G, alertWpf.B))
                    };
                    alertBorder.Child = new TextBlock { Text = alert.Message, FontSize = Ui.FontM, Foreground = new SolidColorBrush(alertWpf), TextWrapping = TextWrapping.Wrap };
                    sp.Children.Add(alertBorder);
                }
            }

            // ── STATISTICS: a single row of compact "pills" (label + value).
            var statsWrap = new System.Windows.Controls.WrapPanel { Margin = new Thickness(0, 0, 0, 12) };

            Border MakePill(string label, TextBlock valueBlock)
            {
                var pill = new Border
                {
                    CornerRadius    = new CornerRadius(6),
                    Padding         = new Thickness(10, 5, 10, 5),
                    Margin          = new Thickness(0, 0, 8, 6),
                    BorderThickness = new Thickness(1),
                    Background      = (Brush)(TryFindResource("BgInputBrush") ?? System.Windows.Media.Brushes.Transparent),
                    BorderBrush     = (Brush)FindResource("CardBorderBrush"),
                };
                var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                row.Children.Add(new TextBlock
                {
                    Text = label + "  ", FontSize = Ui.FontS,
                    Foreground = (Brush)FindResource("TextSecondaryBrush"),
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                });
                valueBlock.VerticalAlignment = System.Windows.VerticalAlignment.Center;
                row.Children.Add(valueBlock);
                pill.Child = row;
                return pill;
            }

            void AddStat(string label, string value) =>
                statsWrap.Children.Add(MakePill(label, new TextBlock
                {
                    Text = value, FontSize = Ui.FontM, FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("TextPrimaryBrush"),
                }));

            AddStat("Capacity", disk.Size);
            AddStat("Serial",   disk.SerialNumber ?? "—");
            AddStat("Status",   disk.Status);
            {
                // The temperature pill keeps the names TxtDiskTemp_* (TextBlock) and PillDiskTemp_* (Border) — ApplyDiskTempToCard
                // finds them by name and updates them live (text, colour, background) without rebuilding the card.
                WpfColor tWpf = TempStatusColor(tempC > 0 ? tempC : 0);
                var tTxt = new TextBlock
                {
                    Text = tempC > 0 ? $"{TempUnit.Fmt(tempC)}" : "",
                    FontSize = Ui.FontM, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(tWpf)
                };
                string tKey = DiskTempKey(disk);
                RegisterOrReplaceName($"TxtDiskTemp_{tKey}", tTxt);
                var tPill = MakePill("Temperature", tTxt);
                tPill.Background  = new SolidColorBrush(WpfColor.FromArgb(20, tWpf.R, tWpf.G, tWpf.B));
                tPill.BorderBrush = new SolidColorBrush(WpfColor.FromArgb(60, tWpf.R, tWpf.G, tWpf.B));
                tPill.Visibility  = tempC > 0 ? Visibility.Visible : Visibility.Collapsed;
                RegisterOrReplaceName($"PillDiskTemp_{tKey}", tPill);
                statsWrap.Children.Add(tPill);
            }
            if (poh > 0) AddStat("Power-On", $"{poh:N0} h  ({poh/24:N0} days)");
            sp.Children.Add(statsWrap);

            // ── EoL PREDICTOR (SSD/NVMe) ──────────────────────────────────────
            if (isSsd || isNvme)
            {
                try
                {
                    var (yearsLeft, basis) = SMDWin.Services.DiskEoLPredictor.Predict(disk);
                    if (yearsLeft >= 0 || basis.Length > 0)
                    {
                        string eolColor, eolText, eolIcon;
                        bool skipRow = false;
                        if (yearsLeft >= 0 && yearsLeft <= 1 && disk.HealthPercent >= 90)
                        {
                            skipRow = true; eolColor = "#94A3B8"; eolIcon = ""; eolText = "";
                        }
                        else if (yearsLeft < 0)      { eolColor = "#94A3B8"; eolIcon = "ℹ"; eolText = basis; }
                        else if (yearsLeft <= 1) { eolColor = "#EF4444"; eolIcon = "⚠"; eolText = yearsLeft < 0.1 ? "Wear detected — a backup is recommended." : $"~{yearsLeft:F1} years remaining  ·  {basis}"; }
                        else if (yearsLeft <= 3) { eolColor = "#F59E0B"; eolIcon = "⚠"; eolText = $"~{yearsLeft:F1} years remaining  ·  {basis}"; }
                        else if (yearsLeft <= 10) { eolColor = "#22C55E"; eolIcon = "✓"; eolText = $"~{yearsLeft:F1} years remaining  ·  {basis}"; }
                        else
                        {
                            eolColor = "#22C55E"; eolIcon = "✓"; eolText = $">10 years remaining  ·  {basis}";
                        }

                        // The remaining-life estimate is very approximate: it sits in the tooltip of the Health percentage.
                        // The visible row stays only for the red warning (<= 1 year / wear detected) — there is a concrete action there (backup).
                        if (!skipRow && !string.IsNullOrEmpty(eolText))
                        {
                            string approx = L("Rough estimate only, based on current SMART wear data and usage pattern — not a guarantee.",
                                              "Doar o estimare aproximativă, bazată pe datele SMART de uzură și pe ritmul de utilizare — nu e o garanție.",
                                              "Nur eine grobe Schätzung aus SMART-Verschleißdaten und Nutzungsmuster — keine Garantie.",
                                              "Estimation approximative, basée sur les données SMART d'usure et le rythme d'utilisation — pas une garantie.");
                            string lifeHdr = L("Estimated lifespan: ", "Durată de viață estimată: ", "Geschätzte Lebensdauer: ", "Durée de vie estimée : ");
                            healthSp.ToolTip = (healthSp.ToolTip as string ?? "") + "\n\n" + lifeHdr + eolText.Replace("  ·  ", " · ") + "\n" + approx;
                        }
                        if (eolColor != "#EF4444") skipRow = true;

                        if (!skipRow)
                        {
                        var eolWpf = (WpfColor)WpfColorConv.ConvertFromString(eolColor)!;
                        var eolBorder = new Border
                        {
                            CornerRadius    = new CornerRadius(8),
                            Padding         = new Thickness(12, 8, 12, 8),
                            Margin          = new Thickness(0, 0, 0, 14),
                            BorderThickness = new Thickness(1),
                            BorderBrush     = new SolidColorBrush(System.Windows.Media.Color.FromArgb(60, eolWpf.R, eolWpf.G, eolWpf.B)),
                            Background      = new SolidColorBrush(System.Windows.Media.Color.FromArgb(15, eolWpf.R, eolWpf.G, eolWpf.B)),
                            ToolTip = L(
                                "Rough estimate only, based on current SMART wear data and usage pattern — not a guarantee. Actual lifespan can vary significantly.",
                                "Doar o estimare aproximativă, bazată pe datele SMART actuale de uzură și pe ritmul de utilizare — nu e o garanție. Durata reală poate varia semnificativ.",
                                "Nur eine grobe Schätzung, basierend auf aktuellen SMART-Verschleißdaten und Nutzungsmuster — keine Garantie. Die tatsächliche Lebensdauer kann erheblich abweichen.",
                                "Estimation approximative uniquement, basée sur les données SMART actuelles d'usure et le rythme d'utilisation — ce n'est pas une garantie. La durée de vie réelle peut varier considérablement.")
                        };
                        var eolSp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                        eolSp.Children.Add(new TextBlock { Text = eolIcon + "  ", FontSize = Ui.FontL, Foreground = new SolidColorBrush(eolWpf), VerticalAlignment = System.Windows.VerticalAlignment.Center });
                        eolSp.Children.Add(new TextBlock { Text = eolText, FontSize = Ui.FontM, Foreground = new SolidColorBrush(eolWpf), TextWrapping = TextWrapping.Wrap, VerticalAlignment = System.Windows.VerticalAlignment.Center });
                        eolBorder.Child = eolSp;
                        sp.Children.Add(eolBorder);
                        }
                    }
                }
                catch (Exception ex) { AppLogger.Debug(ex.Message); }
            }

            // ── PARTITIONS — thin map + list ─────────
            // The thin strip shows how the disk is split — each partition in its colour, the width proportional to its size
            // (including EFI/Recovery). Below it, one row per partition: colour + letter, name · FS · size, usage bar, free space.
            // Red/yellow appear only on the bar and text of the partition that really is low on space.
            if (disk.Partitions.Count > 0)
            {
                string diskStyle = disk.Partitions.FirstOrDefault(p => !string.IsNullOrEmpty(p.PartitionStyle))?.PartitionStyle ?? "";
                var visibleParts = disk.Partitions.Where(p => p.TotalGB > 0.01).ToList();
                double totalGB   = visibleParts.Sum(p => p.TotalGB);
                if (totalGB < 0.1) totalGB = 1;
                var partColors   = new[] { "#3B82F6", "#8B5CF6", "#14B8A6", "#F59E0B", "#EC4899", "#22C55E", "#F97316" };
                WpfColor PartColor(int i) => (WpfColor)WpfColorConv.ConvertFromString(partColors[i % partColors.Length])!;

                sp.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("CardBorderBrush"), Opacity = 0.4, Margin = new Thickness(0, 0, 0, 10) });

                // Antet mic: „Partitions” + stil (GPT/MBR)
                var partHdr = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                partHdr.ColumnDefinitions.Add(new ColumnDefinition());
                partHdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                partHdr.Children.Add(new TextBlock { Text = L("Partitions", "Partiții", "Partitionen", "Partitions"), FontSize = Ui.FontS, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("TextSecondaryBrush"), VerticalAlignment = System.Windows.VerticalAlignment.Center });
                if (!string.IsNullOrEmpty(diskStyle))
                {
                    var sb = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), Background = new SolidColorBrush(WpfColor.FromArgb(30, 100, 160, 255)) };
                    sb.Child = new TextBlock { Text = diskStyle, FontSize = Ui.FontXS, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush((WpfColor)WpfColorConv.ConvertFromString("#60A5FA")!) };
                    Grid.SetColumn(sb, 1); partHdr.Children.Add(sb);
                }
                sp.Children.Add(partHdr);

                // Thin map: 16 px + a 4 px gap between segments + the partition letter in the segment, so C: and D: do not look like a
                // single partition
                var map = new Grid { Height = 16, Margin = new Thickness(0, 0, 0, 8) };
                for (int i = 0; i < visibleParts.Count; i++)
                {
                    var part = visibleParts[i];
                    // Very small partitions (a 100 MB EFI on 1 TB) get a minimum visible width
                    double w = Math.Max(part.TotalGB / totalGB, 0.006);
                    map.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });
                    bool first = i == 0, last = i == visibleParts.Count - 1;
                    string letterM = string.IsNullOrEmpty(part.Letter) ? "" : part.Letter.TrimEnd('\\');
                    string name = !string.IsNullOrEmpty(letterM) ? letterM : (string.IsNullOrWhiteSpace(part.Label) ? "—" : part.Label);
                    // the segment shows usage too — the used part in the partition colour, the free space in the same hue with +18 lightness
                    // (HSL). A sharp transition (two gradient stops at the same offset), with no extra WPF elements.
                    var segCol  = PartColor(i);
                    // on Dark, free space = the partition colour mixed with the card background (a dark, discreet shade) — the lighter
                    // variant (+18 HSL) stays only on Light.
                    var cardCol = (TryFindResource("BgCardBrush") as SolidColorBrush)?.Color ?? WpfColor.FromRgb(255, 255, 255);
                    bool darkCard = (cardCol.R * 0.299 + cardCol.G * 0.587 + cardCol.B * 0.114) < 128;
                    var segFree = darkCard
                        ? WpfColor.FromRgb((byte)(cardCol.R * 0.7 + segCol.R * 0.3), (byte)(cardCol.G * 0.7 + segCol.G * 0.3), (byte)(cardCol.B * 0.7 + segCol.B * 0.3))
                        : LightenHsl(segCol, 0.18);
                    double segUsed = part.TotalGB > 0 ? Math.Clamp(part.UsedGB / part.TotalGB, 0, 1) : 1;
                    var segBrush = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0.5), EndPoint = new System.Windows.Point(1, 0.5) };
                    segBrush.GradientStops.Add(new GradientStop(segCol, 0));
                    segBrush.GradientStops.Add(new GradientStop(segCol, segUsed));
                    segBrush.GradientStops.Add(new GradientStop(segFree, segUsed));
                    segBrush.GradientStops.Add(new GradientStop(segFree, 1));
                    segBrush.Freeze();
                    var seg = new Border
                    {
                        Background   = segBrush,
                        CornerRadius = new CornerRadius(first ? 5 : 2, last ? 5 : 2, last ? 5 : 2, first ? 5 : 2),
                        Margin       = new Thickness(first ? 0 : 2, 0, last ? 0 : 2, 0),
                        ToolTip      = $"{name}  {part.Label}  ·  {SMDWin.Services.DisplayFormat.SizeGb(part.TotalGB)}  ·  {SMDWin.Services.DisplayFormat.SizeGb(part.FreeGB)} free",
                    };
                    // Letter in the segment, only if the segment is wide enough (>= 8% of the disk)
                    if (!string.IsNullOrEmpty(letterM) && w >= 0.08)
                        seg.Child = new TextBlock
                        {
                            Text = letterM, FontSize = 10, FontWeight = FontWeights.Bold,
                            Foreground = System.Windows.Media.Brushes.White,
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                            VerticalAlignment = System.Windows.VerticalAlignment.Center,
                            Margin = new Thickness(6, 0, 0, 0), IsHitTestVisible = false,
                        };
                    if (!string.IsNullOrEmpty(letterM))
                    {
                        seg.Cursor = System.Windows.Input.Cursors.Hand;
                        string lt = letterM;
                        seg.MouseLeftButtonUp += (_, _) => OpenPartitionInExplorer(lt);
                    }
                    Grid.SetColumn(seg, i);
                    map.Children.Add(seg);
                }
                sp.Children.Add(map);

                // The partition list
                for (int i = 0; i < visibleParts.Count; i++)
                {
                    var part = visibleParts[i];
                    var pc = PartColor(i);
                    double usedFrac = part.TotalGB > 0 ? Math.Clamp(part.UsedGB / part.TotalGB, 0, 1) : 0;
                    double fullPct  = usedFrac * 100.0;
                    bool crit = fullPct >= 95, warn = !crit && fullPct >= 90;
                    // The usage bar stays in the partition colour (it links the row to the map segment); the low-space warning is on the
                    // "GB free" text (red / yellow) + tooltip.
                    WpfColor warnColor = crit ? WpfColor.FromRgb(239, 68, 68) : WpfColor.FromRgb(245, 158, 11);
                    WpfColor barColor  = pc;
                    string letter = string.IsNullOrEmpty(part.Letter) ? "" : part.Letter.TrimEnd('\\');

                    var row = new Grid { Margin = new Thickness(0, 0, 0, 0), Background = System.Windows.Media.Brushes.Transparent };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });                    // culoare + litera
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star), MinWidth = 120 }); // nume
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) }); // bar
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 150 });       // liber
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // badge / clean up

                    var lead = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = System.Windows.VerticalAlignment.Center };
                    // The letter in a coloured label (the same colour as the map segment)
                    var chip = new Border
                    {
                        MinWidth = 30, Height = 22, CornerRadius = new CornerRadius(5), Padding = new Thickness(5, 0, 5, 0),
                        Background = new SolidColorBrush(WpfColor.FromArgb(36, pc.R, pc.G, pc.B)),
                        BorderBrush = new SolidColorBrush(WpfColor.FromArgb(110, pc.R, pc.G, pc.B)), BorderThickness = new Thickness(1),
                        VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    };
                    chip.Child = new TextBlock { Text = string.IsNullOrEmpty(letter) ? "—" : letter, FontSize = Ui.FontM, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(pc), HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center };
                    lead.Children.Add(chip);
                    row.Children.Add(lead);

                    var nameSp = new StackPanel { VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                    nameSp.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(part.Label) ? L("(no label)", "(fără nume)", "(ohne Namen)", "(sans nom)") : part.Label, FontSize = Ui.FontM, Foreground = (Brush)FindResource("TextPrimaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
                    nameSp.Children.Add(new TextBlock { Text = $"{part.FileSystem} · {part.TotalGB:F1} GB", FontSize = Ui.FontS, Foreground = (Brush)FindResource("TextSecondaryBrush") });
                    Grid.SetColumn(nameSp, 1); row.Children.Add(nameSp);

                    // Usage bar (filled/rest through star columns — does not depend on ActualWidth)
                    var bar = new Grid { Height = 8, VerticalAlignment = System.Windows.VerticalAlignment.Center };
                    bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, usedFrac), GridUnitType.Star) });
                    bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - usedFrac), GridUnitType.Star) });
                    var track = new Border { CornerRadius = new CornerRadius(4) };
                    track.SetResourceReference(Border.BackgroundProperty, "BorderBrush2");
                    Grid.SetColumnSpan(track, 2); bar.Children.Add(track);
                    bar.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(barColor) });
                    Grid.SetColumn(bar, 2); row.Children.Add(bar);

                    var free = new TextBlock
                    {
                        Text = L($"{SMDWin.Services.DisplayFormat.SizeGb(part.FreeGB)} free", $"{SMDWin.Services.DisplayFormat.SizeGb(part.FreeGB)} liberi", $"{SMDWin.Services.DisplayFormat.SizeGb(part.FreeGB)} frei", $"{SMDWin.Services.DisplayFormat.SizeGb(part.FreeGB).Replace("GB", "Go").Replace("TB", "To")} libres"),
                        FontSize = Ui.FontM, FontWeight = FontWeights.SemiBold, HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                        VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
                    };
                    if (crit || warn) { free.Foreground = new SolidColorBrush(warnColor); free.Text = "⚠ " + free.Text; }
                    else free.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                    if (crit || warn)
                        free.ToolTip = crit
                            ? L($"Almost full — only {100 - fullPct:F0}% free. SSDs below 5% free can be significantly slower.",
                                $"Aproape plin — doar {100 - fullPct:F0}% liber. SSD-urile sub 5% spațiu liber pot fi semnificativ mai lente.",
                                $"Fast voll — nur {100 - fullPct:F0}% frei. SSDs unter 5% freiem Speicher können deutlich langsamer sein.",
                                $"Presque plein — seulement {100 - fullPct:F0}% libre. Les SSD à moins de 5% d'espace libre peuvent être nettement plus lents.")
                            : L($"Low space — {100 - fullPct:F0}% free. At least 10% free is recommended.",
                                $"Spațiu limitat — {100 - fullPct:F0}% liber. Se recomandă cel puțin 10% liber.",
                                $"Wenig Speicher — {100 - fullPct:F0}% frei. Mindestens 10% frei wird empfohlen.",
                                $"Espace limité — {100 - fullPct:F0}% libre. Au moins 10% d'espace libre est recommandé.");
                    Grid.SetColumn(free, 3); row.Children.Add(free);

                    // Last column: "Clean up" for almost full partitions, otherwise badges
                    var tail = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                    if ((crit || warn) && !string.IsNullOrEmpty(letter))
                    {
                        var clean = new Wpf.Ui.Controls.Button
                        {
                            Content = L("Clean up", "Curățare", "Bereinigen", "Nettoyer"), Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
                            Padding = new Thickness(10, 3, 10, 3), FontSize = Ui.FontS,
                            ToolTip = L("Open Auto Clean — temporary files, caches, Windows Update leftovers", "Deschide Auto Clean — fișiere temporare, cache, resturi Windows Update", "Auto Clean öffnen", "Ouvrir Auto Clean"),
                        };
                        clean.Click += (s2, e2) => { ShortcutAutoClean_Click(s2, e2); };
                        tail.Children.Add(clean);
                    }
                    else
                    {
                        void MkBadge(string text, string hex)
                        {
                            var bc = (WpfColor)WpfColorConv.ConvertFromString(hex)!;
                            var b  = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 2, 5, 2), Margin = new Thickness(0, 0, 4, 0), Background = new SolidColorBrush(WpfColor.FromArgb(30, bc.R, bc.G, bc.B)) };
                            b.Child = new TextBlock { Text = text, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(bc) };
                            tail.Children.Add(b);
                        }
                        if (part.IsSystem) MkBadge("SYSTEM", "#F59E0B");
                        else if (part.IsActive) MkBadge("ACTIVE", "#22C55E");
                    }
                    Grid.SetColumn(tail, 4); row.Children.Add(tail);

                    var rowBorder = new Border
                    {
                        Padding = new Thickness(0, 7, 0, 7),
                        BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0),
                        Child = row,
                    };
                    rowBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush2");
                    if (!string.IsNullOrEmpty(letter))
                    {
                        string lt = letter;
                        rowBorder.Cursor = System.Windows.Input.Cursors.Hand;
                        rowBorder.Background = System.Windows.Media.Brushes.Transparent;
                        nameSp.ToolTip = L($"Open {lt} in Explorer", $"Deschide {lt} în Explorer", $"{lt} im Explorer öffnen", $"Ouvrir {lt} dans l'Explorateur");
                        rowBorder.MouseLeftButtonUp += (_, e3) =>
                        {
                            // clicking the Clean up button does not open Explorer too
                            if (e3.OriginalSource is DependencyObject d && FindAncestor<Wpf.Ui.Controls.Button>(d) != null) return;
                            OpenPartitionInExplorer(lt);
                        };
                    }
                    sp.Children.Add(rowBorder);
                }
            }

            outer.Child = sp;
            return outer;
        }


        internal void PanelRamScroll_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
        {
            if (_ramTableAdaptGuard) return;
            if (PanelRamScroll == null || FindName("RamGrid") is not System.Windows.Controls.DataGrid ramGrid) return;
            try
            {
                _ramTableAdaptGuard = true;

                double viewport = PanelRamScroll.ViewportHeight;
                double extent   = PanelRamScroll.ExtentHeight;
                if (viewport <= 0 || extent <= 0) return;

                double slack = viewport - extent;

                double currentH = ramGrid.ActualHeight > 0 ? ramGrid.ActualHeight : 140;
                double targetH;

                if (slack > 20) targetH = Math.Min(220, currentH + slack / 2.0);
                else if (slack < -10) targetH = Math.Max(140, currentH + slack / 2.0);
                else return;

                targetH = Math.Max(140, Math.Min(220, targetH));
                if (Math.Abs(targetH - currentH) < 4) return;

                ramGrid.Height = targetH;
            }
            catch (Exception ex) { AppLogger.Debug($"PanelRamScroll_ScrollChanged: {ex.Message}"); }
            finally { _ramTableAdaptGuard = false; }
        }

        private System.Windows.Threading.DispatcherTimer? _storageTempTimer;
        private int _storageTempTickCount = 0;
        private bool _storageTempBusy = false;

        private void StartStorageTempTimer()
        {
            if (_storageTempTimer != null) return;
            _storageTempTimer = new System.Windows.Threading.DispatcherTimer
            {
                // 10 s: each read starts a small smartctl process per disk; a disk temperature does not change meaningfully within 10 s.
                Interval = TimeSpan.FromSeconds(10)
            };
            _storageTempTimer.Tick += async (_, _) => await StorageTempTickAsync();
            _storageTempTimer.Start();
            // The first read immediately, not after 10 s — an NVMe card has no temperature until then.
            // Fire-and-forget: StorageTempTickAsync catches its own exceptions.
            _ = StorageTempTickAsync();
        }

        private async Task StorageTempTickAsync()
        {
            if (_currentPanel != "Disk") { StopStorageTempTimer(); return; }
            if (_storageTempBusy) return;   // the previous tick is still reading
            _storageTempBusy = true;
            try
            {
                var disks = _allDisks?.ToList();
                if (disks == null || disks.Count == 0) return;

                var results = await Task.WhenAll(disks.Select(async d => (Disk: d, TempC: await ReadStorageTempAsync(d))));
                if (_currentPanel != "Disk") return;  // the user left in the meantime

                foreach (var (disk, tempValue) in results)
                {
                    if (tempValue.HasValue) ApplyDiskTempToCard(disk, tempValue.Value);
                }

                // Snapshot into the SMART history every ~30 s (3 ticks x 10 s); SaveSnapshot also has its own 5-minute limit per disk.
                _storageTempTickCount++;
                if (_storageTempTickCount >= 3)
                {
                    _storageTempTickCount = 0;
                    foreach (var disk in _allDisks!)
                        _ = Task.Run(() => SmartHistoryService.SaveSnapshot(disk)); // fire-and-forget: SaveSnapshot catches its own exceptions
                }
            }
            catch (Exception ex) { AppLogger.Debug("StorageTempTickAsync: {Msg}", ex.Message); }
            finally { _storageTempBusy = false; }
        }

        /// <summary>The current temperature of a disk, or null if unknown (sleeping disk / no source). smartctl is the primary
        /// source (covers NVMe and packed fields); the WMI attribute stays only as a fallback when smartctl is missing or fails.</summary>
        private static async Task<double?> ReadStorageTempAsync(DiskHealthEntry disk)
        {
            try
            {
                if (SmartctlService.IsAvailable && !string.IsNullOrWhiteSpace(disk.PhysicalDevicePath)
                    && !SmartctlService.IsDeviceUnsupported(disk.PhysicalDevicePath))
                {
                    var reading = await SmartctlService.ReadTemperatureAsync(disk.PhysicalDevicePath);
                    if (reading.Celsius.HasValue) return reading.Celsius;
                    if (reading.InStandby) return null; // asleep — not woken, keep the shown value
                    AppLogger.Debug("ReadStorageTempAsync: smartctl gave no temperature for {Path}: {Msg}",
                        disk.PhysicalDevicePath, reading.ErrorMessage);
                }
            }
            catch (Exception ex) { AppLogger.Debug("ReadStorageTempAsync: {Msg}", ex.Message); }

            var attr = disk.SmartAttributes?.FirstOrDefault(a => a.Id == 0xC2)
                    ?? disk.SmartAttributes?.FirstOrDefault(a => a.Id == 0xBE);
            if (attr == null) return null;
            long lsb = attr.RawValue & 0xFF;
            return lsb > 0 && lsb < 100 ? lsb : null;
        }

        /// <summary>Puts the temperature on the disk card: text, colours, and shows it if it was hidden.</summary>
        private void ApplyDiskTempToCard(DiskHealthEntry disk, double tempC)
        {
            string key = DiskTempKey(disk);
            WpfColor c = TempStatusColor(tempC);
            if (FindName($"TxtDiskTemp_{key}") is TextBlock tb)
            {
                tb.Text = $"{TempUnit.Fmt(tempC)}";
                tb.Foreground = new SolidColorBrush(c);
            }
            if (FindName($"PillDiskTemp_{key}") is Border pill)
            {
                pill.Background  = new SolidColorBrush(WpfColor.FromArgb(20, c.R, c.G, c.B));
                pill.BorderBrush = new SolidColorBrush(WpfColor.FromArgb(60, c.R, c.G, c.B));
                pill.Visibility  = Visibility.Visible;
            }
        }

        // The same thresholds when building the card and on refresh.
        private static WpfColor TempStatusColor(double tempC) =>
            tempC >= 60 ? ThemeColor("StatusErrorColor",   WpfColor.FromRgb(239, 68, 68))
          : tempC >= 45 ? ThemeColor("StatusWarningColor", WpfColor.FromRgb(245, 158, 11))
          :               ThemeColor("StatusSuccessColor", WpfColor.FromRgb(34, 197, 94));

        // A unique key per disk for the element names in the card. The serial alone is not enough: disks without a serial
        // ("—") would get the same empty key, and characters like "." or "/" make the name invalid for RegisterName.
        private static string DiskTempKey(DiskHealthEntry disk)
        {
            string serial = new string((disk.SerialNumber ?? "").Where(char.IsLetterOrDigit).ToArray());
            return $"{disk.DiskIndex}_{serial}";
        }

        private void RegisterOrReplaceName(string name, object element)
        {
            try
            {
                if (FindName(name) != null) UnregisterName(name);
                RegisterName(name, element);
            }
            catch (Exception ex) { AppLogger.Debug("RegisterOrReplaceName({Name}): {Msg}", name, ex.Message); }
        }

        private void StopStorageTempTimer()
        {
            _storageTempTimer?.Stop();
            _storageTempTimer = null;
        }
    }
}
