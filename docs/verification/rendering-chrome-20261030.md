# Same-scene Chrome rendering comparison — October 30, 2026

Chrome 152.0.7977.82 versus Weva in Godot 4.7 Mobile/Vulkan, Windows RTX 5080, NVIDIA 617.42. Chrome uses ANGLE/D3D11. Same 1280x720 survival HTML/CSS, images, exported Open Sans SemiBold font bytes and controller states. This is a warm 2D illustrated scene, not a busy 3D game. No engine optimization is included.

Three rounds, 180 measured updates after 120 warmups per case, reversing case order in round two. Both target 60 updates/second; Chrome uses requestAnimationFrame scheduling and Godot uses Engine.max_fps. They were run sequentially. Timing instrumentation is enabled: Chrome tracing and Godot renderer timestamps. These runs diagnose CPU stages; they are not uninstrumented throughput or input-to-photon measurements.

## CPU work

Milliseconds per update. Chrome values are mean CPU work over the marked trace window, divided by mutations. Weva values are mean controller plus document-update CPU. **These columns have different boundaries; do not turn them into an overall speedup ratio.**

| Case | Chrome style/layout | Chrome prepaint/paint/layerize | Chrome raster workers | Weva controller/update | Godot viewport CPU |
|---|---:|---:|---:|---:|---:|
| background | 0.000 | 0.012 | 0.000 | 0.001 | 0.031 |
| idle_hud | 0.000 | 0.011 | 0.000 | 0.010 | 0.064 |
| updating_hud | 0.089 | 0.103 | 0.078 | 0.263 | 0.051 |
| inventory | 0.000 | 0.011 | 0.000 | 0.009 | 0.101 |
| inventory_selection | 0.158 | 0.214 | 0.185 | 2.217 | 0.084 |

Chrome stage accounting clips to the marked window and target renderer process and unions nested/overlapping intervals per thread. Raster workers are summed CPU work, potentially parallel, not wall time. Chrome JavaScript setters, compositor/GPU-process submission and presentation are not included in those stage columns. Weva update includes controller, core layout/paint and font/texture preparation; it excludes the later host draw callback and viewport rendering. Global Godot render setup is separate in the JSON. Do not sum independent percentile values.

Idle Chrome performs no measured layout or raster tasks; its small prepaint/layerize work is retained-compositor bookkeeping under the scheduled callbacks. Godot retains its canvas commands but still renders the viewport. This retained behavior is legitimate, not a reason to force Chrome to repaint.

## GPU and pacing

Chrome produced no usable hardware GPU execution timestamps. GPU-process CPU trace slices are not GPU execution. A Chrome-versus-Weva GPU speed ratio is therefore **unavailable**. Godot GPU timestamps, including background, are below; they have substantial outliers in this capped run.

| Case | Godot GPU median / p95, ms | Godot frame interval mean / p95, ms |
|---|---:|---:|
| background | 0.014 / 0.523 | 16.644 / 30.074 |
| idle_hud | 0.021 / 0.605 | 16.681 / 29.907 |
| updating_hud | 0.021 / 0.516 | 16.678 / 29.757 |
| inventory | 0.030 / 0.588 | 16.684 / 29.945 |
| inventory_selection | 0.028 / 0.582 | 16.647 / 29.629 |

The Godot cap has roughly 30 ms interval tails despite the low median rendering cost. Chrome active-case callback intervals average about 16.58 ms with 20.9 ms p95 on this display; background includes an additional stall. These are different scheduling mechanisms, not isolated rendering latency. The [earlier uncapped receipt](rendering-20261030.md) remains separate evidence, with separate timestamp-disabled and draw-callback passes.

## Fidelity, interpretation and next work

Inspected actual HUD and inventory PNGs from both renderers. Inventory ID rectangles differ by at most 0.047 px; HUD rectangles excluding the crosshair differ by at most 0.032 px. The crosshair reports about 2.51 px different bounds but appears in the same place in both screenshots: query/transform semantics need separate investigation before calling it a visual defect. Font antialiasing and bold appearance differ, so this is not pixel-identical rendering. Both render the real images and text; this is not the previous geometry-only fixture.

Inventory selection remains the primary CPU optimization target: Weva controller/update alone is much more expensive than Chrome’s measured style/layout/paint recording stages. Additional host submission work exists on both sides; the data does not establish a full-pipeline ratio or “same or better always.” Next, profile the selection update and host draw submission, and obtain matching GPU hardware captures (for example a platform GPU profiler covering both applications) before claiming GPU parity.

## Reproduction and validation

See [runner instructions](../../Tools/performance/README.md#same-scene-chrome-rendering-diagnostic). Raw traces, full Godot samples and screenshots are retained under `.utmp/chrome-render-20261030/`; [the tracked receipt](rendering-chrome-20261030.json) records hashes, GPU configuration, per-round Chrome results and pooled statistics. The exported font is a local runtime artifact, not redistributed in Git.

Validation: live state/font export, 15 Chrome trace phases, 15 real Godot rendered phases, inspected captures, Node syntax check and three trace-accounting unit tests (nesting/overlap, process/window filtering and parallel worker accounting, missing-window rejection). Final exporter/runner smoke is repeated after error-handling cleanup. No core, ABI or host renderer implementation changes; previous engine gate counts are unchanged. A hidden crafting-material label order was corrected after the measured run; crafting is hidden in all measured cases.
