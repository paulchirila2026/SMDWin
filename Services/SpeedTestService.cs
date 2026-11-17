using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    public class SpeedTestResult
    {
        public double DownloadMbps { get; set; }
        public double UploadMbps   { get; set; }
        public double PingMs       { get; set; }
        public double JitterMs     { get; set; }
        public string Server       { get; set; } = "";
        public bool   Success      { get; set; }
        public string Error        { get; set; } = "";
        public string Rating       => DownloadMbps switch
        {
            >= 500 => "⚡ Excelent",
            >= 100 => "🚀 Foarte bun",
            >= 25  => "✔ Bun",
            >= 5   => "⚠ Mediu",
            >= 1   => "🐢 Lent",
            _      => "❌ Fără conexiune"
        };
        public string RatingColor => DownloadMbps switch
        {
            >= 100 => "#22C55E",
            >= 25  => "#3B82F6",
            >= 5   => "#F59E0B",
            _      => "#EF4444"
        };
    }

    public class SpeedTestService : IDisposable
    {
        /// <summary>Durata fiecarei faze (download, upload), in secunde.</summary>
        public const double PhaseSeconds = 20.0;

        // Multiple CDN URLs for download test — fallback chain
        private static readonly string[] TestUrls =
        {
            "https://speed.cloudflare.com/__down?bytes=100000000",  // 100 MB Cloudflare (stops at the test time)
            "https://link.testfile.org/150MB",                       // fallback
            "https://proof.ovh.net/files/10Mb.dat",                 // 10 MB OVH
        };

        // Upload test endpoints
        private static readonly string[] UploadUrls =
        {
            "https://speed.cloudflare.com/__up",
            "https://httpbin.org/post",
        };

        private static readonly string[] PingHosts =
            { "8.8.8.8", "1.1.1.1", "8.8.4.4" };

        private readonly HttpClient _http = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        public async Task<SpeedTestResult> RunAsync(
            IProgress<string>? progress = null,
            CancellationToken ct = default,
            IProgress<double>? liveSpeed = null,
            IProgress<string>? phase = null)
        {
            var result = new SpeedTestResult();

            // ── Ping & Jitter ─────────────────────────────────────────────────
            progress?.Report("Measuring latency (ping)...");
            try
            {
                var pings = new double[8];
                using var ping = new Ping();
                int ok = 0;
                for (int i = 0; i < 8 && !ct.IsCancellationRequested; i++)
                {
                    var reply = await ping.SendPingAsync(PingHosts[i % PingHosts.Length], 1500);
                    if (reply.Status == IPStatus.Success)
                        pings[ok++] = reply.RoundtripTime;
                    await Task.Delay(120, ct);
                }

                if (ok > 0)
                {
                    var validPings = pings[..ok];
                    result.PingMs = Math.Round(validPings.Average(), 1);
                    // Real jitter (RFC 3550) = the mean |difference between CONSECUTIVE pings|, not the deviation from the mean
                    // (which underestimated the variation).
                    if (ok > 1)
                    {
                        double sumDelta = 0;
                        for (int j = 1; j < ok; j++)
                            sumDelta += Math.Abs(validPings[j] - validPings[j - 1]);
                        result.JitterMs = Math.Round(sumDelta / (ok - 1), 1);
                    }
                    else result.JitterMs = 0;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { result.PingMs = -1; }

            if (ct.IsCancellationRequested) return result;

            progress?.Report("Measuring download speed...");
            phase?.Report("download");
            const double TargetTestSeconds = PhaseSeconds;
            const int DownloadStreams = 4, UploadStreams = 3;

            {
                // several connections in parallel: a single one does not fill a fast line
                async Task DownloadWorker(CancellationToken t, Action<int> add)
                {
                    var buf = new byte[65536];
                    while (!t.IsCancellationRequested)
                    {
                        bool got = false;
                        foreach (var url in TestUrls)
                        {
                            if (t.IsCancellationRequested) return;
                            try
                            {
                                result.Server = new Uri(url).Host;
                                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, t);
                                response.EnsureSuccessStatusCode();
                                using var stream = await response.Content.ReadAsStreamAsync(t);
                                int read;
                                while ((read = await stream.ReadAsync(buf, t)) > 0) { add(read); got = true; }
                            }
                            catch (OperationCanceledException) { return; }   // the test time expired or the test was stopped
                            catch (Exception ex) { result.Error = ex.Message; }
                            if (got) break;   // the first server that answers stays in use
                        }
                        if (!got) return;     // no source answers: give up
                    }
                }

                var (bytes, seconds) = await RunStreamsAsync(DownloadStreams, TargetTestSeconds, DownloadWorker, liveSpeed, ct);
                if (bytes > 100_000 && seconds > 0.5)
                {
                    result.DownloadMbps = Math.Round(bytes * 8.0 / seconds / 1_000_000, 1);
                    result.Success = true;
                    liveSpeed?.Report(result.DownloadMbps); // valoare finala stabila
                }
            }

            if (!result.Success && string.IsNullOrEmpty(result.Error))
                result.Error = "Could not reach any test server.";

            if (result.Success && !ct.IsCancellationRequested)
            {
                progress?.Report("Measuring upload speed...");
                phase?.Report("upload");
                const int uploadChunkBytes = 4 * 1024 * 1024; // 4 MB per request, sent in blocks: the speed is reported while it uploads
                var uploadData = new byte[uploadChunkBytes];
                System.Random.Shared.NextBytes(uploadData); // variable seed — otherwise every test sent the same data

                async Task UploadWorker(CancellationToken t, Action<int> add)
                {
                    while (!t.IsCancellationRequested)
                    {
                        bool got = false;
                        foreach (var url in UploadUrls)
                        {
                            if (t.IsCancellationRequested) return;
                            long mine = 0;
                            try
                            {
                                using var content = new ProgressContent(uploadData, n => { mine += n; add(n); });
                                using var resp = await _http.PostAsync(url, content, t);
                            }
                            catch (OperationCanceledException) { return; }   // the test time ran out: what was sent until then counts
                            catch (Exception ex) { AppLogger.Debug($"SpeedTest upload failed: {ex.Message}"); }
                            if (mine > 100_000) { got = true; break; }
                        }
                        if (!got) return;
                    }
                }

                var (bytes, seconds) = await RunStreamsAsync(UploadStreams, TargetTestSeconds, UploadWorker, liveSpeed, ct);
                if (bytes > 100_000 && seconds > 0.3)
                {
                    result.UploadMbps = Math.Round(bytes * 8.0 / seconds / 1_000_000, 1);
                    liveSpeed?.Report(result.UploadMbps); // valoare finala stabila
                }
            }
            phase?.Report("done");

            return result;
        }

        /// <summary>
        /// Runs <paramref name="streams"/> connections in parallel for <paramref name="seconds"/> and reports the total speed
        /// every ~400 ms. Returns the bytes transferred and the real time.
        /// </summary>
        private static async Task<(long Bytes, double Seconds)> RunStreamsAsync(
            int streams, double seconds, Func<CancellationToken, Action<int>, Task> worker, IProgress<double>? live, CancellationToken ct)
        {
            long total = 0;
            var sw = Stopwatch.StartNew();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(seconds));
            void Add(int n) => Interlocked.Add(ref total, n);
            var all = Task.WhenAll(Enumerable.Range(0, streams).Select(_ => Task.Run(() => worker(limit.Token, Add))).ToArray());

            long last = 0;
            double lastMs = 0;
            while (!all.IsCompleted)
            {
                await Task.WhenAny(all, Task.Delay(400));
                double nowMs = sw.Elapsed.TotalMilliseconds;
                long cur = Interlocked.Read(ref total);
                double windowMs = nowMs - lastMs;
                if (windowMs >= 350 && !all.IsCompleted)
                {
                    live?.Report(Math.Round((cur - last) * 8.0 / (windowMs / 1000.0) / 1_000_000.0, 1));
                    last = cur;
                    lastMs = nowMs;
                }
            }
            await all;
            sw.Stop();
            return (Interlocked.Read(ref total), sw.Elapsed.TotalSeconds);
        }

        /// <summary>A request body sent in 64 KB blocks; <c>onWritten</c> receives how many bytes went out, so the speed shows while it rises.</summary>
        private sealed class ProgressContent : System.Net.Http.HttpContent
        {
            private const int Block = 64 * 1024;
            private readonly byte[] _data;
            private readonly Action<int> _onWritten;

            public ProgressContent(byte[] data, Action<int> onWritten)
            {
                _data = data;
                _onWritten = onWritten;
                Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            }

            protected override Task SerializeToStreamAsync(System.IO.Stream stream, System.Net.TransportContext? context)
                => WriteAsync(stream, default);

            protected override Task SerializeToStreamAsync(System.IO.Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
                => WriteAsync(stream, cancellationToken);

            private async Task WriteAsync(System.IO.Stream stream, CancellationToken ct)
            {
                for (int offset = 0; offset < _data.Length; offset += Block)
                {
                    int count = Math.Min(Block, _data.Length - offset);
                    await stream.WriteAsync(_data.AsMemory(offset, count), ct);
                    _onWritten(count);
                }
            }

            protected override bool TryComputeLength(out long length)
            {
                length = _data.Length;
                return true;
            }
        }

        // Continuous ping monitor — returns one result each ~1s
        public async Task ContinuousPingAsync(
            string host,
            Action<double> onResult,
            CancellationToken ct)
        {
            using var ping = new Ping();
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var reply = await ping.SendPingAsync(host, 1500);
                    NetworkSelfTraffic.ReportPingBytes(outBytes: 60, inBytes: reply.Status == IPStatus.Success ? 60 : 0);
                    onResult(reply.Status == IPStatus.Success ? reply.RoundtripTime : -1);
                }
                catch { onResult(-1); }
                await Task.Delay(1000, ct);
            }
        }

        public void Dispose() => _http.Dispose();
    }
}
