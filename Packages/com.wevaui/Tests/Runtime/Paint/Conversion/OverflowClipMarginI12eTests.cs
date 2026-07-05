using System.Linq;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Parsing;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Paint.Conversion {
    // CSS Overflow L4 §6 I12e/I12f coverage — gaps not covered by the existing
    // OverflowResolverTests.cs and BoxToPaintConverterTests.cs:
    //
    //  1. <visual-box> keyword on per-side PHYSICAL LONGHANDS (not just shorthand).
    //     The OverflowResolver.ResolveClipMarginVisualBox* path that reads a side
    //     longhand's own value (e.g. overflow-clip-margin-top: content-box 4px) was
    //     implemented in OverflowResolver.ResolveClipMarginVisualBoxSide but had
    //     no test coverage on the direct-longhand branch.
    //
    //  2. Full BoxToPaintConverter clip-rect geometry when per-side longhands carry
    //     the visual-box keyword (shorthand-based geometry was already tested).
    //
    //  3. Mixed per-side visual-boxes (top=content-box, left=border-box) inflate
    //     each axis from the correct reference edge.
    //
    //  4. Logical longhand cascade + paint: <visual-box> travels through the
    //     CascadeEngine.Logical alias to the physical side; the inflated clip rect
    //     respects the resolved box for that physical side.
    public class OverflowClipMarginI12eTests {
        static ComputedStyle Style() => new ComputedStyle(new Element("div"));
        static LengthContext Ctx() => LengthContext.Default;

        static BlockBox BlockWithStyle(double x, double y, double w, double h, ComputedStyle style) {
            var bb = new BlockBox();
            bb.Style = style;
            bb.X = x; bb.Y = y; bb.Width = w; bb.Height = h;
            return bb;
        }

        // ── OverflowResolver: per-side longhand with <visual-box> ─────────────

        [Test]
        public void Per_side_longhand_top_content_box_keyword_resolves_visual_box() {
            // overflow-clip-margin-top: content-box 4px — the longhand carries the
            // <visual-box> keyword. ResolveClipMarginVisualBoxTop must read it from
            // the longhand's own CssValueList, not fall back to the shorthand.
            var s = Style();
            s.Set("overflow", "clip");
            s.Set("overflow-clip-margin-top", "content-box 4px");
            Assert.That(OverflowResolver.ResolveClipMarginVisualBoxTop(s),
                Is.EqualTo(OverflowClipMarginBox.ContentBox),
                "visual-box keyword on per-side top longhand must resolve to ContentBox");
            Assert.That(OverflowResolver.ResolveClipMarginTop(s, Ctx()),
                Is.EqualTo(4.0).Within(1e-6),
                "length part of content-box 4px must resolve to 4px");
        }

        [Test]
        public void Per_side_longhand_right_border_box_keyword_resolves_visual_box() {
            var s = Style();
            s.Set("overflow", "clip");
            s.Set("overflow-clip-margin-right", "border-box 6px");
            Assert.That(OverflowResolver.ResolveClipMarginVisualBoxRight(s),
                Is.EqualTo(OverflowClipMarginBox.BorderBox),
                "visual-box keyword on per-side right longhand must resolve to BorderBox");
            Assert.That(OverflowResolver.ResolveClipMarginRight(s, Ctx()),
                Is.EqualTo(6.0).Within(1e-6));
        }

        [Test]
        public void Per_side_longhand_bottom_padding_box_keyword_resolves_visual_box() {
            var s = Style();
            s.Set("overflow", "clip");
            s.Set("overflow-clip-margin-bottom", "padding-box 10px");
            Assert.That(OverflowResolver.ResolveClipMarginVisualBoxBottom(s),
                Is.EqualTo(OverflowClipMarginBox.PaddingBox),
                "explicit padding-box keyword on per-side bottom longhand must resolve to PaddingBox");
            Assert.That(OverflowResolver.ResolveClipMarginBottom(s, Ctx()),
                Is.EqualTo(10.0).Within(1e-6));
        }

        [Test]
        public void Per_side_longhand_left_content_box_overrides_shorthand_box() {
            // When the left longhand has content-box but the shorthand has border-box,
            // the longhand's own visual-box must win for the left side.
            var s = Style();
            s.Set("overflow", "clip");
            s.Set("overflow-clip-margin", "border-box 8px");
            s.Set("overflow-clip-margin-left", "content-box 5px");
            Assert.That(OverflowResolver.ResolveClipMarginVisualBoxLeft(s),
                Is.EqualTo(OverflowClipMarginBox.ContentBox),
                "per-side left longhand visual-box must override the shorthand visual-box");
            Assert.That(OverflowResolver.ResolveClipMarginLeft(s, Ctx()),
                Is.EqualTo(5.0).Within(1e-6),
                "per-side left length must come from the longhand, not the shorthand");
            // Other sides still fall through to the shorthand (border-box 8px).
            Assert.That(OverflowResolver.ResolveClipMarginVisualBoxTop(s),
                Is.EqualTo(OverflowClipMarginBox.BorderBox),
                "top side falls through to shorthand visual-box when no longhand is set");
            Assert.That(OverflowResolver.ResolveClipMarginTop(s, Ctx()),
                Is.EqualTo(8.0).Within(1e-6));
        }

        // ── BoxToPaintConverter: per-side longhand with <visual-box> ─────────

        [Test]
        public void Per_side_longhand_content_box_inflates_clip_rect_correctly() {
            // Box: 200x120, border=2, padding=10.
            // border-box = (0,0,200,120); padding-box = (2,2,196,116);
            // content-box = (12,12,176,96).
            //
            // overflow-clip-margin-top: content-box 5px:
            //   base = border + padding = 2+10 = 12 on top
            //   rectTop = 12 - 5 = 7
            // overflow-clip-margin-right: border-box 3px:
            //   base = 0
            //   rectRight = 200 - 0 + 3 = 203
            // overflow-clip-margin-bottom: padding-box 4px:
            //   base = border = 2 on bottom
            //   rectBottom = 120 - 2 + 4 = 122
            // overflow-clip-margin-left: 0px (no visual-box, just default 0).
            //   base = padding-box default = border = 2
            //   length = 0 → left side not inflated; rectLeft = 2 - 0 = 2
            //
            // But wait — the gate `if (top>0||right>0||bottom>0||left>0)` is true.
            // With top=5, right=3, bottom=4, left=0:
            //   rectLeft = baseLeft - left = 2 - 0 = 2
            //   rectTop  = baseTop - top   = 12 - 5 = 7
            //   rectRight  = W - baseRight + right   = 200 - 0 + 3 = 203
            //   rectBottom = H - baseBottom + bottom = 120 - 2 + 4 = 122
            // Width  = rectRight - rectLeft = 203 - 2 = 201
            // Height = rectBottom - rectTop = 122 - 7 = 115
            var s = Style();
            s.Set("overflow", "clip");
            s.Set("overflow-clip-margin-top",    "content-box 5px");
            s.Set("overflow-clip-margin-right",  "border-box 3px");
            s.Set("overflow-clip-margin-bottom", "padding-box 4px");
            // overflow-clip-margin-left intentionally omitted (defaults to 0px).
            var box = BlockWithStyle(0, 0, 200, 120, s);
            box.BorderTop = box.BorderRight = box.BorderBottom = box.BorderLeft = 2;
            box.PaddingTop = box.PaddingRight = box.PaddingBottom = box.PaddingLeft = 10;

            var clips = new BoxToPaintConverter().Convert(box).Commands
                .OfType<PushClipCommand>().ToList();
            Assert.That(clips.Count, Is.EqualTo(1));
            Assert.That(clips[0].Bounds.X,      Is.EqualTo(2).Within(1e-6));
            Assert.That(clips[0].Bounds.Y,      Is.EqualTo(7).Within(1e-6));
            Assert.That(clips[0].Bounds.Width,  Is.EqualTo(201).Within(1e-6));
            Assert.That(clips[0].Bounds.Height, Is.EqualTo(115).Within(1e-6));
        }

        [Test]
        public void Mixed_per_side_visual_boxes_inflate_each_axis_independently() {
            // All four sides use different visual-boxes with equal length (8px).
            // Box: 200x200, border=4, padding=12.
            // top=content-box 8px:   base = 4+12=16; rectTop   = 16-8 = 8
            // right=border-box 8px:  base = 0;       rectRight = 200-0+8 = 208
            // bottom=padding-box 8px: base=4;         rectBottom= 200-4+8 = 204
            // left=content-box 8px:  base = 4+12=16; rectLeft  = 16-8 = 8
            // Width  = 208 - 8 = 200
            // Height = 204 - 8 = 196
            var s = Style();
            s.Set("overflow", "clip");
            s.Set("overflow-clip-margin-top",    "content-box 8px");
            s.Set("overflow-clip-margin-right",  "border-box 8px");
            s.Set("overflow-clip-margin-bottom", "padding-box 8px");
            s.Set("overflow-clip-margin-left",   "content-box 8px");
            var box = BlockWithStyle(0, 0, 200, 200, s);
            box.BorderTop = box.BorderRight = box.BorderBottom = box.BorderLeft = 4;
            box.PaddingTop = box.PaddingRight = box.PaddingBottom = box.PaddingLeft = 12;

            var clips = new BoxToPaintConverter().Convert(box).Commands
                .OfType<PushClipCommand>().ToList();
            Assert.That(clips.Count, Is.EqualTo(1));
            Assert.That(clips[0].Bounds.X,      Is.EqualTo(8).Within(1e-6),
                "left: content-box (base=16) minus 8 = 8");
            Assert.That(clips[0].Bounds.Y,      Is.EqualTo(8).Within(1e-6),
                "top: content-box (base=16) minus 8 = 8");
            Assert.That(clips[0].Bounds.Width,  Is.EqualTo(200).Within(1e-6),
                "right=border-box gives edge at 208; left=8 → width=200");
            Assert.That(clips[0].Bounds.Height, Is.EqualTo(196).Within(1e-6),
                "bottom=padding-box gives edge at 204; top=8 → height=196");
        }

        // ── Cascade path: <visual-box> keyword round-trip ──────────────────

        [Test]
        public void Visual_box_keyword_on_logical_longhand_travels_through_cascade_to_clip_rect() {
            // overflow-clip-margin-inline-start: content-box 8px in LTR
            // → aliases to overflow-clip-margin-left: content-box 8px at cascade time.
            // The OverflowResolver reads the visual-box from the physical left slot.
            //
            // Box geometry (box-sizing: content-box default):
            //   CSS width:100px → content=100; border=2; padding=8
            //   total box width  = 100 + 2*8 + 2*2 = 120
            //   total box height = 100 + 2*8 + 2*2 = 120
            //   box at absolute (0,0).
            //
            // Because left > 0, we enter the inflated path on all four sides:
            //   baseLeft = ContentBox → border+padding = 2+8 = 10
            //   rectLeft = 10 - 8 = 2
            //   baseTop = PaddingBox (default, no left-longhand visual-box → falls through
            //             to shorthand which has no visual-box → PaddingBox) = border = 2
            //   rectTop = 2 - 0 = 2
            //   baseRight = PaddingBox = 2; rectRight = 120-2+0 = 118
            //   baseBottom = PaddingBox = 2; rectBottom = 120-2+0 = 118
            //   Width  = 118 - 2 = 116
            //   Height = 118 - 2 = 116
            const string fix = "position: absolute; left: 0; top: 0; ";
            var (root, _, _) = Build(
                "<div id=\"box\"></div>",
                "#box { " + fix + "overflow: clip; width: 100px; height: 100px; " +
                "border: 2px solid black; padding: 8px; " +
                "writing-mode: horizontal-tb; direction: ltr; " +
                "overflow-clip-margin-inline-start: content-box 8px; }",
                viewportWidth: 400);

            var clips = new BoxToPaintConverter().Convert(root).Commands
                .OfType<PushClipCommand>().ToList();
            Assert.That(clips.Count, Is.EqualTo(1));
            // content-box left base = border+padding = 10; length=8 → rectLeft=2
            Assert.That(clips[0].Bounds.X, Is.EqualTo(2).Within(1e-6),
                "inline-start with content-box 8px in LTR: left base=10, minus 8px = 2");
            // Uninflated sides enter inflated path with PaddingBox default → base=border=2, top-length=0 → 2
            Assert.That(clips[0].Bounds.Y,      Is.EqualTo(2).Within(1e-6),
                "top side is uninflated but uses PaddingBox default → base=2, length=0 → Y=2");
            Assert.That(clips[0].Bounds.Width,  Is.EqualTo(116).Within(1e-6));
            Assert.That(clips[0].Bounds.Height, Is.EqualTo(116).Within(1e-6));
        }
    }
}
