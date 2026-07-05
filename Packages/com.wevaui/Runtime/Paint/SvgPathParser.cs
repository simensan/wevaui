using System;
using System.Collections.Generic;
using System.Globalization;

namespace Weva.Paint {
    // SVG 1.1 path data parser (CPU-only, for clip-path: path(...) support).
    // Produces a list of closed sub-polygons (flattened from curves) for use
    // by PathClipPathShape.Contains().
    //
    // Grammar coverage:
    //   M/m  moveto
    //   L/l  lineto          H/h  horizontal-line     V/v  vertical-line
    //   C/c  cubic bezier    S/s  smooth cubic (reflected control point)
    //   Q/q  quadratic       T/t  smooth quadratic (reflected control point)
    //   A/a  elliptical arc  (converted to cubic approximations)
    //   Z/z  closepath
    //
    // Implicit command repetition: "M 0 0 100 100" → M then L per SVG spec.
    // Coordinate separators: commas and/or whitespace.
    // Scientific notation (1e-3) and sign-as-separator ("L-1-2") supported.
    // Arc flags are single chars and may be unspaced ("a1 1 0 011 0").
    //
    // Output: List<Point2D[]>, each entry is the vertices of one closed
    // sub-polygon (open subpaths are implicitly closed for fill purposes).
    internal static class SvgPathParser {
        // Flatness tolerance in pixels (square of allowed deviation from curve).
        // De Casteljau subdivision stops when the curve deviation is ≤ this.
        const double FlatnessTolerance = 0.1;

        /// <summary>
        /// Parse SVG path data into a list of closed sub-polygons.
        /// Returns false if the path data is malformed.
        /// </summary>
        public static bool TryParse(string pathData, out List<Point2D[]> subPolygons) {
            subPolygons = null;
            if (string.IsNullOrWhiteSpace(pathData)) return false;

            var reader = new PathReader(pathData);
            var result = new List<Point2D[]>();
            var currentPoints = new List<Point2D>();

            double curX = 0, curY = 0;      // current pen position
            double startX = 0, startY = 0;  // start of current subpath (for Z)
            double prevCpX = double.NaN, prevCpY = double.NaN; // for S/T reflected control
            char prevCmd = '\0';

            try {
                while (reader.HasMore) {
                    if (!reader.TryReadCommand(out char cmd)) return false;

                    bool rel = char.IsLower(cmd);
                    char upper = char.ToUpperInvariant(cmd);

                    // Determine if the first iteration uses the explicit command;
                    // subsequent iterations in an implicit repeat use the derived command.
                    char implicitCmd = upper == 'M' ? (rel ? 'l' : 'L') : cmd;

                    bool firstArg = true;
                    do {
                        char effectiveCmd = firstArg ? cmd : implicitCmd;
                        bool effectiveRel = char.IsLower(effectiveCmd);
                        char effectiveUpper = char.ToUpperInvariant(effectiveCmd);

                        switch (effectiveUpper) {
                            case 'M': {
                                // Close current subpath before starting new one.
                                if (currentPoints.Count >= 2) {
                                    result.Add(currentPoints.ToArray());
                                }
                                currentPoints = new List<Point2D>();
                                if (!reader.TryReadNumber(out double x)) return !firstArg;
                                if (!reader.TryReadNumber(out double y)) return false;
                                curX = effectiveRel ? curX + x : x;
                                curY = effectiveRel ? curY + y : y;
                                startX = curX; startY = curY;
                                currentPoints.Add(new Point2D(curX, curY));
                                prevCmd = effectiveCmd;
                                prevCpX = double.NaN; prevCpY = double.NaN;
                                break;
                            }
                            case 'Z': {
                                // Close subpath.
                                if (currentPoints.Count >= 2) {
                                    result.Add(currentPoints.ToArray());
                                }
                                currentPoints = new List<Point2D>();
                                curX = startX; curY = startY;
                                prevCmd = effectiveCmd;
                                prevCpX = double.NaN; prevCpY = double.NaN;
                                // Z does not have arguments; break out of do-while immediately.
                                goto doneArgs;
                            }
                            case 'L': {
                                if (!reader.TryReadNumber(out double x)) return !firstArg;
                                if (!reader.TryReadNumber(out double y)) return false;
                                curX = effectiveRel ? curX + x : x;
                                curY = effectiveRel ? curY + y : y;
                                currentPoints.Add(new Point2D(curX, curY));
                                prevCmd = effectiveCmd;
                                prevCpX = double.NaN; prevCpY = double.NaN;
                                break;
                            }
                            case 'H': {
                                if (!reader.TryReadNumber(out double x)) return !firstArg;
                                curX = effectiveRel ? curX + x : x;
                                currentPoints.Add(new Point2D(curX, curY));
                                prevCmd = effectiveCmd;
                                prevCpX = double.NaN; prevCpY = double.NaN;
                                break;
                            }
                            case 'V': {
                                if (!reader.TryReadNumber(out double y)) return !firstArg;
                                curY = effectiveRel ? curY + y : y;
                                currentPoints.Add(new Point2D(curX, curY));
                                prevCmd = effectiveCmd;
                                prevCpX = double.NaN; prevCpY = double.NaN;
                                break;
                            }
                            case 'C': {
                                if (!reader.TryReadNumber(out double x1)) return !firstArg;
                                if (!reader.TryReadNumber(out double y1)) return false;
                                if (!reader.TryReadNumber(out double x2)) return false;
                                if (!reader.TryReadNumber(out double y2)) return false;
                                if (!reader.TryReadNumber(out double x)) return false;
                                if (!reader.TryReadNumber(out double y)) return false;
                                double ax1 = effectiveRel ? curX + x1 : x1;
                                double ay1 = effectiveRel ? curY + y1 : y1;
                                double ax2 = effectiveRel ? curX + x2 : x2;
                                double ay2 = effectiveRel ? curY + y2 : y2;
                                double ax  = effectiveRel ? curX + x  : x;
                                double ay  = effectiveRel ? curY + y  : y;
                                FlattenCubic(curX, curY, ax1, ay1, ax2, ay2, ax, ay, currentPoints);
                                prevCpX = ax2; prevCpY = ay2;
                                curX = ax; curY = ay;
                                prevCmd = effectiveCmd;
                                break;
                            }
                            case 'S': {
                                // Smooth cubic: first control point reflected from previous C/S.
                                double ax1, ay1;
                                if (IsSmooth(prevCmd, 'C', 'c', 'S', 's') && !double.IsNaN(prevCpX)) {
                                    ax1 = 2 * curX - prevCpX;
                                    ay1 = 2 * curY - prevCpY;
                                } else {
                                    ax1 = curX; ay1 = curY;
                                }
                                if (!reader.TryReadNumber(out double x2)) return !firstArg;
                                if (!reader.TryReadNumber(out double y2)) return false;
                                if (!reader.TryReadNumber(out double x)) return false;
                                if (!reader.TryReadNumber(out double y)) return false;
                                double ax2 = effectiveRel ? curX + x2 : x2;
                                double ay2 = effectiveRel ? curY + y2 : y2;
                                double ax  = effectiveRel ? curX + x  : x;
                                double ay  = effectiveRel ? curY + y  : y;
                                FlattenCubic(curX, curY, ax1, ay1, ax2, ay2, ax, ay, currentPoints);
                                prevCpX = ax2; prevCpY = ay2;
                                curX = ax; curY = ay;
                                prevCmd = effectiveCmd;
                                break;
                            }
                            case 'Q': {
                                if (!reader.TryReadNumber(out double x1)) return !firstArg;
                                if (!reader.TryReadNumber(out double y1)) return false;
                                if (!reader.TryReadNumber(out double x)) return false;
                                if (!reader.TryReadNumber(out double y)) return false;
                                double ax1 = effectiveRel ? curX + x1 : x1;
                                double ay1 = effectiveRel ? curY + y1 : y1;
                                double ax  = effectiveRel ? curX + x  : x;
                                double ay  = effectiveRel ? curY + y  : y;
                                // Elevate quadratic to cubic then flatten.
                                double cx1 = curX + 2.0/3.0 * (ax1 - curX);
                                double cy1 = curY + 2.0/3.0 * (ay1 - curY);
                                double cx2 = ax  + 2.0/3.0 * (ax1 - ax);
                                double cy2 = ay  + 2.0/3.0 * (ay1 - ay);
                                FlattenCubic(curX, curY, cx1, cy1, cx2, cy2, ax, ay, currentPoints);
                                prevCpX = ax1; prevCpY = ay1; // quadratic control point reflected for T
                                curX = ax; curY = ay;
                                prevCmd = effectiveCmd;
                                break;
                            }
                            case 'T': {
                                // Smooth quadratic: control point reflected from previous Q/T.
                                double ax1, ay1;
                                if (IsSmooth(prevCmd, 'Q', 'q', 'T', 't') && !double.IsNaN(prevCpX)) {
                                    ax1 = 2 * curX - prevCpX;
                                    ay1 = 2 * curY - prevCpY;
                                } else {
                                    ax1 = curX; ay1 = curY;
                                }
                                if (!reader.TryReadNumber(out double x)) return !firstArg;
                                if (!reader.TryReadNumber(out double y)) return false;
                                double ax  = effectiveRel ? curX + x  : x;
                                double ay  = effectiveRel ? curY + y  : y;
                                double cx1 = curX + 2.0/3.0 * (ax1 - curX);
                                double cy1 = curY + 2.0/3.0 * (ay1 - curY);
                                double cx2 = ax  + 2.0/3.0 * (ax1 - ax);
                                double cy2 = ay  + 2.0/3.0 * (ay1 - ay);
                                FlattenCubic(curX, curY, cx1, cy1, cx2, cy2, ax, ay, currentPoints);
                                prevCpX = ax1; prevCpY = ay1;
                                curX = ax; curY = ay;
                                prevCmd = effectiveCmd;
                                break;
                            }
                            case 'A': {
                                if (!reader.TryReadNumber(out double rx)) return !firstArg;
                                if (!reader.TryReadNumber(out double ry)) return false;
                                if (!reader.TryReadNumber(out double xAngle)) return false;
                                if (!reader.TryReadArcFlag(out int largeArc)) return false;
                                if (!reader.TryReadArcFlag(out int sweep)) return false;
                                if (!reader.TryReadNumber(out double x)) return false;
                                if (!reader.TryReadNumber(out double y)) return false;
                                double ax = effectiveRel ? curX + x : x;
                                double ay = effectiveRel ? curY + y : y;
                                FlattenArc(curX, curY, rx, ry, xAngle, largeArc != 0, sweep != 0, ax, ay, currentPoints);
                                prevCpX = double.NaN; prevCpY = double.NaN;
                                curX = ax; curY = ay;
                                prevCmd = effectiveCmd;
                                break;
                            }
                            default:
                                return false;
                        }

                        firstArg = false;
                        reader.SkipSeparators();
                    } while (reader.HasMore && !reader.PeekIsCommand());

                    doneArgs:;
                }
            } catch {
                return false;
            }

            // Flush any open subpath (implicitly closed for fill).
            if (currentPoints.Count >= 2) {
                result.Add(currentPoints.ToArray());
            }

            subPolygons = result;
            return result.Count > 0;
        }

        // De Casteljau adaptive cubic flattening.
        // Appends intermediate points to `pts`; does NOT add the end point
        // (caller adds it after the loop, or via recursive call's last segment).
        // Exposed as internal so ShapeCommandParser (shape() support) can reuse it.
        internal static void FlattenCubic(
            double x0, double y0,
            double x1, double y1,
            double x2, double y2,
            double x3, double y3,
            List<Point2D> pts) {
            FlattenCubicRecursive(x0, y0, x1, y1, x2, y2, x3, y3, pts, 0);
            pts.Add(new Point2D(x3, y3));
        }

        static void FlattenCubicRecursive(
            double x0, double y0,
            double x1, double y1,
            double x2, double y2,
            double x3, double y3,
            List<Point2D> pts,
            int depth) {
            // Measure flatness as deviation of midpoint of control polygon from
            // the chord. Use the squared distance to avoid sqrt; tolerance² = (0.1)² = 0.01.
            // d = |P0P1P2P3 midpoint - chord midpoint|²
            // Standard SVG flatness: max of squares of distances from ctrl points to line.
            double ux = 3*x1 - 2*x0 - x3; ux *= ux;
            double uy = 3*y1 - 2*y0 - y3; uy *= uy;
            double vx = 3*x2 - 2*x3 - x0; vx *= vx;
            double vy = 3*y2 - 2*y3 - y0; vy *= vy;
            double uv = Math.Max(ux + uy, vx + vy);
            // FlatnessTolerance=0.1 → threshold = (4*0.1)² = 0.16
            if (uv <= 0.16 || depth >= 12) return;

            // Midpoints (De Casteljau for t=0.5).
            double m01x = (x0 + x1) * 0.5, m01y = (y0 + y1) * 0.5;
            double m12x = (x1 + x2) * 0.5, m12y = (y1 + y2) * 0.5;
            double m23x = (x2 + x3) * 0.5, m23y = (y2 + y3) * 0.5;
            double m012x = (m01x + m12x) * 0.5, m012y = (m01y + m12y) * 0.5;
            double m123x = (m12x + m23x) * 0.5, m123y = (m12y + m23y) * 0.5;
            double mx = (m012x + m123x) * 0.5, my = (m012y + m123y) * 0.5;

            FlattenCubicRecursive(x0, y0, m01x, m01y, m012x, m012y, mx, my, pts, depth + 1);
            pts.Add(new Point2D(mx, my));
            FlattenCubicRecursive(mx, my, m123x, m123y, m23x, m23y, x3, y3, pts, depth + 1);
        }

        // Convert SVG arc segment to cubic approximations via center parameterization.
        // See SVG 1.1 Appendix F.6. Splits at 90° quadrants.
        // Exposed as internal so ShapeCommandParser (shape() support) can reuse it.
        internal static void FlattenArc(
            double x1, double y1,
            double rx, double ry,
            double xAngleDeg,
            bool largeArc, bool sweep,
            double x2, double y2,
            List<Point2D> pts) {
            // Degenerate: endpoints coincide or radii zero → line segment.
            if ((Math.Abs(x2 - x1) < 1e-10 && Math.Abs(y2 - y1) < 1e-10) ||
                rx < 1e-10 || ry < 1e-10) {
                pts.Add(new Point2D(x2, y2));
                return;
            }

            double phi = xAngleDeg * Math.PI / 180.0;
            double cosPhi = Math.Cos(phi), sinPhi = Math.Sin(phi);

            // Step 1: compute (x1', y1') — midpoint in rotated frame.
            double dx = (x1 - x2) * 0.5, dy = (y1 - y2) * 0.5;
            double x1p =  cosPhi * dx + sinPhi * dy;
            double y1p = -sinPhi * dx + cosPhi * dy;

            // Ensure radii are large enough (SVG spec correction).
            double x1pSq = x1p * x1p, y1pSq = y1p * y1p;
            double rxSq = rx * rx, rySq = ry * ry;
            double lambda = x1pSq / rxSq + y1pSq / rySq;
            if (lambda > 1.0) {
                double sqrtLambda = Math.Sqrt(lambda);
                rx *= sqrtLambda; ry *= sqrtLambda;
                rxSq = rx * rx; rySq = ry * ry;
            }

            // Step 2: compute center (cx', cy') in rotated frame.
            double num = rxSq * rySq - rxSq * y1pSq - rySq * x1pSq;
            double den = rxSq * y1pSq + rySq * x1pSq;
            double sq = (den == 0) ? 0 : Math.Sqrt(Math.Max(0, num / den));
            if (largeArc == sweep) sq = -sq;

            double cxp =  sq * (rx * y1p / ry);
            double cyp = -sq * (ry * x1p / rx);

            // Step 3: compute center (cx, cy) in original frame.
            double cx = cosPhi * cxp - sinPhi * cyp + (x1 + x2) * 0.5;
            double cy = sinPhi * cxp + cosPhi * cyp + (y1 + y2) * 0.5;

            // Step 4: compute angles.
            double theta1 = Angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
            double dTheta  = Angle(
                (x1p - cxp) / rx,  (y1p - cyp) / ry,
                (-x1p - cxp) / rx, (-y1p - cyp) / ry);

            if (!sweep && dTheta > 0) dTheta -= 2 * Math.PI;
            if ( sweep && dTheta < 0) dTheta += 2 * Math.PI;

            // Split into segments of at most 90° and approximate each with cubic.
            int nSegs = Math.Max(1, (int)Math.Ceiling(Math.Abs(dTheta) / (Math.PI * 0.5)));
            double dT = dTheta / nSegs;

            for (int i = 0; i < nSegs; i++) {
                double t0 = theta1 + i * dT;
                double t1 = theta1 + (i + 1) * dT;
                AppendArcSegmentAsCubic(cx, cy, rx, ry, phi, t0, t1, pts);
            }
        }

        // Approximate one arc segment (<= 90°) as a cubic bezier.
        static void AppendArcSegmentAsCubic(
            double cx, double cy, double rx, double ry,
            double phi, double t0, double t1,
            List<Point2D> pts) {
            double alpha = Math.Sin(t1 - t0) * (Math.Sqrt(4 + 3 * Math.Tan((t1 - t0) * 0.5) * Math.Tan((t1 - t0) * 0.5)) - 1) / 3.0;

            double cos0 = Math.Cos(t0), sin0 = Math.Sin(t0);
            double cos1 = Math.Cos(t1), sin1 = Math.Sin(t1);
            double cosPhi = Math.Cos(phi), sinPhi = Math.Sin(phi);

            // Endpoint on ellipse.
            double ex0 = cosPhi * rx * cos0 - sinPhi * ry * sin0 + cx;
            double ey0 = sinPhi * rx * cos0 + cosPhi * ry * sin0 + cy;
            double ex1 = cosPhi * rx * cos1 - sinPhi * ry * sin1 + cx;
            double ey1 = sinPhi * rx * cos1 + cosPhi * ry * sin1 + cy;

            // Derivative vector at t0/t1: d/dt[rx*cos(t), ry*sin(t)] = [-rx*sin(t), ry*cos(t)],
            // then rotated by phi into the original coordinate frame.
            double derX0 = cosPhi * (-rx * sin0) - sinPhi * (ry * cos0);
            double derY0 = sinPhi * (-rx * sin0) + cosPhi * (ry * cos0);
            double derX1 = cosPhi * (-rx * sin1) - sinPhi * (ry * cos1);
            double derY1 = sinPhi * (-rx * sin1) + cosPhi * (ry * cos1);

            double cp1x = ex0 + alpha * derX0;
            double cp1y = ey0 + alpha * derY0;
            double cp2x = ex1 - alpha * derX1;
            double cp2y = ey1 - alpha * derY1;

            // FlattenCubic appends intermediate subdivision points and then ex1,ey1.
            // The caller (FlattenArc loop) passes the same pts list for all segments;
            // each segment's ex0 equals the previous segment's ex1 (already appended).
            FlattenCubic(ex0, ey0, cp1x, cp1y, cp2x, cp2y, ex1, ey1, pts);
        }

        // Signed angle between vectors (u1,u2) and (v1,v2), in radians.
        static double Angle(double u1, double u2, double v1, double v2) {
            double dot = u1 * v1 + u2 * v2;
            double len = Math.Sqrt((u1*u1 + u2*u2) * (v1*v1 + v2*v2));
            double angle = Math.Acos(Math.Max(-1, Math.Min(1, dot / len)));
            if (u1 * v2 - u2 * v1 < 0) angle = -angle;
            return angle;
        }

        static bool IsSmooth(char prev, char a, char aLow, char b, char bLow) {
            return prev == a || prev == aLow || prev == b || prev == bLow;
        }

        // ===== path token reader =====

        struct PathReader {
            readonly string _data;
            int _pos;

            public PathReader(string data) { _data = data; _pos = 0; }
            public bool HasMore => _pos < _data.Length;

            // Peek whether the next non-whitespace char is a command letter.
            public bool PeekIsCommand() {
                int i = _pos;
                while (i < _data.Length && (_data[i] == ' ' || _data[i] == '\t' ||
                       _data[i] == '\n' || _data[i] == '\r' || _data[i] == ',')) i++;
                if (i >= _data.Length) return false;
                char c = _data[i];
                return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
            }

            // Reads next command letter, skipping whitespace/commas.
            public bool TryReadCommand(out char cmd) {
                cmd = '\0';
                SkipSeparators();
                if (_pos >= _data.Length) return false;
                char c = _data[_pos];
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) {
                    cmd = c;
                    _pos++;
                    SkipWhitespace(); // skip whitespace after command letter
                    return true;
                }
                return false;
            }

            // Reads a floating-point number (handles sign, scientific notation).
            public bool TryReadNumber(out double value) {
                value = 0;
                SkipSeparators();
                if (_pos >= _data.Length) return false;

                int start = _pos;
                char c = _data[_pos];
                if (c == '+' || c == '-') _pos++;

                bool hasDigit = false;
                while (_pos < _data.Length && char.IsDigit(_data[_pos])) { _pos++; hasDigit = true; }
                if (_pos < _data.Length && _data[_pos] == '.') {
                    _pos++;
                    while (_pos < _data.Length && char.IsDigit(_data[_pos])) { _pos++; hasDigit = true; }
                }
                if (!hasDigit) { _pos = start; return false; }

                // Scientific notation.
                if (_pos < _data.Length && (_data[_pos] == 'e' || _data[_pos] == 'E')) {
                    int ePos = _pos;
                    _pos++;
                    if (_pos < _data.Length && (_data[_pos] == '+' || _data[_pos] == '-')) _pos++;
                    if (_pos < _data.Length && char.IsDigit(_data[_pos])) {
                        while (_pos < _data.Length && char.IsDigit(_data[_pos])) _pos++;
                    } else {
                        _pos = ePos; // rewind, no valid exponent
                    }
                }

                string num = _data.Substring(start, _pos - start);
                if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) {
                    _pos = start;
                    return false;
                }
                return true;
            }

            // Arc flags are single chars '0' or '1', may be unspaced.
            public bool TryReadArcFlag(out int flag) {
                flag = 0;
                SkipSeparators();
                if (_pos >= _data.Length) return false;
                char c = _data[_pos];
                if (c == '0') { flag = 0; _pos++; return true; }
                if (c == '1') { flag = 1; _pos++; return true; }
                return false;
            }

            public void SkipSeparators() {
                while (_pos < _data.Length) {
                    char c = _data[_pos];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == ',') _pos++;
                    else break;
                }
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
