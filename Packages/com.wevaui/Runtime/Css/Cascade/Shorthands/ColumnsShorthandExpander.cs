using System;
using System.Collections.Generic;
using System.Globalization;
using Weva.Css.Values;

namespace Weva.Css.Cascade.Shorthands {
    // CSS Multi-column Layout L1 §3.4 — `columns` shorthand.
    // Syntax: columns: <column-width> || <column-count>
    // where <column-width> is a <length> or `auto`,
    //   and <column-count> is a positive <integer> or `auto`.
    // One or two tokens in any order. Missing component resets to `auto`.
    public sealed class ColumnsShorthandExpander : IShorthandExpander {
        public string ShorthandName => "columns";

        public IEnumerable<KeyValuePair<string, string>> Expand(string value) {
            var tokens = ShorthandTokenizer.Tokenize(value);
            if (tokens.Count == 0 || tokens.Count > 2) yield break;

            string width = "auto";
            string count = "auto";
            bool hasWidth = false, hasCount = false;

            foreach (var t in tokens) {
                string lower = CssStringUtil.ToLowerInvariantOrSame(t);
                if (!hasCount && IsColumnCount(lower)) {
                    count = t;
                    hasCount = true;
                    continue;
                }
                if (!hasWidth && IsColumnWidth(lower)) {
                    width = t;
                    hasWidth = true;
                    continue;
                }
                // `auto` is ambiguous — assign to whichever hasn't been set yet.
                if (lower == "auto") {
                    if (!hasWidth) { width = "auto"; hasWidth = true; }
                    else if (!hasCount) { count = "auto"; hasCount = true; }
                    continue;
                }
                yield break; // unrecognised token → bail
            }

            yield return new KeyValuePair<string, string>("column-width", width);
            yield return new KeyValuePair<string, string>("column-count", count);
        }

        // A column-count token is a positive integer (not `auto`, which is handled above).
        static bool IsColumnCount(string s) {
            if (s == "auto") return false;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) {
                return n >= 1;
            }
            return false;
        }

        // A column-width token is a CSS <length> (not a bare integer, not `auto`).
        static bool IsColumnWidth(string s) {
            if (s == "auto") return false;
            // Reject bare integers — those are column-count candidates.
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return false;
            return ShorthandTokens.IsLengthOrPercentage(s) || ShorthandTokens.IsCalc(s);
        }
    }
}
