namespace Weva.Paint {
    public sealed class PopClipPathCommand : PaintCommand {
        public PopClipPathCommand() : base(PaintCommandKind.PopClipPath) { }
        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }
}
