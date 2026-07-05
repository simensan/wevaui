using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Selectors {
    // Combinatorial edge cases for the selector engine. The existing
    // SelectorMatcherTests / HasSelectorTests / SpecificityTests cover one
    // construct at a time; this file exercises their compositions
    // (nested :is/:not/:where, :has() with combinators, pseudo-class +
    // pseudo-element compounds, and specificity in nested constructs).
    //
    // // v1: comments mark places where the engine's current behaviour is
    // narrower than the CSS Selectors L4 spec (e.g. :not() accepts only a
    // single simple selector, not a compound) and the test pins the actual
    // behaviour rather than the spec.
    public class SelectorCombinatorialTests {
        static Document Parse(string html) => HtmlParser.Parse(html);
        static Element ById(Document doc, string id) => doc.GetElementById(id);

        static bool Match(string selector, Element e, IElementStateProvider state = null)
            => SelectorMatcher.Matches(SelectorParser.Parse(selector), e, state);

        static Specificity S(string sel) => SelectorParser.Parse(sel).Specificity;

        sealed class FakeState : IElementStateProvider {
            readonly Dictionary<Element, ElementState> map = new();
            public void Set(Element e, ElementState s) { map[e] = s; }
            public ElementState GetState(Element e) => map.TryGetValue(e, out var s) ? s : ElementState.None;
        }

        // ----- Nested :is / :not / :where -----

        [Test]
        public void Is_inside_not_matches_correctly() {
            // :not(:is(.a, .b)) matches elements that have NEITHER class.
            var doc = Parse(@"<div id=""x"" class=""c""></div><div id=""y"" class=""a""></div><div id=""z"" class=""b""></div>");
            Assert.That(Match(":not(:is(.a, .b))", ById(doc, "x")), Is.True);
            Assert.That(Match(":not(:is(.a, .b))", ById(doc, "y")), Is.False);
            Assert.That(Match(":not(:is(.a, .b))", ById(doc, "z")), Is.False);
        }

        [Test]
        public void Where_inside_is_zero_specificity() {
            // :is(:where(.x), .y) — :where(.x) contributes 0; .y contributes
            // (0,1,0); :is() takes max → (0,1,0).
            Assert.That(S(":is(:where(.x), .y)"), Is.EqualTo(new Specificity(0, 1, 0)));
        }

        [Test]
        public void Not_with_compound_selector() {
            // Fixed in #258: `:not()` accepts a <complex-selector-list> per
            // CSS Selectors L4 §6.2. `:not(.a.b)` excludes elements that
            // have BOTH classes.
            var doc = Parse(@"
                <div>
                    <p id=""ab"" class=""a b""></p>
                    <p id=""a_only"" class=""a""></p>
                    <p id=""b_only"" class=""b""></p>
                    <p id=""none""></p>
                </div>");
            Assert.That(Match(":not(.a.b)", ById(doc, "ab")), Is.False);
            Assert.That(Match(":not(.a.b)", ById(doc, "a_only")), Is.True);
            Assert.That(Match(":not(.a.b)", ById(doc, "b_only")), Is.True);
            Assert.That(Match(":not(.a.b)", ById(doc, "none")), Is.True);
        }

        [Test]
        public void Is_with_multiple_compound_selectors() {
            // :is(<selector-list>) accepts full compound selectors per arg.
            var doc = Parse(@"
                <div>
                    <p id=""ax"" class=""a x""></p>
                    <p id=""by"" class=""b y""></p>
                    <p id=""a_only"" class=""a""></p>
                    <p id=""x_only"" class=""x""></p>
                </div>");
            Assert.That(Match(":is(.a.x, .b.y)", ById(doc, "ax")), Is.True);
            Assert.That(Match(":is(.a.x, .b.y)", ById(doc, "by")), Is.True);
            Assert.That(Match(":is(.a.x, .b.y)", ById(doc, "a_only")), Is.False);
            Assert.That(Match(":is(.a.x, .b.y)", ById(doc, "x_only")), Is.False);
        }

        // ----- :has() chaining -----

        [Test]
        public void Has_with_descendant_combinator() {
            var hit = Parse(@"<div id=""x""><section><span></span></section></div>");
            var miss = Parse(@"<div id=""x""><section><p></p></section></div>");
            Assert.That(Match("div:has(span)", ById(hit, "x")), Is.True);
            Assert.That(Match("div:has(span)", ById(miss, "x")), Is.False);
        }

        [Test]
        public void Has_with_direct_child_combinator() {
            // > span must be a direct child of the subject, not a nested
            // grandchild.
            var direct = Parse(@"<div id=""x""><span></span></div>");
            var nested = Parse(@"<div id=""x""><section><span></span></section></div>");
            Assert.That(Match("div:has(> span)", ById(direct, "x")), Is.True);
            Assert.That(Match("div:has(> span)", ById(nested, "x")), Is.False);
        }

        [Test]
        public void Has_with_sibling_combinator() {
            var hit = Parse(@"<div><div id=""x""></div><span></span></div>");
            var miss = Parse(@"<div><div id=""x""></div></div>");
            Assert.That(Match("div:has(+ span)", ById(hit, "x")), Is.True);
            Assert.That(Match("div:has(+ span)", ById(miss, "x")), Is.False);
        }

        [Test]
        public void Has_nested_with_not() {
            // div:has(:not(.exclude)) is true if ANY descendant lacks class
            // "exclude". (A child element with .exclude itself contributes a
            // text-less, .exclude descendant; we use a clean tree so the
            // positive case is unambiguous.)
            var hit = Parse(@"<div id=""x""><span></span></div>");
            var miss = Parse(@"<div id=""x""></div>");
            Assert.That(Match("div:has(:not(.exclude))", ById(hit, "x")), Is.True);
            // No descendants at all → :has matches nothing → false.
            Assert.That(Match("div:has(:not(.exclude))", ById(miss, "x")), Is.False);
        }

        [Test]
        public void Has_with_pseudo_class() {
            // li:has(.checkbox:checked) — :checked is satisfied by the
            // checked attribute on a descendant input.
            var hit = Parse(@"<ul><li id=""x""><input class=""checkbox"" checked></li></ul>");
            var miss = Parse(@"<ul><li id=""x""><input class=""checkbox""></li></ul>");
            Assert.That(Match("li:has(.checkbox:checked)", ById(hit, "x")), Is.True);
            Assert.That(Match("li:has(.checkbox:checked)", ById(miss, "x")), Is.False);
        }

        // ----- Pseudo-class + pseudo-element compounds -----

        [Test]
        public void Pseudo_element_after_pseudo_class() {
            // .x:hover::before is intended to style the ::before of a hovered
            // .x element. The CSS Selectors matcher's regular Matches() entry
            // point returns false for any selector with a pseudo-element
            // (// v1: pseudo-element routing goes through MatchesPseudoElement,
            // which is what the cascade calls for ::before/::after origin
            // elements). MatchesPseudoElement performs the structural match.
            var doc = Parse(@"<div class=""x"" id=""x""></div><div class=""x"" id=""y""></div>");
            var hovered = ById(doc, "x");
            var idle = ById(doc, "y");
            var state = new FakeState();
            state.Set(hovered, ElementState.Hover);

            var sel = SelectorParser.Parse(".x:hover::before");
            // v1: Matches() rejects pseudo-element selectors outright.
            Assert.That(SelectorMatcher.Matches(sel, hovered, state), Is.False);
            // The actual pseudo-element origin match goes through
            // MatchesPseudoElement.
            Assert.That(SelectorMatcher.MatchesPseudoElement(sel, "before", hovered, state), Is.True);
            Assert.That(SelectorMatcher.MatchesPseudoElement(sel, "before", idle, state), Is.False);
        }

        [Test]
        public void Multiple_pseudo_classes_compound() {
            var doc = Parse(@"<div class=""x"" id=""hf""></div><div class=""x"" id=""h""></div><div class=""x"" id=""n""></div>");
            var hf = ById(doc, "hf");
            var hOnly = ById(doc, "h");
            var none = ById(doc, "n");
            var state = new FakeState();
            state.Set(hf, ElementState.Hover | ElementState.Focus);
            state.Set(hOnly, ElementState.Hover);

            Assert.That(Match(".x:hover:focus", hf, state), Is.True);
            Assert.That(Match(".x:hover:focus", hOnly, state), Is.False);
            Assert.That(Match(".x:hover:focus", none, state), Is.False);
        }

        // ----- Specificity in nested constructs -----

        [Test]
        public void Specificity_is_takes_highest_arg_specificity() {
            // :is(.a, #b) → max((0,1,0), (1,0,0)) = (1,0,0).
            Assert.That(S(":is(.a, #b)"), Is.EqualTo(new Specificity(1, 0, 0)));
        }

        [Test]
        public void Specificity_not_takes_arg_specificity() {
            Assert.That(S(":not(.a)"), Is.EqualTo(new Specificity(0, 1, 0)));
        }

        [Test]
        public void Specificity_where_is_zero() {
            // :where() always contributes zero specificity regardless of args.
            Assert.That(S(":where(#id, .class, type)"), Is.EqualTo(new Specificity(0, 0, 0)));
        }

        [Test]
        public void Specificity_pseudo_element_double_colon_counts_as_pseudo_element() {
            // ::before contributes (0,0,1) — a single pseudo-element bumps C.
            Assert.That(S("::before"), Is.EqualTo(new Specificity(0, 0, 1)));
            // And the bump composes with the preceding compound.
            Assert.That(S("p::before"), Is.EqualTo(new Specificity(0, 0, 2)));
            Assert.That(S(".x::before"), Is.EqualTo(new Specificity(0, 1, 1)));
        }

        // ----- Combinator + pseudo -----

        [Test]
        public void Child_combinator_after_pseudo_class() {
            // :hover > .item — direct child of a hovered element.
            var doc = Parse(@"<section id=""h""><div class=""item"" id=""i""></div><div><div class=""item"" id=""nested""></div></div></section>");
            var hoverHost = ById(doc, "h");
            var directItem = ById(doc, "i");
            var nestedItem = ById(doc, "nested");
            var state = new FakeState();
            state.Set(hoverHost, ElementState.Hover);

            Assert.That(Match(":hover > .item", directItem, state), Is.True);
            Assert.That(Match(":hover > .item", nestedItem, state), Is.False);
        }

        [Test]
        public void Sibling_combinator_after_pseudo_class() {
            // :checked ~ .label — labels following a checked input.
            var doc = Parse(@"<div><input id=""c"" checked><span class=""label"" id=""after""></span><span class=""label"" id=""after2""></span></div><div><span class=""label"" id=""orphan""></span></div>");
            Assert.That(Match(":checked ~ .label", ById(doc, "after")), Is.True);
            Assert.That(Match(":checked ~ .label", ById(doc, "after2")), Is.True);
            Assert.That(Match(":checked ~ .label", ById(doc, "orphan")), Is.False);
        }

        // ----- Universal selector with classes -----

        [Test]
        public void Universal_compound_with_class() {
            // *.x and .x match the same elements.
            var doc = Parse(@"<div class=""x"" id=""hit""></div><div id=""miss""></div>");
            var hit = ById(doc, "hit");
            var miss = ById(doc, "miss");
            Assert.That(Match("*.x", hit), Is.True);
            Assert.That(Match(".x", hit), Is.True);
            Assert.That(Match("*.x", miss), Is.False);
            Assert.That(Match(".x", miss), Is.False);
        }

        [Test]
        public void Universal_in_descendant_chain() {
            // .a * .b — .b must be a descendant of some element, which in
            // turn is a descendant of .a. The intermediate * is greedily
            // bound to the .b's immediate parent by the matcher, so .b
            // being a direct child of .a does NOT match — there has to be
            // an intermediate element in between.
            // v1: descendant combinator matching is greedy (no backtracking);
            // this is consistent with most CSS engines but worth noting.
            var twoLevel = Parse(@"<div class=""a""><section><span class=""b"" id=""ok""></span></section></div>");
            var directOnly = Parse(@"<div class=""a""><span class=""b"" id=""only""></span></div>");
            Assert.That(Match(".a * .b", ById(twoLevel, "ok")), Is.True);
            // v1: greedy descendant binding consumes the only intermediate
            // ancestor for `*`, so a direct `.a > .b` parent/child layout
            // fails because no `.a` ancestor remains above the `*` match.
            Assert.That(Match(".a * .b", ById(directOnly, "only")), Is.False);
        }
    }
}
