namespace Weva.Paint {
    // CSS Compositing 1 §6 / §10: the 17 spec blend modes. Ordinals are LOCKED
    // — the shader (Weva-Quad.shader) packs the value from the C# side into
    // a per-instance float and dispatches on the integer here. Reordering would
    // silently corrupt the GPU dispatch.
    //
    // All 17 modes now ship in the fragment shader: the 13 "separable" /
    // pure-RGB modes plus the four HSL-based modes (`hue`, `saturation`,
    // `color`, `luminosity`) via the SetLum/SetSat/ClipColor helper chain
    // described in CSS Compositing 1 §11.4..§11.8 (tracker B3c).
    public enum MixBlendMode : byte {
        Normal = 0,
        Multiply = 1,
        Screen = 2,
        Overlay = 3,
        Darken = 4,
        Lighten = 5,
        ColorDodge = 6,
        ColorBurn = 7,
        HardLight = 8,
        SoftLight = 9,
        Difference = 10,
        Exclusion = 11,
        PlusLighter = 12,
        // HSL-based modes — CSS Compositing 1 §11.5..§11.8.
        Hue = 13,
        Saturation = 14,
        Color = 15,
        Luminosity = 16,
        // INTERNAL — not a CSS <blend-mode> keyword; never parsed from author CSS.
        // Used by BoxToPaintConverter to request per-pixel sRGB source-over on
        // backdrop-filtered elements whose background-color is translucent. Fixes
        // the −17..−21 blue divergence vs Chrome on glass.html's search pill:
        // Chrome composites in sRGB space while the engine renders in linear space;
        // this mode performs the compositing in sRGB on the GPU using the per-pixel
        // backdrop read that the B24 backdrop machinery already provides.
        //
        // Shader dispatch: Weva_FinishFragment page-backdrop arm detects mode 17,
        // computes exact sRGB source-over, and BYPASSES Weva_PrepareCssPremulForTarget
        // (the lift is not applied — we already wrote the exact target value).
        //
        // Scope: ONLY backdrop-filtered elements' own background-color fill. Do NOT
        // use for general translucent fills (cost: a full-screen blit per batch).
        ExactSrgbSourceOver = 17,
    }
}
