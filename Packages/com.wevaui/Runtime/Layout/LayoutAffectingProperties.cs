using System.Collections.Generic;

namespace Weva.Layout {
    // Static classifier used by the cascade's per-element-state-digest path
    // (CascadeEngine.IncrementalState) and the InvalidationTracker bubble logic
    // to decide whether a computed-style change needs to mark Layout dirty in
    // addition to Style.
    //
    // Layout-affecting set per spec citations in IncrementalLayoutGate.cs:
    // CSS Box Model L3 §8 (margins/borders/padding/sizing), CSS Display L3 §2
    // (display/position), CSS Flexbox L1 §5 (flex-*), CSS Grid L1 §7 (grid-*),
    // CSS Inline L3 §3 (font/line/letter-spacing), CSS Positioned Layout L3 §6
    // (top/right/bottom/left), CSS Sizing L3 §5 (width/height/min-/max-/aspect),
    // CSS UI L4 §4 (outline does NOT participate in box sizing).
    //
    // NON-layout properties NOT in this set (paint-only): color, background-*,
    // border-color, border-style, border-radius, opacity, transform, filter,
    // box-shadow, text-shadow, text-decoration, cursor, outline-{color,style,
    // width,offset}.
    public static class LayoutAffectingProperties {
        static readonly HashSet<string> set = Build();

        public static bool IsLayoutAffecting(string property) {
            if (string.IsNullOrEmpty(property)) return false;
            return set.Contains(property);
        }

        static HashSet<string> Build() {
            var s = new HashSet<string>();
            // Sizing
            s.Add("width"); s.Add("height");
            s.Add("min-width"); s.Add("min-height");
            s.Add("max-width"); s.Add("max-height");
            s.Add("inline-size"); s.Add("block-size");
            s.Add("min-inline-size"); s.Add("min-block-size");
            s.Add("max-inline-size"); s.Add("max-block-size");
            s.Add("aspect-ratio");
            // Box model
            s.Add("margin"); s.Add("margin-top"); s.Add("margin-right"); s.Add("margin-bottom"); s.Add("margin-left");
            s.Add("margin-inline"); s.Add("margin-inline-start"); s.Add("margin-inline-end");
            s.Add("margin-block"); s.Add("margin-block-start"); s.Add("margin-block-end");
            s.Add("padding"); s.Add("padding-top"); s.Add("padding-right"); s.Add("padding-bottom"); s.Add("padding-left");
            s.Add("padding-inline"); s.Add("padding-inline-start"); s.Add("padding-inline-end");
            s.Add("padding-block"); s.Add("padding-block-start"); s.Add("padding-block-end");
            s.Add("border-width"); s.Add("border");
            s.Add("border-top-width"); s.Add("border-right-width"); s.Add("border-bottom-width"); s.Add("border-left-width");
            s.Add("border-top"); s.Add("border-right"); s.Add("border-bottom"); s.Add("border-left");
            s.Add("border-inline"); s.Add("border-inline-start"); s.Add("border-inline-end");
            s.Add("border-block"); s.Add("border-block-start"); s.Add("border-block-end");
            s.Add("border-inline-width"); s.Add("border-inline-start-width"); s.Add("border-inline-end-width");
            s.Add("border-block-width"); s.Add("border-block-start-width"); s.Add("border-block-end-width");
            // border-style "none"/"hidden" zeros the border edge per CSS Box L3 §3.6 — we
            // treat any change to border-style as layout-affecting because it can flip
            // the resolved BorderTop/Right/Bottom/Left between 0 and the declared width.
            s.Add("border-style");
            s.Add("border-top-style"); s.Add("border-right-style"); s.Add("border-bottom-style"); s.Add("border-left-style");
            s.Add("border-inline-style"); s.Add("border-inline-start-style"); s.Add("border-inline-end-style");
            s.Add("border-block-style"); s.Add("border-block-start-style"); s.Add("border-block-end-style");
            s.Add("box-sizing");
            // Display / position
            s.Add("display"); s.Add("position");
            s.Add("top"); s.Add("right"); s.Add("bottom"); s.Add("left");
            s.Add("inset"); s.Add("inset-inline"); s.Add("inset-inline-start"); s.Add("inset-inline-end");
            s.Add("inset-block"); s.Add("inset-block-start"); s.Add("inset-block-end");
            // Inline / typography
            s.Add("font"); s.Add("font-family"); s.Add("font-size"); s.Add("font-weight"); s.Add("font-style");
            s.Add("line-height"); s.Add("letter-spacing"); s.Add("word-spacing");
            s.Add("white-space"); s.Add("word-break"); s.Add("overflow-wrap"); s.Add("word-wrap");
            s.Add("text-wrap"); s.Add("hyphens"); s.Add("tab-size");
            s.Add("text-align"); s.Add("text-align-last"); s.Add("text-indent"); s.Add("direction"); s.Add("writing-mode"); s.Add("unicode-bidi"); s.Add("text-transform");
            // Flex
            s.Add("flex"); s.Add("flex-direction"); s.Add("flex-wrap"); s.Add("flex-basis");
            s.Add("flex-grow"); s.Add("flex-shrink"); s.Add("flex-flow");
            s.Add("justify-content"); s.Add("align-items"); s.Add("align-self"); s.Add("align-content");
            s.Add("gap"); s.Add("row-gap"); s.Add("column-gap"); s.Add("order");
            // Grid
            s.Add("grid"); s.Add("grid-template");
            s.Add("grid-template-columns"); s.Add("grid-template-rows"); s.Add("grid-template-areas");
            s.Add("grid-auto-columns"); s.Add("grid-auto-rows"); s.Add("grid-auto-flow");
            s.Add("grid-column"); s.Add("grid-row");
            s.Add("grid-column-start"); s.Add("grid-column-end");
            s.Add("grid-row-start"); s.Add("grid-row-end"); s.Add("grid-area");
            s.Add("place-items"); s.Add("place-content"); s.Add("place-self");
            s.Add("justify-items"); s.Add("justify-self");
            // Overflow / scroll
            s.Add("overflow"); s.Add("overflow-x"); s.Add("overflow-y"); s.Add("scrollbar-gutter");
            // Anchor positioning resolves at layout time
            s.Add("anchor-name"); s.Add("position-anchor");
            // Containment
            s.Add("container-type"); s.Add("container-name"); s.Add("container"); s.Add("contain");
            // Multi-column layout: count/width change the column geometry; rule properties are paint-only.
            s.Add("column-count"); s.Add("column-width"); s.Add("columns");
            return s;
        }
    }
}
