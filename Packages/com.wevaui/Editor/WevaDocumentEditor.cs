using UnityEditor;
using UnityEngine;

namespace Weva.EditorTools {
    // The inspector for the core-backed WevaDocument: the assets, the paint
    // order and colour scheme, then the fonts and the host switches, with the
    // core's own HTML diagnostics and the last error where the author looks
    // first. Reload is always available: the document is alive in edit mode.
    [CustomEditor(typeof(Weva.WevaDocument))]
    public sealed class WevaDocumentEditor : Editor {
        SerializedProperty documentAssetProp;
        SerializedProperty stylesheetAssetsProp;
        SerializedProperty sortingOrderProp;
        SerializedProperty prefersDarkProp;
        SerializedProperty inlineHtmlProp;
        SerializedProperty inlineCssProp;
        SerializedProperty fontProp, boldProp, italicProp, fallbacksProp;
        SerializedProperty basePathProp;
        SerializedProperty userAgentProp;
        SerializedProperty autoInputProp;
        SerializedProperty safeAreaProp;
        bool showFonts;
        bool showInline;

        void OnEnable() {
            documentAssetProp = serializedObject.FindProperty("documentAsset");
            stylesheetAssetsProp = serializedObject.FindProperty("stylesheetAssets");
            sortingOrderProp = serializedObject.FindProperty("sortingOrder");
            prefersDarkProp = serializedObject.FindProperty("prefersDarkColorScheme");
            inlineHtmlProp = serializedObject.FindProperty("InlineHtml");
            inlineCssProp = serializedObject.FindProperty("InlineCss");
            fontProp = serializedObject.FindProperty("Font");
            boldProp = serializedObject.FindProperty("Bold");
            italicProp = serializedObject.FindProperty("Italic");
            fallbacksProp = serializedObject.FindProperty("Fallbacks");
            basePathProp = serializedObject.FindProperty("BasePath");
            userAgentProp = serializedObject.FindProperty("UseUserAgentStylesheet");
            autoInputProp = serializedObject.FindProperty("AutoInput");
            safeAreaProp = serializedObject.FindProperty("FollowScreenSafeArea");
        }

        public override void OnInspectorGUI() {
            var doc = (Weva.WevaDocument)target;
            serializedObject.Update();
            EditorGUILayout.PropertyField(documentAssetProp);
            EditorGUILayout.PropertyField(stylesheetAssetsProp, true);
            EditorGUILayout.PropertyField(sortingOrderProp);
            EditorGUILayout.PropertyField(prefersDarkProp);

            bool inlineInUse = documentAssetProp.objectReferenceValue == null || stylesheetAssetsProp.arraySize == 0;
            showInline = EditorGUILayout.Foldout(showInline || inlineInUse, "Inline markup (used when no asset is set)", true);
            if (showInline) {
                EditorGUILayout.PropertyField(inlineHtmlProp);
                EditorGUILayout.PropertyField(inlineCssProp);
            }

            showFonts = EditorGUILayout.Foldout(showFonts, "Fonts and host", true);
            if (showFonts) {
                EditorGUILayout.PropertyField(fontProp);
                EditorGUILayout.PropertyField(boldProp);
                EditorGUILayout.PropertyField(italicProp);
                EditorGUILayout.PropertyField(fallbacksProp, true);
                EditorGUILayout.PropertyField(basePathProp);
                EditorGUILayout.PropertyField(userAgentProp);
                EditorGUILayout.PropertyField(autoInputProp);
                EditorGUILayout.PropertyField(safeAreaProp);
            }
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            DrawUrpSetupStatus();
            DrawStatus(doc);
            EditorGUILayout.Space();
            if (GUILayout.Button("Reload")) doc.Reload();
        }

        // The same misconfiguration the legacy inspector pointed at: without
        // the renderer feature the core's draw list has no pass to land in.
        void DrawUrpSetupStatus() {
#if WEVA_URP
            if (!Setup.UrpFeatureSetup.IsFeatureMissingOnActiveRenderer()) return;
            EditorGUILayout.HelpBox(
                "URP is active but UIBatchedRendererFeature is missing from the URP Renderer asset, so this document has no pass to draw in.\n" +
                "Scriptable fix (AI/CI): Weva.EditorTools.Setup.UrpFeatureSetup.ApplyNonInteractive()",
                MessageType.Warning);
            if (GUILayout.Button("Add URP Renderer Feature + shader includes")) {
                Setup.UrpFeatureSetup.ApplyNonInteractive();
            }
            EditorGUILayout.Space();
#endif
        }

        void DrawStatus(Weva.WevaDocument doc) {
            if (doc.DocumentAsset == null && string.IsNullOrEmpty(doc.InlineHtml)) {
                EditorGUILayout.HelpBox("No document asset assigned and no inline HTML. The document is empty.", MessageType.Warning);
            }
            if (!string.IsNullOrEmpty(doc.LastError)) {
                EditorGUILayout.HelpBox(doc.LastError, MessageType.Error);
                return;
            }
            if (doc.Document == null) {
                EditorGUILayout.HelpBox("The core document is not loaded (the native plugin failed to load, or the component is disabled).", MessageType.Warning);
                return;
            }
            string diagnostics = doc.Document.HtmlDiagnostics;
            if (!string.IsNullOrEmpty(diagnostics)) {
                EditorGUILayout.HelpBox("HTML recovered from:\n" + diagnostics, MessageType.Warning);
            } else {
                EditorGUILayout.HelpBox("HTML parsed cleanly.", MessageType.Info);
            }
        }
    }
}
