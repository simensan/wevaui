using UnityEngine;

namespace Weva.Rendering {
    // Answers "is the URP pass actually wired up?" without a hard WEVA_URP
    // dependency, so the setup warning works in any build configuration.
    public static class UrpFeatureStatus {
        // True when the active render pipeline is URP. Identified by type
        // name to avoid a hard reference to the URP assembly.
        public static bool UrpActive {
            get {
                var rp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
                if (rp == null) return false;
                var t = rp.GetType();
                return t.Name == "UniversalRenderPipelineAsset" || t.FullName != null && t.FullName.Contains("Universal");
            }
        }

        // True when a UIBatchedRendererFeature has been created by a URP
        // renderer asset, or enqueued its pass within the last frames (the
        // creation count does not survive a domain reload the way the asset
        // does). Reflection keeps this file WEVA_URP-independent.
        public static bool BatchedFeatureRegistered {
            get {
                var t = System.Type.GetType("Weva.Rendering.URP.UIBatchedRendererFeature, Weva.Runtime");
                if (t == null) return false;
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
                if (t.GetProperty("ActiveCount", flags)?.GetValue(null) is int n && n > 0) return true;
                return t.GetProperty("LastEnqueuedFrame", flags)?.GetValue(null) is int frame && frame >= 0 && Time.frameCount - frame <= 2;
            }
        }

        // The misconfiguration the inspector warns about: URP renders the
        // project but no renderer asset carries the feature, so documents
        // have no pass to draw in.
        public static bool BatchedFeatureMissing => UrpActive && !BatchedFeatureRegistered;

        // Actionable by whoever reads the log: a person gets the menu path,
        // an agent or CI script the exact -executeMethod entry point.
        public static string MissingFeatureMessage(string documentName) =>
            "Weva: URP is the active render pipeline, but UIBatchedRendererFeature is not on any URP Renderer asset, " +
            $"so document '{documentName}' has no pass to draw in.\n" +
            "Fix (menu): Window > Weva > Setup > Add URP Renderer Feature.\n" +
            "Fix (inspector): select the WevaDocument and click the warning's fix button.\n" +
            "Fix (scriptable — AI agents / CI): call Weva.EditorTools.Setup.UrpFeatureSetup.ApplyNonInteractive() from editor code, or run:\n" +
            "  Unity -batchmode -quit -projectPath <project> -executeMethod Weva.EditorTools.Setup.UrpFeatureSetup.ApplyNonInteractive\n" +
            "All fixes also add the Weva shader to Always Included Shaders (required for player builds). " +
            "Details: Packages/com.wevaui/Documentation~/troubleshooting.md (\"Nothing renders\").";
    }
}
