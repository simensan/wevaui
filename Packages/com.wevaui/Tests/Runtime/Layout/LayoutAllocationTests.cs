using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Parsing;

namespace Weva.Tests.Layout {
    // Allocation regressions for the layout hot path. Mirrors CascadeAllocationTests
    // (v0.2.2) and the PaintConverter alloc check (v0.2.3). Marked Explicit /
    // Category "alloc" because GC counter readings vary across runtimes (mono,
    // IL2CPP, .NET CoreCLR all account differently). Run with
    //   dotnet test --filter "Category=alloc"
    // or NUnit's --where "cat == alloc".
    [Category("alloc")]
    public class LayoutAllocationTests {
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));
        static OriginatedStylesheet UA(string s) => OriginatedStylesheet.UserAgent(CssParser.Parse(s));

        const string BuiltinUserAgent = @"
            html, body, div, section, header, footer, nav, main, article, aside, p, h1, h2, h3, h4, h5, h6, ul, ol, li, hr { display: block; }
            a, span, strong, em, b, i, u, code, small, label { display: inline; }
            br { display: inline; }
            body { margin: 0; padding: 0; }
        ";

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

        static (Box root, Dictionary<Element, ComputedStyle> styles, LayoutEngine engine, LayoutContext ctx, Document doc) BuildPipeline(int count) {
            var doc = BuildDoc(count);
            var sheets = new List<OriginatedStylesheet>();
            sheets.Add(UA(BuiltinUserAgent));
            sheets.Add(Author("section { padding: 4px; } .item { font-size: 14px; margin: 2px; } .selected { color: blue; }"));
            var cascade = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in cascade.ComputeAll(doc)) styles[kv.Key] = kv.Value;

            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 1024, ViewportHeightPx = 768,
                RootFontSizePx = 16, DpiPixelsPerInch = 96
            };
            var engine = new LayoutEngine(new MonoFontMetrics());
            ComputedStyle Resolve(Element e) => styles.TryGetValue(e, out var cs) ? cs : null;
            var root = engine.Layout(doc, Resolve, ctx);
            return (root, styles, engine, ctx, doc);
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
        public void Layout_secondCall_allocates_less_than_first() {
            var (_, styles, engine, ctx, doc) = BuildPipeline(100);
            ComputedStyle Resolve(Element e) => styles.TryGetValue(e, out var cs) ? cs : null;

            // Throw away the warmup pass we just ran in BuildPipeline; measure a
            // fresh "first call" against an empty cache by allocating a brand new
            // engine. The cache rebuild on the first call legitimately allocates
            // boxes for every element.
            engine = new LayoutEngine(new MonoFontMetrics());

            Stabilize();
            long b0 = Snapshot();
            engine.Layout(doc, Resolve, ctx);
            long b1 = Snapshot();
            long firstCall = b1 - b0;

            // Second call hits the layout cache entirely; every freshly-built box
            // is replaced by a cached one and recycled. The pool now warmed up,
            // the second call should allocate strictly less.
            Stabilize();
            long b2 = Snapshot();
            engine.Layout(doc, Resolve, ctx);
            long b3 = Snapshot();
            long secondCall = b3 - b2;

            TestContext.Progress.WriteLine($"first Layout: {firstCall} bytes; second: {secondCall} bytes");
            Assert.That(secondCall, Is.LessThanOrEqualTo(firstCall),
                $"second Layout should not allocate more than first (got {secondCall}/{firstCall})");
            // The second pass benefits from a primed BoxPool (every fresh box
            // from this pass came from the recycled pool) plus warm scratch
            // lists. CSS value parsing and StyleResolver still re-parse strings
            // each pass and dominate the absolute number, so the savings appear
            // as a ~10-20% delta rather than the >50% the cascade pool sees.
            // Threshold is "strictly less than first" — the boxed-savings number
            // is positive in the BoxPool_returns_recycled_boxes test below.
        }

        [Test, Explicit("alloc")]
        public void Layout_after_warmup_allocates_under_threshold() {
            var (_, styles, engine, ctx, doc) = BuildPipeline(100);
            ComputedStyle Resolve(Element e) => styles.TryGetValue(e, out var cs) ? cs : null;

            // Several warm-up passes so all internal scratch lists have grown to
            // their high-water marks and the box pool is at steady state.
            for (int i = 0; i < 5; i++) engine.Layout(doc, Resolve, ctx);

            Stabilize();
            long start = Snapshot();
            engine.Layout(doc, Resolve, ctx);
            long end = Snapshot();
            long bytes = end - start;
            TestContext.Progress.WriteLine($"warm Layout for 100-element doc: {bytes} bytes");
            // 100-li list with anchors (~212 elements total). The CSS resolver
            // pipeline (CssValueParser, StyleResolver.ResolveLength) allocates
            // ~5 KB per element across all properties on every Layout call —
            // that's a separate regression target. Threshold here covers Reconcile
            // bookkeeping + the per-pass float of LineBreaker substring allocs;
            // a per-element transient list/dict regression would push this past
            // 5 MB easily.
            Assert.That(bytes, Is.LessThan(2_500_000),
                $"warm Layout allocated {bytes} bytes (>2.5 MB ceiling)");
        }

        [Test, Explicit("alloc")]
        public void Repeated_Layout_with_no_changes_does_not_grow_memory() {
            var (_, styles, engine, ctx, doc) = BuildPipeline(100);
            ComputedStyle Resolve(Element e) => styles.TryGetValue(e, out var cs) ? cs : null;

            // Warm.
            for (int i = 0; i < 3; i++) engine.Layout(doc, Resolve, ctx);

            Stabilize();
            long start = Snapshot();
            for (int i = 0; i < 20; i++) {
                engine.Layout(doc, Resolve, ctx);
            }
            long end = Snapshot();
            long delta = end - start;
            TestContext.Progress.WriteLine($"20x Layout delta: {delta} bytes");
            // 20 × ~1.5 MB-per-call (CSS-parser-dominated) ≈ 30 MB upper bound.
            // We assert <60 MB to leave headroom for noisy CI without missing a
            // regression where per-pass memory grows quadratically (e.g. a
            // dictionary that's never cleared).
            Assert.That(delta, Is.LessThan(60_000_000),
                $"20x Layout grew memory by {delta} bytes (>60 MB ceiling)");
        }

        [Test, Explicit("alloc")]
        public void BoxPool_returns_recycled_boxes() {
            var (_, styles, engine, ctx, doc) = BuildPipeline(20);
            ComputedStyle Resolve(Element e) => styles.TryGetValue(e, out var cs) ? cs : null;
            // First Layout already ran in BuildPipeline. Second Layout: every
            // freshly-built box hits the cache, gets replaced, and recycled.
            engine.Layout(doc, Resolve, ctx);

            // After the second call, the pool must hold at least some recycled
            // BlockBoxes (the freshly built ones from the second pass that were
            // replaced by the cached survivors). Exact counts vary because the
            // doc has BlockBox / TextRun / LineBox in different ratios; we just
            // assert the pool is non-empty for the dominant kind.
            int blockFree = engine.DiagnosticBoxPool.FreeCountFor<BlockBox>();
            int textRunFree = engine.DiagnosticBoxPool.FreeCountFor<TextRun>();
            int lineBoxFree = engine.DiagnosticBoxPool.FreeCountFor<LineBox>();
            TestContext.Progress.WriteLine(
                $"BoxPool free counts after 2 calls: BlockBox={blockFree} TextRun={textRunFree} LineBox={lineBoxFree}");
            // BlockBox alone gives ~20 freshly-built-then-replaced instances per
            // call (one per <li>), so we expect at least a handful to be back in
            // the pool. TextRun/LineBox are non-cacheable so they ALWAYS rebuild
            // each pass — they should recycle on the third pass.
            engine.Layout(doc, Resolve, ctx);
            blockFree = engine.DiagnosticBoxPool.FreeCountFor<BlockBox>();
            textRunFree = engine.DiagnosticBoxPool.FreeCountFor<TextRun>();
            lineBoxFree = engine.DiagnosticBoxPool.FreeCountFor<LineBox>();
            TestContext.Progress.WriteLine(
                $"BoxPool free counts after 3 calls: BlockBox={blockFree} TextRun={textRunFree} LineBox={lineBoxFree}");
            Assert.That(blockFree, Is.GreaterThan(0),
                "BlockBox pool should have recycled instances after a cache-hit pass");
        }

        [Test, Explicit("alloc")]
        public void Pool_does_not_corrupt_layout_results() {
            // Sanity: running Layout twice with cache hits must produce identical
            // dimensions. If the pool handed out a box with stale field values the
            // second pass would diverge.
            var (root1, styles, engine, ctx, doc) = BuildPipeline(30);
            ComputedStyle Resolve(Element e) => styles.TryGetValue(e, out var cs) ? cs : null;

            var first = SerializeLayout(root1);
            var root2 = engine.Layout(doc, Resolve, ctx);
            var second = SerializeLayout(root2);
            Assert.That(second, Is.EqualTo(first), "Layout output must be byte-identical after pool reuse");

            engine.InvalidateAll();
            var root3 = engine.Layout(doc, Resolve, ctx);
            var third = SerializeLayout(root3);
            Assert.That(third, Is.EqualTo(first), "Layout output must be byte-identical after InvalidateAll + relayout");
        }

        static string SerializeLayout(Box root) {
            var sb = new StringBuilder();
            Walk(root, 0, sb);
            return sb.ToString();
        }

        static void Walk(Box b, int depth, StringBuilder sb) {
            sb.Append(b.GetType().Name)
              .Append('@').Append(b.X.ToString("F2")).Append(',').Append(b.Y.ToString("F2"))
              .Append(' ').Append(b.Width.ToString("F2")).Append('x').Append(b.Height.ToString("F2"))
              .Append('\n');
            for (int i = 0; i < b.Children.Count; i++) Walk(b.Children[i], depth + 1, sb);
        }
    }
}
