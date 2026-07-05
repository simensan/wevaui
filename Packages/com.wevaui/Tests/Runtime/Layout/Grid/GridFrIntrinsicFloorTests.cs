using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Grid.GridTestHelpers;

namespace Weva.Tests.Layout.Grid {
    // K7: CSS Grid L1 §11.7 "Expand Flexible Tracks" sizes each flexible
    // track as `max(baseSize, share × flexFactor)`, NOT
    // `baseSize + share × flexFactor`. Pre-K7 the Phase 2 distribution
    // loop in `GridTrackSizing.Resolve` did `sizes[i] += per * frVal`,
    // adding the share on top of any intrinsic content absorbed in
    // Phase 1b — over-growing fr tracks that had already received an
    // intrinsic floor. K7 also introduced the spec's iterative
    // refinement (§11.7.1 step 3): when a flexible track's intrinsic
    // floor exceeds its hypothetical share, it freezes at the floor
    // and the leftover/divisor are recomputed for the remaining flex
    // tracks.
    //
    // Note: Phase 1b currently only contributes intrinsic to bare `fr`
    // tracks when the container is INDEFINITE (see the
    // `(IsFlexible && !containerIsDefinite)` gate). To exercise the
    // intrinsic floor in a definite container these tests use the
    // explicit `minmax(auto, 1fr)` form, whose `MinKind == Auto`
    // makes `IsIntrinsic` true so Phase 1b contributes regardless of
    // container definiteness. Lifting the definite-container gate on
    // bare `fr` is a separate cleanup (out of K7 scope).
    public class GridFrIntrinsicFloorTests {
        // K7 — Test 1: canonical over-grow regression case.
        //   3-column `minmax(auto, 1fr) 1fr 1fr` in a 300-px container
        //   with a 200-px-wide item in column 1.
        //   * Phase 1b: column 1 absorbs the item's 200-px intrinsic
        //     → sizes = [200, 0, 0].
        //   * Phase 2 pass 0: leftover = 300; per = 100. Column 1's
        //     intrinsic (200) > 100*1 → freeze at 200.
        //   * Phase 2 pass 1: leftover = 100; per = 50. Columns 2 & 3
        //     each take max(0, 50) = 50.
        //   * Total = 200 + 50 + 50 = 300 (fills exactly).
        //   * PRE-K7 (`+=` formula): column 1 = 200 + 33.33 ≈ 233.33,
        //     columns 2/3 = 33.33 each, total = 300 but column 1
        //     mis-sized (over-grown).
        [Test]
        public void Fr_track_with_intrinsic_does_not_overgrow_K7() {
            const string css = @"
                .grid { display: grid; grid-template-columns: minmax(auto, 1fr) 1fr 1fr; width: 300px; }
                .wide { width: 200px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"wide\"></div><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            var c0 = ChildAt(grid, 0);
            var c1 = ChildAt(grid, 1);
            var c2 = ChildAt(grid, 2);
            // Column 1 clamps at its 200-px intrinsic floor (NOT 200 + per).
            Assert.That(c0.Width, Is.EqualTo(200).Within(0.01));
            // Iterative refinement: with column 1 frozen, columns 2 & 3
            // split the remaining 100 evenly → 50 each.
            Assert.That(c1.Width, Is.EqualTo(50).Within(0.01));
            Assert.That(c2.Width, Is.EqualTo(50).Within(0.01));
        }

        // K7 — Test 2: no intrinsic on any fr track. The fix must be a
        // no-op when no fr track has been seeded by Phase 1b: pure
        // share distribution still works.
        //   `1fr 1fr` in a 200-px container, empty items.
        //   * Phase 1a: sizes = [0, 0].
        //   * Phase 2: remaining = 200; per = 100; both columns become
        //     max(0, 100) = 100.
        [Test]
        public void Fr_pure_share_distribution_no_intrinsic_K7() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 1fr 1fr; width: 200px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            Assert.That(ChildAt(grid, 0).Width, Is.EqualTo(100).Within(0.01));
            Assert.That(ChildAt(grid, 1).Width, Is.EqualTo(100).Within(0.01));
        }

        // K7 — Test 3: mixed fixed + fr, no items contributing intrinsic.
        //   `100px 1fr 1fr` in a 400-px container, empty items.
        //   * Phase 1a: sizes = [100, 0, 0]; usedFixed = 100.
        //   * Phase 2: remaining = 400 - 100 = 300; per = 150; columns
        //     2 & 3 become max(0, 150) = 150 each. Column 1 stays at
        //     its fixed 100.
        [Test]
        public void Mixed_fixed_and_fr_distributes_remaining_K7() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 100px 1fr 1fr; width: 400px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div></div><div></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            Assert.That(ChildAt(grid, 0).Width, Is.EqualTo(100).Within(0.01));
            Assert.That(ChildAt(grid, 1).Width, Is.EqualTo(150).Within(0.01));
            Assert.That(ChildAt(grid, 2).Width, Is.EqualTo(150).Within(0.01));
        }

        // K7 — Test 4: iterative refinement redistributes the share
        // surrendered by a frozen flex track.
        //   `minmax(auto, 1fr) 1fr` in a 300-px container with a 200-px
        //   item in column 1.
        //   * Phase 1b: sizes = [200, 0].
        //   * Phase 2 pass 0: leftover = 300; per = 150. Column 1's
        //     intrinsic (200) > 150*1, so it freezes at 200.
        //   * Phase 2 pass 1: leftover = 300 - 200 = 100; per = 100/1 = 100.
        //     Column 2 = max(0, 100) = 100.
        //   * Total = 300 (fills container exactly).
        //   * PRE-K7 (no refinement, `+=` formula): column 1 = 200 + (100/2)
        //     = 250; column 2 = 0 + 50 = 50; total = 300 but column 1
        //     mis-sized.
        //   Asserts the intrinsic floor wins AND the share leftover from
        //   freezing flows to the remaining flex track.
        [Test]
        public void Fr_intrinsic_floor_redistributes_to_other_flex_K7() {
            const string css = @"
                .grid { display: grid; grid-template-columns: minmax(auto, 1fr) 1fr; width: 300px; }
                .wide { width: 200px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"wide\"></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            var c0 = ChildAt(grid, 0);
            var c1 = ChildAt(grid, 1);
            // Column 1 frozen at its 200-px intrinsic floor (NOT 250).
            Assert.That(c0.Width, Is.EqualTo(200).Within(0.01));
            // Column 2 absorbs the 100-px leftover after column 1 freezes.
            Assert.That(c1.Width, Is.EqualTo(100).Within(0.01));
        }
    }
}
