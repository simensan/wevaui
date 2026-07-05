using System.Collections.Generic;
using Weva.Css.Selectors;

namespace Weva.Compiled {
    // Inverted index over a list of CompiledSelectors mapping each pseudo-class
    // state bit (Hover, Focus, Active, Checked, ...) to the indices of selectors
    // that test that bit anywhere in their compound sequence.
    //
    // The cascade uses two facets of this:
    //   - GlobalStateMask: union of state bits referenced by ANY selector. A per-
    //     element state digest only needs to vary across these bits; an element
    //     whose own state intersected with the global mask is unchanged is
    //     guaranteed to produce the same selector matches.
    //   - SelectorsForState(bit): yields selector indices to drop / re-resolve
    //     when that single bit flips on some element. v1 stops at the bit set;
    //     finer-grained per-element targeting is handled by the cascade's per-
    //     element digest.
    //
    // RequiresGlobalFallback flags the presence of any selector whose state-bit
    // is gated on a sibling combinator (e.g. `.btn:hover + .next`). v1 cannot
    // route such selectors through the per-element digest path — toggling
    // hover on `.btn` doesn't change `.next`'s own state nor any ancestor's
    // state, so the digest wouldn't notice. Cascade falls back to the global
    // state-version invalidation path when this is true.
    //
    // Built once per compile. Empty stylesheet -> GlobalStateMask == None and
    // every per-bit query yields an empty list.
    internal sealed class StateSelectorIndex {
        readonly Dictionary<ElementState, List<int>> byBit = new();
        readonly ElementState globalMask;
        readonly bool requiresGlobalFallback;
        // C5: the exact selector indices that tripped RequiresGlobalFallback
        // (sibling-state, stateful :has(), stateful :nth-of). The cascade
        // matches an element against THESE selectors' subjects to decide
        // whether it actually needs the coarse global-version digest, instead
        // of folding it into every element. Empty unless requiresGlobalFallback.
        readonly List<int> globalFallbackSelectors = new();

        public ElementState GlobalStateMask => globalMask;
        public bool RequiresGlobalFallback => requiresGlobalFallback;
        public IReadOnlyList<int> GlobalFallbackSelectors => globalFallbackSelectors;

        public StateSelectorIndex(IReadOnlyList<CompiledSelector> selectors) {
            if (selectors == null) return;
            ElementState accumulated = ElementState.None;
            bool fallback = false;
            for (int i = 0; i < selectors.Count; i++) {
                var sel = selectors[i];
                var bits = SelectorStateDependencies.GetStateBits(sel);
                if (bits == ElementState.None) continue;
                accumulated |= bits;
                AddToBuckets(i, bits);
                if (SelectorStateDependencies.RequiresGlobalStateInvalidation(sel)) {
                    fallback = true;
                    globalFallbackSelectors.Add(i);
                }
            }
            globalMask = accumulated;
            requiresGlobalFallback = fallback;
        }

        void AddToBuckets(int selectorIndex, ElementState bits) {
            for (int b = 0; b < 16; b++) {
                var bit = (ElementState)(1 << b);
                if ((bits & bit) == 0) continue;
                if (!byBit.TryGetValue(bit, out var list)) {
                    list = new List<int>();
                    byBit[bit] = list;
                }
                list.Add(selectorIndex);
            }
        }

        public IReadOnlyList<int> SelectorsForState(ElementState bit) {
            if (byBit.TryGetValue(bit, out var list)) return list;
            return System.Array.Empty<int>();
        }

        public bool AnySelectorTests(ElementState bits) {
            return (globalMask & bits) != ElementState.None;
        }
    }
}
