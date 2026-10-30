# Rendered survival UI measurement - October 30, 2026

Added `hosts/godot/project/samples/western_survival/render_bench.gd` and ran it with real text, images and GPU rendering. The engine is unchanged from `a46a9a23`; no runtime optimization is claimed by this measurement.

Windows, Godot 4.7 official, NVIDIA RTX 5080 (driver 617.42), 1280x720. Both Compatibility/OpenGL and Mobile/Vulkan were tested in a visible window. VSync and the frame cap were disabled. This is a static illustrated background, not a running 3D world.

Three rounds per renderer, 600 measured frames per case after 120 warmups. Case order reverses in the second round. Tables pool all 1,800 measured frames per case. A separate three-round run disables GPU timestamps, and a separate 120-frame diagnostic run enables host draw logging. Screenshots are captured after timing; OpenGL inventory and Vulkan HUD/inventory captures were inspected.

## Boundaries

- Update CPU: controlled sample mutations plus `update_document`, including its layout/paint/font/texture preparation. It is not an isolated paint-only measurement.
- Viewport render CPU and GPU: Godot RenderingServer timestamps. CPU setup is recorded separately in JSON. GPU results can arrive asynchronously, so these are warmed phase distributions, not same-frame sums.
- Draw callback: a separate diagnostic measures host packing and draw submission. Logging perturbs timings; those numbers are not folded into the main frame results.
- Wall frame: process-frame intervals, including scheduling and presentation waits. The tables use the separate timestamp-disabled run. Do not add CPU and GPU medians or subtract p95 values to infer UI cost.
- Background control: hide only Weva, keeping the same native background and tint layers. The reported GPU values include those layers; differences from the control are approximate incremental costs.
- Updating HUD changes the stamina text and meter every frame. Inventory selection changes the selected item and detail panel every frame; this is input stress, not typical click frequency. Idle cases preserve retained drawing.

Timestamp definitions follow the [Godot RenderingServer API](https://docs.godotengine.org/en/stable/classes/class_renderingserver.html#class-renderingserver-method-viewport-get-measured-render-time-cpu). Missing timestamps are marked unavailable rather than treated as zero-cost rendering.

## gl_compatibility

Milliseconds, median / p95. Render CPU is the viewport portion; setup is separate.

| Case | Update CPU | Viewport render CPU | GPU | Wall, timestamps off | Draw calls |
|---|---:|---:|---:|---:|---:|
| background | 0.001 / 0.001 | 0.068 / 0.114 | 0.027 / 0.032 | 0.335 / 0.717 | 3 |
| idle_hud | 0.004 / 0.005 | 0.174 / 0.261 | 0.039 / 0.042 | 0.476 / 0.865 | 63 |
| updating_hud | 0.122 / 0.170 | 0.200 / 0.275 | 0.041 / 0.044 | 0.904 / 1.505 | 63 |
| inventory | 0.004 / 0.005 | 0.319 / 0.466 | 0.057 / 0.060 | 0.706 / 1.219 | 140 |
| inventory_selection | 1.887 / 2.626 | 0.364 / 0.494 | 0.066 / 0.076 | 3.377 / 4.566 | 140 |

Separate draw diagnostic (median on frames that redraw):

| Case | Redrawn / measured frames | Callback CPU | Packing CPU | Submission CPU |
|---|---:|---:|---:|---:|
| background | 0 / 120 | no callback | no callback | no callback |
| idle_hud | 0 / 120 | no callback | no callback | no callback |
| updating_hud | 120 / 120 | 0.175 | 0.007 | 0.165 |
| inventory | 0 / 120 | no callback | no callback | no callback |
| inventory_selection | 120 / 120 | 0.475 | 0.083 | 0.385 |

## mobile

Milliseconds, median / p95. Render CPU is the viewport portion; setup is separate.

| Case | Update CPU | Viewport render CPU | GPU | Wall, timestamps off | Draw calls |
|---|---:|---:|---:|---:|---:|
| background | 0.000 / 0.001 | 0.012 / 0.020 | 0.011 / 0.012 | 0.271 / 0.990 | 3 |
| idle_hud | 0.004 / 0.005 | 0.031 / 0.046 | 0.012 / 0.013 | 0.268 / 0.657 | 63 |
| updating_hud | 0.134 / 0.157 | 0.034 / 0.049 | 0.013 / 0.014 | 0.642 / 0.853 | 63 |
| inventory | 0.004 / 0.005 | 0.051 / 0.076 | 0.021 / 0.022 | 0.280 / 0.583 | 140 |
| inventory_selection | 1.889 / 2.441 | 0.067 / 0.095 | 0.022 / 0.028 | 2.930 / 3.446 | 140 |

Separate draw diagnostic (median on frames that redraw):

| Case | Redrawn / measured frames | Callback CPU | Packing CPU | Submission CPU |
|---|---:|---:|---:|---:|
| background | 0 / 120 | no callback | no callback | no callback |
| idle_hud | 0 / 120 | no callback | no callback | no callback |
| updating_hud | 120 / 120 | 0.233 | 0.006 | 0.223 |
| inventory | 0 / 120 | no callback | no callback | no callback |
| inventory_selection | 120 / 120 | 0.577 | 0.081 | 0.488 |

## What the result supports

GPU rendering is small in this sample on the RTX 5080. Inventory selection is primarily CPU work: about 1.9 ms in the controller/document update before host draw and renderer work. Idle retained UI still incurs GPU draw calls even when its host draw callback does not run. Vulkan and OpenGL differ substantially, so do not mix their measurements.

This establishes a reusable rendering measurement, not Chrome rendering parity or a whole-game frame budget. Busy 3D scenes, weaker hardware, higher resolutions, cold opening/shader compilation, Unity rendering and a matched Chrome paint/raster/composite trace remain unmeasured here.

## Reproduction and evidence

- [Run instructions](../../Tools/performance/README.md#rendered-godot-frames).
- [Machine-readable summary, sample counts and hashes](rendering-20261030.json).
- Raw JSON, engine logs, draw traces and PNGs: `.utmp/render-bench-20261030/final/` (OpenGL) and `vulkan/` (Vulkan). Earlier probes are preserved in the parent directory.
- Six successful rendered runs: timestamped, timestamp-disabled and draw-profiled runs for each renderer. No engine errors or missing assets were reported.
- Headless execution was rejected with exit code 1 and no measurement receipt, preventing fake zero-GPU measurements.
- No C++/C# engine or host implementation changed; no engine rebuild or repeated full gate was required for this benchmark-only addition.
