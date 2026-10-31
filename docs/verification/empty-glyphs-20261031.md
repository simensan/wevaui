# Empty glyph reuse — 2026-10-31

The shared glyph atlas now remembers successful raster requests that contain
no ink. In the diagnostic inventory workload, this removes an average **36.79
raster calls and 36.79 glyph-metrics calls per update**. Six provisional paired
runs measure **3.8% lower inventory paint CPU**; complete CPU is effectively
flat at 0.8% lower. This is not a Chrome-parity claim.

## Finding and change

The preceding investigation suggested inspecting traversal and full paint
resets. Instrumentation found **zero full resets** across 200 inventory
updates. Each update reused about 36 subtrees and 261 draws; replay took about
10 microseconds in that diagnostic run. Further attribution identified text
preparation and drawing as useful targets.

Visible glyphs already had atlas slots, but successful empty glyphs such as
spaces returned without recording the result. Preparation and drawing therefore
asked the host to rasterize them again, then requested metrics that were unused.
The normal warm inventory workload made 36.79 calls to each callback per update.
With the change, both counts are zero. Shape calls remain 93.24 per update.
These are diagnostic operation counts; instrumented durations are not used as
an optimization percentage because background load differed between runs.

The atlas owns a fixed 64-entry direct-mapped cache keyed by face, glyph and
the same quantized 1/64-pixel size used for visible slots. An entry is recorded
only after rasterization succeeds with an empty bitmap. Failed requests remain
retryable. Collisions cause another rasterization, never an incorrect hit.
Empty results use no texture space and request no metrics: shaping supplies
their advances. Font resets clear the cache; renderer resets preserve it.
No per-frame allocation, ABI change or host-specific implementation is added.

## Provisional workload comparison

Six alternating Agentic baseline/candidate/Chrome pairs use the existing real-font
HUD and twenty-field inventory fixtures, 20 warmups and 200 measured updates
at requested 60 Hz. No own builds overlapped timings. Other workspace activity
continued as requested, so every timing here is **provisional**. Initial/final
sampled native geometry agrees across each batch's six runs.

Median of run means, milliseconds per inventory update:

| Stage | Baseline | Empty-glyph cache | Chrome 152 |
|---|---:|---:|---:|
| Style/layout | 0.1670 | 0.1690 | 0.1563 |
| Paint recording | 0.2220 | 0.2130 | 0.2080 |
| Complete native CPU | 0.9575 | 0.9540 | Not equivalent |

The median within-pair changes are -3.8% paint, -0.8% complete CPU and +2.2%
unchanged style/layout. Batch results differ: paired paint is +5.2% in the
first batch and -4.8% in the second. These numbers support only a modest,
provisional paint improvement; the eliminated host callbacks are the direct
evidence that repeated work was removed.

HUD paint medians are unchanged at 0.055 ms; its paired paint change is +0.9%
and complete CPU +2.5%, without a demonstrated HUD benefit. Chrome inventory
raster-worker CPU is separately 0.0855 ms. Native complete CPU includes
controller/host work and has no equivalent Chrome column. End-to-end GPU
parity, quieter-machine repeatability and lower-end hardware remain unverified.
These timing percentages are not Godot or Unity frame-time measurements.

The [receipt](empty-glyphs-20261031.json) preserves both full batches
(`comparison-v37` and `comparison-v38`), process observations, diagnostic
counts, source hashes, installed host metadata and validation artifacts.

## Coverage and validation

The atlas regression covers warm empty requests, fractional-size identity,
different faces/glyphs, retryable raster failures, successful ink after a retry,
font reset and more distinct empty glyphs than the bounded cache holds.
The incremental glyph-preparation regression still compares exact draw output
against full recomputation across text, form, font, viewport, renderer and
transform changes. Its old expectation of repeated space rasterization is
replaced with assertions that successful empty results survive unchanged font
inputs and are requested again for new sizes or a reset backend.

The qualified full release gate passes:

- All 17 GCC suites and 19 Clang sanitizer suites, with 808,781 GCC main checks
  and 808,775 sanitized checks; compiler builds report no warnings.
- All 341 tracked Chrome captures at the 1.5px ceiling, and 67 behavior checks
  plus the existing known IME failure.
- Godot font/Unicode coverage (19,833 checks), backend, scene, input, lifecycle,
  debug/release/embedded export and render checks.
- Both Windows hosts rebuilt. Unity full EditMode passes 255 tests with the
  same two environment-gated inconclusive results; the Windows Mono player
  passes all seven checks. IL2CPP was not rerun in this pass.
- All 16 Unity render captures and the Godot reference render are pixel-identical
  to the accepted baseline. Representative Unity and Godot captures were inspected.

The final Agentic executable's code/data sections match the measured candidate,
and the measured source hashes match the final runtime/tests. The temporary
traversal/paint instrumentation is removed. Sources, failed experimental test
evidence and logs remain under `.utmp/paint-traversal-20261031/`.
