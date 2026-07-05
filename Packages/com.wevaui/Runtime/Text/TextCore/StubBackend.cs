using System.Collections.Generic;

namespace Weva.Text.TextCore {
    // StubBackend is a pure-C# fixed-metric backend that mirrors MonoFontMetrics
    // semantics (every glyph is 0.5em wide, line height 1.2em, ascent 0.8em,
    // descent 0.4em). It exists so headless NUnit tests can drive the full
    // TextCore code path without UnityEngine. It uses a simple deterministic
    // synthetic SDF when asked to rasterize glyphs (a centered filled square)
    // so atlas packing tests have realistic byte payloads.
    public sealed class StubBackend : ITextCoreBackend {
        public double CharWidthEm { get; }
        public double LineHeightEm { get; }
        public double AscentEm { get; }
        public double DescentEm { get; }
        public int RasterPaddingPx { get; }
        public double UnitsPerEm => 1024;

        readonly Dictionary<FaceInfo, FaceMetrics> faces = new();
        public int LoadFaceCallCount { get; private set; }
        public int RasterizeCallCount { get; private set; }

        public StubBackend() : this(0.5, 1.2, 0.8, 0.4, 2) { }

        public StubBackend(double charWidthEm, double lineHeightEm, double ascentEm, double descentEm, int rasterPaddingPx) {
            CharWidthEm = charWidthEm;
            LineHeightEm = lineHeightEm;
            AscentEm = ascentEm;
            DescentEm = descentEm;
            RasterPaddingPx = rasterPaddingPx;
        }

        public bool LoadFace(FaceInfo face, out FaceMetrics metrics) {
            LoadFaceCallCount++;
            if (!face.IsValid) { metrics = default; return false; }
            if (faces.TryGetValue(face, out metrics)) return true;
            double upem = UnitsPerEm;
            metrics = new FaceMetrics(
                unitsPerEm: upem,
                ascent: AscentEm * upem,
                descent: DescentEm * upem,
                lineGap: 0,
                lineHeight: LineHeightEm * upem
            );
            faces[face] = metrics;
            return true;
        }

        public bool TryGetGlyphAdvance(FaceInfo face, uint codepoint, double fontSize, out double advancePx) {
            if (!face.IsValid || codepoint == 0) { advancePx = 0; return false; }
            advancePx = CharWidthEm * fontSize;
            return true;
        }

        public bool RasterizeGlyph(FaceInfo face, uint codepoint, double fontSize, out RasterizedGlyph glyph) {
            RasterizeCallCount++;
            if (!face.IsValid || codepoint == 0) { glyph = default; return false; }
            int width = (int)System.Math.Ceiling(CharWidthEm * fontSize) + RasterPaddingPx * 2;
            int height = (int)System.Math.Ceiling((AscentEm + DescentEm) * fontSize) + RasterPaddingPx * 2;
            if (width <= 0) width = 1;
            if (height <= 0) height = 1;
            byte[] pixels = new byte[width * height];
            int innerLeft = RasterPaddingPx;
            int innerRight = width - RasterPaddingPx;
            int innerTop = RasterPaddingPx;
            int innerBottom = height - RasterPaddingPx;
            for (int y = innerTop; y < innerBottom; y++) {
                for (int x = innerLeft; x < innerRight; x++) {
                    pixels[y * width + x] = 255;
                }
            }
            var metrics = new GlyphMetrics(
                advanceX: CharWidthEm * fontSize,
                bearingX: 0,
                bearingY: AscentEm * fontSize,
                width: CharWidthEm * fontSize,
                height: (AscentEm + DescentEm) * fontSize
            );
            glyph = new RasterizedGlyph(pixels, width, height, RasterPaddingPx, metrics);
            return true;
        }
    }
}
