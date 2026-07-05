using UnityEditor;
using UnityEngine;

namespace Weva.EditorTools.Preview {
    public static class PreviewToolbar {
        public static readonly float[] ZoomLevels = { 0.5f, 1f, 2f };
        static readonly string[] ZoomLabels = { "50%", "100%", "200%" };
        static readonly string[] ModeLabels = { "Asset", "Scene" };
        static readonly string[] PresetLabels = {
            "Mobile (390x844)",
            "Tablet (820x1180)",
            "Desktop (1280x720)",
            "Wide (1920x1080)",
            "Custom"
        };
        static readonly string[] SchemeLabels = { "Light", "Dark" };

        [System.Serializable]
        public struct State {
            public PreviewMode Mode;
            public int ZoomIndex;
            public PreviewViewportPreset Preset;
            public PreviewColorScheme ColorScheme;
            public bool RefreshRequested;
        }

        public static State Draw(State state) {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar)) {
                int newMode = GUILayout.Toolbar(
                    (int)state.Mode,
                    ModeLabels,
                    EditorStyles.toolbarButton,
                    GUILayout.Width(140));
                state.Mode = (PreviewMode)newMode;

                GUILayout.Space(8);

                state.Preset = (PreviewViewportPreset)EditorGUILayout.Popup(
                    (int)state.Preset,
                    PresetLabels,
                    GUILayout.Width(160));

                state.ColorScheme = (PreviewColorScheme)EditorGUILayout.Popup(
                    (int)state.ColorScheme,
                    SchemeLabels,
                    GUILayout.Width(70));

                int newZoom = GUILayout.Toolbar(
                    state.ZoomIndex,
                    ZoomLabels,
                    EditorStyles.toolbarButton,
                    GUILayout.Width(120));
                state.ZoomIndex = Mathf.Clamp(newZoom, 0, ZoomLevels.Length - 1);

                GUILayout.FlexibleSpace();

                state.RefreshRequested = GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70));
            }
            return state;
        }

        public static float ZoomFor(int index) {
            if (index < 0 || index >= ZoomLevels.Length) return 1f;
            return ZoomLevels[index];
        }
    }
}
