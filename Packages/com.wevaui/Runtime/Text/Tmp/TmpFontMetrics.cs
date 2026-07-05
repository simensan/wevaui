#if UNITY_2023_1_OR_NEWER && WEVA_TMP
using Weva.Layout.Text;
using Weva.Paint;
using Weva.Text.TextCore;

namespace Weva.Text.Tmp {
    // TmpFontMetrics implements the same IFontMetrics + IGlyphMetrics surface
    // SdfFontMetrics implements, but routes all measurement / advance / glyph
    // rect / line height queries through a TmpFontAssetSource. This is the
    // class SdfBootstrap.PickBest returns when a TMP_FontAsset is registered
    // for the requested family; the returned instance is API-compatible with
    // SdfFontMetrics so the layout pass and the SdfGlyphAtlasAdapter don't
    // need to special-case the source.
    //
    // What we delegate:
    //   - Per-codepoint advance: Source.TryGetGlyph -> metrics.AdvanceX.
    //   - Per-codepoint UV rect: Source.TryGetGlyph -> uvRect.
    //   - Line metrics: faceInfo.{ascentLine, descentLine, lineHeight} scaled
    //     by (fontSize / pointSize).
    //   - Kerning: Source.GetKern(left, right, fontSize). Plumbed through
    //     SdfTextRunBaker via the existing GetKern hook on SdfFontMetrics —
    //     but to avoid forcing every call site to reach for two metrics
    //     surfaces, we expose the same shape directly here.
    //
    // What we don't do:
    //   - We don't fall back to a runtime rasterizer. If the TMP atlas is
    //     missing a glyph, TryGet* returns false and the caller's existing
    //     fallback path takes over (CharacterFallback / synthetic glyph).
    //   - We don't model font-style or weight variants. The TMP_FontAsset is
    //     authored at one weight; a separate registration is required if the
    //     author wants bold or italic variants. The (style, weight) args here
    //     are ignored — a future revision can branch per-variant.
    public sealed class TmpFontMetrics : IFontMetrics, IStyledFontMetrics, IGlyphMetrics {
        public TmpFontAssetSource Source { get; }
        public FaceInfo Face => Source?.Face ?? FaceInfo.Empty;
        readonly System.Collections.Generic.Dictionary<VariantKey, UnityEngine.TextCore.Text.FontAsset> variantFontAssets = new();
        readonly System.Collections.Generic.Dictionary<int, VariantGlyphAddState> variantGlyphAddStates = new();
        readonly System.Text.StringBuilder variantGlyphAddScratch = new(32);

        // Optional fallback chain — when set, codepoints missing from the
        // primary `Source` are looked up against each fallback in turn so
        // emoji and dingbats baked into a separate face contribute their
        // advance to the line layout. Without this the layout pass measures
        // emoji as 0px wide and the paint pass skips them at the
        // `Bounds.IsEmpty` guard, leaving an empty hole where the glyph
        // should be. Populated by SdfBootstrap from
        // TmpFontAssetRegistry.GetChain(resolvedFamily).
        public System.Collections.Generic.IReadOnlyList<TmpFontAssetSource> Fallbacks { get; set; }

        public TmpFontMetrics(TmpFontAssetSource source) {
            Source = source;
        }

        // Resolve the first fallback face that has the codepoint. Returns
        // null when no fallback contains it (caller falls through to the
        // FontEngine raster path or skips the glyph).
        TmpFontAssetSource ResolveFace(uint codepoint, double fontSize) {
            if (Source != null && Source.TryGetGlyph(codepoint, fontSize, out _, out _)) return Source;
            if (Fallbacks == null) return null;
            bool isLatin = IsLatinLetterOrMark(codepoint);
            for (int j = 1; j < Fallbacks.Count; j++) {
                var f = Fallbacks[j];
                if (f == null || f.Asset == null) continue;
                if (isLatin && IsEmojiAsset(f.Asset)) continue;
                if (f.TryGetGlyph(codepoint, fontSize, out _, out _)) return f;
            }
            return null;
        }

        static bool IsEmojiAsset(TMPro.TMP_FontAsset asset) {
            if (asset == null) return false;
            var name = asset.name;
            return name != null && name.IndexOf("Emoji", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsLatinLetterOrMark(uint codepoint) {
            return (codepoint >= 0x00C0 && codepoint <= 0x00FF && codepoint != 0x00D7 && codepoint != 0x00F7)
                || (codepoint >= 0x0100 && codepoint <= 0x024F)
                || (codepoint >= 0x0300 && codepoint <= 0x036F)
                || (codepoint >= 0x1E00 && codepoint <= 0x1EFF);
        }

        // === IFontMetrics ===

        public double LineHeight(double fontSize) {
            if (Source == null) return 0;
            // TMP face metrics (ascentLine/descentLine/lineHeight) are stored
            // in pixels at the asset's bake point size. Scale by
            // fontSize/pointSize to render-pixel units.
            double scale = fontSize / System.Math.Max(1.0, Source.PointSize);
            double lh = Source.LineHeight * scale;
            if (lh > 0) return lh;
            return (Source.AscentLine - Source.DescentLine) * scale * 1.2;
        }

        public double Ascent(double fontSize) {
            if (Source == null) return 0;
            double scale = fontSize / System.Math.Max(1.0, Source.PointSize);
            return Source.AscentLine * scale;
        }

        public double Descent(double fontSize) {
            if (Source == null) return 0;
            double scale = fontSize / System.Math.Max(1.0, Source.PointSize);
            // TMP descentLine is typically a negative value; we return the
            // positive distance below baseline (matching FaceMetrics.Descent
            // convention used by TextCoreFontMetrics).
            return -Source.DescentLine * scale;
        }

        // PAINT-1: weight/style-aware overloads. TmpFontMetrics binds to a
        // single Source asset so the family/style/weight arguments don't
        // route to a different face — we simply delegate. The interface
        // surface still exists so layout can pass the resolved triple
        // without branching on metrics type.
        public double LineHeight(double fontSize, string family, Paint.FontStyle style, int weight) => LineHeight(fontSize);
        public double Ascent(double fontSize, string family, Paint.FontStyle style, int weight) => Ascent(fontSize);
        public double Descent(double fontSize, string family, Paint.FontStyle style, int weight) => Descent(fontSize);

        // External advance provider — when the TMP fallback chain doesn't
        // cover a codepoint (typically color-only emoji that TMP's
        // rasterizer can't extract), we ask this provider for the advance.
        // SdfBootstrap installs an ATG-backed implementation that uses
        // TextCore.Text.FontAsset (which CAN rasterize color emoji via a
        // different code path than TMP). Without this fall-through, layout
        // measures the emoji run as 0px wide → the paint pass drops it →
        // the emoji never reaches the renderer.
        public static System.Func<uint, double, double> ExternalAdvanceProvider;

        // Predicate for codepoints whose ATG render path uses a different
        // font asset than TMP's chain would resolve. For these codepoints
        // we MUST ask ExternalAdvanceProvider first — letting TMP's chain
        // answer would size the layout cell from a different font than the
        // renderer actually fills, leaving the glyph hugging one side of
        // an oversized cell. Concretely: text-default emoji codepoints
        // (↩ ⏸ ⚠) render via Segoe UI Symbol mono SDF (narrow advance),
        // but TMP's chain has Segoe UI Emoji COLOR with a much wider
        // advance for the same codepoint. Without this hook, layout uses
        // COLOR's advance and the rendered Symbol glyph appears off-center.
        public static System.Func<uint, bool> ExternalAdvancePreferred;

        public double Measure(string text, double fontSize) {
            if (string.IsNullOrEmpty(text) || Source == null) return 0;
            return Measure(text, 0, text.Length, fontSize);
        }

        // Substring-window overload. Walks text[start .. start+length) without
        // allocating a fresh String per probe. See IFontMetrics.Measure(...)
        // and CODE_AUDIT_FINDINGS P7/P8 for the LineBreaker rationale.
        public double Measure(string text, int start, int length, double fontSize) {
            if (string.IsNullOrEmpty(text) || Source == null || length <= 0) return 0;
            if (start < 0) { length += start; start = 0; }
            if (start >= text.Length) return 0;
            int n = start + length;
            if (n > text.Length) n = text.Length;
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
                // External-preferred codepoints route past TMP's chain so
                // the layout cell matches the render path's font choice.
                bool preferExternal = ExternalAdvancePreferred != null && ExternalAdvancePreferred(cp);
                if (preferExternal && ExternalAdvanceProvider != null) {
                    double a = ExternalAdvanceProvider(cp, fontSize);
                    if (a > 0) { total += a; goto kerning; }
                }
                // Walk the fallback chain so emoji (U+1F300+) and rare
                // dingbats baked into a separate face contribute their
                // advance — without this, layout measures the run as 0px
                // wide and the paint pass discards the glyph at the
                // Bounds.IsEmpty gate.
                var face = ResolveFace(cp, fontSize);
                if (face != null && face.TryGetGlyph(cp, fontSize, out _, out var gm)) {
                    total += gm.AdvanceX;
                } else if (ExternalAdvanceProvider != null) {
                    // No TMP face covers this codepoint. Fall through to the
                    // external provider (ATG / TextCore) — common for color
                    // emoji like 🔨 that TMP's COLOR raster fails on but
                    // Unity's other text path handles fine.
                    total += ExternalAdvanceProvider(cp, fontSize);
                }
                kerning:
                // Variation selector U+FE0F is zero-width by spec but Chrome
                // reserves a notional advance; matching the primary face's
                // behavior here is harmless because gm.AdvanceX is 0 when the
                // glyph table reports a zero-advance entry.
                if (prevCp != 0) total += Source.GetKern(prevCp, cp, fontSize);
                prevCp = cp;
                i += len;
            }
            return total;
        }

        public double Measure(string text, double fontSize, string family, FontStyle style, int weight) {
            if (string.IsNullOrEmpty(text) || Source == null) return 0;
            var variant = GetVariantFontAsset(style, weight);
            if (variant == null) return Measure(text, fontSize);
            return MeasureVariantFontAsset(variant, text, fontSize);
        }

        // Substring-window styled overload. For the variant-asset path we
        // still slice (TMP's CharacterLookupTable doesn't expose a substring-
        // window API), but the common no-variant case routes through the
        // window-friendly Measure overload above. See CODE_AUDIT_FINDINGS P7/P8.
        public double Measure(string text, int start, int length, double fontSize, string family, FontStyle style, int weight) {
            if (string.IsNullOrEmpty(text) || Source == null || length <= 0) return 0;
            var variant = GetVariantFontAsset(style, weight);
            if (variant == null) return Measure(text, start, length, fontSize);
            if (start < 0) { length += start; start = 0; }
            if (start >= text.Length) return 0;
            if (start + length > text.Length) length = text.Length - start;
            string slice = (start == 0 && length == text.Length) ? text : text.Substring(start, length);
            return MeasureVariantFontAsset(variant, slice, fontSize);
        }

        // === IGlyphMetrics ===

        public bool TryGetAdvance(uint codepoint, double fontSize, out double advancePx) {
            advancePx = 0;
            if (Source == null) return false;
            // External-preferred codepoints route past TMP's chain so the
            // measured advance matches the render path's font choice.
            if (ExternalAdvancePreferred != null && ExternalAdvancePreferred(codepoint)
                && ExternalAdvanceProvider != null) {
                double aPreferred = ExternalAdvanceProvider(codepoint, fontSize);
                if (aPreferred > 0) { advancePx = aPreferred; return true; }
            }
            // Walk the fallback chain — same justification as Measure().
            var face = ResolveFace(codepoint, fontSize);
            if (face == null) {
                // External fall-through for emoji codepoints TMP can't cover.
                if (ExternalAdvanceProvider != null) {
                    double a = ExternalAdvanceProvider(codepoint, fontSize);
                    if (a > 0) {
                        advancePx = a;
                        return true;
                    }
                }
                return false;
            }
            if (!face.TryGetGlyph(codepoint, fontSize, out _, out var gm)) return false;
            advancePx = gm.AdvanceX;
            return true;
        }

        public bool TryGetGlyphRect(uint codepoint, double fontSize, out GlyphRect rect) {
            rect = GlyphRect.Empty;
            if (Source == null) return false;
            var face = ResolveFace(codepoint, fontSize);
            if (face == null) return false;
            return face.TryGetGlyph(codepoint, fontSize, out rect, out _);
        }

        // Extended TryGetGlyph variant that mirrors TextCoreFontMetrics's
        // signature so SdfTextRunBaker can drive both backends through the
        // same call shape (and read padding for quad inflation).
        // TMP atlases bake padding INTO the glyph rect already (atlasPadding),
        // so we report it back to the baker via the out paddingPx.
        public bool TryGetGlyph(uint codepoint, double fontSize, out GlyphRect rect, out GlyphMetrics metrics, out int paddingPx) {
            rect = GlyphRect.Empty;
            metrics = GlyphMetrics.Zero;
            paddingPx = 0;
            if (Source == null) return false;
            if (!Source.TryGetGlyph(codepoint, fontSize, out rect, out metrics)) return false;
            // TMP padding is applied at bake time; the glyphRect already covers
            // glyph + spread. Report it scaled to render pixels so the baker's
            // quad inflation matches the visible footprint.
            int atlasPad = Source.Asset != null ? Source.Asset.atlasPadding : 0;
            double scale = fontSize / System.Math.Max(1.0, Source.PointSize);
            paddingPx = (int)System.Math.Round(atlasPad * scale);
            return true;
        }

        public double GetKern(uint left, uint right, double fontSize) {
            if (Source == null) return 0;
            return Source.GetKern(left, right, fontSize);
        }

        UnityEngine.TextCore.Text.FontAsset GetVariantFontAsset(FontStyle style, int weight) {
            string styleName = StyleNameFor(style, weight);
            if (styleName == "Regular") return null;
            var key = new VariantKey(Source.Asset != null ? Source.Asset.faceInfo.familyName : null, styleName);
            if (variantFontAssets.TryGetValue(key, out var cached)) return cached;
            if (string.IsNullOrEmpty(key.Family)) {
                variantFontAssets[key] = null;
                return null;
            }
            UnityEngine.TextCore.Text.FontAsset asset = null;
            try {
                asset = UnityEngine.TextCore.Text.FontAsset.CreateFontAsset(
                    key.Family,
                    key.StyleName,
                    pointSize: 90,
                    padding: 9,
                    renderMode: UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA_HINTED);
                if (asset != null) asset.hideFlags = UnityEngine.HideFlags.HideAndDontSave;
            } catch {
                asset = null;
            }
            if (asset != null && !StyleMatches(asset.faceInfo.styleName, key.StyleName)) {
                if (UnityEngine.Application.isPlaying) UnityEngine.Object.Destroy(asset);
                else UnityEngine.Object.DestroyImmediate(asset);
                asset = null;
            }
            variantFontAssets[key] = asset;
            return asset;
        }

        double MeasureVariantFontAsset(UnityEngine.TextCore.Text.FontAsset asset, string text, double fontSize) {
            if (asset == null || string.IsNullOrEmpty(text)) return 0;
            if (!EnsureVariantGlyphs(asset, text)) {
                return Measure(text, fontSize);
            }
            double scale = fontSize / System.Math.Max(1.0, asset.faceInfo.pointSize);
            try {
                var lookup = asset.characterLookupTable;
                double total = 0;
                int i = 0;
                while (i < text.Length) {
                    int start = i;
                    int len = 1;
                    uint cp = text[i];
                    if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) {
                        cp = (uint)char.ConvertToUtf32(text[i], text[i + 1]);
                        len = 2;
                    }
                    if (lookup.TryGetValue(cp, out var ch) && ch?.glyph != null) {
                        total += ch.glyph.metrics.horizontalAdvance * scale;
                    } else {
                        total += Measure(text.Substring(start, len), fontSize);
                    }
                    i += len;
                }
                return total;
            } catch {
                return Measure(text, fontSize);
            }
        }

        bool EnsureVariantGlyphs(UnityEngine.TextCore.Text.FontAsset asset, string text) {
            int key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(asset);
            if (!variantGlyphAddStates.TryGetValue(key, out var state)) {
                state = new VariantGlyphAddState();
                variantGlyphAddStates[key] = state;
            }

            try {
                var lookup = asset.characterLookupTable;
                variantGlyphAddScratch.Clear();
                int i = 0;
                while (i < text.Length) {
                    uint cp = ReadCodepoint(text, ref i);
                    if (lookup != null && lookup.ContainsKey(cp)) {
                        state.Attempted.Add(cp);
                        continue;
                    }
                    if (!state.Attempted.Add(cp)) continue;
                    AppendCodepoint(variantGlyphAddScratch, cp);
                }
                if (variantGlyphAddScratch.Length == 0) return true;
                asset.TryAddCharacters(variantGlyphAddScratch.ToString(), out _, false);
                return true;
            } catch {
                return false;
            }
        }

        static uint ReadCodepoint(string text, ref int index) {
            char c = text[index++];
            if (char.IsHighSurrogate(c) && index < text.Length && char.IsLowSurrogate(text[index])) {
                return (uint)char.ConvertToUtf32(c, text[index++]);
            }
            return c;
        }

        static void AppendCodepoint(System.Text.StringBuilder sb, uint codepoint) {
            if (codepoint <= 0xFFFF) {
                sb.Append((char)codepoint);
                return;
            }
            int scalar = (int)codepoint - 0x10000;
            sb.Append((char)((scalar >> 10) + 0xD800));
            sb.Append((char)((scalar & 0x3FF) + 0xDC00));
        }

        static string StyleNameFor(FontStyle style, int weight) {
            bool italic = style == FontStyle.Italic || style == FontStyle.Oblique;
            if (weight >= 700) return italic ? "Bold Italic" : "Bold";
            if (weight >= 600) return italic ? "Semibold Italic" : "Semibold";
            if (italic) return "Italic";
            return "Regular";
        }

        static bool StyleMatches(string actual, string requested) {
            if (string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(requested)) return false;
            return actual.IndexOf(requested, System.StringComparison.OrdinalIgnoreCase) >= 0
                || requested.IndexOf(actual, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        readonly struct VariantKey : System.IEquatable<VariantKey> {
            public readonly string Family;
            public readonly string StyleName;

            public VariantKey(string family, string styleName) {
                Family = family;
                StyleName = styleName;
            }

            public bool Equals(VariantKey other) {
                return string.Equals(Family, other.Family, System.StringComparison.OrdinalIgnoreCase)
                    && string.Equals(StyleName, other.StyleName, System.StringComparison.OrdinalIgnoreCase);
            }

            public override bool Equals(object obj) => obj is VariantKey k && Equals(k);

            public override int GetHashCode() {
                unchecked {
                    int h = Family != null ? System.StringComparer.OrdinalIgnoreCase.GetHashCode(Family) : 0;
                    h = (h * 397) ^ (StyleName != null ? System.StringComparer.OrdinalIgnoreCase.GetHashCode(StyleName) : 0);
                    return h;
                }
            }
        }

        sealed class VariantGlyphAddState {
            public readonly System.Collections.Generic.HashSet<uint> Attempted = new();
        }
    }
}
#endif
