using Weva.Css.Cascade;
using Weva.Dom;

namespace Weva.Layout.Boxes {
    public sealed class TextRun : Box {
        public string Text { get; internal set; }
        public string FontFamily { get; internal set; }
        public double FontSize { get; internal set; }
        public string Color { get; internal set; }
        public TextNode SourceNode { get; internal set; }

        // CSS Text L3 §7.3 inter-character justify: per-run extra spacing (px)
        // added on top of the CSS `letter-spacing` value by JustifyLineInterCharacter.
        // The paint converter adds this to the CSS-resolved letter-spacing so the
        // glyph baker spreads characters to fill the available line width.
        // Reset to 0 in ResetForPool so recycled runs never carry stale justify state.
        public double JustifyLetterSpacingPx { get; internal set; }

        // True when this run stands in for a `<br>`. The run is EMPTY — the
        // newline is consumed by the break it caused — so afterwards nothing
        // about its text or style says it was ever a break.
        //
        // It has to stay identifiable because a container can be laid out
        // TWICE: an inline-block, or an auto-width absolutely positioned atom,
        // is measured and then laid out again at its fitted width. The second
        // pass walks the child list the first pass left behind, and there the
        // <br>'s InlineBox has been replaced by this run. Without the marker
        // that pass sees an ordinary empty run, produces no forced break, and
        // the box renders one line where the author wrote two — and, having
        // measured one long line, does not shrink to fit either.
        // Cleared in ResetForPool so a recycled run never claims to be a break.
        public bool IsForcedBreak { get; internal set; }

        // The `<br>`'s own InlineBox. Kept alongside the marker so a second
        // pass can put the box back in the tree, not just re-create the break:
        // pass 1 re-parents it onto a LineBox, and clearing the container's
        // children for pass 2 orphans it. Chrome reports a <br> as a
        // zero-width box with the line's height, so dropping it leaves the box
        // tree one entry short of what every other engine reports.
        // Cleared in ResetForPool with the marker.
        public InlineBox ForcedBreakBox { get; internal set; }

        public TextRun() { }

        public TextRun(string text, ComputedStyle style, Element element, TextNode source) {
            Text = text ?? "";
            Style = style;
            Element = element;
            SourceNode = source;
        }

        internal override void ResetForPool() {
            base.ResetForPool();
            IsForcedBreak = false;
            ForcedBreakBox = null;
            Text = null;
            FontFamily = null;
            FontSize = 0;
            Color = null;
            SourceNode = null;
            JustifyLetterSpacingPx = 0;
        }
    }
}
