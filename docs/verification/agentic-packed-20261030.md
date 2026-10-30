# Agentic packed paint audit — October 30, 2026

The audit moves to Agentic at the user's request. It uses the current Weva
source through WEVA_SOURCE_DIR, not Agentic's older pinned native SDK.
Agentic base: 2c5229a6109d078619b89d0ba5fbfd3b4d50ad36.
Weva base: a1de6aa4. Engine commit ff1c9925b is on codex/weva-render-audit in engine-9;
it is not yet integrated into the configured engine-main runtime.

## Kept change

A paint snapshot previously allocated vertices and indices per draw, then the
renderer copied and rebased every draw into two additional upload buffers.
The snapshot now owns the packed vertex/index arrays and draw ranges. Indices
are rebased once during capture; the render worker uploads the owned arrays
directly. No borrowed native pointers cross threads. Idle frame reuse, texture
ownership, draw order, clips, blends and backdrop boundaries are unchanged.
This reduces geometry allocations from two per draw plus two renderer arrays
to two arrays per changed frame, and removes one full geometry copy.

Unity and Godot were audited: their mesh/packed-array builders already consume
borrowed core geometry directly and reuse versioned batches; this duplicate
snapshot copy is specific to Agentic. Unity attribute writes already batch;
the earlier Godot attribute batching fix remains in a1de6aa4. No shared core
or ABI change is needed for this chunk.

## Measurements

Windows, Agentic fast optimized profile, current core built Release. Eight
alternating process pairs; each diagnostic warms 20 frames then measures 200.
The table gives median of printed per-process means (0.001 ms resolution).
Host includes snapshot capture, prepare/upload, encoding and queue submission;
these are host-thread elapsed timings, not GPU execution or input-to-photon.
The diagnostic does not report adapter/backend identity: do not attribute a
hardware GPU result to it. Background scheduling/frequency variation is visible
in unchanged-core timings; these are scoped measurements, not guarantees.

| Workload | Before host ms | After host ms | Reduction |
|---|---:|---:|---:|
| Survival HUD number update | 0.1615 | 0.1395 | 13.6% |
| Survival inventory quantity update | 0.5365 | 0.1770 | 67.0% |
| Tiny HUD number updates | 0.1285 | 0.1210 | 5.8% |
| 64-row list number update | 0.1740 | 0.1475 | 15.2% |

The survival inventory improves in all eight pairs (60.7–71.0% host reduction).
The survival HUD improves in six of eight pairs; it is not “always faster.”
These are warmed number updates, **not inventory-selection stress**, cold
opening, a busy 3D game or a new Chrome parity result. Native world/background
layers and inventory focus are absent from these isolated UI fixtures.

The survival HTML/CSS/state originated in the real sample. It renders the
actual text and icons at 1280x720. The exported Godot font was WOFF2 despite
its .ttf filename; the same font was decompressed to SFNT. Pinned Chrome 152
rasterized source SVGs at their authored dimensions into PNGs, outside timing.
Agentic's standalone loader rejects those compressed formats; feedback gap
weva-standalone-compressed-assets records that limitation. Earlier incomplete
asset runs are retained and excluded. The final diagnostic rejects missing
assets; final inventory capture has 299 draws and 25 textures.

## Verification and rejected experiments

Focused final paint tests (2) and render tests (12) pass; this is not a full runtime,
platform or workspace gate. The new packing test verifies every vertex range,
rebased index, texture, scissor, kind and blend against the published core list,
then clears the document while retaining the previous snapshot. Existing tests
cover queued frames across reload/destruction and worker rendering. The final
inventory PNG is RGB pixel-identical to the inspected complete pre-change PNG.

Per-command version sharing and partial GPU buffer uploads were tested first.
They did not consistently improve small HUD CPU cost and are **not included**.
Their patches, timings and failed-build logs remain in the evidence directory.
The full-disk build failure was resolved with the repository cache tool pruning
only this task's rebuildable incremental state.

Reproduce alternating comparisons with Tools/performance/compare_agentic_cost.py.
Agentic weva_cost now optionally accepts HUD.html HUD.css inventory.html
inventory.css, using absolute asset/font paths and a Depth number binding.
The default small HUD now uses a number-only Depth value in both paired builds;
do not compare its figures directly with the previous unit-suffixed workload.
Raw logs, fixtures and executables: .utmp/render-audit-20261030/.
The companion JSON records binary and fixture hashes and all paired results.

## Still required for the full audit

- Matched Agentic/Chrome survival mutations, including inventory selection,
  font/layout validation and equivalent rendering boundaries.
- Hardware GPU timing and CPU preparation/submission separation.
- Busy-scene, resolution, memory/lifecycle and cold-opening verification.
- Broader Agentic runtime validation and integration of the accepted branch.
- Continue checking shared-core findings and applicable Unity/Godot equivalents.
