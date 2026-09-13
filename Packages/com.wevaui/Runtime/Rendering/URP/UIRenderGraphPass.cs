#if WEVA_URP
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Weva.Rendering.URP {
    // The pass that draws every registered document's draw list into the
    // camera colour target, after everything else has rendered. The core
    // has already laid out, painted, clipped and composited: what arrives
    // here is textured triangle lists (NativeDocumentRenderer), issued in
    // SortingOrder with the camera's projection and linear-space blending.
    // One pass per ScriptableRenderer (UIBatchedRendererFeature) so
    // split-screen and picture-in-picture cameras do not share scratch state.
    public sealed class UIRenderGraphPass : ScriptableRenderPass {
        public const string PassName = "Weva.DocumentPass";
        public const RenderPassEvent OverlayRenderPassEvent = RenderPassEvent.AfterRendering;

        readonly List<IUIPaintSource> scratchSources = new List<IUIPaintSource>(8);

        public UIRenderGraphPass() {
            renderPassEvent = OverlayRenderPassEvent;
        }

        class PassData {
            public UIRenderGraphPass Owner;
            public int Width;
            public int Height;
            public TextureHandle ColorTarget;
            public TextureHandle DepthTarget;
        }

        // A static delegate: SetRenderFunc would otherwise allocate a closure
        // every frame.
        static readonly BaseRenderFunc<PassData, UnsafeGraphContext> s_RenderFunc = ExecuteRenderFunc;

        static void ExecuteRenderFunc(PassData data, UnsafeGraphContext ctx) {
            var cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
            if (data.DepthTarget.IsValid()) {
                cmd.SetRenderTarget(data.ColorTarget, data.DepthTarget);
            } else {
                cmd.SetRenderTarget(data.ColorTarget);
            }
            data.Owner.Draw(cmd, data.Width, data.Height);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {
            var resourceData = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();
            if (resourceData == null || cameraData == null) return;
            if (!cameraData.resolveFinalTarget) return;
            // Game cameras only: the scene view and preview cameras resolve a
            // final target too, and a document that followed their viewport
            // would relayout at a foreign size on every editor repaint.
            if (cameraData.cameraType != CameraType.Game) return;

            var color = resourceData.activeColorTexture;
            var depth = resourceData.activeDepthTexture;
            if (!color.IsValid()) return;

            using (var builder = renderGraph.AddUnsafePass<PassData>(PassName, out var passData)) {
                builder.UseTexture(color, AccessFlags.ReadWrite);
                if (depth.IsValid()) builder.UseTexture(depth, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                passData.Owner = this;
                passData.Width = cameraData.cameraTargetDescriptor.width;
                passData.Height = cameraData.cameraTargetDescriptor.height;
                passData.ColorTarget = color;
                passData.DepthTarget = depth;
                builder.SetRenderFunc(s_RenderFunc);
            }
        }

        // Every registered source, in SortingOrder: told the viewport it is
        // drawn into (a resize relayouts before the draw), then asked for
        // its meshes.
        internal void Draw(CommandBuffer cmd, int width, int height) {
            UIPaintSourceRegistry.SnapshotInto(scratchSources);
            for (int i = 0; i < scratchSources.Count; i++) {
                if (scratchSources[i] is IRenderViewportAwarePaintSource aware) aware.PrepareForRenderViewport(width, height);
            }
            for (int i = 0; i < scratchSources.Count; i++) {
                if (scratchSources[i] is Weva.Native.IUINativePaintSource native) native.EmitNative(cmd, width, height);
            }
            scratchSources.Clear();
        }
    }
}
#endif
