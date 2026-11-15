# currentColor rendering and local-layout investigation — 2026-10-31

The shared paint resolver now resolves `currentColor` through the element's
live color instead of treating it as an invalid transparent value. This fixes
the background defect found in the preceding pseudo-keyword pass and applies
to every caller of the common color resolver. `color: currentColor` walks the
inheritance chain iteratively, including the root's initial black. Ordinary
parsed colors retain their direct return path; resolved currentColor values
are not cached independently of their source.

Nine new core checks cover case-insensitive syntax, alpha, source changes,
parent changes and a 512-style chain. The live Chrome check now includes three
currentColor mutations on ordinary and pseudo-element backgrounds. The previously
failing four-background fixture now matches Chrome 152 RGB pixels exactly, and
the final render was inspected. No C ABI or host-specific behavior was added.

## Layout attribution

Temporary instrumentation divided incremental updates into phases. Across 200
sampled inventory updates, local layout was the largest measured phase:
33.7 microseconds, versus 12.9 for splicing, 9.7 for proof/positioning, 8.3 for
box construction and 5.3 for indexing. A second diagnostic pass attributed
most inline work to line construction rather than item collection or atoms.
These are instrumented diagnostic runs at a requested 1 ms interval with the
other workspace active, not comparable frame-time benchmarks. The temporary
source, executables and logs are preserved; instrumentation is not retained.

## Rejected width-reuse experiment

The candidate reused a single left-aligned plain line at another width when
all fragments still fit, retaining the original path for alignment, indentation,
bidi and preserved-whitespace cases. It passed all 17 GCC suites, including
new full-tree equivalence checks across widths, wrapping thresholds, soft
hyphens, whitespace and alignment. A Chrome width-transition check also passed.

The diagnostic inventory run made 2,804 inline-layout requests in both builds.
Actual line builds fell from 2,412 to 1,485, a **38.4% reduction in that work**.
That did **not** establish an end-to-end improvement, so the entire candidate
and its additional tests were reverted. The experiment patch and browser check
are saved under `.utmp/currentcolor-20261031/`.

Two batches contain six alternating baseline/candidate/Chrome pairs in total,
using real fonts, identical fixtures and twenty-field inventory mutations,
20 warmups and 200 measured updates at requested 60 Hz. Both native binaries
include the currentColor fix, so this comparison isolates the width-reuse
experiment. The unrelated host-engine game stayed active as requested; no own
builds overlapped timing. All numbers remain **provisional**.

Median of run means, milliseconds per inventory update:

| Stage | Color fix only, v24 | Rejected reuse, v25 | Chrome 152 |
|---|---:|---:|---:|
| Style/layout | 0.171 | 0.179 | 0.143 |
| Paint recording | 0.221 | 0.230 | 0.196 |
| Complete native CPU | 0.948 | 1.020 | Not equivalent |

The median within-pair change over all six pairs is **2.5% slower style/layout**
and **5.2% slower complete CPU**. The first batch looked faster; the second did
not. Paint and host costs also moved, so this does not isolate a causal slowdown
from the reuse checks. It does not justify shipping the experiment either.
Initial/final sampled geometry agrees across each batch's six native runs.
Chrome raster-worker CPU is 0.083 ms and stays separate from paint recording.
Complete native CPU includes controller/update, preparation and submission.

This pass makes **no new inventory speedup claim**. Chrome parity is not reached.
Further investigation should profile the line-building algorithm itself and
the cost of reuse validation before repeating this candidate. Fewer calls are
insufficient evidence of lower runtime cost.

## Verification and evidence

Timing batches are `.utmp/inventory-chrome-20261031/comparison-v25/` and
`comparison-v26/`. Build, diagnostic and render evidence is under
`.utmp/currentcolor-20261031/`. The experimental full gate was intentionally
stopped after the combined measurements rejected the candidate; its partial
logs are preserved. The final color-only gate runs separately under `gate-color/`.

The final color-only qualified release gate passes with no skipped stages:
17 GCC suites, 19 Clang sanitizer suites, 341 Chrome layout captures, and 67
live browser checks plus the existing known IME failure. Main-suite coverage
is 807,198 checks on GCC and 807,192 under sanitizers, with no compiler warnings.

Both Windows native hosts were rebuilt. Unity full EditMode passes 255 tests
with two existing environment-gated inconclusive; the Mono development player
passes 7/7. All 16 Unity player captures and the Godot gate render are unchanged
from the accepted pseudo-keyword build. Godot font, Unicode, exports, rendering
and live scenes pass, including 19,833 font checks. The player binding/font/image
render and Godot render were inspected. No new IL2CPP or hardware claim is made.

A separate C ABI test performs five consecutive parent-color mutations, including
alpha, and checks all emitted colored vertices for an ordinary background and
a pseudo background. All five updates pass; source and output are preserved.

The final host-engine executable was rebuilt from the retained source; every PE
section is byte-identical to v24 (the full-file hashes differ). The receipt
verifies the discarded layout files match HEAD and records final binary hashes.
The [tracked receipt](currentcolor-and-layout-probes-20261031.json) includes both
timing batches, diagnostic counters, visual fixtures and source/evidence hashes.
