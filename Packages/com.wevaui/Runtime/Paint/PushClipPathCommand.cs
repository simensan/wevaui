using System;

namespace Weva.Paint {
    public sealed class PushClipPathCommand : PaintCommand {
        public ClipPathShape Shape { get; private set; }

        public PushClipPathCommand() : base(PaintCommandKind.PushClipPath) { }

        public PushClipPathCommand(ClipPathShape shape) : base(PaintCommandKind.PushClipPath) {
            Shape = shape ?? throw new ArgumentNullException(nameof(shape));
        }

        public void Set(ClipPathShape shape) {
            Shape = shape ?? throw new ArgumentNullException(nameof(shape));
        }

        public void Reset() {
            Shape = null;
        }

        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }
}
