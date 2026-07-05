using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Cascade.Shorthands;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Paint.Images;

namespace Weva.Tests.Paint.Conversion {
    // Edge-case coverage on top of BorderImageResolverTests. Each test
    // exercises one slot of the CSS Backgrounds 3 §6 grammar: percent
    // slices, the `fill` keyword, the `outset` and `width` longhands,
    // every repeat-mode permutation, gradient sources, and the
    // shorthand path through ShorthandRegistry. Tests that still
    // surface a v1 gap (`round`/`space` shader collapse, gradient
    // sources) document the observed behaviour with a `// v1:` comment
    // so a future engine change has a single grep target.
    public class BorderImageEdgeCasesTests {
        sealed class StubSource : IImageSource {
            public StubSource(int w, int h) { Width = w; Height = h; }
            public int Width { get; }
            public int Height { get; }
        }

        static BlockBox MakeBox(double w, double h, double border) {
            var bb = new BlockBox();
            bb.Width = w; bb.Height = h;
            bb.BorderLeft = border; bb.BorderTop = border;
            bb.BorderRight = border; bb.BorderBottom = border;
            return bb;
        }

        static (List<BorderImageResolver.BorderImagePart> parts, IImageRegistry reg)
        Resolve(
            string sliceCss = "16",
            string repeatCss = "stretch",
            string widthCss = null,
            string outsetCss = null,
            string sourceCss = "url(frame)",
            int srcW = 64, int srcH = 64,
            double box = 200, double border = 16
        ) {
            var bx = MakeBox(box, box, border);
            var style = new ComputedStyle(new Element("div"));
            style.Set("border-image-source", sourceCss);
            style.Set("border-image-slice", sliceCss);
            style.Set("border-image-repeat", repeatCss);
            if (widthCss != null) style.Set("border-image-width", widthCss);
            if (outsetCss != null) style.Set("border-image-outset", outsetCss);
            var reg = new InMemoryImageRegistry();
            reg.Register("frame", new StubSource(srcW, srcH));
            var parts = new List<BorderImageResolver.BorderImagePart>();
            BorderImageResolver.Resolve(style, bx, reg, parts);
            return (parts, reg);
        }

        // Counts parts whose DestRect has nonzero area. EmitEdge skips
        // zero-area edges entirely, so the corner-only frame produces 4.
        static int NonZeroParts(List<BorderImageResolver.BorderImagePart> parts) {
            int n = 0;
            foreach (var p in parts) {
                if (p.DestRect.Width > 0 && p.DestRect.Height > 0) n++;
            }
            return n;
        }

        // ---------- border-image-slice ----------

        [Test]
        public void Border_image_slice_single_pixel_value_applies_to_all_sides() {
            // `16` on a 64×64 source → raw UV slice = 16/64 = 0.25; after half-texel
            // SI inset (tu=0.5/64): width = 0.25 - 2*tu, height = 0.25 - 2*tv.
            var (parts, _) = Resolve("16", srcW: 64, srcH: 64, box: 200, border: 16);
            Assert.That(parts.Count, Is.EqualTo(8));
            double tu = 0.5 / 64;
            for (int i = 0; i < 4; i++) {
                Assert.That(parts[i].SourceRect.Width, Is.EqualTo(0.25 - 2 * tu).Within(1e-6),
                    $"corner {i} source width");
                Assert.That(parts[i].SourceRect.Height, Is.EqualTo(0.25 - 2 * tu).Within(1e-6),
                    $"corner {i} source height");
            }
        }

        [Test]
        public void Border_image_slice_two_values_expands_top_horizontal_then_bottom_vertical() {
            // CSS: two values = top/bottom + left/right (vertical first,
            // then horizontal). `16 8` on a 64×64 source → top/bottom slice
            // = 16/64 = 0.25, left/right slice = 8/64 = 0.125.
            // After SI inset (tu=0.5/64): width = 0.125 - 2*tu, height = 0.25 - 2*tv.
            var (parts, _) = Resolve("16 8", srcW: 64, srcH: 64);
            double tu = 0.5 / 64;
            // Top-left corner inherits left/right slice on width, top/bottom slice on height.
            Assert.That(parts[0].SourceRect.Width, Is.EqualTo(0.125 - 2 * tu).Within(1e-6));
            Assert.That(parts[0].SourceRect.Height, Is.EqualTo(0.25 - 2 * tu).Within(1e-6));
        }

        [Test]
        public void Border_image_slice_four_values_explicit_sides() {
            // CSS: four values = top right bottom left. On a 100×100 source:
            // top=1/100, right=2/100, bottom=3/100, left=4/100.
            // After SI inset (tu=tv=0.5/100=0.005): width/height each shrink by 2*tu.
            var (parts, _) = Resolve("1 2 3 4", srcW: 100, srcH: 100);
            double tu = 0.5 / 100;
            // Top-left corner: left (4) × top (1).
            Assert.That(parts[0].SourceRect.Width, Is.EqualTo(0.04 - 2 * tu).Within(1e-6));
            Assert.That(parts[0].SourceRect.Height, Is.EqualTo(0.01 - 2 * tu).Within(1e-6));
            // Top-right corner: right (2) × top (1).
            Assert.That(parts[1].SourceRect.Width, Is.EqualTo(0.02 - 2 * tu).Within(1e-6));
            Assert.That(parts[1].SourceRect.Height, Is.EqualTo(0.01 - 2 * tu).Within(1e-6));
            // Bottom-right corner: right (2) × bottom (3).
            Assert.That(parts[2].SourceRect.Width, Is.EqualTo(0.02 - 2 * tu).Within(1e-6));
            Assert.That(parts[2].SourceRect.Height, Is.EqualTo(0.03 - 2 * tu).Within(1e-6));
            // Bottom-left corner: left (4) × bottom (3).
            Assert.That(parts[3].SourceRect.Width, Is.EqualTo(0.04 - 2 * tu).Within(1e-6));
            Assert.That(parts[3].SourceRect.Height, Is.EqualTo(0.03 - 2 * tu).Within(1e-6));
        }

        [Test]
        public void Border_image_slice_percent_resolves_against_image_dimensions() {
            // 25% on a 100×100 source = 25px slice on each side, normalised
            // to [0, 1] = 0.25; after SI inset (tu=0.5/100=0.005): 0.25-2*tu.
            var (parts, _) = Resolve("25%", srcW: 100, srcH: 100, box: 200, border: 25);
            double tu = 0.5 / 100;
            for (int i = 0; i < 4; i++) {
                Assert.That(parts[i].SourceRect.Width, Is.EqualTo(0.25 - 2 * tu).Within(1e-6));
                Assert.That(parts[i].SourceRect.Height, Is.EqualTo(0.25 - 2 * tu).Within(1e-6));
            }
        }

        [Test]
        public void Border_image_slice_fill_keyword_keeps_middle() {
            // Per CSS Backgrounds 3 §6.1, the optional `fill` keyword
            // preserves the middle slice as a 9th painted part (the
            // default is to discard it).
            var (parts, _) = Resolve("16 fill", srcW: 64, srcH: 64);
            // 4 corners + 4 edges + 1 center = 9 parts.
            Assert.That(parts.Count, Is.EqualTo(9));
            double tu = 0.5 / 64;
            // Corners must still slice correctly — raw 0.25, after inset: 0.25-2*tu.
            Assert.That(parts[0].SourceRect.Width, Is.EqualTo(0.25 - 2 * tu).Within(1e-6));
            // 9th part is the center: source UV rect spans the central
            // (1 - 2*slice/srcSize) = 0.5 of each axis, after SI inset.
            var center = parts[8];
            // Center X starts at u0 + tu = 0.25 + tu.
            Assert.That(center.SourceRect.X, Is.EqualTo(0.25 + tu).Within(1e-6));
            // Center Y (bottom-up V-flip): midV + tv = wB + tv = 0.25 + tu.
            Assert.That(center.SourceRect.Y, Is.EqualTo(0.25 + tu).Within(1e-6));
            Assert.That(center.SourceRect.Width, Is.EqualTo(0.5 - 2 * tu).Within(1e-6));
            Assert.That(center.SourceRect.Height, Is.EqualTo(0.5 - 2 * tu).Within(1e-6));
        }

        // ---------- border-image-width ----------

        [Test]
        public void Border_image_width_length_round_trips() {
            // Per CSS Backgrounds 3 §6.4, a length value sets the
            // border-image dest edge thickness independently of the
            // regular border-width.
            var (parts, _) = Resolve("16", widthCss: "8px", border: 16);
            Assert.That(parts.Count, Is.EqualTo(8));
            // 8px width → top-left corner is 8x8 (overriding the 16px border).
            Assert.That(parts[0].DestRect.Width, Is.EqualTo(8));
            Assert.That(parts[0].DestRect.Height, Is.EqualTo(8));
        }

        [Test]
        public void Border_image_width_percent_resolves_against_border_box() {
            // Per spec, percentages on border-image-width resolve against
            // the border-image area (= border-box + outset). On a 200px
            // box with zero outset, 25% → 50px.
            var (parts, _) = Resolve("16", widthCss: "25%", box: 200, border: 16);
            Assert.That(parts.Count, Is.EqualTo(8));
            Assert.That(parts[0].DestRect.Width, Is.EqualTo(50).Within(1e-6));
        }

        [Test]
        public void Border_image_width_auto_falls_back_to_slice_value() {
            // Per spec, `border-image-width: auto` uses the slice value
            // (resolved as source pixels) as the dest edge thickness.
            var (parts, _) = Resolve("16", widthCss: "auto", border: 8);
            Assert.That(parts.Count, Is.EqualTo(8));
            // auto → slice (16 source pixels) per spec.
            Assert.That(parts[0].DestRect.Width, Is.EqualTo(16));
        }

        [Test]
        public void Border_image_width_number_multiplies_border_width() {
            // Per spec, a unitless number multiplies the border-width:
            // `border-image-width: 2` on a 16px border = 32px-thick image.
            var (parts, _) = Resolve("16", widthCss: "2", border: 16);
            Assert.That(parts.Count, Is.EqualTo(8));
            Assert.That(parts[0].DestRect.Width, Is.EqualTo(32));
        }

        // ---------- border-image-outset ----------

        [Test]
        public void Border_image_outset_pushes_image_outside_border_box() {
            // Per CSS Backgrounds 3 §6.3, `border-image-outset: 4px`
            // extends the image area 4px beyond the border-box on every
            // side. The top-left corner DestRect origin shifts to
            // (-4, -4) and the bottom-right corner's right/bottom edges
            // land at boxW+4 / boxH+4.
            var (parts, _) = Resolve("16", outsetCss: "4px", border: 16, box: 200);
            Assert.That(parts.Count, Is.EqualTo(8));
            Assert.That(parts[0].DestRect.X, Is.EqualTo(-4));
            Assert.That(parts[0].DestRect.Y, Is.EqualTo(-4));
        }

        [Test]
        public void Border_image_outset_zero_is_default() {
            // No outset set → image area aligns with border-box.
            var (parts, _) = Resolve("16", border: 16, box: 200);
            Assert.That(parts.Count, Is.EqualTo(8));
            // Top-left corner at (0, 0); bottom-right at (200 - 16, 200 - 16).
            Assert.That(parts[0].DestRect.X, Is.EqualTo(0));
            Assert.That(parts[0].DestRect.Y, Is.EqualTo(0));
            Assert.That(parts[2].DestRect.X, Is.EqualTo(184));
            Assert.That(parts[2].DestRect.Y, Is.EqualTo(184));
        }

        // ---------- border-image-repeat ----------

        [Test]
        public void Border_image_repeat_stretch_default() {
            // Default repeat = stretch → every edge part has Tile=null
            // (the renderer treats a null tile as full-bleed stretch).
            var (parts, _) = Resolve("16");
            for (int i = 4; i < 8; i++) {
                Assert.That(parts[i].Tile, Is.Null, $"part {i} should be stretch");
            }
        }

        [Test]
        public void Border_image_repeat_repeat_keyword_round_trips() {
            var (parts, _) = Resolve("16", repeatCss: "repeat");
            // Top edge tiles along X (horizontal), no repeat on Y.
            Assert.That(parts[4].Tile, Is.Not.Null);
            Assert.That(parts[4].Tile.Value.RepeatX, Is.EqualTo(BackgroundRepeatMode.Repeat));
            Assert.That(parts[4].Tile.Value.RepeatY, Is.EqualTo(BackgroundRepeatMode.NoRepeat));
            // Right edge tiles along Y (vertical).
            Assert.That(parts[5].Tile, Is.Not.Null);
            Assert.That(parts[5].Tile.Value.RepeatX, Is.EqualTo(BackgroundRepeatMode.NoRepeat));
            Assert.That(parts[5].Tile.Value.RepeatY, Is.EqualTo(BackgroundRepeatMode.Repeat));
        }

        [Test]
        public void Border_image_repeat_round_keyword_round_trips() {
            // `round` now scales tile size so a whole number of tiles fills
            // the edge. The tile must be non-null with RepeatX=Repeat for the
            // horizontal top edge, and the TileWidth must evenly divide dCenterW.
            var (parts, _) = Resolve("16", repeatCss: "round", srcW: 64, srcH: 64,
                                     box: 200, border: 16);
            Assert.That(parts[4].Tile, Is.Not.Null);
            Assert.That(parts[4].Tile.Value.RepeatX, Is.EqualTo(BackgroundRepeatMode.Repeat));
            // dCenterW = 200 - 16 - 16 = 168, naturalTile = 64 - 16 - 16 = 32.
            // tileCount = round(168/32) = round(5.25) = 5; tileLen = 168/5 = 33.6.
            double edgeLen = 200 - 16 - 16;  // 168
            double tileW = parts[4].Tile.Value.TileWidth;
            double n = edgeLen / tileW;
            Assert.That(n, Is.EqualTo(System.Math.Round(n)).Within(1e-4),
                "round: TileWidth must evenly divide edge length");
        }

        [Test]
        public void Border_image_repeat_two_values_horizontal_then_vertical() {
            // CSS: two values = horizontal then vertical. `repeat round` →
            // X axis repeat, Y axis round. `repeat` tiles at natural source
            // length; `round` scales to fill an integer count.
            var (parts, _) = Resolve("16", repeatCss: "repeat round");
            // Top edge follows the X (horizontal) keyword = repeat.
            Assert.That(parts[4].Tile, Is.Not.Null);
            Assert.That(parts[4].Tile.Value.RepeatX, Is.EqualTo(BackgroundRepeatMode.Repeat));
            // Right edge follows the Y (vertical) keyword = round.
            Assert.That(parts[5].Tile, Is.Not.Null);
            Assert.That(parts[5].Tile.Value.RepeatY, Is.EqualTo(BackgroundRepeatMode.Repeat));
        }

        // ---------- border-image shorthand ----------

        [Test]
        public void Border_image_shorthand_url_slice_width_expands() {
            // The shorthand expander splits `border-image: <source>
            // <slice> [/ <width>] <repeat>` into the five longhands.
            bool registered = ShorthandRegistry.IsShorthand("border-image");
            Assert.That(registered, Is.True);
            // Spot-check the expansion: each longhand receives its slot.
            Assert.That(ShorthandRegistry.TryExpand("border-image",
                "url(frame) 16 / 8px stretch", out var longhands), Is.True);
            var d = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var kv in longhands) d[kv.Key] = kv.Value;
            Assert.That(d["border-image-source"], Is.EqualTo("url(frame)"));
            Assert.That(d["border-image-slice"], Is.EqualTo("16"));
            Assert.That(d["border-image-width"], Is.EqualTo("8px"));
            Assert.That(d["border-image-repeat"], Is.EqualTo("stretch"));
        }

        [Test]
        public void Border_image_shorthand_gradient_source() {
            // CSS allows the source to be a <gradient> in addition to
            // url() / none. v1 only honours url() — see
            // BorderImageResolver.TryResolveSourceHandle: "Function-call
            // sources (gradients) fall through — they'd render as a
            // textured edge atlas, which we don't model here yet."
            var bx = MakeBox(200, 200, 16);
            var style = new ComputedStyle(new Element("div"));
            style.Set("border-image-source", "linear-gradient(red, blue)");
            style.Set("border-image-slice", "16");
            style.Set("border-image-repeat", "stretch");
            var parts = new List<BorderImageResolver.BorderImagePart>();
            BorderImageResolver.Resolve(style, bx, new InMemoryImageRegistry(), parts);
            // v1: gradient sources unsupported — resolver returns empty.
            Assert.That(parts.Count, Is.EqualTo(0));
        }

        // ---------- border-image-source: none ----------

        [Test]
        public void Border_image_source_none_disables_image_rendering() {
            // `border-image-source: none` is the initial value and must
            // produce zero parts even when other longhands are set to
            // non-default values.
            var bx = MakeBox(200, 200, 16);
            var style = new ComputedStyle(new Element("div"));
            style.Set("border-image-source", "none");
            style.Set("border-image-slice", "16");
            style.Set("border-image-repeat", "repeat");
            var reg = new InMemoryImageRegistry();
            reg.Register("frame", new StubSource(64, 64));
            var parts = new List<BorderImageResolver.BorderImagePart>();
            BorderImageResolver.Resolve(style, bx, reg, parts);
            Assert.That(parts.Count, Is.EqualTo(0));
        }
    }
}
