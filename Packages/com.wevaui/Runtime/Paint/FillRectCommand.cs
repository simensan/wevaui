using System;

namespace Weva.Paint {
    public sealed class FillRectCommand : PaintCommand {
        // Mutable internal state for pool reuse. The pool resets these via Set()
        // when an instance is rented; a freshly-rented command is indistinguishable
        // from one constructed via the public ctor. External code reads via the
        // properties only — the setters are private so the pool path is the only
        // mutation surface.
        public Rect Bounds { get; private set; }
        public Brush Brush { get; private set; }
        public BorderRadii Radii { get; private set; }

        public FillRectCommand() : base(PaintCommandKind.FillRect) { }

        public FillRectCommand(Rect bounds, Brush brush, BorderRadii radii) : base(PaintCommandKind.FillRect) {
            Bounds = bounds;
            Brush = brush ?? throw new ArgumentNullException(nameof(brush));
            Radii = radii;
        }

        public FillRectCommand(Rect bounds, Brush brush) : this(bounds, brush, BorderRadii.Zero) { }

        public void Set(Rect bounds, Brush brush, BorderRadii radii) {
            Bounds = bounds;
            Brush = brush ?? throw new ArgumentNullException(nameof(brush));
            Radii = radii;
        }

        public void Reset() {
            Bounds = default;
            Brush = null;
            Radii = BorderRadii.Zero;
        }

        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }
}
