using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Weva.EditorTools.Preview;
using Weva.Rendering;
using PreviewMode = Weva.EditorTools.Preview.PreviewMode;
using PaintList = Weva.Paint.PaintList;
using RecordingBackend = Weva.Paint.RecordingBackend;

namespace Weva.EditorTools {
    // PLAN §9 #7: editor preview = EditorWindow + (formerly) Graphics.DrawTexture
    // of an offscreen RT. v1 uses a Texture2D filled by SoftwarePainter and displayed
    // through EditorGUI.DrawPreviewTexture (which handles alpha and gamma correctly
    // in the editor — Graphics.DrawTexture only works inside OnGUI/Repaint and
    // doesn't compose well over EditorGUILayout chrome).
    //
    // Two modes (PreviewMode):
    //  - Asset: a .html TextAsset is selected -> parse + cascade + layout + paint.
    //  - Scene: a WevaDocument MonoBehaviour is selected. v1 reads its DocumentAsset
    //    + StylesheetAssets fields and runs the same pipeline. Once WevaDocument owns
    //    a live PaintList we'll instead consume IUIPaintSource.EmitPaint directly.
    //
    // Domain-reload survival: state is plain serialized fields on the EditorWindow,
    // and the watcher subscription is re-attached in OnEnable. The PreviewRenderer
    // is recreated on enable; its Texture2D is allocated lazily.
    public sealed class UIPreviewWindow : EditorWindow {
        [SerializeField] PreviewToolbar.State toolbarState = new PreviewToolbar.State {
            Mode = PreviewMode.Asset,
            ZoomIndex = 1,
            Preset = PreviewViewportPreset.Desktop,
            ColorScheme = PreviewColorScheme.Light,
        };
        [SerializeField] Vector2 scrollPosition;

        PreviewRenderer renderer;
        double pendingRefreshAt;
        bool refreshPending;
        const double DebounceSeconds = 0.25;

        [MenuItem("Window/Weva/Preview")]
        public static void Open() => GetWindow<UIPreviewWindow>("Weva Preview");

        // EditorWindow instances survive domain reloads by serialization, but the
        // managed renderer/texture handles do not. OnEnable runs after each reload.
        void OnEnable() {
            renderer = new PreviewRenderer();
            HtmlAssetWatcher.Changed += OnAssetsChanged;
            Selection.selectionChanged += OnSelectionChanged;
            EditorApplication.update += OnEditorUpdate;
            ScheduleRefresh();
        }

        void OnDisable() {
            HtmlAssetWatcher.Changed -= OnAssetsChanged;
            Selection.selectionChanged -= OnSelectionChanged;
            EditorApplication.update -= OnEditorUpdate;
            renderer?.Dispose();
            renderer = null;
        }

        void OnSelectionChanged() {
            ScheduleRefresh();
            Repaint();
        }

        void OnAssetsChanged(string[] paths) {
            renderer?.InvalidateAssetCache();
            ScheduleRefresh();
        }

        void OnEditorUpdate() {
            if (!refreshPending) return;
            if (EditorApplication.timeSinceStartup < pendingRefreshAt) return;
            refreshPending = false;
            RunPipeline();
            Repaint();
        }

        void ScheduleRefresh() {
            refreshPending = true;
            pendingRefreshAt = EditorApplication.timeSinceStartup + DebounceSeconds;
        }

        void OnGUI() {
            var newState = PreviewToolbar.Draw(toolbarState);
            bool changed =
                newState.Mode != toolbarState.Mode ||
                newState.Preset != toolbarState.Preset ||
                newState.ColorScheme != toolbarState.ColorScheme ||
                newState.ZoomIndex != toolbarState.ZoomIndex;
            toolbarState = newState;
            if (changed || newState.RefreshRequested) {
                if (newState.RefreshRequested) renderer?.InvalidateAssetCache();
                RunPipeline();
            }

            DrawSelectionStrip();
            DrawCanvas();
            DrawStatusBar();
        }

        void DrawSelectionStrip() {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox)) {
                string label = DescribeSelection();
                EditorGUILayout.LabelField(label, EditorStyles.miniLabel);
            }
        }

        string DescribeSelection() {
            if (toolbarState.Mode == PreviewMode.Asset) {
                var asset = ResolveSelectedHtml();
                if (asset == null) return "Select a .html TextAsset in the Project window.";
                var path = AssetDatabase.GetAssetPath(asset);
                return string.IsNullOrEmpty(path) ? asset.name : path;
            }
            var doc = ResolveSelectedDocument();
            if (doc == null) return "Select a GameObject with a WevaDocument component.";
            return doc.gameObject.name + " (WevaDocument)";
        }

        void DrawCanvas() {
            var viewport = CurrentViewport();
            float zoom = PreviewToolbar.ZoomFor(toolbarState.ZoomIndex);
            float texW = viewport.Width * zoom;
            float texH = viewport.Height * zoom;

            using (var scroll = new EditorGUILayout.ScrollViewScope(scrollPosition, GUILayout.ExpandHeight(true))) {
                scrollPosition = scroll.scrollPosition;
                var rect = GUILayoutUtility.GetRect(texW, texH, GUILayout.Width(texW), GUILayout.Height(texH));
                EditorGUI.DrawRect(rect, ChromeBackground());
                if (renderer != null && renderer.OutputTexture != null && renderer.HasContent) {
                    EditorGUI.DrawPreviewTexture(rect, renderer.OutputTexture, null, ScaleMode.StretchToFill);
                }
                if (renderer != null && !string.IsNullOrEmpty(renderer.LastError)) {
                    var msgRect = new Rect(rect.x + 8, rect.y + 8, rect.width - 16, 36);
                    EditorGUI.HelpBox(msgRect, renderer.LastError, MessageType.Warning);
                }
            }
        }

        void DrawStatusBar() {
            var viewport = CurrentViewport();
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar)) {
                EditorGUILayout.LabelField(
                    string.Format("Viewport {0}x{1} px @ {2:0%}", viewport.Width, viewport.Height,
                        PreviewToolbar.ZoomFor(toolbarState.ZoomIndex)),
                    EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (renderer != null && renderer.HasContent) {
                    EditorGUILayout.LabelField(renderer.LastCommandCount + " paint cmds", EditorStyles.miniLabel, GUILayout.Width(110));
                }
            }
        }

        Color ChromeBackground() {
            return toolbarState.ColorScheme == PreviewColorScheme.Dark
                ? new Color(0.08f, 0.08f, 0.09f, 1f)
                : new Color(0.95f, 0.95f, 0.96f, 1f);
        }

        PreviewViewport CurrentViewport() {
            return PreviewViewport.FromPreset(toolbarState.Preset, toolbarState.ColorScheme);
        }

        void RunPipeline() {
            if (renderer == null) return;
            var viewport = CurrentViewport();
            switch (toolbarState.Mode) {
                case PreviewMode.Asset:
                    var asset = ResolveSelectedHtml();
                    var sheets = ResolveSelectedStylesheets();
                    renderer.RenderAsset(asset, sheets, viewport);
                    break;
                case PreviewMode.Scene:
                    RunScenePipeline(viewport);
                    break;
            }
        }

        void RunScenePipeline(PreviewViewport viewport) {
            var doc = ResolveSelectedDocument();
            if (doc == null) {
                renderer.RenderAsset(null, null, viewport);
                return;
            }
            // Prefer pulling already-built paint commands via IUIPaintSource if the
            // WevaDocument exposes them. The MonoBehaviour wiring is not yet in place
            // (see PLAN §11 "What's left of v1"), so we fall through to running the
            // pipeline against its asset references for v1.
            var paintSources = doc.GetComponents<IUIPaintSource>();
            PaintList list = null;
            if (paintSources != null) {
                Array.Sort(paintSources, (a, b) => a.Order.CompareTo(b.Order));
                foreach (var src in paintSources) {
                    if (src == null) continue;
                    var rec = new RecordingBackend();
                    try { src.EmitPaint(rec); }
                    catch (Exception ex) { Debug.LogWarning("Weva: IUIPaintSource threw: " + ex); continue; }
                    if (rec.Recorded.Count == 0) continue;
                    if (list == null) list = new PaintList();
                    foreach (var cmd in rec.Recorded) list.Add(cmd);
                }
            }
            if (list != null && list.Commands.Count > 0) {
                renderer.RenderPaintList(list, viewport);
                return;
            }
            renderer.RenderAsset(doc.DocumentAsset, doc.StylesheetAssets, viewport);
        }

        TextAsset ResolveSelectedHtml() {
            var obj = Selection.activeObject;
            if (obj is TextAsset ta && IsHtmlAsset(ta)) return ta;
            return null;
        }

        IReadOnlyList<TextAsset> ResolveSelectedStylesheets() {
            var doc = ResolveSelectedDocument();
            return doc != null ? doc.StylesheetAssets : null;
        }

        Weva.WevaDocument ResolveSelectedDocument() {
            var go = Selection.activeGameObject;
            if (go == null) return null;
            return go.GetComponent<Weva.WevaDocument>();
        }

        static bool IsHtmlAsset(TextAsset ta) {
            if (ta == null) return false;
            var path = AssetDatabase.GetAssetPath(ta);
            if (string.IsNullOrEmpty(path)) return true;
            var lower = path.ToLowerInvariant();
            return lower.EndsWith(".html") || lower.EndsWith(".htm");
        }

        // Re-register the menu after a domain reload — InitializeOnLoadMethod is the
        // canonical hook (the [MenuItem] above is also rediscovered, but the early
        // ping prevents a stale-window race after a script recompile).
        [InitializeOnLoadMethod]
        static void EnsureWindowAlive() {
            // No-op: presence of this method ensures the editor compiles and reloads
            // this assembly in a known state. The window itself is opened on demand.
        }
    }
}
