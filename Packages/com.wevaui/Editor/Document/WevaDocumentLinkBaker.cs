using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Weva.EditorTools.Documents {
    // A player has no files, so every <link rel="stylesheet"> a WevaDocument
    // references is baked into the component before a build: scene-placed
    // documents as each scene is processed (and on entering play mode,
    // harmlessly: the editor path prefers the live file), documents on
    // prefab assets once at the start of the build, so one instantiated at
    // runtime carries its sheets too. The editor always prefers the file
    // next to the document asset, so a stale bake never shadows an edit.
    public sealed class WevaDocumentLinkBaker : IProcessSceneWithReport, IPreprocessBuildWithReport {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report) {
            foreach (GameObject root in scene.GetRootGameObjects()) {
                foreach (WevaDocument doc in root.GetComponentsInChildren<WevaDocument>(true)) {
                    Bake(doc);
                }
            }
        }

        public void OnPreprocessBuild(BuildReport report) {
            int baked = BakePrefabs();
            if (baked > 0) Debug.Log("Weva: baked linked stylesheets into " + baked + " prefab document(s) for the build.");
        }

        [MenuItem("Window/Weva/Setup/Bake Linked Stylesheets in Prefabs", priority = 40)]
        public static void BakePrefabsFromMenu() {
            int baked = BakePrefabs();
            EditorUtility.DisplayDialog("Weva", baked > 0
                ? "Baked linked stylesheets into " + baked + " prefab document(s)."
                : "No prefab document needed baking.", "OK");
        }

        /// <summary>
        /// Bakes every WevaDocument on every prefab asset in the project whose
        /// baked sheets are out of date. Returns how many documents changed.
        /// Prefabs whose bake is already current are not touched, so a build
        /// does not dirty the project.
        /// </summary>
        public static int BakePrefabs() {
            int changed = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab")) {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab")) continue;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null || prefab.GetComponentInChildren<WevaDocument>(true) == null) continue;
                GameObject contents = PrefabUtility.LoadPrefabContents(path);
                try {
                    bool dirty = false;
                    foreach (WevaDocument doc in contents.GetComponentsInChildren<WevaDocument>(true)) {
                        var before = doc.BakedLinkedStylesheets;
                        Bake(doc);
                        var after = doc.BakedLinkedStylesheets;
                        if (!Same(before.Hrefs, after.Hrefs) || !Same(before.Css, after.Css)) {
                            dirty = true;
                            changed++;
                        }
                    }
                    if (dirty) PrefabUtility.SaveAsPrefabAsset(contents, path);
                } finally {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }
            return changed;
        }

        static bool Same(string[] a, string[] b) {
            if (a == null || b == null) return (a == null || a.Length == 0) && (b == null || b.Length == 0);
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>Bakes one document's linked sheets from the files next to its document asset. Returns how many.</summary>
        public static int Bake(WevaDocument doc) {
            if (doc == null) return 0;
            string dir = doc.DocumentAssetDirectory();
            if (dir == null) return 0;
            return doc.BakeLinkedStylesheets(href => {
                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(Path.Combine(dir, href).Replace('\\', '/'));
                return asset != null ? asset.text : null;
            });
        }
    }
}
