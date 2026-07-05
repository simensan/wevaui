using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // CSS 2.1 §9.5 float + §9.5.2 clear regression coverage. Until this file
    // existed the engine had ZERO direct tests for float/clear placement,
    // even though BlockLayout/FloatContext implement the feature. Each test
    // builds a small fragment via the shared `Build` helper, locates the
    // relevant block by element id, and asserts the placement rules.
    //
    // The helper convention mirrors BlockLayoutTests: same namespace, same
    // viewport-width-as-third-argument signature, same per-test
    // FindFirst<T>/FindAll<T> walking utility.
    //
    // Known v1 simplifications (kept as `// v1:` comments next to assertions):
    //   - Floats nested inside <span>/inline parents are NOT picked up by
    //     BlockLayout's direct-child pre-pass, so they never get a placement.
    //     `InlineLayout.CollectInline` still skips them in the inline stream,
    //     so they end up effectively invisible at (0,0). The corresponding
    //     test documents this rather than asserting spec-correct hoisting.
    public class FloatLayoutTests {
        // Walk every box under root in document order.
        static IEnumerable<Box> Walk(Box root) {
            yield return root;
            foreach (var c in root.Children) {
                foreach (var d in Walk(c)) yield return d;
            }
        }

        static T FindFirst<T>(Box root) where T : Box {
            foreach (var b in Walk(root)) if (b is T t) return t;
            return null;
        }

        static List<T> FindAll<T>(Box root) where T : Box {
            var list = new List<T>();
            foreach (var b in Walk(root)) if (b is T t) list.Add(t);
            return list;
        }

        // Finds the first non-anonymous BlockBox whose element has the given id.
        static BlockBox FindById(Box root, string id) {
            foreach (var b in Walk(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox)
                    && bb.Element != null
                    && bb.Element.GetAttribute("id") == id) {
                    return bb;
                }
            }
            return null;
        }

        // Finds the first non-anonymous BlockBox whose element matches tag.
        static BlockBox FindFirstByTag(Box root, string tag) {
            foreach (var b in Walk(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox)
                    && bb.Element?.TagName == tag) return bb;
            }
            return null;
        }

        [Test]
        public void Float_left_pulls_box_to_left_edge_of_containing_block() {
            // CSS 2.1 §9.5.1 rule 5: a float:left box's outer-left edge sits
            // at the containing block's content-left edge (x=0 with no padding).
            var (root, _, _) = Build(
                "<div style=\"width:400px\">" +
                "<div id=\"f\" style=\"float:left;width:100px;height:50px\"></div>" +
                "</div>",
                null, viewportWidth: 800);
            var f = FindById(root, "f");
            Assert.That(f, Is.Not.Null);
            Assert.That(f.IsFloat, Is.True);
            Assert.That(f.X, Is.EqualTo(0).Within(0.001));
            Assert.That(f.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(f.Width, Is.EqualTo(100).Within(0.001));
            Assert.That(f.Height, Is.EqualTo(50).Within(0.001));
        }

        [Test]
        public void Float_right_pulls_box_to_right_edge_of_containing_block() {
            // CSS 2.1 §9.5.1 rule 6: a float:right box's outer-right edge
            // sits at the containing block's content-right edge. With a
            // 400px container and a 100px float, x = 400 - 100 = 300.
            var (root, _, _) = Build(
                "<div style=\"width:400px\">" +
                "<div id=\"f\" style=\"float:right;width:100px;height:50px\"></div>" +
                "</div>",
                null, viewportWidth: 800);
            var f = FindById(root, "f");
            Assert.That(f, Is.Not.Null);
            Assert.That(f.IsFloat, Is.True);
            Assert.That(f.X, Is.EqualTo(300).Within(0.001));
            Assert.That(f.Y, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Two_floats_left_stack_horizontally_until_wrap() {
            // CSS 2.1 §9.5.1 rule 4: when a left float doesn't fit beside
            // earlier ones, it drops below them. Two 100px floats fit in a
            // 250px container; the third wraps to the next row.
            var (root, _, _) = Build(
                "<div style=\"width:250px\">" +
                "<div id=\"a\" style=\"float:left;width:100px;height:50px\"></div>" +
                "<div id=\"b\" style=\"float:left;width:100px;height:50px\"></div>" +
                "<div id=\"c\" style=\"float:left;width:100px;height:50px\"></div>" +
                "</div>",
                null, viewportWidth: 800);
            var a = FindById(root, "a");
            var b = FindById(root, "b");
            var c = FindById(root, "c");
            Assert.That(a.X, Is.EqualTo(0).Within(0.001));
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(b.X, Is.EqualTo(100).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(0).Within(0.001));
            // c didn't fit (200 + 100 > 250), so it drops to the next row.
            Assert.That(c.X, Is.EqualTo(0).Within(0.001));
            Assert.That(c.Y, Is.EqualTo(50).Within(0.001));
        }

        [Test]
        public void Float_left_then_block_sibling_wraps_around_float() {
            // CSS 2.1 §9.5: a block-level in-flow sibling that follows a
            // float keeps its own border box at the containing-block edges
            // (the float doesn't shrink the sibling's box), but the
            // sibling's inline content (line boxes) is shifted to avoid
            // the float. Verifies the <p>'s outer X stays at 0, and the
            // first line inside <p> starts to the right of the float.
            var (root, _, _) = Build(
                "<div style=\"width:400px\">" +
                "<div id=\"f\" style=\"float:left;width:80px;height:60px\"></div>" +
                "<p id=\"p\">hi</p>" +
                "</div>",
                null, viewportWidth: 800);
            var f = FindById(root, "f");
            var p = FindById(root, "p");
            Assert.That(f, Is.Not.Null);
            Assert.That(p, Is.Not.Null);
            // The <p> border box still starts at the containing block's
            // content edge — floats do not push in-flow blocks.
            Assert.That(p.X, Is.EqualTo(0).Within(0.001));
            Assert.That(p.Y, Is.EqualTo(0).Within(0.001));
            // The inline content (first LineBox child of <p>) should start
            // to the right of the float's right edge (x >= 80).
            LineBox firstLine = null;
            foreach (var c in p.Children) if (c is LineBox lb) { firstLine = lb; break; }
            Assert.That(firstLine, Is.Not.Null, "expected a LineBox child inside <p>");
            Assert.That(firstLine.X, Is.GreaterThanOrEqualTo(80 - 0.001),
                "first line should be pushed right of the 80px-wide float");
        }

        [Test]
        public void Clear_left_pushes_box_below_preceding_left_float() {
            // CSS 2.1 §9.5.2: `clear: left` forces the cleared box's top
            // margin edge below the bottom of any preceding left float.
            var (root, _, _) = Build(
                "<div style=\"width:400px\">" +
                "<div id=\"f\" style=\"float:left;width:80px;height:60px\"></div>" +
                "<div id=\"c\" style=\"clear:left;height:20px\"></div>" +
                "</div>",
                null, viewportWidth: 800);
            var c = FindById(root, "c");
            Assert.That(c, Is.Not.Null);
            // Float bottom = 60; cleared box top margin edge sits there.
            Assert.That(c.Y, Is.EqualTo(60).Within(0.001));
        }

        [Test]
        public void Clear_right_only_clears_right_floats() {
            // CSS 2.1 §9.5.2: `clear: right` ignores left floats. With only
            // a left float preceding, the cleared block sits at its normal
            // in-flow Y (no clearance introduced).
            var (root, _, _) = Build(
                "<div style=\"width:400px\">" +
                "<div id=\"f\" style=\"float:left;width:80px;height:60px\"></div>" +
                "<div id=\"c\" style=\"clear:right;height:20px\"></div>" +
                "</div>",
                null, viewportWidth: 800);
            var c = FindById(root, "c");
            Assert.That(c, Is.Not.Null);
            // No right float exists, so clear:right is a no-op. The
            // cleared block sits at the in-flow position y=0 (floats are
            // removed from the in-flow cursor, see §9.5).
            Assert.That(c.Y, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Clear_both_clears_left_and_right_floats() {
            // CSS 2.1 §9.5.2: `clear: both` waits for the taller of any
            // preceding left and right floats. Here left=40, right=70 → 70.
            var (root, _, _) = Build(
                "<div style=\"width:400px\">" +
                "<div id=\"l\" style=\"float:left;width:60px;height:40px\"></div>" +
                "<div id=\"r\" style=\"float:right;width:60px;height:70px\"></div>" +
                "<div id=\"c\" style=\"clear:both;height:20px\"></div>" +
                "</div>",
                null, viewportWidth: 800);
            var c = FindById(root, "c");
            Assert.That(c, Is.Not.Null);
            Assert.That(c.Y, Is.EqualTo(70).Within(0.001));
        }

        [Test]
        public void Float_does_not_affect_following_inline_text_width_below_it() {
            // CSS 2.1 §9.5: once a line's Y is past the bottom of all
            // intruding floats, that line uses the full content width.
            // The float here is 50px tall; the <p>'s narrow line-height
            // means later lines drop past Y=50 and should run full-width.
            // We verify by giving the <p> a multi-line text body and
            // checking that the LAST LineBox starts at x=0 (no intrusion).
            var (root, _, _) = Build(
                "<div style=\"width:200px;font-size:16px;line-height:20px\">" +
                "<div id=\"f\" style=\"float:left;width:60px;height:50px\"></div>" +
                "<p id=\"p\">aaaa bbbb cccc dddd eeee ffff gggg hhhh iiii jjjj kkkk</p>" +
                "</div>",
                null, viewportWidth: 800);
            var p = FindById(root, "p");
            Assert.That(p, Is.Not.Null);
            LineBox last = null;
            int lineCount = 0;
            foreach (var ch in p.Children) {
                if (ch is LineBox lb) { last = lb; lineCount++; }
            }
            Assert.That(lineCount, Is.GreaterThanOrEqualTo(2),
                "test wants multiple wrapped lines past the float's bottom");
            // The last line should sit at Y >= 50 (float bottom) and have
            // x=0 because no float intrudes there anymore.
            Assert.That(last.Y, Is.GreaterThanOrEqualTo(50 - 0.001));
            Assert.That(last.X, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Float_inside_inline_flow_renders_at_float_position_not_inline_position() {
            // CSS 2.1 §9.5.1 / §9.7: a float nested inside an inline
            // element escapes inline flow and places against its nearest
            // block-container ancestor. BoxBuilder blockifies
            // `<span style=float:left>` to a BlockBox per §9.7; BlockLayout
            // hoists that blockified float out of its InlineBox parent
            // into the containing block's child list before the float
            // pre-scan, so the float picks up its FloatType/Clear from
            // style and PlaceFloat positions it at the containing block's
            // content origin.
            var (root, _, _) = Build(
                "<div style=\"width:400px\">" +
                "<p id=\"p\"><span>before<span id=\"f\" style=\"float:left;width:80px;height:60px\"></span>after</span></p>" +
                "</div>",
                null, viewportWidth: 800);
            var f = FindById(root, "f");
            Assert.That(f, Is.Not.Null,
                "<span style=float:left> should be blockified to a BlockBox by BoxBuilder");
            Assert.That(f.IsFloat, Is.True,
                "blockified float in inline ancestor must be hoisted and have FloatType stamped");
            Assert.That(f.X, Is.EqualTo(0).Within(0.001),
                "float:left should sit at the containing block's left content edge");
            Assert.That(f.Width, Is.EqualTo(80).Within(0.001));
            Assert.That(f.Height, Is.EqualTo(60).Within(0.001));
        }

        [Test]
        public void Float_with_margin_separates_from_container_edge() {
            // CSS 2.1 §9.5.1: float margins are honoured — outer margin-
            // left of 16px on a float:left means the float's border-box
            // sits at x=16.
            var (root, _, _) = Build(
                "<div style=\"width:400px\">" +
                "<div id=\"f\" style=\"float:left;width:100px;height:50px;margin-left:16px\"></div>" +
                "</div>",
                null, viewportWidth: 800);
            var f = FindById(root, "f");
            Assert.That(f, Is.Not.Null);
            Assert.That(f.X, Is.EqualTo(16).Within(0.001));
            Assert.That(f.Width, Is.EqualTo(100).Within(0.001));
        }

        [Test]
        public void Float_left_then_float_right_sit_at_opposite_edges_same_line() {
            // CSS 2.1 §9.5.1: a left float and a right float that fit on
            // the same row share that row — left hugs the left edge, right
            // hugs the right edge.
            var (root, _, _) = Build(
                "<div style=\"width:400px\">" +
                "<div id=\"l\" style=\"float:left;width:100px;height:50px\"></div>" +
                "<div id=\"r\" style=\"float:right;width:100px;height:50px\"></div>" +
                "</div>",
                null, viewportWidth: 800);
            var l = FindById(root, "l");
            var r = FindById(root, "r");
            Assert.That(l.X, Is.EqualTo(0).Within(0.001));
            Assert.That(l.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(r.X, Is.EqualTo(300).Within(0.001));
            Assert.That(r.Y, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Block_with_overflow_hidden_establishes_new_BFC_and_contains_floats() {
            // CSS 2.1 §10.6.7: a block that establishes a new BFC (e.g.
            // overflow != visible) grows to enclose any floats it
            // contains. Without a BFC the parent's auto height would
            // collapse to 0; with `overflow: hidden` it should be 50.
            var (root, _, _) = Build(
                "<div id=\"wrap\" style=\"overflow:hidden;width:400px\">" +
                "<div style=\"float:left;width:100px;height:50px\"></div>" +
                "<div style=\"float:left;width:100px;height:30px\"></div>" +
                "</div>",
                null, viewportWidth: 800);
            var wrap = FindById(root, "wrap");
            Assert.That(wrap, Is.Not.Null);
            // Taller float = 50 → BFC grows to 50.
            Assert.That(wrap.Height, Is.EqualTo(50).Within(0.001));
        }
    }
}
