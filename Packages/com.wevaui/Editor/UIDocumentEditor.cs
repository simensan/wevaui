using UnityEditor;
using UnityEngine;
using Weva.Parsing;

namespace Weva.EditorTools {
    [CustomEditor(typeof(Weva.WevaDocument))]
    public sealed class UIDocumentEditor : Editor {
        SerializedProperty documentAssetProp;
        SerializedProperty stylesheetAssetsProp;
        SerializedProperty sortingOrderProp;
        SerializedProperty viewportOverrideProp;
        SerializedProperty autoRebuildProp;
        SerializedProperty prefersDarkProp;

        string lastValidationError;

        void OnEnable() {
            documentAssetProp = serializedObject.FindProperty("documentAsset");
            stylesheetAssetsProp = serializedObject.FindProperty("stylesheetAssets");
            sortingOrderProp = serializedObject.FindProperty("sortingOrder");
            viewportOverrideProp = serializedObject.FindProperty("viewportOverride");
            autoRebuildProp = serializedObject.FindProperty("autoRebuildOnChange");
            prefersDarkProp = serializedObject.FindProperty("prefersDarkColorScheme");
        }

        public override void OnInspectorGUI() {
            serializedObject.Update();
            EditorGUILayout.PropertyField(documentAssetProp);
            EditorGUILayout.PropertyField(stylesheetAssetsProp, true);
            EditorGUILayout.PropertyField(sortingOrderProp);
            EditorGUILayout.PropertyField(viewportOverrideProp);
            EditorGUILayout.PropertyField(autoRebuildProp);
            EditorGUILayout.PropertyField(prefersDarkProp);
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            DrawUrpSetupStatus();
            DrawValidation();
            EditorGUILayout.Space();
            DrawActions();
        }

        // Surfaces the missing-renderer-feature misconfiguration right where
        // authors look first, with a one-click fix. Mirrors the once-per-
        // session Console warning WevaDocument emits (UrpFeatureStatus owns
        // the shared detection); this stays visible until actually fixed.
        void DrawUrpSetupStatus() {
#if WEVA_URP
            var doc = (Weva.WevaDocument)target;
            if (doc.RendererBackend == Weva.RendererBackendKind.IMGUI) return;
            if (!Setup.UrpFeatureSetup.IsFeatureMissingOnActiveRenderer()) return;
            EditorGUILayout.HelpBox(
                "URP is active but UIBatchedRendererFeature is missing from the URP Renderer asset. " +
                "This document renders through the IMGUI debug fallback (flat colors, no gradients/filters) — " +
                "or not at all if Renderer Backend is forced to URP.\n" +
                "Scriptable fix (AI/CI): Weva.EditorTools.Setup.UrpFeatureSetup.ApplyNonInteractive()",
                MessageType.Warning);
            if (GUILayout.Button("Add URP Renderer Feature + shader includes")) {
                Setup.UrpFeatureSetup.ApplyNonInteractive();
            }
            EditorGUILayout.Space();
#endif
        }

        void DrawValidation() {
            var doc = (Weva.WevaDocument)target;
            if (doc.DocumentAsset == null) {
                EditorGUILayout.HelpBox("No document asset assigned. WevaDocument is idle.", MessageType.Warning);
                return;
            }

            try {
                HtmlParser.Parse(doc.DocumentAsset.text);
                if (doc.StylesheetAssets != null) {
                    for (int i = 0; i < doc.StylesheetAssets.Length; i++) {
                        var sheet = doc.StylesheetAssets[i];
                        if (sheet == null) continue;
                        Weva.Css.CssParser.Parse(sheet.text);
                    }
                }
                lastValidationError = null;
            } catch (System.Exception ex) {
                lastValidationError = ex.Message;
            }

            if (!string.IsNullOrEmpty(lastValidationError)) {
                EditorGUILayout.HelpBox($"Parse error: {lastValidationError}", MessageType.Error);
            } else {
                EditorGUILayout.HelpBox("HTML and CSS parse cleanly.", MessageType.Info);
            }
        }

        void DrawActions() {
            var doc = (Weva.WevaDocument)target;
            using (new EditorGUI.DisabledScope(!Application.isPlaying)) {
                if (GUILayout.Button("Rebuild now")) {
                    doc.Rebuild();
                }
            }
            if (!Application.isPlaying) {
                EditorGUILayout.HelpBox("Rebuild is only available in Play Mode.", MessageType.None);
            }
        }
    }
}
