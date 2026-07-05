using Weva.Layout.Boxes;

namespace Weva.Paint {
    // Tells the active backend to start capturing instances for a subtree
    // identified by `Box`. The matching EndSubtreeCaptureCommand closes the
    // window; everything Submitted in between is recorded as the subtree's
    // BoxBatchSnapshot. Backends that don't support snapshots (everything
    // except BatchedURPRenderBackend) treat this as a no-op.
    public sealed class BeginSubtreeCaptureCommand : PaintCommand {
        public Box Box { get; private set; }
        // Cached parent absolute origin at capture time. Replay uses the
        // delta between this and the current frame's absolute origin to
        // shift instance positions when the subtree drifted without a
        // style.Version bump.
        public double AnchorX { get; private set; }
        public double AnchorY { get; private set; }

        public BeginSubtreeCaptureCommand() : base(PaintCommandKind.BeginSubtreeCapture) { }

        public BeginSubtreeCaptureCommand(Box box, double anchorX, double anchorY)
            : base(PaintCommandKind.BeginSubtreeCapture) {
            Box = box;
            AnchorX = anchorX;
            AnchorY = anchorY;
        }

        public void Set(Box box, double anchorX, double anchorY) {
            Box = box;
            AnchorX = anchorX;
            AnchorY = anchorY;
        }

        public void Reset() {
            Box = null;
            AnchorX = 0;
            AnchorY = 0;
        }

        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }

    public sealed class EndSubtreeCaptureCommand : PaintCommand {
        public Box Box { get; private set; }

        public EndSubtreeCaptureCommand() : base(PaintCommandKind.EndSubtreeCapture) { }

        public EndSubtreeCaptureCommand(Box box) : base(PaintCommandKind.EndSubtreeCapture) {
            Box = box;
        }

        public void Set(Box box) { Box = box; }
        public void Reset() { Box = null; }

        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }

    public sealed class ReplaySubtreeSnapshotCommand : PaintCommand {
        public object Snapshot { get; private set; } // boxed BoxBatchSnapshot (URP-specific type)
        public double OffsetX { get; private set; }
        public double OffsetY { get; private set; }

        public ReplaySubtreeSnapshotCommand() : base(PaintCommandKind.ReplaySubtreeSnapshot) { }

        public ReplaySubtreeSnapshotCommand(object snapshot, double offsetX, double offsetY)
            : base(PaintCommandKind.ReplaySubtreeSnapshot) {
            Snapshot = snapshot;
            OffsetX = offsetX;
            OffsetY = offsetY;
        }

        public void Set(object snapshot, double offsetX, double offsetY) {
            Snapshot = snapshot;
            OffsetX = offsetX;
            OffsetY = offsetY;
        }

        public void Reset() {
            Snapshot = null;
            OffsetX = 0;
            OffsetY = 0;
        }

        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }
}
