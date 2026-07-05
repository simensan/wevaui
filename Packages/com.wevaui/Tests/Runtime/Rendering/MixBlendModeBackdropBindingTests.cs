#if WEVA_URP
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Rendering.URP;

namespace Weva.Tests.Rendering {
    // CSS Compositing 1 §10 (mix-blend-mode) — B3b-visual coverage. The URP
    // RenderGraph pass binds `_WevaBackdrop` / `_WevaBackdropAvailable`
    // only when the current document contains at least one non-Normal
    // mix-blend-mode scope; otherwise it skips the per-frame full-screen
    // blit entirely. These tests pin the value-tracking contract:
    //
    //   * UIBatcher latches HasAnyMixBlendMode when a non-Normal Push lands.
    //   * Documents without mix-blend-mode keep the latch false so the
    //     URP pass can short-circuit the backdrop copy.
    //   * The shader-binding ids (Shader.PropertyToID) on UIRenderGraphPass
    //     match the names the shader actually reads, so the per-frame
    //     SetGlobalTexture / SetGlobalFloat lands on the right slots.
    //
    // GPU-side visual validation against Chrome is a separate manual step.
    public class MixBlendModeBackdropBindingTests {
        // Convert-then-submit roundtrip helper. Builds a single BlockBox
        // with the supplied style, converts to PaintCommands, replays them
        // into a fresh BatchedURPRenderBackend so the batcher state can be
        // inspected for the per-frame flag latch.
        static BatchedURPRenderBackend ConvertAndSubmit(ComputedStyle style) {
            var bb = new BlockBox { Style = style, X = 0, Y = 0, Width = 64, Height = 32 };
            var converter = new BoxToPaintConverter();
            var paintList = converter.Convert(bb);
            var backend = new BatchedURPRenderBackend();
            backend.BeginFrame();
            foreach (var cmd in paintList.Commands) cmd.Submit(backend);
            backend.EndFrame();
            return backend;
        }

        static ComputedStyle MakeStyle(string blendMode) {
            var s = new ComputedStyle(new Element("div"));
            // Background-color is required: without a paintable decoration
            // the converter emits no FillRect and no Push wraps it, so the
            // mix-blend-mode never reaches the batcher.
            s.Set("background-color", "rgb(255, 0, 0)");
            if (blendMode != null) s.Set("mix-blend-mode", blendMode);
            return s;
        }

        [Test]
        public void Any_mix_blend_mode_flag_flips_when_multiply_is_present_B3b_visual() {
            // Authoring `<div style="mix-blend-mode: multiply">` must drive
            // the document's per-frame any-mix-blend flag to true so the
            // URP pass knows to perform the camera-color-target copy that
            // feeds `_WevaBackdrop`.
            var backend = ConvertAndSubmit(MakeStyle("multiply"));
            Assert.That(backend.Batcher.HasAnyMixBlendMode, Is.True,
                "A `mix-blend-mode: multiply` document must latch HasAnyMixBlendMode " +
                "so the URP RenderGraph pass copies the active color target.");
        }

        [Test]
        public void Any_mix_blend_mode_flag_stays_false_when_no_element_uses_mix_blend_B3b_visual() {
            // Documents that never touch mix-blend-mode must keep the latch
            // false. The URP pass reads this to skip the per-frame full-
            // screen blit — that's the performance gate the tracker calls
            // out so authors who don't use blend modes don't pay the cost.
            var backend = ConvertAndSubmit(MakeStyle(null));
            Assert.That(backend.Batcher.HasAnyMixBlendMode, Is.False,
                "Documents with no `mix-blend-mode` must leave HasAnyMixBlendMode " +
                "false so the URP pass can skip the per-frame backdrop blit.");
        }

        [Test]
        public void Explicit_normal_keyword_does_not_latch_the_flag_B3b_visual() {
            // CSS Compositing 1 §6.1 — `mix-blend-mode: normal` is the
            // initial value and produces no compositing effect. The
            // resolver returns MixBlendMode.Normal; BoxToPaintConverter
            // short-circuits the Push when the resolved mode is Normal,
            // so the batcher never sees a Push at all and the latch
            // stays false. (Without this short-circuit the perf-gate
            // would mis-fire on every authored `normal`.)
            var backend = ConvertAndSubmit(MakeStyle("normal"));
            Assert.That(backend.Batcher.HasAnyMixBlendMode, Is.False,
                "Explicit `mix-blend-mode: normal` must not latch the per-frame flag.");
        }

        [Test]
        public void Backdrop_shader_global_ids_match_shader_uniform_names_B3b_visual() {
            // The URP pass binds `_WevaBackdrop` / `_WevaBackdropAvailable`
            // via Shader.PropertyToID. If anyone renames the shader uniforms
            // without updating these ids the per-frame SetGlobalTexture lands
            // on the wrong slot and the shader's zero-backdrop fallback runs
            // even when the blit happened — a silent visual regression. This
            // test pins the binding contract.
            Assert.That(UIRenderGraphPass.IdWevaBackdrop,
                Is.EqualTo(UnityEngine.Shader.PropertyToID("_WevaBackdrop")),
                "IdWevaBackdrop must match the shader's TEXTURE2D(_WevaBackdrop) name.");
            Assert.That(UIRenderGraphPass.IdWevaBackdropAvailable,
                Is.EqualTo(UnityEngine.Shader.PropertyToID("_WevaBackdropAvailable")),
                "IdWevaBackdropAvailable must match the shader's _WevaBackdropAvailable float name.");
        }

        [Test]
        public void Push_pop_mix_blend_keeps_flag_latched_for_the_frame_B3b_visual() {
            // CSS Compositing 1 §10's backdrop sampling needs the copy
            // captured BEFORE the Weva overlay draws — which means
            // the URP pass must commit to the copy decision once at
            // frame start, not on a per-quad / per-scope basis. The
            // latch therefore needs to STAY true for the whole frame
            // after the first push, even if every scope has already
            // been popped by the time DrainBatches runs.
            var batcher = new UIBatcher();
            Assert.That(batcher.HasAnyMixBlendMode, Is.False,
                "Fresh batcher must start with the latch cleared.");
            batcher.PushMixBlendMode(MixBlendMode.Multiply);
            batcher.PopMixBlendMode();
            Assert.That(batcher.HasAnyMixBlendMode, Is.True,
                "Per-frame latch must persist across Push+Pop so the pass " +
                "knows a copy is needed even when no scope is open at drain.");
            // Reset() emulates the BeginFrame contract: a new frame
            // starts with the latch cleared so the pass re-evaluates.
            batcher.Reset();
            Assert.That(batcher.HasAnyMixBlendMode, Is.False,
                "Reset must clear the latch so the next frame re-evaluates.");
        }

        [Test]
        public void Pushing_normal_does_not_latch_the_flag_B3b_visual() {
            // A defensive Push(Normal) — possible if a caller routes the
            // Normal case through the same emitter — must NOT trip the
            // perf gate. The latch fires only on visible blend effects.
            var batcher = new UIBatcher();
            batcher.PushMixBlendMode(MixBlendMode.Normal);
            Assert.That(batcher.HasAnyMixBlendMode, Is.False,
                "Push(MixBlendMode.Normal) must not latch the flag; only " +
                "non-Normal scopes require a backdrop copy.");
        }
    }
}
#endif
