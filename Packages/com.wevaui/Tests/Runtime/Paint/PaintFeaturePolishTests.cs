using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Paint.Filters;
using Weva.Testing.Goldens;

namespace Weva.Tests.Paint {
    // Coverage for the paint features closed in PLAN §11: inset shadows,
    // dashed/dotted/double borders, drop-shadow filter, and multi-layer
    // backgrounds. Tests live alongside the resolver and rasterizer tests.
    public class PaintFeaturePolishTests {
        static ComputedStyle Style() => new ComputedStyle(new Element("div"));

        static BlockBox Box(double w, double h, ComputedStyle s) {
            var b = new BlockBox();
            b.Style = s;
            b.X = 0; b.Y = 0; b.Width = w; b.Height = h;
            return b;
        }

        static List<PaintCommand> Convert(Box root) {
            return new BoxToPaintConverter().Convert(root).Commands;
        }

        // ----- Inset shadows -----

        [Test]
        public void Inset_shadow_is_emitted_after_background_and_border() {
            var s = Style();
            s.Set("background-color", "white");
            s.Set("box-shadow", "inset 4px 4px 6px black");
            var cmds = Convert(Box(100, 50, s));
            int fillIdx = cmds.FindIndex(c => c is FillRectCommand);
            int shadowIdx = cmds.FindIndex(c => c is DrawShadowCommand sh && sh.Shadow.Inset);
            Assert.That(fillIdx, Is.GreaterThanOrEqualTo(0));
            Assert.That(shadowIdx, Is.GreaterThan(fillIdx));
        }

        [Test]
        public void Outset_and_inset_compose_in_one_box() {
            var s = Style();
            s.Set("background-color", "white");
            s.Set("box-shadow", "2px 2px 4px black, inset 1px 1px 4px red");
            var cmds = Convert(Box(80, 40, s));
            int outsetCount = 0;
            int insetCount = 0;
            int firstShadow = -1, fillIdx = -1, lastShadow = -1;
            for (int i = 0; i < cmds.Count; i++) {
                if (cmds[i] is DrawShadowCommand ds) {
                    if (firstShadow < 0) firstShadow = i;
                    lastShadow = i;
                    if (ds.Shadow.Inset) insetCount++; else outsetCount++;
                }
                if (cmds[i] is FillRectCommand && fillIdx < 0) fillIdx = i;
            }
            Assert.That(outsetCount, Is.EqualTo(1));
            Assert.That(insetCount, Is.EqualTo(1));
            Assert.That(firstShadow, Is.LessThan(fillIdx));
            Assert.That(lastShadow, Is.GreaterThan(fillIdx));
        }

        [Test]
        public void Inset_shadow_paints_dark_pixels_inside_the_box() {
            var ras = new SoftwareRasterizer(80, 60);
            ras.Clear(255, 255, 255, 255);
            var bounds = new Rect(10, 10, 60, 40);
            var sh = new BoxShadow(8, 8, 4, 0, LinearColor.Black, inset: true);
            ras.Submit(new DrawShadowCommand(bounds, BorderRadii.Zero, sh));
            // With offset (+8, +8) the dark rim is on the top-left edge of the box's
            // interior; the offset moves the hole down-right.
            byte topLeftRim = ras.Pixels[((12) * 80 + 12) * 4 + 0];
            byte interior = ras.Pixels[((30) * 80 + 40) * 4 + 0];
            byte outsidePx = ras.Pixels[((5) * 80 + 5) * 4 + 0];
            Assert.That(topLeftRim, Is.LessThan(255));
            Assert.That(outsidePx, Is.EqualTo(255));
            Assert.That(interior, Is.GreaterThan(topLeftRim));
        }

        // ----- Dashed borders -----

        static ComputedStyle BorderStyle(string side, string style, string width = "4px") {
            var s = Style();
            s.Set("border-" + side + "-style", style);
            s.Set("border-" + side + "-width", width);
            s.Set("border-" + side + "-color", "black");
            return s;
        }

        [Test]
        public void Dashed_top_border_emits_stripes() {
            var ras = new SoftwareRasterizer(40, 20);
            ras.Clear(255, 255, 255, 255);
            var edge = new BorderEdge(global::Weva.Paint.BorderStyle.Dashed, 4, LinearColor.Black);
            var borders = new Borders(edge, BorderEdge.None, BorderEdge.None, BorderEdge.None);
            ras.Submit(new StrokeBorderCommand(new Rect(0, 0, 40, 20), borders));
            // Pattern: dash 8px, gap 8px, dash 8px, gap 8px, dash 8px → black, black,
            // white, white, black across pixel rows on row 0.
            byte black0 = ras.Pixels[(0 * 40 + 0) * 4 + 0];
            byte gap0 = ras.Pixels[(0 * 40 + 12) * 4 + 0];
            byte black1 = ras.Pixels[(0 * 40 + 16) * 4 + 0];
            Assert.That(black0, Is.LessThan(50));
            Assert.That(gap0, Is.GreaterThan(200));
            Assert.That(black1, Is.LessThan(50));
        }

        [Test]
        public void Dashed_left_border_emits_vertical_stripes() {
            var ras = new SoftwareRasterizer(20, 40);
            ras.Clear(255, 255, 255, 255);
            var edge = new BorderEdge(global::Weva.Paint.BorderStyle.Dashed, 4, LinearColor.Black);
            var borders = new Borders(BorderEdge.None, BorderEdge.None, BorderEdge.None, edge);
            ras.Submit(new StrokeBorderCommand(new Rect(0, 0, 20, 40), borders));
            byte black0 = ras.Pixels[(0 * 20 + 0) * 4 + 0];
            byte gap0 = ras.Pixels[(12 * 20 + 0) * 4 + 0];
            byte black1 = ras.Pixels[(16 * 20 + 0) * 4 + 0];
            Assert.That(black0, Is.LessThan(50));
            Assert.That(gap0, Is.GreaterThan(200));
            Assert.That(black1, Is.LessThan(50));
        }

        [Test]
        public void Each_edge_can_have_its_own_dashed_style() {
            var s = Style();
            s.Set("border-top-style", "dashed");
            s.Set("border-top-width", "2px");
            s.Set("border-top-color", "black");
            s.Set("border-right-style", "solid");
            s.Set("border-right-width", "2px");
            s.Set("border-right-color", "red");
            var box = Box(40, 20, s);
            var cmds = Convert(box);
            int strokeIdx = cmds.FindIndex(c => c is StrokeBorderCommand);
            Assert.That(strokeIdx, Is.GreaterThanOrEqualTo(0));
            var sb = (StrokeBorderCommand)cmds[strokeIdx];
            Assert.That(sb.Borders.Top.Style, Is.EqualTo(global::Weva.Paint.BorderStyle.Dashed));
            Assert.That(sb.Borders.Right.Style, Is.EqualTo(global::Weva.Paint.BorderStyle.Solid));
        }

        [Test]
        public void Dashed_border_does_not_paint_in_the_gaps() {
            var ras = new SoftwareRasterizer(40, 20);
            ras.Clear(255, 255, 255, 255);
            var edge = new BorderEdge(global::Weva.Paint.BorderStyle.Dashed, 4, LinearColor.Black);
            var borders = new Borders(edge, BorderEdge.None, BorderEdge.None, BorderEdge.None);
            ras.Submit(new StrokeBorderCommand(new Rect(0, 0, 40, 20), borders));
            // Pixels at gap positions stay white.
            for (int x = 8; x < 16; x++) {
                Assert.That(ras.Pixels[(0 * 40 + x) * 4 + 0], Is.GreaterThan(200));
            }
        }

        // ----- Dotted borders -----

        [Test]
        public void Dotted_top_border_emits_circle_dots() {
            var ras = new SoftwareRasterizer(40, 20);
            ras.Clear(255, 255, 255, 255);
            var edge = new BorderEdge(global::Weva.Paint.BorderStyle.Dotted, 4, LinearColor.Black);
            var borders = new Borders(edge, BorderEdge.None, BorderEdge.None, BorderEdge.None);
            ras.Submit(new StrokeBorderCommand(new Rect(0, 0, 40, 20), borders));
            // Step is 2 * 4 = 8. Centers at (2, 2), (10, 2), (18, 2)... So pixel
            // (1..3, 0..3) inside the first circle, (5..7, 0..3) outside.
            byte dotR = ras.Pixels[(2 * 40 + 2) * 4 + 0];
            byte gapR = ras.Pixels[(2 * 40 + 6) * 4 + 0];
            Assert.That(dotR, Is.LessThan(50));
            Assert.That(gapR, Is.GreaterThan(200));
        }

        [Test]
        public void Dotted_border_period_is_two_widths() {
            var ras = new SoftwareRasterizer(40, 20);
            ras.Clear(255, 255, 255, 255);
            var edge = new BorderEdge(global::Weva.Paint.BorderStyle.Dotted, 2, LinearColor.Black);
            var borders = new Borders(edge, BorderEdge.None, BorderEdge.None, BorderEdge.None);
            ras.Submit(new StrokeBorderCommand(new Rect(0, 0, 40, 20), borders));
            // Step is 2 * 2 = 4 with diameter 2. Centers at (1, 1), (5, 1), (9, 1)...
            byte dot0 = ras.Pixels[(1 * 40 + 1) * 4 + 0];
            byte dot1 = ras.Pixels[(1 * 40 + 5) * 4 + 0];
            byte dot2 = ras.Pixels[(1 * 40 + 9) * 4 + 0];
            Assert.That(dot0, Is.LessThan(80));
            Assert.That(dot1, Is.LessThan(80));
            Assert.That(dot2, Is.LessThan(80));
        }

        [Test]
        public void Dotted_border_resolver_passthrough() {
            var s = Style();
            s.Set("border-top-style", "dotted");
            s.Set("border-top-width", "3px");
            s.Set("border-top-color", "black");
            var cmds = Convert(Box(40, 20, s));
            int strokeIdx = cmds.FindIndex(c => c is StrokeBorderCommand);
            Assert.That(strokeIdx, Is.GreaterThanOrEqualTo(0));
            var sb = (StrokeBorderCommand)cmds[strokeIdx];
            Assert.That(sb.Borders.Top.Style, Is.EqualTo(global::Weva.Paint.BorderStyle.Dotted));
        }

        // ----- Double borders -----

        [Test]
        public void Double_top_border_emits_two_parallel_strokes_with_gap() {
            var ras = new SoftwareRasterizer(20, 20);
            ras.Clear(255, 255, 255, 255);
            var edge = new BorderEdge(global::Weva.Paint.BorderStyle.Double, 9, LinearColor.Black);
            var borders = new Borders(edge, BorderEdge.None, BorderEdge.None, BorderEdge.None);
            ras.Submit(new StrokeBorderCommand(new Rect(0, 0, 20, 20), borders));
            // Width 9 → third 3. Inner stroke rows 0..2, gap 3..5, outer stroke 6..8.
            byte stroke1 = ras.Pixels[(0 * 20 + 5) * 4 + 0];
            byte gap = ras.Pixels[(4 * 20 + 5) * 4 + 0];
            byte stroke2 = ras.Pixels[(7 * 20 + 5) * 4 + 0];
            Assert.That(stroke1, Is.LessThan(50));
            Assert.That(gap, Is.GreaterThan(200));
            Assert.That(stroke2, Is.LessThan(50));
        }

        [Test]
        public void Double_border_resolver_passthrough() {
            var s = Style();
            s.Set("border-top-style", "double");
            s.Set("border-top-width", "6px");
            s.Set("border-top-color", "black");
            var cmds = Convert(Box(40, 20, s));
            int strokeIdx = cmds.FindIndex(c => c is StrokeBorderCommand);
            var sb = (StrokeBorderCommand)cmds[strokeIdx];
            Assert.That(sb.Borders.Top.Style, Is.EqualTo(global::Weva.Paint.BorderStyle.Double));
        }

        // ----- drop-shadow filter -----

        [Test]
        public void Drop_shadow_filter_is_recorded_in_push_filter() {
            // RECALIBRATED (long-standing known-red): a LONE drop-shadow takes
            // the synthetic DrawShadow shortcut, never a filter scope (see
            // isLoneDropShadow in BoxToPaintConverter — the scope's composite
            // painted over later siblings, story-bubble `.frame`). The chain
            // only records DropShadowFilter when ANOTHER function forces the
            // real scope.
            var s = Style();
            s.Set("background-color", "red");
            s.Set("filter", "drop-shadow(4px 4px 0 black) blur(2px)");
            var cmds = Convert(Box(40, 40, s));
            int pushIdx = cmds.FindIndex(c => c is PushFilterCommand);
            Assert.That(pushIdx, Is.GreaterThanOrEqualTo(0));
            var pf = (PushFilterCommand)cmds[pushIdx];
            Assert.That(pf.Filters.Functions.Count, Is.EqualTo(2));
            Assert.That(pf.Filters.Functions[0], Is.InstanceOf<DropShadowFilter>());
        }

        [Test]
        public void Lone_drop_shadow_takes_the_synthetic_shadow_shortcut() {
            var s = Style();
            s.Set("background-color", "red");
            s.Set("filter", "drop-shadow(4px 4px 0 black)");
            var cmds = Convert(Box(40, 40, s));
            Assert.That(cmds.FindIndex(c => c is PushFilterCommand), Is.EqualTo(-1),
                "lone drop-shadow must not open a filter scope");
            int shadowIdx = cmds.FindIndex(c => c is DrawShadowCommand);
            Assert.That(shadowIdx, Is.GreaterThanOrEqualTo(0), "synthetic DrawShadow expected");
            var sh = ((DrawShadowCommand)cmds[shadowIdx]).Shadow;
            Assert.That(sh.OffsetX, Is.EqualTo(4));
            Assert.That(sh.OffsetY, Is.EqualTo(4));
            Assert.That(sh.BlurRadius, Is.EqualTo(0));
            Assert.That(sh.Inset, Is.False);
        }

        [Test]
        public void Drop_shadow_filter_paints_offset_alpha_under_foreground() {
            var ras = new SoftwareRasterizer(60, 60);
            ras.Clear(255, 255, 255, 255);
            var bounds = new Rect(0, 0, 60, 60);
            var ds = new DropShadowFilter(8, 8, 0, LinearColor.Black);
            var chain = new FilterChain(new FilterFunction[] { ds });
            ras.Submit(new PushFilterCommand(bounds, chain));
            // Red rect over white background so the diff registers as "painted".
            ras.Submit(new FillRectCommand(new Rect(10, 10, 20, 20),
                Brush.SolidColor(new LinearColor(1f, 0f, 0f, 1f)), BorderRadii.Zero));
            ras.Submit(new PopFilterCommand());
            // Rect at (10,10..30,30); shadow offset (+8,+8) → shadow stamp covers
            // (18,18..38,38). (35, 35) is in the halo, outside the foreground.
            byte shadowR = ras.Pixels[(35 * 60 + 35) * 4 + 0];
            byte rectR = ras.Pixels[(20 * 60 + 20) * 4 + 0];
            byte outsideR = ras.Pixels[(5 * 60 + 5) * 4 + 0];
            Assert.That(shadowR, Is.LessThan(200));
            Assert.That(rectR, Is.GreaterThan(200));
            Assert.That(outsideR, Is.EqualTo(255));
        }

        [Test]
        public void Chained_filters_preserve_drop_shadow_order() {
            var s = Style();
            s.Set("background-color", "white");
            s.Set("filter", "blur(2px) drop-shadow(2px 2px 0 black)");
            var cmds = Convert(Box(40, 40, s));
            var pf = (PushFilterCommand)cmds.Find(c => c is PushFilterCommand);
            Assert.That(pf.Filters.Functions.Count, Is.EqualTo(2));
            Assert.That(pf.Filters.Functions[0], Is.InstanceOf<BlurFilter>());
            Assert.That(pf.Filters.Functions[1], Is.InstanceOf<DropShadowFilter>());
        }

        [Test]
        public void Drop_shadow_with_fully_transparent_content_paints_no_shadow() {
            var ras = new SoftwareRasterizer(40, 40);
            ras.Clear(255, 255, 255, 255);
            var bounds = new Rect(0, 0, 40, 40);
            var ds = new DropShadowFilter(4, 4, 0, LinearColor.Black);
            var chain = new FilterChain(new FilterFunction[] { ds });
            ras.Submit(new PushFilterCommand(bounds, chain));
            // Nothing painted inside the scope.
            ras.Submit(new PopFilterCommand());
            // Frame buffer still all white.
            for (int i = 0; i < ras.Pixels.Length; i += 4) {
                Assert.That(ras.Pixels[i + 0], Is.EqualTo(255));
            }
        }

        // ----- Multi-layer backgrounds -----

        [Test]
        public void Two_layer_background_emits_two_fill_commands() {
            // RECALIBRATED: a fully-opaque TOP layer triggers the occlusion
            // skip (lower layers are invisible — emitting them double-AAs the
            // silhouette into a gray corner fringe; see the occlusion-skip
            // block in BoxToPaintConverter). A semi-transparent top layer
            // keeps every layer visible, which is what multi-emit pins.
            var s = Style();
            s.Set("background-image", "linear-gradient(rgba(255,0,0,0.5), rgba(0,0,255,0.5)), linear-gradient(white, black)");
            var cmds = Convert(Box(100, 100, s));
            int count = 0;
            foreach (var c in cmds) if (c is FillRectCommand) count++;
            Assert.That(count, Is.EqualTo(2));
        }

        [Test]
        public void Opaque_top_layer_occludes_lower_layers_into_one_fill() {
            // Pin the occlusion skip itself: an opaque covering top layer
            // hides everything beneath — exactly one fill emits, and it is
            // the TOP (first-declared) layer.
            var s = Style();
            s.Set("background-image", "linear-gradient(red, blue), linear-gradient(white, black)");
            var cmds = Convert(Box(100, 100, s));
            FillRectCommand only = null;
            int count = 0;
            foreach (var c in cmds) if (c is FillRectCommand fr) { count++; only = fr; }
            Assert.That(count, Is.EqualTo(1), "opaque top layer occludes the rest");
            var g = (LinearGradient)only.Brush.GradientValue;
            Assert.That(g.Stops[0].Color.R, Is.GreaterThan(g.Stops[0].Color.B),
                "the surviving fill is the TOP layer (red gradient)");
        }

        [Test]
        public void Background_blend_mode_disables_the_occlusion_skip() {
            // background-blend-mode blends each layer with those beneath, so
            // a lower layer contributes even under an opaque top — the skip
            // must stay off (radar minimap-bg regression).
            var s = Style();
            s.Set("background-image", "linear-gradient(red, blue), linear-gradient(white, black)");
            s.Set("background-blend-mode", "overlay");
            var cmds = Convert(Box(100, 100, s));
            int count = 0;
            foreach (var c in cmds) if (c is FillRectCommand) count++;
            Assert.That(count, Is.EqualTo(2), "blend mode keeps every layer");
        }

        [Test]
        public void Three_layer_background_emits_three_fills() {
            // RECALIBRATED: semi-transparent upper layers so the occlusion
            // skip (see Two_layer_background_emits_two_fill_commands) keeps
            // all three visible layers.
            var s = Style();
            s.Set("background-image", "linear-gradient(rgba(255,0,0,0.5), rgba(0,0,255,0.5)), linear-gradient(rgba(255,255,255,0.5), rgba(0,0,0,0.5)), linear-gradient(green, yellow)");
            s.Set("background-color", "transparent");
            var cmds = Convert(Box(50, 50, s));
            int count = 0;
            foreach (var c in cmds) if (c is FillRectCommand) count++;
            Assert.That(count, Is.EqualTo(3));
        }

        [Test]
        public void First_layer_paints_last_so_it_ends_up_on_top() {
            // RECALIBRATED: semi-transparent top layer so the occlusion skip
            // doesn't collapse the pair — the ORDER contract is what this
            // test pins (first-declared paints last → on top).
            var s = Style();
            s.Set("background-image", "linear-gradient(rgba(255,0,0,0.5), rgba(255,0,0,0.5)), linear-gradient(blue, blue)");
            var cmds = Convert(Box(50, 50, s));
            // Two FillRectCommand entries; the LAST one is the topmost CSS layer (red).
            FillRectCommand first = null;
            FillRectCommand last = null;
            foreach (var c in cmds) {
                if (c is FillRectCommand fr) {
                    if (first == null) first = fr;
                    last = fr;
                }
            }
            Assert.That(first, Is.Not.Null);
            Assert.That(last, Is.Not.Null);
            Assert.That(first, Is.Not.SameAs(last));
            // Red (first declared, painted last → on top) should have R > B.
            var topGradient = (LinearGradient)last.Brush.GradientValue;
            Assert.That(topGradient.Stops[0].Color.R, Is.GreaterThan(topGradient.Stops[0].Color.B));
        }

        [Test]
        public void Image_layer_plus_color_layer_emits_both() {
            // RECALIBRATED: semi-transparent gradient so the occlusion skip
            // doesn't hide the color layer beneath it.
            var s = Style();
            s.Set("background-image", "linear-gradient(rgba(255,0,0,0.5), rgba(0,0,255,0.5)), none");
            s.Set("background-color", "yellow");
            var cmds = Convert(Box(50, 50, s));
            int count = 0;
            foreach (var c in cmds) if (c is FillRectCommand) count++;
            // Three layers: image, none (skipped), color. The "none" layer is skipped
            // because no brush parses; the color is only an additional fill if it's
            // non-transparent. So 2 fill commands (gradient + color background).
            Assert.That(count, Is.EqualTo(2));
        }

        [Test]
        public void Color_only_emits_one_fill() {
            var s = Style();
            s.Set("background-color", "red");
            var cmds = Convert(Box(50, 50, s));
            int count = 0;
            foreach (var c in cmds) if (c is FillRectCommand) count++;
            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void Background_layers_resolver_returns_layer_per_image() {
            var s = Style();
            s.Set("background-image", "linear-gradient(red, blue), linear-gradient(white, black)");
            s.Set("background-color", "yellow");
            var layers = BackgroundResolver.ResolveBackgroundLayers(s, new Rect(0, 0, 50, 50));
            Assert.That(layers.Count, Is.EqualTo(3));
            Assert.That(layers[0], Is.Not.Null);
            Assert.That(layers[0].Kind, Is.EqualTo(BrushKind.Gradient));
            Assert.That(layers[1], Is.Not.Null);
            Assert.That(layers[1].Kind, Is.EqualTo(BrushKind.Gradient));
            Assert.That(layers[2], Is.Not.Null);
            Assert.That(layers[2].Kind, Is.EqualTo(BrushKind.SolidColor));
        }

        [Test]
        public void Background_shorthand_two_layer_url_then_color_expands() {
            var d = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var kv in new global::Weva.Css.Cascade.Shorthands.BackgroundShorthandExpander().Expand("url(a.png), red")) {
                d[kv.Key] = kv.Value;
            }
            Assert.That(d["background-color"], Is.EqualTo("red"));
            Assert.That(d["background-image"], Is.EqualTo("url(a.png), none"));
        }
    }
}
