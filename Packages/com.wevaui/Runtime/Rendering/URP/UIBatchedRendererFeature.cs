#if WEVA_URP
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Weva.Rendering.URP {
    // ScriptableRendererFeature for the batched über-shader pipeline. Project authors
    // pick this in their URP renderer asset to opt into the new path; the legacy
    // UIRendererFeature still ships for v0.x users with the per-quad-flush backend.
    //
    // The feature owns one shared BatchedURPRenderBackend + UIBatchedResources and
    // one UIRenderGraphPass PER ScriptableRenderer. The pass carries mutable per-frame
    // state (instance buffer pool, stencil mesh pool, MaterialPropertyBlock pool,
    // batch draw cache, chunk states). Sharing a single pass across multiple cameras
    // — split-screen, editor scene+game view, picture-in-picture — stomped that
    // mutable state and corrupted whichever camera rendered second on the frame.
    public sealed class UIBatchedRendererFeature : ScriptableRendererFeature {
        public bool warnIfNoStencil = true;

        BatchedURPRenderBackend backend;
        UIBatchedResources resources;
        // One pass per renderer. Most projects have 1 renderer, so this is
        // almost always a 1-entry dictionary. The ScriptableRenderer's
        // lifetime is owned by the renderer asset that owns this feature,
        // so retaining keys here doesn't outlive the asset.
        readonly Dictionary<ScriptableRenderer, UIRenderGraphPass> passesByRenderer = new();

        public override void Create() {
            DisposePass();
            resources = new UIBatchedResources();
            backend = new BatchedURPRenderBackend();
            BatchedRendererBackendRegistry.Active = backend;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
            if (backend == null) return;
            if (!ShouldRenderForCamera(renderingData.cameraData)) return;
            if (resources == null || !resources.IsReady) {
                Debug.LogWarning("Weva: batched shader resources not loaded. Make sure Hidden/Weva/Quad and Hidden/Weva/StencilWrite are included in the build.");
                return;
            }
            if (warnIfNoStencil && renderingData.cameraData.cameraTargetDescriptor.depthBufferBits < 24) {
                Debug.LogWarning("Weva: camera target depth buffer is < 24 bits; batched stencil clip stack will fall back to scissor (axis-aligned only).");
            }
            if (!passesByRenderer.TryGetValue(renderer, out var pass) || pass == null) {
                pass = new UIRenderGraphPass(backend, resources);
                passesByRenderer[renderer] = pass;
            }
            renderer.EnqueuePass(pass);
        }

        public static bool ShouldRenderForCamera(CameraData cameraData) {
            // Screen-space UI must be emitted only on the final camera in a URP
            // camera stack. Drawing on an earlier camera lets later scene cameras
            // overwrite the UI.
            //
            // And only on GAME cameras: the editor's SceneView / Preview /
            // Reflection cameras also resolve a final target, so each visible
            // editor view re-ran the ENTIRE paint pipeline (EmitPaint →
            // Convert → batch build → buffer upload) once per camera per
            // frame — particles.html paid its full ~4 ms paint cost twice
            // with a Scene view open. Screen-space UI drawn at viewport
            // coords is meaningless overlaid on the scene view anyway.
            return cameraData.resolveFinalTarget
                && cameraData.cameraType == CameraType.Game;
        }

        protected override void Dispose(bool disposing) {
            if (disposing) DisposePass();
        }

        void DisposePass() {
            if (BatchedRendererBackendRegistry.Active == backend) {
                BatchedRendererBackendRegistry.Active = null;
            }
            foreach (var kv in passesByRenderer) kv.Value?.Dispose();
            passesByRenderer.Clear();
            resources?.Dispose();
            resources = null;
            backend = null;
        }
    }

    // Holds the active batched backend so WevaDocument can record paint commands into it
    // when running under the URP pipeline. Set by UIBatchedRendererFeature.Create().
    public static class BatchedRendererBackendRegistry {
        public static BatchedURPRenderBackend Active;
    }
}
#endif
