using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    public class FirewallRule
    {
        public string Name        { get; set; } = "";
        public string AppPath     { get; set; } = "";
        public string AppName     { get; set; } = "";
        public string Direction   { get; set; } = "";  // Inbound / Outbound
        public string Action      { get; set; } = "";  // Allow / Block
        public string Protocol    { get; set; } = "";
        public string LocalPorts  { get; set; } = "";
        public string RemotePorts { get; set; } = "";
        public string Profile     { get; set; } = "";
        public bool   Enabled     { get; set; } = true;

        public string DisplayName => ExtractPackageName(Name) ?? Name;

        public string AppDisplayName => !string.IsNullOrEmpty(AppName) ? AppName : (ExtractPackageName(Name) ?? "—");

        private static string? ExtractPackageName(string raw)
        {
            if (string.IsNullOrEmpty(raw) || !raw.StartsWith("@{")) return null;
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"^@\{([^_?}]+)");
            return m.Success ? m.Groups[1].Value : null;
        }

        public string ActionColor => Action == "Allow" ? "#22C55E" : "#EF4444";
        public string DirectionIcon => Direction == "Inbound" ? "⬇" : "⬆";
        public string EnabledText => Enabled ? "✔" : "✘";
        public string EnabledColor => Enabled ? "#22C55E" : "#94A3B8";
        public string StatusLabel => Action == "Allow" ? "✔ Allow" : "🚫 Deny";
        public string ActionText  => Action == "Allow" ? "Allow" : "Block";
        public string ActionTip   => (Action == "Allow" ? "Allowed — click to block this rule" : "Blocked — click to allow this rule")
                                   + (Enabled ? "" : "\nThis rule is turned off in Windows (it does not apply)");
    }

    public class FirewallService
    {
        public async Task<List<FirewallRule>> GetRulesAsync(string filter = "")
        {
            return await Task.Run(() => GetRules(filter));
        }

        private static List<FirewallRule> GetRules(string filter)
        {
            var rules = new List<FirewallRule>();
            try
            {
                // Rules are read through PowerShell (Get-NetFirewallRule + the related filters): .NET/CIM properties with FIXED names,
                // whatever the Windows language (netsh translates its labels → impossible to parse on Windows in another language).
                // 3 bulk queries (not one per rule) + an in-memory join on InstanceID.
                const string script = @"
                    $ErrorActionPreference = 'SilentlyContinue'
                    $rules = Get-NetFirewallRule
                    $ports = Get-NetFirewallPortFilter
                    $apps  = Get-NetFirewallApplicationFilter
                    $portById = @{}
                    foreach ($p in $ports) { $portById[$p.InstanceID] = $p }
                    $appById = @{}
                    foreach ($a in $apps) { $appById[$a.InstanceID] = $a }
                    $out = foreach ($r in $rules) {
                        $p = $portById[$r.InstanceID]
                        $a = $appById[$r.InstanceID]
                        [PSCustomObject]@{
                            Name        = $r.DisplayName
                            Enabled     = ($r.Enabled.ToString() -eq 'True')
                            Direction   = $r.Direction.ToString()
                            Action      = $r.Action.ToString()
                            Profile     = $r.Profile.ToString()
                            Protocol    = if ($p) { $p.Protocol } else { '' }
                            LocalPort   = if ($p) { ($p.LocalPort  -join ',') } else { '' }
                            RemotePort  = if ($p) { ($p.RemotePort -join ',') } else { '' }
                            AppPath     = if ($a) { $a.Program } else { '' }
                        }
                    }
                    $out | ConvertTo-Json -Compress -Depth 3
                ";

                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -")
                {
                    RedirectStandardInput  = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                };
                using var proc = Process.Start(psi);
                if (proc == null) return rules;

                proc.StandardInput.Write(script);
                proc.StandardInput.Close();

                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                bool exited = proc.WaitForExit(20000);
                if (!exited) { try { proc.Kill(entireProcessTree: true); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); } return rules; }
                string output = stdoutTask.GetAwaiter().GetResult();
                _ = stderrTask.GetAwaiter().GetResult();

                rules = ParsePowerShellRules(output);

                // Apply filter
                if (!string.IsNullOrWhiteSpace(filter))
                {
                    string f = filter.ToLowerInvariant();
                    rules = rules.Where(r =>
                        r.Name.ToLowerInvariant().Contains(f) ||
                        r.AppName.ToLowerInvariant().Contains(f) ||
                        r.AppPath.ToLowerInvariant().Contains(f)).ToList();
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "FirewallService.GetRulesAsync"); }

            return rules
                .OrderBy(r => r.Action == "Allow" ? 0 : 1)
                .ThenBy(r => r.Direction)
                .ThenBy(r => r.Name)
                .ToList();
        }

        private static List<FirewallRule> ParsePowerShellRules(string json)
        {
            var rules = new List<FirewallRule>();
            if (string.IsNullOrWhiteSpace(json)) return rules;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                // ConvertTo-Json returns a single object (not an array) when there is only one rule — both forms are normalized.
                var root = doc.RootElement;
                IEnumerable<System.Text.Json.JsonElement> items = root.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? root.EnumerateArray()
                    : new[] { root };

                string S(System.Text.Json.JsonElement e, string prop) =>
                    e.TryGetProperty(prop, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                        ? v.GetString() ?? "" : "";
                bool B(System.Text.Json.JsonElement e, string prop) =>
                    e.TryGetProperty(prop, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;

                foreach (var e in items)
                {
                    string name = S(e, "Name");
                    if (string.IsNullOrEmpty(name)) continue;
                    string appPath = S(e, "AppPath");
                    var rule = new FirewallRule
                    {
                        Name        = name,
                        Enabled     = B(e, "Enabled"),
                        Direction   = S(e, "Direction"),
                        Action      = S(e, "Action"),
                        Protocol    = S(e, "Protocol"),
                        LocalPorts  = S(e, "LocalPort"),
                        RemotePorts = S(e, "RemotePort"),
                        Profile     = S(e, "Profile"),
                        AppPath     = appPath,
                    };
                    if (!string.IsNullOrEmpty(appPath) && !appPath.Equals("System", StringComparison.OrdinalIgnoreCase))
                    {
                        try { rule.AppName = System.IO.Path.GetFileNameWithoutExtension(appPath); }
                        catch { rule.AppName = appPath; }
                    }
                    rules.Add(rule);
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "FirewallService.ParsePowerShellRules"); }
            return rules;
        }


        /// <summary>Sets firewall rule action to Allow or Block via netsh.</summary>
        public async Task<bool> SetRuleActionAsync(string ruleName, bool allow)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string action = allow ? "allow" : "block";
                    var psi = new ProcessStartInfo("netsh",
                        $"advfirewall firewall set rule name=\"{ruleName}\" new action={action}")
                    {
                        UseShellExecute = true,
                        CreateNoWindow  = false,
                        WindowStyle     = ProcessWindowStyle.Hidden,
                        Verb            = "runas"
                    };
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit(8000);
                    return proc?.ExitCode == 0;
                }
                catch { return false; }
            });
        }

        // ── App-level block/unblock via Windows Firewall ──────────────────────

        // the rule name includes a fingerprint of the full path: two programs with the same .exe name no longer overwrite each other
        private static string RuleName(string appPath)
        {
            string full = appPath.Trim().ToLowerInvariant();
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full));
            return $"{BlockRulePrefix}{System.IO.Path.GetFileNameWithoutExtension(appPath)}_{Convert.ToHexString(hash, 0, 4)}";
        }
        // the older name (file name only) — for rules created by older versions
        private static string LegacyRuleName(string appPath)
            => $"{BlockRulePrefix}{System.IO.Path.GetFileNameWithoutExtension(appPath)}";

        /// <summary>Creates an outbound BLOCK rule for the given executable path.</summary>
        public async Task<bool> BlockAppAsync(string appPath)
        {
            if (string.IsNullOrWhiteSpace(appPath)) return false;
            return await Task.Run(() =>
            {
                try
                {
                    string name = RuleName(appPath);
                    // Remove any existing rule first
                    RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");
                    // Add new block rule
                    bool ok = RunNetsh(
                        $"advfirewall firewall add rule name=\"{name}\" dir=out action=block program=\"{appPath}\" enable=yes");
                    InvalidateBlockedCache();
                    return ok;
                }
                catch (Exception ex) { AppLogger.Warning(ex, "FirewallService.BlockApp"); return false; }
            });
        }

        /// <summary>Removes the SMDWin outbound BLOCK rule for the given executable.</summary>
        public async Task<bool> UnblockAppAsync(string appPath)
        {
            if (string.IsNullOrWhiteSpace(appPath)) return false;
            return await Task.Run(() =>
            {
                try
                {
                    string name = RuleName(appPath);
                    bool ok = RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");
                    ok |= RunNetsh($"advfirewall firewall delete rule name=\"{LegacyRuleName(appPath)}\"");   // rules created by older versions
                    InvalidateBlockedCache();
                    return ok;
                }
                catch (Exception ex) { AppLogger.Warning(ex, "FirewallService.UnblockApp"); return false; }
            });
        }

        private const string BlockRulePrefix = "SMDWin_Block_";
        private static readonly object _blockedLock = new();
        private static HashSet<string> _blockedNames = new(StringComparer.OrdinalIgnoreCase);
        private static DateTime _blockedLoadedUtc = DateTime.MinValue;

        /// <summary>Returns true if an SMDWin block rule exists for this executable.</summary>
        public bool IsAppBlocked(string appPath)
        {
            if (string.IsNullOrWhiteSpace(appPath)) return false;
            lock (_blockedLock)
            {
                if ((DateTime.UtcNow - _blockedLoadedUtc).TotalSeconds > 20)
                {
                    _blockedNames = LoadBlockedRuleNames();
                    _blockedLoadedUtc = DateTime.UtcNow;
                }
                return _blockedNames.Contains(RuleName(appPath)) || _blockedNames.Contains(LegacyRuleName(appPath));
            }
        }

        private static void InvalidateBlockedCache()
        {
            lock (_blockedLock) _blockedLoadedUtc = DateTime.MinValue;
        }

        // A single enumeration of the rules through the firewall API (in process), instead of one netsh per application.
        private static HashSet<string> LoadBlockedRuleNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (type == null) return names;
                dynamic policy = Activator.CreateInstance(type)!;
                foreach (dynamic rule in policy.Rules)
                {
                    string name = rule.Name ?? "";
                    if (name.StartsWith(BlockRulePrefix, StringComparison.OrdinalIgnoreCase)) names.Add(name);
                }
            }
            catch (Exception ex) { AppLogger.Warning(ex, "FirewallService: reading the SMDWin_Block rules failed"); }
            return names;
        }

        private static bool RunNetsh(string args)
        {
            var psi = new ProcessStartInfo("netsh", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute = false,
                CreateNoWindow  = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(8000);
            return proc?.ExitCode == 0;
        }
    }
}
