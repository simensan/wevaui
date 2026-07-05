namespace Weva.Paint {
    // CSS Compositing 1 §6 — opens a `mix-blend-mode` scope. Every draw
    // emitted between this and the matching PopMixBlendModeCommand composites
    // through the named blend formula against its backdrop. The batched URP
    // backend snapshots the active mode into each emitted UIQuadInstance so
    // the shader can dispatch per-fragment; backends that don't yet implement
    // the formulas (RecordingBackend, SoftwareRasterizer) just record the
    // command for later replay.
    public sealed class PushMixBlendModeCommand : PaintCommand {
        public MixBlendMode Mode { get; private set; }

        public PushMixBlendModeCommand() : base(PaintCommandKind.PushMixBlendMode) { }

        public PushMixBlendModeCommand(MixBlendMode mode) : base(PaintCommandKind.PushMixBlendMode) {
            Mode = mode;
        }

        public void Set(MixBlendMode mode) {
            Mode = mode;
        }

        public void Reset() {
            Mode = MixBlendMode.Normal;
        }

        public override void Submit(IRenderBackend backend) => backend.Submit(this);
    }
}
