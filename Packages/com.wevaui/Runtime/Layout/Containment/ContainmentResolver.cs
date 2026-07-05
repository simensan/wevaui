using Weva.Css;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Layout;

namespace Weva.Layout.Containment {
    // CSS Containment L2 §2 — computed-value resolver for the `contain` property,
    // and §4 — content-visibility property helpers.
    //
    // The `contain` property accepts a space-separated list of keywords, plus two
    // shorthands:
    //   strict  = layout paint size style
    //   content = layout paint style
    //
    // `content-visibility` (§4) implies containment bits:
    //   hidden  ⇒ layout + paint + size containment (per §4.2)
    //   auto    ⇒ layout + paint containment (per §4.1); size only when skipped
    //
    // This helper centralises all token-scanning so callers never inspect the raw
    // string directly.  Designed to be reusable by C3 (content-visibility).
    //
    // Thread-safety: all methods are static and read-only; safe for concurrent use.
    internal static class ContainmentResolver {
        // Returns true when the effective `inline-size` containment bit applies.
        //
        // CSS Containment L2 §3.3 + CSS Containment L3 §inline-size:
        //   • An explicit `inline-size` token on the `contain` property.
        //   • `contain: size` covers BOTH axes (inline + block), so it implies
        //     inline-size containment.  `strict` expands to layout+paint+size+style,
        //     so it also implies inline-size via the `size` axis.
        //   • `content` shorthand = layout+paint+style; it does NOT include size,
        //     so `content` does NOT imply inline-size.
        //   • `content-visibility: hidden` implies size containment (§4.2), which
        //     in turn implies inline-size containment.
        public static bool HasInlineSize(ComputedStyle style) {
            string v = style?.Get(CssProperties.ContainId);
            if (!string.IsNullOrEmpty(v) && v != "none") {
                // Explicit `inline-size` token OR `size`/`strict` (size covers both axes).
                if (HasToken(v, "inline-size") || HasToken(v, "size") || HasToken(v, "strict")) return true;
            }
            // content-visibility: hidden implies size containment (§4.2), which
            // covers both axes — so it also implies inline-size containment.
            if (IsContentVisibilityHidden(style)) return true;
            return false;
        }

        // Returns true when the box has ANY containment value set (other than none).
        public static bool HasAny(ComputedStyle style) {
            string v = style?.Get(CssProperties.ContainId);
            return !string.IsNullOrEmpty(v) && v != "none";
        }

        // ── content-visibility helpers (CSS Containment L2 §4) ───────────────
        //
        // content-visibility: hidden  → element's contents are skipped for
        //   paint, hit-testing, and layout sizing, while the element itself
        //   still renders its own box (background, border).  Implies
        //   layout + paint + size containment (§4.2).
        //
        // content-visibility: auto    → contents always get layout + paint
        //   containment; the engine may additionally skip descendant paint when
        //   the element is entirely off the viewport (§4.1, relevance-based).
        //   Size containment does NOT apply when on-screen in v1.
        //
        // content-visibility: visible → no effect (initial value).

        // Returns true when content-visibility is `hidden`.
        public static bool IsContentVisibilityHidden(ComputedStyle style) {
            string v = style?.Get(CssProperties.ContentVisibilityId);
            if (string.IsNullOrEmpty(v)) return false;
            return v.Trim().Equals("hidden", System.StringComparison.OrdinalIgnoreCase);
        }

        // Returns true when content-visibility is `auto`.
        public static bool IsContentVisibilityAuto(ComputedStyle style) {
            string v = style?.Get(CssProperties.ContentVisibilityId);
            if (string.IsNullOrEmpty(v)) return false;
            return v.Trim().Equals("auto", System.StringComparison.OrdinalIgnoreCase);
        }

        // True when the effective `layout` bit comes from `contain` OR from
        // content-visibility:hidden/auto (both imply layout containment per §4).
        public static bool HasLayout(ComputedStyle style) {
            string v = style?.Get(CssProperties.ContainId);
            if (!string.IsNullOrEmpty(v) && v != "none") {
                if (HasToken(v, "layout") || HasToken(v, "strict") || HasToken(v, "content")) return true;
            }
            // content-visibility: hidden | auto imply layout containment.
            if (IsContentVisibilityHidden(style) || IsContentVisibilityAuto(style)) return true;
            return false;
        }

        // True when the effective `paint` bit comes from `contain` OR from
        // content-visibility:hidden/auto (both imply paint containment per §4).
        public static bool HasPaint(ComputedStyle style) {
            string v = style?.Get(CssProperties.ContainId);
            if (!string.IsNullOrEmpty(v) && v != "none") {
                if (HasToken(v, "paint") || HasToken(v, "strict") || HasToken(v, "content")) return true;
            }
            // content-visibility: hidden | auto imply paint containment.
            if (IsContentVisibilityHidden(style) || IsContentVisibilityAuto(style)) return true;
            return false;
        }

        // True when the effective `size` bit comes from `contain` OR from
        // content-visibility:hidden (hidden implies size containment; auto does NOT
        // at layout time in v1 — only while skipped, which we do not implement as
        // a layout-tree mutation).
        public static bool HasSize(ComputedStyle style) {
            string v = style?.Get(CssProperties.ContainId);
            if (!string.IsNullOrEmpty(v) && v != "none") {
                if (HasToken(v, "size") || HasToken(v, "strict")) return true;
            }
            // content-visibility: hidden implies size containment (§4.2).
            if (IsContentVisibilityHidden(style)) return true;
            return false;
        }

        // CSS Containment L2 §3.3 — style containment.
        //
        // When the `style` bit is active, CSS counters and quotes are scoped
        // to the element's subtree: descendant counter-increment / counter-set
        // mutations cannot affect counters established outside the boundary.
        //
        // The `style` bit is set by:
        //   • An explicit `style` token in the `contain` value.
        //   • `strict` (= layout+paint+size+style per §2.3).
        //   • `content` (= layout+paint+style per §2.3).
        //
        // Note: `content-visibility` does NOT imply style containment per spec
        // (§4 lists only layout+paint+size for `hidden`; no style bit).
        //
        // Spec reference: https://www.w3.org/TR/css-contain-2/#contain-property
        //   "strict: [...] style [...]"
        //   "content: Turns on all forms of containment for an element, except
        //    size containment. [...] is equivalent to contain: layout paint style."
        public static bool HasStyle(ComputedStyle style) {
            string v = style?.Get(CssProperties.ContainId);
            if (!string.IsNullOrEmpty(v) && v != "none") {
                // `style` token directly, or `strict`/`content` shorthands both
                // include the style bit (css-contain-2 §2.3).
                if (HasToken(v, "style") || HasToken(v, "strict") || HasToken(v, "content")) return true;
            }
            // content-visibility does NOT imply style containment.
            return false;
        }

        // Returns true when content-visibility:hidden is set — convenient alias
        // used by the paint converter and hit tester to skip descendants.
        public static bool SkipDescendantPaint(ComputedStyle style) =>
            IsContentVisibilityHidden(style);

        // CSS Sizing L4 §5: resolves a contain-intrinsic-size axis value from the
        // raw style string for a SINGLE axis (e.g. contain-intrinsic-width or one
        // token of contain-intrinsic-size) to pixels.
        //
        // Grammar: `none | <length> | auto <length>`
        //
        // Mapping:
        //   none            → 0  (contained axis contributes nothing; existing behaviour)
        //   <length>        → resolved px value
        //   auto <length>   → resolved px value  (last-remembered-size path not implemented;
        //                     uses the explicit <length> fallback always — documented Chrome
        //                     parity gap: Chrome uses the last painted size when available)
        //
        // Percentages are invalid per spec grammar and are treated as 0 (same as none).
        // Returns 0 when raw is null/empty or does not resolve to a definite px value.
        public static double ResolveContainIntrinsicPx(string raw, LayoutContext ctx, double fontSize) {
            if (string.IsNullOrEmpty(raw) || raw == "none") return 0;
            raw = raw.Trim();
            // `auto <length>` — strip the leading `auto ` prefix; resolve the length token.
            if (raw.StartsWith("auto", System.StringComparison.OrdinalIgnoreCase)) {
                // Skip "auto" and any following whitespace.
                int i = 4;
                while (i < raw.Length && (raw[i] == ' ' || raw[i] == '\t')) i++;
                if (i < raw.Length) raw = raw.Substring(i);
                else return 0;
            }
            // Now `raw` is a single <length> token.  Parse it.
            if (!CssValue.TryParse(raw, out var parsed)) return 0;
            // Reject percentages per spec grammar (invalid for contain-intrinsic-*).
            if (parsed is CssPercentage || (parsed is CssLength cl && cl.Unit == CssLengthUnit.Percent)) return 0;
            var r = StyleResolver.ResolveLengthFromParsed(parsed, ctx, fontSize, null);
            if (r.Kind == StyleResolver.LengthKind.Length) return r.Pixels;
            return 0;
        }

        // Resolves the effective contain-intrinsic height (block axis) for a box.
        // Checks contain-intrinsic-height first, then the height component of
        // contain-intrinsic-size (one-value = both axes; two-value = width height).
        // Returns 0 when no hint is set (existing zero-content behaviour).
        public static double ResolveContainIntrinsicHeightPx(ComputedStyle style, LayoutContext ctx, double fontSize) {
            if (style == null) return 0;
            string h = style.Get(CssProperties.ContainIntrinsicHeightId);
            if (!string.IsNullOrEmpty(h) && h != "none") {
                return ResolveContainIntrinsicPx(h, ctx, fontSize);
            }
            string size = style.Get(CssProperties.ContainIntrinsicSizeId);
            if (string.IsNullOrEmpty(size) || size == "none") return 0;
            // contain-intrinsic-size: one value = both axes; two values = width height.
            // Extract the height component (second token, or first when only one given).
            return ResolveContainIntrinsicPxFromSize(size, second: true, ctx, fontSize);
        }

        // Resolves the effective contain-intrinsic width (inline axis) for a box.
        // Checks contain-intrinsic-width first, then the width component of
        // contain-intrinsic-size.
        public static double ResolveContainIntrinsicWidthPx(ComputedStyle style, LayoutContext ctx, double fontSize) {
            if (style == null) return 0;
            string w = style.Get(CssProperties.ContainIntrinsicWidthId);
            if (!string.IsNullOrEmpty(w) && w != "none") {
                return ResolveContainIntrinsicPx(w, ctx, fontSize);
            }
            string size = style.Get(CssProperties.ContainIntrinsicSizeId);
            if (string.IsNullOrEmpty(size) || size == "none") return 0;
            return ResolveContainIntrinsicPxFromSize(size, second: false, ctx, fontSize);
        }

        // Parses contain-intrinsic-size's shorthand token stream and extracts
        // either the first (width) or second (height) axis value.  The grammar
        // supports optional `auto` prefix on each component:
        //
        //   `100px`             → width=100  height=100  (one value, both axes)
        //   `100px 200px`       → width=100  height=200
        //   `auto 100px`        → width=100  height=100  (auto+one-value → both)
        //   `auto 100px 200px`  → width=100  height=200  (auto prefix on width)
        //
        // Strategy: strip leading `auto` tokens, then collect up to two <length>
        // tokens.  A single length means both axes share it; two lengths are
        // width then height.
        static double ResolveContainIntrinsicPxFromSize(string size, bool second, LayoutContext ctx, double fontSize) {
            // Tokenise by whitespace.
            var tokens = size.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            // Filter out `auto` modifiers; collect length tokens.
            var lengths = new System.Collections.Generic.List<string>(2);
            foreach (var tok in tokens) {
                if (tok.Equals("auto", System.StringComparison.OrdinalIgnoreCase)) continue;
                if (tok.Equals("none", System.StringComparison.OrdinalIgnoreCase)) return 0;
                lengths.Add(tok);
            }
            if (lengths.Count == 0) return 0;
            // One length = both axes share it.  Two lengths = [width, height].
            string chosen = (second && lengths.Count >= 2) ? lengths[1] : lengths[0];
            return ResolveContainIntrinsicPx(chosen, ctx, fontSize);
        }

        // Case-insensitive whitespace-or-comma token scan.  Mirrors the helper
        // in PositionedExtensions / ContainingBlockResolver but kept local so
        // ContainmentResolver is self-contained.
        static bool HasToken(string value, string token) {
            int idx = 0;
            while (idx < value.Length) {
                // Skip separators.
                while (idx < value.Length && (value[idx] == ' ' || value[idx] == ',' || value[idx] == '\t')) idx++;
                int start = idx;
                // Collect next token.
                while (idx < value.Length && value[idx] != ' ' && value[idx] != ',' && value[idx] != '\t') idx++;
                int len = idx - start;
                if (len != token.Length) continue;
                bool eq = true;
                for (int j = 0; j < len; j++) {
                    char a = value[start + j];
                    if (a >= 'A' && a <= 'Z') a = (char)(a + 32);
                    if (a != token[j]) { eq = false; break; }
                }
                if (eq) return true;
            }
            return false;
        }
    }
}
