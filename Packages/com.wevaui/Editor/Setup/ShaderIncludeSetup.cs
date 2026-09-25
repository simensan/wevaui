using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Weva.EditorTools.Setup {
    // One-shot helper that adds the Weva shader to Project Settings →
    // Graphics → Always Included Shaders.
    //
    // Without this, the shaders aren't referenced by any committed Material
    // or Resources/ asset, so Unity STRIPS them from the player build. The
    // symptom in builds is a document that draws nothing. Editor Play mode
    // hides the problem because
    // Unity loads every shader it knows about, regardless of inclusion.
    //
    // Idempotent — re-running with all shaders already included is a no-op.
    // Adds to whatever is currently in the list; never removes the user's
    // other shaders.
    public static class ShaderIncludeSetup {
        const string MenuPath = "Window/Weva/Setup/Add Shaders to Always Included";

        // The shaders the runtime loads by Shader.Find: the one that draws the
        // core's draw list and the one behind backdrop-filter
        // (Runtime/Native/Resources/Weva-Native*.shader). The name is the
        // `Shader "..."` declaration, not the file name.
        static readonly string[] RequiredShaderNames = {
            "Hidden/Weva/NativeMesh",
            "Hidden/Weva/NativeBackdrop",
        };

        // Auto-configure on editor load so consumers don't have to discover the
        // menu item — the #1 "blank UI in a player build" support trap. Deferred
        // to delayCall so the AssetDatabase has imported the shaders before
        // Shader.Find runs, and skipped in batch mode so CI / headless test runs
        // never dirty ProjectSettings/GraphicsSettings.asset. AddAllToAlwaysIncluded
        // is idempotent, so this is a no-op once the shaders are present; if they
        // aren't found yet (mid-import) the next domain reload retries.
        [InitializeOnLoadMethod]
        static void EnsureIncludedOnLoad() {
            if (Application.isBatchMode) return;
            EditorApplication.delayCall += () => {
                int added = AddAllToAlwaysIncluded(out _);
                if (added > 0) {
                    Debug.Log($"[Weva] Added {added} shader(s) to Always Included Shaders so they " +
                              "survive player-build stripping (Project Settings ▸ Graphics). " +
                              "Run Window ▸ Weva ▸ Setup to re-apply manually.");
                }
            };
        }

        [MenuItem(MenuPath)]
        public static void RunFromMenu() {
            int added = AddAllToAlwaysIncluded(out string detail);
            string title = "Weva Shader Setup";
            if (added > 0) {
                EditorUtility.DisplayDialog(title,
                    $"Added {added} shader(s) to Always Included Shaders.\n\n" + detail,
                    "OK");
            } else {
                EditorUtility.DisplayDialog(title,
                    "All Weva shaders are already included.\n\n" + detail,
                    "OK");
            }
        }

        // Returns the number of shaders added to the Always Included list
        // (i.e. that weren't already present). `detail` is a per-shader
        // human-readable summary suitable for a dialog or Console log.
        public static int AddAllToAlwaysIncluded(out string detail) {
            var graphics = AssetDatabase.LoadAssetAtPath<GraphicsSettings>(
                "ProjectSettings/GraphicsSettings.asset");
            if (graphics == null) {
                // GraphicsSettings is loaded via a SerializedObject on the
                // singleton instance — there's no asset to load directly.
                graphics = GetGraphicsSettings();
            }
            if (graphics == null) {
                detail = "Couldn't open ProjectSettings/GraphicsSettings.asset.";
                return 0;
            }

            var serialized = new SerializedObject(graphics);
            var arr = serialized.FindProperty("m_AlwaysIncludedShaders");
            if (arr == null || !arr.isArray) {
                detail = "Couldn't find m_AlwaysIncludedShaders array — Unity version may have changed its layout.";
                return 0;
            }

            // Build a fast lookup of shaders currently in the list so we
            // only insert missing ones.
            var present = new HashSet<string>();
            for (int i = 0; i < arr.arraySize; i++) {
                var elem = arr.GetArrayElementAtIndex(i);
                var s = elem?.objectReferenceValue as Shader;
                if (s != null) present.Add(s.name);
            }

            int added = 0;
            var sb = new System.Text.StringBuilder();
            foreach (var name in RequiredShaderNames) {
                if (present.Contains(name)) {
                    sb.AppendLine("• " + name + " — already included");
                    continue;
                }
                var shader = Shader.Find(name);
                if (shader == null) {
                    sb.AppendLine("• " + name + " — NOT FOUND (shader missing from project)");
                    continue;
                }
                arr.arraySize++;
                var newElem = arr.GetArrayElementAtIndex(arr.arraySize - 1);
                newElem.objectReferenceValue = shader;
                sb.AppendLine("• " + name + " — added");
                added++;
            }

            if (added > 0) {
                serialized.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
            }
            detail = sb.ToString();
            return added;
        }

        // Returns the singleton GraphicsSettings asset (the one backing
        // Project Settings → Graphics). Older Unity APIs hide this behind
        // an internal method; the modern API exposes it through
        // GraphicsSettings.GetGraphicsSettings() but earlier versions
        // require reflecting into the internal `GetGraphicsSettings` or
        // loading via UnityEditor.Unsupported.
        static GraphicsSettings GetGraphicsSettings() {
#if UNITY_2020_2_OR_NEWER
            return UnityEditor.Unsupported.GetSerializedAssetInterfaceSingleton("GraphicsSettings") as GraphicsSettings;
#else
            var m = typeof(GraphicsSettings).GetMethod("GetGraphicsSettings",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            return m?.Invoke(null, null) as GraphicsSettings;
#endif
        }
    }
}
