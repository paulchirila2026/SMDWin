using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SMDWin.Models;

namespace SMDWin.Services
{
    public class CrashService
    {
        // Windows writes dumps where CrashControl in the registry says (default %SystemRoot%\Minidump and
        // %SystemRoot%\MEMORY.DMP); Windows may be installed on a drive other than C:.
        private static readonly string MiniDumpPath = ReadCrashControlPath("MinidumpDir", "Minidump");
        private static readonly string MemoryDumpPath = ReadCrashControlPath("DumpFile", "MEMORY.DMP");

        /// <summary>Folder where Windows saves minidumps on this system.</summary>
        public string MinidumpFolder => MiniDumpPath;

        private static string ReadCrashControlPath(string valueName, string defaultName)
        {
            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), defaultName);
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CrashControl", writable: false);
                string? v = key?.GetValue(valueName) as string;   // REG_EXPAND_SZ: %SystemRoot% already comes expanded
                if (!string.IsNullOrWhiteSpace(v) && Path.IsPathRooted(v)) return v;
            }
            catch (Exception ex) { AppLogger.Debug($"CrashService.ReadCrashControlPath({valueName}): {ex.Message}"); }
            return fallback;
        }

        private static string L(string key, string fallback) => LanguageService.S("Crash", key, fallback);

        public async Task<List<CrashEntry>> GetCrashesAsync()
        {
            return await Task.Run(() =>
            {
                var results = new List<CrashEntry>();

                // Minidumps
                if (Directory.Exists(MiniDumpPath))
                {
                    try
                    {
                        foreach (var file in Directory.GetFiles(MiniDumpPath, "*.dmp"))
                        {
                            var info = new FileInfo(file);
                            var entry = new CrashEntry
                            {
                                CrashTime = info.CreationTime,
                                FileName = info.Name,
                                FilePath = file,
                                StopCode = ExtractStopCodeFromName(info.Name),
                                FaultingModule = L("AnalyzeWithWinDbgForDetails", "Analyze with WinDbg for details")
                            };

                            KernelDumpInfo? kd = null;
                            try { kd = KernelDumpParser.TryParse(file); }
                            catch (Exception kex)
                            {
                                AppLogger.Debug("KernelDumpParser: " + kex.Message);
                                entry.FaultingModule = kex is UnauthorizedAccessException
                                    ? L("AccessDeniedNeedsAdmin", "Access denied — needs Administrator")
                                    : string.Format(L("CannotReadDump", "Could not read the .dmp file ({0})"), kex.GetType().Name);
                            }
                            if (kd != null)
                            {
                                entry.KernelInfo     = kd;
                                entry.StopCode       = string.IsNullOrEmpty(kd.BugCheckName)
                                                     ? kd.CodeHex
                                                     : $"{kd.CodeHex} {kd.BugCheckName}";
                                entry.BugcheckParams = kd.ParamsHex;
                                entry.ModuleCount    = kd.Drivers.Count;
                                if (kd.SystemTimeUtc.HasValue)
                                    entry.CrashTime = kd.SystemTimeUtc.Value.ToLocalTime();
                                if (kd.CulpritName != null)
                                {
                                    string cat = MiniDumpParser.CategorizeDriver(kd.CulpritName);
                                    entry.FaultingModule = kd.CulpritIsGeneric
                                        ? string.Format(LanguageService.S("Crash", "GenericCulprit", "{0} (generic — the real cause is likely a different driver)"), kd.CulpritName)
                                        : $"{kd.CulpritName} ({cat})";
                                    entry.FaultingModuleCategory = cat;
                                    entry.FaultingModuleKind = MiniDumpParser.GetDriverKind(kd.CulpritName).ToString();
                                }
                                else
                                {
                                    entry.FaultingModule = kd.Drivers.Count > 0
                                        ? string.Format(LanguageService.S("Crash", "DoubleClickDrivers", "{0} drivers in dump — double-click for details"), kd.Drivers.Count)
                                        : LanguageService.S("Crash", "CodeParamsFromHeader", "Code + parameters read from header — double-click for details");
                                }
                                results.Add(entry);
                                continue; // the header is authoritative — no need for Event Log / MDMP
                            }

                            // Fallback: Event Log (event 1001 contains the code in its text)
                            var stopCode = TryGetStopCodeFromEventLog(info.CreationTime, out var bugcheckParams);
                            if (!string.IsNullOrEmpty(stopCode))
                            {
                                entry.StopCode = stopCode;
                                entry.BugcheckParams = bugcheckParams;
                            }
                            try
                            {
                                var modules = MiniDumpParser.GetModules(file);
                                entry.ModuleCount = modules.Count;
                                var guessed = MiniDumpParser.GuessFaultingModule(modules, bugcheckParams);
                                if (guessed != null)
                                {
                                    entry.FaultingModule = $"{guessed.Name} ({guessed.Category})";
                                    entry.FaultingModuleCategory = guessed.Category;
                                    entry.FaultingModuleKind = guessed.Kind.ToString();
                                }
                                else
                                {
                                    entry.FaultingModule = modules.Count > 0
                                        ? string.Format(L("DoubleClickDrivers", "{0} drivers in dump — double-click for details"), modules.Count)
                                        : L("UnknownModule", "Unknown");
                                }
                            }
                            catch (Exception mdEx)
                            {
                                AppLogger.Debug("MiniDumpParser: " + mdEx.Message);
                                entry.FaultingModule = mdEx is UnauthorizedAccessException
                                    ? L("AccessDeniedNeedsAdmin", "Access denied — needs Administrator")
                                    : string.Format(L("CannotReadDump", "Could not read the .dmp file ({0})"), mdEx.GetType().Name);
                            }

                            results.Add(entry);
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        results.Add(new CrashEntry
                        {
                            CrashTime = DateTime.Now,
                            FileName = L("AccessDeniedFile", "[Access denied]"),
                            StopCode = "Run as Administrator",
                            FaultingModule = string.Format(L("NoPermissionForFolder", "Not enough permissions for {0}"), MiniDumpPath)
                        });
                    }
                }

                // Full memory dump
                if (File.Exists(MemoryDumpPath))
                {
                    var fi = new FileInfo(MemoryDumpPath);
                    var memEntry = new CrashEntry
                    {
                        CrashTime = fi.LastWriteTime,
                        FileName = "MEMORY.DMP",
                        FilePath = MemoryDumpPath,
                        StopCode = "Full Dump",
                        FaultingModule = string.Format(L("DumpSize", "Size: {0} MB"), fi.Length / (1024 * 1024))
                    };
                    try
                    {
                        var kd = KernelDumpParser.TryParse(MemoryDumpPath);
                        if (kd != null)
                        {
                            memEntry.KernelInfo     = kd;
                            memEntry.StopCode       = string.IsNullOrEmpty(kd.BugCheckName)
                                                    ? kd.CodeHex
                                                    : $"{kd.CodeHex} {kd.BugCheckName}";
                            memEntry.BugcheckParams = kd.ParamsHex;
                            if (kd.SystemTimeUtc.HasValue)
                                memEntry.CrashTime = kd.SystemTimeUtc.Value.ToLocalTime();
                            memEntry.FaultingModule = string.Format(LanguageService.S("Crash", "DumpDetails", "{0} · {1} MB — double-click for details"), kd.DumpTypeName, fi.Length / (1024 * 1024));
                        }
                    }
                    catch (Exception mex) { AppLogger.Debug("KernelDumpParser MEMORY.DMP: " + mex.Message); }
                    results.Add(memEntry);
                }

                results.Sort((a, b) => b.CrashTime.CompareTo(a.CrashTime));
                return results;
            });
        }

        private static string ExtractStopCodeFromName(string fileName)
        {
            // Minidump filenames: MMDDYY-NN.dmp or Mini123456-01.dmp
            return fileName.Replace(".dmp", "").Replace("Mini", "");
        }

        private static readonly System.Text.RegularExpressions.Regex BugcheckRegex =
            new(@"bugcheck was:\s*(0x[0-9a-fA-F]+)\s*\(([^)]*)\)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Result of a successful Event Log extraction: the code + the 4 bugcheck parameters, taken directly
        /// from the text of event 1001 — no binary parsing of the .dmp file (the Windows message already has everything).</summary>
        private readonly struct BugcheckInfo
        {
            public readonly string Code;
            public readonly string Params;
            public BugcheckInfo(string code, string prms) { Code = code; Params = prms; }
        }

        private static string FormatStopCode(BugcheckInfo info)
        {
            // the name comes from the same table as for dumps read directly (KernelDumpParser)
            string hex = info.Code.Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
            string friendly = "";
            if (uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint code))
            {
                string name = KernelDumpParser.DescribeBugCheck(code).Name;
                if (!name.StartsWith("BUGCHECK_0x", StringComparison.Ordinal)) friendly = " " + name;
            }
            return $"{info.Code}{friendly}";
        }

        private static string TryGetStopCodeFromEventLog(DateTime crashTime, out string bugcheckParams)
        {
            bugcheckParams = "";
            try
            {
                var from = crashTime.AddMinutes(-5).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                var to   = crashTime.AddMinutes( 5).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                string xpath = $"*[System[(EventID=41 or EventID=1001 or EventID=1003)" +
                               $" and TimeCreated[@SystemTime>='{from}' and @SystemTime<='{to}']]]";

                using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(
                    new System.Diagnostics.Eventing.Reader.EventLogQuery("System",
                        System.Diagnostics.Eventing.Reader.PathType.LogName, xpath));

                string? fallbackEventId = null;
                System.Diagnostics.Eventing.Reader.EventRecord? rec;
                while ((rec = reader.ReadEvent()) != null)
                {
                    using (rec)
                    {
                        try
                        {
                            var msg = rec.FormatDescription() ?? "";
                            var m = BugcheckRegex.Match(msg);
                            if (m.Success)
                            {
                                bugcheckParams = m.Groups[2].Value.Trim();
                                return FormatStopCode(new BugcheckInfo(m.Groups[1].Value, bugcheckParams));
                            }
                            fallbackEventId ??= $"EventID {rec.Id}";
                        }
                        catch (Exception ex) { AppLogger.Debug($"CrashService parse: {ex.Message}"); }
                    }
                }
                if (fallbackEventId != null) return fallbackEventId;
            }
            catch (Exception ex) { AppLogger.Warning(ex, "CrashService.unknown"); }
            return "";
        }


        public void OpenMinidumpFolder()
        {
            if (Directory.Exists(MiniDumpPath))
                System.Diagnostics.Process.Start("explorer.exe", MiniDumpPath);
        }

        private static readonly string[] WinDbgPaths =
        {
            @"C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\windbg.exe",
            @"C:\Program Files\WindowsApps\Microsoft.WinDbg_1.2306.12001.0_x64__8wekyb3d8bbwe\DbgX.Shell.exe",
        };

        public string? FindWinDbg()
        {
            try
            {
                string alias = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "WindowsApps", "WinDbgX.exe");
                if (File.Exists(alias)) return alias;
            }
            catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            foreach (var path in WinDbgPaths)
                if (File.Exists(path)) return path;
            return null;
        }

        public bool IsWinDbgAvailable() => FindWinDbg() != null;

        public string LastInstallError { get; private set; } = "";

        public async Task<bool> InstallWinDbgAsync()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName  = "winget",
                    Arguments = "install --id Microsoft.WinDbg -e --accept-source-agreements --accept-package-agreements",
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) { LastInstallError = "Could not start winget."; return false; }
                string stdErr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();
                AppLogger.Info($"WinDbg install: winget exited with code {proc.ExitCode}");
                bool installed = FindWinDbg() != null;
                if (!installed)
                    LastInstallError = !string.IsNullOrWhiteSpace(stdErr)
                        ? stdErr.Trim()
                        : $"winget exited with code {proc.ExitCode}, but WinDbg was not found afterward.";
                return installed;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                LastInstallError = "winget is not available on this Windows edition (common on LTSC/IoT LTSC — no Microsoft Store App Installer). Download WinDbg manually from: https://aka.ms/windbg";
                AppLogger.Warning("InstallWinDbgAsync: winget not found (Win32Exception) — likely LTSC/IoT LTSC edition");
                return false;
            }
            catch (Exception ex)
            {
                // winget is missing on some systems (LTSC, older Windows) — log it and let the UI show the manual install message.
                LastInstallError = ex.Message;
                AppLogger.Warning(ex, "InstallWinDbgAsync");
                return false;
            }
        }

        public void OpenWithWinDbg(string filePath)
        {
            var exe = FindWinDbg();
            if (exe != null)
            {
                // -z = open a crash dump file (documented WinDbg argument; WinDbgX ignores a bare path).
                try { System.Diagnostics.Process.Start(exe, $"-z \"{filePath}\""); return; }
                catch (Exception ex) { AppLogger.Warning(ex, "OpenWithWinDbg"); }
            }
            SMDWin.Views.AppDialog.Show(
                SMDWin.Services.LanguageService.S("Crash", "WinDbgNotFoundMessage"),
                SMDWin.Services.LanguageService.S("Crash", "WinDbgNotFoundTitle"), SMDWin.Views.AppDialog.Kind.Info);
        }
    }
}
