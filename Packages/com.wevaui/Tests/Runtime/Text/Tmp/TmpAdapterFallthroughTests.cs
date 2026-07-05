#if UNITY_2023_1_OR_NEWER && WEVA_TMP
using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using Weva.Paint;
using Weva.Rendering.URP;
using Weva.Text.Sdf;
using Weva.Text.TextCore;
using Weva.Text.Tmp;
// Aliases disambiguate types that exist in both UnityEngine.TextCore (the
// TMP-side data structures) and Weva.Text.TextCore (our own metrics).
using TmpGlyph = UnityEngine.TextCore.Glyph;
using TmpGlyphMetrics = UnityEngine.TextCore.GlyphMetrics;
using TmpGlyphRect = UnityEngine.TextCore.GlyphRect;
using TmpFaceInfo = UnityEngine.TextCore.FaceInfo;
using PaintRect = Weva.Paint.Rect;
using FontStyle = Weva.Paint.FontStyle;
using OurFaceInfo = Weva.Text.TextCore.FaceInfo;
using Object = UnityEngine.Object;

namespace Weva.Tests.Text.Tmp {
    // Regression tests for the SdfGlyphAtlasAdapter TMP <-> FontEngine
    // fall-through. The bug being guarded against:
    //
    //   Author registers a TMP_FontAsset for "sans-serif" that contains a
    //   SUBSET of the glyphs needed by the run (e.g. "A","e","r","i" but
    //   missing "t","h"," ","S","o","m","b","n"). ShapeTmp emits a glyph
    //   for each codepoint it CAN find but does not advance the cursor for
    //   ones it can't. With the old code this returned `tmpProduced > 0`
    //   and reported success, leaving the run with overlapping glyphs at
    //   the cursor position of the first missing letter.
    //
    //   New invariant: TMP shaping is authoritative ONLY when it covers
    //   every renderable codepoint in the run. Any miss triggers a rollback
    //   and the FontEngine path takes over for the entire run, so the
    //   final quad list contains EXACTLY one quad per non-whitespace
    //   character and no quads from the (discarded) TMP attempt.
    public class TmpAdapterFallthroughTests {
        TMP_FontAsset asset;
        TMP_FontAsset fallbackAsset;

        [SetUp]
        public void Setup() {
            FontResolver.ClearRegistered();
            FontResolver.SetSystemDefaults(new Dictionary<string, string> {
                ["sans-serif"] = "/test/sans.ttf"
            });
            AtlasRegistry.Clear();
            TmpFontAssetRegistry.Clear();
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
            if (fallbackAsset != null) {
                if (Application.isPlaying) UnityEngine.Object.Destroy(fallbackAsset);
                else UnityEngine.Object.DestroyImmediate(fallbackAsset);
                fallbackAsset = null;
            }
        }

        sealed class StubLoader : FontLoader.IFaceLoader {
            public bool TryLoad(string family, FontStyle style, int weight, out OurFaceInfo face) {
                int styleFlags = style == FontStyle.Italic ? OurFaceInfo.StyleItalic : OurFaceInfo.StyleNormal;
                face = new OurFaceInfo(family, family + "/" + weight, weight, styleFlags);
                return true;
            }
        }

        // Builds an adapter wired to:
        //   * a TMP source containing only the glyphs in `tmpCodepoints`
        //   * a FontEngine baker (StubBackend) that can shape any codepoint
        // This mirrors the real bootstrap topology in TryCreateTmp.
        SdfGlyphAtlasAdapter MakeAdapterWithPartialTmp(uint[] tmpCodepoints) {
            asset = BuildTestAsset(tmpCodepoints);
            var source = new TmpFontAssetSource(asset);
            // FontEngine fallback path: SdfFontMetrics + SdfTextRunBaker over
            // the StubBackend so missing-from-TMP runs still produce real
            // quads in headless tests.
            var backend = new StubBackend();
            var loader = new FontLoader(new StubLoader(), backend);
            var sdf = new SdfFontMetrics(loader, backend);
            var baker = new SdfTextRunBaker(sdf);
            var adapter = new SdfGlyphAtlasAdapter(baker, sdf, null) {
                TmpSource = source,
                TmpFace = source.Face,
                TmpAtlas = new GlyphAtlas { TextureOverride = source.Atlas }
            };
            return adapter;
        }

        // The headline regression: "AerithStormborn" (15 chars, no space — we
        // omit the space to keep the assertion deterministic; the StubBackend
        // returns a non-zero advance for every codepoint, so a space would
        // produce a 16th quad in the FontEngine fallback case and obscure the
        // TMP-vs-FontEngine accounting we care about). The original demo bug
        // observed "Aerith Stormborn" rendering as "Aeri" + "Stormborn" with
        // the second word overlapping the first at position ~4 because TMP
        // had only 4 of the codepoints and ShapeTmp didn't advance the cursor
        // for the other 11. The fix: full coverage -> TMP path; ANY miss ->
        // roll back and use FontEngine for the entire run.
        const string HeadlineText = "AerithStormborn";

        [Test]
        public void Full_TMP_coverage_emits_exactly_one_quad_per_char() {
            // TMP atlas covers every codepoint -> TMP path is authoritative.
            // Exactly N quads expected; no FontEngine fallback leakage.
            uint[] cps = ChardsetFor(HeadlineText);
            var adapter = MakeAdapterWithPartialTmp(cps);

            var quads = new List<SdfGlyphQuad>();
            var cmd = new DrawTextCommand(
                new PaintRect(0, 0, 400, 32),
                HeadlineText,
                new FontHandle("sans-serif", 16, 400, FontStyle.Normal),
                LinearColor.White,
                TextDecoration.None);

            Assert.That(adapter.TryShape(cmd, quads, out _), Is.True);
            Assert.That(quads.Count, Is.EqualTo(HeadlineText.Length),
                "Expected exactly N TMP quads for full-coverage run; got " + quads.Count +
                " — extra quads indicate FontEngine fallback ran on top of TMP.");
        }

        [Test]
        public void Partial_TMP_coverage_falls_through_to_FontEngine_without_double_emission() {
            // TMP atlas covers ONLY "Aeri" — 11 of 15 codepoints are missing.
            // The adapter must (a) NOT emit the 4 TMP quads it could produce
            // (rollback), (b) re-shape via FontEngine which emits all 15.
            // Exactly N quads — not 2N (double emit), not N+4 (leaked TMP),
            // not N-11 (TMP-only with overlap).
            uint[] partial = ChardsetFor("Aeri");
            var adapter = MakeAdapterWithPartialTmp(partial);

            var quads = new List<SdfGlyphQuad>();
            var cmd = new DrawTextCommand(
                new PaintRect(0, 0, 400, 32),
                HeadlineText,
                new FontHandle("sans-serif", 16, 400, FontStyle.Normal),
                LinearColor.White,
                TextDecoration.None);

            Assert.That(adapter.TryShape(cmd, quads, out _), Is.True);
            Assert.That(quads.Count, Is.EqualTo(HeadlineText.Length),
                "Expected exactly N FontEngine quads after rollback; got " + quads.Count);

            // Strictly-increasing X is the visual proof that no two glyphs
            // landed at the same cursor position (which is what produced the
            // "Aeri" + "Stormborn" overlap in the demo).
            double prevX = double.NegativeInfinity;
            for (int i = 0; i < quads.Count; i++) {
                Assert.That(quads[i].Bounds.X, Is.GreaterThan(prevX),
                    "Quad " + i + " regressed in X — cursor failed to advance.");
                prevX = quads[i].Bounds.X;
            }
        }

        // TMP-LATIN-1 fixed: accented Latin missing from the primary face is
        // a hard TMP miss (ResolveTmpChainIndex returns -1 before the chain
        // walk — the old guard only skipped assets NAMED "*Emoji*", which the
        // runtime Segoe UI Symbol fallback slipped past), so the run reshapes
        // through FontEngine — never a borrowed glyph from a symbol/emoji
        // fallback atlas. Ignore lifted 2026-06-06.
        [Test]
        public void Latin1_missing_from_primary_uses_FontEngine_instead_of_TMP_fallback_chain() {
            const string text = "nurfpådinturf";
            asset = BuildTestAsset(ChardsetFor("nurfpdinturf"));
            fallbackAsset = BuildTestAsset(ChardsetFor("å"));
            var source = new TmpFontAssetSource(asset);
            var fallbackSource = new TmpFontAssetSource(fallbackAsset);
            var backend = new StubBackend();
            var loader = new FontLoader(new StubLoader(), backend);
            var sdf = new SdfFontMetrics(loader, backend);
            var baker = new SdfTextRunBaker(sdf);
            var adapter = new SdfGlyphAtlasAdapter(baker, sdf, null) {
                TmpSource = source,
                TmpFace = source.Face,
                TmpAtlas = new GlyphAtlas { TextureOverride = source.Atlas },
                TmpChain = new List<TmpFontAssetSource> { source, fallbackSource }
            };

            var quads = new List<SdfGlyphQuad>();
            var cmd = new DrawTextCommand(
                new PaintRect(0, 0, 400, 32),
                text,
                new FontHandle("leaderboard", 16, 400, FontStyle.Normal),
                LinearColor.White,
                TextDecoration.None);

            Assert.That(adapter.TryShape(cmd, quads, out _), Is.True);
            Assert.That(quads.Count, Is.EqualTo(text.Length),
                "A missing Latin-1 glyph in a static TMP atlas should fall through to a real font shaper.");
            Assert.That(quads.Exists(q => q.AtlasId != 0), Is.False,
                "Latin-1 letters must not route through emoji/symbol TMP fallback atlases.");

            double prevX = double.NegativeInfinity;
            for (int i = 0; i < quads.Count; i++) {
                Assert.That(quads[i].Bounds.X, Is.GreaterThan(prevX),
                    "Partial TMP output must still advance over the missing glyph.");
                prevX = quads[i].Bounds.X;
            }
        }

        // === Helpers ===

        static uint[] ChardsetFor(string s) {
            var set = new HashSet<uint>();
            for (int i = 0; i < s.Length; i++) {
                char c = s[i];
                if (c == ' ') continue;
                set.Add(c);
            }
            var arr = new uint[set.Count];
            int j = 0;
            foreach (var cp in set) arr[j++] = cp;
            return arr;
        }

        // Builds a minimal TMP_FontAsset whose character/glyph tables cover
        // exactly the requested codepoints. Each glyph gets a unique
        // glyphIndex and a placeholder rect — width/height are non-zero so
        // ShapeTmp emits a quad.
        static TMP_FontAsset BuildTestAsset(uint[] codepoints) {
            var a = ScriptableObject.CreateInstance<TMP_FontAsset>();
            var faceInfo = default(TmpFaceInfo);
            SetStructField(ref faceInfo, "m_FamilyName", "TmpFallthrough");
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
            SetField(a, "m_AtlasPadding", 4);
            var tex = new Texture2D(256, 256, TextureFormat.R8, false, true);
            SetField(a, "m_AtlasTexture", tex);
            SetField(a, "m_AtlasTextures", new[] { tex });
            SetField(a, "m_AtlasTextureIndex", 0);

            var glyphTable = new List<TmpGlyph>();
            var charTable = new List<TMP_Character>();
            var glyphLookup = new Dictionary<uint, TmpGlyph>();
            var charLookup = new Dictionary<uint, TMP_Character>();

            uint nextIndex = 10;
            for (int i = 0; i < codepoints.Length; i++) {
                uint cp = codepoints[i];
                uint gi = nextIndex++;
                var g = new TmpGlyph(gi,
                    new TmpGlyphMetrics(20, 30, 1, 25, 22),
                    new TmpGlyphRect(16 + (int)i * 24, 16, 20, 30),
                    1f, 0);
                glyphTable.Add(g);
                var ch = new TMP_Character(cp, g);
                charTable.Add(ch);
                glyphLookup[gi] = g;
                charLookup[cp] = ch;
            }
            SetField(a, "m_GlyphTable", glyphTable);
            SetField(a, "m_CharacterTable", charTable);
            SetField(a, "m_GlyphLookupDictionary", glyphLookup);
            SetField(a, "m_CharacterLookupDictionary", charLookup);
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
