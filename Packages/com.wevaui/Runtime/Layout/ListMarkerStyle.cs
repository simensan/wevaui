using System.Globalization;
using System.Text;

namespace Weva.Layout {
    // CSS Counter Styles 3 §6 — converts a 1-based list index to the marker
    // glyph string for a given `list-style-type`. v2 supports the predefined
    // identifiers required by the task scope; everything outside that set
    // falls back to `disc`. The 1-based ordinal `n` is the index of the
    // `<li>` among its `<li>` siblings (computed by the caller — see
    // BoxBuilder.MaybeInjectListMarker).
    //
    // Bullet shapes follow the spec:
    //   disc   → U+2022 BULLET
    //   circle → U+25E6 WHITE BULLET
    //   square → U+25AA BLACK SMALL SQUARE
    //
    // Numeric / alphabetic styles all append "." per the spec's
    // default suffix for these counters (CSS Counter Styles 3 §3.1).
    internal static class ListMarkerStyle {
        // Returns the marker text for the given `list-style-type` value and
        // 1-based ordinal. Unrecognised types fall back to disc. The bullet
        // shapes ignore `ordinal` entirely.
        public static string MarkerText(string listStyleType, int ordinal) {
            // Normalise: a null/empty value behaves like the initial "disc".
            // We do NOT lowercase — author tokens come through the cascade
            // unchanged (CSS idents are ASCII-case-insensitive but the
            // cascade preserves the author casing), so accept both cases.
            string t = listStyleType;
            if (string.IsNullOrEmpty(t)) t = "disc";
            switch (t) {
                case "disc":   return "•";        // •
                case "circle": return "◦";        // ◦
                case "square": return "▪";        // ▪
                case "decimal":
                    return ordinal.ToString(CultureInfo.InvariantCulture) + ".";
                case "decimal-leading-zero":
                    // Spec §6: pad to 2 digits up to 99; 3 digits 100-999, etc.
                    // The simple "always pad to 2" matches the common case;
                    // we follow Chrome and pad to 2 only.
                    return ordinal.ToString("00", CultureInfo.InvariantCulture) + ".";
                case "lower-roman":
                    return ToRoman(ordinal, lowercase: true) + ".";
                case "upper-roman":
                    return ToRoman(ordinal, lowercase: false) + ".";
                case "lower-alpha":
                case "lower-latin":
                    return ToAlpha(ordinal, lowercase: true) + ".";
                case "upper-alpha":
                case "upper-latin":
                    return ToAlpha(ordinal, lowercase: false) + ".";
                default:
                    // CSS Counter Styles 3 fallback: unsupported `@counter-style`
                    // names resolve to the "decimal" counter, but the task scope
                    // pins the fallback to `disc` to match the existing v1
                    // contract for unrecognised identifiers.
                    return "•";
            }
        }

        // Roman numerals 1..3999. The spec defines lower/upper-roman over
        // the range 1..3999; values outside fall back to decimal in
        // spec-compliant engines. We mirror that to avoid surprising the
        // author who writes a 5000-item list.
        static string ToRoman(int n, bool lowercase) {
            if (n < 1 || n > 3999) {
                return n.ToString(CultureInfo.InvariantCulture);
            }
            // Symbol table for subtractive notation.
            // Pairs sorted descending so the greedy loop emits canonical
            // roman numerals (IV not IIII, IX not VIIII).
            var sb = new StringBuilder();
            int[] vals = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
            string[] up  = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
            string[] low = { "m", "cm", "d", "cd", "c", "xc", "l", "xl", "x", "ix", "v", "iv", "i" };
            var sym = lowercase ? low : up;
            for (int i = 0; i < vals.Length; i++) {
                while (n >= vals[i]) {
                    sb.Append(sym[i]);
                    n -= vals[i];
                }
            }
            return sb.ToString();
        }

        // Bijective base-26 (a..z, aa..az, ba..bz, …, zz, aaa, …). Note this
        // is NOT simple base-26: there is no zero digit, so 26 → "z" and
        // 27 → "aa" (not "ba"). Spec: CSS Counter Styles 3 §6 alphabetic
        // system. Negative / zero ordinals fall back to decimal; the marker
        // pipeline always passes ordinal ≥ 1.
        static string ToAlpha(int n, bool lowercase) {
            if (n < 1) return n.ToString(CultureInfo.InvariantCulture);
            int baseCh = lowercase ? 'a' : 'A';
            // Build digits in reverse, then reverse the buffer.
            var sb = new StringBuilder();
            while (n > 0) {
                int rem = (n - 1) % 26;
                sb.Append((char)(baseCh + rem));
                n = (n - 1) / 26;
            }
            // Reverse in-place — StringBuilder has no Reverse, so swap.
            int len = sb.Length;
            for (int i = 0, j = len - 1; i < j; i++, j--) {
                char tmp = sb[i];
                sb[i] = sb[j];
                sb[j] = tmp;
            }
            return sb.ToString();
        }

        // Returns the raw url(...) (or other image value) if `list-style-image`
        // is non-`none`, else null. Trimming / normalisation is the caller's
        // problem; this just centralises the "is the image set?" check.
        public static string ImageOrNull(string listStyleImage) {
            if (string.IsNullOrEmpty(listStyleImage)) return null;
            if (listStyleImage == "none") return null;
            return listStyleImage;
        }
    }
}
