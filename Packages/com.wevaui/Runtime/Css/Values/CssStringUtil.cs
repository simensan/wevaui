namespace Weva.Css.Values {
    // Non-allocating string predicates used by hot-path resolvers (paint
    // converter, cascade, layout). Each method walks a slice of the input
    // directly instead of constructing trimmed/lowercased substrings, which
    // is what `s.Trim().ToLowerInvariant() == "none"` would do on every
    // call. With paint running per-frame and resolvers consulting these
    // predicates many times per element, the allocation savings compound.
    //
    // Conventions:
    //   * "Trimmed" variants ignore leading/trailing ASCII whitespace.
    //   * Case-insensitive comparison is ASCII-only — CSS keywords are
    //     ASCII, no Turkish-i or non-ASCII case folding to worry about.
    //   * Methods that take a literal `expected` always assume `expected`
    //     is ALREADY lowercase. They case-fold `s` against it; passing a
    //     mixed-case literal produces garbage.
    public static class CssStringUtil {
        public static bool EqualsIgnoreCase(string s, string expectedLower) {
            if (s == null || expectedLower == null) return s == expectedLower;
            if (s.Length != expectedLower.Length) return false;
            for (int i = 0; i < s.Length; i++) {
                if (ToLowerAscii(s[i]) != expectedLower[i]) return false;
            }
            return true;
        }

        // Span overload for callers that have a slice of a larger string and
        // would otherwise have to allocate a Substring to use the string
        // overload above. Equivalent semantics on the slice's contents.
        public static bool EqualsIgnoreCase(System.ReadOnlySpan<char> s, string expectedLower) {
            if (expectedLower == null) return false;
            if (s.Length != expectedLower.Length) return false;
            for (int i = 0; i < s.Length; i++) {
                if (ToLowerAscii(s[i]) != expectedLower[i]) return false;
            }
            return true;
        }

        public static bool EqualsIgnoreCaseTrimmed(string s, string expectedLower) {
            if (s == null || expectedLower == null) return false;
            int start = 0;
            int end = s.Length;
            while (start < end && IsAsciiWhitespace(s[start])) start++;
            while (end > start && IsAsciiWhitespace(s[end - 1])) end--;
            int len = end - start;
            if (len != expectedLower.Length) return false;
            for (int i = 0; i < len; i++) {
                if (ToLowerAscii(s[start + i]) != expectedLower[i]) return false;
            }
            return true;
        }

        public static bool StartsWithIgnoreCase(string s, string prefixLower) {
            if (s == null || prefixLower == null) return false;
            if (s.Length < prefixLower.Length) return false;
            for (int i = 0; i < prefixLower.Length; i++) {
                if (ToLowerAscii(s[i]) != prefixLower[i]) return false;
            }
            return true;
        }

        public static bool StartsWithIgnoreCaseTrimmed(string s, string prefixLower) {
            if (s == null || prefixLower == null) return false;
            int start = 0;
            int end = s.Length;
            while (start < end && IsAsciiWhitespace(s[start])) start++;
            while (end > start && IsAsciiWhitespace(s[end - 1])) end--;
            int len = end - start;
            if (len < prefixLower.Length) return false;
            for (int i = 0; i < prefixLower.Length; i++) {
                if (ToLowerAscii(s[start + i]) != prefixLower[i]) return false;
            }
            return true;
        }

        // Tests whether `s` (after ASCII-trim) starts with any one of the
        // common CSS color-function prefixes: `#`, `rgb(`, `rgba(`, `hsl(`,
        // `hsla(`, `oklab(`, `oklch(`, `lab(`, `lch(`, `color(`. Used by
        // shadow/background resolvers' color-token detection.
        public static bool IsCssColorFunctionPrefix(string s) {
            if (s == null) return false;
            int start = 0;
            while (start < s.Length && IsAsciiWhitespace(s[start])) start++;
            if (start >= s.Length) return false;
            if (s[start] == '#') return true;
            // Scan up to the next '(' or end; case-fold inline.
            int paren = -1;
            for (int i = start; i < s.Length; i++) {
                if (s[i] == '(') { paren = i; break; }
                if (IsAsciiWhitespace(s[i])) break;
            }
            if (paren < 0) return false;
            int len = paren - start;
            return MatchesAnyOf(s, start, len, "rgb")
                || MatchesAnyOf(s, start, len, "rgba")
                || MatchesAnyOf(s, start, len, "hsl")
                || MatchesAnyOf(s, start, len, "hsla")
                || MatchesAnyOf(s, start, len, "oklab")
                || MatchesAnyOf(s, start, len, "oklch")
                || MatchesAnyOf(s, start, len, "lab")
                || MatchesAnyOf(s, start, len, "lch")
                || MatchesAnyOf(s, start, len, "color");
        }

        static bool MatchesAnyOf(string s, int start, int len, string expectedLower) {
            if (len != expectedLower.Length) return false;
            for (int i = 0; i < len; i++) {
                if (ToLowerAscii(s[start + i]) != expectedLower[i]) return false;
            }
            return true;
        }

        // D8: shared `currentcolor` token check used across paint resolvers,
        // shorthand expanders, and the raw-value parser. Centralises the
        // case-insensitive equality so the previous duplicated pattern
        // (`Equals(s, "currentcolor", OrdinalIgnoreCase)` repeated at half a
        // dozen sites) can't drift. Exact-match semantics: returns false for
        // null/empty and for substring matches like "currentcolor-x".
        // For substring detection (e.g. scanning a longhand background value
        // for a `currentcolor` token nested inside a gradient), see
        // BackgroundResolver.ContainsCurrentColor — distinct semantic.
        public static bool IsCurrentColor(string s) {
            return EqualsIgnoreCase(s, "currentcolor");
        }

        public static char ToLowerAscii(char c) {
            return (c >= 'A' && c <= 'Z') ? (char)(c + 32) : c;
        }

        public static bool IsAsciiWhitespace(char c) {
            return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
        }

        // Allocation-free fast path for `s.ToLowerInvariant()`: returns the
        // SAME string instance when `s` contains no ASCII uppercase chars,
        // and only allocates a freshly-lowered string when it actually
        // needs to. CSS computed values are overwhelmingly already-lower
        // (authors and the parser both produce lowercase keywords), so
        // this elides the allocation on the common path. Non-ASCII chars
        // and digits/symbols pass through unchanged either way.
        public static string ToLowerInvariantOrSame(string s) {
            if (s == null) return null;
            for (int i = 0; i < s.Length; i++) {
                char c = s[i];
                if (c >= 'A' && c <= 'Z') {
                    // Hit — fall back to allocating the full lowercased copy.
                    return s.ToLowerInvariant();
                }
            }
            return s;
        }
    }
}
