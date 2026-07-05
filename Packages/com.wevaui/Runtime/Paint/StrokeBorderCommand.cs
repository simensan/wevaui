namespace Weva.Paint {
    public sealed class StrokeBorderCommand : PaintCommand {
        public Rect Bounds { get; private set; }
        public Borders Borders { get; private set; }
        public BorderRadii Radii { get; private set; }

        public StrokeBorderCommand() : base(PaintCommandKind.StrokeBorder) { }

        public StrokeBorderCommand(Rect bounds, Borders borders, BorderRadii radii) : base(PaintCommandKind.StrokeBorder) {
            Bounds = bounds;
            Borders = borders;
            Radii = radii;
        }

        public StrokeBorderCommand(Rect bounds, Borders borders) : this(bounds, borders, BorderRadii.Zero) { }

        public void Set(Rect bounds, Borders borders, BorderRadii radii) {
            Bounds = bounds;
            Borders = borders;
            Radii = radii;
        }

        public void Reset() {
            Bounds = default;
            Borders = Borders.None;
            Radii = BorderRadii.Zero;
        }

        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }
}
