# Product readiness

Weva is a **development preview** for HTML/CSS game UI in Unity and Godot.
The latest integrated checks and remaining engineering debt are recorded in the
[technical audit](verification/tech-debt-20261101.md). Older checkpoints and
measurements are retained in [readiness history](PRODUCT_READINESS_HISTORY.md)
and [verification receipts](verification/README.md).

The core now decodes JPEG as well as PNG, including JPEG bytes with a PNG file
name. Pointer inheritance and replaced-image backgrounds have Chrome-backed
regressions. Adoption into a separate Agentic SDK is a distinct qualification
step; a passing core/Unity/Godot receipt does not prove that SDK contains it.

## Choose your host

| Host | Requirements and tested scope | Start here |
|---|---|---|
| **Unity** — package 2.0.0 (unreleased) | Unity 6000.3+, URP 17 and Input System 1.7. Windows x64 editor tests and a Mono development player pass, including packaged PNG bytes, default fonts, bindings and URP rendering. | [Unity setup](../Packages/com.wevaui/Documentation~/getting-started.md) |
| **Godot** | Godot 4.7, Windows/Linux x64. Sample projects, rendering and desktop exports have been tested with specific editor/template builds; see the text caveat below. Check the addon's `build.json` for included platforms. | [Godot setup and example](../hosts/godot/ADDON_README.md#install-and-run) |

Other Unity platforms and Godot macOS, mobile and console builds are unverified.
Consumer IL2CPP stripping graphs and asset-bundle/Addressables loading
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
- **Performance:** measure first opening, active updates, rendering and a busy
  game scene on target hardware. CPU/GPU and Chrome parity are not established
  universally. Explicit preparation can move work into loading; it does not
  guarantee first GPU execution is complete. Historical speedups describe their
  own builds and workloads; see [measurement receipts](verification/README.md).
- **Memory:** Unity callback/font retention fixes have regressions; Godot
  passes short rendered lifecycle checks. A current locally built Agentic SDK
  does not reproduce the older SDK's five-minute reload growth in matched
  idle/reload controls with a cooldown. The historical allocation owner remains
  unresolved; C++/driver attribution and long hardware soaks remain incomplete.
  See [the current SDK follow-up](verification/agentic-memory-20261101.md) and
  [preserved host memory evidence](verification/host-memory-20261101.md).

- **Deep markup:** content nested more than 64 elements deep is not rendered,
  so layout stays within a 1 MB thread stack.

## Evidence behind this status

Use the [latest integration audit](verification/tech-debt-20261101.md) for exact
build identities, suites and exceptions. These are local checkout results, not
certification of every platform or a published stable release.
