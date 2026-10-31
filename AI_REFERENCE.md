# AI_REFERENCE.md — orientation for AI agents

The [scripted hidden preparation pass](docs/verification/script-preparation-20261031.md)
adds native Agentic html_prepare on the local audit branch. Three paired runs
reduce opening main CPU from 20.42 to 0.55 ms by moving about 20 ms to loading;
GPU first-use work remains. API/accounting/editor checks and live clicks pass.
Shared core and Godot/Unity code are unchanged.

The [1080p/1440p runtime validation](docs/verification/resolution-runtime-20261031.md)
passes 36,000 measured frames and 1,332 reloads. Combined Agentic submission
and uniform changes reduce cycling-inventory worker CPU by 11.6-12.6%; frame
medians improve 1.8-3.6%, with mixed static HUD tails. Core/other hosts unchanged.

The [Agentic viewport-uniform follow-up](docs/verification/viewport-uniform-20261031.md)
skips redundant changed-frame buffer writes. Fourteen renderer tests, 18,000
measured runtime frames and 656 reloads pass. Observed worker savings are small
(about 1.5�1.8%); static-mode timings remain mixed. Shared core is unchanged.

The [Agentic submission follow-up](docs/verification/ui-submission-20261031.md)
combines presentation and HTML encoding: inventory worker UI-phase CPU falls
17.1% and frame median 3.3% in three pairs. 18,000 frames, 713 reloads and nine
worker tests pass; static-mode frame tails remain mixed. No shared-core change.

The [runtime accounting correction](docs/verification/runtime-accounting-20261031.md)
separates worker UI from main-thread cost. Inventory main UI is 0.533 ms mean,
including 0.511 ms document work; 9,000 frames and 355 reloads pass. Older
threaded `ui` values combine main/worker work; this fix claims no new speedup.

The [current Agentic SDK runtime check](docs/verification/current-runtime-20261031.md)
passes 18,000 measured frames, click/actions and 716 reloads in a 240-cube world.
Inventory frame median improves 10.7%, but p95 is mixed; this is not GPU or
Chrome parity evidence. Shared Godot/Unity checks remain in the no-op receipt.

The [unchanged-attribute follow-up](docs/verification/scoped-noop-20261031.md)
avoids no-op restyles after sibling pruning: paired complete inventory CPU
falls 7.3%. Windows/GCC main checks pass 493,402 (493,396 sanitized), all 340
Chrome captures pass, Unity Native passes 243 with two inconclusive, Agentic
renderer passes 13, and affected Godot scenes pass. No ABI change or new full gate.

The [sibling-subject optimization](docs/verification/sibling-subjects-20261031.md)
reduces unnecessary sibling restyles in the shared core. Windows/GCC main tests
pass 493,041 checks (493,035 sanitized), all 340 Chrome captures pass, Unity
Native passes 243 with two inconclusive, and affected Godot scenes pass.
Inventory core CPU improves 6.9%; complete inventory CPU did not improve in
the recorded batch. This is affected-suite evidence, not a new full gate.

The [matched rendering comparison](docs/verification/prepared-render-comparison-20261031.md)
corrects diagnostic backgrounds/bindings and records three prepared Chrome/Agentic
runs. Inventory style/layout remains behind Chrome; no production speedup or
GPU parity is claimed. Shared-host gate counts are unchanged.

For repeatable Weva/Chrome geometry-workload performance baselines, use
[`Tools/performance/README.md`](Tools/performance/README.md). The
[October 30 optimization receipt](docs/verification/chrome-close-20261030.md)
records the matched comparison, dependency-driven cascade/layout improvements,
ten runner regressions and post-mutation geometry validation. Both engines
exclude painting; these timings do not measure rendered frame cost.
The [grid follow-up](docs/verification/chrome-deeper-20261030.md) adds populated
fixed/fractional inventory fixtures, for fourteen workloads, and separate paired
native measurements including paint preparation. Neither measures game FPS.
The [repeated-work follow-up](docs/verification/chrome-target-20261030.md) keeps
those fourteen inputs unchanged: thirteen beat Chrome; autosizing-flex leaf
updates remain 0.000971 ms slower. Paired native layout improves 5.1-53.3%.
Cold fixture results are mixed; see the receipt rather than assuming every
operation improves. The latest gate covers 338 Chrome captures, 704,365 GCC
core checks and 704,359 ASan/UBSan checks, plus both hosts and live Godot scenes.

The [rendering measurement](docs/verification/rendering-20261030.md) adds a
real survival-UI probe with background-only control, CPU/GPU timestamps,
timestamp-disabled frame timing and separate draw-submission diagnostics.
It covers Godot Compatibility/OpenGL and Mobile/Vulkan on an RTX 5080 at
1280x720; it is not a matched Chrome rendering comparison or a busy 3D scene.
Run it using [these instructions](Tools/performance/README.md#rendered-godot-frames).

The [same-scene Chrome rendering comparison](docs/verification/rendering-chrome-20261030.md)
adds actual survival-scene traces at 60 scheduled updates/second. CPU stage
boundaries remain explicit; Chrome hardware GPU timing is unavailable. Three
trace-accounting tests pass; engine gate counts are unchanged. Inventory
selection remains the main measured CPU gap.

A single document for an AI assistant asked to **use, integrate, or reason
about** Weva: what it is, what it can and cannot do, how to wire it up, how
the pieces fit, and which tools verify it. When a claim here and the code
disagree, the code wins — tell the user.

> Audience split — don't confuse them:
> - **Use the library / author UI** (HTML/CSS, controllers, forms) → this doc for the overview, then [`Packages/com.wevaui/Documentation~/AuthoringGuide.md`](Packages/com.wevaui/Documentation~/AuthoringGuide.md) for the manual and [`getting-started.md`](Packages/com.wevaui/Documentation~/getting-started.md) for the wiring.
> - **Change the engine or a host** → [`AGENTS.md`](AGENTS.md) (the engineering contract) + [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) (the core as built).
> - **Check if something is supported** → [`supported-css.md`](Packages/com.wevaui/Documentation~/supported-css.md) / [`supported-html.md`](Packages/com.wevaui/Documentation~/supported-html.md), and `Tools/oracle/known-gaps/` for what the Chrome gate excuses.

_Unity verified: 2026-10-30 (1.0.0 plus unreleased fixes); Native EditMode
243 pass / 2 environment-gated inconclusive, full EditMode 244 pass / 2
inconclusive. See the [repeated-work follow-up](docs/verification/chrome-target-20261030.md)
for the current build and the [technical audit](docs/verification/tech-audit-20261011.md)
for remaining adoption limits._

The [October runtime performance audit](docs/verification/performance-audit-20261011.md)
records 27 passing native workload runs, selected cold-creation profiles and
prioritized investigations. It does not establish Unity or whole-game timing.
The subsequent [cold blur change](docs/verification/cold-blur-20261011.md)
preserves exact blur pixels and records a modest paired core-only improvement.
The [parallel raster follow-up](docs/verification/cold-raster-20261011.md)
reduces paired GCC cold HUD creation by 35.5% with identical pixels. It adds 24
serial/parallel raster comparisons to the 192 blur checks, uses additional CPU
cores, and does not make the public core safe for concurrent callers.
The [runtime geometry follow-up](docs/verification/runtime-geometry-20261011.md)
reduces paired Godot inventory scrolling API CPU p95 by 11.9% with identical
pixels; ordinary active/bound HUD timing is effectively unchanged in the final
repeat. The geometry allocation suite now passes 176 checks.

---

The [scaled-overflow fix](docs/verification/scaled-overflow-20261025.md)
preserves visible content under transformed clips; its focused validation
covers both compiler suites, the Chrome hand corpus and Convoy rendering.

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
  suites: 54/9/32 checks; build, core tests, sanitizers, Chrome
  oracle, behaviour checks, plugin, addon, host tests). CI covers tooling,
  core/sanitizers, Chrome and native builds/package checks; local engine
  installations are still required for Unity suites and live Godot gates.
  The [September 15 review audit](docs/verification/review-three-days-20260915.md)
  records a complete passing run with the qualified patched Linux Godot editor
  and templates, pinned Windows Chrome through WSL, and a private Xvfb display.
  Stock Godot and Linux Chrome retain their documented runtime/reference failures.
- **`Tools/oracle/chrome_sweep.py --chrome-metrics --max-worst 1.5
  --known-gaps`** — every tracked case's layout against its Chrome capture.
  All 334 captures pass the 1.5px ceiling (62 hand, 225 harvest, 47 samples).
  Always pass `--chrome-metrics` (the browser's box semantics).
- **`Tools/oracle/run_chrome_checks.py`** — scripted behaviour checks
  against a live Chrome (forms, focus, popovers, animation clocks, stylesheet
  discovery/imports/font sources/colors/scroll containers/scopes). Review:
  67 scripts, 66 pass and 1 documented Chrome IME failure, including the
  41-case scope and 46-case nesting oracles included in the final full gate.
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
  change, text present, packaged image/font loading and binding updates.
  `Tools/run_unity_player_tests.py` also runs all six checks in a Windows x64
  standalone test player; see [player verification](docs/verification/unity-player-20261011.md).
  **`NativeGameViewCaptureTests`** (PlayMode,
  `[Explicit]`) renders a page to `.utmp/native-gameview/*.png` for a person
  to look at.

Expected numbers live in `docs/verification/review-three-days-20260915.md`;
any red is new.

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
| Verification counts, configuration and evidence | `docs/verification/review-three-days-20260915.md` |
| What changed for 1.0 and how to migrate | `Packages/com.wevaui/CHANGELOG.md` |
# Scaled text validation

Uniform scaled text and controls rasterize at their final pixel size; see
[the scoped core/Agentic receipt](docs/verification/scaled-text-20261025.md).
Open select rows honor authored option/optgroup background colors; see
[the dropdown receipt](docs/verification/option-backgrounds-20261025.md).
Ordinary images/backgrounds filter downscaled pixels in premultiplied sRGB;
thin uniform rounded borders retain antialiasing. See
[the scoped rendering receipt](docs/verification/image-filtering-20261025.md).
Relational selector dependencies now bound unrelated hover/active style work;
template source bodies remain outside the cascade. See
[the work-count and correctness receipt](docs/verification/selector-invalidation-20261025.md).

The [attribute batching audit](docs/verification/render-batching-20261030.md)
removes intermediate Godot paints, adds ten timing checks (31 total), and
records preliminary before/after evidence. The continuing rendering audit
uses Agentic at the user’s request; Godot results do not qualify that host.

The [Agentic runtime audit](docs/verification/agentic-runtime-20261030.md) adds
9,000 CPU-only rendered frames, live click/binding checks and 339 short-cycle
reloads in a published SDK using the current core. It is not a Chrome rendering
comparison or a production-game performance result.

The [Agentic cold renderer audit](docs/verification/agentic-cold-render-20261030.md)
removes unused GPU setup, verifies 13 renderer/42 overlay tests, and measures
both runtime routes. Threaded median opening improves 6.6%; slow cold outliers
remain. Unity/Godot already allocate backdrop resources on demand.

The [hidden UI follow-up](docs/verification/agentic-hidden-ui-20261030.md) adds
correct direct HTML CPU attribution and eliminates hidden layout/upload work
in Agentic direct rendering, matching its snapshot path. 43 overlay tests and
6,000 paired frames pass; visible-workload and other-host gains are not implied.

The [glyph preparation audit](docs/verification/glyph-preparation-20261030.md)
attributes most cold glyph preparation to scaler/raster work and verifies
same-content retained reopening at 0.530 ms median main CPU versus 19.563 ms
cold. This is reuse evidence, not a first-time raster speedup or edit-time bake.

The [Godot hidden UI follow-up](docs/verification/godot-hidden-ui-20261030.md)
pauses automatic hidden document work, including hidden ancestors. Explicit
`update_document(0)` still prewarms while hidden; timing passes 39 checks.
Unity disable releases its document, so it is a different lifecycle.

The [Agentic packed-paint audit](docs/verification/agentic-packed-20261030.md)
removes duplicate snapshot/upload copies in Agentic (ff1c9925b, not integrated
into engine-main yet). Two paint and twelve renderer tests pass. It records
scoped CPU gains and rejected experiments; core/Unity/Godot gate counts are
unchanged. GPU/Chrome parity and the wider rendering audit remain open.

The [Agentic GPU/selection audit](docs/verification/agentic-gpu-20261030.md)
adds real Vulkan timestamps, complete setter/update/submission timing and
720p/1080p/4K scripted workloads. Agentic commit be88b3cb2 is not integrated
yet. Retained GPU costs are small on the RTX 5080; selection preparation is
the main remaining CPU target. It does not establish Chrome GPU parity.

The [attribute no-op experiment](docs/verification/attribute-noop-20261030.md)
was rejected after two paired timing batches failed to show a repeatable
overall gain. Its shared-core changes were reverted and all three hosts
rebuilt. Production gate counts are unchanged; raw evidence is retained.

The [mixed paint/layout reuse pass](docs/verification/paint-layout-20261030.md)
keeps paint-only changes from invalidating retained layout. The targeted
48-cell mixed update is 78.6% faster in complete CPU timing; no survival
inventory gain is claimed. Main core checks are 492,136 (492,130 sanitized);
Windows/GCC/Clang, 338 Chrome captures and affected host suites pass.

The [deferred flex measurement pass](docs/verification/flex-defer-20261030.md)
reduces survival inventory selection complete CPU by 32.2% at 720p and 31.2%
at 1080p. Main checks are 492,686 (492,680 sanitized); 339 tracked Chrome
captures and affected host suites pass. A separate pre-existing nested-flex
baseline discrepancy remains documented in the receipt and product readiness.

The [nested first-baseline correction](docs/verification/nested-baseline-20261030.md)
resolves the retained 47px discrepancy. Main checks are 492,906 (492,900
sanitized); 340 Chrome captures and affected host suites pass. Two paired
batches show a 1.0–3.2% inventory CPU cost for this correctness fix.
