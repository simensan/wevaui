using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;
using Weva.Css;
using Weva.Documents;

namespace Weva.EditorTools {
    // <link rel="stylesheet"> resolution is DISK-based: UIDocumentBuilder
    // reads each href relative to DocumentPath, which only exists where
    // AssetDatabase does. Player builds have neither, so without this hook
    // every linked stylesheet silently dropped in builds and pages rendered
    // UA-only (glass.html build report, 2026-06-06). This scene processor
    // bakes each link's CSS text into the WevaDocument's serialized fields
    // at build time; the runtime consumes the bake only when DocumentPath
    // is unavailable, so the editor always reads the live file instead.
    //
    // Extended (2026-06-06) to also bake <template src="..."> HTML and to
    // pre-flatten @import statements inside linked CSS files so baked sheets
    // are self-contained even though players have no on-disk base path for
    // AtImportLoader. Flattening delegates to CssImportFlattener (Runtime)
    // so the logic is headless-testable without Unity assemblies.
    //
    // Scope (documented limitations, mirrored in CSS_OPEN_GAPS.md):
    //  - Scene-placed WevaDocuments only. A WevaDocument on a prefab
    //    instantiated at RUNTIME in a player never passes through
    //    OnProcessScene; those documents still warn and drop links.
    //    Call LinkedStylesheetBaker.Bake from a custom build step or
    //    assign stylesheets explicitly for that case.
    public sealed class LinkedStylesheetBakeProcessor : IProcessSceneWithReport {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report) {
            // Also runs when entering play mode in the editor (report is
            // null there). Baking is harmless then — the editor path
            // prefers disk — and keeps play-in-editor representative of
            // what a build will do.
            var roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++) {
                var docs = roots[i].GetComponentsInChildren<WevaDocument>(true);
                for (int j = 0; j < docs.Length; j++) {
                    LinkedStylesheetBaker.Bake(docs[j]);
                }
            }
        }
    }

    public static class LinkedStylesheetBaker {
        // Resolves every <link rel="stylesheet"> href and <template src="...">
        // in the document against its asset path and writes parallel arrays
        // into the component's serialized bake fields. Also pre-flattens
        // @import rules inside each linked CSS file so the baked text is
        // self-contained. Returns the number of linked stylesheets baked.
        // Public so custom build pipelines can bake prefab-housed documents
        // the scene processor never sees.
        public static int Bake(WevaDocument doc) {
            if (doc == null) return 0;
            var so = new SerializedObject(doc);
            var hrefsProp = so.FindProperty("bakedLinkedStylesheetHrefs");
            var cssProp = so.FindProperty("bakedLinkedStylesheetCss");
            var tmplHrefsProp = so.FindProperty("bakedTemplateHrefs");
            var tmplHtmlProp = so.FindProperty("bakedTemplateHtml");
            if (hrefsProp == null || cssProp == null) {
                Debug.LogWarning("LinkedStylesheetBaker: WevaDocument bake fields not found (rename?).", doc);
                return 0;
            }

            var docAssetProp = so.FindProperty("documentAsset");
            var ta = docAssetProp != null ? docAssetProp.objectReferenceValue as TextAsset : null;
            var hrefs = new List<string>();
            var css = new List<string>();
            var tmplHrefs = new List<string>();
            var tmplHtml = new List<string>();
            if (ta != null) {
                string assetPath = AssetDatabase.GetAssetPath(ta);
                if (!string.IsNullOrEmpty(assetPath)) {
                    string projectRoot = Path.GetDirectoryName(Application.dataPath);
                    string docPath = Path.GetFullPath(Path.Combine(projectRoot, assetPath));

                    // Bake linked stylesheets with @import pre-flattening.
                    var candidates = UIDocumentBuilder.CollectLinkedStylesheetHrefs(ta.text);
                    for (int i = 0; i < candidates.Count; i++) {
                        string resolved = UIDocumentBuilder.ResolveStylesheetHref(candidates[i], docPath);
                        if (string.IsNullOrEmpty(resolved) || !File.Exists(resolved)) {
                            Debug.LogWarning(
                                $"LinkedStylesheetBaker: '{candidates[i]}' in '{assetPath}' does not resolve " +
                                "to a file; the player will drop this stylesheet.", doc);
                            continue;
                        }
                        try {
                            string rawCss = File.ReadAllText(resolved);
                            // Pre-flatten @import statements so the baked text is
                            // self-contained. CssImportFlattener is a headless-
                            // testable Runtime class; we inject File.ReadAllText
                            // here as the production reader.
                            string flattenedCss = CssImportFlattener.Flatten(rawCss, resolved, File.ReadAllText);
                            css.Add(flattenedCss);
                            hrefs.Add(candidates[i]);
                        } catch (IOException ex) {
                            Debug.LogWarning(
                                $"LinkedStylesheetBaker: failed to read '{resolved}': {ex.Message}", doc);
                        }
                    }

                    // Bake component templates (transitive closure).
                    BakeTemplates(ta.text, docPath, tmplHrefs, tmplHtml, doc);
                }
            }

            hrefsProp.arraySize = hrefs.Count;
            cssProp.arraySize = css.Count;
            for (int i = 0; i < hrefs.Count; i++) {
                hrefsProp.GetArrayElementAtIndex(i).stringValue = hrefs[i];
                cssProp.GetArrayElementAtIndex(i).stringValue = css[i];
            }
            if (tmplHrefsProp != null && tmplHtmlProp != null) {
                tmplHrefsProp.arraySize = tmplHrefs.Count;
                tmplHtmlProp.arraySize = tmplHtml.Count;
                for (int i = 0; i < tmplHrefs.Count; i++) {
                    tmplHrefsProp.GetArrayElementAtIndex(i).stringValue = tmplHrefs[i];
                    tmplHtmlProp.GetArrayElementAtIndex(i).stringValue = tmplHtml[i];
                }
            }
            // During build scene processing this mutates the TRANSIENT scene
            // copy Unity is serializing into the player — the source scene
            // on disk is untouched, so no dirty flag leaks into the project.
            so.ApplyModifiedPropertiesWithoutUndo();
            return hrefs.Count;
        }

        // Recursively bakes all <template src="..."> referenced from htmlSource
        // and any templates they reference (transitive closure). Results are
        // deduplicated by src href.
        static void BakeTemplates(string htmlSource, string ownerDocPath,
            List<string> hrefs, List<string> html, Object logContext) {
            var toVisit = new Queue<(string src, string ownerPath)>();
            var visited = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            // Seed from the root document.
            var rootHrefs = UIDocumentBuilder.CollectTemplateHrefs(htmlSource);
            for (int i = 0; i < rootHrefs.Count; i++) {
                toVisit.Enqueue((rootHrefs[i], ownerDocPath));
            }

            while (toVisit.Count > 0) {
                var (src, ownerPath) = toVisit.Dequeue();
                // Deduplicate by the authored src value (exact string match).
                bool alreadyBaked = false;
                for (int i = 0; i < hrefs.Count; i++) {
                    if (string.Equals(hrefs[i], src, System.StringComparison.Ordinal)) {
                        alreadyBaked = true;
                        break;
                    }
                }
                if (alreadyBaked) continue;

                string resolved = UIDocumentBuilder.ResolveTemplateHref(src, ownerPath);
                if (string.IsNullOrEmpty(resolved)) {
                    Debug.LogWarning(
                        $"LinkedStylesheetBaker: template src '{src}' cannot be resolved against '{ownerPath}'.",
                        logContext);
                    continue;
                }
                string normalizedResolved = Path.GetFullPath(resolved);
                if (!File.Exists(normalizedResolved)) {
                    Debug.LogWarning(
                        $"LinkedStylesheetBaker: template '{src}' -> '{normalizedResolved}' not found on disk; " +
                        "the player will not be able to expand this template.", logContext);
                    continue;
                }
                if (!visited.Add(normalizedResolved)) continue; // cycle guard

                string tmplHtml;
                try {
                    tmplHtml = File.ReadAllText(normalizedResolved);
                } catch (IOException ex) {
                    Debug.LogWarning(
                        $"LinkedStylesheetBaker: failed to read template '{normalizedResolved}': {ex.Message}",
                        logContext);
                    continue;
                }

                hrefs.Add(src);
                html.Add(tmplHtml);

                // Discover transitive template references from the loaded file.
                var nestedHrefs = UIDocumentBuilder.CollectTemplateHrefs(tmplHtml);
                for (int i = 0; i < nestedHrefs.Count; i++) {
                    toVisit.Enqueue((nestedHrefs[i], normalizedResolved));
                }
            }
        }
    }
}
