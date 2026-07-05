using System.Collections.Generic;
using Weva.Binding;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Layout.Diagnostics;
using Weva.Layout.Text;
using Weva.Parsing;

namespace Weva.Tests.Layout {
    internal static class LayoutTestHelpers {
        // Enable invariant checks for all Build() calls in this test assembly.
        static LayoutTestHelpers() { LayoutInvariants.Enabled = true; }
        public static Document Html(string s) => HtmlParser.Parse(s);
        public static Stylesheet ParseCss(string s) => CssParser.Parse(s);
        public static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(ParseCss(s));
        public static OriginatedStylesheet UA(string s) => OriginatedStylesheet.UserAgent(ParseCss(s));

        public static (Box root, Dictionary<Element, ComputedStyle> styles) BuildBoxesOnly(
            string html,
            string css = null
        ) {
            var doc = Html(html);
            var sheets = new List<OriginatedStylesheet>();
            sheets.Add(UA(BuiltinUserAgent));
            if (!string.IsNullOrEmpty(css)) sheets.Add(Author(css));
            var engine = new CascadeEngine(sheets);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            var bb = new BoxBuilder(e => styles.TryGetValue(e, out var cs) ? cs : null);
            return (bb.BuildDocument(doc), styles);
        }

        public static (Box root, Dictionary<Element, ComputedStyle> styles, LayoutContext ctx) Build(
            string html,
            string css = null,
            double viewportWidth = 800,
            double viewportHeight = 600
        ) {
            var doc = Html(html);
            var sheets = new List<OriginatedStylesheet>();
            sheets.Add(UA(BuiltinUserAgent));
            if (!string.IsNullOrEmpty(css)) sheets.Add(Author(css));
            var engine = new CascadeEngine(sheets, true);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;

            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = viewportWidth,
                ViewportHeightPx = viewportHeight,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96,
                Snapshot = engine.LastSnapshot,
                SnapshotStyles = engine.Styles
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var root = le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
            LayoutInvariants.Check(root);
            return (root, styles, ctx);
        }

        // Variant of Build that uses JitteredFontMetrics instead of MonoFontMetrics.
        // Opt-in: existing tests are unaffected. Use this to exercise topologies
        // that are fragile when glyph widths are not uniformly 0.5em.
        public static (Box root, Dictionary<Element, ComputedStyle> styles, LayoutContext ctx) BuildJittered(
            string html,
            string css = null,
            double viewportWidth = 800,
            double viewportHeight = 600
        ) {
            var doc = Html(html);
            var sheets = new List<OriginatedStylesheet>();
            sheets.Add(UA(BuiltinUserAgent));
            if (!string.IsNullOrEmpty(css)) sheets.Add(Author(css));
            var engine = new CascadeEngine(sheets, true);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;

            var fm = new JitteredFontMetrics();
            var ctx = new LayoutContext(fm) {
                ViewportWidthPx = viewportWidth,
                ViewportHeightPx = viewportHeight,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96,
                Snapshot = engine.LastSnapshot,
                SnapshotStyles = engine.Styles
            };
            var le = new LayoutEngine(fm);
            var root = le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
            LayoutInvariants.Check(root);
            return (root, styles, ctx);
        }

        public static (Box root, Dictionary<Element, ComputedStyle> styles, LayoutContext ctx) BuildWithBindings(
            string html,
            string css,
            object controller,
            double viewportWidth = 800,
            double viewportHeight = 600
        ) {
            var doc = Html(html);
            var bindings = new BindingSet();
            BindingScanner.ScanInto(doc, controller, bindings, null);
            bindings.Update(controller);

            var sheets = new List<OriginatedStylesheet>();
            sheets.Add(UA(BuiltinUserAgent));
            if (!string.IsNullOrEmpty(css)) sheets.Add(Author(css));
            var engine = new CascadeEngine(sheets, true);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in engine.ComputeAll(doc)) styles[kv.Key] = kv.Value;

            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = viewportWidth,
                ViewportHeightPx = viewportHeight,
                RootFontSizePx = 16,
                DpiPixelsPerInch = 96,
                Snapshot = engine.LastSnapshot,
                SnapshotStyles = engine.Styles
            };
            var le = new LayoutEngine(new MonoFontMetrics());
            var root = le.Layout(doc, e => styles.TryGetValue(e, out var cs) ? cs : null, ctx);
            return (root, styles, ctx);
        }

        public static Element FindByTag(Node n, string tag) {
            if (n is Element e && e.TagName == tag) return e;
            foreach (var c in n.Children) {
                var f = FindByTag(c, tag);
                if (f != null) return f;
            }
            return null;
        }

        public static Box FindBoxFor(Box root, Element target) {
            if (root.Element == target) return root;
            foreach (var c in root.Children) {
                var f = FindBoxFor(c, target);
                if (f != null) return f;
            }
            return null;
        }

        // HtmlParser now wraps fragments in synthetic `<html><body>` so the
        // layout-root box contains a single `<html>` box. Returns the
        // box where author content lives — i.e. the `<body>` if it exists,
        // else the layout-root itself for backwards compatibility with
        // documents that don't go through the wrapper synthesis path.
        public static Box ContentRoot(Box layoutRoot) {
            if (layoutRoot == null) return null;
            Box html = null;
            foreach (var c in layoutRoot.Children) {
                if (c.Element != null && c.Element.TagName == "html") { html = c; break; }
            }
            if (html == null) return layoutRoot;
            foreach (var c in html.Children) {
                if (c.Element != null && c.Element.TagName == "body") return c;
            }
            return html;
        }

        // Absolute X/Y of a box by summing offsets up to the layout root.
        // Useful for tests that pin positions against the page origin (the
        // wrapper synthesis can move margins onto the parent <body> via
        // margin-collapsing, so per-parent X/Y alone no longer matches the
        // pre-wrapper position).
        public static (double X, double Y) AbsoluteOrigin(Box b) {
            double x = 0, y = 0;
            for (var n = b; n != null; n = n.Parent) {
                x += n.X;
                y += n.Y;
            }
            return (x, y);
        }

        public static IEnumerable<Box> AllBoxes(Box root) {
            yield return root;
            foreach (var c in root.Children) {
                foreach (var d in AllBoxes(c)) yield return d;
            }
        }

        public static IEnumerable<TextRun> AllTextRuns(Box root) {
            foreach (var b in AllBoxes(root)) {
                if (b is TextRun tr) yield return tr;
            }
        }

        public static IEnumerable<LineBox> AllLineBoxes(Box root) {
            foreach (var b in AllBoxes(root)) {
                if (b is LineBox lb) yield return lb;
            }
        }

        // Minimal user-agent stylesheet used by layout tests so common block-level tags
        // get display:block and don't accidentally inherit the inline initial value.
        // NOTE: li uses display:list-item (CSS Lists L3 §2) so that MaybeInjectListMarker
        // fires on display-gate rather than tag-gate. The block-level routing in
        // AppendNodeAsBlockChild treats list-item identically to block for outer layout.
        public const string BuiltinUserAgent = @"
            html, body, div, section, header, footer, nav, main, article, aside, p, h1, h2, h3, h4, h5, h6, ul, ol, hr { display: block; }
            li { display: list-item; }
            a, span, strong, em, b, i, u, code, small, label { display: inline; }
            br { display: inline; }
            body { margin: 0; padding: 0; }
            template, link, meta, head, title, script, style { display: none; }
            [hidden] { display: none; }
        ";
    }
}
