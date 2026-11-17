using System;
using System.Collections.Generic;

namespace SMDWin.Views
{
    /// <summary>
    /// Minimum and maximum of a quantity over the last N seconds (the chart window), for the row under the dials.
    /// Samples older than the window are dropped; with a single sample, minimum and maximum are equal.
    /// </summary>
    internal sealed class DashRangeTracker
    {
        private readonly Queue<(long Ms, float Value)> _samples = new();

        public void Add(float value, int windowSeconds)
        {
            if (!float.IsFinite(value)) return;
            long now = Environment.TickCount64;
            _samples.Enqueue((now, value));
            long cutoff = now - Math.Max(10, windowSeconds) * 1000L;
            while (_samples.Count > 0 && _samples.Peek().Ms < cutoff) _samples.Dequeue();
        }

        public bool TryGet(out float min, out float max)
        {
            min = float.MaxValue; max = float.MinValue;
            foreach (var (_, v) in _samples)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }
            return _samples.Count > 0;
        }
    }
}
