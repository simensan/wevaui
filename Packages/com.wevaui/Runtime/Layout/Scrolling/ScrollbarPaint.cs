using Weva.Css.Cascade;
using Weva.Css.Selectors;
using Weva.Css.Values;
using Weva.Layout.Boxes;
using Weva.Paint;

namespace Weva.Layout.Scrolling {
    public static class ScrollbarPaint {
        static readonly LinearColor TrackColor = LinearColor.FromCssColor(new CssColor(238, 238, 240, 1f));
        static readonly LinearColor ThumbColor = LinearColor.FromCssColor(new CssColor(176, 176, 184, 1f));
        static readonly LinearColor ThumbHoverColor = LinearColor.FromCssColor(new CssColor(120, 120, 132, 1f));
        // UA default corner background (light grey — matches Chrome's classic scrollbar corner).
        // For overlay-style scrollbars with no authored corner rule the corner is NOT painted
        // (we return early when TryResolveWebkitCorner returns false), matching Chrome's
        // overlay mode. This color is a fallback safety value, never actually used.
        static readonly LinearColor CornerColor = LinearColor.FromCssColor(new CssColor(220, 220, 220, 1f));

        // Backward-compatible overload (no webkit cascade — tests and callers without
        // a cascade engine use this).
        public static void Emit(
            Box box,
            ScrollState state,
            double absX,
            double absY,
            PaintList list,
            IElementStateProvider stateProvider) {
            Emit(box, state, absX, absY, list, stateProvider, null);
        }

        // Full overload: accepts a CascadeEngine for ::-webkit-scrollbar* resolution.
        // When `scrollbarCascade` is non-null and any ::-webkit-scrollbar rule matches
        // `box.Element`, the webkit styles are applied in place of CSS Scrollbars L1.
        //
        // Precedence (mirrors Chrome):
        //   1. If any webkit scrollbar rule matches → use webkit colors/thickness/radius;
        //      CSS Scrollbars L1 (scrollbar-color / scrollbar-width) are IGNORED.
        //   2. Otherwise fall through to CSS Scrollbars L1, then UA defaults.
        public static void Emit(
            Box box,
            ScrollState state,
            double absX,
            double absY,
            PaintList list,
            IElementStateProvider stateProvider,
            CascadeEngine scrollbarCascade) {

            if (state == null || list == null) return;

            var element = box?.Element;

            // ── Detect webkit presence ────────────────────────────────────────
            // Chrome precedence: if ANY ::-webkit-scrollbar(-thumb|-track) rule
            // matches this element, the webkit styles override CSS Scrollbars L1
            // entirely.  We resolve thumb and track styles here (caching them in
            // locals) to avoid triple-cascade lookups later.
            bool webkitActive = false;
            bool wkThumbResolved = false;
            bool wkTrackResolved = false;
            LinearColor wkThumbColor = default;
            LinearColor wkTrackColor = default;
            double thumbBorderRadius = 0.0;

            // Per-axis thumb hover/active from ScrollState (updated by ScrollEventHandler
            // pointer-move events). Vertical and horizontal thumbs are resolved separately
            // so ::-webkit-scrollbar-thumb:hover applies only to the hovered axis.
            bool thumbHoveredY = state != null && state.ThumbHoveredY;
            bool thumbActiveY  = state != null && state.ThumbActiveY;
            bool thumbHoveredX = state != null && state.ThumbHoveredX;
            bool thumbActiveX  = state != null && state.ThumbActiveX;

            if (scrollbarCascade != null && element != null) {
                // Attempt thumb resolution with base state (no axis-specific hover yet).
                // We use TryResolveWebkitThumb here just to detect webkit presence;
                // the per-axis resolution happens later in EmitVerticalTrack/EmitHorizontalTrack.
                if (ScrollMath.TryResolveWebkitThumb(element, scrollbarCascade, stateProvider,
                        out wkThumbColor, out thumbBorderRadius)) {
                    webkitActive = true;
                    wkThumbResolved = true;
                }
                // Attempt track resolution — also counts as webkit active.
                if (ScrollMath.TryResolveWebkitTrack(element, scrollbarCascade, stateProvider,
                        out wkTrackColor)) {
                    webkitActive = true;
                    wkTrackResolved = true;
                }
                // Main ::-webkit-scrollbar may also exist even without thumb/track.
                var wkStyle = scrollbarCascade.ComputeWebkitScrollbar(element, stateProvider);
                if (wkStyle != null) webkitActive = true;
            }

            // ── Resolve thickness ─────────────────────────────────────────────
            // When webkit is active, ::-webkit-scrollbar { width/height } sets the
            // track thickness. The same value is used for both axes (Chrome uses
            // `width` for vertical and `height` for horizontal; we pick the axis
            // based on which track will be painted).
            //
            // When webkit is inactive, fall through to CSS Scrollbars L1.
            double thicknessV = -1.0; // vertical track thickness
            double thicknessH = -1.0; // horizontal track thickness
            if (webkitActive && element != null) {
                thicknessV = ScrollMath.ResolveWebkitScrollbarThickness(
                    element, scrollbarCascade, stateProvider, ScrollMath.ScrollAxis.Y);
                thicknessH = ScrollMath.ResolveWebkitScrollbarThickness(
                    element, scrollbarCascade, stateProvider, ScrollMath.ScrollAxis.X);
            }
            // If no webkit thickness was found (webkit may be active via thumb/track only),
            // use the L1 / UA default thickness for both axes.
            double l1Thickness = webkitActive
                ? ScrollMath.ScrollbarTrackThicknessPx  // webkit active but no explicit width: use UA default
                : ScrollMath.ResolveScrollbarThickness(box);
            if (thicknessV < 0) thicknessV = l1Thickness;
            if (thicknessH < 0) thicknessH = l1Thickness;

            // CSS Scrollbars 1 §3.3 — `scrollbar-width: none` suppresses the entire
            // UA scrollbar when webkit is NOT active. The gutter is still reserved.
            if (!webkitActive && l1Thickness <= 0) return;
            // When webkit is active, a zero explicit thickness hides the scrollbar.
            if (webkitActive && thicknessV <= 0 && thicknessH <= 0) return;

            bool hover = stateProvider != null
                         && element != null
                         && (stateProvider.GetState(element) & ElementState.Hover) != 0;

            // ── Resolve shared colors (track + L1 / UA thumb base) ────────────
            LinearColor trackColor = TrackColor;
            LinearColor thumbColorBase = hover ? ThumbHoverColor : ThumbColor;

            if (webkitActive) {
                // Track color is shared across both axes (webkit track rule applies to
                // both vertical and horizontal tracks).
                if (wkTrackResolved && !wkTrackColor.Equals(default(LinearColor))) trackColor = wkTrackColor;
                // Base thumb color from the unqualified ::-webkit-scrollbar-thumb rule.
                if (wkThumbResolved && !wkThumbColor.Equals(default(LinearColor))) thumbColorBase = wkThumbColor;
            } else {
                // CSS Scrollbars 1 §3.2 — `scrollbar-color: <thumb> <track>`.
                if (ScrollMath.TryResolveScrollbarColors(box, out var t, out var tk)) {
                    thumbColorBase = t;
                    trackColor = tk;
                }
            }

            if (state.ShowsTrackY && thicknessV > 0) {
                // For the vertical thumb: re-resolve with per-axis hover/active state
                // so ::-webkit-scrollbar-thumb:hover / :active rules apply when the
                // pointer is over this specific thumb (not the horizontal one).
                LinearColor thumbColorY = thumbColorBase;
                double borderRadiusY = thumbBorderRadius;
                if (webkitActive && element != null && (thumbHoveredY || thumbActiveY)) {
                    if (ScrollMath.TryResolveWebkitThumbForAxis(element, scrollbarCascade, stateProvider,
                            thumbHoveredY, thumbActiveY, out var hovThumbColor, out var hovRadius)) {
                        if (!hovThumbColor.Equals(default(LinearColor))) thumbColorY = hovThumbColor;
                        if (hovRadius > 0) borderRadiusY = hovRadius;
                    }
                }
                EmitVerticalTrack(box, state, absX, absY, list, thicknessV, trackColor, thumbColorY, borderRadiusY);
            }
            if (state.ShowsTrackX && thicknessH > 0) {
                // Same per-axis hover/active resolution for the horizontal thumb.
                LinearColor thumbColorX = thumbColorBase;
                double borderRadiusX = thumbBorderRadius;
                if (webkitActive && element != null && (thumbHoveredX || thumbActiveX)) {
                    if (ScrollMath.TryResolveWebkitThumbForAxis(element, scrollbarCascade, stateProvider,
                            thumbHoveredX, thumbActiveX, out var hovThumbColor, out var hovRadius)) {
                        if (!hovThumbColor.Equals(default(LinearColor))) thumbColorX = hovThumbColor;
                        if (hovRadius > 0) borderRadiusX = hovRadius;
                    }
                }
                EmitHorizontalTrack(box, state, absX, absY, list, thicknessH, trackColor, thumbColorX, borderRadiusX);
            }

            // ── Corner paint ──────────────────────────────────────────────────
            // When both axes show scrollbars and a ::-webkit-scrollbar-corner rule
            // is authored, fill the overlap square. Un-authored → no fill (matches
            // Chrome's overlay-style scrollbar behaviour).
            if (state.ShowsTrackY && state.ShowsTrackX && thicknessV > 0 && thicknessH > 0
                && webkitActive && element != null && scrollbarCascade != null) {
                if (ScrollMath.TryResolveWebkitCorner(element, scrollbarCascade, stateProvider,
                        out var cornerColor) && !cornerColor.Equals(default(LinearColor))) {
                    double cornerX = absX + box.Width - box.BorderRight - thicknessV;
                    double cornerY = absY + box.Height - box.BorderBottom - thicknessH;
                    list.Add(new FillRectCommand(
                        new Rect(cornerX, cornerY, thicknessV, thicknessH),
                        Brush.SolidColor(cornerColor)));
                }
            }
        }

        static void EmitVerticalTrack(Box box, ScrollState state, double absX, double absY, PaintList list, double thickness, LinearColor trackColor, LinearColor thumbColor, double thumbBorderRadius) {
            double trackX = absX + box.Width - box.BorderRight - thickness;
            double trackY = absY + box.BorderTop;
            double trackH = box.Height - box.BorderTop - box.BorderBottom;
            if (state.ShowsTrackX) trackH -= thickness;
            if (trackH <= 0) return;
            list.Add(new FillRectCommand(
                new Rect(trackX, trackY, thickness, trackH),
                Brush.SolidColor(trackColor)));

            double thumbH = state.ScrollHeight > 0
                ? trackH * (state.ViewportHeight / state.ScrollHeight)
                : trackH;
            if (thumbH < ScrollMath.ScrollbarMinThumbPx) thumbH = ScrollMath.ScrollbarMinThumbPx;
            if (thumbH > trackH) thumbH = trackH;

            double thumbY = trackY;
            if (state.MaxScrollY > 0) {
                thumbY += (state.ScrollY / state.MaxScrollY) * (trackH - thumbH);
            }

            // Thumb border-radius from ::-webkit-scrollbar-thumb { border-radius }.
            // Clamp to half the smaller dimension so it never exceeds a semicircle.
            var thumbRect = new Rect(trackX, thumbY, thickness, thumbH);
            var thumbRadii = thumbBorderRadius > 0
                ? BorderRadii.Uniform(System.Math.Min(thumbBorderRadius, System.Math.Min(thickness, thumbH) * 0.5))
                : BorderRadii.Zero;
            list.Add(new FillRectCommand(thumbRect, Brush.SolidColor(thumbColor), thumbRadii));
        }

        static void EmitHorizontalTrack(Box box, ScrollState state, double absX, double absY, PaintList list, double thickness, LinearColor trackColor, LinearColor thumbColor, double thumbBorderRadius) {
            double trackY = absY + box.Height - box.BorderBottom - thickness;
            double trackX = absX + box.BorderLeft;
            double trackW = box.Width - box.BorderLeft - box.BorderRight;
            if (state.ShowsTrackY) trackW -= thickness;
            if (trackW <= 0) return;
            list.Add(new FillRectCommand(
                new Rect(trackX, trackY, trackW, thickness),
                Brush.SolidColor(trackColor)));

            double thumbW = state.ScrollWidth > 0
                ? trackW * (state.ViewportWidth / state.ScrollWidth)
                : trackW;
            if (thumbW < ScrollMath.ScrollbarMinThumbPx) thumbW = ScrollMath.ScrollbarMinThumbPx;
            if (thumbW > trackW) thumbW = trackW;

            double thumbX = trackX;
            if (state.MaxScrollX > 0) {
                thumbX += (state.ScrollX / state.MaxScrollX) * (trackW - thumbW);
            }

            var thumbRect = new Rect(thumbX, trackY, thumbW, thickness);
            var thumbRadii = thumbBorderRadius > 0
                ? BorderRadii.Uniform(System.Math.Min(thumbBorderRadius, System.Math.Min(thumbW, thickness) * 0.5))
                : BorderRadii.Zero;
            list.Add(new FillRectCommand(thumbRect, Brush.SolidColor(thumbColor), thumbRadii));
        }
    }
}
