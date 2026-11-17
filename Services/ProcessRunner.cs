
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut, bool Cancelled);

    public static class ProcessRunner
    {
        public static string? ReadStdoutBounded(Process proc, int timeoutMs)
        {
            try
            {
                var read = proc.StandardOutput.ReadToEndAsync();
                if (read.Wait(timeoutMs)) return read.Result;

                AppLogger.Debug("ProcessRunner.ReadStdoutBounded: {Name} did not answer within {Ms} ms — stopped", SafeName(proc), timeoutMs);
                try { proc.Kill(entireProcessTree: true); }
                catch (Exception ex) { AppLogger.Debug("ProcessRunner.ReadStdoutBounded: kill failed: {Msg}", ex.Message); }
                _ = read.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                return null;
            }
            catch (Exception ex)
            {
                AppLogger.Debug("ProcessRunner.ReadStdoutBounded: {Msg}", ex.Message);
                return null;
            }
        }

        private static string SafeName(Process p)
        {
            try { return p.StartInfo.FileName; } catch (Exception) { return "proces"; }
        }

        /// <summary>
        /// Runs a process and returns the result. Does not throw on timeout/cancellation (reports them in ProcessResult);
        /// throws only if the process cannot be started (missing exe, access denied).
        /// </summary>
        public static async Task<ProcessResult> RunAsync(
            string fileName, string arguments, int timeoutMs, CancellationToken ct = default)
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start {fileName}.");

            Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
            Task<string> errTask = proc.StandardError.ReadToEndAsync();

            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, ct);
            bool timedOut = false, cancelled = false;
            try
            {
                await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = ct.IsCancellationRequested;
                timedOut = !cancelled;
                try { proc.Kill(entireProcessTree: true); }
                catch (Exception ex) { AppLogger.Debug("ProcessRunner: kill {File} failed: {Msg}", fileName, ex.Message); }
            }

            // After stopping, the streams close; no endless wait here either.
            var both = Task.WhenAll(outTask, errTask);
            bool drained = await Task.WhenAny(both, Task.Delay(3000)).ConfigureAwait(false) == both;
            string stdout = drained || outTask.IsCompletedSuccessfully ? outTask.Result : "";
            string stderr = drained || errTask.IsCompletedSuccessfully ? errTask.Result : "";
            if (!drained)
            {   // pending reads finish on their own when the pipes close — no unobserved exception
                _ = outTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                _ = errTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            }

            int exit = -1;
            try { if (proc.HasExited) exit = proc.ExitCode; }
            catch (InvalidOperationException ex) { AppLogger.Debug("ProcessRunner: {File} without an exit code: {Msg}", fileName, ex.Message); }

            return new ProcessResult(exit, stdout, stderr, timedOut, cancelled);
        }
    }
}
