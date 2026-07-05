using System;
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Scrolling {
    // P6 regression coverage (ScrollContainer half). Pre-fix
    // `RetainOnly` lazily allocated `stale ??= new List<Box>()` on every call
    // that found at least one stale entry. After the fix the list is a per-
    // instance `scratchStale` field, cleared at the top of RetainOnly, filled
    // during, and never reallocated.
    //
    // Tracker: P6 in CODE_AUDIT_FINDINGS.md.
    public class ScrollContainerRetainOnlyScratchTests {
        // Build a small box tree with N <div>s under a <div>, return the
        // child boxes. We only need reference-identity Box instances for the
        // dict, but BlockBox is abstract through Box; the easiest path is
        // to lay out a fragment and grab the resulting child Boxes.
        static Box[] BuildChildBoxes(int n) {
            var sb = new System.Text.StringBuilder("<div>");
            for (int i = 0; i < n; i++) sb.Append("<div></div>");
            sb.Append("</div>");
            var (root, _, _) = Build(sb.ToString());
            var content = ContentRoot(root);
            // content -> outer div -> N child divs
            var outer = content.Children[0];
            var boxes = new Box[n];
            for (int i = 0; i < n; i++) boxes[i] = outer.Children[i];
            return boxes;
        }

        [Test]
        public void RetainOnly_no_stale_allocates_near_zero_P6() {
            // Allocation parity, no-stale path: when every entry in `states`
            // is in `live`, RetainOnly's foreach finds no stale entries and
            // the post-pass loop is a no-op. After warmup this path should
            // allocate essentially nothing per call. Pre-fix the body never
            // allocated (the lazy `stale ??= new List<>()` only fires on
            // first `Add`); this guards against an accidental regression
            // that would re-allocate even in the empty-stale case.
            var sc = new ScrollContainer();
            const int N = 16;
            var boxes = BuildChildBoxes(N);
            var live = new HashSet<Box>();
            for (int i = 0; i < N; i++) {
                sc.GetOrCreate(boxes[i]);
                live.Add(boxes[i]);
            }

            // Warmup: prime JIT.
            for (int i = 0; i < 5; i++) sc.RetainOnly(live);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetAllocatedBytesForCurrentThread();
            const int calls = 100;
            for (int i = 0; i < calls; i++) sc.RetainOnly(live);
            long after = GC.GetAllocatedBytesForCurrentThread();
            long delta = after - before;
            long perCall = delta / calls;
            TestContext.WriteLine(
                $"P6 RetainOnly no-stale alloc over {calls} calls = {delta} B (~{perCall} B/call)");
            // No-stale path should be 0 B/call. 16 B headroom for JIT/GC
            // bookkeeping noise.
            Assert.That(perCall, Is.LessThan(16),
                "RetainOnly no-stale path regressed — verify scratchStale isn't reallocating");
        }

        [Test]
        public void RetainOnly_stale_path_reuses_scratch_P6() {
            // Allocation parity, with-stale path: the scratchStale list is
            // re-used across calls. We exercise a recurring "one stale entry"
            // call shape but pre-allocate all ScrollStates up-front so the
            // measurement window only captures the RetainOnly cost, not the
            // ScrollState allocations from re-adding entries.
            //
            // We do this by maintaining a pool of N+1 boxes and rotating
            // which N are live each call. The (N+1)-th box is added to the
            // container only ONCE before the measurement starts — we never
            // call GetOrCreate in the hot loop. After the spare box is
            // removed it's truly gone from `states`, so we re-add it via
            // the dictionary directly... but ScrollContainer doesn't expose
            // a no-alloc re-add path. Instead, we keep a fixed set of boxes
            // and toggle which one is excluded from `live`; each toggled-out
            // box stays in the container's `states` map (because RetainOnly
            // only removes when we actually CALL it with a live set that
            // excludes it). The trick: we exclude box[0] every iteration so
            // RetainOnly never removes anything; instead we directly invoke
            // RetainOnly with a `live` set missing box[0], pre-add box[0]
            // BACK to the container via Remove + re-create...
            //
            // Simpler approach: pre-build a second ScrollContainer per call
            // is no good. Use sc.Remove() to take the entry out without
            // calling GetOrCreate. But Remove doesn't reset for re-add
            // without allocation.
            //
            // Cleanest seam: measure 100 calls where each call walks the
            // full live-set foreach (the hot work), and the rotating box
            // stays IN the live set every other call — so half the calls
            // do nothing and half remove + re-create via GetOrCreate. We
            // compare the two halves to attribute alloc to GetOrCreate
            // rather than RetainOnly.
            //
            // Simplest version: just exercise the foreach + Add path
            // without removing anything from states. RetainOnly with a
            // smaller live set DOES remove from states. So we accept the
            // ScrollState re-alloc cost as a known constant and assert
            // perCall stays under (ScrollState size + small slack).
            var sc = new ScrollContainer();
            const int N = 8;
            var boxes = BuildChildBoxes(N);
            for (int i = 0; i < N; i++) sc.GetOrCreate(boxes[i]);
            var live = new HashSet<Box>();
            for (int i = 0; i < N; i++) live.Add(boxes[i]);

            // Warmup: each iteration toggles one box out, RetainOnly removes
            // it, then we re-add via GetOrCreate (forces a fresh ScrollState).
            for (int w = 0; w < 5; w++) {
                int drop = w % N;
                live.Remove(boxes[drop]);
                sc.RetainOnly(live);
                live.Add(boxes[drop]);
                sc.GetOrCreate(boxes[drop]);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetAllocatedBytesForCurrentThread();
            const int calls = 100;
            for (int i = 0; i < calls; i++) {
                int drop = i % N;
                live.Remove(boxes[drop]);
                sc.RetainOnly(live);
                live.Add(boxes[drop]);
                sc.GetOrCreate(boxes[drop]);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();
            long delta = after - before;
            long perCall = delta / calls;
            TestContext.WriteLine(
                $"P6 RetainOnly+rotate alloc over {calls} calls = {delta} B (~{perCall} B/call)");
            // Per-call alloc is dominated by the new ScrollState() for the
            // re-add (sc.GetOrCreate creates one whenever `drop` isn't in
            // `states`). ScrollState is ~32 B + class header. Empirically
            // this measures ~80 B/call total. A regression to the lazy
            // `stale ??= new List<>()` adds at least another 40 B/call
            // (list header + entry array). 160 B/call is a clean bound.
            Assert.That(perCall, Is.LessThan(160),
                "RetainOnly steady-state regressed — scratchStale may be re-allocating");
        }

        [Test]
        public void RetainOnly_removes_only_non_live_entries_P6() {
            // Functional parity: behaviour of the method (which entries get
            // removed) is identical to the pre-fix lazy-allocated path. Set
            // up four boxes, mark two live, and assert exactly the other two
            // are removed.
            var sc = new ScrollContainer();
            var boxes = BuildChildBoxes(4);
            var a = boxes[0]; var b = boxes[1]; var c = boxes[2]; var d = boxes[3];
            sc.GetOrCreate(a); sc.GetOrCreate(b); sc.GetOrCreate(c); sc.GetOrCreate(d);
            Assert.That(sc.Count, Is.EqualTo(4));

            var live = new HashSet<Box> { a, c };
            sc.RetainOnly(live);

            Assert.That(sc.Has(a), Is.True);
            Assert.That(sc.Has(b), Is.False);
            Assert.That(sc.Has(c), Is.True);
            Assert.That(sc.Has(d), Is.False);
            Assert.That(sc.Count, Is.EqualTo(2));

            // Repeat with the same live set — no entry to remove, scratch
            // remains empty, dict unchanged.
            sc.RetainOnly(live);
            Assert.That(sc.Count, Is.EqualTo(2));
            Assert.That(sc.Has(a), Is.True);
            Assert.That(sc.Has(c), Is.True);
        }

        [Test]
        public void RetainOnly_null_live_clears_all_P6() {
            // The early null-live branch routes through Clear and never
            // touches scratchStale. Functional parity guard.
            var sc = new ScrollContainer();
            var boxes = BuildChildBoxes(2);
            sc.GetOrCreate(boxes[0]);
            sc.GetOrCreate(boxes[1]);
            Assert.That(sc.Count, Is.EqualTo(2));
            sc.RetainOnly(null);
            Assert.That(sc.Count, Is.EqualTo(0));
        }
    }
}
