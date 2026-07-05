using System;
using System.Collections.Generic;
using Weva.Paint;
using Weva.Text.TextCore;

namespace Weva.Text.Sdf {
    // FontLoader resolves a (family, style, weight) triple to a loaded FaceInfo.
    //
    // Resolution order:
    //   1. Resources/Fonts/{family}-{weight}{style}.ttf (Resources.Load on Unity).
    //   2. StreamingAssets/Fonts/{family}-{weight}{style}.ttf
    //   3. Bundled package default ({Packages/com.wevaui/Runtime/Resources/Fonts/Weva-Default.ttf}),
    //      registered by FontEngineWarmup at boot.
    //   4. Font.CreateDynamicFontFromOSFont(family, 16) for system fonts.
    //
    // The pure-C# layer (this file) tracks the (family, style, weight) -> FaceInfo
    // cache and the per-face atlas warmup flag. The Unity-side resolver (loader
    // partial under #if UNITY_2023_1_OR_NEWER) does the actual Font creation +
    // FontEngine.LoadFontFace(Font) call, which on Unity 6000.4.x is the
    // documented entry point. For headless tests we inject IFaceLoader.
    //
    // Caching: a successful Load is permanent for the process lifetime; invalidate
    // via ClearCache() when the registered fonts change.
    public sealed partial class FontLoader {
        public interface IFaceLoader {
            // Resolves a logical (family, style, weight) request to a FaceInfo
            // the active ITextCoreBackend can load. Implementations should also
            // ensure FontEngine has the face registered (via Font, byte[], or
            // path) so subsequent advance/raster calls succeed.
            bool TryLoad(string family, FontStyle style, int weight, out FaceInfo face);
        }

        readonly IFaceLoader faceLoader;
        readonly ITextCoreBackend backend;
        readonly Dictionary<CacheKey, FaceInfo> cache = new();
        readonly HashSet<FaceInfo> warmedFaces = new();

        public int CachedFaceCount => cache.Count;
        public int WarmedFaceCount => warmedFaces.Count;

        // Range we warm at FontLoader.Load time. ASCII printable + space matches
        // the brief: covers the demo's typography card and almost all latin UI.
        public uint WarmRangeStart { get; set; } = 0x20;
        public uint WarmRangeEnd { get; set; } = 0x7E;
        public double WarmFontSize { get; set; } = 16.0;

        public FontLoader(IFaceLoader faceLoader, ITextCoreBackend backend) {
            this.faceLoader = faceLoader;
            this.backend = backend;
        }

        public FaceInfo Load(string family, FontStyle style, int weight) {
            if (string.IsNullOrEmpty(family)) family = "sans-serif";
            if (weight <= 0) weight = 400;
            var key = new CacheKey(family, style, weight);
            if (cache.TryGetValue(key, out var cached)) return cached;

            // CSS font-family is a comma-separated stack with quoted names
            // allowed. Walk the stack in order; first family that resolves to
            // a valid FaceInfo wins. If none do, fall back to the bundled
            // generic ("sans-serif") so we never return an invalid face just
            // because the author wrote a fancy stack.
            FaceInfo face = FaceInfo.Empty;
            foreach (string fam in EnumerateFamilyStack(family)) {
                if (faceLoader != null) {
                    if (faceLoader.TryLoad(fam, style, weight, out face) && face.IsValid) break;
                }
                // Distinguish a GENUINE FontResolver registration (TryResolve)
                // from Resolve's silent default-fallback (which always returns a
                // valid face). If this named family isn't a system font, isn't
                // registered here, isn't a CSS generic, and isn't a TMP face
                // (those resolve on the TMP path and never reach this loader),
                // warn once so authors know the font they asked for is missing.
                bool resolverHas = FontResolver.TryResolve(fam, out _);
                if (!resolverHas && !IsGenericFamily(fam) && !IsRegisteredElsewhere(fam)) {
                    Weva.Diagnostics.UICssDiagnostics.Warn("font-not-found",
                        "font-family '" + fam + "' could not be resolved (not a system font and not " +
                        "registered via TmpFontAssetRegistry.RegisterFontAsset or FontResolver.RegisterFont) — " +
                        "falling back to the next family in the stack");
                }
                face = FontResolver.Resolve(new FontHandle(fam, 0, weight, style));
                if (face.IsValid) break;
            }
            if (!face.IsValid) {
                // Final fallback: bundled default registered as "sans-serif".
                if (faceLoader != null) faceLoader.TryLoad("sans-serif", style, weight, out face);
                if (!face.IsValid) face = FontResolver.Resolve(new FontHandle("sans-serif", 0, weight, style));
            }
            cache[key] = face;
            if (face.IsValid) WarmAtlas(face);
            return face;
        }

        // Splits a CSS font-family value into individual family names. Strips
        // surrounding quotes, trims whitespace, and skips empties. Robust to
        // commas inside quoted names.
        static IEnumerable<string> EnumerateFamilyStack(string raw) {
            if (string.IsNullOrEmpty(raw)) yield break;
            int i = 0;
            int n = raw.Length;
            var sb = new System.Text.StringBuilder();
            while (i < n) {
                while (i < n && char.IsWhiteSpace(raw[i])) i++;
                if (i >= n) break;
                sb.Clear();
                char first = raw[i];
                if (first == '"' || first == '\'') {
                    char quote = first;
                    i++;
                    while (i < n && raw[i] != quote) {
                        sb.Append(raw[i]);
                        i++;
                    }
                    if (i < n) i++; // consume closing quote
                } else {
                    while (i < n && raw[i] != ',') {
                        sb.Append(raw[i]);
                        i++;
                    }
                }
                while (i < n && raw[i] != ',') i++;
                if (i < n) i++; // skip comma
                string seg = sb.ToString().Trim();
                if (seg.Length > 0) yield return seg;
            }
        }

        // CSS generic font keywords (+ the wide CSS-wide keywords). These are
        // SUPPOSED to fall through to the engine default, so a miss on them is
        // not author error — never warn.
        static bool IsGenericFamily(string fam) {
            if (string.IsNullOrEmpty(fam)) return true;
            switch (fam.Trim().ToLowerInvariant()) {
                case "sans-serif": case "serif": case "monospace":
                case "cursive": case "fantasy": case "system-ui":
                case "ui-sans-serif": case "ui-serif": case "ui-monospace":
                case "ui-rounded": case "math": case "emoji": case "fangsong":
                case "-apple-system": case "blinkmacsystemfont":
                case "inherit": case "initial": case "unset": case "revert": case "revert-layer":
                    return true;
                default: return false;
            }
        }

        // A family registered with a TMP_FontAsset resolves on the TMP shaping
        // path, which never reaches this loader — so don't flag it as missing.
        static bool IsRegisteredElsewhere(string fam) {
#if UNITY_2023_1_OR_NEWER && WEVA_TMP
            return Weva.Text.Tmp.TmpFontAssetRegistry.IsRegistered(fam);
#else
            return false;
#endif
        }

        public void ClearCache() {
            cache.Clear();
            warmedFaces.Clear();
        }

        void WarmAtlas(FaceInfo face) {
            if (!warmedFaces.Add(face)) return;
            if (backend == null) return;
            if (!backend.LoadFace(face, out _)) return;
            // Force-warm the configured range. On Unity this triggers the
            // FontAsset's atlas re-bake exactly once (per face, per process).
            // Each TryGetGlyphAdvance call also walks the glyph index table,
            // which is the cheapest way to populate the per-codepoint cache
            // without rasterizing.
            for (uint cp = WarmRangeStart; cp <= WarmRangeEnd; cp++) {
                backend.TryGetGlyphAdvance(face, cp, WarmFontSize, out _);
            }
        }

        readonly struct CacheKey : IEquatable<CacheKey> {
            public readonly string Family;
            public readonly FontStyle Style;
            public readonly int Weight;

            public CacheKey(string family, FontStyle style, int weight) {
                Family = family ?? string.Empty;
                Style = style;
                Weight = weight;
            }

            public bool Equals(CacheKey other) {
                return string.Equals(Family, other.Family, StringComparison.OrdinalIgnoreCase)
                    && Style == other.Style && Weight == other.Weight;
            }

            public override bool Equals(object obj) => obj is CacheKey k && Equals(k);

            public override int GetHashCode() {
                unchecked {
                    int h = Family != null ? StringComparer.OrdinalIgnoreCase.GetHashCode(Family) : 0;
                    h = (h * 397) ^ (int)Style;
                    h = (h * 397) ^ Weight;
                    return h;
                }
            }
        }
    }
}
