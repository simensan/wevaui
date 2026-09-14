# Weva Authoring Guide

The task-oriented manual for building UI with Weva: a page is standard HTML
and CSS, a controller is a C# object, and the engine — the shared C++ core
hosted by `WevaDocument` — keeps the two in step. For *what* HTML and CSS the
engine supports, see the reference pages ([index](index.md)); this page is
about *how* to wire a page to a game.

---

## 1. Five-minute quick start

1. **Author the page.** Drop `menu.html` and `menu.css` into `Assets/UI/`
   (they import as `TextAsset`s):

   ```html
   <link rel="stylesheet" href="menu.css" />
   <main class="menu">
     <h1>{{ PlayerName }}'s Game</h1>
     <p class="coins">Coins: {{ CoinCount }}</p>
     <button id="start" on-click="OnStart">Start</button>
     <button id="quit" on-click="OnQuit" disabled="{{ QuitDisabled }}">Quit</button>
   </main>
   ```
   ```css
   .menu { display: flex; flex-direction: column; gap: 16px; padding: 24px; }
   button { padding: 8px 16px; border-radius: 8px; background: #4f46e5; color: white; }
   button:hover { background: #6366f1; }
   .coins { color: gold; font-weight: 600; }
   ```

2. **Mount it.** GameObject → Weva → New WevaDocument; drag `menu.html` onto
   **Document Asset**. The `<link>` finds `menu.css` next to it.

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

       public void OnStart() { CoinCount += 5; }    // the {{ }} follows next frame
       public void OnQuit()  { Application.Quit(); }
   }
   ```

   Press play. That's the whole loop; edits to the `.html`/`.css` hot-reload
   without leaving play mode.

## 2. Data binding

Every `[UIBind]` field or property on the controller is a **binding root**,
reachable from `{{ Name }}` in HTML text and attribute values. Only the first
segment of a path needs the attribute; the rest walks public fields and
properties, dictionary keys and list indices:

```csharp
public class HUDController : MonoBehaviour {
    [UIBind] public Stats Stats;                              // {{ Stats.Health }}
    [UIBind] public int    MaxHP = 100;
    [UIBind] public bool   IsCriticalHP => Stats.Health < MaxHP / 4;
    [UIBind] public string HpPctStyle => $"--pct:{(Stats.Health * 100.0 / MaxHP):F0}%";
    [UIBind] public List<Quest> Quests;                       // data-each
}
```

```html
<div class="hud">
  <div class="bar hp" style="{{ HpPctStyle }}">
    <span class="num">{{ Stats.Health }} / {{ MaxHP }}</span>
  </div>
  <div class="leader" data-class-critical="IsCriticalHP">{{ PartyLeader }}</div>
</div>
```
```css
.bar.hp .fill { width: var(--pct, 0%); transition: width 0.2s ease; }
.leader.critical { color: #f87171; }
```

How values read:

* A `bool` interpolates as `true` / `false` (lower case, as in a browser's
  `String(true)`); numbers read invariant-culture; anything else `ToString()`.
* Bindings are plain identifiers or dotted paths (plus `$index`) — no
  negation, operators or method calls. To format or compute a value, expose a
  computed `[UIBind]` property and bind it by name.
* Only `[UIBind]` members are reachable; a public field without the attribute
  is not scanned. Private `[UIBind]` members are.

**When the UI updates.** A plain controller is read once a frame — mutating a
`[UIBind]` field anywhere is enough. A controller that implements
`Weva.Binding.IBindingVersion` promises to bump `BindingVersion` when
anything reachable from a binding changed and is read only then, which is
what a large controller wants:

```csharp
public sealed class ShopController : IBindingVersion {
    public int BindingVersion { get; private set; }
    int gold;
    [UIBind] public int Gold { get => gold; set { if (gold != value) { gold = value; BindingVersion++; } } }
}
```

`doc.RequestRefresh()` re-reads on the next frame regardless, and
`doc.Refresh()` does it now (returns how many nodes changed).

### Repeated lists

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

`data-each="<items> as <alias>"` reads any `IList` (a `List<T>`, an array)
and clones the template body once per item. `data-key` gives each clone a
stable identity so reorders refill existing rows instead of rebuilding the
container. Inside the template, `{{ stage.Name }}` resolves against the item,
parent controller bindings still resolve normally, and `$index` is the
zero-based item index. A handler on a row can ask which row it sits in:
`doc.Query("#" + id).RowIndex` / `.RowKey` (§6).

### Class toggles

`data-class-<name>="Path"` adds or removes only that one class from the
element and leaves the rest of its `class` attribute intact.

### Two-way form binding

`data-model="Path"` on an `<input>`, `<textarea>` or `<select>` fills the
control from the path and writes the user's edits back into it, keeping the
member's type — an `int` field gets an `int`, a `bool` a `bool`:

```html
<input data-model="PlayerName">
<input type="range" min="0" max="100" data-model="Settings.Volume">
<input type="checkbox" data-model="Settings.Music">
```

Every other binding to that path follows on the same frame; `doc.DataChanged`
raises the path and the text. Inside a `data-each` row,
`data-model="stage.Score"` unwinds to the item.

### A dictionary instead of a controller

`doc.Bind(model, controller)` adds an `IDictionary<string, object>` graph
(dictionaries, lists, scalars) the resolver falls through to for roots the
controller does not declare — the shape a save file or a server payload
already has. `doc.Data` reads it back.

## 3. Events

`on-<event>="MethodName"` attributes call public methods on the controller,
with the element's `id` if the method takes a `string`:

```html
<button on-click="OnStart">Start</button>
<input type="text" on-input="OnSearch" on-change="OnSearchCommitted" />
<form on-submit="OnLogin">…</form>
<details on-toggle="OnSectionToggled">…</details>
```

```csharp
public void OnStart() { … }
public void OnSearch(string id) { var text = doc.Query("#" + id).Value; … }
```

The event names, as in a browser: `click`, `pointerdown`, `pointerup`,
`pointerenter`, `pointerleave`, `keydown`, `keyup`, `textinput`, `focus`,
`blur`, `input` (a control's value changed while editing), `change` (a value
committed: focus left a changed field, a box toggled), `submit`, `reset`,
`invalid`, `close` and `cancel` (a `<dialog>`), `scroll`, `toggle` and
`beforetoggle` (`<details>`, popovers), `contextmenu` (the secondary button),
and `compositionstart` / `compositionupdate` / `compositionend` (an IME).

The same events reach C# without markup, for code that wires itself:

| `WevaDocument` event | Fires |
|---|---|
| `ElementClicked(id)` | A click, pointer or keyboard activation. |
| `HandlerInvoked(handler, id)` | Any `on-<event>` attribute, before the controller method. |
| `ValueChanged(id, value)` | A control's value changed while editing. |
| `Changed(id, value)` | A value committed. |
| `FormSubmitted(id)` | A form submitted (Enter in a field, a submit button). |
| `Focused(id)` | Focus moved; empty when dropped. |
| `DataChanged(path, text)` | A `data-model` control wrote into the controller or model. |
| `Event(WevaEvent)` | Every core event: `Kind` (`WevaEventKind`), `Target` (a `WevaElement`), `Position`, `Buttons`, `Shift`/`Ctrl`/`Alt`/`Meta`, `Text`, `Handler`. |

Standard CSS state pseudo-classes (`:hover`, `:focus`, `:focus-visible`,
`:active`, `:disabled`, `:checked`, `:placeholder-shown`, `:focus-within`)
flip automatically — no controller code needed.

**UI sound effects.** Weva has no audio system of its own — play click/hover
SFX from the handler like any other game code: `on-click` for clicks,
`on-pointerenter` for hover. Centralise it by subscribing `ElementClicked`
once and checking the id, rather than adding `on-` attributes to every
button.

## 4. Forms

| Element | Behavior |
|---|---|
| `<input type="text">` / `password` / `email` / `number` / `search` / `tel` / `url` | Typed text with click/drag caret and selection, word select on double-click, undo/redo, held-key repeat, paste, and OS IME composition (CJK) through the Input System. |
| `<input type="checkbox">` | Toggleable; `checked` reflects state. |
| `<input type="radio" name="g">` | Group-exclusive (one per `name=` selected). |
| `<input type="range" min max step value>` | Slider with thumb drag, click-track, arrows / PageUp / Home / End. |
| `<input type="hidden">` | Form data only, no rendering. |
| `<select>` + `<option>` | Dropdown with an opening list, typeahead, arrows. |
| `<textarea>` | Multi-line text input. |
| `<button>` / `<button type="submit">` | Click target; submit triggers the enclosing form. |
| `<form on-submit="…">` | Captures Enter inside text inputs and submit clicks. Read the values through `data-model` bindings. |
| `<dialog>` | `doc.Query("#dlg").ShowDialog(modal: true)` / `.CloseDialog()` open and close it; `on-close` / `on-cancel` (Escape) fire. |
| `<details>` / `<summary>` | Opens and closes; `on-toggle` fires. |
| `title="…"` | A tooltip after the browser's hover delay. |

A control's live value is `doc.Query("#name").Value`; setting it is what
the user typing it would do — events and bindings follow.

## 5. Input

With `AutoInput` on (the default), `WevaDocument` reads the Input System
every playing frame:

* **Mouse / pen** — pointer moves, buttons, wheel (one notch scrolls 100 px,
  as in Chrome), hover and the CSS `cursor` keyword (`doc.Cursor`, for you
  to map to a texture).
* **Keyboard** — every key with modifiers, Tab / Shift+Tab focus order
  (wrapping inside the document by default; `doc.WrapTab = false` lets it
  leave and raises `doc.TabbedOut(backwards)`), held-key auto-repeat, the OS's
  copy/paste/undo chords (Cmd on macOS, Ctrl elsewhere).
* **Touch** — a tap is a click; a drag past 8 px pans the nearest scroll
  container.
* **Gamepad** — the d-pad and left stick move focus by geometry (Left/Right
  on a slider or a caret, Up/Down on a select or a number field are that
  control's keys first), South accepts, East cancels (Escape), the shoulders
  step the tab order, with held repeat. Set `doc.GamepadTextEntry = true` to
  have South on a text field raise `doc.TextEntryRequested(id)` (open your
  on-screen keyboard, then set `doc.Query("#" + id).Value`) instead of
  pressing Enter.
* **IME** — composition through `Keyboard.onIMECompositionChange`, enabled
  and positioned at the caret only over a text control.

`doc.InputConsumed` says whether the last frame's input was taken by the
document, so gameplay can ignore a click that landed on the UI. With several
documents on screen the one painted on top (`SortingOrder`) takes the
pointer where it accepts it — everywhere, as in a browser, transparent or
not; a HUD that should let clicks reach the document beneath (or the game)
through its empty areas says `html, body { pointer-events: none }` and
`pointer-events: auto` on its controls.
`doc.AcceptsKeyboard = false` keeps the keyboard for the game while the
pointer still works.

## 6. Programmatic updates

Text and classes change through bindings — that is what they are for. For
the rest, `doc.Query(selector)` hands you a `WevaElement`:

```csharp
WevaElement slot = doc.Query("#slot-7");               // a CSS selector; WevaElement.None when nothing matches
slot.SetAttribute("data-state", "locked");             // attributes drive CSS ([data-state="locked"] { … })
doc.Query("html").SetAttribute("style", "--color-primary: #ef4444;");   // retheme
doc.Query("#search").Focus();
doc.Query("#log").ScrollTo(0, 1e6f);                   // scroll to the bottom (clamped; smooth if the CSS says so)
foreach (WevaElement li in doc.QueryAll("#inventory > li")) { … }
if (slot.HasClass("locked")) { … }
```

`Id`, `Tag`, `Text`, `Value` (get and set, for a control), `Attribute` /
`HasAttribute` / `HasClass`, `Bounds` (the border box in document pixels
after the last frame), `Scroll` / `MaxScroll`, `IsFocused`, `RowIndex` /
`RowKey` (the `data-each` row), `ShowDialog` / `CloseDialog`, `Parent` /
`Children` — see [api-stability](api-stability.md) for the whole list.

A `WevaElement` is a value: it names a node of one tree. `Reload()` replaces
the tree, so elements from before it report `IsValid == false` and every
member answers a default (empty, zero, false) rather than throwing; query
again after a reload. `doc.FocusedElement` is the focused element or `None`.
Writes land on the next frame. Rebuilding a whole section is `doc.Reload()`
with new markup (`InlineHtml`, or a different `DocumentAsset`); the
controller stays attached.

## 7. Layout patterns

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
.list-header { position: sticky; top: 0; background: var(--surface); z-index: 1; }
```

### Container queries (responsive without media queries)

```css
.card-container { container-type: inline-size; }
@container (min-width: 320px) { .card { display: grid; grid-template-columns: 1fr 1fr; } }
@container (max-width: 319px) { .card { display: flex; flex-direction: column; } }
```

### Anchor positioning

```css
.tooltip { position: absolute; position-anchor: --slot-7; bottom: anchor(top); left: anchor(center); translate: -50% -8px; }
.slot[data-id="7"] { anchor-name: --slot-7; }
```

## 8. CSS variables for runtime theming

```css
:root { --color-primary: #4f46e5; --color-text: #f8fafc; --space-md: 12px; }
button { background: var(--color-primary); color: var(--color-text); padding: var(--space-md); }
```

Retheme by binding the root's style — `<html style="{{ Theme }}">` with a
`[UIBind] string Theme => "--color-primary: #ef4444;"` — or set the attribute
directly (§6). `PrefersDarkColorScheme` on the component answers
`@media (prefers-color-scheme: dark)` and `light-dark()`.

## 9. Images and files

`url()` in CSS and `<img src>` resolve relative to `BasePath` — the document
asset's folder in the editor — through the core's asset reader, which reads
files. A player that does not ship its UI as files hands the core its own
reader: `doc.AssetReader = path => bytes` (Addressables, bundles,
`Resources`), returning `null` for an asset it does not have; set it any
time, it survives a reload. 9-slice
frames are CSS `border-image`.

## 10. Fonts

`Font`, `Bold`, `Italic` and `Fallbacks` on the component are the UI face
(the package's Inter and a symbol face when empty). `@font-face` with
`url()` (relative to `BasePath`) or `local("Installed Name")` adds families
from a stylesheet, matched by weight and style the way a browser does;
`doc.RegisterFontFamily("MyFont", font)` names a Unity `Font` from code.
See [Text & Fonts](text-and-fonts.md).

## 11. Performance

* Bindings cost what they read. A large controller should implement
  `IBindingVersion` so a quiet frame reads nothing.
* Avoid setting attributes every frame from `Update()`; drive visuals from
  `[UIBind]` values so an unchanged frame changes no node.
* `box-shadow`, `filter: blur()` and `text-shadow` are the costliest painters
  — keep them off elements that change every frame. `backdrop-filter` copies
  the frame once per filtered element: fine for a few panels, not for a list
  of fifty.

## 12. DevTools

**Window → Weva → Elements** opens the Elements panel for a `WevaDocument`
in the scene: the tree with search, the selected element's matched rules
(winners first, losing declarations struck through), computed style and box
model — the same data Chrome's DevTools shows. The component's inspector shows
the core's HTML diagnostics and last error.

## 13. Hot reload

Saving a `.html` or `.css` the document references — its asset, a linked
sheet, an inspector sheet — reloads it on reimport, in play mode and in edit
mode. The controller stays attached and its `[UIBind]` values survive because
they live on your object. `doc.Reload()` does the same from code.

## 14. Focus & controller navigation

Tab and Shift+Tab walk every focusable control in document order (buttons,
links, form controls, `tabindex`), wrapping inside the document by default.
`:focus-visible` styles the keyboard/gamepad focus ring, as in a browser.
Gamepad navigation is built in (§5): the d-pad and left stick move focus to
the nearest control in that direction, South activates, East cancels, the
shoulders step the tab order. `doc.Query("#first").Focus()` gives the
first input an anchor; `doc.FocusedElement` is the focused element.

## 15. Localization & RTL

There is no translation system baked in — localized strings are **just
bindings**. Expose the resolved string as a `[UIBind]` property backed by your
localization table and bind it by name; swapping the active language and
bumping (or waiting a frame) repaints:

```csharp
[UIBind] public string StartLabel => Loc.Get("menu.start");
public void SetLanguage(string lang) { Loc.Active = lang; }
```

```html
<button on-click="OnStart">{{ StartLabel }}</button>
```

Format numbers and dates the same way — compute a culture-formatted string in
a `[UIBind]` property.

**RTL.** `direction: rtl`, logical properties and `text-align: start | end`
resolve per direction, and the core reorders mixed-direction lines with ICU's
bidi algorithm (`unicode-bidi` honoured). Glyph *shaping* is the host's: on
Unity the package shapes each run itself over the font's OpenType tables —
a right-to-left run is drawn in visual order with mirrored brackets, Arabic
letters take their joining forms and ligatures, combining marks sit on their
base. Hebrew and Arabic need a face that has them: with `SystemFontFallback`
on (the default) the platform's UI font answers (Segoe UI, Arial, DejaVu
Sans); a face of your own comes in through `@font-face` (`url()` or
`local(…)`), which carries the bytes the shaper reads — a `Font` asset in
`Fallbacks` draws its glyphs but gets no substitutions. Vertical writing modes
are not implemented.

## 16. Where to look next

* **Package [`README.md`](../README.md)** — supported HTML/CSS subset,
  architecture overview.
* **[Supported CSS](supported-css.md)** — the property matrix and the known
  divergences from Chrome; **[Supported HTML](supported-html.md)**.
* **`Samples~/PhaseOneDemo/`** — the end-to-end demo scene that ships in the
  package; import via Package Manager → Weva → Samples.
* **`Assets/UI/`** *(repo checkout only)* — ~30 sample pages, each also a
  Chrome-checked layout fixture.
