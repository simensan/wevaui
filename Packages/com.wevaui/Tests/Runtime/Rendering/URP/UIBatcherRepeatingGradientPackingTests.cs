// WEVA_URP_BATCHER_TESTS is the Tests asmdef's URP versionDefine (the
// Tests asmdef does NOT see Runtime's WEVA_URP — using that gate here would
// silently compile the whole file out). Matches UIBatcherTests.cs et al.
#if WEVA_URP_BATCHER_TESTS
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Weva.Css.Values;
using Weva.Paint;
using Weva.Rendering.URP;
using Rect = Weva.Paint.Rect;

namespace Weva.Tests.Rendering.URP {
    // G13c: UIBatcher packing tests for repeating-conic-gradient and
    // repeating-radial-gradient. These pin the per-instance channel values
    // that the GPU shader relies on to derive the tiling period and dispatch
    // the correct wrap sampler.
    //
    // Channel map for repeating gradients (gradient quads, isBordered=false):
    //   BorderColorLeft  (slot 8): (p0, p1, p2, p3) stop positions in [0,1]
    //     For 2-stop repeating conic (promoted to 3-stop): (0, p0, p1, p1)
    //     For repeating radial: positions normalized by RadiusX for px stops
    //   BorderStyles     (slot 9): (encCount, p4, p5, isRepeating)
    //     isRepeating = BorderStyles.w: 1.0 = repeating, 0.0 = non-repeating
    public class UIBatcherRepeatingGradientPackingTests {
        static readonly LinearColor Red  = new LinearColor(1f, 0f, 0f, 1f);
        static readonly LinearColor Blue = new LinearColor(0f, 0f, 1f, 1f);

        // ───────────────────────── conic ─────────────────────────

        // A 2-stop repeating conic with non-default period (45deg = 0.125 turn)
        // is promoted to a 3-stop encoding. BorderStyles.w must be 1 (repeating)
        // and BorderColorLeft.z (= positions.z, the period for a 3-stop) must
        // equal 0.125 so the wrap sampler tiles every 45°.
        [Test]
        public void Repeating_conic_2stop_nondefault_period_packs_period_in_slot8_z() {
            var stops = new List<GradientStop> {
                new GradientStop(Red,  0.0),     // 0° (implicit start)
                new GradientStop(Blue, 0.125),   // 45° / 360 = 0.125 in turn space
            };
            var grad = new ConicGradient(0.0, 100, 100, stops,
                CssColorSpace.Srgb,
                CssHueInterpolationMethod.Shorter,
                isRepeating: true);

            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 200, 200), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();

            var inst = batcher.Batches[0].Instances[0];
            // Promoted to 3-stop: positions = (0, p0, p1, p1) = (0, 0, 0.125, 0.125)
            Assert.That(inst.BorderColorLeft.x, Is.EqualTo(0.0f).Within(1e-5f), "positions.x (base) = 0");
            Assert.That(inst.BorderColorLeft.y, Is.EqualTo(0.0f).Within(1e-5f), "positions.y (p0) = 0");
            Assert.That(inst.BorderColorLeft.z, Is.EqualTo(0.125f).Within(1e-5f), "positions.z (period=45deg) = 0.125");
            Assert.That(inst.BorderColorLeft.w, Is.EqualTo(0.125f).Within(1e-5f), "positions.w = p1 duplicate");
            // isRepeating flag in BorderStyles.w
            Assert.That(inst.BorderStyles.w, Is.EqualTo(1.0f).Within(1e-5f), "BorderStyles.w = repeating flag");
        }

        // A 2-stop repeating conic with DEFAULT positions (0→1, period=1 turn)
        // is also promoted to a 3-stop encoding so the wrap sampler always has
        // valid positions. BorderColorLeft.z = 1.0 (period = full circle).
        [Test]
        public void Repeating_conic_2stop_default_period_is_promoted_and_packs_period_1() {
            var stops = new List<GradientStop> {
                new GradientStop(Red,  0.0),
                new GradientStop(Blue, 1.0),  // default last-stop = full circle
            };
            var grad = new ConicGradient(0.0, 50, 50, stops,
                CssColorSpace.Srgb,
                CssHueInterpolationMethod.Shorter,
                isRepeating: true);

            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();

            var inst = batcher.Batches[0].Instances[0];
            // Promoted to 3-stop even for default positions. positions.z = 1.0.
            Assert.That(inst.BorderColorLeft.z, Is.EqualTo(1.0f).Within(1e-5f), "period = 1.0 for full-circle");
            Assert.That(inst.BorderStyles.w, Is.EqualTo(1.0f).Within(1e-5f), "BorderStyles.w = repeating flag");
        }

        // Regression: a non-repeating 2-stop conic with default positions must NOT
        // set BorderStyles.w=1 (would accidentally route through the wrap sampler).
        [Test]
        public void Non_repeating_conic_2stop_does_not_set_repeating_flag() {
            var stops = new List<GradientStop> {
                new GradientStop(Red,  0.0),
                new GradientStop(Blue, 1.0),
            };
            var grad = new ConicGradient(0.0, 50, 50, stops,
                CssColorSpace.Srgb,
                CssHueInterpolationMethod.Shorter,
                isRepeating: false);

            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();

            var inst = batcher.Batches[0].Instances[0];
            // Non-repeating: flag must be 0; 2-stop fast path doesn't need promoted positions.
            Assert.That(inst.BorderStyles.w, Is.EqualTo(0.0f).Within(1e-5f), "no repeating flag for non-repeating");
        }

        // ───────────────────────── radial ─────────────────────────

        // A 2-stop repeating-radial-gradient with a px stop (`30px`) must pack
        // the period as 30/RadiusX (not 1.0 from NormalizedPos's clamp).
        // RadiusX for `circle farthest-corner` on a 180x180 box is
        // 90*sqrt(2) ≈ 127.28px. Period = 30/127.28 ≈ 0.2357.
        [Test]
        public void Repeating_radial_px_stop_packs_period_as_fraction_of_radius() {
            // 30px as a raw pixel value (> 1.0, so RepeatingRadialNormalizedPos divides by RadiusX).
            var stops = new List<GradientStop> {
                new GradientStop(Red,  0.0),
                new GradientStop(Blue, 30.0), // raw px
            };
            // 180x180 box. circle + farthest-corner: rx = 90*sqrt(2) ≈ 127.28
            const double rx = 90.0 * 1.41421356;
            var rad = new RadialGradient(90, 90, rx, rx,
                RadialGradientShape.Circle, stops,
                CssColorSpace.Srgb,
                CssHueInterpolationMethod.Shorter,
                isRepeating: true);

            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 180, 180), Brush.Gradient(rad), BorderRadii.Zero);
            batcher.Finish();

            var inst = batcher.Batches[0].Instances[0];
            float expectedPeriod = (float)(30.0 / rx);
            // BorderColorLeft.y = positions.y = rp1 = 30px / RadiusX ≈ 0.2357
            Assert.That(inst.BorderColorLeft.y, Is.EqualTo(expectedPeriod).Within(1e-4f),
                "positions.y should be 30/RadiusX (not 1.0)");
            // BorderStyles.w = repeating flag = 1
            Assert.That(inst.BorderStyles.w, Is.EqualTo(1.0f).Within(1e-5f),
                "BorderStyles.w = repeating flag");
        }

        // A repeating-radial-gradient with a percentage stop (`30%` = 0.30) should
        // pack the period as 0.30 (passthrough, no radius normalization).
        [Test]
        public void Repeating_radial_percent_stop_packs_fraction_unchanged() {
            var stops = new List<GradientStop> {
                new GradientStop(Red,  0.0),
                new GradientStop(Blue, 0.30), // 30% as fraction
            };
            const double rx = 90.0 * 1.41421356;
            var rad = new RadialGradient(90, 90, rx, rx,
                RadialGradientShape.Circle, stops,
                CssColorSpace.Srgb,
                CssHueInterpolationMethod.Shorter,
                isRepeating: true);

            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 180, 180), Brush.Gradient(rad), BorderRadii.Zero);
            batcher.Finish();

            var inst = batcher.Batches[0].Instances[0];
            // positions.y = 0.30 (fraction stops pass through)
            Assert.That(inst.BorderColorLeft.y, Is.EqualTo(0.30f).Within(1e-4f),
                "positions.y should be 0.30 (percent fraction, no radius normalization)");
            Assert.That(inst.BorderStyles.w, Is.EqualTo(1.0f).Within(1e-5f),
                "BorderStyles.w = repeating flag");
        }

        // Regression: non-repeating radial with a px stop must still pack 1.0
        // (clamped by NormalizedPos). The non-repeating path is unaffected.
        [Test]
        public void Non_repeating_radial_px_stop_still_clamps_to_1() {
            var stops = new List<GradientStop> {
                new GradientStop(Red,  0.0),
                new GradientStop(Blue, 30.0), // raw px
            };
            const double rx = 90.0 * 1.41421356;
            var rad = new RadialGradient(90, 90, rx, rx,
                RadialGradientShape.Circle, stops,
                CssColorSpace.Srgb,
                CssHueInterpolationMethod.Shorter,
                isRepeating: false);

            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 180, 180), Brush.Gradient(rad), BorderRadii.Zero);
            batcher.Finish();

            var inst = batcher.Batches[0].Instances[0];
            // Non-repeating path uses NormalizedPos which clamps 30 -> 1.0.
            Assert.That(inst.BorderColorLeft.y, Is.EqualTo(1.0f).Within(1e-5f),
                "non-repeating path clamps px position to 1.0");
            Assert.That(inst.BorderStyles.w, Is.EqualTo(0.0f).Within(1e-5f),
                "no repeating flag");
        }
    }
}
#endif
