using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Weva.Css.Values;
using Weva.Paint;

namespace Weva.Css.Animation {
    // H18b: multi-component animatable property interpolators.
    //
    //   - background-position / background-size: per-axis <length-percentage>
    //     pairs, per-layer when comma-separated. Keywords (center/left/right/
    //     top/bottom / auto / cover / contain) resolve to numeric percentages
    //     where possible; mismatched keyword cases fall back to discrete.
    //   - box-shadow / text-shadow: per-shadow component lerp; inset flag is
    //     not interpolable (mismatched inset → discrete for that shadow,
    //     which per spec demotes the whole list to discrete-step).
    //   - clip-path: same-shape basic-shape interpolation only; different
    //     shapes (or polygon vertex-count mismatch) fall back to discrete.
    //
    // Spec refs: CSS Backgrounds 3 §3.5 (shadows), §3.7-3.8 (position/size),
    // CSS Masking §6 (clip-path), CSS Animations 1 §3 (animatable values).
    public static partial class ValueInterpolator {

        // --- background-position --------------------------------------------------

        internal static string InterpolateBackgroundPosition(string from, string to, double t, LengthContext ctx) {
            return InterpolateBackgroundPair(from, to, t, ctx, isSize: false);
        }

        internal static string InterpolateBackgroundSize(string from, string to, double t, LengthContext ctx) {
            return InterpolateBackgroundPair(from, to, t, ctx, isSize: true);
        }

        // Comma-split into layers, then per-layer interpolation. When the
        // layer counts differ the spec leaves us a discrete step.
        static string InterpolateBackgroundPair(string from, string to, double t, LengthContext ctx, bool isSize) {
            var fromLayers = SplitTopLevelCommas(from);
            var toLayers = SplitTopLevelCommas(to);
            if (fromLayers.Count != toLayers.Count) return t < 0.5 ? from : to;
            var sb = new StringBuilder(from.Length + to.Length);
            for (int i = 0; i < fromLayers.Count; i++) {
                if (i > 0) sb.Append(", ");
                string layer = isSize
                    ? InterpolateBackgroundSizeLayer(fromLayers[i], toLayers[i], t, ctx)
                    : InterpolateBackgroundPositionLayer(fromLayers[i], toLayers[i], t, ctx);
                if (layer == null) return t < 0.5 ? from : to;
                sb.Append(layer);
            }
            return sb.ToString();
        }

        // <bg-position>: "<length-percentage> <length-percentage>" with keyword
        // shortcuts. The two tokens map to the X and Y axes; a single token
        // implies the second is "center" (50%). Keywords resolve to:
        //   left/top → 0%, right/bottom → 100%, center → 50%.
        // Mixed-unit lerp (e.g. 0% → 50px) cannot be expressed without a box
        // context here, so falls through the InterpolateLengthOrPercent helper
        // (which has the LengthContext fallback path).
        static string InterpolateBackgroundPositionLayer(string a, string b, double t, LengthContext ctx) {
            var aTokens = SplitWhitespace(a);
            var bTokens = SplitWhitespace(b);
            if (!TryReadBgPositionAxes(aTokens, out string ax, out string ay)) return null;
            if (!TryReadBgPositionAxes(bTokens, out string bx, out string by)) return null;
            string x = InterpolateLengthOrPercent(ResolvePositionKeyword(ax, isYAxis: false),
                                                  ResolvePositionKeyword(bx, isYAxis: false), t, ctx);
            string y = InterpolateLengthOrPercent(ResolvePositionKeyword(ay, isYAxis: true),
                                                  ResolvePositionKeyword(by, isYAxis: true), t, ctx);
            if (x == null || y == null) return null;
            return x + " " + y;
        }

        // 1-token forms: a single position resolves the unspecified axis to
        // `center`. 2-token forms read directly. 3- and 4-token forms (with
        // offset like `left 10px top 20px`) are rare; we fall back to discrete
        // by returning null for them so the caller's discrete-step kicks in.
        static bool TryReadBgPositionAxes(List<string> tokens, out string x, out string y) {
            x = null; y = null;
            if (tokens == null) return false;
            if (tokens.Count == 1) {
                string t = tokens[0];
                if (IsXKeyword(t)) { x = t; y = "center"; return true; }
                if (IsYKeyword(t)) { x = "center"; y = t; return true; }
                // Single length/percentage applies to X only; Y defaults center.
                x = t; y = "center"; return true;
            }
            if (tokens.Count == 2) {
                // Reorder if needed when caller wrote `top left` etc.
                string a = tokens[0], b = tokens[1];
                if (IsYKeyword(a) && IsXKeyword(b)) { x = b; y = a; return true; }
                x = a; y = b; return true;
            }
            return false;
        }

        // <bg-size>: "<length-percentage|auto> [<length-percentage|auto>]" OR
        // "cover" / "contain". cover/contain don't decompose to per-axis lengths
        // and don't interpolate against length pairs (CSS Backgrounds 3 §3.8) —
        // any keyword presence asymmetry falls back to discrete.
        static string InterpolateBackgroundSizeLayer(string a, string b, double t, LengthContext ctx) {
            string aTrim = a.Trim();
            string bTrim = b.Trim();
            bool aCoverContain = IsCoverContainKeyword(aTrim);
            bool bCoverContain = IsCoverContainKeyword(bTrim);
            if (aCoverContain || bCoverContain) {
                if (aCoverContain && bCoverContain && string.Equals(aTrim, bTrim, StringComparison.OrdinalIgnoreCase)) {
                    return aTrim.ToLowerInvariant();
                }
                return null;
            }
            var aTokens = SplitWhitespace(a);
            var bTokens = SplitWhitespace(b);
            if (aTokens == null || bTokens == null) return null;
            string ax = aTokens.Count >= 1 ? aTokens[0] : "auto";
            string ay = aTokens.Count >= 2 ? aTokens[1] : "auto";
            string bx = bTokens.Count >= 1 ? bTokens[0] : "auto";
            string by = bTokens.Count >= 2 ? bTokens[1] : "auto";
            string x = InterpolateBgSizeAxis(ax, bx, t, ctx);
            string y = InterpolateBgSizeAxis(ay, by, t, ctx);
            if (x == null || y == null) return null;
            return x + " " + y;
        }

        static string InterpolateBgSizeAxis(string a, string b, double t, LengthContext ctx) {
            bool aAuto = string.Equals(a.Trim(), "auto", StringComparison.OrdinalIgnoreCase);
            bool bAuto = string.Equals(b.Trim(), "auto", StringComparison.OrdinalIgnoreCase);
            if (aAuto && bAuto) return "auto";
            if (aAuto || bAuto) return null; // `auto ↔ length` is non-animatable per axis.
            return InterpolateLengthOrPercent(a, b, t, ctx);
        }

        static bool IsXKeyword(string s) {
            return string.Equals(s, "left", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "right", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "center", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsYKeyword(string s) {
            return string.Equals(s, "top", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "bottom", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "center", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsCoverContainKeyword(string s) {
            return string.Equals(s, "cover", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "contain", StringComparison.OrdinalIgnoreCase);
        }

        static string ResolvePositionKeyword(string tok, bool isYAxis) {
            if (string.Equals(tok, "center", StringComparison.OrdinalIgnoreCase)) return "50%";
            if (!isYAxis) {
                if (string.Equals(tok, "left", StringComparison.OrdinalIgnoreCase)) return "0%";
                if (string.Equals(tok, "right", StringComparison.OrdinalIgnoreCase)) return "100%";
            } else {
                if (string.Equals(tok, "top", StringComparison.OrdinalIgnoreCase)) return "0%";
                if (string.Equals(tok, "bottom", StringComparison.OrdinalIgnoreCase)) return "100%";
            }
            return tok;
        }

        // --- box-shadow / text-shadow --------------------------------------------

        // CSS Backgrounds 3 §3.5: each shadow has (offset-x, offset-y, blur?,
        // spread?, color?, inset?) and the list is comma-separated. We
        // interpolate per-shadow when lengths match; the inset flag is
        // discrete-only (mismatched inset on any paired shadow demotes that
        // pair to discrete, and by extension the whole list).
        //
        // Padding the shorter list with transparent zero-shadows is what
        // browsers do and gives a nicer fade; do that here. `hasSpread`
        // differs between box-shadow (true) and text-shadow (false).
        internal static string InterpolateShadowList(string from, string to, double t, LengthContext ctx, bool hasSpread) {
            var fromShadows = ParseShadowList(from, hasSpread);
            var toShadows = ParseShadowList(to, hasSpread);
            if (fromShadows == null || toShadows == null) return t < 0.5 ? from : to;
            // Both `none` or both empty ⇒ "none".
            if (fromShadows.Count == 0 && toShadows.Count == 0) return "none";
            // Pad with zero-shadow on the shorter side. Spec allows either
            // discrete or padding; browsers pad.
            int n = Math.Max(fromShadows.Count, toShadows.Count);
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++) {
                ParsedShadow a = i < fromShadows.Count ? fromShadows[i] : ZeroShadow(toShadows[i].Inset);
                ParsedShadow b = i < toShadows.Count ? toShadows[i] : ZeroShadow(fromShadows[i].Inset);
                if (a.Inset != b.Inset) {
                    // Inset mismatch on a paired shadow: per spec the inset
                    // keyword does not interpolate. Demote the WHOLE list to
                    // discrete — partial discrete-on-one-shadow inside an
                    // otherwise smooth list reads worse than a single step.
                    return t < 0.5 ? from : to;
                }
                if (i > 0) sb.Append(", ");
                AppendInterpolatedShadow(sb, a, b, t, ctx, hasSpread);
            }
            return sb.ToString();
        }

        struct ParsedShadow {
            public double Ox, Oy, Blur, Spread;
            public CssLengthUnit OxU, OyU, BlurU, SpreadU;
            public CssColor Color;
            public bool Inset;
            public bool HasColor;
            // `currentColor` written explicitly. It resolves per-element at
            // paint time, so it stays symbolic through interpolation —
            // currentColor↔currentColor pairs (hud's dot-pulse keyframes)
            // must lerp their geometry instead of demoting to discrete.
            public bool IsCurrentColor;
        }

        static ParsedShadow ZeroShadow(bool inset) {
            return new ParsedShadow {
                Ox = 0, Oy = 0, Blur = 0, Spread = 0,
                OxU = CssLengthUnit.Px, OyU = CssLengthUnit.Px,
                BlurU = CssLengthUnit.Px, SpreadU = CssLengthUnit.Px,
                Color = new CssColor(0, 0, 0, 0f), // transparent
                HasColor = true,
                Inset = inset,
            };
        }

        static List<ParsedShadow> ParseShadowList(string text, bool hasSpread) {
            if (string.IsNullOrWhiteSpace(text)) return new List<ParsedShadow>();
            var trimmed = text.Trim();
            if (string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase)) return new List<ParsedShadow>();
            var layers = SplitTopLevelCommas(trimmed);
            var result = new List<ParsedShadow>(layers.Count);
            for (int i = 0; i < layers.Count; i++) {
                if (!TryParseSingleShadow(layers[i], hasSpread, out var sh)) return null;
                result.Add(sh);
            }
            return result;
        }

        static bool TryParseSingleShadow(string text, bool hasSpread, out ParsedShadow shadow) {
            shadow = default;
            var tokens = SplitWhitespace(text);
            if (tokens == null || tokens.Count == 0) return false;
            bool inset = false;
            bool isCurrentColor = false;
            CssColor color = null;
            var lengths = new List<(double v, CssLengthUnit u)>(4);
            for (int i = 0; i < tokens.Count; i++) {
                string tok = tokens[i];
                if (string.Equals(tok, "inset", StringComparison.OrdinalIgnoreCase)) {
                    inset = true;
                    continue;
                }
                if (string.Equals(tok, "currentcolor", StringComparison.OrdinalIgnoreCase)) {
                    if (color != null || isCurrentColor) return false; // two colors not allowed
                    isCurrentColor = true;
                    continue;
                }
                if (TryParseColorToken(tok, out var c)) {
                    if (color != null || isCurrentColor) return false; // two colors not allowed
                    color = c;
                    continue;
                }
                if (TryParseLengthToken(tok, out double v, out var u)) {
                    lengths.Add((v, u));
                    continue;
                }
                // Unrecognised token — discrete fallback.
                return false;
            }
            // CSS Backgrounds 3 §3.5: at least 2 lengths (ox, oy); 3rd is blur,
            // 4th is spread (box-shadow only); text-shadow caps at 3.
            int minLengths = 2;
            int maxLengths = hasSpread ? 4 : 3;
            if (lengths.Count < minLengths || lengths.Count > maxLengths) return false;
            shadow.Ox = lengths[0].v; shadow.OxU = lengths[0].u;
            shadow.Oy = lengths[1].v; shadow.OyU = lengths[1].u;
            shadow.Blur = lengths.Count >= 3 ? lengths[2].v : 0;
            shadow.BlurU = lengths.Count >= 3 ? lengths[2].u : CssLengthUnit.Px;
            shadow.Spread = lengths.Count >= 4 ? lengths[3].v : 0;
            shadow.SpreadU = lengths.Count >= 4 ? lengths[3].u : CssLengthUnit.Px;
            shadow.Color = color;
            shadow.HasColor = color != null;
            shadow.IsCurrentColor = isCurrentColor;
            shadow.Inset = inset;
            return true;
        }

        static void AppendInterpolatedShadow(StringBuilder sb, ParsedShadow a, ParsedShadow b, double t, LengthContext ctx, bool hasSpread) {
            if (a.Inset) {
                sb.Append("inset ");
            }
            AppendLengthLerp(sb, a.Ox, a.OxU, b.Ox, b.OxU, t, ctx);
            sb.Append(' ');
            AppendLengthLerp(sb, a.Oy, a.OyU, b.Oy, b.OyU, t, ctx);
            sb.Append(' ');
            AppendLengthLerp(sb, a.Blur, a.BlurU, b.Blur, b.BlurU, t, ctx);
            if (hasSpread) {
                sb.Append(' ');
                AppendLengthLerp(sb, a.Spread, a.SpreadU, b.Spread, b.SpreadU, t, ctx);
            }
            // Color: an omitted color means `currentColor` (CSS Backgrounds 3
            // §3.5), same as writing it explicitly. currentColor stays
            // symbolic — it resolves per-element at paint time, so a
            // currentColor↔currentColor pair re-emits the keyword and the
            // geometry lerp above stays smooth (hud dot-pulse). Concrete
            // colors lerp in oklab. A concrete↔currentColor mix would need
            // the element's resolved color (not available here), so only the
            // color slot steps discretely while geometry keeps lerping.
            bool aCurrent = a.IsCurrentColor || !a.HasColor;
            bool bCurrent = b.IsCurrentColor || !b.HasColor;
            if (aCurrent && bCurrent) {
                if (a.IsCurrentColor || b.IsCurrentColor) sb.Append(" currentColor");
                // both omitted: emit nothing — identical computed value.
            } else if (!aCurrent && !bCurrent) {
                sb.Append(' ');
                sb.Append(LerpColorOklab(a.Color, b.Color, t));
            } else {
                sb.Append(' ');
                if (t < 0.5) {
                    sb.Append(aCurrent ? "currentColor" : LerpColorOklab(a.Color, a.Color, 0));
                } else {
                    sb.Append(bCurrent ? "currentColor" : LerpColorOklab(b.Color, b.Color, 0));
                }
            }
        }

        // --- clip-path ------------------------------------------------------------

        // CSS Masking §6: clip-path interpolation works between basic-shapes of
        // the same kind. We support inset(), circle(), ellipse(), and polygon()
        // (with matching point counts). Anything else (or different shapes)
        // falls back to discrete.
        internal static string InterpolateClipPath(string from, string to, double t, LengthContext ctx) {
            string a = from.Trim();
            string b = to.Trim();
            if (string.Equals(a, "none", StringComparison.OrdinalIgnoreCase)
                || string.Equals(b, "none", StringComparison.OrdinalIgnoreCase)) {
                return t < 0.5 ? from : to;
            }
            if (!TryParseBasicShape(a, out var aName, out var aArgs)) return t < 0.5 ? from : to;
            if (!TryParseBasicShape(b, out var bName, out var bArgs)) return t < 0.5 ? from : to;
            if (!string.Equals(aName, bName, StringComparison.OrdinalIgnoreCase)) {
                return t < 0.5 ? from : to;
            }
            string lower = aName.ToLowerInvariant();
            string interp = null;
            switch (lower) {
                case "inset": interp = InterpolateInsetShape(aArgs, bArgs, t, ctx); break;
                case "circle": interp = InterpolateCircleShape(aArgs, bArgs, t, ctx); break;
                case "ellipse": interp = InterpolateEllipseShape(aArgs, bArgs, t, ctx); break;
                case "polygon": interp = InterpolatePolygonShape(aArgs, bArgs, t, ctx); break;
                default: return t < 0.5 ? from : to;
            }
            // Per-shape interpolators return null when they hit an internal
            // mismatch (polygon vertex-count diff, inset() with `round` clause,
            // ellipse radii missing on one side, etc.) — apply the spec's
            // discrete fallback here in one place.
            return interp ?? (t < 0.5 ? from : to);
        }

        // inset(<length-percentage>{1,4} [round <border-radius>]?)
        // Simplified support: the four sides only. `round …` (border-radius
        // tail) is preserved as a discrete fallback when present on either side.
        static string InterpolateInsetShape(string a, string b, double t, LengthContext ctx) {
            if (HasRoundClause(a) || HasRoundClause(b)) return t < 0.5 ? "inset(" + a + ")" : "inset(" + b + ")";
            var aTokens = SplitWhitespace(a);
            var bTokens = SplitWhitespace(b);
            if (aTokens == null || bTokens == null) return null;
            // 1/2/3/4-value shorthand → expand to 4-tuple (top, right, bottom, left).
            var aSides = ExpandSidesShorthand(aTokens);
            var bSides = ExpandSidesShorthand(bTokens);
            if (aSides == null || bSides == null) return null;
            string[] lerped = new string[4];
            for (int i = 0; i < 4; i++) {
                lerped[i] = InterpolateLengthOrPercent(aSides[i], bSides[i], t, ctx);
                if (lerped[i] == null) return null;
            }
            // Re-collapse the 4-tuple back to the shortest equivalent form
            // so authors see `inset(20px)` (not `inset(20px 20px 20px 20px)`)
            // when all sides agree. Matches CSS shorthand serialization.
            var sb = new StringBuilder("inset(");
            sb.Append(CollapseSidesShorthand(lerped));
            sb.Append(')');
            return sb.ToString();
        }

        // circle(<shape-radius>? [at <position>]?)
        // We currently support a single radius arg; `at <position>` defaults to
        // center on each side and lerps if both sides specify it.
        static string InterpolateCircleShape(string a, string b, double t, LengthContext ctx) {
            if (!TryParseCircleArgs(a, out string aRadius, out string aPos)) return null;
            if (!TryParseCircleArgs(b, out string bRadius, out string bPos)) return null;
            string radius = (aRadius != null && bRadius != null)
                ? InterpolateLengthOrPercent(aRadius, bRadius, t, ctx)
                : (aRadius ?? bRadius);
            var sb = new StringBuilder("circle(");
            if (radius != null) sb.Append(radius);
            if (aPos != null || bPos != null) {
                if (sb[sb.Length - 1] != '(') sb.Append(' ');
                sb.Append("at ");
                string posA = aPos ?? "center center";
                string posB = bPos ?? "center center";
                string lerpedPos = InterpolateBackgroundPositionLayer(posA, posB, t, ctx);
                sb.Append(lerpedPos ?? (t < 0.5 ? posA : posB));
            }
            sb.Append(')');
            return sb.ToString();
        }

        // ellipse([<shape-radius> <shape-radius>]? [at <position>]?)
        static string InterpolateEllipseShape(string a, string b, double t, LengthContext ctx) {
            if (!TryParseEllipseArgs(a, out string aRx, out string aRy, out string aPos)) return null;
            if (!TryParseEllipseArgs(b, out string bRx, out string bRy, out string bPos)) return null;
            var sb = new StringBuilder("ellipse(");
            if (aRx != null && bRx != null && aRy != null && bRy != null) {
                string rx = InterpolateLengthOrPercent(aRx, bRx, t, ctx);
                string ry = InterpolateLengthOrPercent(aRy, bRy, t, ctx);
                if (rx == null || ry == null) return null;
                sb.Append(rx).Append(' ').Append(ry);
            }
            if (aPos != null || bPos != null) {
                if (sb[sb.Length - 1] != '(') sb.Append(' ');
                sb.Append("at ");
                string posA = aPos ?? "center center";
                string posB = bPos ?? "center center";
                string lerpedPos = InterpolateBackgroundPositionLayer(posA, posB, t, ctx);
                sb.Append(lerpedPos ?? (t < 0.5 ? posA : posB));
            }
            sb.Append(')');
            return sb.ToString();
        }

        // polygon(<fill-rule>? [, <point>]+) where each point is two
        // <length-percentage>. Both sides must have the same point count
        // and the same fill-rule.
        static string InterpolatePolygonShape(string a, string b, double t, LengthContext ctx) {
            var aPoints = SplitTopLevelCommas(a);
            var bPoints = SplitTopLevelCommas(b);
            if (aPoints.Count != bPoints.Count) return null;
            // Detect fill-rule on first token.
            string fillRule = null;
            int aStart = 0, bStart = 0;
            if (aPoints.Count > 0) {
                var firstA = SplitWhitespace(aPoints[0]);
                var firstB = SplitWhitespace(bPoints[0]);
                if (firstA != null && firstB != null && firstA.Count > 0 && firstB.Count > 0) {
                    if (IsFillRule(firstA[0]) && IsFillRule(firstB[0])) {
                        if (!string.Equals(firstA[0], firstB[0], StringComparison.OrdinalIgnoreCase)) return null;
                        fillRule = firstA[0].ToLowerInvariant();
                        // Re-emit first point's coords minus the fill-rule.
                    } else if (IsFillRule(firstA[0]) ^ IsFillRule(firstB[0])) {
                        return null;
                    }
                }
            }
            var sb = new StringBuilder("polygon(");
            if (fillRule != null) { sb.Append(fillRule).Append(", "); }
            for (int i = 0; i < aPoints.Count; i++) {
                if (i > 0) sb.Append(", ");
                var aTok = SplitWhitespace(aPoints[i]);
                var bTok = SplitWhitespace(bPoints[i]);
                if (aTok == null || bTok == null) return null;
                int ai = (i == 0 && fillRule != null) ? 1 : 0;
                int bi = (i == 0 && fillRule != null) ? 1 : 0;
                if (aTok.Count - ai != 2 || bTok.Count - bi != 2) return null;
                string x = InterpolateLengthOrPercent(aTok[ai], bTok[bi], t, ctx);
                string y = InterpolateLengthOrPercent(aTok[ai + 1], bTok[bi + 1], t, ctx);
                if (x == null || y == null) return null;
                sb.Append(x).Append(' ').Append(y);
            }
            sb.Append(')');
            return sb.ToString();
        }

        static bool IsFillRule(string s) =>
            string.Equals(s, "nonzero", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s, "evenodd", StringComparison.OrdinalIgnoreCase);

        static bool TryParseBasicShape(string text, out string name, out string args) {
            name = null; args = null;
            if (string.IsNullOrEmpty(text)) return false;
            int open = text.IndexOf('(');
            if (open <= 0) return false;
            if (!text.EndsWith(")", StringComparison.Ordinal)) return false;
            name = text.Substring(0, open).Trim();
            args = text.Substring(open + 1, text.Length - open - 2).Trim();
            return true;
        }

        static bool TryParseCircleArgs(string args, out string radius, out string pos) {
            radius = null; pos = null;
            if (string.IsNullOrWhiteSpace(args)) return true;
            // Split on "at" keyword at whitespace boundary.
            int atIdx = FindAtKeyword(args);
            if (atIdx >= 0) {
                radius = args.Substring(0, atIdx).Trim();
                if (radius.Length == 0) radius = null;
                pos = args.Substring(atIdx + 2).Trim();
            } else {
                radius = args.Trim();
                if (radius.Length == 0) radius = null;
            }
            return true;
        }

        static bool TryParseEllipseArgs(string args, out string rx, out string ry, out string pos) {
            rx = null; ry = null; pos = null;
            if (string.IsNullOrWhiteSpace(args)) return true;
            int atIdx = FindAtKeyword(args);
            string radiusPart;
            if (atIdx >= 0) {
                radiusPart = args.Substring(0, atIdx).Trim();
                pos = args.Substring(atIdx + 2).Trim();
            } else {
                radiusPart = args.Trim();
            }
            if (!string.IsNullOrEmpty(radiusPart)) {
                var toks = SplitWhitespace(radiusPart);
                if (toks != null && toks.Count == 2) {
                    rx = toks[0]; ry = toks[1];
                } else if (toks != null && toks.Count == 0) {
                    // No radii — both default to closest-side. Treat as null.
                } else {
                    return false;
                }
            }
            return true;
        }

        static int FindAtKeyword(string s) {
            // Find " at " (whitespace bounded) at top depth — args here are
            // already stripped of the outer parens.
            for (int i = 0; i < s.Length - 2; i++) {
                if (char.IsWhiteSpace(s[i])
                    && (s[i + 1] == 'a' || s[i + 1] == 'A')
                    && (s[i + 2] == 't' || s[i + 2] == 'T')
                    && (i + 3 >= s.Length || char.IsWhiteSpace(s[i + 3]))) {
                    return i + 1;
                }
            }
            return -1;
        }

        static bool HasRoundClause(string s) {
            // "<sides> round <radius>" — detect a bare " round " token.
            for (int i = 0; i < s.Length - 5; i++) {
                if (char.IsWhiteSpace(s[i])
                    && (s[i + 1] == 'r' || s[i + 1] == 'R')
                    && (s[i + 2] == 'o' || s[i + 2] == 'O')
                    && (s[i + 3] == 'u' || s[i + 3] == 'U')
                    && (s[i + 4] == 'n' || s[i + 4] == 'N')
                    && (s[i + 5] == 'd' || s[i + 5] == 'D')
                    && (i + 6 >= s.Length || char.IsWhiteSpace(s[i + 6]))) {
                    return true;
                }
            }
            return false;
        }

        // Inverse of ExpandSidesShorthand: collapse (top, right, bottom, left)
        // back to the shortest equivalent form, matching browsers' shorthand
        // serialization for inset() emission.
        static string CollapseSidesShorthand(string[] sides) {
            if (sides == null || sides.Length != 4) return "";
            bool t_eq_b = string.Equals(sides[0], sides[2], StringComparison.Ordinal);
            bool l_eq_r = string.Equals(sides[3], sides[1], StringComparison.Ordinal);
            bool t_eq_r = string.Equals(sides[0], sides[1], StringComparison.Ordinal);
            if (t_eq_b && l_eq_r && t_eq_r) return sides[0];
            if (t_eq_b && l_eq_r) return sides[0] + " " + sides[1];
            if (l_eq_r) return sides[0] + " " + sides[1] + " " + sides[2];
            return sides[0] + " " + sides[1] + " " + sides[2] + " " + sides[3];
        }

        // 1/2/3/4-value side shorthand → (top, right, bottom, left).
        static string[] ExpandSidesShorthand(List<string> tokens) {
            if (tokens == null || tokens.Count == 0 || tokens.Count > 4) return null;
            switch (tokens.Count) {
                case 1: return new[] { tokens[0], tokens[0], tokens[0], tokens[0] };
                case 2: return new[] { tokens[0], tokens[1], tokens[0], tokens[1] };
                case 3: return new[] { tokens[0], tokens[1], tokens[2], tokens[1] };
                case 4: return new[] { tokens[0], tokens[1], tokens[2], tokens[3] };
            }
            return null;
        }

        // --- shared helpers -------------------------------------------------------

        // Top-level comma split that honours parenthesised groups. Used for
        // shadow layer splits and clip-path polygon points.
        static List<string> SplitTopLevelCommas(string text) {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;
            int depth = 0;
            int start = 0;
            for (int i = 0; i < text.Length; i++) {
                char c = text[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0) {
                    result.Add(text.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            string tail = text.Substring(start).Trim();
            if (tail.Length > 0) result.Add(tail);
            return result;
        }

        // Whitespace split that honours parenthesised groups (so rgb(a,b,c)
        // stays a single token).
        static List<string> SplitWhitespace(string text) {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;
            int depth = 0;
            int start = -1;
            for (int i = 0; i < text.Length; i++) {
                char c = text[i];
                if (c == '(') { depth++; if (start < 0) start = i; continue; }
                if (c == ')') { depth--; continue; }
                if (char.IsWhiteSpace(c) && depth == 0) {
                    if (start >= 0) {
                        result.Add(text.Substring(start, i - start));
                        start = -1;
                    }
                } else {
                    if (start < 0) start = i;
                }
            }
            if (start >= 0) result.Add(text.Substring(start));
            return result;
        }

        static bool TryParseLengthToken(string tok, out double value, out CssLengthUnit unit) {
            value = 0; unit = CssLengthUnit.Px;
            if (string.IsNullOrWhiteSpace(tok)) return false;
            var l = TryParseLength(tok);
            if (l == null) {
                // Bare zero → 0px.
                if (TryParseNumber(tok, out double n) && Math.Abs(n) < 1e-9) {
                    value = 0; unit = CssLengthUnit.Px; return true;
                }
                return false;
            }
            value = l.Value; unit = l.Unit;
            return true;
        }

        // D6 — Per-token color probe used by the shadow tokenizer
        // (TryParseSingleShadow at line 230). The shadow component runs this
        // against EVERY whitespace-separated token (lengths, the `inset`
        // keyword, and colors); `CssValueParser.Parse` throws
        // CssValueParseException for any non-color non-length token, which
        // this wrapper swallows so the tokenizer can fall through to the
        // length / keyword branches. That parse-exception tolerance is the
        // live reason the wrapper exists — it is NOT an "identifier ->
        // named color" fallback. The original audit hypothesis (parser
        // returns CssIdentifier for color names "in some legacy paths") no
        // longer holds: as of CssValueParser.cs:80-82 and :1364-1369, every
        // named-color identifier path constructs a CssColor directly via
        // `CssColor.TryFromName`. The previously-defensive
        // `v is CssIdentifier id && CssColor.TryFromName(id.Name, ...)`
        // clause was therefore unreachable and has been removed.
        static bool TryParseColorToken(string tok, out CssColor color) {
            color = null;
            if (string.IsNullOrWhiteSpace(tok)) return false;
            try {
                var v = CssValueParser.Parse(tok);
                if (v is CssColor c) { color = c; return true; }
            } catch (CssValueParseException) {
                // EC3: tightened from bare `catch { }` to the documented parser
                // failure type. By-design fallback (callers fall through to
                // discrete-step). Programmer errors (NullReferenceException
                // etc) now surface instead of being silently swallowed.
            }
            return false;
        }

        // Lerp two CssLength values that share, or can be unified into px.
        // Returns null when the inputs can't be parsed as length/percentage.
        static string InterpolateLengthOrPercent(string a, string b, double t, LengthContext ctx) {
            if (a == null || b == null) return null;
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
            return null;
        }

        // Same-unit length lerp helper used by shadow components, returning
        // text into a StringBuilder to keep allocation low.
        static void AppendLengthLerp(StringBuilder sb, double av, CssLengthUnit au, double bv, CssLengthUnit bu, double t, LengthContext ctx) {
            if (au == bu) {
                double v = av + (bv - av) * t;
                sb.Append(Format(v));
                sb.Append(UnitSuffix(au));
                return;
            }
            // Cross-unit shadow lerp: resolve both endpoints to px. Currently
            // shadows only ever store px in the parser (rem/em pass through
            // TryParseLength as themselves), so this branch typically just
            // emits a px-converted result.
            try {
                var la = new CssLength(av, au, null);
                var lb = new CssLength(bv, bu, null);
                double pa = la.ToPixels(ctx);
                double pb = lb.ToPixels(ctx);
                double v = pa + (pb - pa) * t;
                sb.Append(Format(v));
                sb.Append("px");
            } catch (InvalidOperationException) {
                double v = av + (bv - av) * t;
                sb.Append(Format(v));
                sb.Append(UnitSuffix(au));
            }
        }

        // Oklab color lerp for shadow components — same approach as
        // InterpolateColorTyped but returns a formatted string directly.
        static string LerpColorOklab(CssColor a, CssColor b, double t) {
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

    }
}
