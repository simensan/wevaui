using System;
using System.Collections.Generic;
using Weva.Css.Selectors;

namespace Weva.Compiled {
    // Coarse-filter inverted index over a list of CompiledSelectors. For each
    // selector, we classify the rightmost compound's most-specific key
    // (id -> class -> tag -> universal) and bucket it. Match-time lookup yields
    // a deduplicated list of candidate selector indices that the caller still
    // needs to verify with the full SelectorMatcher (combinators, pseudo-classes,
    // attribute matches not keyed by tag/class/id are not pre-filtered).
    internal sealed class SelectorIndex {
        readonly Dictionary<int, List<int>> idBucket = new();
        readonly Dictionary<int, List<int>> classBucket = new();
        readonly Dictionary<int, List<int>> tagBucket = new();
        readonly Dictionary<int, List<int>> attributeBucket = new();
        readonly List<int> universalBucket = new();

        readonly SymbolTable symbols;

        // Per-selector precomputed shape. When CanFastMatch is true the snapshot
        // matcher can fully verify the selector without delegating to the
        // managed matcher. Compounds are stored right-to-left (Compounds[0]
        // is the rightmost; Combinators[i] connects Compounds[i] to Compounds[i+1]).
        // Compounds[i] is the compound on the LEFT side of Combinators[i].
        internal struct AttrConstraint {
            public int NameSym;
            public AttributeOperator Op;
            public int ValueSym; // 0 = absent (only valid for Op==Exists)
            public string Value; // raw value string for substring/dash/etc.
            public string DashPrefix;
        }

        internal struct CompoundShape {
            public int TagSym;     // 0 = any
            public int IdSym;      // 0 = any
            public int[] ClassSyms;
            public AttrConstraint[] Attrs; // null = no attr constraints
            // Bitmask of ElementState flags this compound requires. The
            // snapshot matcher checks `(state.GetState(e) & RequiredState)
            // == RequiredState` — no flags means no state check (initial
            // value, fast). Storing the state bitmask in the shape moves
            // common simple pseudo-classes (`:hover`, `:focus`,
            // `:focus-visible`, `:active`, `:disabled`, `:checked`,
            // `:placeholder-shown`, `:target`) off the managed-matcher
            // fallback — that was the dominant SelectorMatch cost on
            // animation-heavy UIs where every paint dirty walks the full
            // candidate list.
            public ElementState RequiredState;
        }

        internal struct SelectorShape {
            public bool CanFastMatch;
            // index 0 = rightmost compound; subsequent indices walk left.
            public CompoundShape[] Compounds;
            // Combinators[i] is the combinator BETWEEN Compounds[i] and Compounds[i+1]
            // (i.e. between rightmost+i and rightmost+i+1 working leftward).
            public Combinator[] Combinators;

            public bool IsTrivialSingleCompound =>
                CanFastMatch && Compounds != null && Compounds.Length == 1;
        }

        SelectorShape[] shapes;
        internal ref readonly SelectorShape GetShape(int selectorIndex) => ref shapes[selectorIndex];

        public SelectorIndex(SymbolTable symbols, IReadOnlyList<CompiledSelector> selectors) {
            this.symbols = symbols;
            shapes = new SelectorShape[selectors.Count];
            for (int i = 0; i < selectors.Count; i++) {
                Classify(i, selectors[i]);
                shapes[i] = ComputeShape(selectors[i]);
            }
        }

        SelectorShape ComputeShape(CompiledSelector sel) {
            var seq = sel.Sequence;
            if (sel.PseudoElement != null) return default;
            if (seq == null || seq.Compounds.Count == 0) return default;

            int n = seq.Compounds.Count;
            var compounds = new CompoundShape[n];
            // Walk right-to-left so index 0 is the rightmost compound.
            for (int i = 0; i < n; i++) {
                var src = seq.Compounds[n - 1 - i];
                if (src.PseudoElement != null) return default;
                if (!TryDistillCompound(src, out compounds[i])) return default;
            }
            // Combinators are stored seq.Combinators[k] BETWEEN seq.Compounds[k]
            // and seq.Compounds[k+1] (left to right). For our right-to-left walk
            // Combinators[i] = seq.Combinators[n - 2 - i].
            Combinator[] combs = null;
            if (n > 1) {
                combs = new Combinator[n - 1];
                for (int i = 0; i < n - 1; i++) {
                    combs[i] = seq.Combinators[n - 2 - i];
                    // Only descendant/child supported in the fast path for now.
                    if (combs[i] != Combinator.Descendant && combs[i] != Combinator.Child) {
                        return default;
                    }
                }
            }

            return new SelectorShape {
                CanFastMatch = true,
                Compounds = compounds,
                Combinators = combs,
            };
        }

        bool TryDistillCompound(CompoundSelector src, out CompoundShape shape) {
            shape = default;
            int tagSym = 0;
            int idSym = 0;
            List<int> classes = null;
            List<AttrConstraint> attrs = null;
            ElementState requiredState = ElementState.None;
            foreach (var part in src.Parts) {
                switch (part) {
                    case UniversalSelector _: break;
                    case TypeSelector ts:
                        if (tagSym != 0) return false;
                        tagSym = symbols.Intern(ts.TagName);
                        break;
                    case IdSelector ids:
                        if (idSym != 0) return false;
                        idSym = symbols.Intern(ids.Id);
                        break;
                    case ClassSelector cs:
                        classes ??= new List<int>(2);
                        classes.Add(symbols.Intern(cs.ClassName));
                        break;
                    case AttributeSelector at:
                        if (at.CaseInsensitive) return false;
                        attrs ??= new List<AttrConstraint>(2);
                        attrs.Add(new AttrConstraint {
                            NameSym = symbols.Intern(at.Name),
                            Op = at.Operator,
                            ValueSym = at.Value == null ? 0 : symbols.Intern(at.Value),
                            Value = at.Value,
                            DashPrefix = at.DashPrefix,
                        });
                        break;
                    case PseudoClassSelector pcs:
                        // Simple state-driven pseudo-classes can fast-match
                        // against the IElementStateProvider's bitmask. The
                        // selector only contributes a state flag check —
                        // it adds no parameters and no nested selector
                        // matching. More complex forms (nth-child, is(),
                        // not(), has(), lang(), nth-of-type) keep going to
                        // the managed matcher (return false below).
                        var flag = StatePseudoFlag(pcs);
                        if (flag == ElementState.None) return false;
                        requiredState |= flag;
                        break;
                    default:
                        return false;
                }
            }
            shape.TagSym = tagSym;
            shape.IdSym = idSym;
            shape.ClassSyms = classes?.ToArray() ?? Array.Empty<int>();
            shape.Attrs = attrs?.ToArray();
            shape.RequiredState = requiredState;
            return true;
        }

        // Maps a `PseudoClassSelector` to an `ElementState` flag iff the
        // pseudo-class is (a) state-driven and (b) carries no arguments
        // (Not/Is/Where/Has/Lang/Nth all carry payloads and stay on the
        // managed path). `ElementState.None` signals "not fast-pathable".
        static ElementState StatePseudoFlag(PseudoClassSelector pcs) {
            if (pcs.Argument != null) return ElementState.None;
            if (pcs.InnerSimple != null) return ElementState.None;
            if (pcs.InnerList != null) return ElementState.None;
            if (pcs.NthOfFilter != null) return ElementState.None;
            // Nth is a struct; only the Nth-* pseudo-class kinds use it.
            // Those kinds aren't in our state-pseudo whitelist below, so
            // a non-default Nth on a non-Nth kind is impossible and we
            // don't need to test it here.
            switch (pcs.Kind) {
                case PseudoClassKind.Hover: return ElementState.Hover;
                case PseudoClassKind.Focus: return ElementState.Focus;
                case PseudoClassKind.FocusVisible: return ElementState.FocusVisible;
                case PseudoClassKind.FocusWithin: return ElementState.FocusWithin;
                case PseudoClassKind.Active: return ElementState.Active;
                case PseudoClassKind.Disabled: return ElementState.Disabled;
                case PseudoClassKind.Checked: return ElementState.Checked;
                case PseudoClassKind.PlaceholderShown: return ElementState.PlaceholderShown;
                case PseudoClassKind.Target: return ElementState.Target;
                // Root is structural (first Element child of Document) — handled
                // by SelectorMatcher.IsRootElement, not by state provider bit.
                // Return None so :root falls through to the managed path where
                // the structural check runs correctly regardless of state provider.
                case PseudoClassKind.Root: return ElementState.None;
                default: return ElementState.None;
            }
        }

        void Classify(int selectorIndex, CompiledSelector sel) {
            var seq = sel.Sequence;
            if (seq == null || seq.Compounds.Count == 0) {
                universalBucket.Add(selectorIndex);
                return;
            }
            var rightmost = seq.Compounds[seq.Compounds.Count - 1];

            // Spec ordering: id > class > tag > attribute > universal/pseudo-only.
            int idSym = 0;
            int classSym = 0;
            int tagSym = 0;
            int attrSym = 0;

            foreach (var part in rightmost.Parts) {
                switch (part) {
                    case IdSelector ids:
                        if (idSym == 0) idSym = symbols.Intern(ids.Id);
                        break;
                    case ClassSelector cs:
                        if (classSym == 0) classSym = symbols.Intern(cs.ClassName);
                        break;
                    case TypeSelector ts:
                        if (tagSym == 0) tagSym = symbols.Intern(ts.TagName);
                        break;
                    case AttributeSelector at:
                        if (attrSym == 0) attrSym = symbols.Intern(at.Name);
                        break;
                    case UniversalSelector _:
                        break;
                    default:
                        // Pseudo-class only — falls to universal so it's still considered.
                        break;
                }
            }

            if (idSym != 0) {
                Add(idBucket, idSym, selectorIndex);
                return;
            }
            if (classSym != 0) {
                Add(classBucket, classSym, selectorIndex);
                return;
            }
            if (tagSym != 0) {
                Add(tagBucket, tagSym, selectorIndex);
                return;
            }
            if (attrSym != 0) {
                Add(attributeBucket, attrSym, selectorIndex);
                return;
            }
            // Universal / pseudo-only / pseudo-element-only fall here.
            universalBucket.Add(selectorIndex);
        }

        static void Add(Dictionary<int, List<int>> bucket, int key, int value) {
            if (!bucket.TryGetValue(key, out var list)) {
                list = new List<int>();
                bucket[key] = list;
            }
            list.Add(value);
        }

        public IReadOnlyList<int> UniversalBucket => universalBucket;

        public bool TryGetIdBucket(int idSym, out IReadOnlyList<int> list) {
            if (idBucket.TryGetValue(idSym, out var l)) { list = l; return true; }
            list = null;
            return false;
        }

        public bool TryGetClassBucket(int classSym, out IReadOnlyList<int> list) {
            if (classBucket.TryGetValue(classSym, out var l)) { list = l; return true; }
            list = null;
            return false;
        }

        public bool TryGetTagBucket(int tagSym, out IReadOnlyList<int> list) {
            if (tagBucket.TryGetValue(tagSym, out var l)) { list = l; return true; }
            list = null;
            return false;
        }

        public bool TryGetAttributeBucket(int attrSym, out IReadOnlyList<int> list) {
            if (attributeBucket.TryGetValue(attrSym, out var l)) { list = l; return true; }
            list = null;
            return false;
        }

        // Yields candidate selector indices for an element. Order is union of
        // bucket order; selectors land in exactly one bucket so duplicates are
        // not possible across buckets (within-class-list duplicates from
        // duplicated class tokens on the element are filtered).
        // The buffer (if provided non-null) is reused to avoid allocations.
        // attrNames is the snapshot's attribute-name slice for this node (may be empty).
        public IntsBuffer CandidateSelectors(int tagSym, int idSym, ReadOnlySpan<int> classSyms,
            IntsBuffer buffer = null, ReadOnlySpan<int> attrNames = default) {
            buffer ??= new IntsBuffer();
            buffer.Reset();

            if (idSym != 0 && idBucket.TryGetValue(idSym, out var idList)) {
                buffer.AddRange(idList);
            }
            if (tagSym != 0 && tagBucket.TryGetValue(tagSym, out var tagList)) {
                buffer.AddRange(tagList);
            }
            for (int i = 0; i < classSyms.Length; i++) {
                int sym = classSyms[i];
                bool duplicate = false;
                for (int j = 0; j < i; j++) { if (classSyms[j] == sym) { duplicate = true; break; } }
                if (duplicate) continue;
                if (classBucket.TryGetValue(sym, out var clsList)) {
                    buffer.AddRange(clsList);
                }
            }
            for (int i = 0; i < attrNames.Length; i++) {
                int sym = attrNames[i];
                bool duplicate = false;
                for (int j = 0; j < i; j++) { if (attrNames[j] == sym) { duplicate = true; break; } }
                if (duplicate) continue;
                if (attributeBucket.TryGetValue(sym, out var atList)) {
                    buffer.AddRange(atList);
                }
            }
            buffer.AddRange(universalBucket);

            // Each selector lands in exactly one bucket at build time, so the unioned
            // candidate list cannot contain duplicates and no dedup is needed.
            return buffer;
        }
    }
}
