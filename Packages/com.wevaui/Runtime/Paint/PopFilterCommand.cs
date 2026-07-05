namespace Weva.Paint {
    public sealed class PopFilterCommand : PaintCommand {
        public PopFilterCommand() : base(PaintCommandKind.PopFilter) { }
        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }
}
