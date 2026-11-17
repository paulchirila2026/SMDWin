using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    /// <summary>
    /// Manages the PawnIO driver — a SIGNED and SAFE alternative to WinRing0 (which antivirus products flag because of
    /// CVE-2020-14979).
    ///
    /// PawnIO runs modules signed by its author (namazso) and exposes controlled ioctls to user space, instead of giving
    /// raw kernel access like WinRing0.
    ///
    /// Install: PawnIO_setup.exe -install -silent  (requires UAC).
    /// Detection: the "PawnIO" service/driver registered in SYSTEM\CurrentControlSet\Services.
    /// </summary>
    public static class PawnIOService
    {
        /// <summary>Reason for the last failed install, explained for the user (empty if unknown).</summary>
        public static string LastFailureReason { get; private set; } = "";

        // Official installer URL — the latest release signed by namazso (the author of PawnIO).
        // "latest/download" always redirects to the newest version.
        private const string SetupUrl =
            "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";

        /// <summary>True if the PawnIO driver is installed (a registered kernel service).</summary>
        public static bool IsInstalled()
        {
            try
            {
                // 1) Serviciul kernel "PawnIO" in registry
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\PawnIO"))
                {
                    if (key != null) return true;
                }
                // 2) The installed files (Program Files\PawnIO)
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (Directory.Exists(Path.Combine(pf, "PawnIO"))) return true;
                // 3) The driver in the drivers folder (installed without a visible service entry)
                string drivers = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "PawnIO.sys");
                if (File.Exists(drivers)) return true;
            }
            catch (Exception ex) { AppLogger.Debug("PawnIOService.IsInstalled: " + ex.Message); }
            return false;
        }

        /// <summary>
        /// Runs the PawnIO installer silently, with UAC (bundled copy first, download as a fallback).
        /// Returns true if the installer ran successfully (exit code 0).
        /// Throws Win32Exception if the user declines UAC.
        /// </summary>
        public static async Task<bool> InstallAsync(IProgress<string>? progress = null)
        {
            LastFailureReason = "";
            // a new folder with a unique name for every install: no other program can replace a file with a known name in %TEMP%
            string installerDir = Path.Combine(Path.GetTempPath(), "SMDWin_PawnIO_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(installerDir);
            string installer = Path.Combine(installerDir, "PawnIO_setup.exe");

            // 1) OFFLINE FIRST: try to extract the installer bundled in the EXE (embedded resource).
            // That way the install works without internet, on any computer.
            bool haveLocal = false;
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                // the resource name: <root namespace>.PawnIO_setup.exe (searched by suffix)
                string? resName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("PawnIO_setup.exe", StringComparison.OrdinalIgnoreCase));
                if (resName != null)
                {
                    progress?.Report("Extracting bundled PawnIO installer…");
                    using var rs = asm.GetManifestResourceStream(resName);
                    if (rs != null && rs.Length > 10000)
                    {
                        using var fs = File.Create(installer);
                        await rs.CopyToAsync(fs).ConfigureAwait(false);
                        haveLocal = true;
                    }
                }
                else
                {
                    AppLogger.Warning("PawnIOService: embedded installer resource not found — this build was compiled without Assets\\PawnIO_setup.exe, falling back to online download.");
                }
            }
            catch (Exception ex) { AppLogger.Debug("PawnIO embedded extract: " + ex.Message); }

            // 2) FALLBACK: if it is not bundled, download it from GitHub (needs internet).
            if (!haveLocal)
            {
                progress?.Report("Checking internet connection…");
                try
                {
                    using var pingCts = SMDWin.Services.SharedHttp.TimeoutCts(TimeSpan.FromSeconds(3));
                    using var pingResp = await SMDWin.Services.SharedHttp.Instance.GetAsync("https://github.com",
                        System.Net.Http.HttpCompletionOption.ResponseHeadersRead, pingCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    AppLogger.Warning(ex, "PawnIOService.InstallAsync connectivity check");
                    progress?.Report("No internet connection detected, and no bundled installer was found. Connect to the internet and try again.");
                    return false;
                }

                progress?.Report("Downloading PawnIO installer… 0%");
                try
                {
                    using var dlCts = SMDWin.Services.SharedHttp.TimeoutCts(TimeSpan.FromSeconds(180));
                    var http = SMDWin.Services.SharedHttp.Instance;

                    using var response = await http.GetAsync(SetupUrl,
                        System.Net.Http.HttpCompletionOption.ResponseHeadersRead, dlCts.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    long? totalBytes = response.Content.Headers.ContentLength;

                    using (var httpStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var fileStream = File.Create(installer))
                    {
                        var buffer = new byte[81920];
                        long readTotal = 0;
                        int lastReportedPct = -5;
                        int read;
                        while ((read = await httpStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                        {
                            await fileStream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                            readTotal += read;
                            if (totalBytes is > 0)
                            {
                                int pct = (int)(readTotal * 100 / totalBytes.Value);
                                if (pct >= lastReportedPct + 5)
                                {
                                    lastReportedPct = pct;
                                    progress?.Report($"Downloading PawnIO installer… {pct}%");
                                }
                            }
                            else
                            {
                                // No Content-Length (rare, but possible) — report at least how many MB arrived, so progress is visible.
                                progress?.Report($"Downloading PawnIO installer… {readTotal / 1024 / 1024:F1} MB");
                            }
                        }
                    }

                    var info = new FileInfo(installer);
                    if (!info.Exists || info.Length < 10000)
                        throw new InvalidOperationException($"Downloaded file too small ({(info.Exists ? info.Length : 0)} bytes)");
                }
                catch (TaskCanceledException ex) when (!ex.CancellationToken.IsCancellationRequested)
                {
                    // HttpClient.Timeout expires as TaskCanceledException, not TimeoutException — a specific message, distinct from a
                    // real network error.
                    AppLogger.Warning(ex, "PawnIOService.InstallAsync download timeout");
                    progress?.Report("Download timed out after 3 minutes — the network may be slow. Try again, or check your connection.");
                    return false;
                }
                catch (System.Net.Http.HttpRequestException ex)
                {
                    AppLogger.Warning(ex, "PawnIOService.InstallAsync download");
                    progress?.Report($"Download failed — {ex.Message}");
                    return false;
                }
                catch (Exception ex)
                {
                    AppLogger.Warning(ex, "PawnIOService.InstallAsync download");
                    progress?.Report($"Download failed — {ex.Message}");
                    return false;
                }
            }

            // Authenticode check before running elevated. If the signature is not valid OR the publisher is not the expected one
            // (namazso, the author of PawnIO), the file is NOT run — whether it was extracted locally or downloaded.
            // Windows without updates (e.g. an unpatched 10 LTSC 2021) does not know the newer root certificates, so the signature
            // cannot be verified. For the installer shipped inside SMDWin there is a second safe path: the file's SHA-256
            // fingerprint is written in the program; if the extracted file has exactly this fingerprint, it is the shipped one
            // and it runs. The file stays open (no write access for others) until the installer starts.
            FileStream? pinnedLock = null;
            bool sigOk = AuthenticodeVerifier.IsValidAndTrusted(installer, out string sigError);
            if (!sigOk && haveLocal && MatchesBundledHash(installer, out pinnedLock))
            {
                AppLogger.Info("PawnIOService: signature cannot be verified on this Windows ({0}), but the installer has the fingerprint of the one shipped with SMDWin — continuing.", sigError);
                sigOk = true;
            }
            if (!sigOk)
            {
                AppLogger.Warning("PawnIOService.InstallAsync: Authenticode check failed for {0}: {1} — the install does NOT continue.", installer, sigError);
                LastFailureReason = LanguageService.S("PawnIo", "SignatureUnverified",
                    "The driver installer's digital signature could not be verified, so it was not run. This usually happens on Windows installations without recent updates, which do not recognize newer certificates. Install the latest Windows updates, then try again.");
                progress?.Report(LastFailureReason);
                try { File.Delete(installer); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                return false;
            }
            if (pinnedLock == null && !IsExpectedPublisher(installer, out string? actualSubject))
            {
                AppLogger.Warning("PawnIOService.InstallAsync: valid signature, but unexpected publisher ({0}) for {1} — the install does NOT continue.", actualSubject ?? "necunoscut", installer);
                progress?.Report("Security check failed: the installer is signed by an unexpected publisher. Installation aborted.");
                try { File.Delete(installer); } catch (Exception ex) { AppLogger.Debug(ex.Message); }
                return false;
            }

            progress?.Report("Installing driver (approve the UAC prompt)…");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName        = installer,
                    Arguments       = "-install -silent",
                    UseShellExecute = true,
                    Verb            = "runas",   // UAC
                    CreateNoWindow  = true,
                };
                using var p = Process.Start(psi);
                pinnedLock?.Dispose(); pinnedLock = null;
                if (p == null) return false;
                await p.WaitForExitAsync().ConfigureAwait(false);
                bool ok = p.ExitCode == 0;
                progress?.Report(ok ? "PawnIO installed." : $"Installer exited with code {p.ExitCode}.");
                return ok;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // userul a refuzat UAC
                progress?.Report("Installation cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Warning(ex, "PawnIOService.InstallAsync run");
                progress?.Report("Installation failed.");
                return false;
            }
            finally { pinnedLock?.Dispose(); }
        }
        /// <summary>SHA-256 fingerprint of the Assets\PawnIO_setup.exe shipped with SMDWin. When the installer is replaced it
        /// must change here too (otherwise, on Windows without the new certificates, the install stops — nothing unverified runs).</summary>
        private const string BundledInstallerSha256 = "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032";

        private static bool MatchesBundledHash(string filePath, out FileStream? lockStream)
        {
            lockStream = null;
            try
            {
                var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var sha = System.Security.Cryptography.SHA256.Create();
                string hex = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
                if (hex == BundledInstallerSha256) { lockStream = fs; return true; }
                AppLogger.Warning("PawnIOService: the installer fingerprint ({0}) is not the shipped one — not run.", hex);
                fs.Dispose();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "PawnIOService.MatchesBundledHash"); }
            return false;
        }

        /// <summary>Extra "pinning" check — even if the signature is valid and trusted by Windows, make sure the signer really
        /// is namazso (the known PawnIO author), not just "any valid certificate".</summary>
        private static bool IsExpectedPublisher(string filePath, out string? subject)
        {
            subject = null;
            try
            {
                using var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(filePath);
                subject = cert.Subject;
                return subject != null && subject.Contains("namazso", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("PawnIOService.IsExpectedPublisher: " + ex.Message);
                return false;
            }
        }
    }
}
