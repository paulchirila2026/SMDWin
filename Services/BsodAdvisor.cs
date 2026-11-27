using System;
using System.Collections.Generic;

namespace SMDWin.Services
{
    /// <summary>
    /// Interpretation of a blue screen for the technician: short meaning, likely area (driver / memory / disk /
    /// graphics / hardware / system) and recommended steps, with the SMD Win page where it can be checked. Rules by the
    /// STOP code name and the area of the faulting driver (<see cref="MiniDumpParser.DriverKind"/>, independent of the
    /// interface language). It is guidance, not a certain diagnosis.
    /// </summary>
    public static class BsodAdvisor
    {
        public sealed record Advice(string Area, string Summary, List<string> Steps, string? Page, string? PageLabel);

        /// <param name="bugCheckName">STOP code name (e.g. MEMORY_MANAGEMENT); may also contain the hex code.</param>
        /// <param name="culpritKind">Area of the suspected driver: a name from <see cref="MiniDumpParser.DriverKind"/>
        /// (CrashEntry.FaultingModuleKind). Empty = inferred from <paramref name="culprit"/>.</param>
        /// <param name="culprit">File of the suspected driver (e.g. nvlddmkm.sys).</param>
        /// <param name="culpritIsGeneric">The driver found is the Windows kernel (ntoskrnl/hal) — the real cause is elsewhere.</param>
        public static Advice Get(string? bugCheckName, string? culpritKind, string? culprit, bool culpritIsGeneric)
        {
            string name = (bugCheckName ?? "").ToUpperInvariant();
            string drv  = string.IsNullOrWhiteSpace(culprit) || culpritIsGeneric ? "" : culprit!;
            bool Has(params string[] keys) { foreach (var k in keys) if (name.Contains(k)) return true; return false; }

            var kind = MiniDumpParser.DriverKind.Other;
            if (!string.IsNullOrWhiteSpace(culpritKind) && Enum.TryParse(culpritKind, ignoreCase: true, out MiniDumpParser.DriverKind parsed))
                kind = parsed;
            else if (!string.IsNullOrWhiteSpace(culprit))
                kind = MiniDumpParser.GetDriverKind(culprit);

            // 0) Crash caused on purpose — not a fault
            if (Has("MANUALLY_INITIATED"))
                return new("Manual crash",
                    T("This crash was triggered on purpose (keyboard shortcut or a tool) — it is not a failure."),
                    new()
                    {
                        T("Nothing to fix."),
                        T("If nobody triggered it, check for software that forces crash dumps."),
                    }, null, null);

            // 1) Graphics — by code or by faulting driver
            if (Has("VIDEO_", "THREAD_STUCK_IN_DEVICE_DRIVER") || kind == MiniDumpParser.DriverKind.Video)
                return new("Graphics card / driver",
                    T("The graphics driver stopped responding or crashed."),
                    new()
                    {
                        T("Reinstall the graphics driver cleanly (uninstall the old one first, e.g. with DDU)."),
                        T("Check GPU temperature under load (Temp & Stress → GPU Stress)."),
                        T("Remove any GPU overclock; on laptops, clean the cooling system."),
                    }, "Stress", T("Open Temp & Stress"));

            // 2) Hardware (processor / memory / motherboard / power supply)
            if (Has("WHEA_UNCORRECTABLE", "MACHINE_CHECK", "CLOCK_WATCHDOG", "UNEXPECTED_KERNEL_MODE_TRAP", "DATA_BUS_ERROR", "NMI_HARDWARE_FAILURE"))
                return new("Hardware (CPU / memory / power)",
                    T("The processor or another component reported an error it could not recover from."),
                    new()
                    {
                        T("Disable overclocking and XMP/EXPO in BIOS, then test again."),
                        T("Check CPU temperatures under load (Temp & Stress → CPU Stress)."),
                        T("Update the BIOS; test with another power supply if crashes happen under load."),
                    }, "Stress", T("Open Temp & Stress"));

            // 3) Memorie RAM
            if (Has("MEMORY_MANAGEMENT", "PFN_LIST_CORRUPT", "BAD_POOL", "POOL_CORRUPTION", "FAULTY_HARDWARE_CORRUPTED_PAGE")
                || (Has("PAGE_FAULT_IN_NONPAGED_AREA") && drv.Length == 0))
                return new("Memory (RAM)",
                    T("Windows found corrupted data in memory — often faulty RAM, sometimes a driver."),
                    new()
                    {
                        T("Run the Advanced RAM test (RAM Memory page) or Windows Memory Diagnostic."),
                        T("Disable XMP/EXPO; if there are 2+ modules, test them one at a time."),
                        drv.Length > 0 ? T("Update or remove the driver {0}.", drv) : T("Update chipset and storage drivers."),
                    }, "Ram", T("Open RAM Memory"));

            // 4) Disk / storage. DPC_WATCHDOG points to storage only when no other driver is named.
            if (Has("KERNEL_DATA_INPAGE", "UNEXPECTED_STORE", "NTFS_FILE_SYSTEM", "FAT_FILE_SYSTEM", "INACCESSIBLE_BOOT_DEVICE", "CRITICAL_OBJECT_TERMINATION")
                || (Has("DPC_WATCHDOG") && (drv.Length == 0 || kind == MiniDumpParser.DriverKind.Storage))
                || kind == MiniDumpParser.DriverKind.Storage)
                return new("Disk / storage",
                    T("Windows could not read or write data on the disk in time."),
                    new()
                    {
                        T("Check disk health (Storage → SMART) and run a Surface Scan."),
                        T("Check the SATA cable / M.2 seating; update the SSD firmware."),
                        T("Run chkdsk /f on the system drive."),
                    }, "Disk", T("Open Storage"));

            // 5) Retea
            if (kind == MiniDumpParser.DriverKind.Network || Has("BUGCODE_NDIS"))
                return new("Network driver",
                    T("A network driver{0} caused the crash.", drv.Length > 0 ? $" ({drv})" : ""),
                    new()
                    {
                        T("Update the network (Wi-Fi / Ethernet) driver from the laptop or board manufacturer."),
                        T("If it started after an update, roll the driver back (Drivers page)."),
                        T("Disable VPN / firewall software temporarily to test."),
                    }, "Drivers", T("Open Drivers"));

            // 6) USB
            if (kind == MiniDumpParser.DriverKind.Usb || Has("BUGCODE_USB"))
                return new("USB device / driver",
                    T("A USB device or its driver{0} caused the crash.", drv.Length > 0 ? $" ({drv})" : ""),
                    new()
                    {
                        T("Unplug USB devices (hubs, docks, adapters) and see if the crashes stop."),
                        T("Update the chipset and USB controller drivers."),
                        T("Try another port or cable for the device that was in use."),
                    }, "Drivers", T("Open Drivers"));

            // 7) Trecerea in / din repaus
            if (Has("DRIVER_POWER_STATE_FAILURE", "INTERNAL_POWER_ERROR"))
                return new("Sleep / power transition",
                    drv.Length > 0 ? T("The driver {0} did not answer while the PC was going to sleep or waking up.", drv)
                                   : T("A driver did not answer while the PC was going to sleep or waking up."),
                    new()
                    {
                        drv.Length > 0 ? T("Update the driver {0}.", drv) : T("Update the chipset, network and graphics drivers."),
                        T("Turn off Fast Startup and test again (Power page)."),
                        T("Unplug devices that are connected during sleep (USB, docks) to find the one involved."),
                    }, "Battery", T("Open Power"));

            // 8) BIOS / firmware
            if (Has("ACPI_BIOS"))
                return new("BIOS / firmware",
                    T("The BIOS gave Windows invalid power-management (ACPI) data."),
                    new()
                    {
                        T("Update the BIOS / UEFI from the manufacturer."),
                        T("Load the BIOS default settings."),
                        T("If it started after a hardware change, undo the change and test again."),
                    }, null, null);

            // 9) System files
            if ((Has("CRITICAL_PROCESS_DIED", "SYSTEM_SERVICE_EXCEPTION") && drv.Length == 0)
                || Has("REGISTRY_ERROR", "BAD_SYSTEM_CONFIG_INFO"))
                return new("Windows system files",
                    T("A critical Windows component stopped unexpectedly or its data is damaged."),
                    new()
                    {
                        T("Run sfc /scannow and DISM /RestoreHealth (Tools → Repair Windows)."),
                        T("Check the disk health (Storage → SMART)."),
                        T("Uninstall recently added software or updates."),
                    }, "Tools", T("Open Tools"));

            // 10) Corrupted kernel structures with no named driver: driver or memory
            if (Has("KERNEL_SECURITY_CHECK", "KERNEL_MODE_HEAP_CORRUPTION", "CRITICAL_STRUCTURE_CORRUPTION", "ATTEMPTED_EXECUTE_OF_NOEXECUTE") && drv.Length == 0)
                return new("Driver or memory",
                    T("Windows found damaged data inside the kernel — a faulty driver or faulty RAM."),
                    new()
                    {
                        T("Update the drivers (chipset, graphics, network, storage)."),
                        T("Run the Advanced RAM test (RAM Memory page)."),
                        T("Uninstall low-level tools installed recently (antivirus, tuning or RGB utilities)."),
                    }, "Ram", T("Open RAM Memory"));

            // 11) Driver (cazul general: IRQL, exceptii in kernel)
            if (Has("IRQL", "KMODE_EXCEPTION", "KERNEL_MODE_EXCEPTION", "SYSTEM_THREAD_EXCEPTION", "SYSTEM_SERVICE_EXCEPTION", "DRIVER_", "PAGE_FAULT_IN_NONPAGED_AREA",
                    "KERNEL_SECURITY_CHECK", "KERNEL_MODE_HEAP_CORRUPTION", "CRITICAL_STRUCTURE_CORRUPTION", "ATTEMPTED_", "DPC_WATCHDOG", "FLTMGR")
                || drv.Length > 0)
                return new("Driver",
                    drv.Length > 0 ? T("The driver {0} most likely caused the crash.", drv) : T("A driver most likely caused the crash."),
                    new()
                    {
                        drv.Length > 0 ? T("Update, roll back or uninstall the driver {0}.", drv) : T("Update the drivers (chipset, graphics, network, storage)."),
                        T("If crashes started recently, check what was installed around that date."),
                        T("If it keeps happening with different drivers, test RAM and disk."),
                    }, "Drivers", T("Open Drivers"));

            return new("Unknown",
                T("The cause could not be narrowed down from this dump."),
                new()
                {
                    T("Install pending Windows updates and update the drivers."),
                    T("Test RAM (RAM Memory page) and disk health (Storage page)."),
                    T("Analyze the dump with WinDbg for the full stack."),
                }, null, null);
        }

        /// <summary>The text in the app language (the English text is the key in the language files); {0} gets the argument.</summary>
        private static string T(string en, params object[] a) => a.Length == 0 ? LiteralTexts.Translate(en) : string.Format(LiteralTexts.Translate(en), a);
    }
}
