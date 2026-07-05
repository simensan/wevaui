using NUnit.Framework;
using System.Linq;
using Weva.Events;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Events {
    // Headless tests for SpatialNavigator — the CSS Spatial Navigation
    // (WICG spatnav) box-tree focus picker introduced in W3 phase 1.
    //
    // All tests build real laid-out Box trees via LayoutTestHelpers.Build so
    // that SpatialNavigator operates on the same geometry the engine produces,
    // not hand-crafted stubs. CSS uses pixel-exact sizes so expected winners
    // are deterministic regardless of MonoFontMetrics.
    //
    // Convention: elements that should be focusable carry `tabindex="0"`.
    // Elements that should be invisible to the navigator carry no tabindex and
    // are not naturally-focusable tags (e.g. `<div>`).
    public class SpatialNavigatorTests {
        // ------------------------------------------------------------------ helpers

        static Box BoxFor(Box root, string id) {
            return AllBoxes(root).FirstOrDefault(b =>
                b.Element != null && b.Element.GetAttribute("id") == id);
        }

        // ------------------------------------------------------------------ 3×3 grid tests

        // 3×3 grid layout used by grid tests:
        //   [a][b][c]
        //   [d][e][f]
        //   [g][h][i]
        // Each cell is 60×40, gap 10px between cells.
        static (Box root, Box a, Box b, Box c, Box d, Box e, Box f, Box g, Box h, Box i)
            Grid3x3() {
            const string css = @"
                .grid { display: flex; flex-wrap: wrap; width: 210px; }
                .cell { width: 60px; height: 40px; margin: 5px; }
            ";
            const string html = @"
                <div class='grid'>
                    <button id='a' class='cell' tabindex='0'></button>
                    <button id='b' class='cell' tabindex='0'></button>
                    <button id='c' class='cell' tabindex='0'></button>
                    <button id='d' class='cell' tabindex='0'></button>
                    <button id='e' class='cell' tabindex='0'></button>
                    <button id='f' class='cell' tabindex='0'></button>
                    <button id='g' class='cell' tabindex='0'></button>
                    <button id='h' class='cell' tabindex='0'></button>
                    <button id='i' class='cell' tabindex='0'></button>
                </div>";
            var (root, _, _) = Build(html, css, viewportWidth: 400, viewportHeight: 400);
            return (
                root,
                BoxFor(root, "a"), BoxFor(root, "b"), BoxFor(root, "c"),
                BoxFor(root, "d"), BoxFor(root, "e"), BoxFor(root, "f"),
                BoxFor(root, "g"), BoxFor(root, "h"), BoxFor(root, "i")
            );
        }

        [Test]
        public void Grid3x3_right_from_center_picks_right_neighbor() {
            var (root, _, _, _, _, e, f, _, _, _) = Grid3x3();
            Assert.That(e, Is.Not.Null, "box e must exist");
            Assert.That(f, Is.Not.Null, "box f must exist");
            var next = SpatialNavigator.FindNext(root, e, SpatialDirection.Right);
            Assert.That(next, Is.SameAs(f), "Right from center [e] should pick [f]");
        }

        [Test]
        public void Grid3x3_left_from_center_picks_left_neighbor() {
            var (root, _, _, _, d, e, _, _, _, _) = Grid3x3();
            Assert.That(d, Is.Not.Null, "box d must exist");
            Assert.That(e, Is.Not.Null, "box e must exist");
            var next = SpatialNavigator.FindNext(root, e, SpatialDirection.Left);
            Assert.That(next, Is.SameAs(d), "Left from center [e] should pick [d]");
        }

        [Test]
        public void Grid3x3_down_from_top_center_picks_middle_center() {
            var (root, _, b, _, _, e, _, _, _, _) = Grid3x3();
            Assert.That(b, Is.Not.Null, "box b must exist");
            Assert.That(e, Is.Not.Null, "box e must exist");
            var next = SpatialNavigator.FindNext(root, b, SpatialDirection.Down);
            Assert.That(next, Is.SameAs(e), "Down from top-center [b] should pick [e]");
        }

        [Test]
        public void Grid3x3_up_from_bottom_center_picks_middle_center() {
            var (root, _, _, _, _, e, _, _, h, _) = Grid3x3();
            Assert.That(h, Is.Not.Null, "box h must exist");
            Assert.That(e, Is.Not.Null, "box e must exist");
            var next = SpatialNavigator.FindNext(root, h, SpatialDirection.Up);
            Assert.That(next, Is.SameAs(e), "Up from bottom-center [h] should pick [e]");
        }

        // ------------------------------------------------------------------ vertical list

        [Test]
        public void Vertical_list_down_picks_next_element() {
            // Use display:block on buttons so height applies and they stack
            // vertically with real pixel geometry. Inline buttons don't get
            // an explicit height in CSS.
            const string css = ".item { display: block; width: 100px; height: 30px; margin-bottom: 10px; }";
            const string html = @"
                <div>
                    <button id='top'    class='item' tabindex='0'></button>
                    <button id='middle' class='item' tabindex='0'></button>
                    <button id='bottom' class='item' tabindex='0'></button>
                </div>";
            var (root, _, _) = Build(html, css);
            var top    = BoxFor(root, "top");
            var middle = BoxFor(root, "middle");
            Assert.That(top,    Is.Not.Null);
            Assert.That(middle, Is.Not.Null);
            var next = SpatialNavigator.FindNext(root, top, SpatialDirection.Down);
            Assert.That(next, Is.SameAs(middle));
        }

        [Test]
        public void Vertical_list_up_picks_prev_element() {
            const string css = ".item { display: block; width: 100px; height: 30px; margin-bottom: 10px; }";
            const string html = @"
                <div>
                    <button id='top'    class='item' tabindex='0'></button>
                    <button id='middle' class='item' tabindex='0'></button>
                    <button id='bottom' class='item' tabindex='0'></button>
                </div>";
            var (root, _, _) = Build(html, css);
            var top    = BoxFor(root, "top");
            var middle = BoxFor(root, "middle");
            Assert.That(top,    Is.Not.Null);
            Assert.That(middle, Is.Not.Null);
            var next = SpatialNavigator.FindNext(root, middle, SpatialDirection.Up);
            Assert.That(next, Is.SameAs(top));
        }

        // ------------------------------------------------------------------ edge / no-candidate

        [Test]
        public void No_candidate_at_grid_right_edge_returns_null() {
            var (root, _, _, c, _, _, _, _, _, _) = Grid3x3();
            Assert.That(c, Is.Not.Null, "box c must exist");
            // [c] is the top-right corner — nothing to the right.
            var next = SpatialNavigator.FindNext(root, c, SpatialDirection.Right);
            Assert.That(next, Is.Null, "Right from right-edge corner should return null");
        }

        [Test]
        public void No_candidate_at_grid_top_edge_returns_null() {
            var (root, _, b, _, _, _, _, _, _, _) = Grid3x3();
            Assert.That(b, Is.Not.Null, "box b must exist");
            // [b] is the top-center — nothing above it.
            var next = SpatialNavigator.FindNext(root, b, SpatialDirection.Up);
            Assert.That(next, Is.Null, "Up from top-center should return null");
        }

        // ------------------------------------------------------------------ alignment vs distance

        [Test]
        public void Staggered_rows_orthogonal_penalty_prefers_aligned() {
            // Layout:
            //   [anchor] at (0, 0, 80, 40)
            //   [aligned] at (0, 80, 80, 40)   — directly below, 40 px gap
            //   [near_off] at (200, 50, 80, 40) — closer vertically but far right
            //
            // Expected: Down picks [aligned] because PerpPenalty makes [near_off]
            // score worse despite its smaller vertical distance.
            const string css = @"
                .a { position: absolute; left: 0;   top: 0;  width: 80px; height: 40px; }
                .b { position: absolute; left: 0;   top: 80px; width: 80px; height: 40px; }
                .c { position: absolute; left: 200px; top: 50px; width: 80px; height: 40px; }
                .wrap { position: relative; width: 400px; height: 200px; }
            ";
            const string html = @"
                <div class='wrap'>
                    <button id='anchor'   class='a' tabindex='0'></button>
                    <button id='aligned'  class='b' tabindex='0'></button>
                    <button id='near_off' class='c' tabindex='0'></button>
                </div>";
            var (root, _, _) = Build(html, css, viewportWidth: 500, viewportHeight: 300);
            var anchor   = BoxFor(root, "anchor");
            var aligned  = BoxFor(root, "aligned");
            var near_off = BoxFor(root, "near_off");
            Assert.That(anchor,   Is.Not.Null);
            Assert.That(aligned,  Is.Not.Null);
            Assert.That(near_off, Is.Not.Null);

            var next = SpatialNavigator.FindNext(root, anchor, SpatialDirection.Down);
            Assert.That(next, Is.SameAs(aligned),
                "Directly-below aligned element should win over closer but off-axis element");
        }

        [Test]
        public void Overlap_bonus_row_peer_beats_closer_off_row() {
            // Layout (left/right movement):
            //   [left]  at (0,   0,   60, 40)  — current focus
            //   [peer]  at (80,  10,  60, 40)  — right, row-overlapping (y: 10..50 vs 0..40)
            //   [close] at (70, 100,  60, 40)  — right but off-row; closer left edge but
            //                                    no overlap with current row
            //
            // Expected: Right picks [peer] because the overlap bonus outweighs [close]'s
            // slightly smaller parallel distance.
            const string css = @"
                .a { position: absolute; left: 0;  top: 0;   width: 60px; height: 40px; }
                .b { position: absolute; left: 80px; top: 10px; width: 60px; height: 40px; }
                .c { position: absolute; left: 70px; top: 100px; width: 60px; height: 40px; }
                .wrap { position: relative; width: 300px; height: 200px; }
            ";
            const string html = @"
                <div class='wrap'>
                    <button id='left'  class='a' tabindex='0'></button>
                    <button id='peer'  class='b' tabindex='0'></button>
                    <button id='close' class='c' tabindex='0'></button>
                </div>";
            var (root, _, _) = Build(html, css, viewportWidth: 400, viewportHeight: 300);
            var left  = BoxFor(root, "left");
            var peer  = BoxFor(root, "peer");
            var close = BoxFor(root, "close");
            Assert.That(left,  Is.Not.Null);
            Assert.That(peer,  Is.Not.Null);
            Assert.That(close, Is.Not.Null);

            var next = SpatialNavigator.FindNext(root, left, SpatialDirection.Right);
            Assert.That(next, Is.SameAs(peer),
                "Row-overlapping peer should win over closer but off-row element");
        }

        // ------------------------------------------------------------------ filtering

        [Test]
        public void Skips_non_focusable_elements_between_focusable_ones() {
            // A <div> without tabindex is not focusable; navigator should leap
            // over it and land on the next button. Use display:block so height
            // applies to the button elements.
            const string css = ".item { display: block; width: 100px; height: 30px; margin-bottom: 10px; }";
            const string html = @"
                <div>
                    <button id='a' class='item' tabindex='0'></button>
                    <div    id='b' class='item'></div>
                    <button id='c' class='item' tabindex='0'></button>
                </div>";
            var (root, _, _) = Build(html, css);
            var a = BoxFor(root, "a");
            var c = BoxFor(root, "c");
            Assert.That(a, Is.Not.Null);
            Assert.That(c, Is.Not.Null);

            var next = SpatialNavigator.FindNext(root, a, SpatialDirection.Down);
            Assert.That(next, Is.SameAs(c),
                "Non-focusable div between a and c must be skipped");
        }

        [Test]
        public void Skips_disabled_elements() {
            const string css = ".item { display: block; width: 100px; height: 30px; margin-bottom: 10px; }";
            const string html = @"
                <div>
                    <button id='a' class='item' tabindex='0'></button>
                    <button id='b' class='item' tabindex='0' disabled></button>
                    <button id='c' class='item' tabindex='0'></button>
                </div>";
            var (root, _, _) = Build(html, css);
            var a = BoxFor(root, "a");
            var c = BoxFor(root, "c");
            Assert.That(a, Is.Not.Null);
            Assert.That(c, Is.Not.Null);

            var next = SpatialNavigator.FindNext(root, a, SpatialDirection.Down);
            Assert.That(next, Is.SameAs(c),
                "Disabled element [b] must be skipped; navigator should land on [c]");
        }

        // ------------------------------------------------------------------ tiebreak

        [Test]
        public void Document_order_tiebreak_picks_earlier_element() {
            // Two buttons equidistant to the right of the anchor.
            // Both at x=100 with the same y-center → same score.
            // [first] appears earlier in the document; it should win.
            //
            // We achieve a true tie by placing the candidates at identical
            // (left, top, width, height). The tiebreak is purely docorder.
            const string css = @"
                .anchor { position: absolute; left: 0; top: 50px; width: 80px; height: 40px; }
                .peer   { position: absolute; left: 100px; top: 50px; width: 80px; height: 40px; }
                .wrap { position: relative; width: 300px; height: 200px; }
            ";
            const string html = @"
                <div class='wrap'>
                    <button id='anchor' class='anchor' tabindex='0'></button>
                    <button id='first'  class='peer'   tabindex='0'></button>
                    <button id='second' class='peer'   tabindex='0'></button>
                </div>";
            var (root, _, _) = Build(html, css, viewportWidth: 400, viewportHeight: 300);
            var anchor = BoxFor(root, "anchor");
            var first  = BoxFor(root, "first");
            Assert.That(anchor, Is.Not.Null);
            Assert.That(first,  Is.Not.Null);

            var next = SpatialNavigator.FindNext(root, anchor, SpatialDirection.Right);
            Assert.That(next, Is.SameAs(first),
                "When two candidates score identically, document order picks the earlier one");
        }

        // ------------------------------------------------------------------ null-guard

        [Test]
        public void Current_null_returns_null() {
            const string html = "<button id='a' tabindex='0'></button>";
            var (root, _, _) = Build(html);
            var result = SpatialNavigator.FindNext(root, null, SpatialDirection.Down);
            Assert.That(result, Is.Null, "FindNext with null current must return null");
        }

        [Test]
        public void Null_root_throws_argument_null_exception() {
            const string html = "<button id='a' tabindex='0'></button>";
            var (root, _, _) = Build(html);
            var box = BoxFor(root, "a");
            Assert.Throws<System.ArgumentNullException>(
                () => SpatialNavigator.FindNext(null, box, SpatialDirection.Down));
        }

        // ------------------------------------------------------------------ L-shaped layout

        [Test]
        public void L_shaped_layout_down_from_top_leg_reaches_bottom_leg() {
            // L-shape:
            //   [a][b]       ← top horizontal leg (two buttons side by side)
            //   [c]          ← bottom vertical leg (one button below [a])
            //
            // Pressing Down from [b] should reach [c] because even though [c]
            // is not directly below [b] it is the only candidate below the
            // top leg.
            const string css = @"
                .a { position: absolute; left: 0;   top: 0;  width: 60px; height: 40px; }
                .b { position: absolute; left: 70px; top: 0; width: 60px; height: 40px; }
                .c { position: absolute; left: 0;   top: 50px; width: 60px; height: 40px; }
                .wrap { position: relative; width: 300px; height: 200px; }
            ";
            const string html = @"
                <div class='wrap'>
                    <button id='a' class='a' tabindex='0'></button>
                    <button id='b' class='b' tabindex='0'></button>
                    <button id='c' class='c' tabindex='0'></button>
                </div>";
            var (root, _, _) = Build(html, css, viewportWidth: 400, viewportHeight: 300);
            var b = BoxFor(root, "b");
            var c = BoxFor(root, "c");
            Assert.That(b, Is.Not.Null);
            Assert.That(c, Is.Not.Null);

            var next = SpatialNavigator.FindNext(root, b, SpatialDirection.Down);
            Assert.That(next, Is.SameAs(c),
                "Down from [b] in L-shape should reach [c], the only candidate below");
        }

        [Test]
        public void L_shaped_layout_right_from_bottom_leg_finds_upper_right_element() {
            // [c] is the sole element in the bottom leg. Pressing Right from [c]
            // finds [b] — the upper-right element — because [b]'s left edge (70px)
            // clears [c]'s right edge (60px), so [b] lies in the right half-plane
            // of [c] even though it's above [c]. This matches WICG spatnav
            // half-plane semantics: the half-plane is defined by the parallel
            // (horizontal) axis only, not by vertical proximity.
            const string css = @"
                .a { position: absolute; left: 0;   top: 0;  width: 60px; height: 40px; }
                .b { position: absolute; left: 70px; top: 0; width: 60px; height: 40px; }
                .c { position: absolute; left: 0;   top: 50px; width: 60px; height: 40px; }
                .wrap { position: relative; width: 300px; height: 200px; }
            ";
            const string html = @"
                <div class='wrap'>
                    <button id='a' class='a' tabindex='0'></button>
                    <button id='b' class='b' tabindex='0'></button>
                    <button id='c' class='c' tabindex='0'></button>
                </div>";
            var (root, _, _) = Build(html, css, viewportWidth: 400, viewportHeight: 300);
            var b = BoxFor(root, "b");
            var c = BoxFor(root, "c");
            Assert.That(b, Is.Not.Null);
            Assert.That(c, Is.Not.Null);

            var next = SpatialNavigator.FindNext(root, c, SpatialDirection.Right);
            Assert.That(next, Is.SameAs(b),
                "Right from bottom-leg [c] finds [b] in the right half-plane (above-right)");
        }

        [Test]
        public void No_candidate_when_truly_nothing_in_half_plane() {
            // All three buttons are in a vertical column — pressing Right from
            // any of them returns null because no button has a left edge that
            // clears any button's right edge.
            const string css = @"
                .a { position: absolute; left: 0; top: 0;   width: 60px; height: 40px; }
                .b { position: absolute; left: 0; top: 50px; width: 60px; height: 40px; }
                .c { position: absolute; left: 0; top: 100px; width: 60px; height: 40px; }
                .wrap { position: relative; width: 200px; height: 200px; }
            ";
            const string html = @"
                <div class='wrap'>
                    <button id='a' class='a' tabindex='0'></button>
                    <button id='b' class='b' tabindex='0'></button>
                    <button id='c' class='c' tabindex='0'></button>
                </div>";
            var (root, _, _) = Build(html, css, viewportWidth: 300, viewportHeight: 300);
            var a = BoxFor(root, "a");
            Assert.That(a, Is.Not.Null);

            var next = SpatialNavigator.FindNext(root, a, SpatialDirection.Right);
            Assert.That(next, Is.Null,
                "Pure vertical column — no candidate to the right of any button");
        }
    }
}
