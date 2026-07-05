namespace Weva.Paint {
    // CSS Compositing 1 §9 — opens an element-local background-blend-mode scope.
    //
    // Unlike PushMixBlendModeCommand (which blends the element against the page
    // backdrop via _WevaBackdrop), this command instructs the batched URP backend
    // to blend the enclosed draw against the element's OWN background-color in
    // ISOLATION from the page. The base color (background-color as used, or
    // transparent black if absent) is baked directly into the UIQuadInstance so
    // the shader can perform the element-local composite without sampling any
    // page-backdrop texture.
    //
    // Encoding contract (must match UIBatcher.BuildInstance and the HLSL path in
    // Weva_FinishFragment):
    //   TransformRow0.z — blend mode ordinal (shared with mix-blend-mode).
    //   TransformRow0.w — 1f  = element-local (this command).
    //   TransformRow1.zw — base color R, G  (linear, unpremultiplied).
    //   TransformRow2.zw — base color B, A  (linear, unpremultiplied).
    public sealed class PushBackgroundBlendCommand : PaintCommand {
        public MixBlendMode Mode { get; private set; }
        // Element's used background-color. Unpremultiplied linear-sRGB;
        // transparent black (0,0,0,0) when background-color is absent.
        public LinearColor BaseColor { get; private set; }

        public PushBackgroundBlendCommand() : base(PaintCommandKind.PushBackgroundBlend) { }

        public PushBackgroundBlendCommand(MixBlendMode mode, LinearColor baseColor)
            : base(PaintCommandKind.PushBackgroundBlend) {
            Mode = mode;
            BaseColor = baseColor;
        }

        public void Set(MixBlendMode mode, LinearColor baseColor) {
            Mode = mode;
            BaseColor = baseColor;
        }

        public void Reset() {
            Mode = MixBlendMode.Normal;
            BaseColor = LinearColor.Transparent;
        }

        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }
}
