# AGENTS.md — guidance for AI tools working in this repo

This file is the contract between this codebase and any AI coding assistant
(Claude Code, Cursor, Copilot, etc.) operating inside it. Read it before
making changes.

The codebase is a Unity package: a runtime HTML/CSS UI engine. It is **not**
Unity's own UI Toolkit (UXML/USS); the design rule is "real web HTML/CSS or
nothing", because the value proposition is "LLM-trained UI knowledge runs
unchanged here." A subtly-different behavior is worse than missing — see
`Packages/com.wevaui/README.md` for the why.

---

## 1. Where things live

```
weva/
├── Assets/                              Demo + samples wired into a scene
│   ├── UI/                              randhtml.html / .css — the demo doc
│   ├── Scripts/UitestController.cs      Demo controller (font registration,
│   │                                    [UIBind] fields, click handlers)
│   └── Settings/                        URP renderer + pipeline assets
│
├── Packages/com.wevaui/                The actual package (UPM-installable)
│   ├── Runtime/
│   │   ├── Animation/                   CSS transitions + keyframes engine
│   │   ├── Binding/                     [UIBind], {{templates}}, BindingScanner
│   │   ├── Components/                  Custom-element <my-card> registry
│   │   ├── Css/
│   │   │   ├── Cascade/                 CascadeEngine, ComputedStyle, properties
│   │   │   ├── Container/               @container queries
│   │   │   ├── Media/                   @media queries
│   │   │   ├── Parsing/                 Hand-rolled CSS tokenizer + parser
│   │   │   ├── Selectors/               :has, :is, :where, attr ops, etc.
│   │   │   └── Values/                  CssLength/Color/Calc/Percentage…
│   │   ├── DevTools/                    F12 IMGUI overlay (outlines, perf)
│   │   ├── Document/                    UIDocumentBuilder, UIDocumentLifecycle
│   │   ├── Dom/                         Node, Element, TextNode, Document
│   │   ├── Events/                      Dispatcher, focus, hit testing,
│   │   │   └── Manipulators/            Pan / contextual-menu gesture wrappers
│   │   ├── Forms/                       Buttons, inputs, checkbox, radio,
│   │   │                                select, textarea, dialog, popover,
│   │   │                                range slider, tooltip, context menu,
│   │   │                                virtualized list
│   │   ├── HotReload/                   .html/.css watcher (no domain reload)
│   │   ├── Layout/
│   │   │   ├── Flex/                    Own flexbox impl (no Yoga)
│   │   │   ├── Grid/                    CSS Grid (UI Toolkit doesn't have this)
│   │   │   ├── AnchorPositioning/       CSS Anchor Positioning Level 1
│   │   │   ├── Boxes/                   BlockBox, InlineBox, LineBox, TextRun
│   │   │   ├── Positioning/             absolute/fixed/sticky resolution
│   │   │   ├── Scrolling/               ScrollContainer, smooth scroll, snap
│   │   │   ├── Tables/                  display: table-* (partial)
│   │   │   ├── Text/                    InlineLayout, LineBreaker
│   │   │   ├── Snapshot/                NodeId-array fast-path build
│   │   │   ├── BlockLayout.cs           Block-flow + margin collapsing
│   │   │   └── LayoutEngine.cs          Top-level orchestrator
│   │   ├── Paint/
│   │   │   ├── Conversion/              Box → PaintCommand walk
│   │   │   ├── Brush.cs, BorderRadii.cs, BoxShadow.cs, Transform2D.cs
│   │   │   └── PaintCommand.cs          Sealed class hierarchy of commands
│   │   ├── Parsing/                     Hand-rolled HTML tokenizer + parser
│   │   ├── Reactive/                    InvalidationTracker, dirty propagation
│   │   ├── Rendering/
│   │   │   ├── Backend/                 IRenderBackend, IUIPaintSource
│   │   │   ├── Shaders/                 Weva-Quad.shader (über-shader)
│   │   │   └── URP/                     UIBatcher, UIRenderGraphPass, ShaderLib
│   │   ├── Text/
│   │   │   ├── Sdf/                     SdfFontMetrics, glyph atlas adapter
│   │   │   ├── TextCore/                Backend over Unity TextCore
│   │   │   ├── Tmp/                     TMP_FontAsset bridge (preferred)
│   │   │   └── Unity/                   Bundled-default registration
│   │   ├── ViewTransitions/             View Transitions API
│   │   └── UIDocument.cs                MonoBehaviour entry point
│   ├── Tests/                           ~3850 NUnit tests
│   ├── Editor/                          Editor preview window, importers
│   └── README.md                        User-facing reference
│
├── README.md                            Top-level index (short)
├── PLAN.md                              Architecture + roadmap
├── CONFORMANCE.md                       Spec-vs-impl deltas (read before
│                                        adding anything that "looks like CSS")
└── CHANGELOG.md                         Append-only history of additions
```

The whole engine is **headless-testable**. `Tests/Runtime/` doesn't touch
UnityEngine; tests run via `Tools/BaselineGen/` outside the editor too. Keep
it that way — Unity-specific dependencies live in `Runtime/Rendering/URP/*`,
`Runtime/Forms/Bridge/*.Unity.cs`, etc., gated behind defines.

## 2. The pipeline

A frame's life:

```
HTML/CSS TextAssets
  → HtmlParser / CssParser            (Parsing/)
  → Document tree (DOM)               (Dom/)
  → ComponentExpander                 (Components/)
  → CascadeEngine.ComputeAll          (Css/Cascade/) — produces ComputedStyle
  → CssAnimationRunner                (Css/Animation/) — interpolates active anims
  → BoxBuilder                        (Layout/) — Document → Box tree
  → LayoutEngine.Layout               (Layout/) — block / flex / grid / IFC
  → PositioningPass                   (Layout/Positioning/) — absolute/fixed/sticky
  → BoxToPaintConverter.Convert       (Paint/Conversion/) — Box → PaintList
  → BatchedURPRenderBackend.Submit    (Rendering/URP/) — PaintList → UIQuadInstances
  → UIRenderGraphPass.DrainBatches    (Rendering/URP/) — single GPU draw call
```

**Per-frame entry point:** `UIDocumentLifecycle.Update(state, controller, t)`.

**Every stage caches** keyed on input version numbers from
`Reactive/InvalidationTracker`. Clean subtrees skip every stage. Reading the
cache layout and invalidation propagation rules in PLAN.md §12 before
touching anything cache-adjacent will save you hours.

## 3. Conventions you must follow

### Layout / paint cache invariants

* Every layer's cache key is **input versions**, not heuristic dirty bits.
  When you change inputs, bump the version; when you change cache shape, bump
  every key that pulls from it. Never invalidate "globally" — propagate
  through the tracker. The cascade went 90× faster (7.5 ms → 0.083 ms for a
  single hover flip on 1000 elements) once we did this.

* The `BoxPool`, `PaintListPool`, and `CommandPool` recycle aggressively.
  Anything you allocate from them must be returned. `Box.ResetForPool()` is
  the contract for what each subclass clears — fields you add must be cleared
  there too, or you'll see stale state bleed across frames.

* `UIDocument.NeedsRepaint` short-circuits per-frame paint conversion when
  nothing has changed. Anything you mutate at runtime must mark the tracker
  dirty (or equivalently, the change goes through DOM mutation events which
  do it for you). If you bypass the DOM and mutate Box fields directly, you
  must `MarkDirty` yourself or your change will never reach the screen.

### CSS + selectors

* Real-web behavior or nothing. If you're tempted to add a property called
  `-weva-foo`, stop and read the README "Why" section.
* Properties register in `Css/Cascade/CssProperties.cs`. Inheritance flag
  must match the spec.
* Selectors compile in `Css/Selectors/SelectorParser.cs`; when adding a
  pseudo-class, also extend `PseudoClassKind`, the matcher, and the
  state-propagation tests.
* `var()` resolution has cycle detection; don't bypass it.

### Layout

* Box flow goes block → inline (within block) → flex / grid / table when the
  display says so. Read `BlockLayout.cs` for the orchestration entry point.
* Flex implementation is hand-rolled (no Yoga). Stay consistent.
* When you change shrink-to-fit / inline-block measurement, run
  `RandhtmlLayoutDumpTest` headlessly and `compare_coords.js` against a
  Chrome dump to see the regression.

### Paint / rendering

* `PaintCommand` is a sealed hierarchy. Adding a kind means
  `PaintCommand.cs`, `IRenderBackend` Submit overload, the recording backend,
  the URP backend (`BatchedURPRenderBackend`), and likely the
  `UIBatcher` / shader if it's a new visual.
* The über-shader (`Weva-Quad.shader`) is keyword-stripped per visual:
  `_BORDERED`, `_TEXT`, `_BRUSH_LINEAR`, etc. New keywords must be added to
  `multi_compile_local` and the C# pass that picks the variant.
* Per-instance data layout is in `UIQuadInstance.cs`; slots 0..13 each carry
  a `float4`. There's no room for new slots without bumping
  `WEVA_INSTANCE_FLOAT4S` and the buffer-size math.

### Text / fonts

* Three text backends coexist: `SdfFontMetrics` (Unity FontEngine SDF),
  `TmpFontMetrics` (TMP_FontAsset), `UnityGUIFontMetrics`/`MonoFontMetrics`
  (fallbacks). `SdfBootstrap.PickBest` decides; controller code may register
  TMP assets via `TmpFontAssetRegistry`.
* `<input type="range">` and similar form-control behaviors live in
  `Forms/*Controller.cs`. There is no central auto-attacher — controllers
  are attached imperatively by the controller code (or the demo).
* Em-relative font-size resolution requires the parent box's resolved fs.
  `InlineLayout.MakeItem` threads `parentStyle` through `CollectInline` for
  this; do not regress that.

### Reactive bindings

* `[UIBind]` on a controller field exposes it to `{{ binding }}` templates
  in HTML/CSS. `BindingScanner` scans the document at build time;
  `BindingSet.Update(controller, tracker)` is called from
  `UIDocumentLifecycle.Update`.
* Bindings are reflection-based by default and source-generator-friendly
  (see `Runtime/Generators/`). IL2CPP players need the source-generator
  variant; don't introduce reflection-only paths in hot loops.

### Performance discipline

* Steady-state allocations are budgeted. `Tools/PerfBench/` drives the
  numbers in `Packages/com.wevaui/README.md`. When your change adds GC
  pressure, run the bench and commit the new numbers (or explain the
  regression).
* The "skip paint when nothing dirty" path (`UIDocument.NeedsRepaint`)
  saved roughly 3 ms per idle frame on the demo. Don't add code in
  `EmitPaint` that runs unconditionally — branch on the dirty signal.

## 4. Things to NOT do

* Do not add `display: -unity-foo`, `-unity-text-align`, or any
  Unity-prefixed CSS property. We're modeling the web subset, not extending
  it.
* Do not introduce new MonoBehaviour dependencies in headless code. The
  parser, cascade, layout, and paint converter compile and run without
  UnityEngine; keep it that way.
* Do not silently downgrade behavior. If a feature isn't supported, route
  through `UICssDiagnostics` so authors get a console warning. A working
  demo with a quietly-wrong layout is the worst outcome.
* Do not commit golden image regenerations in the same commit as the code
  that changed them. Goldens live in
  `Packages/com.wevaui/Tests/Runtime/Goldens/Baselines/`; regen with
  `WEVA_REGENERATE_GOLDENS=1` and review the pixel diff before committing.
* Do not push to remotes or amend published commits unless explicitly
  asked. The user owns release cadence.
* Do not break `Runtime/Tests/.../UIPaintSourceRegistryTests.cs` and friends
  by adding required interface members. New `IUIPaintSource` members must
  have defaults (or you must update every implementer including test stubs).

## 5. Workflow recipes

### Adding a new CSS property

1. Register in `Css/Cascade/CssProperties.cs` (name, inherits, initial).
2. If shorthand: handler in `Css/Cascade/Shorthands/`.
3. Resolver (`Paint/Conversion/*.cs` or `Layout/*.cs`).
4. Test in `Tests/Runtime/Css/Cascade/` + a layout/paint test.
5. Add to `CONFORMANCE.md`.

### Adding a new HTML form control

1. Tag-name handling in `Layout/BoxBuilder.cs` if needed.
2. Element wrapper in `Forms/<Name>Element.cs` (attribute getters/setters).
3. Controller in `Forms/<Name>Controller.cs` if it has interactive behavior
   — wire pointer/key listeners via `EventDispatcher.AddEventListener`.
4. UA stylesheet rule in `Forms/FormControlStylesheet.cs` for default look.
5. Tests; smoke-test in play mode against the demo.

### Adding a paint command kind

1. Subclass `PaintCommand` in `Paint/PaintCommand.cs`.
2. Add `Submit(NewKind)` to `IRenderBackend`.
3. Implement in `RecordingBackend`, `NullBackend`, `BatchedURPRenderBackend`.
4. If GPU-rendered: extend `UIBatcher` (build the per-instance data),
   `UIRenderGraphPass` (bind atlas / set keywords), `Weva-Quad.shader`
   (new branch behind a `_FOO` keyword).
5. Pool the command type in `CommandPool`.

### Reproducing a layout bug headlessly

```bash
cd Tools/BaselineGen
dotnet run -c Release -- "C:/path/to/snippet.html" "C:/path/to/snippet.css" \
    --viewport 1434x781 --out unity_coords.json
node compare_coords.js chrome_coords.json unity_coords.json
```

Use `RandhtmlLayoutDumpTest.DumpRandhtmlCoords` from the editor to dump the
demo's box tree to JSON without entering play mode. Compare against a Chrome
dump (`Tools/coord_dump.js` injected via DevTools) to find the divergence.

## 6. Testing

* Run `EditMode` tests via the Test Runner or
  `mcp__UnityMCP__run_tests({mode:"EditMode"})`. ~25 should pass; failure is
  a regression.
* `PlayMode` tests cover ~3850 cases. Many goldens have legacy diffs from
  pre-fix behavior — only treat NEW failures as your responsibility.
* Headless tests live in `Tools/BaselineGen` and `Packages/com.wevaui/Tests/`
  — the latter run inside the Test Runner; the former runs without Unity.

## 7. Authoring vs implementing

If you're being asked to **author UI** (write HTML/CSS for a feature), read
`Packages/com.wevaui/Documentation~/AuthoringGuide.md` — that's the user
manual. AGENTS.md is for changing the **engine**. Don't confuse the two
audiences.

## 8. When you're stuck

* `PLAN.md` is the architecture map; if a piece feels missing, it's probably
  spelled out there.
* `CONFORMANCE.md` is the source of truth for spec deltas.
* `Tests/Runtime/<area>/` shows the intended contract for every public
  surface — read the test before guessing the API.

If you change something with broad blast radius (cache key shape,
invalidation propagation, the over-shader's instance layout), explain the
"why" in the commit message body. Surprises in this codebase are usually
expensive.
