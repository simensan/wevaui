# Runtime performance audit — October 11, 2026

Cold screen creation is the highest measured risk. Continuous HUD work is
sub-millisecond in these fixtures; inventory scrolling is the largest steady
workload. These measurements repeat unchanged runtime binaries, so differences
from September are not code speedups.

## Findings, in priority order

1. **Cold creation can hitch a frame.** Across three runs of 40 fresh core
   documents, median run means are **55.056 ms for HUD**, **53.254 ms for glass**
   and **18.850 ms for menu**. This excludes host font/image I/O, rendering and
   destruction. HUD requests 20,920 C++ allocations totaling 46,604,942 bytes
   during the final construction pass; this is allocation traffic, not retained
   memory. A separate 120-iteration CPU sample identifies background
   rasterization and blur among the leading sampled sites. Prioritize preparing
   and reusing screens, then investigate caching/reuse of cold paint resources.
   The sample lists individual instruction sites, not aggregate subsystem shares;
   it does not prove how much of the total any proposed change will save.

2. **Scrolling is the best steady-state optimization target.** A continuously
   scrolling 96-slot inventory costs **1.219 ms API CPU p95**; its corresponding
   core-update p95 is **1.199 ms**. This points investigation toward core update
   and paint work rather than GDScript dispatch. `weva_element_set_scroll` marks
   paint invalidation; profile that path before changing layout or bindings.
   The measured API cost alone uses about 7.3% of a 60 Hz frame budget, excluding
   later rendering and GPU work. There is no whole-game budget qualification.

3. **Some previous numbers are not comparable.** Direct HUD updates change bars
   every simulation tick but labels every sixth tick; bound HUD updates change
   both every tick. Their difference cannot quantify binding overhead. Chat
   typing also occurs every sixth tick: its all-frame p95 is 0.090 ms, while
   actual typing-event frames reach **0.116 ms p95**. Keep event-frame statistics
   visible instead of relying only on the mixed idle/active distribution.

4. **Important coverage remains missing.** The current nine-workload fixture
   does not test settings toggles, inventory reordering, cold host construction
   or prepared menu reuse. Older Frontier results remain historical. Unity's
   passing player smoke tests establish functionality, not performance. GPU
   duration, a busy game scene, resolution scaling and lower-end machines still
   need separate measurements.

5. **Benchmark process cleanup needs hardening.** The successful Unity player
   smoke run left its `PlayerWithTests.exe` running and consuming CPU. Its exact
   executable path was verified under the previous audit output and that process
   was stopped before this benchmark. The runner should eventually confirm
   player exit and clean up only the player it launched, including on timeout.
   No timing collected with that leftover test process is accepted here.

The allocation profile also shows CSS shorthand/selector compilation allocation
sites. Allocation count alone does not establish their share of elapsed time;
do not prioritize parser micro-optimizations ahead of the cold paint investigation.

## Measured runtime

Windows, Ryzen 7 9800X3D / RTX 5080, patched Godot 4.7.2 editor executable,
Vulkan Mobile, 1280×720. Three sequential runs, 120 warmups and 600 measured
frames per workload: **27/27 workload runs passed**. No builds or other audit
benchmarks overlapped. Other user desktop activity was not controlled.

Values are the median of three per-run API CPU p95 values in milliseconds.
API time includes the drive calls and synchronous updates, but excludes later
Godot drawing and GPU execution. Simulation advances at 1/60 s with vsync off;
this is a throughput fixture, not paced 60 Hz gameplay.

| Workload | CPU p95 | Per-run p95 range |
|---|---:|---:|
| Empty window | 0.001 | 0.001–0.001 |
| Idle HUD | 0.009 | 0.009–0.010 |
| Active HUD | 0.627 | 0.577–0.630 |
| Unchanged HUD writes | 0.026 | 0.026–0.027 |
| Bound HUD updates | 0.670 | 0.664–0.723 |
| Menu hover | 0.124 | 0.122–0.126 |
| Menu opacity animation | 0.145 | 0.144–0.145 |
| Inventory scrolling | 1.219 | 1.145–1.248 |
| Chat, including frames between key events | 0.090 | 0.089–0.090 |

The empty-window whole-frame p95 is 0.353 ms; inventory's is 2.282 ms. These
intervals include scheduling/presentation, are not GPU durations, and cannot be
subtracted across runs to isolate UI cost. All 24 saved PNGs are byte-identical
to the September second pass. The latest HUD and inventory captures were inspected.

## Evidence and next work

The [receipt](performance-audit-20261011.json) records hashes, settings, timing
statistics and screenshot comparisons. Raw logs, profiles, screenshots and
complete runtime results remain in `.utmp/performance-audit-20261011/`.
Use the [September reproduction commands](performance-20260915.md#evidence-and-reproduction)
with a fresh output directory. This audit ran cold tests only for `hud`, `menu`
and `glass`, not the entire 47-sample corpus. Profiling runs are separate from
the reported uninstrumented latency runs.

The runtime DLL and fixture hashes match the September repeat. Since that DLL's
recorded source revision, the only `libweva`/Godot runtime-source-tree difference
is removal of a duplicate CMake source entry. No engine change was made here.

Next implementation priorities: measure host cold-open versus prepared reuse;
profile inventory scroll paint; add equivalent update frequencies before
comparing binding paths; then establish Unity player CPU/allocation timings.
Keep settings-toggle and inventory-reorder measurements separate from scroll.
