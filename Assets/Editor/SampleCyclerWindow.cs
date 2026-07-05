// Dev tool: quickly cycle the scene's WevaDocument through every sample
// HTML/CSS combo under Assets/UI without hand-editing the inspector.
//
// Open via  Window ▸ Weva ▸ Sample Cycler  (or Ctrl/Cmd+Alt+U).
// Works in both edit mode and play mode — setting DocumentAsset triggers
// the document's own Rebuild (AutoRebuildOnChange), and we nudge the Game
// view to repaint so the swap is visible immediately.
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Weva;

public sealed class SampleCyclerWindow : EditorWindow {
    const string SampleDir = "Assets/UI";

    [System.Serializable]
    struct Sample {
        public string Name;     // file name without extension
        public string Path;     // asset path
        public bool HasCss;     // sibling <name>.css exists
    }

    readonly List<Sample> samples = new();
    string filter = "";
    Vector2 scroll;
    WevaDocument target;          // the WevaDocument we drive
    int currentIndex = -1;      // index into `samples` of the live document
    double lastRefreshTime;

    [MenuItem("Window/Weva/Sample Cycler %&u")]
    static void Open() {
        var w = GetWindow<SampleCyclerWindow>("UI Samples");
        w.minSize = new Vector2(260, 200);
        w.Refresh();
    }

    void OnEnable() => Refresh();

    void OnFocus() {
        // Cheap re-scan when the window regains focus so newly-added
        // samples / a re-opened scene are picked up without a manual refresh.
        if (EditorApplication.timeSinceStartup - lastRefreshTime > 0.5)
            Refresh();
    }

    void Refresh() {
        lastRefreshTime = EditorApplication.timeSinceStartup;
        samples.Clear();
        if (Directory.Exists(SampleDir)) {
            foreach (var path in Directory.GetFiles(SampleDir, "*.html", SearchOption.TopDirectoryOnly)
                                          .Select(p => p.Replace('\\', '/'))
                                          .OrderBy(p => p)) {
                var name = Path.GetFileNameWithoutExtension(path);
                samples.Add(new Sample {
                    Name = name,
                    Path = path,
                    HasCss = File.Exists(Path.ChangeExtension(path, ".css")),
                });
            }
        }
        ResolveTarget();
        SyncCurrentIndex();
        Repaint();
    }

    void ResolveTarget() {
        if (target != null) return;
        // Prefer a WevaDocument named "DemoUI"; otherwise the first one found.
        var docs = Object.FindObjectsByType<WevaDocument>(FindObjectsInactive.Include);
        target = docs.FirstOrDefault(d => d.name == "DemoUI") ?? docs.FirstOrDefault();
    }

    void SyncCurrentIndex() {
        currentIndex = -1;
        if (target == null || target.DocumentAsset == null) return;
        var path = AssetDatabase.GetAssetPath(target.DocumentAsset).Replace('\\', '/');
        currentIndex = samples.FindIndex(s => s.Path == path);
    }

    void OnGUI() {
        DrawToolbar();

        if (target == null) {
            EditorGUILayout.HelpBox("No WevaDocument found in the open scene. Open a scene that contains one (e.g. DemoUI).", MessageType.Warning);
            if (GUILayout.Button("Rescan scene")) { target = null; ResolveTarget(); SyncCurrentIndex(); }
            return;
        }

        EditorGUILayout.Space(2);
        DrawList();
    }

    void DrawToolbar() {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar)) {
            using (new EditorGUI.DisabledScope(samples.Count == 0)) {
                if (GUILayout.Button("◀ Prev", EditorStyles.toolbarButton, GUILayout.Width(64))) Step(-1);
                if (GUILayout.Button("Next ▶", EditorStyles.toolbarButton, GUILayout.Width(64))) Step(1);
            }
            string current = (currentIndex >= 0 && currentIndex < samples.Count)
                ? $"{samples[currentIndex].Name}  ({currentIndex + 1}/{samples.Count})"
                : $"— ({samples.Count} samples)";
            GUILayout.Label(current, EditorStyles.toolbarButton);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("⟳", EditorStyles.toolbarButton, GUILayout.Width(24))) Refresh();
        }

        using (new EditorGUILayout.HorizontalScope()) {
            EditorGUIUtility.labelWidth = 40;
            target = (WevaDocument)EditorGUILayout.ObjectField("Doc", target, typeof(WevaDocument), allowSceneObjects: true);
            EditorGUIUtility.labelWidth = 0;
        }

        filter = EditorGUILayout.TextField(GUIContent.none, filter);
        if (string.IsNullOrEmpty(filter)) {
            var r = GUILayoutUtility.GetLastRect();
            EditorGUI.LabelField(new Rect(r.x + 4, r.y, r.width, r.height), "Filter…", EditorStyles.centeredGreyMiniLabel);
        }
    }

    void DrawList() {
        using var sv = new EditorGUILayout.ScrollViewScope(scroll);
        scroll = sv.scrollPosition;
        for (int i = 0; i < samples.Count; i++) {
            var s = samples[i];
            if (!string.IsNullOrEmpty(filter) &&
                s.Name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            bool isCurrent = i == currentIndex;
            var row = EditorGUILayout.GetControlRect(false, 20);
            if (isCurrent) EditorGUI.DrawRect(row, new Color(0.24f, 0.48f, 0.90f, 0.25f));

            var labelRect = new Rect(row.x + 6, row.y, row.width - 70, row.height);
            var cssRect = new Rect(row.xMax - 60, row.y, 56, row.height);

            if (GUI.Button(labelRect, s.Name, EditorStyles.label)) Load(i);
            GUI.Label(cssRect, s.HasCss ? "css" : "—", EditorStyles.miniLabel);
        }
    }

    void Step(int dir) {
        if (samples.Count == 0) return;
        int start = currentIndex < 0 ? -1 : currentIndex;
        int next = ((start + dir) % samples.Count + samples.Count) % samples.Count;
        Load(next);
    }

    void Load(int index) {
        if (target == null || index < 0 || index >= samples.Count) return;
        var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(samples[index].Path);
        if (asset == null) {
            Debug.LogWarning($"[SampleCycler] Could not load {samples[index].Path} as a TextAsset.");
            return;
        }

        Undo.RecordObject(target, "Switch UI Sample");
        // Linked stylesheets resolve relative to the .html (<link rel=...>),
        // so clearing any explicit StylesheetAssets avoids a stale sheet from
        // a previous sample overriding the new one.
        target.StylesheetAssets = new TextAsset[0];
        target.DocumentAsset = asset; // setter triggers Rebuild when enabled
        currentIndex = index;

        if (!Application.isPlaying) {
            // Edit mode: the setter already rebuilt if AutoRebuildOnChange;
            // call Rebuild defensively and force the Game view to repaint.
            target.Rebuild();
        }
        EditorUtility.SetDirty(target);
        RepaintGameViews();
        Repaint();
    }

    static void RepaintGameViews() {
        // Nudge all Game views so the swapped document shows without
        // requiring the user to move the mouse over the view.
        foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>()) {
            if (w.GetType().Name == "GameView") w.Repaint();
        }
        SceneView.RepaintAll();
    }
}
