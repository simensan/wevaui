# AGENTS.md — engineering contract for AI tools


Fix confirmed defects found during audits, verify the fixes and commit finished
chunks. Stop at findings only when the user explicitly requests an audit-only
or read-only pass. Prioritize root causes and useful changes over cosmetic work.

For current gate results and remaining debt, read the
[latest integration audit](docs/verification/parser-memory-20261101.md). Dated
checkpoints live in [the archive](docs/verification/agent-progress-history.md);
do not append experiment journals to this instruction file.

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

Current evidence is indexed by the [latest integration audit](docs/verification/parser-memory-20261101.md).
Latest verified checkpoint: GCC 21 suites / 809,530 main checks; Clang
ASan/UBSan 23 suites / 809,524 checks; 341 Chrome layouts within 1.5 px;
69 Chrome behavior scripts pass plus one known IME exception. Tooling: 90 pass
on Linux, 81 pass plus nine skips on Windows. Unity: 259 EditMode passes plus
two inconclusive, Mono player 7/7. See the audit for scope and build identities.
The [Agentic memory follow-up](docs/verification/agentic-memory-20261101.md)
records five-minute lifecycle controls for its identified SDK.

The qualified full gate uses the patched Linux Godot editor and templates, pinned
Windows Chrome through WSL, and a private Xvfb display. See the
[text-runtime requirements](docs/GODOT_TEXT_SHAPING.md) and the
[reference setup](docs/verification/review-three-days-20260915.md).

## 4. Workflow recipes

Use [the performance runners](Tools/performance/README.md) for measurements.
Record stage boundaries, matched controls, build identities and concurrent load.
Keep CPU, render submission and GPU execution claims separate.

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
* Keep one latest gate summary and receipt link current in `AGENTS.md` and
  `AI_REFERENCE.md`; keep dated narratives in the verification archive.
  `docs/PRODUCT_READINESS.md` is the concise user-facing status: update it when
  supported use or limitations change.
