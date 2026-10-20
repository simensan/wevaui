# In-game UI performance

The [October runtime geometry optimization](verification/runtime-geometry-20261011.md)
reduces paired Godot inventory scrolling API CPU p95 from 1.245 to 1.097 ms
(11.9%) and core-only scrolling mean from 0.636 to 0.540 ms (15.1%). It removes
repeated rounded-geometry work without adding workers; 24 paired screenshots
are identical. Active and bound HUD timing is effectively unchanged in the
final repeat. These remain standalone fixture measurements, not whole-game FPS.

The [October 11 performance audit](verification/performance-audit-20261011.md)
repeats all 27 native workload runs: active HUD CPU p95 is 0.627 ms, bound HUD
0.670 ms and inventory scrolling 1.219 ms. Selected cold-core creation means
remain 18.850–55.056 ms. It adds CPU/allocation profiles and ranks the next
investigations; these are unchanged-binary measurements, not speedup claims.
The [subsequent blur optimization](verification/cold-blur-20261011.md) reduces
the paired HUD cold-core median run mean from 51.952 to 50.653 ms with exact
blur bytes. It does not eliminate the screen-opening hitch or qualify host FPS.

The [parallel raster follow-up](verification/cold-raster-20261011.md) reduces
paired GCC cold HUD creation from 51.755 to 33.368 ms (**35.5%**), with identical
pixels. Glass improves 18.2% and menu 25.0% in the same experiment. The Windows
core DLL HUD cross-check improves 31.3%, with 12.0% more process CPU time. This uses
additional CPU cores for expensive backgrounds; it does not establish a 30%
improvement for all UI, aggregate CPU cost or normal in-game frame time.

The September 15 [performance baseline](verification/performance-20260915.md)
measures the September 15 core and a freshly rebuilt Windows Godot extension. All
47 core samples and 27 native workload runs complete. Median run CPU p95 is
0.788 ms for active HUD updates, 1.230 ms for bound HUD updates and 1.517 ms for
96-slot inventory scrolling. Complete core document creation reaches 56.067 ms
mean in the slowest sample. This is an unpaired standalone measurement, with
the exact scope, variation and machine conditions recorded in the report.

The [same-binary repeat](verification/performance-20260915-pass2.md) also
completes 47 core samples and 27 native workload runs; all 24 corresponding
captures match the first pass. Runtime CPU p95 measures 0.547 ms for the active
HUD, 0.748 ms for the bound HUD and 1.251 ms for inventory scrolling. The
slowest cold-creation mean is 51.857 ms. These are repeated measurements of
unchanged binaries, not a code speedup; both passes remain recorded.

The September 25 [technical audit](verification/tech-audit-20260925.md) cut
core cold-load instructions by 8% on hud and randhtml and by 28% on a
process's first document, with byte-identical renders. It also removed a
depth-cubic cost from the incremental layout index: a 600-deep page dropped
from 2.2 s to 52 ms. Those figures are Linux core measurements. They are not
a new Windows or host baseline. The follow-up spreads cold paint across up to
four threads with byte-identical output: hud 139.8 → 55.3 ms, and
episode-stats 107.4 → 37.6 ms, on a 4-core machine.

The [screen-opening pass](verification/screen-open-20260925.md) runs a
paint pass's textures side by side and draws wide blurs on a coarser grid,
within 4 of 255 levels. First opens: glass 86.6 → 42.3 ms, neon 53.3 → 20.9 ms,
hud 54.9 → 42.2 ms. Rasterized textures are also kept across documents, so a
screen created again skips most of its paint: glass reopens in 11.3 ms,
neon in 6.6 ms and hud in 8.3 ms. That is Unity's case when a disabled
`WevaDocument` is enabled again. These are the same Linux core measurements
and are not a Windows or host baseline.

## What the measurements cover

The native runs use standalone Godot fixtures on Windows, a Ryzen 7 9800X3D
and RTX 5080 at 1280×720. CPU timings exclude later drawing and GPU execution.
The core-only cold tests exclude host font/asset loading. Background desktop
activity varied between runs, so lower repeat timings are not a code speedup.

These runs do not qualify Unity performance, full-game FPS, release exports,
lower-end hardware or memory lifecycle. Measure your own game's screen opening
and gameplay; prepare and reuse expensive screens during loading where useful.

## Reproduce or investigate

- [Baseline report](verification/performance-20260915.md): commands, machine,
  measurement settings and raw-output locations.
- [Repeat report](verification/performance-20260915-pass2.md): same-binary
  comparison and variability.
- [Core benchmark](../Tools/weva_bench/README.md): workload and allocation scopes.
- [Frontier Camp checks](../examples/frontier_camp/README.md#repeat-the-checks):
  sample-specific desktop, 3D and lifecycle profiles.

[Earlier runtime measurements](RUNTIME_PERFORMANCE_HISTORY.md) and the
[development performance log](PERFORMANCE.md) remain available for investigation.
Their results apply to their recorded builds.
