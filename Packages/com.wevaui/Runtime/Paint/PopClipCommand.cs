namespace Weva.Paint {
    public sealed class PopClipCommand : PaintCommand {
        public PopClipCommand() : base(PaintCommandKind.PopClip) { }
        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }
}
