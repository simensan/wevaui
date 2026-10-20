# Product readiness

Updated October 20, 2026 · Integration verification through October 20.

The [merge integration receipt](verification/merge-20261020.md) records
validation of the combined engine and host changes. Performance measurements
remain specific to the earlier builds identified in their reports.

**Weva is a development preview** for building game UI with HTML/CSS in Unity
and Godot. HUDs, menus, settings screens and data-bound lists are ready to try.
Production adoption still needs validation in your game, on your target devices.

## Choose your host

| Host | Requirements and tested scope | Start here |
|---|---|---|
| **Unity** — package 1.0.0 | Unity 6000.3+, URP 17 and Input System 1.7. Windows x64 editor tests and a Mono development player pass, including packaged PNG bytes, default fonts, bindings and URP rendering. | [Unity setup](../Packages/com.wevaui/Documentation~/getting-started.md) |
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
  These performance reports describe separate pre-merge builds; combined
  performance has not been remeasured.

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
