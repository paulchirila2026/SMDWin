using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Services;
using WpfVAlign      = System.Windows.VerticalAlignment;

namespace SMDWin.Views
{
    /// <summary>
    /// System Information, the layouts with a column on the left and a list of rows on the right:
    /// <list type="bullet">
    /// <item>"Split" — "the system on the left, the hardware on the right": on the left a panel with sections (the computer,
    /// Windows, license, protection, power / battery) and, at the bottom, the "To check" list; on the right only the hardware
    /// components, one row per component;</item>
    /// <item>"List" — "the computer on the left, the components in the list": on the left only the computer card, on the
    /// right all the other cards as rows.</item>
    /// </list>
    /// The rows share the screen height, so the page does not stay with empty space below. On narrow windows the left panel
    /// moves above the list. The default layout is the table (SystemInfoView.TableLayout.cs); the choice is in Developer ›
    /// Design experiments.
    /// </summary>
    public partial class SystemInfoView
    {
        /// <summary>
        /// The layout chosen in Developer › Design experiments: "" / "Table" = header + table (default) · "TableGroups" = table in
        /// two groups · "Split" = the system on the left, the hardware on the right · "List" = the computer on the left, all the
        /// components as rows · "Grid" = the older card grid.
        /// </summary>
        private static string LayoutChoice => SettingsService.Current.DesignExperimentSysInfoLayout ?? "";
        private static bool UseMachineLayout => LayoutChoice is "List" or "Split";
        /// <summary>The system and Windows on the left, the hardware on the right.</summary>
        private static bool UseSplitLayout => LayoutChoice == "Split";

        // the cards that, in the default layout, become sections of the left panel (in this order)
        private static readonly string[] SystemSectionTags = { "Motherboard", "Windows", "Key", "Shield", "Bolt", "Battery" };
        private FrameworkElement? _machineLeft;

        private const string MachineLayoutTag = "MachineLayout";
        private const double MachinePanelWidth = 340;
        private const double MachineNarrowBelow = 1000;   // below this width, the computer card sits above the list
        private const double RowGrowLimit = 1.6;           // a row grows at most to 1.6 × its natural height

        private Border? _machineCard;
        private readonly List<Border> _machineRows = new();
        private bool _machineNarrow;
        private Grid? _machineList;

        private static string SI(string key, string en) => LanguageService.S("SysInfo", key, en);

        /// <summary>The left panel and the right rows, from the built cards (only once, after building).</summary>
        private void PrepareMachineLayout(List<Border> cards)
        {
            if (UseSplitLayout) { PrepareSplitLayout(cards); return; }
            _machineRows.Clear();
            _machineCard = cards.FirstOrDefault(c => c.Tag as string == "Motherboard");
            _machineLeft = _machineCard;
            foreach (var card in cards)
            {
                if (card == _machineCard) continue;
                HeroCards.ToRow(card);
                _machineRows.Add(card);
            }
            if (_machineCard != null)
            {
                HeroCards.ToTall(_machineCard, 240, 170);
                try { HeroCards.AddFooter(_machineCard, BuildAttentionPanel()); }
                catch (Exception ex) { AppLogger.Debug($"SystemInfo attention list: {ex.Message}"); }
            }
        }

        /// <summary>Lays out the computer card and the list (the same elements, only moved).</summary>
        private void ArrangeMachineLayout(bool narrow)
        {
            var host = _allCardsHost;
            if (host == null) return;
            foreach (var old in host.Children.OfType<Grid>()
                         .Where(g => g.Tag as string == MachineLayoutTag || g.Tag as string == HeroCards.RowTag).ToList())
            {
                old.Children.Clear();
                host.Children.Remove(old);
            }
            if (_machineList != null)
            {
                if (_machineList.Parent is Border oldListCard) oldListCard.Child = null;
                _machineList.Children.Clear();
            }

            var list = new Grid();
            for (int i = 0; i < _machineRows.Count; i++)
            {
                var row = _machineRows[i];
                list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                row.BorderThickness = new Thickness(0, 0, 0, i == _machineRows.Count - 1 ? 0 : 1);
                Grid.SetRow(row, i);
                list.Children.Add(row);
            }
            _machineList = list;

            var listCard = new Border
            {
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                Padding = new Thickness(20, 2, 20, 2), Child = list, VerticalAlignment = WpfVAlign.Top,
            };
            listCard.SetResourceReference(Border.BackgroundProperty, "CardSurfaceBrush");
            listCard.SetResourceReference(Border.BorderBrushProperty, "CardEdgeBrush");

            var layout = new Grid { Tag = MachineLayoutTag, Margin = new Thickness(0, 0, 0, 12) };
            var left = _machineLeft;
            if (left is { Parent: Grid oldGrid }) oldGrid.Children.Remove(left);
            if (left == null)
            {
                layout.Children.Add(listCard);
            }
            else if (!narrow)
            {
                layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MachinePanelWidth) });
                layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(left, 0); Grid.SetRow(left, 0);
                Grid.SetColumn(listCard, 2);
                layout.Children.Add(left);
                layout.Children.Add(listCard);
            }
            else
            {
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetColumn(left, 0); Grid.SetRow(left, 0);
                Grid.SetRow(listCard, 2);
                layout.Children.Add(left);
                layout.Children.Add(listCard);
            }
            host.Children.Insert(0, layout);
            _machineNarrow = narrow;
            _heroCols = 0;
        }

        /// <summary>
        /// The default layout: Windows, the license, protection and power move, as sections, under the computer in the left
        /// panel; only the hardware components stay on the right, each on a row as short as possible.
        /// </summary>
        private void PrepareSplitLayout(List<Border> cards)
        {
            _machineRows.Clear();
            _machineCard = null;
            var sections = SystemSectionTags
                .Select(tag => cards.FirstOrDefault(c => c.Tag as string == tag))
                .Where(c => c != null).Cast<Border>().ToList();
            foreach (var card in cards)
            {
                if (sections.Contains(card)) continue;
                HeroCards.ToRow(card);
                CompactHardwareRow(card);
                _machineRows.Add(card);
            }

            var stack = new StackPanel();
            foreach (var card in sections)
            {
                string tag = (string)card.Tag;
                HeroCards.ToSection(card, keepPicture: tag == "Motherboard", artW: 150, artH: 110);
                var facts = HeroCards.FindFacts(card);
                switch (tag)
                {
                    // the serial, the chassis type, the motherboard and the PC name; the BIOS and the boot mode stay in the PDF
                    case "Motherboard" when facts != null: HeroCards.TrimFacts(facts, 3, 1); break;
                    case "Windows": CompactWindowsSection(card); break;
                    case "Shield" when facts != null: HeroCards.TrimFacts(facts, 5); break;
                    case "Battery" when facts != null: HeroCards.TrimFacts(facts, 4); break;
                }
                stack.Children.Add(card);
            }
            if (sections.Count > 0) sections[^1].BorderThickness = new Thickness(0);

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(stack);
            try
            {
                var attention = BuildAttentionPanel();
                Grid.SetRow(attention, 2);
                grid.Children.Add(attention);
            }
            catch (Exception ex) { AppLogger.Debug($"SystemInfo attention list: {ex.Message}"); }

            var panel = new Border
            {
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                Padding = new Thickness(20, 4, 20, 16), Child = grid,
            };
            panel.SetResourceReference(Border.BackgroundProperty, "CardSurfaceBrush");
            panel.SetResourceReference(Border.BorderBrushProperty, "CardEdgeBrush");
            _machineLeft = panel;
        }

        /// <summary>The body of a list row (after <see cref="HeroCards.ToRow"/> it sits in the third column).</summary>
        private static FrameworkElement? RowBody(Border row)
            => (row.Child as Grid)?.Children.OfType<Grid>().FirstOrDefault(g => Grid.GetColumn(g) == 2)
                   ?.Children.OfType<FrameworkElement>().FirstOrDefault();

        /// <summary>
        /// A hardware row as short as possible: at most 4 facts per row (the processor has up to 6), the screens side by side,
        /// the network connections side by side, the peripherals on three columns.
        /// </summary>
        private static void CompactHardwareRow(Border row)
        {
            if (HeroCards.FindFacts(row) is { } facts)
            {
                facts.Single = false;
                HeroCards.TrimFacts(facts, 4);
            }
            var body = RowBody(row);
            switch (row.Tag as string)
            {
                case "Globe" when body is StackPanel net:
                    net.Orientation = System.Windows.Controls.Orientation.Horizontal;
                    foreach (var c in net.Children.OfType<FrameworkElement>()) c.Margin = new Thickness(0, 0, 28, 0);
                    break;
                case "Usb" when body is StackPanel usb:
                    foreach (var g in usb.Children.OfType<System.Windows.Controls.Primitives.UniformGrid>()) g.Columns = 3;
                    break;
            }
        }

        /// <summary>The Windows section: without the long timeline; the last update becomes a fact ("748 days ago").</summary>
        private void CompactWindowsSection(Border card)
        {
            if (card.Child is not Grid root
                || root.Children.OfType<Grid>().FirstOrDefault(g => Grid.GetRow(g) == 1) is not { } bodyHost
                || bodyHost.Children.Count == 0 || bodyHost.Children[0] is not StackPanel content) return;
            while (content.Children.Count > 1) content.Children.RemoveAt(content.Children.Count - 1);
            if (_winInfo?.LastUpdateOn is DateTime last && HeroCards.FindFacts(content) is { } facts)
            {
                int days = Math.Max(0, (int)(DateTime.Now - last).TotalDays);
                bool stale = days > UpdateStaleDays;
                facts.Children.Add(HeroFact("Clock", SpecSheet.Hue("Windows"),
                    days == 0 ? SI("Today", "today") : days == 1 ? SI("Yesterday", "yesterday") : string.Format(SI("DaysAgo", "{0} days ago"), days),
                    Ws("LastUpdate"), stale ? "StatusCheckBrush" : "TextPrimaryBrush", SMDWin.Services.DateFormatHelper.FormatDate(last).TrimStart('0')));
            }
        }

        /// <summary>
        /// Fitting on screen: narrow or wide, then, if everything fits, the list rows grow proportionally until they fill the
        /// page height (with a limit, so the rows do not become empty on very tall screens).
        /// </summary>
        private void FitMachineLayout(ScrollViewer scroll)
        {
            var host = _allCardsHost;
            if (host == null) return;
            bool narrow = scroll.ActualWidth < MachineNarrowBelow;
            bool hasLayout = host.Children.OfType<Grid>().Any(g => g.Tag as string == MachineLayoutTag);
            if (narrow != _machineNarrow || !hasLayout) ArrangeMachineLayout(narrow);
            _currentColumns = narrow ? 1 : 2;
            host.LayoutTransform = Transform.Identity;

            var list = _machineList;
            if (list == null) return;
            foreach (var rd in list.RowDefinitions) rd.Height = GridLength.Auto;
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            if (narrow) return;

            UpdateLayout();
            double natural = list.ActualHeight;
            var layout = host.Children.OfType<Grid>().FirstOrDefault(g => g.Tag as string == MachineLayoutTag);
            double others = host.Children.OfType<FrameworkElement>()
                .Where(c => c != layout)
                .Sum(c => c.ActualHeight + c.Margin.Top + c.Margin.Bottom);
            // 16 px the bottom page margin, 10 px reserve for rounding, 12 px the layout margin, 6 px the border and the list spacing
            double available = scroll.ActualHeight - 16 - 10 - 12 - 6 - others;
            double machineHeight = 0;
            if (_machineLeft != null)
            {
                _machineLeft.Measure(new System.Windows.Size(MachinePanelWidth, double.PositiveInfinity));
                machineHeight = _machineLeft.DesiredSize.Height;
            }
            bool fits = natural > 0 && natural <= available && machineHeight <= available + 6;
            scroll.VerticalScrollBarVisibility = fits ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
            if (!fits) return;

            double scale = Math.Min(RowGrowLimit, available / natural);
            for (int i = 0; i < list.RowDefinitions.Count; i++)
            {
                double h = list.RowDefinitions[i].ActualHeight;
                if (h > 0) list.RowDefinitions[i].Height = new GridLength(Math.Floor(h * scale));
            }
            if (list.Parent is Border listCard) listCard.VerticalAlignment = scale < RowGrowLimit ? WpfVAlign.Stretch : WpfVAlign.Top;
        }

        /// <summary>
        /// The problems that go into "To check": almost full volumes, Windows without security updates or not updated for a long
        /// time, protections turned off. The text and the state colour (the brush key).
        /// </summary>
        private List<(string Text, string BrushKey)> GetAttentionItems()
        {
            var items = new List<(string Text, string BrushKey)>();

            foreach (var d in _tileDisks.Where(d => d.SizeBytes > 0))
                foreach (var p in d.Partitions.Where(p => p.TotalGB >= 0.5 && !string.IsNullOrWhiteSpace(p.Letter)))
                {
                    double used = Math.Clamp((p.TotalGB - p.FreeGB) / p.TotalGB * 100, 0, 100);
                    if (used < StatusRules.DiskWarnPct) continue;
                    items.Add((string.Format(SI("AttentionDiskLow", "{0} almost full · {1} free"), p.Letter.TrimEnd('\\'), DisplayFormat.SizeGb(p.FreeGB)),
                               used >= StatusRules.DiskCritPct ? "StatusProblemBrush" : "StatusCheckBrush"));
                }

            if (_winInfo is { } w)
            {
                if (WindowsFacts.SupportFor(w.BuildNumber, w.Edition) != WindowsSupport.Supported)
                    items.Add((Ws("SupportEnded"), "StatusProblemBrush"));
                if (w.LastUpdateOn is DateTime last)
                {
                    int days = Math.Max(0, (int)(DateTime.Now - last).TotalDays);
                    if (days > UpdateStaleDays)
                        items.Add((string.Format(SI("AttentionUpdateStale", "No Windows update for {0} days"), days), "StatusCheckBrush"));
                }
                bool? tpm = w.TpmRead ? !string.IsNullOrEmpty(w.TpmVersion) : (bool?)null;
                bool? av = w.AntivirusRead ? w.Antivirus is { Enabled: true, UpToDate: true } : (bool?)null;
                var (active, known) = WindowsFacts.ProtectionScore(new bool?[] { w.SecureBoot, tpm, av, w.Firewall, w.DiskEncrypted });
                if (known > 0 && active < known)
                    items.Add((string.Format(SI("AttentionProtection", "{0} of {1} protections active"), active, known),
                               av == false ? "StatusProblemBrush" : "StatusCheckBrush"));
            }
            return items;
        }

        /// <summary>The "To check" list. Without problems it is not shown at all (the "Nothing needs attention" banner
        /// only took space). <paramref name="withLine"/> = a fine line above.</summary>
        private FrameworkElement BuildAttentionPanel(bool withLine = true)
        {
            var items = GetAttentionItems();
            if (items.Count == 0) return new Border { Visibility = Visibility.Collapsed };
            // under a line (machine card) the list sits at the bottom; alone in a header box it is centred like the other boxes
            var panel = new StackPanel { Margin = new Thickness(0, withLine ? 14 : 0, 0, 0), VerticalAlignment = withLine ? WpfVAlign.Bottom : WpfVAlign.Center };
            if (withLine)
            {
                var line = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 12) };
                line.SetResourceReference(Border.BackgroundProperty, "CardBorderBrush");
                panel.Children.Add(line);
            }

            string title = SI("AttentionTitle", "To check");
            var head = HeroText(items.Count > 0 ? $"{title} · {items.Count}" : title, Ui.FontXS, "TextSecondaryBrush", semibold: true);
            head.Margin = new Thickness(0, 0, 0, 8);
            panel.Children.Add(head);

            foreach (var (text, brushKey) in items)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = WpfVAlign.Center };
                dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brushKey);
                row.Children.Add(dot);
                var t = HeroText(text, Ui.FontS, "TextPrimaryBrush", wrap: true);
                t.VerticalAlignment = WpfVAlign.Center;
                Grid.SetColumn(t, 1);
                row.Children.Add(t);
                panel.Children.Add(row);
            }
            return panel;
        }
    }
}
