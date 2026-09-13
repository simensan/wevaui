using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Weva.EditorTools.Documents {
    // A player has no files, so every <link rel="stylesheet"> a scene-placed
    // WevaDocument references is baked into the component at build time (and
    // on entering play mode, harmlessly: the editor path prefers the live
    // file). A document on a prefab instantiated at runtime never passes
    // through here: call WevaDocument.BakeLinkedStylesheets from a build
    // step, or assign StylesheetAssets, for that one.
    public sealed class WevaDocumentLinkBaker : IProcessSceneWithReport {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report) {
            foreach (GameObject root in scene.GetRootGameObjects()) {
                foreach (WevaDocument doc in root.GetComponentsInChildren<WevaDocument>(true)) {
                    Bake(doc);
                }
            }
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
