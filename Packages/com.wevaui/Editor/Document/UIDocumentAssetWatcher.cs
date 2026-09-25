using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Weva.EditorTools.Documents {
    // The host's asset reader records imports, images and fonts, including
    // missing files. Directly assigned assets are recorded by WevaDocument.
    // This follows the core's actual dependencies without parsing CSS here.
    public sealed class UIDocumentAssetWatcher : AssetPostprocessor {
        static readonly HashSet<WevaDocument> Pending = new HashSet<WevaDocument>();
        static bool reloadScheduled;

        static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths) {
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddPaths(changed, importedAssets);
            AddPaths(changed, deletedAssets);
            AddPaths(changed, movedAssets);
            AddPaths(changed, movedFromAssetPaths);
            if (changed.Count == 0) return;

            // The explicit two-argument overload also supports Unity 6000.3.
            var docs = GameObject.FindObjectsByType<WevaDocument>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var doc in docs) {
                if (doc == null || doc.Document == null) continue;
                foreach (string dependency in doc.Document.AssetDependencies) {
                    if (!changed.Contains(CanonicalPath(dependency))) continue;
                    Pending.Add(doc);
                    break;
                }
            }
            if (Pending.Count == 0 || reloadScheduled) return;
            // Both play and edit mode must leave the import callback before
            // touching documents. Several imports in one refresh reload once.
            reloadScheduled = true;
            // delayCall depends on inspector updates and does not run in all
            // batch/editor-test contexts. The next editor update works there too.
            EditorApplication.update += ReloadPending;
        }

        static void AddPaths(HashSet<string> target, string[] paths) {
            if (paths == null) return;
            foreach (string path in paths) {
                if (!string.IsNullOrEmpty(path)) target.Add(CanonicalPath(path));
            }
        }

        static string CanonicalPath(string path) {
            try { return Path.GetFullPath(path).Replace('\\', '/'); }
            catch (ArgumentException) { return path; }
            catch (NotSupportedException) { return path; }
            catch (PathTooLongException) { return path; }
        }

        static void ReloadPending() {
            if (EditorApplication.isUpdating || EditorApplication.isCompiling) return;
            EditorApplication.update -= ReloadPending;
            var docs = new List<WevaDocument>(Pending);
            Pending.Clear();
            reloadScheduled = false;
            foreach (var doc in docs) {
                if (doc == null) continue;
                try {
                    doc.Reload();
                } catch (Exception ex) {
                    Debug.LogWarning("Weva: hot-reload failed on '" + doc.name + "': " + ex.Message, doc);
                }
            }
        }
    }
}
