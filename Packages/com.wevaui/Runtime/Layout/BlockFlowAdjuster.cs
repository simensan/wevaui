using Weva.Css.Cascade;
using Weva.Layout.Boxes;
using Weva.Layout.Positioning;

namespace Weva.Layout {
    internal static class BlockFlowAdjuster {
        public static void PropagateHeightDelta(Box changedBox, double delta, LayoutContext ctx = null) {
            if (changedBox == null) return;
            // Two orders of magnitude below half a CSS px — same band as
            // FlexLayout.FlexWrapEpsilonMinPx. Skip the propagation walk when
            // the height change is layout-noise (jitter from float
            // arithmetic), since a sub-percent-pixel shift cannot move a
            // sibling to a different rasterised row.
            if (delta > -0.01 && delta < 0.01) return;
            if (changedBox is BlockBox changedBlock
                && (changedBlock.Position == PositionType.Absolute || changedBlock.Position == PositionType.Fixed)) {
                return;
            }

            var parent = changedBox.Parent as BlockBox;
            while (parent != null) {
                if (parent is Flex.FlexBox || parent is Grid.GridBox || parent is Tables.TableBox) return;
                // Splice containment (see LayoutContext.HeightPropagationBoundary):
                // never walk out of an incrementally re-laid subtree — its
                // ancestors and their siblings already carry the post-flex
                // heights from the pass that laid THEM, so re-applying the
                // pre-flex → post-flex delta there double-counts, cumulatively
                // per warm-flip frame (content below crept upward until the
                // next full layout).
                if (ctx != null && ReferenceEquals(changedBox, ctx.HeightPropagationBoundary)) return;

                ShiftFollowingSiblings(parent, changedBox, delta);
                if (!HasAutoHeight(parent)) return;

                // An auto-height block grows/shrinks with its content — but
                // min-height / max-height are still hard bounds (CSS Sizing L3
                // §5.2). Without re-clamping here, a flex child shrinking on a
                // later pass propagated the shrink straight through an ancestor
                // sized by `min-height: 100vh`, pulling the page off the bottom
                // of the viewport (load-game's `.screen`). Clamp the new height
                // and carry only the EFFECTIVE delta upward so the rest of the
                // chain (and following siblings of the next ancestor) move by
                // what actually changed, not the pre-clamp amount.
                double newHeight = parent.Height + delta;
                double clamped = ClampToMinMaxHeight(parent, newHeight, ctx);
                double effectiveDelta = clamped - parent.Height;
                parent.Height = clamped;
                if (effectiveDelta > -0.01 && effectiveDelta < 0.01) return;
                delta = effectiveDelta;
                changedBox = parent;
                parent = changedBox.Parent as BlockBox;
            }
        }

        // Re-applies the box's min-height / max-height bounds to a candidate
        // height. ctx is needed to resolve viewport / font-relative units
        // (vh, em, …); when absent (legacy 2-arg callers, e.g. unit tests)
        // the bounds are skipped and the candidate passes through unchanged.
        static double ClampToMinMaxHeight(BlockBox box, double height, LayoutContext ctx) {
            if (ctx == null || box.Style == null) return height;
            double fontSize = StyleResolver.FontSizePx(box.Style, box.Parent?.Style, ctx);
            bool borderBox = box.Style.Get(CssProperties.BoxSizingId) == "border-box";
            double frame = box.PaddingTop + box.PaddingBottom + box.BorderTop + box.BorderBottom;

            var maxParsed = box.Style.GetParsed(CssProperties.MaxHeightId);
            var maxR = StyleResolver.ResolveLengthFromParsed(maxParsed, ctx, fontSize, null);
            if (maxR.Kind == StyleResolver.LengthKind.Length) {
                double maxPx = borderBox ? maxR.Pixels : maxR.Pixels + frame;
                if (height > maxPx) height = maxPx;
            }
            var minParsed = box.Style.GetParsed(CssProperties.MinHeightId);
            var minR = StyleResolver.ResolveLengthFromParsed(minParsed, ctx, fontSize, null);
            if (minR.Kind == StyleResolver.LengthKind.Length) {
                double minPx = borderBox ? minR.Pixels : minR.Pixels + frame;
                if (height < minPx) height = minPx;
            }
            return height;
        }

        static void ShiftFollowingSiblings(BlockBox parent, Box changedBox, double delta) {
            var siblings = parent.Children;
            int idx = -1;
            for (int i = 0; i < siblings.Count; i++) {
                if (ReferenceEquals(siblings[i], changedBox)) { idx = i; break; }
            }
            if (idx < 0) return;

            for (int i = idx + 1; i < siblings.Count; i++) {
                var sib = siblings[i];
                if (sib is BlockBox sbb) {
                    if (sbb.Position == PositionType.Absolute || sbb.Position == PositionType.Fixed) continue;
                }
                sib.Y += delta;
            }
        }

        static bool HasAutoHeight(BlockBox box) {
            if (box.Style == null) return true;
            string h = box.Style.Get(CssProperties.HeightId);
            return string.IsNullOrEmpty(h) || h == "auto";
        }
    }
}
