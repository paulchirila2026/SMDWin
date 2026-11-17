using System;
using System.Collections.Generic;

namespace SMDWin.Services
{
    /// <summary>
    /// History of a group of values with one point per second (60 minutes by default), for charts with a selectable time
    /// window. Readings may come at any rate: each <see cref="Push"/> covers the seconds since the previous one — a straight
    /// line between the two values, or a gap (NaN) after a long absence. So the time axis is real whatever the read rate, and
    /// a chart never shows invented values.
    /// Not thread-safe: used from the drawing thread.
    /// </summary>
    public sealed class SecondHistory
    {
        public const int DefaultCapacity = 3600;

        private readonly float[][] _data;
        private readonly int _capacity;
        private long _count;
        private DateTime _lastUtc = DateTime.MinValue;
        private double _carrySeconds;

        public SecondHistory(int series, int capacity = DefaultCapacity)
        {
            _capacity = Math.Max(2, capacity);
            _data = new float[Math.Max(1, series)][];
            for (int i = 0; i < _data.Length; i++) _data[i] = new float[_capacity];
        }

        public int SeriesCount => _data.Length;
        /// <summary>Cate secunde au fost scrise in total (indexul ultimei secunde = Count − 1).</summary>
        public long Count => _count;
        /// <summary>How many seconds are kept now (at most the capacity).</summary>
        public int Stored => (int)Math.Min(_count, _capacity);

        /// <summary>Adds a reading. Returns how many seconds it covered (0 = a second reading in the same second: replaces the
        /// last point). With <paramref name="gap"/> the interval up to it stays empty.</summary>
        public int Push(DateTime nowUtc, IReadOnlyList<float> values, bool gap = false, int maxInterpolated = 15)
        {
            int steps = 1;
            if (_lastUtc != DateTime.MinValue)
            {
                _carrySeconds += Math.Max(0, (nowUtc - _lastUtc).TotalSeconds);
                steps = (int)Math.Floor(_carrySeconds + 0.5);
                if (steps > _capacity) { steps = _capacity; _carrySeconds = 0; }
                else _carrySeconds -= steps;
            }
            _lastUtc = nowUtc;

            int n = Math.Min(values.Count, _data.Length);
            if (steps <= 0)
            {
                if (_count > 0)
                    for (int k = 0; k < n; k++) _data[k][(int)((_count - 1) % _capacity)] = values[k];
                return 0;
            }
            var prev = new float[n];
            for (int k = 0; k < n; k++) prev[k] = _count > 0 ? _data[k][(int)((_count - 1) % _capacity)] : float.NaN;
            for (int s = 0; s < steps; s++)
            {
                int idx = (int)(_count % _capacity);
                bool last = s == steps - 1;
                for (int k = 0; k < n; k++)
                    _data[k][idx] = gap && !last ? float.NaN : SeriesRuns.Fill(prev[k], values[k], s, steps, maxInterpolated);
                _count++;
            }
            return steps;
        }

        /// <summary>The last <paramref name="seconds"/> values of a series, oldest to newest (fewer if not that many were collected).</summary>
        public List<double> Window(int series, int seconds)
        {
            int n = Math.Min(Stored, Math.Max(0, seconds));
            var list = new List<double>(n);
            if (series < 0 || series >= _data.Length) return list;
            for (int i = n; i >= 1; i--) list.Add(_data[series][(int)((_count - i) % _capacity)]);
            return list;
        }

        /// <summary>The last value written (NaN if none).</summary>
        public float Last(int series)
            => _count == 0 || series < 0 || series >= _data.Length ? float.NaN : _data[series][(int)((_count - 1) % _capacity)];
    }
}
