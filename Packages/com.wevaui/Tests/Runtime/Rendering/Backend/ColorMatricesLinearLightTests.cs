#if WEVA_URP
using NUnit.Framework;
using UnityEngine;
using Weva.Rendering;

namespace Weva.Tests.Rendering.Backend {
    // Regression coverage for CSS_COMPLIANCE_ISSUES.md K1:
    //   CSS Filter Effects 1 §6.1 — color-matrix filter primitives operate in
    //   linear-light RGB (per the `linearRGB` default of `color-interpolation-filters`
    //   on filter primitives). The CSS filter() shorthand functions (grayscale, sepia,
    //   saturate, hue-rotate, …) are defined in §10.1 as `feColorMatrix` over that
    //   linear-light domain. Under URP's default Linear color space the intermediate
    //   filter RT already holds premultiplied linear-light values (UIShaderLib.hlsl's
    //   `_WevaRawFilterOutput` toggle bypasses the sRGB encode when content paints
    //   into the filter RT), so `ColorMatrices` is supposed to produce the spec's
    //   linear-light response.
    //
    // Pre-K1, `Grayscale` used Rec.601 weights (0.299/0.587/0.114 — SDTV gamma-domain
    // luma) instead of the spec's Rec.709 luminance (0.2126/0.7152/0.0722). The pure
    // `ColorMatrices.Evaluate` path is a CPU function; we exercise it directly and pin
    // the expected linear-light outputs against the spec's anchor values.
    //
    // These tests intentionally exercise raw matrix output (no saturate/clamp), so
    // values can exceed 1.0 — the shader saturates downstream.
    public class ColorMatricesLinearLightTests {
        const float Eps = 1e-5f;

        // CSS Filter Effects 1 §10.1: `grayscale(1)` on pure red (1,0,0) linear-light
        // collapses to the luminance weight 0.2126 — NOT the Rec.601 0.299 byte-domain
        // value the legacy implementation produced. After encoding back through sRGB
        // this lands at ~127/255 in the framebuffer, matching Chrome's output for
        // `filter: grayscale(1)` on a `color: red` element.
        [Test]
        public void Grayscale_full_on_linear_red_uses_rec709_luminance_K1() {
            var m = ColorMatrices.Grayscale(1.0);
            var red = new Vector4(1, 0, 0, 1);
            var outVec = ColorMatrices.Evaluate(m, red);

            // Spec Rec.709 luminance for pure-red linear-light = 0.2126.
            // (Legacy Rec.601 would have given 0.299; that case is explicitly excluded.)
            Assert.That(outVec.x, Is.EqualTo(0.2126f).Within(Eps),
                "grayscale(1) on linear red must use Rec.709 luminance 0.2126 per CSS Filter Effects 1 §10.1.");
            Assert.That(outVec.y, Is.EqualTo(0.2126f).Within(Eps));
            Assert.That(outVec.z, Is.EqualTo(0.2126f).Within(Eps));
            // Alpha row is identity for grayscale.
            Assert.That(outVec.w, Is.EqualTo(1f).Within(Eps));

            // Explicit regression guard against the pre-K1 Rec.601 behaviour.
            // (Avoid `Is.Not.EqualTo(...).Within(eps)` chaining per project's NUnit
            // pitfalls — assert the absolute delta instead.)
            float deltaFromRec601 = System.Math.Abs(outVec.x - 0.299f);
            Assert.That(deltaFromRec601, Is.GreaterThan(0.01f),
                "Rec.601 0.299 weight regressed — Grayscale must use Rec.709 0.2126 per spec.");
        }

        // Grayscale(1) on green and blue: spec luminance is 0.7152 and 0.0722
        // respectively. Confirms the three Rec.709 channel weights all land per spec
        // (not just the red channel) — guards against a partial Rec.601→Rec.709
        // migration that only fixed one channel.
        [Test]
        public void Grayscale_full_on_linear_green_and_blue_use_rec709_luminance_K1() {
            var m = ColorMatrices.Grayscale(1.0);

            var green = ColorMatrices.Evaluate(m, new Vector4(0, 1, 0, 1));
            Assert.That(green.x, Is.EqualTo(0.7152f).Within(Eps),
                "grayscale(1) on linear green must use Rec.709 luminance 0.7152.");
            Assert.That(green.y, Is.EqualTo(0.7152f).Within(Eps));
            Assert.That(green.z, Is.EqualTo(0.7152f).Within(Eps));

            var blue = ColorMatrices.Evaluate(m, new Vector4(0, 0, 1, 1));
            Assert.That(blue.x, Is.EqualTo(0.0722f).Within(Eps),
                "grayscale(1) on linear blue must use Rec.709 luminance 0.0722.");
            Assert.That(blue.y, Is.EqualTo(0.0722f).Within(Eps));
            Assert.That(blue.z, Is.EqualTo(0.0722f).Within(Eps));

            // Sum of weights == 1 (energy conservation) per Rec.709.
            Assert.That(green.x + blue.x + 0.2126f, Is.EqualTo(1f).Within(Eps));
        }

        // Identity matrix passes RGB and alpha through unchanged regardless of input
        // encoding (linear or gamma). Pins the no-op contract that the rest of the
        // suite implicitly relies on.
        [Test]
        public void Identity_matrix_is_no_op_for_arbitrary_input_K1() {
            var m = ColorMatrix.Identity;
            // A mix of channel values and alphas exercises every row including alpha.
            var inputs = new[] {
                new Vector4(0, 0, 0, 0),
                new Vector4(1, 1, 1, 1),
                new Vector4(0.5f, 0.25f, 0.75f, 0.8f),
                new Vector4(0.2126f, 0.7152f, 0.0722f, 0.5f),
                new Vector4(2.0f, -0.1f, 0.3f, 1.0f), // out-of-gamut; Evaluate is unsaturated.
            };
            foreach (var v in inputs) {
                var outVec = ColorMatrices.Evaluate(m, v);
                Assert.That(outVec.x, Is.EqualTo(v.x).Within(Eps), $"Identity changed R on {v}");
                Assert.That(outVec.y, Is.EqualTo(v.y).Within(Eps), $"Identity changed G on {v}");
                Assert.That(outVec.z, Is.EqualTo(v.z).Within(Eps), $"Identity changed B on {v}");
                Assert.That(outVec.w, Is.EqualTo(v.w).Within(Eps), $"Identity changed A on {v}");
            }
        }

        // CSS Filter Effects 1 §10.1: `saturate(0)` must equal `grayscale(1)` — both
        // are defined as the same `feColorMatrix` full-desaturation result with
        // Rec.709 weights. Pre-K1 they diverged (Saturate used 0.213/0.715/0.072 ≈ Rec.709,
        // Grayscale used 0.299/0.587/0.114 Rec.601). This invariant now holds bit-for-bit
        // up to the small rounding differences between the two literal sets
        // (0.2126 vs 0.213; 0.7152 vs 0.715; 0.0722 vs 0.072).
        [Test]
        public void Saturate_zero_matches_Grayscale_one_within_literal_rounding_K1() {
            var sat0 = ColorMatrices.Saturate(0.0);
            var gray1 = ColorMatrices.Grayscale(1.0);

            // Apply both to a colourful linear-light sample; rows should match within
            // the 0.0004 max abs delta between the two literal forms of Rec.709.
            var v = new Vector4(0.6f, 0.3f, 0.9f, 1f);
            var rSat = ColorMatrices.Evaluate(sat0, v);
            var rGray = ColorMatrices.Evaluate(gray1, v);
            const float LiteralRoundingTol = 1e-3f;
            Assert.That(rSat.x, Is.EqualTo(rGray.x).Within(LiteralRoundingTol),
                "saturate(0) and grayscale(1) must collapse to the same luminance.");
            Assert.That(rSat.y, Is.EqualTo(rGray.y).Within(LiteralRoundingTol));
            Assert.That(rSat.z, Is.EqualTo(rGray.z).Within(LiteralRoundingTol));
            // Both are equal to the same scalar (it's a full desaturation).
            Assert.That(rSat.x, Is.EqualTo(rSat.y).Within(Eps),
                "saturate(0) row outputs must be identical across channels.");
            Assert.That(rGray.x, Is.EqualTo(rGray.y).Within(Eps),
                "grayscale(1) row outputs must be identical across channels.");
        }

        // Sepia(0) is identity per spec (CSS Filter Effects 1 §10.1: the matrix
        // collapses to identity at amount=0). Sanity check that holds in any color
        // space / encoding — the matrix-side guarantee is purely algebraic.
        [Test]
        public void Sepia_zero_is_identity_K1() {
            var m = ColorMatrices.Sepia(0.0);
            var v = new Vector4(0.6f, 0.3f, 0.9f, 1f);
            var outVec = ColorMatrices.Evaluate(m, v);
            Assert.That(outVec.x, Is.EqualTo(v.x).Within(Eps));
            Assert.That(outVec.y, Is.EqualTo(v.y).Within(Eps));
            Assert.That(outVec.z, Is.EqualTo(v.z).Within(Eps));
            Assert.That(outVec.w, Is.EqualTo(v.w).Within(Eps));
        }

        // Sepia(1) on linear white: spec produces (1.351, 1.203, 0.937). After the
        // shader's saturate() this lands at (1.0, 1.0, 0.937), which is the slightly
        // warm off-white Chrome paints for `filter: sepia(1)` on a white element.
        // We pin the raw (pre-saturate) matrix output to lock the spec coefficients —
        // a change to the canonical sepia matrix would silently shift this triple.
        [Test]
        public void Sepia_full_on_linear_white_matches_spec_coefficients_K1() {
            var m = ColorMatrices.Sepia(1.0);
            var outVec = ColorMatrices.Evaluate(m, new Vector4(1, 1, 1, 1));
            // Sum of spec sepia row weights — held to 1e-5 because the spec values
            // are 3-decimal literals.
            Assert.That(outVec.x, Is.EqualTo(0.393f + 0.769f + 0.189f).Within(Eps));
            Assert.That(outVec.y, Is.EqualTo(0.349f + 0.686f + 0.168f).Within(Eps));
            Assert.That(outVec.z, Is.EqualTo(0.272f + 0.534f + 0.131f).Within(Eps));
            // Channel ordering: R > G > B (warm tint).
            Assert.That(outVec.x, Is.GreaterThan(outVec.y));
            Assert.That(outVec.y, Is.GreaterThan(outVec.z));
            Assert.That(outVec.w, Is.EqualTo(1f).Within(Eps));
        }

        // CSS Filter Effects 1 §10.1: `invert(1)` is x → 1-x per channel. On linear
        // black (0,0,0) it returns white (1,1,1) regardless of color space — the
        // matrix is encoding-agnostic for the endpoint cases. Sanity guard.
        [Test]
        public void Invert_full_on_black_is_white_K1() {
            var m = ColorMatrices.Invert(1.0);
            var outVec = ColorMatrices.Evaluate(m, new Vector4(0, 0, 0, 1));
            Assert.That(outVec.x, Is.EqualTo(1f).Within(Eps));
            Assert.That(outVec.y, Is.EqualTo(1f).Within(Eps));
            Assert.That(outVec.z, Is.EqualTo(1f).Within(Eps));
            Assert.That(outVec.w, Is.EqualTo(1f).Within(Eps));
        }

        // CSS Filter Effects 1 §10.1: `brightness(amount)` scales rgb by the amount
        // factor, in the matrix's operating domain. Under linear-light this gives a
        // perceptually correct exposure-style brighten; doubling a mid-grey (0.5)
        // lands at 1.0 (pre-saturate). Pre-K1 the same expression applied to gamma
        // values would have over-brightened — pin the linear-domain math here.
        [Test]
        public void Brightness_two_on_linear_midgrey_doubles_in_linear_domain_K1() {
            var m = ColorMatrices.Brightness(2.0);
            var outVec = ColorMatrices.Evaluate(m, new Vector4(0.5f, 0.5f, 0.5f, 1f));
            Assert.That(outVec.x, Is.EqualTo(1f).Within(Eps));
            Assert.That(outVec.y, Is.EqualTo(1f).Within(Eps));
            Assert.That(outVec.z, Is.EqualTo(1f).Within(Eps));
            Assert.That(outVec.w, Is.EqualTo(1f).Within(Eps));
        }
    }
}
#endif
