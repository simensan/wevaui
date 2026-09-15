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
            TestContext.WriteLine("FontEngine design units: pointSize=" + info.pointSize + " ascentLine=" + info.ascentLine + " descentLine=" + info.descentLine + " lineHeight=" + info.lineHeight + " capLine=" + info.capLine + " meanLine=" + info.meanLine + " baseline=" + info.baseline + " family=" + info.familyName);
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

        // The core snaps each glyph quad to a whole pixel and spans exactly
        // the bitmap's texels, so the bearings it places the quad with must
        // name the bitmap's edge. FreeType rasterises an unhinted outline
        // into a bitmap whose left column is floor(bearingX) and whose top
        // row is ceil(bearingY); the outline's own fractional extents, which
        // is what FontEngine keeps in the packed glyph's metrics, rounded the
        // other way for about half of the glyphs and put them one pixel up or
        // down inside a word (the stock dashboard's 11px labels).
        [Test]
        public void Rasterize_BearingsNameTheBitmapEdge()
        {
            Assume.That(UnityFontBackend.RasterizerAvailable);
            foreach (int size in new[] { 11, 12, 13, 14, 26 })
            foreach (char c in "NASDQEquityMetaPlform")
            {
                uint id = _backend.GlyphFor(_face, c);
                Assert.That(_backend.TryRasterize(_face, id, size, out _, out int w, out int h), $"'{c}' at {size}px rasterizes");
                Assert.That(_backend.TryGlyphMetrics(_face, id, size, out _, out double bx, out double by, out int mw, out int mh));
                Debug.Log($"glyph '{c}' {size}px: bx={bx:0.###} by={by:0.###} w={w} h={h}");
                Assert.That(bx, Is.EqualTo(Math.Floor(bx)), $"'{c}' at {size}px: bearing x is the bitmap's left column");
                Assert.That(by, Is.EqualTo(Math.Ceiling(by)), $"'{c}' at {size}px: bearing y is the bitmap's top row");
                Assert.That(mw, Is.EqualTo(w), "metrics describe the rasterized bitmap");
                Assert.That(mh, Is.EqualTo(h));
            }
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

        // A symbol none of the bundled faces carries reaches the platform's
        // symbol font, as a browser falls back to a system font: the HUD
        // sample's ⚔ and ☥ drew as boxes before this. The font is the OS's,
        // so a machine without it goes inconclusive rather than red.
        // The chain a WevaDocument builds: the UI face, the bundled symbol
        // face, then the platform's fonts. Inconclusive where none is installed.
        private ulong FaceWithSystemFallbacks()
        {
            Assume.That(UnityFontBackend.SystemFallbackFonts.Length, Is.GreaterThan(0), "a platform with named fallback fonts");
            ulong face = _backend.Adopt(_regular);
            var chain = new List<ulong> { _symbolsFace };
            foreach (string name in UnityFontBackend.SystemFallbackFonts)
            {
                ulong installed = _backend.AdoptInstalled(name);
                if (installed != 0) chain.Add(installed);
            }
            Assume.That(chain.Count, Is.GreaterThan(1), "the platform's fonts are installed");
            _backend.SetFallbacks(face, chain.ToArray());
            return face;
        }

        [Test]
        public void SystemFonts_ServeWhatTheBundledFacesLack()
        {
            const uint swords = 0x2694;   // CROSSED SWORDS: not in Inter, not in Noto Sans Symbols 2
            Assert.That(_backend.GlyphFor(_face, swords), Is.EqualTo(0), "fixture: the bundled faces lack U+2694");
            ulong face = FaceWithSystemFallbacks();
            uint id = _backend.GlyphFor(face, swords);
            Assert.That(UnityFontBackend.IndexOf(id), Is.Not.EqualTo(0), "a system face has U+2694");
            Assert.That(UnityFontBackend.SlotOf(id), Is.GreaterThanOrEqualTo(2), "served after the bundled fallback");
            Assert.That(UnityFontBackend.SlotOf(_backend.GlyphFor(face, 0x2601)), Is.EqualTo(1), "the bundled fallback still answers first");
            Assert.That(UnityFontBackend.IndexOf(_backend.GlyphFor(face, 0x05D0)), Is.Not.EqualTo(0), "and a script none of the bundled faces have (Hebrew)");
            Assert.That(UnityFontBackend.IndexOf(_backend.GlyphFor(face, 0x0628)), Is.Not.EqualTo(0), "(Arabic)");

            Assume.That(UnityFontBackend.RasterizerAvailable);
            Assert.That(_backend.TryRasterize(face, id, 24, out byte[] coverage, out int w, out int h));
            int inked = 0;
            foreach (byte b in coverage) if (b > 0) inked++;
            Assert.That(inked, Is.GreaterThan(w * h / 10), "the system face's bitmap carries ink");
        }

        // ---- shaping: the core expects a run's glyphs back in VISUAL order,
        // with brackets mirrored and Arabic joined, as TextServer returns them
        // on the Godot host. FontEngine has none of that; the backend does it
        // over the font's layout tables, asking the core about Unicode.

        [Test]
        public void Shape_HebrewRunComesBackInVisualOrder()
        {
            ulong face = FaceWithSystemFallbacks();
            List<weva_shaped_glyph> shaped = _backend.ShapePositionedText(face, "אבג", 16, out int total);
            Assert.That(total, Is.EqualTo(3));
            Assert.That(shaped[0].cluster, Is.EqualTo(4), "the logically last letter is drawn first (leftmost)");
            Assert.That(shaped[1].cluster, Is.EqualTo(2));
            Assert.That(shaped[2].cluster, Is.EqualTo(0), "the logically first letter is drawn last (rightmost)");
            Assert.That(shaped[2].glyph, Is.EqualTo(_backend.GlyphFor(face, 0x05D0)));
            Assert.That(shaped[0].x_advance, Is.GreaterThan(0));

            List<weva_shaped_glyph> latin = _backend.ShapePositionedText(face, "abc", 16, out _);
            Assert.That(latin[0].cluster, Is.EqualTo(0), "a left-to-right run keeps logical order");
            Assert.That(latin[2].cluster, Is.EqualTo(2));
        }

        [Test]
        public void Shape_RightToLeftRunMirrorsBrackets()
        {
            ulong face = FaceWithSystemFallbacks();
            uint open = _backend.GlyphFor(face, '('), close = _backend.GlyphFor(face, ')');
            Assert.That(open, Is.Not.EqualTo(close));
            List<weva_shaped_glyph> shaped = _backend.ShapePositionedText(face, "(אב)", 16, out int total);
            Assert.That(total, Is.EqualTo(4));
            Assert.That(shaped[3].cluster, Is.EqualTo(0), "the logical '(' is visually last");
            Assert.That(shaped[3].glyph, Is.EqualTo(close), "and draws as ')'");
            Assert.That(shaped[0].cluster, Is.EqualTo(5), "the logical ')' is visually first");
            Assert.That(shaped[0].glyph, Is.EqualTo(open), "and draws as '('");

            List<weva_shaped_glyph> ltr = _backend.ShapePositionedText(face, "(ab)", 16, out _);
            Assert.That(ltr[0].glyph, Is.EqualTo(open), "left-to-right brackets are themselves");
            Assert.That(ltr[3].glyph, Is.EqualTo(close));
        }

        [Test]
        public void Shape_ArabicLettersTakeTheirJoiningForms()
        {
            ulong face = FaceWithSystemFallbacks();
            uint isolated = _backend.GlyphFor(face, 0x0628);   // BEH, the isolated glyph the cmap gives
            Assume.That(UnityFontBackend.IndexOf(isolated), Is.Not.EqualTo(0), "a face has Arabic");

            List<weva_shaped_glyph> one = _backend.ShapePositionedText(face, "ب", 16, out int total);
            Assert.That(total, Is.EqualTo(1));
            Assert.That(one[0].glyph, Is.EqualTo(isolated), "alone, BEH is its isolated form");

            // بب: the first joins forward (init), the second joins back (fina).
            List<weva_shaped_glyph> two = _backend.ShapePositionedText(face, "بب", 16, out total);
            Assert.That(total, Is.EqualTo(2));
            Assert.That(two[0].cluster, Is.EqualTo(2), "visual order: the final form is drawn first (leftmost)");
            Assert.That(two[1].cluster, Is.EqualTo(0));
            Assert.That(two[0].glyph, Is.Not.EqualTo(isolated), "the final form is a different glyph");
            Assert.That(two[1].glyph, Is.Not.EqualTo(isolated), "so is the initial form");
            Assert.That(two[0].glyph, Is.Not.EqualTo(two[1].glyph), "and they differ from each other");

            // ببب: the middle one is medial, a fourth glyph.
            List<weva_shaped_glyph> three = _backend.ShapePositionedText(face, "ببب", 16, out total);
            Assert.That(total, Is.EqualTo(3));
            Assert.That(three[1].glyph, Is.Not.EqualTo(three[0].glyph));
            Assert.That(three[1].glyph, Is.Not.EqualTo(three[2].glyph));
            Assert.That(three[1].glyph, Is.Not.EqualTo(isolated));

            // ب ب: a space breaks the join; both are isolated again.
            List<weva_shaped_glyph> apart = _backend.ShapePositionedText(face, "ب ب", 16, out total);
            Assert.That(total, Is.EqualTo(3));
            Assert.That(apart[0].glyph, Is.EqualTo(isolated));
            Assert.That(apart[2].glyph, Is.EqualTo(isolated));

            // Hebrew has no joining: the same letter twice is the same glyph twice.
            List<weva_shaped_glyph> hebrew = _backend.ShapePositionedText(face, "בב", 16, out _);
            Assert.That(hebrew[0].glyph, Is.EqualTo(hebrew[1].glyph));
        }

        // What FontEngine reports of the system face's GSUB: logged, so a
        // shaping regression on another Unity version is diagnosable from
        // the run's log rather than a debugger.
        [Test]
        public void Shape_LayoutTablesAreReadable()
        {
            ulong face = FaceWithSystemFallbacks();
            Assume.That(UnityFontBackend.IndexOf(_backend.GlyphFor(face, 0x0628)), Is.Not.EqualTo(0), "a face has Arabic");
            string beh = _backend.DescribeLayout(face, 0x0628, "fina");
            string lam = _backend.DescribeLayout(face, 0x0644, "rlig");
            Debug.Log(beh);
            Debug.Log(lam);
            Assert.That(beh, Does.Contain("present=True"), "the face's GSUB was read");
            Assert.That(beh, Does.Contain("script 'arab'"), "with an Arabic script");
        }

        // Lam + alef is the required ligature every Arabic font carries, in
        // one of two shapes: one ligature glyph (Arial: a `rlig` ligature
        // lookup), or two halves substituted by chaining contextual rules
        // (Segoe UI: lam.init becomes a lam-alef head when an alef.fina
        // follows, and the alef its tail). Either way the lam that precedes
        // an alef is not the lam that precedes a beh.
        [Test]
        public void Shape_LamAlefIsTheRequiredLigature()
        {
            ulong face = FaceWithSystemFallbacks();
            Assume.That(UnityFontBackend.IndexOf(_backend.GlyphFor(face, 0x0644)), Is.Not.EqualTo(0), "a face has Arabic");
            List<weva_shaped_glyph> lamAlef = _backend.ShapePositionedText(face, "لا", 16, out int total);   // لا
            Assert.That(total, Is.EqualTo(1).Or.EqualTo(2), "one ligature glyph, or a head and a tail");
            List<weva_shaped_glyph> lamBeh = _backend.ShapePositionedText(face, "لب", 16, out _);   // لب: lam.init, beh.fina
            // Visual order: the lam is the LAST glyph of each run (rightmost).
            uint lamBeforeAlef = lamAlef[lamAlef.Count - 1].glyph, lamBeforeBeh = lamBeh[lamBeh.Count - 1].glyph;
            Assert.That(lamAlef[lamAlef.Count - 1].cluster, Is.EqualTo(0), "the lam keeps its cluster");
            Assert.That(lamBeforeAlef, Is.Not.EqualTo(lamBeforeBeh), "the lam before an alef is the ligature's, not the plain initial lam");
            Assert.That(lamBeforeAlef, Is.Not.EqualTo(_backend.GlyphFor(face, 0x0644)), "nor the isolated lam");
            if (total == 2)
            {
                List<weva_shaped_glyph> behAlef = _backend.ShapePositionedText(face, "با", 16, out _);   // با: beh.init, alef.fina
                Assert.That(lamAlef[0].glyph, Is.Not.EqualTo(behAlef[0].glyph), "the alef after a lam is the ligature's tail, not the plain final alef");
            }
            double width = 0;
            foreach (weva_shaped_glyph g in lamAlef) width += g.x_advance;
            Assert.That(width, Is.GreaterThan(0));
        }

        // An installed font as the face itself (the UI face carries combining
        // marks and would answer for them first), or inconclusive where the
        // machine lacks it.
        private ulong FaceWithInstalled(string name)
        {
            ulong installed = _backend.AdoptInstalled(name);
            Assume.That(installed, Is.Not.EqualTo(0), name + " is installed");
            return installed;
        }

        // A class-based chaining contextual rule (GSUB type 6, format 2):
        // Bahnschrift's `ccmp` swaps a combining mark for a stacking form
        // when another mark follows it. The mark alone keeps its glyph.
        [Test]
        public void Shape_ClassBasedContextualRuleApplies()
        {
            ulong face = FaceWithInstalled("Bahnschrift");
            const string oneMark = "ä", twoMarks = "ä́";   // a + diaeresis (+ acute)
            Assume.That(UnityFontBackend.IndexOf(_backend.GlyphFor(face, 0x0308)), Is.Not.EqualTo(0), "Bahnschrift has the combining diaeresis");
            List<weva_shaped_glyph> one = _backend.ShapePositionedText(face, oneMark, 16, out int total1);
            List<weva_shaped_glyph> two = _backend.ShapePositionedText(face, twoMarks, 16, out int total2);
            Assert.That(total1, Is.EqualTo(2));
            Assert.That(total2, Is.EqualTo(3));
            Assert.That(one[0].glyph, Is.EqualTo(two[0].glyph), "the base is the same");
            Assert.That(two[1].glyph, Is.Not.EqualTo(one[1].glyph), "the diaeresis before another mark is the rule's stacking form");
            Assert.That(two[1].cluster, Is.EqualTo(1));
            Assert.That(two[2].cluster, Is.EqualTo(3));
        }

        // Cursive attachment (GPOS type 3, `curs`): Dubai joins a letter to a
        // following final yeh through swash forms whose exit and entry anchors
        // meet (exit (0, 23) on the beh, entry (483, 134) on the yeh, in a
        // 1000-unit em). The lookup is flagged right-to-left, so the beh is
        // the child: it rises by 134 - 23 units, and the yeh's advance becomes
        // its entry x. The plain "بب" pair has no anchors and stays put.
        [Test]
        public void Shape_CursiveAttachmentLiftsTheAttachedGlyph()
        {
            ulong face = FaceWithInstalled("Dubai");
            Assume.That(UnityFontBackend.IndexOf(_backend.GlyphFor(face, 0x0628)), Is.Not.EqualTo(0), "Dubai has Arabic");
            List<weva_shaped_glyph> behYeh = _backend.ShapePositionedText(face, "بي", 16, out int total);
            Assert.That(total, Is.EqualTo(2));
            weva_shaped_glyph yeh = behYeh[0], beh = behYeh[1];   // visual order: the yeh (logically last) first
            Assert.That(beh.cluster, Is.EqualTo(0));
            List<weva_shaped_glyph> behBeh = _backend.ShapePositionedText(face, "بب", 16, out _);
            Assert.That(beh.glyph, Is.Not.EqualTo(behBeh[1].glyph), "before a yeh the beh takes its swash form");
            Debug.Log($"Dubai cursive: beh glyph={UnityFontBackend.IndexOf(beh.glyph)} y_offset={beh.y_offset:0.##} x_offset={beh.x_offset:0.##} advance={beh.x_advance:0.##}; yeh glyph={UnityFontBackend.IndexOf(yeh.glyph)} y_offset={yeh.y_offset:0.##} advance={yeh.x_advance:0.##}\n" + _backend.DescribeLayout(face, 0x0628, "calt") + "\n" + _backend.DescribeContextual(face, 0x0628, "calt", 160, 161, 324, 325));
            Assert.That(beh.y_offset, Is.EqualTo((134 - 23) * 16.0 / 1000).Within(0.05), "the beh is lifted to the yeh's entry anchor");
            Assert.That(yeh.y_offset, Is.EqualTo(0).Within(0.001), "the yeh, the parent, stays on the baseline");
            Assert.That(yeh.x_advance, Is.EqualTo(483 * 16.0 / 1000).Within(0.05), "the yeh's advance ends at its entry anchor");
            foreach (weva_shaped_glyph g in behBeh) Assert.That(g.y_offset, Is.EqualTo(0).Within(0.001), "no anchors, no lift");
        }

        // Devanagari through Nirmala UI's dev2 tables (Windows' Indic UI
        // font): the i-matra is written before its consonant, a
        // syllable-initial ra + virama becomes the reph over the syllable's
        // end, ka + virama + ssa is one akhand glyph, and a ra after a virama
        // takes its below-base form (a contextual rule in this font, not a
        // ligature).
        [Test]
        public void Shape_DevanagariReordersAndForms()
        {
            ulong face = FaceWithInstalled("Nirmala UI");
            Assume.That(UnityFontBackend.IndexOf(_backend.GlyphFor(face, 0x0915)), Is.Not.EqualTo(0), "Nirmala UI has Devanagari");
            Debug.Log(_backend.DescribeLayout(face, 0x0915, "rphf"));

            // हि: ha (bytes 0-2) + i-matra (bytes 3-5) -> the matra is drawn first.
            List<weva_shaped_glyph> hi = _backend.ShapePositionedText(face, "हि", 16, out int total);
            Assert.That(total, Is.EqualTo(2));
            Assert.That(hi[0].cluster, Is.EqualTo(3), "the left matra comes first");
            Assert.That(hi[1].cluster, Is.EqualTo(0), "then the consonant");
            Assert.That(hi[0].glyph, Is.EqualTo(_backend.GlyphFor(face, 0x093F)));

            // कर्म: ka, ra, virama, ma -> ka, ma, reph (the reph keeps ra's cluster).
            List<weva_shaped_glyph> karma = _backend.ShapePositionedText(face, "कर्म", 16, out total);
            // कर्म, with a comma: the syllable ends before the comma, so the
            // reph stays on the ma (it once slipped after the comma, because
            // the syllable's end was read off the run after rphf had
            // shortened it). HarfBuzz: ka, ma, reph at -0.445, comma.
            List<weva_shaped_glyph> karmaComma = _backend.ShapePositionedText(face, "कर्म,", 16, out int totalWithComma);
            Assert.That(totalWithComma, Is.EqualTo(4));
            Assert.That(karmaComma[2].cluster, Is.EqualTo(3), "the reph comes before the comma");
            Assert.That(karmaComma[2].x_offset, Is.EqualTo(-0.445).Within(0.01), "attached to the ma at its abvm anchor (HarfBuzz's number)");
            Assert.That(karmaComma[3].cluster, Is.EqualTo(12), "the comma is last");
            Assert.That(total, Is.EqualTo(3), "ra + virama became the reph");
            Assert.That(karma[0].cluster, Is.EqualTo(0), "ka first");
            Assert.That(karma[1].cluster, Is.EqualTo(9), "ma next");
            Assert.That(karma[2].cluster, Is.EqualTo(3), "the reph last, over the ma");
            Assert.That(karma[2].x_advance, Is.EqualTo(0).Within(0.01), "the reph is a mark on the ma");
            Assert.That(karma[2].glyph, Is.Not.EqualTo(_backend.GlyphFor(face, 0x0930)), "and not the plain ra");

            // क्ष: ka + virama + ssa -> one akhand glyph.
            List<weva_shaped_glyph> ksha = _backend.ShapePositionedText(face, "क्ष", 16, out total);
            Assert.That(total, Is.EqualTo(1), "akhn: one glyph");
            Assert.That(ksha[0].cluster, Is.EqualTo(0));

            // प्र: pa + virama + ra -> pa with a below-base ra: two glyphs
            // (pa, ra.blwf) or, as Nirmala UI has it, one presentation
            // ligature. Never the plain pa + virama + ra, nor a half pa.
            List<weva_shaped_glyph> pra = _backend.ShapePositionedText(face, "प्र", 16, out total);
            Assert.That(total, Is.EqualTo(1).Or.EqualTo(2), "blwf: the virama and the ra became a below-base form");
            if (total == 2)
            {
                Assert.That(pra[0].glyph, Is.EqualTo(_backend.GlyphFor(face, 0x092A)), "pa stays whole (the ra is below it, pa is the base)");
                Assert.That(pra[1].glyph, Is.Not.EqualTo(_backend.GlyphFor(face, 0x0930)), "the ra is its below-base form");
            }
            else
            {
                Assert.That(pra[0].glyph, Is.Not.EqualTo(_backend.GlyphFor(face, 0x092A)), "one glyph for the whole conjunct");
            }

            // A plain word keeps text order: नमस्ते -> na, ma, then sa + virama
            // + ta as a half form and a ta (5 glyphs) or as one conjunct
            // ligature (4, Nirmala UI), then the e-matra on top.
            List<weva_shaped_glyph> namaste = _backend.ShapePositionedText(face, "नमस्ते", 16, out total);
            Assert.That(namaste[0].cluster, Is.EqualTo(0));
            Assert.That(namaste[1].cluster, Is.EqualTo(3));
            Assert.That(total, Is.EqualTo(4).Or.EqualTo(5), "sa + virama joined the ta as a half form or a conjunct");
            Assert.That(namaste[total - 1].cluster, Is.EqualTo(15), "the e-matra stays last");
            Assert.That(namaste[total - 1].x_advance, Is.EqualTo(0).Within(0.01), "and is a mark on the ta");
        }

        [Test]
        public void Shape_CombiningMarkSitsOnItsBase()
        {
            Assume.That(UnityFontBackend.MarkPositioningAvailable, "FontEngine's mark attachment query is bound");
            ulong face = FaceWithSystemFallbacks();
            Assume.That(UnityFontBackend.IndexOf(_backend.GlyphFor(face, 0x064E)), Is.Not.EqualTo(0), "a face has the fatha");
            List<weva_shaped_glyph> shaped = _backend.ShapePositionedText(face, "بَ", 16, out int total);   // بَ
            Assert.That(total, Is.EqualTo(2));
            int mark = shaped[0].cluster == 2 ? 0 : 1, baseAt = 1 - mark;
            Debug.Log($"fatha on beh: mark x_offset={shaped[mark].x_offset:0.##} y_offset={shaped[mark].y_offset:0.##}, beh advance={shaped[baseAt].x_advance:0.##}");
            Assert.That(shaped[mark].x_advance, Is.EqualTo(0), "a mark takes no space");
            Assert.That(shaped[baseAt].x_advance, Is.GreaterThan(0));
            // The fatha's outline already sits high in its own glyph space;
            // the attachment moves it from there onto the beh's anchor, a
            // real shift of some pixels (Segoe UI at 16px: x 2.4, y -5.1),
            // never a whole em.
            Assert.That(Math.Abs(shaped[mark].y_offset), Is.GreaterThan(0.5).And.LessThan(16.0), "the mark is moved onto the base's anchor");
            // The mark and the base share a pen (the mark advances nothing),
            // so its x offset places it within the base's own advance.
            Assert.That(shaped[mark].x_offset, Is.GreaterThanOrEqualTo(-1.0));
            Assert.That(shaped[mark].x_offset, Is.LessThanOrEqualTo(shaped[baseAt].x_advance + 1.0));
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
            // adapter caches the complete table); the shaped advance of L must carry
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

        [TestCase("asset")]
        [TestCase("installed")]
        [TestCase("bytes")]
        [TestCase("shared-bytes")]
        public void RepeatedBackendLifetime_KeepsNativeMemoryBounded(string source)
        {
            if (source == "installed") Assume.That(UnityFontBackend.SystemFallbackFonts, Is.Not.Empty);
            byte[] sharedBytes = source == "shared-bytes" ? File.ReadAllBytes(FontDir + "Weva-Default.ttf") : null;
            long before = 0;
            // Warm native font/kerning caches once before measuring new backend
            // lifetimes. File and byte sources must retain native identity even
            // after the shaper has read their OpenType tables.
            for (int i = -1; i < 12; i++)
            {
                using (var backend = new UnityFontBackend())
                {
                    ulong face = source == "installed" ? backend.AdoptInstalled(UnityFontBackend.SystemFallbackFonts[0]) :
                        source == "bytes" ? backend.Adopt(File.ReadAllBytes(FontDir + "Weva-Default.ttf")) :
                        source == "shared-bytes" ? backend.Adopt(sharedBytes) : backend.Adopt(_regular);
                    Assume.That(face, Is.Not.Zero);
                    Assert.That(backend.TryFaceMetrics(face, 16, out _, out _, out _));
                    backend.ShapePositionedText(face, "Hello Weva", 16, out _);
                }
                System.GC.Collect();
                System.GC.WaitForPendingFinalizers();
                System.GC.Collect();
                if (i == -1) before = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
            }
            long retained = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() - before;
            TestContext.WriteLine("12 backend lifetimes retained " + retained + " bytes (source=" + source + ")");
            Assert.That(retained, Is.LessThan(16 * 1024 * 1024));
        }

        [Test]
        public void ByteFace_IsIndependentOfTheCallersBuffer()
        {
            byte[] bytes = File.ReadAllBytes(FontDir + "Weva-Default.ttf");
            using (var backend = new UnityFontBackend())
            {
                ulong face = backend.Adopt(bytes);
                Array.Clear(bytes, 0, bytes.Length);
                Assert.That(backend.TryFaceMetrics(face, 16, out double ascent, out _, out _), Is.True, backend.LastError);
                Assert.That(ascent, Is.GreaterThan(0));
                Assert.That(backend.GlyphFor(face, 'A'), Is.Not.Zero);
            }
        }

        [TestCase("asset")]
        [TestCase("bytes")]
        [TestCase("installed")]
        public void ChangingTextAcrossFaces_DoesNotRetainNewNativeTables(string source)
        {
            long before = 0;
            for (int i = -1; i < 4; ++i)
            {
                using (var backend = new UnityFontBackend())
                {
                    ulong regular = source == "installed" ? backend.AdoptInstalled(UnityFontBackend.SystemFallbackFonts[0]) :
                        source == "bytes" ? backend.Adopt(File.ReadAllBytes(FontDir + "Weva-Default.ttf")) : backend.Adopt(_regular);
                    ulong bold = source == "bytes" ? backend.Adopt(File.ReadAllBytes(FontDir + "Weva-Default-Bold.ttf")) : backend.Adopt(_bold);
                    Assume.That(regular, Is.Not.Zero);
                    string text = "L-" + (char)('A' + i);
                    Assert.That(backend.ShapeText(regular, text, 16, out _).Count, Is.EqualTo(3));
                    Assert.That(backend.ShapeText(bold, text, 32, out _).Count, Is.EqualTo(3));
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                if (i == -1) before = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
            }
            long retained = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() - before;
            TestContext.WriteLine("Changing text across " + source + " faces retained " + retained + " bytes");
            Assert.That(retained, Is.LessThan(16 * 1024 * 1024));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CachedPairTable_MatchesIndividualEngineQueries(bool bold)
        {
            Font font = bold ? _bold : _regular;
            ulong face = bold ? _boldFace : _face;
            double unitsPerEm = EngineDesignInfo(font).pointSize;
            var query = typeof(FontEngine).GetMethod("GetPairAdjustmentRecords",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                null, new[] { typeof(List<uint>) }, null);
            Assert.That(query, Is.Not.Null);
            var expected = new Dictionary<string, double>();
            var pairs = new List<string> { "L-", "AV", "VA", "To", "Ta", "Yo", "oo", "HH", "..", "a?", "1/", "aV", "{A", "@T", "W," };
            for (int i = 0; i < 94; ++i) pairs.Add(new string(new[] { (char)('!' + i), (char)('!' + i * 37 % 94) }));
            foreach (string pair in pairs)
            {
                Assert.That(FontEngine.TryGetGlyphIndex(pair[0], out uint left));
                Assert.That(FontEngine.TryGetGlyphIndex(pair[1], out uint right));
                var records = query.Invoke(null, new object[] { new List<uint> { left, right } }) as GlyphPairAdjustmentRecord[];
                double units = 0;
                foreach (var record in records ?? Array.Empty<GlyphPairAdjustmentRecord>())
                {
                    if (record.firstAdjustmentRecord.glyphIndex != left || record.secondAdjustmentRecord.glyphIndex != right) continue;
                    units = record.firstAdjustmentRecord.glyphValueRecord.xAdvance + record.secondAdjustmentRecord.glyphValueRecord.xAdvance;
                    break;
                }
                expected[pair] = units * 32 / unitsPerEm;
            }
            _backend.Invalidate();
            foreach (var pair in expected)
            {
                var shaped = _backend.ShapeText(face, pair.Key, 32, out _);
                var alone = _backend.ShapeText(face, pair.Key.Substring(0, 1), 32, out _);
                Assert.That(shaped[0].Advance - alone[0].Advance, Is.EqualTo(pair.Value).Within(1e-6), pair.Key);
            }
        }

        [Test]
        public void ReimportedFont_DoesNotReuseThePreviousPairTable()
        {
            string path = "Assets/WevaKerningReview-" + Guid.NewGuid().ToString("N") + ".ttf";
            double Adjustment(Font font)
            {
                using (var backend = new UnityFontBackend())
                {
                    ulong face = backend.Adopt(font);
                    return backend.ShapeText(face, "AV", 32, out _)[0].Advance - backend.ShapeText(face, "A", 32, out _)[0].Advance;
                }
            }
            try
            {
                double expected = Adjustment(_bold);
                File.Copy(FontDir + "Weva-Default.ttf", path);
                UnityEditor.AssetDatabase.ImportAsset(path, UnityEditor.ImportAssetOptions.ForceUpdate);
                Font font = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>(path);
                double initial = Adjustment(font);
                Assert.That(initial, Is.Not.EqualTo(expected).Within(1e-6), "fixture: regular and bold kern AV differently");
                File.Copy(FontDir + "Weva-Default-Bold.ttf", path, true);
                UnityEditor.AssetDatabase.ImportAsset(path, UnityEditor.ImportAssetOptions.ForceUpdate);
                font = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>(path);
                Assert.That(Adjustment(font), Is.EqualTo(expected).Within(1e-6));
            }
            finally
            {
                UnityEditor.AssetDatabase.DeleteAsset(path);
            }
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
