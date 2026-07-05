namespace Weva.Paint {
    public sealed class DrawShadowCommand : PaintCommand {
        public Rect Bounds { get; private set; }
        public BorderRadii Radii { get; private set; }
        public BoxShadow Shadow { get; private set; }

        public DrawShadowCommand() : base(PaintCommandKind.DrawShadow) { }

        public DrawShadowCommand(Rect bounds, BorderRadii radii, BoxShadow shadow) : base(PaintCommandKind.DrawShadow) {
            Bounds = bounds;
            Radii = radii;
            Shadow = shadow;
        }

        public void Set(Rect bounds, BorderRadii radii, BoxShadow shadow) {
            Bounds = bounds;
            Radii = radii;
            Shadow = shadow;
        }

        public void Reset() {
            Bounds = default;
            Radii = BorderRadii.Zero;
            Shadow = default;
        }

        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }
}
