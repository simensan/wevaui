using System.Collections.Generic;

namespace Weva.Css.Cascade.Shorthands {
    // CSS UI 4 §7.1: `outline` is a shorthand for outline-width, outline-style,
    // outline-color (in any order). Missing components reset to initial values
    // per spec: width → medium, style → none, color → invert. Mirrors the
    // BorderShorthandExpander triplet parser; intentionally narrow — outline
    // does not have per-side longhands in the spec we target.
    public sealed class OutlineShorthandExpander : IShorthandExpander {
        public string ShorthandName => "outline";

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var tokens = ShorthandTokenizer.Tokenize(value ?? "");
            return ExpandTokens(tokens);
        }

        static IEnumerable<KeyValuePair<string, string>> ExpandTokens(List<string> tokens) {
            string width = "medium";
            string style = "none";
            string color = "invert";
            if (tokens.Count == 0 || tokens.Count > 3) yield break;
            bool hasWidth = false, hasStyle = false, hasColor = false;
            foreach (var t in tokens) {
                if (!hasStyle && ShorthandTokens.IsBorderStyle(t)) {
                    style = t; hasStyle = true; continue;
                }
                if (!hasWidth && IsWidth(t)) {
                    width = t; hasWidth = true; continue;
                }
                if (!hasColor && (t == "invert" || ShorthandTokens.IsColor(t))) {
                    color = t; hasColor = true; continue;
                }
                yield break;
            }
            yield return new KeyValuePair<string, string>("outline-width", width);
            yield return new KeyValuePair<string, string>("outline-style", style);
            yield return new KeyValuePair<string, string>("outline-color", color);
        }

        static bool IsWidth(string s) {
            if (ShorthandTokens.IsBorderWidthKeyword(s)) return true;
            if (s == "0") return true;
            if (ShorthandTokens.IsLength(s)) return true;
            if (ShorthandTokens.IsCalc(s)) return true;
            return false;
        }
    }
}
