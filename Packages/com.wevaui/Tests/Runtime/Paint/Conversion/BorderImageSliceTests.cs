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
    // Focused coverage for Bug #255 — the full CSS Backgrounds 3 §6
    // implementation now wired through the resolver and the shorthand
    // expander. Companion to BorderImageEdgeCasesTests; these tests pin
    // the resolved geometry on success paths (rather than documenting
    // v1 gaps) so regressions in the slice/width/outset/repeat/fill
    // arithmetic surface immediately.
    public class BorderImageSliceTests {
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

        // ---------- §6.1 border-image-slice ----------

        [Test]
        public void Slice_single_value_applies_to_all_four_sides() {
            // `16` on 64x64 → 0.25 UV slice on every side; after half-texel
            // SI inset (tu=0.5/64): width/height = 0.25 - 2*tu.
            var (parts, _) = Resolve("16", srcW: 64, srcH: 64);
            double tu = 0.5 / 64;
            for (int i = 0; i < 4; i++) {
                Assert.That(parts[i].SourceRect.Width, Is.EqualTo(0.25 - 2 * tu).Within(1e-6));
                Assert.That(parts[i].SourceRect.Height, Is.EqualTo(0.25 - 2 * tu).Within(1e-6));
            }
        }

        [Test]
        public void Slice_four_values_map_to_top_right_bottom_left() {
            // `1 2 3 4` on 100x100 → top=0.01, right=0.02, bottom=0.03,
            // left=0.04 in UV space. Top-left corner uses (left, top).
            // After SI inset (tu=tv=0.5/100=0.005): each dimension shrinks by 2*tu.
            var (parts, _) = Resolve("1 2 3 4", srcW: 100, srcH: 100);
            double tu = 0.5 / 100;
            Assert.That(parts[0].SourceRect.Width, Is.EqualTo(0.04 - 2 * tu).Within(1e-6));
            Assert.That(parts[0].SourceRect.Height, Is.EqualTo(0.01 - 2 * tu).Within(1e-6));
            // Bottom-right corner uses (right, bottom).
            Assert.That(parts[2].SourceRect.Width, Is.EqualTo(0.02 - 2 * tu).Within(1e-6));
            Assert.That(parts[2].SourceRect.Height, Is.EqualTo(0.03 - 2 * tu).Within(1e-6));
        }

        [Test]
        public void Slice_percent_resolves_against_image_dimensions() {
            // 25% on 100x100 source → 25px slice → 0.25 UV; after SI inset: 0.25-2*tu.
            var (parts, _) = Resolve("25%", srcW: 100, srcH: 100, box: 200, border: 25);
            double tu = 0.5 / 100;
            for (int i = 0; i < 4; i++) {
                Assert.That(parts[i].SourceRect.Width, Is.EqualTo(0.25 - 2 * tu).Within(1e-6));
                Assert.That(parts[i].SourceRect.Height, Is.EqualTo(0.25 - 2 * tu).Within(1e-6));
            }
        }

        // ---------- §6.1 fill keyword ----------

        [Test]
        public void Slice_fill_emits_ninth_center_part() {
            // `fill` preserves the middle slice as a 9th painted part.
            var (parts, _) = Resolve("16 fill", srcW: 64, srcH: 64);
            Assert.That(parts.Count, Is.EqualTo(9));
            var center = parts[8];
            double tu = 0.5 / 64;
            // Center UV occupies the middle 0.25..0.75 region after SI inset.
            // Center X = u0 + tu = 0.25 + tu.
            // Center Y (V-flip, bottom-up): midV + tv = wB + tv = 0.25 + tu.
            Assert.That(center.SourceRect.X, Is.EqualTo(0.25 + tu).Within(1e-6));
            Assert.That(center.SourceRect.Y, Is.EqualTo(0.25 + tu).Within(1e-6));
            Assert.That(center.SourceRect.Width, Is.EqualTo(0.5 - 2 * tu).Within(1e-6));
            Assert.That(center.SourceRect.Height, Is.EqualTo(0.5 - 2 * tu).Within(1e-6));
        }

        [Test]
        public void Slice_fill_leading_keyword_still_parsed() {
            // CSS Backgrounds 3 §6.1 allows `fill` in any position; v1
            // placed it last conventionally but the parser must accept
            // leading-fill too.
            var (parts, _) = Resolve("fill 16", srcW: 64, srcH: 64);
            Assert.That(parts.Count, Is.EqualTo(9));
        }

        // ---------- §6.4 border-image-width ----------

        [Test]
        public void Width_length_overrides_border_width() {
            // 8px width replaces the rendered edge thickness independent
            // of the regular border-width.
            var (parts, _) = Resolve("16", widthCss: "8px", border: 16);
            Assert.That(parts[0].DestRect.Width, Is.EqualTo(8));
            Assert.That(parts[0].DestRect.Height, Is.EqualTo(8));
        }

        [Test]
        public void Width_number_multiplies_border_width() {
            // Unitless `2` on a 10px border → 20px-thick image edges.
            var (parts, _) = Resolve("16", widthCss: "2", border: 10);
            Assert.That(parts[0].DestRect.Width, Is.EqualTo(20));
        }

        [Test]
        public void Width_auto_uses_slice_pixels() {
            // `auto` falls back to the source-pixel slice value (16).
            var (parts, _) = Resolve("16", widthCss: "auto", border: 8);
            Assert.That(parts[0].DestRect.Width, Is.EqualTo(16));
        }

        [Test]
        public void Width_four_values_set_each_side() {
            // 1px 2px 3px 4px → top 1, right 2, bottom 3, left 4.
            var (parts, _) = Resolve("16", widthCss: "1px 2px 3px 4px", border: 16, box: 200);
            // Top-left corner sized by (left=4, top=1).
            Assert.That(parts[0].DestRect.Width, Is.EqualTo(4));
            Assert.That(parts[0].DestRect.Height, Is.EqualTo(1));
            // Bottom-right corner sized by (right=2, bottom=3).
            Assert.That(parts[2].DestRect.Width, Is.EqualTo(2));
            Assert.That(parts[2].DestRect.Height, Is.EqualTo(3));
        }

        // ---------- §6.3 border-image-outset ----------

        [Test]
        public void Outset_shifts_image_area_outside_border_box() {
            // 4px outset → top-left corner origin is (-4, -4) and the
            // image area grows to (boxW + 8, boxH + 8).
            var (parts, _) = Resolve("16", outsetCss: "4px", border: 16, box: 200);
            Assert.That(parts[0].DestRect.X, Is.EqualTo(-4));
            Assert.That(parts[0].DestRect.Y, Is.EqualTo(-4));
            // Bottom-right corner sits at (200 - 16 + 4, 200 - 16 + 4) = (188, 188).
            Assert.That(parts[2].DestRect.X, Is.EqualTo(188));
            Assert.That(parts[2].DestRect.Y, Is.EqualTo(188));
        }

        [Test]
        public void Outset_number_multiplies_border_width() {
            // Per spec, a unitless number multiplies the corresponding
            // border-width: outset=2 on a 4px border → 8px outset.
            var (parts, _) = Resolve("16", outsetCss: "2", border: 4, box: 200);
            Assert.That(parts[0].DestRect.X, Is.EqualTo(-8));
            Assert.That(parts[0].DestRect.Y, Is.EqualTo(-8));
        }

        // ---------- §6.5 border-image-repeat ----------

        [Test]
        public void Repeat_stretch_emits_null_tile() {
            // Default stretch → every edge part has Tile == null.
            var (parts, _) = Resolve("16", repeatCss: "stretch");
            for (int i = 4; i < 8; i++) {
                Assert.That(parts[i].Tile, Is.Null);
            }
        }

        [Test]
        public void Repeat_repeat_tiles_edges() {
            // `repeat` → top/bottom edges tile X, left/right tile Y.
            var (parts, _) = Resolve("16", repeatCss: "repeat");
            Assert.That(parts[4].Tile, Is.Not.Null);
            Assert.That(parts[4].Tile.Value.RepeatX, Is.EqualTo(BackgroundRepeatMode.Repeat));
            Assert.That(parts[5].Tile, Is.Not.Null);
            Assert.That(parts[5].Tile.Value.RepeatY, Is.EqualTo(BackgroundRepeatMode.Repeat));
        }

        // ---------- §6.6 border-image shorthand ----------

        [Test]
        public void Shorthand_is_registered() {
            // The expander must be reachable from the cascade registry.
            Assert.That(ShorthandRegistry.IsShorthand("border-image"), Is.True);
        }

        [Test]
        public void Shorthand_full_form_expands_all_five_longhands() {
            // `border-image: url(frame) 16 fill / 8px / 4px stretch round`
            // exercises every slot: source, slice (+ fill), width, outset,
            // repeat (two-value form).
            Assert.That(ShorthandRegistry.TryExpand("border-image",
                "url(frame) 16 fill / 8px / 4px stretch round", out var longhands), Is.True);
            var d = new Dictionary<string, string>();
            foreach (var kv in longhands) d[kv.Key] = kv.Value;
            Assert.That(d["border-image-source"], Is.EqualTo("url(frame)"));
            Assert.That(d["border-image-slice"], Is.EqualTo("16 fill"));
            Assert.That(d["border-image-width"], Is.EqualTo("8px"));
            Assert.That(d["border-image-outset"], Is.EqualTo("4px"));
            Assert.That(d["border-image-repeat"], Is.EqualTo("stretch round"));
        }

        [Test]
        public void Shorthand_omitted_slots_fall_back_to_initial_values() {
            // Source-only shorthand: every other longhand reverts to its
            // initial value.
            Assert.That(ShorthandRegistry.TryExpand("border-image", "url(frame)", out var longhands), Is.True);
            var d = new Dictionary<string, string>();
            foreach (var kv in longhands) d[kv.Key] = kv.Value;
            Assert.That(d["border-image-source"], Is.EqualTo("url(frame)"));
            Assert.That(d["border-image-slice"], Is.EqualTo("100%"));
            Assert.That(d["border-image-width"], Is.EqualTo("1"));
            Assert.That(d["border-image-outset"], Is.EqualTo("0"));
            Assert.That(d["border-image-repeat"], Is.EqualTo("stretch"));
        }
    }
}
