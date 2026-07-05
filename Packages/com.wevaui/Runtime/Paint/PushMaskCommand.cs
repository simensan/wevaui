using System;

namespace Weva.Paint {
    public sealed class PushMaskCommand : PaintCommand {
        public Rect Bounds { get; private set; }
        public MaskDefinition Mask { get; private set; }

        public PushMaskCommand() : base(PaintCommandKind.PushMask) { }

        public PushMaskCommand(Rect bounds, MaskDefinition mask) : base(PaintCommandKind.PushMask) {
            Bounds = bounds;
            Mask = mask ?? throw new ArgumentNullException(nameof(mask));
        }

        public void Set(Rect bounds, MaskDefinition mask) {
            Bounds = bounds;
            Mask = mask ?? throw new ArgumentNullException(nameof(mask));
        }

        public void Reset() {
            Bounds = default;
            Mask = null;
        }

        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }
}
