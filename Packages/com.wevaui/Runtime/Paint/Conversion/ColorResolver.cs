using System.Collections.Generic;
using Weva.Css.Cascade;
using Weva.Css.Values;
using Weva.Diagnostics;

namespace Weva.Paint.Conversion {
    internal static class ColorResolver {
        // Per-style currentColor memo. EmitDecorations on a cache-miss box
        // calls ResolveCurrentColor from ~5 different resolvers (Border,
        // BoxShadow, BackgroundResolver, FilterResolver, ...). Each call did
        // a GetParsed + the dispatch below — alloc-free, but redundant work.
        // Keyed on (style, style.Version) — the version check protects us
        // when the cascade re-binds a pooled ComputedStyle instance to a
        // different element. The paint pipeline is single-threaded so a
        // plain static slot suffices.
        //
        // RC3: the slots are [ThreadStatic] so a future parallel LayoutEngine
        // (e.g. a second engine driving a popup document on its own thread)
        // cannot corrupt the memo — each thread gets its own one-slot cache,
        // and a single-threaded engine is unaffected (the per-thread slot is
        // allocated on first access and never observed by any other thread).
        [System.ThreadStatic] static ComputedStyle s_LastStyle;
        [System.ThreadStatic] static long s_LastVersion;
        [System.ThreadStatic] static LinearColor s_LastColor;

        // DD2/DD3 — observability for the two defensive fallbacks below. We
        // KEEP the existing fallback colors (Black for null style, currentColor
        // for parse failures) so rendering output is unchanged, but emit a
        // one-time Debug.LogWarning per unique call-site / offending value so
        // an upstream null-style bug or a malformed CSS color token doesn't
        // silently paint a plausible color with zero developer signal.
        //
        // We keep a local HashSet<string> for the dedupe + a parallel call to
        // UICssDiagnostics.Warn so the warning routes through the same
        // editor-gated diagnostic channel as the rest of the engine. The local
        // set lets ResetWarnings_TestOnly do a focused reset without disturbing
        // unrelated diagnostics state, and gives us per-call-site keying for
        // DD2 (CallerFilePath / CallerLineNumber) that UICssDiagnostics's
        // source+detail dedupe wouldn't capture on its own.
        //
        // Dedupe keys:
        //   DD2 — "DD2:" + caller-file:line  (one warning per unique call site)
        //   DD3 — "DD3:" + raw string         (one warning per unique bad token)
        // The set is process-static (the paint pipeline is single-threaded per
        // RC3) and intentionally never cleared — "once per session" matches the
        // pattern of W4/W5 in CODE_AUDIT_FINDINGS.md and prevents log spam.
        static readonly HashSet<string> s_WarnedKeys = new HashSet<string>();

        internal static void ResetWarnings_TestOnly() {
            s_WarnedKeys.Clear();
            // Also reset UICssDiagnostics so the inner LogWarning the helpers
            // route through can fire again. Tests gate on LogAssert.Expect and
            // need a clean slate per case.
            UICssDiagnostics.ResetForTests();
        }

        public static LinearColor ResolveCurrentColor(ComputedStyle style) {
            if (style == null) {
                WarnNullStyle();
                return LinearColor.Black;
            }
            if (ReferenceEquals(style, s_LastStyle) && style.Version == s_LastVersion) {
                return s_LastColor;
            }
            // Per-style parsed cache (ComputedStyle.GetParsed) returns the
            // already-built CssValue without re-running CssValue.TryParse —
            // O(1) array lookup after the first read, no dictionary probe.
            // The currentcolor / transparent keyword paths are handled by
            // pattern-matching on the cached parse tree.
            var parsed = style.GetParsed(CssProperties.ColorId);
            LinearColor resolved = LinearColor.Black;
            if (parsed != null && TryResolveParsed(parsed, LinearColor.Black, style, out var c)) {
                resolved = c;
            }
            s_LastStyle = style;
            s_LastVersion = style.Version;
            s_LastColor = resolved;
            return resolved;
        }

        // Direct-parsed-value entry point. Avoids the string-keyed
        // TryResolve+TryParse round trip when a CssValue is already in
        // hand (e.g., from ComputedStyle.GetParsed).
        public static bool TryResolveParsed(CssValue parsed, LinearColor currentColor, ComputedStyle style, out LinearColor result) {
            result = LinearColor.Transparent;
            if (parsed == null) return false;
            if (parsed is CssColor cc) {
                result = LinearColor.FromCssColor(cc);
                return true;
            }
            if (parsed is CssKeyword k) {
                string ki = k.Identifier;
                if (CssStringUtil.IsCurrentColor(ki)) { result = currentColor; return true; }
                if (ki == "transparent") { result = LinearColor.Transparent; return true; }
            }
            if (parsed is CssIdentifier id) {
                if (CssColor.TryFromName(id.Name, out var named)) {
                    result = LinearColor.FromCssColor(named);
                    return true;
                }
            }
            return false;
        }

        public static bool TryResolve(string raw, LinearColor currentColor, ComputedStyle style, out LinearColor result) {
            result = LinearColor.Transparent;
            if (string.IsNullOrEmpty(raw)) return false;
            // Fast-path the two most common keywords without allocating a
            // trimmed/lowercased substring. `currentcolor` and `transparent`
            // bypass the CssValue parser entirely. The currentcolor check
            // requires the trimmed variant (raw may have surrounding
            // whitespace), so this call does NOT route through
            // CssStringUtil.IsCurrentColor (which is exact-match only).
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "currentcolor")) { result = currentColor; return true; }
            if (CssStringUtil.EqualsIgnoreCaseTrimmed(raw, "transparent")) { result = LinearColor.Transparent; return true; }
            // CssValue.TryParse internally trims; pass raw directly. The
            // .Trim() allocation here used to be unavoidable; eliminating it
            // saves one alloc per ColorResolver call when raw was already
            // pre-trimmed (the common case for cascade-resolved values).
            if (CssValue.TryParse(raw, out var parsed)) {
                if (parsed is CssColor cc) {
                    result = LinearColor.FromCssColor(cc);
                    return true;
                }
                if (parsed is CssKeyword k) {
                    string ki = k.Identifier;
                    if (CssStringUtil.IsCurrentColor(ki)) { result = currentColor; return true; }
                    if (ki == "transparent") { result = LinearColor.Transparent; return true; }
                }
                if (parsed is CssIdentifier id) {
                    // CssNamedColors.TryGet is case-insensitive at the byte
                    // level, so we don't need to lowercase here. The prior
                    // .ToLowerInvariant() allocation is gone.
                    if (CssColor.TryFromName(id.Name, out var named)) {
                        result = LinearColor.FromCssColor(named);
                        return true;
                    }
                }
            }
            return false;
        }

        public static LinearColor Resolve(string raw, ComputedStyle style) {
            var current = ResolveCurrentColor(style);
            if (TryResolve(raw, current, style, out var c)) return c;
            // DD3 — parse failed and we are about to silently return the
            // currentColor fallback. Warn once per unique offending token so
            // the bug is visible without changing rendered output. Skip the
            // null/empty case (TryResolve early-returns on it and the caller
            // typically passes null intentionally for unset properties).
            if (!string.IsNullOrEmpty(raw)) WarnParseFailure(raw);
            return current;
        }

        static void WarnNullStyle(
            [System.Runtime.CompilerServices.CallerFilePath] string file = "",
            [System.Runtime.CompilerServices.CallerLineNumber] int line = 0) {
            // Key on caller file:line so a null-style bug at site A and a
            // separate one at site B both surface, while a hot loop repeatedly
            // hitting the same site only logs once.
            string key = "DD2:" + file + ":" + line;
            if (!s_WarnedKeys.Add(key)) return;
            UICssDiagnostics.Warn(
                "ColorResolver",
                "DD2: ResolveCurrentColor called with null style; returning " +
                "LinearColor.Black as a defensive fallback (rendered color is NOT " +
                "the cascade's resolved color). Call site: " + file + ":" + line);
        }

        static void WarnParseFailure(string raw) {
            // Key on the raw token so a single bad value in a stylesheet logs
            // once no matter how many boxes consume it.
            string key = "DD3:" + raw;
            if (!s_WarnedKeys.Add(key)) return;
            UICssDiagnostics.Warn(
                "ColorResolver",
                "DD3: failed to parse color '" + raw + "'; falling back to currentColor. " +
                "The rendered color is a defensive fallback — check the CSS source for " +
                "a typo or unsupported syntax.");
        }
    }
}
