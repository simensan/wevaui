using System.Collections.Generic;
using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    public class MarginCollapsingTests {
        static List<BlockBox> AllNamedBlocks(Box root) {
            var list = new List<BlockBox>();
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && !(b is AnonymousBlockBox) && bb.Element != null) list.Add(bb);
            }
            return list;
        }

        static BlockBox FirstById(Box root, string id) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element != null) {
                    var attrId = bb.Element.GetAttribute("id");
                    if (attrId == id) return bb;
                }
            }
            return null;
        }

        [Test]
        public void Two_positive_margins_collapse_to_max() {
            var (root, _, _) = Build(
                "<div id=\"a\" style=\"margin-bottom:20px;height:30px\"></div>" +
                "<div id=\"b\" style=\"margin-top:30px;height:30px\"></div>",
                null, 800);
            var b = FirstById(root, "b");
            // a.bottom=30, gap = max(20,30) = 30 => b.Y = 60.
            Assert.That(b.Y, Is.EqualTo(60).Within(0.001));
        }

        [Test]
        public void Two_negative_margins_collapse_to_min() {
            var (root, _, _) = Build(
                "<div id=\"a\" style=\"margin-bottom:-10px;height:30px\"></div>" +
                "<div id=\"b\" style=\"margin-top:-20px;height:30px\"></div>",
                null, 800);
            var b = FirstById(root, "b");
            // a.bottom=30, gap = min(-10,-20) = -20 => b.Y = 10.
            Assert.That(b.Y, Is.EqualTo(10).Within(0.001));
        }

        [Test]
        public void Mixed_sign_margins_sum_algebraically() {
            var (root, _, _) = Build(
                "<div id=\"a\" style=\"margin-bottom:20px;height:30px\"></div>" +
                "<div id=\"b\" style=\"margin-top:-5px;height:30px\"></div>",
                null, 800);
            var b = FirstById(root, "b");
            // a.bottom=30, gap = 20 + (-5) = 15 => b.Y = 45.
            Assert.That(b.Y, Is.EqualTo(45).Within(0.001));
        }

        [Test]
        public void Parent_top_margin_collapses_with_first_child_when_top_open() {
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"margin-top:30px\">" +
                "<div id=\"first\" style=\"margin-top:20px;height:50px\"></div>" +
                "</div>",
                null, 800);
            var outer = FirstById(root, "outer");
            var first = FirstById(root, "first");
            // outer.MarginTop becomes max(30, 20) = 30. The first child sits at the
            // top of the parent's content (Y=0 within outer; absolute Y=30).
            Assert.That(outer.MarginTop, Is.EqualTo(30).Within(0.001));
            Assert.That(first.Y, Is.EqualTo(0).Within(0.001));
            // With the HTML5 fragment wrapper (Document > <html> > <body> > <div>),
            // outer's margin-top further collapses up through the empty body/html,
            // so outer.Y within its parent is 0. Use absolute coordinates to assert
            // the 30px gap from the page origin.
            Assert.That(AbsoluteOrigin(outer).Y, Is.EqualTo(30).Within(0.001));
        }

        [Test]
        public void Parent_top_padding_blocks_first_child_collapse() {
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"margin-top:30px;padding-top:5px\">" +
                "<div id=\"first\" style=\"margin-top:20px;height:50px\"></div>" +
                "</div>",
                null, 800);
            var outer = FirstById(root, "outer");
            var first = FirstById(root, "first");
            // Top padding closes the top: no collapse. outer.MarginTop stays 30,
            // first.Y = paddingTop + childMarginTop = 5 + 20 = 25 (within outer).
            Assert.That(outer.MarginTop, Is.EqualTo(30).Within(0.001));
            Assert.That(first.Y, Is.EqualTo(25).Within(0.001));
        }

        [Test]
        public void Parent_bottom_margin_collapses_with_last_child_when_bottom_open() {
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"margin-bottom:30px\">" +
                "<div id=\"last\" style=\"margin-bottom:25px;height:40px\"></div>" +
                "</div>",
                null, 800);
            var outer = FirstById(root, "outer");
            // outer.MarginBottom becomes max(30, 25) = 30. outer.Height does NOT
            // include the collapsed margin: it stops at the last child's bottom edge.
            Assert.That(outer.MarginBottom, Is.EqualTo(30).Within(0.001));
            Assert.That(outer.Height, Is.EqualTo(40).Within(0.001));
        }

        [Test]
        public void Three_blocks_in_a_row_collapse_pairwise() {
            var (root, _, _) = Build(
                "<div id=\"a\" style=\"margin-bottom:10px;height:20px\"></div>" +
                "<div id=\"b\" style=\"margin-top:30px;margin-bottom:5px;height:20px\"></div>" +
                "<div id=\"c\" style=\"margin-top:25px;height:20px\"></div>",
                null, 800);
            var a = FirstById(root, "a");
            var b = FirstById(root, "b");
            var c = FirstById(root, "c");
            // a-b gap: max(10, 30) = 30 => b.Y = 20 + 30 = 50.
            // b-c gap: max(5, 25) = 25 => c.Y = 70 + 25 = 95.
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(b.Y, Is.EqualTo(50).Within(0.001));
            Assert.That(c.Y, Is.EqualTo(95).Within(0.001));
        }

        [Test]
        public void Empty_block_self_collapses_top_and_bottom_margins() {
            var (root, _, _) = Build(
                "<div id=\"a\" style=\"margin-bottom:10px;height:20px\"></div>" +
                "<div id=\"empty\" style=\"margin-top:8px;margin-bottom:12px\"></div>" +
                "<div id=\"c\" style=\"margin-top:5px;height:20px\"></div>",
                null, 800);
            var a = FirstById(root, "a");
            var c = FirstById(root, "c");
            // empty self-collapses to max(8, 12) = 12 (both positive). The chain
            // becomes a.bottom-margin (10) <-> 12 <-> c.top-margin (5) = max all
            // three = 12. So c.Y = a.Y + a.Height + 12 = 0 + 20 + 12 = 32.
            Assert.That(c.Y, Is.EqualTo(32).Within(0.001));
        }

        [Test]
        public void Block_with_explicit_height_does_not_collapse_with_last_child_bottom() {
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"margin-bottom:30px;height:100px\">" +
                "<div id=\"last\" style=\"margin-bottom:50px;height:20px\"></div>" +
                "</div>",
                null, 800);
            var outer = FirstById(root, "outer");
            // Explicit height blocks last-child bottom collapsing into parent.
            Assert.That(outer.MarginBottom, Is.EqualTo(30).Within(0.001));
            Assert.That(outer.Height, Is.EqualTo(100).Within(0.001));
        }

        [Test]
        public void Inline_block_does_not_collapse_with_surrounding_blocks() {
            // <span style="display:inline-block"> can't be a block-level sibling,
            // so we verify margins on an inline-block sibling are kept verbatim
            // by placing it INSIDE a wrapper so it sits at block level via mixed-
            // flow semantics. Simpler: declare a <div style="display:inline-block">
            // as the middle element. Anonymous-block wrapping keeps it inline so
            // it doesn't collapse with the surrounding blocks.
            var (root, _, _) = Build(
                "<div id=\"a\" style=\"margin-bottom:20px;height:30px\"></div>" +
                "<div id=\"ib\" style=\"display:inline-block;margin-top:30px;margin-bottom:30px;height:20px;width:50px\"></div>" +
                "<div id=\"c\" style=\"margin-top:20px;height:30px\"></div>",
                null, 800);
            var a = FirstById(root, "a");
            var ib = FirstById(root, "ib");
            var c = FirstById(root, "c");
            // The inline-block sits inside an anonymous block container; the
            // anonymous block's margins are zero (it has no Style). The surrounding
            // blocks therefore see their gaps filled by the anonymous block's
            // height and apply their own margins literally.
            // Topology: a (height 30) -> anon containing inline-block -> c.
            // inline-block height 20, but its participation rules differ — verify
            // a.Y + a.Height < c.Y so margins stayed put.
            Assert.That(a.Y, Is.EqualTo(0).Within(0.001));
            Assert.That(c.Y, Is.GreaterThan(a.Y + a.Height));
        }

        [Test]
        public void Margin_collapsing_through_anonymous_blocks() {
            // CSS spec: collapsing crosses anonymous block boundaries. Our
            // anonymous block has zero margin, so the spec's collapse-through-
            // empty rule yields the same answer as direct sibling collapse.
            // Document case: text between two block siblings creates an anonymous
            // block. Margins on the surrounding real blocks should still collapse
            // (they don't touch each other directly, but the anonymous block has
            // no padding/border/height/margin and self-collapses to nothing).
            var (root, _, _) = Build(
                "<div id=\"outer\">" +
                "<div id=\"a\" style=\"margin-bottom:20px;height:30px\"></div>" +
                "  " +
                "<div id=\"b\" style=\"margin-top:30px;height:30px\"></div>" +
                "</div>",
                null, 800);
            var b = FirstById(root, "b");
            // Expectation: anonymous text between a and b is whitespace-only and
            // is filtered out (per existing AreAllWhitespaceTextRuns rule), so a
            // and b are direct siblings. Gap = max(20, 30) = 30 => b.Y = 60 within
            // outer (outer.Y = 0).
            Assert.That(b.Y, Is.EqualTo(60).Within(0.001));
        }

        [Test]
        public void Negative_only_parent_margin_top_collapses_with_first_child() {
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"margin-top:-10px\">" +
                "<div id=\"first\" style=\"margin-top:-20px;height:30px\"></div>" +
                "</div>",
                null, 800);
            var outer = FirstById(root, "outer");
            // Both negative => min(-10, -20) = -20.
            Assert.That(outer.MarginTop, Is.EqualTo(-20).Within(0.001));
        }

        [Test]
        public void Mixed_sign_parent_and_first_child_top_margin() {
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"margin-top:30px\">" +
                "<div id=\"first\" style=\"margin-top:-10px;height:30px\"></div>" +
                "</div>",
                null, 800);
            var outer = FirstById(root, "outer");
            // Mixed sign => 30 + (-10) = 20.
            Assert.That(outer.MarginTop, Is.EqualTo(20).Within(0.001));
        }

        [Test]
        public void First_child_with_zero_margin_top_keeps_parent_margin() {
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"margin-top:30px\">" +
                "<div id=\"first\" style=\"height:30px\"></div>" +
                "</div>",
                null, 800);
            var outer = FirstById(root, "outer");
            var first = FirstById(root, "first");
            // max(30, 0) = 30. No-op collapse.
            Assert.That(outer.MarginTop, Is.EqualTo(30).Within(0.001));
            Assert.That(first.Y, Is.EqualTo(0).Within(0.001));
        }

        // ----------------------------------------------------------------------
        // Gap-pinning: rules covered by CSS 2.1 §8.3.1 that the current
        // implementation does NOT honor. Each [Ignore]'d test documents the
        // shortcoming and should pass once the gap is closed.
        // ----------------------------------------------------------------------

        [Test]
        public void Overflow_hidden_parent_blocks_first_child_top_collapse() {
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"margin-top:30px;overflow:hidden\">" +
                "<div id=\"first\" style=\"margin-top:20px;height:50px\"></div>" +
                "</div>",
                null, 800);
            var outer = FirstById(root, "outer");
            var first = FirstById(root, "first");
            // overflow:hidden creates a new BFC -> no collapse. Parent margin
            // stays 30, child sits at Y=20 inside the parent.
            Assert.That(outer.MarginTop, Is.EqualTo(30).Within(0.001));
            Assert.That(first.Y, Is.EqualTo(20).Within(0.001));
        }

        [Test]
        public void Flow_root_parent_blocks_child_top_collapse() {
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"margin-top:30px;display:flow-root\">" +
                "<div id=\"first\" style=\"margin-top:20px;height:50px\"></div>" +
                "</div>",
                null, 800);
            var outer = FirstById(root, "outer");
            var first = FirstById(root, "first");
            Assert.That(outer.MarginTop, Is.EqualTo(30).Within(0.001));
            Assert.That(first.Y, Is.EqualTo(20).Within(0.001));
        }

        [Test]
        public void Min_height_in_pixels_blocks_last_child_bottom_collapse() {
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"margin-bottom:30px;min-height:1px\">" +
                "<div id=\"last\" style=\"margin-bottom:50px;height:20px\"></div>" +
                "</div>",
                null, 800);
            var outer = FirstById(root, "outer");
            // min-height is non-auto -> per spec, bottom does NOT collapse.
            // outer.MarginBottom should remain 30; the last child's 50px margin
            // sits inside the parent (parent height grows to 70).
            Assert.That(outer.MarginBottom, Is.EqualTo(30).Within(0.001));
            Assert.That(outer.Height, Is.EqualTo(70).Within(0.001));
        }

        [Test]
        public void Three_way_mixed_sign_collapse_uses_max_plus_min_not_pairwise_fold() {
            var (root, _, _) = Build(
                "<div id=\"a\" style=\"margin-bottom:20px;height:30px\"></div>" +
                "<div id=\"empty\" style=\"margin-top:-15px;margin-bottom:10px\"></div>" +
                "<div id=\"c\" style=\"margin-top:-25px;height:30px\"></div>",
                null, 800);
            var c = FirstById(root, "c");
            // Participants: +20, -15, +10, -25. Spec: max(20,10)=20, min(-15,-25)=-25,
            // gap = 20 + (-25) = -5. Expected c.Y = 30 + (-5) = 25.
            // Pairwise fold yields a different (wrong) answer.
            Assert.That(c.Y, Is.EqualTo(25).Within(0.001));
        }

        [Test]
        public void Zero_pixel_height_block_self_collapses_through() {
            var (root, _, _) = Build(
                "<div id=\"a\" style=\"margin-bottom:20px;height:30px\"></div>" +
                "<div id=\"b\" style=\"margin-top:0;margin-bottom:0;height:0px\"></div>" +
                "<div id=\"c\" style=\"margin-top:30px;height:30px\"></div>",
                null, 800);
            var c = FirstById(root, "c");
            Assert.That(c.Y, Is.EqualTo(60).Within(0.001));
        }

        [Test]
        public void One_pixel_height_block_blocks_collapse_through() {
            var (root, _, _) = Build(
                "<div id=\"a\" style=\"margin-bottom:20px;height:30px\"></div>" +
                "<div id=\"b\" style=\"margin-top:0;margin-bottom:0;height:1px\"></div>" +
                "<div id=\"c\" style=\"margin-top:30px;height:30px\"></div>",
                null, 800);
            var c = FirstById(root, "c");
            Assert.That(c.Y, Is.EqualTo(81).Within(0.001));
        }

        // ----------------------------------------------------------------------
        // A14: EstablishesNewBfc must cover all common BFC roots. Without these
        // a flex/grid/table/abs-pos/float/inline-block parent would let its
        // first child's top-margin collapse THROUGH the boundary, shifting the
        // parent (or the child relative to the parent) instead of staying inside.
        // ----------------------------------------------------------------------

        [Test]
        public void Flex_parent_blocks_first_child_top_margin_collapse() {
            // display:flex establishes a new BFC: the first item's margin-top
            // must NOT collapse with the flex container's margin-top. We use a
            // child margin-top (50) larger than the parent's (10) so a buggy
            // collapse-through would grow `outer.MarginTop` to 50.
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"display:flex;flex-direction:column;margin-top:10px\">" +
                "<div id=\"first\" style=\"margin-top:50px;height:30px\"></div>" +
                "</div>",
                null, 800);
            var outer = FirstById(root, "outer");
            var first = FirstById(root, "first");
            // Flex container's margin stays at its declared 10px (no collapse).
            Assert.That(outer.MarginTop, Is.EqualTo(10).Within(0.001));
            // First item visible top edge (in BFC coords) sits at outer.Y plus
            // its own margin-top, not at outer.Y.
            Assert.That(first.Y + outer.Y, Is.GreaterThanOrEqualTo(outer.Y + 50 - 0.001));
        }

        [Test]
        public void Grid_parent_blocks_first_child_top_margin_collapse() {
            // display:grid establishes a new BFC: the first item's margin-top
            // must NOT collapse with the grid container's margin-top. Child
            // margin (50) > parent margin (10): a leak would grow outer.MarginTop.
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"display:grid;margin-top:10px\">" +
                "<div id=\"first\" style=\"margin-top:50px;height:30px\"></div>" +
                "</div>",
                null, 800);
            var outer = FirstById(root, "outer");
            // Grid container's margin stays at its declared 10px (no collapse).
            Assert.That(outer.MarginTop, Is.EqualTo(10).Within(0.001));
        }

        [Test]
        public void Floated_parent_blocks_first_child_top_margin_collapse() {
            // A floated box establishes a new BFC: child margin-top must NOT
            // collapse into the float's own margin-top. Child margin (50) >
            // parent margin (10).
            var (root, _, _) = Build(
                "<div style=\"width:400px\">" +
                "<div id=\"outer\" style=\"float:left;width:200px;margin-top:10px\">" +
                "<div id=\"first\" style=\"margin-top:50px;height:30px\"></div>" +
                "</div>" +
                "</div>",
                null, 800);
            var outer = FirstById(root, "outer");
            var first = FirstById(root, "first");
            Assert.That(outer.IsFloat, Is.True);
            // The float's own margin-top is preserved verbatim (10) and does
            // NOT collapse with the inner child's 50px margin-top.
            Assert.That(outer.MarginTop, Is.EqualTo(10).Within(0.001));
            // The child sits inside the float at its own margin-top.
            Assert.That(first.Y, Is.EqualTo(50).Within(0.001));
        }

        [Test]
        public void Absolute_parent_blocks_first_child_top_margin_collapse() {
            // position:absolute establishes a new BFC: child margin-top must
            // stay inside the abs-pos box (no collapse-through). Child margin
            // (50) > parent margin (10) so a leak would be observable.
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"position:absolute;margin-top:10px\">" +
                "<div id=\"first\" style=\"margin-top:50px;height:30px\"></div>" +
                "</div>",
                null, 800);
            var outer = FirstById(root, "outer");
            var first = FirstById(root, "first");
            Assert.That(outer, Is.Not.Null);
            Assert.That(first, Is.Not.Null);
            // Outer's margin-top stays 10; child margin-top doesn't leak out.
            Assert.That(outer.MarginTop, Is.EqualTo(10).Within(0.001));
            // First child sits at its own margin-top INSIDE the abs-pos box.
            Assert.That(first.Y, Is.EqualTo(50).Within(0.001));
        }

        [Test]
        public void Inline_block_parent_blocks_first_child_top_margin_collapse() {
            // display:inline-block establishes a new BFC: child margin-top
            // must stay inside. Child margin (50) > parent margin (10).
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"display:inline-block;margin-top:10px\">" +
                "<div id=\"first\" style=\"margin-top:50px;height:30px\"></div>" +
                "</div>",
                null, 800);
            var outer = FirstById(root, "outer");
            var first = FirstById(root, "first");
            Assert.That(outer, Is.Not.Null);
            Assert.That(first, Is.Not.Null);
            // Inline-block's own margin-top stays at 10; child does not leak.
            Assert.That(outer.MarginTop, Is.EqualTo(10).Within(0.001));
            // Child sits at its own margin-top inside the inline-block.
            Assert.That(first.Y, Is.EqualTo(50).Within(0.001));
        }

        [Test]
        public void Float_between_siblings_does_not_break_collapse() {
            var (root, _, _) = Build(
                "<div id=\"a\" style=\"margin-bottom:20px;height:30px\"></div>" +
                "<div id=\"f\" style=\"float:left;margin-top:100px;margin-bottom:100px;height:10px;width:10px\"></div>" +
                "<div id=\"b\" style=\"margin-top:30px;height:30px\"></div>",
                null, 800);
            var b = FirstById(root, "b");
            // a-b collapse to max(20,30)=30 even though a float sits between them.
            Assert.That(b.Y, Is.EqualTo(60).Within(0.001));
        }

        // ----------------------------------------------------------------------
        // MARGINCOLAPSE-RELATIVE (CSS 2.1 §8.3.1): a child's margin-top MUST
        // collapse through a position:relative parent that has no border/padding/
        // clearance. Relative positioning does NOT establish a BFC — only
        // overflow≠visible, floats, absolutes, flex/grid containers, and
        // border/padding between parent and child stop the collapse.
        // Fixed by removing the erroneous `height != "auto"` guard from
        // MarginCollapsing.ParentTopOpen.
        // ----------------------------------------------------------------------

        [Test]
        public void Child_margin_top_collapses_through_plain_parent_no_height() {
            // Control: plain block with no border/padding/height — baseline case
            // that should have worked before the fix too.
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"margin-top:32px\">" +
                "<div id=\"child\" style=\"margin-top:16px;height:50px\"></div>" +
                "</div>",
                null, 800);
            var outer = FirstById(root, "outer");
            var child = FirstById(root, "child");
            // CSS 2.1 §8.3.1: max(32, 16) = 32 collapses onto the outer box.
            // Child sits at the top content edge of outer (Y=0 within outer).
            Assert.That(outer.MarginTop, Is.EqualTo(32).Within(0.001));
            Assert.That(child.Y, Is.EqualTo(0).Within(0.001));
        }

        [Test]
        public void Child_margin_top_collapses_through_position_relative_parent_with_explicit_height() {
            // MARGINCOLAPSE-RELATIVE: position:relative with explicit height MUST
            // still let the first child's top-margin collapse through. An explicit
            // height only closes the BOTTOM (per CSS 2.1 §8.3.1), not the top.
            // This is the exact configuration from snippet 08-positioning-absolute:
            //   container { position:relative; height:300px; margin-top:32px }
            //   child      { margin-top:16px }
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"position:relative;height:300px;margin-top:32px\">" +
                "<div id=\"child\" style=\"margin-top:16px;height:160px\"></div>" +
                "</div>",
                null, 800);
            var outer = FirstById(root, "outer");
            var child = FirstById(root, "child");
            // max(32, 16) = 32 collapses to the outer margin. Child sits flush at
            // the inner top edge (Y=0 relative to outer, no inner gap).
            Assert.That(outer.MarginTop, Is.EqualTo(32).Within(0.001));
            Assert.That(child.Y, Is.EqualTo(0).Within(0.001));
            // Verify absolute position: outer collapsed to viewport edge + 32px.
            Assert.That(AbsoluteOrigin(outer).Y, Is.EqualTo(32).Within(0.001));
            // Child's absolute Y equals outer's absolute Y (no inner gap).
            Assert.That(AbsoluteOrigin(child).Y, Is.EqualTo(AbsoluteOrigin(outer).Y).Within(0.001));
        }

        [Test]
        public void Child_margin_top_does_not_collapse_through_border_top_on_relative_parent() {
            // CSS 2.1 §8.3.1 rule 1: a border between parent top edge and first
            // child's top margin edge blocks the collapse — even for position:relative.
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"position:relative;margin-top:32px;border-top:2px solid black\">" +
                "<div id=\"child\" style=\"margin-top:16px;height:50px\"></div>" +
                "</div>",
                null, 800);
            var outer = FirstById(root, "outer");
            var child = FirstById(root, "child");
            // Border closes the top — no collapse. Outer keeps margin-top=32;
            // child sits at borderTop + marginTop = 2 + 16 = 18 inside outer.
            Assert.That(outer.MarginTop, Is.EqualTo(32).Within(0.001));
            Assert.That(child.Y, Is.EqualTo(18).Within(0.001));
        }

        [Test]
        public void Child_margin_top_does_not_collapse_through_padding_top_on_relative_parent() {
            // CSS 2.1 §8.3.1 rule 1: padding between parent top and first child
            // similarly blocks the collapse.
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"position:relative;margin-top:32px;padding-top:8px\">" +
                "<div id=\"child\" style=\"margin-top:16px;height:50px\"></div>" +
                "</div>",
                null, 800);
            var outer = FirstById(root, "outer");
            var child = FirstById(root, "child");
            // Padding closes the top — no collapse. Outer keeps margin-top=32;
            // child sits at paddingTop + marginTop = 8 + 16 = 24 inside outer.
            Assert.That(outer.MarginTop, Is.EqualTo(32).Within(0.001));
            Assert.That(child.Y, Is.EqualTo(24).Within(0.001));
        }

        [Test]
        public void Child_margin_top_does_not_collapse_into_overflow_hidden_relative_parent() {
            // CSS 2.1 §8.3.1: overflow:hidden establishes a new BFC regardless of
            // position, so the child's top margin stays inside.
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"position:relative;overflow:hidden;margin-top:32px\">" +
                "<div id=\"child\" style=\"margin-top:16px;height:50px\"></div>" +
                "</div>",
                null, 800);
            var outer = FirstById(root, "outer");
            var child = FirstById(root, "child");
            // BFC root: no collapse. Outer keeps margin-top=32;
            // child sits at its own margin-top (16) inside outer.
            Assert.That(outer.MarginTop, Is.EqualTo(32).Within(0.001));
            Assert.That(child.Y, Is.EqualTo(16).Within(0.001));
        }

        [Test]
        public void Relative_parent_own_inset_offset_is_independent_of_collapsed_margin() {
            // CSS 2.1 §9.4.3: a position:relative box's top/left offsets apply
            // AFTER layout (as a visual shift). They are orthogonal to margin
            // collapsing. This test verifies the margin collapses correctly and
            // the layout Y (before visual offset) is sane. The visual offset is
            // applied by the rendering/positioning pass, not BlockLayout, so we
            // only check the layout-tree Y here.
            var (root, _, _) = Build(
                "<div id=\"outer\" style=\"position:relative;margin-top:32px\">" +
                "<div id=\"child\" style=\"margin-top:20px;height:50px\"></div>" +
                "</div>",
                null, 800);
            var outer = FirstById(root, "outer");
            var child = FirstById(root, "child");
            // max(32, 20) = 32; child at inner top (Y=0 within outer).
            Assert.That(outer.MarginTop, Is.EqualTo(32).Within(0.001));
            Assert.That(child.Y, Is.EqualTo(0).Within(0.001));
        }

        // L14: a NaN margin (bad calc()/animated length) must not poison the
        // collapse — it used to fall through both sign tests to `a + b` (NaN)
        // and propagate down the whole margin chain. Treated as absent now.
        [Test]
        public void Collapse_with_nan_input_does_not_propagate_nan() {
            Assert.That(double.IsNaN(Weva.Layout.MarginCollapsing.Collapse(double.NaN, 10)), Is.False);
            Assert.That(Weva.Layout.MarginCollapsing.Collapse(double.NaN, 10), Is.EqualTo(10));
            Assert.That(Weva.Layout.MarginCollapsing.Collapse(7, double.NaN), Is.EqualTo(7));
            Assert.That(Weva.Layout.MarginCollapsing.Collapse(double.NaN, -4), Is.EqualTo(-4));
            Assert.That(Weva.Layout.MarginCollapsing.Collapse(double.NaN, double.NaN), Is.EqualTo(0));
        }

        // The NaN guard must leave every finite case byte-identical.
        [Test]
        public void Collapse_finite_cases_unchanged_by_nan_guard() {
            Assert.That(Weva.Layout.MarginCollapsing.Collapse(20, 32), Is.EqualTo(32), "both positive -> max");
            Assert.That(Weva.Layout.MarginCollapsing.Collapse(-5, -12), Is.EqualTo(-12), "both negative -> min");
            Assert.That(Weva.Layout.MarginCollapsing.Collapse(20, -8), Is.EqualTo(12), "mixed -> sum");
            Assert.That(Weva.Layout.MarginCollapsing.Collapse(0, 15), Is.EqualTo(15));
            Assert.That(Weva.Layout.MarginCollapsing.Collapse(0, -15), Is.EqualTo(-15));
        }
    }
}
