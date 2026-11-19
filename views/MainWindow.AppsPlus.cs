using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SMDWin.Models;
using SMDWin.Services;

namespace SMDWin.Views
{
    /// <summary>
    /// Applications, stage 3 (mockup "tools3"):
    ///   • Updates — the programs with a newer version, found with winget (the Windows package tool); Update one (context
    ///     menu) or all (button next to the filters). Hidden when winget is not on this PC.
    ///   • Unused — not started for 90 days, from the prefetch files Windows keeps (AppInsight.LastRunOnly).
    ///   • Leftovers — after an uninstall, the folders and registry keys the uninstaller left (only the usual places, never
    ///     shared or system makers' folders); removed after confirmation: folders to the Recycle Bin, keys exported first.
    /// </summary>
    public partial class MainWindow
    {
        private Dictionary<string, (string Id, string Installed, string Available, string Name)>? _wingetUpdates;
        private bool _wingetChecked, _wingetBusy;
        private readonly Dictionary<string, (bool Known, DateTime? Last)> _lastRunCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Puts the cached winget results and last starts on the (reloaded) list.</summary>
        private void ApplyAppExtras()
        {
            foreach (var a in _allApps)
            {
                if (_lastRunCache.TryGetValue(AppKey(a), out var lr)) { a.LastRunKnown = lr.Known; a.LastRun = lr.Last; }
                var up = _wingetUpdates == null ? null : MatchWinget(a);
                a.UpdateVersion = up?.Available;
                a.WingetId = up?.Id;
            }
            int n = _allApps.Count(a => a.UpdateVersion != null);
            if (FindName("BtnAppFilterUpdates") is Wpf.Ui.Controls.Button b)
            {
                b.Content = n > 0 ? $"{PT("Updates")} · {n}" : PT("Updates");
                b.Visibility = n > 0 || _appFilter == "Updates" ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private static string AppKey(InstalledApp a) => a.Name + "|" + a.Version;

        /// <summary>Last starts (all programs) and winget (once per session), in the background; then the list is refreshed.</summary>
        private async Task EnrichAppsAsync()
        {
            try
            {
                var apps = _allApps.ToList();
                var todo = apps.Where(a => !_lastRunCache.ContainsKey(AppKey(a))).ToList();
                if (todo.Count > 0)
                {
                    var found = await Task.Run(() => todo.Select(a => (key: AppKey(a), r: AppInsight.LastRunOnly(a))).ToList());
                    foreach (var f in found) _lastRunCache[f.key] = f.r;
                }
                ApplyAppExtras();
                ApplyAppsView();

                if (!_wingetChecked)
                {
                    _wingetChecked = true;
                    _wingetUpdates = await Task.Run(ReadWingetUpdates);
                    AppLogger.Info("Apps: winget {0}", _wingetUpdates == null ? "not available" : $"{_wingetUpdates.Count} updates");
                    ApplyAppExtras();
                    ApplyAppsView();
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "EnrichAppsAsync"); }
        }

        private static string? WingetPath()
        {
            string alias = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe");
            if (File.Exists(alias)) return alias;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                try { var p = Path.Combine(dir.Trim(), "winget.exe"); if (File.Exists(p)) return p; } catch { }
            return null;
        }

        private static (int Exit, string Output) RunWinget(string args, int timeoutSec)
        {
            string? exe = WingetPath();
            if (exe == null) return (-2, "");
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return (-2, "");
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutSec * 1000)) { try { p.Kill(true); } catch { } return (-1, "timeout"); }
            return (p.ExitCode, o.Result + e.Result);
        }

        /// <summary>
        /// "winget upgrade" as a table. The columns are found from the header line above the dashes (by position, so the
        /// localized header words do not matter): name, id, installed version, available version, source.
        /// Null = winget is not available.
        /// </summary>
        private static Dictionary<string, (string Id, string Installed, string Available, string Name)>? ReadWingetUpdates()
        {
            var (exit, output) = RunWinget("upgrade --accept-source-agreements --disable-interactivity", 120);
            if (exit == -2) return null;
            return ParseWingetTable(output);
        }

        /// <summary>The upgrade table of winget's output (see ReadWingetUpdates), by id.</summary>
        internal static Dictionary<string, (string Id, string Installed, string Available, string Name)> ParseWingetTable(string output)
        {
            var result = new Dictionary<string, (string, string, string, string)>(StringComparer.OrdinalIgnoreCase);
            var lines = output.Replace("\b", "").Split('\n').Select(l => { int r = l.TrimEnd('\r').LastIndexOf('\r'); return (r >= 0 ? l[(r + 1)..] : l).TrimEnd('\r'); }).ToList();
            int sep = lines.FindIndex(l => Regex.IsMatch(l.Trim(), "^-{10,}$"));
            if (sep < 1) return result;
            string header = lines[sep - 1];
            var starts = new List<int>();
            for (int i = 0; i < header.Length; i++)
                if (header[i] != ' ' && (i == 0 || header[i - 1] == ' ')) starts.Add(i);
            if (starts.Count < 4) return result;
            string Col(string row, int c)
            {
                int a = starts[c], b = c + 1 < starts.Count ? starts[c + 1] : row.Length;
                if (a >= row.Length) return "";
                return row[a..Math.Min(b, row.Length)].Trim();
            }
            for (int i = sep + 1; i < lines.Count; i++)
            {
                string row = lines[i];
                if (row.Trim().Length == 0 || row.Length < starts[3]) break;
                string name = Col(row, 0), id = Col(row, 1), ver = Col(row, 2), avail = Col(row, 3);
                if (id.Length == 0 || avail.Length == 0 || id.Contains(' ')) continue;
                result[id] = (id, ver, avail, name.TrimEnd('…'));
            }
            return result;
        }

        private (string Id, string Installed, string Available, string Name)? MatchWinget(InstalledApp a)
        {
            if (_wingetUpdates == null) return null;
            string n = a.Name.Trim();
            foreach (var u in _wingetUpdates.Values)
            {
                bool nameOk = u.Name.Length >= 3 && (n.StartsWith(u.Name, StringComparison.OrdinalIgnoreCase) || u.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase));
                if (!nameOk) continue;
                // the installed version confirms the match when both are known
                if (u.Installed.Length > 0 && a.Version.Length > 0 && !a.Version.StartsWith(u.Installed.TrimEnd('…'), StringComparison.OrdinalIgnoreCase)
                    && !u.Installed.StartsWith(a.Version, StringComparison.OrdinalIgnoreCase)) continue;
                return u;
            }
            return null;
        }

        private async Task<bool> WingetUpgradeAsync(InstalledApp a)
        {
            if (a.WingetId == null) return false;
            var (exit, output) = await Task.Run(() => RunWinget($"upgrade --id \"{a.WingetId}\" --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity", 1200));
            bool ok = exit == 0;
            AppLogger.Info("Apps: winget upgrade {0} → exit {1}", a.WingetId, exit);
            ToolActivityLog.Add("Update " + a.Name, ok ? $"{a.Version} → {a.UpdateVersion}" : $"winget error {exit}", ok ? "ok" : "warn");
            if (ok && _wingetUpdates != null) _wingetUpdates.Remove(a.WingetId);
            return ok;
        }

        private async void AppUpdateOne_Click(object sender, RoutedEventArgs e)
        {
            if (AppsGrid.SelectedItem is not InstalledApp a) return;
            if (a.WingetId == null) { ShowToastWarning(PT("No newer version was found for this program with winget")); return; }
            if (_wingetBusy) return;
            _wingetBusy = true;
            ShowToast(PT("Updating {0}…", a.Name));
            try
            {
                bool ok = await WingetUpgradeAsync(a);
                if (ok) ShowToast(PT("{0} was updated", a.Name)); else ShowToastWarning(PT("{0} could not be updated — its own updater may work", a.Name));
            }
            finally { _wingetBusy = false; }
            await LoadAppsInternalAsync();
        }

        private async void AppUpdateAll_Click(object sender, RoutedEventArgs e)
        {
            var list = _allApps.Where(a => a.WingetId != null).ToList();
            if (list.Count == 0 || _wingetBusy) return;
            if (!AppDialog.Confirm(PT("Update {0} programs with winget? Each one installs silently; open programs may be closed by their installer.", list.Count), PT("Update all"))) return;
            _wingetBusy = true;
            ApplyAppsView();
            int ok = 0;
            try
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (TxtAppsCount != null) TxtAppsCount.Text = PT("Updating {0} of {1}: {2}", i + 1, list.Count, list[i].Name);
                    if (await WingetUpgradeAsync(list[i])) ok++;
                }
            }
            finally { _wingetBusy = false; }
            ShowToast(PT("{0} of {1} programs updated", ok, list.Count));
            await LoadAppsInternalAsync();
        }

        // ══ leftovers after an uninstall ════════════════════════════════════════════════════════════════════════════════

        private static readonly string[] NeverTouchMakers = { "microsoft", "windows", "intel", "nvidia", "amd", "advanced micro devices", "realtek", "common files", "package cache", "installer", "google", "apple", "oracle" };

        /// <summary>Waits (up to 20 min) for the program's uninstall key to disappear, then looks for leftovers.</summary>
        private async Task WatchUninstallAsync(InstalledApp app)
        {
            try
            {
                string path = app.RegistryPath ?? "";
                bool Exists()
                {
                    int cut = path.IndexOf('\\');
                    if (cut < 0) return false;
                    var root = path.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase) ? Registry.CurrentUser : Registry.LocalMachine;
                    using var k = root.OpenSubKey(path[(cut + 1)..]);
                    return k != null;
                }
                if (path.Length == 0) return;
                var until = DateTime.Now.AddMinutes(20);
                while (DateTime.Now < until)
                {
                    await Task.Delay(4000);
                    if (!Exists()) break;
                }
                if (Exists()) return;   // not uninstalled (cancelled, or still running after 20 min)
                await Task.Delay(2500); // the uninstaller often deletes its last files a moment later
                await LoadAppsInternalAsync();
                var others = _allApps.ToList();
                var found = await Task.Run(() => FindLeftovers(app, others));
                if (found.Count == 0) { ToolActivityLog.Add("Uninstall " + app.Name, "no leftovers", "ok"); return; }
                long bytes = found.Where(f => !f.IsKey).Sum(f => f.Bytes);
                string list = string.Join("\n", found.Take(8).Select(f => "• " + f.Path + (f.IsKey ? "" : $"  ({LeftoverBytes(f.Bytes)})"))) + (found.Count > 8 ? $"\n• … +{found.Count - 8}" : "");
                bool go = AppDialog.Confirm(PT("{0} was uninstalled, but it left {1} items behind ({2}):", app.Name, found.Count, LeftoverBytes(bytes)) + "\n\n" + list + "\n\n" +
                                            PT("Remove them? Folders go to the Recycle Bin; registry keys are saved to Data\\Backups first."), PT("Leftovers"));
                if (!go) return;
                int removed = await Task.Run(() => RemoveLeftovers(found, app.Name));
                ToolActivityLog.Add("Uninstall " + app.Name, PT("{0} leftovers removed · {1}", removed, LeftoverBytes(bytes)), "ok");
                ShowToast(PT("{0} leftovers removed", removed));
            }
            catch (Exception ex) { AppLogger.Warning(ex, "WatchUninstallAsync"); }
        }

        private sealed record Leftover(string Path, bool IsKey, long Bytes);

        private static string CleanAppName(string name)
        {
            string n = Regex.Replace(name, @"\s*\(.*?\)", "");
            n = Regex.Replace(n, @"\s+(v|version\s*)?\d+(\.\d+)+.*$", "", RegexOptions.IgnoreCase);
            n = Regex.Replace(n, @"\s+(x64|x86|64-bit|32-bit)$", "", RegexOptions.IgnoreCase);
            return n.Trim();
        }

        /// <summary>
        /// The usual places a program leaves: its install folder; Program Files / AppData / LocalAppData / ProgramData with
        /// the program name (or maker\name; the maker folder only when no other installed program has that maker); the
        /// same names under HKCU/HKLM\Software. Shared folders and the folders of big system makers are never proposed.
        /// </summary>
        private static List<Leftover> FindLeftovers(InstalledApp app, List<InstalledApp> stillInstalled)
        {
            var found = new List<Leftover>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string name = CleanAppName(app.Name), maker = (app.Publisher ?? "").Trim().TrimEnd('.');
            maker = Regex.Replace(maker, @",?\s+(Inc|Ltd|LLC|GmbH|Corp|Corporation|Co|S\.?R\.?L|SRL)\.?$", "", RegexOptions.IgnoreCase).Trim();
            bool Bad(string s) => s.Length < 3 || NeverTouchMakers.Any(m => s.Equals(m, StringComparison.OrdinalIgnoreCase) || s.StartsWith(m + " ", StringComparison.OrdinalIgnoreCase));
            if (Bad(name)) return found;
            bool makerShared = maker.Length < 3 || Bad(maker) || stillInstalled.Any(o => (o.Publisher ?? "").StartsWith(maker, StringComparison.OrdinalIgnoreCase));
            // other installed programs using the same name start → keep their folders
            bool nameShared = stillInstalled.Any(o => CleanAppName(o.Name).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (nameShared) return found;

            void Dir(string? path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                path = path.Trim().Trim('"').TrimEnd('\\');
                if (!Directory.Exists(path) || !seen.Add(path)) return;
                if (!AppCommands.IsOwnFolder(path, AppInsight.SharedFoldersPublic())) return;
                // nothing inside the Windows folder is ever proposed, whatever the uninstaller said its folder was
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
                if (winDir.Length > 0 && (path.Equals(winDir, StringComparison.OrdinalIgnoreCase) || path.StartsWith(winDir + "\\", StringComparison.OrdinalIgnoreCase))) return;
                if (stillInstalled.Any(o => AppCommands.IsInside(o.InstallLocation, path) || string.Equals((o.InstallLocation ?? "").Trim().TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase))) return;
                found.Add(new Leftover(path, false, DirSize(path)));
            }
            Dir(app.InstallLocation);
            var bases = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            }.Where(b => b.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var b in bases)
            {
                Dir(Path.Combine(b, name));
                if (maker.Length >= 3 && !Bad(maker))
                {
                    Dir(Path.Combine(b, maker, name));
                    if (!makerShared) Dir(Path.Combine(b, maker));
                }
            }
            void Key(RegistryKey root, string rootName, string sub)
            {
                try
                {
                    using var k = root.OpenSubKey(sub);
                    if (k != null && seen.Add(rootName + "\\" + sub)) found.Add(new Leftover(rootName + "\\" + sub, true, 0));
                }
                catch { }
            }
            foreach (var (root, rn) in new[] { (Registry.CurrentUser, "HKEY_CURRENT_USER"), (Registry.LocalMachine, "HKEY_LOCAL_MACHINE") })
                foreach (var sw in new[] { "Software", @"Software\WOW6432Node" })
                {
                    if (maker.Length >= 3 && !Bad(maker))
                    {
                        Key(root, rn, $@"{sw}\{maker}\{name}");
                        if (!makerShared) Key(root, rn, $@"{sw}\{maker}");
                    }
                    Key(root, rn, $@"{sw}\{name}");
                }
            // a key inside another found key is removed with it
            return found.Where(f => !found.Any(o => o != f && o.IsKey == f.IsKey && f.Path.StartsWith(o.Path + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
        }

        private static long DirSize(string path)
        {
            try
            {
                long total = 0; int n = 0;
                var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", opt)) { total += f.Length; if (++n > 50000) break; }
                return total;
            }
            catch { return 0; }
        }

        private static int RemoveLeftovers(List<Leftover> items, string appName)
        {
            int removed = 0;
            string backups = AppPaths.Data("Backups");
            try { Directory.CreateDirectory(backups); } catch { }
            foreach (var it in items)
            {
                try
                {
                    if (it.IsKey)
                    {
                        string safe = Regex.Replace(appName, @"[^\w\-]+", "_");
                        string file = Path.Combine(backups, $"leftover-{safe}-{DateTime.Now:yyyyMMdd-HHmmss}-{removed}.reg");
                        RunQuiet("reg.exe", $"export \"{it.Path}\" \"{file}\" /y");
                        if (RunQuiet("reg.exe", $"delete \"{it.Path}\" /f") == 0) removed++;
                    }
                    else
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(it.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                                                                               Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                        removed++;
                    }
                    AppLogger.Info("Leftover removed: {0}", it.Path);
                }
                catch (Exception ex) { AppLogger.Warning("Leftover not removed: {0} — {1}", it.Path, ex.Message); }
            }
            return removed;
        }

        private static int RunQuiet(string file, string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
                if (p == null) return -1;
                p.WaitForExit(30000);
                return p.ExitCode;
            }
            catch { return -1; }
        }

        private static string LeftoverBytes(long b) =>
            b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0} MB" : $"{Math.Max(0, b) / 1024} KB";
    }
}
