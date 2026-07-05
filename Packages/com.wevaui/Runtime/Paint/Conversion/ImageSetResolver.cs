using System;
using System.Collections.Generic;
using System.Globalization;
using Weva.Css.Values;

namespace Weva.Paint.Conversion {
    // CSS Images L4 §5.4 — image-set() candidate picker.
    //
    // Syntax (the v1 subset we honour):
    //   image-set( <image-set-option># )
    //   <image-set-option> = [ <image> | <string> ] [ <resolution> ]?
    //   <resolution>       = <number>x | <number>dppx | <number>dpi
    //
    // The full L4 grammar also permits `type(<string>)` after the resolution
    // and the value `from-image` in place of an explicit resolution. We parse
    // through those tokens but do not promote them to selection input; the
    // picker uses only the numeric resolution. A bare option with no
    // resolution defaults to `1x` (per spec).
    //
    // Selection rule:
    //   1. Skip options whose image handle is empty/unrecognised.
    //   2. Among the rest, prefer the smallest resolution that is >= the
    //      target DPR (so a 2x display picks 2x over 3x, conserving texture
    //      memory). If no candidate is >= target, fall back to the highest
    //      available resolution (a 3x display with only 1x/2x available
    //      picks 2x). Equal-resolution ties go to source-order — matches
    //      Blink's `ImageResourceContent::GetImage`.
    //
    // Reads the resolution numerically with NumberStyles.Float +
    // CultureInfo.InvariantCulture — never PowerShell-locale-aware.
    internal static class ImageSetResolver {
        // Picks the best candidate's image handle from an image-set()
        // function-call node. Returns false when no usable candidate
        // exists (e.g. all entries are gradients or unrecognised), so
        // the caller can decide whether to drop the layer or keep
        // walking.
        public static bool TryResolve(CssFunctionCall fn, double targetDpr, out string handle) {
            handle = null;
            if (fn == null) return false;
            if (!IsImageSetName(fn.Name)) return false;
            if (fn.Arguments == null || fn.Arguments.Count == 0) return false;
            if (targetDpr <= 0) targetDpr = 1.0;

            string bestHandle = null;
            double bestResolution = double.NaN;
            string fallbackHandle = null;
            double fallbackResolution = double.NegativeInfinity;

            for (int i = 0; i < fn.Arguments.Count; i++) {
                if (!TryParseOption(fn.Arguments[i], out string optHandle, out double optResolution)) continue;
                if (string.IsNullOrEmpty(optHandle)) continue;

                // Track absolute maximum for the "no candidate >= target" fallback.
                if (optResolution > fallbackResolution) {
                    fallbackResolution = optResolution;
                    fallbackHandle = optHandle;
                }

                // Among candidates >= target, keep the smallest.
                if (optResolution >= targetDpr) {
                    if (double.IsNaN(bestResolution) || optResolution < bestResolution) {
                        bestResolution = optResolution;
                        bestHandle = optHandle;
                    }
                }
            }

            if (bestHandle != null) {
                handle = bestHandle;
                return true;
            }
            if (fallbackHandle != null) {
                handle = fallbackHandle;
                return true;
            }
            return false;
        }

        // Same as TryResolve, but operates on the raw text of an image-set(...)
        // expression. Used by the raw-fallback path in BackgroundResolver,
        // where the parsed tree contains opaque CssIdentifiers because
        // CssValueParser bailed on a token shape it doesn't recognise.
        public static bool TryResolveRaw(string body, double targetDpr, out string handle) {
            handle = null;
            if (string.IsNullOrEmpty(body)) return false;
            if (targetDpr <= 0) targetDpr = 1.0;
            var parts = RawValueParser.SplitTopLevelCommas(body);
            if (parts.Count == 0) return false;

            string bestHandle = null;
            double bestResolution = double.NaN;
            string fallbackHandle = null;
            double fallbackResolution = double.NegativeInfinity;

            for (int i = 0; i < parts.Count; i++) {
                if (!TryParseRawOption(parts[i], out string optHandle, out double optResolution)) continue;
                if (string.IsNullOrEmpty(optHandle)) continue;

                if (optResolution > fallbackResolution) {
                    fallbackResolution = optResolution;
                    fallbackHandle = optHandle;
                }
                if (optResolution >= targetDpr) {
                    if (double.IsNaN(bestResolution) || optResolution < bestResolution) {
                        bestResolution = optResolution;
                        bestHandle = optHandle;
                    }
                }
            }

            if (bestHandle != null) { handle = bestHandle; return true; }
            if (fallbackHandle != null) { handle = fallbackHandle; return true; }
            return false;
        }

        // Combined helper: try the parsed-tree path first; fall back to the
        // raw text when typed parsing yielded no usable candidate. This is the
        // robust shape callers should use, because BackgroundResolver's raw
        // fallback path constructs CssFunctionCalls whose Arguments are bag-
        // of-CssIdentifier strings (TryResolve cannot match them).
        public static bool TryResolveFromFunctionCall(CssFunctionCall fn, double targetDpr, out string handle) {
            if (TryResolve(fn, targetDpr, out handle)) return true;
            if (fn == null) return false;
            string raw = fn.Raw;
            if (string.IsNullOrEmpty(raw)) return false;
            if (!RawValueParser.TryParseFunctionCall(raw, out _, out var body)) return false;
            return TryResolveRaw(body, targetDpr, out handle);
        }

        public static bool IsImageSetName(string name) {
            if (string.IsNullOrEmpty(name)) return false;
            // CSS Images L4 lists `image-set` as the standard form; Safari /
            // pre-standard Blink shipped `-webkit-image-set`. Both should
            // resolve identically.
            return name.Equals("image-set", StringComparison.OrdinalIgnoreCase)
                || name.Equals("-webkit-image-set", StringComparison.OrdinalIgnoreCase);
        }

        // One image-set option as a parsed CssValue. May arrive as:
        //   - a single CssUrl                (`url("a.png")` with no res; defaults to 1x)
        //   - a Space-list                   (`url("a.png") 2x`)
        //   - a single CssString             (`"a.png"`)
        //   - a Space-list starting w/ str   (`"a.png" 2x`)
        static bool TryParseOption(CssValue option, out string handle, out double resolutionDppx) {
            handle = null;
            resolutionDppx = 1.0;
            if (option == null) return false;

            // Single-leaf shorthand (no resolution suffix → 1x default).
            if (option is CssUrl uOnly) {
                handle = uOnly.Href ?? "";
                return !string.IsNullOrEmpty(handle);
            }
            if (option is CssString sOnly) {
                handle = sOnly.Value;
                return !string.IsNullOrEmpty(handle);
            }

            if (!(option is CssValueList list) || list.Separator != CssValueListSeparator.Space) return false;
            if (list.Items.Count == 0) return false;

            // First child carries the image identifier.
            var first = list.Items[0];
            if (first is CssUrl u) handle = u.Href ?? "";
            else if (first is CssString s) handle = s.Value;
            else return false;
            if (string.IsNullOrEmpty(handle)) return false;

            // Walk remaining tokens for the first resolution-shaped value.
            // CSS Images L4 also permits `type(<string>)`; we skip past it
            // without erroring so authors can use it once we wire format
            // negotiation.
            bool foundResolution = false;
            for (int i = 1; i < list.Items.Count; i++) {
                if (TryReadResolutionFromValue(list.Items[i], out double resDppx)) {
                    resolutionDppx = resDppx;
                    foundResolution = true;
                    break;
                }
            }
            if (!foundResolution) resolutionDppx = 1.0;
            return true;
        }

        static bool TryParseRawOption(string raw, out string handle, out double resolutionDppx) {
            handle = null;
            resolutionDppx = 1.0;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            raw = raw.Trim();

            // Extract the leading image source: url(...) or a bare quoted string.
            int srcEnd;
            if (raw.StartsWith("url(", StringComparison.OrdinalIgnoreCase)) {
                int close = FindMatchingParen(raw, 3);
                if (close < 0) return false;
                string inner = raw.Substring(4, close - 4).Trim();
                if (inner.Length >= 2 && (inner[0] == '"' || inner[0] == '\'') && inner[inner.Length - 1] == inner[0]) {
                    inner = inner.Substring(1, inner.Length - 2);
                }
                handle = inner;
                srcEnd = close + 1;
            } else if (raw.Length > 0 && (raw[0] == '"' || raw[0] == '\'')) {
                char quote = raw[0];
                int close = raw.IndexOf(quote, 1);
                if (close < 0) return false;
                handle = raw.Substring(1, close - 1);
                srcEnd = close + 1;
            } else {
                return false;
            }
            if (string.IsNullOrEmpty(handle)) return false;

            string rest = srcEnd < raw.Length ? raw.Substring(srcEnd).Trim() : "";
            if (rest.Length == 0) {
                resolutionDppx = 1.0;
                return true;
            }
            // First whitespace-delimited token after the source carries the
            // resolution; anything else (type(...), trailing junk) is ignored.
            int wsIdx = 0;
            while (wsIdx < rest.Length && !char.IsWhiteSpace(rest[wsIdx])) wsIdx++;
            string token = rest.Substring(0, wsIdx);
            if (TryParseResolutionToken(token, out double dppx)) {
                resolutionDppx = dppx;
                return true;
            }
            resolutionDppx = 1.0;
            return true;
        }

        static int FindMatchingParen(string text, int openIdx) {
            int depth = 0;
            for (int i = openIdx; i < text.Length; i++) {
                if (text[i] == '(') depth++;
                else if (text[i] == ')') {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        // Reads a resolution from a parsed value. CssValueParser doesn't
        // currently have a dedicated CssResolution type — dimensions are
        // surfaced as CssNumber (for unitless), CssLength (for `x` recognised
        // as a length-like dimension, which it ISN'T), or CssIdentifier
        // carrying the raw token like `2x`. We accept all three shapes
        // defensively and let TryParseResolutionToken do the unit math.
        static bool TryReadResolutionFromValue(CssValue v, out double dppx) {
            dppx = 0;
            if (v == null) return false;
            string raw = v.Raw;
            if (!string.IsNullOrEmpty(raw) && TryParseResolutionToken(raw.Trim(), out dppx)) return true;
            if (v is CssIdentifier id && TryParseResolutionToken(id.Name.Trim(), out dppx)) return true;
            return false;
        }

        // CSS Values L4 §5.5: <resolution> = <number>(dpi|dpcm|dppx|x).
        // image-set() in particular also accepts the `x` alias for dppx and a
        // plain integer for the `dpi` unit is NOT valid (must carry a unit).
        // 1dppx = 1x = 96dpi = 1/2.54 * 96 dpcm.
        static bool TryParseResolutionToken(string token, out double dppx) {
            dppx = 0;
            if (string.IsNullOrEmpty(token)) return false;
            // Split number + unit suffix.
            int u = 0;
            while (u < token.Length && (char.IsDigit(token[u]) || token[u] == '.' || token[u] == '-' || token[u] == '+')) u++;
            if (u == 0) return false;
            string numPart = token.Substring(0, u);
            string unit = u < token.Length ? token.Substring(u).ToLowerInvariant() : "";
            if (!double.TryParse(numPart, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return false;
            switch (unit) {
                case "x":
                case "dppx":
                    dppx = n;
                    return n > 0;
                case "dpi":
                    dppx = n / 96.0;
                    return n > 0;
                case "dpcm":
                    dppx = n * 2.54 / 96.0;
                    return n > 0;
                default:
                    return false;
            }
        }

        // Convenience helper for callers that already have a LengthContext on
        // hand (BackgroundResolver does) but no MediaContext. DPR = host DPI /
        // CSS reference DPI (96).
        public static double DprFromLengthContext(Weva.Css.Values.LengthContext ctx) {
            double host = ctx.DpiPixelsPerInch;
            if (host <= 0) return 1.0;
            return host / 96.0;
        }
    }
}
