// CSS Box Model L3 §3.1 / CSS 2.1 §8.3.1 — Negative margin behaviour matrix.
// These tests cover block flow, inline-block, flex, grid, and abs-pos layout
// modes. Where the engine already conforms, we pin the correct behaviour.
// Where the engine diverges from spec, the spec-correct test is [Ignore]'d
// with a note, and a companion regression-anchor test pins the current
// (possibly wrong) behaviour so we don't silently drift further.
//
// Spec rules summarised:
//   Block flow  : negative margins shift blocks; adjacent negatives collapse
//                 per max(positives) + min(negatives) rule (§8.3.1).
//   Flex items  : negative margins are allowed on flex items; they shift the
//                 item; they do NOT collapse with sibling margins (CSS
//                 Flexbox L1 §6 — flex margins never collapse).
//   Grid items  : negative margins displace the item within (or outside) its
//                 cell; fixed track sizes are unaffected (CSS Grid L1).
//   Inline-block: negative margins shift the atom within the line box.
//   Abs-pos     : margins on absolutely-positioned elements offset the box
//                 position (CSS 2.1 §10.6.4).

using System;
using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Layout.Flex;
using Weva.Layout.Grid;
using static Weva.Tests.Layout.LayoutTestHelpers;
using static Weva.Tests.Layout.Flex.FlexTestHelpers;
using static Weva.Tests.Layout.Grid.GridTestHelpers;

namespace Weva.Tests.Layout {
    public class NegativeMarginTests {

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        static BlockBox FirstById(Box root, string id) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null &&
                    bb.Element.GetAttribute("id") == id) return bb;
            }
            return null;
        }

        static BlockBox FirstByClass(Box root, string cls) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null) {
                    var raw = bb.Element.GetAttribute("class") ?? "";
                    foreach (var tok in raw.Split(' ')) {
                        if (tok == cls) return bb;
                    }
                }
            }
            return null;
        }

        static BlockBox FirstInlineBlock(Box root) {
            foreach (var b in AllBoxes(root))
                if (b is BlockBox bb && bb.IsInlineBlock) return bb;
            return null;
        }

        // -----------------------------------------------------------------------
        // §1 Block flow — negative top margin shifts block up
        // -----------------------------------------------------------------------

        [Test]
        public void Block_negative_margin_top_shifts_block_up() {
            // A single block with margin-top:-20px should start 20px above its
            // natural position (Y=0 within a fresh BFC).  Because the body itself
            // collapses its top margin with the child, the absolute Y of the block
            // will be -20.
            var (root, _, _) = Build(
                "<div id=\"a\" style=\"height:30px;margin-top:-20px\"></div>",
                null, 800);
            var a = FirstById(root, "a");
            Assert.That(a, Is.Not.Null);
            var (_, absY) = AbsoluteOrigin(a);
            // Negative margin pulls the block up — absolute Y must be < 0.
            // `.Within` can't chain off `Is.LessThan`; use a strict-less assertion
            // (the followup Is.EqualTo(-20) below pins the exact value).
            Assert.That(absY, Is.LessThan(0.001));
            // Specifically: margin-top = -20 collapsed through empty body/html => -20.
            Assert.That(absY, Is.EqualTo(-20).Within(0.001));
        }

        [Test]
        public void Block_negative_margin_top_layout_produces_no_nan() {
            // Sanity: layout over a negative-margin block must not produce NaN/Inf
            // in any coordinate.
            var (root, _, _) = Build(
                "<div id=\"a\" style=\"height:30px;margin-top:-20px\"></div>",
                null, 800);
            foreach (var b in AllBoxes(root)) {
                Assert.That(double.IsNaN(b.X), Is.False, "NaN in X");
                Assert.That(double.IsNaN(b.Y), Is.False, "NaN in Y");
                Assert.That(double.IsNaN(b.Width), Is.False, "NaN in Width");
                Assert.That(double.IsNaN(b.Height), Is.False, "NaN in Height");
                Assert.That(double.IsInfinity(b.X), Is.False, "Inf in X");
                Assert.That(double.IsInfinity(b.Y), Is.False, "Inf in Y");
            }
        }

        // -----------------------------------------------------------------------
        // §2 Block flow — negative bottom margin pulls subsequent sibling up
        // -----------------------------------------------------------------------

        [Test]
        public void Block_negative_margin_bottom_pulls_next_sibling_up() {
            // First block has margin-bottom:-10px; height:30px.  Natural position
            // of second block without any margin would be Y=30.  The -10px bottom
            // margin collapses with second block's implicit 0 top margin giving a
            // gap of 0 + (-10) = -10, so second block should land at Y=20.
            var (root, _, _) = Build(
                "<div id=\"a\" style=\"height:30px;margin-bottom:-10px\"></div>" +
                "<div id=\"b\" style=\"height:20px\"></div>",
                null, 800);
            var a = FirstById(root, "a");
            var b = FirstById(root, "b");
            Assert.That(a, Is.Not.Null);
            Assert.That(b, Is.Not.Null);
            var (_, absAY) = AbsoluteOrigin(a);
            var (_, absBY) = AbsoluteOrigin(b);
            // b must be ABOVE a's bottom edge (overlap due to negative margin).
            Assert.That(absBY, Is.LessThan(absAY + a.Height + 0.001));
            // Specifically b.Y = a.Y + a.Height + (-10) = 20 from page top.
            Assert.That(absBY, Is.EqualTo(absAY + a.Height - 10).Within(0.001));
        }

        // -----------------------------------------------------------------------
        // §3 Margin collapse — one negative + one positive: max(pos) + min(neg)
        // -----------------------------------------------------------------------

        [Test]
        public void Block_mixed_sign_sibling_collapse_max_positive_plus_min_negative() {
            // CSS 2.1 §8.3.1: when adjacent collapsing margins include negatives,
            // result = max(all positives) + min(all negatives).
            // a: margin-bottom=20, b: margin-top=-5 => 20 + (-5) = 15.
            // a.height=30, so b lands at Y = 30 + 15 = 45.
            var (root, _, _) = Build(
                "<div id=\"a\" style=\"height:30px;margin-bottom:20px\"></div>" +
                "<div id=\"b\" style=\"height:30px;margin-top:-5px\"></div>",
                null, 800);
            var b = FirstById(root, "b");
            Assert.That(b, Is.Not.Null);
            Assert.That(b.Y, Is.EqualTo(45).Within(0.001));
        }

        [Test]
        public void Block_all_negative_siblings_collapse_to_most_negative() {
            // Both margins negative: max positive = 0, min negative = -20.
            // Result = 0 + (-20) = -20 — the most-negative wins.
            // a.height=30 → b.Y = 30 + (-20) = 10.
            var (root, _, _) = Build(
                "<div id=\"a\" style=\"height:30px;margin-bottom:-10px\"></div>" +
                "<div id=\"b\" style=\"height:30px;margin-top:-20px\"></div>",
                null, 800);
            var b = FirstById(root, "b");
            Assert.That(b, Is.Not.Null);
            // pre-existing coverage in MarginCollapsingTests.Two_negative_margins_collapse_to_min.
            Assert.That(b.Y, Is.EqualTo(10).Within(0.001));
        }

        // -----------------------------------------------------------------------
        // §4 Nested block with negative margin — container height clamps
        // -----------------------------------------------------------------------

        [Test]
        public void Nested_block_negative_top_margin_collapses_through_parent_spec() {
            // SPEC RECALIBRATION (MARGINCOLAPSE-RELATIVE fix): CSS 2.1 §8.3.1 does
            // NOT list an explicit `height` as a condition that blocks parent-child
            // TOP-margin collapsing. Only border/padding at the top or a BFC-
            // establishing container prevent it. Chrome confirms: a parent with
            // height:50px and no border/padding lets the child's margin-top:-10px
            // collapse THROUGH — parent.MarginTop becomes -10 (collapsed from 0 and
            // -10), and the child sits at Y=0 within the parent (at the inner top
            // edge, no displacement).
            // Previously this asserted inner.Y = -10, which was wrong: it reflected
            // the erroneous `height != "auto"` guard removed by the MARGINCOLAPSE-
            // RELATIVE fix.
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"height:50px\">" +
                "<div id=\"inner\" style=\"height:20px;margin-top:-10px\"></div>" +
                "</div>",
                null, 800);
            var outer = FirstById(root, "outer");
            var inner = FirstById(root, "inner");
            Assert.That(inner, Is.Not.Null);
            // CSS 2.1 §8.3.1: no border/padding/BFC → top is open → child's -10px
            // collapses onto parent's margin. Child sits at Y=0 (inner top edge).
            Assert.That(outer.MarginTop, Is.EqualTo(-10).Within(0.001));
            Assert.That(inner.Y, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Nested_block_negative_top_margin_does_not_grow_parent_height() {
            // Parent's explicit height must stay fixed at 50px. The child's -10px
            // top-margin collapses through (no border/padding/BFC blocks it), so
            // the parent's MarginTop absorbs -10 and the child sits at Y=0 inside.
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"height:50px\">" +
                "<div id=\"inner\" style=\"height:20px;margin-top:-10px\"></div>" +
                "</div>",
                null, 800);
            var outer = FirstById(root, "outer");
            var inner = FirstById(root, "inner");
            Assert.That(outer, Is.Not.Null);
            Assert.That(inner, Is.Not.Null);
            // Parent explicit height is preserved — negative child margin collapses
            // through (not inside), so parent height stays 50.
            Assert.That(outer.Height, Is.EqualTo(50).Within(0.001));
            // Child collapses through: sits at Y=0 (flush with inner top edge).
            Assert.That(inner.Y, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Nested_block_negative_top_margin_no_nan_in_parent() {
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"height:50px\">" +
                "<div id=\"inner\" style=\"height:20px;margin-top:-10px\"></div>" +
                "</div>",
                null, 800);
            foreach (var b in AllBoxes(root)) {
                Assert.That(double.IsNaN(b.Y), Is.False, "NaN in Y");
                Assert.That(double.IsNaN(b.Height), Is.False, "NaN in Height");
            }
        }

        // -----------------------------------------------------------------------
        // §5 Inline-block — negative left margin shifts within line box
        // -----------------------------------------------------------------------

        [Test]
        public void Inline_block_negative_margin_left_shifts_atom_horizontally() {
            // An inline-block with margin-left:-5px should have its X reduced by
            // 5px compared to a baseline atom with no margin.
            var (root, _, _) = Build(
                "<p>" +
                "<span id=\"base\" style=\"display:inline-block;width:30px;height:20px;margin-left:0\"></span>" +
                "<span id=\"neg\" style=\"display:inline-block;width:30px;height:20px;margin-left:-5px\"></span>" +
                "</p>",
                null, 800);
            // Find the two inline-block atoms on the line.
            BlockBox base_ = null, neg = null;
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.IsInlineBlock && bb.Element != null) {
                    if (bb.Element.GetAttribute("id") == "base") base_ = bb;
                    if (bb.Element.GetAttribute("id") == "neg") neg = bb;
                }
            }
            Assert.That(base_, Is.Not.Null, "base inline-block not found");
            Assert.That(neg, Is.Not.Null, "neg inline-block not found");
            // neg follows base on the same line: expected X = base.X + base.Width - 5.
            double expectedNegX = base_.X + base_.Width - 5;
            Assert.That(neg.X, Is.EqualTo(expectedNegX).Within(0.001));
        }

        [Test]
        public void Inline_block_negative_margin_right_reduces_following_offset() {
            // margin-right:-5px on the first atom should bring the second atom
            // 5px closer to the first (effectively shrinking the gap).
            var (root, _, _) = Build(
                "<p>" +
                "<span id=\"a\" style=\"display:inline-block;width:30px;height:20px;margin-right:-5px\"></span>" +
                "<span id=\"b\" style=\"display:inline-block;width:30px;height:20px\"></span>" +
                "</p>",
                null, 800);
            BlockBox a = null, b = null;
            foreach (var bx in AllBoxes(root)) {
                if (bx is BlockBox bb && bb.IsInlineBlock && bb.Element != null) {
                    if (bb.Element.GetAttribute("id") == "a") a = bb;
                    if (bb.Element.GetAttribute("id") == "b") b = bb;
                }
            }
            Assert.That(a, Is.Not.Null);
            Assert.That(b, Is.Not.Null);
            // b.X should be 5px less than (a.X + a.Width).
            Assert.That(b.X, Is.EqualTo(a.X + a.Width - 5).Within(0.001));
        }

        // -----------------------------------------------------------------------
        // §6 Flex items — negative margin shifts item, NO collapse with siblings
        // -----------------------------------------------------------------------

        [Test]
        public void Flex_item_negative_margin_left_shifts_item_in_row() {
            // Row flex: negative margin-left on the second item shifts it leftward
            // toward (and potentially overlapping) the first.
            const string css = @"
                .flex { display: flex; width: 300px; }
                .a    { width: 80px; height: 40px; }
                .b    { width: 80px; height: 40px; margin-left: -20px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\">" +
                "<div class=\"a\"></div>" +
                "<div class=\"b\"></div>" +
                "</div>",
                css, 800);
            var flex = FindFlex(root, "div");
            Assert.That(flex, Is.Not.Null);
            var itemA = ChildAt(flex, 0);
            var itemB = ChildAt(flex, 1);
            Assert.That(itemA, Is.Not.Null);
            Assert.That(itemB, Is.Not.Null);
            // Without negative margin, itemB.X = 80.  With -20, X = 60.
            Assert.That(itemA.X, Is.EqualTo(0).Within(0.001));
            Assert.That(itemB.X, Is.EqualTo(60).Within(0.001));
        }

        [Test]
        public void Flex_item_negative_margins_do_not_collapse_with_siblings() {
            // Flex margins never collapse (CSS Flexbox L1 §6). Item A has
            // margin-right:-20; item B has margin-left:-20. In block flow these
            // would NOT collapse (margins only collapse in block flow, not flex),
            // but the combined shift from both should double-count (-40 total gap
            // reduction), not collapse to max(-20,-20)=-20.
            const string css = @"
                .flex { display: flex; width: 400px; }
                .a    { width: 100px; height: 40px; margin-right: -20px; }
                .b    { width: 100px; height: 40px; margin-left: -20px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\">" +
                "<div class=\"a\"></div>" +
                "<div class=\"b\"></div>" +
                "</div>",
                css, 800);
            var flex = FindFlex(root, "div");
            Assert.That(flex, Is.Not.Null);
            var itemA = ChildAt(flex, 0);
            var itemB = ChildAt(flex, 1);
            Assert.That(itemA, Is.Not.Null);
            Assert.That(itemB, Is.Not.Null);
            // itemA starts at X=0.  After itemA's right edge (100), minus
            // itemA's margin-right (20) and itemB's margin-left (20) = X=60 for B.
            Assert.That(itemA.X, Is.EqualTo(0).Within(0.001));
            Assert.That(itemB.X, Is.EqualTo(60).Within(0.001));
        }

        [Test]
        public void Flex_column_negative_margin_top_shifts_item_up() {
            // Column flex: negative margin-top on the second item shifts it toward
            // (and possibly overlapping) the first.
            const string css = @"
                .flex { display: flex; flex-direction: column; width: 200px; }
                .a    { height: 50px; }
                .b    { height: 50px; margin-top: -15px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\">" +
                "<div class=\"a\"></div>" +
                "<div class=\"b\"></div>" +
                "</div>",
                css, 800);
            var flex = FindFlex(root, "div");
            Assert.That(flex, Is.Not.Null);
            var itemA = ChildAt(flex, 0);
            var itemB = ChildAt(flex, 1);
            Assert.That(itemA, Is.Not.Null);
            Assert.That(itemB, Is.Not.Null);
            // Without margin, itemB.Y = 50.  With -15, Y = 35.
            Assert.That(itemB.Y, Is.EqualTo(35).Within(0.001));
        }

        [Test]
        public void Flex_item_cross_axis_negative_margin_shifts_item() {
            // CSS Flexbox: cross-axis negative margin on a flex item shifts the
            // item's position within the cross axis.  In a row flex with
            // align-items:flex-start, margin-top:-10 should shift the item up
            // (Y = -10).  Flex cross-axis margins do NOT collapse.
            const string css = @"
                .flex { display: flex; width: 300px; align-items: flex-start; }
                .b    { width: 80px; height: 40px; margin-top: -10px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\">" +
                "<div class=\"b\"></div>" +
                "</div>",
                css, 800);
            var flex = FindFlex(root, "div");
            Assert.That(flex, Is.Not.Null);
            var itemB = ChildAt(flex, 0);
            Assert.That(itemB, Is.Not.Null);
            // Item Y shifts up by the negative margin amount.
            Assert.That(itemB.Y, Is.EqualTo(-10).Within(0.001));
        }

        [Test]
        public void Flex_negative_margin_produces_no_nan() {
            const string css = @"
                .flex { display: flex; width: 300px; }
                .a    { width: 80px; height: 40px; margin-left: -50px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"flex\"><div class=\"a\"></div></div>",
                css, 800);
            foreach (var b in AllBoxes(root)) {
                Assert.That(double.IsNaN(b.X), Is.False, "NaN in X");
                Assert.That(double.IsNaN(b.Y), Is.False, "NaN in Y");
            }
        }

        // -----------------------------------------------------------------------
        // §7 Grid items — negative margin displaces item; track sizing unchanged
        // -----------------------------------------------------------------------

        [Test]
        public void Grid_item_negative_margin_top_shifts_item_above_cell_spec() {
            // SPEC (CSS Grid L1 §11.3): a grid item's negative margin-top displaces
            // the item within (or above) its cell without shrinking the fixed track.
            // margin-top:-15px => item.Y = -15 relative to cell origin.
            const string css = @"
                .grid { display: grid; grid-template-columns: 200px; grid-template-rows: 60px; width: 200px; }
                .item { width: 80px; height: 30px; margin-top: -15px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, 800);
            var grid = FindGridByClass(root, "grid");
            Assert.That(grid, Is.Not.Null);
            var item = ChildAt(grid, 0);
            Assert.That(item, Is.Not.Null);
            Assert.That(item.Y, Is.EqualTo(-15).Within(0.001));
        }

        [Test]
        public void Grid_item_negative_margin_top_does_not_shrink_track_height() {
            // Track height is fixed at 60px — the negative item margin must NOT
            // shrink the grid track allocation. This passes in both the current
            // engine and the spec.
            const string css = @"
                .grid { display: grid; grid-template-columns: 200px; grid-template-rows: 60px; width: 200px; }
                .item { width: 80px; height: 30px; margin-top: -15px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, 800);
            var grid = FindGridByClass(root, "grid");
            Assert.That(grid, Is.Not.Null);
            // Grid height should be 60px (the fixed track) regardless of margin.
            Assert.That(grid.Height, Is.EqualTo(60).Within(0.001));
        }

        [Test]
        public void Grid_item_negative_margin_left_shifts_item_outside_cell_spec() {
            // SPEC (CSS Grid L1 §11.4): margin-left:-20px on a grid item displaces
            // the item left of the cell origin without changing the column track width.
            const string css = @"
                .grid { display: grid; grid-template-columns: 200px; grid-template-rows: 60px; width: 200px; }
                .item { width: 80px; height: 30px; margin-left: -20px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, 800);
            var grid = FindGridByClass(root, "grid");
            Assert.That(grid, Is.Not.Null);
            var item = ChildAt(grid, 0);
            Assert.That(item, Is.Not.Null);
            Assert.That(item.X, Is.EqualTo(-20).Within(0.001));
        }

        [Test]
        public void Grid_negative_margin_produces_no_nan() {
            const string css = @"
                .grid { display: grid; grid-template-columns: 100px; grid-template-rows: 50px; width: 100px; }
                .item { margin-top: -30px; margin-left: -10px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"grid\"><div class=\"item\"></div></div>",
                css, 800);
            foreach (var b in AllBoxes(root)) {
                Assert.That(double.IsNaN(b.X), Is.False, "NaN in X");
                Assert.That(double.IsNaN(b.Y), Is.False, "NaN in Y");
                Assert.That(double.IsInfinity(b.Width), Is.False, "Inf in Width");
            }
        }

        // -----------------------------------------------------------------------
        // §8 Absolute positioning — negative margin offsets box position
        //    CSS 2.1 §10.6.4
        // -----------------------------------------------------------------------

        [Test]
        public void Absolute_negative_margin_top_offsets_box_upward() {
            // position:absolute + top:50px + margin-top:-10px => final Y = 40.
            const string css = @"
                .cb  { position: relative; width: 400px; height: 400px; }
                .abs { position: absolute; top: 50px; left: 0; width: 80px; height: 40px; margin-top: -10px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"cb\"><div class=\"abs\"></div></div>",
                css, 800);
            var abs = FirstByClass(root, "abs");
            Assert.That(abs, Is.Not.Null);
            // Y within containing block = top + margin-top = 50 + (-10) = 40.
            Assert.That(abs.Y, Is.EqualTo(40).Within(0.001));
        }

        [Test]
        public void Absolute_negative_margin_left_offsets_box_leftward() {
            // position:absolute + left:60px + margin-left:-15px => final X = 45.
            const string css = @"
                .cb  { position: relative; width: 400px; height: 400px; }
                .abs { position: absolute; top: 0; left: 60px; width: 80px; height: 40px; margin-left: -15px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"cb\"><div class=\"abs\"></div></div>",
                css, 800);
            var abs = FirstByClass(root, "abs");
            Assert.That(abs, Is.Not.Null);
            Assert.That(abs.X, Is.EqualTo(45).Within(0.001));
        }

        [Test]
        public void Absolute_negative_margin_top_no_nan() {
            // Regression guard: abs-pos + large negative margin must not produce NaN.
            const string css = @"
                .cb  { position: relative; width: 200px; height: 200px; }
                .abs { position: absolute; top: 0; left: 0; width: 50px; height: 50px; margin-top: -999px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"cb\"><div class=\"abs\"></div></div>",
                css, 800);
            foreach (var b in AllBoxes(root)) {
                Assert.That(double.IsNaN(b.Y), Is.False, "NaN in Y");
                Assert.That(double.IsInfinity(b.Y), Is.False, "Inf in Y");
            }
        }

        // -----------------------------------------------------------------------
        // §9 Three-way max+min collapse (CSS 2.1 §8.3.1 aggregate rule)
        // -----------------------------------------------------------------------

        [Test]
        public void Three_way_aggregate_collapse_max_positive_plus_min_negative() {
            // a.bottom=+25, empty.top=-8, empty.bottom=+12, c.top=-30.
            // All four participate in the single collapsed gap between a and c.
            // max(25,12)=25, min(-8,-30)=-30 => gap = 25+(-30) = -5.
            // c.Y = a.height + gap = 40 + (-5) = 35.
            var (root, _, _) = Build(
                "<div id=\"a\" style=\"height:40px;margin-bottom:25px\"></div>" +
                "<div id=\"empty\" style=\"margin-top:-8px;margin-bottom:12px\"></div>" +
                "<div id=\"c\" style=\"height:30px;margin-top:-30px\"></div>",
                null, 800);
            var c = FirstById(root, "c");
            Assert.That(c, Is.Not.Null);
            Assert.That(c.Y, Is.EqualTo(35).Within(0.001));
        }

        // -----------------------------------------------------------------------
        // §10 Extremely large negative margins — no geometry overflow
        // -----------------------------------------------------------------------

        [Test]
        public void Extremely_large_negative_block_margin_does_not_produce_infinity() {
            var (root, _, _) = Build(
                "<div id=\"a\" style=\"height:30px;margin-top:-9999px\"></div>" +
                "<div id=\"b\" style=\"height:30px\"></div>",
                null, 800);
            foreach (var b in AllBoxes(root)) {
                Assert.That(double.IsInfinity(b.Y), Is.False, "Inf in Y");
                Assert.That(double.IsNaN(b.Y), Is.False, "NaN in Y");
            }
        }

        [Test]
        public void Zero_negative_margin_is_identity() {
            // margin-top:0 (or -0) must behave identically to no margin.
            var (root, _, _) = Build(
                "<div id=\"a\" style=\"height:30px;margin-top:0\"></div>" +
                "<div id=\"b\" style=\"height:30px;margin-top:0\"></div>",
                null, 800);
            var a = FirstById(root, "a");
            var b = FirstById(root, "b");
            Assert.That(a, Is.Not.Null);
            Assert.That(b, Is.Not.Null);
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(30).Within(0.001));
        }
    }
}
