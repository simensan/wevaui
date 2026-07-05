#if UNITY_2023_1_OR_NEWER
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Weva.Paint;
using Weva.Rendering.URP;
using Weva.Text.Sdf;
using Weva.Text.TextCore;
using PaintRect = Weva.Paint.Rect;
// FontStyle is in both Weva.Paint and UnityEngine; alias to disambiguate.
using FontStyle = Weva.Paint.FontStyle;

namespace Weva.Tests.Text.Sdf {
    public class SdfTextRendererIntegrationTests {
        sealed class StubLoader : FontLoader.IFaceLoader {
            public bool TryLoad(string family, FontStyle style, int weight, out FaceInfo face) {
                int styleFlags = style == FontStyle.Italic ? FaceInfo.StyleItalic : FaceInfo.StyleNormal;
                face = new FaceInfo(family, family + "/" + weight, weight, styleFlags);
                return true;
            }
        }

        // A no-op rasterizer hook: signals "rasterized" without touching FontEngine.
        // The adapter calls TryEnsureRaster only when the GlyphAtlas reports a miss;
        // with the StubBackend, the atlas's RasterizeGlyph already populates a real
        // raster so the hook is rarely invoked. Keeping it null in tests is safe.
        static SdfGlyphAtlasAdapter MakeAdapter(out SdfFontMetrics metrics) {
            var backend = new StubBackend();
            var fl = new FontLoader(new StubLoader(), backend);
            metrics = new SdfFontMetrics(fl, backend);
            var baker = new SdfTextRunBaker(metrics);
            // Rasterizer can be null in headless tests — the StubBackend path
            // produces UVs without it.
            return new SdfGlyphAtlasAdapter(baker, metrics, null);
        }

        [SetUp]
        public void Reset() {
            FontResolver.ClearRegistered();
            FontResolver.SetSystemDefaults(new Dictionary<string, string> {
                ["sans-serif"] = "/test/sans.ttf",
                ["serif"] = "/test/serif.ttf"
            });
            AtlasRegistry.Clear();
        }

        [Test]
        public void TryShape_produces_quads_for_hello_text() {
            var adapter = MakeAdapter(out _);
            var quads = new List<SdfGlyphQuad>();
            var cmd = new DrawTextCommand(
                new PaintRect(0, 0, 200, 20), "Hello",
                new FontHandle("sans-serif", 16, 400, FontStyle.Normal),
                LinearColor.White, default);
            Assert.That(adapter.TryShape(cmd, quads), Is.True);
            Assert.That(quads.Count, Is.EqualTo(5));
        }

        [Test]
        public void TryShape_with_atlas_id_returns_nonzero_id() {
            var adapter = MakeAdapter(out var metrics);
            var quads = new List<SdfGlyphQuad>();
            // Force the metrics to register its atlas first by querying a glyph.
            metrics.MetricsFor("sans-serif", FontStyle.Normal, 400);
            var cmd = new DrawTextCommand(
                new PaintRect(0, 0, 200, 20), "AB",
                new FontHandle("sans-serif", 16, 400, FontStyle.Normal),
                LinearColor.White, default);
            Assert.That(adapter.TryShape(cmd, quads, out int atlasId), Is.True);
            Assert.That(atlasId, Is.GreaterThan(0));
        }

        [Test]
        public void Letter_spacing_on_command_offsets_glyph_origins() {
            // The DrawTextCommand carries a per-run LetterSpacingPx field that the
            // adapter must thread into the baker's request, so the rendered glyph
            // stream stays aligned with the line breaker's measured width.
            var adapter = MakeAdapter(out _);
            var quadsNoSpace = new List<SdfGlyphQuad>();
            var quadsSpaced = new List<SdfGlyphQuad>();
            var font = new FontHandle("sans-serif", 16, 400, FontStyle.Normal);
            var noSpace = new DrawTextCommand(
                new PaintRect(0, 0, 200, 20), "abc", font, LinearColor.White, default, 0);
            var spaced = new DrawTextCommand(
                new PaintRect(0, 0, 200, 20), "abc", font, LinearColor.White, default, 4);
            adapter.TryShape(noSpace, quadsNoSpace);
            adapter.TryShape(spaced, quadsSpaced);
            // Every glyph after the first picks up an extra 4px of advance per char.
            // (StubBackend: 0.5em advance => 8 px @ 16 fs.)
            // Glyph 1: 0 unchanged. Glyph 2: +4. Glyph 3: +8.
            double dxAt0 = quadsSpaced[0].Bounds.X - quadsNoSpace[0].Bounds.X;
            double dxAt1 = quadsSpaced[1].Bounds.X - quadsNoSpace[1].Bounds.X;
            double dxAt2 = quadsSpaced[2].Bounds.X - quadsNoSpace[2].Bounds.X;
            Assert.That(dxAt0, Is.EqualTo(0).Within(1e-9));
            Assert.That(dxAt1, Is.EqualTo(4).Within(1e-9));
            Assert.That(dxAt2, Is.EqualTo(8).Within(1e-9));
        }

        [Test]
        public void Same_text_produces_stable_quad_count_across_calls() {
            var adapter = MakeAdapter(out _);
            var quadsA = new List<SdfGlyphQuad>();
            var quadsB = new List<SdfGlyphQuad>();
            var cmd = new DrawTextCommand(
                new PaintRect(0, 0, 200, 20), "Hello",
                new FontHandle("sans-serif", 16, 400, FontStyle.Normal),
                LinearColor.White, default);
            adapter.TryShape(cmd, quadsA);
            adapter.TryShape(cmd, quadsB);
            Assert.That(quadsA.Count, Is.EqualTo(quadsB.Count));
            for (int i = 0; i < quadsA.Count; i++) {
                Assert.That(quadsA[i].Bounds.X, Is.EqualTo(quadsB[i].Bounds.X).Within(1e-9));
            }
        }

        [Test]
        public void Different_fonts_share_atlas_in_v1_single_atlas_design() {
            // v1: SdfFontMetrics owns a single GlyphAtlas; all faces register the
            // same atlas. The atlas id is therefore stable across font switches.
            var adapter = MakeAdapter(out var metrics);
            metrics.MetricsFor("sans-serif", FontStyle.Normal, 400);
            metrics.MetricsFor("serif", FontStyle.Normal, 400);
            var quadsSans = new List<SdfGlyphQuad>();
            var quadsSerif = new List<SdfGlyphQuad>();
            var cmdSans = new DrawTextCommand(new PaintRect(0, 0, 200, 20), "A",
                new FontHandle("sans-serif", 16, 400, FontStyle.Normal),
                LinearColor.White, default);
            var cmdSerif = new DrawTextCommand(new PaintRect(0, 0, 200, 20), "A",
                new FontHandle("serif", 16, 400, FontStyle.Normal),
                LinearColor.White, default);
            adapter.TryShape(cmdSans, quadsSans, out int idSans);
            adapter.TryShape(cmdSerif, quadsSerif, out int idSerif);
            Assert.That(idSans, Is.EqualTo(idSerif));
        }

        [Test]
        public void Color_propagates_to_each_glyph_quad() {
            var adapter = MakeAdapter(out _);
            var quads = new List<SdfGlyphQuad>();
            var red = new LinearColor(1f, 0f, 0f, 1f);
            var cmd = new DrawTextCommand(
                new PaintRect(0, 0, 200, 20), "abc",
                new FontHandle("sans-serif", 16, 400, FontStyle.Normal),
                red, default);
            adapter.TryShape(cmd, quads);
            foreach (var q in quads) {
                Assert.That(q.Color.R, Is.EqualTo(1f).Within(1e-6));
                Assert.That(q.Color.G, Is.EqualTo(0f).Within(1e-6));
            }
        }

        [Test]
        public void Empty_text_returns_false() {
            var adapter = MakeAdapter(out _);
            var quads = new List<SdfGlyphQuad>();
            var cmd = new DrawTextCommand(
                new PaintRect(0, 0, 200, 20), "",
                new FontHandle("sans-serif", 16, 400, FontStyle.Normal),
                LinearColor.White, default);
            Assert.That(adapter.TryShape(cmd, quads), Is.False);
            Assert.That(quads.Count, Is.EqualTo(0));
        }

        [Test]
        public void Quads_carry_uv_rect_from_atlas() {
            var adapter = MakeAdapter(out _);
            var quads = new List<SdfGlyphQuad>();
            var cmd = new DrawTextCommand(
                new PaintRect(0, 0, 200, 20), "A",
                new FontHandle("sans-serif", 16, 400, FontStyle.Normal),
                LinearColor.White, default);
            adapter.TryShape(cmd, quads);
            Assert.That(quads.Count, Is.EqualTo(1));
            // UV rect should be valid (non-degenerate).
            Assert.That(quads[0].UvMax.x, Is.GreaterThan(quads[0].UvMin.x));
            Assert.That(quads[0].UvMax.y, Is.GreaterThan(quads[0].UvMin.y));
        }

        [Test]
        public void Underline_decoration_emits_extra_quad() {
            var adapter = MakeAdapter(out _);
            var quads = new List<SdfGlyphQuad>();
            var cmd = new DrawTextCommand(
                new PaintRect(0, 0, 200, 20), "AB",
                new FontHandle("sans-serif", 16, 400, FontStyle.Normal),
                LinearColor.White, TextDecoration.Underline);
            adapter.TryShape(cmd, quads);
            // Two glyph quads + one underline rect.
            Assert.That(quads.Count, Is.EqualTo(3));
        }

        [Test]
        public void Decoration_quad_has_degenerate_uv_signaling_no_atlas_sample() {
            var adapter = MakeAdapter(out _);
            var quads = new List<SdfGlyphQuad>();
            var cmd = new DrawTextCommand(
                new PaintRect(0, 0, 200, 20), "A",
                new FontHandle("sans-serif", 16, 400, FontStyle.Normal),
                LinearColor.White, TextDecoration.Underline);
            adapter.TryShape(cmd, quads);
            // The last quad is the decoration; it must have a degenerate UV
            // (the shader treats UvMax <= UvMin as "flat color, no atlas sample").
            var lastQuad = quads[quads.Count - 1];
            Assert.That(lastQuad.UvMin.x, Is.EqualTo(lastQuad.UvMax.x));
            Assert.That(lastQuad.UvMin.y, Is.EqualTo(lastQuad.UvMax.y));
        }

        [Test]
        public void TryShape_preserves_origin_offsets_in_quad_bounds() {
            var adapter = MakeAdapter(out _);
            var quads = new List<SdfGlyphQuad>();
            var cmd = new DrawTextCommand(
                new PaintRect(50, 100, 200, 20), "A",
                new FontHandle("sans-serif", 16, 400, FontStyle.Normal),
                LinearColor.White, default);
            adapter.TryShape(cmd, quads);
            Assert.That(quads.Count, Is.EqualTo(1));
            Assert.That(quads[0].Bounds.X, Is.EqualTo(50).Within(1e-6));
            // Y is the baselineY - ascent; not exactly 100 but within the line box.
        }
    }
}
#endif
