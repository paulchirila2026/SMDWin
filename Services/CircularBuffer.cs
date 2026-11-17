
using System;
using System.Collections.Generic;

namespace SMDWin.Services
{
    /// <summary>
    /// Fixed-size ring buffer for chart data.
    /// Push O(1), Read O(1), no allocation after init, no GC pressure.
    /// Works with DrawChart through ToArray(). Empty slots = NaN.
    /// </summary>
    public sealed class CircularBuffer
    {
        private readonly float[] _buf;
        private int _head;        // next write position
        private int _count;       // items actually written (0..Capacity)

        public int Capacity => _buf.Length;
        public int Count => _count;

        public CircularBuffer(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _buf = new float[capacity];
            Array.Fill(_buf, float.NaN);
            _head = 0;
            _count = 0;
        }

        /// <summary>Adds a value. Overwrites the oldest when full.</summary>
        public void Push(float value)
        {
            _buf[_head] = value;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }

        /// <summary>Value `ago` positions back (0 = most recent).</summary>
        public float Get(int ago)
        {
            int idx = (_head - 1 - ago % Capacity + Capacity * 2) % Capacity;
            return _buf[idx];
        }

        /// <summary>Indexer: [0] = most recent, [Count-1] = oldest.</summary>
        public float this[int ago] => Get(ago);

        /// <summary>
        /// Returns an array in chronological order (oldest first).
        /// Slots without data are NaN (on the left) — DrawChart skips them, so the chart grows naturally from the right.
        /// </summary>
        public float[] ToArray()
        {
            var arr = new float[Capacity];
            for (int i = 0; i < Capacity; i++)
                arr[i] = _buf[(_head - Capacity + i + Capacity * 2) % Capacity];
            return arr;
        }

        /// <summary>Writes into an existing array — zero allocation while rendering.</summary>
        public void CopyTo(float[] dest)
        {
            int n = Math.Min(dest.Length, Capacity);
            for (int i = 0; i < n; i++)
                dest[i] = _buf[(_head - Capacity + i + Capacity * 2) % Capacity];
        }

        public float Min()
        {
            float m = float.MaxValue;
            foreach (var v in _buf) if (!float.IsNaN(v) && v < m) m = v;
            return m == float.MaxValue ? 0f : m;
        }

        public float Max()
        {
            float m = float.MinValue;
            foreach (var v in _buf) if (!float.IsNaN(v) && v > m) m = v;
            return m == float.MinValue ? 0f : m;
        }

        public float Average()
        {
            float sum = 0; int cnt = 0;
            foreach (var v in _buf) if (!float.IsNaN(v)) { sum += v; cnt++; }
            return cnt > 0 ? sum / cnt : 0f;
        }

        /// <summary>Clears the buffer (Reset Chart) — all slots become NaN.</summary>
        public void Clear() { Array.Fill(_buf, float.NaN); _head = 0; _count = 0; }

        /// <summary>
        /// Resizes — a new, empty buffer into which the existing data is pushed again in chronological order.
        /// The invariants (_head/_count) stay correct by construction.
        /// </summary>
        public CircularBuffer Resize(int newCapacity)
        {
            var nb = new CircularBuffer(newCapacity);
            int toCopy = Math.Min(Count, newCapacity);
            // Get(toCopy-1) = the oldest of those copied → Push in chronological order
            for (int i = toCopy - 1; i >= 0; i--)
                nb.Push(Get(i));
            return nb;
        }
    }
}
