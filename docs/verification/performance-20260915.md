# Performance baseline — September 15, 2026

All 47 core samples completed the layout, incremental-update and cold-creation
measurements. All 27 Windows runtime workload runs passed their functional
checks. This is a measurement baseline at `1cb34072`, with no paired
before/after comparison or timing-budget claim.

The largest measured cost is complete core document creation: the slowest
sample averages **56.067 ms**, compared with **1.517 ms CPU p95** for the
heaviest retained runtime workload, inventory scrolling. These are distinct
fixtures and measurement scopes, not a direct before/after comparison.

## Native Windows UI

Ryzen 7 9800X3D, RTX 5080, Vulkan Forward Mobile, 1280×720. Three runs each
measure 600 frames after 120 warmups, with a deterministic 1/60 simulation step
and VSync disabled. Godot 4.7.2's script-iterator-patched editor executable runs
private standalone fixtures with the freshly rebuilt Release Weva extension.
Source and dependency fingerprints match the current checkout; the MSVC build
reports no compiler warnings.

CPU time includes the GDScript drive operation, API calls and synchronous core
updates. It excludes later drawing and GPU execution. Mean/p95 columns are
medians of the three per-run statistics; the range exposes their variation.
All values below are milliseconds.

| Workload | CPU mean | CPU p95 | Per-run CPU p95 range | Whole-frame p95 |
|---|---:|---:|---:|---:|
| Empty window | 0.000 | 0.001 | 0.001–0.001 | 4.860 |
| Idle HUD | 0.010 | 0.011 | 0.011–0.011 | 4.831 |
| HUD bars + labels | 0.297 | 0.788 | 0.583–0.812 | 3.825 |
| Unchanged HUD writes | 0.029 | 0.052 | 0.038–0.052 | 4.550 |
| Bound HUD updates | 0.696 | 1.230 | 0.917–1.255 | 2.891 |
| Menu hover | 0.041 | 0.130 | 0.117–0.163 | 4.746 |
| Menu opacity animation | 0.135 | 0.436 | 0.187–0.453 | 4.539 |
| 96-slot inventory scroll | 1.127 | 1.517 | 1.252–1.627 | 2.873 |
| Chat typing | 0.026 | 0.111 | 0.109–0.116 | 4.819 |

HUD label-update frames specifically measure **1.205 ms CPU p95**; actual
typing frames measure **0.185 ms**, and hover-change frames **0.383 ms**.
The all-frame columns also include frames between those 10 Hz changes.
The largest individual API sample across all runs is **2.022 ms**, in inventory
scrolling. These observed maxima do not establish a worst-case bound.

The empty-window whole-frame p95 is **4.860 ms**. Whole-frame values include
presentation and scheduling; they cannot establish GPU cost or be subtracted
across unrelated workloads to claim an improvement. Other desktop/game
processes were active. Our own builds and benchmark phases ran sequentially.

The runner saved 24 PNGs. The final HUD and scrolling-inventory captures were
inspected, and workload checks verify actual updates, bindings, hover and scroll.

## Shared core

The Release GCC benchmark ran in WSL on the same CPU. Each of 47 samples uses
three sweeps of 40 iterations. Layout and incremental numbers below are the
median of each sweep's **best** iteration, not p95 or typical latency.

- Box construction and layout only: slowest sample `layout-stress`, **2.287 ms**.
- Full update after changing the final element's padding: slowest `glass`,
  **3.171 ms**. Its background-only update is **0.046 ms**.
- Background-only updates across the whole corpus: slowest `menu`, **0.835 ms**.

Cold creation includes document construction, parsing and the first update with
paint generation. It uses the built-in font and excludes native host rendering
and host font/asset I/O. The median across sample mean-times is **9.135 ms**.
The five slowest samples are below; both columns are medians across three runs.

| Sample | Cold run mean | Cold run best |
|---|---:|---:|
| hud | 56.067 | 51.247 |
| randhtml | 55.667 | 50.586 |
| glass | 55.595 | 51.972 |
| neon | 46.226 | 41.189 |
| episode-stats | 46.121 | 41.477 |

Cold creation is the largest measured cost; this run does not attribute that
cost to a particular engine subsystem. It does not measure Unity, a full game,
a release export, GPU duration or long-term memory behavior.

## Evidence and reproduction

The [machine-readable receipt](performance-20260915.json) records binary/source
hashes, settings, timing ranges and the slowest core samples. Complete results,
all 47 core rows, raw logs and captures are in `.utmp/performance-20260915/`.
The receipt hashes both complete JSON summaries. No source or scene was changed.

From WSL, use the Release core benchmark:

```sh
export WEVA_BENCH=/root/weva/build-gcc/Tools/weva_bench/weva_bench
bash Tools/layoutbench.sh 3 40
bash Tools/flipbench.sh 3 40
# For each sample, repeat three times to collect cold mean/best statistics:
"$WEVA_BENCH" Tools/oracle/corpus/samples/hud.html Tools/oracle/corpus/samples/hud.css 40 --cold
```

From Windows, with the patched editor and current Release extension:

```powershell
python hosts/godot/run_game_ui_bench.py --godot .utmp/safe-engine71/weva-godot-4.7.2-scriptfix71-windows-x86_64/editor/godot.windows.editor.x86_64.console.exe --library hosts/godot/project/addons/weva/bin/weva_godot.dll --out .utmp/performance-next/godot-native --runs 3 --frames 600 --warmups 120
```

Use a new output directory; the runner preserves earlier evidence. The
[runtime benchmark guide](../RUNTIME_PERFORMANCE.md) defines the workload and
the distinction between CPU work and whole-frame intervals.
