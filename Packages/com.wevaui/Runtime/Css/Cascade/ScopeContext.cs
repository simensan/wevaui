using System.Collections.Generic;
using Weva.Css.Selectors;
using Weva.Dom;

namespace Weva.Css.Cascade {
    // Walks ancestors at cascade time to determine whether a given target
    // element falls within a `@scope (start) [to (end)]` window.
    //
    // An element E is within scope iff there exists an ancestor (or E itself)
    // that matches one of the scope-start selectors, AND no ancestor strictly
    // between that scope-start and E (inclusive of E or the scope-start? See
    // spec) matches a scope-end selector.
    //
    // V1 simplification:
    //   * Scope-start selectors are matched as flat compound sequences using
    //     SelectorMatcher.Matches. Pre-parsed selectors are cached on the
    //     ScopeContext so the parse cost amortizes across cascade passes.
    //   * Scope-end selectors are matched the same way; an end-match strictly
    //     between scope-start (exclusive) and E (inclusive) excludes E.
    //   * Implicit `@scope { ... }` (no start) treats the document root as
    //     scope-start: every element is in-scope.
    public sealed class ScopeContext {
        readonly List<CompiledSelector> startSelectors;
        readonly List<CompiledSelector> endSelectors;
        readonly bool implicitStart;

        public ScopeContext(List<string> startTexts, List<string> endTexts) {
            startSelectors = new List<CompiledSelector>();
            endSelectors = new List<CompiledSelector>();
            implicitStart = startTexts == null || startTexts.Count == 0;
            if (startTexts != null) {
                foreach (var t in startTexts) {
                    var c = TryParse(t);
                    if (c != null) startSelectors.Add(c);
                }
            }
            if (endTexts != null) {
                foreach (var t in endTexts) {
                    var c = TryParse(t);
                    if (c != null) endSelectors.Add(c);
                }
            }
        }

        public bool ImplicitStart => implicitStart;

        // Returns true iff `target` falls within at least one start..end window.
        // Walks from `target` upward, examining each ancestor as a potential
        // scope-start. The first matching scope-start that is not separated
        // from `target` by an end-match wins. The walk also matches `target`
        // itself against scope-start (the start element is in-scope).
        public bool Contains(Element target, IElementStateProvider state) {
            if (target == null) return false;
            if (implicitStart) return true;
            return FindScopeRoot(target, state) != null;
        }

        public Element FindScopeRoot(Element target, IElementStateProvider state) {
            if (target == null) return null;
            if (implicitStart) return DocumentRoot(target);

            for (var node = (Node)target; node != null; node = node.Parent) {
                if (!(node is Element anc)) continue;
                if (!StartMatches(anc, state)) continue;
                // Walk back down from `anc` toward `target` checking that no
                // intermediate ancestor is an end-match (which would put
                // `target` outside the scope window). The boundary is
                // exclusive on the start side: an end-match AT the
                // scope-start element does not exclude descendants.
                if (!CrossesEnd(anc, target, state, anc)) return anc;
            }
            return null;
        }

        static Element DocumentRoot(Element e) {
            var doc = e?.OwnerDocument;
            if (doc == null) return null;
            foreach (var c in doc.Children) {
                if (c is Element root) return root;
            }
            return null;
        }

        bool StartMatches(Element e, IElementStateProvider state) {
            for (int i = 0; i < startSelectors.Count; i++) {
                if (SelectorMatcher.Matches(startSelectors[i], e, state)) return true;
            }
            return false;
        }

        // Per CSS Cascade L5 §3.4, the end selector is scoped to the scope
        // root: `:scope` resolves to the matched start element and relative
        // combinators are evaluated against it. The caller threads the
        // current start as `scopeRoot`.
        bool EndMatches(Element e, IElementStateProvider state, Element scopeRoot) {
            if (endSelectors.Count == 0) return false;
            for (int i = 0; i < endSelectors.Count; i++) {
                if (SelectorMatcher.Matches(endSelectors[i], e, state, scopeRoot)) return true;
            }
            return false;
        }

        // Returns true iff some element strictly between `start` (exclusive)
        // and `target` (inclusive) matches a scope-end selector. The scope-end
        // is treated like the spec's "lower boundary" — at-and-below the
        // matching element, the scope no longer applies.
        bool CrossesEnd(Element start, Element target, IElementStateProvider state, Element scopeRoot) {
            if (endSelectors.Count == 0) return false;
            for (var node = (Node)target; node != null && node != start; node = node.Parent) {
                if (node is Element e && EndMatches(e, state, scopeRoot)) return true;
            }
            return false;
        }

        static CompiledSelector TryParse(string text) {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try {
                return SelectorParser.Parse(text);
            } catch {
                return null;
            }
        }
    }
}
