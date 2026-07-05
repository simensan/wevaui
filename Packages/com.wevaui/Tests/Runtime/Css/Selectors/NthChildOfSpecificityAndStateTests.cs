using System.Collections.Generic;
using NUnit.Framework;
using Weva.Compiled;
using Weva.Css.Selectors;

namespace Weva.Tests.Css.Selectors {
    // C9b — specificity adjustment and state-dependency tracking for
    // `:nth-child(An+B of <selector-list>)` / `:nth-last-child(An+B of S)`.
    //
    // Per CSS Selectors L4 §6.6.5:
    //   specificity(:nth-child(An+B of S)) = (0,1,0) + max-specificity(S)
    //
    // State tracking: if S contains a stateful pseudo-class (e.g. :hover),
    // the selector's state-dependency mask must include those bits, AND the
    // selector must require global-state invalidation because a sibling's
    // state flip changes the filtered index of the subject.
    public class NthChildOfSpecificityAndStateTests {
        static Specificity Spec(string sel) => SelectorParser.Parse(sel).Specificity;
        static StateSelectorIndex BuildIndex(params string[] selectors) {
            var sels = new List<CompiledSelector>();
            foreach (var s in selectors) sels.Add(SelectorParser.Parse(s));
            return new StateSelectorIndex(sels);
        }

        // ---- Specificity tests -----------------------------------------------

        [Test]
        public void NthChild_no_filter_has_base_specificity() {
            // No `of S` — plain :nth-child contributes (0,1,0) as a pseudo-class.
            Assert.That(Spec(":nth-child(2)"), Is.EqualTo(new Specificity(0, 1, 0)));
        }

        [Test]
        public void NthLastChild_no_filter_has_base_specificity() {
            Assert.That(Spec(":nth-last-child(2)"), Is.EqualTo(new Specificity(0, 1, 0)));
        }

        [Test]
        public void NthChild_of_class_filter_adds_class_specificity() {
            // :nth-child(1 of .x) = (0,1,0) [nth] + (0,1,0) [.x] = (0,2,0)
            Assert.That(Spec(":nth-child(1 of .x)"), Is.EqualTo(new Specificity(0, 2, 0)));
        }

        [Test]
        public void NthLastChild_of_class_filter_adds_class_specificity() {
            Assert.That(Spec(":nth-last-child(1 of .x)"), Is.EqualTo(new Specificity(0, 2, 0)));
        }

        [Test]
        public void NthChild_of_id_filter_adds_id_specificity() {
            // :nth-child(1 of #hero) = (0,1,0) + (1,0,0) = (1,1,0)
            Assert.That(Spec(":nth-child(1 of #hero)"), Is.EqualTo(new Specificity(1, 1, 0)));
        }

        [Test]
        public void NthChild_of_type_filter_adds_type_specificity() {
            // :nth-child(1 of div) = (0,1,0) + (0,0,1) = (0,1,1)
            Assert.That(Spec(":nth-child(1 of div)"), Is.EqualTo(new Specificity(0, 1, 1)));
        }

        [Test]
        public void NthChild_of_compound_filter_uses_max_not_sum() {
            // :nth-child(1 of .x, #y) — two selectors in the list; max specificity
            // is (1,0,0) from #y, not the sum (1,1,0).
            // Result: (0,1,0) + (1,0,0) = (1,1,0).
            Assert.That(Spec(":nth-child(1 of .x, #y)"), Is.EqualTo(new Specificity(1, 1, 0)));
        }

        [Test]
        public void NthChild_of_filter_specificity_combines_with_outer_compound() {
            // `li:nth-child(1 of .x)` — `li` contributes (0,0,1), the pseudo
            // contributes (0,2,0), total is (0,2,1).
            Assert.That(Spec("li:nth-child(1 of .x)"), Is.EqualTo(new Specificity(0, 2, 1)));
        }

        [Test]
        public void NthOfType_no_filter_path_unaffected() {
            // :nth-of-type does not accept `of S` — must stay at (0,1,0).
            Assert.That(Spec(":nth-of-type(2)"), Is.EqualTo(new Specificity(0, 1, 0)));
        }

        // ---- State-dependency bit tests -------------------------------------

        [Test]
        public void NthChild_of_structural_filter_contributes_no_state_bits() {
            // :nth-child(1 of .x) — class filter is structural; no state bits.
            var sel = SelectorParser.Parse(":nth-child(1 of .x)");
            Assert.That(SelectorStateDependencies.GetStateBits(sel),
                Is.EqualTo(ElementState.None),
                ":nth-child(1 of .x) has no stateful pseudo, state bits must be None");
        }

        [Test]
        public void NthChild_of_hover_filter_propagates_hover_bit() {
            // :nth-child(1 of :hover) — the filter tests :hover on siblings; the
            // selector's state-dependency mask must include the Hover bit.
            var sel = SelectorParser.Parse(":nth-child(1 of :hover)");
            Assert.That((SelectorStateDependencies.GetStateBits(sel) & ElementState.Hover)
                        != ElementState.None,
                ":nth-child(1 of :hover) must contribute the Hover state bit");
        }

        [Test]
        public void NthLastChild_of_checked_filter_propagates_checked_bit() {
            var sel = SelectorParser.Parse(":nth-last-child(2 of :checked)");
            Assert.That((SelectorStateDependencies.GetStateBits(sel) & ElementState.Checked)
                        != ElementState.None,
                ":nth-last-child(2 of :checked) must contribute the Checked state bit");
        }

        [Test]
        public void NthChild_of_hover_filter_included_in_index_global_mask() {
            var idx = BuildIndex(":nth-child(1 of :hover)");
            Assert.That(idx.AnySelectorTests(ElementState.Hover), Is.True,
                "global mask must include Hover for :nth-child(1 of :hover)");
            Assert.That(idx.SelectorsForState(ElementState.Hover), Has.Count.EqualTo(1));
        }

        // ---- RequiresGlobalStateInvalidation tests --------------------------

        [Test]
        public void NthChild_of_stateful_filter_requires_global_invalidation() {
            // A sibling's :hover flip changes the filtered set, which changes
            // the subject's filtered index — per-element digest cannot detect
            // this without a sibling walk. Must force global fallback.
            var idx = BuildIndex(":nth-child(1 of :hover)");
            Assert.That(idx.RequiresGlobalFallback, Is.True,
                ":nth-child(1 of :hover) must require global state invalidation");
        }

        [Test]
        public void NthLastChild_of_stateful_filter_requires_global_invalidation() {
            var idx = BuildIndex(":nth-last-child(1 of :checked)");
            Assert.That(idx.RequiresGlobalFallback, Is.True,
                ":nth-last-child(1 of :checked) must require global state invalidation");
        }

        [Test]
        public void NthChild_of_structural_filter_does_not_force_global_invalidation() {
            // Class/type/attr filters are structural — a class change on a
            // sibling requires a standard sibling-class-change invalidation (which
            // the attribute-changed path already covers), not a global state sweep.
            var idx = BuildIndex(":nth-child(1 of .active)");
            Assert.That(idx.RequiresGlobalFallback, Is.False,
                ":nth-child(1 of .active) must NOT force global state invalidation");
        }

        [Test]
        public void NthChild_no_filter_does_not_force_global_invalidation() {
            // Plain :nth-child has no filter at all; structural-only.
            var idx = BuildIndex(":nth-child(2)");
            Assert.That(idx.RequiresGlobalFallback, Is.False,
                ":nth-child(2) must NOT force global state invalidation");
        }

        [Test]
        public void NthChild_of_stateful_filter_indexed_under_correct_bit() {
            var idx = BuildIndex(":nth-child(1 of :focus)", ".other");
            // Selector index 0 is :nth-child; selector 1 is .other.
            var focusList = idx.SelectorsForState(ElementState.Focus);
            Assert.That(focusList, Has.Count.EqualTo(1),
                "only the :nth-child(1 of :focus) selector should be under the Focus bit");
        }
    }
}
