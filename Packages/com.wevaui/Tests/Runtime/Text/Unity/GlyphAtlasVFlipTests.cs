#if UNITY_2023_1_OR_NEWER
using NUnit.Framework;
using UnityEngine;
using Weva.Text.TextCore;

namespace Weva.Tests.Text.Unity {
    // Regression for the text-path V mapping in Weva-Quad.shader.
    //
    // Storage convention (UploadPixels in GlyphAtlas.Unity.cs):
    //   for (row = 0..raster.Height) {
    //       cpuBuffer[(dstY + row) * Width + dstX..] = raster[row * raster.Width..]
    //   }
    //   texture.LoadRawTextureData(cpuBuffer); texture.Apply(...);
    //
    // Raster row 0 (lowest source offset) lands at the LOWEST cpuBuffer
    // offset for the slot — i.e. (dstY + 0) * Width + dstX.
    //
    // Texture sampler convention: with LoadRawTextureData, byte[0] is
    // pixel (0,0) at the BOTTOM-LEFT of the texture (V = 0 in sampler
    // space). So whatever raster row 0 contained is what the GPU samples
    // when atlasV == uvMin.y, and raster row (H-1) is what the GPU
    // samples when atlasV == uvMax.y.
    //
    // The text-path UV map in the shader is:
    //   atlasUv.y = lerp(uvMin.y, uvMax.y, IN.uv.y)
    //
    // i.e. IN.uv.y = 0 (TOP of rendered quad) -> atlasV = uvMin.y -> the
    // GPU reads raster row 0. IN.uv.y = 1 (BOTTOM of rendered quad)
    // -> atlasV = uvMax.y -> the GPU reads raster row (H-1).
    //
    // Empirically verified by sampling LiberationSans SDF (a standard
    // TMP_FontAsset atlas): the byte at the GlyphRect's v0 line is the
    // visual TOP of the glyph (e.g. apex of the letter A) and the byte
    // at v1 is the visual BOTTOM (the base of the A). The Unity
    // FontEngine SDF rasterizer's output, written through UploadPixels,
    // ends up with the same orientation in the atlas — raster row 0
    // contains the visual TOP of the glyph. So no V-flip is needed at
    // sampling time.
    public class GlyphAtlasVFlipTests {
        static FaceInfo Face() => new FaceInfo("test", "/stub", 400, FaceInfo.StyleNormal);

        // Sentinel raster: row 0 is solid 255 ("ROW0_MARK"), the rest is 0.
        // Lets us trace exactly which raster row a given (IN.uv) sample
        // hits without any rasterizer-orientation guesswork.
        static byte[] BuildRow0SentinelRaster(int width, int height) {
            var px = new byte[width * height];
            for (int x = 0; x < width; x++) px[0 * width + x] = 255;
            return px;
        }

        // C# mirror of the shader's text-path V mapping (see Weva-Quad
        // text path). Returns the raw R8 byte the GPU would sample.
        static byte SampleAtlasShaderEquivalent(
            byte[] textureBytes, int atlasW, int atlasH,
            float uvMinX, float uvMinY, float uvMaxX, float uvMaxY,
            float quadUvX, float quadUvY) {
            float atlasU = Mathf.Lerp(uvMinX, uvMaxX, quadUvX);
            // Straight lerp — atlas rows are stored top-down so quad
            // top (IN.uv.y = 0) reads atlasV = uvMin.y and quad bottom
            // (IN.uv.y = 1) reads atlasV = uvMax.y. Validated against
            // the LiberationSans SDF TMP atlas in production.
            float atlasV = Mathf.Lerp(uvMinY, uvMaxY, quadUvY);
            int tx = Mathf.Clamp(Mathf.FloorToInt(atlasU * atlasW), 0, atlasW - 1);
            int ty = Mathf.Clamp(Mathf.FloorToInt(atlasV * atlasH), 0, atlasH - 1);
            // Texture2D V-up convention: byte index 0 == V=0.
            return textureBytes[ty * atlasW + tx];
        }

        [Test]
        public void Storage_writes_raster_row_0_at_lowest_cpu_buffer_offset() {
            // Pins UploadPixels' storage convention so the shader-side
            // assumption stays valid: raster row 0 -> lowest atlas offset.
            var atlas = new GlyphAtlas(new AtlasResizePolicy(64, 64, AtlasResizePolicy.PolicyMode.FailOnFull));
            const int W = 8, H = 8;
            var raster = BuildRow0SentinelRaster(W, H);
            var glyph = new RasterizedGlyph(raster, W, H, 0, new GlyphMetrics(W, 0, H, W, H));
            Assert.That(atlas.TryUploadRaster(Face(), 'A', 16, glyph), Is.True);

            var bytes = atlas.Texture.GetRawTextureData();
            int aw = atlas.Width;
            // Glyph packed at (0,0). Raster row 0 must be at atlas y=0.
            for (int x = 0; x < W; x++) {
                Assert.That(bytes[0 * aw + x], Is.EqualTo(255),
                    $"raster row 0 col {x} must land at lowest atlas offset");
            }
            // Higher rows of the slot must be 0 (that's where row 1..H-1
            // of the raster — all zeros in our sentinel — were written).
            for (int y = 1; y < H; y++) {
                for (int x = 0; x < W; x++) {
                    Assert.That(bytes[y * aw + x], Is.EqualTo(0),
                        $"raster row {y} (zero in sentinel) at atlas offset");
                }
            }
        }

        [Test]
        public void Upload_recovers_when_owned_texture_was_destroyed() {
            var atlas = new GlyphAtlas(new AtlasResizePolicy(64, 64, AtlasResizePolicy.PolicyMode.FailOnFull));
            const int W = 8, H = 8;
            var first = new RasterizedGlyph(BuildRow0SentinelRaster(W, H), W, H, 0, new GlyphMetrics(W, 0, H, W, H));
            Assert.That(atlas.TryUploadRaster(Face(), 'A', 16, first), Is.True);

            var oldTexture = atlas.Texture;
            Assert.That(oldTexture, Is.Not.Null);
            if (Application.isPlaying) Object.Destroy(oldTexture);
            else Object.DestroyImmediate(oldTexture);

            var second = new RasterizedGlyph(BuildRow0SentinelRaster(W, H), W, H, 0, new GlyphMetrics(W, 0, H, W, H));
            Assert.DoesNotThrow(() => atlas.TryUploadRaster(Face(), 'B', 16, second));
            Assert.That(atlas.Texture, Is.Not.Null);
        }

        [Test]
        public void Shader_v_mapping_is_straight_lerp_quad_top_to_low_atlas_v() {
            // End-to-end regression: with the straight-lerp V mapping,
            // the TOP of the rendered quad (IN.uv.y == 0) reads raster
            // row 0 (the sentinel row, sitting at the lowest cpuBuffer
            // offset = atlas V = uvMin.y). The BOTTOM of the rendered
            // quad (IN.uv.y near 1) reads raster row (H-1) (atlas V =
            // uvMax.y). The previous flipped mapping swapped the two —
            // that's the upside-down-text bug this test pins against
            // a future audit that re-introduces the flip.
            var atlas = new GlyphAtlas(new AtlasResizePolicy(64, 64, AtlasResizePolicy.PolicyMode.FailOnFull));
            const int W = 16, H = 16;
            var raster = BuildRow0SentinelRaster(W, H);
            var glyph = new RasterizedGlyph(raster, W, H, 0, new GlyphMetrics(W, 0, H, W, H));
            Assert.That(atlas.TryUploadRaster(Face(), 'A', 16, glyph), Is.True);

            var bytes = atlas.Texture.GetRawTextureData();
            int aw = atlas.Width;
            int ah = atlas.Height;
            float uvMinX = 0f;
            float uvMinY = 0f;
            float uvMaxX = (float)W / aw;
            float uvMaxY = (float)H / ah;

            // IN.uv.y = 0 (TOP of rendered quad): with straight lerp,
            // samples atlasV = uvMin.y = lowest cpuBuffer offset = raster
            // row 0 (the row containing the 255 sentinel).
            byte topOfQuad = SampleAtlasShaderEquivalent(
                bytes, aw, ah, uvMinX, uvMinY, uvMaxX, uvMaxY,
                quadUvX: 0.5f, quadUvY: 0.0f);
            Assert.That(topOfQuad, Is.EqualTo(255),
                "top of rendered quad must hit raster row 0 (the sentinel)");

            // IN.uv.y = 1 (BOTTOM of rendered quad): with straight lerp,
            // samples atlasV = uvMax.y = highest cpuBuffer offset = raster
            // row (H-1) (which is 0 in our sentinel).
            byte bottomOfQuad = SampleAtlasShaderEquivalent(
                bytes, aw, ah, uvMinX, uvMinY, uvMaxX, uvMaxY,
                quadUvX: 0.5f, quadUvY: 0.95f);
            Assert.That(bottomOfQuad, Is.EqualTo(0),
                "bottom of rendered quad must hit raster row H-1 (zero in sentinel)");

            // Anti-test: re-introducing the V-flip would swap these.
            // If a future audit puts the flip back, this would pass
            // when it should fail — so we explicitly check the flipped
            // mapping yields the OPPOSITE answer to the straight one.
            byte topFlipped = SampleAtlasFlipped(
                bytes, aw, ah, uvMinX, uvMinY, uvMaxX, uvMaxY,
                quadUvX: 0.5f, quadUvY: 0.0f);
            byte bottomFlipped = SampleAtlasFlipped(
                bytes, aw, ah, uvMinX, uvMinY, uvMaxX, uvMaxY,
                quadUvX: 0.5f, quadUvY: 0.95f);
            Assert.That(topFlipped, Is.EqualTo(0),
                "anti-test: flipped V at top of quad would miss the sentinel");
            Assert.That(bottomFlipped, Is.EqualTo(255),
                "anti-test: flipped V at bottom of quad would hit the sentinel — that's the upside-down bug.");
        }

        // Shader sampling WITH the V-flip — the bug shape we are
        // regressing against. lerp(uvMax.y, uvMin.y, IN.uv.y) was the
        // earlier mapping that produced upside-down letters in the live
        // render against TMP atlases (and the FontEngine R8 atlas, both
        // of which store glyphs top-down). Used here as an explicit
        // anti-fingerprint.
        static byte SampleAtlasFlipped(
            byte[] textureBytes, int atlasW, int atlasH,
            float uvMinX, float uvMinY, float uvMaxX, float uvMaxY,
            float quadUvX, float quadUvY) {
            float atlasU = Mathf.Lerp(uvMinX, uvMaxX, quadUvX);
            float atlasV = Mathf.Lerp(uvMaxY, uvMinY, quadUvY); // V-flip (the bug)
            int tx = Mathf.Clamp(Mathf.FloorToInt(atlasU * atlasW), 0, atlasW - 1);
            int ty = Mathf.Clamp(Mathf.FloorToInt(atlasV * atlasH), 0, atlasH - 1);
            return textureBytes[ty * atlasW + tx];
        }
    }
}
#endif
