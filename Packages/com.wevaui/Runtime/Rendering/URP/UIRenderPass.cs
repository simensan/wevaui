#if WEVA_URP
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#if UNITY_2023_3_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace Weva.Rendering.URP {
    // ScriptableRenderPass that drives our URPRenderBackend against the final camera color
    // target. Per PLAN §9 #12 there is no intermediate RT — we issue draws directly into
    // the active camera target.
    //
    // Two entry points are implemented:
    //   - Legacy `Execute(ScriptableRenderContext, ref RenderingData)` for URP compatibility
    //     mode (RenderGraph disabled). Marked obsolete to mirror Unity's own deprecation
    //     of the API; we keep it so projects that haven't enabled RenderGraph still work.
    //   - `RecordRenderGraph(RenderGraph, ContextContainer)` for URP 17+/Unity 6 RenderGraph
    //     mode. Uses AddRasterRenderPass against UniversalResourceData.activeColorTexture.
    //
    // Both paths run the same backend code (URPRenderBackend.BeginFrame/EndFrame) so behavior
    // is identical aside from the command-buffer adapter type.
    //
    // Multiple UIDocuments per camera: we enumerate UIPaintSourceRegistry by Order; each
    // source gets EmitPaint exactly once per camera per frame.
    public sealed class UIRenderPass : ScriptableRenderPass {
        public const string PassName = "Weva.RenderPass";
        public const RenderPassEvent OverlayRenderPassEvent = RenderPassEvent.AfterRendering;

        readonly URPRenderBackend backend;
        readonly List<IUIPaintSource> scratch = new List<IUIPaintSource>();

        public UIRenderPass(URPRenderBackend backend) {
            this.backend = backend;
            renderPassEvent = OverlayRenderPassEvent;
        }

        // Legacy SetColorTarget no-op preserved for the renderer feature's call site;
        // RenderGraph mode picks up the active color target from UniversalResourceData
        // automatically and ignores any explicit handle we used to thread through.
        public void SetColorTarget(RTHandle target) { }

#if UNITY_2023_3_OR_NEWER
        // Pass data carried into the raster render func. Currently empty — the backend and
        // scratch list are captured by reference. PassData is required to be a class for
        // the RG type-parameter contract.
        class PassData {
            public URPRenderBackend Backend;
            public UIRenderPass Owner;
            public int Width;
            public int Height;
            public bool HasStencil;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {
            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();
            if (resourceData == null || cameraData == null) return;
            if (!cameraData.resolveFinalTarget) return;

            var color = resourceData.activeColorTexture;
            var depth = resourceData.activeDepthTexture;
            if (!color.IsValid()) return;

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(PassName, out var passData)) {
                // Transparent UI blends over the scene color, so preserve/load
                // the attachment contents instead of treating this as first writer.
                builder.SetRenderAttachment(color, 0, AccessFlags.ReadWrite);
                if (depth.IsValid()) {
                    builder.SetRenderAttachmentDepth(depth, AccessFlags.ReadWrite);
                }
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);

                passData.Backend = backend;
                passData.Owner = this;
                passData.Width = cameraData.cameraTargetDescriptor.width;
                passData.Height = cameraData.cameraTargetDescriptor.height;
                passData.HasStencil = HasStencil(cameraData.cameraTargetDescriptor);

                builder.SetRenderFunc((PassData data, RasterGraphContext ctx) => {
                    data.Backend.BeginFrame(ctx.cmd, data.Width, data.Height, data.HasStencil);
                    data.Owner.EmitPaintSources(data.Width, data.Height);
                    data.Backend.EndFrame();
                });
            }
        }
#endif

        internal void EmitPaintSources(int viewportWidth = 0, int viewportHeight = 0) {
            scratch.Clear();
            var snap = UIPaintSourceRegistry.Snapshot();
            for (int i = 0; i < snap.Count; i++) scratch.Add(snap[i]);
            if (viewportWidth > 0 && viewportHeight > 0) {
                for (int i = 0; i < scratch.Count; i++) {
                    if (scratch[i] is IRenderViewportAwarePaintSource viewportAware) {
                        viewportAware.PrepareForRenderViewport(viewportWidth, viewportHeight);
                    }
                }
            }
            for (int i = 0; i < scratch.Count; i++) {
                scratch[i].EmitPaint(backend);
            }
        }

        static bool HasStencil(RenderTextureDescriptor desc) {
            // Stencil bits live in the depth attachment when format is D24_UNorm_S8_UInt
            // or D32_SFloat_S8_UInt. URP's cameraTargetDescriptor.depthBufferBits doesn't
            // distinguish stencil; we conservatively report true for >= 24-bit depth, which
            // is the URP default. The feature surfaces a runtime warning if shaders write
            // stencil and the format actually lacks it.
            return desc.depthBufferBits >= 24;
        }
    }
}
#endif
