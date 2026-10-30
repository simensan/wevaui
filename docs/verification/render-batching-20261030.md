# Attribute batching audit — October 30, 2026

Godot attribute set/remove methods forced a full update before each write,
while text/class/value mutations already used non-flushing DOM resolution.
Selecting a survival inventory item therefore painted intermediate states.
Both attribute methods now use the existing non-flushing resolver. Live DOM
and pseudo selectors remain current; geometry reads and explicit/frame updates
still flush. Popover event delivery is unchanged. No core or ABI changes.

A diagnostic on Windows RTX 5080, Godot 4.7 Mobile/Vulkan, 1280x720 reduced
inventory-selection controller/update median from 1.817 ms to 1.166 ms (35.8%).
This is preliminary: one sequential run per version, 120 warmups and 180
measured frames, uncapped, timestamps off, core-stat collection and draw logging
on. It is not a paired multi-round throughput claim or an Agentic result.
HUD updates measured 0.110 versus 0.116 ms in those runs.

The optional `WEVA_RENDER_BENCH_CORE_STATS=1` records last-update core stages.
These are diagnostic and can miss earlier synchronous updates in the controller;
they must not be treated as aggregate controller costs. Background can retain
previous stats because it does not call update.

Validation: Windows Release host build passes. Timing 31, binding 186,
form state 522, explicit validity 104, dialog cancellation 1199, and input
geometry 5 checks pass with zero failures. The timing regression covers batched
text/attribute changes, class and live disabled selectors, missing targets,
final geometry and exactly one geometry-triggered flush. HUD, changing HUD,
inventory and final selection screenshots are RGB pixel-identical to baseline;
the final selection render was inspected. No new whole-engine gate is claimed.

Raw logs, screenshots and samples: `.utmp/render-audit-20261030/`.
Compact measurements: [JSON](render-batching-20261030.json).

At the user's request, the continuing deep rendering audit switches to Agentic.
Its configured checkout is `E:/Convoy_Agentic-slots/engine-main`, with
`agentic_weva` and a wgpu renderer. Its pinned native SDK is older than this
Weva checkout, so a current-core comparison must explicitly build using
`WEVA_SOURCE_DIR`, record the revision, and avoid attributing Godot host gains
to Agentic. Agentic paint snapshots copy all draw geometry on changed frames;
renderer preparation repacks/uploads geometry. These are audit candidates,
not yet measured bottlenecks. Hardware GPU timing still needs instrumentation.
