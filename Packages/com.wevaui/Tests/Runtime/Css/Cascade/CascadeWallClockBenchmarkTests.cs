using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Cascade {
    // Wall-clock perf measurements for the cascade. These run inside the
    // test process (Stopwatch micro-measurements have ~1µs noise) so the
    // numbers are not directly comparable to a real Unity profile, but
    // they're stable enough to (a) catch order-of-magnitude regressions
    // and (b) compare relative cost between paths.
    //
    // Chrome's stylo / Blink cascade does a full page (~thousand elements,
    // hundred selectors) in single-digit milliseconds. We're targeting
    // <5ms for the synthetic 240-element / 6-selector fixture on a
    // modern desktop. Anything substantially over that is fertile ground
    // for the next optimization.
    public class CascadeWallClockBenchmarkTests {
        const int CardCount = 40;

        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static Document BuildSectionWithCards() {
            var sb = new StringBuilder();
            sb.Append("<section id=\"sec\">");
            for (int i = 0; i < CardCount; i++) {
                sb.Append("<div class=\"card\" id=\"c").Append(i).Append("\">");
                sb.Append("<div class=\"icon\"></div>");
                sb.Append("<div class=\"body\"><span class=\"name\">Card ").Append(i).Append("</span></div>");
                sb.Append("<div class=\"footer\"><span class=\"badge\">").Append(i).Append("</span></div>");
                sb.Append("</div>");
            }
            sb.Append("</section>");
            return HtmlParser.Parse(sb.ToString());
        }

        static OriginatedStylesheet StandardSheet() {
            return Author(
                ".card { background: #222; padding: 8px; }" +
                ".card.selected { background: #444; }" +
                ".card:hover { background: #333; }" +
                ".card:active { transform: scale(0.98); }" +
                ".card .name { font-size: 14px; }" +
                ".card.selected .name { color: yellow; }" +
                ".card .badge { color: #aaa; }" +
                ".card .icon { width: 32px; height: 32px; }"
            );
        }

        sealed class FakeStateProvider : IElementStateProvider {
            readonly Dictionary<Element, ElementState> states = new();
            long version;
            public long Version => version;
            public ElementState GetState(Element e) {
                if (e == null) return ElementState.None;
                return states.TryGetValue(e, out var v) ? v : ElementState.None;
            }
            public void SetFlag(Element e, ElementState bit, bool on) {
                var cur = states.TryGetValue(e, out var v) ? v : ElementState.None;
                var next = on ? (cur | bit) : (cur & ~bit);
                if (next == cur) return;
                if (next == ElementState.None) states.Remove(e);
                else states[e] = next;
                version++;
            }
        }

        static long Measure(System.Action action, int iterations = 1) {
            // Warm up to avoid first-call JIT noise.
            action();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) action();
            sw.Stop();
            return sw.ElapsedTicks * 1000 / Stopwatch.Frequency;
        }

        [Test]
        public void Cold_pass_baseline() {
            // First-ever cascade on a fresh engine + fresh doc. This is
            // the worst case — every element pays a full ComputeFor.
            // Run multiple iterations on fresh engines to get a stable
            // average.
            const int iterations = 10;
            long totalMs = 0;
            int totalElements = 0;
            for (int i = 0; i < iterations; i++) {
                var doc = BuildSectionWithCards();
                var engine = new CascadeEngine(new List<OriginatedStylesheet> { StandardSheet() });
                var sw = Stopwatch.StartNew();
                engine.ComputeAll(doc);
                sw.Stop();
                totalMs += sw.ElapsedTicks * 1000 / Stopwatch.Frequency;
                totalElements = (int)(engine.CacheHits + engine.CacheMisses);
            }
            long avgMs = totalMs / iterations;
            double perElement = (double)totalMs / (iterations * totalElements);
            System.Console.WriteLine($"COLD: {avgMs}ms avg over {iterations} iter, {totalElements} elements, {perElement:F3}ms/element");
            // Generous ceiling — Chrome target is 5ms, we're nowhere near
            // yet but we want a regression alarm.
            Assert.That(avgMs, Is.LessThan(200), $"cold pass averaged {avgMs}ms; ceiling 200ms");
        }

        [Test]
        public void Warm_state_flip_baseline() {
            // Realistic case post-cold-pass: cascade is primed, user
            // clicks a card. With Stage 1+2 fixes this should be ~9
            // elements re-cascaded, much smaller wall-clock.
            const int iterations = 50;
            var doc = BuildSectionWithCards();
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { StandardSheet() });
            var state = new FakeStateProvider();
            engine.ComputeAll(doc, state);

            var c5 = doc.GetElementById("c5");
            var chain = new List<Element>();
            for (Element n = c5; n != null; n = n.Parent as Element) chain.Add(n);

            // Alternate press/release across iterations to keep work real.
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) {
                bool press = (i & 1) == 0;
                foreach (var e in chain) state.SetFlag(e, ElementState.Active, press);
                engine.ComputeAllIncremental(doc, state, new[] { c5 });
            }
            sw.Stop();
            long totalMs = sw.ElapsedTicks * 1000 / Stopwatch.Frequency;
            double avgUs = (double)(sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency) / iterations;
            System.Console.WriteLine($"WARM-STATE-FLIP: {avgUs:F1}µs/flip over {iterations} iter ({totalMs}ms total)");
            // Target: under 100µs per flip on the synthetic fixture.
            Assert.That(avgUs, Is.LessThan(2000), $"warm state-flip averaged {avgUs:F1}µs; ceiling 2000µs");
        }

        [Test]
        public void Warm_class_flip_baseline() {
            const int iterations = 50;
            var doc = BuildSectionWithCards();
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { StandardSheet() });
            engine.ComputeAll(doc);

            var c5 = doc.GetElementById("c5");
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) {
                c5.SetAttribute("class", (i & 1) == 0 ? "card selected" : "card");
                engine.ComputeAllIncremental(doc, null, new[] { c5 });
            }
            sw.Stop();
            long totalMs = sw.ElapsedTicks * 1000 / Stopwatch.Frequency;
            double avgUs = (double)(sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency) / iterations;
            System.Console.WriteLine($"WARM-CLASS-FLIP: {avgUs:F1}µs/flip over {iterations} iter ({totalMs}ms total)");
            Assert.That(avgUs, Is.LessThan(2000), $"warm class-flip averaged {avgUs:F1}µs; ceiling 2000µs");
        }

        [Test]
        public void Large_doc_cold_pass() {
            // 200 cards × 6 = 1200 elements. Approximates a real game UI
            // density.
            var sb = new StringBuilder();
            sb.Append("<section>");
            for (int i = 0; i < 200; i++) {
                sb.Append("<div class=\"card\"><div class=\"icon\"></div>");
                sb.Append("<div class=\"body\"><span class=\"name\">x</span></div>");
                sb.Append("<div class=\"footer\"><span class=\"badge\">x</span></div></div>");
            }
            sb.Append("</section>");
            var doc = HtmlParser.Parse(sb.ToString());
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { StandardSheet() });

            var sw = Stopwatch.StartNew();
            engine.ComputeAll(doc);
            sw.Stop();
            long ms = sw.ElapsedTicks * 1000 / Stopwatch.Frequency;
            int elements = (int)(engine.CacheHits + engine.CacheMisses);
            System.Console.WriteLine($"LARGE-COLD: {ms}ms for {elements} elements, {(double)ms / elements:F3}ms/element");
            Assert.That(ms, Is.LessThan(500), $"1200-element cold pass took {ms}ms; ceiling 500ms");
        }

        [Test]
        public void Realistic_stylesheet_cold_pass() {
            // Approximate a real game UI: many short selectors mixing tag,
            // class, descendant, pseudo. 100 rules in the sheet, each
            // declaring 1-3 properties. Tree: 1500 elements (250 cards
            // × 6 elements). This is what surfaces the real per-element
            // cost — small fixtures hide selector-index amortization.
            var sb = new StringBuilder();
            sb.Append("<section>");
            for (int i = 0; i < 250; i++) {
                sb.Append("<div class=\"card kind-").Append(i % 5).Append("\">");
                sb.Append("<div class=\"icon\"></div>");
                sb.Append("<div class=\"body\"><span class=\"name\">x</span></div>");
                sb.Append("<div class=\"footer\"><span class=\"badge\">x</span></div>");
                sb.Append("</div>");
            }
            sb.Append("</section>");
            var doc = HtmlParser.Parse(sb.ToString());

            // Build a sheet with 100 rules spanning the variety seen in
            // a game-UI CSS file.
            var css = new StringBuilder();
            css.Append("body { color: #ccc; font-family: sans-serif; }");
            css.Append("section { display: flex; flex-direction: column; padding: 16px; gap: 8px; }");
            for (int i = 0; i < 5; i++) {
                css.Append(".kind-").Append(i).Append(" { background: hsl(").Append(i * 72).Append(",50%,30%); }");
                css.Append(".kind-").Append(i).Append(":hover { background: hsl(").Append(i * 72).Append(",60%,40%); }");
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
            css.Append(".card.selected .badge { color: orange; }");
            css.Append(".card.selected .icon { border: 1px solid yellow; }");
            // 80 more "filler" rules to bulk up selector count — types
            // that don't match many elements (typical in a real sheet
            // where most rules are for specific components).
            for (int i = 0; i < 80; i++) {
                css.Append(".widget-").Append(i).Append(" { color: red; padding: 2px; }");
            }
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { Author(css.ToString()) });

            // Run 5 cold passes, average.
            const int iterations = 5;
            long totalMs = 0;
            int totalElements = 0;
            for (int i = 0; i < iterations; i++) {
                var freshEngine = new CascadeEngine(new List<OriginatedStylesheet> { Author(css.ToString()) });
                var sw = Stopwatch.StartNew();
                freshEngine.ComputeAll(doc);
                sw.Stop();
                totalMs += sw.ElapsedTicks * 1000 / Stopwatch.Frequency;
                totalElements = (int)(freshEngine.CacheHits + freshEngine.CacheMisses);
            }
            long avgMs = totalMs / iterations;
            double perElement = (double)totalMs / (iterations * totalElements);
            System.Console.WriteLine($"REALISTIC-COLD: {avgMs}ms avg over {iterations} iter, {totalElements} elements, {perElement:F3}ms/element");
            // Chrome benchmark target: ~10ms for ~1500 elements + 100
            // rules. Post-matched-props cache: ~8ms — within range.
            Assert.That(avgMs, Is.LessThan(40), $"realistic cold pass averaged {avgMs}ms; ceiling 40ms");
        }

        [Test]
        public void Realistic_stylesheet_warm_class_flip() {
            // Same realistic fixture but measuring incremental cost of
            // a class flip. This is the real-world click-cascade scenario.
            var sb = new StringBuilder();
            sb.Append("<section>");
            for (int i = 0; i < 250; i++) {
                sb.Append("<div class=\"card kind-").Append(i % 5).Append("\" id=\"c").Append(i).Append("\">");
                sb.Append("<div class=\"icon\"></div>");
                sb.Append("<div class=\"body\"><span class=\"name\">x</span></div>");
                sb.Append("<div class=\"footer\"><span class=\"badge\">x</span></div>");
                sb.Append("</div>");
            }
            sb.Append("</section>");
            var doc = HtmlParser.Parse(sb.ToString());

            var css = new StringBuilder();
            for (int i = 0; i < 5; i++) {
                css.Append(".kind-").Append(i).Append(" { background: hsl(").Append(i * 72).Append(",50%,30%); }");
                css.Append(".kind-").Append(i).Append(":hover { background: hsl(").Append(i * 72).Append(",60%,40%); }");
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

            var engine = new CascadeEngine(new List<OriginatedStylesheet> { Author(css.ToString()) });
            engine.ComputeAll(doc);
            var c50 = doc.GetElementById("c50");

            const int iterations = 100;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) {
                c50.SetAttribute("class", (i & 1) == 0 ? "card kind-0 selected" : "card kind-0");
                engine.ComputeAllIncremental(doc, null, new[] { c50 });
            }
            sw.Stop();
            double avgUs = (double)(sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency) / iterations;
            System.Console.WriteLine($"REALISTIC-WARM-FLIP: {avgUs:F1}µs/flip over {iterations} iter ({sw.ElapsedTicks * 1000 / Stopwatch.Frequency}ms total)");
            // Baseline timeline:
            //   pre-RefreshNode:           644µs
            //   post-RefreshNode:          121µs
            //   post-matched-props cache:   62µs
            Assert.That(avgUs, Is.LessThan(150), $"realistic warm class-flip averaged {avgUs:F1}µs; ceiling 150µs");
        }

#if NET5_0_OR_GREATER
        [Test]
        public void Cascade_warm_flip_allocations() {
            // Diagnostic baseline mirroring LAYOUT-WARM-ALLOCS. Steady-
            // state alloc per cascade warm-flip drives the per-frame GC
            // pressure for click-cadence UIs.
            var doc = BuildSectionWithCards();
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { StandardSheet() });
            var state = new FakeStateProvider();
            engine.ComputeAll(doc, state);

            var c5 = doc.GetElementById("c5");
            // Pre-allocate the dirty-hint array so the benchmark itself
            // doesn't contribute ~24B per iteration to the alloc count.
            var hints = new[] { c5 };

            // Settle.
            for (int i = 0; i < 20; i++) {
                state.SetFlag(c5, ElementState.Active, (i & 1) == 0);
                engine.ComputeAllIncremental(doc, state, hints);
            }
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            System.GC.Collect();

            long bytesBefore = System.GC.GetTotalAllocatedBytes(true);
            const int iterations = 100;
            for (int i = 0; i < iterations; i++) {
                state.SetFlag(c5, ElementState.Active, (i & 1) == 0);
                engine.ComputeAllIncremental(doc, state, hints);
            }
            long bytesAfter = System.GC.GetTotalAllocatedBytes(true);
            long perCall = (bytesAfter - bytesBefore) / iterations;
            System.Console.WriteLine($"CASCADE-WARM-ALLOCS: {perCall} bytes/flip over {iterations} iter");
            Assert.That(perCall, Is.LessThan(8000),
                $"cascade warm flip allocated {perCall}b; cap 8KB");
        }
#endif

        [Test]
        public void Repeated_incremental_passes_dont_leak_time() {
            // Ensure each iteration is roughly constant cost — no per-
            // pass growth (e.g. dictionary buckets, list copies).
            var doc = BuildSectionWithCards();
            var engine = new CascadeEngine(new List<OriginatedStylesheet> { StandardSheet() });
            var state = new FakeStateProvider();
            engine.ComputeAll(doc, state);
            var c5 = doc.GetElementById("c5");

            // Two warmup batches to settle JIT + the snapshot.
            for (int w = 0; w < 50; w++) {
                state.SetFlag(c5, ElementState.Active, (w & 1) == 0);
                engine.ComputeAllIncremental(doc, state, new[] { c5 });
            }

            const int batchSize = 100;
            long batch1Ticks, batch2Ticks;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < batchSize; i++) {
                state.SetFlag(c5, ElementState.Active, (i & 1) == 0);
                engine.ComputeAllIncremental(doc, state, new[] { c5 });
            }
            sw.Stop();
            batch1Ticks = sw.ElapsedTicks;

            sw.Restart();
            for (int i = 0; i < batchSize; i++) {
                state.SetFlag(c5, ElementState.Active, (i & 1) == 0);
                engine.ComputeAllIncremental(doc, state, new[] { c5 });
            }
            sw.Stop();
            batch2Ticks = sw.ElapsedTicks;

            double ratio = (double)batch2Ticks / batch1Ticks;
            System.Console.WriteLine($"LEAK-CHECK: batch1={batch1Ticks * 1000 / Stopwatch.Frequency}ms batch2={batch2Ticks * 1000 / Stopwatch.Frequency}ms ratio={ratio:F2}");
            Assert.That(ratio, Is.LessThan(2.0), $"batch 2 took {ratio:F2}x batch 1 — time is leaking with iteration count");
        }
    }
}
