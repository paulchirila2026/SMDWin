using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfColor = System.Windows.Media.Color;

namespace SMDWin.Views
{
    public partial class LicensesWindow : Window
    {
        private sealed record ThirdPartyEntry(string Name, string License, string Purpose, string Url);

        private static ThirdPartyEntry[] GetThirdPartyEntries(string lang) => lang switch
        {
            "ro" => new ThirdPartyEntry[]
            {
                new("LibreHardwareMonitorLib", "MPL 2.0",
                    "Citirea senzorilor hardware (temperaturi, tensiuni, ventilatoare, ceasuri)",
                    "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor"),
                new("SharpDX", "MIT",
                    "Acces DirectX/DXGI pentru testele de stres GPU",
                    "https://github.com/sharpdx/SharpDX"),
                new("PdfSharp", "MIT",
                    "Generarea rapoartelor PDF (Battery Report, System Info, Diagnostic Package)",
                    "https://github.com/empira/PDFsharp"),
                new("Serilog", "Apache 2.0",
                    "Logging intern al aplicației",
                    "https://github.com/serilog/serilog"),
                new("WPF-UI (lepoco)", "MIT",
                    "Controale de interfață în stil Windows 11 (butoane, liste, tabele)",
                    "https://github.com/lepoco/wpfui"),
                new("Fluent UI System Icons (Microsoft)", "MIT",
                    "Pictogramele aplicației (setul „regular”, 24 px)",
                    "https://github.com/microsoft/fluentui-system-icons"),
                new("smartctl (smartmontools 7.5, Tools\\smartctl.exe)", "GPL v2 or later",
                    "Citire SMART avansată pentru discuri — pornit ca proces extern separat, "
                    + "nu e link-uit direct în binarul SMD Win (respectă clauza de separație GPL)",
                    "https://github.com/smartmontools/smartmontools/releases/tag/RELEASE_7_5"),
                new("IEEE OUI database (Wireshark manuf)", "Licența Wireshark / date publice IEEE",
                    "Identificarea producătorului după adresa MAC (LAN Scanner)",
                    "https://www.wireshark.org/"),
                new("PawnIO", "GPL v2 (cu excepție de linking)",
                    "Driver semnat pentru acces low-level la hardware (temperaturi/tensiuni pe unele "
                    + "plăci unde LibreHardwareMonitor singur nu ajunge). Instalerul oficial e "
                    + "redistribuit nemodificat, ca proces separat — SMD Win comunică cu driverul "
                    + "prin interfața standard IOCTL, exact cazul acoperit de excepția de linking a "
                    + "autorului.",
                    "https://github.com/namazso/PawnIO"),
                new("buildcores-open-db", "ODC-By v1.0 (Open Data Commons Attribution)",
                    "Specificații hardware (CPU, GPU, RAM, Storage) folosite pentru identificarea "
                    + "corectă a tipului de disc, temperaturi critice și consum — completate manual "
                    + "cu surse oficiale (Intel ARK, AMD, NVIDIA) pentru modelele lipsă.",
                    "https://github.com/buildcores/buildcores-open-db"),
            },
            "de" => new ThirdPartyEntry[]
            {
                new("LibreHardwareMonitorLib", "MPL 2.0",
                    "Auslesen der Hardware-Sensoren (Temperaturen, Spannungen, Lüfter, Taktraten)",
                    "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor"),
                new("SharpDX", "MIT",
                    "DirectX/DXGI-Zugriff für die GPU-Stresstests",
                    "https://github.com/sharpdx/SharpDX"),
                new("PdfSharp", "MIT",
                    "Erstellung der PDF-Berichte (Battery Report, System Info, Diagnostic Package)",
                    "https://github.com/empira/PDFsharp"),
                new("Serilog", "Apache 2.0",
                    "Internes Logging der Anwendung",
                    "https://github.com/serilog/serilog"),
                new("WPF-UI (lepoco)", "MIT",
                    "Bedienelemente im Windows-11-Stil (Schaltflächen, Listen, Tabellen)",
                    "https://github.com/lepoco/wpfui"),
                new("Fluent UI System Icons (Microsoft)", "MIT",
                    "Symbole der Anwendung (Satz „regular“, 24 px)",
                    "https://github.com/microsoft/fluentui-system-icons"),
                new("smartctl (smartmontools 7.5, Tools\\smartctl.exe)", "GPL v2 or later",
                    "Erweiterte SMART-Auslesung für Laufwerke — als separater externer Prozess "
                    + "gestartet, nicht direkt in die SMD Win-Binärdatei eingebunden (entspricht der "
                    + "GPL-Trennungsklausel)",
                    "https://github.com/smartmontools/smartmontools/releases/tag/RELEASE_7_5"),
                new("IEEE OUI database (Wireshark manuf)", "Wireshark-Lizenz / öffentliche IEEE-Daten",
                    "Herstellererkennung anhand der MAC-Adresse (LAN Scanner)",
                    "https://www.wireshark.org/"),
                new("PawnIO", "GPL v2 (mit Linking-Ausnahme)",
                    "Signierter Treiber für Low-Level-Hardwarezugriff (Temperaturen/Spannungen auf "
                    + "manchen Mainboards, die LibreHardwareMonitor allein nicht erreicht). Der "
                    + "offizielle Installer wird unverändert, als separater Prozess, weiterverteilt — "
                    + "SMD Win kommuniziert mit dem Treiber über die Standard-IOCTL-Schnittstelle, "
                    + "genau der Fall, den die Linking-Ausnahme des Autors abdeckt.",
                    "https://github.com/namazso/PawnIO"),
                new("buildcores-open-db", "ODC-By v1.0 (Open Data Commons Attribution)",
                    "Hardware-Spezifikationen (CPU, GPU, RAM, Storage) zur korrekten Erkennung von "
                    + "Laufwerkstyp, kritischen Temperaturen und Leistungsaufnahme — manuell ergänzt "
                    + "mit offiziellen Quellen (Intel ARK, AMD, NVIDIA) für fehlende Modelle.",
                    "https://github.com/buildcores/buildcores-open-db"),
            },
            "fr" => new ThirdPartyEntry[]
            {
                new("LibreHardwareMonitorLib", "MPL 2.0",
                    "Lecture des capteurs matériels (températures, tensions, ventilateurs, fréquences)",
                    "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor"),
                new("SharpDX", "MIT",
                    "Accès DirectX/DXGI pour les tests de stress GPU",
                    "https://github.com/sharpdx/SharpDX"),
                new("PdfSharp", "MIT",
                    "Génération des rapports PDF (Battery Report, System Info, Diagnostic Package)",
                    "https://github.com/empira/PDFsharp"),
                new("Serilog", "Apache 2.0",
                    "Journalisation interne de l'application",
                    "https://github.com/serilog/serilog"),
                new("WPF-UI (lepoco)", "MIT",
                    "Contrôles d'interface de style Windows 11 (boutons, listes, tableaux)",
                    "https://github.com/lepoco/wpfui"),
                new("Fluent UI System Icons (Microsoft)", "MIT",
                    "Icônes de l’application (jeu « regular », 24 px)",
                    "https://github.com/microsoft/fluentui-system-icons"),
                new("smartctl (smartmontools 7.5, Tools\\smartctl.exe)", "GPL v2 or later",
                    "Lecture SMART avancée des disques — lancé comme processus externe séparé, "
                    + "non lié directement au binaire SMD Win (respecte la clause de séparation GPL)",
                    "https://github.com/smartmontools/smartmontools/releases/tag/RELEASE_7_5"),
                new("IEEE OUI database (Wireshark manuf)", "Licence Wireshark / données publiques IEEE",
                    "Identification du fabricant à partir de l'adresse MAC (LAN Scanner)",
                    "https://www.wireshark.org/"),
                new("PawnIO", "GPL v2 (avec exception de liaison)",
                    "Pilote signé pour l'accès matériel bas niveau (températures/tensions sur "
                    + "certaines cartes mères que LibreHardwareMonitor seul n'atteint pas). "
                    + "L'installateur officiel est redistribué tel quel, en tant que processus séparé "
                    + "— SMD Win communique avec le pilote via l'interface IOCTL standard, exactement "
                    + "le cas couvert par l'exception de liaison de l'auteur.",
                    "https://github.com/namazso/PawnIO"),
                new("buildcores-open-db", "ODC-By v1.0 (Open Data Commons Attribution)",
                    "Spécifications matérielles (CPU, GPU, RAM, Stockage) utilisées pour identifier "
                    + "correctement le type de disque, les températures critiques et la consommation "
                    + "— complétées manuellement avec des sources officielles (Intel ARK, AMD, NVIDIA) "
                    + "pour les modèles manquants.",
                    "https://github.com/buildcores/buildcores-open-db"),
            },
            _ => new ThirdPartyEntry[]
            {
                new("LibreHardwareMonitorLib", "MPL 2.0",
                    "Reads hardware sensors (temperatures, voltages, fans, clocks)",
                    "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor"),
                new("SharpDX", "MIT",
                    "DirectX/DXGI access for GPU stress tests",
                    "https://github.com/sharpdx/SharpDX"),
                new("PdfSharp", "MIT",
                    "Generates PDF reports (Battery Report, System Info, Diagnostic Package)",
                    "https://github.com/empira/PDFsharp"),
                new("Serilog", "Apache 2.0",
                    "Internal application logging",
                    "https://github.com/serilog/serilog"),
                new("WPF-UI (lepoco)", "MIT",
                    "Windows 11 style interface controls (buttons, lists, tables)",
                    "https://github.com/lepoco/wpfui"),
                new("Fluent UI System Icons (Microsoft)", "MIT",
                    "Application icons (\"regular\" set, 24 px)",
                    "https://github.com/microsoft/fluentui-system-icons"),
                new("smartctl (smartmontools 7.5, Tools\\smartctl.exe)", "GPL v2 or later",
                    "Advanced SMART disk reading — started as a separate external process, not "
                    + "linked directly into the SMD Win binary (respects the GPL separation clause)",
                    "https://github.com/smartmontools/smartmontools/releases/tag/RELEASE_7_5"),
                new("IEEE OUI database (Wireshark manuf)", "Wireshark license / public IEEE data",
                    "Identifies the manufacturer from a MAC address (LAN Scanner)",
                    "https://www.wireshark.org/"),
                new("PawnIO", "GPL v2 (with linking exception)",
                    "Signed driver for low-level hardware access (temperatures/voltages on some "
                    + "boards LibreHardwareMonitor alone can't reach). The official installer is "
                    + "redistributed unmodified, as a separate process — SMD Win talks to the driver "
                    + "through the standard IOCTL interface, exactly the case covered by the author's "
                    + "linking exception.",
                    "https://github.com/namazso/PawnIO"),
                new("buildcores-open-db", "ODC-By v1.0 (Open Data Commons Attribution)",
                    "Hardware specifications (CPU, GPU, RAM, Storage) used to correctly identify "
                    + "drive type, critical temperatures, and power draw — manually supplemented "
                    + "with official sources (Intel ARK, AMD, NVIDIA) for missing models.",
                    "https://github.com/buildcores/buildcores-open-db"),
            },
        };

        private readonly string _lang = SMDWin.Services.LanguageService.CurrentCode;

        public LicensesWindow()
        {
            InitializeComponent();
            PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };

            SourceInitialized += (_, _) =>
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                string theme = SMDWin.Services.SettingsService.Current.ThemeName == "Auto"
                    ? SMDWin.Services.ThemeManager.ResolveAuto()
                    : SMDWin.Services.SettingsService.Current.ThemeName;
                SMDWin.Services.ThemeManager.ApplyTitleBarColor(helper.Handle, theme);
                string resolved = SMDWin.Services.ThemeManager.Normalize(theme);
                if (SMDWin.Services.ThemeManager.Themes.TryGetValue(resolved, out var t))
                    SMDWin.Services.ThemeManager.SetCaptionColor(helper.Handle, t["BgDark"]);
            };
            SMDWin.Services.ThemeManager.Apply(
                SMDWin.Services.SettingsService.Current.ThemeName == "Auto"
                    ? SMDWin.Services.ThemeManager.ResolveAuto()
                    : SMDWin.Services.SettingsService.Current.ThemeName,
                this.Resources);

            ApplyLocalizedText();
            BuildThirdPartyList();
        }

        private void ApplyLocalizedText()
        {

            static string S(string key, string en) => SMDWin.Services.LanguageService.S("Licenses", key, en);
            (string winTitle, string appLicTitle, string appLicBody, string tpHeader, string tpSub) t = (
                S("Title", "Licenses"),
                S("AppTitle", "SMD Win — License"),
                S("AppBody", "SMD Win is free and open-source software, released under the GNU General Public License v3.0. "
                    + "You can use it at home or at work, read its code, change it and share it — changed versions are shared "
                    + "under the same license. It comes with no warranty: it is in Beta, so bugs can happen."),
                S("ThirdTitle", "Third-party components"),
                S("ThirdSub", "SMD Win uses the following components. Each keeps its own original license — the links lead to "
                    + "the project and its full license text."));
            if (TxtAppLicenseLink != null) TxtAppLicenseLink.Text = S("FullLicense", "Read the full license (gnu.org)") + " ↗";
            Title = t.winTitle;
            if (TxtWinTitle != null) TxtWinTitle.Text = t.winTitle;
            if (TxtAppLicenseTitle != null) TxtAppLicenseTitle.Text = t.appLicTitle;
            if (TxtAppLicenseBody != null) TxtAppLicenseBody.Text = t.appLicBody;
            if (TxtThirdPartyHeader != null) TxtThirdPartyHeader.Text = t.tpHeader;
            if (TxtThirdPartySub != null) TxtThirdPartySub.Text = t.tpSub;
        }

        private void BuildThirdPartyList()
        {
            if (ItemsThirdParty == null) return;
            var items = new List<UIElement>();

            foreach (var e in GetThirdPartyEntries(_lang))
            {
                var card = new Border
                {
                    Padding = new Thickness(14, 11, 14, 11),
                    Margin = new Thickness(0, 0, 0, 8),
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(1),
                };
                card.SetResourceReference(Border.BackgroundProperty, "CardSurfaceBrush");
                card.SetResourceReference(Border.BorderBrushProperty, "CardEdgeBrush");

                var stack = new StackPanel();

                var headerRow = new Grid();
                headerRow.ColumnDefinitions.Add(new ColumnDefinition());
                headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var nameTb = new TextBlock { Text = e.Name, FontWeight = FontWeights.SemiBold, FontSize = 13.5, TextWrapping = TextWrapping.Wrap };
                nameTb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                headerRow.Children.Add(nameTb);

                // the license as an outlined pill in the accent colour (follows the theme)
                var licBadge = new Border
                {
                    CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 2), BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 0, 0, 0),
                };
                licBadge.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                var licTb = new TextBlock { Text = e.License, FontSize = 11, FontWeight = FontWeights.SemiBold };
                licTb.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                licBadge.Child = licTb;
                Grid.SetColumn(licBadge, 1);
                headerRow.Children.Add(licBadge);

                stack.Children.Add(headerRow);

                var purposeTb = new TextBlock
                {
                    Text = e.Purpose, TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
                    Margin = new Thickness(0, 4, 0, 4),
                };
                purposeTb.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                stack.Children.Add(purposeTb);

                // the link as a short address ("github.com/serilog/serilog"), the full one in the tooltip
                string shortUrl = e.Url.Replace("https://", "").Replace("http://", "").Replace("www.", "").TrimEnd('/');
                var linkTb = new TextBlock
                {
                    Text = shortUrl + " ↗", FontSize = 11.5, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = e.Url,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                linkTb.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                linkTb.MouseLeftButtonUp += (_, _) =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Url) { UseShellExecute = true }); }
                    catch (System.Exception ex) { SMDWin.Services.AppLogger.Debug($"LicensesWindow open link: {ex.Message}"); }
                };
                stack.Children.Add(linkTb);

                card.Child = stack;
                items.Add(card);
            }

            ItemsThirdParty.ItemsSource = items;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void AppLicenseLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.gnu.org/licenses/gpl-3.0.html") { UseShellExecute = true }); }
            catch (System.Exception ex) { SMDWin.Services.AppLogger.Debug($"LicensesWindow open GPL link: {ex.Message}"); }
        }

        private void TitleBar_DragMove(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }
    }
}
