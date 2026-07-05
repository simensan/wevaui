using Weva.Css.Cascade;
using Weva.Css.Cascade.Shorthands;
using Weva.Css.Selectors;
using Weva.Css.Values;
using Weva.Dom;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Conversion;

namespace Weva.Layout.Scrolling {
    public static class ScrollMath {
        public const double LineStepPx = 40.0;
        public const double PageMarginPx = 60.0;
        public const double ScrollbarTrackThicknessPx = 12.0;
        public const double ScrollbarThinThicknessPx = 8.0;
        public const double ScrollbarMinThumbPx = 16.0;

        static int scrollbarWidthId = -1;
        static int scrollbarColorId = -1;
        static int scrollbarGutterId = -1;
        static int overscrollBehaviorId = -1;
        static int overscrollBehaviorXId = -1;
        static int overscrollBehaviorYId = -1;

        static int ScrollbarWidthId {
            get {
                if (scrollbarWidthId < 0) scrollbarWidthId = CssProperties.GetId("scrollbar-width");
                return scrollbarWidthId;
            }
        }

        static int ScrollbarColorId {
            get {
                if (scrollbarColorId < 0) scrollbarColorId = CssProperties.GetId("scrollbar-color");
                return scrollbarColorId;
            }
        }

        static int ScrollbarGutterId {
            get {
                if (scrollbarGutterId < 0) scrollbarGutterId = CssProperties.GetId("scrollbar-gutter");
                return scrollbarGutterId;
            }
        }

        static int OverscrollBehaviorId {
            get {
                if (overscrollBehaviorId < 0) overscrollBehaviorId = CssProperties.GetId("overscroll-behavior");
                return overscrollBehaviorId;
            }
        }

        static int OverscrollBehaviorXId {
            get {
                if (overscrollBehaviorXId < 0) overscrollBehaviorXId = CssProperties.GetId("overscroll-behavior-x");
                return overscrollBehaviorXId;
            }
        }

        static int OverscrollBehaviorYId {
            get {
                if (overscrollBehaviorYId < 0) overscrollBehaviorYId = CssProperties.GetId("overscroll-behavior-y");
                return overscrollBehaviorYId;
            }
        }

        public static double ResolveScrollbarThickness(Box box) {
            if (box?.Style == null) return ScrollbarTrackThicknessPx;
            int id = ScrollbarWidthId;
            if (id < 0) return ScrollbarTrackThicknessPx;
            string raw = box.Style.Get(id);
            if (string.IsNullOrEmpty(raw)) return ScrollbarTrackThicknessPx;
            switch (CssStringUtil.ToLowerInvariantOrSame(raw.Trim())) {
                case "none": return 0.0;
                case "thin": return ScrollbarThinThicknessPx;
                case "auto":
                default: return ScrollbarTrackThicknessPx;
            }
        }

        // CSS Scrollbars 1 §3.1 — `scrollbar-gutter: stable` reserves the
        // scrollbar gutter even when no scrollbar is visible. Returns true
        // iff the resolved value contains the `stable` keyword.
        public static bool ReservesStableGutter(Box box) {
            if (box?.Style == null) return false;
            int id = ScrollbarGutterId;
            if (id < 0) return false;
            string raw = box.Style.Get(id);
            if (string.IsNullOrEmpty(raw)) return false;
            // Value grammar: auto | stable && both-edges? — the keyword
            // `stable` triggers reservation; `both-edges` is a separate
            // sub-keyword we don't yet model (single-edge only).
            string lower = CssStringUtil.ToLowerInvariantOrSame(raw.Trim());
            if (lower == "stable") return true;
            // Accept "stable both-edges" / "both-edges stable" by token scan.
            foreach (var tok in lower.Split(' ')) {
                if (tok == "stable") return true;
            }
            return false;
        }

        // CSS Scrollbars 1 §3.2 — `scrollbar-color: <color> <color>` sets
        // thumb-color then track-color. `auto` (the initial value) leaves
        // both as null so the painter falls back to its UA defaults.
        public static bool TryResolveScrollbarColors(Box box, out LinearColor thumb, out LinearColor track) {
            thumb = default;
            track = default;
            if (box?.Style == null) return false;
            int id = ScrollbarColorId;
            if (id < 0) return false;
            string raw = box.Style.Get(id);
            if (string.IsNullOrEmpty(raw)) return false;
            string trimmed = raw.Trim();
            if (string.Equals(trimmed, "auto", System.StringComparison.OrdinalIgnoreCase)) return false;
            var tokens = ShorthandTokenizer.Tokenize(trimmed);
            if (tokens.Count != 2) return false;
            // currentColor needs the element's resolved color; ColorResolver
            // handles that lookup via the style's `color` property.
            var current = ColorResolver.ResolveCurrentColor(box.Style);
            if (!ColorResolver.TryResolve(tokens[0], current, box.Style, out thumb)) return false;
            if (!ColorResolver.TryResolve(tokens[1], current, box.Style, out track)) return false;
            return true;
        }

        // ── WebKit scrollbar pseudo-element resolution ──────────────────────────
        //
        // Chrome precedence: when ANY ::-webkit-scrollbar rule matches an element,
        // scrollbar-color and scrollbar-width are IGNORED for that element.
        // These helpers implement that contract by reading pseudo-element computed
        // styles from the cascade engine rather than from the host box's own style.
        //
        // scrollbarCascade must be the CascadeEngine that holds the stylesheet that
        // authored the webkit rules. Passing null is safe and always returns "no match".

        // Returns the resolved thickness from ::-webkit-scrollbar { width: Npx }
        // (for vertical scrollbars) or { height: Npx } (for horizontal). The `axis`
        // parameter selects which dimension to read.
        // Returns -1.0 when no ::-webkit-scrollbar rule matches.
        public static double ResolveWebkitScrollbarThickness(
            Element element,
            CascadeEngine scrollbarCascade,
            IElementStateProvider stateProvider,
            ScrollAxis axis) {

            if (element == null || scrollbarCascade == null) return -1.0;
            var style = scrollbarCascade.ComputeWebkitScrollbar(element, stateProvider);
            if (style == null) return -1.0;

            // Axis selection: vertical scrollbar uses `width`, horizontal uses `height`.
            string propName = axis == ScrollAxis.Y ? "width" : "height";
            int propId = CssProperties.GetId(propName);
            if (propId < 0) return -1.0;
            string raw = style.Get(propId);
            if (string.IsNullOrEmpty(raw)) return -1.0;

            // Parse length — only px values are meaningful for scrollbar thickness.
            if (CssValue.TryParse(raw, out var parsed) && parsed is CssLength len) {
                var ctx = LengthContext.Default;
                double px = len.ToPixels(ctx);
                if (px >= 0) return px;
            }
            // Keyword "auto" or unrecognized → -1 (caller falls back to L1 / UA default).
            return -1.0;
        }

        // Resolves ::-webkit-scrollbar-thumb { background(-color), border-radius }.
        // Returns false when no rule matches; true and populates out-params otherwise.
        // borderRadius is the uniform (top-left) x-radius from border-radius, or 0.
        public static bool TryResolveWebkitThumb(
            Element element,
            CascadeEngine scrollbarCascade,
            IElementStateProvider stateProvider,
            out LinearColor color,
            out double borderRadiusPx) {

            color = default;
            borderRadiusPx = 0.0;
            if (element == null || scrollbarCascade == null) return false;
            var style = scrollbarCascade.ComputeWebkitScrollbarThumb(element, stateProvider);
            if (style == null) return false;

            var current = ColorResolver.ResolveCurrentColor(style);
            // Read background-color longhand (populated by shorthand expander).
            int bgColorId = CssProperties.BackgroundColorId;
            string bgRaw = bgColorId >= 0 ? style.Get(bgColorId) : null;
            if (!string.IsNullOrEmpty(bgRaw) && ColorResolver.TryResolve(bgRaw, current, style, out color)) {
                // border-radius: read the top-left corner radius as the uniform thumb radius.
                int brId = CssProperties.BorderTopLeftRadiusId;
                if (brId >= 0) {
                    string brRaw = style.Get(brId);
                    if (!string.IsNullOrEmpty(brRaw) && CssValue.TryParse(brRaw, out var brParsed)) {
                        if (brParsed is CssLength brLen) {
                            borderRadiusPx = System.Math.Max(0.0, brLen.ToPixels(LengthContext.Default));
                        } else if (brParsed is CssNumber brNum) {
                            borderRadiusPx = System.Math.Max(0.0, brNum.Value);
                        }
                    } else {
                        // Try the shorthand border-radius.
                        int shorthandId = CssProperties.BorderRadiusId;
                        if (shorthandId >= 0) {
                            string shortRaw = style.Get(shorthandId);
                            if (!string.IsNullOrEmpty(shortRaw) && CssValue.TryParse(shortRaw, out var shortParsed)) {
                                if (shortParsed is CssLength sl) {
                                    borderRadiusPx = System.Math.Max(0.0, sl.ToPixels(LengthContext.Default));
                                } else if (shortParsed is CssNumber sn) {
                                    borderRadiusPx = System.Math.Max(0.0, sn.Value);
                                }
                            }
                        }
                    }
                }
                return true;
            }
            // background-color not resolvable (e.g. only border-radius was set).
            // Still report a match so webkit precedence kicks in.
            return true;
        }

        // Resolves ::-webkit-scrollbar-thumb styles with per-axis hover/active
        // override. `thumbHovered` and `thumbActive` are set by ScrollEventHandler
        // based on pointer position and drag state on that axis. When true, the state
        // provider passed to the cascade is wrapped to report Hover/Active for the
        // host element, allowing ::-webkit-scrollbar-thumb:hover / :active rules to
        // match. Chrome keeps hover AND active styles during an active drag.
        public static bool TryResolveWebkitThumbForAxis(
            Element element,
            CascadeEngine scrollbarCascade,
            IElementStateProvider stateProvider,
            bool thumbHovered,
            bool thumbActive,
            out LinearColor color,
            out double borderRadiusPx) {

            // When neither hover nor active is set, fall back to the base resolver.
            if (!thumbHovered && !thumbActive) {
                return TryResolveWebkitThumb(element, scrollbarCascade, stateProvider, out color, out borderRadiusPx);
            }
            // Wrap the state provider to inject hover/active for the host element.
            var wrappedState = new ThumbHoverStateProvider(stateProvider, element, thumbHovered, thumbActive);
            return TryResolveWebkitThumb(element, scrollbarCascade, wrappedState, out color, out borderRadiusPx);
        }

        // Resolves ::-webkit-scrollbar-track { background(-color) }.
        // Returns false when no rule matches; true and populates out-param otherwise.
        public static bool TryResolveWebkitTrack(
            Element element,
            CascadeEngine scrollbarCascade,
            IElementStateProvider stateProvider,
            out LinearColor color) {

            color = default;
            if (element == null || scrollbarCascade == null) return false;
            var style = scrollbarCascade.ComputeWebkitScrollbarTrack(element, stateProvider);
            if (style == null) return false;

            var current = ColorResolver.ResolveCurrentColor(style);
            int bgColorId = CssProperties.BackgroundColorId;
            string bgRaw = bgColorId >= 0 ? style.Get(bgColorId) : null;
            if (!string.IsNullOrEmpty(bgRaw) && ColorResolver.TryResolve(bgRaw, current, style, out color)) {
                return true;
            }
            // Track matched but no background-color resolved (e.g. only border-radius set).
            return true;
        }

        // Resolves ::-webkit-scrollbar-corner { background-color }.
        // Returns false when no rule matches (caller skips the corner fill).
        // Returns true and populates `color` when a rule matches and a color
        // was successfully resolved. When the rule matches but has no
        // background-color, returns true with color=default (opaque black is
        // a safe ignored fallback because the caller won't paint it).
        public static bool TryResolveWebkitCorner(
            Element element,
            CascadeEngine scrollbarCascade,
            IElementStateProvider stateProvider,
            out LinearColor color) {

            color = default;
            if (element == null || scrollbarCascade == null) return false;
            var style = scrollbarCascade.ComputeWebkitScrollbarCorner(element, stateProvider);
            if (style == null) return false;

            var current = ColorResolver.ResolveCurrentColor(style);
            int bgColorId = CssProperties.BackgroundColorId;
            string bgRaw = bgColorId >= 0 ? style.Get(bgColorId) : null;
            if (!string.IsNullOrEmpty(bgRaw) && ColorResolver.TryResolve(bgRaw, current, style, out color)) {
                return true;
            }
            // Matched but no background-color resolved.
            return true;
        }

        public enum ScrollAxis { X, Y }

        // CSS Overscroll Behavior 1 — `overscroll-behavior-x|-y` longhands
        // override the shorthand on their axis. When the per-axis longhand
        // is unset (or `auto`), fall through to the shorthand for back-compat
        // with stylesheets that only set `overscroll-behavior`.
        public static bool ShouldContainOverscroll(Box box, ScrollAxis axis) {
            if (box?.Style == null) return false;
            int axisId = axis == ScrollAxis.X ? OverscrollBehaviorXId : OverscrollBehaviorYId;
            if (axisId >= 0) {
                string axisRaw = box.Style.Get(axisId);
                if (!string.IsNullOrEmpty(axisRaw)) {
                    string axisLower = CssStringUtil.ToLowerInvariantOrSame(axisRaw.Trim());
                    if (axisLower != "auto") {
                        return axisLower == "contain" || axisLower == "none";
                    }
                }
            }
            return ShouldContainOverscroll(box);
        }

        public static bool ShouldContainOverscroll(Box box) {
            if (box?.Style == null) return false;
            int id = OverscrollBehaviorId;
            if (id < 0) return false;
            string raw = box.Style.Get(id);
            if (string.IsNullOrEmpty(raw)) return false;
            switch (CssStringUtil.ToLowerInvariantOrSame(raw.Trim())) {
                case "contain":
                case "none":
                    return true;
                default:
                    return false;
            }
        }

        public static double Clamp(double v, double min, double max) {
            if (max < min) max = min;
            // L14: a NaN scroll offset (from a bad delta / animated value) is
            // not < min nor > max, so it would pass through and propagate.
            // Clamp it to the start instead of letting NaN reach scroll state.
            if (double.IsNaN(v)) return min;
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        public static double LineStep(double fontSizePx) {
            if (fontSizePx <= 0) return LineStepPx;
            // Use the larger of one em or the default 40px line step so very small
            // fonts still scroll at a comfortable speed.
            return fontSizePx > LineStepPx ? fontSizePx : LineStepPx;
        }

        public static double PageStep(double viewportPx) {
            double step = viewportPx - PageMarginPx;
            return step > LineStepPx ? step : LineStepPx;
        }

        public static double ParseOverflow(string raw, out ScrollOverflow value) {
            value = ScrollOverflow.Visible;
            if (string.IsNullOrEmpty(raw)) return 0;
            switch (CssStringUtil.ToLowerInvariantOrSame(raw.Trim())) {
                case "hidden": value = ScrollOverflow.Hidden; return 1;
                case "scroll": value = ScrollOverflow.Scroll; return 1;
                case "auto":   value = ScrollOverflow.Auto;   return 1;
                case "clip":   value = ScrollOverflow.Clip;   return 1;
                case "visible":
                default:       value = ScrollOverflow.Visible; return 0;
            }
        }
    }

    // IElementStateProvider wrapper that injects Hover and/or Active bits
    // for a specific target element. Used by ScrollbarPaint to make
    // ::-webkit-scrollbar-thumb:hover / :active cascade rules match when the
    // pointer is over the thumb rect or a drag is in progress on that axis.
    // All other elements delegate to the wrapped provider unchanged.
    internal sealed class ThumbHoverStateProvider : IElementStateProvider {
        readonly IElementStateProvider inner;
        readonly Dom.Element targetElement;
        readonly bool thumbHovered;
        readonly bool thumbActive;

        public ThumbHoverStateProvider(IElementStateProvider inner, Dom.Element target, bool hovered, bool active) {
            this.inner = inner;
            this.targetElement = target;
            this.thumbHovered = hovered;
            this.thumbActive = active;
        }

        public ElementState GetState(Dom.Element element) {
            var base_ = inner != null ? inner.GetState(element) : ElementState.None;
            if (!ReferenceEquals(element, targetElement)) return base_;
            if (thumbHovered) base_ |= ElementState.Hover;
            if (thumbActive)  base_ |= ElementState.Active;
            return base_;
        }
    }
}
