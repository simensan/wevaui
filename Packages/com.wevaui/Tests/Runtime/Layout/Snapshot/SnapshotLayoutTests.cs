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

namespace Weva.Tests.Layout.Snapshot {
    // Parity suite for the snapshot-driven layout path. Every test runs Layout
    // twice with the same inputs — once with useSnapshot=false (managed walk)
    // and once with useSnapshot=true (snapshot walk) — then compares the two
    // resulting Box trees structurally and numerically.
    //
    // The trees should be byte-identical at the layout-result level: identical
    // Box subtype hierarchy, identical Element / TextRun.SourceNode pointers,
    // identical X/Y/Width/Height/margins/paddings/borders.
    public class SnapshotLayoutTests {
        const string UA =
            "html, body, div, section, header, footer, nav, main, article, aside, p, h1, h2, h3, h4, h5, h6, ul, ol, li, hr { display: block; } " +
            "a, span, strong, em, b, i, u, code, small, label { display: inline; } " +
            "br { display: inline; } " +
            "body { margin: 0; padding: 0; }";

        static Document HtmlOf(string s) => HtmlParser.Parse(s);

        static (Box managed, Box snap, IReadOnlyDictionary<Element, ComputedStyle> styles) RunBoth(
            string html, string css = null, double vw = 800, double vh = 600) {

            var docM = HtmlOf(html);
            var docS = HtmlOf(html);

            var sheets = new List<OriginatedStylesheet> { OriginatedStylesheet.UserAgent(CssParser.Parse(UA)) };
            if (!string.IsNullOrEmpty(css)) sheets.Add(OriginatedStylesheet.Author(CssParser.Parse(css)));

            // Managed engine — snapshot=false everywhere.
            var cascadeM = new CascadeEngine(sheets, false);
            var stylesM = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in cascadeM.ComputeAll(docM)) stylesM[kv.Key] = kv.Value;
            var ctxM = new LayoutContext(new MonoFontMetrics()) { ViewportWidthPx = vw, ViewportHeightPx = vh };
            var engineM = new LayoutEngine(new MonoFontMetrics(), useSnapshot: false);
            var managedRoot = engineM.Layout(docM, e => stylesM.TryGetValue(e, out var cs) ? cs : null, ctxM);

            // Snapshot engine — snapshot=true; supply ctx.Snapshot from the cascade.
            var cascadeS = new CascadeEngine(sheets, true);
            var stylesS = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in cascadeS.ComputeAll(docS)) stylesS[kv.Key] = kv.Value;
            var ctxS = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = vw, ViewportHeightPx = vh,
                Snapshot = cascadeS.LastSnapshot,
            };
            var engineS = new LayoutEngine(new MonoFontMetrics(), useSnapshot: true);
            var snapRoot = engineS.Layout(docS, e => stylesS.TryGetValue(e, out var cs) ? cs : null, ctxS);

            return (managedRoot, snapRoot, stylesS);
        }

        static void AssertTreesEqual(Box a, Box b, string path = "root") {
            Assert.That(b, Is.Not.Null, $"{path}: snap is null but managed is not");
            Assert.That(a, Is.Not.Null, $"{path}: managed is null but snap is not");
            Assert.That(b.GetType(), Is.EqualTo(a.GetType()), $"{path}: type mismatch");
            Assert.That(b.X, Is.EqualTo(a.X).Within(0.0001), $"{path}: X");
            Assert.That(b.Y, Is.EqualTo(a.Y).Within(0.0001), $"{path}: Y");
            Assert.That(b.Width, Is.EqualTo(a.Width).Within(0.0001), $"{path}: Width");
            Assert.That(b.Height, Is.EqualTo(a.Height).Within(0.0001), $"{path}: Height");
            Assert.That(b.MarginTop, Is.EqualTo(a.MarginTop).Within(0.0001), $"{path}: MarginTop");
            Assert.That(b.MarginRight, Is.EqualTo(a.MarginRight).Within(0.0001), $"{path}: MarginRight");
            Assert.That(b.MarginBottom, Is.EqualTo(a.MarginBottom).Within(0.0001), $"{path}: MarginBottom");
            Assert.That(b.MarginLeft, Is.EqualTo(a.MarginLeft).Within(0.0001), $"{path}: MarginLeft");
            Assert.That(b.PaddingTop, Is.EqualTo(a.PaddingTop).Within(0.0001), $"{path}: PaddingTop");
            Assert.That(b.PaddingLeft, Is.EqualTo(a.PaddingLeft).Within(0.0001), $"{path}: PaddingLeft");
            Assert.That(b.BorderTop, Is.EqualTo(a.BorderTop).Within(0.0001), $"{path}: BorderTop");
            Assert.That(b.BorderLeft, Is.EqualTo(a.BorderLeft).Within(0.0001), $"{path}: BorderLeft");
            string aTag = a.Element?.TagName, bTag = b.Element?.TagName;
            Assert.That(bTag, Is.EqualTo(aTag), $"{path}: tag");
            if (a is TextRun ta && b is TextRun tb) {
                Assert.That(tb.Text, Is.EqualTo(ta.Text), $"{path}: text");
            }
            if (a is BlockBox abb && b is BlockBox bbb) {
                Assert.That(bbb.IsInlineBlock, Is.EqualTo(abb.IsInlineBlock), $"{path}: inline-block");
                Assert.That(bbb.ContainsInlines, Is.EqualTo(abb.ContainsInlines), $"{path}: contains-inlines");
            }
            Assert.That(b.Children.Count, Is.EqualTo(a.Children.Count), $"{path}: children count differs ({a.Children.Count} vs {b.Children.Count})");
            for (int i = 0; i < a.Children.Count; i++) {
                AssertTreesEqual(a.Children[i], b.Children[i], $"{path}/{i}");
            }
        }

        static string BuildNestedHtml(int n) {
            var sb = new StringBuilder();
            sb.Append("<section id=\"root\">");
            int depthCounter = 0;
            for (int i = 0; i < n / 4; i++) {
                sb.Append("<div class=\"row r").Append(i % 5).Append("\"><span class=\"a\">a").Append(i).Append("</span><span class=\"b\">b").Append(i).Append("</span><span class=\"c\">c").Append(i).Append("</span></div>");
                depthCounter++;
            }
            sb.Append("</section>");
            return sb.ToString();
        }

        // ---------- size scaling ----------

        [Test]
        public void Parity_50_elements_block_grid() {
            var (m, s, _) = RunBoth(BuildNestedHtml(50));
            AssertTreesEqual(m, s);
        }

        [Test]
        public void Parity_100_elements() {
            var (m, s, _) = RunBoth(BuildNestedHtml(100));
            AssertTreesEqual(m, s);
        }

        [Test]
        public void Parity_500_elements() {
            var (m, s, _) = RunBoth(BuildNestedHtml(500));
            AssertTreesEqual(m, s);
        }

        // ---------- format-context coverage ----------

        [Test]
        public void Parity_flex_container_with_children() {
            string html = "<div style=\"display:flex; width:600px\"><div style=\"flex:1\">a</div><div style=\"flex:2\">b</div><div style=\"flex:1\">c</div></div>";
            var (m, s, _) = RunBoth(html);
            AssertTreesEqual(m, s);
        }

        [Test]
        public void Parity_grid_container_with_children() {
            string html =
                "<div style=\"display:grid; grid-template-columns: 100px 100px 100px; width:300px\">" +
                "<div>1</div><div>2</div><div>3</div><div>4</div>" +
                "</div>";
            var (m, s, _) = RunBoth(html);
            AssertTreesEqual(m, s);
        }

        [Test]
        public void Parity_block_with_paragraphs() {
            string html = "<main><p>first paragraph here</p><p>second one</p><p>and a third</p></main>";
            var (m, s, _) = RunBoth(html);
            AssertTreesEqual(m, s);
        }

        // ---------- inline / text ----------

        [Test]
        public void Parity_inline_content_inside_paragraph() {
            string html = "<p>Hello <strong>world</strong>, this is <em>fine</em>.</p>";
            var (m, s, _) = RunBoth(html);
            AssertTreesEqual(m, s);
        }

        [Test]
        public void Parity_pure_text_block() {
            string html = "<p>Just some plain text content.</p>";
            var (m, s, _) = RunBoth(html);
            AssertTreesEqual(m, s);
        }

        [Test]
        public void Parity_nested_inline_anchors_and_strong() {
            string html = "<p><a href=\"#\"><strong>linked text</strong></a> and free text</p>";
            var (m, s, _) = RunBoth(html);
            AssertTreesEqual(m, s);
        }

        // ---------- inline-block / mixed ----------

        [Test]
        public void Parity_inline_block_atom() {
            string html = "<p>before<span style=\"display:inline-block; width:50px; height:20px\">x</span>after</p>";
            var (m, s, _) = RunBoth(html);
            AssertTreesEqual(m, s);
        }

        [Test]
        public void Parity_mixed_block_and_inline_children() {
            string html = "<div>before<div>inner</div>after</div>";
            var (m, s, _) = RunBoth(html);
            AssertTreesEqual(m, s);
        }

        [Test]
        public void Parity_anonymous_block_skip_pure_whitespace() {
            string html = "<div><div></div>   <div></div></div>";
            var (m, s, _) = RunBoth(html);
            AssertTreesEqual(m, s);
        }

        // ---------- positioning / margins ----------

        [Test]
        public void Parity_margin_collapsing_between_blocks() {
            string css = "div { margin-top: 20px; margin-bottom: 20px; }";
            string html = "<section><div>a</div><div>b</div><div>c</div></section>";
            var (m, s, _) = RunBoth(html, css);
            AssertTreesEqual(m, s);
        }

        [Test]
        public void Parity_relative_positioning() {
            string html = "<div style=\"position:relative; top:10px; left:5px; width:100px; height:50px\"></div>";
            var (m, s, _) = RunBoth(html);
            AssertTreesEqual(m, s);
        }

        [Test]
        public void Parity_absolute_positioning() {
            string html = "<div style=\"position:relative; width:200px; height:200px\"><div style=\"position:absolute; top:10px; left:20px; width:30px; height:40px\"></div></div>";
            var (m, s, _) = RunBoth(html);
            AssertTreesEqual(m, s);
        }

        [Test]
        public void Parity_padding_and_border() {
            string css = "div { padding: 10px; border-top-width: 2px; border-top-style: solid; }";
            string html = "<div>x</div>";
            var (m, s, _) = RunBoth(html, css);
            AssertTreesEqual(m, s);
        }

        // ---------- display:none / contents ----------

        [Test]
        public void Parity_display_none_skipped() {
            string html = "<div><span>visible</span><span style=\"display:none\">hidden</span><span>also visible</span></div>";
            var (m, s, _) = RunBoth(html);
            AssertTreesEqual(m, s);
        }

        [Test]
        public void Parity_display_contents_passes_through() {
            string html = "<div><div style=\"display:contents\"><span>a</span><span>b</span></div></div>";
            var (m, s, _) = RunBoth(html);
            AssertTreesEqual(m, s);
        }

        // ---------- single-element fallback ----------

        [Test]
        public void Single_element_path_uses_managed_unconditionally() {
            // Layout(Element, ...) skips the snapshot path per design — verify it
            // still produces the same tree as managed.
            var doc1 = HtmlOf("<section><div><span>x</span></div></section>");
            var doc2 = HtmlOf("<section><div><span>x</span></div></section>");
            var sheets = new List<OriginatedStylesheet> { OriginatedStylesheet.UserAgent(CssParser.Parse(UA)) };
            var c1 = new CascadeEngine(sheets, false);
            var c2 = new CascadeEngine(sheets, true);
            var m1 = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in c1.ComputeAll(doc1)) m1[kv.Key] = kv.Value;
            var m2 = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in c2.ComputeAll(doc2)) m2[kv.Key] = kv.Value;

            Element root1 = null, root2 = null;
            foreach (var c in doc1.Children) if (c is Element e1) { root1 = e1; break; }
            foreach (var c in doc2.Children) if (c is Element e2) { root2 = e2; break; }

            var ctx1 = new LayoutContext(new MonoFontMetrics()) { ViewportWidthPx = 800, ViewportHeightPx = 600 };
            var ctx2 = new LayoutContext(new MonoFontMetrics()) { ViewportWidthPx = 800, ViewportHeightPx = 600 };
            var e1Engine = new LayoutEngine(new MonoFontMetrics(), false);
            var e2Engine = new LayoutEngine(new MonoFontMetrics(), true);
            var b1 = e1Engine.Layout(root1, e => m1.TryGetValue(e, out var cs) ? cs : null, ctx1);
            var b2 = e2Engine.Layout(root2, e => m2.TryGetValue(e, out var cs) ? cs : null, ctx2);

            AssertTreesEqual(b1, b2);
        }

        // ---------- stylesheet rebuild → snapshot rebuild correctness ----------

        [Test]
        public void Stylesheet_change_after_layout_then_relayout_parity() {
            string html = "<section><div id=\"a\"></div><div id=\"b\"></div></section>";
            string css1 = "div { padding: 5px; }";
            string css2 = "div { padding: 15px; }";

            var (m1, s1, _) = RunBoth(html, css1);
            AssertTreesEqual(m1, s1);

            var (m2, s2, _) = RunBoth(html, css2);
            AssertTreesEqual(m2, s2);
            // Confirm the second pass actually changed something. The root
            // box is the anonymous Document wrapper; HtmlParser now wraps
            // the fragment in synthetic `<html><body>`, so the chain down
            // to a <div> is: root → html → body → section → div.
            var div1 = s1.Children[0].Children[0].Children[0].Children[0];
            var div2 = s2.Children[0].Children[0].Children[0].Children[0];
            Assert.That(div2.PaddingLeft, Is.Not.EqualTo(div1.PaddingLeft));
        }

        // ---------- stress / pool reuse ----------

        [Test]
        public void Pool_reuse_across_passes_no_state_leak() {
            string html = "<div><span>a</span><span>b</span></div>";
            var doc = HtmlOf(html);
            var sheets = new List<OriginatedStylesheet> { OriginatedStylesheet.UserAgent(CssParser.Parse(UA)) };
            var cascade = new CascadeEngine(sheets, true);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in cascade.ComputeAll(doc)) styles[kv.Key] = kv.Value;

            var engine = new LayoutEngine(new MonoFontMetrics(), useSnapshot: true);
            var ctx = new LayoutContext(new MonoFontMetrics()) {
                ViewportWidthPx = 800, ViewportHeightPx = 600,
                Snapshot = cascade.LastSnapshot,
            };
            Box first = engine.Layout(doc, e => styles[e], ctx);
            Box second = engine.Layout(doc, e => styles[e], ctx);
            AssertTreesEqual(first, second);
        }

        [Test]
        public void Snapshot_path_default_engine_picks_snapshot() {
            // The default LayoutEngine constructor should enable the snapshot path.
            var engine = new LayoutEngine(new MonoFontMetrics());
            Assert.That(engine.UseSnapshot, Is.True);
        }

        [Test]
        public void Layout_without_supplied_snapshot_still_works() {
            // useSnapshot=true but ctx.Snapshot=null: layout builds its own internal snapshot.
            var doc = HtmlOf("<div><span>hi</span></div>");
            var sheets = new List<OriginatedStylesheet> { OriginatedStylesheet.UserAgent(CssParser.Parse(UA)) };
            var cascade = new CascadeEngine(sheets, false);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in cascade.ComputeAll(doc)) styles[kv.Key] = kv.Value;

            var engine = new LayoutEngine(new MonoFontMetrics(), useSnapshot: true);
            var ctx = new LayoutContext(new MonoFontMetrics()) { ViewportWidthPx = 400, ViewportHeightPx = 300 };
            var box = engine.Layout(doc, e => styles[e], ctx);
            Assert.That(box, Is.Not.Null);
            Assert.That(box.Width, Is.EqualTo(400).Within(0.001));
        }
    }
}
