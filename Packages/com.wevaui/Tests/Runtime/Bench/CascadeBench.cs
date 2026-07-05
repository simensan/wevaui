using System.Collections.Generic;
using NUnit.Framework;
using Weva.Compiled;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;

namespace Weva.Tests.Bench {
    [Category("perf")]
    public class CascadeBench {
        const int Iterations = 50;
        const int Warmup = 10;

        static BenchScenes.TimeStats RunComputeAll(BenchScenes.Scene scene) {
            var engine = new CascadeEngine(scene.Sheets, true);
            for (int w = 0; w < Warmup; w++) {
                engine.InvalidateAll();
                engine.ComputeAll(scene.Document);
            }
            BenchScenes.StabilizeGC();
            return BenchScenes.Time(Iterations, 0, () => {
                engine.InvalidateAll();
                engine.ComputeAll(scene.Document);
            }, scene.ElementCount);
        }

        [Test, Explicit("perf")]
        public void ComputeAll_100() {
            var s = BenchScenes.Build100Cards();
            var stats = RunComputeAll(s);
            BenchScenes.Report($"Cascade.ComputeAll[{s.Name}/{s.ElementCount}elem]", stats, -1, s.ElementCount);
            Assert.That(stats.MedianMs, Is.LessThan(50.0), "100-elem ComputeAll should be well under 50ms");
        }

        [Test, Explicit("perf")]
        public void ComputeAll_500() {
            var s = BenchScenes.Build500Mixed();
            var stats = RunComputeAll(s);
            BenchScenes.Report($"Cascade.ComputeAll[{s.Name}/{s.ElementCount}elem]", stats, -1, s.ElementCount);
            Assert.That(stats.MedianMs, Is.LessThan(150.0), "500-elem ComputeAll should be under 150ms");
        }

        [Test, Explicit("perf")]
        public void ComputeAll_1000() {
            var s = BenchScenes.Build1000Forms();
            var stats = RunComputeAll(s);
            BenchScenes.Report($"Cascade.ComputeAll[{s.Name}/{s.ElementCount}elem]", stats, -1, s.ElementCount);
            Assert.That(stats.MedianMs, Is.LessThan(300.0), "1000-elem ComputeAll should be under 300ms");
        }

        [Test, Explicit("perf")]
        public void ComputeAll_1000Deep() {
            var s = BenchScenes.Build1000Deep();
            var stats = RunComputeAll(s);
            BenchScenes.Report($"Cascade.ComputeAll[{s.Name}/{s.ElementCount}elem]", stats, -1, s.ElementCount);
            Assert.That(stats.MedianMs, Is.LessThan(500.0), "1000-deep ComputeAll should be under 500ms");
        }

        [Test, Explicit("perf"), Category("Slow")]
        public void ComputeAll_5000() {
            var s = BenchScenes.Build5000Massive();
            // Fewer iterations on the massive fixture so the bench finishes inside a
            // reasonable wall-clock window on slow machines (>10s otherwise).
            var engine = new CascadeEngine(s.Sheets, true);
            for (int w = 0; w < 3; w++) {
                engine.InvalidateAll();
                engine.ComputeAll(s.Document);
            }
            BenchScenes.StabilizeGC();
            var stats = BenchScenes.Time(10, 0, () => {
                engine.InvalidateAll();
                engine.ComputeAll(s.Document);
            }, s.ElementCount);
            BenchScenes.Report($"Cascade.ComputeAll[{s.Name}/{s.ElementCount}elem]", stats, -1, s.ElementCount);
        }

        [Test, Explicit("perf")]
        public void Incremental_AttributeChange() {
            var s = BenchScenes.Build1000Forms();
            var engine = s.Cascade;
            // Find a reasonable target element with stable identity.
            Element target = null;
            foreach (var e in BenchScenes.AllElements(s.Document)) {
                if (e.TagName == "label") { target = e; break; }
            }
            Assert.That(target, Is.Not.Null);

            // Warm: attribute toggling already exercises the cache-invalidate path
            // since SetAttribute bumps Version.
            for (int w = 0; w < Warmup; w++) {
                target.SetAttribute("class", w % 2 == 0 ? "highlight" : "");
                engine.Invalidate(target);
                engine.ComputeAll(s.Document);
            }
            BenchScenes.StabilizeGC();
            int toggle = 0;
            var stats = BenchScenes.Time(Iterations, 0, () => {
                target.SetAttribute("class", (toggle++ & 1) == 0 ? "highlight" : "");
                engine.Invalidate(target);
                engine.ComputeAll(s.Document);
            }, s.ElementCount);
            BenchScenes.Report($"Cascade.Incremental_AttributeChange[{s.ElementCount}elem]", stats, -1, s.ElementCount);
        }

        [Test, Explicit("perf")]
        public void Incremental_PseudoClassChange() {
            var s = BenchScenes.Build1000Forms();
            var engine = new CascadeEngine(s.Sheets, true);
            engine.ComputeAll(s.Document);

            var state = new TestStateProvider();
            Element target = null;
            foreach (var e in BenchScenes.AllElements(s.Document)) {
                if (e.TagName == "button") { target = e; break; }
            }
            Assert.That(target, Is.Not.Null);

            for (int w = 0; w < Warmup; w++) {
                state.Hovered = (w & 1) == 0 ? target : null;
                engine.ComputeAll(s.Document, state);
            }
            BenchScenes.StabilizeGC();
            int toggle = 0;
            var stats = BenchScenes.Time(Iterations, 0, () => {
                state.Hovered = (toggle++ & 1) == 0 ? target : null;
                engine.ComputeAll(s.Document, state);
            }, s.ElementCount);
            BenchScenes.Report($"Cascade.Incremental_PseudoClassChange[{s.ElementCount}elem]", stats, -1, s.ElementCount);
        }

        [Test, Explicit("perf")]
        public void SelectorMatch_Hot_1000() {
            var s = BenchScenes.Build1000Forms();
            // Drop to the snapshot/index level directly so we measure the matcher
            // and not the surrounding cascade pipeline.
            var sym = new SymbolTable();
            var snap = DomSnapshot.Build(s.Document, sym);
            var sels = new List<CompiledSelector>();
            foreach (var sheet in s.Sheets) {
                if (sheet.Stylesheet == null) continue;
                foreach (var rule in sheet.Stylesheet.Rules) {
                    if (rule is StyleRule sr) {
                        foreach (var selText in sr.Selectors) {
                            try { sels.Add(SelectorParser.Parse(selText)); } catch { }
                        }
                    }
                }
            }
            var idx = new SelectorIndex(sym, sels);
            var sink = new List<int>();
            var scratch = new IntsBuffer();

            for (int w = 0; w < Warmup; w++) {
                sink.Clear();
                for (int nid = 0; nid < snap.NodeCount; nid++) {
                    if (snap.Kinds[nid] != NodeKind.Element) continue;
                    SnapshotMatcher.MatchInto(snap, nid, idx, sels, null, scratch, sink);
                }
            }
            BenchScenes.StabilizeGC();
            var stats = BenchScenes.Time(Iterations, 0, () => {
                sink.Clear();
                for (int nid = 0; nid < snap.NodeCount; nid++) {
                    if (snap.Kinds[nid] != NodeKind.Element) continue;
                    SnapshotMatcher.MatchInto(snap, nid, idx, sels, null, scratch, sink);
                }
            }, s.ElementCount);
            BenchScenes.Report($"Cascade.SelectorMatch_Hot[{s.ElementCount}elem,{sels.Count}sels]", stats, -1, s.ElementCount);
        }

        [Test, Explicit("perf")]
        public void IncrementalApply_AllocCheck_1000() {
            var s = BenchScenes.Build1000Forms();
            var engine = new CascadeEngine(s.Sheets, true);
            engine.ComputeAll(s.Document);
            // Warm.
            for (int w = 0; w < Warmup; w++) {
                engine.ComputeAll(s.Document);
            }
            BenchScenes.StabilizeGC();
            long before = BenchScenes.AllocatedBytes();
            for (int i = 0; i < 100; i++) {
                engine.ComputeAll(s.Document);
            }
            long after = BenchScenes.AllocatedBytes();
            long perCall = (after - before) / 100;
            TestContext.Progress.WriteLine(
                $"[BENCH] Cascade.IncrementalApply_AllocCheck[{s.ElementCount}elem]: {BenchScenes.FormatBytes(perCall)}/call");
            // Fully-cached re-run path. v0.5 introduced a reusable result map
            // and StyleArray on the engine plus DomSnapshot reuse when the
            // document hasn't mutated, dropping per-call alloc from the v0.4
            // 381 KB baseline to the PLAN target of 10 KB/call. The threshold
            // is set defensively to absorb GC-counter jitter on test runners
            // without masking a real regression.
            Assert.That(perCall, Is.LessThan(10_000),
                $"Cache-hit ComputeAll allocates {perCall} bytes/call (>10 KB target)");
        }

        [Test, Explicit("perf")]
        public void DeepNested_Cascade_1000Deep() {
            var s = BenchScenes.Build1000Deep();
            var stats = RunComputeAll(s);
            BenchScenes.Report($"Cascade.DeepNested[{s.ElementCount}elem]", stats, -1, s.ElementCount);
        }

        [Test, Explicit("perf")]
        public void SnapshotPath_Vs_Managed_500() {
            var s = BenchScenes.Build500Mixed();

            var snapEngine = new CascadeEngine(s.Sheets, true);
            for (int w = 0; w < Warmup; w++) {
                snapEngine.InvalidateAll();
                snapEngine.ComputeAll(s.Document);
            }
            BenchScenes.StabilizeGC();
            var snapStats = BenchScenes.Time(Iterations, 0, () => {
                snapEngine.InvalidateAll();
                snapEngine.ComputeAll(s.Document);
            }, s.ElementCount);

            var manEngine = new CascadeEngine(s.Sheets, false);
            for (int w = 0; w < Warmup; w++) {
                manEngine.InvalidateAll();
                manEngine.ComputeAll(s.Document);
            }
            BenchScenes.StabilizeGC();
            var manStats = BenchScenes.Time(Iterations, 0, () => {
                manEngine.InvalidateAll();
                manEngine.ComputeAll(s.Document);
            }, s.ElementCount);

            double speedup = manStats.MedianMs / snapStats.MedianMs;
            BenchScenes.Report($"Cascade.SnapshotPath[{s.Name}]", snapStats, -1, s.ElementCount);
            BenchScenes.Report($"Cascade.ManagedPath[{s.Name}]", manStats, -1, s.ElementCount);
            TestContext.Progress.WriteLine($"[BENCH] Cascade snapshot speedup: {speedup:F2}x");
            Assert.That(speedup, Is.GreaterThan(1.0), "snapshot path must beat managed");
        }

        sealed class TestStateProvider : IElementStateProvider {
            Element hovered;
            long version;
            public Element Hovered {
                get => hovered;
                set { if (!ReferenceEquals(hovered, value)) { hovered = value; version++; } }
            }
            public ElementState GetState(Element e) {
                return ReferenceEquals(e, hovered) ? ElementState.Hover : ElementState.None;
            }
            public long Version => version;
        }
    }
}
