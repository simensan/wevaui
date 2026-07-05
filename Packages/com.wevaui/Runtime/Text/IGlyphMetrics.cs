namespace Weva.Text {
    // IGlyphMetrics is an OPTIONAL extension to Weva.Layout.Text.IFontMetrics.
    // The base IFontMetrics interface only knows how to measure a string; for the
    // glyph atlas + shaper we also need:
    //   - per-codepoint advance (TryGetAdvance) to avoid allocating substrings,
    //   - per-codepoint UV rect (TryGetGlyphRect) so the URP backend can build
    //     text quads without round-tripping through the atlas.
    //
    // Implementations of IFontMetrics that also implement this interface light
    // up the fast paths in TextShaper and the URP backend. MonoFontMetrics does
    // not implement it; TextCoreFontMetrics does.
    public interface IGlyphMetrics {
        bool TryGetAdvance(uint codepoint, double fontSize, out double advancePx);
        bool TryGetGlyphRect(uint codepoint, double fontSize, out Weva.Text.TextCore.GlyphRect rect);
    }
}
