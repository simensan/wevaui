using System;
using System.Globalization;
using NUnit.Framework;
using Weva.Css.Animation;
using Weva.Css.Values;

namespace Weva.Tests.Css.Animation {
    // G9 — Transform-list interpolation: mismatched shapes route through 2D
    // matrix decomposition (CSS Transforms L1 §17). Matching shapes keep the
    // existing per-component fast path. The pure-discrete fallback survives
    // for `none ↔ matrix(...)`, which has no per-function identity.
    public class TransformMatrixDecompositionTests {
        static LengthContext Ctx() => LengthContext.Default;

        // Parses a "matrix(a, b, c, d, e, f)" string emitted by the
        // interpolator back into its six numeric components.
        static void ParseMatrixOut(string text, out double a, out double b,
                                                out double c, out double d,
                                                out double e, out double f) {
            Assert.That(text, Does.StartWith("matrix("));
            int open = text.IndexOf('(');
            int close = text.IndexOf(')');
            string body = text.Substring(open + 1, close - open - 1);
            string[] parts = body.Split(',');
            Assert.That(parts.Length, Is.EqualTo(6), "matrix() must serialize as 6 args: " + text);
            a = double.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
            b = double.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
            c = double.Parse(parts[2].Trim(), CultureInfo.InvariantCulture);
            d = double.Parse(parts[3].Trim(), CultureInfo.InvariantCulture);
            e = double.Parse(parts[4].Trim(), CultureInfo.InvariantCulture);
            f = double.Parse(parts[5].Trim(), CultureInfo.InvariantCulture);
        }

        // --- Fast path regression pin: matching shapes still use per-component interp ---

        [Test]
        public void Matching_shape_translate_lerps_per_component_at_t_half_G9_fast_path() {
            // Regression pin for the existing per-component (non-matrix) path.
            // `translate(10px) → translate(20px)` matches shape (1 fn, 1 arg
            // each), so the fast path emits a literal `translate(...)` token —
            // NOT a matrix. If this ever flips to `matrix(...)` the fast path
            // has been lost (every animated transform on the hot tick would
            // then collapse → reformat as matrix() and the gem-spin perf
            // story regresses).
            var v = ValueInterpolator.Interpolate("translate(10px)", "translate(20px)", 0.5,
                PropertyKind.Transform, Ctx());
            Assert.That(v, Does.StartWith("translate("));
            Assert.That(v, Does.Contain("15"));
            Assert.That(v, Does.Not.StartWith("matrix("));
        }

        // --- Mismatched-shape: 2D matrix decomposition (G9 main case) ---

        [Test]
        public void Mismatched_translate_to_rotate_decomposes_and_interpolates_G9() {
            // `translate(10px, 0) → rotate(0deg)`: both endpoints collapse to
            // 2D matrices. At t=0.5 the result should be near-identity but
            // with the translate halved (5px) and rotation still 0. This is
            // the canonical CSS Transforms L1 §17 case the audit flagged —
            // pre-G9 this returned the discrete `translate(10px, 0)` (t<0.5)
            // or `rotate(0deg)` (t>=0.5).
            var v = ValueInterpolator.Interpolate("translate(10px, 0)", "rotate(0deg)", 0.5,
                PropertyKind.Transform, Ctx());
            ParseMatrixOut(v, out double a, out double b, out double c, out double d,
                                 out double e, out double f);
            // rotate(0deg) is the identity matrix, translate(10px,0) is
            // [[1,0,10],[0,1,0]]. Decomposed components:
            //   tx: 10→0, scaleX: 1→1, scaleY: 1→1, rot: 0→0
            // Linear lerp at t=0.5:
            //   tx = 5, ty = 0, scaleX = 1, scaleY = 1, rotation = 0
            // Recompose: [[1,0,5],[0,1,0]].
            Assert.That(a, Is.EqualTo(1).Within(1e-6));
            Assert.That(b, Is.EqualTo(0).Within(1e-6));
            Assert.That(c, Is.EqualTo(0).Within(1e-6));
            Assert.That(d, Is.EqualTo(1).Within(1e-6));
            Assert.That(e, Is.EqualTo(5).Within(1e-6));
            Assert.That(f, Is.EqualTo(0).Within(1e-6));
        }

        [Test]
        public void Mismatched_scale_to_rotate_midpoint_has_scale_1_5_and_rotation_45deg_G9() {
            // `scale(2) → rotate(90deg)` at t=0.5: scale(2) decomposes to
            // (tx=0, ty=0, sx=2, sy=2, rot=0), rotate(90deg) to
            // (tx=0, ty=0, sx=1, sy=1, rot=π/2). Midpoint components:
            //   scale = 1.5, rotation = π/4 (45°).
            // Recomposing translate=0 * rotate(π/4) * scale(1.5):
            //   cos(π/4)*1.5 ≈ 1.06066, -sin(π/4)*1.5 ≈ -1.06066,
            //   sin(π/4)*1.5 ≈ 1.06066,  cos(π/4)*1.5 ≈ 1.06066,
            //   tx=0, ty=0.
            // The decomposition contract is what we assert (Chrome would
            // produce the same numbers).
            var v = ValueInterpolator.Interpolate("scale(2)", "rotate(90deg)", 0.5,
                PropertyKind.Transform, Ctx());
            ParseMatrixOut(v, out double a, out double b, out double c, out double d,
                                 out double e, out double f);
            double expected = 1.5 * Math.Cos(Math.PI / 4);
            // The matrix should be a pure rotation+uniform-scale (no translate,
            // no reflection). Read scale magnitudes from columns.
            double scaleXReadback = Math.Sqrt(a * a + c * c);
            double scaleYReadback = Math.Sqrt(b * b + d * d);
            double rotationReadback = Math.Atan2(c, a); // rad
            Assert.That(scaleXReadback, Is.EqualTo(1.5).Within(1e-4),
                "G9 midpoint scaleX should be 1.5 (halfway between 2 and 1)");
            Assert.That(scaleYReadback, Is.EqualTo(1.5).Within(1e-4),
                "G9 midpoint scaleY should be 1.5");
            Assert.That(rotationReadback * 180.0 / Math.PI, Is.EqualTo(45).Within(1e-4),
                "G9 midpoint rotation should be 45 degrees (halfway between 0 and 90)");
            Assert.That(e, Is.EqualTo(0).Within(1e-6));
            Assert.That(f, Is.EqualTo(0).Within(1e-6));
            // Sanity check against the closed-form a value to make sure
            // the recompose order is translate × rotate × scale rather than
            // rotate × scale (which would swap b/c signs).
            Assert.That(a, Is.EqualTo(expected).Within(1e-4));
        }

        // --- Discrete fallback for `none ↔ matrix(...)` (CSS L1 §17 special case) ---

        [Test]
        public void None_to_matrix_steps_discretely_G9() {
            // `none ↔ matrix(...)` has no per-component identity to lerp
            // against (matrix() args have no neutral-value identity in the
            // way translate=0, scale=1, rotate=0deg do), so the spec's §17
            // matrix-decomposition with `none` as one endpoint degenerates.
            // We deliberately keep the discrete-step fallback for this case
            // — see the comment guard in InterpolateTransform around
            // ContainsMatrixFn. If a future change reintroduces an identity
            // matrix for `none` this assertion will need to be re-evaluated.
            var midLow = ValueInterpolator.Interpolate("none", "matrix(2, 0, 0, 2, 10, 20)", 0.4,
                PropertyKind.Transform, Ctx());
            var midHigh = ValueInterpolator.Interpolate("none", "matrix(2, 0, 0, 2, 10, 20)", 0.6,
                PropertyKind.Transform, Ctx());
            Assert.That(midLow, Is.EqualTo("none"));
            Assert.That(midHigh, Is.EqualTo("matrix(2, 0, 0, 2, 10, 20)"));
        }
    }
}
