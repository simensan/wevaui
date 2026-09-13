using System;
using UnityEditor;
using UnityEngine;

namespace Weva.EditorTools.Documents {
    // Hot-reloads WevaDocument instances when the .html / .css / .htm assets
    // they reference are reimported.
    //
    // Notes on Unity quirks:
    //   - AssetPostprocessor lives in editor assemblies and is rediscovered by
    //     the editor on every domain reload, so we don't need any registration
    //     bookkeeping — the static OnPostprocessAllAssets entry point is found
    //     via reflection.
    //   - In edit mode we defer the Reload() to EditorApplication.delayCall:
    //     OnPostprocessAllAssets fires inside the asset import phase where
    //     touching scene objects (and triggering a re-import) is unsafe and
    //     can re-enter the postprocessor pipeline. delayCall executes on the
    //     next editor tick after the import completes.
    //   - In Play mode the same delayCall path is fine, but we Reload()
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
            ReloadCoreDocuments(importedAssets, movedAssets);
        }

        // The core-backed document: Reload() parses again and keeps the
        // controller, so a saved stylesheet lands in a running scene.
        static void ReloadCoreDocuments(string[] importedAssets, string[] movedAssets) {
            // Both args spelled out: the (FindObjectsInactive)-only overload
            // does not exist before Unity 6000.4, and this package supports
            // 6000.3 consumers.
            var docs = GameObject.FindObjectsByType<WevaDocument>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (docs == null) return;
            for (int i = 0; i < docs.Length; i++) {
                var doc = docs[i];
                if (doc == null) continue;
                if (!ReferencesAny(doc.DocumentAsset, doc.StylesheetAssets, importedAssets)
                    && !ReferencesAny(doc.DocumentAsset, doc.StylesheetAssets, movedAssets)
                    && !LinksAny(doc, importedAssets) && !LinksAny(doc, movedAssets)) continue;
                if (Application.isPlaying) {
                    SafeReload(doc);
                } else {
                    var captured = doc;
                    EditorApplication.delayCall += () => SafeReload(captured);
                }
            }
        }

        // A <link rel="stylesheet"> resolves next to the document asset.
        static bool LinksAny(WevaDocument doc, string[] paths) {
            if (paths == null || paths.Length == 0) return false;
            var hrefs = doc.LinkedStylesheetHrefs;
            if (hrefs == null || hrefs.Count == 0) return false;
            string dir = doc.DocumentAssetDirectory();
            if (dir == null) return false;
            for (int i = 0; i < hrefs.Count; i++) {
                string p = System.IO.Path.Combine(dir, hrefs[i]).Replace('\\', '/');
                if (PathArrayContains(paths, p)) return true;
            }
            return false;
        }

        static void SafeReload(WevaDocument doc) {
            if (doc == null) return;
            try {
                doc.Reload();
            } catch (Exception ex) {
                Debug.LogWarning("Weva: hot-reload failed on '" + doc.name + "': " + ex.Message, doc);
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

        static bool ReferencesAny(TextAsset docAsset, TextAsset[] sheets, string[] paths) {
            if (paths == null || paths.Length == 0) return false;
            if (docAsset != null) {
                var p = AssetDatabase.GetAssetPath(docAsset);
                if (!string.IsNullOrEmpty(p) && PathArrayContains(paths, p)) return true;
            }
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
