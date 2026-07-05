#if UNITY_2023_1_OR_NEWER
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;
using Weva.Paint;
using FontStyle = Weva.Paint.FontStyle;

namespace Weva.Text.TextCore {
    // UnityFontEngineBackend bridges ITextCoreBackend to Unity's FontEngine.
    //
    // FontEngine quirks we work around:
    //   - InitializeFontEngine() must be called once per process. It is safe to
    //     call repeatedly; Unity short-circuits subsequent calls.
    //   - LoadFontFace takes either a Font asset, a path string, or a file
    //     reference. We accept FaceInfo.Path; if empty, we ask FontEngine for
    //     the system default for the generic family name.
    //   - TextCore's "atlas" rendering produces SDF samples when rendered with
    //     a render mode of SDFAA / SDF; otherwise it produces alpha bitmaps.
    //     We use SDFAA at 4x oversample then downsample, so the GPU can do
    //     resolution-independent text at runtime via the Hidden/Weva/Text
    //     shader (see TextCoreShaderContract).
    //   - RenderToTexture writes into a Texture2D destination; we render into
    //     a scratch Texture2D the size of the glyph box plus padding, then
    //     read the pixels into a managed byte[] for the GlyphAtlas to copy.
    //
    // Initialization order: LoadFace() → SetFaceSize() → RenderGlyph(). We
    // re-set the size on every Rasterize call because FontEngine is stateful
    // about the active size.
    public sealed class UnityFontEngineBackend : ITextCoreBackend {
        // Bug #1: aligned with SdfGlyphRasterizer.PaddingPx (8). Previously 9,
        // which produced a 1 px shift + 2 px clip when this legacy stub fell
        // through alongside the rasterizer's 8 px output. The actual padding
        // each glyph carries is now threaded via RasterizedGlyph.Padding so
        // the baker is no longer dependent on this constant either way; we
        // keep them aligned for documentation.
        const int RenderPadding = 8;
        const GlyphRenderMode RenderMode = GlyphRenderMode.SDFAA;

        readonly Dictionary<FaceInfo, FaceState> faces = new();
        FaceInfo currentFace;
        double currentSize = -1;
        Texture2D scratchTexture;
        bool initialized;

        sealed class FaceState {
            public FaceInfo Info;
            public FaceMetrics Metrics;
            public bool Loaded;
        }

        public bool LoadFace(FaceInfo face, out FaceMetrics metrics) {
            EnsureInitialized();
            if (faces.TryGetValue(face, out var state) && state.Loaded) {
                metrics = state.Metrics;
                return true;
            }
            state = state ?? new FaceState { Info = face };
            faces[face] = state;
            if (!ActivateFace(face)) { metrics = default; return false; }
            var faceInfo = FontEngine.GetFaceInfo();
            // unitsPerEM is not exposed on TextCore's FaceInfo across versions, BUT
            // GetFaceInfo() right after LoadFontFace() — before any SetFaceSize() —
            // reports the face's DESIGN-UNIT metrics, and in that state faceInfo.pointSize
            // IS the font's units-per-em (e.g. 2048 for Weva-Default; ascentLine=2146,
            // lineHeight=2701). ActivateFace() above never sets a size, so we are in
            // exactly that state here. The previous code hard-coded upem=1000, which
            // scaled every SDF-path metric by pointSize/1000 ≈ 2.05× — the bug behind
            // INPUTTEST-TITLE-BASELINE: a 30px run's ascent came out 64.38 instead of
            // 31.44, pushing the baseline ~one ascent below the line box so large text
            // (which routes to the SDF baker rather than ATG's small-text coverage)
            // rendered a full line too low and overlapped the row beneath it.
            // Use the real em; fall back to 1000 only if the face reports nothing.
            double upem = faceInfo.pointSize > 0 ? faceInfo.pointSize : 1000;
            state.Metrics = new FaceMetrics(
                unitsPerEm: upem,
                ascent: faceInfo.ascentLine,
                descent: -faceInfo.descentLine,
                lineGap: faceInfo.lineHeight - (faceInfo.ascentLine - faceInfo.descentLine),
                lineHeight: faceInfo.lineHeight
            );
            state.Loaded = true;
            metrics = state.Metrics;
            return true;
        }

        // TODO orchestrator: validate against the actual Unity 6 FontEngine surface:
        //   - method names: TryGetGlyphIndex / TryGetGlyphWithIndexValue may
        //     differ on your installed TextCore.LowLevel version.
        //   - GlyphLoadFlags enum casing varies by Unity version.
        //   - GlyphRenderMode.SDFAA may instead be SDF or SDFAA_HINTED.
        public bool TryGetGlyphAdvance(FaceInfo face, uint codepoint, double fontSize, out double advancePx) {
            advancePx = 0;
            if (!ActivateFaceAtSize(face, fontSize)) return false;
            if (!FontEngine.TryGetGlyphIndex(codepoint, out uint glyphIndex) || glyphIndex == 0) return false;
            var glyph = new Glyph();
            if (!FontEngine.TryGetGlyphWithIndexValue(glyphIndex, GlyphLoadFlags.LOAD_NO_BITMAP, out glyph)) return false;
            advancePx = glyph.metrics.horizontalAdvance;
            return true;
        }

        // Per-face cache of em-relative pair kerning: (leftCp<<32 | rightCp) ->
        // kern / fontSize. Kerning scales linearly with size, so we cache a
        // size-invariant em value and multiply by the requested size on lookup —
        // this keeps the hot layout/paint loop off the stateful FontEngine after
        // each distinct pair is seen once.
        readonly Dictionary<FaceInfo, Dictionary<long, double>> kernByFace = new();

        // Horizontal pair-adjustment (px) between two adjacent codepoints on
        // `face` at `fontSize`, read from the font's GPOS/kern pairs via
        // FontEngine.GetGlyphPairAdjustmentTable. Returns false (0) when the pair
        // has no adjustment or the face/glyphs can't be resolved. Wired into the
        // SDF/FontEngine text path through SdfFontMetrics.WithKernProvider.
        public bool TryGetKernAdvance(FaceInfo face, uint leftCp, uint rightCp, double fontSize, out double kernPx) {
            kernPx = 0;
            if (fontSize <= 0) return false;
            EnsureInitialized();
            if (!kernByFace.TryGetValue(face, out var cache)) {
                cache = new Dictionary<long, double>();
                kernByFace[face] = cache;
            }
            long key = ((long)leftCp << 32) | rightCp;
            if (!cache.TryGetValue(key, out double em)) {
                em = QueryKernEm(face, leftCp, rightCp, fontSize);
                cache[key] = em;
            }
            if (em == 0.0) return false;
            kernPx = em * fontSize;
            return true;
        }

        double QueryKernEm(FaceInfo face, uint leftCp, uint rightCp, double fontSize) {
            if (!ActivateFaceAtSize(face, fontSize)) return 0;
            double sizePx = System.Math.Max(1.0, System.Math.Round(fontSize));
            if (!FontEngine.TryGetGlyphIndex(leftCp, out uint li) || li == 0) return 0;
            if (!FontEngine.TryGetGlyphIndex(rightCp, out uint ri) || ri == 0) return 0;
            if (!TryGetPairRecord(li, ri, out var rec)) return 0;
            // OpenType splits the pair correction across both records' xAdvance;
            // sum for the cumulative horizontal adjustment (matches
            // TmpFontAssetSource.GetKern). Values are px at the active integer
            // face size → normalize to em (size-invariant) for caching.
            double advPx = rec.firstAdjustmentRecord.glyphValueRecord.xAdvance
                         + rec.secondAdjustmentRecord.glyphValueRecord.xAdvance;
            return advPx / sizePx;
        }

        // FontEngine.GetGlyphPairAdjustmentRecord is internal to TextCore (TMP
        // uses the same native table). Bind it once via reflection; the public
        // GlyphPairAdjustmentRecord struct it returns is readable directly. The
        // per-pair cache in TryGetKernAdvance means this fires once per distinct
        // pair per face, so the reflection cost stays off the hot loop.
        static System.Reflection.MethodInfo s_pairKernMethod;
        static bool s_pairKernBound;
        static bool TryGetPairRecord(uint first, uint second, out GlyphPairAdjustmentRecord rec) {
            rec = default;
            if (!s_pairKernBound) {
                s_pairKernBound = true;
                s_pairKernMethod = typeof(FontEngine).GetMethod(
                    "GetGlyphPairAdjustmentRecord",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(uint), typeof(uint) }, null);
            }
            if (s_pairKernMethod == null) return false;
            var r = s_pairKernMethod.Invoke(null, new object[] { first, second });
            if (r is GlyphPairAdjustmentRecord g) { rec = g; return true; }
            return false;
        }

        // Optional external rasterizer. When set (typically by SdfBootstrap), the backend
        // delegates RasterizeGlyph to the reflection-bound TryRenderGlyphsToTexture path
        // owned by SdfGlyphRasterizer. When null, RasterizeGlyph returns the legacy
        // empty-bytes stub (visible-bounds-only — kept for headless tests that don't
        // construct a rasterizer).
        public System.Func<FaceInfo, uint, double, RasterizedGlyph?> RasterizerHook { get; set; }

        public bool RasterizeGlyph(FaceInfo face, uint codepoint, double fontSize, out RasterizedGlyph glyph) {
            glyph = default;
            if (RasterizerHook != null) {
                var raster = RasterizerHook(face, codepoint, fontSize);
                if (raster.HasValue && !raster.Value.IsEmpty) {
                    glyph = raster.Value;
                    return true;
                }
            }
            if (!ActivateFaceAtSize(face, fontSize)) return false;
            if (!FontEngine.TryGetGlyphIndex(codepoint, out uint glyphIndex) || glyphIndex == 0) return false;
            var g = new Glyph();
            if (!FontEngine.TryGetGlyphWithIndexValue(glyphIndex, GlyphLoadFlags.LOAD_DEFAULT, out g)) return false;

            int w = (int)System.Math.Max(1, System.Math.Ceiling(g.metrics.width)) + RenderPadding * 2;
            int h = (int)System.Math.Max(1, System.Math.Ceiling(g.metrics.height)) + RenderPadding * 2;
            if (scratchTexture == null || scratchTexture.width < w || scratchTexture.height < h) {
                if (scratchTexture != null) Object.Destroy(scratchTexture);
                int sw = NextPow2(w);
                int sh = NextPow2(h);
                scratchTexture = new Texture2D(sw, sh, TextureFormat.Alpha8, mipChain: false, linear: true);
                scratchTexture.name = "Weva.GlyphScratch";
            }

            // Legacy path: produce zero-filled pixels matching the metrics box. Visible
            // bounds only, no real glyph silhouette. Used as a last-resort fallback when
            // the SdfGlyphRasterizer hook is not installed.
            byte[] pixels = new byte[w * h];

            var metrics = new GlyphMetrics(
                advanceX: g.metrics.horizontalAdvance,
                bearingX: g.metrics.horizontalBearingX,
                bearingY: g.metrics.horizontalBearingY,
                width: g.metrics.width,
                height: g.metrics.height
            );
            glyph = new RasterizedGlyph(pixels, w, h, RenderPadding, metrics);
            return true;
        }

        // Resolves the platform-default font face via FontResolver and primes the
        // backend by calling LoadFace. Used by TextCoreBootstrap to produce a
        // ready-to-use TextCoreFontMetrics in the absence of an authored
        // font-family. Returns FaceInfo.Empty when the platform default could
        // not be loaded; the caller is expected to fall back to MonoFontMetrics
        // in that case.
        public FaceInfo LoadDefault() {
            EnsureInitialized();
            var handle = new FontHandle(FontResolver.DefaultFamily, UIDocumentDefaultsFontSize, 400, FontStyle.Normal);
            var face = FontResolver.Resolve(handle);
            if (!face.IsValid) return FaceInfo.Empty;
            if (!LoadFace(face, out _)) return FaceInfo.Empty;
            return face;
        }

        // 16 px matches UIDocumentDefaults.DefaultFontSizePx; duplicated here as a
        // local literal to avoid pulling Document into the TextCore namespace.
        const double UIDocumentDefaultsFontSize = 16.0;

        public void Dispose() {
            if (scratchTexture != null) {
                Object.Destroy(scratchTexture);
                scratchTexture = null;
            }
            faces.Clear();
        }

        bool ActivateFaceAtSize(FaceInfo face, double fontSize) {
            if (!ActivateFace(face)) return false;
            int sizePx = (int)System.Math.Max(1, System.Math.Round(fontSize));
            if ((int)currentSize != sizePx) {
                if (FontEngine.SetFaceSize(sizePx) != FontEngineError.Success) return false;
                currentSize = sizePx;
            }
            return true;
        }

        bool ActivateFace(FaceInfo face) {
            if (currentFace.Equals(face) && faces.TryGetValue(face, out var existing) && existing.Loaded) return true;
            string path = ResolvePath(face);
            // Unity 6 / TextCore.LowLevel removed the parameterless LoadFontFace overload.
            // When no path is registered for the family, fail fast — TextCoreBootstrap
            // catches and falls back to MonoFontMetrics, which is fine for the v1 demo.
            // Register a custom path via FontResolver.RegisterFont to use a specific font.
            if (string.IsNullOrEmpty(path)) return false;
            if (FontEngine.LoadFontFace(path) != FontEngineError.Success) return false;
            currentFace = face;
            currentSize = -1;
            return true;
        }

        static string ResolvePath(FaceInfo face) {
            if (!string.IsNullOrEmpty(face.Path)) return face.Path;
            // For generic families with no registered path, leave empty so that
            // ActivateFace falls back to the no-arg LoadFontFace overload, which
            // loads the platform default. Custom font registration via
            // FontResolver.RegisterFont is the recommended path.
            return string.Empty;
        }

        void EnsureInitialized() {
            if (initialized) return;
            FontEngine.InitializeFontEngine();
            initialized = true;
        }

        static int NextPow2(int v) {
            int p = 1;
            while (p < v) p <<= 1;
            return p;
        }
    }
}
#endif
