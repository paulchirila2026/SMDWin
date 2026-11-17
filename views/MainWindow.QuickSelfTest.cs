using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using SMDWin.Services;

namespace SMDWin.Views
{
    /// <summary>
    /// Quick start of the self-test: 5 clicks on LIVE (Dashboard) within 3 seconds. No Developer menu needed: progress
    /// appears in a toast, and at the end a single zip file (report + app log + settings + screenshots of every page on
    /// Light and Dark) is saved next to SMDWin.exe —
    /// the name contains the computer name, so results from several PCs do not get mixed up.
    /// </summary>
    public partial class MainWindow
    {
        private int _liveClicks;
        private DateTime _liveFirstClickUtc;
        private bool _quickSelfTestRunning;

        private static string QT(string key, string en) => LanguageService.S("Diag", key, en);

        internal void LiveBadge_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var now = DateTime.UtcNow;
            if ((now - _liveFirstClickUtc).TotalSeconds > 3) { _liveFirstClickUtc = now; _liveClicks = 0; }
            if (++_liveClicks < 5) return;
            _liveClicks = 0;

            if (_quickSelfTestRunning)
            {
                ShowToastInfo(QT("QuickSelfTestBusy", "The self-test is already running."));
                return;
            }
            // short = the tests + the screenshots of every page; long = also every tool started for ~10 s and captured while it
            // runs (the last option is the primary button, so Short stays the default)
            int choice = AppDialog.Choose(
                QT("QuickSelfTestModeAsk", "Which self-test?\n\nShort: the tests and a screenshot of every page (a few minutes).\n\nLong: also starts every tool for about 10 seconds — speed test, ping, LAN, surface scan, RAM test, CPU + GPU stress — captures it while it runs, then runs the Benchmark page (about 5 minutes more; the speed test uses internet data). The storage benchmark writes a temporary file and deletes it. No setting changes."),
                QT("QuickSelfTestModeTitle", "Self-test"), LanguageService.S("Common", "Cancel", "Cancel"),
                QT("QuickSelfTestModeLong", "Long"), QT("QuickSelfTestModeShort", "Short"));
            if (choice < 0) return;
            _ = RunQuickSelfTestAsync(longMode: choice == 0); // fire-and-forget: errors are caught and logged inside
        }

        private async Task RunQuickSelfTestAsync(bool longMode = false)
        {
            _quickSelfTestRunning = true;
            AppLogger.Info("Quick self-test started from the Dashboard (LIVE ×5), {0}", longMode ? "long" : "short");
            // the progress card (a separate small window: it never shows in the screenshots); the tests take about the first 45 %,
            // the screenshots the rest
            SelfTestProgressWindow? card = null;
            try { card = new SelfTestProgressWindow(); card.Show(); }
            catch (Exception ex) { AppLogger.Debug($"Self-test progress card: {ex.Message}"); card = null; }
            void Report(double f, string? step) => Dispatcher.InvokeAsync(() => { try { card?.Report(f, step); } catch (Exception ex) { AppLogger.Debug(ex.Message); } });
            Report(0.01, QT("SelfTestStepTests", "Tests"));
            var uiData = new SelfTestUiData { SwallowedAtStart = AppLogger.SwallowedCounts() };

            void OnProgress(string line)
            {
                // the title of each step ("── 2/7  CPU STRESS …") moves the bar
                string t = line.TrimStart();
                if (!t.StartsWith("── ") || t.Length < 8 || !char.IsDigit(t[3])) return;
                string[] parts = t[3..].Split("  ", 2, StringSplitOptions.None);
                string[] nm = parts[0].Trim().Split('/');
                if (nm.Length == 2 && int.TryParse(nm[0], out int n) && int.TryParse(nm[1], out int of) && of > 0)
                {
                    string name = parts.Length > 1 ? parts[1].Trim().TrimEnd('─', ' ') : "";
                    Report(0.38 * (n - 1) / of, $"{QT("SelfTestStepTests", "Tests")} {n}/{of} · {DisplayFormat.SentenceCase(name.ToLowerInvariant())}");
                }
            }

            try
            {
                var run = await SelfTestRunner.RunAllAsync(this, OnProgress, CancellationToken.None);

                // the data checks: the same value from two sources, the sensors watched for 20 s
                string dataLabel = QT("SelfTestStepData", "Checking the readings");
                Report(0.38, dataLabel);
                try { await RunDataChecksAsync(uiData.DataChecks, f => Report(0.38 + 0.07 * f, dataLabel), CancellationToken.None); }
                catch (Exception ex) { AppLogger.Warning(ex, "Quick self-test data checks"); }
                Report(0.45, QT("QuickSelfTestShots", "screenshots"));

                // the screenshots (every page, Light and Dark, the tabs, the network tools and the windows), into the same zip
                string shotsDir = AppPaths.Data(Path.Combine("SelfTest-Results", $"screenshots_{DateTime.Now:yyyyMMdd_HHmmss}"));
                var shots = new System.Collections.Generic.List<string>();
                try
                {
                    string shotsLabel = QT("QuickSelfTestShots", "screenshots");
                    shots = await CaptureSelfTestScreenshotsAsync(shotsDir,
                        (step, f) => Report(0.45 + 0.52 * f, $"{DisplayFormat.SentenceCase(shotsLabel)} · {step}"), CancellationToken.None, uiData, longMode);
                }
                catch (Exception ex) { AppLogger.Warning(ex, "Quick self-test screenshots"); }

                Report(0.98, QT("SelfTestStepZip", "Saving the file"));
                string zip = await Task.Run(() => BuildQuickSelfTestZip(run.SavedPath, shotsDir, shots));
                try { if (Directory.Exists(shotsDir) && zip.Length > 0) Directory.Delete(shotsDir, true); }
                catch (Exception ex) { AppLogger.Debug($"Quick self-test: screenshot folder cleanup: {ex.Message}"); }
                if (zip.Length > 0)
                {
                    AppLogger.Info("Quick self-test saved: {0}", zip);
                    ShowToastSuccess(string.Format(QT("QuickSelfTestDone", "Self-test finished · saved next to SMD Win: {0}"),
                                                   Path.GetFileName(zip)), 10_000);
                    // the toast alone was easy to miss (it appeared under the progress card): a clear message once the card has
                    // closed, with the folder one click away
                    string zipPath = zip;
                    _ = Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        await Task.Delay(900);
                        try
                        {
                            System.Media.SystemSounds.Asterisk.Play();
                            if (AppDialog.Confirm(string.Format(QT("QuickSelfTestDoneDialog", "Self-test done.\n\nThe file {0} is saved next to SMD Win.\n\nOpen the folder?"), Path.GetFileName(zipPath)),
                                                  QT("QuickSelfTestDoneTitle", "Self-test done")))
                                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{zipPath}\"");
                        }
                        catch (Exception ex) { AppLogger.Debug($"Self-test done dialog: {ex.Message}"); }
                    }));
                }
                else
                {
                    ShowToastWarning(QT("QuickSelfTestNoZip", "Self-test finished, but the file could not be saved. The report is in Data › SelfTest-Results."), 10_000);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "Quick self-test");
                ShowToastError(QT("QuickSelfTestFailed", "The self-test stopped because of an error. Details are in the log."), 8_000);
            }
            finally
            {
                _quickSelfTestRunning = false;
                try { card?.Report(1, null); await Task.Delay(600); card?.Close(); }
                catch (Exception ex) { AppLogger.Debug($"Self-test progress card close: {ex.Message}"); }
            }
        }

        /// <summary>Zip next to the exe (or in Data › SelfTest-Results if the exe folder is not writable). "" on failure.</summary>
        private static string BuildQuickSelfTestZip(string reportPath, string shotsDir = "", System.Collections.Generic.IReadOnlyList<string>? shots = null)
        {
            string name = $"SMDWin_SelfTest_{SafeFileName(Environment.MachineName)}_b{AppVersionInfo.Build}_{DateTime.Now:yyyy-MM-dd_HHmm}.zip";   // b57 = the build
            foreach (string dir in new[] { AppPaths.ExeDirectory, AppPaths.Data("SelfTest-Results") })
            {
                string path = Path.Combine(dir, name);
                try
                {
                    Directory.CreateDirectory(dir);
                    if (File.Exists(path)) File.Delete(path);
                    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
                    AddShared(zip, reportPath, Path.GetFileName(reportPath));
                    AddShared(zip, Path.Combine(AppLogger.LogDirectory, $"smdwin-{DateTime.Now:yyyyMMdd}.log"), "smdwin-log.txt");
                    AddShared(zip, SettingsService.SettingsFilePath, "settings.json");
                    AddShared(zip, AppPaths.Data("lhm-report.txt"), "lhm-report.txt");   // fan / Super I/O diagnosis
                    // the Benchmark runs (last 10): raw values, scales, GPU batch statistics, sensor timeline — to tune the marks
                    try
                    {
                        string benchDir = AppPaths.Data("Benchmark");
                        if (Directory.Exists(benchDir))
                            foreach (string bf in Directory.GetFiles(benchDir, "benchmark-*.txt"))
                                AddShared(zip, bf, "benchmark/" + Path.GetFileName(bf));
                    }
                    catch (Exception exB) { AppLogger.Debug($"Self-test zip benchmark files: {exB.Message}"); }
                    // screenshots/light/01-Dashboard.png … (PNG is already compressed: stored as is)
                    if (shots != null)
                        foreach (string shot in shots)
                        {
                            string rel = Path.GetRelativePath(shotsDir, shot).Replace('\\', '/');
                            // the report opens from the zip root; its picture links start with screenshots/
                            string entry = rel == "report.html" ? "report.html" : "screenshots/" + rel;
                            var level = rel.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                            try { zip.CreateEntryFromFile(shot, entry, level); }
                            catch (Exception ex) { AppLogger.Warning(ex, $"Quick self-test zip: {rel}"); }
                        }
                    return path;
                }
                catch (Exception ex)
                {
                    AppLogger.Warning(ex, $"Quick self-test zip in {dir}");
                    try { if (File.Exists(path)) File.Delete(path); }
                    catch (Exception ex2) { AppLogger.Debug(ex2.Message); }
                }
            }
            return "";
        }

        /// <summary>Copies a file into the archive even if another process has it open (the log Serilog is still writing).</summary>
        private static void AddShared(ZipArchive zip, string source, string entryName)
        {
            if (string.IsNullOrEmpty(source) || !File.Exists(source)) return;
            try
            {
                using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dst = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
                src.CopyTo(dst);
            }
            catch (Exception ex) { AppLogger.Warning(ex, $"Quick self-test zip: {entryName}"); }
        }

        private static string SafeFileName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
    }
}
