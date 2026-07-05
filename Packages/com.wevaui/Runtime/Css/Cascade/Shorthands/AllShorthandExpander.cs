using System.Collections.Generic;
using Weva.Css.Values;

namespace Weva.Css.Cascade.Shorthands {
    // CSS Cascade L4 §3.2 — `all` shorthand resets every property to the
    // specified CSS-wide keyword, EXCEPT `direction`, `unicode-bidi`, and
    // custom properties (`--*`). It accepts only the four CSS-wide keywords
    // plus `revert-layer` (CSS Cascade L5 §6.3).
    //
    // Per CSS Cascade L4 §3.2, `all` itself is a shorthand — not a longhand
    // — so we do NOT register it in `CssProperties`. The cascade engine
    // looks up shorthands via `ShorthandRegistry.TryGet(name, ...)` which
    // is independent of `CssProperties` registration.
    //
    // Expansion enumerates the live `CssProperties` registry at expansion
    // time so late `Register()` calls (anchor positioning, subgrid helpers)
    // automatically participate. Shorthand entries that also live in the
    // property registry (e.g. `margin`, `border`, `font`) are filtered out
    // — `all: initial` must reset the LONGHANDS, not the shorthand slot
    // they're rooted at, otherwise the same value is set twice and the
    // shorthand-form initial (e.g. `"medium none currentColor"` for
    // `border`) would compete with each longhand's correct initial.
    public sealed class AllShorthandExpander : IShorthandExpander {
        public string ShorthandName => "all";

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var v = CssStringUtil.ToLowerInvariantOrSame((value ?? "").Trim());
            switch (v) {
                case "initial":
                case "inherit":
                case "unset":
                case "revert":
                case "revert-layer":
                    break;
                default:
                    yield break;
            }
            int n = CssProperties.RegisteredCount;
            for (int id = 0; id < n; id++) {
                var name = CssProperties.GetName(id);
                if (name == null) continue;
                if (name == "direction" || name == "unicode-bidi") continue;
                if (ShorthandRegistry.IsShorthand(name)) continue;
                yield return new KeyValuePair<string, string>(name, v);
            }
        }
    }
}
