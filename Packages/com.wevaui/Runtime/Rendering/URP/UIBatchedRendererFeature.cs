#if WEVA_URP
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Weva.Rendering.URP {
    // The ScriptableRendererFeature a project adds to its URP renderer asset
    // to draw Weva documents: one UIRenderGraphPass per ScriptableRenderer
    // (split-screen and picture-in-picture cameras must not share a pass's
    // scratch state), enqueued for game cameras that resolve the final
    // target. The name is the one 0.1.x projects already have on their
    // renderer assets.
    public sealed class UIBatchedRendererFeature : ScriptableRendererFeature {
        readonly Dictionary<ScriptableRenderer, UIRenderGraphPass> passesByRenderer = new();

        // How many features are alive: UrpFeatureStatus reads it by
        // reflection to tell an author the feature is missing.
        public static int ActiveCount { get; private set; }
        bool counted;

        public override void Create() {
            passesByRenderer.Clear();
            if (!counted) { ActiveCount++; counted = true; }
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
            if (!ShouldRenderForCamera(renderingData.cameraData)) return;
            if (!passesByRenderer.TryGetValue(renderer, out var pass) || pass == null) {
                pass = new UIRenderGraphPass();
                passesByRenderer[renderer] = pass;
            }
            renderer.EnqueuePass(pass);
        }

        public static bool ShouldRenderForCamera(CameraData cameraData) {
            // Screen-space UI is drawn once, on the final camera of a URP
            // camera stack, and only for game cameras: the editor's scene,
            // preview and reflection cameras resolve a final target too.
            return cameraData.resolveFinalTarget
                && cameraData.cameraType == CameraType.Game;
        }

        protected override void Dispose(bool disposing) {
            if (disposing) {
                passesByRenderer.Clear();
                if (counted) { ActiveCount--; counted = false; }
            }
        }
    }
}
#endif
