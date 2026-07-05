#if WEVA_URP
using System;
using NUnit.Framework;
using Weva.Rendering;

namespace Weva.Tests.Rendering.Backend {
    // Regression coverage for CSS_COMPLIANCE_ISSUES.md A3:
    //   CSS Filter Effects 1 §6.1 — the <length> argument to `blur(<length>)` IS
    //   the standard deviation σ of the Gaussian, not a radius/diameter. The
    //   FilterPipeline implementation previously computed `sigma = radiusPx * 0.5`,
    //   producing a half-strength blur relative to the browser reference.
    //
    // These tests pin the planner used by FilterPipeline.ApplyBlur. Direct
    // exercise of ApplyBlur requires a live URP CommandBuffer; PlanGaussianBlur
    // captures the spec-relevant decision (σ, cascade-pass count, tap count) in
    // a pure function that is safe to assert on without a render context.
    public class FilterPipelineBlurPlanTests {
        const double Eps = 1e-9;

        [Test]
        public void Blur_zero_radius_plan_sigma_is_zero_and_ApplyBlur_no_ops() {
            // ApplyBlur's contract: radiusPx <= 0 returns srcRt unchanged. The
            // planner is consistent — σ collapses to 0 so even if the early-out
            // were removed, the encoded kernel would be a 1-tap identity. This
            // pins the "blur(0) is a no-op" guarantee at both layers.
            var plan = FilterPipeline.PlanGaussianBlur(0.0);
            Assert.That(plan.Sigma, Is.EqualTo(0.0).Within(Eps));
            Assert.That(plan.EffectiveSigma, Is.EqualTo(0.0).Within(Eps));
            Assert.That(plan.Passes, Is.EqualTo(1));
            // TapsPerSide is clamped to a minimum of 1, so the kernel becomes a
            // 3-tap [w_-1, w_0, w_+1] sample with σ=0. The shader's Gaussian
            // weights collapse to (0, 1, 0), producing an identity copy.
            Assert.That(plan.TapsPerSide, Is.EqualTo(1));
        }

        [Test]
        public void Blur_length_equals_sigma_not_half_per_spec() {
            // Spec anchor: CSS Filter Effects 1 §6.1 states the blur() argument
            // is σ directly. A 5px blur must encode σ=5 into the shader, NOT
            // σ=2.5 (the pre-fix behaviour halved the radius).
            foreach (var radius in new[] { 1.0, 2.5, 5.0, 7.0, 10.0, 16.0 }) {
                var plan = FilterPipeline.PlanGaussianBlur(radius);
                Assert.That(plan.Sigma, Is.EqualTo(radius).Within(Eps),
                    $"blur({radius}px) should encode σ={radius}, got σ={plan.Sigma}");
                // Radii whose 3σ kernel fits the 15-tap budget run one pass
                // with σ' = σ exactly. Larger radii now CASCADE rather than
                // truncate the kernel (truncation at <3σ cut the Gaussian's
                // soft skirt — the old single-pass-up-to-16 behaviour rendered
                // visibly harder blurs than Chrome).
                if (radius <= FilterPipeline.MaxSigmaPerTapBudget) {
                    Assert.That(plan.Passes, Is.EqualTo(1),
                        $"blur({radius}px) should fit one pass (≤ tap-budget σ = {FilterPipeline.MaxSigmaPerTapBudget})");
                    Assert.That(plan.EffectiveSigma, Is.EqualTo(radius).Within(Eps));
                } else {
                    Assert.That(plan.Passes, Is.GreaterThan(1),
                        $"blur({radius}px) must cascade — σ'={plan.EffectiveSigma} would truncate at " +
                        $"{plan.TapsPerSide / plan.EffectiveSigma:F2}σ in one pass");
                    Assert.That(plan.EffectiveSigma,
                        Is.LessThanOrEqualTo(FilterPipeline.MaxSigmaPerTapBudget + Eps));
                }
                // Invariant either way: the per-pass kernel covers the full 3σ'
                // body (σ' ≤ 5 ⇒ ceil(3σ') ≤ 15 — never truncated).
                Assert.That(plan.TapsPerSide,
                    Is.GreaterThanOrEqualTo((int)Math.Ceiling(plan.EffectiveSigma * 3.0)),
                    $"blur({radius}px) kernel must cover 3σ'");
            }
        }

        [Test]
        public void Blur_kernel_tap_count_matches_three_sigma_falloff() {
            // The shader truncates the Gaussian at ±3σ (kernel half-width = N).
            // Canonical sample: at radius=5 the planner reports N = ceil(3·5) = 15
            // (the shader's hard-coded max, also reached exactly at this radius).
            // The Gaussian falloff at x=N relative to peak should be ≈ exp(-9/2)
            // ≈ 0.0111 — well below 5%, confirming the kernel captures the
            // perceptually-relevant body of the Gaussian.
            var plan = FilterPipeline.PlanGaussianBlur(5.0);
            Assert.That(plan.Sigma, Is.EqualTo(5.0).Within(Eps));
            Assert.That(plan.Passes, Is.EqualTo(1));
            // ceil(5 * 3) = 15, exactly at the 15-tap cap.
            Assert.That(plan.TapsPerSide, Is.EqualTo(15));

            // Verify the falloff at the kernel edge (x = N taps from center).
            // g(x; σ) / g(0; σ) = exp(-x² / (2σ²)). At x=15, σ=5:
            //   exp(-225 / 50) = exp(-4.5) ≈ 0.0111.
            double x = plan.TapsPerSide;
            double sigma = plan.EffectiveSigma;
            double relativeWeight = Math.Exp(-(x * x) / (2.0 * sigma * sigma));
            Assert.That(relativeWeight, Is.EqualTo(Math.Exp(-4.5)).Within(1e-12));
            Assert.That(relativeWeight, Is.LessThan(0.02),
                "Kernel edge weight should be <2% of peak — confirms ±3σ truncation captures the Gaussian body.");
        }

        [Test]
        public void Blur_large_radius_cascades_with_variance_preserving_sigma() {
            // CSS spec says σ = radiusPx. The implementation caps per-pass σ at
            // MaxSigmaPerPass so the shader's [loop] unroll stays bounded; a
            // larger σ is split into n cascaded passes of σ' = σ/√n. n independent
            // Gaussians of σ' compose into one Gaussian of σ (variances add:
            //   n · (σ')² = n · σ²/n = σ²
            // ).
            var plan = FilterPipeline.PlanGaussianBlur(64.0);
            // Spec σ is preserved on the plan even when cascaded.
            Assert.That(plan.Sigma, Is.EqualTo(64.0).Within(Eps));
            // Per-pass σ' must respect the cap.
            Assert.That(plan.EffectiveSigma, Is.LessThanOrEqualTo((double)FilterPipeline.MaxSigmaPerPass + Eps));
            Assert.That(plan.Passes, Is.GreaterThan(1));

            // Variance equivalence: passes · (effectiveSigma)² ≈ sigma².
            double composedVariance = plan.Passes * plan.EffectiveSigma * plan.EffectiveSigma;
            double targetVariance = plan.Sigma * plan.Sigma;
            Assert.That(composedVariance, Is.EqualTo(targetVariance).Within(1e-6),
                $"Cascade of {plan.Passes} × σ'={plan.EffectiveSigma} should compose to σ={plan.Sigma} " +
                $"(variance {composedVariance} vs {targetVariance}).");
        }
    }
}
#endif
