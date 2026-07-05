using System.Collections.Generic;
using Weva.Layout.Boxes;
using Weva.Paint.Conversion;

namespace Weva.DevTools {
    // Walks a laid-out Box tree and emits four conventional Chrome-DevTools
    // outline rects per box (margin = orange, border = yellow, padding = green,
    // content = blue). Coordinates are absolute (root-relative) — the same
    // convention BoxToPaintConverter feeds into PaintCommands. The renderer is
    // pure: it neither allocates per-frame state of its own nor mutates the
    // tree, so it's safe to call from OnGUI on every Repaint.
    public sealed class BoxOutlineRenderer {
        readonly List<OverlayRect> scratch = new();

        // When non-null, only emit overlays for boxes whose Element's ClassName
        // contains this substring AND every descendant of those boxes. Lets
        // the DevTools overlay scope to a single subtree (e.g. "play-btn")
        // instead of drawing 4 overlapping rects per ancestor across the page.
        public string MatchClassContains { get; set; }

        // When true (default), skip anonymous boxes — internal box types with
        // no Element (LineBox, anonymous flex items wrapping raw text, etc.).
        // These overlap a real element's rect 1:1 and just add noise.
        public bool SkipAnonymousBoxes { get; set; } = true;

        public IReadOnlyList<OverlayRect> Emit(Box root) {
            scratch.Clear();
            if (root == null) return scratch;
            Walk(root, 0, 0, false);
            return scratch;
        }

        // Reusable variant: caller owns the buffer (e.g. test asserting a single
        // box emits exactly four rects). Returns the count appended.
        public int EmitInto(Box root, List<OverlayRect> output) {
            if (output == null) return 0;
            int before = output.Count;
            if (root == null) return 0;
            WalkInto(root, 0, 0, output, false, MatchClassContains, SkipAnonymousBoxes);
            return output.Count - before;
        }

        void Walk(Box box, double parentAbsX, double parentAbsY, bool insideMatch) {
            WalkInto(box, parentAbsX, parentAbsY, scratch, insideMatch, MatchClassContains, SkipAnonymousBoxes);
        }

        static bool ElementMatches(Box box, string substring) {
            if (string.IsNullOrEmpty(substring)) return true;
            var e = box?.Element;
            if (e == null) return false;
            string cls = e.ClassName;
            return !string.IsNullOrEmpty(cls)
                && cls.IndexOf(substring, System.StringComparison.Ordinal) >= 0;
        }

        static void WalkInto(Box box, double parentAbsX, double parentAbsY, List<OverlayRect> output,
                             bool insideMatch, string matchSubstring, bool skipAnonymous) {
            if (box == null) return;
            double absX = parentAbsX + box.X + box.StickyOffsetX;
            double absY = parentAbsY + box.Y + box.StickyOffsetY;
            // CSS Transforms L1 §6: a `transform` declaration on this box (and
            // CSS Transforms L2 individual `translate`/`rotate`/`scale`
            // properties) move the painted output of the box AND all its
            // descendants. Without applying the same offset here, the overlay
            // outline lands at the layout-computed box.X/Y while URP paints
            // the box at box.X + transform.tx — visibly off by the translate
            // amount. Concrete case: a `position:absolute; left:50%;
            // transform:translateX(-50%)` centring trick offsets paint by
            // -50% of the element's width vs layout.
            //
            // We handle the common axis-aligned cases (pure translate) by
            // adding the resolved Tx/Ty to our running abs offset. Non-axis-
            // aligned transforms (rotate / non-uniform scale) would need a
            // 4-corner transform to render exactly; we still apply their
            // Tx/Ty so at minimum the bounding box position is correct.
            if (box.Style != null) {
                var xf = TransformResolver.ResolveTransform(box.Style, box.Width, box.Height);
                if (xf.Tx != 0f || xf.Ty != 0f) {
                    absX += xf.Tx;
                    absY += xf.Ty;
                }
            }

            // Class-match filter: once any ancestor matches, all descendants are
            // emitted too — so the overlay shows the whole subtree of the
            // selected element. A null/empty filter keeps the legacy "emit
            // every box" behaviour.
            bool nowInMatch = insideMatch
                || string.IsNullOrEmpty(matchSubstring)
                || ElementMatches(box, matchSubstring);

            // Skip text runs (no decoration), anonymous boxes (no Element), and
            // anything outside the matching subtree.
            bool emit = !(box is TextRun) && nowInMatch
                        && (!skipAnonymous || box.Element != null);
            if (emit) EmitFour(box, absX, absY, output);

            var children = box.Children;
            int n = children.Count;
            for (int i = 0; i < n; i++) {
                WalkInto(children[i], absX, absY, output, nowInMatch, matchSubstring, skipAnonymous);
            }
        }

        static void EmitFour(Box box, double absX, double absY, List<OverlayRect> output) {
            // Box.Width / Box.Height are the border-box rect (the layout engine
            // converts `content-box` author widths to border-box at resolve time
            // so painters and the DevTools overlay can read X/Y/W/H uniformly).
            double marginX = absX - box.MarginLeft;
            double marginY = absY - box.MarginTop;
            double marginW = box.Width + box.MarginLeft + box.MarginRight;
            double marginH = box.Height + box.MarginTop + box.MarginBottom;
            output.Add(new OverlayRect(marginX, marginY, marginW, marginH, OverlayRectKind.Margin));

            output.Add(new OverlayRect(absX, absY, box.Width, box.Height, OverlayRectKind.Border));

            double padX = absX + box.BorderLeft;
            double padY = absY + box.BorderTop;
            double padW = box.Width - box.BorderLeft - box.BorderRight;
            double padH = box.Height - box.BorderTop - box.BorderBottom;
            if (padW < 0) padW = 0;
            if (padH < 0) padH = 0;
            output.Add(new OverlayRect(padX, padY, padW, padH, OverlayRectKind.Padding));

            double contentX = padX + box.PaddingLeft;
            double contentY = padY + box.PaddingTop;
            double contentW = padW - box.PaddingLeft - box.PaddingRight;
            double contentH = padH - box.PaddingTop - box.PaddingBottom;
            if (contentW < 0) contentW = 0;
            if (contentH < 0) contentH = 0;
            output.Add(new OverlayRect(contentX, contentY, contentW, contentH, OverlayRectKind.Content));
        }
    }
}
