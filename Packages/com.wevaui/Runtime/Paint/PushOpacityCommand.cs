namespace Weva.Paint {
    public sealed class PushOpacityCommand : PaintCommand {
        public double Opacity { get; private set; }

        public PushOpacityCommand() : base(PaintCommandKind.PushOpacity) { }

        public PushOpacityCommand(double opacity) : base(PaintCommandKind.PushOpacity) {
            Opacity = opacity;
        }

        public void Set(double opacity) {
            Opacity = opacity;
        }

        public void Reset() {
            Opacity = 0;
        }

        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }
}
