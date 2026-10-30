# Hidden UI work and direct profiling — October 30, 2026

Agentic's direct renderer updated and uploaded hidden HTML documents while its
threaded snapshot path deferred that work. Binding changes therefore continued
to pay layout/paint/upload costs for an invisible inventory in direct mode.

Agentic `88e0e7f4b` makes `Overlay::update` defer work while hidden, retaining
dirty state and pending bindings. Showing it applies the latest values. The
direct game host also skips an empty encoder/submission for hidden overlays.
This follows the existing snapshot behavior; it does not add CSS rules or
duplicate shared-core layout behavior.

## Runtime result

Three interleaved before/after pairs, fresh processes, same prepared survival
inventory over the 240-cube scene, 1280x720, direct rendering, no VSync, Windows
RTX 5080 / Vulkan 617.42. Each profile warms 120 frames and measures 1,000
CPU-only frames. Every tick continues changing twelve slot classes and selected
item bindings while the entire overlay is hidden. It is a stress workload for
hidden updates, not a visible HUD benchmark or a general game FPS claim.

Medians of the three per-run statistics:

| Measurement | Before | After |
|---|---:|---:|
| UI CPU p95 | 1.693 ms | 0.0015 ms |
| UI CPU mean | 1.394 ms | 0.0022 ms |
| Main CPU p95 | 2.974 ms | 1.202 ms |
| Whole-frame interval p95 | 3.233 ms | 1.394 ms |

The whole-frame p95 reduction is 56.9% in this synthetic workload. Script
binding production and request application still run; the fix eliminates the
hidden document/render work, not the controller's work. UI p95 is not GPU
execution time. Independent percentile columns must not be summed.

The before SDK is `36c2fc75a`, which includes the profiling correction below;
the after SDK is `88e0e7f4b`. Both link the unchanged Weva core last changed at
`faf04f31`. Both are coherent official SDK publications. The managed Agentic
`engine-main` remains unchanged and these source commits remain on the leased
audit branch.

All 6,000 measured frames are unmarked. Script snapshots prove hidden mode and
continued tick progression throughout each profile. After show, clicking the
stone hatchet updates the selected index and click count. First-pair hidden and
shown captures match RGB pixels exactly before/after, and the after images were
inspected. The unit regression additionally proves latest pending style data
appears on show, no local renderer is allocated for initially hidden updates,
and a previously painted hidden document does not republish until shown again.

## Profiling correction

Agentic `36c2fc75a` includes direct HTML update, upload, encode/submit and IME
synchronization in the accumulated `ui` CPU section. Previously it omitted
that work. A real cold opening now reports 26.321 ms UI within 32.082 ms main
CPU, rather than approximately 0.001 ms UI. This is corrected attribution,
not a performance improvement.

Threaded main UI includes update/snapshot/IME, with drawing on the worker.
Direct main UI includes drawing/submission. Script request application and
initial page loading remain outside these sections; neither is a full
input-to-photon measurement. `docs/CLI_PROFILING.md` defines these boundaries.
The recorded `weva-direct-ui-profile-scope` observation is resolved with this
runtime evidence.

## Validation, host scope and next bottleneck

- Overlay suite: 43 pass. The initial test compilation used an incorrect
  attribute accessor return type; its failure is preserved and the corrected
  suite passes. Optimized CLI/headless/runtime SDK build and publication pass.
- The fixture generator adds key 7 for hidden inventory binding updates.
  Existing visible modes and the cold-open mode remain available.
- No core/ABI/Unity/Godot code changed. This fixes Agentic's direct-versus-
  snapshot lifecycle inconsistency. Unity's component lifecycle and Godot's
  explicit/manual update behavior have different contracts; this result does
  not claim hidden-work parity for them. Audit automatic hidden updates in
  those hosts separately before applying an equivalent optimization.
- An instrumented first-paint inventory trace records 8.13 ms in glyph
  preparation and 2.99 ms in queued raster jobs, within 13.30 ms total paint.
  Glyph preparation includes font callbacks and atlas work; this does not
  establish that all 8.13 ms is font rasterization. Cold glyph/atlas preparation
  remains a stronger optimization target than markup parsing alone.

Raw evidence is in `.utmp/render-audit-20261030/`: `hidden-paired/`,
`direct-ui-attribution/`, `cold-paint-profile.log`, SDK logs and
`hidden-overlay-tests-fixed.log`. All twelve paired profile/frame artifact
hashes were independently verified. The
[JSON receipt](agentic-hidden-ui-20261030.json) preserves per-run statistics,
globals, binary identities and artifact paths. The retained `run-hidden.py`
records exact launch/profile/input commands. All owned sessions were stopped.

Cold-opening tails, preload/reuse workflows, matched Chrome rendering and
long-duration lifecycle tests remain open. No release-readiness claim is made.
