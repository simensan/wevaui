namespace Weva.Paint {
    public sealed class PopMaskCommand : PaintCommand {
        public PopMaskCommand() : base(PaintCommandKind.PopMask) { }
        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }
}
