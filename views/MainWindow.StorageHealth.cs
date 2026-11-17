using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SMDWin.Models;
using SMDWin.Services;
using Border      = System.Windows.Controls.Border;
using TextBlock   = System.Windows.Controls.TextBlock;
using FontFamily  = System.Windows.Media.FontFamily;
using HAlign      = System.Windows.HorizontalAlignment;
using VAlign      = System.Windows.VerticalAlignment;
using UGrid       = System.Windows.Controls.Primitives.UniformGrid;

namespace SMDWin.Views
{
    /// <summary>
    /// Storage › Overview: on each disk, under the picture and partitions, a strip with always-available information (does not
    /// depend on tests run): health and remaining life, temperature, SMART in short, usage (data written / read, power-on
    /// hours, power cycles). The data comes from the SMART attributes already read (ATA by ID, NVMe by name). A disk without
    /// SMART (USB stick, some adapters) gets a single sentence, without invented numbers.
    /// </summary>
    public partial class MainWindow
    {
        private static string DS(string key, string en) => LanguageService.S("Disk", key, en);

        /// <summary>The figures of a disk, gathered once (ATA or NVMe). Null = the disk does not report the value.</summary>
        private sealed class DriveFacts
        {
            public bool HasSmart;
            public int SmartCount;
            public List<string> WarnNames = new();
            public int? LifeLeft;
            public int? TempC;
            public string? TempRaw;          // NVMe: the text read, when it cannot be converted to °C
            public long? ReadErrors;
            public long? Replaced;           // ATA 0x05
            public string? SpareLeft;        // NVMe „Available Spare”
            public long? Pending;            // ATA 0xC5
            public long? UnsafeShutdowns;
            public long? Hours;
            public long? Cycles;
            public string? Written, Read;
            public bool IsHdd;
        }

        private static DriveFacts CollectDriveFacts(DiskHealthEntry d)
        {
            var f = new DriveFacts();
            try { HardwareService.MarkNvmeDataUnits(d); } catch (Exception ex) { AppLogger.Debug($"MarkNvmeDataUnits: {ex.Message}"); }
            SmartAttributeEntry? A(params byte[] ids) => d.SmartAttributes.FirstOrDefault(x => ids.Contains(x.Id));
            NvmeHealthRow? N(string name) =>
                d.NvmeHealthRows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? d.NvmeHealthRows.FirstOrDefault(r => (r.Name ?? "").StartsWith(name, StringComparison.OrdinalIgnoreCase));

            f.SmartCount = d.SmartAttributes.Count + d.NvmeHealthRows.Count;
            f.HasSmart = f.SmartCount > 0;
            f.IsHdd = d.RotationRpm > 0 || (d.Kind ?? "").Contains("HDD", StringComparison.OrdinalIgnoreCase);
            f.WarnNames = d.SmartAttributes.Where(a => a.TrafficLightColor != "#22C55E").Select(a => a.Name)
                .Concat(d.NvmeHealthRows.Where(r => r.TrafficLightColor != "#22C55E").Select(r => r.Name)).ToList();

            // remaining life: the normalized value (100 → 0) of the first wear attribute found; on NVMe 100 − "Percentage Used"
            if (!f.IsHdd)
            {
                var wear = A(0xE7) ?? A(0xE9) ?? A(0xB1) ?? A(0xAD);
                if (wear != null && wear.CurrentValue is > 0 and <= 100) f.LifeLeft = wear.CurrentValue;
                else if (ParseLong(N("Percentage Used")?.RawDisplay) is long used && used is >= 0 and <= 100) f.LifeLeft = (int)(100 - used);
            }

            if (A(0xC2, 0xBE) is { } t) f.TempC = (int)(t.RawValue & 0xFF);
            else if (N("Composite Temperature")?.RawDisplay is string nt)
            {
                f.TempRaw = nt;
                if (nt.Contains('C') && ParseLong(nt) is long c && c is > 0 and < 130) f.TempC = (int)c;
            }
            if (f.TempC is <= 0 or > 129) f.TempC = null;

            f.ReadErrors = A(0xBB, 0xC6)?.RawValue ?? ParseLong(N("Media and Data Integrity")?.RawDisplay);
            f.Replaced = A(0x05)?.RawValue;
            if (f.Replaced == null) f.SpareLeft = N("Available Spare")?.RawDisplay;
            f.Pending = A(0xC5)?.RawValue;
            f.UnsafeShutdowns = A(0xC0, 0xAE)?.RawValue ?? ParseLong(N("Unsafe Shutdown")?.RawDisplay);
            f.Hours = A(0x09)?.RawValue ?? ParseLong(N("Power-On Hours")?.RawDisplay);
            f.Cycles = A(0x0C)?.RawValue ?? ParseLong(N("Power Cycles")?.RawDisplay);

            var wr = A(0xF1); var rd = A(0xF2);
            if (wr != null && wr.RawValue > 0) f.Written = LifetimeData.Format(SsdLifeDetector.AttributeToBytes(wr));
            else f.Written = N("Data Units Written")?.RawDisplay;
            if (rd != null && rd.RawValue > 0) f.Read = LifetimeData.Format(SsdLifeDetector.AttributeToBytes(rd));
            else f.Read = N("Data Units Read")?.RawDisplay;
            return f;
        }

        /// <summary>The first integer in the text ("1,234", "3%", "6 520 h"); null if there is none.</summary>
        private static long? ParseLong(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var digits = new System.Text.StringBuilder();
            bool started = false;
            foreach (char ch in s)
            {
                if (char.IsDigit(ch)) { digits.Append(ch); started = true; }
                else if (started && (ch is ',' or '.' or ' ' or ' ' or ' ')) continue;   // thousands separators
                else if (started) break;
            }
            return digits.Length > 0 && long.TryParse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out long v) ? v : null;
        }

        /// <summary>The power-on hours as in SMART (days rounded the same way in both places).</summary>
        internal static string PoweredOnText(long hours) =>
            hours < 48 ? $"{hours:N0} h"
            : hours >= 24 * 730
                ? $"{hours:N0} h · {hours / (24.0 * 365):0.#} {DS("Years", "years")}"
                : $"{hours:N0} h · {hours / 24.0:N0} {DS("Days", "days")}";

        // ── The strip on the disk card ───────────────────────────────────────

        private FrameworkElement MakeDriveHealthBand(DiskHealthEntry d)
        {
            var f = CollectDriveFacts(d);
            if (!f.HasSmart)
            {
                var none = Txt2(DS("NoSmartBand", "This drive does not report health data (SMART), so there are no values to show. This is common for USB sticks, memory cards and some adapters."),
                                "TextSecondaryBrush", Ui.FontS);
                none.TextWrapping = TextWrapping.Wrap;
                none.Margin = new Thickness(0, 0, 0, 16);
                return none;
            }

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            for (int i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            AddBandCell(grid, 0, HealthCell(d, f));
            AddBandCell(grid, 1, TemperatureCell(f));
            AddBandCell(grid, 2, SmartCell(d, f));
            AddBandCell(grid, 3, UsageCell(f));
            return grid;
        }

        private static void AddBandCell(Grid grid, int col, FrameworkElement content)
        {
            var cell = new Border { Child = content, Padding = new Thickness(col == 0 ? 0 : 18, 2, 14, 2), BorderThickness = new Thickness(col == 0 ? 0 : 1, 0, 0, 0) };
            cell.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            Grid.SetColumn(cell, col);
            grid.Children.Add(cell);
        }

        private TextBlock BandKey(string text)
        {
            var t = new TextBlock { Text = text, Style = TryFindResource("CompFootKey") as Style, Margin = new Thickness(0, 0, 0, 6) };
            return t;
        }

        private TextBlock BandBig(string text, string brushKey = "TextPrimaryBrush")
        {
            var t = new TextBlock { Text = text, FontFamily = TryFindResource("NumFont") as FontFamily, FontSize = 26, FontWeight = FontWeights.SemiBold,
                                    TextTrimming = TextTrimming.CharacterEllipsis };
            t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return t;
        }

        /// <summary>A "label …… value" row (the value right-aligned, coloured only when there is a problem).</summary>
        private FrameworkElement BandRow(string label, string value, string valueBrush = "TextPrimaryBrush", string? tip = null)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), ToolTip = tip };
            var v = new TextBlock { Text = value, FontFamily = TryFindResource("NumFont") as FontFamily, FontSize = Ui.FontM, FontWeight = FontWeights.SemiBold,
                                    Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VAlign.Center };
            v.SetResourceReference(TextBlock.ForegroundProperty, valueBrush);
            DockPanel.SetDock(v, Dock.Right);
            row.Children.Add(v);
            var l = Txt2(label, "TextSecondaryBrush", Ui.FontS);
            l.VerticalAlignment = VAlign.Center;
            row.Children.Add(l);
            return row;
        }

        // 1. sanatatea: cadranul + viata ramasa + verdictul in cuvinte
        private FrameworkElement HealthCell(DiskHealthEntry d, DriveFacts f)
        {
            int hp = d.HealthPercent;
            // the dial shows the life left when the drive reports its wear (one figure, not "100 % health" next to "92 % life");
            // otherwise the health worked out from the SMART values
            int dialValue = f.LifeLeft ?? hp;
            var dock = new DockPanel();
            var dialGrid = new Grid { Style = TryFindResource("CompDialGrid") as Style };
            dialGrid.Children.Add(new PercentGauge { ShowValue = false, Segments = 20, WarnAt = 101, CritAt = 101, HasValue = dialValue > 0, Value = dialValue });
            var dialText = new StackPanel { Style = TryFindResource("CompDialText") as Style };
            dialText.Children.Add(new TextBlock { Style = TryFindResource("CompDialUnitGhost") as Style });
            dialText.Children.Add(new TextBlock { Style = TryFindResource("CompDialValue") as Style, Text = dialValue > 0 ? dialValue.ToString(CultureInfo.InvariantCulture) : "—" });
            dialText.Children.Add(new TextBlock { Style = TryFindResource("CompDialUnit") as Style });
            dialGrid.Children.Add(dialText);
            dialGrid.Children.Add(new TextBlock { Style = TryFindResource("CompDialSub") as Style, Text = f.LifeLeft.HasValue ? DS("LifeWord", "life left") : DS("HealthWord", "health") });
            var dial = new Viewbox { Width = 98, Height = 88, Child = dialGrid, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VAlign.Center };
            DockPanel.SetDock(dial, Dock.Left);
            dock.Children.Add(dial);

            var right = new StackPanel { VerticalAlignment = VAlign.Center };
            string statusKey = hp >= 90 ? "StatusGoodBrush" : hp >= 60 ? "StatusCheckBrush" : "StatusProblemBrush";
            // the life left is in the dial; next to it, the state in one word
            right.Children.Add(BandKey(DS("StatusWord", "Status")));
            right.Children.Add(BandBig(hp >= 90 ? DS("Healthy", "Healthy") : hp >= 60 ? DS("Watch", "Watch") : DS("Failing", "Failing"), statusKey));
            string verdict = f.WarnNames.Count == 0
                ? string.Format(DS("AllWithinLimits", "All {0} SMART values within limits"), f.SmartCount)
                : string.Format(DS("ValuesNeedAttention", "{0} of {1} values need attention"), f.WarnNames.Count, f.SmartCount);
            var v = Txt2(verdict, f.WarnNames.Count == 0 ? "TextSecondaryBrush" : "StatusCheckBrush", Ui.FontS);
            v.TextWrapping = TextWrapping.Wrap;
            v.Margin = new Thickness(0, 4, 0, 0);
            if (f.WarnNames.Count > 0) v.ToolTip = string.Join("\n", f.WarnNames.Take(8));
            right.Children.Add(v);
            dock.Children.Add(right);
            return dock;
        }

        // 2. temperature: the value, the zone (normal / high / too high) and a thin bar on the disk type's scale
        private FrameworkElement TemperatureCell(DriveFacts f)
        {
            var sp = new StackPanel();
            sp.Children.Add(BandKey(DS("Temperature", "Temperature")));
            if (f.TempC is not int t)
            {
                sp.Children.Add(BandBig(string.IsNullOrWhiteSpace(f.TempRaw) ? "—" : f.TempRaw!));
                sp.Children.Add(Txt2(DS("TempNotReported", "Not reported by the drive"), "TextSecondaryBrush", Ui.FontS));
                return sp;
            }
            // SSD: normal below 55 °C, too hot from 70 °C; HDD: normal below 46 °C, too hot from 55 °C
            int warn = f.IsHdd ? 46 : 55, crit = f.IsHdd ? 55 : 70, scale = f.IsHdd ? 70 : 85;
            string key = t >= crit ? "StatusProblemBrush" : t >= warn ? "StatusCheckBrush" : "TextPrimaryBrush";
            sp.Children.Add(BandBig(TempUnit.Fmt(t, 0, true), key));
            string zone = t >= crit ? DS("TempHot", "Too hot") : t >= warn ? DS("TempWarm", "Warm") : DS("TempNormal", "Normal");
            string limit = string.Format(DS("TempNormalBelow", "normal below {0}"), TempUnit.Fmt(warn, 0, true));
            var z = Txt2($"{zone} · {limit}", t >= warn ? key : "TextSecondaryBrush", Ui.FontS);
            z.Margin = new Thickness(0, 2, 0, 8);
            sp.Children.Add(z);
            var bar = new UGrid { Rows = 1, Height = 5 };
            FillBlocks(bar, Math.Clamp(t * 100.0 / scale, 0, 100), 24, key == "TextPrimaryBrush" ? null : key);
            bar.ToolTip = string.Format(DS("TempScaleTip", "Scale 0–{0}"), TempUnit.Fmt(scale, 0, true));
            sp.Children.Add(bar);
            return sp;
        }

        // 3. SMART in short: the errors that matter + sudden power losses, with a link to the full table
        private FrameworkElement SmartCell(DiskHealthEntry d, DriveFacts f)
        {
            var sp = new StackPanel();
            sp.Children.Add(BandKey("SMART"));
            static string Num(long v) => v.ToString("N0", CultureInfo.CurrentCulture);
            if (f.ReadErrors is long re)
                sp.Children.Add(BandRow(DS("ReadErrors", "Read errors"), Num(re), re > 0 ? "StatusProblemBrush" : "TextPrimaryBrush",
                    DS("ReadErrorsTip", "Data the drive could not read back. Anything above 0 means a backup is urgent.")));
            if (f.Replaced is long rp)
                sp.Children.Add(BandRow(DS("ReplacedSectors", "Replaced sectors"), Num(rp), rp > 0 ? "StatusCheckBrush" : "TextPrimaryBrush",
                    DS("ReplacedSectorsTip", "Damaged areas the drive replaced with spare ones. A growing number is a warning sign.")));
            else if (!string.IsNullOrWhiteSpace(f.SpareLeft))
                sp.Children.Add(BandRow(DS("SpareLeft", "Spare left"), f.SpareLeft!, "TextPrimaryBrush",
                    DS("SpareLeftTip", "Reserve blocks still available to replace worn ones.")));
            if (f.Pending is long pe && pe > 0)
                sp.Children.Add(BandRow(DS("PendingSectors", "Sectors waiting"), Num(pe), "StatusCheckBrush",
                    DS("PendingSectorsTip", "Areas the drive could not read and will check again.")));
            if (f.UnsafeShutdowns is long us)
                sp.Children.Add(BandRow(DS("UnsafeShutdowns", "Unsafe shutdowns"), Num(us), "TextPrimaryBrush",
                    DS("UnsafeShutdownsTip", "Times the power was cut while the drive was running (power loss, holding the power button). Not an error by itself.")));

            var link = new TextBlock { Text = string.Format(DS("AllValuesLink", "All {0} values ›"), f.SmartCount), FontSize = Ui.FontS, Margin = new Thickness(0, 6, 0, 0),
                                       Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HAlign.Left };
            link.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            link.MouseLeftButtonUp += (_, _) => OpenSmartFor(d);
            sp.Children.Add(link);
            return sp;
        }

        // 4. usage: data written / read, power-on hours, starts
        private FrameworkElement UsageCell(DriveFacts f)
        {
            var sp = new StackPanel();
            sp.Children.Add(BandKey(DS("Usage", "Usage")));
            if (!string.IsNullOrWhiteSpace(f.Written)) sp.Children.Add(BandRow(DS("DataWritten", "Data written"), f.Written!));
            if (!string.IsNullOrWhiteSpace(f.Read)) sp.Children.Add(BandRow(DS("DataRead", "Data read"), f.Read!));
            if (f.Hours is long h && h > 0) sp.Children.Add(BandRow(DS("PoweredOn", "Powered on"), PoweredOnText(h), "TextPrimaryBrush",
                DS("PoweredOnTip", "Total time the drive has been running, not its age.")));
            if (f.Cycles is long c) sp.Children.Add(BandRow(DS("PowerCycles", "Times turned on"), c.ToString("N0", CultureInfo.CurrentCulture)));
            if (sp.Children.Count == 1) sp.Children.Add(Txt2(DS("UsageNotReported", "Not reported by the drive"), "TextSecondaryBrush", Ui.FontS));
            return sp;
        }

        /// <summary>"All values ›": the SMART tab, with this disk chosen.</summary>
        private void OpenSmartFor(DiskHealthEntry d)
        {
            try
            {
                if (BtnStorageTabSmart != null) StorageTab_Click(BtnStorageTabSmart, new RoutedEventArgs());
                int i = _allDisks.FindIndex(x => ReferenceEquals(x, d));
                if (i < 0) i = _allDisks.FindIndex(x => x.DiskIndex == d.DiskIndex && string.Equals(x.SerialNumber, d.SerialNumber, StringComparison.Ordinal));
                if (i >= 0 && CbSmartDisk != null && i < CbSmartDisk.Items.Count) CbSmartDisk.SelectedIndex = i;
            }
            catch (Exception ex) { AppLogger.Debug($"OpenSmartFor: {ex.Message}"); }
        }
    }
}
