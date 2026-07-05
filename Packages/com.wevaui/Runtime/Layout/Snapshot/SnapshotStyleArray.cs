using System;
using System.Collections.Generic;
using Weva.Compiled;
using Weva.Css.Cascade;
using Weva.Dom;

namespace Weva.Layout {
    // Bridges the cascade's per-Element ComputedStyle map into a NodeId-indexed
    // array that the snapshot builder can read directly. Building this once per
    // Layout call costs O(N) and turns the per-element styleOf(int) call into a
    // raw array indexer.
    //
    // Caller patterns:
    //   var arr = SnapshotStyleArray.Build(snap, e => cascade.GetComposedStyle(e, ...));
    //   var builder = new SnapshotBoxBuilder(arr.At, pool, scratch);
    //
    // Non-element node ids resolve to null (consistent with parent inheritance
    // semantics in BoxBuilder.AppendNodeAsBlockChild for text nodes).
    //
    // Pooling note: callers that re-Build per Layout pass should reuse a single
    // SnapshotStyleArray instance via Refill, which grows the backing array only
    // when the snapshot expands. LayoutEngine holds one such instance per engine.
    internal sealed class SnapshotStyleArray {
        ComputedStyle[] data;
        int count;

        public SnapshotStyleArray() {
            data = Array.Empty<ComputedStyle>();
        }

        SnapshotStyleArray(ComputedStyle[] data) {
            this.data = data;
            this.count = data.Length;
        }

        public ComputedStyle At(int nodeId) {
            if ((uint)nodeId >= (uint)count) return null;
            return data[nodeId];
        }

        public int Length => count;

        // Refills the array from a freshly-built snapshot, reusing the backing
        // buffer when the snapshot fits inside the existing capacity. This is
        // the zero-alloc path for steady-state Layout calls — the per-Layout
        // ComputedStyle[] alloc was the dominant residual after DomSnapshot
        // pooling landed.
        public void Refill(DomSnapshot snap, Func<Element, ComputedStyle> styleOf) {
            if (snap == null) throw new ArgumentNullException(nameof(snap));
            if (styleOf == null) throw new ArgumentNullException(nameof(styleOf));
            int n = snap.NodeCount;
            EnsureCapacity(n);
            // Wipe the live prefix before refill so a NodeId that flipped
            // Element->Text between passes doesn't leak a stale style.
            if (count > 0) Array.Clear(data, 0, count);
            count = n;
            for (int i = 0; i < n; i++) {
                if (snap.Kinds[i] != NodeKind.Element) continue;
                if (snap.ManagedNodes[i] is Element e) data[i] = styleOf(e);
            }
        }

        public void RefillFromMap(DomSnapshot snap, IReadOnlyDictionary<Element, ComputedStyle> map) {
            if (snap == null) throw new ArgumentNullException(nameof(snap));
            if (map == null) throw new ArgumentNullException(nameof(map));
            int n = snap.NodeCount;
            EnsureCapacity(n);
            if (count > 0) Array.Clear(data, 0, count);
            count = n;
            for (int i = 0; i < n; i++) {
                if (snap.Kinds[i] != NodeKind.Element) continue;
                if (snap.ManagedNodes[i] is Element e && map.TryGetValue(e, out var cs)) data[i] = cs;
            }
        }

        void EnsureCapacity(int min) {
            int cap = data.Length;
            if (cap >= min) return;
            int next = cap == 0 ? 16 : cap * 2;
            while (next < min) next *= 2;
            Array.Resize(ref data, next);
        }

        public static SnapshotStyleArray Build(DomSnapshot snap, Func<Element, ComputedStyle> styleOf) {
            if (snap == null) throw new ArgumentNullException(nameof(snap));
            if (styleOf == null) throw new ArgumentNullException(nameof(styleOf));
            int n = snap.NodeCount;
            var arr = new ComputedStyle[n];
            for (int i = 0; i < n; i++) {
                if (snap.Kinds[i] != NodeKind.Element) continue;
                if (snap.ManagedNodes[i] is Element e) arr[i] = styleOf(e);
            }
            return new SnapshotStyleArray(arr);
        }

        public static SnapshotStyleArray FromMap(DomSnapshot snap, IReadOnlyDictionary<Element, ComputedStyle> map) {
            if (snap == null) throw new ArgumentNullException(nameof(snap));
            if (map == null) throw new ArgumentNullException(nameof(map));
            int n = snap.NodeCount;
            var arr = new ComputedStyle[n];
            for (int i = 0; i < n; i++) {
                if (snap.Kinds[i] != NodeKind.Element) continue;
                if (snap.ManagedNodes[i] is Element e && map.TryGetValue(e, out var cs)) arr[i] = cs;
            }
            return new SnapshotStyleArray(arr);
        }
    }
}
