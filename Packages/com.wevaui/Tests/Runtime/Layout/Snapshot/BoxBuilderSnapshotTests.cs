using System.Collections.Generic;
using NUnit.Framework;
using Weva.Compiled;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Dom;
using Weva.Layout;
using Weva.Layout.Boxes;
using Weva.Parsing;

namespace Weva.Tests.Layout.Snapshot {
    // Direct tests of SnapshotBoxBuilder. These bypass LayoutEngine entirely and
    // call BuildFromSnapshot on a freshly-built DomSnapshot so the box-tree
    // shape can be inspected before any layout passes touch it.
    public class BoxBuilderSnapshotTests {
        const string UA =
            "html, body, div, section, header, footer, p, ul, li { display: block; } " +
            "a, span, strong, em, b, i, code, label { display: inline; }";

        static Document HtmlOf(string s) => HtmlParser.Parse(s);

        static (Document doc, DomSnapshot snap, Dictionary<Element, ComputedStyle> styles) BuildSnap(string html, string css = null) {
            var doc = HtmlOf(html);
            var sheets = new List<OriginatedStylesheet> { OriginatedStylesheet.UserAgent(CssParser.Parse(UA)) };
            if (!string.IsNullOrEmpty(css)) sheets.Add(OriginatedStylesheet.Author(CssParser.Parse(css)));
            var cascade = new CascadeEngine(sheets, true);
            var styles = new Dictionary<Element, ComputedStyle>();
            foreach (var kv in cascade.ComputeAll(doc)) styles[kv.Key] = kv.Value;
            return (doc, cascade.LastSnapshot, styles);
        }

        static Box BuildBoxes(DomSnapshot snap, Dictionary<Element, ComputedStyle> styles) {
            var arr = SnapshotStyleArray.FromMap(snap, styles);
            var b = new SnapshotBoxBuilder(arr.At);
            return b.BuildFromSnapshot(snap);
        }

        // HtmlParser wraps fragments in synthetic `<html><body>` so the
        // layout-root box has a single `<html>` child. Return the `<body>`
        // box (or the root if no wrapper was synthesised) so per-test
        // navigation operates on the author's content level.
        static Box ContentRoot(Box layoutRoot) {
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

        [Test]
        public void Empty_document_yields_root_with_no_children() {
            var doc = new Document();
            var snap = DomSnapshot.Build(doc, new SymbolTable());
            var arr = SnapshotStyleArray.FromMap(snap, new Dictionary<Element, ComputedStyle>());
            var b = new SnapshotBoxBuilder(arr.At);
            var root = b.BuildFromSnapshot(snap);
            Assert.That(root, Is.Not.Null);
            Assert.That(root.Children.Count, Is.EqualTo(0));
        }

        [Test]
        public void Single_block_element_yields_one_block_box() {
            var (_, snap, styles) = BuildSnap("<div></div>");
            var root = BuildBoxes(snap, styles);
            var body = ContentRoot(root);
            Assert.That(body.Children.Count, Is.EqualTo(1));
            Assert.That(body.Children[0], Is.InstanceOf<BlockBox>());
            Assert.That(((BlockBox)body.Children[0]).Element.TagName, Is.EqualTo("div"));
        }

        [Test]
        public void Nested_elements_produce_nested_box_tree() {
            var (_, snap, styles) = BuildSnap("<div><div></div></div>");
            var root = BuildBoxes(snap, styles);
            BlockBox outer = null;
            foreach (var c in ContentRoot(root).Children) if (c is BlockBox bb && bb.Element?.TagName == "div") outer = bb;
            Assert.That(outer, Is.Not.Null);
            BlockBox inner = null;
            foreach (var c in outer.Children) if (c is BlockBox bb && bb.Element?.TagName == "div") inner = bb;
            Assert.That(inner, Is.Not.Null);
        }

        [Test]
        public void Inline_element_inside_block_produces_inline_box() {
            var (_, snap, styles) = BuildSnap("<div><span>x</span></div>");
            var root = BuildBoxes(snap, styles);
            InlineBox span = null;
            foreach (var b in AllBoxes(root)) {
                if (b is InlineBox ib && ib.Element?.TagName == "span") span = ib;
            }
            Assert.That(span, Is.Not.Null);
        }

        [Test]
        public void Text_node_at_block_level_becomes_text_run() {
            var (_, snap, styles) = BuildSnap("<p>hello world</p>");
            var root = BuildBoxes(snap, styles);
            TextRun tr = null;
            foreach (var b in AllBoxes(root)) if (b is TextRun r) tr = r;
            Assert.That(tr, Is.Not.Null);
            Assert.That(tr.Text, Does.Contain("hello"));
        }

        [Test]
        public void Text_run_carries_managed_TextNode_via_SourceNode() {
            var (_, snap, styles) = BuildSnap("<p>marker</p>");
            var root = BuildBoxes(snap, styles);
            TextRun tr = null;
            foreach (var b in AllBoxes(root)) if (b is TextRun r && r.Text == "marker") tr = r;
            Assert.That(tr, Is.Not.Null);
            Assert.That(tr.SourceNode, Is.Not.Null);
            Assert.That(tr.SourceNode, Is.InstanceOf<TextNode>());
        }

        [Test]
        public void Element_pointer_is_managed_Element_from_snapshot_ManagedNodes() {
            var (doc, snap, styles) = BuildSnap("<div id=\"target\"></div>");
            var root = BuildBoxes(snap, styles);
            BlockBox bb = null;
            foreach (var c in ContentRoot(root).Children) if (c is BlockBox b && b.Element?.GetAttribute("id") == "target") bb = b;
            Assert.That(bb, Is.Not.Null);
            // Element pointer is the same managed instance the doc holds.
            Assert.That(bb.Element, Is.SameAs(doc.GetElementById("target")));
        }

        [Test]
        public void AttributeMap_remains_accessible_via_Box_Element() {
            var (_, snap, styles) = BuildSnap("<div data-x=\"hello\" class=\"a b\"></div>");
            var root = BuildBoxes(snap, styles);
            BlockBox bb = null;
            foreach (var c in ContentRoot(root).Children) if (c is BlockBox b && b.Element?.TagName == "div") bb = b;
            Assert.That(bb, Is.Not.Null);
            Assert.That(bb.Element.GetAttribute("data-x"), Is.EqualTo("hello"));
            Assert.That(bb.Element.GetAttribute("class"), Is.EqualTo("a b"));
        }

        [Test]
        public void Display_none_descendant_skipped() {
            var (_, snap, styles) = BuildSnap("<div><span style=\"display:none\">hidden</span><span>visible</span></div>");
            var root = BuildBoxes(snap, styles);
            int textRuns = 0;
            foreach (var b in AllBoxes(root)) if (b is TextRun) textRuns++;
            Assert.That(textRuns, Is.EqualTo(1));
        }

        [Test]
        public void Display_contents_passes_through_children() {
            var (_, snap, styles) = BuildSnap("<div><div style=\"display:contents\"><div>a</div><div>b</div></div></div>");
            var root = BuildBoxes(snap, styles);
            BlockBox outer = null;
            foreach (var c in ContentRoot(root).Children) if (c is BlockBox bb && bb.Element?.TagName == "div") outer = bb;
            Assert.That(outer, Is.Not.Null);
            int innerDivs = 0;
            foreach (var c in outer.Children) if (c is BlockBox bb && bb.Element?.TagName == "div") innerDivs++;
            Assert.That(innerDivs, Is.EqualTo(2));
        }

        [Test]
        public void Mixed_block_and_inline_wraps_inlines_in_anonymous() {
            var (_, snap, styles) = BuildSnap("<div>before<div>middle</div>after</div>");
            var root = BuildBoxes(snap, styles);
            BlockBox outer = null;
            foreach (var c in ContentRoot(root).Children) if (c is BlockBox bb && bb.Element?.TagName == "div" && !(c is AnonymousBlockBox)) outer = bb;
            Assert.That(outer, Is.Not.Null);
            int anon = 0, blocks = 0;
            foreach (var c in outer.Children) {
                if (c is AnonymousBlockBox) anon++;
                else if (c is BlockBox bb && bb.Element?.TagName == "div") blocks++;
            }
            Assert.That(anon, Is.EqualTo(2));
            Assert.That(blocks, Is.EqualTo(1));
        }

        [Test]
        public void Inline_block_styled_div_marks_box_as_inline_block() {
            var (_, snap, styles) = BuildSnap("<div style=\"display:inline-block\">x</div>");
            var root = BuildBoxes(snap, styles);
            BlockBox bb = null;
            foreach (var b in AllBoxes(root)) if (b is BlockBox box && box.Element?.TagName == "div") bb = box;
            Assert.That(bb, Is.Not.Null);
            Assert.That(bb.IsInlineBlock, Is.True);
        }

        [Test]
        public void Pool_reuses_boxes_across_builds() {
            var (_, snap, styles) = BuildSnap("<div><span>a</span><span>b</span></div>");
            var pool = new BoxPool();
            var scratch = new LayoutScratch();

            pool.BeginPass();
            var arr = SnapshotStyleArray.FromMap(snap, styles);
            var b1 = new SnapshotBoxBuilder(arr.At, pool, scratch);
            var first = b1.BuildFromSnapshot(snap);
            pool.EndPass(null); // release everything back to free lists

            int freeBefore = pool.FreeCountFor<BlockBox>() + pool.FreeCountFor<InlineBox>() + pool.FreeCountFor<TextRun>();
            Assert.That(freeBefore, Is.GreaterThan(0), "first build should populate free list");

            // Second build: the pool should have non-empty free lists ready.
            pool.BeginPass();
            var b2 = new SnapshotBoxBuilder(arr.At, pool, scratch);
            var second = b2.BuildFromSnapshot(snap);
            pool.EndPass(null);
            Assert.That(second, Is.Not.Null);
            Assert.That(first, Is.Not.Null);
        }

        static IEnumerable<Box> AllBoxes(Box root) {
            yield return root;
            foreach (var c in root.Children) {
                foreach (var d in AllBoxes(c)) yield return d;
            }
        }
    }
}
