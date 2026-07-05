namespace Weva.Paint {
    public sealed class PopOpacityCommand : PaintCommand {
        public PopOpacityCommand() : base(PaintCommandKind.PopOpacity) { }
        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }
}
