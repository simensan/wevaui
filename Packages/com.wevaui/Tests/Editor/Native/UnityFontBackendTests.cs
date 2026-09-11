// The Unity font adapter for the shared core, checked against FontEngine
// itself the way the Godot adapter is checked against TextServer: every
// number the adapter hands the core must be one the engine reports, and the
// shaping protocol (sizing call, byte clusters, per-code-point fallback,
// positioned form) must match what the core expects. The last test proves
// the core actually lays out with these numbers.
using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;
using Weva.Native;

namespace Weva.Tests.EditorTests.Native
{
    public class UnityFontBackendTests
    {
        private const string FontDir = "Packages/com.wevaui/Runtime/Resources/Fonts/";
        private UnityFontBackend _backend;
        private Font _regular, _bold, _italic, _symbols;
        private ulong _face, _boldFace, _italicFace, _symbolsFace, _bytesFace;

        [OneTimeSetUp]
        public void LoadFonts()
        {
            _regular = Resources.Load<Font>("Fonts/Weva-Default");
            _bold = Resources.Load<Font>("Fonts/Weva-Default-Bold");
            _italic = Resources.Load<Font>("Fonts/Weva-Default-Italic");
            _symbols = Resources.Load<Font>("Fonts/NotoSansSymbols2-Regular");
            Assert.That(_regular, Is.Not.Null, "the package's default font loads from Resources");
            Assert.That(_bold, Is.Not.Null);
            Assert.That(_italic, Is.Not.Null);
            Assert.That(_symbols, Is.Not.Null);

            _backend = new UnityFontBackend();
            _face = _backend.Adopt(_regular);
            _boldFace = _backend.Adopt(_bold);
            _italicFace = _backend.Adopt(_italic);
            _symbolsFace = _backend.Adopt(_symbols);
            _backend.SetFallbacks(_face, _symbolsFace);
            _backend.SetRealVariant(_face, 700, false, _boldFace);
            _backend.SetRealVariant(_face, 400, true, _italicFace);
            _bytesFace = _backend.Adopt(File.ReadAllBytes(FontDir + "Weva-Default.ttf"), 0, "Weva-Default.ttf");
        }

        [OneTimeTearDown]
        public void Cleanup()
        {
            _backend?.Dispose();
        }

        // FontEngine's own answer, taken fresh so the adapter cannot have set it
        // up. FontEngine is one state machine for the whole process, so after
        // this the adapter must forget what it had loaded, exactly as
        // NativeDocument does before every update.
        private FaceInfo EngineDesignInfo(Font font)
        {
            FontEngine.InitializeFontEngine();
            Assert.That(FontEngine.LoadFontFace(font), Is.EqualTo(FontEngineError.Success));
            _backend.Invalidate();
            return FontEngine.GetFaceInfo();
        }

        [Test]
        public void FaceMetrics_ScaleTheEngineDesignUnits()
        {
            FaceInfo info = EngineDesignInfo(_regular);
            Assert.That(info.pointSize, Is.GreaterThan(0), "design units are reported before a size is set");
            double scale = 16.0 / info.pointSize;
            Assert.That(_backend.TryFaceMetrics(_face, 16, out double ascent, out double descent, out double lineGap));
            Assert.That(ascent, Is.EqualTo(info.ascentLine * scale).Within(1e-9));
            Assert.That(descent, Is.EqualTo(-info.descentLine * scale).Within(1e-9));
            Assert.That(lineGap, Is.EqualTo((info.lineHeight - (info.ascentLine - info.descentLine)) * scale).Within(1e-9));
            Assert.That(ascent, Is.GreaterThan(10));
            Assert.That(descent, Is.GreaterThan(0));
        }

        [Test]
        public void BytesFace_MatchesTheAssetFace()
        {
            Assert.That(_backend.TryFaceMetrics(_face, 20, out double a1, out double d1, out double g1));
            Assert.That(_backend.TryFaceMetrics(_bytesFace, 20, out double a2, out double d2, out double g2));
            Assert.That(a2, Is.EqualTo(a1).Within(1e-9));
            Assert.That(d2, Is.EqualTo(d1).Within(1e-9));
            Assert.That(g2, Is.EqualTo(g1).Within(1e-9));
            Assert.That(UnityFontBackend.IndexOf(_backend.GlyphFor(_bytesFace, 'A')), Is.EqualTo(UnityFontBackend.IndexOf(_backend.GlyphFor(_face, 'A'))));
        }

        [Test]
        public void GlyphIndex_IsTheEngineIndex()
        {
            EngineDesignInfo(_regular);
            foreach (uint cp in new uint[] { 'A', 'g', '€', ' ' })
            {
                Assert.That(FontEngine.TryGetGlyphIndex(cp, out uint expected) && expected != 0, "engine has U+" + cp.ToString("X4"));
                uint id = _backend.GlyphFor(_face, cp);
                Assert.That(UnityFontBackend.SlotOf(id), Is.EqualTo(0), "primary face serves U+" + cp.ToString("X4"));
                Assert.That(UnityFontBackend.IndexOf(id), Is.EqualTo(expected));
            }
        }

        [Test]
        public void GlyphIndex_FallsBackPerCodePoint()
        {
            const uint star = 0x2601;   // CLOUD, in Noto Sans Symbols 2 and not in the UI face (Inter)
            EngineDesignInfo(_regular);
            FontEngine.TryGetGlyphIndex(star, out uint inPrimary);
            Assume.That(inPrimary, Is.EqualTo(0), "fixture: the primary lacks U+2601");
            EngineDesignInfo(_symbols);
            Assert.That(FontEngine.TryGetGlyphIndex(star, out uint inSymbols) && inSymbols != 0, "fixture: the symbol face has U+2601");

            uint id = _backend.GlyphFor(_face, star);
            Assert.That(UnityFontBackend.SlotOf(id), Is.EqualTo(1), "the fallback slot answers");
            Assert.That(UnityFontBackend.IndexOf(id), Is.EqualTo(inSymbols));
            Assert.That(_backend.GlyphFor(_symbolsFace, star), Is.EqualTo(inSymbols), "asked directly, the symbol face is slot 0");
            Assert.That(_backend.GlyphFor(_face, 0x1F984), Is.EqualTo(0), "a code point in no face has no glyph");
        }

        [Test]
        public void GlyphMetrics_AreTheEngineUnhintedMetrics()
        {
            EngineDesignInfo(_regular);
            Assert.That(FontEngine.SetFaceSize(24), Is.EqualTo(FontEngineError.Success));
            Assert.That(FontEngine.TryGetGlyphIndex('g', out uint index));
            Assert.That(FontEngine.TryGetGlyphWithIndexValue(index, GlyphLoadFlags.LOAD_NO_HINTING | GlyphLoadFlags.LOAD_NO_BITMAP, out Glyph g));

            uint id = _backend.GlyphFor(_face, 'g');
            Assert.That(_backend.TryGlyphMetrics(_face, id, 24, out double advance, out double bx, out double by, out int w, out int h));
            Assert.That(advance, Is.EqualTo(g.metrics.horizontalAdvance).Within(1e-6));
            Assert.That(bx, Is.EqualTo(g.metrics.horizontalBearingX).Within(1e-6));
            Assert.That(by, Is.EqualTo(g.metrics.horizontalBearingY).Within(1e-6));
            Assert.That(w, Is.EqualTo((int)Math.Ceiling(g.metrics.width)));
            Assert.That(h, Is.EqualTo((int)Math.Ceiling(g.metrics.height)));
            Assert.That(by, Is.LessThan(h), "a descender's bitmap reaches below the baseline");
        }

        [Test]
        public void GlyphMetrics_RoundThePixelSizeLikeTheGodotAdapter()
        {
            uint id = _backend.GlyphFor(_face, 'M');
            Assert.That(_backend.TryGlyphMetrics(_face, id, 24, out double at24, out _, out _, out _, out _));
            Assert.That(_backend.TryGlyphMetrics(_face, id, 24.4, out double at24_4, out _, out _, out _, out _));
            Assert.That(_backend.TryGlyphMetrics(_face, id, 24.6, out double at24_6, out _, out _, out _, out _));
            Assert.That(_backend.TryGlyphMetrics(_face, id, 25, out double at25, out _, out _, out _, out _));
            Assert.That(at24_4, Is.EqualTo(at24));
            Assert.That(at24_6, Is.EqualTo(at25));
            Assert.That(at25, Is.GreaterThan(at24));
        }

        [Test]
        public void Rasterize_ProducesCoverageForAnInkedGlyphOnly()
        {
            Assume.That(UnityFontBackend.RasterizerAvailable, "FontEngine.TryAddGlyphToTexture is bound");
            uint a = _backend.GlyphFor(_face, 'A');
            Assert.That(_backend.TryRasterize(_face, a, 32, out byte[] coverage, out int w, out int h));
            Assert.That(w, Is.GreaterThan(10));
            Assert.That(h, Is.GreaterThan(15));
            int inked = 0, max = 0;
            foreach (byte b in coverage)
            {
                if (b > 0) inked++;
                if (b > max) max = b;
            }
            Assert.That(inked, Is.GreaterThan(w * h / 10), "the bitmap carries the glyph's ink");
            Assert.That(max, Is.GreaterThanOrEqualTo(250), "full coverage inside the strokes");

            Assert.That(_backend.TryGlyphMetrics(_face, a, 32, out _, out _, out _, out int mw, out int mh));
            Assert.That(mw, Is.EqualTo(w), "metrics describe the rasterized bitmap");
            Assert.That(mh, Is.EqualTo(h));

            uint space = _backend.GlyphFor(_face, ' ');
            Assert.That(_backend.TryRasterize(_face, space, 32, out _, out _, out _), Is.False, "a space has no bitmap");
            Assert.That(_backend.TryRasterize(_face, 0, 32, out _, out _, out _), Is.False, "glyph 0 has no bitmap");
        }

        [Test]
        public void Rasterize_RowsRunTopDown()
        {
            Assume.That(UnityFontBackend.RasterizerAvailable);
            // 'L' is all foot at the bottom: the last row carries far more ink than the first.
            uint l = _backend.GlyphFor(_face, 'L');
            Assert.That(_backend.TryRasterize(_face, l, 40, out byte[] coverage, out int w, out int h));
            int top = 0, bottom = 0;
            for (int x = 0; x < w; x++)
            {
                top += coverage[x];
                bottom += coverage[(h - 1) * w + x];
            }
            Assert.That(bottom, Is.GreaterThan(top * 2), "row 0 is the top of the glyph");
        }

        [Test]
        public void Rasterize_FallbackGlyphComesFromItsOwnFace()
        {
            Assume.That(UnityFontBackend.RasterizerAvailable);
            uint star = _backend.GlyphFor(_face, 0x2601);
            Assume.That(UnityFontBackend.SlotOf(star), Is.EqualTo(1));
            Assert.That(_backend.TryRasterize(_face, star, 32, out byte[] coverage, out int w, out int h));
            Assert.That(w * h, Is.GreaterThan(100));
            int inked = 0;
            foreach (byte b in coverage) if (b > 0) inked++;
            Assert.That(inked, Is.GreaterThan(w * h / 5), "a cloud is mostly ink");
        }

        [Test]
        public void Shape_ClustersAreUtf8ByteOffsets_AndFallbackIsPerCodePoint()
        {
            // a (1 byte) é (2) € (3) ☁ (3, fallback face) b (1)
            List<UnityFontBackend.Shaped> shaped = _backend.ShapeText(_face, "aé€☁b", 16, out int total);
            Assert.That(total, Is.EqualTo(5));
            Assert.That(shaped.Count, Is.EqualTo(5));
            Assert.That(shaped[0].Cluster, Is.EqualTo(0));
            Assert.That(shaped[1].Cluster, Is.EqualTo(1));
            Assert.That(shaped[2].Cluster, Is.EqualTo(3));
            Assert.That(shaped[3].Cluster, Is.EqualTo(6));
            Assert.That(shaped[4].Cluster, Is.EqualTo(9));
            for (int i = 0; i < 5; i++)
            {
                Assert.That(shaped[i].Glyph, Is.Not.EqualTo(0), "glyph " + i);
                Assert.That(shaped[i].Advance, Is.GreaterThan(0), "advance " + i);
            }
            Assert.That(UnityFontBackend.SlotOf(shaped[3].Glyph), Is.EqualTo(1), "the cloud came from the fallback face");
            Assert.That(UnityFontBackend.SlotOf(shaped[4].Glyph), Is.EqualTo(0), "and shaping returned to the primary");
            // Each advance is that glyph's own metric (no pair adjustment across faces).
            Assert.That(_backend.TryGlyphMetrics(_face, shaped[3].Glyph, 16, out double cloudAdvance, out _, out _, out _, out _));
            Assert.That(shaped[3].Advance, Is.EqualTo(cloudAdvance));
        }

        [Test]
        public void Shape_SizingProtocol_WritesAtMostCapacityAndReturnsTheCount()
        {
            List<UnityFontBackend.Shaped> none = _backend.ShapeText(_face, "Hello", 16, out int total, capacity: 0);
            Assert.That(total, Is.EqualTo(5));
            Assert.That(none.Count, Is.EqualTo(0));
            List<UnityFontBackend.Shaped> two = _backend.ShapeText(_face, "Hello", 16, out total, capacity: 2);
            Assert.That(total, Is.EqualTo(5));
            Assert.That(two.Count, Is.EqualTo(2));
            List<UnityFontBackend.Shaped> all = _backend.ShapeText(_face, "Hello", 16, out total);
            Assert.That(two[0].Glyph, Is.EqualTo(all[0].Glyph));
            Assert.That(two[1].Advance, Is.EqualTo(all[1].Advance));
            Assert.That(_backend.ShapeText(_face, "", 16, out total).Count, Is.EqualTo(0));
            Assert.That(total, Is.EqualTo(0));
        }

        [Test]
        public void Shape_ControlCharactersTakeNoSpace()
        {
            List<UnityFontBackend.Shaped> shaped = _backend.ShapeText(_face, "a\nb\tc", 16, out int total);
            Assert.That(total, Is.EqualTo(5));
            Assert.That(shaped[1].Glyph, Is.EqualTo(0));
            Assert.That(shaped[1].Advance, Is.EqualTo(0));
            Assert.That(shaped[3].Glyph, Is.EqualTo(0));
            Assert.That(shaped[3].Advance, Is.EqualTo(0));
            Assert.That(shaped[2].Cluster, Is.EqualTo(2));
            Assert.That(shaped[4].Cluster, Is.EqualTo(4));
        }

        [Test]
        public void Shape_AppliesTheEnginePairAdjustment()
        {
            // Inter kerns "L-" (a negative pair adjustment). Read the engine's own
            // records for the pair at 32px through the single-list query (the
            // adapter uses the two-list one); the shaped advance of L must carry
            // exactly that, and half of it at 16px.
            Assume.That(UnityFontBackend.KerningAvailable, "FontEngine.GetPairAdjustmentRecords is reachable");
            FaceInfo design = EngineDesignInfo(_regular);
            double unitsPerEm = design.pointSize;
            Assert.That(FontEngine.SetFaceSize(32), Is.EqualTo(FontEngineError.Success));
            Assert.That(FontEngine.TryGetGlyphIndex('L', out uint l));
            Assert.That(FontEngine.TryGetGlyphIndex('-', out uint dash));
            var method = typeof(FontEngine).GetMethod("GetPairAdjustmentRecords",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                null, new[] { typeof(List<uint>) }, null);
            Assume.That(method, Is.Not.Null, "FontEngine.GetPairAdjustmentRecords(List<uint>) is reachable");
            var records = (GlyphPairAdjustmentRecord[])method.Invoke(null, new object[] { new List<uint> { l, dash } });
            _backend.Invalidate();
            // Records are design units, one per subtable covering the pair;
            // OpenType applies the first match. Inter lists L- twice (a pair
            // subtable at -104 units, then a class subtable at -174).
            double kern = 0;
            int matches = 0;
            foreach (GlyphPairAdjustmentRecord r in records ?? new GlyphPairAdjustmentRecord[0])
            {
                if (r.firstAdjustmentRecord.glyphIndex != l || r.secondAdjustmentRecord.glyphIndex != dash) continue;
                double units = r.firstAdjustmentRecord.glyphValueRecord.xAdvance + r.secondAdjustmentRecord.glyphValueRecord.xAdvance;
                TestContext.WriteLine("L- record " + matches + ": " + units + " units");
                if (matches == 0) kern = units * 32 / unitsPerEm;
                matches++;
            }
            TestContext.WriteLine("L- pair adjustment at 32px: " + kern + " px from " + matches + " record(s)");
            Assert.That(matches, Is.GreaterThan(0), "fixture: the face kerns L-");
            Assert.That(kern, Is.LessThan(0).And.GreaterThan(-4), "a kern is a fraction of the size, in pixels");

            List<UnityFontBackend.Shaped> pair = _backend.ShapeText(_face, "L-", 32, out _);
            List<UnityFontBackend.Shaped> alone = _backend.ShapeText(_face, "L", 32, out _);
            Assert.That(pair[0].Advance, Is.EqualTo(alone[0].Advance + kern).Within(1e-6));
            Assert.That(pair[1].Advance, Is.EqualTo(_backend.ShapeText(_face, "-", 32, out _)[0].Advance).Within(1e-6));
            List<UnityFontBackend.Shaped> small = _backend.ShapeText(_face, "L-", 16, out _);
            List<UnityFontBackend.Shaped> smallAlone = _backend.ShapeText(_face, "L", 16, out _);
            Assert.That(small[0].Advance - smallAlone[0].Advance, Is.EqualTo(kern / 2).Within(0.05));
            // A pair the face does not kern is left alone, across faces too.
            List<UnityFontBackend.Shaped> mixed = _backend.ShapeText(_face, "L\u2601", 32, out _);
            Assert.That(mixed[0].Advance, Is.EqualTo(alone[0].Advance).Within(1e-6));
        }

        [Test]
        public void PositionedShaper_AgreesWithTheLegacyCallback()
        {
            List<UnityFontBackend.Shaped> legacy = _backend.ShapeText(_face, "Weva ☁ é", 18, out int total);
            List<weva_shaped_glyph> positioned = _backend.ShapePositionedText(_face, "Weva ☁ é", 18, out int positionedTotal);
            Assert.That(positionedTotal, Is.EqualTo(total));
            Assert.That(positioned.Count, Is.EqualTo(legacy.Count));
            for (int i = 0; i < legacy.Count; i++)
            {
                Assert.That(positioned[i].glyph, Is.EqualTo(legacy[i].Glyph), "glyph " + i);
                Assert.That(positioned[i].cluster, Is.EqualTo(legacy[i].Cluster), "cluster " + i);
                Assert.That(positioned[i].x_advance, Is.EqualTo(legacy[i].Advance), "advance " + i);
                Assert.That(positioned[i].y_advance, Is.EqualTo(0));
                Assert.That(positioned[i].x_offset, Is.EqualTo(0));
                Assert.That(positioned[i].y_offset, Is.EqualTo(0));
            }
        }

        [Test]
        public void Shape_LongRunOfFallbackGlyphs()
        {
            string text = new string('☁', 300);   // 300 code points, 3 bytes each, all from the fallback face
            List<UnityFontBackend.Shaped> shaped = _backend.ShapeText(_face, text, 14, out int total);
            Assert.That(total, Is.EqualTo(300));
            for (int i = 0; i < 300; i++)
            {
                Assert.That(shaped[i].Cluster, Is.EqualTo((uint)(i * 3)));
                Assert.That(UnityFontBackend.SlotOf(shaped[i].Glyph), Is.EqualTo(1));
                Assert.That(shaped[i].Advance, Is.EqualTo(shaped[0].Advance));
            }
        }

        [Test]
        public void Variant_AnswersWithRealFacesOrTheRegularOne()
        {
            Assert.That(_backend.VariantOf(_face, 700, false), Is.EqualTo(_boldFace));
            Assert.That(_backend.VariantOf(_face, 800, false), Is.EqualTo(_boldFace), "black falls to the bold face");
            Assert.That(_backend.VariantOf(_face, 400, true), Is.EqualTo(_italicFace));
            Assert.That(_backend.VariantOf(_face, 700, true), Is.EqualTo(_italicFace), "bold italic keeps the italic axis when no such face exists");
            Assert.That(_backend.VariantOf(_face, 400, false), Is.EqualTo(_face));
            Assert.That(_backend.VariantOf(_symbolsFace, 700, true), Is.EqualTo(_symbolsFace), "a face with no variants serves every weight");

            // The bold face measures wider than the regular one.
            double regular = _backend.ShapeText(_face, "Bold", 24, out _)[0].Advance;
            double bold = _backend.ShapeText(_boldFace, "Bold", 24, out _)[0].Advance;
            Assert.That(bold, Is.GreaterThan(regular));
        }

        [Test]
        public void Invalidate_ReloadsTheFaceOnTheNextCall()
        {
            _backend.GlyphFor(_face, 'x');
            int loads = _backend.FaceLoads;
            _backend.Invalidate();
            Assert.That(_backend.TryGlyphMetrics(_face, _backend.GlyphFor(_face, 'y'), 16, out _, out _, out _, out _, out _));
            Assert.That(_backend.FaceLoads, Is.GreaterThan(loads), "after another FontEngine user, the face is loaded again");
        }

        [Test]
        public void Document_LaysOutWithTheAdapter()
        {
            const string html = "<body><span id=t>Hello Weva</span></body>";
            double stubWidth;
            using (var stub = new NativeDocument(800, 600))
            {
                stub.LoadHtml(html);
                stub.SetCss("body{margin:0}#t{font-size:20px}");
                stub.Update(0);
                Assert.That(stub.TryGetBounds(stub.Query("#t"), out NativeBounds b));
                stubWidth = b.Width;
            }
            using (var doc = new NativeDocument(800, 600))
            {
                _backend.Install(doc, _face);
                doc.LoadHtml(html);
                doc.SetCss("body{margin:0}#t{font-size:20px}");
                doc.Update(0);
                Assert.That(doc.TryGetBounds(doc.Query("#t"), out NativeBounds bounds));
                double expected = 0;
                foreach (UnityFontBackend.Shaped g in _backend.ShapeText(_face, "Hello Weva", 20, out _)) expected += g.Advance;
                Assert.That(bounds.Width, Is.EqualTo(expected).Within(0.51), "the span is as wide as the adapter's advances");
                Assert.That(bounds.Width, Is.Not.EqualTo(stubWidth).Within(0.01), "and not the stub face's width");
                Assert.That(_backend.TryFaceMetrics(_face, 20, out double ascent, out double descent, out _));
                Assert.That(bounds.Height, Is.GreaterThanOrEqualTo(Math.Floor(ascent + descent) - 1), "the line is at least the face's content height");

                // A registered family switches the face: bold is wider.
                doc.RegisterFontFamily("Heavy", _boldFace);
                doc.SetCss("body{margin:0}#t{font-size:20px;font-family:Heavy}");
                doc.Update(0);
                Assert.That(doc.TryGetBounds(doc.Query("#t"), out NativeBounds heavy));
                Assert.That(heavy.Width, Is.GreaterThan(bounds.Width));

                // Draws carry textured triangles once glyphs are rasterized.
                Assume.That(UnityFontBackend.RasterizerAvailable);
                bool textured = false;
                ReadOnlySpan<weva_draw> draws = doc.Draws();
                for (int i = 0; i < draws.Length; i++) if (draws[i].texture_id != 0 && draws[i].vertex_count > 0) textured = true;
                Assert.That(textured, "text draws reference the glyph atlas");
            }
        }
    }
}
