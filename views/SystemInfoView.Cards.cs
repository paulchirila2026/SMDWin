using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SMDWin.Models;
using SMDWin.Services;

// Explicit aliases — avoid conflicts with System.Windows.Forms.* and System.IO.*
using WpfColor       = System.Windows.Media.Color;
using WpfButton      = System.Windows.Controls.Button;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfTextBox     = System.Windows.Controls.TextBox;
using WpfClipboard   = System.Windows.Clipboard;
using WpfFontFamily  = System.Windows.Media.FontFamily;
using WpfHAlign      = System.Windows.HorizontalAlignment;
using WpfVAlign      = System.Windows.VerticalAlignment;
using WpfTabControl  = System.Windows.Controls.TabControl;
using WpfTabItem     = System.Windows.Controls.TabItem;
using WpfBrushes     = System.Windows.Media.Brushes;
using PenLineCap     = System.Windows.Media.PenLineCap;
using PenLineJoin    = System.Windows.Media.PenLineJoin;
using WpfPath        = System.Windows.Shapes.Path;
using WpfCursors     = System.Windows.Input.Cursors;
using WpfViewbox     = System.Windows.Controls.Viewbox;
using WpfCanvas      = System.Windows.Controls.Canvas;
using WpfPanel       = System.Windows.Controls.Panel;

namespace SMDWin.Views
{
    public partial class SystemInfoView
    {
        // ── Build all cards ───────────────────────────────────────────────────
        // the WMI queries run on a background thread, in parallel with the other loads, so the window does not block when opening.
        private readonly record struct EnclosureInfo(ushort[]? ChassisTypes, string? SerialNumber, string? AssetTag);

        private static List<ManagementObject> FetchGpuObjects()
        {
            try
            {
                using var gpuS = SMDWin.Services.WmiHelper.Searcher(
                    "SELECT Name, AdapterCompatibility, DriverVersion, DriverDate, VideoProcessor, " +
                    "AdapterRAM, CurrentBitsPerPixel, CurrentHorizontalResolution, CurrentVerticalResolution, " +
                    "CurrentRefreshRate FROM Win32_VideoController", System.TimeSpan.FromSeconds(10));
                using var col = gpuS.Get();
                return col.Cast<ManagementObject>().ToList();
            }
            catch (Exception ex) { AppLogger.Debug($"FetchGpuObjects: {ex.Message}"); return new(); }
        }

        /// <summary>Like FetchGpuObjects/FetchEnclosureInfo above — the WMI query for the "friendly" monitor names
        /// (WmiMonitorID) runs on a background thread; Screen.AllScreens and EnumDisplaySettings/EnumDisplayDevices stay in
        /// BuildAllCards (fast Win32 calls, not WMI, negligible cost on the UI thread).</summary>
        private static (Dictionary<string, string> friendlyByHwId, Dictionary<string, string> serialByHwId) FetchMonitorFriendlyNames()
        {
            var friendlyByHwId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var serialByHwId   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var monS = SMDWin.Services.WmiHelper.Searcher(@"root\wmi",
                    "SELECT InstanceName, UserFriendlyName, UserFriendlyNameLength, SerialNumberID FROM WmiMonitorID", System.TimeSpan.FromSeconds(10));
                using var col = monS.Get();
                foreach (ManagementObject mo in col)
                {
                    string instanceName = mo["InstanceName"]?.ToString() ?? "";
                    if (mo["UserFriendlyName"] is ushort[] chars && mo["UserFriendlyNameLength"] is byte len && len > 0)
                    {
                        var sbName = new StringBuilder();
                        for (int i = 0; i < len && i < chars.Length; i++)
                            if (chars[i] != 0) sbName.Append((char)chars[i]);
                        string friendly = sbName.ToString().Trim();
                        var segs = instanceName.Split('\\');
                        if (segs.Length > 1 && !string.IsNullOrEmpty(friendly))
                            friendlyByHwId[segs[1]] = friendly;
                    }
                    if (mo["SerialNumberID"] is ushort[] serChars)
                    {
                        var sbSer = new StringBuilder();
                        foreach (var c in serChars)
                            if (c != 0) sbSer.Append((char)c);
                        string serial = sbSer.ToString().Trim();
                        var segs = instanceName.Split('\\');
                        if (segs.Length > 1 && !string.IsNullOrEmpty(serial))
                            serialByHwId[segs[1]] = serial;
                    }
                }
            }
            catch (Exception ex) { AppLogger.Debug($"FetchMonitorFriendlyNames: {ex.Message}"); }
            return (friendlyByHwId, serialByHwId);
        }

        /// <summary>The Win32_SoundDevice query + building the rows (plain text, no UI elements) — can run entirely on a
        /// background thread.</summary>
        private static List<(string, string)> FetchAudioRows()
        {
            var audioRows = new List<(string, string)>();
            try
            {
                using var audS = SMDWin.Services.WmiHelper.Searcher("SELECT Name, Manufacturer, Status FROM Win32_SoundDevice", System.TimeSpan.FromSeconds(10));
                using var col = audS.Get();
                int audIdx = 1;
                foreach (ManagementObject ao in col)
                {
                    string nm = ao["Name"]?.ToString() ?? "—";
                    string mf = ao["Manufacturer"]?.ToString() ?? "";
                    string st = ao["Status"]?.ToString() ?? "";
                    string status = st != "OK" && !string.IsNullOrEmpty(st) ? $"· {st}" : "";
                    string audioValue = string.IsNullOrWhiteSpace($"{mf}{status}") ? nm : $"{nm}\n{mf} {status}".TrimEnd();
                    audioRows.Add(($"{audIdx++}.", audioValue));
                }
            }
            catch (Exception ex) { AppLogger.Debug($"FetchAudioRows: {ex.Message}"); }
            return audioRows;
        }

        private static EnclosureInfo FetchEnclosureInfo()
        {
            try
            {
                using var encS = SMDWin.Services.WmiHelper.Searcher("SELECT ChassisTypes, SerialNumber, SMBIOSAssetTag FROM Win32_SystemEnclosure", System.TimeSpan.FromSeconds(10));
                using var col = encS.Get();
                var encObj = col.Cast<ManagementObject>().FirstOrDefault();
                return new EnclosureInfo(
                    encObj?["ChassisTypes"] as ushort[],
                    encObj?["SerialNumber"]?.ToString(),
                    encObj?["SMBIOSAssetTag"]?.ToString());
            }
            catch (Exception ex) { AppLogger.Debug($"FetchEnclosureInfo: {ex.Message}"); return new EnclosureInfo(null, null, null); }
        }

        private void BuildAllCards(
            SystemSummary           summary,
            BatteryInfo             bat,
            List<NetworkAdapterEntry> nets,
            List<RamEntry>          rams,
            List<DiskHealthEntry>   disks,
            List<TemperatureEntry>  temps,
            List<string>            usbs,
            List<string>            bluetooths,
            List<ManagementObject>  gpuObjsRaw,
            EnclosureInfo           enclosureInfo,
            (Dictionary<string, string> friendlyByHwId, Dictionary<string, string> serialByHwId) monitorNames,
            Dictionary<string, MonitorPhysical> monitorPhysical,
            List<(string, string)> audioRowsPrefetched,
            bool layout = true)
        {
            rams ??= new(); disks ??= new(); usbs ??= new();
            _tileSummary = summary; _tileRams = rams; _tileDisks = disks; _tileBattery = bat;
            _heroGpus.Clear(); _heroScreens.Clear(); _heroNets = new(); _heroNetsOff = new(); _heroBluetooth = null;
            _heroUsbs = usbs; _heroAudio = new(); _heroBoard = null;
            // for the drawings: a laptop chassis (or battery) and graphics integrated in the processor
            _heroLaptop = bat.Present || (enclosureInfo.ChassisTypes?.Any(t => t is 8 or 9 or 10 or 14 or 30 or 31 or 32) ?? false);
            _heroComputer = ComputerPackage.KindFor(enclosureInfo.ChassisTypes, summary.Manufacturer, summary.Model, _heroLaptop);
            try { _heroIntegratedGraphics = gpuObjsRaw.Any(o => GpuPackage.IsIntegrated(o["Name"]?.ToString())); }
            catch (Exception ex) { AppLogger.Debug($"Integrated graphics check: {ex.Message}"); }
            if (_txtHeaderSubtitle != null)
            {
                string model = !string.IsNullOrWhiteSpace(summary.Manufacturer)
                    ? DisplayFormat.MachineName(summary.Manufacturer, summary.Model)
                    : (!string.IsNullOrWhiteSpace(summary.Model) ? summary.Model : "");
                string pc = !string.IsNullOrWhiteSpace(summary.ComputerName) ? summary.ComputerName : Environment.MachineName;
                _txtHeaderSubtitle.Text = string.Join("  ·  ", new[] { model, pc, summary.OsName }
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
            }

            var cpuRows = new List<(string, string)>
            {
                (L("Cores / Threads","Nuclee / Fire","Kerne / Threads","Cœurs / Threads"), summary.CpuCores),
                (L("Max Clock","Frecvență Max","Max. Takt","Fréq. max"),        summary.CpuMaxMHz),
                (L("Cache","Cache","Cache","Cache"),                   summary.CpuCache),
            };
            try
            {
                var dbCpu = SMDWin.Services.HardwareDatabaseService.LookupCpu(summary.Cpu);
                if (dbCpu != null)
                {
                    if (dbCpu.TdpW > 0)
                        cpuRows.Add((L("TDP","TDP","TDP","TDP"), $"{dbCpu.TdpW:0} W"));
                    if (!string.IsNullOrEmpty(dbCpu.Socket))
                        cpuRows.Add((L("Socket","Socket","Sockel","Socket"), dbCpu.Socket));
                    if (!string.IsNullOrEmpty(dbCpu.Microarchitecture))
                        cpuRows.Add((L("Architecture","Arhitectură","Architektur","Architecture"), dbCpu.Microarchitecture));
                    if (dbCpu.TjMaxC.HasValue)
                        cpuRows.Add(("TjMax", $"{TempUnit.Fmt(dbCpu.TjMaxC)}"));
                }
            }
            catch (Exception ex) { AppLogger.Debug($"CPU DB lookup: {ex.Message}"); }
            var cpuCard = AddCard(_tabPanels["CPU"], L("Processor","Procesor","Prozessor","Processeur"),
                summary.Cpu, "Cpu", cpuRows, searchTerm: summary.Cpu);
            SMDWin.Views.TechTooltip.SetText(cpuCard, SMDWin.Services.HardwareDatabaseService.LookupCpu(summary.Cpu) != null
                ? "✓ In the local hardware database."
                : "✗ Not in the local hardware database yet.");

            int totalSlots     = rams.Count;
            int populatedSlots = rams.Count(r => !r.IsEmpty);
            var populated       = rams.Where(r => !r.IsEmpty).ToList();
            int ratedMax        = populated.Count > 0 ? populated.Max(r => r.RatedMHz) : 0;
            int configuredMax   = populated.Count > 0 ? populated.Max(r => r.ConfiguredMHz) : 0;
            string freqText = ratedMax > 0
                ? (configuredMax > 0 && configuredMax != ratedMax
                    ? $"{configuredMax} MHz ({L("rated","nominal","nominal","nominal")} {ratedMax} MHz)"
                    : $"{ratedMax} MHz")
                : "—";
            var ramRows = new List<(string, string)>
            {
                (L("Total","Total","Gesamt","Total"), summary.TotalRam),
                (L("Type","Tip","Typ","Type"),    summary.RamType),
                (L("Frequency","Frecvență","Frequenz","Fréquence"), freqText),
                (L("Slots","Sloturi","Steckplätze","Emplacements"), $"{populatedSlots} / {totalSlots} " + L("used","ocupate","belegt","utilisés")),
            };
            foreach (var r in populated)
            {
                string voltTxt = r.VoltageV > 0 ? $" · {r.VoltageV:F2}V" : "";
                // ConfiguredMHz can be 0 on some boards (e.g. iMac/Boot Camp) — then the nominal speed
                int bankMhz = r.ConfiguredMHz > 0 ? r.ConfiguredMHz : r.RatedMHz;
                string mhzTxt = bankMhz > 0 ? $" · {bankMhz} MHz" : "";
                string bankName = !string.IsNullOrWhiteSpace(r.SlotLabel) ? r.SlotLabel : r.BankLabel;
                ramRows.Add(($"  {bankName}", $"{r.Capacity} {r.MemoryType}{mhzTxt}{voltTxt}"
                    + (string.IsNullOrWhiteSpace(r.Manufacturer) ? "" : $" · {r.Manufacturer}")));
            }
            AddCard(_tabPanels["Memory"], L("Memory (RAM)","Memorie (RAM)","Speicher (RAM)","Mémoire (RAM)"),
                $"{summary.TotalRam} · {summary.RamType}", "Ram", ramRows,
                searchTerm: BuildRamSearchTerm(rams, summary));

            try
            {
                static bool IsIntegratedGpu(ManagementObject o) => GpuPackage.IsIntegrated(o["Name"]?.ToString());
                // A stable OrderBy — integrated (false→0 after negation) first, dedicated after.
                var gpuObjs = gpuObjsRaw.OrderByDescending(IsIntegratedGpu).ToList();

                int gpuIdx = 0;
                foreach (var go in gpuObjs)
                {
                    gpuIdx++;
                    string gpuName = go["Name"]?.ToString()?.Trim() ?? summary.GpuName ?? "";
                    if (string.IsNullOrWhiteSpace(gpuName)) continue;

                    long rawRam = 0;
                    try { rawRam = Convert.ToInt64(go["AdapterRAM"]); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                    double vramGB = rawRam / (1024.0 * 1024 * 1024);
                    bool isIntegrated = IsIntegratedGpu(go);
                    // Windows (WMI) gives the video memory as 32 bits, so at most 4 GB; for the card in the summary the value read through
                    // DirectX is used, which is complete
                    bool inSummary = !string.IsNullOrEmpty(summary.GpuVram) && summary.GpuVram != "—"
                        && (gpuObjs.Count == 1 || string.Equals(summary.GpuName?.Trim(), gpuName, StringComparison.OrdinalIgnoreCase));
                    string dedicatedVram = inSummary ? summary.GpuVram
                                         : (vramGB > 0 && vramGB <= 24) ? $"{vramGB:F1} GB"
                                         : "—";
                    string vramText = isIntegrated ? L("Shared (integrated)","Partajată (integrată)","Gemeinsam (integriert)","Partagée (intégrée)")
                                     : dedicatedVram;

                    string driverVer  = go["DriverVersion"]?.ToString() ?? "";
                    string driverDateRaw = go["DriverDate"]?.ToString() ?? "";
                    string driverDate = "";
                    if (!string.IsNullOrEmpty(driverDateRaw))
                    {
                        try { driverDate = SMDWin.Services.DateFormatHelper.FormatDate(ManagementDateTimeConverter.ToDateTime(driverDateRaw)); }
                        catch (Exception ex) { AppLogger.Debug(ex.Message); }
                    }
                    int bpp = go["CurrentBitsPerPixel"] is uint b ? (int)b : 0;
                    int cw  = go["CurrentHorizontalResolution"] is uint w ? (int)w : 0;
                    int ch  = go["CurrentVerticalResolution"]   is uint h ? (int)h : 0;
                    int hz = 0;
                    try
                    {
                        var primaryScreen = System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens.FirstOrDefault();
                        if (primaryScreen != null)
                        {
                            var dmGpu = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };
                            if (EnumDisplaySettings(primaryScreen.DeviceName, ENUM_CURRENT_SETTINGS, ref dmGpu))
                                hz = dmGpu.dmDisplayFrequency;
                        }
                    }
                    catch (Exception ex) { AppLogger.Debug($"GpuCard EnumDisplaySettings: {ex.Message}"); }
                    if (hz <= 1) hz = go["CurrentRefreshRate"] is uint r ? (int)r : 0; // WMI fallback only if the precise API did not work at all

                    var gpuRows = new List<(string, string)>
                    {
                        (L("Type","Tip","Typ","Type"), isIntegrated
                            ? L("Integrated","Integrată","Integriert","Intégré")
                            : L("Dedicated","Dedicată","Dediziert","Dédié")),
                    };
                    string chip = go["VideoProcessor"]?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(chip) && chip != gpuName)
                        gpuRows.Add((L("Chip","Cip","Chip","Puce"), chip));
                    gpuRows.Add((L("VRAM","VRAM","VRAM","VRAM"), vramText));
                    string vendor = go["AdapterCompatibility"]?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(vendor))
                        gpuRows.Add((L("Vendor","Producător","Hersteller","Fournisseur"), vendor));
                    if (!string.IsNullOrWhiteSpace(driverVer))
                        gpuRows.Add((L("Driver Version","Versiune Driver","Treiberversion","Version du pilote"),
                            driverDate.Length > 0 ? $"{driverVer}  ({driverDate})" : driverVer));
                    if (cw > 0 && ch > 0)
                        gpuRows.Add((L("Current Mode","Mod curent","Aktueller Modus","Mode actuel"),
                            $"{cw}×{ch}" + (hz > 0 ? $" @ {hz}Hz" : "") + (bpp > 0 ? $" · {bpp}-bit" : "")));

                    double gpuTdp = 0;
                    try
                    {
                        var dbGpu = SMDWin.Services.HardwareDatabaseService.LookupGpu(gpuName);
                        if (dbGpu != null && dbGpu.TdpW > 0)
                        {
                            gpuTdp = dbGpu.TdpW.Value;
                            gpuRows.Add(("TDP", $"{dbGpu.TdpW:0} W"));
                        }
                    }
                    catch (Exception ex) { AppLogger.Debug($"GPU DB lookup: {ex.Message}"); }
                    _heroGpus.Add(new HeroGpu(gpuName, isIntegrated, isIntegrated ? "" : dedicatedVram, driverVer, driverDate, gpuTdp));

                    string cardTitle = gpuObjs.Count > 1 ? $"{L("Graphics","Placă Video","Grafik","Graphique")} {gpuIdx}" : L("Graphics","Placă Video","Grafik","Graphique");
                    var gpuCard = AddCard(_tabPanels["Graphics"], cardTitle, gpuName, "Gpu", gpuRows, searchTerm: gpuName);
                    SMDWin.Views.TechTooltip.SetText(gpuCard, SMDWin.Services.HardwareDatabaseService.LookupGpu(gpuName) != null
                        ? $"✓ În baza de date locală (buildcores-open-db)."
                        : $"✗ NU e în baza de date locală — de adăugat la o sesiune viitoare.");
                }
                if (gpuObjs.Count == 0)
                {
                    if (!string.IsNullOrWhiteSpace(summary.GpuName))
                        _heroGpus.Add(new HeroGpu(summary.GpuName, GpuPackage.IsIntegrated(summary.GpuName), summary.GpuVram ?? "", summary.GpuDriverVersion ?? "", "", 0));
                    // Fallback to the summary if WMI returned nothing
                    AddCard(_tabPanels["Graphics"], L("Graphics","Placă Video","Grafik","Graphique"),
                        summary.GpuName ?? "", "Gpu",
                        new List<(string, string)>
                        {
                            (L("VRAM","VRAM","VRAM","VRAM"), summary.GpuVram ?? ""),
                        }, searchTerm: summary.GpuName ?? "");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"GraphicsCard: {ex.Message}");
                AddCard(_tabPanels["Graphics"], L("Graphics","Placă Video","Grafik","Graphique"),
                    summary.GpuName, "Gpu",
                    new List<(string, string)>(),
                    searchTerm: summary.GpuName);
            }

            foreach (var disk in disks)
            {
                var diskRows = new List<(string, string)>
                {
                    (L("Capacity","Capacitate","Kapazität","Capacité"), disk.Size),
                    (L("Type","Tip","Typ","Type"),            disk.TypeText),   // "M.2 SATA SSD" — also on the PDF sheet
                    (L("Status","Status","Status","État"),       disk.Status),
                    (L("Health","Sănătate","Zustand","État"),     $"{disk.HealthPercent}%"),
                };
                if (disk.UsbLinkText.Length > 0)
                    diskRows.Add((L("Connection","Conexiune","Verbindung","Connexion"), disk.UsbLinkText));
                if (!string.IsNullOrWhiteSpace(disk.SerialNumber))
                    diskRows.Add((L("Serial","Serial","Seriennummer","Numéro de série"), disk.SerialNumber));
                var diskCard = AddCard(_tabPanels["Storage"], L("Storage","Stocare","Speicherplatz","Stockage"),
                    disk.Model, "Disk", diskRows, searchTerm: disk.Model);
                SMDWin.Views.TechTooltip.SetText(diskCard, SMDWin.Services.HardwareDatabaseService.LookupStorage(disk.Model) != null
                    ? $"✓ În baza de date locală (buildcores-open-db)."
                    : $"✗ NU e în baza de date locală — de adăugat la o sesiune viitoare.");
            }

            if (bat.Present)
            {
                bool hasWear = bat.WearPct.HasValue;
                int hp = hasWear ? Math.Max(0, 100 - bat.WearPct!.Value) : 0;
                AddCard(_tabPanels["Battery"], L("Battery","Baterie","Akku","Batterie"),
                    bat.DesignCapacityMWh > 0 ? $"{bat.DesignCapacityMWh / 1000.0:F0} Wh design" : bat.Name,
                    "Battery",
                    new List<(string, string)>
                    {
                        (L("Manufacturer","Producător","Hersteller","Fabricant"),               bat.Manufacturer),
                        (L("Chemistry","Chimie","Chemie","Chimie"),                      bat.Chemistry),
                        (L("Health","Sănătate","Zustand","État"),                       hasWear ? $"{hp}%" : "—"),
                        (L("Cycle Count","Cicluri","Ladezyklen","Nombre de cycles"),                   bat.CycleCount > 0 ? bat.CycleCount.ToString() : "—"),
                        (L("Design Capacity","Capacitate Proiectată","Nennkapazität","Capacité nominale"), bat.DesignCapacityMWh > 0 ? $"{bat.DesignCapacityMWh} mWh" : "—"),
                        (L("Current Capacity","Capacitate Curentă","Aktuelle Kapazität","Capacité actuelle"),   bat.FullCapacityMWh  > 0 ? $"{bat.FullCapacityMWh} mWh"  : "—"),
                    });
            }

            try
            {
                _heroChassisType = ""; _heroChassisSerial = "";
                string chassisType = "—", serial = "—", assetTag = "—";
                if (enclosureInfo.ChassisTypes is { Length: > 0 } types)
                    chassisType = types[0] switch
                    {
                        8 or 9 or 10 or 14 => L("Laptop","Laptop","Laptop","Portable"),
                        3 or 4             => L("Desktop","Desktop","Desktop","Bureau"),
                        5 or 6 or 7        => L("Tower","Tower","Tower","Tour"),
                        30 or 31 or 32     => L("Tablet","Tabletă","Tablet","Tablette"),
                        _                  => L("Other","Altul","Andere","Autre"),
                    };
                serial   = enclosureInfo.SerialNumber?.Trim() is { Length: > 0 } sn ? sn : "—";
                assetTag = enclosureInfo.AssetTag?.Trim() is { Length: > 0 } at && at != "0" ? at : "—";
                _heroChassisType = chassisType == "—" ? "" : chassisType;
                _heroChassisSerial = serial == "—" ? "" : serial;
                AddCard(_tabPanels["Chassis"], L("Chassis","Șasiu","Gehäuse","Châssis"), chassisType, "Chassis",
                    new List<(string, string)>
                    {
                        (L("Type","Tip","Typ","Type"), chassisType),
                        (L("Serial","Serial","Seriennummer","Numéro de série"), serial),
                        (L("Asset Tag","Asset Tag","Asset-Tag","Étiquette d'actif"), assetTag),
                    });
            }
            catch (Exception ex) { AppLogger.Debug($"ChassisCard: {ex.Message}"); }

            // 8. Network
            // one row per adapter — label = the type (Wi-Fi / Ethernet), value = the IP, and under it the card name + MAC + speed.
            // Bluetooth is a row here, not a separate card.
            var netRows = new List<(string, string)>();
            // The Bluetooth PAN adapter shows "Up" without an IP — not a real connection, it goes to "Inactive"
            static bool IsIdleBtPan(NetworkAdapterEntry n) => !HasIp(n)
                && (n.Type.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) || n.Name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase));
            var activeNets = nets.Where(n => (n.Status == "Up" || HasIp(n)) && !IsIdleBtPan(n)).ToList();
            foreach (var n in activeNets)
            {
                string type = string.IsNullOrWhiteSpace(n.Type) ? L("Adapter","Adaptor","Adapter","Adaptateur") : n.Type;
                string ip = HasIp(n) ? n.IpAddress : L("no IP","fără IP","keine IP","pas d'IP");
                string detail = string.Join("  ·  ", new[] { n.Name, n.MacAddress, n.Speed }
                    .Where(x => !string.IsNullOrWhiteSpace(x) && x != "—"));
                netRows.Add((type, detail.Length > 0 ? $"{ip}\n{detail}" : ip));
            }
            var offNets = nets.Where(n => !activeNets.Contains(n)).ToList();
            if (offNets.Count > 0)
                netRows.Add((L("Inactive","Inactive","Inaktiv","Inactifs"),
                    string.Join(", ", offNets.Take(4).Select(n => string.IsNullOrWhiteSpace(n.Type) ? n.Name : n.Type))
                    + (offNets.Count > 4 ? $" +{offNets.Count - 4}" : "")));
            _heroNets = activeNets;
            _heroNetsOff = offNets;
            if (bluetooths.Any())
            {
                string btVer = MainWindow.GetBluetoothVersionViaApi() ?? "";
                _heroBluetooth = btVer;
                netRows.Add(("Bluetooth", string.IsNullOrEmpty(btVer) ? L("Present", "Prezent", "Vorhanden", "Présent") : btVer));
            }
            if (!netRows.Any())
                netRows.Add((L("No adapters","Nicio placă","Keine Adapter","Aucun adaptateur"), "—"));
            AddCard(_tabPanels["Network"], L("Network","Rețea","Netzwerk","Réseau"),
                activeNets.Count > 0 ? $"{activeNets.Count} " + L("active","active","aktiv","actives") : L("Not connected","Neconectat","Nicht verbunden","Non connecté"),
                "Wifi", netRows);

            var usbRows = usbs.Any()
                ? usbs.Take(20).Select((u, idx) => ($"{idx + 1}.", u)).ToList()
                : new List<(string, string)>();

            try
            {
                var smbios = SmbiosReader.Read();
                string mbMfg  = SMDWin.Services.HardwareInfo.IsJunkBoardValue(smbios.BoardManufacturer) ? "" : smbios.BoardManufacturer;
                string mbProd = SMDWin.Services.HardwareInfo.IsJunkBoardValue(smbios.BoardProduct)      ? "" : smbios.BoardProduct;
                string mbVer  = SMDWin.Services.HardwareInfo.IsJunkBoardValue(smbios.BoardVersion)      ? "" : smbios.BoardVersion;

                if (string.IsNullOrEmpty(mbMfg) && string.IsNullOrEmpty(mbProd))
                {
                    try
                    {
                        using var s = WmiHelper.Searcher("SELECT Manufacturer,Product,Version FROM Win32_BaseBoard");
                        using (var __wmiTmp1 = s.Get())
                        {
                            foreach (ManagementObject o in __wmiTmp1)
                        {
                            string wMfg = o["Manufacturer"]?.ToString()?.Trim() ?? "";
                            string wProd = o["Product"]?.ToString()?.Trim() ?? "";
                            string wVer = o["Version"]?.ToString()?.Trim() ?? "";
                            if (!SMDWin.Services.HardwareInfo.IsJunkBoardValue(wMfg))  mbMfg  = wMfg;
                            if (!SMDWin.Services.HardwareInfo.IsJunkBoardValue(wProd)) mbProd = wProd;
                            if (!SMDWin.Services.HardwareInfo.IsJunkBoardValue(wVer))  mbVer  = wVer;
                            break;
                        }
                        }
                    }
                    catch (Exception ex) { AppLogger.Debug($"MotherboardCard WMI fallback: {ex.Message}"); }
                }

                string naText = L("Not reported by this motherboard","Neraportat de această placă de bază","Von diesem Mainboard nicht gemeldet","Non signalé par cette carte mère");
                var mbRows = new List<(string, string)>
                {
                    (L("Manufacturer","Producător","Hersteller","Fabricant"),  string.IsNullOrEmpty(mbMfg)  ? naText : mbMfg),
                    (L("Model","Model","Modell","Modèle"),              string.IsNullOrEmpty(mbProd) ? naText : mbProd),
                    (L("Version","Versiune","Version","Version"),         string.IsNullOrEmpty(mbVer)  ? "—" : mbVer),
                    (L("Serial","Serial","Seriennummer","Numéro de série"),             smbios.BoardSerial),
                    ("", ""),
                    (L("BIOS Vendor","Producător BIOS","BIOS-Hersteller","Fournisseur BIOS"), smbios.BiosVendor),
                    (L("BIOS Version","Versiune BIOS","BIOS-Version","Version du BIOS"),  smbios.BiosVersion),
                    (L("BIOS Date","Dată BIOS","BIOS-Datum","Date du BIOS"),         smbios.BiosDate),
                };
                _heroBoard = new HeroBoard(mbMfg ?? "", mbProd ?? "", smbios.BiosVendor ?? "", smbios.BiosVersion ?? "", smbios.BiosDate ?? "", smbios.BoardSerial ?? "");
                AddCard(_tabPanels["Motherboard"], L("Motherboard","Placă de bază","Hauptplatine","Carte mère"),
                    string.IsNullOrEmpty(mbProd) ? mbMfg ?? "" : $"{mbMfg} {mbProd}", "Motherboard",
                    mbRows, searchTerm: string.IsNullOrEmpty(mbProd) ? "" : mbProd);
            }
            catch (Exception ex) { AppLogger.Debug($"MotherboardCard: {ex.Message}"); }

            try
            {
                var dispRows = new List<(string, string)>();
                var screens = System.Windows.Forms.Screen.AllScreens;

                // "friendly" names + serial, preloaded on a background thread (not on the UI thread).
                var (friendlyByHwId, serialByHwId) = monitorNames;

                int idx = 1;
                foreach (var scr in screens)
                {
                    int hz = 0, bpp = 0;
                    // the resolution and the position in the Windows arrangement: from the display settings (the read that also gives the
                    // frequency); Bounds is the fallback
                    int pixelW = scr.Bounds.Width, pixelH = scr.Bounds.Height, positionX = scr.Bounds.X;
                    try
                    {
                        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };
                        if (EnumDisplaySettings(scr.DeviceName, ENUM_CURRENT_SETTINGS, ref dm))
                        {
                            hz = dm.dmDisplayFrequency;
                            bpp = dm.dmBitsPerPel;
                            if (dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
                            {
                                pixelW = dm.dmPelsWidth; pixelH = dm.dmPelsHeight; positionX = dm.dmPositionX;
                            }
                        }
                    }
                    catch (Exception ex) { AppLogger.Debug($"EnumDisplaySettings: {ex.Message}"); }

                    string friendly = "", serial = "";
                    var physical = new MonitorPhysical(0, 0, -1);
                    try
                    {
                        var monDev = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                        if (EnumDisplayDevices(scr.DeviceName, 0, ref monDev, 0) && !string.IsNullOrEmpty(monDev.DeviceID))
                        {
                            var idSegs = monDev.DeviceID.Split('\\');
                            if (idSegs.Length > 1)
                            {
                                if (friendlyByHwId.TryGetValue(idSegs[1], out var fn)) friendly = fn;
                                if (serialByHwId.TryGetValue(idSegs[1], out var sn))    serial   = sn;
                                if (monitorPhysical.TryGetValue(idSegs[1], out var ph)) physical = ph;
                            }
                        }
                    }
                    catch (Exception ex) { AppLogger.Debug($"EnumDisplayDevices: {ex.Message}"); }

                    // the monitor reports its size in the normal orientation; on a rotated one the sides swap
                    double widthCm = physical.WidthCm, heightCm = physical.HeightCm;
                    if (pixelH > pixelW && widthCm > heightCm) (widthCm, heightCm) = (heightCm, widthCm);
                    double inches = DisplayFacts.DiagonalInches(widthCm, heightCm);
                    string connection = physical.Technology < 0 ? ""
                        : DisplayFacts.IsBuiltIn(physical.Technology) ? L("built-in", "ecran propriu", "eingebaut", "intégré")
                        : DisplayFacts.Connection(physical.Technology);
                    _heroScreens.Add(new HeroScreen(idx, friendly, pixelW, pixelH, hz, scr.Primary, positionX, widthCm, heightCm, physical.Technology));

                    string label = $"{L("Monitor","Monitor","Monitor","Moniteur")} {idx}" + (scr.Primary ? $" ({L("Primary","Principal","Primär","Principal")})" : "");
                    string details = $"{pixelW}×{pixelH}"
                        + (hz  > 0 ? $" @ {hz}Hz"    : "")
                        + (bpp > 0 ? $" · {bpp}-bit" : "");
                    // one row per monitor — the resolution, and under it the model, size, connection + serial (visible, not secret)
                    string monDetail = string.Join("  ·  ", new[]
                    {
                        friendly,
                        DisplayFacts.DiagonalText(inches).Replace('″', '"'),     //// the simple sign: the row also goes into the PDF / clipboard
                        connection,
                        string.IsNullOrWhiteSpace(serial) || serial.Trim('0', ' ').Length == 0 ? "" : $"S/N {serial.Trim()}",
                    }.Where(x => !string.IsNullOrWhiteSpace(x)));
                    dispRows.Add((label, monDetail.Length > 0 ? $"{details}\n{monDetail}" : details));
                    idx++;
                }

                if (!dispRows.Any())
                    dispRows.Add((L("Displays","Afișaje","Bildschirme","Écrans"), "—"));
                AddCard(_tabPanels["Displays"], L("Displays","Afișaje","Bildschirme","Écrans"),
                    $"{screens.Length} " + (screens.Length == 1 ? L("monitor","monitor","Monitor","moniteur") : L("monitors","monitoare","Monitore","moniteurs")),
                    "Monitor", dispRows);
            }
            catch (Exception ex) { AppLogger.Debug($"DisplaysCard: {ex.Message}"); }

            // 13. Audio devices
            try
            {
                // the audio rows are preloaded on a background thread (FetchAudioRows).
                var audioRows = new List<(string, string)>(audioRowsPrefetched);
                _heroAudio = audioRows.Select(a => a.Item2.Split('\n')[0].Trim()).Where(x => x.Length > 0 && x != "—").ToList();
                if (!audioRows.Any()) audioRows.Add(("1.", L("Audio devices","Dispozitive audio","Audiogeräte","Périphériques audio")));

                var combinedRows = new List<(string, string)>(usbRows);
                combinedRows.AddRange(audioRows);

                string periphSubtitle = usbRows.Count > 0
                    ? $"{usbs.Count} " + L("USB","USB","USB","USB") + $" · {audioRows.Count} " + L("audio","audio","Audio","audio")
                    : L("Audio","Audio","Audio","Audio");
                AddCard(_tabPanels["Peripherals"], L("Peripherals","Periferice","Peripheriegeräte","Périphériques"),
                    periphSubtitle, "Usb", combinedRows);
            }
            catch (Exception ex) { AppLogger.Debug($"AudioCard: {ex.Message}"); }

            // on the first load the layout is done separately, so the interface thread is not busy all in one piece
            if (layout) RebuildAllCardsLayout();
        }


        // The data for the summary tiles (filled in BuildAllCards)
        private SystemSummary? _tileSummary;
        private List<RamEntry> _tileRams = new();
        private List<DiskHealthEntry> _tileDisks = new();
        private BatteryInfo? _tileBattery;

        private bool _rebuildingCards = false;

        private void RebuildAllCardsLayout()
        {
            if (_allCardsHost == null) return;
            if (_rebuildingCards) return;
            _rebuildingCards = true;
            try
            {
                foreach (var oldWrapper in _allCardsHost.Children)
                    if (oldWrapper is WpfPanel oldPanel) oldPanel.Children.Clear();
                _allCardsHost.Children.Clear();

                // The component cards (SystemInfoView.Hero.cs, .HeroDevices.cs); their text sections no longer appear in the sheet
                // below (they stay in the PDF and in "Copy all").
                var hero = BuildHeroCards(_allCardsHost, _currentColumns);

                // the spec sheet (SpecSheet.Sheet): the sections left without a card (a card that could not be built), in columns, in
                // the order below
                string[] cellOrder = { "CPU", "Motherboard", "Chassis", "Battery", "Graphics", "Displays", "Memory", "Storage", "Network", "Peripherals" };
                var cells = new List<IList<Border>>();
                var seenLists = new HashSet<List<Border>>();
                foreach (var key in cellOrder)
                    if (!hero.Contains(key) && _tabPanels.TryGetValue(key, out var list) && list.Count > 0 && seenLists.Add(list))
                        cells.Add(new List<Border>(list));
                // any new key, not foreseen in the order above, goes to the end
                foreach (var kv in _tabPanels)
                    if (!hero.Contains(kv.Key) && kv.Value.Count > 0 && seenLists.Add(kv.Value))
                        cells.Add(new List<Border>(kv.Value));
                if (cells.Count == 0) return;

                _allCardsHost.Children.Add(SMDWin.Views.SpecSheet.Sheet(cells.SelectMany(c => c).ToList(), _currentColumns));
            }
            finally
            {
                _rebuildingCards = false;
                _heroVersion++;
                // the page already shown (cards rebuilt after a refresh): the layout is applied in place, without a moment with
                // mismatched rows
                bool fitted = false;
                if (_hardwareReady && IsLoaded)
                {
                    try { UpdateLayout(); FitHeroRows(); fitted = true; }
                    catch (Exception ex) { AppLogger.Debug($"SystemInfo refit: {ex.Message}"); }
                }
                if (!fitted) ScheduleFitHeroRows();
            }
        }

        private bool _fittingRows;

        // the fitting measures all the cards several times: on resize it is done only once, after the window has stopped
        private System.Windows.Threading.DispatcherTimer? _fitTimer;
        private void ScheduleFitHeroRows()
        {
            if (_fitTimer == null)
            {
                _fitTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
                    { Interval = TimeSpan.FromMilliseconds(80) };
                _fitTimer.Tick += (_, _) => { _fitTimer!.Stop(); FitHeroRows(); };
            }
            _fitTimer.Stop();
            _fitTimer.Start();
        }

        // ── Fitting the page on screen ───────────────────────────────────────────────────────────────────────────────
        // The page is NOT enlarged or shrunk (no zoom): the text has the same size as in the rest of the app and stays sharp.
        // Only two things are chosen, from a single measurement:
        //   1. how many columns (1–4) — the layout that fits without scrolling and leaves the least empty space;
        //   2. the row heights — what is left free is shared between the rows, with a limit.
        // If no layout fits, the shortest one is chosen and the page scrolls.
        private List<Border> _heroCards = new();
        private int _heroCols;
        private int _heroVersion;          // grows on every rebuild of the cards
        private string _fitKey = "";       // the last applied fitting (same input data → not recalculated)

        private const double CardMinWidth = 380;      // below it a card's content gets cramped
        private const double CardTargetWidth = 520;   // the width at which a card looks best
        private const double CardMaxWidth = 780;      // above it the card looks "stretched" (the information stays on the left, the rest empty)
        private const double CardWidthFor4 = 430;     // 4 columns only on really wide screens
        private const double RowExtraLimit = 0.08;    // a row grows by at most 8 % above the content height (no empty space in the cards)

        /// <summary>Lays the cards out in rows of <paramref name="cols"/> (the same cards, only moved).</summary>
        private void ArrangeHeroRows(int cols)
        {
            var host = _allCardsHost;
            if (host == null) return;
            foreach (var old in host.Children.OfType<Grid>().Where(g => g.Tag as string == HeroCards.RowTag).ToList())
            {
                old.Children.Clear();
                host.Children.Remove(old);
            }
            var temp = new StackPanel();
            HeroCards.AddRows(temp, _heroCards, cols);
            var rows = temp.Children.Cast<UIElement>().ToList();
            temp.Children.Clear();
            for (int i = 0; i < rows.Count; i++) host.Children.Insert(i, rows[i]);
            _heroCols = cols;
        }

        private sealed class FitPlan
        {
            public int Cols;
            public bool Fits;
            public bool Comfortable;                              // the card width is within the limits in which it looks good
            public double[] RowHeights = Array.Empty<double>();   // the natural heights of the rows
            public double Total;                                  // the height of the whole page
            public double Leftover;                               // pixels left free on screen (0 if it does not fit)
        }

        /// <summary>The natural row heights for <paramref name="cols"/> columns, at the given width (in page units).</summary>
        private double[] MeasureRows(int cols, double hostWidth)
        {
            double colWidth = (hostWidth + 12) / cols;   // each card has a 12 px margin on the right; the row "swallows" the last one
            var heights = new List<double>();
            for (int start = 0; start < _heroCards.Count; start += cols)
            {
                int count = Math.Min(cols, _heroCards.Count - start);
                int[] spans = HeroCards.RowSpans(count, cols);
                double h = 0;
                for (int i = 0; i < count; i++)
                {
                    var card = _heroCards[start + i];
                    card.Measure(new System.Windows.Size(Math.Max(40, colWidth * spans[i]), double.PositiveInfinity));
                    h = Math.Max(h, card.DesiredSize.Height);
                }
                heights.Add(Math.Ceiling(h));
            }
            return heights.ToArray();
        }

        private FitPlan? PlanFor(int cols, double width, double height, Func<double, double> otherHeight)
        {
            double perCard = (width + 12) / cols;
            if (cols > 1 && perCard < CardMinWidth) return null;      // too narrow for so many columns
            if (cols > 3 && perCard < CardWidthFor4) return null;
            var rows = MeasureRows(cols, width);
            double total = rows.Sum() + otherHeight(width);
            return new FitPlan
            {
                Cols = cols, RowHeights = rows, Total = total,
                Fits = total <= height,
                Comfortable = cols == 1 || perCard <= CardMaxWidth,
                Leftover = Math.Max(0, height - total),
            };
        }

        private void FitHeroRows()
        {
            if (_fittingRows || _allCardsHost == null || _mainContentScroll is not ScrollViewer scroll) return;
            if (scroll.Visibility == Visibility.Collapsed || scroll.ActualHeight <= 0 || scroll.ActualWidth <= 0) return;
            if (_heroCards.Count == 0)
            {
                _allCardsHost.LayoutTransform = Transform.Identity;
                scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                return;
            }

            // the same input data as last time → the layout is already applied
            string key = $"{Math.Round(scroll.ActualWidth)}|{Math.Round(scroll.ActualHeight)}|{_heroVersion}|{_heroCards.Count}";
            if (key == _fitKey) return;

            if (UseTableLayout || UseMachineLayout)
            {
                _fittingRows = true;
                try { if (UseTableLayout) FitTableLayout(scroll); else FitMachineLayout(scroll); _fitKey = key; }
                catch (Exception ex) { AppLogger.Warning(ex, "SystemInfoView.FitMachineLayout"); }
                finally { _fittingRows = false; }
                return;
            }

            _fittingRows = true;
            try
            {
                var host = _allCardsHost;
                double width = scroll.ActualWidth;
                // 16 px = the bottom margin of the page; 10 px reserve for the pixel rounding of the real layout
                double height = scroll.ActualHeight - 16 - 10;

                // the rest of the page (the sheet with the sections left without a card, if any)
                var others = host.Children.OfType<FrameworkElement>().Where(c => c.Tag as string != HeroCards.RowTag).ToList();
                double OtherHeight(double hostWidth)
                {
                    double sum = 0;
                    foreach (var o in others) { o.Measure(new System.Windows.Size(hostWidth, double.PositiveInfinity)); sum += o.DesiredSize.Height; }
                    return sum;
                }

                var plans = new List<FitPlan>();
                for (int cols = Math.Min(4, _heroCards.Count); cols >= 1; cols--)
                    if (PlanFor(cols, width, height, OtherHeight) is { } p) plans.Add(p);
                if (plans.Count == 0) return;

                // first the layouts with cards of a suitable width; if there are none, any
                var pool = plans.Any(p => p.Comfortable) ? plans.Where(p => p.Comfortable).ToList() : plans;
                var fitting = pool.Where(p => p.Fits).ToList();
                FitPlan chosen = fitting.Count > 0
                    // fits without scrolling: the one that leaves the least empty space; on a tie, 3 columns, then more.
                    // the closest to the target card width (~520 px); on a tie, the one that leaves less empty space
                    ? fitting.OrderBy(p => Math.Round(Math.Abs((width + 12) / p.Cols - CardTargetWidth) / 40)).ThenBy(p => Math.Round(p.Leftover / 8)).ThenBy(p => Math.Abs(p.Cols - 3)).First()
                    // nothing fits: the shortest page (as little scrolling as possible)
                    : pool.OrderBy(p => p.Total).First();

                if (chosen.Cols != _heroCols) ArrangeHeroRows(chosen.Cols);
                _currentColumns = Math.Min(3, chosen.Cols);

                // no scaling: scaled text looked larger than in the rest of the app and slightly blurry
                host.LayoutTransform = Transform.Identity;
                // no scroll bar when everything fits (otherwise its appearance would narrow the page and spoil the fitting)
                scroll.VerticalScrollBarVisibility = chosen.Fits ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;

                var rows = host.Children.OfType<Grid>().Where(g => g.Tag as string == HeroCards.RowTag).ToList();
                double sumRows = Math.Max(1, chosen.RowHeights.Sum());
                for (int i = 0; i < rows.Count; i++)
                {
                    double natural = i < chosen.RowHeights.Length ? chosen.RowHeights[i] : 0;
                    if (!chosen.Fits || natural <= 0) { rows[i].MinHeight = 0; continue; }
                    // the free space is shared in proportion to the row height (a large row gets more), with a limit
                    double extra = chosen.Leftover * (natural / sumRows);
                    rows[i].MinHeight = Math.Floor(natural + Math.Min(extra, natural * RowExtraLimit));
                }
                // the trial measurements left the cards measured at other widths: all are measured again for the final layout
                foreach (var card in _heroCards) card.InvalidateMeasure();
                foreach (var row in rows) row.InvalidateMeasure();
                foreach (var o in others) o.InvalidateMeasure();
                host.InvalidateMeasure();
                _fitKey = key;
                AppLogger.Debug($"SystemInfo fit: {Math.Round(width)}x{Math.Round(height)} → {chosen.Cols} col, content {chosen.Total:0}px, fits={chosen.Fits}, leftover {chosen.Leftover:0}px");
            }
            catch (Exception ex) { AppLogger.Warning(ex, "SystemInfoView.FitHeroRows"); }
            finally { _fittingRows = false; }
        }

        private Border AddCard(List<Border> target, string title, string subtitle, string iconKey,
            List<(string Label, string Value)> rows, string searchTerm = "")
        {
            _cardData.Add((title, subtitle, rows));
            _cardIconKeys.Add(iconKey);
            // The component label (small, coloured) + its NAME (large).
            var card = SMDWin.Views.SpecSheet.Section(iconKey, title,
                string.IsNullOrWhiteSpace(subtitle) ? title : subtitle, rows,
                new SMDWin.Views.SpecSheet.SectionOptions
                {
                    OnCopy   = () => CopyCard(title, rows),
                    CopyTip  = L("Copy", "Copiază", "Kopieren", "Copier"),
                    OnSearch = string.IsNullOrWhiteSpace(searchTerm) ? null : () => OpenGoogleSearch(searchTerm),
                    SearchTip = string.IsNullOrWhiteSpace(searchTerm) ? null
                        : L($"Search Google: {searchTerm}", $"Caută pe Google: {searchTerm}", $"Bei Google suchen: {searchTerm}", $"Rechercher sur Google : {searchTerm}"),
                    ShowTip  = L("Show", "Arată", "Anzeigen", "Afficher"),
                    HideTip  = L("Hide", "Ascunde", "Ausblenden", "Masquer"),
                    MoreFmt  = L("+ {0} more", "+ încă {0}", "+ {0} weitere", "+ {0} de plus"),
                    LessText = L("Show less", "Arată mai puțin", "Weniger anzeigen", "Afficher moins"),
                    ValueColor = SectionValueColor,
                    Flat     = true,
                });
            target.Add(card);
            return card;
        }

        /// <summary>Status colours for a few values (Health, Status) — the rest stay neutral.</summary>
        private static WpfColor? SectionValueColor(string label, string value)
        {
            string l = label.ToLowerInvariant();
            if (l is "health" or "sănătate" or "zustand" or "état" && value.EndsWith("%")
                && int.TryParse(value.TrimEnd('%').Trim(), out int pct))
                return pct >= 90 ? SMDWin.Views.SpecSheet.Green : pct >= 70 ? SMDWin.Views.SpecSheet.Amber : SMDWin.Views.SpecSheet.Red;
            if (l is "status" && value.Equals("OK", StringComparison.OrdinalIgnoreCase))
                return SMDWin.Views.SpecSheet.Green;
            return null;
        }

        private void OpenGoogleSearch(string searchTerm)
        {
            try { Process.Start(new ProcessStartInfo(
                $"https://www.google.com/search?q={Uri.EscapeDataString(searchTerm)}")
                { UseShellExecute = true }); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
        }

        // ── USB / Bluetooth via WMI ───────────────────────────────────────────
        private static (List<string> Usbs, List<string> Bluetooths) FetchUsbAndBluetooth()
        {
            var usbs = new List<string>();
            var bts  = new List<string>();

            // USB devices
            try
            {
                // a single query instead of one ManagementObject per device (the identifier with '\\' gave "Not found" for each one).
                using var s = SMDWin.Services.WmiHelper.Searcher(
                    "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\%'", System.TimeSpan.FromSeconds(10));
                using (var __wmiTmp4 = s.Get())
                {
                    foreach (ManagementObject obj in __wmiTmp4)
                    {
                        try
                        {
                            string name = obj["Name"]?.ToString() ?? "";
                            if (!string.IsNullOrWhiteSpace(name)
                                && !name.Contains("Root Hub", StringComparison.OrdinalIgnoreCase)
                                && !name.Contains("USB Host", StringComparison.OrdinalIgnoreCase)
                                && !name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)   // has its own row in the Network card
                                && !usbs.Contains(name))
                                usbs.Add(name);
                        }
                        catch (Exception ex) { AppLogger.Debug(ex.Message); }
                    }
                }
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }

            // Bluetooth - searched through several methods
            // Metoda 1: Win32_PnPEntity WHERE Name LIKE '%Bluetooth%'
            try
            {
                using var bt = SMDWin.Services.WmiHelper.Searcher(
                    "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%Bluetooth%'", System.TimeSpan.FromSeconds(10));
                using (var __wmiTmp5 = bt.Get())
                {
                    foreach (ManagementObject obj in __wmiTmp5)
                {
                    string name = obj["Name"]?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(name) && !bts.Contains(name))
                        bts.Add(name);
                }
                }
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }

            // Method 2: Win32_PnPEntity with the Bluetooth class (for iMac/Boot Camp)
            if (bts.Count == 0)
            {
                try
                {
                    using var bt2 = SMDWin.Services.WmiHelper.Searcher(
                        "SELECT Name FROM Win32_PnPEntity WHERE PNPClass = 'Bluetooth'", System.TimeSpan.FromSeconds(10));
                    using (var __wmiTmp6 = bt2.Get())
                    {
                        foreach (ManagementObject obj in __wmiTmp6)
                    {
                        string name = obj["Name"]?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(name) && !bts.Contains(name))
                            bts.Add(name);
                    }
                    }
                }
                catch (Exception ex) { AppLogger.Debug(ex.Message); }
            }

            // Method 3: look for Broadcom/Apple BT through USB
            if (bts.Count == 0)
            {
                foreach (var usb in usbs)
                    if (usb.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
                     || usb.Contains("Broadcom", StringComparison.OrdinalIgnoreCase)
                     || (usb.Contains("Apple", StringComparison.OrdinalIgnoreCase) && usb.Contains("Wire", StringComparison.OrdinalIgnoreCase)))
                        if (!bts.Contains(usb)) bts.Add(usb);
            }

            return (usbs.Take(30).ToList(), bts.Take(10).ToList());
        }

        private void CopyCard(string title, List<(string Label, string Value)> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== {title} ===");
            foreach (var (l, v) in rows)
                if (!string.IsNullOrWhiteSpace(l) || !string.IsNullOrWhiteSpace(v))
                    sb.AppendLine($"{l,-26}{v}");
            sb.AppendLine();
            try { WpfClipboard.SetText(sb.ToString()); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
        }

        private void CopyAll()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"SMD Win — {L("System Information", "Informații Sistem", "Systeminformationen", "Informations système")}");
            sb.AppendLine(new string('=', 60));
            sb.AppendLine($"{L("Generated at", "Generat la", "Erstellt am", "Généré le")}: {SMDWin.Services.DateFormatHelper.FormatDateTimeSec(DateTime.Now)}");
            sb.AppendLine();
            foreach (var (title, subtitle, rows) in _cardData)
            {
                sb.AppendLine($"--- {title}{(string.IsNullOrWhiteSpace(subtitle) ? "" : $" — {subtitle}")} ---");
                foreach (var (lbl, val) in rows)
                    if (!string.IsNullOrWhiteSpace(lbl) || !string.IsNullOrWhiteSpace(val))
                        sb.AppendLine($"  {lbl.TrimStart(),-26}{val}");
                sb.AppendLine();
            }
            try { WpfClipboard.SetText(sb.ToString()); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
        }
    }
}
