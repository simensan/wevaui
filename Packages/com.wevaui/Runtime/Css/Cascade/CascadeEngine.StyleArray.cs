using System;
using System.Collections.Generic;
using Weva.Compiled;
using Weva.Dom;
using Weva.Reactive;

namespace Weva.Css.Cascade {
    // Allocation-stable result map + NodeId-indexed style array. Held on the
    // engine across ComputeAll/Apply calls so warm-cache iterations don't pay
    // a fresh Dictionary<Element, ComputedStyle> on each pass — the same
    // instance is cleared and re-populated. The dict keeps its bucket array
    // even after Clear(), so re-inserts hit existing storage.
    //
    // Snapshot reuse: the engine subscribes to Document.Mutated and flips
    // snapshotDirty on any mutation. ComputeAll skips the DomSnapshot rebuild
    // when the same document arrives unchanged; the snapshotBuildCount stat
    // still increments to keep observability of "logical build per call".
    //
    // Invariants:
    //   * resultMap.Count <= styleArray.Count (modulo non-element nodes which
    //     occupy NodeIds in styleArray but never appear in resultMap).
    //   * styleArray.Get(nodeId) is non-null iff the node at nodeId is an
    //     element AND ComputeAll has populated it on the current pass.
    //   * Both fields are reset on InvalidateAll alongside the legacy cache.
    //   * Apply(tracker) drops dirty entries from resultMap and the legacy
    //     cache but does NOT touch styleArray; consumers must not read
    //     styleArray between an Apply call and the next ComputeAll. The
    //     ComputeAll walk overwrites stale styleArray entries.
    public sealed partial class CascadeEngine {
        readonly Dictionary<Element, ComputedStyle> resultMap = new(256);
        readonly StyleArray styleArray = new(256);
        // Reusable scratch list reused by Apply(InvalidationTracker) to avoid
        // a fresh List<Element> allocation each call.
        readonly List<Element> applyDropScratch = new(32);

        // Snapshot reuse state. attachedDoc is the document whose Mutated
        // event we currently subscribe to; if a different document arrives,
        // we re-subscribe. snapshotDirty is set whenever the doc mutates
        // (or we haven't built a snapshot yet) so the next ComputeAll
        // rebuilds.
        //
        // treeShapeDirty is a stricter cousin: it tracks only mutations that
        // change tree shape (child added/removed). Such mutations can invalidate
        // selectors the per-element cache can't notice on its own — nth-child
        // index, descendant relationships, sibling combinators — so the
        // incremental cascade has to fall back to a full Walk. Attribute-only
        // mutations DON'T set this; they require a snapshot refill (the
        // snapshot caches interned attribute values) but the dirty element is
        // already in the tracker's dirty set, and ComputeOrHit's per-element
        // Version bump catches the re-cascade naturally. Siblings unaffected
        // by the attribute change keep their cached entries.
        //
        // Edge case: sibling combinators on attribute selectors (`.A.flag +
        // .B`) are NOT covered by this distinction — flipping A's class
        // should re-resolve B, but B's cache key doesn't shift. This is a
        // pre-existing limitation of Apply(tracker) and the per-element
        // digest path; the incremental cascade inherits it. Workaround for
        // affected stylesheets is to avoid attribute-sibling combinators or
        // mark the sibling dirty manually via the tracker.
        Document attachedDoc;
        bool snapshotDirty = true;
        bool treeShapeDirty = true;
        Action<DomMutation> snapshotInvalidator;

        // Per-node dirty set for incremental snapshot refresh. Populated by
        // attribute/text mutations between cascade passes; processed in
        // ComputeAllIncremental by calling DomSnapshot.RefreshNode for
        // each entry instead of doing a full O(N) Refill. Tree-shape
        // mutations skip the set and force the full path.
        readonly HashSet<Node> snapshotDirtyNodes = new();

        // Persistent NodeId-indexed view of the most recent ComputeAll output.
        // Caller-facing — paired with LastSnapshot for parallel iteration.
        public StyleArray Styles => styleArray;

        // Persistent Element-keyed result map. Same instance returned by
        // ComputeAll on every call; consumers must not retain views past the
        // next ComputeAll/InvalidateAll since entries can be replaced.
        public IReadOnlyDictionary<Element, ComputedStyle> ResultMap => resultMap;

        void ResetStyleArrayState() {
            resultMap.Clear();
            styleArray.Clear();
            snapshotDirty = true;
            treeShapeDirty = true;
            snapshotDirtyNodes.Clear();
        }

        void AlignStyleArrayTo(DomSnapshot snap) {
            if (snap == null) return;
            // Wipe before the walk re-fills entries. Without this, a NodeId
            // that flipped Element→Text between snapshot rebuilds would keep
            // a stale ComputedStyle reference. Cost is O(N) on the existing
            // backing array — no allocation.
            styleArray.Clear();
            styleArray.AlignTo(snap);
        }

        // Hooks Document.Mutated so structural/attribute changes flip
        // snapshotDirty. Idempotent: re-attaching the same document is a
        // no-op; re-attaching a new document detaches the old subscription.
        void EnsureSnapshotSubscription(Document doc) {
            if (ReferenceEquals(attachedDoc, doc)) return;
            DetachSnapshotSubscription();
            attachedDoc = doc;
            if (doc == null) return;
            if (snapshotInvalidator == null) {
                snapshotInvalidator = OnDocumentMutated;
            }
            doc.Mutated += snapshotInvalidator;
            snapshotDirty = true;
            treeShapeDirty = true;
        }

        void DetachSnapshotSubscription() {
            if (attachedDoc != null && snapshotInvalidator != null) {
                attachedDoc.Mutated -= snapshotInvalidator;
            }
            attachedDoc = null;
        }

        void OnDocumentMutated(DomMutation m) {
            // Snapshot always needs a refill — both attribute caches and
            // tree-structure arrays live in the same DomSnapshot. tree-shape
            // dirty is the narrower flag that forces full-cascade fallback.
            snapshotDirty = true;
            if (m.Kind == DomMutationKind.ChildAdded || m.Kind == DomMutationKind.ChildRemoved) {
                treeShapeDirty = true;
                // Tree shape will require a full Refill; per-node refresh
                // queue is moot.
                snapshotDirtyNodes.Clear();
                // Shape cache encodes ancestor classes but NOT child
                // position. Tree-shape mutations shift nth-child positions
                // without changing any ancestor's class/id/tag features,
                // so cache entries become stale. Clear to force fresh
                // CollectMatches on the next cascade. Matched-properties
                // cache keys are derived from the fresh matches list so
                // they invalidate naturally.
                ClearShapeCache();
            } else if (m.Target != null && !treeShapeDirty) {
                snapshotDirtyNodes.Add(m.Target);
            }
        }

        // Cleared by the cascade engine (which holds the actual dictionary)
        // — split here so this partial doesn't need direct access to the
        // shapeCache field declared in CascadeEngine.cs.
        partial void ClearShapeCache();
    }
}
