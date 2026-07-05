// PERF-1 headless allocation/timing probe for the cascade warm path.
// Invoked via: dotnet run -c Release -- --perf1-probe
// Also exposed as an [Explicit] NUnit test for manual --filter runs.
// The runner (Runner.cs) skips [Explicit] tests, so this never runs in CI.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using NUnit.Framework;
using Weva.Compiled;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Perf {

    public static class CascadeWarmAllocProbe {

        static OriginatedStylesheet Author(string s) =>
            OriginatedStylesheet.Author(CssParser.Parse(s));

        // 500-element doc matching the in-Unity benchmark ceiling test.
        static Document BuildDoc(int count = 500) {
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

        // Realistic DomSnapshot.Refill target — matches CascadeComponentMicroBenchmarkTests.
        static Document BuildRealisticDoc(int cardCount = 250) {
            var sb = new StringBuilder();
            sb.Append("<section>");
            for (int i = 0; i < cardCount; i++) {
                sb.Append("<div class=\"card kind-").Append(i % 5).Append("\" id=\"c").Append(i).Append("\">");
                sb.Append("<div class=\"icon\"></div>");
                sb.Append("<div class=\"body\"><span class=\"name\">x</span></div>");
                sb.Append("<div class=\"footer\"><span class=\"badge\">x</span></div>");
                sb.Append("</div>");
            }
            sb.Append("</section>");
            return HtmlParser.Parse(sb.ToString());
        }

        static long GetAllocBytes() {
            return GC.GetTotalAllocatedBytes(precise: false);
        }

        static void Stabilize() {
            for (int i = 0; i < 3; i++) {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }

        public static void RunProbe() {
            Console.WriteLine("=== PERF-1 Cascade Warm Alloc Probe ===");

            // -----------------------------------------------------------------------
            // Part A: warm ComputeAll allocation (matches ComputeAll_after_warmup...)
            // -----------------------------------------------------------------------
            var doc = BuildDoc(500);
            var sheet = BuildSheet();
            var engine = new CascadeEngine(new[] { sheet }, true);

            // Warmup — 5 passes to grow all internal lists/dicts to steady-state.
            for (int i = 0; i < 5; i++) {
                engine.InvalidateAll();
                engine.ComputeAll(doc);
            }

            engine.InvalidateAll();
            Stabilize();
            long a0 = GetAllocBytes();
            engine.ComputeAll(doc);
            long a1 = GetAllocBytes();
            long warmComputeAllBytes = a1 - a0;

            Console.WriteLine($"[A] warm ComputeAll (500 elements): {warmComputeAllBytes:N0} bytes");
            Console.WriteLine($"    ceiling: 500,000 bytes; ratio: {(double)warmComputeAllBytes / 500_000:P1}");

            // -----------------------------------------------------------------------
            // Part B: DomSnapshot.Refill timing (matches Snapshot_refill_alone)
            // -----------------------------------------------------------------------
            var rdoc = BuildRealisticDoc(250);
            var symbols = new SymbolTable();
            var snap = DomSnapshot.Build(rdoc, symbols);

            // Warmup refills.
            for (int i = 0; i < 10; i++) snap.Refill(rdoc, symbols);

            const int refillIterations = 1000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < refillIterations; i++) {
                snap.Refill(rdoc, symbols);
            }
            sw.Stop();
            double avgUs = (double)(sw.ElapsedTicks * 1_000_000 / Stopwatch.Frequency) / refillIterations;
            Console.WriteLine($"[B] DomSnapshot.Refill ({snap.NodeCount} nodes): {avgUs:F1} µs avg over {refillIterations} iter");
            Console.WriteLine($"    ceiling: 1000 µs; ratio: {avgUs / 1000.0:P1}");

            // -----------------------------------------------------------------------
            // Part C: SymbolTable.Intern(source, start, length) hot path check
            // -----------------------------------------------------------------------
            var symbols2 = new SymbolTable();
            // Pre-populate with 50 strings (realistic symbol count after doc parse).
            for (int i = 0; i < 50; i++) symbols2.Intern("symbol-" + i);

            const int internIter = 50_000;
            string classAttr = "card kind-0 selected";
            Stabilize();
            long c0 = GetAllocBytes();
            var sw2 = Stopwatch.StartNew();
            for (int i = 0; i < internIter; i++) {
                // Simulate the Refill path: intern each class token by substring.
                int start = 0;
                int len = classAttr.Length;
                while (start < len) {
                    while (start < len && classAttr[start] == ' ') start++;
                    int end = start;
                    while (end < len && classAttr[end] != ' ') end++;
                    if (end > start) symbols2.Intern(classAttr, start, end - start);
                    start = end;
                }
            }
            sw2.Stop();
            long c1 = GetAllocBytes();
            double internNs = (double)(sw2.ElapsedTicks * 1_000_000_000L / Stopwatch.Frequency) / internIter;
            Console.WriteLine($"[C] Intern(source,start,len) per iter (3 tokens, {internIter} iter): {internNs:F0} ns; alloc: {c1 - c0:N0} bytes");

            // -----------------------------------------------------------------------
            // Part D: SnapshotPassState.ElementToNodeId rebuild cost
            // -----------------------------------------------------------------------
            var rdoc2 = BuildRealisticDoc(250);
            var syms3 = new SymbolTable();
            var snap2 = DomSnapshot.Build(rdoc2, syms3);
            var engine2 = new CascadeEngine(new[] { BuildSheet() }, true);
            engine2.ComputeAll(rdoc2);  // prime snapshot + index
            // Force 200 ComputeAll warm calls to measure ElementToNodeId rebuild cost.
            const int passIter = 200;
            Stabilize();
            long d0 = GetAllocBytes();
            var sw3 = Stopwatch.StartNew();
            for (int i = 0; i < passIter; i++) {
                engine2.InvalidateAll();
                engine2.ComputeAll(rdoc2);
            }
            sw3.Stop();
            long d1 = GetAllocBytes();
            double perPassUs = (double)(sw3.ElapsedTicks * 1_000_000 / Stopwatch.Frequency) / passIter;
            long perPassBytes = (d1 - d0) / passIter;
            Console.WriteLine($"[D] warm ComputeAll (InvalidateAll+ComputeAll, {snap2.NodeCount} nodes): {perPassUs:F1} µs, {perPassBytes:N0} bytes/pass over {passIter} iter");

            Console.WriteLine("=== Done ===");
        }
    }

    // Keep a thin NUnit wrapper so the class is visible to --filter if needed.
    [Explicit("PERF-1 headless probe — run via: dotnet run -c Release -- --perf1-probe. Never runs in CI.")]
    public class CascadeWarmAllocProbeTests {
        [Test]
        public void Run_cascade_warm_alloc_probe() {
            CascadeWarmAllocProbe.RunProbe();
            Assert.Pass("Probe complete — see console output for numbers.");
        }
    }
}
