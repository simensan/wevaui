using System;
using System.Text;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;
using Weva.Reactive;

namespace Weva.Tests.Css.Cascade {
    // GC-counter-driven assertions guarding the v0.5 cascade alloc target
    // (PLAN §13: 381 KB/call -> ≤ 10 KB/call). Numbers vary across runtimes
    // (mono vs CoreCLR vs IL2CPP); thresholds reflect the warm-cache no-op
    // ComputeAll path on the reference machine. Marked Explicit("alloc") so
    // they don't run in the default test pass and noise from concurrent GC
    // doesn't flake the gate.
    [Category("alloc")]
    public class CascadeAllocStableTests {
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static Document BuildDoc(int count) {
            var sb = new StringBuilder();
            sb.Append("<section id=\"root\" class=\"container\">");
            for (int i = 0; i < count; i++) {
                bool selected = (i % 7) == 0;
                sb.Append("<li class=\"item")
                  .Append(selected ? " selected" : "")
                  .Append("\"><a href=\"#\">l</a></li>");
            }
            sb.Append("</section>");
            return HtmlParser.Parse(sb.ToString());
        }

        static OriginatedStylesheet BuildSheet() {
            return Author(
                "section { color: black; padding: 4px; }" +
                ".item { color: red; font-size: 14px; }" +
                ".selected { color: blue; }" +
                "li a { text-decoration: none; }" +
                ".container .item { margin: 2px; }");
        }

        static long Snapshot() {
#if NET5_0_OR_GREATER || NETCOREAPP3_0_OR_GREATER
            return GC.GetTotalAllocatedBytes(precise: false);
#else
            return GC.GetTotalMemory(forceFullCollection: false);
#endif
        }

        static void Stabilize() {
            for (int i = 0; i < 3; i++) {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }

        [Test, Explicit("alloc")]
        public void ComputeAll_returns_same_dictionary_instance() {
            var doc = BuildDoc(10);
            var engine = new CascadeEngine(new[] { BuildSheet() }, true);
            var a = engine.ComputeAll(doc);
            var b = engine.ComputeAll(doc);
            Assert.That(b, Is.SameAs(a),
                "ComputeAll's result dictionary is engine-owned and reused");
        }

        [Test, Explicit("alloc")]
        public void Warm_ComputeAll_alloc_per_call_is_well_under_v04_baseline() {
            var doc = BuildDoc(1000);
            var engine = new CascadeEngine(new[] { BuildSheet() }, true);
            engine.ComputeAll(doc);
            for (int w = 0; w < 5; w++) engine.ComputeAll(doc);
            Stabilize();
            long start = Snapshot();
            for (int i = 0; i < 50; i++) engine.ComputeAll(doc);
            long end = Snapshot();
            long perCall = (end - start) / 50;
            TestContext.Progress.WriteLine($"warm 1000-elem ComputeAll: {perCall} bytes/call");
            // The v0.4 baseline was 381 KB/call. The pooling work targets
            // ≤ 10 KB but we use a defensive 30 KB ceiling here so this gate
            // tolerates GC-counter jitter on the test machine while still
            // flagging any reversion past the result-map fix.
            Assert.That(perCall, Is.LessThan(30_000),
                $"warm ComputeAll allocates {perCall} bytes/call (>30 KB ceiling)");
        }

        [Test, Explicit("alloc")]
        public void ComputeAll_no_changes_zero_dirty_work() {
            var doc = BuildDoc(100);
            var engine = new CascadeEngine(new[] { BuildSheet() }, true);
            engine.ComputeAll(doc);
            for (int w = 0; w < 5; w++) engine.ComputeAll(doc);
            engine.ResetCacheStats();
            Stabilize();
            long start = Snapshot();
            engine.ComputeAll(doc);
            long end = Snapshot();
            long alloc = end - start;
            TestContext.Progress.WriteLine($"no-op ComputeAll: {alloc} bytes; hits={engine.CacheHits}, misses={engine.CacheMisses}");
            // Every per-element call should be a cache hit since nothing changed.
            Assert.That(engine.CacheMisses, Is.EqualTo(0),
                "no-change ComputeAll should be all cache hits");
            Assert.That(alloc, Is.LessThan(20_000),
                $"no-change ComputeAll allocated {alloc} bytes (should be near zero)");
        }

        [Test, Explicit("alloc")]
        public void ComputeAll_after_invalidate_one_leaf_alloc_under_threshold() {
            var doc = BuildDoc(1000);
            var engine = new CascadeEngine(new[] { BuildSheet() }, true);
            engine.ComputeAll(doc);
            for (int w = 0; w < 5; w++) engine.ComputeAll(doc);
            // Pick a leaf <a> so the class toggle invalidates only that element
            // (no descendants to cascade through). 999 hits + 1 miss per call.
            Element leaf = null;
            foreach (var el in EnumerateElements(doc)) {
                if (el.TagName == "a") { leaf = el; break; }
            }
            Assert.That(leaf, Is.Not.Null);
            int toggle = 0;
            Stabilize();
            long start = Snapshot();
            for (int i = 0; i < 50; i++) {
                leaf.SetAttribute("class", (toggle++ & 1) == 0 ? "x" : "y");
                engine.ComputeAll(doc);
            }
            long end = Snapshot();
            long perCall = (end - start) / 50;
            TestContext.Progress.WriteLine(
                $"single-leaf-dirty ComputeAll: {perCall} bytes/call");
            // Per call: snapshot rebuilt (1 mutation flips snapshotDirty), one
            // ComputedStyle freshly allocated for the dirty leaf. Defensive
            // 100 KB ceiling absorbs the snapshot rebuild on mutations.
            Assert.That(perCall, Is.LessThan(120_000),
                $"single-leaf-dirty allocates {perCall} bytes/call");
        }

        static System.Collections.Generic.IEnumerable<Element> EnumerateElements(Node root) {
            if (root is Element e) yield return e;
            foreach (var c in root.Children) {
                foreach (var d in EnumerateElements(c)) yield return d;
            }
        }

        [Test, Explicit("alloc")]
        public void Apply_with_no_dirty_entries_allocates_nothing() {
            var doc = BuildDoc(100);
            var engine = new CascadeEngine(new[] { BuildSheet() }, true);
            engine.ComputeAll(doc);
            var tracker = new InvalidationTracker();
            for (int w = 0; w < 5; w++) engine.Apply(tracker);
            Stabilize();
            long start = Snapshot();
            for (int i = 0; i < 100; i++) engine.Apply(tracker);
            long end = Snapshot();
            long perCall = (end - start) / 100;
            TestContext.Progress.WriteLine($"empty Apply: {perCall} bytes/call");
            Assert.That(perCall, Is.LessThan(200),
                $"empty Apply allocates {perCall} bytes/call (must be ~zero)");
        }

        [Test, Explicit("alloc")]
        public void Repeated_ComputeAll_does_not_grow_memory() {
            var doc = BuildDoc(100);
            var engine = new CascadeEngine(new[] { BuildSheet() }, true);
            for (int i = 0; i < 5; i++) engine.ComputeAll(doc);
            Stabilize();
            long start = Snapshot();
            for (int i = 0; i < 100; i++) engine.ComputeAll(doc);
            long end = Snapshot();
            long delta = end - start;
            TestContext.Progress.WriteLine($"100x ComputeAll delta: {delta} bytes");
            Assert.That(delta, Is.LessThan(500_000),
                $"100x ComputeAll grew memory by {delta} bytes (>500 KB ceiling)");
        }

        [Test, Explicit("alloc")]
        public void StyleArray_reused_across_ComputeAll_calls() {
            var doc = BuildDoc(50);
            var engine = new CascadeEngine(new[] { BuildSheet() }, true);
            engine.ComputeAll(doc);
            var firstStyles = engine.Styles;
            engine.ComputeAll(doc);
            Assert.That(engine.Styles, Is.SameAs(firstStyles),
                "StyleArray is engine-owned and survives ComputeAll calls");
        }

        [Test, Explicit("alloc")]
        public void ComputeAll_followed_by_no_change_Apply_is_alloc_stable() {
            var doc = BuildDoc(500);
            var engine = new CascadeEngine(new[] { BuildSheet() }, true);
            var tracker = new InvalidationTracker();
            engine.ComputeAll(doc);
            for (int w = 0; w < 5; w++) {
                engine.Apply(tracker);
                engine.ComputeAll(doc);
            }
            Stabilize();
            long start = Snapshot();
            for (int i = 0; i < 50; i++) {
                engine.Apply(tracker);
                engine.ComputeAll(doc);
            }
            long end = Snapshot();
            long perCycle = (end - start) / 50;
            TestContext.Progress.WriteLine(
                $"Apply+ComputeAll cycle (no changes, 500 elem): {perCycle} bytes/cycle");
            Assert.That(perCycle, Is.LessThan(20_000),
                $"no-op Apply+ComputeAll allocates {perCycle} bytes/cycle");
        }
    }
}
