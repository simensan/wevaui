using Weva.Css.Values;

namespace Weva.Css.Animation {
    public sealed class RunningTransitionRecord {
        public string Property;
        public string FromText;
        public string ToText;
        // Pre-parsed endpoints. Populated once at StartTransitionFor so the
        // per-frame Tick path skips CssValue.TryParse on both sides.
        // Either field can be null when the raw value didn't round-trip
        // through the parser (e.g. an "auto" identifier on a length-kind
        // transition) — the interpolator's typed paths fall back to the
        // raw string in that case for discrete-step semantics.
        public CssValue FromParsed;
        public CssValue ToParsed;
        public double StartTimeSeconds;
        public TransitionSpec Spec;
        public PropertyKind Kind;
        public string CurrentText;
        public bool Finished;
        // Original "from" of the FIRST transition in this reverse chain — kept
        // across retargets so the reversing-shortening rule (CSS Transitions
        // L1 §3) can detect when a new target equals the chain's original
        // start value and shorten the new duration proportionally.
        public string OriginalFromText;
    }
}
