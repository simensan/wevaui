using System;
using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Css.Values;

namespace Weva.Paint.Conversion {
    internal static class BorderRadiiResolver {
        // Reused scratch buffers for shorthand parsing. ResolveBorderRadii is
        // called per painted box per frame; keeping the per-side token lists
        // alive between calls avoids the steady-state list allocation that
        // would otherwise dominate this resolver. The resolver runs on the
        // paint thread which is single-threaded relative to itself, so a
        // [ThreadStatic] field isn't required, but we keep the buffers
        // private and reset them on entry to defend against re-entrancy.
        static readonly List<CssValue> sXBuf = new List<CssValue>(4);
        static readonly List<CssValue> sYBuf = new List<CssValue>(4);

        public static BorderRadii ResolveBorderRadii(ComputedStyle style, LengthContext ctx, Rect bounds) {
            if (style == null) return BorderRadii.Zero;

            // Per-corner longhands read directly from the per-style parsed
            // cache. Each corner is either a single <length-percentage> or a
            // CssValueList of two for the elliptical "rx ry" form.
            var tl = ResolveCorner(style, CssProperties.BorderTopLeftRadiusId, ctx, bounds);
            var tr = ResolveCorner(style, CssProperties.BorderTopRightRadiusId, ctx, bounds);
            var br = ResolveCorner(style, CssProperties.BorderBottomRightRadiusId, ctx, bounds);
            var bl = ResolveCorner(style, CssProperties.BorderBottomLeftRadiusId, ctx, bounds);

            // Fall back to shorthand if all four corners are at initial 0.
            if (tl.IsZero && tr.IsZero && br.IsZero && bl.IsZero) {
                var shorthand = style.GetParsed(CssProperties.BorderRadiusId);
                if (shorthand != null) {
                    var (sTl, sTr, sBr, sBl) = ResolveShorthand(shorthand, ctx, bounds);
                    tl = sTl; tr = sTr; br = sBr; bl = sBl;
                }
            }

            // Clamp per CSS spec: scale all radii by the smallest factor f <= 1 so that no edge is overrun.
            var radii = new BorderRadii(tl, tr, br, bl);
            return ClampToBounds(radii, bounds);
        }

        static CornerRadius ResolveCorner(ComputedStyle style, int propertyId, LengthContext ctx, Rect bounds) {
            var parsed = style.GetParsed(propertyId);
            if (parsed == null) return new CornerRadius(0, 0);
            // Elliptical "rx ry" — a space-separated CssValueList.
            if (parsed is CssValueList list && list.Separator == CssValueListSeparator.Space && list.Items.Count >= 2) {
                double rx = ResolveAxis(list.Items[0], ctx, bounds.Width);
                double ry = ResolveAxis(list.Items[1], ctx, bounds.Height);
                return new CornerRadius(rx, ry);
            }
            // Single value applied to both axes. For percentages this re-resolves
            // against width vs. height per CSS Backgrounds & Borders L3 §5.
            double r = ResolveAxis(parsed, ctx, bounds.Width);
            double r2 = ResolveAxis(parsed, ctx, bounds.Height);
            return new CornerRadius(r, r2);
        }

        // Resolves a single parsed <length-percentage> against the given axis
        // basis. Mirrors the old ResolveAxis(string,…) path but skips the
        // CssValue.TryParse round-trip — the parse tree is already in hand.
        static double ResolveAxis(CssValue v, LengthContext ctx, double basisPx) {
            if (v == null) return 0;
            if (v is CssLength len) {
                var c = ctx;
                c.BasisPixels = basisPx;
                return Math.Max(0, len.ToPixels(c));
            }
            if (v is CssPercentage p) return Math.Max(0, basisPx * p.Value * 0.01);
            if (v is CssNumber n) return Math.Max(0, n.Value);
            if (v is CssCalc calc) {
                try {
                    var c = ctx;
                    c.BasisPixels = basisPx;
                    return Math.Max(0, calc.Evaluate(c));
                } catch { return 0; }
            }
            return 0;
        }

        // Shorthand syntax: `<x-radii> [ / <y-radii> ]` where each side is 1-4
        // <length-percentage>s. The parser produces:
        //   - "10px"                 → single CssLength
        //   - "10px 20px 30px 40px"  → CssValueList(space, 4 items)
        //   - "10px / 20px"          → CssValueList(space, [Length, Ident("/"), Length])
        // We split the list at the "/" identifier; absent slash means y-radii
        // repeat the x token list against the height axis (re-resolving %).
        static (CornerRadius, CornerRadius, CornerRadius, CornerRadius) ResolveShorthand(CssValue parsed, LengthContext ctx, Rect bounds) {
            sXBuf.Clear();
            sYBuf.Clear();

            if (parsed is CssValueList list && list.Separator == CssValueListSeparator.Space) {
                bool inY = false;
                for (int i = 0; i < list.Items.Count; i++) {
                    var item = list.Items[i];
                    if (!inY && item is CssIdentifier id && id.Name == "/") {
                        inY = true;
                        continue;
                    }
                    (inY ? sYBuf : sXBuf).Add(item);
                }
            } else {
                // Single value: applies to all four corners on both axes.
                sXBuf.Add(parsed);
            }

            ExpandFour(sXBuf, ctx, bounds.Width, out double xTl, out double xTr, out double xBr, out double xBl);
            // Omitted y-radii: re-resolve the same token list against the height axis.
            ExpandFour(sYBuf.Count > 0 ? sYBuf : sXBuf, ctx, bounds.Height,
                out double yTl, out double yTr, out double yBr, out double yBl);
            return (
                new CornerRadius(xTl, yTl),
                new CornerRadius(xTr, yTr),
                new CornerRadius(xBr, yBr),
                new CornerRadius(xBl, yBl)
            );
        }

        // CSS Backgrounds & Borders L3 §5.5: 1 value = all; 2 = (tl-br, tr-bl);
        // 3 = (tl, tr-bl, br); 4 = (tl, tr, br, bl). Out-params instead of a
        // returned `double[4]` so the per-style-version-bump shorthand
        // resolution doesn't allocate a fresh heap array each call.
        static void ExpandFour(List<CssValue> parts, LengthContext ctx, double basisPx,
                               out double tl, out double tr, out double br, out double bl) {
            tl = parts.Count > 0 ? ResolveAxis(parts[0], ctx, basisPx) : 0;
            tr = parts.Count > 1 ? ResolveAxis(parts[1], ctx, basisPx) : tl;
            br = parts.Count > 2 ? ResolveAxis(parts[2], ctx, basisPx) : tl;
            bl = parts.Count > 3 ? ResolveAxis(parts[3], ctx, basisPx) : tr;
        }

        static BorderRadii ClampToBounds(BorderRadii r, Rect bounds) {
            double w = Math.Max(0, bounds.Width);
            double h = Math.Max(0, bounds.Height);
            double topSum = r.TopLeft.XRadius + r.TopRight.XRadius;
            double bottomSum = r.BottomLeft.XRadius + r.BottomRight.XRadius;
            double leftSum = r.TopLeft.YRadius + r.BottomLeft.YRadius;
            double rightSum = r.TopRight.YRadius + r.BottomRight.YRadius;
            double f = 1.0;
            if (topSum > w) f = Math.Min(f, w / topSum);
            if (bottomSum > w) f = Math.Min(f, w / bottomSum);
            if (leftSum > h) f = Math.Min(f, h / leftSum);
            if (rightSum > h) f = Math.Min(f, h / rightSum);
            if (f >= 1.0) return r;
            return new BorderRadii(
                Scale(r.TopLeft, f),
                Scale(r.TopRight, f),
                Scale(r.BottomRight, f),
                Scale(r.BottomLeft, f)
            );
        }

        static CornerRadius Scale(CornerRadius c, double f) {
            return new CornerRadius(c.XRadius * f, c.YRadius * f);
        }
    }
}
