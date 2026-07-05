using System.Collections.Generic;

namespace Weva.Paint {
    public sealed class RecordingBackend : IRenderBackend {
        readonly List<PaintCommand> recorded = new List<PaintCommand>();

        public IReadOnlyList<PaintCommand> Recorded => recorded;

        public void Clear() => recorded.Clear();

        public void Submit(FillRectCommand command) => recorded.Add(command);
        public void Submit(StrokeBorderCommand command) => recorded.Add(command);
        public void Submit(DrawTextCommand command) => recorded.Add(command);
        public void Submit(DrawShadowCommand command) => recorded.Add(command);
        public void Submit(DrawBackdropFilterCommand command) => recorded.Add(command);
        public void Submit(PushClipCommand command) => recorded.Add(command);
        public void Submit(PopClipCommand command) => recorded.Add(command);
        public void Submit(PushClipPathCommand command) => recorded.Add(command);
        public void Submit(PopClipPathCommand command) => recorded.Add(command);
        public void Submit(PushMaskCommand command) => recorded.Add(command);
        public void Submit(PopMaskCommand command) => recorded.Add(command);
        public void Submit(PushOpacityCommand command) => recorded.Add(command);
        public void Submit(PopOpacityCommand command) => recorded.Add(command);
        public void Submit(PushTransformCommand command) => recorded.Add(command);
        public void Submit(PopTransformCommand command) => recorded.Add(command);
        public void Submit(PushFilterCommand command) => recorded.Add(command);
        public void Submit(PopFilterCommand command) => recorded.Add(command);
        public void Submit(PushMixBlendModeCommand command) => recorded.Add(command);
        public void Submit(PopMixBlendModeCommand command) => recorded.Add(command);
        public void Submit(PushBackgroundBlendCommand command) => recorded.Add(command);
        public void Submit(PopBackgroundBlendCommand command) => recorded.Add(command);
        public void Submit(BeginSubtreeCaptureCommand command) => recorded.Add(command);
        public void Submit(EndSubtreeCaptureCommand command) => recorded.Add(command);
        public void Submit(ReplaySubtreeSnapshotCommand command) => recorded.Add(command);
    }
}
