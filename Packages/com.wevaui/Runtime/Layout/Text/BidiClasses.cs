namespace Weva.Layout.Text {
    // UAX #9 Simplified Bidi Class table — codepoint → BidiClass.
    //
    // Full Unicode Bidirectional Algorithm: https://unicode.org/reports/tr9/
    //
    // This implementation covers the Unicode ranges that matter for
    // game-localization: Hebrew (U+0590–05FF), Arabic base plane
    // (U+0600–06FF) plus Arabic Presentation Forms-A/B
    // (U+FB50–FDFF / U+FE70–FEFF), European digits (EN), and all
    // remaining characters classified as Neutral or whitespace.
    //
    // Deliberately omitted vs. the full UAX #9:
    //   - Explicit embedding controls (LRE/RLE/LRO/RLO/PDF/LRI/RLI/FSI/PDI).
    //     These are rare in game UI text and treated as Neutral (ON).
    //   - Brackets pairing (BD16/N0): paired brackets take the type of their
    //     content. Deferred; brackets are treated as Neutral.
    //   - AL (Arabic Letter) vs R (Right-to-Left): both map to BidiClass.R in
    //     v1 because the engine lacks Arabic shaping/tatweel — any Arabic
    //     glyph run is treated as a single RTL block.
    //   - NSM (Non-Spacing Mark): attached to the preceding strong character
    //     in full UAX #9 but not worth the complexity here; treated as Neutral.
    //   - ET (European Terminator) / CS (Common Number Separator): the full
    //     algorithm uses these to extend EN runs (e.g. "$" before digits,
    //     "," inside numbers). v1 treats them as Neutral; only standalone
    //     digit sequences get the EN treatment.
    //   - AN (Arabic Number): rare in game UI; treated as EN for simplicity.
    //   - BN (Boundary Neutral): treated as Neutral.
    //   - Supplementary-plane RTL scripts (Phoenician, Kharoshthi, Meroitic
    //     etc.) are OMITTED — only Hebrew and Arabic BMP + PF ranges covered.
    //
    // The contract here is for the simplified rule subset in BidiRuns.cs,
    // not for a complete UBA implementation.
    internal static class BidiClasses {
        /// <summary>
        /// Simplified bidi class used by the v1 UAX #9 inline reorder pass.
        /// </summary>
        public enum BidiClass {
            /// <summary>Left-to-right strong type (Latin, CJK, …).</summary>
            L,
            /// <summary>Right-to-left strong type (Hebrew, Arabic).</summary>
            R,
            /// <summary>European Number (ASCII digits 0–9, Arabic-Indic digits).</summary>
            EN,
            /// <summary>Whitespace separator (U+0009 TAB, U+0020 SPACE, U+000A LINE FEED, etc.).</summary>
            WS,
            /// <summary>Other neutral (punctuation, symbols that are not strong).</summary>
            ON,
        }

        // --- Strong-type range checks (UAX #9 Table 4, abridged) -----------

        /// <summary>
        /// Returns true when the codepoint belongs to a Hebrew script block.
        /// Covers Hebrew (U+0590–05FF).
        /// UAX #9 bidi class: R (right-to-left).
        /// </summary>
        public static bool IsHebrew(int cp) {
            return cp >= 0x0590 && cp <= 0x05FF;
        }

        /// <summary>
        /// Returns true when the codepoint belongs to an Arabic script block.
        /// Covers:
        ///   Arabic base block        U+0600–06FF
        ///   Arabic Supplement        U+0750–077F
        ///   Arabic Extended-A        U+08A0–08FF
        ///   Arabic Presentation A    U+FB50–FDFF
        ///   Arabic Presentation B    U+FE70–FEFF
        /// UAX #9 bidi class: R (mapped from AL for v1 simplification).
        /// </summary>
        public static bool IsArabic(int cp) {
            if (cp >= 0x0600 && cp <= 0x06FF) return true;  // Arabic base block
            if (cp >= 0x0750 && cp <= 0x077F) return true;  // Arabic Supplement
            if (cp >= 0x08A0 && cp <= 0x08FF) return true;  // Arabic Extended-A
            if (cp >= 0xFB50 && cp <= 0xFDFF) return true;  // Presentation Forms-A
            if (cp >= 0xFE70 && cp <= 0xFEFF) return true;  // Presentation Forms-B
            return false;
        }

        /// <summary>
        /// Returns true when the codepoint is classified as a European Number
        /// (EN) for the purpose of the simplified bidi algorithm.
        /// Covers: ASCII digits U+0030–0039, Arabic-Indic U+0660–0669,
        /// Extended Arabic-Indic U+06F0–06F9.
        /// UAX #9 bidi class: EN.
        /// </summary>
        public static bool IsEuropeanDigit(int cp) {
            if (cp >= 0x0030 && cp <= 0x0039) return true;  // ASCII 0-9
            if (cp >= 0x0660 && cp <= 0x0669) return true;  // Arabic-Indic 0-9
            if (cp >= 0x06F0 && cp <= 0x06F9) return true;  // Extended Arabic-Indic 0-9
            return false;
        }

        /// <summary>
        /// Returns true for Unicode whitespace that acts as a separator
        /// (bidi class WS or B). Whitespace resolves to the base direction
        /// when adjacent to opposite-direction content.
        /// </summary>
        public static bool IsWhitespace(int cp) {
            switch (cp) {
                case 0x0009:  // CHARACTER TABULATION
                case 0x000A:  // LINE FEED
                case 0x000B:  // LINE TABULATION
                case 0x000C:  // FORM FEED
                case 0x000D:  // CARRIAGE RETURN
                case 0x0020:  // SPACE
                case 0x0085:  // NEXT LINE
                case 0x00A0:  // NO-BREAK SPACE (WS in Unicode)
                case 0x1680:  // OGHAM SPACE MARK
                case 0x2000: case 0x2001: case 0x2002: case 0x2003:
                case 0x2004: case 0x2005: case 0x2006: case 0x2007:
                case 0x2008: case 0x2009: case 0x200A:
                case 0x2028:  // LINE SEPARATOR
                case 0x2029:  // PARAGRAPH SEPARATOR
                case 0x202F:  // NARROW NO-BREAK SPACE
                case 0x205F:  // MEDIUM MATHEMATICAL SPACE
                case 0x3000:  // IDEOGRAPHIC SPACE
                    return true;
            }
            return false;
        }

        // --- Primary classification entry point ----------------------------

        /// <summary>
        /// Returns the simplified bidi class for a Unicode codepoint.
        /// L  — any strongly LTR character (Latin, CJK, Greek, Cyrillic, …)
        /// R  — Hebrew (U+0590–05FF) or Arabic (U+0600–06FF + PF ranges)
        /// EN — European / Arabic-Indic decimal digits
        /// WS — whitespace (SPACE, TAB, LF, …)
        /// ON — anything else (punctuation, symbols, control codes)
        /// </summary>
        public static BidiClass Classify(int cp) {
            // Fast path: ASCII printable range is overwhelmingly L or digits.
            if (cp >= 0x0021 && cp <= 0x002F) return BidiClass.ON;  // !"#$%&'()*+,-./
            if (cp >= 0x0030 && cp <= 0x0039) return BidiClass.EN;  // 0–9
            if (cp >= 0x003A && cp <= 0x0040) return BidiClass.ON;  // :;<=>?@
            if (cp >= 0x0041 && cp <= 0x007A) return BidiClass.L;   // A–z
            if (cp >= 0x007B && cp <= 0x007E) return BidiClass.ON;  // {|}~
            if (IsWhitespace(cp)) return BidiClass.WS;
            if (IsEuropeanDigit(cp)) return BidiClass.EN;
            if (IsHebrew(cp)) return BidiClass.R;
            if (IsArabic(cp)) return BidiClass.R;
            // Latin supplement / extended Latin: L.
            if (cp >= 0x0080 && cp <= 0x058F) return BidiClass.L;
            // Arabic ranges already handled above; skip.
            // Greek and Coptic, Cyrillic, Armenian, Syriac, Thaana, …
            if (cp >= 0x0700 && cp <= 0x08AF) return BidiClass.L;   // Syriac–Arabic Extended  (some R but EN covers digits)
            // Most of the rest of the BMP that isn't Hebrew/Arabic is L (CJK, etc.)
            // or ON (punctuation blocks). We handle the common non-L punctuation
            // blocks explicitly; the remainder defaults to L.
            if (cp >= 0x2000 && cp <= 0x206F) return BidiClass.ON;  // General Punctuation
            if (cp >= 0x2070 && cp <= 0x209F) return BidiClass.EN;  // Superscript/subscript digits
            if (cp >= 0x20A0 && cp <= 0x20CF) return BidiClass.ON;  // Currency Symbols
            if (cp >= 0x2100 && cp <= 0x214F) return BidiClass.ON;  // Letterlike Symbols
            if (cp >= 0x2150 && cp <= 0x218F) return BidiClass.EN;  // Number Forms
            if (cp >= 0x2190 && cp <= 0x23FF) return BidiClass.ON;  // Arrows, Misc Technical
            if (cp >= 0x2400 && cp <= 0x27BF) return BidiClass.ON;  // Control Pictures … Misc Symbols
            // Remaining codepoints default to L (CJK, Hangul, supplementary scripts, etc.)
            return BidiClass.L;
        }

        // --- String-level helpers (re-use LineBreakClasses helpers) ---------

        /// <summary>
        /// Returns the codepoint at position <paramref name="idx"/> in string
        /// <paramref name="s"/>, consuming a surrogate pair as one codepoint.
        /// Returns -1 when idx is out of range.
        /// </summary>
        public static int CodepointAt(string s, int idx) {
            return LineBreakClasses.CodepointAt(s, idx);
        }

        /// <summary>
        /// Returns the number of UTF-16 chars consumed by the codepoint at
        /// position <paramref name="idx"/> (1 or 2 for a surrogate pair).
        /// </summary>
        public static int CodepointCharCount(string s, int idx) {
            return LineBreakClasses.CodepointCharCount(s, idx);
        }

        /// <summary>
        /// Returns true when <paramref name="text"/> contains at least one
        /// codepoint whose bidi class is R. Used as the fast-path gate in
        /// BidiRuns.Analyze (and the InlineLayout bidi integration).
        /// </summary>
        public static bool ContainsRtl(string text) {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; ) {
                int cp = CodepointAt(text, i);
                if (cp < 0) break;
                if (IsHebrew(cp) || IsArabic(cp)) return true;
                i += CodepointCharCount(text, i);
            }
            return false;
        }
    }
}
