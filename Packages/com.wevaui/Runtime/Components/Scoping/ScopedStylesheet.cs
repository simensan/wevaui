using Weva.Css;

namespace Weva.Components.Scoping {
    public sealed class ScopedStylesheet {
        public ScopeId ScopeId { get; }
        public Stylesheet Original { get; }
        public Stylesheet Rewritten { get; }

        public ScopedStylesheet(ScopeId scopeId, Stylesheet original, Stylesheet rewritten) {
            ScopeId = scopeId;
            Original = original;
            Rewritten = rewritten;
        }
    }
}
