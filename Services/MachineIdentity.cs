using System;

namespace SMDWin.Services
{
    /// <summary>
    /// Identity of this Windows: computer name + MachineGuid (unique per Windows installation).
    /// Used by the cache files in Data\cache: with the app on a stick moved between computers, data saved on another PC
    /// is not shown.
    /// </summary>
    public static class MachineIdentity
    {
        private static string? _current;

        public static string Current => _current ??= Read();

        private static string Read()
        {
            string guid = "";
            try
            {
                using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                guid = key?.GetValue("MachineGuid") as string ?? "";
            }
            catch (Exception ex) { AppLogger.Debug($"MachineIdentity: {ex.Message}"); }
            return $"{Environment.MachineName}|{guid}";
        }

        public static bool IsThisMachine(string? id) => !string.IsNullOrEmpty(id) && string.Equals(id, Current, StringComparison.OrdinalIgnoreCase);
    }
}
