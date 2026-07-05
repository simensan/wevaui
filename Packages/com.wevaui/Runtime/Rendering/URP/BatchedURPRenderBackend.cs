using System;
using System.Collections.Generic;
using Weva.Layout.Boxes;
using Weva.Paint;
using Weva.Paint.Images;
using PaintRect = Weva.Paint.Rect;

namespace Weva.Rendering.URP {
    // IRenderBackend that records into a UIBatcher. The render pass drains the batcher
    // each frame and submits the produced UIQuadInstance batches to the GPU.
    //
    // This is the v1 batched-rendering path, an alternative to the per-quad-flush
    // Weva.Rendering.URPRenderBackend that ships in v0.x. It coalesces identical
    // brushes and clips into wider DrawMeshInstanced calls, which is the requirement
    // PLAN§9 calls out for the production pipeline.
    public sealed class BatchedURPRenderBackend : IRenderBackend {
        public UIBatcher Batcher { get; }
        public IImageRegistry ImageRegistry {
            get => Batcher.ImageRegistry;
            set => Batcher.ImageRegistry = value;
        }

        // Synthetic image registry for path-clip coverage textures (B16).
        // BoxToPaintConverter generates coverage images here; the batcher reads
        // them back when building UIQuadBatches with a path-clip mask layer.
        public IImageRegistry SyntheticImageRegistry {
            get => Batcher.SyntheticImageRegistry;
            set => Batcher.SyntheticImageRegistry = value;
        }

        public BatchedURPRenderBackend() : this(new UIBatcher()) { }
        public BatchedURPRenderBackend(UIBatcher batcher) {
            Batcher = batcher ?? throw new ArgumentNullException(nameof(batcher));
        }

        public void BeginFrame() {
            Batcher.Reset();
            activeCaptures.Clear();
        }

        public void EndFrame() {
            Batcher.Finish();
        }

        public bool PrepareText(PaintList list) {
            return SdfTextRendering.PrepareText(list);
        }

        public void Submit(FillRectCommand command) {
            Batcher.SubmitFillRect(command.Bounds, command.Brush, command.Radii);
        }

        public void Submit(StrokeBorderCommand command) {
            Batcher.SubmitStrokeBorder(command.Bounds, command.Borders, command.Radii);
        }

        public void Submit(DrawTextCommand command) {
            SdfTextRendering.EmitGlyphs(Batcher, command);
        }

        public void Submit(DrawShadowCommand command) {
            Batcher.SubmitDrawShadow(command.Bounds, command.Radii, command.Shadow);
        }

        public void Submit(DrawBackdropFilterCommand command) {
            Batcher.DrawBackdropFilter(command.Bounds, command.Radii, command.Filters);
        }

        public void Submit(PushClipCommand command) {
            Batcher.PushClip(command.Bounds, command.Radii);
        }

        public void Submit(PopClipCommand command) {
            Batcher.PopClip();
        }

        public void Submit(PushClipPathCommand command) {
            Batcher.PushClipPath(command.Shape);
        }

        public void Submit(PopClipPathCommand command) {
            Batcher.PopClipPath();
        }

        public void Submit(PushMaskCommand command) {
            Batcher.PushMask(command.Mask);
        }

        public void Submit(PopMaskCommand command) {
            Batcher.PopMask();
        }

        public void Submit(PushOpacityCommand command) {
            Batcher.PushOpacity((float)command.Opacity);
        }

        public void Submit(PopOpacityCommand command) {
            Batcher.PopOpacity();
        }

        public void Submit(PushTransformCommand command) {
            Batcher.PushTransform(command.Transform);
        }

        public void Submit(PopTransformCommand command) {
            Batcher.PopTransform();
        }

        public void Submit(PushFilterCommand command) {
            // Recorded in the batcher's FilterEvents list. The RenderGraph
            // pass walks these events to split paint emission into per-filter
            // ranges and registers temp-RT passes (blur-H, blur-V, color-
            // matrix, drop-shadow tint, composite) per scope. The batcher
            // flushes the in-flight batch on PushFilter so the scope's first
            // batch starts cleanly. ScopeBoxTransform threads the owning
            // element's CSS `transform` through to the composite step so the
            // blur cache survives transform-only animations (see
            // PushFilterCommand.ScopeBoxTransform).
            Batcher.PushFilter(command.Bounds, command.Filters, command.ScopeBoxTransform);
        }

        public void Submit(PopFilterCommand command) {
            // Mirror of Submit(PushFilterCommand) — closes the scope and
            // records its [Begin, End) batch range into FilterEvents.
            Batcher.PopFilter();
        }

        public void Submit(PushMixBlendModeCommand command) {
            // CSS Compositing 1 §6 — record the new page-backdrop blend mode
            // on the batcher's stack. Each subsequent quad picks up the mode
            // in its TransformRow0.z slot via BuildInstance; the shader
            // dispatches the blend formula per-fragment (Option A: no
            // shader keyword variant explosion). This path DOES latch the
            // per-frame anyMixBlendMode flag (it samples _WevaBackdrop).
            Batcher.PushMixBlendMode(command.Mode);
        }

        public void Submit(PopMixBlendModeCommand command) {
            Batcher.PopMixBlendMode();
        }

        public void Submit(PushBackgroundBlendCommand command) {
            // CSS Compositing 1 §9 — element-local background-blend-mode scope.
            // Blends against the element's own background-color (baked into the
            // instance's Row1/Row2 spare channels) without sampling _WevaBackdrop.
            // Does NOT latch anyMixBlendMode; see UIBatcher.PushBackgroundBlend.
            Batcher.PushBackgroundBlend(command.Mode, command.BaseColor);
        }

        public void Submit(PopBackgroundBlendCommand command) {
            Batcher.PopBackgroundBlend();
        }

        // In-flight subtree captures, keyed on the originating Box. Begin
        // records a marker + anchor; End pops them, materialises the
        // snapshot with the anchor stamped, and calls the registered
        // SubtreeSnapshotSink so the painter can stash the snapshot for
        // next-frame replay.
        struct CaptureEntry {
            public UIBatcher.SubtreeMarker Marker;
            public double AnchorX;
            public double AnchorY;
        }
        readonly Dictionary<Box, CaptureEntry> activeCaptures = new Dictionary<Box, CaptureEntry>();
        // Callback invoked with the completed snapshot. Painter wires this
        // to its per-box snapshot dictionary so subsequent frames can
        // splice the cached instances back in.
        public Action<Box, IBoxBatchSnapshot> SubtreeSnapshotSink { get; set; }

        public void Submit(BeginSubtreeCaptureCommand command) {
            if (command.Box == null) return;
            var marker = Batcher.BeginSubtreeCapture();
            activeCaptures[command.Box] = new CaptureEntry {
                Marker = marker,
                AnchorX = command.AnchorX,
                AnchorY = command.AnchorY,
            };
        }

        public void Submit(EndSubtreeCaptureCommand command) {
            if (command.Box == null) return;
            if (!activeCaptures.TryGetValue(command.Box, out var entry)) return;
            activeCaptures.Remove(command.Box);
            var snap = Batcher.EndSubtreeCapture(entry.Marker);
            if (snap == null) return;
            snap.AnchorX = entry.AnchorX;
            snap.AnchorY = entry.AnchorY;
            // Stamp parent context at the moment End is processed (same
            // batcher state that was active at Begin since the marker
            // hasn't allowed any internal pushes to escape).
            Batcher.StampParentContext(snap);
            SubtreeSnapshotSink?.Invoke(command.Box, snap);
        }

        public void Submit(ReplaySubtreeSnapshotCommand command) {
            var snap = command.Snapshot as BoxBatchSnapshot;
            if (snap == null) return;
            Batcher.ReplaySubtreeSnapshot(snap, command.OffsetX, command.OffsetY);
        }
    }
}
