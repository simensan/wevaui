# Troubleshooting

[← Back to index](index.md)

A field guide to the problems you hit first. Start with the blank-screen
checklist; the rest is grouped by symptom.

## Nothing renders (blank screen)

Work down this list — it's ordered by how often each one is the cause:

1. **No `DocumentAsset` assigned.** Select the `WevaDocument` and confirm an
   `.html` `TextAsset` is in the **Document Asset** field. An empty document
   paints nothing.
2. **URP renderer feature missing.** For the production path you must add
   `UIBatchedRendererFeature` (or `UIRendererFeature`) to your URP Renderer
   asset's **Renderer Features** list. Without it and with
   `RendererBackend = URP`, nothing draws. To check fast, set
   `RendererBackend = IMGUI` (or `Auto`) — if the UI appears, the missing
   feature was the cause. See [Getting Started → URP render setup](getting-started.md).
3. **Zero-size viewport.** If `ViewportOverride` is set to something like
   `(0,0)` *and* no camera / screen size resolves, layout runs against a
   0×0 surface and everything collapses. Leave `ViewportOverride` at `(0,0)`
   to track the screen, or set a real size. Confirm the resolved size in the
   DevTools overlay (F12 → Performance shows the viewport).
4. **The GameObject or component is disabled,** or there is no enabled camera
   rendering with the URP renderer that owns the feature.
5. **A root element collapsed to zero height.** A top-level flex/grid container
   with no explicit height and no growing content can compute to height 0.
   Give the page root a height (`html, body { height: 100%; }` or a `100vh`
   wrapper) — open the **Elements** window (Window → Weva → Elements) and look
   at the root box's computed height.
6. **CSS never loaded.** See "My styles don't apply" below — an unstyled but
   *present* DOM still renders text top-left; a truly blank screen is usually
   one of 1–5.

## My styles don't apply

- **`<link href>` doesn't resolve.** `href="menu.css"` is matched by **file
  name** against imported `TextAsset`s, not a filesystem path. Keep the `.css`
  next to the `.html` under `Assets/`, and make sure it imported as a
  `TextAsset` (it will, for `.css`). You can also assign the sheet directly in
  the **Stylesheet Assets** inspector list instead of using `<link>`.
- **Player build can't find the sheet.** Linked CSS, `@import`, and
  `<template src>` are baked from disk at build time — but a `WevaDocument` on
  a prefab **instantiated at runtime** skips that hook. Assign
  `StylesheetAssets` explicitly, or call `LinkedStylesheetBaker.Bake` from a
  build step. See [Getting Started → Player builds](getting-started.md).
- **A property silently does nothing.** It may be parse-only or partial — check
  the [Supported CSS](supported-css.md) matrix and
  [AuthoringGuide §17](AuthoringGuide.md) (intentionally-not-implemented list).
  Weva fails *loud* on unknown syntax but some spec features parse and no-op by
  design.
- **Selector doesn't match a bound boolean.** `bool` interpolation produces the
  literal `"True"`/`"False"` — an attribute selector must match that casing:
  `[data-critical="True"]`, not `="true"`.

## `{{ Bindings }}` show literally or never update

- **No controller registered.** Call
  `GetComponent<WevaDocument>().SetController(this)` (typically in `Awake`).
  Without it, `{{ Name }}` has nothing to resolve against.
- **Field isn't `[UIBind]`.** Only `[UIBind]` fields/properties are visible to
  templates. Plain public fields are not scanned.
- **Expression is too complex.** Bindings are plain identifiers or dotted paths
  (plus `$index`) — **no** `!`, operators, or method calls inside `{{ }}`.
  Expose a computed `[UIBind]` property and bind it by name instead:
  `[UIBind] bool QuitDisabled => !CanQuit;`.
- **Value mutated but UI didn't repaint.** Bindings are dirty-checked once per
  frame from the lifecycle `Update`; mutating the backing field is enough. If
  you change data the binding doesn't *read* (e.g. an item deep in a list
  without a `data-key`), see [AuthoringGuide §6](AuthoringGuide.md) for the
  programmatic-mutation path.

## Clicks / input don't fire

- **No input controller.** `WevaDocument.OnEnable` auto-attaches
  `Forms.Bridge.UnityInputController` at play time. If you removed it or build
  the document yourself, pointer/keyboard events won't reach the DOM.
- **`on-click="Method"` names a missing method.** The method must be `public`
  on the registered controller. Optionally take one event arg
  (`void OnStart(PointerEvent e)`).
- **An overlay eats the click.** A full-screen transparent element on top
  intercepts pointer events. `opacity:0` elements still receive events (per the
  web) — use `pointer-events: none` to let clicks pass through.
- **EventSystem / other UI on top.** If a uGUI canvas or another `WevaDocument`
  with a higher `SortingOrder` overlaps, it may consume the pointer first.

## Text looks wrong

- **Everything sits a few px lower than Chrome.** That's the intentional
  default-face metric divergence (bundled Inter vs Chrome's Arial), not a bug —
  see [Text & Fonts](text-and-fonts.md). Register your own face to match a
  target exactly.
- **My font doesn't load.** Confirm the drop-in path / `@font-face` / OS-name
  resolution per [Text & Fonts](text-and-fonts.md). Missing faces fall back to
  the default face rather than failing silently-invisible.
- **RTL / Arabic / Hebrew text isn't reordered.** Bidi text reordering is a v1
  non-goal — layout-level `direction: rtl` logical mapping works, but glyph-level
  bidi does not. See "Localization & RTL" in [AuthoringGuide §19](AuthoringGuide.md).

## Performance / stutter

- **Per-frame attribute writes.** Don't `SetAttribute` every frame from
  `Update()`. Drive visuals from `[UIBind]` fields (dirty-checked) and let the
  paint pass short-circuit on idle frames.
- **Heavy painters.** `box-shadow`, `filter: blur()`, and `text-shadow` are the
  costliest — keep them off elements that change every frame.
- **Profile it.** F12 → Performance shows cascade / layout / paint ms, GC/frame,
  and paint-cache hit ratio. `Tools/PerfBench/` baselines on your machine.

## Editor-specific

- **Edit-mode preview is blank but Play works.** Controller-side registrations
  (fonts, image registries) only reach the preview if the controller is also
  `[ExecuteAlways]`. Gate gameplay work on `Application.isPlaying`; keep
  `OnEnable` registrations edit-safe. See
  [Getting Started → Edit-mode preview](getting-started.md).
- **Hot reload didn't pick up my edit.** Confirm `EnableHotReload` is on, and
  that the edited file is one of the document's linked/assigned sheets. For
  programmatically-built UI with no source file, call `doc.Rebuild()`.

## Still stuck?

Open **Window → Weva → Elements** to inspect the live DOM, matched rules, and
the computed box model — the same data Chrome's DevTools shows. If a box is
present in the tree but has zero size or the wrong style, the answer is usually
there.

---

Next: [Authoring Guide](AuthoringGuide.md) · [Supported CSS](supported-css.md)
