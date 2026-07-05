using Weva.Layout.Boxes;
using Weva.Layout.Positioning;

namespace Weva.Layout.Scrolling {
    // Resolves CSS `position: sticky` offsets against the current scroll position
    // of the nearest non-`overflow: visible` ancestor. Per the CSS Positioned
    // Layout Module Level 3 §6.3 sticky algorithm, a sticky element behaves like
    // `relative` until its bounds are about to leave the scroll container's
    // viewport edge, at which point it "pins" to that edge for as long as its
    // containing block can carry it. Once the containing block has scrolled past
    // the pin point, the element resumes scrolling with its container.
    //
    // The CSS spec defines a per-axis algorithm. For the top edge:
    //   inset_top = max(specifiedTop - scrollTop, 0)
    //   stickyShift = inset_top - (elementNaturalTopRelativeToContainer)
    // and clamped so the sticky element never leaves its containing block.
    //
    // We compute the shift in two passes (one per axis, top/bottom and
    // left/right) and write it onto Box.StickyOffsetX/Y for the paint
    // converter to apply at emit time. Layout coordinates are NOT mutated
    // here — Box.X/Y still represent the natural in-flow position so that
    // sticky behaves like relative when no scroll movement has happened.
    //
    // Single-axis simplification (v1): top OR bottom — when both are set, top
    // wins. Same for left/right. This matches modern engines' "the second pin
    // becomes a constraint" treatment when the element is shorter than the
    // containing block; we simply pick the dominant edge.
    public sealed class StickyResolver {
        readonly ScrollContainer container;

        public StickyResolver(ScrollContainer container) {
            this.container = container;
        }

        public void Resolve(Box root) {
            if (root == null) return;
            Walk(root, scrollAncestor: null);
        }

        void Walk(Box box, Box scrollAncestor) {
            if (box.Position == PositionType.Sticky && scrollAncestor != null) {
                Apply(box, scrollAncestor);
            } else if (box.Position == PositionType.Sticky && scrollAncestor == null) {
                // Sticky inside an `overflow: visible` ancestor reduces to
                // "relative" per spec. The PositioningPass already wrote the
                // OffsetTop/etc; we simply translate the box like relative.
                ApplyAsRelative(box);
            }

            Box nextAncestor = scrollAncestor;
            if (ScrollContainerLookup.HasNonVisibleOverflow(box)) {
                nextAncestor = box;
            }
            for (int i = 0; i < box.Children.Count; i++) Walk(box.Children[i], nextAncestor);
        }

        static void ApplyAsRelative(Box box) {
            box.StickyOffsetX = 0;
            box.StickyOffsetY = 0;
            double dx = 0, dy = 0;
            if (box.OffsetLeft.HasValue) dx = box.OffsetLeft.Value;
            else if (box.OffsetRight.HasValue) dx = -box.OffsetRight.Value;
            if (box.OffsetTop.HasValue) dy = box.OffsetTop.Value;
            else if (box.OffsetBottom.HasValue) dy = -box.OffsetBottom.Value;
            box.StickyOffsetX = dx;
            box.StickyOffsetY = dy;
        }

        void Apply(Box box, Box scrollAncestor) {
            var state = container.Get(scrollAncestor);
            // No scroll state yet (ScrollLayout hasn't run, or container is
            // overflow:hidden/clip with no overflow): use a zero-scroll model
            // so sticky still degrades to "relative".
            double scrollX = state != null ? state.ScrollX : 0;
            double scrollY = state != null ? state.ScrollY : 0;

            // Element's natural position relative to the scroll container's
            // interior origin. We sum X/Y from box up to (but excluding) the
            // scroll ancestor, then subtract the scroll ancestor's interior
            // origin (padding-box).
            double naturalX, naturalY;
            ComputeNaturalRelativeToContainer(box, scrollAncestor, out naturalX, out naturalY);

            double containingTop, containingLeft, containingHeight, containingWidth;
            ComputeContainingBlockRelativeToContainer(box, scrollAncestor,
                out containingLeft, out containingTop, out containingWidth, out containingHeight);

            double viewportTop = scrollY;
            double viewportLeft = scrollX;
            double viewportHeight = state != null
                ? state.ViewportHeight
                : NonNegative(scrollAncestor.Height - scrollAncestor.PaddingTop - scrollAncestor.PaddingBottom - scrollAncestor.BorderTop - scrollAncestor.BorderBottom);
            double viewportWidth = state != null
                ? state.ViewportWidth
                : NonNegative(scrollAncestor.Width - scrollAncestor.PaddingLeft - scrollAncestor.PaddingRight - scrollAncestor.BorderLeft - scrollAncestor.BorderRight);
            double viewportBottom = viewportTop + viewportHeight;
            double viewportRight = viewportLeft + viewportWidth;

            // Vertical axis: top dominates when both are set.
            double dy = 0;
            if (box.OffsetTop.HasValue) {
                double specifiedTop = box.OffsetTop.Value;
                // pinned-top y in container space: viewportTop + specifiedTop
                double pinnedY = viewportTop + specifiedTop;
                if (pinnedY > naturalY) {
                    // Constrain so element stays within its containing block.
                    double maxY = containingTop + containingHeight - box.Height;
                    if (pinnedY > maxY) pinnedY = maxY;
                    if (pinnedY < naturalY) pinnedY = naturalY;
                    dy = pinnedY - naturalY;
                }
            } else if (box.OffsetBottom.HasValue) {
                double specifiedBottom = box.OffsetBottom.Value;
                // pinned-bottom y so that element bottom = viewportBottom - specifiedBottom.
                // Pinning happens when the natural position is ABOVE the pinned
                // line (i.e. the element wants to stay at the bottom of the
                // viewport while it's scrolled away from there). Bottom-sticky
                // moves the element DOWN relative to flow when natural < pinned.
                double pinnedY = viewportBottom - specifiedBottom - box.Height;
                if (pinnedY > naturalY) {
                    // Per CSS Position L3 §6.3 a sticky element must stay
                    // inside its containing block — the symmetric clamp to
                    // top-sticky's `maxY` above. The prior `minY = containingTop`
                    // was an inactive clamp (pinnedY > naturalY ≥ containingTop
                    // in any in-flow case), so bottom-sticky never released
                    // its grip when scrolled past the CB's bottom edge.
                    double maxY = containingTop + containingHeight - box.Height;
                    if (pinnedY > maxY) pinnedY = maxY;
                    if (pinnedY < naturalY) pinnedY = naturalY;
                    dy = pinnedY - naturalY;
                }
            }

            double dx = 0;
            if (box.OffsetLeft.HasValue) {
                double specifiedLeft = box.OffsetLeft.Value;
                double pinnedX = viewportLeft + specifiedLeft;
                if (pinnedX > naturalX) {
                    double maxX = containingLeft + containingWidth - box.Width;
                    if (pinnedX > maxX) pinnedX = maxX;
                    if (pinnedX < naturalX) pinnedX = naturalX;
                    dx = pinnedX - naturalX;
                }
            } else if (box.OffsetRight.HasValue) {
                double specifiedRight = box.OffsetRight.Value;
                double pinnedX = viewportRight - specifiedRight - box.Width;
                if (pinnedX < naturalX) {
                    double minX = containingLeft;
                    if (pinnedX < minX) pinnedX = minX;
                    if (pinnedX > naturalX) pinnedX = naturalX;
                    dx = pinnedX - naturalX;
                }
            }

            box.StickyOffsetX = dx;
            box.StickyOffsetY = dy;
        }

        // Sum (X, Y) from `box` up to (but not including) `until`, then subtract
        // the scroll container's interior origin so the result is relative to the
        // padding-box top-left of the container.
        static void ComputeNaturalRelativeToContainer(Box box, Box until, out double x, out double y) {
            x = 0; y = 0;
            for (var b = box; b != null && b != until; b = b.Parent) {
                x += b.X;
                y += b.Y;
            }
            // Subtract the container's interior origin (padding-box corner).
            x -= until.PaddingLeft + until.BorderLeft;
            y -= until.PaddingTop + until.BorderTop;
        }

        void ComputeContainingBlockRelativeToContainer(
            Box box, Box until,
            out double left, out double top, out double width, out double height) {
            // Containing block is the parent box for sticky, per spec.
            var p = box.Parent ?? until;
            if (p == until) {
                // Sticky's parent is the scroll container itself: containing
                // block is the scrollable content area, sized to the scroll
                // extent so a sticky element can pin all the way down to the
                // bottom of overflowed content.
                var state = container.Get(until);
                left = 0;
                top = 0;
                width = state != null ? state.ScrollWidth : until.Width;
                height = state != null ? state.ScrollHeight : until.Height;
                return;
            }
            double x = 0, y = 0;
            for (var b = p; b != null && b != until; b = b.Parent) {
                x += b.X;
                y += b.Y;
            }
            x -= until.PaddingLeft + until.BorderLeft;
            y -= until.PaddingTop + until.BorderTop;
            // CSS Positioned Layout L3 §6.3: a sticky element's containing
            // block is its parent's content edge, so the clamp must use
            // content-box origin and extent (parents' inner padding box
            // minus padding == content edge). Mirrors the D1 fix in
            // PositioningPass.PopulateOffsets for percentage insets.
            left = x + p.BorderLeft + p.PaddingLeft;
            top = y + p.BorderTop + p.PaddingTop;
            width = p.Width - p.PaddingLeft - p.PaddingRight - p.BorderLeft - p.BorderRight;
            height = p.Height - p.PaddingTop - p.PaddingBottom - p.BorderTop - p.BorderBottom;
        }

        static double NonNegative(double v) {
            return v < 0 ? 0 : v;
        }
    }

}
