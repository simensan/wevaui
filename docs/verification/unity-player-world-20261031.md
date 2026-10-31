# Unity synthetic 3D scene timing — October 31, 2026

The presented-frame diagnostic now accepts `--world cubes`. It creates 320
lit cubes plus a ground renderer, eight shared material instances, a directional
light, a moving camera and 64 rotating objects. Geometry, animation and camera
motion repeat deterministically for each case. Colliders are removed; there
is no physics simulation, gameplay, skinning, terrain streaming or postprocessing.
The current URP asset disables main-light shadows: the light's Hard setting
does not override that. This is a modest synthetic scene load, not GPU saturation.

The world stays active for the control and all UI cases. No user scene is loaded,
edited or saved. The test owns and destroys its runtime objects and materials.
An explicit test material resource includes the URP Lit dependency in the player.
The initial attempt used URP's editor-only defaultMaterial accessor and failed
because it returns null in players; that failed build/run evidence is preserved
under `.utmp/render-audit-20261030/unity-player-world`.

## Results

Unity 6000.4.1f1, Windows x64 Mono development player, D3D12, RTX 5080,
1280×720, target 60 Hz, VSync off. Three rounds reverse case order in the
middle round. Each phase has 120 warmups and 600 unique measured frames.
All 9,000 GPU readings are available; all frames are focused, with no missing
or repeated frame timestamps.

Median of three per-round medians / p95 values, milliseconds:

| Case | CPU main | CPU render thread | Reported GPU frame |
| --- | ---: | ---: | ---: |
| World only | 0.482 / 0.792 | 0.177 / 0.296 | 0.0335 / 0.0376 |
| World + idle HUD | 0.570 / 0.853 | 0.286 / 0.405 | 0.0348 / 0.0389 |
| World + changing HUD | 0.817 / 1.321 | 0.402 / 0.616 | 0.0412 / 0.0458 |
| World + idle inventory | 0.649 / 0.997 | 0.399 / 0.623 | 0.0445 / 0.0489 |
| World + changing inventory | 1.054 / 1.591 | 0.525 / 0.773 | 0.0525 / 0.0563 |

These are whole-player timings, not isolated UI GPU intervals. CPU frame
medians remain about 16.667 ms because of the target frame rate. Do not add
phase statistics or subtract p95 values to derive UI cost. This is neither
an uncapped throughput test nor a before/after speedup measurement. GPU clocks
are not locked, and results do not establish lower-end or GPU-bound performance.

The HUD changes bound stamina and bar width. Inventory changes quantity and
two slot highlights, not its complete selection/controller sequence. Automatic
updates and camera rendering stay active. The test checks renderer serials
from eligible frames and current binding values, then takes verification captures
outside timing. World-only, HUD and inventory captures were inspected.
The [original player timing receipt](unity-player-timing-20261031.md) defines
the timestamp, availability and capture boundaries in detail.

## Verification and reproduction

The opt-in player diagnostic passes, exit zero, with fifteen complete phases.
Python compilation and diff checks pass. Production engine/host behavior is
unchanged, so production correctness suites were not rerun. The added test
material is serialized by Unity's importer and references the existing URP shader.

```powershell
python Tools/performance/run_unity_player_timing.py --unity "C:/Program Files/Unity/Hub/Editor/6000.4.1f1/Editor/Unity.exe" --fixtures .utmp/render-audit-20261030/survival-fixtures --output .utmp/render-audit-20261030/unity-player-world-v2 --fps 60 --world cubes
```

Use a new output directory. Raw frames, summaries, build log, XML and captures
are in that directory. The adjacent JSON preserves summaries and hashes,
including the pipeline asset that disables shadows. This closes a Unity
co-rendering measurement gap; shadow-heavy/GPU-bound scenes, full controller
mutations, weaker hardware and matched Chrome GPU timing remain open.
