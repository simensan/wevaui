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

- Unity **6000.3** or newer
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
     "com.wevaui": "https://github.com/simensan/wevaui.git?path=Packages/com.wevaui"
     ```
     or Package Manager → **+** → "Add package from git URL…" with the same
     URL. That tracks `main` (1.0.0); pin a release with a `#v*` tag suffix
     once one is tagged.
   - **From disk:** clone the repo and add via Package Manager → "Add package
     from disk" → pick `Packages/com.wevaui/package.json`.

2. **Author.** Drop `.html` and `.css` into `Assets/UI/` — they import as
   `TextAsset`s. Or skip straight to the point of Weva: tell your AI model of
   choice *"Create a main menu UI using Weva — standard HTML and CSS"* and use
   what it gives you.
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
   `[UIBind]` fields and properties show through `{{ CoinCount }}`
   placeholders; `on-click="OnStart"` calls the controller's `OnStart()` (or
   `OnStart(string id)`).

4. **Press play.** Hot reload picks up `.html` / `.css` edits without a
   domain reload. **Window → Weva → Elements** shows the live tree, the
   matched rules and the box model of whatever you pick.

The `Phase One Demo` sample (Package Manager → Weva → Samples) is a
complete scene exercising the pipeline end-to-end.

## Supported subset

Everything below is implemented in the core and checked against Chrome (the
sample pages and ~270 harvested cases, layout within 1.5 px; 57 scripted
behaviour checks for what geometry cannot pin). Anything *not* listed fails
loudly rather than silently miscomputing.

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

### Known gaps

What the core does not do yet, with where it shows:

- **Multi-column layout** and **vertical writing modes** — the two sample
  pages the Chrome gate excuses (`Tools/oracle/known-gaps/`).
- **`@property`** (typed custom properties), **View Transitions**, dictionary
  hyphenation (`hyphens: manual` with soft hyphens works).
- **On the Unity host:** the shaper is the package's own, over the font's
  OpenType tables — right-to-left runs come back in visual order with
  mirrored brackets, Arabic joins (`isol`/`init`/`medi`/`fina`, `rlig`,
  `calt`, `liga`, `ccmp`; single, multiple, ligature and contextual lookups
  in every format), cursive scripts join at their anchors (`curs`) and marks
  sit on their base — but Indic syllable reordering is not done, a face
  given as a `Font` asset gets no substitutions (its bytes are not
  readable; a file or `@font-face` is), and
  colour emoji are not rasterised (monochrome symbols are). The native plugin
  ships for Windows x64; other platforms follow their CI jobs.

Prefer flex and grid for new UI where exact browser parity matters; the
per-property record, including what parses but does not render, is
[`Documentation~/supported-css.md`](Documentation~/supported-css.md).

## Architecture

One engine, the C++ core `libweva` (in this repository's `libweva/`), behind
a C ABI (`weva_c.h`); this package is the Unity host over it.

```
+--------------------------------------------------------------+
|  Host (C#)        WevaDocument: assets, [UIBind] controller, |
|                   on-<event> dispatch, Input System feed,    |
|                   FontEngine faces, URP draw-list renderer   |
+--------------------------------------------------------------+
|  C ABI            weva_c.h -- document, input, events,       |
|                   bindings, fonts, inspector (WevaNative.g.cs)|
+--------------------------------------------------------------+
|  Core (C++)       HTML + CSS parsers, cascade (var(), calc(),|
|                   media/container queries, layers, :has()),  |
|                   block / inline / flex / grid / positioned  |
|                   layout, forms and text editing, animation, |
|                   paint to textured triangle lists           |
+--------------------------------------------------------------+
```

The core lays out and paints; the host answers its font callbacks with
`FontEngine`, uploads its draw list to the URP pass, feeds it the Input System
and turns its event queue into C# events. The same core drives the Godot host
in `hosts/godot`, and its layout is checked against headless Chrome, not
against another implementation.

## Performance

The core is measured on the Godot host (`docs/PERFORMANCE.md`,
`docs/RUNTIME_PERFORMANCE.md`: per-API CPU budgets, whole-frame limits at 1080p
and 4K, lifecycle soaks) and by its own `weva_bench`. The Unity host has no
published numbers yet: the pass uploads the core's triangle lists and draws
one mesh per texture run, and a document that has not changed uploads nothing.
Numbers for the Unity host land here when they are measured, not before.

## API surface

The pieces a game dev touches (the supported set, see
[`Documentation~/api-stability.md`](Documentation~/api-stability.md)):

- **`WevaDocument`** (MonoBehaviour). Holds your HTML + stylesheet
  `TextAsset`s and hosts the core. Properties: `DocumentAsset`,
  `StylesheetAssets`, `SortingOrder`, `PrefersDarkColorScheme`, `Font` /
  `Bold` / `Italic` / `Fallbacks`, `BasePath`, `AutoInput`, `AcceptsKeyboard`,
  `WrapTab`, `GamepadTextEntry`, `FollowScreenSafeArea`, `AssetReader`,
  `Cursor`. Methods: `Reload()`, `SetController(...)`, `GetController<T>()`,
  `Bind(model, controller)`, `RequestRefresh()`, `Query(selector)`,
  `QueryAll`, `RegisterFontFamily`, `SetSafeAreaInsets`. Events:
  `ElementClicked`, `HandlerInvoked`, `ValueChanged`, `Changed`,
  `FormSubmitted`, `Focused`, `DataChanged`, `TabbedOut`,
  `TextEntryRequested`, and `Event` for every core event.
- **`[UIBind]`** attribute. Marks a controller field or property as a
  binding root for `{{ path }}`, `data-class-*`, `data-each` and
  `data-model`. Two-way through `data-model`.
- **`IBindingVersion`**. Opt-in: a controller that bumps `BindingVersion`
  is re-read only then, instead of once a frame.
- **Repeat and class bindings.** `<template data-each="Items as item"
  data-key="Id">` clones keyed list rows; `data-class-selected="item.Active"`
  toggles one class without replacing static classes.
- **`UIBatchedRendererFeature`** (`ScriptableRendererFeature`). The URP
  pass that draws every document's draw list into the camera colour target.
- **`WevaElement`** (struct). What `doc.Query(selector)` / `QueryAll` /
  `FocusedElement` return: `Value`, `Attribute` / `SetAttribute`, `HasClass`,
  `Text`, `Bounds`, `Focus()`, `Scroll` / `ScrollTo`, `RowIndex` / `RowKey`,
  `ShowDialog` / `CloseDialog`, `Parent` / `Children`. Stale after `Reload()`
  (`IsValid`), never throws. **`WevaEvent`** is what `doc.Event` raises.

## DevTools

**Window → Weva → Elements** opens the core's Elements panel for a document
in the scene: the tree, the selected element's matched rules (winners first,
losing declarations struck through), computed style and box model -- the
data Chrome's DevTools shows, read through the ABI's inspector surface. The
`WevaDocument` inspector shows the core's HTML diagnostics and last error.

## Examples

- **Phase One Demo** (`Samples~/PhaseOneDemo/`). Hero menu with hot reload,
  controller binding, and component composition. Import via Package Manager
  → Weva → Samples → "Phase One Demo".

## Testing

The core's own suite (`libweva/tests`, ~500,000 checks) and the Chrome
oracle (`Tools/oracle`, every sample page's layout against a headless Chrome
capture) gate the engine. The Unity host has EditMode tests in
`Tests/Editor/Native` -- the document, bindings and controllers, input
through the Input System's test fixture, fonts, the Elements window -- and
PlayMode rendering tests; run them with the Unity Test Runner.

## Status

1.0.0. One engine: the Weva core (`libweva/` in the repository, C++ behind a C
ABI, checked against Chrome) hosted by this package through the native plugin
(Windows x64 now; other platforms as their CI jobs go green). The C# engine
that 0.1.x shipped was frozen on 2026-09-13 and deleted the same day; the
CHANGELOG has the migration notes. The controller model (`[UIBind]`,
`on-<event>`, `SetController`) carried over unchanged; the C# DOM did not.

## License

MIT — see [`LICENSE.md`](LICENSE.md).

The native plugin (`weva_core`) statically links ICU, the generated Unicode
tables, Blink's WTF Decimal and the ada URL parser; their licences ship with
the package in [`Third Party Notices.md`](Third%20Party%20Notices.md). The
bundled fonts each carry their own OFL text beside the `.ttf`.
