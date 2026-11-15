# Glyph preparation and reuse audit — October 30, 2026

The next measured cold bottleneck is font work, not atlas copying or redundant
hint-cache setup. Five instrumented fresh-process renders of the prepared
1280x720 survival inventory each rasterize 252 distinct face/glyph/size keys
across twelve pixel sizes. Median combined scaler/raster time is **7.528 ms**,
within a median **8.46 ms** core glyph-preparation bucket. These are nested
elapsed intervals; do not add them to the glyph-preparation total.

The diagnostic records only cache misses and buffers at most 4096 samples per
font library. It reports omissions and prints at teardown, outside paint timing.
The timing/storage is compiled out without the host engine's `diagnostics` feature.
Enable `WEVA_FONT_LOG=1` alongside the existing `WEVA_PAINT_LOG=1` for a
diagnostic `render_page` or `weva_cost` run. Instrumentation still has a cost;
this receipt makes no production speedup claim from these timings.

An earlier immediate-print probe stalled for 14.926 ms within one glyph render.
Its I/O also inflated the whole paint pass. It is preserved separately as
exploratory evidence. Elapsed timing cannot determine whether a long interval
is raster work, allocation/page faults or OS preemption. Buffered total paint
times still range from 14.52 to 28.01 ms; cold tails remain unresolved.

Local Swash 0.2.10 source uses eight hint-instance cache entries. In a
representative trace, twelve initial sizes require setup and only one older
size incurs another expensive setup. Cache thrashing is therefore a small
part of this case; increasing cache capacity would not address most of its cost.
Text hinting/coverage and the host-owned font contract remain unchanged.

## Retained opening in the actual runtime

Three fresh threaded runtime processes, host-engine `88e0e7f4b`, unchanged Weva core
last changed at `faf04f31`, official optimized SDK. Each starts the synthetic
240-cube scene without UI, opens inventory once, hides the same overlay, then
opens it again. Each phase retains 6,000 CPU-only frames; the exact injected
input tick is checked inside the profile. Hardware: Windows, RTX 5080, Vulkan
617.42, 1280x720, no VSync, off-desktop window.

| Opening-frame statistic, median of 3 processes | First opening | Retained reopening |
|---|---:|---:|
| Main CPU | 19.563 ms | 0.530 ms |
| Main UI CPU | 18.085 ms | 0.262 ms |
| Worker UI CPU | 1.332 ms | 0.133 ms |
| Following frame interval | 19.732 ms | 0.572 ms |

Main/worker spans overlap. This demonstrates retaining an already-painted
document and its font/texture resources, not a 97% improvement to first-time
rasterization. The first process takes 37.130 ms cold; that sample is retained.
All three cold/reopened RGB capture pairs match exactly; the retained capture
was inspected. All twelve report/frame hashes match. Total retained frames:
36,000. These are same-content reopenings, not a changed-data reopening bound.

For a loading-phase workflow, let the UI complete its first update/paint before
hiding it, then retain the document and toggle visibility instead of repeatedly
loading/destroying it. Loading and immediately hiding does **not** prewarm
layout/glyphs, because hidden updates are deferred. New glyphs, sizes or changed
layout can still add work when shown. This is a verified reuse workflow; an
edit-time font bake or hidden prewarm API has not been implemented.

## Verification and evidence

- Diagnostic `render_page` build and all five renders pass. Eight focused font
  tests pass with one existing ignored test. No runtime/core behavior changed.
- Unity and Godot retain their host-owned font backends; this diagnostic does
  not imply comparable raster timings or introduce a shared ABI change.
- The host engine's managed default checkout remains unchanged; the runtime SDK and
  leased audit branch are explicit in the evidence.
- [Machine-readable receipt](glyph-preparation-20261030.json) retains each run,
  runtime binary identities, artifact paths and font log hashes. Raw evidence:
  `.utmp/render-audit-20261030/font-buffered-*`, `font-profile-*`,
  `font-diagnostic-*`, `cold-vs-retained/` and `run-retained.py`.

Next useful optimization work is reducing first-use glyph preparation while
preserving font output, or preparing it before interactive gameplay. Simple
markup precompilation and larger hint caches leave most of this measured cost.
Matched Chrome rendering, other-host hidden-update policies and longer memory
tests remain open; this does not change release readiness.
