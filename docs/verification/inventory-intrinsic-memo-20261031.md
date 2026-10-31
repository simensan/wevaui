# Local intrinsic-size memo storage — 2026-10-31

The shared core now sizes short-lived intrinsic contribution memo storage by
the work a scope actually performs. Previously, its first measurement allocated
and initialized one entry for every box in the document, even when indexing a
small replaced label. The table now starts with sixteen inline entries per
measurement variant and promotes to dense indexing for larger walks. Recursive
measurement may promote the table before an ancestor inserts its result; that
case is handled explicitly. Scope lifetime, tree/context isolation and sizing
arithmetic are unchanged. No storage persists beyond the scope.
The tradeoff is a larger fixed memo object for very small documents; scopes
that promote keep that inline prefix alongside the dense table until teardown.

This follows the [box-copy cleanup](inventory-box-copies-20261031.md), baseline
commit `f03515ee`. Unity, Godot and explicit-source Agentic builds use the same
core. No C ABI or authoring API changes, sample simplifications or host-specific
layout paths were added.

## Provisional performance

Three rounds alternate baseline/candidate/Chrome order with the same inventory,
real Open Sans, twenty-field selection mutations, 20 warmups and 200 measured
updates at requested 60 Hz. The other Agentic workspace remains active at the
user's request; process observations are retained. No own builds overlap timing.
These results remain **provisional**. Sampled initial/final target geometry is
identical across native baseline and candidate runs.

Median of run means, milliseconds per inventory selection:

| Stage | Baseline | Candidate | Chrome 152 |
|---|---:|---:|---:|
| Style/layout | 0.193 | 0.173 | 0.138 |
| Paint recording | 0.216 | 0.202 | 0.196 |
| Complete native CPU | 0.898 | 0.797 | Not equivalent |

Paired median changes are **−10.1% style/layout** and **−9.7% complete native
CPU**. HUD complete CPU measured −8.5%. Other stages vary too; the paint result
is not evidence of a separate paint optimization. Ratios of independent medians
differ from the paired percentages.

Chrome parity is **not reached**: style/layout is about 25% higher and paint
about 3% higher in this batch. Chrome raster-worker CPU (0.081 ms) stays
separate. Complete native CPU includes controller/update, resource preparation,
encode and submission. Comparisons to earlier Chrome batches are not paired
measurements, and successive optimization percentages must not be added.

## Allocation regression

The new `weva_intrinsic_allocations` suite measures three sparse box queries
in 32-box and 4,096-box documents. Both use **one allocation, 1,264 bytes** in
the GCC build. This pins locality independently of background timing noise.
It also checks all three measurement variants across promotion, reverse cache
hits, nested scope restoration and promotion during recursive measurement.
The existing intrinsic/layout corpus continues to check the sizing behavior.

Timing evidence is in `.utmp/inventory-chrome-20261031/comparison-v12/`;
build/test evidence is in `.utmp/inventory-memo-20261031/`.
[Measurement receipt](inventory-intrinsic-memo-20261031.json).

## Verification

The complete qualified release gate passes with **exit code 0**. GCC passes
16 suites and Clang ASan/UBSan passes 18. The main suites retain 707,090 and
707,084 checks respectively; the new intrinsic allocation suite adds ten
separate checks on both configurations. All 341 tracked Chrome captures pass
the 1.5 px ceiling, including 69 hand cases. Browser behavior passes 66 scripts
with the existing IME exception.

Windows Unity and Godot native libraries were rebuilt and installed. Unity full
EditMode passes 255 tests with two existing environment-gated inconclusive
results; its Windows Mono player passes 7/7. All sixteen player captures are
RGB-identical to the box-copy build, and the packaged-font/binding capture was
inspected. Godot font safety passes 19,833 checks; Unicode, debug/release/embedded
exports, backend renders and live scenes pass. The final Godot render was
inspected. This pass does not add IL2CPP or hardware qualification.

The final Agentic benchmark was rebuilt after adding the explicit `<array>`
include and tests. Its `.text`, `.rdata`, `.data`, `.pdata`, `.tls` and `.reloc`
sections are byte-identical to the measured candidate; PE header metadata
differs. Both binary hashes and section comparisons are retained in
`agentic-rebuild-hashes.json` under the build/test evidence directory.
