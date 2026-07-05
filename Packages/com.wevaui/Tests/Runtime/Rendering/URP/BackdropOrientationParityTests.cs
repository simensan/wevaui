// Gate matches the test assembly's URP versionDefine (see
// UIRenderGraphFilterBlurPlanTests for the full rationale: bare WEVA_URP is
// undefined in Weva.Tests.Runtime and silently drops the file).
#if WEVA_URP_BATCHER_TESTS
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Paint;
using Weva.Paint.Filters;
using Weva.Rendering.URP;
using Rect = Weva.Paint.Rect;

namespace Weva.Tests.Rendering.URP {
    // Orientation ledger F1 (2026-07): the backdrop blur's single-step
    // downsample blit contributes one internal Y-inversion, and it exists
    // only when the radius-based factor exceeds 1 (radius > 5px). Before it
    // was counted, the correct composite flip was RADIUS-DEPENDENT — the
    // shared-backdrop path sampled the Y-mirrored screen position at
    // blur(22px) hover while blur(4px) rest was correct, which is why both
    // fixed signs of entry.Flipped failed (each fixed one radius regime).
    // These tests pin the factor threshold and the toggle decision so the
    // parity accounting can't silently drift off the 5px cliff again.
    public class BackdropOrientationParityTests {
        static bool TogglesAt(double radiusPx) =>
            UIRenderGraphFilterRuntime.BackdropDownsampleTogglesFlip(
                UIRenderGraphFilterRuntime.EffectiveBlurFactor(radiusPx, 1920, 1080, isBackdrop: true));

        [Test]
        public void Rest_blur_4px_does_not_toggle_the_chain_parity() {
            Assert.That(TogglesAt(4), Is.False);
        }

        [Test]
        public void Factor_threshold_sits_at_5px() {
            Assert.That(TogglesAt(5), Is.False, "radius 5 is factor 1 (threshold is exclusive)");
            Assert.That(TogglesAt(5.1), Is.True, "just past the threshold downsampling starts");
        }

        [Test]
        public void Validation_page_radii_flank_the_threshold() {
            // audit-validation.html §11: blur(4) rest / blur(8) mid / blur(22) hover.
            Assert.That(TogglesAt(4), Is.False);
            Assert.That(TogglesAt(8), Is.True);
            Assert.That(TogglesAt(22), Is.True);
        }

        [Test]
        public void All_downsample_factors_toggle_exactly_once() {
            // The isBackdrop branch is a SINGLE blit regardless of factor
            // (2, 4 or 8) — parity toggles once, not per halving step.
            Assert.That(UIRenderGraphFilterRuntime.BackdropDownsampleTogglesFlip(2), Is.True);
            Assert.That(UIRenderGraphFilterRuntime.BackdropDownsampleTogglesFlip(4), Is.True);
            Assert.That(UIRenderGraphFilterRuntime.BackdropDownsampleTogglesFlip(8), Is.True);
            Assert.That(UIRenderGraphFilterRuntime.BackdropDownsampleTogglesFlip(1), Is.False);
        }
    }

    // Rendering audit Gap1/F5: shareability must test the candidate's
    // filters-INFLATED rect (border box + 3σ blur halo — the region the
    // scope READS), not just its border box. An earlier panel's composite
    // landing inside the halo feeds wrong (pristine-capture) values into
    // the crop's edge pixels.
    public class BackdropShareabilityHaloTests {
        static BackdropFilterEvent Ev(double x, double y, double w, double h, FilterChain filters, int batchIndex = 0) =>
            new BackdropFilterEvent(new Rect(x, y, w, h), BorderRadii.Zero,
                filters, Transform2D.Identity, batchIndex);

        static readonly FilterChain Blur22 =
            new FilterChain(new FilterFunction[] { new BlurFilter(22) });

        [Test]
        public void Halo_overlap_with_an_earlier_panel_demotes() {
            // Border boxes are 60px apart (200..260) — disjoint. blur(22)
            // inflates the candidate by 3×22 = 66px, so its READ region
            // reaches x=194 and overlaps the earlier panel's output.
            var shareable = new List<bool>();
            UIRenderGraphPass.ComputeBackdropShareability(new[] {
                Ev(0, 0, 200, 100, Blur22),
                Ev(260, 0, 200, 100, Blur22),
            }, 1000, 800, shareable);
            Assert.That(shareable[0], Is.True, "first panel reads the pristine scene");
            Assert.That(shareable[1], Is.False, "halo reaches the earlier panel's composite");
        }

        [Test]
        public void Beyond_the_halo_stays_shareable() {
            // 3σ = 66px; 80px of separation keeps the read regions disjoint.
            var shareable = new List<bool>();
            UIRenderGraphPass.ComputeBackdropShareability(new[] {
                Ev(0, 0, 200, 100, Blur22),
                Ev(280, 0, 200, 100, Blur22),
            }, 1000, 800, shareable);
            Assert.That(shareable, Is.EqualTo(new[] { true, true }));
        }

        [Test]
        public void Post_capture_batch_inside_the_halo_demotes() {
            // Capture happens at event 0 (batch index 0). A batch drawn
            // between the capture and event 1 sits OUTSIDE event 1's border
            // box but INSIDE its blur halo (border box starts at x=300;
            // halo reaches 300-66=234; batch spans 240..260).
            var batches = new List<UIQuadBatch> {
                new UIQuadBatch(default, System.Array.Empty<UIQuadInstance>(), 0, 0,
                    0, 0, 0, 0, needsBackdropRefresh: false, maskImageTexture: null,
                    pixelBounds: new UnityEngine.Vector4(0, 0, 10, 10)),      // scanned but disjoint from event 1's read region
                new UIQuadBatch(default, System.Array.Empty<UIQuadInstance>(), 0, 0,
                    0, 0, 0, 0, needsBackdropRefresh: false, maskImageTexture: null,
                    pixelBounds: new UnityEngine.Vector4(240, 0, 260, 100)),  // in the halo
            };
            var shareable = new List<bool>();
            UIRenderGraphPass.ComputeBackdropShareability(new[] {
                Ev(0, 0, 100, 100, Blur22, batchIndex: 0),
                Ev(300, 0, 200, 100, Blur22, batchIndex: 2),
            }, batches, 1000, 800, shareable);
            Assert.That(shareable[0], Is.True);
            Assert.That(shareable[1], Is.False, "post-capture content in the halo poisons the crop edge");
        }
    }
}
#endif
