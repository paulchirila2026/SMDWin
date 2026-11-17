using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using SMDWin.Models;

namespace SMDWin.Services
{
    public class StartupManagerService
    {

        /// <summary>Extracts the executable path from a registry/service command, which may have quotes, arguments or the kernel
        /// prefix "\??\" (drivers). Used both to check that it "looks like a real command" and to resolve the Publisher
        /// (FileVersionInfo needs a clean file path).</summary>
        public static string ExtractExePath(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return "";
            string s = command.Trim();
            if (s.StartsWith(@"\??\", StringComparison.Ordinal)) s = s[4..];

            if (s.StartsWith("\""))
            {
                int close = s.IndexOf('"', 1);
                if (close > 1) return s[1..close];
                return "";
            }
            // No quotes — the first "word" up to .exe/.dll/.sys (the path may contain unquoted spaces in old/bad cases, but the
            // extension is a reliable anchor).
            int extIdx = -1;
            foreach (var ext in new[] { ".exe", ".dll", ".sys" })
            {
                int idx = s.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) { extIdx = idx + ext.Length; break; }
            }
            if (extIdx > 0) return s[..extIdx];
            // Simple fallback: up to the first space (arguments)
            int sp = s.IndexOf(' ');
            return sp > 0 ? s[..sp] : s;
        }

        /// <summary>Filters the noise found in generic registry keys (e.g. "LoadAppInit_DLLs"=0, "LeakTrackingAllocatorMode"=1)
        /// that are not real startup commands — just flags/numeric values left in the same key. A real command has a
        /// recognisable executable extension OR a path (contains \ or /).</summary>
        internal static bool LooksLikeRealCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            string s = command.Trim();
            if (s.Length < 3) return false;
            // Pure numeric flags ("0", "1", "2"...) — certainly not a command
            if (s.All(char.IsDigit)) return false;
            bool hasPathSep = s.Contains('\\') || s.Contains('/');
            bool hasExeExt  = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) >= 0
                            || s.IndexOf(".dll", StringComparison.OrdinalIgnoreCase) >= 0
                            || s.IndexOf(".cmd", StringComparison.OrdinalIgnoreCase) >= 0
                            || s.IndexOf(".bat", StringComparison.OrdinalIgnoreCase) >= 0
                            || s.IndexOf(".ps1", StringComparison.OrdinalIgnoreCase) >= 0
                            || s.IndexOf(".msi", StringComparison.OrdinalIgnoreCase) >= 0;
            return hasPathSep || hasExeExt;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _publisherCache = new(StringComparer.OrdinalIgnoreCase);

        private static string ResolvePublisher(string command)
        {
            try
            {
                string exePath = ExtractExePath(command);
                if (string.IsNullOrEmpty(exePath)) return "";
                exePath = Environment.ExpandEnvironmentVariables(exePath);
                if (!File.Exists(exePath)) return "";
                if (_publisherCache.TryGetValue(exePath, out var cached)) return cached;
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
                string pub = info.CompanyName?.Trim() ?? "";
                _publisherCache[exePath] = pub;
                return pub;
            }
            catch { return ""; }
        }

        // ── HKCU Run / RunOnce ────────────────────────────────────────────────
        private static readonly (string key, string location)[] RegistryLocations =
        {
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",      "HKCU — Run"),
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",  "HKCU — RunOnce"),
        };

        // ── HKLM Run / RunOnce (32-bit + 64-bit) ─────────────────────────────
        private static readonly (string key, string location)[] RegistryLocationsHklm =
        {
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",                   "HKLM — Run"),
            (@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",               "HKLM — RunOnce"),
            (@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",       "HKLM — Run (32-bit)"),
            (@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce",   "HKLM — RunOnce (32-bit)"),
        };

        // ── Additional HKLM/HKCU startup locations ───────────────────────────
        private static readonly (string hive, string key, string location)[] ExtraLocations =
        {
            // RunServices (legacy, still used by some apps)
            ("HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunServices",     "HKCU — RunServices"),
            ("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunServices",     "HKLM — RunServices"),
            // Active Setup (runs once per user, used by IE, Office, VS)
            ("HKLM", @"SOFTWARE\Microsoft\Active Setup\Installed Components",      "HKLM — Active Setup"),
            ("HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Active Setup\Installed Components", "HKLM — Active Setup (32-bit)"),
            // Shell ServiceObjects (run as shell extensions at login)
            ("HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\ShellServiceObjectDelayLoad", "HKCU — ShellServiceObject"),
            ("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\ShellServiceObjectDelayLoad", "HKLM — ShellServiceObject"),
            // Explorer Run (older Windows compatibility)
            ("HKCU", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows",     "HKCU — WinLoad"),
            ("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows",     "HKLM — WinLoad"),
            // Winlogon Userinit / Shell (critical system entries)
            ("HKLM", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",    "HKLM — Winlogon"),
        };

        private const string DisabledKeyHkcu =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string DisabledKeyHklm =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string DisabledKeyHklm32 =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";

        /// <summary>Scheduled tasks only (schtasks is slow: ~12 s). Called separately, after the rest of the list is shown.</summary>
        public Task<List<StartupEntry>> GetScheduledTaskEntriesAsync()
        {
            return Task.Run(() =>
            {
                var entries = new List<StartupEntry>();
                ReadFromTaskScheduler(entries, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                System.Threading.Tasks.Parallel.ForEach(entries, e => e.Publisher = ResolvePublisher(e.Command));
                return entries;
            });
        }

        public Task<List<StartupEntry>> GetStartupEntriesAsync(bool includeThirdPartyServices = false, bool includeTasks = true)
        {
            return Task.Run(() =>
            {
                var entries = new List<StartupEntry>();
                var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var sw = System.Diagnostics.Stopwatch.StartNew();

                // The service list (WMI) is requested at the start, in parallel with the rest: it is independent of the other sources
                // and would otherwise add to the time of schtasks, the slowest of them.
                Task<List<WinServiceEntry>>? servicesTask = includeThirdPartyServices
                    ? Task.Run(() => new WinServicesService().GetServicesAsync().GetAwaiter().GetResult())
                    : null;

                // Core registry locations
                ReadFromRegistry(entries, seen, Registry.CurrentUser,  RegistryLocations,     false);
                ReadFromRegistry(entries, seen, Registry.LocalMachine, RegistryLocationsHklm, true);

                // Extended registry locations
                ReadExtraRegistryLocations(entries, seen);

                // Startup folders (User + All Users)
                ReadFromStartupFolders(entries, seen);
                long msRegistry = sw.ElapsedMilliseconds;

                // Task Scheduler — logon / startup triggered tasks
                if (includeTasks) ReadFromTaskScheduler(entries, seen);
                long msTasks = sw.ElapsedMilliseconds - msRegistry;

                entries = entries.Where(e =>
                    e.RegistryHive == "Task" || e.RegistryHive == "Folder" || e.Location.Contains("Active Setup")
                    || LooksLikeRealCommand(e.Command)).ToList();

                long tPub = sw.ElapsedMilliseconds;
                System.Threading.Tasks.Parallel.ForEach(entries, e => e.Publisher = ResolvePublisher(e.Command));
                long msPublishers = sw.ElapsedMilliseconds - tPub;
                long tSvc = sw.ElapsedMilliseconds;

                // THIRD-PARTY (non-Windows) services set to start automatically — only when the "Show services" toggle is on.
                // Microsoft/system services are NEVER included here, whatever their state.
                if (servicesTask != null)
                {
                    try
                    {
                        var services = servicesTask.GetAwaiter().GetResult();
                        foreach (var svc in services)
                        {
                            if (svc.StartType != "Automatic") continue;
                            if (!svc.IsThirdParty) continue;

                            string key = $"service:{svc.Name}";
                            if (!seen.Add(key)) continue;

                            entries.Add(new StartupEntry
                            {
                                Name         = svc.DisplayName,
                                Command      = svc.ImagePath,
                                Location     = "Windows Service",
                                IsEnabled    = svc.Status == "Running",
                                RegistryHive = "Service",
                                RegistryKey  = svc.Name,
                                Publisher    = svc.Publisher,
                            });
                        }
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "GetStartupEntriesAsync: merge servicii terte"); }
                }

                // where the time goes, for slow loads of the Startup page
                if (sw.ElapsedMilliseconds > 1500)
                    AppLogger.Info("Startup entries: {0} ms total — registry+folders {1} ms, scheduled tasks (schtasks) {2} ms, publishers {3} ms, waiting for services {4} ms, {5} entries",
                        sw.ElapsedMilliseconds, msRegistry, msTasks, msPublishers, sw.ElapsedMilliseconds - tSvc, entries.Count);

                return entries.OrderBy(e => e.Location).ThenBy(e => e.Name).ToList();
            });
        }

        // ── Startup Folders ────────────────────────────────────────────────────
        private static void ReadFromStartupFolders(List<StartupEntry> entries, HashSet<string> seen)
        {
            var folders = new[]
            {
                (Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                 "Startup Folder (User)"),
                (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                 "Startup Folder (All Users)"),
            };

            foreach (var (folder, location) in folders)
            {
                if (!Directory.Exists(folder)) continue;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(folder, "*.*")
                             .Where(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                                      || f.EndsWith(".url", StringComparison.OrdinalIgnoreCase)
                                      || f.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                                      || f.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                                      || f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                    {
                        string key = $"folder:{file}";
                        if (!seen.Add(key)) continue;
                        entries.Add(new StartupEntry
                        {
                            Name         = Path.GetFileNameWithoutExtension(file),
                            Command      = file,
                            Location     = location,
                            IsEnabled    = true,
                            RegistryHive = "Folder",
                            RegistryKey  = folder,
                        });
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "StartupManagerService.unknown"); }
            }
        }

        // ── Task Scheduler — logon / startup triggered tasks ──────────────────
        private static void ReadFromTaskScheduler(List<StartupEntry> entries, HashSet<string> seen)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe",
                    "/query /fo CSV /v")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,   // must redirect to prevent blocking
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return;

                // Drain stdout and stderr concurrently — schtasks output can be large (100+ KB).
                // Reading stdout before WaitForExit with un-drained stderr can deadlock.
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                bool exited = proc.WaitForExit(12000);
                if (!exited) { try { proc.Kill(entireProcessTree: true); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); } }
                string output = stdoutTask.GetAwaiter().GetResult();
                stderrTask.GetAwaiter().GetResult(); // discard stderr, just drain

                var lines = output.Split('\n');
                foreach (var raw in lines)
                {
                    try
                    {
                        var cols = SplitCsv(raw);
                        if (cols.Length < 9) continue;

                        string name     = cols[0].Trim();
                        string trig     = (cols.Length > 7  ? cols[7]  : "").Trim();
                        string cmd      = (cols.Length > 8  ? cols[8]  : "").Trim();
                        string status   = (cols.Length > 3  ? cols[3]  : "Ready").Trim();
                        string runAs    = (cols.Length > 29 ? cols[29] : "").Trim();

                        if (name == "TaskName" || string.IsNullOrWhiteSpace(name)) continue;

                        // Only startup / logon triggered tasks
                        bool isStartup = trig.IndexOf("log on",  StringComparison.OrdinalIgnoreCase) >= 0
                                      || trig.IndexOf("startup", StringComparison.OrdinalIgnoreCase) >= 0
                                      || trig.IndexOf("logon",   StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!isStartup) continue;

                        string key = $"task:{name}";
                        if (!seen.Add(key)) continue;

                        // Extract short name
                        string shortName = name.Contains('\\')
                            ? name[(name.LastIndexOf('\\') + 1)..] : name;

                        bool enabled = !status.Equals("Disabled", StringComparison.OrdinalIgnoreCase);

                        // Append run-as info if not SYSTEM/blank
                        string loc = "Task Scheduler";
                        if (!string.IsNullOrEmpty(runAs)
                            && !runAs.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)
                            && !runAs.Equals("NT AUTHORITY\\SYSTEM", StringComparison.OrdinalIgnoreCase))
                            loc += $" ({runAs})";

                        entries.Add(new StartupEntry
                        {
                            Name         = shortName,
                            Command      = cmd,
                            Location     = loc,
                            IsEnabled    = enabled,
                            RegistryHive = "Task",
                            RegistryKey  = name,
                        });
                    }
                    catch (Exception ex) { AppLogger.Warning(ex, "StartupManagerService.unknown"); }
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "StartupManagerService.unknown"); }
        }

        private static string[] SplitCsv(string line)
        {
            var result = new List<string>();
            bool inQ = false;
            var cur = new System.Text.StringBuilder();
            foreach (char c in line)
            {
                if (c == '"') { inQ = !inQ; }
                else if (c == ',' && !inQ) { result.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
            result.Add(cur.ToString());
            return result.ToArray();
        }

        // ── Extra registry locations (Active Setup, ShellServiceObject etc.) ──
        private static void ReadExtraRegistryLocations(List<StartupEntry> entries, HashSet<string> seen)
        {
            foreach (var (hive, keyPath, location) in ExtraLocations)
            {
                try
                {
                    var root = hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                    using var key = root.OpenSubKey(keyPath, writable: false);
                    if (key == null) continue;

                    // Winlogon: look at specific value names only (not all subkeys)
                    if (keyPath.Contains("Winlogon"))
                    {
                        foreach (var valName in new[] { "Userinit", "Shell", "AppSetup" })
                        {
                            var val = key.GetValue(valName)?.ToString()?.Trim();
                            if (string.IsNullOrEmpty(val)) continue;
                            // Skip default system values
                            if (valName == "Userinit" && val.Equals(@"C:\Windows\system32\userinit.exe,", StringComparison.OrdinalIgnoreCase)) continue;
                            if (valName == "Shell"    && val.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                            string k = $"extra:{hive}:{keyPath}:{valName}";
                            if (!seen.Add(k)) continue;
                            entries.Add(new StartupEntry
                            {
                                Name = valName, Command = val, Location = location,
                                IsEnabled = true, RegistryHive = hive, RegistryKey = keyPath,
                            });
                        }
                        continue;
                    }

                    // Active Setup: each subkey has a StubPath value
                    if (keyPath.Contains("Active Setup"))
                    {
                        foreach (var subName in key.GetSubKeyNames())
                        {
                            try
                            {
                                using var sub = key.OpenSubKey(subName);
                                if (sub == null) continue;
                                var stub = sub.GetValue("StubPath")?.ToString()?.Trim();
                                if (string.IsNullOrEmpty(stub)) continue;
                                var displayName = sub.GetValue("")?.ToString()
                                               ?? sub.GetValue("ComponentID")?.ToString()
                                               ?? subName;
                                string k = $"active:{hive}:{subName}";
                                if (!seen.Add(k)) continue;
                                entries.Add(new StartupEntry
                                {
                                    Name = displayName, Command = stub, Location = location,
                                    IsEnabled = true, RegistryHive = hive, RegistryKey = keyPath + "\\" + subName,
                                });
                            }
                            catch (Exception ex) { AppLogger.Warning(ex, "StartupManagerService.unknown"); }
                        }
                        continue;
                    }

                    // Standard name→value entries
                    foreach (var name in key.GetValueNames())
                    {
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        var cmd = key.GetValue(name)?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(cmd)) continue;
                        string k = $"extra:{hive}:{keyPath}:{name}";
                        if (!seen.Add(k)) continue;
                        entries.Add(new StartupEntry
                        {
                            Name = name, Command = cmd, Location = location,
                            IsEnabled = true, RegistryHive = hive, RegistryKey = keyPath,
                        });
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "StartupManagerService.unknown"); }
            }
        }

        // ── Core registry reader ───────────────────────────────────────────────
        private static void ReadFromRegistry(
            List<StartupEntry> entries,
            HashSet<string> seen,
            RegistryKey root,
            (string key, string location)[] locations,
            bool isHklm)
        {
            foreach (var (keyPath, location) in locations)
            {
                try
                {
                    using var key = root.OpenSubKey(keyPath, writable: false);
                    if (key == null) continue;

                    string disabledKeyPath = isHklm
                        ? (keyPath.Contains("WOW6432") ? DisabledKeyHklm32 : DisabledKeyHklm)
                        : DisabledKeyHkcu;

                    foreach (var name in key.GetValueNames())
                    {
                        var cmd = key.GetValue(name)?.ToString() ?? "";
                        bool enabled = IsEnabled(root, disabledKeyPath, name);

                        string k = $"reg:{(isHklm ? "HKLM" : "HKCU")}:{keyPath}:{name}";
                        if (!seen.Add(k)) continue;

                        entries.Add(new StartupEntry
                        {
                            Name         = name,
                            Command      = cmd,
                            Location     = location,
                            IsEnabled    = enabled,
                            RegistryHive = isHklm ? "HKLM" : "HKCU",
                            RegistryKey  = keyPath,
                        });
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "StartupManagerService.unknown"); }
            }
        }

        private static bool IsEnabled(RegistryKey root, string approvedKey, string name)
        {
            try
            {
                using var key = root.OpenSubKey(approvedKey);
                if (key == null) return true;
                var val = key.GetValue(name) as byte[];
                if (val == null || val.Length < 1) return true;
                return val[0] != 0x03;
            }
            catch { return true; }
        }

        public bool SetEnabled(StartupEntry entry, bool enable)
        {
            if (entry.RegistryHive == "Folder")
            {
                try
                {
                    string disabledDir = Path.Combine(entry.RegistryKey, "Disabled");
                    if (!enable)
                    {
                        Directory.CreateDirectory(disabledDir);
                        File.Move(entry.Command, Path.Combine(disabledDir, Path.GetFileName(entry.Command)));
                    }
                    else
                    {
                        string src = Path.Combine(entry.RegistryKey, "Disabled", Path.GetFileName(entry.Command));
                        if (File.Exists(src)) File.Move(src, Path.Combine(entry.RegistryKey, Path.GetFileName(entry.Command)));
                    }
                    return true;
                }
                catch { return false; }
            }

            if (entry.RegistryHive == "Task")
            {
                try
                {
                    string arg = enable ? $"/change /tn \"{entry.RegistryKey}\" /enable"
                                       : $"/change /tn \"{entry.RegistryKey}\" /disable";
                    var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "schtasks.exe", arg) { UseShellExecute = false, CreateNoWindow = true });
                    p?.WaitForExit(3000);
                    return true;
                }
                catch { return false; }
            }

            if (entry.RegistryHive == "Service")
            {
                if (WinServicesService.IsCritical(entry.RegistryKey)) return false; // never disabled automatically
                try
                {
                    var svc = new WinServicesService();
                    if (!enable)
                    {
                        // remember the REAL state before disabling (it may be "Manual" or "Automatic (Delayed Start)", not just
                        // "Automatic"/"Disabled").
                        var (curType, curDelayed) = WinServicesService.GetStartTypeAndDelayed(entry.RegistryKey);
                        if (curType is "Automatic" or "Manual")
                            StartupOriginalStateStore.RecordBeforeDisable(entry.RegistryKey, curType, curDelayed);
                        return svc.SetServiceStartTypeAsync(entry.RegistryKey, "Disabled")
                            .GetAwaiter().GetResult();
                    }
                    else
                    {
                        // restore the remembered original state, not a default "Automatic".
                        var original = StartupOriginalStateStore.GetOriginal(entry.RegistryKey);
                        string targetType = original?.startType ?? "Automatic";
                        bool targetDelayed = original?.delayed ?? false;
                        return svc.SetServiceStartTypeAsync(entry.RegistryKey, targetType, targetDelayed)
                            .GetAwaiter().GetResult();
                    }
                }
                catch { return false; }
            }

            try
            {
                var root         = entry.RegistryHive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                var approvedPath = entry.RegistryHive == "HKLM" ? DisabledKeyHklm : DisabledKeyHkcu;

                using var key = root.CreateSubKey(approvedPath, writable: true);
                if (key == null) return false;

                var existing = key.GetValue(entry.Name) as byte[] ?? new byte[12];
                if (existing.Length < 12) { var tmp = new byte[12]; existing.CopyTo(tmp, 0); existing = tmp; }

                existing[0] = enable ? (byte)0x02 : (byte)0x03;
                key.SetValue(entry.Name, existing, RegistryValueKind.Binary);
                return true;
            }
            catch { return false; }
        }

        public bool Remove(StartupEntry entry)
        {
            if (entry.RegistryHive == "Folder")
            {
                try
                {
                    // No irreversible delete: the shortcut is moved to a backup folder next to the app data
                    // (a wrong click on "Remove" no longer loses the entry).
                    string backupDir = AppPaths.Data("Startup-Removed");
                    Directory.CreateDirectory(backupDir);
                    string dest = Path.Combine(backupDir, Path.GetFileName(entry.Command));
                    if (File.Exists(dest))
                        dest = Path.Combine(backupDir, $"{Path.GetFileNameWithoutExtension(entry.Command)}_{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(entry.Command)}");
                    File.Move(entry.Command, dest);
                    AppLogger.Info("StartupManagerService.Remove: shortcut moved to {0} (not deleted for good)", dest);
                    return true;
                }
                catch { return false; }
            }
            if (entry.RegistryHive == "Task") return false; // don't auto-delete scheduled tasks
            if (entry.RegistryHive == "Service") return false; // never sc delete from a "Remove entry" button

            try
            {
                var root = entry.RegistryHive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                using var key = root.OpenSubKey(entry.RegistryKey, writable: true);
                // backup: the deleted value is kept in a file, so it can be restored
                try
                {
                    var val = key?.GetValue(entry.Name);
                    if (val != null)
                    {
                        string backupDir = AppPaths.Data("Startup-Removed");
                        Directory.CreateDirectory(backupDir);
                        File.AppendAllText(Path.Combine(backupDir, "registry-removed.txt"),
                            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{entry.RegistryHive}\\{entry.RegistryKey}\t{entry.Name}\t{val}{Environment.NewLine}");
                    }
                }
                catch (Exception ex) { AppLogger.Warning(ex, "StartupManagerService.Remove backup"); }
                key?.DeleteValue(entry.Name, throwOnMissingValue: false);
                return true;
            }
            catch { return false; }
        }
    }
}
