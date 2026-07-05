using System;
using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Css.Values;

namespace Weva.Paint.Conversion {
    // text-shadow value parser. CSS Text Decoration §6 grammar:
    //   text-shadow: none | <shadow-list>
    //   <shadow-list> = <shadow># where <shadow> = <length>{2,3} && <color>?
    // Component-by-component the same shape as box-shadow minus `inset` and
    // `spread`. Reads the parse tree directly via ComputedStyle.GetParsed so
    // we skip the string→CssValue.TryParse round trip (and the raw token
    // splitting it used to need) on every hot-path call.
    internal static class TextShadowResolver {
        public static TextShadow[] ResolveTextShadow(ComputedStyle style, LengthContext ctx) {
            if (style == null) return Array.Empty<TextShadow>();
            var parsed = style.GetParsed(CssProperties.TextShadowId);
            if (parsed == null) return Array.Empty<TextShadow>();
            if (IsNone(parsed)) return Array.Empty<TextShadow>();

            var current = ColorResolver.ResolveCurrentColor(style);
            var list = new List<TextShadow>();
            AppendShadows(parsed, ctx, current, style, list);
            return list.ToArray();
        }

        // Pool-aware overload: appends shadows into `output`. Hot-path callers
        // (BoxToPaintConverter / TextRunResolver) pre-Clear() the buffer
        // before calling this. Mirrors BoxShadowResolver.ResolveBoxShadowInto.
        public static bool ResolveTextShadowInto(ComputedStyle style, LengthContext ctx, List<TextShadow> output) {
            if (output == null || style == null) return false;
            var parsed = style.GetParsed(CssProperties.TextShadowId);
            if (parsed == null) return false;
            if (IsNone(parsed)) return false;

            var current = ColorResolver.ResolveCurrentColor(style);
            int before = output.Count;
            AppendShadows(parsed, ctx, current, style, output);
            return output.Count > before;
        }

        // The parse tree for `text-shadow` is one of:
        //   - CssKeyword/CssIdentifier "none"         → no shadows
        //   - CssValueList(Comma)                     → multi-shadow list
        //   - CssValueList(Space) / single CssValue   → exactly one shadow
        static void AppendShadows(CssValue parsed, LengthContext ctx, LinearColor currentColor, ComputedStyle style, List<TextShadow> output) {
            if (parsed is CssValueList list && list.Separator == CssValueListSeparator.Comma) {
                for (int i = 0; i < list.Items.Count; i++) {
                    if (TryParseSingle(list.Items[i], ctx, currentColor, style, out var sh)) output.Add(sh);
                }
                return;
            }
            if (TryParseSingle(parsed, ctx, currentColor, style, out var only)) output.Add(only);
        }

        static bool IsNone(CssValue v) {
            if (v is CssKeyword k) return k.Identifier == "none";
            if (v is CssIdentifier id) return string.Equals(id.Name, "none", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        // Per CSS Text Decoration §6, a single <shadow> is at least two
        // <length>s (offset-x, offset-y) plus optional blur and optional
        // <color>. The color may appear before or after the lengths.
        static bool TryParseSingle(CssValue seg, LengthContext ctx, LinearColor currentColor, ComputedStyle style, out TextShadow shadow) {
            shadow = default;
            if (seg == null) return false;

            // A single shadow with a single component is invalid even if it
            // parses as a length — the grammar requires two offsets.
            if (!(seg is CssValueList list) || list.Separator != CssValueListSeparator.Space) {
                return false;
            }

            // Walk the space-separated items: lengths feed offset-x/y/blur in
            // order; the last color-like token wins (matches the prior raw-
            // token-walk behavior where the loop overwrote `colorToken` on
            // each color hit).
            LinearColor color = currentColor;
            int lengthCount = 0;
            double offX = 0, offY = 0, blur = 0;
            for (int i = 0; i < list.Items.Count; i++) {
                var item = list.Items[i];
                if (TryResolveColor(item, currentColor, style, out var c)) {
                    color = c;
                    continue;
                }
                if (TryResolveLength(item, ctx, out var v)) {
                    if (lengthCount == 0) offX = v;
                    else if (lengthCount == 1) offY = v;
                    else if (lengthCount == 2) blur = v;
                    lengthCount++;
                    continue;
                }
                // Unknown token: skip (matches the prior best-effort behavior
                // where unparseable tokens were silently ignored).
            }
            if (lengthCount < 2) return false;
            // CSS spec: blur-radius cannot be negative; clamp at 0.
            if (blur < 0) blur = 0;

            shadow = new TextShadow(offX, offY, blur, color);
            return true;
        }

        // Color tokens that ColorResolver.TryResolveParsed handles directly:
        // CssColor (hex / rgb() / hsl() / etc.), CssKeyword (currentcolor /
        // transparent), CssIdentifier-named-color. Anything else is not a
        // color.
        static bool TryResolveColor(CssValue v, LinearColor currentColor, ComputedStyle style, out LinearColor result) {
            return ColorResolver.TryResolveParsed(v, currentColor, style, out result);
        }

        static bool TryResolveLength(CssValue v, LengthContext ctx, out double pixels) {
            pixels = 0;
            if (v is CssLength len) { pixels = len.ToPixels(ctx); return true; }
            if (v is CssNumber num) { pixels = num.Value; return true; }
            if (v is CssCalc calc) {
                try { pixels = calc.Evaluate(ctx); return true; } catch { return false; }
            }
            return false;
        }
    }
}
