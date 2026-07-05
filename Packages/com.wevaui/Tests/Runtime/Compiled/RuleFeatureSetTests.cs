using System.Collections.Generic;
using NUnit.Framework;
using Weva.Compiled;
using Weva.Css.Selectors;

namespace Weva.Tests.Compiled {
    // Pins RuleFeatureSet's parse-time bucketing. Each test names a CSS
    // selector and asserts which buckets (subject / descendant / sibling)
    // get the selector's index for which features (class, attribute name,
    // state bit). These tests guard the invalidation contract the
    // cascade will later read against — a regression here silently
    // over- or under-invalidates.
    public class RuleFeatureSetTests {
        static RuleFeatureSet Build(params string[] selectors) {
            var sels = new List<CompiledSelector>();
            foreach (var s in selectors) sels.Add(SelectorParser.Parse(s));
            return new RuleFeatureSet(sels);
        }

        [Test]
        public void Simple_class_goes_into_subject_bucket() {
            // .card — single compound, classes live on the subject.
            var set = Build(".card");
            Assert.That(set.SubjectSelectorsForClass("card"), Is.EquivalentTo(new[] { 0 }));
            Assert.That(set.DescendantSelectorsForClass("card"), Is.Empty);
            Assert.That(set.SiblingSelectorsForClass("card"), Is.Empty);
        }

        [Test]
        public void Subject_state_pseudo_indexed_under_subject_state_bucket() {
            // .card:hover — :hover lives on subject. Hover state changing
            // on a .card element flips this rule's match on the card
            // itself, not on descendants.
            var set = Build(".card:hover");
            Assert.That(set.SubjectSelectorsForState(ElementState.Hover), Is.EquivalentTo(new[] { 0 }));
            Assert.That(set.DescendantSelectorsForState(ElementState.Hover), Is.Empty);
            Assert.That(set.SubjectStateMask, Is.EqualTo(ElementState.Hover));
        }

        [Test]
        public void Descendant_chain_buckets_ancestor_features_under_descendant() {
            // ".parent .child" — .parent is an ancestor, .child the subject.
            // A class change on a .parent element invalidates that
            // element's DESCENDANTS, not the element itself.
            var set = Build(".parent .child");
            Assert.That(set.SubjectSelectorsForClass("child"), Is.EquivalentTo(new[] { 0 }));
            Assert.That(set.DescendantSelectorsForClass("parent"), Is.EquivalentTo(new[] { 0 }));
            // .parent must NOT show up in the subject bucket — otherwise
            // changing .parent on any element would falsely invalidate
            // that element itself.
            Assert.That(set.SubjectSelectorsForClass("parent"), Is.Empty);
            Assert.That(set.SiblingSelectorsForClass("parent"), Is.Empty);
        }

        [Test]
        public void Ancestor_state_pseudo_goes_to_descendant_state_bucket() {
            // ".parent:hover .child" — :hover lives on an ancestor
            // compound. The element whose :hover flips is the .parent;
            // descendants need re-eval, the ancestor itself does NOT.
            var set = Build(".parent:hover .child");
            Assert.That(set.DescendantSelectorsForState(ElementState.Hover), Is.EquivalentTo(new[] { 0 }));
            Assert.That(set.SubjectSelectorsForState(ElementState.Hover), Is.Empty);
            Assert.That(set.DescendantStateMask, Is.EqualTo(ElementState.Hover));
        }

        [Test]
        public void Adjacent_sibling_chain_buckets_left_compound_as_sibling() {
            // ".prev + .next" — .prev sits LEFT of an adjacent-sibling
            // combinator. A change on a .prev element invalidates its
            // adjacent sibling, not the element itself.
            var set = Build(".prev + .next");
            Assert.That(set.SubjectSelectorsForClass("next"), Is.EquivalentTo(new[] { 0 }));
            Assert.That(set.SiblingSelectorsForClass("prev"), Is.EquivalentTo(new[] { 0 }));
            Assert.That(set.DescendantSelectorsForClass("prev"), Is.Empty);
        }

        [Test]
        public void General_sibling_state_bucketed_as_sibling_state() {
            // ".prev:hover ~ .next" — :hover on a sibling-of-subject
            // compound. Subject mask should NOT carry Hover.
            var set = Build(".prev:hover ~ .next");
            Assert.That(set.SiblingSelectorsForState(ElementState.Hover), Is.EquivalentTo(new[] { 0 }));
            Assert.That(set.SubjectSelectorsForState(ElementState.Hover), Is.Empty);
            Assert.That(set.SiblingStateMask, Is.EqualTo(ElementState.Hover));
        }

        [Test]
        public void Sibling_combinator_then_descendant_buckets_each_compound_by_its_own_chain() {
            // ".a + .b > .c" — .c subject; .b is .c's parent (Child
            // combinator from .b to .c); .a is .b's adjacent prev sibling
            // (AdjacentSibling from .a to .b).
            //
            // .b's chain to subject is just Child → DESCENDANT (a class
            //   change on a .b element invalidates that element's child
            //   subtree, not its siblings).
            // .a's chain to subject crosses the AdjacentSibling
            //   combinator → SIBLING (a class change on .a invalidates
            //   .a's next sibling's subtree).
            var set = Build(".a + .b > .c");
            Assert.That(set.SubjectSelectorsForClass("c"), Is.EquivalentTo(new[] { 0 }));
            Assert.That(set.DescendantSelectorsForClass("b"), Is.EquivalentTo(new[] { 0 }));
            Assert.That(set.SiblingSelectorsForClass("a"), Is.EquivalentTo(new[] { 0 }));
            Assert.That(set.SiblingSelectorsForClass("b"), Is.Empty);
            Assert.That(set.DescendantSelectorsForClass("a"), Is.Empty);
        }

        [Test]
        public void Attribute_selector_indexed_under_subject_attribute_bucket() {
            // [data-x] on subject — an attribute mutation on an element
            // with this rule could flip its match.
            var set = Build(".btn[data-x=\"foo\"]");
            Assert.That(set.SubjectSelectorsForAttribute("data-x"), Is.EquivalentTo(new[] { 0 }));
            Assert.That(set.SubjectSelectorsForClass("btn"), Is.EquivalentTo(new[] { 0 }));
        }

        [Test]
        public void Multiple_selectors_share_buckets_by_feature() {
            var set = Build(
                ".card:hover",            // idx 0
                ".card:focus",            // idx 1
                ".outer .card",           // idx 2
                ".outer + .card",         // idx 3
                ".unrelated");            // idx 4
            // All 4 .card rules write index into SubjectSelectorsForClass("card").
            Assert.That(set.SubjectSelectorsForClass("card"), Is.EquivalentTo(new[] { 0, 1, 2, 3 }));
            // States split per bit.
            Assert.That(set.SubjectSelectorsForState(ElementState.Hover), Is.EquivalentTo(new[] { 0 }));
            Assert.That(set.SubjectSelectorsForState(ElementState.Focus), Is.EquivalentTo(new[] { 1 }));
            // .outer appears as descendant (idx 2) AND sibling (idx 3).
            Assert.That(set.DescendantSelectorsForClass("outer"), Is.EquivalentTo(new[] { 2 }));
            Assert.That(set.SiblingSelectorsForClass("outer"), Is.EquivalentTo(new[] { 3 }));
            Assert.That(set.SubjectSelectorsForClass("unrelated"), Is.EquivalentTo(new[] { 4 }));
        }

        [Test]
        public void Not_pseudo_with_inner_state_contributes_inner_state_bits() {
            // :not(:hover) on a subject compound contributes Hover to the
            // subject state bucket — a Hover flip on a matching element
            // can flip :not(:hover)'s result, so it's still a real
            // dependency.
            var set = Build(".btn:not(:hover)");
            Assert.That(set.SubjectSelectorsForState(ElementState.Hover), Is.EquivalentTo(new[] { 0 }));
            Assert.That(set.SubjectStateMask, Is.EqualTo(ElementState.Hover));
        }

        [Test]
        public void Is_pseudo_aggregates_inner_state_bits() {
            // :is(:hover, :focus) — both Hover and Focus appear as
            // subject-state dependencies.
            var set = Build(".btn:is(:hover, :focus)");
            Assert.That(set.SubjectSelectorsForState(ElementState.Hover), Is.EquivalentTo(new[] { 0 }));
            Assert.That(set.SubjectSelectorsForState(ElementState.Focus), Is.EquivalentTo(new[] { 0 }));
            Assert.That(set.SubjectStateMask & (ElementState.Hover | ElementState.Focus),
                Is.EqualTo(ElementState.Hover | ElementState.Focus));
        }

        [Test]
        public void Empty_selector_list_produces_empty_index() {
            var set = new RuleFeatureSet(new List<CompiledSelector>());
            Assert.That(set.SubjectStateMask, Is.EqualTo(ElementState.None));
            Assert.That(set.DescendantStateMask, Is.EqualTo(ElementState.None));
            Assert.That(set.SiblingStateMask, Is.EqualTo(ElementState.None));
            Assert.That(set.SubjectSelectorsForClass("anything"), Is.Empty);
        }

        [Test]
        public void Null_selector_list_doesnt_throw() {
            Assert.DoesNotThrow(() => new RuleFeatureSet(null));
        }
    }
}
