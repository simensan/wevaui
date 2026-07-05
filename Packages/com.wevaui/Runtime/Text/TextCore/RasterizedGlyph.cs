using System;

namespace Weva.Text.TextCore {
    // RasterizedGlyph is the SDF bitmap returned by ITextCoreBackend.RasterizeGlyph.
    // Pixels are R8 (single byte per sample). The texel value is an SDF distance
    // sample, scaled so 0 = far outside, 255 = far inside, 127 ≈ glyph edge. The
    // exact range is defined by TextCoreShaderContract.SdfRange.
    public readonly struct RasterizedGlyph {
        public readonly byte[] Pixels;
        public readonly int Width;
        public readonly int Height;
        public readonly int Padding;
        public readonly GlyphMetrics Metrics;

        public RasterizedGlyph(byte[] pixels, int width, int height, int padding, GlyphMetrics metrics) {
            Pixels = pixels;
            Width = width;
            Height = height;
            Padding = padding;
            Metrics = metrics;
        }

        public bool IsEmpty => Pixels == null || Pixels.Length == 0 || Width == 0 || Height == 0;
    }
}
