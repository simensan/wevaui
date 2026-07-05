using System;
using System.Collections.Generic;
using System.Text;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Parsing;

namespace Weva.BaselineGen {
    // Headless allocation smoke test for the layout pipeline. Mirrors the
    // structure of LayoutAllocationTests (Tests/Runtime/Layout) so we can
    // measure before/after numbers without booting the Unity test runner.
    static class LayoutAllocCheck {
        const string BuiltinUserAgent = @"
            html, body, div, section, header, footer, nav, main, article, aside, p, h1, h2, h3, h4, h5, h6, ul, ol, li, hr { display: block; }
            a, span, strong, em, b, i, u, code, small, label { display: inline; }
            br { display: inline; }
            body { margin: 0; padding: 0; }
        ";

        public static int Run() {
            int failures = 0;
            failures += MeasureN(100);
            failures += MeasureN(500);
            failures += MeasureSimple(500);
            failures += CompareWithoutPool(500);
            failures += CompareSimpleWithoutPool(500);
            return failures;
        }

        // Compare with/without pool on the minimal-text fixture (no anchors, no
        // text runs needing measurement). Surfaces the pure box-construction
        // win without the LineBreaker / measurement overhead.
        static int CompareSimpleWithoutPool(int count) {
            var sb = new StringBuilder();
            sb.Append("<section>");
            for (int i = 0; i < count; i++) sb.Append("<div></div>");
            sb.Append("</section>");
            var doc = HtmlParser.Parse(sb.ToString());

            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(BuiltinUserAgent))
            };
            var cascade = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in cascade.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            int elementCount = 0;
            foreach (var _ in AllElements(doc)) elementCount++;

            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 1024, ViewportHeightPx = 768,
                RootFontSizePx = 16, DpiPixelsPerInch = 96
            };
            ComputedStyle Resolve(Element e) => styles.TryGetValue(e, out var cs) ? cs : null;

            var withPool = new LayoutEngine(new MonoFontMetrics());
            for (int i = 0; i < 5; i++) withPool.Layout(doc, Resolve, ctx);
            const int n = 20;
            Stabilize();
            long b0 = AllocBytes();
            for (int i = 0; i < n; i++) withPool.Layout(doc, Resolve, ctx);
            long b1 = AllocBytes();
            double withPoolPerCall = (double)(b1 - b0) / n;

            var withoutPool = new LayoutEngine(new MonoFontMetrics());
            withoutPool.DiagnosticBoxPool.DisablePooling = true;
            for (int i = 0; i < 5; i++) withoutPool.Layout(doc, Resolve, ctx);
            Stabilize();
            long c0 = AllocBytes();
            for (int i = 0; i < n; i++) withoutPool.Layout(doc, Resolve, ctx);
            long c1 = AllocBytes();
            double withoutPoolPerCall = (double)(c1 - c0) / n;

            double savedPerCall = withoutPoolPerCall - withPoolPerCall;
            double savedPerElement = savedPerCall / elementCount;

            Console.WriteLine($"[Layout simple pool comparison n={count}] elements={elementCount}");
            Console.WriteLine($"  WITH    pool: {withPoolPerCall:F0} bytes/call");
            Console.WriteLine($"  WITHOUT pool: {withoutPoolPerCall:F0} bytes/call");
            Console.WriteLine($"  saved by pool: {savedPerCall:F0} bytes/call ({savedPerElement:F2} bytes/element)");
            return savedPerCall <= 0 ? 1 : 0;
        }

        // Side-by-side: same fixture, with/without the BoxPool. Demonstrates the
        // box-construction allocation savings independent of unrelated CSS-value
        // parsing allocations that dominate the overall per-call number.
        static int CompareWithoutPool(int count) {
            string html = BuildLargeHtml(count);
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(BuiltinUserAgent)),
                OriginatedStylesheet.Author(CssParser.Parse(
                    ".item { font-size: 14px; padding: 2px; margin: 1px; }" +
                    ".panel { padding: 4px; }" +
                    ".selected { color: blue; }"))
            };
            var cascade = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in cascade.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            int elementCount = 0;
            foreach (var _ in AllElements(doc)) elementCount++;

            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 1024, ViewportHeightPx = 768,
                RootFontSizePx = 16, DpiPixelsPerInch = 96
            };
            ComputedStyle Resolve(Element e) => styles.TryGetValue(e, out var cs) ? cs : null;

            // With pool.
            var withPool = new LayoutEngine(new MonoFontMetrics());
            for (int i = 0; i < 5; i++) withPool.Layout(doc, Resolve, ctx);
            const int n = 20;
            Stabilize();
            long b0 = AllocBytes();
            for (int i = 0; i < n; i++) withPool.Layout(doc, Resolve, ctx);
            long b1 = AllocBytes();
            double withPoolPerCall = (double)(b1 - b0) / n;

            // Without pool: defeat the BoxPool free-list lookup.
            var withoutPool = new LayoutEngine(new MonoFontMetrics());
            withoutPool.DiagnosticBoxPool.DisablePooling = true;
            for (int i = 0; i < 5; i++) withoutPool.Layout(doc, Resolve, ctx);
            Stabilize();
            long c0 = AllocBytes();
            for (int i = 0; i < n; i++) withoutPool.Layout(doc, Resolve, ctx);
            long c1 = AllocBytes();
            double withoutPoolPerCall = (double)(c1 - c0) / n;

            double savedPerCall = withoutPoolPerCall - withPoolPerCall;
            double savedPerElement = savedPerCall / elementCount;

            Console.WriteLine($"[Layout pool comparison n={count}] elements={elementCount}");
            Console.WriteLine($"  WITH    pool: {withPoolPerCall:F0} bytes/call");
            Console.WriteLine($"  WITHOUT pool: {withoutPoolPerCall:F0} bytes/call");
            Console.WriteLine($"  saved by pool: {savedPerCall:F0} bytes/call ({savedPerElement:F2} bytes/element)");

            return savedPerCall <= 0 ? 1 : 0;
        }

        // Minimal-text fixture to surface the box pool wins independent of the
        // CSS value parser hot path. Each child is a plain div with no text.
        static int MeasureSimple(int count) {
            var sb = new StringBuilder();
            sb.Append("<section>");
            for (int i = 0; i < count; i++) sb.Append("<div></div>");
            sb.Append("</section>");
            var doc = HtmlParser.Parse(sb.ToString());

            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(BuiltinUserAgent))
            };
            var cascade = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in cascade.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            int elementCount = 0;
            foreach (var _ in AllElements(doc)) elementCount++;

            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 1024, ViewportHeightPx = 768,
                RootFontSizePx = 16, DpiPixelsPerInch = 96
            };
            var engine = new LayoutEngine(new MonoFontMetrics());
            ComputedStyle Resolve(Element e) => styles.TryGetValue(e, out var cs) ? cs : null;

            Stabilize();
            long b0 = AllocBytes();
            engine.Layout(doc, Resolve, ctx);
            long b1 = AllocBytes();
            long firstCall = b1 - b0;

            for (int i = 0; i < 5; i++) engine.Layout(doc, Resolve, ctx);
            engine.ResetCacheStats();

            const int n = 20;
            Stabilize();
            long before = AllocBytes();
            for (int i = 0; i < n; i++) engine.Layout(doc, Resolve, ctx);
            long after = AllocBytes();
            long total = after - before;
            double perCall = (double)total / n;

            Console.WriteLine($"[Layout simple n={count}] elements={elementCount}");
            Console.WriteLine($"  first call: {firstCall:N0} bytes (cold cache)");
            Console.WriteLine($"  warm steady state: {perCall:F0} bytes/call ({perCall / elementCount:F2} bytes/element)");
            Console.WriteLine($"  cache: {engine.CacheHits} hits, {engine.CacheMisses} misses, size={engine.CacheSize}");

            int failures = 0;
            return failures;
        }

        static int MeasureN(int count) {
            string html = BuildLargeHtml(count);
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(BuiltinUserAgent)),
                OriginatedStylesheet.Author(CssParser.Parse(
                    ".item { font-size: 14px; padding: 2px; margin: 1px; }" +
                    ".panel { padding: 4px; }" +
                    ".selected { color: blue; }"))
            };
            var cascade = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in cascade.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            int elementCount = 0;
            foreach (var _ in AllElements(doc)) elementCount++;

            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 1024, ViewportHeightPx = 768,
                RootFontSizePx = 16, DpiPixelsPerInch = 96
            };
            var engine = new LayoutEngine(new MonoFontMetrics());
            ComputedStyle Resolve(Element e) => styles.TryGetValue(e, out var cs) ? cs : null;

            // First call: cold pool, cold cache. Allocates one box per element.
            Stabilize();
            long b0 = AllocBytes();
            engine.Layout(doc, Resolve, ctx);
            long b1 = AllocBytes();
            long firstCall = b1 - b0;

            // Several warm passes prime the box pool and let scratch lists settle.
            for (int i = 0; i < 5; i++) engine.Layout(doc, Resolve, ctx);

            engine.ResetCacheStats();
            // Steady-state: every Layout rebuilds and Reconcile replaces fresh
            // boxes with cached ones; pool absorbs the discarded fresh boxes.
            const int n = 20;
            Stabilize();
            long before = AllocBytes();
            for (int i = 0; i < n; i++) engine.Layout(doc, Resolve, ctx);
            long after = AllocBytes();
            long total = after - before;
            double perCall = (double)total / n;

            Console.WriteLine($"[Layout n={count}] elements={elementCount}");
            Console.WriteLine($"  first call: {firstCall:N0} bytes (cold cache)");
            Console.WriteLine($"  warm steady state: {perCall:F0} bytes/call ({perCall / elementCount:F2} bytes/element)");
            Console.WriteLine($"  cache: {engine.CacheHits} hits, {engine.CacheMisses} misses, size={engine.CacheSize}");

            int failures = 0;
            if (perCall >= firstCall) {
                Console.WriteLine("  FAIL: warm allocates >= first call");
                failures++;
            }
            return failures;
        }

        static IEnumerable<Element> AllElements(Node n) {
            if (n is Element e) yield return e;
            foreach (var c in n.Children) {
                foreach (var d in AllElements(c)) yield return d;
            }
        }

        static string BuildLargeHtml(int elementCount) {
            var sb = new StringBuilder();
            sb.Append("<section id=\"root\" class=\"container\">");
            int nPanels = elementCount / 100;
            if (nPanels < 1) nPanels = 1;
            int idCounter = 0;
            for (int p = 0; p < nPanels; p++) {
                sb.Append("<div class=\"panel zone-").Append(p % 4).Append("\">");
                for (int r = 0; r < 10; r++) {
                    sb.Append("<ul class=\"list\">");
                    for (int i = 0; i < 10; i++) {
                        bool selected = (i + r + p) % 7 == 0;
                        string cls = selected ? "item selected" : "item";
                        sb.Append("<li class=\"").Append(cls).Append("\"");
                        if ((idCounter % 13) == 0) sb.Append(" id=\"e").Append(idCounter).Append('"');
                        sb.Append("><a href=\"#\">link</a></li>");
                        idCounter++;
                    }
                    sb.Append("</ul>");
                }
                sb.Append("</div>");
            }
            sb.Append("</section>");
            return sb.ToString();
        }

        static long AllocBytes() {
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
    }
}
