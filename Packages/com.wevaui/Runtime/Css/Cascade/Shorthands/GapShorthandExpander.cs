using System.Collections.Generic;

namespace Weva.Css.Cascade.Shorthands {
    public sealed class GapShorthandExpander : IShorthandExpander {
        public string ShorthandName => "gap";

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var tokens = ShorthandTokenizer.Tokenize(value);
            if (tokens.Count == 0 || tokens.Count > 2) yield break;
            foreach (var t in tokens) {
                if (!IsGapValue(t)) yield break;
            }
            string row = tokens[0];
            string col = tokens.Count == 2 ? tokens[1] : tokens[0];
            yield return new KeyValuePair<string, string>("row-gap", row);
            yield return new KeyValuePair<string, string>("column-gap", col);
        }

        static bool IsGapValue(string s) {
            if (string.Equals(s, "normal", System.StringComparison.OrdinalIgnoreCase)) return true;
            if (s == "0") return true;
            if (ShorthandTokens.IsLengthOrPercentage(s)) return true;
            if (ShorthandTokens.IsCalc(s)) return true;
            return false;
        }
    }
}
