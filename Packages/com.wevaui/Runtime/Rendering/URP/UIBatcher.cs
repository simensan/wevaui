using System;
using System.Collections.Generic;
using UnityEngine;
using Weva.Css.Values;
using Weva.Paint;
using Weva.Paint.Filters;
using Weva.Paint.Images;
using PaintRect = Weva.Paint.Rect;
using PaintGradient = Weva.Paint.Gradient;

namespace Weva.Rendering.URP {
    // Folds an IRenderBackend command stream into UIQuadInstance arrays, one per "batch."
    //
    // A batch is a maximal run of quads that share the same shader keyword + stencil ref.
    // Identical-key quads coalesce, breaking on:
    //   - keyword change (brush kind / shadow / text)
    //   - stencil ref change (Push/Pop clip)
    //   - opacity layer boundary (Push/Pop opacity allocates an offscreen RT)
    //
    // The batcher is pure C# — no Unity Mesh / Material / GraphicsBuffer dependencies — so
    // it's exhaustively unit-testable in editor-mode without spinning up the pipeline. The
    // RenderGraph pass consumes the produced batches as plain arrays.
    //
    // Maximum instances per batch is configurable; 1024 is the conservative default that
    // fits the 64 KB CBUFFER on every D3D11/Vulkan/Metal target.
    public sealed class UIBatcher {
        // Default mirrors UIRenderGraphPass.MaxInstancesPerDraw — keeps each batch under
        // the cross-platform 64 KB constant-buffer cap. Authors can raise this when running
        // a batched-pipeline customization that uploads via StructuredBuffer instead of CB.
        public const int DefaultMaxInstancesPerBatch = 1024;

        // Default normalized stop positions used when a 5- or 6-stop gradient has
        // unpositioned stops — CSS Images 3 §3.5 evenly distributes them across
        // [0,1]. Hoisted to avoid a per-quad heap allocation in the gradient
        // submit path (P16). NormalizedPos only reads these as fallbacks; they
        // are never mutated, so sharing a single instance is safe.
        static readonly double[] DefaultStops5 = { 0.0, 0.25, 0.50, 0.75, 1.0 };
        static readonly double[] DefaultStops6 = { 0.0, 0.20, 0.40, 0.60, 0.80, 1.0 };

        // When true, ClipRect is enforced per-fragment in the shader (slot 13)
        // and StencilRef is omitted from the batch key so quads at different
        // clip depths can coalesce. Mirrors UIRenderGraphPass.ForceDisableStencil:
        // both are flipped together. Per-instance ClipRect / Transform mean we
        // also skip flushing on PushClip/PopClip/PushTransform/PopTransform —
        // each emitted instance already snapshots the current state.
        public static bool UseAabbClipping = true;
        public IImageRegistry ImageRegistry { get; set; }

        // B16 — secondary registry for synthetic path coverage images (see
        // BoxToPaintConverter.SyntheticImageRegistry). Only IRawPixelImageSource
        // instances live here; the URP pass converts them to Texture2D on first
        // bind. Set by BatchedURPRenderBackend after the converter is wired up.
        public IImageRegistry SyntheticImageRegistry { get; set; }

        readonly List<UIQuadBatch> batches = new List<UIQuadBatch>();
        readonly List<UIQuadInstance> currentInstances = new List<UIQuadInstance>(256);
        readonly Stack<TransformFrame> transformStack = new Stack<TransformFrame>();
        readonly Stack<float> opacityStack = new Stack<float>();
        readonly Stack<OpacityLayer> opacityLayers = new Stack<OpacityLayer>();
        readonly StencilClipManager clips = new StencilClipManager();
        // Recorded filter scope events, in submission order. Each Push captures the
        // bounds + filter chain + current transform at the time it was emitted, plus
        // the index of the FIRST batch that will be emitted inside the scope. Pop
        // captures the index of the batch immediately AFTER the scope ended.
        // The RenderGraph pass walks these to split paint emission into
        // per-filter ranges and register temp-RT compositing passes around them.
        // Untouched when no filter is active.
        readonly List<FilterEvent> filterEvents = new List<FilterEvent>();
        readonly Stack<int> filterEventStack = new Stack<int>();
        readonly List<BackdropFilterEvent> backdropFilterEvents = new List<BackdropFilterEvent>();
        // Stack of intersected clip rects; the top entry is the AABB applied
        // to all in-flight quads. Sentinel root (NoClipRect) means "no clip".
        // Each PushClip pushes the intersection of the incoming rect with
        // the prior top; PopClip pops it. The top is sampled into every
        // emitted instance's ClipRect slot, replacing the FF-stencil path.
        readonly Stack<Vector4> clipRectStack = new Stack<Vector4>();
        readonly Stack<ClipPathShape> clipPathStack = new Stack<ClipPathShape>();
        readonly Stack<MaskDefinition> maskStack = new Stack<MaskDefinition>();
        // CSS Compositing 1 §6/§9 — unified blend entry stack.
        //
        // Each blend scope (either a page-backdrop mix-blend-mode scope OR an
        // element-local background-blend-mode scope) pushes one BlendEntry.
        // The active entry is snapshotted into every UIQuadInstance via
        // BuildInstance so the shader can dispatch per-fragment.
        //
        // Encoding into UIQuadInstance (slots 10-12):
        //   TransformRow0.z — blend mode ordinal (MixBlendMode enum value)
        //   TransformRow0.w — 0 = page-backdrop (mix-blend-mode §6)
        //                     1 = element-local  (background-blend-mode §9)
        //   TransformRow1.zw — base color R, G  (linear, unpremultiplied)
        //   TransformRow2.zw — base color B, A  (linear, unpremultiplied)
        // For page-backdrop entries (elementLocal=false) the spare channels
        // (Row0.w, Row1.zw, Row2.zw) are zeroed.
        struct BlendEntry {
            public MixBlendMode Mode;
            // True for background-blend-mode (element-local); false for
            // mix-blend-mode (page-backdrop via _WevaBackdrop).
            public bool ElementLocal;
            // Used background-color, linear unpremultiplied. Meaningful only
            // when ElementLocal is true; zero otherwise.
            public LinearColor BaseColor;
        }
        readonly Stack<BlendEntry> blendEntryStack = new Stack<BlendEntry>();
        // Sticky per-frame flag: latches true the first time the document
        // pushes a non-Normal page-backdrop `mix-blend-mode` scope, and stays
        // latched until the next Reset(). The URP RenderGraph pass reads this
        // to decide whether to perform the per-frame camera-color-target copy
        // that feeds `_WevaBackdrop`. Element-local background-blend-mode scopes
        // do NOT sample _WevaBackdrop and must NOT latch this flag — they would
        // otherwise trigger a wasteful full-screen backdrop copy even when no
        // element uses mix-blend-mode (CSS Compositing 1 §10).
        bool anyMixBlendModeInFrame;
        // Count of currently active non-Normal page-backdrop mix-blend-mode scopes.
        // Used to detect outermost Normal→non-Normal and non-Normal→Normal
        // transitions so PushMixBlendMode / PopMixBlendMode can issue a
        // FlushCurrentBatch only at those outer boundaries (CSS Compositing 1
        // §10 B24 v1 batch-break design). Element-local PushBackgroundBlend
        // scopes do NOT update this counter — they never sample the page backdrop.
        int nonNormalBlendDepth;
        // True when the in-flight (currentInstances) batch should have its
        // NeedsBackdropRefresh flag set on the next FlushCurrentBatch. Set by
        // AppendInstance whenever a page-backdrop mix-blend instance lands in the
        // current batch. Reset at the top of FlushCurrentBatch after being latched
        // into the UIQuadBatch struct.
        bool currentBatchNeedsBackdropRefresh;
        // Screen-space AABB of the in-flight batch, accumulated per instance
        // in AppendInstance and latched into UIQuadBatch.PixelBounds at flush.
        // Consumed by the shared-backdrop shareability check (see
        // AppendInstance's comment). Reset to the empty sentinel at flush.
        float currentBatchMinX = float.MaxValue, currentBatchMinY = float.MaxValue;
        float currentBatchMaxX = float.MinValue, currentBatchMaxY = float.MinValue;

        void ResetCurrentBatchBounds() {
            currentBatchMinX = float.MaxValue;
            currentBatchMinY = float.MaxValue;
            currentBatchMaxX = float.MinValue;
            currentBatchMaxY = float.MinValue;
        }
        // Sentinel: a rect huge enough to never reject a fragment.
        static readonly Vector4 NoClipRect = new Vector4(-1e9f, -1e9f, 1e9f, 1e9f);

        UIBatchKey currentKey;
        bool hasCurrentBatch;
        Transform2D currentTransform = Transform2D.Identity;
        float currentOpacity = 1f;
        // Active blend entry (snapshotted per-instance by BuildInstance).
        MixBlendMode currentBlendMode = MixBlendMode.Normal;
        bool currentBlendElementLocal = false;
        LinearColor currentBlendBaseColor = LinearColor.Transparent;

        // ExactSrgbSourceOver (mode 17) is NOT a page-backdrop blend. Per
        // Weva-Quad.shader's Weva_FinishFragment it renders as an ordinary
        // premultiplied fill — the fixed-function One/OneMinusSrcAlpha blend in
        // the sRGB target already performs exact sRGB source-over — and it
        // NEVER samples _WevaBackdrop. So it must not latch the per-frame
        // backdrop copy, set a batch's NeedsBackdropRefresh, or trigger the
        // scope flushes that real CSS §6 page-backdrop modes need. Treating it
        // as a backdrop mode forced every glass bg-color / border / inset-shadow
        // fill into its own batch + a full-screen blit (glass.html measured 127
        // spurious refreshes and 172 batch breaks — the dominant frame cost).
        // The converter still emits the mode-17 wrap (harmless; the shader folds
        // it into the plain-fill path), so this stays a pure batcher-side fix.
        // Kill switch: set true to restore the legacy behaviour (mode 17 treated
        // as a page-backdrop blend → per-batch refresh + scope flushes). Visually
        // identical either way (the shader never samples _WevaBackdrop for mode
        // 17); this only re-adds the wasted work. Default false = fast path.
        public static bool ExactSrgbSourceOverNeedsBackdrop = false;
        static bool IsPageBackdropBlend(MixBlendMode mode) =>
            mode != MixBlendMode.Normal
            && (mode != MixBlendMode.ExactSrgbSourceOver || ExactSrgbSourceOverNeedsBackdrop);
        // Four-slot atlas binding. A text batch can reference up to four
        // distinct glyph atlases (e.g. LiberationSans for text + Segoe UI
        // Emoji for emoji). Each emitted glyph instance carries its slot
        // (0..3) in BorderColorTop.y. When a fifth unique atlasId would
        // be needed the batch flushes. Resets to (0,0) per batch.
        int currentAtlas0;
        int currentAtlas1;
        int currentAtlas2;
        int currentAtlas3;

        // B16 — path coverage mask texture for the current batch.
        // Set when a PushMask with a synthetic path-clip layer is active; null otherwise.
        // Forces a batch break (EnsureBatch → FlushCurrentBatch) when it changes.
        // The resolved Texture2D is stored in UIQuadBatch.MaskImageTexture so
        // UIRenderGraphPass can bind it to _WevaMaskImage per draw call.
        UnityEngine.Texture currentMaskImageTexture;

        public int MaxInstancesPerBatch { get; set; } = DefaultMaxInstancesPerBatch;
        public IReadOnlyList<UIQuadBatch> Batches => batches;
        public IReadOnlyList<FilterEvent> FilterEvents => filterEvents;
        public IReadOnlyList<BackdropFilterEvent> BackdropFilterEvents => backdropFilterEvents;
        public StencilClipManager Clips => clips;
        public int OpacityLayerCount => opacityLayers.Count;
        public Transform2D CurrentTransform => currentTransform;
        public float CurrentOpacity => currentOpacity;
        public MixBlendMode CurrentBlendMode => currentBlendMode;
        // True iff the current frame has pushed at least one non-Normal
        // mix-blend-mode scope. Surfaces the latch above so the URP pass
        // can gate its per-frame backdrop copy.
        public bool HasAnyMixBlendMode => anyMixBlendModeInFrame;

        public UIBatcher() {
            Reset();
        }

        // -------- Subtree-snapshot capture API --------
        // The painter wraps a clean subtree's walk in
        // BeginSubtreeCapture/EndSubtreeCapture so it can splice the resulting
        // instances back next frame via ReplaySubtreeSnapshot without re-
        // walking. Marker captures batcher state at start so EndSubtreeCapture
        // can compute the slice this subtree contributed.
        public struct SubtreeMarker {
            public int BatchCountAtStart;
            public int InstancesInCurrentBatchAtStart;
            public bool HadCurrentBatchAtStart;
            public UIBatchKey CurrentBatchKeyAtStart;
            public int FilterEventCountAtStart;
            public int BackdropFilterEventCountAtStart;
            public int CurrentAtlas0AtStart;
            public int CurrentAtlas1AtStart;
            public int CurrentAtlas2AtStart;
            public int CurrentAtlas3AtStart;
        }

        public SubtreeMarker BeginSubtreeCapture() {
            // A capture must begin on a batch boundary. Otherwise, if the
            // subtree later changes shader key and flushes the current batch,
            // EndSubtreeCapture cannot distinguish instances that were
            // submitted before the subtree from instances emitted by the
            // subtree. That poisoned retained snapshots with unrelated UI and
            // caused duplicated/corrupted quads when hover or scroll replayed
            // the snapshot on later frames.
            FlushCurrentBatch();
            return new SubtreeMarker {
                BatchCountAtStart = batches.Count,
                InstancesInCurrentBatchAtStart = currentInstances.Count,
                HadCurrentBatchAtStart = hasCurrentBatch,
                CurrentBatchKeyAtStart = currentKey,
                FilterEventCountAtStart = filterEvents.Count,
                BackdropFilterEventCountAtStart = backdropFilterEvents.Count,
                CurrentAtlas0AtStart = currentAtlas0,
                CurrentAtlas1AtStart = currentAtlas1,
                CurrentAtlas2AtStart = currentAtlas2,
                CurrentAtlas3AtStart = currentAtlas3,
            };
        }

        // Materialises a BoxBatchSnapshot containing every instance added to
        // the batcher between BeginSubtreeCapture and now. Walks the current
        // in-flight batch (currentInstances) plus any flushed batches that
        // were created in the interval. Returns null when the subtree
        // contained a filter scope — those are not replayable in v1.
        public BoxBatchSnapshot EndSubtreeCapture(SubtreeMarker marker) {
            if (filterEvents.Count != marker.FilterEventCountAtStart) {
                // Filter scope opened inside this subtree; the snapshot would
                // need to replay scope events too. Bail.
                var filterMarked = BoxBatchSnapshot.Rent();
                filterMarked.ContainsFilterScopes = true;
                return filterMarked;
            }
            if (backdropFilterEvents.Count != marker.BackdropFilterEventCountAtStart) {
                var backdropMarked = BoxBatchSnapshot.Rent();
                backdropMarked.ContainsFilterScopes = true;
                return backdropMarked;
            }
            var snap = BoxBatchSnapshot.Rent();
            snap.TextAtlasIdentity = SdfTextRendering.CurrentAtlasIdentity;
            snap.TextAtlasVersion = SdfTextRendering.CurrentAtlasVersion;
            // Flushed batches inside the interval.
            for (int b = marker.BatchCountAtStart; b < batches.Count; b++) {
                var batch = batches[b];
                if (batch.InstanceCount <= 0) continue;
                int start = 0;
                int count = batch.InstanceCount;
                // For the FIRST flushed batch in the interval, the marker
                // might have started mid-batch — but since we record
                // currentInstances.Count BEFORE the marker, any flushed batch
                // whose index > marker.BatchCountAtStart was opened entirely
                // inside the subtree (BeginCapture happened during a different
                // batch). The flush-on-PushFilter etc semantics mean
                // CurrentBatchKey snapshot lets us re-detect mid-batch starts
                // if we needed; v1 just takes the whole batch.
                if (b == marker.BatchCountAtStart && marker.HadCurrentBatchAtStart) {
                    // The marker began with this same batch open — but
                    // FlushCurrentBatch was called before this batch entered
                    // `batches`, so the previously-in-flight contents are now
                    // batches[BatchCountAtStart - 1] and this entry was
                    // opened fresh. Take the whole batch.
                }
                // ArrayPool<>.Rent may return an array LARGER than requested;
                // Count tracks the real instance count so replay still walks
                // the right slice. clearArray:false on Return — captured
                // slots get overwritten before the next consumer reads them.
                var seg = new BoxBatchSnapshot.Segment {
                    Key = batch.Key,
                    Instances = System.Buffers.ArrayPool<UIQuadInstance>.Shared.Rent(count),
                    Count = count,
                    AtlasIdSlot0 = batch.AtlasIdSlot0,
                    AtlasIdSlot1 = batch.AtlasIdSlot1,
                    AtlasIdSlot2 = batch.AtlasIdSlot2,
                    AtlasIdSlot3 = batch.AtlasIdSlot3,
                };
                System.Array.Copy(batch.Instances, start, seg.Instances, 0, count);
                snap.Segments.Add(seg);
            }
            // In-flight current batch: the instances added since the marker.
            if (hasCurrentBatch && currentInstances.Count > marker.InstancesInCurrentBatchAtStart) {
                int start = marker.InstancesInCurrentBatchAtStart;
                int count = currentInstances.Count - start;
                // Only valid if the in-flight batch's KEY hasn't been flushed
                // and replaced. We approximate by comparing against the
                // current key — if it matches the marker's, no flush happened
                // for this batch. Otherwise the marker's in-flight batch was
                // flushed and the current one is fresh; the marker's
                // contribution is already counted in the flushed-batches loop
                // above, and this current batch was opened inside the subtree
                // so we take all of it.
                if (marker.HadCurrentBatchAtStart && marker.CurrentBatchKeyAtStart.Equals(currentKey)) {
                    var seg = new BoxBatchSnapshot.Segment {
                        Key = currentKey,
                        Instances = System.Buffers.ArrayPool<UIQuadInstance>.Shared.Rent(count),
                        Count = count,
                        AtlasIdSlot0 = currentAtlas0,
                        AtlasIdSlot1 = currentAtlas1,
                        AtlasIdSlot2 = currentAtlas2,
                        AtlasIdSlot3 = currentAtlas3,
                    };
                    for (int i = 0; i < count; i++) seg.Instances[i] = currentInstances[start + i];
                    snap.Segments.Add(seg);
                } else {
                    int fullCount = currentInstances.Count;
                    var seg = new BoxBatchSnapshot.Segment {
                        Key = currentKey,
                        Instances = System.Buffers.ArrayPool<UIQuadInstance>.Shared.Rent(fullCount),
                        Count = fullCount,
                        AtlasIdSlot0 = currentAtlas0,
                        AtlasIdSlot1 = currentAtlas1,
                        AtlasIdSlot2 = currentAtlas2,
                        AtlasIdSlot3 = currentAtlas3,
                    };
                    for (int i = 0; i < fullCount; i++) seg.Instances[i] = currentInstances[i];
                    snap.Segments.Add(seg);
                }
            }
            return snap;
        }

        // Splices a previously-captured snapshot's instances into the current
        // frame's batches. For each segment: open a matching batch (key +
        // atlas slots) and append instances translated by (offsetX, offsetY).
        // Replay always intersects the captured per-instance clip with the
        // current parent clip. A scrollport/viewport can shrink while the
        // subtree itself stays clean; reusing the old wider ClipRect would
        // let retained quest cards paint through the footer.
        public void ReplaySubtreeSnapshot(BoxBatchSnapshot snap, double offsetX, double offsetY) {
            if (snap == null || snap.ContainsFilterScopes) return;
            bool needsOffset = offsetX != 0 || offsetY != 0;
            var parentClip = CurrentClipRect();
            var capturedParent = ParentTransformFromSnapshot(snap);
            var currentParent = currentTransform;
            bool remapParentTransform = !Approx(
                new Vector4(currentParent.A, currentParent.B, currentParent.Tx, 0f),
                snap.ParentTransformA)
                || !Approx(
                    new Vector4(currentParent.C, currentParent.D, currentParent.Ty, 0f),
                    snap.ParentTransformB);
            var capturedParentInverse = Transform2D.Identity;
            bool canRemapParent = !remapParentTransform || TryInvert(capturedParent, out capturedParentInverse);
            float opacityScale = 1f;
            if (Mathf.Abs(snap.ParentOpacity - currentOpacity) > 1e-6f) {
                opacityScale = snap.ParentOpacity > 1e-6f ? currentOpacity / snap.ParentOpacity : currentOpacity;
            }
            for (int si = 0; si < snap.Segments.Count; si++) {
                var seg = snap.Segments[si];
                if (seg.Count <= 0) continue;
                EnsureBatch(seg.Key);
                // Restore atlas slots for text segments so the batch's atlas
                // binding matches the captured one. AssignAtlasSlot would
                // re-detect this, but we're bypassing per-instance shaping.
                // Replayed glyph instances carry their slot index baked into
                // BorderColorTop.y against the CAPTURED binding — if the
                // in-flight batch already holds a DIFFERENT atlas in any slot
                // this segment needs, adopting silently would make the glyphs
                // sample the wrong texture. Flush and re-open the same key so
                // the segment's binding applies to empty slots.
                bool atlasConflict =
                       (seg.AtlasIdSlot0 != 0 && currentAtlas0 != 0 && currentAtlas0 != seg.AtlasIdSlot0)
                    || (seg.AtlasIdSlot1 != 0 && currentAtlas1 != 0 && currentAtlas1 != seg.AtlasIdSlot1)
                    || (seg.AtlasIdSlot2 != 0 && currentAtlas2 != 0 && currentAtlas2 != seg.AtlasIdSlot2)
                    || (seg.AtlasIdSlot3 != 0 && currentAtlas3 != 0 && currentAtlas3 != seg.AtlasIdSlot3);
                if (atlasConflict) {
                    var savedKey = currentKey;
                    FlushCurrentBatch();
                    currentKey = savedKey;
                    hasCurrentBatch = true;
                }
                if (seg.AtlasIdSlot0 != 0 && currentAtlas0 == 0) currentAtlas0 = seg.AtlasIdSlot0;
                if (seg.AtlasIdSlot1 != 0 && currentAtlas1 == 0) currentAtlas1 = seg.AtlasIdSlot1;
                if (seg.AtlasIdSlot2 != 0 && currentAtlas2 == 0) currentAtlas2 = seg.AtlasIdSlot2;
                if (seg.AtlasIdSlot3 != 0 && currentAtlas3 == 0) currentAtlas3 = seg.AtlasIdSlot3;
                for (int i = 0; i < seg.Count; i++) {
                    var inst = seg.Instances[i];
                    if (needsOffset) {
                        inst.PosSize.x += (float)offsetX;
                        inst.PosSize.y += (float)offsetY;
                    }
                    if (remapParentTransform && canRemapParent) {
                        RemapInstanceParentTransform(ref inst, capturedParentInverse, currentParent);
                    }
                    if (opacityScale != 1f) {
                        inst.Color *= opacityScale;
                    }
                    var clip = inst.ClipRect;
                    if (remapParentTransform && canRemapParent) {
                        clip = RemapClipRect(clip, capturedParentInverse, currentParent);
                    }
                    inst.ClipRect = IntersectClipRects(clip, parentClip);
                    // CSS Compositing 1 §10 — replayed instances carry their
                    // baked-in MixBlendMode ordinal on TransformRow0.z (see
                    // WriteTransform's preservation contract). PushMixBlendMode
                    // is NOT re-emitted on the replay path, so the per-frame
                    // any-mix-blend latch needs to inspect the replayed value
                    // directly. Otherwise an all-clean frame whose snapshots
                    // contain blend-mode content would skip the URP backdrop
                    // copy and the shader's separable formulas would sample
                    // a stale / zero backdrop.
                    //
                    // CRITICAL: element-local background-blend-mode instances
                    // (TransformRow0.w > 0.5) never sample _WevaBackdrop and
                    // must NOT latch anyMixBlendModeInFrame — that would
                    // trigger a wasteful per-frame backdrop copy even when no
                    // element uses page-backdrop mix-blend-mode (CSS
                    // Compositing 1 §9: element-local blend is self-contained).
                    if (!anyMixBlendModeInFrame
                        && inst.TransformRow0.z > 0.5f
                        && inst.TransformRow0.w < 0.5f) {
                        anyMixBlendModeInFrame = true;
                    }
                    AppendInstance(inst);
                }
            }
        }

        static Transform2D ParentTransformFromSnapshot(BoxBatchSnapshot snap) {
            return new Transform2D(
                snap.ParentTransformA.x,
                snap.ParentTransformA.y,
                snap.ParentTransformB.x,
                snap.ParentTransformB.y,
                snap.ParentTransformA.z,
                snap.ParentTransformB.z);
        }

        static Transform2D TransformFromInstance(UIQuadInstance inst) {
            return new Transform2D(
                inst.TransformRow0.x,
                inst.TransformRow0.y,
                inst.TransformRow1.x,
                inst.TransformRow1.y,
                inst.TransformRow2.x,
                inst.TransformRow2.y);
        }

        static void WriteTransform(ref UIQuadInstance inst, Transform2D t) {
            // Preserve the spare channels packed alongside the affine components:
            //   Row0.z = blend mode ordinal (CSS Compositing 1 §6 mix-blend-mode
            //            OR §9 background-blend-mode; shared slot).
            //   Row0.w = element-local flag (0 = page-backdrop §6,
            //            1 = element-local §9).
            //   Row1.zw = base color R, G  (linear, unpremultiplied; §9 only).
            //   Row2.zw = base color B, A  (linear, unpremultiplied; §9 only).
            // Snapshot-replay paths recompute the affine when a quad's parent
            // transform shifts but must not blow away any other per-instance
            // state packed into the same float4 rows.
            float blendModeOrdinal = inst.TransformRow0.z;
            float elementLocalFlag = inst.TransformRow0.w;
            float baseR = inst.TransformRow1.z;
            float baseG = inst.TransformRow1.w;
            float baseB = inst.TransformRow2.z;
            float baseA = inst.TransformRow2.w;
            inst.TransformRow0 = new Vector4(t.A, t.B, blendModeOrdinal, elementLocalFlag);
            inst.TransformRow1 = new Vector4(t.C, t.D, baseR, baseG);
            inst.TransformRow2 = new Vector4(t.Tx, t.Ty, baseB, baseA);
        }

        static void RemapInstanceParentTransform(ref UIQuadInstance inst, Transform2D capturedParentInverse, Transform2D currentParent) {
            // Captured instance transforms are `local * capturedParent`.
            // Rehydrate the local part, then compose it with the current
            // parent transform so retained scroll/transform/opacity contexts
            // do not replay stale screen-space movement.
            var local = TransformFromInstance(inst).Multiply(capturedParentInverse);
            WriteTransform(ref inst, local.Multiply(currentParent));
        }

        static Vector4 RemapClipRect(Vector4 clip, Transform2D capturedParentInverse, Transform2D currentParent) {
            if (clip.x <= -9e8f && clip.y <= -9e8f && clip.z >= 9e8f && clip.w >= 9e8f) return clip;
            var remap = capturedParentInverse.Multiply(currentParent);
            var p0 = remap.Apply(clip.x, clip.y);
            var p1 = remap.Apply(clip.z, clip.y);
            var p2 = remap.Apply(clip.z, clip.w);
            var p3 = remap.Apply(clip.x, clip.w);
            float minX = (float)Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X));
            float minY = (float)Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y));
            float maxX = (float)Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X));
            float maxY = (float)Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y));
            return new Vector4(minX, minY, maxX, maxY);
        }

        static bool TryInvert(Transform2D t, out Transform2D inverse) {
            float det = t.A * t.D - t.B * t.C;
            if (Mathf.Abs(det) < 1e-8f) {
                inverse = Transform2D.Identity;
                return false;
            }
            float invDet = 1f / det;
            float a = t.D * invDet;
            float b = -t.B * invDet;
            float c = -t.C * invDet;
            float d = t.A * invDet;
            float tx = -(t.Tx * a + t.Ty * c);
            float ty = -(t.Tx * b + t.Ty * d);
            inverse = new Transform2D(a, b, c, d, tx, ty);
            return true;
        }

        static Vector4 IntersectClipRects(Vector4 a, Vector4 b) {
            return new Vector4(
                Math.Max(a.x, b.x),
                Math.Max(a.y, b.y),
                Math.Min(a.z, b.z),
                Math.Min(a.w, b.w));
        }

        // Records parent batcher state into a snapshot so the painter can
        // fast-reject mismatches on replay. Called right after BeginSubtree-
        // Capture so the snapshot's Parent* fields reflect the state that
        // was active when the subtree was walked.
        public void StampParentContext(BoxBatchSnapshot snap) {
            if (snap == null) return;
            snap.ParentTransformA = new Vector4(currentTransform.A, currentTransform.B, (float)currentTransform.Tx, 0f);
            snap.ParentTransformB = new Vector4(currentTransform.C, currentTransform.D, (float)currentTransform.Ty, 0f);
            snap.ParentClipRect = CurrentClipRect();
            snap.ParentOpacity = currentOpacity;
            snap.ParentStencilRef = CurrentRefForBatch();
            snap.ParentAtlas0 = currentAtlas0;
            snap.ParentAtlas1 = currentAtlas1;
            snap.ParentAtlas2 = currentAtlas2;
            snap.ParentAtlas3 = currentAtlas3;
        }

        public bool ParentContextMatches(BoxBatchSnapshot snap) {
            if (snap == null) return false;
            if (Mathf.Abs(snap.ParentOpacity - currentOpacity) > 1e-6f) return false;
            if (snap.ParentStencilRef != CurrentRefForBatch()) return false;
            var ta = new Vector4(currentTransform.A, currentTransform.B, (float)currentTransform.Tx, 0f);
            var tb = new Vector4(currentTransform.C, currentTransform.D, (float)currentTransform.Ty, 0f);
            if (!Approx(ta, snap.ParentTransformA) || !Approx(tb, snap.ParentTransformB)) return false;
            var cr = CurrentClipRect();
            if (!Approx(cr, snap.ParentClipRect)) return false;
            return true;
        }

        static bool Approx(Vector4 a, Vector4 b) {
            return Mathf.Abs(a.x - b.x) < 1e-5f
                && Mathf.Abs(a.y - b.y) < 1e-5f
                && Mathf.Abs(a.z - b.z) < 1e-5f
                && Mathf.Abs(a.w - b.w) < 1e-5f;
        }

        public void Reset() {
            // Return pooled instance arrays acquired by FlushCurrentBatch.
            // clearArray:false — the next consumer (FlushCurrentBatch on a
            // re-rent, or the snapshot copy path) overwrites only the slots
            // it writes and reads only up to its own Count, so the unused
            // tail never leaks captured data downstream. Skipping the zero-
            // wipe avoids ~1 KB worth of memset per pooled batch.
            for (int i = 0; i < batches.Count; i++) {
                var b = batches[i];
                if (b.Instances != null && b.Instances.Length > 0
                    && !ReferenceEquals(b.Instances, Array.Empty<UIQuadInstance>())) {
                    System.Buffers.ArrayPool<UIQuadInstance>.Shared.Return(b.Instances, clearArray: false);
                }
            }
            batches.Clear();
            currentInstances.Clear();
            transformStack.Clear();
            opacityStack.Clear();
            opacityLayers.Clear();
            clips.Reset();
            clipRectStack.Clear();
            filterEvents.Clear();
            filterEventStack.Clear();
            backdropFilterEvents.Clear();
            clipPathStack.Clear();
            maskStack.Clear();
            blendEntryStack.Clear();
            anyMixBlendModeInFrame = false;
            nonNormalBlendDepth = 0;
            currentBatchNeedsBackdropRefresh = false;
            ResetCurrentBatchBounds();
            currentKey = default;
            hasCurrentBatch = false;
            currentTransform = Transform2D.Identity;
            currentOpacity = 1f;
            currentBlendMode = MixBlendMode.Normal;
            currentBlendElementLocal = false;
            currentBlendBaseColor = LinearColor.Transparent;
            currentAtlas0 = 0;
            currentAtlas1 = 0;
            currentAtlas2 = 0;
            currentAtlas3 = 0;
            currentClipRect = NoClipRect;
            currentClipPath = null;
            currentMask = null;
            currentMaskImageTexture = null;
            // B16 — coverage texture cache is NOT cleared on Reset. Textures are reused
            // across frames for the same path shape + size (the cache key is the handle
            // which encodes shape hash + pixel dims). This avoids per-frame Texture2D
            // GC pressure. The cache grows at most O(unique path shapes) per document
            // lifetime — typically O(1) for CSS clip-path. If the document is destroyed,
            // the whole UIBatcher is also dropped so the cache GCs naturally.
        }

        // Tracks the top-of-stack clip rect so BuildInstance can read it
        // with a single field load instead of `clipRectStack.Count == 0
        // ? NoClipRect : clipRectStack.Peek()` per quad. The stack is the
        // source of truth for Pop's restoration; this field is updated
        // alongside every Push/Pop and reset to NoClipRect when the stack
        // empties. With 350+ quads per frame the per-quad Stack.Peek was
        // measurable.
        Vector4 currentClipRect = NoClipRect;
        ClipPathShape currentClipPath;
        MaskDefinition currentMask;

        Vector4 CurrentClipRect() => currentClipRect;
        ClipPathShape CurrentClipPath() => currentClipPath;
        MaskDefinition CurrentMask() => currentMask;

        // Stencil ref to use in the batch key. With AABB clipping the shader
        // discards out-of-rect fragments per-pixel, so distinct clip depths
        // no longer require distinct FF stencil state — collapsing them into
        // one batch kept ~280 frames'-worth of single-instance batches from
        // exploding into separate draws (Solid stencil=2 + Solid stencil=4
        // are now the same Solid batch).
        int CurrentRefForBatch() => UseAabbClipping ? 0 : clips.CurrentRef;

        // Pixel-snap a paint rect to the device pixel grid using WebKit's
        // "enclosing int rect" rule: Floor(left, top), Ceil(right, bottom).
        // This produces a snapped rect that always FULLY CONTAINS the
        // logical (sub-pixel) rect — children never escape their parent's
        // painted area. Crucially, two NESTED rects (e.g. border outer
        // and background inner) snapped independently still preserve their
        // nesting because Floor and Ceil are monotonic — the snapped inner
        // is always inside the snapped outer. That's the property that
        // lets us snap per-Submit instead of needing to thread a single
        // snapped rect through the converter for the whole box.
        //
        // Why snap: with the layout engine producing sub-pixel-precise
        // coords (required for grid-fr / flex-grow / aspect-ratio math
        // — see [[pixel-snap-strategy]]), a rect at world (1224.72, 62.99)
        // ends with its rightmost rasterized pixel only 0.21px inside the
        // silhouette → SDF coverage saturate(0.5+0.21) ≈ 0.72 (72%); the
        // leftmost is 0.78px inside → coverage 1.0. The right edge of a
        // 1px border renders ~28% fainter than the left edge — visible as
        // "right side thinner". Snapping puts both edge pixels 0.5px
        // inside the silhouette so coverage is 1.0 on both sides.
        //
        // Why NOT shadows / text: shadow gaussian falloff is 3σ wide so a
        // 0.5px shift is imperceptible; snapping the shadow rect would
        // unnecessarily shift the halo. Text uses sub-pixel glyph
        // positioning for crisp AA — snapping breaks inter-glyph spacing.
        static PaintRect SnapToPixels(PaintRect r) {
            double left = System.Math.Floor(r.X);
            double top = System.Math.Floor(r.Y);
            double right = System.Math.Ceiling(r.X + r.Width);
            double bottom = System.Math.Ceiling(r.Y + r.Height);
            return new PaintRect(left, top, right - left, bottom - top);
        }

        // Image and gradient content need a stable sampled extent, but not a
        // snapped origin. The enclosing snap above can turn a 48px icon into a
        // 49px quad whenever its origin is fractional. Snapping the origin as
        // well makes icons jump by a whole pixel when flex/percentage layout
        // crosses an integer boundary. Conic/radial/linear overlays have the
        // same issue: if an overlay is snapped but the image beneath keeps its
        // sub-pixel origin, the overlay slides relative to the icon.
        static PaintRect SnapSampledFillToPixels(PaintRect r) {
            double width = System.Math.Round(r.Width);
            double height = System.Math.Round(r.Height);
            if (width < 1 && r.Width > 0) width = 1;
            if (height < 1 && r.Height > 0) height = 1;
            return new PaintRect(r.X, r.Y, width, height);
        }

        // B-9SLICE-SNAP edge snapping lives in Weva.Paint.PixelSnapping
        // (headless-testable; the backend folders are excluded from the
        // headless compile). See the rationale there.

        public void SubmitFillRect(PaintRect bounds, Brush brush, BorderRadii radii) {
            if (bounds.IsEmpty) return;
            if (brush == null) return;
            switch (brush.Kind) {
                case BrushKind.SolidColor:
                    bounds = SnapToPixels(bounds);
                    EmitSolid(bounds, brush.Color, radii);
                    break;
                case BrushKind.Gradient:
                    bounds = brush.Tile.HasValue ? SnapToPixels(bounds) : SnapSampledFillToPixels(bounds);
                    EmitGradient(bounds, brush.GradientValue, radii, brush.Tile);
                    break;
                case BrushKind.Image:
                    // SnapEdgesToDevicePixels (9-slice parts) takes precedence
                    // over the tiled floor/ceil snap: a TILED border-image edge
                    // must round its outer edges the same way the stretched
                    // corner parts do, or it juts past them at the shared
                    // boundary (border-image-repeat:round/repeat). Plain tiled
                    // backgrounds don't set the flag → still SnapToPixels.
                    bounds = brush.SnapEdgesToDevicePixels ? Weva.Paint.PixelSnapping.SnapSlicePartEdges(bounds)
                        : brush.Tile.HasValue ? SnapToPixels(bounds)
                        : SnapSampledFillToPixels(bounds);
                    EmitImage(bounds, brush, radii);
                    break;
            }
        }

        void EmitImage(PaintRect bounds, Brush brush, BorderRadii radii) {
            if (brush == null || string.IsNullOrEmpty(brush.ImageHandle)) return;
            if (!TryResolveImageTexture(ImageRegistry, brush.ImageHandle, out var imageTex, out var baseUv)) {
                // R20: a still-resolving image (async / Addressables load) hits
                // this for a frame or two. In the editor / dev builds paint a
                // magenta placeholder so a missing asset is obvious; in a
                // shipped player emit NOTHING (Chrome's behaviour) so a
                // late-loading image doesn't flash magenta at players.
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                EmitSolid(bounds, new LinearColor(1f, 0f, 1f, 1f), radii);
#endif
                return;
            }

            var src = brush.ImageSourceRect;
            float u0 = baseUv.x + (float)src.X * baseUv.width;
            float v0 = baseUv.y + (float)src.Y * baseUv.height;
            float u1 = baseUv.x + (float)(src.X + src.Width) * baseUv.width;
            float v1 = baseUv.y + (float)(src.Y + src.Height) * baseUv.height;

            var key = new UIBatchKey(UIQuadBrush.Image, isBordered: false,
                stencilRef: CurrentRefForBatch(), atlasId: 0, imageHandle: brush.ImageHandle, imageTexture: imageTex);
            EnsureBatch(key);
            var inst = BuildInstance(bounds, ApplyOpacity(LinearColor.White), radii, UIQuadBrush.Image);
            inst.BrushParams = new Vector4((float)UIQuadBrush.Image, u0, v0, u1);
            inst.BorderColorTop = new Vector4(v1, 0f, 0f, 0f);
            if (brush.Tile.HasValue) {
                var tile = brush.Tile.Value;
                inst.BorderColorRight = new Vector4(
                    (float)tile.OriginX,
                    (float)tile.OriginY,
                    (float)tile.TileWidth,
                    (float)tile.TileHeight);
                inst.BorderColorBottom = new Vector4(
                    (float)tile.RepeatX,
                    (float)tile.RepeatY,
                    (float)tile.GapX,
                    (float)tile.GapY);
            }
            AppendInstance(inst);
        }

        public static bool TryResolveImageTexture(IImageRegistry registry, string handle,
                                                  out UnityEngine.Texture texture,
                                                  out UnityEngine.Rect uvRect) {
            texture = null;
            uvRect = new UnityEngine.Rect(0f, 0f, 1f, 1f);
            if (registry == null || string.IsNullOrEmpty(handle)) return false;
            if (!registry.TryResolve(handle, out var source) || source == null) return false;
            switch (source) {
                case Texture2DImageSource tex2d:
                    texture = tex2d.Texture;
                    return texture != null;
                case SpriteImageSource sprite:
                    texture = sprite.Texture;
                    uvRect = sprite.UvRect;
                    return texture != null;
                case RenderTextureImageSource rt:
                    texture = rt.Texture;
                    return texture != null;
                default:
                    return false;
            }
        }

        public void SubmitStrokeBorder(PaintRect bounds, Borders borders, BorderRadii radii) {
            if (bounds.IsEmpty || borders.IsNone) return;
            // A bordered rect is a single quad rendered with the _BORDERED keyword. The
            // shader reads per-edge widths/colors/styles from the instance data.
            var key = new UIBatchKey(UIQuadBrush.Solid, isBordered: true, stencilRef: CurrentRefForBatch());
            EnsureBatch(key);
            var snapped = SnapToPixels(bounds);
            var inst = BuildInstance(snapped, LinearColor.Transparent, radii, UIQuadBrush.Solid);
            inst.BorderWidths = new Vector4(
                (float)borders.Top.Width,
                (float)borders.Right.Width,
                (float)borders.Bottom.Width,
                (float)borders.Left.Width);
            inst.BorderColorTop = ColorVec(ApplyOpacity(borders.Top.Color));
            inst.BorderColorRight = ColorVec(ApplyOpacity(borders.Right.Color));
            inst.BorderColorBottom = ColorVec(ApplyOpacity(borders.Bottom.Color));
            inst.BorderColorLeft = ColorVec(ApplyOpacity(borders.Left.Color));
            inst.BorderStyles = new Vector4(
                (float)(int)borders.Top.Style,
                (float)(int)borders.Right.Style,
                (float)(int)borders.Bottom.Style,
                (float)(int)borders.Left.Style);
            AppendInstance(inst);
        }

        public void SubmitDrawShadow(PaintRect bounds, BorderRadii radii, BoxShadow shadow) {
            if (bounds.IsEmpty) return;
            var brushKind = shadow.Inset ? UIQuadBrush.ShadowInset : UIQuadBrush.Shadow;
            var key = new UIBatchKey(brushKind, isBordered: false, stencilRef: CurrentRefForBatch());
            EnsureBatch(key);
            // Outset shadows expand the quad by 3σ + |spread| so the gaussian
            // falls off completely inside the quad geometry. The shader's
            // shadow alpha formula `0.5 * (1 - erf(sd / (σ√2)))` decays to
            // 0.07% at 3σ from the shape edge (σ = blur/2, so 3σ = 1.5×blur).
            // Earlier this used `pad = blur + |spread|`, which only reached
            // 1σ past the edge — the alpha at the quad boundary was still
            // ~2.3%, producing a visible HARD CUTOFF where the gaussian
            // halo abruptly stopped instead of continuing to fade. On the
            // map.html quest-pin (24px circle, 12px blur, orange 0.7α) this
            // is what made Unity's halo look "over-extended" vs Chrome:
            // Chrome's halo continues to fade beyond 12px past the pin;
            // Unity's stopped dead at 12px with a faint visible boundary.
            // Animated `box-shadow` (#226) hit the same artifact — the
            // moving cutoff line traced a rectangular silhouette around
            // the animated shape.
            double pad = shadow.Inset ? 0 : (shadow.BlurRadius * 1.5 + Math.Abs(shadow.SpreadRadius));
            var quadRect = new PaintRect(
                bounds.X + (shadow.Inset ? 0 : shadow.OffsetX) - pad,
                bounds.Y + (shadow.Inset ? 0 : shadow.OffsetY) - pad,
                bounds.Width + pad * 2,
                bounds.Height + pad * 2);
            var inst = BuildInstance(quadRect, ApplyOpacity(shadow.Color), radii, brushKind);
            // Slot 4 is free for non-bordered shadow quads. Both inset and
            // outset paths need the authored offset in shader space: inset
            // offsets the inner silhouette; outset uses it to clip the shadow
            // out of the unshifted border box so transparent backgrounds do
            // not reveal the shadow through the element interior.
            inst.BorderWidths = new Vector4((float)shadow.OffsetX, (float)shadow.OffsetY, 0f, 0f);
            // BrushParams: x = brush index, y = blur radius, z = spread radius, w = inset flag
            inst.BrushParams = new Vector4(
                (float)brushKind,
                (float)shadow.BlurRadius,
                (float)shadow.SpreadRadius,
                shadow.Inset ? 1f : 0f);
            AppendInstance(inst);
        }

        public void SubmitDrawText(PaintRect bounds, LinearColor color) {
            // Text rendering with the SDF atlas is implemented in SdfTextRendering — this
            // method handles the fallback when no atlas is bound: emit a colored rect so
            // bounds are visible but glyphs are placeholders.
            if (bounds.IsEmpty) return;
            EmitSolid(bounds, color, BorderRadii.Zero);
        }

        public void SubmitGlyphQuads(IReadOnlyList<SdfGlyphQuad> glyphs) {
            SubmitGlyphQuads(glyphs, 0);
        }

        // atlasId disambiguates which Texture2D the renderer binds for the
        // glyph. The batcher carries up to two distinct atlas IDs per batch;
        // each instance encodes its slot (0 or 1) into BorderColorTop.y.
        // A third unique atlasId triggers a flush. Zero atlasId = legacy
        // fallback (no atlas).
        public void SubmitGlyphQuads(IReadOnlyList<SdfGlyphQuad> glyphs, int atlasId) {
            if (glyphs == null || glyphs.Count == 0) return;
            // Key drops AtlasId — atlas binding is now per-batch via the
            // (atlas0, atlas1) pair stored on UIQuadBatch. All Text quads on
            // the same stencil ref share one key and merge unless we exceed
            // the two-slot capacity.
            var key = new UIBatchKey(UIQuadBrush.Text, isBordered: false, stencilRef: CurrentRefForBatch(), atlasId: 0);
            EnsureBatch(key);
            for (int i = 0; i < glyphs.Count; i++) {
                var g = glyphs[i];
                // Per-glyph atlas dispatch. SdfGlyphQuad.AtlasId == 0 means
                // "use the run-level primary atlas"; non-zero is a fallback
                // face's registered id (set by SdfGlyphAtlasAdapter when the
                // glyph came from chainIndex >= 1, e.g. an emoji fallback).
                // Decoration quads have UV area = 0; their atlas slot
                // doesn't matter (the shader's degenerate-UV branch returns
                // the fillColor without sampling), so we send them on the
                // run's primary slot to keep them in the same batch.
                int quadAtlasId = g.AtlasId != 0 ? g.AtlasId : atlasId;
                bool degenerateUv = g.UvMax.x <= g.UvMin.x || g.UvMax.y <= g.UvMin.y;
                if (degenerateUv) quadAtlasId = atlasId;
                // CRITICAL ORDERING: AssignAtlasSlot MUST run AFTER EnsureBatch.
                // EnsureBatch may flush a prior Solid batch (and reset
                // currentAtlas0/1 to 0). If atlas tracking ran first, the slot
                // assignment would apply to the OLD Solid batch's data and the
                // new Text batch would start with no atlas tracking — every
                // text instance would render slot 0 against an unbound _GlyphAtlas.
                //
                // AssignAtlasSlot may flush + re-open a fresh Text batch when
                // a third unique atlas would be needed for this run, or when
                // a color/mono atlas mismatch is detected (the _TEXT_COLOR
                // shader keyword is per-batch, so mono SDF and RGBA color
                // quads must land in different batches).
                int slot = AssignAtlasSlot(quadAtlasId);
                bool atlasIsColor = quadAtlasId != 0
                    && Weva.Text.Sdf.AtlasRegistry.IsColorAtlasId(quadAtlasId);
                bool atlasIsCoverage = quadAtlasId != 0
                    && Weva.Text.Sdf.AtlasRegistry.IsCoverageAtlasId(quadAtlasId);
                // Text-default emoji (↩ ⏸ etc.) come from a color atlas but
                // should pick up CSS color rather than the texel's RGB. Bit
                // 2 of slotEnc tells the shader to use texel.a as coverage
                // and apply fillColor.rgb. Only meaningful for color atlases
                // — mono atlas batches ignore the bit.
                bool tintColor = g.TintWithFillColor && atlasIsColor;
                var inst = BuildInstance(g.Bounds, ApplyOpacity(g.Color), BorderRadii.Zero, UIQuadBrush.Text);
                // BrushParams: x=brush, y=u0, z=v0, w=u1. The remaining
                // UV-rect component (v1) plus per-glyph metadata (atlas slot
                // + color flag, text-shadow blur radius) is packed into
                // BorderColorTop. The shader reads them back via
                // ReadInstance(id, 5).
                inst.BrushParams = new Vector4(
                    (float)UIQuadBrush.Text,
                    g.UvMin.x, g.UvMin.y, g.UvMax.x);
                // BorderColorTop.x = v1 (from atlas UV rect),
                //                .y = (slot | (isColor ? 2 : 0)) — packed
                //                     tuple. Slot is 0/1 (low bit), color
                //                     flag is the next bit. Pre-fix this
                //                     value was just `slot` and the shader
                //                     read color-ness from a per-batch
                //                     keyword; per-instance dispatch
                //                     unifies mono+color text in one batch.
                //                .z = text-shadow blur radius in pixels
                //                     (0 = crisp). The shader uses this to
                //                     widen the SDF AA band so the glyph
                //                     silhouette feathers outward (CSS Text
                //                     Decoration §6 Path A).
                //                .w = faux-bold SDF-threshold shift
                //                     (computed from CSS font-weight in
                //                     SdfGlyphAtlasAdapter). 0 = regular.
                float slotEnc = slot
                    + (atlasIsColor ? 4 : 0)
                    + (tintColor ? 8 : 0)
                    + (atlasIsCoverage ? 16 : 0);
                inst.BorderColorTop = new Vector4(g.UvMax.y, slotEnc, g.BlurRadius, g.WeightBias);
                AppendInstance(inst);
            }
        }

        // Returns the slot (0..3) in this batch's atlas binding set that
        // should be used for `atlasId`. Adopts an empty slot if available;
        // flushes the in-flight batch and starts a fresh Text batch with this
        // atlas in slot 0 when a fifth unique atlas would be needed.
        // atlasId == 0 (no atlas, fallback path) maps to slot 0 — those
        // quads early-return before sampling so the binding doesn't matter.
        //
        // Color (RGBA-bitmap) atlases and mono SDF atlases are NEVER co-bound
        // in the same batch: the shader picks one path or the other for the
        // whole draw via the _TEXT_COLOR keyword. If the incoming atlasId's
        // color-ness disagrees with anything already in the slots, we flush
        // and start a fresh batch. This is the source of "color text and
        // mono text in DIFFERENT batches" mentioned in the v0.9 plan.
        int AssignAtlasSlot(int atlasId) {
            if (atlasId == 0) return 0;
            if (currentAtlas0 == atlasId) return 0;
            if (currentAtlas1 == atlasId) return 1;
            if (currentAtlas2 == atlasId) return 2;
            if (currentAtlas3 == atlasId) return 3;
            // The shader dispatches color-bitmap vs mono-SDF per-instance
            // via slotEnc, so we no longer have to flush on a color/mono
            // mismatch between consecutive glyphs. Slots remain
            // mode-constrained (each slot's bound texture is one format),
            // but slot 0 and slot 1 can hold different formats. Only the
            // "third unique atlas" condition still forces a flush.
            if (currentAtlas0 == 0) { currentAtlas0 = atlasId; return 0; }
            if (currentAtlas1 == 0) { currentAtlas1 = atlasId; return 1; }
            if (currentAtlas2 == 0) { currentAtlas2 = atlasId; return 2; }
            if (currentAtlas3 == 0) { currentAtlas3 = atlasId; return 3; }
            // All slots occupied with different ids. Flush the in-flight
            // text batch (writes it with the current atlas0/1) then re-open
            // a fresh text batch keyed the same way, with this atlas in slot 0.
            var savedKey = currentKey;
            FlushCurrentBatch();
            currentKey = savedKey;
            hasCurrentBatch = true;
            currentAtlas0 = atlasId;
            return 0;
        }

        public void PushClip(PaintRect bounds, BorderRadii radii) {
            // With AABB clipping (UseAabbClipping=true) the per-instance ClipRect
            // slot scopes this push to subsequently-emitted quads, so we don't
            // need to flush the in-flight batch — quads emitted before this
            // push already snapshotted the previous (looser) clip and quads
            // emitted after will snapshot the new (tighter) one. Flushing here
            // shattered same-key batches around every overflow:hidden boundary.
            // Keep the flush only when we're actually using FF stencil writes.
            if (!UseAabbClipping) FlushCurrentBatch();
            clips.PushClip(bounds, radii, currentTransform);
            if (UseAabbClipping) {
                clipPathStack.Push(currentClipPath);
                if (!radii.IsZero && bounds.Width > 0 && bounds.Height > 0) {
                    currentClipPath = new InsetClipPathShape(bounds, radii).Transform(currentTransform);
                }
            }
            // Compute the new clip rect (intersection with the parent's). The
            // bounds passed in are in layout pixel space, but the shader tests
            // this scissor against the POST-transform fragment position
            // (worldPos). So when the clip's element sits under a CSS transform
            // (e.g. combat-hud's `.target-frame { transform: translateX(-50%) }`
            // containing a `.target-meter { overflow: hidden }`), the raw layout
            // rect is offset from the painted pixels — the meter clipped its own
            // red HP fill away because the fill painted 180px left of the
            // un-transformed clip rect. Map the rect through currentTransform
            // (same space as worldPos) by transforming its corners and taking
            // their AABB. Translate + scale are exact; rotation/skew degrade to
            // the AABB, which is acceptable (and unchanged for the identity
            // transform, so non-transformed clips are byte-identical).
            var c0 = currentTransform.Apply(bounds.X, bounds.Y);
            var c1 = currentTransform.Apply(bounds.X + bounds.Width, bounds.Y);
            var c2 = currentTransform.Apply(bounds.X, bounds.Y + bounds.Height);
            var c3 = currentTransform.Apply(bounds.X + bounds.Width, bounds.Y + bounds.Height);
            float xmin = (float)Math.Min(Math.Min(c0.X, c1.X), Math.Min(c2.X, c3.X));
            float ymin = (float)Math.Min(Math.Min(c0.Y, c1.Y), Math.Min(c2.Y, c3.Y));
            float xmax = (float)Math.Max(Math.Max(c0.X, c1.X), Math.Max(c2.X, c3.X));
            float ymax = (float)Math.Max(Math.Max(c0.Y, c1.Y), Math.Max(c2.Y, c3.Y));
            var parent = CurrentClipRect();
            var intersected = new Vector4(
                Math.Max(parent.x, xmin),
                Math.Max(parent.y, ymin),
                Math.Min(parent.z, xmax),
                Math.Min(parent.w, ymax));
            clipRectStack.Push(intersected);
            currentClipRect = intersected;
        }

        public void PopClip() {
            if (!UseAabbClipping) FlushCurrentBatch();
            clips.PopClip();
            if (UseAabbClipping) {
                currentClipPath = clipPathStack.Count > 0 ? clipPathStack.Pop() : null;
            }
            if (clipRectStack.Count > 0) clipRectStack.Pop();
            currentClipRect = clipRectStack.Count == 0 ? NoClipRect : clipRectStack.Peek();
        }

        public void PushClipPath(ClipPathShape shape) {
            if (shape == null) {
                // PopClipPath unconditionally pops BOTH stacks, so a null
                // (unresolvable) shape must still push a clip-rect frame —
                // the unchanged current rect — or the matching pop would
                // consume an enclosing PushClip's rect and mis-clip
                // everything after it for the rest of the frame.
                clipRectStack.Push(CurrentClipRect());
                clipPathStack.Push(currentClipPath);
                return;
            }
            var transformed = shape.Transform(currentTransform);
            var b = transformed.Bounds;
            var parent = CurrentClipRect();
            var intersected = new Vector4(
                Math.Max(parent.x, (float)b.X),
                Math.Max(parent.y, (float)b.Y),
                Math.Min(parent.z, (float)b.Right),
                Math.Min(parent.w, (float)b.Bottom));
            clipRectStack.Push(intersected);
            currentClipRect = intersected;
            clipPathStack.Push(currentClipPath);
            currentClipPath = transformed;
        }

        public void PopClipPath() {
            if (clipRectStack.Count > 0) clipRectStack.Pop();
            currentClipRect = clipRectStack.Count == 0 ? NoClipRect : clipRectStack.Peek();
            currentClipPath = clipPathStack.Count > 0 ? clipPathStack.Pop() : null;
        }

        public void PushMask(MaskDefinition mask) {
            maskStack.Push(currentMask);
            currentMask = mask;
            // B16 — if the new mask has a synthetic path-clip layer, resolve the
            // coverage texture and force a batch break so all subsequent instances
            // in this scope share a single _WevaMaskImage binding.
            var newMaskTex = ResolveSyntheticMaskImageTexture(mask);
            if (!ReferenceEquals(newMaskTex, currentMaskImageTexture)) {
                if (hasCurrentBatch) FlushCurrentBatch();
                currentMaskImageTexture = newMaskTex;
            }
        }

        public void PopMask() {
            currentMask = maskStack.Count > 0 ? maskStack.Pop() : null;
            // B16 — restore the mask image texture for the previous scope.
            var prevMaskTex = ResolveSyntheticMaskImageTexture(currentMask);
            if (!ReferenceEquals(prevMaskTex, currentMaskImageTexture)) {
                if (hasCurrentBatch) FlushCurrentBatch();
                currentMaskImageTexture = prevMaskTex;
            }
        }

        // B16 — scans the mask definition for a synthetic path-clip layer (kind=5,
        // IsSyntheticClipMask=true) and resolves its image handle to a Texture2D
        // via the SyntheticImageRegistry. Returns null when no such layer is present
        // or when the registry cannot resolve the handle. Uses a lazily-created
        // Texture2D cache keyed by handle string; the registry holds the source
        // bytes and the texture is created once per unique path shape + pixel size.
        UnityEngine.Texture ResolveSyntheticMaskImageTexture(MaskDefinition mask) {
            if (mask == null || SyntheticImageRegistry == null) return null;
            foreach (var layer in mask.Layers) {
                if (layer == null || !layer.IsSyntheticClipMask) continue;
                if (layer.Brush == null || layer.Brush.Kind != BrushKind.Image) continue;
                string handle = layer.Brush.ImageHandle;
                if (string.IsNullOrEmpty(handle)) continue;
                if (!SyntheticImageRegistry.TryResolve(handle, out var src) || src == null) continue;
                if (!(src is Weva.Paint.Images.IRawPixelImageSource raw)) continue;
                if (raw.Pixels == null || raw.Width <= 0 || raw.Height <= 0) continue;
                // Convert to Texture2D (or return cached) via the coverage texture cache.
                return GetOrCreateCoverageTexture(handle, raw);
            }
            return null;
        }

        // B16 — per-batcher cache of Texture2D objects created from IRawPixelImageSource
        // coverage images. Keyed by the synthetic handle string. Lives for the batcher's
        // lifetime (typically one render pass / document). Texture2D objects are created
        // with RGBA32 format + bilinear filter, no mipmap — the coverage data is
        // already anti-aliased by the 4x4 supersampling rasterizer.
        System.Collections.Generic.Dictionary<string, UnityEngine.Texture2D> _coverageTextureCache;

        UnityEngine.Texture GetOrCreateCoverageTexture(string handle, Weva.Paint.Images.IRawPixelImageSource raw) {
            if (_coverageTextureCache == null)
                _coverageTextureCache = new System.Collections.Generic.Dictionary<string, UnityEngine.Texture2D>();
            if (_coverageTextureCache.TryGetValue(handle, out var existing)) return existing;
            var tex = new UnityEngine.Texture2D(raw.Width, raw.Height,
                UnityEngine.TextureFormat.RGBA32, mipChain: false, linear: true) {
                filterMode = UnityEngine.FilterMode.Bilinear,
                wrapMode = UnityEngine.TextureWrapMode.Clamp,
                name = "Weva_PathCoverage_" + handle,
            };
            // Copy pixels STRAIGHT — no row flip. IRawPixelImageSource is
            // R8G8B8A8 linear with a top-left origin, and the mask-layer UV
            // math in the shader already maps worldPos in CSS top-down space
            // onto the tile rect with the same convention as url() mask
            // images. A "helpful" bottom-up flip here double-flips: verified
            // game-view-true in play mode (NOT an offscreen-probe artifact —
            // probes take a different Y path and must never calibrate
            // orientation), the path() fixture shapes rendered upside down
            // until this became a straight copy.
            tex.LoadRawTextureData(raw.Pixels);
            tex.Apply(updateMipmaps: false);
            _coverageTextureCache[handle] = tex;
            return tex;
        }

        public void DrawBackdropFilter(PaintRect bounds, BorderRadii radii, FilterChain filters) {
            if (bounds.IsEmpty || filters == null || filters.IsEmpty) return;
            FlushCurrentBatch();
            backdropFilterEvents.Add(new BackdropFilterEvent(bounds, radii, filters, currentTransform, batches.Count));
        }

        public void PushOpacity(float opacity) {
            opacityStack.Push(currentOpacity);
            currentOpacity = Mathf.Clamp01(currentOpacity * Mathf.Clamp01(opacity));
            // Opacity is folded into per-instance Color via ApplyOpacity() so
            // we don't need to flush. The opacityLayers marker is tracked
            // anyway in case the RenderGraph pass later wants to composite an
            // offscreen RT around this range.
            opacityLayers.Push(new OpacityLayer(opacity, batches.Count));
        }

        public void PopOpacity() {
            if (opacityStack.Count == 0) return;
            currentOpacity = opacityStack.Pop();
            if (opacityLayers.Count > 0) opacityLayers.Pop();
        }

        // CSS Compositing 1 §6 — open a page-backdrop mix-blend-mode scope.
        // The active mode is sampled into every emitted UIQuadInstance's
        // TransformRow0.z by BuildInstance, so a Push doesn't strictly
        // need to flush — the next quad will snapshot the updated value.
        // We don't track a separate FilterEvent-style range because the
        // blend formula is purely per-fragment in the shader (no offscreen
        // RT round-trip yet).
        //
        // This push DOES latch anyMixBlendModeInFrame (page-backdrop path
        // samples _WevaBackdrop). For element-local §9 scopes, call
        // PushBackgroundBlend instead — it must NOT latch the flag.
        //
        // B24 v1 batch-break contract (CSS Compositing 1 §10):
        // The backdrop of a blended element is "everything rendered prior to
        // the element". Blitting colorTarget → _WevaBackdrop happens once per
        // batch flagged NeedsBackdropRefresh; to ensure all pre-scope content
        // is in a completed batch (and therefore already drawn when the blit
        // fires), we flush at the outermost Normal→non-Normal transition only.
        // Nested non-Normal pushes don't re-flush — the depth is already > 0.
        public void PushMixBlendMode(MixBlendMode mode) {
            blendEntryStack.Push(new BlendEntry {
                Mode = currentBlendMode,
                ElementLocal = currentBlendElementLocal,
                BaseColor = currentBlendBaseColor,
            });
            currentBlendMode = mode;
            currentBlendElementLocal = false;
            currentBlendBaseColor = LinearColor.Transparent;
            // ExactSrgbSourceOver (17) is excluded here — it is not a
            // page-backdrop blend (see IsPageBackdropBlend) so it must not latch
            // the backdrop copy, bump the depth, or force a flush.
            if (IsPageBackdropBlend(mode)) {
                // Latch the per-frame flag the first time the document opens
                // a non-Normal page-backdrop scope. The URP pass reads this
                // to gate its per-frame backdrop copy (CSS Compositing 1 §10):
                // no scope means the separable blend formulas are never
                // dispatched, so we skip the full-screen blit entirely.
                anyMixBlendModeInFrame = true;
                // At the outermost Normal→non-Normal boundary: flush so that
                // everything painted BEFORE this scope is committed to a
                // completed batch. The per-batch backdrop refresh in
                // DrainBatches then captures a colorTarget that already
                // includes all preceding UI (body bg, sibling panels, etc.).
                if (nonNormalBlendDepth == 0) {
                    FlushCurrentBatch();
                }
                nonNormalBlendDepth++;
            }
        }

        // CSS Compositing 1 §9 — open an element-local background-blend-mode
        // scope. The enclosed quads blend against `baseColor` (the element's
        // used background-color) WITHOUT sampling _WevaBackdrop. Therefore
        // this push must NOT latch anyMixBlendModeInFrame (that flag gates the
        // expensive per-frame backdrop copy; element-local blending never needs
        // the page backdrop). V1 limitation: when multiple image layers stack
        // with non-Normal modes, each still blends against the background-color
        // base only — inter-layer blending (image N against image N-1) is not
        // composited here; the base is always the background-color (§9 note).
        public void PushBackgroundBlend(MixBlendMode mode, LinearColor baseColor) {
            blendEntryStack.Push(new BlendEntry {
                Mode = currentBlendMode,
                ElementLocal = currentBlendElementLocal,
                BaseColor = currentBlendBaseColor,
            });
            currentBlendMode = mode;
            currentBlendElementLocal = true;
            currentBlendBaseColor = baseColor;
            // Do NOT latch anyMixBlendModeInFrame — element-local blending
            // never samples _WevaBackdrop (CSS Compositing 1 §9).
        }

        // Unified pop for both page-backdrop (§6) and element-local (§9) scopes.
        // Restores whichever blend entry was on top of the stack.
        //
        // B24 v1 batch-break contract: at the outermost non-Normal→Normal
        // transition (nonNormalBlendDepth going 1→0), flush the in-flight
        // batch so subsequent Normal content starts in a fresh batch. This
        // keeps the backdrop-refresh bookkeeping clean: a batch that contains
        // ONLY normal content never gets NeedsBackdropRefresh set.
        // Element-local PopBackgroundBlend calls reach here too, but they
        // never incremented nonNormalBlendDepth, so no extra flush fires.
        public void PopMixBlendMode() {
            // Determine whether the scope being popped was a non-Normal
            // page-backdrop scope BEFORE restoring the entry.
            bool wasNonNormalPageBackdrop = !currentBlendElementLocal
                && IsPageBackdropBlend(currentBlendMode);
            if (blendEntryStack.Count == 0) {
                currentBlendMode = MixBlendMode.Normal;
                currentBlendElementLocal = false;
                currentBlendBaseColor = LinearColor.Transparent;
                // Underflow — depth may be inconsistent; guard below.
            } else {
                var prev = blendEntryStack.Pop();
                currentBlendMode = prev.Mode;
                currentBlendElementLocal = prev.ElementLocal;
                currentBlendBaseColor = prev.BaseColor;
            }
            if (wasNonNormalPageBackdrop && nonNormalBlendDepth > 0) {
                nonNormalBlendDepth--;
                // At the outermost transition back to Normal, flush so that
                // post-scope Normal quads land in a separate, unflagged batch.
                if (nonNormalBlendDepth == 0) {
                    FlushCurrentBatch();
                }
            }
        }

        // Alias so BatchedURPRenderBackend can call the correct method name for
        // PopBackgroundBlendCommand submissions. Both §6 and §9 scopes use the
        // same unified stack; either pop can close either push safely as long as
        // the paint command stream is balanced (guaranteed by BoxToPaintConverter).
        public void PopBackgroundBlend() => PopMixBlendMode();

        public void PushTransform(Transform2D local) {
            // Per-instance TransformRow0/1/2 carry the active 2D affine, so a
            // transform push doesn't need to flush — the next quad emitted will
            // snapshot the updated currentTransform into its instance data.
            transformStack.Push(new TransformFrame(currentTransform));
            currentTransform = local.Multiply(currentTransform);
        }

        public void PopTransform() {
            if (transformStack.Count == 0) return;
            currentTransform = transformStack.Pop().Previous;
        }

        // Mark the start of a filter scope. The RenderGraph pass uses the
        // recorded event list to redirect subsequent batches into an offscreen
        // RT, run the filter chain, and composite the result back into the
        // main color target on the matching PopFilter.
        //
        // Empty / null chains are ignored — the pass would otherwise allocate a
        // zero-effect RT pair per such call. Filters that are entirely no-ops
        // (e.g. blur(0px) chained with nothing else) currently still take the
        // RT path; trimming them at parse time is a separate optimization.
        //
        // We FLUSH the in-flight batch so the filter scope starts on a clean
        // boundary and so the recorded batch index points exactly at the first
        // batch emitted inside the scope. Without the flush, an in-progress
        // solid batch from BEFORE the filter would carry over and be drawn
        // INTO the filter's offscreen RT (visually: the background of the
        // parent would re-paint on top of the filtered content). Same hazard
        // for the matching PopFilter — both ends need a flush.
        public void PushFilter(PaintRect bounds, FilterChain filters) {
            PushFilter(bounds, filters, Transform2D.Identity);
        }

        // Pool overload that wires the box's own CSS `transform` through to
        // the composite step (see FilterEvent.ScopeBoxTransform). Identity =
        // no transform → behaves identically to the 2-arg overload.
        public void PushFilter(PaintRect bounds, FilterChain filters, Transform2D scopeBoxTransform) {
            if (filters == null || filters.IsEmpty) {
                // Push a sentinel event with -1 BeginIndex so PopFilter's
                // stack stays balanced. The pass scheduler ignores sentinels.
                FlushCurrentBatch();
                filterEventStack.Push(-1);
                return;
            }
            FlushCurrentBatch();
            int idx = filterEvents.Count;
            filterEvents.Add(new FilterEvent(
                FilterEventKind.Push,
                bounds,
                filters,
                currentTransform,
                scopeBoxTransform,
                batches.Count,
                -1,
                -1));
            filterEventStack.Push(idx);
        }

        public void PopFilter() {
            if (filterEventStack.Count == 0) return;
            int pushIdx = filterEventStack.Pop();
            if (pushIdx < 0) {
                // Sentinel (Push with empty chain) — no event to close.
                return;
            }
            FlushCurrentBatch();
            int endIndex = batches.Count;
            var push = filterEvents[pushIdx];
            int popIdx = filterEvents.Count;
            // Update the Push event with its End index now that we know it.
            // Use the 8-arg constructor (preserving ScopeBoxTransform) so
            // the cached-composite path can still place the blurred RT
            // with the owning element's CSS `transform` applied — the
            // 7-arg overload resets ScopeBoxTransform to Identity, which
            // pinned cached scopes at their pre-transform origin and made
            // transform-animated filtered elements (canonical case: the
            // aurora's translate+scale animation) drift visibly.
            filterEvents[pushIdx] = new FilterEvent(
                push.Kind,
                push.Bounds,
                push.Filters,
                push.Transform,
                push.ScopeBoxTransform,
                push.BeginIndex,
                endIndex,
                popIdx);
            filterEvents.Add(new FilterEvent(
                FilterEventKind.Pop,
                push.Bounds,
                push.Filters,
                push.Transform,
                push.ScopeBoxTransform,
                push.BeginIndex,
                endIndex,
                pushIdx));
        }

        public void Finish() {
            FlushCurrentBatch();
        }

        void EmitSolid(PaintRect bounds, LinearColor color, BorderRadii radii) {
            var key = new UIBatchKey(UIQuadBrush.Solid, isBordered: false, stencilRef: CurrentRefForBatch());
            EnsureBatch(key);
            var inst = BuildInstance(bounds, ApplyOpacity(color), radii, UIQuadBrush.Solid);
            AppendInstance(inst);
        }

        void EmitGradient(PaintRect bounds, PaintGradient gradient, BorderRadii radii, BackgroundTile? tile = null) {
            if (gradient == null) return;
            UIQuadBrush kind;
            Vector4 brushParams = default;
            switch (gradient) {
                case LinearGradient lin: {
                    kind = UIQuadBrush.LinearGradient;
                    double rad = lin.AngleDegrees * Math.PI / 180.0;
                    // BrushParams.w carries effective stopCount for linear
                    // gradients (no other field competes for that slot). The
                    // shader reads it to dispatch between the 2-stop fast
                    // path and the multi-stop walker. Set after we know how
                    // many stops we actually packed.
                    //
                    // CSS Images 3 §3.1 angle convention: 0deg points "to top",
                    // angle increases clockwise. In box-local UV (y-down) the
                    // direction vector is (sin θ, -cos θ): 0deg=(0,-1) up,
                    // 90deg=(1,0) right, 180deg=(0,1) down. Earlier code used
                    // (cos θ, sin θ) which is the math-convention right-at-0°,
                    // rotating every gradient 90° counter-clockwise — match3's
                    // `goal-fill { 90deg pink→gold }` painted top-to-bottom
                    // and the bomb tile's 135deg gradient ran along the wrong
                    // diagonal.
                    brushParams = new Vector4(
                        (float)kind,
                        (float)Math.Sin(rad),
                        -(float)Math.Cos(rad),
                        0f);
                    break;
                }
                case ConicGradient con: {
                    kind = UIQuadBrush.ConicGradient;
                    // Normalize center from gradient-tile pixels to the
                    // [0,1] uv space the shader samples in. Without this
                    // the shader saw center coordinates measured in
                    // pixels (e.g. 988 for a 1977-wide body) and `uv -
                    // center` produced nonsense — the conic gradient
                    // collapsed to its first/last colors.
                    double conNx = bounds.Width > 0 ? con.CenterX / bounds.Width : 0.5;
                    double conNy = bounds.Height > 0 ? con.CenterY / bounds.Height : 0.5;
                    // BrushParams.w is taken by FromAngleDegrees; the
                    // multi-stop count for conic lives in BorderStyles.x
                    // (slot 9), which is otherwise meaningful only when
                    // _BORDERED is set — gradient quads emit isBordered=false.
                    brushParams = new Vector4(
                        (float)kind,
                        (float)conNx,
                        (float)conNy,
                        (float)con.FromAngleDegrees);
                    break;
                }
                case RadialGradient rad: {
                    kind = UIQuadBrush.RadialGradient;
                    // Same normalization as conic. With this fix and the
                    // farthest-corner default radius landed in
                    // BackgroundResolver, off-center radials like
                    // `radial-gradient(ellipse at 20% 10%, …)` finally
                    // produce the corner-glow shape the spec describes.
                    double radNx = bounds.Width > 0 ? rad.CenterX / bounds.Width : 0.5;
                    double radNy = bounds.Height > 0 ? rad.CenterY / bounds.Height : 0.5;
                    // Clamp the source radius to an epsilon so a parser-
                    // produced zero radius (degenerate ellipse) doesn't
                    // pack as 0 in BrushParams.w. The shader's
                    // `max(radius, 1e-6)` rescues color sampling but the
                    // host-side packing already lost any meaningful
                    // ratio — clamping here keeps a tiny but coherent
                    // gradient instead of a single-pixel artifact.
                    double radRx = System.Math.Max(rad.RadiusX, 1e-6);
                    double radNrx = bounds.Width > 0 ? radRx / bounds.Width : 0.5;
                    brushParams = new Vector4(
                        (float)kind,
                        (float)radNx,
                        (float)radNy,
                        (float)radNrx);
                    break;
                }
                default:
                    EmitSolid(bounds, LinearColor.White, radii);
                    return;
            }
            var key = new UIBatchKey(kind, isBordered: false, stencilRef: CurrentRefForBatch());
            EnsureBatch(key);
            var inst = BuildInstance(bounds, ApplyOpacity(LinearColor.White), radii, kind);
            inst.BrushParams = brushParams;

            // GTILE-1: Pack background tile origin+size into BorderWidths (slot 4).
            // Gradient quads are never bordered (isBordered=false) so slot 4 is free.
            // Default = full-box, repeat → (0, 0, boxW, boxH). When the brush carries
            // a resolved BackgroundTile the origin/size are box-local pixel coordinates.
            // The shader maps pixel position into tile UV space before evaluating the
            // gradient, using BorderStyles.xy as no-repeat flags (safe for non-repeating
            // linear ≤4 stops; conic/radial have stopCount there so only full-box
            // default applies for those kinds — documented v1 scope).
            {
                double tileOriginX = tile.HasValue ? tile.Value.OriginX : 0.0;
                double tileOriginY = tile.HasValue ? tile.Value.OriginY : 0.0;
                double tileW = tile.HasValue ? tile.Value.TileWidth : bounds.Width;
                double tileH = tile.HasValue ? tile.Value.TileHeight : bounds.Height;
                inst.BorderWidths = new Vector4(
                    (float)tileOriginX, (float)tileOriginY,
                    (float)tileW, (float)tileH);
            }

            // ────────────────────────────────────────────────────────────
            // Multi-stop slot map (linear / conic, non-bordered, non-text):
            //   slot 2  Color            = stop[0].color
            //   slot 5  BorderColorTop   = stop[1].color (or stop[N-1] for 2-stop fast path)
            //   slot 6  BorderColorRight = stop[2].color (linear/conic only;
            //                              radial keeps RadiusY in .x)
            //   slot 7  BorderColorBot   = stop[3].color
            //   slot 8  BorderColorLeft  = (s0, s1, s2, s3) stop positions in [0,1]
            //   stopCount lives in BrushParams.w for linear (otherwise unused);
            //   for conic it lives in BorderStyles.x because BrushParams.w
            //   already carries FromAngleDegrees.
            //
            // Count==2 keeps the legacy layout exactly: stop[0] in Color,
            // stop[N-1] in BorderColorTop — so the fast-path shader function
            // still works untouched. Stops beyond 4 are clamped to 4 by
            // picking stop[0], two evenly spaced intermediates, and stop[N-1].
            // Radials stay 2-stop because slot 6 carries RadiusY for them.
            // ────────────────────────────────────────────────────────────
            var stops = gradient.Stops;
            int srcCount = stops.Count;

            // Gradient stops upload as PREMULTIPLIED colors. The shader's
            // Weva_PremulSrgbEncode (called on the final fragment output)
            // assumes its input is premul — unpremultiplying-then-sRGB-encode-
            // then-premultiply — so feeding it straight-alpha values causes
            // `unpremul = rgb / a` to overflow when `a < rgb` (e.g. white at
            // 40% alpha: (1,1,1,0.4) → unpremul = (2.5,2.5,2.5) → saturated
            // to (1,1,1) → output (0.4,0.4,0.4,0.4) by chance correct, but a
            // *colored* translucent stop (e.g. (1,0.5,0.8,0.3)) saturates the
            // same way, dropping color information and painting as pure white).
            // Premultiplying here also makes the shader's `lerp(start,end,t)`
            // sample in premultiplied space — CSS gradient spec behaviour. The
            // base inst.Color set by BuildInstance is already premul; the
            // gradient stop overrides must match.
            if (gradient is RadialGradient radial) {
                int radialPackCount = Math.Min(Math.Max(srcCount, 1), 4);
                int ri0 = 0;
                int ri1 = srcCount > 1 ? 1 : 0;
                int ri2 = srcCount > 2 ? 2 : ri1;
                int ri3 = srcCount > 3 ? 3 : ri2;
                if (srcCount > 4) {
                    radialPackCount = 4;
                    ri1 = (int)Math.Round((srcCount - 1) * (1.0 / 3.0));
                    ri2 = (int)Math.Round((srcCount - 1) * (2.0 / 3.0));
                    ri3 = srcCount - 1;
                    if (ri1 <= ri0) ri1 = ri0 + 1;
                    if (ri2 <= ri1) ri2 = ri1 + 1;
                    if (ri3 <= ri2) ri3 = ri2 + 1;
                }

                inst.Color = ColorVec(ApplyOpacity(stops[ri0].Color).Premultiplied());
                inst.BorderColorTop = radialPackCount == 1
                    ? inst.Color
                    : ColorVec(ApplyOpacity(stops[ri1].Color).Premultiplied());
                if (radialPackCount >= 3) {
                    inst.BorderColorBottom = ColorVec(ApplyOpacity(stops[ri2].Color).Premultiplied());
                }
                if (radialPackCount >= 4) {
                    inst.GradientStop4 = ColorVec(ApplyOpacity(stops[ri3].Color).Premultiplied());
                }
                double radRy = System.Math.Max(radial.RadiusY, 1e-6);
                double radNry = bounds.Height > 0 ? radRy / bounds.Height : 0.5;
                inst.BorderColorRight = new Vector4((float)radNry, 0f, 0f, 0f);
                // Pack the first/last stop positions into slot 8 so the
                // shader can remap the radial t to [p0..p1]. Without this,
                // `radial-gradient(... 0%, transparent 50%)` would stretch
                // its ramp over the full radius instead of fading by 50%.
                // NaN / unset positions fall back to (0, 1) — identical to
                // pre-position behavior, so 2-stop radials with default
                // positions render the same as before.
                // G13c: For repeating-radial-gradient, stop positions may be in
                // pixels (e.g. `30px`) rather than fractions [0,1]. The shader
                // derives the period from the packed positions; NormalizedPos
                // clamps px values to 1.0 (correct for non-repeating where the
                // gradient radius defines the full scale), but for repeating the
                // period must equal (px / RadiusX) so the rings repeat at the
                // declared pixel interval. Use RepeatingRadialNormalizedPos when
                // IsRepeating is true so "30px" packs as 30/RadiusX, not 1.0.
                double radRxForPeriod = System.Math.Max(radial.RadiusX, 1e-6);
                double rp0 = radial.IsRepeating
                    ? RepeatingRadialNormalizedPos(stops, ri0, 0.0, radRxForPeriod)
                    : NormalizedPos(stops, ri0, 0.0);
                double rp1 = radialPackCount >= 2
                    ? (radial.IsRepeating
                        ? RepeatingRadialNormalizedPos(stops, ri1, radialPackCount == 2 ? 1.0 : (1.0 / 3.0), radRxForPeriod)
                        : NormalizedPos(stops, ri1, radialPackCount == 2 ? 1.0 : (1.0 / 3.0)))
                    : rp0;
                double rp2 = radialPackCount >= 3
                    ? (radial.IsRepeating
                        ? RepeatingRadialNormalizedPos(stops, ri2, radialPackCount == 3 ? 1.0 : (2.0 / 3.0), radRxForPeriod)
                        : NormalizedPos(stops, ri2, radialPackCount == 3 ? 1.0 : (2.0 / 3.0)))
                    : rp1;
                double rp3 = radialPackCount >= 4
                    ? (radial.IsRepeating
                        ? RepeatingRadialNormalizedPos(stops, ri3, 1.0, radRxForPeriod)
                        : NormalizedPos(stops, ri3, 1.0))
                    : rp2;
                inst.BorderColorLeft = new Vector4((float)rp0, (float)rp1, (float)rp2, (float)rp3);
                // G13c: BorderStyles.w carries the IsRepeating flag (mirrors the
                // linear repeating flag layout) so the shader's radial branch can
                // dispatch the `frac(t)` wrap path for repeating-radial-gradient.
                float radialRepeatFlag = radial.IsRepeating ? 1f : 0f;
                inst.BorderStyles = new Vector4(EncodeGradientCountAndColorSpace(gradient, radialPackCount), 0f, 0f, radialRepeatFlag);
                AppendInstance(inst);
                return;
            }

            // Pick up to 4 source stops by default (radial, repeating-linear).
            // For conic and non-repeating linear, stops 5/6 land in the
            // extra-slot path (slots 14/15 + BorderStyles.y/.z for their
            // positions). The down-sampling for srcCount > 4 only applies to
            // gradient kinds that cap at 4 — radial (slot 6 carries RadiusY)
            // and repeating-linear (period encoding occupies BorderStyles.y).
            bool isConic = gradient is ConicGradient;
            bool isRepeatLinear = (gradient is LinearGradient lgCap) && lgCap.IsRepeating;

            // 2-stop conic with non-default positions (e.g.
            // `conic-gradient(black 270deg, transparent 270deg)` — a hard
            // 270° / 90° split): the legacy 2-stop fast path passes only
            // (c0, c1) to the shader and lerps across the full circle,
            // ignoring the stop positions entirely. Promote to a 3-stop
            // encoding so the multi-stop walker — which honours positions —
            // handles it. The promotion duplicates the first stop so the
            // c0..c1 segment stays constant, and the c1..c2 segment runs
            // the colour transition at the declared position(s). Covers
            // both the hard-transition case (p0 == p1) and the offset-2-
            // stop case (e.g. `conic(black 30%, white 70%)`).
            // G13c: Every 2-stop repeating-conic-gradient is promoted to a 3-stop
            // encoding so the wrap sampler (`Weva_ConicGradientRepeating`) always
            // receives valid stop positions and period information. Previously this
            // only fired when positions were non-default (stops[0]!=0 || stops[1]!=1),
            // which let a default-position repeating conic (period=1 turn) bypass
            // the promotion and land in the non-repeating 2-stop fast path — correct
            // visually (period==1 == single full sweep) but only by accident. The
            // shader now unconditionally routes repeating conics to the wrap sampler,
            // so positions MUST be valid for all cases, including period==1.
            //
            // Non-repeating 2-stop conics with non-default positions are also promoted
            // here (same as before: hard-transition and offset-2-stop cases).
            if (isConic && srcCount == 2
                && (((ConicGradient)gradient).IsRepeating
                    || stops[0].Position != 0.0 || stops[1].Position != 1.0)) {
                var c0 = ColorVec(ApplyOpacity(stops[0].Color).Premultiplied());
                var c1 = ColorVec(ApplyOpacity(stops[1].Color).Premultiplied());
                inst.Color = c0;
                inst.BorderColorTop = c0;
                inst.BorderColorRight = c1;
                float p0 = (float)NormalizedPos(stops, 0, 0.0);
                float p1 = (float)NormalizedPos(stops, 1, 1.0);
                // Pack (0, p0, p1, p1) so the 3-stop walker sees:
                //   t <= p0  → c0      (initial colour, full coverage)
                //   p0..p1   → lerp(c0, c1)  (transition; degenerates to a
                //                              hard edge when p0 == p1)
                //   t >= p1  → c1      (terminal colour)
                // For the default-position repeating case (p0=0, p1=1), period=1:
                // the wrap sampler produces one full-circle red→blue sweep per
                // revolution — equivalent to the non-repeating behaviour.
                inst.BorderColorLeft = new Vector4(0f, p0, p1, p1);
                // G13c: BorderStyles.w carries the IsRepeating flag for conic
                // gradients (same layout as radial/linear). The promoted 3-stop
                // encoding still needs the flag so a repeating-conic-gradient
                // hits the shader's wrap branch.
                float conicPromotedRepeatFlag = ((ConicGradient)gradient).IsRepeating ? 1f : 0f;
                inst.BorderStyles = new Vector4(EncodeGradientCountAndColorSpace(gradient, 3), 0f, 0f, conicPromotedRepeatFlag);
                AppendInstance(inst);
                return;
            }

            // Non-repeating 2-stop linear gradients normally use the cheap
            // shader path that lerps over the full box. That is only correct
            // for default stop positions (0, 1). CSS allows explicit offsets
            // such as `linear-gradient(180deg, white, transparent 48%)`;
            // promote those to the multi-stop walker by duplicating the last
            // stop so the ramp ends at the declared position and stays there.
            if (!isConic && !isRepeatLinear && gradient is LinearGradient && srcCount == 2) {
                double lp0 = NormalizedPos(stops, 0, 0.0);
                double lp1 = NormalizedPos(stops, 1, 1.0);
                if (Math.Abs(lp0) > 1e-6 || Math.Abs(lp1 - 1.0) > 1e-6) {
                    var c0 = ColorVec(ApplyOpacity(stops[0].Color).Premultiplied());
                    var c1 = ColorVec(ApplyOpacity(stops[1].Color).Premultiplied());
                    inst.Color = c0;
                    inst.BorderColorTop = c1;
                    inst.BorderColorRight = c1;
                    inst.BorderColorLeft = new Vector4((float)lp0, (float)lp1, (float)lp1, (float)lp1);
                    inst.BrushParams = new Vector4(
                        inst.BrushParams.x, inst.BrushParams.y, inst.BrushParams.z,
                        EncodeGradientCountAndColorSpace(gradient, 3));
                    AppendInstance(inst);
                    return;
                }
            }
            int maxPack = (isConic || (gradient is LinearGradient && !isRepeatLinear)) ? 6 : 4;
            int packCount = Math.Min(srcCount, maxPack);
            int i0 = 0;
            int i1 = srcCount > 1 ? 1 : 0;
            int i2 = srcCount > 2 ? 2 : i1;
            int i3 = srcCount > 3 ? 3 : i2;
            int i4 = srcCount > 4 ? 4 : i3;
            int i5 = srcCount > 5 ? 5 : i4;
            if (srcCount > maxPack) {
                packCount = maxPack;
                if (maxPack == 4) {
                    i0 = 0;
                    i1 = (int)Math.Round((srcCount - 1) * (1.0 / 3.0));
                    i2 = (int)Math.Round((srcCount - 1) * (2.0 / 3.0));
                    i3 = srcCount - 1;
                    if (i1 == i0) i1 = i0 + 1;
                    if (i2 <= i1) i2 = i1 + 1;
                    if (i3 <= i2) i3 = i2 + 1;
                } else {
                    // maxPack == 6: pick 4 evenly-spaced intermediates between
                    // i0=0 and i5=last so the perceived stop distribution is
                    // preserved when downsampling a 7+ stop gradient.
                    i0 = 0;
                    i1 = (int)Math.Round((srcCount - 1) * (1.0 / 5.0));
                    i2 = (int)Math.Round((srcCount - 1) * (2.0 / 5.0));
                    i3 = (int)Math.Round((srcCount - 1) * (3.0 / 5.0));
                    i4 = (int)Math.Round((srcCount - 1) * (4.0 / 5.0));
                    i5 = srcCount - 1;
                    if (i1 <= i0) i1 = i0 + 1;
                    if (i2 <= i1) i2 = i1 + 1;
                    if (i3 <= i2) i3 = i2 + 1;
                    if (i4 <= i3) i4 = i3 + 1;
                    if (i5 <= i4) i5 = i4 + 1;
                }
            }

            // All stop colors upload as PREMULTIPLIED — see the comment on the
            // radial branch above for why.
            if (packCount > 0) inst.Color = ColorVec(ApplyOpacity(stops[i0].Color).Premultiplied());
            if (packCount == 1) {
                inst.BorderColorTop = inst.Color;
            } else if (packCount == 2) {
                // Legacy 2-stop layout: BorderColorTop holds the LAST stop so
                // the existing fast-path shader function continues to work.
                inst.BorderColorTop = ColorVec(ApplyOpacity(stops[i3].Color).Premultiplied());
            } else if (packCount == 3) {
                inst.BorderColorTop = ColorVec(ApplyOpacity(stops[i1].Color).Premultiplied());
                inst.BorderColorRight = ColorVec(ApplyOpacity(stops[i2].Color).Premultiplied());
            } else if (packCount == 4) {
                inst.BorderColorTop = ColorVec(ApplyOpacity(stops[i1].Color).Premultiplied());
                inst.BorderColorRight = ColorVec(ApplyOpacity(stops[i2].Color).Premultiplied());
                inst.BorderColorBottom = ColorVec(ApplyOpacity(stops[i3].Color).Premultiplied());
            } else if (packCount == 5) {
                inst.BorderColorTop = ColorVec(ApplyOpacity(stops[i1].Color).Premultiplied());
                inst.BorderColorRight = ColorVec(ApplyOpacity(stops[i2].Color).Premultiplied());
                inst.BorderColorBottom = ColorVec(ApplyOpacity(stops[i3].Color).Premultiplied());
                inst.GradientStop4 = ColorVec(ApplyOpacity(stops[i4].Color).Premultiplied());
            } else { // packCount == 6
                inst.BorderColorTop = ColorVec(ApplyOpacity(stops[i1].Color).Premultiplied());
                inst.BorderColorRight = ColorVec(ApplyOpacity(stops[i2].Color).Premultiplied());
                inst.BorderColorBottom = ColorVec(ApplyOpacity(stops[i3].Color).Premultiplied());
                inst.GradientStop4 = ColorVec(ApplyOpacity(stops[i4].Color).Premultiplied());
                inst.GradientStop5 = ColorVec(ApplyOpacity(stops[i5].Color).Premultiplied());
            }

            // Pack stop positions into slot 8 (BorderColorLeft) when packCount>=3.
            // BackgroundResolver.NormalizeStopPositions guarantees positions
            // are in [0,1] monotonic with stop[0]=0 and stop[N-1]=1 for the
            // common (non-repeating, % or implicit) case. For
            // `repeating-linear-gradient(...)` with pixel positions the
            // resolver leaves the raw px values intact; the repeating branch
            // below normalizes them by axis length so the shader sees a
            // proper [0,1] ramp + a period in the same space.
            bool repeatLinear = (gradient is LinearGradient lg) && lg.IsRepeating;
            double axisLengthPx = 0.0;
            // Whether this repeating gradient is positioned in PIXELS. The
            // resolver stores px stops as raw px (e.g. 1, 80) and % stops as
            // [0,1]; once ANY stop exceeds 1.0 the gradient is pixel-spaced and
            // EVERY stop must be normalized by the axis length — including a
            // thin `1px` stop, whose value (1) is NOT > 1.0 and was previously
            // misclassified as the fraction 100%, collapsing the period (e.g.
            // map.css `.canvas-grid` repeating-linear-gradient(... 1px, ... 80px)
            // washed its 1px grid lines across the whole line → invisible).
            bool gradientPixelStops = false;
            if (repeatLinear) {
                // Gradient line length per CSS Images 3 §3.1: project the
                // box's diagonal onto the gradient axis. With angle measured
                // from "to top" clockwise, this is |W·sin| + |H·cos|.
                double rad = ((LinearGradient)gradient).AngleDegrees * Math.PI / 180.0;
                axisLengthPx = Math.Abs(bounds.Width * Math.Sin(rad))
                             + Math.Abs(bounds.Height * Math.Cos(rad));
                if (axisLengthPx < 1e-6) axisLengthPx = 1.0;
                for (int k = 0; k < stops.Count; k++) {
                    if (stops[k].Position > 1.0) { gradientPixelStops = true; break; }
                }
            }
            if (packCount >= 3) {
                if (repeatLinear) {
                    float p0 = (float)RepeatingNormalizedPos(stops, i0, 0.0, axisLengthPx, gradientPixelStops);
                    float p1 = (float)RepeatingNormalizedPos(stops, i1, packCount == 3 ? 0.5 : (1.0 / 3.0), axisLengthPx, gradientPixelStops);
                    float p2 = (float)RepeatingNormalizedPos(stops, i2, packCount == 3 ? 1.0 : (2.0 / 3.0), axisLengthPx, gradientPixelStops);
                    float p3 = packCount == 4 ? (float)RepeatingNormalizedPos(stops, i3, 1.0, axisLengthPx, gradientPixelStops) : p2;
                    inst.BorderColorLeft = new Vector4(p0, p1, p2, p3);
                } else if (packCount <= 4) {
                    float p0 = (float)NormalizedPos(stops, i0, 0.0);
                    float p1 = (float)NormalizedPos(stops, i1, packCount == 3 ? 0.5 : (1.0 / 3.0));
                    float p2 = (float)NormalizedPos(stops, i2, packCount == 3 ? 1.0 : (2.0 / 3.0));
                    float p3 = packCount == 4 ? (float)NormalizedPos(stops, i3, 1.0) : p2;
                    inst.BorderColorLeft = new Vector4(p0, p1, p2, p3);
                } else {
                    // Conic or non-repeating linear with 5 or 6 stops. Pack
                    // p0..p3 into slot 8 (BorderColorLeft) the same way the
                    // 4-stop path does; p4 (and p5 when packCount==6) ride
                    // in slot 9 — see BorderStyles handling further down.
                    // Default position spacing for packCount==5 is
                    // (0, 0.25, 0.5, 0.75, 1.0); for packCount==6 it's
                    // (0, 0.2, 0.4, 0.6, 0.8, 1.0).
                    double[] defaults = packCount == 5 ? DefaultStops5 : DefaultStops6;
                    float p0 = (float)NormalizedPos(stops, i0, defaults[0]);
                    float p1 = (float)NormalizedPos(stops, i1, defaults[1]);
                    float p2 = (float)NormalizedPos(stops, i2, defaults[2]);
                    float p3 = (float)NormalizedPos(stops, i3, defaults[3]);
                    inst.BorderColorLeft = new Vector4(p0, p1, p2, p3);
                }
            } else if (repeatLinear && packCount == 2) {
                // 2-stop repeating: the shader takes the multi-stop walker
                // anyway (the legacy fast path can't honor frac()), so feed
                // the first and last stop positions into slot 8 the same
                // way the 3/4-stop branch above does.
                float p0 = (float)RepeatingNormalizedPos(stops, i0, 0.0, axisLengthPx, gradientPixelStops);
                float p3 = (float)RepeatingNormalizedPos(stops, i3, 1.0, axisLengthPx, gradientPixelStops);
                inst.BorderColorLeft = new Vector4(p0, p3, p3, p3);
            }

            // Stash effective stop count where the shader will read it.
            int effectiveCount = packCount;
            if (gradient is LinearGradient lin2) {
                inst.BrushParams = new Vector4(
                    inst.BrushParams.x, inst.BrushParams.y, inst.BrushParams.z,
                    EncodeGradientCountAndColorSpace(gradient, effectiveCount));
                if (lin2.IsRepeating) {
                    // Slot 9 (BorderStyles) is unused for non-bordered linear
                    // gradient quads, so we co-opt it to carry the repeating
                    // flag (.w = 1) and the ramp period (.y) along the axis
                    // in [0,1] space. The shader's repeating branch reads
                    // these to drive a frac(t/period) ramp.
                    //
                    // The flag lives in .w (not .x) so it can't collide with
                    // the non-repeating 5/6-stop encoding below, which puts
                    // p4 in .y and p5 in .z. Repeating linear caps at 4
                    // stops today so .y / .z continue to carry period only.
                    //
                    // Period == largest stop position after normalization.
                    // For pixel-positioned stops we just divided by the axis
                    // length, so the largest normalized position lands at
                    // (totalPx / axisLengthPx). For percent-positioned stops
                    // the resolver already produced [0,1] so the largest
                    // stop's position is the period directly.
                    double period = 1.0;
                    if (srcCount > 0) {
                        double maxPos = 0.0;
                        bool anyPx = false;
                        for (int k = 0; k < srcCount; k++) {
                            double p = stops[k].Position;
                            if (double.IsNaN(p)) continue;
                            if (p > 1.0) anyPx = true;
                            if (p > maxPos) maxPos = p;
                        }
                        period = anyPx ? (maxPos / axisLengthPx) : maxPos;
                        if (period <= 1e-6) period = 1.0;
                    }
                    inst.BorderStyles = new Vector4(0f, (float)period, 0f, 1f);
                } else if (effectiveCount >= 5) {
                    // Non-repeating 5/6-stop linear: slot 9 mirrors the conic
                    // encoding — (stopCount, p4, p5, 0). stopCount is also in
                    // BrushParams.w for the shader's fast-path dispatch; we
                    // duplicate it in .x so future readers can self-describe
                    // the slot without cross-referencing brushParams.
                    float p4 = 0f, p5 = 0f;
                    if (effectiveCount == 5) {
                        p4 = (float)NormalizedPos(stops, i4, 1.0);
                        p5 = p4;
                    } else { // effectiveCount == 6
                        p4 = (float)NormalizedPos(stops, i4, 0.8);
                        p5 = (float)NormalizedPos(stops, i5, 1.0);
                    }
                    inst.BorderStyles = new Vector4(EncodeGradientCountAndColorSpace(gradient, effectiveCount), p4, p5, 0f);
                }
            } else if (gradient is ConicGradient conicMs) {
                // Slot 9 carries (stopCount, p4, p5, isRepeating) for conic.
                // p4/p5 are only meaningful when effectiveCount >= 5/6
                // respectively; for ≤4 stops they're zero and the shader's
                // multi-stop walker never reads them. Default fallbacks match
                // the BorderColorLeft defaults above so a 5-stop gradient
                // with no explicit positions sits at (0,.25,.5,.75,1.0) and
                // a 6-stop gradient sits at (0,.2,.4,.6,.8,1.0).
                //
                // G13c: BorderStyles.w carries the IsRepeating flag (same
                // layout as the linear repeating flag and the radial flag
                // above). The shader's conic branch reads it to dispatch the
                // wrap path for repeating-conic-gradient(...).
                float p4 = 0f, p5 = 0f;
                if (effectiveCount == 5) {
                    p4 = (float)NormalizedPos(stops, i4, 1.0);
                    p5 = p4;
                } else if (effectiveCount == 6) {
                    p4 = (float)NormalizedPos(stops, i4, 0.8);
                    p5 = (float)NormalizedPos(stops, i5, 1.0);
                }
                float conicRepeatFlag = conicMs.IsRepeating ? 1f : 0f;
                inst.BorderStyles = new Vector4(EncodeGradientCountAndColorSpace(gradient, effectiveCount), p4, p5, conicRepeatFlag);
            }

            // GTILE-1: For non-repeating linear gradients (BorderStyles.xy is otherwise
            // zero), pack the tile no-repeat flags so the shader can clip outside the
            // tile rect. For conic/radial, BorderStyles.x already carries the encoded
            // stop count — tiling those kinds is a v1 follow-on (full-box default above).
            if (tile.HasValue && gradient is LinearGradient linTile && !linTile.IsRepeating) {
                var t = tile.Value;
                float noRepX = t.RepeatX == BackgroundRepeatMode.NoRepeat ? 1f : 0f;
                float noRepY = t.RepeatY == BackgroundRepeatMode.NoRepeat ? 1f : 0f;
                float gapX = (float)t.GapX;
                float gapY = (float)t.GapY;
                // Only overwrite BorderStyles when the gradient branch left it at zero
                // (effectiveCount <= 4 for non-repeating linear keeps .x=0 above).
                // For 5/6-stop linear, BorderStyles.x was set to stopCount — skip to
                // avoid destroying it; tiling 5/6-stop linear is a follow-on.
                if (effectiveCount <= 4) {
                    inst.BorderStyles = new Vector4(noRepX, noRepY, gapX, gapY);
                }
            }

            AppendInstance(inst);
        }

        // Repeating-linear position normalization: treats positions > 1 as
        // pixel values (the BackgroundResolver leaves them in px for the
        // double-position / px-stop case) and divides by the axis length.
        // Fractional positions (<= 1) pass through unchanged so percent-
        // positioned repeating gradients also work.
        static double RepeatingNormalizedPos(System.Collections.Generic.IReadOnlyList<Weva.Paint.GradientStop> stops, int index, double fallback, double axisLengthPx, bool pixelStops) {
            if (index < 0 || index >= stops.Count) return fallback;
            double p = stops[index].Position;
            if (double.IsNaN(p) || p < 0.0) return fallback;
            // When the gradient is pixel-spaced, EVERY stop (including a thin
            // <=1px one) is a pixel value to divide by the axis length. The old
            // per-stop `p > 1.0` gate misread a 1px stop as the fraction 100%.
            if (pixelStops) {
                double n = p / axisLengthPx;
                return n < 0.0 ? 0.0 : n;
            }
            return p;
        }

        // G13c: repeating-radial-gradient position normalization. Radial stop
        // positions may be in pixels (BackgroundResolver leaves them as raw px,
        // e.g. 30.0 for `30px`) or as fractions [0,1] (for `30%`). The shader
        // derives the tiling period from the packed positions; NormalizedPos
        // clamps px values to 1.0 (correct for non-repeating where RadiusX == the
        // full normalisation unit), but for repeating the period must equal
        // (px / RadiusX) so the rings repeat at the declared pixel interval.
        // `radiusX` is the gradient circle radius in pixels (already clamped to
        // 1e-6 by the caller).
        static double RepeatingRadialNormalizedPos(System.Collections.Generic.IReadOnlyList<Weva.Paint.GradientStop> stops, int index, double fallback, double radiusX) {
            if (index < 0 || index >= stops.Count) return fallback;
            double p = stops[index].Position;
            if (double.IsNaN(p) || p < 0.0) return fallback;
            // Positions > 1 are pixel values (BackgroundResolver stores px stops
            // as raw numbers, e.g. 30 for `30px`). Divide by RadiusX to get the
            // normalised [0,∞) offset so the shader period = 30px/RadiusX.
            // Fractional values (% stops, already in [0,1]) pass through unchanged.
            if (p > 1.0) return p / radiusX;
            return p;
        }

        // Returns a normalized [0,1] position for the requested stop. NaN /
        // out-of-range positions fall back to the supplied default (the
        // evenly-spaced position the stop would land at if no resolver had
        // touched it). Protects the shader's monotone-walk from bad data.
        static double NormalizedPos(System.Collections.Generic.IReadOnlyList<Weva.Paint.GradientStop> stops, int index, double fallback) {
            if (index < 0 || index >= stops.Count) return fallback;
            double p = stops[index].Position;
            if (double.IsNaN(p) || p < 0.0) return fallback;
            if (p > 1.0) return 1.0;
            return p;
        }

        UIQuadInstance BuildInstance(PaintRect bounds, LinearColor color, BorderRadii radii, UIQuadBrush brush) {
            float halfW = (float)(bounds.Width * 0.5);
            float halfH = (float)(bounds.Height * 0.5);
            float cx = (float)(bounds.X + halfW);
            float cy = (float)(bounds.Y + halfH);
            var inst = new UIQuadInstance {
                PosSize = new Vector4(cx, cy, halfW, halfH),
                Radii = new Vector4(
                    (float)radii.TopLeft.XRadius,
                    (float)radii.TopRight.XRadius,
                    (float)radii.BottomRight.XRadius,
                    (float)radii.BottomLeft.XRadius),
                // Vertical corner radii for elliptical border-radius. Equals
                // Radii for circular corners; the shader collapses to the
                // circular SDF when they match (CSS B&B L3 §5).
                RadiiY = new Vector4(
                    (float)radii.TopLeft.YRadius,
                    (float)radii.TopRight.YRadius,
                    (float)radii.BottomRight.YRadius,
                    (float)radii.BottomLeft.YRadius),
                Color = ColorVec(color.Premultiplied()),
                BrushParams = new Vector4((float)brush, 0f, 0f, 0f),
                BorderWidths = Vector4.zero,
                BorderColorTop = Vector4.zero,
                BorderColorRight = Vector4.zero,
                BorderColorBottom = Vector4.zero,
                BorderColorLeft = Vector4.zero,
                BorderStyles = Vector4.zero,
                // TransformRow0.z — blend mode ordinal (CSS Compositing 1 §6
                //   mix-blend-mode OR §9 background-blend-mode; shared slot).
                // TransformRow0.w — element-local flag:
                //   0 = page-backdrop blend (mix-blend-mode, §6): the shader
                //       samples _WevaBackdrop as the destination color.
                //   1 = element-local blend (background-blend-mode, §9): the
                //       shader reads the baked base color from Row1/Row2 and
                //       never touches _WevaBackdrop.
                // TransformRow1.zw — base color R, G (linear, unpremultiplied).
                // TransformRow2.zw — base color B, A (linear, unpremultiplied).
                // For page-backdrop entries the spare channels are zero.
                TransformRow0 = new Vector4(currentTransform.A, currentTransform.B,
                    (float)currentBlendMode, currentBlendElementLocal ? 1f : 0f),
                TransformRow1 = new Vector4(currentTransform.C, currentTransform.D,
                    currentBlendBaseColor.R, currentBlendBaseColor.G),
                TransformRow2 = new Vector4(currentTransform.Tx, currentTransform.Ty,
                    currentBlendBaseColor.B, currentBlendBaseColor.A),
                ClipRect = CurrentClipRect(),
                GradientStop4 = Vector4.zero,
                GradientStop5 = Vector4.zero,
                ClipShape0 = Vector4.zero,
                ClipShape1 = Vector4.zero,
                ClipShape2 = Vector4.zero,
                ClipShape3 = Vector4.zero,
                ClipShape4 = Vector4.zero,
                MaskParams0 = Vector4.zero,
                MaskBounds = Vector4.zero,
                MaskTile = Vector4.zero,
                MaskParams1 = Vector4.zero,
                MaskColor0 = Vector4.zero,
                MaskColor1 = Vector4.zero,
                MaskColor2 = Vector4.zero,
                MaskColor3 = Vector4.zero,
                MaskPositions = Vector4.zero
            };
            EncodeClipPath(CurrentClipPath(), ref inst);
            EncodeMask(CurrentMask(), currentTransform, ref inst);
            return inst;
        }

        static float GradientColorSpaceSign(PaintGradient gradient) {
            // Legacy 2-state sign-bit: -1 for sRGB (also used for Oklab in the
            // 3-state encoding below because both are non-linear-RGB), +1 for
            // linear-RGB. Callers should prefer EncodeGradientCountAndColorSpace
            // when packing the per-instance count value so the Oklab fractional
            // offset is folded in correctly.
            return gradient != null
                   && (gradient.InterpolationSpace == CssColorSpace.Srgb
                       || gradient.InterpolationSpace == CssColorSpace.Oklab)
                ? -1f : 1f;
        }

        // G1b — 3-state colorspace encoding packed into BrushParams.w (linear)
        // or BorderStyles.x (conic / radial), riding alongside the gradient
        // stop count. Encoding (decoded in UIShaderLib.hlsl's gradient branches):
        //   linear-RGB : +count          (positive, integer)
        //   sRGB       : -count          (negative, integer; legacy default)
        //   Oklab      : -(count + 0.25) (negative, fractional offset)
        // The 0.25 fractional offset is invisible to the existing magnitude
        // readout `(int)(abs(val) + 0.5)` so the shader still recovers a
        // correct integer stop count; the oklab branch is detected via
        // `frac(abs(val)) > 0.1`. Three states fit in one float channel with
        // no extra slot allocation.
        static float EncodeGradientCountAndColorSpace(PaintGradient gradient, int count) {
            float signed = (float)count * GradientColorSpaceSign(gradient);
            if (gradient != null && gradient.InterpolationSpace == CssColorSpace.Oklab) {
                // Sign is already negative (Oklab shares the sRGB sign). Push
                // the magnitude up by 0.25 so `frac(abs(val))` flags Oklab on
                // the GPU side.
                signed -= 0.25f;
            }
            return signed;
        }

        static void EncodeClipPath(ClipPathShape shape, ref UIQuadInstance inst) {
            if (shape == null) return;
            switch (shape) {
                case InsetClipPathShape inset: {
                    var r = inset.Rect;
                    inst.ClipShape0 = new Vector4((float)ClipPathShapeKind.Inset, (float)r.X, (float)r.Y, (float)r.Right);
                    inst.ClipShape1 = new Vector4((float)r.Bottom,
                        (float)inset.Radii.TopLeft.XRadius,
                        (float)inset.Radii.TopRight.XRadius,
                        (float)inset.Radii.BottomRight.XRadius);
                    inst.ClipShape2 = new Vector4((float)inset.Radii.BottomLeft.XRadius, 0f, 0f, 0f);
                    return;
                }
                case CircleClipPathShape circle:
                    inst.ClipShape0 = new Vector4((float)ClipPathShapeKind.Circle,
                        (float)circle.CenterX, (float)circle.CenterY, (float)circle.Radius);
                    return;
                case EllipseClipPathShape ellipse:
                    inst.ClipShape0 = new Vector4((float)ClipPathShapeKind.Ellipse,
                        (float)ellipse.CenterX, (float)ellipse.CenterY, (float)ellipse.RadiusX);
                    inst.ClipShape1 = new Vector4((float)ellipse.RadiusY, 0f, 0f, 0f);
                    return;
                case PolygonClipPathShape polygon:
                    // ClipShape0.z carries the CSS fill rule (0 = nonzero, 1 = evenodd) so
                    // the URP polygon-clip fragment branch can dispatch between winding-
                    // count and parity-count algorithms. CPU `PolygonClipPathShape.Contains`
                    // already honours `FillRule`; without this channel the GPU silently
                    // diverges for self-intersecting polygons (e.g. star shapes with the
                    // default `nonzero` rule paint as if `evenodd`). See tracker G5b.
                    inst.ClipShape0 = new Vector4((float)ClipPathShapeKind.Polygon,
                        Math.Min(8, polygon.Points.Length), (float)polygon.FillRule, 0f);
                    PackPointPair(polygon, 0, ref inst.ClipShape1);
                    PackPointPair(polygon, 2, ref inst.ClipShape2);
                    PackPointPair(polygon, 4, ref inst.ClipShape3);
                    PackPointPair(polygon, 6, ref inst.ClipShape4);
                    return;
                case PathClipPathShape pathClip: {
                    // B16 (PHASE 2): path() GPU clip via rasterized mask texture.
                    // The mask path (coverage image in synthetic mask layer, kind=5) handles
                    // the real shape clip on the GPU. Here we encode the bounding box as a
                    // cheap Inset scissor so the GPU can pre-reject fragments that are
                    // definitively outside the path's AABB — correct conservatively AND
                    // provides the AABB scissor for shadow/overflow logic.
                    var pb = pathClip.Bounds;
                    inst.ClipShape0 = new Vector4((float)ClipPathShapeKind.Inset,
                        (float)pb.X, (float)pb.Y, (float)pb.Right);
                    inst.ClipShape1 = new Vector4((float)pb.Bottom, 0f, 0f, 0f);
                    return;
                }
            }
        }

        static void PackPointPair(PolygonClipPathShape polygon, int start, ref Vector4 dst) {
            dst = Vector4.zero;
            if (start < polygon.Points.Length) {
                dst.x = (float)polygon.Points[start].X;
                dst.y = (float)polygon.Points[start].Y;
            }
            int next = start + 1;
            if (next < polygon.Points.Length) {
                dst.z = (float)polygon.Points[next].X;
                dst.w = (float)polygon.Points[next].Y;
            }
        }

        static void EncodeMask(MaskDefinition mask, Transform2D transform, ref UIQuadInstance inst) {
            if (mask == null || mask.IsEmpty) return;
            int count = Math.Min(mask.Count, MaskDefinition.MaxRenderedLayers);
            for (int i = 0; i < count; i++) {
                EncodeMaskLayer(mask.Layers[i], transform, i, count, ref inst);
            }
        }

        // The mask shader compares the POST-transform fragment position
        // (`worldPos` = the element's vertices after the active CSS transform)
        // against the mask bounds, but those bounds arrive in pre-transform
        // layout space. The base-fill SDF sidesteps this by working in the
        // quad's UV space (transform-independent), so a `transform`ed element
        // whose mask bounds stay in layout space gets its mask region offset
        // from the painted pixels — e.g. the combat-HUD target frame with
        // `transform: translateX(-50%)` had the left half of its mask-image
        // edge-fade fall entirely OUTSIDE the (un-shifted) mask bounds, hard-
        // clipping ~50% of the panel. Mirror what the clip path already does
        // (it is handed currentTransform) by mapping the mask bounds + tile
        // into the same transformed space as worldPos. Translate + scale are
        // handled exactly; rotation/skew (B/C) fall back to the axis-aligned
        // approximation the mask's AABB in/out test already assumes.
        static Vector4 TransformMaskBounds(Vector4 r, Transform2D t) {
            var (ox, oy) = t.Apply(r.x, r.y);
            return new Vector4((float)ox, (float)oy, r.z * t.A, r.w * t.D);
        }

        // Tile origin is an offset relative to the bounds (not an absolute
        // point), so only the scale components apply — the translation is
        // already carried by the transformed bounds origin above.
        static Vector4 TransformMaskTile(Vector4 tile, Transform2D t) {
            return new Vector4(tile.x * t.A, tile.y * t.D, tile.z * t.A, tile.w * t.D);
        }

        static void EncodeMaskLayer(MaskLayer mask, Transform2D transform, int layerIndex, int renderedCount, ref UIQuadInstance inst) {
            if (mask == null) return;
            int kind = 0;
            if (mask.Brush != null) {
                switch (mask.Brush.Kind) {
                    case BrushKind.SolidColor: kind = 1; break;
                    case BrushKind.Gradient:
                        if (mask.Brush.GradientValue is LinearGradient) kind = 2;
                        else if (mask.Brush.GradientValue is RadialGradient) kind = 3;
                        else if (mask.Brush.GradientValue is ConicGradient) kind = 4;
                        break;
                    case BrushKind.Image: kind = 5; break;
                }
            }

            var bounds = mask.Bounds;
            int packedModeCompositeCount = (int)mask.Mode + ((int)mask.Composite * 4);
            if (layerIndex == 0) packedModeCompositeCount += renderedCount * 16;
            // .z packs repeatX + repeatY*4 (each a BackgroundRepeatMode 0..3) so .w
            // is free to carry the gradient stop count. A radial mask consumes all
            // four params1 slots for cx/cy/rx/ry, leaving no room for the count there
            // — reading it from params1.z (radiusX) gave count==1, so the radial
            // collapsed to a single solid stop (full reveal, no dot). params0.w is
            // set to the real stop count for gradient kinds below; 0 for solid/image.
            int repX = mask.Tile.HasValue ? (int)mask.Tile.Value.RepeatX : (int)BackgroundRepeatMode.NoRepeat;
            int repY = mask.Tile.HasValue ? (int)mask.Tile.Value.RepeatY : (int)BackgroundRepeatMode.NoRepeat;
            var maskParams0 = new Vector4(kind, packedModeCompositeCount, repX + repY * 4, 0f);
            var maskBounds = TransformMaskBounds(
                new Vector4((float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height),
                transform);

            BackgroundTile tile = mask.Tile ?? new BackgroundTile(bounds.Width, bounds.Height, 0, 0,
                BackgroundRepeatMode.NoRepeat, BackgroundRepeatMode.NoRepeat);
            var maskTile = TransformMaskTile(
                new Vector4(
                    (float)tile.OriginX,
                    (float)tile.OriginY,
                    (float)tile.TileWidth,
                    (float)tile.TileHeight),
                transform);
            var maskParams1 = Vector4.zero;
            var maskColor0 = Vector4.zero;
            var maskColor1 = Vector4.zero;
            var maskColor2 = Vector4.zero;
            var maskColor3 = Vector4.zero;
            var maskPositions = Vector4.zero;

            if (kind == 0) {
                StoreMaskLayer(ref inst, layerIndex, maskParams0, maskBounds, maskTile,
                    maskParams1, maskColor0, maskColor1, maskColor2, maskColor3, maskPositions);
                return;
            }

            if (kind == 1) {
                maskColor0 = ColorVec(mask.Brush.Color);
                maskPositions = new Vector4(0f, 1f, 1f, 1f);
                StoreMaskLayer(ref inst, layerIndex, maskParams0, maskBounds, maskTile,
                    maskParams1, maskColor0, maskColor1, maskColor2, maskColor3, maskPositions);
                return;
            }

            if (kind == 5) {
                maskColor0 = new Vector4(1f, 1f, 1f, 1f);
                maskPositions = new Vector4(0f, 1f, 1f, 1f);
                StoreMaskLayer(ref inst, layerIndex, maskParams0, maskBounds, maskTile,
                    maskParams1, maskColor0, maskColor1, maskColor2, maskColor3, maskPositions);
                return;
            }

            var gradient = mask.Brush.GradientValue;
            var stops = gradient.Stops;
            int srcCount = stops.Count;
            int count = Math.Min(Math.Max(srcCount, 1), 4);
            int i0 = 0;
            int i1 = srcCount > 1 ? 1 : 0;
            int i2 = srcCount > 2 ? 2 : i1;
            int i3 = srcCount > 3 ? srcCount - 1 : i2;
            if (srcCount > 4) {
                i1 = (int)Math.Round((srcCount - 1) * (1.0 / 3.0));
                i2 = (int)Math.Round((srcCount - 1) * (2.0 / 3.0));
                i3 = srcCount - 1;
            }

            maskColor0 = srcCount > 0 ? ColorVec(stops[i0].Color) : new Vector4(1f, 1f, 1f, 1f);
            maskColor1 = srcCount > 1 ? ColorVec(stops[i1].Color) : maskColor0;
            maskColor2 = srcCount > 2 ? ColorVec(stops[i2].Color) : maskColor1;
            maskColor3 = srcCount > 3 ? ColorVec(stops[i3].Color) : maskColor2;
            maskPositions = new Vector4(
                (float)NormalizedPos(stops, i0, 0.0),
                (float)NormalizedPos(stops, i1, count <= 2 ? 1.0 : 1.0 / 3.0),
                (float)NormalizedPos(stops, i2, count <= 3 ? 1.0 : 2.0 / 3.0),
                (float)NormalizedPos(stops, i3, 1.0));

            if (gradient is LinearGradient lin) {
                double rad = lin.AngleDegrees * Math.PI / 180.0;
                maskParams1 = new Vector4(
                    (float)Math.Sin(rad),
                    -(float)Math.Cos(rad),
                    count,
                    lin.IsRepeating ? 1f : 0f);
            } else if (gradient is RadialGradient rad) {
                double tw = tile.TileWidth > 0 ? tile.TileWidth : Math.Max(bounds.Width, 1.0);
                double th = tile.TileHeight > 0 ? tile.TileHeight : Math.Max(bounds.Height, 1.0);
                maskParams1 = new Vector4(
                    (float)(rad.CenterX / tw),
                    (float)(rad.CenterY / th),
                    (float)(rad.RadiusX / tw),
                    (float)(rad.RadiusY / th));
            } else if (gradient is ConicGradient con) {
                double tw = tile.TileWidth > 0 ? tile.TileWidth : Math.Max(bounds.Width, 1.0);
                double th = tile.TileHeight > 0 ? tile.TileHeight : Math.Max(bounds.Height, 1.0);
                maskParams1 = new Vector4(
                    (float)(con.CenterX / tw),
                    (float)(con.CenterY / th),
                    (float)con.FromAngleDegrees,
                    count);
            }
            // Stop count lives in params0.w for every gradient kind (linear/radial/
            // conic) so the shader reads it from one place regardless of how params1
            // is laid out. This is what fixes the radial mask, whose params1 has no
            // spare slot for the count.
            maskParams0.w = count;
            StoreMaskLayer(ref inst, layerIndex, maskParams0, maskBounds, maskTile,
                maskParams1, maskColor0, maskColor1, maskColor2, maskColor3, maskPositions);
        }

        static void StoreMaskLayer(ref UIQuadInstance inst, int layerIndex,
            Vector4 params0, Vector4 bounds, Vector4 tile, Vector4 params1,
            Vector4 color0, Vector4 color1, Vector4 color2, Vector4 color3, Vector4 positions) {
            // params0 = (kind, mode+composite*4(+count*16 on layer0), repeatX, repeatY)
            // tile    = (originX, originY, tileW, tileH)  — tileW/H==box size means "no tiling"
            // params1 = radial/conic: (cx, cy, rx, ry) normalized by tile; linear: (sinθ,−cosθ,count,repeat)
            Weva.Diagnostics.UILayoutDiagnostics.TraceMask(
                $"layer{layerIndex} kind={params0.x} modeCompCount={params0.y} repeat=({params0.z},{params0.w}) " +
                $"bounds=({bounds.x:F1},{bounds.y:F1} {bounds.z:F1}x{bounds.w:F1}) " +
                $"tile=(origin {tile.x:F2},{tile.y:F2}  size {tile.z:F2}x{tile.w:F2}) " +
                $"params1=({params1.x:F3},{params1.y:F3},{params1.z:F3},{params1.w:F3})");
            switch (layerIndex) {
                case 0:
                    inst.MaskParams0 = params0;
                    inst.MaskBounds = bounds;
                    inst.MaskTile = tile;
                    inst.MaskParams1 = params1;
                    inst.MaskColor0 = color0;
                    inst.MaskColor1 = color1;
                    inst.MaskColor2 = color2;
                    inst.MaskColor3 = color3;
                    inst.MaskPositions = positions;
                    break;
                case 1:
                    inst.Mask1Params0 = params0;
                    inst.Mask1Bounds = bounds;
                    inst.Mask1Tile = tile;
                    inst.Mask1Params1 = params1;
                    inst.Mask1Color0 = color0;
                    inst.Mask1Color1 = color1;
                    inst.Mask1Color2 = color2;
                    inst.Mask1Color3 = color3;
                    inst.Mask1Positions = positions;
                    break;
                case 2:
                    inst.Mask2Params0 = params0;
                    inst.Mask2Bounds = bounds;
                    inst.Mask2Tile = tile;
                    inst.Mask2Params1 = params1;
                    inst.Mask2Color0 = color0;
                    inst.Mask2Color1 = color1;
                    inst.Mask2Color2 = color2;
                    inst.Mask2Color3 = color3;
                    inst.Mask2Positions = positions;
                    break;
                case 3:
                    inst.Mask3Params0 = params0;
                    inst.Mask3Bounds = bounds;
                    inst.Mask3Tile = tile;
                    inst.Mask3Params1 = params1;
                    inst.Mask3Color0 = color0;
                    inst.Mask3Color1 = color1;
                    inst.Mask3Color2 = color2;
                    inst.Mask3Color3 = color3;
                    inst.Mask3Positions = positions;
                    break;
            }
        }

        static Vector4 ColorVec(LinearColor c) {
            return new Vector4(c.R, c.G, c.B, c.A);
        }

        LinearColor ApplyOpacity(LinearColor c) {
            float a = currentOpacity;
            if (a >= 1f) return c;
            return new LinearColor(c.R, c.G, c.B, c.A * a);
        }

        void EnsureBatch(UIBatchKey key) {
            if (!hasCurrentBatch || !currentKey.Equals(key) || currentInstances.Count >= MaxInstancesPerBatch) {
                FlushCurrentBatch();
                currentKey = key;
                hasCurrentBatch = true;
            }
        }

        void AppendInstance(UIQuadInstance instance) {
            // B24 v1 — flag the in-flight batch when a page-backdrop mix-blend
            // instance lands in it. Page-backdrop instances have:
            //   TransformRow0.z > 0   (non-Normal blend ordinal)
            //   TransformRow0.w == 0  (element-local flag = 0 → page backdrop)
            // Element-local background-blend-mode instances have w ≈ 1 and
            // must NOT set the flag (they never sample _WevaBackdrop).
            // The replay path also reaches AppendInstance for retained
            // snapshots — those carry baked-in ordinals and are covered here.
            // ExactSrgbSourceOver (ordinal 17) carries a non-zero z but is NOT a
            // page-backdrop blend (it never samples _WevaBackdrop — see
            // IsPageBackdropBlend / Weva-Quad.shader), so it must not flag a
            // refresh. Real §6 modes (1..16) still do.
            if (!currentBatchNeedsBackdropRefresh
                && instance.TransformRow0.z > 0.5f
                && instance.TransformRow0.w < 0.5f
                && (ExactSrgbSourceOverNeedsBackdrop
                    || (int)(instance.TransformRow0.z + 0.5f) != (int)MixBlendMode.ExactSrgbSourceOver)) {
                currentBatchNeedsBackdropRefresh = true;
            }
            // Accumulate the batch's screen-space AABB (conservative: the
            // transformed quad's bounding box). Consumed by the shared-
            // backdrop shareability check: a panel may ride the one-capture
            // shared blur only if no content drawn AFTER the capture
            // underlies it — otherwise its crop samples a screen state that
            // predates its own backdrop (a later section's glass panel
            // blurred the page's bare background instead of the gradient
            // painted between the capture and its event). PosSize =
            // (centerX, centerY, halfW, halfH); rows 0/1 carry the 2x2
            // (A,B / C,D) in .xy; row 2 carries the translation. Points map
            // as x' = A*x + C*y + Tx, y' = B*x + D*y + Ty (Transform2D is
            // row-major with the point on the LEFT; the shader agrees:
            // wpos.x = R0.x*x + R1.x*y + R2.x). An earlier version applied
            // the transposed linear part here — invisible for the
            // axis-aligned calibration content (B=C=0) but wrong for
            // rotate/skew (rendering audit NEW-2). Covers the
            // snapshot-replay path too (it also lands here).
            {
                float cx = instance.PosSize.x, cy = instance.PosSize.y;
                float hw = instance.PosSize.z, hh = instance.PosSize.w;
                float a = instance.TransformRow0.x, bb = instance.TransformRow0.y;
                float c = instance.TransformRow1.x, d = instance.TransformRow1.y;
                float scx = a * cx + c * cy + instance.TransformRow2.x;
                float scy = bb * cx + d * cy + instance.TransformRow2.y;
                float ex = (a < 0 ? -a : a) * hw + (c < 0 ? -c : c) * hh;
                float ey = (bb < 0 ? -bb : bb) * hw + (d < 0 ? -d : d) * hh;
                if (scx - ex < currentBatchMinX) currentBatchMinX = scx - ex;
                if (scy - ey < currentBatchMinY) currentBatchMinY = scy - ey;
                if (scx + ex > currentBatchMaxX) currentBatchMaxX = scx + ex;
                if (scy + ey > currentBatchMaxY) currentBatchMaxY = scy + ey;
            }
            currentInstances.Add(instance);
            if (currentInstances.Count >= MaxInstancesPerBatch) {
                FlushCurrentBatch();
                hasCurrentBatch = true; // keep the same key open if more come in
            }
        }

        void FlushCurrentBatch() {
            if (!hasCurrentBatch || currentInstances.Count == 0) {
                hasCurrentBatch = false;
                currentInstances.Clear();
                currentAtlas0 = 0;
                currentAtlas1 = 0;
                currentAtlas2 = 0;
                currentAtlas3 = 0;
                currentBatchNeedsBackdropRefresh = false;
                ResetCurrentBatchBounds();
                return;
            }
            // Was: currentInstances.ToArray() — that allocated a fresh
            // exact-fit array per flush, ~103 KB / frame across ~7 batches
            // in the gem-grid scene. The render pass and snapshot capture
            // both walk via batch.Count, never via .Length, so an oversized
            // pooled array is safe. Reset() returns these to the pool before
            // the next frame's flushes re-rent.
            int count = currentInstances.Count;
            var arr = System.Buffers.ArrayPool<UIQuadInstance>.Shared.Rent(count);
            for (int i = 0; i < count; i++) arr[i] = currentInstances[i];
            // Capture the event-log position at flush time. The render pass uses
            // this to drain stencil events in submission order even when sibling
            // clip-pushers produce content batches with the same numeric ref —
            // a previous reference-equality check skipped the intervening
            // Pop+Push pair and rendered later siblings against the wrong mask.
            // B24 v1: propagate the NeedsBackdropRefresh flag captured by
            // AppendInstance so DrainBatches knows to blit colorTarget →
            // _WevaBackdrop before drawing this batch.
            batches.Add(new UIQuadBatch(currentKey, arr, count, clips.Events.Count,
                currentAtlas0, currentAtlas1, currentAtlas2, currentAtlas3,
                needsBackdropRefresh: currentBatchNeedsBackdropRefresh,
                maskImageTexture: currentMaskImageTexture,
                pixelBounds: new Vector4(currentBatchMinX, currentBatchMinY,
                                         currentBatchMaxX, currentBatchMaxY)));
            currentInstances.Clear();
            hasCurrentBatch = false;
            ResetCurrentBatchBounds();
            currentAtlas0 = 0;
            currentAtlas1 = 0;
            currentAtlas2 = 0;
            currentAtlas3 = 0;
            currentBatchNeedsBackdropRefresh = false;
            // Do NOT clear currentMaskImageTexture here. The field is owned by
            // the Push/PopMask scope stack (B16), and a mask scope spans the
            // element AND all descendants — but batches can break mid-scope
            // for reasons unrelated to masking (an <img> switching the batch
            // class, subtree-capture begin, 5th-atlas overflow, instance-count
            // overflow, blend/filter transitions). Clearing on flush latched
            // maskImageTexture: null into every batch after such a break, so
            // the remainder of the scope rendered with the material-default
            // white coverage — i.e. clipped only to the path's AABB instead
            // of the path (rendering audit NEW-1). Reset() still clears the
            // field between frames; PopMask restores the outer scope's value.
        }

        readonly struct TransformFrame {
            public readonly Transform2D Previous;
            public TransformFrame(Transform2D previous) { Previous = previous; }
        }

        readonly struct OpacityLayer {
            public readonly float Opacity;
            public readonly int StartBatchIndex;
            public OpacityLayer(float opacity, int startBatchIndex) {
                Opacity = opacity;
                StartBatchIndex = startBatchIndex;
            }
        }
    }

    public enum FilterEventKind {
        Push,
        Pop
    }

    public readonly struct FilterEvent {
        public readonly FilterEventKind Kind;
        public readonly PaintRect Bounds;
        public readonly FilterChain Filters;
        // Snapshot of the transform stack at the time of the Push. Pop reuses
        // this so callers don't need to thread it forward independently. This
        // is the OUTER transform (parent + ancestors); the box's own
        // transform is recorded separately in ScopeBoxTransform below.
        public readonly Transform2D Transform;
        // The filter-owner element's own CSS `transform`. Per CSS Filter
        // Effects L1, filter applies BEFORE transform — meaning the
        // rasterization inside the scope uses only the OUTER `Transform`
        // (stable across the owning element's transform animation), and
        // this ScopeBoxTransform is applied at composite to position the
        // blurred RT on the camera target. Identity = no extra composite-
        // time transform (the legacy behaviour). Threaded through by the
        // converter when an element has both `filter` and `transform`.
        public readonly Transform2D ScopeBoxTransform;
        // Index of the first batch emitted inside this scope (Push) / start of
        // the matching scope (Pop).
        public readonly int BeginIndex;
        // Index of the batch immediately AFTER the scope ends (Push) / same
        // value as on the matching Push (Pop). Stored on Push events too so
        // the scheduler can find a scope's extent from a single event lookup.
        public readonly int EndIndex;
        // Cross-reference to the matched Push/Pop event in the flat list, or
        // -1 if this is an unclosed (still-open) Push at the time the list
        // was inspected.
        public readonly int PairedIndex;

        public FilterEvent(FilterEventKind kind, PaintRect bounds, FilterChain filters,
                           Transform2D transform, int beginIndex, int endIndex, int pairedIndex)
            : this(kind, bounds, filters, transform, Transform2D.Identity, beginIndex, endIndex, pairedIndex) { }

        public FilterEvent(FilterEventKind kind, PaintRect bounds, FilterChain filters,
                           Transform2D transform, Transform2D scopeBoxTransform,
                           int beginIndex, int endIndex, int pairedIndex) {
            Kind = kind;
            Bounds = bounds;
            Filters = filters;
            Transform = transform;
            ScopeBoxTransform = scopeBoxTransform;
            BeginIndex = beginIndex;
            EndIndex = endIndex;
            PairedIndex = pairedIndex;
        }
    }

    public readonly struct BackdropFilterEvent {
        public readonly PaintRect Bounds;
        public readonly BorderRadii Radii;
        public readonly FilterChain Filters;
        public readonly Transform2D Transform;
        public readonly int BatchIndex;

        public BackdropFilterEvent(PaintRect bounds, BorderRadii radii, FilterChain filters,
                                   Transform2D transform, int batchIndex) {
            Bounds = bounds;
            Radii = radii;
            Filters = filters;
            Transform = transform;
            BatchIndex = batchIndex;
        }
    }

    public readonly struct UIBatchKey : IEquatable<UIBatchKey> {
        public readonly UIQuadBrush Brush;
        public readonly bool Bordered;
        public readonly int StencilRef;
        // AtlasId is non-zero only for _TEXT batches and disambiguates per-atlas.
        // Distinct atlases (multi-font runs) break the batch so the renderer can
        // bind the correct _GlyphAtlas texture for each draw.
        public readonly int AtlasId;
        public readonly string ImageHandle;
        public readonly UnityEngine.Texture ImageTexture;

        public UIBatchKey(UIQuadBrush brush, bool isBordered, int stencilRef)
            : this(brush, isBordered, stencilRef, 0, null) { }

        public UIBatchKey(UIQuadBrush brush, bool isBordered, int stencilRef, int atlasId)
            : this(brush, isBordered, stencilRef, atlasId, null) { }

        public UIBatchKey(UIQuadBrush brush, bool isBordered, int stencilRef, int atlasId, string imageHandle,
                          UnityEngine.Texture imageTexture = null) {
            Brush = brush;
            Bordered = isBordered;
            StencilRef = stencilRef;
            AtlasId = atlasId;
            ImageHandle = imageHandle;
            ImageTexture = imageTexture;
        }

        public bool Equals(UIBatchKey other) {
            // Bordered AND AtlasId are intentionally NOT part of equality.
            // Borders are per-instance (zero widths render as no edge).
            // Atlas binding is per-batch via UIQuadBatch.AtlasIdSlot0/1.
            //
            // Solid and Text brushes share a single batch class because the
            // shader's bordered+text variant (_BORDERED + _TEXT keywords) can
            // dispatch both paths via the per-instance brushIndex. This
            // collapses the Solid↔Text alternation that arose from emitting
            // bg → text → bg → text in tree order; instead one batch holds
            // mixed brushIndex instances. Gradient/Shadow brushes still
            // distinguish because their math diverges before the brushIndex
            // dispatch (the gradient samplers + shadow erf path).
            int brushClass = BrushClass(Brush);
            int otherClass = BrushClass(other.Brush);
            if (brushClass != otherClass || StencilRef != other.StencilRef) return false;
            if (brushClass == 6) {
                return ImageHandle == other.ImageHandle
                    && ReferenceEquals(ImageTexture, other.ImageTexture);
            }
            return true;
        }

        public override bool Equals(object obj) => obj is UIBatchKey k && Equals(k);
        public override int GetHashCode() {
            unchecked {
                int h = (int)BrushClass(Brush);
                h = (h * 397) ^ StencilRef;
                if (BrushClass(Brush) == 6) {
                    h = (h * 397) ^ (ImageHandle != null ? ImageHandle.GetHashCode() : 0);
                    h = (h * 397) ^ (ImageTexture != null ? ImageTexture.GetInstanceID() : 0);
                }
                return h;
            }
        }

        // Maps the fine-grained Brush enum onto a batch class. Class 0 is the
        // "fill-text-shadow" union — Solid, Text, all gradient variants
        // (Linear/Radial/Conic), AND outset/inset shadows share it. The
        // shader has every sampling path compiled in (the keyword toggles
        // in ApplyKeywordsToBuffer unconditionally enable
        // _BRUSH_LINEAR/RADIAL/CONIC + _BORDERED + _TEXT + _SHADOW_OUTSET
        // + _SHADOW_INSET for class 0) and dispatches per-instance via
        // brushIndex; non-matching paths early-return for a given quad.
        // This collapses both the Solid → LinearGradient alternation AND
        // the Shadow → Fill alternation (each .tile.box-shadow forced its
        // own batch in match3 — 64 tiles × 2 = ~128 batches just for the
        // board; folding them into class 0 brings it to a handful).
        static int BrushClass(UIQuadBrush b) {
            switch (b) {
                case UIQuadBrush.Solid:
                case UIQuadBrush.Text:
                case UIQuadBrush.LinearGradient:
                case UIQuadBrush.RadialGradient:
                case UIQuadBrush.ConicGradient:
                case UIQuadBrush.Shadow:
                case UIQuadBrush.ShadowInset:
                    return 0;
                case UIQuadBrush.Image:          return 6;
                default:                         return (int)b + 100;
            }
        }
    }

    public readonly struct UIQuadBatch {
        public readonly UIBatchKey Key;
        // ArrayPool-rented; may be OVERSIZED. Consumers MUST honour `Count`
        // (or the equivalent `InstanceCount` accessor) for the active slice.
        // The batcher returns these arrays to ArrayPool<UIQuadInstance>.Shared
        // in UIBatcher.Reset() before clearing the batches list.
        public readonly UIQuadInstance[] Instances;
        // Real instance count — Instances may be longer when the array came
        // from ArrayPool.Rent (which returns power-of-two sizes >= the asked
        // count). InstanceCount returns Count for this reason.
        public readonly int Count;
        // Number of stencil clip events that were recorded BEFORE this batch was
        // flushed. Used by the render pass to drain events up to (but not past)
        // this point regardless of whether the batch's stencil ref happens to
        // match the prior batch's. CRITICAL for sibling clip-pushers: e.g.
        // Push(1) → content @ ref=1 → Pop(1) → Push(1) → content @ ref=1 →
        // Pop(1). Both content batches have StencilRef=1, but the Pop+Push pair
        // BETWEEN them MUST be issued or the second batch renders against the
        // first sibling's stencil mask region.
        public readonly int EventIndexBefore;
        // Multi-atlas binding for text batches. Up to four distinct glyph
        // atlases may be referenced per batch; each instance picks via slot
        // (encoded in BorderColorTop.y). 0 = unbound. The renderer binds
        // _GlyphAtlas[0..3] when issuing.
        public readonly int AtlasIdSlot0;
        public readonly int AtlasIdSlot1;
        public readonly int AtlasIdSlot2;
        public readonly int AtlasIdSlot3;
        // B24 v1 — CSS Compositing 1 §10. True when this batch contains at
        // least one page-backdrop mix-blend-mode instance (TransformRow0.z > 0,
        // TransformRow0.w == 0). DrainBatches blits colorTarget →
        // _WevaBackdrop immediately before drawing this batch so the blend
        // formulas sample a destination that includes all UI painted earlier
        // in the same frame (body bg, sibling panels, etc.) rather than only
        // the pre-UI camera clear. Element-local background-blend-mode batches
        // (TransformRow0.w == 1) never sample _WevaBackdrop and must NOT set
        // this flag. Set by UIBatcher.AppendInstance; propagated into this
        // struct by FlushCurrentBatch.
        public readonly bool NeedsBackdropRefresh;

        // B16 — path coverage mask texture bound to _WevaMaskImage for this batch.
        // Non-null when at least one instance in this batch uses a synthetic path-
        // clip mask layer (kind=5 with a PathCoverageImageSource). The batcher
        // forces a batch break when this texture changes so the renderer can rebind
        // _WevaMaskImage per draw call. Null means no path coverage mask (the
        // shader samples the default white texture and applies no clip effect).
        public readonly UnityEngine.Texture MaskImageTexture;

        // Screen-space AABB of the batch's instances (minX, minY, maxX, maxY),
        // accumulated conservatively (transformed-quad bounding boxes) by
        // UIBatcher.AppendInstance. Consumed by the shared-backdrop
        // shareability check: a backdrop panel may only use the one-capture
        // shared blur when no batch drawn AFTER the capture underlies it.
        // Empty batches carry the (+inf, +inf, -inf, -inf) sentinel, which
        // never overlaps anything. Legacy convenience ctors (tests) carry the
        // UNKNOWN sentinel (-inf..+inf), which conservatively overlaps
        // everything.
        public readonly UnityEngine.Vector4 PixelBounds;
        static readonly UnityEngine.Vector4 UnknownBounds =
            new UnityEngine.Vector4(float.MinValue, float.MinValue, float.MaxValue, float.MaxValue);

        public UIQuadBatch(UIBatchKey key, UIQuadInstance[] instances)
            : this(key, instances, instances?.Length ?? 0, 0, 0, 0, 0, 0, false, null) { }

        public UIQuadBatch(UIBatchKey key, UIQuadInstance[] instances, int eventIndexBefore)
            : this(key, instances, instances?.Length ?? 0, eventIndexBefore, 0, 0, 0, 0, false, null) { }

        public UIQuadBatch(UIBatchKey key, UIQuadInstance[] instances, int eventIndexBefore, int atlasIdSlot0, int atlasIdSlot1)
            : this(key, instances, instances?.Length ?? 0, eventIndexBefore, atlasIdSlot0, atlasIdSlot1, 0, 0, false, null) { }

        public UIQuadBatch(UIBatchKey key, UIQuadInstance[] instances, int eventIndexBefore,
                           int atlasIdSlot0, int atlasIdSlot1, int atlasIdSlot2, int atlasIdSlot3)
            : this(key, instances, instances?.Length ?? 0, eventIndexBefore,
                atlasIdSlot0, atlasIdSlot1, atlasIdSlot2, atlasIdSlot3, false, null) { }

        public UIQuadBatch(UIBatchKey key, UIQuadInstance[] instances, int count, int eventIndexBefore,
                           int atlasIdSlot0, int atlasIdSlot1, int atlasIdSlot2, int atlasIdSlot3)
            : this(key, instances, count, eventIndexBefore,
                atlasIdSlot0, atlasIdSlot1, atlasIdSlot2, atlasIdSlot3, false, null) { }

        public UIQuadBatch(UIBatchKey key, UIQuadInstance[] instances, int count, int eventIndexBefore,
                           int atlasIdSlot0, int atlasIdSlot1, int atlasIdSlot2, int atlasIdSlot3,
                           bool needsBackdropRefresh)
            : this(key, instances, count, eventIndexBefore,
                atlasIdSlot0, atlasIdSlot1, atlasIdSlot2, atlasIdSlot3, needsBackdropRefresh, null) { }

        public UIQuadBatch(UIBatchKey key, UIQuadInstance[] instances, int count, int eventIndexBefore,
                           int atlasIdSlot0, int atlasIdSlot1, int atlasIdSlot2, int atlasIdSlot3,
                           bool needsBackdropRefresh, UnityEngine.Texture maskImageTexture)
            : this(key, instances, count, eventIndexBefore, atlasIdSlot0, atlasIdSlot1,
                atlasIdSlot2, atlasIdSlot3, needsBackdropRefresh, maskImageTexture, UnknownBounds) { }

        public UIQuadBatch(UIBatchKey key, UIQuadInstance[] instances, int count, int eventIndexBefore,
                           int atlasIdSlot0, int atlasIdSlot1, int atlasIdSlot2, int atlasIdSlot3,
                           bool needsBackdropRefresh, UnityEngine.Texture maskImageTexture,
                           UnityEngine.Vector4 pixelBounds) {
            PixelBounds = pixelBounds;
            Key = key;
            Instances = instances ?? Array.Empty<UIQuadInstance>();
            Count = count;
            EventIndexBefore = eventIndexBefore;
            AtlasIdSlot0 = atlasIdSlot0;
            AtlasIdSlot1 = atlasIdSlot1;
            AtlasIdSlot2 = atlasIdSlot2;
            AtlasIdSlot3 = atlasIdSlot3;
            NeedsBackdropRefresh = needsBackdropRefresh;
            MaskImageTexture = maskImageTexture;
        }

        public int InstanceCount => Count;
    }

    // Quad emitted by SdfTextRendering — bounds + atlas UV rect + color. The batcher
    // packs (UvMin, UvMax) into the instance's Brush + BorderColorTop slots.
    //
    // BlurRadius is the CSS Text Decoration §6 `text-shadow` blur radius (in
    // CSS pixels) propagated by SdfGlyphAtlasAdapter when the originating
    // DrawTextCommand carries a non-zero blur. The batcher writes this into
    // BorderColorTop.z; the shader widens its SDF AA smoothstep band by the
    // same amount so the silhouette feathers outward by `BlurRadius` px (Path
    // A — SDF dilation). Zero (the default) renders crisp — every non-shadow
    // glyph and every zero-blur drop shadow uses this path with no extra
    // ALU vs. v0.
    public readonly struct SdfGlyphQuad {
        public readonly PaintRect Bounds;
        public readonly LinearColor Color;
        public readonly Vector2 UvMin;
        public readonly Vector2 UvMax;
        public readonly int AtlasId;
        public readonly float BlurRadius;
        // Faux-bold SDF threshold shift. The shader shifts its smoothstep
        // midpoint from 0.5 to 0.5 - WeightBias so the coverage band moves
        // outward, widening the glyph stroke by roughly `WeightBias /
        // fwidth(d)` screen pixels (cheaply: a bias of 0.075 on a typical
        // 14–24pt atlas thickens strokes by ≈1–2 px). Zero = unchanged
        // (weight 400 / regular). Positive values produce faux-bold; negative
        // would produce faux-thin (reserved). Computed at shape time from
        // FontHandle.Weight in SdfGlyphAtlasAdapter.
        public readonly float WeightBias;

        // When true and the bound atlas is a COLOR (RGBA) atlas, the shader
        // ignores the texel's RGB and tints the glyph with this quad's
        // `Color` instead — sampling the alpha as a coverage mask. Used for
        // Unicode "text-default" emoji codepoints (↩ ⏸ ⚠ etc.) that come
        // from a color font but should pick up CSS `color` to match how
        // browsers render them. No effect on mono SDF atlas batches.
        public readonly bool TintWithFillColor;

        public SdfGlyphQuad(PaintRect bounds, LinearColor color, Vector2 uvMin, Vector2 uvMax,
            int atlasId = 0, float blurRadius = 0f, float weightBias = 0f, bool tintWithFillColor = false) {
            Bounds = bounds;
            Color = color;
            UvMin = uvMin;
            UvMax = uvMax;
            AtlasId = atlasId;
            BlurRadius = blurRadius > 0f ? blurRadius : 0f;
            WeightBias = weightBias;
            TintWithFillColor = tintWithFillColor;
        }
    }
}
