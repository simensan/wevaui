#if UNITY_2023_1_OR_NEWER
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Weva.Paint;
using Weva.Rendering.URP;
using Weva.Text.Sdf;
using Weva.Text.TextCore;
using PaintRect = Weva.Paint.Rect;

namespace Weva.Text.Atg {
    // AtgGlyphAtlasAdapter — produces hinted-bitmap glyph quads by driving
    // Unity's high-level TextGenerator + TextInfo pipeline via reflection.
    // GetTextGenerator() → GenerateText(settings, textInfo) populates
    // TextInfo.textElementInfo[] with per-glyph data: codepoint, owning
    // FontAsset (primary or any fallback), and screen-space quad corners.
    // UVs come from fontAsset.characterLookupTable rather than the vertex
    // data because TextGenerator's UVs go stale after atlas re-pack
    // mid-shape; the lookup table is always live.
    //
    // Why not TextLib.GenerateText directly: TextLib is a lower-level
    // shaper that doesn't drive font-asset preparation, glyph
    // rasterization, or atlas population. TextGenerator wraps that whole
    // pipeline in one call.
    //
    // Constraints (deferred):
    //   - Letter-spacing — passed through TextGenerationSettings.characterSpacing
    //     but units may not match our CSS px exactly; needs measurement.
    public sealed class AtgGlyphAtlasAdapter : IGlyphAtlasWithId, IGlyphAtlasVersioned, IGlyphAtlasPreparer, IGlyphAtlasTextRunSnapshotPolicy {
        // The TextCore FontAsset (UnityEngine.TextCore.Text.FontAsset).
        // Typed as Object because the type is in an internal-ish module;
        // we read it via reflection in TryShape. The caller is responsible
        // for creating one via FontAsset.CreateFontAsset(Font).
        public UnityEngine.Object FontAsset { get; set; }
        public UnityEngine.Object SemiboldFontAsset { get; set; }
        public UnityEngine.Object BoldFontAsset { get; set; }
        public bool EnableSmallTextCoverage { get; set; } = true;
        public int SmallTextCoverageMaxSize { get; set; } = 20;

        // Global, scoped kill-switch for the hinted small-text COVERAGE path.
        // Hinted coverage bitmaps only render correctly pixel-exact; the editor
        // panel host (WevaEditorPanel) draws through a resampled RenderTexture,
        // which ruins the hinting for small text (uneven strokes / baseline jitter
        // / clipping). The panel sets this true around its synchronous paint so
        // small chrome text falls to the uniform SDFAA path; play-mode rendering
        // leaves it false so game UI keeps crisp hinted coverage drawn to screen.
        public static bool SuppressSmallTextCoverage;

        // Optional TextSettings (UnityEngine.TextCore.Text.TextSettings).
        // When null we look up the project's Editor Text Settings on
        // first use. TextSettings drives the fallback chain Unity uses
        // for characters the primary FontAsset doesn't cover (CJK,
        // emoji, etc.).
        public UnityEngine.Object TextSettings { get; set; }

        public int ShapeHits => shapeHits;
        public int ShapeFailures => shapeFailures;
        public long Version {
            get {
                ObserveAtlasFingerprint();
                return atlasRevision;
            }
        }
        public bool UseTextRunSnapshots => true;
        int shapeHits;
        int shapeFailures;
        long atlasRevision;
        int observedAtlasFingerprint = int.MinValue;
        readonly HashSet<PreparedTextKey> preparedTextKeys = new HashSet<PreparedTextKey>();

        public static bool IsAvailable {
            get {
                EnsureBindings();
                return !bindingFailed;
            }
        }

        // Reset latches so callers can retry after fixing config. Useful
        // for editor tests / hot-reload scenarios.
        public static void ResetBindings() {
            bindingFailed = false;
            bindingAttempted = false;
        }

        readonly Dictionary<CoverageFontKey, UnityEngine.TextCore.Text.FontAsset> coverageFontAssets
            = new Dictionary<CoverageFontKey, UnityEngine.TextCore.Text.FontAsset>();
        readonly Dictionary<CoverageFontKey, UnityEngine.TextCore.Text.FontAsset> textSymbolFontAssets
            = new Dictionary<CoverageFontKey, UnityEngine.TextCore.Text.FontAsset>();
        readonly HashSet<int> coverageFontAssetIdentities = new HashSet<int>();

        // Revision the prepared-text dedup set was built against. The set
        // encodes "this (asset, text) pair already had TryAddCharacters run
        // while the atlas looked like revision R" — so it stays valid for as
        // long as the atlas is unchanged. Clearing it every frame (the old
        // behaviour) forced PrepareText to re-run the reflection
        // TryAddCharacters invoke + TWO fingerprint walks per text run per
        // repaint frame — on a 405-run page that alone burned milliseconds
        // per scrolled frame without ever adding a character.
        long preparedAtRevision = -1;

        public void BeginPrepareText() {
            // Frame-memoized; detects external atlas mutations at most once
            // per frame and bumps atlasRevision if anything moved.
            ObserveAtlasFingerprint();
            if (preparedAtRevision != atlasRevision) {
                preparedTextKeys.Clear();
                preparedAtRevision = atlasRevision;
            }
        }

        public void ClearSmallTextCoverageCache() {
            foreach (var kv in coverageFontAssets) {
                var fa = kv.Value;
                if (fa == null) continue;
                if (Application.isPlaying) UnityEngine.Object.Destroy(fa);
                else UnityEngine.Object.DestroyImmediate(fa);
            }
            foreach (var kv in textSymbolFontAssets) {
                var fa = kv.Value;
                if (fa == null) continue;
                if (Application.isPlaying) UnityEngine.Object.Destroy(fa);
                else UnityEngine.Object.DestroyImmediate(fa);
            }
            coverageFontAssets.Clear();
            textSymbolFontAssets.Clear();
            coverageFontAssetIdentities.Clear();
            atlasRevision++;
            observedAtlasFingerprint = CurrentAtlasFingerprint();
        }

        // Frame memo for the fingerprint walk below. The full fingerprint
        // touches every font asset's character table, atlas textures, and the
        // whole fallback table — engine-object getters that also CLONE arrays
        // (fa.atlasTextures). Version is consulted by
        // SynchronizeSnapshotCacheWithAtlas for EVERY DrawTextCommand, so an
        // unmemoized walk costs O(text runs × font assets) per repaint frame —
        // measured ~14 ms + ~1 MB of array clones on a 405-run page while
        // scrolling, dwarfing the actual batching work. Adapter-initiated
        // mutations bump atlasRevision eagerly (TryAddCharactersAndTrack /
        // ClearSmallTextCoverageCache), so the per-read walk exists only to
        // catch EXTERNAL mutations of the shared font assets — once per frame
        // is enough for that.
        int fingerprintObservedFrame = -1;

        void ObserveAtlasFingerprint() {
            int frame = UnityEngine.Time.frameCount;
            if (frame == fingerprintObservedFrame) return;
            fingerprintObservedFrame = frame;
            int fingerprint = CurrentAtlasFingerprint();
            if (observedAtlasFingerprint == int.MinValue) {
                observedAtlasFingerprint = fingerprint;
            } else if (observedAtlasFingerprint != fingerprint) {
                observedAtlasFingerprint = fingerprint;
                atlasRevision++;
            }
        }

        void TryAddCharactersAndTrack(UnityEngine.Object asset, string text) {
            if (tryAddCharactersMethod == null || asset == null) return;
            int before = CurrentAtlasFingerprint();
            tryAddCharactersMethod.Invoke(asset, new object[] { text, false });
            int after = CurrentAtlasFingerprint();
            if (after != before) {
                observedAtlasFingerprint = after;
                atlasRevision++;
            }
        }

        int CurrentAtlasFingerprint() {
            unchecked {
                int h = 17;
                h = (h * 397) ^ FontAssetFingerprint(FontAsset);
                h = (h * 397) ^ FontAssetFingerprint(SemiboldFontAsset);
                h = (h * 397) ^ FontAssetFingerprint(BoldFontAsset);
                foreach (var kv in coverageFontAssets) {
                    h = (h * 397) ^ FontAssetFingerprint(kv.Value);
                }
                foreach (var kv in atgAtlasCache) {
                    h = (h * 397) ^ TextureFingerprint(kv.Key);
                }
                return h;
            }
        }

        static int FontAssetFingerprint(UnityEngine.Object asset) {
            try {
                var fa = asset as UnityEngine.TextCore.Text.FontAsset;
                if (fa == null) return 0;
                unchecked {
                    int h = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(fa);
                    h = (h * 397) ^ fa.atlasPadding;
                    h = (h * 397) ^ (fa.characterLookupTable != null ? fa.characterLookupTable.Count : 0);
                    var atlases = fa.atlasTextures;
                    h = (h * 397) ^ (atlases != null ? atlases.Length : 0);
                    if (atlases != null) {
                        for (int i = 0; i < atlases.Length; i++) h = (h * 397) ^ TextureFingerprint(atlases[i]);
                    }
                    var fallbacks = fa.fallbackFontAssetTable;
                    h = (h * 397) ^ (fallbacks != null ? fallbacks.Count : 0);
                    if (fallbacks != null) {
                        for (int i = 0; i < fallbacks.Count; i++) {
                            var fb = fallbacks[i];
                            if (fb == null) continue;
                            h = (h * 397) ^ System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(fb);
                            h = (h * 397) ^ (fb.characterLookupTable != null ? fb.characterLookupTable.Count : 0);
                            var fbAtlases = fb.atlasTextures;
                            h = (h * 397) ^ (fbAtlases != null ? fbAtlases.Length : 0);
                            if (fbAtlases != null) {
                                for (int j = 0; j < fbAtlases.Length; j++) h = (h * 397) ^ TextureFingerprint(fbAtlases[j]);
                            }
                        }
                    }
                    return h;
                }
            } catch {
                return 0;
            }
        }

        static int TextureFingerprint(Texture2D tex) {
            if (tex == null) return 0;
            unchecked {
                try {
                    int h = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(tex);
                    h = (h * 397) ^ tex.width;
                    h = (h * 397) ^ tex.height;
                    h = (h * 397) ^ (int)tex.format;
                    return h;
                } catch {
                    return 0;
                }
            }
        }

        // ------------------------------------------------------------------
        // Reflection bindings — captured once, latched on failure.
        // ------------------------------------------------------------------
        static bool bindingAttempted;
        static bool bindingFailed;

        // High-level pipeline
        static object textGeneratorInstance;
        static MethodInfo generateTextMethod;

        // TextGenerationSettings (class)
        static Type tgsType;
        static PropertyInfo tgsTextProp;
        static FieldInfo tgsFontAssetField;
        static FieldInfo tgsTextSettingsField;
        static FieldInfo tgsFontSizeField;
        static FieldInfo tgsColorField;
        static FieldInfo tgsScreenRectField;
        static FieldInfo tgsPixelsPerPointField;
        static FieldInfo tgsRichTextField;
        static FieldInfo tgsCharacterSpacingField;

        // TextInfo (class) + TextElementInfo (per-glyph data)
        static Type tiType;
        static FieldInfo tiCharacterCountField;
        static FieldInfo tiMaterialCountField;
        static FieldInfo tiTextElementInfoArrayField;
        static FieldInfo teiCharacterField;
        static FieldInfo teiFontAssetField;
        static FieldInfo teiBottomLeftField;
        static FieldInfo teiTopRightField;
        static FieldInfo teiIsVisibleField;
        static FieldInfo teiBaseLineField;
        static FieldInfo teiOriginField;
        static FieldInfo teiScaleField;
        static FieldInfo tgsTextWrappingModeField;
        static object textWrappingNoWrapValue;

        // FontAsset
        static PropertyInfo fontAssetAtlasTexturesProp;
        static MethodInfo tryAddCharactersMethod;

        UnityEngine.Object SelectFontAsset(
            DrawTextCommand command,
            out bool usesWeightVariant,
            out bool usesCoverageAtlas) {
            usesWeightVariant = false;
            usesCoverageAtlas = false;
            var font = command.Font;
            UnityEngine.Object asset = FontAsset;
            if (font.Weight >= 700 && BoldFontAsset != null) {
                asset = BoldFontAsset;
                usesWeightVariant = true;
            } else if (font.Weight >= 600 && SemiboldFontAsset != null) {
                asset = SemiboldFontAsset;
                usesWeightVariant = true;
            }
            if (TryGetTextDefaultSymbolFont(command, out var symbolAsset)) {
                usesWeightVariant = false;
                usesCoverageAtlas = false;
                return symbolAsset;
            }
            if (TryGetSmallTextCoverageFont(command, asset, out var coverageAsset, out bool coverageWeightVariant)) {
                usesCoverageAtlas = true;
                usesWeightVariant = coverageWeightVariant;
                return coverageAsset;
            }
            return asset;
        }

        bool TryGetTextDefaultSymbolFont(DrawTextCommand command, out UnityEngine.Object symbolAsset) {
            symbolAsset = null;
            if (!IsTextDefaultSymbolRun(command?.Text)) return false;

            int sizePx = (int)Math.Max(1, Math.Round(command.Font.Size > 0 ? command.Font.Size : 14));
            var key = new CoverageFontKey("Segoe UI Symbol", "Regular", sizePx);
            if (textSymbolFontAssets.TryGetValue(key, out var existing) && existing != null) {
                symbolAsset = existing;
                return true;
            }
            textSymbolFontAssets.Remove(key);

            try {
                var fa = UnityEngine.TextCore.Text.FontAsset.CreateFontAsset(
                    "Segoe UI Symbol",
                    "Regular",
                    pointSize: Math.Max(16, sizePx),
                    padding: Math.Max(4, (int)Math.Ceiling(sizePx * 0.12)),
                    renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA_HINTED);
                if (fa == null) return false;
                if (!fa.TryAddCharacters(command.Text, out _, includeFontFeatures: false)) {
                    DestroyFontAsset(fa);
                    return false;
                }
                fa.hideFlags = HideFlags.HideAndDontSave;
                textSymbolFontAssets[key] = fa;
                symbolAsset = fa;
                atlasRevision++;
                observedAtlasFingerprint = CurrentAtlasFingerprint();
                return true;
            } catch {
                return false;
            }
        }

        static bool IsTextDefaultSymbolRun(string text) {
            if (string.IsNullOrEmpty(text)) return false;
            bool sawSymbol = false;
            for (int i = 0; i < text.Length; i++) {
                char ch = text[i];
                if (char.IsWhiteSpace(ch)) continue;
                uint cp;
                if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) {
                    cp = (uint)char.ConvertToUtf32(ch, text[++i]);
                } else {
                    cp = ch;
                }
                if (!AtgPrimaryFallbackAdapter.IsTextDefaultEmoji(cp)) return false;
                sawSymbol = true;
            }
            return sawSymbol;
        }

        bool TryGetSmallTextCoverageFont(
            DrawTextCommand command,
            UnityEngine.Object weightedAsset,
            out UnityEngine.Object coverageAsset,
            out bool usesWeightVariant) {
            coverageAsset = null;
            usesWeightVariant = false;
            if (!EnableSmallTextCoverage || SuppressSmallTextCoverage) return false;
            if (command.BlurRadius > 0) return false;
            if (command.Font.Style != Weva.Paint.FontStyle.Normal) return false;
            int sizePx = (int)Math.Max(1, Math.Round(command.Font.Size > 0 ? command.Font.Size : 14));
            if (sizePx > SmallTextCoverageMaxSize) return false;
            string styleName;
            if (command.Font.Weight >= 700) {
                styleName = "Bold";
                usesWeightVariant = true;
            } else if (command.Font.Weight >= 600) {
                styleName = "Semibold";
                usesWeightVariant = true;
            } else if (command.Font.Weight > 400) {
                return false;
            } else {
                styleName = "Regular";
            }

            var source = (weightedAsset as UnityEngine.TextCore.Text.FontAsset)
                ?? (FontAsset as UnityEngine.TextCore.Text.FontAsset);
            var fa = GetOrCreateCoverageFontAsset(source, styleName, sizePx);
            if (fa == null) {
                usesWeightVariant = false;
                return false;
            }
            coverageAsset = fa;
            return true;
        }

        UnityEngine.TextCore.Text.FontAsset GetOrCreateCoverageFontAsset(
            UnityEngine.TextCore.Text.FontAsset source,
            string styleName,
            int sizePx) {
            if (source == null) return null;
            string family = source.faceInfo.familyName;
            if (string.IsNullOrEmpty(family)) return null;
            var key = new CoverageFontKey(family, styleName, sizePx);
            if (coverageFontAssets.TryGetValue(key, out var existing) && existing != null) {
                return existing;
            }
            coverageFontAssets.Remove(key);
            try {
                var fa = UnityEngine.TextCore.Text.FontAsset.CreateFontAsset(
                    family,
                    styleName,
                    pointSize: sizePx,
                    padding: 1,
                    renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SMOOTH_HINTED);
                if (fa == null) return null;
                if (!StyleMatches(fa.faceInfo.styleName, styleName)) {
                    DestroyFontAsset(fa);
                    return null;
                }
                fa.hideFlags = HideFlags.HideAndDontSave;
                CopyFallbacks(source, fa);
                coverageFontAssets[key] = fa;
                coverageFontAssetIdentities.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(fa));
                return fa;
            } catch {
                return null;
            }
        }

        static void CopyFallbacks(
            UnityEngine.TextCore.Text.FontAsset source,
            UnityEngine.TextCore.Text.FontAsset destination) {
            if (source == null || destination == null) return;
            var fallbacks = source.fallbackFontAssetTable;
            if (fallbacks == null || fallbacks.Count == 0) return;
            if (destination.fallbackFontAssetTable == null) {
                destination.fallbackFontAssetTable = new List<UnityEngine.TextCore.Text.FontAsset>();
            } else {
                destination.fallbackFontAssetTable.Clear();
            }
            destination.fallbackFontAssetTable.AddRange(fallbacks);
        }

        static bool StyleMatches(string actual, string requested) {
            if (string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(requested)) return false;
            if (requested.Equals("Regular", StringComparison.OrdinalIgnoreCase)) {
                return actual.IndexOf("Regular", StringComparison.OrdinalIgnoreCase) >= 0
                    || actual.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0
                    || actual.IndexOf("Book", StringComparison.OrdinalIgnoreCase) >= 0
                    || actual.IndexOf("Roman", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            return actual.IndexOf(requested, StringComparison.OrdinalIgnoreCase) >= 0
                || requested.IndexOf(actual, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        bool IsCoverageFontAsset(UnityEngine.TextCore.Text.FontAsset fontAsset) {
            return fontAsset != null
                && coverageFontAssetIdentities.Contains(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(fontAsset));
        }

        static bool ShouldSnapRun(DrawTextCommand command) {
            if (command == null) return false;
            if (command.BlurRadius > 0) return false;
            double size = command.Font.Size > 0 ? command.Font.Size : 14;
            return size <= 20;
        }

        static double PixelSnapDelta(double value) {
            return Math.Floor(value + 0.5) - value;
        }

        static void DestroyFontAsset(UnityEngine.TextCore.Text.FontAsset fa) {
            if (fa == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(fa);
            else UnityEngine.Object.DestroyImmediate(fa);
        }

        readonly struct CoverageFontKey : IEquatable<CoverageFontKey> {
            readonly string family;
            readonly string style;
            readonly int sizePx;

            public CoverageFontKey(string family, string style, int sizePx) {
                this.family = family ?? string.Empty;
                this.style = style ?? string.Empty;
                this.sizePx = sizePx;
            }

            public bool Equals(CoverageFontKey other) {
                return sizePx == other.sizePx
                    && string.Equals(family, other.family, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(style, other.style, StringComparison.OrdinalIgnoreCase);
            }

            public override bool Equals(object obj) {
                return obj is CoverageFontKey other && Equals(other);
            }

            public override int GetHashCode() {
                int hash = StringComparer.OrdinalIgnoreCase.GetHashCode(family);
                hash = (hash * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(style);
                hash = (hash * 397) ^ sizePx;
                return hash;
            }
        }

        readonly struct PreparedTextKey : IEquatable<PreparedTextKey> {
            readonly int fontAssetIdentity;
            readonly string text;

            public PreparedTextKey(UnityEngine.Object fontAsset, string text) {
                fontAssetIdentity = fontAsset != null
                    ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(fontAsset)
                    : 0;
                this.text = text ?? string.Empty;
            }

            public bool Equals(PreparedTextKey other) {
                return fontAssetIdentity == other.fontAssetIdentity && text == other.text;
            }

            public override bool Equals(object obj) {
                return obj is PreparedTextKey other && Equals(other);
            }

            public override int GetHashCode() {
                unchecked {
                    return (fontAssetIdentity * 397) ^ StringComparer.Ordinal.GetHashCode(text);
                }
            }
        }

        public bool TryShape(DrawTextCommand command, List<SdfGlyphQuad> output) {
            return TryShape(command, output, out _);
        }

        public void PrepareText(DrawTextCommand command) {
            if (command == null || string.IsNullOrEmpty(command.Text)) return;
            if (FontAsset == null || !EnsureBindings() || tryAddCharactersMethod == null) return;
            var runFontAsset = SelectFontAsset(command, out _, out _);
            if (runFontAsset == null) return;
            var preparedKey = new PreparedTextKey(runFontAsset, command.Text);
            if (preparedTextKeys.Contains(preparedKey)) return;
            try {
                TryAddCharactersAndTrack(runFontAsset, command.Text);
                StampAtlasHideFlags(runFontAsset);
                preparedTextKeys.Add(preparedKey);

                string emojiOnly = ExtractNonAscii(command.Text);
                if (string.IsNullOrEmpty(emojiOnly)) return;
                var fbProp = runFontAsset.GetType().GetProperty("fallbackFontAssetTable", BF);
                var fbList = fbProp?.GetValue(runFontAsset) as System.Collections.IList;
                if (fbList == null) return;
                for (int i = 0; i < fbList.Count; i++) {
                    var fb = fbList[i] as UnityEngine.Object;
                    if (fb == null) continue;
                    try {
                        var fbFa = fb as UnityEngine.TextCore.Text.FontAsset;
                        var t = fbFa?.atlasTextures != null && fbFa.atlasTextures.Length > 0
                            ? fbFa.atlasTextures[0] : null;
                        if (t == null) continue;
                        TryAddCharactersAndTrack(fb, emojiOnly);
                        StampAtlasHideFlags(fb);
                    } catch { /* best-effort */ }
                }
            } catch {
                // Preparation is an optimization/correctness preflight. A
                // per-command failure should fall through to normal TryShape,
                // which already handles diagnostics and SDF fallback.
            }
        }

        public bool TryShape(DrawTextCommand command, List<SdfGlyphQuad> output, out int atlasId) {
            atlasId = 0;
            if (command == null || output == null) return false;
            if (string.IsNullOrEmpty(command.Text)) return false;
            // FontAsset is a UnityEngine.Object — Unity overloads `==` so a
            // destroyed asset compares equal to null. Plain `== null` catches
            // both real null and "fake null" (still-managed-referenced but
            // destroyed underlying native object). Without this check, the
            // first MissingReferenceException inside GenerateText latches
            // bindingFailed and disables ATG for the rest of the session.
            if (FontAsset == null) {
                shapeFailures++;
                return false;
            }
            if (!EnsureBindings()) {
                shapeFailures++;
                return false;
            }
            var runFontAsset = SelectFontAsset(command, out bool usesWeightVariant, out bool usesCoverageAtlas);
            if (runFontAsset == null) {
                shapeFailures++;
                return false;
            }

            try {
                // Pre-populate the font atlas with the characters in this run.
                // The rasterizer is lazy; without this the first frame after a
                // string change returns placeholder geometry until Unity gets
                // around to rasterizing. TryAddCharacters is idempotent for
                // already-rasterized characters.
                //
                // Importantly: ALSO call TryAddCharacters on every fallback
                // FontAsset (emoji, CJK, etc.) so codepoints those fallbacks
                // cover get rasterized into their respective atlases. Without
                // this, fallbacks render as missing-glyph tofu because the
                // primary's TryAddCharacters doesn't propagate to fallbacks.
                if (tryAddCharactersMethod != null
                    && !preparedTextKeys.Contains(new PreparedTextKey(runFontAsset, command.Text))) {
                    // Primary atlas: full command text (Latin lives here).
                    TryAddCharactersAndTrack(runFontAsset, command.Text);
                    StampAtlasHideFlags(runFontAsset);
                    // Fallback atlases: only NON-ASCII characters. Pushing
                    // Latin chars to the emoji fallback caused the Segoe UI
                    // Emoji font (which happens to have Latin coverage) to
                    // rasterize Latin glyphs alongside emoji. Atlas re-packs
                    // then displaced emoji glyphs and TextGenerator's
                    // already-returned UVs pointed to Latin chars instead.
                    // Filtering to NON-ASCII keeps the emoji atlas clean.
                    string emojiOnly = ExtractNonAscii(command.Text);
                    if (!string.IsNullOrEmpty(emojiOnly)) {
                        // Pass every non-ASCII codepoint to ALL fallback
                        // assets. The chain order (mono Symbol first, COLOR
                        // Emoji second) ensures mono wins for codepoints
                        // both atlases cover — that handles the text-default
                        // partition (↩ ⏸ ⚠) where the COLOR variant has a
                        // filled-button design we don't want. For glyphs
                        // only one atlas can rasterize (e.g. ★ exists only
                        // in Symbol; 🔨 only in Emoji COLOR), the chain
                        // walk naturally picks the one that succeeds. An
                        // earlier per-format partitioning blocked ★ from
                        // reaching Symbol because U+2605 isn't classified
                        // as Emoji by Unicode, so the routing sent it to
                        // COLOR alone — which doesn't have it.
                        var fbProp = runFontAsset.GetType().GetProperty("fallbackFontAssetTable", BF);
                        var fbList = fbProp?.GetValue(runFontAsset) as System.Collections.IList;
                        if (fbList != null) {
                            for (int i = 0; i < fbList.Count; i++) {
                                var fb = fbList[i] as UnityEngine.Object;
                                if (fb == null) continue;
                                try {
                                    var fbFa = fb as UnityEngine.TextCore.Text.FontAsset;
                                    var t = fbFa?.atlasTextures != null && fbFa.atlasTextures.Length > 0
                                        ? fbFa.atlasTextures[0] : null;
                                    if (t == null) continue;
                                    TryAddCharactersAndTrack(fb, emojiOnly);
                                    StampAtlasHideFlags(fb);
                                } catch { /* best-effort */ }
                            }
                        }
                    }
                }

                // Resolve TextSettings if caller didn't supply one.
                var settingsObj = TextSettings;
                if (settingsObj == null) {
                    settingsObj = ResolveDefaultTextSettings();
                    if (settingsObj == null) {
                        shapeFailures++;
                        return false;
                    }
                }

                // Build TextGenerationSettings
                var tgs = Activator.CreateInstance(tgsType);
                tgsTextProp.SetValue(tgs, command.Text);
                tgsFontAssetField.SetValue(tgs, runFontAsset);
                tgsTextSettingsField.SetValue(tgs, settingsObj);
                tgsFontSizeField.SetValue(tgs, (int)Math.Max(1, Math.Round(command.Font.Size)));
                tgsColorField.SetValue(tgs, LinearColorToRgb(command.Color));
                // Give ATG a generously wide screenRect so it never wraps mid-run.
                // Our line breaker has already split the source text; what ATG sees
                // here is one logical line. If the rect's width is too narrow (e.g.
                // the rect comes from a small button's bounds), ATG wraps each
                // glyph onto its own line and the result reads vertically.
                tgsScreenRectField.SetValue(tgs, new UnityEngine.Rect(
                    0, 0,
                    100000f,
                    (float)Math.Max(1, command.Bounds.Height)));
                tgsPixelsPerPointField.SetValue(tgs, 1.0f);
                tgsRichTextField.SetValue(tgs, false);
                if (tgsTextWrappingModeField != null && textWrappingNoWrapValue != null) {
                    tgsTextWrappingModeField.SetValue(tgs, textWrappingNoWrapValue);
                }
                if (tgsCharacterSpacingField != null) {
                    // CSS Text §10.1 `letter-spacing` is in CSS pixels. Unity
                    // TextCore's `TextGenerationSettings.characterSpacing`,
                    // however, is in a per-engine internal unit (scaled at
                    // render time by `fontSize / sourcePointSize` in some
                    // builds; treated as em-hundredths in others). Rather than
                    // chase the conversion, we pass ZERO here and apply
                    // letter-spacing as a post-process offset on the per-glyph
                    // X coordinates we already pull out of TextElementInfo
                    // below — that path is pixel-exact regardless of which
                    // TextCore internal scaling rule applies. Pinned by the
                    // A `.play-btn-label` regression: layout label.W
                    // = sum + (N-1)*LSpx but ATG's TGS character spacing
                    // produced visibly tighter glyph runs (LS=4 at 28px
                    // rendered ~1.12px between glyphs).
                    tgsCharacterSpacingField.SetValue(tgs, 0f);
                }
                if (Weva.Diagnostics.UILayoutDiagnostics.Enabled) {
                    Weva.Diagnostics.UILayoutDiagnostics.Trace("AtgGlyphAtlasAdapter.TryShape",
                        $"text='{command.Text}' fontSize={command.Font.Size} weight={command.Font.Weight} " +
                        $"LSpx={command.LetterSpacingPx} " +
                        $"charSpacingTGS={command.LetterSpacingPx * 100.0 / Math.Max(1, command.Font.Size):F3}");
                }

                // TextInfo (output buffer — fresh per call; pooling is a
                // follow-up if profiling shows this matters)
                var ti = Activator.CreateInstance(tiType);

                generateTextMethod.Invoke(textGeneratorInstance, new object[] { tgs, ti });

                int charCount = (int)tiCharacterCountField.GetValue(ti);
                int matCount = (int)tiMaterialCountField.GetValue(ti);
                if (charCount <= 0 || matCount <= 0) {
                    shapeFailures++;
                    return false;
                }

                // Atlas: use the primary atlas of the FontAsset we passed in
                // as the run-level id. The per-glyph loop below assigns a
                // quad-specific id when a glyph came from a fallback FontAsset
                // (emoji etc.), so the renderer still routes correctly even
                // though the run-level id is the primary's.
                var atlasArr = (Array)fontAssetAtlasTexturesProp.GetValue(runFontAsset);
                if (atlasArr == null || atlasArr.Length == 0) {
                    shapeFailures++;
                    return false;
                }
                var primaryAtlas = (Texture2D)atlasArr.GetValue(0);
                if (primaryAtlas == null) {
                    shapeFailures++;
                    return false;
                }
                atlasId = EnsureAtlasRegistered(primaryAtlas, IsCoverageFontAsset(runFontAsset as UnityEngine.TextCore.Text.FontAsset));

                // Walk TextElementInfo[] — this gives us, per glyph:
                //   - the codepoint (so we know which char this is)
                //   - the FontAsset that owns the glyph (could be primary or
                //     any fallback — different per glyph)
                //   - vertex positions (TextGenerator's correct positions)
                // The key insight: TextGenerator's UV values go STALE when
                // the atlas resizes mid-shape, but the FontAsset's character
                // lookup table is always live and accurate. So we ignore
                // TextGenerator's vertex UVs and recompute from the current
                // glyphRect via fontAsset.characterLookupTable[codepoint].
                var teiArr = (Array)tiTextElementInfoArrayField.GetValue(ti);
                if (teiArr == null || teiArr.Length == 0) {
                    shapeFailures++;
                    return false;
                }

                double bx = command.Bounds.X;
                double by = command.Bounds.Y;
                double bh = command.Bounds.Height;
                LinearColor color = command.Color;
                float blurRadius = command.BlurRadius > 0 ? (float)command.BlurRadius : 0f;
                float weightBias = usesCoverageAtlas
                    ? ComputeCoverageWeightBias(command.Font)
                    : usesWeightVariant
                        ? 0f
                        : SdfGlyphAtlasAdapter.ComputeWeightBias(command.Font.Weight);
                bool snapTextRun = ShouldSnapRun(command);
                bool snapComputed = false;
                double snapDx = 0;
                double snapDy = 0;
                // CSS Inline Layout §3: when layout supplied a baseline,
                // re-anchor the run's baseline to it. TextCore bottom-aligns
                // the baseline within the run box (baseline ≈ box bottom), so
                // `bh - baselineTC` lands the glyphs at the box bottom — wrong
                // for tight line boxes (match3 `.combo-banner`). The shift
                // `LayoutBaseline - (bh - baselineTC)` is computed once from
                // the first glyph (baselineTC is the same for every glyph in
                // the run) and added to every glyph's yTop.
                bool useLayoutBaseline = !double.IsNaN(command.LayoutBaseline) && teiBaseLineField != null;
                double baselineDelta = 0;
                bool baselineDeltaComputed = false;

                for (int e = 0; e < charCount && e < teiArr.Length; e++) {
                    var elem = teiArr.GetValue(e);
                    if (elem == null) continue;

                    bool isVisible = teiIsVisibleField != null
                        ? (bool)teiIsVisibleField.GetValue(elem)
                        : true;
                    if (!isVisible) continue;

                    uint codepoint = (uint)teiCharacterField.GetValue(elem);
                    var fontAsset = teiFontAssetField.GetValue(elem) as UnityEngine.TextCore.Text.FontAsset;
                    if (fontAsset == null) continue;
                    if (!fontAsset.characterLookupTable.TryGetValue(codepoint, out var ch)) continue;
                    var glyph = ch.glyph;
                    if (glyph == null) continue;

                    int atlasIdx = glyph.atlasIndex;
                    var atlases = fontAsset.atlasTextures;
                    if (atlases == null || atlasIdx >= atlases.Length) continue;
                    var atlasTex = atlases[atlasIdx];
                    if (atlasTex == null) continue;

                    // Live UV from the FontAsset's current glyph rect.
                    // Inflate by atlasPadding so the SDF spread region is
                    // sampled — TextGenerator's original vertex UVs included
                    // this padding ring, and the quad corners we read from
                    // textElementInfo span the padded extent too. Without
                    // the inflation, our UV samples a tighter region than
                    // the quad covers and glyphs appear compressed / lose
                    // their AA edges at the boundary.
                    int aw = atlasTex.width;
                    int ah = atlasTex.height;
                    int pad = fontAsset.atlasPadding;
                    bool isColorAtlas = IsColorTexture(atlasTex);
                    bool isCoverageAtlas = IsCoverageFontAsset(fontAsset);
                    int samplePad = isColorAtlas || isCoverageAtlas ? 0 : pad;
                    var gr = glyph.glyphRect;
                    var uvMin = new Vector2(
                        (float)(gr.x - samplePad) / aw,
                        (float)(gr.y - samplePad) / ah);
                    var uvMax = new Vector2(
                        (float)(gr.x + gr.width + samplePad) / aw,
                        (float)(gr.y + gr.height + samplePad) / ah);

                    // Position: use TextGenerator's bottomLeft / topRight
                    // (these are screen-space coords; UVs were the stale part).
                    var bl = teiBottomLeftField.GetValue(elem);
                    var tr = teiTopRightField.GetValue(elem);
                    Vector3 blPos = bl is Vector3 v0 ? v0 : (Vector3)bl;
                    Vector3 trPos = tr is Vector3 v1 ? v1 : (Vector3)tr;
                    double xMin = Math.Min(blPos.x, trPos.x);
                    double xMax = Math.Max(blPos.x, trPos.x);
                    double yMinTC = Math.Min(blPos.y, trPos.y);
                    double yMaxTC = Math.Max(blPos.y, trPos.y);
                    if (isCoverageAtlas && teiOriginField != null && teiBaseLineField != null && teiScaleField != null) {
                        double scale = Convert.ToDouble(teiScaleField.GetValue(elem));
                        double origin = Convert.ToDouble(teiOriginField.GetValue(elem));
                        double baseline = Convert.ToDouble(teiBaseLineField.GetValue(elem));
                        var metrics = glyph.metrics;
                        xMin = origin + metrics.horizontalBearingX * scale;
                        xMax = xMin + metrics.width * scale;
                        yMaxTC = baseline + metrics.horizontalBearingY * scale;
                        yMinTC = yMaxTC - metrics.height * scale;
                    }
                    // CSS Text §10.1 letter-spacing post-process. TextCore's
                    // characterSpacing was set to 0 above so the glyph
                    // positions we read here are advance-only (no inter-letter
                    // spacing applied by TGS). Apply the CSS letter-spacing
                    // value as a per-glyph X offset: the i-th character in the
                    // run shifts right by `i * LSpx`. Counts the array index
                    // `e` (NOT just visible glyphs) so spaces correctly
                    // contribute their own gap to the cumulative offset of
                    // following glyphs — matches Chrome's "letter-spacing
                    // applies between every adjacent letter unit including
                    // across U+0020" rule.
                    if (command.LetterSpacingPx != 0) {
                        double lsOffset = e * command.LetterSpacingPx;
                        xMin += lsOffset;
                        xMax += lsOffset;
                    }
                    double w = xMax - xMin;
                    double h = yMaxTC - yMinTC;
                    if (w <= 0 || h <= 0) continue;

                    int quadAtlasId = EnsureAtlasRegistered(atlasTex, isCoverageAtlas);
                    // Re-anchor to the layout baseline (once per run). baselineTC
                    // is constant across the run's glyphs, so the first visible
                    // glyph fixes the shift.
                    if (useLayoutBaseline && !baselineDeltaComputed) {
                        double baselineTC = Convert.ToDouble(teiBaseLineField.GetValue(elem));
                        baselineDelta = command.LayoutBaseline - (bh - baselineTC);
                        baselineDeltaComputed = true;
                    }
                    if (snapTextRun && !snapComputed) {
                        snapDx = PixelSnapDelta(bx);
                        if (useLayoutBaseline) {
                            // Snap the re-anchored baseline (by + LayoutBaseline).
                            snapDy = PixelSnapDelta(by + command.LayoutBaseline);
                        } else if (teiBaseLineField != null) {
                            double baselineTC = Convert.ToDouble(teiBaseLineField.GetValue(elem));
                            double baselineScreen = by + (bh - baselineTC);
                            snapDy = PixelSnapDelta(baselineScreen);
                        } else {
                            snapDy = PixelSnapDelta(by + (bh - yMaxTC));
                        }
                        snapComputed = true;
                    }
                    // TC stores positions y-UP within the screenRect (baseline
                    // at small y, ascender extending UP to larger y values).
                    // Our screen is y-DOWN. To align baselines across glyphs of
                    // different heights (H tall, n short), flip: yTop_screen =
                    // bounds.Y + (bh - yMaxTC). This puts all glyphs' BOTTOM
                    // at the same baseline (different per-glyph yTops produce
                    // different glyph heights that all align at the bottom).
                    double x = bx + xMin + snapDx;
                    double yTop = by + (bh - yMaxTC) + snapDy + baselineDelta;
                    // Keep text-shadow blur geometry identical to the crisp
                    // glyph geometry. The shader samples neighboring SDF
                    // coverage for BlurRadius; inflating this quad would
                    // stretch the same UV rect over a larger screen area,
                    // making large symbol shadows look scaled and crowding
                    // nearby content even though layout boxes match Chrome.
                    var rect = new PaintRect(x, yTop, w, h);
                    // Text-default emoji codepoints (↩ ⏸ ⚠ etc.) come from
                    // a color atlas but Chrome renders them monochrome with
                    // CSS color. Flag the quad so the shader tints it.
                    bool tint = AtgPrimaryFallbackAdapter.IsTextDefaultEmoji(codepoint);
                    output.Add(new SdfGlyphQuad(rect, color, uvMin, uvMax, quadAtlasId, blurRadius, weightBias, tint));
                }

                shapeHits++;
                return true;
            } catch (Exception ex) {
                // DO NOT latch bindingFailed=true here. A per-shape exception
                // (e.g. a destroyed FontAsset on domain reload, a malformed
                // codepoint, or an incompatible FontAsset type passed in
                // for a single call) is per-CALL — the underlying TextCore
                // bindings are still healthy and the next request with a
                // valid font will work. Previously this catch latched
                // bindingFailed permanently, so a single bad call disabled
                // ATG for the rest of the session and every subsequent
                // PickBest skipped the ATG wrap — small text fell through
                // to SDF and lost thin features (e.g. the "E" cardinal
                // label on map.html's compass at 11px rendered as a left-
                // vertical-only silhouette resembling "[" because the SDF
                // AA threshold dropped the horizontal arms). Only
                // EnsureBindings should set bindingFailed, and only when
                // the TextCore reflection bindings themselves can't be
                // located. Per-call problems route through shapeFailures.
                shapeFailures++;
                Weva.Diagnostics.UICssDiagnostics.Warn("AtgGlyphAtlasAdapter",
                    "ATG shape failed: " + ex.Message + " — falling back to SDF for this run");
                return false;
            }
        }

        // Per-atlas-texture FaceInfo + GlyphAtlas registrations so multiple
        // ATG atlases (primary text + emoji + ...) each have their own slot
        // in AtlasRegistry. Without separate entries, registering a new atlas
        // texture would overwrite the primary's registration and the renderer
        // would bind only the most recent atlas — visible as text disappearing
        // when emoji glyphs are also present.
        readonly Dictionary<Texture2D, (Weva.Text.TextCore.FaceInfo face, Weva.Text.TextCore.GlyphAtlas atlas, int id)> atgAtlasCache
            = new Dictionary<Texture2D, (Weva.Text.TextCore.FaceInfo, Weva.Text.TextCore.GlyphAtlas, int)>();

        int EnsureAtlasRegistered(Texture2D tex, bool isCoverageAtlas = false) {
            if (tex == null) return 0;
            if (atgAtlasCache.TryGetValue(tex, out var existing)) {
                Weva.Text.Sdf.AtlasRegistry.RegisterAtlas(existing.face, existing.atlas);
                if (isCoverageAtlas) Weva.Text.Sdf.AtlasRegistry.MarkCoverageAtlasById(existing.id);
                return existing.id;
            }
            var atlas = new Weva.Text.TextCore.GlyphAtlas();
            atlas.TextureOverride = tex;
            // Family is only a diagnostic label; identity is the Texture2D
            // reference in atgAtlasCache. RuntimeHelpers.GetHashCode gives a
            // stable identity-based int without using the deprecated
            // GetInstanceID().
            string family = "atg:" + (tex.name ?? "atlas") + ":"
                + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(tex);
            var face = new Weva.Text.TextCore.FaceInfo(family, string.Empty, 400, Weva.Text.TextCore.FaceInfo.StyleNormal);
            Weva.Text.Sdf.AtlasRegistry.RegisterAtlas(face, atlas);
            int id = Weva.Text.Sdf.AtlasRegistry.GetAtlasId(atlas);
            if (isCoverageAtlas) Weva.Text.Sdf.AtlasRegistry.MarkCoverageAtlasById(id);
            // Detect color atlas by texture format. Color emoji (Segoe UI
            // Emoji's COLR/CPAL glyphs) rasterizes into RGBA32 instead of
            // Alpha8; the renderer's `_TEXT_COLOR` shader path samples the
            // RGBA texel directly instead of SDF-thresholding. Without this
            // flag, color-emoji quads would treat the RGBA bytes as a
            // distance field and render as wrong-tinted silhouettes.
            switch (tex.format) {
                case UnityEngine.TextureFormat.RGBA32:
                case UnityEngine.TextureFormat.BGRA32:
                case UnityEngine.TextureFormat.ARGB32:
                case UnityEngine.TextureFormat.RGB24:
                    Weva.Text.Sdf.AtlasRegistry.MarkColorAtlasById(id);
                    break;
            }
            atgAtlasCache[tex] = (face, atlas, id);
            return id;
        }

        static bool IsColorTexture(Texture2D tex) {
            if (tex == null) return false;
            switch (tex.format) {
                case UnityEngine.TextureFormat.RGBA32:
                case UnityEngine.TextureFormat.BGRA32:
                case UnityEngine.TextureFormat.ARGB32:
                case UnityEngine.TextureFormat.RGB24:
                case UnityEngine.TextureFormat.RGBAHalf:
                case UnityEngine.TextureFormat.RGBAFloat:
                    return true;
                default:
                    return false;
            }
        }

        static float ComputeCoverageWeightBias(FontHandle font) {
            double size = font.Size > 0 ? font.Size : 14;
            if (size > 20) return 0f;
            double sizeT = Math.Max(0.0, Math.Min(1.0, (20.0 - size) / 8.0));
            double bias = 0.22 * sizeT;
            if (font.Weight >= 600) bias *= 0.55;
            else if (font.Weight >= 500) bias *= 0.75;
            return (float)bias;
        }

        // Filter a string down to codepoints that should be offered to symbol
        // / emoji fallback atlases. Latin accented letters must stay on the
        // primary text face (or the real font fallback path), even if an emoji
        // font reports broad Latin coverage.
        static string ExtractNonAscii(string s) {
            if (string.IsNullOrEmpty(s)) return s;
            System.Text.StringBuilder sb = null;
            int n = s.Length;
            for (int i = 0; i < n; i++) {
                char c = s[i];
                bool isSurrogate = char.IsHighSurrogate(c) && i + 1 < n && char.IsLowSurrogate(s[i + 1]);
                uint cp = isSurrogate ? (uint)char.ConvertToUtf32(c, s[i + 1]) : c;
                bool keep = cp >= 0x80 && !IsLatinLetterOrMark(cp);
                if (keep) {
                    if (sb == null) sb = new System.Text.StringBuilder(n);
                    sb.Append(c);
                    if (isSurrogate) {
                        sb.Append(s[i + 1]);
                        i++;
                    }
                }
            }
            return sb?.ToString() ?? string.Empty;
        }

        static bool IsLatinLetterOrMark(uint codepoint) {
            return (codepoint >= 0x00C0 && codepoint <= 0x00FF && codepoint != 0x00D7 && codepoint != 0x00F7)
                || (codepoint >= 0x0100 && codepoint <= 0x024F)
                || (codepoint >= 0x0300 && codepoint <= 0x036F)
                || (codepoint >= 0x1E00 && codepoint <= 0x1EFF);
        }

        // Set HideAndDontSave on each atlas Texture2D inside a FontAsset.
        // Unity GCs atlas textures separately from their owning FontAsset, so
        // setting HideFlags only on the FontAsset is insufficient — the
        // textures still get unloaded on scene change / asset GC, leaving the
        // FontAsset's lookup table referencing a destroyed Texture2D. Calling
        // this after every TryAddCharacters keeps fresh textures pinned.
        static void StampAtlasHideFlags(UnityEngine.Object asset) {
            try {
                var fa = asset as UnityEngine.TextCore.Text.FontAsset;
                if (fa == null) return;
                var arr = fa.atlasTextures;
                if (arr == null) return;
                for (int i = 0; i < arr.Length; i++) {
                    if (arr[i] != null && arr[i].hideFlags != UnityEngine.HideFlags.HideAndDontSave) {
                        arr[i].hideFlags = UnityEngine.HideFlags.HideAndDontSave;
                    }
                }
            } catch { /* defensive */ }
        }

        // Find the project's default TextSettings. UI Toolkit auto-creates
        // an "Editor Text Settings" instance the first time text renders;
        // we piggyback on it instead of creating a parallel one.
        static UnityEngine.Object cachedTextSettings;
        static UnityEngine.Object ResolveDefaultTextSettings() {
            if (cachedTextSettings != null) return cachedTextSettings;
            var found = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.TextCore.Text.TextSettings>();
            if (found != null && found.Length > 0) {
                cachedTextSettings = found[0];
            }
            return cachedTextSettings;
        }

        static Color LinearColorToRgb(LinearColor c) {
            // TextGenerationSettings.color is Color (linear). LinearColor → Color.
            return new Color(c.R, c.G, c.B, c.A);
        }

        // --------------------------------------------------------------
        // Binding setup
        // --------------------------------------------------------------
        const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        const BindingFlags BFs = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        static bool EnsureBindings() {
            if (bindingFailed) return false;
            if (bindingAttempted) return true;
            bindingAttempted = true;

            try {
                Assembly textCoreAsm = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) {
                    if (a.GetName().Name == "UnityEngine.TextCoreTextEngineModule") {
                        textCoreAsm = a;
                        break;
                    }
                }
                if (textCoreAsm == null) return FailBindings(
                    "UnityEngine.TextCoreTextEngineModule assembly not loaded — Unity's TextCore text engine module is unavailable. " +
                    "Player builds fall back to the SDF-only path (small text looks fuzzy/mashed).");

                var tgType = textCoreAsm.GetType("UnityEngine.TextCore.Text.TextGenerator");
                tgsType = textCoreAsm.GetType("UnityEngine.TextCore.Text.TextGenerationSettings");
                tiType = textCoreAsm.GetType("UnityEngine.TextCore.Text.TextInfo");
                var faType = textCoreAsm.GetType("UnityEngine.TextCore.Text.FontAsset");
                if (tgType == null || tgsType == null || tiType == null || faType == null) {
                    return FailBindings(
                        "TextCore type(s) missing — TextGenerator=" + (tgType != null) +
                        " TextGenerationSettings=" + (tgsType != null) +
                        " TextInfo=" + (tiType != null) +
                        " FontAsset=" + (faType != null) +
                        ". Most likely IL2CPP stripped them; add UnityEngine.TextCoreTextEngineModule to link.xml.");
                }

                // TextGenerator singleton
                var getTextGen = tgType.GetMethod("GetTextGenerator", BFs);
                if (getTextGen == null) return FailBindings(
                    "TextGenerator.GetTextGenerator() method missing via reflection — IL2CPP likely stripped it. " +
                    "Add UnityEngine.TextCoreTextEngineModule to link.xml with preserve='all'.");
                textGeneratorInstance = getTextGen.Invoke(null, null);
                if (textGeneratorInstance == null) return FailBindings(
                    "TextGenerator.GetTextGenerator() returned null at runtime.");

                generateTextMethod = tgType.GetMethod("GenerateText", BF, null,
                    new[] { tgsType, tiType }, null);

                // TGS members
                tgsTextProp = tgsType.GetProperty("text", BF);
                tgsFontAssetField = tgsType.GetField("fontAsset", BF);
                tgsTextSettingsField = tgsType.GetField("textSettings", BF);
                tgsFontSizeField = tgsType.GetField("fontSize", BF);
                tgsColorField = tgsType.GetField("color", BF);
                tgsScreenRectField = tgsType.GetField("screenRect", BF);
                tgsPixelsPerPointField = tgsType.GetField("pixelsPerPoint", BF);
                tgsRichTextField = tgsType.GetField("richText", BF);
                tgsCharacterSpacingField = tgsType.GetField("characterSpacing", BF); // optional
                tgsTextWrappingModeField = tgsType.GetField("textWrappingMode", BF);
                if (tgsTextWrappingModeField != null) {
                    // TextWrappingMode.NoWrap = 0 — disable wrapping inside this run.
                    // Our line breaker already split the text upstream, so any wrap
                    // here would re-break a single word into multiple stacked lines.
                    var wrapEnum = tgsTextWrappingModeField.FieldType;
                    textWrappingNoWrapValue = Enum.ToObject(wrapEnum, 0);
                }

                // TI members
                tiCharacterCountField = tiType.GetField("characterCount", BF);
                tiMaterialCountField = tiType.GetField("materialCount", BF);
                tiTextElementInfoArrayField = tiType.GetField("textElementInfo", BF);

                // TextElementInfo members (per-glyph data with current FontAsset reference)
                var teiType = textCoreAsm.GetType("UnityEngine.TextCore.Text.TextElementInfo");
                if (teiType != null) {
                    teiCharacterField = teiType.GetField("character", BF);
                    teiFontAssetField = teiType.GetField("fontAsset", BF);
                    teiBottomLeftField = teiType.GetField("bottomLeft", BF);
                    teiTopRightField = teiType.GetField("topRight", BF);
                    teiIsVisibleField = teiType.GetField("isVisible", BF);
                    teiBaseLineField = teiType.GetField("baseLine", BF);
                    teiOriginField = teiType.GetField("origin", BF);
                    teiScaleField = teiType.GetField("scale", BF);
                }

                // FontAsset accessors
                fontAssetAtlasTexturesProp = faType.GetProperty("atlasTextures", BF);
                // TryAddCharacters(string, bool) overload
                tryAddCharactersMethod = faType.GetMethod("TryAddCharacters", BF, null,
                    new[] { typeof(string), typeof(bool) }, null);

                // Sanity: everything we hit on the hot path must be non-null.
                if (generateTextMethod == null ||
                    tgsTextProp == null || tgsFontAssetField == null || tgsTextSettingsField == null ||
                    tgsFontSizeField == null || tgsColorField == null || tgsScreenRectField == null ||
                    tgsPixelsPerPointField == null || tgsRichTextField == null ||
                    tiCharacterCountField == null || tiMaterialCountField == null ||
                    tiTextElementInfoArrayField == null ||
                    teiCharacterField == null || teiFontAssetField == null ||
                    teiBottomLeftField == null || teiTopRightField == null ||
                    fontAssetAtlasTexturesProp == null) {
                    return FailBindings(
                        "TextCore reflection members partially stripped — generateText=" + (generateTextMethod != null) +
                        " tgsText=" + (tgsTextProp != null) +
                        " tgsFontAsset=" + (tgsFontAssetField != null) +
                        " tgsTextSettings=" + (tgsTextSettingsField != null) +
                        " tgsFontSize=" + (tgsFontSizeField != null) +
                        " tgsColor=" + (tgsColorField != null) +
                        " tgsScreenRect=" + (tgsScreenRectField != null) +
                        " tgsPixelsPerPoint=" + (tgsPixelsPerPointField != null) +
                        " tgsRichText=" + (tgsRichTextField != null) +
                        " tiCharacterCount=" + (tiCharacterCountField != null) +
                        " tiMaterialCount=" + (tiMaterialCountField != null) +
                        " tiTextElementInfo=" + (tiTextElementInfoArrayField != null) +
                        " teiCharacter=" + (teiCharacterField != null) +
                        " teiFontAsset=" + (teiFontAssetField != null) +
                        " teiBottomLeft=" + (teiBottomLeftField != null) +
                        " teiTopRight=" + (teiTopRightField != null) +
                        " faAtlasTextures=" + (fontAssetAtlasTexturesProp != null) +
                        ". Add UnityEngine.TextCoreTextEngineModule to link.xml with preserve='all'.");
                }

                return true;
            } catch (Exception ex) {
                return FailBindings("ATG binding threw: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Centralised failure path so every bail-out logs an actionable message.
        // Before this, silent `bindingFailed = true; return false;` returns left
        // ATG disabled in player builds with no signal — text rendered through
        // the raw SDF adapter (SDFAA + low-padding atlas = fuzzy/mashed look)
        // and the user had no way to know ATG had been knocked out by IL2CPP
        // stripping. The warning is once-per-session because EnsureBindings
        // latches via bindingAttempted/bindingFailed.
        static bool FailBindings(string reason) {
            bindingFailed = true;
            Weva.Diagnostics.UICssDiagnostics.Warn("AtgGlyphAtlasAdapter",
                "ATG binding failed → falling back to raw SDF text path. " + reason);
            return false;
        }
    }
}
#endif
