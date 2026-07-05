# WEVA_EDITOR_PLAN.md — visual UI editor for non-coder designers

> Living spec + execution tracker. Audience: **designers / non-coders** (Figma is the
> mental-model benchmark). Scope: **full editor**. This doc is the source of truth for
> the `/loop execute on WEVA_EDITOR_PLAN.md` build loop — check boxes as milestones land.

## North star

> A designer who has never written CSS can build a production game UI that ships as real
> Weva HTML/CSS, and never once sees the word "flex," "px," or "specificity."

Three principles that resolve every later decision:

1. **Constrain, don't expose.** Fewer, opinionated controls beat full CSS coverage.
   Power lives in *composition* (components, tokens), not in property breadth.
2. **The canvas is the truth.** Direct manipulation first; panels are secondary. The
   canvas is rendered by the *real Weva engine*, so WYSIWYG is literal, not approximate.
3. **No invisible state.** Every value shows where it comes from and how to change it.
   The cascade is an implementation detail the user never debugs.

## Architecture keystone — the Design Document IR

The editor never edits CSS. It edits a **Design Document** (the IR) that *compiles* to
Weva HTML/CSS. This is the most important decision; everything depends on it.

```
DesignDocument
 ├─ Tokens        colors, spacing scale, type scale, radii, shadows, breakpoints
 ├─ Components     reusable nodes with props, variants, slots
 └─ Nodes (tree)
      ├─ layout:   Stack↓ | Stack→ | Grid | Free
      ├─ sizing:   Fill | Hug | Fixed   (per axis)   ← retires width/height/grow/shrink/min/max
      ├─ spacing:  padding, gap   (token refs)
      ├─ style:    fill, stroke, radius, shadow, opacity, text (token refs)
      ├─ states:   default / hover / pressed / focus / disabled overrides
      ├─ binding:  optional data binding (text, visibility, list-repeat)
      └─ overrides per breakpoint
```

Why an IR (not "edit CSS properties"): guarantees clean, scoped, conflict-free output;
makes Figma import lossless (same primitives); enables undo/history/diff; keeps the door
open to emit other targets later.

### Reuse, don't reinvent (key finding)

`Packages/com.wevaui.figma/Runtime/` **already has this model**, Figma-flavored:
- `Model/FigmaNode.cs` — auto-layout nodes (layoutMode, sizing, padding, gap).
- `Tokens/` — `FigmaVariables` → `VariablesToCss` (the token system).
- `Mapping/` — `LayoutMapper`, `StyleMapper`, `HtmlWriter`, `FigmaDocumentExporter`:
  a working **auto-layout-model → Weva HTML/CSS compiler**.

Strategy: extract a **neutral `DesignDocument` IR + compiler** that is NOT Figma-specific,
then make the Figma importer a thin `FigmaNode → DesignDocument` mapper that feeds the
same compiler. One compiler, two front-ends (editor canvas + Figma import).

## Where code lives

- **`Packages/com.wevaui/Runtime/Designer/`** — the IR (`DesignDocument`, `DesignNode`,
  tokens, enums) + the **compiler** (`DesignDocument → HTML/CSS`). Pure C#, **headless,
  no UnityEngine deps**, fully NUnit-tested in `Tests/Runtime/Designer/`. This is the
  correctness-critical core (test-heavy, per house rule).
- **`Packages/com.wevaui/Editor/Designer/`** — the Unity Editor GUI (canvas, panels,
  manipulation, live engine viewport). Editor-only.
- Figma importer converges in `Packages/com.wevaui.figma/` onto the new IR.

> Note: adding files outside globbed test dirs needs a `<Compile Include>` in the
> headless TestVerifyAll csproj — check for nonzero test counts after adding test files.

## Editor surface

- **Canvas (center):** drag move/resize/reorder; snapping + smart guides + live
  measurements; on-canvas padding/gap handles; multi-select; zoom/pan. Rendered by Weva.
- **Layers (left):** node tree, drag-to-reorder, components marked distinctly.
- **Design panel (right):** the *only* property surface — Layout, Sizing (Fill/Hug/Fixed),
  Spacing, Style, States — all token-first.
- **Tokens / Library (left tab):** manage colors/spacing/type; component library.
- **Top bar:** breakpoint switcher, state preview toggles, play-in-Unity, undo/redo.

## The hard parts (named up front)

1. **Auto-layout ⇄ flex fidelity.** Fill/Hug/Fixed must compile to predictable flex output
   across nesting. Highest-risk correctness area — big test matrix. (Leverage existing
   `LayoutMapper` + the engine's own flex tests.)
2. **Canvas manipulation feel.** Snapping, nudging, resize handles that respect Hug/Fill.
   Where "friendly" is won or lost.
3. **Responsive overrides model.** Inheritance + per-breakpoint deltas without confusing
   "where did this value come from."
4. **Live-engine performance** in the editor (incremental recompile/re-render).

---

## Production-readiness bar (definition of done)

> Directive (2026-06-07): **do not stop until this is a top-notch, production-ready
> editor.** "Feature-complete" is not "done". Every milestone ships with the depth,
> polish, and robustness below — and the build loop runs until all of it is met.

A milestone is only "done" when:
1. **Tested** — unit + engine round-trip coverage, green headlessly (house rule: test-heavy).
2. **Persists** — state survives save/load and domain reload without corruption.
3. **Reversible** — every mutation is undo/redo-able; no dead-ends.
4. **Robust** — bad input, empty states, huge docs, and errors are handled gracefully
   (no exceptions to the user; diagnostics routed, never silent-wrong — house rule).
5. **Fast** — interactive (<16ms) for typical docs; incremental recompile/re-render.
6. **Discoverable** — keyboard shortcuts, sensible defaults, empty-state guidance.
7. **Documented** — authoring docs + a runnable sample.

### Cross-cutting workstreams (threaded through every milestone, hardened in M9)
- **Persistence/serialization:** [~] `DesignSerializer` (JSON, versioned, round-trip +
  forward-compat tested, headless) done. Remaining: Unity asset wrapper + migration (M9).
- **Undo/redo:** [x] `DocumentEditor` command history (the only write path to the Document):
  reversible property + structural edits, drag-coalescing, batches, dirty/version
  tracking, `Changed` event. 16 tests, headless.
- **Clipboard:** [x] `DesignNode.Clone()` (deep copy) + `DesignClipboard` (copy/cut/paste,
  snapshot semantics) + editor `Duplicate`. 10 tests, headless. Remaining: OS-clipboard
  bridge (Editor layer).
- **Keyboard & input:** shortcuts, nudging, arrow-key reorder, multi-select.
- **Performance:** incremental compile + re-render; large-document budget tests.
- **Accessibility & i18n:** focus order, contrast in chrome, RTL-aware canvas.
- **Error handling & diagnostics:** [x] `DesignValidator` — read-only diagnostics pass
  (unknown component/variant/token refs, undeclared props, invalid repeat, unknown
  events, misplaced/duplicate slots, component cycles); 15 tests. Editor surfaces these.
  Remaining: wire into the GUI + load-path exception wrapping.
- **Docs & samples:** [~] `Documentation~/DesignerModel.md` — full model-layer API
  reference (layout/sizing/tokens/states/binding/components/persistence/undo/clipboard/
  templates/validation + pipeline) done. Runnable Unity sample pending (Editor stack).

## Milestones (build-loop tracker)

Each milestone is shippable/demoable on its own. Implement in order; keep the core
headless and test-heavy. Tick boxes as they land; add commit hashes.

### M1 — Design Document IR + compiler (headless, fully tested)
- [x] `Runtime/Designer/` IR types: `DesignDocument`, `DesignNode`, enums
      (`LayoutMode`, `SizeMode` = Fill/Hug/Fixed, `MainAlign`/`CrossAlign`), `DesignTokens`.
- [~] Token model: **colors done** (`{token}` refs → `:root` custom props + `var()`,
      unknown-token magenta fallback). Spacing/type/radii/shadows/breakpoints → M4.
- [x] Compiler `DesignCompiler: DesignDocument → (html, css)` emitting clean scoped Weva
      HTML/CSS (one generated class per node, no cascade conflicts). Mirrors figma
      `LayoutMapper`; full convergence in M8.
- [x] Fill/Hug/Fixed → flex compilation rules, documented + table-tested.
- [x] NUnit suite in `Tests/Runtime/Designer/`: compiler unit suite (28 tests) + engine
      round-trip suite (6 tests) — all green headlessly.
- [x] Verify: headless test count nonzero & green (34 passed, 0 failed).
- [x] Engine round-trip test (compile → cascade + `LayoutEngine` → assert real boxes:
      equal Fill split, Fixed+Fill mix, padding content-area, column gap, cross-stretch).

**M1 COMPLETE.**

### M2 — Read-only canvas (live engine)
- [ ] `Editor/Designer/` window hosting the Weva engine in an editor viewport.
- [ ] Render a `DesignDocument` (compiled) live; reuse headless capture path for thumbs.
- [ ] Layer tree (read-only) reflecting the node tree.

### M3 — Auto-layout authoring (the "is this friendly?" proof)
- [ ] Stack ↓/→ + Fill/Hug/Fixed + padding/gap editing via Design panel.
- [ ] Canvas direct manipulation: select, move, resize (respecting sizing modes), reorder.
- [ ] Snapping + smart guides + live measurements.
- [ ] Mutation-based undo/redo (all writes go through Document mutations).

### M4 — Tokens + Style panel
- [x] **Token model (core):** `Dim` value type (px *or* token ref) + token tables for
      colors / spacing / radii / type / shadows; gap/padding/radius/font-size/shadow all
      tokenizable; `:root` custom-property emission; serializer round-trip. 11 tests.
- [ ] Token manager UI (colors/spacing/type/radii/shadows).  *(Editor GUI — M2 stack)*
- [ ] Style panel: fill/stroke/radius/shadow/opacity/text — token-first pickers, raw
      values behind an "advanced" affordance.  *(Editor GUI — M2 stack)*

### M5 — Components & variants
- [x] Component definition (props via `$name`, single slot, variants) + instances that
      reference by name. `DesignExpander` expands instances (defaults ⊕ variant ⊕
      instance props), fills slots, applies instance sizing, with cycle backstop.
- [x] Editing a component updates all instances; compiler emits scoped (no cascade
      conflicts). Serializer round-trip; editor AddInstance/SetVariant/SetInstanceProp
      with undo. 10 tests.
- [ ] Library panel to drag components onto canvas.  *(Editor GUI — M2 stack)*

### M6 — States + responsive
- [x] **States (core):** `StateStyle` per-state overrides on `DesignNode`
      (hover/pressed/focus/disabled) → compile to `:hover`/`:active`/`:focus`/`.is-disabled`;
      token-resolving; serializer round-trip; editor setters + undo. 10 tests.
- [ ] State chips UI.  *(Editor GUI — M2 stack)*
- [ ] Breakpoints with visible inheritance; per-breakpoint overrides badged.
- [ ] Prefer container/constraint-driven sizing so most layouts adapt automatically.

### M7 — Data binding (the unfair advantage)
- [x] **Binding model (core):** `NodeBinding` on `DesignNode` — text (`{{ path }}`),
      list-repeat (`data-each`/`data-key`), class toggles (`data-class-*`, covers
      visibility), and events (`on-click`/etc → controller methods). Compiler emits the
      engine's binding markup; serializer round-trip; editor setters + undo. 10 tests.
- [ ] Visual bind UI (data picker).  *(Editor GUI — M2 stack)*

### M8 — Templates, Figma import, library polish
- [~] Starter screens: `DesignTemplates` (Blank, Main Menu, Combat HUD, Settings) +
      `Catalog()` for the picker + reusable fragments (button/badge/row), token-themed.
      7 tests (compile, layout-at-size, serialize round-trip).
- [x] **Base component kit:** `DesignComponentKit` (Button+variants, Card, Panel,
      SettingRow, Heading, ListItem) + theme tokens + `Install(doc)`. Fully token-driven,
      every component validates clean. 7 tests.
- [ ] Converge Figma importer onto the IR: `FigmaNode → DesignDocument` → same compiler.
- [ ] Lossless round-trip tests (Figma → IR → HTML/CSS).

### M9 — Production hardening (the "top-notch" pass)
- [~] Persistence: versioned `DesignDocument` JSON (done); Unity asset wrapper + migration
      tests pending (needs Editor stack).
- [x] Undo/redo + clipboard: covered by their own suites (DocumentEditor, clipboard,
      state/binding/component editor ops).
- [x] Performance: large-document compile + serialize round-trip budget tests (~3000
      nodes < 2s each).
- [x] Robustness: edge-case suite — deep/wide trees, component cycles (self + mutual,
      terminate), malformed JSON, empty doc, unicode, punctuation token names. 11 tests.
- [ ] Accessibility/i18n pass on the editor chrome; RTL canvas.  *(Editor GUI — M2 stack)*
- [ ] Authoring docs + a polished sample project; onboarding/empty states.
- [x] Full headless suite green except 3 PRE-EXISTING reds (drop-shadow filter ×2,
      backdrop golden) — zero new regressions from the editor work (6738/3/30).

---

## Status log

- 2026-06-07: Plan created. Surveyed repo; found figma package already has an
  auto-layout IR + working compiler (`Mapping/`) to reuse. M1 reshaped around extracting
  a neutral IR + compiler rather than building from scratch.
- 2026-06-07: M1 core landed. `Runtime/Designer/` IR (`DesignDocument`/`DesignNode`/
  enums/`DesignTokens`) + `DesignCompiler` (Stack + Fill/Hug/Fixed → flex, padding/gap/
  align, color tokens, text). 28-test unit suite green via headless TestVerifyAll
  (added `Weva.Tests.Designer` to runner allowlist + csproj include). Remaining M1:
  engine round-trip test; geometry/type tokens deferred to M4.
- 2026-06-07: **M1 COMPLETE.** Added engine round-trip suite (6 tests) compiling Design
  Documents through the real cascade + LayoutEngine and asserting boxes (Fill split,
  Fixed+Fill, padding, column gap, cross-stretch). 34 Designer tests total, all green.
  Added a production-readiness bar + cross-cutting workstreams + M9 hardening milestone
  per the "don't stop until production-ready" directive. Next: M2 (read-only live canvas).
- 2026-06-07: Persistence foundation landed. Hand-rolled JSON DOM/parser/writer
  (`Runtime/Designer/Serialization/Json.cs`, Unity- + headless-safe) + `DesignSerializer`
  (versioned, non-default-only emit, deterministic). 12 tests: round-trip stability,
  reloaded-doc-compiles-identically, unknown/missing-key tolerance, escaping, fractions.
  46 Designer tests total, all green. Building headless foundations (serialize → undo)
  ahead of the Unity-GUI milestones so each chunk stays verifiable. Next: undo/command
  history.
- 2026-06-07: Undo/redo foundation landed. `Runtime/Designer/Editing/`: `DocumentEditor`
  (single write-path: typed property + structural mutations, drag-coalescing via merge
  keys, batch transactions, dirty/version tracking, `Changed` event) + reversible
  command primitives. 16 tests. 62 Designer tests total, all green. Next: a small
  builder/sample + then start the Unity Editor canvas (M2) on these foundations.
- 2026-06-07: Clipboard foundation landed. `DesignNode.Clone()` (detached deep copy),
  `DesignClipboard` (copy/cut/paste with snapshot semantics) + `DocumentEditor.Duplicate`.
  10 tests. 72 Designer tests total, all green. Headless foundations complete (compiler,
  persistence, undo/redo, clipboard). Next: starter templates as code + a runnable
  sample, then begin the Unity Editor GUI (M2 live canvas).
- 2026-06-07: Starter templates landed. `Runtime/Designer/Templates/DesignTemplates`
  (Blank / Main Menu / Combat HUD / Settings + `Catalog()` + button/badge/row fragments,
  fully token-themed). 7 tests (compile, layout-at-declared-size, serialize round-trip).
  79 Designer tests, all green. **Full-suite checkpoint: 6697/3/30 — the 3 reds (2
  drop-shadow filter + 1 backdrop-modal golden) are PRE-EXISTING on master `c9a1cfe6`,
  confirmed not caused by the editor branch** (verified against the branch point). Logged
  in memory as a baseline regression to fix separately. Next: begin M2 Unity Editor GUI.
- 2026-06-07: M4 token model (core) landed. Introduced `Dim` (px-or-token value type);
  extended `DesignTokens` to colors/spacing/radii/type/shadows with `:root` emission;
  gap/padding/radius/font-size/shadow are all tokenizable; serializer handles Dim
  (number or `{token}`) + all token tables; added `Shadow` field + box-shadow emission;
  editor setters take `Dim`. Refactor verified: all 79 prior tests still green + 11 new
  token tests = 90 Designer tests, all green. (Staying on the headless model layer to
  avoid overlap with the parallel `feat/editor-panels` rendering branch.) Next: M6 states
  (hover/pressed/focus → pseudo-classes) — pure compiler work, headless.
- 2026-06-07: M6 states (core) landed. `StateStyle` per-state overrides on `DesignNode`
  (hover/pressed/focus/disabled) → `:hover`/`:active`/`:focus`/`.is-disabled` rules,
  token-resolving, deterministic order; serializer round-trip; editor setters
  (SetStateFill/TextColor/Shadow/Radius/Opacity + ClearState) with undo. 10 tests.
  100 Designer tests total, all green. Next: M7 data binding (text/visibility/repeat →
  `{{ }}` templates) — headless, the game-UI unfair advantage.
- 2026-06-07: M7 data binding (core) landed. `NodeBinding` on `DesignNode`: text bind
  (`{{ path }}`), list-repeat (`data-each`/`data-key`), class toggles (`data-class-*`,
  covers visibility), events (`on-click`/change/input/submit/focus/blur → controller
  methods). Compiler emits engine-compatible binding markup; serializer round-trip;
  editor setters (SetTextBind/SetRepeat/BindClass/BindEvent/ClearBinding) with undo.
  10 tests. 110 Designer tests total, all green. **Model layer feature-complete (M1,M4,
  M5-partial,M6,M7 cores + persistence/undo/clipboard/templates).** Next: M5 components
  & variants (the remaining model-layer feature), then converge with editor-panels GUI.
- 2026-06-07: **M5 components & variants landed — MODEL LAYER NOW FEATURE-COMPLETE.**
  `DesignComponent` (template + `$name` props + variants + slot); instances reference by
  name; `DesignExpander` expands them (defaults ⊕ variant ⊕ instance props, slot fill,
  instance sizing override, cycle backstop) before compile. Serializer round-trip for the
  component library + instance fields; editor AddInstance/SetVariant/SetInstanceProp with
  undo. 10 tests. **120 Designer tests total, all green.** The headless model layer is
  done: IR+compiler, tokens, states, binding, components, persistence, undo, clipboard,
  templates. Next: M9 hardening (perf/large-doc + fuzz/edge-case suites, still headless)
  while the GUI converges on `feat/editor-panels`.
- 2026-06-07: M9 hardening (headless portion). Full-suite regression confirmed
  6738/3/30 — the only 3 reds are pre-existing (drop-shadow filter ×2, backdrop golden),
  zero new regressions from the entire model layer. Added `DesignRobustnessTests`:
  deep (300) + wide (1000) trees, self- and mutually-recursive component termination
  (cycle backstop), malformed-JSON-throws-cleanly, empty doc, unicode round-trip,
  punctuation token-name sanitization, and large-doc (~3000 node) compile + serialize
  budgets (<2s). 11 tests. 131 Designer tests total, all green. Remaining M9 (asset
  wrapper/migration, a11y, docs/sample) is Editor-GUI-stack work → converge with
  `feat/editor-panels`.
- 2026-06-07: `DesignValidator` diagnostics pass landed (Validation/). Reports unknown
  component/variant/token refs, undeclared props, invalid repeat syntax, unknown events,
  slots outside components / multiple slots, and component cycles — severity-tagged with
  stable codes for the GUI. Starter templates validate clean. 15 tests. 146 Designer
  tests total, all green. Headless model layer + tooling now genuinely production-grade.
  Remaining editor work is Unity-GUI (canvas/panels/asset-wrapper/a11y/docs) → converge
  with `feat/editor-panels`; began authoring docs next.
- 2026-06-09: **Transform controls (rotate / scale)** added to the inspector for all nodes
  (base style). Two drag-scrubs: Rotate (° , −180..180) and Scale (shown as %, 10–400) →
  `SetRotation`/`SetScale` (paint-time, no layout effect — tilted badges, emphasis pop). New
  `rot`/`scale` scrub keys + `OnStepRotation`/`OnStepScale`. **Editor-only batch.** (Stronger
  pre-commit check now: all 71 referenced handler names — incl. scrub step methods passed as
  args — resolve to a definition.)
- 2026-06-09: **Typography controls completed** — added a letter-spacing drag-scrub (can go
  negative to tighten, min -20) and **Text-shadow presets** (None/S/M/L, same shape as the
  box-shadow presets) to the text inspector. The text-shadow presets surface the model-layer
  TextShadow feature added earlier today, so a designer can now make HUD text legible over busy
  backgrounds. New `TextShadowPresets`/`TextShadowCss`, `OnStepLetterSpacing`/`OnSetTextShadow`
  + `ls` scrub key. **Editor-only batch.**
- 2026-06-09: **Per-corner radius + link toggle** (rounded-top tabs, single-corner cards — the
  node stored four corner overrides + `SetCornerRadii` but the GUI only did uniform). Added a
  🔗 Linked / Per-corner toggle under the radius presets: linked shows the uniform Radius scrub,
  per-corner shows Top L / Top R / Bot R / Bot L scrubs (each seeded from the corner's effective
  value, written via `SetCornerRadii`). Toggling back to linked collapses the overrides so the
  uniform radius takes over (Figma's link-reset). New `RadiusControl` + `radtl/tr/br/bl` scrub
  keys → `ApplyCorner`, `OnToggleRadiusLink` / `OnStepRad{TL,TR,BR,BL}`. **Editor-only batch.**
- 2026-06-09: **Grid layout + Wrap exposed** in the inspector (both were implemented + tested in
  the model but unreachable from the GUI). Layout chips gained **Grid**; when Grid, a **Columns**
  drag-scrub (`cols` key → `SetGridColumns`, min 1). Added a **Wrap** toggle for Row/Column
  (`SetWrap`). Also gated the alignment pad / Main / Cross / Wrap to flex containers only (Grid
  arranges via columns+gap, so justify/align/wrap don't apply) — removes a silent-no-op trap.
  New `OnToggleWrap` / `OnStepCols` handlers. **Editor-only; in the unverified batch.**
- 2026-06-09: **Per-side padding + link toggle** (Figma's spacing control). The GUI only did
  uniform padding though the node always stored four sides; added a 🔗 Linked / Per-side toggle
  (editor-only UI state) — linked shows one "All" drag-scrub, per-side shows Top/Right/Bottom/
  Left scrubs, each writing one side via the existing `SetPadding` (one merge key ⇒ a side drag
  is one undo). New `PaddingControl` renderer, `padt/r/b/l` scrub keys + `ApplyPadSide`, and
  `OnTogglePadLink` / `OnStepPad{T,R,B,L}` handlers. **Editor-only; in the unverified batch.**
  (Self-checks: brace/paren balance + all 24 referenced on-click/pointerdown handlers resolve.)
- 2026-06-09: **Figma 3×3 alignment pad** added to the inspector (Row/Column). One click on a
  cell sets both main- and cross-axis alignment at once, direction-aware (horizontal→main for a
  Row, →cross for a Column), as a single batched undo; the lit cell reflects the current
  alignment. The Main/Cross chips stay below for SpaceBetween / Stretch (which a 9-cell pad
  can't express). New `AlignPad`/`AlignIndices` renderers + `.wd-align-pad` chrome +
  `OnSetAlignPad` handler (BeginBatch/EndBatch). **Editor-only; joins the unverified editor
  batch awaiting one Unity compile + visual pass.**
- 2026-06-08: **Inspector now surfaces the rich model props** (the model layer was far ahead
  of the GUI — Stroke / FontWeight / Italic / TextAlign / TextTransform / TextDecoration all
  tested but unreachable). Added, reusing the existing Chips/Swatches/Scrub helpers: text
  Weight / Align / Case / Decoration chips + an Italic toggle (text nodes), and a Stroke colour
  swatch + drag-scrub Border width (all nodes, base style). Chip rows now `flex-wrap` so they
  never overflow the panel. New tiny Model handlers (OnSetFontWeight/TextAlign/Transform/
  Decoration/ToggleItalic/SetStroke/StepStrokeW) + a `strokew` scrub key. **Editor-only (`#if
  WEVA_URP`); joins the scrub change awaiting one Unity compile + visual pass.**
- 2026-06-08: **Inspector drag-to-scrub numeric controls** (Figma-signature usability — the
  ± steppers required dozens of clicks to cross a range). The numeric values (Font, Gap,
  Padding, Radius, W, H, Opacity) are now drag-to-scrub: press the value and drag left/right,
  ±1 step per ~4px, clamped to min/max. The − / + chips stay for one-click fine control. Reuses
  the proven wd-root pointer-drag pattern (held-button self-heal, per-node merge-key coalescing
  so a whole drag is one undo). New `Scrub()` renderer + `.wd-scrub` chrome + Model scrub
  handlers (`OnScrubDown`/`TrackScrub`/`ScrubCurrent`/`ApplyScrub`). **Editor-only (`#if
  WEVA_URP`); not headlessly compilable — needs a Unity compile + visual pass before "done".**
- 2026-06-08: **Text-shadow landed** (HUD legibility — the iconic glyph drop-shadow that
  keeps numbers/titles readable over busy backgrounds). `DesignNode.TextShadow` (raw CSS
  `text-shadow` or a `{shadow-token}` resolved via the shared shadow table; raw passes
  through). Distinct from the box-level `Shadow` — a node can carry both. Gated to text +
  text-binding nodes (matches the other text props); default off; serializer round-trip;
  `DocumentEditor.SetTextShadow` + undo/redo; `Clone` deep-copies. `DesignTextShadowTests`
  (9). **291 Designer tests green** (was 282); full suite 8532/0/16.
- 2026-06-08: **Component kit polished with the new props** (production quality, not just
  feature-complete). Button: `cursor:pointer` + `transition:120ms` + SemiBold label;
  ListItem: pointer + smooth-hover transition; Card: `overflow:clip` so content respects the
  rounded corners (outer shadow unaffected). 3 new kit tests pin the polish. Designer suite
  282 green. Merged to master.
- 2026-06-08: **Transform (rotate/scale) landed** (tilted badges, emphasis pop — paint-time,
  no layout effect). `Rotation` (deg, 0=none) + `Scale` (1=none) compose into one `transform:
  rotate(Ndeg) scale(S)`. Serializer round-trip (Scale defaults to 1, survives reload as 1);
  `DocumentEditor` SetRotation/SetScale + undo; Clone. `DesignTransformTests` (8). Designer
  suite 279 green.
- 2026-06-08: **Flex-wrap landed** (tag lists, button groups that overflow). `Wrap` bool →
  `flex-wrap: wrap` on Row/Column containers; default off; serializer round-trip;
  `DocumentEditor.SetWrap` + undo; Clone. `DesignWrapTests` (7) incl. a **real-engine
  round-trip** (3×80px children in a 200px row → 3rd wraps below). Merged to master.
- 2026-06-08: **Background-image fills landed** (textured panels, item thumbnails). `Background
  Image` (URL/path) → `background-image: url("…")` + size/position:center/repeat:no-repeat,
  layered after the colour-fill shorthand so it survives the shorthand's reset; `Background
  Size` Cover/Contain/Stretch (=`100% 100%`). URL escaped against `")`-breakout via new
  `DesignCssText.Url`. Serializer round-trip; `DocumentEditor` SetBackgroundImage/SetBackground
  Size + undo; Clone. `DesignBackgroundImageTests` (8). **264 Designer tests green**.
- 2026-06-08: **Interactivity polish landed** (`Cursor` Default/Pointer → `cursor: pointer`
  clickable affordance; `TransitionMs` → `transition: all <ms>ms ease` to smoothly animate
  into hover/pressed). Both default off; serializer round-trip; `DocumentEditor` SetCursor/
  SetTransition + undo; Clone. `DesignInteractivityTests` (9). **256 Designer tests green**.
- 2026-06-08: **Typography-in-states landed** (the iconic underline-link-on-hover, plus
  bold-on-hover). `StateStyle.TextDecoration?` + `FontWeight?` → emit into the state's
  pseudo-class rule; `None` is a meaningful override (emits `text-decoration: none` to
  remove a base underline). Serializer round-trip; `DocumentEditor` SetStateTextDecoration/
  SetStateFontWeight + undo. `DesignStateTypographyTests` (5). **247 Designer tests green**.
- 2026-06-08: **Rich text props landed** (letter-spacing incl. negative/tighten,
  text-transform UPPERCASE/lowercase/Capitalize, text-decoration underline/line-through —
  button labels, links, struck prices). Emit on text + text-binding nodes only; defaults
  emit nothing; serializer round-trip; `DocumentEditor` SetLetterSpacing/SetTextTransform/
  SetTextDecoration + undo; Clone. `DesignTextStyleTests` (11). **242 Designer tests green**.
- 2026-06-08: **Per-corner radius landed** (rounded-top tabs, cards with one rounded
  corner). Optional `RadiusTopLeft/TopRight/BottomRight/BottomLeft` (Dim?, null=inherit the
  uniform `Radius`); any set ⇒ 4-value `border-radius` shorthand (TL TR BR BL) with unset
  corners falling back to uniform; tokens resolve per corner. Serializer round-trip;
  `DocumentEditor.SetCornerRadii` + undo; Clone; validator checks each corner's token.
  `DesignCornerRadiusTests` (7). **231 Designer tests green** (was 224).
- 2026-06-08: **Validator geometry-mistake diagnostics landed** (the new sizing/placement
  props made silent-mis-render traps easy to hit). `CheckGeometry` flags max<min (w/h,
  Warning), Fixed-mode-with-no-size (Warning, "it will hug instead"), edge offsets on a
  non-absolute node (Info), and negative aspect-ratio (Warning) — all non-error so they
  never block save/compile. Starter templates still validate clean. `DesignGeometry
  ValidationTests` (9). **224 Designer tests green** (was 215).
- 2026-06-08: **Aspect-ratio landed** (item icons, thumbnails, 16:9 media). `AspectRatio`
  (width÷height, 0=unset) → CSS `aspect-ratio`. Serializer round-trip; `DocumentEditor.
  SetAspectRatio` + undo; Clone. `DesignAspectRatioTests` (7) incl. a **real-engine
  round-trip** (200px wide + 2:1 → 100px tall, derived by the engine). **215 Designer
  tests green** (was 208).
- 2026-06-08: **Gradient fills landed** (fill was colour-only as a *token*; raw gradient
  strings already passed through but a designer couldn't pick a *named* gradient). New
  `DesignTokens.Gradients` table + `Gradient()` builder + `--gradient-*` `:root` emission;
  `ResolvePaint` resolves a fill `{token}` as colour OR gradient (gradient checked first),
  used for base + state fill; validator accepts a gradient token on fill (still flags
  unknown); serializer round-trip. `DesignGradientTests` (9). **208 Designer tests green**.
- 2026-06-08: **Absolute/overlay placement landed** (HUD badges, corner buttons, overlays —
  the out-of-flow capability game UI needs). `Position` InFlow/Absolute + nullable edge
  offsets `OffTop/Right/Bottom/Left` (Dim, px or spacing token; null=unpinned, 0=pinned to
  edge). Absolute → `position:absolute` + offsets, sized via the root path (flex props are
  meaningless out of flow); a parent with any absolute child auto-emits `position:relative`
  to become the positioning context. Serializer round-trip; `DocumentEditor` SetPosition/
  SetOffsets + undo; Clone. `DesignPositionTests` (10) incl. a **real-engine round-trip**
  (badge pins to parent top-right corner). **199 Designer tests, all green** (was 189).
- 2026-06-08: **Min/max size constraints landed** (the constraint-driven escape hatch the
  responsive model leans on — "fill, but never exceed 400px"; "at least 200px"). `MinWidth/
  MaxWidth/MinHeight/MaxHeight` (px, 0=unset) → min/max-width/height, applied independent of
  sizing mode. Guarded the Fill main-axis `min-width:0` floor so an author min replaces it
  without a duplicate decl (CssDecls.Set appends, not overwrites). Serializer round-trip;
  DocumentEditor Set{Min,Max}{Width,Height} + undo; Clone. `DesignConstraintsTests` (9) incl.
  **real-engine round-trips** (max-width caps a Fill child at 200; min-width floors a Hug
  child at 150). **189 Designer tests, all green headlessly** (was 180).
- 2026-06-08: **Overflow/clip landed** (`Overflow` Visible/Clip/Scroll → `overflow`
  visible/hidden/auto — e.g. a card cropping its image to its rounded corners, or a
  scrollable list region). Visible default emits nothing; serializer round-trip;
  `DocumentEditor.SetOverflow` + undo; `Clone` copies it. `DesignOverflowTests` (7).
  **180 Designer tests, all green headlessly** (was 173).
- 2026-06-08: **Grid layout landed** (was half-wired: `LayoutMode.Grid` existed in the
  enum + serializer but the compiler emitted nothing, so a Grid node silently fell through
  to block flow — a house-rule "silent-wrong"). Minimal friendly model: `GridColumns` (N
  equal columns) → `display: grid; grid-template-columns: repeat(N, minmax(0, 1fr))` (the
  minmax(0,…) guards the classic grid min-content overflow trap) + gap; ≤1 ⇒ single track.
  Serializer round-trip; `DocumentEditor.SetGridColumns` with undo; `Clone` copies it. New
  `DesignGridTests` (10) incl. two **real-engine round-trips** proving the grid splits its
  width (100/100/100; 80 with gap). **173 Designer tests, all green headlessly** (was 163).
- 2026-06-08: **Typography style landed** (the "text" half of the Style surface had only
  colour + size). `DesignNode.FontWeight` (Normal/Medium/SemiBold/Bold → 400/500/600/700),
  `Italic`, `TextAlign` (Start/Center/End/Justify → logical `text-align`), `LineHeight`
  (unitless multiplier). Compiler emits onto text + text-binding nodes only (matches the
  colour/size gating); neutral values emit nothing; serializer round-trip; `DocumentEditor`
  SetFontWeight/SetItalic/SetTextAlign/SetLineHeight with undo; `Clone` copies them. New
  `DesignTypographyTests` (12). **163 Designer tests, all green headlessly** (was 151).
- 2026-06-08: **Stroke/border style property landed** (closes a real gap — the plan's
  Style surface lists `fill/stroke/radius/shadow/opacity/text` but the model had no
  border). `DesignNode.Stroke` (color, tokenizable) + `StrokeWidth` (px, 1px default à la
  Figma); compiler emits `border: <w> solid <color>`; `StateStyle` per-state stroke
  (full `border` when colour set, `border-width` when only weight) for focus rings;
  serializer round-trip; validator flags unknown stroke colour tokens; `DocumentEditor`
  SetStroke/SetStrokeWidth/SetStateStroke(+Width) with undo; `Clone` copies it. New
  `DesignStrokeTests` (14). **151 Designer tests, all green headlessly** (was 137).
- 2026-06-07: Authoring docs (`Documentation~/DesignerModel.md`) + base component kit
  (`DesignComponentKit`: Button/Card/Panel/SettingRow/Heading/ListItem + theme +
  `Install`) landed. Fully token-driven, every component validates clean. 7 tests.
  153 Designer tests total, all green. Headless value largely exhausted — remaining is
  the Unity Editor GUI (canvas/panels/manipulation) which overlaps `feat/editor-panels`.
  **Next: bring the user a focused decision on GUI convergence** (merge editor-panels vs
  build GUI here vs leave GUI to that branch), then proceed.
