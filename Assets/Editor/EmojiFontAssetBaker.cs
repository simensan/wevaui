// EmojiFontAssetBaker
//
// Bakes a TMP_FontAsset for Segoe UI Emoji (or a fallback emoji font) so the
// runtime fallback chain registered by UitestController can resolve emoji
// codepoints (U+1F300+, U+2600+, etc.) instead of dropping them.
//
// Strategy:
//  1. Copy C:/Windows/Fonts/seguiemj.ttf into Assets/UI/Fonts/SegoeUIEmoji.ttf
//     (Unity's TMP CreateFontAsset(Font) requires a Font asset reference).
//  2. Import as Font.
//  3. Try GlyphRenderMode.COLOR first (gives true Chrome-like color emoji on
//     Unity 6 / TMP 2.x — TextCore 1.5+ supports COLR/CPAL fonts).
//     Fall back to SDFAA monochrome if COLOR fails.
//  4. Allocate a 2048x2048 atlas. The randhtml demo bakes ~1500 emoji
//     glyphs (curated ranges below); at 90pt SDFAA padding=5 they fit in
//     ~25% of a 4096 atlas. 2048 is the smallest power-of-two with multi-
//     atlas support that contains the full curated set. (1024 overflows.)
//  5. Pre-bake the typical Unicode emoji ranges so first-frame text doesn't
//     stall on dynamic glyph rasterization.
//  6. Save as Assets/UI/Fonts/SegoeUIEmoji SDF.asset.

using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace Weva.EditorTools {
    public static class EmojiFontAssetBaker {
        const string SegoeEmojiSystemPath = @"C:\Windows\Fonts\seguiemj.ttf";
        const string SegoeSymSystemPath   = @"C:\Windows\Fonts\seguisym.ttf";
        const string FontsFolder          = "Assets/UI/Fonts";
        const string OutputAssetPath      = "Assets/UI/Fonts/SegoeUIEmoji SDF.asset";
        const string OutputColorAssetPath = "Assets/UI/Fonts/SegoeUIEmoji COLOR.asset";

        // Emoji + symbol ranges. Anything not in these ranges that the
        // primary face also lacks will simply not render — that's fine; the
        // demo's content sits inside these ranges.
        static readonly (uint Lo, uint Hi)[] Ranges = new (uint, uint)[] {
            (0x2300, 0x23FF), // Miscellaneous Technical (⌘ ⌚ ⏰ ⤾ etc.)
            (0x2500, 0x257F), // Box drawing
            (0x2580, 0x259F), // Block elements
            (0x25A0, 0x25FF), // Geometric shapes
            (0x2600, 0x26FF), // Misc Symbols (☀ ☂ ⚔ ⚡ ⚙ ☠ etc.)
            (0x2700, 0x27BF), // Dingbats (✂ ✈ ✉ ✏ ✓ ✔ ✗ ✘ etc.)
            (0x2B00, 0x2BFF), // Misc symbols & arrows
            (0x1F300, 0x1F5FF), // Misc Symbols and Pictographs
            (0x1F600, 0x1F64F), // Emoticons
            (0x1F680, 0x1F6FF), // Transport
            (0x1F700, 0x1F77F), // Alchemical
            (0x1F780, 0x1F7FF), // Geometric extended
            (0x1F800, 0x1F8FF), // Supplemental arrows-C
            (0x1F900, 0x1F9FF), // Supplemental symbols
            (0x1FA00, 0x1FAFF), // Symbols & pictographs ext-A
        };

        [MenuItem("Weva/Bake Emoji Font Asset")]
        public static void Bake() {
            BakeImpl();
        }

        // COLOR variant — bakes Segoe UI Emoji as a 4-channel RGBA atlas using
        // GlyphRenderMode.COLOR (TextCore 1.5+). Used by the renderer's
        // _TEXT_COLOR shader path, which samples the RGBA texture directly
        // instead of doing an SDF coverage-threshold like the SDFAA path.
        // Output is `Assets/UI/Fonts/SegoeUIEmoji COLOR.asset` so the SDFAA
        // asset stays available as a fallback authoring option.
        [MenuItem("Weva/Bake Emoji Font Asset (Color)")]
        public static void BakeColor() {
            BakeColorImpl();
        }

        public static TMP_FontAsset BakeImpl() {
            Directory.CreateDirectory(FontsFolder);

            // Step 1 — copy a usable .ttf into the project. Try Segoe UI Emoji
            // first (color font); if not present try seguisym.ttf (mono).
            string projectFontPath = null;
            string sourceLabel = null;
            if (File.Exists(SegoeEmojiSystemPath)) {
                projectFontPath = $"{FontsFolder}/SegoeUIEmoji.ttf";
                sourceLabel = "Segoe UI Emoji";
                CopyIfNewer(SegoeEmojiSystemPath, projectFontPath);
            } else if (File.Exists(SegoeSymSystemPath)) {
                projectFontPath = $"{FontsFolder}/SegoeUISymbol.ttf";
                sourceLabel = "Segoe UI Symbol";
                CopyIfNewer(SegoeSymSystemPath, projectFontPath);
            } else {
                Debug.LogError("[EmojiFontAssetBaker] No usable emoji TTF found in C:/Windows/Fonts (tried seguiemj.ttf, seguisym.ttf).");
                return null;
            }

            AssetDatabase.ImportAsset(projectFontPath, ImportAssetOptions.ForceUpdate);
            var font = AssetDatabase.LoadAssetAtPath<Font>(projectFontPath);
            if (font == null) {
                Debug.LogError($"[EmojiFontAssetBaker] Failed to load Font at {projectFontPath}");
                return null;
            }

            FontEngine.InitializeFontEngine();

            // Step 2 — bake SDFAA. The Weva runtime renders TMP atlases
            // through its Weva_Text shader, which expects an SDF/alpha
            // texture, not the RGBA32 atlas that GlyphRenderMode.COLOR
            // produces. Color emoji rendering would require a dedicated
            // sampler path; for now we trade fidelity for compatibility and
            // produce monochrome emoji glyphs that the existing pipeline can
            // already shade and tint.
            TMP_FontAsset fontAsset = TryCreate(font, GlyphRenderMode.SDFAA, out string renderModeUsed);
            if (fontAsset == null) {
                Debug.LogError("[EmojiFontAssetBaker] Could not create a TMP_FontAsset for " + sourceLabel);
                return null;
            }

            fontAsset.name = "SegoeUIEmoji SDF";

            // Step 3 — save the SO + sub-assets to disk.
            // The CreateFontAsset path returns a brand-new in-memory SO; we
            // wrap it with AssetDatabase.CreateAsset and re-attach the atlas
            // texture + material as sub-assets so a single .asset file holds
            // everything (matches the layout of LiberationSans SDF.asset).
            if (File.Exists(OutputAssetPath)) AssetDatabase.DeleteAsset(OutputAssetPath);
            AssetDatabase.CreateAsset(fontAsset, OutputAssetPath);

            // Add the main atlas texture + material as sub-assets if they're
            // not already part of the asset (CreateFontAsset(Font) creates a
            // standalone instance, so the sub-asset graph is empty).
            if (fontAsset.atlasTextures != null) {
                for (int i = 0; i < fontAsset.atlasTextures.Length; i++) {
                    var tex = fontAsset.atlasTextures[i];
                    if (tex == null) continue;
                    if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(tex))) {
                        tex.name = "SegoeUIEmoji Atlas " + i;
                        AssetDatabase.AddObjectToAsset(tex, fontAsset);
                    }
                }
            }
            if (fontAsset.material != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(fontAsset.material))) {
                fontAsset.material.name = "SegoeUIEmoji Material";
                AssetDatabase.AddObjectToAsset(fontAsset.material, fontAsset);
            }

            // Step 4 — pre-bake known emoji ranges so the runtime doesn't
            // stall on first-frame dynamic glyph rasterization. Walk each
            // range and attempt to add every codepoint; misses are silently
            // ignored (the chain walks to the next fallback face).
            //
            // CRITICAL: TMP_FontAsset.TryAddCharacters(uint[]) expects the
            // input to be UTF-16 code units (so supplementary-plane
            // codepoints must arrive as surrogate-pair pairs that
            // TMP_FontAssetUtilities.GetCodePoint will recombine). Passing
            // 0x1F409 directly silently fails to find a glyph because the
            // internal GetCodePoint reads the value as a single utf-16 unit
            // and gets the high bits truncated to 0xF409 (a private-use
            // codepoint that the emoji font doesn't contain). We expand
            // every codepoint above U+FFFF into the matching pair.
            var unicodes = new List<uint>(16384);
            foreach (var (lo, hi) in Ranges) {
                for (uint cp = lo; cp <= hi; cp++) AppendCodepoint(unicodes, cp);
            }
            // Specific named codepoints used by the randhtml demo, in case
            // any sit outside the ranges above. Includes ZWJ (U+200D) and
            // VS-16 (U+FE0F) which Chrome uses for ️🛡️ etc.
            uint[] extras = new uint[] {
                0x2694, 0x26A1, 0x2728, 0x1F6E1, 0x1F409, 0x1F525, 0x1F4A7, 0x1F4A8,
                0x1F31F, 0x2B50, 0x2705, 0x274C, 0x1F3AF, 0xFE0F, 0x200D, 0x2B06,
                0x2B07, 0x2B05, 0x27A1, 0x21A9, 0x21AA,
            };
            foreach (var cp in extras) AppendCodepoint(unicodes, cp);

            uint[] missing;
            bool addOk = fontAsset.TryAddCharacters(unicodes.ToArray(), out missing, includeFontFeatures: false);
            int missingCount = missing == null ? 0 : missing.Length;
            int addedCount = unicodes.Count - missingCount;

            EditorUtility.SetDirty(fontAsset);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[EmojiFontAssetBaker] Baked '{sourceLabel}' -> {OutputAssetPath}. Mode={renderModeUsed}, Added={addedCount}, Missing={missingCount}, AtlasSize={fontAsset.atlasWidth}x{fontAsset.atlasHeight}.");
            return fontAsset;
        }

        // Append a Unicode codepoint to the buffer, expanding supplementary
        // plane (>U+FFFF) values into UTF-16 surrogate pairs. See the
        // commentary at the call site for why this is required.
        static void AppendCodepoint(List<uint> sink, uint cp) {
            if (cp > 0xFFFF) {
                uint v = cp - 0x10000;
                sink.Add(0xD800u + (v >> 10));
                sink.Add(0xDC00u + (v & 0x3FFu));
            } else {
                sink.Add(cp);
            }
        }

        static void CopyIfNewer(string sysPath, string projectPath) {
            try {
                if (!File.Exists(projectPath) || File.GetLastWriteTimeUtc(sysPath) > File.GetLastWriteTimeUtc(projectPath)) {
                    File.Copy(sysPath, projectPath, overwrite: true);
                }
            } catch (Exception e) {
                Debug.LogWarning("[EmojiFontAssetBaker] Copy failed: " + e.Message);
            }
        }

        // Color implementation. Mirrors BakeImpl() but uses GlyphRenderMode.COLOR
        // and a different output path. The COLOR path expects the source font to
        // contain a COLR/CPAL or sbix table — Segoe UI Emoji ships with COLR/CPAL
        // on Windows 10+, so this works on most users' systems. If COLOR fails
        // (e.g. older TextCore or a font without color tables) the method logs
        // and returns null without touching the SDFAA asset.
        public static TMP_FontAsset BakeColorImpl() {
            Directory.CreateDirectory(FontsFolder);

            string projectFontPath = null;
            string sourceLabel = null;
            if (File.Exists(SegoeEmojiSystemPath)) {
                projectFontPath = $"{FontsFolder}/SegoeUIEmoji.ttf";
                sourceLabel = "Segoe UI Emoji";
                CopyIfNewer(SegoeEmojiSystemPath, projectFontPath);
            } else {
                Debug.LogError("[EmojiFontAssetBaker] Color bake requires seguiemj.ttf with COLR/CPAL tables; not found in C:/Windows/Fonts.");
                return null;
            }

            AssetDatabase.ImportAsset(projectFontPath, ImportAssetOptions.ForceUpdate);
            var font = AssetDatabase.LoadAssetAtPath<Font>(projectFontPath);
            if (font == null) {
                Debug.LogError($"[EmojiFontAssetBaker] Failed to load Font at {projectFontPath}");
                return null;
            }

            FontEngine.InitializeFontEngine();

            // GlyphRenderMode.COLOR (= 0x10 in TextCore 1.5+ flag layout) bakes
            // an RGBA32 page populated by the OS color glyph rasterizer. No SDF
            // spread is needed — pixel-art-style sampling — so atlasPadding = 0.
            // Sampling at 64pt produces ~64x64 emoji rasters which fit ~900
            // emoji into a 2048 page; that covers the curated demo set with
            // multi-atlas support enabled as a safety net.
            TMP_FontAsset fontAsset = TryCreateColor(font, out string renderModeUsed);
            if (fontAsset == null) {
                Debug.LogError("[EmojiFontAssetBaker] Could not create a COLOR TMP_FontAsset for " + sourceLabel + ". The source font may lack COLR/CPAL/sbix tables, or TextCore is older than 1.5.");
                return null;
            }

            fontAsset.name = "SegoeUIEmoji COLOR";

            if (File.Exists(OutputColorAssetPath)) AssetDatabase.DeleteAsset(OutputColorAssetPath);
            AssetDatabase.CreateAsset(fontAsset, OutputColorAssetPath);

            if (fontAsset.atlasTextures != null) {
                for (int i = 0; i < fontAsset.atlasTextures.Length; i++) {
                    var tex = fontAsset.atlasTextures[i];
                    if (tex == null) continue;
                    if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(tex))) {
                        tex.name = "SegoeUIEmoji Color Atlas " + i;
                        AssetDatabase.AddObjectToAsset(tex, fontAsset);
                    }
                }
            }
            if (fontAsset.material != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(fontAsset.material))) {
                fontAsset.material.name = "SegoeUIEmoji Color Material";
                AssetDatabase.AddObjectToAsset(fontAsset.material, fontAsset);
            }

            // Pre-bake known emoji ranges, same as the SDFAA path.
            var unicodes = new List<uint>(16384);
            foreach (var (lo, hi) in Ranges) {
                for (uint cp = lo; cp <= hi; cp++) AppendCodepoint(unicodes, cp);
            }
            uint[] extras = new uint[] {
                0x2694, 0x26A1, 0x2728, 0x1F6E1, 0x1F409, 0x1F525, 0x1F4A7, 0x1F4A8,
                0x1F31F, 0x2B50, 0x2705, 0x274C, 0x1F3AF, 0xFE0F, 0x200D, 0x2B06,
                0x2B07, 0x2B05, 0x27A1, 0x21A9, 0x21AA,
            };
            foreach (var cp in extras) AppendCodepoint(unicodes, cp);

            uint[] missing;
            bool addOk = fontAsset.TryAddCharacters(unicodes.ToArray(), out missing, includeFontFeatures: false);
            int missingCount = missing == null ? 0 : missing.Length;
            int addedCount = unicodes.Count - missingCount;

            EditorUtility.SetDirty(fontAsset);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[EmojiFontAssetBaker] Baked '{sourceLabel}' -> {OutputColorAssetPath}. Mode={renderModeUsed}, Added={addedCount}, Missing={missingCount}, AtlasSize={fontAsset.atlasWidth}x{fontAsset.atlasHeight}.");
            return fontAsset;
        }

        static TMP_FontAsset TryCreateColor(Font font, out string usedLabel) {
            usedLabel = "COLOR";
            try {
                // 64pt sampling at padding 0 — color glyphs need no SDF spread;
                // multi-atlas support is on so coverage of the full curated
                // emoji set isn't bounded by a single page.
                var asset = TMP_FontAsset.CreateFontAsset(
                    font,
                    samplingPointSize: 64,
                    atlasPadding: 0,
                    renderMode: GlyphRenderMode.COLOR,
                    atlasWidth: 2048,
                    atlasHeight: 2048,
                    atlasPopulationMode: AtlasPopulationMode.Dynamic,
                    enableMultiAtlasSupport: true);
                if (asset == null) return null;
                return asset;
            } catch (Exception e) {
                Debug.LogWarning($"[EmojiFontAssetBaker] CreateFontAsset(COLOR) threw: {e.Message}");
                return null;
            }
        }

        static TMP_FontAsset TryCreate(Font font, GlyphRenderMode mode, out string usedLabel) {
            usedLabel = mode.ToString();
            try {
                // 2048x2048 single-page atlas at 32pt sampling. Each emoji
                // glyph is ~36px square at this sampling, so the curated
                // ~1500-glyph set packs into ~1.9M px — well under a 2048
                // atlas's 4.2M px. SDFAA scales smoothly; 32pt source still
                // renders crisply at the demo's 24-32px text sizes. Multi-
                // atlas stays enabled as a safety net but is expected to
                // remain a single page in practice. Asset drops from
                // ~33MB (4096 single page at 90pt) to ~4MB.
                var asset = TMP_FontAsset.CreateFontAsset(
                    font,
                    samplingPointSize: 32,
                    atlasPadding: 3,
                    renderMode: mode,
                    atlasWidth: 2048,
                    atlasHeight: 2048,
                    atlasPopulationMode: AtlasPopulationMode.Dynamic,
                    enableMultiAtlasSupport: true);
                if (asset == null) return null;
                // atlasRenderMode is read-only post-creation; CreateFontAsset
                // already wires it from the renderMode argument.
                return asset;
            } catch (Exception e) {
                Debug.LogWarning($"[EmojiFontAssetBaker] CreateFontAsset({mode}) threw: {e.Message}");
                return null;
            }
        }
    }
}
