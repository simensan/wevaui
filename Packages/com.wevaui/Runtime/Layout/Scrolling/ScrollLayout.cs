using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Layout.Boxes;
using Weva.Layout.Positioning;

namespace Weva.Layout.Scrolling {
    public sealed class ScrollLayout {
        readonly ScrollContainer container;

        public ScrollLayout(ScrollContainer container) {
            this.container = container;
        }

        public void Run(Box root) {
            if (root == null) return;
            Visit(root);
        }

        // CSS Overflow §3.3 — Viewport Scroll.
        //
        // Browsers special-case the viewport: when the root element's content
        // overflows the viewport, the UA automatically makes the viewport
        // scrollable even if the author never wrote `overflow: scroll/auto` on
        // <html> or <body>. The overflow value that controls this is propagated
        // from <body> to <html> (if <html> has `overflow: visible`, which is the
        // default, the UA adopts <body>'s overflow for the viewport instead).
        //
        // Per the CSS spec, if the propagated value is `hidden` or `clip`, the
        // viewport is non-scrollable. Any other value (including the default
        // `visible`) lets the viewport scroll automatically.
        //
        // This method registers (or removes) a ScrollState entry on the ROOT box
        // (the anonymous viewport box with Width/Height == viewport dimensions,
        // no Element) so the existing ScrollContainer / ScrollEventHandler /
        // BoxToPaintConverter machinery can drive viewport-level scrolling through
        // the same code path as element-level scroll containers.
        //
        // Important: call this AFTER Run(root) so per-element scroll states are
        // already established. The viewport scroll entry is keyed on the root box.
        public void RunViewportScroll(Box root, double viewportWidth, double viewportHeight) {
            if (root == null || viewportWidth <= 0 || viewportHeight <= 0) return;

            // Find <html> and <body> boxes among root's direct children and
            // their children respectively. The layout root is an anonymous
            // viewport box (Element == null) so <html> is its first named child.
            Box htmlBox = null, bodyBox = null;
            for (int i = 0; i < root.Children.Count; i++) {
                var c = root.Children[i];
                if (c.Element != null && c.Element.TagName == "html") { htmlBox = c; break; }
            }
            if (htmlBox != null) {
                for (int i = 0; i < htmlBox.Children.Count; i++) {
                    var c = htmlBox.Children[i];
                    if (c.Element != null && c.Element.TagName == "body") { bodyBox = c; break; }
                }
            }

            // CSS Overflow §3.3 root-overflow propagation:
            //   - If <html> has a non-visible overflow, it already has its own
            //     scroll container (Visit created it). The VIEWPORT itself is
            //     NOT also scrollable in that case.
            //   - If <html> overflow is 'visible' (or absent), the UA uses
            //     <body>'s overflow value to govern the VIEWPORT. The body's
            //     propagated overflow is then treated as 'visible' on the body
            //     element itself (browsers do not make the body a scroll container
            //     for the same overflow that was propagated to the viewport).
            //   - 'hidden'/'clip' on the element that governs the viewport
            //     suppresses viewport scrolling entirely.
            ScrollOverflow htmlOx = ScrollOverflow.Visible, htmlOy = ScrollOverflow.Visible;
            ScrollOverflow bodyOx = ScrollOverflow.Visible, bodyOy = ScrollOverflow.Visible;
            if (htmlBox != null) ResolveOverflow(htmlBox, out htmlOx, out htmlOy);
            if (bodyBox != null) ResolveOverflow(bodyBox, out bodyOx, out bodyOy);

            // If <html> itself has scroll/auto/hidden, Visit() already created a
            // scroll container for the <html> box — the viewport does not also
            // scroll separately.
            if (ScrollableNonVisible(htmlOx) || ScrollableNonVisible(htmlOy)) {
                if (container.Has(root)) container.Remove(root);
                return;
            }

            // Effective overflow for the viewport is the body's overflow
            // (propagated, since html's overflow is 'visible').
            // If body explicitly suppresses scrolling, remove any prior state.
            bool suppressX = bodyOx == ScrollOverflow.Hidden || bodyOx == ScrollOverflow.Clip;
            bool suppressY = bodyOy == ScrollOverflow.Hidden || bodyOy == ScrollOverflow.Clip;

            // Measure the content extent relative to the viewport. Walk the
            // content-owner box (body or html or root itself), accumulating
            // descendant extents, then add the content-owner's offset from the
            // viewport origin to get viewport-space extents.
            Box contentOwner = bodyBox ?? htmlBox ?? root;
            ComputeContentExtent(contentOwner, out double contentRight, out double contentBottom);

            // contentRight/Bottom are relative to contentOwner's interior
            // origin. Convert to viewport space.
            double originX = 0, originY = 0;
            for (var b = contentOwner; b != null && b != root; b = b.Parent) {
                originX += b.X + b.BorderLeft + b.PaddingLeft;
                originY += b.Y + b.BorderTop  + b.PaddingTop;
            }
            contentRight  += originX;
            contentBottom += originY;

            bool needsX = !suppressX && contentRight  > viewportWidth  + 0.0001;
            bool needsY = !suppressY && contentBottom > viewportHeight + 0.0001;

            if (!needsX && !needsY) {
                if (container.Has(root)) container.Remove(root);
                return;
            }

            var state = container.GetOrCreate(root);
            // Link state to root box for box.ScrollState API consistency.
            if (state.OwnerBox != root) { state.OwnerBox = root; root.ScrollState = state; }
            state.OverflowX = suppressX ? ScrollOverflow.Hidden : ScrollOverflow.Auto;
            state.OverflowY = suppressY ? ScrollOverflow.Hidden : ScrollOverflow.Auto;
            state.ViewportWidth  = viewportWidth;
            state.ViewportHeight = viewportHeight;
            double scrollW = suppressX ? viewportWidth  : (contentRight  > viewportWidth  ? contentRight  : viewportWidth);
            double scrollH = suppressY ? viewportHeight : (contentBottom > viewportHeight ? contentBottom : viewportHeight);
            state.ScrollWidth  = scrollW;
            state.ScrollHeight = scrollH;
            state.ScrollX = ScrollMath.Clamp(state.ScrollX, 0, state.MaxScrollX);
            state.ScrollY = ScrollMath.Clamp(state.ScrollY, 0, state.MaxScrollY);
        }

        // Returns the viewport scroll root box (the anonymous root with no Element)
        // if it has a viewport scroll state registered, otherwise null.
        // Used by ScrollEventHandler to route unconsumed wheel events to the
        // viewport scroll state.
        public Box ViewportScrollRoot(Box root) {
            if (root == null) return null;
            if (root.Element == null && container.Has(root)) return root;
            return null;
        }

        void Visit(Box box) {
            ResolveOverflow(box, out var ox, out var oy);
            bool isContainer = ScrollableNonVisible(ox) || ScrollableNonVisible(oy);

            if (isContainer) {
                var state = container.GetOrCreate(box);
                // CSS Overflow L3 §3 — link the state back to the box for
                // the box.ScrollState / box.IsScrollContainer fast-path API.
                // OwnerBox enables ScrollLeft/Top setters to mirror the offset
                // onto box.ScrollX/Y without a separate container lookup.
                if (state.OwnerBox != box) {
                    state.OwnerBox = box;
                    box.ScrollState = state;
                }
                state.OverflowX = ox;
                state.OverflowY = oy;

                ComputeContentExtent(box, out var contentRight, out var contentBottom);

                double innerW = box.Width
                                - box.BorderLeft - box.BorderRight
                                - box.PaddingLeft - box.PaddingRight;
                double innerH = box.Height
                                - box.BorderTop - box.BorderBottom
                                - box.PaddingTop - box.PaddingBottom;
                if (innerW < 0) innerW = 0;
                if (innerH < 0) innerH = 0;

                // Two-iteration fixed-point: scrollbar reservation feeds back
                // into the cross-axis viewport size, which can in turn make the
                // cross-axis content overflow its (now-shrunk) viewport. Rather
                // than iterate to convergence we run twice; the two-iteration
                // result matches the spec for any v1-relevant case (the only
                // case where it diverges is a marginal "barely overflows by
                // less than 12px" scenario, which we accept).
                double thickness = ScrollMath.ResolveScrollbarThickness(box);
                // CSS Scrollbars 1 §3.1 — `scrollbar-gutter: stable` reserves
                // the gutter even when no scrollbar would be visible. We
                // reserve on the block-axis (vertical) gutter only; the spec
                // ties gutter reservation to the document's overflow axis, and
                // the v1 writing-mode is horizontal-tb so block-axis = Y.
                bool stableGutter = thickness > 0 && ScrollMath.ReservesStableGutter(box);
                double viewportW = innerW;
                double viewportH = innerH;
                bool showX = false, showY = false;
                for (int iter = 0; iter < 2; iter++) {
                    showY = ShouldShowTrack(oy, contentBottom, viewportH);
                    showX = ShouldShowTrack(ox, contentRight, viewportW);
                    bool reserveY = showY || stableGutter;
                    viewportW = innerW - (reserveY ? thickness : 0);
                    viewportH = innerH - (showX ? thickness : 0);
                    if (viewportW < 0) viewportW = 0;
                    if (viewportH < 0) viewportH = 0;
                }

                double scrollW = contentRight;
                double scrollH = contentBottom;
                if (scrollW < viewportW) scrollW = viewportW;
                if (scrollH < viewportH) scrollH = viewportH;

                state.ViewportWidth = viewportW;
                state.ViewportHeight = viewportH;
                state.ScrollWidth = scrollW;
                state.ScrollHeight = scrollH;

                state.ScrollX = ScrollMath.Clamp(state.ScrollX, 0, state.MaxScrollX);
                state.ScrollY = ScrollMath.Clamp(state.ScrollY, 0, state.MaxScrollY);
            } else if (container.Has(box)) {
                container.Remove(box);
                // Clear the per-box reference so box.ScrollState returns null
                // immediately after the element's overflow is changed back to visible.
                if (box.ScrollState != null) {
                    box.ScrollState.OwnerBox = null;
                    box.ScrollState = null;
                }
            }

            for (int i = 0; i < box.Children.Count; i++) Visit(box.Children[i]);
        }

        // Content extent is measured in the scroll container's interior space:
        // origin at the inner padding-box corner. Per CSS Overflow L3 §3.2,
        // scrollable overflow is the union of border-boxes of ALL descendants
        // (not just direct children) whose containing block is the scroll
        // container or one of its descendants — so a nested in-flow box, or
        // a `position: relative` descendant whose far edge protrudes past
        // its parent, enlarges the scroll region.
        //
        // Excluded:
        //   - `position: fixed` descendants (CB is the viewport / a
        //     transformed ancestor, not the scroll container).
        //   - `position: absolute` descendants whose CB lies strictly above
        //     the scroll container in the tree.
        //   - Descendants of a nested scroll container: those are clipped /
        //     scrolled by the nested container; the outer container only
        //     sees the nested container's own border-box.
        static void ComputeContentExtent(Box box, out double right, out double bottom) {
            double interiorOriginX = box.PaddingLeft + box.BorderLeft;
            double interiorOriginY = box.PaddingTop + box.BorderTop;
            double maxRight = 0;
            double maxBottom = 0;
            for (int i = 0; i < box.Children.Count; i++) {
                AccumulateExtent(box.Children[i], box, -interiorOriginX, -interiorOriginY, ref maxRight, ref maxBottom);
            }
            // CSS Overflow §3.2: the scrollable overflow region includes the
            // scroll container's end-side (right/bottom) padding so a fully
            // scrolled view preserves the gutter. Start-side padding is
            // implicit in the descendant origin above.
            if (maxRight > 0) maxRight += box.PaddingRight;
            if (maxBottom > 0) maxBottom += box.PaddingBottom;
            right = maxRight;
            bottom = maxBottom;
        }

        static void AccumulateExtent(Box node, Box scrollContainer, double parentLocalX, double parentLocalY, ref double maxRight, ref double maxBottom) {
            if (!ContributesToScrollableOverflow(node, scrollContainer)) return;

            double nodeLocalX = parentLocalX + node.X;
            double nodeLocalY = parentLocalY + node.Y;
            double r = nodeLocalX + node.Width;
            double b = nodeLocalY + node.Height;
            if (r > maxRight) maxRight = r;
            if (b > maxBottom) maxBottom = b;

            // A nested scroll container (overflow != visible) clips/scrolls
            // its own descendants — they don't enlarge the outer container's
            // scrollable region beyond the nested container's border-box.
            if (ScrollContainerLookup.HasNonVisibleOverflow(node)) return;

            for (int i = 0; i < node.Children.Count; i++) {
                AccumulateExtent(node.Children[i], scrollContainer, nodeLocalX, nodeLocalY, ref maxRight, ref maxBottom);
            }
        }

        // Per CSS Overflow §3.2 a descendant contributes to the scroll
        // container's scrollable overflow only when its containing block is
        // the scroll container or a descendant of it. Fixed-positioned boxes
        // are anchored to the viewport (or a transformed ancestor) — outside
        // the scroll container in all v1-relevant cases. For absolute-
        // positioned descendants, walk up to the first positioned ancestor
        // and check whether it lies on the path from node to scrollContainer
        // (inclusive): if the first positioned ancestor is above the scroll
        // container (or no positioned ancestor exists between node and
        // scrollContainer and the scroll container itself isn't positioned),
        // the CB lies above the scroll container — skip.
        static bool ContributesToScrollableOverflow(Box node, Box scrollContainer) {
            if (node.Position == PositionType.Fixed) return false;
            if (node.Position != PositionType.Absolute) return true;
            for (var p = node.Parent; p != null; p = p.Parent) {
                if (p == scrollContainer) return p.IsPositioned();
                if (p.IsPositioned()) return true;
            }
            return false;
        }

        static bool ShouldShowTrack(ScrollOverflow ov, double content, double viewport) {
            if (ov == ScrollOverflow.Scroll) return true;
            if (ov == ScrollOverflow.Auto) return content > viewport + 0.0001;
            return false;
        }

        static bool ScrollableNonVisible(ScrollOverflow ov) {
            // Only scroll/auto/hidden produce a scroll context; "clip" is a paint-time
            // clip without a scroll machinery. Visible is the no-op default.
            return ov == ScrollOverflow.Hidden
                   || ov == ScrollOverflow.Scroll
                   || ov == ScrollOverflow.Auto;
        }

        static void ResolveOverflow(Box box, out ScrollOverflow ox, out ScrollOverflow oy) {
            ox = ScrollOverflow.Visible;
            oy = ScrollOverflow.Visible;
            ComputedStyle style = box.Style;
            if (style == null) return;
            // Per CSS Overflow §3, `overflow` is a shorthand for overflow-x/y; the
            // cascade has already expanded it into longhands, so we only consult
            // the longhands here. We still fall back to the shorthand to be tolerant
            // of styles authored without expansion (synthetic ComputedStyle in tests).
            //
            // Per-style parsed cache: overflow-x/-y are keyword-typed, so we
            // pattern-match the cached CssValue directly. Falls back to the
            // legacy ParseOverflow(string) only when the parser produced
            // something other than a keyword/identifier (rare — only authors
            // writing malformed values would hit it). Keeps the function on
            // an alloc-free path for the dominant case.
            var xParsed = style.GetParsed(CssProperties.OverflowXId);
            var yParsed = style.GetParsed(CssProperties.OverflowYId);
            if (xParsed == null && yParsed == null) {
                var sParsed = style.GetParsed(CssProperties.OverflowId);
                ox = DecodeOverflow(sParsed);
                oy = ox;
                return;
            }
            ox = DecodeOverflow(xParsed);
            oy = DecodeOverflow(yParsed);
        }

        // CSS Overflow §3 — keyword decode for the cached parse tree.
        // Mirrors ScrollMath.ParseOverflow but skips the string compare and
        // .ToLowerInvariant() allocation; the parser canonicalises CssKeyword
        // identifiers to lowercase already.
        static ScrollOverflow DecodeOverflow(CssValue parsed) {
            string name = null;
            if (parsed is CssKeyword k) name = k.Identifier;
            else if (parsed is CssIdentifier id) name = id.Name;
            else return ScrollOverflow.Visible;
            switch (name) {
                case "hidden": return ScrollOverflow.Hidden;
                case "scroll": return ScrollOverflow.Scroll;
                case "auto": return ScrollOverflow.Auto;
                case "clip": return ScrollOverflow.Clip;
                default: return ScrollOverflow.Visible;
            }
        }
    }

    internal static class ScrollContainerLookup {
        public static Box Nearest(Box from) {
            for (var b = from; b != null; b = b.Parent) {
                if (HasNonVisibleOverflow(b)) return b;
            }
            return null;
        }

        public static bool HasNonVisibleOverflow(Box box) {
            if (box?.Style == null) return false;
            // Per-style parsed cache: keyword-typed values surface directly,
            // no per-call .Trim().ToLowerInvariant() allocation. Falls
            // through to false on anything that isn't a recognised keyword,
            // matching the legacy Has(string) semantics.
            if (HasNonVisible(box.Style.GetParsed(CssProperties.OverflowXId))) return true;
            if (HasNonVisible(box.Style.GetParsed(CssProperties.OverflowYId))) return true;
            if (HasNonVisible(box.Style.GetParsed(CssProperties.OverflowId))) return true;
            return false;
        }

        static bool HasNonVisible(CssValue parsed) {
            string name = null;
            if (parsed is CssKeyword k) name = k.Identifier;
            else if (parsed is CssIdentifier id) name = id.Name;
            else return false;
            switch (name) {
                case "hidden":
                case "scroll":
                case "auto":
                    return true;
                default:
                    return false;
            }
        }
    }
}
