using System;
using NUnit.Framework;
using Weva.Dom;
using Weva.Reactive;
using static Weva.Tests.Layout.Incremental.IncrementalLayoutTestHelpers;

namespace Weva.Tests.Layout.Incremental {
    // P5 regression coverage. Pre-fix `LayoutEngine.Apply(tracker)` lazily
    // allocated `toDrop = new List<Element>()` whenever any dirty entry was
    // layout/style relevant. The new path uses a per-engine `scratchToDrop`
    // field, cleared at the top of Apply and reused across calls.
    //
    // Tracker: P5 in CODE_AUDIT_FINDINGS.md.
    public class LayoutEngineApplyScratchTests {
        [Test]
        public void Apply_steady_state_allocates_near_zero_P5() {
            // Allocation parity: repeated Apply calls with a single-element
            // dirty set must reuse the scratch list. Pre-fix every dirty
            // frame allocated `new List<Element>()` (~40 B + element ref
            // array). 100 calls * 40 B = 4 KB minimum.
            //
            // We also expect the inner `foreach (var kv in tracker.DirtyEntries)`
            // to be alloc-free in steady state — InvalidationTracker exposes
            // a struct enumerator over its backing dictionary.
            var h = Build("<div id=\"a\"><div id=\"b\"></div><div id=\"c\"></div></div>");
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            var a = h.Doc.GetElementById("a");

            // Warmup: prime caches and scratch list capacity.
            for (int i = 0; i < 5; i++) {
                var t = new InvalidationTracker();
                t.MarkDirty(a, InvalidationKind.Layout);
                h.Engine.Apply(t);
                // Re-layout to repopulate the cache so subsequent Applies
                // have something to drop.
                h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // We measure ONLY the Apply calls — exclude the tracker
            // allocation and any re-layout cost. The MarkDirty + Apply pair
            // is the dirty path the lifecycle exercises every frame.
            var trackerScratch = new InvalidationTracker();
            long before = GC.GetAllocatedBytesForCurrentThread();
            const int calls = 100;
            for (int i = 0; i < calls; i++) {
                trackerScratch.Clear();
                trackerScratch.MarkDirty(a, InvalidationKind.Layout);
                h.Engine.Apply(trackerScratch);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();
            long delta = after - before;
            long perCall = delta / calls;
            TestContext.WriteLine(
                $"P5 Apply alloc over {calls} calls = {delta} B (~{perCall} B/call)");
            // Pre-fix `new List<Element>()` + entry array == ~64 B minimum
            // per call. Post-fix should be ~zero (the tracker MarkDirty
            // does a single Dictionary insert/update). 48 B is a tight
            // bound that catches the lazy-alloc regression.
            Assert.That(perCall, Is.LessThan(48),
                "Apply steady-state regressed — scratchToDrop may be re-allocating");
        }

        [Test]
        public void Apply_drops_dirty_elements_from_cache_P5() {
            // Functional parity: the behaviour of Apply (which elements get
            // evicted from cache) is unchanged. After a Layout pass the cache
            // contains every block-level Element; Apply must drop exactly
            // those flagged with a layout-relevant kind.
            var h = Build("<div id=\"a\"><div id=\"b\"></div><div id=\"c\"></div></div>");
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            int before = h.Engine.CacheSize;
            Assert.That(before, Is.GreaterThanOrEqualTo(3));

            // Mark a single element with Layout: only that element is dropped.
            var tracker = new InvalidationTracker();
            tracker.MarkDirty(h.Doc.GetElementById("b"), InvalidationKind.Layout);
            h.Engine.Apply(tracker);
            Assert.That(h.Engine.CacheSize, Is.EqualTo(before - 1));

            // Re-layout to repopulate, then drop a different element via
            // a Style flag (style is in the layout-relevant kind mask too).
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            int beforeB = h.Engine.CacheSize;
            var t2 = new InvalidationTracker();
            t2.MarkDirty(h.Doc.GetElementById("c"), InvalidationKind.Style);
            h.Engine.Apply(t2);
            Assert.That(h.Engine.CacheSize, Is.EqualTo(beforeB - 1));
        }

        [Test]
        public void Apply_skips_non_layout_kinds_P5() {
            // Functional parity: Paint-only dirtiness must NOT evict the
            // cache — the scratchToDrop list stays empty for the call and
            // the post-pass loop is a no-op.
            var h = Build("<div id=\"a\"></div>");
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            int before = h.Engine.CacheSize;
            Assert.That(before, Is.GreaterThan(0));

            var tracker = new InvalidationTracker();
            tracker.MarkDirty(h.Doc.GetElementById("a"), InvalidationKind.Paint);
            h.Engine.Apply(tracker);
            Assert.That(h.Engine.CacheSize, Is.EqualTo(before),
                "Apply must not drop entries for Paint-only dirty flags");
        }

        [Test]
        public void Apply_handles_repeated_calls_with_growing_dirty_set_P5() {
            // Functional parity: a multi-element dirty set must drop every
            // flagged element. Confirms the in-place packing into
            // scratchToDrop preserves all entries through the foreach +
            // post-pass loop. Repeated calls reuse the SAME scratch list —
            // any leftover state from a prior call would corrupt the result.
            var h = Build("<div id=\"a\"><div id=\"b\"></div><div id=\"c\"></div><div id=\"d\"></div></div>");
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            int before = h.Engine.CacheSize;

            // First Apply: drop b only.
            var t1 = new InvalidationTracker();
            t1.MarkDirty(h.Doc.GetElementById("b"), InvalidationKind.Layout);
            h.Engine.Apply(t1);
            Assert.That(h.Engine.CacheSize, Is.EqualTo(before - 1));

            // Re-populate.
            h.Engine.Layout(h.Doc, h.StyleOf, h.Ctx);
            int afterRelayout = h.Engine.CacheSize;

            // Second Apply on same engine: drop c AND d. If scratchToDrop
            // weren't Cleared at entry, leftover `b` from the prior call
            // would also be Removed (harmlessly, since cache.Remove on a
            // missing key is a no-op) — but the test still confirms the
            // intended set is dropped.
            var t2 = new InvalidationTracker();
            t2.MarkDirty(h.Doc.GetElementById("c"), InvalidationKind.Layout);
            t2.MarkDirty(h.Doc.GetElementById("d"), InvalidationKind.Layout);
            h.Engine.Apply(t2);
            Assert.That(h.Engine.CacheSize, Is.EqualTo(afterRelayout - 2));
        }
    }
}
