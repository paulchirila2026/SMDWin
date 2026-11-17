using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SMDWin.Services
{
    /// <summary>
    /// Generates and plays WAV test tones for left/right speaker channel verification.
    /// Uses raw WAV synthesis — no external files or Media Foundation needed.
    /// Safe to call repeatedly; previous playback is stopped and disposed before each new play.
    /// </summary>
    public class AudioTestService : IDisposable
    {
        private System.Media.SoundPlayer? _player;
        private readonly object _lock = new();

        // ── Public API ────────────────────────────────────────────────────────

        public enum Channel { Left, Right, Both }


        /// <summary>Stops any currently playing tone immediately.</summary>
        public void Stop()
        {
            lock (_lock) { StopInternal(); }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void StopInternal()
        {
            try { _player?.Stop(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            DisposePlayer();
        }

        private void DisposePlayer()
        {
            try { _player?.Dispose(); } catch (System.Exception __ignored) { SMDWin.Services.AppLogger.Swallowed(__ignored); }
            _player = null;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                StopInternal();
            }
        }
    }
}
