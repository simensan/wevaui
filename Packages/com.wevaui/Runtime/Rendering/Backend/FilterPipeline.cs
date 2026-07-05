#if WEVA_URP
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Weva.Paint;
using Weva.Paint.Filters;
using PaintRect = Weva.Paint.Rect;

namespace Weva.Rendering {
    // GPU filter pipeline for the legacy URPRenderBackend. Walks a FilterChain and emits
    // CommandBuffer draws that read from a source RT, run the requested passes (Gaussian
    // blur / color matrix / drop-shadow tint), and composite the result back into the
    // destination target.
    //
    // The chain is materialized lazily — callers Push() at the start of a filter scope,
    // emit their normal draws into the redirected target, then Pop() to apply the chain
    // and splat the output. Multiple filter scopes nest by stacking redirection targets.
    //
    // This class is ONLY safe to use with a legacy CommandBuffer. RasterCommandBuffer
    // (RenderGraph mode) doesn't expose SetRenderTarget — the render-graph variant of
    // filter handling needs to be wired via builder.UseTexture() and per-pass attachment
    // declarations, which is a separate integration. When the backend is running under
    // RG, FilterPipeline.IsSupported returns false and the backend treats Push/Pop as
    // no-ops.
    public sealed class FilterPipeline : IDisposable {
        public static readonly int IdMainTex = Shader.PropertyToID("_MainTex");
        public static readonly int IdMainTexTexelSize = Shader.PropertyToID("_MainTex_TexelSize");
        public static readonly int IdFilterParams = Shader.PropertyToID("_WevaFilterParams");
        public static readonly int IdFilterMatrixRow0 = Shader.PropertyToID("_WevaFilterMatrixRow0");
        public static readonly int IdFilterMatrixRow1 = Shader.PropertyToID("_WevaFilterMatrixRow1");
        public static readonly int IdFilterMatrixRow2 = Shader.PropertyToID("_WevaFilterMatrixRow2");
        public static readonly int IdFilterMatrixRow3 = Shader.PropertyToID("_WevaFilterMatrixRow3");
        public static readonly int IdFilterMatrixBias = Shader.PropertyToID("_WevaFilterMatrixBias");
        public static readonly int IdFilterDropShadowTint = Shader.PropertyToID("_WevaFilterDropShadowTint");

        // Cap σ per pass so the shader's [loop] unroll stays bounded. Anything larger gets
        // split into multiple passes — n cascaded Gaussians of σ' = σ/√n equal one
        // Gaussian of σ (within 1%) and let large-radius blur stay fast.
        public const int MaxSigmaPerPass = 16;
        public const int MaxTapsPerPass = 31;

        public bool IsSupported => commandBuffer != null;

        readonly ShaderResources resources;
        readonly Mesh fullscreenQuad;
        readonly Material filterMaterial;
        readonly Stack<FilterFrame> frames = new Stack<FilterFrame>();
        readonly List<int> tempRtFreeList = new List<int>(8);
        readonly List<int> tempRtNames = new List<int>(8);

        CommandBuffer commandBuffer;
        RenderTargetIdentifier defaultColorTarget;
        bool hasDefaultColorTarget;
        int viewportWidth;
        int viewportHeight;
        int rtNameCounter;

        public FilterPipeline(ShaderResources resources) {
            this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
            fullscreenQuad = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            fullscreenQuad.MarkDynamic();
            filterMaterial = resources.GetFilter(BlendKind.PremultipliedAlpha);
        }

        public void BeginFrame(CommandBuffer cb, RenderTargetIdentifier defaultTarget, int width, int height) {
            commandBuffer = cb;
            defaultColorTarget = defaultTarget;
            hasDefaultColorTarget = true;
            viewportWidth = width;
            viewportHeight = height;
            frames.Clear();
        }

        // Variant for backends that don't have an explicit color target handle. When
        // popping a filter, restoring to BuiltinRenderTextureType.CameraTarget is the
        // safe fallback.
        public void BeginFrame(CommandBuffer cb, int width, int height) {
            commandBuffer = cb;
            defaultColorTarget = default;
            hasDefaultColorTarget = false;
            viewportWidth = width;
            viewportHeight = height;
            frames.Clear();
        }

        public void EndFrame() {
            // Discard any unpopped frames so we don't leak RTs across frames.
            while (frames.Count > 0) {
                var leaked = frames.Pop();
                ReleaseTempRt(leaked.RtNameId);
            }
            commandBuffer = null;
            hasDefaultColorTarget = false;
        }

        // Returns true if the filter scope was redirected to an offscreen RT. The caller
        // must keep emitting draws (they'll land in that RT) until the matching Pop().
        // Returns false if the chain is empty or this pipeline can't run (no legacy CB).
        public bool Push(PaintRect bounds, FilterChain filters, Transform2D currentTransform) {
            if (commandBuffer == null || filters == null || filters.IsEmpty) return false;
            if (filterMaterial == null) return false;

            // Bounds are in box-local space (pre-transform). Apply current transform to
            // get pixel-space AABB; pad by ≈ 3σ so Gaussian halos live inside the texture.
            var aabb = ComputeAabb(bounds, currentTransform);
            int padPx = ComputePadding(filters);
            int x0 = (int)Math.Floor(aabb.X) - padPx;
            int y0 = (int)Math.Floor(aabb.Y) - padPx;
            int x1 = (int)Math.Ceiling(aabb.X + aabb.Width) + padPx;
            int y1 = (int)Math.Ceiling(aabb.Y + aabb.Height) + padPx;
            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;
            if (x1 > viewportWidth) x1 = viewportWidth;
            if (y1 > viewportHeight) y1 = viewportHeight;
            int w = x1 - x0;
            int h = y1 - y0;
            if (w <= 0 || h <= 0) return false;

            int rtId = AllocTempRt(w, h);
            commandBuffer.SetRenderTarget(new RenderTargetIdentifier(rtId));
            commandBuffer.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));

            // Vertex shader expects target-space px → NDC via _WevaViewport. Update so
            // subsequent draws into this RT land at the correct sub-rect. Caller is
            // responsible for translating their content into RT-local coords; URPRenderBackend
            // does this via the FilterFrame's offset (see Submit(PushFilterCommand)).
            commandBuffer.SetGlobalVector(URPRenderBackend.IdViewport,
                new Vector4(w, h, 1f / w, 1f / h));

            frames.Push(new FilterFrame {
                RtNameId = rtId,
                Width = w,
                Height = h,
                OffsetX = x0,
                OffsetY = y0,
                Filters = filters
            });
            return true;
        }

        public bool TryPeekFrame(out int offsetX, out int offsetY) {
            if (frames.Count == 0) { offsetX = 0; offsetY = 0; return false; }
            var f = frames.Peek();
            offsetX = f.OffsetX;
            offsetY = f.OffsetY;
            return true;
        }

        public void Pop() {
            if (commandBuffer == null || frames.Count == 0) return;
            var frame = frames.Pop();

            // Run the chain. Each step takes a `current` RT (output of the previous step)
            // and produces a new RT. We never free `current` if it's the frame's original
            // RT — Pop frees that at the end. Intermediate RTs are freed as soon as they're
            // consumed.
            int current = frame.RtNameId;
            int currentW = frame.Width;
            int currentH = frame.Height;
            bool hasPendingColorMatrix = false;
            ColorMatrix pendingColorMatrix = ColorMatrix.Identity;

            for (int fi = 0; fi < frame.Filters.Functions.Count; fi++) {
                var fn = frame.Filters.Functions[fi];
                int next = current;
                if (TryGetColorMatrix(fn, out var colorMatrix)) {
                    pendingColorMatrix = hasPendingColorMatrix
                        ? ColorMatrices.Compose(pendingColorMatrix, colorMatrix)
                        : colorMatrix;
                    hasPendingColorMatrix = true;
                    continue;
                }

                if (hasPendingColorMatrix) {
                    next = ApplyColorMatrix(current, currentW, currentH, pendingColorMatrix);
                    if (next != current && current != frame.RtNameId) {
                        ReleaseTempRt(current);
                    }
                    current = next;
                    hasPendingColorMatrix = false;
                    pendingColorMatrix = ColorMatrix.Identity;
                    next = current;
                }

                switch (fn) {
                    case BlurFilter bf:
                        next = ApplyBlur(current, currentW, currentH, bf.RadiusPx);
                        break;
                    case DropShadowFilter ds:
                        next = ApplyDropShadow(current, currentW, currentH, ds);
                        break;
                }
                if (next != current && current != frame.RtNameId) {
                    ReleaseTempRt(current);
                }
                current = next;
            }
            if (hasPendingColorMatrix) {
                int next = ApplyColorMatrix(current, currentW, currentH, pendingColorMatrix);
                if (next != current && current != frame.RtNameId) {
                    ReleaseTempRt(current);
                }
                current = next;
            }

            // Composite into the parent target. Restore globals.
            RestoreParentTarget();
            commandBuffer.SetGlobalVector(URPRenderBackend.IdViewport,
                new Vector4(viewportWidth, viewportHeight,
                    viewportWidth > 0 ? 1f / viewportWidth : 0f,
                    viewportHeight > 0 ? 1f / viewportHeight : 0f));
            DrawCompositeToTarget(current, frame.OffsetX, frame.OffsetY, frame.Width, frame.Height);

            ReleaseTempRt(frame.RtNameId);
            if (current != frame.RtNameId) ReleaseTempRt(current);
        }

        int ApplyBlur(int srcRt, int w, int h, double radiusPx) {
            if (radiusPx <= 0) return srcRt;
            var plan = PlanGaussianBlur(radiusPx);
            int passes = plan.Passes;
            double effectiveSigma = plan.EffectiveSigma;
            int N = plan.TapsPerSide;

            int current = srcRt;
            for (int pass = 0; pass < passes; pass++) {
                int hRt = AllocTempRt(w, h);
                commandBuffer.SetRenderTarget(new RenderTargetIdentifier(hRt));
                commandBuffer.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));
                commandBuffer.SetGlobalVector(IdFilterParams,
                    new Vector4((float)effectiveSigma, 1f / w, 1f / h, N));
                DrawIntermediate(current, hRt, w, h, ShaderResources.FilterBlurHorizontalPass);
                if (current != srcRt) ReleaseTempRt(current);
                current = hRt;

                int vRt = AllocTempRt(w, h);
                commandBuffer.SetRenderTarget(new RenderTargetIdentifier(vRt));
                commandBuffer.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));
                DrawIntermediate(current, vRt, w, h, ShaderResources.FilterBlurVerticalPass);
                ReleaseTempRt(current);
                current = vRt;
            }
            return current;
        }

        int ApplyColorMatrix(int srcRt, int w, int h, ColorMatrix m) {
            int dst = AllocTempRt(w, h);
            commandBuffer.SetRenderTarget(new RenderTargetIdentifier(dst));
            commandBuffer.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));
            commandBuffer.SetGlobalVector(IdFilterMatrixRow0, m.Row0);
            commandBuffer.SetGlobalVector(IdFilterMatrixRow1, m.Row1);
            commandBuffer.SetGlobalVector(IdFilterMatrixRow2, m.Row2);
            commandBuffer.SetGlobalVector(IdFilterMatrixRow3, m.Row3);
            commandBuffer.SetGlobalVector(IdFilterMatrixBias, m.Bias);
            DrawIntermediate(srcRt, dst, w, h, ShaderResources.FilterColorMatrixPass);
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

        int ApplyDropShadow(int srcRt, int w, int h, DropShadowFilter ds) {
            // Tint the source's alpha into the shadow color.
            int tinted = AllocTempRt(w, h);
            commandBuffer.SetRenderTarget(new RenderTargetIdentifier(tinted));
            commandBuffer.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));
            var c = ds.Color;
            commandBuffer.SetGlobalVector(IdFilterDropShadowTint, new Vector4(c.R, c.G, c.B, c.A));
            DrawIntermediate(srcRt, tinted, w, h, ShaderResources.FilterDropShadowTintPass);

            // Blur the tinted silhouette.
            int blurred = ApplyBlur(tinted, w, h, ds.BlurRadius);
            if (blurred != tinted) ReleaseTempRt(tinted);

            // Composite source on top of shadow, with shadow offset.
            int composited = AllocTempRt(w, h);
            commandBuffer.SetRenderTarget(new RenderTargetIdentifier(composited));
            commandBuffer.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));
            commandBuffer.SetGlobalVector(URPRenderBackend.IdViewport,
                new Vector4(w, h, 1f / w, 1f / h));
            // Shadow at offset.
            DrawQuadAtPx(blurred, ds.OffsetX, ds.OffsetY, w, h, ShaderResources.FilterCompositePass);
            // Source at origin, on top.
            DrawQuadAtPx(srcRt, 0, 0, w, h, ShaderResources.FilterCompositePass);

            if (blurred != srcRt) ReleaseTempRt(blurred);
            return composited;
        }

        // Renders srcRt → dstRt (full-coverage quad sized w×h).
        void DrawIntermediate(int srcRt, int dstRt, int w, int h, int pass) {
            commandBuffer.SetGlobalVector(URPRenderBackend.IdViewport,
                new Vector4(w, h, 1f / w, 1f / h));
            DrawQuadAtPx(srcRt, 0, 0, w, h, pass);
        }

        // Composites srcRt into the currently bound render target at (offsetX, offsetY)
        // covering (w × h) pixels with premultiplied-alpha blend (handled by the material).
        void DrawCompositeToTarget(int srcRt, int offsetX, int offsetY, int w, int h) {
            DrawQuadAtPx(srcRt, offsetX, offsetY, w, h, ShaderResources.FilterCompositePass);
        }

        // Builds and draws a single pixel-space quad. The shader's vert maps position px
        // by _WevaViewport.zw to NDC, so the active viewport must be set by the caller.
        void DrawQuadAtPx(int srcRt, double x, double y, double w, double h, int pass) {
            // MaterialPropertyBlock.SetTexture doesn't accept RenderTargetIdentifier; route
            // the source RT through a global so the shader's _MainTex binding resolves
            // against the temp RT we allocated. The texel-size global stays per-draw.
            commandBuffer.SetGlobalTexture(IdMainTex, new RenderTargetIdentifier(srcRt));
            commandBuffer.SetGlobalVector(IdMainTexTexelSize,
                new Vector4(1f / (float)w, 1f / (float)h, (float)w, (float)h));
            scratchPositions[0] = new Vector3((float)x, (float)y, 0f);
            scratchPositions[1] = new Vector3((float)x, (float)(y + h), 0f);
            scratchPositions[2] = new Vector3((float)(x + w), (float)(y + h), 0f);
            scratchPositions[3] = new Vector3((float)(x + w), (float)y, 0f);
            scratchUvs[0] = new Vector2(0, 0);
            scratchUvs[1] = new Vector2(0, 1);
            scratchUvs[2] = new Vector2(1, 1);
            scratchUvs[3] = new Vector2(1, 0);
            fullscreenQuad.Clear();
            fullscreenQuad.SetVertices(scratchPositions);
            fullscreenQuad.SetUVs(0, scratchUvs);
            fullscreenQuad.SetIndices(scratchIndices, MeshTopology.Triangles, 0);
            fullscreenQuad.UploadMeshData(false);
            commandBuffer.DrawMesh(fullscreenQuad, Matrix4x4.identity, filterMaterial, 0, pass);
        }

        int AllocTempRt(int w, int h) {
            int nameId;
            if (tempRtFreeList.Count > 0) {
                nameId = tempRtFreeList[tempRtFreeList.Count - 1];
                tempRtFreeList.RemoveAt(tempRtFreeList.Count - 1);
            } else {
                nameId = Shader.PropertyToID("_WevaFilterTempRt_" + rtNameCounter);
                rtNameCounter++;
                tempRtNames.Add(nameId);
            }
            var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGB32, 0) {
                msaaSamples = 1,
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false,
                depthBufferBits = 0
            };
            commandBuffer.GetTemporaryRT(nameId, desc, FilterMode.Bilinear);
            return nameId;
        }

        void ReleaseTempRt(int nameId) {
            if (commandBuffer == null) return;
            commandBuffer.ReleaseTemporaryRT(nameId);
            tempRtFreeList.Add(nameId);
        }

        void RestoreParentTarget() {
            if (commandBuffer == null) return;
            if (hasDefaultColorTarget) {
                commandBuffer.SetRenderTarget(defaultColorTarget);
            } else {
                commandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            }
        }

        static PaintRect ComputeAabb(PaintRect r, Transform2D t) {
            var (x0, y0) = t.Apply(r.X, r.Y);
            var (x1, y1) = t.Apply(r.Right, r.Y);
            var (x2, y2) = t.Apply(r.X, r.Bottom);
            var (x3, y3) = t.Apply(r.Right, r.Bottom);
            double minX = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3));
            double minY = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3));
            double maxX = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
            double maxY = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));
            return new PaintRect(minX, minY, maxX - minX, maxY - minY);
        }

        // CSS Filter Effects 1 §6.1: `blur(<length>)`'s argument IS the standard
        // deviation σ of the Gaussian — NOT a radius/diameter to be halved. The
        // shader's [loop] is bounded to MaxSigmaPerPass; larger σ cascades through
        // n passes of σ' = σ/√n (n independent Gaussians of σ' equal one Gaussian
        // of σ to within 1%). Returns the per-pass σ, the number of passes, and
        // the tap-count per side N (kernel half-width ≈ 3σ, clamped to the
        // shader's hard-coded max of 15).
        public readonly struct GaussianBlurPlan {
            public readonly double Sigma;
            public readonly double EffectiveSigma;
            public readonly int Passes;
            public readonly int TapsPerSide;
            public GaussianBlurPlan(double sigma, double effectiveSigma, int passes, int tapsPerSide) {
                Sigma = sigma;
                EffectiveSigma = effectiveSigma;
                Passes = passes;
                TapsPerSide = tapsPerSide;
            }
        }

        // Hard shader bound: the blur [loop] samples at most this many taps per
        // side. A per-pass σ above MaxTapsPerSide/3 cannot fit its 3σ kernel in
        // the budget — the Gaussian gets TRUNCATED at ~N/σ standard deviations,
        // which visibly tightens the blur (the soft skirt past the cut is what
        // reads as "Chrome's blur is softer"; glass.html blur(26) at σ'=13 was
        // cut at 1.15σ). The cascade threshold below therefore keys on the TAP
        // budget, not on MaxSigmaPerPass (the historical loop-bound constant,
        // kept for reference): σ over the budget splits into n passes of
        // σ/√n ≤ budget, each a full un-truncated kernel.
        public const int MaxTapsPerSide = 15;
        public const double MaxSigmaPerTapBudget = MaxTapsPerSide / 3.0; // 5.0

        public static GaussianBlurPlan PlanGaussianBlur(double radiusPx) {
            // Spec: the <length> argument IS σ directly. No halving.
            double sigma = radiusPx;
            int passes = 1;
            double effectiveSigma = sigma;
            while (effectiveSigma > MaxSigmaPerTapBudget) {
                passes++;
                effectiveSigma = sigma / Math.Sqrt(passes);
            }
            int N = (int)Math.Ceiling(effectiveSigma * 3.0);
            if (N < 1) N = 1;
            if (N > MaxTapsPerSide) N = MaxTapsPerSide;
            return new GaussianBlurPlan(sigma, effectiveSigma, passes, N);
        }

        static int ComputePadding(FilterChain filters) {
            int max = 0;
            for (int i = 0; i < filters.Functions.Count; i++) {
                switch (filters.Functions[i]) {
                    case BlurFilter bf:
                        // Per spec σ = radiusPx, so the Gaussian halo extends ~3σ
                        // beyond the source edge. Pad by 3 × radius to keep the
                        // halo inside the offscreen RT.
                        int p = (int)Math.Ceiling(bf.RadiusPx * 3.0);
                        if (p > max) max = p;
                        break;
                    case DropShadowFilter ds:
                        int dsPad = (int)Math.Ceiling(Math.Max(Math.Abs(ds.OffsetX), Math.Abs(ds.OffsetY))
                            + ds.BlurRadius * 3.0);
                        if (dsPad > max) max = dsPad;
                        break;
                }
            }
            return max;
        }

        readonly Vector3[] scratchPositions = new Vector3[4];
        readonly Vector2[] scratchUvs = new Vector2[4];
        readonly int[] scratchIndices = new[] { 0, 1, 2, 0, 2, 3 };

        public void Dispose() {
            if (fullscreenQuad != null) {
#if UNITY_EDITOR
                if (Application.isPlaying) UnityEngine.Object.Destroy(fullscreenQuad);
                else UnityEngine.Object.DestroyImmediate(fullscreenQuad);
#else
                UnityEngine.Object.Destroy(fullscreenQuad);
#endif
            }
            frames.Clear();
            tempRtFreeList.Clear();
            tempRtNames.Clear();
        }

        struct FilterFrame {
            public int RtNameId;
            public int Width;
            public int Height;
            public int OffsetX;
            public int OffsetY;
            public FilterChain Filters;
        }
    }
}
#endif
