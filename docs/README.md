# Documentation

Start with [product readiness](PRODUCT_READINESS.md) for tested platforms and
adoption limits. Weva is a development preview for HTML/CSS game UI in Unity
and Godot.

## Build UI

| Task | Unity | Godot |
|---|---|---|
| Install and run an example | [Getting started](../Packages/com.wevaui/Documentation~/getting-started.md) | [Addon guide](../hosts/godot/ADDON_README.md) |
| Connect UI to game state | [Authoring guide](../Packages/com.wevaui/Documentation~/AuthoringGuide.md) | [Bindings and actions](../hosts/godot/ADDON_README.md#connect-a-game-in-three-steps) |
| Use fonts and languages | [Text and fonts](../Packages/com.wevaui/Documentation~/text-and-fonts.md) | [Godot text integration](GODOT_TEXT_SHAPING.md) |
| Diagnose a problem | [Troubleshooting](../Packages/com.wevaui/Documentation~/troubleshooting.md) | [Input](KEYBOARD_INPUT.md), [IME](IME.md), [exports](DESKTOP_EXPORTS.md) |

The shared engine's [HTML](../Packages/com.wevaui/Documentation~/supported-html.md)
and [CSS](../Packages/com.wevaui/Documentation~/supported-css.md) references
describe the supported subset. Host-specific APIs and fonts differ.
[Frontier Camp](../examples/frontier_camp/README.md) shows a standalone Godot
game integration.

## Develop and verify Weva

- [Engineering contract](../AGENTS.md), [architecture](ARCHITECTURE.md) and
  [C++ conventions](CONVENTIONS.md).
- Host build/reference guides: [Unity](../hosts/unity/README.md),
  [Godot](../hosts/godot/README.md).
- [Chrome oracle](../Tools/oracle/README.md), [input parity](INPUT_PARITY.md),
  [sanitizers](SANITIZERS.md) and [release checks](RELEASE.md).
- [Latest full review](verification/review-three-days-20260915.md) and
  [performance measurements](RUNTIME_PERFORMANCE.md). Results apply to their
  recorded builds and machines; they are not platform-wide guarantees.

## Historical records

The [port plan](PORT_PLAN.md), [original design](../PLAN.md),
[C# conformance record](../CONFORMANCE.md), [old roadmap](../ROADMAP.md),
[performance log](PERFORMANCE.md) and [readiness history](PRODUCT_READINESS_HISTORY.md)
preserve earlier decisions and measurements. Their build numbers, pending work
and old source paths describe those checkpoints. Use the current guides above
for setup and supported behavior. [Verification receipts](verification/README.md)
are retained unchanged.
