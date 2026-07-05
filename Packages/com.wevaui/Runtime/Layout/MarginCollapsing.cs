using Weva.Css.Cascade;
using Weva.Layout.Boxes;
using Weva.Layout.Containment;

namespace Weva.Layout {
    // CSS Box Model Level 3 §8.3.1: vertical margins of adjacent in-flow block
    // siblings collapse to the largest of:
    //   both positive  -> max(a, b)
    //   both negative  -> min(a, b)        (most negative wins)
    //   mixed sign     -> a + b            (algebraic sum)
    //
    // Inline-block / floats / absolute / inline-flex / inline-grid do NOT
    // participate in collapsing. Their margins are kept verbatim. We model that
    // by skipping them when walking siblings and treating them as a barrier.
    internal static class MarginCollapsing {
        public static double Collapse(double a, double b) {
            // L14: a NaN margin (bad calc(), a NaN-producing animated length)
            // fails both sign tests below and falls through to `a + b`, which
            // is also NaN — and that NaN then propagates through the entire
            // collapse chain, corrupting every downstream block's position.
            // Treat a NaN input as absent (0): Collapse(0, x) == x, so we just
            // return the finite operand (or 0 when both are NaN). Finite
            // inputs are unaffected — these branches never fire for them.
            if (double.IsNaN(a)) return double.IsNaN(b) ? 0.0 : b;
            if (double.IsNaN(b)) return a;
            if (a >= 0 && b >= 0) return a > b ? a : b;
            if (a <= 0 && b <= 0) return a < b ? a : b;
            return a + b;
        }

        // True for boxes whose top/bottom margins collapse with adjacent block
        // siblings. Inline-block, absolutely-positioned, fixed, and floats are
        // excluded. AnonymousBlockBox participates so collapsing crosses
        // anonymous-block boundaries (per spec — though our anonymous blocks have
        // zero margin so they collapse trivially).
        public static bool ParticipatesInFlow(BlockBox box) {
            if (box == null) return false;
            if (box.IsInlineBlock) return false;
            // CSS 2.1 §8.3.1 rule 5: a float's margins do not collapse
            // with any other margins (its margins are kept verbatim and
            // it doesn't participate in adjacent siblings' collapse chain).
            if (box.IsFloat) return false;
            if (IsOutOfFlow(box)) return false;
            return true;
        }

        public static bool IsOutOfFlow(BlockBox box) {
            if (box.Style == null) return false;
            string pos = box.Style.Get(CssProperties.PositionId);
            if (string.IsNullOrEmpty(pos)) return false;
            return pos == "absolute" || pos == "fixed";
        }

        // CSS 2.1 §9.4.1 / CSS Display L3 / CSS Positioning: a box establishes
        // a new block formatting context when:
        //   * `overflow` is anything other than `visible`
        //   * `display` is one of `flow-root`, `flex`, `inline-flex`, `grid`,
        //     `inline-grid`, `inline-block`, `table`, `inline-table`,
        //     `table-cell`, `table-caption`
        //   * `position` is `absolute` or `fixed`
        //   * `float` is `left` or `right`
        // The BFC root's own margins do NOT collapse with its in-flow children
        // — neither at the top nor at the bottom.
        public static bool EstablishesNewBfc(BlockBox box) {
            if (box == null || box.Style == null) return false;
            // The `overflow` shorthand expands to `overflow-x` / `overflow-y`
            // (see OverflowShorthandExpander), so the longhand `overflow` slot
            // typically holds its initial value `visible` even when the author
            // wrote `overflow: hidden`. Check both axis longhands.
            string ox = box.Style.Get(CssProperties.OverflowXId);
            if (!string.IsNullOrEmpty(ox) && ox != "visible") return true;
            string oy = box.Style.Get(CssProperties.OverflowYId);
            if (!string.IsNullOrEmpty(oy) && oy != "visible") return true;
            string display = box.Style.Get(CssProperties.DisplayId);
            switch (display) {
                case "flow-root":
                case "flex":
                case "inline-flex":
                case "grid":
                case "inline-grid":
                case "inline-block":
                case "table":
                case "inline-table":
                case "table-cell":
                case "table-caption":
                    return true;
            }
            string pos = box.Style.Get(CssProperties.PositionId);
            if (pos == "absolute" || pos == "fixed") return true;
            string flt = box.Style.Get(CssProperties.FloatId);
            if (flt == "left" || flt == "right" || flt == "inline-start" || flt == "inline-end") return true;
            // CSS Containment L2 §3.1 / §3.2: `contain: layout` and
            // `contain: paint` (and their shorthands `strict` / `content`)
            // establish an independent formatting context — which is the
            // same as a new BFC for block-layout purposes.  This suppresses
            // margin-collapsing across the containment boundary (parent-child
            // top/bottom collapse) per the spec's "independent formatting
            // context" requirement, without regressing NegativeMarginTests.
            if (ContainmentResolver.HasLayout(box.Style) || ContainmentResolver.HasPaint(box.Style)) return true;
            return false;
        }

        // True when the box has no top padding and no top border, allowing the
        // first-child's margin-top to collapse through to its parent. A new
        // block formatting context (overflow != visible / flow-root / etc.)
        // closes the top regardless of padding/border per CSS 2.1 §8.3.1.
        //
        // CSS 2.1 §8.3.1: an explicit `height` on the parent does NOT prevent
        // parent-child top-margin collapsing. Only border/padding between the
        // parent's top edge and its first in-flow child, or a BFC-establishing
        // box, closes the top. An explicit height only prevents BOTTOM-margin
        // collapsing (handled by ParentBottomOpen → ParentHeightAuto). A prior
        // version incorrectly returned false for height != "auto", which blocked
        // collapse through `position:relative` containers with a set height —
        // fixed as MARGINCOLAPSE-RELATIVE (CSS 2.1 §8.3.1 rule 1).
        public static bool ParentTopOpen(BlockBox parent) {
            if (EstablishesNewBfc(parent)) return false;
            return parent.PaddingTop <= 0 && parent.BorderTop <= 0;
        }

        public static bool ParentBottomOpen(BlockBox parent) {
            if (EstablishesNewBfc(parent)) return false;
            return parent.PaddingBottom <= 0 && parent.BorderBottom <= 0;
        }

        // True when the parent has no explicit height/min-height that would
        // prevent its bottom margin from collapsing with the last child's.
        // The min-height check used to be a string compare against "0"/"auto"
        // which silently treated `min-height: 1px` as auto and let bottom
        // collapse fire incorrectly. Route through CssLength so any explicit
        // positive length blocks the collapse.
        public static bool ParentHeightAuto(BlockBox parent, LayoutContext ctx, double fontSize) {
            if (parent.Style == null) return true;
            string h = parent.Style.Get(CssProperties.HeightId);
            if (!string.IsNullOrEmpty(h) && h != "auto") {
                var hr = StyleResolver.ResolveLength(h, parent.Style, ctx, fontSize, null);
                if (hr.Kind == StyleResolver.LengthKind.Length && hr.Pixels > 0) return false;
                if (hr.Kind == StyleResolver.LengthKind.Percent) return false;
            }
            string minH = parent.Style.Get(CssProperties.MinHeightId);
            if (!string.IsNullOrEmpty(minH) && minH != "auto") {
                var mr = StyleResolver.ResolveLength(minH, parent.Style, ctx, fontSize, null);
                if (mr.Kind == StyleResolver.LengthKind.Length && mr.Pixels > 0) return false;
                if (mr.Kind == StyleResolver.LengthKind.Percent && mr.Percent > 0) return false;
            }
            return true;
        }

        // Self-collapsing: a block whose own top and bottom margins collapse
        // with each other when it has no padding, border, height, min-height,
        // or in-flow content.
        public static bool IsSelfCollapsing(BlockBox box, LayoutContext ctx, double fontSize) {
            if (box.PaddingTop > 0 || box.PaddingBottom > 0) return false;
            if (box.BorderTop > 0 || box.BorderBottom > 0) return false;
            if (box.Style != null) {
                string h = box.Style.Get(CssProperties.HeightId);
                if (!string.IsNullOrEmpty(h) && h != "auto" && h != "0") {
                    var hr = StyleResolver.ResolveLength(h, box.Style, ctx, fontSize, null);
                    if (hr.Kind == StyleResolver.LengthKind.Length && hr.Pixels > 0) return false;
                    if (hr.Kind == StyleResolver.LengthKind.Percent && hr.Percent > 0) return false;
                }
                string minH = box.Style.Get(CssProperties.MinHeightId);
                if (!string.IsNullOrEmpty(minH) && minH != "auto" && minH != "0") {
                    var mr = StyleResolver.ResolveLength(minH, box.Style, ctx, fontSize, null);
                    if (mr.Kind == StyleResolver.LengthKind.Length && mr.Pixels > 0) return false;
                    if (mr.Kind == StyleResolver.LengthKind.Percent && mr.Percent > 0) return false;
                }
            }
            // Has any non-empty in-flow content?
            foreach (var c in box.Children) {
                if (c is BlockBox bb && !ParticipatesInFlow(bb)) continue;
                return false;
            }
            return true;
        }
    }
}
