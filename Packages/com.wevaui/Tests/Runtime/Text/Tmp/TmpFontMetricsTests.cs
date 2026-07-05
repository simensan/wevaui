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
    // TmpFontMetricsTests verifies that the IFontMetrics surface
    // (Measure, Ascent, Descent, LineHeight, TryGetAdvance) returns values
    // consistent with the underlying TMP_FontAsset's faceInfo and glyph
    // metrics. We don't compare against a live TMP_Text MonoBehaviour
    // (that would require a Canvas + UGUI surface and is gold-plating);
    // we compare against the same arithmetic the TMP renderer uses
    // (sum of horizontalAdvance scaled by fontSize/pointSize).
    public class TmpFontMetricsTests {
        // Reuse the same builder logic as TmpFontAssetSourceTests but kept
        // local to avoid cross-test dependencies.
        static TMP_FontAsset BuildTestAsset() {
            var asset = ScriptableObject.CreateInstance<TMP_FontAsset>();
            var faceInfo = default(FaceInfo);
            SetStructField(ref faceInfo, "m_FamilyName", "TestFamily");
            SetStructField(ref faceInfo, "m_StyleName", "Regular");
            SetStructField(ref faceInfo, "m_PointSize", 90f);
            SetStructField(ref faceInfo, "m_Scale", 1.0f);
            SetStructField(ref faceInfo, "m_LineHeight", 100f);
            SetStructField(ref faceInfo, "m_AscentLine", 80f);
            SetStructField(ref faceInfo, "m_DescentLine", -20f);
            SetStructField(ref faceInfo, "m_UnitsPerEM", 1000f);
            SetField(asset, "m_FaceInfo", faceInfo, includeBase: true);
            SetField(asset, "m_AtlasWidth", 256);
            SetField(asset, "m_AtlasHeight", 256);
            SetField(asset, "m_AtlasPadding", 9);
            var tex = new Texture2D(256, 256, TextureFormat.R8, false, true);
            SetField(asset, "m_AtlasTexture", tex);
            SetField(asset, "m_AtlasTextures", new[] { tex });
            SetField(asset, "m_AtlasTextureIndex", 0);
            var glyphA = new Glyph(10,
                new UnityEngine.TextCore.GlyphMetrics(32, 40, 2, 30, 36),
                new UnityEngine.TextCore.GlyphRect(16, 16, 32, 40), 1f, 0);
            var glyphV = new Glyph(20,
                new UnityEngine.TextCore.GlyphMetrics(30, 40, 1, 30, 32),
                new UnityEngine.TextCore.GlyphRect(64, 16, 30, 40), 1f, 0);
            SetField(asset, "m_GlyphTable", new List<Glyph> { glyphA, glyphV });
            var charA = new TMP_Character(65u, glyphA);
            var charV = new TMP_Character(86u, glyphV);
            SetField(asset, "m_CharacterTable", new List<TMP_Character> { charA, charV });
            SetField(asset, "m_GlyphLookupDictionary",
                new Dictionary<uint, Glyph> { { 10u, glyphA }, { 20u, glyphV } });
            SetField(asset, "m_CharacterLookupDictionary",
                new Dictionary<uint, TMP_Character> { { 65u, charA }, { 86u, charV } });
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
        public void Measure_at_pointsize_sums_advances() {
            var asset = BuildTestAsset();
            var metrics = new TmpFontMetrics(new TmpFontAssetSource(asset));
            // "AV" at point size 90: A.advance(36) + kern(-3) + V.advance(32) = 65.
            double w = metrics.Measure("AV", fontSize: 90);
            Assert.That(w, Is.EqualTo(65.0).Within(1e-5));
        }

        [Test]
        public void Measure_scales_with_render_size() {
            var asset = BuildTestAsset();
            var metrics = new TmpFontMetrics(new TmpFontAssetSource(asset));
            // Half the size -> half the width.
            double w = metrics.Measure("AV", fontSize: 45);
            Assert.That(w, Is.EqualTo(32.5).Within(1e-5));
        }

        [Test]
        public void Ascent_descent_lineheight_scale_with_size() {
            var asset = BuildTestAsset();
            var metrics = new TmpFontMetrics(new TmpFontAssetSource(asset));
            // At point size 90 (scale = 1): ascent=80, descent=20 (positive),
            // lineHeight=100.
            Assert.That(metrics.Ascent(90), Is.EqualTo(80.0).Within(1e-6));
            Assert.That(metrics.Descent(90), Is.EqualTo(20.0).Within(1e-6));
            Assert.That(metrics.LineHeight(90), Is.EqualTo(100.0).Within(1e-6));
            // At half size -> half values.
            Assert.That(metrics.Ascent(45), Is.EqualTo(40.0).Within(1e-6));
            Assert.That(metrics.LineHeight(45), Is.EqualTo(50.0).Within(1e-6));
        }

        [Test]
        public void TryGetAdvance_returns_glyph_advance() {
            var asset = BuildTestAsset();
            var metrics = new TmpFontMetrics(new TmpFontAssetSource(asset));
            Assert.That(metrics.TryGetAdvance(65u, fontSize: 90, out double adv), Is.True);
            Assert.That(adv, Is.EqualTo(36.0).Within(1e-6));
        }

        [Test]
        public void TryGetGlyph_returns_padding_scaled_to_render_size() {
            var asset = BuildTestAsset();
            var metrics = new TmpFontMetrics(new TmpFontAssetSource(asset));
            // atlasPadding = 9 in the asset; at the asset's point size (90)
            // the scale is 1 so padding should round-trip exactly.
            Assert.That(metrics.TryGetGlyph(65u, fontSize: 90, out _, out _, out int pad), Is.True);
            Assert.That(pad, Is.EqualTo(9));
        }

        [Test]
        public void Empty_text_measures_to_zero() {
            var asset = BuildTestAsset();
            var metrics = new TmpFontMetrics(new TmpFontAssetSource(asset));
            Assert.That(metrics.Measure("", 16), Is.EqualTo(0));
            Assert.That(metrics.Measure(null, 16), Is.EqualTo(0));
        }

        [Test]
        public void Face_is_valid_and_carries_family_name() {
            var asset = BuildTestAsset();
            var metrics = new TmpFontMetrics(new TmpFontAssetSource(asset));
            Assert.That(metrics.Face.IsValid, Is.True);
            Assert.That(metrics.Face.Family, Is.EqualTo("TestFamily"));
        }
    }
}
#endif
