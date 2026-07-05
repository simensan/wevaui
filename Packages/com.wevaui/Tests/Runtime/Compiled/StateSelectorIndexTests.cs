using System.Collections.Generic;
using NUnit.Framework;
using Weva.Compiled;
using Weva.Css.Selectors;

namespace Weva.Tests.Compiled {
    public class StateSelectorIndexTests {
        static StateSelectorIndex BuildIndex(params string[] selectors) {
            var sels = new List<CompiledSelector>();
            foreach (var s in selectors) sels.Add(SelectorParser.Parse(s));
            return new StateSelectorIndex(sels);
        }

        [Test]
        public void Hover_only_selector_indexed_under_hover_bit() {
            var idx = BuildIndex(".button:hover");
            Assert.That(idx.GlobalStateMask, Is.EqualTo(ElementState.Hover));
            Assert.That(idx.SelectorsForState(ElementState.Hover), Is.EquivalentTo(new[] { 0 }));
        }

        [Test]
        public void Compound_focus_and_active_indexed_under_both_bits() {
            var idx = BuildIndex(".button:focus.active");
            // .button:focus.active — :focus on rightmost compound, .active is a class.
            // Only :focus contributes to the state bits.
            Assert.That(idx.GlobalStateMask, Is.EqualTo(ElementState.Focus));
        }

        [Test]
        public void Compound_focus_and_active_pseudos_indexed_under_both() {
            var idx = BuildIndex(":focus:active");
            Assert.That(idx.GlobalStateMask, Is.EqualTo(ElementState.Focus | ElementState.Active));
            Assert.That(idx.SelectorsForState(ElementState.Focus), Has.Count.EqualTo(1));
            Assert.That(idx.SelectorsForState(ElementState.Active), Has.Count.EqualTo(1));
        }

        [Test]
        public void Selector_without_pseudo_state_not_indexed() {
            var idx = BuildIndex(".button");
            Assert.That(idx.GlobalStateMask, Is.EqualTo(ElementState.None));
            Assert.That(idx.SelectorsForState(ElementState.Hover), Is.Empty);
        }

        [Test]
        public void Not_pseudo_propagates_inner_state_bit() {
            var idx = BuildIndex(":not(:hover)");
            // A flip on hover changes whether :not(:hover) matches; the bit must be tracked.
            Assert.That(idx.GlobalStateMask, Is.EqualTo(ElementState.Hover));
        }

        [Test]
        public void Compound_hover_focus_indexed_under_both() {
            var idx = BuildIndex(":hover:focus");
            Assert.That((idx.GlobalStateMask & ElementState.Hover) != 0);
            Assert.That((idx.GlobalStateMask & ElementState.Focus) != 0);
        }

        [Test]
        public void Empty_selector_list_yields_empty_index() {
            var idx = new StateSelectorIndex(new List<CompiledSelector>());
            Assert.That(idx.GlobalStateMask, Is.EqualTo(ElementState.None));
            Assert.That(idx.RequiresGlobalFallback, Is.False);
            Assert.That(idx.SelectorsForState(ElementState.Hover), Is.Empty);
        }

        [Test]
        public void Multiple_selectors_union_their_state_bits() {
            var idx = BuildIndex(".a:hover", ".b:focus", ".c:active");
            Assert.That(idx.GlobalStateMask,
                Is.EqualTo(ElementState.Hover | ElementState.Focus | ElementState.Active));
            Assert.That(idx.SelectorsForState(ElementState.Hover), Is.EquivalentTo(new[] { 0 }));
            Assert.That(idx.SelectorsForState(ElementState.Focus), Is.EquivalentTo(new[] { 1 }));
            Assert.That(idx.SelectorsForState(ElementState.Active), Is.EquivalentTo(new[] { 2 }));
        }

        [Test]
        public void Descendant_combinator_with_state_on_left_does_not_force_fallback() {
            // `.parent:hover .child` — descendant combinator, state on left compound.
            // The cascade is parent-first; ancestor recompute bumps parentStyle.Version
            // which propagates to descendants. Sibling combinators are the only case
            // requiring global fallback.
            var idx = BuildIndex(".parent:hover .child");
            Assert.That(idx.GlobalStateMask, Is.EqualTo(ElementState.Hover));
            Assert.That(idx.RequiresGlobalFallback, Is.False);
        }

        [Test]
        public void Adjacent_sibling_combinator_with_state_on_left_forces_fallback() {
            // `.btn:hover + .next` — sibling combinator with state on left compound.
            // Per-element digest cannot detect "my left sibling's state changed";
            // index marks this as requiring global fallback.
            var idx = BuildIndex(".btn:hover + .next");
            Assert.That(idx.GlobalStateMask, Is.EqualTo(ElementState.Hover));
            Assert.That(idx.RequiresGlobalFallback, Is.True);
        }

        [Test]
        public void General_sibling_combinator_with_state_on_left_forces_fallback() {
            var idx = BuildIndex(".btn:focus ~ .other");
            Assert.That(idx.RequiresGlobalFallback, Is.True);
        }

        [Test]
        public void Sibling_combinator_with_state_only_on_right_does_not_force_fallback() {
            // State on the rightmost compound is unambiguously about the matched
            // element itself; the per-element digest sees it directly.
            var idx = BuildIndex(".btn + .next:hover");
            Assert.That(idx.GlobalStateMask, Is.EqualTo(ElementState.Hover));
            Assert.That(idx.RequiresGlobalFallback, Is.False);
        }

        [Test]
        public void Is_pseudo_propagates_inner_state_bits() {
            var idx = BuildIndex(":is(:hover, :focus)");
            Assert.That((idx.GlobalStateMask & ElementState.Hover) != 0);
            Assert.That((idx.GlobalStateMask & ElementState.Focus) != 0);
        }

        [Test]
        public void Where_pseudo_propagates_inner_state_bits() {
            var idx = BuildIndex(":where(:checked)");
            Assert.That((idx.GlobalStateMask & ElementState.Checked) != 0);
        }

        [Test]
        public void Disabled_and_checked_are_state_bits() {
            var idx = BuildIndex(":disabled", ":checked");
            Assert.That((idx.GlobalStateMask & ElementState.Disabled) != 0);
            Assert.That((idx.GlobalStateMask & ElementState.Checked) != 0);
        }

        [Test]
        public void Placeholder_shown_is_a_state_bit() {
            var idx = BuildIndex(":placeholder-shown");
            Assert.That((idx.GlobalStateMask & ElementState.PlaceholderShown) != 0);
        }

        [Test]
        public void Structural_pseudo_is_not_a_state_bit() {
            // :first-child / :nth-of-type / :empty are structural — they depend on
            // DOM shape, not interactive state. They don't contribute bits.
            var idx = BuildIndex(":first-child", ":nth-of-type(2)", ":empty");
            Assert.That(idx.GlobalStateMask, Is.EqualTo(ElementState.None));
        }

        [Test]
        public void AnySelectorTests_returns_true_for_indexed_bit() {
            var idx = BuildIndex(":hover");
            Assert.That(idx.AnySelectorTests(ElementState.Hover), Is.True);
            Assert.That(idx.AnySelectorTests(ElementState.Focus), Is.False);
        }

        [Test]
        public void AnySelectorTests_returns_true_for_any_overlap_in_mask() {
            var idx = BuildIndex(":hover");
            Assert.That(idx.AnySelectorTests(ElementState.Hover | ElementState.Focus), Is.True);
        }

        [Test]
        public void Multiple_selectors_share_a_bit_bucket() {
            var idx = BuildIndex(":hover", ".a:hover", "div:hover");
            var hoverList = idx.SelectorsForState(ElementState.Hover);
            Assert.That(hoverList, Is.EquivalentTo(new[] { 0, 1, 2 }));
        }

        // A8 — `:has(<inner>)` must surface the inner list's stateful pseudo bits
        // into the subject's dependency mask so the StateSelectorIndex tracks
        // the dependency. Upward invalidation on descendant state flips is closed
        // by routing a stateful `:has` through the global-version fallback path
        // (RequiresGlobalFallback) — pinned by the *_forces_fallback_A8 tests below.

        [Test]
        public void Has_hover_propagates_hover_bit_A8() {
            var sel = SelectorParser.Parse(".card:has(:hover)");
            Assert.That((SelectorStateDependencies.GetStateBits(sel) & ElementState.Hover) != 0,
                ":has(:hover) must contribute the Hover state bit");
            var idx = BuildIndex(".card:has(:hover)");
            Assert.That(idx.AnySelectorTests(ElementState.Hover), Is.True);
            Assert.That(idx.SelectorsForState(ElementState.Hover), Has.Count.EqualTo(1));
        }

        [Test]
        public void Has_checked_propagates_checked_bit_A8() {
            var sel = SelectorParser.Parse(".form:has(.opt:checked)");
            Assert.That((SelectorStateDependencies.GetStateBits(sel) & ElementState.Checked) != 0,
                ":has(.opt:checked) must contribute the Checked state bit");
            var idx = BuildIndex(".form:has(.opt:checked)");
            Assert.That(idx.AnySelectorTests(ElementState.Checked), Is.True);
            Assert.That(idx.SelectorsForState(ElementState.Checked), Has.Count.EqualTo(1));
        }

        [Test]
        public void Is_wrapping_has_hover_propagates_hover_bit_A8() {
            // :is(.a, :has(:hover)) — :is recurses into its inner list, and
            // :has recurses into its relative-selector list. The composed
            // walker must reach the inner :hover.
            var sel = SelectorParser.Parse(":is(.a, :has(:hover))");
            Assert.That((SelectorStateDependencies.GetStateBits(sel) & ElementState.Hover) != 0,
                ":is(.a, :has(:hover)) must contribute the Hover state bit");
            var idx = BuildIndex(":is(.a, :has(:hover))");
            Assert.That(idx.AnySelectorTests(ElementState.Hover), Is.True);
        }

        [Test]
        public void Not_wrapping_has_focus_propagates_focus_bit_A8() {
            // Defensive: :not is implemented via the same InnerList path; a
            // flip on the inner :focus changes the negation result, so the
            // dependency must propagate through both layers.
            var sel = SelectorParser.Parse(":not(:has(:focus))");
            Assert.That((SelectorStateDependencies.GetStateBits(sel) & ElementState.Focus) != 0,
                ":not(:has(:focus)) must contribute the Focus state bit");
        }

        [Test]
        public void Has_with_no_stateful_pseudo_contributes_no_bits_A8() {
            // :has(img) keys off DOM structure (presence of a descendant img),
            // not interactive state. The state-dependency mask should remain
            // empty for the digest path — structural :has dependencies are
            // handled by the HasSensitive ancestor-walk in InvalidationTracker.
            var sel = SelectorParser.Parse(".card:has(img)");
            Assert.That(SelectorStateDependencies.GetStateBits(sel), Is.EqualTo(ElementState.None));
        }

        // A8 — a stateful `:has` makes the SUBJECT's match depend on a DESCENDANT's
        // state, which the per-element digest (keyed on the subject's own state)
        // can't observe. So the sheet must use the coarse global-version path.
        // These pin RequiresGlobalFallback for the stateful-:has cases and confirm
        // the structural / plain-state cases do NOT over-trigger it.

        [Test]
        public void Has_hover_forces_fallback_A8() {
            // `.card:has(:hover)` — a child hover flips .card's match; the digest
            // can't see the child's state, so the global path is required.
            var idx = BuildIndex(".card:has(:hover)");
            Assert.That(idx.RequiresGlobalFallback, Is.True);
            Assert.That(SelectorStateDependencies.RequiresGlobalStateInvalidation(
                SelectorParser.Parse(".card:has(:hover)")), Is.True);
        }

        [Test]
        public void Has_checked_forces_fallback_A8() {
            var idx = BuildIndex(".form:has(.opt:checked)");
            Assert.That(idx.RequiresGlobalFallback, Is.True);
        }

        [Test]
        public void Is_wrapping_has_hover_forces_fallback_A8() {
            // Nesting must be detected: :is(.a, :has(:hover)) still depends on
            // descendant hover and so must route through the global path.
            var idx = BuildIndex(":is(.a, :has(:hover))");
            Assert.That(idx.RequiresGlobalFallback, Is.True);
        }

        [Test]
        public void Not_wrapping_has_focus_forces_fallback_A8() {
            var idx = BuildIndex(":not(:has(:focus))");
            Assert.That(idx.RequiresGlobalFallback, Is.True);
        }

        [Test]
        public void Structural_has_does_not_force_fallback_A8() {
            // `.card:has(img)` depends on DOM structure, not interactive state.
            // The structural HasSensitive ancestor-walk handles it; it must NOT
            // pay the coarse global-state-version cost.
            var idx = BuildIndex(".card:has(img)");
            Assert.That(idx.RequiresGlobalFallback, Is.False);
            Assert.That(SelectorStateDependencies.RequiresGlobalStateInvalidation(
                SelectorParser.Parse(".card:has(img)")), Is.False);
        }

        [Test]
        public void Plain_state_pseudo_does_not_force_fallback_A8() {
            // Regression guard: a bare `:hover` (state on the subject itself) is
            // observed directly by the per-element digest and must NOT be pushed
            // onto the global path by the new stateful-:has detection.
            var idx = BuildIndex(".btn:hover");
            Assert.That(idx.RequiresGlobalFallback, Is.False);
        }

        [Test]
        public void Has_with_descendant_combinator_inside_forces_fallback_A8() {
            // `.card:has(.row :hover)` — the stateful pseudo is on a descendant
            // within the :has relative selector; still descendant-state-dependent.
            var idx = BuildIndex(".card:has(.row :hover)");
            Assert.That(idx.RequiresGlobalFallback, Is.True);
        }

        [Test]
        public void Index_is_immutable_after_build() {
            // The exposed read API is IReadOnlyList<int>; no Add / Remove on the
            // index itself. Re-construction is the only way to mutate. This test
            // documents the contract by re-building and checking values match.
            var sels1 = new List<CompiledSelector> { SelectorParser.Parse(":hover") };
            var idx1 = new StateSelectorIndex(sels1);
            sels1.Add(SelectorParser.Parse(":focus"));
            Assert.That(idx1.GlobalStateMask, Is.EqualTo(ElementState.Hover),
                "post-construction list mutation must not affect the index");
        }
    }
}
