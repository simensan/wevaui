using System.Collections.Generic;

namespace Weva.Css.Cascade.Shorthands {
    // CSS Multi-column Layout L1 §3.6 — `column-rule` shorthand.
    // Syntax: <column-rule-width> || <column-rule-style> || <column-rule-color>
    // Same grammar as `border` / `outline`.  Missing components reset to their
    // respective initial values: width=medium, style=none, color=currentcolor.
    public sealed class ColumnRuleShorthandExpander : IShorthandExpander {
        public string ShorthandName => "column-rule";

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var tokens = ShorthandTokenizer.Tokenize(value);
            if (tokens.Count == 0 || tokens.Count > 3) yield break;

            string width = "medium";
            string style = "none";
            string color = "currentcolor";
            bool hasWidth = false, hasStyle = false, hasColor = false;

            foreach (var t in tokens) {
                if (!hasStyle && ShorthandTokens.IsBorderStyle(t)) {
                    style = t;
                    hasStyle = true;
                    continue;
                }
                if (!hasWidth && IsBorderWidth(t)) {
                    width = t;
                    hasWidth = true;
                    continue;
                }
                if (!hasColor && ShorthandTokens.IsColor(t)) {
                    color = t;
                    hasColor = true;
                    continue;
                }
                yield break; // unrecognised token → bail
            }

            yield return new KeyValuePair<string, string>("column-rule-width", width);
            yield return new KeyValuePair<string, string>("column-rule-style", style);
            yield return new KeyValuePair<string, string>("column-rule-color", color);
        }

        static bool IsBorderWidth(string s) {
            if (ShorthandTokens.IsBorderWidthKeyword(s)) return true;
            return ShorthandTokens.IsLengthOrPercentage(s) || ShorthandTokens.IsCalc(s);
        }
    }
}
