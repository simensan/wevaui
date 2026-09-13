# Troubleshooting

[← Back to index](index.md)

A field guide to the problems you hit first. Start with the blank-screen
checklist; the rest is grouped by symptom.

## Nothing renders (blank screen)

Work down this list — it's ordered by how often each one is the cause:

1. **No `DocumentAsset` assigned** and no `InlineHtml`. Select the
   `WevaDocument`; the inspector warns when the document is empty.
2. **URP renderer feature missing.** `UIBatchedRendererFeature` must be in
   your URP Renderer asset's **Renderer Features** list — the core's draw
   list has no pass to land in without it. The `WevaDocument` inspector shows
   a warning with a one-click **Add URP Renderer Feature + shader includes**
   button. Fix it any of three ways (all idempotent):
   - Menu: `Window > Weva > Setup > Add URP Renderer Feature`.
   - Inspector: click the fix button on the warning.
   - Script / AI agents / CI:
     `Weva.EditorTools.Setup.UrpFeatureSetup.ApplyNonInteractive()`, or
     headless `Unity -batchmode -quit -projectPath <project> -executeMethod
     Weva.EditorTools.Setup.UrpFeatureSetup.ApplyNonInteractive`.
   See [Getting Started → URP render setup](getting-started.md).
3. **The native plugin did not load.** The inspector shows the core's last
   error; the plugin ships for Windows x64 (see the package README's status
   for other platforms).
4. **The GameObject or component is disabled,** or there is no enabled camera
   rendering with the URP renderer that owns the feature.
5. **A root element collapsed to zero height.** A top-level flex/grid container
   with no explicit height and no growing content can compute to height 0.
   Give the page root a height (`html, body { height: 100%; }` or a `100vh`
   wrapper) — open **Window → Weva → Elements** and look at the root box's
   computed height.
6. **CSS never loaded.** See "My styles don't apply" below — an unstyled but
   *present* DOM still renders text top-left; a truly blank screen is usually
   one of 1–5.

## My styles don't apply

- **`<link href>` doesn't resolve.** `href="menu.css"` is looked up **next to
  the document asset** (the same folder, in the editor) — the Console warns
  `linked stylesheet 'menu.css' not found` when it is not there. Keep the
  `.css` beside the `.html`, or assign the sheet in the **Stylesheet Assets**
  inspector list instead of using `<link>`.
- **Player build can't find the sheet.** Linked sheets are baked into the
  component at build time — but a `WevaDocument` on a prefab **instantiated
  at runtime** skips that hook. Assign `StylesheetAssets` explicitly, or call
  `doc.BakeLinkedStylesheets(...)` from a build step. See
  [Getting Started → Player builds](getting-started.md).
- **A property silently does nothing.** Check the
  [Supported CSS](supported-css.md) matrix and its known divergences from
  Chrome.
- **Selector doesn't match a bound boolean.** `bool` interpolation produces
  the literal `true`/`false` (lower case): `[data-critical="true"]`. Prefer
  `data-class-critical="IsCriticalHP"` and style `.critical`.

## `{{ Bindings }}` show literally or never update

- **No controller registered.** Call
  `GetComponent<WevaDocument>().SetController(this)` (typically in `Awake`).
  Without it, `{{ Name }}` has nothing to resolve against and shows nothing.
- **Field isn't `[UIBind]`.** Only `[UIBind]` fields/properties are roots.
  Plain public fields are not scanned.
- **Expression is too complex.** Bindings are plain identifiers or dotted paths
  (plus `$index`) — **no** `!`, operators, or method calls inside `{{ }}`.
  Expose a computed `[UIBind]` property and bind it by name instead:
  `[UIBind] bool QuitDisabled => !CanQuit;`.
- **The controller implements `IBindingVersion` and didn't bump.** A
  versioned controller is read only when `BindingVersion` changes; bump after
  every mutation, or call `doc.RequestRefresh()`.
- **Value mutated but UI didn't update.** A plain controller is read once a
  frame; mutating the field is enough. If the value lives behind a member the
  path does not name (a private list a public property copies), bind the
  member the page reads.

## Clicks / input don't fire

- **`AutoInput` is off,** or the Input System package is missing (the feed
  compiles only with it).
- **`on-click="Method"` names a missing method.** The method must be `public`
  on the registered controller, taking nothing or one `string` (the id).
- **An overlay eats the click.** A full-screen transparent element on top
  intercepts pointer events. `opacity:0` elements still receive events (per the
  web) — use `pointer-events: none` to let clicks pass through.
- **Another document is on top.** A `WevaDocument` with a higher
  `SortingOrder` overlapping the same pixels receives the pointer first.
- **The keyboard is for the game.** `doc.AcceptsKeyboard` gates keys;
  `doc.InputConsumed` says whether the UI took the frame's input.

## Text looks wrong

- **Everything sits a few px lower than Chrome.** That's the intentional
  default-face metric divergence (bundled Inter vs Chrome's Arial), not a bug —
  see [Text & Fonts](text-and-fonts.md). Assign your own face to match a
  target exactly.
- **My font doesn't load.** Assign it on the component (`Font` / `Bold` /
  `Italic`), or declare `@font-face` with a `url()` relative to `BasePath` or
  a `local("Installed Name")`. Missing faces fall back to the default face
  rather than failing invisibly.
- **Arabic letters don't join / a word's letters are reversed.** The core
  reorders bidi runs; glyph shaping on Unity is one glyph per code point, so
  contextual forms are not produced. See "Localization & RTL" in
  [AuthoringGuide §15](AuthoringGuide.md).

## Performance / stutter

- **Per-frame attribute writes.** Don't `SetAttribute` every frame from
  `Update()`. Drive visuals from `[UIBind]` values; an unchanged value changes
  no node.
- **A big controller polled every frame.** Implement `IBindingVersion`.
- **Heavy painters.** `box-shadow`, `filter: blur()`, `backdrop-filter` and
  `text-shadow` are the costliest — keep them off elements that change every
  frame.

## Editor-specific

- **Edit-mode preview differs from play.** Controller-side work only reaches
  the edit-mode document if the controller is also `[ExecuteAlways]` and
  registers in `OnEnable`; gate gameplay work on `Application.isPlaying`.
- **Hot reload didn't pick up my edit.** The edited file must be the
  document's asset, one of its `<link>`ed sheets (next to the asset), or an
  inspector sheet. For markup built in code, call `doc.Reload()`.

## Still stuck?

Open **Window → Weva → Elements** to inspect the live tree, matched rules, and
the computed box model — the same data Chrome's DevTools shows. If a box is
present in the tree but has zero size or the wrong style, the answer is usually
there.

---

Next: [Authoring Guide](AuthoringGuide.md) · [Supported CSS](supported-css.md)
