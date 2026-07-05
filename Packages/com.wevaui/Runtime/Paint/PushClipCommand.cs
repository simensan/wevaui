namespace Weva.Paint {
    public sealed class PushClipCommand : PaintCommand {
        public Rect Bounds { get; private set; }
        public BorderRadii Radii { get; private set; }

        public PushClipCommand() : base(PaintCommandKind.PushClip) { }

        public PushClipCommand(Rect bounds, BorderRadii radii) : base(PaintCommandKind.PushClip) {
            Bounds = bounds;
            Radii = radii;
        }

        public PushClipCommand(Rect bounds) : this(bounds, BorderRadii.Zero) { }

        public void Set(Rect bounds, BorderRadii radii) {
            Bounds = bounds;
            Radii = radii;
        }

        public void Reset() {
            Bounds = default;
            Radii = BorderRadii.Zero;
        }

        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }
}
