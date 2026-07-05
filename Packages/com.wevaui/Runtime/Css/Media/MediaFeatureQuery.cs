using System;
using System.Globalization;
using Weva.Css.Values;

namespace Weva.Css.Media {
    public enum MediaFeatureRange {
        Equals,
        Min,
        Max
    }

    public sealed class MediaFeatureQuery : MediaQuery {
        public string Name { get; }
        public string ValueText { get; }
        public MediaFeatureRange Range { get; }

        // Pre-parsed CssValue cached at construction. @media queries are
        // compiled once per stylesheet and Evaluate() is invoked once per
        // cascade pass — pre-parsing here turns the steady-state cost into a
        // dispatch on an already-typed value with no Parse round trip. The
        // raw text is preserved on ValueText for callers that inspect it.
        readonly CssValue parsedValue;
        // 0=not-attempted (text was null/empty), 1=parsed-ok, 2=parsed-failed.
        readonly byte parsedState;

        public MediaFeatureQuery(string name, string valueText, MediaFeatureRange range) {
            Name = name == null ? "" : CssStringUtil.ToLowerInvariantOrSame(name);
            ValueText = valueText;
            Range = range;
            if (string.IsNullOrEmpty(valueText)) {
                parsedValue = null;
                parsedState = 0;
            } else {
                try {
                    parsedValue = CssValueParser.Parse(valueText.Trim());
                    parsedState = parsedValue != null ? (byte)1 : (byte)2;
                } catch {
                    parsedValue = null;
                    parsedState = 2;
                }
            }
        }

        public override bool Evaluate(MediaContext ctx) {
            string baseName;
            MediaFeatureRange unused;
            ResolveBase(Name, out baseName, out unused);
            var range = Range;

            switch (baseName) {
                case "width": return EvaluateLength(ctx.ViewportWidthPx, range, ctx);
                case "height": return EvaluateLength(ctx.ViewportHeightPx, range, ctx);
                case "aspect-ratio": return EvaluateAspectRatio(ctx, range);
                case "orientation": return EvaluateOrientation(ctx);
                case "resolution": return EvaluateResolution(ctx, range);
                case "prefers-color-scheme": return EvaluateColorScheme(ctx);
                case "prefers-reduced-motion": return EvaluateReducedMotion(ctx);
                case "hover":
                case "any-hover":
                    return EvaluateHover(ctx);
                case "pointer":
                case "any-pointer":
                    return EvaluatePointer(ctx);
            }
            Weva.Diagnostics.UICssDiagnostics.Warn(
                "MediaQueryParser",
                "unrecognised @media feature '" + baseName + "'");
            return false;
        }

        static void ResolveBase(string name, out string baseName, out MediaFeatureRange range) {
            if (name.StartsWith("min-")) {
                baseName = name.Substring(4);
                range = MediaFeatureRange.Min;
                return;
            }
            if (name.StartsWith("max-")) {
                baseName = name.Substring(4);
                range = MediaFeatureRange.Max;
                return;
            }
            baseName = name;
            range = MediaFeatureRange.Equals;
        }

        bool EvaluateLength(double contextPx, MediaFeatureRange range, MediaContext ctx) {
            if (parsedState != 1) return false;
            if (!TryResolveParsedLengthPx(parsedValue, ctx, out double valuePx)) return false;
            return Compare(contextPx, valuePx, range);
        }

        bool EvaluateAspectRatio(MediaContext ctx, MediaFeatureRange range) {
            if (string.IsNullOrEmpty(ValueText)) return false;
            if (!TryParseRatio(ValueText, out double ratio)) return false;
            if (ctx.ViewportHeightPx <= 0) return false;
            double actual = ctx.ViewportWidthPx / ctx.ViewportHeightPx;
            return Compare(actual, ratio, range);
        }

        bool EvaluateOrientation(MediaContext ctx) {
            if (string.IsNullOrEmpty(ValueText)) return false;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(ValueText, "portrait")) return ctx.Orientation == Orientation.Portrait;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(ValueText, "landscape")) return ctx.Orientation == Orientation.Landscape;
            return false;
        }

        bool EvaluateResolution(MediaContext ctx, MediaFeatureRange range) {
            if (string.IsNullOrEmpty(ValueText)) return false;
            if (!TryParseResolutionDpi(ValueText, out double valueDpi)) return false;
            return Compare(ctx.DpiPixelsPerInch, valueDpi, range);
        }

        bool EvaluateColorScheme(MediaContext ctx) {
            if (string.IsNullOrEmpty(ValueText)) return false;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(ValueText, "light")) return ctx.ColorScheme == ColorScheme.Light;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(ValueText, "dark")) return ctx.ColorScheme == ColorScheme.Dark;
            return false;
        }

        bool EvaluateReducedMotion(MediaContext ctx) {
            if (string.IsNullOrEmpty(ValueText)) return false;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(ValueText, "no-preference")) return !ctx.PrefersReducedMotion;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(ValueText, "reduce")) return ctx.PrefersReducedMotion;
            return false;
        }

        bool EvaluateHover(MediaContext ctx) {
            if (string.IsNullOrEmpty(ValueText)) {
                return ctx.Hover != HoverCapability.None;
            }
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(ValueText, "none")) return ctx.Hover == HoverCapability.None;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(ValueText, "hover")) return ctx.Hover == HoverCapability.Hover;
            return false;
        }

        bool EvaluatePointer(MediaContext ctx) {
            if (string.IsNullOrEmpty(ValueText)) {
                return ctx.Pointer != PointerCapability.None;
            }
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(ValueText, "none")) return ctx.Pointer == PointerCapability.None;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(ValueText, "coarse")) return ctx.Pointer == PointerCapability.Coarse;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(ValueText, "fine")) return ctx.Pointer == PointerCapability.Fine;
            return false;
        }

        static bool Compare(double actual, double target, MediaFeatureRange range) {
            switch (range) {
                case MediaFeatureRange.Min: return actual >= target;
                case MediaFeatureRange.Max: return actual <= target;
                default: return actual == target;
            }
        }

        // Resolves the ctor-parsed CssValue to pixels against a LengthContext
        // derived from the current MediaContext. Mirrors the prior
        // TryParseLengthPx dispatch but skips the per-Evaluate CssValueParser
        // hop — the predicate value is parsed once at rule-compile time.
        static bool TryResolveParsedLengthPx(CssValue parsed, MediaContext ctx, out double pixels) {
            pixels = 0;
            if (parsed == null) return false;
            if (parsed is CssLength len) {
                var lc = LengthContext.Default;
                lc.ViewportWidthPx = ctx.ViewportWidthPx;
                lc.ViewportHeightPx = ctx.ViewportHeightPx;
                lc.DpiPixelsPerInch = ctx.DpiPixelsPerInch;
                pixels = len.ToPixels(lc);
                return true;
            }
            if (parsed is CssNumber num && num.Value == 0) {
                pixels = 0;
                return true;
            }
            return false;
        }

        static bool TryParseRatio(string text, out double ratio) {
            ratio = 0;
            string s = text.Trim();
            if (s.Length == 0) return false;
            int slash = s.IndexOf('/');
            string left, right;
            if (slash >= 0) {
                left = s.Substring(0, slash).Trim();
                right = s.Substring(slash + 1).Trim();
            } else {
                int sp = s.IndexOf(' ');
                if (sp < 0) {
                    if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double single)) return false;
                    ratio = single;
                    return true;
                }
                left = s.Substring(0, sp).Trim();
                right = s.Substring(sp + 1).Trim();
            }
            if (!double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out double ln)) return false;
            if (!double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out double rn)) return false;
            if (rn == 0) return false;
            ratio = ln / rn;
            return true;
        }

        static bool TryParseResolutionDpi(string text, out double dpi) {
            dpi = 0;
            string s = CssStringUtil.ToLowerInvariantOrSame(text.Trim());
            if (s.Length == 0) return false;
            ReadOnlySpan<char> num;
            double scale;
            if (s.EndsWith("dppx")) { num = s.AsSpan(0, s.Length - 4); scale = 96.0; }
            else if (s.EndsWith("dpcm")) { num = s.AsSpan(0, s.Length - 4); scale = 2.54; }
            else if (s.EndsWith("dpi")) { num = s.AsSpan(0, s.Length - 3); scale = 1.0; }
            else return false;
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return false;
            dpi = n * scale;
            return true;
        }
    }
}
