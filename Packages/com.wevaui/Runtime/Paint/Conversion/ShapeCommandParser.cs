using System;
using System.Collections.Generic;
using Weva.Css.Values;

namespace Weva.Paint.Conversion {
    // CSS Shapes 2 §3 — shape() basic shape parser.
    //
    // Grammar:
    //   shape( [<fill-rule>,]? from <x> <y> , <shape-command># )
    //
    //   <fill-rule>     = nonzero | evenodd  (default: nonzero, same as path())
    //   <shape-command> =
    //     move [by|to] <x> <y>
    //     line [by|to] <x> <y>
    //     hline [by|to] <x>
    //     vline [by|to] <y>
    //     curve [by|to] <x> <y> with <cx1> <cy1> [/ <cx2> <cy2>]   (quadratic / cubic)
    //     smooth [by|to] <x> <y> [with <cx> <cy>]                   (smooth cubic / quadratic)
    //     arc [by|to] <x> <y> of <rx> [<ry>] [<angle>] [large|small] [cw|ccw]
    //     close
    //
    //   Coordinates are <length-percentage> (px, %, em, calc()).
    //   Percentages resolve against the reference box: x-axis percentages against
    //   box.Width, y-axis percentages against box.Height — same as inset()/circle().
    //
    //   by = relative to the current point (delta).
    //   to = absolute (relative to the reference box origin).
    //   The spec requires by|to explicitly on each command. Attempting to omit it
    //   returns false (invalid), consistent with other strict CSS parsers.
    //
    // The parser converts the command list to absolute flattened sub-polygons in
    // the same format as SvgPathParser, then anchors at (box.X, box.Y) to produce
    // a PathClipPathShape — identical to what TryParsePath() produces.
    //
    // Curve flattening and arc approximation are delegated to SvgPathParser's
    // internal helpers (FlattenCubic / FlattenArc) which were exposed as
    // internal for this purpose — no math is duplicated.
    //
    // IMPORTANT: CssTokenReader is a struct. All helpers that read from the token
    // stream take it as `ref CssTokenReader` so mutations (position advances) are
    // visible to the caller. Forgetting `ref` causes the classic struct-copy bug
    // where the helper advances a copy and the caller's position never moves.
    internal static class ShapeCommandParser {
        // Entry point called from ClipPathResolver.
        // body: the string inside shape(...), already stripped of outer parens.
        // box: the reference box (used for %-resolution and origin anchoring).
        public static bool TryParse(
            string body,
            LengthContext ctx,
            Rect box,
            out ClipPathShape shape) {

            shape = null;
            if (string.IsNullOrWhiteSpace(body)) return false;

            // ---- 1. Optional fill-rule prefix  --------------------------------
            // The fill-rule, if present, must be the very first token before the
            // first top-level comma (same placement as path()).
            var fillRule = ClipPathFillRule.Nonzero;
            string rest = body.Trim();

            int firstComma = IndexOfFirstTopLevelComma(rest);
            if (firstComma >= 0) {
                string maybeRule = rest.Substring(0, firstComma).Trim();
                if (string.Equals(maybeRule, "nonzero", StringComparison.OrdinalIgnoreCase)) {
                    fillRule = ClipPathFillRule.Nonzero;
                    rest = rest.Substring(firstComma + 1).Trim();
                } else if (string.Equals(maybeRule, "evenodd", StringComparison.OrdinalIgnoreCase)) {
                    fillRule = ClipPathFillRule.Evenodd;
                    rest = rest.Substring(firstComma + 1).Trim();
                }
                // If the first comma-delimited segment is not a fill-rule keyword,
                // treat the whole body as starting with "from" (no fill-rule prefix).
            }

            // ---- 2. "from <x> <y>"  -------------------------------------------
            // After the optional fill-rule, the body must begin with "from".
            var tokenReader = new CssTokenReader(rest);
            if (!tokenReader.TryReadIdentifier(out string fromKw) ||
                !string.Equals(fromKw, "from", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!tokenReader.TryReadLengthToken(out string fromXStr)) return false;
            if (!tokenReader.TryReadLengthToken(out string fromYStr)) return false;
            if (!ResolveLengthPct(fromXStr, ctx, box.Width, out double fromX)) return false;
            if (!ResolveLengthPct(fromYStr, ctx, box.Height, out double fromY)) return false;

            // ---- 3. Expect comma after "from <x> <y>"  -----------------------
            if (!tokenReader.TryConsume(',')) return false;

            // ---- 4. Parse comma-separated shape-commands  -------------------
            var result   = new List<Point2D[]>();
            var current  = new List<Point2D>();
            double curX  = fromX, curY = fromY;
            double startX = fromX, startY = fromY;
            // Reflected control points for smooth curve/arc:
            double prevCpX = double.NaN, prevCpY = double.NaN;
            bool prevWasCurve  = false; // used to decide reflected cp for 'smooth curve'
            bool prevWasQuad   = false; // same for 'smooth quadratic'

            // Add the "from" point as the first point of the first subpath.
            current.Add(new Point2D(fromX, fromY));

            while (tokenReader.HasMore) {
                if (!tokenReader.TryReadIdentifier(out string cmd)) return false;
                string cmdLow = cmd.ToLowerInvariant();

                switch (cmdLow) {
                    case "move": {
                        if (!ParseByTo(ref tokenReader, ctx, box, curX, curY,
                            out double tx, out double ty)) return false;
                        // Flush current sub-path (if it has ≥2 points).
                        if (current.Count >= 2) result.Add(current.ToArray());
                        current = new List<Point2D>();
                        curX = tx; curY = ty;
                        startX = curX; startY = curY;
                        current.Add(new Point2D(curX, curY));
                        prevCpX = double.NaN; prevCpY = double.NaN;
                        prevWasCurve = prevWasQuad = false;
                        break;
                    }
                    case "line": {
                        if (!ParseByTo(ref tokenReader, ctx, box, curX, curY,
                            out double tx, out double ty)) return false;
                        curX = tx; curY = ty;
                        current.Add(new Point2D(curX, curY));
                        prevCpX = double.NaN; prevCpY = double.NaN;
                        prevWasCurve = prevWasQuad = false;
                        break;
                    }
                    case "hline": {
                        // hline [by|to] <x>  — only an x coordinate, y unchanged.
                        if (!ParseByToSingle(ref tokenReader, ctx, box.Width, curX,
                            out double tx)) return false;
                        curX = tx;
                        current.Add(new Point2D(curX, curY));
                        prevCpX = double.NaN; prevCpY = double.NaN;
                        prevWasCurve = prevWasQuad = false;
                        break;
                    }
                    case "vline": {
                        // vline [by|to] <y>  — only a y coordinate, x unchanged.
                        if (!ParseByToSingle(ref tokenReader, ctx, box.Height, curY,
                            out double ty)) return false;
                        curY = ty;
                        current.Add(new Point2D(curX, curY));
                        prevCpX = double.NaN; prevCpY = double.NaN;
                        prevWasCurve = prevWasQuad = false;
                        break;
                    }
                    case "curve": {
                        // curve [by|to] <x> <y> with <cx1> <cy1> [/ <cx2> <cy2>]
                        // One control point => quadratic; two control points => cubic.
                        if (!ParseByToCoord(ref tokenReader, ctx, box, curX, curY,
                            out double tx, out double ty, out bool rel)) return false;

                        // Expect 'with'
                        if (!tokenReader.TryReadIdentifier(out string withKw) ||
                            !string.Equals(withKw, "with", StringComparison.OrdinalIgnoreCase))
                            return false;

                        // First control point.
                        if (!tokenReader.TryReadLengthToken(out string cp1xStr)) return false;
                        if (!tokenReader.TryReadLengthToken(out string cp1yStr)) return false;
                        if (!ResolveLengthPct(cp1xStr, ctx, box.Width,  out double cp1xRel)) return false;
                        if (!ResolveLengthPct(cp1yStr, ctx, box.Height, out double cp1yRel)) return false;
                        // Control points follow the same by/to rule as the endpoint.
                        double cp1x = rel ? curX + cp1xRel : cp1xRel;
                        double cp1y = rel ? curY + cp1yRel : cp1yRel;

                        // Optional '/' separator for second control point (cubic).
                        bool isCubic = tokenReader.TryConsumeSlash();
                        if (isCubic) {
                            if (!tokenReader.TryReadLengthToken(out string cp2xStr)) return false;
                            if (!tokenReader.TryReadLengthToken(out string cp2yStr)) return false;
                            if (!ResolveLengthPct(cp2xStr, ctx, box.Width,  out double cp2xRel)) return false;
                            if (!ResolveLengthPct(cp2yStr, ctx, box.Height, out double cp2yRel)) return false;
                            double cp2x = rel ? curX + cp2xRel : cp2xRel;
                            double cp2y = rel ? curY + cp2yRel : cp2yRel;
                            SvgPathParser.FlattenCubic(curX, curY, cp1x, cp1y, cp2x, cp2y, tx, ty, current);
                            prevCpX = cp2x; prevCpY = cp2y;
                            prevWasCurve = true; prevWasQuad = false;
                        } else {
                            // Quadratic: elevate to cubic, then flatten.
                            double cx1 = curX + 2.0 / 3.0 * (cp1x - curX);
                            double cy1 = curY + 2.0 / 3.0 * (cp1y - curY);
                            double cx2 = tx   + 2.0 / 3.0 * (cp1x - tx);
                            double cy2 = ty   + 2.0 / 3.0 * (cp1y - ty);
                            SvgPathParser.FlattenCubic(curX, curY, cx1, cy1, cx2, cy2, tx, ty, current);
                            prevCpX = cp1x; prevCpY = cp1y; // quadratic cp reflected for smooth
                            prevWasCurve = false; prevWasQuad = true;
                        }
                        curX = tx; curY = ty;
                        break;
                    }
                    case "smooth": {
                        // smooth [by|to] <x> <y> [with <cx> <cy>]
                        // 'smooth' without 'with' => smooth quadratic (reflected prev quadratic cp).
                        // 'smooth' with 'with'    => smooth cubic (reflected prev cubic cp2).
                        if (!ParseByToCoord(ref tokenReader, ctx, box, curX, curY,
                            out double tx, out double ty, out bool rel)) return false;

                        // Check for optional 'with'.
                        bool hasExplicitCp = tokenReader.TryPeekIdentifier("with");
                        if (hasExplicitCp) tokenReader.TryReadIdentifier(out _); // consume 'with'

                        if (hasExplicitCp) {
                            // Smooth cubic: reflected first CP, explicit second CP.
                            double ax1, ay1;
                            if (prevWasCurve && !double.IsNaN(prevCpX)) {
                                ax1 = 2 * curX - prevCpX;
                                ay1 = 2 * curY - prevCpY;
                            } else {
                                ax1 = curX; ay1 = curY;
                            }
                            if (!tokenReader.TryReadLengthToken(out string cp2xStr)) return false;
                            if (!tokenReader.TryReadLengthToken(out string cp2yStr)) return false;
                            if (!ResolveLengthPct(cp2xStr, ctx, box.Width,  out double cp2xRel)) return false;
                            if (!ResolveLengthPct(cp2yStr, ctx, box.Height, out double cp2yRel)) return false;
                            double ax2 = rel ? curX + cp2xRel : cp2xRel;
                            double ay2 = rel ? curY + cp2yRel : cp2yRel;
                            SvgPathParser.FlattenCubic(curX, curY, ax1, ay1, ax2, ay2, tx, ty, current);
                            prevCpX = ax2; prevCpY = ay2;
                            prevWasCurve = true; prevWasQuad = false;
                        } else {
                            // Smooth quadratic: reflected quadratic control point.
                            double ax1, ay1;
                            if (prevWasQuad && !double.IsNaN(prevCpX)) {
                                ax1 = 2 * curX - prevCpX;
                                ay1 = 2 * curY - prevCpY;
                            } else {
                                ax1 = curX; ay1 = curY;
                            }
                            double cx1 = curX + 2.0 / 3.0 * (ax1 - curX);
                            double cy1 = curY + 2.0 / 3.0 * (ay1 - curY);
                            double cx2 = tx   + 2.0 / 3.0 * (ax1 - tx);
                            double cy2 = ty   + 2.0 / 3.0 * (ay1 - ty);
                            SvgPathParser.FlattenCubic(curX, curY, cx1, cy1, cx2, cy2, tx, ty, current);
                            prevCpX = ax1; prevCpY = ay1;
                            prevWasCurve = false; prevWasQuad = true;
                        }
                        curX = tx; curY = ty;
                        break;
                    }
                    case "arc": {
                        // arc [by|to] <x> <y> of <rx> [<ry>] [<angle>] [large|small] [cw|ccw]
                        if (!ParseByToCoord(ref tokenReader, ctx, box, curX, curY,
                            out double tx, out double ty, out bool rel)) return false;

                        // Expect 'of'
                        if (!tokenReader.TryReadIdentifier(out string ofKw) ||
                            !string.Equals(ofKw, "of", StringComparison.OrdinalIgnoreCase))
                            return false;

                        // rx is required.
                        if (!tokenReader.TryReadLengthToken(out string rxStr)) return false;
                        if (!ResolveLengthPct(rxStr, ctx, box.Width, out double rx)) return false;

                        // Optional ry: present if the next token is a length/number/percentage,
                        // not a keyword (large/small/cw/ccw) or an angle token.
                        double ry = rx;
                        if (tokenReader.TryPeekLengthToken(out string ryStr) &&
                            !IsAngleOrArcKeyword(ryStr)) {
                            tokenReader.TryReadLengthToken(out _); // consume
                            if (!ResolveLengthPct(ryStr, ctx, box.Height, out ry)) return false;
                        }

                        // Optional rotation angle: present if next token ends in an angle unit.
                        double angleDeg = 0;
                        if (tokenReader.TryPeekLengthToken(out string angleStr) &&
                            IsAngleToken(angleStr)) {
                            tokenReader.TryReadLengthToken(out _); // consume
                            if (!TryParseAngleDeg(angleStr, out angleDeg)) return false;
                        }

                        // Optional large|small (default: small → not large).
                        bool largeArc = false;
                        if (tokenReader.TryPeekIdentifier("large")) {
                            tokenReader.TryReadIdentifier(out _);
                            largeArc = true;
                        } else if (tokenReader.TryPeekIdentifier("small")) {
                            tokenReader.TryReadIdentifier(out _);
                            largeArc = false;
                        }

                        // Optional cw|ccw (default: ccw → not sweep).
                        // CSS shape() arc: cw = clockwise sweep direction = SVG sweep-flag=1.
                        //                  ccw = counter-clockwise = SVG sweep-flag=0.
                        bool sweep = false; // ccw by default
                        if (tokenReader.TryPeekIdentifier("cw")) {
                            tokenReader.TryReadIdentifier(out _);
                            sweep = true;
                        } else if (tokenReader.TryPeekIdentifier("ccw")) {
                            tokenReader.TryReadIdentifier(out _);
                            sweep = false;
                        }

                        SvgPathParser.FlattenArc(curX, curY, rx, ry, angleDeg, largeArc, sweep, tx, ty, current);
                        curX = tx; curY = ty;
                        prevCpX = double.NaN; prevCpY = double.NaN;
                        prevWasCurve = prevWasQuad = false;
                        break;
                    }
                    case "close": {
                        if (current.Count >= 2) result.Add(current.ToArray());
                        current = new List<Point2D>();
                        curX = startX; curY = startY;
                        prevCpX = double.NaN; prevCpY = double.NaN;
                        prevWasCurve = prevWasQuad = false;
                        // After close, if there are further commands (e.g. a new move), the
                        // next command will set up a new subpath. We add the close point so
                        // that a bare "close" at the end doesn't leave an open point.
                        current.Add(new Point2D(curX, curY));
                        break;
                    }
                    default:
                        return false; // unknown command → whole value invalid
                }

                // Consume optional comma separator between commands.
                tokenReader.TryConsume(',');
            }

            // Flush any open sub-path (at least 2 points to form a polygon edge).
            if (current.Count >= 2) result.Add(current.ToArray());
            if (result.Count == 0) return false;

            // Anchor to border-box origin (same as TryParsePath / TryParsePolygon).
            if (box.X != 0 || box.Y != 0) {
                for (int s = 0; s < result.Count; s++) {
                    var poly = result[s];
                    for (int i = 0; i < poly.Length; i++)
                        poly[i] = poly[i].Translate(box.X, box.Y);
                }
            }

            shape = new PathClipPathShape(result, fillRule);
            return true;
        }

        // ===== helpers ==========================================================

        // Parse [by|to] <x> <y> → absolute target (tx, ty).
        // Returns `rel` so callers can apply the same transformation to control points.
        // NOTE: takes CssTokenReader by ref to mutate the caller's position.
        static bool ParseByToCoord(
            ref CssTokenReader tokenReader,
            LengthContext ctx,
            Rect box,
            double curX, double curY,
            out double tx, out double ty, out bool rel) {

            tx = ty = 0;
            rel = false;

            if (!tokenReader.TryReadIdentifier(out string byTo)) return false;
            if (string.Equals(byTo, "by", StringComparison.OrdinalIgnoreCase)) rel = true;
            else if (!string.Equals(byTo, "to", StringComparison.OrdinalIgnoreCase)) return false;

            if (!tokenReader.TryReadLengthToken(out string xStr)) return false;
            if (!tokenReader.TryReadLengthToken(out string yStr)) return false;
            if (!ResolveLengthPct(xStr, ctx, box.Width, out double xVal)) return false;
            if (!ResolveLengthPct(yStr, ctx, box.Height, out double yVal)) return false;
            tx = rel ? curX + xVal : xVal;
            ty = rel ? curY + yVal : yVal;
            return true;
        }

        // Parse [by|to] <x> <y> → absolute (tx, ty), discarding the rel flag.
        // NOTE: takes CssTokenReader by ref.
        static bool ParseByTo(
            ref CssTokenReader tokenReader,
            LengthContext ctx,
            Rect box,
            double curX, double curY,
            out double tx, out double ty) {

            return ParseByToCoord(ref tokenReader, ctx, box, curX, curY, out tx, out ty, out _);
        }

        // Parse [by|to] <single-value> → absolute single coordinate.
        // Used for hline (x only) and vline (y only).
        // NOTE: takes CssTokenReader by ref.
        static bool ParseByToSingle(
            ref CssTokenReader tokenReader,
            LengthContext ctx,
            double basis,
            double cur,
            out double result) {

            result = 0;
            if (!tokenReader.TryReadIdentifier(out string byTo)) return false;
            bool rel = false;
            if (string.Equals(byTo, "by", StringComparison.OrdinalIgnoreCase)) rel = true;
            else if (!string.Equals(byTo, "to", StringComparison.OrdinalIgnoreCase)) return false;

            if (!tokenReader.TryReadLengthToken(out string valStr)) return false;
            if (!ResolveLengthPct(valStr, ctx, basis, out double val)) return false;
            result = rel ? cur + val : val;
            return true;
        }

        // Resolve a raw length/percentage token against `basis` px.
        // Mirrors ClipPathResolver.TryResolveLengthPercentage exactly.
        static bool ResolveLengthPct(string raw, LengthContext ctx, double basis, out double value) {
            value = 0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            raw = raw.Trim();
            if (!CssValue.TryParse(raw, out var parsed) || parsed == null) return false;
            if (parsed is CssPercentage p) { value = basis * p.Value * 0.01; return true; }
            if (parsed is CssLength len) {
                var c = ctx; c.BasisPixels = basis;
                value = len.ToPixels(c);
                return true;
            }
            if (parsed is CssNumber n) {
                if (n.Value == 0) { value = 0; return true; }
                // Bare numbers (non-zero) are treated as pixels (CSS Shapes is lenient here).
                value = n.Value;
                return true;
            }
            if (parsed is CssCalc calc) {
                try {
                    var c = ctx; c.BasisPixels = basis;
                    value = calc.Evaluate(c);
                    return true;
                } catch { return false; }
            }
            return false;
        }

        // True if the token looks like an angle: ends with deg/rad/turn/grad.
        static bool IsAngleToken(string token) {
            if (string.IsNullOrEmpty(token)) return false;
            string t = token.ToLowerInvariant();
            return t.EndsWith("deg") || t.EndsWith("rad") || t.EndsWith("turn") || t.EndsWith("grad");
        }

        // True if the token is one of the arc direction/size keywords or an angle token.
        static bool IsAngleOrArcKeyword(string token) {
            if (string.IsNullOrEmpty(token)) return false;
            string t = token.ToLowerInvariant();
            return t == "large" || t == "small" || t == "cw" || t == "ccw"
                || IsAngleToken(t);
        }

        // Parse an angle token that may end with deg/rad/turn/grad.
        static bool TryParseAngleDeg(string token, out double degrees) {
            degrees = 0;
            if (string.IsNullOrEmpty(token)) return false;
            string t = token.Trim().ToLowerInvariant();
            if (t.EndsWith("deg")) {
                return double.TryParse(t.Substring(0, t.Length - 3),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out degrees);
            }
            if (t.EndsWith("rad")) {
                if (!double.TryParse(t.Substring(0, t.Length - 3),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double rad)) return false;
                degrees = rad * 180.0 / Math.PI;
                return true;
            }
            if (t.EndsWith("turn")) {
                if (!double.TryParse(t.Substring(0, t.Length - 4),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double turn)) return false;
                degrees = turn * 360.0;
                return true;
            }
            if (t.EndsWith("grad")) {
                if (!double.TryParse(t.Substring(0, t.Length - 4),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double grad)) return false;
                degrees = grad * 360.0 / 400.0;
                return true;
            }
            return false;
        }

        // Index of first top-level (depth-0) comma in text, -1 if none.
        static int IndexOfFirstTopLevelComma(string text) {
            int depth = 0;
            for (int i = 0; i < text.Length; i++) {
                char c = text[i];
                if (c == '(') depth++;
                else if (c == ')' && depth > 0) depth--;
                else if (c == ',' && depth == 0) return i;
            }
            return -1;
        }

        // ===== CssTokenReader — lightweight token scanner for shape() ============
        //
        // Scans tokens separated by whitespace.
        // Commas between commands must be consumed explicitly via TryConsume(',').
        // "Tokens" are either identifiers (letters/hyphens) or value tokens
        // (anything that looks like a length, percentage, number, or calc()).
        // This is NOT a general CSS tokenizer — it is purpose-built for the
        // shape() command grammar.
        //
        // CRITICAL: This is a struct, so always pass as `ref` when the callee
        // needs to advance the scanner position. A by-value call makes a copy
        // and the caller's position is never updated.

        internal struct CssTokenReader {
            readonly string _data;
            int _pos;

            public CssTokenReader(string data) {
                _data = data ?? "";
                _pos = 0;
                SkipWhitespace();
            }

            public bool HasMore => _pos < _data.Length;

            // Read a CSS identifier token (letters, digits, hyphens, starting with a letter or hyphen).
            // Returns false if next non-whitespace char is not an identifier start.
            public bool TryReadIdentifier(out string ident) {
                ident = null;
                SkipWhitespace();
                if (_pos >= _data.Length) return false;
                char c = _data[_pos];
                if (!char.IsLetter(c) && c != '-' && c != '_') return false;
                int start = _pos;
                while (_pos < _data.Length) {
                    char ch = _data[_pos];
                    if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_') _pos++;
                    else break;
                }
                // Reject if we only consumed a leading '-' with no following letter.
                if (_pos == start + 1 && _data[start] == '-') { _pos = start; return false; }
                ident = _data.Substring(start, _pos - start);
                return true;
            }

            // Peek whether the next token is an identifier equal to `expected` (case-insensitive).
            // Does NOT advance.
            public bool TryPeekIdentifier(string expected) {
                int saved = _pos;
                bool ok = TryReadIdentifier(out string ident)
                       && string.Equals(ident, expected, StringComparison.OrdinalIgnoreCase);
                _pos = saved;
                return ok;
            }

            // Read a "value token": number, percentage, length (with unit), or calc(...).
            // Returns the raw string; does not consume trailing whitespace/comma.
            public bool TryReadLengthToken(out string token) {
                token = null;
                SkipWhitespace();
                if (_pos >= _data.Length) return false;
                char c = _data[_pos];

                // calc( ... )
                if (_pos + 4 < _data.Length &&
                    string.Compare(_data, _pos, "calc(", 0, 5, StringComparison.OrdinalIgnoreCase) == 0) {
                    int start = _pos; _pos += 5;
                    int depth = 1;
                    while (_pos < _data.Length && depth > 0) {
                        if (_data[_pos] == '(') depth++;
                        else if (_data[_pos] == ')') depth--;
                        _pos++;
                    }
                    token = _data.Substring(start, _pos - start);
                    return true;
                }

                // Number (possibly with sign): starts with digit, '.', '+', or '-'.
                if (c == '+' || c == '-' || char.IsDigit(c) || c == '.') {
                    int start = _pos;
                    if (c == '+' || c == '-') _pos++;
                    while (_pos < _data.Length && (char.IsDigit(_data[_pos]) || _data[_pos] == '.')) _pos++;
                    // Exponent (1e-3)
                    if (_pos < _data.Length && (_data[_pos] == 'e' || _data[_pos] == 'E')) {
                        int ePos = _pos; _pos++;
                        if (_pos < _data.Length && (_data[_pos] == '+' || _data[_pos] == '-')) _pos++;
                        if (_pos < _data.Length && char.IsDigit(_data[_pos])) {
                            while (_pos < _data.Length && char.IsDigit(_data[_pos])) _pos++;
                        } else { _pos = ePos; } // rewind if no valid exponent digits
                    }
                    // Unit suffix (letters or %)
                    while (_pos < _data.Length && (char.IsLetter(_data[_pos]) || _data[_pos] == '%')) _pos++;
                    if (_pos == start) return false; // nothing consumed
                    token = _data.Substring(start, _pos - start);
                    return true;
                }

                return false;
            }

            // Peek the next "value token" without consuming it.
            public bool TryPeekLengthToken(out string token) {
                int saved = _pos;
                bool ok = TryReadLengthToken(out token);
                _pos = saved;
                return ok;
            }

            // Consume a specific character (usually ',') after skipping whitespace.
            public bool TryConsume(char ch) {
                SkipWhitespace();
                if (_pos < _data.Length && _data[_pos] == ch) { _pos++; return true; }
                return false;
            }

            // Consume a '/' for curve control-point separation (after skipping whitespace).
            public bool TryConsumeSlash() {
                SkipWhitespace();
                if (_pos < _data.Length && _data[_pos] == '/') { _pos++; return true; }
                return false;
            }

            void SkipWhitespace() {
                while (_pos < _data.Length) {
                    char c = _data[_pos];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') _pos++;
                    else break;
                }
            }
        }
    }
}
