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

        // How many features are alive, and the last frame one enqueued its
        // pass: UrpFeatureStatus reads both by reflection to tell an author
        // the feature is missing. Two signals because Create() runs at
        // pipeline creation, which a domain reload can separate from the
        // static counter's reset, while a frame stamp is true whenever a
        // camera has just rendered.
        public static int ActiveCount { get; private set; }
        public static int LastEnqueuedFrame { get; private set; } = -1;
        bool counted;

        public override void Create() {
            foreach (var kv in passesByRenderer) kv.Value?.Dispose();
            passesByRenderer.Clear();
            if (!counted) { ActiveCount++; counted = true; }
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData) {
            if (!ShouldRenderForCamera(renderingData.cameraData)) return;
            LastEnqueuedFrame = Time.frameCount;
            if (!passesByRenderer.TryGetValue(renderer, out var pass) || pass == null) {
                pass = new UIRenderGraphPass();
                passesByRenderer[renderer] = pass;
            }
            // A backdrop-filter reads the target back, and the back buffer
            // cannot be sampled. At AfterRendering the target is always the
            // back buffer (URP's final blit has happened), so a frame with a
            // backdrop draw runs the pass after post-processing instead:
            // URP then keeps the frame in an intermediate texture for the
            // passes there (and makes one when nothing else would have), and
            // its final blit carries the UI to the screen. Without one, the
            // pass stays at the end and draws straight to the target.
            bool backdrop = AnyDocumentNeedsBackdropCopy();
            pass.requiresIntermediateTexture = backdrop;
            pass.NeedsBackdropCopy = backdrop;
            pass.renderPassEvent = backdrop ? RenderPassEvent.AfterRenderingPostProcessing : UIRenderGraphPass.OverlayRenderPassEvent;
            renderer.EnqueuePass(pass);
        }

        readonly List<IUIPaintSource> scratchSources = new List<IUIPaintSource>(8);

        bool AnyDocumentNeedsBackdropCopy() {
            UIPaintSourceRegistry.SnapshotInto(scratchSources);
            bool any = false;
            for (int i = 0; i < scratchSources.Count && !any; i++) {
                if (scratchSources[i] is Weva.Native.IUINativePaintSource native && native.NeedsBackdropCopy) any = true;
            }
            scratchSources.Clear();
            return any;
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
                foreach (var kv in passesByRenderer) kv.Value?.Dispose();
                passesByRenderer.Clear();
                if (counted) { ActiveCount--; counted = false; }
            }
        }
    }
}
#endif
