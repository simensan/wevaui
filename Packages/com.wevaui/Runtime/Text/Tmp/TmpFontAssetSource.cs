#if UNITY_2023_1_OR_NEWER && WEVA_TMP
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using TmpGlyph = UnityEngine.TextCore.Glyph;
using OurGlyphRect = Weva.Text.TextCore.GlyphRect;
using OurGlyphMetrics = Weva.Text.TextCore.GlyphMetrics;
using OurFaceInfo = Weva.Text.TextCore.FaceInfo;

namespace Weva.Text.Tmp {
    // TmpFontAssetSource wraps a TMPro.TMP_FontAsset and exposes the data we
    // need to drive our own SDF text path: an atlas Texture2D, per-codepoint
    // glyph metrics + UV rects in our own GlyphRect/GlyphMetrics types, kerning
    // pair adjustments, and a face-info translation so callers can scale font-
    // unit values to render-pixel values via (fontSize / pointSize).
    //
    // We DO NOT instantiate any TMP_Text MonoBehaviours. The atlas asset is the
    // authoritative bytes; we treat TMP as a glyph-data backend.
    //
    // Workflow for authors (documented here so the consumer of the API knows
    // how to populate one):
    //   1. In the Editor, open Window > TextMeshPro > Font Asset Creator.
    //   2. Pick a source font, character set, atlas resolution, render mode
    //      (SDFAA recommended), padding, and click Generate Font Atlas.
    //      "Save as..." into the project as MyFont SDF.asset.
    //   3. At app startup, call:
    //        TmpFontAssetRegistry.RegisterFontAsset("sans-serif", myAsset);
    //      Our SdfBootstrap will then route glyph queries for that family
    //      through this source instead of the runtime FontEngine rasterizer.
    public sealed class TmpFontAssetSource {
        public TMP_FontAsset Asset { get; }

        // The face's NATURAL CSS weight (TMP's faceInfo doesn't carry one).
        // Callers that know the face's real weight — e.g. registering a
        // Sniglet ExtraBold face — set this so the paint path's faux-bold only
        // synthesizes the gap ABOVE it (an 800 face asked for 800 gets no
        // synthesis). Defaults to 400 (regular).
        public int NaturalWeight { get; set; } = 400;

        // Cached lookup: codepoint -> Glyph (resolved through TMP's character
        // table -> glyphIndex -> glyphLookupTable). Populated lazily so the
        // first call rebuilds the dictionaries inside TMP if needed.
        readonly Dictionary<uint, TmpGlyph> codepointToGlyph = new();
        // Cached pair lookup: (firstGlyphIndex, secondGlyphIndex) packed -> adv.
        // Built lazily on first GetKern call.
        Dictionary<ulong, double> kernCache;

        bool tablesPrimed;

        public TmpFontAssetSource(TMP_FontAsset asset) {
            Asset = asset;
        }

        public Texture2D Atlas {
            get {
                if (Asset == null) return null;
                return Asset.atlasTexture;
            }
        }

        // True when the asset's atlas is a 4-channel color image (RGBA32)
        // rather than the monochrome alpha texture produced by SDFAA / SMOOTH
        // bakes. The URP renderer keys its _TEXT_COLOR shader variant off
        // this so the per-fragment path samples RGBA directly instead of
        // doing the SDF coverage smoothstep used for monochrome atlases.
        //
        // We probe the texture format because TMP exposes the render mode as
        // an internal flag enum (GlyphRasterModes) we can't reference without
        // pulling in TMP internals. RGBA32 / RGBAHalf / etc. all imply a
        // color bake; Alpha8 / R8 / RFloat imply mono SDF.
        public bool IsColor {
            get {
                var tex = Atlas;
                if (tex == null) return false;
                var fmt = tex.format;
                return fmt == UnityEngine.TextureFormat.RGBA32
                    || fmt == UnityEngine.TextureFormat.RGB24
                    || fmt == UnityEngine.TextureFormat.BGRA32
                    || fmt == UnityEngine.TextureFormat.ARGB32
                    || fmt == UnityEngine.TextureFormat.RGBAHalf
                    || fmt == UnityEngine.TextureFormat.RGBAFloat;
            }
        }

        // Translates the TMP face-info into our FaceInfo wrapper. The Family is
        // taken from the TMP_FontAsset's faceInfo.familyName; Path is left
        // empty (TMP is a pre-baked asset, not a file path); Weight/Style come
        // from the asset's faceInfo where available.
        public OurFaceInfo Face {
            get {
                if (Asset == null) return OurFaceInfo.Empty;
                var f = Asset.faceInfo;
                int style = string.IsNullOrEmpty(f.styleName)
                    ? OurFaceInfo.StyleNormal
                    : (f.styleName.IndexOf("Italic", System.StringComparison.OrdinalIgnoreCase) >= 0
                        ? OurFaceInfo.StyleItalic
                        : OurFaceInfo.StyleNormal);
                // TMP's faceInfo doesn't expose a numeric weight, so the caller
                // declares the face's NATURAL weight at registration (a Sniglet
                // ExtraBold face is 800). The paint path uses this to decide
                // faux-bold: it synthesizes only the gap ABOVE the natural
                // weight, so an already-bold face isn't double-bolded. Bake it
                // into the path so distinct (asset, weight) pairs hash apart.
                int weight = NaturalWeight;
                string path = "tmp:" + (Asset.name ?? f.familyName ?? "") + ":" + weight;
                return new OurFaceInfo(f.familyName ?? "tmp", path, weight, style);
            }
        }

        // PointSize is the font size at which the atlas was baked; advance and
        // bearings are scaled by (renderSize / pointSize) to get pixels.
        public double PointSize {
            get {
                if (Asset == null) return 0;
                return Asset.faceInfo.pointSize;
            }
        }

        public double AscentLine => Asset?.faceInfo.ascentLine ?? 0;
        public double DescentLine => Asset?.faceInfo.descentLine ?? 0;
        public double LineHeight => Asset?.faceInfo.lineHeight ?? 0;

        // Look up a codepoint and produce both the atlas-UV rect (0..1 against
        // atlas dimensions) and per-glyph metrics in pixels at the requested
        // fontSize. Returns false when the codepoint isn't in the asset's
        // character table — the caller is expected to fall through to the
        // runtime rasterizer for that glyph.
        public bool TryGetGlyph(uint codepoint, double fontSize, out OurGlyphRect uvRect, out OurGlyphMetrics metrics) {
            uvRect = OurGlyphRect.Empty;
            metrics = OurGlyphMetrics.Zero;
            if (Asset == null) return false;
            EnsureTables();
            if (!codepointToGlyph.TryGetValue(codepoint, out var glyph) || glyph == null) {
                return false;
            }

            // Atlas-pixel rect -> 0..1 UV. TMP's GlyphRect uses bottom-left origin
            // (y measured from atlas bottom); our shader samples with y growing
            // downward, but our existing SdfGlyphAtlas also flips internally —
            // we keep the same sign convention as our atlas so the renderer
            // uses one formula for both sources. Verified by SdfTextRendering
            // golden tests under TmpFontMetrics.
            int aw = Asset.atlasWidth > 0 ? Asset.atlasWidth : Asset.atlasTexture.width;
            int ah = Asset.atlasHeight > 0 ? Asset.atlasHeight : Asset.atlasTexture.height;
            if (aw <= 0 || ah <= 0) return false;

            // Inflate the UV rect by atlasPadding atlas-pixels in each direction so
            // the shader can sample the SDF spread region (the ring of valid signed-
            // distance values rendered AROUND the glyph silhouette at bake time).
            //
            // Without this inflation, UV = glyph bounding box only — the SDF spread
            // is in adjacent atlas pixels we never sample. Smoothstep AA + stem
            // darkening + weight bias all need data OUTSIDE the silhouette to
            // feather correctly; on the un-inflated path they ran on bilinear-
            // filtered raw silhouette and the apparent AA was just texture filter
            // softness, not real distance-field math.
            //
            // SdfTextRunBaker already inflates the display quad by `2 * paddingPx`
            // (= round(atlasPadding * fontSize/pointSize) display-px per side).
            // The match isn't perfect — `paddingPx` is rounded to int — so a
            // ~0.3 display-pixel mismatch remains, visible as ~5% silhouette
            // over-display vs the "natural" size. That's a separate cleanup
            // (would require threading paddingPx as a double through the bake
            // chain); for now we accept it because eliminating the silhouette
            // stretch entirely would also visibly shrink every glyph 5–6%.
            int atlasPad = Asset.atlasPadding;
            var r = glyph.glyphRect;
            float u0 = (float)(r.x - atlasPad) / aw;
            float v0 = (float)(r.y - atlasPad) / ah;
            float u1 = (float)(r.x + r.width + atlasPad) / aw;
            float v1 = (float)(r.y + r.height + atlasPad) / ah;
            uvRect = new OurGlyphRect(u0, v0, u1, v1);

            double scale = fontSize / System.Math.Max(1.0, PointSize);
            var gm = glyph.metrics;
            metrics = new OurGlyphMetrics(
                advanceX: gm.horizontalAdvance * scale,
                bearingX: gm.horizontalBearingX * scale,
                bearingY: gm.horizontalBearingY * scale,
                width: gm.width * scale,
                height: gm.height * scale
            );
            return true;
        }

        // Returns the kerning advance correction in pixels for the given
        // (left, right) codepoint pair at the given font size. Returns 0 if
        // the asset has no kerning table or the pair is not present.
        public double GetKern(uint leftCp, uint rightCp, double fontSize) {
            if (Asset == null) return 0;
            EnsureTables();
            if (!codepointToGlyph.TryGetValue(leftCp, out var leftGlyph) || leftGlyph == null) return 0;
            if (!codepointToGlyph.TryGetValue(rightCp, out var rightGlyph) || rightGlyph == null) return 0;
            EnsureKernCache();
            if (kernCache == null || kernCache.Count == 0) return 0;
            ulong key = ((ulong)leftGlyph.index << 32) | rightGlyph.index;
            if (!kernCache.TryGetValue(key, out double adjust)) return 0;
            // adjust is in font units (xAdvance from the first record); scale
            // to pixels via (fontSize / pointSize).
            double scale = fontSize / System.Math.Max(1.0, PointSize);
            return adjust * scale;
        }

        void EnsureTables() {
            if (tablesPrimed) return;
            tablesPrimed = true;
            if (Asset == null) return;
            // Touching characterLookupTable forces TMP to call ReadFontAssetDefinition
            // which builds the internal dictionaries — important for assets that
            // were just deserialized from disk.
            var charTable = Asset.characterTable;
            var glyphLookup = Asset.glyphLookupTable;
            if (charTable == null || glyphLookup == null) return;
            for (int i = 0; i < charTable.Count; i++) {
                var ch = charTable[i];
                if (ch == null) continue;
                uint cp = ch.unicode;
                uint gi = ch.glyphIndex;
                if (glyphLookup.TryGetValue(gi, out var glyph) && glyph != null) {
                    codepointToGlyph[cp] = glyph;
                }
            }
        }

        void EnsureKernCache() {
            if (kernCache != null) return;
            kernCache = new Dictionary<ulong, double>();
            if (Asset == null) return;
            var table = Asset.fontFeatureTable;
            if (table == null) return;
            var pairs = table.glyphPairAdjustmentRecords;
            if (pairs == null) return;
            for (int i = 0; i < pairs.Count; i++) {
                var p = pairs[i];
                ulong key = ((ulong)p.firstAdjustmentRecord.glyphIndex << 32) | p.secondAdjustmentRecord.glyphIndex;
                // OpenType GPOS pair adjustment splits the correction across the
                // first and second records' xAdvance fields; the cumulative
                // horizontal adjustment to apply between the two glyphs is the
                // sum (HarfBuzz's behavior). The first.xAdvance is the dominant
                // term in TMP-baked tables; second.xAdvance is usually 0 but
                // we add it for correctness.
                double adv = p.firstAdjustmentRecord.glyphValueRecord.xAdvance
                           + p.secondAdjustmentRecord.glyphValueRecord.xAdvance;
                kernCache[key] = adv;
            }
        }

        // Test/diagnostic surface: number of cached codepoint mappings.
        public int CachedGlyphCount {
            get {
                EnsureTables();
                return codepointToGlyph.Count;
            }
        }

        // Test surface: number of cached kerning pairs.
        public int CachedKernCount {
            get {
                EnsureKernCache();
                return kernCache?.Count ?? 0;
            }
        }
    }
}
#endif
