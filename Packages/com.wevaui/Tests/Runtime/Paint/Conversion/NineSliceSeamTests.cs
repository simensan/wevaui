using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Paint.Images;

namespace Weva.Tests.Paint.Conversion {
    // Regression tests for 9-slice seam fixes:
    //   Bug 1 — Bottom vs top source UV symmetry (V-flip + SI inset correctness)
    //   Bug 2 — `border-image-repeat: round` correct tile scaling
    //
    // Bug 1 context: after the V-flip (topV=1-wT, botV=0) and the half-texel
    // SI inset, the bottom band must be symmetric with the top band. Top outer
    // edge samples at 1-tv; bottom outer edge samples at tv. Both bands are
    // equal in source-height after the inset.
    //
    // Bug 2 context: `round` must scale each tile so a whole number of tiles
    // fills the edge exactly (CSS Backgrounds 3 §6.2). Before the fix, `round`
    // was silently treated as `repeat` (tile = natural source pixel length,
    // possibly with fractional tiles at the edges).
    public class NineSliceSeamTests {

        // ──────────────────────────────────────────────────────────
        // Helpers shared by Bug 1 and Bug 2 tests
        // ──────────────────────────────────────────────────────────

        sealed class StubSource : IImageSource {
            public StubSource(int w, int h) { Width = w; Height = h; }
            public int Width { get; }
            public int Height { get; }
        }

        sealed class NineSliceStub : IImageSource, IImageNineSliceSource {
            public int Width { get; }
            public int Height { get; }
            readonly ImageNineSlice slice;
            public NineSliceStub(int w, int h, double l, double r, double t, double b) {
                Width = w; Height = h;
                slice = new ImageNineSlice(top: t, right: r, bottom: b, left: l);
            }
            public bool TryGetNineSlice(out ImageNineSlice s) { s = slice; return !s.IsEmpty; }
        }

        static BlockBox MakeBox(double w, double h, double border) {
            var bb = new BlockBox();
            bb.Width = w; bb.Height = h;
            bb.BorderLeft = border; bb.BorderTop = border;
            bb.BorderRight = border; bb.BorderBottom = border;
            return bb;
        }

        static List<BorderImageResolver.BorderImagePart> ResolveBorderImage(
            string sliceCss, string repeatCss,
            int srcW, int srcH,
            double box, double border) {
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

        // ──────────────────────────────────────────────────────────
        // Bug 1 — Bottom seam: V-flip + SI inset symmetry
        // ──────────────────────────────────────────────────────────

        // The top and bottom corner source rect widths/heights must be
        // equal after the V-flip and SI inset. If they differ, one band
        // covers more source texels than the other and the outer edge
        // appears lighter or darker than its opposite.
        [Test]
        public void Bug1_top_and_bottom_corner_source_widths_are_symmetric() {
            var parts = ResolveBorderImage("8", "stretch", srcW: 32, srcH: 32, box: 200, border: 8);
            // parts[0] = top-left, parts[3] = bottom-left.
            Assert.That(parts[0].SourceRect.Width, Is.EqualTo(parts[3].SourceRect.Width).Within(1e-6),
                "TL and BL corner source widths must match after V-flip + SI inset");
            Assert.That(parts[0].SourceRect.Height, Is.EqualTo(parts[3].SourceRect.Height).Within(1e-6),
                "TL and BL corner source heights must match after V-flip + SI inset");
        }

        // The top-left corner source band spans [1-tv .. 1-wT+tv] (outer→inner)
        // in V-flip space. The bottom-left corner source band spans [tv .. wB-tv].
        // Both bands are equally wide: wT-2*tv == wB-2*tv when wT==wB.
        [Test]
        public void Bug1_top_and_bottom_corner_source_heights_match_when_slices_equal() {
            // Uniform 8px slice on 32x32: wT=wB=0.25, tv=0.5/32.
            var parts = ResolveBorderImage("8", "stretch", srcW: 32, srcH: 32, box: 200, border: 8);
            double expectedH = 0.25 - 2.0 * (0.5 / 32);
            Assert.That(parts[0].SourceRect.Height, Is.EqualTo(expectedH).Within(1e-6),
                "TL corner source height = wT - 2*tv");
            Assert.That(parts[3].SourceRect.Height, Is.EqualTo(expectedH).Within(1e-6),
                "BL corner source height = wB - 2*tv");
        }

        // Bottom-left corner outer bottom: the source band starts at botV=0,
        // after SI inset Y=tv. The outermost sampled V is tv (half-texel from
        // image bottom), never V=0 itself — correct for bilinear.
        [Test]
        public void Bug1_bottom_corner_source_Y_starts_at_half_texel_inset() {
            var parts = ResolveBorderImage("8", "stretch", srcW: 32, srcH: 32, box: 200, border: 8);
            // Bottom-left corner = parts[3].
            double tv = 0.5 / 32;
            Assert.That(parts[3].SourceRect.Y, Is.EqualTo(tv).Within(1e-6),
                "BL corner source Y = botV + tv = tv (after SI inset)");
        }

        // Top-left corner outer top: source band ends at 1-tv (half-texel from
        // image top). Together with Bug1_bottom_corner_source_Y_starts_at_half_texel_inset,
        // this pins that both outer edges sample identically far from their
        // respective image edges.
        [Test]
        public void Bug1_top_corner_source_outer_edge_is_one_minus_half_texel() {
            var parts = ResolveBorderImage("8", "stretch", srcW: 32, srcH: 32, box: 200, border: 8);
            // TL corner = parts[0].
            double tv = 0.5 / 32;
            double expectedTopEnd = 1.0 - tv;
            double actualTopEnd = parts[0].SourceRect.Y + parts[0].SourceRect.Height;
            Assert.That(actualTopEnd, Is.EqualTo(expectedTopEnd).Within(1e-6),
                "TL corner source outer end = topV + tv + (wT-2*tv) = 1-tv");
        }

        // The bottom center edge (parts[6] in order: TL,TR,BR,BL,top,right,bottom,left)
        // must have the same source height as the top center edge.
        [Test]
        public void Bug1_top_and_bottom_edge_source_heights_are_symmetric() {
            var parts = ResolveBorderImage("8", "stretch", srcW: 32, srcH: 32, box: 200, border: 8);
            // Edges: parts[4]=top, parts[5]=right, parts[6]=bottom, parts[7]=left.
            Assert.That(parts[4].SourceRect.Height, Is.EqualTo(parts[6].SourceRect.Height).Within(1e-6),
                "Top and bottom edge source heights must be equal");
        }

        // Regression: with the V-flip, the bottom-left corner's source Y must
        // be LESS than the top-left corner's source Y (bottom band is near V=0,
        // top band is near V=1). Before the flip, both were near V=0, which was wrong.
        [Test]
        public void Bug1_bottom_corner_source_Y_is_less_than_top_corner_source_Y() {
            var parts = ResolveBorderImage("8", "stretch", srcW: 32, srcH: 32, box: 200, border: 8);
            Assert.That(parts[3].SourceRect.Y, Is.LessThan(parts[0].SourceRect.Y),
                "BL corner source Y (near 0) < TL corner source Y (near 1-wT)");
        }

        // The <img> 9-slice path (EmitImageNineSlice) must also be symmetric.
        [Test]
        public void Bug1_img_nineslice_bottom_top_dest_widths_are_symmetric() {
            var reg = new InMemoryImageRegistry();
            reg.Register("ui/frame", new NineSliceStub(32, 32, l: 8, r: 8, t: 8, b: 8));

            var element = new Element("img");
            element.SetAttribute("src", "ui/frame");
            var style = new ComputedStyle(element);
            var box = new BlockBox();
            box.Element = element;
            box.Style = style;
            box.Width = 200; box.Height = 200;

            var conv = new BoxToPaintConverter { ImageRegistry = reg };
            var cmds = conv.Convert(box).Commands;

            // Collect all image FillRect commands.
            var imgParts = new System.Collections.Generic.List<FillRectCommand>();
            foreach (var c in cmds) {
                if (c is FillRectCommand fr && fr.Brush != null && fr.Brush.Kind == BrushKind.Image)
                    imgParts.Add(fr);
            }
            Assert.That(imgParts.Count, Is.EqualTo(9), "9 parts from EmitImageNineSlice");

            // Paint order is center(0), edges(1-4), corners(5-8: TL,TR,BR,BL)
            // — corners last so their bleed sits on top of the edge strips.
            // The top-left corner (index 5) and bottom-left corner (index 8)
            // source heights must be equal (symmetric V-flip + SI inset).
            Assert.That(imgParts[5].Brush.ImageSourceRect.Height,
                Is.EqualTo(imgParts[8].Brush.ImageSourceRect.Height).Within(1e-6),
                "img: TL and BL corner source heights equal");

            // Bottom-left corner source Y must be less than top-left's.
            Assert.That(imgParts[8].Brush.ImageSourceRect.Y,
                Is.LessThan(imgParts[5].Brush.ImageSourceRect.Y),
                "img: BL corner source Y < TL corner source Y (V-flip)");
        }

        // ──────────────────────────────────────────────────────────
        // Bug 3 — converter-level seam bleed + paint layering
        // ──────────────────────────────────────────────────────────
        //
        // The 9 parts are separate quads; at a fractional shared boundary the
        // backend's SnapSampledFillToPixels rounding plus independent edge AA
        // leaves a sub-pixel gap — a visible seam line (9slice-demo Method B).
        // The converter inflates each part by 1px (clamped to the parts'
        // union) and paints center → edges → corners so every bleed tucks
        // UNDER its neighbour (painting the center last cut a 1px line of
        // stretched interior into the edge artwork — Method A).

        static List<FillRectCommand> ConvertBorderImage(string sliceCss, double boxW, double boxH, double border) {
            var element = new Element("div");
            var style = new ComputedStyle(element);
            style.Set("border-image-source", "url(frame)");
            style.Set("border-image-slice", sliceCss);
            var bx = MakeBox(boxW, boxH, border);
            bx.Element = element;
            bx.Style = style;
            var reg = new InMemoryImageRegistry();
            reg.Register("frame", new StubSource(64, 64));
            var conv = new BoxToPaintConverter { ImageRegistry = reg };
            var cmds = conv.Convert(bx).Commands;
            var parts = new List<FillRectCommand>();
            foreach (var c in cmds) {
                if (c is FillRectCommand fr && fr.Brush != null
                    && (fr.Brush.Kind == BrushKind.Image)) parts.Add(fr);
            }
            return parts;
        }

        [Test]
        public void Bug3_converter_paints_center_first_and_corners_last() {
            var parts = ConvertBorderImage("16 fill", boxW: 200.5, boxH: 120.5, border: 16);
            Assert.That(parts.Count, Is.EqualTo(9));
            // First-painted part is the CENTER: it contains the box midpoint.
            var first = parts[0].Bounds;
            Assert.That(first.X, Is.LessThan(100.25).And.GreaterThan(10),
                "first part is the center patch");
            Assert.That(first.X + first.Width, Is.GreaterThan(100.25));
            Assert.That(first.Y + first.Height, Is.GreaterThan(60.25));
            // Last four painted parts are the 16x16 corners (inflated ≤1px,
            // clamped to the union at the outer sides).
            for (int i = 5; i < 9; i++) {
                Assert.That(parts[i].Bounds.Width, Is.LessThanOrEqualTo(17.0 + 1e-6),
                    $"part {i} is a corner (W={parts[i].Bounds.Width:F2})");
                Assert.That(parts[i].Bounds.Height, Is.LessThanOrEqualTo(17.0 + 1e-6));
            }
        }

        [Test]
        public void Bug3_converter_bleeds_parts_by_one_pixel_within_union() {
            var parts = ConvertBorderImage("16 fill", boxW: 200, boxH: 120, border: 16);
            // Center resolver rect is (16,16,168,88); bled by 1px → (15,15,170,90).
            var center = parts[0].Bounds;
            Assert.That(center.X, Is.EqualTo(15).Within(1e-6), "center bled 1px left");
            Assert.That(center.Y, Is.EqualTo(15).Within(1e-6), "center bled 1px up");
            Assert.That(center.Width, Is.EqualTo(170).Within(1e-6), "center bled 1px each side");
            Assert.That(center.Height, Is.EqualTo(90).Within(1e-6));
            // A corner stays clamped at the union's outer corner: TL corner
            // (0,0,16,16) → (0,0,17,17) — no bleed past the border-image area.
            bool sawTl = false;
            for (int i = 5; i < 9; i++) {
                var b = parts[i].Bounds;
                if (System.Math.Abs(b.X) < 1e-6 && System.Math.Abs(b.Y) < 1e-6) {
                    sawTl = true;
                    Assert.That(b.Width, Is.EqualTo(17).Within(1e-6), "TL corner bleeds only inward");
                    Assert.That(b.Height, Is.EqualTo(17).Within(1e-6));
                }
            }
            Assert.That(sawTl, Is.True, "found the top-left corner part");
        }

        // ──────────────────────────────────────────────────────────
        // Bug 2 — border-image-repeat: round tile scaling
        // ──────────────────────────────────────────────────────────

        // CSS Backgrounds 3 §6.2: `round` scales each tile so a whole number
        // of tiles fills the edge. tileLen = edgeLen / round(edgeLen / natural).
        // The top edge (part[4]) must have TileWidth that divides dCenterW exactly.
        [Test]
        public void Bug2b_round_top_edge_tile_width_divides_edge_length_exactly() {
            // srcW=64, srcH=64, slice=16 → center column = 64-16-16 = 32px.
            // box=200, border=16 → dCenterW = 200-16-16 = 168px.
            // tileCount = round(168/32) = round(5.25) = 5; tileLen = 168/5 = 33.6.
            var parts = ResolveBorderImage("16", "round", srcW: 64, srcH: 64, box: 200, border: 16);
            Assert.That(parts[4].Tile, Is.Not.Null, "round top edge must have a tile");
            double edgeLen = 200 - 16 - 16;  // 168
            double tileW = parts[4].Tile.Value.TileWidth;
            double tileCount = edgeLen / tileW;
            Assert.That(tileCount, Is.EqualTo(System.Math.Round(tileCount)).Within(1e-4),
                "round: TileWidth * N == edgeLen for integer N");
        }

        // Verify the specific tileCount for the above case.
        [Test]
        public void Bug2b_round_top_edge_tile_count_is_closest_integer() {
            // Natural = 32px center, edge = 168px → round(168/32) = round(5.25) = 5.
            var parts = ResolveBorderImage("16", "round", srcW: 64, srcH: 64, box: 200, border: 16);
            double edgeLen = 200 - 16 - 16;  // 168
            double tileW = parts[4].Tile.Value.TileWidth;
            int tileCount = (int)System.Math.Round(edgeLen / tileW);
            Assert.That(tileCount * tileW, Is.EqualTo(edgeLen).Within(1e-4),
                "N tiles must exactly fill the edge");
        }

        // The vertical right edge (part[5]) must also have an integer tile count.
        [Test]
        public void Bug2b_round_right_edge_tile_height_divides_edge_length_exactly() {
            // Same 64x64/16px slice; vertical center = 64-16-16 = 32px natural.
            // box=200, border=16 → dCenterH = 168px.
            var parts = ResolveBorderImage("16", "round", srcW: 64, srcH: 64, box: 200, border: 16);
            Assert.That(parts[5].Tile, Is.Not.Null, "round right edge must have a tile");
            double edgeLen = 200 - 16 - 16;  // 168
            double tileH = parts[5].Tile.Value.TileHeight;
            double n = edgeLen / tileH;
            Assert.That(n, Is.EqualTo(System.Math.Round(n)).Within(1e-4),
                "round: TileHeight * N == edgeLen for integer N (vertical edge)");
        }

        // `round` with edge length exactly divisible: tileLen == naturalTile.
        [Test]
        public void Bug2b_round_exact_fit_keeps_natural_tile_size() {
            // srcW=64, slice=16 → center = 32px. box=160, border=16 → dCenterW=128.
            // 128/32 = 4 exactly; tileLen = 128/4 = 32 = natural.
            var parts = ResolveBorderImage("16", "round", srcW: 64, srcH: 64, box: 160, border: 16);
            double edgeLen = 160 - 16 - 16;  // 128
            double tileW = parts[4].Tile.Value.TileWidth;
            Assert.That(tileW, Is.EqualTo(edgeLen / 4).Within(1e-4),
                "exact-fit round: tileLen = natural (32px)");
        }

        // The cross-axis tile size (TileHeight for horizontal edge) must equal
        // the dest edge thickness — this ensures the tile fills exactly one
        // edge thickness in the non-repeating axis and never shifts content.
        [Test]
        public void Bug2_round_horizontal_edge_tile_height_equals_dest_thickness() {
            // border=16 → top edge dest height = 16px.
            var parts = ResolveBorderImage("16", "round", srcW: 64, srcH: 64, box: 200, border: 16);
            double destThickness = 16;  // border-width
            Assert.That(parts[4].Tile.Value.TileHeight, Is.EqualTo(destThickness).Within(1e-4),
                "round horizontal edge: TileHeight must equal dest edge thickness (cross-axis)");
        }

        // Same for vertical edge: TileWidth must equal dest edge thickness.
        [Test]
        public void Bug2_round_vertical_edge_tile_width_equals_dest_thickness() {
            var parts = ResolveBorderImage("16", "round", srcW: 64, srcH: 64, box: 200, border: 16);
            double destThickness = 16;  // border-width
            Assert.That(parts[5].Tile.Value.TileWidth, Is.EqualTo(destThickness).Within(1e-4),
                "round vertical edge: TileWidth must equal dest edge thickness (cross-axis)");
        }

        // `repeat` must NOT scale tiles — tile size stays at natural source length.
        [Test]
        public void Bug2_repeat_tile_width_is_natural_source_pixel_length() {
            // srcW=64, slice=16 → center = 32px natural.
            var parts = ResolveBorderImage("16", "repeat", srcW: 64, srcH: 64, box: 200, border: 16);
            double naturalCenter = 64 - 16 - 16;  // 32
            Assert.That(parts[4].Tile.Value.TileWidth, Is.EqualTo(naturalCenter).Within(1e-4),
                "repeat: TileWidth = natural source center width (unscaled)");
        }

        // `round` with edge shorter than half a natural tile: tileCount must be ≥1
        // (the single tile is compressed to fit).
        [Test]
        public void Bug2b_round_minimum_tile_count_is_one() {
            // srcW=64, slice=16 → center = 32px. box=50, border=8 → dCenterW=34px.
            // round(34/32) = round(1.0625) = 1; tileLen = 34/1 = 34.
            var parts = ResolveBorderImage("16", "round", srcW: 64, srcH: 64, box: 50, border: 8);
            if (parts.Count >= 5 && parts[4].Tile.HasValue) {
                double edgeLen = 50 - 8 - 8;  // 34
                double tileW = parts[4].Tile.Value.TileWidth;
                Assert.That(tileW, Is.EqualTo(edgeLen).Within(1e-4),
                    "round with very short edge: single tile fills entire edge");
            }
            // If the edge collapses (too small), there's no tile to check; skip.
        }

        // ──────────────────────────────────────────────────────────
        // B-9SLICE-SNAP — device-pixel edge snapping of part rects
        // ──────────────────────────────────────────────────────────
        //
        // The residual artifact after Bug 3's bleed: at a FRACTIONAL shared
        // boundary the top quad's AA edge still has fractional coverage over
        // sub-pixel-mismatched content — a ~1px ~10% luminance dip. Browsers
        // snap border-image part boundaries to device pixels so parts never
        // AA against each other (CSS Backgrounds 3 §6.2). The converter
        // flags every stretch part (Brush.SnapEdgesToDevicePixels) and the
        // batcher rounds all four dest edges; identical pre-snap boundary
        // coords round identically, so shared edges stay flush.

        [Test]
        public void Snap_img_nineslice_parts_carry_edge_snap_flag() {
            var reg = new InMemoryImageRegistry();
            reg.Register("ui/frame", new NineSliceStub(32, 32, l: 8, r: 8, t: 8, b: 8));
            var element = new Element("img");
            element.SetAttribute("src", "ui/frame");
            var style = new ComputedStyle(element);
            var box = new BlockBox { Element = element, Style = style, Width = 200, Height = 200 };
            var conv = new BoxToPaintConverter { ImageRegistry = reg };
            var cmds = conv.Convert(box).Commands;
            int parts = 0;
            foreach (var c in cmds) {
                if (c is FillRectCommand fr && fr.Brush != null && fr.Brush.Kind == BrushKind.Image) {
                    parts++;
                    Assert.That(fr.Brush.SnapEdgesToDevicePixels, Is.True,
                        "every <img> 9-slice part must request edge snapping");
                }
            }
            Assert.That(parts, Is.EqualTo(9));
        }

        [Test]
        public void Snap_border_image_stretch_parts_carry_edge_snap_flag() {
            var parts = ConvertBorderImage("16 fill", boxW: 200, boxH: 120, border: 16);
            Assert.That(parts.Count, Is.EqualTo(9));
            foreach (var p in parts) {
                if (p.Brush.Tile == null) {
                    Assert.That(p.Brush.SnapEdgesToDevicePixels, Is.True,
                        "stretch border-image parts must request edge snapping");
                }
            }
        }

        [Test]
        public void Snap_plain_img_does_not_carry_edge_snap_flag() {
            // Control: a non-9-slice image keeps the origin-preserving snap
            // (icon-jump concern at SnapSampledFillToPixels).
            var reg = new InMemoryImageRegistry();
            reg.Register("ui/icon", new StubSource(48, 48));
            var element = new Element("img");
            element.SetAttribute("src", "ui/icon");
            var style = new ComputedStyle(element);
            var box = new BlockBox { Element = element, Style = style, Width = 48, Height = 48 };
            var conv = new BoxToPaintConverter { ImageRegistry = reg };
            var cmds = conv.Convert(box).Commands;
            foreach (var c in cmds) {
                if (c is FillRectCommand fr && fr.Brush != null && fr.Brush.Kind == BrushKind.Image) {
                    Assert.That(fr.Brush.SnapEdgesToDevicePixels, Is.False,
                        "plain images must NOT edge-snap (icon-jump)");
                }
            }
        }

        [Test]
        public void Snap_shared_fractional_boundary_rounds_identically() {
            // Two abutting parts: A's right edge == B's left edge == 100.7
            // pre-snap. Both must land on the SAME integer after snapping —
            // flush, no gap, no overlap-dependent AA.
            var a = PixelSnapping.SnapSlicePartEdges(
                new Weva.Paint.Rect(10.4, 5.3, 90.3, 20.6));   // right = 100.7
            var b = PixelSnapping.SnapSlicePartEdges(
                new Weva.Paint.Rect(100.7, 5.3, 30.2, 20.6));  // left = 100.7
            Assert.That(a.X + a.Width, Is.EqualTo(b.X),
                "shared boundary must snap to the same device pixel");
            Assert.That(a.X, Is.EqualTo(10));
            Assert.That(a.X + a.Width, Is.EqualTo(101));
            Assert.That(a.Y, Is.EqualTo(b.Y), "shared top edges snap identically");
            Assert.That(a.Y + a.Height, Is.EqualTo(b.Y + b.Height));
        }

        [Test]
        public void Snap_integral_rect_passes_through_unchanged() {
            var r = PixelSnapping.SnapSlicePartEdges(
                new Weva.Paint.Rect(10, 20, 30, 40));
            Assert.That(r.X, Is.EqualTo(10));
            Assert.That(r.Y, Is.EqualTo(20));
            Assert.That(r.Width, Is.EqualTo(30));
            Assert.That(r.Height, Is.EqualTo(40));
        }

        [Test]
        public void Snap_thin_part_never_collapses_to_zero() {
            // A 0.4px-wide sliver (degenerate slice) must stay ≥1px wide,
            // mirroring SnapSampledFillToPixels' minimum.
            var r = PixelSnapping.SnapSlicePartEdges(
                new Weva.Paint.Rect(10.3, 5.0, 0.4, 8.0));
            Assert.That(r.Width, Is.EqualTo(1), "sub-pixel part keeps 1px minimum");
            Assert.That(r.Height, Is.EqualTo(8));
        }
    }
}
