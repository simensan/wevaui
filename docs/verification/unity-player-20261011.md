# Unity standalone player audit — October 11, 2026

The Windows x64 **Mono development player passes all six UI smoke checks**
using Unity 6000.4.1f1. This closes the previous "no actual player build"
gap for this configuration. It is not an IL2CPP or production stripping result.

## Build blocker fixed

`Assets/Settings/Weva_Renderer.asset` retained an inactive legacy
`UIRendererFeature` subasset whose script was deleted with the old C# engine.
Editor rendering still worked, but the standalone build failed inside URP's
`RenderingLayerUtils.RequireRenderingLayers` / shader preprocessing with a
null renderer feature. Removed only that obsolete subasset and its list entry;
the active `UIBatchedRendererFeature` remains. No user scene was edited.

This is project configuration cleanup, not a core layout or rendering change.
Migrated consumer projects need the same missing-feature cleanup if affected.

## Verification

| Check | Result |
|---|---|
| Windows x64 Mono development player | 6 passed, 0 failed, 0 skipped/inconclusive |
| Full Unity EditMode regression suite | 243 passed, 0 failed, 2 existing environment-gated inconclusive |
| Windows native plugin Release build | Passed |
| Player plugin compared with package plugin | Identical SHA-256 |
| Packaged asset screenshot | Inspected: green PNG on the left, white `Camp` text on the right |

The five existing player rendering cases cover document stacking and disabling,
attribute-driven repaint, text, backdrop inversion and backdrop blur. The new
case loads a PNG from a packaged `Resources` TextAsset through `AssetReader`,
disables system font fallback, verifies default font inclusion and visible
glyphs, dispatches a keyboard-activated button to a preserved controller method,
and changes bound text from `Camp` to `Trail` without rebuilding the document.
The image reader serves only the packaged PNG; it does not read the checkout.

The first asset test used incorrect binding markup (`data-text`), which produced
an empty label. Correcting the fixture to the supported `{{ Name }}` syntax made
the check pass. That failure was in the test, not a player binding defect.

The existing EditMode inconclusive tests still require `WEVA_NATIVE_PARITY`
and `WEVA_NATIVE_DUMP_MANIFEST`. The earlier complete release gate is recorded
in [the technical audit](tech-audit-20261011.md); it was not rerun for this
Unity-only fixture/configuration change. No runtime performance claim is added.

## Repeat and inspect

```powershell
python Tools/run_unity_player_tests.py --unity "C:/Program Files/Unity/Hub/Editor/6000.4.1f1/Editor/Unity.exe" --out .utmp/player-smoke-new
```

The output directory must be new. The runner sets `WEVA_TEST_PLAYER=1`, enabling
an otherwise inactive build modifier that includes only Unity's generated test
bootstrap scene. It does not change the project's build scene list or scripting
backend. Normal test-player builds without this environment variable keep their
normal scene selection. Unity still performs its normal package build hooks.
The runner fails if Unity fails or fewer than six successful test cases return.

Raw evidence is preserved under `.utmp/player-audit-20261011/`:

- `build.log`: initial stale-feature build failure.
- `retry/`: successful build, five passing tests and the incorrect fixture failure.
- `final/`: passing player, build log, XML, screenshots and runner receipt.
- `editmode.xml` and `editmode.log`: full editor regression run after the change.

The [machine-readable receipt](unity-player-20261011.json) records hashes and
counts. The runnable player is under `final/player/PlayerWithTests/`.

Still unverified: IL2CPP/AOT, release stripping, baked CSS in a serialized
consumer scene/prefab in a player, custom `@font-face` byte packaging,
Addressables/bundles, other platforms and real game performance. Existing
editor tests for stylesheet baking do not substitute for those player checks.
