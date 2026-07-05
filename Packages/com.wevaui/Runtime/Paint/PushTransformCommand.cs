namespace Weva.Paint {
    public sealed class PushTransformCommand : PaintCommand {
        public Transform2D Transform { get; private set; }

        public PushTransformCommand() : base(PaintCommandKind.PushTransform) { }

        public PushTransformCommand(Transform2D transform) : base(PaintCommandKind.PushTransform) {
            Transform = transform;
        }

        public void Set(Transform2D transform) {
            Transform = transform;
        }

        public void Reset() {
            Transform = Transform2D.Identity;
        }

        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }
}
