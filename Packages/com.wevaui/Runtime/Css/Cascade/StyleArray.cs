using System;
using Weva.Compiled;
using Weva.Dom;

namespace Weva.Css.Cascade {
    // NodeId-indexed parallel array of ComputedStyle pinned to a CascadeEngine
    // and reused across ComputeAll/Apply calls. Storage layout invariant: index
    // equals DomSnapshot NodeId. Entries for non-element nodes (Document, text)
    // stay null; out-of-range Get returns null instead of throwing so consumers
    // can probe a NodeId without bounds checking.
    //
    // The array survives the lifetime of the CascadeEngine — buckets aren't
    // freed when entries are cleared, so the next ComputeAll's per-element
    // ComputedStyle assignments hit existing capacity. Capacity grows only when
    // a snapshot wider than the current backing array arrives.
    public sealed class StyleArray {
        ComputedStyle[] data;
        int count;

        public StyleArray() : this(0) { }

        public StyleArray(int initialCapacity) {
            if (initialCapacity < 0) throw new ArgumentOutOfRangeException(nameof(initialCapacity));
            data = initialCapacity > 0 ? new ComputedStyle[initialCapacity] : Array.Empty<ComputedStyle>();
            count = 0;
        }

        public int Capacity => data.Length;
        public int Count => count;

        public ComputedStyle Get(int nodeId) {
            if ((uint)nodeId >= (uint)data.Length) return null;
            return data[nodeId];
        }

        public void Set(int nodeId, ComputedStyle style) {
            if (nodeId < 0) throw new ArgumentOutOfRangeException(nameof(nodeId));
            EnsureCapacity(nodeId + 1);
            data[nodeId] = style;
            if (nodeId >= count) count = nodeId + 1;
        }

        public void EnsureCapacity(int min) {
            if (min <= data.Length) return;
            int next = data.Length == 0 ? 16 : data.Length * 2;
            while (next < min) next *= 2;
            var fresh = new ComputedStyle[next];
            Array.Copy(data, fresh, data.Length);
            data = fresh;
        }

        public void Resize(int newCount) {
            if (newCount < 0) throw new ArgumentOutOfRangeException(nameof(newCount));
            if (newCount > data.Length) EnsureCapacity(newCount);
            else if (newCount < count) {
                Array.Clear(data, newCount, count - newCount);
            }
            count = newCount;
        }

        public void Clear() {
            if (count == 0) return;
            Array.Clear(data, 0, count);
            count = 0;
        }

        // Aligns the array to a freshly-built snapshot, dropping references for
        // node ids past the new node count and growing capacity if required.
        // Existing per-element ComputedStyle entries within range are preserved.
        internal void AlignTo(DomSnapshot snapshot) {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            int n = snapshot.NodeCount;
            if (n > data.Length) EnsureCapacity(n);
            else if (n < count) {
                Array.Clear(data, n, count - n);
            }
            count = n;
        }

        public ComputedStyle[] UnsafeBuffer => data;
    }
}
