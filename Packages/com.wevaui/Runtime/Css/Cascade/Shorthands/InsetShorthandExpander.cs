using System.Collections.Generic;

namespace Weva.Css.Cascade.Shorthands {
    // Expands the `inset` shorthand into top/right/bottom/left longhands using the
    // same 1-2-3-4 value pattern as `margin` and `padding`. Unlike margin/padding,
    // the longhand names are bare (no `inset-` prefix), so this can't reuse
    // MarginShorthandExpander directly. `auto` is accepted per side because it is
    // the initial value of top/right/bottom/left.
    public sealed class InsetShorthandExpander : IShorthandExpander {
        public string ShorthandName => "inset";

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var tokens = ShorthandTokenizer.Tokenize(value);
            if (tokens.Count == 0 || tokens.Count > 4) yield break;
            for (int i = 0; i < tokens.Count; i++) {
                if (!IsValidEdge(tokens[i])) yield break;
            }
            string top, right, bottom, left;
            switch (tokens.Count) {
                case 1:
                    top = right = bottom = left = tokens[0];
                    break;
                case 2:
                    top = bottom = tokens[0];
                    right = left = tokens[1];
                    break;
                case 3:
                    top = tokens[0];
                    right = left = tokens[1];
                    bottom = tokens[2];
                    break;
                default:
                    top = tokens[0];
                    right = tokens[1];
                    bottom = tokens[2];
                    left = tokens[3];
                    break;
            }
            yield return new KeyValuePair<string, string>("top", top);
            yield return new KeyValuePair<string, string>("right", right);
            yield return new KeyValuePair<string, string>("bottom", bottom);
            yield return new KeyValuePair<string, string>("left", left);
        }

        static bool IsValidEdge(string s) {
            if (string.Equals(s, "auto", System.StringComparison.OrdinalIgnoreCase)) return true;
            if (s == "0") return true;
            if (ShorthandTokens.IsLengthOrPercentage(s)) return true;
            if (ShorthandTokens.IsCalc(s)) return true;
            return false;
        }
    }
}
