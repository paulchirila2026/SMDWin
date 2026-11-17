using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SMDWin.Models;
using SMDWin.Services;
using WpfColor       = System.Windows.Media.Color;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfHAlign      = System.Windows.HorizontalAlignment;
using WpfVAlign      = System.Windows.VerticalAlignment;
using WpfViewbox     = System.Windows.Controls.Viewbox;
using WpfPanel       = System.Windows.Controls.Panel;

namespace SMDWin.Views
{
    /// <summary>
    /// System Information, the component cards: Processor, Memory, Storage (here), Graphics card, Display, Motherboard,
    /// Network, Battery, Peripherals (SystemInfoView.HeroDevices.cs). Each is an illustration of the component + a few large
    /// facts, instead of 10–20 rows of text. The PDF export and "Copy all" still use the text rows. The card is built with
    /// <see cref="HeroCards"/>, as in the Windows tab.
    /// </summary>
    public partial class SystemInfoView
    {
        // Completate in BuildAllCards
        private bool _heroLaptop;
        private bool _heroIntegratedGraphics;

        private static TextBlock HeroText(string text, double size, string brushKey, bool semibold = false, bool wrap = false)
            => HeroCards.Text(text, size, brushKey, semibold, wrap);

        private static FrameworkElement HeroFact(string iconKey, WpfColor hue, string value, string label,
                                                 string valueBrushKey = "TextPrimaryBrush", string? tip = null)
            => HeroCards.Fact(iconKey, hue, value, label, valueBrushKey, tip);

        /// <summary>The card of a component. A non-empty <paramref name="searchTerm"/> = clicking the name searches the
        /// component on the internet.</summary>
        private Border HeroCard(string iconKey, string caption, string title, string? searchTerm, UIElement body)
        {
            bool search = !string.IsNullOrWhiteSpace(searchTerm);
            return HeroCards.Card(iconKey, caption, title, body, new HeroCards.CardOptions
            {
                TitleTip = search
                    ? L($"Search Google: {searchTerm}", $"Caută pe Google: {searchTerm}", $"Bei Google suchen: {searchTerm}", $"Rechercher sur Google : {searchTerm}")
                    : null,
                OnTitleClick = search ? () => OpenGoogleSearch(searchTerm!) : null,
            });
        }

        /// <summary>
        /// The component cards, in rows. Returns the keys of the sections shown this way (they no longer appear in the sheet
        /// below). A card that cannot be built is missing, and its sections stay in the sheet.
        /// </summary>
        private HashSet<string> BuildHeroCards(StackPanel host, int columns)
        {
            var shown = new HashSet<string>();
            var cards = new List<Border>();
            void Add(Func<Border?> build, params string[] keys)
            {
                try
                {
                    var card = build();
                    if (card == null) return;
                    cards.Add(card);
                    foreach (string key in keys) shown.Add(key);
                }
                catch (Exception ex) { AppLogger.Warning(ex, $"System Information card {keys[0]}"); }
            }
            Add(BuildBoardHero, "Motherboard", "Chassis");
            Add(BuildCpuHero, "CPU");
            Add(BuildMemoryHero, "Memory");
            Add(BuildStorageHero, "Storage");
            Add(BuildGraphicsHero, "Graphics");
            Add(BuildDisplaysHero, "Displays");
            Add(BuildNetworkHero, "Network");
            Add(BuildPeripheralsHero, "Peripherals");
            Add(BuildBatteryHero, "Battery");   // with a battery: the battery; without: mains power
            // at the end: Windows, license, protection (they appear after the Windows data is read)
            if (_winInfo is { } win)
            {
                Add(() => BuildWindowsOsCard(win), "WindowsOs");
                Add(() => BuildLicenseCard(win), "WindowsLicense");
                Add(() => BuildProtectionCard(win), "WindowsProtection");
            }
            _heroCards = cards;
            if (UseTableLayout)
                BuildTableLayout(cards);   // antet + tabel (SystemInfoView.TableLayout.cs)
            else if (UseMachineLayout)
            {
                // the computer on the left, the rest of the components as rows of a list (SystemInfoView.MachineLayout.cs)
                PrepareMachineLayout(cards);
                ArrangeMachineLayout(_machineNarrow);
            }
            else
                ArrangeHeroRows(Math.Clamp(_heroCols > 0 ? _heroCols : columns, 1, Math.Max(1, cards.Count)));
            return shown;
        }

        // ── Processor ─────────────────────────────────────────────────────────
        private Border? BuildCpuHero()
        {
            var s = _tileSummary;
            if (s == null || string.IsNullOrWhiteSpace(s.Cpu)) return null;
            var hue = SpecSheet.Hue("Cpu");
            CpuSpec? db = null;
            try { db = HardwareDatabaseService.LookupCpu(s.Cpu); } catch (Exception ex) { AppLogger.Debug($"CPU DB lookup: {ex.Message}"); }

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var picture = HardwareIllustrations.Cpu(s.Cpu, _heroLaptop, db?.Socket, false);
            picture.Width = HeroCards.ArtW; picture.Height = HeroCards.ArtH;
            picture.Margin = new Thickness(0, 0, 14, 0);
            picture.VerticalAlignment = WpfVAlign.Top;
            body.Children.Add(picture);

            var facts = HeroCards.FactsGrid();
            Grid.SetColumn(facts, 1); body.Children.Add(facts);

            // frequency: base (from Windows) + turbo (from the hardware database, if the processor is there)
            string clock = CpuPackage.ClockText(s.CpuMaxMHz);
            if (clock.Length > 0)
                facts.Children.Add(HeroFact("Clock", hue, clock,
                    db?.BoostClockGhz is double boost && boost > 0
                        ? L($"base · turbo up to {boost:0.0#} GHz", $"de bază · turbo până la {boost:0.0#} GHz", $"Basis · Turbo bis {boost:0.0#} GHz", $"base · turbo jusqu'à {boost:0.0#} GHz")
                        : L("base clock", "frecvență de bază", "Basistakt", "fréquence de base")));

            // cores / threads; on hybrid processors also the performance / efficiency split
            int cores = 0, threads = s.CpuThreads;
            var numbers = System.Text.RegularExpressions.Regex.Matches(s.CpuCores ?? "", @"\d+");
            if (numbers.Count > 0) int.TryParse(numbers[0].Value, out cores);
            if (threads <= 0 && numbers.Count > 1) int.TryParse(numbers[1].Value, out threads);
            if (threads <= 0) threads = Environment.ProcessorCount;
            var hybrid = CpuPackage.HybridCores(s.Cpu, cores, threads);
            if (cores > 0)
                facts.Children.Add(HeroFact("Cpu", hue,
                    L($"{cores} cores", $"{cores} nuclee", $"{cores} Kerne", $"{cores} cœurs")
                        + (threads > 0 ? L($" · {threads} threads", $" · {threads} fire", $" · {threads} Threads", $" · {threads} threads") : ""),
                    hybrid is (int p, int e)
                        ? L($"{p} performance + {e} efficiency", $"{p} de performanță + {e} de eficiență", $"{p} Performance + {e} Effizienz", $"{p} performance + {e} efficacité")
                        : ""));

            if (!string.IsNullOrWhiteSpace(s.CpuCache))
                facts.Children.Add(HeroFact("Layers", hue, s.CpuCache.Replace(": ", " ").Replace("  ", " · "), L("cache", "cache", "Cache", "cache")));

            if (db != null)
            {
                if (db.TdpW is double tdp && tdp > 0)
                    facts.Children.Add(HeroFact("Bolt", hue, $"{tdp:0} W", L("rated power (TDP)", "putere nominală (TDP)", "Nennleistung (TDP)", "puissance nominale (TDP)")));
                string where = string.Join(" · ", new[] { db.Lithography, db.Microarchitecture }.Where(x => !string.IsNullOrWhiteSpace(x)));
                if (!string.IsNullOrWhiteSpace(db.Socket))
                    facts.Children.Add(HeroFact("Socket", hue, db.Socket, where));
                else if (where.Length > 0)
                    facts.Children.Add(HeroFact("Socket", hue, where, ""));
            }

            // turbo: the state in the active power plan (a "Disabled" there keeps the processor at the base frequency)
            try
            {
                var boostMode = PowerAdvancedService.GetBoostMode(true);
                if (boostMode.HasValue)
                {
                    bool off = boostMode.Value == PowerAdvancedService.BoostMode.Disabled;
                    facts.Children.Add(HeroFact("Gauge", hue,
                        off ? L("Off", "Oprit", "Aus", "Désactivé") : L("On", "Pornit", "An", "Activé"),
                        "Turbo",
                        off ? "StatusWarningBrush" : "TextPrimaryBrush",
                        off ? L("The power plan keeps the processor at its base clock (Processor Boost Mode = Disabled). It runs cooler and quieter, but slower. Change it from the Power page.",
                                "Planul de energie ține procesorul la frecvența de bază (Processor Boost Mode = Disabled). Merge mai rece și mai silențios, dar mai încet. Se schimbă din pagina Power.",
                                "Der Energiesparplan hält den Prozessor auf dem Basistakt (Processor Boost Mode = Disabled). Kühler und leiser, aber langsamer. Änderbar auf der Seite Power.",
                                "Le mode d'alimentation maintient le processeur à sa fréquence de base (Processor Boost Mode = Disabled). Plus frais et plus silencieux, mais plus lent. Modifiable depuis la page Power.")
                            : null));
                }
            }
            catch (Exception ex) { AppLogger.Debug($"CPU hero boost mode: {ex.Message}"); }


            return HeroCard("Cpu", L("Processor", "Procesor", "Prozessor", "Processeur"), s.Cpu, s.Cpu, body);
        }

        // ── Memorie ──────────────────────────────────────────────────────────
        private Border? BuildMemoryHero()
        {
            var s = _tileSummary;
            var modules = _tileRams.Where(r => !r.IsEmpty).ToList();
            if (s == null || modules.Count == 0) return null;
            var hue = SpecSheet.Hue("Ram");

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // the drawing: always a single module (the first one), at its real proportions, centred in the same place and at the
            // same size as the other cards' illustrations. How many modules there are and in which slots shows below, in labels.
            bool soDimm = DimmLayout.IsSoDimm(modules[0].FormFactor ?? "");
            var stick = HardwareIllustrations.Dimm(modules[0].MemoryType, modules[0].FormFactor);
            stick.Width = soDimm ? 150 : 220; stick.Height = 62;
            var picSlot = new WpfViewbox
            {
                Width = HeroCards.ArtW, Height = HeroCards.ArtH, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both,
                VerticalAlignment = WpfVAlign.Top, Margin = new Thickness(0, 0, 14, 0), Child = stick,
            };
            body.Children.Add(picSlot);

            // the board slots, in order: one label per slot — the module capacity or "free"
            var slots = _tileRams.Take(8).ToList();
            if (slots.Count > 1)
            {
                var map = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
                foreach (var m in slots)
                {
                    bool free = m.IsEmpty;
                    int mhz = m.ConfiguredMHz > 0 ? m.ConfiguredMHz : m.RatedMHz;
                    string text = free ? L("free", "liber", "frei", "libre") : (string.IsNullOrWhiteSpace(m.Capacity) || m.Capacity == "—" ? "RAM" : m.Capacity);
                    var label = HeroText(text, Ui.FontXS, free ? "TextSecondaryBrush" : "TextPrimaryBrush", semibold: !free);
                    var pill = new Border
                    {
                        CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 4),
                        BorderThickness = new Thickness(1), Child = label, Opacity = free ? 0.75 : 1,
                    };
                    if (free)
                    {
                        pill.Background = System.Windows.Media.Brushes.Transparent;
                        pill.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                    }
                    else
                    {
                        pill.Background = new SolidColorBrush(WpfColor.FromArgb(34, hue.R, hue.G, hue.B));
                        pill.BorderBrush = new SolidColorBrush(WpfColor.FromArgb(90, hue.R, hue.G, hue.B));
                    }
                    pill.ToolTip = string.Join("\n", new[]
                    {
                        !string.IsNullOrWhiteSpace(m.SlotLabel) ? m.SlotLabel : m.BankLabel,
                        free ? L("free slot", "slot liber", "freier Steckplatz", "emplacement libre") : $"{m.Capacity} {m.MemoryType} {m.FormFactor}".Trim(),
                        free || mhz <= 0 ? "" : $"{mhz} MHz",
                        free ? "" : m.Manufacturer, free ? "" : m.PartNumber,
                    }.Where(x => !string.IsNullOrWhiteSpace(x) && x != "—"));
                    map.Children.Add(pill);
                }
                if (_tileRams.Count > 8)
                    map.Children.Add(HeroText(L($"+ {_tileRams.Count - 8} more", $"+ încă {_tileRams.Count - 8}", $"+ {_tileRams.Count - 8} weitere", $"+ {_tileRams.Count - 8} de plus"), Ui.FontXS, "TextSecondaryBrush"));
                Grid.SetRow(map, 1); Grid.SetColumnSpan(map, 2);
                body.Children.Add(map);
            }

            var facts = HeroCards.FactsGrid();
            Grid.SetColumn(facts, 1); body.Children.Add(facts);

            var first = modules[0];
            string form = DimmLayout.IsSoDimm(first.FormFactor ?? "") ? "SO-DIMM" : first.FormFactor is "DIMM" ? "DIMM" : "";
            facts.Children.Add(HeroFact("Ram", hue, s.TotalRam,
                string.Join(" · ", new[] { (s.RamType ?? "").Split('·')[0].Trim(), form }.Where(x => x.Length > 0))));

            int running = modules.Max(r => r.ConfiguredMHz > 0 ? r.ConfiguredMHz : r.RatedMHz);
            int rated = modules.Max(r => r.RatedMHz);
            if (running > 0)
                facts.Children.Add(HeroFact("Clock", hue, $"{running} MHz",
                    rated > running ? L($"modules rated {rated} MHz", $"modulele suportă {rated} MHz", $"Module für {rated} MHz ausgelegt", $"modules prévus pour {rated} MHz")
                                    : L("running speed", "viteza de lucru", "Arbeitsgeschwindigkeit", "vitesse de fonctionnement")));

            try
            {
                var channel = RamDiagnosticsService.DetectChannelMode(_tileRams);
                string mode = channel.Status switch
                {
                    DualChannelStatus.QuadChannel   => L("Quad channel", "Quad channel", "Quad-Channel", "Quad channel"),
                    DualChannelStatus.DualChannel   => L("Dual channel", "Dual channel", "Dual-Channel", "Dual channel"),
                    DualChannelStatus.FlexMode      => L("Flex mode", "Flex mode", "Flex-Modus", "Mode Flex"),
                    DualChannelStatus.SingleChannel => L("Single channel", "Single channel", "Single-Channel", "Single channel"),
                    _                               => "",
                };
                if (mode.Length > 0)
                    facts.Children.Add(HeroFact("Channels", hue, mode, "",
                        channel.Status == DualChannelStatus.SingleChannel ? "StatusWarningBrush" : "TextPrimaryBrush",
                        string.IsNullOrWhiteSpace(channel.Advice) ? channel.Description : $"{channel.Description}\n{channel.Advice}".Trim()));
            }
            catch (Exception ex) { AppLogger.Debug($"Memory hero channel mode: {ex.Message}"); }

            int total = _tileRams.Count;
            int freeSlots = total - modules.Count;
            if (total > 0)
                facts.Children.Add(HeroFact("Motherboard", hue,
                    total == 1 ? L("1 slot", "1 slot", "1 Steckplatz", "1 emplacement")
                    : L($"{modules.Count} of {total} slots", $"{modules.Count} din {total} sloturi", $"{modules.Count} von {total} Steckplätzen", $"{modules.Count} emplacements sur {total}"),
                    freeSlots == 1 ? L("1 slot free", "1 slot liber", "1 Steckplatz frei", "1 emplacement libre")
                    : freeSlots > 1 ? L($"{freeSlots} slots free", $"{freeSlots} sloturi libere", $"{freeSlots} Steckplätze frei", $"{freeSlots} emplacements libres")
                                    : L("all in use", "toate ocupate", "alle belegt", "tous utilisés"),
                    tip: freeSlots > 0 ? L("A free slot means memory can be added without replacing the existing module.",
                                           "Un slot liber înseamnă că se poate adăuga memorie fără a înlocui modulul existent.",
                                           "Ein freier Steckplatz bedeutet: Speicher lässt sich ergänzen, ohne das vorhandene Modul zu ersetzen.",
                                           "Un emplacement libre permet d'ajouter de la mémoire sans remplacer le module existant.") : null));

            string title = $"{s.TotalRam} {(s.RamType ?? "").Split('·')[0].Trim()}".Trim();
            return HeroCard("Ram", L("Memory", "Memorie", "Speicher", "Mémoire"), title, BuildRamSearchTerm(_tileRams, s), body);
        }

        // ── Stocare ──────────────────────────────────────────────────────────
        private Border? BuildStorageHero()
        {
            if (_tileDisks.Count == 0) return null;
            // an empty card reader appears in Windows as a 0 B disk: it is not a disk, it is not shown or counted
            var disks = _tileDisks.Where(d => d.SizeBytes > 0).ToList();
            if (disks.Count == 0) disks = _tileDisks.ToList();
            var body = new StackPanel();
            double DiskPicW() => disks.Count switch { 1 => HeroCards.ArtW, 2 => 86.0, _ => 74.0 };   // 1 disk: the same size as the other illustrations; several: slightly smaller so they fit

            foreach (var d in disks.Take(3))
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                string pictureKind = DriveForm.PictureKind(d.Kind, d.Model, _heroLaptop, d.FormFactor);
                bool noHealth = d.SmartAttributes.Count == 0 && d.NvmeHealthRows.Count == 0;
                int hp = noHealth ? 0 : d.HealthPercent;
                row.Children.Add(new WpfViewbox
                {
                    Width = DiskPicW(), Height = DiskPicW() * 46 / 66,
                    Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = WpfVAlign.Top,
                    ToolTip = noHealth ? null : L($"Disk health {hp}%. The small LED on the drawing shows it: green = healthy, amber = watch, red = failing.",
                        $"Sănătatea discului: {hp}%. Ledul mic din desen o arată: verde = sănătos, galben = de urmărit, roșu = se defectează.",
                        $"Zustand des Laufwerks: {hp}%. Die kleine LED zeigt ihn: grün = gesund, gelb = beobachten, rot = Ausfall.",
                        $"État du disque : {hp}%. La petite LED l'indique : verte = sain, orange = à surveiller, rouge = défaillant."),
                    Child = HardwareIllustrations.Drive(pictureKind, DriveHealth.From(hp, !noHealth)),
                });

                var info = new StackPanel { VerticalAlignment = WpfVAlign.Center };
                Grid.SetColumn(info, 1); row.Children.Add(info);

                var name = HeroText(string.IsNullOrWhiteSpace(d.Model) ? $"Disk {d.DiskIndex}" : d.Model.Trim(), Ui.FontL, "TextPrimaryBrush", semibold: true);
                name.ToolTip = d.Model;
                HeroCards.TwoLines(name);
                info.Children.Add(name);

                // the type and the capacity + the health, in a single colour
                var (healthText, healthKey) =
                      hp >= 90 ? (L($"health {hp}%", $"sănătate {hp}%", $"Zustand {hp}%", $"état {hp}%"), "StatusOkBrush")
                    : hp >= 60 ? (L($"health {hp}% · watch", $"sănătate {hp}% · de urmărit", $"Zustand {hp}% · beobachten", $"état {hp}% · à surveiller"), "StatusWarnBrush")
                    : hp > 0   ? (L($"health {hp}% · failing", $"sănătate {hp}% · se defectează", $"Zustand {hp}% · fällt aus", $"état {hp}% · défaillant"), "StatusCritBrush")
                    :            (L("no health data", "fără date de sănătate", "keine Zustandsdaten", "pas de données d'état"), "TextSecondaryBrush");
                // the exact type ("M.2 SATA SSD", "3.5\" HDD · 7200 rpm") + the USB link for external disks (11.11.2026)
                string kindText = d.Kind == "Unknown" ? "" : d.TypeText + (d.UsbConnected.Length > 0 ? " · " + d.UsbConnected.Split(" · ")[0] : "");
                var line = new StackPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(0, 1, 0, 0) };
                line.Children.Add(HeroText(string.Join(" · ", new[] { kindText, d.Size }.Where(x => !string.IsNullOrWhiteSpace(x))), Ui.FontS, "TextSecondaryBrush"));
                var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(10, 1, 5, 0), VerticalAlignment = WpfVAlign.Center };
                dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, healthKey);
                line.Children.Add(dot);
                line.Children.Add(HeroText(healthText, Ui.FontS, healthKey == "TextSecondaryBrush" ? "TextSecondaryBrush" : "TextPrimaryBrush"));
                info.Children.Add(line);

                // the disk volumes: one row each with the letter, the block bar and the free space (a single volume: a large bar)
                var parts = d.Partitions.Where(p => p.TotalGB >= 0.5).ToList();   // ~0 GB volumes (EFI, reserved) are not shown
                if (parts.Count >= 1)
                {
                    var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
                    foreach (var p in parts.Take(5))
                    {
                        double usedPct = Math.Clamp((p.TotalGB - p.FreeGB) / p.TotalGB * 100, 0, 100);
                        var volume = new Grid { Margin = new Thickness(0, 0, 0, 3) };
                        volume.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                        volume.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 50 });
                        volume.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        string letter = string.IsNullOrWhiteSpace(p.Letter) ? "—" : p.Letter.TrimEnd('\\');
                        var l = HeroText(letter, Ui.FontXS, "TextPrimaryBrush", semibold: true);
                        l.ToolTip = string.IsNullOrWhiteSpace(p.Label) ? letter : $"{letter} {p.Label}";
                        volume.Children.Add(l);
                        var bar = new BlockBar
                        {
                            Blocks = 24, Percent = usedPct, Height = 6, HorizontalAlignment = WpfHAlign.Stretch, VerticalAlignment = WpfVAlign.Center,
                            FillKey = SMDWin.Services.StatusRules.DiskBrushKey(usedPct),
                        };
                        Grid.SetColumn(bar, 1); volume.Children.Add(bar);
                        var free = HeroText(L($"{DisplayFormat.SizeGb(p.FreeGB)} free of {DisplayFormat.SizeGb(p.TotalGB)}", $"{DisplayFormat.SizeGb(p.FreeGB)} liberi din {DisplayFormat.SizeGb(p.TotalGB)}",
                            $"{DisplayFormat.SizeGb(p.FreeGB)} frei von {DisplayFormat.SizeGb(p.TotalGB)}", $"{DisplayFormat.SizeGb(p.FreeGB)} libres sur {DisplayFormat.SizeGb(p.TotalGB)}"), Ui.FontXS, "TextSecondaryBrush");
                        free.Margin = new Thickness(12, 0, 0, 0); free.TextAlignment = TextAlignment.Right; free.HorizontalAlignment = WpfHAlign.Right;
                        Grid.SetColumn(free, 2); volume.Children.Add(free);
                        list.Children.Add(volume);
                    }
                    if (parts.Count > 5)
                        list.Children.Add(HeroText(L($"+ {parts.Count - 5} more volumes", $"+ încă {parts.Count - 5} volume", $"+ {parts.Count - 5} weitere Volumes", $"+ {parts.Count - 5} volumes de plus"), Ui.FontXS, "TextSecondaryBrush"));
                    info.Children.Add(list);
                }
                body.Children.Add(row);
            }
            if (disks.Count > 3)
                body.Children.Add(HeroText(L($"+ {disks.Count - 3} more drives", $"+ încă {disks.Count - 3} discuri",
                    $"+ {disks.Count - 3} weitere Laufwerke", $"+ {disks.Count - 3} disques de plus"), Ui.FontS, "TextSecondaryBrush"));

            // the same unit as Windows and the rest of the app (447 GB, not the 480 GB on the label); the capacity of the PC
            // counts only its own drives — a USB stick or an external disk is only there for now and is named apart
            static bool External(DiskHealthEntry d) => d.Bus.Equals("USB", StringComparison.OrdinalIgnoreCase)
                || d.Kind.Contains("USB", StringComparison.OrdinalIgnoreCase) || d.Kind.StartsWith("External", StringComparison.OrdinalIgnoreCase);
            var inside = disks.Where(d => !External(d)).ToList();
            int outside = disks.Count - inside.Count;
            if (inside.Count == 0) { inside = disks; outside = 0; }
            string totalText = DisplayFormat.SizeBytes(inside.Sum(d => (long)d.SizeBytes));
            // a single disk: the capacity + the short type ("238 GB SSD"), so the title says what kind of disk it is
            string ShortKind(DiskHealthEntry d)
            {
                string k = DriveForm.PictureKind(d.Kind, d.Model, _heroLaptop, d.FormFactor);
                return k.Contains("SSD", StringComparison.OrdinalIgnoreCase) ? "SSD"
                     : k.Contains("HDD", StringComparison.OrdinalIgnoreCase) ? "HDD" : "";
            }
            string title = inside.Count > 1
                ? $"{totalText} · {inside.Count} " + L("drives", "discuri", "Laufwerke", "disques")
                : $"{totalText} {ShortKind(inside[0])}".Trim();
            if (outside > 0) title += " · " + (outside == 1 ? L("+ 1 USB", "+ 1 USB", "+ 1 USB", "+ 1 USB") : $"+ {outside} USB");
            return HeroCard("Disk", L("Storage", "Stocare", "Speicher", "Stockage"), title, null, body);
        }

    }
}
