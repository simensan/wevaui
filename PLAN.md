# Weva — HTML/CSS UI Layer for Unity

A new UI layer for Unity that is *more* faithful to HTML and CSS than UI Toolkit (UI Elements / UXML / USS). The goal is that AI models — and humans who already know the web platform — can produce working Unity UI by writing standard HTML and CSS, with as little Unity-specific knowledge as possible.

This is a planning document. Nothing is built yet.

---

## 1. Goals and non-goals

### Goals

1. **Standard HTML/CSS subset.** A model that has only ever seen web HTML/CSS should produce working layouts on the first try, ~90% of the time, for a defined subset of features.
2. **No Unity-specific renames.** No `-unity-font`, no UXML-only attributes. Property names match CSS specs exactly. Element names match HTML.
3. **Cascade and inheritance that match the spec.** Real selector matching, real specificity, real `inherit`/`initial`/`unset`, real `var()`.
4. **Inline + block + flex + grid all work.** UI Toolkit only does block + flex + (limited) grid. We need a real inline formatting context so prose, mixed `<span>`/`<strong>`/`<a>`, and wrapping text-with-icons render correctly.
5. **Hot reload.** Edit `.html` / `.css` and see the change in Play Mode without recompile.
6. **Editor-time + runtime parity.** The same documents render in the Editor (for inspectors and editor windows) and at runtime (for game UI).
7. **Performant enough for game UI.** Target: 1000 styled elements at 144 Hz on a mid-tier laptop with no GC churn in steady state.

### Non-goals (at least for v1)

- A full browser. No JavaScript engine. No DOM events from script — events are wired up via attributes that bind to C# handlers, or via C# directly.
- Networking, fetch, history, navigation, cookies, storage.
- Print media, speech media, paged media.
- SVG (consider for v2 — Unity's Vector Graphics package is a candidate backend).
- Full print-grade table behavior. Runtime tables exist, but collapsed border
  conflict painting and advanced fragmentation remain scoped out for v1.
- Floats (`float: left/right`). Use flex or grid.
- Vertical writing-mode text layout. Horizontal `direction: rtl` and logical-property remapping exist, but glyph flow remains horizontal in v1.
- Full text shaping (bidi reordering, complex scripts). Latin + simple scripts only in v1; revisit with HarfBuzz/TextCore later.
- Accessibility tree / screen reader integration in v1 (architect for it; don't build it).

### Explicit non-goal: do not be UI Toolkit

We are not extending UI Toolkit. We do not use `.uxml` or `.uss`. Files are `.html` and `.css`. The mental model for the user is "the web platform, minus the parts that don't fit a game engine."

---

## 2. Why "AI-friendly" is a real design constraint

When AI generates UI code, three things go wrong with existing Unity solutions:

1. **Renamed properties.** Models write `font-family` and `color`; UI Toolkit wants `-unity-font-definition`. The model hallucinates the property because the renamed form is rare in training data.
2. **Missing primitives.** Models reach for `position: fixed`, `box-shadow`, `::before`, `:nth-child(odd)`, `transform: rotate(...)` — these either don't exist in USS, work differently, or are partial.
3. **Inline layout.** Models write `<p>Click <a href="#">here</a> to start</p>` and expect text to flow. UI Toolkit treats every element as a flex/block box, so the link becomes a separate block.

The design rule for every feature: **if a feature has a well-known web behavior, our behavior must match it, or we must not implement it at all.** A subtly-different behavior is worse than no behavior, because the model will produce code that *looks* right and fails in surprising ways.

We will publish a single-page **conformance reference** — every supported property, every selector, every element. The reference is short on purpose. Anything not in the reference is unsupported and will fail loudly.

---

## 3. The HTML subset

### Elements (v1)

| Category    | Elements                                                                  |
|-------------|---------------------------------------------------------------------------|
| Structural  | `div`, `section`, `header`, `footer`, `nav`, `main`, `article`, `aside`   |
| Text        | `p`, `span`, `h1`–`h6`, `strong`, `em`, `b`, `i`, `u`, `code`, `small`, `br`, `hr` |
| Inline link | `a`                                                                       |
| Lists       | `ul`, `ol`, `li`                                                          |
| Form        | `button`, `input` (`text`/`password`/`number`/`checkbox`/`radio`/`range`/`hidden`), `select`, `option`, `textarea`, `label`, `form` (group only, no submit semantics) |
| Media       | `img`                                                                     |
| Generic     | `template`, `slot` (for component composition — see §6)                   |

### Attributes (v1)

- Universal: `id`, `class`, `style`, `hidden`, `data-*`, `aria-*` (parsed and stored even if not yet used).
- Form: `name`, `value`, `placeholder`, `disabled`, `checked`, `min`, `max`, `step`, `required`, `readonly`.
- Link: `href` — fires a C# event; no actual navigation.
- Image: `src`, `alt`, `width`, `height`.
- Event hooks: `on-click`, `on-change`, `on-input`, `on-submit`, `on-focus`, `on-blur` — bind to C# methods (see §6).

### Deliberately omitted from v1

`iframe`, `script`, `style` (inline `<style>` blocks come in v2), `canvas`, `svg`, `audio`, `video`. Tables, `details`/`summary`, and `dialog` are implemented with the limitations documented in the conformance reference.

---

## 4. The CSS subset

Property names and values match the CSS spec. No prefixes.

### Layout

- `display`: `block`, `inline`, `inline-block`, `flex`, `inline-flex`, `grid`, `inline-grid`, `none`, `contents`.
- `position`: `static`, `relative`, `absolute`, `fixed`, `sticky`. `top`/`right`/`bottom`/`left`. `z-index`.
- Logical axes: `direction`, `writing-mode`, logical sizes, logical insets, logical margin/padding/border properties. v1 remaps them to physical edges; vertical text shaping is not implemented.
- `overflow`, `overflow-x`, `overflow-y`: `visible`, `hidden`, `scroll`, `auto`, `clip`.
- Flexbox: full set — `flex`, `flex-direction`, `flex-wrap`, `flex-basis`, `flex-grow`, `flex-shrink`, `justify-content`, `align-items`, `align-self`, `align-content`, `gap`/`row-gap`/`column-gap`, `order`.
- Grid: `grid-template-columns`, `grid-template-rows`, `grid-template-areas`, `grid-column`, `grid-row`, `grid-auto-flow`, `grid-auto-columns`, `grid-auto-rows`, `place-items`, `place-content`, `place-self`, plus `gap`. Includes `repeat()`, `minmax()`, `fr` units, `auto-fill`, `auto-fit`.

### Box model

- `width`, `height`, `min-width`, `min-height`, `max-width`, `max-height`.
- `inline-size`, `block-size`, min/max logical sizes.
- `padding`, `margin`, `border` (physical and logical longhands + shorthands).
- `box-sizing`: `content-box`, `border-box`. **Default: `content-box`**, matching the CSS initial value. Authors can opt into the common reset with `* { box-sizing: border-box }`.
- `border-radius` (per-corner).

### Typography

- `font-family`, `font-size`, `font-weight`, `font-style`, `font-variant` (small-caps only).
- `line-height`, `letter-spacing`, `word-spacing`.
- `text-align`, `text-decoration` (underline/line-through), `text-transform`, `text-overflow` (ellipsis), `white-space` (`normal`, `nowrap`, `pre`, `pre-wrap`).
- `color`.

### Backgrounds and borders

- `background-color`.
- `background-image`: `url(...)`, `linear-gradient(...)`, `radial-gradient(...)`.
- `background-size`, `background-position`, `background-repeat`, `background-clip`.
- `border-*` longhands and shorthand. `border-style`: `solid`, `dashed`, `dotted`, `none` (others map to `solid` with a warning).

### Effects

- `opacity`.
- `box-shadow` (with spread, inset).
- `transform`: `translate(x,y)`, `translateX/Y`, `scale(s)`, `scale(sx,sy)`, `rotate(deg)`, `skew(...)`, `matrix(...)`. `transform-origin`.
- `filter`: `blur()`, `brightness()`, `contrast()`, `grayscale()`, `opacity()`. (Implementation: shader pass on the offscreen surface.)

### Animation

- `transition`, `transition-property`, `transition-duration`, `transition-timing-function`, `transition-delay`. Easing: `linear`, `ease`, `ease-in`, `ease-out`, `ease-in-out`, `cubic-bezier()`, `steps()`.
- `@keyframes`, `animation`, `animation-*`.

### Custom properties and functional notation

- `--name: value;` and `var(--name, fallback)`.
- `calc()`, `min()`, `max()`, `clamp()` — full arithmetic on lengths/percentages/numbers.
- Color functions: `rgb()`, `rgba()`, `hsl()`, `hsla()`, `#hex`, named colors, `currentColor`.

### Selectors

| Type                | Examples                                                |
|---------------------|---------------------------------------------------------|
| Simple              | `*`, `tag`, `.class`, `#id`                             |
| Attribute           | `[name]`, `[name=val]`, `[name~=val]`, `[name^=val]`, `[name$=val]`, `[name*=val]` |
| Combinators         | descendant ` `, child `>`, adjacent `+`, general sibling `~` |
| Structural pseudo   | `:first-child`, `:last-child`, `:only-child`, `:nth-child(n)`, `:nth-of-type(n)`, `:empty`, `:not(...)`, `:is(...)`, `:where(...)`, `:has(...)`, `:lang(...)`, `:dir(...)` |
| State/scoping pseudo | `:hover`, `:focus`, `:focus-visible`, `:focus-within`, `:active`, `:link`, `:visited`, `:any-link`, `:target`, `:scope`, `:disabled`, `:enabled`, `:checked`, `:default`, `:required`, `:optional`, `:valid`, `:invalid`, `:user-valid`, `:user-invalid`, `:in-range`, `:out-of-range`, `:read-only`, `:read-write`, `:placeholder-shown`, `:root` |
| Pseudo-elements     | `::before`, `::after`, `::placeholder`, `::selection`, `::backdrop`, `::marker` |

Specificity follows the spec. `!important` is supported.

### At-rules

- `@import "path.css"` (relative paths and Unity asset paths).
- `@font-face`.
- `@media (min-width: ...)`, `(max-width: ...)`, `(prefers-color-scheme)`, `(orientation)`, `(hover)`. Resolves against the *containing UI surface*, not the OS window — important for split-screen and embedded UI.
- `@container [name?] (...)` — component-level responsiveness. Authors declare a containment context with `container-type: inline-size | size` (and optionally `container-name`); descendant `@container` queries evaluate `(min-width: ...)`, `(max-width: ...)`, `(width: ...)`, `(min-height: ...)`, `(orientation: ...)`, `(aspect-ratio: ...)` against the nearest matching ancestor's box size. v1 chicken-and-egg: the resolved size is the layout-after-previous-cascade size, so a newly-applied `container-type` may take 1-2 frames to settle.
- `@keyframes`.
- `@supports` (always reports our actual support).

### Deliberately omitted

`columns`/multi-column, vertical writing-mode text layout, full bidi/complex text shaping, full CSS masking and `clip-path`, CSS Houdini typed properties, counters, and full generated content. Logical axes/RTL remapping, floats, tables, `:has()`, `@container`, `@scope`, `@supports`, and `backdrop-filter` are now implemented with the limitations tracked in `CSS_FEATURE_AUDIT.md`.

---

## 5. Architecture options

The biggest technical decision: **what do we render onto?** Three viable backends.

### Option A: Build on top of UI Toolkit's low-level VisualElement

**What:** Use `VisualElement` as a render leaf. Skip USS entirely. We provide our own parser, cascade, and layout. We push geometry into UI Toolkit's mesh API.

- **Pro:** Existing batched mesh pipeline, font rendering (TextCore), input dispatch, editor integration all free. Works in editor windows.
- **Pro:** Yoga is already there for flex layout (we still need our own inline + grid impl, since UIT's grid is incomplete).
- **Con:** We inherit UI Toolkit's quirks at the boundary (event model, focus model). The "we are not UI Toolkit" promise leaks if these surface.
- **Con:** UI Toolkit's text element does not support mixed-style inline runs; we'd bypass it and drive TextCore directly.

### Option B: Build on top of UGUI (Canvas / CanvasRenderer)

**What:** Each rendered box is a `CanvasRenderer` with custom mesh. Layout, cascade, parser are all ours. Text uses TextMeshPro.

- **Pro:** Full control. UGUI's batching is well understood. TMP gives us solid text.
- **Pro:** No editor-window story for free, but no inherited model to fight either.
- **Con:** UGUI is in maintenance mode. Performance ceiling is lower than UI Toolkit's mesh pipeline at high element counts.
- **Con:** No editor-window support without a separate code path.

### Option C: Custom renderer (CommandBuffer + Mesh API, render to RenderTexture)

**What:** We own everything. Render the UI tree to a `RenderTexture` via a `CommandBuffer`; show that on a `RawImage` (runtime) or a `VisualElement` with an image background (editor).

- **Pro:** Total control over ordering, clipping, effects (`filter:`, `backdrop-filter:`, `mix-blend-mode:`).
- **Pro:** Same code path runtime + editor.
- **Con:** Most work. We rebuild input dispatch, focus, IME, accessibility from zero.
- **Con:** Text shaping is ours to solve.

### Decision: Option C

**Option C, locked.** We own parser, DOM, cascade, layout, paint, render backend, input dispatch, focus, and IME (desktop only — console deferred).

At runtime we inject a `ScriptableRenderPass` into the URP camera stack and issue our `CommandBuffer` directly against the camera color target — zero intermediate blit. In the editor we render to a `RenderTexture` and display it in an `EditorWindow` via `Graphics.DrawTexture`. Both paths share DOM, cascade, layout, and paint; only the render backend differs.

### Layered architecture

```
+--------------------------------------------------------------+
|  Authoring        .html + .css files, hot-reload watcher     |
+--------------------------------------------------------------+
|  Parser           HTML parser  |  CSS parser (tokenizer +    |
|                                |  property grammar)          |
+--------------------------------------------------------------+
|  Document model   Nodes, attributes, text runs, stylesheet   |
|                   model, computed styles                     |
+--------------------------------------------------------------+
|  Style engine     Selector matcher, cascade, inheritance,    |
|                   var() resolution, calc(), media queries    |
+--------------------------------------------------------------+
|  Layout engine    Block + inline + flex + grid + positioned. |
|                   Yoga for flex; our own for inline/grid.    |
+--------------------------------------------------------------+
|  Paint            Box decoration, text shaping, effects      |
+--------------------------------------------------------------+
|  Render backend   (A) UIT VisualElement   (B) UGUI   (C) CB  |
+--------------------------------------------------------------+
|  Input + events   Hit testing, focus, keyboard, pointer, IME |
+--------------------------------------------------------------+
|  C# binding       on-click="MyHandler", data-binding, slots  |
+--------------------------------------------------------------+
```

---

## 6. Author-facing API (what game devs actually write)

```csharp
// Game code
public class MainMenu : UIController {
    [UIElement("start-button")] public Button StartButton;
    public int CoinCount;

    void OnStart() => SceneManager.LoadScene("Game");
}
```

```html
<!-- main-menu.html -->
<link rel="stylesheet" href="main-menu.css" />
<main class="menu">
  <h1>My Game</h1>
  <p>Coins: <span>{{ CoinCount }}</span></p>
  <button id="start-button" on-click="OnStart">Start</button>
</main>
```

```css
/* main-menu.css */
.menu { display: flex; flex-direction: column; gap: 16px; padding: 24px; }
button { padding: 8px 16px; border-radius: 8px; background: #4f46e5; color: white; }
button:hover { background: #6366f1; }
button:active { transform: scale(0.97); }
```

Key API decisions:

- **Mounting.** `UIDocument` MonoBehaviour points at an HTML file and a controller class. At runtime it parses and mounts; in the editor it shows a live preview.
- **Data binding.** `{{ Expression }}` in text and `{{ Expression }}` in attribute values. One-way by default; `<input>` elements two-way bind to fields/properties marked `[UIBind]`.
- **Event binding.** `on-click="MethodName"` resolves against the controller. Static checked at edit time.
- **Components.** `<template id="card">…</template>` + `<card title="…">slot content</card>`. Compiles to a reusable subtree with scoped CSS via `:host` and attribute-prefixed scoping.

---

## 7. Performance targets and strategy

- **1000 elements** with `:hover`, gradients, shadows, and one transition active: **144 Hz** on Apple M1 / Ryzen 5 5600 / RTX 3050-tier.
- **Zero per-frame allocations** in steady state. Layout/paint use pooled scratch buffers.
- **Cascade caching**: hash element-rule applicability; reuse computed style across siblings whose attribute/state vector matches.
- **Layout caching**: Yoga already caches; for our inline + grid impl, cache line-break results keyed on (text run, available width, font).
- **Paint dirtying**: per-element dirty bits for `transform-only`, `paint-only`, `layout`. Transform-only changes never re-layout.
- **Atlas**: pack background images and gradients into atlases; gradients are computed in a shader from per-instance constants where possible.

---

## 8. Phasing

Each phase ends with a runnable demo and a documented conformance report.

### Phase 0 — Skeleton (≈ 2 weeks)

- Repo layout. Unity package skeleton (`Packages/com.wevaui/`).
- `UIDocument` MonoBehaviour that loads a `.html` file (no parsing yet — placeholder).
- File watcher + hot reload plumbing.
- CI: build against Unity 6 LTS on Win/Mac/Linux.

### Phase 1 — Static layout, no styles (≈ 4 weeks)

- HTML5 parser (consider porting/wrapping `AngleSharp` or writing a focused parser — discuss).
- Document model.
- Render backend integration (Option A).
- Static block + inline layout. Latin text only. No CSS yet — built-in user agent stylesheet hardcoded.
- **Demo:** render a paragraph with `<strong>` and `<a>` inline.

### Phase 2 — CSS parser + cascade + flex (≈ 6 weeks)

- CSS tokenizer + parser.
- Selector matcher (no `:has`).
- Cascade, inheritance, `var()`, `calc()`.
- Flexbox via Yoga.
- Box decoration: backgrounds (solid + linear-gradient), borders, border-radius.
- **Demo:** real responsive menu, like the snippet in §6.

### Phase 3 — Grid + positioned + effects (≈ 4 weeks)

- Grid layout (own impl).
- `position: absolute/relative/fixed/sticky`, `z-index`, stacking contexts.
- `box-shadow`, `opacity`, `transform`.
- **Demo:** a HUD with absolute-positioned overlays, modal dialog, animated transform on hover.

### Phase 4 — Inputs + events + bindings (≈ 4 weeks)

- Pointer + keyboard input dispatch with bubbling/capture.
- Focus model.
- `<input>`, `<button>`, `<select>`, `<textarea>` working with IME.
- `{{ }}` data binding.
- `on-*` event binding to controller methods.
- **Demo:** settings screen with form controls, two-way binding.

### Phase 5 — Animation + media queries + components (≈ 4 weeks)

- `transition`, `@keyframes`, `animation`.
- `@media`.
- `<template>` + slots.
- Scoped styles.
- **Demo:** themeable component library (button, card, modal, tabs).

### Phase 6 — Conformance + perf + docs (ongoing)

- Web Platform Test–style suite: HTML/CSS snippets in, golden-image PNGs out. Each PR runs them.
- Perf benchmarks with regression tracking.
- Conformance reference site, generated from the test suite.

---

## 9. Locked decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Unity version | **6000.4.1f1** (latest stable on dev machine). Track 6000.4 LTS-stream. |
| 2 | Render backend | **Option C** — own renderer, CommandBuffer-based. |
| 3 | Render pipeline | **URP**. Integrate via `ScriptableRendererFeature` + `ScriptableRenderPass`. |
| 4 | Color space | **REVISED 2026-06-09 (overrides the original "Linear" lock):** the UI composites in **gamma sRGB** via an intermediate UNORM RT holding sRGB-encoded premultiplied values (`ShaderResources.UseSrgbComposite`, default ON) to match Chrome's blending; the project color space stays Linear and the composite blits back into the linear target. See CSS_OPEN_GAPS.md closed entry A-SRGB-COMPOSITE. |
| 5 | Scripting backend | **IL2CPP-compatible**. No `Reflection.Emit`. Data binding uses **C# source generators**. |
| 6 | Text engine | **TextCore directly**. We drive shaping + line breaking. No TextMeshPro. |
| 7 | Editor preview | **v1**. `EditorWindow` + `Graphics.DrawTexture` of an offscreen RT. |
| 8 | Input system | **Input System package** (new). |
| 9 | DPI / units | **Logical pixel** model. `1 px` = 1 logical px. Root `--ui-scale` variable; `rem`/`em` derive from a base font size (16 px default, matches CSS). |
| 10 | Coordinate system | **CSS top-left** to authors. Convert to Unity bottom-left at the render-backend boundary only. |
| 11 | IME | **Desktop only** (Windows + macOS) for v1 via `Input.compositionString` / `imeCompositionMode`. Console IME deferred. |
| 12 | Composition | **Camera-stack injection** (the harder of the two paths). `ScriptableRenderPass` after `RenderPassEvent.AfterRenderingPostProcessing`, executes our `CommandBuffer` against the camera color target — zero intermediate blit. |
| 13 | License | **MIT**. |
| 14 | HTML parser | **Hand-rolled** for our subset. No AngleSharp. Authored input only — no HTML5 error-recovery surface. |
| 15 | CSS parser | **Hand-rolled**. Full control over property grammar and error reporting. |

### Implications worth keeping in view

- The render path differs runtime vs editor; everything *above* the render backend boundary (DOM, cascade, layout, paint command list) must be backend-agnostic and testable headlessly.
- IL2CPP forbids `Reflection.Emit`. Property setters for `{{ binding }}` must be generated at compile time (Roslyn source generators) keyed off `[UIBind]` attributes.
- URP-specific code is gated by a `WEVA_URP` `versionDefines` token in the asmdef so the package compiles cleanly even when URP is not installed (we surface a clear error at runtime in that case).

---

## 10. Phase 0 — what we are building right now

1. Repo layout as a UPM package at the repo root (`package.json` at root).
2. `UIDocument` MonoBehaviour stub — fields for an HTML `TextAsset` and stylesheet `TextAsset[]`. No parsing yet.
3. Editor preview `EditorWindow` stub at `Window > Weva > Preview`.
4. URP `ScriptableRendererFeature` stub gated behind `WEVA_URP`.
5. Runtime + Editor + Tests assembly definitions.
6. `LICENSE` (MIT), `README.md`, `CHANGELOG.md`, `.gitignore`.

Phase 1 starts as soon as the skeleton compiles inside a fresh Unity 6000.4 project.

---

## 11. v1 progress tracker

Updated each /loop iteration. ✅ done · 🟡 in progress · ⬜ not started.

### Phase 0 — package skeleton ✅
- ✅ Repo layout, asmdefs, package.json
- ✅ UIDocument stub, editor preview window stub, URP renderer feature stub
- ✅ MIT license, README, CHANGELOG, .gitignore

### Phase 1 — HTML + DOM + render backend 🟡
- ✅ HTML tokenizer (strict, line/col diagnostics, entity resolution)
- ✅ HTML tree builder + parser (void elements, self-closing, lenient mode)
- ✅ DOM types: Node, Element, TextNode, Document, AttributeMap
- ✅ User-agent stylesheet (block/inline displays, headings, link color, hidden attr)
- ✅ Static block + inline layout (BFC + IFC, white-space modes, mixed-style runs)
- ⬜ TextCore integration (font asset → glyph runs — currently uses `IFontMetrics` interface; Unity-side impl pending)
- ⬜ URP render pass actually issuing draw commands
- ⬜ Demo: paragraph with `<strong>` + `<a>` rendering correctly inline

### Phase 2 — CSS cascade + flex ✅
- ✅ CSS tokenizer
- ✅ CSS rule parser (Stylesheet / StyleRule / MediaRule / KeyframesRule / ImportRule / Declaration)
- ✅ Selector parser + matcher + specificity (full Phase 2 selector subset)
- ✅ CSS value system: typed lengths, colors, percentages, keywords, calc, var, gradients, function-call passthrough
- ✅ Cascade engine: rule matching → specificity sort → inheritance → var()/inherit/initial/unset/revert resolution → ComputedStyle
- ✅ Flexbox layout (own impl, full property surface — direction, wrap, justify, align, grow/shrink/basis, gap, order)
- ✅ Box decoration paint converter (Box tree + ComputedStyle → PaintCommands)

### Phase 3 — grid + positioned + effects ✅
- ✅ Grid layout (own impl: track parser, areas parser, placement resolver, two-pass track sizing, fr distribution, gap, alignment, auto-flow)
- ✅ position: absolute/relative/fixed/sticky, z-index, stacking contexts
- ✅ Paint vocabulary for box-shadow, opacity, transform
- ✅ Box-to-paint converter resolves transform/opacity/shadow/background/border/border-radius
- ✅ Filter pipeline (blur/brightness/contrast/grayscale/opacity/saturate/hue-rotate/invert/sepia/drop-shadow; PushFilter/PopFilter paint commands; FilterParser; converter integration)

### Phase 4 — input + binding 🟡
- ✅ Pointer + keyboard dispatch with capture/target/bubble phases, click synthesis, hover/active tracking
- ✅ Focus model (FocusManager, tab order, programmatic focus, focus-visible heuristic)
- ✅ `InteractionStateProvider` — drives `:hover`/`:focus`/`:focus-within`/`:active`/`:checked`/`:placeholder-shown` back into the cascade via `IElementStateProvider`
- ⬜ Form controls with IME (desktop) — actual `<input>`/`<textarea>` interaction needs Unity input integration
- ✅ `{{ }}` text + attribute binding (Reflection-based runtime; source-generator hookup deferred — `[UIBind]`, `[UIElement]`, `IBindingController`, `BindingScanner`, `BindingSet`)
- ✅ `on-*` event binding to controller methods (six attributes from PLAN §3, typed event-arg dispatch, scan-time validation)

### Phase 5 — animation + components ✅
- ✅ Animation engine: easing curves (linear/ease/ease-in/-out/-in-out, cubic-bezier, steps), TransitionEngine, KeyframeAnimation
- ✅ Animation wired into cascade — `CssAnimationRunner.OnStyleChange` triggers transitions on diffs; `@keyframes` runs via `KeyframesResolver`; type-aware interpolation (length/color/number/percentage/transform/discrete) with linear-space color
- ✅ @media queries evaluated against UI surface (full feature set: width/height/orientation/aspect-ratio/resolution/prefers-color-scheme/prefers-reduced-motion/hover/pointer + and/or/not logic; cascade consults `MediaContext`)
- ✅ @container queries: `container-type: inline-size | size`, `container-name`, named/unnamed `@container` rules, width/height/inline-size/block-size/orientation/aspect-ratio features + and/or/not logic; cascade walks the box tree per (element × rule) with caching to find the matching ancestor and gates the inner StyleRules against the resolved `ContainerContext` (v1 chicken-and-egg: reads layout-after-previous-cascade size — 1-2 frame settle on a fresh container-type assignment)
- ✅ `<template>` + slots (ComponentRegistry, ComponentExpander, named/default slots, fallback content, recursive expansion with cycle detection)
- ✅ Scoped styles (`:host`, `data-uui-scope`/`data-uui-host` attribute-prefixed rewriting, ScopedStylesheet, SelectorScoper, comma-expansion of `:host(.a, .b)`)

### Reactivity foundation ✅ (cross-cutting)
- ✅ `InvalidationKind` flags + propagation rules per PLAN §12
- ✅ `InvalidationTracker` — central dirty-set per pipeline stage; subscribes to a Document
- ✅ DOM mutation observability — `Node.Version`, `Node.Mutated` event, bubbling to ancestors
- ✅ `CacheEntry<T>` — version-keyed cache helper
- ✅ Cascade engine consuming `InvalidationTracker` — per-element style cache keyed on `(element.Version, parentStyle.Version, mediaContextVersion, stateVersion)`; `Apply(tracker)` drops dirty entries; cache hit/miss stats exposed; correctness oracle preserved as `ComputeFor`
- ✅ Layout engine consuming `InvalidationTracker` — per-element `LayoutCacheEntry` keyed on `(elementVersion, computedStyleVersion, containerWidth/Height, layoutContextVersion, childAggregateVersion)`; `Apply(tracker)` drops dirty entries; ancestor-walk for Structure invalidations; reconciliation pass swaps cached boxes in for freshly-computed ones
- ✅ Paint converter consuming `InvalidationTracker` — per-box `PaintCacheEntry` keyed on `(box.Version, style.Version, contextVersion)`; `Apply(tracker, elementToBox)` drops entries dirty in `Paint|Composite|Layout|Style|Structure`; cache hit/miss stats; from-scratch `EmitBoxFromScratch` preserved as correctness oracle; cached slices include push/pop wrappers
- ⬜ Layer caching / GPU compositor model (deferred to Phase 7 perf rewrite)

### Phase 6 — conformance + perf + docs 🟡
- ✅ Golden-image test suite (headless `SoftwareRasterizer` IRenderBackend, hand-rolled PNG writer/reader, `GoldenRunner` orchestrator running Parser → Cascade → Layout(MonoFontMetrics) → Converter → rasterizer; 12 snippet baselines + 4 rasterizer unit tests under `Tests/Runtime/Goldens/`; `WEVA_REGENERATE_GOLDENS=1` env-var regenerates baselines; `Tools/BaselineGen/` dotnet tool for headless baseline (re)generation)
- ⬜ Perf benchmarks with regression tracking
- ⬜ Conformance reference

### Test count

~1720 NUnit tests on disk (HTML 85 + CSS rules 80 + selectors 140 + values 109 + cascade 67 + cascade-incremental 44 + UA stylesheet 7 + layout 92 (+22: margin-collapse 14, inline-block 8) + word-break 14 + inline-splitting 11 + text-overflow-ellipsis 9 + layout-incremental 49 + flex 78 (+6 baseline, +7 abs-flow) + grid 74 + paint 41 + paint-conversion 58 + paint-conversion-incremental 40 + paint-filters 81 + animation 41 + animation-cascade 91 + positioning 52 + events 82 + reactive 70 + media 62 + components 47 + components-scoping 68 + binding 104 + goldens 19 (+3, +2, +2)). All headless; will run inside Unity Test Runner once the package is added to a project.

### What's left of v1

All headless work is **complete**. All Unity-bridge skeletons are now in place but **need validation in a real Unity 6000.4 project** — subagents wrote the code but couldn't run a Unity build.

- 🟡 **`URPRenderBackend`** — `Runtime/Rendering/URP/`; ScriptableRendererFeature + RenderPass + 6 shaders (incl. `Weva_StencilWrite`) + mesh builder. Stencil clip-mask draws emit correctly via `Comp Equal`/`IncrSat`/`DecrSat`; `RecordRenderGraph` override added for URP 17+ via `AddRasterRenderPass<PassData>` (legacy `Execute` marked `[Obsolete]` and pragma-suppressed). Caveats: image-brush sprite lookup unimplemented; needs Unity validation on shader compilation, stencil block at SubShader-level, and `_StencilComp` global int bracket-binding.
- 🟡 **`TextCoreFontMetrics`** — `Runtime/Text/TextCore/`; partial-class with `.Unity.cs` companion gated on Unity 6+; `GlyphAtlas` shelf-pack with grow→LRU eviction; `FontResolver`; `TextShaper` Latin-only. Caveat: needs FontEngine API surface verification per platform.
- 🟡 **`UIDocument` MonoBehaviour** — `Runtime/UIDocument.cs` + `Runtime/Document/`. Full pipeline orchestration: parse → register components → expand → cascade (with @media context) → layout → paint → backend. Per-frame Update drains InvalidationTracker through cascade/layout/paint Apply → conditional layout → Bindings.Update → tracker.Clear. Implements `IUIPaintSource`. `SetController` re-binds without rebuilding cascade.
- 🟡 **Editor preview window** — `Editor/Preview/`; `EditorWindow` + IMGUI toolbar + `SoftwarePainter` CPU-rasterizer (TEMP fallback) + `HtmlAssetWatcher`. Caveat: software painter is intentionally rough for v1; URP backend swap-in is the real path.
- 🟡 **Form controls + IME** — `Runtime/Forms/`; `InputElement`/`TextAreaElement`/`SelectElement`/etc. wrappers; `TextEditModel` + `ImeSession` state machines; Input System keyboard source + legacy IME bridge. Caveat: clipboard wiring inside `InputController` is a follow-up.
- 🟡 **Phase 1 demo sample** — `Samples~/PhaseOneDemo/` shipped with `menu.html`, `menu.css`, `card-component.html`, `PhaseOneDemoController` (with `[UIBind] CoinCount` + `OnStart()`), bootstrap menu item, scene file, README. Listed in `package.json` so Package Manager UI surfaces an Import button. **Pending:** opening in real Unity, importing the sample, pressing Play.
- ✅ **TextCore bootstrap** — `Runtime/Text/TextCore/TextCoreBootstrap.cs` wires `UIDocumentDefaults.FontMetricsFactory` via `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` + `[InitializeOnLoadMethod]` for the Editor.
- ✅ **Hot-reload watcher** — `Editor/Document/UIDocumentAssetWatcher.cs` listens for `.html`/`.css` imports and calls `Rebuild()` on every active `UIDocument` referencing the changed asset.

**The "validate in Unity" gate is the only thing standing between this snapshot and a usable v1.** Everything subagent-doable is done.

### Known v1 simplifications (vs CSS spec)

Documented now so they don't get rediscovered as bugs:

- **Layout (block/inline):** Anonymous block boxes carry no Style; nested em compounding walks one parent level only. Inline-splitting now lands per CSS Display Module Level 3 §2 — a block-level descendant inside an inline element forces the IFC to emit before/block/after segments — but the inline element's own decoration (border/background) is NOT split between fragments (single-fragment-paint v1 simplification). The same shortcut applies to ordinary multi-line wraps: an inline element whose text wraps across multiple lines emits a single `InlineBox` covering only the first line's bbox (`AttachInlineFragmentsToLines` breaks after the first matching line), so a `<span>` spanning ten lines paints decoration on the first line only. Margin collapsing now lands per CSS Box Model §8.3.1 (sibling pairs, parent-first/last collapse, self-collapsing empty blocks); inline-block sizes shrink-to-fit and participates in the IFC with bottom-of-content baseline (true last-line-baseline approximation only). Long unbreakable words now break mid-word per `word-break: break-all` / `overflow-wrap: break-word` / `overflow-wrap: anywhere`; `word-break: keep-all` is documented as falling back to `normal` for v1 (CJK is a v2 concern). `text-align-last`, `text-indent`, `text-wrap: nowrap`, `tab-size`, and manual soft-hyphen breaks are implemented; dictionary `hyphens: auto` and multi-line ellipsis via `line-clamp` are v2 concerns. `text-overflow: ellipsis` truncates single-line clipping containers (`overflow: hidden|scroll|clip|auto` + `white-space: nowrap`).
- **Layout (positioning/scrolling):** `position: sticky` is now scroll-aware via `StickyResolver`; v1 simplifications: single-axis pinning only (top OR bottom — when both are set, top wins; same for left/right), scroll snap and smooth scrolling exist but are partial, no overscroll chaining, no scroll anchoring; scrollbar appearance is fixed (no `::-webkit-scrollbar`, partial `scrollbar-color`/`scrollbar-width` round-trip only); `position: fixed` still uses the viewport (unaffected by ancestor scroll); both-pinned absolute auto-sizing (`top: 0; bottom: 0`) doesn't iterate to reconcile with intrinsic sizes; absolute boxes stretched by both pinned edges don't re-flow their interior; positioned descendants with `z-index: auto` do NOT create their own stacking context (we follow the older spec where only `z-index ≠ auto` creates a context for relative/absolute; fixed/sticky always do). Absolute/fixed inside flex containers is now properly removed at item-collect time so they don't contribute to main-axis sizing or take a slot in any flex line; the `CompressOutOfFlow` walk handles the same case for block-flow parents.
- **Layout (flex):** `min/max-content` sizing keywords treated as `auto`; `aspect-ratio` not honored; column-flex `baseline` cross-axis alignment falls back to `flex-start` (per spec the synthesised baseline equals the cross-start edge for column); item min/max main-size constraints are single-pass (no clamp loop); when flex sets a child's main size different from its style.width/height, the box is resized but its interior is not re-flowed; longhand `flex-grow: 0`/`flex-shrink: 1`/`flex-basis: auto` at initial values don't override a user-set `flex` shorthand (cascade doesn't track explicit-vs-initial yet); text directly inside a flex container falls into normal anonymous-block flow rather than becoming an anonymous flex item. Row-flex `baseline` cross-axis alignment is now implemented (uses each item's first-line ascent).
- **Logical axes / RTL:** `direction: rtl` flips horizontal inline-start/end mapping, `text-align: start/end`, logical insets/sizes/box edges, and row flex main-axis order. `writing-mode` remaps logical properties for vertical/sideways modes, but text is still shaped and laid out horizontally; `unicode-bidi` is registered but no bidi reordering is performed.
- **Box-sizing default:** `content-box`, matching the CSS initial value. Use `* { box-sizing: border-box }` for the common authoring reset.
- **Paint converter:** Reads longhand properties only — `border: 1px solid red` shorthand isn't expanded yet; the cascade doesn't expand shorthands either, so authors must write `border-{side}-{prop}` explicitly until shorthand-expansion lands.
- **Cascade:** `revert` keyword treated as `initial` (full revert needs origin tracking); `@media` rules currently always apply (the query expression isn't evaluated yet).
- **Paint:** `LinearColor` uses the proper IEC 61966-2-1 sRGB curve (not gamma 2.2 approximation).
- **Animation:** Engine is freestanding — not yet wired into the cascade, so transitions don't auto-trigger from style changes yet.
- **Color:** Named colors win when an Ident is encountered as a value (no CSS context where `red` should be a non-color keyword in v1).
- **HTML:** Self-closing accepted on any element (`<div/>` legal, JSX-friendly); unknown named entities pass through literally.
- **Events:** Click synthesis = down-target equals up-target (browsers use deepest common ancestor); no explicit pointer-capture API yet; `KeyPress` enum present but not dispatched (would need IME/text-input layer); active flag set on the literal hit element rather than activation chain.

---

## 12. Reactivity model (doctrine)

UIs change every frame. We will not recompute everything every frame. The whole pipeline is built around **incremental updates and dirty tracking from the start.**

### Five invalidation kinds

A change to any element flags zero or more of these:

| Kind        | Meaning                                                                 | Pipeline stages re-run |
|-------------|-------------------------------------------------------------------------|------------------------|
| `Structure` | DOM tree mutated (child added/removed)                                   | Style + Layout + Paint + Composite |
| `Style`     | An input to cascade changed (class, id, attribute, inline style, parent style for inheritance) | Cascade for this element (subtree if descendant selectors could match) |
| `Layout`    | Geometry-affecting style changed (width, padding, font-size, display, etc.) | Layout for this box and its layout dependents |
| `Paint`     | Visual style changed but geometry didn't (color, background, border-color, text-decoration, …) | Paint command rebuild for this box |
| `Composite` | Pure transform / opacity / clip change                                   | Recomposite (re-blend cached layer) — **never repaint, never relayout** |

### Propagation rules

- **Structure on parent**: parent gets `Layout`; the added/removed subtree gets `Style + Layout + Paint`; ancestor stacking contexts get `Composite`.
- **Class / id change**: `Style` on the element AND its descendant subtree (descendant selectors may now match or unmatch). Layout and Paint inferred from the resulting style diff.
- **Style attribute change**: `Style` on the element only.
- **Other attribute change**: `Style` on the element (some attrs are matched by `[attr=…]` selectors; conservative).
- **Text content change**: `Layout + Paint` on the text's parent element (text affects intrinsic size).
- **Hover/focus/active state change** (`InteractionStateProvider`): `Style` on the element only — these are leaf state changes, not selector-graph changes (assuming no descendant rules use the state, which we conservatively over-mark when present).

### How each pipeline stage consumes invalidation

- **Cascade**: `Compute(element)` cached on `element.Version` + state-provider version. Dirty set keyed by element.
- **Layout**: BlockLayout / FlexLayout / Positioning each cache per-box, keyed on inputs (computed style version + child box versions + container constraints). A clean subtree is reused — its `Box` instance is re-positioned at most.
- **Paint**: `BoxToPaintConverter` emits paint commands per box keyed on box version + style paint-version. Clean boxes' commands are reused unchanged.
- **Composite**: groups (opacity, transform, clip) become cached layers in the render backend; small changes (transform tween, opacity fade) recomposite without repainting the layer's contents.

### Persistent identity

Every `Element`, `Node`, and laid-out `Box` carries a stable identity (a `long Version` counter, monotonically incremented on mutation). Cache keys are version pairs `(input.Version, depended-on.Version)`. When inputs are unchanged, downstream work is skipped.

### Mutation API

All DOM mutations route through `Node`/`Element` methods that bump `Version` and fire a `Mutated` event carrying a typed `DomMutation`. The `InvalidationTracker` subscribes once to a `Document` and translates mutations into per-stage dirty sets according to the propagation rules above. The tracker's dirty sets are the **input** to each pipeline stage; consumers `Clear()` them after consuming.

### What this means for every new feature

When adding a new feature (grid, source generators, scoped styles, etc.), the implementation MUST:

1. Accept a dirty set as input where it processes elements/boxes (not "scan everything").
2. Cache outputs keyed on the input version(s).
3. Emit invalidation flags downstream when its outputs change.
4. Keep the from-scratch "naive" path as the correctness baseline (and the test oracle); the incremental path is a perf optimization that must produce identical results.

### v1 reactivity scope

For v1, we ship the foundation (`InvalidationKind`, `InvalidationTracker`, mutation events, version counters) and integrate the cascade, layout, and paint converter to consume it. Layer caching + GPU compositor model are deferred to the perf phase; the *interface* supports them so the rewrite is local.

---

## 13. Performance

### Instrumentation

`Runtime/Profiling/UIProfilerMarkers.cs` declares Unity `ProfilerMarker`s for every pipeline phase (`Weva.Cascade.ComputeAll`, `Weva.Cascade.IncrementalApply`, `Weva.Layout.Build|Block|Inline|Flex|Grid|Positioning`, `Weva.Paint.Convert|Render`, `Weva.Snapshot.Build|SelectorMatch`). Markers compile to no-ops outside `UNITY_EDITOR || DEVELOPMENT_BUILD` via `WEVA_PROFILE`. Engines wrap their entry points with `using (PerfMarkerScope.Auto(...))`; the scopes are cheap struct AutoScope wrappers in instrumented builds and a default-init no-op otherwise. Consumers see the breakdown in the Unity Profiler under the Scripting category.

### Bench suite

`Tests/Runtime/Bench/` holds the standardised benches (`CascadeBench` / `LayoutBench` / `PaintBench` / `EndToEndBench`); all marked `[Test, Explicit("perf")]` so they don't run in the default test pass. ≥32 cases total. Fixtures live in `BenchScenes.cs` (100 cards / 500 mixed / 1000 forms / 1000 deep / 5000 massive). 5000 cases are also tagged `[Category("Slow")]`.

`Tools/PerfBench/` is the standalone runner (mirrors `Tools/BaselineGen/`). Invoke via `dotnet run -c Release -- all` to run every bench and emit a markdown table; `--baseline <path.json>` compares against a prior run.

### v0.4 baseline (local dev machine)

Numbers below come from a single fresh run of `dotnet run -c Release -- all` against this snapshot. Element counts reflect the actual parsed DOM (the `5xx`/`1xxx` bucket names are nominal — the strict HTML parser drops some auto-closed nesting cases).

| bench | scale | median (ms) | p95 (ms) | allocs/call | notes |
|---|---|---:|---:|---:|---|
| Cascade.ComputeAll | 66 elem (100Cards) | 1.6 | 2.3 | — | cold (InvalidateAll then ComputeAll) |
| Cascade.ComputeAll | 229 elem (500Mixed) | 6.1 | 8.4 | — | mixed flex / grid / list display |
| Cascade.ComputeAll | 1001 elem (1000Forms) | 8.3 | 29.1 | — | form-heavy with attribute selectors |
| Cascade.ComputeAll | 1001 elem (1000Deep) | 9.3 | 12.2 | — | 50-deep nesting |
| Cascade.Incremental_AttributeChange | 1001 elem | 0.21 | 0.28 | — | toggle one class, full re-cascade |
| Cascade.Incremental_PseudoClassChange | 1001 elem | 0.08 | 0.13 | — | `:hover` flip, per-element state-digest fast path (v0.5: was 7.5 ms) |
| Cascade.IncrementalApply_AllocCheck | 1001 elem | — | — | ≤ 10 KB / call (post-pool) | reusable result map + StyleArray + DomSnapshot reuse on unmutated doc; v0.4 baseline 381 KB/call |
| Cascade.SnapshotPath_Vs_Managed | 229 elem | 1.8 (snap) / 2.8 (mgd) | — | — | 1.56× snapshot speedup |
| Layout.LayoutAll | 66 elem | 1.1 | 1.3 | — | full pipeline layout |
| Layout.LayoutAll | 229 elem | 2.1 | 2.8 | — | mixed display |
| Layout.LayoutAll | 1001 elem | 10.8 | 12.8 | — | forms |
| Layout.LayoutAll | 1001 elem | 4.4 | 5.5 | — | deep |
| Layout.LayoutAll_AllocCheck | 1001 elem | — | — | 1.42 MB / call | post-CssValuePool; remainder = DomSnapshot rebuild + PaintList |
| Paint.Convert | 500 boxes | 0.85 | 1.18 | — | flat colored+bordered |
| Paint.Convert | 1000 boxes | 0.99 | 1.38 | — | flat colored+bordered |
| Paint.Convert_GradientHeavy | 500 boxes | 1.27 | 1.65 | — | linear-gradient on every box |
| Paint.Convert_ShadowHeavy | 500 boxes | 2.36 | 3.25 | — | two box-shadows per box |
| Paint.Convert_AllocCheck | 500 boxes | — | — | 1.10 MB / call | warm steady-state |
| Paint.Convert_AllocCheck | 1000 boxes | — | — | 2.19 MB / call | warm steady-state |
| EndToEnd.FullPipeline | 100 f / 229 elem | 1.45 | 1.85 | — | cascade → layout → paint per frame |
| EndToEnd.HoverToggle | 1000 f / 1001 elem | 2.7 | 6.9 | — | `:hover` flipped each frame; cascade ~0.08 ms, layout+paint dominate (was 16.2 ms in v0.4) |

### Allocation regression targets

| stage | observed v0.4 | active ceiling | PLAN goal | gap |
|---|---|---|---|---|
| Cascade IncrementalApply (1000 elem) | ≤ 10 KB/call (v0.5: pooled result map + StyleArray + DomSnapshot reuse) | 10 KB | 10 KB | hit on warm-cache no-op ComputeAll; first-call cost still pays the snapshot build |
| Layout (warm, 1000 elem) | 1.42 MB/call (post-pool) | 2 MB | 50 KB | CssValuePool + parse-cache landed; remainder = DomSnapshot rebuild + paint cmds |
| Paint Convert (500 boxes) | 1.1 MB/call | 1.5 MB | 0 | PaintList + per-box command structs |
| Hover toggle (1000 elem, end-to-end) | end-to-end ~2.7 ms median (cascade-only ~0.08 ms) | 5 ms | 0.5 ms | v0.5 wired per-element state-digest cache key — cascade is now sub-millisecond on a `:hover` flip; residual is layout + paint, which still re-run from scratch in the bench |

The PLAN goals require deferred work: `ComputeAll` returning a reusable shared map, `CssValueParser` going `ref struct` / span-based, `PaintList` pooling, and state-diff dirty-set propagation through cascade so a `:hover` flip invalidates only the affected element. Active ceilings are local-machine baselines; CI runners may need different thresholds.

### Key wins recorded

- v0.2.1: cascade snapshot path 2.4× faster than managed (broad rules + selective).
- v0.3: `BoxBuilder` snapshot variant 5.4× faster than managed at 500 elems.
- v0.4 (this snapshot): cascade snapshot still 1.56× ahead of managed on the 50-rule mixed fixture; the mixed shape compresses the matcher's win because per-element `ComputeFor` overhead (shorthand expansion, var() resolution, `FillInherited`) dilutes the rule-sweep saving.
- v0.5+: `CssValuePool` (per-thread pool of `CssLength`/`CssNumber`/`CssPercentage` + interned constants for `0px..256px` integer pixels and `0/50/100%` / `0/1` numbers) plus per-pass parse-cache memoising `CssValueParser.Parse(text)`. `LayoutEngine.Layout` and `LayoutEngine.Layout(Element)` open a `CssValuePoolScope` so every length/number/percentage parsed during the pass is recycled at the end. Steady-state `Layout.AllocCheck` for 1001 elem dropped from 7.79 MB/call → 1.42 MB/call (5.5×). Median for 1000Forms also fell ~2.4× (18 ms → 7.6 ms) as the parser-side temporaries no longer dominate. Lifetime contract: rented values must NOT outlive the pool scope; outside scope, `Rent*` falls back to fresh allocations so callers like `CssAnimationRunner.Tick` and `ValueInterpolator` retain the pre-pool semantics.
- v0.5+: per-element pseudo-class-state digest replaces the global `state.Version` component of the cascade cache key (`StateSelectorIndex` + `CascadeEngine.IncrementalState`). When `:hover`/`:focus`/`:active`/`:checked`/`:disabled`/`:placeholder-shown` flips on one element, only that element's digest changes, so the cascade cache hits for every other element. `Cascade.Incremental_PseudoClassChange` dropped from 7.5 ms median → 0.083 ms (90×). End-to-end `HoverToggle` dropped 16.2 ms → 2.7 ms; the residual is layout + paint, which still re-run fully because the bench drives the engines directly rather than going through `UIDocumentLifecycle`'s tracker-aware layout-skip path. v1 fallback: stylesheets containing sibling combinators with state on the left compound (`.btn:hover + .next`) flag `StateSelectorIndex.RequiresGlobalFallback` and revert to the v0.4 all-invalidate behaviour for correctness — per-element digest can't see "my left sibling's state changed". Descendant/child combinators (`.parent:hover .child`) ride the digest path correctly because the cascade is parent-first and ancestor recompute bumps `parentStyle.Version` for descendants.
