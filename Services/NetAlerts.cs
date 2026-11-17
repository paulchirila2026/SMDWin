using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    /// <summary>
    /// Network alerts, all off by default and switched on in Network › Apps. They go to the notification centre (the bell):
    ///  • a program uses the internet for the first time — compared with the list of programs already seen on this PC
    ///    (Data\net-known-apps.txt; the first 2 minutes after switching it on only learn, without alerts);
    ///  • a program moves more than N MB in one minute (upload + download);
    ///  • a program talks to an address on the abuse.ch Feodo Tracker list (servers that control known banking/botnet malware,
    ///    CC0 licence). The list is downloaded only when this alert is switched on, then once a day — that download is the
    ///    only thing SMD Win sends for these alerts; the traffic itself is never sent anywhere.
    /// They need the ETW monitor running, so while any alert is on it stays on in the background (a few % of one core under
    /// heavy traffic, close to nothing when idle).
    /// </summary>
    public static class NetAlerts
    {
        private const string BlocklistUrl = "https://feodotracker.abuse.ch/downloads/ipblocklist.txt";
        private static readonly string KnownFile = AppPaths.Data("net-known-apps.txt");
        private static readonly string BlocklistFile = AppPaths.Data("net-blocklist-feodo.txt");

        private static bool _subscribed;
        private static int _tickCount;
        private static HashSet<string>? _known;
        private static bool _knownDirty;
        private static DateTime _learnUntilUtc = DateTime.MinValue;
        private static HashSet<IPAddress> _blocklist = new();
        private static DateTime _blocklistLoadedUtc = DateTime.MinValue;
        private static readonly Dictionary<string, DateTime> _cooldown = new();

        public static bool AnyOn => SettingsService.Current.NetAlertNewApp || SettingsService.Current.NetAlertMbPerMin > 0
                                    || SettingsService.Current.NetAlertBlocklist;

        public static int BlocklistCount => _blocklist.Count;

        /// <summary>Applies the settings: starts or releases the monitor. Call at start-up and after any change.</summary>
        public static void Apply()
        {
            try
            {
                var mon = EtwNetMonitor.Instance;
                mon.KeepAlive = AnyOn;
                if (!AnyOn)
                {
                    if (_subscribed) { mon.Tick -= OnTick; _subscribed = false; }
                    SaveKnown();
                    return;
                }
                if (!_subscribed) { mon.Tick += OnTick; _subscribed = true; }
                if (SettingsService.Current.NetAlertNewApp && _known == null) LoadKnown();
                if (SettingsService.Current.NetAlertBlocklist) _ = RefreshBlocklistAsync(force: false);
                mon.EnsureStarted();
            }
            catch (Exception ex) { AppLogger.Warning(ex, "NetAlerts.Apply"); }
        }

        private static void OnTick()
        {
            if (++_tickCount % 5 != 0) return;   // every 5 s is enough for alerts
            try
            {
                var s = SettingsService.Current;
                var apps = EtwNetMonitor.Instance.Snapshot(withRemotes: s.NetAlertNewApp || s.NetAlertBlocklist,
                                                           withHistory: s.NetAlertMbPerMin > 0);
                foreach (var a in apps)
                {
                    string path = a.Pid == 4 ? "System" : ProcessMemoryInfo.GetImagePath(a.Pid) ?? "";
                    string name = a.Pid == 4 ? "System" : path.Length > 0 ? Path.GetFileNameWithoutExtension(path) : $"PID {a.Pid}";

                    if (s.NetAlertNewApp && path.Length > 0 && a.Remotes.Any(r => IsPublic(r.Address)))
                        CheckNewApp(path, name);

                    if (s.NetAlertMbPerMin > 0 && a.HistSend.Length > 0)
                    {
                        int n = Math.Min(60, a.HistSend.Length);
                        double kb = 0;
                        for (int i = a.HistSend.Length - n; i < a.HistSend.Length; i++) kb += a.HistSend[i] + a.HistRecv[i];
                        double mb = kb / 1024.0;
                        if (mb > s.NetAlertMbPerMin && Cool($"mb|{a.Pid}", TimeSpan.FromMinutes(10)))
                            NotificationCenter.Add(NotifCategory.Network, NotifSeverity.Warning,
                                $"{name} moved {mb:F0} MB in the last minute (limit {s.NetAlertMbPerMin} MB).");
                    }

                    if (s.NetAlertBlocklist && _blocklist.Count > 0)
                        foreach (var r in a.Remotes)
                            if (_blocklist.Contains(r.Address) && Cool($"bl|{path}|{r.Address}", TimeSpan.FromHours(1)))
                                NotificationCenter.Add(NotifCategory.Network, NotifSeverity.Critical,
                                    $"{name} connected to {r.Address}, an address on the Feodo Tracker list of malware control servers. " +
                                    "Check this program with an antivirus scan.");
                }
                if (_knownDirty && _tickCount % 30 == 0) SaveKnown();
                if (s.NetAlertBlocklist && DateTime.UtcNow - _blocklistLoadedUtc > TimeSpan.FromHours(24)) _ = RefreshBlocklistAsync(force: false);
            }
            catch (Exception ex) { AppLogger.Debug($"NetAlerts tick: {ex.Message}"); }
        }

        private static void CheckNewApp(string path, string name)
        {
            if (_known == null) LoadKnown();
            string key = path.ToLowerInvariant();
            if (_known!.Contains(key)) return;
            _known.Add(key);
            _knownDirty = true;
            if (DateTime.UtcNow < _learnUntilUtc) return;
            NotificationCenter.Add(NotifCategory.Network, NotifSeverity.Info,
                $"{name} used the internet for the first time on this PC ({path}).");
        }

        private static bool Cool(string key, TimeSpan span)
        {
            lock (_cooldown)
            {
                if (_cooldown.TryGetValue(key, out var t) && DateTime.UtcNow - t < span) return false;
                _cooldown[key] = DateTime.UtcNow;
                if (_cooldown.Count > 2000) _cooldown.Clear();
                return true;
            }
        }

        private static bool IsPublic(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip)) return false;
            var b = ip.GetAddressBytes();
            if (b.Length == 4)
                return !(b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                         || (b[0] == 169 && b[1] == 254) || b[0] >= 224 || b[0] == 0 || (b[0] == 100 && b[1] >= 64 && b[1] <= 127));
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC);
        }

        private static void LoadKnown()
        {
            _known = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(KnownFile))
                {
                    foreach (var l in File.ReadAllLines(KnownFile))
                        if (l.Trim().Length > 0) _known.Add(l.Trim().ToLowerInvariant());
                }
                else
                {
                    _learnUntilUtc = DateTime.UtcNow.AddMinutes(2);   // first time: learn what already runs, without alerts
                    AppLogger.Info("NetAlerts: no list of known programs yet — learning for 2 minutes");
                }
            }
            catch (Exception ex) { AppLogger.Debug($"NetAlerts load known: {ex.Message}"); }
        }

        private static void SaveKnown()
        {
            if (_known == null || !_knownDirty) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(KnownFile)!);
                File.WriteAllLines(KnownFile, _known.OrderBy(x => x));
                _knownDirty = false;
            }
            catch (Exception ex) { AppLogger.Debug($"NetAlerts save known: {ex.Message}"); }
        }

        /// <summary>Forgets the known programs: the next 2 minutes learn again.</summary>
        public static void ResetKnown()
        {
            _known = null; _knownDirty = false;
            try { if (File.Exists(KnownFile)) File.Delete(KnownFile); } catch (Exception ex) { AppLogger.Debug($"NetAlerts reset: {ex.Message}"); }
            if (SettingsService.Current.NetAlertNewApp) LoadKnown();
        }

        private static int _blocklistBusy;

        /// <summary>The saved list if it is less than a day old, otherwise a fresh download (the saved one stays if it fails).</summary>
        public static async Task RefreshBlocklistAsync(bool force)
        {
            if (System.Threading.Interlocked.Exchange(ref _blocklistBusy, 1) == 1) return;
            try
            {
                bool fresh = File.Exists(BlocklistFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(BlocklistFile) < TimeSpan.FromHours(24);
                if (force || !fresh)
                {
                    try
                    {
                        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                        http.DefaultRequestHeaders.UserAgent.ParseAdd("SMDWin");
                        string text = await http.GetStringAsync(BlocklistUrl).ConfigureAwait(false);
                        if (text.Contains('.'))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(BlocklistFile)!);
                            await File.WriteAllTextAsync(BlocklistFile, text).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) { AppLogger.Info($"NetAlerts: blocklist download failed ({ex.Message}) — keeping the saved copy"); }
                }
                var set = new HashSet<IPAddress>();
                if (File.Exists(BlocklistFile))
                    foreach (var line in await File.ReadAllLinesAsync(BlocklistFile).ConfigureAwait(false))
                    {
                        string l = line.Trim();
                        if (l.Length == 0 || l[0] == '#') continue;
                        if (IPAddress.TryParse(l.Split(',', ' ', '\t')[0], out var ip)) set.Add(ip);
                    }
                _blocklist = set;
                _blocklistLoadedUtc = DateTime.UtcNow;
                AppLogger.Info($"NetAlerts: {set.Count} addresses on the Feodo Tracker list");
            }
            catch (Exception ex) { AppLogger.Warning(ex, "NetAlerts.RefreshBlocklist"); }
            finally { System.Threading.Interlocked.Exchange(ref _blocklistBusy, 0); }
        }
    }
}
