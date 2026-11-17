using System.Collections.Generic;
using System.Linq;
using System.Windows;
using SMDWin.Services;
using WpfBrush = System.Windows.Media.Brush;
using WpfApp   = System.Windows.Application;

namespace SMDWin.Views
{
    public partial class FeaturesWindow : Window
    {
        public FeaturesWindow()
        {
            SourceInitialized += (_, _) =>
            {
                try
                {
                    var helper = new System.Windows.Interop.WindowInteropHelper(this);
                    string theme = SMDWin.Services.SettingsService.Current.ThemeName;
                    SMDWin.Services.ThemeManager.ApplyTitleBarColor(helper.Handle, theme);
                    string resolved = SMDWin.Services.ThemeManager.Normalize(theme);
                    if (SMDWin.Services.ThemeManager.Themes.TryGetValue(resolved, out var t))
                        SMDWin.Services.ThemeManager.SetCaptionColor(helper.Handle, t["BgDark"]);
                }
                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            };
            InitializeComponent();
            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
            ApplyTheme();
            PopulateContent();
        }

        // ── Close ─────────────────────────────────────────────────────────────
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        // ── Theme ─────────────────────────────────────────────────────────────
        private void ApplyTheme()
        {
            // Inherit all resource dictionaries from the running app
            Resources.MergedDictionaries.Clear();
            foreach (ResourceDictionary rd in WpfApp.Current.Resources.MergedDictionaries)
                Resources.MergedDictionaries.Add(rd);

            // Copy all dynamic resources from App so DynamicResource works in this window
            foreach (var key in WpfApp.Current.Resources.Keys)
            {
                try { Resources[key] = WpfApp.Current.Resources[key]; } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            }

            // Window + banner background from theme resources
            var bgBrush   = (WpfBrush)WpfApp.Current.Resources["BgDarkBrush"];
            var cardBrush = (WpfBrush)WpfApp.Current.Resources["BgCardBrush"];
            var textBrush = (WpfBrush)WpfApp.Current.Resources["TextPrimaryBrush"];
            Background              = bgBrush;
            BannerBorder.Background = bgBrush;
            TxtAppName.Foreground   = textBrush;
            TxtBannerSub.Foreground = textBrush;

            // Force card backgrounds explicitly (DynamicResource may not propagate in child windows)
            var accentBrush = (WpfBrush)WpfApp.Current.Resources["AccentBrush"];
            var secondaryBrush = (WpfBrush)WpfApp.Current.Resources["TextSecondaryBrush"];
            var borderBrush = (WpfBrush)WpfApp.Current.Resources["BorderBrush2"];
            foreach (var card in new[] { Card1, Card2, Card3, Card4, Card5, Card6 })
            {
                card.Background  = cardBrush;
                card.BorderBrush = borderBrush;
            }
            // Force text colors on headers and icons
            foreach (var hdr in new[] { Hdr1, Hdr2, Hdr3, Hdr4, Hdr5, Hdr6 })
                hdr.Foreground = accentBrush;
            foreach (var ico in new[] { Ico1, Ico2, Ico3, Ico4, Ico5, Ico6 })
                ico.Foreground = textBrush;
            // Force ScrollViewer and ContentGrid background
            ContentGrid.Background = bgBrush;

            string themeName = SettingsService.Current.ThemeName ?? "Dark Midnight";
            if (themeName == "Auto")
                themeName = ThemeManager.ResolveAuto();

            Loaded += (_, _) =>
            {
                try
                {
                    var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    ThemeManager.ApplyTitleBarColor(handle, themeName);
                }
                catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            };
        }

        // ── Localised content ─────────────────────────────────────────────────
        private static string S(string key, string fallback = "")
        {
            var v = LanguageService.S("Features", key);
            // LanguageService returns "[Features.Key]" when key is missing
            return (v.Length > 0 && !v.StartsWith("[Features.")) ? v : fallback;
        }

        private static List<string> Items(string prefix)
        {
            var list = new List<string>();
            for (int i = 1; i <= 6; i++)   // max 6 items per column
            {
                string val = LanguageService.S("Features", $"{prefix}{i}");
                if (string.IsNullOrWhiteSpace(val) || val.StartsWith("[Features.")) break;
                list.Add("• " + val);
            }
            return list;
        }

        private void PopulateContent()
        {
            string lang = LanguageService.CurrentCode;
            bool isRo = lang == "ro";
            bool isDe = lang == "de";
            bool isFr = lang == "fr";

            Title = isRo ? "SMD Win — Capabilități"
                  : isDe ? "SMD Win — Funktionen"
                  : isFr ? "SMD Win — Fonctionnalités"
                  : "SMD Win — Capabilities";
            TxtBannerSub.Text = isRo
                ? "— Ce face aplicația, senzori disponibili și limitări cunoscute"
                : isDe ? "— Was die App tut, verfügbare Sensoren & bekannte Einschränkungen"
                : isFr ? "— Ce que fait l'application, capteurs disponibles et limites connues"
                : "— What the app does, available sensors & known limitations";

            var asmVer = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            string appVersion = SMDWin.Services.AppVersionInfo.Short;
            TxtFooter.Text = $"SMD Win {appVersion}  •  2026  •  Windows 10 / 11  •  .NET 8  •  WPF";

            // Card definitions: (icon, title_en, title_ro, title_de, title_fr,
            //                    items_en, items_ro, items_de, items_fr)
            var cardDefs = new[]
            {
                ("📡",
                 "Sensors — CPU & GPU", "Senzori — CPU & GPU",
                 "Sensoren — CPU & GPU", "Capteurs — CPU & GPU",
                 new[] {
                     "• CPU Package temp · Core temps per-thread",
                     "• CPU frequency (GHz) · load % per core",
                     "• CPU power consumption (W) · VCore voltage",
                     "• BDPROCHOT detection (thermal throttle flag)",
                     "• GPU temperature · load % · clock (MHz)",
                     "• GPU VRAM used / total · hotspot temp (GDDR6X)",
                 },
                 new[] {
                     "• Temp CPU Package · temp Core per fir",
                     "• Frecvență CPU (GHz) · load % per core",
                     "• Consum CPU (W) · tensiune VCore",
                     "• Detecție BDPROCHOT (thermal throttle flag)",
                     "• Temperatură GPU · load % · clock (MHz)",
                     "• VRAM GPU folosit / total · hotspot temp (GDDR6X)",
                 },
                 new[] {
                     "• CPU-Package-Temp · Kerntemp. pro Thread",
                     "• CPU-Frequenz (GHz) · Auslastung % pro Kern",
                     "• CPU-Leistungsaufnahme (W) · VCore-Spannung",
                     "• BDPROCHOT-Erkennung (Thermal-Throttle-Flag)",
                     "• GPU-Temperatur · Auslastung % · Takt (MHz)",
                     "• GPU-VRAM belegt / gesamt · Hotspot-Temp (GDDR6X)",
                 },
                 new[] {
                     "• Temp CPU Package · temp Core par thread",
                     "• Fréquence CPU (GHz) · charge % par cœur",
                     "• Consommation CPU (W) · tension VCore",
                     "• Détection BDPROCHOT (indicateur thermal throttle)",
                     "• Température GPU · charge % · fréquence (MHz)",
                     "• VRAM GPU utilisée / totale · temp hotspot (GDDR6X)",
                 }),

                ("💾",
                 "Sensors — Storage, RAM & Fans", "Senzori — Stocare, RAM & Fani",
                 "Sensoren — Speicher, RAM & Lüfter", "Capteurs — Stockage, RAM & Ventilateurs",
                 new[] {
                     "• S.M.A.R.T. temp (attr 0xC2) · health % · wear level",
                     "• Disk read/write speed (MB/s) real-time I/O",
                     "• RAM used / available (GB) · usage %",
                     "• RAM frequency (MHz) · XMP / EXPO profile",
                     "• Fan RPM per channel (CPU fan, chassis fans)",
                     "• Fan control detection via embedded controller",
                 },
                 new[] {
                     "• Temp S.M.A.R.T. (attr 0xC2) · sănătate % · wear level",
                     "• Viteză citire/scriere disc (MB/s) I/O real-time",
                     "• RAM folosit / disponibil (GB) · usage %",
                     "• Frecvență RAM (MHz) · profil XMP / EXPO",
                     "• RPM fani per canal (CPU fan, chassis fans)",
                     "• Detecție control fani via embedded controller",
                 },
                 new[] {
                     "• S.M.A.R.T.-Temp (Attr. 0xC2) · Zustand % · Wear-Level",
                     "• Lese-/Schreibgeschwindigkeit (MB/s) Echtzeit-I/O",
                     "• RAM belegt / verfügbar (GB) · Auslastung %",
                     "• RAM-Frequenz (MHz) · XMP-/EXPO-Profil",
                     "• Lüfter-RPM pro Kanal (CPU-Lüfter, Gehäuselüfter)",
                     "• Lüftersteuerung via Embedded Controller erkannt",
                 },
                 new[] {
                     "• Temp S.M.A.R.T. (attr 0xC2) · santé % · niveau d'usure",
                     "• Vitesse lecture/écriture disque (Mo/s) I/O temps réel",
                     "• RAM utilisée / disponible (Go) · utilisation %",
                     "• Fréquence RAM (MHz) · profil XMP / EXPO",
                     "• RPM ventilateurs par canal (CPU, boîtier)",
                     "• Détection contrôle ventilateurs via contrôleur intégré",
                 }),

                ("🔬",
                 "Libraries Used", "Librării Utilizate",
                 "Verwendete Bibliotheken", "Bibliothèques utilisées",
                 new[] {
                     "✦ LibreHardwareMonitor 0.9.6 — sensor core",
                     "✦ PawnIO — kernel-mode driver (MSR / EC access)",
                     "✦ System.Management — WMI queries (SMBIOS, OS info)",
                     "✦ PdfSharp — PDF export for system report",
                     "✦ .NET 8 WPF — UI framework, Windows-only",
                 },
                 new[] {
                     "✦ LibreHardwareMonitor 0.9.6 — nucleu senzori",
                     "✦ PawnIO — driver kernel-mode (acces MSR / EC)",
                     "✦ System.Management — interogări WMI (SMBIOS, OS)",
                     "✦ PdfSharp — export PDF raport sistem",
                     "✦ .NET 8 WPF — framework UI, exclusiv Windows",
                 },
                 new[] {
                     "✦ LibreHardwareMonitor 0.9.6 — Sensorkern",
                     "✦ PawnIO — Kernel-Mode-Treiber (MSR-/EC-Zugriff)",
                     "✦ System.Management — WMI-Abfragen (SMBIOS, OS-Info)",
                     "✦ PdfSharp — PDF-Export für Systembericht",
                     "✦ .NET 8 WPF — UI-Framework, nur Windows",
                 },
                 new[] {
                     "✦ LibreHardwareMonitor 0.9.6 — cœur des capteurs",
                     "✦ PawnIO — pilote kernel-mode (accès MSR / EC)",
                     "✦ System.Management — requêtes WMI (SMBIOS, infos OS)",
                     "✦ PdfSharp — export PDF du rapport système",
                     "✦ .NET 8 WPF — framework UI, Windows uniquement",
                 }),

                ("🛡",
                 "PawnIO — Kernel Driver", "PawnIO — Driver Kernel",
                 "PawnIO — Kernel-Treiber", "PawnIO — Pilote noyau",
                 new[] {
                     "⚡ Replaces WinRing0 — safer, actively maintained",
                     "⚡ Kernel-mode driver: reads CPU MSR registers",
                     "⚡ Enables VCore, power (W), BDPROCHOT readings",
                     "⚡ Accesses embedded controller (EC) for fan RPM",
                     "⚠ Requires Administrator rights to load",
                     "⚠ Without Admin: temps/load work, MSR data N/A",
                 },
                 new[] {
                     "⚡ Înlocuiește WinRing0 — mai sigur, menținut activ",
                     "⚡ Driver kernel-mode: citește registre MSR CPU",
                     "⚡ Permite citire VCore, putere (W), BDPROCHOT",
                     "⚡ Accesează embedded controller (EC) pentru RPM fani",
                     "⚠ Necesită drepturi de Administrator pentru încărcare",
                     "⚠ Fără Admin: temp/load funcționează, date MSR N/A",
                 },
                 new[] {
                     "⚡ Ersetzt WinRing0 — sicherer, aktiv gepflegt",
                     "⚡ Kernel-Mode-Treiber: liest CPU-MSR-Register",
                     "⚡ Ermöglicht VCore-, Leistungs- (W), BDPROCHOT-Werte",
                     "⚡ Greift für Lüfter-RPM auf Embedded Controller (EC) zu",
                     "⚠ Erfordert Administratorrechte zum Laden",
                     "⚠ Ohne Admin: Temp/Last funktioniert, MSR-Daten n. v.",
                 },
                 new[] {
                     "⚡ Remplace WinRing0 — plus sûr, activement maintenu",
                     "⚡ Pilote kernel-mode : lit les registres MSR du CPU",
                     "⚡ Permet lecture VCore, puissance (W), BDPROCHOT",
                     "⚡ Accède au contrôleur intégré (EC) pour RPM ventilateurs",
                     "⚠ Nécessite les droits Administrateur pour charger",
                     "⚠ Sans Admin : temp/charge fonctionnent, données MSR indispo",
                 }),

                ("🔧",
                 "Tools & Optimization", "Unelte & Optimizare",
                 "Tools & Optimierung", "Outils & Optimisation",
                 new[] {
                     "• Shutdown / Sleep / Hibernate timer with presets",
                     "• Auto-clean: Temp, Prefetch, WU cache, Recycle Bin",
                     "• Startup Manager: enable / disable startup entries",
                     "• Windows Services: start / stop / set startup type",
                     "• Driver Manager: detail card + update history",
                     "• Integrated PowerShell runner with quick commands",
                 },
                 new[] {
                     "• Timer Shutdown / Sleep / Hibernate cu preseturi",
                     "• Auto-clean: Temp, Prefetch, cache WU, Coș reciclare",
                     "• Startup Manager: activare/dezactivare programe boot",
                     "• Windows Services: start/stop/tip pornire",
                     "• Driver Manager: card detalii + istoric actualizări",
                     "• PowerShell runner integrat cu comenzi rapide",
                 },
                 new[] {
                     "• Shutdown-/Sleep-/Hibernate-Timer mit Vorlagen",
                     "• Auto-Clean: Temp, Prefetch, WU-Cache, Papierkorb",
                     "• Startup Manager: Autostart-Einträge aktivieren/deaktivieren",
                     "• Windows-Dienste: starten/stoppen/Starttyp festlegen",
                     "• Treiber-Manager: Detailkarte + Update-Verlauf",
                     "• Integrierter PowerShell-Runner mit Schnellbefehlen",
                 },
                 new[] {
                     "• Minuteur Arrêt / Veille / Hibernation avec préréglages",
                     "• Nettoyage auto : Temp, Prefetch, cache WU, Corbeille",
                     "• Startup Manager : activer/désactiver démarrages",
                     "• Services Windows : démarrer/arrêter/type de démarrage",
                     "• Gestionnaire de pilotes : détails + historique MAJ",
                     "• Exécuteur PowerShell intégré avec commandes rapides",
                 }),

                ("⚠️",
                 "Known Limitations", "Limitări Cunoscute",
                 "Bekannte Einschränkungen", "Limitations connues",
                 new[] {
                     "⚠ PawnIO requires Admin — MSR/EC data needs kernel driver",
                     "⚠ S.M.A.R.T. unavailable on some NVMe controllers via RAID",
                     "⚠ Wi-Fi saved passwords require Administrator privileges",
                     "⚠ GPU hotspot temp only on GDDR6X cards (RTX 3000+)",
                     "⚠ Fan RPM via EC may not work on all motherboards",
                 },
                 new[] {
                     "⚠ PawnIO necesită Admin — date MSR/EC cer driver kernel",
                     "⚠ S.M.A.R.T. indisponibil pe unele NVMe via RAID",
                     "⚠ Parole Wi-Fi necesită drepturi de Administrator",
                     "⚠ Temp hotspot GPU doar pe GDDR6X (RTX 3000+)",
                     "⚠ RPM fani via EC poate să nu funcționeze pe orice placă",
                 },
                 new[] {
                     "⚠ PawnIO erfordert Admin — MSR/EC-Daten brauchen Kernel-Treiber",
                     "⚠ S.M.A.R.T. bei manchen NVMe-Controllern via RAID nicht verfügbar",
                     "⚠ Gespeicherte WLAN-Passwörter erfordern Administratorrechte",
                     "⚠ GPU-Hotspot-Temp nur bei GDDR6X-Karten (RTX 3000+)",
                     "⚠ Lüfter-RPM via EC funktioniert nicht auf jedem Mainboard",
                 },
                 new[] {
                     "⚠ PawnIO nécessite Admin — données MSR/EC exigent un pilote kernel",
                     "⚠ S.M.A.R.T. indisponible sur certains contrôleurs NVMe via RAID",
                     "⚠ Mots de passe Wi-Fi enregistrés nécessitent droits Administrateur",
                     "⚠ Temp hotspot GPU uniquement sur cartes GDDR6X (RTX 3000+)",
                     "⚠ RPM ventilateurs via EC peut ne pas fonctionner sur toutes cartes mères",
                 }),
            };

            var cardControls = new[] {
                (Ico1, Hdr1, Lst1), (Ico2, Hdr2, Lst2), (Ico3, Hdr3, Lst3),
                (Ico4, Hdr4, Lst4), (Ico5, Hdr5, Lst5), (Ico6, Hdr6, Lst6),
            };

            var cards = cardDefs.Zip(cardControls, (def, ctrl) =>
                (ctrl.Item1, ctrl.Item2, ctrl.Item3, def.Item1,
                 isRo ? def.Item3 : isDe ? def.Item4 : isFr ? def.Item5 : def.Item2,
                 isRo ? def.Item7 : isDe ? def.Item8 : isFr ? def.Item9 : def.Item6)).ToArray();

            string themeName = SMDWin.Services.SettingsService.Current.ThemeName == "Auto"
                ? SMDWin.Services.ThemeManager.ResolveAuto() : SMDWin.Services.SettingsService.Current.ThemeName;
            bool light = SMDWin.Services.ThemeManager.IsLight(SMDWin.Services.ThemeManager.Normalize(themeName));
            foreach (var (ico, hdr, lst, icon, title, items) in cards)
            {
                ico.Text        = icon;
                hdr.Text        = title;
                lst.ItemsSource = items.ToList();

                // Known Limitations card — amber accent (a darker amber on Light: the bright one is hard to read on white)
                if (icon == "⚠️")
                    hdr.Foreground = new System.Windows.Media.SolidColorBrush(light
                        ? System.Windows.Media.Color.FromRgb(0xB4, 0x53, 0x09)
                        : System.Windows.Media.Color.FromRgb(245, 158, 11));

                // PawnIO card — purple/violet accent (darker on Light, same reason)
                if (icon == "🛡")
                    hdr.Foreground = new System.Windows.Media.SolidColorBrush(light
                        ? System.Windows.Media.Color.FromRgb(0x6D, 0x28, 0xD9)
                        : System.Windows.Media.Color.FromRgb(167, 139, 250));
            }
        }
    }
}
