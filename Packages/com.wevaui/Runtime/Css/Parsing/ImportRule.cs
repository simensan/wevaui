namespace Weva.Css {
    public sealed class ImportRule : Rule {
        public string Href;
        public string MediaConditionText;
        // CSS Cascade L5 §3.3 — `@import url(x) layer(name);` or anonymous
        // `@import url(x) layer;`. `HasLayer` distinguishes "no layer
        // qualifier" from "anonymous layer" (both have null `Layer`).
        // Accept-and-stash only; layer membership of the imported rules is
        // not yet wired through to the cascade.
        public bool HasLayer;
        public string Layer;
        // CSS Cascade L5 §3.3 — `@import url(x) supports(<condition>);`.
        // Per grammar, `supports(...)` follows the optional `layer` clause
        // and precedes the media list. Accept-and-stash only; the import
        // is NOT yet gated on the supports() result (AtImportLoader still
        // lumps any leftover text through MatchesMedia).
        public bool HasSupports;
        public string SupportsText;
    }
}
