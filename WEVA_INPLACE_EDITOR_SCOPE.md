# In-place HTML/CSS editor — scope

> Pivot the Weva visual editor from "edit a JSON `DesignDocument` IR that compiles to HTML/CSS"
> to "edit the live HTML/CSS document in place." Motivation: the primary workflow is **AI writes
> HTML/CSS → human tweaks visually**, so HTML/CSS is the artifact everyone touches. A separate
> persisted IR (`.json`) is then two-sources-of-truth liability. HTML on disk is the source of
> truth; any in-memory model is transient plumbing the user never sees.

This doc is grounded in a read of the current code (see file:line refs). It supersedes the
JSON-IR assumption *for the tweak-existing-HTML workflow*; the IR generator is retained for the
greenfield/Figma-import workflow (§7).

---

## 1. Reframing: there is (almost) no intermediate format

In the in-place model the in-memory model is **the engine's own live document**, not a parallel IR:

- `Weva.Dom.Document` / `Element` / `TextNode` (`Runtime/Dom/`) — the parsed tree.
- The CSS cascade (`Runtime/Css/`) — `Stylesheet → Rule → Declaration` with `ValueText`.

The editor reads/writes *these*. "Load HTML → edit → save HTML" with no user-visible IR is exactly
what the engine is shaped for — it parses once and mutates in place already (hot-reload does this).

## 2. What the engine gives us for free

- **Live mutation + auto-invalidation.** `element.SetAttribute("style", …)` →
  `DomMutation.AttributeChanged` bubbles → `InvalidationTracker` marks Style|Layout|Paint dirty →
  re-cascade/relayout/repaint next frame. No compile step. (`Runtime/Dom/Element.cs`,
  `WevaDocument.MarkStyleDirty`/`MarkLayoutDirty` at `WevaDocument.cs:553,560`.)
- **In-place DOM diffing precedent.** Hot-reload re-parses + `DomDiffer.ApplyDocumentDiff` mutates
  the live tree, preserving identity for keyed nodes (`Runtime/HotReload/DomDiffer.cs`).
- **Cascade source-tracing (the hard part, already built).** `MatchedDeclaration`
  (`Runtime/Css/Cascade/MatchedDeclaration.cs`) ties a computed value back to its exact
  `Declaration` + `SelectorText` + `SourceIndex` + `InRuleIndex` + `Origin` + `Specificity`.
  `StyleInspector.Dump` (`Runtime/DevTools/StyleInspector.cs`) already emits a per-property cascade
  trace with winner + overridden declarations. This is what lets us answer "what set this value, and
  where does an edit go."
- **~80% of the Designer UI.** Figma-style scrubs, color picker, align pad, chips, panel chrome,
  keyboard/menus, and the `DocumentEditor` undo/redo/merge/batch engine
  (`Runtime/Designer/Editing/DocumentEditor.cs`) are IR-agnostic — they just need their apply/revert
  closures retargeted from `node.Field = x` to element/declaration mutation.

## 3. The catch: no serializer, lossy parse → DO NOT reserialize

Confirmed gaps:
- **No DOM→HTML serializer** anywhere. Parse drops element/attr source positions.
- **No CSS→text serializer.** The CSS AST is good (`Stylesheet`/`StyleRule`/`Declaration`), but
  **comments are skipped** (`CssParser.SkipCommentRun`), whitespace is normalized, and `@nest` is
  flattened by `NestingExpander`. `@media/@supports/@container/@layer/@keyframes` *do* survive as
  structured rules.

Therefore a naive **parse → AST → re-emit round-trip would reformat the whole file and delete every
comment.** That is the file-mangling failure mode; it is the default of any reserialize approach.

**The fix is surgical splicing, not serialization.** Keep the original source buffer in memory.
Apply each edit as a minimal text splice over the byte range of the *one* declaration that changed;
leave everything else byte-identical. Untouched comments/formatting/ordering are preserved exactly.

### 3.1 Required infra (the real new work)

1. **Source spans on declarations + inline style.** Add `(start,end)` offsets to `Declaration`
   (and the parsed inline-`style` declarations) so an edit maps to a byte range in the original
   source. The tokenizer already carries `Line`/`Column` (`CssToken`) — it's just not retained on
   `Declaration`. Same for the element's `style="…"` attribute value range, and (for insert/move)
   element start-tag ranges.
2. **A localized emitter for *new* content only.** Inserting a node/rule the user creates needs to
   emit a snippet (clean formatting is fine — it's new) and splice it at a known insertion point.
   This is small and bounded; it is NOT a whole-file serializer.

Net: round-trip = (original text) with minimal splices. No faithful pretty-printer needed.

## 4. The genuinely hard problem: edit targeting (not serialization)

A property's winning declaration may come from a **shared rule** (`.btn { background: … }` matching
20 elements). Editing it changes all 20 — a generator-based tool never faces this; an in-place
editor must. We already have the data to handle it well (`MatchedDeclaration`).

Policy (recommended default):
- **Default: write a per-element inline-style override** on just the selected element (safe, local,
  obvious). Cost: inline-style sprawl.
- **One click away: "edit the rule instead"**, surfacing "this affects N elements" using the
  cascade trace. Powerful for systematic changes.
- Surface both through a cascade panel (the `StyleInspector` data already exists).

This UX decision is the crux of the whole pivot — more than any serialization detail.

## 5. Recognition layer (best-effort, with graceful fallback)

The nice high-level controls (Hug/Fill sizing, "is this a token ref or a literal") are an
*interpretation on top of* the live CSS, not a transcode:
- When an element's declared CSS matches a recognizable pattern (e.g. `flex: 1 1 0` ⇒ "Fill",
  `width:max-content`/auto ⇒ "Hug"), light up the high-level control.
- Otherwise fall back to **raw property editing**. Always better to show a raw `width` field than to
  confidently mislabel/normalize the user's intent.
- Token refs: recognize `var(--x)` / `{x}` ⇒ token swatch; else literal color picker.

## 6. IR features → engine-native equivalents (not lost — remapped)

| Current IR concept | In-place mapping | Notes |
|---|---|---|
| Sizing modes (Hug/Fill/Fixed) | raw `width`/`flex-basis`/`min/max-content` + recognition (§5) | core IR concept; becomes inference |
| Main/Cross align (directional) | `justify-content`/`align-items` directly | lose the auto-flip-with-direction nicety |
| Layout mode (Row/Column/Grid) | `display:flex` + `flex-direction` / `display:grid` | direct |
| Interactive states (Hover/Pressed/…) | `:hover`/`:active`/`:focus`/`:disabled` rules | engine already cascades these |
| Component instances + variants | **engine components** (`ComponentTemplateImporter`, `<template>`) | real feature, not IR-only |
| Data binding (`NodeBinding`) | engine `{{ }}` binding markup | real feature, not IR-only |
| Text node | live `TextNode.Data` edit | direct |

Components and bindings are **engine-native already** — they don't disappear in the HTML-source
model, they map to the markup the engine ships.

## 7. Fate of the `DesignDocument` IR + compiler

Keep it — but scoped to the **no-existing-HTML entry points**: "create from blank" and **Figma
import** (`FigmaNode → DesignDocument → DesignCompiler`). There's nothing to preserve there, so the
clean one-class-per-node generator is ideal. Both surfaces share the same inspector controls.
Do **not** route "edit existing HTML" through it. The persisted `.json` save format is dropped as a
user-facing artifact.

## 8. Reuse assessment (condensed)

| Piece | Reuse | Work |
|---|---|---|
| Panel chrome, scrubs, color picker, align pad, menus, shortcuts | 90–100% | rebind data source |
| Selection model | 100% | DesignNode ref → Element ref |
| `DocumentEditor` undo/redo/merge/batch | ~95% | swap closure bodies |
| Direct-CSS-property controls (font, color, radius, opacity, transform, stroke, shadow, …) | ~95% | map to declaration edits |
| Sizing-mode / directional-align controls | ~30–50% | recognition + raw fallback |
| States / components / bindings | new wiring | remap to pseudo-classes / engine components / `{{ }}` |
| HTML/CSS rendering | n/a | already the live engine |
| Compiler | retained for greenfield/Figma only | — |

## 9. Phasing

- **P0 — read-only inspector over the live document.** Select an element on the canvas → show its
  computed/declared styles + cascade trace (`StyleInspector`). No edits. Proves selection,
  element↔box mapping, cascade panel. (Low risk; mostly wiring existing pieces.)
- **P1 — edit via inline-style splice.** Add source spans (§3.1.1); retarget the direct-property
  inspector controls to write inline-style overrides through `DocumentEditor`; save = splice the
  buffer. Round-trip a real AI-generated file and diff: only touched bytes change.
- **P2 — edit-the-rule path + cascade panel UX** (§4). The "affects N elements" workflow.
- **P3 — structural edits** (add/move/delete nodes): DOM mutation + element source spans + the
  localized emitter (§3.1.2).
- **P4 — recognition layer** (§5): Hug/Fill + token inference on top of P1 controls.
- **P5 — converge entry points:** greenfield/Figma still generate via the IR compiler, then hand off
  to the same in-place surface.

## 10. Biggest risks

1. **Edit targeting UX** (§4) — the make-or-break product decision; get it wrong and edits either
   sprawl inline or silently restyle the whole app.
2. **Source-span plumbing** must cover every place a value can live (inline style, rule declaration,
   shorthand expansion). Miss one and that edit can't be applied as a splice (fallback: regenerate
   just that rule — localized, not whole-file).
3. **Shorthand round-trips** — editing `padding-left` when the source says `padding: 8px` means
   either rewriting the shorthand or adding a longhand override; needs a rule (prefer longhand
   override to keep splices minimal).
4. **Canvas hit-testing** to the right element across overlap/transforms — reuse DevTools
   `HoverInspector`/box hierarchy, don't reinvent.
