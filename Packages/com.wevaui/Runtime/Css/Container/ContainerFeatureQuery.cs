using System.Globalization;
using Weva.Css.Values;

namespace Weva.Css.Container {
    public sealed class ContainerFeatureQuery : ContainerQuery {
        public string Name { get; }
        public string ValueText { get; }
        public ContainerFeatureRange Range { get; }

        // Pre-parsed value cached at construction. @container queries are
        // compiled once per stylesheet but Evaluate() runs once per cascade
        // pass per matched element — re-running CssValueParser.Parse on the
        // same predicate every pass is pure overhead. The parser is invoked
        // exactly once here (or kept as a sentinel when the text is null /
        // unparseable) and Evaluate dispatches on the typed result.
        readonly CssValue parsedValue;
        // 0=not-attempted (text was null/empty), 1=parsed-ok, 2=parsed-failed.
        readonly byte parsedState;

        public ContainerFeatureQuery(string name, string valueText, ContainerFeatureRange range) {
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

        public override bool Evaluate(ContainerContext ctx) {
            // Container queries cannot resolve against an unknown ancestor: a ctx with
            // Type == None means no matching containment context was found, so every
            // feature evaluates false (mirrors the spec's "indeterminate => false").
            if (ctx.Type == ContainerType.None) return false;

            string baseName;
            ResolveBase(Name, out baseName);
            var range = Range;

            switch (baseName) {
                case "width":
                case "inline-size":
                    return EvaluateLength(ctx.InlineSizePx, range);
                case "height":
                case "block-size":
                    if (ctx.Type != ContainerType.Size) return false;
                    return EvaluateLength(ctx.BlockSizePx, range);
                case "aspect-ratio":
                    return EvaluateAspectRatio(ctx, range);
                case "orientation":
                    return EvaluateOrientation(ctx);
            }
            return false;
        }

        static void ResolveBase(string name, out string baseName) {
            if (name.StartsWith("min-")) {
                baseName = name.Substring(4);
                return;
            }
            if (name.StartsWith("max-")) {
                baseName = name.Substring(4);
                return;
            }
            baseName = name;
        }

        bool EvaluateLength(double contextPx, ContainerFeatureRange range) {
            // parsedValue was built at ctor time; here we just unwrap to pixels.
            if (parsedState != 1) return false;
            if (!TryResolveParsedLengthPx(parsedValue, out double valuePx)) return false;
            return Compare(contextPx, valuePx, range);
        }

        bool EvaluateAspectRatio(ContainerContext ctx, ContainerFeatureRange range) {
            if (string.IsNullOrEmpty(ValueText)) return false;
            if (!TryParseRatio(ValueText, out double ratio)) return false;
            // aspect-ratio depends on both axes; without size containment we have no block size.
            if (ctx.Type != ContainerType.Size) return false;
            if (ctx.BlockSizePx <= 0) return false;
            double actual = ctx.InlineSizePx / ctx.BlockSizePx;
            return Compare(actual, ratio, range);
        }

        bool EvaluateOrientation(ContainerContext ctx) {
            if (string.IsNullOrEmpty(ValueText)) return false;
            if (ctx.Type != ContainerType.Size) return false;
            bool landscape = ctx.InlineSizePx >= ctx.BlockSizePx;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(ValueText, "portrait")) return !landscape;
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(ValueText, "landscape")) return landscape;
            return false;
        }

        static bool Compare(double actual, double target, ContainerFeatureRange range) {
            switch (range) {
                case ContainerFeatureRange.Min: return actual >= target;
                case ContainerFeatureRange.Max: return actual <= target;
                default: return actual == target;
            }
        }

        // Resolves the ctor-parsed CssValue to pixels against the default
        // length context. Mirrors the prior TryParseLengthPx dispatch but
        // operates on an already-typed tree — no Parse round trip.
        static bool TryResolveParsedLengthPx(CssValue parsed, out double pixels) {
            pixels = 0;
            if (parsed == null) return false;
            if (parsed is CssLength len) {
                pixels = len.ToPixels(LengthContext.Default);
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
    }
}
