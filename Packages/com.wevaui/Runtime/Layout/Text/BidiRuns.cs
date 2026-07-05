using System.Collections.Generic;

namespace Weva.Layout.Text {
    // UAX #9 Simplified Bidirectional Run Splitter
    //
    // Full Unicode Bidirectional Algorithm: https://unicode.org/reports/tr9/
    //
    // This module implements a deliberately limited subset of UAX #9 that
    // covers the game-localization use-case: Hebrew/Arabic text embedded in
    // LTR UI copy, or a full RTL block (direction:rtl). It is NOT a
    // conformant UBA implementation; see the "Deliberate simplifications"
    // section below for everything that is omitted.
    //
    // -- Algorithm overview (UAX #9 sub-rules implemented) --
    //
    // P2/P3  — Base paragraph direction: either forced by CSS `direction`
    //          property, or derived by scanning for the first strong character
    //          (L → base = LTR; R → base = RTL; no strong → base = LTR).
    //
    // X1–X9  — Explicit embedding levels.
    //          OMITTED (see simplifications below). Characters that would be
    //          LRE/RLE/LRO/RLO/PDF/LRI/RLI/FSI/PDI in the full algorithm are
    //          treated as Neutral (ON). In practice no game-UI string should
    //          contain raw embedding control codes.
    //
    // W1     — NSM inherits the type of the preceding character.
    //          OMITTED (NSM treated as ON — see simplifications).
    //
    // W2     — EN adjacent to AL (Arabic Letter) becomes AN.
    //          OMITTED (AL mapped to R in v1 — see simplifications). Digits
    //          inside Arabic text are still classified EN and receive the
    //          surrounding-context rule (W7, below).
    //
    // W7     — EN becomes L when the preceding strong character is L.
    //          IMPLEMENTED: a digit sequence takes the type of the nearest
    //          preceding strong character (L or R). If no strong precedes,
    //          the base direction is used. This is what keeps "מחיר 50 שח"
    //          readable: the 50 is flanked by R on the left (→ R context) so
    //          it remains R-level and stays inside the Hebrew run.
    //
    // N1/N2  — Neutral resolution: neutral sequences between two same-type
    //          strong characters take that type; between different types (or
    //          at the edge of the paragraph) they take the paragraph base
    //          direction.
    //          IMPLEMENTED: simplified to one-pass look-ahead using the
    //          accumulated runs (see NeutralResolution).
    //
    // L2     — Reorder runs to visual order: at level L, swap every pair of
    //          runs at levels ≥ L, from the highest level down to L+1.
    //          IMPLEMENTED but applied PER LINE (see note on integration).
    //
    // -- Deliberate simplifications vs full UAX #9 --
    //
    //  1. NO explicit embedding controls (LRE/RLE/LRO/RLO/PDF/LRI/RLI/FSI/PDI
    //     — Unicode CC characters U+202A..U+202E / U+2066..U+2069). Game UI
    //     strings are typically HTML text nodes, not bidi-control-enriched RTF.
    //     Treating them as ON is safe for this scope.
    //
    //  2. NO brackets pairing (BD16/N0 rule). Brackets are treated as Neutral
    //     and resolved by N1/N2. A future pass could add bracket pairing if
    //     mathematical or tabular RTL content becomes a priority.
    //
    //  3. AL merged into R. Arabic Letter (AL) is distinct from RTL (R) in
    //     full UAX #9 because it affects adjacent EN/AN digits. The engine
    //     lacks Arabic shaping (kashida/tatweel), so all Arabic codepoints are
    //     treated as R (same embedding level, no AN conversion).
    //
    //  4. NSM (Non-Spacing Mark) treated as ON. NSM inherits the type of the
    //     preceding character under W1. In practice game UI text nodes rarely
    //     carry isolated combining marks; the cost of implementing W1 for the
    //     cases that matter is higher than the marginal gain.
    //
    //  5. ET / CS / AN treated as ON (not EN). The full algorithm has rules
    //     for European Terminators (currency symbols, percent sign, etc.) and
    //     Common Number Separators (comma, period inside numbers). Omitting
    //     these means "$50" is not recognised as an EN run; the $ is ON. This
    //     is acceptable for game-price strings where the digit sequence alone
    //     already communicates the value and the currency symbol will just
    //     attach to the surrounding run direction.
    //
    //  6. Only two embedding levels are ever produced: 0 (LTR) and 1 (RTL).
    //     The full algorithm supports up to 125 levels; nested bidi overrides
    //     require deeper levels. Game strings don't use nested overrides, so
    //     two levels is sufficient.
    //
    //  7. L2 reordering is applied PER LINE, not per paragraph. UAX #9 L2
    //     applies to the entire paragraph, but CSS inline layout requires
    //     each line box to be independently reordered after line breaking.
    //     The per-line approach matches what browsers implement and is
    //     required for correct wrap behaviour (a line ending mid-RTL-run
    //     is not the same as the paragraph ending there).
    //
    // -- Integration note --
    //
    // BidiRuns.Analyze returns a logical list of (start, length, level) runs.
    // The InlineLayout.cs integration point calls Reorder(runs) to obtain the
    // visual order for a single line after LineBreaker finishes it. Each run
    // carries the TEXT positions from the original string; only the X
    // coordinates of the emitted TextRuns change, not the Text content.
    // Selection/editing code always operates on logical order (the original
    // string indices) and is unaffected by this reordering.
    internal static class BidiRuns {
        /// <summary>
        /// A logical bidi run: [Start, Start+Length) of the paragraph string,
        /// at bidi embedding level <c>Level</c> (0 = LTR, 1 = RTL).
        /// </summary>
        public struct Run {
            /// <summary>Start index (char index) in the original string.</summary>
            public int Start;
            /// <summary>Number of chars in this run.</summary>
            public int Length;
            /// <summary>Bidi embedding level (0 = LTR, 1 = RTL).</summary>
            public int Level;
        }

        /// <summary>
        /// Analyzes the paragraph string <paramref name="text"/> under the
        /// given base direction and returns the ordered LOGICAL list of bidi
        /// runs (start, length, level). Use <see cref="Reorder"/> to convert
        /// a subset of these runs into VISUAL order for a single line.
        /// </summary>
        /// <param name="text">Paragraph text (UTF-16 string).</param>
        /// <param name="baseIsRtl">
        ///   True when CSS <c>direction: rtl</c> is in effect for this
        ///   paragraph. False for <c>direction: ltr</c> (default).
        /// </param>
        /// <param name="result">
        ///   Output list (cleared on entry). Receives runs in logical order.
        /// </param>
        public static void Analyze(string text, bool baseIsRtl, List<Run> result) {
            result.Clear();
            if (string.IsNullOrEmpty(text)) return;

            // --- Step 1: build a per-char bidi-class array --------------------
            // We work in char units (UTF-16) throughout; surrogate pairs are
            // treated as a single unit (both chars get the same class from the
            // combined codepoint). This avoids a separate grapheme-cluster pass.
            int n = text.Length;
            // Reuse a scratch array allocated on the heap (small paragraphs are
            // the norm in game UI; allocate once per Analyze call).
            var cls = new BidiClasses.BidiClass[n];
            for (int i = 0; i < n; ) {
                int cp = BidiClasses.CodepointAt(text, i);
                int cc = BidiClasses.CodepointCharCount(text, i);
                var bc = BidiClasses.Classify(cp);
                for (int k = 0; k < cc && i + k < n; k++) {
                    cls[i + k] = bc;
                }
                i += cc;
            }

            // --- Step 2: W7 — EN takes nearest preceding strong type ----------
            // UAX #9 rule W7 (simplified): scan left to right; keep track of
            // the last strong character seen (L or R). When we encounter an EN,
            // if the last strong is L → promote EN to L (it'll end up in an LTR
            // run). If the last strong is R (or no strong yet and base is RTL) →
            // leave EN as EN (treated as R-level in the run builder below).
            //
            // This is what keeps digits inside Hebrew text (last strong = R)
            // RIGHT-TO-LEFT aligned with the surrounding RTL run, while digits
            // that follow Latin text (last strong = L) align LEFT-TO-RIGHT.
            var lastStrong = baseIsRtl ? BidiClasses.BidiClass.R : BidiClasses.BidiClass.L;
            for (int i = 0; i < n; i++) {
                var c = cls[i];
                if (c == BidiClasses.BidiClass.L) { lastStrong = BidiClasses.BidiClass.L; }
                else if (c == BidiClasses.BidiClass.R) { lastStrong = BidiClasses.BidiClass.R; }
                else if (c == BidiClasses.BidiClass.EN) {
                    // W7: if last strong is L, promote EN to L.
                    if (lastStrong == BidiClasses.BidiClass.L) cls[i] = BidiClasses.BidiClass.L;
                    // else: leave EN as EN → will get level 1 (RTL) below.
                }
            }

            // --- Step 3: N1/N2 — Neutral (ON/WS/EN-as-R) resolution ----------
            // UAX #9 N1: a sequence of neutrals between two strong chars of the
            // same type takes that type. N2: remaining neutrals take the base
            // direction.
            //
            // We implement a simplified two-pass version:
            //   a) Build a "strong context" array: for each position, what is
            //      the nearest strong type to its left and right?
            //   b) A neutral position takes: left==right (same strong) → that
            //      type; otherwise → base direction.
            //
            // EN chars that survived W7 (i.e. are in RTL context) are treated
            // as strong R for the purposes of this resolution.
            var strongLeft  = new BidiClasses.BidiClass[n];
            var strongRight = new BidiClasses.BidiClass[n];

            // Forward pass: accumulate nearest strong to the left.
            var runStrong = baseIsRtl ? BidiClasses.BidiClass.R : BidiClasses.BidiClass.L;
            for (int i = 0; i < n; i++) {
                strongLeft[i] = runStrong;
                var c = cls[i];
                if (c == BidiClasses.BidiClass.L) runStrong = BidiClasses.BidiClass.L;
                else if (c == BidiClasses.BidiClass.R || c == BidiClasses.BidiClass.EN)
                    runStrong = BidiClasses.BidiClass.R;
            }
            // Backward pass: accumulate nearest strong to the right.
            runStrong = baseIsRtl ? BidiClasses.BidiClass.R : BidiClasses.BidiClass.L;
            for (int i = n - 1; i >= 0; i--) {
                strongRight[i] = runStrong;
                var c = cls[i];
                if (c == BidiClasses.BidiClass.L) runStrong = BidiClasses.BidiClass.L;
                else if (c == BidiClasses.BidiClass.R || c == BidiClasses.BidiClass.EN)
                    runStrong = BidiClasses.BidiClass.R;
            }
            // Apply: neutral chars → resolved type.
            var baseCls = baseIsRtl ? BidiClasses.BidiClass.R : BidiClasses.BidiClass.L;
            for (int i = 0; i < n; i++) {
                var c = cls[i];
                if (c != BidiClasses.BidiClass.ON && c != BidiClasses.BidiClass.WS) continue;
                // N1/N2
                var l = strongLeft[i];
                var r = strongRight[i];
                cls[i] = (l == r) ? l : baseCls;
            }

            // --- Step 4: Build level assignments and merge adjacent same-level
            //             positions into logical runs. -------------------------
            // Level 0 = LTR, Level 1 = RTL (two levels only — see simplification 6).
            int runStart = 0;
            int runLevel = LevelFor(cls[0], baseIsRtl);
            for (int i = 1; i < n; i++) {
                int lvl = LevelFor(cls[i], baseIsRtl);
                if (lvl != runLevel) {
                    result.Add(new Run { Start = runStart, Length = i - runStart, Level = runLevel });
                    runStart = i;
                    runLevel = lvl;
                }
            }
            result.Add(new Run { Start = runStart, Length = n - runStart, Level = runLevel });
        }

        // --- Level assignment helper ----------------------------------------
        // Maps a resolved bidi class to an embedding level.
        // L → 0 (LTR), R/EN → 1 (RTL), WS/ON after neutral resolution → handled
        // by the resolved class already being L or R at this point.
        static int LevelFor(BidiClasses.BidiClass bc, bool baseIsRtl) {
            if (bc == BidiClasses.BidiClass.R || bc == BidiClasses.BidiClass.EN)
                return 1;
            if (bc == BidiClasses.BidiClass.L)
                return 0;
            // WS/ON that were not fully resolved → base direction.
            return baseIsRtl ? 1 : 0;
        }

        // --- L2 Visual Reorder -----------------------------------------------

        /// <summary>
        /// Given a slice of logical runs covering a single line (i.e. the
        /// runs whose character ranges lie within this line's fragment list),
        /// returns the same runs in VISUAL order (left-to-right screen order).
        ///
        /// UAX #9 rule L2 (per-line): from the highest embedding level down to
        /// the lowest odd level, reverse each maximal sequence of characters
        /// whose level is ≥ the current level. In our two-level model this
        /// reduces to: reverse the order of ALL runs at level ≥ 1 within each
        /// contiguous block of level-1 runs, then reverse the whole array if
        /// the paragraph base direction is RTL.
        ///
        /// The returned list is a NEW list; the input is not modified.
        /// </summary>
        /// <param name="runs">Logical runs for this line (sorted by char start).</param>
        /// <param name="baseIsRtl">Paragraph base direction.</param>
        /// <param name="visual">
        ///   Output list (cleared on entry). Receives runs in visual order.
        /// </param>
        public static void Reorder(List<Run> runs, bool baseIsRtl, List<Run> visual) {
            visual.Clear();
            if (runs == null || runs.Count == 0) return;

            // Copy to scratch so we don't mutate the logical list.
            for (int i = 0; i < runs.Count; i++) visual.Add(runs[i]);

            // UAX #9 L2 (two-level variant):
            // Find contiguous RTL (level-1) sub-sequences and reverse their ORDER
            // within each sub-sequence. The individual runs' char content is not
            // reversed here — that would produce mirror-image glyphs. Only the
            // run ORDER changes so that the rightmost logical RTL character paints
            // at the leftmost visual position of the RTL block.
            int count = visual.Count;
            int rtlStart = -1;
            for (int i = 0; i <= count; i++) {
                bool isRtl = (i < count) && (visual[i].Level == 1);
                if (isRtl && rtlStart < 0) {
                    rtlStart = i;
                } else if (!isRtl && rtlStart >= 0) {
                    // Reverse the block [rtlStart, i).
                    ReverseSegment(visual, rtlStart, i - 1);
                    rtlStart = -1;
                }
            }

            // For a base-RTL paragraph the whole line order also flips: the
            // first logical LTR run in an RTL paragraph sits visually on the right.
            if (baseIsRtl) {
                visual.Reverse();
            }
        }

        static void ReverseSegment(List<Run> list, int lo, int hi) {
            while (lo < hi) {
                var tmp = list[lo];
                list[lo] = list[hi];
                list[hi] = tmp;
                lo++;
                hi--;
            }
        }

        // --- Convenience overload (no pre-allocated output) ------------------

        /// <summary>
        /// Convenience overload: returns a new list of visual-order runs.
        /// For hot paths prefer the output-list overload to avoid the
        /// per-call allocation.
        /// </summary>
        public static List<Run> Reorder(List<Run> runs, bool baseIsRtl) {
            var result = new List<Run>(runs.Count);
            Reorder(runs, baseIsRtl, result);
            return result;
        }
    }
}
