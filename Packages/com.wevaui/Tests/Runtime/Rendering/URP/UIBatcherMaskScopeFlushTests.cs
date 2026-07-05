// Gate matches the test assembly's URP versionDefine (see
// UIRenderGraphFilterBlurPlanTests for the full rationale: bare WEVA_URP is
// undefined in Weva.Tests.Runtime and silently drops the file).
#if WEVA_URP_BATCHER_TESTS
using System.Reflection;
using NUnit.Framework;
using Weva.Paint;
using Weva.Rendering.URP;
using Rect = Weva.Paint.Rect;

namespace Weva.Tests.Rendering.URP {
    // Rendering audit NEW-1: FlushCurrentBatch used to null the mask-image
    // coverage binding (currentMaskImageTexture). The field is owned by the
    // Push/PopMask scope stack (B16) and a mask scope spans the element and
    // ALL descendants — but batches break mid-scope for reasons unrelated to
    // masking (an <img> switching batch class, subtree-capture begin, atlas
    // or instance-count overflow, blend/filter transitions). Every batch
    // flushed after such a break latched maskImageTexture: null, so the rest
    // of the scope rendered with the material-default white coverage —
    // clip-path'd content bled to the path's AABB.
    //
    // These tests inject the texture reference directly (an uninitialized
    // Texture2D shell — no native backing needed; batches only latch the
    // reference) because ResolveSyntheticMaskImageTexture needs a real
    // Texture2D upload, which requires the Unity player. The scope-stack
    // interplay itself (Push/PopMask restore) is covered by B16's tests.
    public class UIBatcherMaskScopeFlushTests {
        static UnityEngine.Texture FakeTexture() {
            return (UnityEngine.Texture)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(UnityEngine.Texture2D));
        }

        static void InjectMaskBinding(UIBatcher batcher, UnityEngine.Texture tex) {
            typeof(UIBatcher)
                .GetField("currentMaskImageTexture", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(batcher, tex);
        }

        [Test]
        public void Blend_scope_flush_inside_a_mask_scope_keeps_the_binding_for_later_batches() {
            var batcher = new UIBatcher();
            var maskTex = FakeTexture();
            InjectMaskBinding(batcher, maskTex);

            // Batch 1: content before the blend scope.
            batcher.SubmitFillRect(new Rect(0, 0, 10, 10),
                Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            // Outermost Normal -> non-Normal boundary flushes batch 1 mid-"scope".
            batcher.PushMixBlendMode(MixBlendMode.Multiply);
            batcher.SubmitFillRect(new Rect(0, 0, 10, 10),
                Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.PopMixBlendMode(); // flushes batch 2
            // Batch 3: content after the blend scope, still inside the mask scope.
            batcher.SubmitFillRect(new Rect(0, 0, 10, 10),
                Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.Finish();

            Assert.That(batcher.Batches.Count, Is.EqualTo(3));
            for (int i = 0; i < batcher.Batches.Count; i++) {
                Assert.That(batcher.Batches[i].MaskImageTexture, Is.SameAs(maskTex),
                    $"batch {i} lost the mask-image binding after a mid-scope flush");
            }
        }

        [Test]
        public void Instance_overflow_continuation_batch_keeps_the_binding() {
            var batcher = new UIBatcher { MaxInstancesPerBatch = 4 };
            var maskTex = FakeTexture();
            InjectMaskBinding(batcher, maskTex);

            // Overflow MaxInstancesPerBatch so AppendInstance flushes and keeps
            // the same key open — the continuation batch must keep the binding.
            for (int i = 0; i < 5; i++) {
                batcher.SubmitFillRect(new Rect(0, 0, 10, 10),
                    Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            }
            batcher.Finish();

            Assert.That(batcher.Batches.Count, Is.EqualTo(2));
            Assert.That(batcher.Batches[0].MaskImageTexture, Is.SameAs(maskTex));
            Assert.That(batcher.Batches[1].MaskImageTexture, Is.SameAs(maskTex),
                "the overflow continuation batch lost the mask-image binding");
        }

        [Test]
        public void Reset_clears_the_binding_between_frames() {
            var batcher = new UIBatcher();
            InjectMaskBinding(batcher, FakeTexture());
            batcher.Reset();
            batcher.SubmitFillRect(new Rect(0, 0, 10, 10),
                Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
            batcher.Finish();
            Assert.That(batcher.Batches[0].MaskImageTexture, Is.Null);
        }
    }
}
#endif
