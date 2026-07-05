using System;
using System.Collections.Generic;
using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Values;

namespace Weva.Paint.Conversion {
    internal static class ClipPathResolver {
        public static ClipPathShape Resolve(ComputedStyle style, LengthContext ctx, Rect borderBox) {
            if (style == null) return null;
            string raw = style.Get(CssProperties.ClipPathId);
            if (string.IsNullOrWhiteSpace(raw) || CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "none")) return null;
            return TryParseBasicShape(raw.Trim(), ctx, borderBox, out var shape) ? shape : null;
        }

        public static bool IsSupportedValue(string raw) {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            raw = raw.Trim();
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "none")) return true;
            return RawValueParser.TryParseFunctionCall(raw, out var name, out _)
                && (name == "inset" || name == "circle" || name == "ellipse" || name == "polygon" || name == "xywh" || name == "path" || name == "shape");
        }

        static bool TryParseBasicShape(string raw, LengthContext ctx, Rect box, out ClipPathShape shape) {
            shape = null;
            if (!RawValueParser.TryParseFunctionCall(raw, out var name, out var body)) return false;
            switch (name) {
                case "inset": return TryParseInset(body, ctx, box, out shape);
                case "circle": return TryParseCircle(body, ctx, box, out shape);
                case "ellipse": return TryParseEllipse(body, ctx, box, out shape);
                case "polygon": return TryParsePolygon(body, ctx, box, out shape);
                case "xywh": return TryParseXywh(body, ctx, box, out shape);
                case "path": return TryParsePath(body, box, out shape);
                case "shape": return ShapeCommandParser.TryParse(body, ctx, box, out shape);
                default: return false;
            }
        }

        static bool TryParseInset(string body, LengthContext ctx, Rect box, out ClipPathShape shape) {
            shape = null;
            string args = body ?? "";
            string roundPart = null;
            int roundIdx = IndexOfRoundKeyword(args);
            if (roundIdx >= 0) {
                roundPart = args.Substring(roundIdx + "round".Length).Trim();
                args = args.Substring(0, roundIdx).Trim();
            }
            var tokens = SplitSpace(args);
            if (tokens.Count < 1 || tokens.Count > 4) return false;
            if (!ResolveBoxOffsets(tokens, ctx, box, out double top, out double right, out double bottom, out double left)) return false;
            var rect = new Rect(
                box.X + left,
                box.Y + top,
                Math.Max(0, box.Width - left - right),
                Math.Max(0, box.Height - top - bottom));
            BorderRadii radii = BorderRadii.Zero;
            if (!string.IsNullOrWhiteSpace(roundPart)) {
                radii = ParseRoundRadii(roundPart, ctx, rect);
            }
            shape = new InsetClipPathShape(rect, radii);
            return true;
        }

        // CSS Shapes L1 §3.1.1 / css-values-4 xywh() grammar:
        //   xywh( <x> <y> <w> <h> [ round <border-radius> ]? )
        // x, w resolved against reference-box width; y, h against height.
        // Mathematically equivalent to inset(y  refW-x-w  refH-y-h  x  round ...).
        // Per spec w and h must be clamped to >= 0 before computing insets.
        // Chrome allows the rect to extend beyond the reference box (negative
        // computed insets); InsetClipPathShape already supports negative insets
        // (the rect just protrudes outside), so no additional clamping needed there.
        static bool TryParseXywh(string body, LengthContext ctx, Rect box, out ClipPathShape shape) {
            shape = null;
            string args = body ?? "";
            string roundPart = null;
            int roundIdx = IndexOfRoundKeyword(args);
            if (roundIdx >= 0) {
                roundPart = args.Substring(roundIdx + "round".Length).Trim();
                args = args.Substring(0, roundIdx).Trim();
            }
            var tokens = SplitSpace(args);
            // Must have exactly 4 positional tokens: x y w h
            if (tokens.Count != 4) return false;
            // x resolves against reference-box width
            if (!TryResolveLengthPercentage(tokens[0], ctx, box.Width, out double x)) return false;
            // y resolves against reference-box height
            if (!TryResolveLengthPercentage(tokens[1], ctx, box.Height, out double y)) return false;
            // w resolves against reference-box width; spec clamps to >= 0
            if (!TryResolveLengthPercentage(tokens[2], ctx, box.Width, out double w)) return false;
            w = Math.Max(0, w);
            // h resolves against reference-box height; spec clamps to >= 0
            if (!TryResolveLengthPercentage(tokens[3], ctx, box.Height, out double h)) return false;
            h = Math.Max(0, h);
            // Map xywh onto inset: top=y, right=refW-x-w, bottom=refH-y-h, left=x
            double top    = y;
            double left   = x;
            double right  = box.Width  - x - w;
            double bottom = box.Height - y - h;
            var rect = new Rect(
                box.X + left,
                box.Y + top,
                Math.Max(0, box.Width  - left - right),
                Math.Max(0, box.Height - top  - bottom));
            BorderRadii radii = BorderRadii.Zero;
            if (!string.IsNullOrWhiteSpace(roundPart)) {
                radii = ParseRoundRadii(roundPart, ctx, rect);
            }
            shape = new InsetClipPathShape(rect, radii);
            return true;
        }

        static int IndexOfRoundKeyword(string text) {
            if (string.IsNullOrEmpty(text)) return -1;
            int depth = 0;
            for (int i = 0; i < text.Length; i++) {
                char c = text[i];
                if (c == '(') depth++;
                else if (c == ')' && depth > 0) depth--;
                if (depth != 0) continue;
                if (i + 5 <= text.Length
                    && string.Equals(text.Substring(i, 5), "round", StringComparison.OrdinalIgnoreCase)
                    && IsBoundary(text, i - 1)
                    && IsBoundary(text, i + 5)) {
                    return i;
                }
            }
            return -1;
        }

        static bool IsBoundary(string text, int index) {
            if (index < 0 || index >= text.Length) return true;
            char c = text[index];
            return char.IsWhiteSpace(c) || c == ',';
        }

        static BorderRadii ParseRoundRadii(string raw, LengthContext ctx, Rect rect) {
            var tokens = SplitSpace(raw);
            int slash = tokens.IndexOf("/");
            var xTokens = slash >= 0 ? tokens.GetRange(0, slash) : tokens;
            var yTokens = slash >= 0 ? tokens.GetRange(slash + 1, tokens.Count - slash - 1) : xTokens;
            ExpandFour(xTokens, ctx, rect.Width, out var xTl, out var xTr, out var xBr, out var xBl);
            ExpandFour(yTokens, ctx, rect.Height, out var yTl, out var yTr, out var yBr, out var yBl);
            return new BorderRadii(
                new CornerRadius(xTl, yTl),
                new CornerRadius(xTr, yTr),
                new CornerRadius(xBr, yBr),
                new CornerRadius(xBl, yBl));
        }

        static void ExpandFour(List<string> tokens, LengthContext ctx, double basis,
                               out double tl, out double tr, out double br, out double bl) {
            tl = tokens.Count > 0 && TryResolveLengthPercentage(tokens[0], ctx, basis, out var v0) ? v0 : 0;
            tr = tokens.Count > 1 && TryResolveLengthPercentage(tokens[1], ctx, basis, out var v1) ? v1 : tl;
            br = tokens.Count > 2 && TryResolveLengthPercentage(tokens[2], ctx, basis, out var v2) ? v2 : tl;
            bl = tokens.Count > 3 && TryResolveLengthPercentage(tokens[3], ctx, basis, out var v3) ? v3 : tr;
        }

        static bool ResolveBoxOffsets(List<string> tokens, LengthContext ctx, Rect box,
                                      out double top, out double right, out double bottom, out double left) {
            top = right = bottom = left = 0;
            if (!TryResolveLengthPercentage(tokens[0], ctx, box.Height, out top)) return false;
            right = top; bottom = top; left = top;
            if (tokens.Count > 1) {
                if (!TryResolveLengthPercentage(tokens[1], ctx, box.Width, out right)) return false;
                left = right;
            }
            if (tokens.Count > 2 && !TryResolveLengthPercentage(tokens[2], ctx, box.Height, out bottom)) return false;
            if (tokens.Count > 3 && !TryResolveLengthPercentage(tokens[3], ctx, box.Width, out left)) return false;
            return true;
        }

        static bool TryParseCircle(string body, LengthContext ctx, Rect box, out ClipPathShape shape) {
            shape = null;
            SplitAtKeyword(body ?? "", "at", out var radiusPart, out var positionPart);
            double cx = box.X + box.Width * 0.5;
            double cy = box.Y + box.Height * 0.5;
            if (!string.IsNullOrWhiteSpace(positionPart)) {
                ResolvePosition(positionPart, ctx, box, out cx, out cy);
            }
            double radius = Math.Min(box.Width, box.Height) * 0.5;
            radiusPart = (radiusPart ?? "").Trim();
            if (radiusPart.Length > 0) {
                if (CssStringUtil.EqualsIgnoreCaseTrimmed(radiusPart, "closest-side")) {
                    radius = Math.Min(Math.Min(cx - box.X, box.Right - cx), Math.Min(cy - box.Y, box.Bottom - cy));
                } else if (CssStringUtil.EqualsIgnoreCaseTrimmed(radiusPart, "farthest-side")) {
                    radius = Math.Max(Math.Max(cx - box.X, box.Right - cx), Math.Max(cy - box.Y, box.Bottom - cy));
                } else {
                    double basis = Math.Sqrt(box.Width * box.Width + box.Height * box.Height) / Math.Sqrt(2.0);
                    if (!TryResolveLengthPercentage(radiusPart, ctx, basis, out radius)) return false;
                }
            }
            shape = new CircleClipPathShape(cx, cy, radius);
            return true;
        }

        static bool TryParseEllipse(string body, LengthContext ctx, Rect box, out ClipPathShape shape) {
            shape = null;
            SplitAtKeyword(body ?? "", "at", out var radiiPart, out var positionPart);
            double cx = box.X + box.Width * 0.5;
            double cy = box.Y + box.Height * 0.5;
            if (!string.IsNullOrWhiteSpace(positionPart)) ResolvePosition(positionPart, ctx, box, out cx, out cy);
            double rx = box.Width * 0.5;
            double ry = box.Height * 0.5;
            var tokens = SplitSpace(radiiPart ?? "");
            if (tokens.Count > 0) {
                if (tokens.Count != 2) return false;
                if (!TryResolveShapeRadius(tokens[0], ctx, box.Width, cx - box.X, box.Right - cx, out rx)) return false;
                if (!TryResolveShapeRadius(tokens[1], ctx, box.Height, cy - box.Y, box.Bottom - cy, out ry)) return false;
            }
            shape = new EllipseClipPathShape(cx, cy, rx, ry);
            return true;
        }

        static bool TryResolveShapeRadius(string raw, LengthContext ctx, double basis, double nearSide, double farSide, out double radius) {
            radius = 0;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "closest-side")) {
                radius = Math.Min(nearSide, farSide);
                return true;
            }
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "farthest-side")) {
                radius = Math.Max(nearSide, farSide);
                return true;
            }
            return TryResolveLengthPercentage(raw, ctx, basis, out radius);
        }

        static bool TryParsePolygon(string body, LengthContext ctx, Rect box, out ClipPathShape shape) {
            shape = null;
            var parts = RawValueParser.SplitTopLevelCommas(body ?? "");
            if (parts.Count == 0) return false;
            int start = 0;
            var fillRule = ClipPathFillRule.Nonzero;
            string first = parts[0].Trim();
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(first, "nonzero")) {
                start = 1;
            } else if (CssStringUtil.EqualsIgnoreCaseTrimmed(first, "evenodd")) {
                fillRule = ClipPathFillRule.Evenodd;
                start = 1;
            }
            var points = new List<Point2D>(parts.Count - start);
            for (int i = start; i < parts.Count; i++) {
                var xy = SplitSpace(parts[i]);
                if (xy.Count != 2) return false;
                if (!TryResolveLengthPercentage(xy[0], ctx, box.Width, out var x)) return false;
                if (!TryResolveLengthPercentage(xy[1], ctx, box.Height, out var y)) return false;
                points.Add(new Point2D(box.X + x, box.Y + y));
            }
            if (points.Count < 3) return false;
            shape = new PolygonClipPathShape(points.ToArray(), fillRule);
            return true;
        }

        // CSS Shapes L1 / CSS Masking L1 — path() basic shape.
        // Grammar: path( [<fill-rule>,]? "<svg-path-data>" )
        //   <fill-rule> = nonzero | evenodd  (default: nonzero)
        // SVG path data coordinates are in px, used as-is in the element's local
        // coordinate system. They are anchored at the border-box origin (box.X, box.Y)
        // to match how polygon() points are offset — see TryParsePolygon which adds
        // box.X + x / box.Y + y for every point. SvgPathParser produces points in
        // path-data space; we translate by (box.X, box.Y) after parsing.
        // Invalid path data (malformed SVG, empty result) → whole clip-path invalid.
        static bool TryParsePath(string body, Rect box, out ClipPathShape shape) {
            shape = null;
            if (string.IsNullOrWhiteSpace(body)) return false;
            body = body.Trim();

            // Optional fill-rule prefix: "evenodd , ..." or "nonzero , ..."
            var fillRule = ClipPathFillRule.Nonzero;
            string pathData = body;

            // Find first comma not inside quotes.
            int commaIdx = -1;
            bool inQuote = false;
            char quoteChar = '\0';
            for (int i = 0; i < body.Length; i++) {
                char c = body[i];
                if (!inQuote && (c == '"' || c == '\'')) { inQuote = true; quoteChar = c; }
                else if (inQuote && c == quoteChar) { inQuote = false; }
                else if (!inQuote && c == ',') { commaIdx = i; break; }
            }

            if (commaIdx >= 0) {
                string maybeRule = body.Substring(0, commaIdx).Trim();
                if (CssStringUtil.EqualsIgnoreCaseTrimmed(maybeRule, "evenodd")) {
                    fillRule = ClipPathFillRule.Evenodd;
                    pathData = body.Substring(commaIdx + 1).Trim();
                } else if (CssStringUtil.EqualsIgnoreCaseTrimmed(maybeRule, "nonzero")) {
                    fillRule = ClipPathFillRule.Nonzero;
                    pathData = body.Substring(commaIdx + 1).Trim();
                }
                // If the "prefix" doesn't look like a fill-rule, treat the whole body
                // as path data (the comma was inside the quoted string — already extracted).
            }

            // Strip surrounding quotes (' or ").
            if (pathData.Length >= 2 &&
                ((pathData[0] == '"' && pathData[pathData.Length - 1] == '"') ||
                 (pathData[0] == '\'' && pathData[pathData.Length - 1] == '\''))) {
                pathData = pathData.Substring(1, pathData.Length - 2);
            } else {
                return false; // quotes are required per CSS Shapes spec
            }

            if (!SvgPathParser.TryParse(pathData, out var subPolygons) || subPolygons.Count == 0)
                return false;

            // Translate from path-data space to border-box world space.
            if (box.X != 0 || box.Y != 0) {
                for (int s = 0; s < subPolygons.Count; s++) {
                    var poly = subPolygons[s];
                    for (int i = 0; i < poly.Length; i++) poly[i] = poly[i].Translate(box.X, box.Y);
                }
            }

            shape = new PathClipPathShape(subPolygons, fillRule);
            return true;
        }

        static void SplitAtKeyword(string text, string keyword, out string before, out string after) {
            before = text;
            after = null;
            var tokens = SplitSpace(text);
            for (int i = 0; i < tokens.Count; i++) {
                if (!CssStringUtil.EqualsIgnoreCaseTrimmed(tokens[i], keyword)) continue;
                before = string.Join(" ", tokens.GetRange(0, i));
                after = string.Join(" ", tokens.GetRange(i + 1, tokens.Count - i - 1));
                return;
            }
        }

        static void ResolvePosition(string raw, LengthContext ctx, Rect box, out double x, out double y) {
            x = box.X + box.Width * 0.5;
            y = box.Y + box.Height * 0.5;
            if (!CssValue.TryParse(raw, out var parsed)) return;
            BackgroundLayoutResolver.ResolvePosition(parsed, box.Width, box.Height, 0, 0, ctx, null, out var ox, out var oy);
            x = box.X + ox;
            y = box.Y + oy;
        }

        static bool TryResolveLengthPercentage(string raw, LengthContext ctx, double basis, out double value) {
            value = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            raw = raw.Trim();
            if (!CssValue.TryParse(raw, out var parsed) || parsed == null) return false;
            if (parsed is CssPercentage p) { value = basis * p.Value * 0.01; return true; }
            if (parsed is CssLength len) {
                var c = ctx;
                c.BasisPixels = basis;
                value = len.ToPixels(c);
                return true;
            }
            if (parsed is CssNumber n) {
                if (n.Value == 0) { value = 0; return true; }
                value = n.Value;
                return true;
            }
            if (parsed is CssCalc calc) {
                try {
                    var c = ctx;
                    c.BasisPixels = basis;
                    value = calc.Evaluate(c);
                    return true;
                } catch {
                    return false;
                }
            }
            return false;
        }

        static List<string> SplitSpace(string raw) {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(raw)) return result;
            int depth = 0;
            int start = -1;
            for (int i = 0; i <= raw.Length; i++) {
                char c = i < raw.Length ? raw[i] : ' ';
                bool isSplit = i == raw.Length || (char.IsWhiteSpace(c) && depth == 0);
                if (isSplit) {
                    if (start >= 0) {
                        result.Add(raw.Substring(start, i - start));
                        start = -1;
                    }
                    continue;
                }
                if (start < 0) start = i;
                if (c == '(') depth++;
                else if (c == ')' && depth > 0) depth--;
            }
            return result;
        }
    }
}
