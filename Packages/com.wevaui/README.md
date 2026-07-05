# Weva

HTML and CSS for Unity. AI-friendly UI layer that produces working Unity UI from
HTML/CSS that AI models have already learned from the web.

## Why

UI Toolkit's USS / UXML differ from web HTML/CSS just enough that LLM-generated
UI almost-works but breaks subtly. Weva commits to the actual web subset so
the same HTML and CSS that runs in a browser runs in your game. No
`-unity-font-definition`, no UXML attribute renames, no quietly-different inline
flow.

The design rule is loud and simple: **if a feature has a well-known web
behavior, our behavior matches it — or we don't ship it.** A subtly different
behavior is worse than none, because the model produces code that *looks* right
and fails in surprising ways.

## Requirements

- Unity **6000.4**
- Universal RP **17.0.0** and Input System **1.7.0** (pulled in automatically as
  package dependencies)

## Quick start

1. **Install.** Pick whichever fits how you received the package:

   - **From a tarball** (`com.wevaui-<version>.tgz`): Package Manager → **+** →
     "Install package from tarball…" → select the `.tgz`. Unity copies it under
     `Packages/`. (The tarball ships Runtime + Editor + the Phase One Demo
     sample; the NUnit test suite is omitted.)
   - **From Git** (recommended): add to `Packages/manifest.json`:
     ```json
     "com.wevaui": "https://github.com/simensan/wevaui.git?path=Packages/com.wevaui#v0.1.0"
     ```
     or Package Manager → **+** → "Add package from git URL…" with the same
     URL. Pin a release with the `#v*` tag suffix, or drop it to track `main`.
   - **From disk:** clone the repo and add via Package Manager → "Add package
     from disk" → pick `Packages/com.wevaui/package.json`.

2. **Author.** Drop `.html` and `.css` into `Assets/UI/` — they import as
   `TextAsset`s.
   ```html
   <link rel="stylesheet" href="menu.css" />
   <main class="menu">
     <h1>My Game</h1>
     <button id="start" on-click="OnStart">Start</button>
   </main>
   ```
   ```css
   .menu { display: flex; flex-direction: column; gap: 16px; padding: 24px; }
   button { padding: 8px 16px; border-radius: 8px; background: #4f46e5; color: white; }
   button:hover { background: #6366f1; }
   ```

3. **Mount.** GameObject → Weva → New WevaDocument. Drop your HTML / CSS
   `TextAsset`s into the inspector slots. Attach a controller script:
   ```csharp
   public class MainMenu : MonoBehaviour {
       [UIBind] public int CoinCount;
       public void OnStart() => SceneManager.LoadScene("Game");
   }
   ```
   `[UIBind]` properties show through `{{ CoinCount }}` placeholders;
   `on-click="OnStart"` resolves against the controller via the
   source-generator-friendly `BindingScanner`.

4. **Press play.** Hot reload picks up `.html` / `.css` edits without a
   domain reload. Press `F12` for the in-game DevTools overlay (box outlines,
   dirty highlighter, perf readout).

The `Phase One Demo` sample (Package Manager → Weva → Samples) is a
complete scene exercising the pipeline end-to-end.

## Supported subset

Everything below is implemented and tested. Anything *not* listed fails loudly
rather than silently miscomputing.

### HTML elements

| Category    | Elements                                                                  |
|-------------|---------------------------------------------------------------------------|
| Structural  | `div`, `section`, `header`, `footer`, `nav`, `main`, `article`, `aside`   |
| Text        | `p`, `span`, `h1`–`h6`, `strong`, `em`, `b`, `i`, `u`, `code`, `small`, `br`, `hr` |
| Lists       | `ul`, `ol`, `li`                                                          |
| Form        | `button`, `input` (text/password/email/search/tel/url/number/checkbox/radio/range/hidden), `select`, `option`, `textarea`, `label`, `form` |
| Media       | `img`                                                                     |
| Composition | `template`, `slot`, `<template src="...">` imports                         |

### CSS

- **Layout:** `display` (block / inline / inline-block / flex / inline-flex /
  grid / inline-grid / contents / none), full flexbox, full grid (with
  `repeat()` / `minmax()` / `fr` / `auto-fill` / `auto-fit`), `position`
  (static / relative / absolute / fixed / sticky), logical sizes/insets,
  horizontal `direction: rtl`, `z-index`, stacking contexts.
- **Box model:** all longhands + shorthands. Default `box-sizing: content-box`
  per the CSS spec. Includes logical margin/padding/border edges and logical
  corner radii.
- **Typography:** `font-family`, `font-size`, `font-weight`, `font-style`,
  `line-height`, `letter-spacing`, `text-align`, `text-align-last`,
  `text-indent`, `text-decoration`, `text-transform`, `text-overflow:
  ellipsis`, `white-space`, `text-wrap: nowrap`, `tab-size`,
  `word-break: break-all`, `overflow-wrap`, manual `hyphens`. Real inline
  formatting context with mixed-style runs.
- **Effects:** `opacity`, `box-shadow` (with spread + inset), `transform`
  (translate / scale / rotate / skew / matrix), `filter` (blur / brightness /
  contrast / grayscale / opacity / saturate / hue-rotate / invert / sepia /
  drop-shadow), `backdrop-filter`, `clip-path` basic shapes, layered gradient
  masks, `border-radius` (per-corner).
- **Backgrounds:** color, `linear-gradient`, `radial-gradient`,
  `background-size` / `position` / `repeat` / `clip`.
- **Custom properties + functions:** `--name` + `var(--name, fallback)`,
  `calc()`, `min()`, `max()`, `clamp()`. Color: `rgb`, `rgba`, `hsl`, `hsla`,
  `#hex`, named colors, `currentColor`.
- **Selectors:** `*`, tag, `.class`, `#id`, all attribute operators (`[a]`,
  `[a=v]`, `~=`, `^=`, `$=`, `*=`), all combinators (` `, `>`, `+`, `~`),
  structural pseudos (`:first-child`, `:last-child`, `:only-child`,
  `:nth-child(an+b)`, `:nth-of-type`, `:empty`, `:not`, `:is`, `:where`,
  `:has`, `:lang`, `:dir`), state pseudos (`:hover`, `:focus`,
  `:focus-visible`, `:focus-within`, `:active`, `:link`, `:visited`,
  `:any-link`, `:target`, `:scope`, `:disabled`, `:enabled`, `:checked`, `:default`,
  `:required`, `:optional`, `:valid`, `:invalid`, `:user-valid`,
  `:user-invalid`, `:in-range`, `:out-of-range`,
  `:read-only`, `:read-write`, `:placeholder-shown`, `:root`), pseudo-elements
  (`::before` / `:before`, `::after` / `:after`, `::placeholder`,
  `::selection`, `::backdrop`, `::marker`).
- **At-rules:** `@import`, `@font-face`, `@media` (full feature set: width /
  height / orientation / aspect-ratio / resolution / prefers-color-scheme /
  prefers-reduced-motion / hover / pointer + `and`/`or`/`not`), `@container`
  (`container-type: inline-size | size`, named/unnamed), `@keyframes`,
  `@supports`, `@scope` (CSS Cascade Level 6).
- **Animation:** `transition` (full property surface, all easing functions
  including `cubic-bezier()` and `steps()`), `@keyframes`, `animation-*`.
  Type-aware interpolation (length / color / number / percentage / transform
  / discrete); color animates in OKLab (gradient stops lerp in linear-RGB).
- **Cascade:** Specificity, `!important`, `inherit` / `initial` / `unset`,
  `var()` resolution with cycle detection, cascade layers (`@layer`),
  nested rules (`& > .child`).

For the full supported / partial / parse-only / missing CSS matrix, see
[`CSS_FEATURES.md`](CSS_FEATURES.md).

### Deliberately out of v1

Multi-column, vertical writing-mode text layout, dictionary hyphenation, full
multi-layer masking/compositing, full bidi/complex text shaping, and typed
custom properties via `@property`. Logical axes/RTL remapping, floats, tables,
basic clip paths, masks, and backdrop filters exist as partial engine support;
prefer flex/grid for new UI where exact browser parity matters.

### v1 simplifications worth knowing

A short list of where we differ from the spec by design or by phase scoping.
These are the known v1 simplifications:

- `position: sticky` is single-axis (top OR bottom — top wins on conflict).
  No `scroll-snap-type`, no smooth scrolling, no overscroll-behavior.
- `position: absolute` containing block is the nearest positioned ancestor's
  *border*-box, not its padding-box.
- `min-content` / `max-content` keywords are treated as `auto` in flex.
- `revert` keyword is treated as `initial`.

## Architecture

```
+--------------------------------------------------------------+
|  Authoring        .html + .css TextAssets, hot-reload watcher|
+--------------------------------------------------------------+
|  Parser           Hand-rolled HTML + CSS parsers             |
+--------------------------------------------------------------+
|  Document model   Node, Element, TextNode, Document          |
+--------------------------------------------------------------+
|  Style engine     Selector matcher, cascade, var(), calc(),  |
|                   media queries, container queries, @scope,  |
|                   cascade layers, nested rules, :has()       |
+--------------------------------------------------------------+
|  Layout engine    Block, inline, flex (own impl), grid,      |
|                   positioned, sticky, scroll containers      |
+--------------------------------------------------------------+
|  Paint            Box → PaintCommand list, per-box paint     |
|                   cache (box-local coords)                   |
+--------------------------------------------------------------+
|  Render backend   IMGUI (debug) or URP (production) — same   |
|                   IRenderBackend interface                   |
+--------------------------------------------------------------+
|  Reactivity       InvalidationTracker drives incremental     |
|                   cascade / layout / paint per frame         |
+--------------------------------------------------------------+
```

Five invalidation kinds — `Structure`, `Style`, `Layout`, `Paint`,
`Composite` — propagate per the engine's invalidation rules. Each pipeline stage
caches outputs keyed on input versions; clean subtrees re-use their cached
boxes / paint commands.

## Performance

Numbers from `Tools/PerfBench/` against the v0.7 dev-machine baseline
(Apple M1-tier laptop, single fresh `dotnet run -c Release -- all`). Note:
`v0.x` labels in this section refer to internal development milestones, not
the package version (currently 0.1.0):

| Bench | Scale | Median ms | p95 ms |
|---|---|---:|---:|
| Cascade.ComputeAll | 1001 elements (forms) | 8.3 | 29.1 |
| Cascade.ComputeAll | 1001 elements (deep nested) | 9.3 | 12.2 |
| Cascade.IncrementalApply (attribute change) | 1001 elements | 0.21 | 0.28 |
| Cascade.IncrementalApply (`:hover` flip) | 1001 elements | 0.08 | 0.13 |
| Layout.LayoutAll | 1001 elements (forms) | 10.8 | 12.8 |
| Layout.LayoutAll | 1001 elements (deep) | 4.4 | 5.5 |
| Paint.Convert | 500 boxes | 0.85 | 1.18 |
| Paint.Convert | 1000 boxes | 0.99 | 1.38 |
| Paint.Convert (gradient-heavy) | 500 boxes | 1.27 | 1.65 |
| Paint.Convert (shadow-heavy) | 500 boxes | 2.36 | 3.25 |
| EndToEnd.HoverToggle | 1000 elements | 2.7 | 6.9 |

Per-element-state-digest `:hover` flips dropped from 7.5 ms (v0.4) to
0.083 ms (v0.5+) — a 90× win — once the cascade keyed cache misses on the
specific element whose state flipped instead of invalidating globally.

Steady-state allocations: paint converter ~1.1 MB/call at 500 boxes (target
0); layout ~1.4 MB/call at 1000 elements (target 50 KB). Both above the
target but already well inside frame budgets at typical UI sizes; the gap
is on the v0.8+ roadmap.

## API surface

The pieces a game dev typically touches:

- **`WevaDocument`** (MonoBehaviour). Holds your HTML + stylesheet
  `TextAsset`s and runs the pipeline. Properties: `DocumentAsset`,
  `StylesheetAssets`, `RendererBackend` (Auto / IMGUI / URP),
  `EnableHotReload`, `ViewportOverride`. Methods: `Rebuild()`,
  `SetController(...)`, `GetElementById(...)`, `GetElementsByClassName(...)`,
  `MarkStyleDirty(...)`, `MarkLayoutDirty(...)`.
- **`[UIBind]`** attribute. Marks a controller field/property for
  `{{ data binding }}`. Two-way for `<input>`-bound fields.
- **`[UIElement("id")]`** attribute. Captures an `Element` reference into a
  field at build time.
- **`BindingScanner`** + **`BindingSet`**. Reflection-driven binding
  resolution today; source-generator-friendly so IL2CPP players are
  supported.
- **Repeat and class bindings.** `<template data-each="Items as item"
  data-key="Id">` clones keyed list rows; `data-class-selected="item.Active"`
  toggles one class without replacing static classes.
- **`IMGUIDocumentRenderer`** (MonoBehaviour). Debug-grade IMGUI backend.
  Auto-attached when not on URP; suppress by setting
  `RendererBackend = URP`.
- **`UIRendererFeature`** (`ScriptableRendererFeature`). The URP path —
  injects after `RenderPassEvent.AfterRendering` and renders
  through seven dedicated shaders (incl. `Weva_StencilWrite` for clipping).
- **`DevToolsOverlay`** (MonoBehaviour). F12 toggle, three composable modes
  (Outlines / DirtyTracking / Performance); hover inspection is always on
  when the overlay is enabled. Lives outside the main paint pipeline so it
  can't accidentally break it.
- **`IRenderBackend`**. Implement to plug in a custom renderer. The
  shipped `RecordingBackend` and `NullBackend` are useful for tests.

## DevTools

Press `F12` in play mode (configurable via `DevToolsOverlay.ToggleKey`). The
overlay renders via IMGUI, never via the main paint pipeline:

- **Outlines.** Margin (orange) / border (yellow) / padding (green) /
  content (blue), Chrome DevTools palette.
- **Dirty highlighter.** Red flash when a box re-laid this frame, yellow
  when style changed, gray when paint-only. Decays over 3 frames.
- **Hover inspector.** `<button.btn-primary#start>` style header,
  computed dimensions (W×H px @ X,Y), 10 most relevant computed style
  properties.
- **Performance corner.** FPS, frame ms, cascade / layout / paint ms
  breakdown, GC bytes/frame, paint cache hit ratio.

There's also a Window → Weva → DevTools editor window for inspection
without entering Play Mode.

## Examples

- **Phase One Demo** (`Samples~/PhaseOneDemo/`). Hero menu with hot reload,
  controller binding, and component composition. Import via Package Manager
  → Weva → Samples → "Phase One Demo".

## Testing

~9,800 NUnit tests run headlessly via `Tools/TestVerifyAll/` (or the Unity Test
Runner once the package is added to a project). Coverage spans HTML parsing,
CSS rule and selector parsing, cascade, layout (block / inline / flex /
grid / positioning), paint conversion, animation, components, bindings,
events, reactivity propagation, and golden-image rasterization.

## Status

0.1.0 (preview). Headless layers (parser → cascade → layout → paint) are
production-quality; the URP renderer feature, IMGUI fallback, TextCore
bootstrap, hot reload watcher, and DevTools overlay are all wired up but
need real-Unity validation gating before v1.0.

## License

MIT — see [`LICENSE.md`](LICENSE.md).
