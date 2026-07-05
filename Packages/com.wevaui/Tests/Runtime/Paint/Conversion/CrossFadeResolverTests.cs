using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Tests.Paint.Conversion {
    // CSS Images L3 §2.6 / Chrome's -webkit-cross-fade() implementation.
    //
    // Tests cover:
    //   - Parsing: both prefixed and unprefixed names, default 50%, clamping
    //   - Operand kinds: url+url, gradient+gradient, url+gradient, color+url
    //   - Emitted paint structure: two layers with correct LayerAlpha values
    //   - image-set() inside cross-fade() operands
    //   - Invalid forms rejected (one operand, junk percentage, empty body)
    //   - Cascade round-trip via background-image property
    //   - IsOpaqueCoveringLayer not triggered by alpha-weighted layers
    //   - BoxToPaintConverter emits PushOpacity/PopOpacity around cross-fade layers
    //
    // NUnit constraints: Never chain .Within() off Is.GreaterThan/LessThan;
    // use Is.EqualTo().Within() for tolerance. Does.Not.Contain is string-only.
    public class CrossFadeResolverTests {
        static ComputedStyle Style() => new ComputedStyle(new Element("div"));
        static Rect Bounds(double w = 100, double h = 100) => new Rect(0, 0, w, h);
        static LinearColor Black => new LinearColor(0, 0, 0, 1);

        // ---- IsCrossFadeName ----

        [Test]
        public void IsCrossFadeName_recognises_unprefixed() {
            Assert.That(CrossFadeResolver.IsCrossFadeName("cross-fade"), Is.True);
        }

        [Test]
        public void IsCrossFadeName_recognises_webkit_prefix() {
            Assert.That(CrossFadeResolver.IsCrossFadeName("-webkit-cross-fade"), Is.True);
        }

        [Test]
        public void IsCrossFadeName_rejects_other_names() {
            Assert.That(CrossFadeResolver.IsCrossFadeName("linear-gradient"), Is.False);
            Assert.That(CrossFadeResolver.IsCrossFadeName("image-set"), Is.False);
            Assert.That(CrossFadeResolver.IsCrossFadeName(null), Is.False);
            Assert.That(CrossFadeResolver.IsCrossFadeName(""), Is.False);
        }

        // ---- TryParse ----

        [Test]
        public void TryParse_three_arg_form_with_50pct() {
            bool ok = CrossFadeResolver.TryParse(
                "url(a.png), url(b.png), 50%",
                out var first, out var second, out var alpha);
            Assert.That(ok, Is.True);
            Assert.That(first, Is.EqualTo("url(a.png)"));
            Assert.That(second, Is.EqualTo("url(b.png)"));
            Assert.That(alpha, Is.EqualTo(0.5f).Within(1e-5f));
        }

        [Test]
        public void TryParse_three_arg_form_with_30pct() {
            bool ok = CrossFadeResolver.TryParse(
                "url(a.png), url(b.png), 30%",
                out _, out _, out var alpha);
            Assert.That(ok, Is.True);
            Assert.That(alpha, Is.EqualTo(0.30f).Within(1e-5f));
        }

        [Test]
        public void TryParse_two_arg_form_defaults_to_50pct() {
            // Chrome: two-arg form → defaults to 50%.
            bool ok = CrossFadeResolver.TryParse(
                "url(a.png), url(b.png)",
                out _, out _, out var alpha);
            Assert.That(ok, Is.True);
            Assert.That(alpha, Is.EqualTo(0.5f).Within(1e-5f));
        }

        [Test]
        public void TryParse_percentage_clamps_above_100() {
            bool ok = CrossFadeResolver.TryParse(
                "url(a.png), url(b.png), 150%",
                out _, out _, out var alpha);
            Assert.That(ok, Is.True);
            Assert.That(alpha, Is.EqualTo(1.0f).Within(1e-5f));
        }

        [Test]
        public void TryParse_percentage_clamps_below_0() {
            bool ok = CrossFadeResolver.TryParse(
                "url(a.png), url(b.png), -20%",
                out _, out _, out var alpha);
            Assert.That(ok, Is.True);
            Assert.That(alpha, Is.EqualTo(0.0f).Within(1e-5f));
        }

        [Test]
        public void TryParse_rejects_one_operand() {
            bool ok = CrossFadeResolver.TryParse(
                "url(a.png)",
                out _, out _, out _);
            Assert.That(ok, Is.False);
        }

        [Test]
        public void TryParse_rejects_four_args() {
            bool ok = CrossFadeResolver.TryParse(
                "url(a.png), url(b.png), url(c.png), 50%",
                out _, out _, out _);
            Assert.That(ok, Is.False);
        }

        [Test]
        public void TryParse_rejects_junk_percentage() {
            // Third argument is not a percentage → invalid.
            bool ok = CrossFadeResolver.TryParse(
                "url(a.png), url(b.png), notapercent",
                out _, out _, out _);
            Assert.That(ok, Is.False);
        }

        [Test]
        public void TryParse_rejects_empty_body() {
            bool ok = CrossFadeResolver.TryParse("", out _, out _, out _);
            Assert.That(ok, Is.False);
        }

        // ---- ResolveOperand ----

        [Test]
        public void ResolveOperand_url_returns_image_brush() {
            var brush = CrossFadeResolver.ResolveOperand(
                "url(foo.png)", Black, Bounds(), ImageRenderingMode.Auto, 1.0);
            Assert.That(brush, Is.Not.Null);
            Assert.That(brush.Kind, Is.EqualTo(BrushKind.Image));
            Assert.That(brush.ImageHandle, Is.EqualTo("foo.png"));
        }

        [Test]
        public void ResolveOperand_linear_gradient_returns_gradient_brush() {
            var brush = CrossFadeResolver.ResolveOperand(
                "linear-gradient(red, blue)", Black, Bounds(), ImageRenderingMode.Auto, 1.0);
            Assert.That(brush, Is.Not.Null);
            Assert.That(brush.Kind, Is.EqualTo(BrushKind.Gradient));
        }

        [Test]
        public void ResolveOperand_color_keyword_returns_solid_brush() {
            var brush = CrossFadeResolver.ResolveOperand(
                "red", Black, Bounds(), ImageRenderingMode.Auto, 1.0);
            Assert.That(brush, Is.Not.Null);
            Assert.That(brush.Kind, Is.EqualTo(BrushKind.SolidColor));
            Assert.That(brush.Color.R, Is.GreaterThan(0.9f));
        }

        [Test]
        public void ResolveOperand_unknown_returns_null() {
            var brush = CrossFadeResolver.ResolveOperand(
                "not-a-valid-image", Black, Bounds(), ImageRenderingMode.Auto, 1.0);
            Assert.That(brush, Is.Null);
        }

        // ---- TryExpandIntoLayers ----

        [Test]
        public void TryExpandIntoLayers_url_url_emits_two_layers() {
            var output = new List<Brush>();
            bool ok = CrossFadeResolver.TryExpandIntoLayers(
                "url(a.png), url(b.png), 30%",
                Black, Bounds(), ImageRenderingMode.Auto, 1.0, output);
            Assert.That(ok, Is.True);
            Assert.That(output.Count, Is.EqualTo(2));
        }

        [Test]
        public void TryExpandIntoLayers_first_layer_alpha_is_1_minus_p() {
            var output = new List<Brush>();
            CrossFadeResolver.TryExpandIntoLayers(
                "url(a.png), url(b.png), 30%",
                Black, Bounds(), ImageRenderingMode.Auto, 1.0, output);
            // p=30% → first at 70%, second at 30%
            Assert.That(output[0].LayerAlpha, Is.EqualTo(0.70f).Within(1e-4f));
        }

        [Test]
        public void TryExpandIntoLayers_second_layer_alpha_is_p() {
            var output = new List<Brush>();
            CrossFadeResolver.TryExpandIntoLayers(
                "url(a.png), url(b.png), 30%",
                Black, Bounds(), ImageRenderingMode.Auto, 1.0, output);
            Assert.That(output[1].LayerAlpha, Is.EqualTo(0.30f).Within(1e-4f));
        }

        [Test]
        public void TryExpandIntoLayers_default_50pct_gives_equal_alphas() {
            var output = new List<Brush>();
            CrossFadeResolver.TryExpandIntoLayers(
                "url(a.png), url(b.png)",
                Black, Bounds(), ImageRenderingMode.Auto, 1.0, output);
            Assert.That(output.Count, Is.EqualTo(2));
            Assert.That(output[0].LayerAlpha, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(output[1].LayerAlpha, Is.EqualTo(0.5f).Within(1e-4f));
        }

        [Test]
        public void TryExpandIntoLayers_p0_first_layer_has_full_alpha() {
            // p=0% → first at 100% (no alpha wrapping needed), second at 0%
            var output = new List<Brush>();
            CrossFadeResolver.TryExpandIntoLayers(
                "url(a.png), url(b.png), 0%",
                Black, Bounds(), ImageRenderingMode.Auto, 1.0, output);
            Assert.That(output[0].LayerAlpha, Is.EqualTo(1.0f).Within(1e-4f));
            Assert.That(output[1].LayerAlpha, Is.EqualTo(0.0f).Within(1e-4f));
        }

        [Test]
        public void TryExpandIntoLayers_p100_second_layer_has_full_alpha() {
            var output = new List<Brush>();
            CrossFadeResolver.TryExpandIntoLayers(
                "url(a.png), url(b.png), 100%",
                Black, Bounds(), ImageRenderingMode.Auto, 1.0, output);
            Assert.That(output[0].LayerAlpha, Is.EqualTo(0.0f).Within(1e-4f));
            Assert.That(output[1].LayerAlpha, Is.EqualTo(1.0f).Within(1e-4f));
        }

        [Test]
        public void TryExpandIntoLayers_gradient_plus_gradient() {
            var output = new List<Brush>();
            bool ok = CrossFadeResolver.TryExpandIntoLayers(
                "linear-gradient(red, blue), linear-gradient(green, yellow), 40%",
                Black, Bounds(), ImageRenderingMode.Auto, 1.0, output);
            Assert.That(ok, Is.True);
            Assert.That(output.Count, Is.EqualTo(2));
            Assert.That(output[0].Kind, Is.EqualTo(BrushKind.Gradient));
            Assert.That(output[1].Kind, Is.EqualTo(BrushKind.Gradient));
            Assert.That(output[0].LayerAlpha, Is.EqualTo(0.60f).Within(1e-4f));
            Assert.That(output[1].LayerAlpha, Is.EqualTo(0.40f).Within(1e-4f));
        }

        [Test]
        public void TryExpandIntoLayers_url_plus_gradient() {
            var output = new List<Brush>();
            bool ok = CrossFadeResolver.TryExpandIntoLayers(
                "url(bg.png), linear-gradient(red, blue), 25%",
                Black, Bounds(), ImageRenderingMode.Auto, 1.0, output);
            Assert.That(ok, Is.True);
            Assert.That(output[0].Kind, Is.EqualTo(BrushKind.Image));
            Assert.That(output[1].Kind, Is.EqualTo(BrushKind.Gradient));
        }

        [Test]
        public void TryExpandIntoLayers_invalid_operand_returns_false_and_no_layers() {
            var output = new List<Brush>();
            bool ok = CrossFadeResolver.TryExpandIntoLayers(
                "url(a.png), not-valid-image, 50%",
                Black, Bounds(), ImageRenderingMode.Auto, 1.0, output);
            Assert.That(ok, Is.False);
            Assert.That(output.Count, Is.EqualTo(0));
        }

        [Test]
        public void TryExpandIntoLayers_one_operand_returns_false() {
            var output = new List<Brush>();
            bool ok = CrossFadeResolver.TryExpandIntoLayers(
                "url(a.png)",
                Black, Bounds(), ImageRenderingMode.Auto, 1.0, output);
            Assert.That(ok, Is.False);
            Assert.That(output.Count, Is.EqualTo(0));
        }

        // ---- image-set() inside cross-fade() ----

        [Test]
        public void ResolveOperand_image_set_inside_cross_fade_resolves_handle() {
            // image-set() as a cross-fade operand: picks the 1x URL.
            var brush = CrossFadeResolver.ResolveOperand(
                "image-set(url(low.png) 1x, url(high.png) 2x)",
                Black, Bounds(), ImageRenderingMode.Auto, 1.0 /* dpr=1 */);
            Assert.That(brush, Is.Not.Null);
            Assert.That(brush.Kind, Is.EqualTo(BrushKind.Image));
            Assert.That(brush.ImageHandle, Is.EqualTo("low.png"));
        }

        [Test]
        public void TryExpandIntoLayers_image_set_operand_expanded_correctly() {
            var output = new List<Brush>();
            bool ok = CrossFadeResolver.TryExpandIntoLayers(
                "image-set(url(low.png) 1x, url(high.png) 2x), url(b.png), 60%",
                Black, Bounds(), ImageRenderingMode.Auto, 1.0, output);
            Assert.That(ok, Is.True);
            Assert.That(output.Count, Is.EqualTo(2));
            Assert.That(output[0].Kind, Is.EqualTo(BrushKind.Image));
            Assert.That(output[0].ImageHandle, Is.EqualTo("low.png"));
            Assert.That(output[0].LayerAlpha, Is.EqualTo(0.40f).Within(1e-4f));
        }

        // ---- Cascade round-trip via BackgroundResolver ----

        [Test]
        public void BackgroundResolver_cross_fade_url_url_emits_two_layers() {
            BackgroundResolver.ResetCaches_TestOnly();
            var s = Style();
            s.Set("background-image", "cross-fade(url(a.png), url(b.png), 40%)");
            s.Set("color", "black");
            var output = new List<Brush>();
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(), output, null);
            Assert.That(output.Count, Is.EqualTo(2));
            Assert.That(output[0].Kind, Is.EqualTo(BrushKind.Image));
            Assert.That(output[1].Kind, Is.EqualTo(BrushKind.Image));
        }

        [Test]
        public void BackgroundResolver_cross_fade_alphas_match_weight() {
            BackgroundResolver.ResetCaches_TestOnly();
            var s = Style();
            s.Set("background-image", "cross-fade(url(a.png), url(b.png), 40%)");
            s.Set("color", "black");
            var output = new List<Brush>();
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(), output, null);
            // p=40% → first layer at 60%, second at 40%
            Assert.That(output[0].LayerAlpha, Is.EqualTo(0.60f).Within(1e-4f));
            Assert.That(output[1].LayerAlpha, Is.EqualTo(0.40f).Within(1e-4f));
        }

        [Test]
        public void BackgroundResolver_webkit_cross_fade_also_works() {
            BackgroundResolver.ResetCaches_TestOnly();
            var s = Style();
            s.Set("background-image", "-webkit-cross-fade(url(a.png), url(b.png), 25%)");
            s.Set("color", "black");
            var output = new List<Brush>();
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(), output, null);
            Assert.That(output.Count, Is.EqualTo(2));
            Assert.That(output[0].LayerAlpha, Is.EqualTo(0.75f).Within(1e-4f));
            Assert.That(output[1].LayerAlpha, Is.EqualTo(0.25f).Within(1e-4f));
        }

        [Test]
        public void BackgroundResolver_cross_fade_default_percentage_is_50() {
            BackgroundResolver.ResetCaches_TestOnly();
            var s = Style();
            s.Set("background-image", "cross-fade(url(a.png), url(b.png))");
            s.Set("color", "black");
            var output = new List<Brush>();
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(), output, null);
            Assert.That(output.Count, Is.EqualTo(2));
            Assert.That(output[0].LayerAlpha, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(output[1].LayerAlpha, Is.EqualTo(0.5f).Within(1e-4f));
        }

        [Test]
        public void BackgroundResolver_cross_fade_gradient_operands() {
            BackgroundResolver.ResetCaches_TestOnly();
            var s = Style();
            s.Set("background-image",
                "cross-fade(linear-gradient(red, blue), linear-gradient(green, yellow), 70%)");
            s.Set("color", "black");
            var output = new List<Brush>();
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(), output, null);
            Assert.That(output.Count, Is.EqualTo(2));
            Assert.That(output[0].Kind, Is.EqualTo(BrushKind.Gradient));
            Assert.That(output[1].Kind, Is.EqualTo(BrushKind.Gradient));
            Assert.That(output[0].LayerAlpha, Is.EqualTo(0.30f).Within(1e-4f));
            Assert.That(output[1].LayerAlpha, Is.EqualTo(0.70f).Within(1e-4f));
        }

        [Test]
        public void BackgroundResolver_invalid_cross_fade_one_operand_yields_null_layer() {
            // One operand → invalid → Chrome discards declaration → null layer
            BackgroundResolver.ResetCaches_TestOnly();
            var s = Style();
            s.Set("background-image", "cross-fade(url(a.png))");
            s.Set("color", "black");
            var output = new List<Brush>();
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(), output, null);
            // The resolver emits a null placeholder (declaration dropped).
            Assert.That(output.Count, Is.EqualTo(1));
            Assert.That(output[0], Is.Null);
        }

        [Test]
        public void BackgroundResolver_invalid_percentage_yields_null_layer() {
            BackgroundResolver.ResetCaches_TestOnly();
            var s = Style();
            s.Set("background-image", "cross-fade(url(a.png), url(b.png), notapct)");
            s.Set("color", "black");
            var output = new List<Brush>();
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(), output, null);
            Assert.That(output.Count, Is.EqualTo(1));
            Assert.That(output[0], Is.Null);
        }

        [Test]
        public void BackgroundResolver_cross_fade_alongside_normal_layer() {
            // cross-fade() as one of two comma-separated background-image layers.
            BackgroundResolver.ResetCaches_TestOnly();
            var s = Style();
            s.Set("background-image",
                "linear-gradient(red, blue), cross-fade(url(a.png), url(b.png), 50%)");
            s.Set("color", "black");
            var output = new List<Brush>();
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(), output, null);
            // First declaration = gradient (1 layer), second = cross-fade (2 layers)
            Assert.That(output.Count, Is.EqualTo(3));
            Assert.That(output[0].Kind, Is.EqualTo(BrushKind.Gradient));
            Assert.That(output[1].Kind, Is.EqualTo(BrushKind.Image));
            Assert.That(output[2].Kind, Is.EqualTo(BrushKind.Image));
        }

        // ---- Brush.WithLayerAlpha ----

        [Test]
        public void Brush_WithLayerAlpha_preserves_kind_and_returns_new_instance() {
            var original = Brush.ImageFullRect("test.png", ImageRenderingMode.Auto);
            var modified = original.WithLayerAlpha(0.3f);
            Assert.That(modified, Is.Not.SameAs(original));
            Assert.That(modified.Kind, Is.EqualTo(BrushKind.Image));
            Assert.That(modified.ImageHandle, Is.EqualTo("test.png"));
            Assert.That(modified.LayerAlpha, Is.EqualTo(0.3f).Within(1e-5f));
        }

        [Test]
        public void Brush_default_LayerAlpha_is_1() {
            var brush = Brush.ImageFullRect("test.png", ImageRenderingMode.Auto);
            Assert.That(brush.LayerAlpha, Is.EqualTo(1.0f).Within(1e-5f));
        }

        [Test]
        public void Brush_SolidColor_default_LayerAlpha_is_1() {
            var brush = Brush.SolidColor(new LinearColor(1, 0, 0, 1));
            Assert.That(brush.LayerAlpha, Is.EqualTo(1.0f).Within(1e-5f));
        }

        // ---- BoxToPaintConverter emits opacity commands for cross-fade layers ----

        [Test]
        public void Converter_wraps_cross_fade_layers_in_push_pop_opacity() {
            BackgroundResolver.ResetCaches_TestOnly();
            var s = Style();
            s.Set("background-image", "cross-fade(url(a.png), url(b.png), 30%)");
            s.Set("color", "black");

            // Build the layer list that the converter would use.
            var output = new List<Brush>();
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(), output, null);
            Assert.That(output.Count, Is.EqualTo(2));

            // Both sub-layers have LayerAlpha < 1 → both need PushOpacity wrapping.
            bool firstNeedsWrap = output[0].LayerAlpha < 0.9999f;
            bool secondNeedsWrap = output[1].LayerAlpha < 0.9999f;
            Assert.That(firstNeedsWrap, Is.True, "first layer (70%) needs opacity wrap");
            Assert.That(secondNeedsWrap, Is.True, "second layer (30%) needs opacity wrap");
        }

        [Test]
        public void Converter_p0_first_layer_no_opacity_wrap_needed() {
            // p=0% → first at 100% → no wrap needed; second at 0% → wrap needed.
            var output = new List<Brush>();
            CrossFadeResolver.TryExpandIntoLayers(
                "url(a.png), url(b.png), 0%",
                Black, Bounds(), ImageRenderingMode.Auto, 1.0, output);
            bool firstNeedsWrap = output[0].LayerAlpha < 0.9999f;
            bool secondNeedsWrap = output[1].LayerAlpha < 0.9999f;
            Assert.That(firstNeedsWrap, Is.False, "p=0: first is at full alpha");
            Assert.That(secondNeedsWrap, Is.True, "p=0: second is at 0% — still wraps");
        }

        // ---- image-set() round-trip through BackgroundResolver ----

        [Test]
        public void BackgroundResolver_image_set_inside_cross_fade_resolves() {
            BackgroundResolver.ResetCaches_TestOnly();
            var s = Style();
            // image-set() as first operand of cross-fade()
            s.Set("background-image",
                "cross-fade(image-set(url(low.png) 1x, url(high.png) 2x), url(b.png), 60%)");
            s.Set("color", "black");
            var output = new List<Brush>();
            // DPR=1 (default LengthContext)
            BackgroundResolver.ResolveBackgroundLayersInto(s, Bounds(), output, null);
            Assert.That(output.Count, Is.EqualTo(2));
            Assert.That(output[0].Kind, Is.EqualTo(BrushKind.Image));
            // At DPR=1, image-set picks "low.png"
            Assert.That(output[0].ImageHandle, Is.EqualTo("low.png"));
        }
    }
}
