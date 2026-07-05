using System;
using Weva.Layout.Text;

namespace Weva.Forms.Text {
    // LineCaretNavigator — goal-column (preferred-X) line-up/down navigation
    // for multi-line text fields.
    //
    // Background (W4 phase 1, ROADMAP.md):
    //   Browsers preserve a "goal column" (preferred visual X position) across
    //   vertical arrow-key moves.  When you move the caret to a shorter line,
    //   the engine clamps to that line's end.  When you then arrow back to the
    //   longer line, the caret RETURNS to its original X — not to the
    //   truncated column.  The preferred X is cleared only when the user makes
    //   a horizontal move or types.
    //
    //   TextEditModel.MoveLineUp/Down implements the simple char-column path
    //   (good enough for monospace).  LineCaretNavigator provides the metric-
    //   aware goal-column variant that is wired into production multi-line inputs.
    //
    // Usage:
    //   Call UpdatePreferredX whenever a non-vertical move occurs (typing, left/
    //   right, click).  Call MoveUp / MoveDown for ArrowUp / ArrowDown.  The
    //   navigator holds the preferred X so the caller does not need to track it.
    //
    // API is box-tree-friendly but decoupled from the layout pipeline: accepts
    // the per-line strings plus a shared IFontMetrics + fontSize, so the caller
    // controls measurement.
    //
    // Surrogate-pair safety is inherited from CaretGeometry.IndexForX.
    public sealed class LineCaretNavigator {
        double preferredX = -1.0; // negative = not set; cleared on non-vertical move

        // Index of the "active" caret: the position that IndexForX / CaretXForIndex
        // operate against.
        //
        // Layout inputs — passed as a value type for determinism.
        readonly IFontMetrics metrics;
        readonly double fontSize;

        public LineCaretNavigator(IFontMetrics metrics, double fontSize) {
            if (metrics == null) throw new ArgumentNullException(nameof(metrics));
            this.metrics = metrics;
            this.fontSize = fontSize;
        }

        // ---- Preferred-X management ----

        // Capture the current caret X from (lineText, indexInLine) and store it
        // as the new preferred X.  Call this after every horizontal move or edit.
        public void UpdatePreferredX(string lineText, int indexInLine) {
            preferredX = CaretGeometry.CaretXForIndex(lineText, indexInLine, fontSize, metrics);
        }

        // Clears the preferred X — i.e. the next vertical move will capture it
        // fresh from the current position.
        public void ClearPreferredX() {
            preferredX = -1.0;
        }

        public bool HasPreferredX => preferredX >= 0.0;
        public double PreferredX => preferredX < 0.0 ? 0.0 : preferredX;

        // ---- Line navigation ----

        // Returns the caret index within `targetLineText` that is nearest to the
        // preferred X, lazily capturing it from (currentLineText, currentIndexInLine)
        // if it has not been set yet.
        //
        // The "preferred X" is NOT cleared here — a subsequent MoveUp/MoveDown
        // will continue using the same goal column.  The caller is responsible for
        // calling UpdatePreferredX when the user makes a non-vertical move.
        //
        // Parameters:
        //   currentLineText     — the text of the line the caret is currently on.
        //   currentIndexInLine  — caret index within currentLineText.
        //   targetLineText      — the text of the destination line (above or below).
        //
        // Returns: caret index within targetLineText (0..targetLineText.Length).
        public int NavigateTo(string currentLineText, int currentIndexInLine, string targetLineText) {
            // Lazily establish the preferred X on the first vertical move.
            if (preferredX < 0.0) {
                preferredX = CaretGeometry.CaretXForIndex(
                    currentLineText, currentIndexInLine, fontSize, metrics);
            }
            return CaretGeometry.IndexForX(targetLineText, preferredX, fontSize, metrics);
        }
    }

    // LineBox — thin value type describing one visual line for multi-line
    // navigation.  Box-tree-friendly: the consumer extracts the line strings from
    // the layout tree and passes them here; no layout types leak in.
    //
    // offsetInText = byte offset of lineText[0] within the full field text.
    // This lets the navigator translate a within-line index back to a global
    // text index.
    public readonly struct LineBox {
        public readonly string Text;
        public readonly int OffsetInText;

        public LineBox(string text, int offsetInText) {
            Text = text ?? "";
            OffsetInText = offsetInText;
        }
    }

    // MultiLineCaretNavigator — operates over an array of LineBoxes (one per
    // visual line) and tracks the global caret index + preferred X.
    //
    // This is the stateful driver the InputController will wire into a multi-
    // line field.  All indices are in the global text coordinate space
    // (0..text.Length).  The navigator decomposes them into (line, col) pairs
    // for metric-aware X mapping.
    public sealed class MultiLineCaretNavigator {
        readonly IFontMetrics metrics;
        readonly double fontSize;
        double preferredX = -1.0; // negative = stale / not set

        public MultiLineCaretNavigator(IFontMetrics metrics, double fontSize) {
            if (metrics == null) throw new ArgumentNullException(nameof(metrics));
            this.metrics = metrics;
            this.fontSize = fontSize;
        }

        public bool HasPreferredX => preferredX >= 0.0;
        public double PreferredX => preferredX < 0.0 ? 0.0 : preferredX;

        // Call this after every horizontal edit/move so the goal column resets.
        public void InvalidatePreferredX() {
            preferredX = -1.0;
        }

        // Returns the new global caret index after moving up from `globalIndex`
        // across the supplied `lines`.  If already on the first line, returns
        // the index of the first character of that line (Home-equivalent clamping
        // — same as Chrome/Firefox behaviour at top boundary).
        //
        // Side-effect: establishes preferredX if not already set.
        public int MoveUp(int globalIndex, LineBox[] lines) {
            if (lines == null || lines.Length == 0) return globalIndex;
            int lineIdx = FindLine(globalIndex, lines);
            var curLine = lines[lineIdx];
            int colInLine = globalIndex - curLine.OffsetInText;

            // Capture goal X on first vertical move.
            if (preferredX < 0.0) {
                preferredX = CaretGeometry.CaretXForIndex(curLine.Text, colInLine, fontSize, metrics);
            }

            if (lineIdx == 0) {
                // Already on first line — clamp to start.
                return curLine.OffsetInText;
            }

            var prevLine = lines[lineIdx - 1];
            int colInPrev = CaretGeometry.IndexForX(prevLine.Text, preferredX, fontSize, metrics);
            return prevLine.OffsetInText + colInPrev;
        }

        // Returns the new global caret index after moving down from `globalIndex`.
        // If already on the last line, returns the last character's position
        // (End-equivalent clamping).
        //
        // Side-effect: establishes preferredX if not already set.
        public int MoveDown(int globalIndex, LineBox[] lines) {
            if (lines == null || lines.Length == 0) return globalIndex;
            int lineIdx = FindLine(globalIndex, lines);
            var curLine = lines[lineIdx];
            int colInLine = globalIndex - curLine.OffsetInText;

            // Capture goal X on first vertical move.
            if (preferredX < 0.0) {
                preferredX = CaretGeometry.CaretXForIndex(curLine.Text, colInLine, fontSize, metrics);
            }

            if (lineIdx == lines.Length - 1) {
                // Already on last line — clamp to end.
                return curLine.OffsetInText + curLine.Text.Length;
            }

            var nextLine = lines[lineIdx + 1];
            int colInNext = CaretGeometry.IndexForX(nextLine.Text, preferredX, fontSize, metrics);
            return nextLine.OffsetInText + colInNext;
        }

        // Returns the line index (0-based) that contains globalIndex.
        // Ties (index == line.OffsetInText + line.Text.Length and there is a
        // next line) go to the NEXT line — matching browser behaviour where the
        // end-of-line newline is logically part of the next line.
        // Exception: if globalIndex equals the total text length, it stays on
        // the last line.
        static int FindLine(int globalIndex, LineBox[] lines) {
            for (int i = 0; i < lines.Length - 1; i++) {
                int lineEnd = lines[i].OffsetInText + lines[i].Text.Length;
                if (globalIndex < lineEnd) return i;
                // When globalIndex == lineEnd, check if there's a trailing
                // newline separator (meaning the caret is at end-of-line,
                // logically belonging to this line).
                if (globalIndex == lineEnd) return i;
            }
            return lines.Length - 1;
        }
    }
}
