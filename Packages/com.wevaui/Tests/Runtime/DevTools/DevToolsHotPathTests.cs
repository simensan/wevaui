using System;
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.DevTools;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint.Conversion;
using Weva.Reactive;

namespace Weva.Tests.DevTools {
    [Category("alloc")]
    public class DevToolsHotPathTests {
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

        static BlockBox MakeBox(double x, double y, double w, double h) {
            var b = new BlockBox();
            b.X = x; b.Y = y; b.Width = w; b.Height = h;
            return b;
        }

        [Test]
        public void BoxOutlineRenderer_steady_state_zero_per_call_alloc() {
            var renderer = new BoxOutlineRenderer { SkipAnonymousBoxes = false };
            var root = MakeBox(0, 0, 100, 100);
            for (int i = 0; i < 32; i++) {
                var c = MakeBox(i * 4, 0, 10, 10);
                root.AddChild(c);
            }
            // Warm up internal scratch buffer.
            for (int i = 0; i < 4; i++) renderer.Emit(root);

            StabilizeGC();
            long before = AllocatedBytes();
            for (int i = 0; i < 100; i++) {
                renderer.Emit(root);
            }
            long after = AllocatedBytes();
            long delta = after - before;
            Assert.That(delta, Is.LessThan(16 * 1024),
                "BoxOutlineRenderer.Emit should reuse its scratch list — allocated " + delta + " bytes over 100 calls");
        }

        [Test]
        public void DirtyHighlighter_disabled_zero_alloc() {
            // The "overlay disabled" hot path: never call CaptureFrame.
            var h = new DirtyHighlighter();
            StabilizeGC();
            long before = AllocatedBytes();
            for (int i = 0; i < 1000; i++) {
                int n = h.Active.Count;
                if (n != 0) Assert.Fail();
            }
            long after = AllocatedBytes();
            // One allocation-context block crossing during the loop is
            // unavoidable on the JIT path and its size is RUNTIME-dependent:
            // Mono uses 4KB contexts, .NET 8 (headless TestVerifyAll, where
            // this test runs since the AR2 inversion) hands out 16,400-byte
            // blocks. 32KB tolerates one block on either runtime while still
            // failing loudly on a real per-iteration leak (>=33 B x 1000).
            Assert.That(after - before, Is.LessThan(32 * 1024));
        }

        [Test]
        public void DirtyHighlighter_empty_tracker_capture_bounded() {
            var h = new DirtyHighlighter();
            var tracker = new InvalidationTracker();
            h.CaptureFrame(tracker);
            StabilizeGC();
            long before = AllocatedBytes();
            for (int i = 0; i < 100; i++) {
                h.CaptureFrame(tracker);
            }
            long after = AllocatedBytes();
            // Empty tracker capture may allocate the enumerator; but it must
            // be bounded.
            Assert.That(after - before, Is.LessThan(64 * 1024));
        }

        [Test]
        public void CacheStats_record_zero_alloc() {
            var converter = new BoxToPaintConverter();
            var s = new ComputedStyle(new Element("div"));
            s.Set("background-color", "red");
            var box = MakeBox(0, 0, 10, 10);
            box.Style = s;
            converter.Convert(box);
            var stats = new CacheStats();
            stats.RecordFrame(converter);

            StabilizeGC();
            long before = AllocatedBytes();
            for (int i = 0; i < 1000; i++) {
                stats.RecordFrame(converter);
            }
            long after = AllocatedBytes();
            // Runtime-agnostic budget: one allocation-context block (4KB on
            // Mono, 16,400 B on .NET 8) — see the comment in
            // DirtyHighlighter_disabled_zero_alloc.
            Assert.That(after - before, Is.LessThan(32 * 1024));
        }

        [Test]
        public void PerfReadout_record_bounded_alloc() {
            var p = new PerfReadout();
            p.Start();
            for (int i = 0; i < 4; i++) p.RecordFrame(0.016);

            StabilizeGC();
            long before = AllocatedBytes();
            for (int i = 0; i < 200; i++) {
                p.RecordFrame(0.016);
            }
            long after = AllocatedBytes();
            // Recorder samples + bookkeeping: tolerate up to 32 KB over 200 calls.
            Assert.That(after - before, Is.LessThan(32 * 1024));
            p.Dispose();
        }

        [Test]
        public void OutlineRenderer_emit_scales_linearly_with_box_count() {
            var renderer = new BoxOutlineRenderer { SkipAnonymousBoxes = false };
            var root = MakeBox(0, 0, 1000, 1000);
            for (int i = 0; i < 100; i++) {
                root.AddChild(MakeBox(i * 5, 0, 4, 4));
            }
            var rects = renderer.Emit(root);
            Assert.That(rects.Count, Is.EqualTo(404));
        }
    }
}
