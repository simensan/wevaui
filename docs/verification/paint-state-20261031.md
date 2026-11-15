# Combined containing-block queries during paint — 2026-10-31

Paint now asks the shared positioning code for absolute and fixed containing-
block status together. Previously it scanned the same transform, filter,
perspective, will-change and containment properties twice for a static box.
The combined query performs that scan once. Positioning still owns the rules;
the existing single-answer queries remain available to callers needing only
one answer. No cache, invalidation rule, C ABI or host-specific behavior changes.

The shared core change reaches Unity and Godot. The 550 new checks cover all
five position kinds, missing styles, 18 property/value cases, and removing each
property from a live style. Both answers are checked against expected behavior
and the existing individual queries.

## Attribution and provisional timings

A finer diagnostic split of paint state setup identified containing-block
queries as its largest measured part. Mean milliseconds over the final 200
inventory samples, at a requested 1 ms interval:

| Instrumented stage | Baseline | Combined query |
|---|---:|---:|
| Containing-block checks | 0.01056 | 0.00705 |
| Font size | 0.00219 | 0.00223 |
| Border radii | 0.00325 | 0.00321 |
| Transform | 0.00357 | 0.00366 |
| Clip path | 0.00268 | 0.00268 |
| Border colors | 0.00394 | 0.00400 |
| Border tessellation | 0.00922 | 0.00962 |

The containing-block stage is 33.2% lower, a saving of about 3.5 microseconds
per inventory update. These temporary timers add overhead and are attribution,
not host frame timings. All instrumentation was removed before benchmarking.

Two batches contain six alternating baseline/candidate/Chrome pairs in total,
with real fonts, unchanged HUD/inventory fixtures, twenty-field inventory
mutations, 20 warmups and 200 measured updates at requested 60 Hz. No own
builds overlapped timing. Other work remained allowed to run as requested;
the manifests record unrelated compiler activity in some runs. Results remain
**provisional**.

Median of run means, milliseconds per inventory update:

| Stage | Baseline 468b1846 | Combined query | Chrome 152 |
|---|---:|---:|---:|
| Style/layout | 0.1740 | 0.1640 | 0.1571 |
| Paint recording | 0.2310 | 0.2165 | 0.2097 |
| Complete native CPU | 1.0090 | 0.9520 | Not equivalent |

The median within-pair inventory change is **2.6% lower paint CPU** and
**3.7% lower complete native CPU**. The per-batch paired paint medians are
1.0% and 4.3% lower. Unchanged style/layout CPU moves by 1.2% too, so the
entire complete-CPU reduction cannot be attributed to this code change.
HUD paired paint CPU is 2.6% lower and complete CPU is 1.3% lower, but its
second batch moves in the opposite direction. These are modest results with
load variability, not a broad FPS claim.

Chrome timings also vary substantially: the second batch's inventory medians
are 0.265 ms for style/layout and 0.346 ms for paint, versus 0.157 and 0.207 ms
in the first batch. The combined table includes every run, not just favorable
samples. It does not establish consistent Chrome parity. Chrome raster-worker
CPU is 0.0869 ms and stays separate from paint recording; complete native CPU
includes additional host preparation and submission work.

Initial/final sampled geometry agrees across each batch's six native runs.
The [tracked receipt](paint-state-20261031.json) preserves both complete batches,
the diagnostic breakdown, hashes and gate evidence. Local artifacts are under
`.utmp/paint-state-20261031/`; timing batches are
`.utmp/inventory-chrome-20261031/comparison-v31/` and `comparison-v32/`.
The intermediate test compile error from passing a property name to `unset(int)`
is preserved alongside the corrected passing build.

Restoring a saved source file also restored its timestamp, so the first final
Host-engine build reused the diagnostic executable. The section comparison caught
that before delivery. After refreshing the source timestamp and rebuilding,
every PE section matches the measured non-instrumented candidate and differs
from the diagnostic build; whole-file hashes differ in PE headers. Both Linux
archives were checked for absence of the temporary diagnostic marker too.

## Verification

The qualified release gate passes with no skipped stages: 17 GCC suites,
19 Clang sanitizer suites, 341 Chrome layout captures, and 67 browser behavior
checks plus the existing known IME failure. Main-suite coverage is 808,138
checks on GCC and 808,132 under sanitizers, with no compiler warnings.
Godot font, Unicode, rendering, exports and live interaction scenes pass,
including 19,833 font-backend checks.

Both Windows native hosts were rebuilt. Unity full EditMode passes 255 tests
with the same two environment-gated inconclusive; the Mono development player
passes 7/7. All 16 Unity captures and the Godot render match the accepted
combined-metrics baseline pixel-for-pixel. The binding/font/image capture and
Godot render were inspected. No new IL2CPP or hardware qualification is claimed.
