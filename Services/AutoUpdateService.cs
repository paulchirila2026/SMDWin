using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    /// <summary>
    /// Checks GitHub Releases for a newer version of SMDWin and optionally downloads the installer.
    /// Usage: set GitHubOwner + GitHubRepo, then call CheckForUpdateAsync().
    /// </summary>
    public class AutoUpdateService : IDisposable
    {
        public static string GitHubOwner { get; set; } = "paulchirila2026";   // 17.11.2026: the public repository is paulchirila2026/SMDWin
        public static string GitHubRepo  { get; set; } = "SMDWin";
        // Asset name pattern to match in the release (e.g. "SMDWin-Setup.exe" or "SMDWin.zip")
        public static string AssetPattern { get; set; } = "SMDWin";

        /// <summary>True if GitHubOwner/GitHubRepo still look unconfigured.
        /// Permanent guard — if a future refactor resets the defaults, CheckForUpdateAsync refuses to query an unknown
        /// repository instead of failing silently (or, worse, hitting someone else's real repository by mistake).</summary>
        private static bool LooksUnconfigured =>
            string.IsNullOrWhiteSpace(GitHubOwner) ||
            string.IsNullOrWhiteSpace(GitHubRepo) ||
            GitHubOwner.Contains("your-", StringComparison.OrdinalIgnoreCase) ||
            GitHubOwner.Contains("username", StringComparison.OrdinalIgnoreCase);

        // ── Result ─────────────────────────────────────────────────────────────
        public record UpdateInfo(
            bool   Available,
            string LatestVersion,
            string CurrentVersion,
            string ReleaseNotes,
            string DownloadUrl,
            string HtmlUrl,
            string AssetName);

        private readonly HttpClient _http;

        public AutoUpdateService()
        {
            _http = new HttpClient();
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("SMDWin", GetCurrentVersion()));
            _http.Timeout = TimeSpan.FromSeconds(15);
        }

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Queries GitHub for the latest release. Returns an UpdateInfo.
        /// Never throws — returns Available=false on any error.
        /// </summary>
        public async Task<UpdateInfo> CheckForUpdateAsync(
            System.Threading.CancellationToken ct = default)
        {
            string current = GetCurrentVersion();

            if (LooksUnconfigured)
            {
                AppLogger.Warning("AutoUpdateService: GitHubOwner/GitHubRepo look unconfigured — skipping the check.");
                return new UpdateInfo(false, current, current, "", "", "", "");
            }

            try
            {
                var url  = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
                var json = await _http.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string tagName = root.GetProperty("tag_name").GetString() ?? "";
                // Strip 'v' prefix and any pre-release suffix like "-beta.1", "-rc2"
                string latestV = StripPreReleaseSuffix(tagName.TrimStart('v', 'V'));
                string body    = root.TryGetProperty("body",     out var bodyEl) ? bodyEl.GetString() ?? "" : "";
                string htmlUrl = root.TryGetProperty("html_url", out var hu)     ? hu.GetString()   ?? "" : "";

                // Skip pre-release builds (marked as prerelease=true in API)
                bool isPrerelease = root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean();
                bool available = !isPrerelease && IsNewer(latestV, current);

                // Find matching asset
                string dlUrl     = htmlUrl;
                string assetName = "";
                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString() ?? "";
                        if (name.Contains(AssetPattern, StringComparison.OrdinalIgnoreCase))
                        {
                            dlUrl     = asset.GetProperty("browser_download_url").GetString() ?? htmlUrl;
                            assetName = name;
                            break;
                        }
                    }
                }

                return new UpdateInfo(available, latestV, current, body, dlUrl, htmlUrl, assetName);
            }
            catch (OperationCanceledException)
            {
                return new UpdateInfo(false, current, current, "", "", "", "");
            }
            catch
            {
                return new UpdateInfo(false, current, current, "", "", "", "");
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        public static string GetCurrentVersion()
        {
            try
            {
                var v = Assembly.GetEntryAssembly()?.GetName().Version;
                return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
            }
            catch { return "0.0.0"; }
        }

        /// <summary>Returns true if candidate is strictly newer than current (semver).</summary>
        private static bool IsNewer(string candidate, string current)
        {
            try
            {
                var c = Version.Parse(PadVersion(candidate));
                var r = Version.Parse(PadVersion(current));
                return c > r;
            }
            catch { return false; }
        }

        private static string PadVersion(string v)
        {
            var parts = v.Split('.');
            // Ensure at least Major.Minor.Patch
            return parts.Length switch
            {
                1 => $"{parts[0]}.0.0",
                2 => $"{parts[0]}.{parts[1]}.0",
                _ => $"{parts[0]}.{parts[1]}.{parts[2]}",
            };
        }

        /// <summary>Strips pre-release suffixes like "-beta.1", "-rc2", "-alpha" from a version string.</summary>
        private static string StripPreReleaseSuffix(string v)
        {
            int dash = v.IndexOf('-');
            return dash > 0 ? v[..dash] : v;
        }

        public void Dispose() => _http.Dispose();
    }
}
