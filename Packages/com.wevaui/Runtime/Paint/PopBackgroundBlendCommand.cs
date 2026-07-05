namespace Weva.Paint {
    // CSS Compositing 1 §9 — closes an element-local background-blend-mode
    // scope opened by PushBackgroundBlendCommand. Stateless: a single shared
    // singleton (PaintCommandSingletons.PopBackgroundBlend) is used everywhere.
    public sealed class PopBackgroundBlendCommand : PaintCommand {
        public PopBackgroundBlendCommand() : base(PaintCommandKind.PopBackgroundBlend) { }
        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }
}
