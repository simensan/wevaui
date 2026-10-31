# Border geometry investigation — 2026-10-31

This pass retains **144 additional geometry regression checks**, but no runtime
optimization. The accepted engine remains `d9c4db38`. Three border experiments
were reverted because their measurements did not establish a useful inventory
runtime gain. There is no new Chrome-parity or speedup claim.

## Experiments

The earlier paint profile attributed about 9 microseconds per inventory update
to border tessellation. Two prototypes reused normalized edge directions while
walking outlines, including runs of coincident corner points. A cursor version
was slower; a callback loop with direct vertex writes was approximately flat.
Both preserved the existing geometry hash and passed the core checks, but
neither was retained.

The third candidate kept the original geometry calculations and filled the
known thin-border index range directly, avoiding a vector capacity check per
index. Instrumented border-stage means over the final 200 inventory samples:

| Build | Border-stage CPU, microseconds/update |
|---|---:|
| Accepted baseline | 9.29 |
| Rejected cursor walker | 13.93 |
| Rejected callback walker | 9.35 |
| Rejected bulk indices | 7.44 |

These are diagnostic runs at a requested 1 ms interval, including timer
overhead; they are not host frame-time results. Temporary instrumentation and
all three runtime candidates were removed. Their sources, binaries and logs
remain under `.utmp/border-20261031/`.

## Full workload comparisons

The bulk-index candidate was compared in six alternating baseline/candidate/
Chrome pairs across two batches. Fixtures, real fonts, twenty-field inventory
mutations, 20 warmups and 200 measured updates at requested 60 Hz are unchanged.
No own builds overlapped timing. Other work remained allowed to run as requested,
so all numbers are **provisional**. Initial/final sampled geometry agrees across
each batch's six native runs.

Median of run means, milliseconds per inventory update:

| Stage | Accepted baseline | Rejected bulk indices | Chrome 152 |
|---|---:|---:|---:|
| Style/layout | 0.1675 | 0.1550 | 0.1440 |
| Paint recording | 0.2165 | 0.2145 | 0.1853 |
| Complete native CPU | 0.9915 | 0.9340 | Not equivalent |

The aggregate within-pair paint median is 2.8% lower, but unchanged style/layout
work also falls by 7.3%. The first batch regresses by 3.9% in paint and 1.8% in
complete CPU. The second improves by 6.5% and 9.4%, while unchanged style/layout
falls by 14.9%. One baseline run reaches 3.531 ms complete CPU. This variability
does not isolate a benefit from bulk index writes, so the apparent aggregate
gain is not treated as an optimization result.

HUD paired paint is 1.0% lower and complete CPU 1.5% lower. Chrome raster-worker
CPU is 0.0739 ms for inventory and is separate from paint recording. Native
complete CPU includes additional host work and has no equivalent Chrome column.
The [tracked receipt](border-investigation-20261031.json) preserves both full
batches (`comparison-v33` and `comparison-v34`) and diagnostic evidence.

## Retained coverage and validation

The geometry suite grows from 176 to 320 checks. The added independent scalar
comparisons cover thin borders, mixed elliptical and square corners, collapsed
inner outlines, varied subdivision counts, alpha, exact triangle ordering, and
appending to a mesh with existing vertices/indices. Existing allocation budgets
and the geometry hash `e45be54e6504f2cd` are unchanged. The scalar reference is
test-only; it is not a fallback path in the engine.

The restored runtime is verified against HEAD, and its rebuilt Agentic executable
has byte-identical PE sections to the accepted baseline. All 17 GCC suites and
19 Clang sanitizer suites pass with zero compiler warnings. Both compilers
report 320 geometry checks and the unchanged geometry hash.
Neither host source nor installed host binary changes in this pass. The preceding
paint-state full gate remains the runtime qualification; browser/host gates are
not claimed as rerun here.

The next investigation should consider whether decoration meshes can be retained
across more updates before further local tessellation tuning. That is a larger
invalidation and ownership change and needs a separate measured implementation.
