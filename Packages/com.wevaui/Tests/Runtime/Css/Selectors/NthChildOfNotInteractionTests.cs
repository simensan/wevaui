using NUnit.Framework;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Selectors {
    // CSS Selectors L4 §6.6.5 — :nth-child(An+B of <selector-list>) interaction
    // with :not(), :is(), :where(), and compound selectors.  The spec mandates
    // that the "of S" filter is an *independent* match against each candidate
    // sibling; it has no interaction with the qualifying selector on the left
    // side.  Tests also cover edge cases where the filter itself uses functional
    // pseudo-classes so the engine's MatchesFilter -> MatchSequence path is
    // exercised end-to-end.
    public class NthChildOfNotInteractionTests {
        static Document Parse(string html) => HtmlParser.Parse(html);
        static bool Match(string selector, Element e)
            => SelectorMatcher.Matches(SelectorParser.Parse(selector), e);
        static Element ById(Document doc, string id) => doc.GetElementById(id);

        // Fixture: five items — a(.active), b, c(.active.special), d(.active), e
        // Filtered positions among .active: B=1(a), C=2(c), D=3(d).
        static Document FiveItems() => Parse(
            "<ul>" +
            "<li id=\"a\" class=\"active\">A</li>" +
            "<li id=\"b\">B</li>" +
            "<li id=\"c\" class=\"active special\">C</li>" +
            "<li id=\"d\" class=\"active\">D</li>" +
            "<li id=\"e\">E</li>" +
            "</ul>");

        // Fixture: six items with alternating classes
        // a(.x), b(.y), c(.x), d(.y), e(.x), f(.y)
        static Document SixAlternating() => Parse(
            "<ul>" +
            "<li id=\"a\" class=\"x\">A</li>" +
            "<li id=\"b\" class=\"y\">B</li>" +
            "<li id=\"c\" class=\"x\">C</li>" +
            "<li id=\"d\" class=\"y\">D</li>" +
            "<li id=\"e\" class=\"x\">E</li>" +
            "<li id=\"f\" class=\"y\">F</li>" +
            "</ul>");

        // ---- :not() inside the `of S` filter --------------------------------

        [Test]
        public void NthChild_of_not_active_selects_inactive_items() {
            // CSS Selectors L4 §6.6.5: the filter is `:not(.active)`.
            // Among the five items, the non-active items are b (pos 1) and e (pos 2).
            var doc = FiveItems();
            // b is the 1st child matching :not(.active).
            Assert.That(Match("li:nth-child(1 of :not(.active))", ById(doc, "b")), Is.True);
            // e is the 2nd child matching :not(.active).
            Assert.That(Match("li:nth-child(2 of :not(.active))", ById(doc, "e")), Is.True);
            // a has .active so it does NOT satisfy :not(.active) — must not match.
            Assert.That(Match("li:nth-child(1 of :not(.active))", ById(doc, "a")), Is.False);
            Assert.That(Match("li:nth-child(2 of :not(.active))", ById(doc, "b")), Is.False);
        }

        [Test]
        public void NthChild_of_not_active_even_positions_among_inactive() {
            // :nth-child(even of :not(.active)) — 2nd, 4th, … inactive items.
            // Inactive items are b (1st inactive) and e (2nd inactive).
            // So "even" = e only (2nd inactive).
            var doc = FiveItems();
            Assert.That(Match("li:nth-child(even of :not(.active))", ById(doc, "e")), Is.True);
            Assert.That(Match("li:nth-child(even of :not(.active))", ById(doc, "b")), Is.False);
        }

        // ---- :is() inside the `of S` filter ---------------------------------

        [Test]
        public void NthChild_of_is_compound_matches_correct_positions() {
            // :is(.x, .y) accepts all six items in the alternating fixture.
            // :nth-child(3 of :is(.x, .y)) is the 3rd item among all .x or .y items.
            // In SixAlternating that is c (a=1, b=2, c=3, ...).
            var doc = SixAlternating();
            Assert.That(Match("li:nth-child(3 of :is(.x, .y))", ById(doc, "c")), Is.True);
            Assert.That(Match("li:nth-child(3 of :is(.x, .y))", ById(doc, "b")), Is.False);
        }

        [Test]
        public void NthChild_of_is_x_y_1n_plus_1_matches_odd_among_all() {
            // :nth-child(2n+1 of :is(.x, .y)) = odd positions: a(1), c(3), e(5).
            var doc = SixAlternating();
            Assert.That(Match("li:nth-child(2n+1 of :is(.x, .y))", ById(doc, "a")), Is.True);
            Assert.That(Match("li:nth-child(2n+1 of :is(.x, .y))", ById(doc, "c")), Is.True);
            Assert.That(Match("li:nth-child(2n+1 of :is(.x, .y))", ById(doc, "e")), Is.True);
            Assert.That(Match("li:nth-child(2n+1 of :is(.x, .y))", ById(doc, "b")), Is.False);
            Assert.That(Match("li:nth-child(2n+1 of :is(.x, .y))", ById(doc, "d")), Is.False);
        }

        // ---- Compound selector inside the filter ----------------------------

        [Test]
        public void NthChild_of_compound_tag_plus_class_filter() {
            // :nth-child(1 of li.active.special) — among items that are <li>
            // AND have both .active AND .special, c is the only one.
            var doc = FiveItems();
            Assert.That(Match("li:nth-child(1 of li.active.special)", ById(doc, "c")), Is.True);
            // a has .active but not .special — must not match.
            Assert.That(Match("li:nth-child(1 of li.active.special)", ById(doc, "a")), Is.False);
        }

        // ---- Interaction: left-side qualifier vs `of S` filter independence --

        [Test]
        public void NthChild_filter_counts_all_matching_siblings_regardless_of_type_qualifier() {
            // CSS Selectors L4 §6.6.5 (note): the "of S" filter is applied
            // to ALL element-type siblings, not only those matching the left-
            // side type selector. The type qualifier (li) restricts which
            // elements the WHOLE rule can match, but the filter count is
            // scoped to the filtered set of siblings.
            //
            // Fixture: li(.x), span(.x), li(.x), span(.x)
            // Among .x siblings: li(1st), span(2nd), li(3rd), span(4th).
            // :nth-child(2 of .x) = span at position 2 (sibling index 2
            // among .x siblings). `li:nth-child(2 of .x)` applies the li
            // qualifier on TOP: the 2nd .x sibling is a <span>, so NO <li>
            // can match :nth-child(2 of .x) in this fixture.
            var doc = Parse(
                "<ul>" +
                "<li id=\"l1\" class=\"x\">A</li>" +
                "<span id=\"s1\" class=\"x\">B</span>" +
                "<li id=\"l2\" class=\"x\">C</li>" +
                "<span id=\"s2\" class=\"x\">D</span>" +
                "</ul>");
            // The 2nd .x sibling is the <span>; applying the li qualifier means
            // li:nth-child(2 of .x) matches nothing in this fixture.
            Assert.That(Match("li:nth-child(2 of .x)", ById(doc, "l1")), Is.False);
            Assert.That(Match("li:nth-child(2 of .x)", ById(doc, "l2")), Is.False);
            // But `span:nth-child(2 of .x)` matches s1.
            Assert.That(Match("span:nth-child(2 of .x)", ById(doc, "s1")), Is.True);
            // And li:nth-child(1 of .x) correctly finds l1 (1st .x sibling is an li).
            Assert.That(Match("li:nth-child(1 of .x)", ById(doc, "l1")), Is.True);
        }

        // ---- :nth-last-child with :not() ------------------------------------

        [Test]
        public void NthLastChild_of_not_active_from_end() {
            // Walking backward through the five-item fixture:
            // e (5th overall, 1st inactive from end), b (2nd overall, 2nd inactive from end).
            // :nth-last-child(1 of :not(.active)) = e.
            // :nth-last-child(2 of :not(.active)) = b.
            var doc = FiveItems();
            Assert.That(Match("li:nth-last-child(1 of :not(.active))", ById(doc, "e")), Is.True);
            Assert.That(Match("li:nth-last-child(2 of :not(.active))", ById(doc, "b")), Is.True);
            Assert.That(Match("li:nth-last-child(1 of :not(.active))", ById(doc, "b")), Is.False);
            // a has .active; does not satisfy :not(.active) at any position.
            Assert.That(Match("li:nth-last-child(1 of :not(.active))", ById(doc, "a")), Is.False);
        }

        // ---- :not() combined with :nth-child in the outer selector ----------

        [Test]
        public void Compound_not_plus_nth_of_filter_combined_match() {
            // :not(.special):nth-child(1 of .active) — the outer :not(.special)
            // filters ON TOP of the :nth-child match. In the five-item fixture
            // a is the 1st .active child AND does not have .special -> matches.
            // c is the 2nd .active child and has .special -> would fail :not(.special).
            var doc = FiveItems();
            Assert.That(Match("li:not(.special):nth-child(1 of .active)", ById(doc, "a")), Is.True);
            // c is the 2nd .active, not 1st, and has .special — fails both.
            Assert.That(Match("li:not(.special):nth-child(1 of .active)", ById(doc, "c")), Is.False);
            // d is the 3rd .active and lacks .special — fails the nth count.
            Assert.That(Match("li:not(.special):nth-child(1 of .active)", ById(doc, "d")), Is.False);
        }

        // ---- An+B with n=0 edge case ----------------------------------------

        [Test]
        public void NthChild_zero_step_only_matches_exact_B_position() {
            // :nth-child(0n+2 of .active) is the same as :nth-child(2 of .active).
            // Active items: a(1), c(2), d(3) → position 2 is c.
            var doc = FiveItems();
            Assert.That(Match("li:nth-child(0n+2 of .active)", ById(doc, "c")), Is.True);
            Assert.That(Match("li:nth-child(0n+2 of .active)", ById(doc, "a")), Is.False);
            Assert.That(Match("li:nth-child(0n+2 of .active)", ById(doc, "d")), Is.False);
        }
    }
}
