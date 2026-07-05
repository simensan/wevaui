namespace Weva.Text.TextCore {
    // ITextCoreBackend is the seam between the headless-testable text layer and
    // the Unity-API-bound TextCore implementation. Implementations must be
    // single-threaded; the UI updates on the main thread per PLAN §9.
    //
    // LoadFace must be idempotent: calling it twice with the same FaceInfo should
    // be cheap. Implementations are expected to cache faces internally.
    //
    // RasterizeGlyph should produce an SDF bitmap; see RasterizedGlyph and
    // TextCoreShaderContract for the encoding.
    public interface ITextCoreBackend {
        bool LoadFace(FaceInfo face, out FaceMetrics metrics);
        bool TryGetGlyphAdvance(FaceInfo face, uint codepoint, double fontSize, out double advancePx);
        bool RasterizeGlyph(FaceInfo face, uint codepoint, double fontSize, out RasterizedGlyph glyph);
    }
}
