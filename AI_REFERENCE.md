# AI_REFERENCE.md — orientation for AI agents

A single document for an AI assistant asked to **use, integrate, or reason
about** Weva: what it is, what it can and cannot do, how to wire it up, how
the pieces fit, and which tools verify it. When a claim here and the code
disagree, the code wins — tell the user.

> Audience split — don't confuse them:
> - **Use the library / author UI** (HTML/CSS, controllers, forms) → this doc for the overview, then [`Packages/com.wevaui/Documentation~/AuthoringGuide.md`](Packages/com.wevaui/Documentation~/AuthoringGuide.md) for the manual and [`getting-started.md`](Packages/com.wevaui/Documentation~/getting-started.md) for the wiring.
> - **Change the engine or a host** → [`AGENTS.md`](AGENTS.md) (the engineering contract) + [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) (the core as built).
> - **Check if something is supported** → [`supported-css.md`](Packages/com.wevaui/Documentation~/supported-css.md) / [`supported-html.md`](Packages/com.wevaui/Documentation~/supported-html.md), and `Tools/oracle/known-gaps/` for what the Chrome gate excuses.


Current readiness and verification: [integration audit](docs/verification/parser-memory-20261101.md).

Latest verified checkpoint: GCC 21 suites / 809,530 main checks; Clang
ASan/UBSan 23 suites / 809,524 checks; 341 Chrome layouts within 1.5 px;
69 Chrome behavior scripts pass plus one known IME exception. Tooling: 90 pass
on Linux, 81 pass plus nine skips on Windows. Unity: 259 EditMode passes plus
two inconclusive, Mono player 7/7. See the audit for scope and build identities.
The [Agentic memory follow-up](docs/verification/agentic-memory-20261101.md)
records five-minute lifecycle controls for its identified SDK.

## 1. What it is

A **runtime HTML/CSS UI engine** — `libweva`, C++ behind a C ABI — hosted by a
Unity package (`com.wevaui`) that renders through **URP RenderGraph**, and by
a Godot addon. It is deliberately **not** Unity UI Toolkit (UXML/USS): the
design target is standard web HTML/CSS. The implemented subset has documented
limits; consult the support tables before promising browser behavior.
Conformance is measured against headless Chrome captures.

- Own HTML + CSS parsers; cascade with `var()`, `calc()`, `@media`,
  `@container`, `@scope`, `@property`, layers, nesting, `:has()`.
- Block/inline flow, flexbox, CSS Grid, positioning, sticky, scroll
  containers and snap, anchor positioning; bidi via ICU.
- Transitions and keyframe animations; forms with text editing, IME, undo;
  focus and gamepad navigation; dialogs, popovers, details, tooltips.
- Paints to textured triangle lists; the host draws them. Text is host-owned:
  Unity answers the font callbacks with `FontEngine`, Godot with TextServer.

## 2. How to use it (Unity)

`WevaDocument` (`Packages/com.wevaui/Runtime/WevaDocument.cs`, menu
**GameObject → Weva → New WevaDocument**) is the component. Assign
`DocumentAsset` (`.html` TextAsset; its `<link rel="stylesheet">` resolves
next to it) and optionally `StylesheetAssets`. `UIBatchedRendererFeature`
must be on the URP renderer asset — apply it yourself:
`Weva.EditorTools.Setup.UrpFeatureSetup.ApplyNonInteractive()` from editor
code, or `Unity -batchmode -quit -projectPath <project> -executeMethod
Weva.EditorTools.Setup.UrpFeatureSetup.ApplyNonInteractive` (idempotent; also
adds the shader to Always Included Shaders).

Controller:

```csharp
public sealed class MainMenu : MonoBehaviour, IBindingVersion {   // IBindingVersion optional
    public int BindingVersion { get; private set; }
    [UIBind] public int CoinCount;                                  // {{ CoinCount }}
    [UIBind] public List<Quest> Quests;                             // data-each="Quests as q"
    void Awake() => GetComponent<WevaDocument>().SetController(this);
    public void OnStart() { CoinCount++; BindingVersion++; }        // on-click="OnStart"
    public void OnRow(string id) { … }                              // or take the element id
}
```

- `{{ path }}` in text and attributes reads `[UIBind]` roots and dotted paths;
  `data-class-<name>="Path"` toggles a class; `data-each`/`data-key`/`$index`
  clone rows; `data-model` binds a control two-way. A `bool` reads
  `true`/`false`. A plain controller is polled once a frame; an
  `IBindingVersion` one only when it bumps; `RequestRefresh()` forces.
- `on-<event>="Name"` calls `Name()` or `Name(string id)` — `click`,
  `input`, `change`, `submit`, `focus`, `blur`, `keydown`, `toggle`, `close`,
  `contextmenu`, … C# events on the component: `ElementClicked`,
  `ValueChanged`, `Changed`, `FormSubmitted`, `Focused`, `DataChanged`, `Event`.
- Input is automatic (`AutoInput`): mouse, keyboard with repeat, touch,
  gamepad (d-pad/stick navigate, South accepts, East cancels, shoulders tab),
  IME. `InputConsumed` says whether the UI took the frame's input;
  `AcceptsKeyboard`, `WrapTab` / `TabbedOut`, `GamepadTextEntry` /
  `TextEntryRequested` are the knobs.
- Programmatic access is `doc.Query(selector)` → `WevaElement` (`Value`,
  `SetAttribute`, `HasClass`, `Focus()`, `Bounds`, `ScrollTo`, `RowIndex`,
  `ShowDialog`, `Parent`/`Children`; stale after `Reload()`, never throws).
  Text changes go through bindings. `Weva.Native` is internal.
- Fonts: `Font`/`Bold`/`Italic`/`Fallbacks` on the component, `@font-face`
  with `url()` (relative to `BasePath`, the asset's folder by default) or
  `local("Installed")`. Images: `url()` relative to `BasePath` through the
  core's asset reader; a player without files sets `doc.AssetReader`.
- Hot reload: saving a referenced `.html`/`.css` reloads the document, in play
  and edit mode; `Reload()` from code. DevTools: **Window → Weva → Elements**.

Details and the exact supported set: [`api-stability.md`](Packages/com.wevaui/Documentation~/api-stability.md).

## 3. What it does NOT do (read before promising a feature)

- **Platforms.** The native plugin ships for Windows x64. macOS, Linux,
  Android and iOS follow as their CI jobs go green (never validated yet).
- **World-space UI.** Screen-space overlay only; a diegetic screen means
  rendering the pass to a RenderTexture yourself.
- **Colour emoji on Unity.** Not rasterised yet; monochrome symbols render
  from the bundled symbol face.
- **Shaping limits on Unity.** The host shapes Arabic and the nine main Indic
  scripts from font bytes, with the core supplying Unicode properties. A
  `Font` asset has no bytes for substitutions. Sinhala, Khmer, Myanmar and
  Tibetan shaping are not implemented.
- **Vertical writing, partly.** `vertical-rl` / `vertical-lr` lay out as
  orthogonal flows and paint runs turned a quarter turn
  (`libweva/src/writing_mode.cpp`); `sideways-*`, upright CJK, a horizontal
  box inside a vertical one, and decorations / selection / caret on a
  vertical run are not done. The Chrome gate excuses no case
  (`Tools/oracle/known-gaps/chrome-sweep.txt`). Multi-column layout is
  implemented and balanced the way Blink balances (`libweva/src/multicol.cpp`).
- **No script.** No JavaScript, no DOM mutation API beyond attributes,
  values and reload; a list changes through `data-each`.
- **Motion.** Opacity, same-unit numeric values and colors interpolate.
  Transform/gradient/filter function values switch discretely; multi-point
  `linear(...)` easing is not implemented. See the animation reference.
- **Not UI Toolkit.** No USS, no `-unity-*` properties, no uGUI interop.

## 4. Architecture map

```
libweva/                          the engine (C++17, C ABI in include/weva_c.h)
  src/   html_parser, css_parser, cascade, layout (block/inline/flex/grid/…),
         paint, forms, text editing, bidi (ICU), animation, bindings, weva_c
  tests/ the core suites, allocation guards and incremental corpus
hosts/godot/                      GDExtension addon: WevaDocument Control, TextServer fonts
hosts/unity/                      plugin build (CMake), gen_bindings.py → WevaNative.g.cs, notices
Packages/com.wevaui/
  Runtime/Native/                 NativeDocument (ABI wrapper), UnityFontBackend (FontEngine),
                                  NativeDocumentRenderer (meshes), NativeInputFeed (Input System),
                                  NativeBindings + UIBindResolver ([UIBind]), NativeInspectorModel
  Runtime/WevaDocument.cs         the component
  Runtime/Rendering/              UIBatchedRendererFeature + UIRenderGraphPass (draws the draw list)
  Editor/                         inspector, Elements window, URP/shader setup, .css importer,
                                  hot-reload watcher, <link> baker for players
  Tests/Editor/Native/            EditMode suite (document, bindings, input, fonts, tooling)
Tools/oracle/                     Chrome captures + chrome_sweep.py gate + behaviour checks
Tools/Layout/                     puppeteer capture tooling
Tools/weva_dump|render|bench      core CLIs (layout dump, software render, benchmarks)
docs/                             ARCHITECTURE, PRODUCT_READINESS, INPUT_PARITY, IME, receipts
check.sh                          the gate
```

## 5. Tooling & verification

- `check.sh --release`: full local core, sanitizer, Chrome, native host, addon
  and scene gate. Unity editor/player suites need the installed Unity editor.
- `Tools/oracle/chrome_sweep.py --chrome-metrics --max-worst 1.5 --known-gaps`:
  tracked Chrome layout captures. `Tools/oracle/run_chrome_checks.py`: live
  behavior checks with the pinned Windows reference.
- Unity Native EditMode: `Unity -batchmode -projectPath <repo> -runTests
  -testPlatform EditMode -testFilter Weva.Tests.EditorTests.Native
  -testResults <path>`; omit `-quit` and `-nographics`. Full EditMode also
  checks compilation and broader package behavior.
- `Tools/run_unity_player_tests.py`: real Windows test players.
  `hosts/unity/goldens_from_unity.py`: fresh Unity/Chrome images for review;
  captures do not themselves prove pixel conformance.
- [Latest validation and open debt](docs/verification/parser-memory-20261101.md);
  [historical checkpoints](docs/verification/agent-progress-history.md).
  Keep one latest gate summary here; archive earlier checkpoints.

## 6. Where to look — quick index

| Question | File |
|---|---|
| The ABI, what each entry point guarantees | `libweva/include/weva_c.h` |
| How the core is put together, ABI minor history | `docs/ARCHITECTURE.md` |
| What the component exposes and promises | `Packages/com.wevaui/Documentation~/api-stability.md` |
| How to author a page and a controller | `Packages/com.wevaui/Documentation~/AuthoringGuide.md` |
| Which CSS works, and how it differs from Chrome | `Packages/com.wevaui/Documentation~/supported-css.md` |
| Input decisions on both hosts, with tests | `docs/INPUT_PARITY.md` |
| Readiness, requirements and limitations for users | `docs/PRODUCT_READINESS.md` |
| User guides, engineering references and historical records | `docs/README.md` |
| Verification counts, configuration and evidence | `docs/verification/parser-memory-20261101.md` |
| What changed for 1.0 and how to migrate | `Packages/com.wevaui/CHANGELOG.md` |
