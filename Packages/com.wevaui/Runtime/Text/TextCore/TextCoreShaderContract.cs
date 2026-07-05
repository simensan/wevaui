namespace Weva.Text.TextCore {
    // TextCoreShaderContract is the documented seam between this package's
    // glyph atlas and the URP backend's Hidden/Weva/Text shader. The shader
    // lives in the URP backend agent's tree; both sides MUST agree on the
    // values defined here. Treat this file as the authoritative ABI.
    //
    // SDF encoding:
    //   - Atlas texture format: TextureFormat.R8 (single byte per texel,
    //     linear color space). The shader samples this as a float in [0, 1].
    //   - 0.0 = far outside the glyph, 1.0 = far inside, 0.5 = exactly on the
    //     glyph edge. The shader compares against 0.5 with screen-space
    //     derivatives for resolution-independent anti-aliased text.
    //   - Render padding (SdfPaddingPx) is the number of texels between the
    //     glyph metrics box and the texture bounds; this is the SDF "spread"
    //     and matches FontEngine.RenderGlyph's padding argument.
    //
    // Vertex layout (per-quad, 4 verts, 6 indices):
    //   - position (float2, screen-space px in CSS top-left coords)
    //   - uv (float2, atlas UV)
    //   - color (float4, linear premultiplied)
    //   - flags (float4 placeholder for italic skew, weight bias, decoration)
    //
    // Uniforms the shader expects:
    //   - _MainTex (the atlas Texture2D)
    //   - _Weva_TextSdfRange (float, see SdfRange below)
    //   - _Weva_AtlasSize (float2, current Width/Height of the atlas)
    public static class TextCoreShaderContract {
        public const string ShaderName = "Hidden/Weva/Text";
        public const string MainTexProperty = "_MainTex";
        public const string SdfRangeProperty = "_Weva_TextSdfRange";
        public const string AtlasSizeProperty = "_Weva_AtlasSize";

        // Number of texels of SDF spread on each side of the glyph. Must match
        // SdfGlyphRasterizer.PaddingPx (the canonical SDF padding source of
        // truth) and UnityFontEngineBackend.RenderPadding. The actual value
        // each glyph carries is also threaded via RasterizedGlyph.Padding so
        // callers that need precision read the per-glyph value; this constant
        // is documentation for the shader contract.
        public const int SdfPaddingPx = 8;

        // SDF range expressed in texels: how many texels does a unit-length
        // signed distance occupy. With FontEngine.SDFAA at 4x oversample this
        // is approximately equal to SdfPaddingPx.
        public const float SdfRange = 8.0f;

        // SDF channel layout: which channel of the R8 atlas carries the
        // distance sample. R8 only has one channel, but we keep this constant
        // so the shader can reference it symbolically (and so future RGBA
        // multi-channel SDF can be plumbed without breaking the contract).
        public const int SdfChannel = 0;

        // Threshold the shader compares against. 0.5 means "median of 0..1".
        // 127/255 ≈ 0.498; the shader rounds to 0.5 for clarity.
        public const float SdfEdgeThreshold = 0.5f;

        // When the atlas is non-power-of-two, the shader still sees Linear,
        // Clamp wrapping. UnityFontEngineBackend keeps the atlas pow2 by
        // default (512/1024/2048).
        public const bool RequiresPow2Atlas = true;
    }
}
