using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Weva.Compiled {
    // Pooled int buffer used by SelectorIndex / SnapshotMatcher to avoid
    // allocating a fresh List<int> on every match call. NOT thread-safe by
    // itself; pair with [ThreadStatic] or a per-call instance for concurrent use.
    internal sealed class IntsBuffer : IReadOnlyList<int> {
        int[] data = new int[16];
        int count;

        public int Count => count;

        public int this[int index] => data[index];

        public void Reset() { count = 0; }

        public void Add(int v) {
            if (count == data.Length) Grow();
            data[count++] = v;
        }

        public void AddRange(IReadOnlyList<int> src) {
            int need = count + src.Count;
            if (need > data.Length) {
                int cap = data.Length;
                while (cap < need) cap *= 2;
                Array.Resize(ref data, cap);
            }
            for (int i = 0; i < src.Count; i++) data[count++] = src[i];
        }

        public void AddRange(ReadOnlySpan<int> src) {
            int need = count + src.Length;
            if (need > data.Length) {
                int cap = data.Length;
                while (cap < need) cap *= 2;
                Array.Resize(ref data, cap);
            }
            for (int i = 0; i < src.Length; i++) data[count++] = src[i];
        }

        public void SortAndDedup() {
            if (count <= 1) return;
            Array.Sort(data, 0, count);
            int w = 1;
            for (int r = 1; r < count; r++) {
                if (data[r] != data[r - 1]) data[w++] = data[r];
            }
            count = w;
        }

        public ReadOnlySpan<int> AsSpan() => new(data, 0, count);

        public IEnumerator<int> GetEnumerator() {
            for (int i = 0; i < count; i++) yield return data[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Grow() {
            Array.Resize(ref data, data.Length * 2);
        }
    }
}
