namespace Weva.Css.Selectors {
    public sealed class CompiledSelector {
        internal CompoundSequence Sequence { get; }

        // Original selector text as written by the author, e.g. ".card:hover > .title".
        // Captured at parse/compile time; never null (empty string for edge cases).
        // Not allocated during matching — assigned once during SelectorParser.Parse.
        // For selector-list rules, each compiled selector carries only its own
        // complex-selector text (its slice of the comma-separated list), not the
        // full comma list.
        public string SourceText { get; }

        internal CompiledSelector(CompoundSequence sequence, string sourceText) {
            Sequence   = sequence;
            SourceText = sourceText ?? "";
        }

        public Specificity Specificity => Sequence.Specificity;

        public string PseudoElement => Sequence.PseudoElement;
    }
}
