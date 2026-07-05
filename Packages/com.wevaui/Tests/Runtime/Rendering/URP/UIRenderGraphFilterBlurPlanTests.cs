// Gate on WEVA_URP_BATCHER_TESTS — the symbol Weva.Tests.Runtime.asmdef
// defines for URP (its versionDefine). Historically, gating on bare
// `#if WEVA_URP` silently excluded files from the test assembly (the symbol
// was undefined here), which is how 12 rendering test files went dead. The
// asmdef now defines WEVA_URP as well (same URP expression), so either symbol
// works; new files should still prefer WEVA_URP_BATCHER_TESTS.
#if WEVA_URP_BATCHER_TESTS
using System;
using NUnit.Framework;
using Weva.Rendering;
using Weva.Rendering.URP;
using Weva.Testing.Goldens;

namespace Weva.Tests.Rendering.URP {
    // Regression coverage for CSS_COMPLIANCE_ISSUES.md A3b:
    //   CSS Filter Effects 1 §6.1 — `blur(<length>)`'s argument IS the standard
    //   deviation σ of the Gaussian, NOT a radius to be halved. Two production
    //   paths were still half-spec after A3:
    //     1. UIRenderGraphFilterRuntime.ApplyBlur (URP RenderGraph) used
    //        sigma = (radiusPx * 0.5) / factor.
    //     2. SoftwareRasterizer.ApplyFilterChain (golden 3-box approximation)
    //        used r = radiusPx / 3, which yields σ_approx ≈ radius/3.
    //
    // The URP-path tests live here (gated on WEVA_URP_BATCHER_TESTS — see the
    // file-top note); a companion SoftwareRasterizerBlurSigmaTests file covers
    // the golden side without the URP gate so it runs in non-URP test passes
    // too. This file also pins parity between the two paths (URP defined
    // implies both classes are compiled).
    public class UIRenderGraphFilterBlurPlanTests {
        const double Eps = 1e-9;

        [Test]
        public void Urp_blur_plan_resolves_sigma_to_radius_not_half_A3b() {
            // Spec anchor: a 10px CSS blur must encode σ=10 in the shader's
            // _WevaFilterParams (or σ' across n passes that compose back to
            // σ=10 in screen-pixel space). The pre-A3b code halved it to σ=5.
            // Choose a radius small enough that PickBlurDownsampleFactor stays
            // at 1 — this makes the screen-space σ identical to the per-pass σ
            // and lets us pin the exact value without unwinding the cascade.
            var plan = UIRenderGraphFilterRuntime.PlanBlur(5.0);
            Assert.That(plan.DownsampleFactor, Is.EqualTo(1),
                "radius 5 should not trigger downsampling (first cutoff is >5)");
            Assert.That(plan.SpecSigmaInDownsampledSpace, Is.EqualTo(5.0).Within(Eps),
                "spec σ must equal the radius — NOT radius/2");
            // Single-pass: σ=5 sits exactly at the 15-tap budget (3σ = 15).
            Assert.That(plan.Plan.Passes, Is.EqualTo(1));
            Assert.That(plan.Plan.EffectiveSigma, Is.EqualTo(5.0).Within(Eps));
            // The screen-space σ recovered from the cascade must equal radius.
            Assert.That(plan.EffectiveSigmaInScreenSpace, Is.EqualTo(5.0).Within(Eps));
            // Defence in depth: explicitly assert the halved value would fail.
            Assert.That(plan.SpecSigmaInDownsampledSpace, Is.Not.EqualTo(2.5).Within(Eps),
                "regression guard — the pre-A3b code produced σ=2.5 here");
        }

        [Test]
        public void Urp_blur_plan_with_downsample_preserves_screen_space_sigma_A3b() {
            // At radius=40 PickBlurDownsampleFactor returns 8 (the heavyweight
            // tier — picked so σ' = 5 fits the 15-tap kernel in ONE full
            // un-truncated pass). The per-pass σ runs in 1/8-size pixel space
            // (σ_ds = 5), but the recovered screen-space σ must still equal
            // the CSS radius.
            var plan = UIRenderGraphFilterRuntime.PlanBlur(40.0);
            Assert.That(plan.DownsampleFactor, Is.EqualTo(8));
            Assert.That(plan.SpecSigmaInDownsampledSpace, Is.EqualTo(5.0).Within(Eps),
                "σ measured in downsampled-RT pixels = radius / factor = 40 / 8");
            Assert.That(plan.EffectiveSigmaInScreenSpace, Is.EqualTo(40.0).Within(Eps),
                "screen-space σ recovered from cascade must equal CSS radius (A3b spec)");
            // The variance-preserving cascade — n passes of σ'=σ/√n compose to σ.
            double composedVariance = plan.Plan.Passes
                                      * plan.Plan.EffectiveSigma
                                      * plan.Plan.EffectiveSigma;
            double targetVariance = plan.SpecSigmaInDownsampledSpace
                                    * plan.SpecSigmaInDownsampledSpace;
            Assert.That(composedVariance, Is.EqualTo(targetVariance).Within(1e-6));
        }

        [Test]
        public void Urp_blur_kernel_is_never_truncated_below_three_sigma() {
            // Regression (glass.html `.glass { backdrop-filter: blur(26px) }`):
            // the old factor map gave blur(26) → factor 2 → σ'=13, and the
            // 15-tap shader cut that kernel at 15/13 ≈ 1.15σ — the missing
            // Gaussian skirt rendered visibly harder/tighter than Chrome.
            // The planner must now always cover 3σ' with its tap budget.
            foreach (var radius in new[] { 6.0, 11.0, 14.0, 22.0, 26.0, 40.0, 64.0, 90.0 }) {
                var plan = UIRenderGraphFilterRuntime.PlanBlur(radius);
                int needed = (int)Math.Ceiling(plan.Plan.EffectiveSigma * 3.0);
                Assert.That(plan.Plan.TapsPerSide, Is.GreaterThanOrEqualTo(needed),
                    $"blur({radius}): σ'={plan.Plan.EffectiveSigma:F2} needs {needed} taps, " +
                    $"got {plan.Plan.TapsPerSide} — kernel truncated");
                // And the composed screen-space σ still equals the CSS radius.
                double composed = Math.Sqrt(plan.Plan.Passes
                    * plan.Plan.EffectiveSigma * plan.Plan.EffectiveSigma) * plan.DownsampleFactor;
                Assert.That(composed, Is.EqualTo(radius).Within(1e-6),
                    $"blur({radius}): cascade must compose back to the spec σ");
            }
        }

        [Test]
        public void Small_filter_scope_blurs_full_res_no_downsample_ghost() {
            // Regression (glass.html `.art-note { text-shadow: 0 6px 24px ... }`):
            // a text-shadow scope is sized to one glyph run + 3×blur halo
            // (~309×363 px for the 56px album-art note). PickBlurDownsampleFactor(24)
            // returns 8, which crushed the ~29px glyph to ~3.6px in the blur RT;
            // the Gaussian then smeared that nub across the whole RT and the
            // shadow composited back as a shapeless dark BOX instead of a soft
            // glyph-shaped glow. A small scope must blur at full resolution.
            var plan = UIRenderGraphFilterRuntime.PlanBlur(24.0, 309, 363);
            Assert.That(plan.DownsampleFactor, Is.EqualTo(1),
                "a sub-512px scope must blur full-res so the glyph survives");
            // Screen-space σ still equals the CSS radius (full-res ⇒ σ' == radius).
            Assert.That(plan.EffectiveSigmaInScreenSpace, Is.EqualTo(24.0).Within(Eps));
            Assert.That(plan.SpecSigmaInDownsampledSpace, Is.EqualTo(24.0).Within(Eps));
            // The radius-only planner (no scope context) still downsamples — proving
            // it's the scope size, not the radius, that changes the decision.
            Assert.That(UIRenderGraphFilterRuntime.PlanBlur(24.0).DownsampleFactor, Is.EqualTo(8));
        }

        [Test]
        public void Large_filter_scope_keeps_perf_downsample() {
            // The content-aware cap must NOT regress the perf-sensitive case it
            // was built for: match3's `filter: blur(40)` on a full-screen
            // `.bg-aurora`. A ~1080p scope must keep the factor-8 downsample.
            var plan = UIRenderGraphFilterRuntime.PlanBlur(40.0, 1920, 1080);
            Assert.That(plan.DownsampleFactor, Is.EqualTo(8),
                "a full-screen wash must keep the perf-saving downsample");
            Assert.That(plan.EffectiveSigmaInScreenSpace, Is.EqualTo(40.0).Within(Eps));
        }

        [Test]
        public void Effective_blur_factor_threshold_is_per_axis() {
            // Both axes must be within the threshold to qualify as "small".
            // A scope that is short but very wide (e.g. a full-width single-line
            // text-shadow) is NOT small and keeps the radius-based factor.
            Assert.That(UIRenderGraphFilterRuntime.EffectiveBlurFactor(24.0, 400, 400), Is.EqualTo(1));
            Assert.That(UIRenderGraphFilterRuntime.EffectiveBlurFactor(24.0, 2000, 80), Is.EqualTo(8),
                "wide-but-short scope exceeds the threshold on X → not full-res");
            Assert.That(UIRenderGraphFilterRuntime.EffectiveBlurFactor(24.0, 512, 512), Is.EqualTo(1),
                "exactly at the threshold is still 'small'");
            Assert.That(UIRenderGraphFilterRuntime.EffectiveBlurFactor(24.0, 513, 513), Is.EqualTo(8));
        }

        [Test]
        public void Small_backdrop_scope_downsamples_unlike_filter_scope() {
            // Perf regression (glass.html ~788 draw calls): a `.glass` panel is
            // `backdrop-filter: blur(26px) saturate(1.7)` and most panels are
            // sub-512px on both axes (tiles, notes, slider-card, stats). The
            // small-scope full-res exemption is correct for text-shadow/
            // drop-shadow GLYPH scopes (a downsample crushes the glyph into a
            // box) but wrong for a backdrop wash: at factor 1, σ=26 can't fit
            // one shader pass and cascades into ~28 FULL-RES Gaussian passes
            // (56 clear/draw pairs) PER panel. A backdrop blur is a
            // low-frequency wash that's about to be blurred anyway, so it must
            // take the radius-based factor regardless of scope size.
            // Same 300×300 small scope, two paths:
            Assert.That(UIRenderGraphFilterRuntime.EffectiveBlurFactor(26.0, 300, 300, isBackdrop: false),
                Is.EqualTo(1), "filter/text-shadow path keeps the small-scope full-res guard");
            Assert.That(UIRenderGraphFilterRuntime.EffectiveBlurFactor(26.0, 300, 300, isBackdrop: true),
                Is.EqualTo(8), "backdrop path downsamples the small wash (factor follows radius)");

            // And the resulting plan collapses the 28-pass cascade to one pass.
            var filterScopePlan = UIRenderGraphFilterRuntime.PlanBlur(26.0, 300, 300);
            var backdropPlan = UIRenderGraphFilterRuntime.PlanBlur(26.0, 300, 300, isBackdrop: true);
            Assert.That(backdropPlan.DownsampleFactor, Is.EqualTo(8));
            Assert.That(backdropPlan.Plan.Passes, Is.EqualTo(1),
                "blur(26) downsampled 8× gives σ'=3.25 ≤ budget → a single H/V pass");
            Assert.That(filterScopePlan.Plan.Passes, Is.GreaterThan(backdropPlan.Plan.Passes),
                "the full-res filter-scope path still cascades many passes for the same radius");

            // Both paths must still resolve to the SAME visible blur (CSS A3b):
            // screen-space σ equals the CSS radius regardless of downsampling.
            Assert.That(backdropPlan.EffectiveSigmaInScreenSpace, Is.EqualTo(26.0).Within(Eps));
            Assert.That(filterScopePlan.EffectiveSigmaInScreenSpace, Is.EqualTo(26.0).Within(Eps));
        }

        [Test]
        public void Backdrop_blur_factor_ignores_scope_size_but_follows_radius() {
            // The backdrop variant must depend ONLY on the radius (it forwards
            // to PickBlurDownsampleFactor), never on scope size — a tiny glass
            // chip and a full-screen frosted overlay with the same radius get
            // the same factor.
            foreach (var (w, h) in new[] { (64, 64), (300, 300), (512, 512), (1920, 1080) }) {
                Assert.That(UIRenderGraphFilterRuntime.EffectiveBlurFactor(26.0, w, h, isBackdrop: true),
                    Is.EqualTo(UIRenderGraphFilterRuntime.PickBlurDownsampleFactor(26.0)),
                    $"backdrop factor at {w}×{h} must match the radius-only factor");
            }
            // Radius still drives the tier: thin glass (blur 14) → factor 4,
            // heavy glass (blur 26) → factor 8, sub-threshold (blur 4) → 1.
            Assert.That(UIRenderGraphFilterRuntime.EffectiveBlurFactor(14.0, 300, 300, isBackdrop: true), Is.EqualTo(4));
            Assert.That(UIRenderGraphFilterRuntime.EffectiveBlurFactor(26.0, 300, 300, isBackdrop: true), Is.EqualTo(8));
            Assert.That(UIRenderGraphFilterRuntime.EffectiveBlurFactor(4.0, 300, 300, isBackdrop: true), Is.EqualTo(1));
        }

        [Test]
        public void Urp_and_software_blur_paths_agree_on_screen_space_sigma_A3b() {
            // Parity pin: the visible σ from the URP RenderGraph path and the
            // visible σ from the SoftwareRasterizer golden path must match
            // (within the 3-box approximation tolerance) for the same input
            // radius. Pre-A3b this was silently off by 1.5× (GPU produced σ/2,
            // golden produced σ/3 — neither matched the other, neither matched
            // spec). Post-A3b both produce ≈ σ = radius.
            foreach (var radius in new[] { 4.0, 8.0, 16.0 }) {
                var urpPlan = UIRenderGraphFilterRuntime.PlanBlur(radius);
                int boxR = SoftwareRasterizer.ComputeBoxBlurRadiusForSigma(radius);
                double softwareSigma = SoftwareRasterizer.EstimateSigmaForBoxBlurRadius(boxR);

                // The URP σ in screen space is exactly the spec σ (no
                // approximation — separable Gaussian per pass).
                Assert.That(urpPlan.EffectiveSigmaInScreenSpace, Is.EqualTo(radius).Within(Eps),
                    $"URP screen-space σ should equal radius {radius}");

                // The software approximation should agree within 20%
                // (3-box tolerance — Wells 1986 cites <1% for round-trips,
                // 20% leaves room for integer rounding at small radii).
                double diff = Math.Abs(urpPlan.EffectiveSigmaInScreenSpace - softwareSigma);
                Assert.That(diff, Is.LessThanOrEqualTo(radius * 0.20),
                    $"URP σ ({urpPlan.EffectiveSigmaInScreenSpace}) and software σ " +
                    $"({softwareSigma}) should agree within 20% of radius {radius}");
            }
        }
    }
}
#endif
