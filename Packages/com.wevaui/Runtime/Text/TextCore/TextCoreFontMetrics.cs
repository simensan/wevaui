using System.Collections.Generic;
using Weva.Layout.Text;
using Weva.Paint;

namespace Weva.Text.TextCore {
    // TextCoreFontMetrics is the production IFontMetrics implementation. It
    // wraps an ITextCoreBackend (UnityFontEngineBackend in players, StubBackend
    // in headless tests) and a GlyphAtlas. Per-(face, size) face metrics are
    // cached so LineHeight/Ascent/Descent are O(1) after the first call.
    //
    // Line height policy (per spec deliverable §5):
    //   We use face.Ascent + face.Descent + face.LineGap when the face reports
    //   non-zero LineGap, otherwise we fall back to face.LineHeight (when
    //   present) or (Ascent + Descent) * 1.2 as a last resort. All values are
    //   normalized by face.UnitsPerEm and multiplied by fontSize.
    //
    // Caching:
    //   Two layers — face-level (LoadFace results), and per-size scaled
    //   metrics (LineHeight/Ascent/Descent/measured advances). The per-size
    //   cache is unbounded; for v1 we expect a small number of distinct
    //   (face, size) pairs per app session.
    //
    // Concurrency: single-threaded. UI updates happen on the Unity main thread.
    public sealed class TextCoreFontMetrics : IFontMetrics, IGlyphMetrics {
        public ITextCoreBackend Backend { get; }
        public GlyphAtlas Atlas { get; }
        public FaceInfo Face { get; }

        readonly Dictionary<double, ScaledMetrics> scaled = new();
        readonly Dictionary<long, double> advanceCache = new();
        FaceMetrics faceMetrics;
        bool faceLoaded;
        bool faceLoadFailed;

        struct ScaledMetrics {
            public double LineHeight;
            public double Ascent;
            public double Descent;
        }

        public TextCoreFontMetrics(ITextCoreBackend backend, FaceInfo face)
            : this(backend, face, new GlyphAtlas()) { }

        public TextCoreFontMetrics(ITextCoreBackend backend, FaceInfo face, GlyphAtlas atlas) {
            Backend = backend;
            Face = face;
            Atlas = atlas ?? new GlyphAtlas();
        }

        public static TextCoreFontMetrics Resolve(FontHandle handle, ITextCoreBackend backend, GlyphAtlas atlas) {
            var face = FontResolver.Resolve(handle);
            return new TextCoreFontMetrics(backend, face, atlas);
        }

        public double LineHeight(double fontSize) {
            if (!EnsureFaceLoaded()) return 0;
            var s = ScaleFor(fontSize);
            return s.LineHeight;
        }

        public double Ascent(double fontSize) {
            if (!EnsureFaceLoaded()) return 0;
            var s = ScaleFor(fontSize);
            return s.Ascent;
        }

        public double Descent(double fontSize) {
            if (!EnsureFaceLoaded()) return 0;
            var s = ScaleFor(fontSize);
            return s.Descent;
        }

        public double Measure(string text, double fontSize) {
            if (string.IsNullOrEmpty(text)) return 0;
            return Measure(text, 0, text.Length, fontSize);
        }

        // Substring-window overload: walk text[start .. start+length) without
        // allocating a fresh String per probe. See IFontMetrics.Measure(...)
        // and CODE_AUDIT_FINDINGS P7 for the LineBreaker wrap-path rationale.
        public double Measure(string text, int start, int length, double fontSize) {
            if (string.IsNullOrEmpty(text) || length <= 0) return 0;
            if (!EnsureFaceLoaded()) return 0;
            if (start < 0) { length += start; start = 0; }
            if (start >= text.Length) return 0;
            int end = start + length;
            if (end > text.Length) end = text.Length;
            double total = 0;
            int i = start;
            while (i < end) {
                char c = text[i];
                int len = 1;
                uint cp = c;
                if (char.IsHighSurrogate(c) && i + 1 < end && char.IsLowSurrogate(text[i + 1])) {
                    cp = (uint)char.ConvertToUtf32(c, text[i + 1]);
                    len = 2;
                }
                if (TryGetAdvance(cp, fontSize, out double adv)) {
                    total += adv;
                }
                i += len;
            }
            return total;
        }

        public bool TryGetAdvance(uint codepoint, double fontSize, out double advancePx) {
            advancePx = 0;
            if (!EnsureFaceLoaded()) return false;
            long key = MakeAdvanceKey(codepoint, fontSize);
            if (advanceCache.TryGetValue(key, out advancePx)) return true;
            if (Backend == null) return false;
            if (!Backend.TryGetGlyphAdvance(Face, codepoint, fontSize, out advancePx)) {
                advancePx = 0;
                return false;
            }
            advanceCache[key] = advancePx;
            return true;
        }

        public bool TryGetGlyphRect(uint codepoint, double fontSize, out GlyphRect rect) {
            return TryGetGlyph(codepoint, fontSize, out rect, out _);
        }

        // Like TryGetGlyphRect but also returns the glyph's physical metrics
        // (bearingX, bearingY, width, height). The text run baker uses these
        // to place each glyph quad at its real footprint instead of stretching
        // it across the advance × line-height cell, which produces the
        // characteristic SDF "smear" at small sizes.
        public bool TryGetGlyph(uint codepoint, double fontSize, out GlyphRect rect, out GlyphMetrics metrics) {
            return TryGetGlyph(codepoint, fontSize, out rect, out metrics, out _);
        }

        // Overload that also returns the per-glyph SDF padding so callers (the
        // SdfTextRunBaker) can inflate their quads by the exact padding the
        // rasterizer used. Bug #1: prior to this overload the baker hard-coded
        // 8 px while UnityFontEngineBackend's legacy stub used 9 px, producing
        // a 1 px shift + 2 px clip when the two paths met.
        public bool TryGetGlyph(uint codepoint, double fontSize, out GlyphRect rect, out GlyphMetrics metrics, out int paddingPx) {
            rect = GlyphRect.Empty;
            metrics = GlyphMetrics.Zero;
            paddingPx = 0;
            if (!EnsureFaceLoaded()) return false;
            if (Atlas.TryGetCachedRect(Face, codepoint, fontSize, out rect, out metrics, out paddingPx)) return true;
            // RequestGlyph (not Headless) uploads the rasterized bytes to the GPU
            // texture. Headless variant packs the slot but never calls UploadPixels —
            // suitable for unit tests that don't have a Texture2D, but in production
            // it leaves _GlyphAtlas as a zero-filled R8 page and every text quad
            // samples to black.
            return Atlas.RequestGlyph(Backend, Face, codepoint, fontSize, out rect, out metrics, out paddingPx);
        }

        public void InvalidateCaches() {
            scaled.Clear();
            advanceCache.Clear();
            faceLoaded = false;
            faceLoadFailed = false;
        }

        public int CachedScaleCount => scaled.Count;
        public int CachedAdvanceCount => advanceCache.Count;

        bool EnsureFaceLoaded() {
            if (faceLoaded) return true;
            if (faceLoadFailed) return false;
            if (Backend == null || !Face.IsValid) { faceLoadFailed = true; return false; }
            if (!Backend.LoadFace(Face, out faceMetrics)) { faceLoadFailed = true; return false; }
            faceLoaded = true;
            return true;
        }

        ScaledMetrics ScaleFor(double fontSize) {
            if (scaled.TryGetValue(fontSize, out var cached)) return cached;
            double upem = faceMetrics.UnitsPerEm;
            if (upem <= 0) upem = 1024;
            double scale = fontSize / upem;
            double ascent = faceMetrics.Ascent * scale;
            double descent = faceMetrics.Descent * scale;
            double lineHeight;
            if (faceMetrics.LineGap > 0) {
                lineHeight = (faceMetrics.Ascent + faceMetrics.Descent + faceMetrics.LineGap) * scale;
            } else if (faceMetrics.LineHeight > 0) {
                lineHeight = faceMetrics.LineHeight * scale;
            } else {
                lineHeight = (faceMetrics.Ascent + faceMetrics.Descent) * scale * 1.2;
            }
            var result = new ScaledMetrics { LineHeight = lineHeight, Ascent = ascent, Descent = descent };
            scaled[fontSize] = result;
            return result;
        }

        static long MakeAdvanceKey(uint codepoint, double fontSize) {
            long sizeBits = System.BitConverter.DoubleToInt64Bits(fontSize);
            return (sizeBits ^ ((long)codepoint << 1)) * 2654435761L;
        }
    }
}
