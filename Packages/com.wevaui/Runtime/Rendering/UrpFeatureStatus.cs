using UnityEngine;

namespace Weva.Rendering {
    // Answers "is the batched URP path actually wired up?" without a hard
    // WEVA_URP dependency, so WevaDocument and the IMGUI fallback can gate
    // rendering decisions and setup warnings on it in any build
    // configuration (URP present or not, editor or player).
    public static class UrpFeatureStatus {
        // True when the active render pipeline is URP. Identified by type
        // name to avoid a hard reference to the URP assembly (kept
        // compatible with builds that don't link URP).
        public static bool UrpActive {
            get {
                var rp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
                if (rp == null) return false;
                var t = rp.GetType();
                return t.Name == "UniversalRenderPipelineAsset" || t.FullName != null && t.FullName.Contains("Universal");
            }
        }

        // True when UIBatchedRendererFeature.Create() has run and registered
        // its backend — i.e. the feature is present on a URP renderer asset
        // and the pipeline has initialized it. Reflection keeps this file
        // WEVA_URP-independent; the registry type only compiles under URP.
        public static bool BatchedFeatureRegistered {
            get {
                var t = System.Type.GetType("Weva.Rendering.URP.BatchedRendererBackendRegistry, Weva.Runtime");
                if (t == null) return false;
                var f = t.GetField("Active", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                return f?.GetValue(null) != null;
            }
        }

        // The misconfiguration WevaDocument warns about: URP is rendering
        // the project but no renderer asset carries the Weva feature, so
        // documents fall back to the IMGUI debug renderer (or draw nothing
        // under a forced-URP backend).
        public static bool BatchedFeatureMissing => UrpActive && !BatchedFeatureRegistered;

        // The canonical warning. Written to be actionable by whoever reads
        // the log — a person gets the menu path, an AI agent / CI script
        // gets the exact -executeMethod entry point — so the message alone
        // is enough to repair the project without opening the docs.
        public static string MissingFeatureMessage(string documentName) =>
            "Weva: URP is the active render pipeline, but UIBatchedRendererFeature is not on any URP Renderer asset. " +
            $"Document '{documentName}' falls back to the IMGUI debug renderer (flat colors, no gradients/filters/images) — " +
            "or draws nothing if Renderer Backend is forced to URP.\n" +
            "Fix (menu): Window > Weva > Setup > Add URP Renderer Feature.\n" +
            "Fix (inspector): select the WevaDocument and click the warning's fix button.\n" +
            "Fix (scriptable — AI agents / CI): call Weva.EditorTools.Setup.UrpFeatureSetup.ApplyNonInteractive() from editor code, or run:\n" +
            "  Unity -batchmode -quit -projectPath <project> -executeMethod Weva.EditorTools.Setup.UrpFeatureSetup.ApplyNonInteractive\n" +
            "All fixes also add the Weva shaders to Always Included Shaders (required for player builds). " +
            "Details: Packages/com.wevaui/Documentation~/troubleshooting.md (\"Nothing renders\").";
    }
}
