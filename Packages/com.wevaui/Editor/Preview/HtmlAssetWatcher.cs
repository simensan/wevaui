using System;
using System.Collections.Generic;
using UnityEditor;

namespace Weva.EditorTools.Preview {
    // Subscribes to AssetDatabase imports via AssetPostprocessor and surfaces a
    // single static event the preview window listens to. We could rely on the
    // window's own Repaint via EditorApplication.update polling, but routing
    // through OnPostprocessAllAssets is cheaper and exact.
    //
    // Survives domain reloads automatically: AssetPostprocessor is rediscovered
    // by the editor on each reload, so re-registration is free as long as the
    // event consumers re-attach in their own OnEnable hooks.
    public sealed class HtmlAssetWatcher : AssetPostprocessor {
        public static event Action<string[]> Changed;

        static readonly HashSet<string> WatchedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            ".html", ".htm", ".css"
        };

        static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths) {
            if (Changed == null) return;
            List<string> hits = null;
            foreach (var p in importedAssets) {
                if (string.IsNullOrEmpty(p)) continue;
                int dot = p.LastIndexOf('.');
                if (dot < 0) continue;
                var ext = p.Substring(dot);
                if (!WatchedExtensions.Contains(ext)) continue;
                hits ??= new List<string>();
                hits.Add(p);
            }
            if (hits == null) return;
            try {
                Changed?.Invoke(hits.ToArray());
            } catch (Exception ex) {
                UnityEngine.Debug.LogWarning("Weva Preview watcher consumer threw: " + ex);
            }
        }
    }
}
