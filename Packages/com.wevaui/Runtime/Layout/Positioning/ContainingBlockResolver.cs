using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Layout.Boxes;

namespace Weva.Layout.Positioning {
    internal static class ContainingBlockResolver {
        public readonly struct ContainingBlock {
            public readonly Box Box;
            public readonly double X;
            public readonly double Y;
            public readonly double Width;
            public readonly double Height;
            public readonly bool IsViewport;

            public ContainingBlock(Box box, double x, double y, double w, double h, bool isViewport) {
                Box = box;
                X = x;
                Y = y;
                Width = w;
                Height = h;
                IsViewport = isViewport;
            }
        }

        // Per CSS Positioned Layout Module Level 3, the containing block for an
        // `absolute`-positioned box is the PADDING box of its nearest positioned
        // ancestor (i.e., inside the border edge). We subtract the ancestor's
        // border widths from the border-box rect so `inset: 0` and percent-based
        // top/left/right/bottom resolve relative to the padding edge — matching
        // Chrome's behaviour for cases like `.panel { border: 1px } .child {
        // position: absolute; inset: 0 }`, where the child should be 2px smaller
        // than the bordered ancestor.
        //
        // CSS Grid L1 §9 (E7): an abs-pos child of a grid container whose
        // grid-placement properties resolve to a definite grid area uses
        // THAT grid area as its containing block — overriding the padding-
        // edge fallback. GridLayout stamps the area's offset (relative to
        // the grid container's border-box) and size onto the box; if the
        // stamp is present AND the direct parent is the grid container,
        // return the grid-area rect instead of walking up to the nearest
        // positioned ancestor.
        public static ContainingBlock ResolveAbsolute(Box box, LayoutContext ctx) {
            if (box is Weva.Layout.Boxes.BlockBox bbCb
                && bbCb.HasGridAreaContainingBlock
                && box.Parent is Weva.Layout.Grid.GridBox gridParent) {
                var (gax, gay) = AbsolutePosition(gridParent);
                double areaX = gax + bbCb.GridAreaContainingBlockOffsetX;
                double areaY = gay + bbCb.GridAreaContainingBlockOffsetY;
                double areaW = bbCb.GridAreaContainingBlockWidth;
                double areaH = bbCb.GridAreaContainingBlockHeight;
                if (areaW < 0) areaW = 0;
                if (areaH < 0) areaH = 0;
                return new ContainingBlock(gridParent, areaX, areaY, areaW, areaH, false);
            }
            for (var p = box.Parent; p != null; p = p.Parent) {
                if (EstablishesAbsoluteContainingBlock(p)) {
                    var (ax, ay) = AbsolutePosition(p);
                    double padX = ax + p.BorderLeft;
                    double padY = ay + p.BorderTop;
                    double padW = p.Width - p.BorderLeft - p.BorderRight;
                    double padH = p.Height - p.BorderTop - p.BorderBottom;
                    if (padW < 0) padW = 0;
                    if (padH < 0) padH = 0;
                    return new ContainingBlock(p, padX, padY, padW, padH, false);
                }
            }
            return Viewport(ctx);
        }

        public static ContainingBlock ResolveFixed(Box box, LayoutContext ctx) {
            // CSS Transforms L1 §6.1: a `transform` / `filter` / `perspective`
            // ancestor (and `will-change: transform`, `contain: paint`, ...)
            // ESCAPES position:fixed from the viewport — those ancestors
            // become the containing block for fixed descendants too. Walk up
            // looking for one of those triggers; fall back to the viewport.
            for (var p = box?.Parent; p != null; p = p.Parent) {
                if (EstablishesFixedContainingBlock(p)) {
                    var (ax, ay) = AbsolutePosition(p);
                    double padX = ax + p.BorderLeft;
                    double padY = ay + p.BorderTop;
                    double padW = p.Width - p.BorderLeft - p.BorderRight;
                    double padH = p.Height - p.BorderTop - p.BorderBottom;
                    if (padW < 0) padW = 0;
                    if (padH < 0) padH = 0;
                    return new ContainingBlock(p, padX, padY, padW, padH, false);
                }
            }
            return Viewport(ctx);
        }

        // Existing call site (other parts of the layout pipeline) without a
        // box reference. Falls back to viewport unconditionally — preserved
        // for compatibility but new callers should pass the box.
        public static ContainingBlock ResolveFixed(LayoutContext ctx) {
            return Viewport(ctx);
        }

        // Per CSS Positioned Layout L3 §4.3 + CSS Transforms L1 §6.1:
        // a containing block for `position: absolute` descendants is
        // established by either a positioned ancestor (relative/absolute/
        // fixed/sticky), OR an ancestor with a transform / filter /
        // perspective / will-change: transform / contain: paint, even if
        // that ancestor is otherwise position:static. The previous
        // implementation only consulted `IsPositioned()`, so an abspos
        // descendant of `.card { transform: scale(1) }` resolved its CB
        // to the viewport — `inset: 0` filled the screen instead of the
        // card.
        static bool EstablishesAbsoluteContainingBlock(Box p) {
            if (p == null) return false;
            if (p.IsPositioned()) return true;
            return HasContainingBlockEstablishingProperty(p);
        }

        // `position: fixed` is normally containing-block'd by the viewport,
        // but the same set of properties also captures it (the spec calls
        // this out explicitly — a transformed ancestor is the CB for both
        // absolute and fixed descendants because the transform changes how
        // viewport coordinates map to local coordinates).
        static bool EstablishesFixedContainingBlock(Box p) {
            return HasContainingBlockEstablishingProperty(p);
        }

        static bool HasContainingBlockEstablishingProperty(Box p) {
            var style = p?.Style;
            if (style == null) return false;
            string transform = style.Get(CssProperties.TransformId);
            if (!string.IsNullOrEmpty(transform) && !CssStringUtil.EqualsIgnoreCaseTrimmed(transform, "none")) return true;
            string filter = style.Get(CssProperties.FilterId);
            if (!string.IsNullOrEmpty(filter) && !CssStringUtil.EqualsIgnoreCaseTrimmed(filter, "none")) return true;
            string perspective = style.Get(CssProperties.PerspectiveId);
            if (!string.IsNullOrEmpty(perspective) && !CssStringUtil.EqualsIgnoreCaseTrimmed(perspective, "none")) return true;
            string willChange = style.Get(CssProperties.WillChangeId);
            if (!string.IsNullOrEmpty(willChange)
                && (HasToken(willChange, "transform") || HasToken(willChange, "filter") || HasToken(willChange, "perspective"))) return true;
            string contain = style.Get(CssProperties.ContainId);
            if (!string.IsNullOrEmpty(contain)
                && (HasToken(contain, "layout") || HasToken(contain, "paint")
                    || HasToken(contain, "strict") || HasToken(contain, "content"))) return true;
            return false;
        }

        // Whitespace-or-comma token match without case-folding allocation.
        // Mirrors PositionedExtensions.HasTokenIgnoreCase but kept local so
        // ContainingBlockResolver doesn't depend on its sibling file's
        // private helpers.
        static bool HasToken(string value, string token) {
            int idx = 0;
            while (idx < value.Length) {
                while (idx < value.Length && (value[idx] == ' ' || value[idx] == ',' || value[idx] == '\t')) idx++;
                int start = idx;
                while (idx < value.Length && value[idx] != ' ' && value[idx] != ',' && value[idx] != '\t') idx++;
                int len = idx - start;
                if (len != token.Length) continue;
                bool eq = true;
                for (int j = 0; j < len; j++) {
                    char a = value[start + j];
                    if (a >= 'A' && a <= 'Z') a = (char)(a + 32);
                    if (a != token[j]) { eq = false; break; }
                }
                if (eq) return true;
            }
            return false;
        }

        public static ContainingBlock Viewport(LayoutContext ctx) {
            return new ContainingBlock(null, 0, 0, ctx.ViewportWidthPx, ctx.ViewportHeightPx, true);
        }

        // Sums local-to-parent X/Y from `box` up the tree, INCLUDING box.X/box.Y,
        // so the returned pair is `box`'s own absolute (root-relative) origin.
        // Use AbsolutePositionOfParent if you want the parent's origin (which
        // is what `inset` resolution wants for a fresh box).
        public static (double x, double y) AbsolutePosition(Box box) {
            double x = 0, y = 0;
            for (var b = box; b != null; b = b.Parent) {
                x += b.X;
                y += b.Y;
            }
            return (x, y);
        }

        public static (double x, double y) AbsolutePositionOfParent(Box box) {
            if (box?.Parent == null) return (0, 0);
            return AbsolutePosition(box.Parent);
        }
    }
}
