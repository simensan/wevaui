using System;
using System.Collections.Generic;
using System.Globalization;
using Weva.Css.Cascade;
using Weva.Css.Values;

namespace Weva.Paint.Conversion {
    internal static class TransformResolver {
        // Cached ids for the L2 individual transform properties — resolved
        // once at type init so the paint loop indexes ComputedStyle directly
        // instead of paying a registry lookup per frame.
        static readonly int TranslateId = CssProperties.GetId("translate");
        static readonly int RotateId = CssProperties.GetId("rotate");
        static readonly int ScaleId = CssProperties.GetId("scale");

        public static Transform2D ResolveTransform(ComputedStyle style, double refWidth, double refHeight) {
            if (style == null) return Transform2D.Identity;

            Transform2D explicitXf = ResolveExplicitTransform(style, refWidth, refHeight);
            // CSS Transforms L2 §3: the effective transform is
            //   translate * rotate * scale * <transform-property>
            // i.e. when applied to a point the explicit transform runs
            // first, then scale, then rotate, then translate. Multiply uses
            // the "apply-this-first-then-other" contract, so the chain
            // composes left-to-right.
            Transform2D scaleXf = ResolveIndividualScale(style);
            Transform2D rotateXf = ResolveIndividualRotate(style);
            Transform2D translateXf = ResolveIndividualTranslate(style, refWidth, refHeight);
            return explicitXf.Multiply(scaleXf).Multiply(rotateXf).Multiply(translateXf);
        }

        static Transform2D ResolveExplicitTransform(ComputedStyle style, double refWidth, double refHeight) {

            // Hot path: read the cached parse tree via per-style GetParsed
            // instead of the raw string + RawValueParser tokenizer chain. A
            // valid transform parses to either a single CssFunctionCall
            // (e.g. `translate(10px, 20px)`), a comma-or-space CssValueList of
            // CssFunctionCalls (compound transforms), or a CssKeyword/
            // CssIdentifier for `none`.
            //
            // Important caveat: angle dimensions (deg / turn / grad / rad) are
            // NOT in CssLengthUnit, so any rotate(...) / skew(...) containing
            // an angle currently fails CssValueParser.ParseSingle and
            // GetParsed returns null. We detect that and fall through to the
            // legacy string-walking path (which goes through
            // RawValueParser.TryParseAngleDegrees) so existing tests for
            // rotate/skew keep passing untouched.
            var parsed = style.GetParsed(CssProperties.TransformId);
            if (parsed != null) {
                if (parsed is CssKeyword kw && kw.Identifier == "none") return Transform2D.Identity;
                if (parsed is CssIdentifier id && string.Equals(id.Name, "none", System.StringComparison.OrdinalIgnoreCase)) return Transform2D.Identity;
                if (parsed is CssFunctionCall single) {
                    return ParseStep(single, refWidth, refHeight);
                }
                if (parsed is CssValueList list) {
                    Transform2D result = Transform2D.Identity;
                    for (int i = 0; i < list.Items.Count; i++) {
                        // Mirrors the prior "string fallthrough" behaviour:
                        // any non-function item means the value isn't a
                        // well-formed transform list, so bail out to identity.
                        if (!(list.Items[i] is CssFunctionCall fn)) return Transform2D.Identity;
                        // CSS transform lists compose so the rightmost
                        // function acts on the point first. With
                        // Transform2D.Multiply's "apply this, then other"
                        // contract, pre-multiplying each parsed step gives
                        // `translate(...) rotate(...)` the browser behavior:
                        // rotate the box, then translate it.
                        result = ParseStep(fn, refWidth, refHeight).Multiply(result);
                    }
                    return result;
                }
            }

            // String fallback: the parse tree wasn't usable (typically because
            // the value contains an angle unit the CssValue tokenizer rejects).
            // The raw string still lives in the slot; tokenise it ourselves.
            string raw = style.Get(CssProperties.TransformId);
            if (string.IsNullOrEmpty(raw)) return Transform2D.Identity;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "none")) return Transform2D.Identity;

            var calls = SplitFunctionCalls(raw);
            Transform2D resultStr = Transform2D.Identity;
            foreach (var call in calls) {
                if (!RawValueParser.TryParseFunctionCall(call, out var name, out var inner)) continue;
                var args = RawValueParser.SplitTopLevelCommas(inner);
                Transform2D step = ParseStep(name, args, refWidth, refHeight);
                resultStr = step.Multiply(resultStr);
            }
            return resultStr;
        }

        // Typed step: dispatches on a CssFunctionCall's Name and reads
        // CssValue-shaped Arguments directly. Avoids re-tokenising and
        // re-parsing each arg through CssValue.TryParse on every paint.
        static Transform2D ParseStep(CssFunctionCall fn, double refW, double refH) {
            var args = fn.Arguments;
            string name = CssStringUtil.ToLowerInvariantOrSame(fn.Name);
            switch (name) {
                case "translate": {
                    // CSS Transforms L1: translate(<length-percentage>{1,2}).
                    // Percentage args resolve against the reference box's
                    // width (X axis) and height (Y axis) respectively.
                    double tx = ResolvePx(args, 0, 0, refW);
                    double ty = ResolvePx(args, 1, 0, refH);
                    return Transform2D.Translate((float)tx, (float)ty);
                }
                case "translatex": return Transform2D.Translate((float)ResolvePx(args, 0, 0, refW), 0);
                case "translatey": return Transform2D.Translate(0, (float)ResolvePx(args, 0, 0, refH));
                case "scale": {
                    double sx = ResolveNumber(args, 0, 1);
                    double sy = args.Count > 1 ? ResolveNumber(args, 1, sx) : sx;
                    return Transform2D.Scale((float)sx, (float)sy);
                }
                case "scalex": return Transform2D.Scale((float)ResolveNumber(args, 0, 1), 1);
                case "scaley": return Transform2D.Scale(1, (float)ResolveNumber(args, 0, 1));
                case "rotate": {
                    // Angle units aren't yet representable as a typed CssValue
                    // (CssLengthUnit has no deg/rad/turn/grad), so any
                    // well-formed rotate() doesn't reach this branch — it
                    // hits the string fallback in ResolveTransform. Kept
                    // here for forward-compat when an angle type lands.
                    if (args.Count == 0) return Transform2D.Identity;
                    if (TryAngleDeg(args[0], out var deg)) return Transform2D.Rotate(deg);
                    return Transform2D.Identity;
                }
                case "skew": {
                    double ax = ResolveAngleDeg(args, 0, 0);
                    double ay = ResolveAngleDeg(args, 1, 0);
                    return Skew(ax, ay);
                }
                case "skewx": return Skew(ResolveAngleDeg(args, 0, 0), 0);
                case "skewy": return Skew(0, ResolveAngleDeg(args, 0, 0));
                case "matrix": {
                    if (args.Count < 6) return Transform2D.Identity;
                    float a = (float)ResolveNumber(args, 0, 1);
                    float b = (float)ResolveNumber(args, 1, 0);
                    float c = (float)ResolveNumber(args, 2, 0);
                    float d = (float)ResolveNumber(args, 3, 1);
                    float tx = (float)ResolveNumber(args, 4, 0);
                    float ty = (float)ResolveNumber(args, 5, 0);
                    return new Transform2D(a, b, c, d, tx, ty);
                }
            }
            return Transform2D.Identity;
        }

        // Typed arg resolver: pixel-or-percent. Mirrors the string overload's
        // semantics — px/percent/number/calc — but reads the CssValue subtype
        // directly, no allocation or re-parse.
        static double ResolvePx(IReadOnlyList<CssValue> args, int idx, double fallback, double percentRef) {
            if (idx >= args.Count) return fallback;
            var v = args[idx];
            if (v is CssLength len) {
                // CssLength.Percent goes through the LengthContext basis; for
                // translate we resolve against the caller-supplied axis ref.
                if (len.Unit == CssLengthUnit.Percent) return len.Value * 0.01 * percentRef;
                return len.ToPixels(LengthContext.Default);
            }
            if (v is CssNumber num) return num.Value;
            // CSS Transforms L1 §13.4: translate percentages resolve against
            // the reference box dimension passed by caller (width for X axis,
            // height for Y axis). Without this, `transform: translateX(-50%)`
            // collapses to 0.
            if (v is CssPercentage pct) return pct.Value * 0.01 * percentRef;
            if (v is CssCalc calc) {
                var ctx = LengthContext.Default;
                ctx.BasisPixels = percentRef;
                return calc.Evaluate(ctx);
            }
            return fallback;
        }

        static double ResolveNumber(IReadOnlyList<CssValue> args, int idx, double fallback) {
            if (idx >= args.Count) return fallback;
            var v = args[idx];
            if (v is CssNumber num) return num.Value;
            if (v is CssLength len) return len.Value;
            if (v is CssPercentage p) return p.Value * 0.01;
            if (v is CssCalc calc) return calc.Evaluate(LengthContext.Default);
            return fallback;
        }

        // Typed angle resolver. Falls back through Raw so a stray
        // CssLength-with-angle-unit (future) or CssIdentifier carrying the
        // raw "45deg" text still resolves via the proven string parser.
        static double ResolveAngleDeg(IReadOnlyList<CssValue> args, int idx, double fallback) {
            if (idx >= args.Count) return fallback;
            return TryAngleDeg(args[idx], out var deg) ? deg : fallback;
        }

        static bool TryAngleDeg(CssValue v, out double deg) {
            deg = 0;
            if (v == null) return false;
            // CSS Values 4 §6.1: the parser produces a typed CssAngle for
            // `<number><angle-unit>` dimensions; bare unitless numbers are
            // interpreted as degrees per CSS Transforms L1 §6 (rotate/skew
            // angle grammar).
            if (v is CssAngle a) { deg = a.ToDegrees(); return true; }
            if (v is CssNumber n) { deg = n.Value; return true; }
            if (v.Raw != null && RawValueParser.TryParseAngleDegrees(v.Raw, out var d)) {
                deg = d;
                return true;
            }
            return false;
        }

        // ---- String-fallback path (kept verbatim from the pre-GetParsed
        // implementation). Reached when CssValue.TryParse rejected the raw
        // value — chiefly transforms containing angle units. ----

        static List<string> SplitFunctionCalls(string raw) {
            var list = new List<string>();
            int depth = 0;
            int start = 0;
            int i = 0;
            for (; i < raw.Length; i++) {
                char c = raw[i];
                if (c == '(') depth++;
                else if (c == ')') {
                    depth--;
                    if (depth == 0) {
                        list.Add(raw.Substring(start, i - start + 1).Trim());
                        start = i + 1;
                        while (start < raw.Length && (raw[start] == ' ' || raw[start] == '\t' || raw[start] == ',')) start++;
                        i = start - 1;
                    }
                }
            }
            return list;
        }

        static Transform2D ParseStep(string name, List<string> args, double refW, double refH) {
            switch (name) {
                case "translate": {
                    double tx = ResolvePx(args, 0, 0, refW);
                    double ty = ResolvePx(args, 1, 0, refH);
                    return Transform2D.Translate((float)tx, (float)ty);
                }
                case "translatex": return Transform2D.Translate((float)ResolvePx(args, 0, 0, refW), 0);
                case "translatey": return Transform2D.Translate(0, (float)ResolvePx(args, 0, 0, refH));
                case "scale": {
                    double sx = ResolveNumber(args, 0, 1);
                    double sy = args.Count > 1 ? ResolveNumber(args, 1, sx) : sx;
                    return Transform2D.Scale((float)sx, (float)sy);
                }
                case "scalex": return Transform2D.Scale((float)ResolveNumber(args, 0, 1), 1);
                case "scaley": return Transform2D.Scale(1, (float)ResolveNumber(args, 0, 1));
                case "rotate": {
                    if (args.Count == 0) return Transform2D.Identity;
                    if (RawValueParser.TryParseAngleDegrees(args[0], out var deg)) return Transform2D.Rotate(deg);
                    return Transform2D.Identity;
                }
                case "skew": {
                    double ax = ParseAngleDeg(args, 0, 0);
                    double ay = ParseAngleDeg(args, 1, 0);
                    return Skew(ax, ay);
                }
                case "skewx": return Skew(ParseAngleDeg(args, 0, 0), 0);
                case "skewy": return Skew(0, ParseAngleDeg(args, 0, 0));
                case "matrix": {
                    if (args.Count < 6) return Transform2D.Identity;
                    float a = (float)ResolveNumber(args, 0, 1);
                    float b = (float)ResolveNumber(args, 1, 0);
                    float c = (float)ResolveNumber(args, 2, 0);
                    float d = (float)ResolveNumber(args, 3, 1);
                    float tx = (float)ResolveNumber(args, 4, 0);
                    float ty = (float)ResolveNumber(args, 5, 0);
                    return new Transform2D(a, b, c, d, tx, ty);
                }
            }
            return Transform2D.Identity;
        }

        static Transform2D Skew(double xDeg, double yDeg) {
            double rx = xDeg * System.Math.PI / 180.0;
            double ry = yDeg * System.Math.PI / 180.0;
            float a = 1f;
            float b = (float)System.Math.Tan(ry);
            float c = (float)System.Math.Tan(rx);
            float d = 1f;
            return new Transform2D(a, b, c, d, 0, 0);
        }

        static double ResolvePx(List<string> args, int idx, double fallback, double percentRef = 0) {
            if (idx >= args.Count) return fallback;
            string raw = args[idx].Trim();
            if (string.IsNullOrEmpty(raw)) return fallback;
            if (CssValue.TryParse(raw, out var v)) {
                if (v is CssLength len) return len.ToPixels(LengthContext.Default);
                if (v is CssNumber num) return num.Value;
                if (v is CssPercentage pct) return pct.Value * 0.01 * percentRef;
            }
            if (raw.EndsWith("px") && double.TryParse(raw.AsSpan(0, raw.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var px)) {
                return px;
            }
            if (raw.EndsWith("%") && double.TryParse(raw.AsSpan(0, raw.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var pctn)) {
                return pctn * 0.01 * percentRef;
            }
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return n;
            return fallback;
        }

        static double ResolveNumber(List<string> args, int idx, double fallback) {
            if (idx >= args.Count) return fallback;
            string raw = args[idx].Trim();
            if (CssValue.TryParse(raw, out var v)) {
                if (v is CssNumber num) return num.Value;
                if (v is CssLength len) return len.Value;
                if (v is CssPercentage p) return p.Value * 0.01;
            }
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return n;
            return fallback;
        }

        static double ParseAngleDeg(List<string> args, int idx, double fallback) {
            if (idx >= args.Count) return fallback;
            if (RawValueParser.TryParseAngleDegrees(args[idx], out var deg)) return deg;
            return fallback;
        }

        // ---- CSS Transforms L2 §3 — individual transform properties ----

        static Transform2D ResolveIndividualTranslate(ComputedStyle style, double refW, double refH) {
            string raw = style.Get(TranslateId);
            if (string.IsNullOrEmpty(raw)) return Transform2D.Identity;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "none")) return Transform2D.Identity;
            var parts = RawValueParser.SplitTopLevelSpaces(raw);
            // `translate: <length-percentage> [<length-percentage> [<length>]]?`
            // 2D paint ignores the Z component but tolerates its presence.
            double tx = parts.Count > 0 ? ResolvePx(parts, 0, 0, refW) : 0;
            double ty = parts.Count > 1 ? ResolvePx(parts, 1, 0, refH) : 0;
            return Transform2D.Translate((float)tx, (float)ty);
        }

        static Transform2D ResolveIndividualRotate(ComputedStyle style) {
            string raw = style.Get(RotateId);
            if (string.IsNullOrEmpty(raw)) return Transform2D.Identity;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "none")) return Transform2D.Identity;
            var parts = RawValueParser.SplitTopLevelSpaces(raw);
            // Grammar (Transforms L2 §3.3):
            //   <angle> | [ x | y | z | <number>{3} ] && <angle>
            // For 2D paint a rotation about Z (or unspecified axis) maps to
            // a planar rotation; rotation about X/Y is approximated as
            // identity since v1 only emits 2D transforms.
            if (parts.Count == 0) return Transform2D.Identity;
            if (parts.Count == 1) {
                return RawValueParser.TryParseAngleDegrees(parts[0], out var deg)
                    ? Transform2D.Rotate(deg)
                    : Transform2D.Identity;
            }
            // Last token is the angle; preceding tokens are the axis spec.
            string angleTok = parts[parts.Count - 1];
            if (!RawValueParser.TryParseAngleDegrees(angleTok, out var degN)) return Transform2D.Identity;
            if (parts.Count == 2) {
                string axis = CssStringUtil.ToLowerInvariantOrSame(parts[0].Trim());
                if (axis == "z") return Transform2D.Rotate(degN);
                if (axis == "x" || axis == "y") return Transform2D.Identity;
                return Transform2D.Identity;
            }
            if (parts.Count == 4) {
                // `<number> <number> <number> <angle>` — only the Z component
                // contributes to 2D paint. A pure-Z axis (0 0 1) rotates;
                // anything else is approximated as identity.
                if (!RawValueParser.TryParseNumber(parts[0], out var ax)) return Transform2D.Identity;
                if (!RawValueParser.TryParseNumber(parts[1], out var ay)) return Transform2D.Identity;
                if (!RawValueParser.TryParseNumber(parts[2], out var az)) return Transform2D.Identity;
                if (ax == 0 && ay == 0 && az != 0) return Transform2D.Rotate(az < 0 ? -degN : degN);
                return Transform2D.Identity;
            }
            return Transform2D.Identity;
        }

        static Transform2D ResolveIndividualScale(ComputedStyle style) {
            string raw = style.Get(ScaleId);
            if (string.IsNullOrEmpty(raw)) return Transform2D.Identity;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "none")) return Transform2D.Identity;
            var parts = RawValueParser.SplitTopLevelSpaces(raw);
            // `scale: <number-percentage>{1,3}` — Z is dropped in 2D paint.
            if (parts.Count == 0) return Transform2D.Identity;
            if (!TryParseScaleComponent(parts[0], out double sx)) return Transform2D.Identity;
            double sy = sx;
            if (parts.Count > 1 && !TryParseScaleComponent(parts[1], out sy)) sy = sx;
            return Transform2D.Scale((float)sx, (float)sy);
        }

        static bool TryParseScaleComponent(string raw, out double value) {
            value = 1;
            if (string.IsNullOrEmpty(raw)) return false;
            string t = raw.Trim();
            if (t.EndsWith("%")) {
                if (double.TryParse(t.AsSpan(0, t.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) {
                    value = pct * 0.01;
                    return true;
                }
                return false;
            }
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
