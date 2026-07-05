namespace Weva.Paint {
    // Discriminator for the leaf PaintCommand type. Carried as a readonly field
    // on the base class so hot-loop dispatchers (BoxToPaintConverter.ReplayTranslated,
    // PaintCommandPool.ReturnOne) can switch on a single int compare instead of
    // an N-way `isinst` type-pattern cascade. Each subclass sets its discriminator
    // via the base ctor; the field is set once at construction and never mutates.
    public enum PaintCommandKind : byte {
        Unknown = 0,
        FillRect,
        StrokeBorder,
        DrawText,
        DrawShadow,
        DrawBackdropFilter,
        PushClip,
        PopClip,
        PushClipPath,
        PopClipPath,
        PushMask,
        PopMask,
        PushOpacity,
        PopOpacity,
        PushTransform,
        PopTransform,
        PushFilter,
        PopFilter,
        PushMixBlendMode,
        PopMixBlendMode,
        // CSS Compositing 1 §9 — element-local background-blend-mode scope.
        // Unlike PushMixBlendMode (page-backdrop blend), this blends the
        // enclosed draw against the element's own background-color without
        // sampling _WevaBackdrop. The pop shares PopMixBlendMode as a
        // singleton because both pop-directions share the same batcher method.
        PushBackgroundBlend,
        PopBackgroundBlend,
        // Subtree-replay markers. The batched URP backend uses these to
        // capture/replay a per-subtree slice of UIQuadInstances; other
        // backends ignore them.
        BeginSubtreeCapture,
        EndSubtreeCapture,
        ReplaySubtreeSnapshot,
    }

    public abstract class PaintCommand {
        // Set once by each subclass's ctor. Readonly externally; the only
        // mutation surface is a constructor argument so pooled rented
        // instances retain their discriminator across rent/return cycles.
        public PaintCommandKind Kind { get; }

        protected PaintCommand(PaintCommandKind kind) { Kind = kind; }

        public abstract void Submit(IRenderBackend backend);
    }
}
