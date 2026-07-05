using System.Collections.Generic;
using Weva.Layout.Text;
using Weva.Paint;
using Weva.Text.TextCore;

namespace Weva.Text.Sdf {
    // SdfFontMetrics is the v1 production IFontMetrics. It resolves a (family,
    // style, weight) triple to a FaceInfo via FontLoader, then defers per-face
    // measurements to a per-face TextCoreFontMetrics instance. This composition
    // means we get:
    //   - per-glyph advance widths (no monospace),
    //   - face-driven ascent/descent/line-height (real ratios per font),
    //   - per-(face, size) caching that's already exercised by
    //     TextCoreFontMetricsTests,
    //   - GlyphAtlas integration (registered with AtlasRegistry for the URP
    //     backend to bind during DrawText).
    //
    // The IFontMetrics interface itself doesn't carry style/weight. Layout passes
    // a font-family string into LayoutContext.GetMetrics; we expose richer
    // measurement via MetricsFor(family, style, weight) for callers that have
    // computed style on hand. The interface methods (LineHeight/Measure/Ascent/
    // Descent) use the default (regular, 400) variant — matching existing tests.
    //
    // Subpixel: Measure returns full-precision sums; the caller (LineBreaker)
    // is responsible for rounding only at line endings. We never pre-round.
    //
    // Kerning: TextCore exposes per-glyph-pair adjustments via FontFeatureTable
    // on Unity. The seam is here (TryGetKern); v1 returns 0 for all pairs but
    // the structure is present so the URP-text agent can wire it.
    public sealed class SdfFontMetrics : IFontMetrics, IStyledFontMetrics, IGlyphMetrics {
        public FontLoader Loader { get; }
        public ITextCoreBackend Backend { get; }
        public GlyphAtlas Atlas { get; }
        public string DefaultFamily { get; set; } = "sans-serif";
        public int DefaultWeight { get; set; } = 400;

        readonly Dictionary<FaceInfo, TextCoreFontMetrics> perFace = new();
        readonly Dictionary<FaceKey, FaceInfo> faceLookup = new();

        public SdfFontMetrics(FontLoader loader, ITextCoreBackend backend, GlyphAtlas atlas = null) {
            Loader = loader;
            Backend = backend;
            Atlas = atlas ?? new GlyphAtlas();
        }

        // === IFontMetrics ===

        public double LineHeight(double fontSize) => MetricsFor(DefaultFamily, FontStyle.Normal, DefaultWeight)?.LineHeight(fontSize) ?? 0;
        public double Ascent(double fontSize) => MetricsFor(DefaultFamily, FontStyle.Normal, DefaultWeight)?.Ascent(fontSize) ?? 0;
        public double Descent(double fontSize) => MetricsFor(DefaultFamily, FontStyle.Normal, DefaultWeight)?.Descent(fontSize) ?? 0;

        public double Measure(string text, double fontSize) {
            if (string.IsNullOrEmpty(text)) return 0;
            return MeasureText(text, 0, text.Length, FontStyle.Normal, DefaultWeight, DefaultFamily, fontSize);
        }

        public double Measure(string text, double fontSize, string family, FontStyle style, int weight) {
            if (string.IsNullOrEmpty(text)) return 0;
            return MeasureText(text, 0, text.Length, style, weight, string.IsNullOrEmpty(family) ? DefaultFamily : family, fontSize);
        }

        // Substring-window overloads. See IFontMetrics / IStyledFontMetrics
        // and CODE_AUDIT_FINDINGS P7/P8 for the LineBreaker rationale.
        public double Measure(string text, int start, int length, double fontSize) {
            if (string.IsNullOrEmpty(text) || length <= 0) return 0;
            return MeasureText(text, start, length, FontStyle.Normal, DefaultWeight, DefaultFamily, fontSize);
        }

        public double Measure(string text, int start, int length, double fontSize, string family, FontStyle style, int weight) {
            if (string.IsNullOrEmpty(text) || length <= 0) return 0;
            return MeasureText(text, start, length, style, weight, string.IsNullOrEmpty(family) ? DefaultFamily : family, fontSize);
        }

        // PAINT-1: weight/style-aware line-box metrics. Routes to the same
        // per-face TextCoreFontMetrics that paint's SdfTextRunBaker uses, so
        // layout's line-box height and paint's baseline placement agree even
        // when a span declares a non-default font-weight.
        public double LineHeight(double fontSize, string family, FontStyle style, int weight) {
            var m = MetricsFor(string.IsNullOrEmpty(family) ? DefaultFamily : family, style, weight);
            return m?.LineHeight(fontSize) ?? 0;
        }
        public double Ascent(double fontSize, string family, FontStyle style, int weight) {
            var m = MetricsFor(string.IsNullOrEmpty(family) ? DefaultFamily : family, style, weight);
            return m?.Ascent(fontSize) ?? 0;
        }
        public double Descent(double fontSize, string family, FontStyle style, int weight) {
            var m = MetricsFor(string.IsNullOrEmpty(family) ? DefaultFamily : family, style, weight);
            return m?.Descent(fontSize) ?? 0;
        }

        // === Style/weight-aware measurement ===

        public double MeasureText(string text, FontStyle style, int weight, string family, double fontSize) {
            if (string.IsNullOrEmpty(text)) return 0;
            return MeasureText(text, 0, text.Length, style, weight, family, fontSize);
        }

        public double MeasureText(string text, int start, int length, FontStyle style, int weight, string family, double fontSize) {
            if (string.IsNullOrEmpty(text) || length <= 0) return 0;
            var m = MetricsFor(family ?? DefaultFamily, style, weight);
            if (m == null) return 0;
            if (start < 0) { length += start; start = 0; }
            if (start >= text.Length) return 0;
            int n = start + length;
            if (n > text.Length) n = text.Length;
            // Don't pre-round per glyph — let advance accumulate at full precision.
            // Caller rounds only at line endings (per brief deliverable §2).
            double total = 0;
            int i = start;
            uint prevCp = 0;
            while (i < n) {
                char c = text[i];
                int len = 1;
                uint cp = c;
                if (char.IsHighSurrogate(c) && i + 1 < n && char.IsLowSurrogate(text[i + 1])) {
                    cp = (uint)char.ConvertToUtf32(c, text[i + 1]);
                    len = 2;
                }
                if (m.TryGetAdvance(cp, fontSize, out double adv)) total += adv;
                if (prevCp != 0) total += GetKern(m, prevCp, cp, fontSize);
                prevCp = cp;
                i += len;
            }
            return total;
        }

        // === IGlyphMetrics ===

        public bool TryGetAdvance(uint codepoint, double fontSize, out double advancePx) {
            var m = MetricsFor(DefaultFamily, FontStyle.Normal, DefaultWeight);
            if (m == null) { advancePx = 0; return false; }
            return m.TryGetAdvance(codepoint, fontSize, out advancePx);
        }

        public bool TryGetGlyphRect(uint codepoint, double fontSize, out GlyphRect rect) {
            var m = MetricsFor(DefaultFamily, FontStyle.Normal, DefaultWeight);
            if (m == null) { rect = GlyphRect.Empty; return false; }
            return m.TryGetGlyphRect(codepoint, fontSize, out rect);
        }

        // === Lookup ===

        public TextCoreFontMetrics MetricsFor(string family, FontStyle style, int weight) {
            if (Loader == null) return null;
            string famKey = string.IsNullOrEmpty(family) ? DefaultFamily : family;
            if (weight <= 0) weight = DefaultWeight;
            var fk = new FaceKey(famKey, style, weight);
            if (!faceLookup.TryGetValue(fk, out var face)) {
                // FontLoader handles its own caching, but we cache the (key -> face)
                // mapping here to avoid re-tokenizing the family string.
                face = Loader.Load(famKey, style, weight);
                faceLookup[fk] = face;
            }
            if (!face.IsValid) return null;
            if (!perFace.TryGetValue(face, out var metrics)) {
                metrics = new TextCoreFontMetrics(Backend, face, Atlas);
                perFace[face] = metrics;
                AtlasRegistry.RegisterAtlas(face, Atlas);
            }
            return metrics;
        }

        public FaceInfo FaceFor(string family, FontStyle style, int weight) {
            var m = MetricsFor(family, style, weight);
            return m?.Face ?? FaceInfo.Empty;
        }

        public int CachedFaceCount => perFace.Count;

        public void InvalidateCaches() {
            foreach (var kv in perFace) kv.Value.InvalidateCaches();
            perFace.Clear();
            faceLookup.Clear();
        }

        // Kerning hook. v1 returns 0 — the FontFeatureTable wiring through
        // TextCore.Text.FontAsset is deferred until the URP-text agent ships.
        // The signature matches what HarfBuzz's hb_font_get_pair_adjustment
        // returns: a single horizontal advance correction in design units,
        // scaled here to pixels. Tests inject a stub via WithKernProvider.
        public double GetKern(TextCoreFontMetrics m, uint left, uint right, double fontSize) {
            if (kernProvider == null) return 0;
            return kernProvider(m.Face, left, right, fontSize);
        }

        System.Func<FaceInfo, uint, uint, double, double> kernProvider;
        public SdfFontMetrics WithKernProvider(System.Func<FaceInfo, uint, uint, double, double> provider) {
            kernProvider = provider;
            return this;
        }

        readonly struct FaceKey : System.IEquatable<FaceKey> {
            public readonly string Family;
            public readonly FontStyle Style;
            public readonly int Weight;

            public FaceKey(string family, FontStyle style, int weight) {
                Family = family;
                Style = style;
                Weight = weight;
            }

            public bool Equals(FaceKey other) {
                return string.Equals(Family, other.Family, System.StringComparison.OrdinalIgnoreCase)
                    && Style == other.Style && Weight == other.Weight;
            }

            public override bool Equals(object obj) => obj is FaceKey k && Equals(k);

            public override int GetHashCode() {
                unchecked {
                    int h = Family != null ? System.StringComparer.OrdinalIgnoreCase.GetHashCode(Family) : 0;
                    h = (h * 397) ^ (int)Style;
                    h = (h * 397) ^ Weight;
                    return h;
                }
            }
        }
    }
}
