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
    // B24 v1 — CSS Compositing 1 §10 per-batch backdrop-refresh batcher tests.
    //
    // Verifies the three-part contract:
    //   1. PushMixBlendMode(non-Normal) closes the in-flight batch at the
    //      outermost Normal→non-Normal boundary (batch break on enter).
    //   2. PopMixBlendMode leaving the outermost non-Normal scope closes the
    //      batch again (batch break on exit).
    //   3. Batches that contain page-backdrop mix-blend instances have
    //      NeedsBackdropRefresh = true; element-local PushBackgroundBlend
    //      batches and Normal-only batches do NOT.
    //
    // NUnit pitfalls (from project memory): NEVER chain .Within() off
    // Is.LessThan/GreaterThan; Does.Not.Contain is substring-only on strings —
    // use Has.None.EqualTo for collection membership.
    public class UIBatcherMixBlendModeBackdropRefreshTests {
        const float Eps = 1e-4f;

        static UIBatcher MakeBatcher() {
            var b = new UIBatcher();
            b.Reset();
            return b;
        }

        static void EmitSolid(UIBatcher batcher) {
            batcher.SubmitFillRect(new Rect(0, 0, 10, 10),
                Brush.SolidColor(LinearColor.White), BorderRadii.Zero);
        }

        // ── Batch-break on PushMixBlendMode ──────────────────────────────────

        [Test]
        public void PushMixBlendMode_NonNormal_flushes_in_flight_batch() {
            // Content emitted before the scope must be in a COMPLETED batch
            // so DrainBatches has drawn it before the per-batch backdrop blit
            // fires. A batch emitted before the Push and then the Push itself
            // should leave the pre-scope content in batches[0].
            var batcher = MakeBatcher();
            EmitSolid(batcher);                          // pre-scope content
            int batchesBeforePush = batcher.Batches.Count;
            batcher.PushMixBlendMode(MixBlendMode.Multiply);
            // The flush happens inside PushMixBlendMode when depth 0→1.
            Assert.That(batcher.Batches.Count, Is.GreaterThan(batchesBeforePush),
                "PushMixBlendMode(non-Normal) must flush the in-flight batch");
        }

        [Test]
        public void PushMixBlendMode_Normal_does_NOT_flush() {
            // Pushing Normal mode is a no-op for the batch-break contract
            // (Normal mode never samples _WevaBackdrop).
            var batcher = MakeBatcher();
            EmitSolid(batcher);
            int batchesBeforePush = batcher.Batches.Count;
            batcher.PushMixBlendMode(MixBlendMode.Normal);
            Assert.That(batcher.Batches.Count, Is.EqualTo(batchesBeforePush),
                "PushMixBlendMode(Normal) must NOT flush the in-flight batch");
        }

        // ── Batch-break on PopMixBlendMode ───────────────────────────────────

        [Test]
        public void PopMixBlendMode_to_Normal_flushes_blend_batch() {
            // Content emitted inside the blend scope must be closed into a
            // completed batch before subsequent Normal content starts. This
            // ensures post-scope Normal quads go into a fresh, unflagged batch.
            var batcher = MakeBatcher();
            batcher.PushMixBlendMode(MixBlendMode.Screen);
            EmitSolid(batcher);                          // inside blend scope
            int batchesBeforePop = batcher.Batches.Count;
            batcher.PopMixBlendMode();
            Assert.That(batcher.Batches.Count, Is.GreaterThan(batchesBeforePop),
                "PopMixBlendMode (returning to Normal) must flush the blend batch");
        }

        [Test]
        public void Normal_content_after_PopMixBlendMode_goes_to_new_batch() {
            // After the blend scope closes, a fresh Normal batch must start.
            // Verify by checking that the post-scope solid lands in a later
            // batch than the blend content.
            var batcher = MakeBatcher();
            EmitSolid(batcher);                          // batch 0 (pre-scope)
            batcher.PushMixBlendMode(MixBlendMode.Multiply);
            EmitSolid(batcher);                          // blend batch
            batcher.PopMixBlendMode();
            EmitSolid(batcher);                          // post-scope Normal
            batcher.Finish();

            // Expect: pre-scope, blend, post-scope = 3 distinct batches.
            Assert.That(batcher.Batches.Count, Is.EqualTo(3),
                "pre-scope + blend + post-scope must produce 3 distinct batches");
        }

        // ── Nested scopes: only outer transition breaks ───────────────────────

        [Test]
        public void Nested_nonNormal_push_does_NOT_break_batch() {
            // A second non-Normal Push while already inside a non-Normal scope
            // must NOT issue another flush — the depth is already > 0.
            var batcher = MakeBatcher();
            batcher.PushMixBlendMode(MixBlendMode.Multiply);   // outer: 0→1
            EmitSolid(batcher);
            int batchesBeforeInnerPush = batcher.Batches.Count;
            batcher.PushMixBlendMode(MixBlendMode.Screen);     // inner: 1→2, no flush
            Assert.That(batcher.Batches.Count, Is.EqualTo(batchesBeforeInnerPush),
                "inner non-Normal PushMixBlendMode must NOT flush (depth already > 0)");
        }

        [Test]
        public void Nested_nonNormal_pop_to_outer_nonNormal_does_NOT_break_batch() {
            // Popping back to an outer non-Normal scope (depth 2→1) must NOT
            // flush; only the pop that returns to Normal (1→0) should flush.
            var batcher = MakeBatcher();
            batcher.PushMixBlendMode(MixBlendMode.Multiply);   // depth 1
            batcher.PushMixBlendMode(MixBlendMode.Screen);     // depth 2
            EmitSolid(batcher);
            int batchesBeforeInnerPop = batcher.Batches.Count;
            batcher.PopMixBlendMode();                         // depth 2→1, no flush
            Assert.That(batcher.Batches.Count, Is.EqualTo(batchesBeforeInnerPop),
                "inner Pop (depth 2→1) must NOT flush; only outermost Pop should");
            batcher.PopMixBlendMode();                         // depth 1→0, flush
            Assert.That(batcher.Batches.Count, Is.GreaterThan(batchesBeforeInnerPop),
                "outermost Pop (depth 1→0) must flush the blend batch");
        }

        // ── NeedsBackdropRefresh flag ─────────────────────────────────────────

        [Test]
        public void Mix_blend_batch_has_NeedsBackdropRefresh_true() {
            // A batch containing a page-backdrop mix-blend-mode quad must have
            // NeedsBackdropRefresh = true so DrainBatches refreshes _WevaBackdrop.
            var batcher = MakeBatcher();
            batcher.PushMixBlendMode(MixBlendMode.Multiply);
            EmitSolid(batcher);
            batcher.PopMixBlendMode();
            batcher.Finish();

            // Find the blend batch — it's the one not Normal-only.
            bool anyFlagged = false;
            for (int i = 0; i < batcher.Batches.Count; i++) {
                if (batcher.Batches[i].NeedsBackdropRefresh) {
                    anyFlagged = true;
                    break;
                }
            }
            Assert.That(anyFlagged, Is.True,
                "at least one batch in a non-Normal mix-blend scope must have NeedsBackdropRefresh");
        }

        [Test]
        public void Element_local_PushBackgroundBlend_does_NOT_set_NeedsBackdropRefresh() {
            // background-blend-mode is element-local (§9) and never samples
            // _WevaBackdrop. Its batch must NOT have NeedsBackdropRefresh so
            // we don't issue a wasteful blit for element-local blends.
            var batcher = MakeBatcher();
            batcher.PushBackgroundBlend(MixBlendMode.Multiply,
                new LinearColor(0.5f, 0.5f, 0.5f, 1f));
            EmitSolid(batcher);
            batcher.PopBackgroundBlend();
            batcher.Finish();

            for (int i = 0; i < batcher.Batches.Count; i++) {
                Assert.That(batcher.Batches[i].NeedsBackdropRefresh, Is.False,
                    $"batch {i}: element-local PushBackgroundBlend must NOT set NeedsBackdropRefresh");
            }
        }

        [Test]
        public void Normal_only_frame_has_zero_flagged_batches_and_no_extra_breaks() {
            // A frame with only Normal-mode quads must produce zero flagged
            // batches and exactly one batch (no spurious flushes).
            var batcher = MakeBatcher();
            EmitSolid(batcher);
            EmitSolid(batcher);
            batcher.Finish();

            int flaggedCount = 0;
            for (int i = 0; i < batcher.Batches.Count; i++) {
                if (batcher.Batches[i].NeedsBackdropRefresh) flaggedCount++;
            }
            Assert.That(flaggedCount, Is.EqualTo(0),
                "Normal-only frame must have zero NeedsBackdropRefresh batches");
            // Both quads share the same Solid key — they coalesce into one batch.
            Assert.That(batcher.Batches.Count, Is.EqualTo(1),
                "Normal-only frame with same-key quads must produce exactly one batch");
        }

        [Test]
        public void PushBackgroundBlend_does_NOT_break_batch_on_push_or_pop() {
            // Element-local background-blend-mode is self-contained and must
            // never issue a flush on Push or Pop.
            var batcher = MakeBatcher();
            EmitSolid(batcher);
            int batchesBeforePush = batcher.Batches.Count;
            batcher.PushBackgroundBlend(MixBlendMode.Screen,
                new LinearColor(1f, 1f, 1f, 1f));
            Assert.That(batcher.Batches.Count, Is.EqualTo(batchesBeforePush),
                "PushBackgroundBlend must NOT flush the in-flight batch");
            EmitSolid(batcher);
            int batchesBeforePop = batcher.Batches.Count;
            batcher.PopBackgroundBlend();
            Assert.That(batcher.Batches.Count, Is.EqualTo(batchesBeforePop),
                "PopBackgroundBlend must NOT flush the in-flight batch");
        }

        // ── Pre-scope content is in a completed batch before the blend batch ──

        [Test]
        public void Pre_scope_batch_is_completed_before_blend_batch() {
            // Verify that the pre-scope content (batch 0) is a DIFFERENT batch
            // from the blend content (batch 1), confirming the batch-break
            // ensures the backdrop blit sees all pre-scope geometry.
            var batcher = MakeBatcher();
            EmitSolid(batcher);                                 // pre-scope
            batcher.PushMixBlendMode(MixBlendMode.Overlay);
            EmitSolid(batcher);                                 // blend content
            batcher.PopMixBlendMode();
            batcher.Finish();

            Assert.That(batcher.Batches.Count, Is.GreaterThanOrEqualTo(2),
                "must have at least 2 batches: pre-scope and blend");
            // The blend batch must have the flag; the pre-scope batch must not.
            bool foundFlagged = false;
            bool foundUnflaggedBefore = false;
            for (int i = 0; i < batcher.Batches.Count; i++) {
                if (batcher.Batches[i].NeedsBackdropRefresh) {
                    // All earlier batches must be unflagged (pre-scope content).
                    foundFlagged = true;
                    Assert.That(foundUnflaggedBefore, Is.True,
                        "flagged batch must be preceded by at least one unflagged batch");
                } else {
                    if (!foundFlagged) foundUnflaggedBefore = true;
                }
            }
            Assert.That(foundFlagged, Is.True,
                "there must be at least one NeedsBackdropRefresh batch");
        }
    }
}
#endif
