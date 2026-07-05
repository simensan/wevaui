using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Css.Values;

namespace Weva.Layout {
    internal static class StyleResolver {
        // CSS Values L4 §6.2 — "normal" line-height resolves to a UA-chosen
        // value, conventionally ~1.2 of the font-size. We expose the factor
        // here so every fallback (font-metrics missing / IMGUI without a
        // bound font / paint-time vertical centering / ellipsis row height)
        // agrees on a single constant. Do NOT change without auditing all
        // call sites — many golden / parity tests pin against 1.2x.
        public const double DefaultLineHeightFactor = 1.2;

        // CSS Fonts L3 §3.5 — absolute-size keyword multipliers, relative
        // to the parent's computed font-size. The spec defines these as
        // implementation-defined within recommended ratios; the values
        // below are the de-facto browser defaults (Blink / Gecko / WebKit
        // converge on these for body-sized text).
        public const double AbsoluteFontSize_XXSmall = 0.6;
        public const double AbsoluteFontSize_XSmall = 0.75;
        public const double AbsoluteFontSize_Small = 0.85;
        public const double AbsoluteFontSize_Medium = 1.0;
        public const double AbsoluteFontSize_Large = 1.2;
        public const double AbsoluteFontSize_XLarge = 1.5;
        public const double AbsoluteFontSize_XXLarge = 2.0;

        // CSS Fonts L3 §3.7 — relative-size keywords. Spec-distinct from
        // the absolute-size table above: `smaller` / `larger` compute via
        // their own UA-defined derivation (typically one "step" on the
        // absolute-size scale relative to the parent). The current values
        // happen to numerically coincide with `small` / `large` from the
        // absolute table, but that coincidence is NOT spec-guaranteed.
        // Keep these constants separate so a future tweak to either table
        // does not silently drift the other.
        public const double RelativeFontSize_Smaller = 0.85;
        public const double RelativeFontSize_Larger = 1.2;

        public enum LengthKind {
            Length,
            Auto,
            Percent,
            None,
            // CSS Sizing L3 §5.1: fit-content(<length-percentage>). Pixels
            // carries the resolved argument value; callers must probe
            // min-content / max-content and clamp:
            //   used = min(max-content, max(min-content, Pixels))
            FitContent
        }

        public struct ResolvedLength {
            public LengthKind Kind;
            public double Pixels;
            public double Percent;

            public static ResolvedLength Auto() => new ResolvedLength { Kind = LengthKind.Auto };
            public static ResolvedLength None() => new ResolvedLength { Kind = LengthKind.None };
            public static ResolvedLength Pixel(double px) => new ResolvedLength { Kind = LengthKind.Length, Pixels = px };
            public static ResolvedLength PercentOf(double pct) => new ResolvedLength { Kind = LengthKind.Percent, Percent = pct };
            // CSS Sizing L3 §5.1: fit-content(<length-percentage>) — the
            // argument is pre-resolved to pixels (percentage resolved against
            // containingBlockWidth at parse time). Callers that can probe
            // intrinsic sizes apply the clamp; callers that cannot treat it
            // as Auto (safe degradation).
            public static ResolvedLength FitContentArg(double argPx) => new ResolvedLength { Kind = LengthKind.FitContent, Pixels = argPx };
            // Unparseable / unsupported input collapses to Auto. Every caller
            // already treated the former dedicated `Invalid` kind as `Auto`, so
            // the variant carried no extra signal. Kept as a factory so call
            // sites that mean "couldn't parse" still read clearly.
            public static ResolvedLength Invalid() => new ResolvedLength { Kind = LengthKind.Auto };
        }

        public static double FontSizePx(ComputedStyle style, ComputedStyle parentStyle, LayoutContext ctx) {
            double parentFs = parentStyle != null ? FontSizePx(parentStyle, null, ctx) : ctx.RootFontSizePx;
            var parsed = style?.GetParsed(CssProperties.FontSizeId);
            if (TryResolveFontSizeFromParsed(parsed, parentFs, ctx, out double parsedPx)) return parsedPx;

            string raw = style?.Get(CssProperties.FontSizeId);
            if (string.IsNullOrEmpty(raw) || raw == "medium") return parentFs > 0 ? parentFs : ctx.RootFontSizePx;
            if (raw == "small") return parentFs * AbsoluteFontSize_Small;
            if (raw == "large") return parentFs * AbsoluteFontSize_Large;
            if (raw == "x-small") return parentFs * AbsoluteFontSize_XSmall;
            if (raw == "x-large") return parentFs * AbsoluteFontSize_XLarge;
            if (raw == "xx-small") return parentFs * AbsoluteFontSize_XXSmall;
            if (raw == "xx-large") return parentFs * AbsoluteFontSize_XXLarge;
            if (raw == "smaller") return parentFs * RelativeFontSize_Smaller;
            if (raw == "larger") return parentFs * RelativeFontSize_Larger;
            if (CssValue.TryParse(raw, out var v)) {
                if (v is CssLength l) {
                    var lc = ctx.ToLengthContext(parentFs, parentFs);
                    return l.ToPixels(lc);
                }
                if (v is CssPercentage p) {
                    return parentFs * p.Value * 0.01;
                }
                if (v is CssNumber n) {
                    return n.Value;
                }
                // CSS Values L4 §10: math functions (calc / min / max / clamp)
                // resolve to a length when their inputs do. Without this branch,
                // `font-size: clamp(12px, 1.5vmin, 14px)` fell through to the
                // inherited / root fallback, silently dropping the author
                // value — which broke vmin/clamp scaling across the demo.
                if (v is CssCalc c) {
                    try {
                        var lc = ctx.ToLengthContext(parentFs, parentFs);
                        return c.Evaluate(lc);
                    } catch {
                        // Fall through to parent-size fallback below.
                    }
                }
            }
            return parentFs > 0 ? parentFs : ctx.RootFontSizePx;
        }

        public static double LineHeightPx(ComputedStyle style, double fontSize, LayoutContext ctx, Text.IFontMetrics fm)
            => LineHeightPx(style, fontSize, ctx, fm, null, Paint.FontStyle.Normal, 0);

        // PAINT-1: weight/style-aware overload. When `line-height: normal` falls
        // through to the font's metric line-height, route through
        // IStyledFontMetrics.LineHeight(fs, family, style, weight) if available
        // so a bold span uses the bold face's metric line-height, not the
        // default-weight face's. The interface fallback (unstyled
        // metrics.LineHeight) is preserved for legacy callers and stub metrics.
        public static double LineHeightPx(ComputedStyle style, double fontSize, LayoutContext ctx, Text.IFontMetrics fm,
                                          string family, Paint.FontStyle fontStyle, int weight) {
            var parsed = style?.GetParsed(CssProperties.LineHeightId);
            if (TryResolveLineHeightFromParsed(parsed, fontSize, ctx, out double parsedPx)) return parsedPx;

            string raw = style?.Get(CssProperties.LineHeightId);
            if (string.IsNullOrEmpty(raw) || raw == "normal") return DefaultLineHeight(fontSize, fm, family, fontStyle, weight);
            if (CssValue.TryParse(raw, out var v)) {
                if (v is CssLength l) {
                    var lc = ctx.ToLengthContext(fontSize, fontSize);
                    return l.ToPixels(lc);
                }
                if (v is CssPercentage p) {
                    return fontSize * p.Value * 0.01;
                }
                if (v is CssNumber n) {
                    return fontSize * n.Value;
                }
                // Mirror FontSizePx — math functions evaluate as lengths.
                if (v is CssCalc c) {
                    try {
                        var lc = ctx.ToLengthContext(fontSize, fontSize);
                        return c.Evaluate(lc);
                    } catch {
                        // Fall through to font-derived fallback below.
                    }
                }
            }
            return DefaultLineHeight(fontSize, fm);
        }

        static bool TryResolveFontSizeFromParsed(CssValue parsed, double parentFs, LayoutContext ctx, out double px) {
            px = 0;
            if (parsed == null) return false;
            if (parsed is CssKeyword k) return TryResolveFontSizeKeyword(k.Identifier, parentFs, out px);
            if (parsed is CssIdentifier id) return TryResolveFontSizeKeyword(id.Name, parentFs, out px);
            if (parsed is CssLength l) {
                var lc = ctx.ToLengthContext(parentFs, parentFs);
                px = l.ToPixels(lc);
                return true;
            }
            if (parsed is CssPercentage p) {
                px = parentFs * p.Value * 0.01;
                return true;
            }
            if (parsed is CssNumber n) {
                px = n.Value;
                return true;
            }
            if (parsed is CssCalc c) {
                try {
                    var lc = ctx.ToLengthContext(parentFs, parentFs);
                    px = c.Evaluate(lc);
                    return true;
                } catch {
                    return false;
                }
            }
            return false;
        }

        static bool TryResolveFontSizeKeyword(string raw, double parentFs, out double px) {
            px = 0;
            if (string.IsNullOrEmpty(raw) || raw == "medium") {
                px = parentFs;
                return true;
            }
            string s = CssStringUtil.ToLowerInvariantOrSame(raw);
            switch (s) {
                case "medium": px = parentFs; return true;
                case "small": px = parentFs * AbsoluteFontSize_Small; return true;
                case "large": px = parentFs * AbsoluteFontSize_Large; return true;
                case "x-small": px = parentFs * AbsoluteFontSize_XSmall; return true;
                case "x-large": px = parentFs * AbsoluteFontSize_XLarge; return true;
                case "xx-small": px = parentFs * AbsoluteFontSize_XXSmall; return true;
                case "xx-large": px = parentFs * AbsoluteFontSize_XXLarge; return true;
                case "smaller": px = parentFs * RelativeFontSize_Smaller; return true;
                case "larger": px = parentFs * RelativeFontSize_Larger; return true;
                default: return false;
            }
        }

        static bool TryResolveLineHeightFromParsed(CssValue parsed, double fontSize, LayoutContext ctx, out double px) {
            px = 0;
            if (parsed == null) return false;
            if (parsed is CssKeyword k) return false;
            if (parsed is CssIdentifier id) return false;
            if (parsed is CssLength l) {
                var lc = ctx.ToLengthContext(fontSize, fontSize);
                px = l.ToPixels(lc);
                return true;
            }
            if (parsed is CssPercentage p) {
                px = fontSize * p.Value * 0.01;
                return true;
            }
            if (parsed is CssNumber n) {
                px = fontSize * n.Value;
                return true;
            }
            if (parsed is CssCalc c) {
                try {
                    var lc = ctx.ToLengthContext(fontSize, fontSize);
                    px = c.Evaluate(lc);
                    return true;
                } catch {
                    return false;
                }
            }
            return false;
        }

        static double DefaultLineHeight(double fontSize, Text.IFontMetrics fm) {
            return fm != null ? fm.LineHeight(fontSize) : fontSize * DefaultLineHeightFactor;
        }

        static double DefaultLineHeight(double fontSize, Text.IFontMetrics fm, string family, Paint.FontStyle style, int weight) {
            if (fm == null) return fontSize * DefaultLineHeightFactor;
            // PAINT-1: when a weight/style-aware metrics impl is available,
            // route through it so line-height: normal picks up the actual
            // face that paint will render with. Falls back to the unstyled
            // overload for simple/stub IFontMetrics implementations.
            if (weight > 0 && fm is Text.IStyledFontMetrics styled)
                return styled.LineHeight(fontSize, family, style, weight);
            return fm.LineHeight(fontSize);
        }

        public static ResolvedLength ResolveLength(string raw, ComputedStyle style, LayoutContext ctx, double fontSize, double? basisPx) {
            if (string.IsNullOrEmpty(raw)) return ResolvedLength.Auto();
            if (raw == "auto") return ResolvedLength.Auto();
            if (raw == "none") return ResolvedLength.None();
            // CSS Sizing L3 §5: intrinsic sizing keywords. v1 doesn't compute
            // intrinsic content sizes outside of inline-block shrink-to-fit, so
            // we map these to `auto` (matches the existing FlexItemProperties
            // behaviour for flex-basis). Doing it here keeps parsing out of the
            // slow CssValue path. The `fit-content(<length-percentage>)` function
            // form is handled by ResolveLengthFromParsed (CssFunctionCall branch)
            // via the CssValue.TryParse fall-through below.
            if (raw == "min-content" || raw == "max-content" || raw == "fit-content") {
                return ResolvedLength.Auto();
            }
            if (CssValue.TryParse(raw, out var v)) {
                return ResolveLengthFromParsed(v, ctx, fontSize, basisPx);
            }
            return ResolvedLength.Invalid();
        }

        // Direct-parsed-value entry point. Mirrors ResolveLength(string,...)
        // but skips the string→CssValue round-trip when callers already hold
        // the parse tree (e.g. via ComputedStyle.GetParsed). Same kind
        // semantics: a CssKeyword/CssIdentifier with name "auto" → Auto,
        // "none" → None, intrinsic-sizing keywords → Auto.
        public static ResolvedLength ResolveLengthFromParsed(CssValue parsed, LayoutContext ctx, double fontSize, double? basisPx) {
            return ResolveLengthFromParsed(parsed, ctx, fontSize, basisPx, 0);
        }

        // H5b overload: callers that have resolved the element's line-height
        // pass it through so `lh`-typed lengths (`padding: 1lh`, `width: 2lh`,
        // etc.) resolve against the cascaded line-height instead of the
        // CssLength.ToPixels fontSize * 1.2 fallback. `lineHeightPx == 0`
        // keeps the legacy fallback intact (defensive for synthetic / test
        // contexts that never resolved line-height).
        public static ResolvedLength ResolveLengthFromParsed(CssValue parsed, LayoutContext ctx, double fontSize, double? basisPx, double lineHeightPx) {
            if (parsed == null) return ResolvedLength.Auto();
            // Keyword fast path — the parser canonicalises identifiers to
            // CssKeyword for known names (auto/none/medium/...) and CssIdentifier
            // for everything else. Both surface here and we accept either.
            if (parsed is CssKeyword k) {
                string ki = k.Identifier;
                if (ki == "auto") return ResolvedLength.Auto();
                if (ki == "none") return ResolvedLength.None();
                if (ki == "min-content" || ki == "max-content" || ki == "fit-content") {
                    return ResolvedLength.Auto();
                }
                return ResolvedLength.Invalid();
            }
            if (parsed is CssIdentifier id) {
                string n = id.Name;
                if (n == "auto") return ResolvedLength.Auto();
                if (n == "none") return ResolvedLength.None();
                if (n == "min-content" || n == "max-content" || n == "fit-content") {
                    return ResolvedLength.Auto();
                }
                return ResolvedLength.Invalid();
            }
            if (parsed is CssLength l) {
                // Percentage-typed CssLength (unit = %) requires a basis;
                // surface as Percent so callers without one keep their
                // pre-migration fallback (same as ResolveLength(string,...)).
                if (l.Unit == CssLengthUnit.Percent) {
                    if (basisPx.HasValue) return ResolvedLength.Pixel(l.Value * 0.01 * basisPx.Value);
                    return ResolvedLength.PercentOf(l.Value);
                }
                var lc = ctx.ToLengthContext(fontSize, basisPx, lineHeightPx);
                return ResolvedLength.Pixel(l.ToPixels(lc));
            }
            if (parsed is CssPercentage p) {
                if (basisPx.HasValue) return ResolvedLength.Pixel(p.Value * 0.01 * basisPx.Value);
                return ResolvedLength.PercentOf(p.Value);
            }
            if (parsed is CssNumber num) {
                return ResolvedLength.Pixel(num.Value);
            }
            if (parsed is CssCalc c) {
                try {
                    var lc = ctx.ToLengthContext(fontSize, basisPx, lineHeightPx);
                    return ResolvedLength.Pixel(c.Evaluate(lc));
                } catch {
                    return ResolvedLength.Invalid();
                }
            }
            // CSS Sizing L3 §5.1: fit-content(<length-percentage>) function form.
            // The CssValueParser emits this as a CssFunctionCall named "fit-content"
            // with one argument (a CssLength or CssPercentage). Resolve the argument
            // to a definite pixel value now; the caller (BlockLayout.ApplyBoxModel /
            // InlineLayout.MakeAtomItem) is responsible for probing min-content /
            // max-content and computing min(max-content, max(min-content, argPx)).
            if (parsed is CssFunctionCall fn && fn.Name == "fit-content" && fn.Arguments.Count == 1) {
                var arg = fn.Arguments[0];
                double argPx;
                if (arg is CssLength al) {
                    if (al.Unit == CssLengthUnit.Percent) {
                        argPx = basisPx.HasValue ? al.Value * 0.01 * basisPx.Value : 0;
                    } else {
                        var lc2 = ctx.ToLengthContext(fontSize, basisPx, lineHeightPx);
                        argPx = al.ToPixels(lc2);
                    }
                } else if (arg is CssPercentage ap) {
                    argPx = basisPx.HasValue ? ap.Value * 0.01 * basisPx.Value : 0;
                } else if (arg is CssNumber an) {
                    argPx = an.Value;
                } else if (arg is CssCalc ac) {
                    try {
                        var lc2 = ctx.ToLengthContext(fontSize, basisPx, lineHeightPx);
                        argPx = ac.Evaluate(lc2);
                    } catch {
                        return ResolvedLength.Auto();
                    }
                } else {
                    return ResolvedLength.Auto();
                }
                if (argPx < 0) argPx = 0;
                return ResolvedLength.FitContentArg(argPx);
            }
            return ResolvedLength.Invalid();
        }

        public static double ResolveLengthPx(string raw, double fallback, ComputedStyle style, LayoutContext ctx, double fontSize, double? basisPx) {
            var r = ResolveLength(raw, style, ctx, fontSize, basisPx);
            switch (r.Kind) {
                case LengthKind.Length: return r.Pixels;
                case LengthKind.Auto:
                case LengthKind.None:
                    return fallback;
                case LengthKind.Percent:
                    return fallback;
            }
            return fallback;
        }

        public static double ResolveBorderWidth(string raw, double fontSize, LayoutContext ctx) {
            if (string.IsNullOrEmpty(raw)) return 0;
            if (raw == "thin") return 1;
            if (raw == "medium") return 3;
            if (raw == "thick") return 5;
            if (CssValue.TryParse(raw, out var v)) {
                return ResolveBorderWidth(v, fontSize, ctx);
            }
            return 0;
        }

        public static double ResolveBorderWidth(CssValue parsed, double fontSize, LayoutContext ctx) {
            if (parsed == null) return 0;
            if (parsed is CssKeyword k) return ResolveBorderWidthKeyword(k.Identifier);
            if (parsed is CssIdentifier id) return ResolveBorderWidthKeyword(id.Name);
            if (parsed is CssLength l) {
                var lc = ctx.ToLengthContext(fontSize, 0);
                return l.ToPixels(lc);
            }
            if (parsed is CssNumber n) return n.Value;
            if (parsed is CssCalc c) {
                try {
                    var lc = ctx.ToLengthContext(fontSize, 0);
                    return c.Evaluate(lc);
                } catch {
                    return 0;
                }
            }
            return 0;
        }

        static double ResolveBorderWidthKeyword(string raw) {
            if (string.IsNullOrEmpty(raw)) return 0;
            string s = CssStringUtil.ToLowerInvariantOrSame(raw);
            if (s == "thin") return 1;
            if (s == "medium") return 3;
            if (s == "thick") return 5;
            return 0;
        }

        // Parses the aspect-ratio CSS property (CSS Sizing L4 §5).
        // Returns true with `ratio` = numerator / denominator (>0) when the value is
        // a valid <number> or <number> / <number>. Returns false for "auto", empty,
        // or zero/negative ratios. v1 simplification: ignores the "auto <ratio>"
        // second form — when present we use the explicit ratio.
        public static bool TryResolveAspectRatio(ComputedStyle style, out double ratio) {
            ratio = 0;
            if (style == null) return false;
            // Hot path: consult the per-style parsed cache directly. For the
            // common numeric forms (`16 / 9`, `1.618`, `auto`) the parser
            // produces CssNumber / CssCalc / CssRatio / CssKeyword directly,
            // and we resolve without ever touching the raw string. The
            // "<ratio> auto" / "auto <ratio>" shorthand still falls through
            // to CssRatio.TryParse on the raw string — Css.TryParse exposes
            // it as a CssValueList(space, [keyword auto, ratio]) which we
            // could decode here, but the spec's intent is the explicit
            // ratio takes precedence so the raw-string fallback is fine.
            var parsed = style.GetParsed(CssProperties.AspectRatioId);
            if (parsed is CssKeyword k && k.Identifier == "auto") return false;
            if (parsed is CssIdentifier id && id.Name == "auto") return false;
            if (parsed is CssRatio r && r.IsValid) {
                ratio = r.Value;
                return ratio > 0;
            }
            if (parsed is CssNumber n && n.Value > 0) {
                ratio = n.Value;
                return true;
            }
            if (parsed is CssCalc c) {
                try {
                    double v = c.Evaluate(default);
                    if (v > 0) { ratio = v; return true; }
                } catch {
                    // Fall through to the raw-string path below — calc that
                    // needs a context (em/%/...) is invalid for aspect-ratio
                    // anyway per CSS Sizing L4 §5, but we keep the legacy
                    // behaviour for any author who relied on it.
                }
            }
            // Raw-string fallback for the `<ratio> auto` / `auto <ratio>` shorthand
            // and for any value the parser didn't recognise as one of the typed
            // forms above. Matches the v1 behaviour pre-migration.
            string raw = style.Get(CssProperties.AspectRatioId);
            if (string.IsNullOrEmpty(raw) || raw == "auto") return false;
            string trimmed = raw.Trim();
            if (trimmed.StartsWith("auto ", System.StringComparison.OrdinalIgnoreCase)) {
                trimmed = trimmed.Substring(5).Trim();
            } else if (trimmed.EndsWith(" auto", System.StringComparison.OrdinalIgnoreCase)) {
                trimmed = trimmed.Substring(0, trimmed.Length - 5).Trim();
            }
            if (CssRatio.TryParse(trimmed, out var r2) && r2.IsValid) {
                ratio = r2.Value;
                return ratio > 0;
            }
            return false;
        }

        public static string Display(ComputedStyle style) {
            string d = style?.Get(CssProperties.DisplayId);
            if (string.IsNullOrEmpty(d)) return "inline";
            // ToLowerInvariantOrSame returns `d` itself when no uppercase
            // chars are present — eliminates the per-call string allocation
            // for the dominant case where authors and the cascade store the
            // already-canonical lowercase form.
            return CssStringUtil.ToLowerInvariantOrSame(d);
        }

        public static string WhiteSpace(ComputedStyle style) {
            string ws = style?.Get(CssProperties.WhiteSpaceId);
            if (string.IsNullOrEmpty(ws)) return "normal";
            ws = CssStringUtil.ToLowerInvariantOrSame(ws);
            string wrap = style?.Get(CssProperties.TextWrapId);
            if (!string.IsNullOrEmpty(wrap)
                && CssStringUtil.EqualsIgnoreCaseTrimmed(wrap, "nowrap")
                && ws == "normal") {
                return "nowrap";
            }
            return ws;
        }

        public static string TextAlign(ComputedStyle style) {
            string t = style?.Get(CssProperties.TextAlignId);
            if (string.IsNullOrEmpty(t)) t = "start";
            t = CssStringUtil.ToLowerInvariantOrSame(t);
            if (t == "start") return IsRtl(style) ? "right" : "left";
            if (t == "end") return IsRtl(style) ? "left" : "right";
            return t;
        }

        public static string TextAlignLast(ComputedStyle style, string resolvedTextAlign) {
            string t = style?.Get(CssProperties.TextAlignLastId);
            if (string.IsNullOrEmpty(t) || CssStringUtil.EqualsIgnoreCaseTrimmed(t, "auto")) {
                return resolvedTextAlign == "justify" ? (IsRtl(style) ? "right" : "left") : resolvedTextAlign;
            }
            t = CssStringUtil.ToLowerInvariantOrSame(t.Trim());
            if (t == "start") return IsRtl(style) ? "right" : "left";
            if (t == "end") return IsRtl(style) ? "left" : "right";
            return t;
        }

        // CSS Text L3 §7.3: `text-justify` controls how lines are spread when
        // `text-align:justify` is active. Inherited; initial value `auto`.
        // Returns the normalised keyword: "auto", "inter-word", "inter-character", or "none".
        // `auto` and `inter-word` are treated identically by the engine (inter-word spreading).
        public static string TextJustify(ComputedStyle style) {
            string t = style?.Get(CssProperties.TextJustifyId);
            if (string.IsNullOrEmpty(t)) return "auto";
            t = CssStringUtil.ToLowerInvariantOrSame(t.Trim());
            // Normalise synonyms: empty / unrecognised → "auto".
            if (t == "auto" || t == "inter-word" || t == "inter-character" || t == "none")
                return t;
            return "auto";
        }

        public static double TextIndentPx(ComputedStyle style, ComputedStyle parentStyle, LayoutContext ctx, double containingBlockWidth) {
            if (style == null || ctx == null) return 0;
            double fs = FontSizePx(style, parentStyle, ctx);
            var parsed = style.GetParsed(CssProperties.TextIndentId);
            // CSS Text L3 §7.1: text-indent accepts trailing `hanging` and/or
            // `each-line` keyword modifiers. v1 doesn't implement either
            // semantic, but we must still honour the leading <length>|<percentage>
            // rather than dropping the rule. Strip the keywords to the indent
            // term; any other trailing token leaves the value invalid (→ 0).
            if (parsed is CssValueList list && list.Separator == CssValueListSeparator.Space) {
                CssValue indentTerm = null;
                for (int i = 0; i < list.Items.Count; i++) {
                    var item = list.Items[i];
                    if (item is CssIdentifier ident) {
                        string n = CssStringUtil.ToLowerInvariantOrSame(ident.Name);
                        if (n == "hanging" || n == "each-line") continue;
                        indentTerm = null;
                        break;
                    }
                    if (indentTerm != null) { indentTerm = null; break; }
                    indentTerm = item;
                }
                if (indentTerm != null) parsed = indentTerm;
            }
            var resolved = ResolveLengthFromParsed(parsed, ctx, fs, containingBlockWidth);
            if (resolved.Kind == LengthKind.Length) return resolved.Pixels;
            if (resolved.Kind == LengthKind.Percent) return containingBlockWidth * resolved.Percent * 0.01;
            return 0;
        }

        public static double TabSizeSpaces(ComputedStyle style, Text.IFontMetrics fm = null, double fontSizePx = 0, LengthContext lengthCtx = default) {
            if (style == null) return 8;
            var parsed = style.GetParsed(CssProperties.TabSizeId);
            if (parsed is CssNumber n && n.Value > 0) return n.Value;
            if (parsed is CssLength l && l.Value > 0) {
                if (l.Unit != CssLengthUnit.Percent && fm != null && fontSizePx > 0) {
                    double px = l.ToPixels(lengthCtx);
                    double spaceW = fm.Measure(" ", fontSizePx);
                    if (spaceW > 0 && px > 0) return px / spaceW;
                }
                return l.Value;
            }
            string raw = style.Get(CssProperties.TabSizeId);
            if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) && v > 0) {
                return v;
            }
            return 8;
        }

        public static bool IsRtl(ComputedStyle style) {
            string raw = style?.Get(CssProperties.DirectionId);
            return !string.IsNullOrEmpty(raw) && CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "rtl");
        }

        // Returns a 4-tuple (top, right, bottom, left) for a shorthand box property whose
        // value is either explicitly set (e.g. "padding: 10px 20px") or derived per-side.
        // Longhand values (padding-top, padding-right, ...) take precedence; the shorthand
        // is consulted only as a fallback to the initial value.
        //
        // The string overload exists for back-compat tests; the hot layout path uses the
        // int-id overload below which avoids the per-call string concat.
        public static (string top, string right, string bottom, string left) BoxSides(ComputedStyle style, string shorthand) {
            int shId = CssProperties.GetId(shorthand);
            int topId, rightId, bottomId, leftId;
            if (shId == CssProperties.PaddingId) {
                topId = CssProperties.PaddingTopId; rightId = CssProperties.PaddingRightId;
                bottomId = CssProperties.PaddingBottomId; leftId = CssProperties.PaddingLeftId;
            } else if (shId == CssProperties.MarginId) {
                topId = CssProperties.MarginTopId; rightId = CssProperties.MarginRightId;
                bottomId = CssProperties.MarginBottomId; leftId = CssProperties.MarginLeftId;
            } else {
                // Fallback to the original string-keyed path for any other shorthand
                // that callers might pass — only padding/margin go through the hot
                // layout path so the slow path here is acceptable.
                return BoxSidesByName(style, shorthand);
            }
            return BoxSides(style, shId, topId, rightId, bottomId, leftId);
        }

        public static (string top, string right, string bottom, string left) BoxSides(
            ComputedStyle style, int shorthandId, int topId, int rightId, int bottomId, int leftId) {
            string top = style?.Get(topId);
            string right = style?.Get(rightId);
            string bottom = style?.Get(bottomId);
            string left = style?.Get(leftId);

            string initial = "0";
            bool topIsInitial = string.IsNullOrEmpty(top) || top == initial;
            bool rightIsInitial = string.IsNullOrEmpty(right) || right == initial;
            bool bottomIsInitial = string.IsNullOrEmpty(bottom) || bottom == initial;
            bool leftIsInitial = string.IsNullOrEmpty(left) || left == initial;

            if (topIsInitial && rightIsInitial && bottomIsInitial && leftIsInitial) {
                string sh = style?.Get(shorthandId);
                if (!string.IsNullOrEmpty(sh) && sh != "0") {
                    var parts = SplitTopLevelTokens(sh);
                    if (parts.Count == 1) return (parts[0], parts[0], parts[0], parts[0]);
                    if (parts.Count == 2) return (parts[0], parts[1], parts[0], parts[1]);
                    if (parts.Count == 3) return (parts[0], parts[1], parts[2], parts[1]);
                    if (parts.Count == 4) return (parts[0], parts[1], parts[2], parts[3]);
                }
            }
            return (top ?? "0", right ?? "0", bottom ?? "0", left ?? "0");
        }

        static (string top, string right, string bottom, string left) BoxSidesByName(ComputedStyle style, string shorthand) {
            string top = style?.Get(shorthand + "-top");
            string right = style?.Get(shorthand + "-right");
            string bottom = style?.Get(shorthand + "-bottom");
            string left = style?.Get(shorthand + "-left");

            string initial = "0";
            bool topIsInitial = string.IsNullOrEmpty(top) || top == initial;
            bool rightIsInitial = string.IsNullOrEmpty(right) || right == initial;
            bool bottomIsInitial = string.IsNullOrEmpty(bottom) || bottom == initial;
            bool leftIsInitial = string.IsNullOrEmpty(left) || left == initial;

            if (topIsInitial && rightIsInitial && bottomIsInitial && leftIsInitial) {
                string sh = style?.Get(shorthand);
                if (!string.IsNullOrEmpty(sh) && sh != "0") {
                    var parts = SplitTopLevelTokens(sh);
                    if (parts.Count == 1) return (parts[0], parts[0], parts[0], parts[0]);
                    if (parts.Count == 2) return (parts[0], parts[1], parts[0], parts[1]);
                    if (parts.Count == 3) return (parts[0], parts[1], parts[2], parts[1]);
                    if (parts.Count == 4) return (parts[0], parts[1], parts[2], parts[3]);
                }
            }
            return (top ?? "0", right ?? "0", bottom ?? "0", left ?? "0");
        }

        static List<string> SplitTopLevelTokens(string s) {
            var list = new List<string>();
            int depth = 0;
            int start = 0;
            for (int i = 0; i < s.Length; i++) {
                char c = s[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (depth == 0 && (c == ' ' || c == '\t')) {
                    if (i > start) list.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            if (start < s.Length) list.Add(s.Substring(start));
            list.RemoveAll(string.IsNullOrEmpty);
            return list;
        }
    }
}
