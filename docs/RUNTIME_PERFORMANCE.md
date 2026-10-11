# In-game UI performance

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
