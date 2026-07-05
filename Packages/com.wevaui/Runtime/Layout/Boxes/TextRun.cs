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

        public TextRun() { }

        public TextRun(string text, ComputedStyle style, Element element, TextNode source) {
            Text = text ?? "";
            Style = style;
            Element = element;
            SourceNode = source;
        }

        internal override void ResetForPool() {
            base.ResetForPool();
            Text = null;
            FontFamily = null;
            FontSize = 0;
            Color = null;
            SourceNode = null;
            JustifyLetterSpacingPx = 0;
        }
    }
}
