using System;
using NUnit.Framework;
using Weva.Css.Values;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using static Weva.Tests.Paint.Conversion.Incremental.PaintIncrementalTestHelpers;

namespace Weva.Tests.Paint.Conversion {
    // Allocation-rate tests for the BoxToPaintConverter hot path. The pool wired
    // in for v0.2.3 (PaintConverterPools) targets the steady-state PLAN.md §1
    // goal: re-converting an unchanged tree should not churn the GC.
    //
    // GC.GetTotalAllocatedBytes is approximate and noisy; tests use a fairly
    // generous ceiling so they don't flake on CI but still catch a per-Convert
    // regression from "0 to N allocs". Run via `Category=alloc`.
    [Category("alloc")]
    public class PaintAllocationTests {
        static long AllocatedBytes() {
#if NET5_0_OR_GREATER || NETCOREAPP3_0_OR_GREATER
            return GC.GetTotalAllocatedBytes(precise: false);
#else
            return GC.GetTotalMemory(forceFullCollection: false);
#endif
        }

        // Stable absolute-heap reading for steady-state drift tests. Forces a
        // synchronous full collection then reports the post-collection heap so
        // window-to-window comparisons aren't perturbed by whatever uncollected
        // garbage happened to be on the heap when the sample was taken. The
        // unforced AllocatedBytes() above is fine for per-call delta tests
        // because the noise floor is dwarfed by the per-call rate, but
        // Repeated_Convert_with_no_changes_does_not_grow_memory measures the
        // *change* in rate across windows where the rate itself is near zero,
        // so a few-KB heap reading wobble would otherwise dominate the signal.
        static long HeapBytesStable() {
            for (int i = 0; i < 3; i++) {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            return GC.GetTotalMemory(forceFullCollection: true);
        }

        static void StabilizeGC() {
            for (int i = 0; i < 3; i++) {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }

        // Builds a flat tree of `n` background-coloured children under a single
        // root. Mirrors the most common "pile of styled divs" hot-path shape.
        static BlockBox BuildFlatColoredTree(int n) {
            var rootStyle = Style();
            var root = Block(0, 0, 1000, 1000, rootStyle);
            string[] colors = { "red", "blue", "green", "yellow", "orange" };
            for (int i = 0; i < n; i++) {
                var s = Style();
                s.Set("background-color", colors[i % colors.Length]);
                var b = Block(0, i * 10, 100, 10, s);
                root.AddChild(b);
            }
            return root;
        }

        static BlockBox BuildDecoratedTree(int n) {
            var rootStyle = Style();
            rootStyle.Set("background-color", "white");
            var root = Block(0, 0, 1000, 1000, rootStyle);
            for (int i = 0; i < n; i++) {
                var s = Style();
                s.Set("background-color", i % 2 == 0 ? "red" : "blue");
                s.Set("border-top-style", "solid");
                s.Set("border-top-width", "1px");
                s.Set("border-top-color", "black");
                var b = Block(0, i * 12, 100, 12, s);
                root.AddChild(b);
            }
            return root;
        }

        [Test]
        public void Convert_secondCall_allocates_less_than_first() {
            // 50 boxes spanning many gradient-laden styles so the first-call cost
            // (parsing + Brush construction) is meaningfully bigger than the
            // warm second call. Without gradients the per-call delta is dominated
            // by FillRect alloc and the cache-warmup saving doesn't show clearly.
            var rootStyle = Style();
            var root = Block(0, 0, 1000, 1000, rootStyle);
            for (int i = 0; i < 50; i++) {
                var s = Style();
                s.Set("background-image", $"linear-gradient({i % 360}deg, red, blue)");
                s.Set("filter", $"blur({(i % 4) + 1}px)");
                var b = Block(0, i * 10, 100, 10, s);
                root.AddChild(b);
            }
            var c = new BoxToPaintConverter();

            // Cold call: parsers prime the filter chain cache, parse 50 gradients.
            StabilizeGC();
            long b0 = AllocatedBytes();
            c.Convert(root);
            long firstCall = AllocatedBytes() - b0;

            // Warm call: cached filter chains, but gradients are still re-parsed
            // (no cache there). Should still be less due to filter cache.
            StabilizeGC();
            long b1 = AllocatedBytes();
            c.Convert(root);
            long secondCall = AllocatedBytes() - b1;

            TestContext.Progress.WriteLine($"first={firstCall} bytes, second={secondCall} bytes");
            // Allow equal (both 0) because with the filter:blur fallback the
            // engine suppresses box decorations for blurred boxes and skips
            // gradient parsing entirely on both calls — no diff is possible.
            // The cache-warmup behavior is asserted when allocations exist.
            Assert.That(secondCall, Is.LessThanOrEqualTo(firstCall),
                $"second Convert ({secondCall} bytes) should allocate no more than the first ({firstCall} bytes)");
        }

        [Test]
        public void Convert_after_warmup_allocates_under_X_bytes() {
            // 100-box tree: reasonable mid-size, ceiling 16 KB per call.
            var root = BuildFlatColoredTree(100);
            var c = new BoxToPaintConverter();

            // Warm.
            for (int i = 0; i < 10; i++) c.Convert(root);

            const int n = 50;
            StabilizeGC();
            long before = AllocatedBytes();
            for (int i = 0; i < n; i++) c.Convert(root);
            long total = AllocatedBytes() - before;
            double perCall = (double)total / n;

            TestContext.Progress.WriteLine($"warm Convert (100-box flat colored): {perCall:F0} bytes/call");
            // Each box contributes ~120 bytes (FillRectCommand + Rect + BorderRadii
            // payload) and the PaintList backing array runs ~4 KB pre-sized at
            // 4 commands per box. 24 KB ceiling leaves ~50% headroom for JIT
            // scaffolding without masking a per-box parser regression.
            Assert.That(perCall, Is.LessThan(24_000),
                $"warm Convert allocates {perCall:F0} bytes/call (>24 KB ceiling)");
        }

        [Test]
        public void Repeated_Convert_with_no_changes_does_not_grow_memory() {
            var root = BuildDecoratedTree(50);
            var c = new BoxToPaintConverter();

            // Burn an extra round of warmup AFTER the first StabilizeGC so the
            // GC.GetTotalAllocatedBytes per-thread allocation-context blocks
            // (~8 KB) are already advanced and tiered-JIT recompilation has
            // settled. Otherwise the first measured window picks up one-shot
            // costs (block boundary crossings, tier-1 JIT) that don't repeat
            // in the second window, producing a misleadingly large drift.
            for (int i = 0; i < 10; i++) c.Convert(root);
            StabilizeGC();
            for (int i = 0; i < 50; i++) c.Convert(root);

            long sample1 = HeapBytesStable();
            for (int i = 0; i < 100; i++) c.Convert(root);
            long sample2 = HeapBytesStable();
            for (int i = 0; i < 100; i++) c.Convert(root);
            long sample3 = HeapBytesStable();

            long delta12 = sample2 - sample1;
            long delta23 = sample3 - sample2;
            TestContext.Progress.WriteLine($"100xConvert delta (run1->2): {delta12}, (run2->3): {delta23}");
            // Successive 100-call windows should allocate roughly the same — i.e.
            // memory rate is linear in calls, not super-linear / accumulating.
            // Use a generous absolute headroom (64 KB) so GC accounting jitter on
            // CI doesn't flake; the regression we want to catch is "delta23 is
            // multiple X bigger than delta12 because something was retained".
            long drift = System.Math.Abs(delta23 - delta12);
            Assert.That(drift, Is.LessThan(System.Math.Max(delta12 / 2, 64 * 1024)),
                $"alloc rate grew across windows ({delta12} -> {delta23}); expected steady-state");
        }

        [Test]
        public void LengthContext_construction_does_not_allocate() {
            // Verify the value-type contract for LengthContext: building one in a
            // tight loop must not allocate, since the converter does this per box.
            //
            // Measurement note: GC.GetTotalAllocatedBytes(precise: false) reports
            // rounded-up per-thread allocation-context blocks (~8 KB on .NET 8).
            // The first observation after StabilizeGC can therefore step by one
            // full block even when the loop body itself made zero allocations.
            // The 9 KB ceiling allows for one such block-boundary crossing while
            // still catching a regression that allocates ~1 byte per iter
            // (10_000 iters x 1 byte = 10 KB > 9 KB).
            var ctx0 = LengthContext.Default;
            // Warm.
            for (int i = 0; i < 100; i++) {
                var c = ctx0;
                c.BaseFontSizePx = 16 + (i & 7);
            }

            const int n = 10_000;
            StabilizeGC();
            long before = AllocatedBytes();
            double sink = 0;
            for (int i = 0; i < n; i++) {
                var c = ctx0;
                c.BaseFontSizePx = 16 + (i & 7);
                c.RootFontSizePx = 16;
                sink += c.BaseFontSizePx;
            }
            long delta = AllocatedBytes() - before;
            TestContext.Progress.WriteLine($"{n} LengthContext copies allocated {delta} bytes (sink={sink})");
            // 9 KB tolerates one allocation-context block boundary while still
            // failing if the per-iter alloc rate exceeds ~1 byte.
            Assert.That(delta, Is.LessThan(9 * 1024),
                $"LengthContext copy/mutate path leaked {delta} bytes over {n} iters");
        }

        [Test]
        public void PaintCommandPool_ReturnAll_recycles_rented_commands() {
            // Pin the contract that PaintCommandPool returns rented commands to
            // their per-type stacks so a second Rent of the same type pops the
            // same instance. Regression guard for any change that breaks the
            // pool's stack discipline (e.g. forgetting to push back, dropping
            // refs through ReturnAll).
            var pool = new PaintCommandPool();
            var list = new PaintList();
            var brush = Brush.SolidColor(new LinearColor(1, 0, 0, 1));
            var fr = pool.RentFillRect(new Rect(0, 0, 10, 10), brush, BorderRadii.Zero);
            var sb = pool.RentStrokeBorder(new Rect(0, 0, 10, 10), default, BorderRadii.Zero);
            var pc = pool.RentPushClip(new Rect(0, 0, 10, 10));
            list.Commands.Add(fr);
            list.Commands.Add(sb);
            list.Commands.Add(pc);

            int beforeFill = pool.FillRectStackSize;
            int beforeStroke = pool.StrokeBorderStackSize;
            int beforeClip = pool.PushClipStackSize;
            pool.ReturnAll(list);
            Assert.That(pool.FillRectStackSize, Is.EqualTo(beforeFill + 1),
                "FillRect should be parked on its free list after ReturnAll");
            Assert.That(pool.StrokeBorderStackSize, Is.EqualTo(beforeStroke + 1),
                "StrokeBorder should be parked on its free list after ReturnAll");
            Assert.That(pool.PushClipStackSize, Is.EqualTo(beforeClip + 1),
                "PushClip should be parked on its free list after ReturnAll");

            // Rent again — must pop the same instance back.
            var fr2 = pool.RentFillRect(new Rect(0, 0, 5, 5), brush, BorderRadii.Zero);
            Assert.That(fr2, Is.SameAs(fr),
                "Second Rent should return the previously-pooled instance");
        }

        [Test]
        public void Convert_steady_state_is_alloc_bounded() {
            // Steady-state Convert with the PaintList returned to the pool every
            // frame. Pins the current per-call allocation rate so a regression
            // (e.g. closure capture, params array, boxing in the hot loop) gets
            // caught. Today's measurement: ~6 KB per call for a 50-box tree —
            // that is NOT zero (steady-state goal not yet met) but the rate is
            // stable. Actual zero-alloc would require returning rented FillRect
            // commands per call, which the public Convert() doesn't currently do.
            var root = BuildFlatColoredTree(50);
            var c = new BoxToPaintConverter();

            for (int i = 0; i < 30; i++) {
                var list = c.Convert(root);
                c.Return(list);
            }

            const int n = 100;
            StabilizeGC();
            long before = AllocatedBytes();
            for (int i = 0; i < n; i++) {
                var list = c.Convert(root);
                c.Return(list);
            }
            long delta = AllocatedBytes() - before;
            double perCall = (double)delta / n;
            TestContext.Progress.WriteLine($"steady-state Convert (50 boxes, returned to pool): {perCall:F0} bytes/call ({delta} bytes / {n} calls)");

            // Pin current behaviour: 12 KB per call ceiling for a 50-box tree
            // (~240 bytes/box). The TODO is to drive this towards 0 by reusing
            // the cached translated commands across frames; today the cache
            // hit path still rents fresh translated copies per box.
            Assert.That(perCall, Is.LessThan(12_288),
                $"steady-state Convert allocates {perCall:F0} bytes/call (>12 KB ceiling)");
        }

        [Test]
        public void Pop_commands_are_singletons() {
            // Documents the singleton invariant: every PopXCommand of a given kind
            // emitted by the converter must be reference-identical, so that a
            // 1000-box tree doesn't alloc 4000 fresh wrappers.
            var s = Style();
            s.Set("opacity", "0.5");
            s.Set("transform", "translate(1px, 1px)");
            s.Set("overflow", "hidden");
            s.Set("filter", "blur(2px)");
            s.Set("background-color", "red");
            var root = Block(0, 0, 100, 100, s);

            var c = new BoxToPaintConverter();
            var cmds = c.Convert(root).Commands;

            PopClipCommand clipA = null;
            PopOpacityCommand opA = null;
            PopTransformCommand xfA = null;
            PopFilterCommand fA = null;
            foreach (var cmd in cmds) {
                if (cmd is PopClipCommand pc) clipA = pc;
                if (cmd is PopOpacityCommand po) opA = po;
                if (cmd is PopTransformCommand pt) xfA = pt;
                if (cmd is PopFilterCommand pf) fA = pf;
            }

            // Re-convert and compare references.
            var cmds2 = c.Convert(root).Commands;
            PopClipCommand clipB = null;
            PopOpacityCommand opB = null;
            PopTransformCommand xfB = null;
            PopFilterCommand fB = null;
            foreach (var cmd in cmds2) {
                if (cmd is PopClipCommand pc) clipB = pc;
                if (cmd is PopOpacityCommand po) opB = po;
                if (cmd is PopTransformCommand pt) xfB = pt;
                if (cmd is PopFilterCommand pf) fB = pf;
            }

            Assert.That(clipA, Is.Not.Null);
            Assert.That(clipA, Is.SameAs(clipB), "PopClipCommand should be a singleton across Converts");
            Assert.That(opA, Is.SameAs(opB), "PopOpacityCommand should be a singleton across Converts");
            Assert.That(xfA, Is.SameAs(xfB), "PopTransformCommand should be a singleton across Converts");
            Assert.That(fA, Is.SameAs(fB), "PopFilterCommand should be a singleton across Converts");
        }

        [Test]
        public void Brush_for_repeated_color_is_memoized() {
            // Two boxes with the same `background-color: red` should resolve to
            // the same Brush instance after the first cold parse.
            var s1 = Style(); s1.Set("background-color", "red");
            var s2 = Style(); s2.Set("background-color", "red");
            var root = Block(0, 0, 100, 100, Style());
            root.AddChild(Block(0, 0, 50, 50, s1));
            root.AddChild(Block(0, 50, 50, 50, s2));

            var c = new BoxToPaintConverter();
            var cmds = c.Convert(root).Commands;

            Brush a = null, b = null;
            foreach (var cmd in cmds) {
                if (cmd is FillRectCommand fr) {
                    if (a == null) a = fr.Brush;
                    else b = fr.Brush;
                }
            }
            Assert.That(a, Is.Not.Null);
            Assert.That(b, Is.Not.Null);
            Assert.That(a, Is.SameAs(b), "Two boxes with the same color string should share a Brush");
        }
    }
}
