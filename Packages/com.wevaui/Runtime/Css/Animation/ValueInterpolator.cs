using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Weva.Animation;
using Weva.Css.Values;
using Weva.Diagnostics;
using Weva.Paint;
using Weva.Paint.Filters;

namespace Weva.Css.Animation {
    public static partial class ValueInterpolator {
        // String-keyed entry point. Kept for the existing test surface
        // (ValueInterpolatorTests / KeyframeAnimationTests) and for any
        // caller that hasn't been migrated to the parsed API. New hot-path
        // callers should use InterpolateParsed, which skips two re-parses
        // per frame per animated property.
        public static string Interpolate(string from, string to, double t, PropertyKind kind, LengthContext ctx) {
            if (from == null) from = "";
            if (to == null) to = "";
            if (t <= 0) return from;
            if (t >= 1) return to;

            // For typed-numeric paths (number/length/percentage/color/transform)
            // route through the parsed entry — TryParse is memoized so the
            // legacy string overload still benefits from the global cache;
            // the parsed overload is what new callers (CssAnimationRunner +
            // pre-parsed keyframe sampling) use to avoid the dictionary
            // probe per frame.
            switch (kind) {
                case PropertyKind.Discrete:
                    return t < 0.5 ? from : to;
                case PropertyKind.Number: {
                    var fv = TryParseValue(from);
                    var tv = TryParseValue(to);
                    return InterpolateNumberTyped(fv, tv, t, from, to);
                }
                case PropertyKind.Integer: {
                    var fv = TryParseValue(from);
                    var tv = TryParseValue(to);
                    return InterpolateIntegerTyped(fv, tv, t, from, to);
                }
                case PropertyKind.Length: {
                    var fv = TryParseValue(from);
                    var tv = TryParseValue(to);
                    return InterpolateLengthTyped(fv, tv, t, ctx, from, to);
                }
                case PropertyKind.Percentage: {
                    var fv = TryParseValue(from);
                    var tv = TryParseValue(to);
                    return InterpolatePercentageTyped(fv, tv, t, from, to);
                }
                case PropertyKind.Color: {
                    var fv = TryParseValue(from);
                    var tv = TryParseValue(to);
                    return InterpolateColorTyped(fv, tv, t, from, to);
                }
                case PropertyKind.Transform:
                    // Transform args can carry angle units (deg/rad/grad/turn)
                    // that CssValue.TryParse rejects, so the transform path
                    // stays on the string tokenizer. The other numeric paths
                    // route through the typed surface.
                    return InterpolateTransform(from, to, t, ctx);
                case PropertyKind.Filter:
                    return InterpolateFilter(from, to, t, ctx);
                case PropertyKind.BackgroundPosition:
                    return InterpolateBackgroundPosition(from, to, t, ctx);
                case PropertyKind.BackgroundSize:
                    return InterpolateBackgroundSize(from, to, t, ctx);
                case PropertyKind.BoxShadow:
                    return InterpolateShadowList(from, to, t, ctx, hasSpread: true);
                case PropertyKind.TextShadow:
                    return InterpolateShadowList(from, to, t, ctx, hasSpread: false);
                case PropertyKind.ClipPath:
                    return InterpolateClipPath(from, to, t, ctx);
                case PropertyKind.Translate:
                    return InterpolateIndividualTranslate(from, to, t, ctx);
                case PropertyKind.Rotate:
                    return InterpolateIndividualRotate(from, to, t, ctx);
                case PropertyKind.Scale:
                    return InterpolateIndividualScale(from, to, t);
                case PropertyKind.Gradient:
                    return InterpolateGradient(from, to, t);
                default:
                    return t < 0.5 ? from : to;
            }
        }

        // Parsed entry point. Both sides have already been through
        // CssValue.TryParse exactly once (cached on the keyframe / per-style
        // GetParsed slot) — every subsequent animation frame skips the
        // parse + dictionary probe entirely.
        //
        // Returns the same string contract as Interpolate(string, string,…)
        // since both AnimationInstance.SampleAtPhaseOne and
        // RunningTransitionRecord.CurrentText hold strings (the value
        // ultimately re-enters ComputedStyle via Set(string,string), and
        // changing that contract is out of scope).
        //
        // `fromRaw` / `toRaw` are the original strings — used only as the
        // fallback when the parse tree doesn't match the kind (e.g. an
        // "auto" identifier on a Length-kinded property) and discrete-step
        // semantics kick in.
        public static string InterpolateParsed(CssValue from, CssValue to, string fromRaw, string toRaw, double t, PropertyKind kind, LengthContext ctx) {
            if (fromRaw == null) fromRaw = "";
            if (toRaw == null) toRaw = "";
            if (t <= 0) return fromRaw;
            if (t >= 1) return toRaw;

            switch (kind) {
                case PropertyKind.Discrete:
                    return t < 0.5 ? fromRaw : toRaw;
                case PropertyKind.Number:
                    return InterpolateNumberTyped(from, to, t, fromRaw, toRaw);
                case PropertyKind.Integer:
                    return InterpolateIntegerTyped(from, to, t, fromRaw, toRaw);
                case PropertyKind.Length:
                    return InterpolateLengthTyped(from, to, t, ctx, fromRaw, toRaw);
                case PropertyKind.Percentage:
                    return InterpolatePercentageTyped(from, to, t, fromRaw, toRaw);
                case PropertyKind.Color:
                    return InterpolateColorTyped(from, to, t, fromRaw, toRaw);
                case PropertyKind.Transform:
                    // Transforms still go through the string tokenizer for
                    // angle support. See note in Interpolate().
                    return InterpolateTransform(fromRaw, toRaw, t, ctx);
                case PropertyKind.Filter:
                    return InterpolateFilter(fromRaw, toRaw, t, ctx);
                case PropertyKind.BackgroundPosition:
                    // The multi-component interpolators all re-tokenise from
                    // the raw string — the structured CssValue tree doesn't
                    // currently carry the per-axis split for shorthand-like
                    // values (e.g. `center top` resolves to two identifiers).
                    return InterpolateBackgroundPosition(fromRaw, toRaw, t, ctx);
                case PropertyKind.BackgroundSize:
                    return InterpolateBackgroundSize(fromRaw, toRaw, t, ctx);
                case PropertyKind.BoxShadow:
                    return InterpolateShadowList(fromRaw, toRaw, t, ctx, hasSpread: true);
                case PropertyKind.TextShadow:
                    return InterpolateShadowList(fromRaw, toRaw, t, ctx, hasSpread: false);
                case PropertyKind.ClipPath:
                    return InterpolateClipPath(fromRaw, toRaw, t, ctx);
                case PropertyKind.Translate:
                    return InterpolateIndividualTranslate(fromRaw, toRaw, t, ctx);
                case PropertyKind.Rotate:
                    return InterpolateIndividualRotate(fromRaw, toRaw, t, ctx);
                case PropertyKind.Scale:
                    return InterpolateIndividualScale(fromRaw, toRaw, t);
                case PropertyKind.Gradient:
                    return InterpolateGradient(fromRaw, toRaw, t);
                default:
                    return t < 0.5 ? fromRaw : toRaw;
            }
        }

        // --- A9: CSS Images L3 §3.5 — gradient stop interpolation ---
        //
        // Both endpoints must be the same gradient type (linear/radial/conic),
        // with the same angle/direction/shape metadata and the same number of
        // stops. When all conditions pass, each stop's position and color lerps
        // independently. Any mismatch falls back to discrete (t < 0.5 ? from : to).
        //
        // The implementation delegates parsing to BackgroundResolver.TryParseGradient
        // (internal, accessible from the same assembly). Output is serialized back to
        // a CSS gradient string so the result round-trips through ComputedStyle.Set.
        //
        // Color lerp uses linear-RGB (Interpolator.LerpColor) to stay consistent
        // with gradient.Sample() which already uses linear-RGB for the stop-to-stop
        // ramp. Animation value-interpolation uses oklab (InterpolateColorTyped)
        // for the solid-color case, but for gradient stops the gradient shader itself
        // does linear-RGB interpolation between stops — matching that space here
        // gives the smoothest visual result across the transition frames.
        static string InterpolateGradient(string fromRaw, string toRaw, double t) {
            if (fromRaw == null) fromRaw = "";
            if (toRaw == null) toRaw = "";

            var fromGrad = ParseGradientFromRaw(fromRaw);
            var toGrad = ParseGradientFromRaw(toRaw);

            // If either side isn't a parseable gradient, discrete.
            if (fromGrad == null || toGrad == null) return t < 0.5 ? fromRaw : toRaw;

            // Types must match: both linear, both radial, or both conic.
            if (fromGrad.GetType() != toGrad.GetType()) return t < 0.5 ? fromRaw : toRaw;

            // Stop count must match.
            if (fromGrad.Stops.Count != toGrad.Stops.Count) return t < 0.5 ? fromRaw : toRaw;

            // Linear: angles must match (no rotation lerp in v1).
            if (fromGrad is Weva.Paint.LinearGradient fl && toGrad is Weva.Paint.LinearGradient tl) {
                if (Math.Abs(fl.AngleDegrees - tl.AngleDegrees) > 1e-6) return t < 0.5 ? fromRaw : toRaw;
                return SerializeLinearGradient(fl, tl, t);
            }

            // Radial: shape must match (shape enum + center coords expected same).
            if (fromGrad is Weva.Paint.RadialGradient fr && toGrad is Weva.Paint.RadialGradient tr) {
                if (fr.Shape != tr.Shape) return t < 0.5 ? fromRaw : toRaw;
                return SerializeRadialGradient(fr, tr, t);
            }

            // Conic: from-angle and center must match.
            if (fromGrad is Weva.Paint.ConicGradient fc && toGrad is Weva.Paint.ConicGradient tc) {
                if (Math.Abs(fc.FromAngleDegrees - tc.FromAngleDegrees) > 1e-6) return t < 0.5 ? fromRaw : toRaw;
                return SerializeConicGradient(fc, tc, t);
            }

            return t < 0.5 ? fromRaw : toRaw;
        }

        // Serialize a per-stop-lerped linear-gradient back to CSS text.
        static string SerializeLinearGradient(Weva.Paint.LinearGradient from, Weva.Paint.LinearGradient to, double t) {
            var sb = new StringBuilder(64);
            sb.Append("linear-gradient(");
            sb.Append(Format(from.AngleDegrees));
            sb.Append("deg");
            AppendInterpolatedStops(sb, from, to, t);
            sb.Append(')');
            return sb.ToString();
        }

        static string SerializeRadialGradient(Weva.Paint.RadialGradient from, Weva.Paint.RadialGradient to, double t) {
            var sb = new StringBuilder(64);
            sb.Append("radial-gradient(");
            // Emit shape keyword; omit explicit radii/center to keep output simple.
            sb.Append(from.Shape == Weva.Paint.RadialGradientShape.Circle ? "circle" : "ellipse");
            AppendInterpolatedStops(sb, from, to, t);
            sb.Append(')');
            return sb.ToString();
        }

        static string SerializeConicGradient(Weva.Paint.ConicGradient from, Weva.Paint.ConicGradient to, double t) {
            var sb = new StringBuilder(64);
            sb.Append("conic-gradient(from ");
            sb.Append(Format(from.FromAngleDegrees));
            sb.Append("deg");
            AppendInterpolatedStops(sb, from, to, t);
            sb.Append(')');
            return sb.ToString();
        }

        // Append ", <stop0>, <stop1>, ..." to `sb`, lerping each stop's color
        // and position from the two gradient endpoints.
        static void AppendInterpolatedStops(StringBuilder sb, Weva.Paint.Gradient from, Weva.Paint.Gradient to, double t) {
            for (int i = 0; i < from.Stops.Count; i++) {
                var fs = from.Stops[i];
                var ts = to.Stops[i];
                sb.Append(", ");
                // Lerp color in linear-RGB (matches the gradient shader's color
                // space — see implementation note above).
                var color = Weva.Animation.Interpolator.LerpColor(fs.Color, ts.Color, t);
                AppendLinearColorAsSrgb(sb, color);
                // Lerp position (0–1 normalized stop position → % for CSS output).
                double pos = fs.Position + (ts.Position - fs.Position) * t;
                sb.Append(' ');
                sb.Append(Format(pos * 100.0));
                sb.Append('%');
            }
        }

        // Convert a LinearColor (linear-RGB) back to sRGB bytes and emit as rgb()/rgba().
        static void AppendLinearColorAsSrgb(StringBuilder sb, Weva.Paint.LinearColor c) {
            byte r = LinearToSrgbByte(c.R);
            byte g = LinearToSrgbByte(c.G);
            byte b = LinearToSrgbByte(c.B);
            if (c.A >= 1f - 1e-4f) {
                sb.Append("rgb(");
                sb.Append(r); sb.Append(", ");
                sb.Append(g); sb.Append(", ");
                sb.Append(b); sb.Append(')');
            } else {
                sb.Append("rgba(");
                sb.Append(r); sb.Append(", ");
                sb.Append(g); sb.Append(", ");
                sb.Append(b); sb.Append(", ");
                sb.Append(Format(c.A)); sb.Append(')');
            }
        }

        // Parse a raw CSS gradient string into a Gradient AST.
        // Uses a zero-bounds Rect (sufficient for linear gradients; radial/conic
        // center keywords resolve against bounds but the v1 shape-match contract
        // requires centers to match so the value is unused in per-stop lerp).
        static Weva.Paint.Gradient ParseGradientFromRaw(string raw) {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string trimmed = raw.Trim();
            if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
            if (!Weva.Paint.Conversion.RawValueParser.TryParseFunctionCall(trimmed, out var name, out var body)) return null;
            if (string.IsNullOrEmpty(name)) return null;
            var argStrings = Weva.Paint.Conversion.RawValueParser.SplitTopLevelCommas(body);
            var fauxArgs = new List<CssValue>(argStrings.Count);
            for (int i = 0; i < argStrings.Count; i++) {
                string a = argStrings[i].Trim();
                fauxArgs.Add(new CssIdentifier(a, a));
            }
            var fn = new CssFunctionCall(name, fauxArgs, trimmed);
            return Weva.Paint.Conversion.BackgroundResolver.TryParseGradient(fn, Weva.Paint.LinearColor.Black, default);
        }

        static string InterpolateFilter(string fromRaw, string toRaw, double t, LengthContext ctx) {
            if (fromRaw == null) fromRaw = "";
            if (toRaw == null) toRaw = "";
            if (t <= 0) return fromRaw;
            if (t >= 1) return toRaw;

            FilterChain from;
            FilterChain to;
            try {
                from = FilterParser.Parse(fromRaw, ctx);
                to = FilterParser.Parse(toRaw, ctx);
            } catch (FilterParseException ex) {
                // EC10: by-design discrete-step on filter parse failure (CSS
                // Animations L1: unparseable endpoint → discrete). Warn once
                // per offending endpoint pair so the author sees their filter
                // expression was rejected.
                WarnFilterParseFailure(fromRaw, toRaw, ex);
                return t < 0.5 ? fromRaw : toRaw;
            }

            if (from.IsEmpty && to.IsEmpty) return "none";
            IReadOnlyList<FilterFunction> a = from.Functions;
            IReadOnlyList<FilterFunction> b = to.Functions;
            if (from.IsEmpty) {
                a = BuildIdentityFilterList(b);
                if (a == null) return t < 0.5 ? fromRaw : toRaw;
            } else if (to.IsEmpty) {
                b = BuildIdentityFilterList(a);
                if (b == null) return t < 0.5 ? fromRaw : toRaw;
            }
            if (a.Count != b.Count) return t < 0.5 ? fromRaw : toRaw;

            var result = new List<FilterFunction>(a.Count);
            for (int i = 0; i < a.Count; i++) {
                if (a[i] == null || b[i] == null || a[i].Kind != b[i].Kind) {
                    return t < 0.5 ? fromRaw : toRaw;
                }
                var fn = InterpolateFilterFunction(a[i], b[i], t);
                if (fn == null) return t < 0.5 ? fromRaw : toRaw;
                result.Add(fn);
            }
            return new FilterChain(result).ToText();
        }

        static IReadOnlyList<FilterFunction> BuildIdentityFilterList(IReadOnlyList<FilterFunction> shape) {
            if (shape == null || shape.Count == 0) return Array.Empty<FilterFunction>();
            var list = new List<FilterFunction>(shape.Count);
            for (int i = 0; i < shape.Count; i++) {
                var identity = IdentityFilter(shape[i]?.Kind ?? FilterKind.DropShadow);
                if (identity == null) return null;
                list.Add(identity);
            }
            return list;
        }

        static FilterFunction IdentityFilter(FilterKind kind) {
            switch (kind) {
                case FilterKind.Blur: return new BlurFilter(0);
                case FilterKind.Brightness: return new BrightnessFilter(1);
                case FilterKind.Contrast: return new ContrastFilter(1);
                case FilterKind.Grayscale: return new GrayscaleFilter(0);
                case FilterKind.Opacity: return new OpacityFilter(1);
                case FilterKind.Saturate: return new SaturateFilter(1);
                case FilterKind.HueRotate: return new HueRotateFilter(0);
                case FilterKind.Invert: return new InvertFilter(0);
                case FilterKind.Sepia: return new SepiaFilter(0);
                default: return null;
            }
        }

        static FilterFunction InterpolateFilterFunction(FilterFunction a, FilterFunction b, double t) {
            switch (a.Kind) {
                case FilterKind.Blur:
                    return new BlurFilter(Lerp(((BlurFilter)a).RadiusPx, ((BlurFilter)b).RadiusPx, t));
                case FilterKind.Brightness:
                    return new BrightnessFilter(Lerp(((BrightnessFilter)a).Amount, ((BrightnessFilter)b).Amount, t));
                case FilterKind.Contrast:
                    return new ContrastFilter(Lerp(((ContrastFilter)a).Amount, ((ContrastFilter)b).Amount, t));
                case FilterKind.Grayscale:
                    return new GrayscaleFilter(Lerp(((GrayscaleFilter)a).Amount, ((GrayscaleFilter)b).Amount, t));
                case FilterKind.Opacity:
                    return new OpacityFilter(Lerp(((OpacityFilter)a).Amount, ((OpacityFilter)b).Amount, t));
                case FilterKind.Saturate:
                    return new SaturateFilter(Lerp(((SaturateFilter)a).Amount, ((SaturateFilter)b).Amount, t));
                case FilterKind.HueRotate:
                    return new HueRotateFilter(Lerp(((HueRotateFilter)a).DegreesNormalized, ((HueRotateFilter)b).DegreesNormalized, t));
                case FilterKind.Invert:
                    return new InvertFilter(Lerp(((InvertFilter)a).Amount, ((InvertFilter)b).Amount, t));
                case FilterKind.Sepia:
                    return new SepiaFilter(Lerp(((SepiaFilter)a).Amount, ((SepiaFilter)b).Amount, t));
                case FilterKind.DropShadow: {
                    var da = (DropShadowFilter)a;
                    var db = (DropShadowFilter)b;
                    return new DropShadowFilter(
                        Lerp(da.OffsetX, db.OffsetX, t),
                        Lerp(da.OffsetY, db.OffsetY, t),
                        Lerp(da.BlurRadius, db.BlurRadius, t),
                        Interpolator.LerpColor(da.Color, db.Color, t));
                }
                default:
                    return null;
            }
        }

        static double Lerp(double a, double b, double t) => a + (b - a) * t;

        static CssValue TryParseValue(string text) {
            if (string.IsNullOrEmpty(text)) return null;
            return CssValue.TryParse(text, out var v) ? v : null;
        }

        static string InterpolateNumberTyped(CssValue from, CssValue to, double t, string fromRaw, string toRaw) {
            if (TryAsNumber(from, out double a) && TryAsNumber(to, out double b)) {
                double v = a + (b - a) * t;
                return Format(v);
            }
            return t < 0.5 ? fromRaw : toRaw;
        }

        // CSS Transitions L1 §2.3 — integer-typed animatable values interpolate
        // as real numbers and the result is rounded to the nearest integer for
        // exposure (getComputedStyle, ComputedStyle.Get). Ties round away from
        // zero to match browser convention.
        static string InterpolateIntegerTyped(CssValue from, CssValue to, double t, string fromRaw, string toRaw) {
            if (TryAsNumber(from, out double a) && TryAsNumber(to, out double b)) {
                double v = a + (b - a) * t;
                long rounded = (long)Math.Round(v, MidpointRounding.AwayFromZero);
                return rounded.ToString(CultureInfo.InvariantCulture);
            }
            return t < 0.5 ? fromRaw : toRaw;
        }

        static string InterpolatePercentageTyped(CssValue from, CssValue to, double t, string fromRaw, string toRaw) {
            if (TryAsPercentage(from, out double a) && TryAsPercentage(to, out double b)) {
                double v = a + (b - a) * t;
                return Format(v) + "%";
            }
            return InterpolateNumberTyped(from, to, t, fromRaw, toRaw);
        }

        static string InterpolateLengthTyped(CssValue from, CssValue to, double t, LengthContext ctx, string fromRaw, string toRaw) {
            var fl = AsLength(from);
            var tl = AsLength(to);
            if (fl != null && tl != null) {
                if (fl.Unit == tl.Unit) {
                    double v = fl.Value + (tl.Value - fl.Value) * t;
                    return Format(v) + UnitSuffix(fl.Unit);
                }
                try {
                    double pa = fl.ToPixels(ctx);
                    double pb = tl.ToPixels(ctx);
                    double v = pa + (pb - pa) * t;
                    return Format(v) + "px";
                } catch (InvalidOperationException) {
                    return t < 0.5 ? fromRaw : toRaw;
                }
            }
            if (TryAsNumber(from, out double na) && TryAsNumber(to, out double nb)) {
                double v = na + (nb - na) * t;
                return Format(v);
            }
            return t < 0.5 ? fromRaw : toRaw;
        }

        static string InterpolateColorTyped(CssValue from, CssValue to, double t, string fromRaw, string toRaw) {
            CssColor a = AsColor(from);
            CssColor b = AsColor(to);
            if (a == null || b == null) return t < 0.5 ? fromRaw : toRaw;
            // CSS Color L4 §12 + CSS Transitions L1: color animations interpolate in
            // oklab by default — linear-RGB component lerp on display-referred sRGB
            // input gives perceptibly off mid-stops (e.g. red→blue at t=0.5 reads as
            // a muddy gray instead of the perceptually-uniform purple oklab yields).
            // Convert each endpoint sRGB byte → linear-RGB → oklab, lerp the (L,a,b)
            // components in oklab space, convert back to linear-RGB → sRGB byte.
            // Alpha is straight-component (not premultiplied) and lerps linearly per
            // spec — the existing typed LerpColor in Interpolator is also straight.
            // The Interpolator.LerpColor path (still linear-RGB) is intentionally
            // untouched so gradient evaluation + DropShadow color stays on its
            // existing space; this method is the value-interpolator color animation
            // / transition surface only.
            var la = LinearColor.FromCssColor(a);
            var lb = LinearColor.FromCssColor(b);
            CssColor.LinearRgbToOklab(la.R, la.G, la.B, out double aL, out double aA, out double aB);
            CssColor.LinearRgbToOklab(lb.R, lb.G, lb.B, out double bL, out double bA, out double bB);
            double L = aL + (bL - aL) * t;
            double A = aA + (bA - aA) * t;
            double B = aB + (bB - aB) * t;
            CssColor.OklabToLinearRgb(L, A, B, out double lr, out double lg, out double lbl);
            byte r = LinearToSrgbByte((float)lr);
            byte g = LinearToSrgbByte((float)lg);
            byte bb = LinearToSrgbByte((float)lbl);
            float alpha = (float)(la.A + (lb.A - la.A) * t);
            if (alpha >= 1f - 1e-4f) {
                return string.Format(CultureInfo.InvariantCulture, "rgb({0}, {1}, {2})", r, g, bb);
            }
            return string.Format(CultureInfo.InvariantCulture, "rgba({0}, {1}, {2}, {3})", r, g, bb, Format(alpha));
        }

        // --- typed-value accessors ---

        static bool TryAsNumber(CssValue v, out double value) {
            if (v is CssNumber n) { value = n.Value; return true; }
            if (v is CssLength l && l.Unit == CssLengthUnit.Px) { value = l.Value; return true; }
            if (v != null && v.Raw != null) {
                return double.TryParse(v.Raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            }
            value = 0;
            return false;
        }

        static bool TryAsPercentage(CssValue v, out double value) {
            if (v is CssPercentage p) { value = p.Value; return true; }
            if (v is CssLength l && l.Unit == CssLengthUnit.Percent) { value = l.Value; return true; }
            value = 0;
            return false;
        }

        static CssLength AsLength(CssValue v) {
            if (v is CssLength l) return l;
            if (v is CssPercentage p) return new CssLength(p.Value, CssLengthUnit.Percent, p.Raw);
            // Bare zero behaves as 0px for length interpolation (see
            // ValueInterpolatorTests.Length_with_both_zero_works).
            if (v is CssNumber n && n.Value == 0) return CssLength.Zero;
            return null;
        }

        static CssColor AsColor(CssValue v) {
            if (v is CssColor cc) return cc;
            if (v is CssIdentifier id && CssColor.TryFromName(id.Name, out var named)) return named;
            return null;
        }

        // --- transform path: unchanged string-tokenizer impl (angle units
        // aren't representable as a typed CssValue, see TransformResolver).

        // Process-static cache of parsed transform-function lists keyed on
        // the raw input text. Animation keyframes hold stable strings —
        // `rotate(0deg)` / `rotate(360deg)` for the gem-spin case — so the
        // parsed shape is invariant across Ticks. Without this cache the
        // hot path re-tokenised both endpoints every frame and allocated
        // a fresh List<TransformFn> + per-function Args list + new
        // string-trimmed arg slots. Profile attributed ~75% of the per-
        // tile per-frame transform-animation alloc to those parses.
        //
        // RC4: this cache, `identityListCache` below, and the reusable
        // `transformOutSb` StringBuilder are single-threaded by Unity
        // main-thread convention. The StringBuilder reuse is the highest
        // re-entrancy risk in the file — a callback that re-enters
        // InterpolateTransform mid-build would scramble both outputs into
        // one string. The public entrypoints (`Interpolate`,
        // `InterpolateTransform`) call UIMainThreadGuard.AssertMainThread so
        // a misuse fires a debug-build assertion rather than corrupting.
        static readonly Dictionary<string, List<TransformFn>> transformFnCache = new Dictionary<string, List<TransformFn>>();
        const int TransformFnCacheCap = 256;
        // Reusable StringBuilder for InterpolateTransform's output. Cleared
        // on entry; the result is materialised via ToString() and the
        // builder slot is reused for the next call. See RC4 note above —
        // re-entrancy on this slot would scramble both outputs.
        static readonly StringBuilder transformOutSb = new StringBuilder(64);

        // Fast-classifier used by AnimationInstance's typed rotate overlay.
        // Returns true and the degree value when `text` is a single
        // \`rotate(<angle>)\` with an angle-classified arg, OR an empty/none
        // transform (rotate identity = 0deg). Caller uses this to decide
        // whether to take the typed in-place update path that bypasses
        // sb.ToString() + Format(double).
        public static bool TryReadSingleRotateDeg(string text, out double degrees) {
            degrees = 0;
            if (string.IsNullOrWhiteSpace(text)) return true; // implicit identity
            var trimmed = text.Trim();
            if (string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase)) return true;
            var fns = ParseTransformFunctionsCached(trimmed);
            if (fns == null) return false;
            if (fns.Count == 0) return true;
            if (fns.Count != 1) return false;
            var fn = fns[0];
            if (!string.Equals(fn.Name, "rotate", StringComparison.OrdinalIgnoreCase)) return false;
            if (fn.Args.Count != 1) return false;
            if (fn.ArgKinds.Count == 0 || fn.ArgKinds[0] != TransformArgKind.AngleDeg) return false;
            degrees = fn.ParsedNumeric[0];
            return true;
        }

        // Per-animation typed-transform overlay state. Owned by the caller
        // (typically AnimationInstance). Refs to the typed args are stable
        // across Ticks once built, so ComputedStyle.parsedValues[transformId]
        // can keep pointing at the same CssValueList graph and only the
        // mutable backing fields of each CssLength/CssNumber/CssAngle update.
        public sealed class TransformTypedOverlay {
            // Public face — caller assigns this into typedSampleResult.
            public CssValueList List;
            // Internal mutable backing: parallel to List.Values's entries.
            internal List<CssValue> backingArgs;       // backing for List.Values
            internal List<List<CssValue>> perFnArgs;   // each fn's args list
            internal List<CssValue[]> perFnArgRefs;    // typed refs, parallel to perFnArgs
            // The TransformFn list pair the overlay was last built against.
            // Identity-compared next Tick to skip the rebuild cost. Source
            // lists come from the process-static ParseTransformFunctionsCached,
            // so identity is stable as long as the keyframe strings haven't
            // mutated.
            internal object lastFromList;
            internal object lastToList;
        }

        // Mutates `overlay` in place from interpolated `from`/`to` transform
        // strings. Returns true when both endpoints parse to the same fn
        // signature (names + arg counts + same per-arg numeric kinds), the
        // typed path can carry every arg, and overlay.List was updated.
        // Returns false to signal a fallback to the string interpolation
        // path; caller should clear typedSampleResult[transform] if so.
        public static bool TryUpdateTransformTyped(string from, string to, double t, TransformTypedOverlay overlay) {
            if (overlay == null) return false;
            var fl = ParseTransformFunctionsCached(from);
            var tl = ParseTransformFunctionsCached(to);
            if (fl == null || tl == null) return false;
            if (fl.Count == 0 && tl.Count > 0) fl = MakeIdentityListCached(tl);
            else if (tl.Count == 0 && fl.Count > 0) tl = MakeIdentityListCached(fl);
            if (fl.Count == 0 && tl.Count == 0) return false; // nothing to animate
            if (fl.Count != tl.Count) return false;
            for (int i = 0; i < fl.Count; i++) {
                if (!string.Equals(fl[i].Name, tl[i].Name, StringComparison.OrdinalIgnoreCase)) return false;
                if (fl[i].ArgKinds.Count != tl[i].ArgKinds.Count) return false;
                for (int j = 0; j < fl[i].ArgKinds.Count; j++) {
                    var fk = fl[i].ArgKinds[j];
                    var tk = tl[i].ArgKinds[j];
                    if (fk != tk) return false;
                    if (fk == TransformArgKind.None) return false;
                    if (fk == TransformArgKind.Length) {
                        if (fl[i].ParsedLengthUnit[j] != tl[i].ParsedLengthUnit[j]) return false;
                    }
                }
            }
            // Rebuild the overlay graph when the shape changes (first call
            // OR keyframe strings differ from the last build).
            if (!ReferenceEquals(overlay.lastFromList, fl) || !ReferenceEquals(overlay.lastToList, tl)) {
                BuildTransformOverlay(fl, overlay);
                overlay.lastFromList = fl;
                overlay.lastToList = tl;
            }
            // Walk the (pre-classified) numerics, lerp, Reset each typed arg.
            for (int i = 0; i < fl.Count; i++) {
                var ffn = fl[i];
                var tfn = tl[i];
                var argRefs = overlay.perFnArgRefs[i];
                for (int j = 0; j < ffn.ArgKinds.Count; j++) {
                    double va = ffn.ParsedNumeric[j];
                    double vb = tfn.ParsedNumeric[j];
                    double v = va + (vb - va) * t;
                    var argRef = argRefs[j];
                    switch (ffn.ArgKinds[j]) {
                        case TransformArgKind.AngleDeg:
                            ((CssAngle)argRef).Reset(v, CssAngleUnit.Deg, null);
                            break;
                        case TransformArgKind.Length:
                            ((CssLength)argRef).Reset(v, ffn.ParsedLengthUnit[j], null);
                            break;
                        case TransformArgKind.Number:
                            ((CssNumber)argRef).Reset(v, null);
                            break;
                    }
                }
            }
            return true;
        }

        static void BuildTransformOverlay(List<TransformFn> shape, TransformTypedOverlay overlay) {
            if (overlay.backingArgs == null) overlay.backingArgs = new List<CssValue>(shape.Count);
            else overlay.backingArgs.Clear();
            if (overlay.perFnArgs == null) overlay.perFnArgs = new List<List<CssValue>>(shape.Count);
            else overlay.perFnArgs.Clear();
            if (overlay.perFnArgRefs == null) overlay.perFnArgRefs = new List<CssValue[]>(shape.Count);
            else overlay.perFnArgRefs.Clear();
            for (int i = 0; i < shape.Count; i++) {
                var fn = shape[i];
                var argList = new List<CssValue>(fn.ArgKinds.Count);
                var refs = new CssValue[fn.ArgKinds.Count];
                for (int j = 0; j < fn.ArgKinds.Count; j++) {
                    CssValue arg;
                    switch (fn.ArgKinds[j]) {
                        case TransformArgKind.AngleDeg:
                            arg = new CssAngle(fn.ParsedNumeric[j], CssAngleUnit.Deg, null);
                            break;
                        case TransformArgKind.Length:
                            arg = new CssLength(fn.ParsedNumeric[j], fn.ParsedLengthUnit[j], null);
                            break;
                        case TransformArgKind.Number:
                            arg = new CssNumber(fn.ParsedNumeric[j], null);
                            break;
                        default:
                            arg = new CssNumber(0, "0");
                            break;
                    }
                    argList.Add(arg);
                    refs[j] = arg;
                }
                var call = new CssFunctionCall(fn.Name, argList, null);
                overlay.backingArgs.Add(call);
                overlay.perFnArgs.Add(argList);
                overlay.perFnArgRefs.Add(refs);
            }
            overlay.List = new CssValueList(overlay.backingArgs, CssValueListSeparator.Space, null);
        }

        static List<TransformFn> ParseTransformFunctionsCached(string text) {
            if (text == null) return null;
            if (transformFnCache.TryGetValue(text, out var cached)) return cached;
            var parsed = ParseTransformFunctions(text);
            if (parsed != null) {
                // Full-clear eviction when at cap. The previous drop-ONE
                // policy ("first enumerated key") THRASHED at capacity: a
                // burst of per-frame-unique interpolated strings (e.g.
                // "rotate(0deg)" … "rotate(255deg)" from a spin) fills the
                // dictionary once and stays resident in the early hash
                // slots, after which every STABLE keyframe string inserted
                // evicts another stable key — particles.html measured
                // 270-340 µs PER TryUpdateTransformTyped call (two full
                // re-parses + a typed-overlay graph rebuild, every tick of
                // all 420 animations ≈ 60 ms/frame) because its ~20 hot
                // keyframe strings could never stay cached. Clearing
                // wholesale (same pattern as TextRunSnapshotCache's
                // soft-cap) lets the CURRENT working set re-insert within
                // one frame and stay; one-shot garbage only forces a clear
                // once per ~256 distinct strings, amortized noise.
                if (transformFnCache.Count >= TransformFnCacheCap) {
                    transformFnCache.Clear();
                }
                transformFnCache[text] = parsed;
            }
            return parsed;
        }

        static string InterpolateTransform(string from, string to, double t, LengthContext ctx) {
            // RC4: the transform parse cache, identity-list cache, and the
            // reusable `transformOutSb` are single-threaded by Unity main-
            // thread convention. A re-entrant call would scramble both
            // outputs into one string. Fires a debug-build assertion if a
            // callback ever re-routes interpolation off the main thread.
            Weva.Diagnostics.UIMainThreadGuard.AssertMainThread(nameof(InterpolateTransform));
            var fl = ParseTransformFunctionsCached(from);
            var tl = ParseTransformFunctionsCached(to);
            if (fl == null || tl == null) {
                return t < 0.5 ? from : to;
            }
            // CSS Transforms L1 §11/§17: when one side is `none` and the
            // other is a function list, the missing side interpolates as
            // the identity matching the present side's function shape.
            // Exception: if the other list contains `matrix()` we cannot
            // build a per-component identity (matrix args have no per-slot
            // neutral value the way scale/rotate/translate do); the spec's
            // own matrix-decomposition path also degenerates to discrete
            // when the start side is `none` because there is no second
            // matrix to decompose. We therefore fall through to the
            // discrete fallback below — see G9 test
            // `Transform_none_to_matrix_steps_discretely_G9`.
            if (fl.Count == 0 && tl.Count > 0 && !ContainsMatrixFn(tl)) fl = MakeIdentityListCached(tl);
            else if (tl.Count == 0 && fl.Count > 0 && !ContainsMatrixFn(fl)) tl = MakeIdentityListCached(fl);

            // `none ↔ matrix(...)` (or empty list ↔ matrix list) keeps the
            // discrete-step semantics: matrix() has no per-function identity
            // to lerp against, and CSS Transforms L1 §17 carves this case
            // out from the decomposition path. The identity-expansion guard
            // above already left fl/tl asymmetric in this case; treat it
            // explicitly here so the mismatched-shape branch doesn't try to
            // decompose `none` into an implicit identity matrix.
            if ((fl.Count == 0 && ContainsMatrixFn(tl)) || (tl.Count == 0 && ContainsMatrixFn(fl))) {
                return t < 0.5 ? from : to;
            }

            // Shape-match fast path: same length, same per-fn name + arg
            // count → per-component interpolation (the existing route).
            // Anything else routes through 2D matrix decomposition per
            // CSS Transforms L1 §17 (G9). The decomposition path is the
            // ONLY way `translate(10px, 0) → rotate(45deg)` produces a
            // visually-continuous tween instead of a t<0.5 discrete step.
            if (!ShapesMatch(fl, tl)) {
                return InterpolateTransformViaMatrixDecomposition(from, to, fl, tl, t, ctx);
            }
            // Reuse the pooled StringBuilder rather than allocating one per
            // call. ToString() at the end materialises the final result;
            // the builder slot is cleared on the next entry.
            transformOutSb.Clear();
            for (int i = 0; i < fl.Count; i++) {
                if (i > 0) transformOutSb.Append(' ');
                transformOutSb.Append(fl[i].Name);
                transformOutSb.Append('(');
                var flFn = fl[i];
                var tlFn = tl[i];
                for (int j = 0; j < flFn.Args.Count; j++) {
                    if (j > 0) transformOutSb.Append(", ");
                    // Fast path: when both endpoints classify as the same
                    // numeric kind (Length / AngleDeg / Number) and the
                    // length units match, interpolate directly from the
                    // pre-cached numerics — zero parser hits on this Tick.
                    InterpolateTransformArgInto(flFn, tlFn, j, t, ctx, transformOutSb);
                }
                transformOutSb.Append(')');
            }
            return transformOutSb.ToString();
        }

        // --- G9: 2D matrix-decomposition fallback for mismatched shapes ---
        //
        // CSS Transforms L1 §17 specifies that two transform lists with
        // different shapes (different function counts, names, or arg counts)
        // collapse each to a single matrix, decompose both to
        // (translate, scale, rotate, skew), interpolate components linearly,
        // and recompose. Today the engine only registers 2D transform
        // properties (3D `perspective`/`transform-style`/`backface-visibility`
        // are still deferred — see G8b/H6); a 2D-only decomposition therefore
        // covers every shape the runtime actually encounters.
        //
        // The non-skew 2D decomposition for an affine matrix
        //   [[a, b, e],
        //    [c, d, f]]
        // is:
        //   translate = (e, f)
        //   scaleX = sqrt(a^2 + c^2)
        //   scaleY = sqrt(b^2 + d^2)
        //   rotation = atan2(c/scaleX, a/scaleX)   (first column post-normalize)
        //   reflection: if (a*d - b*c) < 0 flip sign of scaleY
        // Skew is NOT extracted in v1 — the spec's full decomposition would
        // factor a horizontal skew between the scale and rotation steps;
        // omitting it means skewed start/end matrices interpolate slightly
        // off-spec but no current transform property registration produces
        // such inputs (skew(...) when present on both sides would shape-match
        // and hit the per-component path instead).

        static bool ContainsMatrixFn(List<TransformFn> list) {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++) {
                var n = list[i]?.Name;
                if (n == null) continue;
                if (n.Equals("matrix", StringComparison.OrdinalIgnoreCase)) return true;
                if (n.Equals("matrix3d", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static bool ShapesMatch(List<TransformFn> fl, List<TransformFn> tl) {
            if (fl.Count != tl.Count) return false;
            for (int i = 0; i < fl.Count; i++) {
                if (!string.Equals(fl[i].Name, tl[i].Name, StringComparison.OrdinalIgnoreCase)) return false;
                if (fl[i].Args.Count != tl[i].Args.Count) return false;
            }
            return true;
        }

        // 2D affine matrix [[a, b, e], [c, d, f]] stored row-major as
        // (a, b, c, d, e, f). e/f are the translate column.
        struct Mat2D {
            public double A, B, C, D, E, F;
            public static Mat2D Identity => new Mat2D { A = 1, B = 0, C = 0, D = 1, E = 0, F = 0 };
            public static Mat2D Multiply(Mat2D x, Mat2D y) {
                // x * y, applied as "first y, then x" in the usual column-vector
                // convention: result transforms a point by y, then by x.
                return new Mat2D {
                    A = x.A * y.A + x.B * y.C,
                    B = x.A * y.B + x.B * y.D,
                    C = x.C * y.A + x.D * y.C,
                    D = x.C * y.B + x.D * y.D,
                    E = x.A * y.E + x.B * y.F + x.E,
                    F = x.C * y.E + x.D * y.F + x.F,
                };
            }
        }

        // Decomposed 2D components.
        struct Mat2DDecomposed {
            public double Tx, Ty;
            public double ScaleX, ScaleY;
            public double RotationRad;
        }

        static Mat2DDecomposed Decompose2D(Mat2D m) {
            double a = m.A, b = m.B, c = m.C, d = m.D;
            double scaleX = Math.Sqrt(a * a + c * c);
            // First column normalized; if scaleX == 0 the matrix is degenerate
            // (collapsed inline axis). Fall back to identity rotation in that
            // case — recomposition will still emit a valid (zero-scale) matrix.
            if (scaleX > 0) { a /= scaleX; c /= scaleX; }
            else { a = 1; c = 0; }
            double scaleY = Math.Sqrt(b * b + d * d);
            if (scaleY > 0) { b /= scaleY; d /= scaleY; }
            // Reflection: original determinant negative ⇒ flip one scale to
            // keep the rotation in the canonical range.
            double det = m.A * m.D - m.B * m.C;
            if (det < 0) scaleY = -scaleY;
            double rotation = Math.Atan2(c, a);
            return new Mat2DDecomposed {
                Tx = m.E, Ty = m.F,
                ScaleX = scaleX, ScaleY = scaleY,
                RotationRad = rotation,
            };
        }

        static Mat2D Recompose2D(Mat2DDecomposed d) {
            double cos = Math.Cos(d.RotationRad);
            double sin = Math.Sin(d.RotationRad);
            // translate * rotate * scale
            return new Mat2D {
                A = cos * d.ScaleX,
                B = -sin * d.ScaleY,
                C = sin * d.ScaleX,
                D = cos * d.ScaleY,
                E = d.Tx,
                F = d.Ty,
            };
        }

        // Lerp two decompositions. Rotation lerps by the shortest arc so
        // 0° → 350° doesn't spin the long way (matches Chrome's behaviour
        // for the simple-list mismatched-shape path).
        static Mat2DDecomposed LerpDecomposed(Mat2DDecomposed a, Mat2DDecomposed b, double t) {
            double dr = b.RotationRad - a.RotationRad;
            // Normalize into (-PI, PI] for shortest-arc.
            while (dr > Math.PI) dr -= 2 * Math.PI;
            while (dr < -Math.PI) dr += 2 * Math.PI;
            return new Mat2DDecomposed {
                Tx = a.Tx + (b.Tx - a.Tx) * t,
                Ty = a.Ty + (b.Ty - a.Ty) * t,
                ScaleX = a.ScaleX + (b.ScaleX - a.ScaleX) * t,
                ScaleY = a.ScaleY + (b.ScaleY - a.ScaleY) * t,
                RotationRad = a.RotationRad + dr * t,
            };
        }

        // Collapse a parsed transform-function list into a single 2D matrix.
        // Returns false when the list contains a 3D function (matrix3d /
        // translate3d / rotate3d / scale3d / rotateX/Y / perspective etc.)
        // — those can't lossless-collapse into Mat2D. Caller falls back to
        // discrete in that case.
        static bool TryCollapseToMat2D(List<TransformFn> list, LengthContext ctx, out Mat2D m) {
            m = Mat2D.Identity;
            if (list == null) return true; // empty / null ⇒ identity
            for (int i = 0; i < list.Count; i++) {
                var fn = list[i];
                if (fn == null || fn.Name == null) return false;
                if (!TryFnToMat2D(fn, ctx, out var fm)) return false;
                m = Mat2D.Multiply(m, fm);
            }
            return true;
        }

        static bool TryFnToMat2D(TransformFn fn, LengthContext ctx, out Mat2D m) {
            m = Mat2D.Identity;
            string name = fn.Name;
            if (name == null) return false;
            // 3D transform functions can't lossless-collapse to Mat2D.
            if (name.IndexOf("3d", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (name.Equals("perspective", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Equals("rotateX", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Equals("rotateY", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Equals("translateZ", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.Equals("scaleZ", StringComparison.OrdinalIgnoreCase)) return false;

            if (name.Equals("matrix", StringComparison.OrdinalIgnoreCase)) {
                if (fn.Args.Count != 6) return false;
                if (!TryReadNumberArg(fn, 0, out double a)) return false;
                if (!TryReadNumberArg(fn, 1, out double b)) return false;
                if (!TryReadNumberArg(fn, 2, out double c)) return false;
                if (!TryReadNumberArg(fn, 3, out double d)) return false;
                if (!TryReadLengthArgPx(fn, 4, ctx, out double e)) return false;
                if (!TryReadLengthArgPx(fn, 5, ctx, out double f)) return false;
                m = new Mat2D { A = a, B = b, C = c, D = d, E = e, F = f };
                return true;
            }
            if (name.Equals("translate", StringComparison.OrdinalIgnoreCase)) {
                if (fn.Args.Count < 1 || fn.Args.Count > 2) return false;
                if (!TryReadLengthArgPx(fn, 0, ctx, out double tx)) return false;
                double ty = 0;
                if (fn.Args.Count == 2 && !TryReadLengthArgPx(fn, 1, ctx, out ty)) return false;
                m = new Mat2D { A = 1, B = 0, C = 0, D = 1, E = tx, F = ty };
                return true;
            }
            if (name.Equals("translateX", StringComparison.OrdinalIgnoreCase)) {
                if (!TryReadLengthArgPx(fn, 0, ctx, out double tx)) return false;
                m = new Mat2D { A = 1, B = 0, C = 0, D = 1, E = tx, F = 0 };
                return true;
            }
            if (name.Equals("translateY", StringComparison.OrdinalIgnoreCase)) {
                if (!TryReadLengthArgPx(fn, 0, ctx, out double ty)) return false;
                m = new Mat2D { A = 1, B = 0, C = 0, D = 1, E = 0, F = ty };
                return true;
            }
            if (name.Equals("scale", StringComparison.OrdinalIgnoreCase)) {
                if (fn.Args.Count < 1 || fn.Args.Count > 2) return false;
                if (!TryReadNumberArg(fn, 0, out double sx)) return false;
                double sy = (fn.Args.Count == 2) ? 0 : sx;
                if (fn.Args.Count == 2 && !TryReadNumberArg(fn, 1, out sy)) return false;
                m = new Mat2D { A = sx, B = 0, C = 0, D = sy, E = 0, F = 0 };
                return true;
            }
            if (name.Equals("scaleX", StringComparison.OrdinalIgnoreCase)) {
                if (!TryReadNumberArg(fn, 0, out double sx)) return false;
                m = new Mat2D { A = sx, B = 0, C = 0, D = 1, E = 0, F = 0 };
                return true;
            }
            if (name.Equals("scaleY", StringComparison.OrdinalIgnoreCase)) {
                if (!TryReadNumberArg(fn, 0, out double sy)) return false;
                m = new Mat2D { A = 1, B = 0, C = 0, D = sy, E = 0, F = 0 };
                return true;
            }
            if (name.Equals("rotate", StringComparison.OrdinalIgnoreCase)
                || name.Equals("rotateZ", StringComparison.OrdinalIgnoreCase)) {
                if (!TryReadAngleRad(fn, 0, out double rad)) return false;
                double cs = Math.Cos(rad);
                double sn = Math.Sin(rad);
                m = new Mat2D { A = cs, B = -sn, C = sn, D = cs, E = 0, F = 0 };
                return true;
            }
            if (name.Equals("skew", StringComparison.OrdinalIgnoreCase)) {
                if (!TryReadAngleRad(fn, 0, out double ax)) return false;
                double ay = 0;
                if (fn.Args.Count >= 2 && !TryReadAngleRad(fn, 1, out ay)) return false;
                m = new Mat2D { A = 1, B = Math.Tan(ax), C = Math.Tan(ay), D = 1, E = 0, F = 0 };
                return true;
            }
            if (name.Equals("skewX", StringComparison.OrdinalIgnoreCase)) {
                if (!TryReadAngleRad(fn, 0, out double ax)) return false;
                m = new Mat2D { A = 1, B = Math.Tan(ax), C = 0, D = 1, E = 0, F = 0 };
                return true;
            }
            if (name.Equals("skewY", StringComparison.OrdinalIgnoreCase)) {
                if (!TryReadAngleRad(fn, 0, out double ay)) return false;
                m = new Mat2D { A = 1, B = 0, C = Math.Tan(ay), D = 1, E = 0, F = 0 };
                return true;
            }
            return false;
        }

        static bool TryReadNumberArg(TransformFn fn, int j, out double value) {
            if (j < fn.ArgKinds.Count) {
                var k = fn.ArgKinds[j];
                if (k == TransformArgKind.Number) { value = fn.ParsedNumeric[j]; return true; }
                // tolerate 0 length etc.
                if (k == TransformArgKind.Length && fn.ParsedNumeric[j] == 0) { value = 0; return true; }
            }
            value = 0;
            return TryParseNumber(fn.Args[j], out value);
        }

        static bool TryReadLengthArgPx(TransformFn fn, int j, LengthContext ctx, out double px) {
            px = 0;
            if (j < fn.ArgKinds.Count) {
                var k = fn.ArgKinds[j];
                if (k == TransformArgKind.Length) {
                    var unit = fn.ParsedLengthUnit[j];
                    if (unit == CssLengthUnit.Px) { px = fn.ParsedNumeric[j]; return true; }
                    try {
                        var len = new CssLength(fn.ParsedNumeric[j], unit, null);
                        px = len.ToPixels(ctx);
                        return true;
                    } catch { return false; }
                }
                if (k == TransformArgKind.Number) { px = fn.ParsedNumeric[j]; return true; }
            }
            // Fall back to the string path (mixed-unit slot or unclassified).
            var l = TryParseLength(fn.Args[j]);
            if (l != null) {
                try { px = l.ToPixels(ctx); return true; } catch { return false; }
            }
            return TryParseNumber(fn.Args[j], out px);
        }

        static bool TryReadAngleRad(TransformFn fn, int j, out double rad) {
            rad = 0;
            if (j < fn.ArgKinds.Count && fn.ArgKinds[j] == TransformArgKind.AngleDeg) {
                rad = fn.ParsedNumeric[j] * (Math.PI / 180.0);
                return true;
            }
            // Fallback string parse — also handles bare-zero unitless rotation
            // arguments which classify as Number but are angle-meaningful.
            rad = ParseAngleDeg(fn.Args[j]) * (Math.PI / 180.0);
            return true;
        }

        static string InterpolateTransformViaMatrixDecomposition(
                string fromRaw, string toRaw,
                List<TransformFn> fl, List<TransformFn> tl,
                double t, LengthContext ctx) {
            if (!TryCollapseToMat2D(fl, ctx, out var mA) || !TryCollapseToMat2D(tl, ctx, out var mB)) {
                // Either side contains 3D / unsupported function — fall back
                // to discrete per CSS Transforms L1 §17.
                return t < 0.5 ? fromRaw : toRaw;
            }
            var da = Decompose2D(mA);
            var db = Decompose2D(mB);
            var lerped = LerpDecomposed(da, db, t);
            var m = Recompose2D(lerped);
            return FormatMatrix(m);
        }

        static string FormatMatrix(Mat2D m) {
            // CSS Transforms L1 §6: `matrix(a, b, c, d, e, f)` serialization.
            // Translate components carry a `px` unit when serialized via
            // computed-style readback, but the matrix() function takes
            // unitless lengths (interpreted as pixels). Emit unitless.
            var sb = new StringBuilder(64);
            sb.Append("matrix(");
            sb.Append(Format(m.A)); sb.Append(", ");
            sb.Append(Format(m.B)); sb.Append(", ");
            sb.Append(Format(m.C)); sb.Append(", ");
            sb.Append(Format(m.D)); sb.Append(", ");
            sb.Append(Format(m.E)); sb.Append(", ");
            sb.Append(Format(m.F));
            sb.Append(')');
            return sb.ToString();
        }

        static void InterpolateTransformArgInto(TransformFn from, TransformFn to, int j,
                                                double t, LengthContext ctx, StringBuilder sb) {
            if (j < from.ArgKinds.Count && j < to.ArgKinds.Count) {
                var fk = from.ArgKinds[j];
                var tk = to.ArgKinds[j];
                if (fk == TransformArgKind.AngleDeg && tk == TransformArgKind.AngleDeg) {
                    double da = from.ParsedNumeric[j];
                    double db = to.ParsedNumeric[j];
                    sb.Append(Format(da + (db - da) * t));
                    sb.Append("deg");
                    return;
                }
                if (fk == TransformArgKind.Length && tk == TransformArgKind.Length) {
                    var ua = from.ParsedLengthUnit[j];
                    var ub = to.ParsedLengthUnit[j];
                    if (ua == ub) {
                        double va = from.ParsedNumeric[j];
                        double vb = to.ParsedNumeric[j];
                        sb.Append(Format(va + (vb - va) * t));
                        sb.Append(UnitSuffix(ua));
                        return;
                    }
                }
                if (fk == TransformArgKind.Number && tk == TransformArgKind.Number) {
                    double va = from.ParsedNumeric[j];
                    double vb = to.ParsedNumeric[j];
                    sb.Append(Format(va + (vb - va) * t));
                    return;
                }
            }
            // Fallback: legacy string-driven path covers mixed-unit lengths
            // (needs LengthContext resolution) and any classify-as-None args.
            sb.Append(InterpolateTransformArg(from.Args[j], to.Args[j], t, ctx));
        }

        static string InterpolateTransformArg(string a, string b, double t, LengthContext ctx) {
            string aa = a.Trim();
            string bb = b.Trim();
            if (aa.EndsWith("deg", StringComparison.OrdinalIgnoreCase) ||
                bb.EndsWith("deg", StringComparison.OrdinalIgnoreCase)) {
                double da = ParseAngleDeg(aa);
                double db = ParseAngleDeg(bb);
                return Format(da + (db - da) * t) + "deg";
            }
            if (TryParseLength(aa) is CssLength la && TryParseLength(bb) is CssLength lb) {
                if (la.Unit == lb.Unit) {
                    double v = la.Value + (lb.Value - la.Value) * t;
                    return Format(v) + UnitSuffix(la.Unit);
                }
                try {
                    double pa = la.ToPixels(ctx);
                    double pb = lb.ToPixels(ctx);
                    double v = pa + (pb - pa) * t;
                    return Format(v) + "px";
                } catch (InvalidOperationException ex) {
                    // EC1 — typically a `%` length with no LengthContext.BasisPixels
                    // set. Warn-once per offending unit-pair so an author who
                    // animates `translateX(50%)` -> `translateX(100px)` without a
                    // resolvable basis sees the failure surface; bare-number
                    // fallback below preserves prior behaviour.
                    WarnTransformLengthConversionFailure(la.Unit, lb.Unit, ex);
                }
            }
            if (TryParseNumber(aa, out double na) && TryParseNumber(bb, out double nb)) {
                double v = na + (nb - na) * t;
                return Format(v);
            }
            return t < 0.5 ? aa : bb;
        }

        static double ParseAngleDeg(string s) {
            // Allocation-free: the previous `s.ToLowerInvariant().Trim()`
            // chain copied the string twice on every interpolation step
            // (60Hz × N animated transforms). Use OrdinalIgnoreCase suffix
            // comparisons directly on the trimmed input; AsSpan slicing
            // for the number portion avoids the per-suffix Substring alloc.
            //
            // DD1 — every fall-through case returns 0 (animators tolerate
            // "no rotation"), but routes through UICssDiagnostics so an
            // author who typos a `rotate(NaNdeg)` keyframe sees the parse
            // failure rather than silently picking up a 0° default. Warn is
            // deduped per offending token by UICssDiagnostics's (source,
            // detail) key, so a 60Hz tick on a single bad keyframe logs
            // exactly once per session.
            if (s == null) { WarnParseAngleDegFailure("<null>"); return 0; }
            string t = s.Trim();
            if (t.EndsWith("deg", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(t.AsSpan(0, t.Length - 3), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
            if (t.EndsWith("rad", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(t.AsSpan(0, t.Length - 3), NumberStyles.Float, CultureInfo.InvariantCulture, out double r)) return r * (180.0 / Math.PI);
            if (t.EndsWith("turn", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(t.AsSpan(0, t.Length - 4), NumberStyles.Float, CultureInfo.InvariantCulture, out double tu)) return tu * 360;
            if (t.EndsWith("grad", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(t.AsSpan(0, t.Length - 4), NumberStyles.Float, CultureInfo.InvariantCulture, out double g)) return g * 0.9;
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return n;
            WarnParseAngleDegFailure(t);
            return 0;
        }

        // DD1 — emit a deduped diagnostic when ParseAngleDeg's defensive
        // 0-fallback fires. Behaviour (return 0) is preserved; the warn
        // surfaces the malformed keyframe to authors. UICssDiagnostics
        // dedupes on the full (source, detail) pair, so passing the
        // offending token as the detail gives us per-token dedupe.
        static void WarnParseAngleDegFailure(string token) {
            UICssDiagnostics.Warn("animation", "ParseAngleDeg failed for: " + token);
        }

        sealed class TransformFn {
            public string Name;
            public List<string> Args = new List<string>();
            // Pre-resolved per-arg numeric forms, indexed parallel to Args.
            // Populated in ParseTransformFunctions immediately after the
            // raw arg string is captured, so InterpolateTransformArg can
            // skip the CssValueParser round-trip on every Tick (the inner
            // hot path that allocated ~1.7 KB per call before this cache).
            // ArgKinds[i] tells the consumer which slot is meaningful:
            //   Length   -> ParsedLengthValue[i] + ParsedLengthUnit[i] valid
            //   AngleDeg -> ParsedNumeric[i] holds degrees
            //   Number   -> ParsedNumeric[i] holds the bare value
            //   None     -> none of the above; fall back to a < 0.5 ? a : b
            public List<TransformArgKind> ArgKinds = new List<TransformArgKind>();
            public List<double> ParsedNumeric = new List<double>();
            public List<CssLengthUnit> ParsedLengthUnit = new List<CssLengthUnit>();
        }
        enum TransformArgKind : byte { None, Length, AngleDeg, Number }

        // Builds a parallel "identity" function list matching `source`'s
        // shape — same function names + arg counts, but each arg replaced
        // with the function's neutral value (rotate→0deg, scale→1,
        // translate→0px, skew→0deg). Used when interpolating `none ↔ list`
        // so the rest of InterpolateTransform's per-arg lerp logic just
        // works without a special case.
        // Cache MakeIdentityList output keyed on the source list reference.
        // The source list itself comes from the cached
        // ParseTransformFunctionsCached path, so the same source instance
        // is handed in across every Tick — identity dedupes perfectly.
        // Fires whenever an animation has implicit `none` on one keyframe
        // and an explicit transform on the other (the gem-spin canonical
        // case — `from: none -> to: rotate(360deg)`). Without this cache
        // the identity-list build allocated ~360 B per Tick per such tile.
        // Identity-based equality. System.Collections.Generic.ReferenceEqualityComparer
        // doesn't exist on Unity's .NET Standard 2.1 surface, so we provide our own.
        sealed class RefEq<T> : IEqualityComparer<T> where T : class {
            public static readonly RefEq<T> Instance = new RefEq<T>();
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
        static readonly Dictionary<List<TransformFn>, List<TransformFn>> identityListCache =
            new Dictionary<List<TransformFn>, List<TransformFn>>(RefEq<List<TransformFn>>.Instance);
        const int IdentityListCacheCap = 64;
        static List<TransformFn> MakeIdentityListCached(List<TransformFn> source) {
            if (identityListCache.TryGetValue(source, out var cached)) return cached;
            var built = MakeIdentityList(source);
            // Same full-clear-at-cap eviction as transformFnCache (see the
            // thrash note there): drop-ONE eviction keeps stale early-slot
            // entries resident and rotates out the live working set instead.
            // A wholesale clear lets the current shapes re-populate within a
            // frame; the per-build cost is small (a handful of TransformFn +
            // IdentityArgFor lookups) so the occasional refill is noise.
            if (identityListCache.Count >= IdentityListCacheCap) {
                identityListCache.Clear();
            }
            identityListCache[source] = built;
            return built;
        }

        // Test-only helpers — mirror the BackgroundResolver MC1 fix pattern
        // (Packages/com.wevaui/Runtime/Paint/Conversion/BackgroundResolver.cs).
        // The caches are process-static, so without an explicit reset NUnit
        // cases would leak state across runs and the MS3 cap/eviction
        // assertions would depend on test ordering.
        internal static void ResetCaches_TestOnly() {
            transformFnCache.Clear();
            identityListCache.Clear();
        }

        // Occupancy windows for the MS3 regression suite — the cap values
        // are otherwise implementation-detail and shouldn't leak into
        // production callers.
        internal static int TransformFnCacheCount_TestOnly() => transformFnCache.Count;
        internal static int TransformFnCacheCap_TestOnly => TransformFnCacheCap;
        internal static int IdentityListCacheCount_TestOnly() => identityListCache.Count;
        internal static int IdentityListCacheCap_TestOnly => IdentityListCacheCap;

        static List<TransformFn> MakeIdentityList(List<TransformFn> source) {
            var result = new List<TransformFn>(source.Count);
            foreach (var fn in source) {
                var id = new TransformFn { Name = fn.Name };
                string identity = IdentityArgFor(fn.Name);
                for (int i = 0; i < fn.Args.Count; i++) id.Args.Add(identity);
                result.Add(id);
            }
            return result;
        }

        static string IdentityArgFor(string fnName) {
            if (fnName == null) return "0";
            // OrdinalIgnoreCase prefix matching is allocation-free vs the
            // prior ToLowerInvariant + StartsWith. Called once per arg on
            // each MakeIdentityList build — fires during every transform
            // animation startup.
            if (fnName.StartsWith("scale", StringComparison.OrdinalIgnoreCase)) return "1";
            if (fnName.StartsWith("translate", StringComparison.OrdinalIgnoreCase)) return "0px";
            if (fnName.StartsWith("rotate", StringComparison.OrdinalIgnoreCase)
                || fnName.StartsWith("skew", StringComparison.OrdinalIgnoreCase)) return "0deg";
            // Generic numeric identity for matrix entries / future kinds.
            return "0";
        }

        static List<TransformFn> ParseTransformFunctions(string text) {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string s = text.Trim();
            if (s.Equals("none", StringComparison.OrdinalIgnoreCase)) return new List<TransformFn>();
            var list = new List<TransformFn>();
            int i = 0;
            while (i < s.Length) {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i >= s.Length) break;
                int nameStart = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '-' || s[i] == '_')) i++;
                if (nameStart == i) return null;
                string name = s.Substring(nameStart, i - nameStart);
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i >= s.Length || s[i] != '(') return null;
                i++;
                int argStart = i;
                int depth = 1;
                while (i < s.Length && depth > 0) {
                    if (s[i] == '(') depth++;
                    else if (s[i] == ')') { depth--; if (depth == 0) break; }
                    i++;
                }
                if (i >= s.Length) return null;
                string argsText = s.Substring(argStart, i - argStart);
                i++;
                var fn = new TransformFn { Name = name };
                foreach (var arg in argsText.Split(',')) {
                    string trimmed = arg.Trim();
                    fn.Args.Add(trimmed);
                    ClassifyTransformArg(trimmed, fn);
                }
                list.Add(fn);
            }
            return list;
        }

        // Resolves an arg string into one of (Length, AngleDeg, Number, None)
        // and stashes the parsed numeric on the TransformFn so per-Tick
        // interpolation reads the slot without re-parsing. Called once per
        // arg at ParseTransformFunctions time; the cached TransformFn list
        // (transformFnCache) reuses these slots across every subsequent
        // Tick on the same keyframe text.
        static void ClassifyTransformArg(string arg, TransformFn fn) {
            if (string.IsNullOrEmpty(arg)) {
                fn.ArgKinds.Add(TransformArgKind.None);
                fn.ParsedNumeric.Add(0);
                fn.ParsedLengthUnit.Add(CssLengthUnit.Px);
                return;
            }
            // Angle: matches deg / rad / grad / turn. ParseAngleDeg already
            // tolerates the unit suffix without allocating beyond the input
            // string itself.
            if (arg.EndsWith("deg", StringComparison.OrdinalIgnoreCase)
                || arg.EndsWith("rad", StringComparison.OrdinalIgnoreCase)
                || arg.EndsWith("turn", StringComparison.OrdinalIgnoreCase)
                || arg.EndsWith("grad", StringComparison.OrdinalIgnoreCase)) {
                fn.ArgKinds.Add(TransformArgKind.AngleDeg);
                fn.ParsedNumeric.Add(ParseAngleDeg(arg));
                fn.ParsedLengthUnit.Add(CssLengthUnit.Px);
                return;
            }
            var len = TryParseLength(arg);
            if (len != null) {
                fn.ArgKinds.Add(TransformArgKind.Length);
                fn.ParsedNumeric.Add(len.Value);
                fn.ParsedLengthUnit.Add(len.Unit);
                return;
            }
            if (TryParseNumber(arg, out double num)) {
                fn.ArgKinds.Add(TransformArgKind.Number);
                fn.ParsedNumeric.Add(num);
                fn.ParsedLengthUnit.Add(CssLengthUnit.Px);
                return;
            }
            fn.ArgKinds.Add(TransformArgKind.None);
            fn.ParsedNumeric.Add(0);
            fn.ParsedLengthUnit.Add(CssLengthUnit.Px);
        }

        // D1: routes through the canonical CssColor.LinearToSrgb piecewise
        // encode so the (0.0031308 / 12.92 / 1.055 / 1/2.4 / 0.055) constants
        // live in one place. Float input is promoted to double for the encode
        // (CssColor's signature) and the result is rounded + clamped to byte
        // for the caller's animator byte-buffer ergonomics. CssColor already
        // clamps v at [0, 1] and handles NaN, so the explicit early-outs are
        // gone — Math.Round of {0, *, 1} * 255 lands in [0, 255] before clamp.
        static byte LinearToSrgbByte(float l) {
            double v = CssColor.LinearToSrgb(l) * 255.0;
            int b = (int)Math.Round(v);
            if (b < 0) b = 0; if (b > 255) b = 255;
            return (byte)b;
        }

        static bool TryParseNumber(string text, out double value) {
            value = 0;
            if (string.IsNullOrEmpty(text)) return false;
            return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        static CssLength TryParseLength(string text) {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try {
                var v = CssValueParser.Parse(text);
                if (v is CssLength l) return l;
                if (v is CssPercentage p) return new CssLength(p.Value, CssLengthUnit.Percent, p.Raw);
                if (v is CssNumber n && n.Value == 0) return new CssLength(0, CssLengthUnit.Px);
            } catch (CssValueParseException) {
                // EC2: tightened from bare `catch { }` to the documented parser
                // failure type. By-design fallback (callers treat null as
                // "discrete-step"). NullReferenceException / out-of-memory now
                // propagate instead of being silently swallowed.
            }
            return null;
        }

        static string Format(double v) {
            if (Math.Abs(v) < 1e-9) return "0";
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }

        static string Format(float v) => Format((double)v);

        // --- G8b: CSS Transforms L2 §13 — per-component interpolation for
        // the individual transform properties (`translate`, `rotate`, `scale`).
        //
        // Per spec each axis lerps independently from its endpoint's matching
        // axis. Missing components fill from the property's identity:
        //   translate: <length-percentage>{1,3}  — missing tx/ty/tz = 0
        //   scale:     <number>{1,3}             — missing sx/sy/sz = 1
        //                                          (one-arg `scale: 2` ⇒ `2 2`)
        //   rotate:    [ x|y|z|<number>{3} ]? <angle>
        //              Weva's paint pipeline is 2D-only, so we lerp the
        //              angle and preserve the from-side axis identifier
        //              (the to-side must match for component-wise interp;
        //              a mismatched axis falls back to discrete).
        //
        // The 3D Z component on translate/scale is registered but never
        // painted (G8b paint-side is a deliberate no-op — Weva has no
        // perspective shader). The interpolator still round-trips a 3-axis
        // value so an author animating between `0 0 0` and `10px 20px 30px`
        // gets the spec-faithful intermediate string.

        // Top-level space splitter, local copy of the RawValueParser helper.
        // Returns a non-cached fresh list per call — these strings are short
        // (≤4 tokens) and the runtime-cached upstream entry caches the parse
        // result on the keyframe, so the per-Tick re-split is negligible.
        static List<string> SplitTopLevelSpacesLocal(string s) {
            var list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;
            int depth = 0;
            int start = 0;
            int i = 0;
            // Skip leading whitespace.
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            start = i;
            for (; i < s.Length; i++) {
                char c = s[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (char.IsWhiteSpace(c) && depth == 0) {
                    if (i > start) list.Add(s.Substring(start, i - start));
                    while (i + 1 < s.Length && char.IsWhiteSpace(s[i + 1])) i++;
                    start = i + 1;
                }
            }
            if (start < s.Length) list.Add(s.Substring(start).Trim());
            return list;
        }

        // Lerp a single length-or-percentage token. Same-unit pairs preserve
        // the unit; mixed units resolve to pixels via LengthContext. Returns
        // null on unparseable input — caller falls back to discrete.
        static string LerpLengthToken(string a, string b, double t, LengthContext ctx) {
            var la = TryParseLength(a);
            var lb = TryParseLength(b);
            if (la != null && lb != null) {
                if (la.Unit == lb.Unit) {
                    double v = la.Value + (lb.Value - la.Value) * t;
                    return Format(v) + UnitSuffix(la.Unit);
                }
                try {
                    double pa = la.ToPixels(ctx);
                    double pb = lb.ToPixels(ctx);
                    double v = pa + (pb - pa) * t;
                    return Format(v) + "px";
                } catch (InvalidOperationException) {
                    return null;
                }
            }
            // Bare-zero number behaves as 0px (handled by TryParseLength
            // returning a 0px CssLength for `0`).
            return null;
        }

        // `translate: <length-percentage>{1,3}` — missing components default
        // to `0` (CSS Transforms L2 §3.2).
        static string InterpolateIndividualTranslate(string fromRaw, string toRaw, double t, LengthContext ctx) {
            if (string.IsNullOrWhiteSpace(fromRaw)) fromRaw = "none";
            if (string.IsNullOrWhiteSpace(toRaw)) toRaw = "none";
            string fTrim = fromRaw.Trim();
            string tTrim = toRaw.Trim();
            bool fromNone = string.Equals(fTrim, "none", StringComparison.OrdinalIgnoreCase);
            bool toNone = string.Equals(tTrim, "none", StringComparison.OrdinalIgnoreCase);
            // CSS Transforms L2 §3.5: `none` interpolates as the identity
            // value (translate identity = `0 0 0` ≡ `0`). Treat as a one-arg
            // `0` so the per-axis path handles arg-count alignment uniformly.
            var fParts = fromNone ? new List<string> { "0" } : SplitTopLevelSpacesLocal(fTrim);
            var tParts = toNone ? new List<string> { "0" } : SplitTopLevelSpacesLocal(tTrim);
            if (fParts.Count == 0 || tParts.Count == 0) return t < 0.5 ? fromRaw : toRaw;
            if (fParts.Count > 3 || tParts.Count > 3) return t < 0.5 ? fromRaw : toRaw;

            int n = Math.Max(fParts.Count, tParts.Count);
            var outParts = new string[n];
            for (int i = 0; i < n; i++) {
                string a = i < fParts.Count ? fParts[i] : "0";
                string b = i < tParts.Count ? tParts[i] : "0";
                string lerped = LerpLengthToken(a, b, t, ctx);
                if (lerped == null) return t < 0.5 ? fromRaw : toRaw;
                outParts[i] = lerped;
            }
            return string.Join(" ", outParts);
        }

        // `scale: <number>{1,3}` — `scale: 2` is shorthand for `scale: 2 2`
        // (2D) / `scale: 2 2 1` if a Z component is present elsewhere. A
        // one-arg value expands to two equal values per CSS Transforms L2
        // §3.4; the third (Z) component defaults to `1`.
        static string InterpolateIndividualScale(string fromRaw, string toRaw, double t) {
            if (string.IsNullOrWhiteSpace(fromRaw)) fromRaw = "none";
            if (string.IsNullOrWhiteSpace(toRaw)) toRaw = "none";
            string fTrim = fromRaw.Trim();
            string tTrim = toRaw.Trim();
            bool fromNone = string.Equals(fTrim, "none", StringComparison.OrdinalIgnoreCase);
            bool toNone = string.Equals(tTrim, "none", StringComparison.OrdinalIgnoreCase);
            var fParts = fromNone ? new List<string> { "1" } : SplitTopLevelSpacesLocal(fTrim);
            var tParts = toNone ? new List<string> { "1" } : SplitTopLevelSpacesLocal(tTrim);
            if (fParts.Count == 0 || tParts.Count == 0) return t < 0.5 ? fromRaw : toRaw;
            if (fParts.Count > 3 || tParts.Count > 3) return t < 0.5 ? fromRaw : toRaw;

            // Spec: `scale: 2` expands to `2 2`. We expand BOTH sides to the
            // longer arg count before pairwise lerp so `1 → 2 3` correctly
            // lerps as `1 1` → `2 3`.
            int n = Math.Max(fParts.Count, tParts.Count);
            // The Z identity (when expanding 2-arg to 3-arg) is 1, NOT 0.
            // Two-axis (the common case) leaves Z untouched.
            double[] f = ExpandScaleParts(fParts, n);
            double[] tg = ExpandScaleParts(tParts, n);
            if (f == null || tg == null) return t < 0.5 ? fromRaw : toRaw;

            var sb = new StringBuilder(16);
            for (int i = 0; i < n; i++) {
                if (i > 0) sb.Append(' ');
                double v = f[i] + (tg[i] - f[i]) * t;
                sb.Append(Format(v));
            }
            return sb.ToString();
        }

        static double[] ExpandScaleParts(List<string> parts, int n) {
            var result = new double[n];
            // First parse what we have.
            double[] raw = new double[parts.Count];
            for (int i = 0; i < parts.Count; i++) {
                if (!TryParseScaleComponent(parts[i], out raw[i])) return null;
            }
            // CSS Transforms L2 §3.4: one-arg `scale: <s>` ≡ `<s> <s>`; the
            // X-axis value duplicates to Y. The Z component defaults to 1
            // when absent.
            if (parts.Count == 1) {
                // sx == sy from the implicit expansion. Z defaults to 1.
                for (int i = 0; i < n; i++) {
                    result[i] = (i < 2) ? raw[0] : 1.0;
                }
            } else {
                for (int i = 0; i < n; i++) {
                    if (i < parts.Count) result[i] = raw[i];
                    else result[i] = 1.0; // missing axis ⇒ identity (1)
                }
            }
            return result;
        }

        static bool TryParseScaleComponent(string raw, out double value) {
            value = 1;
            if (string.IsNullOrEmpty(raw)) return false;
            string s = raw.Trim();
            if (s.EndsWith("%")) {
                if (double.TryParse(s.AsSpan(0, s.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) {
                    value = pct * 0.01;
                    return true;
                }
                return false;
            }
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        // `rotate: <angle> | [ x|y|z|<number>{3} ] && <angle>`. The 2D-only
        // engine lerps the angle component and preserves the from-side axis
        // identifier. Mismatched axis identifiers (`x` vs `z`, or numeric-
        // axis vs keyword-axis) fall back to discrete since the spec defines
        // shortest-arc interpolation only within the same rotation axis.
        static string InterpolateIndividualRotate(string fromRaw, string toRaw, double t, LengthContext ctx) {
            if (string.IsNullOrWhiteSpace(fromRaw)) fromRaw = "none";
            if (string.IsNullOrWhiteSpace(toRaw)) toRaw = "none";
            string fTrim = fromRaw.Trim();
            string tTrim = toRaw.Trim();
            bool fromNone = string.Equals(fTrim, "none", StringComparison.OrdinalIgnoreCase);
            bool toNone = string.Equals(tTrim, "none", StringComparison.OrdinalIgnoreCase);
            // `none` is the identity rotation (0deg, default axis).
            var fParts = fromNone ? new List<string> { "0deg" } : SplitTopLevelSpacesLocal(fTrim);
            var tParts = toNone ? new List<string> { "0deg" } : SplitTopLevelSpacesLocal(tTrim);
            if (fParts.Count == 0 || tParts.Count == 0) return t < 0.5 ? fromRaw : toRaw;

            // Last token is the angle; preceding tokens are the axis spec.
            string fAxis = fParts.Count > 1 ? string.Join(" ", fParts.GetRange(0, fParts.Count - 1)) : null;
            string tAxis = tParts.Count > 1 ? string.Join(" ", tParts.GetRange(0, tParts.Count - 1)) : null;
            // Axis must match for per-component interp; mismatched axes fall
            // back to discrete (the spec's `not interpolable` clause).
            if (!AxisMatches(fAxis, tAxis)) return t < 0.5 ? fromRaw : toRaw;

            string fAngleTok = fParts[fParts.Count - 1];
            string tAngleTok = tParts[tParts.Count - 1];
            double fDeg = ParseAngleDeg(fAngleTok);
            double tDeg = ParseAngleDeg(tAngleTok);
            double v = fDeg + (tDeg - fDeg) * t;
            string angleOut = Format(v) + "deg";
            return fAxis != null ? fAxis + " " + angleOut : angleOut;
        }

        static bool AxisMatches(string a, string b) {
            // Both null ⇒ default axis (z) on both sides.
            if (a == null && b == null) return true;
            if (a == null || b == null) {
                // Default axis is z; treat a present `z` as equivalent.
                string present = a ?? b;
                return string.Equals(present.Trim(), "z", StringComparison.OrdinalIgnoreCase);
            }
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        static string UnitSuffix(CssLengthUnit u) {
            switch (u) {
                case CssLengthUnit.Px: return "px";
                case CssLengthUnit.Em: return "em";
                case CssLengthUnit.Rem: return "rem";
                case CssLengthUnit.Percent: return "%";
                case CssLengthUnit.Vh: return "vh";
                case CssLengthUnit.Vw: return "vw";
                case CssLengthUnit.Vmin: return "vmin";
                case CssLengthUnit.Vmax: return "vmax";
                case CssLengthUnit.Pt: return "pt";
                case CssLengthUnit.Pc: return "pc";
                case CssLengthUnit.In: return "in";
                case CssLengthUnit.Cm: return "cm";
                case CssLengthUnit.Mm: return "mm";
                case CssLengthUnit.Ch: return "ch";
                case CssLengthUnit.Ex: return "ex";
            }
            return "";
        }

        // EC1 — observability for InterpolateTransformArg's silent mixed-unit
        // length-conversion catch. The dominant trigger is a `%` endpoint with
        // no `LengthContext.BasisPixels` set (e.g. animating
        // `translateX(50%)` -> `translateX(100px)` without a resolvable
        // containing-block basis). Behaviour preserved (bare-number fallback
        // below the catch still runs), but the author now sees the conversion
        // failure routed through UICssDiagnostics. Dedupe key is the offending
        // unit-pair string so a 60Hz sampled animation logs exactly once per
        // session per from/to unit-pair.
        static void WarnTransformLengthConversionFailure(CssLengthUnit fromUnit, CssLengthUnit toUnit, Exception ex) {
            string unitPair = UnitSuffix(fromUnit) + "->" + UnitSuffix(toUnit);
            UICssDiagnostics.Warn(
                "animation",
                "EC1: transform length-conversion failed for unit-pair " + unitPair +
                " (" + (ex?.GetType().Name ?? "Exception") +
                "); falling back to bare-number interp.");
        }

        // EC10 — observability for the by-design discrete-step fallback when
        // FilterParser rejects an animation endpoint. Process-static dedupe
        // mirrors the ColorResolver DD2/DD3 pattern: keyed on the offending
        // (from|to) pair so one bad keyframe doesn't spam the console every
        // frame the interpolator samples it. Single session, never cleared
        // except via ResetWarnings_TestOnly.
        static readonly HashSet<string> s_WarnedFilterKeys = new HashSet<string>();

        static void WarnFilterParseFailure(string fromRaw, string toRaw, Exception ex) {
            string key = "EC10:" + (fromRaw ?? "") + "|" + (toRaw ?? "");
            lock (s_WarnedFilterKeys) {
                if (!s_WarnedFilterKeys.Add(key)) return;
            }
            UICssDiagnostics.Warn(
                "ValueInterpolator",
                "EC10: filter() parse failed (" + (ex?.GetType().Name ?? "Exception") +
                ") interpolating '" + (fromRaw ?? "") + "' -> '" + (toRaw ?? "") +
                "'; falling back to discrete-step per CSS Animations L1.");
        }

        // Test hook — wipes the dedupe set so a re-running test can observe a
        // warning that was already emitted by an earlier test in the same
        // session. Not part of the production contract.
        internal static void ResetWarnings_TestOnly() {
            lock (s_WarnedFilterKeys) s_WarnedFilterKeys.Clear();
            UICssDiagnostics.ResetForTests();
        }
    }
}
