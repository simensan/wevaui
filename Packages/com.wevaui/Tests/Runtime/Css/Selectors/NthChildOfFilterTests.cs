using NUnit.Framework;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Selectors {
    // C9b — `:nth-child(An+B of <selector-list>)` (CSS Selectors L4 §6.6.5):
    // the An+B index counts only siblings that match the filter selector-list.
    // `:nth-last-child(... of S)` is the from-the-end variant. Pins use the
    // four-li canonical fixture so the per-position behavior is unambiguous.
    public class NthChildOfFilterTests {
        static Document Parse(string html) => HtmlParser.Parse(html);
        static bool Match(string selector, Element e)
            => SelectorMatcher.Matches(SelectorParser.Parse(selector), e);
        static Element ById(Document doc, string id) => doc.GetElementById(id);

        // <ul><li id="a">A</li><li id="b" class="x">B</li><li id="c">C</li><li id="d" class="x">D</li></ul>
        static Document FourLis() => Parse(
            "<ul>" +
            "<li id=\"a\">A</li>" +
            "<li id=\"b\" class=\"x\">B</li>" +
            "<li id=\"c\">C</li>" +
            "<li id=\"d\" class=\"x\">D</li>" +
            "</ul>");

        [Test]
        public void NthChild_1_of_x_matches_first_filtered_child_not_first_overall() {
            var doc = FourLis();
            // B is the 1st .x-child; A is the 1st child overall but does not
            // satisfy the filter so it must NOT match.
            Assert.That(Match("li:nth-child(1 of .x)", ById(doc, "b")), Is.True);
            Assert.That(Match("li:nth-child(1 of .x)", ById(doc, "a")), Is.False);
            // C and D also must not match :nth-child(1 of .x).
            Assert.That(Match("li:nth-child(1 of .x)", ById(doc, "c")), Is.False);
            Assert.That(Match("li:nth-child(1 of .x)", ById(doc, "d")), Is.False);
        }

        [Test]
        public void NthChild_2_of_x_matches_second_filtered_child() {
            var doc = FourLis();
            // D is the 2nd .x-child (B is 1st, D is 2nd among class="x" siblings).
            Assert.That(Match("li:nth-child(2 of .x)", ById(doc, "d")), Is.True);
            Assert.That(Match("li:nth-child(2 of .x)", ById(doc, "b")), Is.False);
            Assert.That(Match("li:nth-child(2 of .x)", ById(doc, "a")), Is.False);
            Assert.That(Match("li:nth-child(2 of .x)", ById(doc, "c")), Is.False);
        }

        [Test]
        public void NthChild_1_no_filter_keeps_all_children_semantics() {
            var doc = FourLis();
            // Regression pin: without `of S`, :nth-child(1) is the first
            // element child overall — A, not B.
            Assert.That(Match("li:nth-child(1)", ById(doc, "a")), Is.True);
            Assert.That(Match("li:nth-child(1)", ById(doc, "b")), Is.False);
        }

        [Test]
        public void NthLastChild_1_of_x_matches_last_filtered_child() {
            var doc = FourLis();
            // Walking from the end, D is the 1st .x-child and B is the 2nd.
            Assert.That(Match("li:nth-last-child(1 of .x)", ById(doc, "d")), Is.True);
            Assert.That(Match("li:nth-last-child(1 of .x)", ById(doc, "b")), Is.False);
            Assert.That(Match("li:nth-last-child(2 of .x)", ById(doc, "b")), Is.True);
            Assert.That(Match("li:nth-last-child(2 of .x)", ById(doc, "d")), Is.False);
            // The trailing non-matching element (none here) — C is unfiltered
            // and cannot satisfy the .x filter at any position.
            Assert.That(Match("li:nth-last-child(1 of .x)", ById(doc, "c")), Is.False);
        }
    }
}
