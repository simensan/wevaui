using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Grid.GridTestHelpers;

namespace Weva.Tests.Layout.Grid {
    // CSS Grid L1 §11.4 / §11.5: a track whose MIN sizing function is
    // `min-content` / `max-content` / `auto` must compute its base from
    // the intrinsic contribution of its items. Pre-E10, `GridTrackSizing`
    // unconditionally pre-inflated `minmax(<anything>, <length|%>)` tracks
    // to their max — which made `minmax(auto, 100px)` resolve to 100 even
    // when the item's intrinsic was smaller, collapsing the spec's
    // "intrinsic floor, under max" semantics. After E10 the pre-inflation
    // is gated on the MIN sizing function being concrete (length /
    // percentage); intrinsic mins now flow through Phase 1b → final max
    // clamp.
    public class GridMinmaxIntrinsicFloorTests {
        // Test 1 (E10): `minmax(min-content, 1fr)` with an item that has a
        // 56-px min-content contribution (7-char mono word @ 0.5em*16px =
        // 8px/char). Container is narrower than the item, so fr cannot grow
        // the track — the intrinsic floor must keep the track at >= the
        // item's min-content rather than collapsing to 0.
        [Test]
        public void Minmax_min_content_with_intrinsic_item_floors_at_min_content_E10() {
            // 7 chars * 8px = 56px min-content for the unbreakable word.
            // Container is only 30px wide, so fr has nothing to distribute
            // and the only thing keeping the track from 0 is the intrinsic
            // contribution.
            const string css = @"
                .grid { display: grid; grid-template-columns: minmax(min-content, 1fr); width: 30px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div>Hellobd</div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            var c0 = ChildAt(grid, 0);
            // Track must be at least the item's intrinsic (56px), not 0,
            // even though the container is only 30px. v1 contributes the
            // item's max-content (= min-content here, since one word) so
            // an exact-equal check is safe; widen to >= 50 to stay robust
            // against future min-content/max-content split.
            Assert.That(c0.Width, Is.GreaterThanOrEqualTo(50));
        }

        // Test 2 (E10): `minmax(auto, 100px)` with a 60-px-wide item must
        // resolve to 60 (intrinsic floor, under max). Pre-fix this was
        // unconditionally inflated to 100 by the "grow base toward
        // definite max" pass — the regression that motivated E10.
        [Test]
        public void Minmax_auto_with_concrete_max_resolves_to_intrinsic_under_max_E10() {
            const string css = @"
                .grid { display: grid; grid-template-columns: minmax(auto, 100px); width: 500px; }
                .item { width: 60px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            var c0 = ChildAt(grid, 0);
            // Track sizes to item's intrinsic (60), NOT the 100px max.
            // Pre-E10 this asserted 100 — that was the bug.
            Assert.That(c0.Width, Is.EqualTo(60).Within(0.01));
        }

        // Test 3 (E10): `minmax(auto, 1fr)` with an 80-px-intrinsic item in
        // a 200-px container. The track must (a) stretch with fr to fill
        // the container (200), and (b) never go below the intrinsic floor
        // (80) — the upper bound is fr, so fr distribution dominates here.
        // We use a 10-char unbreakable word (mono 0.5em*16px = 8px/char →
        // 80-px min/max-content) so the item's intrinsic comes from text
        // rather than an explicit width — `justify-self: stretch` then
        // re-sizes the item to the resolved track width, giving us a
        // direct read on the track size via `c0.Width`.
        [Test]
        public void Minmax_auto_with_fr_max_fills_container_and_floors_at_intrinsic_E10() {
            const string css = @"
                .grid { display: grid; grid-template-columns: minmax(auto, 1fr); width: 200px; }
            ";
            var (root, _, _) = Build(
                // 10-char word: 10 * 8px = 80-px unbreakable run.
                "<div class=\"grid\"><div>HelloWorld</div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            var c0 = ChildAt(grid, 0);
            // 1fr stretches the lone track across the container; the
            // intrinsic floor (80) is satisfied as a side effect since
            // 200 > 80. With default `justify-items: stretch` the item
            // resolves to the full track width.
            Assert.That(c0.Width, Is.EqualTo(200).Within(0.01));
        }

        // Test 4 (E10, regression-pin for non-regression): `minmax(240px,
        // 22%)` in a 2000-px-wide container should still pre-inflate the
        // base to 22% * 2000 = 440 (the `.hud` motivation case that lines
        // 148-159's prior unconditional inflation was added for). The E10
        // gate on `minIsConcrete` must not lose this behavior.
        [Test]
        public void Minmax_concrete_min_concrete_max_pre_inflates_base_to_max_E10_no_regression() {
            const string css = @"
                .grid { display: grid; grid-template-columns: minmax(240px, 22%) 1fr; width: 2000px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 2400);
            var grid = FindGridByClass(root, "grid");
            var c0 = ChildAt(grid, 0);
            var c1 = ChildAt(grid, 1);
            // 22% of 2000 = 440 > 240, so the minmax track pre-inflates to
            // 440 and the 1fr middle absorbs the rest (2000 - 440 = 1560).
            Assert.That(c0.Width, Is.EqualTo(440).Within(0.01));
            Assert.That(c1.Width, Is.EqualTo(1560).Within(0.01));
        }
    }
}
