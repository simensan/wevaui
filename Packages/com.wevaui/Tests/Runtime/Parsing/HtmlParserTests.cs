using System.Linq;
using NUnit.Framework;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Parsing {
    public class HtmlParserTests {
        // HTML5 §13.2 fragment parsing: HtmlParser wraps fragments in
        // synthetic `<html><body>` so the author's "root" element is now
        // body.firstElementChild rather than doc.Children[0]. Most tests
        // here predate the wrapper and want to assert against the first
        // *author-supplied* element; route them through this helper so the
        // existing assertions keep their semantic intent.
        static Element Root(Document doc) {
            var html = (Element)doc.Children[0];
            Element body = null;
            foreach (var c in html.Children) {
                if (c is Element be && be.TagName == "body") { body = be; break; }
            }
            if (body == null) return html;
            foreach (var c in body.Children) {
                if (c is Element ee) return ee;
            }
            return body;
        }

        // For tests that want to count top-level author children (text +
        // elements), enumerate body.Children rather than doc.Children.
        static System.Collections.Generic.IReadOnlyList<Node> AuthorTopLevel(Document doc) {
            var html = (Element)doc.Children[0];
            foreach (var c in html.Children) {
                if (c is Element be && be.TagName == "body") return be.Children;
            }
            return html.Children;
        }

        [Test]
        public void Parses_empty_string_to_empty_document() {
            var doc = HtmlParser.Parse("");
            Assert.That(doc.Children, Has.Count.EqualTo(0));
        }

        [Test]
        public void Parses_single_element() {
            var doc = HtmlParser.Parse("<div></div>");
            // Synthetic <html> wrapper is the only Document child.
            Assert.That(doc.Children, Has.Count.EqualTo(1));
            Assert.That(((Element)doc.Children[0]).TagName, Is.EqualTo("html"));
            Assert.That(Root(doc).TagName, Is.EqualTo("div"));
        }

        [Test]
        public void Parses_nested_elements() {
            var doc = HtmlParser.Parse("<div><span></span></div>");
            var div = Root(doc);
            Assert.That(div.Children, Has.Count.EqualTo(1));
            Assert.That(((Element)div.Children[0]).TagName, Is.EqualTo("span"));
        }

        [Test]
        public void Parses_text_node_as_child() {
            var doc = HtmlParser.Parse("<p>hello</p>");
            var p = Root(doc);
            Assert.That(p.Children, Has.Count.EqualTo(1));
            Assert.That(((TextNode)p.Children[0]).Data, Is.EqualTo("hello"));
        }

        [Test]
        public void Parses_attributes_onto_element() {
            var doc = HtmlParser.Parse("<div id=\"main\" class=\"box\"></div>");
            var div = Root(doc);
            Assert.That(div.GetAttribute("id"), Is.EqualTo("main"));
            Assert.That(div.GetAttribute("class"), Is.EqualTo("box"));
        }

        [Test]
        public void Parses_void_element_without_close() {
            var doc = HtmlParser.Parse("<div><br></div>");
            var div = Root(doc);
            Assert.That(div.Children, Has.Count.EqualTo(1));
            Assert.That(((Element)div.Children[0]).TagName, Is.EqualTo("br"));
        }

        [Test]
        public void Parses_self_closing_non_void_as_empty_element() {
            var doc = HtmlParser.Parse("<div><span/></div>");
            var div = Root(doc);
            var span = (Element)div.Children[0];
            Assert.That(span.TagName, Is.EqualTo("span"));
            Assert.That(span.Children, Has.Count.EqualTo(0));
        }

        [Test]
        public void Parses_paragraph_with_inline_strong_and_link() {
            var doc = HtmlParser.Parse("<p>Click <a href=\"#\"><strong>here</strong></a> to start</p>");
            var p = Root(doc);
            Assert.That(p.TagName, Is.EqualTo("p"));
            Assert.That(p.Children, Has.Count.EqualTo(3));
            Assert.That(((TextNode)p.Children[0]).Data, Is.EqualTo("Click "));
            var a = (Element)p.Children[1];
            Assert.That(a.TagName, Is.EqualTo("a"));
            Assert.That(a.GetAttribute("href"), Is.EqualTo("#"));
            var strong = (Element)a.Children[0];
            Assert.That(strong.TagName, Is.EqualTo("strong"));
            Assert.That(((TextNode)strong.Children[0]).Data, Is.EqualTo("here"));
            Assert.That(((TextNode)p.Children[2]).Data, Is.EqualTo(" to start"));
        }

        [Test]
        public void Parses_multiple_root_elements_into_document() {
            var doc = HtmlParser.Parse("<div></div><p></p><span></span>");
            // All three live under the synthetic <html> > <body>.
            Assert.That(AuthorTopLevel(doc), Has.Count.EqualTo(3));
        }

        [Test]
        public void Parses_whitespace_between_elements_as_text() {
            var doc = HtmlParser.Parse("<div></div>\n  <p></p>");
            var top = AuthorTopLevel(doc);
            Assert.That(top, Has.Count.EqualTo(3));
            Assert.That(top[1], Is.TypeOf<TextNode>());
        }

        [Test]
        public void Discards_comment_nodes() {
            var doc = HtmlParser.Parse("<div><!-- skip me --><span></span></div>");
            var div = Root(doc);
            Assert.That(div.Children, Has.Count.EqualTo(1));
            Assert.That(((Element)div.Children[0]).TagName, Is.EqualTo("span"));
        }

        [Test]
        public void Discards_doctype() {
            var doc = HtmlParser.Parse("<!DOCTYPE html><div></div>");
            // Synthetic <html> wrapper is the only Element child.
            int elementCount = 0;
            foreach (var c in doc.Children) if (c is Element) elementCount++;
            Assert.That(elementCount, Is.EqualTo(1));
            Assert.That(Root(doc).TagName, Is.EqualTo("div"));
        }

        [Test]
        public void Parses_lists() {
            var doc = HtmlParser.Parse("<ul><li>a</li><li>b</li></ul>");
            var ul = Root(doc);
            Assert.That(ul.TagName, Is.EqualTo("ul"));
            Assert.That(ul.Children, Has.Count.EqualTo(2));
            Assert.That(((Element)ul.Children[0]).TagName, Is.EqualTo("li"));
            Assert.That(((TextNode)((Element)ul.Children[0]).Children[0]).Data, Is.EqualTo("a"));
        }

        [Test]
        public void Parses_input_with_multiple_attributes() {
            var doc = HtmlParser.Parse("<input type=\"text\" name=\"q\" placeholder=\"Search\" required>");
            var input = Root(doc);
            Assert.That(input.TagName, Is.EqualTo("input"));
            Assert.That(input.GetAttribute("type"), Is.EqualTo("text"));
            Assert.That(input.GetAttribute("placeholder"), Is.EqualTo("Search"));
            Assert.That(input.HasAttribute("required"), Is.True);
            Assert.That(input.GetAttribute("required"), Is.EqualTo(""));
        }

        [Test]
        public void Mismatched_end_tag_throws_by_default() {
            var ex = Assert.Throws<HtmlParseException>(() => HtmlParser.Parse("<div></span>"));
            Assert.That(ex.Message, Does.Contain("Mismatched"));
        }

        [Test]
        public void Unclosed_element_throws_by_default() {
            Assert.Throws<HtmlParseException>(() => HtmlParser.Parse("<div><span></div>"));
        }

        [Test]
        public void Unclosed_root_element_throws_by_default() {
            Assert.Throws<HtmlParseException>(() => HtmlParser.Parse("<div>"));
        }

        [Test]
        public void End_tag_for_void_element_throws_by_default() {
            Assert.Throws<HtmlParseException>(() => HtmlParser.Parse("</br>"));
        }

        [Test]
        public void Lenient_mode_swallows_mismatched_end_tag() {
            var doc = HtmlParser.Parse("<div></span>", new ParseOptions { ThrowOnError = false });
            Assert.That(Root(doc).TagName, Is.EqualTo("div"));
        }

        [Test]
        public void Lenient_mode_tolerates_unclosed_root() {
            var doc = HtmlParser.Parse("<div><span>", new ParseOptions { ThrowOnError = false });
            var div = Root(doc);
            Assert.That(div.TagName, Is.EqualTo("div"));
            Assert.That(((Element)div.Children[0]).TagName, Is.EqualTo("span"));
        }

        [Test]
        public void Owner_document_set_on_all_descendants() {
            var doc = HtmlParser.Parse("<div><p><span>hi</span></p></div>");
            var span = doc.GetElementsByTagName("span").First();
            Assert.That(span.OwnerDocument, Is.SameAs(doc));
        }

        [Test]
        public void Resolves_entities_inside_text() {
            var doc = HtmlParser.Parse("<p>Tom &amp; Jerry &lt;3</p>");
            var p = Root(doc);
            Assert.That(((TextNode)p.Children[0]).Data, Is.EqualTo("Tom & Jerry <3"));
        }

        [Test]
        public void Resolves_entities_inside_attribute() {
            var doc = HtmlParser.Parse("<a href=\"a.html?x=1&amp;y=2\">link</a>");
            Assert.That(Root(doc).GetAttribute("href"), Is.EqualTo("a.html?x=1&y=2"));
        }

        [Test]
        public void Implicit_close_p_on_p_produces_siblings() {
            // HTML Living Standard "Optional tags": <p>One<p>Two should
            // auto-close the first <p> when the second <p> opens, producing
            // two sibling paragraphs (not a nested one).
            var doc = HtmlParser.Parse("<div><p>One<p>Two</p></div>");
            var div = Root(doc);
            Assert.That(div.Children, Has.Count.EqualTo(2));
            var p1 = (Element)div.Children[0];
            var p2 = (Element)div.Children[1];
            Assert.That(p1.TagName, Is.EqualTo("p"));
            Assert.That(p2.TagName, Is.EqualTo("p"));
            Assert.That(((TextNode)p1.Children[0]).Data, Is.EqualTo("One"));
            Assert.That(((TextNode)p2.Children[0]).Data, Is.EqualTo("Two"));
        }

        [Test]
        public void Implicit_close_li_on_li_produces_siblings() {
            // <li> auto-closes when another <li> opens, so list items
            // remain siblings under their <ul>/<ol> parent.
            var doc = HtmlParser.Parse("<ul><li>a<li>b</li></ul>");
            var ul = Root(doc);
            Assert.That(ul.TagName, Is.EqualTo("ul"));
            Assert.That(ul.Children, Has.Count.EqualTo(2));
            var li1 = (Element)ul.Children[0];
            var li2 = (Element)ul.Children[1];
            Assert.That(li1.TagName, Is.EqualTo("li"));
            Assert.That(li2.TagName, Is.EqualTo("li"));
            Assert.That(((TextNode)li1.Children[0]).Data, Is.EqualTo("a"));
            Assert.That(((TextNode)li2.Children[0]).Data, Is.EqualTo("b"));
        }

        [Test]
        public void Realistic_form_parses() {
            var html = @"
                <form>
                  <label for=""name"">Name</label>
                  <input id=""name"" type=""text"" required>
                  <button on-click=""submit"">Go</button>
                </form>";
            var doc = HtmlParser.Parse(html);
            var form = doc.GetElementsByTagName("form").Single();
            Assert.That(form.TagName, Is.EqualTo("form"));

            var input = doc.GetElementById("name");
            Assert.That(input, Is.Not.Null);
            Assert.That(input.TagName, Is.EqualTo("input"));
            Assert.That(input.GetAttribute("type"), Is.EqualTo("text"));
            Assert.That(input.HasAttribute("required"), Is.True);

            var label = doc.GetElementsByTagName("label").Single();
            Assert.That(label.GetAttribute("for"), Is.EqualTo("name"));

            var button = doc.GetElementsByTagName("button").Single();
            Assert.That(button.GetAttribute("on-click"), Is.EqualTo("submit"));
        }

        // ----------------------------------------------------------------
        // HTML5 §13.2.6 "adoption agency" coverage. These tests pin the
        // tree shape Chrome/Firefox produce for the narrow slice of AAA
        // we implement: a block element opening while an inline formatting
        // element is open inside a <p>. The block lifts out to a sibling
        // of <p>, and the formatting element is re-opened around the
        // post-block content.
        // ----------------------------------------------------------------

        [Test]
        public void Adoption_agency_div_inside_a_inside_p_lifts_div() {
            // Chrome's DOM for `<p>Click <a><div>here</div></a> to start</p>`:
            //   <p>Click <a/></p>
            //   <div><a>here</a></div>
            //   " to start"
            //   <p/>            -- from the stray </p>
            var doc = HtmlParser.Parse("<p>Click <a href=\"#\"><div class=\"block-link\">here</div></a> to start</p>");
            // Top-level body children: p, div, text, p.
            var top = AuthorTopLevel(doc);
            Assert.That(top, Has.Count.EqualTo(4));
            var p1 = (Element)top[0];
            Assert.That(p1.TagName, Is.EqualTo("p"));
            // <p> contains "Click " and an empty <a>.
            Assert.That(p1.Children, Has.Count.EqualTo(2));
            Assert.That(((TextNode)p1.Children[0]).Data, Is.EqualTo("Click "));
            Assert.That(((Element)p1.Children[1]).TagName, Is.EqualTo("a"));
            // <div class="block-link"> with an <a href="#">here</a> inside.
            var div = (Element)top[1];
            Assert.That(div.TagName, Is.EqualTo("div"));
            Assert.That(div.GetAttribute("class"), Is.EqualTo("block-link"));
            var innerA = (Element)div.Children[0];
            Assert.That(innerA.TagName, Is.EqualTo("a"));
            Assert.That(innerA.GetAttribute("href"), Is.EqualTo("#"));
            Assert.That(((TextNode)innerA.Children[0]).Data, Is.EqualTo("here"));
            // " to start" is a sibling text node (no surrounding <a>).
            Assert.That(((TextNode)top[2]).Data, Is.EqualTo(" to start"));
            // Trailing empty <p> from the stray </p>.
            var p2 = (Element)top[3];
            Assert.That(p2.TagName, Is.EqualTo("p"));
            Assert.That(p2.Children, Has.Count.EqualTo(0));
        }

        [Test]
        public void Adoption_agency_a_with_only_block_child_lifts_block() {
            // `<p><a><div>solo</div></a></p>` → <p><a/></p>, <div><a>solo</a></div>, <p/>.
            // (The trailing empty <p> is what HTML5 inserts for the stray </p>.)
            var doc = HtmlParser.Parse("<p><a href=\"#\"><div>solo</div></a></p>");
            var top = AuthorTopLevel(doc);
            var firstP = (Element)top[0];
            Assert.That(firstP.TagName, Is.EqualTo("p"));
            // The original <a> was popped off the stack by AAA but remains
            // structurally a child of <p> (it was already appended there).
            Assert.That(firstP.Children, Has.Count.EqualTo(1));
            Assert.That(((Element)firstP.Children[0]).TagName, Is.EqualTo("a"));
            var div = (Element)top[1];
            Assert.That(div.TagName, Is.EqualTo("div"));
            var a = (Element)div.Children[0];
            Assert.That(a.TagName, Is.EqualTo("a"));
            Assert.That(((TextNode)a.Children[0]).Data, Is.EqualTo("solo"));
        }

        [Test]
        public void Adoption_agency_preserves_formatting_element_attributes() {
            // The re-opened <a> inside the <div> must carry the original
            // href (and any other attributes).
            var doc = HtmlParser.Parse(
                "<p><a href=\"https://example.com\" target=\"_blank\"><div>x</div></a></p>");
            var div = (Element)AuthorTopLevel(doc)[1];
            var aClone = (Element)div.Children[0];
            Assert.That(aClone.TagName, Is.EqualTo("a"));
            Assert.That(aClone.GetAttribute("href"), Is.EqualTo("https://example.com"));
            Assert.That(aClone.GetAttribute("target"), Is.EqualTo("_blank"));
        }

        [Test]
        public void Adoption_agency_does_not_fire_when_no_p_ancestor() {
            // <a><div>x</div></a> directly under document: AAA only
            // operates inside a <p>, so the original nesting is kept.
            var doc = HtmlParser.Parse("<a href=\"#\"><div>x</div></a>");
            var a = Root(doc);
            Assert.That(a.TagName, Is.EqualTo("a"));
            var div = (Element)a.Children[0];
            Assert.That(div.TagName, Is.EqualTo("div"));
            Assert.That(((TextNode)div.Children[0]).Data, Is.EqualTo("x"));
        }

        [Test]
        public void Adoption_agency_does_not_affect_plain_formatting_in_p() {
            // No block child — AAA isn't triggered. Original test pinned
            // here so the new code path stays a no-op for the common case.
            var doc = HtmlParser.Parse("<p><a href=\"#\"><strong>bold link</strong></a></p>");
            var p = Root(doc);
            Assert.That(p.TagName, Is.EqualTo("p"));
            var a = (Element)p.Children[0];
            Assert.That(a.TagName, Is.EqualTo("a"));
            var strong = (Element)a.Children[0];
            Assert.That(strong.TagName, Is.EqualTo("strong"));
            Assert.That(((TextNode)strong.Children[0]).Data, Is.EqualTo("bold link"));
        }

        [Test]
        public void Stray_close_p_inserts_empty_p() {
            // HTML5 §13.2.6.4.7: `</p>` with no <p> in scope inserts an
            // empty <p>. Matches Chrome and is required for LayoutDiff
            // 23 to line up its element count.
            var doc = HtmlParser.Parse("<div></p></div>");
            var outer = Root(doc);
            Assert.That(outer.TagName, Is.EqualTo("div"));
            Assert.That(outer.Children, Has.Count.EqualTo(1));
            var inserted = (Element)outer.Children[0];
            Assert.That(inserted.TagName, Is.EqualTo("p"));
            Assert.That(inserted.Children, Has.Count.EqualTo(0));
        }
    }
}
