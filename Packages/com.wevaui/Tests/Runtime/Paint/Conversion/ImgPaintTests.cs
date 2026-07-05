using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Paint.Images;

namespace Weva.Tests.Paint.Conversion {
    // BoxToPaintConverter handles `<img>` elements by emitting a
    // FillRectCommand with Brush.Image(src, ...). The image fills the
    // box rect (border-box space, same as background) so border-radius
    // clips both consistently. Empty / missing src is silently skipped.
    public class ImgPaintTests {
        static (BlockBox box, ComputedStyle style) ImgBox(string src, double w = 32, double h = 32) {
            var element = new Element("img");
            if (src != null) element.SetAttribute("src", src);
            var style = new ComputedStyle(element);
            var box = new BlockBox();
            box.Element = element;
            box.Style = style;
            box.Width = w; box.Height = h;
            return (box, style);
        }

        [Test]
        public void Img_with_src_emits_image_brush_fillrect() {
            var (box, _) = ImgBox("ui/heart-icon");
            var cmds = new BoxToPaintConverter().Convert(box).Commands;

            FillRectCommand fr = null;
            foreach (var c in cmds) {
                if (c is FillRectCommand x && x.Brush != null && x.Brush.Kind == BrushKind.Image) {
                    fr = x;
                    break;
                }
            }
            Assert.That(fr, Is.Not.Null, "expected an Image-brushed FillRect for <img>");
            Assert.That(fr.Brush.ImageHandle, Is.EqualTo("ui/heart-icon"));
            Assert.That(fr.Bounds.Width, Is.EqualTo(32));
            Assert.That(fr.Bounds.Height, Is.EqualTo(32));
        }

        [Test]
        public void Img_without_src_emits_no_image_command() {
            var (box, _) = ImgBox(src: null);
            var cmds = new BoxToPaintConverter().Convert(box).Commands;
            foreach (var c in cmds) {
                if (c is FillRectCommand x && x.Brush != null && x.Brush.Kind == BrushKind.Image) {
                    Assert.Fail("did not expect an image brush for <img> with no src");
                }
            }
        }

        [Test]
        public void Img_inherits_image_rendering_pixelated() {
            var (box, style) = ImgBox("sprites/player");
            style.Set("image-rendering", "pixelated");
            var cmds = new BoxToPaintConverter().Convert(box).Commands;

            FillRectCommand fr = null;
            foreach (var c in cmds) {
                if (c is FillRectCommand x && x.Brush != null && x.Brush.Kind == BrushKind.Image) { fr = x; break; }
            }
            Assert.That(fr, Is.Not.Null);
            Assert.That(fr.Brush.ImageRendering, Is.EqualTo(ImageRenderingMode.Pixelated));
        }

        [Test]
        public void Img_paints_after_background_and_before_border() {
            // Order check: background fillrect → image fillrect → stroke border.
            var (box, style) = ImgBox("icon");
            style.Set("background-color", "red");
            style.Set("border-top-style", "solid");
            style.Set("border-top-width", "2px");
            style.Set("border-top-color", "black");
            var cmds = new BoxToPaintConverter().Convert(box).Commands;

            int bgIdx = -1, imgIdx = -1, borderIdx = -1;
            for (int i = 0; i < cmds.Count; i++) {
                if (cmds[i] is FillRectCommand fr) {
                    if (fr.Brush.Kind == BrushKind.SolidColor && bgIdx < 0) bgIdx = i;
                    else if (fr.Brush.Kind == BrushKind.Image) imgIdx = i;
                }
                if (cmds[i] is StrokeBorderCommand && borderIdx < 0) borderIdx = i;
            }
            Assert.That(bgIdx, Is.GreaterThanOrEqualTo(0));
            Assert.That(imgIdx, Is.GreaterThan(bgIdx));
            Assert.That(borderIdx, Is.GreaterThan(imgIdx));
        }

        [Test]
        public void Img_inherits_border_radius_from_style() {
            var (box, style) = ImgBox("icon");
            style.Set("border-radius", "8px");
            var cmds = new BoxToPaintConverter().Convert(box).Commands;

            FillRectCommand fr = null;
            foreach (var c in cmds) {
                if (c is FillRectCommand x && x.Brush != null && x.Brush.Kind == BrushKind.Image) { fr = x; break; }
            }
            Assert.That(fr, Is.Not.Null);
            Assert.That(fr.Radii.TopLeft.XRadius, Is.EqualTo(8).Within(1e-6));
        }

        [Test]
        public void Non_img_element_emits_no_image_command() {
            // Make sure the img branch doesn't fire on arbitrary boxes.
            var element = new Element("div");
            element.SetAttribute("src", "should-not-render");
            var style = new ComputedStyle(element);
            var box = new BlockBox();
            box.Element = element;
            box.Style = style;
            box.Width = 32; box.Height = 32;

            var cmds = new BoxToPaintConverter().Convert(box).Commands;
            foreach (var c in cmds) {
                if (c is FillRectCommand x && x.Brush != null && x.Brush.Kind == BrushKind.Image) {
                    Assert.Fail("only <img> should emit an image brush");
                }
            }
        }

        // -------------------- 9-slice <img> --------------------
        //
        // When the image source carries native 9-slice metadata
        // (`IImageNineSliceSource` — Unity sprites with border, etc.) the
        // converter paints the <img> as 9 sub-quads instead of a single
        // stretched fill. Corners stay at source-pixel size, edges and
        // center stretch to fill. This matches Unity UGUI's Image with
        // Sliced sprite — no CSS required.
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

        [Test]
        public void Img_with_native_9slice_source_emits_9_parts() {
            // 32×32 frame with 8px border on every side. 200×200 box →
            // corners stay 8×8, edges stretch to 184×8 (h) / 8×184 (v),
            // center 184×184.
            var reg = new InMemoryImageRegistry();
            reg.Register("ui/frame", new NineSliceStub(32, 32, l: 8, r: 8, t: 8, b: 8));

            var (box, _) = ImgBox("ui/frame", w: 200, h: 200);
            var conv = new BoxToPaintConverter { ImageRegistry = reg };
            var cmds = conv.Convert(box).Commands;

            int imgParts = 0;
            foreach (var c in cmds) {
                if (c is FillRectCommand fr && fr.Brush != null && fr.Brush.Kind == BrushKind.Image) imgParts++;
            }
            Assert.That(imgParts, Is.EqualTo(9), "expected 4 corners + 4 edges + 1 center");
        }

        [Test]
        public void Img_9slice_corners_preserve_source_pixel_size() {
            var reg = new InMemoryImageRegistry();
            reg.Register("ui/frame", new NineSliceStub(32, 32, l: 8, r: 8, t: 8, b: 8));

            var (box, _) = ImgBox("ui/frame", w: 200, h: 200);
            var conv = new BoxToPaintConverter { ImageRegistry = reg };
            var cmds = conv.Convert(box).Commands;

            // EmitImageNineSlice uses DI(bleed=1) which expands each part by 1px
            // on internal seams. The top-left corner's dest = DI(0,0,8,8):
            //   x0=max(0,-1)=0, y0=max(0,-1)=0, x1=min(200,9)=9, y1=min(200,9)=9
            // → {0, 0, 9, 9}. Source UV with SI half-texel inset (tu=tv=0.5/32):
            //   X=tu, Y=topV+tv=(1-0.25)+tv=0.75+tv, W=0.25-2*tu, H=0.25-2*tv.
            double tu = 0.5 / 32;
            double topV = 1.0 - 8.0 / 32.0;  // 0.75
            bool foundTopLeft = false;
            foreach (var c in cmds) {
                if (c is FillRectCommand fr && fr.Brush != null && fr.Brush.Kind == BrushKind.Image
                    && System.Math.Abs(fr.Bounds.X) < 1e-4 && System.Math.Abs(fr.Bounds.Y) < 1e-4
                    && fr.Bounds.Width > 8 && fr.Bounds.Width < 10
                    && fr.Bounds.Height > 8 && fr.Bounds.Height < 10) {
                    // Found top-left corner (DI expanded to ~9×9)
                    Assert.That(fr.Brush.ImageSourceRect.X, Is.EqualTo(tu).Within(1e-5),
                        "TL corner source X = tu");
                    Assert.That(fr.Brush.ImageSourceRect.Y, Is.EqualTo(topV + tu).Within(1e-5),
                        "TL corner source Y = topV + tv (V-flip, bottom-up)");
                    Assert.That(fr.Brush.ImageSourceRect.Width, Is.EqualTo(0.25 - 2 * tu).Within(1e-5),
                        "TL corner source W = wL - 2*tu");
                    Assert.That(fr.Brush.ImageSourceRect.Height, Is.EqualTo(0.25 - 2 * tu).Within(1e-5),
                        "TL corner source H = wT - 2*tv");
                    foundTopLeft = true;
                    break;
                }
            }
            Assert.That(foundTopLeft, Is.True, "expected top-left corner part near (0,0)");
        }

        [Test]
        public void Img_9slice_clamps_corners_when_box_too_small() {
            // Box is 10×10 but corners want 8+8=16 — corners must scale
            // down so they don't overlap and the center collapses to 0.
            var reg = new InMemoryImageRegistry();
            reg.Register("ui/frame", new NineSliceStub(32, 32, l: 8, r: 8, t: 8, b: 8));

            var (box, _) = ImgBox("ui/frame", w: 10, h: 10);
            var conv = new BoxToPaintConverter { ImageRegistry = reg };
            var cmds = conv.Convert(box).Commands;

            double maxX = 0, maxY = 0;
            foreach (var c in cmds) {
                if (c is FillRectCommand fr && fr.Brush != null && fr.Brush.Kind == BrushKind.Image) {
                    maxX = System.Math.Max(maxX, fr.Bounds.X + fr.Bounds.Width);
                    maxY = System.Math.Max(maxY, fr.Bounds.Y + fr.Bounds.Height);
                }
            }
            Assert.That(maxX, Is.LessThanOrEqualTo(10.001), "parts must fit within box width");
            Assert.That(maxY, Is.LessThanOrEqualTo(10.001), "parts must fit within box height");
        }

        [Test]
        public void Img_without_9slice_metadata_falls_back_to_single_fill() {
            // Regular IImageSource (no nine-slice) — keep the original
            // single FillRect behaviour.
            var reg = new InMemoryImageRegistry();
            reg.Register("ui/heart", new StubFlat(64, 64));
            var (box, _) = ImgBox("ui/heart", w: 64, h: 64);
            var conv = new BoxToPaintConverter { ImageRegistry = reg };
            var cmds = conv.Convert(box).Commands;

            int imgParts = 0;
            foreach (var c in cmds) {
                if (c is FillRectCommand fr && fr.Brush != null && fr.Brush.Kind == BrushKind.Image) imgParts++;
            }
            Assert.That(imgParts, Is.EqualTo(1));
        }

        sealed class StubFlat : IImageSource {
            public StubFlat(int w, int h) { Width = w; Height = h; }
            public int Width { get; }
            public int Height { get; }
        }

        [Test]
        public void Img_source_rect_is_full_image_v1() {
            var (box, _) = ImgBox("icon");
            var cmds = new BoxToPaintConverter().Convert(box).Commands;
            FillRectCommand fr = null;
            foreach (var c in cmds) {
                if (c is FillRectCommand x && x.Brush != null && x.Brush.Kind == BrushKind.Image) { fr = x; break; }
            }
            Assert.That(fr.Brush.ImageSourceRect.X, Is.EqualTo(0));
            Assert.That(fr.Brush.ImageSourceRect.Width, Is.EqualTo(1));
            Assert.That(fr.Brush.ImageSourceRect.Height, Is.EqualTo(1));
        }
    }
}
