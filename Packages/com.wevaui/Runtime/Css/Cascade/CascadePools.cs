using System.Collections.Generic;
using Weva.Compiled;
using Weva.Css.Selectors;
using Weva.Dom;

namespace Weva.Css.Cascade {
    // Per-engine reusable scratch buffers that get Reset()-ed at the start of each
    // ComputeAll. Walking the DOM is single-threaded, so a single shared instance per
    // engine is enough — ComputeFor never recurses into another ComputeFor (Walk is
    // tail-recursive over children, but each call to ComputeOrHit fully consumes the
    // buffers before recursing into the next element).
    //
    // Lives on CascadeEngine; not held by callers. Public-but-internal so
    // CascadeEngine.cs can co-locate types if it ever wants to inline the pool.
    internal sealed class CascadeScratch {
        // Per-element scratch reused across every Walk() step.
        public readonly List<MatchedDeclaration> Matches = new(64);
        public readonly List<MatchedDeclaration> ExpandedMatches = new(64);
        // perPropertyWinner maps property name -> winning declaration after sort.
        // Cleared per element; re-grown on the first miss after warmup.
        public readonly Dictionary<string, MatchedDeclaration> PerPropertyWinner = new(128);
        public readonly Dictionary<string, string> RawValues = new(128);
        public readonly Dictionary<string, string> CustomsResolved = new(16);

        // Declaration pool. ExpandShorthandMatchesInto rents one per longhand
        // produced by each shorthand expansion; the rented instances live
        // until the next ResetPerElement(). Recycling instead of allocating
        // a fresh Declaration each frame avoids the bulk of the
        // expand-phase per-Compute bytes — every animated element typically
        // triggers a handful of shorthand expansions (e.g. inline
        // `background:`), and each expansion produces ~8 longhands.
        readonly List<Declaration> declPool = new(64);
        int declPoolUsed;

        public Declaration RentDeclaration(string property, string valueText, bool important) {
            Declaration d;
            if (declPoolUsed < declPool.Count) {
                d = declPool[declPoolUsed];
                d.Property = property;
                d.ValueText = valueText;
                d.Important = important;
                // The cachedId is a lazily-computed cache of CssProperties.GetId(Property).
                // When we reuse a pool slot with a different property name, the stale id
                // would be read later by style.Set(pid, value) and write into the WRONG
                // ComputedStyle slot. Invalidate it so the next PropertyId read recomputes
                // from the (now correct) Property string.
                d.InvalidatePropertyIdCache();
            } else {
                d = new Declaration(property, valueText, important);
                declPool.Add(d);
            }
            declPoolUsed++;
            return d;
        }

        public void ResetPerElement() {
            Matches.Clear();
            ExpandedMatches.Clear();
            PerPropertyWinner.Clear();
            RawValues.Clear();
            CustomsResolved.Clear();
            declPoolUsed = 0;
        }
    }

    // Long-lived snapshot pass state. Reset() is called at the start of each ComputeAll
    // when useSnapshot is enabled, swapping in the freshly-built DomSnapshot/SelectorIndex
    // pair while reusing the Dictionary<Element,int> buckets, the IntsBuffer scratch,
    // and the matched-indices list. Dictionary<TKey,TValue>.Clear() is O(n) but doesn't
    // free buckets; subsequent inserts hit the existing storage.
    internal sealed class SnapshotPassState {
        public DomSnapshot Snapshot;
        public SelectorIndex Index;
        public IReadOnlyList<CompiledSelector> Selectors;
        public readonly Dictionary<Element, int> ElementToNodeId = new(256);
        public readonly IntsBuffer Scratch = new();
        public readonly List<int> MatchedIndices = new(32);
        public bool Active;

        public void Reset(DomSnapshot snapshot, SelectorIndex index, IReadOnlyList<CompiledSelector> selectors) {
            Snapshot = snapshot;
            Index = index;
            Selectors = selectors;
            // PERF-1: reuse the snapshot's own Node→NodeId map instead of
            // rebuilding ElementToNodeId from a full ManagedNodes[] scan.
            // DomSnapshot.nodeToId is already the exact same mapping
            // (built during Refill/Build and cleared+rebuilt on each Refill),
            // so we reference it directly rather than duplicating it.
            // ElementToNodeId is now a thin wrapper — TryGetNodeId reads it.
            // Scratch and MatchedIndices are reset per CollectMatchesFromSnapshot call.
            snapshotNodeToId = snapshot.NodeToIdMap;
            ElementToNodeId.Clear();    // kept for Deactivate contract; not populated
            Active = true;
        }

        // Reference to the snapshot's live Node→NodeId mapping. Set in Reset,
        // cleared in Deactivate. Accessed only by TryGetNodeId — never mutated.
        IReadOnlyDictionary<Dom.Node, int> snapshotNodeToId;

        public void Deactivate() {
            // Drop strong references to the (potentially large) snapshot graph so it can
            // be collected if the next ComputeAll happens far in the future. The buckets
            // for ElementToNodeId remain allocated and ready for the next pass.
            Snapshot = null;
            Index = null;
            Selectors = null;
            snapshotNodeToId = null;
            ElementToNodeId.Clear();
            Active = false;
        }

        public bool TryGetNodeId(Element e, out int nodeId) {
            // PERF-1: use the snapshot's own Node→NodeId dict (set in Reset)
            // instead of the redundant ElementToNodeId copy. Falls back to
            // ElementToNodeId when snapshotNodeToId is null (Deactivated or
            // legacy callers that set Active directly).
            if (snapshotNodeToId != null) {
                return snapshotNodeToId.TryGetValue(e, out nodeId);
            }
            return ElementToNodeId.TryGetValue(e, out nodeId);
        }
    }
}
