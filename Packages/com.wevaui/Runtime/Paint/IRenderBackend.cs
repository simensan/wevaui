namespace Weva.Paint {
    public interface IRenderBackend {
        void Submit(FillRectCommand command);
        void Submit(StrokeBorderCommand command);
        void Submit(DrawTextCommand command);
        void Submit(DrawShadowCommand command);
        void Submit(DrawBackdropFilterCommand command) { }
        void Submit(PushClipCommand command);
        void Submit(PopClipCommand command);
        void Submit(PushClipPathCommand command) { }
        void Submit(PopClipPathCommand command) { }
        void Submit(PushMaskCommand command) { }
        void Submit(PopMaskCommand command) { }
        void Submit(PushOpacityCommand command);
        void Submit(PopOpacityCommand command);
        void Submit(PushTransformCommand command);
        void Submit(PopTransformCommand command);
        // Filters wrap a sub-region for blur/brightness/etc; backends that do not support
        // shader effects yet may treat these as no-ops (NullBackend) or simply record them
        // (RecordingBackend) until the URP filter pass lands.
        void Submit(PushFilterCommand command);
        void Submit(PopFilterCommand command);
        // CSS Compositing 1 §6 — mix-blend-mode scope. Default no-op so
        // backends that don't implement compositing (NullBackend's count
        // surface, IMGUI-only paths) can stay opt-in. Backends that DO
        // implement it (BatchedURPRenderBackend, RecordingBackend) override.
        void Submit(PushMixBlendModeCommand command) { }
        void Submit(PopMixBlendModeCommand command) { }
        // CSS Compositing 1 §9 — element-local background-blend-mode scope.
        // Blends the enclosed draw against the element's own background-color
        // without sampling the page-backdrop texture. Default no-op.
        void Submit(PushBackgroundBlendCommand command) { }
        void Submit(PopBackgroundBlendCommand command) { }

        // Subtree-snapshot capture/replay markers used by the batched URP
        // backend's per-subtree cache. Default no-op for backends that don't
        // care (IMGUI, SoftwareRasterizer, NullBackend, RecordingBackend).
        void Submit(BeginSubtreeCaptureCommand command) { }
        void Submit(EndSubtreeCaptureCommand command) { }
        void Submit(ReplaySubtreeSnapshotCommand command) { }

        void Submit(PaintList list) {
            if (list == null) return;
            foreach (var c in list.Commands) {
                c.Submit(this);
            }
        }
    }
}
