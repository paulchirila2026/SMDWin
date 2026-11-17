using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SMDWin.Services
{
    /// <summary>
    /// What can be found out and done with an installed program from its registry entry (Applications page):
    /// icon file, modify / repair command, path for Registry Editor, prefetch traces, clipboard text.
    /// Text processing only — no registry or disk access.
    /// </summary>
    public static class AppCommands
    {
        /// <summary>
        /// The file from the DisplayIcon value: “"C:\App\app.exe",0” → "C:\App\app.exe"
        /// (no quotes, no icon index, environment variables expanded).
        /// </summary>
        public static string IconFilePath(string? displayIcon)
        {
            string path = Environment.ExpandEnvironmentVariables(displayIcon ?? "").Trim().Trim('"');
            int comma = path.LastIndexOf(',');
            if (comma > 2 && int.TryParse(path[(comma + 1)..].Trim(), out _)) path = path[..comma].Trim().Trim('"');
            return path;
        }

        private static readonly Regex RxGuid = new(@"^\{[0-9A-Fa-f]{8}-([0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}\}$", RegexOptions.Compiled);

        /// <summary>
        /// Command that opens the program's "Change / Repair" window, or empty if the program offers none.
        /// First the ModifyPath declared by the program; for Windows Installer (MSI) packages the maintenance window
        /// opens with "msiexec /I {code}", unless the program disallows both modify and repair.
        /// </summary>
        public static string ModifyCommand(string? modifyPath, bool windowsInstaller, string? subKeyName, bool noModify, bool noRepair)
        {
            if (!string.IsNullOrWhiteSpace(modifyPath)) return modifyPath.Trim();
            if (windowsInstaller && !(noModify && noRepair) && subKeyName != null && RxGuid.IsMatch(subKeyName))
                return $"MsiExec.exe /I{subKeyName}";
            return "";
        }

        private static readonly Regex RxMsi = new(@"^\s*""?(?:[^""]*\\)?msiexec(?:\.exe)?""?\s*(.*)$",
                                                  RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>
        /// Arguments of a Windows Installer command ("MsiExec.exe /X{code}" → "/X{code}"), however the program name
        /// is written (case, full path, quotes). null if it is not an msiexec command.
        /// </summary>
        public static string? MsiArguments(string? command)
        {
            var m = RxMsi.Match(command ?? "");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        /// <summary>The full path of the program's key, as it appears in Registry Editor.</summary>
        public static string RegistryPath(string hive, string uninstallKeyPath, string subKeyName)
            => $"{(hive == "HKCU" ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE")}\\{uninstallKeyPath.Trim('\\')}\\{subKeyName}";

        /// <summary>
        /// Does the folder belong to this program only? Some programs declare a shared folder as their install location
        /// (C:\, Program Files, Windows…); the size and processes of such a folder are not the program's.
        /// </summary>
        public static bool IsOwnFolder(string? folder, IEnumerable<string> sharedFolders)
        {
            string f = Normalize(folder);
            if (f.Length <= 3) return false;                       // empty or the root of a drive ("C:\")
            foreach (var shared in sharedFolders)
            {
                string s = Normalize(shared);
                if (s.Length == 0) continue;
                // the shared folder itself or one of its parents
                if (s.Equals(f, StringComparison.OrdinalIgnoreCase) || s.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        private static string Normalize(string? path) => (path ?? "").Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');

        /// <summary>true if <paramref name="path"/> is inside the folder <paramref name="folder"/>.</summary>
        public static bool IsInside(string? path, string? folder)
        {
            string p = Normalize(path), f = Normalize(folder);
            return f.Length > 0 && p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
        }

        // Windows keeps at most 29 characters of the program name in the prefetch file name
        private const int PrefetchNameMax = 29;
        private static readonly Regex RxPrefetchTail = new(@"^-[0-9A-F]{8}\.PF$", RegexOptions.Compiled);

        /// <summary>The start of a program's prefetch file name: "chrome.exe" → "CHROME.EXE".</summary>
        public static string PrefetchName(string exeFileName)
        {
            string n = Path.GetFileName(exeFileName.Replace('\\', '/')).ToUpperInvariant();
            return n.Length > PrefetchNameMax ? n[..PrefetchNameMax] : n;
        }

        /// <summary>Does the prefetch file (e.g. "CHROME.EXE-D999B1BA.pf") belong to the given program?</summary>
        public static bool IsPrefetchOf(string prefetchFileName, string exeFileName)
        {
            string pf = Path.GetFileName(prefetchFileName.Replace('\\', '/')).ToUpperInvariant();
            string name = PrefetchName(exeFileName);
            return name.Length > 0 && pf.StartsWith(name, StringComparison.Ordinal) && RxPrefetchTail.IsMatch(pf[name.Length..]);
        }

        // Helper programs with names shared by many applications: their prefetch trace says nothing about the application
        private static readonly string[] GenericExeNames =
        {
            "setup", "install", "installer", "uninstall", "uninst", "unins000", "update", "updater", "helper",
            "launcher", "crashpad_handler", "crashreporter", "notification_helper", "elevation_service",
        };

        public static bool IsGenericExeName(string exeFileName)
        {
            string n = Path.GetFileNameWithoutExtension(exeFileName.Replace('\\', '/')).ToLowerInvariant();
            return GenericExeNames.Contains(n);
        }

        /// <summary>
        /// The main program among those in the application folder: the one whose name resembles the application name
        /// ("Mozilla Firefox" → firefox.exe); if none, the first one that is not a generic helper.
        /// </summary>
        public static string PickMainExe(string appName, IEnumerable<string> exeFileNames)
        {
            string squashed = Regex.Replace(appName.ToLowerInvariant(), @"[^a-z0-9]", "");
            var names = exeFileNames.Where(n => !IsGenericExeName(n)).ToList();
            var byName = names
                .Select(n => (Name: n, Key: Regex.Replace(Path.GetFileNameWithoutExtension(n.Replace('\\', '/')).ToLowerInvariant(), @"[^a-z0-9]", "")))
                .Where(x => x.Key.Length >= 3 && squashed.Contains(x.Key))
                .OrderByDescending(x => x.Key.Length)
                .Select(x => x.Name)
                .FirstOrDefault();
            return byName ?? names.FirstOrDefault() ?? "";
        }

        /// <summary>Program details as text to pass on (one line per item; missing items are skipped).</summary>
        public static string ClipboardText(string name, string version, string publisher, string installDate,
                                           string location, string registryPath, string uninstallCommand)
        {
            var lines = new List<string> { name.Trim() };
            void Add(string label, string value)
            {
                if (!string.IsNullOrWhiteSpace(value) && value.Trim() != "—") lines.Add($"{label}: {value.Trim()}");
            }
            Add("Version", version);
            Add("Publisher", publisher);
            Add("Installed", installDate);
            Add("Location", location);
            Add("Registry", registryPath);
            Add("Uninstall", uninstallCommand);
            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>"today", "yesterday", "5 days ago", "3 months ago", "2 years ago" — in English; the page shows it as is.</summary>
        public static string Ago(DateTime when, DateTime now)
        {
            double days = (now.Date - when.Date).TotalDays;
            if (days <= 0) return "today";
            if (days < 2) return "yesterday";
            if (days < 60) return $"{(int)days} days ago";
            if (days < 730) return $"{(int)(days / 30)} months ago";
            return $"{(int)(days / 365)} years ago";
        }
    }
}
