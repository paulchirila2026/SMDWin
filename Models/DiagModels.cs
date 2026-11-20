using System;
using System.Collections.Generic;
using SMDWin.Services;

namespace SMDWin.Models
{
    public class EventLogEntry
    {
        public DateTime TimeCreated { get; set; }
        public string Level { get; set; } = "";
        public string Source { get; set; } = "";
        public long EventId { get; set; }
        public string Message { get; set; } = "";
        public string MessageSingleLine => string.IsNullOrEmpty(Message)
            ? Message
            : System.Text.RegularExpressions.Regex.Replace(Message.Replace('\r', ' ').Replace('\n', ' '), @"\s{2,}", " ").Trim();
        public string LogName { get; set; } = "";
        /// <summary>The level as shown in the list, in the interface language (Level stays in English for filters and exports).</summary>
        public string LevelDisplay => SMDWin.Services.LiteralTexts.Translate(Level);
        public string LevelColor => Level switch { "Critical" => "#EF4444", "Error" => "#F97316", "Warning" => "#F59E0B", _ => "#94A3B8" };
        /// <summary>Background of the level "pill" — the same colour at ~15% opacity.</summary>
        public string LevelBg => Level switch { "Critical" => "#26EF4444", "Error" => "#26F97316", "Warning" => "#26F59E0B", _ => "#1A94A3B8" };

        // ── Event Viewer ──
        /// <summary>How many times the same event (source + ID + level) appeared in the range.</summary>
        public int Count { get; set; } = 1;
        public DateTime FirstSeen { get; set; }
        public string CountDisplay => Count > 1 ? $"×{Count}" : "";
        /// <summary>Plain-language explanation (EventExplainer), empty if the event is not known.</summary>
        public string Explanation { get; set; } = "";
        public bool IsHarmless { get; set; }
        /// <summary>From the event database (Data/EventDB): short title, steps, severity (routine / watch / serious), SMD Win page.</summary>
        public string KbTitle { get; set; } = "";
        public List<string> KbSteps { get; set; } = new();
        public string KbSeverity { get; set; } = "";
        public string KbPage { get; set; } = "";
        /// <summary>The table row: the short title if the event is known, otherwise the first sentence of the message.</summary>
        public string Headline => EventText.Headline(KbTitle, Message, Source, EventId);
        /// <summary>The second, small line: where the event comes from (technical source, ID, log).</summary>
        public string SourceLine => $"{Source} · ID {EventId} · {LogName}";
        public string LevelBrushKey => Level switch { "Critical" or "Error" => "StatusCritBrush", "Warning" => "StatusWarningBrush", _ => "TextSecondaryBrush" };
        /// <summary>What the Message column shows: the explanation if there is one, otherwise the raw message.</summary>
        public string Summary => string.IsNullOrEmpty(Explanation) ? MessageSingleLine : Explanation;
    }

    public class CrashEntry
    {
        public DateTime CrashTime { get; set; }
        public string FileName { get; set; } = "";
        public string StopCode { get; set; } = "";
        public string FaultingModule { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string BugcheckParams { get; set; } = "";
        public int ModuleCount { get; set; }
        public string FaultingModuleCategory { get; set; } = "";
        /// <summary>Area of the suspected driver, language-independent (the name from MiniDumpParser.DriverKind); empty = unknown.</summary>
        public string FaultingModuleKind { get; set; } = "";
        public SMDWin.Services.KernelDumpInfo? KernelInfo { get; set; }

        // Interpretation: the probable area + a short explanation for the table
        public string AdviceArea    { get; set; } = "";
        public string AdviceSummary { get; set; } = "";
    }

    public class DriverEntry
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Version { get; set; } = "";
        public string Provider { get; set; } = "";
        public string Status { get; set; } = "";
        public string Date { get; set; } = "";
        public DateTime? DateRaw { get; set; }
        public bool IsSigned { get; set; }
        public string StatusColor => Status == "Running" ? "#22C55E" : "#94A3B8";
        public string SignedText  => IsSigned
            ? (SMDWin.Services.LanguageService.S("Driver", "Signed",   "✔ Signed"))
            : (SMDWin.Services.LanguageService.S("Driver", "Unsigned", "✘ Unsigned"));
        public string SignedColor => IsSigned ? "#22C55E" : "#EF4444";
        public DriverEntry Clone() => (DriverEntry)MemberwiseClone();
    }

    public class DiskHealthEntry
    {
        public string DeviceId { get; set; } = "";
        public string Model { get; set; } = "";
        public string SerialNumber { get; set; } = "";
        public string Size { get; set; } = "";
        public ulong SizeBytes { get; set; }
        public string MediaType { get; set; } = "";
        public string Status { get; set; } = "";
        public int HealthPercent { get; set; }
        public string HealthColor => HealthPercent >= 80 ? "#22C55E" : HealthPercent >= 50 ? "#F59E0B" : "#EF4444";
        public List<PartitionEntry> Partitions { get; set; } = new();
        public List<SmartAttributeEntry> SmartAttributes { get; set; } = new();
        public List<NvmeHealthRow> NvmeHealthRows { get; set; } = new();
        public string PhysicalDevicePath { get; set; } = "";
        /// <summary>From smartctl: the firmware and, for SATA drives, the link the drive supports / negotiated.</summary>
        public string Firmware { get; set; } = "";
        public string SataLinkMax { get; set; } = "";
        public string SataLinkCurrent { get; set; } = "";
        public int SataLinkMaxTenths { get; set; }
        public int SataLinkCurrentTenths { get; set; }
        public int DiskIndex { get; set; } = 0;
        /// <summary>The exact disk type (from MSFT_PhysicalDisk: BusType + MediaType + SpindleSpeed, then the name):
        /// "NVMe SSD", "SATA SSD", "HDD", "USB stick", "External HDD", "External SSD", "External drive", "SD card",
        /// "Virtual disk", "SSD", "Unknown". Used for the icon and label on the Storage page.</summary>
        public string Kind { get; set; } = "Unknown";
        /// <summary>Bus: "NVMe", "SATA", "USB", "SAS", "RAID", "SD", "ATA", "SCSI", "Virtual" or "".</summary>
        public string Bus { get; set; } = "";
        /// <summary>Rotation speed (HDD), 0 = no platters / unknown.</summary>
        public int RotationRpm { get; set; }
        /// <summary>Physical form: "M.2", "2.5\"", "3.5\"", "mSATA"… — from the disk itself (smartctl), else from the model name
        /// (M.2 SATA series, 2.5" laptop HDDs); NVMe = "M.2" unless the disk says otherwise. "" when nothing is sure.</summary>
        public string FormFactor { get; set; } = "";
        /// <summary>USB disks: what the device supports ("USB 3.0 · 5 Gb/s") and how it is connected now; "" when unknown.</summary>
        public string UsbSupported { get; set; } = "";
        public string UsbConnected { get; set; } = "";
        /// <summary>A USB 3 device running at a lower speed (USB 2 port, cable or old hub).</summary>
        public bool UsbSlowLink { get; set; }

        /// <summary>
        /// The disk type in one line, as people say it: "M.2 NVMe SSD", "M.2 SATA SSD", "2.5\" SATA SSD",
        /// "3.5\" HDD · 7200 rpm", "External HDD · 2.5\"", "USB stick"… The USB link is in <see cref="UsbLinkText"/>.
        /// </summary>
        public string TypeText
        {
            get
            {
                string ff = FormFactor;
                string rpm = RotationRpm > 0 ? $" · {RotationRpm} rpm" : "";
                return Kind switch
                {
                    "NVMe SSD"       => $"{(ff.Length > 0 ? ff : "M.2")} NVMe SSD",
                    "SATA SSD"       => ff.Length > 0 ? $"{ff} SATA SSD" : "SATA SSD",
                    "SSD"            => ff.Length > 0 ? $"{ff} SSD" : (Bus.Length > 0 ? $"{Bus} SSD" : "SSD"),
                    "HDD"            => (ff.Length > 0 ? $"{ff} HDD" : "HDD") + rpm,
                    "External HDD"   => "External HDD" + (ff.Length > 0 ? $" · {ff}" : "") + rpm,
                    "External SSD"   => "External SSD" + (ff.Length > 0 ? $" · {ff}" : ""),
                    "External drive" => "External drive",
                    "USB stick"      => "USB stick",
                    "SD card"        => "SD card",
                    "Virtual disk"   => "Virtual disk",
                    _                => string.IsNullOrWhiteSpace(MediaType) || MediaType == "Unknown" ? "Disk" : MediaType,
                };
            }
        }

        /// <summary>"USB 3.0 · 5 Gb/s", or "USB 3.0 device · connected as USB 2.0" when the link is slower; "" for internal disks.</summary>
        public string UsbLinkText =>
            UsbConnected.Length == 0 ? "" :
            UsbSlowLink && UsbSupported.Length > 0 ? $"{UsbSupported.Split(" · ")[0]} device · connected as {UsbConnected.Split(" · ")[0]}" :
            UsbConnected;
    }

    public class SmartAttributeEntry
    {
        public byte   Id           { get; set; }
        public string IdHex        => $"0x{Id:X2}";
        public string Name         { get; set; } = "";
        public byte   CurrentValue { get; set; }
        public byte   WorstValue   { get; set; }
        public long   RawValue     { get; set; }
        public string RawDisplay => (Id == 0xC2 || Id == 0xBE) ? TempUnit.Fmt(RawValue & 0xFF) : RawValue.ToString("N0");
        public byte   ThresholdValue { get; set; }
        public string SmartctlName { get; set; } = "";
        public int    SectorBytes  { get; set; } = 512;
        /// <summary>true for an NVMe SSD whose written / read data (0xF1 / 0xF2) come in NVMe data units (512,000 bytes), not
        /// sectors — e.g. NVMe seen through a RAID controller.</summary>
        public bool   NvmeDataUnits { get; set; }
        public bool   IsCritical   { get; set; }
        public string Description  { get; set; } = "";
        public string StatusText  => TrafficLightColor != "#22C55E"
            ? LanguageService.S("Smart", "Warning", "WARNING")
            : LanguageService.S("Smart", "Ok", "OK");
        public string StatusColor => TrafficLightColor;

        // ── Traffic light: green / yellow / red ───────────────────────────
        // Green  = attribute within normal parameters, no problem
        // Yellow = attention — value close to the threshold OR a non-zero attribute that may be harmless
        //          (e.g. Power Cycles) but is worth watching
        // Red    = confirmed problem — non-zero ReallocatedSectors, Pending, Uncorrectable
        //          OR CurrentValue ≤ ThresholdValue (the disk will fail soon)
        public string TrafficLightColor
        {
            get
            {
                // If Current ≤ Threshold (and the threshold is a valid > 0) = pre-fail
                if (ThresholdValue > 0 && CurrentValue <= ThresholdValue)
                    return "#EF4444"; // red

                // Critical attributes — any raw value > 0 is red
                if (Id is 0x05 or 0xC5 or 0xC6 or 0xBB)
                    return RawValue > 0 ? "#EF4444" : "#22C55E";

                // Command timeouts: a few over the years are normal (a loose cable, a sudden power cut); yellow only above 5, or
                // when the drive itself lowers the value (CrystalDiskInfo shows these as fine too)
                if (Id is 0xBC)
                    return RawValue > 5 || (CurrentValue is > 0 and < 100) ? "#F59E0B" : "#22C55E";

                // Temperatura
                if (Id is 0xC2 or 0xBE)
                {
                    long t = RawValue & 0xFF; // LSB byte = °C on most drives
                    return t >= 60 ? "#EF4444" : t >= 50 ? "#F59E0B" : "#22C55E";
                }

                if (Id is 0xA9 or 0xE7 or 0xE9 or 0xD1 or 0xAA)
                {
                    if (ThresholdValue == 0) return "#22C55E"; // no real threshold — no alarm
                    return CurrentValue <= 10 ? "#EF4444" : CurrentValue <= 40 ? "#F59E0B" : "#22C55E";
                }

                // Reallocation Events — non-zero = yellow/red
                if (Id is 0xC4)
                    return RawValue >= 100 ? "#EF4444" : RawValue > 0 ? "#F59E0B" : "#22C55E";

                // CRC/Transfer errors
                if (Id is 0xC7 or 0xB7 or 0xB8)
                    return RawValue >= 10 ? "#EF4444" : RawValue > 0 ? "#F59E0B" : "#22C55E";

                // Default — OK if IsCritical=false and raw=0
                return IsCritical && RawValue > 0 ? "#EF4444" : "#22C55E";
            }
        }

        /// <summary>Friendly explanation for an ordinary user, shown in the "What it means" column.</summary>
        public string FriendlyDescription => Id switch
        {
            0x01 => RawValue == 0 ? "No read errors — OK" : $"Raw counter {RawValue:N0} — judge by the normalized value vs. threshold (large raw values are normal on many drives)",
            0x05 => RawValue == 0 ? "No bad sectors — OK" : $"⚠ {RawValue} bad sector(s) remapped — disk may be failing",
            0x09 => $"{RawValue:N0} hours powered on ({RawValue/24:N0} days)",
            0x0C => $"{RawValue:N0} power cycles (on/off)",
            0xBB => RawValue == 0 ? "No uncorrectable errors — OK" : $"⚠ {RawValue} uncorrectable error(s) — data loss risk",
            0xBC => RawValue == 0 ? "No command timeouts — OK" : $"{RawValue} timeout(s) — possible cable/controller issue",
            0xBE or 0xC2 => $"{TempUnit.Fmt(RawValue & 0xFF)} — {(( RawValue & 0xFF) < 45 ? "normal" : (RawValue & 0xFF) < 55 ? "warm" : "⚠ hot")}",
            0xC4 => RawValue == 0 ? "No reallocation events — OK" : $"{RawValue} reallocation attempt(s)",
            0xC5 => RawValue == 0 ? "No unstable sectors — OK" : $"⚠ {RawValue} unstable sector(s) pending — backup data now",
            0xC6 => RawValue == 0 ? "No unreadable sectors — OK" : $"⚠ {RawValue} unreadable sector(s) — data loss risk",
            0xC7 => RawValue == 0 ? "No transfer errors — OK" : $"{RawValue} CRC error(s) — check cable/port",
            // 0xA9 / 0xE9 do not mean "life left" for every manufacturer (on Kingston/Phison 0xA9 = bad blocks, 0xE9 = data written
            // to NAND). The percentage is shown only when the attribute name says life / wear; otherwise the raw value, without an
            // invented "100 %".
            0xE7 or 0xA9 or 0xE9 when !NameSaysLife => RawValue == 0 ? "0 — OK" : $"Maker-specific counter: {RawValue:N0} (normalized {CurrentValue}/100, within limits)",
            0xE7 or 0xA9 or 0xE9 => ThresholdValue == 0
                ? $"{CurrentValue}% life remaining (reported by the drive)"
                : $"{CurrentValue}% life remaining — {(CurrentValue > 50 ? "good" : CurrentValue > 20 ? "degraded" : "⚠ near end of life")}",
            0xAA or 0xE8 => $"Spare blocks: normalized {CurrentValue}/100{(ThresholdValue > 0 ? $" (limit {ThresholdValue})" : "")} — {(ThresholdValue > 0 && CurrentValue <= ThresholdValue ? "⚠ reserve used up" : "OK")}",
            0xAB or 0xB5 => RawValue == 0 ? "No write (program) failures — OK" : $"{RawValue:N0} write failure(s) — the drive replaced the blocks",
            0xAC or 0xB6 => RawValue == 0 ? "No erase failures — OK" : $"{RawValue:N0} erase failure(s) — the drive replaced the blocks",
            0xAE or 0xC0 => RawValue == 0 ? "No unsafe shutdowns" : $"{RawValue:N0} unsafe shutdown(s) — power lost while running (not an error by itself)",
            0xB1 => $"Wear spread between blocks: normalized {CurrentValue}/100",
            0xB3 or 0xB4 => $"Unused reserve blocks: normalized {CurrentValue}/100 — {(CurrentValue > 20 ? "OK" : "⚠ low")}",
            0xB7 => RawValue == 0 ? "No SATA speed drops — OK" : $"{RawValue:N0} SATA speed drop(s) — check cable/port",
            0xB8 => RawValue == 0 ? "No internal data errors — OK" : $"⚠ {RawValue:N0} internal data error(s)",
            0xC1 => $"{RawValue:N0} head park cycles",
            0xC3 => $"Errors corrected by the drive: {RawValue:N0} (normal on many drives)",
            0x03 => $"Spin-up time: normalized {CurrentValue}/100",
            0x04 => $"{RawValue:N0} start/stop cycles",
            0x07 => $"Seek errors: normalized {CurrentValue}/100 (raw values are vendor-encoded)",
            0x0A => RawValue == 0 ? "No spin-up retries — OK" : $"⚠ {RawValue:N0} spin-up retries — motor problem",
            0xF0 => $"{RawValue:N0} head flying hours",
            0xF9 => $"{RawValue:N0} GB written to the flash memory (lifetime)",
            0xAD => $"Wear leveling normalized: {CurrentValue}/100 — {(CurrentValue > 50 ? "good" : CurrentValue > 20 ? "degraded" : "⚠ near end of life")}",
            0xF1 => $"{LifetimeData.Format(SsdLifeDetector.AttributeToBytes(this))} written (lifetime)",
            0xF2 => $"{LifetimeData.Format(SsdLifeDetector.AttributeToBytes(this))} read (lifetime)",
            _ => Description.Length > 0 ? Description
               : RawValue == 0 ? "0 — OK"
               : $"Maker-specific counter: {RawValue:N0} (normalized {CurrentValue}/100, within limits)"
        };

        /// <summary>The attribute name (from the database / smartctl) says life left or wear.</summary>
        private bool NameSaysLife => (Name ?? "").IndexOf("life", StringComparison.OrdinalIgnoreCase) >= 0
            || (Name ?? "").IndexOf("wearout", StringComparison.OrdinalIgnoreCase) >= 0
            || (Name ?? "").IndexOf("remaining", StringComparison.OrdinalIgnoreCase) >= 0
            || (Name ?? "").IndexOf("percent", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>An attribute with no standard meaning (only the manufacturer's number) whose value is 0 and within limits:
        /// it tells the user nothing, so the SMART table hides it until "Show all".</summary>
        public bool IsRoutine => Description.Length == 0 && RawValue == 0
            && !IsCritical && !(ThresholdValue > 0 && CurrentValue <= ThresholdValue)
            && Id is not (0x01 or 0x05 or 0x09 or 0x0C or 0xBB or 0xBC or 0xBE or 0xC2 or 0xC4 or 0xC5 or 0xC6 or 0xC7 or 0xE7 or 0xA9 or 0xE9 or 0xAD or 0xF1 or 0xF2);

        /// <summary>Light row tint for critical attributes (semi-transparent red/yellow background).</summary>
        public string RowBackground
        {
            get
            {
                string tl = TrafficLightColor;
                return tl == "#EF4444" ? "#10EF4444" : tl == "#F59E0B" ? "#10F59E0B" : "Transparent";
            }
        }

        // Tooltip descriptions for each known SMART attribute ID
        public string AttributeTooltip
        {
            get
            {
                var desc = Id switch
                {
                    0x01 => "Read Error Rate — Rate of hardware read errors. High raw value = possible disk failure.",
                    0x02 => "Throughput Performance — Overall drive throughput. Low = degraded performance.",
                    0x03 => "Spin-Up Time — Time needed to spin disk platters to full speed (HDDs only).",
                    0x04 => "Start/Stop Count — Number of times the disk has been started and stopped.",
                    0x05 => "Reallocated Sectors Count — Bad sectors remapped to spare area. ANY non-zero value is serious.",
                    0x07 => "Seek Error Rate — Rate of seek errors. High = mechanical issues (HDD).",
                    0x08 => "Seek Time Performance — Average seek performance. Low = slowdowns.",
                    0x09 => "Power-On Hours — Total hours the drive has been powered on.",
                    0x0A => "Spin Retry Count — Retries needed to spin up to speed. High = motor problems.",
                    0x0B => "Recalibration Retries — Times the drive had to recalibrate. High = mechanical wear.",
                    0x0C => "Power Cycle Count — Number of full power cycles (off → on).",
                    0xB7 => "SATA Downshift Error Count — SATA interface speed downgrades. High = cable/port issues.",
                    0xB8 => "End-to-End Error Count — Data errors between cache and host. High = possible RAM or controller issue.",
                    0xBB => "Uncorrectable Error Count — Errors that could NOT be corrected. Non-zero = concern.",
                    0xBC => "Command Timeout Count — Commands that timed out. High = connectivity or drive issue.",
                    0xBD => "High Fly Writes — Write head too far from surface. High = physical damage risk.",
                    0xBE => "Airflow Temperature — Drive temperature measured at airflow sensor.",
                    0xBF => "G-Sense Error Rate — Errors caused by external shock/vibration.",
                    0xC0 => "Power-Off Retract Count — Emergency head parking events (unsafe shutdowns).",
                    0xC1 => "Load/Unload Cycle Count — Times the head was parked/unparked.",
                    0xC2 => "Temperature — Current drive temperature in Celsius. Keep below 55°C.",
                    0xC3 => "Hardware ECC Recovered — ECC error correction activations. High = reliability concern.",
                    0xC4 => "Reallocation Event Count — Total reallocation attempts (successful or not).",
                    0xC5 => "Current Pending Sector Count — Unstable sectors waiting to be remapped. Non-zero = bad sign.",
                    0xC6 => "Uncorrectable Sector Count — Sectors that cannot be read. Non-zero = data loss risk.",
                    0xC7 => "UltraDMA CRC Error Count — Data transfer errors on the interface cable.",
                    0xC8 => "Write Error Rate — Rate of write errors. High = failing write head.",
                    0xCA => "Data Address Mark Errors — Positioning errors during read/write.",
                    0xCE => "Flying Height — Distance between read head and disk platter.",
                    0xF0 => "Head Flying Hours — Total hours the read/write head has been flying.",
                    0xF1 => "Total LBAs Written — Total data written (lifetime). Relevant for SSD wear.",
                    0xF2 => "Total LBAs Read — Total data read (lifetime).",
                    0xFE => "Free Fall Protection — Freefall events detected by accelerometer.",
                    _    => $"Vendor-specific attribute (0x{Id:X2}) — no universal standard meaning; varies by manufacturer/controller. Raw value: {RawValue}"
                };
                return $"[0x{Id:X2}] {Name}\n{desc}\n\nCurrent: {CurrentValue}  Worst: {WorstValue}  Raw: {RawValue}";
            }
        }
    }

    public class SurfaceScanProgress
    {
        public double PercentComplete  { get; set; }
        public long   BadSectors       { get; set; }
        public long   SlowSectors      { get; set; }
        public long   CurrentLBA       { get; set; }
        public double SpeedMBps        { get; set; }
        public double EtaSeconds       { get; set; }
        public System.Windows.Media.Color BlockColor { get; set; }
    }

    public class PartitionEntry
    {
        public string Letter     { get; set; } = "";
        public string Label      { get; set; } = "";
        public string FileSystem { get; set; } = "";
        public double TotalGB    { get; set; }
        public double FreeGB     { get; set; }
        public double UsedGB     => Math.Max(0, TotalGB - FreeGB);
        public int    UsedPct    => TotalGB > 0 ? (int)(Math.Max(0, Math.Min(100, UsedGB / TotalGB * 100))) : 0;
        public string FreeColor  => TotalGB > 0 && (UsedGB / TotalGB) >= 0.90 ? "#EF4444"
                                   : TotalGB > 0 && (UsedGB / TotalGB) >= 0.75 ? "#F59E0B"
                                   : "#22C55E";
        public string DisplayUsed => $"{UsedGB:F1} GB / {TotalGB:F1} GB";

        // Partition metadata
        public bool   IsActive       { get; set; }  // active (bootable) partition
        public bool   IsSystem       { get; set; }  // Windows system partition
        public string PartitionStyle { get; set; } = ""; // "GPT", "MBR", ""
        public int    PartitionNumber { get; set; }
    }

    public class RamEntry
    {
        public string BankLabel    { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string PartNumber   { get; set; } = "";
        public string Capacity     { get; set; } = "";
        public ulong  CapacityBytes { get; set; }
        public string Speed        { get; set; } = "";
        // Number only (the unit is in the "MHz" column header) — "1600 MHz" did not fit in the column
        public string SpeedShort   => RatedMHz > 0 ? RatedMHz.ToString()
                                    : ConfiguredMHz > 0 ? ConfiguredMHz.ToString()
                                    : string.IsNullOrEmpty(Speed) ? "—" : Speed.Replace("MHz", "").Trim();
        public string MemoryType   { get; set; } = "";
        public string FormFactor   { get; set; } = "";
        public bool   IsEmpty      { get; set; } = false;

        // Extended fields — populated from WMI Win32_PhysicalMemory
        public int    ConfiguredMHz   { get; set; }   // current configured speed
        public int    RatedMHz        { get; set; }   // viteza max SPD
        public double VoltageV        { get; set; }   // configured voltage (mV → V)
        public double MinVoltageV     { get; set; }   // minimum SPD voltage
        public double MaxVoltageV     { get; set; }   // maximum SPD voltage
        public int    DataWidthBits   { get; set; }   // data width (64 bit)
        public int    TotalWidthBits  { get; set; }   // total width (72 bit = ECC)
        public bool   IsEcc           { get; set; }   // TotalWidth > DataWidth → ECC
        public string SlotLabel       { get; set; } = ""; // ex: "DIMM_A1"
        public int    SlotIndex       { get; set; }   // index numeric 0-based

        public string IsEccDisplay      => IsEmpty ? "" : (IsEcc ? "Yes" : "No");
        public string VoltageDisplay    => IsEmpty || VoltageV <= 0 ? "—" : $"{VoltageV:F2} V";
    }

    public class TemperatureEntry
    {
        public string Name { get; set; } = "";
        public double Temperature { get; set; }
        public string TempColor => Temperature >= 85 ? "#EF4444" : Temperature >= 70 ? "#F59E0B" : "#22C55E";
        public string Display => Temperature < 0 ? "N/A" : $"{TempUnit.Fmt(Temperature, 1)}";

        // 4.2 — Context label: "76°C  ⚠ High (threshold: 70°C)"
        public string ContextLabel
        {
            get
            {
                if (Temperature < 0) return "N/A";
                if (Temperature >= 85) return $"{TempUnit.Fmt(Temperature, 1)}  ✗ {LanguageService.S("Temp", "Critical", "Critical")} ({LanguageService.S("Temp", "Threshold", "threshold")}: 85°C)";
                if (Temperature >= 70) return $"{TempUnit.Fmt(Temperature, 1)}  ⚠ {LanguageService.S("Temp", "High", "High")} ({LanguageService.S("Temp", "Threshold", "threshold")}: 70°C)";
                return $"{TempUnit.Fmt(Temperature, 1)}  ✔ {LanguageService.S("Temp", "Normal", "Normal")}";
            }
        }

        public string StatusIcon  => Temperature >= 85 ? "✗" : Temperature >= 70 ? "⚠" : "✔";
        public string StatusLabel => Temperature >= 85
            ? LanguageService.S("Temp", "Critical", "Critical")
            : Temperature >= 70
                ? LanguageService.S("Temp", "High", "High")
                : LanguageService.S("Temp", "Normal", "Normal");
        public int    ThresholdValue => Temperature >= 85 ? 85 : 70;
        public double MaxToday { get; set; }   // filled by monitor tick
    }

    public class SystemSummary
    {
        public string OsName { get; set; } = "";
        public string OsVersion { get; set; } = "";
        public string OsBuild { get; set; } = "";
        public string OsBuildNumber { get; set; } = "";
        public string OsArch { get; set; } = "";
        public string ComputerName { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string Model { get; set; } = "";
        public string Cpu { get; set; } = "";
        public string CpuCores { get; set; } = "";
        public int    CpuThreads { get; set; }
        public string CpuCache { get; set; } = ""; // "L2: 512 KB  L3: 8 MB"
        public string CpuMaxMHz { get; set; } = ""; // "3600 MHz"
        public string TotalRam { get; set; } = "";
        public float  RamTotalGB { get; set; }
        public string RamType { get; set; } = "";
        public string Uptime { get; set; } = "";
        public string UptimeString { get; set; } = "";
        public int    UptimeDays { get; set; }
        public string GpuName { get; set; } = "";
        public string GpuVram { get; set; } = "";
        public float  GpuVramGB { get; set; }
        public string GpuDriverVersion { get; set; } = "";
        public string InstallDate { get; set; } = "";
        public string Architecture { get; set; } = "";
        public string BiosVersion { get; set; } = "";
        public int CriticalEvents { get; set; }
        public int Warnings { get; set; }
        public int CrashCount { get; set; }
        public string BatteryStatus { get; set; } = "N/A";
        public string BatteryCharge { get; set; } = "";
        public bool   HasBattery { get; set; }
        public int    BatteryChargeInt { get; set; }
        public float  BatteryWearPct { get; set; }
        public string BatteryMfr { get; set; } = "";
        public int    BatteryCycles { get; set; }
        public string BatteryRuntime { get; set; } = "";
        // Display info
        public string DisplayResolution { get; set; } = "";   // "1920 × 1080, 60 Hz"
        public string DisplayName { get; set; } = "";         // "Generic PnP Monitor"
        public int    DisplayCount { get; set; }
    }

    public class InstalledApp : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Publisher { get; set; } = "";
        public string InstallDate { get; set; } = "";
        public string UninstallKey { get; set; } = "";
        public string Size { get; set; } = "";
        public long SizeBytes { get; set; }
        public DateTime? InstallDateRaw { get; set; }

        // ── Applications: iconita, instalat recent, bloatware ──
        /// <summary>The DisplayIcon value from the registry ("C:\app.exe,0" or ".ico").</summary>
        public string IconSource { get; set; } = "";
        private System.Windows.Media.ImageSource? _icon;
        private bool _iconTried;
        /// <summary>The program icon (loaded on first display, then kept in memory).</summary>
        public System.Windows.Media.ImageSource? Icon
        {
            get
            {
                if (_iconTried) return _icon;
                _iconTried = true;
                try
                {
                    string path = AppCommands.IconFilePath(IconSource);
                    if (path.Length == 0 || !System.IO.File.Exists(path)) return null;
                    if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                    {
                        var bi = new System.Windows.Media.Imaging.BitmapImage();
                        bi.BeginInit(); bi.UriSource = new Uri(path); bi.DecodePixelWidth = 32;
                        bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze();
                        _icon = bi;
                    }
                    else
                    {
                        using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
                        if (ico != null)
                        {
                            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(ico.Handle,
                                System.Windows.Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                            src.Freeze();
                            _icon = src;
                        }
                    }
                }
                catch { _icon = null; }
                return _icon;
            }
        }
        /// <summary>The initial in the fallback icon (programs without their own icon): a coloured square with the first letter.</summary>
        public string Initial => AppTile.Initial(Name);
        private System.Windows.Media.Brush? _tileBrush;
        /// <summary>Colour of the initial square — chosen by name, the same on every start (AppTile).</summary>
        public System.Windows.Media.Brush TileBrush
        {
            get
            {
                if (_tileBrush == null)
                {
                    var brush = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(AppTile.ColorHex(Name)));
                    brush.Freeze();
                    _tileBrush = brush;
                }
                return _tileBrush;
            }
        }
        /// <summary>The program's registry key, as it appears in Registry Editor.</summary>
        public string RegistryPath { get; set; } = "";
        /// <summary>The program's "Change / Repair" command; empty if the program offers none.</summary>
        public string ModifyCommand { get; set; } = "";
        /// <summary>The technician left a note on this program (Services/AppNotesStore.cs).</summary>
        public bool HasNote
        {
            get => _hasNote;
            set
            {
                if (_hasNote == value) return;
                _hasNote = value;
                // the table row shows / hides its "Note" mark without reloading the list
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasNote)));
            }
        }
        private bool _hasNote;
        /// <summary>The program folder (InstallLocation from the registry; may be missing).</summary>
        public string InstallLocation { get; set; } = "";
        /// <summary>The date in the registry when it is newer than the first install (the program rewrote it on an update).</summary>
        public DateTime? UpdatedDateRaw { get; set; }
        /// <summary>A newer version found by winget (null = none / not checked) and the winget package id.</summary>
        public string? UpdateVersion { get; set; }
        public string? WingetId { get; set; }
        public string VersionDisplay => UpdateVersion != null ? $"{Version} → {UpdateVersion}" : Version;
        /// <summary>Last start recorded by Windows (prefetch); LastRunKnown = the program could be looked up at all.</summary>
        public DateTime? LastRun { get; set; }
        public bool LastRunKnown { get; set; }
        /// <summary>Not started for 90 days (or no start recorded and installed more than 90 days ago).</summary>
        public bool IsUnused => LastRunKnown && (LastRun is DateTime l
            ? (DateTime.Now - l).TotalDays > 90
            : InstallDateRaw is DateTime d && (DateTime.Now - d).TotalDays > 90);
        /// <summary>Where the program is registered: HKLM (all users) or HKCU (current account only).</summary>
        public string Hive { get; set; } = "HKLM";
        /// <summary>Width of the size bar in the table (0–60 px), relative to the largest program in the list.</summary>
        public double SizeBarW { get; set; }
        public string PublisherLine => string.IsNullOrWhiteSpace(Publisher) ? "Unknown publisher" : Publisher;
        public bool IsRecent => InstallDateRaw.HasValue && (DateTime.Now - InstallDateRaw.Value).TotalDays <= 30;
        public bool IsLarge  => SizeBytes >= 1L * 1024 * 1024 * 1024;

        /// <summary>Commonly preinstalled programs / "bloatware" (demos, trial antivirus, useless manufacturer utilities).
        /// Only a hint for the technician, not a rule.</summary>
        private static readonly string[] BloatHints =
        {
            "McAfee", "Norton", "WildTangent", "Candy Crush", "Booking.com", "ExpressVPN", "Dropbox Promotion",
            "HP Documentation", "HP JumpStart", "HP Sure", "Lenovo Vantage Service", "Acer Care Center", "ASUS GiftBox",
            "Dell SupportAssist Remediation", "CyberLink Power2Go", "CyberLink PowerDVD", "WPS Office", "Avast", "AVG ",
            "Opera Browser Assistant", "Driver Booster", "Driver Easy", "CCleaner", "WinZip", "Amazon Alexa", "Disney+",
        };
        public bool IsBloat => BloatHints.Any(h => Name.Contains(h, StringComparison.OrdinalIgnoreCase));

        public string Details
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(Version) && Version != "—") parts.Add($"v{Version}");
                if (!string.IsNullOrWhiteSpace(Size) && Size != "—") parts.Add(Size);
                if (!string.IsNullOrWhiteSpace(InstallDate) && InstallDate != "—") parts.Add(InstallDate);
                return parts.Count > 0 ? string.Join("  ·  ", parts) : "—";
            }
        }
    }

    public class WinServiceEntry
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Status { get; set; } = "";
        public string StartType { get; set; } = "";
        public bool IsKnown { get; set; }
        public bool IsCritical { get; set; }
        public string Description { get; set; } = "";
        public string ImagePath  { get; set; } = "";
        public string Publisher  { get; set; } = "";
        /// <summary>The line under the name: the service name, and the maker for services that are not part of Windows.</summary>
        public string SubLine => IsThirdParty && !string.IsNullOrWhiteSpace(Publisher) ? $"{Name} · {Publisher}" : Name;
        public string StatusColor => Status == "Running" ? "#22C55E" : Status == "Stopped" ? "#94A3B8" : "#F59E0B";
        public string StartColor => StartType == "Disabled" ? "#EF4444" : StartType == "Automatic" ? "#3B82F6" : "#94A3B8";
        public string StartBg => "#20" + StartColor.TrimStart('#');
        public int StartTypeSortOrder => StartType switch
        {
            "Automatic" => 0,
            "Manual"    => 1,
            "Disabled"  => 2,
            _           => 3, // Unknown
        };

        /// <summary>True if the service is NOT part of Windows itself — the executable is not under System32/SysWOW64, OR the
        /// publisher (from FileVersionInfo) is not Microsoft. A service with no resolvable path (rare) is conservatively treated
        /// as system, not third-party, so the default list is not filled with meaningless entries.</summary>
        public bool IsThirdParty
        {
            get
            {
                if (string.IsNullOrWhiteSpace(ImagePath)) return false;
                bool underSystemDir = ImagePath.IndexOf(@"\System32\", StringComparison.OrdinalIgnoreCase) >= 0
                                   || ImagePath.IndexOf(@"\SysWOW64\", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isMicrosoftPublisher = Publisher.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;
                return !(underSystemDir || isMicrosoftPublisher);
            }
        }

        // 5.3 — Risk score: "safe" (green), "caution" (yellow), "no" (red)
        public string RiskLevel => IsCritical ? "no" : _safeToDisable.Contains(Name) ? "safe" : "caution";
        public string RiskLabel => IsCritical
            ? LanguageService.S("Services", "RiskNo",      "Do not disable")
            : _safeToDisable.Contains(Name)
                ? LanguageService.S("Services", "RiskSafe",    "Safe")
                : LanguageService.S("Services", "RiskCaution", "Caution");
        public string RiskColor => IsCritical ? "#EF4444" : _safeToDisable.Contains(Name) ? "#22C55E" : "#F59E0B";
        public string RiskBgColor => IsCritical ? "#33EF4444" : _safeToDisable.Contains(Name) ? "#2222C55E" : "#33F59E0B";
        // in the table only "Do not disable" is coloured (red pill); "Safe" / "Caution" stay grey text, no background.
        // The right panel uses RiskColor.
        public string RiskTableColor => IsCritical ? "#EF4444" : "#64748B";
        public double RiskPillOpacity => IsCritical ? 0.14 : 0;
        public string RiskIcon  => IsCritical ? "🔴" : _safeToDisable.Contains(Name) ? "🟢" : "🟡";
        public bool   SafeToDisable => !IsCritical && _safeToDisable.Contains(Name);

        private static readonly HashSet<string> _safeToDisable = new(StringComparer.OrdinalIgnoreCase)
        {
            "DiagTrack", "WerSvc", "TabletInputService", "XblGameSave", "XboxNetApiSvc",
            "MapsBroker", "lfsvc", "RetailDemo", "Fax", "PhoneSvc", "WMPNetworkSvc",
            "TrkWks", "RemoteRegistry"
        };
    }

    public class NetworkAdapterEntry
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string Status { get; set; } = "";
        public string MacAddress { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public string Gateway { get; set; } = "";
        public string Dns { get; set; } = "";
        public string Speed { get; set; } = "";
        public string StatusColor => Status == "Up" ? "#22C55E" : "#94A3B8";
    }

    // ── Process Monitor ──────────────────────────────────────────────────────

    public class ProcessEntry
    {
        public int    Pid         { get; set; }
        public string Name        { get; set; } = "";
        public string Description { get; set; } = "";
        public float  CpuPct      { get; set; }
        public float  RamMB       { get; set; }
        public int    Threads     { get; set; }
        public int    Handles     { get; set; }
        public float  DiskReadKBs { get; set; }
        public float  DiskWriteKBs{ get; set; }
        public string DiskDisplay => (DiskReadKBs + DiskWriteKBs) > 0.5f
            ? $"R:{FormatDisk(DiskReadKBs)} W:{FormatDisk(DiskWriteKBs)}" : "—";
        public string DiskReadDisplay  => FormatDisk(DiskReadKBs);
        public string DiskWriteDisplay => FormatDisk(DiskWriteKBs);
        public string DiskColor   => (DiskReadKBs + DiskWriteKBs) >= 5000 ? "#DC2626"
                                   : (DiskReadKBs + DiskWriteKBs) >= 500  ? "#D97706" : "#64748B";
        public double DiskBarWidth => Math.Min(55, (DiskReadKBs + DiskWriteKBs) / 10000.0 * 55.0);
        private static string FormatDisk(float kbs) =>
            kbs >= 1024 ? $"{kbs/1024:F1}M/s" : $"{kbs:F0}K/s";
        public long   NetSentBytes   { get; set; }
        public long   NetRecvBytes   { get; set; }
        public float  NetSentKBs     => NetSentBytes / 1024f;
        public float  NetRecvKBs     => NetRecvBytes / 1024f;
        public bool   HasWindow      { get; set; }  // true = foreground app

        public string Publisher   { get; set; } = "";
        public string SignStatus  { get; set; } = "";
        public string SignDisplay => SignStatus switch
        {
            "signed"    => string.IsNullOrWhiteSpace(Publisher) ? "Signed" : Publisher,
            "untrusted" => "Signed (untrusted)",
            "unsigned"  => "Unsigned",
            // signature not read (yet): the program's own description ("Windows Explorer") instead of a lone dash
            _           => !string.IsNullOrWhiteSpace(Publisher) ? Publisher : Description ?? ""
        };
        public string SignColor => SignStatus switch
        {
            "signed"    => "#2E8B57",
            "untrusted" => "#D97706",
            // "unsigned" in grey, not red — many legitimate programs (SMD Win included) are not signed;
            // red stays for real problems (an unusual location has its own warning)
            "unsigned"  => "#64748B",
            _           => "#64748B"
        };
        public string SignTooltip => SignStatus switch
        {
            "signed"    => SMDWin.Services.LiteralTexts.Translate("Has a valid Authenticode digital signature (a quick local check — not a legal guarantee of authenticity)."),
            "untrusted" => SMDWin.Services.LiteralTexts.Translate("Has a digital signature, but the certificate chain could not be validated locally — often because an intermediate certificate is not yet in the Windows cache, not necessarily because the file is suspicious. For a full check: right-click the exe → Properties → Digital Signatures."),
            "unsigned"  => SMDWin.Services.LiteralTexts.Translate("Has no Authenticode digital signature. That does not mean malware — many legitimate programs, older or open-source, are not signed — but it deserves more attention than an exe signed by a known vendor."),
            _           => SMDWin.Services.LiteralTexts.Translate("The signature has not been checked yet.")
        };
        public bool   IsNew        { get; set; }

        public int    ParentPid    { get; set; }
        public string ParentName   { get; set; } = "";
        // Parent already closed (very common: apps started by a launcher / by a restarted explorer) → "—" in the table,
        // the detail stays in the tooltip.
        public string ParentDisplay => ParentPid <= 0 || string.IsNullOrEmpty(ParentName) ? "—"
            : $"{ParentName} ({ParentPid})";
        public string ParentTooltip => ParentPid <= 0 ? "No parent process"
            : string.IsNullOrEmpty(ParentName) ? $"Started by PID {ParentPid}, which has already closed"
            : $"Started by {ParentName} (PID {ParentPid})";

        public string ExtraInfo      { get; set; } = "";

        // Visual grouping in Process Monitor
        public string ProcessGroup => HasWindow ? "Apps" : IsSystem ? "System" : "Background";
        public bool   IsSystem     { get; set; }  // true = Windows system process

        public string CpuDisplay  => CpuPct > 0.05f ? $"{CpuPct:F1}%" : "~0%";
        public string RamDisplay  => RamMB >= 1024 ? $"{RamMB/1024:F2} GB" : $"{RamMB:F0} MB";
        public string CpuColor    => CpuPct >= 25 ? "#DC2626" : CpuPct >= 10 ? "#D97706" : "#4B7AB5";
        public string RamColor    => RamMB  >= 1024 ? "#DC2626" : RamMB >= 256 ? "#D97706" : "#2E8B57";

        // ── GPU per-process (populated by ProcessMonitorService GPU PDH reader) ──
        public float  GpuPct      { get; set; }
        public string GpuDisplay  => GpuPct > 0.05f ? $"{GpuPct:F1}%" : GpuPct > 0 ? "<1%" : "—";
        public string GpuColor    => GpuPct >= 50 ? "#DC2626" : GpuPct >= 20 ? "#D97706" : "#6366F1";
        public double GpuBarWidth => GpuPct <= 0 ? 0 : Math.Max(2, Math.Min(55, GpuPct / 100.0 * 55.0));
        public string NetDisplay  => NetSentKBs + NetRecvKBs > 1
            ? $"↑{FormatRate(NetSentKBs)} ↓{FormatRate(NetRecvKBs)}" : "—";
        public string NetSendDisplay => FormatRate(NetSentKBs);
        public string NetRecvDisplay => FormatRate(NetRecvKBs);
        private static string FormatRate(float kbs) =>
            kbs >= 1024 ? $"{kbs/1024:F1} MB/s" : $"{kbs:F0} KB/s";
        // Process Monitor: second line under the name, CPU bar (full at 25%), disk / network summed
        public int    GroupCount   { get; set; } = 1;
        public string SubLine      => GroupCount > 1
            ? (SignDisplay.Length > 0 ? $"{SignDisplay} · {GroupCount} processes" : $"{GroupCount} processes")
            : SignDisplay;
        public double CpuBarW      => Math.Max(CpuPct > 0.05f ? 2 : 0, Math.Min(56, CpuPct / 25.0 * 56.0));
        public System.Windows.Media.Brush? CpuBarBrush =>
            System.Windows.Application.Current?.TryFindResource(CpuPct >= 10 ? "StatusWarningBrush" : "AccentBrush") as System.Windows.Media.Brush;
        public float  DiskTotalKBs => DiskReadKBs + DiskWriteKBs;
        /// <summary>A copy with the same values, used for the summed app rows in Process Monitor › Apps.</summary>
        public ProcessEntry CloneShallow() => (ProcessEntry)MemberwiseClone();
        public string DiskTotalDisplay => DiskTotalKBs < 0.5f ? "—" : FormatRate(DiskTotalKBs);
        public float  NetTotalKBs  => NetSentKBs + NetRecvKBs;
        public string NetTotalDisplay  => NetTotalKBs < 0.5f ? "—" : FormatRate(NetTotalKBs);
        public double CpuBarWidth  => Math.Min(55, CpuPct / 100.0 * 55.0);
        public double RamBarWidth  => RamMB <= 0 ? 0 : Math.Min(55, RamMB / 4096.0 * 55.0);
    }

    public class ProcessSnapshot
    {
        public List<ProcessEntry> TopByCpu          { get; set; } = new();
        public List<ProcessEntry> TopByRam          { get; set; } = new();
        public List<ProcessEntry> AllProcesses      { get; set; } = new();
        public List<ProcessEntry> ForegroundApps    { get; set; } = new();
        public List<ProcessEntry> BackgroundProcesses { get; set; } = new();
        public float TotalCpuPct  { get; set; }
        public long  TotalRamMB   { get; set; }
        public long  UsedRamMB    { get; set; }
        public int   ProcessCount { get; set; }
        public float RamUsedPct   => TotalRamMB > 0 ? (float)UsedRamMB / TotalRamMB * 100f : 0f;
        public float TotalDiskKBs { get; set; } = -1;   // real disk throughput, all physical disks; -1 = counter unavailable
        public float TotalNetKBs  { get; set; } = -1;   // real traffic, all physical adapters; -1 = unavailable
        public string? Error { get; set; }
    }

    // ── Grouped Process Entry (Simple view — all instances grouped by exe) ──
    public class GroupedProcessEntry : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnPropChanged(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));

        public string ExeName      { get; set; } = "";
        public string DisplayName  { get; set; } = "";
        public float  TotalRamMB   { get; set; }
        public float  TotalCpuPct  { get; set; }
        public int    InstanceCount { get; set; }
        public int    Pid          { get; set; }
        public List<int> AllPids   { get; set; } = new();

        // Sub-processes (the individual instances) — populated by BuildGrouped
        public List<ProcessEntry> Children { get; set; } = new();

        // Expandable only if it has more than one instance
        public bool HasChildren => Children.Count > 1;

        private bool _isExpanded = false;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                OnPropChanged(nameof(IsExpanded));
                OnPropChanged(nameof(ExpandIcon));
                OnPropChanged(nameof(VisibleChildren));
            }
        }

        public string ExpandIcon     => HasChildren ? (_isExpanded ? "▾" : "▸") : "";
        public List<ProcessEntry> VisibleChildren => _isExpanded ? Children : new();

        // Fractions for the progress bar [0..1]
        public double RamFraction  { get; set; }
        public double CpuFraction  { get; set; }

        public string RamDisplay   => TotalRamMB >= 1024 ? $"{TotalRamMB/1024:F2} GB" : $"{TotalRamMB:F0} MB";
        public string CpuDisplay   => TotalCpuPct > 0.05f ? $"{TotalCpuPct:F1}%" : "~0%";

        // GPU — sum of all instances in the group
        public float  TotalGpuPct  => Children.Count > 0 ? Children.Sum(c => c.GpuPct) : 0;
        public string GpuDisplay   => TotalGpuPct > 0.05f ? $"{TotalGpuPct:F1}%" : TotalGpuPct > 0 ? "<1%" : "—";
        public string GpuColor     => TotalGpuPct >= 50 ? "#DC2626" : TotalGpuPct >= 20 ? "#D97706" : "#6366F1";
        public double GpuFraction  => Math.Max(0.0, Math.Min(1.0, TotalGpuPct / 100.0));
        public double GpuBarPx     => TotalGpuPct <= 0 ? 0 : Math.Max(2, Math.Min(120, TotalGpuPct / 100.0 * 120.0));
        public string FullName     => InstanceCount > 1 ? $"{DisplayName}  ×{InstanceCount}" : DisplayName;

        public string RamColor     => TotalRamMB >= 1024 ? "#DC2626" : TotalRamMB >= 256 ? "#D97706" : "#2E8B57";
        public string CpuColor     => TotalCpuPct >= 25 ? "#DC2626" : TotalCpuPct >= 10 ? "#D97706" : "#4B7AB5";

        public double RamBarPx     => Math.Max(2, Math.Min(120, RamFraction * 120));
        public double CpuBarPx     => Math.Max(0, Math.Min(120, CpuFraction * 120));
    }

    public class StartupEntry
    {
        public string Name         { get; set; } = "";
        public string Command      { get; set; } = "";
        public string Location     { get; set; } = "";
        public bool   IsEnabled    { get; set; }
        public string RegistryHive { get; set; } = "HKCU";
        public string RegistryKey  { get; set; } = "";
        public string Publisher    { get; set; } = "";

        public string StatusText    => IsEnabled ? "Active"   : "Disabled";
        public string StatusColor   => IsEnabled ? "#22C55E"    : "#94A3B8";
        public string StatusBg      => IsEnabled ? "#1A22C55E"  : "#1494A3B8";
        public string ShortCommand  => Command.Length > 80 ? Command[..77] + "…" : Command;
        public string StatusBadgeBg => IsEnabled ? "#052E16" : "#1C1917";

        /// <summary>
        /// Human-readable category for grouping in Basic view.
        /// Derived from Location + RegistryHive.
        /// </summary>
        public string Category
        {
            get
            {
                if (RegistryHive == "Task")      return "Scheduled Task";
                if (RegistryHive == "Folder")    return "Startup Folder";
                if (RegistryHive == "Service")   return "Windows Service";
                if (RegistryHive == "HKCU")      return "Current User (Registry)";
                if (RegistryHive == "HKLM")
                {
                    if (Location.Contains("Active Setup"))   return "System — Active Setup";
                    if (Location.Contains("Winlogon"))       return "System — Winlogon";
                    if (Location.Contains("ShellService"))   return "System — Shell Extension";
                    return "All Users (Registry)";
                }
                return "Other";
            }
        }

        /// <summary>
        /// Short executable name extracted from Command for Basic view.
        /// </summary>
        public string ExeName
        {
            get
            {
                try
                {
                    var cmd = Command.Trim('"').Trim();
                    var exe = System.IO.Path.GetFileName(cmd.Split(' ')[0]);
                    return string.IsNullOrEmpty(exe) ? Command : exe;
                }
                catch { return Command; }
            }
        }

        public string RiskLevel
        {
            get
            {
                if (Location.Contains("Winlogon"))    return "High";
                if (Location.Contains("Active Setup")) return "System";
                if (RegistryHive == "Service")         return "Service";
                if (RegistryHive == "HKLM")            return "System";
                if (RegistryHive == "Task")            return "Scheduled";
                return "User";
            }
        }

        public string RiskColor
        {
            get
            {
                if (Location.Contains("Winlogon"))    return "#F87171";
                if (Location.Contains("Active Setup")) return "#94A3B8";
                if (RegistryHive == "Service")         return "#38BDF8";
                if (RegistryHive == "HKLM")            return "#60A5FA";
                if (RegistryHive == "Task")            return "#A78BFA";
                return "#22C55E";
            }
        }

        // ── Startup Manager: the target file, invalid entries, impact from the file size ──
        /// <summary>Path of the started file (resolved from Command), or "" if it could not be determined.</summary>
        public string TargetPath { get; set; } = "";
        /// <summary>The started file no longer exists (program uninstalled / moved) — invalid entry.</summary>
        public bool   IsBroken   { get; set; }
        /// <summary>
        /// An entry from the other places Windows starts things from (Active Setup, Winlogon, shell extensions…): it cannot be
        /// turned off the way Task Manager does it (StartupApproved), so it has no switch — only "Remove" (with a back-up).
        /// </summary>
        public bool   IsExtraLocation { get; set; }
        public bool   CanToggle => !IsExtraLocation;
        public string ToggleTip => SMDWin.Services.LiteralTexts.Translate(IsExtraLocation
            ? (Location.Contains("Active Setup") ? "Runs once for each new user, not at every start — it cannot be turned off here" : "Windows starts this from a special place — it cannot be turned off with a switch")
            : (IsEnabled ? "On — click to turn off at the next start" : "Off — click to turn on again"));
        /// <summary>The extra start time Windows measured for this program (Diagnostics-Performance 101/103), ms; 0 = none.</summary>
        public int    MeasuredDelayMs { get; set; }
        public string MeasuredDelayText => MeasuredDelayMs >= 500 ? $"+{MeasuredDelayMs / 1000.0:0.#} s" : "—";
        /// <summary>The program's file appeared in the last 14 days (a new entry at start is often the cause of a new problem).</summary>
        public bool   AddedRecently { get; set; }
        /// <summary>Computed impact (null = the older rule by source).</summary>
        public string? ImpactOverride { get; set; }

        /// <summary>Resolves the executable path from the command line: environment variables, quotes, unquoted paths with
        /// spaces, rundll32 (the DLL from the arguments), simple names from System32.</summary>
        public static string ResolveTarget(string command)
        {
            try
            {
                string cmd = Environment.ExpandEnvironmentVariables(command ?? "").Trim();
                if (cmd.Length == 0) return "";
                string first;
                string rest;
                if (cmd.StartsWith("\""))
                {
                    int end = cmd.IndexOf('"', 1);
                    first = end > 0 ? cmd.Substring(1, end - 1) : cmd.Trim('"');
                    rest  = end > 0 ? cmd.Substring(end + 1).Trim() : "";
                }
                else
                {
                    // Unquoted path with spaces: the longest part that is an existing file
                    first = cmd; rest = "";
                    var parts = cmd.Split(' ');
                    for (int n = parts.Length; n >= 1; n--)
                    {
                        string cand = string.Join(" ", parts.Take(n));
                        if (System.IO.File.Exists(cand)) { first = cand; rest = string.Join(" ", parts.Skip(n)); break; }
                        if (n == 1) { first = parts[0]; rest = string.Join(" ", parts.Skip(1)); }
                    }
                }
                // rundll32 x.dll,Entry → the real target is the DLL
                if (System.IO.Path.GetFileName(first).Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase) && rest.Length > 0)
                {
                    string dll = rest.Split(',')[0].Trim().Trim('"');
                    if (dll.Length > 0) first = dll;
                }
                if (!System.IO.Path.IsPathRooted(first))
                {
                    foreach (var dir in new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
                    {
                        var p = System.IO.Path.Combine(dir, first);
                        if (System.IO.File.Exists(p)) return p;
                        if (!p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(p + ".exe")) return p + ".exe";
                    }
                    return "";   // simple name not found — not declared invalid (it may be in PATH)
                }
                return first;
            }
            catch { return ""; }
        }

        /// <summary>Filled on load (on a background thread): target, invalid, impact.</summary>
        public void AnalyzeTarget()
        {
            TargetPath = ResolveTarget(Command);
            bool checkable = RegistryHive is "HKCU" or "HKLM" or "Folder";
            IsBroken = checkable && TargetPath.Length > 0 && !System.IO.File.Exists(TargetPath);
            if (IsBroken) { ImpactOverride = "—"; return; }
            if (TargetPath.Length == 0) return;
            try
            {
                // Impact approximated by the size of the started program: a small DLL / helper of a few hundred KB does not slow down
                // startup as much as a 100+ MB application.
                long size = new System.IO.FileInfo(TargetPath).Length;
                TargetSizeBytes = size;
                bool isDll = TargetPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
                ImpactOverride = isDll || size < 2L * 1024 * 1024 ? "Low"
                               : size < 25L * 1024 * 1024 ? "Medium" : "High";
            }
            catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
        }
        /// <summary>Size of the started file (0 if it could not be read).</summary>
        public long TargetSizeBytes { get; set; }

        /// <summary>Where it starts from, in plain words.</summary>
        public string WhereLabel => RegistryHive switch
        {
            "HKCU"    => "Registry · this account",
            "HKLM"    => Location.Contains("Winlogon") ? "Registry · Winlogon"
                       : Location.Contains("Active Setup") ? "Registry · Active Setup" : "Registry · all users",
            "Folder"  => "Startup folder",
            "Task"    => "Scheduled task",
            "Service" => "Service",
            _         => Category,
        };

        /// <summary>The 3 bars of the impact indicator: lit by level (Low 1, Medium 2, High 3).</summary>
        private int ImpactLevel => ImpactRating switch { "High" => 3, "Medium" => 2, "Low" => 1, _ => 0 };
        public double ImpactBar1 => ImpactLevel >= 1 ? 1.0 : 0.2;
        public double ImpactBar2 => ImpactLevel >= 2 ? 1.0 : 0.2;
        public double ImpactBar3 => ImpactLevel >= 3 ? 1.0 : 0.2;
        public string ImpactText => IsBroken ? "None" : ImpactRating == "—" ? "Unknown" : ImpactRating;
        public string PublisherLine => string.IsNullOrWhiteSpace(Publisher) ? "Unknown publisher" : Publisher;

        public string BrokenTooltip => IsBroken ? $"File not found: {TargetPath}\nThe program was probably uninstalled or moved — this entry can be removed." : "";

        // Startup — impact rating
        /// <summary>High/Medium/Low boot impact based on entry type and source.</summary>
        public string ImpactRating
        {
            get
            {
                if (ImpactOverride != null) return ImpactOverride;
                if (RegistryHive == "HKLM" && !Location.Contains("Active Setup")) return "High";
                if (RegistryHive == "Service") return "Medium";
                if (RegistryHive == "Task")   return "Medium";
                if (RegistryHive == "Folder") return "Medium";
                return "Low";
            }
        }
        public string ImpactColor => ImpactRating == "High" ? "#EF4444" : ImpactRating == "Medium" ? "#F59E0B" : "#22C55E";
        public string ImpactIcon  => ImpactRating == "High" ? "🔴" : ImpactRating == "Medium" ? "🟡" : "🟢";

        /// <summary>True for Microsoft/Windows system entries that shouldn't be removed.</summary>
        public bool IsSystemEntry =>
            RegistryHive == "Service"
                ? Publisher.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0
                : RegistryHive == "HKLM" ||
                  Location.Contains("Winlogon") ||
                  Location.Contains("Active Setup") ||
                  (Command?.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0) ||
                  (Command?.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0);

        public string SourceGroup => IsSystemEntry ? "System / Microsoft" : "Third-party";
        public string SourceColor => IsSystemEntry ? "#60A5FA" : "#A78BFA";
    }

    // ── Battery ───────────────────────────────────────────────────────────────

    public class BatteryInfo
    {
        public bool   Present           { get; set; }
        public string Name              { get; set; } = "";
        public string Manufacturer      { get; set; } = "";
        public string SerialNumber      { get; set; } = "";
        public string ManufactureDate   { get; set; } = "";
        public string Chemistry         { get; set; } = "";
        public string UniqueID          { get; set; } = "";
        public int    ChargePercent     { get; set; }
        public string Status            { get; set; } = "";
        public bool   IsCharging        { get; set; }
        public bool   IsAC              { get; set; }
        public int    EstimatedRuntime  { get; set; } // minutes
        public int    DesignCapacityMWh { get; set; }
        public int    FullCapacityMWh   { get; set; }
        public int    CycleCount        { get; set; }
        public double VoltageV          { get; set; }
        /// <summary>Nominal battery voltage (0 = not reported) — the reference for "abnormally low voltage".</summary>
        public double DesignVoltageV    { get; set; }
        public double ChargePowerW      { get; set; }
        public double DischargePowerW   { get; set; }
        public int?   WearPct           { get; set; }
        public bool   IsFull            { get; set; }

        public string ChargeColor   => ChargePercent >= 60 ? "#22C55E" : ChargePercent >= 20 ? "#F59E0B" : "#EF4444";
        public string WearColor     => !WearPct.HasValue ? "#6B7280" : WearPct <= 15 ? "#22C55E" : WearPct <= 35 ? "#F59E0B" : "#EF4444";

        /// <summary>Letter grade A-D based on wear and cycle count.</summary>
        public string HealthGrade
        {
            get {
                if (!WearPct.HasValue) return "—";
                int score = 100 - WearPct.Value;
                if (CycleCount > 0) score -= Math.Min(30, CycleCount / 30); // -1pt per 30 cycles, max -30
                return score >= 85 ? "A" : score >= 70 ? "B" : score >= 50 ? "C" : "D";
            }
        }
        public string HealthGradeColor => HealthGrade switch { "A" => "#22C55E", "B" => "#84CC16", "C" => "#F59E0B", "D" => "#EF4444", _ => "#6B7280" };

        /// <summary>Estimated years of life remaining based on wear rate per cycle.</summary>
        public string EstimatedLifeText
        {
            get {
                if (CycleCount <= 10 || !WearPct.HasValue || WearPct.Value <= 0) return "—";
                double wearPerCycle = (double)WearPct.Value / CycleCount;
                double cyclesLeft   = wearPerCycle > 0 ? (80.0 - WearPct.Value) / wearPerCycle : 0; // 80% wear = EOL
                if (cyclesLeft <= 0) return "Near end of life";
                // Assume ~1 cycle/day average
                double yearsLeft = cyclesLeft / 365.0;
                return yearsLeft >= 1 ? $"~{yearsLeft:F1} years" : $"~{cyclesLeft:F0} cycles";
            }
        }
        public string RuntimeText   => EstimatedRuntime > 0 && EstimatedRuntime < 999
                                       ? $"{EstimatedRuntime / 60}h {EstimatedRuntime % 60}m"
                                       : (IsAC || IsFull)
                                           ? SMDWin.Services.LanguageService.S("Battery", "OnACPower", "On AC power")
                                           : "—";
        public string CapacityText  => FullCapacityMWh > 0 && DesignCapacityMWh > 0
                                       ? $"{FullCapacityMWh / 1000} / {DesignCapacityMWh / 1000} Wh"
                                       : "—";
        public string PowerText     => IsCharging && ChargePowerW > 0
                                           ? $"⚡ +{ChargePowerW:F1} W ({SMDWin.Services.LanguageService.S("Battery", "Charging", "charging")})"
                                       : !IsCharging && DischargePowerW > 0
                                           ? $"🔋 -{DischargePowerW:F1} W ({SMDWin.Services.LanguageService.S("Battery", "Discharging", "discharging")})"
                                       : IsFull
                                           ? SMDWin.Services.LanguageService.S("Battery", "FullyCharged", "✅ Fully charged (AC)")
                                       : IsAC
                                           ? SMDWin.Services.LanguageService.S("Battery", "ACPower", "🔌 AC Power")
                                       : IsCharging
                                           ? $"⚡ {SMDWin.Services.LanguageService.S("Battery", "Charging", "Charging")}"
                                           : $"🔋 {SMDWin.Services.LanguageService.S("Battery", "OnBattery", "On battery")}";
        public string StatusIcon    => IsFull ? "✅" : IsCharging ? "⚡" : IsAC ? "🔌" : "🔋";
    }

    // ── Network Traffic ───────────────────────────────────────────────────────

    public class AdapterTrafficEntry
    {
        public string Name        { get; set; } = "";
        public double SendKBs     { get; set; }
        public double RecvKBs     { get; set; }
        public double TotalSentMB { get; set; }
        public double TotalRecvMB { get; set; }
        public bool   IsVirtual   { get; set; }   // Hyper-V/VMware/VirtualBox/VPN/tunnel — excluded from the aggregate total

        public string SendDisplay => SendKBs >= 1024 ? $"{SendKBs/1024:F2} MB/s" : $"{SendKBs:F1} KB/s";
        public string RecvDisplay => RecvKBs >= 1024 ? $"{RecvKBs/1024:F2} MB/s" : $"{RecvKBs:F1} KB/s";
    }

    public class PortScanResult
    {
        public int    Port     { get; set; }
        public bool   IsOpen   { get; set; }
        public string Protocol { get; set; } = "TCP";
        public string Service  { get; set; } = "";
        public string Risk     { get; set; } = "";   // "", "Low", "Medium", "High"
        public string RiskColor => Risk switch
        {
            "High"   => "#EF4444",
            "Medium" => "#F59E0B",
            "Low"    => "#22C55E",
            _        => "#94A3B8"
        };

        public string StatusText  => IsOpen ? "✔ Open" : "✘ Closed";
        public string StatusColor => IsOpen ? "#22C55E" : "#94A3B8";
        public string PortDisplay => Port.ToString();
    }

    /// <summary>One network connection entry for the Network Apps tab.</summary>
    public class NetAppEntry
    {
        public string ProcessName    { get; set; } = "";
        public string ProcessPath    { get; set; } = "";   // full .exe path — needed for firewall rules
        public int    Pid            { get; set; }
        public string Protocol       { get; set; } = "TCP";
        public string LocalEndpoint  { get; set; } = "";
        public string RemoteEndpoint { get; set; } = "";
        public string State          { get; set; } = "";
        public double SendKBs        { get; set; }
        public double RecvKBs        { get; set; }
        public double TotalSentMB    { get; set; }
        public double TotalRecvMB    { get; set; }
        public string GeoCountry     { get; set; } = "";
        public string GeoCity        { get; set; } = "";
        public bool   IsBlocked      { get; set; }         // true = SMDWin firewall block rule exists
        public string Site           { get; set; } = "";   // busiest remote: the DNS name seen for it, else "" (ETW monitor)
        public string Service        { get; set; } = "";   // what that remote port is for ("Web (HTTPS)", "DNS"…)
        public string SiteDisplay => Site.Length > 0 && Service.Length > 0 ? $"{Site} · {Service}"
                                   : Site.Length > 0 ? Site : Service.Length > 0 ? Service : "—";
        public int    ConnectionCount { get; set; } = 1;   // total connections for this process
        public int    EstablishedCount { get; set; }       // only the open (ESTABLISHED) ones

        // 5.5 — App category for grouping
        public string AppCategory => SMDWin.Services.AppKinds.Label(Kind);
        public SMDWin.Services.AppKind Kind => SMDWin.Services.AppKinds.Classify(ProcessName);
        public SMDWin.Services.ArtDrawing CategoryArt => SMDWin.Services.AppArt.For(Kind);
        public string CategoryColor => AppCategory switch
        {
            "Browser"       => "#60A5FA",
            "Gaming"        => "#A78BFA",
            "System"        => "#94A3B8",
            "Communication" => "#34D399",
            "Remote access" => "#F472B6",
            "Cloud sync"    => "#38BDF8",
            "Media"         => "#FB7185",
            "Development"   => "#A3E635",
            _               => "#F59E0B"
        };
        public string CategoryIcon => AppCategory switch
        {
            "Browser"       => "🌐",
            "Gaming"        => "🎮",
            "System"        => "⚙",
            "Communication" => "💬",
            "Remote access" => "🖥",
            "Cloud sync"    => "☁",
            "Media"         => "▶",
            "Development"   => "⌨",
            _               => "📦"
        };

        /// <summary>Total traffic for sorting descending (5.5).</summary>
        public double TotalTrafficKBs => SendKBs + RecvKBs;

        // GeoInfo display — "RO · București" or just the country flag + code
        // the country without its code ("Poland (PL)" → "Poland"), for the narrow column; the full text is the tooltip
        public string GeoShort => string.IsNullOrEmpty(GeoCountry) ? "—"
            : System.Text.RegularExpressions.Regex.Replace(GeoCountry, @"\s*\([A-Z]{2}\)$", "");
        // both directions in one column: "↓12 KB/s ↑3 KB/s", "—" when idle
        public string NowDisplay => SendKBs + RecvKBs < 0.05 ? "—" : $"↓{Rate(RecvKBs)} ↑{Rate(SendKBs)}";
        private static string Rate(double kbs) => kbs >= 1024 ? $"{kbs / 1024:F1} MB/s" : kbs >= 10 ? $"{kbs:F0} KB/s" : $"{kbs:F1} KB/s";
        // TCP states in plain words, short enough for the column
        public string StateDisplay => State switch
        {
            "ESTABLISHED" => "Connected", "LISTEN" => "Listening", "ACTIVE" => "Active", "UDP" => "UDP",
            "TIME_WAIT" => "Closing", "CLOSE_WAIT" => "Closing", "SYN_SENT" => "Connecting", "" => "—", _ => State,
        };
        public string EndpointsTip => $"Remote: {RemoteEndpoint}\nLocal: {LocalEndpoint}";
        public string GeoDisplay => string.IsNullOrEmpty(GeoCountry) ? "—"
            : string.IsNullOrEmpty(GeoCity) ? GeoCountry
            : $"{GeoCountry} · {GeoCity}";

        public string StatusColor => State switch
        {
            "ESTABLISHED" => "#22C55E",
            "LISTEN"      => "#60A5FA",
            "TIME_WAIT"   => "#F59E0B",
            "CLOSE_WAIT"  => "#F97316",
            _             => "#94A3B8"
        };

        public string BlockedIcon    => IsBlocked ? "🚫" : "";
        public string BlockedColor   => IsBlocked ? "#EF4444" : "#94A3B8";
        public string BlockedDisplay => IsBlocked ? "Blocked" : "Allowed";

        public string SendDisplay => SendKBs >= 1024 ? $"{SendKBs/1024:F1} MB/s"
                                   : SendKBs > 0     ? $"{SendKBs:F1} KB/s" : "—";
        public string RecvDisplay => RecvKBs >= 1024 ? $"{RecvKBs/1024:F1} MB/s"
                                   : RecvKBs > 0     ? $"{RecvKBs:F1} KB/s" : "—";
        public string SendColor   => SendKBs > 10 ? "#EF4444" : SendKBs > 0 ? "#F97316" : "#94A3B8";
        public string RecvColor   => RecvKBs > 10 ? "#22C55E" : RecvKBs > 0 ? "#60A5FA" : "#94A3B8";

        // e.g. "↑12 KB ↓3.4 MB" — each side in its own unit (small amounts read 0.0 MB before)
        public string TotalDisplay => (TotalSentMB + TotalRecvMB) > 0
            ? $"↑{Amount(TotalSentMB)} ↓{Amount(TotalRecvMB)}" : "";
        private static string Amount(double mb) => mb >= 1024 ? $"{mb / 1024:F2} GB" : mb >= 1 ? $"{mb:F1} MB"
                                                 : mb * 1024 >= 1 ? $"{mb * 1024:F0} KB" : mb > 0 ? "<1 KB" : "0";

        public bool IsUnsigned          { get; set; }
        public bool IsSuspiciousLocation { get; set; }
        // "unsigned" no longer gives a warning (our own program is unsigned too); only the unusual location remains
        public bool HasRiskFlag => IsSuspiciousLocation;
        public string RiskLabel
        {
            get
            {
                bool ro = SMDWin.Services.LanguageService.CurrentCode == "ro";
                bool de = SMDWin.Services.LanguageService.CurrentCode == "de";
                bool fr = SMDWin.Services.LanguageService.CurrentCode == "fr";
                string unsigned = ro ? "Nesemnat" : de ? "Unsigniert" : fr ? "Non signé" : "Unsigned";
                string location = ro ? "Locație neobișnuită" : de ? "Ungewöhnlicher Speicherort" : fr ? "Emplacement inhabituel" : "Unusual location";
                return (IsUnsigned, IsSuspiciousLocation) switch
                {
                    (true, true)  => location,
                    (true, false) => "",
                    (false, true) => location,
                    _             => "",
                };
            }
        }
        public string RiskIcon  => HasRiskFlag ? "⚠" : "";
        public string RiskColor => HasRiskFlag ? "#F59E0B" : "#94A3B8";
    }

    public class AppSettings
    {
        public int    SettingsVersion { get; set; } = 0;   // used for incremental migration
        public bool   PingBackgroundTrackingEnabled { get; set; } = true;
        public string PingBackgroundHost { get; set; } = "8.8.8.8";
        public bool   PingUptimeSustainedOnly { get; set; } = false;
        public bool EnableAnimations { get; set; } = true;
        // indicatorul „test in curs” din sidebar: Dot | Pulse | SpinRing | Hourglass | Bolt | Square* (SquareBadgeAnim)
        public string TaskBadgeStyle { get; set; } = "SquareOrbit";
        public string ThemeName { get; set; } = "Auto";
        public bool ColorfulIcons { get; set; } = true;   // vivid colored nav icons
        public bool   AutoTheme { get; set; } = false;  // kept for JSON backward compat
        public string AutoDarkTheme  { get; set; } = "Dark";
        public string AutoLightTheme { get; set; } = "Light";
        public string Language  { get; set; } = "en";
        public bool   LanguageManuallySet { get; set; } = false;
        public double RefreshInterval { get; set; } = 1.0;
        public int    NetworkRefreshSec  { get; set; } = 2;   // Network tab
        public int    StorageRefreshSec  { get; set; } = 5;   // Storage tab
        public int    BatteryRefreshSec  { get; set; } = 10;  // Battery tab
        public bool StartMinimized { get; set; } = false;
        public bool MinimizeToTray { get; set; } = true;
        public bool StartWithWindows { get; set; } = false;  // #7: registry Run key
        public bool AutoScanOnStart { get; set; } = true;
        public int EventDaysBack { get; set; } = 7;
        public string    HealthCheckLastSeverity { get; set; } = "None"; // "None"/"Ok"/"Warning"/"Critical"
        public int       HealthCheckLastCritical { get; set; } = 0;
        /// <summary>The last Health check found physical danger (DiagnosticOrchestrator.IsPhysicalDanger) — tints the Dark background red.</summary>
        public bool      HealthCheckLastDanger   { get; set; } = false;
        public int       HealthCheckLastWarning  { get; set; } = 0;
        public DateTime? HealthCheckLastRunUtc   { get; set; } = null;
        [System.Text.Json.Serialization.JsonIgnore]
        public string ReportSavePath
        {
            get => ReportPath;
            set { if (!string.IsNullOrEmpty(value)) ReportPath = value; }
        }
        /// <summary>The folder chosen for reports; empty = Data\Reports next to the exe (SettingsService.GetEffectiveReportFolder).</summary>
        public string ReportPath { get; set; } = "";
        public bool   UseNetworkReportPath { get; set; } = false;
        public string NetworkReportPath    { get; set; } = "";
        public string ReportTemplate { get; set; } = "Standard";
        public bool ShowTempNotif { get; set; } = true;
        public float TempWarnCpu { get; set; } = 90f;
        public float TempWarnGpu { get; set; } = 90f;
        public string CrystalDiskInfoPath { get; set; } = @"C:\Program Files\CrystalDiskInfo\DiskInfo64.exe";
        public bool   SensorSafeMode { get; set; } = false;       // set after a start that closed while the sensor libraries were opening (SensorStartGuard)
        public bool   BetaNoticeShown { get; set; } = false;      // the one-time "beta, no warranty, what it can change" notice
        public string DriverSearchSite { get; set; } = "google";      // "google" (the default) or "driverpack" — a diagnostics app should not send people to a third-party driver site by default
        public string DriverViewMode { get; set; } = "Basic";     // "Basic" or "Advanced"
        public int ProcessRefreshSec { get; set; } = 2;
        public bool UseMica { get; set; } = (Environment.OSVersion.Version.Major == 10 && Environment.OSVersion.Version.Build >= 22000);
        public bool MicaExplicitlyDisabled { get; set; } = false;  // set to true when the user turns Mica off in Settings
        public double MicaOpacity { get; set; } = 0.80;
        public bool AuroraAlertTint { get; set; } = true;
        // Windows 11 24H2+: reading the Wi-Fi details counts as location. "" = not asked yet, "granted", "denied" — only the
        // Network › Wi-Fi › Live tab may trigger the Windows prompt; elsewhere the network name comes from the network profile.
        public string WifiLocationAccess { get; set; } = "";
        // Network › Apps alerts (NetAlerts) — all off by default
        public bool NetAlertNewApp { get; set; } = false;     // a program uses the internet for the first time on this PC
        public int  NetAlertMbPerMin { get; set; } = 0;       // a program moves more than this many MB in one minute (0 = off)
        public bool NetAlertBlocklist { get; set; } = false;  // contact with an address on the Feodo Tracker list (downloaded)  // Dark: the background light slowly turns red while something is critical
        public string WidgetMode { get; set; } = "Graphs";  // "Graphs" or "Gauges"

        // ── Widget configurable metrics (CSV of MetricType names) ──
        public string WidgetMetrics    { get; set; } = "CPU,RAM,Disk,Network";
        public bool   WidgetShowMetrics { get; set; } = true;
        public bool   WidgetShowProcs   { get; set; } = true;

        // ── Widget saved positions (per type, set on drag) ──
        public double WidgetPosX { get; set; }
        public double WidgetPosY { get; set; }
        public bool   WidgetPosValid { get; set; }
        public double PinnedPosX { get; set; }
        public double PinnedPosY { get; set; }
        public bool   PinnedPosValid { get; set; }
        public double ShutdownTimerPosX { get; set; }
        public double ShutdownTimerPosY { get; set; }
        public bool   ShutdownTimerPosValid { get; set; }

        // ── Legacy (kept for JSON backward compat, ignored at runtime) ──
        public bool WidgetForceDark { get; set; } = true;

        // ── Notification thresholds ──
        public float CpuTempAlertThreshold  { get; set; } = 85f;
        public float GpuTempAlertThreshold  { get; set; } = 85f;
        public float CpuUsageAlertThreshold { get; set; } = 95f;
        /// <summary>Secondary/accent color name: "Blue" | "Red" | "Green" | "Orange"</summary>
        public string AccentName { get; set; } = "Blue";
        public string? CustomAccentHex { get; set; } = null;
        /// <summary>Enable/disable all system notifications (throttle, temperature, network etc.)</summary>
        public bool EnableNotifications { get; set; } = true;

        // ── Sensor backend ──
        /// <summary>
        /// When true, LibreHardwareMonitor is loaded at startup for precise CPU/GPU/fan/voltage sensors.
        /// Default false — avoids antivirus false-positives and speeds up startup.
        /// Requires restart to take effect. When false, falls back to WMI + PerformanceCounter.
        /// </summary>
        public bool UseLhm { get; set; } = true; // LHM always-on prin PawnIO

        // ── Dashboard chart settings ──
        /// <summary>Time window shown in dashboard live charts: 60, 600 or 3600 seconds</summary>
        public int  ChartWindowSeconds { get; set; } = 60;
        public bool StressProcTableExpanded { get; set; } = false;
        public bool RamProcTableExpanded { get; set; } = false;
        /// <summary>Show gradient fill under chart lines</summary>
        public bool ChartShowFill      { get; set; } = true;
        public string ChartColorMode   { get; set; } = "LightBlue"; // Colored | Accent | LightBlue | Gray
        // Marking of stress tests on the Temp & Stress charts: Band (bottom band) | Full | Subtle
        public string StressMarkMode   { get; set; } = "Subtle";
        /// <summary>Temp &amp; Stress: the line under the charts that shows how long a stress test ran.</summary>
        public bool StressChartShowTests { get; set; } = true;
        /// <summary>How the period of a stress test is marked on the charts: "Tint" = the curve and its shade in red; "Line" = a line under the chart.</summary>
        public string StressChartMarkStyle { get; set; } = "Tint";
        /// <summary>Temp &amp; Stress: the line under the charts that shows when there was throttling.</summary>
        public bool StressChartShowThrottle { get; set; } = true;
        public bool ShowChartGridlines { get; set; } = false;   // the vertical chart grid lines (the horizontal ones are always drawn)
        public bool ShowDiskLatency    { get; set; } = false;

        public Dictionary<string, float> ScaleMaxObserved { get; set; } = new();

        public string WidgetCompactMetrics { get; set; } = "CPUTemp";

        public bool TraySensorsEnabled { get; set; } = true;

        public string TraySensorColors { get; set; } = "";

        public string? TraySensorMetricsOverride { get; set; } = null;

        public string SidebarTestAnimation { get; set; } = "ArcRotate";
        public string HwInfoPdfTemplate { get; set; } = "OnePage";

        public string BatteryGlyphStyle { get; set; } = SMDWin.Services.BatteryBlocks.Default;
        /// <summary>The processor drawing in System Information: "Auto" (by processor) or the name of a CpuPackageStyle style.</summary>
        public string CpuPictureStyle { get; set; } = SMDWin.Services.CpuPackage.Auto;

        public bool DesignExperimentMonoNumbers { get; set; } = false;


        public bool DesignExperimentSignatureMotif { get; set; } = false;

        public bool DesignExperimentGrainTexture { get; set; } = false;
        /// <summary>Design experiment: the Power page shows the power map (boxes + lines) instead of the "What the plan does now" list.</summary>
        public bool DesignExperimentPowerMap { get; set; } = false;
        /// <summary>
        /// Design experiment: layout of the System Information page. "" = default (header + table with a status column) ·
        /// "TableGroups" = table in two groups · "Split" = system and Windows on the left, hardware on the right ·
        /// "List" = the computer on the left, all components as rows · "Grid" = the old card grid.
        /// </summary>
        public string DesignExperimentSysInfoLayout { get; set; } = "";
        // About window: which logo animation plays (developer window › Design; the one chosen stays as the final one)
        public string DesignExperimentAboutAnim { get; set; } = "Heartbeat";
        /// <summary>The copy of the Ultimate Performance plan SMD Win created (Power page), so it is not created twice.</summary>
        public string? UltimatePlanGuid { get; set; }
        /// <summary>Laptop: the plan to use on the charger / on battery (null = whatever plan is active). SMD Win switches
        /// the plan when the power source changes, while it runs.</summary>
        public string? PowerPlanAc { get; set; }
        public string? PowerPlanDc { get; set; }
        /// <summary>The electricity price per kWh, for the monthly estimate on the Power page.</summary>
        public double ElectricityPricePerKwh { get; set; } = 1.30;
        /// <summary>Developer: the old three-column "Sleep &amp; wake" card instead of the chip strip (kept as a fallback).</summary>
        public bool DesignExperimentOldSleepCard { get; set; }

        public double DesignExperimentCornerRadius { get; set; } = 8.0;

        public bool DesignExperimentShadowsOff { get; set; } = true;

        public int SelectedButtonStyleId { get; set; } = 6;

        public bool SidebarRailEnabled { get; set; } = true;

        public string TraySensorFontSize { get; set; } = "M";

        /// <summary>Saved X position of the compact widget (screen coords).</summary>
        public double WidgetCompactX { get; set; }

        /// <summary>Saved Y position of the compact widget (screen coords).</summary>
        public double WidgetCompactY { get; set; }

        /// <summary>True when WidgetCompactX/Y hold a valid saved position.</summary>
        public bool WidgetCompactPosValid { get; set; }

        public double WidgetOpacity { get; set; } = 0.93;

        public bool WidgetAlertOnlyMode { get; set; } = false;
        // The compact widget style: "Pill" (value + sparkline) | "Gauges" (mini arc) | "Minimal" (text only)
        public string WidgetStyle { get; set; } = "Gauges"; // implicit Gauges
        public bool AutoSnapshotOnStress { get; set; } = true;
        /// <summary>Writes the sensors to HKCU\Software\SMDWin\Sensors every 5 s (for other programs). Off by default.</summary>
        public bool ExportSensorsToRegistry { get; set; } = false;
        /// <summary>Chart recording while the charts are not on screen (window minimized, in the tray or on another page):
        /// "Full" (default), "Reduced" (one reading every 5 s) or "Off" (paused).</summary>
        public string BackgroundRecording { get; set; } = "Full";
        /// <summary>Time window of the traffic chart on the Network page, in seconds (300 / 600 / 1800 / 3600).</summary>
        public int NetTrafficWindowSec { get; set; } = 300;
        /// <summary>Time window of the charts in a process's details, in seconds (60 / 300 / 900 / 3600).</summary>
        public int ProcessChartWindowSec { get; set; } = 60;
        /// <summary>Time window of the "Disk activity" chart on the Storage page, in seconds (60 / 300 / 900 / 3600).</summary>
        public int StorageActWindowSec { get; set; } = 60;   // the Storage activity charts: 1 minute fills quickly
        /// <summary>Surface Scan, read speed chart: −1 = the whole disk (default), 0 = what has been scanned so far,
        /// N = the last N minutes.</summary>
        public int SurfaceChartWindowMin { get; set; } = -1;
        /// <summary>The unit of the displayed temperatures: "C" (default) or "F".</summary>
        public string TemperatureUnit { get; set; } = "C";
        /// <summary>Interface density: "Auto" (by resolution, at start), "Compact" or "Normal".</summary>
        public string UiDensity { get; set; } = "Auto";
        /// <summary>Workshop name, put in the report header (PDF / diagnostic package). Empty = not shown.</summary>
        public string WorkshopName { get; set; } = "";
        /// <summary>Workshop phone, next to the name in reports. Empty = not shown.</summary>
        public string WorkshopPhone { get; set; } = "";
        /// <summary>Advanced › "Use in a computer service": the workshop details appear in reports only when it is on.</summary>
        public bool WorkshopMode { get; set; } = false;
        /// <summary>Workshop address or website, under the name in HTML reports and in the diagnostic package.</summary>
        public string WorkshopContact { get; set; } = "";
        /// <summary>Temperature threshold for the processor; 0 = Auto (from the processor model).</summary>
        public int CpuTempLimit { get; set; } = 0;
        /// <summary>Temperature threshold for the graphics card; 0 = Auto (90 °C).</summary>
        public int GpuTempLimit { get; set; } = 0;
        /// <summary>How often values are read: "Saver" (3 s), "Normal" (1 s), "Fast" (0.5 s).</summary>
        public string UpdateSpeed { get; set; } = "Normal";
        /// <summary>On battery it switches to Saver by itself.</summary>
        public bool SlowerOnBattery { get; set; } = true;
        public bool WidgetEnabled { get; set; } = false;

        /// <summary>When true, closing main window sends it to tray instead of exiting.</summary>
        public bool CloseToTray { get; set; } = false;
        public bool StartupShowAllServices { get; set; } = false;
        public bool SelfTestSavePortable { get; set; } = false;
    }

    public class DeviceManagerEntry
    {
        public string Name         { get; set; } = "";
        public string DeviceClass  { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string HardwareId   { get; set; } = "";
        public string DeviceId     { get; set; } = "";
        public string Status       { get; set; } = "";
        public int    ErrorCode    { get; set; }
        public bool   IsMissing    { get; set; }

        public string StatusColor => IsMissing ? "#EF4444" : "#22C55E";
        public string StatusIcon  => IsMissing ? "⚠" : "✔";
        public string ClassIcon
        {
            get
            {
                var c = (DeviceClass ?? "").ToLower();
                if (c.Contains("display") || c.Contains("video"))   return "🖥";
                if (c.Contains("net")     || c.Contains("network")) return "🌐";
                if (c.Contains("audio")   || c.Contains("sound"))   return "🔊";
                if (c.Contains("usb"))                               return "🔌";
                if (c.Contains("disk")    || c.Contains("storage")) return "💾";
                if (c.Contains("bluetooth"))                         return "🔵";
                if (c.Contains("print"))                             return "🖨";
                if (c.Contains("keyboard"))                          return "⌨";
                if (c.Contains("mouse")   || c.Contains("hid"))     return "🖱";
                return "🔧";
            }
        }

        // ── Fields for the Driver Details card ──
        public string? Date     { get; set; }
        public DateTime? DateRaw { get; set; }
        public string? Version  { get; set; }
        public bool    IsSigned { get; set; } = true;
    }

    /// <summary>Results container for Diagnose &amp; Report feature.</summary>
    public class DiagResults
    {
        /// <summary>True = Extended mode (3 min stress + benchmark). False = Quick mode (~2 min).</summary>
        public bool IsExtended { get; set; } = false;
        /// <summary>True = temperatures captured under full CPU+GPU stress load (no disclaimer needed).</summary>
        public bool TempsFromStress { get; set; } = false;

        public SystemSummary Summary { get; set; } = new();
        public List<DiskHealthEntry> Disks { get; set; } = new();
        public SMDWin.Services.DiskBenchmarkResult? DiskBenchmark { get; set; }

        // Null-safe accessors — safe to reference anywhere without null checks
        public double DiskBenchReadMBs  => DiskBenchmark?.SeqReadMBs  ?? 0;
        public double DiskBenchWriteMBs => DiskBenchmark?.SeqWriteMBs ?? 0;
        public string DiskBenchRating   => DiskBenchmark?.Rating      ?? "—";
        public string DiskBenchDrive    => DiskBenchmark?.DriveLetter ?? "—";
        public List<RamEntry> RamModules { get; set; } = new();
        public double CpuTempMax { get; set; }
        public double CpuTempMin { get; set; }
        public double GpuTempMax { get; set; }
        public double GpuTempMin { get; set; }
        public double GpuLoadMax { get; set; }   // Peak GPU utilization % during test
        public double GpuLoadAvg { get; set; }   // Average GPU utilization % during test
        public List<TemperatureEntry> AllTemps { get; set; } = new();
        public BatteryInfo Battery { get; set; } = new();
        public SMDWin.Services.SpeedTestResult? Speed { get; set; }
        public List<EventLogEntry> Events { get; set; } = new();
        public List<CrashEntry> Crashes { get; set; } = new();
        public double CpuBenchScore       { get; set; }   // multi-core hash/s
        public double CpuSingleCoreScore  { get; set; }   // single-core hash/s
        public int    CpuIntegrityErrors  { get; set; } = -1; // -1 = not run yet
        public double RamBenchReadGBs     { get; set; }
        public double RamBenchWriteGBs    { get; set; }
        public double RamLatencyNs        { get; set; }   // memory access latency in nanoseconds
        public long   DiskRandRead4kIOPS  { get; set; }   // 4K random read IOPS
        public long   DiskRandWrite4kIOPS { get; set; }   // 4K random write IOPS
        // RAM integrity test result (optional — only if user enabled it)
        public bool   RamIntegrityRan     { get; set; }
        public bool   RamIntegrityPassed  { get; set; }
        public long   RamIntegrityErrors  { get; set; }
        public int    RamIntegritySizeMB  { get; set; }
        // Disk End-of-Life prediction
        public double DiskEoLYearsEstimate { get; set; }  // -1 = not available
        public string DiskEoLBasis         { get; set; } = "";
        public List<NetworkAdapterEntry> NetworkAdapters { get; set; } = new();
        // Throttle detection
        public bool   CpuThrottleDetected { get; set; }
        public int    CpuThrottleCount    { get; set; }
        public double CpuThrottlePct      { get; set; }
        public List<double> CpuTempSamples { get; set; } = new();
    }

    // ── Local Network Device ──────────────────────────────────────────────────
    public class LocalNetworkDevice
    {
        public string IpAddress  { get; set; } = "";
        public string Hostname   { get; set; } = "—";
        public string MacAddress { get; set; } = "—";
        public string Vendor     { get; set; } = "—";
        public string PingMs     { get; set; } = "—";
        public string Status     { get; set; } = "Online";
        public bool IsRouter     { get; set; }

        // "ms" is added only to a number; "—" and "no ping" stay as they are
        public string PingDisplay => int.TryParse(PingMs.Trim(), out _) ? $"{PingMs.Trim()} ms" : PingMs;
        public string StatusColor  => Status == "Online" ? "#22C55E" : "#94A3B8";
    }

    // ── Dashboard Drive Row ───────────────────────────────────────────────────
    public class DashDriveEntry
    {
        public string DriveLetter   { get; set; } = "";
        public string Label         { get; set; } = "";
        public string DriveType     { get; set; } = "";
        public string Format        { get; set; } = "";
        public string Model         { get; set; } = "";
        public double UsedPct       { get; set; }
        public double FreeGB        { get; set; }
        public double TotalGB       { get; set; }

        public string UsedPctDisplay => $"{UsedPct:F0}%";
        public string FreeDisplay    => $"{FreeGB:F1} GB";
        public string TotalDisplay   => $"{TotalGB:F0} GB";

        // IsCritical = true if the used space > 90%
        public bool   IsCritical     => UsedPct >= 90;

        // UsedBarWidth: kept for compatibility with other places
        public double UsedBarWidth   => Math.Max(2, UsedPct / 100.0 * 300.0);

        // UsedRatio: 0.0 - 1.0, used by the ScaleTransform in XAML
        public double UsedRatio      => Math.Max(0.01, Math.Min(1.0, UsedPct / 100.0));

        // FreeBarPct: kept for compatibility
        public double FreeBarPct     => Math.Max(1, 100.0 - UsedPct);

        // UsedColor: indicator text colour (orange at 75-90%, red above 90%)
        public string UsedColor      => UsedPct >= 90 ? "#EF4444" : UsedPct >= 75 ? "#F59E0B" : "#22C55E";

        // FreeTrackColor kept for compatibility
        public string FreeTrackColor => "#2A3040";
    }

    public class BlockedDriverItem
    {
        public string Index      { get; set; } = "";
        public string HardwareId { get; set; } = "";
        public string DeviceName { get; set; } = "";
    }

    public class TrimDisplayEntry
    {
        public string DriveLetter { get; set; } = "";
        public string Description { get; set; } = "";
        public string TrimColor   => Description.Contains("enabled") ? "#22C55E" :
                                     Description.Contains("disabled") ? "#EF4444" : "#6B7280";
    }

    public class ApmDisplayEntry
    {
        public string DriveLetter  { get; set; } = "";
        public string LevelLabel   { get; set; } = "";
        public string LevelDisplay => CurrentLevel > 0 ? CurrentLevel.ToString() : "N/A";
        public int    CurrentLevel { get; set; } = -1;
    }
}
