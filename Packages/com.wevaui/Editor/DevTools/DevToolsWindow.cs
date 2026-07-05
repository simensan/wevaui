using UnityEditor;
using UnityEngine;
using Weva;
using Weva.DevTools;

namespace Weva.EditorTools.DevTools {
    // Editor window mirroring the in-game overlay. Selects an active WevaDocument
    // in the scene (or the one the user picks from the dropdown) and runs the
    // same readout pipeline the runtime overlay uses. Doesn't yet stream live
    // computed-style data while in Play Mode — it samples the document state
    // every editor repaint, which is enough for inspecting layout snapshots
    // without a connected play-mode session.
    public sealed class DevToolsWindow : EditorWindow {
        WevaDocument target;
        OverlayMode mode = OverlayMode.All;
        Vector2 scroll;
        readonly PerfReadout perf = new();
        readonly CacheStats cache = new();

        [MenuItem("Window/Weva/DevTools", priority = 200)]
        public static void Open() {
            var w = GetWindow<DevToolsWindow>("Weva DevTools");
            w.minSize = new Vector2(320, 240);
            w.Show();
        }

        void OnEnable() {
            perf.Start();
            EditorApplication.update += OnEditorUpdate;
        }

        void OnDisable() {
            EditorApplication.update -= OnEditorUpdate;
            perf.Dispose();
        }

        void OnEditorUpdate() {
            if (target != null) {
                perf.RecordFrame(Time.unscaledDeltaTime);
                if (target.Painter != null) cache.RecordFrame(target.Painter);
            }
            Repaint();
        }

        void OnGUI() {
            DrawToolbar();
            EditorGUILayout.Space();
            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (target == null) {
                EditorGUILayout.HelpBox("Pick a WevaDocument from the toolbar above. Open scenes are scanned automatically.", MessageType.Info);
            } else {
                DrawDocumentReadout();
            }
            EditorGUILayout.EndScrollView();
        }

        void DrawToolbar() {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            target = (WevaDocument)EditorGUILayout.ObjectField(target, typeof(WevaDocument), allowSceneObjects: true, GUILayout.Width(220));
            if (GUILayout.Button("Pick first in scene", EditorStyles.toolbarButton)) {
                target = FindFirstUIDocument();
            }
            GUILayout.FlexibleSpace();
            mode = (OverlayMode)EditorGUILayout.EnumFlagsField(mode, EditorStyles.toolbarPopup, GUILayout.Width(140));
            EditorGUILayout.EndHorizontal();
        }

        void DrawDocumentReadout() {
            EditorGUILayout.LabelField("Performance", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(perf.Format());
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Paint cache", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(cache.Format());
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Document", EditorStyles.boldLabel);
            var state = target.CurrentState;
            if (state == null || state.RootBox == null) {
                EditorGUILayout.HelpBox("Document has not produced a layout yet. Enter Play Mode to see live readouts.", MessageType.Warning);
                return;
            }
            EditorGUILayout.LabelField("Box tree size", CountBoxes(state.RootBox).ToString());
            EditorGUILayout.LabelField("Element index size", state.ElementToBox != null ? state.ElementToBox.Count.ToString() : "(none)");
            EditorGUILayout.LabelField("Cascade hits / misses",
                state.Cascade != null ? state.Cascade.CacheHits + " / " + state.Cascade.CacheMisses : "(none)");
        }

        static WevaDocument FindFirstUIDocument() {
#if UNITY_2023_1_OR_NEWER
            // FindAnyObjectByType is the modern, ordering-independent API.
            // FindFirstObjectByType was deprecated because its result depends
            // on instance ID ordering; we don't care which WevaDocument we
            // pick — only that one is found.
            return Object.FindAnyObjectByType<WevaDocument>();
#else
            return Object.FindObjectOfType<WevaDocument>();
#endif
        }

        static int CountBoxes(Weva.Layout.Boxes.Box root) {
            if (root == null) return 0;
            int n = 1;
            for (int i = 0; i < root.Children.Count; i++) n += CountBoxes(root.Children[i]);
            return n;
        }
    }
}
