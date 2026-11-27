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
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
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
using WpfPoint         = System.Windows.Point;
using WpfColorConv     = System.Windows.Media.ColorConverter;
using Line             = System.Windows.Shapes.Line;
using Ellipse          = System.Windows.Shapes.Ellipse;
using Rectangle        = System.Windows.Shapes.Rectangle;
using Polygon          = System.Windows.Shapes.Polygon;

namespace SMDWin.Views
{
    public partial class MainWindow
    {
        // ══ Network B: the connection chain PC → adapter → router → DNS → internet ══════════
        // Every 10 s (5 ticks of the page timer): ping to the router, DNS resolution, ping to the internet.
        // The first step that fails explains the problem in one sentence.

        private int _netChainTick;
        private bool _netChainBusy;

        private enum HopState { Unknown, Ok, Warn, Fail }

        private async Task CheckNetChainAsync()
        {
            if (_netChainBusy || NetHop0Ring == null) return;
            _netChainBusy = true;
            try
            {
                var cards = (AdapterCardsPanel?.ItemsSource as IEnumerable<AdapterCardVm>)?.ToList() ?? new List<AdapterCardVm>();
                var active = cards.FirstOrDefault(c => c.HasGateway) ?? cards.FirstOrDefault(c => c.Status == "Up");

                // 0 · this PC
                SetHop(0, active != null ? HopState.Ok : HopState.Fail, active?.IpAddress ?? "no IP address", active != null ? "OK" : "—");
                // 1 · the adapter used
                if (active == null)
                {
                    SetHop(1, HopState.Fail, "not connected", "—");
                    for (int i = 2; i <= 4; i++) SetHop(i, HopState.Unknown, "—", "—");
                    SetChainVerdict(HopState.Fail, "Not connected", "This PC is not connected to any network — check the cable or turn on Wi-Fi.");
                    return;
                }
                NetHop1Name.Text = active.Type is "Wi-Fi" or "Ethernet" ? active.Type : "Adapter";
                string sub1 = !string.IsNullOrEmpty(active.Ssid) ? $"{active.Ssid}{(string.IsNullOrEmpty(active.Signal) ? "" : " · " + active.Signal)}" : active.Name;
                bool weakWifi = int.TryParse(active.Signal.TrimEnd('%'), out int sig) && sig > 0 && sig < 35;
                SetHop(1, weakWifi ? HopState.Warn : HopState.Ok, sub1, active.Speed);

                // 2 · router, 3 · DNS, 4 · internet — in paralel
                string gw = active.HasGateway ? active.Gateway.Split(',')[0].Trim() : "";
                string dnsSrv = active.Dns.Split(',', ' ').FirstOrDefault(x => x.Length > 0 && x != "—") ?? "";
                var tRouter = gw.Length > 0 ? PingMedianMsAsync(gw, 1500) : Task.FromResult<long?>(null);
                var tDns    = ResolveMsAsync("www.msftconnecttest.com", 3000);
                var tNet    = PingMedianMsAsync("1.1.1.1", 2000);
                string netHost = "1.1.1.1";
                await Task.WhenAll(tRouter, tDns, tNet);
                long? router = tRouter.Result, dns = tDns.Result, net = tNet.Result;
                if (net == null) { net = await PingMedianMsAsync("8.8.8.8", 2000); netHost = "8.8.8.8"; }

                HopState sRouter = gw.Length == 0 ? HopState.Fail : router == null ? HopState.Fail : router > 50 ? HopState.Warn : HopState.Ok;
                HopState sDns    = dns == null ? HopState.Fail : dns > 300 ? HopState.Warn : HopState.Ok;
                HopState sNet    = net == null ? HopState.Fail : net > 120 ? HopState.Warn : HopState.Ok;
                SetHop(2, sRouter, gw.Length > 0 ? gw : "no gateway", router != null ? $"{router} ms" : "no reply");
                SetHop(3, sDns, dnsSrv.Length > 0 ? dnsSrv : "DNS", dns != null ? $"{dns} ms" : "failed");
                // under the circle is the address that was measured (not the public IP, which is not the one pinged)
                SetHop(4, sNet, netHost, net != null ? $"{net} ms" : "no reply");

                // the verdict: the first step that does not work
                if (gw.Length == 0 || sRouter == HopState.Fail)
                    SetChainVerdict(HopState.Fail, "Router not answering",
                        gw.Length == 0 ? "The adapter has no gateway (router address) — the network did not give this PC a proper address. Try Release / renew IP."
                                       : "The router does not answer — restart the router, or check the cable / Wi-Fi signal.");
                else if (sNet == HopState.Fail && sDns == HopState.Fail)
                    SetChainVerdict(HopState.Fail, "No internet", "The router works but there is no internet — the provider or the router's internet connection is down.");
                else if (sDns == HopState.Fail)
                    SetChainVerdict(HopState.Fail, "Names don't resolve", "The internet answers, but website names can't be found — try Flush DNS, or set another DNS (for example 1.1.1.1).");
                else if (sNet == HopState.Fail)
                    SetChainVerdict(HopState.Warn, "Ping blocked", "Websites resolve, but the internet test address does not answer to ping — often a firewall on the network; pages may still work.");
                else if (sNet == HopState.Warn || sRouter == HopState.Warn || sDns == HopState.Warn || weakWifi)
                    SetChainVerdict(HopState.Warn, "Works · slow",
                        weakWifi ? $"Wi-Fi signal is weak ({active.Signal}) — move closer to the router or use a cable."
                        : sRouter == HopState.Warn ? $"The router answers slowly ({router} ms) — it may be overloaded or the Wi-Fi is congested."
                        : sDns == HopState.Warn ? $"DNS answers slowly ({dns} ms) — another DNS server may be faster."
                        : $"The internet answers slowly ({net} ms) — the network or the provider is busy. Nothing to fix on this PC.");
                else
                    SetChainVerdict(HopState.Ok, "Everything works", $"Router {router} ms · DNS {dns} ms · internet {net} ms.");
            }
            catch (Exception ex) { AppLogger.Warning(ex, "CheckNetChainAsync"); }
            finally { _netChainBusy = false; }
        }

        /// <summary>
        /// The typical time to an address: 4 pings in a row, the first one ignored (on Wi-Fi it includes waking the card from
        /// power saving), then the median of the successful ones. A single ping gave values like 167 ms to the router while the
        /// ping to the internet was 40 ms.
        /// </summary>
        private static async Task<long?> PingMedianMsAsync(string host, int timeoutMs)
        {
            long? first = await PingMsAsync(host, timeoutMs);
            var ok = new List<long>();
            for (int i = 0; i < 3; i++)
            {
                if (await PingMsAsync(host, timeoutMs) is long ms) ok.Add(ms);
                else if (first == null && ok.Count == 0) return null;   // no reply at all: do not wait for 2 more timeouts
            }
            if (ok.Count == 0) return first;
            ok.Sort();
            return ok[ok.Count / 2];
        }

        private static async Task<long?> PingMsAsync(string host, int timeoutMs)
        {
            try
            {
                using var p = new System.Net.NetworkInformation.Ping();
                var r = await p.SendPingAsync(host, timeoutMs);
                return r.Status == System.Net.NetworkInformation.IPStatus.Success ? Math.Max(1, r.RoundtripTime) : null;
            }
            catch (Exception ex) { AppLogger.Debug($"PingMsAsync {host}: {ex.Message}"); return null; }
        }

        private static async Task<long?> ResolveMsAsync(string host, int timeoutMs)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var t = System.Net.Dns.GetHostAddressesAsync(host);
                if (await Task.WhenAny(t, Task.Delay(timeoutMs)) != t) return null;
                var addrs = await t;
                return addrs.Length > 0 ? Math.Max(1, sw.ElapsedMilliseconds) : null;
            }
            catch (Exception ex) { AppLogger.Debug($"ResolveMsAsync {host}: {ex.Message}"); return null; }
        }

        private static string HopBrushKey(HopState s) => s switch
        {
            HopState.Ok => "StatusOkBrush", HopState.Warn => "StatusWarningBrush", HopState.Fail => "StatusCritBrush", _ => "BorderBrush2"
        };

        private void SetHop(int i, HopState s, string sub, string val)
        {
            if (FindName($"NetHop{i}Ring") is Border ring) ring.SetResourceReference(Border.BorderBrushProperty, HopBrushKey(s));
            if (FindName($"NetHop{i}Sub") is TextBlock tSub) tSub.Text = sub;
            if (FindName($"NetHop{i}Val") is TextBlock tVal)
            {
                tVal.Text = val;
                if (s == HopState.Unknown) tVal.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                else tVal.SetResourceReference(TextBlock.ForegroundProperty, HopBrushKey(s));
            }
            // the link from the previous step takes the step's colour
            if (i > 0 && FindName($"NetLink{i}") is Border link) link.SetResourceReference(Border.BackgroundProperty, HopBrushKey(s));
        }

        private void SetChainVerdict(HopState s, string title, string note)
        {
            // the fixed titles and explanations are translated through PageText (key = the English text)
            title = SMDWin.Services.LiteralTexts.Translate(title);
            note = SMDWin.Services.LiteralTexts.Translate(note);
            TxtNetChainStatus.Text = title;
            TxtNetChainStatus.SetResourceReference(TextBlock.ForegroundProperty, HopBrushKey(s));
            NetChainDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, HopBrushKey(s));
            // The explanation line has a fixed place in the header (one line): when everything works, the times are already under
            // each step, so the neutral text stays; on a problem the explanation appears, with the full text in the tooltip.
            bool problem = s != HopState.Ok && !string.IsNullOrWhiteSpace(note);
            TxtNetChainNote.Text = problem ? note
                : LanguageService.S("Network", "ChainCheckedEvery", "Each step is checked every 10 seconds");
            TxtNetChainNote.ToolTip = string.IsNullOrWhiteSpace(note) ? null : note;
            TxtNetChainNote.SetResourceReference(TextBlock.ForegroundProperty, problem ? "TextPrimaryBrush" : "TextSecondaryBrush");
            TxtNetChainStatus.ToolTip = string.IsNullOrWhiteSpace(note) ? null : note;
        }

        // The TRAFFIC chart: download (accent) + upload (grey), from the history kept in the background (NetworkTrafficSampler),
        // so it does not start from zero on every page open and has the same time axis whatever the rate in Settings. The time
        // window is chosen with a right click on the chart (5 / 10 / 30 / 60 min); on long windows a point covers several
        // seconds and keeps their peak. The Download / Upload values are the last read.
        private static readonly int[] NetTrafficWindows = { 300, 600, 1800, 3600 };
        private bool _netTrafficChartReady;
        private long _netLastBucket = long.MinValue;

        private static int NetTrafficWindowSec()
        {
            int w = SettingsService.Current.NetTrafficWindowSec;
            return Array.IndexOf(NetTrafficWindows, w) >= 0 ? w : 300;
        }

        private void EnsureNetTrafficChartSetup()
        {
            if (_netTrafficChartReady) return;
            _netTrafficChartReady = true;
            // rounded upper limit + its label on the chart: shows how much a peak means
            NetTrafficChart.NiceMax = peak => SMDWin.Services.ChartScale.NiceRateMaxKBs(peak, 500);   // at least 500 KB/s (4 Mbit/s): a few hundred KB/s of background traffic does not fill half the chart
            NetTrafficChart.ScaleText = SMDWin.Services.ChartScale.FormatRateKBs;
            ChartRangeMenu.Attach(NetTrafficChart, NetTrafficWindows, NetTrafficWindowSec, sec =>
            {
                SettingsService.Current.NetTrafficWindowSec = sec;
                try { SettingsService.Save(); } catch (Exception ex) { AppLogger.Warning(ex, "NetTrafficWindow save"); }
                _netLastBucket = long.MinValue;
                ApplyNetTrafficWindowTexts();
                RenderNetTraffic();
            });
            ApplyNetTrafficWindowTexts();
        }

        /// <summary>The card subtitle and the "Peak" label, by the chosen time window and the current language.</summary>
        private void ApplyNetTrafficWindowTexts()
        {
            string window = SMDWin.Services.ChartScale.FormatWindow(NetTrafficWindowSec());
            if (TxtNetTrafficSub != null)
            {
                TxtNetTrafficSub.Text = LanguageService.S("Network", "TrafficAllAdapters", "All adapters")
                    + " · " + LanguageService.S("Common", "ChartLast", "Last").ToLowerInvariant() + " " + window;
                TxtNetTrafficSub.ToolTip = LanguageService.S("Common", "ChartRangeTip", "Right-click to change the time range");
            }
            if (FindName("TxtNetTrafficPeakLbl") is TextBlock peakLbl)
                peakLbl.Text = LanguageService.S("Network", "TrafficPeak", "Peak") + " (" + window + ")";
        }

        private void RenderNetTraffic()
        {
            try
            {
                EnsureNetTrafficChartSetup();
                int window = NetTrafficWindowSec();
                var (down, up, endSecond) = SMDWin.Services.NetworkTrafficSampler.HistoryPerSecond(window);
                if (down.Count == 0) return;

                int bucket = SMDWin.Services.SeriesRuns.BucketSecondsFor(window);
                var downPts = SMDWin.Services.SeriesRuns.Buckets(down, endSecond, bucket);
                var upPts = SMDWin.Services.SeriesRuns.Buckets(up, endSecond, bucket);
                int capacity = (int)Math.Ceiling(window / (double)bucket);
                if (NetTrafficChart.Capacity != capacity) NetTrafficChart.Capacity = capacity;

                // how many new points appeared since the previous drawing → how far the chart slides
                long lastBucket = SMDWin.Services.SeriesRuns.BucketOf(endSecond, bucket);
                int newPts = _netLastBucket == long.MinValue ? 0 : (int)Math.Clamp(lastBucket - _netLastBucket, 0, 10);
                _netLastBucket = lastBucket;
                NetTrafficChart.SetData(downPts, upPts, newPts);

                static void Show(TextBlock? num, TextBlock? unit, double kbs)
                {
                    if (num != null) num.Text = kbs >= 1024 ? $"{kbs / 1024.0:F1}" : $"{kbs:F0}";
                    if (unit != null) unit.Text = kbs >= 1024 ? "MB/s" : "KB/s";
                }
                Show(TxtNetOverviewDown, TxtNetOverviewDownUnit, down[^1]);
                Show(TxtNetOverviewUp, TxtNetOverviewUpUnit, up[^1]);
                Show(TxtNetTrafficPeak, TxtNetTrafficPeakUnit, down.Max());
            }
            catch (Exception ex) { AppLogger.Debug(ex.Message); }
        }

    }
}
