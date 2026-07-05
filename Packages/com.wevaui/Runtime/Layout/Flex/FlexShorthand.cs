using System.Collections.Generic;
using Weva.Css.Values;

namespace Weva.Layout.Flex {
    internal static class FlexShorthand {
        public struct Result {
            public bool HasValue;
            public double Grow;
            public double Shrink;
            public FlexBasis Basis;
        }

        // String-keyed entry point preserved for legacy callers and tests
        // (FlexShorthandTests). Funnels through CssValue.TryParse exactly
        // once and then dispatches on the typed parse tree via ParseValue
        // so the keyword + numeric/length token-walking logic isn't
        // duplicated.
        public static Result Parse(string raw, LengthContext lengthCtx) {
            var r = default(Result);
            if (string.IsNullOrEmpty(raw)) return r;
            if (!CssValue.TryParse(raw.Trim(), out var v)) return r;
            return ParseValue(v, lengthCtx);
        }

        // Typed entry point used by FlexItemProperties on the cascade hot
        // path. Operates directly on the parsed CssValue handed back by
        // ComputedStyle.GetParsed, avoiding the round-trip through
        // CssValue.TryParse on every layout pass.
        //
        // Shorthand grammar (CSS Flexbox L1 §7.2):
        //   none      → 0 0 auto
        //   auto      → 1 1 auto
        //   initial   → 0 1 auto
        //   <number>                    → grow [shrink=1] basis=0
        //   <number> <number>           → grow shrink basis=0
        //   <number> <basis>            → grow [shrink=1] basis
        //   <number> <number> <basis>   → grow shrink basis
        //   <basis>                     → grow=1 shrink=1 basis
        public static Result ParseValue(CssValue v, LengthContext lengthCtx) {
            var r = default(Result);
            if (v == null) return r;

            // Single-keyword forms come back from the parser as CssKeyword
            // (lowercased Identifier) or CssIdentifier (case-preserved). The
            // cascade expander already handles these strings; here we cover
            // the direct-Set test path.
            if (v is CssKeyword sk) {
                if (TryApplyKeyword(sk.Identifier, ref r)) return r;
            } else if (v is CssIdentifier si) {
                if (TryApplyKeyword(CssStringUtil.ToLowerInvariantOrSame(si.Name), ref r)) return r;
            }

            // Multi-token form: space-separated CssValueList of numbers and
            // a length/percent/keyword basis. Single non-list values (a bare
            // <number>, <length>, etc.) are treated as a 1-item list.
            IReadOnlyList<CssValue> parts;
            if (v is CssValueList vl && vl.Separator == CssValueListSeparator.Space) {
                parts = vl.Items;
            } else {
                sSingle[0] = v;
                parts = sSingle;
            }
            if (parts.Count == 0) return r;

            bool hasGrow = false, hasBasis = false;
            double grow = 0, shrink = 1;
            FlexBasis basis = FlexBasis.Length(0);

            for (int i = 0; i < parts.Count; i++) {
                var p = parts[i];
                if (!hasGrow && p is CssNumber nGrow) {
                    grow = nGrow.Value;
                    hasGrow = true;
                    if (i + 1 < parts.Count && parts[i + 1] is CssNumber nShrink) {
                        shrink = nShrink.Value;
                        i++;
                    }
                    continue;
                }
                if (TryReadBasis(p, lengthCtx, out var b)) {
                    basis = b;
                    hasBasis = true;
                    continue;
                }
            }

            if (!hasGrow && !hasBasis) return r;

            if (hasGrow && !hasBasis) {
                basis = FlexBasis.Length(0);
                hasBasis = true;
            }
            if (hasBasis && !hasGrow) {
                grow = 1;
                shrink = 1;
                hasGrow = true;
            }

            r.HasValue = true;
            r.Grow = grow;
            r.Shrink = shrink;
            r.Basis = basis;
            return r;
        }

        // Single-element scratch buffer used to fake an IReadOnlyList for
        // the non-list single-value path. Single-threaded under the layout
        // engine; cleared on entry to defend against re-entrancy from
        // recursive layout invocations (Element children).
        static readonly CssValue[] sSingle = new CssValue[1];

        static bool TryApplyKeyword(string id, ref Result r) {
            switch (id) {
                case "none":
                    r.HasValue = true;
                    r.Grow = 0; r.Shrink = 0; r.Basis = FlexBasis.Auto;
                    return true;
                case "auto":
                    r.HasValue = true;
                    r.Grow = 1; r.Shrink = 1; r.Basis = FlexBasis.Auto;
                    return true;
                case "initial":
                    r.HasValue = true;
                    r.Grow = 0; r.Shrink = 1; r.Basis = FlexBasis.Auto;
                    return true;
            }
            return false;
        }

        static bool TryReadBasis(CssValue v, LengthContext lengthCtx, out FlexBasis basis) {
            if (v is CssLength l) {
                basis = FlexBasis.Length(l.ToPixels(lengthCtx));
                return true;
            }
            if (v is CssPercentage p) {
                basis = FlexBasis.Percentage(p.Value);
                return true;
            }
            if (v is CssNumber n && n.Value == 0) {
                basis = FlexBasis.Length(0);
                return true;
            }
            if (v is CssCalc c) {
                try {
                    basis = FlexBasis.Length(c.Evaluate(lengthCtx));
                    return true;
                } catch {
                    basis = FlexBasis.Auto;
                    return false;
                }
            }
            if (v is CssKeyword k) {
                string id = k.Identifier;
                if (id == "auto") { basis = FlexBasis.Auto; return true; }
                if (id == "content") { basis = FlexBasis.Content; return true; }
            }
            if (v is CssIdentifier ci) {
                string id = CssStringUtil.ToLowerInvariantOrSame(ci.Name);
                if (id == "auto") { basis = FlexBasis.Auto; return true; }
                if (id == "content") { basis = FlexBasis.Content; return true; }
            }
            basis = FlexBasis.Auto;
            return false;
        }
    }
}
