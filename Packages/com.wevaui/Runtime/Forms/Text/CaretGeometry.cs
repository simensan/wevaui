using System;
using Weva.Layout.Text;

namespace Weva.Forms.Text {
    // CaretGeometry — deterministic caret-index ↔ X-position mapping for a
    // single styled text run, using IFontMetrics as the measurement back-end.
    //
    // Design notes (W4 phase 1, ROADMAP.md):
    //
    //   • CaretXForIndex  — converts a UTF-16 caret index (i.e. an insertion
    //     point, 0 = before first char, text.Length = after last char) into a
    //     pixel X offset measured from the left edge of the run.  Uses the
    //     IFontMetrics substring-window overload so no intermediate strings are
    //     allocated per call.
    //
    //   • IndexForX — inverts the mapping.  Searches for the caret slot whose
    //     rendered edge is nearest to the given X.  When the query lands inside
    //     a glyph, the slot is rounded to whichever side is closer — matching
    //     the browser "mid-glyph" rule (Chrome/Firefox both use this for click-
    //     to-place and touch-to-place).
    //
    //   • Surrogate-pair safety — both methods walk only valid caret positions
    //     (indices that are NOT inside a surrogate pair).  IndexForX therefore
    //     never returns an index that splits a supplementary-plane codepoint.
    //
    //   • Empty / null text — all boundary inputs return X=0 / index=0.
    //
    // Threading: all methods are pure / re-entrant; no mutable state.
    public static class CaretGeometry {
        // Returns the pixel X offset of caret slot `index` within `text` at the
        // given `fontSize`, measured from the left edge of the run (X = 0).
        //
        // A "caret slot" is an insertion point between characters: slot 0 is
        // before the first character, slot text.Length is after the last.
        //
        // Pre: 0 ≤ index ≤ text.Length; index must not split a surrogate pair.
        // Post: return value ≥ 0.
        public static double CaretXForIndex(string text, int index, double fontSize, IFontMetrics metrics) {
            if (metrics == null) throw new ArgumentNullException(nameof(metrics));
            if (string.IsNullOrEmpty(text) || index <= 0) return 0.0;
            int len = text.Length;
            if (index > len) index = len;
            // Clamp away from the middle of a surrogate pair — if the supplied
            // index lands on a low surrogate, snap left to the high surrogate so
            // the returned X sits at the pair's leading edge.
            if (index > 0 && index < len
                && char.IsLowSurrogate(text[index])
                && char.IsHighSurrogate(text[index - 1])) {
                index--;
            }
            return metrics.Measure(text, 0, index, fontSize);
        }

        // Returns the caret slot index (0..text.Length) whose left/right boundary
        // is nearest to pixel X from the run's left edge.
        //
        // Algorithm:
        //   Walk caret positions left-to-right.  For each inter-glyph gap, the
        //   mid-point x_mid = (x_left + x_right) / 2 determines which slot wins:
        //   if x < x_mid → left slot; if x ≥ x_mid → right slot.  This matches
        //   the browser "nearest insertion point" rule for click-to-place.
        //
        // Surrogate-pair safety: the inner loop advances by 2 for supplementary-
        // plane characters so only valid caret positions are visited.
        //
        // Edge cases:
        //   x ≤ 0            → 0
        //   x ≥ total width  → text.Length
        //   text null/empty  → 0
        public static int IndexForX(string text, double x, double fontSize, IFontMetrics metrics) {
            if (metrics == null) throw new ArgumentNullException(nameof(metrics));
            if (string.IsNullOrEmpty(text)) return 0;
            if (x <= 0.0) return 0;

            int len = text.Length;
            double runWidth = metrics.Measure(text, 0, len, fontSize);
            if (x >= runWidth) return len;

            // Walk each caret slot from 0 to len, comparing the mid-point of
            // each inter-glyph gap against x.
            double xLeft = 0.0;
            int i = 0;
            while (i < len) {
                // Advance one codepoint (1 or 2 code units for surrogates).
                int step = (char.IsHighSurrogate(text[i]) && i + 1 < len && char.IsLowSurrogate(text[i + 1]))
                    ? 2 : 1;
                // xRight = X of caret slot after this codepoint
                double xRight = metrics.Measure(text, 0, i + step, fontSize);
                double xMid = (xLeft + xRight) * 0.5;
                if (x < xMid) {
                    // Nearest slot is before this codepoint.
                    return i;
                }
                xLeft = xRight;
                i += step;
            }
            // x was past the last mid-point but before runWidth — end of string.
            return len;
        }

        // Returns true when `index` is a valid caret slot within `text` — i.e. it
        // does NOT land in the middle of a UTF-16 surrogate pair.  Useful for
        // assertions in test code.
        public static bool IsValidCaretIndex(string text, int index) {
            if (text == null || index < 0 || index > text.Length) return false;
            if (index == 0 || index == text.Length) return true;
            // Invalid if index sits between a high surrogate and its low partner.
            return !(char.IsLowSurrogate(text[index]) && char.IsHighSurrogate(text[index - 1]));
        }
    }
}
