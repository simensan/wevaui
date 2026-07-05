# AI_REFERENCE.md — capability audit + map for AI agents

A single orientation document for an AI assistant asked to **use, integrate,
or reason about** the Weva engine: what it is, what it can and cannot do,
how to wire it up, how the pipeline is shaped, and which tools verify it.
Every fact here was checked against the code (file:line cited where it
matters); when a claim and the code disagree, the code wins — tell the user.

> Audience split — don't confuse them:
> - **Use the library / author UI** (HTML/CSS, controllers, forms) → this doc for the overview, then [`Packages/com.wevaui/Documentation~/AuthoringGuide.md`](Packages/com.wevaui/Documentation~/AuthoringGuide.md) for the manual.
> - **Change the engine** (add a property, a paint command, fix layout) → [`AGENTS.md`](AGENTS.md) (the engineering contract) + [`PLAN.md`](PLAN.md) (architecture/roadmap) + [`CONFORMANCE.md`](CONFORMANCE.md) (spec deltas).
> - **Check if something is a known gap** → [`CSS_FEATURE_AUDIT.md`](CSS_FEATURE_AUDIT.md) (full capability map) + [`CONFORMANCE.md`](CONFORMANCE.md) (per-feature spec deltas).

_Last verified: 2026-06-02._

---

## 1. What it is

A **runtime HTML/CSS UI engine** shipped as a Unity package (`com.wevaui`),
rendering through **URP RenderGraph**. It is deliberately **not** Unity UI
Toolkit (UXML/USS): the design rule is "real-web HTML/CSS or nothing", so
LLM-trained and browser-trained UI knowledge transfers unchanged. A subtly
divergent behavior is considered worse than a missing one.

- Hand-rolled HTML + CSS tokenizers/parsers (no browser, no Yoga).
- Own flexbox, CSS Grid, block/inline flow, positioning, scrolling.
- Cascade with `var()`, `@media`, `@container`, `@scope`, `@property`, layers.
- CSS transitions + keyframe animations.
- Forms, gestures, dialogs, popovers, tooltips, virtualized lists.
- Headless-testable end to end (the parser/cascade/layout/paint stages do not
  touch `UnityEngine`); ~6,500 NUnit tests.

A companion package (`com.wevaui.figma`, separate repository) imports Figma
frames into Weva HTML/CSS.

---

## 2. How to use it (integration, code-verified)

### Minimum scene wiring

`UIDocument` is the entry-point MonoBehaviour (`Packages/com.wevaui/Runtime/UIDocument.cs`).
The GameObject menu **Weva → New UIDocument** creates a GameObject with
`UIDocument` + input controller + debug renderer + DevTools overlay wired.
Manually, you need a `UIDocument` plus a controller `MonoBehaviour`.

Inspector-set serialized fields (`UIDocument.cs:48-60`):

| Field | Purpose |
|---|---|
| `TextAsset documentAsset` | the `.html` source |
| `TextAsset[] stylesheetAssets` | one or more `.css` sources |
| `int sortingOrder` | draw order across documents |
| `Vector2 viewportOverride` | force a viewport size (else camera/screen) |
| `bool autoRebuildOnChange`, `bool enableHotReload` | editor live-edit |
| `bool prefersDarkColorScheme` | seeds `prefers-color-scheme` |
| `RendererBackendKind rendererBackend` | URP-batched / legacy / IMGUI |

### Controller, bindings, events

```csharp
using Weva;            // UIDocument
using Weva.Binding;    // [UIBind]

public sealed class MenuController : MonoBehaviour {
    [UIBind] public string PlayerName = "Aerith";
    [UIBind] public int    Coins;
    [UIBind] public bool   CanQuit => true;          // expression bindings ok

    UIDocument doc;
    void Awake() { doc = GetComponent<UIDocument>(); doc.SetController(this); }
    public void OnStart() { Coins += 5; }            // {{ Coins }} repaints next frame
}
```

- `[UIBind]` fields/props are reachable as `{{ Name }}` in HTML **and** in CSS
  attribute values; polled once per frame, repaint only fires on an actual flip.
- HTML `on-click="OnStart"` (and `on-input`, `on-change`, `on-submit`, …) call
  controller methods, optionally taking an event arg (`PointerEvent`,
  `InputEvent`, `KeyboardEvent`, `SubmitFormEvent`).
- Repeats: `<template data-each="Items as it" data-key="Id">`; class toggles:
  `data-class-foo="BoolPath"`. Full patterns in the AuthoringGuide §2-§3.

Key public API for programmatic work (`UIDocument.cs`):
`SetController` (380), `GetController<T>` (396), `GetElementById` (400),
`GetElementsByClassName` (404), `GetElementsByTagName` (409),
`MarkStyleDirty`/`MarkLayoutDirty` (414/419), `Rebuild` (375),
`NeedsRepaint` (170); accessors `CurrentState`, `LayoutEngine`, `Events`,
`Invalidation`, `Doc`, and the settable `ImageRegistry` (139).

### Images (`<img>`, `background-image`, `border-image`)

HTML/CSS reference an **image handle string**, never a path/GUID; your game
owns an `IImageRegistry` that resolves it (`Runtime/Paint/Images/`).

- `InMemoryImageRegistry` — tests/prototypes; `Register(handle, IImageSource)`,
  `Count`, `Version` (`InMemoryImageRegistry.cs:14-45`).
- `AddressablesImageRegistry` — production lazy-load; gated behind the
  `WEVA_ADDRESSABLES` define + the Addressables package
  (`AddressablesImageRegistry.Unity.cs`).
- Sources: `SpriteImageSource(Sprite)` (reads `sprite.border` → automatic
  9-slice), `Texture2DImageSource(Texture2D)`.

```csharp
doc.ImageRegistry = new InMemoryImageRegistry();
((InMemoryImageRegistry)doc.ImageRegistry).Register("ui/panel", new SpriteImageSource(panelSprite));
// <img src="ui/panel"> or  border-image-source: url(ui/panel)
```

**9-slice:** a Sprite with non-zero border auto-paints as 9 sub-quads (corners
at source size, edges/center stretch); `border-image-repeat: round` scales
tiles to a whole count. Source UVs are bottom-up to match the shader's
V-flipped sampling. See `Assets/UI/9slice-demo.html`.

### Fonts

`Weva.Text.Tmp.TmpFontAssetRegistry` (static; gated by `WEVA_TMP`):
`RegisterFontAsset(family, TMP_FontAsset)` and `AddFallback(family, …)` map a
TMP SDF asset to a CSS family name (`TmpFontAssetRegistry.cs:42/85`). Register
under `"sans-serif"`/`"system-ui"` to back generic text; add an emoji asset as
a fallback for pictographs. `Assets/Scripts/UitestController.cs` is a working
reference (Segoe UI + emoji fallback chain).

---

## 3. What it supports (broad strokes)

Treat this as the headline list; the authoritative per-feature matrix is
[`CSS_FEATURE_AUDIT.md`](CSS_FEATURE_AUDIT.md) and the deltas are in
[`CONFORMANCE.md`](CONFORMANCE.md).

- **Layout:** block + inline/IFC, flexbox, CSS Grid (incl. subgrid auto-tracks),
  absolute/fixed/sticky, floats + clear (multi-paragraph exclusion), tables
  (incl. collapsed-border winner rule), **viewport/root-level scrolling**, scroll
  snap, anchor positioning (L1), `aspect-ratio`, `fit-content()`, container queries.
- **Cascade/selectors:** full specificity, `var()` w/ cycle detection, `@media`,
  `@container` (range + nested), `@scope`, `@property` typed custom props,
  `@layer`, `@font-face` (full descriptor parse), `:has/:is/:where` (forgiving),
  attribute ops, state pseudo-classes, `revert`/`revert-layer`, `env()`,
  `color-mix()` (incl. wide-gamut), `light-dark()`.
- **Paint:** solid/linear/radial/conic gradients, border-radius, box-shadow
  (outset/inset), text-shadow, `filter:` chains incl. `blur()`, `backdrop-filter`
  (with same-frame backdrop), `clip-path` basic shapes, alpha/luminance masks,
  9-slice & `border-image`, transforms (2D), opacity, view transitions.
- **Animation:** transitions + `@keyframes` (incl. `var()` in keyframes,
  `transition-behavior: allow-discrete`); interpolable gradients.
- **Interaction:** forms (text/checkbox/radio/range/select/textarea/dialog/
  popover), IME, gestures (pan, contextual-menu), tooltips, context menus,
  virtualized lists, custom `<my-component>` templates with slots.

---

## 4. What it does NOT do (read before promising a feature)

Two classes: **(A) parses-but-no-effect / partial** (author CSS stays valid, but
pin the listed value if you depend on spec behavior), and **(B) GPU/render
limits** where the C# side is correct but pixels don't match. AuthoringGuide §17
lists the author-facing "intentionally not implemented" set.

### A. Intentionally out of v1 scope / partial (CSS-side)

- **Vertical writing modes** (`writing-mode: vertical-*`) — logical edges map,
  but inline shaping stays horizontal → effectively unsupported visually.
- **Bidi / complex-script shaping** (Arabic, Hebrew, Devanagari, CJK joining),
  dictionary `hyphens: auto` — LTR only; manual `&shy;` works.
- **Multi-column** (`columns`, `column-*`) and fragmentation
  (`break-before/after/inside`, `box-decoration-break: clone`) — none.
- **`content-visibility` / real `contain` layout-paint containment** — registered,
  influences stacking only.
- **`text-wrap: balance|pretty|stable`, `text-justify: inter-character`,
  `text-indent: hanging|each-line`** — parse, no effect.
- **3D transforms** (`perspective`, `transform-style`, `backface-visibility`) —
  cascade-only, no 3D paint path. 2D transforms paint.
- **`clip-path`** — `inset/circle/ellipse/polygon` only; no `path()/shape()/url()`.
- **`mask`** — solid/gradient layers composite; **URL-sourced masks don't sample
  source pixels** in the software path; URP uploads **only the first 4 mask layers**.
- **Container query units** (`cqw`/`cqh`/…) not registered; some font features
  (`font-feature-settings`, `font-variant-numeric`, `font-size-adjust`,
  `font-synthesis-*`) cascade but don't reshape text.
- **DOM-style transition/animation events** (`transitionend`, …) — read state
  from C# instead.

### B. GPU / render limits (C# correct, pixels diverge)

These are where "the paint command is right but the screen isn't" — surfaced
empirically and **not fully closeable without RenderDoc/pixel capture**:

- **`mix-blend-mode` / `background-blend-mode` / `isolation`** — keywords parse
  and create stacking contexts, but the URP compositor renders **as-if `normal`**
  (B24/B25). No real blend groups.
- **Gradient `background-position` / `background-size` tiling** — `EmitGradient`
  doesn't pack tile origin/size, so a tiled/clipped *background* gradient fills the
  whole box (GTILE-1, still open). _(The mask-path equivalent — tiled/sized
  radial-gradient masks, the css-effects polka-dot — is **fixed**: a radial mask's
  stop count now packs into `maskParams0.w` instead of being misread from the
  radius slot. Linear/conic/radial masks all tile correctly.)_
- **Very-low-alpha fills on rounded rects** — `background: rgba(…, ~0.04)` on a
  `border-radius` box can be culled to invisible; the threshold sits between ~4%
  (invisible) and ~30% (clearly visible) (CHIP-LOWALPHA). Author workaround: use
  ≥10% alpha for visible tints.
- ~~**SDF corner anti-aliasing bleed**~~ — **fixed**. `Weva_Coverage` sized the
  AA band from `fwidth(d)` (L1 norm), which over-widened the band on diagonal
  (corner) SDF gradients by up to √2 → a soft gray fringe on rounded corners.
  Now uses the Euclidean gradient `length(ddx(d),ddy(d))` for a uniform ~1px band
  at every orientation. Verified on `corner-probe.html`.
- **`backdrop-filter`** — blur + color chains render and now sample same-frame
  content; exact wide-radius edge expansion and `drop-shadow()` backdrop remain
  partial (B23).

> When asked to "fix" a B-class item: confirm the C# paint command first (dump
> via `RecordingBackend` / MCP PaintList inspection). If C# is correct, the fix is
> in the shader/URP pass and **requires a Unity Play-mode + RenderDoc session** —
> `ScreenCapture.CaptureScreenshotAsTexture()` returns null in edit mode, so you
> cannot pixel-verify headlessly. Say so rather than guessing.

---

## 5. Architecture map (verified)

Per-frame entry point: **`UIDocumentLifecycle.Update(state, controller, nowSeconds)`**
(`Runtime/Document/UIDocumentLifecycle.cs:28`), called from `UIDocument.Update()`
(`UIDocument.cs:242`). Every stage caches on input version numbers from the
`InvalidationTracker`; clean subtrees skip every stage (see PLAN.md §12).

```
HTML/CSS TextAssets
 → HtmlParser / CssParser            (Css/Parsing, Parsing)
 → Document tree (DOM)               (Dom/)
 → ComponentExpander                 (Components/)
 → CascadeEngine.ComputeAll          (Css/Cascade/)            → ComputedStyle
 → CssAnimationRunner                (Css/Animation/)
 → BoxBuilder → LayoutEngine.Layout  (Layout/)  block/flex/grid/IFC
        LayoutEngine.Layout(doc, styleOf, ctx, tracker)        (LayoutEngine.cs:319)
 → PositioningPass                   (Layout/Positioning/)     absolute/fixed/sticky
 → ScrollLayout (incl. RunViewportScroll)                      (Layout/Scrolling/)
 → BoxToPaintConverter.Convert       (Paint/Conversion/)       (BoxToPaintConverter.cs:518) → PaintList
 → BatchedURPRenderBackend → UIBatcher → UIRenderGraphPass     (Rendering/URP/) → instanced GPU draws
```

### Render backends (`IRenderBackend` implementations)

| Backend | File | Role |
|---|---|---|
| `RecordingBackend` | `Runtime/Paint/RecordingBackend.cs` | headless — records commands (tests, paint dumps) |
| `NullBackend` | `Runtime/Paint/NullBackend.cs` | headless — counts submits, no output |
| **`BatchedURPRenderBackend`** | `Runtime/Rendering/URP/BatchedURPRenderBackend.cs:16` | **current live URP path** → `UIBatcher` → `UIRenderGraphPass` |
| `URPRenderBackend` | `Runtime/Rendering/Backend/URPRenderBackend.cs:28` | legacy per-quad CommandBuffer path |
| `IMGUIDocumentRenderer` | `Runtime/Rendering/IMGUIDocumentRenderer.cs` | IMGUI debug/fallback |

`UIRenderGraphPass` (`Rendering/URP/UIRenderGraphPass.cs:21`) drains the batcher
into a StructuredBuffer of instances, chunking at `MaxInstancesPerDraw` (1024).

### Über-shader + instance layout

- `Weva-Quad.shader` (`Runtime/Rendering/Shaders/`) keyword toggles
  (`multi_compile_local`, lines 64-77): `_BRUSH_LINEAR`, `_BRUSH_RADIAL`,
  `_BRUSH_CONIC`, `_BORDERED`, `_SHADOW_OUTSET`, `_SHADOW_INSET`, `_TEXT`.
- `UIQuadInstance` (`Rendering/URP/UIQuadInstance.cs`): **`Float4Count = 57`**
  (line 120) = 228 bytes/quad. Slots: 0 pos/size, 1 radii, 2 color, 3 brush,
  4-9 borders, 10-12 2D transform rows, 13 clip-rect, 14-15 extra gradient stops,
  16-20 clip-shape, **21-56 four mask layers (9 slots each)**.
  _(AGENTS.md still says "slots 0..13 / `WEVA_INSTANCE_FLOAT4S`" — that is
  stale; trust `Float4Count = 57`.)_

---

## 6. Tooling & verification

- **Chrome-vs-Unity layout diff** (`Tools/Layout/*.mjs`): `extract-*.mjs` load a
  fixture in headless Chrome and dump `getBoundingClientRect` coords to
  `<name>.chrome-layout.json`; `diff-*.mjs` / `diff-assets.mjs` compare against the
  Unity dump. **Each fixture has its own viewport** baked into its chrome JSON —
  regenerate the Unity dump at the matching viewport before diffing or you get
  false deltas.
- **Headless Unity layout dump**: `RandhtmlLayoutDumpTest` (EditMode,
  `Tests/Editor/RandhtmlLayoutDumpTest.cs`) — `DumpCoords(fixture)` writes
  `Assets/UI/<fixture>.unity-layout.json` deterministically (stable fonts, single
  pass). Per-fixture entry methods (`DumpHudCoords`, `DumpStatsCoords`, …).
- **`Tools/BaselineGen`** (.NET CLI, no Unity): `verify` asserts golden images,
  default renders `Tests/Runtime/Goldens/Snippets/*.html` → `…/Goldens/Baselines/`.
- **DevTools overlay** (`Weva.DevTools.DevToolsOverlay`, F12 in play mode):
  outlines, dirty highlighter, hover inspector, perf (FPS/cascade/layout/paint ms,
  GC/frame, paint-cache hit ratio). Also `Window → Weva → DevTools` in-editor.
- **Hot reload** (`Weva.HotReload.HotReloadCoordinator`): edits to `.html`/`.css`
  reparse without a domain reload; controller + `[UIBind]` state survive.
- **Layout tracing** (`Weva.Diagnostics.UILayoutDiagnostics`, static):
  `Enabled`, `MatchClassContains`, `TraceFor(element, …)` for class-scoped layout
  traces (`Runtime/Diagnostics/UILayoutDiagnostics.cs:21`).

### Running tests

- Suites live in `Packages/com.wevaui/Tests/`: `Runtime/` (namespace
  `Weva.Tests.*`, asmdef `Weva.Tests.Runtime`) and `Editor/`
  (`Weva.Tests.EditorTests`, asmdef `Weva.Tests.Editor`).
- The Runtime suite is driven via **PlayMode** through the MCP
  (`run_tests({mode:"PlayMode", group_names:["Weva.Tests.<Namespace>"]})`,
  then poll `get_test_job`). EditMode hosts the editor-only dumps.
- `run_tests` **cannot start while the editor is in Play mode** — exit Play mode
  first. Full-suite batch (`TestVerifyAll`) runs ~6,500 tests via command-line
  Unity and is independent of the open editor.
- Golden baselines: `Tests/Runtime/Goldens/Baselines/`; regenerate with
  `WEVA_REGENERATE_GOLDENS=1` and review the pixel diff — never commit a golden
  regen in the same commit as the code that changed it.

---

## 7. Conventions that bite (carry over from AGENTS.md)

- **No `-unity-*` / vendor-prefixed CSS.** Real-web subset only. Unsupported →
  route a warning through `UICssDiagnostics`; never silently render wrong.
- **Cache keys are input versions, not dirty bits.** Mutating Box fields directly
  bypasses the DOM-mutation path — you must mark the tracker dirty or it never
  paints. Pooled objects (`BoxPool`/`PaintListPool`/`CommandPool`) must reset every
  field you add in `ResetForPool()`.
- **Keep headless code free of `UnityEngine`.** Unity deps live behind defines in
  `Rendering/URP/*`, `*.Unity.cs`, etc.
- **NUnit pitfalls** (have broken this build): `.Within` does not chain off a
  comparison constraint; `Does.Not.Contain` is substring-only.
- Commit each logical chunk as it lands; only commit verified fixes.

---

## 8. Where to look — quick index

| Need | Go to |
|---|---|
| Author HTML/CSS, controllers, forms, gestures | `Packages/com.wevaui/Documentation~/AuthoringGuide.md` |
| Focus / Tab + game-controller (gamepad) navigation | `AuthoringGuide.md` §18 + `Assets/Scripts/InputTestController.cs` |
| Change the engine (properties, paint, layout) | `AGENTS.md` |
| Architecture + roadmap rationale | `PLAN.md` |
| Per-feature spec deviations | `CONFORMANCE.md` |
| Full CSS capability matrix | `CSS_FEATURE_AUDIT.md` |
| Package overview + perf numbers | `Packages/com.wevaui/README.md` |
| End-to-end demo scene | `Packages/com.wevaui/Samples~/PhaseOneDemo/` |
| Dev demo + golden/perf calibration doc | `Assets/UI/randhtml.html` + `.css` |
