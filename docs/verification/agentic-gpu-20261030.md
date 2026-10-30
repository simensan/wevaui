# Agentic GPU and selection audit — October 30, 2026

Agentic commit be88b3cb2 adds opt-in hardware GPU timing, adapter identity, viewport controls and data-driven authored mutations to the existing Agentic `weva_cost` diagnostic. No production renderer/core behavior changes in this chunk.

The current Weva source is linked using `WEVA_SOURCE_DIR`. Agentic starts from packed-snapshot commit ff1c9925b on codex/weva-render-audit. The branch remains unintegrated into the configured engine-main runtime.

## Actual scope

The same prepared survival UI fixtures used by the packed-snapshot audit are rendered with real text and PNG icons derived from the authored SVGs. The font is the decompressed original Open Sans SemiBold. Native world/background layers and tab focus are absent. Source fixture hashes are in agentic-packed-20261030.json; the mutation hash is in the companion receipt.

HUD mutations update stamina text and meter width. Inventory mutations cycle all twelve selections: slot classes, item name/category/description/weight/count, action label, icon and disabled state. These are repeated scripted mutations, not OS input-to-photon measurements. The controller uses direct attribute writes, including unchanged classes; Chrome classList.toggle(force) and Godot class toggling can avoid those unchanged writes. That semantic no-op cost is an explicit next core audit target.

Three independent runs per resolution; each CPU phase warms 20 frames and measures 200 mutations, uncapped with simulated dt=1/60. CPU values below are the median of three run means, not per-frame p95. Unlike the old core-plus-host subtotal, complete host timing includes selector queries, all setters, the update, paint snapshot/upload preparation, command encoding and queue submission. Queue calls may include driver waits; this is host-thread elapsed time.

The GPU measurement is a **separate retained-state pass after mutations stop**: 20 warmups, 180 timestamped draws per run, 540 pooled samples. It measures rendering the final selection, not GPU distributions over changing selections. Target clearing is encoded before the start timestamp; UI render commands are bracketed by encoder timestamps. Query resolve/readback/wait occurs after the batch and outside the recorded intervals. wgpu warns that exact encoder timestamp ordering can depend on driver/backend, so these are measured command intervals rather than exact shader-stage attribution.

Hardware: RTX 5080, NVIDIA 617.42, Vulkan, RGBA8Unorm, 1x sampling. Timestamp period is 1 ns. Missing timestamp support fails explicitly; zero-only or non-monotonic intervals fail. No GPU-process CPU trace is substituted for GPU execution.

## Results

Milliseconds:

| Viewport | Workload | Controller/update | Complete host path | Retained GPU median / p95 |
|---|---|---:|---:|---:|
| 1280x720 | hud_updates | 0.121 | 0.251 | 0.003936 / 0.004704 |
| 1280x720 | inventory_selection | 1.501 | 1.741 | 0.011072 / 0.011616 |
| 1920x1080 | hud_updates | 0.130 | 0.271 | 0.003968 / 0.004544 |
| 1920x1080 | inventory_selection | 1.488 | 1.733 | 0.014784 / 0.015232 |
| 3840x2160 | hud_updates | 0.110 | 0.242 | 0.003488 / 0.004032 |
| 3840x2160 | inventory_selection | 1.603 | 1.856 | 0.033760 / 0.034208 |

Selection preparation remains the dominant measured cost. These numbers do not establish Chrome GPU parity, whole-game FPS, cold-opening behavior, lower-end hardware performance or typical click-frequency cost. The large GPU remains lightly loaded by this isolated UI. CPU variation across runs is retained rather than filtered.

## Validation and reproduction

The optimized diagnostic build and all nine resolution/mutation runs pass. Empty state arrays and missing mutation targets are rejected; a final GPU+mutation smoke run passes. Asset checks run before measurement and after mutations. This is diagnostic validation, not a new engine gate or runtime integration test.

Set WEVA_GPU_TIMING=1 to enable the separate timestamp pass. WEVA_BENCH_WIDTH and WEVA_BENCH_HEIGHT default to 1280 and 720. Pass HUD.html HUD.css inventory.html inventory.css and optionally mutations.json. The mutation file is an array of two cases, each an array of nonempty cyclic states. Each state contains objects with selector and value; optional attribute selects an attribute mutation, with null value removing it. Without attribute, value must be text. See crates/agentic_weva/tools/scripted_mutations.rs and gpu_cost.rs.

Raw evidence: .utmp/render-audit-20261030/selection-<width>-<round>.log, gpu-final-smoke.log, negative-input logs and build logs. All GPU samples and per-run CPU means are in agentic-gpu-20261030.json. The older printed frame subtotal excludes setters and should not be used as end-to-end CPU time; use complete CPU path.

## Next work

Inspect shared-core redundant attribute invalidation and selection reflow/paint work, then verify applicable changes through Agentic, Godot and Unity. Matched Chrome rendering remains separate work because its trace did not provide hardware GPU execution. Busy-scene/lifecycle/cold-opening checks and branch integration remain open; the overall audit is not complete.
