using System;
using System.Collections.Generic;

namespace SMDWin.Services
{
    /// <summary>
    /// Plain-language explanations for the most common Windows events (Event Viewer).
    /// Key = a fragment of the source (case-insensitive) + Event ID.
    /// Purpose: the technician sees at once whether an event matters or is harmless noise.
    /// </summary>
    public static class EventExplainer
    {
        public enum Weight { Harmless, Info, Attention, Serious }

        private sealed record Rule(string SourcePart, long Id, string Text, Weight W);

        private static readonly List<Rule> Rules = new()
        {
            // Opriri / pornire
            new("Kernel-Power", 41,  "Unexpected shutdown — power loss, a crash or a forced power-off (holding the power button).", Weight.Serious),
            new("EventLog", 6008,    "The previous shutdown was unexpected.", Weight.Attention),
            new("EventLog", 6005,    "Windows started (event log service started).", Weight.Info),
            new("EventLog", 6006,    "Windows shut down normally.", Weight.Info),
            new("Kernel-General", 12,"Windows started.", Weight.Info),
            new("Kernel-General", 13,"Windows shut down.", Weight.Info),
            new("BugCheck", 1001,    "The system crashed with a blue screen (BSOD). Details on the BSOD / Crash page.", Weight.Serious),
            new("WER-SystemErrorReporting", 1001, "The system crashed with a blue screen (BSOD). Details on the BSOD / Crash page.", Weight.Serious),
            new("volmgr", 161,       "Windows could not save the crash dump (memory dump) — often a too-small page file.", Weight.Attention),

            // Hardware
            new("WHEA-Logger", 17,   "A hardware error was detected and corrected (often PCIe / graphics card link). Occasional ones are harmless.", Weight.Attention),
            new("WHEA-Logger", 18,   "Fatal hardware error (CPU / memory / bus). Usually causes a crash — check temperatures, RAM and overclock.", Weight.Serious),
            new("WHEA-Logger", 19,   "A corrected hardware error in the CPU / cache.", Weight.Attention),
            new("WHEA-Logger", 47,   "A corrected memory (RAM) error.", Weight.Attention),
            new("Kernel-Processor-Power", 37, "The CPU speed was limited by the firmware — usually overheating or a power limit (throttling).", Weight.Attention),
            new("disk", 7,           "Bad block on the disk — the drive could not read a sector. Check disk health (SMART / Surface Scan).", Weight.Serious),
            new("disk", 11,          "The disk controller reported an error — often a faulty cable or port.", Weight.Serious),
            new("disk", 51,          "An error occurred while writing to the disk (paging operation).", Weight.Attention),
            new("disk", 153,         "A disk operation had to be retried — early sign of a slow or failing drive or cable.", Weight.Attention),
            new("Ntfs", 55,          "The file system is corrupted — run chkdsk /f.", Weight.Serious),
            new("Ntfs", 98,          "The volume needs a check (chkdsk) — possible file system damage.", Weight.Attention),
            new("Display", 4101,     "The display driver stopped responding and recovered (screen flicker / black screen for a moment).", Weight.Attention),
            new("nvlddmkm", 13,      "NVIDIA driver error — usually driver, overheating or unstable GPU overclock.", Weight.Attention),
            new("nvlddmkm", 14,      "NVIDIA driver error — usually driver, overheating or unstable GPU overclock.", Weight.Attention),
            new("nvlddmkm", 153,     "NVIDIA driver was reset after it stopped responding.", Weight.Attention),
            new("amdkmdag", 4101,    "AMD display driver stopped responding and recovered.", Weight.Attention),
            new("Kernel-PnP", 219,   "A driver for a device failed to load (device may not work).", Weight.Attention),
            new("Kernel-PnP", 411,   "A device has a problem starting (driver or hardware issue).", Weight.Attention),

            // Services
            new("Service Control Manager", 7000, "A service failed to start.", Weight.Attention),
            new("Service Control Manager", 7001, "A service could not start because a service it depends on failed.", Weight.Attention),
            new("Service Control Manager", 7009, "A service took too long to start (timeout) — often slow boot or a busy disk.", Weight.Attention),
            new("Service Control Manager", 7011, "A service did not respond in time.", Weight.Attention),
            new("Service Control Manager", 7023, "A service stopped with an error.", Weight.Attention),
            new("Service Control Manager", 7031, "A service crashed and Windows restarted it.", Weight.Attention),
            new("Service Control Manager", 7034, "A service crashed unexpectedly.", Weight.Attention),
            new("Service Control Manager", 7040, "A service's startup type was changed (normal — Windows and updates do this often).", Weight.Harmless),
            new("Service Control Manager", 7036, "A service started or stopped (normal activity).", Weight.Harmless),

            // Applications
            new("Application Error", 1000, "An application crashed.", Weight.Attention),
            new("Application Hang", 1002,  "An application stopped responding and was closed.", Weight.Attention),
            new("Windows Error Reporting", 1001, "A crash report was saved (follows an application or system error).", Weight.Info),
            new(".NET Runtime", 1026,      "A .NET application crashed.", Weight.Attention),
            new("SideBySide", 33,          "An application could not start — a Visual C++ runtime is missing or damaged.", Weight.Attention),

            // Zgomot cunoscut
            new("DistributedCOM", 10016, "Harmless permission warning, present on almost every Windows PC — can be ignored.", Weight.Harmless),
            new("DistributedCOM", 10010, "A background component did not register in time — usually harmless.", Weight.Harmless),
            new("Security-SPP", 16384,  "Windows license service scheduled a check (normal).", Weight.Harmless),
            new("Security-SPP", 16394,  "Windows license service finished a check (normal).", Weight.Harmless),
            new("VSS", 8224,            "Shadow copy service stopped because it was idle (normal).", Weight.Harmless),
            new("RestartManager", 10000,"An installer started a restart session (normal during installs/updates).", Weight.Harmless),
            new("RestartManager", 10001,"An installer finished a restart session (normal).", Weight.Harmless),
            new("HttpService", 113,     "Network service registered a URL (normal background activity).", Weight.Harmless),
            new("HttpService", 114,     "Network service released a URL (normal background activity).", Weight.Harmless),
            new("User Profile Service", 1534, "Profile notification issue — usually harmless.", Weight.Harmless),

            // Retea / timp / update / securitate
            new("Tcpip", 4199,          "IP address conflict — another device on the network has the same address.", Weight.Attention),
            new("DNS Client Events", 1014, "Name resolution timed out — slow or unreachable DNS server.", Weight.Attention),
            new("Time-Service", 129,    "Could not reach the time server — clock may drift.", Weight.Attention),
            new("Time-Service", 134,    "Could not reach the time server — clock may drift.", Weight.Attention),
            new("WindowsUpdateClient", 20, "A Windows update failed to install.", Weight.Attention),
            new("WindowsUpdateClient", 19, "A Windows update was installed.", Weight.Info),
            new("Windows Defender", 1116, "Microsoft Defender detected a threat.", Weight.Serious),
            new("Windows Defender", 1117, "Microsoft Defender took action against a threat.", Weight.Attention),
            new("Winlogon", 6003,        "Winlogon notification (normal).", Weight.Harmless),
        };

        /// <summary>Explanation for (source, id), or null if unknown.</summary>
        public static (string text, Weight weight)? Explain(string source, long id)
        {
            if (string.IsNullOrEmpty(source)) return null;
            foreach (var r in Rules)
                if (r.Id == id && source.Contains(r.SourcePart, StringComparison.OrdinalIgnoreCase))
                    return (r.Text, r.W);
            return null;
        }
    }
}
