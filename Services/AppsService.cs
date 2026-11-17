using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Win32;
using SMDWin.Models;

namespace SMDWin.Services
{
    public class AppsService
    {
        public async Task<List<InstalledApp>> GetInstalledAppsAsync()
        {
            return await Task.Run(() =>
            {
                var results = new List<InstalledApp>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var keys = new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                };

                var roots = new[] { Registry.LocalMachine, Registry.CurrentUser };

                foreach (var root in roots)
                {
                    foreach (var keyPath in keys)
                    {
                        try
                        {
                            using var key = root.OpenSubKey(keyPath);
                            if (key == null) continue;

                            foreach (var subName in key.GetSubKeyNames())
                            {
                                try
                                {
                                    using var sub = key.OpenSubKey(subName);
                                    if (sub == null) continue;

                                    var name = sub.GetValue("DisplayName")?.ToString();
                                    if (string.IsNullOrWhiteSpace(name)) continue;
                                    if (!seen.Add(name)) continue;

                                    // Skip Windows components / Updates
                                    var sysComp = sub.GetValue("SystemComponent")?.ToString();
                                    if (sysComp == "1") continue;

                                    var releaseType = sub.GetValue("ReleaseType")?.ToString() ?? "";
                                    if (releaseType.Contains("Update") || releaseType.Contains("Hotfix")) continue;

                                    var publisher = sub.GetValue("Publisher")?.ToString() ?? "";
                                    var version   = CleanVersionPrefix(sub.GetValue("DisplayVersion")?.ToString() ?? "");
                                    var rawDate   = sub.GetValue("InstallDate")?.ToString();
                                    var date      = FormatDate(rawDate);
                                    DateTime? dateRaw = null;
                                    if (!string.IsNullOrEmpty(rawDate) && rawDate.Length >= 8
                                        && int.TryParse(rawDate.Substring(0, 4), out int yr)
                                        && int.TryParse(rawDate.Substring(4, 2), out int mo)
                                        && int.TryParse(rawDate.Substring(6, 2), out int dy))
                                    {
                                        try { dateRaw = new DateTime(yr, mo, dy); } catch { dateRaw = null; }
                                    }
                                    var uninstall = sub.GetValue("UninstallString")?.ToString() ?? "";
                                    var sizeKb    = sub.GetValue("EstimatedSize");
                                    long sizeBytesRaw = 0;
                                    string sizeStr = "—";
                                    if (sizeKb != null)
                                    {
                                        try
                                        {
                                            sizeBytesRaw = Convert.ToInt64(sizeKb) * 1024;
                                            sizeStr = FormatSize(sizeBytesRaw);
                                        }
                                        catch { sizeBytesRaw = 0; sizeStr = "—"; }
                                    }

                                    var installLocation = sub.GetValue("InstallLocation")?.ToString()?.Trim().Trim('"') ?? "";
                                    if (dateRaw == null)
                                    {
                                        var installLoc = installLocation;
                                        if (!string.IsNullOrWhiteSpace(installLoc) && System.IO.Directory.Exists(installLoc))
                                        {
                                            try
                                            {
                                                var created = System.IO.Directory.GetCreationTime(installLoc);
                                                if (created.Year > 1980) // filters placeholder/corrupt timestamps
                                                {
                                                    dateRaw = created;
                                                    date = DateFormatHelper.FormatDate(created);
                                                }
                                            }
                                            catch (Exception ex) { AppLogger.Debug($"AppsService InstallLocation fallback: {ex.Message}"); }
                                        }
                                    }

                                    results.Add(new InstalledApp
                                    {
                                        Name           = name,
                                        Version        = version,
                                        Publisher      = publisher,
                                        InstallDate    = date,
                                        InstallDateRaw = dateRaw,
                                        UninstallKey   = uninstall,
                                        Size           = sizeStr,
                                        SizeBytes      = sizeBytesRaw,
                                        IconSource     = sub.GetValue("DisplayIcon")?.ToString() ?? "",
                                        InstallLocation = installLocation,
                                        Hive           = root == Registry.LocalMachine ? "HKLM" : "HKCU",
                                        RegistryPath   = AppCommands.RegistryPath(root == Registry.LocalMachine ? "HKLM" : "HKCU", keyPath, subName),
                                        ModifyCommand  = AppCommands.ModifyCommand(
                                            sub.GetValue("ModifyPath")?.ToString(),
                                            sub.GetValue("WindowsInstaller")?.ToString() == "1", subName,
                                            sub.GetValue("NoModify")?.ToString() == "1",
                                            sub.GetValue("NoRepair")?.ToString() == "1"),
                                    });
                                }
                                catch (Exception ex) { AppLogger.Warning(ex, "AppsService.unknown"); }
                            }
                        }
                        catch (Exception ex) { AppLogger.Warning(ex, "AppsService.unknown"); }
                    }
                }

                results.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                return results;
            });
        }

        public void Uninstall(InstalledApp app)
        {
            if (string.IsNullOrEmpty(app.UninstallKey))
                throw new Exception("No uninstall command found for this application.");
            Launch(app.UninstallKey);
        }

        /// <summary>Opens the program's "Change / Repair" window (its installer's).</summary>
        public void Modify(InstalledApp app)
        {
            if (string.IsNullOrEmpty(app.ModifyCommand))
                throw new Exception("This application does not offer a change or repair command.");
            Launch(app.ModifyCommand);
        }

        /// <summary>Runs a command written by an installer in the registry (uninstall, modify).</summary>
        private static void Launch(string cmd)
        {
            // Windows Installer package: msiexec gets only the arguments (the program name removed however it is written)
            if (AppCommands.MsiArguments(cmd) is string msiArgs)
            {
                Process.Start(new ProcessStartInfo("msiexec.exe", msiArgs)
                {
                    UseShellExecute = true
                });
            }
            else
            {
                var (file, args) = SplitCommand(cmd);
                try
                {
                    Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    AppLogger.Warning(ex, "AppsService.Uninstall: direct execution failed, falling back to cmd.exe (last resort)");
                    var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = true, CreateNoWindow = true };
                    psi.ArgumentList.Add("/c");
                    psi.ArgumentList.Add(cmd);
                    Process.Start(psi);
                }
            }
        }

        private static (string FileName, string Arguments) SplitCommand(string cmd)
        {
            cmd = cmd.Trim();
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                if (end > 0)
                {
                    string file = cmd.Substring(1, end - 1);
                    string args = cmd.Length > end + 1 ? cmd.Substring(end + 1).Trim() : "";
                    return (file, args);
                }
            }
            int sp = cmd.IndexOf(' ');
            if (sp < 0) return (cmd, "");
            return (cmd.Substring(0, sp), cmd.Substring(sp + 1).Trim());
        }

        private static string FormatDate(string? raw) => SMDWin.Services.DateFormatHelper.FormatCompactDate(raw);

        private static readonly System.Text.RegularExpressions.Regex _versionPrefixRx =
            new(@"^[a-z]{2,4}\s+(?=\d)", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static string CleanVersionPrefix(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return version;
            return _versionPrefixRx.Replace(version, "");
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "—";
            if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
            if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F0} MB";
            return $"{bytes / 1024} KB";
        }
    }
}
