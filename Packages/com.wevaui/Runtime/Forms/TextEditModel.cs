using System;
using System.Collections.Generic;
using System.Text;
using Weva.Forms.Text;

namespace Weva.Forms {
    public sealed class TextEditModel {
        // Hoisted so `IndexOfAny` doesn't allocate a fresh array on every
        // keystroke — single-line inputs hit the newline-strip path on
        // every Insert, so the prior `new[] { '\n', '\r' }` cost ~16 B
        // per character of GC churn.
        static readonly char[] s_NewlineChars = { '\n', '\r' };

        string text;
        TextSelection selection;
        bool multiline;
        int? maxLength;

        // W4 phase-1 measurement seam — metric-aware up/down navigation.
        //
        // When non-null, LineUpFrom / LineDownFrom resolve the goal column
        // using pixel X (preserving visual position across variable-width
        // fonts and proportional text) rather than raw character count.
        //
        // Signature: (text, startIndex, charCount) → pixel width of that
        // substring.  This maps directly to IFontMetrics.Measure and to
        // CaretGeometry.CaretXForIndex (which internally calls Measure(0..idx)).
        //
        // Back-compat: when null the methods fall back to the original
        // char-column logic, keeping all existing single-line and
        // monospace-textarea tests green.
        //
        // Wiring point (engineer TODO — out of headless scope):
        //   In InputController.ctor, after `Model = new TextEditModel(...)`,
        //   assign:
        //     Model.SetMeasureSubstring((t, s, n) => widthOf(t.Substring(s, n), fontSize));
        //   where `widthOf` is the InputRenderer.TextWidthFunc the controller
        //   already has access to, and `fontSize` is resolved from the element's
        //   ComputedStyle.  This requires no API change to TextEditModel's
        //   public surface — just call SetMeasureSubstring once after construction.
        Func<string, int, int, double> measureSubstring;

        // IME state — distinct from confirmed text. While composing, the composing
        // string overlays the caret position; on commit it is inserted via Insert,
        // on cancel it is dropped.
        bool composing;
        string compositionString = "";
        int compositionAnchor;

        public TextEditModel(string initialText = "", bool multiline = false, int? maxLength = null) {
            text = initialText ?? "";
            this.multiline = multiline;
            this.maxLength = maxLength;
            selection = TextSelection.Caret(text.Length);
        }

        public string Text => text;
        public TextSelection Selection => selection;
        public bool Multiline {
            get => multiline;
            set => multiline = value;
        }
        public int? MaxLength {
            get => maxLength;
            set => maxLength = value;
        }

        public bool IsComposing => composing;
        public string CompositionString => compositionString;
        public int CompositionAnchor => compositionAnchor;

        // Sets the pixel-width measurer used by LineUpFrom / LineDownFrom.
        // Pass null to revert to the char-column fallback.
        // See field comment above for the wiring-point instructions.
        public void SetMeasureSubstring(Func<string, int, int, double> measurer) {
            measureSubstring = measurer;
        }

        // The wired measurer, for consumers that need raw substring widths in
        // the SAME metric space as the caret math (TextAreaCaretMap). Null
        // when no measurer is wired.
        public Func<string, int, int, double> MeasureSubstring => measureSubstring;

        public event Action Changed;
        public event Action SelectionChanged;

        void RaiseChanged() {
            Changed?.Invoke();
            SelectionChanged?.Invoke();
        }

        void RaiseSelectionChanged() {
            SelectionChanged?.Invoke();
        }

        // ── Undo/redo (input/selection audit follow-up) ─────────────────────
        // Snapshot-based: each user edit pushes the PRE-edit (text, selection)
        // onto the undo stack; consecutive plain typing (single collapsed-
        // caret character inserts) coalesces into ONE entry, broken by any
        // other edit or an explicit selection change — Chrome's grouping.
        // External SetText (attribute rewrites) clears the history: the field
        // no longer contains what the stack describes.
        readonly List<(string Text, TextSelection Sel)> undoStack = new();
        readonly List<(string Text, TextSelection Sel)> redoStack = new();
        bool lastEditWasTyping;
        const int UndoDepthCap = 100;

        public bool CanUndo => undoStack.Count > 0;
        public bool CanRedo => redoStack.Count > 0;

        void PushUndo(bool typing) {
            if (!(typing && lastEditWasTyping && undoStack.Count > 0)) {
                undoStack.Add((text, selection));
                if (undoStack.Count > UndoDepthCap) undoStack.RemoveAt(0);
            }
            lastEditWasTyping = typing;
            redoStack.Clear();
        }

        public bool Undo() {
            if (undoStack.Count == 0) return false;
            var entry = undoStack[undoStack.Count - 1];
            undoStack.RemoveAt(undoStack.Count - 1);
            redoStack.Add((text, selection));
            text = entry.Text;
            selection = entry.Sel;
            ClampSelection();
            lastEditWasTyping = false;
            RaiseChanged();
            return true;
        }

        public bool Redo() {
            if (redoStack.Count == 0) return false;
            var entry = redoStack[redoStack.Count - 1];
            redoStack.RemoveAt(redoStack.Count - 1);
            undoStack.Add((text, selection));
            text = entry.Text;
            selection = entry.Sel;
            ClampSelection();
            lastEditWasTyping = false;
            RaiseChanged();
            return true;
        }

        public void SetText(string newText) {
            newText ??= "";
            if (text == newText) return;
            // External rewrite — the history describes a value that no
            // longer exists.
            undoStack.Clear();
            redoStack.Clear();
            lastEditWasTyping = false;
            text = newText;
            ClampSelection();
            // Clamp compositionAnchor too. Without this, if `SetText` shortens
            // the text mid-composition (e.g., the value attribute is rewritten
            // externally while the user is typing CJK), the next
            // CommitComposition lands at a stale `compositionAnchor` past
            // `text.Length` and Insert throws ArgumentOutOfRangeException on
            // `text.Substring(0, selStart)`. Clamping keeps the model
            // recoverable — the in-flight composition is effectively
            // cancelled and the next IME callback re-anchors.
            if (compositionAnchor > text.Length) compositionAnchor = text.Length;
            if (compositionAnchor < 0) compositionAnchor = 0;
            RaiseChanged();
        }

        public void SetSelection(int start, int end, SelectionDirection direction = SelectionDirection.Forward) {
            int n = text.Length;
            if (start < 0) start = 0;
            if (end < 0) end = 0;
            if (start > n) start = n;
            if (end > n) end = n;
            int s, e;
            SelectionDirection d;
            if (start <= end) { s = start; e = end; d = direction == SelectionDirection.Backward ? SelectionDirection.Backward : (start == end ? SelectionDirection.None : SelectionDirection.Forward); }
            else { s = end; e = start; d = SelectionDirection.Backward; }
            var ns = new TextSelection(s, e, d);
            if (Eq(ns, selection)) return;
            selection = ns;
            // A deliberate caret/selection move ends a typing run — the next
            // character starts a fresh undo group (Chrome).
            lastEditWasTyping = false;
            RaiseSelectionChanged();
        }

        public void SetCaret(int position) => SetSelection(position, position, SelectionDirection.None);

        // FM2: pixel X of a caret slot, using the measurer wired via
        // SetMeasureSubstring. `displayText` substitutes the RENDERED text
        // (password bullet mask) when it differs from the model text; both
        // are code-unit aligned so indices carry over 1:1. 0 without a
        // measurer (callers treat the gesture as unmappable).
        public double CaretXForIndex(int index, string displayText = null) {
            string t = displayText ?? text;
            if (measureSubstring == null || string.IsNullOrEmpty(t) || index <= 0) return 0;
            if (index > t.Length) index = t.Length;
            return measureSubstring(t, 0, index);
        }

        // FM2: nearest caret slot for a run-relative pixel X — the
        // click-to-place primitive (Chrome's mid-glyph rounding: a click
        // inside a glyph snaps to the closer edge). Surrogate-safe: slots
        // inside a pair are skipped, mirroring CaretGeometry.IndexForX.
        //
        // Audit #12: binary search over the monotonic prefix advances —
        // O(log n) measures instead of the old linear scan's O(hit-index)
        // measures each O(i) deep (O(n²) per pointer-move near the end of a
        // long value; a 120Hz drag through a 500-char value hitched).
        public int CaretIndexForX(double x, string displayText = null) {
            string t = displayText ?? text;
            if (string.IsNullOrEmpty(t) || measureSubstring == null) return 0;
            if (x <= 0) return 0;
            int len = t.Length;
            double total = measureSubstring(t, 0, len);
            if (x >= total) return len;
            // Invariants: lo/hi are caret boundaries, cx(lo) <= x < cx(hi).
            int lo = 0, hi = len;
            while (true) {
                int mid = SnapBoundaryUp(t, lo + (hi - lo) / 2);
                if (mid == lo) mid = SnapBoundaryUp(t, lo + 1);
                if (mid >= hi) break;
                if (measureSubstring(t, 0, mid) <= x) lo = mid; else hi = mid;
            }
            // Chrome mid-glyph rounding: snap to the closer edge; exact
            // midpoint prefers the lower slot (same tie-break as the old
            // linear scan's strict `<` best tracking).
            double cLo = lo == 0 ? 0 : measureSubstring(t, 0, lo);
            double cHi = measureSubstring(t, 0, hi);
            return x - cLo <= cHi - x ? lo : hi;
        }

        // Rounds a would-be caret index UP off the low half of a surrogate
        // pair (an index inside a pair is not a valid caret boundary).
        static int SnapBoundaryUp(string t, int i) {
            if (i > 0 && i < t.Length && char.IsLowSurrogate(t[i])) i++;
            return i;
        }

        static bool Eq(TextSelection a, TextSelection b) {
            return a.Start == b.Start && a.End == b.End && a.Direction == b.Direction;
        }

        void ClampSelection() {
            int n = text.Length;
            int s = selection.Start;
            int e = selection.End;
            if (s > n) s = n;
            if (e > n) e = n;
            if (s < 0) s = 0;
            if (e < 0) e = 0;
            selection = new TextSelection(s, e, s == e ? SelectionDirection.None : selection.Direction);
        }

        public bool Insert(string s) {
            if (s == null) s = "";
            int selStart = selection.Start;
            int selEnd = selection.End;
            int newLen = text.Length - (selEnd - selStart) + s.Length;
            if (maxLength.HasValue && newLen > maxLength.Value) {
                int allowed = maxLength.Value - (text.Length - (selEnd - selStart));
                if (allowed <= 0) return false;
                if (allowed < s.Length) {
                    // Never split a surrogate pair (audit FM6): truncating
                    // between a high and low surrogate stores an orphan half
                    // into the model AND the value attribute — a corrupt
                    // string flows to serialization, measurement and paint.
                    // Chrome drops the whole astral character instead.
                    if (char.IsHighSurrogate(s[allowed - 1])) allowed--;
                    if (allowed <= 0) return false;
                    s = s.Substring(0, allowed);
                }
            }
            if (!multiline && s.IndexOfAny(s_NewlineChars) >= 0) {
                var sb = new StringBuilder(s.Length);
                foreach (var ch in s) {
                    if (ch == '\n' || ch == '\r') continue;
                    sb.Append(ch);
                }
                s = sb.ToString();
            }
            // Plain typing (a single collapsed-caret character) coalesces.
            PushUndo(typing: s.Length == 1 && selStart == selEnd);
            var before = text.Substring(0, selStart);
            var after = text.Substring(selEnd);
            text = before + s + after;
            int caret = selStart + s.Length;
            selection = TextSelection.Caret(caret);
            RaiseChanged();
            return true;
        }

        public bool DeleteBackward() {
            if (!selection.IsCollapsed) return DeleteSelection();
            if (selection.Start == 0) return false;
            int newCaret = selection.Start - 1;
            int count = 1;
            // Surrogate-pair guard: a single emoji or supplementary-plane
            // codepoint encodes as two UTF-16 code units (high + low
            // surrogate). Removing only one unit leaves an orphan and
            // corrupts the string when serialized. Detect the low
            // surrogate at newCaret and back up one more to include its
            // high partner.
            if (newCaret > 0 && char.IsLowSurrogate(text[newCaret]) && char.IsHighSurrogate(text[newCaret - 1])) {
                newCaret -= 1;
                count = 2;
            }
            PushUndo(typing: false);
            text = text.Remove(newCaret, count);
            selection = TextSelection.Caret(newCaret);
            RaiseChanged();
            return true;
        }

        public bool DeleteForward() {
            if (!selection.IsCollapsed) return DeleteSelection();
            if (selection.Start >= text.Length) return false;
            int count = 1;
            // Symmetric surrogate-pair guard for forward delete: if the
            // caret is at a high surrogate, the matching low surrogate
            // follows and we must remove both.
            int start = selection.Start;
            if (start + 1 < text.Length && char.IsHighSurrogate(text[start]) && char.IsLowSurrogate(text[start + 1])) {
                count = 2;
            }
            PushUndo(typing: false);
            text = text.Remove(start, count);
            // Caret stays put.
            selection = TextSelection.Caret(start);
            RaiseChanged();
            return true;
        }

        public bool DeleteWordBackward() {
            if (!selection.IsCollapsed) return DeleteSelection();
            int caret = selection.Start;
            if (caret == 0) return false;
            int wordStart = PreviousWordBoundary(caret);
            PushUndo(typing: false);
            text = text.Remove(wordStart, caret - wordStart);
            selection = TextSelection.Caret(wordStart);
            RaiseChanged();
            return true;
        }

        public bool DeleteWordForward() {
            if (!selection.IsCollapsed) return DeleteSelection();
            int caret = selection.Start;
            if (caret >= text.Length) return false;
            int wordEnd = NextWordBoundary(caret);
            PushUndo(typing: false);
            text = text.Remove(caret, wordEnd - caret);
            selection = TextSelection.Caret(caret);
            RaiseChanged();
            return true;
        }

        bool DeleteSelection() {
            if (selection.IsCollapsed) return false;
            int s = selection.Start;
            int e = selection.End;
            PushUndo(typing: false);
            text = text.Remove(s, e - s);
            selection = TextSelection.Caret(s);
            RaiseChanged();
            return true;
        }

        public void MoveCaretLeft(bool extendSelection = false) {
            int focus = extendSelection ? selection.Focus : (selection.IsCollapsed ? selection.Start : selection.Start);
            int newFocus;
            if (extendSelection) {
                newFocus = StepLeftAcrossSurrogate(focus);
                ApplyFocusMovement(newFocus);
            } else {
                if (selection.IsCollapsed) {
                    newFocus = StepLeftAcrossSurrogate(selection.Start);
                    SetCaret(newFocus);
                } else {
                    SetCaret(selection.Start);
                }
            }
        }

        public void MoveCaretRight(bool extendSelection = false) {
            int focus = extendSelection ? selection.Focus : selection.End;
            if (extendSelection) {
                int newFocus = StepRightAcrossSurrogate(focus);
                ApplyFocusMovement(newFocus);
            } else {
                if (selection.IsCollapsed) {
                    int newFocus = StepRightAcrossSurrogate(selection.End);
                    SetCaret(newFocus);
                } else {
                    SetCaret(selection.End);
                }
            }
        }

        // Step one code-point left, advancing past UTF-16 surrogate pairs as
        // a single unit. Mirrors the guards DeleteBackward / DeleteForward
        // already apply (lines 147, 165) — caret movement was missed, so
        // Left-Arrow over an emoji like 👍 (a high+low surrogate pair) used
        // to land between the two code units, producing orphaned-surrogate
        // substrings on the next read.
        int StepLeftAcrossSurrogate(int from) {
            int n = Math.Max(0, from - 1);
            if (n > 0 && char.IsLowSurrogate(text[n]) && char.IsHighSurrogate(text[n - 1])) n--;
            return n;
        }

        int StepRightAcrossSurrogate(int from) {
            int n = Math.Min(text.Length, from + 1);
            if (n < text.Length && char.IsLowSurrogate(text[n]) && n > 0 && char.IsHighSurrogate(text[n - 1])) n++;
            return n;
        }

        public void MoveWordLeft(bool extendSelection = false) {
            int focus = extendSelection ? selection.Focus : selection.Start;
            int newFocus = PreviousWordBoundary(focus);
            if (extendSelection) ApplyFocusMovement(newFocus);
            else SetCaret(newFocus);
        }

        public void MoveWordRight(bool extendSelection = false) {
            int focus = extendSelection ? selection.Focus : selection.End;
            int newFocus = NextWordBoundary(focus);
            if (extendSelection) ApplyFocusMovement(newFocus);
            else SetCaret(newFocus);
        }

        public void MoveLineUp(bool extendSelection = false) {
            int focus = extendSelection ? selection.Focus : selection.Start;
            int newFocus = LineUpFrom(focus);
            if (extendSelection) ApplyFocusMovement(newFocus);
            else SetCaret(newFocus);
        }

        public void MoveLineDown(bool extendSelection = false) {
            int focus = extendSelection ? selection.Focus : selection.End;
            int newFocus = LineDownFrom(focus);
            if (extendSelection) ApplyFocusMovement(newFocus);
            else SetCaret(newFocus);
        }

        public void MoveToHome(bool extendSelection = false, bool wholeText = false) {
            int focus = extendSelection ? selection.Focus : selection.Start;
            int newFocus = wholeText ? 0 : LineStart(focus);
            if (extendSelection) ApplyFocusMovement(newFocus);
            else SetCaret(newFocus);
        }

        public void MoveToEnd(bool extendSelection = false, bool wholeText = false) {
            int focus = extendSelection ? selection.Focus : selection.End;
            int newFocus = wholeText ? text.Length : LineEnd(focus);
            if (extendSelection) ApplyFocusMovement(newFocus);
            else SetCaret(newFocus);
        }

        public void SelectAll() {
            SetSelection(0, text.Length, SelectionDirection.Forward);
        }

        public void Collapse(bool toEnd = false) {
            if (selection.IsCollapsed) return;
            SetCaret(toEnd ? selection.End : selection.Start);
        }

        void ApplyFocusMovement(int newFocus) {
            int anchor = selection.Direction == SelectionDirection.Backward ? selection.End : selection.Start;
            if (selection.IsCollapsed) anchor = selection.Start;
            int s, e;
            SelectionDirection d;
            if (newFocus >= anchor) { s = anchor; e = newFocus; d = newFocus == anchor ? SelectionDirection.None : SelectionDirection.Forward; }
            else { s = newFocus; e = anchor; d = SelectionDirection.Backward; }
            var ns = new TextSelection(s, e, d);
            if (Eq(ns, selection)) return;
            selection = ns;
            RaiseSelectionChanged();
        }

        int LineStart(int pos) {
            if (pos <= 0) return 0;
            int i = pos - 1;
            while (i >= 0 && text[i] != '\n') i--;
            return i + 1;
        }

        int LineEnd(int pos) {
            int i = pos;
            while (i < text.Length && text[i] != '\n') i++;
            return i;
        }

        int LineUpFrom(int pos) {
            int curLineStart = LineStart(pos);
            if (curLineStart == 0) return 0;
            int prevLineStart = LineStart(curLineStart - 1);
            int prevLineEnd = curLineStart - 1; // points at the '\n'
            // W4 phase-1: metric-aware goal-column when a measurer is wired.
            // CSS Text L3 §9.2: vertical moves preserve visual X ("goal column").
            if (measureSubstring != null) {
                // Measure pixel X of the caret on the current line, then find
                // the nearest caret slot on the previous line at the same X.
                double goalX = measureSubstring(text, curLineStart, pos - curLineStart);
                return prevLineStart + IndexForXInLine(prevLineStart, prevLineEnd - prevLineStart, goalX);
            }
            // Char-column fallback (monospace / no measurer wired).
            int col = pos - curLineStart;
            int prevLineLen = prevLineEnd - prevLineStart;
            return prevLineStart + Math.Min(col, prevLineLen);
        }

        int LineDownFrom(int pos) {
            int curLineEnd = LineEnd(pos);
            if (curLineEnd >= text.Length) return text.Length;
            int curLineStart = LineStart(pos);
            int nextLineStart = curLineEnd + 1;
            int nextLineEnd = LineEnd(nextLineStart);
            // W4 phase-1: metric-aware goal-column when a measurer is wired.
            if (measureSubstring != null) {
                double goalX = measureSubstring(text, curLineStart, pos - curLineStart);
                return nextLineStart + IndexForXInLine(nextLineStart, nextLineEnd - nextLineStart, goalX);
            }
            // Char-column fallback (monospace / no measurer wired).
            int col = pos - curLineStart;
            int nextLineLen = nextLineEnd - nextLineStart;
            return nextLineStart + Math.Min(col, nextLineLen);
        }

        // Returns the caret column (0..lineLen) within a line segment of `text`
        // whose pixel X is nearest to `goalX`, using the measureSubstring delegate.
        //
        // Implements the same "mid-glyph" rule as CaretGeometry.IndexForX:
        // if goalX falls in the left half of glyph i, the slot BEFORE i wins;
        // if it falls in the right half, the slot AFTER i wins.
        // Surrogate-pair safety: step by 2 when the current code unit is a
        // high surrogate (same guard as CaretGeometry.IndexForX).
        int IndexForXInLine(int lineStart, int lineLen, double goalX) {
            if (lineLen <= 0 || goalX <= 0.0) return 0;
            double totalWidth = measureSubstring(text, lineStart, lineLen);
            if (goalX >= totalWidth) return lineLen;
            double xLeft = 0.0;
            int i = 0;
            while (i < lineLen) {
                int absIdx = lineStart + i;
                int step = (absIdx + 1 < text.Length
                    && char.IsHighSurrogate(text[absIdx])
                    && char.IsLowSurrogate(text[absIdx + 1])) ? 2 : 1;
                double xRight = measureSubstring(text, lineStart, i + step);
                double xMid = (xLeft + xRight) * 0.5;
                if (goalX < xMid) return i;
                xLeft = xRight;
                i += step;
            }
            return lineLen;
        }

        // W4 phase-1: delegate to WordBoundary which handles CJK one-codepoint-
        // at-a-time (UAX #14 ID class) and surrogate-pair safety (UAX #29 §3).
        // Old private IsWordSeparator treated CJK as word-chars, so Ctrl+Arrow
        // jumped over entire CJK phrases instead of landing on each codepoint.
        int PreviousWordBoundary(int pos) {
            return WordBoundary.PreviousWordBoundary(text, pos);
        }

        int NextWordBoundary(int pos) {
            return WordBoundary.NextWordBoundary(text, pos);
        }

        // ---- IME composition ----

        public void BeginComposition() {
            if (composing) {
                CancelComposition();
            }
            if (!selection.IsCollapsed) {
                DeleteSelection();
            }
            composing = true;
            compositionAnchor = selection.Start;
            compositionString = "";
        }

        public void UpdateComposition(string composing) {
            if (!this.composing) {
                BeginComposition();
            }
            compositionString = composing ?? "";
        }

        public bool CommitComposition(string finalText) {
            if (!composing) return false;
            composing = false;
            compositionString = "";
            if (string.IsNullOrEmpty(finalText)) {
                RaiseChanged();
                return false;
            }
            // Caret was at compositionAnchor; insert there.
            selection = TextSelection.Caret(compositionAnchor);
            return Insert(finalText);
        }

        public void CancelComposition() {
            if (!composing) return;
            composing = false;
            compositionString = "";
            selection = TextSelection.Caret(compositionAnchor);
            RaiseChanged();
        }
    }
}
