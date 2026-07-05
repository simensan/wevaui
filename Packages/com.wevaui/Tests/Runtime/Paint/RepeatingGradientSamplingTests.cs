using System.Collections.Generic;
using NUnit.Framework;
using Weva.Paint;
using Weva.Testing.Goldens;

namespace Weva.Tests.Paint {
    // G13c: CPU-side wrap tests for repeating-conic-gradient and
    // repeating-radial-gradient. We exercise SoftwareRasterizer directly:
    // submit a single rect filled with the gradient, then read the pixel at a
    // known angle/radius and assert the wrapped color. The non-repeating
    // counterparts are pinned to keep clamping behavior intact so the wrap
    // path is a strict opt-in driven by the IsRepeating flag.
    public class RepeatingGradientSamplingTests {
        static readonly LinearColor Red = new LinearColor(1f, 0f, 0f, 1f);
        static readonly LinearColor Blue = new LinearColor(0f, 0f, 1f, 1f);

        // Conic angle convention (per ConicGradient.SampleAtPixel): 0deg
        // points "to top", increases clockwise — so a sample pixel directly
        // south of the center sits at 180deg. With a repeating conic of
        // period 90deg (red at 0, blue at 90deg → normalized 0.25), 180deg
        // wraps via frac(180/90)=0 → red.
        [Test]
        public void Repeating_conic_at_180deg_with_90deg_period_wraps_to_red() {
            var stops = new List<GradientStop> {
                new GradientStop(Red, 0.0),
                // 90deg out of 360 = 0.25 in normalized turn space.
                new GradientStop(Blue, 0.25),
            };
            var conic = new ConicGradient(0.0, 50, 50, stops,
                Weva.Css.Values.CssColorSpace.Srgb,
                Weva.Css.Values.CssHueInterpolationMethod.Shorter,
                isRepeating: true);

            // South of the center: dx=0, dy=+10. atan2(0, -(-(-10))) =
            // atan2(0, -10) is undefined for the standard; the code uses
            // atan2(dx, -dy) = atan2(0, -10) → pi (180deg). Good.
            var col = SoftwareRasterizer.SampleConicWithRepeating(conic, 50, 60);

            // 180 / 90 = 2 sweeps, wrap → 0deg → red. Without the wrap, the
            // clamp would land on blue (the last stop).
            Assert.That(col.R, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(col.B, Is.EqualTo(0f).Within(1e-4f));
        }

        // Mid-period sanity: a sample at 45deg (NE) inside the first 90deg
        // period interpolates red→blue at t=0.5 inside the first stop span,
        // producing a magenta-ish mix (sRGB-lerp curve, so the linear-space
        // channel values are well below 0.5 — but both channels are non-
        // zero and similar, which is the signature). The point of this test
        // is to pin "wrap math doesn't break the primary period" — i.e. a
        // sample that should NOT wrap still produces an in-period mix.
        [Test]
        public void Repeating_conic_at_45deg_inside_first_period_is_midpoint_red_blue() {
            var stops = new List<GradientStop> {
                new GradientStop(Red, 0.0),
                new GradientStop(Blue, 0.25),
            };
            var conic = new ConicGradient(0.0, 50, 50, stops,
                Weva.Css.Values.CssColorSpace.Srgb,
                Weva.Css.Values.CssHueInterpolationMethod.Shorter,
                isRepeating: true);

            // 45deg ≈ NE. dx=+1, dy=-1 → atan2(1, 1) = pi/4 (45deg).
            // 45 / 90 = 0.5 of the way red→blue.
            var col = SoftwareRasterizer.SampleConicWithRepeating(conic, 51, 49);

            // Both channels present, neither saturated to the endpoint.
            Assert.That(col.R, Is.GreaterThan(0.05f));
            Assert.That(col.B, Is.GreaterThan(0.05f));
            Assert.That(col.R, Is.LessThan(0.95f));
            Assert.That(col.B, Is.LessThan(0.95f));
        }

        // Regression pin: non-repeating conic must NOT wrap. A 270deg-out-of-
        // 360 gradient (red 0 → blue 90deg) sampled at 180deg should clamp
        // to the last stop color (blue) because 180 > 90.
        [Test]
        public void Non_repeating_conic_at_180deg_still_clamps_to_last_stop() {
            var stops = new List<GradientStop> {
                new GradientStop(Red, 0.0),
                new GradientStop(Blue, 0.25),
            };
            var conic = new ConicGradient(0.0, 50, 50, stops,
                Weva.Css.Values.CssColorSpace.Srgb,
                Weva.Css.Values.CssHueInterpolationMethod.Shorter,
                isRepeating: false);

            // Same southward sample as the repeating test above.
            var col = SoftwareRasterizer.SampleConicWithRepeating(conic, 50, 60);

            // Clamped to last stop (blue). If the wrap accidentally fires the
            // sample would land back on red — the assertion catches that.
            Assert.That(col.B, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(col.R, Is.EqualTo(0f).Within(1e-4f));
        }

        // For radial we drive the full FillRectCommand path through
        // SoftwareRasterizer so the IsRepeating flag is honored end-to-end.
        // 200x200 framebuffer; gradient centered at (100, 100) with
        // RadiusX/RadiusY = 40. Stops red @ 0%, blue @ 100%. At a sample
        // pixel (170, 100), distance from center = 70 = 1.75 R, wrapped via
        // frac(1.75) = 0.75 → 75% red→blue ≈ mostly blue. Without the wrap
        // the rasterizer would clamp t→1 → SOLID blue (which the non-
        // repeating regression below pins). At (180, 100) distance = 80 =
        // 2.0 R, wrap → 0.0 → solid red — the smoking gun for the wrap.
        [Test]
        public void Repeating_radial_past_radius_wraps_to_first_stop() {
            var stops = new List<GradientStop> {
                new GradientStop(Red, 0.0),
                new GradientStop(Blue, 1.0),
            };
            var rad = new RadialGradient(100, 100, 40, 40,
                RadialGradientShape.Circle, stops,
                Weva.Css.Values.CssColorSpace.Srgb,
                Weva.Css.Values.CssHueInterpolationMethod.Shorter,
                isRepeating: true);
            var rasterizer = new SoftwareRasterizer(200, 200);
            rasterizer.Clear(0, 0, 0, 0);
            rasterizer.Submit(new FillRectCommand(
                new Rect(0, 0, 200, 200),
                Brush.Gradient(rad),
                BorderRadii.Zero));

            byte[] px = rasterizer.Pixels;

            // (180, 100): distance = 80, wrapped t = frac(2.0) = 0.0 → red.
            // Pixel center is at (180.5, 100.5); offsets by 0.5 push the
            // sample slightly off-axis — t = sqrt((80.5/40)^2 + (0.5/40)^2)
            // ≈ 2.013, frac → 0.013, still strongly red.
            int o = (100 * 200 + 180) * 4;
            Assert.That(px[o + 0], Is.GreaterThanOrEqualTo((byte)200),
                "Expected red dominant after wrap to t≈0");
            Assert.That(px[o + 2], Is.LessThanOrEqualTo((byte)64),
                "Expected blue near-zero after wrap to t≈0");
            Assert.That(px[o + 3], Is.EqualTo((byte)255));
        }

        // Regression pin: with IsRepeating=false, sampling past the gradient
        // radius must clamp to the last stop (blue) — no wrap to red.
        [Test]
        public void Non_repeating_radial_past_radius_clamps_to_last_stop() {
            var stops = new List<GradientStop> {
                new GradientStop(Red, 0.0),
                new GradientStop(Blue, 1.0),
            };
            var rad = new RadialGradient(100, 100, 40, 40,
                RadialGradientShape.Circle, stops,
                Weva.Css.Values.CssColorSpace.Srgb,
                Weva.Css.Values.CssHueInterpolationMethod.Shorter,
                isRepeating: false);
            var rasterizer = new SoftwareRasterizer(200, 200);
            rasterizer.Clear(0, 0, 0, 0);
            rasterizer.Submit(new FillRectCommand(
                new Rect(0, 0, 200, 200),
                Brush.Gradient(rad),
                BorderRadii.Zero));

            byte[] px = rasterizer.Pixels;
            // (180, 100): distance = 80 = 2 R, clamped to last stop → blue.
            int o = (100 * 200 + 180) * 4;
            Assert.That(px[o + 0], Is.LessThanOrEqualTo((byte)4));
            Assert.That(px[o + 2], Is.GreaterThanOrEqualTo((byte)250));
            Assert.That(px[o + 3], Is.EqualTo((byte)255));
        }
    }
}
