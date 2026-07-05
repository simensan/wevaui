#if UNITY_2023_1_OR_NEWER && WEVA_TMP
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;
using Weva.Paint;
using Weva.Rendering.URP;
using Weva.Text.Sdf;
using Weva.Text.TextCore;
using Weva.Text.Tmp;
using PaintRect = Weva.Paint.Rect;
using FontStyle = Weva.Paint.FontStyle;
using FaceInfo = UnityEngine.TextCore.FaceInfo;
using GlyphAtlas = Weva.Text.TextCore.GlyphAtlas;
using Object = UnityEngine.Object;

namespace Weva.Tests.Text.Tmp {
    // Verifies the font-fallback chain: when a codepoint is missing from the
    // primary TMP_FontAsset but present in a registered fallback, ShapeTmp
    // resolves it from the fallback's atlas and tags the emitted SdfGlyphQuad
    // with the fallback atlas's id so UIBatcher can split batches at the
    // boundary.
    public class TmpFontFallbackChainTests {
        TMP_FontAsset primary;
        TMP_FontAsset fallback;

        [SetUp]
        public void Setup() {
            TmpFontAssetRegistry.Clear();
            AtlasRegistry.Clear();
        }

        [TearDown]
        public void Teardown() {
            TmpFontAssetRegistry.Clear();
            AtlasRegistry.Clear();
            DestroyAsset(ref primary);
            DestroyAsset(ref fallback);
        }

        [Test]
        public void Registry_AddFallback_extends_chain_length() {
            primary = BuildAssetWithChars(new[] { (uint)'A' }, "Primary");
            fallback = BuildAssetWithChars(new[] { (uint)'B' }, "Fallback");

            TmpFontAssetRegistry.RegisterFontAsset("sans-serif", primary);
            Assert.That(TmpFontAssetRegistry.ChainLength("sans-serif"), Is.EqualTo(1));

            TmpFontAssetRegistry.AddFallback("sans-serif", fallback);
            Assert.That(TmpFontAssetRegistry.ChainLength("sans-serif"), Is.EqualTo(2));

            var chain = TmpFontAssetRegistry.GetChain("sans-serif");
            Assert.That(chain, Is.Not.Null);
            Assert.That(chain.Count, Is.EqualTo(2));
            Assert.That(chain[0].Asset, Is.SameAs(primary));
            Assert.That(chain[1].Asset, Is.SameAs(fallback));
        }

        [Test]
        public void Registry_TryGet_returns_primary_only() {
            primary = BuildAssetWithChars(new[] { (uint)'A' }, "Primary");
            fallback = BuildAssetWithChars(new[] { (uint)'B' }, "Fallback");

            TmpFontAssetRegistry.RegisterFontAsset("sans-serif", primary);
            TmpFontAssetRegistry.AddFallback("sans-serif", fallback);

            Assert.That(TmpFontAssetRegistry.TryGet("sans-serif", out var got), Is.True);
            Assert.That(got.Asset, Is.SameAs(primary));
        }

        [Test]
        public void Registry_RegisterFontAsset_preserves_fallback_chain() {
            primary = BuildAssetWithChars(new[] { (uint)'A' }, "Primary");
            fallback = BuildAssetWithChars(new[] { (uint)'B' }, "Fallback");

            TmpFontAssetRegistry.RegisterFontAsset("sans-serif", primary);
            TmpFontAssetRegistry.AddFallback("sans-serif", fallback);
            Assert.That(TmpFontAssetRegistry.ChainLength("sans-serif"), Is.EqualTo(2));

            // Re-registering the primary REPLACES the primary slot only and
            // keeps fallbacks intact (ReplacePrimaryKeepFallbacks). Critical
            // for the SdfBootstrap → play-mode-controller flow: SdfBootstrap
            // registers Segoe UI SDF + emoji fallbacks, then a controller's
            // PinFont re-registers Segoe UI SDF as primary, and we must
            // preserve the emoji fallback or non-ASCII codepoints (✕ — ⚠)
            // silently drop to placeholders.
            TmpFontAssetRegistry.RegisterFontAsset("sans-serif", primary);
            Assert.That(TmpFontAssetRegistry.ChainLength("sans-serif"), Is.EqualTo(2));
        }

        [Test]
        public void Registry_AddFallback_no_op_without_primary() {
            fallback = BuildAssetWithChars(new[] { (uint)'B' }, "Fallback");
            // No primary registered -> AddFallback should be a no-op (no
            // throw, no entry created).
            TmpFontAssetRegistry.AddFallback("sans-serif", fallback);
            Assert.That(TmpFontAssetRegistry.ChainLength("sans-serif"), Is.EqualTo(0));
            Assert.That(TmpFontAssetRegistry.IsRegistered("sans-serif"), Is.False);
        }

        [Test]
        public void Adapter_resolves_glyph_from_primary_atlas() {
            primary = BuildAssetWithChars(new[] { (uint)'A' }, "Primary");
            fallback = BuildAssetWithChars(new[] { (uint)'B' }, "Fallback");
            var (adapter, primaryAtlasId, fallbackAtlasId) = BuildAdapter();

            var output = new List<SdfGlyphQuad>();
            var cmd = MakeCommand("A");
            bool ok = adapter.TryShape(cmd, output, out int atlasId);

            Assert.That(ok, Is.True);
            Assert.That(atlasId, Is.EqualTo(primaryAtlasId));
            Assert.That(output.Count, Is.GreaterThanOrEqualTo(1));
            // Primary glyphs carry AtlasId=0 (= use the run's atlasId).
            Assert.That(output[0].AtlasId, Is.EqualTo(0));
        }

        [Test]
        public void Adapter_resolves_glyph_from_fallback_atlas() {
            primary = BuildAssetWithChars(new[] { (uint)'A' }, "Primary");
            fallback = BuildAssetWithChars(new[] { (uint)'B' }, "Fallback");
            var (adapter, primaryAtlasId, fallbackAtlasId) = BuildAdapter();
            Assert.That(fallbackAtlasId, Is.Not.EqualTo(0));
            Assert.That(fallbackAtlasId, Is.Not.EqualTo(primaryAtlasId));

            var output = new List<SdfGlyphQuad>();
            // 'B' is only in the fallback. The chain walk should find it
            // there, emit a quad tagged with the fallback's atlasId, and
            // advance the cursor.
            var cmd = MakeCommand("B");
            bool ok = adapter.TryShape(cmd, output, out int atlasId);

            Assert.That(ok, Is.True);
            // Find the glyph quad (decorations are absent for plain text).
            Assert.That(output.Count, Is.GreaterThanOrEqualTo(1));
            // The glyph quad should carry the fallback's atlasId (non-zero
            // because it does NOT inherit the run-level atlasId).
            int found = 0;
            foreach (var q in output) {
                if (q.AtlasId == fallbackAtlasId) found++;
            }
            Assert.That(found, Is.GreaterThanOrEqualTo(1),
                "Expected at least one quad tagged with the fallback atlas id");
        }

        [Test]
        public void Adapter_walks_chain_for_mixed_run() {
            primary = BuildAssetWithChars(new[] { (uint)'A' }, "Primary");
            fallback = BuildAssetWithChars(new[] { (uint)'B' }, "Fallback");
            var (adapter, primaryAtlasId, fallbackAtlasId) = BuildAdapter();

            var output = new List<SdfGlyphQuad>();
            var cmd = MakeCommand("AB");
            bool ok = adapter.TryShape(cmd, output, out int atlasId);
            Assert.That(ok, Is.True);
            Assert.That(atlasId, Is.EqualTo(primaryAtlasId));

            // Expect one quad from primary (AtlasId=0) and one from fallback
            // (AtlasId=fallbackAtlasId).
            int primaryQuads = 0, fallbackQuads = 0;
            foreach (var q in output) {
                if (q.AtlasId == 0) primaryQuads++;
                else if (q.AtlasId == fallbackAtlasId) fallbackQuads++;
            }
            Assert.That(primaryQuads, Is.GreaterThanOrEqualTo(1),
                "Expected at least one primary-atlas glyph for 'A'");
            Assert.That(fallbackQuads, Is.GreaterThanOrEqualTo(1),
                "Expected at least one fallback-atlas glyph for 'B'");
        }

        [Test]
        public void Adapter_without_chain_emits_zero_atlasid_for_all_quads() {
            // Single-face baseline: when no chain is wired, every emitted
            // quad must have AtlasId == 0 so the back-compat path through
            // SubmitGlyphQuads stays a single batch.
            primary = BuildAssetWithChars(new[] { (uint)'A' }, "Primary");
            var source = new TmpFontAssetSource(primary);
            var adapter = new SdfGlyphAtlasAdapter(null, null, null) {
                TmpSource = source,
                TmpFace = source.Face,
                TmpAtlas = WrapAsAtlas(source),
                TmpChain = null
            };
            // Register so the adapter can resolve atlasId.
            AtlasRegistry.RegisterAtlas(adapter.TmpFace, adapter.TmpAtlas);

            var output = new List<SdfGlyphQuad>();
            bool ok = adapter.TryShape(MakeCommand("A"), output, out int atlasId);
            Assert.That(ok, Is.True);
            foreach (var q in output) {
                Assert.That(q.AtlasId, Is.EqualTo(0),
                    "Single-face shaping must leave per-quad AtlasId at 0");
            }
        }

        // === Helpers ===

        (SdfGlyphAtlasAdapter adapter, int primaryAtlasId, int fallbackAtlasId) BuildAdapter() {
            var primarySource = new TmpFontAssetSource(primary);
            var fallbackSource = new TmpFontAssetSource(fallback);

            var primaryAtlas = WrapAsAtlas(primarySource);
            AtlasRegistry.RegisterAtlas(primarySource.Face, primaryAtlas);
            int primaryAtlasId = AtlasRegistry.GetAtlasId(primaryAtlas);

            var chain = new List<TmpFontAssetSource> { primarySource, fallbackSource };

            var adapter = new SdfGlyphAtlasAdapter(null, null, null) {
                TmpSource = primarySource,
                TmpFace = primarySource.Face,
                TmpAtlas = primaryAtlas,
                TmpChain = chain
            };
            // Trigger lazy fallback-atlas registration by shaping once with
            // the fallback codepoint — easier than reaching into private
            // state. We discard the output. Alternatively we can do a single
            // probe to cause EnsureTmpChainAtlases to run.
            var probeOut = new List<SdfGlyphQuad>();
            adapter.TryShape(MakeCommand("B"), probeOut, out _);

            int fallbackAtlasId = AtlasRegistry.GetAtlasId(AtlasRegistry.GetAtlas(fallbackSource.Face));
            return (adapter, primaryAtlasId, fallbackAtlasId);
        }

        static GlyphAtlas WrapAsAtlas(TmpFontAssetSource source) {
            var atlas = new GlyphAtlas();
            atlas.TextureOverride = source.Atlas;
            return atlas;
        }

        static DrawTextCommand MakeCommand(string text) {
            return new DrawTextCommand(
                new PaintRect(0, 0, 200, 40),
                text,
                new FontHandle("sans-serif", 24, 400, FontStyle.Normal),
                LinearColor.White,
                TextDecoration.None);
        }

        TMP_FontAsset BuildAssetWithChars(uint[] codepoints, string familyName) {
            var asset = ScriptableObject.CreateInstance<TMP_FontAsset>();
            var faceInfo = default(FaceInfo);
            SetStructField(ref faceInfo, "m_FamilyName", familyName);
            SetStructField(ref faceInfo, "m_StyleName", "Regular");
            SetStructField(ref faceInfo, "m_PointSize", 90f);
            SetStructField(ref faceInfo, "m_Scale", 1f);
            SetStructField(ref faceInfo, "m_LineHeight", 100f);
            SetStructField(ref faceInfo, "m_AscentLine", 80f);
            SetStructField(ref faceInfo, "m_DescentLine", -20f);
            SetStructField(ref faceInfo, "m_UnitsPerEM", 1000f);
            SetField(asset, "m_FaceInfo", faceInfo, includeBase: true);
            SetField(asset, "m_AtlasWidth", 256);
            SetField(asset, "m_AtlasHeight", 256);
            SetField(asset, "m_AtlasPadding", 9);
            var tex = new Texture2D(256, 256, TextureFormat.R8, false, true);
            tex.name = familyName + "Atlas";
            SetField(asset, "m_AtlasTexture", tex);
            SetField(asset, "m_AtlasTextures", new[] { tex });
            SetField(asset, "m_AtlasTextureIndex", 0);

            var glyphTable = new List<UnityEngine.TextCore.Glyph>();
            var charTable = new List<TMP_Character>();
            var glyphLookup = new Dictionary<uint, UnityEngine.TextCore.Glyph>();
            var charLookup = new Dictionary<uint, TMP_Character>();
            uint gi = 10;
            foreach (var cp in codepoints) {
                var g = new UnityEngine.TextCore.Glyph(gi,
                    new UnityEngine.TextCore.GlyphMetrics(32, 40, 2, 30, 36),
                    new UnityEngine.TextCore.GlyphRect(16, 16, 32, 40), 1f, 0);
                glyphTable.Add(g);
                glyphLookup[gi] = g;
                var ch = new TMP_Character(cp, g);
                charTable.Add(ch);
                charLookup[cp] = ch;
                gi++;
            }
            SetField(asset, "m_GlyphTable", glyphTable);
            SetField(asset, "m_CharacterTable", charTable);
            SetField(asset, "m_GlyphLookupDictionary", glyphLookup);
            SetField(asset, "m_CharacterLookupDictionary", charLookup);
            SetField(asset, "m_FontFeatureTable", new TMP_FontFeatureTable());
            return asset;
        }

        static void DestroyAsset(ref TMP_FontAsset a) {
            if (a == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(a);
            else UnityEngine.Object.DestroyImmediate(a);
            a = null;
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
