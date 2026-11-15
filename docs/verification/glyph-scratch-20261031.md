# Glyph preparation buffer reuse — 2026-10-31

Glyph preparation now shares one temporary shaping-output buffer across the
pass. A 100-label regression measures **1,200 shaping allocations before the
change and 606 after**, including the unchanged drawing phase. The six paired
runtime comparisons do **not** establish a frame-time improvement. This pass
retains a small allocation reduction, not a speedup or Chrome-parity claim.

## Change and allocation evidence

The host shaper cache already avoids repeating shaping callbacks, but copying
its result into a fresh vector still allocates. Preparation previously created
that vector separately for each text run, form value and popup row. It now
clears and reuses a pass-local vector. Each run is consumed before the next
one replaces it. Storage is freed before recursive painting begins; no text,
font identity or output buffer is retained between frames or documents.

The allocation regression counts actual allocations inside shaping for a
100-label screen. The previous runtime uses 600 allocations during preparation
and 600 during drawing. The new runtime uses six during preparation and 600
during drawing: **594 fewer allocations**, or 49.5% of this test's shaping
allocations. These are deterministic StubFont test counts, not a claim that
the real-font inventory has the same allocation total or a 49.5% CPU saving.

The new regression was linked against the accepted pre-change sanitizer
library: it fails only the new 606-allocation ceiling, measuring 1,200. The
candidate passes at 606. Both produce the existing geometry hash
`e45be54e6504f2cd`. The geometry suite grows from 320 to 322 checks. The failed
baseline probe is preserved as positive evidence that the budget detects the
unnecessary allocation behavior.

## Provisional runtime comparison

Six alternating host-engine baseline/candidate/Chrome pairs use real fonts, the
existing HUD and twenty-field inventory fixtures, 20 warmups and 200 measured
updates at requested 60 Hz. Own builds did not overlap timings. Other workspace
activity continued as requested, so all timings are **provisional**. Each
batch's initial/final sampled native geometry agrees across its six runs.

Median of run means, milliseconds per inventory update:

| Stage | Baseline | Buffer reuse | Chrome 152 |
|---|---:|---:|---:|
| Style/layout | 0.1605 | 0.1700 | 0.1612 |
| Paint recording | 0.1985 | 0.2065 | 0.2008 |
| Complete native CPU | 0.8000 | 0.8435 | Not equivalent |

The median within-pair changes are +1.0% paint, +2.6% complete CPU and +3.7%
unchanged style/layout. The first batch measures -4.9% paint and -4.5% complete
CPU; the second measures +4.9% and +10.0%. HUD paired paint is +11.2%, while
unchanged HUD style/layout is +11.1%; complete CPU is +10.0%. This variation
does not isolate an effect of buffer reuse on runtime speed. No timing outliers
or unfavorable pairs are excluded.

Chrome inventory raster-worker CPU is separately 0.0850 ms. Native complete
CPU includes controller/host work and has no equivalent Chrome column.
These timings do not measure Godot/Unity frame time, isolated GPU time or
lower-end hardware. They must not be combined with percentages from earlier
batches. The [receipt](glyph-scratch-20261031.json) preserves both full batches
(`comparison-v39` and `comparison-v40`) and their process observations.

## Validation

The first full gate passed every stage except the Godot live-reload scene,
which missed the expected CSS-edit notification and produced three assertion
failures. An isolated rerun passed all 23 checks without a source change.
The failed full-gate log and successful isolated rerun are preserved. The
failure is intermittent; its root cause is not established, and this paint
change is not presented as a fix for live reload.

The repeated qualified full gate passes, including all 23 live-reload checks:

- All 17 GCC and 19 Clang sanitizer suites: 808,781 GCC main checks and
  808,775 sanitized checks; the geometry suite has 322 checks. Final compiler
  builds report no warnings.
- All 341 tracked Chrome captures at the 1.5px ceiling, plus 67 behavior
  checks and the existing known IME failure.
- Godot backend, font/Unicode (19,833 checks), input, lifecycle, scene and
  debug/release/embedded export checks.
- Both Windows hosts rebuilt. Unity full EditMode passes 255 tests with the
  same two environment-gated inconclusive results; the Windows Mono player
  passes all seven checks. IL2CPP was not rerun in this pass.
- All 16 Unity player captures and the Godot reference render match the
  accepted baseline pixel for pixel. Representative captures were inspected.

The final host-engine executable's code/data sections match the measured candidate;
the final runtime/test sources match the measurement manifests. Validation
results and installed host metadata are recorded in the receipt.
The internal preparation helper changes no ABI or author-facing API. The
existing exact incremental/full draw comparisons and Chrome/host gates cover
text, controls, popup rows, transforms and fonts. Temporary files, the initial
compile failure and the deliberately failing baseline allocation probe remain
under `.utmp/glyph-scratch-20261031/`.
