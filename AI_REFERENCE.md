# AI_REFERENCE.md — orientation for AI agents

A single document for an AI assistant asked to **use, integrate, or reason
about** Weva: what it is, what it can and cannot do, how to wire it up, how
the pieces fit, and which tools verify it. When a claim here and the code
disagree, the code wins — tell the user.

> Audience split — don't confuse them:
> - **Use the library / author UI** (HTML/CSS, controllers, forms) → this doc for the overview, then [`Packages/com.wevaui/Documentation~/AuthoringGuide.md`](Packages/com.wevaui/Documentation~/AuthoringGuide.md) for the manual and [`getting-started.md`](Packages/com.wevaui/Documentation~/getting-started.md) for the wiring.
> - **Change the engine or a host** → [`AGENTS.md`](AGENTS.md) (the engineering contract) + [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) (the core as built).
> - **Check if something is supported** → [`supported-css.md`](Packages/com.wevaui/Documentation~/supported-css.md) / [`supported-html.md`](Packages/com.wevaui/Documentation~/supported-html.md), and `Tools/oracle/known-gaps/` for what the Chrome gate excuses.

_Last verified: 2026-09-15 (1.0.0); Native EditMode 236 pass / 2 environment-gated
inconclusive, full EditMode 237 pass / 2 inconclusive._

---

## 1. What it is

A **runtime HTML/CSS UI engine** — `libweva`, C++ behind a C ABI — hosted by a
Unity package (`com.wevaui`) that renders through **URP RenderGraph**, and by
a Godot addon. It is deliberately **not** Unity UI Toolkit (UXML/USS): the
design rule is "real-web HTML/CSS or nothing", so LLM-trained and
browser-trained UI knowledge transfers unchanged. A subtly divergent behaviour
is considered worse than a missing one, and conformance is measured against
headless Chrome captures, not against another implementation.

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
- **Not UI Toolkit.** No USS, no `-unity-*` properties, no uGUI interop.

## 4. Architecture map

```
libweva/                          the engine (C++17, C ABI in include/weva_c.h)
  src/   html_parser, css_parser, cascade, layout (block/inline/flex/grid/…),
         paint, forms, text editing, bidi (ICU), animation, bindings, weva_c
  tests/ the core suite (~500k checks), corpus
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

- **`check.sh`** — the whole gate (release-oracle/text-safety/performance tooling
  suites: 50/9/32 checks; build, core tests, sanitizers, Chrome
  oracle, behaviour checks, plugin, addon, host tests). CI covers tooling,
  core/sanitizers, Chrome and native builds/package checks; local engine
  installations are still required for Unity suites and live Godot gates.
  The [September 15 review audit](docs/verification/review-three-days-20260915.md)
  records the three unresolved full-gate failures alongside the passing suites.
- **`Tools/oracle/chrome_sweep.py --chrome-metrics --max-worst 1.5
  --known-gaps`** — every tracked case's layout against its Chrome capture.
  All 334 captures pass the 1.5px ceiling (62 hand, 225 harvest, 47 samples).
  Always pass `--chrome-metrics` (the browser's box semantics).
- **`Tools/oracle/run_chrome_checks.py`** — scripted behaviour checks
  against a live Chrome (forms, focus, popovers, animation clocks, stylesheet
  discovery/imports/font sources/colors/scroll containers/scopes). Review:
  67 scripts, 66 pass and 1 documented Chrome IME failure, including the
  41-case scope and 46-case nesting oracles added after the full gate.
  CI pins Windows Chrome 152; numeric-editing and CR fixtures depend on the
  reference OS/browser version. Set `WEVA_CHROME` to choose the executable.
- **Unity EditMode**: `Unity -batchmode -projectPath <repo> -runTests
  -testPlatform EditMode -testFilter Weva.Tests.EditorTests.Native
  -testResults <path>` (no `-quit`, no `-nographics`). Also the compile check.
- **`hosts/unity/goldens_from_unity.py`** — fresh Unity/Chrome PNG pairs of the
  current sample pages for visual review (`--only <name>` selects a case).
  It preserves tracked captures; image generation is not a pixel-conformance pass.
- **`NativeRenderPassTests`** (PlayMode, automated) — renders documents
  through the real URP pass into a RenderTexture and asserts the pixels:
  drawn, stacked by `SortingOrder`, gone when disabled, repainted on a
  change, text present. **`NativeGameViewCaptureTests`** (PlayMode,
  `[Explicit]`) renders a page to `.utmp/native-gameview/*.png` for a person
  to look at.

Expected numbers live in `docs/PRODUCT_READINESS.md`; any red is new.

## 6. Where to look — quick index

| Question | File |
|---|---|
| The ABI, what each entry point guarantees | `libweva/include/weva_c.h` |
| How the core is put together, ABI minor history | `docs/ARCHITECTURE.md` |
| What the component exposes and promises | `Packages/com.wevaui/Documentation~/api-stability.md` |
| How to author a page and a controller | `Packages/com.wevaui/Documentation~/AuthoringGuide.md` |
| Which CSS works, and how it differs from Chrome | `Packages/com.wevaui/Documentation~/supported-css.md` |
| Input decisions on both hosts, with tests | `docs/INPUT_PARITY.md` |
| Where each host stands, and the numbers | `docs/PRODUCT_READINESS.md` |
| What changed for 1.0 and how to migrate | `Packages/com.wevaui/CHANGELOG.md` |
