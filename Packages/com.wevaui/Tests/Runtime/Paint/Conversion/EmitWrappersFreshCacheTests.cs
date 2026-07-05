using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using static Weva.Tests.Paint.Conversion.Incremental.PaintIncrementalTestHelpers;

namespace Weva.Tests.Paint.Conversion {
    // P9 (CODE_AUDIT_FINDINGS.md) — EmitWrappersFresh used to call
    // FilterResolver / TransformResolver / MaskResolver / OpacityResolver /
    // MixBlendModeResolver per decoratable box per frame regardless of cache
    // state. WrapperEmitCache short-circuits the bundle when style.Version,
    // box.Version, abs position, and contextVersion all match the previous
    // resolution.
    //
    // The tests below pin three flavours of the contract:
    //   - no-wrapper boxes never enter the resolver bundle to begin with
    //     (the older HasWrapperProperties early-out is preserved verbatim);
    //   - unchanged frames for wrapper-bearing boxes re-hit the cache
    //     instead of re-resolving;
    //   - any property mutation that bumps style.Version (cascade write or
    //     animation tick) invalidates the cache and forces a fresh resolve.
    public class EmitWrappersFreshCacheTests {
        static BlockBox StyledBox(double w, double h, params (string, string)[] decls) {
            var s = Style();
            foreach (var (k, v) in decls) s.Set(k, v);
            return Block(0, 0, w, h, s);
        }

        [Test]
        public void No_wrapper_box_never_increments_resolve_count_across_repeated_converts() {
            // Plain decoratable box with no wrapper property — the
            // HasWrapperProperties early-out should hit on every Convert,
            // and the resolver counter must never tick.
            var box = StyledBox(100, 50, ("background-color", "red"));
            var c = new BoxToPaintConverter();
            c.Convert(box);
            c.Convert(box);
            Assert.That(c.WrapperResolveCount, Is.EqualTo(0),
                "HasWrapperProperties=false must skip the resolver bundle entirely");
            Assert.That(c.WrapperCacheHits, Is.EqualTo(0),
                "Fast-out should never even consult the wrapper cache");
            Assert.That(box.WrapperCache, Is.Null,
                "No cache instance should be allocated for a no-wrapper box");
        }

        [Test]
        public void Wrapper_box_resolves_once_then_hits_cache_on_unchanged_convert() {
            // opacity:0.5 trips HasWrapperProperties → first frame runs the
            // resolvers, second frame must hit the WrapperEmitCache fast path.
            var box = StyledBox(80, 40, ("opacity", "0.5"));
            var c = new BoxToPaintConverter();
            c.Convert(box);
            Assert.That(c.WrapperResolveCount, Is.EqualTo(1),
                "First Convert must resolve the wrapper state");
            Assert.That(c.WrapperCacheHits, Is.EqualTo(0));
            Assert.That(box.WrapperCache, Is.Not.Null,
                "Cache should be stamped after the miss");

            c.Convert(box);
            Assert.That(c.WrapperResolveCount, Is.EqualTo(1),
                "Second Convert with unchanged inputs must NOT re-resolve");
            Assert.That(c.WrapperCacheHits, Is.EqualTo(1),
                "Second Convert must take the cache-hit fast path");

            c.Convert(box);
            Assert.That(c.WrapperResolveCount, Is.EqualTo(1));
            Assert.That(c.WrapperCacheHits, Is.EqualTo(2));
        }

        [Test]
        public void Wrapper_cache_hit_emits_same_pop_balanced_commands_as_miss() {
            // Output stability check: regardless of which path emits the
            // wrapper, the PaintList must contain a PushOpacity / PopOpacity
            // pair surrounding the box's decoration.
            var box = StyledBox(80, 40, ("opacity", "0.5"), ("background-color", "red"));
            var c = new BoxToPaintConverter();
            var firstCmds = c.Convert(box).Commands;
            int firstPushes = 0, firstPops = 0;
            foreach (var cmd in firstCmds) {
                if (cmd is PushOpacityCommand) firstPushes++;
                if (cmd is PopOpacityCommand) firstPops++;
            }
            var secondCmds = c.Convert(box).Commands;
            int secondPushes = 0, secondPops = 0;
            foreach (var cmd in secondCmds) {
                if (cmd is PushOpacityCommand) secondPushes++;
                if (cmd is PopOpacityCommand) secondPops++;
            }
            Assert.That(firstPushes, Is.EqualTo(1));
            Assert.That(firstPops, Is.EqualTo(1));
            Assert.That(secondPushes, Is.EqualTo(1),
                "Cache hit must still emit the PushOpacity command");
            Assert.That(secondPops, Is.EqualTo(1),
                "Cache hit must still emit the PopOpacity command");
            Assert.That(c.WrapperCacheHits, Is.EqualTo(1));
        }

        [Test]
        public void Style_version_change_invalidates_wrapper_cache() {
            // Cascade write → new ComputedStyle.Version → wrapper cache must
            // miss and re-run the resolvers. Simulates the animator path
            // (CssAnimationRunner.Compose bumps Version on every tick).
            var box = StyledBox(80, 40, ("opacity", "0.5"));
            var c = new BoxToPaintConverter();
            c.Convert(box);
            Assert.That(c.WrapperResolveCount, Is.EqualTo(1));

            // Replace style with a fresh-Version style at opacity 0.25 —
            // mimics what an animation tick produces.
            box.Style = CloneWithNewVersion(box.Style, ("opacity", "0.25"));
            c.Convert(box);
            Assert.That(c.WrapperResolveCount, Is.EqualTo(2),
                "Style.Version bump must invalidate the wrapper cache");

            // And again — second tick should also re-resolve.
            box.Style = CloneWithNewVersion(box.Style, ("opacity", "0.10"));
            c.Convert(box);
            Assert.That(c.WrapperResolveCount, Is.EqualTo(3),
                "Each Version bump (animation frame) must re-resolve");
        }

        [Test]
        public void Mid_frame_filter_blur_change_invalidates_cache() {
            // The audit's specific worked example: a box with `filter:blur(5px)`
            // mutated mid-frame must re-run the resolvers.
            var box = StyledBox(80, 40, ("filter", "blur(5px)"));
            var c = new BoxToPaintConverter();
            c.Convert(box);
            // filter chains take the hasFilter opt-out (subtree-dependent
            // ComputeFilterScopeBounds), so the cache never stamps for them
            // — but the resolver counter must still bump on every frame,
            // confirming the bundle ran.
            long after1 = c.WrapperResolveCount;
            c.Convert(box);
            long after2 = c.WrapperResolveCount;
            Assert.That(after1, Is.EqualTo(1));
            Assert.That(after2, Is.EqualTo(2),
                "Filter chains opt out of the wrapper cache (subtree-dependent bounds)");

            // Now mutate filter and confirm we still re-run.
            box.Style = CloneWithNewVersion(box.Style, ("filter", "blur(8px)"));
            c.Convert(box);
            Assert.That(c.WrapperResolveCount, Is.EqualTo(3),
                "Style.Version bump must keep re-resolving on every Convert");
        }

        [Test]
        public void Hundred_no_wrapper_boxes_across_hundred_converts_keep_resolve_count_at_zero() {
            // Forest of plain boxes — HasWrapperProperties=false for every
            // one. 100 boxes × 100 converts should still see zero resolver
            // calls, confirming the fast-out covers steady-state cost.
            var root = StyledBox(1000, 1000);
            for (int i = 0; i < 99; i++) {
                var child = StyledBox(10, 10, ("background-color", "red"));
                root.AddChild(child);
            }
            var c = new BoxToPaintConverter();
            for (int frame = 0; frame < 100; frame++) {
                c.Convert(root);
            }
            Assert.That(c.WrapperResolveCount, Is.EqualTo(0),
                "100 no-wrapper boxes × 100 frames must never enter the resolver bundle");
        }

        [Test]
        public void Hundred_wrapper_boxes_across_hundred_converts_resolve_once_each() {
            // Wrapper-bearing forest — first frame resolves all 100, the
            // remaining 99 frames must take the cache-hit fast path on each.
            var root = StyledBox(1000, 1000);
            for (int i = 0; i < 99; i++) {
                var child = StyledBox(10, 10, ("opacity", "0.5"));
                root.AddChild(child);
            }
            var c = new BoxToPaintConverter();
            c.Convert(root); // Frame 1: 99 misses (root has no opacity so it stays at 0)
            long afterFirst = c.WrapperResolveCount;
            for (int frame = 0; frame < 99; frame++) {
                c.Convert(root);
            }
            Assert.That(c.WrapperResolveCount, Is.EqualTo(afterFirst),
                "Frames 2..100 must all cache-hit; no further resolutions");
            Assert.That(c.WrapperCacheHits, Is.EqualTo(99 * 99),
                "99 wrapper children × 99 cache-hit frames");
        }

        [Test]
        public void Animated_opacity_keeps_resolving_each_frame_the_animator_ticks() {
            // The audit's other worked example: an `opacity` transition
            // bumps style.Version each frame. EmitWrappersFresh must
            // re-resolve every tick, never serving the stale cached value.
            var box = StyledBox(80, 40, ("opacity", "1.0"));
            var c = new BoxToPaintConverter();
            c.Convert(box);
            // First frame stamps the cache (opacity:1 → no PushOpacity emitted
            // but the wrapper cache still records the resolved state).
            long ticks = c.WrapperResolveCount;

            // Five animation ticks, each producing a new Version with a
            // distinct opacity value.
            double[] values = { 0.9, 0.7, 0.5, 0.3, 0.1 };
            foreach (var v in values) {
                box.Style = CloneWithNewVersion(box.Style, ("opacity", v.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                c.Convert(box);
            }
            Assert.That(c.WrapperResolveCount, Is.EqualTo(ticks + values.Length),
                "Each animator tick must re-resolve the wrapper state");
        }

        [Test]
        public void Layout_version_bump_invalidates_wrapper_cache() {
            // Box.Version bumps on layout — transform-origin pivot depends
            // on Width/Height which can shift without a style change
            // (flex / grid re-distribution). The cache key must catch this.
            var box = StyledBox(80, 40, ("transform", "rotate(45deg)"));
            var c = new BoxToPaintConverter();
            c.Convert(box);
            Assert.That(c.WrapperResolveCount, Is.EqualTo(1));
            c.Convert(box);
            Assert.That(c.WrapperResolveCount, Is.EqualTo(1),
                "Unchanged inputs → cache hit");

            // Simulate a layout-pass rewrite of the box's geometry.
            BumpBoxVersion(box);
            box.Width = 160; box.Height = 80;
            c.Convert(box);
            Assert.That(c.WrapperResolveCount, Is.EqualTo(2),
                "Box.Version bump must invalidate the wrapper cache");
        }

        [Test]
        public void Ancestor_move_invalidates_descendant_wrapper_cache() {
            // Parent re-position shifts absX/absY for the child but doesn't
            // bump the child's Box.Version (PaintBoxCache's whole point is
            // to skip on ancestor moves via box-local storage). The wrapper
            // cache key must catch the abs shift because filter bounds /
            // transform pivots bake the absolute coords.
            var root = StyledBox(1000, 1000);
            var child = StyledBox(80, 40, ("transform", "rotate(30deg)"));
            child.X = 10; child.Y = 20;
            root.AddChild(child);
            var c = new BoxToPaintConverter();
            c.Convert(root);
            Assert.That(c.WrapperResolveCount, Is.EqualTo(1));
            c.Convert(root);
            Assert.That(c.WrapperResolveCount, Is.EqualTo(1));

            // Move child within parent (X/Y shift, but Box.Version unchanged
            // — simulates a parent-driven flex/grid re-flow that didn't
            // touch the child's own layout key).
            child.X = 50;
            c.Convert(root);
            Assert.That(c.WrapperResolveCount, Is.EqualTo(2),
                "absX shift must invalidate the wrapper cache");
        }

        [Test]
        public void InvalidateAll_drops_wrapper_caches() {
            // InvalidateAll bumps contextVersion AND nulls every box's
            // WrapperCache field. Next Convert must miss.
            var box = StyledBox(80, 40, ("opacity", "0.5"));
            var c = new BoxToPaintConverter();
            c.Convert(box);
            Assert.That(box.WrapperCache, Is.Not.Null);
            Assert.That(c.WrapperResolveCount, Is.EqualTo(1));

            c.InvalidateAll();
            Assert.That(box.WrapperCache, Is.Null,
                "InvalidateAll must null the per-box WrapperCache field");

            c.Convert(box);
            Assert.That(c.WrapperResolveCount, Is.EqualTo(2),
                "Post-InvalidateAll Convert must re-resolve");
        }
    }
}
