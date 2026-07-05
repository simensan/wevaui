using System.Text;

namespace Weva.InPlace {
    /// <summary>
    /// Surgical edits to an element's inline <c>style="…"</c> string: set, remove, or read a
    /// single CSS property while leaving every other declaration's source text byte-for-byte
    /// intact (whitespace, property-name casing, ordering, comments, trailing semicolons).
    ///
    /// This is the default edit-targeting path for the in-place editor (see
    /// <c>/WEVA_INPLACE_EDITOR_SCOPE.md</c> §4): a visual tweak becomes a per-element inline
    /// override rather than a whole-file CSS reserialize. The whole point is that we NEVER
    /// rebuild the style from a parsed model — we splice the one value the user changed and copy
    /// the rest of the original string verbatim.
    ///
    /// Mirrors the inline-declaration grammar the cascade uses
    /// (<see cref="Weva.Css.CssParser.ParseInlineDeclarations"/>): a flat <c>prop: value;</c>
    /// sequence, scanned char-by-char respecting paren/bracket/brace, string, and <c>/* */</c>
    /// comment nesting. Tokenizer-free; allocates only the result string (and a builder for
    /// remove). Property matching is ASCII case-insensitive; last declaration wins (CSS order).
    ///
    /// Known v1 limitations: a comment placed *between* a property name and its colon
    /// (<c>color /* x */: red</c>) defeats name matching for that declaration, and setting a
    /// property that appears more than once updates only the last (winning) occurrence, leaving
    /// earlier overridden duplicates in place. Both are vanishingly rare in real inline styles.
    /// </summary>
    public static class InlineStyleEdit {

        /// <summary>Read the (last-winning) value of <paramref name="property"/> from an inline
        /// style string. Returns false if absent. <paramref name="value"/> excludes any
        /// <c>!important</c>, which is reported via <paramref name="important"/>.</summary>
        public static bool TryGetProperty(string style, string property, out string value, out bool important) {
            value = null;
            important = false;
            if (string.IsNullOrEmpty(style) || string.IsNullOrEmpty(property)) return false;
            string target = property.Trim().ToLowerInvariant();
            int n = style.Length;
            int i = 0;
            bool found = false;
            while (i < n) {
                Seg seg = NextSeg(style, ref i, n);
                if (seg.Valid && RegionEqualsLower(style, seg.PropStart, seg.PropEnd, target)) {
                    value = style.Substring(seg.ValueStart, seg.CleanValueEnd - seg.ValueStart);
                    important = seg.Important;
                    found = true; // keep scanning — last occurrence wins
                }
            }
            return found;
        }

        /// <summary>True if <paramref name="property"/> is present in the inline style string.</summary>
        public static bool HasProperty(string style, string property)
            => TryGetProperty(style, property, out _, out _);

        /// <summary>
        /// Return a new inline-style string with <paramref name="property"/> set to
        /// <paramref name="value"/>. If the property already exists its value is replaced in place
        /// (preserving the surrounding source); otherwise a new declaration is appended. A null or
        /// blank <paramref name="value"/> removes the property (CSSOM-style). Every untouched
        /// declaration's bytes are preserved exactly.
        /// </summary>
        public static string SetProperty(string style, string property, string value, bool important = false) {
            if (string.IsNullOrEmpty(property)) return style ?? string.Empty;
            if (value == null || value.Trim().Length == 0) return RemoveProperty(style, property);
            style ??= string.Empty;
            string target = property.Trim().ToLowerInvariant();
            int n = style.Length;
            int i = 0;
            int lastValStart = -1, lastBodyEnd = -1;
            while (i < n) {
                Seg seg = NextSeg(style, ref i, n);
                if (seg.Valid && RegionEqualsLower(style, seg.PropStart, seg.PropEnd, target)) {
                    lastValStart = seg.ValueStart;
                    lastBodyEnd = seg.BodyEnd;
                }
            }
            string body = important ? value.Trim() + " !important" : value.Trim();
            if (lastValStart >= 0) {
                // Splice just the value run; keep the prefix (incl. property name + ':' + leading
                // ws) and suffix (trailing ws + ';' + later declarations) byte-for-byte.
                return style.Substring(0, lastValStart) + body + style.Substring(lastBodyEnd);
            }
            // Append a fresh declaration.
            string core = style.TrimEnd();
            string decl = property.Trim() + ": " + body;
            if (core.Length == 0) return decl;
            if (core[core.Length - 1] == ';') return core + " " + decl;
            return core + "; " + decl;
        }

        /// <summary>Return a new inline-style string with all declarations of
        /// <paramref name="property"/> removed, preserving the rest verbatim.</summary>
        public static string RemoveProperty(string style, string property) {
            if (string.IsNullOrEmpty(style) || string.IsNullOrEmpty(property)) return style ?? string.Empty;
            string target = property.Trim().ToLowerInvariant();
            int n = style.Length;
            int i = 0;
            var sb = new StringBuilder(n);
            bool removedAny = false;
            while (i < n) {
                int segStart = i;
                Seg seg = NextSeg(style, ref i, n); // advances i past this segment (incl. its ';')
                if (seg.Valid && RegionEqualsLower(style, seg.PropStart, seg.PropEnd, target)) {
                    removedAny = true;
                    continue; // drop the whole segment (its leading ws + decl + ';')
                }
                sb.Append(style, segStart, i - segStart);
            }
            if (!removedAny) return style;
            // A removed first/last declaration can expose its separator as leading/trailing
            // whitespace; that's insignificant, so trim the ends. Separators *between* surviving
            // declarations live inside kept segments and are preserved verbatim.
            return sb.ToString().Trim();
        }

        // --- internals ---

        struct Seg {
            public bool Valid;
            public int PropStart, PropEnd;     // property-name run (trimmed)
            public int ValueStart, BodyEnd;    // value run incl. !important, trailing ws trimmed
            public int CleanValueEnd;          // value run excl. !important
            public bool Important;
        }

        // Parse the segment starting at i (a `prop: value` run up to and including the next
        // top-level ';'), advance i past it, and return its spans. Malformed segments come back
        // with Valid=false but i is still advanced so callers can preserve them verbatim.
        static Seg NextSeg(string s, ref int i, int n) {
            int sep = ScanToSep(s, i, n);   // index of the top-level ';' or n
            int contentEnd = sep;
            Seg seg = default;
            int j = SkipWsAndComments(s, i, contentEnd);
            int propStart = j;
            int k = j;
            while (k < contentEnd && s[k] != ':') k++;
            if (k < contentEnd && s[k] == ':') {
                int propEnd = TrimTrailingWs(s, propStart, k);
                if (propEnd > propStart) {
                    int valueStart = SkipWsAndComments(s, k + 1, contentEnd);
                    int bodyEnd = TrimTrailingWs(s, valueStart, contentEnd);
                    if (bodyEnd > valueStart) {
                        bool important = false;
                        int cleanEnd = bodyEnd;
                        if (bodyEnd - valueStart >= 10 && s[bodyEnd - 10] == '!'
                            && EqualsIgnoreCaseAscii(s, bodyEnd - 9, "important")) {
                            important = true;
                            cleanEnd = TrimTrailingWs(s, valueStart, bodyEnd - 10);
                        }
                        if (cleanEnd > valueStart) {
                            seg.Valid = true;
                            seg.PropStart = propStart;
                            seg.PropEnd = propEnd;
                            seg.ValueStart = valueStart;
                            seg.BodyEnd = bodyEnd;
                            seg.CleanValueEnd = cleanEnd;
                            seg.Important = important;
                        }
                    }
                }
            }
            i = sep < n ? sep + 1 : n;
            return seg;
        }

        // Scan forward to the next top-level ';' (or end), respecting strings, paren/bracket/brace
        // nesting, and /* */ comments.
        static int ScanToSep(string s, int i, int n) {
            int depth = 0;
            char quote = '\0';
            while (i < n) {
                char c = s[i];
                if (quote != '\0') {
                    if (c == '\\' && i + 1 < n) { i += 2; continue; }
                    if (c == quote) quote = '\0';
                    i++;
                    continue;
                }
                if (c == '"' || c == '\'') { quote = c; i++; continue; }
                if (c == '/' && i + 1 < n && s[i + 1] == '*') { i = SkipComment(s, i, n); continue; }
                if (c == '(' || c == '[' || c == '{') { depth++; i++; continue; }
                if (c == ')' || c == ']' || c == '}') { if (depth > 0) depth--; i++; continue; }
                if (c == ';' && depth == 0) return i;
                i++;
            }
            return n;
        }

        static int SkipWsAndComments(string s, int i, int end) {
            while (i < end) {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f') { i++; continue; }
                if (c == '/' && i + 1 < end && s[i + 1] == '*') { i = SkipComment(s, i, end); continue; }
                break;
            }
            return i;
        }

        // s[i]=='/' && s[i+1]=='*' on entry. Returns the index just past the closing */ (or end).
        static int SkipComment(string s, int i, int end) {
            i += 2;
            while (i + 1 < end) {
                if (s[i] == '*' && s[i + 1] == '/') return i + 2;
                i++;
            }
            return end;
        }

        static int TrimTrailingWs(string s, int start, int end) {
            while (end > start) {
                char c = s[end - 1];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f') end--;
                else break;
            }
            return end;
        }

        static bool RegionEqualsLower(string s, int start, int end, string lowerTarget) {
            int len = end - start;
            if (len != lowerTarget.Length) return false;
            for (int k = 0; k < len; k++) {
                char c = s[start + k];
                if (c >= 'A' && c <= 'Z') c = (char)(c + 32);
                if (c != lowerTarget[k]) return false;
            }
            return true;
        }

        static bool EqualsIgnoreCaseAscii(string s, int idx, string lowerWord) {
            if (idx + lowerWord.Length > s.Length) return false;
            for (int k = 0; k < lowerWord.Length; k++) {
                char c = s[idx + k];
                if (c >= 'A' && c <= 'Z') c = (char)(c + 32);
                if (c != lowerWord[k]) return false;
            }
            return true;
        }
    }
}
