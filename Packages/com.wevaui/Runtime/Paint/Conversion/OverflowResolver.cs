using Weva.Css.Cascade;
using Weva.Css.Values;

namespace Weva.Paint.Conversion {
    // CSS Overflow L4 §6: `overflow-clip-margin` is `<visual-box>? <length [0,∞]>?`
    // and `<visual-box>` is one of `content-box | padding-box | border-box`. The
    // keyword chooses the reference edge from which the length inflates.
    internal enum OverflowClipMarginBox {
        PaddingBox = 0, // spec default
        ContentBox = 1,
        BorderBox = 2,
    }

    internal static class OverflowResolver {
        public static bool ShouldClip(ComputedStyle style) {
            if (style == null) return false;
            // All three reads go through the per-style parsed cache
            // (ComputedStyle.GetParsed) instead of the raw-string path.
            // Each is a single keyword grammar, so the dispatch is alloc-
            // free pattern matching on CssKeyword / CssIdentifier.
            return ClipsValue(style.GetParsed(CssProperties.OverflowId))
                || ClipsValue(style.GetParsed(CssProperties.OverflowXId))
                || ClipsValue(style.GetParsed(CssProperties.OverflowYId));
        }

        // CSS Overflow L4 §6: `overflow-clip-margin` only takes effect when
        // an `overflow: clip` axis is active. `hidden` / `scroll` / `auto`
        // clip at the padding box and ignore the margin.
        public static bool IsOverflowClip(ComputedStyle style) {
            if (style == null) return false;
            return IsClipKeyword(style.GetParsed(CssProperties.OverflowId))
                || IsClipKeyword(style.GetParsed(CssProperties.OverflowXId))
                || IsClipKeyword(style.GetParsed(CssProperties.OverflowYId));
        }

        public static double ResolveClipMargin(ComputedStyle style, LengthContext ctx) {
            return ResolveClipMarginSide(style, ctx, CssProperties.OverflowClipMarginId);
        }

        // CSS Overflow L4 §6 per-side longhands. When a side longhand is
        // explicitly set, it overrides the shorthand on that side; otherwise
        // the shorthand value applies to all four sides uniformly.
        public static double ResolveClipMarginTop(ComputedStyle style, LengthContext ctx) {
            return ResolveClipMarginSide(style, ctx, CssProperties.OverflowClipMarginTopId);
        }

        public static double ResolveClipMarginRight(ComputedStyle style, LengthContext ctx) {
            return ResolveClipMarginSide(style, ctx, CssProperties.OverflowClipMarginRightId);
        }

        public static double ResolveClipMarginBottom(ComputedStyle style, LengthContext ctx) {
            return ResolveClipMarginSide(style, ctx, CssProperties.OverflowClipMarginBottomId);
        }

        public static double ResolveClipMarginLeft(ComputedStyle style, LengthContext ctx) {
            return ResolveClipMarginSide(style, ctx, CssProperties.OverflowClipMarginLeftId);
        }

        // CSS Overflow L4 §6 `<visual-box>` accessor — defaults to padding-box
        // when the declaration omits the keyword (or no declaration is present).
        public static OverflowClipMarginBox ResolveClipMarginVisualBoxTop(ComputedStyle style) {
            return ResolveClipMarginVisualBoxSide(style, CssProperties.OverflowClipMarginTopId);
        }

        public static OverflowClipMarginBox ResolveClipMarginVisualBoxRight(ComputedStyle style) {
            return ResolveClipMarginVisualBoxSide(style, CssProperties.OverflowClipMarginRightId);
        }

        public static OverflowClipMarginBox ResolveClipMarginVisualBoxBottom(ComputedStyle style) {
            return ResolveClipMarginVisualBoxSide(style, CssProperties.OverflowClipMarginBottomId);
        }

        public static OverflowClipMarginBox ResolveClipMarginVisualBoxLeft(ComputedStyle style) {
            return ResolveClipMarginVisualBoxSide(style, CssProperties.OverflowClipMarginLeftId);
        }

        static double ResolveClipMarginSide(ComputedStyle style, LengthContext ctx, int sideId) {
            if (style == null) return 0;
            var parsed = style.GetParsed(sideId);
            if (parsed == null) {
                if (sideId == CssProperties.OverflowClipMarginId) return 0;
                parsed = style.GetParsed(CssProperties.OverflowClipMarginId);
                if (parsed == null) return 0;
            }
            // CSS Overflow L4 §6 grammar `<visual-box>? <length [0,∞]>?` —
            // when both tokens are present the parser yields a CssValueList.
            // Extract the length item; the keyword is consumed by
            // ResolveClipMarginVisualBoxSide.
            if (parsed is CssValueList list) {
                for (int i = 0; i < list.Items.Count; i++) {
                    var item = list.Items[i];
                    if (item is CssLength itemLen) return itemLen.ToPixels(ctx);
                    if (item is CssNumber itemNum) return itemNum.Value;
                    if (item is CssCalc itemCalc) {
                        try { return itemCalc.Evaluate(ctx); } catch { return 0; }
                    }
                }
                return 0;
            }
            if (parsed is CssLength len) return len.ToPixels(ctx);
            if (parsed is CssNumber num) return num.Value;
            if (parsed is CssCalc calc) {
                try { return calc.Evaluate(ctx); } catch { return 0; }
            }
            return 0;
        }

        static OverflowClipMarginBox ResolveClipMarginVisualBoxSide(ComputedStyle style, int sideId) {
            if (style == null) return OverflowClipMarginBox.PaddingBox;
            var parsed = style.GetParsed(sideId);
            if (parsed == null) {
                if (sideId == CssProperties.OverflowClipMarginId) return OverflowClipMarginBox.PaddingBox;
                parsed = style.GetParsed(CssProperties.OverflowClipMarginId);
                if (parsed == null) return OverflowClipMarginBox.PaddingBox;
            }
            if (parsed is CssValueList list) {
                for (int i = 0; i < list.Items.Count; i++) {
                    if (TryReadVisualBox(list.Items[i], out var box)) return box;
                }
                return OverflowClipMarginBox.PaddingBox;
            }
            if (TryReadVisualBox(parsed, out var single)) return single;
            return OverflowClipMarginBox.PaddingBox;
        }

        static bool TryReadVisualBox(CssValue v, out OverflowClipMarginBox box) {
            string name = null;
            if (v is CssKeyword k) name = k.Identifier;
            else if (v is CssIdentifier id) name = id.Name;
            if (!string.IsNullOrEmpty(name)) {
                if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "padding-box")) {
                    box = OverflowClipMarginBox.PaddingBox;
                    return true;
                }
                if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "content-box")) {
                    box = OverflowClipMarginBox.ContentBox;
                    return true;
                }
                if (CssStringUtil.EqualsIgnoreCaseTrimmed(name, "border-box")) {
                    box = OverflowClipMarginBox.BorderBox;
                    return true;
                }
            }
            box = OverflowClipMarginBox.PaddingBox;
            return false;
        }

        static bool ClipsValue(CssValue parsed) {
            if (parsed == null) return false;
            string name = null;
            if (parsed is CssKeyword k) name = k.Identifier;
            else if (parsed is CssIdentifier id) name = id.Name;
            if (string.IsNullOrEmpty(name)) return false;
            return CssStringUtil.EqualsIgnoreCaseTrimmed(name, "hidden")
                || CssStringUtil.EqualsIgnoreCaseTrimmed(name, "scroll")
                || CssStringUtil.EqualsIgnoreCaseTrimmed(name, "clip")
                || CssStringUtil.EqualsIgnoreCaseTrimmed(name, "auto");
        }

        static bool IsClipKeyword(CssValue parsed) {
            if (parsed == null) return false;
            string name = null;
            if (parsed is CssKeyword k) name = k.Identifier;
            else if (parsed is CssIdentifier id) name = id.Name;
            if (string.IsNullOrEmpty(name)) return false;
            return CssStringUtil.EqualsIgnoreCaseTrimmed(name, "clip");
        }
    }
}
