# Unity loading-phase material preparation — October 31, 2026

PrepareRenderer now prepares materials for its current batches after textures
and meshes. Normal, blended and backdrop draws use the same material factories
as normal drawing. Ordinary Sync stays lazy. This adds no alternate rendering
path, public API, ABI change or shader variant warmup.

Three fresh-editor pairs alternate texture/mesh-only preparation and the
supported PrepareRenderer path. Unity 6000.4.1f1, RTX 5080, bound survival
inventory, 1280×720. Median CPU milliseconds:

| Stage | Textures/meshes only | Including materials |
| --- | ---: | ---: |
| Resource preparation during loading | 11.438 | 13.106 |
| First command recording | 3.092 | 1.310 |
| First offscreen render plus readback | 13.011 | 10.237 |

First recording is 57.6% lower in this three-pair sample. Its ranges are
2.931–3.250 ms before and 1.223–1.945 ms after. The combined diagnostic is
21.3% lower, but includes target allocation, submission and synchronous
readback. This is a shift into loading, not a reduction in total rendering
work, a player FPS result, or a GPU execution timing. Managed first use and
GPU pipeline work can still occur on the first draw. No sample was discarded.

All six processes exited zero and all twelve captures match the inspected
inventory reference. Raw evidence: `.utmp/render-audit-20261030/unity-material-preparation`.
Run the preparation benchmark with `--rounds 3 --material-comparison` and
the [existing editor/fixture arguments](unity-preparation-cost-20261031.md).
The control uses the same build and retains textures/meshes while leaving
material creation lazy. An XML-comment edit triggered a compilation between
processes, outside the measured method; executable benchmark logic was unchanged.

Godot ordinary canvas draws do not use Unity's per-texture Material objects.
Its custom blend/backdrop shaders have their own lazy/pool behavior. No Godot
gain is inferred from these measurements; profile that path before extending
hidden preparation there. Shared core behavior is unchanged.

## Correctness verification

Full EditMode passes 251 tests with two existing environment-gated inconclusive
and zero failures. Three new cases compare exact prepared/lazy pixels for normal
textured, multiply-blended and backdrop-filter content; repeated preparation and
drawing create no extra materials, and disposal releases them. The component
lifecycle test also requires materials to be present after hidden preparation.

URP PlayMode passes 7/7; Windows x64 Mono development player passes 7/7.
The four hidden/show captures match the inspected black/red/black/blue
reference in both runs. Player checks establish correctness, not the editor
timing improvement or IL2CPP/release stripping. Survival output was inspected.

Correctness evidence is alongside the benchmark under `material-api-edit.*`,
`material-api-play.*`, `material-api-play-renders/`, and `material-api-player/`.
The adjacent JSON preserves measurements, XML counts and SHA-256 evidence.
Python compilation and diff checks pass. No full integration gate is claimed.
