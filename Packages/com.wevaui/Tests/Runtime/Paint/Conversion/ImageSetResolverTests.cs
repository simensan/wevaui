using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // CSS Images L4 §5.4 — image-set() picker, both the standalone resolver
    // and its integration into BackgroundResolver. Coverage focuses on the
    // candidate-selection rule (smallest >= target, else largest), unit
    // handling (x/dppx/dpi/dpcm), and the parsed-tree vs raw-text fallback
    // paths. The picker is host-DPR-driven via LengthContext.DpiPixelsPerInch
    // (CSS reference DPI is 96).
    public class ImageSetResolverTests {
        static ComputedStyle Style() => new ComputedStyle(new Element("div"));
        static Rect Bounds() => new Rect(0, 0, 100, 100);

        static LengthContext CtxAtDpr(double dpr) {
            var c = LengthContext.Default;
            c.DpiPixelsPerInch = dpr * 96.0;
            return c;
        }

        // ---------------- Raw path: TryResolveRaw ----------------

        [Test]
        public void TryResolveRaw_picks_1x_at_dpr_1() {
            bool ok = ImageSetResolver.TryResolveRaw(
                "url(low.png) 1x, url(high.png) 2x", 1.0, out var handle);
            Assert.That(ok, Is.True);
            Assert.That(handle, Is.EqualTo("low.png"));
        }

        [Test]
        public void TryResolveRaw_picks_2x_at_dpr_2() {
            bool ok = ImageSetResolver.TryResolveRaw(
                "url(low.png) 1x, url(high.png) 2x", 2.0, out var handle);
            Assert.That(ok, Is.True);
            Assert.That(handle, Is.EqualTo("high.png"));
        }

        [Test]
        public void TryResolveRaw_at_intermediate_dpr_picks_smallest_above() {
            // dpr=1.5 → choose 2x (smallest >= target) rather than 1x.
            bool ok = ImageSetResolver.TryResolveRaw(
                "url(low.png) 1x, url(high.png) 2x", 1.5, out var handle);
            Assert.That(ok, Is.True);
            Assert.That(handle, Is.EqualTo("high.png"));
        }

        [Test]
        public void TryResolveRaw_falls_back_to_max_when_no_candidate_above_target() {
            // dpr=3 but only 1x/2x available → picks 2x (the highest).
            bool ok = ImageSetResolver.TryResolveRaw(
                "url(low.png) 1x, url(high.png) 2x", 3.0, out var handle);
            Assert.That(ok, Is.True);
            Assert.That(handle, Is.EqualTo("high.png"));
        }

        [Test]
        public void TryResolveRaw_treats_missing_resolution_as_1x() {
            // Per L4 §5.4, an option without a resolution defaults to 1x.
            bool ok = ImageSetResolver.TryResolveRaw(
                "url(default.png), url(high.png) 2x", 1.0, out var handle);
            Assert.That(ok, Is.True);
            Assert.That(handle, Is.EqualTo("default.png"));
        }

        [Test]
        public void TryResolveRaw_accepts_dppx_unit() {
            bool ok = ImageSetResolver.TryResolveRaw(
                "url(a.png) 1dppx, url(b.png) 2dppx", 2.0, out var handle);
            Assert.That(ok, Is.True);
            Assert.That(handle, Is.EqualTo("b.png"));
        }

        [Test]
        public void TryResolveRaw_accepts_dpi_unit_normalising_to_96dppx() {
            // 192dpi == 2dppx; at dpr=2 it should win over the 96dpi candidate.
            bool ok = ImageSetResolver.TryResolveRaw(
                "url(low.png) 96dpi, url(high.png) 192dpi", 2.0, out var handle);
            Assert.That(ok, Is.True);
            Assert.That(handle, Is.EqualTo("high.png"));
        }

        [Test]
        public void TryResolveRaw_handles_quoted_string_sources() {
            // Per L4 §5.4 a quoted string is equivalent to url("…").
            bool ok = ImageSetResolver.TryResolveRaw(
                "\"a.png\" 1x, \"b.png\" 2x", 2.0, out var handle);
            Assert.That(ok, Is.True);
            Assert.That(handle, Is.EqualTo("b.png"));
        }

        [Test]
        public void TryResolveRaw_rejects_empty_body() {
            Assert.That(ImageSetResolver.TryResolveRaw("", 1.0, out _), Is.False);
            Assert.That(ImageSetResolver.TryResolveRaw(null, 1.0, out _), Is.False);
        }

        [Test]
        public void TryResolveRaw_strips_quotes_from_url() {
            bool ok = ImageSetResolver.TryResolveRaw(
                "url(\"quoted.png\") 1x", 1.0, out var handle);
            Assert.That(ok, Is.True);
            Assert.That(handle, Is.EqualTo("quoted.png"));
        }

        // ---------------- IsImageSetName ----------------

        [Test]
        public void IsImageSetName_accepts_standard_form() {
            Assert.That(ImageSetResolver.IsImageSetName("image-set"), Is.True);
        }

        [Test]
        public void IsImageSetName_accepts_webkit_prefixed_form() {
            // Pre-standard Safari/Blink shipped -webkit-image-set; preserve
            // it as an alias so author code that targets older mobile WKs
            // still resolves to a candidate.
            Assert.That(ImageSetResolver.IsImageSetName("-webkit-image-set"), Is.True);
        }

        [Test]
        public void IsImageSetName_rejects_unrelated_function() {
            Assert.That(ImageSetResolver.IsImageSetName("url"), Is.False);
            Assert.That(ImageSetResolver.IsImageSetName("linear-gradient"), Is.False);
            Assert.That(ImageSetResolver.IsImageSetName(""), Is.False);
            Assert.That(ImageSetResolver.IsImageSetName(null), Is.False);
        }

        // ---------------- DprFromLengthContext ----------------

        [Test]
        public void DprFromLengthContext_defaults_to_1_at_96dpi() {
            var dpr = ImageSetResolver.DprFromLengthContext(LengthContext.Default);
            Assert.That(dpr, Is.EqualTo(1.0).Within(1e-9));
        }

        [Test]
        public void DprFromLengthContext_scales_with_host_dpi() {
            Assert.That(ImageSetResolver.DprFromLengthContext(CtxAtDpr(2.0)),
                Is.EqualTo(2.0).Within(1e-9));
            Assert.That(ImageSetResolver.DprFromLengthContext(CtxAtDpr(3.0)),
                Is.EqualTo(3.0).Within(1e-9));
        }

        [Test]
        public void DprFromLengthContext_returns_1_for_nonpositive_dpi() {
            var ctx = LengthContext.Default;
            ctx.DpiPixelsPerInch = 0;
            Assert.That(ImageSetResolver.DprFromLengthContext(ctx), Is.EqualTo(1.0));
        }

        // ---------------- BackgroundResolver integration ----------------
        //
        // The end-to-end path: background-image: image-set(...) on a style
        // resolves to a Brush.Image whose handle is the picked candidate.

        [Test]
        public void Background_image_set_picks_1x_handle_at_default_dpr() {
            var s = Style();
            s.Set("background-image", "image-set(url(low.png) 1x, url(high.png) 2x)");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            Assert.That(brush, Is.Not.Null,
                "image-set() at dpr=1 should yield a Brush.Image, not null");
            Assert.That(brush.Kind, Is.EqualTo(BrushKind.Image));
            Assert.That(brush.ImageHandle, Is.EqualTo("low.png"));
        }

        [Test]
        public void Background_image_set_picks_2x_handle_at_high_dpr() {
            var s = Style();
            s.Set("background-image", "image-set(url(low.png) 1x, url(high.png) 2x)");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds(), CtxAtDpr(2.0));
            Assert.That(brush, Is.Not.Null);
            Assert.That(brush.ImageHandle, Is.EqualTo("high.png"));
        }

        [Test]
        public void Background_image_set_default_dpr_picks_1x_implicit_candidate() {
            // An option with no explicit resolution defaults to 1x, so on a
            // dpr=1 display it wins source-order over any 2x sibling.
            var s = Style();
            s.Set("background-image", "image-set(url(default.png), url(high.png) 2x)");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds());
            Assert.That(brush, Is.Not.Null);
            Assert.That(brush.ImageHandle, Is.EqualTo("default.png"));
        }

        [Test]
        public void Background_image_set_inherits_image_rendering_pixelated() {
            // image-set() must flow through the same image-rendering resolver
            // as bare url(): a pixelated authoring intent stays pixelated on
            // the picked candidate.
            var s = Style();
            s.Set("background-image", "image-set(url(sprite-1x.png) 1x, url(sprite-2x.png) 2x)");
            s.Set("image-rendering", "pixelated");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds(), CtxAtDpr(2.0));
            Assert.That(brush, Is.Not.Null);
            Assert.That(brush.ImageRendering, Is.EqualTo(ImageRenderingMode.Pixelated));
        }

        [Test]
        public void Background_image_set_with_webkit_prefix_resolves() {
            var s = Style();
            s.Set("background-image", "-webkit-image-set(url(low.png) 1x, url(high.png) 2x)");
            var brush = BackgroundResolver.ResolveBackground(s, Bounds(), CtxAtDpr(2.0));
            Assert.That(brush, Is.Not.Null,
                "-webkit-image-set is an accepted alias for image-set on legacy authoring");
            Assert.That(brush.ImageHandle, Is.EqualTo("high.png"));
        }
    }
}
