using System.Collections.Generic;
using Weva.Css.Values;

namespace Weva.Css.Cascade.Shorthands {
    // Expands the `font` shorthand. Required: <font-size> [/<line-height>] <font-family>
    // Optional, in any order before size: <font-style>, <font-variant>, <font-weight>.
    // Per CSS spec, the shorthand resets font-style/variant/weight/line-height to their
    // initial values when omitted.
    public sealed class FontShorthandExpander : IShorthandExpander {
        public string ShorthandName => "font";

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var tokens = ShorthandTokenizer.Tokenize(value);
            if (tokens.Count == 0) yield break;

            // CSS Fonts L4 §3.6: the `font` shorthand also accepts a system-font
            // keyword on its own. We map each to a UA default rather than querying
            // the host OS; values are deliberately consistent across platforms.
            if (tokens.Count == 1 && TryExpandSystemFont(tokens[0], out var sysSize, out var sysFamily)) {
                yield return new KeyValuePair<string, string>("font-style", "normal");
                yield return new KeyValuePair<string, string>("font-variant", "normal");
                yield return new KeyValuePair<string, string>("font-weight", "normal");
                yield return new KeyValuePair<string, string>("font-size", sysSize);
                yield return new KeyValuePair<string, string>("line-height", "normal");
                yield return new KeyValuePair<string, string>("font-family", sysFamily);
                yield return new KeyValuePair<string, string>("font-variant-numeric", "normal");
                yield return new KeyValuePair<string, string>("font-variation-settings", "normal");
                yield return new KeyValuePair<string, string>("font-feature-settings", "normal");
                yield return new KeyValuePair<string, string>("font-optical-sizing", "auto");
                yield break;
            }

            if (tokens.Count < 2) yield break;

            string fontStyle = "normal";
            string fontVariant = "normal";
            string fontWeight = "normal";
            string lineHeight = "normal";
            bool hasStyle = false, hasVariant = false, hasWeight = false;

            int i = 0;
            // Consume up to 3 leading style/variant/weight keywords (each at most once;
            // each may be `normal`, which doesn't disambiguate but is allowed).
            while (i < tokens.Count && i < 3) {
                string t = tokens[i];
                bool consumed = false;
                if (!hasStyle && ShorthandTokens.IsFontStyleKeyword(t) && !CssStringUtil.EqualsIgnoreCase(t, "normal")) {
                    fontStyle = t;
                    hasStyle = true;
                    consumed = true;
                } else if (!hasVariant && CssStringUtil.EqualsIgnoreCase(t, "small-caps")) {
                    fontVariant = t;
                    hasVariant = true;
                    consumed = true;
                } else if (!hasWeight && ShorthandTokens.IsFontWeightKeyword(t) && !CssStringUtil.EqualsIgnoreCase(t, "normal")) {
                    fontWeight = t;
                    hasWeight = true;
                    consumed = true;
                } else if (CssStringUtil.EqualsIgnoreCase(t, "normal")) {
                    // Ambiguous "normal" — assign to the first slot still unset.
                    if (!hasStyle) hasStyle = true;
                    else if (!hasVariant) hasVariant = true;
                    else if (!hasWeight) hasWeight = true;
                    else break;
                    consumed = true;
                }
                if (!consumed) break;
                i++;
            }

            // Now <font-size>, possibly `/<line-height>`, then <font-family>.
            if (i >= tokens.Count) yield break;
            string fontSize = tokens[i];
            if (!IsFontSize(fontSize)) yield break;
            i++;

            if (i < tokens.Count && tokens[i] == "/") {
                i++;
                if (i >= tokens.Count) yield break;
                lineHeight = tokens[i];
                if (!IsLineHeight(lineHeight)) yield break;
                i++;
            }

            if (i >= tokens.Count) yield break;
            // Remaining tokens form the font-family value (may include commas and quoted strings).
            var familyTokens = new List<string>();
            for (int k = i; k < tokens.Count; k++) familyTokens.Add(tokens[k]);
            string family = JoinFamily(familyTokens);
            if (string.IsNullOrEmpty(family)) yield break;

            yield return new KeyValuePair<string, string>("font-style", fontStyle);
            yield return new KeyValuePair<string, string>("font-variant", fontVariant);
            yield return new KeyValuePair<string, string>("font-weight", fontWeight);
            yield return new KeyValuePair<string, string>("font-size", fontSize);
            yield return new KeyValuePair<string, string>("line-height", lineHeight);
            yield return new KeyValuePair<string, string>("font-family", family);
            // CSS Fonts L4 §17.7: "All subproperties of the font property are
            // first reset to their initial values, including ... all
            // subproperties of font-variant ...". These longhands are
            // separately registered in CssProperties.cs and inherit, so
            // without an explicit reset here a parent declaration leaks
            // past a child's `font: 16px serif`.
            yield return new KeyValuePair<string, string>("font-variant-numeric", "normal");
            yield return new KeyValuePair<string, string>("font-variation-settings", "normal");
            yield return new KeyValuePair<string, string>("font-feature-settings", "normal");
            yield return new KeyValuePair<string, string>("font-optical-sizing", "auto");
        }

        static bool TryExpandSystemFont(string token, out string size, out string family) {
            const string DefaultFamily = "system-ui, sans-serif";
            if (CssStringUtil.EqualsIgnoreCase(token, "caption")
                || CssStringUtil.EqualsIgnoreCase(token, "icon")
                || CssStringUtil.EqualsIgnoreCase(token, "menu")
                || CssStringUtil.EqualsIgnoreCase(token, "message-box")
                || CssStringUtil.EqualsIgnoreCase(token, "status-bar")) {
                size = "13px";
                family = DefaultFamily;
                return true;
            }
            if (CssStringUtil.EqualsIgnoreCase(token, "small-caption")) {
                size = "11px";
                family = DefaultFamily;
                return true;
            }
            size = null;
            family = null;
            return false;
        }

        static bool IsFontSize(string s) {
            if (ShorthandTokens.IsAbsoluteFontSizeKeyword(s)) return true;
            if (ShorthandTokens.IsRelativeFontSizeKeyword(s)) return true;
            if (s == "0") return true;
            if (ShorthandTokens.IsLengthOrPercentage(s)) return true;
            if (ShorthandTokens.IsCalc(s)) return true;
            return false;
        }

        static bool IsLineHeight(string s) {
            if (string.Equals(s, "normal", System.StringComparison.OrdinalIgnoreCase)) return true;
            if (ShorthandTokens.IsNumber(s)) return true;
            if (ShorthandTokens.IsLengthOrPercentage(s)) return true;
            if (ShorthandTokens.IsCalc(s)) return true;
            return false;
        }

        // Joins family tokens with single spaces, preserving commas. Quoted strings and
        // unquoted runs both pass through unchanged.
        static string JoinFamily(List<string> tokens) {
            var sb = new System.Text.StringBuilder();
            bool first = true;
            foreach (var t in tokens) {
                if (t == ",") {
                    sb.Append(", ");
                    first = true;
                    continue;
                }
                if (!first) sb.Append(' ');
                sb.Append(t);
                first = false;
            }
            return sb.ToString().Trim();
        }
    }
}
