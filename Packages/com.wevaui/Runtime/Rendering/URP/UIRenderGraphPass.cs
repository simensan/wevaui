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
        // A backdrop-filter's copy of the target, owned by the pass and
        // imported into the graph each frame: a pooled graph texture of the
        // camera colour's own shape came back as the camera colour on the
        // next frame, carrying the copied frame with it.
        RTHandle backdropCopy;

        /// <summary>Set by the feature each frame: whether any document draws a
        /// backdrop-filter. Without one the copy is neither allocated nor
        /// imported, so a page with no backdrop does not keep a camera-sized
        /// texture and a read-write dependency on it.</summary>
        public bool NeedsBackdropCopy { get; set; } = true;

        /// <summary>Diagnostics: what the last recorded pass drew into, and when.</summary>
        public static bool LastTargetWasBackBuffer { get; private set; }
        public static int LastRecordedFrame { get; private set; } = -1;

        public UIRenderGraphPass() {
            renderPassEvent = OverlayRenderPassEvent;
        }

        public void Dispose() {
            backdropCopy?.Release();
            backdropCopy = null;
        }

        class PassData {
            public UIRenderGraphPass Owner;
            public int Width;
            public int Height;
            public TextureHandle ColorTarget;
            public TextureHandle DepthTarget;
            // A backdrop-filter's copy of the target: declared here so the
            // graph owns it, filled by the renderer just before each such draw.
            public TextureHandle BackdropCopy;
            public RenderTextureDescriptor Descriptor;
        }

        // A static delegate: SetRenderFunc would otherwise allocate a closure
        // every frame.
        static readonly BaseRenderFunc<PassData, UnsafeGraphContext> s_RenderFunc = ExecuteRenderFunc;

        static void ExecuteRenderFunc(PassData data, UnsafeGraphContext ctx) {
            var cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
            RTHandle color = data.ColorTarget;
            RTHandle depth = data.DepthTarget.IsValid() ? (RTHandle)data.DepthTarget : null;
            if (depth != null) {
                cmd.SetRenderTarget(color, depth);
            } else {
                cmd.SetRenderTarget(color);
            }
            RTHandle copy = data.BackdropCopy.IsValid() ? (RTHandle)data.BackdropCopy : null;
            var target = new Weva.Native.NativeRenderTarget(color, depth, copy, data.Descriptor);
            data.Owner.Draw(cmd, data.Width, data.Height, target);
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
            // Whether this frame's draw lands on the back buffer (or the
            // camera's own render texture) rather than URP's intermediate. A
            // backdrop-filter draw must never see the former: the feature
            // asks for the intermediate when one is due, and the PlayMode
            // test reads this back.
            LastTargetWasBackBuffer = resourceData.isActiveTargetBackBuffer;
            LastRecordedFrame = Time.frameCount;

            // The copy a backdrop-filter draw reads: the target's shape, single-
            // sampled, no depth. Kept by the pass (re-made when the target
            // changes shape) and imported, never pooled.
            TextureHandle backdropCopyHandle = TextureHandle.nullHandle;
            if (NeedsBackdropCopy) {
                RenderTextureDescriptor copyDesc = cameraData.cameraTargetDescriptor;
                copyDesc.depthBufferBits = 0;
                copyDesc.msaaSamples = 1;
                copyDesc.bindMS = false;
                copyDesc.useMipMap = false;
                RenderingUtils.ReAllocateHandleIfNeeded(ref backdropCopy, copyDesc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "Weva.BackdropCopy");
                backdropCopyHandle = renderGraph.ImportTexture(backdropCopy);
            } else if (backdropCopy != null) {
                backdropCopy.Release();
                backdropCopy = null;
            }

            using (var builder = renderGraph.AddUnsafePass<PassData>(PassName, out var passData)) {
                builder.UseTexture(color, AccessFlags.ReadWrite);
                if (depth.IsValid()) builder.UseTexture(depth, AccessFlags.ReadWrite);
                if (backdropCopyHandle.IsValid()) builder.UseTexture(backdropCopyHandle, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                passData.Owner = this;
                // The camera's own pixel size, not the render-scaled target's:
                // input arrives in screen pixels, and the draw maps document
                // pixels through _WevaViewport, so a scaled target only
                // changes resolution. Sizing the layout from the scaled
                // target put hit testing off by the render scale.
                passData.Width = cameraData.camera.pixelWidth;
                passData.Height = cameraData.camera.pixelHeight;
                passData.ColorTarget = color;
                passData.DepthTarget = depth;
                passData.BackdropCopy = backdropCopyHandle;
                passData.Descriptor = cameraData.cameraTargetDescriptor;
                builder.SetRenderFunc(s_RenderFunc);
            }
        }

        // Every registered source, in SortingOrder: told the viewport it is
        // drawn into (a resize relayouts before the draw), then asked for
        // its meshes.
        internal void Draw(CommandBuffer cmd, int width, int height, in Weva.Native.NativeRenderTarget target) {
            UIPaintSourceRegistry.SnapshotInto(scratchSources);
            for (int i = 0; i < scratchSources.Count; i++) {
                if (scratchSources[i] is IRenderViewportAwarePaintSource aware) aware.PrepareForRenderViewport(width, height);
            }
            for (int i = 0; i < scratchSources.Count; i++) {
                if (scratchSources[i] is Weva.Native.IUINativePaintSource native) native.EmitNative(cmd, width, height, target);
            }
            scratchSources.Clear();
        }
    }
}
#endif
