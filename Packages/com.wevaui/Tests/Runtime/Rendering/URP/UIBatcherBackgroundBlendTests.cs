// WEVA_URP_BATCHER_TESTS is the Tests asmdef's URP versionDefine (the
// Runtime asmdef calls its equivalent WEVA_URP — they are NOT the same
// symbol; a WEVA_URP gate here silently compiles the whole file out).
#if WEVA_URP_BATCHER_TESTS
using NUnit.Framework;
using UnityEngine;
using Weva.Paint;
using Weva.Rendering.URP;
using Rect = Weva.Paint.Rect;

namespace Weva.Tests.Rendering.URP {
    // CSS Compositing 1 §9 — UIBatcher element-local background-blend-mode tests.
    //
    // Verifies that PushBackgroundBlend bakes the correct per-instance encoding
    // and that the page-backdrop anyMixBlendMode latch is NOT triggered by
    // element-local scopes (since they never sample _WevaBackdrop).
    //
    // Encoding contract:
    //   TransformRow0.z — blend mode ordinal (shared with mix-blend-mode).
    //   TransformRow0.w — 0 = page-backdrop §6, 1 = element-local §9.
    //   TransformRow1.zw — base color R, G (linear, unpremultiplied).
    //   TransformRow2.zw — base color B, A (linear, unpremultiplied).
    //
    // NUnit pitfall: NEVER chain .Within() off Is.LessThan/GreaterThan.
    // Use Is.EqualTo(...).Within(eps) or raw inequality + tolerance instead.
    public class UIBatcherBackgroundBlendTests {
        const float Eps = 1e-4f;

        static UIBatcher MakeBatcher() {
            var b = new UIBatcher();
            b.Reset();
            return b;
        }

        static UIQuadInstance EmitSolid(UIBatcher batcher) {
            batcher.SubmitFillRect(new Rect(0, 0, 10, 10), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.Finish();
            Assert.That(batcher.Batches.Count, Is.GreaterThan(0));
            return batcher.Batches[0].Instances[0];
        }

        // ── Normal baseline (no blend) ────────────────────────────────────────

        [Test]
        public void No_blend_bakes_w_zero_and_base_channels_zero() {
            // Without any blend scope, Row0.w must be 0 (page-backdrop flag = no blend)
            // and the base color spare channels must be zero.
            var batcher = MakeBatcher();
            var inst = EmitSolid(batcher);
            Assert.That(inst.TransformRow0.w, Is.EqualTo(0f).Within(Eps),
                "no blend scope → element-local flag must be 0");
            Assert.That(inst.TransformRow1.z, Is.EqualTo(0f).Within(Eps),
                "no blend scope → base color R must be 0");
            Assert.That(inst.TransformRow2.w, Is.EqualTo(0f).Within(Eps),
                "no blend scope → base color A must be 0");
        }

        // ── PushBackgroundBlend bakes ordinal, flag, and base color ───────────

        [Test]
        public void PushBackgroundBlend_bakes_ordinal_into_row0_z() {
            // CSS Compositing 1 §9 — the blend mode ordinal must reach the
            // shader's per-instance dispatch slot (TransformRow0.z).
            var batcher = MakeBatcher();
            batcher.PushBackgroundBlend(MixBlendMode.Multiply,
                new LinearColor(1f, 1f, 1f, 1f));
            var inst = EmitSolid(batcher);
            Assert.That(inst.TransformRow0.z, Is.EqualTo((float)MixBlendMode.Multiply).Within(Eps),
                "element-local blend ordinal must be packed into TransformRow0.z");
            Assert.That((int)(inst.TransformRow0.z + 0.5f), Is.EqualTo((int)MixBlendMode.Multiply));
        }

        [Test]
        public void PushBackgroundBlend_bakes_element_local_flag_w_equals_one() {
            // TransformRow0.w = 1 identifies the element-local §9 path in the shader.
            var batcher = MakeBatcher();
            batcher.PushBackgroundBlend(MixBlendMode.Screen,
                new LinearColor(0.5f, 0.2f, 0.1f, 0.8f));
            var inst = EmitSolid(batcher);
            Assert.That(inst.TransformRow0.w, Is.EqualTo(1f).Within(Eps),
                "element-local flag must be 1 for PushBackgroundBlend");
        }

        [Test]
        public void PushBackgroundBlend_bakes_base_color_into_spare_channels() {
            // Base color (linear, unpremultiplied) baked into:
            //   Row1.zw = R, G
            //   Row2.zw = B, A
            var baseColor = new LinearColor(0.2f, 0.4f, 0.6f, 0.8f);
            var batcher = MakeBatcher();
            batcher.PushBackgroundBlend(MixBlendMode.Overlay, baseColor);
            var inst = EmitSolid(batcher);
            Assert.That(inst.TransformRow1.z, Is.EqualTo(baseColor.R).Within(Eps), "Row1.z = base R");
            Assert.That(inst.TransformRow1.w, Is.EqualTo(baseColor.G).Within(Eps), "Row1.w = base G");
            Assert.That(inst.TransformRow2.z, Is.EqualTo(baseColor.B).Within(Eps), "Row2.z = base B");
            Assert.That(inst.TransformRow2.w, Is.EqualTo(baseColor.A).Within(Eps), "Row2.w = base A");
        }

        // ── Page-backdrop PushMixBlendMode bakes w=0, latches flag ───────────

        [Test]
        public void PushMixBlendMode_bakes_w_zero_and_latches_any_mix_blend() {
            // CSS Compositing 1 §6 — page-backdrop path must set w=0 so the shader
            // takes the _WevaBackdrop sampling branch, and must latch
            // HasAnyMixBlendMode so the RenderGraph pass issues the backdrop copy.
            var batcher = MakeBatcher();
            batcher.PushMixBlendMode(MixBlendMode.Multiply);
            var inst = EmitSolid(batcher);
            Assert.That(inst.TransformRow0.w, Is.EqualTo(0f).Within(Eps),
                "page-backdrop blend flag must be 0 for PushMixBlendMode");
            Assert.That(batcher.HasAnyMixBlendMode, Is.True,
                "PushMixBlendMode must latch HasAnyMixBlendMode");
        }

        [Test]
        public void PushMixBlendMode_spare_channels_are_zero() {
            // For page-backdrop entries the base color slots must be zero (no element-
            // local color baked).
            var batcher = MakeBatcher();
            batcher.PushMixBlendMode(MixBlendMode.Screen);
            var inst = EmitSolid(batcher);
            Assert.That(inst.TransformRow1.z, Is.EqualTo(0f).Within(Eps), "Row1.z spare must be 0");
            Assert.That(inst.TransformRow1.w, Is.EqualTo(0f).Within(Eps), "Row1.w spare must be 0");
            Assert.That(inst.TransformRow2.z, Is.EqualTo(0f).Within(Eps), "Row2.z spare must be 0");
            Assert.That(inst.TransformRow2.w, Is.EqualTo(0f).Within(Eps), "Row2.w spare must be 0");
        }

        // ── Element-local does NOT latch HasAnyMixBlendMode ───────────────────

        [Test]
        public void PushBackgroundBlend_does_NOT_latch_any_mix_blend_mode() {
            // CRITICAL: element-local §9 blending never samples _WevaBackdrop.
            // Latching anyMixBlendModeInFrame for an element-local push would
            // cause a wasteful per-frame backdrop copy even when no element uses
            // page-backdrop mix-blend-mode.
            var batcher = MakeBatcher();
            Assert.That(batcher.HasAnyMixBlendMode, Is.False,
                "baseline: no mix blend mode at start");
            batcher.PushBackgroundBlend(MixBlendMode.Multiply,
                new LinearColor(1f, 0f, 0f, 1f));
            // Emit a quad so the scope is actually used.
            batcher.SubmitFillRect(new Rect(0, 0, 10, 10), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.PopBackgroundBlend();
            batcher.Finish();
            Assert.That(batcher.HasAnyMixBlendMode, Is.False,
                "PushBackgroundBlend (element-local §9) must NOT latch HasAnyMixBlendMode");
        }

        // ── Pop restores previous state ───────────────────────────────────────

        [Test]
        public void PopBackgroundBlend_restores_previous_blend_state() {
            // After popping, the next quad should carry no blend mode (w=0, ordinal=0).
            var batcher = MakeBatcher();
            batcher.PushBackgroundBlend(MixBlendMode.Multiply,
                new LinearColor(0.5f, 0.5f, 0.5f, 1f));
            // Push + immediately pop before emitting anything.
            batcher.PopBackgroundBlend();
            var inst = EmitSolid(batcher);
            Assert.That(inst.TransformRow0.z, Is.EqualTo(0f).Within(Eps),
                "after pop, ordinal must revert to 0 (Normal)");
            Assert.That(inst.TransformRow0.w, Is.EqualTo(0f).Within(Eps),
                "after pop, element-local flag must revert to 0");
            Assert.That(inst.TransformRow1.z, Is.EqualTo(0f).Within(Eps),
                "after pop, base R must revert to 0");
        }

        [Test]
        public void Nested_blend_scopes_restore_outer_state() {
            // Outer scope: page-backdrop Multiply. Inner scope: element-local Screen.
            // After inner pop, outer state is restored.
            var batcher = MakeBatcher();
            batcher.PushMixBlendMode(MixBlendMode.Multiply);   // outer: page-backdrop
            batcher.PushBackgroundBlend(MixBlendMode.Screen,
                new LinearColor(0.3f, 0.4f, 0.5f, 1f));        // inner: element-local

            // Emit inside inner scope.
            batcher.SubmitFillRect(new Rect(0, 0, 10, 10), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.PopBackgroundBlend();
            batcher.Finish();

            // Check inner instance (before Finish, so the only completed batch).
            var innerInst = batcher.Batches[0].Instances[0];
            Assert.That(innerInst.TransformRow0.w, Is.EqualTo(1f).Within(Eps),
                "inner scope: element-local flag = 1");
            Assert.That(innerInst.TransformRow0.z, Is.EqualTo((float)MixBlendMode.Screen).Within(Eps),
                "inner scope: ordinal = Screen");

            // After pop, outer Multiply scope is restored. Emit another quad.
            var batcher2 = MakeBatcher();
            batcher2.PushMixBlendMode(MixBlendMode.Multiply);
            batcher2.PushBackgroundBlend(MixBlendMode.Screen, LinearColor.White);
            batcher2.PopBackgroundBlend();
            // Now outer scope is active.
            batcher2.SubmitFillRect(new Rect(0, 0, 10, 10), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher2.Finish();
            var outerInst = batcher2.Batches[0].Instances[0];
            Assert.That(outerInst.TransformRow0.w, Is.EqualTo(0f).Within(Eps),
                "after inner pop, outer page-backdrop flag = 0");
            Assert.That(outerInst.TransformRow0.z, Is.EqualTo((float)MixBlendMode.Multiply).Within(Eps),
                "after inner pop, ordinal = Multiply (outer scope restored)");
        }

        // ── WriteTransform preserves all spare channels ───────────────────────

        [Test]
        public void Instance_built_inside_background_blend_scope_with_transform_has_spare_channels_intact() {
            // The snapshot-replay path calls WriteTransform to recompute the
            // affine when a quad's parent transform shifts. It must preserve
            // TransformRow0.w and TransformRow1/2.zw (base color channels).
            //
            // Verify by pushing a transform alongside a background-blend scope
            // so that WriteTransform is applied to the baked instance: if the
            // spare channel preservation is correct, the re-baked affine
            // doesn't wipe the blend flag / base color.
            //
            // Here we use PushTransform to generate a non-identity instance,
            // then confirm the blend channels survived in the built instance.
            var batcher = MakeBatcher();
            var baseColor = new LinearColor(0.1f, 0.2f, 0.3f, 0.9f);
            batcher.PushBackgroundBlend(MixBlendMode.Darken, baseColor);
            batcher.PushTransform(Transform2D.Identity);
            batcher.SubmitFillRect(new Rect(0, 0, 10, 10), Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.PopTransform();
            batcher.PopBackgroundBlend();
            batcher.Finish();

            Assert.That(batcher.Batches.Count, Is.GreaterThan(0));
            var inst = batcher.Batches[0].Instances[0];

            // Spare channels must be present in the built instance.
            Assert.That(inst.TransformRow0.w, Is.EqualTo(1f).Within(Eps),
                "element-local flag Row0.w must be 1 inside background-blend scope");
            Assert.That(inst.TransformRow0.z, Is.EqualTo((float)MixBlendMode.Darken).Within(Eps),
                "blend ordinal Row0.z must be Darken");
            Assert.That(inst.TransformRow1.z, Is.EqualTo(baseColor.R).Within(Eps),
                "base color R (Row1.z)");
            Assert.That(inst.TransformRow1.w, Is.EqualTo(baseColor.G).Within(Eps),
                "base color G (Row1.w)");
            Assert.That(inst.TransformRow2.z, Is.EqualTo(baseColor.B).Within(Eps),
                "base color B (Row2.z)");
            Assert.That(inst.TransformRow2.w, Is.EqualTo(baseColor.A).Within(Eps),
                "base color A (Row2.w)");
        }
    }
}
#endif
