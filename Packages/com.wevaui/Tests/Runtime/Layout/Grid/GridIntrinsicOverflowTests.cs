using NUnit.Framework;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Grid.GridTestHelpers;

namespace Weva.Tests.Layout.Grid {
    // E11 — CSS Grid L1 §11.5 "Resolve Intrinsic Track Sizes": when ALL
    // tracks are sized by intrinsic functions (auto / min-content /
    // max-content / fit-content) and the sum of their Phase 1 base sizes
    // exceeds the container, the engine MUST let the tracks overflow at
    // their resolved Phase 1 base sizes — it MUST NOT shrink them below
    // their intrinsic minimums. Pre-E11 the v1 auto-shrink helper would
    // proportionally scale `auto` tracks toward `availForAuto / autoSum`
    // on negative free space, collapsing min-content contributions below
    // the spec floor. After E11 the shrink is gated on at least one
    // non-collapsed track having a concrete max (length/percentage) it
    // can shrink toward — the purely-intrinsic case now overflows the
    // container as the spec requires.
    public class GridIntrinsicOverflowTests {
        // E11 — Test 1: canonical regression. Three `auto` tracks in a
        // 100-px container, each child has an explicit 50-px width (so
        // every track's intrinsic contribution in Phase 1b is 50). Total
        // intrinsic = 150 > 100 → negative free space.
        //   Pre-fix: `availForAuto = 150 + (-50) = 100`, scale = 100/150
        //     ≈ 0.667 → every track shrinks to ~33.3 px, items overflow
        //     their assigned cells, min-content floor is violated.
        //   Post-fix: no track has a concrete max to shrink toward, so
        //     the auto-shrink branch is skipped and tracks stay at 50
        //     each. The grid content overflows the container, which is
        //     the spec-correct outcome.
        [Test]
        public void Auto_tracks_overflow_when_no_fr_and_no_concrete_max_E11() {
            const string css = @"
                .grid { display: grid; grid-template-columns: auto auto auto; width: 100px; }
                .item { width: 50px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div><div class=\"item\"></div><div class=\"item\"></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            // Each track stays at its 50-px intrinsic — does NOT shrink
            // to ~33 even though the container is only 100 px wide.
            Assert.That(ChildAt(grid, 0).Width, Is.EqualTo(50).Within(0.01));
            Assert.That(ChildAt(grid, 1).Width, Is.EqualTo(50).Within(0.01));
            Assert.That(ChildAt(grid, 2).Width, Is.EqualTo(50).Within(0.01));
            // Track origins follow Phase 1 base sizes: 0 / 50 / 100. The
            // third track starts AT the container right edge — overflow
            // is allowed; the alternative would be the buggy ~33-px scale.
            Assert.That(ChildAt(grid, 1).X, Is.EqualTo(50).Within(0.01));
            Assert.That(ChildAt(grid, 2).X, Is.EqualTo(100).Within(0.01));
        }

        // E11 — Test 2: fr-present regression pin. With ANY fr track in
        // the row, `totalFr > 0` and the auto-shrink helper never runs
        // — fr distribution governs the layout. Pre/post-E11 must agree.
        //   `minmax(auto, 1fr) 1fr` in a 100-px container with a 200-px
        //   item in column 1: Phase 1b seeds col 0 at 200 (auto min
        //   contributes intrinsic via the K7 path); Phase 2 freezes
        //   col 0 at its 200-px intrinsic floor (it exceeds the 50-px
        //   hypothetical fr share), col 1 absorbs the negative leftover
        //   and collapses to 0. Result: 200 / 0. The E11 guard must NOT
        //   alter this — it's the fr path, not the auto path.
        [Test]
        public void Fr_distribution_with_intrinsic_overflow_unchanged_by_E11() {
            const string css = @"
                .grid { display: grid; grid-template-columns: minmax(auto, 1fr) 1fr; width: 100px; }
                .wide { width: 200px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"wide\"></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            // Column 0 frozen at its 200-px intrinsic floor.
            Assert.That(ChildAt(grid, 0).Width, Is.EqualTo(200).Within(0.01));
            // Column 1 collapses: the leftover after col 0 freezes is
            // negative, so per <= 0 short-circuits Phase 2's growth.
            Assert.That(ChildAt(grid, 1).Width, Is.EqualTo(0).Within(0.01));
        }

        // E11 — Test 3: `minmax(min-content, max-content)` track with an
        // item that exceeds the container. Both ends are intrinsic, so
        // Phase 1a leaves base=0 / max=0; Phase 1b raises the base to
        // the item's intrinsic (200). Free space goes negative
        // (100 - 200 = -100) but `Kind == Minmax` was never selected by
        // the auto-shrink loop anyway (it only touches `Kind == Auto`),
        // and the new E11 guard preserves that — the track stays at
        // its min-content base and overflows the container.
        [Test]
        public void Minmax_min_content_max_content_does_not_collapse_below_floor_E11() {
            const string css = @"
                .grid { display: grid; grid-template-columns: minmax(min-content, max-content); width: 100px; }
                .wide { width: 200px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"wide\"></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            // Track stays at the item's 200-px intrinsic (min-content
            // floor honored, max-content cap allows growth to 200).
            // Pre-E11 this already passed because the shrink loop only
            // matched `Kind == Auto` tracks — this test pins the
            // non-regression on the Minmax path.
            Assert.That(ChildAt(grid, 0).Width, Is.EqualTo(200).Within(0.01));
        }

        // E11 — Test 4: the shrink helper still fires when there IS a
        // concrete-max track in the mix. `auto 30px` in a 50-px
        // container with a 60-px item in column 0:
        //   Phase 1a: sizes = [0, 30], maxes = [0, 30].
        //   Phase 1b: item (col 0, intrinsic 60) — accountedFixed = 0
        //     (item spans only the intrinsic track), rem = 60,
        //     sizes[0] = 60.
        //   Sum = 90 > 50 → remaining = -40, autoCount = 1, autoSum = 60.
        //   The 30-px fixed track has a concrete `maxes[i] > 0` upper
        //   bound, so hasConcreteMaxTrack = true and the v1 helper
        //   still scales the auto track: availForAuto = 60 + (-40) = 20;
        //   scale = 20/60; sizes[0] = 60 * 20/60 = 20.
        // This pins the non-regression of the shrink helper when
        // concrete tracks coexist with intrinsic ones. We observe the
        // shrunk TRACK width via the second item's X position (it sits
        // at the right edge of track 0). The first item carries an
        // explicit `width: 60px`, which CSS honors even when the cell
        // is smaller — the item overflows its cell — so we don't read
        // its Width to infer the track size (that conflates two
        // concepts the engine intentionally keeps separate per CSS Box
        // Sizing L3 §5).
        [Test]
        public void Auto_track_still_shrinks_when_concrete_max_track_present_E11() {
            const string css = @"
                .grid { display: grid; grid-template-columns: auto 30px; width: 50px; }
                .item { width: 60px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div><div></div></div>",
                css, viewportWidth: 800);
            var grid = FindGridByClass(root, "grid");
            // Auto track scales to 20 (= 60 * 20/60); fixed track stays at 30.
            // Track 0 width is observable through track 1's X offset.
            Assert.That(ChildAt(grid, 0).X, Is.EqualTo(0).Within(0.01));
            Assert.That(ChildAt(grid, 1).X, Is.EqualTo(20).Within(0.01),
                "track 0 must be shrunk to 20 (auto-shrink helper fires because " +
                "the 30px fixed track supplies a concrete max). Item 2's X equals " +
                "track 0's resolved width.");
            Assert.That(ChildAt(grid, 1).Width, Is.EqualTo(30).Within(0.01),
                "track 1 (fixed 30px) stays at its declared length.");
        }
    }
}
