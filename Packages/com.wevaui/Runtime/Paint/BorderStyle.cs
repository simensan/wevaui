namespace Weva.Paint {
    // Integer values are shader-visible: the UIBatcher passes them verbatim
    // as a float in BorderStyles.x/.y/.z/.w (one per edge). The HLSL shader
    // reads the value and branches on 1 = Solid, 2 = Dashed, 3 = Dotted,
    // 4 = Double. None (0) is never sent to the shader because SubmitStrokeBorder
    // guards on borders.IsNone. Hidden (99) is also never actually drawn — the
    // border-collapsed winner resolver may produce a Hidden winning edge (meaning
    // "suppress this border entirely"), but BoxToPaintConverter.EmitVisibleDecorations
    // checks IsNone which counts Hidden as invisible. The value 99 is chosen to be
    // clearly out-of-range for the shader; if a Hidden edge somehow reaches the
    // GPU the shader's default branch produces no contribution.
    public enum BorderStyle {
        None   = 0,
        Solid  = 1,
        Dashed = 2,
        Dotted = 3,
        Double = 4,
        // Hidden is the collapsed-borders sentinel. In the separate-borders model
        // it renders identically to None. In the collapsed-borders conflict
        // resolution model (CSS 2.2 §17.6.2.1) it wins over every other style,
        // including styles with a greater border-width, so that the shared border
        // disappears entirely.
        Hidden = 99,
    }
}
