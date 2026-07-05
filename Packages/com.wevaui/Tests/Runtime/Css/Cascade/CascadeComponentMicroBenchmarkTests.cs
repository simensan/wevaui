using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using NUnit.Framework;
using Weva.Compiled;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // Decompose the cascade's per-flip cost so we can see which sub-phase
    // dominates and what's worth optimizing. Each test measures ONE
    // sub-component in isolation.
    public class CascadeComponentMicroBenchmarkTests {
        const int CardCount = 250;

        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static Document BuildRealisticDoc() {
            var sb = new StringBuilder();
            sb.Append("<section>");
            for (int i = 0; i < CardCount; i++) {
                sb.Append("<div class=\"card kind-").Append(i % 5).Append("\" id=\"c").Append(i).Append("\">");
                sb.Append("<div class=\"icon\"></div>");
                sb.Append("<div class=\"body\"><span class=\"name\">x</span></div>");
                sb.Append("<div class=\"footer\"><span class=\"badge\">x</span></div>");
                sb.Append("</div>");
            }
            sb.Append("</section>");
            return HtmlParser.Parse(sb.ToString());
        }

        static OriginatedStylesheet BuildRealisticSheet() {
            var css = new StringBuilder();
            for (int i = 0; i < 5; i++) {
                css.Append(".kind-").Append(i).Append(" { background: #333; }");
                css.Append(".kind-").Append(i).Append(":hover { background: #444; }");
                css.Append(".kind-").Append(i).Append(":active { transform: scale(0.98); }");
                css.Append(".kind-").Append(i).Append(".selected { border: 2px solid yellow; }");
            }
            css.Append(".card { padding: 8px; border-radius: 4px; }");
            css.Append(".card .icon { width: 32px; height: 32px; background: #444; }");
            css.Append(".card .body { display: flex; flex-direction: column; }");
            css.Append(".card .name { font-size: 14px; font-weight: bold; color: white; }");
            css.Append(".card .footer { display: flex; gap: 4px; margin-top: 4px; }");
            css.Append(".card .badge { color: #aaa; font-size: 11px; }");
            css.Append(".card.selected .name { color: yellow; }");
            for (int i = 0; i < 80; i++) {
                css.Append(".widget-").Append(i).Append(" { color: red; padding: 2px; }");
            }
            return Author(css.ToString());
        }

        [Test]
        public void Snapshot_refill_alone() {
            var doc = BuildRealisticDoc();
            var symbols = new SymbolTable();
            var snap = DomSnapshot.Build(doc, symbols);

            // Refill 1000 times — pure refill cost, no cascade work.
            const int iterations = 1000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) {
                snap.Refill(doc, symbols);
            }
            sw.Stop();
            double avgUs = (double)(sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency) / iterations;
            System.Console.WriteLine($"SNAPSHOT-REFILL: {avgUs:F1}µs over {iterations} iter ({snap.NodeCount} nodes)");
            // Refill cost dominates the warm-flip cost; this test is for
            // diagnostic visibility, not a strict ceiling.
            Assert.That(avgUs, Is.LessThan(1000),
                $"snapshot refill averaged {avgUs:F1}µs; ceiling 1000µs");
        }

        [Test]
        public void Single_element_compute_for_alone() {
            // Time one ComputeOrHit call against a primed engine to see
            // per-element cost of selector match + style build.
            var doc = BuildRealisticDoc();
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { BuildRealisticSheet() });
            engine.ComputeAll(doc);

            var c50 = doc.GetElementById("c50");
            // Trigger 1000 cache MISSES on c50 by bumping its Version.
            const int iterations = 1000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) {
                c50.SetAttribute("data-x", i.ToString());
                engine.Compute(c50);
            }
            sw.Stop();
            double avgUs = (double)(sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency) / iterations;
            System.Console.WriteLine($"COMPUTE-ONE: {avgUs:F1}µs over {iterations} iter");
            Assert.That(avgUs, Is.LessThan(500),
                $"single-element compute averaged {avgUs:F1}µs; ceiling 500µs");
        }

        [Test]
        public void WalkIncremental_empty_walkset() {
            // Measure the cost of WalkIncremental traversing the entire
            // tree but skipping every node (empty walkSet). This is the
            // baseline "what does the walk itself cost" number.
            var doc = BuildRealisticDoc();
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { BuildRealisticSheet() });
            engine.ComputeAll(doc);

            // Hint with a non-existent (orphan) element so closure is
            // empty → WalkIncremental skips everything but still
            // traverses the tree. Actually if walkSet is empty,
            // ComputeAllIncremental returns early. Use a leaf element
            // for closure of size 1.
            var c0 = doc.GetElementById("c0");
            const int iterations = 1000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) {
                engine.ComputeAllIncremental(doc, null, new[] { c0 });
            }
            sw.Stop();
            double avgUs = (double)(sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency) / iterations;
            System.Console.WriteLine($"WALK-INCREMENTAL-MINIMAL: {avgUs:F1}µs over {iterations} iter (no attribute mutation, just walk)");
            Assert.That(avgUs, Is.LessThan(500),
                $"empty-walkset incremental averaged {avgUs:F1}µs; ceiling 500µs");
        }

        [Test]
        public void ComputeAllIncremental_after_attribute_mutation() {
            // Full realistic warm flip — should match the
            // CascadeWallClockBenchmarkTests.Realistic_warm_class_flip
            // number (~644µs). Isolated here so changes are easier to
            // attribute.
            var doc = BuildRealisticDoc();
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { BuildRealisticSheet() });
            engine.ComputeAll(doc);
            var c50 = doc.GetElementById("c50");

            const int iterations = 200;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) {
                c50.SetAttribute("class", (i & 1) == 0 ? "card kind-0 selected" : "card kind-0");
                engine.ComputeAllIncremental(doc, null, new[] { c50 });
            }
            sw.Stop();
            double avgUs = (double)(sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency) / iterations;
            System.Console.WriteLine($"FULL-WARM-FLIP: {avgUs:F1}µs over {iterations} iter");
            Assert.That(avgUs, Is.LessThan(300),
                $"full warm flip averaged {avgUs:F1}µs; ceiling 300µs (post-RefreshNode baseline ~115µs)");
        }

        [Test]
        public void Build_rulefeatureset_alone() {
            // RuleFeatureSet construction cost. Built lazily on first
            // cascade after a stylesheet swap. Use the engine to derive
            // the compiled selector list, then measure RFS-only.
            var doc = BuildRealisticDoc();
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { BuildRealisticSheet() });
            engine.ComputeAll(doc); // primes RFS
            var rfs = engine.RuleFeatures;
            // Approximate selector count via accessible buckets.
            int selectorCount = rfs.SubjectStateMask != ElementState.None ? 1 : 0;
            const int iterations = 1000;
            // We can't rebuild RFS without rebuilding the engine.
            // Measure the engine rebuild as a proxy.
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) {
                var freshEngine = new CascadeEngine(new List<OriginatedStylesheet> { BuildRealisticSheet() });
            }
            sw.Stop();
            double avgUs = (double)(sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency) / iterations;
            System.Console.WriteLine($"ENGINE-CTOR: {avgUs:F1}µs over {iterations} iter (includes selector parse + RFS lazy)");
            Assert.That(avgUs, Is.LessThan(5000),
                $"engine construction averaged {avgUs:F1}µs; ceiling 5000µs");
        }
    }
}
