using System.Collections.Generic;
using Weva.Css.Selectors;

namespace Weva.Compiled {
    // Per-feature invalidation index modeled on Chrome's RuleFeatureSet
    // (Blink's `style/rule_feature_set.h`). At stylesheet compile time we
    // walk every CompiledSelector's CompoundSequence and extract the
    // "features" — class, attribute name, stateful pseudo — each compound
    // tests, bucketing them by where the compound sits in the chain:
    //
    //   * SUBJECT (rightmost compound): the feature lives on the element
    //     the rule applies to. A change to this feature on element E can
    //     flip the rule's match on E itself.
    //
    //   * DESCENDANT (non-subject compound, chain to subject uses only
    //     descendant/child combinators): the feature lives on an ancestor
    //     of the matched element. A change on element E can flip the rule's
    //     match on E's descendants.
    //
    //   * SIBLING (non-subject compound, chain to subject crosses an
    //     adjacent or general sibling combinator anywhere): the feature
    //     lives on a sibling-side of the chain. A change on E can flip
    //     the match on E's later siblings (and their descendants).
    //
    // Cascade consumers query the buckets with a (kind, feature) pair to
    // get the list of selector indices that could be affected. Combined
    // with the actual selector match results, this lets incremental
    // invalidation narrow the dirty set from "ancestor closure of every
    // pseudo-state flip" down to "elements whose matched-selectors
    // genuinely depend on the change."
    //
    // v1 limitations:
    //   - All non-subject compounds along a sibling-crossing chain are
    //     conservatively classed as SIBLING. A more precise impl could
    //     distinguish "ancestor of a sibling" vs "sibling of an ancestor".
    //   - Type selectors and id selectors aren't indexed (those flip
    //     rarely in practice and the cascade tracks element identity).
    //   - :has() / :is() / :where() inner-list features aren't recursively
    //     extracted into the outer feature buckets; they only contribute
    //     via SelectorStateDependencies for state bits. Class / attribute
    //     features inside these pseudos aren't yet indexed.
    internal sealed class RuleFeatureSet {
        // Subject (rightmost) compound feature index → selector indices.
        readonly Dictionary<string, List<int>> subjectClassFeatures = new();
        readonly Dictionary<string, List<int>> subjectAttributeFeatures = new();
        readonly Dictionary<ElementState, List<int>> subjectStateFeatures = new();

        // Non-subject compound features when the chain to subject uses only
        // descendant/child combinators.
        readonly Dictionary<string, List<int>> descendantClassFeatures = new();
        readonly Dictionary<string, List<int>> descendantAttributeFeatures = new();
        readonly Dictionary<ElementState, List<int>> descendantStateFeatures = new();

        // Non-subject compound features when the chain to subject crosses
        // a sibling combinator.
        readonly Dictionary<string, List<int>> siblingClassFeatures = new();
        readonly Dictionary<string, List<int>> siblingAttributeFeatures = new();
        readonly Dictionary<ElementState, List<int>> siblingStateFeatures = new();

        // Aggregate masks let callers cheap-check "is any selector at all
        // sensitive to this feature kind" before doing the dictionary
        // lookup. Useful for the cascade's "should we even bother" gates.
        public ElementState SubjectStateMask { get; private set; }
        public ElementState DescendantStateMask { get; private set; }
        public ElementState SiblingStateMask { get; private set; }

        public RuleFeatureSet(IReadOnlyList<CompiledSelector> selectors) {
            if (selectors == null) return;
            for (int i = 0; i < selectors.Count; i++) {
                IndexSelector(selectors[i], i);
            }
        }

        void IndexSelector(CompiledSelector selector, int selectorIndex) {
            if (selector == null) return;
            var seq = selector.Sequence;
            if (seq == null || seq.Compounds.Count == 0) return;

            int subjectIdx = seq.Compounds.Count - 1;
            ExtractFromCompound(seq.Compounds[subjectIdx], selectorIndex,
                subjectClassFeatures, subjectAttributeFeatures, subjectStateFeatures,
                ref _subjectMaskAccum);

            // Walk from the compound just-left-of-subject back to the head.
            // The combinator AT index c connects compound c to compound c+1.
            // We track whether the chain from compound c onward to the
            // subject has crossed a sibling combinator yet; once it has,
            // every further-left compound gets bucketed as SIBLING since a
            // change there propagates through the sibling edge.
            bool chainCrossesSibling = false;
            for (int c = subjectIdx - 1; c >= 0; c--) {
                var combToNext = seq.Combinators[c];
                if (combToNext == Combinator.AdjacentSibling || combToNext == Combinator.GeneralSibling) {
                    chainCrossesSibling = true;
                }
                if (chainCrossesSibling) {
                    ExtractFromCompound(seq.Compounds[c], selectorIndex,
                        siblingClassFeatures, siblingAttributeFeatures, siblingStateFeatures,
                        ref _siblingMaskAccum);
                } else {
                    ExtractFromCompound(seq.Compounds[c], selectorIndex,
                        descendantClassFeatures, descendantAttributeFeatures, descendantStateFeatures,
                        ref _descendantMaskAccum);
                }
            }

            SubjectStateMask = _subjectMaskAccum;
            DescendantStateMask = _descendantMaskAccum;
            SiblingStateMask = _siblingMaskAccum;
        }

        // Mask accumulators live as instance fields rather than locals
        // because we update them across the multiple ExtractFromCompound
        // calls inside IndexSelector (state bits accumulated from many
        // compounds need to flow into the public mask properties).
        ElementState _subjectMaskAccum;
        ElementState _descendantMaskAccum;
        ElementState _siblingMaskAccum;

        static void ExtractFromCompound(CompoundSelector compound, int selectorIndex,
            Dictionary<string, List<int>> classMap,
            Dictionary<string, List<int>> attrMap,
            Dictionary<ElementState, List<int>> stateMap,
            ref ElementState stateMaskAccum) {
            if (compound == null) return;
            for (int p = 0; p < compound.Parts.Count; p++) {
                var part = compound.Parts[p];
                switch (part) {
                    case ClassSelector cs:
                        AddToListMap(classMap, cs.ClassName, selectorIndex);
                        break;
                    case AttributeSelector attr:
                        AddToListMap(attrMap, attr.Name, selectorIndex);
                        break;
                    case PseudoClassSelector pc:
                        var bits = BitsForPseudo(pc);
                        AddBitsToMap(stateMap, bits, selectorIndex);
                        stateMaskAccum |= bits;
                        break;
                }
            }
        }

        static void AddToListMap<TKey>(Dictionary<TKey, List<int>> map, TKey key, int value) {
            if (key == null) return;
            if (!map.TryGetValue(key, out var list)) {
                list = new List<int>();
                map[key] = list;
            }
            // Dedupe consecutive inserts of the same selector — a compound
            // with `.foo.foo` is malformed CSS but we shouldn't blow up.
            if (list.Count == 0 || list[list.Count - 1] != value) list.Add(value);
        }

        static void AddBitsToMap(Dictionary<ElementState, List<int>> map, ElementState bits, int value) {
            if (bits == ElementState.None) return;
            // Decompose the bitmask into individual bit dictionary entries
            // so callers can query per-bit (`SelectorsForState(Hover)`)
            // without scanning every selector.
            for (int b = 0; b < 16; b++) {
                var bit = (ElementState)(1 << b);
                if ((bits & bit) == 0) continue;
                if (!map.TryGetValue(bit, out var list)) {
                    list = new List<int>();
                    map[bit] = list;
                }
                if (list.Count == 0 || list[list.Count - 1] != value) list.Add(value);
            }
        }

        // Recursive state-bit extraction matching SelectorStateDependencies
        // so :not(:hover) / :is(:hover, :focus) / :has(:hover) contribute
        // their inner states to the outer compound's bucket. The state
        // bits indicate "this compound's match depends on these state
        // bits being whatever value they need to be for the selector to
        // match" — that's the invalidation contract.
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
                case PseudoClassKind.Not:
                case PseudoClassKind.Is:
                case PseudoClassKind.Where:
                case PseudoClassKind.Has: {
                    var bits = ElementState.None;
                    if (pc.InnerList != null) {
                        for (int s = 0; s < pc.InnerList.Count; s++) {
                            var seq = pc.InnerList[s];
                            for (int c = 0; c < seq.Compounds.Count; c++) {
                                var compound = seq.Compounds[c];
                                for (int q = 0; q < compound.Parts.Count; q++) {
                                    if (compound.Parts[q] is PseudoClassSelector inner) {
                                        bits |= BitsForPseudo(inner);
                                    }
                                }
                            }
                        }
                    } else if (pc.InnerSimple is PseudoClassSelector innerSimple) {
                        bits |= BitsForPseudo(innerSimple);
                    }
                    return bits;
                }
                default:
                    return ElementState.None;
            }
        }

        // The state bits a single compound's match depends on, recursing
        // into :not/:is/:where/:has exactly like the feature-index build.
        // Exposed for the SetFlag gate (audit CX1): given a state flip on an
        // element, the gate walks a selector's NON-subject compounds and
        // needs to know which of them test the flipping bit.
        internal static ElementState CompoundStateBits(CompoundSelector compound) {
            var bits = ElementState.None;
            if (compound == null) return bits;
            for (int p = 0; p < compound.Parts.Count; p++) {
                if (compound.Parts[p] is PseudoClassSelector pc) bits |= BitsForPseudo(pc);
            }
            return bits;
        }

        // Subject (rightmost) queries.
        public IReadOnlyList<int> SubjectSelectorsForClass(string className) => GetOrEmpty(subjectClassFeatures, className);
        public IReadOnlyList<int> SubjectSelectorsForAttribute(string attrName) => GetOrEmpty(subjectAttributeFeatures, attrName);
        public IReadOnlyList<int> SubjectSelectorsForState(ElementState bit) => GetOrEmpty(subjectStateFeatures, bit);

        // Descendant (non-subject, descendant/child chain) queries.
        public IReadOnlyList<int> DescendantSelectorsForClass(string className) => GetOrEmpty(descendantClassFeatures, className);
        public IReadOnlyList<int> DescendantSelectorsForAttribute(string attrName) => GetOrEmpty(descendantAttributeFeatures, attrName);
        public IReadOnlyList<int> DescendantSelectorsForState(ElementState bit) => GetOrEmpty(descendantStateFeatures, bit);

        // Sibling (non-subject, sibling-crossing chain) queries.
        public IReadOnlyList<int> SiblingSelectorsForClass(string className) => GetOrEmpty(siblingClassFeatures, className);
        public IReadOnlyList<int> SiblingSelectorsForAttribute(string attrName) => GetOrEmpty(siblingAttributeFeatures, attrName);
        public IReadOnlyList<int> SiblingSelectorsForState(ElementState bit) => GetOrEmpty(siblingStateFeatures, bit);

        static IReadOnlyList<int> GetOrEmpty<TKey>(Dictionary<TKey, List<int>> map, TKey key) {
            if (key == null) return System.Array.Empty<int>();
            if (map.TryGetValue(key, out var list)) return list;
            return System.Array.Empty<int>();
        }
    }
}
