# Weva Editor Panels

Render custom Unity **EditorWindows** with Weva (HTML/CSS), the way UI Toolkit
lets you author editor UI — but with the full CSS box/paint model and Weva's
event pipeline.

> **Status: experimental / unverified.** The host renders and dispatches input in
> code, but the render output has **not yet been visually confirmed in a live
> editor**. See [Validation checklist](#validation-checklist) before relying on
> it. Live behind `WEVA_URP` (URP ≥ 17 present).

---

## Why this works without a render pipeline

A common worry: "Weva renders through URP — how can it draw in an editor panel?"
It can, because **editor windows are not drawn by any render pipeline.** URP/HDRP
only render *cameras* (the Game view, the Scene view camera). An `EditorWindow`
is drawn by Unity's legacy immediate repaint loop — render-pipeline-agnostic.
This is the same reason UI Toolkit's own renderer works in the editor.

So the panel renders Weva's batched mesh into an offscreen `RenderTexture` with a
one-off `CommandBuffer` + `Graphics.ExecuteCommandBuffer` (no camera, no
`ScriptableRendererFeature`, no scene), then blits that texture into the window.

```
HTML/CSS
  → UIDocumentBuilder (headless: parse → cascade → layout → paint wiring)
  → UIDocumentLifecycle.Update  (per repaint: cascade/layout/paint)
  → BatchedURPRenderBackend     (record PaintList into GPU batches)
  → BatchedSurfaceRenderer      (CommandBuffer → RenderTexture, no pipeline)
  → EditorGUI.DrawPreviewTexture (blit into the EditorWindow)
```

Input runs the other direction: `Event.current` (the window's IMGUI events) →
`EventDispatcher`, the same dispatcher the runtime uses. Because the dispatcher
shares the cascade's `InteractionStateProvider`, `:hover` / `:active` / `:focus`
and click handlers re-render correctly.

---

## Authoring a panel

Subclass `WevaEditorPanel` and override `Html`:

```csharp
using UnityEditor;
using Weva.EditorTools.Panels;

public sealed class MyPanel : WevaEditorPanel {
    [MenuItem("Window/My Tools/My Panel")]
    static void Open() => GetWindow<MyPanel>("My Panel");

    protected override string Html =>
@"<style>
  .panel { padding:16px; font-family:sans-serif; color:#eee; }
  .btn   { background:#3b82f6; color:#fff; padding:8px 14px; border-radius:6px; }
  .btn:hover { background:#60a5fa; }
</style>
<div class='panel'>
  <div class='btn'>Hover me</div>
</div>";
}
```

`Html` is re-read every repaint, so returning a string that depends on state
gives you a reactive panel. Style with inline `style=`, a `<style>` block, or a
`<link>` to a `.css` file (same as the runtime).

### Binding to editor state

Override `Controller` to bind `{{ path }}` text/attribute bindings and `on-*`
event handlers against a plain object. The controller's members are re-evaluated
every frame by `UIDocumentLifecycle.Update`, so live getters reflect editor
state. Trigger `Repaint()` when external state changes so the panel updates
promptly.

A worked example bound to the current `Selection` (`{{ Count }}` /
`{{ ActiveName }}` bindings against a plain controller whose getters read
`Selection` live, plus a `selectionChanged → Repaint()` hook) lives in the
engine repository's dev project (`Assets/Editor/WevaSelectionPanel.cs`),
together with a static `:hover`-cards smoke-test panel
(`Assets/Editor/WevaPanelDemoWindow.cs`). Neither ships with the package —
they are ~60-line subclasses you can recreate from the snippets above.

---

## Validation checklist

These need a real editor render to confirm (the host is written but unproven).
Open a minimal `WevaEditorPanel` subclass (e.g. the demo panel from the
engine repository's dev project) and check, in order:

1. **It draws at all.** If the panel is blank, check the Console for
   *"Hidden/Weva/Quad shader not found"* — add it to **Project Settings ▸
   Graphics ▸ Always Included Shaders**.
2. **Color space.** The `RenderTexture` is allocated linear (`sRGB:false`). If
   colors look washed out / too dark, `EditorGUI.DrawPreviewTexture` may need a
   gamma RT or a manual convert. (Compare against the same HTML in the existing
   `Window ▸ Weva ▸ Preview` CPU window, which is known-correct.)
3. **Orientation.** The Quad shader flips Y to CSS top-left. If the content is
   upside-down, flip the draw rect (negative height) or blit through an
   intermediate. This is a known editor-vs-RT Y-axis trap.
4. **DPI / crispness.** v1 builds the document in GUI-point units so hit-testing
   matches mouse coords; on a retina/HiDPI display the RT is point-sized and
   upscaled, so it will look soft. For crisp output, size the RT in physical
   pixels (`× EditorGUIUtility.pixelsPerPoint`) and scale pointer coords to match.
5. **Interactivity.** Hover the cards — they should change color (proves
   `Event.current → EventDispatcher → cascade → re-render`).

---

## Known limitations (v1)

- Visual output unverified (above).
- Document is **rebuilt** on resize/skin/source change rather than relayed-out in
  place (cross-assembly access to the internal viewport setter is needed for the
  fluid path). Fine for small panels; revisit if a large panel janks on drag.
- The batched backend is allocated per frame (mirrors the golden runner); pool it
  if profiling shows pressure.
- Key/`code` mapping is best-effort (covers Tab/nav/editing keys + typed chars),
  not the full DOM `KeyboardEvent` tables.
- `[CustomEditor]` inspectors (vs. standalone windows) are not wired yet.
