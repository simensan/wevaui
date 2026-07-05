namespace Weva.Css.Selectors {
    // Inspects a CompiledSelector and extracts the union of pseudo-class state bits
    // it tests anywhere in its compound sequence. Used by StateSelectorIndex to
    // route per-element state-change invalidation to only the selectors whose match
    // could flip. Pseudo-classes that are NOT state-driven (e.g. :first-child,
    // :nth-of-type, :empty, :is/:where/:not branches) do not contribute bits;
    // only the dynamic interactive states do.
    //
    // :not(:hover) and :is/:where containing a stateful pseudo recurse into their
    // inner selectors and accumulate the bits — a state flip outside the negation
    // can flip the negation result, so the dependency is real either direction.
    // :has(:hover) (and other stateful pseudos inside :has) also contribute their
    // inner state bits to the subject's dependency mask: a descendant hover flip
    // changes whether :has(:hover) matches on the ancestor. The per-element state
    // digest in CascadeEngine.IncrementalState.cs reads only the subject element's
    // own state and so cannot observe a descendant flip; A8 closes that gap by
    // routing any sheet containing a stateful :has through the coarse global-
    // version path (RequiresGlobalStateInvalidation → true), the same v1 tradeoff
    // used for stateful `of S` filters and sibling combinators.
    //
    // The returned ElementState is a bitmask. Tests:
    //   GetStateBits(parse(":hover"))                  -> Hover
    //   GetStateBits(parse(".btn"))                    -> None
    //   GetStateBits(parse(":not(:hover)"))            -> Hover
    //   GetStateBits(parse(".x:focus.active"))         -> Focus | Active
    //   GetStateBits(parse(":hover:focus"))            -> Hover | Focus
    //   GetStateBits(parse(".parent:hover .child"))    -> Hover  (state seen anywhere)
    //   GetStateBits(parse(":is(:hover, :focus)"))     -> Hover | Focus
    public static class SelectorStateDependencies {
        public static ElementState GetStateBits(CompiledSelector selector) {
            if (selector == null) return ElementState.None;
            var seq = selector.Sequence;
            if (seq == null) return ElementState.None;
            var bits = ElementState.None;
            foreach (var compound in seq.Compounds) {
                if (compound == null) continue;
                foreach (var part in compound.Parts) {
                    bits |= BitsForSimple(part);
                }
            }
            return bits;
        }

        public static bool TestsState(CompiledSelector selector, ElementState bits) {
            if (bits == ElementState.None) return false;
            return (GetStateBits(selector) & bits) != ElementState.None;
        }

        // True when the selector tests a stateful pseudo-class on ANY compound to
        // the LEFT of the rightmost (i.e. the state bit lives in a compound that
        // would match an ancestor-or-sibling of the matching element). Used by
        // CascadeEngine to detect cases where a state flip on one element can flip
        // the match for a different element via combinators. v1: when sibling
        // combinators are involved we fall back to the old all-invalidate path.
        public static bool TestsStateOnNonRightmost(CompiledSelector selector) {
            if (selector == null) return false;
            var seq = selector.Sequence;
            if (seq == null || seq.Compounds.Count <= 1) return false;
            for (int i = 0; i < seq.Compounds.Count - 1; i++) {
                var compound = seq.Compounds[i];
                if (compound == null) continue;
                foreach (var part in compound.Parts) {
                    if (BitsForSimple(part) != ElementState.None) return true;
                }
            }
            return false;
        }

        // True when the selector contains an adjacent-sibling or general-sibling
        // combinator AND tests a stateful pseudo on a non-rightmost compound. v1
        // simplification: such selectors force the cache to fall back to the
        // global state-version path because the state-on-element-A could flip
        // the match for sibling element B and per-element digesting on B alone
        // wouldn't notice. Selectors using only descendant/child combinators
        // are safe because the cascade is parent-first and ancestor recomputes
        // bump parent-style version which propagates to descendants.
        //
        // Also returns true when ANY compound carries a `:nth-child(An+B of S)`
        // or `:nth-last-child(An+B of S)` whose filter S contains a stateful
        // pseudo-class. A sibling's state flip can change its membership in the
        // filtered set, which changes the filtered index of the subject element
        // even without an explicit sibling combinator. The per-element digest
        // only keys on the SUBJECT's own state, so these selectors must use the
        // coarse global-version path. (v1 limitation — documented in C9b.)
        public static bool RequiresGlobalStateInvalidation(CompiledSelector selector) {
            if (selector == null) return false;
            var seq = selector.Sequence;
            if (seq == null) return false;
            // Check for stateful NthOfFilter on any compound (sibling-counting
            // means sibling state affects subject match even with no combinator).
            //
            // A8: also check for a stateful `:has(...)` on any compound. A
            // descendant's state flip (e.g. `.parent:has(.child:hover)` when the
            // child hovers) changes whether the SUBJECT (the ancestor carrying
            // `:has`) matches, but the per-element state digest keys only on the
            // SUBJECT's OWN state — it can't observe a descendant flip. So a
            // sheet containing a stateful `:has` must use the coarse global-
            // version path (same v1 tradeoff as stateful `of S` and sibling
            // combinators). Detection recurses through :is/:where/:not and
            // nth-of filters so a nested `:is(:has(:hover))` still trips it.
            foreach (var compound in seq.Compounds) {
                if (compound == null) continue;
                foreach (var part in compound.Parts) {
                    if (part is PseudoClassSelector pc) {
                        if ((pc.Kind == PseudoClassKind.NthChild || pc.Kind == PseudoClassKind.NthLastChild)
                            && pc.NthOfFilter != null) {
                            // Recurse into the filter to see if it's stateful.
                            var filterBits = ElementState.None;
                            foreach (var fseq in pc.NthOfFilter) {
                                foreach (var fcomp in fseq.Compounds) {
                                    foreach (var fpart in fcomp.Parts) {
                                        filterBits |= BitsForSimple(fpart);
                                    }
                                }
                            }
                            if (filterBits != ElementState.None) return true;
                        }
                        if (ContainsStatefulHas(pc)) return true;
                    }
                }
            }
            if (seq.Combinators == null) return false;
            for (int i = 0; i < seq.Combinators.Count; i++) {
                var c = seq.Combinators[i];
                if (c != Combinator.AdjacentSibling && c != Combinator.GeneralSibling) continue;
                // The compound to the LEFT of a sibling combinator (index i in
                // the left-to-right list) might host the stateful pseudo. Check
                // every compound to the LEFT of any sibling combinator.
                for (int j = 0; j <= i; j++) {
                    var compound = seq.Compounds[j];
                    if (compound == null) continue;
                    foreach (var part in compound.Parts) {
                        if (BitsForSimple(part) != ElementState.None) return true;
                    }
                }
            }
            return false;
        }

        static ElementState BitsForSimple(SimpleSelector part) {
            if (part is PseudoClassSelector pc) return BitsForPseudo(pc);
            return ElementState.None;
        }

        // A8: true when this pseudo IS a stateful `:has(...)` (its relative
        // selector list tests a dynamic state pseudo, e.g. `:has(:hover)` /
        // `:has(.x:checked)`), OR wraps one via `:is`/`:where`/`:not` or an
        // `:nth-child(... of S)` filter. A non-stateful `:has(.static)` returns
        // false — it doesn't depend on any dynamic descendant state, so it must
        // NOT force the coarse global-version path. Recurses so nesting like
        // `:not(:has(:focus))` is detected.
        static bool ContainsStatefulHas(PseudoClassSelector pc) {
            if (pc == null) return false;
            switch (pc.Kind) {
                case PseudoClassKind.Has: {
                    if (pc.InnerList == null) return false;
                    var bits = ElementState.None;
                    foreach (var seq in pc.InnerList) {
                        foreach (var compound in seq.Compounds) {
                            foreach (var inner in compound.Parts) {
                                bits |= BitsForSimple(inner);
                            }
                        }
                    }
                    return bits != ElementState.None;
                }
                case PseudoClassKind.Is:
                case PseudoClassKind.Where:
                case PseudoClassKind.Not: {
                    if (pc.InnerList != null) {
                        foreach (var seq in pc.InnerList) {
                            foreach (var compound in seq.Compounds) {
                                foreach (var inner in compound.Parts) {
                                    if (inner is PseudoClassSelector ip && ContainsStatefulHas(ip)) return true;
                                }
                            }
                        }
                    }
                    if (pc.InnerSimple is PseudoClassSelector simple) return ContainsStatefulHas(simple);
                    return false;
                }
                case PseudoClassKind.NthChild:
                case PseudoClassKind.NthLastChild: {
                    if (pc.NthOfFilter == null) return false;
                    foreach (var seq in pc.NthOfFilter) {
                        foreach (var compound in seq.Compounds) {
                            foreach (var inner in compound.Parts) {
                                if (inner is PseudoClassSelector ip && ContainsStatefulHas(ip)) return true;
                            }
                        }
                    }
                    return false;
                }
                default:
                    return false;
            }
        }

        static ElementState BitsForPseudo(PseudoClassSelector pc) {
            switch (pc.Kind) {
                case PseudoClassKind.Hover: return ElementState.Hover;
                case PseudoClassKind.Focus: return ElementState.Focus;
                case PseudoClassKind.FocusVisible: return ElementState.FocusVisible;
                case PseudoClassKind.FocusWithin: return ElementState.FocusWithin;
                case PseudoClassKind.Active: return ElementState.Active;
                case PseudoClassKind.Target: return ElementState.Target;
                case PseudoClassKind.Disabled: return ElementState.Disabled;
                case PseudoClassKind.Enabled: return ElementState.Disabled;
                case PseudoClassKind.Checked: return ElementState.Checked;
                case PseudoClassKind.UserValid:
                case PseudoClassKind.UserInvalid: return ElementState.UserInteracted;
                case PseudoClassKind.PlaceholderShown: return ElementState.PlaceholderShown;
                case PseudoClassKind.Autofill: return ElementState.Autofill;
                case PseudoClassKind.Not: {
                    // :not() now uses the InnerList path like :is/:where
                    // (#258). Fall back to InnerSimple for legacy callers.
                    var notBits = ElementState.None;
                    if (pc.InnerList != null) {
                        foreach (var seq in pc.InnerList) {
                            foreach (var compound in seq.Compounds) {
                                foreach (var inner in compound.Parts) {
                                    notBits |= BitsForSimple(inner);
                                }
                            }
                        }
                        return notBits;
                    }
                    return BitsForSimple(pc.InnerSimple);
                }
                case PseudoClassKind.Is:
                case PseudoClassKind.Where: {
                    var bits = ElementState.None;
                    if (pc.InnerList == null) return bits;
                    foreach (var seq in pc.InnerList) {
                        foreach (var compound in seq.Compounds) {
                            foreach (var inner in compound.Parts) {
                                bits |= BitsForSimple(inner);
                            }
                        }
                    }
                    return bits;
                }
                case PseudoClassKind.Has: {
                    // `:has(<relative-selector-list>)` matches the subject when
                    // any descendant matched by the inner selector exists. A
                    // stateful pseudo inside the inner list (e.g. `:has(:hover)`)
                    // therefore makes the subject's match depend on descendant
                    // state. The bits must be union'd into the subject's state-
                    // dependency mask so the StateSelectorIndex registers the
                    // selector under those bits. A8: reporting the bits here is
                    // necessary but not sufficient — the per-element digest keys
                    // on the SUBJECT's own state and can't see a descendant flip,
                    // so RequiresGlobalStateInvalidation also returns true for a
                    // stateful :has, routing the sheet through the global-version
                    // path so the ancestor re-cascades on any state change.
                    var bits = ElementState.None;
                    if (pc.InnerList == null) return bits;
                    foreach (var seq in pc.InnerList) {
                        foreach (var compound in seq.Compounds) {
                            foreach (var inner in compound.Parts) {
                                bits |= BitsForSimple(inner);
                            }
                        }
                    }
                    return bits;
                }
                case PseudoClassKind.NthChild:
                case PseudoClassKind.NthLastChild: {
                    // CSS Selectors L4 §6.6.5: `:nth-child(An+B of S)` — if S
                    // contains a stateful pseudo (e.g. `:nth-child(1 of :hover)`)
                    // then whether the element matches depends on the sibling's
                    // state. Recurse into the filter list the same way :is/:not
                    // do above so the StateSelectorIndex registers the selector
                    // under those bits.
                    //
                    // V1 limitation: the per-element digest in
                    // CascadeEngine.IncrementalState.cs keys on the SUBJECT's
                    // OWN state, so a SIBLING's state flip won't trigger a re-
                    // cascade of the subject through the digest path.  Selectors
                    // with a stateful `of S` filter on the rightmost compound
                    // fall back to the global state-version path via
                    // RequiresGlobalStateInvalidation → true. That path is
                    // correct but coarse; a finer per-element sibling-walk is
                    // a v2 concern.
                    if (pc.NthOfFilter == null) return ElementState.None;
                    var bits = ElementState.None;
                    foreach (var seq in pc.NthOfFilter) {
                        foreach (var compound in seq.Compounds) {
                            foreach (var inner in compound.Parts) {
                                bits |= BitsForSimple(inner);
                            }
                        }
                    }
                    return bits;
                }
                default:
                    return ElementState.None;
            }
        }
    }
}
