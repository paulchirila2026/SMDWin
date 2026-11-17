using System;

namespace SMDWin.Services
{
    /// <summary>
    /// When SMDWin runs as Administrator under a DIFFERENT account than the one logged on interactively
    /// ("Run as different user", common in repair shops and schools where the technician uses a separate
    /// admin account), the HKCU that Startup Manager writes to belongs to the ADMIN account, not the real user.
    /// The real user's startup entries do not appear in the list and are not changed, with no visible error.
    ///
    /// This cannot be fixed technically (it would require loading the other account's NTUSER.DAT hive),
    /// but the risk can be made visible instead of silent.
    /// </summary>
    public static class AccountMismatchDetector
    {
        private static bool? _cached;

        /// <summary>True if the current process runs under a different Windows account than the one that owns
        /// the interactive session (the Explorer shell). Cached — it does not change while the app runs.</summary>
        public static bool IsRunningAsDifferentUser()
        {
            if (_cached.HasValue) return _cached.Value;
            _cached = Compute();
            return _cached.Value;
        }

        private static bool Compute()
        {
            try
            {
                string currentUser = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                string? shellUser = GetInteractiveShellOwner();
                if (string.IsNullOrEmpty(shellUser)) return false; // cannot tell — assume OK
                return !string.Equals(currentUser, shellUser, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) { AppLogger.Debug("AccountMismatchDetector: " + ex.Message); return false; }
        }

        /// <summary>Owner of explorer.exe in the current session (the user logged on at the console), as
        /// DOMAIN\User — or null if it cannot be determined (no graphical session, WMI unavailable, etc.).</summary>
        private static string? GetInteractiveShellOwner()
        {
            try
            {
                int sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
                using var searcher = SMDWin.Services.WmiHelper.Searcher(
                    "SELECT ProcessId, SessionId FROM Win32_Process WHERE Name='explorer.exe'", System.TimeSpan.FromSeconds(10));
                using (var __wmiTmp1 = searcher.Get())
                {
                    foreach (System.Management.ManagementObject mo in __wmiTmp1)
                {
                    try
                    {
                        var sid = Convert.ToInt32(mo["SessionId"]);
                        if (sid != sessionId) continue;
                        var outParams = mo.InvokeMethod("GetOwner", null, null) as System.Management.ManagementBaseObject;
                        string? domain = outParams?["Domain"] as string;
                        string? user   = outParams?["User"] as string;
                        if (!string.IsNullOrEmpty(user))
                            return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
                    }
                    catch (Exception ex) { AppLogger.Debug("AccountMismatchDetector.GetOwner: " + ex.Message); }
                }
                }
            }
            catch (Exception ex) { AppLogger.Debug("AccountMismatchDetector.Explorer lookup: " + ex.Message); }
            return null;
        }
    }
}
