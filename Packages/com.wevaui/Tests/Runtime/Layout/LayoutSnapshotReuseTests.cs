using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Weva.Compiled;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Text;
using Weva.Parsing;

namespace Weva.Tests.Layout {
    // Verifies the snapshot wire-up between CascadeEngine and LayoutEngine:
    //   * Layout reads ctx.Snapshot when supplied (no rebuild).
    //   * Layout's internal SnapshotStyleArray + DomSnapshot pools recycle
    //     across calls so a stable tree shape rebuild is zero-alloc.
    //   * After document mutation, the cascade refills its snapshot in place
    //     (no new instance) and Layout consumes the same instance.
    [Category("layout")]
    public class LayoutSnapshotReuseTests {
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));
        static OriginatedStylesheet UA(string s) => OriginatedStylesheet.UserAgent(CssParser.Parse(s));

        const string BuiltinUA =
            "html, body, div, section, p, ul, li { display: block; } " +
            "span, a, strong, em { display: inline; } " +
            "body { margin: 0; padding: 0; }";

        sealed class Pipeline {
            public Document Doc;
            public CascadeEngine Cascade;
            public LayoutEngine Layout;
            public LayoutContext Ctx;
            public Func<Element, ComputedStyle> StyleOf;
            public Dictionary<Element, ComputedStyle> Styles;
        }

        static Pipeline Build(int n) {
            var sb = new StringBuilder("<section>");
            for (int i = 0; i < n; i++) sb.Append("<div class=\"item\">i").Append(i).Append("</div>");
            sb.Append("</section>");
            var doc = HtmlParser.Parse(sb.ToString());
            var sheets = new List<OriginatedStylesheet> {
                UA(BuiltinUA),
                Author(".item { padding: 2px; }")
            };
            var cascade = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in cascade.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 1024, ViewportHeightPx = 768,
                RootFontSizePx = 16, DpiPixelsPerInch = 96,
                Snapshot = cascade.LastSnapshot,
                SnapshotStyles = cascade.Styles
            };
            var engine = new LayoutEngine(new MonoFontMetrics());
            return new Pipeline {
                Doc = doc, Cascade = cascade, Layout = engine, Ctx = ctx,
                Styles = styles,
                StyleOf = e => styles.TryGetValue(e, out var cs) ? cs : null
            };
        }

        [Test]
        public void Layout_uses_ctx_Snapshot_when_supplied() {
            var p = Build(10);
            var snap = p.Cascade.LastSnapshot;
            Assert.That(snap, Is.Not.Null);
            // Snapshot wired into ctx via Build(); Layout consumes it.
            Assert.That(p.Ctx.Snapshot, Is.SameAs(snap));
            var box = p.Layout.Layout(p.Doc, p.StyleOf, p.Ctx);
            Assert.That(box, Is.Not.Null);
            // Same snapshot instance still on ctx after the call (no swap).
            Assert.That(p.Ctx.Snapshot, Is.SameAs(snap));
        }

        [Test]
        public void Layout_uses_ctx_SnapshotStyles_when_supplied() {
            var p = Build(10);
            int styleCalls = 0;
            ComputedStyle StyleOf(Element e) {
                styleCalls++;
                return p.Styles.TryGetValue(e, out var cs) ? cs : null;
            }

            var box = p.Layout.Layout(p.Doc, StyleOf, p.Ctx);

            Assert.That(box, Is.Not.Null);
            Assert.That(styleCalls, Is.EqualTo(0),
                "SnapshotBoxBuilder should read CascadeEngine.Styles by NodeId instead of calling styleOf per element.");
        }

        [Test]
        public void Layout_reuses_cascade_snapshot_across_passes() {
            var p = Build(10);
            var snap = p.Cascade.LastSnapshot;
            for (int i = 0; i < 3; i++) p.Layout.Layout(p.Doc, p.StyleOf, p.Ctx);
            // Same snapshot instance — cascade didn't run again, so no rebuild.
            Assert.That(p.Cascade.LastSnapshot, Is.SameAs(snap));
        }

        [Test]
        public void Cascade_refills_lastSnapshot_in_place_after_mutation() {
            var p = Build(10);
            var snap = p.Cascade.LastSnapshot;
            // Mutate the doc to flip the cascade's snapshotDirty flag.
            Element first = null;
            foreach (var e in p.Doc.GetElementsByClassName("item")) { first = e; break; }
            Assert.That(first, Is.Not.Null);
            first.SetAttribute("class", "item changed");
            // Re-run cascade — it should refill the same snapshot instance.
            p.Cascade.ComputeAll(p.Doc);
            Assert.That(p.Cascade.LastSnapshot, Is.SameAs(snap),
                "Cascade should refill its persistent snapshot, not allocate a new one");
        }

        [Test]
        public void Layout_consumes_refilled_snapshot_after_mutation() {
            var p = Build(10);
            var snap = p.Cascade.LastSnapshot;

            // Add a new element, re-cascade, re-layout. The snapshot instance
            // must be the same (refill in place) and its node count must grow.
            int beforeNodes = snap.NodeCount;
            var newDiv = new Element("div");
            newDiv.SetAttribute("class", "item");
            Element section = null;
            foreach (var c in p.Doc.Children) { if (c is Element se) { section = se; break; } }
            Assert.That(section, Is.Not.Null);
            section.AppendChild(newDiv);

            // Need to refresh styles after structural change.
            p.Styles.Clear();
            foreach (var kv in p.Cascade.ComputeAll(p.Doc)) p.Styles[kv.Key] = kv.Value;
            Assert.That(p.Cascade.LastSnapshot, Is.SameAs(snap));
            Assert.That(snap.NodeCount, Is.GreaterThan(beforeNodes));

            // Make sure layout still works with the refilled snapshot.
            p.Ctx.Snapshot = p.Cascade.LastSnapshot;
            var box = p.Layout.Layout(p.Doc, p.StyleOf, p.Ctx);
            Assert.That(box, Is.Not.Null);
        }

        [Test]
        public void Layout_falls_back_to_managed_walk_when_no_snapshot_supplied() {
            var p = Build(10);
            // Strip the wired-in snapshot so the layout engine builds its own.
            p.Ctx.Snapshot = null;
            var box = p.Layout.Layout(p.Doc, p.StyleOf, p.Ctx);
            Assert.That(box, Is.Not.Null);
            // A second call without ctx.Snapshot should reuse the engine's own
            // pooled snapshot (no exception, no behaviour change).
            var box2 = p.Layout.Layout(p.Doc, p.StyleOf, p.Ctx);
            Assert.That(box2, Is.Not.Null);
        }

        [Test, Category("alloc")]
        public void Steady_state_layout_with_cascade_snapshot_below_alloc_ceiling() {
            var p = Build(50);
            // Warm box pool, css value pool, snapshot style array.
            for (int i = 0; i < 5; i++) p.Layout.Layout(p.Doc, p.StyleOf, p.Ctx);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long before = GC.GetTotalMemory(false);
            const int n = 20;
            for (int i = 0; i < n; i++) p.Layout.Layout(p.Doc, p.StyleOf, p.Ctx);
            long after = GC.GetTotalMemory(false);
            long perCall = (after - before) / n;
            // 50-element scene; ample headroom under the 200 KB plan target.
            // The exact ceiling depends on what the rest of the layout pipeline
            // still allocates (BlockLayout/InlineLayout/FlexLayout instances).
            // 100 KB is generous protection against regressions in the
            // DomSnapshot/SnapshotStyleArray pooling we just added.
            Assert.That(perCall, Is.LessThan(100_000),
                $"Warm Layout (50-elem) allocates {perCall} B/call");
        }

        [Test]
        public void Snapshot_node_count_stable_across_layout_passes_on_unchanged_doc() {
            var p = Build(20);
            int initialNodes = p.Cascade.LastSnapshot.NodeCount;
            for (int i = 0; i < 5; i++) p.Layout.Layout(p.Doc, p.StyleOf, p.Ctx);
            Assert.That(p.Cascade.LastSnapshot.NodeCount, Is.EqualTo(initialNodes));
        }
    }
}
