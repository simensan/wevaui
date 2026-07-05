using System.Buffers;
using System.Collections.Generic;
using UnityEngine;
using Weva.Paint;

namespace Weva.Rendering.URP {
    // Per-subtree snapshot of UIQuadInstances that subtree contributed to the
    // batcher last frame. Painter splices these into the current frame's
    // batches instead of re-walking the subtree when none of its descendants
    // are dirty AND the parent's batcher state (transform / clip / opacity /
    // stencil / atlas slots) matches what was active at capture time.
    public sealed class BoxBatchSnapshot : Weva.Paint.IValidatedBoxBatchSnapshot {
        public struct Segment {
            public UIBatchKey Key;
            public UIQuadInstance[] Instances; // owned slice (possibly oversized)
            public int Count;
            public int AtlasIdSlot0;
            public int AtlasIdSlot1;
            public int AtlasIdSlot2;
            public int AtlasIdSlot3;
        }

        public readonly List<Segment> Segments = new List<Segment>(2);

        // Anchor at capture time (the box's absolute origin). Replay shifts
        // every instance's PosSize.xy by (currentAbsX-AnchorX, currentAbsY-
        // AnchorY) so a subtree whose layout position drifted without a
        // style.Version bump still composites at the right screen location.
        public double AnchorX { get; set; }
        public double AnchorY { get; set; }

        // Parent batcher state at capture. Replay fast-rejects on any mismatch
        // — those values are baked into per-instance TransformRow0/1/2 + Color
        // alpha + ClipRect slots, so re-using the cached instances under a
        // different parent context would draw with stale matrices.
        public Vector4 ParentTransformA;   // (A, B, Tx, _)
        public Vector4 ParentTransformB;   // (C, D, Ty, _)
        public Vector4 ParentClipRect;
        public float ParentOpacity;
        public int ParentStencilRef;
        public int ParentAtlas0;
        public int ParentAtlas1;
        public int ParentAtlas2;
        public int ParentAtlas3;
        public int TextAtlasIdentity;
        public long TextAtlasVersion;

        // Snapshot is invalidated if the subtree contains filter scopes —
        // those need scope-event replay too and we don't carry that.
        public bool ContainsFilterScopes { get; set; }
        public bool IsValid =>
            TextAtlasIdentity == SdfTextRendering.CurrentAtlasIdentity
            && TextAtlasVersion == SdfTextRendering.CurrentAtlasVersion;

        // Returns each segment's Instances buffer to the shared ArrayPool and
        // wipes the snapshot's own state. EndSubtreeCapture pulls fresh
        // buffers via ArrayPool<UIQuadInstance>.Shared.Rent so subsequent
        // captures cost zero array allocation in steady state. The wrapper
        // itself is pooled below.
        public void Reset() {
            for (int i = 0; i < Segments.Count; i++) {
                var s = Segments[i];
                if (s.Instances != null) {
                    // clearArray:false — the consumer wrote real instances
                    // over the rented slots and we track the active count
                    // via Segment.Count; the unused tail is overwritten
                    // before the next Replay, so zeroing on Return is just
                    // GC-pressure for nothing.
                    ArrayPool<UIQuadInstance>.Shared.Return(s.Instances, clearArray: false);
                    s.Instances = null;
                }
                s.Count = 0;
                Segments[i] = s;
            }
            Segments.Clear();
            ContainsFilterScopes = false;
            AnchorX = 0;
            AnchorY = 0;
            TextAtlasIdentity = 0;
            TextAtlasVersion = 0;
        }


        // Process-static pool. Capped to keep memory bounded; oversized pools
        // do drop the BoxBatchSnapshot to GC. Single-threaded — Weva's
        // render path runs on the main thread only.
        const int MaxPoolSize = 64;
        static readonly System.Collections.Generic.Stack<BoxBatchSnapshot> pool
            = new System.Collections.Generic.Stack<BoxBatchSnapshot>(MaxPoolSize);

        public static BoxBatchSnapshot Rent() {
            if (pool.Count > 0) {
                var snap = pool.Pop();
                snap.Reset();
                return snap;
            }
            return new BoxBatchSnapshot();
        }

        public static void Return(BoxBatchSnapshot snap) {
            if (snap == null) return;
            if (pool.Count >= MaxPoolSize) return;
            snap.Reset();
            pool.Push(snap);
        }

        // IBoxBatchSnapshot.Recycle implementation — routes back to the
        // static pool. Callers in the painter layer hold us via the
        // interface, so this is how they ask for return without
        // referencing Weva.Rendering.URP directly.
        public void Recycle() => Return(this);
    }
}
