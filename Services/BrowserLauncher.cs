using System;
using System.Collections.Generic;
using System.Linq;

namespace SMDWin.Services
{
    /// <summary>
    /// Opens several addresses at once, as tabs in the default browser.
    /// Sent one by one through the shell, the browser usually kept only the last one (especially when the app runs as
    /// Administrator and the browser does not). Chrome, Edge, Firefox, Brave and Opera receive all addresses in one
    /// command and open one tab for each.
    /// </summary>
    public static class BrowserLauncher
    {
        /// <summary>Returns true if the addresses were sent to the browser in a single command.</summary>
        public static bool TryOpenMany(IReadOnlyList<string> urls)
        {
            if (urls.Count == 0) return true;
            try
            {
                string? exe = DefaultBrowserExe();
                if (string.IsNullOrEmpty(exe) || !System.IO.File.Exists(exe)) return false;
                string name = System.IO.Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (name is not ("chrome" or "msedge" or "firefox" or "brave" or "opera" or "vivaldi")) return false;
                var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
                foreach (var u in urls) psi.ArgumentList.Add(u);
                System.Diagnostics.Process.Start(psi);
                AppLogger.Info($"BrowserLauncher: {urls.Count} addresses sent to {name}");
                return true;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "BrowserLauncher.TryOpenMany"); return false; }
        }

        /// <summary>Executable of the default browser for https (from the user's choice in Windows).</summary>
        private static string? DefaultBrowserExe()
        {
            using var choice = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            string? progId = choice?.GetValue("ProgId") as string;
            if (string.IsNullOrEmpty(progId)) return null;
            using var cmdKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            string? cmd = cmdKey?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            cmd = cmd.Trim();
            // "C:\...\chrome.exe" --single-argument %1  →  C:\...\chrome.exe
            if (cmd.StartsWith("\"")) { int end = cmd.IndexOf('"', 1); return end > 1 ? cmd[1..end] : null; }
            int exeEnd = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exeEnd > 0 ? cmd[..(exeEnd + 4)] : null;
        }
    }
}
