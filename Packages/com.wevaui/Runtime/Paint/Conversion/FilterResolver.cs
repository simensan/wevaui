using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Paint.Filters;

namespace Weva.Paint.Conversion {
    internal static class FilterResolver {
        // Process-static FilterChain cache keyed on (raw, currentColor). Was a
        // converter-local Dictionary<string, FilterChain> that dropped any
        // chain containing `currentcolor` (because the resolved colour depends
        // on the element). Hashing the resolved currentColor into the key lets
        // those chains hit too — and promoting the cache to process-static
        // means independent converter instances share the work.
        const int FilterChainCacheCap = 128;
        // Key must include every LengthContext field a filter length resolves
        // against — `blur(2vh)`, `drop-shadow(0 0 1em ...)`, etc. The prior key
        // was just (raw, currentColor), so a vh/em-bearing filter froze at the
        // viewport/font-size that first populated the entry (same class of bug as
        // BoxShadowResolver's viewport-blind cache). (% is invalid in filter
        // lengths, so BasisPixels is excluded.)
        readonly struct FilterChainKey : System.IEquatable<FilterChainKey> {
            public readonly string Raw;
            public readonly double BaseFontSizePx;
            public readonly double RootFontSizePx;
            public readonly double ViewportWidthPx;
            public readonly double ViewportHeightPx;
            public readonly double DpiPixelsPerInch;
            public readonly double LineHeightPx;
            public readonly double RootLineHeightPx;
            public readonly LinearColor CurrentColor;
            public FilterChainKey(string raw, in LengthContext ctx, LinearColor currentColor) {
                Raw = raw;
                BaseFontSizePx = ctx.BaseFontSizePx;
                RootFontSizePx = ctx.RootFontSizePx;
                ViewportWidthPx = ctx.ViewportWidthPx;
                ViewportHeightPx = ctx.ViewportHeightPx;
                DpiPixelsPerInch = ctx.DpiPixelsPerInch;
                LineHeightPx = ctx.LineHeightPx;
                RootLineHeightPx = ctx.RootLineHeightPx;
                CurrentColor = currentColor;
            }
            public bool Equals(FilterChainKey other) =>
                Raw == other.Raw && BaseFontSizePx == other.BaseFontSizePx
                && RootFontSizePx == other.RootFontSizePx
                && ViewportWidthPx == other.ViewportWidthPx
                && ViewportHeightPx == other.ViewportHeightPx
                && DpiPixelsPerInch == other.DpiPixelsPerInch
                && LineHeightPx == other.LineHeightPx
                && RootLineHeightPx == other.RootLineHeightPx
                && CurrentColor.Equals(other.CurrentColor);
            public override bool Equals(object obj) => obj is FilterChainKey k && Equals(k);
            public override int GetHashCode() {
                unchecked {
                    int h = Raw != null ? Raw.GetHashCode() : 0;
                    h = (h * 397) ^ BaseFontSizePx.GetHashCode();
                    h = (h * 397) ^ RootFontSizePx.GetHashCode();
                    h = (h * 397) ^ ViewportWidthPx.GetHashCode();
                    h = (h * 397) ^ ViewportHeightPx.GetHashCode();
                    h = (h * 397) ^ DpiPixelsPerInch.GetHashCode();
                    h = (h * 397) ^ LineHeightPx.GetHashCode();
                    h = (h * 397) ^ RootLineHeightPx.GetHashCode();
                    h = (h * 397) ^ CurrentColor.GetHashCode();
                    return h;
                }
            }
        }
        static readonly Dictionary<FilterChainKey, FilterChain> s_FilterChainCache
            = new Dictionary<FilterChainKey, FilterChain>();

        public static FilterChain Resolve(ComputedStyle style, LengthContext ctx) {
            return Resolve(style, ctx, CssProperties.FilterId);
        }

        public static FilterChain ResolveBackdrop(ComputedStyle style, LengthContext ctx) {
            return Resolve(style, ctx, CssProperties.BackdropFilterId);
        }

        static FilterChain Resolve(ComputedStyle style, LengthContext ctx, int propertyId) {
            if (style == null) return FilterChain.Empty;

            // Pull the parsed `filter:` tree directly from the per-style cache
            // (ComputedStyle.GetParsed). Skips the global string→CssValue probe
            // CssValue.TryParse would do, and lets us detect `none` and walk
            // function calls via typed pattern-matching instead of re-tokenising.
            var parsed = style.GetParsed(propertyId);
            if (parsed == null) {
                // CssValue.TryParse currently rejects angle units (deg/turn/rad/
                // grad) as length units, so `hue-rotate(45deg)` falls out of the
                // parsed-tree path. Fall back to the raw-text FilterParser so
                // those cases continue to render correctly until the value
                // parser learns angle units.
                string raw = style.Get(propertyId);
                if (string.IsNullOrEmpty(raw)) return FilterChain.Empty;
                var currentColorRaw = ColorResolver.ResolveCurrentColor(style);
                var fallbackKey = new FilterChainKey(raw, ctx, currentColorRaw);
                if (s_FilterChainCache.TryGetValue(fallbackKey, out var fallbackHit)) return fallbackHit;
                var fallback = FilterParser.Parse(raw, ctx, currentColorRaw);
                // P16: slice-evict on overflow rather than drop-new (an
                // animated brightness()/blur() value floods the cap with
                // novel raw strings; drop-new then re-parses every static
                // filter every frame for the rest of the session).
                ParseCacheEviction.EnsureRoom(s_FilterChainCache, FilterChainCacheCap);
                s_FilterChainCache[fallbackKey] = fallback;
                return fallback;
            }
            if (IsNone(parsed)) return FilterChain.Empty;

            string rawForCache = parsed.Raw ?? "";
            var currentColor = ColorResolver.ResolveCurrentColor(style);
            // Hashing currentColor into the key means filter chains that
            // depend on the element's `color` (e.g. `drop-shadow(0 0 2px
            // currentcolor)`) cache correctly — same (filter-text, color)
            // pair returns the same FilterChain. Prior implementation
            // skipped caching whenever `currentcolor` appeared in the raw
            // text, which forced a fresh parse + List<FilterFunction>
            // allocation per cache miss for those chains.
            var key = new FilterChainKey(rawForCache, ctx, currentColor);
            if (s_FilterChainCache.TryGetValue(key, out var cached)) return cached;
            var functions = new List<FilterFunction>();
            AppendFunctions(parsed, ctx, currentColor, functions);
            var chain = functions.Count == 0 ? FilterChain.Empty : new FilterChain(functions);
            ParseCacheEviction.EnsureRoom(s_FilterChainCache, FilterChainCacheCap);
            s_FilterChainCache[key] = chain;
            return chain;
        }

        // A filter-function list is a *space-separated* CssValueList of
        // CssFunctionCalls (commas aren't permitted between filter functions).
        // A single function appears as a lone CssFunctionCall.
        static void AppendFunctions(CssValue parsed, LengthContext ctx, LinearColor currentColor, List<FilterFunction> output) {
            if (parsed is CssValueList list && list.Separator == CssValueListSeparator.Space) {
                for (int i = 0; i < list.Items.Count; i++) {
                    AppendOne(list.Items[i], ctx, currentColor, output);
                }
                return;
            }
            AppendOne(parsed, ctx, currentColor, output);
        }

        static void AppendOne(CssValue v, LengthContext ctx, LinearColor currentColor, List<FilterFunction> output) {
            // SVG-referenced filters (url(#id)) are out of v1 scope. Skip them
            // gracefully so sibling functions in the same declaration still apply.
            if (v is CssUrl) return;
            if (!(v is CssFunctionCall fn)) {
                // The parser only emits a CssFunctionCall for `name(...)`.
                // Anything else here (a bare keyword that isn't `none`, etc.)
                // is malformed per the filter grammar. Match the prior
                // FilterParser behaviour and surface it as a hard error so
                // authors notice typos.
                throw new FilterParseException("Expected filter function, got '" + (v?.Raw ?? "") + "'");
            }
            if (fn.Name == "url") return;
            // The parser already lowercases the name; this dispatch matches
            // FilterParser.DispatchFunction one-for-one.
            //
            // Identity filters (brightness(1), contrast(1), saturate(1), opacity(1),
            // blur(0), grayscale(0), sepia(0), invert(0), hue-rotate(0deg)) are
            // no-ops by the CSS spec — they neither change pixels nor introduce
            // a stacking context that affects compositing semantics worth
            // preserving here. Dropping them from the chain avoids pushing a
            // filter scope (and the offscreen RT round-trip in the URP backend)
            // for an effect that produces zero visual change. Crucially, this
            // matters for `animation: ... { 0%, 100% filter: brightness(1) }`
            // keyframes — without this skip, the wrapper's RT compositing path
            // (which has a separate bug under the batched URP backend) renders
            // the element invisibly at the keyframe endpoints.
            switch (fn.Name) {
                case "blur": {
                    var f = ParseBlur(fn, ctx);
                    if (f.RadiusPx > 0) output.Add(f);
                    return;
                }
                case "brightness": {
                    double a = ParseAmount(fn);
                    if (a != 1.0) output.Add(new BrightnessFilter(a));
                    return;
                }
                case "contrast": {
                    double a = ParseAmount(fn);
                    if (a != 1.0) output.Add(new ContrastFilter(a));
                    return;
                }
                case "grayscale": {
                    double a = ParseAmount(fn);
                    if (a != 0.0) output.Add(new GrayscaleFilter(a));
                    return;
                }
                case "opacity": {
                    double a = ParseAmount(fn);
                    if (a != 1.0) output.Add(new OpacityFilter(a));
                    return;
                }
                case "saturate": {
                    double a = ParseAmount(fn);
                    if (a != 1.0) output.Add(new SaturateFilter(a));
                    return;
                }
                case "hue-rotate": {
                    double d = ParseAngleDegrees(fn);
                    if (d != 0.0) output.Add(new HueRotateFilter(d));
                    return;
                }
                case "invert": {
                    double a = ParseAmount(fn);
                    if (a != 0.0) output.Add(new InvertFilter(a));
                    return;
                }
                case "sepia": {
                    double a = ParseAmount(fn);
                    if (a != 0.0) output.Add(new SepiaFilter(a));
                    return;
                }
                case "drop-shadow": output.Add(ParseDropShadow(fn, ctx, currentColor)); return;
            }
            throw new FilterParseException("Unknown filter function '" + fn.Name + "'");
        }

        // blur() takes a single <length> (or unit-less 0).
        static BlurFilter ParseBlur(CssFunctionCall fn, LengthContext ctx) {
            if (fn.Arguments == null || fn.Arguments.Count != 1) {
                throw new FilterParseException("blur() requires exactly one length argument");
            }
            var a = fn.Arguments[0];
            if (a is CssLength len) return new BlurFilter(len.ToPixels(ctx));
            if (a is CssNumber num && num.Value == 0) return new BlurFilter(0);
            throw new FilterParseException("blur() requires a <length> argument (e.g. blur(5px))");
        }

        // <number-percentage> functions (brightness, contrast, grayscale, ...).
        static double ParseAmount(CssFunctionCall fn) {
            if (fn.Arguments == null || fn.Arguments.Count != 1) {
                throw new FilterParseException(fn.Name + "() requires exactly one number or percentage");
            }
            var a = fn.Arguments[0];
            if (a is CssNumber n) return n.Value;
            if (a is CssPercentage p) return p.Value * 0.01;
            throw new FilterParseException(fn.Name + "() requires a <number> or <percentage> (got '" + (a?.Raw ?? "") + "')");
        }

        // hue-rotate() takes an <angle> or unit-less number (= degrees).
        static double ParseAngleDegrees(CssFunctionCall fn) {
            if (fn.Arguments == null || fn.Arguments.Count != 1) {
                throw new FilterParseException(fn.Name + "() requires exactly one angle argument");
            }
            var a = fn.Arguments[0];
            if (a is CssNumber num) return num.Value;
            // The CSS parser turns angles (deg/turn/rad/grad) into
            // CssLength with the matching unit. Map back to degrees here.
            if (a is CssLength len) {
                switch (len.Unit) {
                    case CssLengthUnit.Px:
                        // Bare unit-less 0 sometimes parses as 0px; treat as 0deg.
                        if (len.Value == 0) return 0;
                        break;
                }
                // Unknown unit → fall through and error.
            }
            // The current value parser doesn't yet model deg/rad/turn/grad
            // as CssLength; for unit-bearing angles the raw text comes through
            // as a CssIdentifier or similar — fall back to the legacy
            // string-based parser so we don't regress hue-rotate(45deg).
            if (a != null && a.Raw != null && RawValueParser.TryParseAngleDegrees(a.Raw, out double deg)) {
                return deg;
            }
            throw new FilterParseException(fn.Name + "() requires an <angle>");
        }

        // drop-shadow(<length> <length> [<length>] [<color>]). Arguments inside
        // the parser come through as a single space-separated CssValueList when
        // there are no top-level commas, or as a bare CssLength for the
        // one-token edge case (which is never valid here but kept defensive).
        static DropShadowFilter ParseDropShadow(CssFunctionCall fn, LengthContext ctx, LinearColor currentColor) {
            if (fn.Arguments == null || fn.Arguments.Count == 0) {
                throw new FilterParseException("drop-shadow() requires at least <offset-x> and <offset-y>");
            }
            // The parser packs all space-separated values into a single
            // argument slot (no top-level commas → one segment). Unpack it
            // here so the walk below sees a flat list of CssValues.
            IReadOnlyList<CssValue> items;
            if (fn.Arguments.Count == 1 && fn.Arguments[0] is CssValueList spaceList
                && spaceList.Separator == CssValueListSeparator.Space) {
                items = spaceList.Items;
            } else {
                items = fn.Arguments;
            }

            double? offX = null, offY = null, blur = null;
            LinearColor color = currentColor;
            bool sawColor = false;

            for (int i = 0; i < items.Count; i++) {
                var item = items[i];
                if (TryReadLength(item, ctx, out var px)) {
                    if (offX == null) offX = px;
                    else if (offY == null) offY = px;
                    else if (blur == null) blur = px;
                    else throw new FilterParseException("drop-shadow(): too many length arguments");
                    continue;
                }
                if (ColorResolver.TryResolveParsed(item, currentColor, null, out var c)) {
                    if (sawColor) throw new FilterParseException("drop-shadow(): multiple colors");
                    color = c;
                    sawColor = true;
                    continue;
                }
                throw new FilterParseException("drop-shadow(): unexpected token '" + (item?.Raw ?? "") + "'");
            }

            if (offX == null || offY == null) {
                throw new FilterParseException("drop-shadow() requires at least <offset-x> and <offset-y>");
            }
            return new DropShadowFilter(offX.Value, offY.Value, blur ?? 0.0, color);
        }

        static bool TryReadLength(CssValue v, LengthContext ctx, out double pixels) {
            pixels = 0;
            if (v is CssLength len) { pixels = len.ToPixels(ctx); return true; }
            // Unit-less 0 is accepted per the legacy FilterParser behaviour.
            if (v is CssNumber num && num.Value == 0) { pixels = 0; return true; }
            if (v is CssCalc calc) {
                try { pixels = calc.Evaluate(ctx); return true; } catch { return false; }
            }
            return false;
        }

        static bool IsNone(CssValue v) {
            if (v is CssKeyword k && k.Identifier == "none") return true;
            if (v is CssIdentifier id && CssStringUtil.EqualsIgnoreCase(id.Name, "none")) return true;
            return false;
        }
    }
}
