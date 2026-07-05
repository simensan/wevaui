namespace Weva.Layout.Text {
    // UAX #14 Unicode Line Breaking Algorithm — simplified CJK subset.
    // Full UAX #14 class table: https://unicode.org/reports/tr14/#Table1
    // This implementation covers the Unicode ranges that matter for CJK
    // break-between-every-grapheme behaviour (Chrome's default for East
    // Asian text), kinsoku prohibition rules (CSS Text L3 §5.3 / JIS X
    // 4051), and the CSS Text L3 §5.2-§5.3 `word-break`/`line-break`
    // properties.
    //
    // F17b: CSS Text L3 §5.3 `line-break` distinctions now implemented.
    // Three strictness groups drive the kinsoku rules:
    //
    //   line-break: anywhere  → kinsoku prohibitions lifted entirely.
    //   line-break: loose     → only the universal kinsoku set applies;
    //                           "loose-only" groups are allowed to start a line.
    //   line-break: normal (default) / strict → full kinsoku applies.
    //
    // "Loose-only" groups (CSS Text L3 §5.3 Table 1):
    //   Group 1 — Small kana: ぁぃぅぇぉっゃゅょゎゕゖ (U+3041-3096 small forms)
    //             + katakana equivalents ァィゥェォッャュョヮヵヶ (U+30A1-30F6 small)
    //             + Katakana Phonetic Extensions small kana (U+31F0-31FF)
    //             + PROLONGED SOUND MARK ー (U+30FC)
    //             → loose allows breaks BEFORE these; normal/strict forbid.
    //   Group 2 — Hyphen-like: ‐ (U+2010), – (U+2013), 〜 (U+301C), ゠ (U+30A0)
    //             → loose allows breaks BEFORE these; normal/strict forbid.
    //             Chrome reference: Blink BreakingContext treats these as class BA
    //             under non-strict, but as NS-like under strict; we mirror that.
    //   Group 3 — Iteration marks: 々 (U+3005), 〻 (U+303B), ゝ (U+309D),
    //             ゞ (U+309E), ヽ (U+30FD), ヾ (U+30FE)
    //             → loose allows breaks BEFORE these; normal/strict forbid.
    //   Group 4 — Centered punctuation / CJK colons (in CJK context):
    //             ・ (U+30FB), ： (U+FF1A), ； (U+FF1B), ‼ (U+203C),
    //             ⁇ (U+2047), ⁈ (U+2048), ⁉ (U+2049), ！ (U+FF01), ？ (U+FF1F)
    //             → loose allows breaks BEFORE these when content contains CJK;
    //               normal/strict forbid.
    //
    // Ranges covered:
    //   Ideographic (ID) — Han, Katakana, Hiragana, Hangul, full-width forms
    //   Close-punctuation prohibition (no break BEFORE) — kinsoku tail set
    //   Open-punctuation prohibition (no break AFTER)  — kinsoku head set
    //
    // Integration contract (CSS Text L3 §5.2-§5.3):
    //   word-break: normal   → IsCjkBreakOpportunity() governs; kinsoku applies.
    //   word-break: keep-all → CJK breaks suppressed; Latin-only word-breaks.
    //   word-break: break-all → every grapheme is a break opportunity (existing
    //                            path; CJK break logic is bypassed).
    //   line-break: anywhere → kinsoku prohibitions are lifted.
    //   line-break: loose    → only universal kinsoku; loose-only groups allowed.
    //   line-break: normal / strict (and auto / null) → full kinsoku.
    internal static class LineBreakClasses {
        // --- Ideographic range checks (UAX #14 class ID / H2 / H3) ---------
        // Returns true when the codepoint is a CJK ideographic character that
        // participates in the break-between-ideographs logic. Chrome breaks
        // freely between any two characters in this set.
        //
        // Ranges chosen for Chrome-parity (v1 — not exhaustive UAX #14):
        //   U+3040–309F   Hiragana
        //   U+30A0–30FF   Katakana
        //   U+3400–4DBF   CJK Extension A
        //   U+4E00–9FFF   CJK Unified Ideographs (core BMP block)
        //   U+AC00–D7AF   Hangul Syllables
        //   U+F900–FAFF   CJK Compatibility Ideographs
        //   U+FF01–FF5E   Fullwidth letter/digit forms
        //   U+3005, U+3006 CJK iteration/closing marks
        public static bool IsCjkIdeographic(int codepoint) {
            // Hiragana block (U+3040–309F)
            if (codepoint >= 0x3040 && codepoint <= 0x309F) return true;
            // Katakana block (U+30A0–30FF)
            if (codepoint >= 0x30A0 && codepoint <= 0x30FF) return true;
            // CJK Extension A (U+3400–4DBF)
            if (codepoint >= 0x3400 && codepoint <= 0x4DBF) return true;
            // CJK Unified Ideographs core block (U+4E00–9FFF)
            if (codepoint >= 0x4E00 && codepoint <= 0x9FFF) return true;
            // Hangul Syllables (U+AC00–D7AF)
            if (codepoint >= 0xAC00 && codepoint <= 0xD7AF) return true;
            // CJK Compatibility Ideographs (U+F900–FAFF)
            if (codepoint >= 0xF900 && codepoint <= 0xFAFF) return true;
            // Fullwidth Latin/digit forms (U+FF01–FF5E)
            if (codepoint >= 0xFF01 && codepoint <= 0xFF5E) return true;
            // CJK iteration mark (U+3005) and closing mark (U+3006)
            if (codepoint == 0x3005 || codepoint == 0x3006) return true;
            return false;
        }

        // Returns true for supplementary-plane CJK (Extension B+). Call this
        // after decoding a surrogate pair into a full codepoint.
        public static bool IsCjkSupplementary(int codepoint) {
            // Extension B (U+20000–2A6DF) and Extension C–F/G ranges
            return (codepoint >= 0x20000 && codepoint <= 0x2FA1F);
        }

        // Returns true when the codepoint participates in CJK text flow for the
        // purpose of line-breaking decisions. This includes both ideographic
        // characters AND the kinsoku-class punctuation that follows/precedes them
        // (e.g. 。is kinsoku-close and trails ideographic runs; 「 is kinsoku-open
        // and precedes them). UAX #14 assigns these to classes CL/CP/OP/QU/NS
        // which all participate in the East Asian break algorithm.
        //
        // F17b: Extended to include loose-only kinsoku characters (hyphens,
        // iteration marks, group-4 punctuation) so that IsCjkBreakOpportunity
        // can gate on them properly even when those characters are the cpBefore
        // or cpAfter in a seam check.
        public static bool IsCjkFlowChar(int codepoint) {
            return IsCjkIdeographic(codepoint)
                || IsCjkSupplementary(codepoint)
                || IsKinsokuClose(codepoint)
                || IsKinsokuOpen(codepoint)
                || IsLooseOnlyKinsokuClose(codepoint);
        }

        // --- Kinsoku prohibitions (CSS Text L3 §5.3) -----------------------

        // Kinsoku "close" class (universal — applies at line-break: normal and
        // strict): NO break BEFORE this character (it cannot appear at the start
        // of a new line). Includes closing brackets, closing Japanese quotation
        // marks, and common sentence-ending punctuation.
        //
        // F17b: Characters that only apply under normal/strict (not loose) have
        // been removed from this method and are now in IsLooseOnlyKinsokuClose.
        // Both methods are consulted by IsKinsokuCloseForLevel.
        //
        // Reference: JIS X 4051 "行末禁則" set + Chrome's effective kinsoku.
        public static bool IsKinsokuClose(int codepoint) {
            // --- Closing brackets, quotation marks, sentence-ending punctuation ---
            // These never start a line under any `line-break` level except `anywhere`.
            // (JIS X 4051 "強い行末禁則" / CSS Text L3 Table 1 row 1 — non-relaxable set)

            // CJK Symbols and Punctuation block — sentence-ending
            if (codepoint == 0x3001) return true;  // IDEOGRAPHIC COMMA 、
            if (codepoint == 0x3002) return true;  // IDEOGRAPHIC FULL STOP 。
            // Closing brackets in U+300x block
            if (codepoint == 0x3009) return true;  // RIGHT ANGLE BRACKET 〉
            if (codepoint == 0x300B) return true;  // RIGHT DOUBLE ANGLE BRACKET 》
            if (codepoint == 0x300D) return true;  // RIGHT CORNER BRACKET 」
            if (codepoint == 0x300F) return true;  // RIGHT WHITE CORNER BRACKET 』
            if (codepoint == 0x3011) return true;  // RIGHT BLACK LENTICULAR BRACKET 】
            if (codepoint == 0x3015) return true;  // RIGHT TORTOISE SHELL BRACKET 〕
            if (codepoint == 0x3017) return true;  // RIGHT WHITE CORNER BRACKET 〗
            if (codepoint == 0x3019) return true;  // RIGHT WHITE TORTOISE SHELL BRACKET 〙
            if (codepoint == 0x301B) return true;  // RIGHT WHITE SQUARE BRACKET 〛
            // Voiced/semi-voiced iteration marks (combining; not in loose-only group)
            if (codepoint == 0x309B) return true;  // VOICED ITERATION MARK ゛
            if (codepoint == 0x309C) return true;  // SEMI-VOICED ITERATION MARK ゜
            // Fullwidth closing punctuation — non-relaxable
            if (codepoint == 0xFF09) return true;  // Fullwidth ) ）
            if (codepoint == 0xFF0C) return true;  // Fullwidth , ，
            if (codepoint == 0xFF0E) return true;  // Fullwidth . ．
            if (codepoint == 0xFF3D) return true;  // Fullwidth ] ］
            if (codepoint == 0xFF5D) return true;  // Fullwidth } ｝
            if (codepoint == 0xFF60) return true;  // Fullwidth right white parenthesis ｠
            if (codepoint == 0xFF61) return true;  // Halfwidth ideographic full stop ｡
            if (codepoint == 0xFF63) return true;  // Halfwidth right corner bracket ｣
            if (codepoint == 0xFF64) return true;  // Halfwidth ideographic comma ､
            // Unicode smart quotes (appear in CJK contexts — non-relaxable)
            if (codepoint == 0x2019) return true;  // RIGHT SINGLE QUOTATION MARK '
            if (codepoint == 0x201D) return true;  // RIGHT DOUBLE QUOTATION MARK "
            // Fullwidth RIGHT PARENTHESIS — same as U+FF09 above but JIS set lists
            // the ASCII U+0029 in fullwidth context; U+FF09 covers it already.
            return false;
        }

        // Kinsoku "close" characters that are FORBIDDEN from starting a line
        // under line-break: normal and strict, but ALLOWED under line-break: loose.
        //
        // CSS Text L3 §5.3 Table 1, loose-relaxable rows:
        //   Group 1 — Small kana (ぁ–ゖ small forms, ァ–ヶ small forms, U+31F0-31FF)
        //             + PROLONGED SOUND MARK ー (U+30FC)
        //   Group 2 — Hyphen-like: ‐ (U+2010), – (U+2013), 〜 (U+301C), ゠ (U+30A0)
        //   Group 3 — Iteration marks: 々 (U+3005), 〻 (U+303B),
        //             ゝ (U+309D), ゞ (U+309E), ヽ (U+30FD), ヾ (U+30FE)
        //   Group 4 — Centered punctuation in CJK context (consulted only when
        //             ContainsCjk is true for the surrounding text):
        //             ・ (U+30FB), ： (U+FF1A), ； (U+FF1B), ‼ (U+203C),
        //             ⁇ (U+2047), ⁈ (U+2048), ⁉ (U+2049), ！ (U+FF01), ？ (U+FF1F)
        //
        // Chrome reference: Blink BreakIterator treats all four groups as
        // breakable before in loose mode and non-breakable in normal/strict.
        public static bool IsLooseOnlyKinsokuClose(int codepoint) {
            // --- Group 1: Small kana ---
            // Small Hiragana: ぁ(3041) ぃ(3043) ぅ(3045) ぇ(3047) ぉ(3049)
            //                 っ(3063) ゃ(3083) ゅ(3085) ょ(3087) ゎ(308E)
            //                 ゕ(3095) ゖ(3096)
            if (codepoint == 0x3041) return true;  // small a ぁ
            if (codepoint == 0x3043) return true;  // small i ぃ
            if (codepoint == 0x3045) return true;  // small u ぅ
            if (codepoint == 0x3047) return true;  // small e ぇ
            if (codepoint == 0x3049) return true;  // small o ぉ
            if (codepoint == 0x3063) return true;  // small tsu っ
            if (codepoint == 0x3083) return true;  // small ya ゃ
            if (codepoint == 0x3085) return true;  // small yu ゅ
            if (codepoint == 0x3087) return true;  // small yo ょ
            if (codepoint == 0x308E) return true;  // small wa ゎ
            if (codepoint == 0x3095) return true;  // small ka ゕ
            if (codepoint == 0x3096) return true;  // small ke ゖ
            // Small Katakana: ァ(30A1) ィ(30A3) ゥ(30A5) ェ(30A7) ォ(30A9)
            //                 ッ(30C3) ャ(30E3) ュ(30E5) ョ(30E7) ヮ(30EE)
            //                 ヵ(30F5) ヶ(30F6)
            if (codepoint == 0x30A1) return true;  // small A ァ
            if (codepoint == 0x30A3) return true;  // small I ィ
            if (codepoint == 0x30A5) return true;  // small U ゥ
            if (codepoint == 0x30A7) return true;  // small E ェ
            if (codepoint == 0x30A9) return true;  // small O ォ
            if (codepoint == 0x30C3) return true;  // small tsu ッ
            if (codepoint == 0x30E3) return true;  // small ya ャ
            if (codepoint == 0x30E5) return true;  // small yu ュ
            if (codepoint == 0x30E7) return true;  // small yo ョ
            if (codepoint == 0x30EE) return true;  // small wa ヮ
            if (codepoint == 0x30F5) return true;  // small ka ヵ
            if (codepoint == 0x30F6) return true;  // small ke ヶ
            // Katakana Phonetic Extensions small kana (U+31F0–31FF)
            // These are small-variant phonetic katakana used in Ainu.
            if (codepoint >= 0x31F0 && codepoint <= 0x31FF) return true;
            // PROLONGED SOUND MARK ー (U+30FC) — CSS Text L3 §5.3 Group 1
            if (codepoint == 0x30FC) return true;  // PROLONGED SOUND MARK ー

            // --- Group 2: Hyphen-like characters ---
            // CSS Text L3 §5.3: loose allows breaks BEFORE these; normal/strict forbid.
            // Chrome parity: Blink classifies U+2010 and U+2013 as BA (break-after)
            // in normal mode but NS-like in CJK context for strict.
            if (codepoint == 0x2010) return true;  // HYPHEN ‐
            if (codepoint == 0x2013) return true;  // EN DASH –
            if (codepoint == 0x301C) return true;  // WAVE DASH 〜
            if (codepoint == 0x30A0) return true;  // KATAKANA-HIRAGANA DOUBLE HYPHEN ゠

            // --- Group 3: Iteration marks ---
            if (codepoint == 0x3005) return true;  // IDEOGRAPHIC ITERATION MARK 々
            if (codepoint == 0x303B) return true;  // VERTICAL IDEOGRAPHIC ITERATION MARK 〻
            if (codepoint == 0x309D) return true;  // HIRAGANA ITERATION MARK ゝ
            if (codepoint == 0x309E) return true;  // VOICED HIRAGANA ITERATION MARK ゞ
            if (codepoint == 0x30FD) return true;  // KATAKANA ITERATION MARK ヽ
            if (codepoint == 0x30FE) return true;  // VOICED KATAKANA ITERATION MARK ヾ

            // --- Group 4: Centered punctuation / CJK colons ---
            // These are allowed to start a line under loose (in CJK context).
            // IsCjkBreakOpportunity checks ContainsCjk for the surrounding text;
            // at the classification level we mark them here, and the caller
            // decides whether to apply the prohibition.
            if (codepoint == 0x30FB) return true;  // KATAKANA MIDDLE DOT ・
            if (codepoint == 0xFF1A) return true;  // Fullwidth : ：
            if (codepoint == 0xFF1B) return true;  // Fullwidth ; ；
            if (codepoint == 0x203C) return true;  // DOUBLE EXCLAMATION MARK ‼
            if (codepoint == 0x2047) return true;  // DOUBLE QUESTION MARK ⁇
            if (codepoint == 0x2048) return true;  // QUESTION EXCLAMATION MARK ⁈
            if (codepoint == 0x2049) return true;  // EXCLAMATION QUESTION MARK ⁉
            if (codepoint == 0xFF01) return true;  // Fullwidth ! ！
            if (codepoint == 0xFF1F) return true;  // Fullwidth ? ？

            return false;
        }

        // Returns true when a break BEFORE `codepoint` is forbidden at the
        // given `line-break` level. This is the per-level dispatch:
        //   anywhere → never forbidden (all kinsoku lifted).
        //   loose    → only IsKinsokuClose (universal set); loose-only group
        //              characters are allowed to start a new line.
        //   normal / strict / auto / null → IsKinsokuClose OR IsLooseOnlyKinsokuClose.
        //
        // F17b: CSS Text L3 §5.3 — distinction between loose, normal, and strict.
        // strict note: CSS Text L3 says strict MAY apply a stricter set than
        // normal for some punctuation classes. For v1 we treat strict == normal.
        // If a future audit identifies specific characters where strict diverges,
        // they can be added to a new IsStrictOnlyKinsokuClose helper.
        public static bool IsKinsokuCloseForLevel(int codepoint, string lineBreak) {
            if (lineBreak == "anywhere") return false;
            if (IsKinsokuClose(codepoint)) return true;
            if (lineBreak == "loose") return false;
            // normal / strict (and auto / null → treated as normal)
            return IsLooseOnlyKinsokuClose(codepoint);
        }

        // Kinsoku "open" class: NO break AFTER this character (it cannot appear
        // at the end of a line). Includes opening brackets and Japanese
        // opening quotation marks.
        //
        // F17b: The open-class set is not affected by loose/normal/strict per CSS
        // Text L3 §5.3; all open-class prohibitions apply at every level except
        // `anywhere`. The open-class set is the same regardless of line-break value.
        //
        // Reference: JIS X 4051 "行頭禁則" set + Chrome's effective kinsoku.
        public static bool IsKinsokuOpen(int codepoint) {
            if (codepoint == 0x3008) return true;  // LEFT ANGLE BRACKET 〈
            if (codepoint == 0x300A) return true;  // LEFT DOUBLE ANGLE BRACKET 《
            if (codepoint == 0x300C) return true;  // LEFT CORNER BRACKET 「
            if (codepoint == 0x300E) return true;  // LEFT WHITE CORNER BRACKET 『
            if (codepoint == 0x3010) return true;  // LEFT BLACK LENTICULAR BRACKET 【
            if (codepoint == 0x3014) return true;  // LEFT TORTOISE SHELL BRACKET 〔
            if (codepoint == 0x3016) return true;  // LEFT WHITE CORNER BRACKET 〖
            if (codepoint == 0x3018) return true;  // LEFT WHITE TORTOISE SHELL BRACKET 〘
            if (codepoint == 0x301A) return true;  // LEFT WHITE SQUARE BRACKET 〚
            if (codepoint == 0xFF08) return true;  // Fullwidth left parenthesis （
            if (codepoint == 0xFF3B) return true;  // Fullwidth left bracket ［
            if (codepoint == 0xFF5B) return true;  // Fullwidth left brace ｛
            if (codepoint == 0xFF62) return true;  // Halfwidth left corner bracket ｢
            if (codepoint == 0x2018) return true;  // LEFT SINGLE QUOTATION MARK '
            if (codepoint == 0x201C) return true;  // LEFT DOUBLE QUOTATION MARK "
            return false;
        }

        // --- High-level break-opportunity query used by the line breaker -----
        // CSS Text L3 §5.2: whether a break opportunity exists BETWEEN two
        // adjacent codepoints `cpBefore` and `cpAfter`, taking `wordBreak` and
        // `lineBreak` values into account.
        //
        // Returns true if a break may occur at the seam just BEFORE `cpAfter`.
        //
        // Algorithm (simplified UAX #14 + kinsoku filter):
        //   1. Both chars must participate in CJK text flow (IsCjkFlowChar):
        //      ideographic characters OR kinsoku punctuation.
        //   2. line-break: anywhere → skip all kinsoku (steps 3-4).
        //   3. kinsoku: if `cpAfter` is forbidden from starting a line at this
        //      level → no break. Uses IsKinsokuCloseForLevel.
        //   4. kinsoku: if `cpBefore` is an open-class char → no break.
        //      (Open-class prohibition is not relaxed by loose.)
        //
        // F17b: loose vs normal/strict is now correctly dispatched via
        // IsKinsokuCloseForLevel, so each CSS `line-break` value produces the
        // correct set of forbidden break positions.
        public static bool IsCjkBreakOpportunity(int cpBefore, int cpAfter, string lineBreak) {
            bool beforeIsCjk = IsCjkFlowChar(cpBefore);
            bool afterIsCjk  = IsCjkFlowChar(cpAfter);

            // Both chars must be in the CJK flow. If neither is CJK, the normal
            // Latin word-break tokenizer handles the decision instead.
            if (!beforeIsCjk || !afterIsCjk) return false;

            // line-break: anywhere lifts all kinsoku.
            if (lineBreak == "anywhere") return true;

            // F17b: dispatch kinsoku-close check through the level-aware helper.
            if (IsKinsokuCloseForLevel(cpAfter, lineBreak)) return false;

            // No break AFTER an open-class char — this prohibition is invariant
            // across loose/normal/strict (only `anywhere` lifts it).
            if (IsKinsokuOpen(cpBefore)) return false;

            return true;
        }

        // --- String-level helpers -------------------------------------------

        // Extract the codepoint at `idx` in string `s`, handling surrogate pairs.
        // Returns -1 if `idx` is out of range.
        public static int CodepointAt(string s, int idx) {
            if (idx >= s.Length) return -1;
            char c = s[idx];
            if (char.IsHighSurrogate(c) && idx + 1 < s.Length && char.IsLowSurrogate(s[idx + 1])) {
                return char.ConvertToUtf32(c, s[idx + 1]);
            }
            return c;
        }

        // Number of UTF-16 chars consumed by the codepoint at `idx`.
        public static int CodepointCharCount(string s, int idx) {
            if (idx >= s.Length) return 0;
            char c = s[idx];
            if (char.IsHighSurrogate(c) && idx + 1 < s.Length && char.IsLowSurrogate(s[idx + 1])) {
                return 2;
            }
            return 1;
        }

        // Returns true when `text` contains at least one CJK codepoint (ideographic
        // or kinsoku punctuation). Used to gate the CJK break path in the line breaker.
        public static bool ContainsCjk(string text) {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; ) {
                int cp = CodepointAt(text, i);
                if (cp < 0) break;
                if (IsCjkIdeographic(cp) || IsCjkSupplementary(cp)) return true;
                i += CodepointCharCount(text, i);
            }
            return false;
        }
    }
}
