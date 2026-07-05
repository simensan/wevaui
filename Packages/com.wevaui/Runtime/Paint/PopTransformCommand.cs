namespace Weva.Paint {
    public sealed class PopTransformCommand : PaintCommand {
        public PopTransformCommand() : base(PaintCommandKind.PopTransform) { }
        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }
}
