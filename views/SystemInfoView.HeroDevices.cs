using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SMDWin.Models;
using SMDWin.Services;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfHAlign = System.Windows.HorizontalAlignment;
using WpfVAlign = System.Windows.VerticalAlignment;

namespace SMDWin.Views
{
    /// <summary>
    /// System Information, the cards: Graphics card, Display, Motherboard (with an illustration) and Network, Battery,
    /// Peripherals (information only). Their data is gathered in BuildAllCards, together with the text rows.
    /// </summary>
    public partial class SystemInfoView
    {
        private sealed record HeroGpu(string Name, bool Integrated, string Vram, string Driver, string DriverDate, double TdpW);
        private sealed record HeroScreen(int Number, string Name, int Width, int Height, int Hz, bool Primary, int X,
                                         double WidthCm, double HeightCm, long Technology);
        private sealed record HeroBoard(string Maker, string Model, string BiosVendor, string BiosVersion, string BiosDate, string Serial);

        // Completate in BuildAllCards
        private readonly List<HeroGpu> _heroGpus = new();
        private readonly List<HeroScreen> _heroScreens = new();
        private HeroBoard? _heroBoard;
        private string _heroChassisType = "", _heroChassisSerial = "";
        private ComputerKind _heroComputer = ComputerKind.Tower;
        private List<NetworkAdapterEntry> _heroNets = new();
        private List<NetworkAdapterEntry> _heroNetsOff = new();   // the adapters that are off / not connected (a LAN card without a cable)
        private string? _heroBluetooth;                 // null = no Bluetooth; empty = present, unknown version
        private List<string> _heroUsbs = new();
        private List<string> _heroAudio = new();

        private static bool Known(string? value)
            => !string.IsNullOrWhiteSpace(value) && value.Trim() != "—" && !HardwareInfo.IsJunkBoardValue(value);

        // ── Graphics card ──────────────────────────────────────────────────────
        private Border? BuildGraphicsHero()
        {
            if (_heroGpus.Count == 0) return null;
            var hue = SpecSheet.Hue("Gpu");
            // the dedicated card is the one that matters; the integrated one appears on a row below
            var main = _heroGpus.FirstOrDefault(g => !g.Integrated && !GpuPackage.IsBasicDriver(g.Name)) ?? _heroGpus[0];

            var picture = HardwareIllustrations.Gpu(main.Name, _heroLaptop, main.TdpW);
            picture.Width = HeroCards.ArtW; picture.Height = HeroCards.ArtH;
            var body = HeroCards.PictureBody(picture, out var facts);

            // the video memory shows as text (no bar under the drawing: a fixed block scale gave the wrong impression for small cards)
            string vramTip = "";

            if (GpuPackage.IsBasicDriver(main.Name))
                facts.Children.Add(HeroFact("Drivers", hue,
                    L("Driver not installed", "Driver neinstalat", "Treiber nicht installiert", "Pilote non installé"),
                    L("Windows uses its basic one", "Windows îl folosește pe cel de bază", "Windows nutzt den Basistreiber", "Windows utilise le pilote de base"),
                    "StatusWarningBrush",
                    L("Windows shows the picture with its basic display driver: no 3D acceleration, low resolutions, no brightness control. The video card's own driver has to be installed.",
                      "Windows afișează imaginea cu driverul lui de bază: fără accelerare 3D, rezoluții mici, fără reglarea luminozității. Trebuie instalat driverul plăcii video.",
                      "Windows zeigt das Bild mit seinem Basistreiber: keine 3D-Beschleunigung, niedrige Auflösungen, keine Helligkeitsregelung. Der Treiber der Grafikkarte muss installiert werden.",
                      "Windows affiche l'image avec son pilote de base : pas d'accélération 3D, résolutions basses, pas de réglage de la luminosité. Le pilote de la carte graphique doit être installé.")));
            else
                // the label says what the value is ("Type"), the explanation (part of the processor / separate card) is in the tooltip
                facts.Children.Add(HeroFact("Gpu", hue,
                    main.Integrated ? L("Integrated", "Integrată", "Integriert", "Intégrée") : L("Dedicated", "Dedicată", "Dediziert", "Dédiée"),
                    L("type", "tip", "Typ", "type"), tip:
                    main.Integrated ? L("Part of the processor", "Parte din procesor", "Teil des Prozessors", "Intégrée au processeur")
                    : _heroLaptop   ? L("Separate chip", "Cip separat", "Eigener Chip", "Puce séparée")
                                    : L("Separate card", "Placă separată", "Eigene Karte", "Carte séparée")));

            if (main.Integrated)
                facts.Children.Add(HeroFact("Ram", hue, L("Shared memory", "Memorie partajată", "Geteilter Speicher", "Mémoire partagée"),
                    L("taken from the RAM", "luată din memoria RAM", "aus dem Arbeitsspeicher", "prise sur la RAM"),
                    tip: L("An integrated video card has no memory of its own: it borrows from the computer's RAM, as much as it needs.",
                           "O placă video integrată nu are memorie proprie: împrumută din memoria RAM a calculatorului, cât are nevoie.",
                           "Eine integrierte Grafikkarte hat keinen eigenen Speicher: Sie leiht sich bei Bedarf Arbeitsspeicher.",
                           "Une carte graphique intégrée n'a pas de mémoire propre : elle emprunte à la RAM selon ses besoins.")));
            else if (Known(main.Vram))
                facts.Children.Add(HeroFact("Ram", hue, main.Vram.Replace(".0 GB", " GB"), L("video memory", "memorie video", "Grafikspeicher", "mémoire vidéo"), tip: vramTip.Length > 0 ? vramTip : null));

            if (main.TdpW > 0)
                facts.Children.Add(HeroFact("Bolt", hue, $"{main.TdpW:0} W", L("rated power (TDP)", "putere nominală (TDP)", "Nennleistung (TDP)", "puissance nominale (TDP)")));

            if (Known(main.Driver) && !GpuPackage.IsBasicDriver(main.Name))
            {
                string shown = GpuPackage.DriverText(main.Name, main.Driver);
                facts.Children.Add(HeroFact("Drivers", hue, shown,
                    L("driver", "driver", "Treiber", "pilote") + (main.DriverDate.Length > 0 ? $" · {main.DriverDate}" : ""),
                    tip: shown == main.Driver ? null : L($"Driver {shown} (Windows version {main.Driver})", $"Driver {shown} (versiunea Windows {main.Driver})",
                                                         $"Treiber {shown} (Windows-Version {main.Driver})", $"Pilote {shown} (version Windows {main.Driver})")));
            }

            foreach (var other in _heroGpus.Where(g => !ReferenceEquals(g, main)).Take(2))
                facts.Children.Add(HeroFact(other.Integrated ? "Cpu" : "Gpu", hue, GpuPackage.ShortName(other.Name),
                    other.Integrated ? L("integrated, for light work", "integrată, pentru sarcini ușoare", "integriert, für leichte Aufgaben", "intégrée, pour les tâches légères")
                                     : L("second video card", "a doua placă video", "zweite Grafikkarte", "deuxième carte graphique"),
                    tip: other.Name));

            return HeroCard("Gpu", L("Graphics", "Placă video", "Grafik", "Carte graphique"), GpuPackage.ShortName(main.Name), main.Name, body);
        }

        // ── Display ───────────────────────────────────────────────────────────
        // Windows reports 59 / 119 / 143 Hz for 60 / 120 / 144 Hz screens (the real frequency is 59.94 etc.)
        private static int RoundHz(int hz) => hz switch { 59 => 60, 74 => 75, 119 => 120, 143 => 144, 164 => 165, 239 => 240, _ => hz };

        private bool IsBuiltInScreen(HeroScreen m)
            => m.Technology >= 0 ? DisplayFacts.IsBuiltIn(m.Technology) : (_heroLaptop || _heroComputer == ComputerKind.AllInOne) && _heroScreens.Count == 1;

        private Border? BuildDisplaysHero()
        {
            if (_heroScreens.Count == 0) return null;
            var hue = SpecSheet.Hue("Monitor");
            bool several = _heroScreens.Count > 1;

            // the drawing: the screens from left to right, as they are arranged in Windows
            var arranged = _heroScreens.OrderBy(m => m.X).ToList();
            var picture = HardwareIllustrations.Monitors(arranged.Select(m =>
                new MonitorSpec(m.Width, m.Height, m.HeightCm, m.Primary, IsBuiltInScreen(m), several ? m.Number.ToString() : "",
                    AllInOne: _heroComputer == ComputerKind.AllInOne && IsBuiltInScreen(m))).ToList());
            picture.Width = HeroCards.ArtW; picture.Height = HeroCards.ArtH;
            var body = HeroCards.PictureBody(picture, out var facts);
            // several screens: one per row, one under the other (on two columns the text of each does not fit)
            if (several) HeroCards.SingleColumn(facts);

            string builtInText = L("built-in screen", "ecran încorporat", "eingebauter Bildschirm", "écran intégré");
            string Link(HeroScreen m) => IsBuiltInScreen(m) ? builtInText : m.Technology >= 0 ? DisplayFacts.Connection(m.Technology) : "";
            double Inches(HeroScreen m) => DisplayFacts.DiagonalInches(m.WidthCm, m.HeightCm);

            string title;
            string? search = null;
            if (!several)
            {
                var m = _heroScreens[0];
                double inches = Inches(m);
                facts.Children.Add(HeroFact("Monitor", hue, $"{m.Width} × {m.Height}", DisplayFacts.ResolutionName(m.Width, m.Height)));
                if (m.Hz > 1)
                    facts.Children.Add(HeroFact("Gauge", hue, $"{RoundHz(m.Hz)} Hz", L("refresh rate", "rată de reîmprospătare", "Bildwiederholrate", "fréquence de rafraîchissement")));
                if (inches > 0)
                {
                    int ppi = DisplayFacts.Ppi(m.Width, m.Height, DisplayFacts.NiceDiagonal(inches));
                    facts.Children.Add(HeroFact("Ruler", hue, DisplayFacts.DiagonalText(inches),
                        L("diagonal", "diagonală", "Diagonale", "diagonale") + (ppi > 0 ? L($" · {ppi} ppi", $" · {ppi} ppi", $" · {ppi} ppi", $" · {ppi} ppi") : "")));
                }
                string link = Link(m);
                if (link.Length > 0)
                    facts.Children.Add(HeroFact("Channels", hue, IsBuiltInScreen(m) ? L("Built-in", "Încorporat", "Eingebaut", "Intégré") : link,
                        IsBuiltInScreen(m) ? L("screen type", "tip ecran", "Bildschirmtyp", "type d'écran") : L("connection", "conexiune", "Anschluss", "connexion")));
                bool named = Known(m.Name) && !m.Name.Contains("Generic", StringComparison.OrdinalIgnoreCase);
                title = named ? m.Name : IsBuiltInScreen(m) ? L("Built-in screen", "Ecran încorporat", "Eingebauter Bildschirm", "Écran intégré")
                                       : $"{m.Width} × {m.Height}";
                if (named && !IsBuiltInScreen(m)) search = m.Name;
            }
            else
            {
                // one row per monitor: the resolution and frequency, then the model, size, connection
                foreach (var m in _heroScreens.Take(4))
                {
                    string about = string.Join(" · ", new[]
                    {
                        Known(m.Name) ? m.Name : "", DisplayFacts.DiagonalText(Inches(m)), Link(m),
                        m.Primary ? L("main", "principal", "Haupt", "principal") : "",
                    }.Where(x => x.Length > 0));
                    facts.Children.Add(HeroFact("Monitor", hue, $"{m.Number} · {m.Width} × {m.Height}" + (m.Hz > 1 ? $" · {RoundHz(m.Hz)} Hz" : ""), about));
                }
                if (_heroScreens.Count > 4)
                    facts.Children.Add(HeroText(L($"+ {_heroScreens.Count - 4} more", $"+ încă {_heroScreens.Count - 4}",
                        $"+ {_heroScreens.Count - 4} weitere", $"+ {_heroScreens.Count - 4} de plus"), Ui.FontS, "TextSecondaryBrush"));
                title = $"{_heroScreens.Count} " + L("monitors", "monitoare", "Monitore", "moniteurs");
            }
            return HeroCard("Monitor", L("Display", "Afișaj", "Bildschirm", "Écran"), title, search, body);
        }

        // The chassis type: the one deduced from the model when the BIOS only says "Other" (iMacs), otherwise the BIOS one
        private string ChassisLabel()
        {
            bool vague = _heroChassisType.Length == 0 || _heroChassisType is "Other" or "Altul" or "Andere" or "Autre";
            return vague ? ComputerPackage.Label(_heroComputer, LanguageService.CurrentCode) : _heroChassisType;
        }

        // ── System: the computer as a whole (model, serial, chassis, motherboard, BIOS, boot mode, name) ──
        private Border? BuildBoardHero()
        {
            var board = _heroBoard;
            var s = _tileSummary;
            if (board == null && s == null) return null;
            var hue = SpecSheet.Hue("Motherboard");

            // laptop, tablet, all-in-one and mini PC: the computer silhouette; tower, small desktop and server: the motherboard
            bool silhouette = _heroComputer is ComputerKind.Laptop or ComputerKind.Ultrabook or ComputerKind.Tablet
                or ComputerKind.AllInOne or ComputerKind.MiniPc;
            var picture = silhouette
                ? HardwareIllustrations.Computer(_heroComputer)
                : HardwareIllustrations.Board(BoardPackage.KindFor(board?.Model, _heroComputer));
            picture.Width = HeroCards.ArtW; picture.Height = HeroCards.ArtH;
            var body = HeroCards.PictureBody(picture, out var facts);

            string boardName = string.Join(" ", new[] { board?.Maker, board?.Model }.Where(Known)).Trim();
            string systemName = DisplayFormat.MachineName(Known(s?.Manufacturer) ? s!.Manufacturer : "", Known(s?.Model) ? s!.Model : "");
            // a brand computer (laptop, iMac) has its own name; a self-built one is named after the motherboard
            bool systemDiffers = systemName.Length > 0 && Known(s?.Model)
                && !string.Equals(s!.Model.Trim(), board?.Model?.Trim(), StringComparison.OrdinalIgnoreCase);
            string title = systemDiffers ? systemName : boardName.Length > 0 ? boardName : systemName;

            // the computer serial (from the label); on self-built computers it is missing and the board's one stays
            string serial = Known(_heroChassisSerial) ? _heroChassisSerial : Known(board?.Serial) ? board!.Serial : "";
            if (serial.Length > 0)
                facts.Children.Add(HeroFact("Key", hue, serial, L("serial number", "număr de serie", "Seriennummer", "numéro de série")));
            if (_heroChassisType.Length > 0 || systemDiffers)
                facts.Children.Add(HeroFact("Chassis", hue, ChassisLabel(), L("case type", "tipul carcasei", "Gehäusetyp", "type de boîtier")));
            if (Known(board?.Model) && (systemDiffers || boardName != title))
                facts.Children.Add(HeroFact("Motherboard", hue, board!.Model,
                    L("motherboard", "placă de bază", "Hauptplatine", "carte mère") + (Known(board.Maker) ? $" · {board.Maker}" : "")));
            if (board != null && Known(board.BiosVersion))
                facts.Children.Add(HeroFact("Socket", hue, board.BiosVersion,
                    string.Join(" · ", new[] { "BIOS", Known(board.BiosDate) ? board.BiosDate : "", Known(board.BiosVendor) ? board.BiosVendor : "" }.Where(x => x.Length > 0))));
            string boot = WindowsInfoReader.ReadBootMode();
            if (boot.Length > 0)
                facts.Children.Add(HeroFact("Bolt", hue, boot, L("boot mode", "mod de pornire", "Startmodus", "mode de démarrage")));
            string pc = !string.IsNullOrWhiteSpace(s?.ComputerName) ? s!.ComputerName : Environment.MachineName;
            string workgroup = WindowsInfoReader.ReadWorkgroup();
            facts.Children.Add(HeroFact("Ethernet", hue, pc, L("computer name", "nume calculator", "Computername", "nom de l'ordinateur"),
                tip: workgroup.Length > 0 ? L($"{pc}\nWorkgroup: {workgroup}", $"{pc}\nGrup de lucru: {workgroup}", $"{pc}\nArbeitsgruppe: {workgroup}", $"{pc}\nGroupe de travail : {workgroup}") : null));

            if (title.Length == 0 && facts.Children.Count == 0) return null;
            string search = systemDiffers ? systemName : Known(board?.Model) ? boardName : "";
            return HeroCard("Motherboard", L("System", "Sistem", "System", "Système"), title, search, body);
        }

        // ── Retea ────────────────────────────────────────────────────────────
        /// <summary>The adapter has a real IP address (the adapter list puts "—" when it has none; 169.254.x = no network).</summary>
        internal static bool HasIp(NetworkAdapterEntry n)
        {
            string ip = (n.IpAddress ?? "").Trim();
            return ip.Length > 0 && ip != "—" && ip != "0.0.0.0" && !ip.StartsWith("169.254.", StringComparison.Ordinal);
        }

        /// <summary>Mobile internet modem (SIM card): 3G / 4G / 5G.</summary>
        internal static bool IsMobileBroadband(NetworkAdapterEntry n)
        {
            string text = n.Type + " " + n.Name;
            foreach (string mark in new[] { "Mobile Broadband", "WWAN", "HSPA", "Sierra Wireless", "Fibocom", "Snapdragon X", "5G Modem", "Mobile Data" })
                if (text.Contains(mark, StringComparison.OrdinalIgnoreCase)) return true;
            // "LTE" only as a whole word (otherwise "Filter" would match too)
            return n.Type == "Mobile" || System.Text.RegularExpressions.Regex.IsMatch(text, @"\bLTE\b");
        }

        private Border? BuildNetworkHero()
        {
            var hue = SpecSheet.Hue("Wifi");
            static bool IsWireless(NetworkAdapterEntry n)
                => (n.Type + " " + n.Name).Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)
                || (n.Type + " " + n.Name).Contains("Wireless", StringComparison.OrdinalIgnoreCase)
                || (n.Type + " " + n.Name).Contains("WLAN", StringComparison.OrdinalIgnoreCase)
                || n.Type.Contains("802.11", StringComparison.OrdinalIgnoreCase);

            // one row per type (Wi-Fi, LAN, mobile internet, Bluetooth): the icon, the type, the adapter NAME (the hardware) and the
            // state; the IP address, MAC and speed stay in the tooltip
            var mobile = _heroNets.Where(IsMobileBroadband).ToList();
            var wifi = _heroNets.Where(n => IsWireless(n) && !IsMobileBroadband(n)).ToList();
            var wired = _heroNets.Where(n => !IsWireless(n) && !IsMobileBroadband(n)
                                             && !n.Name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
                                             && !n.Type.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)).ToList();
            // virtual cards (virtual machines, VPN) are not hardware: the real card takes priority
            static bool IsVirtual(NetworkAdapterEntry n)
            {
                foreach (string mark in new[] { "Virtual", "VPN", "TAP-", "Hyper-V", "VMware", "VirtualBox", "Miniport", "Loopback", "Bluetooth" })
                    if (n.Name.Contains(mark, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
            // a LAN card without a cable and a modem without a SIM appear in Windows as disabled adapters: they stay in the list, as
            // "not connected"
            wired.AddRange(_heroNetsOff.Where(n => n.Type == "Ethernet" && !IsVirtual(n) && !IsMobileBroadband(n)));
            if (mobile.Count == 0) mobile = _heroNetsOff.Where(IsMobileBroadband).Take(1).ToList();
            if (wifi.Count == 0) wifi = _heroNetsOff.Where(n => IsWireless(n) && !IsMobileBroadband(n) && !IsVirtual(n)).Take(1).ToList();
            // the LAN row shows the real card; a virtual one (Hyper-V, WSL, VMware) appears only if no other exists
            var realWired = wired.Where(n => !IsVirtual(n)).ToList();
            var lan = realWired.Count > 0 ? realWired : wired;
            // "connected" = has a real IP address; the title counts exactly the rows that appear connected below
            int connectedCount = (wifi.Any(HasIp) ? 1 : 0) + (lan.Any(HasIp) ? 1 : 0) + (mobile.Any(HasIp) ? 1 : 0);
            var body = new StackPanel { VerticalAlignment = WpfVAlign.Center };
            static string CleanName(string name) => System.Text.RegularExpressions.Regex.Replace(name.Replace("(R)", "").Replace("(TM)", ""), @"\s+", " ").Trim();
            void Row(string iconKey, string kind, string adapterName, bool up, string state, string tip)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 8), Background = WpfBrushes.Transparent, ToolTip = tip };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var tile = SpecSheet.IconTile(iconKey, 34);
                tile.Margin = new Thickness(0, 0, 12, 0);
                tile.Opacity = up ? 1 : 0.45;
                row.Children.Add(tile);
                var text = new StackPanel { VerticalAlignment = WpfVAlign.Center };
                // the type and the state on the same row ("Wi-Fi · connected"), the adapter name below: four rows fit in the card
                var top = HeroText("", Ui.FontXS, "TextSecondaryBrush");
                top.Inlines.Add(new System.Windows.Documents.Run(kind + "  ·  "));
                var stateRun = new System.Windows.Documents.Run(state) { FontWeight = up ? FontWeights.SemiBold : FontWeights.Normal };
                stateRun.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, up ? "StatusOkBrush" : "TextSecondaryBrush");
                top.Inlines.Add(stateRun);
                text.Children.Add(top);
                text.Children.Add(HeroText(adapterName.Length > 0 ? adapterName : kind, Ui.FontL, up ? "TextPrimaryBrush" : "TextSecondaryBrush", semibold: true));
                Grid.SetColumn(text, 1); row.Children.Add(text);
                body.Children.Add(row);
            }
            string Describe(NetworkAdapterEntry n) => string.Join("\n", new[] { n.Name, HasIp(n) ? n.IpAddress : "", n.MacAddress, n.Speed }.Where(x => !string.IsNullOrWhiteSpace(x) && x != "—"));
            string notConnected = L("not connected", "neconectat", "nicht verbunden", "non connecté");
            string connectedText = L("connected", "conectat", "verbunden", "connecté");
            if (wifi.Count > 0)
            {
                var w = wifi.FirstOrDefault(HasIp) ?? wifi.FirstOrDefault(n => !IsVirtual(n)) ?? wifi[0];
                Row("Wifi", "Wi-Fi", CleanName(w.Name), HasIp(w), HasIp(w) ? connectedText : notConnected,
                    string.Join("\n\n", wifi.Select(Describe)));
            }
            // without an IP address and without a wired network card name: it is not a LAN card (no row is invented)
            if (lan.Any(n => HasIp(n) || n.Type == "Ethernet"))
            {
                var w = lan.FirstOrDefault(HasIp) ?? lan.FirstOrDefault(n => n.Type == "Ethernet") ?? lan[0];
                Row("Ethernet", "LAN", CleanName(w.Name), HasIp(w),
                    HasIp(w) ? connectedText : L("no cable", "fără cablu", "kein Kabel", "pas de câble"),
                    string.Join("\n\n", wired.Select(Describe)));
            }
            if (mobile.Count > 0)
            {
                var w = mobile.FirstOrDefault(HasIp) ?? mobile[0];
                Row("Mobile", L("Mobile broadband (SIM)", "Internet mobil (SIM)", "Mobiles Breitband (SIM)", "Haut débit mobile (SIM)"), CleanName(w.Name), HasIp(w),
                    HasIp(w) ? connectedText : notConnected, string.Join("\n\n", mobile.Select(Describe)));
            }
            if (_heroBluetooth != null)
                Row("Bluetooth", "Bluetooth", _heroBluetooth.Length > 0 ? CleanName(_heroBluetooth) : "Bluetooth", true, L("present", "prezent", "vorhanden", "présent"), _heroBluetooth);
            if (body.Children.Count == 0)
                Row("Wifi", "Wi-Fi", "", false, notConnected, "");

            // the large name appears only for connected adapters; without them, the card stays a grey line
            if (connectedCount == 0)
                return HeroCards.Card("Globe", L("Network", "Rețea", "Netzwerk", "Réseau"), L("Not connected", "Neconectat", "Nicht verbunden", "Non connecté"),
                    body, new HeroCards.CardOptions { TitleBrushKey = "TextSecondaryBrush" });
            // the title does not repeat the types (Wi-Fi, LAN, Bluetooth): they already have a row each below
            string title = connectedCount == 1
                ? L("1 connection active", "1 conexiune activă", "1 Verbindung aktiv", "1 connexion active")
                : L($"{connectedCount} connections active", $"{connectedCount} conexiuni active", $"{connectedCount} Verbindungen aktiv", $"{connectedCount} connexions actives");
            return HeroCard("Globe", L("Network", "Rețea", "Netzwerk", "Réseau"), title, null, body);
        }

        /// <summary>The name of the active power plan (Balanced, High performance…); empty when it cannot be read.</summary>
        private string ActivePlanName()
        {
            try
            {
                return (PowerAdvancedService.ActiveSchemeGuid() ?? "").ToLowerInvariant() switch
                {
                    "381b4222-f694-41f0-9685-ff5bb260df2e" => L("Balanced", "Echilibrat", "Ausbalanciert", "Équilibré"),
                    "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => L("High performance", "Performanță înaltă", "Höchstleistung", "Performances élevées"),
                    "a1841308-3541-4fab-bc81-f71556f20b4a" => L("Power saver", "Economisire energie", "Energiesparmodus", "Économie d'énergie"),
                    "e9a42b02-d5df-448d-aa00-03f14749eb61" => L("Ultimate performance", "Performanță maximă", "Maximale Leistung", "Performances ultimes"),
                    "" => "",
                    _ => L("Custom", "Personalizat", "Benutzerdefiniert", "Personnalisé"),
                };
            }
            catch (Exception ex) { AppLogger.Debug($"Power plan name: {ex.Message}"); return ""; }
        }

        // ── Power without a battery (desktop, iMac) ─────────────────────────
        // Neutral: having no battery is not a problem. The title states the source and the plan, and below, on one row, the few
        // rest settings of the computer.
        private Border? BuildMainsPowerHero()
        {
            var hue = SpecSheet.Hue("Battery");
            var facts = new WrapPanel { VerticalAlignment = WpfVAlign.Top, HorizontalAlignment = WpfHAlign.Left };

            string plan = ActivePlanName();

            try
            {
                int? display = PowerAdvancedService.GetDisplayOffAfterSeconds(true);
                if (display != null)
                    facts.Children.Add(HeroFact("Monitor", hue, PowerAdvancedService.FormatIdle(display, L("Never", "Niciodată", "Nie", "Jamais")),
                        L("screen off after", "ecran oprit după", "Bildschirm aus nach", "écran éteint après")));
                int? sleep = PowerAdvancedService.GetSleepAfterSeconds(true);
                if (sleep != null)
                    facts.Children.Add(HeroFact("Clock", hue, PowerAdvancedService.FormatIdle(sleep, L("Never", "Niciodată", "Nie", "Jamais")),
                        L("sleep after", "sleep după", "Ruhezustand nach", "mise en veille après")));
                bool? fast = PowerAdvancedService.GetFastStartup();
                if (fast != null)
                    facts.Children.Add(HeroFact("Drivers", hue, fast.Value ? L("On", "Activ", "Ein", "Activé") : L("Off", "Oprit", "Aus", "Désactivé"),
                        L("fast startup", "pornire rapidă", "Schnellstart", "démarrage rapide")));
            }
            catch (Exception ex) { AppLogger.Debug($"Power card timers: {ex.Message}"); }

            facts.Children.Add(HeroFact("Clock", hue, DisplayFormat.Uptime(PowerOnClock.SinceOn),
                L("running for", "pornit de", "läuft seit", "allumé depuis")));

            string source = L("Mains", "Priză", "Netzteil", "Secteur");
            return HeroCard("Bolt", L("Power", "Alimentare", "Stromversorgung", "Alimentation"),
                plan.Length > 0 ? $"{source} · {plan}" : source, null, facts);
        }

        // ── Baterie ──────────────────────────────────────────────────────────
        private Border? BuildBatteryHero()
        {
            var bat = _tileBattery;
            if (bat == null || !bat.Present) return BuildMainsPowerHero();
            var hue = SpecSheet.Hue("Battery");

            // left: the battery drawing (how charged it is now); right: its health as a bar with the lost part hatched
            var picture = new BatteryPicture
            {
                Variant = SettingsService.Current.BatteryGlyphStyle, Percent = bat.ChargePercent, Charging = bat.IsCharging,
                Width = HeroCards.ArtW, Height = HeroCards.ArtW * 0.5, HorizontalAlignment = WpfHAlign.Left,
            };
            var body = HeroCards.PictureBody(picture, out var oldFacts);
            body.Children.Remove(oldFacts);
            var right = new StackPanel { VerticalAlignment = WpfVAlign.Top, HorizontalAlignment = WpfHAlign.Left, MaxWidth = 250 };
            Grid.SetColumn(right, 1); body.Children.Add(right);
            // the health bar sits under the battery drawing (small, as wide as the drawing), so it does not take height in the right column
            var left = new StackPanel { VerticalAlignment = WpfVAlign.Top, HorizontalAlignment = WpfHAlign.Left, Width = HeroCards.ArtW, Margin = new Thickness(0, 0, 14, 0) };
            if (body.Children.Count > 0 && body.Children[0] is FrameworkElement pic0 && !ReferenceEquals(pic0, right))
            {
                body.Children.Remove(pic0);
                pic0.Margin = new Thickness(0);
                left.Children.Add(pic0);
            }
            Grid.SetColumn(left, 0); body.Children.Add(left);

            if (bat.WearPct is int wear)
            {
                int health = Math.Clamp(100 - wear, 0, 100);
                string healthKey = BatteryHealthBar.KeyForWear(wear);
                var head = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.Children.Add(HeroText(L("Health", "Sănătate", "Zustand", "État"), Ui.FontXS, "TextSecondaryBrush"));
                var pct = HeroText($"{health}%", Ui.FontXS, healthKey, semibold: true);
                Grid.SetColumn(pct, 1); head.Children.Add(pct);
                left.Children.Add(head);
                left.Children.Add(new BatteryHealthBar { Health = health, FillKey = healthKey, Height = 6, Margin = new Thickness(0, 2, 0, 0) });

                if (bat.FullCapacityMWh > 0 && bat.DesignCapacityMWh > 0)
                {
                    double now = bat.FullCapacityMWh / 1000.0, design = bat.DesignCapacityMWh / 1000.0;
                    // new batteries sometimes report more than the design capacity ("67 of 60 Wh"): no wear to show
                    var capText = HeroText(now >= design
                        ? L($"Full charge {now:0.#} Wh · design {design:0.#} Wh · no wear",
                            $"Încărcare completă {now:0.#} Wh · de fabricație {design:0.#} Wh · fără uzură",
                            $"Volle Ladung {now:0.#} Wh · Nennwert {design:0.#} Wh · kein Verschleiß",
                            $"Charge complète {now:0.#} Wh · nominale {design:0.#} Wh · sans usure")
                        : L($"{now:0.#} of {design:0.#} Wh · {design - now:0.#} Wh lost to wear",
                            $"{now:0.#} din {design:0.#} Wh · {design - now:0.#} Wh pierduți prin uzură",
                            $"{now:0.#} von {design:0.#} Wh · {design - now:0.#} Wh durch Verschleiß verloren",
                            $"{now:0.#} sur {design:0.#} Wh · {design - now:0.#} Wh perdus par usure"),
                        Ui.FontXS, "TextSecondaryBrush");
                    if (now > design * 1.05)
                        capText.ToolTip = L("The battery reports a full charge above its design capacity. This is common on new batteries and is not a fault.",
                                            "Bateria raportează o încărcare completă peste capacitatea de fabricație. Este obișnuit la bateriile noi și nu este un defect.",
                                            "Der Akku meldet eine volle Ladung über der Nennkapazität. Das ist bei neuen Akkus üblich und kein Fehler.",
                                            "La batterie indique une charge complète supérieure à sa capacité nominale. C'est courant sur les batteries neuves et ce n'est pas un défaut.");
                    right.Children.Add(capText);
                }
                if (health < 65)
                {
                    var warn = HeroText(L("Worth replacing", "Merită înlocuită", "Sollte ersetzt werden", "À remplacer"), Ui.FontXS, "StatusCritBrush", semibold: true);
                    warn.Margin = new Thickness(0, 2, 0, 0);
                    right.Children.Add(warn);
                }
                left.Background = WpfBrushes.Transparent;
                left.ToolTip = L($"Battery health {health}%: how much of the factory capacity is left. Under about 65% the battery holds noticeably less and is worth replacing.",
                                  $"Sănătatea bateriei {health}%: cât a rămas din capacitatea de fabrică. Sub aproximativ 65% bateria ține vizibil mai puțin și merită înlocuită.",
                                  $"Akkuzustand {health}%: so viel der Werkskapazität ist noch übrig. Unter etwa 65% hält der Akku deutlich kürzer und sollte ersetzt werden.",
                                  $"État de la batterie {health}% : part restante de la capacité d'origine. Sous environ 65%, l'autonomie baisse nettement et un remplacement est à envisager.");
            }

            // cycles, type, manufacture date: on one row
            var facts = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            if (bat.CycleCount > 0)
                facts.Children.Add(HeroFact("Clock", hue, bat.CycleCount.ToString(), L("charge cycles", "cicluri de încărcare", "Ladezyklen", "cycles de charge")));
            if (Known(bat.Chemistry))
                facts.Children.Add(HeroFact("Layers", hue, bat.Chemistry,
                    L("type", "tip", "Typ", "type") + (Known(bat.Manufacturer) ? $" · {bat.Manufacturer}" : "")));
            else if (Known(bat.Manufacturer))
                facts.Children.Add(HeroFact("Layers", hue, bat.Manufacturer, L("manufacturer", "producător", "Hersteller", "fabricant")));
            if (Known(bat.ManufactureDate))
                facts.Children.Add(HeroFact("Clock", hue, bat.ManufactureDate, L("manufactured", "fabricată", "hergestellt", "fabriquée")));
            if (facts.Children.Count > 0) right.Children.Add(facts);

            // the large percentage sits where the capacity was (the card title), with the state next to it
            string state = bat.IsCharging ? L("charging", "se încarcă", "lädt", "en charge")
                         : bat.IsAC       ? L("plugged in", "la priză", "am Netz", "sur secteur")
                                          : L("on battery", "pe baterie", "im Akkubetrieb", "sur batterie");
            return HeroCard("Battery", L("Battery", "Baterie", "Akku", "Batterie"), $"{bat.ChargePercent}% · {state}", null, body);
        }

        // System devices (hubs, controllers, "Composite Device", "Input Device", generic HID) are not peripherals the user
        // recognises: they are counted in a small row, and their list stays in the tooltip.
        private static readonly System.Text.RegularExpressions.Regex SystemDeviceName = new(
            @"\bhub\b|root hub|composite device|input device|hid[- ]compliant|usb controller|host controller|generic usb|\bxhci\b|\behci\b|billboard|usb device$|unknown",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        private const int PeripheralChipMax = 6;

        private static readonly System.Text.RegularExpressions.Regex DeviceNameNoise = new(
            @"\((built-?in|integrated|internal|usb|wdm)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>The key used to remove duplicates: without "(Built-in)" and ignoring case / spacing differences.</summary>
        internal static string SameDeviceKey(string name) =>
            System.Text.RegularExpressions.Regex.Replace(DeviceNameNoise.Replace(name, ""), @"\s+", " ").Trim().ToLowerInvariant();

        private static string PeripheralIconKey(string name, bool audio)
            => PeripheralArt.KindFor(name) switch
            {
                PeripheralKind.Camera => "Camera",
                PeripheralKind.Keyboard => "Keyboard",
                PeripheralKind.Mouse => "Mouse",
                PeripheralKind.Printer => "Printer",
                PeripheralKind.Phone => "Phone",
                PeripheralKind.Headphones => "Headphones",
                PeripheralKind.Router => "Router",
                PeripheralKind.ExternalHdd or PeripheralKind.UsbStick => "Disk",
                PeripheralKind.Speakers => "Speaker",
                _ => audio ? "Speaker" : "Usb",
            };

        // ── Peripherals ───────────────────────────────────────────────────────
        // Chips with a category icon, on two columns (at most six, then "+N"); system devices are counted separately
        private Border? BuildPeripheralsHero()
        {
            if (_heroUsbs.Count == 0 && _heroAudio.Count == 0) return null;
            var hue = SpecSheet.Hue("Usb");

            var shown = new List<(string Name, bool Audio)>();
            var system = new List<string>();
            foreach (var n in _heroUsbs) { if (SystemDeviceName.IsMatch(n)) system.Add(n); else shown.Add((n, false)); }
            foreach (var n in _heroAudio) shown.Add((n, true));
            // the same device sometimes appears twice (e.g. "FaceTime HD Camera (Built-in)" and "FaceTime HD camera")
            shown = shown.GroupBy(x => SameDeviceKey(x.Name)).Select(g => g.OrderByDescending(x => x.Name.Length).First()).ToList();

            var body = new StackPanel { VerticalAlignment = WpfVAlign.Top };
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2, HorizontalAlignment = WpfHAlign.Stretch };
            foreach (var (name, audio) in shown.Take(PeripheralChipMax))
            {
                var chip = new Grid { Margin = new Thickness(0, 0, 8, 6) };
                chip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                chip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var icon = SpecSheet.IconGlyph(PeripheralIconKey(name, audio), hue, 16);
                icon.Margin = new Thickness(0, 0, 6, 0);
                chip.Children.Add(icon);
                var t = HeroText(name, Ui.FontS, "TextPrimaryBrush");
                Grid.SetColumn(t, 1); chip.Children.Add(t);
                chip.ToolTip = name;
                chip.Background = WpfBrushes.Transparent;
                grid.Children.Add(chip);
            }
            body.Children.Add(grid);

            var notes = new List<string>();
            if (shown.Count > PeripheralChipMax)
            {
                var rest = shown.Skip(PeripheralChipMax).Select(x => x.Name).ToList();
                var more = HeroText(L($"+ {rest.Count} more", $"+ încă {rest.Count}", $"+ {rest.Count} weitere", $"+ {rest.Count} de plus"), Ui.FontXS, "TextSecondaryBrush");
                more.ToolTip = string.Join("\n", rest);
                more.Margin = new Thickness(0, 0, 0, 2);
                body.Children.Add(more);
            }
            if (system.Count > 0)
            {
                var sys = HeroText(L($"+ {system.Count} system device{(system.Count == 1 ? "" : "s")}",
                                     $"+ {system.Count} {(system.Count == 1 ? "dispozitiv" : "dispozitive")} de sistem",
                                     $"+ {system.Count} Systemgerät{(system.Count == 1 ? "" : "e")}",
                                     $"+ {system.Count} périphérique{(system.Count == 1 ? "" : "s")} système"),
                                   Ui.FontXS, "TextSecondaryBrush");
                sys.ToolTip = string.Join("\n", system);
                body.Children.Add(sys);
            }

            string title = string.Join(" · ", new[]
            {
                _heroUsbs.Count > 0 ? $"{_heroUsbs.Count} USB" : "",
                _heroAudio.Count > 0 ? $"{_heroAudio.Count} audio" : "",
            }.Where(x => x.Length > 0));
            return HeroCard("Usb", L("Peripherals", "Periferice", "Peripheriegeräte", "Périphériques"), title, null, body);
        }
    }
}
