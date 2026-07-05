using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Selectors {
    // CSS Selectors L4 §17 + §6.2 — :not(:has(...)) interaction.
    //
    // `:not(:has(...))` is the negated form of the relational pseudo-class.
    // The matcher routes through PseudoClassKind.Not → InnerList → MatchSequence,
    // which in turn hits PseudoClassKind.Has → MatchHas. These tests verify the
    // composed path is wired end-to-end: negation correctly inverts :has(), the
    // argument permutations (descendant, child, sibling) all invert correctly,
    // and the interaction composes with cascade application.
    public class NotHasInteractionTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(CssParser.Parse(s));

        static bool Match(string selector, Element e) {
            var sel = SelectorParser.Parse(selector);
            return SelectorMatcher.Matches(sel, e);
        }

        // ---- Basic negation of descendant :has ----

        [Test]
        public void Not_has_descendant_matches_when_no_matching_descendant() {
            // :not(:has(img)) is true for an element with NO img descendant.
            var doc = Html(@"<div id=""x""><p>text</p></div>");
            Assert.That(Match("div:not(:has(img))", doc.GetElementById("x")), Is.True);
        }

        [Test]
        public void Not_has_descendant_does_not_match_when_descendant_present() {
            // When the div DOES have an img descendant, :not(:has(img)) is false.
            var doc = Html(@"<div id=""x""><img></div>");
            Assert.That(Match("div:not(:has(img))", doc.GetElementById("x")), Is.False);
        }

        [Test]
        public void Not_has_deep_descendant_does_not_match() {
            // Deeply nested img still satisfies :has(img) → :not(:has(img)) is false.
            var doc = Html(@"<div id=""x""><section><article><img></article></section></div>");
            Assert.That(Match("div:not(:has(img))", doc.GetElementById("x")), Is.False);
        }

        // ---- :not(:has(> ...)) — direct child combinator ----

        [Test]
        public void Not_has_direct_child_matches_when_no_direct_child() {
            // img is a grandchild, not a direct child → :has(> img) is false
            // → :not(:has(> img)) is true.
            var doc = Html(@"<div id=""x""><span><img></span></div>");
            Assert.That(Match("div:not(:has(> img))", doc.GetElementById("x")), Is.True);
        }

        [Test]
        public void Not_has_direct_child_does_not_match_when_direct_child_present() {
            var doc = Html(@"<div id=""x""><img></div>");
            Assert.That(Match("div:not(:has(> img))", doc.GetElementById("x")), Is.False);
        }

        // ---- :not(:has(+ ...)) — adjacent sibling combinator ----

        [Test]
        public void Not_has_adjacent_sibling_matches_when_no_next_sibling() {
            // No sibling at all → :has(+ p) is false → :not(:has(+ p)) is true.
            var doc = Html(@"<div><div id=""x""></div></div>");
            Assert.That(Match("div:not(:has(+ p))", doc.GetElementById("x")), Is.True);
        }

        [Test]
        public void Not_has_adjacent_sibling_matches_when_sibling_wrong_type() {
            // Next sibling is a span, not a p → :not(:has(+ p)) is true.
            var doc = Html(@"<div><div id=""x""></div><span></span></div>");
            Assert.That(Match("div:not(:has(+ p))", doc.GetElementById("x")), Is.True);
        }

        [Test]
        public void Not_has_adjacent_sibling_does_not_match_when_correct_next_sibling() {
            var doc = Html(@"<div><div id=""x""></div><p></p></div>");
            Assert.That(Match("div:not(:has(+ p))", doc.GetElementById("x")), Is.False);
        }

        // ---- :not(:has(.class)) — class in descendant ----

        [Test]
        public void Not_has_class_descendant_matches_card_without_active_child() {
            // Spec invariant: .card:not(:has(.active)) targets cards with no
            // active child — a common UI pattern (CSS Selectors L4 §17).
            var doc = Html(@"<div class=""card"" id=""x""><span class=""normal""></span></div>");
            Assert.That(Match(".card:not(:has(.active))", doc.GetElementById("x")), Is.True);
        }

        [Test]
        public void Not_has_class_descendant_excludes_card_with_active_child() {
            var doc = Html(@"<div class=""card"" id=""x""><span class=""active""></span></div>");
            Assert.That(Match(".card:not(:has(.active))", doc.GetElementById("x")), Is.False);
        }

        // ---- Cascade integration ----

        [Test]
        public void Not_has_applied_in_cascade_selects_correct_elements() {
            // p:not(:has(span)) { color: red } applies only to p elements that
            // contain no span descendants. CSS Selectors L4 §6.2 + §17.
            var doc = Html(
                @"<p id=""plain"">text</p>" +
                @"<p id=""with_span""><span>hi</span></p>");
            var engine = new CascadeEngine(new[] {
                Author("p:not(:has(span)) { color: red; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("plain")).Get("color"), Is.EqualTo("red"));
            Assert.That(engine.Compute(doc.GetElementById("with_span")).Get("color"), Is.Not.EqualTo("red"));
        }

        [Test]
        public void Not_has_in_cascade_combined_with_class() {
            // .card:not(:has(> .hero)) { background: blue } — scoped to cards
            // that have no direct .hero child.
            var doc = Html(
                @"<div class=""card"" id=""no_hero""><p></p></div>" +
                @"<div class=""card"" id=""has_hero""><div class=""hero""></div></div>");
            var engine = new CascadeEngine(new[] {
                Author(".card:not(:has(> .hero)) { background-color: blue; }")
            });
            Assert.That(engine.Compute(doc.GetElementById("no_hero")).Get("background-color"), Is.EqualTo("blue"));
            Assert.That(engine.Compute(doc.GetElementById("has_hero")).Get("background-color"), Is.Not.EqualTo("blue"));
        }
    }
}
