using System;
using SMDWin.Views;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SMDWin;
using SMDWin.Models;
using SMDWin.Services;
using Forms = System.Windows.Forms;
using Application      = System.Windows.Application;
using MessageBox       = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage  = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using SaveFileDialog   = Microsoft.Win32.SaveFileDialog;
using ToolTip          = System.Windows.Controls.ToolTip;
using OpenFileDialog   = Microsoft.Win32.OpenFileDialog;
using Button           = System.Windows.Controls.Button;
using Brush            = System.Windows.Media.Brush;
using WpfColor         = System.Windows.Media.Color;
using WpfColorConv     = System.Windows.Media.ColorConverter;

namespace SMDWin.Views
{
    public partial class MainWindow
    {


        // ══ PUBLIC IP / NETWORK INFO ══════════════════════════════════════════

        private bool _publicIpLoaded = false;

        /// <summary>Called when Network tab is shown — load public IP once automatically.</summary>
        internal void EnsurePublicIpLoaded()
        {
            if (!_publicIpLoaded)
                _ = LoadPublicIpInfoAsync();
        }

        internal async void RefreshPublicIp_Click(object s, RoutedEventArgs e)
            => await LoadPublicIpInfoAsync();

        internal void CopyPublicIp_Click(object s, RoutedEventArgs e)
        {
            string ip = TxtPublicIp?.Text ?? "";
            if (!string.IsNullOrEmpty(ip) && ip != "—")
            {
                try { System.Windows.Clipboard.SetText(ip); }
                catch (Exception ex) { AppLogger.Debug(ex.Message); }
                if (TxtPublicStatus != null) TxtPublicStatus.Text = "✓ Copied to clipboard";
            }
        }

        private async Task LoadPublicIpInfoAsync()
        {
            if (TxtPublicIp    != null) TxtPublicIp.Text    = "Loading…";
            if (TxtPublicIsp   != null) TxtPublicIsp.Text   = "Loading…";
            if (TxtPublicLocation != null) TxtPublicLocation.Text = "Loading…";
            if (TxtPublicTimezone != null) TxtPublicTimezone.Text  = "Loading…";
            if (TxtPublicAsn   != null) TxtPublicAsn.Text   = "";
            if (TxtPublicStatus!= null) TxtPublicStatus.Text= "";
            if (PubIpVpnBadge  != null) PubIpVpnBadge.Visibility = Visibility.Collapsed;
            if (BtnRefreshPublicIpVisible != null) BtnRefreshPublicIpVisible.Visibility = Visibility.Collapsed;
            if (BtnCopyPublicIp != null) BtnCopyPublicIp.IsEnabled = true;
            if (TxtPublicStatus != null) TxtPublicStatus.ToolTip = null;

            var result = await FetchPublicIpFromIpwhoIsAsync();
            string? firstError = result.ErrorDetail;
            if (!result.Ok)
            {
                AppLogger.Warning(new Exception(firstError ?? "unknown"), "PublicIp: ipwho.is failed, trying fallback (ipapi.co)");
                result = await FetchPublicIpFromIpapiCoAsync();
            }

            if (!result.Ok)
            {
                if (TxtPublicIp != null) TxtPublicIp.Text = "—";
                if (TxtPublicIsp != null) TxtPublicIsp.Text = "—";
                if (TxtPublicLocation != null) TxtPublicLocation.Text = "—";
                if (TxtPublicTimezone != null) TxtPublicTimezone.Text = "—";
                if (TxtPublicStatus != null)
                {
                    TxtPublicStatus.Text = LanguageService.S("Network", "PublicIpUnavailable");
                    string combined = string.Join("  |  ", new[] { firstError, result.ErrorDetail }
                        .Where(s => !string.IsNullOrEmpty(s)));
                    TxtPublicStatus.ToolTip = combined.Length > 0 ? combined : (object?)null;
                }
                if (BtnCopyPublicIp != null) BtnCopyPublicIp.IsEnabled = false;
                if (BtnRefreshPublicIpVisible != null) BtnRefreshPublicIpVisible.Visibility = Visibility.Visible;
                AppLogger.Warning(new Exception(result.ErrorDetail ?? "unknown"), "PublicIp: both providers failed");
                return;
            }

            _publicIpLoaded = true;
            _lastPublicIp = result.Ip;

            if (TxtPublicIp       != null) TxtPublicIp.Text       = result.Ip;
            if (TxtPublicIsp      != null) TxtPublicIsp.Text      = result.Isp;
            if (TxtPublicLocation != null) TxtPublicLocation.Text = $"{result.City}, {result.Region}, {result.Country}";
            if (TxtPublicAsn      != null) TxtPublicAsn.Text      = result.Asn.Length > 40 ? result.Asn[..40] + "…" : result.Asn;
            if (TxtPublicTimezone != null) TxtPublicTimezone.Text = result.Timezone;
            if (TxtPublicStatus   != null) TxtPublicStatus.Text   = $"Last updated: {DateTime.Now:HH:mm:ss}"
                + (result.Provider == "ipapi.co" ? " (fallback source)" : "");

            // None of the providers used here offers proxy/hosting/VPN detection on the free tier — the badge always stays hidden
            // (more honest than a false negative).
            if (PubIpVpnBadge != null) PubIpVpnBadge.Visibility = Visibility.Collapsed;
        }

        private sealed class PublicIpResult
        {
            public bool   Ok       { get; set; }
            public string Provider { get; set; } = "";
            public string Ip       { get; set; } = "—";
            public string Isp      { get; set; } = "—";
            public string Asn      { get; set; } = "";
            public string City     { get; set; } = "—";
            public string Region   { get; set; } = "";
            public string Country  { get; set; } = "—";
            public string Timezone { get; set; } = "—";
            public string? ErrorDetail { get; set; }
        }

        /// <summary>
        /// Reads a JSON property as text, whatever its type: text → the value, number/bool → ToString,
        /// object → the <paramref name="objectField"/> field (if given), null / missing → <paramref name="fallback"/>.
        /// Never throws — IP providers change their format without warning.
        /// </summary>
        private static string JsonStr(System.Text.Json.JsonElement parent, string prop, string fallback, string? objectField = null)
        {
            try
            {
                if (parent.ValueKind != System.Text.Json.JsonValueKind.Object) return fallback;
                if (!parent.TryGetProperty(prop, out var el)) return fallback;
                switch (el.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.String:
                        var sv = el.GetString();
                        return string.IsNullOrWhiteSpace(sv) ? fallback : sv;
                    case System.Text.Json.JsonValueKind.Number:
                    case System.Text.Json.JsonValueKind.True:
                    case System.Text.Json.JsonValueKind.False:
                        return el.ToString();
                    case System.Text.Json.JsonValueKind.Object when objectField != null:
                        return JsonStr(el, objectField, fallback);
                    default:
                        return fallback;
                }
            }
            catch { return fallback; }
        }

        private async Task<PublicIpResult> FetchPublicIpFromIpwhoIsAsync()
        {
            try
            {
                using var cts = SMDWin.Services.SharedHttp.TimeoutCts(TimeSpan.FromSeconds(8));
                var http = SMDWin.Services.SharedHttp.Instance;

                string json = await http.GetStringAsync("https://ipwho.is/?fields=success,message,ip,country,region,city,timezone,latitude,longitude,connection", cts.Token);
                var data = System.Text.Json.JsonDocument.Parse(json).RootElement;

                bool success = data.TryGetProperty("success", out var succ) && succ.ValueKind == System.Text.Json.JsonValueKind.True;
                if (!success)
                {
                    string apiMsg = JsonStr(data, "message", "");
                    return new PublicIpResult { Ok = false, ErrorDetail = $"ipwho.is: {(apiMsg.Length > 0 ? apiMsg : "success=false")}" };
                }

                // ipwho.is returns "timezone" as an OBJECT ({"id":"Europe/Bucharest","utc":"+03:00",…}), not as text.
                // JsonStr accepts text, a number or an object (with the given field).
                var conn = data.TryGetProperty("connection", out var connEl) ? connEl : default;
                string ip       = JsonStr(data, "ip", "—");
                string isp      = JsonStr(conn, "isp", "—");
                string org      = JsonStr(conn, "org", "");
                string asnNum   = JsonStr(conn, "asn", "");
                string asn      = asnNum.Length > 0 ? $"AS{asnNum} {org}".Trim() : org;
                string country  = JsonStr(data, "country", "—");
                string region   = JsonStr(data, "region", "");
                string city     = JsonStr(data, "city", "—");
                string tzId     = JsonStr(data, "timezone", "—", objectField: "id");
                string tzUtc    = data.ValueKind == System.Text.Json.JsonValueKind.Object
                                  && data.TryGetProperty("timezone", out var tzEl) ? JsonStr(tzEl, "utc", "") : "";
                string tz       = tzUtc.Length > 0 && tzId != "—" ? $"{tzId} (UTC{tzUtc})" : tzId;

                return new PublicIpResult
                {
                    Ok = true, Provider = "ipwho.is", Ip = ip, Isp = isp, Asn = asn,
                    City = city, Region = region, Country = country, Timezone = tz
                };
            }
            catch (Exception ex)
            {
                string detail = $"{ex.GetType().Name}: {ex.Message}";
                if (ex.InnerException != null) detail += $" ← {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
                return new PublicIpResult { Ok = false, ErrorDetail = $"ipwho.is: {detail}" };
            }
        }

        private async Task<PublicIpResult> FetchPublicIpFromIpapiCoAsync()
        {
            try
            {
                using var cts = SMDWin.Services.SharedHttp.TimeoutCts(TimeSpan.FromSeconds(8));
                var http = SMDWin.Services.SharedHttp.Instance;
                string json = await http.GetStringAsync("https://ipapi.co/json/", cts.Token);
                var data = System.Text.Json.JsonDocument.Parse(json).RootElement;

                bool hasError = data.TryGetProperty("error", out var errEl)
                    && errEl.ValueKind == System.Text.Json.JsonValueKind.True;
                if (hasError)
                {
                    string reason = JsonStr(data, "reason", "");
                    return new PublicIpResult { Ok = false, ErrorDetail = $"ipapi.co: {(reason.Length > 0 ? reason : "error=true")}" };
                }

                string ip      = JsonStr(data, "ip",           "—");
                string isp     = JsonStr(data, "org",          "—");
                string asn     = JsonStr(data, "asn",          "");
                string country = JsonStr(data, "country_name", "—");
                string region  = JsonStr(data, "region",       "");
                string city    = JsonStr(data, "city",         "—");
                string tz      = JsonStr(data, "timezone",     "—", objectField: "id");

                return new PublicIpResult
                {
                    Ok = true, Provider = "ipapi.co", Ip = ip, Isp = isp, Asn = asn,
                    City = city, Region = region, Country = country, Timezone = tz
                };
            }
            catch (Exception ex)
            {
                string detail = $"{ex.GetType().Name}: {ex.Message}";
                if (ex.InnerException != null) detail += $" ← {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
                return new PublicIpResult { Ok = false, ErrorDetail = $"ipapi.co: {detail}" };
            }
        }
    }
}
