#if UNITY_2023_1_OR_NEWER && WEVA_TMP
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;
using Weva.Text.Sdf;
using Weva.Text.Tmp;
using Object = UnityEngine.Object;

namespace Weva.Tests.Text.Tmp {
    // Verifies the SdfBootstrap.PickBest decision tree:
    //   * Empty registry -> falls through to SdfFontMetrics or its successors
    //     (UnityGUIFontMetrics / MonoFontMetrics depending on environment).
    //   * Registered TMP_FontAsset -> returns TmpFontMetrics.
    //
    // These are integration tests in the sense that they exercise the public
    // bootstrap API; they don't run the URP rendering pipeline.
    public class TmpBootstrapIntegrationTests {
        TMP_FontAsset asset;

        [SetUp]
        public void Setup() {
            TmpFontAssetRegistry.Clear();
            AtlasRegistry.Clear();
        }

        [TearDown]
        public void Teardown() {
            TmpFontAssetRegistry.Clear();
            AtlasRegistry.Clear();
            if (asset != null) {
                if (Application.isPlaying) UnityEngine.Object.Destroy(asset);
                else UnityEngine.Object.DestroyImmediate(asset);
                asset = null;
            }
        }

        [Test]
        public void Empty_registry_does_not_return_TmpFontMetrics() {
            // Without any TMP registration, PickBest should NOT return a
            // TmpFontMetrics instance — the FontEngine path (or its fallbacks)
            // should win.
            var metrics = SdfBootstrap.PickBest();
            Assert.That(metrics, Is.Not.Null);
            Assert.That(metrics, Is.Not.InstanceOf<TmpFontMetrics>());
        }

        [Test]
        public void Registered_TMP_asset_is_returned_by_PickBest() {
            asset = BuildTestAsset();
            TmpFontAssetRegistry.RegisterFontAsset("sans-serif", asset);
            var metrics = SdfBootstrap.PickBest();
            Assert.That(metrics, Is.InstanceOf<TmpFontMetrics>());
            var tmp = (TmpFontMetrics)metrics;
            Assert.That(tmp.Source, Is.Not.Null);
            Assert.That(tmp.Source.Asset, Is.SameAs(asset));
        }

        [Test]
        public void Registered_TMP_asset_registers_face_in_AtlasRegistry() {
            asset = BuildTestAsset();
            TmpFontAssetRegistry.RegisterFontAsset("sans-serif", asset);
            var metrics = (TmpFontMetrics)SdfBootstrap.PickBest();
            // The bootstrap should have registered the TMP face against
            // AtlasRegistry so the URP renderer can resolve the right
            // texture id at draw time.
            var atlas = AtlasRegistry.GetAtlas(metrics.Face);
            Assert.That(atlas, Is.Not.Null);
            int id = AtlasRegistry.GetAtlasId(atlas);
            Assert.That(id, Is.Not.EqualTo(0));
            // GetTextureById should return the TMP atlas texture (overridden
            // on the GlyphAtlas shell).
            Assert.That(AtlasRegistry.GetTextureById(id), Is.SameAs(asset.atlasTexture));
        }

        // === Asset builder (mirrors TmpFontAssetSourceTests; kept local to
        // avoid a cross-test dependency that would force shared state.) ===

        static TMP_FontAsset BuildTestAsset() {
            var a = ScriptableObject.CreateInstance<TMP_FontAsset>();
            var faceInfo = default(FaceInfo);
            SetStructField(ref faceInfo, "m_FamilyName", "BootstrapTest");
            SetStructField(ref faceInfo, "m_StyleName", "Regular");
            SetStructField(ref faceInfo, "m_PointSize", 90f);
            SetStructField(ref faceInfo, "m_Scale", 1f);
            SetStructField(ref faceInfo, "m_LineHeight", 100f);
            SetStructField(ref faceInfo, "m_AscentLine", 80f);
            SetStructField(ref faceInfo, "m_DescentLine", -20f);
            SetStructField(ref faceInfo, "m_UnitsPerEM", 1000f);
            SetField(a, "m_FaceInfo", faceInfo, includeBase: true);
            SetField(a, "m_AtlasWidth", 256);
            SetField(a, "m_AtlasHeight", 256);
            SetField(a, "m_AtlasPadding", 9);
            var tex = new Texture2D(256, 256, TextureFormat.R8, false, true);
            SetField(a, "m_AtlasTexture", tex);
            SetField(a, "m_AtlasTextures", new[] { tex });
            SetField(a, "m_AtlasTextureIndex", 0);
            var glyphA = new Glyph(10,
                new UnityEngine.TextCore.GlyphMetrics(32, 40, 2, 30, 36),
                new UnityEngine.TextCore.GlyphRect(16, 16, 32, 40), 1f, 0);
            SetField(a, "m_GlyphTable", new List<Glyph> { glyphA });
            var charA = new TMP_Character(65u, glyphA);
            SetField(a, "m_CharacterTable", new List<TMP_Character> { charA });
            SetField(a, "m_GlyphLookupDictionary", new Dictionary<uint, Glyph> { { 10u, glyphA } });
            SetField(a, "m_CharacterLookupDictionary", new Dictionary<uint, TMP_Character> { { 65u, charA } });
            SetField(a, "m_FontFeatureTable", new TMP_FontFeatureTable());
            return a;
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
    }
}
#endif
