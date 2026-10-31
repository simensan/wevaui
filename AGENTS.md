# AGENTS.md — engineering contract for AI tools

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

The [October 30 Agentic runtime receipt](docs/verification/agentic-runtime-20261030.md)
adds 9,000 CPU-only rendered frames, input/binding checks and 339 reloads in an
immutable SDK built with the current core. No engine code changes in this chunk;
shared-host gate counts remain those in the nested-baseline receipt.

The [Agentic cold renderer receipt](docs/verification/agentic-cold-render-20261030.md)
adds lazy backdrop/local-renderer setup in Agentic, 13 renderer and 42 overlay
tests, and paired cold opening in both runtime routes. The core and Unity/Godot
are unchanged; cold-opening tails remain unresolved.

The [hidden UI follow-up](docs/verification/agentic-hidden-ui-20261030.md) fixes
Agentic direct UI attribution and defers hidden document work. Its 43 overlay
tests and 6,000 paired runtime frames pass. The shared core and other hosts are
unchanged; their automatic hidden-update policies need a separate audit.

The [glyph preparation audit](docs/verification/glyph-preparation-20261030.md)
adds bounded diagnostic font timing and verifies retained inventory reopening
in three runtime processes. Eight font tests pass (one existing ignored).
No shared engine or host behavior changes; cold glyph work remains open.

The [Godot hidden UI follow-up](docs/verification/godot-hidden-ui-20261030.md)
defers automatic hidden ticks while preserving explicit prewarming. Timing now
passes 39 checks; bindings, transitions and timed-input scenes pass, with a real
Vulkan hide/show capture. Core and Unity are unchanged; this is not a full gate.

Read this before changing anything. It is for **changing the engine or a
host**; if you are being asked to *author UI* (write HTML/CSS and a
controller), the manual is
`Packages/com.wevaui/Documentation~/AuthoringGuide.md` and the orientation is
`AI_REFERENCE.md`.

## 1. What this repository is

One engine, two hosts.

* **`libweva/`** — the engine. C++17, no RTTI dependence, behind a C ABI
  (`libweva/include/weva_c.h`, `WEVA_ABI_VERSION_MINOR` in it). It parses
  HTML and CSS, cascades, lays out (block, inline, flex, grid, positioned,
  sticky, scroll), runs forms, text editing, focus, animation and bindings,
  and paints to textured triangle lists. Text is **host-owned**: the core asks
  the host for faces, metrics, glyph coverage and kerning through a callback
  table and never links a shaper. `docs/ARCHITECTURE.md` is the core as
  built; `docs/CONVENTIONS.md` the C++ conventions.
* **`hosts/godot/`** — the Godot GDExtension addon (`WevaDocument` Control,
  TextServer as the font backend, scene tests under `project/`).
* **`Packages/com.wevaui/`** — the Unity package: the same core as a native
  plugin (`Runtime/Native/Plugins`, `WevaNative.g.cs` generated from the
  header by `hosts/unity/gen_bindings.py`), `Runtime/Native/*` (the
  `NativeDocument` wrapper, `UnityFontBackend` over `FontEngine`,
  `NativeDocumentRenderer`, `NativeInputFeed`, `NativeBindings`,
  `UIBindResolver`), `Runtime/WevaDocument.cs` (the component),
  `Runtime/Rendering` (the URP feature and the pass that draws the draw
  list), `Editor/` (inspector, Elements window, setup, importers, hot reload).

The C# engine that once lived in the package was frozen and deleted on
2026-09-13. **There is no C# layout, cascade, paint or text code to change.**

## 2. The rules

* **The core is the single source of truth.** A CSS or HTML behaviour lives
  in `libweva/` with a test in `libweva/tests`; it reaches Unity and Godot
  through the C ABI. Never re-implement engine behaviour in a host.
* **Chrome is the only reference.** A cross-check is against a headless Chrome
  capture (`Tools/oracle`), never against another implementation. When
  changing a wrong default breaks a test, recalibrate the test against Chrome;
  do not paper over the divergence.
* **Hosts are thin translators.** A host turns its engine's input, fonts and
  draw calls into ABI calls. A decision both hosts must make the same way
  (wheel notch, key repeat, double-click, chord modifier) goes into the core
  behind an ABI call so it is tested once; `docs/INPUT_PARITY.md` tables the
  rows and the test that pins each on each host. Keep it current.
* **ABI changes bump the minor**, are documented in `docs/ARCHITECTURE.md`,
  regenerate `WevaNative.g.cs`, and rebuild both hosts. Every entry point
  tolerates a null document and a stale handle (the preamble of `weva_c.h`
  says how).
* **Do not add `-unity-*` or any non-web CSS.** The web subset, not an
  extension of it. Anything outside it fails loudly.
* **Do not touch `Assets/*.unity` scenes** or the user's live scene from
  tooling without being asked.
* **Never push. Commit each finished chunk. Never leave a gate red.** Preserve
  failed evidence (logs, PNGs) rather than deleting it.
* **Verify before claiming.** A code change is followed by the build and the
  affected suite; a visual change by a real render you looked at.

## 3. The gate

`check.sh` (run from WSL or Linux; `WEVA_NO_CHROME=1` skips the browser
steps) is the full local gate: build, core tests and the
incremental corpus, sanitizers, the Chrome oracle
(`Tools/oracle/chrome_sweep.py --chrome-metrics --max-worst 1.5 --known-gaps`
over every tracked capture — `known-gaps/` is the only allowed excuse), the
Chrome behaviour checks (`Tools/oracle/run_chrome_checks.py`), cached ids,
assets, the Unity plugin, the Godot extension and its font/Unicode safety,
the addon install, the backend gate and the host scene tests.

CI runs tooling, core/sanitizer tests, the tracked Chrome layout oracle,
live Chrome behavior checks on pinned Windows Chrome 152, native
host/platform builds and package checks.
The Unity editor suites and live Godot scene, rendering, Unicode and export
checks still require the local gate and its engine installations.

Per chunk, the local recipe is:

* Core: gcc and clang builds at 0 warnings, `ctest`, the core suite
  (`~/weva/build-gcc`, `~/weva/build-clang` in WSL).
* Unity: build the plugin (`hosts/unity`), then the Native EditMode suite —
  `Unity -batchmode -projectPath <repo> -runTests -testPlatform EditMode
  -testFilter Weva.Tests.EditorTests.Native -testResults <path>` (no `-quit`,
  no `-nographics`; exit code 2 with 0 failures is normal). It compiles the
  whole project, so it is also the compile check. The full EditMode suite
  before a commit that touches the package broadly.
* Godot: the affected scenes in `hosts/godot/project` through the runner
  `check.sh` uses.

Numbers to expect are in `docs/verification/review-three-days-20260915.md`;
any red is new.
The 2026-09-15 review passes 14 gcc suites (507,328 core checks),
16 sanitizer suites, and all 334 Chrome layout captures at the 1.5px ceiling.
The 2026-09-25 [technical audit](docs/verification/tech-audit-20260925.md)
and its follow-up add lifetime, hostile-input, nested-layout and threaded-raster
regressions, and the [screen-opening pass](docs/verification/screen-open-20260925.md)
adds raster-job, coarse-blur and shared-texture tests: 507,885 core checks on
gcc, clang and ThreadSanitizer, 507,879 under the 16 sanitizer suites (the
small-stack test skips there).
Its Unity changes still need the editor suites; see the report.
Unity Native EditMode passes 236 tests with 2 environment-gated inconclusive;
the full EditMode suite passes 237 with the same 2 inconclusive.
Tooling suites pass 54 release/oracle checks, 9 text-safety runner checks, and
32 Frontier performance-runner checks; `check.sh` runs all three.
The complete local gate passes with the qualified patched Linux Godot editor
and both templates, pinned Windows Chrome 152 run through the WSL bridge, and
a private Xvfb display. Stock Godot and Linux Chrome retain the documented
Unicode/reference failures; use the qualified configuration in the
[review audit](docs/verification/review-three-days-20260915.md), which records
coverage, exact runtime hashes and the passing full-gate evidence.

The [October 11 technical audit](docs/verification/tech-audit-20261011.md)
adds callback/renderer ownership regressions: Unity Native EditMode now passes
242 tests with the same 2 environment-gated inconclusive; full EditMode passes
243 with 2 inconclusive; PlayMode rendering remains 5/5. Release/oracle tooling
now passes 55 tests on Linux (46 pass and 9 Linux-only skips on Windows).
Use the audit receipt for the newer full-gate result and browser-driver version.
The [Unity player follow-up](docs/verification/unity-player-20261011.md) passes
6/6 rendering/asset/binding checks in a Windows x64 Mono development player.
Repeat it with `Tools/run_unity_player_tests.py`; IL2CPP/release stripping are
outside that result.
The [October performance audit](docs/verification/performance-audit-20261011.md)
repeats 27 native workload runs and profiles selected cold-core creation.
It changes no engine behavior and makes no optimization claim.
The [cold blur follow-up](docs/verification/cold-blur-20261011.md) interleaves
independent SSE2 blur rows with exact-byte portable-kernel coverage; its paired
HUD cold-core result is a modest 2.5% reduction, not a host frame-time claim.
The [parallel raster follow-up](docs/verification/cold-raster-20261011.md)
reduces paired GCC cold HUD creation by 35.5% using additional CPU cores.
Its 24 raster equivalence checks supplement the 192 blur checks. Public engine
calls remain single-caller; only prepared pixel rows execute on workers.
The [runtime geometry follow-up](docs/verification/runtime-geometry-20261011.md)
reduces paired Godot inventory scrolling API CPU p95 by 11.9%, without adding
workers. Its geometry suite now has 176 checks, including 36 scalar-reference
AA comparisons and the thin-border allocation budget.

The [October 20 merge integration](docs/verification/merge-20261020.md)
combines the raster scheduler/cache work with the October optimizations.
Full Unity EditMode passes 244 tests with two environment-gated inconclusive;
its Native subset passes 243 with the same two inconclusive. Earlier
performance receipts apply to their pre-merge builds, not this combination.

The [repeated-work follow-up](docs/verification/chrome-target-20261030.md)
reduces all fourteen paired layout workloads by 5.1-53.3% against `ca1748c6`.
Thirteen beat Chrome; autosizing-flex leaf updates remain 0.000971 ms slower.
Cold fixture results are mixed, including a 2.5% populated-inventory regression.
Windows core passes 492,088 checks; GCC passes 15 suites / 704,365 checks;
Clang ASan/UBSan passes 17 suites / 704,359 checks. The full qualified gate,
338 Chrome captures, both Windows host builds and Unity full EditMode pass
(244 passed, two environment-gated inconclusive). The receipt records the
bounded caches, retained-flex proofs, allocation changes and measurement limits.

The [same-scene Chrome rendering comparison](docs/verification/rendering-chrome-20261030.md)
adds actual survival-scene traces at 60 scheduled updates/second. CPU stage
boundaries remain explicit; Chrome hardware GPU timing is unavailable. Three
trace-accounting tests pass; engine gate counts are unchanged. Inventory
selection remains the main measured CPU gap.

## 4. Workflow recipes

For rendered survival UI measurements, use the
[Godot render probe](Tools/performance/README.md#rendered-godot-frames).
The [October 30 rendering receipt](docs/verification/rendering-20261030.md)
separates controller/update CPU, host draw callbacks, viewport CPU/GPU and
timestamp-disabled wall intervals. It changes no engine code or gate counts;
its RTX 5080/OpenGL/Vulkan results are not Chrome rendering parity.

The [October 30 Chrome benchmark setup](docs/verification/chrome-performance-20261030.md)
is superseded by the [matched layout benchmark](docs/verification/chrome-matched-20261030.md).
The [layout optimization follow-up](docs/verification/chrome-close-20261030.md)
adds dependency-driven cascade and layout reuse, ten runner regressions and
post-mutation Chrome geometry checks. Windows tooling discovery passes 61 tests
with nine Linux-only skips (70 total). GCC passes 15 suites and 698,598 core
checks; Clang ASan/UBSan passes 17 suites and 698,592 core checks. Windows core
passes 486,321 checks; full Unity EditMode remains 244 pass with two inconclusive.
The full qualified local gate passes, including 336 Chrome layout captures,
Chrome behavior checks and Godot rendering, exports and scene tests.
These compare mutation-to-layout latency, not end-to-end rendered frame cost.

The [grid and allocation follow-up](docs/verification/chrome-deeper-20261030.md)
adds final-area-first grid layout and allocation-free traversal, with fourteen
matched benchmark workloads. GCC passes 15 suites / 700,445 core checks;
Clang ASan/UBSan passes 17 suites / 700,439 checks. Windows core passes 488,168
checks; full Unity EditMode remains 244 pass with two inconclusive. The Chrome
layout corpus now has 337 captures (65 hand cases), all within the 1.5px ceiling.
Tooling remains 70 tests, with nine Linux-only skips on Windows.
The full qualified release gate passes, including live Chrome behavior and
Godot rendering, native exports and scene tests; see the follow-up receipt.

The [incremental layout follow-up](docs/verification/chrome-parity-20261030.md)
keeps all fourteen benchmark inputs unchanged. Seven workloads beat Chrome;
four more are within 0.0013ms. Fractional panel resizing and autosizing flex
still trail Chrome. Windows core passes 489,999 checks; GCC passes 15 suites /
702,276 checks; Clang ASan/UBSan passes 17 suites / 702,270 checks. The full
qualified gate passes with 338 Chrome captures, including the new intrinsic
grid case; a final conservative cache restriction repeats both core/corpus
suites and Windows host validation. Unity remains 244 pass, two inconclusive.
See the receipt for raw failed-candidate evidence and final binary hashes.

The [scaled-overflow follow-up](docs/verification/scaled-overflow-20261025.md)
passes 14 CTest suites on GCC and Clang and 63 Chrome hand-corpus cases.
The [scaled-text follow-up](docs/verification/scaled-text-20261025.md) passes
both core suites, 64 Chrome hand cases and the Agentic font/render gate.
The [option-background follow-up](docs/verification/option-backgrounds-20261025.md)
repeats those gates and checks a live dark dropdown.
The [image and thin-border follow-up](docs/verification/image-filtering-20261025.md)
passes both core suites and 64 Chrome hand cases, with inspected Chrome and
Agentic renders of downscaled icons and rounded borders.
The [selector invalidation follow-up](docs/verification/selector-invalidation-20261025.md)
passes both core suites and 64 Chrome hand cases; its unrelated-hover regression
restyles one of 610 elements with output equivalent to a full cascade.

### A CSS feature or fix

1. Find or write the Chrome capture: a case in `Tools/oracle/corpus`
   (`harvest_corpus.py`, `capture-all-chrome-layouts.mjs --metrics=mono`).
2. Implement in `libweva/src`, test in `libweva/tests` (`CHECK` macros; a
   `std::vector` literal inside `CHECK` needs extra parentheses).
3. `chrome_sweep.py` agrees; if the feature is host-visible (a new draw kind,
   a new event), extend the ABI, regenerate, and pin it in both hosts.
4. Update `Packages/com.wevaui/Documentation~/supported-css.md` if the
   author-facing matrix changes.

### A host behaviour (input, fonts, rendering)

1. Decide whether the core should own it (both hosts need the same answer)
   or the host (engine-specific translation). Core-owned: ABI call, core test,
   both hosts opt in.
2. Unity: the feed/backend change in `Runtime/Native`, an EditMode test in
   `Tests/Editor/Native` (input through `InputTestFixture`).
3. Godot: `hosts/godot/src`, a scene test in `hosts/godot/project`.
4. Add the row to `docs/INPUT_PARITY.md`.

### The Unity API

`WevaDocument`'s supported surface is listed in
`Packages/com.wevaui/Documentation~/api-stability.md`; a change to it is a
major bump with a CHANGELOG entry. `Weva.Native` is internal and unsupported —
it follows the ABI.

## 5. Conventions that bite

* The Bash tool's heredocs mangle backslashes in C++/C#/Python text: write
  patch scripts to a file and run them, or use the Edit tool.
* C++ hex escapes are greedy (`"\xAD" "cdef"`, not `"\xADcdef"`).
* `FontEngine` is one process-global state machine; the Unity backend reloads
  the face before every document update. `GetPairAdjustmentRecord(a, b)`
  returns garbage for unkerned pairs; use the list overload per pair.
* Core vertex colours are linear, texels sRGB; the in-pass URP path blends in
  linear space, the offscreen path composites in gamma like the Godot host.
* A full Unity EditMode run may recreate `Tests/Runtime/Goldens/Out/`; do not
  commit it.
* Keep gate counts and evidence links current in `AGENTS.md`, `AI_REFERENCE.md`
  and the verification reports. `docs/PRODUCT_READINESS.md` is the concise
  user-facing status: update it when supported use or limitations change.

The [attribute batching audit](docs/verification/render-batching-20261030.md)
removes intermediate Godot paints, adds ten timing checks (31 total), and
records preliminary before/after evidence. The continuing rendering audit
uses Agentic at the user’s request; Godot results do not qualify that host.

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
