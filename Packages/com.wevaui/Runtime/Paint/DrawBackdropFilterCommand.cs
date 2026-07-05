using System;
using Weva.Paint.Filters;

namespace Weva.Paint {
    public sealed class DrawBackdropFilterCommand : PaintCommand {
        public Rect Bounds { get; private set; }
        public BorderRadii Radii { get; private set; }
        public FilterChain Filters { get; private set; }

        public DrawBackdropFilterCommand() : base(PaintCommandKind.DrawBackdropFilter) { }

        public DrawBackdropFilterCommand(Rect bounds, BorderRadii radii, FilterChain filters)
            : base(PaintCommandKind.DrawBackdropFilter) {
            Bounds = bounds;
            Radii = radii;
            Filters = filters ?? throw new ArgumentNullException(nameof(filters));
        }

        public void Set(Rect bounds, BorderRadii radii, FilterChain filters) {
            Bounds = bounds;
            Radii = radii;
            Filters = filters ?? throw new ArgumentNullException(nameof(filters));
        }

        public void Reset() {
            Bounds = default;
            Radii = BorderRadii.Zero;
            Filters = null;
        }

        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }
}
