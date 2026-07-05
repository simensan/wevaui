#if WEVA_URP
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_2023_3_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif
using Weva.Paint;
using Weva.Paint.Filters;
using Weva.Paint.Images;
using Weva.Rendering;

namespace Weva.Rendering.URP {
    // Batched RenderGraph pass for the new IRenderBackend pipeline. Drains UIBatcher
    // batches into a single mesh + uniform-array DrawMeshInstanced call per batch.
    // Honors PushClip/PopClip via stencil writes (StencilClipManager.Events). PushOpacity
    // is currently flattened into the per-quad alpha; nested RT compositing is queued for
    // a later iteration.
    public sealed class UIRenderGraphPass : ScriptableRenderPass {
        public const string PassName = "Weva.BatchedQuadPass";
        public const RenderPassEvent OverlayRenderPassEvent = RenderPassEvent.AfterRendering;
        public const int InstanceFloat4Stride = UIQuadInstance.Float4Count;
        // 1024 instance cap. Instance data is uploaded via a StructuredBuffer
        // (GraphicsBuffer.Target.Structured), not a constant buffer, so the
        // 64 KB cbuffer cap that limited us to 256 instances no longer applies
        // — we sized for the largest single batch we've seen (~514 in the
        // demo) plus generous headroom. Each chunk = 1 cmd.DrawMesh; pushing
        // larger keeps text+UI batches single-draw rather than 2-3-chunk
        // splits at 256. Buffer size tracks UIQuadInstance.Float4Count so
        // mask/clip layout changes stay in one place.
        // GPU-friendly upload thresholds.
        public const int MaxInstancesPerDraw = 1024;

        public static readonly int IdInstances = Shader.PropertyToID("_WevaInstances");
        public static readonly int IdInstancesSB = Shader.PropertyToID("_WevaInstancesSB");
        public static readonly int IdInstanceCount = Shader.PropertyToID("_WevaInstanceCount");
        public static readonly int IdViewport = Shader.PropertyToID("_WevaViewport");
        public static readonly int IdStencilRef = Shader.PropertyToID(StencilClipGeometry.StencilRefProperty);
        public static readonly int IdStencilComp = Shader.PropertyToID(StencilClipGeometry.StencilCompProperty);
        public static readonly int IdStencilWriteRef = Shader.PropertyToID(StencilClipGeometry.StencilWriteRefProperty);

        public const string KeywordBrushLinear = "_BRUSH_LINEAR";
        public const string KeywordBrushRadial = "_BRUSH_RADIAL";
        public const string KeywordBrushConic = "_BRUSH_CONIC";
        public const string KeywordBordered = "_BORDERED";
        public const string KeywordShadowOutset = "_SHADOW_OUTSET";
        public const string KeywordShadowInset = "_SHADOW_INSET";
        public const string KeywordText = "_TEXT";
        public static readonly int IdGlyphAtlas = Shader.PropertyToID("_GlyphAtlas");
        public static readonly int IdGlyphAtlasChannelMask = Shader.PropertyToID("_GlyphAtlasChannelMask");
        public static readonly int IdGlyphAtlas1 = Shader.PropertyToID("_GlyphAtlas1");
        public static readonly int IdGlyphAtlas1ChannelMask = Shader.PropertyToID("_GlyphAtlas1ChannelMask");
        public static readonly int IdGlyphAtlas2 = Shader.PropertyToID("_GlyphAtlas2");
        public static readonly int IdGlyphAtlas2ChannelMask = Shader.PropertyToID("_GlyphAtlas2ChannelMask");
        public static readonly int IdGlyphAtlas3 = Shader.PropertyToID("_GlyphAtlas3");
        public static readonly int IdGlyphAtlas3ChannelMask = Shader.PropertyToID("_GlyphAtlas3ChannelMask");
        public static readonly int IdImageTexture = Shader.PropertyToID("_WevaImage");
        // B16 — path coverage mask image. Bound per-batch when MaskImageTexture is non-null.
        public static readonly int IdMaskImageTexture = Shader.PropertyToID("_WevaMaskImage");
        // CSS Compositing 1 §10 (mix-blend-mode) — the shader samples the
        // pre-composite backdrop from `_WevaBackdrop` to evaluate the
        // separable blend formulas. `_WevaBackdropAvailable` flags
        // whether the binding is live this frame; when 0 the shader
        // falls back to a zero backdrop. UIRenderGraphPass copies the
        // active color target into a temp RT before draining batches so
        // the read-source and the UI write-target are distinct (URP's
        // RenderGraph rejects read-write-same-resource on a single pass).
        public static readonly int IdWevaBackdrop = Shader.PropertyToID("_WevaBackdrop");
        public static readonly int IdWevaBackdropAvailable = Shader.PropertyToID("_WevaBackdropAvailable");
        // 1 when the backdrop copy is bottom-up (camera renders to an
        // intermediate RT — post-processing on) so Weva_SampleBackdropPremul
        // must flip V; 0 when the copy is already CSS-oriented (backbuffer
        // source). Same derivation as the backdrop-filter capture's
        // backdropCaptureSourceYFlip — A-MIXBLEND-YFLIP was the filed
        // sibling gap for the mix-blend sampler.
        public static readonly int IdWevaBackdropYFlip = Shader.PropertyToID("_WevaBackdropYFlip");
        // Per-frame temp RT used as the destination of the backdrop blit.
        // Sized to the camera target; allocated only when the document
        // contains a backdrop-sampling feature (`mix-blend-mode` or
        // `backdrop-filter`). The GetTemporaryRT name id is a constant so
        // URP's RT cache can hit on identical descriptors across frames.
        public static readonly int IdWevaBackdropCopyRt = Shader.PropertyToID("_WevaBackdropCopyRt");
        // A-SRGB-COMPOSITE: the always-on intermediate UI RT the whole frame's
        // UI draws into for gamma-space compositing, so the fixed-function blend
        // can run in gamma space; a final pass composites it back to the camera.
        public static readonly int IdWevaUiCompositeRt = Shader.PropertyToID("_WevaUiCompositeRt");
        // A-SRGB-COMPOSITE: per-frame global gating Weva_EncodeForTarget — 1 when
        // the UI renders into the intermediate RT (gamma-space compositing).
        public static readonly int IdWevaSrgbComposite = Shader.PropertyToID("_WevaSrgbComposite");

        // Selects which channel of the bound _GlyphAtlas carries the SDF
        // distance. R8 atlases (TextCore-rasterized) keep the data in red and
        // sample as (R, 0, 0, 1). Alpha8 atlases (TMP's default SDFAA bake)
        // keep the data in alpha and sample as (0, 0, 0, A). For color
        // (RGBA32+) emoji atlases the channel mask is irrelevant — the
        // _TEXT_COLOR shader variant samples the atlas RGBA directly and
        // never dot-products against this mask. We still return the alpha
        // mask as the safe default in case a batch ends up bound to a color
        // texture without the keyword on (e.g. batch coalescing accident);
        // the worst-case render is then a translucent silhouette rather than
        // a black square.
        public static Vector4 GetGlyphAtlasChannelMask(UnityEngine.Texture tex) {
            if (tex == null) return new Vector4(1f, 0f, 0f, 0f);
            if (tex is Texture2D tex2D) {
                var fmt = tex2D.format;
                switch (fmt) {
                    case TextureFormat.R8:
                    case TextureFormat.RFloat:
                    case TextureFormat.RHalf:
                        return new Vector4(1f, 0f, 0f, 0f);
                    case TextureFormat.Alpha8:
                        return new Vector4(0f, 0f, 0f, 1f);
                    default:
                        // RGBA / BGRA / ARGB / etc. The actual color-bitmap
                        // path uses _TEXT_COLOR (no mask). For TMP SDFAA
                        // bakes that emit RGBA but keep the SDF in alpha,
                        // .a is still the right channel.
                        return new Vector4(0f, 0f, 0f, 1f);
                }
            }
            return new Vector4(0f, 0f, 0f, 1f);
        }

        // Heuristic: a color atlas is one explicitly tagged in AtlasRegistry
        // by the SdfGlyphAtlasAdapter when it sees TmpFontAssetSource.IsColor,
        // OR (defensively) one whose backing Texture2D is an RGBA bitmap
        // format. The registry tag is the authoritative path; the texture
        // probe is a fallback for assets registered without going through
        // SdfGlyphAtlasAdapter.
        public static bool IsColorAtlas(int atlasId, UnityEngine.Texture tex) {
            if (atlasId != 0 && Weva.Text.Sdf.AtlasRegistry.IsColorAtlasId(atlasId)) return true;
            if (tex is Texture2D t) {
                switch (t.format) {
                    case TextureFormat.RGBA32:
                    case TextureFormat.BGRA32:
                    case TextureFormat.ARGB32:
                    case TextureFormat.RGB24:
                    case TextureFormat.RGBAHalf:
                    case TextureFormat.RGBAFloat:
                        return true;
                }
            }
            return false;
        }

        // Filter runtime — owns the temp-RT pool + filter material binding.
        // Lazily constructed because Shader.Find for "Hidden/Weva/Filter"
        // can fail to resolve during early app startup (shader hasn't been
        // imported yet); we retry on each frame until IsReady. The legacy
        // CB FilterPipeline lives in URPRenderBackend and stays untouched.
        public static readonly int IdViewportOrigin = Shader.PropertyToID("_WevaViewportOrigin");

        readonly BatchedURPRenderBackend backend;
        readonly UIBatchedResources resources;
        readonly Vector4[] uploadBuffer;
        readonly List<IUIPaintSource> scratchSources = new List<IUIPaintSource>();
        readonly MeshBuilder stencilBuilder = new MeshBuilder();
        UIRenderGraphFilterRuntime filterRuntime;

        Mesh quadMesh;
        // Pool of StructuredBuffers — one per cmd.DrawMesh issued this frame.
        // Each chunk needs its own buffer because cmd.DrawMesh captures the
        // buffer reference at record time and reads it back at execute time;
        // a single shared buffer would let later chunks' SetData overwrite
        // earlier chunks' data before the GPU got around to drawing them.
        // Reused across frames; recycled at the start of each DrainBatches.
        readonly Stack<GraphicsBuffer> instanceBufferPool = new Stack<GraphicsBuffer>();
        readonly List<GraphicsBuffer> instanceBuffersInUse = new List<GraphicsBuffer>(8);

        // Persistent per-chunk upload state. Each chunk drawn during
        // DrainBatches (across all batches, in submission order) gets a
        // stable slot keyed by sequential index. The slot's GraphicsBuffer
        // is kept alive across frames so the GPU memory carrying the
        // chunk's instance data persists — that lets us diff this frame's
        // packed data against the prior frame's and issue
        // GraphicsBuffer.SetData only for the *changed* ranges, instead
        // of re-uploading the entire chunk. For animations that touch a
        // handful of instances per frame this is the difference between
        // ~100KB/frame of CPU→GPU upload and a few KB. Multiple
        // GraphicsBuffer.SetData calls coalesce by range; consecutive
        // changed slots produce one call.
        //
        // Stable-index caveat: when the batch order or instance count is
        // unstable across frames the seq index points at a "different"
        // chunk than last frame's. The diff would then find most slots
        // mismatched, the partial-upload path issues one big range, and
        // we land at the legacy SetData(...full count) behavior. So the
        // fast path requires structural stability; volatility falls back
        // gracefully to the prior cost.
        sealed class ChunkUploadState {
            public GraphicsBuffer Buffer;
            public Vector4[] LastData;
            public int LastFloat4Count;
            public MaterialPropertyBlock Mpb;
        }
        readonly List<ChunkUploadState> chunkStates = new List<ChunkUploadState>(16);
        int chunkSeqIdx;

        // Counters for telemetry. PartialUploadFloat4s and
        // FullUploadFloat4s sum across a frame's chunks: their ratio
        // tells how much upload bandwidth the diff-based path is saving.
        public long PartialUploadFloat4s { get; private set; }
        public long FullUploadFloat4s { get; private set; }

        // Idle-frame fast path: when EmitAllPaintSources short-circuits because
        // no source repainted, the batcher's instance arrays are byte-identical
        // to the last full frame. The buffer-pool entries still hold that
        // upload, the MPBs still have the right buffer/atlas/viewport bindings,
        // and the only thing the GPU needs is the draw call itself. We cache
        // (buffer, mpb, mesh, texture bindings, chunk-count) per batch chunk on each full frame
        // and re-issue them on idle frames without touching SetData, packing,
        // or even keyword state (those are still set per-batch, but the
        // PackInstances + GraphicsBuffer.SetData cost — ~100KB upload for the
        // match3 demo — disappears).
        //
        // Filter-scope batches don't participate in the per-batch upload
        // cache. When a scope is rendered, inner draws target that scope RT;
        // when a validated scope cache replays, those inner draws are skipped.
        struct CachedDraw {
            public GraphicsBuffer Buffer;
            public MaterialPropertyBlock Mpb;
            public Mesh Mesh;
            public Material Material;
            public UnityEngine.Texture ImageTexture;
            public UnityEngine.Texture AtlasTexture0;
            public UnityEngine.Texture AtlasTexture1;
            public UnityEngine.Texture AtlasTexture2;
            public UnityEngine.Texture AtlasTexture3;
            public int ChunkCount;
        }
        sealed class ActiveFilterScope {
            public int Rt;
            public int X, Y, W, H;
            public int Key;
            public int ContentHash;
            public FilterChain Filters;
            public Weva.Paint.Transform2D ScopeBoxTransform;
            public RenderTargetIdentifier ParentTarget;
            public int ParentWidth, ParentHeight;
            public int ParentOriginX, ParentOriginY;
            public bool ParentIsCamera;
        }
        readonly List<List<CachedDraw>> batchDrawCache = new List<List<CachedDraw>>(16);
        readonly Stack<List<CachedDraw>> batchDrawCacheListPool = new Stack<List<CachedDraw>>();
        readonly List<ActiveFilterScope> activeFilterScopes = new List<ActiveFilterScope>(4);
        readonly List<bool> filterScopeCreatedStack = new List<bool>(4);
        // Per-frame shared-backdrop eligibility (index ↔ BackdropFilterEvents).
        // Reused across frames to avoid per-frame allocation. See
        // ComputeBackdropShareability + the DrainBatches backdrop handlers.
        readonly List<bool> backdropShareable = new List<bool>(8);

        // ── Per-UI-section GPU markers ───────────────────────────────────
        // SRP ProfilingScopes wrap the UI sub-phases so they appear as children
        // of "Weva.BatchedQuadPass" in the Profiler with real per-section GPU
        // ms: Drain = all content + backdrop draw work; Backdrop = the sum of
        // every backdrop-filter scope; Composite = the sRGB intermediate
        // composite-back. The Profiler aggregates the repeated Backdrop marker
        // correctly and is the AUTHORITATIVE read.
        static readonly ProfilingSampler s_gpuDrain = new ProfilingSampler("Weva.UI.Drain");
        static readonly ProfilingSampler s_gpuBackdrop = new ProfilingSampler("Weva.UI.Backdrop");
        static readonly ProfilingSampler s_gpuComposite = new ProfilingSampler("Weva.UI.Composite");
        // Optional console echo of the above (set LogUiGpuTimings = true). Best
        // effort: ProfilingSampler.gpuElapsedTime is flaky in-editor for nested/
        // repeated samplers and async readback (it can return huge/negative
        // junk), so frames outside a sane range are skipped. The Profiler
        // hierarchy is the trustworthy source; this is just a convenience peek.
        public static bool LogUiGpuTimings;
        static int s_gpuLogFrame;
        const double GpuLogSaneMaxMs = 100.0;

        bool drainIdleFrame;
        int currentBatchIndex;
        Vector4 lastDrainViewport;

        // Public counters for profiling / regression tests. IdleFramesServed
        // bumps each time DrainBatches replayed cached draws instead of
        // re-uploading; FullFramesServed bumps when the full pack/upload path
        // ran. A static dashboard can compute the ratio to track the idle
        // hit rate over time.
        public long IdleFramesServed { get; private set; }
        public long FullFramesServed { get; private set; }

        internal static bool ShouldUseCachedFilterScope(bool hasMatchingCachedScope) {
            return hasMatchingCachedScope;
        }

        internal static bool NeedsBackdropCopy(UIBatcher batcher) {
            return batcher != null
                   && (batcher.HasAnyMixBlendMode || batcher.BackdropFilterEvents.Count > 0);
        }

        internal static bool HasDrainableWork(UIBatcher batcher) {
            return batcher != null
                   && (batcher.Batches.Count > 0 || batcher.BackdropFilterEvents.Count > 0);
        }

        internal static int ComputeFilterScopeContentHash(IReadOnlyList<UIQuadBatch> batches, int beginIndex, int endIndex) {
            unchecked {
                int hash = 17;
                if (batches == null) return hash;
                if (beginIndex < 0) beginIndex = 0;
                if (endIndex > batches.Count) endIndex = batches.Count;
                if (endIndex < beginIndex) return hash;
                for (int b = beginIndex; b < endIndex; b++) {
                    var batch = batches[b];
                    hash = (hash * 397) ^ batch.Key.GetHashCode();
                    hash = (hash * 397) ^ batch.InstanceCount;
                    hash = (hash * 397) ^ batch.AtlasIdSlot0;
                    hash = (hash * 397) ^ batch.AtlasIdSlot1;
                    hash = (hash * 397) ^ batch.AtlasIdSlot2;
                    hash = (hash * 397) ^ batch.AtlasIdSlot3;
                    for (int i = 0; i < batch.InstanceCount; i++) {
                        hash = (hash * 397) ^ batch.Instances[i].GetHashCode();
                    }
                }
                return hash;
            }
        }

        public UIRenderGraphPass(BatchedURPRenderBackend backend, UIBatchedResources resources) {
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
            this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
            renderPassEvent = OverlayRenderPassEvent;
            // Sized to the per-draw cap; chunked larger batches reuse the same buffer.
            uploadBuffer = new Vector4[MaxInstancesPerDraw * InstanceFloat4Stride];
            quadMesh = BuildUnitQuad();
        }

        // Borrows a GraphicsBuffer big enough for `requiredFloat4s` entries.
        // Pool entries that are too small get disposed and replaced.
        GraphicsBuffer RentInstanceBuffer(int requiredFloat4s) {
            // Round up to power of two so the pool concentrates on a few sizes.
            int cap = 64;
            while (cap < requiredFloat4s) cap <<= 1;
            GraphicsBuffer found = null;
            // Pop from pool until we find one of suitable size; smaller ones get
            // disposed (the next batch can re-rent at the larger size).
            while (instanceBufferPool.Count > 0) {
                var candidate = instanceBufferPool.Pop();
                if (candidate.count >= cap) {
                    found = candidate;
                    break;
                }
                candidate.Dispose();
            }
            if (found == null) {
                found = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cap, sizeof(float) * 4);
            }
            instanceBuffersInUse.Add(found);
            return found;
        }
        void ResetInstanceBufferPool() {
            for (int i = 0; i < instanceBuffersInUse.Count; i++) {
                instanceBufferPool.Push(instanceBuffersInUse[i]);
            }
            instanceBuffersInUse.Clear();
        }

        public Mesh QuadMesh => quadMesh;

#if UNITY_2023_3_OR_NEWER
        class PassData {
            public UIRenderGraphPass Owner;
            public BatchedURPRenderBackend Backend;
            public int Width;
            public int Height;
            public bool HasStencil;
            public RenderTextureDescriptor CameraTargetDescriptor;
            public TextureHandle ColorTarget;
            public TextureHandle DepthTarget;
            public TextureHandle BackdropSourceTarget;
            // True when the camera renders straight to the backbuffer (no
            // intermediate color texture: no post-processing/HDR/upscaling).
            // Drives the backdrop copy's Y-orientation flag — see
            // ExecuteBatched's backdropCaptureSourceYFlip.
            public bool BackdropSourceIsBackBuffer;
        }

        // Static delegate so SetRenderFunc doesn't allocate a fresh closure
        // every RecordRenderGraph (one per frame). A lambda that closes over
        // nothing CAN be cached by the C# compiler — but only when written
        // as a method group or `static` lambda. Caching it ourselves takes
        // the guesswork out and shows up as a no-alloc setup line.
        static readonly UnityEngine.Rendering.RenderGraphModule.BaseRenderFunc<PassData, UnsafeGraphContext> s_RenderFunc = ExecuteRenderFunc;

        static void ExecuteRenderFunc(PassData data, UnsafeGraphContext ctx) {
            var nativeCmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
            // Bind the camera color/depth targets so the first batch lands
            // on the camera RT. Subsequent filter scopes switch the target
            // with cmd.SetRenderTarget and restore this binding via the
            // same target handle on Pop.
            if (data.DepthTarget.IsValid()) {
                nativeCmd.SetRenderTarget(data.ColorTarget, data.DepthTarget);
            } else {
                nativeCmd.SetRenderTarget(data.ColorTarget);
            }
            RTHandle backdropSourceHandle = data.BackdropSourceTarget.IsValid()
                ? data.BackdropSourceTarget
                : null;
            data.Owner.ExecuteBatched(nativeCmd, data.ColorTarget, data.DepthTarget,
                data.BackdropSourceTarget,
                backdropSourceHandle,
                data.Backend, data.Width, data.Height, data.HasStencil, data.CameraTargetDescriptor,
                data.BackdropSourceIsBackBuffer);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {
            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();
            if (resourceData == null || cameraData == null) return;
            if (!cameraData.resolveFinalTarget) return;
            // Game cameras ONLY. The scene-view (and preview/reflection)
            // cameras share this pass instance and its backend; letting them
            // run it made EmitAllPaintSources adopt THEIR viewport
            // (PrepareForRenderViewport → ApplyViewportSize → a full
            // relayout at a foreign size, executed INSIDE the editor-event
            // render) and thrash the retained batcher against the game
            // camera every editor repaint. Live symptom that pinned it
            // (typing-scrolls-to-top, round 5): pressing a key repainted the
            // scene view via GUIUtility.ProcessEvent, the document re-laid
            // out at the scene-view size, and the page's scroll state was
            // rebuilt/clamped at that geometry — the game view then rendered
            // the page at the top with ZERO scroll-state transitions visible
            // to any breadcrumb on the game-view timeline.
            if (cameraData.cameraType != CameraType.Game) return;

            var color = resourceData.activeColorTexture;
            var depth = resourceData.activeDepthTexture;
            if (!color.IsValid()) return;

            // Switched from AddRasterRenderPass to AddUnsafePass so the
            // execute callback can SetRenderTarget mid-pass — that's the API
            // call RasterCommandBuffer refuses, which is exactly what real
            // RT-based filter compositing needs (allocate a temp RT, draw
            // batches into it, run blur passes ping-pong, composite back
            // to the camera color). With AddRasterRenderPass we'd have to
            // register N separate raster passes per filter scope and thread
            // TextureHandles between them, which is doable but explodes the
            // pass count and makes the schedule fragile (each filter event
            // would need a forward decl in RecordRenderGraph). One unsafe
            // pass is simpler and matches how the legacy URPRenderBackend
            // already runs the FilterPipeline.
            using (var builder = renderGraph.AddUnsafePass<PassData>(PassName, out var passData)) {
                builder.UseTexture(color, AccessFlags.ReadWrite);
                if (depth.IsValid()) {
                    builder.UseTexture(depth, AccessFlags.ReadWrite);
                }
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

                passData.Owner = this;
                passData.Backend = backend;
                passData.Width = cameraData.cameraTargetDescriptor.width;
                passData.Height = cameraData.cameraTargetDescriptor.height;
                passData.HasStencil = HasStencil(cameraData.cameraTargetDescriptor);
                passData.CameraTargetDescriptor = cameraData.cameraTargetDescriptor;
                passData.ColorTarget = color;
                passData.DepthTarget = depth;
                // Use the active color target (backbuffer at AfterRendering)
                // as the backdrop source. It contains post-processed content
                // (tonemapped, color-graded). ExecuteBatched copies it through
                // cmd.Blit before any shader samples it because the backbuffer
                // is not a reliable SetGlobalTexture source.
                passData.BackdropSourceTarget = color;
                // Whether `color` IS the backbuffer (no intermediate texture)
                // decides the orientation the backdrop copy blit produces —
                // backbuffer→RT blits come out top-down (CSS orientation),
                // RT→RT blits preserve the intermediate target's bottom-up
                // layout. ExecuteBatched picks the Y-flip flag from this.
                passData.BackdropSourceIsBackBuffer = resourceData.isActiveTargetBackBuffer;

                builder.SetRenderFunc(s_RenderFunc);
            }
        }
#endif

        // Tracks whether the most recent EmitAllPaintSources call refilled the
        // batcher (false) or short-circuited as an idle frame (true). DrainBatches
        // reads this to decide whether to recycle the per-frame instance-buffer
        // and MPB pools — on idle frames the buffers from the prior frame are
        // still valid and can be re-bound without re-uploading.
        bool lastEmitWasIdle;
        int lastPaintSourceVersion = -1;
        // A-SRGB-COMPOSITE Stage 1: the intermediate-UI-RT -> camera composite
        // re-uses the filter composite pass's RT->camera Y-flip convention.
        // The bottom-up intermediate RT needs a V-flip when composited onto the
        // top-down backbuffer, and none when composited onto another (bottom-up)
        // intermediate camera RT — so the flip is DERIVED from the same
        // isActiveTargetBackBuffer signal the backdrop copy uses (validated
        // live in the editor game view: backbuffer => flip). This XOR override
        // stays as a debug escape hatch for any platform/pipeline config where
        // the derivation is wrong (calibrate from the LIVE game view — offscreen
        // probes lie). Default false: derivation alone is correct in-editor.
        public static bool SrgbCompositeSourceYFlipOverride = false;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // Debug aid: set true (e.g. via reflection from tooling) to log the
        // backdrop-copy / sRGB-composite orientation decision every frame, used
        // to compare the game-view path against offscreen camera.Render captures
        // when bisecting flip miscalibrations (GLASS-PANEL-DARK). OFF by default
        // so a fresh import renders silently (no [Weva] startup chatter).
        public static bool LogBackdropOrientationEveryFrame = false;
#endif

        // Public for headless test use; emits all paint sources, then drains the batcher.
        public void EmitAllPaintSources(int viewportWidth = 0, int viewportHeight = 0) {
            // SnapshotInto fills the scratch buffer in-place — the original
            // Snapshot() allocated a fresh List<> every call. With this swap
            // the steady-state hot path holds the same scratchSources backing
            // array across frames.
            UIPaintSourceRegistry.SnapshotInto(scratchSources);
            if (viewportWidth > 0 && viewportHeight > 0) {
                for (int i = 0; i < scratchSources.Count; i++) {
                    if (scratchSources[i] is IRenderViewportAwarePaintSource viewportAware) {
                        viewportAware.PrepareForRenderViewport(viewportWidth, viewportHeight);
                    }
                }
            }
            int sourceVersion = UIPaintSourceRegistry.Version;
            bool sourceSetChanged = sourceVersion != lastPaintSourceVersion;
            bool anyNeedsRepaint = false;
            for (int i = 0; i < scratchSources.Count; i++) {
                if (scratchSources[i].NeedsRepaint) { anyNeedsRepaint = true; break; }
            }
            // Idle frame: every source confirmed nothing it draws has changed.
            // Leave the batcher's prior-frame batches in place so DrainBatches
            // re-issues the same instance buffers — the GPU draws an identical
            // image to the previous frame without any CPU paint conversion.
            // Saves the box-tree walk, paint-cmd allocation, and per-instance
            // build that EmitPaint normally pays every frame.
            // If the source set changed, rebuild even when every remaining
            // source is idle. Otherwise unregistering/disabling a document can
            // leave its last submitted batches in the retained idle cache.
            if (!sourceSetChanged && !anyNeedsRepaint && backend.Batcher.Batches.Count > 0) {
                lastEmitWasIdle = true;
                return;
            }
            lastEmitWasIdle = false;
            // Named phase so profiler GC/time attribution lands on a readable
            // bucket instead of raw GC.Alloc directly under the pass marker.
            UnityEngine.Profiling.Profiler.BeginSample("Weva.Pass.EmitPaint");
            backend.BeginFrame();
            for (int i = 0; i < scratchSources.Count; i++) {
                scratchSources[i].EmitPaint(backend);
            }
            backend.EndFrame();
            UnityEngine.Profiling.Profiler.EndSample();
            lastPaintSourceVersion = sourceVersion;
        }

#if UNITY_2023_3_OR_NEWER
        Vector4 currentViewport;
        Vector4 drawViewport;
        internal static Vector4 MakeViewportVector(int width, int height) {
            return new Vector4(width, height,
                width > 0 ? 1f / width : 0f,
                height > 0 ? 1f / height : 0f);
        }

        void ExecuteBatched(CommandBuffer cmd, RenderTargetIdentifier colorTarget, RenderTargetIdentifier depthTarget,
                            RenderTargetIdentifier backdropSourceTarget,
                            RTHandle backdropSourceHandle,
                            BatchedURPRenderBackend backend, int width, int height, bool hasStencil,
                            RenderTextureDescriptor cameraTargetDescriptor,
                            bool backdropSourceIsBackBuffer = true) {
            EmitAllPaintSources(width, height);
            currentViewport = MakeViewportVector(width, height);
            drawViewport = currentViewport;
            cmd.SetGlobalVector(IdViewport, currentViewport);
            cmd.SetGlobalVector(IdViewportOrigin, Vector4.zero);

            // A-SRGB-COMPOSITE Stage 1: redirect the whole frame's UI into an
            // always-on intermediate RT, then composite that RT back onto the
            // camera after DrainBatches. Browsers composite CSS layers in gamma
            // space; drawing offscreen first is what lets the fixed-function
            // blend run in a chosen colour space (Stage 2). Stage 1 is
            // deliberately colour-NEUTRAL — the shader still emits the legacy
            // encode and the composite-back is a raw premultiplied passthrough
            // — so toggling the flag should look identical, isolating the RT /
            // orientation / clipping plumbing from the later colour change.
            //
            // Resolve the filter runtime up front (normally lazily resolved
            // below) because the composite-back reuses its filter material; if
            // it isn't ready yet we fall back to drawing straight to the camera.
            if (filterRuntime == null && resources.Resources != null && resources.Resources.IsReady) {
                filterRuntime = new UIRenderGraphFilterRuntime(resources.Resources);
            }
            // Gamma-space compositing is the engine's compositing model: render
            // the whole frame into the intermediate sRGB RT and composite back.
            // The only time we can't is before the filter shader has loaded
            // (the composite reuses its material) — then we draw straight to the
            // linear camera and the encode seam emits raw linear premul. Nothing
            // visible renders on those pre-load frames anyway (no quad shader).
            bool srgbComposite = filterRuntime != null && width > 0 && height > 0;
            // Tell the shader encode seam (Weva_EncodeForTarget) whether this
            // frame's target stores sRGB-encoded premul, every frame so a stale 1
            // can't leak into the linear-camera fallback.
            cmd.SetGlobalFloat(IdWevaSrgbComposite, srgbComposite ? 1f : 0f);
            RenderTargetIdentifier cameraColorTarget = colorTarget;
            RenderTargetIdentifier cameraDepthTarget = depthTarget;
            if (srgbComposite) {
                var uiDesc = cameraTargetDescriptor;
                uiDesc.width = width;
                uiDesc.height = height;
                uiDesc.msaaSamples = 1;
                uiDesc.useMipMap = false;
                uiDesc.autoGenerateMips = false;
                // Colour-only: ForceDisableStencil routes clipping through the
                // in-shader AABB scissor, and the UI shaders are ZTest Always /
                // ZWrite Off, so the intermediate needs no depth/stencil. Passing
                // depthTarget=default below makes every DrainBatches restore bind
                // colour-only.
                uiDesc.depthBufferBits = 0;
                // sRGB=false: the shader writes already-sRGB-ENCODED premul into
                // this RT (Weva_EncodeForTarget), so the GPU must store those
                // values verbatim — no linear<->sRGB hardware conversion on
                // write/read. Keeping the camera's HDR float format (when set)
                // avoids 8-bit banding on the dark gradients; the single
                // quantization happens at the composite back into the camera.
                uiDesc.sRGB = false;
                cmd.GetTemporaryRT(IdWevaUiCompositeRt, uiDesc, FilterMode.Bilinear);
                var uiTarget = new RenderTargetIdentifier(IdWevaUiCompositeRt);
                cmd.SetRenderTarget(uiTarget);
                cmd.ClearRenderTarget(false, true, Color.clear);
                colorTarget = uiTarget;
                depthTarget = default;
            }

            // CSS Compositing 1 §10 (mix-blend-mode) — wire the per-frame
            // backdrop. The active color target is also the UI write target
            // for this pass, so we can't bind it directly as the shader's
            // sample source (URP's RenderGraph rejects read-write-same-
            // resource on a single pass, and backbuffers are not reliable
            // shader resources). When a frame contains `mix-blend-mode` or
            // `backdrop-filter`, we GetTemporaryRT a screen-sized copy, blit
            // the backdrop source into it, and bind that copy as
            // `_WevaBackdrop`. The blit and the temp RT live for the
            // duration of the UI pass; the matching ReleaseTemporaryRT is
            // issued after DrainBatches.
            // Released bool tracked so cleanup matches allocation.
            bool backdropCopyAllocated = false;
            bool needsBackdropCopy = NeedsBackdropCopy(backend.Batcher);
            if (needsBackdropCopy) {
                backdropCopyAllocated = TryBindBackdropCopy(cmd, backdropSourceTarget, backdropSourceHandle,
                    colorTarget, depthTarget,
                    width, height, cameraTargetDescriptor);
            }
            if (!backdropCopyAllocated) {
                // No scope this frame (or alloc failed) — explicitly drop
                // the previous frame's binding so the shader's zero-backdrop
                // fallback path runs. Without this the GPU could keep the
                // last frame's _WevaBackdrop bound and the new frame's
                // pure-Normal content would sample a stale image (visible
                // only on the first frame after the scope disappears, but
                // worth the cheap SetGlobalFloat to keep the contract
                // self-consistent).
                cmd.SetGlobalFloat(IdWevaBackdropAvailable, 0f);
            }
            // Lazily resolve the filter runtime. ShaderResources construction
            // depends on Shader.Find for "Hidden/Weva/Filter" succeeding;
            // we retry every frame until it does so the path comes up live
            // even when the URP/Weva package finishes importing mid-session.
            if (filterRuntime == null && resources.Resources != null && resources.Resources.IsReady) {
                filterRuntime = new UIRenderGraphFilterRuntime(resources.Resources);
            }
            // Recycle per-draw filter pools so the next frame's filter
            // scopes start with fresh Mesh/MPB rentals. Each filter pass
            // chunk needs its own Mesh + MPB because cmd.DrawMesh
            // captures both by reference at record time — sharing one
            // instance lets later DrawQuadAtPx calls overwrite earlier
            // ones' data before the GPU draws.
            filterRuntime?.ResetFrame();
            // When the backdrop copy was allocated, use it as the source
            // for ApplyBackdropAndComposite. cmd.Blit can read from the
            // backbuffer (which has post-processed content) whereas the
            // composite pass's cb.SetGlobalTexture cannot reliably bind
            // the backbuffer as a shader resource. The copy path can leave
            // the readable texture vertically inverted relative to CSS
            // screen coordinates, but the correction must flip inside the
            // same capture rect; flipping against the full source texture
            // samples the wrong part of the screen.
            var effectiveBackdropSource = backdropCopyAllocated
                ? new RenderTargetIdentifier(IdWevaBackdropCopyRt)
                : backdropSourceTarget;
            // The copy blit's output orientation depends on the SOURCE target
            // type AND on editor-vs-player, so one constant can't be right
            // for every configuration:
            //   - PLAYER build, camera renders straight to the true BACKBUFFER
            //     (no post-processing/HDR — the weva sample project):
            //     backbuffer→RT blit comes out top-down, i.e. already in CSS
            //     orientation → no flip. The old unconditional flip rendered
            //     the quests `.log` backdrop-filter upside-down in builds.
            //   - camera renders to an INTERMEDIATE color RT (post-processing
            //     on): RT→RT blits preserve the intermediate
            //     target's bottom-up texel layout → the filter capture must
            //     flip. Hardcoding no-flip inverted a real main-menu blur.
            //   - OFFSCREEN camera.Render (camera.targetTexture set, e.g.
            //     tooling captures): URP still reports
            //     isActiveTargetBackBuffer=True but the copy comes out
            //     BOTTOM-UP — a no-op blur(0.01px) probe showed every
            //     backdrop-filter panel sampling its backdrop Y-mirrored
            //     (2026-06-07). The EDITOR GAME VIEW does NOT share this:
            //     forcing the flip for all editor "backbuffer" cameras
            //     mirrored the game view's backdrops (user-visible
            //     regression, reverted same day). Offscreen captures are a
            //     tooling-only path, so the game-view-correct choice wins;
            //     orientation probes must NOT be calibrated from
            //     camera.targetTexture captures.
            // resourceData.isActiveTargetBackBuffer is the authoritative
            // signal for which case this camera is in.
            //
            // A-SRGB-COMPOSITE: the UI renders into the intermediate RT, so the
            // intermediate RT, the live surface backdrop-filter captures
            // (cmd.Blit(colorTarget, ...) in DrainBatches) is that RT, not the
            // backbuffer — so the capture preserves the RT's bottom-up layout
            // and must flip, exactly like the "camera renders to an
            // intermediate color RT" case above, regardless of whether the
            // CAMERA targets the backbuffer. (The mix-blend page-backdrop also
            // rides this flag; under srgbComposite that is reworked in Stage 3 —
            // glass.html, the validation case, has no page-backdrop mix-blend.)
            bool effectiveSourceIsBackBuffer = backdropSourceIsBackBuffer && !srgbComposite;
            bool backdropCaptureSourceYFlip = backdropCopyAllocated && !effectiveSourceIsBackBuffer;
            // Mirror the orientation to the mix-blend sampler: the SAME copy
            // feeds `_WevaBackdrop`, so Weva_SampleBackdropPremul needs the
            // same flip decision the filter capture applies (A-MIXBLEND-YFLIP).
            cmd.SetGlobalFloat(IdWevaBackdropYFlip, backdropCaptureSourceYFlip ? 1f : 0f);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (LogBackdropOrientationEveryFrame && backdropCopyAllocated) {
                Debug.Log($"[Weva] backdrop copy orientation: isBackBuffer={backdropSourceIsBackBuffer} " +
                          $"useScaling={(backdropSourceHandle != null ? backdropSourceHandle.useScaling.ToString() : "null")} " +
                          $"captureYFlip={backdropCaptureSourceYFlip}");
            }
#endif
            if (LogUiGpuTimings) {
                s_gpuDrain.enableRecording = true;
                s_gpuBackdrop.enableRecording = true;
                s_gpuComposite.enableRecording = true;
            }
            UnityEngine.Profiling.Profiler.BeginSample("Weva.Pass.Drain");
            using (new ProfilingScope(cmd, s_gpuDrain)) {
                DrainBatches(cmd, colorTarget, depthTarget, effectiveBackdropSource, backdropCaptureSourceYFlip,
                    backend.Batcher, width, height, hasStencil, srgbComposite);
            }
            UnityEngine.Profiling.Profiler.EndSample();
            if (backdropCopyAllocated) {
                // Release the temp RT so RenderGraph reclaims its memory at
                // pass end. The global texture binding stays "valid" on the
                // GPU for this frame's submission window; the next frame
                // re-binds or zeros the available flag.
                cmd.ReleaseTemporaryRT(IdWevaBackdropCopyRt);
            }

            // A-SRGB-COMPOSITE Stage 1: composite the intermediate UI RT back
            // onto the camera target. DrainBatches may have left the viewport
            // globals at the last filter scope's values, so restore the full
            // frame viewport first. The composite uses the proven filter
            // composite pass (premultiplied-over); Stage 1 passes encodeSrgb:
            // false so it's a raw passthrough (colour-neutral).
            if (srgbComposite) {
                cmd.SetGlobalVector(IdViewport, currentViewport);
                cmd.SetGlobalVector(IdViewportOrigin, Vector4.zero);
                if (cameraDepthTarget.Equals(default(RenderTargetIdentifier))) {
                    cmd.SetRenderTarget(cameraColorTarget);
                } else {
                    cmd.SetRenderTarget(cameraColorTarget, cameraDepthTarget);
                }
                // Bottom-up intermediate RT -> top-down backbuffer needs a
                // V-flip; -> another bottom-up camera RT does not. Derive from
                // the backbuffer signal, XOR the debug override.
                bool srgbCompositeYFlip = backdropSourceIsBackBuffer ^ SrgbCompositeSourceYFlipOverride;
                // The intermediate holds sRGB-encoded premul. In a Linear
                // project decode it back to linear for the linear camera; in a
                // Gamma project the camera already holds gamma values so the
                // encoded result passes through unchanged.
                bool decodeSrgb = QualitySettings.activeColorSpace == ColorSpace.Linear;
                using (new ProfilingScope(cmd, s_gpuComposite)) {
                    filterRuntime.CompositeUiRtToTarget(cmd, IdWevaUiCompositeRt, width, height,
                        sourceYFlip: srgbCompositeYFlip, encodeSrgb: false, decodeSrgb: decodeSrgb);
                }
                cmd.ReleaseTemporaryRT(IdWevaUiCompositeRt);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (LogBackdropOrientationEveryFrame) {
                    Debug.Log($"[Weva] A-SRGB-COMPOSITE intermediate composite: " +
                              $"isBackBuffer={backdropSourceIsBackBuffer} yFlip={srgbCompositeYFlip} " +
                              $"size={width}x{height}");
                }
#endif
            }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (LogUiGpuTimings && (s_gpuLogFrame++ % 15) == 0) {
                double drain = s_gpuDrain.gpuElapsedTime;
                double bd = s_gpuBackdrop.gpuElapsedTime;
                double cp = s_gpuComposite.gpuElapsedTime;
                // Skip frames where the async GPU readback returned junk.
                bool sane = drain >= 0 && bd >= 0 && cp >= 0
                    && drain < GpuLogSaneMaxMs && cp < GpuLogSaneMaxMs && bd <= drain + 0.05;
                if (sane) {
                    Debug.Log($"[WevaGpu] UI GPU ms — total(drain)={drain:F3} backdrop={bd:F3} " +
                              $"content≈{(drain - bd):F3} composite={cp:F3} " +
                              "(Profiler ▸ Weva.BatchedQuadPass is authoritative)");
                }
            }
#endif
        }

        // Allocates a screen-sized temp RT, blits `backdropSourceTarget`
        // into it, and binds it to the `_WevaBackdrop` shader global.
        // Returns true on success. cmd.Blit transiently changes the
        // active render target, so we restore the camera color (+ depth)
        // afterwards — DrainBatches assumes the camera target is active
        // when the first batch issues. Explicit Load actions preserve
        // the contents URP has already painted into the camera target
        // for this frame; the default Blit re-attach can discard them.
        bool TryBindBackdropCopy(CommandBuffer cmd, RenderTargetIdentifier backdropSourceTarget,
                                  RTHandle backdropSourceHandle,
                                  RenderTargetIdentifier colorTarget, RenderTargetIdentifier depthTarget,
                                  int width, int height, RenderTextureDescriptor cameraTargetDescriptor) {
            if (width <= 0 || height <= 0) return false;
            var desc = cameraTargetDescriptor;
            desc.width = width;
            desc.height = height;
            desc.msaaSamples = 1;
            desc.useMipMap = false;
            desc.autoGenerateMips = false;
            desc.depthBufferBits = 0;
            cmd.GetTemporaryRT(IdWevaBackdropCopyRt, desc, FilterMode.Bilinear);
            var copyTarget = new RenderTargetIdentifier(IdWevaBackdropCopyRt);
            if (backdropSourceHandle != null && backdropSourceHandle.useScaling) {
                cmd.SetRenderTarget(copyTarget);
                cmd.SetViewport(new UnityEngine.Rect(0f, 0f, width, height));
                var scale = backdropSourceHandle.rtHandleProperties.rtHandleScale;
                Blitter.BlitTexture(cmd, backdropSourceHandle, new Vector4(scale.x, scale.y, 0f, 0f), 0f, false);
            } else {
                cmd.Blit(backdropSourceTarget, copyTarget);
            }
            cmd.SetGlobalTexture(IdWevaBackdrop, copyTarget);
            cmd.SetGlobalFloat(IdWevaBackdropAvailable, 1f);
            // Restore the camera color (+ depth) target so the first UI
            // batch lands where ExecuteRenderFunc originally aimed. Without
            // this restore the Blit leaves the temp RT bound and the
            // overlay would paint into a discarded buffer.
            if (!depthTarget.Equals(default(RenderTargetIdentifier))) {
                cmd.SetRenderTarget(colorTarget,
                    RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                    depthTarget,
                    RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            } else {
                cmd.SetRenderTarget(colorTarget,
                    RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            }
            return true;
        }

        // Fills `shareable[i]` = the i-th backdrop scope's INPUT region is
        // disjoint from EVERY earlier scope's border box (paint order). An
        // eligible scope's backdrop is the pristine scene shared by all such
        // scopes, so it can crop from a single whole-screen blur; a scope that
        // overlaps an earlier one (a nested glass child over its glass parent)
        // must keep the per-panel path. The candidate's rect is inflated by
        // its own filter chain (rendering audit Gap1/F5): a blur pulls ~3σ of
        // halo from OUTSIDE the border box, so content there feeds the
        // blurred values INSIDE it — an earlier panel's composite (or any
        // post-capture batch) landing in the halo makes the pristine capture
        // wrong at the crop edges. Earlier scopes stay border-box: that is
        // the region their composite actually painted. Returns the
        // eligible-scope count.
        internal static int ComputeBackdropShareability(
                IReadOnlyList<BackdropFilterEvent> events, int viewportW, int viewportH,
                List<bool> shareable) {
            return ComputeBackdropShareability(events, null, viewportW, viewportH, shareable);
        }

        internal static int ComputeBackdropShareability(
                IReadOnlyList<BackdropFilterEvent> events, IReadOnlyList<UIQuadBatch> batches,
                int viewportW, int viewportH, List<bool> shareable) {
            shareable.Clear();
            int count = 0;
            int captureBatchIndex = -1; // batch index of the FIRST eligible event (= capture time)
            for (int i = 0; i < events.Count; i++) {
                // Filters-inflated: the region the scope READS (border box +
                // blur halo), not just the region it draws.
                var ri = UIRenderGraphFilterRuntime.ComputeRtRect(
                    events[i].Bounds, events[i].Transform, events[i].Filters, viewportW, viewportH);
                bool ok = ri.W > 0 && ri.H > 0;
                for (int j = 0; j < i && ok; j++) {
                    var rj = UIRenderGraphFilterRuntime.ComputeRtRect(
                        events[j].Bounds, events[j].Transform, FilterChain.Empty, viewportW, viewportH);
                    if (BackdropRectsOverlap(ri, rj)) ok = false;
                }
                // The shared blur is built from ONE capture taken at the first
                // eligible event. A later panel may reuse it only if no CONTENT
                // drawn after that capture underlies the panel — otherwise its
                // crop shows a screen state that predates its own backdrop
                // (in-editor find: a later section's glass panel blurred the
                // page's bare background instead of the gradient painted
                // between the capture and its event). Batch AABBs are
                // conservative (UIQuadBatch.PixelBounds); a null batch list
                // (legacy callers/tests) skips the check.
                if (ok && batches != null && captureBatchIndex >= 0) {
                    int upTo = events[i].BatchIndex;
                    if (upTo > batches.Count) upTo = batches.Count;
                    for (int b = captureBatchIndex; b < upTo && ok; b++) {
                        var pb = batches[b].PixelBounds;
                        if (pb.x < ri.X + ri.W && ri.X < pb.z
                            && pb.y < ri.Y + ri.H && ri.Y < pb.w) {
                            ok = false;
                        }
                    }
                }
                if (ok && captureBatchIndex < 0) captureBatchIndex = events[i].BatchIndex;
                shareable.Add(ok);
                if (ok) count++;
            }
            return count;
        }

        internal static bool BackdropRectsOverlap(
                (int X, int Y, int W, int H) a, (int X, int Y, int W, int H) b) {
            return a.X < b.X + b.W && b.X < a.X + a.W
                && a.Y < b.Y + b.H && b.Y < a.Y + a.H;
        }

        // Rebinds the camera color (and depth, when present) after a backdrop
        // step temporarily switched render targets — shared by both backdrop
        // event handlers.
        void RestoreBackdropTarget(CommandBuffer cmd, RenderTargetIdentifier colorTarget,
                                   RenderTargetIdentifier depthTarget) {
            if (!depthTarget.Equals(default(RenderTargetIdentifier))) {
                cmd.SetRenderTarget(colorTarget,
                    RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                    depthTarget,
                    RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            } else {
                cmd.SetRenderTarget(colorTarget,
                    RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            }
        }

        // Handles one backdrop-filter event. `useShared` routes eligible
        // (disjoint-from-earlier) scopes through the shared whole-screen blur;
        // the rest take the original per-panel refresh + ApplyBackdropAndComposite
        // path. On the first shared event of the frame the pristine scene is
        // captured once and every eligible chain's blur is built from it.
        void HandleBackdropEvent(CommandBuffer cmd, in BackdropFilterEvent be, bool useShared,
                                 ref bool sharedBuilt,
                                 IReadOnlyList<BackdropFilterEvent> bdEvents, List<bool> shareable,
                                 RenderTargetIdentifier colorTarget, RenderTargetIdentifier depthTarget,
                                 RenderTargetIdentifier backdropSourceTarget, bool backdropSourceYFlip,
                                 int viewportW, int viewportH, bool srgbComposite, Vector4 currentViewport) {
            if (useShared) {
                if (!sharedBuilt) {
                    // Capture the pristine scene once, then build every eligible
                    // chain from it so no panel content bleeds into another's blur.
                    cmd.Blit(colorTarget, backdropSourceTarget);
                    RestoreBackdropTarget(cmd, colorTarget, depthTarget);
                    for (int k = 0; k < bdEvents.Count; k++) {
                        if (!shareable[k]) continue;
                        filterRuntime.BuildSharedBackdropBlur(cmd, backdropSourceTarget, bdEvents[k].Filters,
                            backdropSourceYFlip, viewportW, viewportH, IdViewportOrigin, IdViewport, srgbComposite);
                    }
                    sharedBuilt = true;
                }
                filterRuntime.CompositeSharedBackdrop(cmd, colorTarget, be.Bounds, be.Radii, be.Filters,
                    be.Transform, viewportW, viewportH, IdViewportOrigin, IdViewport, srgbComposite);
                RestoreBackdropTarget(cmd, colorTarget, depthTarget);
                cmd.SetGlobalVector(IdViewport, currentViewport);
                cmd.SetGlobalVector(IdViewportOrigin, Vector4.zero);
                return;
            }
            // Per-panel path (CSS Filter Effects L2 §3): refresh the backdrop copy
            // from the current colorTarget so the sample reflects everything
            // painted up to (not including) this element, then capture+filter the
            // panel's own inflated region. The no-copy fallback leaves the two
            // targets equal and skips the blit.
            if (!backdropSourceTarget.Equals(colorTarget)) {
                cmd.Blit(colorTarget, backdropSourceTarget);
                RestoreBackdropTarget(cmd, colorTarget, depthTarget);
            }
            filterRuntime.ApplyBackdropAndComposite(cmd, backdropSourceTarget, colorTarget,
                be.Bounds, be.Radii, be.Filters, be.Transform, backdropSourceYFlip,
                viewportW, viewportH, IdViewportOrigin, IdViewport, srgbComposite);
            RestoreBackdropTarget(cmd, colorTarget, depthTarget);
            cmd.SetGlobalVector(IdViewport, currentViewport);
            cmd.SetGlobalVector(IdViewportOrigin, Vector4.zero);
        }

        void DrainBatches(CommandBuffer cmd, RenderTargetIdentifier colorTarget,
                          RenderTargetIdentifier depthTarget,
                          RenderTargetIdentifier backdropSourceTarget,
                          bool backdropSourceYFlip,
                          UIBatcher batcher,
                          int viewportW, int viewportH, bool hasStencil,
                          bool srgbComposite = false) {
            if (!HasDrainableWork(batcher)) return;
            // Idle-frame skip: when EmitAllPaintSources confirmed nothing
            // changed AND we have a valid per-batch draw cache from the last
            // full frame AND the viewport hasn't changed (a resize would
            // leave the cached MPBs' _WevaViewport vector stale), keep
            // the buffer/MPB pools intact so prior uploads stay GPU-visible.
            // Otherwise recycle and rebuild the cache from scratch. The
            // cache validity check (Count matches) also catches the
            // boundary case where the prior full frame had a different
            // number of batches.
            bool viewportSame = lastDrainViewport == currentViewport;
            drainIdleFrame = lastEmitWasIdle
                && batchDrawCache.Count == batcher.Batches.Count
                && viewportSame
                && AreCachedImageBindingsValid(batcher)
                && AreCachedTextAtlasBindingsValid(batcher);
            if (!drainIdleFrame) {
                ResetMpbPool();
                ResetInstanceBufferPool();
                ClearBatchDrawCache(batcher.Batches.Count);
                FullFramesServed++;
                // Invalidate cached filter-scope RTs at the start of every full
                // (non-idle) frame. The batch list has changed, so a scope's
                // captured content may be stale; reusing the cached RT composites
                // a ghost of the previous content (e.g. story-bubble's drop-shadow
                // + text-shadow scopes leaving a faint, offset copy of the line
                // under animation). Idle frames intentionally skip this so a
                // static page keeps reusing the cached scope RTs. This call was
                // documented as part of the design but was never wired up.
                filterRuntime?.InvalidateScopeCaches();
                // Reset chunk seq index every full frame; chunk uploads
                // walk this in submission order, deciding partial vs full
                // upload per slot.
                chunkSeqIdx = 0;
                PartialUploadFloat4s = 0;
                FullUploadFloat4s = 0;
            } else {
                IdleFramesServed++;
            }
            lastDrainViewport = currentViewport;
            ReturnStencilMeshes();
            int total = batcher.Batches.Count;
            int evtCursor = 0;
            var evts = batcher.Clips.Events;
            // ForceDisableStencil routes every clip through per-fragment AABB
            // rejection (ClipRect slot 13). The stencil write quads are then
            // pure overhead — they paint to a stencil buffer the content
            // material reads as Comp=Always anyway. Skipping them here cut
            // 64 draw calls in the demo (one per clip Push/Pop) without any
            // visual change. Materials still bake _StencilComp=Always.
            bool issueStencilEvents = hasStencil && !UIBatchedResources.ForceDisableStencil;

            // Filter scope tracking. Walk filterEvents in parallel with
            // batches: when we hit a Push, allocate an RT and redirect
            // drawing; when we hit its Pop, filter that RT and composite it
            // back into its parent target. Scopes can nest: text-shadow blur
            // inside an animated `filter: brightness(...)` element is the
            // common case in match3-endgame.
            //
            // Filter-scope cache path: when the persistent scope RT matches
            // the current BeginIndex, rect, filter chain, and inner content
            // hash, skip BeginScope plus every content batch inside the
            // scope. The matching Pop event composites the cached output.
            // BeginIndex by itself is not identity: hover can insert a new
            // earlier scope and collide with a previous frame's key.
            var fEvents = batcher.FilterEvents;
            int fEvtCursor = 0;
            var bdEvents = batcher.BackdropFilterEvents;
            int bdEvtCursor = 0;
            int filterDepth = 0;
            activeFilterScopes.Clear();
            filterScopeCreatedStack.Clear();
            // Cached scopes also need a per-frame ScopeBoxTransform to
            // composite at the current position — separate var because
            // active scopes track open-scope state and get popped as they
            // complete.
            Weva.Paint.Transform2D idleSkipScopeBoxTransform = Weva.Paint.Transform2D.Identity;
            bool filterRuntimeReady = filterRuntime != null && filterRuntime.IsReady;
            cmd.SetGlobalFloat(UIRenderGraphFilterRuntime.IdRawFilterOutput, 0f);
            // Shared backdrop blur eligibility (perf): when ≥2 backdrop scopes
            // are mutually-disjoint-from-earlier, the same scene blur serves
            // them all — blur once per chain instead of once per panel. Nested
            // glass (a child over its glass parent) overlaps an earlier scope
            // and stays on the per-panel path. backdropShareable[i] mirrors
            // bdEvents[i]; bdSharedBuilt latches the one-time scene capture.
            bool bdSharedActive = false;
            bool bdSharedBuilt = false;
            if (filterRuntimeReady && UIRenderGraphFilterRuntime.EnableSharedBackdropBlur
                && bdEvents.Count > 1 && !backdropSourceTarget.Equals(colorTarget)
                && ComputeBackdropShareability(bdEvents, batcher.Batches, viewportW, viewportH, backdropShareable) >= 2) {
                bdSharedActive = true;
                filterRuntime.NextSharedBackdropFrame();
            }
            // When > 0 we're inside an idle-cached scope whose content batches
            // we're skipping (no draw, no buffer upload, no temp RT alloc).
            // The Pop handler turns this off when filterDepth reaches the
            // matching outer depth. Note: nested scopes inside a cached one
            // collapse — we treat the entire outer scope as opaque cached
            // content per the v1 nesting contract.
            int filterIdleSkipDepth = 0;
            int filterIdleSkipScopeKey = 0;
            // Filter scope caches are keyed by BeginIndex plus rect/filter
            // identity and a hash of the inner batches. BeginIndex alone can
            // collide during a full repaint that inserts/removes a filter
            // scope. Match3's Hint hover is the canonical case: adding
            // filter: brightness(...) before the aurora blur reused the
            // aurora's cached RT and skipped drawing the button.

            for (int b = 0; b < total; b++) {
                while (bdEvtCursor < bdEvents.Count && bdEvents[bdEvtCursor].BatchIndex == b) {
                    if (filterRuntimeReady) {
                        bool useShared = bdSharedActive && backdropShareable[bdEvtCursor];
                        using (new ProfilingScope(cmd, s_gpuBackdrop)) {
                            HandleBackdropEvent(cmd, bdEvents[bdEvtCursor], useShared, ref bdSharedBuilt,
                                bdEvents, backdropShareable, colorTarget, depthTarget, backdropSourceTarget,
                                backdropSourceYFlip, viewportW, viewportH, srgbComposite, currentViewport);
                        }
                    }
                    bdEvtCursor++;
                }
                // Before issuing this batch, check for filter Push events
                // whose BeginIndex == b. Multiple pushes at the same index
                // are possible (depth>0 nesting starts immediately); each is
                // visited in submission order.
                while (fEvtCursor < fEvents.Count) {
                    var fe = fEvents[fEvtCursor];
                    if (fe.Kind == FilterEventKind.Push && fe.BeginIndex == b) {
                        bool createdScope = false;
                        if (filterIdleSkipDepth == 0 && filterRuntimeReady) {
                            // scopeKey starts from the Push's BeginIndex for
                            // fast lookup, but replay is allowed only after
                            // the rect/filter/content identity checks below.
                            // The +1 offset keeps 0 as "no caching" since
                            // EndScopeAndComposite treats scopeKey=0 as
                            // disabled.
                            bool topLevelScope = activeFilterScopes.Count == 0;
                            int scopeKey = topLevelScope ? fe.BeginIndex + 1 : 0;
                            var (sx, sy, sw, sh) = UIRenderGraphFilterRuntime.ComputeRtRect(
                                fe.Bounds, fe.Transform, fe.Filters, viewportW, viewportH);
                            if (sw > 0 && sh > 0) {
                                int contentHash = topLevelScope
                                    ? ComputeFilterScopeContentHash(batcher.Batches, fe.BeginIndex, fe.EndIndex)
                                    : 0;
                                if (topLevelScope && ShouldUseCachedFilterScope(
                                        filterRuntime.HasCachedScope(scopeKey, sx, sy, sw, sh, fe.Filters, contentHash))) {
                                    // Cached path: skip BeginScope and every
                                    // batch inside the scope; the matching Pop
                                    // composites the cached RT.
                                    filterIdleSkipDepth = filterDepth + 1;
                                    filterIdleSkipScopeKey = scopeKey;
                                    idleSkipScopeBoxTransform = fe.ScopeBoxTransform;
                                    filterScopeCreatedStack.Add(false);
                                    filterDepth++;
                                    fEvtCursor++;
                                    continue;
                                }
                                RenderTargetIdentifier parentTarget;
                                int parentWidth, parentHeight, parentOriginX, parentOriginY;
                                bool parentIsCamera;
                                if (activeFilterScopes.Count > 0) {
                                    var parent = activeFilterScopes[activeFilterScopes.Count - 1];
                                    parentTarget = new RenderTargetIdentifier(parent.Rt);
                                    parentWidth = parent.W;
                                    parentHeight = parent.H;
                                    parentOriginX = parent.X;
                                    parentOriginY = parent.Y;
                                    parentIsCamera = false;
                                } else {
                                    parentTarget = colorTarget;
                                    parentWidth = viewportW;
                                    parentHeight = viewportH;
                                    parentOriginX = 0;
                                    parentOriginY = 0;
                                    parentIsCamera = true;
                                }
                                int scopeRt = filterRuntime.BeginScope(cmd, sx, sy, sw, sh,
                                    IdViewportOrigin, IdViewport);
                                createdScope = true;
                                activeFilterScopes.Add(new ActiveFilterScope {
                                    Rt = scopeRt,
                                    X = sx,
                                    Y = sy,
                                    W = sw,
                                    H = sh,
                                    Key = scopeKey,
                                    ContentHash = contentHash,
                                    Filters = fe.Filters,
                                    ScopeBoxTransform = fe.ScopeBoxTransform,
                                    ParentTarget = parentTarget,
                                    ParentWidth = parentWidth,
                                    ParentHeight = parentHeight,
                                    ParentOriginX = parentOriginX,
                                    ParentOriginY = parentOriginY,
                                    ParentIsCamera = parentIsCamera
                                });
                                drawViewport = MakeViewportVector(sw, sh);
                            }
                        }
                        filterScopeCreatedStack.Add(createdScope);
                        filterDepth++;
                        fEvtCursor++;
                        continue;
                    }
                    if (fe.Kind == FilterEventKind.Pop && fe.BeginIndex <= b && fe.EndIndex == b) {
                        // Top-level scope is ending right before this batch
                        // (the scope contained batches [BeginIndex, EndIndex)).
                        bool createdScope = false;
                        if (filterScopeCreatedStack.Count > 0) {
                            int last = filterScopeCreatedStack.Count - 1;
                            createdScope = filterScopeCreatedStack[last];
                            filterScopeCreatedStack.RemoveAt(last);
                        }
                        filterDepth--;
                        if (filterIdleSkipDepth > 0 && filterDepth < filterIdleSkipDepth) {
                            // Composite the cached RT and stop skipping.
                            filterRuntime.CompositeCachedScope(cmd,
                                filterIdleSkipScopeKey,
                                colorTarget,
                                viewportW, viewportH,
                                IdViewportOrigin, IdViewport,
                                idleSkipScopeBoxTransform);
                            // Re-bind main color target so subsequent batches
                            // see the same attachments. Composite already
                            // set the target; the Load action keeps prior
                            // contents (composite output + everything else
                            // painted so far) intact for the next batch.
                            if (!depthTarget.Equals(default(RenderTargetIdentifier))) {
                                cmd.SetRenderTarget(colorTarget,
                                    RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                                    depthTarget,
                                    RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                            } else {
                                cmd.SetRenderTarget(colorTarget,
                                    RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                            }
                            cmd.SetGlobalVector(IdViewport, currentViewport);
                            cmd.SetGlobalVector(IdViewportOrigin, Vector4.zero);
                            filterIdleSkipDepth = 0;
                            filterIdleSkipScopeKey = 0;
                            fEvtCursor++;
                            continue;
                        }
                        if (createdScope && activeFilterScopes.Count > 0 && filterRuntimeReady) {
                            var scope = activeFilterScopes[activeFilterScopes.Count - 1];
                            activeFilterScopes.RemoveAt(activeFilterScopes.Count - 1);
                            cmd.SetGlobalFloat(UIRenderGraphFilterRuntime.IdRawFilterOutput, 0f);
                            filterRuntime.EndScopeAndComposite(cmd, scope.Rt,
                                scope.X, scope.Y, scope.W, scope.H,
                                scope.Filters,
                                scope.ParentTarget,
                                scope.ParentWidth, scope.ParentHeight,
                                IdViewportOrigin, IdViewport,
                                scope.ScopeBoxTransform,
                                scope.Key,
                                scope.ContentHash,
                                scope.ParentOriginX,
                                scope.ParentOriginY,
                                scope.ParentIsCamera);
                            // Re-bind the main color target with depth so any
                            // subsequent stencil-write or content batch sees
                            // the same attachments as the pass started with.
                            // Explicit Load actions — the default overload
                            // discards prior contents under URP RenderGraph
                            // unsafe passes, which would wipe everything
                            // we'd already painted into the camera target
                            // before this filter scope ran.
                            if (scope.ParentIsCamera) {
                                if (!depthTarget.Equals(default(RenderTargetIdentifier))) {
                                    cmd.SetRenderTarget(colorTarget,
                                        RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                                        depthTarget,
                                        RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                                } else {
                                    cmd.SetRenderTarget(colorTarget,
                                        RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                                }
                                cmd.SetGlobalVector(IdViewport, currentViewport);
                                cmd.SetGlobalVector(IdViewportOrigin, Vector4.zero);
                                drawViewport = currentViewport;
                            } else {
                                cmd.SetRenderTarget(scope.ParentTarget,
                                    RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                                cmd.SetGlobalVector(IdViewport, MakeViewportVector(scope.ParentWidth, scope.ParentHeight));
                                cmd.SetGlobalVector(IdViewportOrigin, new Vector4(scope.ParentOriginX, scope.ParentOriginY, 0f, 0f));
                                drawViewport = MakeViewportVector(scope.ParentWidth, scope.ParentHeight);
                            }
                        }
                        fEvtCursor++;
                        continue;
                    }
                    break;
                }

                var batch = batcher.Batches[b];
                // Drain ALL stencil events recorded before this batch flushed —
                // not just enough to match the ref. Sibling clip-pushers can
                // produce two consecutive batches at the same ref separated by
                // a Pop+Push in the event log; without draining those events,
                // the second batch would render against the first sibling's
                // mask and disappear (minimap content, chat-log lines, bar
                // fills — all clipped to the wrong region).
                if (issueStencilEvents) {
                    while (evtCursor < batch.EventIndexBefore && evtCursor < evts.Count) {
                        var e = evts[evtCursor++];
                        IssueStencilEvent(cmd, e);
                    }
                }
                currentBatchIndex = b;
                if (filterIdleSkipDepth > 0) {
                    // Inside an idle-cached scope — every batch is replayed
                    // from the persistent cache RT, so the actual draws are
                    // skipped entirely.
                    continue;
                }
                // B24 v1 — CSS Compositing 1 §10. When this batch contains
                // page-backdrop mix-blend-mode instances, refresh _WevaBackdrop
                // from the current colorTarget before drawing so the blend
                // formulas sample a destination that includes all UI painted
                // earlier in the same frame (body bg, sibling panels, etc.).
                // The initial TryBindBackdropCopy only captured the pre-UI
                // framebuffer; without this per-batch refresh a blended element
                // over same-frame UI blends against the camera clear instead.
                //
                // Only refresh when the backdrop copy RT is allocated
                // (!backdropSourceTarget.Equals(colorTarget) — the no-copy
                // fallback path leaves them equal and skips the Blit).
                // Same Blit + restore-render-target pattern as the
                // backdrop-filter event handler above (CSS Compositing §10 ≡
                // Filter Effects L2 §3: identical "everything rendered prior"
                // backdrop definition). Consecutive flagged batches each get
                // their own refresh — an earlier blended element is part of
                // the next one's backdrop.
                if (batch.NeedsBackdropRefresh
                    && !backdropSourceTarget.Equals(colorTarget)) {
                    cmd.Blit(colorTarget, backdropSourceTarget);
                    if (!depthTarget.Equals(default(RenderTargetIdentifier))) {
                        cmd.SetRenderTarget(colorTarget,
                            RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                            depthTarget,
                            RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                    } else {
                        cmd.SetRenderTarget(colorTarget,
                            RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                    }
                }
                // Filter-scope batches don't get cached — the temp RT they
                // draw into is freshly allocated each frame, so the inner
                // draw must always pack and upload. Only the outermost
                // (filterDepth == 0) batches participate in the idle-frame
                // cache; that already covers most of the demo's batches
                // (match3 has 13/14 batches outside the aurora filter).
                bool drawingIntoFilterRt = activeFilterScopes.Count > 0;
                cmd.SetGlobalFloat(UIRenderGraphFilterRuntime.IdRawFilterOutput, drawingIntoFilterRt ? 1f : 0f);
                IssueBatch(cmd, batch, allowIdleCache: filterDepth == 0);
                if (drawingIntoFilterRt) cmd.SetGlobalFloat(UIRenderGraphFilterRuntime.IdRawFilterOutput, 0f);
            }
            while (bdEvtCursor < bdEvents.Count && bdEvents[bdEvtCursor].BatchIndex == total) {
                if (filterRuntimeReady) {
                    bool useShared = bdSharedActive && backdropShareable[bdEvtCursor];
                    using (new ProfilingScope(cmd, s_gpuBackdrop)) {
                        HandleBackdropEvent(cmd, bdEvents[bdEvtCursor], useShared, ref bdSharedBuilt,
                            bdEvents, backdropShareable, colorTarget, depthTarget, backdropSourceTarget,
                            backdropSourceYFlip, viewportW, viewportH, srgbComposite, currentViewport);
                    }
                }
                bdEvtCursor++;
            }
            // Drain any trailing filter Pop events whose EndIndex == total.
            // The scheduling loop runs only b < total, so a Pop placed after
            // the very last batch wouldn't otherwise be reached.
            while (fEvtCursor < fEvents.Count) {
                var fe = fEvents[fEvtCursor];
                if (fe.Kind == FilterEventKind.Pop && fe.EndIndex == total) {
                    bool createdScope = false;
                    if (filterScopeCreatedStack.Count > 0) {
                        int last = filterScopeCreatedStack.Count - 1;
                        createdScope = filterScopeCreatedStack[last];
                        filterScopeCreatedStack.RemoveAt(last);
                    }
                    filterDepth--;
                    if (filterIdleSkipDepth > 0 && filterDepth < filterIdleSkipDepth) {
                        filterRuntime.CompositeCachedScope(cmd,
                            filterIdleSkipScopeKey,
                            colorTarget,
                            viewportW, viewportH,
                            IdViewportOrigin, IdViewport,
                            idleSkipScopeBoxTransform);
                        if (!depthTarget.Equals(default(RenderTargetIdentifier))) {
                            cmd.SetRenderTarget(colorTarget,
                                RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                                depthTarget,
                                RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                        } else {
                            cmd.SetRenderTarget(colorTarget,
                                RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                        }
                        cmd.SetGlobalVector(IdViewport, currentViewport);
                        cmd.SetGlobalVector(IdViewportOrigin, Vector4.zero);
                        filterIdleSkipDepth = 0;
                        filterIdleSkipScopeKey = 0;
                    } else if (createdScope && activeFilterScopes.Count > 0 && filterRuntimeReady) {
                        var scope = activeFilterScopes[activeFilterScopes.Count - 1];
                        activeFilterScopes.RemoveAt(activeFilterScopes.Count - 1);
                        cmd.SetGlobalFloat(UIRenderGraphFilterRuntime.IdRawFilterOutput, 0f);
                        filterRuntime.EndScopeAndComposite(cmd, scope.Rt,
                            scope.X, scope.Y, scope.W, scope.H,
                            scope.Filters, scope.ParentTarget, scope.ParentWidth, scope.ParentHeight,
                            IdViewportOrigin, IdViewport,
                            scope.ScopeBoxTransform,
                            scope.Key,
                            scope.ContentHash,
                            scope.ParentOriginX,
                            scope.ParentOriginY,
                            scope.ParentIsCamera);
                        if (scope.ParentIsCamera) {
                            if (!depthTarget.Equals(default(RenderTargetIdentifier))) {
                                cmd.SetRenderTarget(colorTarget,
                                    RenderBufferLoadAction.Load, RenderBufferStoreAction.Store,
                                    depthTarget,
                                    RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                            } else {
                                cmd.SetRenderTarget(colorTarget,
                                    RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                            }
                            cmd.SetGlobalVector(IdViewport, currentViewport);
                            cmd.SetGlobalVector(IdViewportOrigin, Vector4.zero);
                            drawViewport = currentViewport;
                        } else {
                            cmd.SetRenderTarget(scope.ParentTarget,
                                RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                            cmd.SetGlobalVector(IdViewport, MakeViewportVector(scope.ParentWidth, scope.ParentHeight));
                            cmd.SetGlobalVector(IdViewportOrigin, new Vector4(scope.ParentOriginX, scope.ParentOriginY, 0f, 0f));
                            drawViewport = MakeViewportVector(scope.ParentWidth, scope.ParentHeight);
                        }
                    }
                }
                fEvtCursor++;
            }
            cmd.SetGlobalFloat(UIRenderGraphFilterRuntime.IdRawFilterOutput, 0f);
        }

        bool AreCachedImageBindingsValid(UIBatcher batcher) {
            if (batcher == null) return true;
            var batches = batcher.Batches;
            if (batchDrawCache.Count != batches.Count) return false;
            for (int i = 0; i < batches.Count; i++) {
                var batch = batches[i];
                if (batch.Key.Brush != UIQuadBrush.Image) continue;
                var expectedTexture = batch.Key.ImageTexture;
                if (expectedTexture == null) return false;
                if (i >= batchDrawCache.Count) return false;
                var cached = batchDrawCache[i];
                if (cached == null || cached.Count == 0) return false;
                for (int c = 0; c < cached.Count; c++) {
                    if (!ReferenceEquals(cached[c].ImageTexture, expectedTexture)) return false;
                }
            }
            return true;
        }

        bool AreCachedTextAtlasBindingsValid(UIBatcher batcher) {
            if (batcher == null) return true;
            var batches = batcher.Batches;
            if (batchDrawCache.Count != batches.Count) return false;
            for (int i = 0; i < batches.Count; i++) {
                var batch = batches[i];
                if (batch.AtlasIdSlot0 == 0
                    && batch.AtlasIdSlot1 == 0
                    && batch.AtlasIdSlot2 == 0
                    && batch.AtlasIdSlot3 == 0) {
                    continue;
                }
                var expected0 = AtlasTextureForId(batch.AtlasIdSlot0);
                var expected1 = AtlasTextureForId(batch.AtlasIdSlot1);
                var expected2 = AtlasTextureForId(batch.AtlasIdSlot2);
                var expected3 = AtlasTextureForId(batch.AtlasIdSlot3);
                if (batch.AtlasIdSlot0 != 0 && expected0 == null) return false;
                if (batch.AtlasIdSlot1 != 0 && expected1 == null) return false;
                if (batch.AtlasIdSlot2 != 0 && expected2 == null) return false;
                if (batch.AtlasIdSlot3 != 0 && expected3 == null) return false;
                if (i >= batchDrawCache.Count) return false;
                var cached = batchDrawCache[i];
                if (cached == null || cached.Count == 0) return false;
                for (int c = 0; c < cached.Count; c++) {
                    if (!ReferenceEquals(cached[c].AtlasTexture0, expected0)) return false;
                    if (!ReferenceEquals(cached[c].AtlasTexture1, expected1)) return false;
                    if (!ReferenceEquals(cached[c].AtlasTexture2, expected2)) return false;
                    if (!ReferenceEquals(cached[c].AtlasTexture3, expected3)) return false;
                }
            }
            return true;
        }

        static UnityEngine.Texture AtlasTextureForId(int atlasId) {
            return atlasId != 0
                ? Weva.Text.Sdf.AtlasRegistry.GetTextureById(atlasId)
                : null;
        }

        void IssueStencilEvent(CommandBuffer cmd, StencilClipManager.ClipEvent e) {
            int passIdx = e.Kind == StencilClipManager.ClipEventKind.Push
                ? StencilClipGeometry.PushPassIndex
                : StencilClipGeometry.PopPassIndex;
            int writeRef = e.Kind == StencilClipManager.ClipEventKind.Push ? e.Frame.Ref - 1 : e.Frame.Ref;
            // Same FF-state caveat as the content quad: `Stencil { Ref [_StencilWriteRef] }`
            // resolves from the material's serialized property, not from cmd.SetGlobalInt or
            // a per-draw MPB. We keep one stencil-write material per writeRef value (max 17).
            var stencilMat = resources.GetStencilWriteForRef(writeRef);
            if (stencilMat == null) return;
            stencilBuilder.Reset();
            StencilClipGeometry.EncodeClipMask(stencilBuilder, e.Frame.Bounds, e.Frame.Radii, e.Frame.WorldTransform);
            // CRITICAL: each event needs its own Mesh instance because cmd.DrawMesh
            // captures the mesh by reference and reads it back at execute time. Reusing
            // a single dynamic Mesh across multiple recorded draws means every replay
            // sees the LAST event's geometry. Allocate a per-event Mesh from a frame pool.
            var mesh = RentStencilMesh();
            UploadMeshFrom(mesh, stencilBuilder);
            cmd.DrawMesh(mesh, Matrix4x4.identity, stencilMat, 0, passIdx);
        }

        readonly Stack<Mesh> stencilMeshPool = new Stack<Mesh>();
        readonly List<Mesh> stencilMeshInUse = new List<Mesh>(16);
        Mesh RentStencilMesh() {
            Mesh m;
            if (stencilMeshPool.Count > 0) {
                m = stencilMeshPool.Pop();
            } else {
                m = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                m.MarkDynamic();
            }
            stencilMeshInUse.Add(m);
            return m;
        }
        void ReturnStencilMeshes() {
            for (int i = 0; i < stencilMeshInUse.Count; i++) stencilMeshPool.Push(stencilMeshInUse[i]);
            stencilMeshInUse.Clear();
        }

        static void UploadMeshFrom(Mesh target, MeshBuilder b) {
            int n = b.VertexCount;
            if (n == 0) return;
            var positions = new Vector3[n];
            var uvs = new Vector2[n];
            var colors = new Color[n];
            var tangents = new Vector4[n];
            for (int i = 0; i < n; i++) {
                var v = b.Vertices[i];
                positions[i] = new Vector3(v.Px, v.Py, v.Pz);
                uvs[i] = new Vector2(v.Uvx, v.Uvy);
                colors[i] = new Color(v.Color.R, v.Color.G, v.Color.B, v.Color.A);
                tangents[i] = new Vector4(v.Tx, v.Ty, v.Tz, v.Tw);
            }
            int iCount = b.IndexCount;
            var idx = new int[iCount];
            for (int i = 0; i < iCount; i++) idx[i] = b.Indices[i];
            target.Clear();
            target.indexFormat = IndexFormat.UInt32;
            target.SetVertices(positions);
            target.SetUVs(0, uvs);
            target.SetColors(colors);
            target.SetTangents(tangents);
            target.SetIndices(idx, MeshTopology.Triangles, 0);
            target.UploadMeshData(false);
        }

        void IssueBatch(CommandBuffer cmd, UIQuadBatch batch, bool allowIdleCache = true) {
            int total = batch.InstanceCount;
            if (total <= 0) return;
            // Idle-frame replay: prior frame's full path populated
            // batchDrawCache[currentBatchIndex] with one entry per chunk.
            // We still set keywords (cheap; persists per material variant
            // anyway) and then re-record the cached cmd.DrawMesh calls
            // pointing at the same Mesh + Material + MPB. The MPB still
            // holds the StructuredBuffer reference + atlas + viewport from
            // last frame, and the buffer's contents are unchanged because
            // we skipped ResetInstanceBufferPool / SetData. Net effect:
            // ~one DrawMesh per chunk and zero CPU/GPU upload work.
            if (drainIdleFrame && allowIdleCache
                && currentBatchIndex < batchDrawCache.Count
                && batchDrawCache[currentBatchIndex] != null
                && batchDrawCache[currentBatchIndex].Count > 0) {
                var cached = batchDrawCache[currentBatchIndex];
                var idleMat = cached[0].Material;
                if (idleMat != null) {
                    for (int i = 0; i < cached.Count; i++) {
                        var c = cached[i];
                        cmd.DrawMesh(c.Mesh, Matrix4x4.identity, c.Material, 0, 0, c.Mpb);
                    }
                    return;
                }
            }
            // CRITICAL FIX: fixed-function stencil state (`Stencil { Ref [_StencilRef] }`)
            // reads from the MATERIAL's serialized property values, NOT from per-draw
            // MaterialPropertyBlocks. MPB.SetInt cannot drive FF stencil bracket bindings.
            // The previous code set _StencilRef on the MPB, leaving every draw to use the
            // material's default _StencilRef=0 / _StencilComp=8 (Always). For frames that
            // ALSO uploaded SetGlobalInt(_StencilRef,...) per-batch, those globals collapse
            // to the LAST batch's value at execute time — explaining why only one element
            // survived (only fragments matching that last clip's ref pass the test).
            //
            // Fix: keep one material per distinct (stencilRef, stencilComp) tuple. Max
            // depth 16 → at most 17 materials. We bake _StencilRef/_StencilComp directly
            // into each variant's serialized state so the FF stencil block resolves them.
            UnityEngine.Texture batchAtlasTex0 = null;
            UnityEngine.Texture batchAtlasTex1 = null;
            UnityEngine.Texture batchAtlasTex2 = null;
            UnityEngine.Texture batchAtlasTex3 = null;
            UnityEngine.Texture batchImageTex = null;
            // B16 — path coverage mask texture (null = no path clip mask in this batch).
            UnityEngine.Texture batchMaskImageTex = batch.MaskImageTexture;
            // Solid+Text now share one batch class so a batch keyed Brush=Solid
            // can still carry Text instances (with their own atlas bindings).
            // Probe AtlasIdSlot0/1 unconditionally — they're zero unless a
            // text quad was actually emitted into this batch.
            if (batch.AtlasIdSlot0 != 0)
                batchAtlasTex0 = Weva.Text.Sdf.AtlasRegistry.GetTextureById(batch.AtlasIdSlot0);
            if (batch.AtlasIdSlot1 != 0)
                batchAtlasTex1 = Weva.Text.Sdf.AtlasRegistry.GetTextureById(batch.AtlasIdSlot1);
            if (batch.AtlasIdSlot2 != 0)
                batchAtlasTex2 = Weva.Text.Sdf.AtlasRegistry.GetTextureById(batch.AtlasIdSlot2);
            if (batch.AtlasIdSlot3 != 0)
                batchAtlasTex3 = Weva.Text.Sdf.AtlasRegistry.GetTextureById(batch.AtlasIdSlot3);
            if (batch.Key.Brush == UIQuadBrush.Image) batchImageTex = batch.Key.ImageTexture;
            var mat = resources.GetQuadMaterial(batch.Key.StencilRef, batchImageTex);
            if (mat == null) return;
            UnityEngine.Profiling.Profiler.BeginSample("Weva.Pass.UploadChunks");
            int offset = 0;
            while (offset < total) {
                int chunk = Math.Min(MaxInstancesPerDraw, total - offset);
                int chunkFloat4s = chunk * InstanceFloat4Stride;
                PackInstancesChunk(batch.Instances, offset, chunk, uploadBuffer);

                // Acquire (or lazily create) the persistent chunk state
                // for this sequential index. Across frames the same index
                // refers to the same buffer + last-data snapshot, which is
                // what makes the diff-based partial upload work.
                while (chunkStates.Count <= chunkSeqIdx) chunkStates.Add(null);
                var st = chunkStates[chunkSeqIdx];
                if (st == null) {
                    st = new ChunkUploadState();
                    chunkStates[chunkSeqIdx] = st;
                }
                chunkSeqIdx++;

                // Ensure the persistent GraphicsBuffer is large enough.
                // Power-of-two sizing keeps the pool concentrated on a
                // few sizes; growing reallocates and forces a full
                // upload (since LastData is reset).
                int cap = 64;
                while (cap < chunkFloat4s) cap <<= 1;
                if (st.Buffer == null || st.Buffer.count < cap) {
                    st.Buffer?.Dispose();
                    st.Buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cap, sizeof(float) * 4);
                    st.LastData = null;
                    st.LastFloat4Count = 0;
                }

                if (st.LastData == null || st.LastFloat4Count != chunkFloat4s) {
                    // First upload for this chunk slot (or size changed):
                    // do one full SetData and remember the contents.
                    st.Buffer.SetData(uploadBuffer, 0, 0, chunkFloat4s);
                    if (st.LastData == null || st.LastData.Length < chunkFloat4s) {
                        st.LastData = new Vector4[cap];
                    }
                    System.Array.Copy(uploadBuffer, 0, st.LastData, 0, chunkFloat4s);
                    st.LastFloat4Count = chunkFloat4s;
                    FullUploadFloat4s += chunkFloat4s;
                } else {
                    // Diff against the previous frame's data and coalesce
                    // adjacent dirty float4s into the smallest set of
                    // SetData(srcOffset, dstOffset, count) ranges. For an
                    // animation that touches one tile of 112, this
                    // typically issues one SetData of count=16 (one
                    // instance) instead of count=1792 (whole chunk).
                    int rangeStart = -1;
                    int partialUploadedThisChunk = 0;
                    for (int i = 0; i < chunkFloat4s; i++) {
                        var u = uploadBuffer[i];
                        var p = st.LastData[i];
                        // Exact float equality — Vector4.operator== uses an
                        // epsilon that's too lenient for this purpose
                        // (animation-interpolated values can match within
                        // epsilon and we'd skip writing them when the GPU
                        // still has last frame's value).
                        bool diff = u.x != p.x || u.y != p.y || u.z != p.z || u.w != p.w;
                        if (diff) {
                            if (rangeStart == -1) rangeStart = i;
                        } else if (rangeStart != -1) {
                            int len = i - rangeStart;
                            st.Buffer.SetData(uploadBuffer, rangeStart, rangeStart, len);
                            partialUploadedThisChunk += len;
                            rangeStart = -1;
                        }
                    }
                    if (rangeStart != -1) {
                        int len = chunkFloat4s - rangeStart;
                        st.Buffer.SetData(uploadBuffer, rangeStart, rangeStart, len);
                        partialUploadedThisChunk += len;
                    }
                    PartialUploadFloat4s += partialUploadedThisChunk;
                    System.Array.Copy(uploadBuffer, 0, st.LastData, 0, chunkFloat4s);
                }

                // Persistent MPB rebinds the persistent buffer + viewport
                // + atlases each frame. cmd.DrawMesh captures the MPB by
                // reference; since each chunk seq slot has its own MPB
                // they don't collide across the frame's draws.
                if (st.Mpb == null) st.Mpb = new MaterialPropertyBlock();
                st.Mpb.Clear();
                st.Mpb.SetBuffer(IdInstancesSB, st.Buffer);
                st.Mpb.SetVector(IdViewport, drawViewport);
                if (batchAtlasTex0 != null) {
                    st.Mpb.SetTexture(IdGlyphAtlas, batchAtlasTex0);
                    st.Mpb.SetVector(IdGlyphAtlasChannelMask, GetGlyphAtlasChannelMask(batchAtlasTex0));
                }
                if (batchAtlasTex1 != null) {
                    st.Mpb.SetTexture(IdGlyphAtlas1, batchAtlasTex1);
                    st.Mpb.SetVector(IdGlyphAtlas1ChannelMask, GetGlyphAtlasChannelMask(batchAtlasTex1));
                }
                if (batchAtlasTex2 != null) {
                    st.Mpb.SetTexture(IdGlyphAtlas2, batchAtlasTex2);
                    st.Mpb.SetVector(IdGlyphAtlas2ChannelMask, GetGlyphAtlasChannelMask(batchAtlasTex2));
                }
                if (batchAtlasTex3 != null) {
                    st.Mpb.SetTexture(IdGlyphAtlas3, batchAtlasTex3);
                    st.Mpb.SetVector(IdGlyphAtlas3ChannelMask, GetGlyphAtlasChannelMask(batchAtlasTex3));
                }
                if (batchImageTex != null) {
                    // _WevaImage is a shader property so the MPB captures the per-draw image texture.
                    st.Mpb.SetTexture(IdImageTexture, batchImageTex);
                }
                if (batchMaskImageTex != null) {
                    // B16 — bind path coverage mask texture to _WevaMaskImage.
                    st.Mpb.SetTexture(IdMaskImageTexture, batchMaskImageTex);
                }
                var meshForChunk = GetChunkMesh(chunk);
                cmd.DrawMesh(meshForChunk, Matrix4x4.identity, mat, 0, 0, st.Mpb);
                if (allowIdleCache) {
                    var slot = EnsureBatchCacheSlot(currentBatchIndex);
                    slot.Add(new CachedDraw {
                        Buffer = st.Buffer,
                        Mpb = st.Mpb,
                        Mesh = meshForChunk,
                        Material = mat,
                        ImageTexture = batchImageTex,
                        AtlasTexture0 = batchAtlasTex0,
                        AtlasTexture1 = batchAtlasTex1,
                        AtlasTexture2 = batchAtlasTex2,
                        AtlasTexture3 = batchAtlasTex3,
                        ChunkCount = chunk,
                    });
                }
                offset += chunk;
            }
            UnityEngine.Profiling.Profiler.EndSample();
        }

        List<CachedDraw> EnsureBatchCacheSlot(int index) {
            while (batchDrawCache.Count <= index) {
                batchDrawCache.Add(null);
            }
            var slot = batchDrawCache[index];
            if (slot == null) {
                slot = batchDrawCacheListPool.Count > 0 ? batchDrawCacheListPool.Pop() : new List<CachedDraw>(2);
                slot.Clear();
                batchDrawCache[index] = slot;
            }
            return slot;
        }

        void ClearBatchDrawCache(int newBatchCount) {
            for (int i = 0; i < batchDrawCache.Count; i++) {
                var slot = batchDrawCache[i];
                if (slot == null) continue;
                slot.Clear();
                batchDrawCacheListPool.Push(slot);
                batchDrawCache[i] = null;
            }
            batchDrawCache.Clear();
            while (batchDrawCache.Count < newBatchCount) batchDrawCache.Add(null);
        }

        // MPB pool — one per draw call this frame, freed by Reset() at frame
        // start. Each draw needs its own snapshot because Unity's command
        // buffer captures the MPB by reference at draw-record time and reads
        // it back at execute time; sharing one MPB across draws would put us
        // back in the same global-state race we just fixed.
        readonly Stack<MaterialPropertyBlock> mpbPool = new Stack<MaterialPropertyBlock>();
        readonly List<MaterialPropertyBlock> mpbInUse = new List<MaterialPropertyBlock>(64);
        MaterialPropertyBlock RentMpb() {
            var mpb = mpbPool.Count > 0 ? mpbPool.Pop() : new MaterialPropertyBlock();
            mpb.Clear();
            mpbInUse.Add(mpb);
            return mpb;
        }
        void ResetMpbPool() {
            for (int i = 0; i < mpbInUse.Count; i++) mpbPool.Push(mpbInUse[i]);
            mpbInUse.Clear();
        }

        // Routes through the Burst-compiled pointer-based pack when Burst
        // is available, otherwise the managed loop. The Burst version
        // (see PackInstancesChunkBurst) is a straight memcpy at the
        // GPU-instance layout level — typical 3-5× speedup on this hot
        // path versus Mono's per-field copy because the loop body
        // collapses to ~16 unaligned 16B loads + stores per instance
        // with no bounds checks.
        static unsafe void PackInstancesChunk(UIQuadInstance[] src, int srcOffset, int count, Vector4[] dst) {
            if (src == null || dst == null) return;
            int dstCap = dst.Length / InstanceFloat4Stride;
            int max = Math.Min(count, dstCap);
            if (max <= 0) return;
#if WEVA_BURST
            fixed (UIQuadInstance* sp = src)
            fixed (Vector4* dp = dst) {
                PackInstancesChunkBurst(sp + srcOffset, dp, max);
            }
#else
            for (int i = 0; i < max; i++) {
                int o = i * InstanceFloat4Stride;
                var inst = src[srcOffset + i];
                dst[o + 0] = inst.PosSize;
                dst[o + 1] = inst.Radii;
                dst[o + 2] = inst.Color;
                dst[o + 3] = inst.BrushParams;
                dst[o + 4] = inst.BorderWidths;
                dst[o + 5] = inst.BorderColorTop;
                dst[o + 6] = inst.BorderColorRight;
                dst[o + 7] = inst.BorderColorBottom;
                dst[o + 8] = inst.BorderColorLeft;
                dst[o + 9] = inst.BorderStyles;
                dst[o + 10] = inst.TransformRow0;
                dst[o + 11] = inst.TransformRow1;
                dst[o + 12] = inst.TransformRow2;
                dst[o + 13] = inst.ClipRect;
                dst[o + 14] = inst.GradientStop4;
                dst[o + 15] = inst.GradientStop5;
                dst[o + 16] = inst.ClipShape0;
                dst[o + 17] = inst.ClipShape1;
                dst[o + 18] = inst.ClipShape2;
                dst[o + 19] = inst.ClipShape3;
                dst[o + 20] = inst.ClipShape4;
                dst[o + 21] = inst.MaskParams0;
                dst[o + 22] = inst.MaskBounds;
                dst[o + 23] = inst.MaskTile;
                dst[o + 24] = inst.MaskParams1;
                dst[o + 25] = inst.MaskColor0;
                dst[o + 26] = inst.MaskColor1;
                dst[o + 27] = inst.MaskColor2;
                dst[o + 28] = inst.MaskColor3;
                dst[o + 29] = inst.MaskPositions;
                dst[o + 30] = inst.Mask1Params0;
                dst[o + 31] = inst.Mask1Bounds;
                dst[o + 32] = inst.Mask1Tile;
                dst[o + 33] = inst.Mask1Params1;
                dst[o + 34] = inst.Mask1Color0;
                dst[o + 35] = inst.Mask1Color1;
                dst[o + 36] = inst.Mask1Color2;
                dst[o + 37] = inst.Mask1Color3;
                dst[o + 38] = inst.Mask1Positions;
                dst[o + 39] = inst.Mask2Params0;
                dst[o + 40] = inst.Mask2Bounds;
                dst[o + 41] = inst.Mask2Tile;
                dst[o + 42] = inst.Mask2Params1;
                dst[o + 43] = inst.Mask2Color0;
                dst[o + 44] = inst.Mask2Color1;
                dst[o + 45] = inst.Mask2Color2;
                dst[o + 46] = inst.Mask2Color3;
                dst[o + 47] = inst.Mask2Positions;
                dst[o + 48] = inst.Mask3Params0;
                dst[o + 49] = inst.Mask3Bounds;
                dst[o + 50] = inst.Mask3Tile;
                dst[o + 51] = inst.Mask3Params1;
                dst[o + 52] = inst.Mask3Color0;
                dst[o + 53] = inst.Mask3Color1;
                dst[o + 54] = inst.Mask3Color2;
                dst[o + 55] = inst.Mask3Color3;
                dst[o + 56] = inst.Mask3Positions;
                dst[o + 57] = inst.RadiiY;
            }
#endif
        }

#if WEVA_BURST
        // Burst-compiled instance pack. The struct UIQuadInstance has a
        // fixed Vector4 layout — same as the dst stride — so a single memcpy
        // per instance would also be correct. We keep the field-by-field
        // write because Burst will vectorize it and the
        // explicit field reads tolerate any future struct padding the
        // CLR might introduce. FloatMode.Fast doesn't change semantics
        // here (no math, only copies); CompileSynchronously avoids the
        // first-frame compile stall.
        [Unity.Burst.BurstCompile(FloatMode = Unity.Burst.FloatMode.Fast,
            CompileSynchronously = true)]
        static unsafe void PackInstancesChunkBurst(UIQuadInstance* src, Vector4* dst, int count) {
            for (int i = 0; i < count; i++) {
                Vector4* d = dst + i * InstanceFloat4Stride;
                UIQuadInstance s = src[i];
                d[0] = s.PosSize;
                d[1] = s.Radii;
                d[2] = s.Color;
                d[3] = s.BrushParams;
                d[4] = s.BorderWidths;
                d[5] = s.BorderColorTop;
                d[6] = s.BorderColorRight;
                d[7] = s.BorderColorBottom;
                d[8] = s.BorderColorLeft;
                d[9] = s.BorderStyles;
                d[10] = s.TransformRow0;
                d[11] = s.TransformRow1;
                d[12] = s.TransformRow2;
                d[13] = s.ClipRect;
                d[14] = s.GradientStop4;
                d[15] = s.GradientStop5;
                d[16] = s.ClipShape0;
                d[17] = s.ClipShape1;
                d[18] = s.ClipShape2;
                d[19] = s.ClipShape3;
                d[20] = s.ClipShape4;
                d[21] = s.MaskParams0;
                d[22] = s.MaskBounds;
                d[23] = s.MaskTile;
                d[24] = s.MaskParams1;
                d[25] = s.MaskColor0;
                d[26] = s.MaskColor1;
                d[27] = s.MaskColor2;
                d[28] = s.MaskColor3;
                d[29] = s.MaskPositions;
                d[30] = s.Mask1Params0;
                d[31] = s.Mask1Bounds;
                d[32] = s.Mask1Tile;
                d[33] = s.Mask1Params1;
                d[34] = s.Mask1Color0;
                d[35] = s.Mask1Color1;
                d[36] = s.Mask1Color2;
                d[37] = s.Mask1Color3;
                d[38] = s.Mask1Positions;
                d[39] = s.Mask2Params0;
                d[40] = s.Mask2Bounds;
                d[41] = s.Mask2Tile;
                d[42] = s.Mask2Params1;
                d[43] = s.Mask2Color0;
                d[44] = s.Mask2Color1;
                d[45] = s.Mask2Color2;
                d[46] = s.Mask2Color3;
                d[47] = s.Mask2Positions;
                d[48] = s.Mask3Params0;
                d[49] = s.Mask3Bounds;
                d[50] = s.Mask3Tile;
                d[51] = s.Mask3Params1;
                d[52] = s.Mask3Color0;
                d[53] = s.Mask3Color1;
                d[54] = s.Mask3Color2;
                d[55] = s.Mask3Color3;
                d[56] = s.Mask3Positions;
                d[57] = s.RadiiY;
            }
        }
#endif
        // The trailing #endif closes the outer UNITY_2023_3_OR_NEWER guard
        // that wraps the entire RenderGraph pass implementation.
#endif

        // Public for tests; produces the flattened Vector4 array consumed by the shader.
        public static void PackInstances(UIQuadInstance[] src, int count, Vector4[] dst) {
            if (src == null || dst == null) return;
            int max = Math.Min(count, src.Length);
            int dstCap = dst.Length / InstanceFloat4Stride;
            if (max > dstCap) max = dstCap;
            for (int i = 0; i < max; i++) {
                int o = i * InstanceFloat4Stride;
                var inst = src[i];
                dst[o + 0] = inst.PosSize;
                dst[o + 1] = inst.Radii;
                dst[o + 2] = inst.Color;
                dst[o + 3] = inst.BrushParams;
                dst[o + 4] = inst.BorderWidths;
                dst[o + 5] = inst.BorderColorTop;
                dst[o + 6] = inst.BorderColorRight;
                dst[o + 7] = inst.BorderColorBottom;
                dst[o + 8] = inst.BorderColorLeft;
                dst[o + 9] = inst.BorderStyles;
                dst[o + 10] = inst.TransformRow0;
                dst[o + 11] = inst.TransformRow1;
                dst[o + 12] = inst.TransformRow2;
                dst[o + 13] = inst.ClipRect;
                dst[o + 14] = inst.GradientStop4;
                dst[o + 15] = inst.GradientStop5;
                dst[o + 16] = inst.ClipShape0;
                dst[o + 17] = inst.ClipShape1;
                dst[o + 18] = inst.ClipShape2;
                dst[o + 19] = inst.ClipShape3;
                dst[o + 20] = inst.ClipShape4;
                dst[o + 21] = inst.MaskParams0;
                dst[o + 22] = inst.MaskBounds;
                dst[o + 23] = inst.MaskTile;
                dst[o + 24] = inst.MaskParams1;
                dst[o + 25] = inst.MaskColor0;
                dst[o + 26] = inst.MaskColor1;
                dst[o + 27] = inst.MaskColor2;
                dst[o + 28] = inst.MaskColor3;
                dst[o + 29] = inst.MaskPositions;
                dst[o + 30] = inst.Mask1Params0;
                dst[o + 31] = inst.Mask1Bounds;
                dst[o + 32] = inst.Mask1Tile;
                dst[o + 33] = inst.Mask1Params1;
                dst[o + 34] = inst.Mask1Color0;
                dst[o + 35] = inst.Mask1Color1;
                dst[o + 36] = inst.Mask1Color2;
                dst[o + 37] = inst.Mask1Color3;
                dst[o + 38] = inst.Mask1Positions;
                dst[o + 39] = inst.Mask2Params0;
                dst[o + 40] = inst.Mask2Bounds;
                dst[o + 41] = inst.Mask2Tile;
                dst[o + 42] = inst.Mask2Params1;
                dst[o + 43] = inst.Mask2Color0;
                dst[o + 44] = inst.Mask2Color1;
                dst[o + 45] = inst.Mask2Color2;
                dst[o + 46] = inst.Mask2Color3;
                dst[o + 47] = inst.Mask2Positions;
                dst[o + 48] = inst.Mask3Params0;
                dst[o + 49] = inst.Mask3Bounds;
                dst[o + 50] = inst.Mask3Tile;
                dst[o + 51] = inst.Mask3Params1;
                dst[o + 52] = inst.Mask3Color0;
                dst[o + 53] = inst.Mask3Color1;
                dst[o + 54] = inst.Mask3Color2;
                dst[o + 55] = inst.Mask3Color3;
                dst[o + 56] = inst.Mask3Positions;
                dst[o + 57] = inst.RadiiY;
            }
        }

        public static void ApplyKeywords(Material mat, UIBatchKey key) {
            // Class 0 (fill + text + shadow) enables every relevant keyword so
            // a single shader variant dispatches per-instance via brushIndex.
            bool isFill = key.Brush == UIQuadBrush.Solid
                || key.Brush == UIQuadBrush.Text
                || key.Brush == UIQuadBrush.LinearGradient
                || key.Brush == UIQuadBrush.RadialGradient
                || key.Brush == UIQuadBrush.ConicGradient
                || key.Brush == UIQuadBrush.Shadow
                || key.Brush == UIQuadBrush.ShadowInset;
            ApplyKeywords(mat, isFill);
        }

        // Keyword sets are CONSTANT per material instance: the null-texture
        // quad material only ever draws fill-class batches (every non-Image
        // brush is fill class, and an unresolved image degrades to EmitSolid),
        // while per-texture image materials only ever draw Image batches.
        // UIBatchedResources therefore bakes the keywords once at material
        // creation — the old path re-issued 7 cmd.SetKeyword calls per batch
        // per frame for values that never changed.
        public static void ApplyKeywords(Material mat, bool isFill) {
            if (mat == null) return;
            ToggleKeyword(mat, KeywordBrushLinear, isFill);
            ToggleKeyword(mat, KeywordBrushRadial, isFill);
            ToggleKeyword(mat, KeywordBrushConic, isFill);
            ToggleKeyword(mat, KeywordBordered, isFill);
            ToggleKeyword(mat, KeywordShadowOutset, isFill);
            ToggleKeyword(mat, KeywordShadowInset, isFill);
            ToggleKeyword(mat, KeywordText, isFill);
        }

        static void ToggleKeyword(Material mat, string keyword, bool enable) {
            if (enable) mat.EnableKeyword(keyword);
            else mat.DisableKeyword(keyword);
        }

        // Per-frame keyword writes were removed: keyword sets are constant per
        // material instance and are baked at creation by UIBatchedResources
        // (see the ApplyKeywords(Material, bool) overload). The old
        // ApplyKeywordsToBuffer issued 7 cmd.SetKeyword calls per batch per
        // frame — including on idle frames — for values that never changed.

        static bool HasStencil(RenderTextureDescriptor desc) {
            return desc.depthBufferBits >= 24;
        }

        Matrix4x4[] cachedIdentities;
        Matrix4x4[] AllOnesMatrices(int count) {
            if (cachedIdentities == null || cachedIdentities.Length < count) {
                cachedIdentities = new Matrix4x4[Math.Max(count, MaxInstancesPerDraw)];
                for (int i = 0; i < cachedIdentities.Length; i++) cachedIdentities[i] = Matrix4x4.identity;
            }
            return cachedIdentities;
        }

        // Cached "mega-mesh" per chunk size. Each mesh is `chunkSize` copies of the unit
        // quad, with the COLOR.r vertex attribute carrying the per-quad instance index
        // (0..chunkSize-1). The shader reads COLOR.r as a uint and indexes into the
        // _WevaInstances uniform array to fetch its instance data.
        readonly Mesh[] chunkMeshes = new Mesh[MaxInstancesPerDraw + 1];
        Mesh GetChunkMesh(int chunkSize) {
            if (chunkSize < 1) return null;
            if (chunkSize > MaxInstancesPerDraw) chunkSize = MaxInstancesPerDraw;
            var existing = chunkMeshes[chunkSize];
            if (existing != null) return existing;
            var m = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            int vCount = chunkSize * 4;
            int iCount = chunkSize * 6;
            var verts = new Vector3[vCount];
            var uvs = new Vector2[vCount];
            // Per-vertex instance index lives on TANGENT.x — a float4 channel
            // that Mesh stores at full float precision (unlike COLOR which is
            // UNorm8 and clamps values >1 / aliases integers >255).
            var tangents = new Vector4[vCount];
            var idx = new int[iCount];
            for (int q = 0; q < chunkSize; q++) {
                int v0 = q * 4;
                verts[v0 + 0] = new Vector3(0f, 0f, 0f);
                verts[v0 + 1] = new Vector3(0f, 1f, 0f);
                verts[v0 + 2] = new Vector3(1f, 1f, 0f);
                verts[v0 + 3] = new Vector3(1f, 0f, 0f);
                uvs[v0 + 0] = new Vector2(0f, 0f);
                uvs[v0 + 1] = new Vector2(0f, 1f);
                uvs[v0 + 2] = new Vector2(1f, 1f);
                uvs[v0 + 3] = new Vector2(1f, 0f);
                var t = new Vector4((float)q, 0f, 0f, 1f);
                tangents[v0 + 0] = t;
                tangents[v0 + 1] = t;
                tangents[v0 + 2] = t;
                tangents[v0 + 3] = t;
                int t0 = q * 6;
                idx[t0 + 0] = v0 + 0;
                idx[t0 + 1] = v0 + 1;
                idx[t0 + 2] = v0 + 2;
                idx[t0 + 3] = v0 + 0;
                idx[t0 + 4] = v0 + 2;
                idx[t0 + 5] = v0 + 3;
            }
            m.indexFormat = IndexFormat.UInt32;
            m.SetVertices(verts);
            m.SetUVs(0, uvs);
            m.SetTangents(tangents);
            m.SetIndices(idx, MeshTopology.Triangles, 0);
            m.UploadMeshData(false);
            chunkMeshes[chunkSize] = m;
            return m;
        }

        Mesh BuildUnitQuad() {
            var m = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            m.vertices = new[] {
                new Vector3(0f, 0f, 0f),
                new Vector3(0f, 1f, 0f),
                new Vector3(1f, 1f, 0f),
                new Vector3(1f, 0f, 0f)
            };
            m.uv = new[] {
                new Vector2(0f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(1f, 0f)
            };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            m.UploadMeshData(false);
            return m;
        }

        public void Dispose() {
            filterRuntime?.Dispose();
            filterRuntime = null;
            for (int i = 0; i < chunkMeshes.Length; i++) {
                if (chunkMeshes[i] != null) DestroyMesh(chunkMeshes[i]);
                chunkMeshes[i] = null;
            }
            if (quadMesh != null) {
#if UNITY_EDITOR
                if (Application.isPlaying) UnityEngine.Object.Destroy(quadMesh);
                else UnityEngine.Object.DestroyImmediate(quadMesh);
#else
                UnityEngine.Object.Destroy(quadMesh);
#endif
                quadMesh = null;
            }
            DestroyMeshPool(stencilMeshPool);
            DestroyMeshList(stencilMeshInUse);
            // GraphicsBuffers are unmanaged GPU resources — Unity's GC won't
            // touch them. Dispose every pooled and in-flight buffer.
            while (instanceBufferPool.Count > 0) instanceBufferPool.Pop().Dispose();
            for (int i = 0; i < instanceBuffersInUse.Count; i++) instanceBuffersInUse[i].Dispose();
            instanceBuffersInUse.Clear();
            // Persistent per-chunk buffers (partial-upload state) — same
            // unmanaged-resource caveat applies.
            for (int i = 0; i < chunkStates.Count; i++) {
                var s = chunkStates[i];
                if (s != null && s.Buffer != null) s.Buffer.Dispose();
            }
            chunkStates.Clear();
        }

        static void DestroyMeshPool(Stack<Mesh> pool) {
            while (pool.Count > 0) DestroyMesh(pool.Pop());
        }
        static void DestroyMeshList(List<Mesh> list) {
            for (int i = 0; i < list.Count; i++) DestroyMesh(list[i]);
            list.Clear();
        }
        static void DestroyMesh(Mesh m) {
            if (m == null) return;
#if UNITY_EDITOR
            if (Application.isPlaying) UnityEngine.Object.Destroy(m);
            else UnityEngine.Object.DestroyImmediate(m);
#else
            UnityEngine.Object.Destroy(m);
#endif
        }
    }

    // Resource pool for the batched pipeline. Keeps the quad material + a stencil-write
    // material; loaded by Shader.Find on first construction.
    public sealed class UIBatchedResources : IDisposable {
        public const string QuadShaderName = "Hidden/Weva/Quad";
        public Shader QuadShader { get; private set; }

        Material quadMaterial;
        // One material per stencil-ref state. _StencilRef / _StencilComp drive a
        // fixed-function stencil block; FF state can only be sourced from the
        // MATERIAL's serialized properties, not from per-draw MPBs or globals
        // sequenced into a RasterCommandBuffer (which collapse to the final
        // value at execute time for shared globals).
        readonly System.Collections.Generic.Dictionary<int, Material> stencilMaterials =
            new System.Collections.Generic.Dictionary<int, Material>();
        // R5: keyed by stencil-ref ONLY, not by (stencilRef, textureId). The
        // image texture is bound per-draw by the chunk MPB (DrainBatches sets
        // _WevaImage on st.Mpb), so a per-texture material was redundant — and
        // because it keyed on texture instance id, dynamic textures (RT-backed
        // <img>, atlas rebuilds minting fresh Texture2D objects) leaked one
        // never-evicted Material each. One image-class material per stencil ref
        // is all that's needed (≤ a handful; one when ForceDisableStencil).
        readonly System.Collections.Generic.Dictionary<int, Material> imageMaterials =
            new System.Collections.Generic.Dictionary<int, Material>();
        ShaderResources fallback;

        public UIBatchedResources() {
            QuadShader = Shader.Find(QuadShaderName);
            fallback = new ShaderResources();
        }

        // ShaderResources is owned by this batched-pipeline pool; the filter
        // runtime (UIRenderGraphFilterRuntime) borrows the Filter shader's
        // material from here to drive blur/composite/color-matrix passes.
        // Tests asserting on materials should rely on Shaders being loaded
        // when IsReady returns true.
        public ShaderResources Resources => fallback;

        public bool IsReady => QuadShader != null && fallback.IsReady;

        public Material GetQuadMaterial() => GetQuadMaterial(0, null);

        // Stencil clipping is replaced by per-fragment AABB rejection in
        // the quad shader (UIQuadInstance.ClipRect, slot 13). The FF stencil
        // path silently failed under Unity 6 / URP RenderGraph: stencil
        // writes never reached the post-FX depth target's stencil bits, so
        // every clipped quad disappeared (chat lines, minimap content, HP-
        // bar fills). Forcing stencil off makes every quad use Comp=Always
        // — the AABB scissor handles the actual clip math now. Kept as a
        // toggle so tests can flip it back off to exercise the stencil
        // codepath in isolation.
        public static bool ForceDisableStencil = true;

        public Material GetQuadMaterial(int stencilRef) => GetQuadMaterial(stencilRef, null);

        public Material GetQuadMaterial(int stencilRef, UnityEngine.Texture imageTexture) {
            if (QuadShader == null) return null;
            int key = ForceDisableStencil ? 0 : stencilRef;
            if (imageTexture != null) {
                // The texture itself is bound per-draw via the chunk MPB, so we
                // only need ONE image-class material per stencil ref — NOT one
                // per texture (that leaked a Material per dynamic texture).
                if (imageMaterials.TryGetValue(key, out var existingImage) && existingImage != null) {
                    return existingImage;
                }
                var imageMat = CreateQuadMaterial(key, fillClass: false);
                imageMaterials[key] = imageMat;
                return imageMat;
            }
            if (stencilMaterials.TryGetValue(key, out var existing) && existing != null) {
                return existing;
            }
            var mat = CreateQuadMaterial(key, fillClass: true);
            stencilMaterials[key] = mat;
            // Keep the legacy single-material slot pointing at the unclipped variant
            // for any code that still calls GetQuadMaterial() with no argument.
            if (key == 0) quadMaterial = mat;
            return mat;
        }

        Material CreateQuadMaterial(int key, bool fillClass) {
            var mat = new Material(QuadShader) { hideFlags = HideFlags.HideAndDontSave };
            // Keywords are constant per material (fill-class vs image — see
            // UIRenderGraphPass.ApplyKeywords(Material, bool)); bake them here
            // instead of re-issuing 7 SetKeyword commands per batch per frame.
            UIRenderGraphPass.ApplyKeywords(mat, fillClass);
            mat.SetFloat("_SrcBlend", (float)(int)BlendMode.One);
            mat.SetFloat("_DstBlend", (float)(int)BlendMode.OneMinusSrcAlpha);
            // The shader's Properties block declares these as Float; SetInt on
            // a Float-declared property is silently ignored in some Unity 6
            // shader-state paths (verified empirically — content at non-zero
            // refs failed Comp Equal because the material kept its default
            // _StencilComp=8 / _StencilRef=0). Use SetFloat for FF stencil
            // state to guarantee the serialized value matches the property
            // declaration the FF state reads.
            mat.SetFloat("_StencilRef", (float)key);
            mat.SetFloat("_StencilComp", (float)(int)((ForceDisableStencil || key == 0)
                ? CompareFunction.Always
                : CompareFunction.Equal));
            // Required for cmd.DrawMeshInstanced — Unity throws
            // "Material needs to enable instancing" otherwise.
            mat.enableInstancing = true;
            return mat;
        }

        public Material GetStencilWrite() => fallback.GetStencilWrite();

        // Per-writeRef stencil-write material variants. Same FF-state reasoning as the
        // content quad — `Stencil { Ref [_StencilWriteRef] }` reads from the material's
        // serialized property, so we bake the writeRef in and key by it.
        readonly System.Collections.Generic.Dictionary<int, Material> stencilWriteMaterials =
            new System.Collections.Generic.Dictionary<int, Material>();

        public Material GetStencilWriteForRef(int writeRef) {
            if (fallback == null || fallback.StencilWrite == null) return null;
            if (stencilWriteMaterials.TryGetValue(writeRef, out var existing) && existing != null) {
                return existing;
            }
            var mat = new Material(fallback.StencilWrite) { hideFlags = HideFlags.HideAndDontSave };
            // Float to match the Properties block declaration. See the matching
            // GetQuadMaterial fix for why SetInt is unreliable for FF state.
            mat.SetFloat("_StencilWriteRef", (float)writeRef);
            stencilWriteMaterials[writeRef] = mat;
            return mat;
        }

        public void Dispose() {
            foreach (var kv in stencilMaterials) {
                var m = kv.Value;
                if (m == null) continue;
#if UNITY_EDITOR
                if (Application.isPlaying) UnityEngine.Object.Destroy(m);
                else UnityEngine.Object.DestroyImmediate(m);
#else
                UnityEngine.Object.Destroy(m);
#endif
            }
            stencilMaterials.Clear();
            foreach (var kv in imageMaterials) {
                var m = kv.Value;
                if (m == null) continue;
#if UNITY_EDITOR
                if (Application.isPlaying) UnityEngine.Object.Destroy(m);
                else UnityEngine.Object.DestroyImmediate(m);
#else
                UnityEngine.Object.Destroy(m);
#endif
            }
            imageMaterials.Clear();
            foreach (var kv in stencilWriteMaterials) {
                var m = kv.Value;
                if (m == null) continue;
#if UNITY_EDITOR
                if (Application.isPlaying) UnityEngine.Object.Destroy(m);
                else UnityEngine.Object.DestroyImmediate(m);
#else
                UnityEngine.Object.Destroy(m);
#endif
            }
            stencilWriteMaterials.Clear();
            quadMaterial = null;
            fallback?.Dispose();
        }
    }
}
#endif
