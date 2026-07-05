using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Layout.Boxes;

namespace Weva.Paint.Conversion {
    // Resolves CSS `background-clip` and `background-origin` keywords into
    // box-local rectangles used by the painter. Both default to their
    // CSS-spec defaults (clip = border-box, origin = padding-box).
    //
    // Coords are box-local: (0,0) at the box's top-left, expressed in
    // border-box pixel space. The converter passes the resulting paint
    // rect as the FillRectCommand bounds and uses the origin rect when
    // resolving tile position so author CSS like
    // `background-position: center` lines up against the padding-box
    // (the spec-compliant default).
    internal static class BackgroundClipOrigin {
        public enum Box { BorderBox, PaddingBox, ContentBox }

        public static Box ParseBox(string raw, Box fallback) {
            if (string.IsNullOrEmpty(raw)) return fallback;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "border-box")) return Box.BorderBox;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "padding-box")) return Box.PaddingBox;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "content-box")) return Box.ContentBox;
            return fallback;
        }

        // Returns the box-local rect for the given clip/origin keyword.
        // Border-box = (0, 0, box.Width, box.Height).
        // Padding-box = inset by border widths.
        // Content-box = inset by border + padding.
        public static Rect RectFor(Box kind, Weva.Layout.Boxes.Box box) {
            if (box == null) return new Rect(0, 0, 0, 0);
            switch (kind) {
                case Box.PaddingBox: {
                    double x = box.BorderLeft;
                    double y = box.BorderTop;
                    double w = System.Math.Max(0, box.Width - box.BorderLeft - box.BorderRight);
                    double h = System.Math.Max(0, box.Height - box.BorderTop - box.BorderBottom);
                    return new Rect(x, y, w, h);
                }
                case Box.ContentBox: {
                    double x = box.BorderLeft + box.PaddingLeft;
                    double y = box.BorderTop + box.PaddingTop;
                    double w = System.Math.Max(0, box.Width - box.BorderLeft - box.BorderRight - box.PaddingLeft - box.PaddingRight);
                    double h = System.Math.Max(0, box.Height - box.BorderTop - box.BorderBottom - box.PaddingTop - box.PaddingBottom);
                    return new Rect(x, y, w, h);
                }
                default:
                    return new Rect(0, 0, box.Width, box.Height);
            }
        }

        // Reads `background-clip` and `background-origin` from the
        // ComputedStyle and returns the matching rects.
        //
        // Multi-layer values: per CSS Backgrounds 3 each layer can carry its
        // own clip/origin, but the painter currently emits one FillRect per
        // layer using the same paintRect — so we honor the FIRST layer's
        // values (treating subsequent layers as inheriting the topmost layer's
        // box). True per-layer clip rects need a parallel rect list threaded
        // through ResolveBackgroundLayersInto.
        public static void Resolve(ComputedStyle style, Weva.Layout.Boxes.Box box,
                                   out Rect paintRect, out Rect originRect) {
            var clip = ParseBox(FirstLayer(style?.Get(CssProperties.BackgroundClipId)), Box.BorderBox);
            var origin = ParseBox(FirstLayer(style?.Get(CssProperties.BackgroundOriginId)), Box.PaddingBox);
            paintRect = RectFor(clip, box);
            originRect = RectFor(origin, box);
        }

        // Picks the first layer's value out of a comma-joined longhand
        // (e.g. "padding-box, content-box" → "padding-box"). Returns the
        // input untouched when there's no comma.
        static string FirstLayer(string raw) {
            if (string.IsNullOrEmpty(raw)) return raw;
            int comma = -1;
            int depth = 0;
            for (int i = 0; i < raw.Length; i++) {
                char c = raw[i];
                if (c == '(') depth++;
                else if (c == ')') { if (depth > 0) depth--; }
                else if (c == ',' && depth == 0) { comma = i; break; }
            }
            return comma < 0 ? raw : raw.Substring(0, comma).Trim();
        }
    }
}
