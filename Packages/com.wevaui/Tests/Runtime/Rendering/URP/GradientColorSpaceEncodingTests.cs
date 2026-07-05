#if WEVA_URP
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Weva.Css.Values;
using Weva.Paint;
using Weva.Rendering.URP;
using Rect = Weva.Paint.Rect;

namespace Weva.Tests.Rendering.URP {
    // G1b — Gradient colour-space encoding in per-instance data.
    //
    // The shader reads `BrushParams.w` (linear) or `BorderStyles.x` (conic /
    // radial) and decodes 3 states from one float channel:
    //   0 = linear-RGB  (positive integer)
    //   1 = sRGB        (negative integer; legacy default)
    //   2 = Oklab       (negative value with +0.25 fractional offset)
    // The 0.25 offset is invisible to the existing
    // `(int)(abs(val) + 0.5)` magnitude readout, so the stop count still
    // decodes correctly across all three states.
    //
    // These tests pin the C# packing only — the HLSL branch has no unit-test
    // path. Visual validation in Unity play mode is required (see the
    // commit-message note for the engineer's checklist).
    public class GradientColorSpaceEncodingTests {
        static LinearColor C(float r, float g, float b) => new LinearColor(r, g, b, 1f);

        static List<GradientStop> RedBlueStops() => new List<GradientStop> {
            new GradientStop(C(1, 0, 0), 0.0),
            new GradientStop(C(0, 0, 1), 1.0),
        };

        [Test]
        public void Linear_gradient_in_oklab_packs_negative_count_with_quarter_offset() {
            // `linear-gradient(in oklab, red, blue)` must encode as -2.25 in
            // BrushParams.w: the sign-bit stays negative (Oklab shares the
            // non-linear-RGB sign with sRGB) and the magnitude carries a 0.25
            // fractional offset so the shader's `frac(abs(val)) > 0.1` check
            // routes the fragment through the Oklab lerp branch.
            var grad = new LinearGradient(0, RedBlueStops(), CssColorSpace.Oklab);
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();

            var inst = batcher.Batches[0].Instances[0];
            Assert.That(inst.BrushParams.w, Is.EqualTo(-2.25f).Within(1e-5f),
                "Oklab linear gradient encodes as -(count + 0.25) in BrushParams.w");
            // Sanity: the shader's count readout still recovers an integer 2.
            Assert.That(Mathf.RoundToInt(Mathf.Abs(inst.BrushParams.w)), Is.EqualTo(2),
                "Oklab encoding must remain count-decodable by the existing GPU readout");
            // Fractional part is the Oklab discriminator.
            float frac = Mathf.Abs(inst.BrushParams.w) - Mathf.Floor(Mathf.Abs(inst.BrushParams.w));
            Assert.That(frac, Is.GreaterThan(0.1f), "Oklab branch is detected by frac(abs(val)) > 0.1");
        }

        [Test]
        public void Linear_gradient_in_srgb_keeps_legacy_integer_negative_encoding() {
            // Regression pin: explicit `in srgb` must stay at the legacy
            // signed-integer encoding (-2 for a 2-stop gradient) so the
            // shader's existing sRGB branch still dispatches and the
            // pre-G1b cached uploads continue to render unchanged.
            var grad = new LinearGradient(0, RedBlueStops(), CssColorSpace.Srgb);
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();

            var inst = batcher.Batches[0].Instances[0];
            Assert.That(inst.BrushParams.w, Is.EqualTo(-2f).Within(1e-5f),
                "sRGB encoding stays at the legacy -count integer value");
            float frac = Mathf.Abs(inst.BrushParams.w) - Mathf.Floor(Mathf.Abs(inst.BrushParams.w));
            Assert.That(frac, Is.LessThan(0.1f),
                "sRGB encoding must NOT trip the Oklab fractional-offset detector");
        }

        [Test]
        public void Linear_gradient_with_no_in_keyword_still_defaults_to_srgb_until_g1_flip() {
            // CSS Images 4 §3.4 makes Oklab the spec default, but G1's tracker
            // explicitly gates that flip on G1b PLUS visual verification. Until
            // the engineer flips the default, a bare `linear-gradient(red, blue)`
            // (no `in <space>`) must continue to encode as sRGB (-2). When G1
            // flips the default this test will need its expected value updated
            // to the Oklab encoding (-2.25); failing this test is the marker.
            var grad = new LinearGradient(0, RedBlueStops());
            Assert.That(grad.InterpolationSpace, Is.EqualTo(CssColorSpace.Srgb),
                "Gradient constructor default must still be sRGB pre-G1 flip");

            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();
            var inst = batcher.Batches[0].Instances[0];
            Assert.That(inst.BrushParams.w, Is.EqualTo(-2f).Within(1e-5f),
                "Bare linear-gradient(red, blue) still encodes as sRGB pre-G1 default-flip");
        }

        [Test]
        public void Linear_gradient_in_linear_rgb_packs_positive_integer() {
            // Pin the third corner of the 3-state encoding: linear-RGB is the
            // *positive* sign path. Distinct from both sRGB (negative integer)
            // and Oklab (negative + .25). This test guards against a future
            // refactor accidentally collapsing linear-RGB into the Oklab branch.
            var grad = new LinearGradient(0, RedBlueStops(), CssColorSpace.LinearRgb);
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();

            var inst = batcher.Batches[0].Instances[0];
            Assert.That(inst.BrushParams.w, Is.EqualTo(2f).Within(1e-5f),
                "linear-RGB encoding is +count (positive sign, integer magnitude)");
        }

        [Test]
        public void Conic_gradient_in_oklab_packs_into_border_styles_x_with_quarter_offset() {
            // Conic gradients carry the count/colorspace in BorderStyles.x
            // instead of BrushParams.w (BrushParams.w holds the from-angle).
            // The same 3-state encoding must apply: -count - 0.25 for Oklab.
            var stops = new List<GradientStop> {
                new GradientStop(C(1, 0, 0), 0.0),
                new GradientStop(C(0, 1, 0), 0.5),
                new GradientStop(C(0, 0, 1), 1.0),
            };
            var grad = new ConicGradient(0, 0.5, 0.5, stops, CssColorSpace.Oklab);
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();

            var inst = batcher.Batches[0].Instances[0];
            Assert.That(inst.BorderStyles.x, Is.EqualTo(-3.25f).Within(1e-5f),
                "Oklab conic gradient encodes as -(count + 0.25) in BorderStyles.x");
            Assert.That(Mathf.RoundToInt(Mathf.Abs(inst.BorderStyles.x)), Is.EqualTo(3),
                "Conic Oklab encoding stays count-decodable by the GPU magnitude readout");
        }
    }
}
#endif
