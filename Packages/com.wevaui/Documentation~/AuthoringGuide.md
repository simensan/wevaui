# Weva Authoring Guide

How to build UI with this package: HTML/CSS authoring patterns, controller
binding, events, forms, gestures, and the runtime API. This is the manual
for **users of the package**; engine internals live in `AGENTS.md` at the
repo root.

For the supported HTML/CSS feature subset, see the package
[`README.md`](../README.md).

---

## 1. Five-minute quick start

1. **Drop a WevaDocument into the scene.** GameObject menu → Weva →
   New WevaDocument creates a GameObject with a `WevaDocument`,
   `IMGUIDocumentRenderer` (debug mode), and `DevToolsOverlay`.
   `UnityInputController` is attached automatically at play time by
   `WevaDocument.OnEnable`.

2. **Author your UI.** Two `TextAsset`s (`.html` + `.css`) imported from
   `Assets/UI/`:

   ```html
   <!-- Assets/UI/menu.html -->
   <link rel="stylesheet" href="menu.css" />
   <main class="menu">
     <h1>Welcome, {{ PlayerName }}</h1>
     <p>Coins: <span class="coins">{{ CoinCount }}</span></p>
     <button on-click="OnStart">Start</button>
     <button on-click="OnQuit" disabled="{{ QuitDisabled }}">Quit</button>
   </main>
   ```
   ```css
   /* Assets/UI/menu.css */
   .menu { display: flex; flex-direction: column; gap: 16px; padding: 24px; }
   button { padding: 8px 16px; border-radius: 8px; background: #4f46e5; color: white; }
   button:hover { background: #6366f1; }
   button:disabled { opacity: 0.5; cursor: not-allowed; }
   .coins { color: gold; font-weight: 600; }
   ```

3. **Wire a controller.**

   ```csharp
   using UnityEngine;
   using Weva;
   using Weva.Binding;

   public sealed class MainMenuController : MonoBehaviour {
       [UIBind] public string PlayerName = "Aerith";
       [UIBind] public int CoinCount = 0;
       [UIBind] public bool CanQuit = true;
       // Bindings are plain identifiers / dotted paths only — no `!`, operators,
       // or method calls in `{{ }}`. Expose a computed [UIBind] property instead:
       [UIBind] public bool QuitDisabled => !CanQuit;

       WevaDocument doc;
       void Awake() { doc = GetComponent<WevaDocument>(); doc.SetController(this); }

       public void OnStart() { CoinCount += 5; }   // mutating a [UIBind] field
       public void OnQuit()  { Application.Quit(); } // re-renders the {{ }} on its next read
   }
   ```

   Drop the script next to the WevaDocument and assign the HTML/CSS
   `TextAsset`s on the inspector. Press play.

That's the whole loop. Hot-reload picks up edits to the `.html`/`.css`
without restarting play mode.

## 2. Data binding

Every `[UIBind]` field/property on the controller is reachable via
`{{ Name }}` in HTML and CSS-attribute values. Bindings are polled once
per frame from `UIDocumentLifecycle.Update`, write-only when changed, and
the only frames that re-paint are ones where a binding actually flipped.

```csharp
public class HUDController : MonoBehaviour {
    [UIBind] public int    HP;
    [UIBind] public int    MaxHP;
    [UIBind] public string PartyLeader;
    [UIBind] public bool   IsCriticalHP => HP < MaxHP / 4;
    [UIBind] public string HpPctStyle => $"--pct:{(HP * 100.0 / MaxHP):F0}%";
}
```

```html
<div class="hud">
  <div class="bar hp" style="{{ HpPctStyle }}">
    <span class="num">{{ HP }} / {{ MaxHP }}</span>
  </div>
  <div class="leader" data-critical="{{ IsCriticalHP }}">{{ PartyLeader }}</div>
</div>
```
```css
.bar.hp .fill { width: var(--pct, 0%); transition: width 0.2s ease; }
.leader[data-critical="True"] { color: #f87171; }
```

Notes:

* Templates support attribute interpolation (`disabled="{{ QuitDisabled }}"`)
  and dotted property paths (`{{ Inventory.Gold }}`). Binding expressions
  are plain identifiers or dotted paths only (plus `$index`) — no negation,
  operators, or method calls. To format or compute a value, expose a
  computed `[UIBind]` property and bind it by name:
  `[UIBind] string FormattedGold => FormatCurrency(Inventory.Gold);` then
  `{{ FormattedGold }}`.
* The string `"True"`/`"False"` is what `bool` interpolation produces; CSS
  attribute selectors should match those literals.
* Mutating a `[UIBind]` field anywhere in user code is enough — the next
  frame's `BindingSet.Update` notices and queues a paint.

Repeated lists can be authored with a template:

```html
<ol class="stages">
  <template data-each="Stages as stage" data-key="Id">
    <li class="stage-card" data-class-selected="stage.IsSelected">
      <strong>{{ $index }}. {{ stage.Name }}</strong>
      <span>{{ stage.Score }}</span>
    </li>
  </template>
</ol>
```

`data-each="<items> as <alias>"` reads any `IEnumerable` binding and clones
the template body once per item. `data-key` gives each clone stable identity
so reorders reuse existing DOM nodes instead of clearing the container.
Inside the template, `{{ stage.Name }}` resolves against the item, parent
controller bindings still resolve normally, and `$index` exposes the
zero-based item index.

Use `data-class-<name>="BindingPath"` for boolean class toggles. The binding
adds or removes only that one class and leaves the rest of the element's
`class` attribute intact.

For changes outside `[UIBind]` (programmatic DOM mutation), see §6.

## 3. Events

`on-<event>="MethodName"` attributes call methods on the controller:

```html
<button on-click="OnStart">Start</button>
<input type="text" on-input="OnSearch" on-keydown="OnSearchKey" />
<form on-submit="OnLogin">…</form>
```

Method signatures:

```csharp
public void OnStart() { … }
public void OnStart(PointerEvent e) { … }   // optional event arg
public void OnSearch(InputEvent e) { … }
public void OnSearchKey(KeyboardEvent e) { if (e.Key == "Enter") DoSearch(); }
public void OnLogin(SubmitFormEvent e) { … }
```

The arg types live in two namespaces: `PointerEvent` / `KeyboardEvent` /
`WheelEvent` are in `Weva.Events`; `InputEvent` / `SubmitFormEvent` are in
`Weva.Forms`. Add the `using` for whichever your handler signature names.

Recognized event kinds (see `Runtime/Events/EventKind.cs`):
`PointerDown` / `PointerUp` / `PointerMove` / `PointerEnter` / `PointerLeave` /
`Click` / `KeyDown` / `KeyUp` / `KeyPress` / `Focus` / `Blur` / `Change` /
`Input` / `Submit` / `Wheel` / `Scroll`.

Standard CSS state pseudo-classes (`:hover`, `:focus`, `:focus-visible`,
`:active`, `:disabled`, `:checked`, `:placeholder-shown`,
`:focus-within`) flip automatically based on event-driven state — no
controller code needed.

**UI sound effects.** Weva has no audio system of its own — play click/hover
SFX from the event handler like any other game code:

```csharp
public void OnStart(PointerEvent e) { sfx.PlayOneShot(clickClip); StartGame(); }
```

For hover sounds, handle `on-pointerenter`. Centralize it by wiring one
`PointerDown`/`PointerEnter` listener at the document root in C# and checking
the target's class, rather than adding `on-` attributes to every button.

## 4. Forms

| Element | Behavior |
|---|---|
| `<input type="text">` / `password` / `email` / `number` / `search` / `tel` / `url` | Typed text input with click/drag caret + selection. IME composition is not wired in v1 (the `ImeSession` model exists; the Unity bridge is not yet instantiated), so CJK composition input is unavailable. |
| `<input type="checkbox">` | Toggleable boolean. `checked=""` reflects state. |
| `<input type="radio" name="g">` | Group-exclusive (one per `name=` selected). |
| `<input type="range" min max step value>` | Slider with thumb-drag, click-track, keyboard arrows / PageUp / Home. |
| `<input type="hidden">` | Form data only, no rendering. |
| `<select>` + `<option>` | Single-select dropdown source. (Visible popup is `ContextMenu` — see §7.) |
| `<textarea>` | Multi-line text input. |
| `<button>` / `<button type="submit">` | Click target; submit triggers enclosing form. |
| `<form on-submit="…">` | Captures Enter inside text inputs and submit clicks; collect via `FormElement.CollectFormData()`. |
| `<dialog>` / `<dialog open>` | Modal / non-modal dialog. `DialogElement.ShowModal()` opens. |
| `<details>` / `<summary>` | Collapsible group (UA stylesheet handles `[open]` toggle visuals; controller wiring TBD). |
| `[popover]` attribute | Light-dismissable popup. `PopoverController` handles outside-click + Escape. |

Programmatic access:

```csharp
var range = doc.GetElementById("volume");
var rc = new Weva.Forms.RangeController(range, doc.Events, e => doc.CurrentState.ElementToBox.Lookup(e));
rc.Wire();
rc.ValueChanged  += () => Debug.Log($"slid: {rc.Value}");
rc.ValueCommitted += () => Save();   // pointer-up or keyboard step
```

Sliders, checkbox toggle, select option pick, etc. all dispatch standard
`input` (per-change) and `change` (committed) events you can subscribe via
`on-input` / `on-change` HTML attributes or
`dispatcher.AddEventListener(elem, EventKind.Input, listener)` from C#.

## 5. Gestures (Manipulators)

Drag, right-click, long-press → high-level callbacks without writing the
pointer-down/move/up plumbing yourself:

```csharp
using Weva.Events.Manipulators;

var canvas = doc.GetElementById("paint-canvas");
var pan = new PanManipulator(canvas, doc.Events) { Threshold = 4 };
pan.PanStart += (sx, sy) => StartStroke(sx, sy);
pan.PanMove  += (dx, dy) => ExtendStroke(dx, dy);
pan.PanEnd   += (tx, ty) => EndStroke(tx, ty);
pan.Wire();

var slot = doc.GetElementById("slot-7");
var ctx = new ContextualMenuManipulator(slot, doc.Events) { LongPressSeconds = 0.5 };
ctx.MenuRequested += (x, y) => ShowSlotMenu(x, y);
ctx.Wire();
```

`PanManipulator` calls `dispatcher.SetPointerCapture(target)` so drags
survive leaving the source element. `ContextualMenuManipulator` fires on
right-click (PointerDown button 2), Shift+F10 keyboard shortcut, and
long-press touch.

## 6. Programmatic DOM updates

Anything beyond polled `[UIBind]` fields:

```csharp
// Get an element
var coins = doc.GetElementById("coins");
var slots = doc.GetElementsByClassName("slot");

// Mutate text
foreach (var c in coins.Children)
    if (c is Weva.Dom.TextNode tn) tn.Data = newValue.ToString();

// Mutate attributes (style, class, data-*, etc.)
slot.SetAttribute("style", $"--pct:{pct}%");
slot.SetAttribute("data-rarity", "epic");
slot.RemoveAttribute("disabled");

// Add/remove children
var newRow = new Weva.Dom.Element("div");
newRow.SetAttribute("class", "log-line");
newRow.AppendChild(new Weva.Dom.TextNode(message));
logContainer.AppendChild(newRow);

logContainer.RemoveChild(oldestRow);
```

DOM mutations fire bubbling `Mutated` events; the `InvalidationTracker`
catches them and queues the affected stages. The "skip paint when idle"
optimization correctly re-enables paint on the next frame.

## 7. Pop-ups, dropdowns, context menus

```csharp
using Weva.Forms;

ContextMenu.Show(doc.Doc, doc.Events, doc.CurrentState.Invalidation, x, y, new[] {
    MenuItem.Item("Equip",      () => Equip(slot),  shortcut: "E"),
    MenuItem.Item("Inspect",    () => Inspect(slot)),
    MenuItem.Separator(),
    MenuItem.Item("Drop",       () => Drop(slot),   disabled: !canDrop),
});
```

Dismissed automatically on outside click, Escape, or item activation.
ArrowUp/Down + Home/End + Enter navigate the menu via keyboard. Restyle
by targeting `.ui-menu`, `.ui-menu-item`, `.ui-menu-separator` in your CSS.

## 8. Tooltips

Set `title="…"` on any element. Construct one `TooltipManager` per
document at startup:

```csharp
var st = doc.CurrentState;
var tt = new Weva.Forms.TooltipManager(st.Doc, doc.Events, st.Clock, st.Invalidation) {
    ShowDelaySeconds = 0.6
};
tt.Wire();
```

The manager injects a `<div class="ui-tooltip">…</div>` into the document
when the pointer rests on a `title`-bearing element for the configured
delay. Restyle via `.ui-tooltip` in your CSS.

## 9. Virtualized lists

Render only the visible window of a 100k-row data source:

```csharp
using Weva.Forms;

var listHost = doc.GetElementById("inventory-list");
var st = doc.CurrentState;
var ctl = new VirtualListController<Item>(
    listHost,
    itemHeight: 32,
    elementToBox: e => st.ElementToBox.Lookup(e),
    scrollContainer: doc.LayoutEngine.ScrollContainer,
    tracker: st.Invalidation
) {
    Source = inventory,
    ItemTemplate = (i, item) => {
        var row = new Weva.Dom.Element("div");
        row.SetAttribute("class", "row");
        row.AppendChild(new Weva.Dom.TextNode($"{item.Name} ×{item.Count}"));
        return row;
    },
};

void Update() { ctl.Tick(); }   // call every frame from a MonoBehaviour
```

The host should be `overflow: auto` with explicit dimensions so it has a
scroll viewport. Items must have a known fixed height. Variable-height
support is a v2 follow-up.

## 10. Custom components

Reusable element templates with slots:

```html
<template id="hp-pill">
  <span class="icon">♥</span>
  <span class="num"><slot></slot></span>
</template>
```

```html
<hp-pill>{{ HP }}</hp-pill>
<hp-pill>{{ MaxHP }}</hp-pill>
```

Large components can live next to the document and be imported before the
component registry scans the DOM:

```html
<!-- menu.html -->
<template src="stage-card.html"></template>
<stage-card>Forest Gate</stage-card>
```

```html
<!-- stage-card.html -->
<template id="stage-card">
  <article class="stage-card"><slot></slot></article>
</template>
```

If the importing template already has an id, its body is filled from the
external file instead:

```html
<template id="stage-card" src="stage-card-body.html"></template>
```

`ComponentExpander` expands `<hp-pill>` instances into the template's
content with per-instance slot fills. Put component styles in the regular
`.css` asset for v1:

```css
hp-pill { display: inline-flex; align-items: center; gap: 4px; }
hp-pill .num { font-variant-numeric: tabular-nums; }
```

Inline `<style>` blocks inside templates are parsed as HTML but are not wired
into the cascade yet. Code that registers a scoped stylesheet through
`ComponentRegistry.Register(tag, template, stylesheet)` can use `:host` and
the component selector scoper directly.

## 11. Layout patterns

### Flex (most common)

```css
.row     { display: flex; gap: 8px; align-items: center; }
.column  { display: flex; flex-direction: column; gap: 12px; }
.spacer  { flex: 1; }                     /* push siblings to the edges */
```

### Grid

```css
.toolbar {
  display: grid;
  grid-template-columns: auto 1fr auto;   /* left / center-stretch / right */
  align-items: center;
  gap: 12px;
}
```

### Sticky positioning

```css
.list-header {
  position: sticky;
  top: 0;
  background: var(--surface);
  z-index: 1;
}
```

### Container queries (responsive without media queries)

```css
.card-container { container-type: inline-size; }
@container (min-width: 320px) {
  .card { display: grid; grid-template-columns: 1fr 1fr; }
}
@container (max-width: 319px) {
  .card { display: flex; flex-direction: column; }
}
```

### Anchor positioning

```css
.tooltip {
  position: absolute;
  position-anchor: --slot-7;
  bottom: anchor(top);
  left: anchor(center);
  translate: -50% -8px;
}
.slot[data-id="7"] { anchor-name: --slot-7; }
```

## 12. CSS variables for runtime theming

```css
:root {
  --color-primary: #4f46e5;
  --color-text: #f8fafc;
  --space-md: 12px;
}
button { background: var(--color-primary); color: var(--color-text); padding: var(--space-md); }
```

Mutate at runtime to retheme:

```csharp
doc.GetElementsByTagName("html").First().SetAttribute("style", "--color-primary: #ef4444;");
```

## 13. Images and the image registry

`<img>` (and CSS `background-image` / `border-image-source`) reference assets
through an **image handle string** — a stable address the engine looks up in
an `IImageRegistry` your game owns. The HTML never names a file path or
GUID directly; the registry decides what `"ui/heart"` actually resolves to.

### Pick a registry

The package ships two implementations:

| Registry                    | When to use                                                       |
| --------------------------- | ----------------------------------------------------------------- |
| `InMemoryImageRegistry`     | Tests, prototypes, or assets you load yourself and push in        |
| `AddressablesImageRegistry` | Production — handles resolve to Addressables addresses; lazy load |

**Recommendation:** for any shipping game with more than a handful of icons,
use `AddressablesImageRegistry`. It loads sprites on first paint, retains
ref-counted handles for the registry lifetime, and bumps `Version` so paint
caches refresh when an async load completes.

### Using `AddressablesImageRegistry`

1. In **Project Settings → Player → Other Settings → Scripting Define
   Symbols**, add `WEVA_ADDRESSABLES` (the registry is compile-gated so
   projects without the Addressables UPM package still compile).

2. Install `com.unity.addressables` via Package Manager.

3. Mark sprites Addressable (right-click → Mark as Addressable) and pick
   short, stable addresses like `ui/heart`, `SkillIcons/Stimpack`. These
   strings go straight into your HTML.

   ```html
   <img src="ui/heart" />
   <div style="border-image-source: url(ui/panel-frame); border-image-slice: 16;">
     ...
   </div>
   ```

4. Wire it once per `WevaDocument`:

   ```csharp
   void OnEnable() {
       var doc = GetComponent<WevaDocument>();
       doc.ImageRegistry = new AddressablesImageRegistry();
       doc.SetController(this);
   }
   ```

That's it — every `<img>` and every CSS `url(...)` reference now lazy-loads
through Addressables. No `Register(...)` calls per asset, no manual
`LoadAssetAsync` boilerplate.

### Mixing Addressable and non-Addressable sources

For sprites that don't live in Addressables (e.g. a runtime-generated
texture, or a sprite from a ScriptableObject manager), call `Register` on
the same registry — it accepts pre-loaded sources too:

```csharp
var registry = new AddressablesImageRegistry();
// Manually-loaded sprite — bypasses Addressables for this handle:
registry.Register("ui/runtime-thumbnail", new SpriteImageSource(thumbnailSprite));
// Other handles still load lazily through Addressables:
// <img src="ui/heart"> → AddressablesImageRegistry.LoadAssetAsync("ui/heart")
```

### Preloading

For known-up-front asset sets (main menu icons, all skill icons), preload
to avoid the first-paint flash:

```csharp
await registry.PreloadAsync(new[] { "ui/heart", "ui/star", "ui/coin" });
```

### 9-slice frames

When a Sprite is configured with **border** values in the Sprite Editor
(green handles in the inspector preview), an `<img>` referencing that
sprite automatically paints as 9 sub-quads — corners stay at source-pixel
size, edges and center stretch. No CSS needed. Combine with
`object-fit: fill` if you don't want 9-slice; without it the auto path
fires when the sprite has borders.

For CSS `border-image`, the sprite's border supplies the slice values
when `border-image-slice` is omitted or set to `100% fill`. See
`Assets/UI/9slice-demo.html` for a side-by-side demo.

## 14. Performance

* Idle frames cost ~1 ms total. The paint pass short-circuits when nothing
  in the document is dirty, so a static UI doesn't re-walk its tree every
  frame.
* Avoid mutating attributes every frame from `Update()`. Use `[UIBind]`
  fields whose backing data only changes when state changes; the binding
  layer dirty-checks before writing.
* Heavy `box-shadow` / `filter: blur()` / `text-shadow` are the most
  expensive painters. Use sparingly on elements that change often.
* Run `Tools/PerfBench/` to baseline cascade / layout / paint cost on your
  machine if you suspect regressions.
* The `DevToolsOverlay` (F12 in play mode) shows per-frame cascade /
  layout / paint ms in the bottom corner.

## 15. DevTools

### The Elements window (Window → Weva → Elements)

A Chrome DevTools "Elements"-style panel that works in **edit mode**
(against the edit-mode preview) and in play mode:

* **DOM tree.** Live Chrome-style tree of the active document
  (`<div id="x" class="card">`, quoted text previews, whitespace-only
  text nodes hidden). Auto-refreshes on DOM mutation; the search field
  filters by label. The document picker auto-finds the first
  `WevaDocument` in the scene.
* **Styles.** Matched rules exactly as Chrome presents them — rule
  blocks winner-first (`element.style`, then author/UA in cascade
  order), the authored selector text, origin label right-aligned,
  overridden declarations dimmed with a leading `~`.
* **Computed.** Box-model diagram (margin / border / padding / content
  with numbers) above the alphabetized computed list, including custom
  properties; filterable.
* **Selection highlight.** Selecting a node tints the element in the
  Game view with Chrome's box-model overlay (orange margin / yellow
  border / green padding / blue content), rendered through the engine's
  own paint pipeline.
* **Pick (⊕).** Click an element in the Game view to select it in the
  tree. Works in edit mode (clicks are captured off the Game view
  window; correct at the default fit zoom) and in play mode (clicks are
  consumed so picking never also presses app buttons).

### The in-game overlay (F12)

Press F12 in play mode for an IMGUI overlay with four modes (cycle with
keys configured on the overlay component):

* **Outlines.** Margin / border / padding / content rectangles in Chrome
  DevTools colors.
* **Dirty highlighter.** Boxes flash red / yellow / gray as they re-layout
  / re-style / re-paint.
* **Hover inspector.** `<button.btn-primary#start>` header + dimensions +
  10 most-relevant computed style props.
* **Performance.** FPS, frame ms, cascade / layout / paint ms, GC bytes/frame,
  paint cache hit ratio.

There's also `Window → Weva → DevTools` for editor-time perf/cache
readouts without entering play mode.

## 16. Hot reload

Edit a `.html` or `.css` `TextAsset` while play mode is running; the
watcher picks it up, reparses, and rebuilds without a domain reload.
Controller state and `[UIBind]` field values survive the reload.

For programmatically constructed UI (no source `.html` file),
`doc.Rebuild()` re-runs the pipeline from scratch.

## 17. CSS features intentionally not implemented

A handful of spec features parse without error but won't be wired up — they target prose layouts or browser scenarios that game UI doesn't reach. If your stylesheet uses them, the declarations stay valid but have no visible effect.

- **Transition / animation events** (`transitionstart`, `transitionend`, `animationstart`, etc.) — read state directly from C# instead of subscribing to DOM-style events.
- **`overflow-anchor` / scroll anchoring** — automatic scroll re-positioning when content is inserted above the viewport. Built for browser dynamic loads; game UI rebuilds its trees explicitly.
- **`text-wrap: balance | pretty | stable`** — multi-line balanced wrap and last-line wrap-quality controls. Game UI typically sizes copy to fit by design. `text-wrap: nowrap` and `wrap` work.
- **`text-justify: inter-character`** — CJK per-grapheme justification. `text-justify: auto` and `inter-word` (the default for Latin) are honored.
- **`text-indent: hanging` / `each-line`** — modifier keywords on `text-indent`. The bare length/percentage value is honored; the modifiers parse without effect.
- **SVG `url(#id)` filter references inside `filter:`** — the function is parsed and skipped silently so other filter functions in the same declaration still apply.
- **`@namespace` at-rule** — parses but no XML namespace map. Namespace-prefixed selectors (`svg|circle`, `[xlink|href]`) accept the syntax and match on the local name.
- **Side-placed table captions in vertical writing modes** — `caption-side: inline-start | inline-end` (and `block-start`/`block-end` in vertical modes) fall through to top-placement. Use `caption-side: top` / `bottom`.

### Partial support — accepts the syntax, behaves differently than the spec

These features parse cleanly so author stylesheets stay valid, but the run-time semantics are reduced. Pin the listed values if your design depends on the spec behavior.

- **`:nth-child(... of <selector>)`** — the `of <selector>` filter parses but is silently dropped; the index counts ALL children. Use a more specific selector instead.
- **`@import url(x) layer(name)` / `@import url(x) supports(condition)`** — the qualifier parses but the spliced rules are NOT wrapped in the named layer or gated by the supports condition. Move layered/conditional rules into the importing sheet.
- **`isolation: isolate`** — `mix-blend-mode` itself is fully supported (all modes blend against everything painted before the element, including same-frame UI, in sRGB, Chrome-matched), but isolated blend groups are not bounded yet: a blended element inside an `isolation: isolate` (or opacity/filter-isolated) ancestor still blends against the full backdrop instead of stopping at the group.
- **`animation-composition: add | accumulate`** — registered and cascades, but the runner composes as `replace` regardless. Use separate animation properties for combined effects.
- **`linear(...)` easing inside `animation:` / `transition:` shorthand** — the shorthand parsers don't recognise `linear(` and silently drop the easing. Bare `linear` works in the shorthand; use the `animation-timing-function` / `transition-timing-function` longhand for `linear(...)`.
- **Smooth interpolation for `box-shadow` / `text-shadow` / `clip-path` / `background-position` / `background-size`** — these animate discrete (snap from start to end). Use simpler interpolable properties (`opacity`, `transform`, `color`) when you need smooth tweening.
- **`calc()` referencing channel identifiers in relative-color syntax** — `rgb(from var(--c) r g b)` works (literal-channel form); `rgb(from var(--c) calc(r + 20) g b)` does not (the channel identifier doesn't resolve inside `calc`). Compute the channels in C# or use direct numeric values.
- **3D transform properties** — `perspective`, `transform-style`, `backface-visibility`, `perspective-origin` are registered (cascade-only) so author stylesheets pasted from the web stay valid and the values round-trip through `style.Get(name)`, but Weva's URP paint pipeline has no 3D path so the values have no visible effect. 2D `translate`/`rotate`/`scale` and the `transform` shorthand paint normally.
- **Container query units** (`cqw`, `cqh`, `cqi`, `cqb`, `cqmin`, `cqmax`) — not registered. Use viewport units (`vw`/`vh`) or container queries with explicit length values.
- **`overflow-clip-margin: <visual-box> <length>`** — the `<visual-box>` argument (`padding-box 8px`, `content-box 4px`) is not parsed. The bare length form works; per-side longhands also work.
- **Default gradient color-interpolation** — engine defaults to sRGB; spec defaults to oklab. The `in <color-space>` override (e.g. `linear-gradient(in oklab, red, blue)`) is honored — use it explicitly if you need oklab mid-stops.
- **`visibility: collapse` on `<col>` / `<colgroup>` / `<thead>` / `<tbody>` / `<tfoot>`** — only row-level `visibility: collapse` is implemented. Drop columns or row groups from the DOM instead.
- **`env()` function** — parses and resolves like `var()`, but reads from a runtime registry rather than `--custom-property` declarations. The four canonical safe-area names (`safe-area-inset-top|right|bottom|left`) are pre-registered to `0px` (Weva runs in a fixed viewport with no notch by default); an unknown name falls back to its fallback argument if supplied, otherwise the declaration becomes invalid-at-computed-value-time. Games that DO care (e.g. iOS shipping titles) can register values at startup via `Weva.Css.Cascade.EnvironmentVariables.Register(name, value)`; subsequent cascades pick up the new value.

If you need one of the strict "not implemented" items above, file an issue with a use case — they were excluded based on game-UI authoring patterns, not technical impossibility. Partial-support items are tracked for future expansion.

## 18. Focus & controller (gamepad) navigation

### Keyboard focus — works out of the box

When a `WevaDocument` has the built-in input controller attached (added
automatically by `WevaDocument.OnEnable`, or explicitly as
`Weva.Forms.Bridge.UnityInputController`), **Tab / Shift+Tab move focus**
through the document with no code:

* Natively focusable elements: `<button>`, `<input>`, `<select>`,
  `<textarea>`, `<a href>`. Add `tabindex="0"` to make any element focusable,
  `tabindex="-1"` for programmatic-only focus, or a positive `tabindex` to
  force order. `disabled` and `display:none` / `visibility:hidden` elements
  are skipped.
* Style the focused control with `:focus` / `:focus-visible` (§3) — a visible
  focus ring is the whole point of controller-navigable UI.

```css
.btn:focus { box-shadow: 0 0 0 3px #36e0ff; }   /* shows where the pad landed */
```

### Activating the focused control

Pointer clicks fire `Click` automatically. To activate the **focused** element
from a key or gamepad button, dispatch a synthetic click at its centre — this
drives `:active` and any `on-click` handler:

```csharp
var f = doc.Events.FocusedElement;
var box = doc.CurrentState.ElementToBox.Lookup(f);   // layout box
// accumulate absolute centre from box.X/Y up the parent chain, then:
doc.Events.DispatchPointerDown(cx, cy, 0, default);
doc.Events.DispatchPointerUp(cx, cy, 0, default);
```

### Gamepad / directional navigation — you wire it

Spatial (d-pad / stick) navigation is **not** automatic. Create a
`Weva.Events.DirectionalNavigation` for the document and feed it input each
frame. It picks the nearest focusable in a direction using the elements' layout
rects:

```csharp
using Weva.Events;

var nav = new DirectionalNavigation(doc.Events, doc.Doc, NavRectOf) {
    // Skip display:none / visibility:hidden (e.g. a closed menu's items).
    IsHidden = e => { var s = doc.Cascade?.GetComposedStyle(e, doc.State);
                      return s != null && (s.Get("display")=="none" || s.Get("visibility")=="hidden"); }
};
doc.Events.IsHidden = nav.IsHidden;   // make Tab honour the same hidden test

// Each frame, from your input source (new Input System shown):
var pad = UnityEngine.InputSystem.Gamepad.current;
if (pad != null) {
    var v = pad.dpad.ReadValue();
    if (v.x >  0.5f) nav.MoveFocus(NavDirection.Right);
    if (v.x < -0.5f) nav.MoveFocus(NavDirection.Left);
    if (v.y >  0.5f) nav.MoveFocus(NavDirection.Up);
    if (v.y < -0.5f) nav.MoveFocus(NavDirection.Down);
    if (pad.buttonSouth.wasPressedThisFrame) /* activate focused — see above */;
}

// NavRectOf maps an element to its absolute layout rect:
NavRect? NavRectOf(Element e) {
    var b = doc.CurrentState?.ElementToBox?.Lookup(e); if (b == null) return null;
    double x = 0, y = 0; for (var n = b; n != null; n = n.Parent) { x += n.X; y += n.Y; }
    return new NavRect(x, y, b.Width, b.Height);
}
```

Add edge-repeat (hold-to-repeat with a delay) for held directions, and call
`doc.Events.Focus(firstButton)` once so the first input has an anchor.

**Full working reference (repo checkout only):** `Assets/inputtest.unity` +
`Assets/Scripts/InputTestController.cs` — a focus test bench with
d-pad/stick/arrow nav, A/Enter/Space activate, B/Esc to close an overlay menu,
and a focus-trap. These live in the repo, not in the distributed package; the
packaged `Samples~/PhaseOneDemo/` is the equivalent for package consumers.

## 19. Localization & RTL

There is no translation system baked in — localized strings are **just
bindings**. Expose the resolved string as a `[UIBind]` property backed by your
localization table and bind it by name; swapping the active language and
re-reading the field repaints the next frame:

```csharp
[UIBind] public string StartLabel => Loc.Get("menu.start");   // table lookup
public void SetLanguage(string lang) { Loc.Active = lang; }   // next frame re-binds
```

```html
<button on-click="OnStart">{{ StartLabel }}</button>
```

Format numbers/dates the same way — compute a culture-formatted string in a
`[UIBind]` property (`{{ }}` takes plain paths only, no method calls).

**RTL — what works and what doesn't.** Layout-level RTL is supported:
`direction: rtl` flips the inline axis, logical properties
(`margin-inline-start`, `inset-inline-end`, …) resolve per-direction, and
`text-align: start | end` follow the direction. **Glyph-level bidi reordering
is a v1 non-goal** — a line mixing LTR and RTL runs (Arabic/Hebrew with Latin)
is not reordered, so true RTL-script text isn't correctly shaped yet. Vertical
writing modes are likewise not implemented. Plan around this if you target RTL
languages; see [Text layout (css-text)](css-text.md).

## 20. Where to look next

* **Package [`README.md`](../README.md)** — supported HTML/CSS subset,
  architecture overview, performance numbers.
* **`Samples~/PhaseOneDemo/`** — an end-to-end demo scene; import via
  Package Manager → Weva → Samples. This is the example that ships *in* the
  package — start here.
* **`Assets/UI/randhtml.html` + `randhtml.css`** *(repo checkout only)* — the
  dev demo this repo's golden tests + perf benches calibrate against. A useful
  real-world example of HUD / quest log / chat / map widgets, if you've cloned
  the repo.
