using System.Collections.Generic;
using NUnit.Framework;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Dom;
using Weva.Parsing;

namespace Weva.Tests.Css.Selectors {
    public class HasSelectorTests {
        static Document Html(string s) => HtmlParser.Parse(s);
        static Stylesheet Css(string s) => CssParser.Parse(s);
        static OriginatedStylesheet Author(string s) => OriginatedStylesheet.Author(Css(s));

        static Element ById(Document doc, string id) => doc.GetElementById(id);

        static bool Match(string selector, Element e) {
            var sel = SelectorParser.Parse(selector);
            return SelectorMatcher.Matches(sel, e);
        }

        [Test]
        public void Has_descendant_matches_card_with_img_descendant() {
            var doc = Html(@"<div class=""card"" id=""x""><div><img></div></div>");
            Assert.That(Match(".card:has(img)", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Has_descendant_does_not_match_card_without_img() {
            var doc = Html(@"<div class=""card"" id=""x""><p>no image</p></div>");
            Assert.That(Match(".card:has(img)", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Has_direct_child_matches_only_direct_child() {
            var doc = Html(@"<div class=""card"" id=""x""><img></div>");
            Assert.That(Match(".card:has(> img)", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Has_direct_child_does_not_match_nested_descendant() {
            var doc = Html(@"<div class=""card"" id=""x""><div><img></div></div>");
            Assert.That(Match(".card:has(> img)", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Has_adjacent_sibling_matches_when_next_sibling_matches() {
            var doc = Html(@"<div><div class=""card"" id=""x""></div><div class=""footer""></div></div>");
            Assert.That(Match(".card:has(+ .footer)", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Has_adjacent_sibling_no_match_when_no_next_sibling() {
            var doc = Html(@"<div><div class=""card"" id=""x""></div></div>");
            Assert.That(Match(".card:has(+ .footer)", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Has_general_sibling_matches_following_sibling() {
            var doc = Html(@"<div><div class=""card"" id=""x""></div><p></p><div class=""footer""></div></div>");
            Assert.That(Match(".card:has(~ .footer)", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Multiple_has_on_same_selector_AND() {
            var doc = Html(@"<div class=""card"" id=""x""><img><span class=""tag""></span></div>");
            Assert.That(Match(".card:has(img):has(.tag)", ById(doc, "x")), Is.True);
            Assert.That(Match(".card:has(img):has(.missing)", ById(doc, "x")), Is.False);
        }

        [Test]
        public void Has_with_compound_inner_selector() {
            var doc = Html(@"<div class=""card"" id=""x""><span class=""accent""></span></div>");
            Assert.That(Match(".card:has(span.accent)", ById(doc, "x")), Is.True);
        }

        [Test]
        public void Has_inside_has_is_rejected() {
            Assert.Throws<SelectorParseException>(() => SelectorParser.Parse(".x:has(:has(img))"));
        }

        [Test]
        public void Has_in_cascade_applies_styles_to_parent() {
            var doc = Html(@"<div class=""card"" id=""x""><img></div>");
            var engine = new CascadeEngine(new[] { Author(".card:has(img) { color: red; }") });
            var cs = engine.Compute(ById(doc, "x"));
            Assert.That(cs.Get("color"), Is.EqualTo("red"));
        }

        [Test]
        public void Has_no_match_in_cascade_falls_through() {
            var doc = Html(@"<div class=""card"" id=""x""></div>");
            var engine = new CascadeEngine(new[] { Author(".card:has(img) { color: red; }") });
            var cs = engine.Compute(ById(doc, "x"));
            // No img descendant — rule doesn't apply, fall back to initial.
            Assert.That(cs.Get("color"), Is.EqualTo("black"));
        }

        [Test]
        public void Has_engine_flags_sensitivity_for_has_sheets() {
            var engine = new CascadeEngine(new[] { Author(".card:has(img) { color: red; }") });
            Assert.That(engine.HasAnyHasSelector, Is.True);
        }

        [Test]
        public void Has_engine_does_not_flag_for_plain_sheets() {
            var engine = new CascadeEngine(new[] { Author(".card { color: red; }") });
            Assert.That(engine.HasAnyHasSelector, Is.False);
        }

        // A7 — :has(...) traversal must be anchored at the subject and walk
        // outward (down / right). The matcher must NOT consult the subject's
        // ancestors, because that would let `.subject:has(.x)` match when only
        // an ancestor (not a descendant) carries `.x`. Regression test pins
        // descendant scope to the subject's own subtree.
        [Test]
        public void Has_descendant_does_not_match_when_only_ancestor_has_class_A7() {
            var doc = Html(@"<div class=""x""><section><div id=""subject""></div></section></div>");
            // .x is on the GRANDPARENT of #subject, not in its subtree.
            // `:has(.x)` must NOT match #subject — it has no `.x` descendant.
            Assert.That(Match("#subject:has(.x)", ById(doc, "subject")), Is.False);
            // Sanity: when .x IS a descendant, the same selector matches.
            var doc2 = Html(@"<div id=""subject2""><section><div class=""x""></div></section></div>");
            Assert.That(Match("#subject2:has(.x)", ById(doc2, "subject2")), Is.True);
        }

        // A7 — `:has(> .x)` must match only when a DIRECT child of the subject
        // carries `.x`; a deeper descendant must not satisfy the relation.
        [Test]
        public void Has_child_combinator_rejects_grandchild_A7() {
            var grandchildOnly = Html(@"<div id=""subject""><section><div class=""x""></div></section></div>");
            Assert.That(Match("#subject:has(> .x)", ById(grandchildOnly, "subject")), Is.False);
            var directChild = Html(@"<div id=""subject2""><div class=""x""></div></div>");
            Assert.That(Match("#subject2:has(> .x)", ById(directChild, "subject2")), Is.True);
        }

        // A7 — `:has(+ .x)` must match only when the SUBJECT's immediate next
        // sibling carries `.x`; an intervening sibling between subject and the
        // .x element must defeat the match. Also asserts the matcher does not
        // accidentally reach the subject's parent or other siblings via an
        // upward walk.
        [Test]
        public void Has_adjacent_sibling_requires_immediate_next_sibling_A7() {
            var notImmediate = Html(@"<div><div id=""subject""></div><p></p><div class=""x""></div></div>");
            Assert.That(Match("#subject:has(+ .x)", ById(notImmediate, "subject")), Is.False);
            var immediate = Html(@"<div><div id=""subject2""></div><div class=""x""></div></div>");
            Assert.That(Match("#subject2:has(+ .x)", ById(immediate, "subject2")), Is.True);
            // Sanity: a previous sibling with `.x` must NOT satisfy `:has(+ .x)`
            // — the relation is strictly forward-from-subject.
            var prevSiblingOnly = Html(@"<div><div class=""x""></div><div id=""subject3""></div></div>");
            Assert.That(Match("#subject3:has(+ .x)", ById(prevSiblingOnly, "subject3")), Is.False);
        }

        // A7 — Multi-compound `:has(> X > Y)` must match the WHOLE relative
        // chain anchored at the subject: subject > X-element > Y-element. The
        // old implementation re-ran MatchSequence on the candidate, which
        // walks Parent upward — so `:has(> X > Y)` could accidentally match
        // when subject itself satisfied X (i.e. it would consume subject as
        // part of the chain via the parent walk). This test pins the spec
        // semantics.
        [Test]
        public void Has_multi_compound_child_chain_anchored_at_subject_A7() {
            var positive = Html(@"<div id=""subject""><div class=""mid""><span class=""leaf""></span></div></div>");
            Assert.That(Match("#subject:has(> .mid > .leaf)", ById(positive, "subject")), Is.True);

            // .mid is a grandchild of subject (not a direct child), so the
            // `>` chain must fail even though .leaf is a child of .mid.
            var midIsGrandchild = Html(@"<div id=""subject2""><section><div class=""mid""><span class=""leaf""></span></div></section></div>");
            Assert.That(Match("#subject2:has(> .mid > .leaf)", ById(midIsGrandchild, "subject2")), Is.False);

            // .leaf is a grandchild of .mid (not a direct child), so the
            // second `>` step must fail.
            var leafIsGrandchild = Html(@"<div id=""subject3""><div class=""mid""><span><i class=""leaf""></i></span></div></div>");
            Assert.That(Match("#subject3:has(> .mid > .leaf)", ById(leafIsGrandchild, "subject3")), Is.False);

            // Negative regression: with the buggy upward walk, `:has(> .mid > .leaf)`
            // could accidentally match a subject whose PARENT is `.mid` (because
            // the inner MatchSequence walks Parent backward). Pin that this does
            // not happen.
            var subjectHasMidParent = Html(@"<div class=""mid""><div id=""subject4""><span class=""leaf""></span></div></div>");
            Assert.That(Match("#subject4:has(> .mid > .leaf)", ById(subjectHasMidParent, "subject4")), Is.False);
        }

        // A7 — Multi-compound `:has(+ X Y)`: subject's adjacent sibling X must
        // contain a descendant Y. Pins both the leading `+` (immediate next
        // sibling) and the trailing descendant (any descendant of X).
        [Test]
        public void Has_adjacent_sibling_then_descendant_chain_anchored_at_subject_A7() {
            var positive = Html(@"<div><div id=""subject""></div><section class=""sib""><div><span class=""leaf""></span></div></section></div>");
            Assert.That(Match("#subject:has(+ .sib .leaf)", ById(positive, "subject")), Is.True);

            // .sib is NOT the immediate next sibling — there's a `<p>` between.
            var notImmediateSib = Html(@"<div><div id=""subject2""></div><p></p><section class=""sib""><span class=""leaf""></span></section></div>");
            Assert.That(Match("#subject2:has(+ .sib .leaf)", ById(notImmediateSib, "subject2")), Is.False);

            // .leaf lives in a different sibling, not inside .sib.
            var leafInWrongSib = Html(@"<div><div id=""subject3""></div><section class=""sib""></section><span class=""leaf""></span></div>");
            Assert.That(Match("#subject3:has(+ .sib .leaf)", ById(leafInWrongSib, "subject3")), Is.False);
        }
    }
}
