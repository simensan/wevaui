#if WEVA_URP
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Weva.Rendering.URP;

namespace Weva.Rendering {
    public sealed class UIRendererFeature : ScriptableRendererFeature {
        public LayerMask renderLayerMask = -1;
        public bool warnIfNoStencil = true;

        URPRenderBackend backend;
        UIRenderPass pass;

        public override void Create() {
            backend?.Dispose();
            backend = new URPRenderBackend();
            pass = new UIRenderPass(backend);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
            if (pass == null) return;
            if (!UIBatchedRendererFeature.ShouldRenderForCamera(renderingData.cameraData)) return;
            if (!backend.Resources.IsReady) {
                Debug.LogWarning("Weva: shader resources not loaded. Make sure Hidden/Weva/* shaders are included in the build.");
                return;
            }
            if (warnIfNoStencil && renderingData.cameraData.cameraTargetDescriptor.depthBufferBits < 24) {
                Debug.LogWarning("Weva: camera target depth buffer is < 24 bits; stencil-based clip stack will fall back to scissor (axis-aligned only). Configure the URP asset for a depth+stencil target to enable rounded-rect clips.");
            }
            // RenderGraph mode picks up the active color target from UniversalResourceData
            // automatically; no explicit SetColorTarget needed in URP 17+.
            renderer.EnqueuePass(pass);
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                backend?.Dispose();
                backend = null;
                pass = null;
            }
        }
    }
}
#endif
