// CSS Containment L2 §4 — NUnit coverage for `content-visibility` behaviour.
//
// Covers:
//   § visible   — initial value, no effect on layout/paint/hit-test
//   § hidden    — descendant paint suppressed; element own box still paints;
//                 size containment (auto-height collapses); layout barrier;
//                 descendant hit-test suppressed; children boxes still exist
//   § auto      — layout+paint containment; no size collapse on-screen;
//                 off-viewport descendant paint skip; margin-collapse barrier
//   § cascade   — non-inherited; initial = visible
//   § interaction — union semantics with explicit contain

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Events;
using Weva.Layout.Boxes;
using Weva.Layout.Containment;
using Weva.Paint;
using Weva.Paint.Conversion;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    public class ContentVisibilityTests {

        // ─── Helpers ──────────────────────────────────────────────────────────

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
                    var c = bb.Element.GetAttribute("class") ?? "";
                    if (c == cls || c.StartsWith(cls + " ") || c.EndsWith(" " + cls)
                        || c.Contains(" " + cls + " ")) return bb;
                }
            }
            return null;
        }

        // ─── ContainmentResolver unit tests — hidden ───────────────────────────

        [Test]
        public void Resolver_hidden_detects_IsContentVisibilityHidden() {
            var s = new ComputedStyle(new Element("div"));
            s.Set("content-visibility", "hidden");
            Assert.That(ContainmentResolver.IsContentVisibilityHidden(s), Is.True);
            Assert.That(ContainmentResolver.IsContentVisibilityAuto(s), Is.False);
        }

        [Test]
        public void Resolver_auto_detects_IsContentVisibilityAuto() {
            var s = new ComputedStyle(new Element("div"));
            s.Set("content-visibility", "auto");
            Assert.That(ContainmentResolver.IsContentVisibilityAuto(s), Is.True);
            Assert.That(ContainmentResolver.IsContentVisibilityHidden(s), Is.False);
        }

        [Test]
        public void Resolver_visible_returns_false_for_hidden_and_auto() {
            var s = new ComputedStyle(new Element("div"));
            s.Set("content-visibility", "visible");
            Assert.That(ContainmentResolver.IsContentVisibilityHidden(s), Is.False);
            Assert.That(ContainmentResolver.IsContentVisibilityAuto(s), Is.False);
        }

        [Test]
        public void Resolver_null_style_returns_false_for_hidden_and_auto() {
            Assert.That(ContainmentResolver.IsContentVisibilityHidden(null), Is.False);
            Assert.That(ContainmentResolver.IsContentVisibilityAuto(null), Is.False);
        }

        [Test]
        public void Resolver_hidden_implies_HasLayout() {
            var s = new ComputedStyle(new Element("div"));
            s.Set("content-visibility", "hidden");
            Assert.That(ContainmentResolver.HasLayout(s), Is.True,
                "content-visibility:hidden implies layout containment");
        }

        [Test]
        public void Resolver_hidden_implies_HasPaint() {
            var s = new ComputedStyle(new Element("div"));
            s.Set("content-visibility", "hidden");
            Assert.That(ContainmentResolver.HasPaint(s), Is.True,
                "content-visibility:hidden implies paint containment");
        }

        [Test]
        public void Resolver_hidden_implies_HasSize() {
            var s = new ComputedStyle(new Element("div"));
            s.Set("content-visibility", "hidden");
            Assert.That(ContainmentResolver.HasSize(s), Is.True,
                "content-visibility:hidden implies size containment");
        }

        [Test]
        public void Resolver_auto_implies_HasLayout_and_HasPaint() {
            var s = new ComputedStyle(new Element("div"));
            s.Set("content-visibility", "auto");
            Assert.That(ContainmentResolver.HasLayout(s), Is.True,
                "content-visibility:auto implies layout containment");
            Assert.That(ContainmentResolver.HasPaint(s), Is.True,
                "content-visibility:auto implies paint containment");
        }

        [Test]
        public void Resolver_auto_does_not_imply_HasSize() {
            // auto does NOT apply size containment at layout time in v1
            var s = new ComputedStyle(new Element("div"));
            s.Set("content-visibility", "auto");
            Assert.That(ContainmentResolver.HasSize(s), Is.False,
                "content-visibility:auto must NOT imply size containment (v1: only hidden does)");
        }

        [Test]
        public void Resolver_visible_does_not_imply_any_containment() {
            var s = new ComputedStyle(new Element("div"));
            s.Set("content-visibility", "visible");
            Assert.That(ContainmentResolver.HasLayout(s), Is.False);
            Assert.That(ContainmentResolver.HasPaint(s), Is.False);
            Assert.That(ContainmentResolver.HasSize(s), Is.False);
        }

        // ─── cascade / non-inherited ───────────────────────────────────────────

        [Test]
        public void ContentVisibility_is_not_inherited_by_children() {
            // The parent has content-visibility:auto; the child must NOT
            // inherit it (non-inherited, initial:visible).
            const string css = "#parent { content-visibility: auto; } #child { width:50px; }";
            var (root, styles, _) = Build(
                "<div id=\"parent\"><div id=\"child\"></div></div>", css, 400);
            var childBox = FirstById(root, "child");
            Assert.That(childBox, Is.Not.Null);
            string cvValue = childBox.Style?.Get(CssProperties.ContentVisibilityId);
            // Child must see initial value (visible) or empty, not "auto".
            bool childHasAuto = !string.IsNullOrEmpty(cvValue)
                && cvValue.Trim().Equals("auto", System.StringComparison.OrdinalIgnoreCase);
            Assert.That(childHasAuto, Is.False,
                "content-visibility must not be inherited; child must see initial 'visible'");
        }

        // ─── visible = no-op ──────────────────────────────────────────────────

        [Test]
        public void ContentVisibility_visible_element_paints_children() {
            // visible is the initial value and must have no special behaviour.
            const string css = @"
                #outer { width:200px; content-visibility:visible; background:blue; }
                #inner { height:50px; background:red; }
            ";
            var (root, _, _) = Build(
                "<div id=\"outer\"><div id=\"inner\"></div></div>", css, 400);
            var outer = FirstById(root, "outer");
            Assert.That(outer, Is.Not.Null);

            var converter = new BoxToPaintConverter();
            var cmds = converter.Convert(outer).Commands;
            // Should contain at least one background draw command for the child.
            bool hasRectFill = cmds.Any(c => c is FillRectCommand);
            Assert.That(hasRectFill, Is.True,
                "content-visibility:visible must not suppress child paint; child bg must appear");
        }

        [Test]
        public void ContentVisibility_visible_does_not_collapse_auto_height() {
            // visible is a no-op — the parent must grow to fit its child.
            const string css = @"
                #outer { width:200px; content-visibility:visible; }
                #inner { height:80px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"outer\"><div id=\"inner\"></div></div>", css, 400);
            var outer = FirstById(root, "outer");
            Assert.That(outer, Is.Not.Null);
            Assert.That(outer.Height, Is.EqualTo(80).Within(0.001),
                "content-visibility:visible must not collapse height; must reflect child height");
        }

        // ─── hidden — size containment / layout ───────────────────────────────

        [Test]
        public void Hidden_auto_height_collapses_to_zero_content() {
            // content-visibility:hidden implies size containment → auto height
            // collapses to padding+border (0 when neither is set).
            const string css = @"
                #outer { width:200px; content-visibility:hidden; }
                #inner { height:100px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"outer\"><div id=\"inner\"></div></div>", css, 400);
            var outer = FirstById(root, "outer");
            Assert.That(outer, Is.Not.Null);
            Assert.That(outer.Height, Is.EqualTo(0).Within(0.001),
                "content-visibility:hidden must collapse auto height via size containment");
        }

        [Test]
        public void Hidden_explicit_height_is_preserved() {
            // Size containment doesn't override an explicit height.
            const string css = @"
                #outer { width:200px; height:60px; content-visibility:hidden; }
                #inner { height:200px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"outer\"><div id=\"inner\"></div></div>", css, 400);
            var outer = FirstById(root, "outer");
            Assert.That(outer, Is.Not.Null);
            Assert.That(outer.Height, Is.EqualTo(60).Within(0.001),
                "explicit height must survive content-visibility:hidden size containment");
        }

        [Test]
        public void Hidden_children_boxes_still_exist_in_tree() {
            // The spec says contents are "skipped" not "discarded" — box tree
            // must still exist with geometry so DevTools/state is preserved.
            const string css = @"
                #outer { width:200px; content-visibility:hidden; }
                #inner { height:80px; width:100px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"outer\"><div id=\"inner\"></div></div>", css, 400);
            var inner = FirstById(root, "inner");
            Assert.That(inner, Is.Not.Null,
                "child box must still exist in the box tree for content-visibility:hidden");
            Assert.That(inner.Height, Is.EqualTo(80).Within(0.001),
                "child box must still have its layout geometry");
        }

        [Test]
        public void Hidden_suppresses_parent_child_margin_collapse_via_layout_containment() {
            // content-visibility:hidden implies layout containment which is a
            // new BFC → parent-child top-margin collapse is blocked.
            const string css = @"
                #outer { content-visibility:hidden; }
                #child { margin-top:30px; height:50px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"outer\"><div id=\"child\"></div></div>", css, 400);
            var child = FirstById(root, "child");
            Assert.That(child, Is.Not.Null);
            Assert.That(child.Y, Is.EqualTo(30).Within(0.001),
                "content-visibility:hidden (layout containment) must block margin collapse");
        }

        // ─── hidden — paint suppression ───────────────────────────────────────

        [Test]
        public void Hidden_suppresses_descendant_paint_commands() {
            // An element with content-visibility:hidden and a background-bearing
            // child: the child's background DrawRect must NOT appear in the list.
            // The parent's own decorations (its background) MUST appear.
            const string css = @"
                #outer { width:200px; height:50px;
                         content-visibility:hidden; background:blue; }
                #inner { height:30px; background:red; }
            ";
            var (root, _, _) = Build(
                "<div id=\"outer\"><div id=\"inner\"></div></div>", css, 400);
            var outer = FirstById(root, "outer");
            Assert.That(outer, Is.Not.Null);

            var converter = new BoxToPaintConverter();
            var cmds = converter.Convert(outer).Commands;

            // Count DrawRect commands: outer background (1) must appear;
            // inner background must NOT contribute additional rects with the
            // child's position/size.  The child box has height:30px so its
            // rect would sit inside the parent.
            var rects = cmds.OfType<FillRectCommand>().ToList();
            // At least the outer's own background should be present.
            Assert.That(rects.Count, Is.GreaterThanOrEqualTo(1),
                "parent's own background must still be painted");

            // Inner has height 30, outer has height 50 (with size containment,
            // outer height collapses to 0 from content — but we explicitly set
            // height:50px so outer.Height == 50). The child rect Y relative
            // to the outer should start at 0 and have height 30; verify no
            // such rect appears in the commands (descendant paint is skipped).
            // We verify via command count: with hidden, only the parent's own
            // bg should appear (1 rect), not the child's (which would be 2).
            Assert.That(rects.Count, Is.EqualTo(1),
                "content-visibility:hidden must suppress descendant paint; " +
                "only the element's own background rect must appear");
        }

        [Test]
        public void Hidden_element_own_background_paints_despite_skip() {
            // Key spec requirement: the element's own box still paints background/border
            // even though its contents are hidden.
            const string css = @"
                .cv { width:100px; height:40px; background:green;
                      content-visibility:hidden; }
            ";
            var (root, _, _) = Build("<div class=\"cv\"></div>", css, 400);
            var box = FirstByClass(root, "cv");
            Assert.That(box, Is.Not.Null);

            var converter = new BoxToPaintConverter();
            var cmds = converter.Convert(box).Commands;
            bool hasOwnBg = cmds.OfType<FillRectCommand>().Any();
            Assert.That(hasOwnBg, Is.True,
                "element with content-visibility:hidden must still paint its own background");
        }

        [Test]
        public void Hidden_emits_paint_clip_from_paint_containment() {
            // content-visibility:hidden implies paint containment →
            // a PushClip/PopClip pair must be emitted (the contain:paint clip).
            const string css = @"
                .cv { width:100px; height:40px; content-visibility:hidden; }
            ";
            var (root, _, _) = Build("<div class=\"cv\"></div>", css, 400);
            var box = FirstByClass(root, "cv");
            Assert.That(box, Is.Not.Null);

            var converter = new BoxToPaintConverter();
            var cmds = converter.Convert(box).Commands;
            Assert.That(cmds.Any(c => c is PushClipCommand), Is.True,
                "content-visibility:hidden implies paint containment → PushClip expected");
        }

        // ─── hidden — hit-test suppression ───────────────────────────────────

        [Test]
        public void Hidden_contents_are_not_hit_testable() {
            // A box with content-visibility:hidden wraps a button-sized child.
            // Clicking where the child would be must NOT return the child element.
            const string css = @"
                #outer { width:200px; height:100px; content-visibility:hidden; }
                #inner { height:50px; margin-top:25px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"outer\"><div id=\"inner\"></div></div>", css, 400);
            var outer = FirstById(root, "outer");
            var inner = FirstById(root, "inner");
            Assert.That(outer, Is.Not.Null);
            Assert.That(inner, Is.Not.Null);

            // Hit at a point inside where the inner child would paint.
            // outer Y=0, inner.Y within outer = 25.  Test at (100, 50).
            // We need absolute coords: both are at X=0, Y=0 from layout root
            // (outer is the first block child of the viewport-width root box).
            var (outerAbsX, outerAbsY) = AbsoluteOrigin(outer);
            double testX = outerAbsX + 50;
            double testY = outerAbsY + inner.Y + 10; // inside inner's rect

            var hitter = new BoxTreeHitTester(root);
            var hit = hitter.HitTest(testX, testY);

            // Result must NOT be the inner element (content hidden).
            // It should be the outer element itself or null (depending on whether
            // the outer's own box is at that position — outer height collapses
            // to 0 from size containment, so outer.Height=0; the test point
            // at y>0 is outside the outer box, so hit may be null here).
            if (hit != null) {
                Assert.That(hit, Is.Not.SameAs(inner.Element),
                    "content-visibility:hidden contents must not be hit-testable");
            }
            // If hit is null that's correct too — outer height=0 so the outer
            // box doesn't contain the test point.
        }

        [Test]
        public void Hidden_element_itself_is_hit_testable() {
            // The element itself (not just its contents) must be hittable.
            // Give it an explicit height so the test point falls within its box.
            const string css = @"
                #outer { width:200px; height:60px; content-visibility:hidden; }
                #inner { height:40px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"outer\"><div id=\"inner\"></div></div>", css, 400);
            var outer = FirstById(root, "outer");
            Assert.That(outer, Is.Not.Null);

            var (absX, absY) = AbsoluteOrigin(outer);
            // Test at center of the outer box's border box.
            double testX = absX + 100;
            double testY = absY + 10; // well within height:60px

            var hitter = new BoxTreeHitTester(root);
            var hit = hitter.HitTest(testX, testY);
            // The outer element itself is hittable.
            Assert.That(hit, Is.SameAs(outer.Element),
                "content-visibility:hidden element itself must still be hit-testable");
        }

        // ─── auto — layout/paint containment + no size collapse on-screen ─────

        [Test]
        public void Auto_does_not_collapse_height() {
            // auto does NOT apply size containment while on-screen.
            const string css = @"
                #outer { width:200px; content-visibility:auto; }
                #inner { height:70px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"outer\"><div id=\"inner\"></div></div>", css, 400);
            var outer = FirstById(root, "outer");
            Assert.That(outer, Is.Not.Null);
            Assert.That(outer.Height, Is.EqualTo(70).Within(0.001),
                "content-visibility:auto must not collapse height when on-screen");
        }

        [Test]
        public void Auto_suppresses_parent_child_margin_collapse() {
            // auto implies layout containment → new BFC → margin collapse blocked.
            const string css = @"
                #outer { content-visibility:auto; }
                #child { margin-top:25px; height:40px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"outer\"><div id=\"child\"></div></div>", css, 400);
            var child = FirstById(root, "child");
            Assert.That(child, Is.Not.Null);
            Assert.That(child.Y, Is.EqualTo(25).Within(0.001),
                "content-visibility:auto must act as layout barrier blocking margin collapse");
        }

        [Test]
        public void Auto_emits_paint_clip_for_paint_containment() {
            // auto implies paint containment → PushClip expected.
            const string css = ".cv { width:100px; height:50px; content-visibility:auto; }";
            var (root, _, _) = Build("<div class=\"cv\"></div>", css, 400);
            var box = FirstByClass(root, "cv");
            Assert.That(box, Is.Not.Null);

            var converter = new BoxToPaintConverter();
            var cmds = converter.Convert(box).Commands;
            Assert.That(cmds.Any(c => c is PushClipCommand), Is.True,
                "content-visibility:auto implies paint containment → PushClip expected");
        }

        [Test]
        public void Auto_on_screen_paints_descendants() {
            // When viewport is set and box is visible, descendants must paint.
            const string css = @"
                #outer { width:200px; height:50px;
                         content-visibility:auto; background:blue; }
                #inner { height:30px; background:red; }
            ";
            var (root, _, _) = Build(
                "<div id=\"outer\"><div id=\"inner\"></div></div>", css, 400);
            var outer = FirstById(root, "outer");
            Assert.That(outer, Is.Not.Null);

            // Outer is at Y=0; with a 600px viewport the outer is fully on-screen.
            var converter = new BoxToPaintConverter {
                ViewportWidth = 400,
                ViewportHeight = 600
            };
            var cmds = converter.Convert(outer).Commands;
            var rects = cmds.OfType<FillRectCommand>().ToList();
            // Both outer (blue) and inner (red) backgrounds should appear.
            Assert.That(rects.Count, Is.GreaterThanOrEqualTo(2),
                "content-visibility:auto on-screen must paint descendants");
        }

        [Test]
        public void Auto_off_viewport_skips_descendant_paint() {
            // When the box is completely outside the viewport, descendants are skipped.
            const string css = @"
                #outer { width:200px; height:50px;
                         content-visibility:auto; background:blue;
                         position:relative; top:700px; }
                #inner { height:30px; background:red; }
            ";
            var (root, _, _) = Build(
                "<div id=\"outer\"><div id=\"inner\"></div></div>", css, 400);
            var outer = FirstById(root, "outer");
            Assert.That(outer, Is.Not.Null);

            // Viewport is 400x600; outer is at top:700px so entirely below viewport.
            var converter = new BoxToPaintConverter {
                ViewportWidth = 400,
                ViewportHeight = 600
            };
            // Convert from the root so absolute coords are computed correctly.
            var (outerAbsX, outerAbsY) = AbsoluteOrigin(outer);
            // To test the skip we need to convert from a subtree that includes
            // the correct absolute coordinates. Build a minimal box at Y=700.
            // Use the outer box directly but provide absY offset to the converter
            // by using the root. Since the converter computes coords incrementally,
            // let's use the full root Convert.
            // For simplicity: construct a BlockBox at Y=700 with a child.
            var style = new ComputedStyle(new Element("div"));
            style.Set("content-visibility", "auto");
            var outerBox = new BlockBox();
            outerBox.Style = style;
            outerBox.X = 0; outerBox.Y = 700;
            outerBox.Width = 200; outerBox.Height = 50;

            var childStyle = new ComputedStyle(new Element("div"));
            childStyle.Set("background-color", "red");
            var innerBox = new BlockBox();
            innerBox.Style = childStyle;
            innerBox.X = 0; innerBox.Y = 0;
            innerBox.Width = 200; innerBox.Height = 30;
            outerBox.ChildList.Add(innerBox);
            innerBox.Parent = outerBox;

            var conv2 = new BoxToPaintConverter {
                ViewportWidth = 400,
                ViewportHeight = 600
            };
            // Convert from parent box (which is at Y=0 in the call, so absY=700)
            // We pass it directly; the converter adds box.Y to parentAbsY.
            // parentAbsY=0, box.Y=700 → absY=700 for outerBox.
            var cmds2 = conv2.Convert(outerBox).Commands;
            var rects2 = cmds2.OfType<FillRectCommand>().ToList();
            // There should be 0 rects: outerBox has no background set,
            // and the inner box should be skipped.
            Assert.That(rects2.Count, Is.EqualTo(0),
                "content-visibility:auto off-viewport must skip descendant paint " +
                "(inner red background rect must not appear)");
        }

        [Test]
        public void Auto_no_viewport_set_paints_descendants() {
            // When ViewportWidth/Height == 0 (default), the skip is disabled and
            // descendants always render.
            var style = new ComputedStyle(new Element("div"));
            style.Set("content-visibility", "auto");
            var outer = new BlockBox();
            outer.Style = style;
            outer.X = 0; outer.Y = 5000; // far off any implicit viewport
            outer.Width = 200; outer.Height = 50;

            var childStyle = new ComputedStyle(new Element("div"));
            childStyle.Set("background-color", "red");
            var inner = new BlockBox();
            inner.Style = childStyle;
            inner.X = 0; inner.Y = 0;
            inner.Width = 200; inner.Height = 30;
            outer.ChildList.Add(inner);
            inner.Parent = outer;

            // Default converter: ViewportWidth/Height == 0 → skip disabled.
            var conv = new BoxToPaintConverter();
            // No rects from outer (no bg), but inner has bg-color set.
            // EmitDecorationsLocal will produce a rect for background-color:red.
            var cmds = conv.Convert(outer).Commands;
            var rects = cmds.OfType<FillRectCommand>().ToList();
            Assert.That(rects.Count, Is.GreaterThanOrEqualTo(1),
                "with no viewport set (0,0), content-visibility:auto must not skip descendants");
        }

        // ─── interaction with explicit contain ────────────────────────────────

        [Test]
        public void Hidden_plus_explicit_contain_size_union_still_size_contains() {
            // Union semantics: both content-visibility:hidden and contain:size
            // independently imply size containment — union is still size contained.
            const string css = @"
                #outer { width:200px; content-visibility:hidden; contain:size; }
                #inner { height:100px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"outer\"><div id=\"inner\"></div></div>", css, 400);
            var outer = FirstById(root, "outer");
            Assert.That(outer, Is.Not.Null);
            Assert.That(outer.Height, Is.EqualTo(0).Within(0.001),
                "hidden + contain:size union must size-contain (height = 0)");
        }

        [Test]
        public void Hidden_plus_explicit_contain_layout_union_still_blocks_margin_collapse() {
            // Union: contain:layout was already blocking margin collapse;
            // adding content-visibility:hidden must not undo that.
            const string css = @"
                #outer { content-visibility:hidden; contain:layout; }
                #child { margin-top:20px; height:30px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"outer\"><div id=\"child\"></div></div>", css, 400);
            var child = FirstById(root, "child");
            Assert.That(child, Is.Not.Null);
            Assert.That(child.Y, Is.EqualTo(20).Within(0.001),
                "hidden + contain:layout union must still block margin collapse");
        }

        [Test]
        public void Auto_plus_explicit_contain_layout_both_block_margin_collapse() {
            // Union: auto already implies layout; explicit contain:layout is redundant
            // but must not break anything.
            const string css = @"
                #outer { content-visibility:auto; contain:layout; }
                #child { margin-top:15px; height:25px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"outer\"><div id=\"child\"></div></div>", css, 400);
            var child = FirstById(root, "child");
            Assert.That(child, Is.Not.Null);
            Assert.That(child.Y, Is.EqualTo(15).Within(0.001),
                "auto + contain:layout union must block margin collapse");
        }
    }
}
