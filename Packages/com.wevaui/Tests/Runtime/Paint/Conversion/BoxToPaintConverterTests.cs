using System.Collections.Generic;
using System;
using System.Linq;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Layout.Scrolling;
using Weva.Paint;
using Weva.Paint.Conversion;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Paint.Conversion {
    public class BoxToPaintConverterTests {
        static BlockBox BlockWithStyle(double x, double y, double w, double h, ComputedStyle style) {
            var bb = new BlockBox();
            bb.Style = style;
            bb.X = x; bb.Y = y; bb.Width = w; bb.Height = h;
            return bb;
        }

        static ComputedStyle MakeStyle() {
            return new ComputedStyle(new Element("div"));
        }

        static List<PaintCommand> Commands(BoxToPaintConverter c, Box root) {
            return c.Convert(root).Commands;
        }

        [Test]
        public void Empty_box_with_no_style_emits_no_commands() {
            var root = new BlockBox();
            root.X = 0; root.Y = 0; root.Width = 10; root.Height = 10;
            var c = new BoxToPaintConverter();
            var cmds = Commands(c, root);
            Assert.That(cmds.Count, Is.EqualTo(0));
        }

        [Test]
        public void Solid_background_emits_one_FillRect() {
            var s = MakeStyle();
            s.Set("background-color", "red");
            var box = BlockWithStyle(0, 0, 50, 50, s);
            var cmds = Commands(new BoxToPaintConverter(), box);
            Assert.That(cmds.Count, Is.EqualTo(1));
            Assert.That(cmds[0], Is.InstanceOf<FillRectCommand>());
        }

        [Test]
        public void Background_plus_border_emits_two_commands() {
            var s = MakeStyle();
            s.Set("background-color", "red");
            s.Set("border-top-style", "solid");
            s.Set("border-top-width", "1px");
            s.Set("border-top-color", "black");
            var box = BlockWithStyle(0, 0, 100, 50, s);
            var cmds = Commands(new BoxToPaintConverter(), box);
            Assert.That(cmds.Count, Is.EqualTo(2));
            Assert.That(cmds[0], Is.InstanceOf<FillRectCommand>());
            Assert.That(cmds[1], Is.InstanceOf<StrokeBorderCommand>());
        }

        [Test]
        public void Border_radius_propagates_to_FillRect_and_StrokeBorder() {
            var s = MakeStyle();
            s.Set("background-color", "red");
            s.Set("border-radius", "8px");
            s.Set("border-top-style", "solid");
            s.Set("border-top-width", "1px");
            s.Set("border-top-color", "black");
            var box = BlockWithStyle(0, 0, 100, 50, s);
            var cmds = Commands(new BoxToPaintConverter(), box);
            var fill = (FillRectCommand)cmds[0];
            var stroke = (StrokeBorderCommand)cmds[1];
            Assert.That(fill.Radii.TopLeft.XRadius, Is.EqualTo(8).Within(1e-6));
            Assert.That(stroke.Radii.TopLeft.XRadius, Is.EqualTo(8).Within(1e-6));
        }

        [Test]
        public void Single_shadow_emits_DrawShadow_before_FillRect() {
            var s = MakeStyle();
            s.Set("background-color", "white");
            s.Set("box-shadow", "2px 2px 4px black");
            var box = BlockWithStyle(0, 0, 100, 50, s);
            var cmds = Commands(new BoxToPaintConverter(), box);
            Assert.That(cmds.Count, Is.EqualTo(2));
            Assert.That(cmds[0], Is.InstanceOf<DrawShadowCommand>());
            Assert.That(cmds[1], Is.InstanceOf<FillRectCommand>());
        }

        [Test]
        public void Opacity_below_one_wraps_content_in_PushPop() {
            var s = MakeStyle();
            s.Set("opacity", "0.5");
            s.Set("background-color", "red");
            var box = BlockWithStyle(0, 0, 100, 50, s);
            var cmds = Commands(new BoxToPaintConverter(), box);
            Assert.That(cmds[0], Is.InstanceOf<PushOpacityCommand>());
            Assert.That(cmds[cmds.Count - 1], Is.InstanceOf<PopOpacityCommand>());
            Assert.That(((PushOpacityCommand)cmds[0]).Opacity, Is.EqualTo(0.5).Within(1e-6));
        }

        [Test]
        public void Transform_wraps_content_in_PushPop() {
            var s = MakeStyle();
            s.Set("transform", "translate(10px, 20px)");
            s.Set("background-color", "red");
            var box = BlockWithStyle(0, 0, 100, 50, s);
            var cmds = Commands(new BoxToPaintConverter(), box);
            Assert.That(cmds[0], Is.InstanceOf<PushTransformCommand>());
            Assert.That(cmds[cmds.Count - 1], Is.InstanceOf<PopTransformCommand>());
        }

        [Test]
        public void Overflow_hidden_emits_PushClip_PopClip() {
            var s = MakeStyle();
            s.Set("overflow", "hidden");
            s.Set("background-color", "red");
            var box = BlockWithStyle(0, 0, 100, 50, s);
            var cmds = Commands(new BoxToPaintConverter(), box);
            // Expect: FillRect, PushClip, PopClip (clip wraps children, none here).
            bool sawPushClip = false, sawPopClip = false;
            foreach (var cmd in cmds) {
                if (cmd is PushClipCommand) sawPushClip = true;
                if (cmd is PopClipCommand) sawPopClip = true;
            }
            Assert.That(sawPushClip, Is.True);
            Assert.That(sawPopClip, Is.True);
        }

        [Test]
        public void Overflow_clip_margin_inflates_clip_rect_on_all_sides() {
            var s = MakeStyle();
            s.Set("overflow", "clip");
            s.Set("overflow-clip-margin", "8px");
            var box = BlockWithStyle(0, 0, 100, 100, s);
            var clips = Commands(new BoxToPaintConverter(), box).OfType<PushClipCommand>().ToList();
            Assert.That(clips.Count, Is.EqualTo(1));
            Assert.That(clips[0].Bounds.X, Is.EqualTo(-8).Within(1e-6));
            Assert.That(clips[0].Bounds.Y, Is.EqualTo(-8).Within(1e-6));
            Assert.That(clips[0].Bounds.Width, Is.EqualTo(116).Within(1e-6));
            Assert.That(clips[0].Bounds.Height, Is.EqualTo(116).Within(1e-6));

            var s0 = MakeStyle();
            s0.Set("overflow", "clip");
            var box0 = BlockWithStyle(0, 0, 100, 100, s0);
            var clips0 = Commands(new BoxToPaintConverter(), box0).OfType<PushClipCommand>().ToList();
            Assert.That(clips0.Count, Is.EqualTo(1));
            Assert.That(clips0[0].Bounds.X, Is.EqualTo(0).Within(1e-6));
            Assert.That(clips0[0].Bounds.Y, Is.EqualTo(0).Within(1e-6));
            Assert.That(clips0[0].Bounds.Width, Is.EqualTo(100).Within(1e-6));
            Assert.That(clips0[0].Bounds.Height, Is.EqualTo(100).Within(1e-6));
        }

        [Test]
        public void Overflow_clip_margin_visual_box_keyword_selects_reference_edge() {
            // CSS Overflow L4 §6 grammar `<visual-box>? <length [0,∞]>?`. The
            // `<visual-box>` keyword (padding-box | content-box | border-box)
            // selects the reference edge the length inflates from. Defaults to
            // padding-box when the keyword is omitted.

            // Setup: 200x120 box, border=1 on all sides, padding=34 on all sides.
            // border-box = (0,0,200,120); padding-box = (1,1,198,118);
            // content-box = (35,35,130,50).

            // padding-box 8px — inflate the padding-box edges outward by 8.
            // Expected: (1-8, 1-8, 198+16, 118+16) = (-7, -7, 214, 134).
            var sPad = MakeStyle();
            sPad.Set("overflow", "clip");
            sPad.Set("overflow-clip-margin", "padding-box 8px");
            var boxPad = BlockWithStyle(0, 0, 200, 120, sPad);
            boxPad.BorderTop = boxPad.BorderRight = boxPad.BorderBottom = boxPad.BorderLeft = 1;
            boxPad.PaddingTop = boxPad.PaddingRight = boxPad.PaddingBottom = boxPad.PaddingLeft = 34;
            var clipsPad = Commands(new BoxToPaintConverter(), boxPad).OfType<PushClipCommand>().ToList();
            Assert.That(clipsPad.Count, Is.EqualTo(1));
            Assert.That(clipsPad[0].Bounds.X, Is.EqualTo(-7).Within(1e-6));
            Assert.That(clipsPad[0].Bounds.Y, Is.EqualTo(-7).Within(1e-6));
            Assert.That(clipsPad[0].Bounds.Width, Is.EqualTo(214).Within(1e-6));
            Assert.That(clipsPad[0].Bounds.Height, Is.EqualTo(134).Within(1e-6));

            // content-box 8px — inflate the content-box edges outward by 8.
            // Expected: (35-8, 35-8, 130+16, 50+16) = (27, 27, 146, 66).
            var sCon = MakeStyle();
            sCon.Set("overflow", "clip");
            sCon.Set("overflow-clip-margin", "content-box 8px");
            var boxCon = BlockWithStyle(0, 0, 200, 120, sCon);
            boxCon.BorderTop = boxCon.BorderRight = boxCon.BorderBottom = boxCon.BorderLeft = 1;
            boxCon.PaddingTop = boxCon.PaddingRight = boxCon.PaddingBottom = boxCon.PaddingLeft = 34;
            var clipsCon = Commands(new BoxToPaintConverter(), boxCon).OfType<PushClipCommand>().ToList();
            Assert.That(clipsCon.Count, Is.EqualTo(1));
            Assert.That(clipsCon[0].Bounds.X, Is.EqualTo(27).Within(1e-6));
            Assert.That(clipsCon[0].Bounds.Y, Is.EqualTo(27).Within(1e-6));
            Assert.That(clipsCon[0].Bounds.Width, Is.EqualTo(146).Within(1e-6));
            Assert.That(clipsCon[0].Bounds.Height, Is.EqualTo(66).Within(1e-6));

            // border-box 8px — inflate the border-box edges outward by 8.
            // Expected: (0-8, 0-8, 200+16, 120+16) = (-8, -8, 216, 136).
            var sBor = MakeStyle();
            sBor.Set("overflow", "clip");
            sBor.Set("overflow-clip-margin", "border-box 8px");
            var boxBor = BlockWithStyle(0, 0, 200, 120, sBor);
            boxBor.BorderTop = boxBor.BorderRight = boxBor.BorderBottom = boxBor.BorderLeft = 1;
            boxBor.PaddingTop = boxBor.PaddingRight = boxBor.PaddingBottom = boxBor.PaddingLeft = 34;
            var clipsBor = Commands(new BoxToPaintConverter(), boxBor).OfType<PushClipCommand>().ToList();
            Assert.That(clipsBor.Count, Is.EqualTo(1));
            Assert.That(clipsBor[0].Bounds.X, Is.EqualTo(-8).Within(1e-6));
            Assert.That(clipsBor[0].Bounds.Y, Is.EqualTo(-8).Within(1e-6));
            Assert.That(clipsBor[0].Bounds.Width, Is.EqualTo(216).Within(1e-6));
            Assert.That(clipsBor[0].Bounds.Height, Is.EqualTo(136).Within(1e-6));

            // Regression: omitting the keyword defaults to padding-box (spec).
            // Same numbers as the `padding-box 8px` case above.
            var sDef = MakeStyle();
            sDef.Set("overflow", "clip");
            sDef.Set("overflow-clip-margin", "8px");
            var boxDef = BlockWithStyle(0, 0, 200, 120, sDef);
            boxDef.BorderTop = boxDef.BorderRight = boxDef.BorderBottom = boxDef.BorderLeft = 1;
            boxDef.PaddingTop = boxDef.PaddingRight = boxDef.PaddingBottom = boxDef.PaddingLeft = 34;
            var clipsDef = Commands(new BoxToPaintConverter(), boxDef).OfType<PushClipCommand>().ToList();
            Assert.That(clipsDef.Count, Is.EqualTo(1));
            Assert.That(clipsDef[0].Bounds.X, Is.EqualTo(-7).Within(1e-6),
                "spec default with no <visual-box> keyword is padding-box");
            Assert.That(clipsDef[0].Bounds.Y, Is.EqualTo(-7).Within(1e-6));
            Assert.That(clipsDef[0].Bounds.Width, Is.EqualTo(214).Within(1e-6));
            Assert.That(clipsDef[0].Bounds.Height, Is.EqualTo(134).Within(1e-6));
        }

        [Test]
        public void Overflow_clip_margin_per_side_longhands_inflate_independently() {
            var s = MakeStyle();
            s.Set("overflow", "clip");
            s.Set("overflow-clip-margin-top", "8px");
            s.Set("overflow-clip-margin-right", "4px");
            s.Set("overflow-clip-margin-bottom", "12px");
            s.Set("overflow-clip-margin-left", "16px");
            var box = BlockWithStyle(0, 0, 100, 100, s);
            var clips = Commands(new BoxToPaintConverter(), box).OfType<PushClipCommand>().ToList();
            Assert.That(clips.Count, Is.EqualTo(1));
            Assert.That(clips[0].Bounds.X, Is.EqualTo(-16).Within(1e-6));
            Assert.That(clips[0].Bounds.Y, Is.EqualTo(-8).Within(1e-6));
            Assert.That(clips[0].Bounds.Width, Is.EqualTo(120).Within(1e-6));
            Assert.That(clips[0].Bounds.Height, Is.EqualTo(120).Within(1e-6));
        }

        [Test]
        public void Overflow_clip_margin_side_longhand_overrides_shorthand() {
            var s = MakeStyle();
            s.Set("overflow", "clip");
            s.Set("overflow-clip-margin", "8px");
            s.Set("overflow-clip-margin-right", "20px");
            var box = BlockWithStyle(0, 0, 100, 100, s);
            var clips = Commands(new BoxToPaintConverter(), box).OfType<PushClipCommand>().ToList();
            Assert.That(clips.Count, Is.EqualTo(1));
            Assert.That(clips[0].Bounds.X, Is.EqualTo(-8).Within(1e-6));
            Assert.That(clips[0].Bounds.Y, Is.EqualTo(-8).Within(1e-6));
            Assert.That(clips[0].Bounds.Width, Is.EqualTo(128).Within(1e-6));
            Assert.That(clips[0].Bounds.Height, Is.EqualTo(116).Within(1e-6));
        }

        [Test]
        public void Overflow_clip_margin_with_border_radius_zeros_radii_on_inflated_rect() {
            var s = MakeStyle();
            s.Set("overflow", "clip");
            s.Set("overflow-clip-margin", "8px");
            s.Set("border-radius", "20px");
            var box = BlockWithStyle(0, 0, 100, 100, s);
            var clips = Commands(new BoxToPaintConverter(), box).OfType<PushClipCommand>().ToList();
            Assert.That(clips.Count, Is.EqualTo(1));
            Assert.That(clips[0].Bounds.X, Is.EqualTo(-8).Within(1e-6));
            Assert.That(clips[0].Bounds.Y, Is.EqualTo(-8).Within(1e-6));
            Assert.That(clips[0].Bounds.Width, Is.EqualTo(116).Within(1e-6));
            Assert.That(clips[0].Bounds.Height, Is.EqualTo(116).Within(1e-6));
            Assert.That(clips[0].Radii.IsZero, Is.True);

            var s0 = MakeStyle();
            s0.Set("overflow", "clip");
            s0.Set("border-radius", "20px");
            var box0 = BlockWithStyle(0, 0, 100, 100, s0);
            var clips0 = Commands(new BoxToPaintConverter(), box0).OfType<PushClipCommand>().ToList();
            Assert.That(clips0.Count, Is.EqualTo(1));
            Assert.That(clips0[0].Bounds.Width, Is.EqualTo(100).Within(1e-6));
            Assert.That(clips0[0].Bounds.Height, Is.EqualTo(100).Within(1e-6));
            Assert.That(clips0[0].Radii.TopLeft.XRadius, Is.EqualTo(20).Within(1e-6));
            Assert.That(clips0[0].Radii.BottomRight.YRadius, Is.EqualTo(20).Within(1e-6));
        }

        [Test]
        public void Overflow_clip_margin_logical_axis_longhands_map_to_physical_via_writing_mode() {
            // CSS Overflow L4 §6 logical-axis longhands. The cascade aliases
            // overflow-clip-margin-{inline|block}-{start|end} to a physical
            // edge via writing-mode + direction, so the paint clip rect
            // inflates on the resolved physical side.

            const string fix = "position: absolute; left: 0; top: 0; ";

            // horizontal-tb + ltr: inline-start -> left.
            var (rootLtr, _, _) = Build(
                "<div id=\"box\"></div>",
                "#box { " + fix + "overflow: clip; width: 100px; height: 100px; " +
                "writing-mode: horizontal-tb; direction: ltr; " +
                "overflow-clip-margin-inline-start: 8px; }",
                viewportWidth: 400);
            var clipsLtr = new BoxToPaintConverter().Convert(rootLtr).Commands
                .OfType<PushClipCommand>().ToList();
            Assert.That(clipsLtr.Count, Is.EqualTo(1));
            Assert.That(clipsLtr[0].Bounds.X, Is.EqualTo(-8).Within(1e-6),
                "inline-start in horizontal-tb ltr is the left edge");
            Assert.That(clipsLtr[0].Bounds.Y, Is.EqualTo(0).Within(1e-6));
            Assert.That(clipsLtr[0].Bounds.Width, Is.EqualTo(108).Within(1e-6));
            Assert.That(clipsLtr[0].Bounds.Height, Is.EqualTo(100).Within(1e-6));

            // horizontal-tb + rtl: inline-start -> right.
            var (rootRtl, _, _) = Build(
                "<div id=\"box\"></div>",
                "#box { " + fix + "overflow: clip; width: 100px; height: 100px; " +
                "writing-mode: horizontal-tb; direction: rtl; " +
                "overflow-clip-margin-inline-start: 8px; }",
                viewportWidth: 400);
            var clipsRtl = new BoxToPaintConverter().Convert(rootRtl).Commands
                .OfType<PushClipCommand>().ToList();
            Assert.That(clipsRtl.Count, Is.EqualTo(1));
            Assert.That(clipsRtl[0].Bounds.X, Is.EqualTo(0).Within(1e-6),
                "rtl flips inline-start to the right edge — left stays at 0");
            Assert.That(clipsRtl[0].Bounds.Width, Is.EqualTo(108).Within(1e-6));
            Assert.That(clipsRtl[0].Bounds.Y, Is.EqualTo(0).Within(1e-6));
            Assert.That(clipsRtl[0].Bounds.Height, Is.EqualTo(100).Within(1e-6));

            // vertical-rl + ltr: inline-start -> top.
            var (rootVrl, _, _) = Build(
                "<div id=\"box\"></div>",
                "#box { " + fix + "overflow: clip; width: 100px; height: 100px; " +
                "writing-mode: vertical-rl; direction: ltr; " +
                "overflow-clip-margin-inline-start: 8px; }",
                viewportWidth: 400);
            var clipsVrl = new BoxToPaintConverter().Convert(rootVrl).Commands
                .OfType<PushClipCommand>().ToList();
            Assert.That(clipsVrl.Count, Is.EqualTo(1));
            Assert.That(clipsVrl[0].Bounds.Y, Is.EqualTo(-8).Within(1e-6),
                "vertical-rl maps inline-start to top");
            Assert.That(clipsVrl[0].Bounds.Height, Is.EqualTo(108).Within(1e-6));
            Assert.That(clipsVrl[0].Bounds.X, Is.EqualTo(0).Within(1e-6));
            Assert.That(clipsVrl[0].Bounds.Width, Is.EqualTo(100).Within(1e-6));

            // Regression: physical longhand keeps working untouched.
            var (rootPhysical, _, _) = Build(
                "<div id=\"box\"></div>",
                "#box { " + fix + "overflow: clip; width: 100px; height: 100px; " +
                "overflow-clip-margin-left: 4px; }",
                viewportWidth: 400);
            var clipsPhys = new BoxToPaintConverter().Convert(rootPhysical).Commands
                .OfType<PushClipCommand>().ToList();
            Assert.That(clipsPhys.Count, Is.EqualTo(1));
            Assert.That(clipsPhys[0].Bounds.X, Is.EqualTo(-4).Within(1e-6));
            Assert.That(clipsPhys[0].Bounds.Width, Is.EqualTo(104).Within(1e-6));
        }

        [Test]
        public void Overflow_hidden_ignores_clip_margin() {
            var s = MakeStyle();
            s.Set("overflow", "hidden");
            s.Set("overflow-clip-margin", "8px");
            var box = BlockWithStyle(0, 0, 100, 100, s);
            var clips = Commands(new BoxToPaintConverter(), box).OfType<PushClipCommand>().ToList();
            Assert.That(clips.Count, Is.EqualTo(1));
            Assert.That(clips[0].Bounds.X, Is.EqualTo(0).Within(1e-6));
            Assert.That(clips[0].Bounds.Y, Is.EqualTo(0).Within(1e-6));
            Assert.That(clips[0].Bounds.Width, Is.EqualTo(100).Within(1e-6));
            Assert.That(clips[0].Bounds.Height, Is.EqualTo(100).Within(1e-6));
        }

        [Test]
        public void Scroll_container_clip_uses_padding_box_and_preserves_radii() {
            var s = MakeStyle();
            s.Set("overflow", "hidden");
            s.Set("border-radius", "24px");
            var box = BlockWithStyle(0, 0, 200, 120, s);
            box.BorderTop = box.BorderRight = box.BorderBottom = box.BorderLeft = 1;
            box.PaddingTop = box.PaddingRight = box.PaddingBottom = box.PaddingLeft = 34;

            var scroll = new ScrollContainer();
            var state = scroll.GetOrCreate(box);
            state.OverflowX = ScrollOverflow.Hidden;
            state.OverflowY = ScrollOverflow.Scroll;

            var cmds = new BoxToPaintConverter()
                .Convert(box, null, null, scroll, null)
                .Commands;

            var clips = cmds.OfType<PushClipCommand>().ToList();
            Assert.That(clips.Count, Is.EqualTo(2),
                "overflow:hidden emits the cached overflow clip and the live scrollport clip");

            var scrollport = clips[1];
            Assert.That(scrollport.Bounds.X, Is.EqualTo(1).Within(1e-6));
            Assert.That(scrollport.Bounds.Y, Is.EqualTo(1).Within(1e-6));
            Assert.That(scrollport.Bounds.Width, Is.EqualTo(186).Within(1e-6));
            Assert.That(scrollport.Bounds.Height, Is.EqualTo(118).Within(1e-6));
            Assert.That(scrollport.Radii.TopLeft.XRadius, Is.EqualTo(24).Within(1e-6));

            Assert.That(clips.Any(c => Math.Abs(c.Bounds.X - 35) < 1e-6
                                       && Math.Abs(c.Bounds.Y - 35) < 1e-6),
                Is.False,
                "The scrollport is the padding box; clipping to the content box cuts off padded overflow effects.");
        }

        [Test]
        public void Scroll_container_without_scrollbar_reuses_overflow_clip() {
            var s = MakeStyle();
            s.Set("overflow", "hidden");
            s.Set("border-radius", "24px");
            var box = BlockWithStyle(0, 0, 200, 120, s);
            box.BorderTop = box.BorderRight = box.BorderBottom = box.BorderLeft = 1;
            box.PaddingTop = box.PaddingRight = box.PaddingBottom = box.PaddingLeft = 34;

            var scroll = new ScrollContainer();
            var state = scroll.GetOrCreate(box);
            state.OverflowX = ScrollOverflow.Hidden;
            state.OverflowY = ScrollOverflow.Hidden;

            var cmds = new BoxToPaintConverter()
                .Convert(box, null, null, scroll, null)
                .Commands;

            var clips = cmds.OfType<PushClipCommand>().ToList();
            Assert.That(clips.Count, Is.EqualTo(1),
                "When no scrollbar shrinks the scrollport, the normal overflow clip already scopes descendants.");
            Assert.That(clips[0].Bounds.X, Is.EqualTo(1).Within(1e-6));
            Assert.That(clips[0].Bounds.Y, Is.EqualTo(1).Within(1e-6));
            Assert.That(clips[0].Bounds.Width, Is.EqualTo(198).Within(1e-6));
            Assert.That(clips[0].Bounds.Height, Is.EqualTo(118).Within(1e-6));
            Assert.That(clips[0].Radii.TopLeft.XRadius, Is.EqualTo(24).Within(1e-6));
        }

        [Test]
        public void Two_child_boxes_paint_in_document_order() {
            var rootStyle = MakeStyle();
            var root = BlockWithStyle(0, 0, 200, 200, rootStyle);
            var aStyle = MakeStyle();
            aStyle.Set("background-color", "red");
            var a = BlockWithStyle(0, 0, 100, 50, aStyle);
            var bStyle = MakeStyle();
            bStyle.Set("background-color", "blue");
            var b = BlockWithStyle(0, 50, 100, 50, bStyle);
            root.AddChild(a);
            root.AddChild(b);
            var cmds = Commands(new BoxToPaintConverter(), root);
            Assert.That(cmds.Count, Is.EqualTo(2));
            var fa = (FillRectCommand)cmds[0];
            var fb = (FillRectCommand)cmds[1];
            // First fill is red, second is blue.
            Assert.That(fa.Brush.Color.R, Is.GreaterThan(0.5f));
            Assert.That(fb.Brush.Color.B, Is.GreaterThan(0.5f));
        }

        [Test]
        public void End_to_end_div_red_with_p_hello_emits_fill_and_text() {
            var (root, _, _) = Build(
                "<div style=\"background-color:red\"><p>hello</p></div>",
                null, 800);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            int fills = 0, texts = 0;
            bool divFillRed = false;
            bool helloText = false;
            foreach (var cmd in cmds) {
                if (cmd is FillRectCommand fr) {
                    fills++;
                    if (fr.Brush != null && fr.Brush.Kind == BrushKind.SolidColor && fr.Brush.Color.R > 0.5f) divFillRed = true;
                }
                if (cmd is DrawTextCommand dt) {
                    texts++;
                    if (dt.Text.Contains("hello")) helloText = true;
                }
            }
            Assert.That(divFillRed, Is.True, "Expected red fill from div background");
            Assert.That(helloText, Is.True, "Expected DrawText for 'hello'");
        }

        [Test]
        public void Phase1_demo_three_text_runs_with_link_and_bold() {
            var (root, _, _) = Build(
                "<p>Click <a href=\"#\"><strong>here</strong></a> to start</p>",
                "a { color: blue; } strong { font-weight: bold; }",
                300);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            var texts = new List<DrawTextCommand>();
            foreach (var c in cmds) if (c is DrawTextCommand t) texts.Add(t);
            Assert.That(texts.Count, Is.GreaterThanOrEqualTo(3));
            // "here" should appear as a bold + blue text run.
            DrawTextCommand here = null;
            foreach (var t in texts) if (t.Text.Contains("here")) here = t;
            Assert.That(here, Is.Not.Null);
            Assert.That(here.Font.Weight, Is.EqualTo(700));
            Assert.That(here.Color.B, Is.GreaterThan(0.5f));
            Assert.That(here.Color.R, Is.LessThan(0.1f));
        }

        [Test]
        public void Combined_opacity_transform_clip_nest_correctly() {
            var s = MakeStyle();
            s.Set("transform", "translate(5px, 5px)");
            s.Set("opacity", "0.5");
            s.Set("overflow", "hidden");
            s.Set("background-color", "red");
            var box = BlockWithStyle(0, 0, 100, 100, s);
            var cmds = Commands(new BoxToPaintConverter(), box);
            // Expected order:
            //  PushTransform, PushOpacity, FillRect, PushClip, PopClip, PopOpacity, PopTransform
            Assert.That(cmds[0], Is.InstanceOf<PushTransformCommand>());
            Assert.That(cmds[1], Is.InstanceOf<PushOpacityCommand>());
            Assert.That(cmds[cmds.Count - 1], Is.InstanceOf<PopTransformCommand>());
            Assert.That(cmds[cmds.Count - 2], Is.InstanceOf<PopOpacityCommand>());
            // Find clip pair indices.
            int pushClipIdx = -1, popClipIdx = -1;
            for (int i = 0; i < cmds.Count; i++) {
                if (cmds[i] is PushClipCommand) pushClipIdx = i;
                else if (cmds[i] is PopClipCommand) popClipIdx = i;
            }
            Assert.That(pushClipIdx, Is.GreaterThan(0));
            Assert.That(popClipIdx, Is.GreaterThan(pushClipIdx));
            Assert.That(popClipIdx, Is.LessThan(cmds.Count - 2));
        }

        [Test]
        public void Anonymous_block_skips_decoration_but_recurses() {
            var root = new BlockBox();
            root.X = 0; root.Y = 0; root.Width = 100; root.Height = 100;
            var anon = new AnonymousBlockBox();
            anon.X = 0; anon.Y = 0; anon.Width = 100; anon.Height = 50;
            // anon.Style is null by design.
            var childStyle = MakeStyle();
            childStyle.Set("background-color", "red");
            var child = BlockWithStyle(10, 10, 30, 30, childStyle);
            anon.AddChild(child);
            root.AddChild(anon);
            var cmds = Commands(new BoxToPaintConverter(), root);
            // Only the inner child's red fill should appear; anon emits nothing of its own.
            Assert.That(cmds.Count, Is.EqualTo(1));
            Assert.That(cmds[0], Is.InstanceOf<FillRectCommand>());
        }

        // Regression — the demo's randhtml.css declares `.world { z-index: 0 }` and
        // `.hud { z-index: 10 }`, both `position: fixed; inset: 0`. The HUD must
        // paint OVER the world (its z-index is higher). Currently the converter
        // walks children in document order only; this test pins paint order for
        // the in-document case AND will catch a regression if document order ever
        // diverges from z-order without the converter learning to use
        // PaintOrderTraversal.
        [Test]
        public void World_zindex_lower_than_hud_paints_first_in_document_order() {
            const string html =
                "<div class=\"world\"></div>" +
                "<div class=\"hud\"></div>";
            const string css = @"
                .world { position: fixed; inset: 0; z-index: 0; background-color: red; }
                .hud   { position: fixed; inset: 0; z-index: 10; background-color: blue; }
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 800, viewportHeight: 600);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            int worldIdx = -1, hudIdx = -1;
            for (int i = 0; i < cmds.Count; i++) {
                if (cmds[i] is FillRectCommand fr && fr.Brush != null && fr.Brush.Kind == BrushKind.SolidColor) {
                    if (fr.Brush.Color.R > 0.5f && worldIdx < 0) worldIdx = i;
                    if (fr.Brush.Color.B > 0.5f && hudIdx < 0) hudIdx = i;
                }
            }
            Assert.That(worldIdx, Is.GreaterThanOrEqualTo(0), "Expected red world fill");
            Assert.That(hudIdx, Is.GreaterThanOrEqualTo(0), "Expected blue hud fill");
            // hud (z=10) paints AFTER world (z=0) — so on top.
            Assert.That(hudIdx, Is.GreaterThan(worldIdx));
        }

        // Regression for `.unit-name { overflow: hidden }` and similar in the
        // randhtml demo — each truncated label depends on PushClip wrapping
        // child text emission. This guards the layered case where the parent's
        // own background must paint inside the clip region too (radii etc.).
        [Test]
        public void Overflow_hidden_clips_absolutely_positioned_child() {
            const string html =
                "<div class=\"parent\"><div class=\"tooltip\"></div></div>";
            const string css = @"
                .parent { position: relative; width: 100px; height: 50px; overflow: hidden;
                          background-color: white; }
                .tooltip { position: absolute; top: -20px; left: 0; width: 80px; height: 20px;
                           background-color: red; }
            ";
            var (root, _, _) = Build(html, css, viewportWidth: 800);
            var cmds = new BoxToPaintConverter().Convert(root).Commands;
            int pushClipIdx = -1, popClipIdx = -1, tooltipFillIdx = -1;
            for (int i = 0; i < cmds.Count; i++) {
                switch (cmds[i]) {
                    case PushClipCommand: pushClipIdx = i; break;
                    case PopClipCommand:
                        if (pushClipIdx >= 0 && popClipIdx < 0) popClipIdx = i;
                        break;
                    case FillRectCommand fr:
                        // Match red specifically (not just R>0.5 which also
                        // matches the parent's white background fill at index 0
                        // — without the green/blue suppression we'd capture the
                        // parent's bg as `tooltipFillIdx` and the assertion
                        // below would fail trivially).
                        if (fr.Brush != null && fr.Brush.Kind == BrushKind.SolidColor
                            && fr.Brush.Color.R > 0.5f
                            && fr.Brush.Color.G < 0.3f
                            && fr.Brush.Color.B < 0.3f
                            && tooltipFillIdx < 0)
                            tooltipFillIdx = i;
                        break;
                }
            }
            Assert.That(pushClipIdx, Is.GreaterThanOrEqualTo(0));
            Assert.That(popClipIdx, Is.GreaterThan(pushClipIdx));
            Assert.That(tooltipFillIdx, Is.GreaterThan(pushClipIdx),
                "Absolutely-positioned tooltip must paint inside the parent's clip scope");
            Assert.That(tooltipFillIdx, Is.LessThan(popClipIdx),
                "Tooltip fill must come BEFORE the matching PopClip");
        }

        [Test]
        public void Visibility_hidden_skips_box_decorations_but_keeps_layout() {
            // Per CSS UI 4 §9 a `visibility: hidden` box reserves layout space
            // (its X/Y/Width/Height stay valid) but its own background, border,
            // shadow and image fills must not paint.
            var s = MakeStyle();
            s.Set("background-color", "red");
            s.Set("border-top-style", "solid");
            s.Set("border-top-width", "2px");
            s.Set("border-top-color", "black");
            s.Set("box-shadow", "2px 2px 4px black");
            s.Set("visibility", "hidden");
            var box = BlockWithStyle(0, 0, 100, 50, s);
            var cmds = Commands(new BoxToPaintConverter(), box);
            // No FillRect (background), StrokeBorder, or DrawShadow should
            // survive the visibility filter. Push/Pop wrappers (transform /
            // opacity / filter / clip) are not introduced by this style, so
            // the command list should be empty.
            foreach (var c in cmds) {
                Assert.That(c, Is.Not.InstanceOf<FillRectCommand>(),
                    "visibility:hidden must suppress background fill");
                Assert.That(c, Is.Not.InstanceOf<StrokeBorderCommand>(),
                    "visibility:hidden must suppress border stroke");
                Assert.That(c, Is.Not.InstanceOf<DrawShadowCommand>(),
                    "visibility:hidden must suppress box-shadow");
            }
        }

        [Test]
        public void Visibility_hidden_parent_lets_visible_child_paint() {
            // visibility is inherited but can be re-set on a descendant. A
            // hidden parent + visible child should: parent paints nothing of
            // its own, child paints normally.
            var parentStyle = MakeStyle();
            parentStyle.Set("background-color", "red");
            parentStyle.Set("visibility", "hidden");
            var parent = BlockWithStyle(0, 0, 100, 100, parentStyle);

            var childStyle = MakeStyle();
            childStyle.Set("background-color", "blue");
            childStyle.Set("visibility", "visible");
            var child = BlockWithStyle(10, 10, 30, 30, childStyle);
            parent.AddChild(child);

            var cmds = Commands(new BoxToPaintConverter(), parent);
            int fills = 0;
            foreach (var c in cmds) if (c is FillRectCommand) fills++;
            Assert.That(fills, Is.EqualTo(1),
                "Parent's red fill must be skipped; child's blue fill must paint.");
        }

        [Test]
        public void Opacity_zero_still_emits_paint_commands() {
            // CSS Pointer Events 1: opacity:0 elements remain hit-testable
            // and continue to paint (the alpha:0 happens at composite). The
            // converter must NOT short-circuit on opacity:0 — that would
            // break opacity transitions from 0 → 1 on the very first frame.
            var s = MakeStyle();
            s.Set("background-color", "red");
            s.Set("opacity", "0");
            var box = BlockWithStyle(0, 0, 50, 50, s);
            var cmds = Commands(new BoxToPaintConverter(), box);
            int fills = 0;
            bool sawPushOpacity = false;
            foreach (var c in cmds) {
                if (c is FillRectCommand) fills++;
                if (c is PushOpacityCommand) sawPushOpacity = true;
            }
            Assert.That(fills, Is.EqualTo(1),
                "opacity:0 must still emit the underlying FillRect (alpha applied at composite).");
            Assert.That(sawPushOpacity, Is.True,
                "opacity:0 must emit a PushOpacity wrapper.");
        }

        [Test]
        public void Convert_steady_state_does_not_walk_box_tree_for_estimation() {
            // Build a small tree: root with two children. First Convert must
            // walk all three nodes to size the PaintList; the second Convert,
            // with no version bump and the same root reference, must reuse the
            // cached estimate and skip the walk entirely (zero node visits).
            var s = MakeStyle();
            s.Set("background-color", "red");
            var root = BlockWithStyle(0, 0, 100, 100, s);
            var a = BlockWithStyle(0, 0, 10, 10, MakeStyle());
            var b = BlockWithStyle(10, 0, 10, 10, MakeStyle());
            root.AddChild(a);
            root.AddChild(b);

            var c = new BoxToPaintConverter();
            c.Convert(root);
            long firstWalk = c.EstimateWalkNodes;
            Assert.That(firstWalk, Is.EqualTo(3),
                "First Convert must walk root + 2 children to estimate capacity.");

            c.Convert(root);
            Assert.That(c.EstimateWalkNodes, Is.EqualTo(firstWalk),
                "Second Convert with same root and unchanged version must reuse the cached estimate (no additional walk).");
        }

        [Test]
        public void Outline_emits_extra_StrokeBorder_outside_box_bounds() {
            // CSS UI 4 §7: a focused element with `outline: 2px solid red`
            // and `outline-offset: 2px` paints a 2px ring offset 2px outside
            // the border edge. We accept the simple uniform implementation —
            // a single StrokeBorderCommand with bounds bigger than the box.
            var s = MakeStyle();
            s.Set("outline-style", "solid");
            s.Set("outline-width", "2px");
            s.Set("outline-color", "red");
            s.Set("outline-offset", "2px");
            var box = BlockWithStyle(10, 20, 100, 50, s);
            var cmds = Commands(new BoxToPaintConverter(), box);
            var strokes = cmds.OfType<StrokeBorderCommand>().ToList();
            Assert.That(strokes.Count, Is.EqualTo(1),
                "outline must emit exactly one StrokeBorderCommand when no border is set");
            var ring = strokes[0];
            // Outline is offset (offset + width) outside on every side, so the
            // bounds expand by 2*(offset+width) = 8 in each dimension and shift
            // by -(offset+width) = -4 in box-local coords. After absolute
            // translation by (10, 20), bounds.X == 10 - 4 == 6.
            Assert.That(ring.Bounds.X, Is.EqualTo(6).Within(1e-6));
            Assert.That(ring.Bounds.Y, Is.EqualTo(16).Within(1e-6));
            Assert.That(ring.Bounds.Width, Is.EqualTo(108).Within(1e-6));
            Assert.That(ring.Bounds.Height, Is.EqualTo(58).Within(1e-6));
            Assert.That(ring.Borders.Top.Width, Is.EqualTo(2).Within(1e-6));
        }

        [Test]
        public void Clip_path_wraps_visible_decorations() {
            var s = MakeStyle();
            s.Set("background-color", "red");
            s.Set("clip-path", "circle(40% at 50% 50%)");
            var box = BlockWithStyle(0, 0, 100, 100, s);
            var cmds = Commands(new BoxToPaintConverter(), box);

            int push = cmds.FindIndex(c => c is PushClipPathCommand);
            int fill = cmds.FindIndex(c => c is FillRectCommand);
            int pop = cmds.FindIndex(c => c is PopClipPathCommand);
            Assert.That(push, Is.GreaterThanOrEqualTo(0));
            Assert.That(fill, Is.GreaterThan(push));
            Assert.That(pop, Is.GreaterThan(fill));
        }

        [Test]
        public void Backdrop_filter_emits_before_background() {
            var s = MakeStyle();
            s.Set("backdrop-filter", "brightness(0.5) blur(2px)");
            s.Set("background-color", "rgba(255,255,255,0.25)");
            var box = BlockWithStyle(0, 0, 100, 50, s);
            var cmds = Commands(new BoxToPaintConverter(), box);

            int backdrop = cmds.FindIndex(c => c is DrawBackdropFilterCommand);
            int fill = cmds.FindIndex(c => c is FillRectCommand);
            Assert.That(backdrop, Is.GreaterThanOrEqualTo(0));
            Assert.That(fill, Is.GreaterThan(backdrop));
        }

        [Test]
        public void Backdrop_filter_skipped_when_opacity_is_zero() {
            // CSS Filter Effects 1 §4 + CSS Color L3 §3: opacity:0 makes the
            // element fully transparent including any backdrop-filter output.
            // The filter runtime owns its own RT and composites back outside
            // the element's PushOpacity stack, so without an explicit gate
            // the blur paints regardless and leaks visibly under a
            // supposedly-hidden element. Repro from a real game's
            // .discovery-toast (opacity:0 idle, opacity:1 when ToastVisible)
            // — the toast rendered a rectangular blur band at top-center
            // even when fully transparent.
            var s = MakeStyle();
            s.Set("backdrop-filter", "blur(6px)");
            s.Set("background-color", "rgba(10,14,22,0.7)");
            s.Set("opacity", "0");
            var box = BlockWithStyle(0, 0, 100, 50, s);
            var cmds = Commands(new BoxToPaintConverter(), box);

            int backdrop = cmds.FindIndex(c => c is DrawBackdropFilterCommand);
            Assert.That(backdrop, Is.LessThan(0),
                "opacity:0 must suppress backdrop-filter emission — the GPU blur is wasted work and visibly leaks under a transparent element");
        }

        [Test]
        public void Backdrop_filter_emitted_for_partial_opacity() {
            // Regression guard for the fix above: only EXACTLY zero opacity
            // should suppress emission. Partial opacity (0 < α < 1) still
            // emits and gets modulated by the wrapper PushOpacity through
            // the normal compositor — otherwise authors using a fade-in
            // animation from 0 to 1 would see backdrop-filter pop in
            // abruptly instead of fading.
            var s = MakeStyle();
            s.Set("backdrop-filter", "blur(6px)");
            s.Set("background-color", "rgba(10,14,22,0.7)");
            s.Set("opacity", "0.5");
            var box = BlockWithStyle(0, 0, 100, 50, s);
            var cmds = Commands(new BoxToPaintConverter(), box);

            int backdrop = cmds.FindIndex(c => c is DrawBackdropFilterCommand);
            Assert.That(backdrop, Is.GreaterThanOrEqualTo(0),
                "partial opacity must still emit backdrop-filter so fade-in animations show the blur smoothly");
        }

        [Test]
        public void Backdrop_filter_emitted_at_full_opacity_default() {
            // Baseline: with no opacity declared (cascade initial = 1), the
            // backdrop-filter must emit. Pins that the opacity gate isn't
            // accidentally tripped for the common case.
            var s = MakeStyle();
            s.Set("backdrop-filter", "blur(6px)");
            s.Set("background-color", "rgba(10,14,22,0.7)");
            var box = BlockWithStyle(0, 0, 100, 50, s);
            var cmds = Commands(new BoxToPaintConverter(), box);

            int backdrop = cmds.FindIndex(c => c is DrawBackdropFilterCommand);
            Assert.That(backdrop, Is.GreaterThanOrEqualTo(0),
                "default opacity:1 must emit backdrop-filter — this is the common visible-toast case");
        }

        [Test]
        public void Mask_image_wraps_subtree_paint() {
            var s = MakeStyle();
            s.Set("mask-image", "linear-gradient(90deg, transparent, black)");
            s.Set("mask-repeat", "no-repeat");
            s.Set("mask-size", "100% 100%");
            s.Set("background-color", "red");
            var box = BlockWithStyle(0, 0, 100, 50, s);
            var cmds = Commands(new BoxToPaintConverter(), box);

            int push = cmds.FindIndex(c => c is PushMaskCommand);
            int fill = cmds.FindIndex(c => c is FillRectCommand);
            int pop = cmds.FindIndex(c => c is PopMaskCommand);
            Assert.That(push, Is.GreaterThanOrEqualTo(0));
            Assert.That(fill, Is.GreaterThan(push));
            Assert.That(pop, Is.GreaterThan(fill));
            var mask = (PushMaskCommand)cmds[push];
            Assert.That(mask.Mask.Layers.Count, Is.EqualTo(1));
            Assert.That(mask.Mask.Layers[0].Tile.HasValue, Is.True);
            Assert.That(mask.Mask.Layers[0].Tile.Value.RepeatX, Is.EqualTo(BackgroundRepeatMode.NoRepeat));
        }

        [Test]
        public void Mask_image_repeating_conic_gradient_builds_brush() {
            var s = MakeStyle();
            s.Set("mask-image", "repeating-conic-gradient(red, blue 30deg)");
            s.Set("mask-repeat", "no-repeat");
            s.Set("mask-size", "100% 100%");
            s.Set("background-color", "red");
            var box = BlockWithStyle(0, 0, 100, 50, s);
            var cmds = Commands(new BoxToPaintConverter(), box);

            var mask = cmds.OfType<PushMaskCommand>().Single();
            Assert.That(mask.Mask.Layers.Count, Is.EqualTo(1));
            Assert.That(mask.Mask.Layers[0].Brush, Is.Not.Null);
            Assert.That(mask.Mask.Layers[0].Brush.GradientValue, Is.InstanceOf<ConicGradient>());
        }

        [Test]
        public void Mask_image_layers_cycle_longhands_and_preserve_composites() {
            var s = MakeStyle();
            s.Set("mask-image",
                "linear-gradient(90deg, transparent, black), linear-gradient(0deg, transparent, black)");
            s.Set("mask-repeat", "no-repeat");
            s.Set("mask-size", "100% 100%, 50% 50%");
            s.Set("mask-composite", "intersect");
            s.Set("background-color", "red");
            var box = BlockWithStyle(0, 0, 100, 50, s);
            var cmds = Commands(new BoxToPaintConverter(), box);

            var mask = cmds.OfType<PushMaskCommand>().Single();
            Assert.That(mask.Mask.Layers.Count, Is.EqualTo(2));
            Assert.That(mask.Mask.Layers[0].Composite, Is.EqualTo(MaskComposite.Intersect));
            Assert.That(mask.Mask.Layers[1].Composite, Is.EqualTo(MaskComposite.Intersect));
            Assert.That(mask.Mask.Layers[0].Tile.Value.TileWidth, Is.EqualTo(100).Within(1e-6));
            Assert.That(mask.Mask.Layers[1].Tile.Value.TileWidth, Is.EqualTo(50).Within(1e-6));
            Assert.That(mask.Mask.Layers[0].Tile.Value.RepeatX, Is.EqualTo(BackgroundRepeatMode.NoRepeat));
        }
    }
}
