# Unity retained visibility and preparation — October 31, 2026

Unity now supports the loading-phase preparation workflow through
`WevaDocument.Visible` and `Prepare()`. The package moves to **2.0.0** under
the repository contract requiring a major bump for supported component API
changes. Existing components remain visible by default; disabling still
releases their native documents.

`Visible=false` removes the document from the render registry and input-feed
registry, pauses automatic updates, and clears focus/pointer gestures. The
document, bindings, form data and renderer resources remain alive. Explicit
`Prepare()` refreshes current controller/binding data and performs CPU layout,
glyph and paint work at zero elapsed time. It leaves the UI hidden, allocates
no new renderer and does not upload/draw GPU data. Showing registers the
retained source again. Focus is restored only by an explicit focus request.

```csharp
ui.Visible = false;
ui.SetController(menuController);
ui.Prepare(); // synchronous work during loading
// Later:
ui.Visible = true;
```

This moves work into loading. It does not eliminate preparation cost, warm
every future glyph or avoid first GPU upload and viewport-dependent work.
There is no Unity timing speedup claim in this receipt; Agentic's earlier
measurements must not be transferred to Unity's different font backend.

## Root cause found during verification

A pending `OnValidate` request survived `OnEnable` even though `Create` had
already loaded the current settings. The first update or explicit preparation
could therefore reload the same tree again. Successful creation now consumes
that pending request. A regression verifies the document generation stays
unchanged through preparation and the first update after enabling. Later
inspector edits still queue their normal reload.

The initial input guard also incorrectly cleared consumption when a callback
reloaded/disabled UI. The full suite caught this: that activation must remain
consumed for its original frame so gameplay does not act on it too. The final
code preserves this behavior while hidden subsequent ticks consume nothing.

## Verification

- Full Unity EditMode: **247 passed, 2 existing environment-gated inconclusive,
  zero failures**. Native subset: **246 passed, 2 inconclusive**. New tests cover
  ordinary/versioned controllers, deferred hidden updates, current preparation,
  document identity, unchanged-paint reuse, focus/input suppression, disable
  cleanup and pending inspector reload consumption.
- Unity URP PlayMode: **7/7 passed**. The new real render sequence verifies
  black while hidden, red after showing, black after preparing a blue change
  while hidden, then blue after showing, without replacing the document.
  The four captures were inspected; final-run PNGs are byte-identical to those
  inspected captures. Existing text, stacking, backdrop and rendering checks
  also pass. No `Assets/*.unity` scene was edited.
- Godot's existing explicit `update_document(0)` already supports hidden
  preparation. Its timing/visibility scene passes **39 checks** on the current
  Windows addon and the qualified patched Linux editor after rebuilding the
  stale Linux addon. No Godot source or core/ABI behavior changed.
- Source whitespace checks pass. No new full cross-platform release gate,
  Unity standalone-player build, long memory soak or GPU timing result is claimed.

Initial failures are preserved: `unity-hidden-editmode.xml` records the input
handoff regression, `unity-hidden-playmode.xml` the redundant initial reload,
and `unity-hidden-editmode-final.xml` a corrected test-harness mistake (Unity
does not deliver SendMessage to inactive GameObjects). The final test invokes
the validation callback directly. `hidden-prepare-godot-timing.log` retains the
stale Linux artifact's five failures; rebuilding from the current checkout
resolves them without code changes.

[Final result counts and artifact hashes](unity-hidden-preparation-20261031.json).
Raw evidence: `.utmp/render-audit-20261030/unity-hidden-editmode-verified.*`,
`unity-hidden-playmode-final.*`, `unity-hidden-renders-final/`,
`hidden-prepare-godot-verified.log`, `hidden-prepare-godot-linux-verified.log`
and `hidden-prepare-godot-linux-rebuild.log`.
