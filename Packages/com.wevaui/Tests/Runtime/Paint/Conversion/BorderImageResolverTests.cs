using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Paint.Images;

namespace Weva.Tests.Paint.Conversion {
    // CSS Backgrounds 3 §6 9-slice resolver. Tests cover the typical
    // frame-shape (corners + edges, fixed slice insets) plus the
    // shorthand expansion of `border-image-slice` and the repeat
    // keyword normalisation.
    public class BorderImageResolverTests {
        sealed class StubSource : IImageSource {
            public StubSource(int w, int h) { Width = w; Height = h; }
            public int Width { get; }
            public int Height { get; }
        }

        sealed class NineSliceSource : IImageSource, IImageNineSliceSource {
            public int Width => 32;
            public int Height => 32;
            public bool TryGetNineSlice(out ImageNineSlice slice) {
                slice = new ImageNineSlice(top: 6, right: 10, bottom: 8, left: 4);
                return true;
            }
        }

        static BlockBox MakeBox(double w, double h, double border) {
            var bb = new BlockBox();
            bb.Width = w; bb.Height = h;
            bb.BorderLeft = border; bb.BorderTop = border;
            bb.BorderRight = border; bb.BorderBottom = border;
            return bb;
        }

        static (List<BorderImageResolver.BorderImagePart> parts, IImageRegistry reg)
        Resolve(string sliceCss, string repeatCss = "stretch", int srcSize = 32, double box = 200, double border = 8) {
            var bx = MakeBox(box, box, border);
            var style = new ComputedStyle(new Element("div"));
            style.Set("border-image-source", "url(frame)");
            style.Set("border-image-slice", sliceCss);
            style.Set("border-image-repeat", repeatCss);
            var reg = new InMemoryImageRegistry();
            reg.Register("frame", new StubSource(srcSize, srcSize));
            var parts = new List<BorderImageResolver.BorderImagePart>();
            BorderImageResolver.Resolve(style, bx, reg, parts);
            return (parts, reg);
        }

        [Test]
        public void No_source_yields_empty_result() {
            var bx = MakeBox(200, 200, 8);
            var style = new ComputedStyle(new Element("div"));
            // border-image-source defaults to "none".
            var parts = new List<BorderImageResolver.BorderImagePart>();
            BorderImageResolver.Resolve(style, bx, new InMemoryImageRegistry(), parts);
            Assert.That(parts.Count, Is.EqualTo(0));
        }

        [Test]
        public void Eight_parts_emitted_for_typical_frame() {
            var (parts, _) = Resolve("8", "stretch");
            // 4 corners + 4 edges = 8 parts (center omitted in v1).
            Assert.That(parts.Count, Is.EqualTo(8));
        }

        [Test]
        public void Corners_use_border_widths_as_dest_size() {
            var (parts, _) = Resolve("8", "stretch", srcSize: 32, box: 200, border: 8);
            // First 4 parts are corners.
            // Top-left corner: dest = (0, 0, 8, 8).
            Assert.That(parts[0].DestRect.X, Is.EqualTo(0));
            Assert.That(parts[0].DestRect.Y, Is.EqualTo(0));
            Assert.That(parts[0].DestRect.Width, Is.EqualTo(8));
            Assert.That(parts[0].DestRect.Height, Is.EqualTo(8));
            // Top-right corner: dest = (192, 0, 8, 8).
            Assert.That(parts[1].DestRect.X, Is.EqualTo(192));
            Assert.That(parts[1].DestRect.Y, Is.EqualTo(0));
        }

        [Test]
        public void Source_uvs_normalized_to_zero_one() {
            // slice=8 on srcSize=32: raw UV slice = 0.25; half-texel inset
            // tu=tv=0.5/32=0.015625 is applied, and V is flipped for bottom-up
            // Unity texture origin (topV = 1 - wT = 0.75).
            // Top-left corner: X = tu, Y = topV + tv, W = 0.25 - 2*tu, H = 0.25 - 2*tv.
            var (parts, _) = Resolve("8", "stretch", srcSize: 32, box: 200, border: 8);
            double tu = 0.5 / 32;
            double topV = 1.0 - 8.0 / 32.0;
            Assert.That(parts[0].SourceRect.X, Is.EqualTo(tu).Within(1e-6));
            Assert.That(parts[0].SourceRect.Y, Is.EqualTo(topV + tu).Within(1e-6));
            Assert.That(parts[0].SourceRect.Width, Is.EqualTo(0.25 - 2 * tu).Within(1e-6));
            Assert.That(parts[0].SourceRect.Height, Is.EqualTo(0.25 - 2 * tu).Within(1e-6));
        }

        [Test]
        public void Stretch_repeat_yields_null_tile_on_edges() {
            var (parts, _) = Resolve("8", "stretch");
            // Edges (parts[4..7]) should have null Tile (= stretch).
            for (int i = 4; i < 8; i++) {
                Assert.That(parts[i].Tile, Is.Null, $"part {i} should be stretched (Tile null)");
            }
        }

        [Test]
        public void Repeat_keyword_yields_tiled_edge() {
            var (parts, _) = Resolve("8", "repeat");
            // Top edge (part[4]): horizontal stretch span; tile must be set
            // and tile.RepeatX == Repeat.
            Assert.That(parts[4].Tile, Is.Not.Null);
            Assert.That(parts[4].Tile.Value.RepeatX, Is.EqualTo(BackgroundRepeatMode.Repeat));
            Assert.That(parts[4].Tile.Value.RepeatY, Is.EqualTo(BackgroundRepeatMode.NoRepeat));
        }

        [Test]
        public void Slice_shorthand_one_value_applies_to_all_sides() {
            var (parts, _) = Resolve("8", "stretch", srcSize: 32, box: 200, border: 8);
            // All four corner source rects should have the same width/height.
            // After half-texel SI inset (tu=0.5/32): width = 0.25 - 2*tu.
            double tu = 0.5 / 32;
            for (int i = 0; i < 4; i++) {
                Assert.That(parts[i].SourceRect.Width, Is.EqualTo(0.25 - 2 * tu).Within(1e-6));
                Assert.That(parts[i].SourceRect.Height, Is.EqualTo(0.25 - 2 * tu).Within(1e-6));
            }
        }

        [Test]
        public void Slice_shorthand_two_values_vertical_horizontal() {
            // 8 16 → top/bottom = 8, left/right = 16
            var (parts, _) = Resolve("8 16", "stretch", srcSize: 32, box: 200, border: 8);
            // Top-left corner source = (tu, ..., 16/32 - 2*tu, 8/32 - 2*tv).
            double tu = 0.5 / 32;
            Assert.That(parts[0].SourceRect.Width, Is.EqualTo(0.5 - 2 * tu).Within(1e-6));
            Assert.That(parts[0].SourceRect.Height, Is.EqualTo(0.25 - 2 * tu).Within(1e-6));
        }

        [Test]
        public void Percent_slice_resolves_against_source_dimensions() {
            // 25% on 32px = 8px slice → raw UV 0.25; after SI inset: 0.25 - 2*tu.
            var (parts, _) = Resolve("25%", "stretch", srcSize: 32, box: 200, border: 8);
            double tu = 0.5 / 32;
            Assert.That(parts[0].SourceRect.Width, Is.EqualTo(0.25 - 2 * tu).Within(1e-6));
            Assert.That(parts[0].SourceRect.Height, Is.EqualTo(0.25 - 2 * tu).Within(1e-6));
        }

        [Test]
        public void Fill_keyword_is_tolerated() {
            // border-image-slice: 8 fill; fill keyword must not corrupt the parse.
            var (parts, _) = Resolve("8 fill", "stretch", srcSize: 32, box: 200, border: 8);
            Assert.That(parts.Count, Is.GreaterThanOrEqualTo(8));
            double tu = 0.5 / 32;
            Assert.That(parts[0].SourceRect.Width, Is.EqualTo(0.25 - 2 * tu).Within(1e-6));
        }

        [Test]
        public void Round_and_space_keywords_resolve_to_distinct_modes() {
            var (parts, _) = Resolve("8", "round", srcSize: 32, box: 200, border: 8);
            // `round` now scales each tile so a whole number fits the edge.
            // The tile must be non-null and the TileWidth must evenly divide the
            // edge length (dCenterW = 200 - 8 - 8 = 184px; natural tile = 16px
            // center; tileCount = round(184/16) = 12; tileLen = 184/12 ≈ 15.33).
            Assert.That(parts[4].Tile, Is.Not.Null);
            // The tile width must be an integer divisor of the edge length.
            double edgeLen = 200 - 8 - 8;  // 184
            double tileW = parts[4].Tile.Value.TileWidth;
            Assert.That(tileW, Is.GreaterThan(0));
            // tileWidth * N == edgeLen for some integer N.
            double n = edgeLen / tileW;
            Assert.That(n, Is.EqualTo(System.Math.Round(n)).Within(1e-4),
                "round tiles must divide edge length exactly");
        }

        [Test]
        public void Missing_handle_yields_empty_result() {
            var bx = MakeBox(200, 200, 8);
            var style = new ComputedStyle(new Element("div"));
            style.Set("border-image-source", "url(missing)");
            style.Set("border-image-slice", "8");
            var parts = new List<BorderImageResolver.BorderImagePart>();
            BorderImageResolver.Resolve(style, bx, new InMemoryImageRegistry(), parts);
            Assert.That(parts.Count, Is.EqualTo(0));
        }

        [Test]
        public void Source_nine_slice_metadata_supplies_default_slice() {
            var bx = MakeBox(200, 200, 8);
            var style = new ComputedStyle(new Element("div"));
            style.Set("border-image-source", "url(frame)");
            var reg = new InMemoryImageRegistry();
            reg.Register("frame", new NineSliceSource());
            var parts = new List<BorderImageResolver.BorderImagePart>();

            BorderImageResolver.Resolve(style, bx, reg, parts);

            // 9 parts: 4 corners + 4 edges + 1 center. Sprite-supplied
            // 9-slice defaults fill=true (the sprite's center is content).
            // All source rects have the half-texel SI inset applied (tu=tv=0.5/32).
            Assert.That(parts.Count, Is.EqualTo(9));
            double tu = 0.5 / 32;
            Assert.That(parts[0].SourceRect.Width, Is.EqualTo(4.0 / 32.0 - 2 * tu).Within(1e-6));
            Assert.That(parts[0].SourceRect.Height, Is.EqualTo(6.0 / 32.0 - 2 * tu).Within(1e-6));
            Assert.That(parts[1].SourceRect.Width, Is.EqualTo(10.0 / 32.0 - 2 * tu).Within(1e-6));
            Assert.That(parts[2].SourceRect.Height, Is.EqualTo(8.0 / 32.0 - 2 * tu).Within(1e-6));
        }

        [Test]
        public void Source_nine_slice_with_100_percent_still_fills_center() {
            // `border-image-slice: 100%` on a sprite-sliced source keeps the
            // sprite's slices AND the center fill. An earlier version treated
            // bare 100% as a fill opt-out, but the `border:` SHORTHAND resets
            // border-image-slice to its initial 100% (CSS Backgrounds 3 §6),
            // making author-100% indistinguishable from the reset — the
            // "opt-out" silently killed the center on every element styled
            // with a `border:` declaration (9slice-demo's default card). The
            // sprite ergonomic (UGUI Sliced Image paints its center) wins.
            var bx = MakeBox(200, 200, 8);
            var style = new ComputedStyle(new Element("div"));
            style.Set("border-image-source", "url(frame)");
            style.Set("border-image-slice", "100%");
            var reg = new InMemoryImageRegistry();
            reg.Register("frame", new NineSliceSource());
            var parts = new List<BorderImageResolver.BorderImagePart>();

            BorderImageResolver.Resolve(style, bx, reg, parts);

            Assert.That(parts.Count, Is.EqualTo(9),
                "sprite-sliced source fills its center even at slice:100% (border shorthand resets to 100%)");
        }

        [Test]
        public void Source_nine_slice_with_fill_keyword_only_uses_sprite_border_and_fills() {
            // `border-image-slice: fill` alone — sprite border supplies the
            // numeric slice, the keyword forces the center patch.
            var bx = MakeBox(200, 200, 8);
            var style = new ComputedStyle(new Element("div"));
            style.Set("border-image-source", "url(frame)");
            style.Set("border-image-slice", "fill");
            var reg = new InMemoryImageRegistry();
            reg.Register("frame", new NineSliceSource());
            var parts = new List<BorderImageResolver.BorderImagePart>();

            BorderImageResolver.Resolve(style, bx, reg, parts);

            Assert.That(parts.Count, Is.EqualTo(9));
            double tu = 0.5 / 32;
            Assert.That(parts[0].SourceRect.Width, Is.EqualTo(4.0 / 32.0 - 2 * tu).Within(1e-6),
                "Sprite border should still drive the slice values");
        }

        [Test]
        public void Zero_border_widths_skip_corners_and_edges() {
            // With no border widths, all 9 dest rects collapse to zero size.
            var bx = MakeBox(200, 200, 0);
            var style = new ComputedStyle(new Element("div"));
            style.Set("border-image-source", "url(frame)");
            style.Set("border-image-slice", "8");
            style.Set("border-image-repeat", "stretch");
            var reg = new InMemoryImageRegistry();
            reg.Register("frame", new StubSource(32, 32));
            var parts = new List<BorderImageResolver.BorderImagePart>();
            BorderImageResolver.Resolve(style, bx, reg, parts);
            // Corners are emitted but with 0×0 size — renderer skips them
            // via FillRectCommand.IsEmpty. Edges are filtered out by
            // EmitEdge's early-return.
            int nonzero = 0;
            foreach (var p in parts) {
                if (p.DestRect.Width > 0 && p.DestRect.Height > 0) nonzero++;
            }
            Assert.That(nonzero, Is.EqualTo(0));
        }
    }
}
