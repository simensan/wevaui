using System.Collections.Generic;
using Weva.Css.Values;

namespace Weva.Css.Cascade.Shorthands {
    // Expands `flex: ...` to `flex-grow`, `flex-shrink`, `flex-basis`. Mirrors the
    // existing layout-side FlexShorthand parser but emits string longhands so the
    // cascade can store them like any other property.
    public sealed class FlexShorthandExpander : IShorthandExpander {
        public string ShorthandName => "flex";

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var tokens = ShorthandTokenizer.Tokenize(value);
            if (tokens.Count == 0 || tokens.Count > 3) yield break;
            // Reject commas/slashes
            for (int i = 0; i < tokens.Count; i++) {
                if (tokens[i] == "," || tokens[i] == "/") yield break;
            }

            // Single-keyword forms.
            if (tokens.Count == 1) {
                string lower = CssStringUtil.ToLowerInvariantOrSame(tokens[0]);
                switch (lower) {
                    case "none":
                        yield return new KeyValuePair<string, string>("flex-grow", "0");
                        yield return new KeyValuePair<string, string>("flex-shrink", "0");
                        yield return new KeyValuePair<string, string>("flex-basis", "auto");
                        yield break;
                    case "auto":
                        yield return new KeyValuePair<string, string>("flex-grow", "1");
                        yield return new KeyValuePair<string, string>("flex-shrink", "1");
                        yield return new KeyValuePair<string, string>("flex-basis", "auto");
                        yield break;
                    case "initial":
                        yield return new KeyValuePair<string, string>("flex-grow", "0");
                        yield return new KeyValuePair<string, string>("flex-shrink", "1");
                        yield return new KeyValuePair<string, string>("flex-basis", "auto");
                        yield break;
                }
            }

            // Numeric / length tokens. Per CSS Flexbox Module Level 1 §7, an unadorned
            // <number> binds to flex-grow first (then flex-shrink); a <length>/<percentage>
            // or basis keyword binds to flex-basis. So `flex: 0 1 400px` parses as
            // grow=0, shrink=1, basis=400px even though "0" is also a valid basis length.
            // Once two numbers have been seen, a subsequent unitless number is interpreted
            // as basis (e.g. `flex: 1 1 0`).
            string grow = null, shrink = null, basis = null;
            int numbersConsumed = 0;
            for (int i = 0; i < tokens.Count; i++) {
                string t = tokens[i];
                bool isUnitlessNumber = ShorthandTokens.IsNumber(t) && !ShorthandTokens.IsPercentage(t) && !HasUnit(t);
                if (isUnitlessNumber && numbersConsumed < 2) {
                    if (numbersConsumed == 0) grow = t;
                    else shrink = t;
                    numbersConsumed++;
                    continue;
                }
                if (IsBasisKeyword(t)) {
                    if (basis != null) yield break;
                    basis = t;
                    continue;
                }
                if (ShorthandTokens.IsLengthOrPercentage(t) || ShorthandTokens.IsCalc(t) || isUnitlessNumber) {
                    if (basis != null) yield break;
                    basis = t;
                    continue;
                }
                yield break;
            }

            if (grow == null && basis == null) yield break;

            if (grow != null && basis == null) {
                basis = "0";
            }
            if (basis != null && grow == null) {
                grow = "1";
                shrink = "1";
            }
            if (shrink == null) shrink = "1";
            if (basis == null) basis = "0";

            yield return new KeyValuePair<string, string>("flex-grow", grow);
            yield return new KeyValuePair<string, string>("flex-shrink", shrink);
            yield return new KeyValuePair<string, string>("flex-basis", basis);
        }

        // True when the token has a unit suffix (e.g. "10px", "5%", "1em"). Used to
        // distinguish unitless numbers (grow/shrink) from lengths (basis).
        static bool HasUnit(string s) {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++) {
                char c = s[i];
                if (char.IsLetter(c) || c == '%') return true;
            }
            return false;
        }

        static bool IsBasisKeyword(string s) {
            switch (CssStringUtil.ToLowerInvariantOrSame(s)) {
                case "auto": case "content": case "min-content": case "max-content": case "fit-content":
                    return true;
                default:
                    return false;
            }
        }
    }
}
