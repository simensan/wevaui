using System.Collections.Generic;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Text;
using Weva.Parsing;

// CssParser lives in Weva.Css, HtmlParser in Weva.Parsing.

namespace Weva.Tests.Layout.Incremental {
    internal static class IncrementalLayoutTestHelpers {
        public const string BuiltinUserAgent = @"
            html, body, div, section, header, footer, nav, main, article, aside, p, h1, h2, h3, h4, h5, h6, ul, ol, li, hr { display: block; }
            a, span, strong, em, b, i, u, code, small, label { display: inline; }
            br { display: inline; }
            body { margin: 0; padding: 0; }
        ";

        public sealed class Harness {
            public Document Doc;
            public CascadeEngine Cascade;
            public LayoutEngine Engine;
            public LayoutContext Ctx;
            public Dictionary<Element, ComputedStyle> Styles = new();

            public ComputedStyle StyleOf(Element e) =>
                Styles.TryGetValue(e, out var cs) ? cs : null;

            public void Recompute() {
                Styles.Clear();
                foreach (var kv in Cascade.ComputeAll(Doc)) Styles[kv.Key] = kv.Value;
            }

            public Weva.Layout.Boxes.Box Run() {
                Recompute();
                return Engine.Layout(Doc, StyleOf, Ctx);
            }
        }

        public static Harness Build(string html, string css = null, double viewportWidth = 800, double viewportHeight = 600) {
            var doc = HtmlParser.Parse(html);
            var sheets = new List<OriginatedStylesheet> {
                OriginatedStylesheet.UserAgent(CssParser.Parse(BuiltinUserAgent))
            };
            if (!string.IsNullOrEmpty(css)) {
                sheets.Add(OriginatedStylesheet.Author(CssParser.Parse(css)));
            }
            var cascade = new CascadeEngine(sheets);
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = viewportWidth,
                ViewportHeightPx = viewportHeight,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96
            };
            var engine = new LayoutEngine(new MonoFontMetrics());
            var harness = new Harness {
                Doc = doc,
                Cascade = cascade,
                Engine = engine,
                Ctx = ctx
            };
            harness.Recompute();
            return harness;
        }

        public static IEnumerable<Element> AllElements(Node n) {
            if (n is Element e) yield return e;
            foreach (var c in n.Children) {
                foreach (var d in AllElements(c)) yield return d;
            }
        }

        public static int CountElements(Document doc) {
            int n = 0;
            foreach (var _ in AllElements(doc)) n++;
            return n;
        }

        public static Weva.Layout.Boxes.Box FindBoxFor(Weva.Layout.Boxes.Box root, Element target) {
            if (root.Element == target) return root;
            foreach (var c in root.Children) {
                var f = FindBoxFor(c, target);
                if (f != null) return f;
            }
            return null;
        }
    }
}
