# Frontier Camp performance verification

Measured 2026-09-07 using Windows **release exports**, Godot 4.7.2,
Ryzen 7 9800X3D and RTX 5080, at 1280 × 720. This report compares the
previous runtime63 with the installed runtime64 using the actual sample.

**Ordinary bindings now use incremental updates.** Continuous two-meter
updates take 0.33–0.44 ms p95; at the sample’s 10 Hz cadence, changing-frame
p95 is 0.52–0.58 ms and average update cost is 0.059–0.067 ms per frame.
Idle CPU document ticks remain about 0.006–0.007 ms p95.

## Matched release-build measurements

Three runs per runtime and renderer, 120 warmup and 600 measured frames per
case. Runtime order alternates between pairs; renderer and workload order
also alternate. Values are medians of the three per-run p95s, in milliseconds.
Except idle, the table uses **changing frames only**. No concurrent builds,
other tests or profiling logs run during the measurements.

| Case | Before OpenGL | After OpenGL | Before Vulkan | After Vulkan |
| --- | ---: | ---: | ---: | ---: |
| Idle document tick | 0.007 | 0.007 | 0.007 | 0.006 |
| Clock label, continuous | 1.563 | 0.427 | 1.628 | 0.432 |
| Two meters + labels, continuous | 1.451 | 0.331 | 1.459 | 0.440 |
| Two meters + labels, simulated 10 Hz | 1.692 | 0.515 | 1.732 | 0.580 |
| Typing in the bound name field | 2.897 | 1.207 | 2.890 | 1.208 |
| Bound volume slider | 2.064 | 0.528 | 2.193 | 0.524 |
| Open/close settings | 3.769 | 2.816 | 3.807 | 2.640 |
| Inventory reverse-order click | 2.061 | 2.001 | 2.017 | 2.009 |
| Pointer hover change | 0.224 | 0.209 | 0.203 | 0.203 |
| Redundant signal, unchanged data | 0.181 | 0.160 | 0.185 | 0.163 |
| Clock, simulated 1 Hz (10 events/run) | 2.474 | 1.763 | 2.612 | 1.608 |

The CPU timer includes game actions, native Godot viewport mouse/keyboard
input where applicable, binding refresh, layout/paint update and texture
preparation. It excludes later canvas submission and GPU work. This pass
flushes the helper’s deferred binding work synchronously and explicitly ticks
once. The separate normal-schedule pass below leaves `_process` and deferred
callbacks enabled. Controls, typed model values and displayed content are
asserted after each workload.

Continuous meter p95 improves by **77% on OpenGL and 70% on Vulkan**.
Typing improves by about 58%; slider edits by 74–76%; settings toggles by
25–31%. Inventory sorting remains about 2 ms because reordered rows still
rebuild layout. Settings transitions remain about 2.6–2.8 ms. Those are
interaction costs, not an idle per-frame charge.

The low-frequency clock still has a 1.6–1.8 ms p95 tail in this short run;
it has only ten measured changes per run. Its per-run p95 ranges from
0.504 to 2.153 ms across the two renderers. A separate diagnostic profile
confirms local layout updates, with occasional paint-cache misses while new
text is prepared. The 600-event continuous cases are stronger evidence for
steady update cost; this is not an all-events-under-1-ms claim.

## What changed

- Binding substitutions record actual DOM mutations. Text and image sources
  queue content inputs; attributes restyle their selector scope. Repeat
  structure keeps the full rebuild and handle cleanup it requires.
- Caret, selection and composition updates invalidate the affected controls,
  including both controls on focus changes, instead of the whole paint cache.
- Dialog box changes retain their cascade origin. Dialog/focus writes can
  share the final layout, and focus reveal waits for updated geometry.
- Parsed Godot binding paths are cached with bounded storage. Live Dictionary
  values are still read each refresh. Small forms find model controls in one
  scan; larger forms use the dynamic fallback.
- Removed rows release their computed styles after the previous box/paint pass
  is replaced. Reordering no longer accumulates dead styles indefinitely.
- A retained-grid margin-collapse bug found by the new comparisons is fixed.
  The HUD name has an explicit 170px slot, preserving its previous pixels
  while avoiding intrinsic-size changes around the name on each edit.

## Pixel and correctness evidence

All **84 before/after workload screenshots are byte-identical**. Each runtime
also has 12 byte-identical bound/direct screenshot pairs. The sample still
uses the same `bind_state`, change signal and `data-model` authoring API.

Final checks: 9/9 core CTest suites; 11/11 ASan/UBSan suites, including positive
controls and the 47-sample mutation corpus; **8,436 Godot host checks**; the
packaged addon’s 17-check example; and Frontier Camp’s 89 checks headlessly,
on OpenGL and on Vulkan. Screenshot runs add six checks. A relocated Windows
release export also passes, with pixels identical to the project.

New core comparisons cover binding text/style/boolean/class updates, list
changes, removal/reload before update, selector dependencies, multiple layout
modes, blink, selection, IME, undo/redo and focus. They compare draws against
forced full rebuilds and assert retained draws on local updates. Godot checks
also cover more models than the stack buffer and more parsed paths than the
bounded cache, including shared-data mutation and reload.

## Renderer and normal scheduling

For idle UI, median per-run mean viewport CPU submission rises from
0.097 to 0.176 ms on OpenGL and 0.016 to 0.029 ms on Vulkan when UI is enabled.
GPU time rises from 0.038 to 0.051 ms and 0.017 to 0.020 ms, respectively.
The disabled baseline hides UI and disables its processing while retaining
its resources. These are rendering deltas, not memory savings.

The visible idle UI contains 116 DOM elements, 169 Weva draw commands and
2,932 triangles. Godot reports 44 canvas draw calls including the backdrop
scene, versus two with UI disabled. Commands are not hardware draw calls.

Three additional runs per renderer keep Godot’s native `_process` and the
helper’s deferred refresh schedule. Median of per-run mean complete-frame
intervals, in milliseconds:

| Case | OpenGL | Vulkan |
| --- | ---: | ---: |
| UI disabled | 2.203 | 2.933 |
| Idle UI | 2.284 | 2.937 |
| 10 Hz vitals | 2.300 | 2.878 |
| Typing | 2.501 | 2.953 |
| Slider | 2.382 | 2.877 |
| Settings toggles | 2.786 | 2.973 |

Windows offscreen presentation and scheduling are noisy; some intervals
decrease despite added work. Do not turn these differences into a promised
FPS impact for a 3D game. VSync is disabled, presentation is uncapped and
simulation steps are fixed at 1/60 second. No busy 3D scene, lower-end device,
1080p/4K GPU load or cold startup is covered. Godot’s static-memory monitor
is unavailable in this release template; receipts report null. No zero-
allocation claim is made.

## Reproduce

Install the matching addon and Godot export templates, then run from the
repository root:

```sh
python godot-port/hosts/godot/run_frontier_perf.py --godot /path/to/godot \
  --output /new/results --runs 3 --frames 600 --warmups 120 --capture

python godot-port/hosts/godot/run_frontier_perf.py --godot /path/to/godot \
  --output /new/native-schedule --automatic --runs 3 \
  --cases ui_disabled,idle,vitals_10hz,settings_typing,settings_slider,settings_toggle

python godot-port/hosts/godot/run_frontier_perf.py --godot /path/to/godot \
  --output /new/direct-comparison --runs 3 --capture \
  --cases clock_update,clock_direct,vitals_update,vitals_direct
```

Use `--project` to target an isolated sample and `--run-offset` to continue
alternating order across paired invocations. `--executable` reuses an already
exported matching project; the caller must keep its scripts/assets unchanged.
The runner verifies the native library, captures source/build fingerprints,
records all runs and fails on workload assertions or bound/direct pixel diffs.

Local evidence: `.utmp/runtime64/paired/comparison.json` and the adjacent
per-run JSON/logs/PNGs; `.utmp/runtime64/automatic/summary.json`; core and
sanitizer logs; `host-checks/verification.json`; `verified/verification.json`;
and `installed.json`. `paired_perf.py` records the alternating runtime order.
The earlier diagnostic report’s receipts remain under `.utmp/frontier-perf/`.

Native library SHA-256:

- Before: `ec183f695f331bc4de585bc7b829b535110ba83eccaf4c8dd17094a93beaaa20`
- After: `44b046f2768074844623f7f4b34df93638bbd55504c965e6260ef5a8e63f549f`
