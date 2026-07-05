using System;
using UnityEditor;
using UnityEngine;

namespace Weva.EditorTools.Documents {
    // Hot-reloads WevaDocument instances when the .html / .css / .htm assets they
    // reference are reimported.
    //
    // Notes on Unity quirks:
    //   - AssetPostprocessor lives in editor assemblies and is rediscovered by
    //     the editor on every domain reload, so we don't need any registration
    //     bookkeeping — the static OnPostprocessAllAssets entry point is found
    //     via reflection.
    //   - In edit mode we defer the Rebuild() to EditorApplication.delayCall:
    //     OnPostprocessAllAssets fires inside the asset import phase where
    //     touching scene objects (and triggering a re-import) is unsafe and
    //     can re-enter the postprocessor pipeline. delayCall executes on the
    //     next editor tick after the import completes.
    //   - In Play mode the same delayCall path is fine, but we Rebuild()
    //     immediately so the running application sees the change without
    //     waiting for the next editor frame.
    //   - FindObjectsByType is O(n) over loaded scene objects. Acceptable for
    //     editor-only hot-reload; a runtime registry would be faster but adds
    //     a global dependency we don't yet need.
    public sealed class UIDocumentAssetWatcher : AssetPostprocessor {
        static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths) {
            if (!HasRelevantChange(importedAssets, movedAssets)) return;
            var docs = GameObject.FindObjectsByType<WevaDocument>(FindObjectsInactive.Include);
            if (docs == null || docs.Length == 0) return;
            for (int i = 0; i < docs.Length; i++) {
                var doc = docs[i];
                if (doc == null) continue;
                if (!ReferencesAny(doc, importedAssets) && !ReferencesAny(doc, movedAssets)) continue;
                if (Application.isPlaying) {
                    SafeRebuild(doc);
                } else {
                    var captured = doc;
                    EditorApplication.delayCall += () => SafeRebuild(captured);
                }
            }
        }

        static void SafeRebuild(WevaDocument doc) {
            if (doc == null) return;
            try {
                doc.Rebuild();
            } catch (Exception ex) {
                Debug.LogWarning("Weva: hot-reload Rebuild failed on '" + doc.name + "': " + ex.Message, doc);
            }
        }

        static bool HasRelevantChange(string[] importedAssets, string[] movedAssets) {
            for (int i = 0; i < importedAssets.Length; i++) {
                if (IsRelevant(importedAssets[i])) return true;
            }
            for (int i = 0; i < movedAssets.Length; i++) {
                if (IsRelevant(movedAssets[i])) return true;
            }
            return false;
        }

        static bool IsRelevant(string path) {
            if (string.IsNullOrEmpty(path)) return false;
            return path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase);
        }

        static bool ReferencesAny(WevaDocument doc, string[] paths) {
            if (paths == null || paths.Length == 0) return false;
            var docAsset = doc.DocumentAsset;
            if (docAsset != null) {
                var p = AssetDatabase.GetAssetPath(docAsset);
                if (!string.IsNullOrEmpty(p) && PathArrayContains(paths, p)) return true;
            }
            var sheets = doc.StylesheetAssets;
            if (sheets != null) {
                for (int i = 0; i < sheets.Length; i++) {
                    var s = sheets[i];
                    if (s == null) continue;
                    var p = AssetDatabase.GetAssetPath(s);
                    if (!string.IsNullOrEmpty(p) && PathArrayContains(paths, p)) return true;
                }
            }
            return false;
        }

        static bool PathArrayContains(string[] paths, string target) {
            for (int i = 0; i < paths.Length; i++) {
                if (string.Equals(paths[i], target, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
