// CSS Containment L2 — NUnit coverage for `contain` layout/paint/size behaviour.
//
// Covers:
//   § contain:paint  — clips paint to padding box, establishes abs/fixed CB,
//                      establishes stacking context (pre-existing).
//   § contain:layout — independent formatting context (suppresses parent-child
//                      margin collapse), establishes abs/fixed CB.
//   § contain:size   — auto-height collapses to padding+border; explicit height
//                      kept; min/max still respected; children still lay out.
//   § contain:strict — shorthand = layout + paint + size + style.
//   § contain:content — shorthand = layout + paint + style (no size).
//   § contain:none   — regression: no containment applied.
//   § interact        — contain:paint + overflow:hidden do not double-clip.
//
// ContainmentResolver unit tests live at the bottom of this file.

using System.Linq;
using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Layout.Containment;
using Weva.Layout.Positioning;
using Weva.Paint;
using Weva.Paint.Conversion;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    public class ContainmentTests {

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

        // ─── ContainmentResolver unit tests ───────────────────────────────────

        [Test]
        public void ContainmentResolver_HasLayout_true_for_layout_keyword() {
            var s = new Weva.Css.Cascade.ComputedStyle(new Weva.Dom.Element("div"));
            s.Set("contain", "layout");
            Assert.That(ContainmentResolver.HasLayout(s), Is.True);
        }

        [Test]
        public void ContainmentResolver_HasPaint_true_for_paint_keyword() {
            var s = new Weva.Css.Cascade.ComputedStyle(new Weva.Dom.Element("div"));
            s.Set("contain", "paint");
            Assert.That(ContainmentResolver.HasPaint(s), Is.True);
        }

        [Test]
        public void ContainmentResolver_strict_expands_to_layout_paint_size() {
            var s = new Weva.Css.Cascade.ComputedStyle(new Weva.Dom.Element("div"));
            s.Set("contain", "strict");
            Assert.That(ContainmentResolver.HasLayout(s), Is.True);
            Assert.That(ContainmentResolver.HasPaint(s), Is.True);
            Assert.That(ContainmentResolver.HasSize(s), Is.True);
        }

        [Test]
        public void ContainmentResolver_content_expands_to_layout_paint_no_size() {
            var s = new Weva.Css.Cascade.ComputedStyle(new Weva.Dom.Element("div"));
            s.Set("contain", "content");
            Assert.That(ContainmentResolver.HasLayout(s), Is.True);
            Assert.That(ContainmentResolver.HasPaint(s), Is.True);
            Assert.That(ContainmentResolver.HasSize(s), Is.False);
        }

        [Test]
        public void ContainmentResolver_none_means_no_containment() {
            var s = new Weva.Css.Cascade.ComputedStyle(new Weva.Dom.Element("div"));
            s.Set("contain", "none");
            Assert.That(ContainmentResolver.HasLayout(s), Is.False);
            Assert.That(ContainmentResolver.HasPaint(s), Is.False);
            Assert.That(ContainmentResolver.HasSize(s), Is.False);
            Assert.That(ContainmentResolver.HasAny(s), Is.False);
        }

        [Test]
        public void ContainmentResolver_null_style_returns_false_for_all() {
            Assert.That(ContainmentResolver.HasLayout(null), Is.False);
            Assert.That(ContainmentResolver.HasPaint(null), Is.False);
            Assert.That(ContainmentResolver.HasSize(null), Is.False);
            Assert.That(ContainmentResolver.HasInlineSize(null), Is.False);
            Assert.That(ContainmentResolver.HasAny(null), Is.False);
        }

        [Test]
        public void ContainmentResolver_explicit_size_not_implied_by_content() {
            // `content` shorthand does NOT include size.
            var s = new Weva.Css.Cascade.ComputedStyle(new Weva.Dom.Element("div"));
            s.Set("contain", "content");
            Assert.That(ContainmentResolver.HasSize(s), Is.False);
        }

        [Test]
        public void ContainmentResolver_inline_size_implied_by_size_and_strict() {
            // CSS Containment L2 §3.3: `contain: size` covers BOTH axes (inline +
            // block), so it implies inline-size containment.  `strict` expands to
            // layout+paint+size+style, which includes size, so it also implies
            // inline-size.  `content` = layout+paint+style has NO size, so it does
            // NOT imply inline-size.  An explicit `inline-size` token always applies.
            var sStrict = new Weva.Css.Cascade.ComputedStyle(new Weva.Dom.Element("div"));
            sStrict.Set("contain", "strict");
            Assert.That(ContainmentResolver.HasInlineSize(sStrict), Is.True,
                "strict implies size which implies inline-size");

            var sSize = new Weva.Css.Cascade.ComputedStyle(new Weva.Dom.Element("div"));
            sSize.Set("contain", "size");
            Assert.That(ContainmentResolver.HasInlineSize(sSize), Is.True,
                "contain:size covers both axes, so HasInlineSize must be true");

            var sContent = new Weva.Css.Cascade.ComputedStyle(new Weva.Dom.Element("div"));
            sContent.Set("contain", "content");
            Assert.That(ContainmentResolver.HasInlineSize(sContent), Is.False,
                "contain:content does not include size, so no inline-size containment");

            var sExplicit = new Weva.Css.Cascade.ComputedStyle(new Weva.Dom.Element("div"));
            sExplicit.Set("contain", "inline-size");
            Assert.That(ContainmentResolver.HasInlineSize(sExplicit), Is.True,
                "explicit inline-size token always applies");
        }

        // ─── contain:paint — paint clipping ───────────────────────────────────

        [Test]
        public void Contain_paint_emits_PushClip_PopClip() {
            // A box with contain:paint and overflow:visible should emit a
            // PushClip/PopClip pair clipping to the padding box.
            const string css = ".box { width:100px; height:50px; contain:paint; }";
            var (root, _, _) = Build("<div class=\"box\"></div>", css);
            var box = FirstByClass(root, "box");
            Assert.That(box, Is.Not.Null, "box must be found");

            var converter = new BoxToPaintConverter();
            var cmds = converter.Convert(box).Commands;
            bool hasPushClip = cmds.Any(c => c is PushClipCommand);
            bool hasPopClip = cmds.Any(c => c is PopClipCommand);
            Assert.That(hasPushClip, Is.True, "contain:paint must push a clip");
            Assert.That(hasPopClip, Is.True, "contain:paint must pop the clip");
        }

        [Test]
        public void Contain_paint_clip_is_at_padding_box_not_content_box() {
            // The clip rect for contain:paint is the PADDING box (inside the
            // border), not the content box. We use a direct ComputedStyle with
            // border explicitly set on the box object to avoid shorthand
            // expansion issues in the headless runner.
            var s = new Weva.Css.Cascade.ComputedStyle(new Weva.Dom.Element("div"));
            s.Set("contain", "paint");
            var box = new BlockBox();
            box.Style = s;
            box.X = 0; box.Y = 0;
            box.Width = 100; box.Height = 60;
            // Simulate a 5px border on all sides.
            box.BorderLeft = 5; box.BorderRight = 5;
            box.BorderTop = 5; box.BorderBottom = 5;

            var converter = new BoxToPaintConverter();
            var cmds = converter.Convert(box).Commands;
            var pushClips = cmds.OfType<PushClipCommand>().ToList();
            Assert.That(pushClips.Count, Is.GreaterThanOrEqualTo(1),
                "expect at least one PushClip from contain:paint");
            // The clip rect produced by contain:paint sits at (BorderLeft, BorderTop)
            // in box-local coords; with 5px border all sides that is X=5, Y=5.
            var containClip = pushClips[0];
            Assert.That(containClip.Bounds.X, Is.EqualTo(5).Within(0.001),
                "clip X must be BorderLeft");
            Assert.That(containClip.Bounds.Y, Is.EqualTo(5).Within(0.001),
                "clip Y must be BorderTop");
            // Width = 100 - 5 - 5 = 90; Height = 60 - 5 - 5 = 50.
            Assert.That(containClip.Bounds.Width, Is.EqualTo(90).Within(0.001),
                "clip width = total width minus both borders");
            Assert.That(containClip.Bounds.Height, Is.EqualTo(50).Within(0.001),
                "clip height = total height minus both borders");
        }

        [Test]
        public void Contain_paint_plus_overflow_hidden_emits_only_one_clip() {
            // When both contain:paint and overflow:hidden are set, only ONE clip
            // is needed — the overflow:hidden path already clips to the padding box.
            const string css = ".box { width:100px; height:50px; overflow:hidden; contain:paint; }";
            var (root, _, _) = Build("<div class=\"box\"></div>", css);
            var box = FirstByClass(root, "box");
            Assert.That(box, Is.Not.Null);

            var converter = new BoxToPaintConverter();
            var cmds = converter.Convert(box).Commands;
            var pushClips = cmds.OfType<PushClipCommand>().ToList();
            // Both overflow:hidden and contain:paint clip to the same rect, but we
            // suppress the contain:paint clip when overflow already clips — so
            // exactly 1 PushClip should appear.
            Assert.That(pushClips.Count, Is.EqualTo(1),
                "overflow:hidden already clips; contain:paint must not add a second clip");
        }

        [Test]
        public void Contain_none_emits_no_clip_when_overflow_visible() {
            // Regression: contain:none must not clip.
            const string css = ".box { width:100px; height:50px; contain:none; background:red; }";
            var (root, _, _) = Build("<div class=\"box\"></div>", css);
            var box = FirstByClass(root, "box");
            Assert.That(box, Is.Not.Null);

            var converter = new BoxToPaintConverter();
            var cmds = converter.Convert(box).Commands;
            var pushClips = cmds.OfType<PushClipCommand>().ToList();
            Assert.That(pushClips.Count, Is.EqualTo(0),
                "contain:none with overflow:visible must not emit any clip");
        }

        // ─── contain:paint — abs/fixed containing block ───────────────────────

        [Test]
        public void Contain_paint_establishes_abs_containing_block() {
            // A static element with contain:paint becomes the containing block for
            // its absolutely-positioned children.
            const string css = @"
                .container { width:200px; height:200px; contain:paint; margin-left:50px; }
                .abs { position:absolute; top:10px; left:20px; width:30px; height:30px; }
            ";
            var (root, _, ctx) = Build(
                "<div class=\"container\"><div class=\"abs\"></div></div>", css, 800);
            var container = FirstByClass(root, "container");
            var abs = FirstByClass(root, "abs");
            Assert.That(container, Is.Not.Null);
            Assert.That(abs, Is.Not.Null);

            var (cx, cy) = AbsoluteOrigin(container);
            var (ax, ay) = AbsoluteOrigin(abs);
            // abs top:10 left:20 relative to the contain:paint container.
            // container has no padding/border so padding-box = content area.
            Assert.That(ax - cx, Is.EqualTo(20).Within(1),
                "abs left:20 should be 20px from the contain:paint container's left edge");
            Assert.That(ay - cy, Is.EqualTo(10).Within(1),
                "abs top:10 should be 10px from the contain:paint container's top edge");
        }

        // ─── contain:paint — stacking context ────────────────────────────────

        [Test]
        public void Contain_paint_creates_stacking_context() {
            const string css = ".box { width:100px; height:50px; contain:paint; }";
            var (root, styles, _) = Build("<div class=\"box\"></div>", css);
            var box = FirstByClass(root, "box");
            Assert.That(box, Is.Not.Null);
            Assert.That(box.CreatesStackingContext(), Is.True,
                "contain:paint must create a stacking context");
        }

        [Test]
        public void Contain_layout_creates_stacking_context() {
            const string css = ".box { width:100px; height:50px; contain:layout; }";
            var (root, _, _) = Build("<div class=\"box\"></div>", css);
            var box = FirstByClass(root, "box");
            Assert.That(box, Is.Not.Null);
            Assert.That(box.CreatesStackingContext(), Is.True,
                "contain:layout must create a stacking context");
        }

        // ─── contain:layout — margin collapse suppression ─────────────────────

        [Test]
        public void Contain_layout_suppresses_parent_child_top_margin_collapse() {
            // Without containment, a parent with no padding/border lets the first
            // child's margin-top collapse into the parent's.  With contain:layout
            // the parent establishes an independent formatting context — no collapse.
            const string css = @"
                #outer { margin-top:0; contain:layout; }
                #child { margin-top:30px; height:50px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"outer\"><div id=\"child\"></div></div>", css, 800);
            var outer = FirstById(root, "outer");
            var child = FirstById(root, "child");
            Assert.That(outer, Is.Not.Null);
            Assert.That(child, Is.Not.Null);
            // Child's margin-top should NOT have collapsed into the parent.
            // child.Y within outer should be 30 (the margin), not 0.
            Assert.That(child.Y, Is.EqualTo(30).Within(0.001),
                "contain:layout must block parent-child top margin collapse");
        }

        [Test]
        public void Without_contain_layout_parent_child_margin_collapses() {
            // Control: same structure without contain:layout → margins collapse.
            const string css = @"
                #outer { margin-top:0; }
                #child { margin-top:30px; height:50px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"outer\"><div id=\"child\"></div></div>", css, 800);
            var child = FirstById(root, "child");
            Assert.That(child, Is.Not.Null);
            // Without containment the child's top-margin collapses through the
            // parent so child.Y within the parent is 0.
            Assert.That(child.Y, Is.EqualTo(0).Within(0.001),
                "without contain:layout, parent-child top margin should collapse");
        }

        [Test]
        public void Contain_layout_establishes_abs_containing_block() {
            // A STATIC element with contain:layout becomes the CB for abs children,
            // rather than the abs child resolving to the viewport.
            // We verify this by placing two static containers: one with contain:layout
            // (inner), one without (outer).  The abs child must resolve against inner,
            // not outer or the viewport.
            const string css = @"
                .outer { position:relative; width:500px; height:500px; }
                .inner { width:200px; height:200px; contain:layout; margin-left:50px; margin-top:30px; }
                .abs  { position:absolute; top:0; left:0; width:20px; height:20px; }
            ";
            var (root, _, _) = Build(
                "<div class=\"outer\"><div class=\"inner\"><div class=\"abs\"></div></div></div>",
                css, 800);
            var inner = FirstByClass(root, "inner");
            var abs = FirstByClass(root, "abs");
            Assert.That(inner, Is.Not.Null);
            Assert.That(abs, Is.Not.Null);

            var (ix, iy) = AbsoluteOrigin(inner);
            var (ax, ay) = AbsoluteOrigin(abs);
            // abs top:0 left:0 should resolve to the inner container's padding-box
            // origin (which with no border = inner's own origin).
            Assert.That(ax, Is.EqualTo(ix).Within(1),
                "abs left:0 must align with inner contain:layout container's left edge");
            Assert.That(ay, Is.EqualTo(iy).Within(1),
                "abs top:0 must align with inner contain:layout container's top edge");
        }

        // ─── contain:size — intrinsic sizing ──────────────────────────────────

        [Test]
        public void Contain_size_auto_height_collapses_to_zero_content() {
            // An auto-height element with contain:size must size as if it has no
            // content — height = padding + border (with no padding/border: 0).
            const string css = @"
                #container { width:200px; contain:size; }
                #child { height:100px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"container\"><div id=\"child\"></div></div>", css, 800);
            var container = FirstById(root, "container");
            Assert.That(container, Is.Not.Null);
            // No padding or border: height should be 0.
            Assert.That(container.Height, Is.EqualTo(0).Within(0.001),
                "contain:size with auto height and no padding/border → height 0");
        }

        [Test]
        public void Contain_size_explicit_height_is_preserved() {
            // When an explicit height is given, contain:size doesn't override it.
            const string css = @"
                #container { width:200px; height:80px; contain:size; }
                #child { height:200px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"container\"><div id=\"child\"></div></div>", css, 800);
            var container = FirstById(root, "container");
            Assert.That(container, Is.Not.Null);
            Assert.That(container.Height, Is.EqualTo(80).Within(0.001),
                "explicit height must survive contain:size");
        }

        [Test]
        public void Contain_size_with_padding_height_includes_padding() {
            // Auto height + contain:size + padding → height = paddingTop + paddingBottom.
            const string css = @"
                #container { width:200px; padding:10px; contain:size; }
                #child { height:100px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"container\"><div id=\"child\"></div></div>", css, 800);
            var container = FirstById(root, "container");
            Assert.That(container, Is.Not.Null);
            // 10px padding top + 10px padding bottom = 20px total border-box.
            Assert.That(container.Height, Is.EqualTo(20).Within(0.001),
                "contain:size auto height must equal top+bottom padding when no border");
        }

        [Test]
        public void Contain_size_children_still_lay_out_inside() {
            // Children still participate in layout inside a contain:size box —
            // they just don't affect the box's own size.
            const string css = @"
                #container { width:200px; contain:size; }
                #child { height:80px; width:100px; margin-left:20px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"container\"><div id=\"child\"></div></div>", css, 800);
            var container = FirstById(root, "container");
            var child = FirstById(root, "child");
            Assert.That(container, Is.Not.Null);
            Assert.That(child, Is.Not.Null);
            // Container sized to 0 content height.
            Assert.That(container.Height, Is.EqualTo(0).Within(0.001));
            // Child still has its layout height.
            Assert.That(child.Height, Is.EqualTo(80).Within(0.001),
                "child inside contain:size still lays out with its own height");
        }

        [Test]
        public void Contain_size_min_height_is_respected() {
            // min-height still applies as a floor even with contain:size.
            const string css = @"
                #container { width:200px; min-height:40px; contain:size; }
                #child { height:100px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"container\"><div id=\"child\"></div></div>", css, 800);
            var container = FirstById(root, "container");
            Assert.That(container, Is.Not.Null);
            // Auto height would be 0 from size-containment, but min-height:40 floors it.
            Assert.That(container.Height, Is.EqualTo(40).Within(0.001),
                "min-height must floor the contain:size auto height");
        }

        [Test]
        public void Contain_size_max_height_is_respected_with_explicit_height() {
            // max-height still caps an explicit height that exceeds it.
            const string css = @"
                #container { width:200px; height:150px; max-height:60px; contain:size; }
                #child { height:100px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"container\"><div id=\"child\"></div></div>", css, 800);
            var container = FirstById(root, "container");
            Assert.That(container, Is.Not.Null);
            Assert.That(container.Height, Is.EqualTo(60).Within(0.001),
                "max-height must cap the height under contain:size");
        }

        // ─── contain:strict shorthand ─────────────────────────────────────────

        [Test]
        public void Contain_strict_collapses_auto_height_to_zero() {
            // strict = layout + paint + size + style.
            const string css = @"
                #container { width:200px; contain:strict; }
                #child { height:100px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"container\"><div id=\"child\"></div></div>", css, 800);
            var container = FirstById(root, "container");
            Assert.That(container, Is.Not.Null);
            Assert.That(container.Height, Is.EqualTo(0).Within(0.001),
                "contain:strict must collapse auto height to 0 (size containment)");
        }

        [Test]
        public void Contain_strict_emits_clip_command() {
            // strict includes paint containment → PushClip expected.
            const string css = ".box { width:100px; height:50px; contain:strict; }";
            var (root, _, _) = Build("<div class=\"box\"></div>", css);
            var box = FirstByClass(root, "box");
            Assert.That(box, Is.Not.Null);

            var converter = new BoxToPaintConverter();
            var cmds = converter.Convert(box).Commands;
            var pushClips = cmds.OfType<PushClipCommand>().ToList();
            Assert.That(pushClips.Count, Is.GreaterThanOrEqualTo(1),
                "contain:strict (includes paint) must emit a PushClip");
        }

        [Test]
        public void Contain_strict_suppresses_margin_collapse() {
            // strict includes layout containment → margin collapse suppressed.
            const string css = @"
                #outer { contain:strict; }
                #child { margin-top:30px; height:50px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"outer\"><div id=\"child\"></div></div>", css, 800);
            var child = FirstById(root, "child");
            Assert.That(child, Is.Not.Null);
            Assert.That(child.Y, Is.EqualTo(30).Within(0.001),
                "contain:strict (layout) must block parent-child margin collapse");
        }

        // ─── contain:content shorthand ────────────────────────────────────────

        [Test]
        public void Contain_content_does_not_collapse_auto_height() {
            // content = layout + paint + style — does NOT include size.
            // So auto height should still reflect content.
            const string css = @"
                #container { width:200px; contain:content; }
                #child { height:80px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"container\"><div id=\"child\"></div></div>", css, 800);
            var container = FirstById(root, "container");
            Assert.That(container, Is.Not.Null);
            Assert.That(container.Height, Is.EqualTo(80).Within(0.001),
                "contain:content does NOT include size; auto height follows content");
        }

        [Test]
        public void Contain_content_suppresses_margin_collapse() {
            // content includes layout → margin collapse blocked.
            const string css = @"
                #outer { contain:content; }
                #child { margin-top:25px; height:40px; }
            ";
            var (root, _, _) = Build(
                "<div id=\"outer\"><div id=\"child\"></div></div>", css, 800);
            var child = FirstById(root, "child");
            Assert.That(child, Is.Not.Null);
            Assert.That(child.Y, Is.EqualTo(25).Within(0.001),
                "contain:content (layout) blocks parent-child margin collapse");
        }

        [Test]
        public void Contain_content_emits_clip_command() {
            // content includes paint → PushClip expected.
            const string css = ".box { width:100px; height:50px; contain:content; }";
            var (root, _, _) = Build("<div class=\"box\"></div>", css);
            var box = FirstByClass(root, "box");
            Assert.That(box, Is.Not.Null);

            var converter = new BoxToPaintConverter();
            var cmds = converter.Convert(box).Commands;
            Assert.That(cmds.Any(c => c is PushClipCommand), Is.True,
                "contain:content (includes paint) must emit a PushClip");
        }
    }
}
