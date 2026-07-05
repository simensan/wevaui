using System.Collections.Generic;
using System.Globalization;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Layout.Boxes;

namespace Weva.Paint.Conversion {
    internal static class TextRunResolver {
        // Pool-aware overload with a pre-resolved size. When `preResolvedSize >= 0`,
        // the layout-time computed font-size is used verbatim and the raw style
        // string is NOT re-resolved against `ctx`. That matters because LengthContextFor
        // sets ctx.BaseFontSizePx to THIS box's resolved fs — so re-resolving an
        // em-relative `font-size` value against it would compound the relative
        // factor twice (a `0.55em` then resolves to 0.55*0.55*parent ≈ 0.3*parent
        // instead of 0.55*parent). Threading the layout value through avoids the
        // round-trip entirely.
        //
        // `string.Trim()` on a non-empty font-family raw can allocate (only when
        // leading/trailing whitespace is present); the cache memoizes the result
        // so that hot-path text runs reuse the same FontHandle value rather than
        // re-trimming + re-resolving each call.
        public static FontHandle BuildFont(ComputedStyle style, LengthContext ctx, Dictionary<FontKey, FontHandle> cache, double preResolvedSize) {
            string family = "sans-serif";
            double size = preResolvedSize >= 0
                ? preResolvedSize
                : (ctx.BaseFontSizePx > 0 ? ctx.BaseFontSizePx : 16);
            int weight = 400;
            FontStyle fontStyle = FontStyle.Normal;
            if (style != null) {
                // font-family: the parsed cache gives us the typed value
                // (CssIdentifier for `sans-serif`, CssString for `"My Font"`,
                // CssValueList for fallback chains). For our v1 single-family
                // model we prefer the typed value's canonical text; fall back
                // to the raw string only when the parse tree isn't useful
                // (e.g., a multi-token list whose canonical Raw already does
                // the right thing).
                var familyParsed = style.GetParsed(CssProperties.FontFamilyId);
                if (familyParsed is CssIdentifier famId && famId.Name.Length > 0) {
                    family = famId.Name;
                } else if (familyParsed is CssString famStr && famStr.Value.Length > 0) {
                    family = famStr.Value;
                } else if (familyParsed is CssKeyword famKw && famKw.Identifier.Length > 0) {
                    family = famKw.Identifier;
                } else {
                    // Fallback: list, calc, or unparseable — use the raw
                    // string so quoted multi-word families (`"Times New
                    // Roman", serif`) still resolve to their author text.
                    string famRaw = style.Get(CssProperties.FontFamilyId);
                    if (!string.IsNullOrEmpty(famRaw)) family = famRaw.Trim();
                }
                if (preResolvedSize < 0) {
                    var sizeParsed = style.GetParsed(CssProperties.FontSizeId);
                    if (sizeParsed != null) {
                        size = ResolveFontSizeParsed(sizeParsed, ctx, size);
                    } else {
                        string sizeRaw = style.Get(CssProperties.FontSizeId);
                        if (!string.IsNullOrEmpty(sizeRaw)) size = ResolveFontSize(sizeRaw, ctx, size);
                    }
                }
                weight = ResolveFontWeight(style, weight);
                fontStyle = ResolveFontStyle(style);
            }
            if (cache != null) {
                var key = new FontKey(family, size, weight, fontStyle);
                if (cache.TryGetValue(key, out var hit)) return hit;
                var fh = new FontHandle(family, size, weight, fontStyle);
                cache[key] = fh;
                return fh;
            }
            return new FontHandle(family, size, weight, fontStyle);
        }

        // Walks the parsed `font-variation-settings` / `font-optical-sizing`
        // properties into a list of (tag, value) axis tuples. v1 plumbing:
        // the returned list isn't yet handed to the FontAsset (that step
        // requires reflection against UnityEngine.TextCore.Text.FontAsset
        // — same trick as AtgGlyphAtlasAdapter — and a FontHandle/FontKey
        // axis extension so the FontCache distinguishes variant instances).
        // For now this method exists so authors can write the CSS today and
        // the parser path is reachable from BuildFont's surface; downstream
        // wiring is the follow-up.
        public static IReadOnlyList<FontAxis> ResolveFontVariationAxes(ComputedStyle style, LengthContext ctx) {
            return FontVariationResolver.Resolve(style, ctx);
        }

        public static LinearColor ResolveTextColor(ComputedStyle style) {
            return ColorResolver.ResolveCurrentColor(style);
        }

        // CSS Text Decoration L4 §10 `-webkit-text-stroke-{width,color}`.
        // Returns (widthPx, color, hasStroke). hasStroke is false when the
        // resolved width is <= 0 — the EmitTextRun path can skip the phantom
        // entirely. The color falls back to the run's currentcolor when the
        // longhand is `currentcolor` (the spec initial).
        public static (double widthPx, LinearColor color, bool hasStroke) ResolveTextStroke(ComputedStyle style, LengthContext ctx) {
            if (style == null) return (0, default, false);
            double widthPx = 0;
            var wParsed = style.GetParsed(CssProperties.WebkitTextStrokeWidthId);
            if (wParsed is CssLength wlen) widthPx = wlen.ToPixels(ctx);
            else if (wParsed is CssNumber wn) widthPx = wn.Value;
            else if (wParsed is CssKeyword wk) {
                // <line-width> keywords per CSS Backgrounds L3 §6.6.
                switch (wk.Identifier) {
                    case "thin": widthPx = 1; break;
                    case "medium": widthPx = 3; break;
                    case "thick": widthPx = 5; break;
                }
            } else if (wParsed is CssCalc wc) {
                try { widthPx = wc.Evaluate(ctx); } catch { widthPx = 0; }
            }
            if (widthPx <= 0) return (0, default, false);
            var current = ColorResolver.ResolveCurrentColor(style);
            // text-stroke-color defaults to currentcolor; ColorResolver.Resolve
            // returns currentcolor when the parsed value is the `currentcolor`
            // keyword (initial) — so the typical author-omits-color case still
            // matches the run's fill.
            string rawColor = style.Get(CssProperties.WebkitTextStrokeColorId);
            LinearColor strokeColor = string.IsNullOrEmpty(rawColor)
                ? current
                : ColorResolver.Resolve(rawColor, style);
            return (widthPx, strokeColor, true);
        }

        // CSS Fonts L4 §6.5 — `font-kerning: auto | normal | none`. Only the
        // explicit `none` keyword disables the shaper's glyph-pair kerning
        // pass; `auto` and `normal` (and the implicit unset / initial) all
        // keep kerning on. Returns `true` for any unrecognised value so
        // authors who roll in unknown keywords don't accidentally lose
        // kerning — the spec wants UA discretion in `auto`, and `false`
        // would silently degrade.
        public static bool ResolveKerningEnabled(ComputedStyle style) {
            if (style == null) return true;
            int id = CssProperties.GetId("font-kerning");
            var v = style.GetParsed(id);
            if (v is CssKeyword k) return !string.Equals(k.Identifier, "none", System.StringComparison.OrdinalIgnoreCase);
            if (v is CssIdentifier ident) return !string.Equals(ident.Name, "none", System.StringComparison.OrdinalIgnoreCase);
            string raw = style.Get(id);
            if (string.IsNullOrEmpty(raw)) return true;
            return !CssStringUtil.EqualsIgnoreCaseTrimmed(raw.Trim(), "none");
        }

        // CSS Text Module Level 3 §10.1: `letter-spacing` value resolved to pixels.
        // Accepts <length>, `normal` (= 0), and calc(). Returns 0 for empty / unsupported.
        public static double ResolveLetterSpacingPx(ComputedStyle style, LengthContext ctx) {
            if (style == null) return 0;
            var v = style.GetParsed(CssProperties.LetterSpacingId);
            if (v == null) return 0;
            if (v is CssKeyword k && k.Identifier == "normal") return 0;
            if (v is CssIdentifier id && string.Equals(id.Name, "normal", System.StringComparison.OrdinalIgnoreCase)) return 0;
            if (v is CssLength len) return len.ToPixels(ctx);
            if (v is CssNumber num) return num.Value;
            if (v is CssPercentage p) return ctx.BaseFontSizePx * p.Value * 0.01;
            if (v is CssCalc c) {
                try { return c.Evaluate(ctx); } catch { return 0; }
            }
            return 0;
        }

        // CSS Text Module Level 3 §10.2: `word-spacing` value resolved to
        // pixels. Same grammar shape as letter-spacing (<length>, `normal`,
        // calc(), percentage of font-size) but applies extra advance to each
        // word-separator character — primarily U+0020 ASCII space. Returns 0
        // for `normal`, unset, or any unsupported value form.
        public static double ResolveWordSpacingPx(ComputedStyle style, LengthContext ctx) {
            if (style == null) return 0;
            var v = style.GetParsed(CssProperties.WordSpacingId);
            if (v == null) return 0;
            if (v is CssKeyword k && k.Identifier == "normal") return 0;
            if (v is CssIdentifier id && string.Equals(id.Name, "normal", System.StringComparison.OrdinalIgnoreCase)) return 0;
            if (v is CssLength len) return len.ToPixels(ctx);
            if (v is CssNumber num) return num.Value;
            if (v is CssPercentage p) return ctx.BaseFontSizePx * p.Value * 0.01;
            if (v is CssCalc c) {
                try { return c.Evaluate(ctx); } catch { return 0; }
            }
            return 0;
        }

        // CSS Text Module Level 3 §3: `text-transform` rewrites the text PRIOR to
        // shaping. Applied at layout-item construction so that both the line-breaker
        // measurements and the painted glyph stream see the same transformed text.
        // v1 supports: none (default), uppercase, lowercase, capitalize.
        // `full-width` and `full-size-kana` are deferred (CJK-specific).
        public static string ApplyTextTransform(ComputedStyle style, string text) {
            if (string.IsNullOrEmpty(text) || style == null) return text;
            var v = style.GetParsed(CssProperties.TextTransformId);
            string mode = null;
            if (v is CssKeyword k) mode = k.Identifier;
            else if (v is CssIdentifier id) mode = CssStringUtil.ToLowerInvariantOrSame(id.Name);
            if (mode == null) return text;
            if (mode == "none" || mode == "normal") return text;
            if (mode == "uppercase") return text.ToUpperInvariant();
            if (mode == "lowercase") return text.ToLowerInvariant();
            if (mode == "capitalize") return Capitalize(text);
            return text;
        }

        // Capitalizes the first letter of each word. A word boundary is whitespace
        // or any non-letter; we use the simple Unicode-aware rule that applies the
        // uppercase mapping to the first letter following a boundary.
        static string Capitalize(string text) {
            var sb = new System.Text.StringBuilder(text.Length);
            bool atBoundary = true;
            for (int i = 0; i < text.Length; i++) {
                char c = text[i];
                if (char.IsLetter(c)) {
                    sb.Append(atBoundary ? char.ToUpperInvariant(c) : c);
                    atBoundary = false;
                } else {
                    sb.Append(c);
                    atBoundary = char.IsWhiteSpace(c) || !char.IsLetterOrDigit(c);
                }
            }
            return sb.ToString();
        }

        public static TextDecoration ResolveDecoration(ComputedStyle style) {
            if (style == null) return TextDecoration.None;
            var v = style.GetParsed(CssProperties.TextDecorationLineId);
            if (v == null) v = style.GetParsed(CssProperties.TextDecorationId);
            if (v != null) {
                var result = TextDecoration.None;
                AccumulateDecorationFlags(v, ref result);
                if (result != TextDecoration.None) return result;
                // If the parsed value yielded no flags (e.g. it was just
                // `none`, or a color-only `text-decoration` shorthand parsed
                // as a CssColor), fall through to the raw-string scan below
                // so the historical contains-substring semantics still apply.
            }
            string raw = style.Get(CssProperties.TextDecorationLineId);
            if (string.IsNullOrEmpty(raw)) raw = style.Get(CssProperties.TextDecorationId);
            if (string.IsNullOrEmpty(raw)) return TextDecoration.None;
            // `raw.ToLowerInvariant() + Contains(...)` allocated a fresh
            // lowercased copy every call. Use IndexOf with OrdinalIgnoreCase
            // — String.IndexOf(string, StringComparison) is alloc-free.
            TextDecoration flags = TextDecoration.None;
            if (raw.IndexOf("underline", System.StringComparison.OrdinalIgnoreCase) >= 0) flags |= TextDecoration.Underline;
            if (raw.IndexOf("overline", System.StringComparison.OrdinalIgnoreCase) >= 0) flags |= TextDecoration.Overline;
            if (raw.IndexOf("line-through", System.StringComparison.OrdinalIgnoreCase) >= 0) flags |= TextDecoration.LineThrough;
            return flags;
        }

        // Walks a parsed text-decoration[-line] tree and ORs in the matching
        // line-keyword flags. Handles the single-keyword, identifier, and
        // space-separated list shapes the parser produces for values like
        // `underline overline` or `underline solid red`.
        static void AccumulateDecorationFlags(CssValue v, ref TextDecoration flags) {
            switch (v) {
                case CssKeyword k:
                    // CssKeyword.Identifier is already lowercased by the parser.
                    ApplyDecorationKeyword(k.Identifier, ref flags);
                    break;
                case CssIdentifier id:
                    // Was `id.Name.ToLowerInvariant()` per call — ToLowerInvariant
                    // ALWAYS allocates a fresh string, even when the input is
                    // already lowercase. Use the ignore-case comparator below
                    // to do the keyword match without the per-call alloc
                    // (~16 B × 200+ calls / frame on text-heavy scenes).
                    ApplyDecorationKeywordIgnoreCase(id.Name, ref flags);
                    break;
                case CssValueList list:
                    for (int i = 0; i < list.Items.Count; i++) {
                        AccumulateDecorationFlags(list.Items[i], ref flags);
                    }
                    break;
            }
        }

        static void ApplyDecorationKeyword(string name, ref TextDecoration flags) {
            if (name == "underline") flags |= TextDecoration.Underline;
            else if (name == "overline") flags |= TextDecoration.Overline;
            else if (name == "line-through") flags |= TextDecoration.LineThrough;
        }

        static void ApplyDecorationKeywordIgnoreCase(string name, ref TextDecoration flags) {
            if (string.Equals(name, "underline", System.StringComparison.OrdinalIgnoreCase)) flags |= TextDecoration.Underline;
            else if (string.Equals(name, "overline", System.StringComparison.OrdinalIgnoreCase)) flags |= TextDecoration.Overline;
            else if (string.Equals(name, "line-through", System.StringComparison.OrdinalIgnoreCase)) flags |= TextDecoration.LineThrough;
        }

        // CSS Text Decoration 4 §3-§4: resolves the full decoration set —
        // line flags + per-CSS color override + style enum + thickness/offset
        // in pixels. Color = null when the author did NOT set a non-default
        // text-decoration-color (the baker then falls back to the run color,
        // preserving the v0 behaviour for plain `text-decoration: underline`).
        // Thickness/Offset come back as -1 when the author wrote `auto`; the
        // baker resolves the font-derived default (ascent/12 / 0 px).
        public static (TextDecoration line, LinearColor? color, DecorationStyle style, double thickness, double offset)
            ResolveDecorationStyle(ComputedStyle style) {
            var line = ResolveDecoration(style);
            if (style == null) {
                return (line, null, DecorationStyle.Solid, -1, -1);
            }

            LinearColor? color = null;
            var colorParsed = style.GetParsed(CssProperties.TextDecorationColorId);
            if (colorParsed != null) {
                // `currentcolor` (the spec initial value) means "fall back to
                // the run color" — leave color null so the baker uses
                // req.Color and existing tests that expect that behaviour
                // continue to pass.
                bool isCurrentColor = colorParsed is CssKeyword ck && ck.Identifier == "currentcolor";
                if (!isCurrentColor) {
                    var current = ResolveTextColor(style);
                    if (ColorResolver.TryResolveParsed(colorParsed, current, style, out var parsed)) {
                        color = parsed;
                    }
                }
            }

            DecorationStyle decoStyle = DecorationStyle.Solid;
            var styleParsed = style.GetParsed(CssProperties.TextDecorationStyleId);
            // CssKeyword.Identifier is already lowercased; CssIdentifier.Name
            // may not be. Compare with OrdinalIgnoreCase rather than allocating
            // a ToLowerInvariant copy per call (~16 B × N text runs / frame).
            string styleKw = null;
            if (styleParsed is CssKeyword sk) styleKw = sk.Identifier;
            else if (styleParsed is CssIdentifier sid) styleKw = sid.Name;
            if (styleKw != null) {
                if (string.Equals(styleKw, "solid", System.StringComparison.OrdinalIgnoreCase)) decoStyle = DecorationStyle.Solid;
                else if (string.Equals(styleKw, "double", System.StringComparison.OrdinalIgnoreCase)) decoStyle = DecorationStyle.Double;
                else if (string.Equals(styleKw, "dotted", System.StringComparison.OrdinalIgnoreCase)) decoStyle = DecorationStyle.Dotted;
                else if (string.Equals(styleKw, "dashed", System.StringComparison.OrdinalIgnoreCase)) decoStyle = DecorationStyle.Dashed;
                else if (string.Equals(styleKw, "wavy", System.StringComparison.OrdinalIgnoreCase)) decoStyle = DecorationStyle.Wavy;
            }

            double thickness = ResolveDecorationLengthAuto(
                style.GetParsed(CssProperties.TextDecorationThicknessId),
                LengthContext.Default,
                allowFromFont: true);
            double offset = ResolveDecorationLengthAuto(
                style.GetParsed(CssProperties.TextUnderlineOffsetId),
                LengthContext.Default,
                allowFromFont: false);

            return (line, color, decoStyle, thickness, offset);
        }

        // Length-context-aware sibling for the paint hot path where the
        // converter has already built a real LengthContext (em/rem need it).
        public static (TextDecoration line, LinearColor? color, DecorationStyle style, double thickness, double offset)
            ResolveDecorationStyle(ComputedStyle style, LengthContext ctx) {
            var (line, color, decoStyle, thickness, offset) = ResolveDecorationStyle(style);
            if (style == null) return (line, color, decoStyle, thickness, offset);

            // Re-resolve thickness/offset under the proper LengthContext so em
            // / rem / percentage values land at correct pixels (the
            // ComputedStyle-only overload uses LengthContext.Default which is
            // the safe fallback when no document context is available).
            thickness = ResolveDecorationLengthAuto(
                style.GetParsed(CssProperties.TextDecorationThicknessId),
                ctx,
                allowFromFont: true);
            offset = ResolveDecorationLengthAuto(
                style.GetParsed(CssProperties.TextUnderlineOffsetId),
                ctx,
                allowFromFont: false);
            return (line, color, decoStyle, thickness, offset);
        }

        // Shared resolver for text-decoration-thickness and
        // text-underline-offset. Both default to `auto` (returns -1 so the
        // baker picks a font-derived default), accept lengths/numbers/percent,
        // and only thickness honours the `from-font` keyword. The keyword/
        // identifier branches all collapse to -1 either way — any non-length
        // keyword falls back to the baker's font-derived default. The
        // `allowFromFont` arg is kept for spec clarity (the property table
        // only allows `from-font` on thickness) even though the current
        // collapse makes it a no-op at this layer.
        static double ResolveDecorationLengthAuto(CssValue v, LengthContext ctx, bool allowFromFont) {
            if (v == null) return -1;
            if (v is CssLength len) return len.ToPixels(ctx);
            if (v is CssNumber num) return num.Value;
            if (v is CssPercentage p) return ctx.BaseFontSizePx * p.Value * 0.01;
            if (v is CssCalc c) {
                try { return c.Evaluate(ctx); } catch { return -1; }
            }
            // CssKeyword / CssIdentifier / anything else → -1 (auto / unknown
            // keyword → baker's font-derived default).
            return -1;
        }

        // Parsed-CssValue variant of ResolveFontSize. The cascade-resolved
        // string still flows through this path when GetParsed returns null
        // (parser failure / empty slot), but the hot path now dispatches
        // directly on the typed value without re-running CssValue.TryParse.
        static double ResolveFontSizeParsed(CssValue v, LengthContext ctx, double fallback) {
            if (v is CssKeyword k) return ResolveAbsoluteFontSize(k.Identifier, ctx, fallback);
            // CssIdentifier.Name may not be pre-lowercased. ToLowerInvariantOrSame
            // short-circuits when the input has no uppercase, avoiding the per-
            // call string allocation for the common already-lower path.
            if (v is CssIdentifier id) return ResolveAbsoluteFontSize(CssStringUtil.ToLowerInvariantOrSame(id.Name), ctx, fallback);
            if (v is CssLength len) return len.ToPixels(ctx);
            if (v is CssNumber num) return num.Value;
            if (v is CssPercentage p) return ctx.BaseFontSizePx * p.Value * 0.01;
            // CSS Values L4 §10: clamp/min/max/calc resolve to a length.
            // Layout's StyleResolver.FontSizePx already handles this branch;
            // missing it here means EVERY clamp()'d font-size resolves to
            // `fallback` (= the inherited base, typically 16) at paint
            // time while layout used the actual resolved value (12-14).
            // The result was every text run shaping ~14% wider than
            // measured, so adjacent runs overlapped (Aerith → Stormborn,
            // every chat-line word, EMBERS OF THE WYRM, etc.).
            if (v is CssCalc c) {
                try { return c.Evaluate(ctx); } catch { /* fall through */ }
            }
            return fallback;
        }

        static double ResolveAbsoluteFontSize(string keyword, LengthContext ctx, double fallback) {
            switch (keyword) {
                case "xx-small": return ctx.RootFontSizePx * 0.6;
                case "x-small": return ctx.RootFontSizePx * 0.75;
                case "small": return ctx.RootFontSizePx * 0.85;
                case "medium": return ctx.RootFontSizePx;
                case "large": return ctx.RootFontSizePx * 1.2;
                case "x-large": return ctx.RootFontSizePx * 1.5;
                case "xx-large": return ctx.RootFontSizePx * 2.0;
            }
            return fallback;
        }

        static double ResolveFontSize(string raw, LengthContext ctx, double fallback) {
            string t = CssStringUtil.ToLowerInvariantOrSame(raw.Trim());
            switch (t) {
                case "xx-small": return ctx.RootFontSizePx * 0.6;
                case "x-small": return ctx.RootFontSizePx * 0.75;
                case "small": return ctx.RootFontSizePx * 0.85;
                case "medium": return ctx.RootFontSizePx;
                case "large": return ctx.RootFontSizePx * 1.2;
                case "x-large": return ctx.RootFontSizePx * 1.5;
                case "xx-large": return ctx.RootFontSizePx * 2.0;
            }
            if (CssValue.TryParse(raw, out var v)) {
                if (v is CssLength len) return len.ToPixels(ctx);
                if (v is CssNumber num) return num.Value;
                if (v is CssPercentage p) return ctx.BaseFontSizePx * p.Value * 0.01;
                if (v is CssCalc c) {
                    try { return c.Evaluate(ctx); } catch { /* fall through */ }
                }
            }
            return fallback;
        }

        // Parsed-value resolver for font-weight. Accepts the symbolic
        // keywords (`normal`, `bold`, `lighter`, `bolder`) and numeric
        // weights (`400`, `700`). Falls back to the supplied default on any
        // other shape.
        public static int ResolveFontWeight(ComputedStyle style, int fallback = 400) {
            if (style == null) return fallback;
            var weightParsed = style.GetParsed(CssProperties.FontWeightId);
            return weightParsed != null ? ResolveWeightParsed(weightParsed, fallback) : fallback;
        }

        public static FontStyle ResolveFontStyle(ComputedStyle style) {
            if (style == null) return FontStyle.Normal;
            var styleParsed = style.GetParsed(CssProperties.FontStyleId);
            if (styleParsed is CssKeyword fsKw) {
                return ResolveFontStyleKeyword(fsKw.Identifier);
            }
            if (styleParsed is CssIdentifier fsId) {
                // CssStringUtil.ToLowerInvariantOrSame short-circuits when
                // the input is already lowercase, so the common case (the
                // cascade canonicalises keyword values) is allocation-free.
                return ResolveFontStyleKeyword(CssStringUtil.ToLowerInvariantOrSame(fsId.Name));
            }
            return FontStyle.Normal;
        }

        static int ResolveWeightParsed(CssValue v, int fallback) {
            if (v is CssKeyword k) return ResolveWeightKeyword(k.Identifier, fallback);
            if (v is CssIdentifier id) return ResolveWeightKeyword(CssStringUtil.ToLowerInvariantOrSame(id.Name), fallback);
            if (v is CssNumber num) return (int)num.Value;
            return fallback;
        }

        static int ResolveWeightKeyword(string keyword, int fallback) {
            switch (keyword) {
                case "normal": return 400;
                case "bold": return 700;
                case "lighter": return 300;
                case "bolder": return 700;
            }
            // Some authors stringify numeric weights as identifiers via
            // intermediate cascade layers; tolerate that shape.
            if (int.TryParse(keyword, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
            return fallback;
        }

        static FontStyle ResolveFontStyleKeyword(string keyword) {
            switch (keyword) {
                case "italic": return FontStyle.Italic;
                case "oblique": return FontStyle.Oblique;
                default: return FontStyle.Normal;
            }
        }
    }
}
