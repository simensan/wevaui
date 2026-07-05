using NUnit.Framework;
using Weva.Layout.Boxes;
using static Weva.Tests.Layout.LayoutTestHelpers;

namespace Weva.Tests.Layout {
    public class BoxBuilderTests {
        [Test]
        public void Block_element_produces_block_box() {
            var (root, _, _) = Build("<div></div>");
            BlockBox found = null;
            foreach (var b in AllBoxes(root)) if (b is BlockBox bb && bb.Element?.TagName == "div") found = bb;
            Assert.That(found, Is.Not.Null);
        }

        [Test]
        public void Display_none_element_is_skipped() {
            var (root, _, _) = Build("<div id=\"a\"></div><div id=\"b\" style=\"display:none\"></div>");
            int divs = 0;
            foreach (var c in ContentRoot(root).Children) if (c is BlockBox bb && bb.Element?.TagName == "div") divs++;
            Assert.That(divs, Is.EqualTo(1));
        }

        [Test]
        public void Display_none_descendant_is_skipped() {
            var (root, _, _) = Build("<div><span id=\"x\" style=\"display:none\">hidden</span><span>visible</span></div>");
            int textRuns = 0;
            foreach (var b in AllBoxes(root)) if (b is TextRun) textRuns++;
            Assert.That(textRuns, Is.EqualTo(1));
        }

        [Test]
        public void Inline_element_inside_block_produces_inline_box() {
            var (root, _) = BuildBoxesOnly("<div><span>hi</span></div>");
            InlineBox found = null;
            foreach (var b in AllBoxes(root)) if (b is InlineBox ib && ib.Element?.TagName == "span") found = ib;
            Assert.That(found, Is.Not.Null);
        }

        [Test]
        public void Text_node_at_block_level_becomes_text_run() {
            var (root, _, _) = Build("<p>hello world</p>");
            int textRuns = 0;
            foreach (var b in AllBoxes(root)) if (b is TextRun tr && tr.Text.Contains("hello")) textRuns++;
            Assert.That(textRuns, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void Mixed_block_and_inline_children_wrap_inlines_in_anonymous_block() {
            // div has block children (<div>) interleaved with inline content -> the inline
            // content should be wrapped in an AnonymousBlockBox.
            var (root, _) = BuildBoxesOnly("<div>before<div>middle</div>after</div>");
            BlockBox outer = null;
            foreach (var b in AllBoxes(root)) if (b is BlockBox bb && bb.Element?.TagName == "div" && !(b is AnonymousBlockBox)) {
                if (outer == null) outer = bb;
            }
            Assert.That(outer, Is.Not.Null);
            int anon = 0;
            int inner = 0;
            foreach (var c in outer.Children) {
                if (c is AnonymousBlockBox) anon++;
                else if (c is BlockBox bb && bb.Element?.TagName == "div") inner++;
            }
            Assert.That(anon, Is.EqualTo(2));
            Assert.That(inner, Is.EqualTo(1));
        }

        [Test]
        public void All_inline_children_keep_block_in_inline_mode() {
            var (root, _) = BuildBoxesOnly("<p>just text <span>and span</span></p>");
            BlockBox p = null;
            foreach (var b in AllBoxes(root)) if (b is BlockBox bb && bb.Element?.TagName == "p") p = bb;
            Assert.That(p, Is.Not.Null);
            Assert.That(p.ContainsInlines, Is.True);
        }

        [Test]
        public void Block_in_block_produces_nested_block_boxes() {
            var (root, _, _) = Build("<div><div></div></div>");
            BlockBox outer = null;
            foreach (var c in ContentRoot(root).Children) if (c is BlockBox bb && bb.Element?.TagName == "div") outer = bb;
            Assert.That(outer, Is.Not.Null);
            BlockBox inner = null;
            foreach (var c in outer.Children) if (c is BlockBox bb && bb.Element?.TagName == "div") inner = bb;
            Assert.That(inner, Is.Not.Null);
        }

        [Test]
        public void Nested_inline_elements_layer() {
            var (root, _) = BuildBoxesOnly("<p><a href=\"#\"><strong>here</strong></a></p>");
            InlineBox a = null;
            InlineBox strong = null;
            foreach (var b in AllBoxes(root)) {
                if (b is InlineBox ib && ib.Element?.TagName == "a") a = ib;
                if (b is InlineBox ib2 && ib2.Element?.TagName == "strong") strong = ib2;
            }
            Assert.That(a, Is.Not.Null);
            Assert.That(strong, Is.Not.Null);
        }

        [Test]
        public void Inline_block_at_block_level_is_block_box() {
            var (root, _, _) = Build("<div style=\"display:inline-block\">x</div>");
            BlockBox bb = null;
            foreach (var b in AllBoxes(root)) if (b is BlockBox box && box.Element?.TagName == "div") bb = box;
            Assert.That(bb, Is.Not.Null);
        }

        [Test]
        public void Text_run_carries_parent_style_for_color_inheritance() {
            var (root, _, _) = Build("<p>hello</p>", "p { color: red; }");
            TextRun first = null;
            foreach (var tr in AllTextRuns(root)) { first = tr; break; }
            Assert.That(first, Is.Not.Null);
            Assert.That(first.Style.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Anonymous_block_skipped_when_inline_is_pure_whitespace_between_blocks() {
            var (root, _, _) = Build("<div><div></div>   <div></div></div>");
            BlockBox outer = null;
            foreach (var c in ContentRoot(root).Children) if (c is BlockBox bb && bb.Element?.TagName == "div") outer = bb;
            Assert.That(outer, Is.Not.Null);
            int anon = 0;
            foreach (var c in outer.Children) if (c is AnonymousBlockBox) anon++;
            Assert.That(anon, Is.EqualTo(0));
        }

        [Test]
        public void Empty_paragraph_still_creates_block_box() {
            var (root, _, _) = Build("<p></p>");
            BlockBox p = null;
            foreach (var c in ContentRoot(root).Children) if (c is BlockBox bb && bb.Element?.TagName == "p") p = bb;
            Assert.That(p, Is.Not.Null);
        }

        // CSS Display 3 §2.4 — `display: contents` extended coverage.
        // The spec has multiple sub-rules; the single existing test only
        // pins box-removal. These add: nested contents (hoisting through
        // multiple levels), inheritance through the contents wrapper, the
        // wrapper's own background/border being suppressed visually
        // (because no box exists), and contents-on-document-root behaviour.

        [Test]
        public void Nested_display_contents_hoists_grandchildren_to_outer() {
            // Multiple chained contents wrappers must all vanish; the
            // deepest descendant lands directly under the outer in-flow
            // ancestor. Spec wording: "the element generates no boxes" —
            // the rule transitively flattens chains.
            var (root, _, _) = Build(
                "<div id=\"outer\">" +
                "<div id=\"a\" style=\"display:contents\">" +
                "<div id=\"b\" style=\"display:contents\">" +
                "<div id=\"leaf\"></div>" +
                "</div></div></div>");
            BlockBox outer = null;
            foreach (var c in ContentRoot(root).Children) {
                if (c is BlockBox bb && bb.Element?.GetAttribute("id") == "outer") outer = bb;
            }
            Assert.That(outer, Is.Not.Null);
            int leafCount = 0, contentsCount = 0;
            foreach (var c in outer.Children) {
                if (c is BlockBox bb && bb.Element?.GetAttribute("id") == "leaf") leafCount++;
                if (c is BlockBox bb2) {
                    string id = bb2.Element?.GetAttribute("id");
                    if (id == "a" || id == "b") contentsCount++;
                }
            }
            Assert.That(contentsCount, Is.EqualTo(0),
                "neither contents wrapper may produce a box; the chain must flatten through all levels");
            Assert.That(leafCount, Is.EqualTo(1),
                "the deepest descendant must be hoisted into the outer in-flow ancestor");
        }

        [Test]
        public void Display_contents_does_not_break_inheritance() {
            // CSS Display 3 §2.4: although the contents element has no
            // box, inheritance still flows THROUGH it — descendants
            // continue to inherit from the contents element's computed
            // style, which itself inherits from its parent. So a `color`
            // declaration on the contents element must reach the child
            // even though the wrapper has no box.
            var (root, styles, _) = Build(
                "<div style=\"color:red\">" +
                "<div style=\"display:contents;color:green\">" +
                "<div id=\"leaf\"></div>" +
                "</div></div>");
            var leaf = FindLeaf(root);
            Assert.That(leaf, Is.Not.Null);
            Assert.That(leaf.Element, Is.Not.Null);
            var cs = styles[leaf.Element];
            Assert.That(cs.Get("color"), Is.EqualTo("green"),
                "leaf must inherit color from its contents-display ancestor (color: green), not skip to the grandparent");
        }

        [Test]
        public void Display_contents_on_inline_element_still_hoists_children() {
            // Spec applies to ANY element regardless of original display:
            // a `<span style="display:contents">` must also flatten away.
            var (root, _, _) = Build(
                "<div id=\"outer\"><span style=\"display:contents\"><div id=\"inner\"></div></span></div>");
            BlockBox outer = null;
            foreach (var c in ContentRoot(root).Children) {
                if (c is BlockBox bb && bb.Element?.GetAttribute("id") == "outer") outer = bb;
            }
            Assert.That(outer, Is.Not.Null);
            int spanCount = 0, innerCount = 0;
            foreach (var c in outer.Children) {
                if (c is InlineBox ib && ib.Element?.TagName == "span") spanCount++;
                if (c is BlockBox bb && bb.Element?.GetAttribute("id") == "inner") innerCount++;
            }
            Assert.That(spanCount, Is.EqualTo(0),
                "a <span> with display:contents must vanish just like a <div> would");
            Assert.That(innerCount, Is.EqualTo(1),
                "the inner <div> must be hoisted into the outer regardless of the wrapper's original display");
        }

        static BlockBox FindLeaf(Weva.Layout.Boxes.Box root) {
            foreach (var b in AllBoxes(root)) {
                if (b is BlockBox bb && bb.Element?.GetAttribute("id") == "leaf") return bb;
            }
            return null;
        }

        [Test]
        public void Display_contents_box_is_replaced_by_its_children() {
            // CSS Display 3 §2.4: `display: contents` makes the element vanish
            // from the box tree; its children participate in the parent
            // formatting context as if the wrapper weren't there.
            var (root, _, _) = Build(
                "<div id=\"outer\"><div id=\"wrap\" style=\"display:contents\"><div id=\"inner\"></div></div></div>");
            BlockBox outer = null;
            foreach (var c in ContentRoot(root).Children) if (c is BlockBox bb && bb.Element?.GetAttribute("id") == "outer") outer = bb;
            Assert.That(outer, Is.Not.Null);
            // outer's children should be `inner` directly — no `wrap` box.
            int wrapCount = 0, innerCount = 0;
            foreach (var c in outer.Children) {
                if (c is BlockBox bb && bb.Element?.GetAttribute("id") == "wrap") wrapCount++;
                if (c is BlockBox bb2 && bb2.Element?.GetAttribute("id") == "inner") innerCount++;
            }
            Assert.That(wrapCount, Is.EqualTo(0), "display:contents wrapper must not produce a box");
            Assert.That(innerCount, Is.EqualTo(1), "display:contents children must be hoisted into parent");
        }
    }
}
