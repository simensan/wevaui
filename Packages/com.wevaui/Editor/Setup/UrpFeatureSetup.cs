#if WEVA_URP
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Weva.Rendering.URP;

namespace Weva.EditorTools.Setup {
    // One-shot helper that adds `UIBatchedRendererFeature` to whichever
    // URP renderer asset the project's currently-active URP pipeline
    // points at. Without that feature, WevaDocument falls back to the
    // IMGUI debug renderer (gradients render as flat colors, filters
    // skipped). This window walks the user through the setup; the menu
    // entry runs the same logic non-interactively for scripted projects.
    public static class UrpFeatureSetup {
        // All shipped Weva menu items live under Window/Weva so end users
        // find everything in one place (the Tools/Weva split confused people).
        const string MenuPath = "Window/Weva/Setup/Add URP Renderer Feature";

        [MenuItem(MenuPath)]
        public static void RunFromMenu() {
            int added = AddFeatureToActiveRenderer(out string detail);
            // Also chain the shader-include setup — both are required for
            // a working build and both are easy to forget. Idempotent on
            // re-runs.
            int shaderAdded = ShaderIncludeSetup.AddAllToAlwaysIncluded(out string shaderDetail);
            string title = "Weva";
            string body =
                (added > 0
                    ? "Added UIBatchedRendererFeature to " + added + " renderer(s).\n\n"
                    : "URP renderer feature: nothing to add.\n\n")
                + detail
                + "\n\n--- Shaders ---\n\n"
                + (shaderAdded > 0
                    ? $"Added {shaderAdded} shader(s) to Always Included Shaders.\n\n"
                    : "Always Included Shaders: nothing to add.\n\n")
                + shaderDetail;
            EditorUtility.DisplayDialog(title, body, "OK");
        }

        // Returns the number of renderer assets the feature was added to.
        // `detail` is a human-readable explanation suitable for a dialog
        // or a Console log.
        public static int AddFeatureToActiveRenderer(out string detail) {
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null) {
                detail = "URP isn't the active render pipeline. Set Project Settings → Graphics → Scriptable Render Pipeline Settings to a UniversalRenderPipelineAsset first.";
                return 0;
            }

            // URP keeps its renderer-data list private (`m_RendererDataList`).
            // Reflect into it so we can inspect every renderer the user has
            // configured, not just `defaultRenderer`.
            var rendererListField = typeof(UniversalRenderPipelineAsset)
                .GetField("m_RendererDataList", BindingFlags.NonPublic | BindingFlags.Instance);
            if (rendererListField == null) {
                detail = "Couldn't reflect URP renderer-data list — URP version may have changed its internal layout.";
                return 0;
            }
            var list = rendererListField.GetValue(pipeline) as ScriptableRendererData[];
            if (list == null || list.Length == 0) {
                detail = "URP pipeline has no renderer data assets configured.";
                return 0;
            }

            int added = 0;
            var sb = new System.Text.StringBuilder();
            foreach (var data in list) {
                if (data == null) continue;
                if (HasFeature(data)) {
                    sb.AppendLine("• " + data.name + " — already has the feature");
                    continue;
                }
                var feature = ScriptableObject.CreateInstance<UIBatchedRendererFeature>();
                feature.name = nameof(UIBatchedRendererFeature);
                AppendFeature(data, feature);
                AssetDatabase.AddObjectToAsset(feature, data);
                EditorUtility.SetDirty(data);
                sb.AppendLine("• " + data.name + " — added");
                added++;
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            detail = sb.ToString();
            return added;
        }

        static bool HasFeature(ScriptableRendererData data) {
            foreach (var f in data.rendererFeatures) {
                if (f is UIBatchedRendererFeature) return true;
            }
            return false;
        }

        // ScriptableRendererData.rendererFeatures is a public IList; URP's
        // API also has an internal `m_RendererFeatures` field. We append
        // through the public list and call the data's internal save hook
        // so the inspector picks up the change without a domain reload.
        static void AppendFeature(ScriptableRendererData data, ScriptableRendererFeature feature) {
            data.rendererFeatures.Add(feature);
            // Tell the renderer to rebuild its feature pipeline on next
            // SetupRenderPasses; URP exposes this via SetDirty internally.
            var setDirty = typeof(ScriptableRendererData).GetMethod("SetDirty", BindingFlags.Instance | BindingFlags.NonPublic);
            setDirty?.Invoke(data, null);
        }
    }
}
#endif
