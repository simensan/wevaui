using Weva.Layout.Text;

namespace Weva.Forms.Text {
    // WordBoundary — simplified word-boundary navigation for text editing.
    //
    // Browser word-boundary rules (W4 phase 1, ROADMAP.md):
    //
    //   Full UAX #29 word-break rules are out of scope for v1.  This class
    //   implements the simplified model that browsers use for Ctrl+Arrow / Alt+Arrow
    //   (word-by-word caret movement):
    //
    //   Rule 1 — SPACE / PUNCTUATION run:
    //     A contiguous span of whitespace and/or punctuation characters forms a
    //     single separator unit.  Ctrl+Right skips the entire separator before
    //     entering the next word; Ctrl+Left skips any trailing separator before
    //     the previous word.
    //
    //   Rule 2 — ALPHANUMERIC / UNDERSCORE word:
    //     A contiguous span of letters, digits, and underscores ('_') is a single
    //     word token.
    //
    //   Rule 3 — CJK (each codepoint is its own word unit):
    //     Any codepoint classified as a CJK flow character by
    //     LineBreakClasses.IsCjkFlowChar is treated as a one-codepoint word.
    //     This matches browser behaviour: Ctrl+Right over "日本語テスト" lands on
    //     each character boundary, NOT at the end of the run.
    //
    //   Classification priority: CJK > alphanumeric/underscore > separator.
    //
    //   Newlines are treated as separators (they terminate a word boundary just
    //   like a space), which matches how most editors handle multiline text.
    //
    //   Simplified boundary rule summary:
    //     SEPARATOR = whitespace OR (punctuation AND NOT letter AND NOT digit AND
    //                                NOT '_' AND NOT CJK-flow)
    //     WORD-CHAR  = letter OR digit OR '_'   (and NOT CJK-flow)
    //     CJK-WORD   = IsCjkFlowChar(codepoint) (takes priority)
    //
    // Threading: all methods are pure / re-entrant.
    public static class WordBoundary {
        // Returns the index of the start of the previous word, searching backward
        // from `pos`.  Behaviour mirrors Ctrl+Left in Chrome/Firefox:
        //
        //   1. Skip any separator(s) immediately before the caret.
        //   2. Consume the word token before the separator:
        //      - If the character is CJK, consume exactly one codepoint.
        //      - Otherwise consume a maximal word-char span.
        //
        // Returns 0 if already at the beginning.
        public static int PreviousWordBoundary(string text, int pos) {
            if (pos <= 0 || string.IsNullOrEmpty(text)) return 0;

            int i = pos;

            // Phase 1: skip whitespace / punctuation separators that immediately
            // precede the caret.
            while (i > 0) {
                int cp = PeekCodepointLeft(text, i, out int cpLen);
                if (!IsSeparator(cp)) break;
                i -= cpLen;
            }

            if (i <= 0) return 0;

            // Phase 2: consume one word token.
            int cpStart = PeekCodepointLeft(text, i, out _);
            if (LineBreakClasses.IsCjkFlowChar(cpStart)) {
                // CJK word unit = exactly one codepoint.
                int cpLen2 = cpStart > 0xFFFF ? 2 : 1;
                return i - cpLen2;
            }
            // Alphanumeric / underscore run.
            while (i > 0) {
                int cp = PeekCodepointLeft(text, i, out int cpLen);
                if (IsSeparator(cp) || LineBreakClasses.IsCjkFlowChar(cp)) break;
                i -= cpLen;
            }
            return i;
        }

        // Returns the index of the end of the next word, searching forward from
        // `pos`.  Behaviour mirrors Ctrl+Right in Chrome/Firefox:
        //
        //   1. Skip any separator(s) immediately after the caret.
        //   2. Consume the word token after the separator:
        //      - If the character is CJK, consume exactly one codepoint.
        //      - Otherwise consume a maximal word-char span.
        //
        // Returns text.Length if already at the end.
        public static int NextWordBoundary(string text, int pos) {
            if (string.IsNullOrEmpty(text)) return 0;
            int n = text.Length;
            if (pos >= n) return n;

            int i = pos;

            // Phase 1: skip whitespace / punctuation separators.
            while (i < n) {
                int cp = PeekCodepointRight(text, i, out int cpLen);
                if (!IsSeparator(cp)) break;
                i += cpLen;
            }

            if (i >= n) return n;

            // Phase 2: consume one word token.
            int cpNext = PeekCodepointRight(text, i, out _);
            if (LineBreakClasses.IsCjkFlowChar(cpNext)) {
                // CJK word unit = exactly one codepoint.
                int cpLen2 = cpNext > 0xFFFF ? 2 : 1;
                return i + cpLen2;
            }
            // Alphanumeric / underscore run.
            while (i < n) {
                int cp = PeekCodepointRight(text, i, out int cpLen);
                if (IsSeparator(cp) || LineBreakClasses.IsCjkFlowChar(cp)) break;
                i += cpLen;
            }
            return i;
        }

        // The word UNIT under a caret index — the double-click selection range
        // (input/selection audit #4). Chrome's model: the clicked glyph's run —
        // a word-char run selects the word, a separator run selects the
        // separators, a CJK codepoint selects itself. At a word/separator
        // boundary the word to the LEFT wins (matches clicking the trailing
        // edge of a word in Chrome). Returns (0,0) for empty text.
        public static void WordRangeAt(string text, int index, out int start, out int end) {
            start = 0; end = 0;
            if (string.IsNullOrEmpty(text)) return;
            int n = text.Length;
            if (index < 0) index = 0;
            if (index > n) index = n;
            // Pivot on the glyph at the index; at the very end (or on a
            // separator whose left neighbour is a word char) pivot left.
            int pivot = index < n ? index : n - 1;
            if (pivot > 0 && char.IsLowSurrogate(text[pivot])) pivot--;
            int cpAt = PeekCodepointRight(text, pivot, out _);
            if (IsSeparator(cpAt) && !LineBreakClasses.IsCjkFlowChar(cpAt) && pivot > 0) {
                int cpLeft = PeekCodepointLeft(text, pivot, out int leftLen);
                if (!IsSeparator(cpLeft)) pivot -= leftLen;
            }
            int cp = PeekCodepointRight(text, pivot, out int cpLen);
            if (LineBreakClasses.IsCjkFlowChar(cp)) {
                start = pivot;
                end = pivot + cpLen;
                return;
            }
            bool separatorRun = IsSeparator(cp);
            int s = pivot, e = pivot + cpLen;
            while (s > 0) {
                int left = PeekCodepointLeft(text, s, out int len);
                if (LineBreakClasses.IsCjkFlowChar(left)) break;
                if (IsSeparator(left) != separatorRun) break;
                s -= len;
            }
            while (e < n) {
                int right = PeekCodepointRight(text, e, out int len);
                if (LineBreakClasses.IsCjkFlowChar(right)) break;
                if (IsSeparator(right) != separatorRun) break;
                e += len;
            }
            start = s;
            end = e;
        }

        // Returns true when `codepoint` acts as a word separator.
        // '_' is NOT a separator (it joins identifier-like words).
        // CJK codepoints are not separators but are also not word-chars;
        // the caller handles them before reaching this path.
        public static bool IsSeparator(int codepoint) {
            if (codepoint == '_') return false;
            // Fast path for ASCII.
            if (codepoint < 128) {
                return !(codepoint >= 'a' && codepoint <= 'z')
                    && !(codepoint >= 'A' && codepoint <= 'Z')
                    && !(codepoint >= '0' && codepoint <= '9');
            }
            char c = (char)codepoint;
            if (char.IsLetterOrDigit(c)) return false;
            return true;
        }

        // ---- Private helpers ----

        // Reads the codepoint to the LEFT of `i` (i.e. ending at index i-1),
        // returning the codepoint and writing `cpLen` (1 or 2).
        static int PeekCodepointLeft(string text, int i, out int cpLen) {
            // Check for a low surrogate preceded by a high surrogate.
            if (i >= 2
                && char.IsLowSurrogate(text[i - 1])
                && char.IsHighSurrogate(text[i - 2])) {
                cpLen = 2;
                return char.ConvertToUtf32(text[i - 2], text[i - 1]);
            }
            cpLen = 1;
            return text[i - 1];
        }

        // Reads the codepoint starting at index `i` going right, writing `cpLen`.
        static int PeekCodepointRight(string text, int i, out int cpLen) {
            if (i + 1 < text.Length
                && char.IsHighSurrogate(text[i])
                && char.IsLowSurrogate(text[i + 1])) {
                cpLen = 2;
                return char.ConvertToUtf32(text[i], text[i + 1]);
            }
            cpLen = 1;
            return text[i];
        }
    }
}
