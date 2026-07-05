using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Paint.Images;

namespace Weva.Tests.Paint.Conversion {
    // CSS Images 3 §5 — `object-fit` decides how a replaced element's intrinsic
    // image content maps to the box's content area. ApplyObjectFit lives in
    // BoxToPaintConverter and produces the destination rect for the
    // FillRectCommand it emits per <img>. The keyword set is:
    //
    //   fill        (default) stretch to bounds, ignore aspect ratio
    //   contain     letterbox: scale uniformly to fit, preserve aspect
    //   cover       crop: scale uniformly to cover, preserve aspect
    //   none        no scaling — paint at natural pixel size
    //   scale-down  whichever of `none` or `contain` produces a smaller rect
    //
    // The image is centered in the box (matches the default
    // `object-position: 50% 50%`). The IImageRegistry must be wired AND
    // resolve the src AND report Width/Height > 0, otherwise the converter
    // falls back to painting at the full bounds (`fill` behaviour) regardless
    // of the keyword.
    //
    // v1 GAP: `object-position` is parsed and stored in the cascade but
    // ApplyObjectFit always centers (50%/50%). Any author-supplied
    // `object-position` value other than the default has no visible effect.
    public class ObjectFitTests {
        sealed class StubSource : IImageSource {
            public StubSource(int w, int h) { Width = w; Height = h; }
            public int Width { get; }
            public int Height { get; }
        }

        // Build an <img> box at (0,0) with the given content rect, register the
        // src against a stub source of `natW × natH`, and apply `object-fit`.
        // Returns the image FillRectCommand's Bounds — the destination rect.
        static Rect FitRect(string fit, double boxW, double boxH, int natW, int natH, string src = "icon") {
            var element = new Element("img");
            element.SetAttribute("src", src);
            var style = new ComputedStyle(element);
            if (fit != null) style.Set("object-fit", fit);
            var box = new BlockBox();
            box.Element = element;
            box.Style = style;
            box.Width = boxW;
            box.Height = boxH;

            var reg = new InMemoryImageRegistry();
            reg.Register(src, new StubSource(natW, natH));

            var converter = new BoxToPaintConverter { ImageRegistry = reg };
            var cmds = converter.Convert(box).Commands;
            foreach (var c in cmds) {
                if (c is FillRectCommand fr && fr.Brush != null && fr.Brush.Kind == BrushKind.Image) {
                    return fr.Bounds;
                }
            }
            Assert.Fail("expected an Image-brushed FillRect for <img>");
            return default;
        }

        [Test]
        public void Fill_default_stretches_to_full_bounds() {
            // Default `fill` ignores aspect ratio and paints the image edge-to-
            // edge in the box. No registry consultation is needed for `fill`.
            var r = FitRect(fit: null, boxW: 200, boxH: 100, natW: 64, natH: 64);
            Assert.That(r.X, Is.EqualTo(0).Within(1e-6));
            Assert.That(r.Y, Is.EqualTo(0).Within(1e-6));
            Assert.That(r.Width, Is.EqualTo(200).Within(1e-6));
            Assert.That(r.Height, Is.EqualTo(100).Within(1e-6));
        }

        [Test]
        public void Contain_letterboxes_wider_box_with_square_image() {
            // 200×100 box, 64×64 source: scale = min(200/64, 100/64) = 100/64.
            // Target = 100×100, horizontally centered at x = (200-100)/2 = 50.
            var r = FitRect("contain", boxW: 200, boxH: 100, natW: 64, natH: 64);
            Assert.That(r.Width, Is.EqualTo(100).Within(1e-6));
            Assert.That(r.Height, Is.EqualTo(100).Within(1e-6));
            Assert.That(r.X, Is.EqualTo(50).Within(1e-6));
            Assert.That(r.Y, Is.EqualTo(0).Within(1e-6));
        }

        [Test]
        public void Contain_pillarboxes_taller_box_with_square_image() {
            // 100×200 box, 64×64 source: scale = min(100/64, 200/64) = 100/64.
            // Target = 100×100, vertically centered at y = (200-100)/2 = 50.
            var r = FitRect("contain", boxW: 100, boxH: 200, natW: 64, natH: 64);
            Assert.That(r.Width, Is.EqualTo(100).Within(1e-6));
            Assert.That(r.Height, Is.EqualTo(100).Within(1e-6));
            Assert.That(r.X, Is.EqualTo(0).Within(1e-6));
            Assert.That(r.Y, Is.EqualTo(50).Within(1e-6));
        }

        [Test]
        public void Cover_crops_to_fill_box_preserving_aspect() {
            // 200×100 box, 64×64 source: scale = max(200/64, 100/64) = 200/64.
            // Target = 200×200, vertically centered at y = (100-200)/2 = -50.
            // The negative Y means the top + bottom of the image are cropped
            // by the box's content edges (no painter-side clipping needed —
            // the surrounding clip-rect handles it).
            var r = FitRect("cover", boxW: 200, boxH: 100, natW: 64, natH: 64);
            Assert.That(r.Width, Is.EqualTo(200).Within(1e-6));
            Assert.That(r.Height, Is.EqualTo(200).Within(1e-6));
            Assert.That(r.X, Is.EqualTo(0).Within(1e-6));
            Assert.That(r.Y, Is.EqualTo(-50).Within(1e-6));
        }

        [Test]
        public void None_paints_at_natural_size_centered() {
            // `none` keyword: no scaling. 200×100 box, 64×32 source paints at
            // 64×32, centered at ((200-64)/2, (100-32)/2) = (68, 34).
            var r = FitRect("none", boxW: 200, boxH: 100, natW: 64, natH: 32);
            Assert.That(r.Width, Is.EqualTo(64).Within(1e-6));
            Assert.That(r.Height, Is.EqualTo(32).Within(1e-6));
            Assert.That(r.X, Is.EqualTo(68).Within(1e-6));
            Assert.That(r.Y, Is.EqualTo(34).Within(1e-6));
        }

        [Test]
        public void None_with_image_larger_than_box_paints_at_natural_size_with_negative_offset() {
            // `none` does NOT shrink: source 256×256 inside 100×100 box stays
            // 256×256, centered at ((100-256)/2, (100-256)/2) = (-78, -78).
            // The painter clips at the box edges; the spec lets the image
            // overflow visually under `overflow: visible`.
            var r = FitRect("none", boxW: 100, boxH: 100, natW: 256, natH: 256);
            Assert.That(r.Width, Is.EqualTo(256).Within(1e-6));
            Assert.That(r.Height, Is.EqualTo(256).Within(1e-6));
            Assert.That(r.X, Is.EqualTo(-78).Within(1e-6));
            Assert.That(r.Y, Is.EqualTo(-78).Within(1e-6));
        }

        [Test]
        public void Scale_down_picks_none_when_image_fits() {
            // `scale-down` = min(`none`, `contain`). Image 32×32 fits inside
            // 200×100 box, so `none` (32×32) is smaller than `contain`
            // (100×100). Result: paint at 32×32 centered at (84, 34).
            var r = FitRect("scale-down", boxW: 200, boxH: 100, natW: 32, natH: 32);
            Assert.That(r.Width, Is.EqualTo(32).Within(1e-6));
            Assert.That(r.Height, Is.EqualTo(32).Within(1e-6));
            Assert.That(r.X, Is.EqualTo(84).Within(1e-6));
            Assert.That(r.Y, Is.EqualTo(34).Within(1e-6));
        }

        [Test]
        public void Scale_down_picks_contain_when_image_overflows() {
            // `scale-down` = min(`none`, `contain`). Image 256×256 overflows
            // 100×100 box. `contain` yields 100×100 (scale = 100/256 < 1).
            // `none` would be 256×256. `scale-down` clamps to contain.
            var r = FitRect("scale-down", boxW: 100, boxH: 100, natW: 256, natH: 256);
            Assert.That(r.Width, Is.EqualTo(100).Within(1e-6));
            Assert.That(r.Height, Is.EqualTo(100).Within(1e-6));
            Assert.That(r.X, Is.EqualTo(0).Within(1e-6));
            Assert.That(r.Y, Is.EqualTo(0).Within(1e-6));
        }

        [Test]
        public void Object_fit_falls_back_to_fill_when_registry_is_null() {
            // Without an IImageRegistry the converter has no natural-size info,
            // so it cannot apply `contain`/`cover`/`none`/`scale-down`. The
            // EmitImageContent branch short-circuits to `bounds` (fill).
            var element = new Element("img");
            element.SetAttribute("src", "icon");
            var style = new ComputedStyle(element);
            style.Set("object-fit", "contain");
            var box = new BlockBox();
            box.Element = element;
            box.Style = style;
            box.Width = 200;
            box.Height = 100;

            // No ImageRegistry set on the converter.
            var converter = new BoxToPaintConverter();
            var cmds = converter.Convert(box).Commands;
            FillRectCommand fr = null;
            foreach (var c in cmds) {
                if (c is FillRectCommand x && x.Brush != null && x.Brush.Kind == BrushKind.Image) {
                    fr = x;
                    break;
                }
            }
            Assert.That(fr, Is.Not.Null);
            Assert.That(fr.Bounds.Width, Is.EqualTo(200).Within(1e-6));
            Assert.That(fr.Bounds.Height, Is.EqualTo(100).Within(1e-6));
        }

        [Test]
        public void Object_fit_falls_back_to_fill_when_handle_is_unregistered() {
            // Registry is set but the src doesn't resolve. Same outcome as
            // null registry: paint to bounds.
            var element = new Element("img");
            element.SetAttribute("src", "missing");
            var style = new ComputedStyle(element);
            style.Set("object-fit", "cover");
            var box = new BlockBox();
            box.Element = element;
            box.Style = style;
            box.Width = 200;
            box.Height = 100;

            var reg = new InMemoryImageRegistry();
            // Note: no Register("missing", ...) call.
            var converter = new BoxToPaintConverter { ImageRegistry = reg };
            var cmds = converter.Convert(box).Commands;
            FillRectCommand fr = null;
            foreach (var c in cmds) {
                if (c is FillRectCommand x && x.Brush != null && x.Brush.Kind == BrushKind.Image) {
                    fr = x;
                    break;
                }
            }
            Assert.That(fr, Is.Not.Null);
            Assert.That(fr.Bounds.Width, Is.EqualTo(200).Within(1e-6));
            Assert.That(fr.Bounds.Height, Is.EqualTo(100).Within(1e-6));
        }

        [Test]
        public void Unknown_keyword_falls_back_to_fill() {
            // ApplyObjectFit's switch returns `bounds` for any unrecognised
            // keyword. Author typos / future keywords don't silently break.
            var r = FitRect("squish", boxW: 200, boxH: 100, natW: 64, natH: 64);
            Assert.That(r.X, Is.EqualTo(0).Within(1e-6));
            Assert.That(r.Y, Is.EqualTo(0).Within(1e-6));
            Assert.That(r.Width, Is.EqualTo(200).Within(1e-6));
            Assert.That(r.Height, Is.EqualTo(100).Within(1e-6));
        }

        // Convenience for the object-position scenarios below.
        static Rect FitRectAt(string fit, string position, double boxW, double boxH, int natW, int natH) {
            var element = new Element("img");
            element.SetAttribute("src", "icon");
            var style = new ComputedStyle(element);
            if (fit != null) style.Set("object-fit", fit);
            if (position != null) style.Set("object-position", position);
            var box = new BlockBox();
            box.Element = element;
            box.Style = style;
            box.Width = boxW;
            box.Height = boxH;

            var reg = new InMemoryImageRegistry();
            reg.Register("icon", new StubSource(natW, natH));
            var converter = new BoxToPaintConverter { ImageRegistry = reg };
            var cmds = converter.Convert(box).Commands;
            foreach (var c in cmds) {
                if (c is FillRectCommand fr && fr.Brush != null && fr.Brush.Kind == BrushKind.Image) {
                    return fr.Bounds;
                }
            }
            Assert.Fail("expected an Image-brushed FillRect for <img>");
            return default;
        }

        [Test]
        public void Object_position_left_top_pins_image_at_box_origin() {
            // `contain` produces a 100×100 target inside a 200×100 box. With
            // `object-position: left top` the residual range (200-100, 100-100)
            // = (100, 0) is multiplied by (0%, 0%) → image at (0, 0).
            var r = FitRectAt("contain", "left top", boxW: 200, boxH: 100, natW: 64, natH: 64);
            Assert.That(r.X, Is.EqualTo(0).Within(1e-6));
            Assert.That(r.Y, Is.EqualTo(0).Within(1e-6));
            Assert.That(r.Width, Is.EqualTo(100).Within(1e-6));
            Assert.That(r.Height, Is.EqualTo(100).Within(1e-6));
        }

        [Test]
        public void Object_position_right_bottom_pins_image_to_far_corner() {
            // Same `contain` 100×100 target; `right bottom` shifts to the box's
            // far corner: x = 200-100 = 100, y = 100-100 = 0.
            var r = FitRectAt("contain", "right bottom", boxW: 200, boxH: 100, natW: 64, natH: 64);
            Assert.That(r.X, Is.EqualTo(100).Within(1e-6));
            Assert.That(r.Y, Is.EqualTo(0).Within(1e-6));
        }

        [Test]
        public void Object_position_percent_on_both_axes_interpolates_offset() {
            // 200×200 box, 64×64 source, `contain` produces 200×200 = box size
            // exactly. Change box to 300×200 so x has 100 of slack; 25% pulls
            // the image to x = 25 (100 * 0.25).
            var r = FitRectAt("contain", "25% 75%", boxW: 300, boxH: 200, natW: 64, natH: 64);
            Assert.That(r.Width, Is.EqualTo(200).Within(1e-6));
            Assert.That(r.Height, Is.EqualTo(200).Within(1e-6));
            Assert.That(r.X, Is.EqualTo(25).Within(1e-6),
                "25% of (300 - 200) slack = 25px from the left edge.");
            Assert.That(r.Y, Is.EqualTo(0).Within(1e-6),
                "200 - 200 = 0 slack on Y, so 75% of 0 still pins at the top.");
        }

        [Test]
        public void Object_position_pixel_length_shifts_image_absolutely() {
            // 200×100 box, 64×64 source, `contain` gives 100×100. `10px 5px`
            // places the image at (10, 5) regardless of the residual space.
            var r = FitRectAt("contain", "10px 5px", boxW: 200, boxH: 100, natW: 64, natH: 64);
            Assert.That(r.X, Is.EqualTo(10).Within(1e-6));
            Assert.That(r.Y, Is.EqualTo(5).Within(1e-6));
        }

        [Test]
        public void Object_position_with_cover_chooses_crop_direction() {
            // `cover` on a 200×100 box with a 64×64 source produces a 200×200
            // target that overflows by 100px on Y. `object-position: top`
            // means the TOP of the image is shown — y = 0 (no overflow up,
            // full overflow on bottom). Default (50% 50%) would put y at -50.
            var r = FitRectAt("cover", "center top", boxW: 200, boxH: 100, natW: 64, natH: 64);
            Assert.That(r.Width, Is.EqualTo(200).Within(1e-6));
            Assert.That(r.Height, Is.EqualTo(200).Within(1e-6));
            Assert.That(r.X, Is.EqualTo(0).Within(1e-6));
            Assert.That(r.Y, Is.EqualTo(0).Within(1e-6),
                "object-position: top with cover crops the bottom — image rect starts at y=0.");
        }

        [Test]
        public void Object_position_default_centers_unchanged() {
            // Sanity: with no explicit object-position the cascade still
            // returns the parsed 50% 50% default and the result matches the
            // legacy centered behaviour.
            var r = FitRectAt("contain", null, boxW: 200, boxH: 100, natW: 64, natH: 64);
            Assert.That(r.X, Is.EqualTo(50).Within(1e-6));
            Assert.That(r.Y, Is.EqualTo(0).Within(1e-6));
        }

        [Test]
        public void Zero_natural_height_falls_back_to_fill() {
            // ApplyObjectFit early-outs when natW or natH is <= 0 to avoid
            // divide-by-zero in the scale ratio. A stub source reporting 0
            // height should paint to bounds rather than producing NaN.
            var r = FitRect("cover", boxW: 200, boxH: 100, natW: 64, natH: 0);
            Assert.That(r.X, Is.EqualTo(0).Within(1e-6));
            Assert.That(r.Y, Is.EqualTo(0).Within(1e-6));
            Assert.That(r.Width, Is.EqualTo(200).Within(1e-6));
            Assert.That(r.Height, Is.EqualTo(100).Within(1e-6));
        }
    }
}
