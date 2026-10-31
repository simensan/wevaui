# Decoration reuse investigation — 2026-10-31

This pass retains a 22-state decoration update regression, but **no runtime
optimization**. Independent background/border mesh retention did not establish
a useful workload gain, so the prototype was removed. The accepted runtime is
still the paint-state implementation in `d9c4db38`.

## Opportunity and prototype

A diagnostic run compared rebuilt ordinary decoration meshes and their incoming
effects with the preceding frame. Over the last 200 inventory updates, an average
5.495 nonempty decoration meshes were rebuilt, of which 3.335 were identical.
Those meshes contained 676.94 vertices, of which 241.02 were identical. This
instrumentation copied meshes and is opportunity evidence, not timing evidence.

The prototype retained draw ranges by box ID, validating resolved background,
border geometry/colors and incoming effects. It moved unchanged draws instead
of rebuilding their meshes and preserved draw versions for the host. Full paint
resets cleared the records. Whole-subtree replay relocated their ranges. Column
rules and split table paint remained outside this cache. The prototype passed
all 17 GCC suites, including incremental-versus-fresh draw comparisons.

## Measurements and decision

Six alternating baseline/candidate/Chrome pairs used the existing real-font
HUD and twenty-field inventory fixtures, 20 warmups and 200 measured updates
at requested 60 Hz. No own builds overlapped measurement. Other workspace
activity continued as requested, so **all timings are provisional**. Each
batch's initial/final sampled native geometry matched across its six runs.

Median of run means, milliseconds per inventory update:

| Stage | Accepted baseline | Rejected cache | Chrome 152 |
|---|---:|---:|---:|
| Style/layout | 0.1665 | 0.1950 | 0.1426 |
| Paint recording | 0.2130 | 0.2400 | 0.1873 |
| Complete native CPU | 0.9475 | 1.1060 | Not equivalent |

The within-pair paint median is 5.1% higher, and complete native CPU is 20.9%
higher. The first batch's paired paint median was 3.7% lower; the second was
13.9% higher. Unchanged style/layout also varied substantially. These results
do not isolate a cache-caused regression, but they do not justify accepting
the extra cache state as an optimization. HUD paired paint was 32.0% higher
across the six pairs, also under substantial background interference.

Chrome raster-worker CPU is separately 0.0792 ms per inventory update. Native
complete CPU includes host/controller work and has no equivalent Chrome column.
This pass makes no Chrome-parity or speedup claim. The [receipt](decoration-reuse-20261031.json)
retains both complete batches (`comparison-v35` and `comparison-v36`), including
the outliers and process observations.

## Retained coverage and verification

The regression compares a live document with a fresh build through 22 states:
text changes, currentColor/background changes, opacity, transform, filter,
blend mode, rounded overflow clipping, positioned content, border dimensions,
column rules, visibility and display removal/restoration. It also requires
unique draw versions within a frame and identical draw content whenever a
version is reused across frames. Prototype-only retention expectations were
removed with the cache.

The restored runtime matches HEAD. All 17 GCC and 19 Clang sanitizer suites
pass with no compiler warnings. The rebuilt Agentic executable has byte-identical
PE sections to the accepted baseline. Both installed host binaries are unchanged;
the preceding paint-state full gate remains their qualification. Browser and
host gates were not rerun for this test-only change.

The rejected sources, patch, binaries, diagnostic output, failed experimental
assertion and benchmark logs remain under `.utmp/decoration-reuse-20261031/` and
`.utmp/inventory-chrome-20261031/`. The failed retention assertion identified an
intentional full paint reset on a shrinking label, rather than a rendered-output
mismatch. A future performance pass should profile traversal and full-reset
frequency before adding another geometry cache: the unchanged decoration mesh
opportunity measured here is small.
