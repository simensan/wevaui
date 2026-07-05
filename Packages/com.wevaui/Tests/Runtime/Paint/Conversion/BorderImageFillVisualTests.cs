using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Paint.Images;

namespace Weva.Tests.Paint.Conversion {
    // §2.3 — border-image-slice fill keyword visual behavior.
    // CSS Backgrounds 3 §6.1: the optional `fill` keyword in
    // border-image-slice causes the center slice of the source image to
    // also be painted into the center destination area, in addition to the
    // 8 edge/corner parts that are always emitted. Without `fill` the
    // center is discarded. These tests pin the paint-command geometry so
    // any regression in the center-fill emission path surfaces immediately.
    public class BorderImageFillVisualTests {
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

        static List<BorderImageResolver.BorderImagePart> Resolve(
            string sliceCss,
            string repeatCss = "stretch",
            int srcW = 64, int srcH = 64,
            double box = 200, double border = 16) {
            var bx = MakeBox(box, box, border);
            var style = new ComputedStyle(new Element("div"));
            style.Set("border-image-source", "url(frame)");
            style.Set("border-image-slice", sliceCss);
            style.Set("border-image-repeat", repeatCss);
            var reg = new InMemoryImageRegistry();
            reg.Register("frame", new StubSource(srcW, srcH));
            var parts = new List<BorderImageResolver.BorderImagePart>();
            BorderImageResolver.Resolve(style, bx, reg, parts);
            return parts;
        }

        // --- Default (no fill): only 8 parts; center is NOT painted ---

        [Test]
        public void No_fill_emits_exactly_8_parts() {
            // Without the `fill` keyword the center region of the source
            // image is discarded — only 4 corners + 4 edges are returned.
            var parts = Resolve("16");
            Assert.That(parts.Count, Is.EqualTo(8),
                "Without fill, only corners and edges should be emitted (center discarded)");
        }

        [Test]
        public void No_fill_none_of_the_8_parts_covers_center_destination() {
            // Verify that no emitted part's DestRect sits entirely within
            // the center cell. With a 200x200 box and 16px border-width
            // the center cell is (16, 16, 168, 168) in box-local coords.
            // Any part that is NOT a corner or edge must land in this area.
            // The 8 parts should only touch the border-width band.
            var parts = Resolve("16", box: 200, border: 16);
            bool hasCenter = false;
            for (int i = 0; i < parts.Count; i++) {
                var d = parts[i].DestRect;
                // A "center cell" part would have X > 0 AND Y > 0 AND
                // X + Width < box AND Y + Height < box AND not touch any edge.
                bool innerX = d.X > 0 && d.X + d.Width < 200;
                bool innerY = d.Y > 0 && d.Y + d.Height < 200;
                bool touchesTopEdge = d.Y <= 0;
                bool touchesBottomEdge = d.Y + d.Height >= 200;
                bool touchesLeftEdge = d.X <= 0;
                bool touchesRightEdge = d.X + d.Width >= 200;
                bool isCenterCandidate = innerX && innerY
                    && !touchesTopEdge && !touchesBottomEdge
                    && !touchesLeftEdge && !touchesRightEdge;
                if (isCenterCandidate) { hasCenter = true; break; }
            }
            Assert.That(hasCenter, Is.False,
                "Without fill no part should occupy the interior center cell");
        }

        // --- fill keyword: 9th part IS emitted with correct geometry ---

        [Test]
        public void Fill_keyword_emits_9_parts() {
            // `16 fill` on a 64x64 source: 8 edge/corner parts + 1 center.
            var parts = Resolve("16 fill", srcW: 64, srcH: 64);
            Assert.That(parts.Count, Is.EqualTo(9));
        }

        [Test]
        public void Fill_center_dest_rect_occupies_inner_box_area() {
            // With 16px uniform slice on a 200x200 box the border-image-width
            // defaults to the border-width (16px). The center dest rect must
            // be (16, 16, 168, 168) in box-local coords.
            var parts = Resolve("16 fill", srcW: 64, srcH: 64, box: 200, border: 16);
            var center = parts[8];
            Assert.That(center.DestRect.X, Is.EqualTo(16).Within(1e-4),
                "Center dest X should start at border-width (16)");
            Assert.That(center.DestRect.Y, Is.EqualTo(16).Within(1e-4),
                "Center dest Y should start at border-width (16)");
            Assert.That(center.DestRect.Width, Is.EqualTo(168).Within(1e-4),
                "Center dest Width = box - 2*border = 200 - 32 = 168");
            Assert.That(center.DestRect.Height, Is.EqualTo(168).Within(1e-4),
                "Center dest Height = box - 2*border = 200 - 32 = 168");
        }

        [Test]
        public void Fill_center_source_uv_is_inner_image_region() {
            // On a 64x64 source with a 16px (0.25) slice the center UV
            // occupies (0.25, 0.25, 0.5, 0.5) before SI inset; after the
            // half-texel SI inset (tu=tv=0.5/64=0.0078125):
            //   X = u0 + tu, Y (V-flip, bottom-up) = midV + tv = wB + tv = 0.25 + tu,
            //   W = 0.5 - 2*tu, H = 0.5 - 2*tv.
            var parts = Resolve("16 fill", srcW: 64, srcH: 64);
            var center = parts[8];
            double tu = 0.5 / 64;
            Assert.That(center.SourceRect.X, Is.EqualTo(0.25 + tu).Within(1e-6),
                "Center UV X = u0 + tu = 0.25 + tu");
            Assert.That(center.SourceRect.Y, Is.EqualTo(0.25 + tu).Within(1e-6),
                "Center UV Y (V-flip) = midV + tv = wB + tv = 0.25 + tu");
            Assert.That(center.SourceRect.Width, Is.EqualTo(0.5 - 2 * tu).Within(1e-6),
                "Center UV Width = 0.5 - 2*tu");
            Assert.That(center.SourceRect.Height, Is.EqualTo(0.5 - 2 * tu).Within(1e-6),
                "Center UV Height = 0.5 - 2*tv");
        }

        // --- Per-side slice + fill ---

        [Test]
        public void Four_value_slice_with_fill_center_uv_reflects_asymmetric_slices() {
            // `10 20 30 40 fill` on 100x100: top=10, right=20, bottom=30, left=40.
            // Raw center UV: X=0.4, Y(V-flip)=midV=wB=0.3, W=0.4, H=0.6.
            // After SI inset (tu=tv=0.5/100=0.005): X+tu, Y+tv, W-2*tu, H-2*tv.
            var parts = Resolve("10 20 30 40 fill", srcW: 100, srcH: 100,
                                box: 300, border: 40);
            Assert.That(parts.Count, Is.EqualTo(9));
            var center = parts[8];
            double tu = 0.5 / 100;
            Assert.That(center.SourceRect.X, Is.EqualTo(0.4 + tu).Within(1e-6),
                "UV X = u0 + tu = 40/100 + tu");
            // V-flip: midV = wB = sBottom/srcH = 30/100 = 0.3; after inset: 0.3 + tu.
            Assert.That(center.SourceRect.Y, Is.EqualTo(0.3 + tu).Within(1e-6),
                "UV Y (V-flip) = midV + tv = wB + tv = 0.3 + tu");
            Assert.That(center.SourceRect.Width, Is.EqualTo(0.4 - 2 * tu).Within(1e-6),
                "UV W = (100-40-20)/100 - 2*tu = 0.4 - 2*tu");
            Assert.That(center.SourceRect.Height, Is.EqualTo(0.6 - 2 * tu).Within(1e-6),
                "UV H = (100-10-30)/100 - 2*tv = 0.6 - 2*tu");
        }

        // --- Percentage slice + fill ---

        [Test]
        public void Percentage_slice_with_fill_center_uv_resolves_against_image_size() {
            // `25% fill` on a 100x100 source: each slice = 25px → raw UV 0.25.
            // After SI inset (tu=0.5/100): X=0.25+tu, Y(V-flip)=midV+tv=0.25+tu,
            // W=0.5-2*tu, H=0.5-2*tv.
            var parts = Resolve("25% fill", srcW: 100, srcH: 100, box: 200, border: 25);
            Assert.That(parts.Count, Is.EqualTo(9));
            var center = parts[8];
            double tu = 0.5 / 100;
            Assert.That(center.SourceRect.X, Is.EqualTo(0.25 + tu).Within(1e-6));
            Assert.That(center.SourceRect.Y, Is.EqualTo(0.25 + tu).Within(1e-6));
            Assert.That(center.SourceRect.Width, Is.EqualTo(0.5 - 2 * tu).Within(1e-6));
            Assert.That(center.SourceRect.Height, Is.EqualTo(0.5 - 2 * tu).Within(1e-6));
        }

        // --- Fill with repeat ---

        [Test]
        public void Fill_with_repeat_center_has_non_null_tile() {
            // When border-image-repeat is `repeat` and fill is active the
            // center patch should carry a BackgroundTile that tiles on both
            // axes (not null like a stretched center).
            var parts = Resolve("16 fill", repeatCss: "repeat", srcW: 64, srcH: 64,
                                box: 200, border: 16);
            Assert.That(parts.Count, Is.EqualTo(9));
            var center = parts[8];
            Assert.That(center.Tile, Is.Not.Null,
                "With repeat mode the center fill should carry a BackgroundTile");
            Assert.That(center.Tile.Value.RepeatX, Is.EqualTo(BackgroundRepeatMode.Repeat),
                "Center tile RepeatX should be Repeat when repeat mode is active");
            Assert.That(center.Tile.Value.RepeatY, Is.EqualTo(BackgroundRepeatMode.Repeat),
                "Center tile RepeatY should be Repeat when repeat mode is active");
        }
    }
}
