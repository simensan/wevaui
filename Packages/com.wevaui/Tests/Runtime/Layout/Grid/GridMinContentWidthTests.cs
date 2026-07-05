using System.Linq;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout.Grid {
    // Tests for real grid-container min-content inline size, wired into the
    // CSS Grid L1 §6.6 automatic-minimum floor for fr tracks. Prior to this
    // feature, nested GridBoxes contributed 0 as their min-content to the
    // parent grid's fr-floor Phase 1c, leaving them under-floored.
    //
    // Spec math: under a min-content constraint (available inline size = 0),
    //   - Fixed px tracks   → their declared size
    //   - Auto / fr tracks  → max of items' min-content contributions
    //   - Percentage tracks → 0 (indefinite container)
    //   - Sum + (n-1)*columnGap + container horizontal frame = min-content
    //   - Clamped by container's own min-width / max-width
    //
    // MonoFontMetrics: charWidthEm = 0.5, so at 16px each char = 8px wide.
    public class GridMinContentWidthTests {

        static Box FirstWithClass(Box root, string cls) =>
            AllBoxes(root).FirstOrDefault(b =>
                b.Element != null && !(b is TextRun)
                && (b.Element.GetAttribute("class") ?? "").Split(' ').Contains(cls));

        // -----------------------------------------------------------------------
        // 1. Fixed px columns — min-content = sum of fixed sizes + gaps + frame.
        // -----------------------------------------------------------------------
        [Test]
        public void Fixed_px_tracks_contribute_their_declared_size() {
            // .grid: 3 explicit px columns = 80+120+60 = 260, gap=10 (×2=20).
            // No padding/border on the grid itself.
            // Min-content = 260 + 20 = 280px.
            // Outer board is a 1fr column that would shrink to 0 without the
            // grid's min-content floor — must be ≥ 280.
            const string css =
                ".board { display: grid; grid-template-columns: 1fr; width: 200px; }" +
                ".grid  { display: grid; grid-template-columns: 80px 120px 60px; gap: 10px; }" +
                ".item  { height: 20px; }";
            const string html =
                "<div class='board'>" +
                "<div class='grid'>" +
                "<div class='item'></div>" +
                "<div class='item'></div>" +
                "<div class='item'></div>" +
                "</div>" +
                "</div>";
            var (root, _, _) = Build(html, css, viewportWidth: 300, viewportHeight: 300);

            var grid = FirstWithClass(root, "grid");
            Assert.That(grid, Is.Not.Null);
            // Fixed-track min-content = 80 + 120 + 60 + 10 + 10 = 280.
            // The outer 1fr column floors at the nested grid's min-content
            // so the grid is at least 280px wide.
            Assert.That(grid.Width, Is.GreaterThanOrEqualTo(280 - 1),
                $"fixed tracks must contribute their px sizes, got {grid.Width:F1}");
        }

        // -----------------------------------------------------------------------
        // 2. Auto columns sized from items' text min-content.
        //    "Hello" = 5 chars × 8 = 40px; padded item (4px pad each side) = 48px.
        //    "Worlds" = 6 chars × 8 = 48px; padded item = 56px.
        //    min-content = 48 + 56 + gap(8) = 112px.
        // -----------------------------------------------------------------------
        [Test]
        public void Auto_tracks_floor_at_items_longest_word_width() {
            // Outer 1fr column at 80px would squash auto tracks without the
            // min-content floor. The nested grid's 2-auto-column min-content
            // must push the outer track above 80.
            const string css =
                ".board { display: grid; grid-template-columns: 1fr; width: 80px; }" +
                ".grid  { display: grid; grid-template-columns: auto auto; gap: 8px; }" +
                ".a     { padding: 0 4px; }" +
                ".b     { padding: 0 4px; }";
            const string html =
                "<div class='board'>" +
                "<div class='grid'>" +
                "<div class='a'>Hello</div>" +     // 5 chars × 8 = 40, +8 frame = 48
                "<div class='b'>Worlds</div>" +    // 6 chars × 8 = 48, +8 frame = 56
                "</div>" +
                "</div>";
            var (root, _, _) = Build(html, css, viewportWidth: 200, viewportHeight: 200);

            var grid = FirstWithClass(root, "grid");
            Assert.That(grid, Is.Not.Null);
            // min-content = 48 (Hello+frame) + 56 (Worlds+frame) + 8 (gap) = 112
            // Outer 1fr track is floored at ≥112 so grid.Width ≥ 112.
            Assert.That(grid.Width, Is.GreaterThanOrEqualTo(112 - 1),
                $"auto tracks must floor at longest-word min-content, got {grid.Width:F1}");
        }

        // -----------------------------------------------------------------------
        // 3. fr tracks floor at auto minimum (§7.2.4/§6.6): under min-content
        //    constraint, fr = auto minimum = items' min-content.
        // -----------------------------------------------------------------------
        [Test]
        public void Fr_sub_tracks_floor_at_items_min_content_contribution() {
            // Nested grid with two 1fr sub-columns, no gap.
            // Left item: fixed 60px wide (contributes 60 to col0 min-content).
            // Right item: text "ABCD" = 4×8 = 32px (contributes 32 to col1).
            // Nested min-content = 60 + 32 = 92px.
            // Outer board: 1fr column, 70px wide → would squash to 70 without floor.
            const string css =
                ".board { display: grid; grid-template-columns: 1fr; width: 70px; }" +
                ".grid  { display: grid; grid-template-columns: 1fr 1fr; }" +
                ".fixed { width: 60px; height: 10px; }" +
                ".text  { }";
            const string html =
                "<div class='board'>" +
                "<div class='grid'>" +
                "<div class='fixed'></div>" +
                "<div class='text'>ABCD</div>" +
                "</div>" +
                "</div>";
            var (root, _, _) = Build(html, css, viewportWidth: 200, viewportHeight: 200);

            var grid = FirstWithClass(root, "grid");
            Assert.That(grid, Is.Not.Null);
            // min-content: col0 = 60 (from fixed item), col1 = 32 (4 chars×8)
            // Nested min-content = 92; outer 1fr floors at ≥ 92.
            Assert.That(grid.Width, Is.GreaterThanOrEqualTo(92 - 1),
                $"fr sub-tracks must floor at auto minimum, got {grid.Width:F1}");
        }

        // -----------------------------------------------------------------------
        // 4. Column gap is included in the min-content sum.
        // -----------------------------------------------------------------------
        [Test]
        public void Column_gap_is_included_in_min_content() {
            // 3 items, 2 gaps of 20px each = 40px gap total.
            // Items: "Hi" = 2×8 = 16px each, no padding → col min = 16 each.
            // min-content = 16+16+16 + 40 = 88.
            // Outer 1fr at 50px → floors at ≥ 88.
            const string css =
                ".board { display: grid; grid-template-columns: 1fr; width: 50px; }" +
                ".grid  { display: grid; grid-template-columns: auto auto auto; column-gap: 20px; }";
            const string html =
                "<div class='board'>" +
                "<div class='grid'>" +
                "<div>Hi</div>" +
                "<div>Hi</div>" +
                "<div>Hi</div>" +
                "</div>" +
                "</div>";
            var (root, _, _) = Build(html, css, viewportWidth: 200, viewportHeight: 200);

            var grid = FirstWithClass(root, "grid");
            Assert.That(grid, Is.Not.Null);
            // gap contributes: 2 × 20 = 40px added on top of 3×16 = 48 → total 88.
            Assert.That(grid.Width, Is.GreaterThanOrEqualTo(88 - 1),
                $"column gap must be included, got {grid.Width:F1}");
        }

        // -----------------------------------------------------------------------
        // 5. Container padding + border are part of the frame (§5.2).
        // -----------------------------------------------------------------------
        [Test]
        public void Container_frame_padding_and_border_are_included_in_min_content() {
            // Single 1fr column, item "ABCDE" = 40px.
            // Grid has 10px padding each side + 3px border each side = 26px frame.
            // min-content = 40 + 26 = 66px.
            // Outer board 1fr at 40px → floors at ≥ 66.
            const string css =
                ".board { display: grid; grid-template-columns: 1fr; width: 40px; }" +
                ".grid  { display: grid; grid-template-columns: 1fr;" +
                "          padding: 0 10px; border: 3px solid black; }";
            const string html =
                "<div class='board'>" +
                "<div class='grid'><div>ABCDE</div></div>" +
                "</div>";
            var (root, _, _) = Build(html, css, viewportWidth: 200, viewportHeight: 200);

            var grid = FirstWithClass(root, "grid");
            Assert.That(grid, Is.Not.Null);
            // frame = 10+10+3+3 = 26; content min = 40px ("ABCDE") → total = 66.
            Assert.That(grid.Width, Is.GreaterThanOrEqualTo(66 - 1),
                $"container frame (pad+border) must add to min-content, got {grid.Width:F1}");
        }

        // -----------------------------------------------------------------------
        // 6. min-content < fair share → fr track stays at proportional share.
        //    This is the non-regression case: must NOT inflate beyond share.
        // -----------------------------------------------------------------------
        [Test]
        public void Nested_grid_min_content_below_share_does_not_inflate_track() {
            // Outer board: 3 equal 1fr cols, width=300px (content 300, no gap).
            // Each fair share = 100px.
            // Nested grid in col1: 2 auto columns, item "Hi" (2×8=16) each.
            // Nested min-content = 16 + 16 + 0 = 32px < 100px → no floor pressure.
            const string css =
                ".board { display: grid; grid-template-columns: 1fr 1fr 1fr; width: 300px; }" +
                ".nested { display: grid; grid-template-columns: auto auto; }" +
                ".other { height: 10px; }";
            const string html =
                "<div class='board'>" +
                "<div class='nested'>" +
                "<div>Hi</div><div>Hi</div>" +
                "</div>" +
                "<div class='other'></div>" +
                "<div class='other'></div>" +
                "</div>";
            var (root, _, _) = Build(html, css, viewportWidth: 400, viewportHeight: 200);

            var nested = FirstWithClass(root, "nested");
            Assert.That(nested, Is.Not.Null);
            // min-content (32) < share (100) → no floor → nested stays at 100.
            Assert.That(nested.Width, Is.EqualTo(100).Within(1.5),
                $"nested min-content below share must not inflate track, got {nested.Width:F1}");
        }

        // -----------------------------------------------------------------------
        // 7. min-content > fair share → fr track FLOORS at the min-content.
        //    This is the new floor-behavior test (the key feature of this task).
        // -----------------------------------------------------------------------
        [Test]
        public void Nested_grid_min_content_above_share_floors_the_fr_track() {
            // Outer board: 3 equal 1fr cols, width=300px (content 300, no gap).
            // Each fair share = 100px.
            // Nested grid in col1: 2 auto columns, no gap.
            //   Col0: item "ABCDEFGHIJK" = 11 chars × 8 = 88px min-content.
            //   Col1: item with fixed width 80px.
            //   Nested min-content = 88 + 80 = 168px > 100px (fair share).
            // → The outer 1fr track must floor at ≥ 168.
            const string css =
                ".board  { display: grid; grid-template-columns: 1fr 1fr 1fr; width: 300px; }" +
                ".nested { display: grid; grid-template-columns: auto auto; }" +
                ".longw  { }" +
                ".fixd   { width: 80px; height: 10px; }" +
                ".other  { height: 10px; }";
            const string html =
                "<div class='board'>" +
                "<div class='nested'>" +
                "<div class='longw'>ABCDEFGHIJK</div>" +  // 11×8=88 min-content
                "<div class='fixd'></div>" +               // fixed 80px
                "</div>" +
                "<div class='other'></div>" +
                "<div class='other'></div>" +
                "</div>";
            var (root, _, _) = Build(html, css, viewportWidth: 400, viewportHeight: 200);

            var nested = FirstWithClass(root, "nested");
            Assert.That(nested, Is.Not.Null);
            // min-content = 88 + 80 = 168 > 100 (fair share).
            // The outer 1fr track is floored at the nested grid's min-content.
            Assert.That(nested.Width, Is.GreaterThanOrEqualTo(168 - 1),
                $"fr track must floor at nested grid min-content (168), got {nested.Width:F1}");
            // And the board itself (300px container) must not be exceeded.
            var board = FirstWithClass(root, "board");
            Assert.That(nested.X + nested.Width, Is.LessThanOrEqualTo(board.Width + 1),
                "nested must not overflow the board");
        }

        // -----------------------------------------------------------------------
        // 8. A nested grid whose items contribute no min-content (all have
        //    fixed 0-height placeholders with no inline content) must not
        //    floor the outer fr track above 0. This verifies the algorithm
        //    correctly returns 0 for empty auto tracks (no items ≥ 0).
        // -----------------------------------------------------------------------
        [Test]
        public void Nested_grid_with_empty_auto_tracks_does_not_floor_outer_track() {
            // Outer board: 3 equal 1fr cols, width=300px → fair share = 100px.
            // Nested grid in col0: two auto columns, items are height-only (0 text).
            // min-content = 0 + 0 = 0 → no floor pressure → track at 100.
            const string css =
                ".board  { display: grid; grid-template-columns: 1fr 1fr 1fr; width: 300px; }" +
                ".nested { display: grid; grid-template-columns: auto auto; }" +
                ".empty  { height: 10px; }" +
                ".other  { height: 10px; }";
            const string html =
                "<div class='board'>" +
                "<div class='nested'>" +
                "<div class='empty'></div>" +
                "<div class='empty'></div>" +
                "</div>" +
                "<div class='other'></div>" +
                "<div class='other'></div>" +
                "</div>";
            var (root, _, _) = Build(html, css, viewportWidth: 400, viewportHeight: 200);

            var nested = FirstWithClass(root, "nested");
            Assert.That(nested, Is.Not.Null);
            // No text / no fixed-width items → min-content = 0 → no floor.
            // Nested must stay at the proportional 100px, not blow up.
            Assert.That(nested.Width, Is.EqualTo(100).Within(2),
                $"empty auto tracks must not floor fr track, got {nested.Width:F1}");
        }

        // -----------------------------------------------------------------------
        // 9. Container's own min-width clamps the result from below.
        // -----------------------------------------------------------------------
        [Test]
        public void Container_own_min_width_clamps_the_min_content_result() {
            // Nested grid: single auto column, item "Hi" (16px), no frame.
            // But the grid itself has min-width:100px.
            // min-content would be 16px, clamped up to 100 by min-width.
            // Outer 1fr at 50px → floors at ≥ 100.
            const string css =
                ".board  { display: grid; grid-template-columns: 1fr; width: 50px; }" +
                ".nested { display: grid; grid-template-columns: auto; min-width: 100px; }";
            const string html =
                "<div class='board'>" +
                "<div class='nested'><div>Hi</div></div>" +
                "</div>";
            var (root, _, _) = Build(html, css, viewportWidth: 200, viewportHeight: 200);

            var nested = FirstWithClass(root, "nested");
            Assert.That(nested, Is.Not.Null);
            Assert.That(nested.Width, Is.GreaterThanOrEqualTo(100 - 1),
                $"min-width on the nested grid must clamp the min-content upward, got {nested.Width:F1}");
        }

        // -----------------------------------------------------------------------
        // 10. Container's own max-width clamps the result from above.
        // -----------------------------------------------------------------------
        [Test]
        public void Container_own_max_width_clamps_the_min_content_result() {
            // Nested grid: single auto column, item "ABCDEFGHIJ" (10×8=80px).
            // But max-width: 50px caps it.
            // Outer 1fr at 40px → floors at max-width cap = 50.
            const string css =
                ".board  { display: grid; grid-template-columns: 1fr; width: 40px; }" +
                ".nested { display: grid; grid-template-columns: auto; max-width: 50px; }";
            const string html =
                "<div class='board'>" +
                "<div class='nested'><div>ABCDEFGHIJ</div></div>" +
                "</div>";
            var (root, _, _) = Build(html, css, viewportWidth: 200, viewportHeight: 200);

            var nested = FirstWithClass(root, "nested");
            Assert.That(nested, Is.Not.Null);
            // The floor is capped at max-width (50) even though intrinsic was 80.
            Assert.That(nested.Width, Is.LessThanOrEqualTo(50 + 1),
                $"max-width on the nested grid must cap the min-content result, got {nested.Width:F1}");
        }

        // -----------------------------------------------------------------------
        // 11. Deep nesting: grid-in-grid-in-grid (3 levels).
        //    Innermost has a 120px fixed item; each level's min-content must
        //    propagate outward so the outermost fr track floors correctly.
        // -----------------------------------------------------------------------
        [Test]
        public void Deep_nesting_grid_in_grid_in_grid_propagates_min_content() {
            // Level 3 (innermost): 1 column, 1 item 120px wide, no frame.
            //   min-content = 120.
            // Level 2: 1fr column containing Level 3.
            //   min-content = 120 (fr floors at inner's min-content).
            // Level 1 (outer board): 1fr column, 60px wide.
            //   min-content = 120 → floors outer track at ≥ 120.
            const string css =
                ".lvl1 { display: grid; grid-template-columns: 1fr; width: 60px; }" +
                ".lvl2 { display: grid; grid-template-columns: 1fr; }" +
                ".lvl3 { display: grid; grid-template-columns: 1fr; }" +
                ".leaf { width: 120px; height: 10px; }";
            const string html =
                "<div class='lvl1'>" +
                "<div class='lvl2'>" +
                "<div class='lvl3'>" +
                "<div class='leaf'></div>" +
                "</div>" +
                "</div>" +
                "</div>";
            var (root, _, _) = Build(html, css, viewportWidth: 200, viewportHeight: 200);

            var lvl2 = FirstWithClass(root, "lvl2");
            Assert.That(lvl2, Is.Not.Null);
            // Outermost grid (lvl1) has its 1fr column floored at the lvl2
            // min-content which chains down to lvl3's leaf (120px).
            Assert.That(lvl2.Width, Is.GreaterThanOrEqualTo(120 - 1),
                $"deep-nested min-content must propagate up, got {lvl2.Width:F1}");
        }

        // -----------------------------------------------------------------------
        // 12. minmax(px, fr) track — the px minimum is the min-content floor,
        //     NOT the item's content min-content (Phase 1a already set it).
        // -----------------------------------------------------------------------
        [Test]
        public void Minmax_px_fr_track_uses_px_floor_not_content_min_content() {
            // .nested has grid-template-columns: minmax(150px, 1fr) auto.
            // First column min-content = max(150, item-content) = 150 (since
            // item text "Hi" = 16 < 150). Second column = 16 (auto, sized by item).
            // Nested min-content = 150 + 16 = 166.
            // Outer 1fr at 100px → floors at ≥ 166.
            const string css =
                ".board  { display: grid; grid-template-columns: 1fr; width: 100px; }" +
                ".nested { display: grid; grid-template-columns: minmax(150px, 1fr) auto; }";
            const string html =
                "<div class='board'>" +
                "<div class='nested'>" +
                "<div>Hi</div>" +
                "<div>Hi</div>" +
                "</div>" +
                "</div>";
            var (root, _, _) = Build(html, css, viewportWidth: 400, viewportHeight: 200);

            var nested = FirstWithClass(root, "nested");
            Assert.That(nested, Is.Not.Null);
            // min-content = 150 (minmax floor) + 16 (auto, "Hi") = 166
            Assert.That(nested.Width, Is.GreaterThanOrEqualTo(166 - 1),
                $"minmax(px,fr) must contribute px as fixed minimum, got {nested.Width:F1}");
        }

        // -----------------------------------------------------------------------
        // 13. K8-OPT: `min-width: 0` on the nested-grid ITEM opts out of the
        //     automatic minimum (s6.6) — the fr track may squash it to the
        //     fair share. The zero check was dead code ("0"/"0px" entered the
        //     resolve branch first, where 0px never beats the intrinsic min),
        //     invisible while nested grids contributed 0 anyway; with a real
        //     grid min-content the opt-out must actually zero the floor.
        // -----------------------------------------------------------------------
        [Test]
        public void Min_width_zero_on_nested_grid_opts_out_of_the_floor() {
            // Same structure as the floors-the-track case: a fixed 120px
            // track inside the nested grid makes its min-content (≥120)
            // exceed the 1fr fair share (200 - 120 art = 80)... but the
            // explicit `min-width: 0` says squash, so the fr share stands.
            const string css =
                ".board  { display: grid; grid-template-columns: 120px 1fr; width: 200px; }" +
                ".art    { height: 20px; }" +
                ".nested { display: grid; grid-template-columns: 120px; min-width: 0; }" +
                ".cell   { height: 20px; }";
            const string html =
                "<div class='board'>" +
                "<div class='art'></div>" +
                "<div class='nested'><div class='cell'></div></div>" +
                "</div>";
            var (root, _, _) = Build(html, css, viewportWidth: 300, viewportHeight: 200);

            var nested = FirstWithClass(root, "nested");
            Assert.That(nested, Is.Not.Null);
            // 1fr share = 200 - 120 = 80. Without the opt-out the nested
            // grid's 120px fixed track would floor the track at ≥120.
            Assert.That(nested.Width, Is.EqualTo(80).Within(1.0),
                $"min-width:0 must opt the item out of the s6.6 floor, got {nested.Width:F1}");
        }
    }
}
