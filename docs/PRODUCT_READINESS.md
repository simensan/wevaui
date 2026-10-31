# Product readiness

The local Agentic audit branch adds explicit hidden CPU preparation through
`html_prepare()`. Paired runtime checks show lower interactive opening cost
by moving layout/glyph/paint work into loading; GPU first-use work remains.
This is not yet an upstream Agentic integration. See the
[October 31 preparation receipt](verification/script-preparation-20261031.md).

Unity 2.0 adds retained `Visible`, CPU `Prepare()` and texture/mesh/material
`PrepareRenderer()` loading controls, with 252 EditMode passes (two existing
environment-gated inconclusive), seven URP render passes and seven Windows
Mono player passes. Renderer preparation moves work into loading; first shader
execution and cold-opening player timing remain unverified. See the
[renderer preparation receipt](verification/unity-renderer-api-20261031.md).
The [material preparation follow-up](verification/unity-material-preparation-20261031.md)
moves lazy material setup into loading and reduces first command recording
in the paired editor benchmark; it does not warm GPU shader execution.
The [interleaved upload follow-up](verification/unity-interleaved-upload-20261031.md)
reduces median inventory mesh synchronization CPU; tail results remain mixed.
Godot's existing hidden manual-update workflow is verified on Windows
and the rebuilt qualified Linux addon. See the
[host preparation receipt](verification/unity-hidden-preparation-20261031.md).

Updated October 31, 2026 · Latest full integration gate: October 30; scoped host verification: October 31.

Scoped October 25 core/Agentic checks cover filtered downscaled icons and
antialiased thin rounded borders; see the
[rendering receipt](verification/image-filtering-20261025.md).
The [October 30 receipt](verification/chrome-target-20261030.md) records the latest
full local gate and Unity EditMode results: 244 pass, zero failures and two
environment-gated inconclusive. Godot rendering, exports and scene checks pass
with the qualified patched editor and templates.

The [merge integration receipt](verification/merge-20261020.md) records
validation of the combined engine and host changes. Performance measurements
remain specific to the earlier builds identified in their reports.

**Weva is a development preview** for building game UI with HTML/CSS in Unity
and Godot. HUDs, menus, settings screens and data-bound lists are ready to try.
Production adoption still needs validation in your game, on your target devices.

The rendering audit's nested first-baseline discrepancy is fixed for the
captured block/grid/flex cases, including reordered children. See the
[baseline receipt](verification/nested-baseline-20261030.md) for the scoped
Chrome and host checks; this is not complete CSS baseline conformance.

The [Agentic runtime audit](verification/agentic-runtime-20261030.md) validates
the current core in an immutable Agentic SDK with a scripted survival UI over
a synthetic 3D scene: clicks/bindings, 9,000 profiled frames and 339 short-cycle
reloads. This is scoped runtime evidence, not a new supported-host release or
a matched Chrome rendering comparison.

The [cold renderer follow-up](verification/agentic-cold-render-20261030.md)
reduces Agentic's median threaded opening frame by 6.6% in that synthetic
scene, but retains a 69 ms outlier. Cold opening is not yet hitch-free.

The [hidden UI follow-up](verification/agentic-hidden-ui-20261030.md) removes
unnecessary Agentic direct rendering work while hidden data continues changing.
This improves that specific workload; it does not change release status.

Godot also [defers automatic hidden updates](verification/godot-hidden-ui-20261030.md).
Explicit `update_document(0)` can prewarm a hidden document before showing it.
Unity disabling releases the native document rather than retaining it.

## Choose your host

| Host | Requirements and tested scope | Start here |
|---|---|---|
| **Unity** — package 2.0.0 (unreleased) | Unity 6000.3+, URP 17 and Input System 1.7. Windows x64 editor tests and a Mono development player pass, including packaged PNG bytes, default fonts, bindings and URP rendering. | [Unity setup](../Packages/com.wevaui/Documentation~/getting-started.md) |
| **Godot** | Godot 4.7, Windows/Linux x64. Sample projects, rendering and desktop exports have been tested with specific editor/template builds; see the text caveat below. Check the addon's `build.json` for included platforms. | [Godot setup and example](../hosts/godot/ADDON_README.md#install-and-run) |

Other Unity platforms and Godot macOS, mobile and console builds are unverified.
Unity IL2CPP, release stripping and consumer asset-bundle/Addressables loading
remain unverified; see the [player smoke audit](verification/unity-player-20261011.md).
Unity 0.1.x users should follow the [1.0 migration notes](../Packages/com.wevaui/CHANGELOG.md#migrating-from-01x):
there are breaking API changes, and migration of an existing consumer project
has not yet been verified.

## What you can build

Both hosts support flex/grid layouts, scrolling panels, styled text and images,
animations, editable controls, repeated rows and two-way data bindings. Connect
HTML actions to C# or GDScript and use hot reload while developing.

Weva implements a web subset, with no JavaScript engine or network resource
loader. Check the [HTML](../Packages/com.wevaui/Documentation~/supported-html.md)
and [CSS support tables](../Packages/com.wevaui/Documentation~/supported-css.md)
before bringing over a web design. Advanced writing modes, some form behavior
and browser-level visual/text fidelity remain incomplete.

## Check these before adopting

- **Godot native text:** Weva documents work around a stock Godot text-shaping
  defect. Native Godot controls can still corrupt or crash on long emoji-heavy
  strings. The verified fix needs a patched editor **and matching export
  templates**. Use the [tested configuration and instructions](GODOT_TEXT_SHAPING.md#stock-godot-472-limitation)
  if your game uses affected native controls.
- **Fonts and languages:** test your actual strings and fonts. Unity has no
  color emoji, and Sinhala, Khmer, Myanmar and Tibetan shaping is incomplete.
  Unity `Font` assets do not receive the substitutions available to fonts loaded
  from files/bytes; see [font setup and limits](../Packages/com.wevaui/Documentation~/text-and-fonts.md).
  Mixed-direction caret behavior also needs further work.
- **Input and accessibility:** automated input tests cover mouse, keyboard,
  gamepad, touch and IME composition. Physical gamepad, touchscreen and system
  IME acceptance, plus accessibility, remain incomplete.
- **Performance:** measure screen opening as well as steady gameplay. Across the
  earlier runs, the slowest core-only cold-creation samples averaged 52–56 ms.
  [Parallel rasterization](verification/cold-raster-20261011.md) lowers paired
  GCC cold HUD creation by 35.5% to 33.368 ms, using additional CPU cores. This
  still exceeds a 60 Hz frame budget before host rendering or asset loading.
  Preparing and reusing screens may help.
  Since the [September 25 audit](verification/tech-audit-20260925.md), cold paint
  uses up to four threads, and wide blurs are drawn at the resolution they need.
  The [screen-opening pass](verification/screen-open-20260925.md) measured
  first opens up to 2.5× faster. A screen created again, such as a Unity menu
  re-enabled, was up to 8× faster on paint-heavy pages, because rasterized
  textures are kept across documents. `weva_set_raster_cache_limit` bounds
  that cache (32 MiB by default). `WEVA_RASTER_THREADS=1` keeps everything on
  the calling thread.
  The [two performance passes](verification/performance-20260915-pass2.md) cover
  standalone Godot fixtures on a Ryzen 7 9800X3D/RTX 5080; they do not establish
  Unity performance, full-game FPS or lower-end hardware suitability.
  The [October audit](verification/performance-audit-20261011.md) repeats the
  runtime workloads and identifies cold paint preparation and scrolling as the
  leading investigation targets; it adds no whole-game performance qualification.
  The [runtime geometry change](verification/runtime-geometry-20261011.md)
  lowers paired inventory scrolling API CPU p95 by 11.9% to 1.097 ms with
  identical pixels; whole-game and lower-end testing remain outstanding.
  Those performance reports describe separate pre-merge builds. The
  [latest October 30 comparison](verification/chrome-target-20261030.md) keeps
  fourteen matched workloads unchanged. All improve against the previous native
  build by 5.1-53.3%. Thirteen beat Chrome; autosizing-flex leaf updates remain
  0.000971 ms slower (0.009621 vs 0.008650 ms). Both engines exclude paint in
  this comparison. Separate native measurements include paint preparation.
  Cold fixture creation is mixed, including a 2.5% populated-inventory regression.
  These results do not measure host/GPU presentation or game load.
  The separate [rendering probe](verification/rendering-20261030.md) now measures
  Godot CPU/GPU rendering of the actual survival sample on an RTX 5080 at
  1280x720, including a background-only control. It covers OpenGL and Vulkan;
  the [same-scene Chrome follow-up](verification/rendering-chrome-20261030.md)
  adds CPU rendering-stage traces. Inventory selection remains more expensive
  in Weva. End-to-end/GPU parity, weaker hardware and busy 3D scenes remain
  unverified.
  The [Unity player timing audit](verification/unity-player-timing-20261031.md)
  adds 9,000 presented frames at 60 Hz on D3D12/RTX 5080. It reports whole-player
  CPU/GPU timing with a background control and explicit GPU availability
  (8,997/9,000), not isolated UI GPU time or uncapped FPS.
  A [synthetic 3D follow-up](verification/unity-player-world-20261031.md) adds
  321 renderers, camera motion and 64 rotating objects with 9,000/9,000 GPU
  readings. This scene has no active shadows and is not GPU-saturated.

- **Deep markup:** content nested more than 64 elements deep is not rendered,
  so layout stays within a 1 MB thread stack.

## Evidence behind this status

The October 11 technical audit fixed Unity callback ownership and renderer reuse,
repaired browser tooling and passed the complete local release gate with the
specified patched Godot builds and Windows Chrome reference. Unity full EditMode
passes 243 tests with two environment-dependent tests inconclusive; five PlayMode
rendering tests pass. Exact findings, coverage and remaining exceptions are in the
[technical audit](verification/tech-audit-20261011.md).
The subsequent [Windows player audit](verification/unity-player-20261011.md)
removed a stale legacy renderer reference that blocked builds and passed six
rendering/asset/binding checks in an actual standalone player.
These are local checkout results, not certification of a published release.
