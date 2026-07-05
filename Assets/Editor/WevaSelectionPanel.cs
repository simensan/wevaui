// Dev-project-only example panel (lives in Assets/Editor, not the package):
// the WEVA_URP guard is dropped because the dev project always has URP.
using UnityEditor;

namespace Weva.EditorTools.Panels {
    // Example: a Weva editor panel bound to LIVE EDITOR STATE (the current Selection), the way a
    // UI Toolkit inspector binds to a SerializedObject. Demonstrates the Controller path —
    // {{ path }} text bindings re-evaluated every frame by UIDocumentLifecycle.Update against the
    // controller, plus an on-click handler that calls a controller method.
    //
    // The controller's property getters read Selection live, so each frame's Bindings.Update
    // pulls current values; selectionChanged triggers a Repaint so the panel reflects the new
    // selection immediately rather than waiting for the next input/animation tick.
    //
    // Window ▸ Weva Dev ▸ Selection Panel
    //
    // STATUS: shares the WevaEditorPanel render path, which is NOT yet visually verified in a
    // live editor (color space / orientation / DPI — see WevaEditorPanel header).
    public sealed class WevaSelectionPanel : WevaEditorPanel {
        [MenuItem("Window/Weva Dev/Selection Panel")]
        static void Open() => GetWindow<WevaSelectionPanel>("Weva Selection");

        SelectionModel model;
        SelectionModel Model {
            get {
                if (model == null) model = new SelectionModel();
                return model;
            }
        }

        // Stable controller instance across the panel's life (bindings are scanned against it at
        // build time, so it must not change identity between rebuilds).
        protected override object Controller => Model;

        protected override void OnEnable() {
            base.OnEnable();
            Selection.selectionChanged += OnSelectionChanged;
        }

        protected override void OnDisable() {
            Selection.selectionChanged -= OnSelectionChanged;
            base.OnDisable();
        }

        void OnSelectionChanged() => Repaint();

        protected override string Html =>
@"<style>
  body { margin:0; }
  .panel { width:100%; height:100%; background:#1e1e22; padding:18px; box-sizing:border-box; font-family:sans-serif; color:#e6e6e6; }
  .title { font-size:12px; letter-spacing:1px; text-transform:uppercase; color:#8a8a93; margin-bottom:10px; }
  .count { font-size:13px; color:#9aa0a6; margin-bottom:14px; }
  .name  { font-size:22px; font-weight:600; color:#ffffff; }
  .type  { font-size:13px; color:#3b82f6; margin-top:4px; }
  .hint  { margin-top:18px; font-size:12px; color:#6b6b73; }
</style>
<div class='panel'>
  <div class='title'>Selection</div>
  <div class='count'>{{ Count }} object(s) selected</div>
  <div class='name'>{{ ActiveName }}</div>
  <div class='type'>{{ ActiveType }}</div>
  <div class='hint'>Select something in the Hierarchy or Project &mdash; this panel is HTML/CSS bound to the live editor Selection.</div>
</div>";

        // Plain controller object. Getters read editor state live; BindingScanner resolves the
        // {{ Count }} / {{ ActiveName }} / {{ ActiveType }} paths against these public members.
        sealed class SelectionModel {
            public int Count => Selection.objects != null ? Selection.objects.Length : 0;
            public string ActiveName =>
                Selection.activeObject != null ? Selection.activeObject.name : "(nothing selected)";
            public string ActiveType =>
                Selection.activeObject != null ? Selection.activeObject.GetType().Name : "";
        }
    }
}
