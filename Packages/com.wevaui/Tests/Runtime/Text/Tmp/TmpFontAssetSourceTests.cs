#if UNITY_2023_1_OR_NEWER && WEVA_TMP
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;
using Weva.Text.Tmp;

namespace Weva.Tests.Text.Tmp {
    // These tests build a TMP_FontAsset in-memory by populating its
    // internal fields via reflection. We avoid relying on a pre-baked
    // .asset file in the package because the brief says we should NOT
    // author a TMP_FontAsset in this PR — the user creates one via the
    // Font Asset Creator. The reflection-built asset is equivalent in
    // shape (faceInfo, characterTable, glyphTable, fontFeatureTable)
    // to one produced by the Editor tool.
    public class TmpFontAssetSourceTests {
        // Build a tiny TMP_FontAsset with two glyphs: 'A' (cp=65, gi=10) and
        // 'V' (cp=86, gi=20). Atlas is 256x256 with both glyphs at known
        // pixel positions. Kerning table includes one (A, V) pair adjustment.
        static TMP_FontAsset BuildTestAsset(int atlasW = 256, int atlasH = 256, int pointSize = 90, int atlasPadding = 9) {
            var asset = ScriptableObject.CreateInstance<TMP_FontAsset>();

            // FaceInfo: set via reflection through the m_FaceInfo backing
            // field (TMP_Asset is the base; the field is internal).
            var faceInfo = default(FaceInfo);
            // FaceInfo is a struct with public fields. Use reflection to set
            // family/point size/ascent/descent/lineHeight/unitsPerEM.
            SetStructField(ref faceInfo, "m_FamilyName", "TestFamily");
            SetStructField(ref faceInfo, "m_StyleName", "Regular");
            SetStructField(ref faceInfo, "m_PointSize", (float)pointSize);
            SetStructField(ref faceInfo, "m_Scale", 1.0f);
            SetStructField(ref faceInfo, "m_LineHeight", 100f);
            SetStructField(ref faceInfo, "m_AscentLine", 80f);
            SetStructField(ref faceInfo, "m_DescentLine", -20f);
            SetStructField(ref faceInfo, "m_UnitsPerEM", 1000f);

            SetField(asset, "m_FaceInfo", faceInfo, includeBase: true);
            SetField(asset, "m_AtlasWidth", atlasW);
            SetField(asset, "m_AtlasHeight", atlasH);
            SetField(asset, "m_AtlasPadding", atlasPadding);

            // Build atlas texture (R8 black; we don't actually sample bytes
            // in unit tests).
            var tex = new Texture2D(atlasW, atlasH, TextureFormat.R8, false, true);
            tex.name = "TestTmpAtlas";
            SetField(asset, "m_AtlasTexture", tex);
            SetField(asset, "m_AtlasTextures", new[] { tex });
            SetField(asset, "m_AtlasTextureIndex", 0);

            // Glyph 'A' (gi=10): rect (16,16,32,40), bearings (2,30), advance 36.
            // Use positional construction; Unity's GlyphMetrics ctor parameter
            // names vary slightly between versions, but the order
            // (width, height, bearingX, bearingY, advance) is stable.
            var glyphA = new Glyph(10,
                new GlyphMetrics(32, 40, 2, 30, 36),
                new UnityEngine.TextCore.GlyphRect(16, 16, 32, 40),
                scale: 1.0f, atlasIndex: 0);
            // Glyph 'V' (gi=20): rect (64,16,30,40), bearings (1,30), advance 32.
            var glyphV = new Glyph(20,
                new GlyphMetrics(30, 40, 1, 30, 32),
                new UnityEngine.TextCore.GlyphRect(64, 16, 30, 40),
                scale: 1.0f, atlasIndex: 0);

            var glyphList = new List<Glyph> { glyphA, glyphV };
            SetField(asset, "m_GlyphTable", glyphList);

            // Character table: maps codepoints to glyph indices.
            var charA = new TMP_Character(65u, glyphA);
            var charV = new TMP_Character(86u, glyphV);
            var charList = new List<TMP_Character> { charA, charV };
            SetField(asset, "m_CharacterTable", charList);

            // Build dictionaries by hand (TMP normally builds them in
            // ReadFontAssetDefinition; we sidestep that to keep the test
            // hermetic). The TmpFontAssetSource calls glyphLookupTable /
            // characterLookupTable which lazily build them; the public
            // accessors will reuse what we set here.
            var glyphLookup = new Dictionary<uint, Glyph> { { 10u, glyphA }, { 20u, glyphV } };
            SetField(asset, "m_GlyphLookupDictionary", glyphLookup);
            var charLookup = new Dictionary<uint, TMP_Character> { { 65u, charA }, { 86u, charV } };
            SetField(asset, "m_CharacterLookupDictionary", charLookup);

            // Kerning: one (A, V) pair with -3 horizontal advance.
            var pair = new GlyphPairAdjustmentRecord(
                new GlyphAdjustmentRecord(10u, new GlyphValueRecord(0, 0, -3, 0)),
                new GlyphAdjustmentRecord(20u, new GlyphValueRecord(0, 0, 0, 0))
            );
            var feat = new TMP_FontFeatureTable();
            feat.glyphPairAdjustmentRecords = new List<GlyphPairAdjustmentRecord> { pair };
            SetField(asset, "m_FontFeatureTable", feat);

            return asset;
        }

        static void SetField(object target, string name, object value, bool includeBase = false) {
            var t = target.GetType();
            FieldInfo fi = null;
            while (t != null && fi == null) {
                fi = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (!includeBase && fi == null) break;
                t = t.BaseType;
            }
            if (fi == null) throw new MissingFieldException(target.GetType().FullName, name);
            fi.SetValue(target, CoerceTo(value, fi.FieldType));
        }

        static void SetStructField<TStruct>(ref TStruct s, string name, object value) where TStruct : struct {
            // Boxing trick: reflect on the boxed copy, write back.
            object boxed = s;
            var fi = typeof(TStruct).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (fi == null) throw new MissingFieldException(typeof(TStruct).FullName, name);
            fi.SetValue(boxed, CoerceTo(value, fi.FieldType));
            s = (TStruct)boxed;
        }

        // TMP's internal numeric field types vary across package versions
        // (e.g. m_AtlasWidth/m_AtlasHeight have flipped between int and
        // float). Tests author values as plain literals; coerce to the
        // declared field type so FieldInfo.SetValue doesn't throw
        // "cannot be converted" for primitive widening/narrowing.
        // Null, reference, or already-assignable values pass through
        // unchanged so string/struct/object fields are untouched.
        static object CoerceTo(object value, Type targetType) {
            if (value == null) return null;
            if (targetType.IsInstanceOfType(value)) return value;
            if (value is IConvertible && (targetType.IsPrimitive || targetType == typeof(decimal))) {
                return Convert.ChangeType(value, targetType);
            }
            return value;
        }

        [Test]
        public void TryGetGlyph_returns_correct_uv_for_known_codepoint() {
            var asset = BuildTestAsset(atlasW: 256, atlasH: 256);
            var src = new TmpFontAssetSource(asset);
            // Render at the asset's point size so the scale factor is 1.
            Assert.That(src.TryGetGlyph(65u, fontSize: 90, out var uv, out var gm), Is.True);
            // 'A' glyph rect is (16,16,32,40) on a 256x256 atlas. The UV
            // returned by TryGetGlyph is INFLATED by `atlasPadding` atlas-
            // pixels on every edge so the shader has access to the SDF
            // spread ring outside the silhouette — see the comment on
            // TmpFontAssetSource.TryGetGlyph. BuildTestAsset uses
            // atlasPadding = 9, so each edge expands by 9.
            const int pad = 9;
            Assert.That(uv.U0, Is.EqualTo((16f - pad) / 256f).Within(1e-5f));
            Assert.That(uv.V0, Is.EqualTo((16f - pad) / 256f).Within(1e-5f));
            Assert.That(uv.U1, Is.EqualTo((16f + 32f + pad) / 256f).Within(1e-5f));
            Assert.That(uv.V1, Is.EqualTo((16f + 40f + pad) / 256f).Within(1e-5f));
            // Metrics at point size: advance=36, bearingX=2, bearingY=30, width=32, height=40.
            Assert.That(gm.AdvanceX, Is.EqualTo(36.0).Within(1e-6));
            Assert.That(gm.BearingX, Is.EqualTo(2.0).Within(1e-6));
            Assert.That(gm.BearingY, Is.EqualTo(30.0).Within(1e-6));
            Assert.That(gm.Width, Is.EqualTo(32.0).Within(1e-6));
            Assert.That(gm.Height, Is.EqualTo(40.0).Within(1e-6));
        }

        [Test]
        public void TryGetGlyph_scales_metrics_by_render_size_over_pointsize() {
            var asset = BuildTestAsset(pointSize: 90);
            var src = new TmpFontAssetSource(asset);
            // Render at half the point size — advance and bearings should halve.
            Assert.That(src.TryGetGlyph(65u, fontSize: 45, out _, out var gm), Is.True);
            Assert.That(gm.AdvanceX, Is.EqualTo(18.0).Within(1e-6));
            Assert.That(gm.BearingX, Is.EqualTo(1.0).Within(1e-6));
            Assert.That(gm.BearingY, Is.EqualTo(15.0).Within(1e-6));
            Assert.That(gm.Width, Is.EqualTo(16.0).Within(1e-6));
            Assert.That(gm.Height, Is.EqualTo(20.0).Within(1e-6));
        }

        [Test]
        public void TryGetGlyph_returns_false_for_unknown_codepoint() {
            var asset = BuildTestAsset();
            var src = new TmpFontAssetSource(asset);
            Assert.That(src.TryGetGlyph(0xFFFFu, fontSize: 16, out _, out _), Is.False);
        }

        [Test]
        public void GetKern_returns_nonzero_for_AV_pair_at_pointsize() {
            var asset = BuildTestAsset(pointSize: 90);
            var src = new TmpFontAssetSource(asset);
            // Pair (A, V) was set with first.xAdvance = -3 in font units.
            // At point size, scale = 1 so kern = -3.
            double k = src.GetKern(65u, 86u, fontSize: 90);
            Assert.That(k, Is.EqualTo(-3.0).Within(1e-6));
        }

        [Test]
        public void GetKern_scales_by_render_size_over_pointsize() {
            var asset = BuildTestAsset(pointSize: 90);
            var src = new TmpFontAssetSource(asset);
            // Half the point size -> half the kern.
            double k = src.GetKern(65u, 86u, fontSize: 45);
            Assert.That(k, Is.EqualTo(-1.5).Within(1e-6));
        }

        [Test]
        public void GetKern_returns_zero_for_unknown_pair() {
            var asset = BuildTestAsset();
            var src = new TmpFontAssetSource(asset);
            // (V, A) — reversed order, not in the table.
            Assert.That(src.GetKern(86u, 65u, fontSize: 90), Is.EqualTo(0.0));
        }

        [Test]
        public void Atlas_property_returns_asset_atlasTexture() {
            var asset = BuildTestAsset();
            var src = new TmpFontAssetSource(asset);
            Assert.That(src.Atlas, Is.Not.Null);
            Assert.That(src.Atlas, Is.SameAs(asset.atlasTexture));
        }

        [Test]
        public void Face_translates_familyName_and_styleFlags() {
            var asset = BuildTestAsset();
            var src = new TmpFontAssetSource(asset);
            var face = src.Face;
            Assert.That(face.IsValid, Is.True);
            Assert.That(face.Family, Is.EqualTo("TestFamily"));
            Assert.That(face.StyleFlags, Is.EqualTo(Weva.Text.TextCore.FaceInfo.StyleNormal));
        }

        [Test]
        public void CachedGlyphCount_reflects_character_table_size() {
            var asset = BuildTestAsset();
            var src = new TmpFontAssetSource(asset);
            Assert.That(src.CachedGlyphCount, Is.EqualTo(2));
        }
    }
}
#endif
