#if WEVA_URP
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Weva.Paint;
using Weva.Paint.Filters;
using Weva.Rendering;

namespace Weva.Rendering.URP {
    // Runs the CSS `filter:` chain (blur / brightness / drop-shadow / etc.) inside
    // the batched RenderGraph pass. The owner (UIRenderGraphPass) hands us:
    //   1. The pass's underlying native CommandBuffer (obtained from the
    //      UnsafeGraphContext — RasterCommandBuffer can't switch render targets
    //      mid-pass, which is why the legacy FilterPipeline doesn't run there).
    //   2. The screen-space color target identifier (camera color RT).
    //   3. A FilterEvent describing the scope's bounds + filter chain.
    //
    // Push:
    //   - Allocate a temp RT sized to the filter's screen-space AABB + halo.
    //   - SetRenderTarget(tempRt) + ClearRenderTarget.
    //   - Set _WevaViewport so the quad shader maps the temp RT's pixel
    //     space to NDC, and _WevaViewportOrigin so screen-space instance
    //     positions are shifted into the RT's coordinate frame.
    //
    // After the caller has drawn the scope's batches INTO the temp RT, Pop():
    //   - Walk the FilterChain functions, running each through a separate
    //     pass on a ping-pong RT (blur-H, blur-V cascaded for large σ; color
    //     matrix; drop-shadow tint). All passes use the existing
    //     Weva_Filter shader (5 passes already authored).
    //   - SetRenderTarget back to the camera color, restore the viewport
    //     globals, composite the final filter RT into the screen at the
    //     scope's offset.
    //
    // Temp RTs are managed via cmd.GetTemporaryRT/ReleaseTemporaryRT, keyed
    // by Shader.PropertyToID("_WevaFilterTempRt_N"). The Pool grows
    // monotonically over the frame and is recycled on EndFrame so reused
    // scopes hit the same RT names (cheaper than reallocating, and Unity's
    // RT cache hits on identical descriptors).
    //
    // Y-flip handling: the quad shader uses `ndc.y *= -_ProjectionParams.x`
    // which Unity sets based on the camera/target combination. For temp
    // ARGB32 RTs created via GetTemporaryRT, _ProjectionParams stays as
    // the camera's value (URP doesn't update it on mid-pass SetRenderTarget).
    // The legacy FilterPipeline relies on the same invariant.
    public sealed class UIRenderGraphFilterRuntime : IDisposable {
        public static readonly int IdMainTex = Shader.PropertyToID("_MainTex");
        public static readonly int IdMainTexTexelSize = Shader.PropertyToID("_MainTex_TexelSize");
        public static readonly int IdFilterParams = Shader.PropertyToID("_WevaFilterParams");
        public static readonly int IdFilterMatrixRow0 = Shader.PropertyToID("_WevaFilterMatrixRow0");
        public static readonly int IdFilterMatrixRow1 = Shader.PropertyToID("_WevaFilterMatrixRow1");
        public static readonly int IdFilterMatrixRow2 = Shader.PropertyToID("_WevaFilterMatrixRow2");
        public static readonly int IdFilterMatrixRow3 = Shader.PropertyToID("_WevaFilterMatrixRow3");
        public static readonly int IdFilterMatrixBias = Shader.PropertyToID("_WevaFilterMatrixBias");
        public static readonly int IdFilterDropShadowTint = Shader.PropertyToID("_WevaFilterDropShadowTint");
        public static readonly int IdFilterClipRect = Shader.PropertyToID("_WevaFilterClipRect");
        public static readonly int IdFilterClipRadii = Shader.PropertyToID("_WevaFilterClipRadii");
        public static readonly int IdFilterClipRadiiY = Shader.PropertyToID("_WevaFilterClipRadiiY");
        public static readonly int IdFilterClipEnabled = Shader.PropertyToID("_WevaFilterClipEnabled");
        public static readonly int IdFilterSourceYFlip = Shader.PropertyToID("_WevaFilterSourceYFlip");
        public static readonly int IdFilterEncodeSrgb = Shader.PropertyToID("_WevaFilterEncodeSrgb");
        public static readonly int IdFilterDecodeSrgb = Shader.PropertyToID("_WevaFilterDecodeSrgb");
        public static readonly int IdRawFilterOutput = Shader.PropertyToID("_WevaRawFilterOutput");

        // Match the legacy FilterPipeline's σ cap so large radii cascade through
        // multiple Gaussian passes (n cascades of σ/√n ≈ one Gaussian of σ).
        public const int MaxSigmaPerPass = 16;
        // Rendering from one temp RT into another goes through the same
        // projection-Y convention as the camera target. Flip source UVs on
        // those internal copies so the visible orientation does not depend on
        // how many filter passes a chain happens to run.
        const bool InternalFilterSourceYFlip = true;

        readonly Mesh fullscreenQuad;
        readonly Material filterMaterial;
        readonly List<int> tempRtFreeList = new List<int>(8);
        readonly List<int> tempRtNames = new List<int>(8);
        int rtNameCounter;

        // Per-scope filter cache. When a scope is rendered, the filtered
        // result is also preserved in a persistent RenderTexture indexed by
        // scope key. A later frame can replay it only if the key, rect,
        // filter chain, and inner content hash still match. That keeps the
        // match3 aurora blur cheap without letting a newly inserted hover
        // filter reuse the wrong cached RT.
        sealed class FilterScopeCache {
            public RenderTexture Rt;
            public int W, H;
            public int ScreenX, ScreenY;
            public int Fingerprint;
            public int ContentHash;
            public bool Populated;
            // Y-flip the composite must apply when drawing this cache.Rt back to
            // the parent. The blur-final path writes the result straight into
            // cache.Rt via the flipping V-pass, so it needs a flip at composite;
            // the CopyTexture path (drop-shadow / color-matrix finals) mirrors
            // the fresh DrawQuadAtPx(sourceYFlip:false) draw, so it needs none.
            // Stored per-scope so the cached-replay path stays consistent with
            // how each scope's RT was produced.
            public bool CompositeYFlip;
            // R5/R2: full-frame index of the last time this entry was queried
            // (HasCachedScope hit) or (re)populated. Stale entries — whose
            // scopeKey (a churning BeginIndex) shifted away and is never asked
            // for again — are evicted + their RT released after
            // ScopeEvictAfterFrames, instead of growing the cache for the
            // process lifetime on any dynamic `filter:` page.
            public int LastTouchedFrame;
        }
        readonly Dictionary<int, FilterScopeCache> scopeCaches = new Dictionary<int, FilterScopeCache>(4);
        // Frame-aged eviction state. frameCounter advances per ResetFrame
        // (full frame); an entry untouched for this many frames is released.
        // ~2s at 60fps — long enough that a scope cycling on/off across a few
        // frames isn't thrashed, short enough to bound VRAM on churny pages.
        const int ScopeEvictAfterFrames = 120;
        int frameCounter;
        readonly List<int> scopeEvictScratch = new List<int>(8);

        public bool IsReady => filterMaterial != null;

        // Returns true iff `scopeKey` has a populated cache with matching
        // scope identity and content. The pass uses this to decide whether
        // to skip the BeginScope -> batch draw -> filter chain sequence.
        public bool HasCachedScope(int scopeKey, int x, int y, int w, int h,
                                   FilterChain filters, int contentHash) {
            if (scopeCaches.TryGetValue(scopeKey, out var c)
                && c.Populated
                && c.Rt != null
                && c.Fingerprint == ComputeScopeFingerprint(x, y, w, h, filters)
                && c.ContentHash == contentHash) {
                c.LastTouchedFrame = frameCounter; // keep alive: queried this frame
                return true;
            }
            return false;
        }

        internal static int ComputeScopeFingerprint(int x, int y, int w, int h, FilterChain filters) {
            unchecked {
                int hash = 17;
                hash = (hash * 397) ^ x;
                hash = (hash * 397) ^ y;
                hash = (hash * 397) ^ w;
                hash = (hash * 397) ^ h;
                hash = (hash * 397) ^ (filters?.GetHashCode() ?? 0);
                return hash;
            }
        }

        // Invalidate every scope cache. Called at the start of every full
        // frame so a re-emit (which can produce different content per scope)
        // re-renders the chain from scratch. Idle frames preserve the cache.
        public void InvalidateScopeCaches() {
            foreach (var kv in scopeCaches) {
                kv.Value.Populated = false;
            }
        }

        public UIRenderGraphFilterRuntime(ShaderResources resources) {
            if (resources == null) throw new ArgumentNullException(nameof(resources));
            fullscreenQuad = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            fullscreenQuad.MarkDynamic();
            filterMaterial = resources.GetFilter(BlendKind.PremultipliedAlpha);
        }

        // Computes the AABB of a filter scope in screen-pixel space, applying
        // the transform recorded on the Push event. The scope's local bounds
        // are box-coordinates; the transform maps them to absolute pixels.
        // The result is clipped to (0,0,viewportW,viewportH) and padded by the
        // chain's max blur radius so the Gaussian halo lives inside the RT.
        public static (int X, int Y, int W, int H) ComputeRtRect(
                Paint.Rect bounds, Transform2D transform, FilterChain filters,
                int viewportWidth, int viewportHeight) {
            // Apply transform to all four corners; the bounding box of the
            // transformed quad is the screen-space extent.
            var (x0, y0) = transform.Apply(bounds.X, bounds.Y);
            var (x1, y1) = transform.Apply(bounds.Right, bounds.Y);
            var (x2, y2) = transform.Apply(bounds.X, bounds.Bottom);
            var (x3, y3) = transform.Apply(bounds.Right, bounds.Bottom);
            double minX = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3));
            double minY = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3));
            double maxX = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
            double maxY = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));
            int padPx = ComputePadding(filters);
            int rx0 = (int)Math.Floor(minX) - padPx;
            int ry0 = (int)Math.Floor(minY) - padPx;
            int rx1 = (int)Math.Ceiling(maxX) + padPx;
            int ry1 = (int)Math.Ceiling(maxY) + padPx;
            if (rx0 < 0) rx0 = 0;
            if (ry0 < 0) ry0 = 0;
            if (rx1 > viewportWidth) rx1 = viewportWidth;
            if (ry1 > viewportHeight) ry1 = viewportHeight;
            int w = rx1 - rx0;
            int h = ry1 - ry0;
            if (w < 0) w = 0;
            if (h < 0) h = 0;
            return (rx0, ry0, w, h);
        }

        static int ComputePadding(FilterChain filters) {
            if (filters == null || filters.Functions == null) return 0;
            int max = 0;
            for (int i = 0; i < filters.Functions.Count; i++) {
                switch (filters.Functions[i]) {
                    case BlurFilter bf:
                        // CSS Filter Effects 1 §6.1: σ = radiusPx, so the Gaussian
                        // halo extends ~3σ beyond the source edge. Pad by 3 ×
                        // radius to keep the halo inside the offscreen RT. Matches
                        // the FilterPipeline padding post-A3.
                        int p = (int)Math.Ceiling(bf.RadiusPx * 3.0);
                        if (p > max) max = p;
                        break;
                    case DropShadowFilter ds:
                        int dsPad = (int)Math.Ceiling(
                            Math.Max(Math.Abs(ds.OffsetX), Math.Abs(ds.OffsetY))
                                + ds.BlurRadius * 3.0);
                        if (dsPad > max) max = dsPad;
                        break;
                }
            }
            return max;
        }

        // Begin a filter scope. Allocates a temp RT, redirects the bound
        // render target to it, clears, and sets the viewport globals so the
        // quad shader maps screen-space instance positions correctly into the
        // sub-RT. Returns the RT name id (caller stashes it for Pop) and the
        // scope rect (used for the composite back).
        public int BeginScope(CommandBuffer cb, int x, int y, int w, int h,
                              int viewportOriginNameId, int viewportNameId) {
            int rtId = AllocTempRt(cb, w, h);
            cb.SetRenderTarget(new RenderTargetIdentifier(rtId));
            cb.SetViewport(new UnityEngine.Rect(0f, 0f, w, h));
            cb.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));
            cb.SetGlobalVector(viewportNameId,
                new Vector4(w, h, w > 0 ? 1f / w : 0f, h > 0 ? 1f / h : 0f));
            // Shift screen-space positions into RT-local space inside the
            // vertex shader. The instance data still carries absolute pixel
            // positions (so the per-fragment AABB clip-rect test stays valid
            // — the clip rects are also in screen space).
            cb.SetGlobalVector(viewportOriginNameId, new Vector4(x, y, 0f, 0f));
            return rtId;
        }

        public void ApplyBackdropAndComposite(CommandBuffer cb,
                                              RenderTargetIdentifier backdropSourceTarget,
                                              RenderTargetIdentifier parentTarget,
                                              Paint.Rect bounds,
                                              BorderRadii radii,
                                              FilterChain filters,
                                              Transform2D transform,
                                              bool backdropSourceYFlip,
                                              int parentWidth, int parentHeight,
                                              int viewportOriginNameId, int viewportNameId,
                                              bool srgbComposite = false) {
            if (filters == null || filters.IsEmpty) return;
            var (clipX, clipY, clipW, clipH) = ComputeRtRect(bounds, transform, FilterChain.Empty, parentWidth, parentHeight);
            if (clipW <= 0 || clipH <= 0) return;
            // CSS backdrop-filter applies the filter to the Backdrop Root
            // Image, then clips the result to this element's border box. A
            // blur therefore needs source pixels around the element; filtering
            // only the exact border-box creates a visible hard RT boundary at
            // rounded corners. Capture the filter-inflated region, but only
            // composite the original border-box back.
            var (sourceX, sourceY, sourceW, sourceH) = ComputeRtRect(bounds, transform, filters, parentWidth, parentHeight);
            if (sourceW <= 0 || sourceH <= 0) return;

            int source = AllocTempRt(cb, sourceW, sourceH);
            cb.SetRenderTarget(new RenderTargetIdentifier(source));
            cb.SetViewport(new UnityEngine.Rect(0f, 0f, sourceW, sourceH));
            cb.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));
            cb.SetGlobalVector(viewportNameId,
                new Vector4(sourceW, sourceH, sourceW > 0 ? 1f / sourceW : 0f, sourceH > 0 ? 1f / sourceH : 0f));
            cb.SetGlobalVector(viewportOriginNameId, Vector4.zero);
            var (u0, v0, u1, v1) = ComputeBackdropCaptureUvs(
                sourceX, sourceY, sourceW, sourceH, parentWidth, parentHeight, backdropSourceYFlip);
            // The composite vertex shader already handles target projection
            // differences via _ProjectionParams.x. If the readable backdrop
            // copy is inverted, swap the source UV endpoints inside this
            // same rect so the fix changes orientation without moving the
            // sampled screen area.
            //
            // A-SRGB-COMPOSITE: under gamma compositing the captured backdrop is
            // sRGB-ENCODED (it was blitted from the sRGB intermediate). The blur
            // / colour-matrix chain expects LINEAR premul (light-correct blur,
            // matching Chrome), so decode sRGB -> linear on this capture draw.
            // The global is sequenced, so set it only around this draw.
            if (srgbComposite) cb.SetGlobalFloat(IdFilterDecodeSrgb, 1f);
            DrawQuadAtPx(cb, backdropSourceTarget, 0, 0, sourceW, sourceH, ShaderResources.FilterCompositePass,
                u0, v0, u1, v1, sourceYFlip: false);
            if (srgbComposite) cb.SetGlobalFloat(IdFilterDecodeSrgb, 0f);

            int current = source;
            int currentW = sourceW;
            int currentH = sourceH;
            // IN-EDITOR CALIBRATION (2026-07, audit-validation page §11): the
            // initial capture DrawQuadAtPx above is itself an internal blit —
            // its OUTPUT carries the same Y-inversion every chain pass does
            // (InternalFilterSourceYFlip), but it was never counted, leaving
            // both backdrop paths off by exactly one flip. Symptoms that
            // pinned it: the per-panel path (RT = just the panel's band)
            // rendered the RIGHT region UPSIDE DOWN; the shared path (RT =
            // whole screen) relocated the crop to the MIRRORED band (a panel
            // near the screen bottom blurred the top of the page). Invisible
            // on glass.html — a blurred smooth gradient is mirror-symmetric
            // to the eye — and offscreen probes can't calibrate orientation
            // (they take a different flip path), so this needed live eyes.
            bool bdFlipped = InternalFilterSourceYFlip;
            bool hasPendingColorMatrix = false;
            ColorMatrix pendingColorMatrix = ColorMatrix.Identity;
            for (int fi = 0; fi < filters.Functions.Count; fi++) {
                var fn = filters.Functions[fi];
                int next = current;
                if (TryGetColorMatrix(fn, out var colorMatrix)) {
                    pendingColorMatrix = hasPendingColorMatrix
                        ? ColorMatrices.Compose(pendingColorMatrix, colorMatrix)
                        : colorMatrix;
                    hasPendingColorMatrix = true;
                    continue;
                }

                if (hasPendingColorMatrix) {
                    next = ApplyColorMatrix(cb, current, currentW, currentH,
                        pendingColorMatrix, viewportOriginNameId, viewportNameId);
                    if (next != current && current != source && current != -1) ReleaseTempRt(cb, current);
                    current = next;
                    if (InternalFilterSourceYFlip) bdFlipped = !bdFlipped;
                    hasPendingColorMatrix = false;
                    pendingColorMatrix = ColorMatrix.Identity;
                    next = current;
                }

                switch (fn) {
                    case BlurFilter bf:
                        next = ApplyBlur(cb, current, currentW, currentH, bf.RadiusPx,
                            viewportOriginNameId, viewportNameId, finalDestRt: null, isBackdrop: true);
                        // ApplyBlur returns its result at the downsampled size
                        // (w/factor, h/factor). Track that so a trailing colour
                        // matrix (glass.css's `saturate()` after `blur()`) runs
                        // at the smaller size too instead of upscaling back to
                        // full res. The final composite samples with normalised
                        // crop UVs, so the lower resolution is invisible.
                        int bdFactor = EffectiveBlurFactor(bf.RadiusPx, currentW, currentH, isBackdrop: true);
                        currentW = Math.Max(1, currentW / bdFactor);
                        currentH = Math.Max(1, currentH / bdFactor);
                        // ORIENTATION LEDGER F1 (2026-07): the single-step
                        // backdrop downsample blit inside ApplyBlur draws
                        // sourceYFlip:false, which under the live projection
                        // convention is ONE MORE internal inversion — exactly
                        // like the capture and colour-matrix blits that ARE
                        // counted. It exists only when the factor exceeds 1
                        // (radius > 5px), so the correct composite flip is
                        // RADIUS-DEPENDENT: blur(4) rest was right while
                        // blur(22) hover mirrored, which is why both fixed
                        // signs of entry.Flipped failed — each fixed one
                        // radius regime and broke the other. Count it here
                        // (H+V blur passes stay self-cancelling).
                        if (BackdropDownsampleTogglesFlip(bdFactor)) bdFlipped = !bdFlipped;
                        break;
                }
                if (next != current && current != source && current != -1) ReleaseTempRt(cb, current);
                current = next;
            }
            if (hasPendingColorMatrix) {
                int next = ApplyColorMatrix(cb, current, currentW, currentH,
                    pendingColorMatrix, viewportOriginNameId, viewportNameId);
                if (next != current && current != source && current != -1) ReleaseTempRt(cb, current);
                current = next;
                if (InternalFilterSourceYFlip) bdFlipped = !bdFlipped;
            }

            cb.SetRenderTarget(parentTarget, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            cb.SetViewport(new UnityEngine.Rect(0f, 0f, parentWidth, parentHeight));
            cb.SetGlobalVector(viewportNameId,
                new Vector4(parentWidth, parentHeight,
                    parentWidth > 0 ? 1f / parentWidth : 0f,
                    parentHeight > 0 ? 1f / parentHeight : 0f));
            cb.SetGlobalVector(viewportOriginNameId, Vector4.zero);
            double sourceInvW = 1.0 / Math.Max(1, sourceW);
            double sourceInvH = 1.0 / Math.Max(1, sourceH);
            double cropU0 = (clipX - sourceX) * sourceInvW;
            double cropV0 = (clipY - sourceY) * sourceInvH;
            double cropU1 = (clipX + clipW - sourceX) * sourceInvW;
            double cropV1 = (clipY + clipH - sourceY) * sourceInvH;
            // bdFlipped tracks whether the filter chain's internal blits
            // left the final current RT with an odd number of Y-flips
            // relative to the backdrop source. The Gaussian H+V passes are
            // parity-neutral; a colour-matrix pass contributes one flip; and
            // a backdrop blur's DOWNSAMPLE blit contributes one flip when
            // the radius-based factor exceeds 1 (orientation ledger F1 —
            // counted in the chain walker above).
            // Use bdFlipped as sourceYFlip so the composite reads the RT in
            // the correct orientation regardless of the chain composition.
            // A-SRGB-COMPOSITE: the filtered result is LINEAR premul; encode it
            // to sRGB premul so it lands correctly in the sRGB intermediate (the
            // same Weva_EncodeForTarget every other fill goes through). Flag-off
            // keeps the raw passthrough into the linear camera target.
            DrawQuadAtPx(cb, current, clipX, clipY, clipW, clipH, ShaderResources.FilterCompositePass,
                radii, cropU0, cropV0, cropU1, cropV1, sourceYFlip: bdFlipped, encodeSrgb: srgbComposite);
            ReleaseTempRt(cb, source);
            if (current != source && current != -1) ReleaseTempRt(cb, current);
        }

        // ───────────────────────────────────────────────────────────────
        // Shared backdrop blur. Pages like glass.html have many panels that
        // all blur the SAME backdrop (a static-position scene painted behind
        // them). Instead of capture+blurring the backdrop once PER panel, blur
        // the whole-screen backdrop ONCE per distinct filter chain into a
        // persistent RT, then each panel composites a crop of it. This is
        // correct ONLY for a panel whose backdrop IS the shared capture — i.e.
        // a panel disjoint from every earlier-painted backdrop scope. A nested
        // glass child (whose backdrop includes its glass parent) is NOT
        // eligible and keeps the per-panel ApplyBackdropAndComposite path.
        // The pass decides eligibility (ComputeBackdropShareability) and builds
        // all eligible chains from the pristine scene at the first eligible
        // event, so no panel content bleeds into another panel's blur.
        // 2026-07: briefly quarantined (default false) when the validation
        // page's bisect showed shared panels sampling the wrong vertical band
        // while BOTH fixed signs of entry.Flipped failed. The orientation
        // ledger audit resolved it: the build+crop math here is term-for-term
        // identical to the (user-verified) per-panel path; the one wrong term
        // was the backdrop downsample blit's uncounted inversion, which made
        // the correct composite flip RADIUS-DEPENDENT (right at blur ≤ 5px,
        // mirrored above — so each fixed sign fixed one radius regime and
        // broke the other). Counted now (BackdropDownsampleTogglesFlip), and
        // eligibility demotes on filters-inflated overlap + post-capture
        // content (ComputeBackdropShareability), so the path is back on.
        // Keep UIRenderingDefaults.ResetRenderingDefaults in sync with this
        // default (audit N1: the A6 reset silently overrode the quarantine).
        public static bool EnableSharedBackdropBlur = true;

        sealed class SharedBackdropEntry {
            public RenderTexture Rt;   // whole-screen backdrop blurred at the chain's downsampled size
            public int W, H;           // Rt dimensions (downsampled)
            public bool Flipped;       // composite sourceYFlip (chain's residual Y-flip)
            public int BuiltFrame;     // sharedBackdropFrame this entry was last built for
            // RD1: frameCounter stamp for the eviction sweep (the R2 recipe).
            // Entries are keyed by FilterChain.GetHashCode() — a VALUE hash
            // over the filter parameters — so animating a backdrop-filter
            // blur minted a brand-new whole-screen RT per frame of the
            // transition, and nothing ever released them until Dispose:
            // monotonic VRAM growth per hover on any glass panel.
            public int LastTouchedFrame;
        }
        readonly Dictionary<int, SharedBackdropEntry> sharedBackdrop = new Dictionary<int, SharedBackdropEntry>(2);
        int sharedBackdropFrame;

        // Bumped once per DrainBatches that uses shared backdrop blur so cached
        // entries rebuild every rendered frame (the backdrop animates, so the
        // shared blur is never reused across frames).
        public void NextSharedBackdropFrame() { sharedBackdropFrame++; }

        public bool HasSharedBackdrop(FilterChain filters) {
            if (filters == null) return false;
            if (!sharedBackdrop.TryGetValue(filters.GetHashCode(), out var e)) return false;
            e.LastTouchedFrame = frameCounter; // RD1: keep alive — queried this frame
            return e.BuiltFrame == sharedBackdropFrame && e.Rt != null;
        }

        // Build (once per frame per distinct chain) a whole-screen blur of the
        // current backdrop copy. Mirrors ApplyBackdropAndComposite's capture +
        // filter-chain EXACTLY, but over the full viewport and WITHOUT the final
        // composite — the result is copied into a persistent RT for later crops.
        public void BuildSharedBackdropBlur(CommandBuffer cb,
                                            RenderTargetIdentifier backdropSourceTarget,
                                            FilterChain filters,
                                            bool backdropSourceYFlip,
                                            int parentWidth, int parentHeight,
                                            int viewportOriginNameId, int viewportNameId,
                                            bool srgbComposite = false) {
            if (filters == null || filters.IsEmpty) return;
            if (parentWidth <= 0 || parentHeight <= 0) return;
            int key = filters.GetHashCode();
            if (sharedBackdrop.TryGetValue(key, out var existing)
                && existing.BuiltFrame == sharedBackdropFrame && existing.Rt != null) {
                return; // already built this frame
            }

            int sourceW = parentWidth, sourceH = parentHeight;
            int source = AllocTempRt(cb, sourceW, sourceH);
            cb.SetRenderTarget(new RenderTargetIdentifier(source));
            cb.SetViewport(new UnityEngine.Rect(0f, 0f, sourceW, sourceH));
            cb.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));
            cb.SetGlobalVector(viewportNameId,
                new Vector4(sourceW, sourceH, 1f / sourceW, 1f / sourceH));
            cb.SetGlobalVector(viewportOriginNameId, Vector4.zero);
            var (u0, v0, u1, v1) = ComputeBackdropCaptureUvs(
                0, 0, sourceW, sourceH, parentWidth, parentHeight, backdropSourceYFlip);
            if (srgbComposite) cb.SetGlobalFloat(IdFilterDecodeSrgb, 1f);
            DrawQuadAtPx(cb, backdropSourceTarget, 0, 0, sourceW, sourceH, ShaderResources.FilterCompositePass,
                u0, v0, u1, v1, sourceYFlip: false);
            if (srgbComposite) cb.SetGlobalFloat(IdFilterDecodeSrgb, 0f);

            int current = source;
            int currentW = sourceW, currentH = sourceH;
            // IN-EDITOR CALIBRATION (2026-07, audit-validation page §11): the
            // initial capture DrawQuadAtPx above is itself an internal blit —
            // its OUTPUT carries the same Y-inversion every chain pass does
            // (InternalFilterSourceYFlip), but it was never counted, leaving
            // both backdrop paths off by exactly one flip. Symptoms that
            // pinned it: the per-panel path (RT = just the panel's band)
            // rendered the RIGHT region UPSIDE DOWN; the shared path (RT =
            // whole screen) relocated the crop to the MIRRORED band (a panel
            // near the screen bottom blurred the top of the page). Invisible
            // on glass.html — a blurred smooth gradient is mirror-symmetric
            // to the eye — and offscreen probes can't calibrate orientation
            // (they take a different flip path), so this needed live eyes.
            bool bdFlipped = InternalFilterSourceYFlip;
            bool hasPendingColorMatrix = false;
            ColorMatrix pendingColorMatrix = ColorMatrix.Identity;
            for (int fi = 0; fi < filters.Functions.Count; fi++) {
                var fn = filters.Functions[fi];
                int next = current;
                if (TryGetColorMatrix(fn, out var colorMatrix)) {
                    pendingColorMatrix = hasPendingColorMatrix
                        ? ColorMatrices.Compose(pendingColorMatrix, colorMatrix)
                        : colorMatrix;
                    hasPendingColorMatrix = true;
                    continue;
                }
                if (hasPendingColorMatrix) {
                    next = ApplyColorMatrix(cb, current, currentW, currentH,
                        pendingColorMatrix, viewportOriginNameId, viewportNameId);
                    if (next != current && current != source && current != -1) ReleaseTempRt(cb, current);
                    current = next;
                    if (InternalFilterSourceYFlip) bdFlipped = !bdFlipped;
                    hasPendingColorMatrix = false;
                    pendingColorMatrix = ColorMatrix.Identity;
                    next = current;
                }
                switch (fn) {
                    case BlurFilter bf:
                        next = ApplyBlur(cb, current, currentW, currentH, bf.RadiusPx,
                            viewportOriginNameId, viewportNameId, finalDestRt: null, isBackdrop: true);
                        int f = EffectiveBlurFactor(bf.RadiusPx, currentW, currentH, isBackdrop: true);
                        currentW = Math.Max(1, currentW / f);
                        currentH = Math.Max(1, currentH / f);
                        // ORIENTATION LEDGER F1: count the downsample blit's
                        // inversion, same as ApplyBackdropAndComposite (see
                        // the comment there). This was THE shared-path band
                        // bug: entry.Flipped was radius-wrong above 5px.
                        if (BackdropDownsampleTogglesFlip(f)) bdFlipped = !bdFlipped;
                        break;
                }
                if (next != current && current != source && current != -1) ReleaseTempRt(cb, current);
                current = next;
            }
            if (hasPendingColorMatrix) {
                int next = ApplyColorMatrix(cb, current, currentW, currentH,
                    pendingColorMatrix, viewportOriginNameId, viewportNameId);
                if (next != current && current != source && current != -1) ReleaseTempRt(cb, current);
                current = next;
                if (InternalFilterSourceYFlip) bdFlipped = !bdFlipped;
            }

            var entry = existing ?? new SharedBackdropEntry();
            if (entry.Rt == null || entry.Rt.width != currentW || entry.Rt.height != currentH) {
                // Release+Destroy (audit RD5): a bare Release() frees the GPU
                // memory but leaks the native RenderTexture object shell on
                // every resize event. Same helper the R2 eviction uses.
                ReleaseScopeRt(entry.Rt);
                entry.Rt = new RenderTexture(Math.Max(1, currentW), Math.Max(1, currentH), 0,
                    FilterRtFormat, RenderTextureReadWrite.Linear) {
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear,
                    useMipMap = false,
                    autoGenerateMips = false,
                };
                entry.Rt.Create();
            }
            cb.CopyTexture(new RenderTargetIdentifier(current), new RenderTargetIdentifier(entry.Rt));
            entry.W = currentW;
            entry.H = currentH;
            // bdFlipped is now radius-correct: the chain walker above counts
            // the capture blit, colour-matrix blits AND the downsample blit
            // (orientation ledger F1 — the previously uncounted term that
            // made "both signs of entry.Flipped fail": the right sign
            // depended on whether the radius crossed the 5px downsample
            // threshold). With that counted, entry.Flipped + the full-screen
            // crop in CompositeSharedBackdrop are exactly the per-panel
            // composite math (a full-texture V mirror both relocates AND
            // reverses, which is the correct correction for a whole-screen
            // mirrored image).
            entry.Flipped = bdFlipped;
            entry.BuiltFrame = sharedBackdropFrame;
            entry.LastTouchedFrame = frameCounter; // RD1: populated this frame
            sharedBackdrop[key] = entry;

            ReleaseTempRt(cb, source);
            if (current != source && current != -1) ReleaseTempRt(cb, current);
        }

        // Composite one panel's border-box crop from the pre-built shared blur
        // for its chain. The shared RT spans the whole viewport, so the panel's
        // clip rect maps to normalised crop UVs; the rounded border box clips
        // the draw (same FilterCompositePass + flip/encode contract as the
        // per-panel composite at the tail of ApplyBackdropAndComposite).
        public void CompositeSharedBackdrop(CommandBuffer cb,
                                            RenderTargetIdentifier parentTarget,
                                            Paint.Rect bounds,
                                            BorderRadii radii,
                                            FilterChain filters,
                                            Transform2D transform,
                                            int parentWidth, int parentHeight,
                                            int viewportOriginNameId, int viewportNameId,
                                            bool srgbComposite = false) {
            if (filters == null || filters.IsEmpty) return;
            if (!sharedBackdrop.TryGetValue(filters.GetHashCode(), out var e)
                || e.Rt == null || e.BuiltFrame != sharedBackdropFrame) {
                return; // not built this frame — caller should have used the per-panel path
            }
            var (clipX, clipY, clipW, clipH) = ComputeRtRect(bounds, transform, FilterChain.Empty, parentWidth, parentHeight);
            if (clipW <= 0 || clipH <= 0) return;

            cb.SetRenderTarget(parentTarget, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            cb.SetViewport(new UnityEngine.Rect(0f, 0f, parentWidth, parentHeight));
            cb.SetGlobalVector(viewportNameId,
                new Vector4(parentWidth, parentHeight, 1f / parentWidth, 1f / parentHeight));
            cb.SetGlobalVector(viewportOriginNameId, Vector4.zero);

            double invW = 1.0 / parentWidth, invH = 1.0 / parentHeight;
            double cropU0 = clipX * invW;
            double cropV0 = clipY * invH;
            double cropU1 = (clipX + clipW) * invW;
            double cropV1 = (clipY + clipH) * invH;

            var mpb = RentMpb();
            mpb.SetTexture(IdMainTex, e.Rt);
            mpb.SetVector(IdMainTexTexelSize,
                new Vector4(e.W > 0 ? 1f / e.W : 0f, e.H > 0 ? 1f / e.H : 0f, e.W, e.H));
            mpb.SetFloat(IdFilterSourceYFlip, e.Flipped ? 1f : 0f);
            mpb.SetFloat(IdFilterEncodeSrgb, srgbComposite ? 1f : 0f);
            SetCompositeClip(mpb, clipX, clipY, clipW, clipH, true, radii);
            scratchPositions[0] = new Vector3(clipX, clipY, 0f);
            scratchPositions[1] = new Vector3(clipX, clipY + clipH, 0f);
            scratchPositions[2] = new Vector3(clipX + clipW, clipY + clipH, 0f);
            scratchPositions[3] = new Vector3(clipX + clipW, clipY, 0f);
            scratchUvs[0] = new Vector2((float)cropU0, (float)cropV0);
            scratchUvs[1] = new Vector2((float)cropU0, (float)cropV1);
            scratchUvs[2] = new Vector2((float)cropU1, (float)cropV1);
            scratchUvs[3] = new Vector2((float)cropU1, (float)cropV0);
            var quad = RentFilterQuad();
            quad.Clear();
            quad.SetVertices(scratchPositions);
            quad.SetUVs(0, scratchUvs);
            quad.SetIndices(scratchIndices, MeshTopology.Triangles, 0);
            quad.UploadMeshData(false);
            cb.DrawMesh(quad, Matrix4x4.identity, filterMaterial, 0, ShaderResources.FilterCompositePass, mpb);
        }

        internal static (double U0, double V0, double U1, double V1) ComputeBackdropCaptureUvs(
            int sourceX, int sourceY, int sourceW, int sourceH,
            int parentWidth, int parentHeight, bool sourceYFlip) {
            double invW = 1.0 / Math.Max(1, parentWidth);
            double invH = 1.0 / Math.Max(1, parentHeight);
            double u0 = sourceX * invW;
            double u1 = (sourceX + sourceW) * invW;
            double top = sourceY * invH;
            double bottom = (sourceY + sourceH) * invH;
            // sourceYFlip means the copied backdrop texture is BOTTOM-UP
            // (texel row 0 = screen bottom). Sampling the CSS band
            // [sourceY, sourceY+sourceH] from such a texture requires a
            // FULL-TEXTURE V mirror — both relocating the band to
            // 1-v AND reversing its orientation. The earlier form swapped
            // the band's own endpoints (bottom, top), which reverses
            // orientation but keeps reading the WRONG band: a top-of-screen
            // glass panel blurred the BOTTOM of the screen (the teal blob in
            // the glass sample — GLASS-PANEL-DARK's wrong-tint symptom),
            // while panels near mid-height looked merely off. Mirrors the
            // full-texture flip Weva_SampleBackdropPremul applies for
            // mix-blend-mode, which always had this right.
            return sourceYFlip
                ? (u0, 1.0 - top, u1, 1.0 - bottom)
                : (u0, top, u1, bottom);
        }

        // End the filter scope. Walks the chain on the temp RT (ping-pong),
        // then composites the result back into the parent target at the
        // scope's screen-space offset. The caller must SetRenderTarget back
        // to the parent BEFORE calling this method's composite step is fine —
        // we handle the target switch ourselves to keep the parent-target
        // identifier inside the runtime.
        // Composites the cached filtered output for `scopeKey` onto
        // `parentTarget`. Called in place of the full BeginScope -> batch
        // draw -> filter chain -> EndScopeAndComposite pipeline. The caller
        // must have verified HasCachedScope with the current rect, filters,
        // and content hash first.
        public void CompositeCachedScope(CommandBuffer cb, int scopeKey,
                                          RenderTargetIdentifier parentTarget,
                                          int parentWidth, int parentHeight,
                                          int viewportOriginNameId, int viewportNameId) {
            CompositeCachedScope(cb, scopeKey, parentTarget, parentWidth, parentHeight,
                viewportOriginNameId, viewportNameId, Transform2D.Identity);
        }

        // Per CSS Filter Effects L1, filter applies before transform: the
        // cached RT holds the un-transformed blurred result, and the owner
        // element's CSS `transform` is applied at composite time via
        // `scopeBoxTransform`. Identity = no extra shift (legacy behaviour).
        public void CompositeCachedScope(CommandBuffer cb, int scopeKey,
                                          RenderTargetIdentifier parentTarget,
                                          int parentWidth, int parentHeight,
                                          int viewportOriginNameId, int viewportNameId,
                                          Transform2D scopeBoxTransform) {
            if (!scopeCaches.TryGetValue(scopeKey, out var cache)) return;
            if (!cache.Populated || cache.Rt == null) return;
            cb.SetRenderTarget(parentTarget,
                RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            cb.SetViewport(new UnityEngine.Rect(0f, 0f, parentWidth, parentHeight));
            cb.SetGlobalVector(viewportNameId,
                new Vector4(parentWidth, parentHeight,
                    parentWidth > 0 ? 1f / parentWidth : 0f,
                    parentHeight > 0 ? 1f / parentHeight : 0f));
            cb.SetGlobalVector(viewportOriginNameId, Vector4.zero);
            // The persistent RT is bound by RenderTexture (a concrete
            // Texture instance), so MPB.SetTexture works directly — no need
            // for the cb.SetGlobalTexture fallback the temp-RT path uses.
            var mpb = RentMpb();
            mpb.SetTexture(IdMainTex, cache.Rt);
            mpb.SetVector(IdMainTexTexelSize,
                new Vector4(cache.W > 0 ? 1f / cache.W : 0f,
                            cache.H > 0 ? 1f / cache.H : 0f,
                            cache.W, cache.H));
            // The flip depends on how this scope's cache.Rt was produced:
            // CopyTexture'd finals (drop-shadow / color-matrix) need none, but a
            // blur-final wrote cache.Rt through the flipping V-pass and needs a
            // flip here (else the blurred result composites Y-mirrored — the
            // text-shadow-above-glyphs bug). Stored per-scope at populate time.
            mpb.SetFloat(IdFilterSourceYFlip, cache.CompositeYFlip ? 1f : 0f);
            mpb.SetFloat(IdFilterEncodeSrgb, 1f);
            BuildAndDrawCompositeQuad(cb, cache.ScreenX, cache.ScreenY, cache.W, cache.H, mpb, scopeBoxTransform);
        }

        void BuildAndDrawCompositeQuad(CommandBuffer cb, double x, double y, double w, double h, MaterialPropertyBlock mpb) {
            BuildAndDrawCompositeQuad(cb, x, y, w, h, mpb, Transform2D.Identity);
        }

        // Overload that applies a 2D affine transform to the four composite
        // corners. The matrix uses the same absolute screen-space convention
        // as Weva-Quad.shader: transform-origin has already been baked
        // into Tx/Ty by the paint converter, so corners must be transformed
        // as full CSS pixel positions, not as center-relative offsets.
        void BuildAndDrawCompositeQuad(CommandBuffer cb, double x, double y, double w, double h, MaterialPropertyBlock mpb, Transform2D scopeBoxTransform) {
            if (scopeBoxTransform.Equals(Transform2D.Identity)) {
                scratchPositions[0] = new Vector3((float)x, (float)y, 0f);
                scratchPositions[1] = new Vector3((float)x, (float)(y + h), 0f);
                scratchPositions[2] = new Vector3((float)(x + w), (float)(y + h), 0f);
                scratchPositions[3] = new Vector3((float)(x + w), (float)y, 0f);
            } else {
                scratchPositions[0] = TransformCompositePoint(scopeBoxTransform, x,     y);
                scratchPositions[1] = TransformCompositePoint(scopeBoxTransform, x,     y + h);
                scratchPositions[2] = TransformCompositePoint(scopeBoxTransform, x + w, y + h);
                scratchPositions[3] = TransformCompositePoint(scopeBoxTransform, x + w, y);
            }
            scratchUvs[0] = new Vector2(0, 0);
            scratchUvs[1] = new Vector2(0, 1);
            scratchUvs[2] = new Vector2(1, 1);
            scratchUvs[3] = new Vector2(1, 0);
            var quad = RentFilterQuad();
            quad.Clear();
            quad.SetVertices(scratchPositions);
            quad.SetUVs(0, scratchUvs);
            quad.SetIndices(scratchIndices, MeshTopology.Triangles, 0);
            quad.UploadMeshData(false);
            cb.DrawMesh(quad, Matrix4x4.identity, filterMaterial, 0, ShaderResources.FilterCompositePass, mpb);
        }

        // Mirrors Weva-Quad.shader's transform path for filter-composite
        // meshes. `t` is a pivot-baked matrix in absolute CSS pixels.
        public static Vector3 TransformCompositePoint(Transform2D t, double px, double py) {
            var (x, y) = t.Apply(px, py);
            return new Vector3((float)x, (float)y, 0f);
        }

        // A-SRGB-COMPOSITE Stage 1: composite the always-on intermediate UI RT
        // (the whole frame's UI drawn into an offscreen RT so the fixed-function
        // blend can run in a chosen colour space) back onto the camera target.
        // Reuses the proven filter composite pass (Blend One OneMinusSrcAlpha =
        // premultiplied-over) and its RT->camera Y-flip convention, so the
        // intermediate's accumulated premultiplied result lands on the camera
        // exactly as if the UI had drawn there directly.
        //   encodeSrgb=false (Stage 1): raw premul passthrough — colour-neutral.
        //   encodeSrgb (Stage 2) will carry the sRGB-premul -> linear-premul
        //   decode so the gamma-space-composited UI lands correctly in the
        //   linear camera target.
        // sourceYFlip mirrors the same orientation hint the filter scope
        // composite uses for RT->camera; default false, validated live.
        public void CompositeUiRtToTarget(CommandBuffer cb, int srcRt, int width, int height,
                                          bool sourceYFlip = false, bool encodeSrgb = false,
                                          bool decodeSrgb = false) {
            // decodeSrgb (Stage 2, Linear projects): the intermediate holds
            // sRGB-encoded premul (gamma-space composited); decode back to
            // linear premul for the linear camera target. Set the global only
            // around this draw — command-buffer globals are sequenced, so the
            // reset keeps it from leaking into any later filter composites.
            if (decodeSrgb) cb.SetGlobalFloat(IdFilterDecodeSrgb, 1f);
            DrawQuadAtPx(cb, srcRt, 0.0, 0.0, width, height, ShaderResources.FilterCompositePass,
                Transform2D.Identity, sourceYFlip, encodeSrgb);
            if (decodeSrgb) cb.SetGlobalFloat(IdFilterDecodeSrgb, 0f);
        }

        public void EndScopeAndComposite(CommandBuffer cb, int sourceRt,
                                          int x, int y, int w, int h,
                                          FilterChain filters,
                                          RenderTargetIdentifier parentTarget,
                                          int parentWidth, int parentHeight,
                                          int viewportOriginNameId, int viewportNameId,
                                          int scopeKey = 0) {
            EndScopeAndComposite(cb, sourceRt, x, y, w, h, filters, parentTarget,
                parentWidth, parentHeight, viewportOriginNameId, viewportNameId,
                Transform2D.Identity, scopeKey, 0);
        }

        // Overload threading the owner box's CSS `transform` through to the
        // composite-quad placement so the per-scope blur cache survives
        // transform-only animations on the filtered element.
        public void EndScopeAndComposite(CommandBuffer cb, int sourceRt,
                                          int x, int y, int w, int h,
                                          FilterChain filters,
                                          RenderTargetIdentifier parentTarget,
                                          int parentWidth, int parentHeight,
                                          int viewportOriginNameId, int viewportNameId,
                                          Transform2D scopeBoxTransform,
                                          int scopeKey = 0,
                                          int scopeContentHash = 0,
                                          int parentOriginX = 0,
                                          int parentOriginY = 0,
                                          bool encodeForTarget = true) {
            // If a scope key was provided, pre-allocate the persistent cache
            // RT so the LAST filter in the chain can write directly into it.
            // The downstream "result is in cache.Rt" path replaces a ~7MB
            // cb.CopyTexture with a no-op for the common case where the
            // chain's last filter is a BlurFilter; other filter types fall
            // back to the old CopyTexture-after-chain path.
            //
            // When the last filter is a BlurFilter we size the cache RT at
            // the blur's downsampled resolution. Composite bilinear-samples
            // the smaller texture so the full-scope screen rect gets
            // naturally upscaled — there's no visible quality loss (the
            // blur already smoothed high frequencies) and every blur pass
            // runs at factor² less pixel work.
            FilterScopeCache cache = null;
            int cacheW = w, cacheH = h;
            if (scopeKey != 0 && filters != null && filters.Functions.Count > 0
                && filters.Functions[filters.Functions.Count - 1] is BlurFilter lastBlur) {
                int factor = EffectiveBlurFactor(lastBlur.RadiusPx, w, h);
                cacheW = Math.Max(1, w / factor);
                cacheH = Math.Max(1, h / factor);
            }
            if (scopeKey != 0) {
                if (!scopeCaches.TryGetValue(scopeKey, out cache)) {
                    cache = new FilterScopeCache();
                    scopeCaches[scopeKey] = cache;
                }
                cache.LastTouchedFrame = frameCounter; // populated this frame
                if (cache.Rt == null || cache.Rt.width != cacheW || cache.Rt.height != cacheH) {
                    ReleaseScopeRt(cache.Rt); // Release+Destroy (audit RD5)
                    cache.Rt = new RenderTexture(Math.Max(1, cacheW), Math.Max(1, cacheH), 0,
                        FilterRtFormat, RenderTextureReadWrite.Linear) {
                        hideFlags = HideFlags.HideAndDontSave,
                        filterMode = FilterMode.Bilinear,
                        useMipMap = false,
                        autoGenerateMips = false,
                    };
                    cache.Rt.Create();
                    cache.W = w;
                    cache.H = h;
                }
                cache.ScreenX = x;
                cache.ScreenY = y;
                cache.Fingerprint = ComputeScopeFingerprint(x, y, w, h, filters);
                cache.ContentHash = scopeContentHash;
                // Even when the cache RT is downsampled, the COMPOSITE quad
                // still draws at the full scope screen rect (w, h) — the
                // bilinear sampler does the upscale at sample-time.
                cache.W = w;
                cache.H = h;
            }

            int current = sourceRt;
            int currentW = w;
            int currentH = h;
            // Track when the result is written into cache.Rt instead of a
            // temp int RT. Set by ApplyBlur returning -1 on the last pass.
            bool resultInCacheRt = false;
            // Track accumulated Y-flip state through the filter chain.
            // Each internal blit with InternalFilterSourceYFlip toggles the
            // orientation. Blur does 2 blits (H+V) so it self-cancels.
            // A lone color-matrix pass does 1 blit → flipped. The
            // composite at the end must compensate for any residual flip.
            bool flipped = false;
            if (filters != null) {
                bool hasPendingColorMatrix = false;
                ColorMatrix pendingColorMatrix = ColorMatrix.Identity;
                for (int fi = 0; fi < filters.Functions.Count; fi++) {
                    var fn = filters.Functions[fi];
                    int next = current;
                    if (TryGetColorMatrix(fn, out var colorMatrix)) {
                        pendingColorMatrix = hasPendingColorMatrix
                            ? ColorMatrices.Compose(pendingColorMatrix, colorMatrix)
                            : colorMatrix;
                        hasPendingColorMatrix = true;
                        continue;
                    }

                    if (hasPendingColorMatrix) {
                        next = ApplyColorMatrix(cb, current, currentW, currentH,
                            pendingColorMatrix,
                            viewportOriginNameId, viewportNameId);
                        if (next != current && current != sourceRt && current != -1) {
                            ReleaseTempRt(cb, current);
                        }
                        current = next;
                        if (InternalFilterSourceYFlip) flipped = !flipped;
                        hasPendingColorMatrix = false;
                        pendingColorMatrix = ColorMatrix.Identity;
                        next = current;
                    }

                    bool isLastFilter = fi == filters.Functions.Count - 1;
                    RenderTexture blurFinalDest = (isLastFilter && cache != null)
                        ? cache.Rt
                        : null;
                    switch (fn) {
                        case BlurFilter bf:
                            next = ApplyBlur(cb, current, currentW, currentH, bf.RadiusPx,
                                viewportOriginNameId, viewportNameId, blurFinalDest);
                            if (next == -1) resultInCacheRt = true;
                            break;
                        case DropShadowFilter ds:
                            next = ApplyDropShadow(cb, current, currentW, currentH, ds,
                                viewportOriginNameId, viewportNameId);
                            if (InternalFilterSourceYFlip) flipped = !flipped;
                            break;
                    }
                    if (next != current && current != sourceRt && current != -1) {
                        ReleaseTempRt(cb, current);
                    }
                    current = next;
                }
                if (hasPendingColorMatrix) {
                    int next = ApplyColorMatrix(cb, current, currentW, currentH,
                        pendingColorMatrix,
                        viewportOriginNameId, viewportNameId);
                    if (next != current && current != sourceRt && current != -1) {
                        ReleaseTempRt(cb, current);
                    }
                    current = next;
                    if (InternalFilterSourceYFlip) flipped = !flipped;
                }
            }

            // Composite back into the parent target at the screen-space
            // offset. CRITICAL: rebind with explicit Load action — the
            // default cb.SetRenderTarget(target) overload uses
            // DontCare/Discard for load actions under URP RenderGraph
            // (the unsafe-pass path), which wipes the body bg that batch 0
            // already painted. With RenderBufferLoadAction.Load the prior
            // content (body bg, html bg, anything drawn pre-filter) is
            // preserved so the composite's premul-alpha blend merges the
            // blurred aurora onto the existing canvas instead of replacing
            // it. Symptom before this fix: every pixel went pure white
            // because the discard zeroed the framebuffer, and the
            // composite quad's transparent regions (most of the screen,
            // since the aurora gradients fade to alpha=0 by 40% radius)
            // wrote nothing to fill the discarded contents back.
            // When resultInCacheRt is true, the LAST blur pass wrote
            // directly into cache.Rt and we can skip both the legacy
            // CopyTexture and the int-keyed temp-RT release for `current`.
            // Otherwise (non-blur final filter, or no caching) we fall back
            // to the original CopyTexture path to populate the cache.
            if (scopeKey != 0 && !resultInCacheRt) {
                // Allocator already ran at the top — cache is guaranteed live.
                if (cache.Rt == null || cache.Rt.width != w || cache.Rt.height != h) {
                    ReleaseScopeRt(cache.Rt); // Release+Destroy (audit RD5)
                    cache.Rt = new RenderTexture(Math.Max(1, w), Math.Max(1, h), 0,
                        FilterRtFormat, RenderTextureReadWrite.Linear) {
                        hideFlags = HideFlags.HideAndDontSave,
                        filterMode = FilterMode.Bilinear,
                        useMipMap = false,
                        autoGenerateMips = false,
                    };
                    cache.Rt.Create();
                    cache.W = w;
                    cache.H = h;
                }
                cache.ScreenX = x;
                cache.ScreenY = y;
                cache.Fingerprint = ComputeScopeFingerprint(x, y, w, h, filters);
                cache.ContentHash = scopeContentHash;
                cb.CopyTexture(new RenderTargetIdentifier(current),
                    new RenderTargetIdentifier(cache.Rt));
                cache.Populated = true;
                // CopyTexture preserves the orientation of `current`, which the
                // fresh draw below composites via DrawQuadAtPx(sourceYFlip:false).
                // The cached replay must match → no flip.
                cache.CompositeYFlip = false;
            } else if (scopeKey != 0 && resultInCacheRt) {
                // The last blur pass already wrote into cache.Rt and the
                // caller (DrainBatches) is responsible for the trailing
                // composite. No CopyTexture, no `current` temp to release.
                cache.Populated = true;
                // The blur V-pass that wrote cache.Rt sampled with
                // InternalFilterSourceYFlip, leaving cache.Rt in the flipped
                // orientation; the composite must flip it back. Without this the
                // blurred result (e.g. a text-shadow blur scope) renders
                // Y-mirrored — the shadow lands above the glyphs instead of below.
                cache.CompositeYFlip = InternalFilterSourceYFlip;
            }

            cb.SetRenderTarget(parentTarget,
                RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
            cb.SetViewport(new UnityEngine.Rect(0f, 0f, parentWidth, parentHeight));
            cb.SetGlobalVector(viewportNameId,
                new Vector4(parentWidth, parentHeight,
                    parentWidth > 0 ? 1f / parentWidth : 0f,
                    parentHeight > 0 ? 1f / parentHeight : 0f));
            cb.SetGlobalVector(viewportOriginNameId, Vector4.zero);
            if (resultInCacheRt) {
                // Final blurred result lives in cache.Rt (a RenderTexture).
                // Re-use the cached-scope composite path: it binds via MPB
                // since the persistent RT is a concrete Texture instance.
                // scopeBoxTransform is applied to the composite quad so the
                // owning element's CSS `transform` shifts the blurred result
                // at composite time (filter-before-transform per spec).
                CompositeCachedScope(cb, scopeKey, parentTarget,
                    parentWidth, parentHeight, viewportOriginNameId, viewportNameId,
                    scopeBoxTransform);
            } else {
                DrawQuadAtPx(cb, current, x - parentOriginX, y - parentOriginY, w, h, ShaderResources.FilterCompositePass,
                    scopeBoxTransform, sourceYFlip: false, encodeSrgb: encodeForTarget);
            }

            // Cleanup. The scope's original RT and any intermediate get freed.
            ReleaseTempRt(cb, sourceRt);
            if (!resultInCacheRt && current != sourceRt) ReleaseTempRt(cb, current);
        }

        // Picks a downsample factor for the blur ping-pong RTs based on the
        // requested radius. The visual blur is low-frequency so downsampling
        // costs little quality (the kernel still spans many target-space
        // pixels) but cuts shader work by factor^2 and lets larger radii
        // collapse to a single cascade pass. For match3's
        // `filter: blur(40px)` this drops ~236M sample ops to ~30M without
        // a perceptible change to the aurora wash.
        internal static int PickBlurDownsampleFactor(double radiusPx) {
            // Target σ ≤ FilterPipeline.MaxSigmaPerTapBudget (5) in
            // downsampled-pixel space, so a single H+V pair runs a FULL
            // (un-truncated) Gaussian kernel for radii up to 40. The old
            // {≥32:4, ≥12:2} mapping left e.g. blur(26) at σ'=13, which the
            // 15-tap shader truncated at ~1.15σ — visibly tighter and harder
            // than Chrome's blur. Heavier downsampling is invisible at these
            // radii (features smaller than σ are gone anyway; URP's own bloom
            // chain downsamples just as hard) and makes large blurs CHEAPER.
            // Above 40 the cascade in PlanGaussianBlur covers the remainder.
            if (radiusPx > 20) return 8;
            if (radiusPx > 10) return 4;
            if (radiusPx > 5) return 2;
            return 1;
        }

        // ORIENTATION LEDGER F1 (2026-07): does a backdrop blur's downsample
        // stage contribute one internal Y-inversion to the chain parity?
        // ApplyBlur's isBackdrop branch downsamples with a SINGLE blit drawn
        // sourceYFlip:false — under the live projection convention that blit
        // inverts, exactly like the capture and colour-matrix blits the
        // bdFlipped accounting already counts. The blit only exists when the
        // radius-based factor exceeds 1 (radius > 5px — see
        // PickBlurDownsampleFactor), which made the correct composite flip
        // RADIUS-DEPENDENT and produced the shared-backdrop "both signs of
        // entry.Flipped fail" mystery: blur(4px) rest was correct while
        // blur(22px) hover sampled the Y-mirrored screen position. The
        // per-panel path had the same parity error above 5px, hidden as an
        // in-place mirror of the panel's own (blur-washed) band. Callers
        // toggle bdFlipped when this returns true; the H+V Gaussian passes
        // and the iterative-halving branch (non-backdrop, absorbed by the
        // scope-cache path) are not this helper's concern.
        internal static bool BackdropDownsampleTogglesFlip(int effectiveFactor) =>
            effectiveFactor > 1 && InternalFilterSourceYFlip;

        // Filter scopes smaller than this (on BOTH axes) blur at full
        // resolution regardless of radius. The radius-based downsample above
        // exists to make a full-screen blur wash (match3's `filter: blur(40)`
        // on `.bg-aurora`, ~1080p) cheap; applied to a SMALL scope it is
        // destructive. A text-shadow scope is sized to one run + 3×blur halo
        // (~309×363 px for glass.html's 56px album-art note); downsampling
        // that by 8× crushes the ~29 px glyph to ~3.6 px, and the Gaussian
        // then smears that nub across the whole RT — the shadow composites as
        // a shapeless dark "box" instead of a soft glyph-shaped glow (the
        // glass.html `.art-note` ghost). Full-res blur of a sub-512 px scope
        // is cheap (≤ ~260k px), so there is no perf reason to downsample it.
        internal const int SmallScopeFullResThreshold = 512;

        // The downsample factor actually used for a blur, accounting for scope
        // size. Large scopes keep the radius-based factor (perf); small scopes
        // collapse to factor 1 (quality). Both the cache-RT sizing and the
        // blur passes MUST agree, so they call through here.
        internal static int EffectiveBlurFactor(double radiusPx, int w, int h) {
            if (w <= SmallScopeFullResThreshold && h <= SmallScopeFullResThreshold) return 1;
            return PickBlurDownsampleFactor(radiusPx);
        }

        // backdrop-filter variant. The SmallScopeFullResThreshold exemption
        // exists for `filter:`/text-shadow/drop-shadow scopes, where a small
        // scope is a single glyph SILHOUETTE that downsampling would crush into
        // a shapeless box. A backdrop-filter blur is different in kind: its
        // input is the Backdrop Root Image (whatever the page painted behind
        // the element), a low-frequency wash that is about to be blurred
        // anyway, so downsampling it is invisible (same rationale as the
        // radius-based factor itself — see PickBlurDownsampleFactor). Without
        // this exemption a sub-512px glass panel with blur(26) takes factor 1,
        // so σ=26 can't fit one shader pass and cascades into ~28 FULL-RES
        // Gaussian passes (56 clear/draw pairs per panel — the bulk of
        // glass.html's ~788 draw calls). Routing backdrop blurs through the
        // plain radius-based factor collapses that to a single pass.
        internal static int EffectiveBlurFactor(double radiusPx, int w, int h, bool isBackdrop) {
            if (isBackdrop) return PickBlurDownsampleFactor(radiusPx);
            return EffectiveBlurFactor(radiusPx, w, h);
        }

        // Pure planner for the URP blur path — captures the spec-relevant
        // decisions (per-pass σ, cascade pass count, tap count, downsample
        // factor) without needing a CommandBuffer. The σ that ends up in the
        // shader's _WevaFilterParams is plan.Plan.EffectiveSigma (measured
        // in downsampled-RT pixels). Multiplying by the chosen downsample
        // factor recovers the equivalent σ in the original full-resolution
        // pixel space — which per CSS Filter Effects 1 §6.1 must equal
        // radiusPx (NOT radiusPx / 2 — that was the pre-A3b bug).
        internal readonly struct UrpBlurPlan {
            public readonly double RadiusPx;
            public readonly int DownsampleFactor;
            public readonly double SpecSigmaInDownsampledSpace;
            public readonly FilterPipeline.GaussianBlurPlan Plan;
            public UrpBlurPlan(double radiusPx, int factor, double specSigma, FilterPipeline.GaussianBlurPlan plan) {
                RadiusPx = radiusPx;
                DownsampleFactor = factor;
                SpecSigmaInDownsampledSpace = specSigma;
                Plan = plan;
            }
            // σ measured in the full-resolution screen-pixel space. Per the
            // CSS spec this MUST equal RadiusPx.
            public double EffectiveSigmaInScreenSpace =>
                SpecSigmaInDownsampledSpace * DownsampleFactor;
        }

        internal static UrpBlurPlan PlanBlur(double radiusPx) {
            int factor = PickBlurDownsampleFactor(radiusPx);
            double specSigma = radiusPx / factor;
            var plan = FilterPipeline.PlanGaussianBlur(specSigma);
            return new UrpBlurPlan(radiusPx, factor, specSigma, plan);
        }

        // Scope-size-aware overload: small scopes blur full-res (see
        // EffectiveBlurFactor) so a text-shadow glyph isn't crushed into a box.
        internal static UrpBlurPlan PlanBlur(double radiusPx, int w, int h) {
            int factor = EffectiveBlurFactor(radiusPx, w, h);
            double specSigma = radiusPx / factor;
            var plan = FilterPipeline.PlanGaussianBlur(specSigma);
            return new UrpBlurPlan(radiusPx, factor, specSigma, plan);
        }

        // backdrop-filter variant: a backdrop blur skips the small-scope
        // full-res exemption (see EffectiveBlurFactor(_,_,_,isBackdrop)) so even
        // a small glass panel downsamples before the Gaussian.
        internal static UrpBlurPlan PlanBlur(double radiusPx, int w, int h, bool isBackdrop) {
            int factor = EffectiveBlurFactor(radiusPx, w, h, isBackdrop);
            double specSigma = radiusPx / factor;
            var plan = FilterPipeline.PlanGaussianBlur(specSigma);
            return new UrpBlurPlan(radiusPx, factor, specSigma, plan);
        }

        // Returns -1 to signal "result was written into finalDestRt" so the
        // caller can skip the trailing CopyTexture into the persistent cache.
        // When finalDestRt is null the old behavior is preserved: the result
        // lives in an int-keyed temp RT the caller still owns + must release.
        //
        // When `finalDestRt` is non-null its size is treated as the target
        // resolution for the LAST blur pass — the cache RT can be allocated
        // smaller than the scope's screen size, in which case the blur runs
        // at the smaller size and composite bilinear-upscales when drawing
        // it back to the camera.
        int ApplyBlur(CommandBuffer cb, int srcRt, int w, int h, double radiusPx,
                      int viewportOriginNameId, int viewportNameId,
                      RenderTexture finalDestRt = null, bool isBackdrop = false) {
            if (radiusPx <= 0) {
                if (finalDestRt != null) {
                    // No blur to apply but the caller wants the result in the
                    // persistent RT — copy via a single composite pass so the
                    // downstream skip-CopyTexture path still works.
                    cb.SetRenderTarget(new RenderTargetIdentifier(finalDestRt));
                    int dW = finalDestRt.width, dH = finalDestRt.height;
                    cb.SetViewport(new UnityEngine.Rect(0f, 0f, dW, dH));
                    cb.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));
                    cb.SetGlobalVector(viewportNameId,
                        new Vector4(dW, dH, dW > 0 ? 1f / dW : 0f, dH > 0 ? 1f / dH : 0f));
                    cb.SetGlobalVector(viewportOriginNameId, Vector4.zero);
                    DrawQuadAtPx(cb, srcRt, 0, 0, dW, dH, ShaderResources.FilterCompositePass,
                        Transform2D.Identity, sourceYFlip: InternalFilterSourceYFlip, encodeSrgb: false);
                    return -1;
                }
                return srcRt;
            }
            var urpPlan = PlanBlur(radiusPx, w, h, isBackdrop);
            int factor = urpPlan.DownsampleFactor;
            int bw = Math.Max(1, w / factor);
            int bh = Math.Max(1, h / factor);
            // Downsample step: blit the full-size source into the small
            // blur-space RT. Bilinear filtering naturally averages source
            // texels into the smaller grid. The blur passes then read from
            // this smaller RT, cutting per-pass shader work by factor^2.
            // The downsample also lets us run the blur with proportionally
            // smaller sigma — a 40px visual blur becomes a 20px blur on a
            // half-size RT, which often collapses two cascade passes into
            // one.
            int blurSrc = srcRt;
            int allocatedDownsampleRt = -1;
            if (factor > 1 && isBackdrop) {
                // Single-step downsample for backdrop scopes. The backdrop is a
                // low-frequency wash (glass.html's soft gradient blobs + page
                // gradient), so a direct bilinear downscale to blur space has
                // negligible aliasing and saves the iterative-halving draws
                // (factor 8 → 1 downsample draw instead of 3). The shimmer-prone
                // case the halving below guards against is SHARP filtered content
                // (filter:/drop-shadow glyph silhouettes), which never reaches
                // this branch (isBackdrop is false for those).
                int dsRt = AllocTempRt(cb, bw, bh);
                cb.SetRenderTarget(new RenderTargetIdentifier(dsRt));
                cb.SetViewport(new UnityEngine.Rect(0f, 0f, bw, bh));
                cb.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));
                cb.SetGlobalVector(viewportNameId,
                    new Vector4(bw, bh, bw > 0 ? 1f / bw : 0f, bh > 0 ? 1f / bh : 0f));
                cb.SetGlobalVector(viewportOriginNameId, Vector4.zero);
                DrawQuadAtPx(cb, srcRt, 0, 0, bw, bh, ShaderResources.FilterCompositePass,
                    Transform2D.Identity, sourceYFlip: false, encodeSrgb: false);
                blurSrc = dsRt;
                allocatedDownsampleRt = dsRt;
            } else if (factor > 1) {
                // Downsample by ITERATIVE HALVING: each 2× step is an exact
                // 2×2 box average under bilinear filtering. A single direct
                // blit to 1/4 or 1/8 size samples only 4 of the 16/64 covered
                // texels — aliasing that survives the blur as shimmer on
                // moving content (the old factor-4 path had this latent).
                //
                // Each step is a plain bilinear downscale; it must NOT toggle
                // orientation. The H+V blur passes already self-cancel (2 toggles),
                // so the surrounding flip accounting (and ApplyDropShadow's
                // tint-vs-source "lockstep") assumes the whole blur is
                // orientation-neutral. Using InternalFilterSourceYFlip here added
                // a THIRD toggle whenever the radius was large enough to
                // downsample, so the blurred result came out Y-mirrored —
                // visible as a drop-shadow rendered on the wrong side / a
                // "ghost" of the filtered content above the element
                // (story-bubble's blur(24) drop-shadow). Small blurs (factor 1,
                // e.g. menu's card-shadow) never downsampled, so they were
                // unaffected — which is why this only bit large radii.
                int stepSrc = srcRt;
                int curW = w, curH = h;
                while (curW / 2 >= bw || curH / 2 >= bh) {
                    int nextW = Math.Max(bw, curW / 2);
                    int nextH = Math.Max(bh, curH / 2);
                    int dsRt = AllocTempRt(cb, nextW, nextH);
                    cb.SetRenderTarget(new RenderTargetIdentifier(dsRt));
                    cb.SetViewport(new UnityEngine.Rect(0f, 0f, nextW, nextH));
                    cb.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));
                    cb.SetGlobalVector(viewportNameId,
                        new Vector4(nextW, nextH, nextW > 0 ? 1f / nextW : 0f, nextH > 0 ? 1f / nextH : 0f));
                    cb.SetGlobalVector(viewportOriginNameId, Vector4.zero);
                    DrawQuadAtPx(cb, stepSrc, 0, 0, nextW, nextH, ShaderResources.FilterCompositePass,
                        Transform2D.Identity, sourceYFlip: false, encodeSrgb: false);
                    if (stepSrc != srcRt) ReleaseTempRt(cb, stepSrc);
                    stepSrc = dsRt;
                    curW = nextW; curH = nextH;
                    if (curW == bw && curH == bh) break;
                }
                blurSrc = stepSrc;
                // Only mark for release when the loop actually allocated — for
                // degenerate sizes (source already ≤ the downsample target) the
                // loop never runs and stepSrc is still srcRt, which belongs to
                // the caller.
                allocatedDownsampleRt = stepSrc != srcRt ? stepSrc : -1;
            }
            // CSS Filter Effects 1 §6.1: σ = radiusPx (the spec <length> IS σ
            // directly, NOT a radius to be halved). When the source has been
            // downsampled by `factor` to save shader work, one downsampled-RT
            // pixel covers `factor` source pixels, so the σ measured in
            // downsampled-pixel space is σ / factor (still producing the spec
            // blur once the result is upsampled back). PlanBlur reuses
            // FilterPipeline.PlanGaussianBlur so the URP RenderGraph path and
            // the legacy CommandBuffer path stay in lock-step.
            int passes = urpPlan.Plan.Passes;
            double effectiveSigma = urpPlan.Plan.EffectiveSigma;
            int N = urpPlan.Plan.TapsPerSide;

            int current = blurSrc;
            // When finalDestRt is provided we'd like the LAST blur pass to
            // render directly into it. The destination size determines the
            // blur output size; if the cache RT is half-size we keep blur
            // working at half-size too.
            int destW = finalDestRt != null ? finalDestRt.width : bw;
            int destH = finalDestRt != null ? finalDestRt.height : bh;
            for (int pass = 0; pass < passes; pass++) {
                int hRt = AllocTempRt(cb, bw, bh);
                cb.SetRenderTarget(new RenderTargetIdentifier(hRt));
                cb.SetViewport(new UnityEngine.Rect(0f, 0f, bw, bh));
                cb.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));
                cb.SetGlobalVector(viewportNameId,
                    new Vector4(bw, bh, bw > 0 ? 1f / bw : 0f, bh > 0 ? 1f / bh : 0f));
                cb.SetGlobalVector(viewportOriginNameId, Vector4.zero);
                cb.SetGlobalVector(IdFilterParams,
                    new Vector4((float)effectiveSigma, bw > 0 ? 1f / bw : 0f, bh > 0 ? 1f / bh : 0f, N));
                DrawQuadAtPx(cb, current, 0, 0, bw, bh, ShaderResources.FilterBlurHorizontalPass,
                    Transform2D.Identity, sourceYFlip: InternalFilterSourceYFlip, encodeSrgb: false);
                if (current != blurSrc && current != srcRt) ReleaseTempRt(cb, current);
                current = hRt;

                bool isLastPass = pass == passes - 1;
                if (isLastPass && finalDestRt != null) {
                    cb.SetRenderTarget(new RenderTargetIdentifier(finalDestRt));
                    cb.SetViewport(new UnityEngine.Rect(0f, 0f, destW, destH));
                    cb.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));
                    cb.SetGlobalVector(viewportNameId,
                        new Vector4(destW, destH, destW > 0 ? 1f / destW : 0f, destH > 0 ? 1f / destH : 0f));
                    cb.SetGlobalVector(viewportOriginNameId, Vector4.zero);
                    cb.SetGlobalVector(IdFilterParams,
                        new Vector4((float)effectiveSigma, bw > 0 ? 1f / bw : 0f, bh > 0 ? 1f / bh : 0f, N));
                    DrawQuadAtPx(cb, current, 0, 0, destW, destH, ShaderResources.FilterBlurVerticalPass,
                        Transform2D.Identity, sourceYFlip: InternalFilterSourceYFlip, encodeSrgb: false);
                    ReleaseTempRt(cb, current);
                    if (allocatedDownsampleRt != -1) ReleaseTempRt(cb, allocatedDownsampleRt);
                    return -1;
                }

                int vRt = AllocTempRt(cb, bw, bh);
                cb.SetRenderTarget(new RenderTargetIdentifier(vRt));
                cb.SetViewport(new UnityEngine.Rect(0f, 0f, bw, bh));
                cb.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));
                cb.SetGlobalVector(viewportNameId,
                    new Vector4(bw, bh, bw > 0 ? 1f / bw : 0f, bh > 0 ? 1f / bh : 0f));
                cb.SetGlobalVector(viewportOriginNameId, Vector4.zero);
                cb.SetGlobalVector(IdFilterParams,
                    new Vector4((float)effectiveSigma, bw > 0 ? 1f / bw : 0f, bh > 0 ? 1f / bh : 0f, N));
                DrawQuadAtPx(cb, current, 0, 0, bw, bh, ShaderResources.FilterBlurVerticalPass,
                    Transform2D.Identity, sourceYFlip: InternalFilterSourceYFlip, encodeSrgb: false);
                ReleaseTempRt(cb, current);
                current = vRt;
            }
            if (allocatedDownsampleRt != -1 && allocatedDownsampleRt != current) {
                ReleaseTempRt(cb, allocatedDownsampleRt);
            }
            return current;
        }

        int ApplyColorMatrix(CommandBuffer cb, int srcRt, int w, int h, ColorMatrix m,
                              int viewportOriginNameId, int viewportNameId) {
            int dst = AllocTempRt(cb, w, h);
            cb.SetRenderTarget(new RenderTargetIdentifier(dst));
            cb.SetViewport(new UnityEngine.Rect(0f, 0f, w, h));
            cb.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));
            cb.SetGlobalVector(viewportNameId,
                new Vector4(w, h, w > 0 ? 1f / w : 0f, h > 0 ? 1f / h : 0f));
            cb.SetGlobalVector(viewportOriginNameId, Vector4.zero);
            cb.SetGlobalVector(IdFilterMatrixRow0, m.Row0);
            cb.SetGlobalVector(IdFilterMatrixRow1, m.Row1);
            cb.SetGlobalVector(IdFilterMatrixRow2, m.Row2);
            cb.SetGlobalVector(IdFilterMatrixRow3, m.Row3);
            cb.SetGlobalVector(IdFilterMatrixBias, m.Bias);
            DrawQuadAtPx(cb, srcRt, 0, 0, w, h, ShaderResources.FilterColorMatrixPass,
                Transform2D.Identity, sourceYFlip: false, encodeSrgb: false);
            return dst;
        }

        static bool TryGetColorMatrix(FilterFunction fn, out ColorMatrix matrix) {
            switch (fn) {
                case BrightnessFilter br:
                    matrix = ColorMatrices.Brightness(br.Amount);
                    return true;
                case ContrastFilter ct:
                    matrix = ColorMatrices.Contrast(ct.Amount);
                    return true;
                case GrayscaleFilter gs:
                    matrix = ColorMatrices.Grayscale(gs.Amount);
                    return true;
                case SepiaFilter sp:
                    matrix = ColorMatrices.Sepia(sp.Amount);
                    return true;
                case InvertFilter iv:
                    matrix = ColorMatrices.Invert(iv.Amount);
                    return true;
                case SaturateFilter sat:
                    matrix = ColorMatrices.Saturate(sat.Amount);
                    return true;
                case HueRotateFilter hue:
                    matrix = ColorMatrices.HueRotate(hue.DegreesNormalized);
                    return true;
                case OpacityFilter op:
                    matrix = ColorMatrices.Opacity(op.Amount);
                    return true;
                default:
                    matrix = ColorMatrix.Identity;
                    return false;
            }
        }

        int ApplyDropShadow(CommandBuffer cb, int srcRt, int w, int h, DropShadowFilter ds,
                             int viewportOriginNameId, int viewportNameId) {
            // Tint source's alpha into the shadow color silhouette.
            int tinted = AllocTempRt(cb, w, h);
            cb.SetRenderTarget(new RenderTargetIdentifier(tinted));
            cb.SetViewport(new UnityEngine.Rect(0f, 0f, w, h));
            cb.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));
            cb.SetGlobalVector(viewportNameId,
                new Vector4(w, h, w > 0 ? 1f / w : 0f, h > 0 ? 1f / h : 0f));
            cb.SetGlobalVector(viewportOriginNameId, Vector4.zero);
            var c = ds.Color;
            cb.SetGlobalVector(IdFilterDropShadowTint, new Vector4(c.R, c.G, c.B, c.A));
            // sourceYFlip:false here (NOT InternalFilterSourceYFlip). The shadow
            // silhouette goes through tint → blur → composite, while the source
            // layer goes straight to composite. Both composite draws (lines below)
            // flip, and the outer chain toggles `flipped` once for the whole
            // drop-shadow. That budget matches the SOURCE layer (one composite
            // flip, undone by the toggle — net-zero vs source, same as a working
            // blur). If the tint pass ALSO flipped, the shadow silhouette would
            // carry one extra flip the toggle can't account for and render
            // Y-mirrored relative to the card (menu.html .card-shadow). Reading
            // the source un-flipped here keeps the silhouette's orientation in
            // lockstep with the source layer.
            DrawQuadAtPx(cb, srcRt, 0, 0, w, h, ShaderResources.FilterDropShadowTintPass,
                Transform2D.Identity, sourceYFlip: false, encodeSrgb: false);

            // Blur the silhouette.
            int blurred = ApplyBlur(cb, tinted, w, h, ds.BlurRadius,
                viewportOriginNameId, viewportNameId);
            if (blurred != tinted) ReleaseTempRt(cb, tinted);

            // Composite: shadow at offset, then source at origin on top.
            int composited = AllocTempRt(cb, w, h);
            cb.SetRenderTarget(new RenderTargetIdentifier(composited));
            cb.SetViewport(new UnityEngine.Rect(0f, 0f, w, h));
            cb.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));
            cb.SetGlobalVector(viewportNameId,
                new Vector4(w, h, w > 0 ? 1f / w : 0f, h > 0 ? 1f / h : 0f));
            cb.SetGlobalVector(viewportOriginNameId, Vector4.zero);
            DrawQuadAtPx(cb, blurred, ds.OffsetX, ds.OffsetY, w, h, ShaderResources.FilterCompositePass,
                Transform2D.Identity, sourceYFlip: InternalFilterSourceYFlip, encodeSrgb: false);
            DrawQuadAtPx(cb, srcRt, 0, 0, w, h, ShaderResources.FilterCompositePass,
                Transform2D.Identity, sourceYFlip: InternalFilterSourceYFlip, encodeSrgb: false);

            if (blurred != srcRt) ReleaseTempRt(cb, blurred);
            return composited;
        }

        // Builds and draws a single pixel-space quad covering (x,y,w,h). The
        // shader's vert maps position px by _WevaViewport.zw to NDC.
        //
        // `_MainTex` must be bound via a MaterialPropertyBlock per-draw, not
        // via cb.SetGlobalTexture: under URP RenderGraph's unsafe-pass path
        // the global-texture call is silently dropped (or applied to the
        // wrong material state), so the shader's `_MainTex ("Source", 2D)
        // = "white" {}` fallback fires and SAMPLE_TEXTURE2D returns
        // (1,1,1,1) on every fragment. That made the composite write
        // opaque white over the camera target, wiping anything painted
        // before the filter scope (the match3 demo lost its body radial
        // background entirely).
        //
        // Each draw also needs its OWN MPB / its OWN Mesh — cmd.DrawMesh
        // captures both references at record time and reads them back at
        // execute time, so reusing one instance lets each later call
        // overwrite the data the earlier calls were supposed to draw with.
        // Same pattern UIRenderGraphPass uses for the chunk-mesh pool.
        readonly System.Collections.Generic.Stack<MaterialPropertyBlock> filterMpbPool =
            new System.Collections.Generic.Stack<MaterialPropertyBlock>();
        readonly System.Collections.Generic.List<MaterialPropertyBlock> filterMpbInUse =
            new System.Collections.Generic.List<MaterialPropertyBlock>(8);
        readonly System.Collections.Generic.Stack<Mesh> filterQuadPool =
            new System.Collections.Generic.Stack<Mesh>();
        readonly System.Collections.Generic.List<Mesh> filterQuadInUse =
            new System.Collections.Generic.List<Mesh>(8);

        MaterialPropertyBlock RentMpb() {
            var mpb = filterMpbPool.Count > 0 ? filterMpbPool.Pop() : new MaterialPropertyBlock();
            mpb.Clear();
            filterMpbInUse.Add(mpb);
            return mpb;
        }

        Mesh RentFilterQuad() {
            Mesh m;
            if (filterQuadPool.Count > 0) {
                m = filterQuadPool.Pop();
            } else {
                m = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            }
            filterQuadInUse.Add(m);
            return m;
        }

        public void ResetFrame() {
            // R2: advance the frame clock and release scope-cache RTs whose
            // scopeKey hasn't been queried/populated in a while. Eviction runs
            // BEFORE this frame's DrainBatches scope queries (the pass calls
            // ResetFrame at frame start), so an entry used this frame is
            // re-stamped after eviction and survives; an over-aggressive evict
            // only forces a re-render, never samples a freed RT.
            frameCounter++;
            EvictStaleScopeCaches();
            for (int i = 0; i < filterMpbInUse.Count; i++) filterMpbPool.Push(filterMpbInUse[i]);
            filterMpbInUse.Clear();
            for (int i = 0; i < filterQuadInUse.Count; i++) filterQuadPool.Push(filterQuadInUse[i]);
            filterQuadInUse.Clear();
        }

        void EvictStaleScopeCaches() {
            scopeEvictScratch.Clear();
            foreach (var kv in scopeCaches) {
                if (frameCounter - kv.Value.LastTouchedFrame > ScopeEvictAfterFrames) {
                    scopeEvictScratch.Add(kv.Key);
                }
            }
            for (int i = 0; i < scopeEvictScratch.Count; i++) {
                if (scopeCaches.TryGetValue(scopeEvictScratch[i], out var c)) {
                    ReleaseScopeRt(c.Rt);
                    scopeCaches.Remove(scopeEvictScratch[i]);
                }
            }
            scopeEvictScratch.Clear();
            // RD1: same sweep for the shared-backdrop entries — the exact R2
            // leak class one dictionary down. The key is a VALUE hash of the
            // filter chain, so an animated `backdrop-filter: blur(4px→24px)`
            // mints a new whole-screen RT per transition frame; without aging
            // they accumulated until Dispose (monotonic VRAM growth per
            // hover). Same safety argument as scope caches: eviction runs at
            // frame start, an entry used this frame is re-stamped after, and
            // an over-aggressive evict only forces a rebuild.
            foreach (var kv in sharedBackdrop) {
                if (frameCounter - kv.Value.LastTouchedFrame > ScopeEvictAfterFrames) {
                    scopeEvictScratch.Add(kv.Key);
                }
            }
            for (int i = 0; i < scopeEvictScratch.Count; i++) {
                if (sharedBackdrop.TryGetValue(scopeEvictScratch[i], out var e)) {
                    ReleaseScopeRt(e.Rt);
                    sharedBackdrop.Remove(scopeEvictScratch[i]);
                }
            }
            scopeEvictScratch.Clear();
        }

        static void ReleaseScopeRt(RenderTexture rt) {
            if (rt == null) return;
            rt.Release();
#if UNITY_EDITOR
            if (Application.isPlaying) UnityEngine.Object.Destroy(rt);
            else UnityEngine.Object.DestroyImmediate(rt);
#else
            UnityEngine.Object.Destroy(rt);
#endif
        }

        void DrawQuadAtPx(CommandBuffer cb, int srcRt, double x, double y, double w, double h, int pass) {
            DrawQuadAtPx(cb, srcRt, x, y, w, h, pass, Transform2D.Identity);
        }

        void DrawQuadAtPx(CommandBuffer cb, int srcRt, double x, double y, double w, double h, int pass, BorderRadii radii) {
            DrawQuadAtPx(cb, srcRt, x, y, w, h, pass, Transform2D.Identity, true, radii);
        }

        void DrawQuadAtPx(CommandBuffer cb, int srcRt, double x, double y, double w, double h, int pass,
                          BorderRadii radii, double u0, double v0, double u1, double v1,
                          bool sourceYFlip = false, bool encodeSrgb = false) {
            DrawQuadAtPx(cb, srcRt, x, y, w, h, pass, Transform2D.Identity, true, radii,
                u0, v0, u1, v1, sourceYFlip, encodeSrgb);
        }

        // Overload that applies a 2D affine transform to the four quad
        // corners using absolute CSS pixel coordinates. Used for the final
        // composite-back step so the owner element's CSS `transform` applies
        // after filter (per CSS Filter Effects L1).
        void DrawQuadAtPx(CommandBuffer cb, int srcRt, double x, double y, double w, double h, int pass, Transform2D scopeBoxTransform) {
            DrawQuadAtPx(cb, srcRt, x, y, w, h, pass, scopeBoxTransform, false, BorderRadii.Zero);
        }

        void DrawQuadAtPx(CommandBuffer cb, int srcRt, double x, double y, double w, double h, int pass,
                          Transform2D scopeBoxTransform, bool sourceYFlip, bool encodeSrgb) {
            DrawQuadAtPx(cb, srcRt, x, y, w, h, pass, scopeBoxTransform, false, BorderRadii.Zero,
                0.0, 0.0, 1.0, 1.0, sourceYFlip, encodeSrgb);
        }

        void DrawQuadAtPx(CommandBuffer cb, int srcRt, double x, double y, double w, double h, int pass,
                          Transform2D scopeBoxTransform, bool roundedClip, BorderRadii clipRadii) {
            DrawQuadAtPx(cb, srcRt, x, y, w, h, pass, scopeBoxTransform, roundedClip, clipRadii, 0.0, 0.0, 1.0, 1.0);
        }

        void DrawQuadAtPx(CommandBuffer cb, int srcRt, double x, double y, double w, double h, int pass,
                          Transform2D scopeBoxTransform, bool roundedClip, BorderRadii clipRadii,
                          double u0, double v0, double u1, double v1,
                          bool sourceYFlip = false, bool encodeSrgb = false) {
            // cb.SetGlobalTexture is the correct binding mechanism for a
            // temp RT identified by nameId — MPB.SetTexture only accepts
            // concrete Texture / RenderTexture references, and we don't
            // hold one for cb.GetTemporaryRT-allocated RTs (they're
            // resolved lazily at execute time). The material's
            // `Properties { _MainTex = "white" }` declaration used to
            // SHADOW the global binding (Unity routes shader
            // TEXTURE2D(_MainTex) reads through the material property
            // first); we dropped _MainTex from Properties so the global
            // binding is the canonical source.
            cb.SetGlobalTexture(IdMainTex, new RenderTargetIdentifier(srcRt));
            var mpb = RentMpb();
            mpb.SetVector(IdMainTexTexelSize,
                new Vector4(w > 0 ? 1f / (float)w : 0f, h > 0 ? 1f / (float)h : 0f, (float)w, (float)h));
            mpb.SetFloat(IdFilterSourceYFlip, sourceYFlip ? 1f : 0f);
            mpb.SetFloat(IdFilterEncodeSrgb, encodeSrgb ? 1f : 0f);
            SetCompositeClip(mpb, x, y, w, h, roundedClip, clipRadii);
            if (scopeBoxTransform.Equals(Transform2D.Identity)) {
                scratchPositions[0] = new Vector3((float)x, (float)y, 0f);
                scratchPositions[1] = new Vector3((float)x, (float)(y + h), 0f);
                scratchPositions[2] = new Vector3((float)(x + w), (float)(y + h), 0f);
                scratchPositions[3] = new Vector3((float)(x + w), (float)y, 0f);
            } else {
                scratchPositions[0] = TransformCompositePoint(scopeBoxTransform, x,     y);
                scratchPositions[1] = TransformCompositePoint(scopeBoxTransform, x,     y + h);
                scratchPositions[2] = TransformCompositePoint(scopeBoxTransform, x + w, y + h);
                scratchPositions[3] = TransformCompositePoint(scopeBoxTransform, x + w, y);
            }
            scratchUvs[0] = new Vector2((float)u0, (float)v0);
            scratchUvs[1] = new Vector2((float)u0, (float)v1);
            scratchUvs[2] = new Vector2((float)u1, (float)v1);
            scratchUvs[3] = new Vector2((float)u1, (float)v0);
            var quad = RentFilterQuad();
            quad.Clear();
            quad.SetVertices(scratchPositions);
            quad.SetUVs(0, scratchUvs);
            quad.SetIndices(scratchIndices, MeshTopology.Triangles, 0);
            quad.UploadMeshData(false);
            cb.DrawMesh(quad, Matrix4x4.identity, filterMaterial, 0, pass, mpb);
        }

        void DrawQuadAtPx(CommandBuffer cb, RenderTargetIdentifier src,
                          double x, double y, double w, double h, int pass,
                          double u0, double v0, double u1, double v1,
                          bool sourceYFlip = false, bool encodeSrgb = false) {
            cb.SetGlobalTexture(IdMainTex, src);
            var mpb = RentMpb();
            mpb.SetVector(IdMainTexTexelSize,
                new Vector4(w > 0 ? 1f / (float)w : 0f, h > 0 ? 1f / (float)h : 0f, (float)w, (float)h));
            mpb.SetFloat(IdFilterSourceYFlip, sourceYFlip ? 1f : 0f);
            mpb.SetFloat(IdFilterEncodeSrgb, encodeSrgb ? 1f : 0f);
            SetCompositeClip(mpb, x, y, w, h, false, BorderRadii.Zero);
            scratchPositions[0] = new Vector3((float)x, (float)y, 0f);
            scratchPositions[1] = new Vector3((float)x, (float)(y + h), 0f);
            scratchPositions[2] = new Vector3((float)(x + w), (float)(y + h), 0f);
            scratchPositions[3] = new Vector3((float)(x + w), (float)y, 0f);
            scratchUvs[0] = new Vector2((float)u0, (float)v0);
            scratchUvs[1] = new Vector2((float)u0, (float)v1);
            scratchUvs[2] = new Vector2((float)u1, (float)v1);
            scratchUvs[3] = new Vector2((float)u1, (float)v0);
            var quad = RentFilterQuad();
            quad.Clear();
            quad.SetVertices(scratchPositions);
            quad.SetUVs(0, scratchUvs);
            quad.SetIndices(scratchIndices, MeshTopology.Triangles, 0);
            quad.UploadMeshData(false);
            cb.DrawMesh(quad, Matrix4x4.identity, filterMaterial, 0, pass, mpb);
        }

        static void SetCompositeClip(MaterialPropertyBlock mpb, double x, double y, double w, double h,
                                     bool enabled, BorderRadii radii) {
            mpb.SetFloat(IdFilterClipEnabled, enabled ? 1f : 0f);
            if (!enabled) {
                mpb.SetVector(IdFilterClipRect, Vector4.zero);
                mpb.SetVector(IdFilterClipRadii, Vector4.zero);
                mpb.SetVector(IdFilterClipRadiiY, Vector4.zero);
                return;
            }
            mpb.SetVector(IdFilterClipRect, new Vector4((float)x, (float)y, (float)w, (float)h));
            // CSS Backgrounds & Borders L3 §5 — `border-radius: <x> / <y>` lets
            // authors set asymmetric corner radii (the corner is an ellipse
            // arc, not a circular arc). Pack BOTH axes so the shader can
            // evaluate the per-axis SDF. The Y vector is the dual of the X
            // vector — same corner order (TL, TR, BR, BL), one entry per
            // corner. When YRadius == XRadius (the common case) the shader's
            // per-axis SDF short-circuits to the circular path.
            mpb.SetVector(IdFilterClipRadii, new Vector4(
                (float)radii.TopLeft.XRadius,
                (float)radii.TopRight.XRadius,
                (float)radii.BottomRight.XRadius,
                (float)radii.BottomLeft.XRadius));
            mpb.SetVector(IdFilterClipRadiiY, new Vector4(
                (float)radii.TopLeft.YRadius,
                (float)radii.TopRight.YRadius,
                (float)radii.BottomRight.YRadius,
                (float)radii.BottomLeft.YRadius));
        }

        // The filter chain works in LINEAR values (encodeSrgb:false between
        // passes). 8-bit linear posterizes dark gradients badly — every blur
        // ping-pong re-quantizes, and the banding compounds (glass.html's dark
        // backdrop showed hard steps Chrome doesn't have). FP16 intermediates
        // keep the chain smooth; the single quantization happens at the final
        // composite into the camera target, same as a browser compositor.
        static RenderTextureFormat filterRtFormat = RenderTextureFormat.ARGB32;
        static bool filterRtFormatResolved;
        internal static RenderTextureFormat FilterRtFormat {
            get {
                if (!filterRtFormatResolved) {
                    filterRtFormat = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf)
                        ? RenderTextureFormat.ARGBHalf
                        : RenderTextureFormat.ARGB32;
                    filterRtFormatResolved = true;
                }
                return filterRtFormat;
            }
        }

        int AllocTempRt(CommandBuffer cb, int w, int h) {
            int nameId;
            if (tempRtFreeList.Count > 0) {
                nameId = tempRtFreeList[tempRtFreeList.Count - 1];
                tempRtFreeList.RemoveAt(tempRtFreeList.Count - 1);
            } else {
                nameId = Shader.PropertyToID("_WevaRgFilterTempRt_" + rtNameCounter);
                rtNameCounter++;
                tempRtNames.Add(nameId);
            }
            var desc = new RenderTextureDescriptor(Math.Max(1, w), Math.Max(1, h),
                FilterRtFormat, 0) {
                msaaSamples = 1,
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false,
                depthBufferBits = 0
            };
            cb.GetTemporaryRT(nameId, desc, FilterMode.Bilinear);
            return nameId;
        }

        void ReleaseTempRt(CommandBuffer cb, int nameId) {
            cb.ReleaseTemporaryRT(nameId);
            tempRtFreeList.Add(nameId);
        }

        public void Dispose() {
            if (fullscreenQuad != null) {
#if UNITY_EDITOR
                if (Application.isPlaying) UnityEngine.Object.Destroy(fullscreenQuad);
                else UnityEngine.Object.DestroyImmediate(fullscreenQuad);
#else
                UnityEngine.Object.Destroy(fullscreenQuad);
#endif
            }
            DestroyMeshList(filterQuadInUse);
            filterQuadInUse.Clear();
            while (filterQuadPool.Count > 0) {
                var m = filterQuadPool.Pop();
                if (m == null) continue;
#if UNITY_EDITOR
                if (Application.isPlaying) UnityEngine.Object.Destroy(m);
                else UnityEngine.Object.DestroyImmediate(m);
#else
                UnityEngine.Object.Destroy(m);
#endif
            }
            foreach (var kv in scopeCaches) {
                if (kv.Value.Rt != null) {
                    kv.Value.Rt.Release();
#if UNITY_EDITOR
                    if (Application.isPlaying) UnityEngine.Object.Destroy(kv.Value.Rt);
                    else UnityEngine.Object.DestroyImmediate(kv.Value.Rt);
#else
                    UnityEngine.Object.Destroy(kv.Value.Rt);
#endif
                }
            }
            scopeCaches.Clear();
            foreach (var kv in sharedBackdrop) {
                if (kv.Value.Rt != null) {
                    kv.Value.Rt.Release();
#if UNITY_EDITOR
                    if (Application.isPlaying) UnityEngine.Object.Destroy(kv.Value.Rt);
                    else UnityEngine.Object.DestroyImmediate(kv.Value.Rt);
#else
                    UnityEngine.Object.Destroy(kv.Value.Rt);
#endif
                }
            }
            sharedBackdrop.Clear();
            tempRtFreeList.Clear();
            tempRtNames.Clear();
        }

        static void DestroyMeshList(System.Collections.Generic.List<Mesh> list) {
            for (int i = 0; i < list.Count; i++) {
                var m = list[i];
                if (m == null) continue;
#if UNITY_EDITOR
                if (Application.isPlaying) UnityEngine.Object.Destroy(m);
                else UnityEngine.Object.DestroyImmediate(m);
#else
                UnityEngine.Object.Destroy(m);
#endif
            }
        }

        readonly Vector3[] scratchPositions = new Vector3[4];
        readonly Vector2[] scratchUvs = new Vector2[4];
        readonly int[] scratchIndices = new[] { 0, 1, 2, 0, 2, 3 };
    }
}
#endif
