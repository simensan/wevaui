# Performance repeat — September 15, 2026

The second pass completes all **47 core samples** and **27 native workload
runs**. It repeats the [first pass](performance-20260915.md) with identical
core, Godot and extension binaries, source inputs and measurement settings.
The working revision is `79c266f0`; intervening commits only changed
documentation. No engine, host or scene source was changed for this run.

## Runtime results

Windows, Ryzen 7 9800X3D, RTX 5080, Vulkan Forward Mobile, 1280×720. Each pass
contains three runs of 600 measured frames after 120 warmups. The tables show
the median of the three per-run CPU p95s, in milliseconds.

| Workload | First pass | Second pass | Second-pass run range |
|---|---:|---:|---:|
| Idle HUD | 0.011 | 0.009 | 0.009–0.010 |
| Active HUD | 0.788 | 0.547 | 0.546–0.604 |
| Unchanged HUD writes | 0.052 | 0.028 | 0.027–0.032 |
| Bound HUD updates | 1.230 | 0.748 | 0.655–0.776 |
| Menu hover | 0.130 | 0.129 | 0.128–0.129 |
| Menu opacity animation | 0.436 | 0.181 | 0.147–0.186 |
| Inventory scrolling | 1.517 | 1.251 | 1.247–1.289 |
| Chat typing | 0.111 | 0.090 | 0.089–0.092 |

The CPU numbers measured lower in most workloads on the repeat. Because the
binaries are unchanged, this is run-to-run variation, not a code speedup.
Other desktop/game processes remained active; our benchmark phases ran
sequentially, with no builds or tests overlapping measurements.

The empty-window whole-frame p95 changed from **4.860 ms** in the first pass
to **0.516 ms** in this pass, showing substantial background timing variation.
Whole-frame intervals include scheduling and presentation and are recorded
separately in the JSON. They do not establish GPU duration or full-game FPS.
All workload checks passed. 24/24 corresponding saved PNGs are
byte-identical between passes; the new HUD and scrolling-inventory captures
were also inspected.

## Shared core

Both passes use three sweeps of 40 iterations across the same 47 samples in
WSL. Layout and incremental figures are medians of each sweep's best time;
they are not p95. Cold figures below are medians of each run's mean time.

- Slowest box construction/layout: `layout-stress`, **2.278 ms**
  (first pass 2.287 ms).
- Slowest padding-change full update: `glass`, **3.392 ms**
  (first pass 3.171 ms).
- Slowest background-change update: `menu`, **0.891 ms**
  (first pass 0.835 ms).

| Slowest cold samples in this pass | First mean | Second mean |
|---|---:|---:|
| glass | 55.595 | 51.857 |
| hud | 56.067 | 50.535 |
| randhtml | 55.667 | 50.533 |
| neon | 46.226 | 44.124 |
| episode-stats | 46.121 | 42.518 |

Cold creation remains the largest measured cost, reaching **51.857 ms**
mean in `glass`. It covers core document construction, parsing and first
update/paint generation with the built-in font; host rendering and host
font/asset I/O are excluded. This run does not attribute that cost to a specific
subsystem or qualify Unity, a full game, release exports or memory lifecycle.

## Evidence

The [second-pass receipt](performance-20260915-pass2.json) includes settings,
hashes, complete runtime statistics, top core samples and the comparison.
All 47 core rows, raw logs, captures and full comparisons are preserved under
`.utmp/performance-20260915-pass2/`; the original output remains untouched.
Reproduce with the commands in the [first report](performance-20260915.md),
using a new output directory. Functional checks pass; this fixture does not
apply a performance-budget threshold.
