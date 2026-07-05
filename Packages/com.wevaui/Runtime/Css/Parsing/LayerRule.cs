using System.Collections.Generic;

namespace Weva.Css {
    // @layer rule. Two forms:
    //   1. Statement form `@layer base, components, utilities;` — declares
    //      ordering only, no body. `Names` populated, `IsBlock` false.
    //   2. Block form `@layer name { rules }` or anonymous `@layer { rules }`.
    //      Single-name (or null for anonymous) + body. `IsBlock` true,
    //      `Names` has one entry (or null for anonymous).
    //
    // Sub-layers (`@layer base.utilities`) are kept as the dotted name; the
    // cascade engine treats them as a flat layer for v1.
    public sealed class LayerRule : Rule {
        public List<string> Names = new();
        public bool IsBlock;
        public List<Rule> Rules = new();
    }
}
