using System.Collections.Generic;

namespace Weva.Css {
    // CSS Cascade Module Level 5 — `@scope (start) to (end) { ... }`.
    //
    // ScopeStartSelectors: each entry is a comma-separated selector text that
    // identifies "scope-root" elements. May be empty (implicit @scope) — in
    // which case scope-root is treated as the document root.
    //
    // ScopeEndSelectors: optional. When present, the scope ends at any
    // descendant matching one of these selectors (the matching element and
    // its subtree are excluded from the scope).
    //
    // Inner Rules are resolved against scope-roots at cascade time: a rule
    // matches its target only when (a) the target's compound selector matches
    // and (b) the target falls within at least one scope-root's subtree
    // bounded by a scope-end (if any).
    public sealed class ScopeRule : Rule {
        public List<string> ScopeStartSelectors = new();
        public List<string> ScopeEndSelectors = new();
        public List<Rule> Rules = new();
    }
}
