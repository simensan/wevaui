using Weva.Css.Selectors;

namespace Weva.Css.Cascade {
    // CSS Cascade 4 §6.2 — one declaration that matched an element's selector
    // set. Public so StyleInspector (DevTools W7) can surface the cascade trace
    // without requiring DevTools to depend on an internal type. Normal production
    // code should treat this as a cascade-internal detail accessed only via the
    // CollectMatchesFor DevTools hook.
    public readonly struct MatchedDeclaration {
        public readonly Declaration Declaration;
        public readonly DeclarationOrigin Origin;
        public readonly Specificity Specificity;
        public readonly int SourceIndex;
        public readonly bool IsInline;
        // Tiebreak when two declarations share SourceIndex (e.g. multiple declarations
        // inside the same rule, or longhands produced by shorthand expansion). Lower =
        // earlier in the source = lower precedence. Defaults to 0 for backward compatibility.
        public readonly int InRuleIndex;
        // CSS Cascade Module Level 5 §6.4.1 step 4 — layer order axis. Higher
        // wins for normal declarations; the comparator inverts for !important
        // (step 5). Unlayered rules use CssLayer.UnlayeredOrdinal so they
        // beat every layered rule.
        public readonly int LayerOrdinal;
        // Human-readable selector text as authored (e.g. ".card:hover > .title").
        // Null for inline styles (they have no selector). Populated from
        // CompiledSelector.SourceText at match time — the string is already
        // interned at compile time so this field costs one pointer copy only.
        // Never allocated during matching.
        public readonly string SelectorText;

        public MatchedDeclaration(Declaration declaration, DeclarationOrigin origin, Specificity specificity, int sourceIndex, bool isInline)
            : this(declaration, origin, specificity, sourceIndex, isInline, 0, CssLayer.UnlayeredOrdinal, null) { }

        public MatchedDeclaration(Declaration declaration, DeclarationOrigin origin, Specificity specificity, int sourceIndex, bool isInline, int inRuleIndex)
            : this(declaration, origin, specificity, sourceIndex, isInline, inRuleIndex, CssLayer.UnlayeredOrdinal, null) { }

        public MatchedDeclaration(Declaration declaration, DeclarationOrigin origin, Specificity specificity, int sourceIndex, bool isInline, int inRuleIndex, int layerOrdinal)
            : this(declaration, origin, specificity, sourceIndex, isInline, inRuleIndex, layerOrdinal, null) { }

        public MatchedDeclaration(Declaration declaration, DeclarationOrigin origin, Specificity specificity, int sourceIndex, bool isInline, int inRuleIndex, int layerOrdinal, string selectorText) {
            Declaration  = declaration;
            Origin       = origin;
            Specificity  = specificity;
            SourceIndex  = sourceIndex;
            IsInline     = isInline;
            InRuleIndex  = inRuleIndex;
            LayerOrdinal = layerOrdinal;
            SelectorText = selectorText;
        }
    }
}
