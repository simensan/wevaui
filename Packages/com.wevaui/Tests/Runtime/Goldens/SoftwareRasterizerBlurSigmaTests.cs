using System;
using NUnit.Framework;
using Weva.Testing.Goldens;

namespace Weva.Tests.Goldens {
    // Regression coverage for CSS_COMPLIANCE_ISSUES.md A3b (golden side):
    //   The SoftwareRasterizer's filter-chain blur is a 3-pass box-blur
    //   approximation of the CSS Filter Effects 1 §6.1 Gaussian. Previously
    //   ApplyFilterChain used `r = radiusPx / 3`, which produced a composed
    //   approximate σ ≈ radius/3 — a 3× understrength blur relative to the
    //   spec (which says σ = radiusPx). The new helper uses Wells (1986)'s
    //   per-pass r ≈ σ · √3 / 2 mapping.
    //
    // We assert on the helper rather than diffing rendered pixels: that keeps
    // the test cheap and pins the spec-relevant invariant (σ_approx ≈ σ within
    // the 20% tolerance acceptable for a 3-box approximation). The URP-side
    // parity test lives in UIRenderGraphFilterBlurPlanTests so it's only
    // compiled when URP is present.
    public class SoftwareRasterizerBlurSigmaTests {
        [Test]
        public void Box_blur_three_pass_approximates_spec_sigma_within_tolerance_A3b() {
            // Wells 1986: n=3 boxes of half-width r ≈ σ · √3 / 2 compose to a
            // Gaussian of std-dev σ. At σ=5 that's r = round(5 · 0.866) = 4,
            // and σ_approx = √(((2·4+1)² − 1) / 4) = √(80/4) = √20 ≈ 4.47.
            // The pre-A3b mapping σ/3 gave r=2 → σ_approx = √(24/4) ≈ 2.45.
            foreach (var sigma in new[] { 3.0, 5.0, 10.0, 20.0, 30.0 }) {
                int r = SoftwareRasterizer.ComputeBoxBlurRadiusForSigma(sigma);
                double approxSigma = SoftwareRasterizer.EstimateSigmaForBoxBlurRadius(r);

                Assert.That(approxSigma, Is.EqualTo(sigma).Within(sigma * 0.20),
                    $"3-box approximation of σ={sigma} should land within 20% of spec — " +
                    $"r={r}, σ_approx={approxSigma:F3}. NOT radius/3 (would give σ_approx ≈ σ/3).");

                // Belt-and-braces: assert the result is NOT what the pre-A3b
                // σ/3 mapping would have produced. With σ=30 the old r was 10
                // (σ_approx ≈ 11.0) — a clearly different bucket from r ≈ 26
                // (σ_approx ≈ 30.3) that the new mapping produces.
                int oldR = (int)Math.Round(sigma / 3.0);
                if (oldR < 1) oldR = 1;
                double oldApproxSigma = SoftwareRasterizer.EstimateSigmaForBoxBlurRadius(oldR);
                Assert.That(approxSigma, Is.GreaterThan(oldApproxSigma * 1.5),
                    $"new mapping should produce visibly stronger blur than the old σ/3 rule " +
                    $"(new σ_approx={approxSigma:F2}, old σ_approx={oldApproxSigma:F2})");
            }
        }

        [Test]
        public void Box_blur_radius_clamps_to_one_for_subpixel_sigma_A3b() {
            // σ < ~0.6 px rounds to 0 under σ·√3/2; clamp to 1 so blur() with
            // any positive radius still applies one pass (matches the GPU
            // path, which always emits one shader pass). σ = 0 stays 0 — no
            // pass at all.
            Assert.That(SoftwareRasterizer.ComputeBoxBlurRadiusForSigma(0.0), Is.EqualTo(0),
                "zero σ should produce zero radius (no-op)");
            Assert.That(SoftwareRasterizer.ComputeBoxBlurRadiusForSigma(0.5), Is.EqualTo(1),
                "sub-pixel σ should clamp to a 1-pixel box (one pass still visible)");
            Assert.That(SoftwareRasterizer.ComputeBoxBlurRadiusForSigma(1.0), Is.EqualTo(1),
                "σ=1 maps to r=round(0.866) = 1");
        }

        [Test]
        public void Box_blur_radius_no_longer_uses_radius_over_three_A3b() {
            // The bug: r = radiusPx / 3. Pin that we have moved off that
            // mapping for every σ ≥ 3 where it diverges from the new rule.
            // (At σ=2 both rules round to r=1, so we start at σ=3.)
            foreach (var sigma in new[] { 3.0, 5.0, 10.0, 20.0, 40.0 }) {
                int r = SoftwareRasterizer.ComputeBoxBlurRadiusForSigma(sigma);
                int oldR = (int)Math.Round(sigma / 3.0);
                if (oldR < 1) oldR = 1;
                Assert.That(r, Is.Not.EqualTo(oldR),
                    $"σ={sigma}: new r ({r}) must differ from the buggy radius/3 mapping ({oldR})");
                // The new r is the Wells formula rounded.
                int expected = (int)Math.Round(sigma * Math.Sqrt(3.0) / 2.0);
                Assert.That(r, Is.EqualTo(expected),
                    $"σ={sigma}: r should follow Wells 1986 (σ·√3/2 = {expected})");
            }
        }
    }
}
