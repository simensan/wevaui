// WEVA_URP_BATCHER_TESTS is the Tests asmdef's URP versionDefine (the
// Runtime asmdef calls its equivalent WEVA_URP — they are NOT the same
// symbol; a WEVA_URP gate here silently compiles the whole file out).
#if WEVA_URP_BATCHER_TESTS
using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Weva.Css.Values;
using Weva.Paint;
using Weva.Rendering.URP;
using Rect = Weva.Paint.Rect;

namespace Weva.Tests.Rendering.URP {
    // P16 — UIBatcher hoists the 5- and 6-element `defaults[]` arrays for the
    // gradient submit path (>4 stops) to `static readonly` fields. The previous
    // code allocated a fresh `double[]` per quad in the hot path. These tests:
    //
    //   1. Pin the default-position spacing the hoisted arrays must keep:
    //      5-stop -> (0, 0.25, 0.50, 0.75, 1.0)
    //      6-stop -> (0, 0.20, 0.40, 0.60, 0.80, 1.0)
    //      The first four positions land in BorderColorLeft (slot 8) via
    //      NormalizedPos(...) when source stops have NaN positions.
    //
    //   2. Verify the per-quad submit no longer leaks a fresh array allocation:
    //      100 SubmitFillRect calls with 5- and 6-stop linear gradients should
    //      stay near zero per-call after the hoist. The ceiling is generous so
    //      CI noise doesn't flake it but a regression from "0 -> 80 B/call"
    //      (the pre-hoist 5-elem double[] plus 6-elem double[] header) still
    //      trips it.
    public class UIBatcherGradientDefaultStopsTests {
        static LinearColor C(float r, float g, float b) => new LinearColor(r, g, b, 1f);

        // Build N stops with NaN positions so NormalizedPos falls back to the
        // hoisted `defaults[]` entries. Constructing the LinearGradient directly
        // (not through BackgroundResolver) keeps the positions NaN — the resolver
        // would auto-space them.
        static List<GradientStop> NaNStops(int count) {
            var stops = new List<GradientStop>(count);
            for (int i = 0; i < count; i++) {
                // Cycle a few colors so all stops aren't identical — the colors
                // are not what these tests pin, but distinct values guard against
                // a future "all stops collapsed" regression masking the test.
                var color = (i % 3) switch {
                    0 => C(1, 0, 0),
                    1 => C(0, 1, 0),
                    _ => C(0, 0, 1),
                };
                stops.Add(new GradientStop(color, double.NaN));
            }
            return stops;
        }

        [Test]
        public void Five_stop_gradient_with_unset_positions_uses_default_quarter_spacing() {
            // P16 parity pin: the hoisted DefaultStops5 must keep the same
            // (0, 0.25, 0.50, 0.75, 1.0) spacing the inline literal produced.
            // BorderColorLeft (slot 8) carries p0..p3; p4 lives in BorderStyles.y
            // (covered by the conic/linear branches further down in UIBatcher).
            var grad = new LinearGradient(0, NaNStops(5));
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();

            var inst = batcher.Batches[0].Instances[0];
            Assert.That(inst.BorderColorLeft.x, Is.EqualTo(0.00f).Within(1e-5f), "p0 default = 0.00");
            Assert.That(inst.BorderColorLeft.y, Is.EqualTo(0.25f).Within(1e-5f), "p1 default = 0.25");
            Assert.That(inst.BorderColorLeft.z, Is.EqualTo(0.50f).Within(1e-5f), "p2 default = 0.50");
            Assert.That(inst.BorderColorLeft.w, Is.EqualTo(0.75f).Within(1e-5f), "p3 default = 0.75");
            // p4 sits in BorderStyles.y per the slot-9 conic/linear encoding.
            // For non-repeating 5-stop linear the value comes from
            // NormalizedPos(stops, i4, 1.0) -> 1.0 fallback.
            Assert.That(inst.BorderStyles.y, Is.EqualTo(1.00f).Within(1e-5f), "p4 default = 1.00");
        }

        [Test]
        public void Six_stop_gradient_with_unset_positions_uses_default_fifth_spacing() {
            // P16 parity pin for the 6-stop branch: DefaultStops6 keeps the
            // (0, 0.20, 0.40, 0.60, 0.80, 1.0) spacing. p0..p3 -> BorderColorLeft,
            // p4 -> BorderStyles.y, p5 -> BorderStyles.z.
            var grad = new LinearGradient(0, NaNStops(6));
            var batcher = new UIBatcher();
            batcher.SubmitFillRect(new Rect(0, 0, 100, 100), Brush.Gradient(grad), BorderRadii.Zero);
            batcher.Finish();

            var inst = batcher.Batches[0].Instances[0];
            Assert.That(inst.BorderColorLeft.x, Is.EqualTo(0.00f).Within(1e-5f), "p0 default = 0.00");
            Assert.That(inst.BorderColorLeft.y, Is.EqualTo(0.20f).Within(1e-5f), "p1 default = 0.20");
            Assert.That(inst.BorderColorLeft.z, Is.EqualTo(0.40f).Within(1e-5f), "p2 default = 0.40");
            Assert.That(inst.BorderColorLeft.w, Is.EqualTo(0.60f).Within(1e-5f), "p3 default = 0.60");
            Assert.That(inst.BorderStyles.y, Is.EqualTo(0.80f).Within(1e-5f), "p4 default = 0.80");
            Assert.That(inst.BorderStyles.z, Is.EqualTo(1.00f).Within(1e-5f), "p5 default = 1.00");
        }

        // GC alloc check — see PaintAllocationTests for the ceiling rationale.
        // We measure the per-call delta after stabilizing the GC, using the
        // unforced precise=false reading so the noise floor stays low.
        static long AllocatedBytes() {
#if NET5_0_OR_GREATER || NETCOREAPP3_0_OR_GREATER
            return GC.GetTotalAllocatedBytes(precise: false);
#else
            return GC.GetTotalMemory(forceFullCollection: false);
#endif
        }

        static void StabilizeGC() {
            for (int i = 0; i < 3; i++) {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }

        [Test, Category("alloc")]
        public void SubmitFillRect_with_five_and_six_stop_gradients_does_not_allocate_per_call_defaults_array() {
            // P16 regression guard: before the hoist, the >4-stop submit path
            // allocated a fresh `new double[5]` or `new double[6]` per quad.
            // After the hoist, the `defaults[]` reference points at a shared
            // static array, so the per-call alloc rate for that array drops
            // to zero. A 5-elem managed double[] is ~64 B; a 6-elem ~72 B.
            // Pre-hoist, 200 calls (100 5-stop + 100 6-stop) would add ~13 KB
            // just from this site. The ceiling below leaves headroom for other
            // unrelated transient allocs (instance growth on first calls, etc.)
            // but still flags a >4 KB regression from the defaults[] site
            // coming back.
            var grad5 = new LinearGradient(0, NaNStops(5));
            var grad6 = new LinearGradient(0, NaNStops(6));
            var brush5 = Brush.Gradient(grad5);
            var brush6 = Brush.Gradient(grad6);
            var bounds = new Rect(0, 0, 100, 100);

            var batcher = new UIBatcher();

            // Warm-up: prime any first-call instance-list growth so the
            // measurement window only captures steady-state per-quad allocs.
            for (int i = 0; i < 32; i++) {
                batcher.SubmitFillRect(bounds, brush5, BorderRadii.Zero);
                batcher.SubmitFillRect(bounds, brush6, BorderRadii.Zero);
            }
            batcher.Finish();
            batcher.Reset();

            StabilizeGC();
            long before = AllocatedBytes();
            for (int i = 0; i < 100; i++) {
                batcher.SubmitFillRect(bounds, brush5, BorderRadii.Zero);
                batcher.SubmitFillRect(bounds, brush6, BorderRadii.Zero);
            }
            long after = AllocatedBytes();
            long delta = after - before;

            // Pre-hoist this site alone added ~13 KB across 200 calls; the
            // 4 KB ceiling is well below that but generous enough to absorb
            // CI noise + the residual instance-list amortized growth.
            Assert.That(delta, Is.LessThan(4096),
                $"SubmitFillRect with 5/6-stop gradients allocated {delta} B over 200 calls; " +
                "P16 defaults[] hoist should keep this near zero (legacy was ~13 KB).");

            batcher.Finish();
        }
    }
}
#endif // WEVA_URP_BATCHER_TESTS
