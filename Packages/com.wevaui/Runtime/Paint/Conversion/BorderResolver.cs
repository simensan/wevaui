using Weva.Css.Cascade;
using Weva.Css.Values;

namespace Weva.Paint.Conversion {
    internal static class BorderResolver {
        // Per-style Borders memo. Borders is a struct of 4 BorderEdges (style
        // + width + color each). Resolving four edges via GetParsed +
        // ColorResolver.TryResolveParsed per cache miss is alloc-free but
        // costs ~12 typed dispatch hits per bordered box. Single-slot last-
        // seen cache: same style is queried only once per EmitDecorations
        // call, but typical scenes have many sibling boxes sharing the same
        // resolved style (4 identical buttons, 16 same-color gem tiles, …).
        // Key includes ctx.BaseFontSizePx so em/rem border widths refresh
        // when the inherited font-size changes without bumping style.Version.
        static ComputedStyle s_LastStyle;
        static long s_LastVersion;
        static double s_LastFontSizePx;
        static Borders s_LastBorders;

        public static Borders ResolveBorders(ComputedStyle style, LengthContext ctx) {
            if (style == null) return Borders.None;
            if (ReferenceEquals(style, s_LastStyle)
                && style.Version == s_LastVersion
                && ctx.BaseFontSizePx == s_LastFontSizePx) {
                return s_LastBorders;
            }
            var current = ColorResolver.ResolveCurrentColor(style);
            // Reads go through ComputedStyle.GetParsed so each per-edge
            // property pays a single O(1) array lookup against the cached
            // parse tree, instead of a Dictionary probe + CssValue.TryParse
            // round trip through the global string cache. Saves ~12 alloc-
            // free lookups per bordered box vs the prior style.Get path.
            var top = ResolveEdge(style, CssProperties.BorderTopStyleId, CssProperties.BorderTopWidthId, CssProperties.BorderTopColorId, ctx, current);
            var right = ResolveEdge(style, CssProperties.BorderRightStyleId, CssProperties.BorderRightWidthId, CssProperties.BorderRightColorId, ctx, current);
            var bottom = ResolveEdge(style, CssProperties.BorderBottomStyleId, CssProperties.BorderBottomWidthId, CssProperties.BorderBottomColorId, ctx, current);
            var left = ResolveEdge(style, CssProperties.BorderLeftStyleId, CssProperties.BorderLeftWidthId, CssProperties.BorderLeftColorId, ctx, current);
            var borders = new Borders(top, right, bottom, left);
            s_LastStyle = style;
            s_LastVersion = style.Version;
            s_LastFontSizePx = ctx.BaseFontSizePx;
            s_LastBorders = borders;
            return borders;
        }

        static BorderEdge ResolveEdge(ComputedStyle style, int styleId, int widthId, int colorId, LengthContext ctx, LinearColor currentColor) {
            BorderStyle bs = ParseStyle(style.GetParsed(styleId));
            if (bs == BorderStyle.None) return BorderEdge.None;
            // Hidden in the separate-borders model renders as invisible. We still
            // resolve the width and color so the collapsed-border winner rule can
            // compare widths; but the edge carries Hidden as style so the winner
            // resolver can distinguish it from a zero-width None.
            double width = ResolveWidth(style.GetParsed(widthId), ctx);
            if (bs == BorderStyle.Hidden) {
                // Return the Hidden edge with resolved width so CollapsedBorderWinnerResolver
                // can apply the §17.6.2.1 "hidden always wins" rule. The paint
                // backends skip it (Hidden is treated like None in DrawEdge).
                LinearColor hiddenColor = currentColor;
                var parsedHiddenColor = style.GetParsed(colorId);
                if (parsedHiddenColor != null && ColorResolver.TryResolveParsed(parsedHiddenColor, currentColor, style, out var hc)) {
                    hiddenColor = hc;
                }
                return new BorderEdge(BorderStyle.Hidden, width, hiddenColor);
            }
            if (width <= 0) return BorderEdge.None;
            LinearColor color = currentColor;
            var parsedColor = style.GetParsed(colorId);
            if (parsedColor != null && ColorResolver.TryResolveParsed(parsedColor, currentColor, style, out var c)) {
                color = c;
            }
            return new BorderEdge(bs, width, color);
        }

        // Exposed as internal so EmitColumnRules (BoxToPaintConverter) can
        // map a column-rule-style keyword string to the BorderStyle enum
        // without duplicating the keyword table.
        internal static BorderStyle ParseBorderStyle(string name) {
            if (string.IsNullOrEmpty(name)) return BorderStyle.None;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "none")) return BorderStyle.None;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "hidden")) return BorderStyle.Hidden;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "solid")) return BorderStyle.Solid;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "dashed")) return BorderStyle.Dashed;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "dotted")) return BorderStyle.Dotted;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "double")) return BorderStyle.Double;
            return BorderStyle.Solid;
        }

        static BorderStyle ParseStyle(CssValue parsed) {
            if (parsed == null) return BorderStyle.None;
            // Border-style is a keyword grammar; the parser maps it to
            // CssKeyword (recognized) or CssIdentifier (unknown name).
            // Both expose the token via .Raw, which is already lowercased
            // for CssKeyword — alloc-free dispatch.
            string name = null;
            if (parsed is CssKeyword k) name = k.Identifier;
            else if (parsed is CssIdentifier id) name = id.Name;
            else name = parsed.Raw;
            if (string.IsNullOrEmpty(name)) return BorderStyle.None;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "none")) return BorderStyle.None;
            // Preserve hidden as its own enum value so the collapsed-border winner
            // rule (CollapsedBorderWinnerResolver) can distinguish it from none.
            // In the separate-borders model ResolveEdge still returns BorderEdge.None
            // for Hidden because the width-zero guard below treats it as invisible.
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "hidden")) return BorderStyle.Hidden;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "solid")) return BorderStyle.Solid;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "dashed")) return BorderStyle.Dashed;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "dotted")) return BorderStyle.Dotted;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "double")) return BorderStyle.Double;
            return BorderStyle.Solid;
        }

        static double ResolveWidth(CssValue parsed, LengthContext ctx) {
            if (parsed == null) return 0;
            // CssLength is the overwhelmingly common case (px / em / rem),
            // so it's checked first. CssNumber covers unit-less widths
            // (technically invalid CSS but the old path accepted them, so
            // parity is preserved). The thin/medium/thick keywords go
            // through CssKeyword / CssIdentifier — same dispatch as
            // ParseStyle.
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
    }
}
