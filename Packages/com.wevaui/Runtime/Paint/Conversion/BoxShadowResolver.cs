using System;
using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Css.Values;

namespace Weva.Paint.Conversion {
    internal static class BoxShadowResolver {
        public static BoxShadow[] ResolveBoxShadow(ComputedStyle style, LengthContext ctx) {
            if (style == null) return Array.Empty<BoxShadow>();
            // Per-style parsed cache (ComputedStyle.GetParsed) returns the
            // already-built CssValue tree without re-running CssValue.TryParse
            // or the old RawValueParser comma/space split — O(1) array lookup
            // after the first read, no dictionary probe.
            var parsed = style.GetParsed(CssProperties.BoxShadowId);
            if (parsed == null) return Array.Empty<BoxShadow>();
            if (IsNone(parsed)) return Array.Empty<BoxShadow>();
            var current = ColorResolver.ResolveCurrentColor(style);
            var list = new List<BoxShadow>();
            AppendShadows(parsed, ctx, current, style, list);
            return list.Count == 0 ? Array.Empty<BoxShadow>() : list.ToArray();
        }

        // Process-static BoxShadow result cache. The key must include EVERY
        // LengthContext field a shadow length can resolve against, or a vh/vw
        // (viewport), cm/in (dpi) or lh/rlh shadow freezes at whatever context
        // first populated the entry. Omitting the viewport was a real bug:
        // `0 4.44vh 11.11vh` resolved once at 4k and every other resolution with
        // the same font-size + color hit that frozen 4k blur. (% can't appear in
        // box-shadow lengths, so BasisPixels is intentionally excluded.) Keyed
        // here rather than on style.Version so animated tiles whose composed
        // style bumps Version every frame — but whose box-shadow text is stable —
        // still hit.
        readonly struct ShadowCacheKey : System.IEquatable<ShadowCacheKey> {
            public readonly string Raw;
            public readonly double BaseFontSizePx;
            public readonly double RootFontSizePx;
            public readonly double ViewportWidthPx;
            public readonly double ViewportHeightPx;
            public readonly double DpiPixelsPerInch;
            public readonly double LineHeightPx;
            public readonly double RootLineHeightPx;
            public readonly LinearColor CurrentColor;
            public ShadowCacheKey(string raw, in LengthContext ctx, LinearColor cc) {
                Raw = raw;
                BaseFontSizePx = ctx.BaseFontSizePx;
                RootFontSizePx = ctx.RootFontSizePx;
                ViewportWidthPx = ctx.ViewportWidthPx;
                ViewportHeightPx = ctx.ViewportHeightPx;
                DpiPixelsPerInch = ctx.DpiPixelsPerInch;
                LineHeightPx = ctx.LineHeightPx;
                RootLineHeightPx = ctx.RootLineHeightPx;
                CurrentColor = cc;
            }
            public bool Equals(ShadowCacheKey other) =>
                Raw == other.Raw && BaseFontSizePx == other.BaseFontSizePx
                && RootFontSizePx == other.RootFontSizePx
                && ViewportWidthPx == other.ViewportWidthPx
                && ViewportHeightPx == other.ViewportHeightPx
                && DpiPixelsPerInch == other.DpiPixelsPerInch
                && LineHeightPx == other.LineHeightPx
                && RootLineHeightPx == other.RootLineHeightPx
                && CurrentColor.Equals(other.CurrentColor);
            public override bool Equals(object obj) => obj is ShadowCacheKey k && Equals(k);
            public override int GetHashCode() {
                unchecked {
                    int h = Raw != null ? Raw.GetHashCode() : 0;
                    h = (h * 397) ^ BaseFontSizePx.GetHashCode();
                    h = (h * 397) ^ RootFontSizePx.GetHashCode();
                    h = (h * 397) ^ ViewportWidthPx.GetHashCode();
                    h = (h * 397) ^ ViewportHeightPx.GetHashCode();
                    h = (h * 397) ^ DpiPixelsPerInch.GetHashCode();
                    h = (h * 397) ^ LineHeightPx.GetHashCode();
                    h = (h * 397) ^ RootLineHeightPx.GetHashCode();
                    h = (h * 397) ^ CurrentColor.GetHashCode();
                    return h;
                }
            }
        }
        const int ShadowCacheCap = 128;
        static readonly Dictionary<ShadowCacheKey, BoxShadow[]> s_ShadowCache
            = new Dictionary<ShadowCacheKey, BoxShadow[]>();
        static readonly BoxShadow[] EmptyShadows = Array.Empty<BoxShadow>();

        // Pool-aware overload: appends shadows into `output`. Returns true if any
        // shadow was emitted (so callers can skip the pop/inset loop without an
        // extra .Count read). Hot-path callers (BoxToPaintConverter) pre-Clear()
        // the buffer before calling this.
        public static bool ResolveBoxShadowInto(ComputedStyle style, LengthContext ctx, List<BoxShadow> output) {
            if (output == null || style == null) return false;
            var parsed = style.GetParsed(CssProperties.BoxShadowId);
            if (parsed == null) return false;
            if (IsNone(parsed)) return false;
            var current = ColorResolver.ResolveCurrentColor(style);
            string raw = parsed.Raw;
            // Without a stable raw key we can't safely cache — fall through
            // to the walk. parsed.Raw is populated by the CssValue parser for
            // every typed tree it emits, so this branch is effectively dead
            // in production but defends against malformed CssValue subclasses.
            if (raw == null) {
                AppendShadows(parsed, ctx, current, style, output);
                return true;
            }
            var key = new ShadowCacheKey(raw, ctx, current);
            if (s_ShadowCache.TryGetValue(key, out var cached)) {
                if (cached.Length == 0) return false;
                for (int i = 0; i < cached.Length; i++) output.Add(cached[i]);
                return true;
            }
            int before = output.Count;
            AppendShadows(parsed, ctx, current, style, output);
            int produced = output.Count - before;
            // Allocation is amortised across every future hit on the same
            // (raw, ctx, currentColor) tuple — for the gem-grid scene where
            // 16 animated tiles all share the same box-shadow declaration,
            // 15 of the 16 misses become hits after the first frame.
            BoxShadow[] snap;
            if (produced == 0) {
                snap = EmptyShadows;
            } else {
                snap = new BoxShadow[produced];
                for (int i = 0; i < produced; i++) snap[i] = output[before + i];
            }
            // Slice-evict instead of drop-new-on-overflow (audit PF1): the
            // viewport-widened key (b107e622) means a window-resize drag mints
            // a new key per frame — under drop-new those dead keys filled the
            // cap in ~1-2s and PERMANENTLY locked every shadow out of caching
            // for the rest of the process. Same recipe as FilterResolver (P16).
            ParseCacheEviction.EnsureRoom(s_ShadowCache, ShadowCacheCap);
            s_ShadowCache[key] = snap;
            return produced > 0;
        }

        // Test seams (UITestCacheGuards-style): the cache is process-static,
        // so eviction pins need to observe and reset it.
        internal static int CacheCountForTests => s_ShadowCache.Count;
        internal static void ClearCacheForTests() => s_ShadowCache.Clear();

        // Top-level shape dispatch. A comma-separated CssValueList is a
        // multi-shadow declaration — each item is its own per-shadow expression.
        // Anything else (a space-separated CssValueList, or a single value such
        // as a lone CssLength) is a single shadow segment in itself.
        static void AppendShadows(CssValue parsed, LengthContext ctx, LinearColor currentColor, ComputedStyle style, List<BoxShadow> output) {
            if (parsed is CssValueList outer && outer.Separator == CssValueListSeparator.Comma) {
                var items = outer.Items;
                for (int i = 0; i < items.Count; i++) {
                    if (TryParseSegment(items[i], ctx, currentColor, style, out var sh)) {
                        output.Add(sh);
                    }
                }
                return;
            }
            if (TryParseSegment(parsed, ctx, currentColor, style, out var single)) {
                output.Add(single);
            }
        }

        // Walks one shadow segment, collecting up to four <length> offsets +
        // optional `inset` keyword + optional color token. Items typed as
        // CssLength / CssNumber / CssCalc are length-shaped; CssKeyword "inset"
        // sets the inset flag; the remaining color-shaped tokens (CssColor,
        // CssFunctionCall, color-named CssIdentifier/CssKeyword) feed
        // ColorResolver.TryResolveParsed.
        static bool TryParseSegment(CssValue segment, LengthContext ctx, LinearColor currentColor, ComputedStyle style, out BoxShadow shadow) {
            shadow = default;
            if (segment == null) return false;

            // Single-item shape: per CSS Box-Shadow L3, a valid shadow needs
            // at least two <length> offsets, so a lone value (anything except
            // a CssValueList with ≥2 items) can never produce a shadow.
            if (!(segment is CssValueList list) || list.Separator != CssValueListSeparator.Space) {
                return false;
            }

            bool inset = false;
            bool hasColor = false;
            LinearColor color = currentColor;
            // Stack-allocate the four offset slots inline to avoid a List<double>
            // alloc per segment (this resolver runs per box-decoration on the
            // paint hot path).
            double l0 = 0, l1 = 0, l2 = 0, l3 = 0;
            int lengthCount = 0;

            var items = list.Items;
            for (int i = 0; i < items.Count; i++) {
                var item = items[i];
                if (TryReadLength(item, ctx, out var px)) {
                    switch (lengthCount) {
                        case 0: l0 = px; break;
                        case 1: l1 = px; break;
                        case 2: l2 = px; break;
                        case 3: l3 = px; break;
                    }
                    if (lengthCount < 4) lengthCount++;
                    continue;
                }
                if (item is CssKeyword kw) {
                    if (kw.Identifier == "inset") { inset = true; continue; }
                    // Named colors *can* arrive as a CssKeyword in some
                    // parser paths (the parser emits CssColor for known
                    // names today, but stay defensive). Fall through to
                    // ColorResolver.TryResolveParsed which handles both.
                }
                if (ColorResolver.TryResolveParsed(item, currentColor, style, out var c)) {
                    color = c;
                    hasColor = true;
                    continue;
                }
            }

            if (lengthCount < 2) return false;
            double offX = l0;
            double offY = l1;
            double blur = lengthCount >= 3 ? l2 : 0;
            // CSS Backgrounds 3 — `box-shadow` blur-radius cannot be
            // negative; clamp at 0 (mirroring TextShadowResolver:99-100).
            // Without the clamp a `-8px` blur lands at the SDF shadow
            // shader as a negative half-width, inverting the gradient
            // math instead of producing the spec-mandated hard edge.
            if (blur < 0) blur = 0;
            double spread = lengthCount >= 4 ? l3 : 0;
            if (!hasColor) color = currentColor;
            shadow = new BoxShadow(offX, offY, blur, spread, color, inset);
            return true;
        }

        // Length-shaped item → pixel value. Mirrors the RawValueParser path the
        // old code took, but operates on the already-typed parse tree.
        static bool TryReadLength(CssValue v, LengthContext ctx, out double pixels) {
            pixels = 0;
            if (v is CssLength len) { pixels = len.ToPixels(ctx); return true; }
            if (v is CssNumber num) { pixels = num.Value; return true; }
            if (v is CssCalc calc) {
                try { pixels = calc.Evaluate(ctx); return true; } catch { return false; }
            }
            return false;
        }

        static bool IsNone(CssValue v) {
            if (v is CssKeyword k && k.Identifier == "none") return true;
            if (v is CssIdentifier id && CssStringUtil.EqualsIgnoreCase(id.Name, "none")) return true;
            return false;
        }
    }
}
