# AI_REFERENCE.md — orientation for AI agents

The [glyph lookup experiment](docs/verification/glyph-map-20261031.md) was
rejected after six paired native selection runs: the hash-table candidate lost
four pairs. The original source and byte-identical GCC core library are restored;
no host binary changed and no speedup is claimed.


The [shared-core selection profile](docs/verification/core-selection-profile-20261031.md)
attributes fixed-font native selection work: computed-style reads are 9.47%
and glyph-atlas lookup 5.02% of self samples. No optimization or host-frame
claim follows from this diagnostic; its deeper caller table was capped.


The [direct vertex upload pass](docs/verification/unity-direct-vertices-20261031.md)
reduces paired full-inventory Unity sync median 11.9%, with mixed total CPU tails.
Full EditMode passes 255 tests (two existing inconclusive); URP and Windows
Mono player pass 7/7 each, with all 16 captures per host path unchanged.


The [mesh reset experiment](docs/verification/unity-mesh-reset-20261031.md)
was rejected after three alternating pairs: full inventory CPU medians did not
improve. Production renderer source was restored; no new speedup is claimed.


The [full inventory CPU audit](docs/verification/unity-full-inventory-20261031.md)
measures all 20 selection mutations through Unity bindings: 24,576 measured
iterations pass, with full-selection CPU median/p95 of 1.180/1.459 ms.
This is editor update/sync/record time, excludes GPU execution, and claims no speedup.


The [texture lifetime audit](docs/verification/texture-lifetime-20261031.md) adds
replacement regressions in both hosts: Unity renderer tests pass 14 with one
existing environment-gated inconclusive; Godot passes 96 rendered and 48
headless checks. No production behavior or performance claim changes.


The [Unity player timing audit](docs/verification/unity-player-timing-20261031.md)
passes its opt-in Windows Mono/D3D12 diagnostic: 9000 presented frames at
60 Hz, 8997 available GPU timings, all focused. Whole-player metrics are
not isolated UI GPU time; production correctness counts remain unchanged.
The [3D follow-up](docs/verification/unity-player-world-20261031.md) adds
321 renderers and 64 rotating objects, with 9000/9000 GPU readings; the
synthetic scene has no active shadows and is not GPU-saturated.

The [Unity interleaved upload pass](docs/verification/unity-interleaved-upload-20261031.md)
passes 252 EditMode tests (two existing inconclusive), URP 7/7 and Windows
Mono player 7/7. Paired inventory sync median improves 12.1%; tails are mixed.

The [Unity material preparation pass](docs/verification/unity-material-preparation-20261031.md)
adds materials to explicit loading preparation. Full EditMode passes 251
(two existing inconclusive), URP 7/7 and Windows Mono player 7/7. Paired
editor first recording falls from 3.09 to 1.31 ms by moving work into loading.

The [Unity renderer preparation pass](docs/verification/unity-renderer-api-20261031.md)
adds explicit PrepareRenderer in unreleased 2.0: full EditMode 248 passes
(two existing inconclusive), URP 7/7 and Windows Mono player 7/7.
Texture/mesh setup can move into loading; player timing remains unverified.

The [Unity retained preparation pass](docs/verification/unity-hidden-preparation-20261031.md)
adds Visible/Prepare in package 2.0.0 and removes redundant enable-time reloads.
Full EditMode passes 247 tests (Native 246), with two existing inconclusive;
URP PlayMode passes 7/7. Godot hidden preparation passes 39 checks on Windows
and rebuilt qualified Linux. No Unity timing speedup is claimed.

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

The [stretched-item probe follow-up](docs/verification/stretched-probe-20261031.md)
was reverted after the [real-font Unity comparison](docs/verification/stretched-probe-host-20261031.md)
failed to establish an overall CPU benefit. Its generic layout regressions
remain. Both hosts use the restored shared core; the fixed-font 5.53% result
describes the rejected candidate, not current host performance.

The [rendering integration audit](docs/verification/render-audit-integration-20261031.md)
passes the October 31 qualified release gate with no skipped stages:
15 GCC suites, 17 Clang ASan/UBSan suites, 340 Chrome captures, and 66
browser behavior checks with the existing IME exception. Full Unity
EditMode passes 255 with two inconclusive; Windows Mono player rendering
passes 7/7. Godot font, Unicode, renderer, scene and native export gates
pass. See the receipt for accepted gains, rejected experiments and limits.

The [flex-row allocation follow-up](docs/verification/flex-row-allocation-20261031.md)
keeps proven text updates inside the flex item and fixes padding-dependent
shrink weights in the shared core. Four paired Unity runs reduce full-inventory
CPU by 14.9% (median paired), with unchanged renders. The qualified release
gate passes 15 GCC and 17 sanitizer suites (706,127 / 706,121 main checks),
341 Chrome captures, and the existing browser checks. Unity full EditMode
passes 255 with two inconclusive; Windows Mono player passes 7/7; Godot font,
Unicode, rendering, exports and scene checks pass. See the receipt for scope.

The [Godot flex-row timing follow-up](docs/verification/flex-row-godot-20261031.md)
measures four rendered 60 Hz pairs: inventory controller/update median improves
10.49% (median paired), with 40 RGB-identical captures. HUD and frame-tail
results are mixed; this is not a GPU or FPS speedup claim. No production code
changes in the follow-up.

The [post-optimization Chrome comparison](docs/verification/optimized-render-comparison-20261031.md)
uses the verified flex-row candidate in three fresh alternating engine pairs.
Inventory style/layout remains 0.336 ms versus Chrome 0.242 ms; paint and host
CPU stay separately reported. This is not full rendering or GPU parity.

The [paint caller audit](docs/verification/paint-callers-20261031.md) fixes
benchmark stack output that silently stopped at four frames. GCC and Clang
tool builds and smokes pass. The uncapped fixed-font diagnostic attributes
remaining paint work without claiming host latency or a new runtime speedup;
Unity and Godot runtime code are unchanged.

The [paint shaping-buffer experiment](docs/verification/shaping-scratch-20261031.md)
is rejected: 45.2% fewer diagnostic allocations but 4.76% slower median paired
core update, losing five of six pairs. Original source is restored; all 15 GCC
suites pass. Neither host received the experiment.

The [outline-neighbor experiment](docs/verification/outline-neighbors-20261031.md)
is rejected after six fixed-font pairs split three wins/three losses. Geometry
checks stayed identical, but no useful runtime gain was established. Original
source is restored and all 15 GCC suites pass; hosts are unchanged.

The [Agentic pass-scissor follow-up](docs/verification/agentic-pass-scissor-20261031.md)
fixes inherited clipping hiding canvas backgrounds. The leased audit branch
passes 84 library tests with two existing ignored; the GPU regression fails
before and passes after. This is adapter correctness, not a speedup or an
upstream SDK release. Unity/Godot use different clipping paths and are unchanged.

The [Agentic scissor command follow-up](docs/verification/agentic-scissor-dedup-20261031.md)
reduces repeated clip commands while preserving first-draw state ownership.
Encoding CPU improves 5.04% HUD / 4.26% inventory in four paired runs; 84 tests
pass with two existing ignored and captures match. It is on the unpublished
Agentic audit branch; no shared-core or Unity/Godot change is needed.

The [resolution audit](docs/verification/resolution-audit-20261031.md) measures
nine Agentic processes at 720p/1080p/4K: 7,200 CPU updates and 3,240 separate
retained GPU intervals. RTX 5080 inventory GPU median reaches 0.034 ms at 4K.
This is native-pixel viewport evidence, not high-DPI UI, a busy game world,
or lower-end qualification. Runtime source and both supported hosts are unchanged.

The [integrated runtime verification](docs/verification/integrated-runtime-20261031.md) checks the current core and Agentic renderer in 9,000 CPU-profiled game frames; selection/use/menu actions pass. It is not a paired speedup claim.

The [ordinary-draw bounds experiment](docs/verification/backdrop-bounds-20261031.md) was rejected after a fresh-baseline repeat failed to establish a runtime gain; accepted renderer source and binaries were restored.

The [final rendering audit](docs/verification/render-audit-final-20261031.md) reconciles accepted changes against current source, installed host binaries and original test evidence, with remaining product qualifications explicit.

The [IL2CPP qualification](docs/verification/il2cpp-qualification-20261031.md) fixes callback P/Invoke marshalling and verifies Windows development/high-stripping non-development players. It also records orphan audit players and the need to remeasure prior performance results without that background load.

The [Agentic integration](docs/verification/agentic-integration-20261031.md) merges the audit into local Agentic main, passes eleven integration targets and three editor queue tests, and validates an immutable merged SDK with runtime actions and reloads. No push or default native SDK pin change.

The [October 31 lifecycle soak](docs/verification/lifecycle-soak-20261031.md) completes 111,587 reloads over 30 minutes with post-reload interaction passing. Dedicated GPU allocation settles; private CPU memory rises 13.7 MiB and remains an open qualification issue. Both Unity runners now stop owned players to prevent background rendering from contaminating measurements.

The [clean October 31 Chrome comparison](docs/verification/clean-render-comparison-20261031.md) repeats three alternating rounds with no audit renderers/builds: inventory style/layout is 0.280 ms versus Chrome 0.142 ms, paint recording 0.255 versus 0.201 ms. Sampled geometry stays within 0.754 px. A subsequent flex replay overlapped another workspace runtime and is explicitly excluded from accepted timing evidence.

The [inventory update pass](docs/verification/inventory-update-20261031.md) adds fixed-boundary text reuse, selector-local text/class updates and equal-dimension image swaps. GCC passes 707,079 checks, sanitizers 707,073, 341 Chrome captures and 66 behavior scripts pass; Unity EditMode 255 plus two inconclusive and player 7/7 pass. The full gate first failed only the cleanup-test psutil import; the repaired tooling suite passes 72 tests. Timings remain provisional and above Chrome.

The final [inventory update gate](docs/verification/inventory-update-20261031.md) rerun passes the complete qualified release gate (exit 0), including the repaired tooling stage. The further ancestor-batching experiment was rejected after no style/layout gain; accepted source remains e20544d0. Chrome parity is still not reached, and timing results remain provisional.

The [inventory box-copy follow-up](docs/verification/inventory-box-copies-20261031.md) removes redundant construction/probe copies. The full qualified release gate passes: GCC 707,090 checks, sanitizers 707,084; Unity full EditMode 255 plus two inconclusive and Mono player 7/7 pass, with all sixteen captures unchanged. Latest provisional inventory style/layout is 0.194 ms versus Chrome 0.159 ms; parity is not reached. Borrowed shaping was rejected after its repeat failed to confirm a gain.

The [local intrinsic-memo follow-up](docs/verification/inventory-intrinsic-memo-20261031.md) avoids document-sized memo allocation for local indexing. The full qualified release gate passes: 16 GCC suites, 18 sanitizer suites, with ten new allocation checks (one 1,264-byte allocation for local queries in both 32- and 4,096-box trees). Main core counts remain 707,090/707,084; Unity full EditMode 255 plus two inconclusive and Mono player 7/7 pass, with sixteen unchanged captures. Paired inventory style/layout improves 10.1% provisionally; Chrome parity is not reached.

The [ASCII grapheme follow-up](docs/verification/inventory-graphemes-20261031.md) avoids Unicode table searches for ASCII text. The full qualified release gate passes: 16 GCC suites (807,102 main checks), 18 sanitizer suites (807,096), 341 Chrome captures and 66 behavior scripts. Unity full EditMode passes 255 plus two inconclusive, Mono player 7/7; Godot font safety passes 19,833 checks and all renders are unchanged. Direct ASCII grapheme iteration measures about 87% less CPU time provisionally; this is not an inventory frame-time or Chrome-parity claim.

The [bidi mapping follow-up](docs/verification/inventory-bidi-20261031.md) removes mapping allocations from ordinary LTR layout. Paired inventory style/layout improves 7.7% provisionally (0.182 to 0.168 ms; Chrome 0.139 ms), with complete native CPU down 3.7%. Full qualified gate passes: 17 GCC and 19 sanitizer suites, including 792 new bidi checks with zero LTR mapping allocations. Main counts remain 807,102/807,096; Unity EditMode 255 plus two inconclusive, player 7/7, Godot and Chrome gates pass with unchanged renders. Chrome parity remains open.

The [matched property-ID follow-up](docs/verification/inventory-property-ids-20261031.md)
reuses stable IDs during cascade application. Its three paired inventory rounds
show a provisional 4.3% style/layout reduction and effectively flat complete CPU;
Chrome parity remains unproven. GCC main coverage is 807,129 checks (807,123
under sanitizers), with 17 GCC and 19 sanitizer suites. The report also preserves
a rejected line-height experiment and an existing pseudo color-inherit defect.

The [pseudo keyword follow-up](docs/verification/pseudo-keywords-20261031.md)
shares standard-property CSS-wide keyword resolution between elements and
pseudos and corrects inherited rollback defaults against Chrome. The full
qualified gate passes: 807,189 GCC main checks (807,183 sanitized), 17 GCC
and 19 sanitizer suites, 341 layout captures, and 67 live Chrome checks plus
the existing known failure. Unity remains 255 passes/two inconclusive and
7/7 Mono player checks. A focused core render matches Chrome pixels. The
report records provisional regression timings, local layout counters and
a separate unresolved background-color currentColor paint defect.

The [currentColor and layout investigation](docs/verification/currentcolor-and-layout-probes-20261031.md)
fixes shared currentColor painting and verifies live ordinary/pseudo background
updates. The width-reuse experiment is reverted: six provisional pairs do not
establish a runtime gain despite fewer line builds. The final color-only full
gate passes 807,198 GCC main checks (807,192 sanitized), 17 GCC/19 sanitizer
suites, 341 Chrome captures and 67 browser checks plus the existing known
failure. Unity remains 255 passes/two inconclusive and 7/7 Mono player passes.

The [combined vertical-metrics pass](docs/verification/vertical-metrics-20261031.md)
removes duplicate cached face lookups during inline layout. Six provisional
pairs measure 12.5% lower inventory style/layout CPU; this is not universal
Chrome parity. The full gate passes 807,588 GCC main checks (807,582 sanitized),
17 GCC/19 sanitizer suites, 341 Chrome captures and 67 browser checks plus
the existing known failure. Both Windows hosts are rebuilt; Unity passes
255 EditMode tests/two inconclusive and 7/7 Mono player tests. All 17 render
comparisons match the accepted baseline.

The [inventory paint investigation](docs/verification/paint-investigation-20261031.md)
retains no runtime change. The no-shadow color experiment was reverted after
six provisional pairs failed to isolate a useful gain; unrelated Rust builds
contaminated the first batch. Corrected paint attribution, both batches and
the rejected patch are preserved. The restored engine passes all 17 GCC
suites and the rebuilt Agentic PE sections match the accepted baseline.
