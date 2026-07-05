// MulticolLayout — post-pass formatting context for CSS Multi-column containers.
//
// Dispatch site: BoxBuilder constructs a MulticolBox for elements whose computed
// column-count or column-width is non-auto.  LayoutEngine runs RunMulticolPasses
// after BlockLayout (same pattern as FlexLayout / GridLayout).
//
// Algorithm: CSS Multi-column Layout L1 §6 (simplified v1).
//
// v1 scope (documented in CSS_OPEN_GAPS.md C2):
//   - column-count, column-width, columns shorthand, column-gap (shared).
//   - column-rule-width/style/color + column-rule shorthand (paint only).
//   - column-fill: balance (default) for auto-height containers.
//   - Explicit container height → sequential fill, no balancing.
//   - Fragmentation granularity: WHOLE block children move between columns.
//     Chrome slices single blocks across column boundaries — v1 does not.
//     If a single block child is taller than the column, it stays in that
//     column and the column grows to fit (overflow in block direction).
//   - No column-span, no forced break properties (break-before/after/inside),
//     no margin collapsing across column boundaries.
//   - Nested multicol: supported (outer distributes children; inner runs
//     its own post-pass normally).
//
// Divergence from Chrome (documented):
//   1. Fragmentation is whole-child only — Chrome slices individual blocks.
//   2. Overflow in the block direction: when a child is taller than the
//      balanced column height, v1 keeps it in that column and lets the
//      column overflow downward (Chrome slices the overflowing block).
//   3. Margin collapsing across column boundaries: not performed (v1).

using System;
using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Layout.Boxes;

namespace Weva.Layout.Multicol {
    internal sealed class MulticolLayout {
        // Pass-reuse: one instance per LayoutEngine, ctx refreshed per pass.
        LayoutContext ctx;
        // Re-use to lay out children at a synthetic column width before
        // distributing them.  Wired by LayoutEngine after construction.
        BlockLayout blockLayout;

        public MulticolLayout() { }

        public void Reset(LayoutContext ctx) {
            this.ctx = ctx;
        }

        public void SetBlockLayout(BlockLayout bl) {
            this.blockLayout = bl;
        }

        // -----------------------------------------------------------------------
        // Entry point called by LayoutEngine.RunMulticolPasses for every
        // MulticolBox in the tree (depth-first post-order, leaves first).
        // -----------------------------------------------------------------------
        public void Layout(MulticolBox box) {
            if (box == null || box.Style == null) return;

            // Capture the height BlockLayout assigned so we can compute the
            // delta after FinalizeContainerHeight and propagate it to following
            // siblings (mirrors FlexLayout.ShiftFollowingSiblingsIfHeightChanged
            // and GridLayout.ShiftFollowingSiblingsIfHeightChanged).  For an
            // auto-height multicol container BlockLayout stacks all children at
            // full height before distribution — the balanced height is always
            // smaller, producing a negative delta that must shift siblings up.
            double preLayoutHeight = box.Height;

            // 1. Resolve used column-count / column-width per CSS Multicol §6.
            double availableWidth = box.ContentWidth;
            // Em-bearing values (column-width, column-gap, the `normal` gap)
            // resolve against the ELEMENT's font size per CSS Values §5.1.1.
            double fs = StyleResolver.FontSizePx(box.Style, box.Parent?.Style, ctx);
            var lctx = new LengthContext {
                BaseFontSizePx = fs,
                BasisPixels = availableWidth,
                ViewportWidthPx = ctx.ViewportWidthPx,
                ViewportHeightPx = ctx.ViewportHeightPx
            };

            // column-gap resolves against the container inline axis.
            double gap = ResolveGap(box.Style, lctx, fs);

            int usedCount;
            double usedWidth;
            ResolveColumnGeometry(box.Style, availableWidth, gap, lctx, out usedCount, out usedWidth);
            if (usedCount <= 0) usedCount = 1;
            if (usedWidth < 0) usedWidth = 0;

            box.UsedColumnCount = usedCount;
            box.UsedColumnWidth = usedWidth;
            box.UsedGap = gap;

            // 2. Re-flow children at usedWidth so their heights reflect the
            //    actual column width (BlockLayout ran at the full container width).
            ReflowChildrenAtColumnWidth(box, usedWidth);

            // 3. Distribute children into columns.
            //    Balanced height: totalContentHeight / usedCount (rounded up),
            //    then iterate until everything fits.
            bool hasExplicitHeight = HasExplicitHeight(box.Style, lctx);
            double colHeight;
            if (hasExplicitHeight && box.Height > 0) {
                // Sequential fill: columns have a fixed height = container content height.
                colHeight = box.Height - box.PaddingTop - box.PaddingBottom - box.BorderTop - box.BorderBottom;
                if (colHeight < 0) colHeight = 0;
                // Place children into columns but do NOT call FinalizeContainerHeight:
                // BlockLayout already set box.Height from the explicit CSS value, and
                // FinalizeContainerHeight would incorrectly shrink it to the tallest-column
                // content height (losing any empty space at the column bottom).
                var children = CollectInFlowChildren(box);
                if (children.Count == 0) {
                    FinalizeEmpty(box, usedCount, usedWidth, gap);
                } else {
                    TryDistribute(box, children, usedCount, usedWidth, gap, colHeight, out _);
                    // Column heights are recorded but the container height is kept
                    // at the BlockLayout-assigned explicit value (no FinalizeContainerHeight).
                }
                // Record the column height limit for paint-level fragmentation.
                box.UsedColumnHeight = colHeight;
            } else {
                DistributeBalanced(box, usedCount, usedWidth, gap);
                // UsedColumnHeight is set inside DistributeBalanced after convergence.
            }

            // 4. Propagate the height delta (post-balance minus pre-balance) to
            //    following siblings and auto-height ancestors, exactly as the
            //    flex/grid post-passes do via BlockFlowAdjuster.PropagateHeightDelta
            //    (CSS Multi-column L1 / CSS Sizing L3 §5.2; MULTICOL-PARENT-REFLOW).
            //    For auto-height containers the balanced height is smaller than the
            //    BlockLayout estimate (stacked full heights), so delta < 0 and
            //    siblings shift upward.  For explicit-height containers this path
            //    is a no-op (delta == 0) because we preserve the BlockLayout height.
            double delta = box.Height - preLayoutHeight;
            if (delta > 0.01 || delta < -0.01) {
                BlockFlowAdjuster.PropagateHeightDelta(box, delta, ctx);
            }
        }

        // -----------------------------------------------------------------------
        // CSS Multicol §3.3–3.5: resolve used column-count and column-width.
        // -----------------------------------------------------------------------
        static void ResolveColumnGeometry(
            ComputedStyle style, double availableWidth, double gap,
            LengthContext lctx,
            out int usedCount, out double usedWidth)
        {
            // Read declared values.
            int declaredCount = ReadColumnCount(style);        // -1 = auto
            double declaredWidth = ReadColumnWidth(style, lctx); // -1 = auto

            // CSS Multicol §3.4 algorithm:
            // Case A: both auto → single column at available width.
            if (declaredCount < 0 && declaredWidth < 0) {
                usedCount = 1;
                usedWidth = availableWidth;
                return;
            }
            // Case B: count specified, width auto.
            if (declaredCount >= 1 && declaredWidth < 0) {
                usedCount = declaredCount;
                usedWidth = Math.Max(0, (availableWidth - gap * (usedCount - 1)) / usedCount);
                return;
            }
            // Case C: width specified, count auto.
            if (declaredWidth >= 0 && declaredCount < 0) {
                if (availableWidth <= 0 || declaredWidth <= 0) {
                    usedCount = 1;
                    usedWidth = availableWidth;
                    return;
                }
                // N = floor((W + gap) / (colWidth + gap)), clamped to [1, ∞).
                usedCount = (int)Math.Floor((availableWidth + gap) / (declaredWidth + gap));
                if (usedCount < 1) usedCount = 1;
                usedWidth = Math.Max(0, (availableWidth - gap * (usedCount - 1)) / usedCount);
                return;
            }
            // Case D: both specified → count caps the column count, width is advisory.
            {
                // Start from the declared width.
                if (declaredWidth > 0 && availableWidth > 0) {
                    int countFromWidth = (int)Math.Floor((availableWidth + gap) / (declaredWidth + gap));
                    if (countFromWidth < 1) countFromWidth = 1;
                    usedCount = Math.Min(declaredCount, countFromWidth);
                } else {
                    usedCount = declaredCount;
                }
                if (usedCount < 1) usedCount = 1;
                usedWidth = Math.Max(0, (availableWidth - gap * (usedCount - 1)) / usedCount);
            }
        }

        // -----------------------------------------------------------------------
        // Read declared column-count (returns -1 for auto / missing).
        // -----------------------------------------------------------------------
        static int ReadColumnCount(ComputedStyle style) {
            string raw = style.Get(CssProperties.ColumnCountId);
            if (string.IsNullOrEmpty(raw) || raw == "auto") return -1;
            if (int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int n) && n >= 1) return n;
            return -1;
        }

        // -----------------------------------------------------------------------
        // Read declared column-width in px (returns -1 for auto / missing).
        // -----------------------------------------------------------------------
        static double ReadColumnWidth(ComputedStyle style, LengthContext lctx) {
            var parsed = style.GetParsed(CssProperties.ColumnWidthId);
            if (parsed == null) {
                string raw = style.Get(CssProperties.ColumnWidthId);
                if (string.IsNullOrEmpty(raw) || raw == "auto") return -1;
                return -1;
            }
            if (parsed is CssKeyword kw && kw.Identifier == "auto") return -1;
            if (parsed is CssIdentifier id && id.Name == "auto") return -1;
            if (parsed is CssLength len) return Math.Max(0, len.ToPixels(lctx));
            if (parsed is CssNumber num) return Math.Max(0, num.Value);
            return -1;
        }

        // -----------------------------------------------------------------------
        // Read column-gap (resolves against inline axis = container width).
        // Falls through to the shared gap/column-gap property chain.
        // -----------------------------------------------------------------------
        static double ResolveGap(ComputedStyle style, LengthContext lctx, double fontSizePx) {
            // column-gap longhand has priority; fall back to the gap shorthand.
            double g = TryResolveGapProp(style, CssProperties.ColumnGapId, lctx);
            if (g >= 0) return g;
            g = TryResolveGapProp(style, CssProperties.GapId, lctx);
            if (g >= 0) return g;
            // CSS Multicol §3.6 / css-align-3 §8.4: `column-gap: normal`
            // computes to 1em in multicol context — Chrome behavior.
            return fontSizePx;
        }

        static double TryResolveGapProp(ComputedStyle style, int propId, LengthContext lctx) {
            var parsed = style.GetParsed(propId);
            if (parsed == null) {
                string raw = style.Get(propId);
                if (string.IsNullOrEmpty(raw) || raw == "normal") return -1;
                if (!CssValue.TryParse(raw, out parsed)) return -1;
                if (parsed == null) return -1;
            }
            if (parsed is CssKeyword k && k.Identifier == "normal") return -1;
            if (parsed is CssLength len) return Math.Max(0, len.ToPixels(lctx));
            if (parsed is CssNumber num) return Math.Max(0, num.Value);
            if (parsed is CssPercentage pct) {
                if (!lctx.BasisPixels.HasValue) return 0;
                return Math.Max(0, lctx.BasisPixels.Value * pct.Value * 0.01);
            }
            return -1;
        }

        // -----------------------------------------------------------------------
        // Re-flow in-flow block children at the column content width.
        // -----------------------------------------------------------------------
        void ReflowChildrenAtColumnWidth(MulticolBox box, double colWidth) {
            if (blockLayout == null) return;
            var children = box.ChildList;
            for (int i = 0; i < children.Count; i++) {
                if (children[i] is BlockBox cb && IsInFlowBlock(cb)) {
                    blockLayout.RelayoutContentAt(cb, colWidth);
                }
            }
        }

        static bool IsInFlowBlock(BlockBox cb) {
            if (cb == null) return false;
            if (cb.Position == Positioning.PositionType.Absolute
                || cb.Position == Positioning.PositionType.Fixed) return false;
            if (cb.IsFloat) return false;
            return true;
        }

        // -----------------------------------------------------------------------
        // Balanced distribution: iterate column height until all children fit.
        // v1: whole-child granularity only.
        // -----------------------------------------------------------------------
        void DistributeBalanced(MulticolBox box, int N, double colWidth, double gap) {
            var children = CollectInFlowChildren(box);
            if (children.Count == 0) {
                FinalizeEmpty(box, N, colWidth, gap);
                return;
            }

            // Initial guess: totalHeight / N, rounded up.
            double totalHeight = 0;
            for (int i = 0; i < children.Count; i++) {
                var cb = children[i];
                totalHeight += cb.MarginTop + cb.Height + cb.MarginBottom;
            }
            double guess = Math.Ceiling(totalHeight / Math.Max(1, N));
            if (guess < 1) guess = 1;

            // Iterate: grow the column height until nothing overflows.
            const int MaxIter = 32;
            double colHeight = guess;
            for (int iter = 0; iter < MaxIter; iter++) {
                bool fits = TryDistribute(box, children, N, colWidth, gap, colHeight, out double actualMax);
                if (fits) break;
                // Grow: use the overflow height as the new column height.
                double newHeight = Math.Max(colHeight + 1, actualMax);
                if (newHeight <= colHeight) break; // converged
                colHeight = newHeight;
            }
            // Final pass to actually write positions.
            TryDistribute(box, children, N, colWidth, gap, colHeight, out _);
            FinalizeContainerHeight(box, N);
            // Record the converged column height for paint-level fragmentation
            // (MULTICOL-FRAG v1). The balanced colHeight is the limit each column
            // was allowed to grow to; ColumnHeights[] stores actual filled heights
            // (≤ colHeight after convergence when whole-child granularity fits).
            box.UsedColumnHeight = colHeight;
        }

        // -----------------------------------------------------------------------
        // Sequential fill: columns have a fixed height; overflow goes inline.
        // v1: whole-child granularity only.
        // -----------------------------------------------------------------------
        void DistributeSequential(MulticolBox box, int N, double colWidth, double gap, double colHeight) {
            var children = CollectInFlowChildren(box);
            if (children.Count == 0) {
                FinalizeEmpty(box, N, colWidth, gap);
                return;
            }
            TryDistribute(box, children, N, colWidth, gap, colHeight, out _);
            FinalizeContainerHeight(box, N);
        }

        // -----------------------------------------------------------------------
        // Place children into columns by setting their X/Y.
        // Returns true when everything fit without the tallest column exceeding
        // colHeight.  Returns the max column height in actualMax.
        // -----------------------------------------------------------------------
        bool TryDistribute(MulticolBox box, List<BlockBox> children, int N,
            double colWidth, double gap, double colHeight,
            out double actualMax)
        {
            double top = box.PaddingTop + box.BorderTop;
            double left = box.PaddingLeft + box.BorderLeft;

            if (box.ColumnHeights == null || box.ColumnHeights.Length != N) {
                box.ColumnHeights = new double[N];
            }
            var colCursors = new double[N]; // cursor within each column
            var colIndices = new int[N];    // child count per column (unused but useful for debug)

            int col = 0;
            bool fits = true;
            for (int i = 0; i < children.Count; i++) {
                var cb = children[i];
                double childMarginBox = cb.MarginTop + cb.Height + cb.MarginBottom;

                // If adding this child to the current column would exceed colHeight,
                // move to the next column — unless we're already on the last column
                // or the cursor is 0 (child won't fit anywhere smaller).
                if (col < N - 1 && colCursors[col] > 0
                    && colCursors[col] + childMarginBox > colHeight + LayoutEpsilons.SubPixelEqual) {
                    col++;
                }

                // Place the child in the current column.
                cb.X = left + col * (colWidth + gap) + cb.MarginLeft;
                cb.Y = top + colCursors[col] + cb.MarginTop;

                colCursors[col] += childMarginBox;
                if (colCursors[col] > colHeight + LayoutEpsilons.SubPixelEqual) {
                    fits = false; // overflow in block direction
                }
            }

            for (int c = 0; c < N; c++) box.ColumnHeights[c] = colCursors[c];
            actualMax = 0;
            for (int c = 0; c < N; c++) if (colCursors[c] > actualMax) actualMax = colCursors[c];
            return fits;
        }

        void FinalizeContainerHeight(MulticolBox box, int N) {
            // Container height = tallest column + vertical frame.
            double max = 0;
            if (box.ColumnHeights != null) {
                for (int c = 0; c < box.ColumnHeights.Length; c++) {
                    if (box.ColumnHeights[c] > max) max = box.ColumnHeights[c];
                }
            }
            double frame = box.PaddingTop + box.PaddingBottom + box.BorderTop + box.BorderBottom;
            box.Height = max + frame;
        }

        void FinalizeEmpty(MulticolBox box, int N, double colWidth, double gap) {
            box.ColumnHeights = new double[N];
            double frame = box.PaddingTop + box.PaddingBottom + box.BorderTop + box.BorderBottom;
            box.Height = frame;
        }

        // -----------------------------------------------------------------------
        // Collect in-flow block children (skip OOF, floats).
        // -----------------------------------------------------------------------
        static List<BlockBox> CollectInFlowChildren(MulticolBox box) {
            var result = new List<BlockBox>(box.ChildList.Count);
            var children = box.ChildList;
            for (int i = 0; i < children.Count; i++) {
                if (children[i] is BlockBox cb && IsInFlowBlock(cb)) {
                    result.Add(cb);
                }
            }
            return result;
        }

        // -----------------------------------------------------------------------
        // Check whether the container has an explicit height.
        // -----------------------------------------------------------------------
        static bool HasExplicitHeight(ComputedStyle style, LengthContext lctx) {
            if (style == null) return false;
            var parsed = style.GetParsed(CssProperties.HeightId);
            if (parsed == null) {
                string raw = style.Get(CssProperties.HeightId);
                if (string.IsNullOrEmpty(raw) || raw == "auto") return false;
                return true; // e.g. "100px" stored as a raw string
            }
            if (parsed is CssKeyword k && k.Identifier == "auto") return false;
            if (parsed is CssIdentifier id && id.Name == "auto") return false;
            return true;
        }
    }
}
