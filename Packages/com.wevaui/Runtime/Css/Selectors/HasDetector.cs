namespace Weva.Css.Selectors {
    // Inspects a CompiledSelector to detect any `:has()` pseudo-class anywhere
    // in its compound sequence (or inside a nested `:is()` / `:where()` /
    // `:not()`). Used by the cascade engine to flip the InvalidationTracker
    // into "has-sensitive" mode so DOM-tree mutations propagate Style dirt up
    // ancestor chains. Cheap O(parts) walk.
    internal static class HasDetector {
        public static bool Contains(CompiledSelector sel) {
            if (sel == null || sel.Sequence == null) return false;
            return ContainsInSequence(sel.Sequence);
        }

        public static bool ContainsPseudo(CompiledSelector sel, PseudoClassKind kind) {
            if (sel == null || sel.Sequence == null) return false;
            return ContainsPseudoInSequence(sel.Sequence, kind);
        }

        static bool ContainsInSequence(CompoundSequence seq) {
            for (int i = 0; i < seq.Compounds.Count; i++) {
                var c = seq.Compounds[i];
                if (c == null) continue;
                foreach (var p in c.Parts) {
                    if (ContainsInSimple(p)) return true;
                }
            }
            return false;
        }

        static bool ContainsInSimple(SimpleSelector part) {
            if (!(part is PseudoClassSelector pc)) return false;
            if (pc.Kind == PseudoClassKind.Has) return true;
            if (pc.InnerList != null) {
                foreach (var seq in pc.InnerList) {
                    if (ContainsInSequence(seq)) return true;
                }
            }
            if (pc.InnerSimple != null && ContainsInSimple(pc.InnerSimple)) return true;
            return false;
        }

        static bool ContainsPseudoInSequence(CompoundSequence seq, PseudoClassKind kind) {
            for (int i = 0; i < seq.Compounds.Count; i++) {
                var c = seq.Compounds[i];
                if (c == null) continue;
                foreach (var p in c.Parts) {
                    if (ContainsPseudoInSimple(p, kind)) return true;
                }
            }
            return false;
        }

        static bool ContainsPseudoInSimple(SimpleSelector part, PseudoClassKind kind) {
            if (!(part is PseudoClassSelector pc)) return false;
            if (pc.Kind == kind) return true;
            if (pc.InnerList != null) {
                foreach (var seq in pc.InnerList) {
                    if (ContainsPseudoInSequence(seq, kind)) return true;
                }
            }
            if (pc.InnerSimple != null && ContainsPseudoInSimple(pc.InnerSimple, kind)) return true;
            return false;
        }

        // ── CX2: shape-cache position-dependence detection ────────────────
        //
        // The cascade's shape-keyed match cache hashes tag/id/class/attrs/
        // ancestors/state — NOT sibling position. Two classes of selector
        // break that assumption:
        //
        //   INDEX-positional (representable): :first/:last/:only-child,
        //   :nth-child, :nth-last-child, :empty — their outcome is a
        //   function of (element index, sibling count, own child count),
        //   which TryComputeShapeKey folds into the key when present.
        //
        //   COMPOSITION-dependent (NOT representable): sibling combinators
        //   (`p + p`, `.a ~ .b`) and the *-of-type pseudos — their outcome
        //   depends on WHICH tags precede the element, which the key cannot
        //   capture. Shape sharing is disabled outright for such sheets.

        public static bool ContainsIndexPositionalPseudo(CompiledSelector sel) {
            if (sel == null || sel.Sequence == null) return false;
            var seq = sel.Sequence;
            return ContainsPseudoInSequence(seq, PseudoClassKind.FirstChild)
                || ContainsPseudoInSequence(seq, PseudoClassKind.LastChild)
                || ContainsPseudoInSequence(seq, PseudoClassKind.OnlyChild)
                || ContainsPseudoInSequence(seq, PseudoClassKind.NthChild)
                || ContainsPseudoInSequence(seq, PseudoClassKind.NthLastChild)
                || ContainsPseudoInSequence(seq, PseudoClassKind.Empty);
        }

        public static bool ContainsSiblingCompositionDependence(CompiledSelector sel) {
            if (sel == null || sel.Sequence == null) return false;
            return ContainsSiblingCompositionInSequence(sel.Sequence);
        }

        static bool ContainsSiblingCompositionInSequence(CompoundSequence seq) {
            if (seq == null) return false;
            // Sibling combinators at THIS nesting level.
            if (seq.Combinators != null) {
                for (int i = 0; i < seq.Combinators.Count; i++) {
                    var comb = seq.Combinators[i];
                    if (comb == Combinator.AdjacentSibling || comb == Combinator.GeneralSibling) return true;
                }
            }
            for (int i = 0; i < seq.Compounds.Count; i++) {
                var c = seq.Compounds[i];
                if (c == null) continue;
                foreach (var p in c.Parts) {
                    if (ContainsSiblingCompositionInSimple(p)) return true;
                }
            }
            return false;
        }

        static bool ContainsSiblingCompositionInSimple(SimpleSelector part) {
            if (!(part is PseudoClassSelector pc)) return false;
            switch (pc.Kind) {
                case PseudoClassKind.FirstOfType:
                case PseudoClassKind.LastOfType:
                case PseudoClassKind.OnlyOfType:
                case PseudoClassKind.NthOfType:
                case PseudoClassKind.NthLastOfType:
                    return true;
            }
            // Recurse into :is/:where/:not/:has inner selectors — a sibling
            // combinator inside `:has(p + p)` is just as key-breaking.
            if (pc.InnerList != null) {
                foreach (var seq in pc.InnerList) {
                    if (ContainsSiblingCompositionInSequence(seq)) return true;
                }
            }
            if (pc.InnerSimple != null && ContainsSiblingCompositionInSimple(pc.InnerSimple)) return true;
            return false;
        }
    }
}
