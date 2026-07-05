using NUnit.Framework;
using Weva.Layout.Boxes;
using Weva.Paint.Conversion;

namespace Weva.Tests.Bench {
    [Category("perf")]
    public class PaintBench {
        const int Iterations = 200;
        const int Warmup = 100;

        [Test, Explicit("perf")]
        public void Convert_500() {
            var root = BenchScenes.BuildPaintFlatTree(500);
            var converter = new BoxToPaintConverter();
            for (int w = 0; w < Warmup; w++) converter.Convert(root);
            BenchScenes.StabilizeGC();
            var stats = BenchScenes.Time(Iterations, 0, () => converter.Convert(root), 500);
            BenchScenes.Report("Paint.Convert[500]", stats, -1, 500);
            Assert.That(stats.MedianMs, Is.LessThan(50.0), "500-box convert should be under 50ms");
        }

        [Test, Explicit("perf")]
        public void Convert_1000() {
            var root = BenchScenes.BuildPaintFlatTree(1000);
            var converter = new BoxToPaintConverter();
            for (int w = 0; w < Warmup; w++) converter.Convert(root);
            BenchScenes.StabilizeGC();
            var stats = BenchScenes.Time(Iterations, 0, () => converter.Convert(root), 1000);
            BenchScenes.Report("Paint.Convert[1000]", stats, -1, 1000);
        }

        [Test, Explicit("perf"), Category("Slow")]
        public void Convert_5000() {
            var root = BenchScenes.BuildPaintFlatTree(5000);
            var converter = new BoxToPaintConverter();
            for (int w = 0; w < 30; w++) converter.Convert(root);
            BenchScenes.StabilizeGC();
            var stats = BenchScenes.Time(50, 0, () => converter.Convert(root), 5000);
            BenchScenes.Report("Paint.Convert[5000]", stats, -1, 5000);
        }

        [Test, Explicit("perf")]
        public void Convert_GradientHeavy_500() {
            var root = BenchScenes.BuildPaintGradientTree(500);
            var converter = new BoxToPaintConverter();
            for (int w = 0; w < Warmup; w++) converter.Convert(root);
            BenchScenes.StabilizeGC();
            var stats = BenchScenes.Time(Iterations, 0, () => converter.Convert(root), 500);
            BenchScenes.Report("Paint.Convert_GradientHeavy[500]", stats, -1, 500);
        }

        [Test, Explicit("perf")]
        public void Convert_ShadowHeavy_500() {
            var root = BenchScenes.BuildPaintShadowTree(500);
            var converter = new BoxToPaintConverter();
            for (int w = 0; w < Warmup; w++) converter.Convert(root);
            BenchScenes.StabilizeGC();
            var stats = BenchScenes.Time(Iterations, 0, () => converter.Convert(root), 500);
            BenchScenes.Report("Paint.Convert_ShadowHeavy[500]", stats, -1, 500);
        }

        [Test, Explicit("perf")]
        public void Convert_AllocCheck_500() {
            var root = BenchScenes.BuildPaintFlatTree(500);
            var converter = new BoxToPaintConverter();
            // Warm: prime brush cache, JIT, and both PaintList + PaintCommand pools.
            // The pool only fills when callers Return() the produced list — that's
            // the contract from PLAN §13. Skipping the Return defeats the pool.
            for (int w = 0; w < 200; w++) {
                var l = converter.Convert(root);
                converter.Return(l);
            }
            BenchScenes.StabilizeGC();

            const int n = 1000;
            long before = BenchScenes.AllocatedBytes();
            for (int i = 0; i < n; i++) {
                var l = converter.Convert(root);
                converter.Return(l);
            }
            long after = BenchScenes.AllocatedBytes();
            long perCall = (after - before) / n;
            TestContext.Progress.WriteLine($"[BENCH] Paint.Convert_AllocCheck[500]: {BenchScenes.FormatBytes(perCall)}/call");
            // Pre-pooling baseline: ~1.1 MB/call (PLAN §13 entry).
            // Post-pooling steady state: ~1.5 KB/call. The remaining bytes come from
            // CssValue parse-cache cold misses on properties whose cache is cleared
            // at scope-end (border-color "black", border-width "1px"); the cold parse
            // re-tokenizes once per Convert. Eliminating that fully is a sister-task
            // job (cross-frame parse-cache for canonical literals). Per-box allocation
            // is 0 — the 1.5 KB figure is fixed-per-call regardless of box count, so
            // the soft ceiling tolerates Dictionary growth jitter.
            Assert.That(perCall, Is.LessThan(8_192),
                $"Paint.Convert allocates {perCall} bytes/call (>8 KB ceiling)");
        }

        [Test, Explicit("perf")]
        public void Convert_AllocCheck_1000() {
            var root = BenchScenes.BuildPaintFlatTree(1000);
            var converter = new BoxToPaintConverter();
            for (int w = 0; w < 100; w++) {
                var l = converter.Convert(root);
                converter.Return(l);
            }
            BenchScenes.StabilizeGC();

            const int n = 500;
            long before = BenchScenes.AllocatedBytes();
            for (int i = 0; i < n; i++) {
                var l = converter.Convert(root);
                converter.Return(l);
            }
            long after = BenchScenes.AllocatedBytes();
            long perCall = (after - before) / n;
            TestContext.Progress.WriteLine($"[BENCH] Paint.Convert_AllocCheck[1000]: {BenchScenes.FormatBytes(perCall)}/call");
            // Same fixed ~1.5 KB/call as the 500-box variant — confirming per-box is
            // 0 and the cost does not scale with tree size.
            Assert.That(perCall, Is.LessThan(8_192),
                $"Paint.Convert allocates {perCall} bytes/call (>8 KB ceiling)");
        }
    }
}
