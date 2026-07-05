using Weva.Css.Cascade;
using Weva.Css.Values;

namespace Weva.Paint.Conversion {
    // CSS UI 4 §7: resolves outline-{style,width,color} + outline-offset for a
    // computed style. Mirrors BorderResolver.ResolveEdge but stays a single
    // (uniform) edge — outline does not have per-side longhands. `invert`
    // (initial color) has no v1 primitive, so we approximate it by falling
    // through to currentColor; the focus ring still shows for the a11y baseline.
    internal static class OutlineResolver {
        // Per-style outline memo. Same single-slot pattern as BorderResolver
        // — outline rarely uses em/rem so the (style, version, fontSizePx)
        // tuple captures the result across cache misses on sibling boxes
        // that share the same focus/hover state.
        static ComputedStyle s_LastStyle;
        static long s_LastVersion;
        static double s_LastFontSizePx;
        static bool s_LastHasOutline;
        static BorderEdge s_LastEdge;
        static double s_LastOffset;

        public static bool TryResolve(ComputedStyle style, LengthContext ctx, out BorderEdge edge, out double offset) {
            edge = BorderEdge.None;
            offset = 0;
            if (style == null) return false;
            if (ReferenceEquals(style, s_LastStyle)
                && style.Version == s_LastVersion
                && ctx.BaseFontSizePx == s_LastFontSizePx) {
                edge = s_LastEdge;
                offset = s_LastOffset;
                return s_LastHasOutline;
            }

            // Each of the four longhands now reads from the per-style parsed
            // cache (ComputedStyle.GetParsed) — one O(1) array lookup against
            // the slot, no dictionary probe and no CssValue.TryParse round
            // trip through the global string cache. Mirrors BorderResolver's
            // per-edge dispatch.
            BorderStyle bs = ParseStyle(style.GetParsed(CssProperties.OutlineStyleId));
            if (bs == BorderStyle.None) {
                StoreMiss(style, ctx);
                return false;
            }

            double width = ResolveWidth(style.GetParsed(CssProperties.OutlineWidthId), ctx);
            if (width <= 0) {
                StoreMiss(style, ctx);
                return false;
            }

            var current = ColorResolver.ResolveCurrentColor(style);
            LinearColor color = current;
            var parsedColor = style.GetParsed(CssProperties.OutlineColorId);
            // CSS UI 4: `invert` is the initial color and has no equivalent in
            // a v1 paint primitive. Detect it via the typed parse tree (keyword
            // or identifier shape) and fall through to currentColor — same
            // behaviour the prior raw-string path encoded.
            if (parsedColor != null && !IsInvert(parsedColor)
                && ColorResolver.TryResolveParsed(parsedColor, current, style, out var c)) {
                color = c;
            }

            offset = ResolveLength(style.GetParsed(CssProperties.OutlineOffsetId), ctx);
            edge = new BorderEdge(bs, width, color);

            s_LastStyle = style;
            s_LastVersion = style.Version;
            s_LastFontSizePx = ctx.BaseFontSizePx;
            s_LastHasOutline = true;
            s_LastEdge = edge;
            s_LastOffset = offset;
            return true;
        }

        static void StoreMiss(ComputedStyle style, LengthContext ctx) {
            s_LastStyle = style;
            s_LastVersion = style.Version;
            s_LastFontSizePx = ctx.BaseFontSizePx;
            s_LastHasOutline = false;
            s_LastEdge = BorderEdge.None;
            s_LastOffset = 0;
        }

        static BorderStyle ParseStyle(CssValue parsed) {
            if (parsed == null) return BorderStyle.None;
            // outline-style is a keyword grammar; the parser maps it to
            // CssKeyword (recognized) or CssIdentifier (unknown name). Both
            // expose the token via .Identifier / .Name and are already
            // lowercased for CssKeyword — alloc-free dispatch.
            string name = null;
            if (parsed is CssKeyword k) name = k.Identifier;
            else if (parsed is CssIdentifier id) name = id.Name;
            else name = parsed.Raw;
            if (string.IsNullOrEmpty(name)) return BorderStyle.None;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "none")) return BorderStyle.None;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "hidden")) return BorderStyle.None;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "solid")) return BorderStyle.Solid;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "dashed")) return BorderStyle.Dashed;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "dotted")) return BorderStyle.Dotted;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "double")) return BorderStyle.Double;
            return BorderStyle.Solid;
        }

        static double ResolveWidth(CssValue parsed, LengthContext ctx) {
            if (parsed == null) return 0;
            // CssLength is the overwhelmingly common case (px / em / rem),
            // so it's checked first. The thin/medium/thick keyword
            // fallbacks go through CssKeyword / CssIdentifier — same
            // dispatch as ParseStyle. Mirrors BorderResolver.ResolveWidth.
            if (parsed is CssLength len) return len.ToPixels(ctx);
            if (parsed is CssNumber num) return num.Value;
            if (parsed is CssCalc calc) {
                try { return calc.Evaluate(ctx); } catch { return 0; }
            }
            string name = null;
            if (parsed is CssKeyword k) name = k.Identifier;
            else if (parsed is CssIdentifier id) name = id.Name;
            if (string.IsNullOrEmpty(name)) return 0;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "thin")) return 1;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "medium")) return 3;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "thick")) return 5;
            return 0;
        }

        static double ResolveLength(CssValue parsed, LengthContext ctx) {
            if (parsed == null) return 0;
            if (parsed is CssLength len) return len.ToPixels(ctx);
            if (parsed is CssNumber num) return num.Value;
            if (parsed is CssCalc calc) {
                try { return calc.Evaluate(ctx); } catch { return 0; }
            }
            return 0;
        }

        static bool IsInvert(CssValue parsed) {
            if (parsed is CssKeyword k) return k.Identifier == "invert";
            if (parsed is CssIdentifier id) return CssStringUtil.EqualsIgnoreCase(id.Name, "invert");
            return false;
        }
    }
}
