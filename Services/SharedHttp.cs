using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    public static class SharedHttp
    {
        // UseProxy = true (the .NET default) uses the proxy configured in Windows, like any other program —
        // on networks with a mandatory proxy (schools, companies) the online features would otherwise fail without explanation.
        private static readonly HttpClientHandler _handler = new() { UseProxy = true };

        public static readonly HttpClient Instance = new(_handler)
        {
            // safety net for a caller that forgets to use a CancellationToken with its own timeout — it would no longer hang forever.
            // NOTE: HttpClient.Timeout applies to the whole request (including downloading the response body) and is the EFFECTIVE
            // limit if it is lower than a caller's CTS — so the value must stay above the longest legitimate case (the PawnIO
            // download, 180 s), not 30 s as a "typical" timeout would suggest. Current callers already use TimeoutCts() with their
            // own, stricter limits (3-180 s); this value does not change their behaviour, it only caps the case where someone
            // forgets their own CTS.
            Timeout = TimeSpan.FromMinutes(5),
        };

        static SharedHttp()
        {
            Instance.DefaultRequestHeaders.UserAgent.ParseAdd("SMDWin/1.0 (+https://github.com)");
        }

        /// <summary>Creates a CancellationTokenSource with the requested timeout, also linked to an optional external token
        /// (e.g. a CancellationToken from the caller, which must be honoured in addition to the own timeout).</summary>
        public static CancellationTokenSource TimeoutCts(TimeSpan timeout, CancellationToken external = default)
        {
            var cts = external == default
                ? new CancellationTokenSource()
                : CancellationTokenSource.CreateLinkedTokenSource(external);
            cts.CancelAfter(timeout);
            return cts;
        }

        /// <summary>Simple GET with a dedicated timeout, without creating a new HttpClient instance.</summary>
        public static async Task<string> GetStringAsync(string url, TimeSpan timeout, CancellationToken external = default)
        {
            using var cts = TimeoutCts(timeout, external);
            return await Instance.GetStringAsync(url, cts.Token).ConfigureAwait(false);
        }
    }
}
