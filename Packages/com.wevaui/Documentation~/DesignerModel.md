# Weva Designer — Document Model

The Designer model layer (`Weva.Designer`) is the in-memory representation a visual
UI editor manipulates. You build a **Design Document** out of opinionated, designer-
friendly primitives (auto-layout, Fill/Hug/Fixed sizing, tokens, components) and the
**compiler** lowers it to ordinary Weva HTML/CSS — the same HTML/CSS the runtime
already renders. Nothing here invents new CSS; it's a friendlier *authoring* surface
on top of the engine.

This document is the API reference for the headless model layer. It is fully
testable without Unity (see `Tests/Runtime/Designer/`).

> Philosophy: **constrain, don't expose.** The model offers a small set of concepts a
> non-coder designer understands; power comes from composition (tokens + components),
> not from exposing every CSS property. Every value can show where it came from, and
> the cascade is never something the author has to debug.

---

## Quick start

```csharp
using Weva.Designer;

var root = new DesignNode("Screen") { Layout = LayoutMode.Column, Gap = 16, Fill = "{bg}" };
root.SetFixedSize(390, 844);
root.SetPadding(24);
root.Add(new DesignNode("Title") { Text = "Hello", TextColor = "{text}", FontSize = 32 });

var doc = new DesignDocument(root);
doc.Tokens.Color("bg", "#0e0f14").Color("text", "#f2f4f8");

DesignCompileResult r = doc.Compile();   // r.Html, r.Css — feed these to a WevaDocument
```

---

## Layout — auto-layout, not flexbox

A node arranges its children with a `LayoutMode`:

| LayoutMode | Meaning              | Compiles to                  |
|------------|----------------------|------------------------------|
| `None`     | Normal block flow    | (no `display`)               |
| `Row`      | Stack left → right   | `display:flex; flex-direction:row` |
| `Column`   | Stack top → bottom   | `display:flex; flex-direction:column` |
| `Grid`     | Equal columns        | `display:grid; grid-template-columns:repeat(N, minmax(0,1fr))` |

Alignment along the two axes:

- `MainAlign` — `Start` / `Center` / `End` / `SpaceBetween` → `justify-content`.
- `CrossAlign` — `Start` / `Center` / `End` / `Stretch` → `align-items`
  (default `Start`, emitted as `flex-start` so a Hug child doesn't get stretched).

Other container controls:

- `GridColumns` — number of equal columns when `Layout` is `Grid` (≤1 ⇒ a single track).
- `Wrap` — let Row/Column children wrap onto new lines when they overflow (`flex-wrap:wrap`).
- `Gap`, `PadTop/Right/Bottom/Left` — spacing (px or tokens — see Tokens).

## Sizing — Fill / Hug / Fixed

The single most important control. Each node sizes itself per axis with a `SizeMode`:

| SizeMode | Meaning                | On the main axis        | On the cross axis      |
|----------|------------------------|-------------------------|------------------------|
| `Hug`    | Shrink to fit contents | `auto`                  | `auto`                 |
| `Fill`   | Grow to fill parent    | `flex:1 1 0; min:0`     | `align-self:stretch`   |
| `Fixed`  | Explicit px            | `width`/`height`        | `width`/`height`       |

```csharp
node.SetSize(SizeMode.Fill, SizeMode.Hug);  // fill width, hug height
node.SetFixedSize(120, 40);                  // both fixed
```

This retires `flex-grow`/`flex-shrink` from the author's mental model. For the rare case
that needs them, **constraints** are the escape hatch (all px, `0` = unset):

- `MinWidth` / `MaxWidth` / `MinHeight` / `MaxHeight` — clamp any sizing mode
  ("Fill, but never exceed 400px"; "at least 200px"). A `MinWidth` replaces the Fill
  `min-width:0` floor.
- `AspectRatio` — width ÷ height (e.g. `16.0/9.0`); lets one axis derive from the other.

## Style

**Fill & border**

- `Fill` — background: a colour, a **gradient token**, or a raw `linear-gradient(...)`.
- `BackgroundImage` (+ `BackgroundSize` Cover/Contain/Stretch) — a background image layered
  over the fill, e.g. a textured panel; the URL is escaped safely.
- `Stroke` (+ `StrokeWidth`, px) — a border; width defaults to 1px (Figma parity).
- `Shadow` — a raw `box-shadow` or a shadow token.

**Shape**

- `Radius` — uniform corner radius (`Dim`: px or token).
- `RadiusTopLeft/TopRight/BottomRight/BottomLeft` — optional per-corner overrides
  (null = inherit `Radius`); any set ⇒ the 4-value `border-radius` shorthand.
- `Overflow` — `Visible` / `Clip` (`hidden`) / `Scroll` (`auto`).
- `Opacity`.

**Text** (a node is a text node when `Text` is set, or when it has a text binding)

- `TextColor`, `FontSize` (`Dim`).
- `FontWeight` (Normal/Medium/SemiBold/Bold → 400/500/600/700), `Italic`.
- `TextAlign` (Start/Center/End/Justify → logical `text-align`), `LineHeight` (unitless).
- `LetterSpacing` (px, may be negative), `TextTransform` (Uppercase/Lowercase/Capitalize),
  `TextDecoration` (Underline/LineThrough).
- `TextShadow` — glyph drop-shadow for legibility over busy backgrounds (HUD numbers,
  titles). A raw `text-shadow` value or a shadow token (`{name}`); distinct from the
  box-level `Shadow`. Emits on text nodes only.

**Transform & interactivity**

- `Rotation` (deg) + `Scale` — compose into one paint-time `transform` (no layout effect).
- `Cursor` — `Pointer` for the "clickable" affordance.
- `TransitionMs` — animate style changes (into hover/pressed) over N ms.

Colours/shadows are raw CSS or token refs (`"{name}"`); `Dim` fields are px or token.

---

## Tokens

Named design tokens are the single source of truth for **colors, spacing, radii, the
type scale, and shadows**. Reference a token anywhere with `{name}`; the compiler emits
a `:root` custom property and a `var(--…)` reference, so swapping a token restyles
every element that uses it. A `Fill` token resolves as a colour or a **gradient** token
(gradient checked first).

```csharp
doc.Tokens
   .Color("brand/primary", "#5b8cff")
   .Space("md", 16)
   .Radius("card", 12)
   .Font("h1", 32)
   .Shadow("elevated", "0 4px 12px rgba(0,0,0,0.3)")
   .Gradient("brand", "linear-gradient(90deg, #5b8cff, #a855f7)");
```

### `Dim` — a tokenizable dimension

Spacing/radius/type values are `Dim`: either px or a token reference.

```csharp
node.Gap = 16;                  // px (implicit conversion from double)
node.Gap = Dim.Token("md");     // spacing token → var(--space-md)
node.Radius = Dim.Token("card");
```

Unknown color/shadow tokens compile to a visible fallback (magenta for color) rather
than silently disappearing.

---

## Interactive states

Style a node for `Hover` / `Pressed` / `Focus` / `Disabled`; only the properties you
change are overridden, the base style shows through. Compiles to pseudo-classes
(`:hover`, `:active`, `:focus`) or a state class (`.is-disabled`, toggled by app/data).

```csharp
node.State(InteractionState.Hover).Fill = "{primary-hover}";
node.State(InteractionState.Hover).TextDecoration = TextDecoration.Underline; // link on hover
node.State(InteractionState.Disabled).Opacity = 0.4;
```

A state can override `Fill`, `TextColor`, `Shadow`, `Stroke`/`StrokeWidth`, `Radius`,
`Opacity`, `TextDecoration` and `FontWeight`. Pair with a base `TransitionMs` for a smooth
animated change.

---

## Placement — out-of-flow overlays

`Position` lifts a node out of its parent's auto-layout and pins it to the parent box —
HUD badges, corner close-buttons, overlays.

```csharp
badge.Position = Position.Absolute;
badge.OffTop = Dim.Of(8);
badge.OffRight = Dim.Of(8);   // pin to the top-right corner
```

Offsets are `Dim?` (px or spacing token); `null` = that edge is unpinned, `0` still pins to
the edge. A parent that contains any absolute child automatically becomes the positioning
context (`position:relative`).

---

## Data binding

Wire UI to game data — the capability generic web builders can't match. Built on the
engine's binding markup (resolved by `BindingScanner` against an `[UIBind]` controller).

```csharp
var b = node.Bind();
b.Text = "Player.Health";              // → {{ Player.Health }}
b.RepeatEach = "Inventory.Items as item";  // → data-each
b.RepeatKey  = "item.id";                  // → data-key
b.BindClass("is-hidden", "Menu.IsClosed"); // → data-class-is-hidden
b.BindEvent("click", "OnPlay");            // → on-click="OnPlay"
```

Event names: `click`, `change`, `input`, `submit`, `focus`, `blur`.

---

## Components & variants

A `DesignComponent` is a reusable template with props (`$name` placeholders), variants
(named prop sets), and an optional slot. An **instance** references a component by name;
editing the component updates every instance.

```csharp
var tpl = new DesignNode("Button") { Layout = LayoutMode.Row, Fill = "$bg", Radius = 8 };
tpl.SetPadding(12);
tpl.Add(new DesignNode("Label") { Text = "$label", TextColor = "#fff" });

var button = new DesignComponent("Button", tpl)
    .Prop("label", "Button")
    .Prop("bg", "#888")
    .Variant("primary", new() { { "bg", "#5b8cff" } });

doc.AddComponent(button);

var inst = new DesignNode { ComponentRef = "Button", Variant = "primary" };
inst.SetProp("label", "Play");          // effective props: defaults ⊕ variant ⊕ instance
parent.Add(inst);
```

Effective prop value precedence: **instance override > variant > component default.**
Mark one template node `IsSlot = true` to receive an instance's children. Component
reference cycles are depth-limited (won't hang); the validator flags them.

---

## Persistence

`DesignSerializer` round-trips a document to/from a stable, versioned JSON format
(deterministic, diff-friendly; only non-default fields emitted; unknown keys ignored
and missing keys defaulted for forward/backward compatibility).

```csharp
string text = DesignSerializer.Serialize(doc);
DesignDocument loaded = DesignSerializer.Deserialize(text);
```

---

## Editing (undo/redo)

`DocumentEditor` is the single write-path into a document. Every mutation is undoable,
marks the document dirty, bumps a version (for recompile) and fires `Changed`.

```csharp
var ed = new DocumentEditor(doc);
ed.SetFill(node, "{primary}");
ed.SetGap(node, Dim.Token("md"));
ed.AppendChild(parent, new DesignNode("Card"));
ed.Duplicate(parent, card);
ed.Undo();  ed.Redo();
```

- **Coalescing:** consecutive edits to the same node+property merge into one undo step
  (so a slider drag is a single undo).
- **Batches:** `BeginBatch("Style") … EndBatch()` groups edits into one transaction.
- **State / binding / component** edits have their own setters
  (`SetStateFill`, `SetTextBind`, `SetVariant`, `SetInstanceProp`, …), all undoable.

### Clipboard

```csharp
var clip = new DesignClipboard();
clip.Copy(node);                 // detached snapshot
clip.PasteInto(ed, parent);      // fresh independent copy, single undo step
clip.Cut(ed, parent, node);
```

`DesignNode.Clone()` deep-copies a subtree (used by clipboard, duplicate, instancing).

---

## Templates

`DesignTemplates.Catalog()` returns ready-made starting points (Blank, Main Menu,
Combat HUD, Settings) for the editor's "New from template" picker — designers start by
editing a real screen, never a blank div.

```csharp
foreach (var t in DesignTemplates.Catalog())
    DesignDocument d = t.Create();
```

---

## Validation

`DesignValidator.Validate(doc)` returns a list of `DesignDiagnostic` (severity + stable
code + message + node) for the editor to surface — unknown component/variant/token
references (colour *or* gradient on a fill), undeclared props, invalid repeat syntax,
unknown events, misplaced or duplicate slots, component cycles, and geometry mistakes
(`max < min`, Fixed sizing with no size, edge offsets on a non-absolute node, negative
aspect ratio). A clean document yields none.

```csharp
var diags = DesignValidator.Validate(doc);
if (DesignValidator.HasErrors(diags)) { /* block save / show banner */ }
```

---

## Pipeline summary

```
DesignDocument (IR)
  → DesignValidator       (diagnostics, optional)
  → DesignExpander        (component instances → concrete tree)
  → DesignCompiler        (→ scoped HTML/CSS, one class per node)
  → Weva engine           (parse → cascade → layout → paint)
```

Editing flows the other way: gestures → `DocumentEditor` mutations → recompile →
re-render. Nothing edits CSS directly; everything is the document model.
