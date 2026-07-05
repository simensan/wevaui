namespace Weva.Paint {
    public sealed class NullBackend : IRenderBackend {
        public int FillRectCount { get; private set; }
        public int StrokeBorderCount { get; private set; }
        public int DrawTextCount { get; private set; }
        public int DrawShadowCount { get; private set; }
        public int DrawBackdropFilterCount { get; private set; }
        public int PushClipCount { get; private set; }
        public int PopClipCount { get; private set; }
        public int PushClipPathCount { get; private set; }
        public int PopClipPathCount { get; private set; }
        public int PushMaskCount { get; private set; }
        public int PopMaskCount { get; private set; }
        public int PushOpacityCount { get; private set; }
        public int PopOpacityCount { get; private set; }
        public int PushTransformCount { get; private set; }
        public int PopTransformCount { get; private set; }
        public int PushFilterCount { get; private set; }
        public int PopFilterCount { get; private set; }
        public int PushMixBlendModeCount { get; private set; }
        public int PopMixBlendModeCount { get; private set; }
        public int PushBackgroundBlendCount { get; private set; }
        public int PopBackgroundBlendCount { get; private set; }

        public int TotalCount =>
            FillRectCount + StrokeBorderCount + DrawTextCount + DrawShadowCount + DrawBackdropFilterCount
            + PushClipCount + PopClipCount + PushClipPathCount + PopClipPathCount
            + PushMaskCount + PopMaskCount + PushOpacityCount + PopOpacityCount
            + PushTransformCount + PopTransformCount
            + PushFilterCount + PopFilterCount
            + PushMixBlendModeCount + PopMixBlendModeCount
            + PushBackgroundBlendCount + PopBackgroundBlendCount;

        public void Submit(FillRectCommand command) { FillRectCount++; }
        public void Submit(StrokeBorderCommand command) { StrokeBorderCount++; }
        public void Submit(DrawTextCommand command) { DrawTextCount++; }
        public void Submit(DrawShadowCommand command) { DrawShadowCount++; }
        public void Submit(DrawBackdropFilterCommand command) { DrawBackdropFilterCount++; }
        public void Submit(PushClipCommand command) { PushClipCount++; }
        public void Submit(PopClipCommand command) { PopClipCount++; }
        public void Submit(PushClipPathCommand command) { PushClipPathCount++; }
        public void Submit(PopClipPathCommand command) { PopClipPathCount++; }
        public void Submit(PushMaskCommand command) { PushMaskCount++; }
        public void Submit(PopMaskCommand command) { PopMaskCount++; }
        public void Submit(PushOpacityCommand command) { PushOpacityCount++; }
        public void Submit(PopOpacityCommand command) { PopOpacityCount++; }
        public void Submit(PushTransformCommand command) { PushTransformCount++; }
        public void Submit(PopTransformCommand command) { PopTransformCount++; }
        public void Submit(PushFilterCommand command) { PushFilterCount++; }
        public void Submit(PopFilterCommand command) { PopFilterCount++; }
        public void Submit(PushMixBlendModeCommand command) { PushMixBlendModeCount++; }
        public void Submit(PopMixBlendModeCommand command) { PopMixBlendModeCount++; }
        public void Submit(PushBackgroundBlendCommand command) { PushBackgroundBlendCount++; }
        public void Submit(PopBackgroundBlendCommand command) { PopBackgroundBlendCount++; }
        public void Submit(BeginSubtreeCaptureCommand command) { }
        public void Submit(EndSubtreeCaptureCommand command) { }
        public void Submit(ReplaySubtreeSnapshotCommand command) { }
    }
}
