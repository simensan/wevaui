// Dev-project-only smoke-test panel (lives in Assets/Editor, not the
// package): the WEVA_URP guard is dropped because the dev project always
// has URP.
using UnityEditor;

namespace Weva.EditorTools.Panels {
    // Smoke-test panel: an EditorWindow whose entire contents are an HTML/CSS document rendered
    // by Weva on the GPU (no Camera, no URP feature), with a live event pipeline. Use it to
    // visually verify the WevaEditorPanel host — color space, orientation, layout — and to
    // confirm interactivity: hovering the cards should change their color (a :hover rule), which
    // exercises Event.current → EventDispatcher → cascade → re-render.
    //
    // Window ▸ Weva Dev ▸ Panel Demo
    public sealed class WevaPanelDemoWindow : WevaEditorPanel {
        [MenuItem("Window/Weva Dev/Panel Demo")]
        static void Open() => GetWindow<WevaPanelDemoWindow>("Weva Panel");

        // A <style> block (supported by UIDocumentBuilder.AppendInlineStyleBlocks) so we can use
        // :hover — inline style= attributes cannot express pseudo-classes.
        protected override string Html =>
@"<style>
  body { margin:0; }
  .panel { width:100%; height:100%; background:#1e1e22; padding:16px; box-sizing:border-box; font-family:sans-serif; }
  .title { background:#3b82f6; color:#ffffff; padding:12px 16px; border-radius:8px; font-size:18px; font-weight:600; }
  .note  { margin-top:12px; background:#2a2a2e; border:1px solid #444; border-radius:6px; padding:14px; color:#ddd; font-size:13px; }
  .row   { display:flex; margin-top:12px; }
  .card  { flex:1; height:56px; border-radius:6px; margin-right:8px; transition: background-color 120ms ease, transform 120ms ease; }
  .card:last-child { margin-right:0; }
  .card:hover { transform: translateY(-3px); }
  .a { background:#10b981; } .a:hover { background:#34d399; }
  .b { background:#f59e0b; } .b:hover { background:#fbbf24; }
  .c { background:#ef4444; } .c:hover { background:#f87171; }
</style>
<div class='panel'>
  <div class='title'>Weva editor panel</div>
  <div class='note'>
    This EditorWindow is drawn by Weva: HTML/CSS &rarr; layout &rarr; paint &rarr; GPU, with no
    camera and no URP renderer feature. Hover the cards below &mdash; the colour change proves the
    Event.current &rarr; EventDispatcher input bridge and a live cascade re-render.
  </div>
  <div class='row'>
    <div class='card a'></div>
    <div class='card b'></div>
    <div class='card c'></div>
  </div>
</div>";
    }
}
