using System;
using System.Collections.Generic;
using Weva.Layout.Boxes;

namespace Weva.Forms {
    // Multiline caret geometry for <textarea> (input/selection audit #6).
    //
    // The value renders through the normal inline pipeline (FM1 syncs the
    // model into the child TextNode), so the AUTHORITATIVE line/wrap
    // geometry already exists as LineBox/TextRun children of the textarea's
    // box. This map aligns those painted runs back to model-text indices and
    // answers the three questions the controller and painter need:
    //   IndexFromPoint  — pointer (border-box-relative) → caret index
    //   CaretRectFor    — caret index → (x, y, height)
    //   AddSelectionRects — selection range → one rect per visual line
    //
    // Alignment contract (UA `textarea { white-space: pre-wrap }`): every
    // character the line breaker drops from the painted runs is WHITESPACE —
    // consumed newlines at forced breaks and hung/discarded trailing spaces
    // at soft wraps. So walking the model text with a pointer and skipping
    // only whitespace where a run doesn't match re-derives each run's source
    // offset deterministically. Any mismatch (nested markup, a second
    // TextNode, future pipeline changes) returns null and callers fall back
    // to the pre-map behavior instead of guessing.
    public sealed class TextAreaCaretMap {
        public struct Segment {
            public int StartIndex;   // model-text index of the segment's first char
            public string Text;      // the painted run text
            public double X;         // border-box-relative
            public double Width;
            public int Line;         // index into lines
        }

        public struct Line {
            public int StartIndex;   // first model index laid on this line
            public int EndIndex;     // exclusive; == next line's StartIndex (dropped ws attributed here)
            public int VisualEndIndex; // last index with painted geometry (before dropped ws/newline)
            public double X;         // line box origin, border-box-relative
            public double Y;
            public double Height;
            public int FirstSegment; // index into segments (count via next line / total)
            public int SegmentCount;
        }

        readonly List<Line> lines = new();
        readonly List<Segment> segments = new();
        readonly Func<string, int, int, double> measure;
        readonly string text;

        public IReadOnlyList<Line> Lines => lines;

        TextAreaCaretMap(string text, Func<string, int, int, double> measure) {
            this.text = text ?? "";
            this.measure = measure;
        }

        // Builds the map from the textarea's laid-out box. Returns null when
        // the painted runs cannot be aligned to the model text (see the
        // alignment contract above) or when there is nothing to map.
        public static TextAreaCaretMap Build(Box textareaBox, string modelText,
                                             Func<string, int, int, double> measure) {
            if (textareaBox == null || measure == null) return null;
            var map = new TextAreaCaretMap(modelText, measure);
            return map.Populate(textareaBox) ? map : null;
        }

        bool Populate(Box root) {
            int p = 0;
            bool sawLine = false;
            if (!Walk(root, 0, 0, ref p, ref sawLine)) return false;
            if (!sawLine) {
                // Empty textarea: synthesize one empty line at the content
                // origin so the caret has somewhere to sit. Use the box's
                // padding/border as the origin and a zero height marker the
                // caller replaces with the font's line height.
                lines.Add(new Line {
                    StartIndex = 0, EndIndex = text.Length, VisualEndIndex = 0,
                    X = root.PaddingLeft + root.BorderLeft,
                    Y = root.PaddingTop + root.BorderTop,
                    Height = 0, FirstSegment = 0, SegmentCount = 0,
                });
            }
            // Close the line ranges: each line ends where the next begins.
            for (int i = 0; i < lines.Count; i++) {
                var ln = lines[i];
                ln.EndIndex = i + 1 < lines.Count ? lines[i + 1].StartIndex : text.Length;
                if (ln.EndIndex < ln.StartIndex) return false; // alignment went backwards
                lines[i] = ln;
            }
            return true;
        }

        // Depth-first in paint order. Line boxes may sit under anonymous
        // wrappers; runs may nest below the line box. Offsets accumulate
        // parent-relative X/Y so segment coordinates are border-box-relative
        // to the textarea.
        bool Walk(Box box, double offX, double offY, ref int p, ref bool sawLine) {
            foreach (var child in box.ChildList) {
                double cx = offX + child.X;
                double cy = offY + child.Y;
                if (child is LineBox lb) {
                    sawLine = true;
                    var line = new Line {
                        X = cx, Y = cy, Height = lb.Height,
                        FirstSegment = segments.Count, SegmentCount = 0,
                        StartIndex = -1,
                    };
                    int lineIdx = lines.Count;
                    lines.Add(line);
                    if (!CollectRuns(lb, cx, cy, lineIdx, ref p)) return false;
                    line = lines[lineIdx];
                    line.SegmentCount = segments.Count - line.FirstSegment;
                    if (line.SegmentCount == 0) {
                        // Empty forced line ("\n\n"): its start sits after the
                        // newline that ended the previous line. Skip exactly
                        // one line terminator (plus hung spaces before it).
                        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) p++;
                        if (p < text.Length && text[p] == '\r') p++;
                        if (p < text.Length && text[p] == '\n') p++;
                        line.StartIndex = p;
                        line.VisualEndIndex = p;
                    } else {
                        line.StartIndex = segments[line.FirstSegment].StartIndex;
                        var last = segments[segments.Count - 1];
                        line.VisualEndIndex = last.StartIndex + (last.Text?.Length ?? 0);
                    }
                    lines[lineIdx] = line;
                } else if (!(child is TextRun)) {
                    // Descend through anonymous/inline wrappers.
                    if (!Walk(child, cx, cy, ref p, ref sawLine)) return false;
                }
            }
            return true;
        }

        bool CollectRuns(Box box, double offX, double offY, int lineIdx, ref int p) {
            foreach (var child in box.ChildList) {
                if (child is TextRun tr) {
                    string rt = tr.Text ?? "";
                    if (rt.Length == 0) continue;
                    // Greedy whitespace-skipping alignment (see contract).
                    while (p < text.Length && !MatchesAt(p, rt)) {
                        char c = text[p];
                        if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f') p++;
                        else return false; // non-ws mismatch — bail, don't guess
                    }
                    if (!MatchesAt(p, rt)) return false; // ran off the end
                    segments.Add(new Segment {
                        StartIndex = p, Text = rt,
                        X = offX + tr.X, Width = tr.Width, Line = lineIdx,
                    });
                    p += rt.Length;
                } else {
                    if (!CollectRuns(child, offX + child.X, offY + child.Y, lineIdx, ref p)) return false;
                }
            }
            return true;
        }

        bool MatchesAt(int pos, string run) {
            if (pos + run.Length > text.Length) return false;
            return string.CompareOrdinal(text, pos, run, 0, run.Length) == 0;
        }

        // ── Queries ─────────────────────────────────────────────────────────

        int LineOf(int index) {
            for (int i = lines.Count - 1; i >= 0; i--) {
                if (index >= lines[i].StartIndex) return i;
            }
            return 0;
        }

        // Caret index → border-box-relative (x, y, height).
        public (double X, double Y, double Height) CaretRectFor(int index) {
            if (index < 0) index = 0;
            if (index > text.Length) index = text.Length;
            int li = LineOf(index);
            var ln = lines[li];
            double x = ln.X;
            for (int s = ln.FirstSegment; s < ln.FirstSegment + ln.SegmentCount; s++) {
                var seg = segments[s];
                int segEnd = seg.StartIndex + seg.Text.Length;
                if (index < seg.StartIndex) break;           // in dropped ws before this segment
                if (index <= segEnd) {
                    x = seg.X + measure(seg.Text, 0, index - seg.StartIndex);
                    return (x, ln.Y, ln.Height);
                }
                x = seg.X + seg.Width;                        // past this segment — keep advancing
            }
            // Past the last painted char (inside hung spaces / before the
            // newline): extend by measuring the dropped characters from the
            // MODEL text so the caret hangs like Chrome's.
            if (ln.SegmentCount > 0 && index > lines[li].VisualEndIndex) {
                int from = lines[li].VisualEndIndex;
                int count = Math.Min(index, ln.EndIndex) - from;
                // Never measure the line terminator itself.
                while (count > 0 && from + count > from
                       && (text[from + count - 1] == '\n' || text[from + count - 1] == '\r')) count--;
                if (count > 0) x += measure(text, from, count);
            }
            return (x, ln.Y, ln.Height);
        }

        // Border-box-relative point → caret index (Chrome clamping: above the
        // first line → its start edge column; below the last → its end; X
        // clamps to the line's ends; mid-glyph rounds to the closer slot).
        public int IndexFromPoint(double x, double y) {
            if (lines.Count == 0) return 0;
            int li = 0;
            if (y >= lines[0].Y) {
                li = lines.Count - 1;
                for (int i = 0; i < lines.Count; i++) {
                    if (y < lines[i].Y + lines[i].Height) { li = i; break; }
                }
            }
            var ln = lines[li];
            if (ln.SegmentCount == 0) return ln.StartIndex;
            if (x <= ln.X) return ln.StartIndex;
            for (int s = ln.FirstSegment; s < ln.FirstSegment + ln.SegmentCount; s++) {
                var seg = segments[s];
                if (x < seg.X) return seg.StartIndex;         // in a gap before the segment
                if (x <= seg.X + seg.Width) {
                    return seg.StartIndex + IndexWithinRun(seg.Text, x - seg.X);
                }
            }
            return ln.VisualEndIndex;
        }

        // Mid-glyph rounding within one run (runs are short — linear with
        // early exit is fine here; the O(log n) path exists for the flat
        // single-line model in TextEditModel.CaretIndexForX).
        int IndexWithinRun(string run, double x) {
            int best = 0;
            double bestD = double.MaxValue;
            for (int i = 0; i <= run.Length; i++) {
                if (i > 0 && i < run.Length && char.IsLowSurrogate(run[i])) continue;
                double cx = i == 0 ? 0 : measure(run, 0, i);
                double d = Math.Abs(cx - x);
                if (d < bestD) { bestD = d; best = i; }
                else if (cx > x) break;
            }
            return best;
        }

        // Emits one border-box-relative rect per visual line covered by
        // [selStart, selEnd). A line's rect spans from the selection start's
        // caret X to the end's; fully-covered middle lines span their painted
        // extent (plus a thin newline tab like Chrome's).
        public void AddSelectionRects(int selStart, int selEnd, List<(double X, double Y, double W, double H)> into) {
            if (into == null || selEnd <= selStart) return;
            if (selStart < 0) selStart = 0;
            if (selEnd > text.Length) selEnd = text.Length;
            int first = LineOf(selStart);
            int last = LineOf(Math.Max(selStart, selEnd - 1));
            for (int li = first; li <= last && li < lines.Count; li++) {
                var ln = lines[li];
                double x0 = li == first ? CaretRectFor(selStart).X : ln.X;
                double x1;
                if (li == last) {
                    x1 = CaretRectFor(selEnd).X;
                    // Selection ending exactly at the next line's start (the
                    // wrap/newline boundary) paints to this line's end instead.
                    if (li != LineOf(selEnd)) x1 = EndXOf(ln);
                } else {
                    // Newline-inclusive middle lines get Chrome's small tab
                    // past the last glyph to make the break visible.
                    x1 = EndXOf(ln) + (ln.EndIndex > ln.VisualEndIndex ? 4.0 : 0.0);
                }
                if (x1 > x0) into.Add((x0, ln.Y, x1 - x0, ln.Height));
            }
        }

        double EndXOf(Line ln) {
            if (ln.SegmentCount == 0) return ln.X;
            var seg = segments[ln.FirstSegment + ln.SegmentCount - 1];
            return seg.X + seg.Width;
        }
    }
}
