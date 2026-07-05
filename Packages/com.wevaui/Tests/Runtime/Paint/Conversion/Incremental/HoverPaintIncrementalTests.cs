using System;
using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Text;
using Weva.Paint;
using Weva.Paint.Conversion;
using Weva.Reactive;

namespace Weva.Tests.Paint.Conversion.Incremental {
    // Integration tests for the hover toggle path through the full pipeline:
    // cascade → layout → paint converter. The forms scene has no scroll state,
    // so every cacheable box goes through the box-local-coords cache, and only
    // the hovered box's cache should miss when its pseudo-class flips.
    public class HoverPaintIncrementalTests {
        sealed class HoverState : IElementStateProvider {
            Element hovered;
            long version;
            public void SetHover(Element e) {
                if (!ReferenceEquals(hovered, e)) { hovered = e; version++; }
            }
            public ElementState GetState(Element e) {
                return ReferenceEquals(e, hovered) ? ElementState.Hover : ElementState.None;
            }
            public long Version => version;
        }

        sealed class Setup {
            public Document Doc;
            public CascadeEngine Cascade;
            public LayoutEngine Layout;
            public BoxToPaintConverter Painter;
            public LayoutContext Context;
            public Element Button;
            public HoverState State;
            public Box LastRoot;
        }

        static Setup BuildFormsScene(int formCount) {
            string ua = "html, body, div, section, form, fieldset, label { display: block; } "
                      + "a, span, button, input { display: inline; } "
                      + "body { margin: 0; padding: 0; }";
            string author = ".panel { padding: 4px; margin: 2px; }"
                          + ".form-row { padding: 4px; margin-bottom: 4px; }"
                          + ".form-row label { font-weight: bold; padding-right: 4px; }"
                          + "button { padding: 4px 8px; background-color: lightgray; }"
                          + "button:hover { background-color: cyan; }";
            var doc = Weva.Parsing.HtmlParser.Parse(BuildFormsHtml(formCount));
            var sheets = new List<Weva.Css.Cascade.OriginatedStylesheet> {
                Weva.Css.Cascade.OriginatedStylesheet.UserAgent(Weva.Css.CssParser.Parse(ua)),
                Weva.Css.Cascade.OriginatedStylesheet.Author(Weva.Css.CssParser.Parse(author)),
            };
            var cascade = new CascadeEngine(sheets, true);
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 1024,
                ViewportHeightPx = 768,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96,
            };
            var layout = new LayoutEngine(new MonoFontMetrics(), true);
            var painter = new BoxToPaintConverter();

            Element button = null;
            foreach (var e in AllElements(doc)) {
                if (e.TagName == "button") { button = e; break; }
            }
            return new Setup {
                Doc = doc, Cascade = cascade, Layout = layout, Painter = painter,
                Context = ctx, Button = button, State = new HoverState(),
            };
        }

        static string BuildFormsHtml(int formCount) {
            var sb = new System.Text.StringBuilder();
            sb.Append("<section class=\"container\">");
            for (int f = 0; f < formCount; f++) {
                sb.Append("<form class=\"panel\">");
                for (int r = 0; r < 6; r++) {
                    sb.Append("<div class=\"form-row\"><label>L").Append(f).Append("R").Append(r).Append("</label>");
                    if (r == 5 && f == 0) sb.Append("<button>OK</button>");
                    else sb.Append("<input type=\"text\" />");
                    sb.Append("</div>");
                }
                sb.Append("</form>");
            }
            sb.Append("</section>");
            return sb.ToString();
        }

        static IEnumerable<Element> AllElements(Node n) {
            if (n is Element e) yield return e;
            foreach (var c in n.Children) {
                foreach (var d in AllElements(c)) yield return d;
            }
        }

        static int CountCacheableBoxes(Box root) {
            int n = 0;
            CountWalk(root, ref n);
            return n;
        }

        static void CountWalk(Box b, ref int n) {
            if (b == null) return;
            if (!(b is TextRun)) n++;
            for (int i = 0; i < b.Children.Count; i++) CountWalk(b.Children[i], ref n);
        }

        static void RunFrame(Setup s) {
            var styles = s.Cascade.ComputeAll(s.Doc, s.State);
            s.LastRoot = s.Layout.Layout(s.Doc, e => styles.TryGetValue(e, out var cs) ? cs : null, s.Context);
            s.Painter.Convert(s.LastRoot);
        }

        [Test]
        public void Hover_toggle_misses_at_most_a_handful_of_boxes() {
            var s = BuildFormsScene(20);
            Assert.That(s.Button, Is.Not.Null);

            // Warm up the cascade + layout + paint caches across a few frames.
            for (int i = 0; i < 5; i++) {
                s.State.SetHover((i & 1) == 0 ? s.Button : null);
                RunFrame(s);
            }

            int totalBoxes = CountCacheableBoxes(s.LastRoot);
            Assert.That(totalBoxes, Is.GreaterThan(20),
                "Forms scene should produce a meaningful number of cacheable boxes.");

            // Single hover toggle: invert state, run one frame, count misses.
            s.State.SetHover(s.State.Version % 2 == 0 ? s.Button : null);
            s.Painter.ResetCacheStats();
            RunFrame(s);
            // The cascade may produce fresh ComputedStyles for descendants of
            // the hovered element if any author rule depends on the parent's
            // state (none here), or for ancestors traversed by the cascade. In
            // the worst case, we expect misses on the order of 5 — orders of
            // magnitude below the total box count.
            Assert.That(s.Painter.CacheMisses, Is.LessThanOrEqualTo(20),
                "Hover toggle must invalidate only a small slice of the tree, not all of it.");
            Assert.That(s.Painter.CacheHits, Is.GreaterThan(s.Painter.CacheMisses * 5),
                "Hits should dominate misses by a comfortable margin (>5x).");
        }

        [Test]
        public void Hover_toggle_invalidates_only_a_small_subset() {
            var s = BuildFormsScene(10);

            // Warm.
            for (int i = 0; i < 5; i++) {
                s.State.SetHover((i & 1) == 0 ? s.Button : null);
                RunFrame(s);
            }

            int totalBoxes = CountCacheableBoxes(s.LastRoot);
            // Toggle hover and measure misses on the painter side only.
            s.State.SetHover(s.Button);
            s.Painter.ResetCacheStats();
            RunFrame(s);

            // Misses must be a small fraction of the total tree — the whole
            // point of the box-local-coords cache is that a single :hover flip
            // doesn't re-emit a tree-sized command set.
            Assert.That(s.Painter.CacheMisses, Is.LessThan(totalBoxes / 2),
                $"Hover toggle invalidated {s.Painter.CacheMisses}/{totalBoxes} boxes; expected <50%.");
        }

        [Test]
        public void Hover_toggle_paint_walk_dominates_with_hits() {
            var s = BuildFormsScene(30);

            for (int i = 0; i < 5; i++) {
                s.State.SetHover((i & 1) == 0 ? s.Button : null);
                RunFrame(s);
            }

            // 50 hover toggles. Across all of them the steady-state hit/miss
            // ratio must remain hits-dominated (we accept some misses around
            // ancestors of the hovered element).
            long totalHits = 0, totalMisses = 0;
            for (int i = 0; i < 50; i++) {
                s.State.SetHover((i & 1) == 0 ? s.Button : null);
                s.Painter.ResetCacheStats();
                RunFrame(s);
                totalHits += s.Painter.CacheHits;
                totalMisses += s.Painter.CacheMisses;
            }
            Assert.That(totalHits, Is.GreaterThan(totalMisses * 5),
                "Across 50 hover toggles, hits must outweigh misses by >5x.");
        }

        [Test]
        public void Stable_box_tree_re_converts_are_pure_hits() {
            // Run full cascade+layout once to build a real-shaped tree, then
            // re-Convert the same Box root multiple times without re-laying-out.
            // No state change → no box.Version bumps → 100% hits on the painter.
            var s = BuildFormsScene(15);
            var styles = s.Cascade.ComputeAll(s.Doc, s.State);
            var root = s.Layout.Layout(s.Doc, e => styles.TryGetValue(e, out var cs) ? cs : null, s.Context);
            // Warm the converter.
            for (int i = 0; i < 3; i++) s.Painter.Convert(root);
            s.Painter.ResetCacheStats();
            for (int i = 0; i < 5; i++) s.Painter.Convert(root);
            Assert.That(s.Painter.CacheMisses, Is.EqualTo(0),
                "Stable Box tree: paint cache must be 100% hits across all re-Converts.");
            Assert.That(s.Painter.CacheHits, Is.GreaterThan(0));
        }

        static Box FindBox(Box root, Element e) {
            if (root == null || e == null) return null;
            if (root.Element == e) return root;
            for (int i = 0; i < root.Children.Count; i++) {
                var f = FindBox(root.Children[i], e);
                if (f != null) return f;
            }
            return null;
        }
    }
}
