using NUnit.Framework;
using Weva.Compiled;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Parsing;
using System.Collections.Generic;

namespace Weva.Tests.Css.Cascade {
    // TG29 — Direct tests for the scratch types declared in
    // Runtime/Css/Cascade/CascadePools.cs (CascadeScratch + SnapshotPassState).
    //
    // NOTE on scope: the TG29 tracker entry describes "per-style cascade
    // scratch POOLS (decl list, key-pair, source-array buffers)" with
    // rent/return + size caps. The actual file does NOT implement a
    // rent/return pool with a size cap — it declares two single-instance
    // scratch holders (one CascadeScratch + one SnapshotPassState per
    // CascadeEngine) whose collections are reused across passes and
    // emptied by Reset…() between elements / between snapshot passes.
    //
    // These tests therefore pin the actual surface of CascadePools.cs:
    //   - CascadeScratch.ResetPerElement() clears all five collections.
    //   - SnapshotPassState.Reset() swaps in fresh snapshot/index/selectors,
    //     re-populates ElementToNodeId, clears the previous mapping, and
    //     flips Active.
    //   - SnapshotPassState.Deactivate() nulls the snapshot graph refs but
    //     keeps the ElementToNodeId backing buckets allocated for reuse.
    //   - TryGetNodeId round-trips through the rebuilt map.
    //
    // The "rent/return same instance" property collapses to the trivial
    // "the same shared scratch object is used across calls" — pinned by
    // re-invoking ResetPerElement on a single instance and confirming the
    // collections survive (same reference) and are emptied (same length=0).
    public class CascadePoolsDirectTests {

        // 1. The scratch's collections survive across ResetPerElement calls
        //    (i.e. the engine reuses the same backing storage instead of
        //    allocating fresh dicts/lists per element). This is the
        //    "rent + return returns the SAME instance on the next rent"
        //    invariant collapsed to a single-instance scratch.
        [Test]
        public void ResetPerElement_preserves_collection_instances_TG29() {
            var s = new CascadeScratch();
            var matchesRef = s.Matches;
            var expandedRef = s.ExpandedMatches;
            var winnerRef = s.PerPropertyWinner;
            var rawRef = s.RawValues;
            var customsRef = s.CustomsResolved;

            // Mutate every collection so Reset has something to do.
            s.Matches.Add(default);
            s.ExpandedMatches.Add(default);
            s.PerPropertyWinner["color"] = default;
            s.RawValues["color"] = "red";
            s.CustomsResolved["--x"] = "1";

            s.ResetPerElement();

            Assert.That(s.Matches, Is.SameAs(matchesRef),
                "Matches list reference should survive ResetPerElement");
            Assert.That(s.ExpandedMatches, Is.SameAs(expandedRef),
                "ExpandedMatches list reference should survive ResetPerElement");
            Assert.That(s.PerPropertyWinner, Is.SameAs(winnerRef),
                "PerPropertyWinner dict reference should survive ResetPerElement");
            Assert.That(s.RawValues, Is.SameAs(rawRef),
                "RawValues dict reference should survive ResetPerElement");
            Assert.That(s.CustomsResolved, Is.SameAs(customsRef),
                "CustomsResolved dict reference should survive ResetPerElement");
        }

        // 2. A freshly-constructed CascadeScratch has empty collections —
        //    the cold-start case. This is the analogue of "rent without prior
        //    return creates a new instance" for a single-instance scratch:
        //    no stale data leaks from a previous construction.
        [Test]
        public void Fresh_CascadeScratch_has_empty_collections_TG29() {
            var s = new CascadeScratch();
            Assert.That(s.Matches, Is.Not.Null.And.Empty);
            Assert.That(s.ExpandedMatches, Is.Not.Null.And.Empty);
            Assert.That(s.PerPropertyWinner, Is.Not.Null.And.Empty);
            Assert.That(s.RawValues, Is.Not.Null.And.Empty);
            Assert.That(s.CustomsResolved, Is.Not.Null.And.Empty);
        }

        // 3. Repeated populate+reset cycles do not unboundedly grow the
        //    scratch — after N rounds the collections still report Count=0
        //    (analogue of "you can't grow the pool past the documented limit"
        //    for the single-instance scratch: the per-element clear is what
        //    keeps memory steady). 256 cycles dwarfs realistic per-element
        //    declaration counts.
        [Test]
        public void Repeated_populate_then_reset_keeps_scratch_drained_TG29() {
            var s = new CascadeScratch();
            for (int i = 0; i < 256; i++) {
                s.Matches.Add(default);
                s.ExpandedMatches.Add(default);
                s.PerPropertyWinner["k" + i] = default;
                s.RawValues["k" + i] = "v";
                s.CustomsResolved["--k" + i] = "v";
                s.ResetPerElement();
            }
            Assert.That(s.Matches.Count, Is.Zero, "Matches not drained after 256 cycles");
            Assert.That(s.ExpandedMatches.Count, Is.Zero, "ExpandedMatches not drained after 256 cycles");
            Assert.That(s.PerPropertyWinner.Count, Is.Zero, "PerPropertyWinner not drained after 256 cycles");
            Assert.That(s.RawValues.Count, Is.Zero, "RawValues not drained after 256 cycles");
            Assert.That(s.CustomsResolved.Count, Is.Zero, "CustomsResolved not drained after 256 cycles");
        }

        // 4. ResetPerElement clears stale data from every collection so a
        //    second per-element pass doesn't observe leftovers from the
        //    first — this is the "returned items are cleared before being
        //    handed back out" invariant.
        [Test]
        public void ResetPerElement_clears_stale_data_before_reuse_TG29() {
            var s = new CascadeScratch();
            s.Matches.Add(default);
            s.Matches.Add(default);
            s.ExpandedMatches.Add(default);
            s.PerPropertyWinner["display"] = default;
            s.PerPropertyWinner["color"] = default;
            s.RawValues["display"] = "block";
            s.RawValues["color"] = "red";
            s.CustomsResolved["--theme"] = "dark";

            s.ResetPerElement();

            Assert.That(s.Matches.Count, Is.Zero, "Matches should be empty after reset");
            Assert.That(s.ExpandedMatches.Count, Is.Zero, "ExpandedMatches should be empty after reset");
            Assert.That(s.PerPropertyWinner.Count, Is.Zero, "PerPropertyWinner should be empty after reset");
            Assert.That(s.RawValues.Count, Is.Zero, "RawValues should be empty after reset");
            Assert.That(s.CustomsResolved.Count, Is.Zero, "CustomsResolved should be empty after reset");

            // And the next "rent" can write fresh keys without seeing stale ones.
            s.RawValues["color"] = "blue";
            Assert.That(s.RawValues["color"], Is.EqualTo("blue"));
            Assert.That(s.RawValues.ContainsKey("display"), Is.False,
                "stale 'display' key from previous element leaked through reset");
        }

        // 5. SnapshotPassState.Reset() wires TryGetNodeId to the snapshot's own
        //    Node→NodeId map (PERF-1: avoids rebuilding a duplicate ElementToNodeId
        //    from a ManagedNodes[] scan on every ComputeAll pass). The public
        //    ElementToNodeId dict stays empty — it is a legacy bucket kept for
        //    the Deactivate() contract (buckets survive for the next Reset).
        //    What we pin: TryGetNodeId correctly resolves every Element that
        //    belongs to the active snapshot, and returns false for elements that
        //    belong to a snapshot swapped out by a subsequent Reset.
        [Test]
        public void SnapshotPassState_Reset_clears_prior_mapping_and_repopulates_TG29() {
            // Two snapshots of different size; share a symbol table so
            // both Builds work but otherwise carry entirely disjoint
            // managed Element instances.
            var sym = new SymbolTable();
            var big = HtmlParser.Parse("<div><span></span><span></span><p></p></div>");
            var small = HtmlParser.Parse("<div></div>");
            var bigSnap = DomSnapshot.Build(big, sym);
            var smallSnap = DomSnapshot.Build(small, sym);
            var index = new SelectorIndex(sym, System.Array.Empty<CompiledSelector>());
            var selectors = System.Array.Empty<CompiledSelector>();

            var pass = new SnapshotPassState();
            Assert.That(pass.Active, Is.False, "newly-constructed pass should be inactive");
            Assert.That(pass.ElementToNodeId, Is.Empty, "fresh pass should have empty element map");

            pass.Reset(bigSnap, index, selectors);
            Assert.That(pass.Active, Is.True, "Reset should flip Active");

            // PERF-1 contract: ElementToNodeId is NOT populated (the lookup
            // now delegates to the snapshot's own NodeToIdMap via snapshotNodeToId).
            // The observable contract is that TryGetNodeId works for every Element
            // in the big snapshot.
            var bigElements = new List<Weva.Dom.Element>();
            for (int i = 0; i < bigSnap.NodeCount; i++) {
                if (bigSnap.ManagedNodes[i] is Weva.Dom.Element e) bigElements.Add(e);
            }
            Assert.That(bigElements.Count, Is.GreaterThan(0),
                "test fixture: big snapshot must contain elements");
            foreach (var e in bigElements) {
                Assert.That(pass.TryGetNodeId(e, out int nid), Is.True,
                    "TryGetNodeId must resolve every element from the active snapshot");
                Assert.That(bigSnap.ManagedNodes[nid], Is.SameAs(e),
                    "resolved NodeId must round-trip back to the same Element");
            }

            // After Reset against the small snap, big-snap Elements must no
            // longer resolve via TryGetNodeId.
            var dictBefore = pass.ElementToNodeId;
            pass.Reset(smallSnap, index, selectors);
            Assert.That(pass.ElementToNodeId, Is.SameAs(dictBefore),
                "Reset should reuse the existing dict instance (buckets stay allocated)");
            foreach (var e in bigElements) {
                Assert.That(pass.TryGetNodeId(e, out _), Is.False,
                    "stale element from big snapshot survived Reset to small snapshot");
            }

            // Deactivate drops the snapshot/index/selectors refs but keeps
            // the dict instance for the next pass.
            pass.Deactivate();
            Assert.That(pass.Active, Is.False);
            Assert.That(pass.Snapshot, Is.Null);
            Assert.That(pass.Index, Is.Null);
            Assert.That(pass.Selectors, Is.Null);
            Assert.That(pass.ElementToNodeId, Is.SameAs(dictBefore),
                "Deactivate should keep the dict instance for the next pass");
            Assert.That(pass.ElementToNodeId, Is.Empty,
                "Deactivate should clear the element map");
        }

        // 6. TryGetNodeId returns false for an Element the snapshot has
        //    never seen — analogue of "returning a null/foreign instance is
        //    safely ignored (no exception)". A foreign Element shouldn't NRE
        //    or false-positive; out int is the zero default. Same goes for
        //    null — Dictionary<Element,int>.TryGetValue tolerates a null key
        //    only if the comparer does; we pin current behavior either way:
        //    must not throw NRE on a real Element it has never indexed.
        [Test]
        public void SnapshotPassState_TryGetNodeId_returns_false_for_foreign_element_TG29() {
            var sym = new SymbolTable();
            var docA = HtmlParser.Parse("<div id='a'></div>");
            var docB = HtmlParser.Parse("<div id='b'></div>");
            var snapA = DomSnapshot.Build(docA, sym);
            var index = new SelectorIndex(sym, System.Array.Empty<CompiledSelector>());

            var pass = new SnapshotPassState();
            pass.Reset(snapA, index, System.Array.Empty<CompiledSelector>());

            // The "b" Element belongs to a different document — never
            // registered in pass.ElementToNodeId. TryGetNodeId must
            // return false, not throw, and must not yield a spurious
            // positive node id.
            var foreign = docB.GetElementById("b");
            Assert.That(foreign, Is.Not.Null, "test fixture: docB should contain #b");
            bool found = pass.TryGetNodeId(foreign, out int nid);
            Assert.That(found, Is.False, "foreign element should not round-trip via TryGetNodeId");
            Assert.That(nid, Is.Zero, "out param should be default(int) when foreign element is missed");
        }
    }
}
