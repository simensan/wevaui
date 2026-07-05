namespace Weva.Paint {
    public sealed class PopMixBlendModeCommand : PaintCommand {
        public PopMixBlendModeCommand() : base(PaintCommandKind.PopMixBlendMode) { }
        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }
}
