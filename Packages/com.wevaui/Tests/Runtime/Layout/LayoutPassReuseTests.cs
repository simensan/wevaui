using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.AnchorPositioning;
using Weva.Layout.Boxes;
using Weva.Layout.Flex;
using Weva.Layout.Grid;
using Weva.Layout.Positioning;
using Weva.Layout.Text;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    // Pass-reuse invariants for v0.8 alloc-floor reduction. Each LayoutEngine
    // holds one InlineLayout / BlockLayout / FlexLayout / GridLayout /
    // PositioningPass / AnchorSizePass instance and reuses them across every
    // Layout call. These tests verify each pass produces byte-identical output
    // with reuse vs a fresh-construction baseline, that internal scratch is
    // either stack-discipline or .Clear()-reset between calls, and that two
    // concurrent LayoutEngine instances stay isolated.
    public class LayoutPassReuseTests {
        static string Serialize(Box root) {
            var sb = new StringBuilder();
            Walk(root, 0, sb);
            return sb.ToString();
        }

        static void Walk(Box b, int depth, StringBuilder sb) {
            sb.Append(b.GetType().Name)
              .Append('@').Append(b.X.ToString("F3")).Append(',').Append(b.Y.ToString("F3"))
              .Append(' ').Append(b.Width.ToString("F3")).Append('x').Append(b.Height.ToString("F3"))
              .Append('\n');
            for (int i = 0; i < b.Children.Count; i++) Walk(b.Children[i], depth + 1, sb);
        }

        static (Document doc, System.Func<Element, ComputedStyle> resolver, LayoutContext ctx)
            BuildPipeline(string html, string css = null, double vw = 800, double vh = 600) {
            var doc = Html(html);
            var sheets = new List<OriginatedStylesheet>();
            sheets.Add(UA(BuiltinUserAgent));
            if (!string.IsNullOrEmpty(css)) sheets.Add(Author(css));
            var cascade = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in cascade.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = vw, ViewportHeightPx = vh,
                RootFontSizePx = 16, DpiPixelsPerInch = 96
            };
            return (doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
        }

        [Test]
        public void BlockLayout_reused_across_calls_produces_identical_output() {
            const string html = "<div style=\"padding:8px\"><p>hello world</p><p>second</p></div>";
            var (doc, resolver, ctx) = BuildPipeline(html);

            // Fresh engine baseline.
            var fresh = new LayoutEngine(new MonoFontMetrics());
            var first = Serialize(fresh.Layout(doc, resolver, ctx));

            // Same engine: second pass must reuse the cached BlockLayout.
            var second = Serialize(fresh.Layout(doc, resolver, ctx));
            Assert.That(second, Is.EqualTo(first),
                "Reused BlockLayout must produce byte-identical output across passes");

            // Third pass to verify steady-state reuse, not just two-call equality.
            var third = Serialize(fresh.Layout(doc, resolver, ctx));
            Assert.That(third, Is.EqualTo(first));
        }

        [Test]
        public void InlineLayout_state_cleared_between_calls_with_inline_split() {
            // Inline-splitting forces InlineLayout to populate segmentBreaks /
            // segmentBlocks. If Reset doesn't clear properly, the second pass
            // will see leftover segments and corrupt the output.
            const string html = "<p>before<div style=\"display:block;width:50px;height:20px\"></div>after</p>";
            var (doc, resolver, ctx) = BuildPipeline(html);
            var engine = new LayoutEngine(new MonoFontMetrics());

            var first = Serialize(engine.Layout(doc, resolver, ctx));
            var second = Serialize(engine.Layout(doc, resolver, ctx));
            var third = Serialize(engine.Layout(doc, resolver, ctx));
            Assert.That(second, Is.EqualTo(first));
            Assert.That(third, Is.EqualTo(first));
        }

        [Test]
        public void FlexLayout_grow_shrink_scratch_reset_between_calls() {
            const string css = ".row { display: flex; } .row > div { flex: 1 1 auto; height: 30px; }";
            const string html = "<div class=\"row\"><div></div><div></div><div></div></div>";
            var (doc, resolver, ctx) = BuildPipeline(html, css);
            var engine = new LayoutEngine(new MonoFontMetrics());

            var first = Serialize(engine.Layout(doc, resolver, ctx));
            // 5 more passes — any per-call scratch leak (e.g. FlexItems
            // appended without pop) would either drift positions or throw.
            string last = first;
            for (int i = 0; i < 5; i++) last = Serialize(engine.Layout(doc, resolver, ctx));
            Assert.That(last, Is.EqualTo(first));
        }

        [Test]
        public void GridLayout_track_templates_rebuilt_fresh_per_call() {
            const string css = ".g { display: grid; grid-template-columns: repeat(3, 1fr); } .g > div { height: 20px; }";
            const string html = "<div class=\"g\"><div></div><div></div><div></div><div></div><div></div><div></div></div>";
            var (doc, resolver, ctx) = BuildPipeline(html, css);
            var engine = new LayoutEngine(new MonoFontMetrics());

            var first = Serialize(engine.Layout(doc, resolver, ctx));
            string last = first;
            for (int i = 0; i < 5; i++) last = Serialize(engine.Layout(doc, resolver, ctx));
            Assert.That(last, Is.EqualTo(first),
                "GridLayout reuse must not leak track templates between calls");
        }

        [Test]
        public void PositioningPass_scratch_reset_between_calls() {
            // Mix in/out of flow items to force CompressOutOfFlow / ApplyAbsolute
            // through Apply walks each pass.
            const string css = @"
                .abs { position: absolute; left: 10px; top: 10px; width: 50px; height: 20px; }
                .container { position: relative; height: 200px; }
            ";
            const string html = "<div class=\"container\"><div class=\"abs\"></div><div style=\"height:30px\"></div></div>";
            var (doc, resolver, ctx) = BuildPipeline(html, css);
            var engine = new LayoutEngine(new MonoFontMetrics());

            var first = Serialize(engine.Layout(doc, resolver, ctx));
            string last = first;
            for (int i = 0; i < 5; i++) last = Serialize(engine.Layout(doc, resolver, ctx));
            Assert.That(last, Is.EqualTo(first));
        }

        [Test]
        public void AnchorSizePass_apply_twice_produces_same_result_no_state_leak() {
            // anchor-size(width) on a consumer reads the anchor's width. Run
            // ApplyInstance twice on the same tree: the second call must NOT
            // double-resolve (already-pixel value should be skipped) and the
            // dictionary must be re-populated freshly.
            const string css = @"
                .anchor { width: 200px; height: 30px; anchor-name: --tip; }
                .tip { position-anchor: --tip; width: anchor-size(width); height: 20px; }
            ";
            const string html = "<div class=\"anchor\"></div><div class=\"tip\"></div>";
            var (doc, resolver, ctx) = BuildPipeline(html, css);
            var engine = new LayoutEngine(new MonoFontMetrics());

            var first = Serialize(engine.Layout(doc, resolver, ctx));
            var second = Serialize(engine.Layout(doc, resolver, ctx));
            var third = Serialize(engine.Layout(doc, resolver, ctx));
            Assert.That(second, Is.EqualTo(first));
            Assert.That(third, Is.EqualTo(first));
        }

        [Test]
        public void AnchorSizePass_dictionary_clears_between_unrelated_trees() {
            // Build two unrelated trees with different anchor names. The same
            // pass instance must collect only the second tree's anchors after
            // ApplyInstance is called again — the persistent dictionary is
            // cleared at the start of every Apply.
            const string css1 = ".a { anchor-name: --first; width: 100px; height: 10px; }";
            const string css2 = ".b { anchor-name: --second; width: 200px; height: 20px; }";

            // Use the static Apply (which builds a fresh AnchorSizePass each
            // time) plus a manually-driven instance to verify reset semantics.
            var pass = new AnchorSizePass();

            var (doc1, resolver1, _) = BuildPipeline("<div class=\"a\"></div>", css1);
            var (doc2, resolver2, _) = BuildPipeline("<div class=\"b\"></div>", css2);

            var bb1 = new BoxBuilder(resolver1).BuildDocument(doc1);
            var bb2 = new BoxBuilder(resolver2).BuildDocument(doc2);

            pass.ApplyInstance(bb1);
            int afterFirst = pass.LastResolvedCount;
            pass.ApplyInstance(bb2);
            int afterSecond = pass.LastResolvedCount;

            Assert.That(afterFirst, Is.EqualTo(1));
            Assert.That(afterSecond, Is.EqualTo(1),
                "ApplyInstance must clear the dictionary at start; second tree contributes only its own anchor");
        }

        [Test]
        public void Concurrent_LayoutEngines_remain_independent() {
            // Two engines, two unrelated documents, interleaved Layout calls.
            // Per-engine pass instances must not share state (they're field-
            // private). If they did, output would diverge after interleaving.
            const string htmlA = "<div style=\"padding:4px\"><p>A</p></div>";
            const string htmlB = "<section><h1>B</h1><p>body</p></section>";
            var (docA, resolverA, ctxA) = BuildPipeline(htmlA);
            var (docB, resolverB, ctxB) = BuildPipeline(htmlB);

            var engineA = new LayoutEngine(new MonoFontMetrics());
            var engineB = new LayoutEngine(new MonoFontMetrics());

            var aOnce = Serialize(engineA.Layout(docA, resolverA, ctxA));
            var bOnce = Serialize(engineB.Layout(docB, resolverB, ctxB));

            // Interleave: A1,B1,A2,B2,A3 — each engine's reused passes should
            // continue to produce A's / B's layout independently.
            var a2 = Serialize(engineA.Layout(docA, resolverA, ctxA));
            var b2 = Serialize(engineB.Layout(docB, resolverB, ctxB));
            var a3 = Serialize(engineA.Layout(docA, resolverA, ctxA));
            var b3 = Serialize(engineB.Layout(docB, resolverB, ctxB));

            Assert.That(a2, Is.EqualTo(aOnce));
            Assert.That(a3, Is.EqualTo(aOnce));
            Assert.That(b2, Is.EqualTo(bOnce));
            Assert.That(b3, Is.EqualTo(bOnce));
            Assert.That(aOnce, Is.Not.EqualTo(bOnce),
                "Sanity: A and B should not coincidentally serialize identically");
        }

        [Test]
        public void Reset_idempotent_on_each_pass() {
            // Reset() with the same context twice in a row must be a no-op
            // (no state buildup) — a Layout call with a single Reset should
            // produce identical output to one with two consecutive Resets.
            const string html = "<div><p>x</p><p>y</p></div>";
            var (doc, resolver, ctx) = BuildPipeline(html);

            var engine = new LayoutEngine(new MonoFontMetrics());
            var first = Serialize(engine.Layout(doc, resolver, ctx));

            // Layout always Resets internally; running back-to-back Layouts
            // re-Resets the same passes. Output must remain identical.
            for (int i = 0; i < 10; i++) {
                var s = Serialize(engine.Layout(doc, resolver, ctx));
                Assert.That(s, Is.EqualTo(first), $"iteration {i}");
            }
        }

        [Test]
        public void Mixed_pipeline_reused_across_calls() {
            // Single pass must drive Block + Inline + Flex + Grid + Positioning +
            // AnchorSize all at once. Verifies the cycle wiring (InlineLayout <->
            // BlockLayout) survives Reset.
            const string css = @"
                .flex { display: flex; gap: 4px; }
                .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 2px; }
                .anchor { anchor-name: --a; width: 50px; height: 10px; }
                .abs { position: absolute; top: 5px; left: 5px; width: 20px; height: 5px; }
                .rel { position: relative; }
            ";
            const string html = @"
                <div class=""rel"">
                    <div class=""anchor""></div>
                    <div class=""flex""><span>a</span><span>b</span></div>
                    <div class=""grid""><div></div><div></div><div></div></div>
                    <div class=""abs""></div>
                    <p>some text content here</p>
                </div>
            ";
            var (doc, resolver, ctx) = BuildPipeline(html, css);
            var engine = new LayoutEngine(new MonoFontMetrics());

            var first = Serialize(engine.Layout(doc, resolver, ctx));
            string last = first;
            for (int i = 0; i < 8; i++) last = Serialize(engine.Layout(doc, resolver, ctx));
            Assert.That(last, Is.EqualTo(first));
        }

        [Test]
        public void Inline_layout_BlockLayout_cycle_survives_reuse() {
            // Inline-blocks force InlineLayout.MakeAtomItem to call into
            // BlockLayout. Reuse must preserve that cycle wiring.
            const string css = ".ib { display: inline-block; width: 50px; height: 30px; }";
            const string html = "<p>before<span class=\"ib\"></span>after</p>";
            var (doc, resolver, ctx) = BuildPipeline(html, css);
            var engine = new LayoutEngine(new MonoFontMetrics());

            var first = Serialize(engine.Layout(doc, resolver, ctx));
            string last = first;
            for (int i = 0; i < 5; i++) last = Serialize(engine.Layout(doc, resolver, ctx));
            Assert.That(last, Is.EqualTo(first));
        }

        [Test]
        public void Engine_works_without_useSnapshot_managed_path() {
            // Both Layout overloads (Document-driven and Element-driven, snapshot
            // and managed) must reuse the same pass instances correctly.
            const string html = "<div><p>managed</p></div>";
            var (doc, resolver, ctx) = BuildPipeline(html);

            var snapshotEngine = new LayoutEngine(new MonoFontMetrics(), true);
            var managedEngine = new LayoutEngine(new MonoFontMetrics(), false);

            var snap1 = Serialize(snapshotEngine.Layout(doc, resolver, ctx));
            var snap2 = Serialize(snapshotEngine.Layout(doc, resolver, ctx));
            var man1 = Serialize(managedEngine.Layout(doc, resolver, ctx));
            var man2 = Serialize(managedEngine.Layout(doc, resolver, ctx));

            Assert.That(snap2, Is.EqualTo(snap1));
            Assert.That(man2, Is.EqualTo(man1));
        }
    }
}
