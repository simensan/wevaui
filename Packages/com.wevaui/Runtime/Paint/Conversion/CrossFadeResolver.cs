using System;
using System.Collections.Generic;
using System.Globalization;
using Weva.Css.Cascade;
using Weva.Css.Values;

namespace Weva.Paint.Conversion {
    // CSS Images L3 §2.6 / Chrome's -webkit-cross-fade() implementation.
    //
    // Chrome ships the older two-image form (not the L4 n-image grammar):
    //
    //   cross-fade(<image>, <image>, <percentage>)
    //   -webkit-cross-fade(<image>, <image>, <percentage>)
    //
    // Semantics:
    //   result = first*(1-p) + second*p   (premultiplied source-over compositing)
    //   p = percentage applied to the SECOND image (defaults to 50% when omitted).
    //   p is clamped to [0%, 100%].
    //
    // Chrome implementation note: an unparseable cross-fade() declaration is
    // treated as invalid and the WHOLE declaration is dropped — we mirror this
    // by returning false and letting the caller omit the layer entirely.
    //
    // Rendering approach (layer-duplication):
    //   The GPU backend cannot blend two textures in one brush, so we expand a
    //   single cross-fade() layer into TWO background layers:
    //     layer A — first image  at LayerAlpha = (1-p)
    //     layer B — second image at LayerAlpha = p
    //   BoxToPaintConverter wraps each layer in PushOpacity/PopOpacity when
    //   LayerAlpha < 1. For fully opaque source images this is pixel-exact;
    //   for translucent sources the two layers alpha-compose independently
    //   (not premultiplied together), which is a v1 documented divergence.
    //
    // Gradient operands work automatically because each operand is resolved via
    // the same BackgroundResolver helpers that handle linear-gradient() etc. in
    // regular background-image layers — the opacity wrapping then achieves the
    // same visual blend weight.
    internal static class CrossFadeResolver {
        // Returns true for "cross-fade" and "-webkit-cross-fade" (already
        // lowercased — CssFunctionCall ctor lowercases names).
        public static bool IsCrossFadeName(string name) {
            if (string.IsNullOrEmpty(name)) return false;
            return name.Equals("cross-fade", StringComparison.Ordinal)
                || name.Equals("-webkit-cross-fade", StringComparison.Ordinal);
        }

        // Parses the body of a cross-fade()/−webkit-cross-fade() call.
        // `body` is the raw text INSIDE the outer parentheses (i.e. what
        // RawValueParser.TryParseFunctionCall returns as `inner`).
        //
        // On success, sets:
        //   firstRaw  — raw text of the first operand (may be url(), gradient, etc.)
        //   secondRaw — raw text of the second operand
        //   alpha     — blend weight for the SECOND image in [0, 1]
        //
        // Returns false if the body doesn't match the expected shape (Chrome
        // discards the entire declaration in that case).
        public static bool TryParse(string body, out string firstRaw, out string secondRaw, out float alpha) {
            firstRaw = null;
            secondRaw = null;
            alpha = 0.5f;

            if (string.IsNullOrEmpty(body)) return false;

            // Split top-level commas: we expect 2 or 3 tokens.
            var parts = RawValueParser.SplitTopLevelCommas(body);
            if (parts.Count < 2 || parts.Count > 3) return false;

            // Validate at least two non-empty operands.
            string p0 = parts[0].Trim();
            string p1 = parts[1].Trim();
            if (p0.Length == 0 || p1.Length == 0) return false;

            // When there are exactly 3 parts the LAST must be the percentage.
            if (parts.Count == 3) {
                string pctRaw = parts[2].Trim();
                if (!TryParsePercentage(pctRaw, out float parsedAlpha)) return false;
                alpha = Clamp01(parsedAlpha);
                firstRaw = p0;
                secondRaw = p1;
                return true;
            }

            // Two parts: check whether the SECOND part ends with a % (the
            // percentage-without-third-arg form that some implementations allow,
            // where the percentage is attached to the second image). Chrome
            // actually requires exactly three comma-separated tokens when a
            // percentage is present, so we only accept the default-50% form here.
            // Assign defaults and let the caller build both layers.
            firstRaw = p0;
            secondRaw = p1;
            alpha = 0.5f;
            return true;
        }

        // Attempts to resolve a single operand string to a Brush (url(), gradient,
        // image-set(), or color). Mirrors the private helpers in BackgroundResolver
        // that are accessible here via the raw-text fallback path.
        // Returns null when the operand cannot be resolved (which causes the caller
        // to treat the whole cross-fade() as invalid per Chrome's behaviour).
        public static Brush ResolveOperand(string raw, LinearColor currentColor, Rect bounds,
                                           ImageRenderingMode rendering, double dpr) {
            if (string.IsNullOrEmpty(raw)) return null;
            raw = raw.Trim();

            // url("...") / url(...) image handle.
            if (raw.StartsWith("url(", StringComparison.OrdinalIgnoreCase)) {
                if (!RawValueParser.TryParseFunctionCall(raw, out _, out var urlBody)) return null;
                string handle = urlBody?.Trim().Trim('"', '\'') ?? "";
                if (handle.Length == 0) return null;
                return Brush.ImageFullRect(handle, rendering);
            }

            // Function call: gradient or image-set.
            if (!RawValueParser.TryParseFunctionCall(raw, out var name, out var fnBody)) {
                // Not a function — might be a bare color keyword used as a solid fill.
                // Chrome supports <color> as a cross-fade operand (L4 §2.6).
                if (RawValueParser.LooksLikeColor(raw)) {
                    if (ColorResolver.TryResolve(raw, currentColor, null, out var col)) {
                        return Brush.SolidColor(col);
                    }
                }
                return null;
            }

            if (ImageSetResolver.IsImageSetName(name)) {
                if (ImageSetResolver.TryResolveRaw(fnBody, dpr, out var pickedHandle)) {
                    return Brush.ImageFullRect(pickedHandle, rendering);
                }
                return null;
            }

            // Gradient: build a faux CssFunctionCall from the raw text.
            var argStrings = RawValueParser.SplitTopLevelCommas(fnBody);
            var fauxArgs = new List<CssValue>(argStrings.Count);
            for (int i = 0; i < argStrings.Count; i++) {
                string a = argStrings[i].Trim();
                fauxArgs.Add(new CssIdentifier(a, a));
            }
            var fauxFn = new CssFunctionCall(name, fauxArgs, raw);
            var grad = BackgroundResolver.TryParseGradient(fauxFn, currentColor, bounds);
            if (grad != null) return Brush.Gradient(grad);

            return null;
        }

        // Resolves a cross-fade() layer into two brushes appended to `output`.
        // Mirrors the contract of BackgroundResolver.ResolveBackgroundLayersInto:
        //   - On success: appends exactly two Brush entries (first at 1-alpha, second at alpha).
        //   - On failure: appends nothing (Chrome discards the declaration).
        //
        // Returns true when both operands resolved successfully; false otherwise.
        public static bool TryExpandIntoLayers(
            string body,
            LinearColor currentColor,
            Rect bounds,
            ImageRenderingMode rendering,
            double dpr,
            List<Brush> output
        ) {
            if (!TryParse(body, out var firstRaw, out var secondRaw, out float alpha)) return false;

            var first = ResolveOperand(firstRaw, currentColor, bounds, rendering, dpr);
            if (first == null) return false;

            var second = ResolveOperand(secondRaw, currentColor, bounds, rendering, dpr);
            if (second == null) return false;

            float alphaFirst = 1f - alpha;
            float alphaSecond = alpha;

            // Emit both layers. LayerAlpha = 1 is a no-op (the emitter won't
            // wrap in PushOpacity), which handles the edge cases p=0 and p=1.
            output.Add(alphaFirst >= 1f ? first : first.WithLayerAlpha(alphaFirst));
            output.Add(alphaSecond >= 1f ? second : second.WithLayerAlpha(alphaSecond));
            return true;
        }

        // ---- helpers ----

        static bool TryParsePercentage(string s, out float value) {
            value = 0f;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            if (!s.EndsWith("%")) return false;
            if (!double.TryParse(s.AsSpan(0, s.Length - 1), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double d)) return false;
            value = (float)d / 100f;
            return true;
        }

        static float Clamp01(float v) {
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
        }
    }
}
