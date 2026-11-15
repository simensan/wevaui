# Combined vertical font queries — 2026-10-31

Inline layout now requests ascent/descent together, and line extents together,
instead of repeatedly querying the same face for each scalar value. The
host-backed implementation performs one face-metrics lookup per combined
query. The existing host adapter already caches these values; this removes
redundant cache lookups, not additional host font calls. No cache, worker,
font-lifetime rule, C ABI change or host-specific layout behavior is added.
Both Unity and Godot receive the shared core change.

The defaults preserve custom metric and leading overrides. Rounded leading,
failed font callbacks, missing backends and the original floating-point
operation order are covered by 390 additional core checks. Strut extents are
computed once per line-building invocation rather than on every line reset.

## Measurements

Two batches contain six alternating baseline/candidate/Chrome pairs, using
real fonts, identical HUD/inventory fixtures, twenty-field inventory mutations,
20 warmups and 200 measured updates at requested 60 Hz. No own builds overlapped
timing. The unrelated workspace was allowed to remain running as requested;
these results are **provisional**, not an isolated-machine parity guarantee.

Median of run means, milliseconds per inventory update:

| Stage | Baseline 95c2874a | Combined queries | Chrome 152 |
|---|---:|---:|---:|
| Style/layout | 0.1855 | 0.1670 | 0.1699 |
| Paint recording | 0.2375 | 0.2380 | 0.2170 |
| Complete native CPU | 0.9665 | 0.9090 | Not equivalent |

The median within-pair change is **12.5% less inventory style/layout CPU** and
**8.6% less complete native CPU**. Five of six layout pairs improved; individual
changes range from 16.8% faster to 6.0% slower. The first batch's paired layout
median was only 1.1% faster, versus 14.1% in the second batch. Paint and host
costs moved too, so the complete-CPU reduction cannot be attributed solely to
the changed code. Chrome raster-worker CPU is 0.0858 ms and is kept separate
from paint recording. Native complete CPU includes controller/update,
preparation and submission and has no equivalent Chrome column here.

HUD paired style/layout is essentially flat (0.4% lower); unpaired medians
are 0.077 ms baseline, 0.082 candidate and 0.0914 Chrome. HUD complete CPU
has a 2.7% lower paired median, but unpaired medians rise from 0.395 to 0.4135 ms.
Do not interpret this batch as a general HUD speedup.

A separate instrumented inventory run reduces cached face-metrics lookups
from 418.17 to 143.05 per update, **65.8% fewer**, over the last 200 samples.
That diagnostic run used a requested 1 ms interval and is not frame-time
evidence. Initial/final sampled geometry agrees across all six native runs in
each timing batch. The inventory style/layout median is near Chrome in this
sample; paint recording remains higher, and consistent Chrome parity is not
established.

## Evidence

Timing batches are `.utmp/inventory-chrome-20261031/comparison-v27/` and
`comparison-v28/`; both use the same baseline-v27 and candidate-v27 binaries.
Build, diagnostic and validation evidence is under
`.utmp/vertical-metrics-20261031/`. The intermediate compile failure from
renaming the combined API before updating test calls is preserved alongside
the successful retry. The [tracked receipt](vertical-metrics-20261031.json)
records both complete batches, counters, source hashes and validation evidence.

The qualified release gate passes with no skipped stages: 17 GCC suites,
19 Clang sanitizer suites, 341 Chrome layout captures, and 67 browser behavior
checks plus the existing known IME failure. The main suite now has 807,588
checks on GCC and 807,582 under sanitizers; both builds have zero warnings.
Godot font, Unicode, rendering, exports and live interaction scenes pass,
including 19,833 font-backend checks.

Both Windows native hosts were rebuilt. Unity full EditMode passes 255 tests
with the same two environment-gated inconclusive; the Mono development player
passes 7/7. All 16 Unity captures and the Godot render match the accepted
currentColor baseline pixel-for-pixel. The binding/font/image capture and
Godot render were inspected. No new IL2CPP or hardware qualification is claimed.
The final host-engine executable hash matches the measured candidate.
