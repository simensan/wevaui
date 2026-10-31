# Retain proven flex-row allocations — 2026-10-31

The shared core can keep a text update inside a flex item when its parent
allocation is proven independent of the changed intrinsic widths. Four paired
Unity runs improve full-inventory total CPU in every pair: **14.9% median paired
reduction**, with **20.6% lower p95**. This is not the earlier rejected shortcut:
it accepts a smaller replacement subtree instead of skipping a doomed probe
and rebuilding the same parent.

## Allocation proof and correctness fix

The flex solver records a restricted case: a row item has a definite width,
auto basis, auto minimum, no maximum, and no authored aspect ratio. Its width
caps the automatic minimum at the base. The proof is revoked if any allocation
step goes below that base. Descendant intrinsic widths therefore cannot change
this allocation. Incremental layout still compares natural dimensions, final
geometry, margins, baselines and intrinsic exports. It reapplies a previously
stretched height only after the natural input checks pass. Changes to the
item's own layout styles cannot use this new proof.

The investigation also exposed an existing edge-change defect. Removing
padding from a border-box item can keep its natural dimensions unchanged while
changing its inner base and therefore the flex-shrink weights. Incremental
layout kept 100 px where full layout and Chrome produce 75 px; both row and
column regressions reproduced it. Flex-item padding/border-frame changes now
return to the parent solver. This avoids duplicating flex sizing arithmetic
inside the incremental code.

The new generic text matrix covers 17 flex/grid configurations and four
successive text values each, including wrapping, shrinking, growing,
intrinsic sizes, maximum widths, percentage heights, nested flex, baselines
and opposing min/max clamps. Every incremental frame is compared with forced
full layout using identical glyph history. The edge-change tests and new
Chrome capture `75-flex-border-box-shrink-weights` pin the 100/50 to 75/75 split.

The title-only diagnostic now replaces the details panel instead of the
inventory row: 255 of 299 draws reused, versus 242 previously (**44 rather
than 57 rebuilt**), with no new textures and unchanged target geometry.
That counter probe uses StubFont and logging; it is not the timing result.

## Paired Unity measurement

Four pairs of fresh Unity 6000.4.1f1 editor processes alternate order. Each
process measures four workloads after 128 warmups, 2,048 updates each:
**65,536 measured iterations**. The native plugin changes; the managed renderer,
font bytes, prepared fixtures and bindings stay the same. Full inventory applies
all twenty selection mutations. Every target is checked outside timing, and
retained output matches a fresh renderer. All four workload PNGs are identical
across all eight processes; the candidate inventory was inspected.

Milliseconds below are medians of four per-run median / p95 values. Totals use
each iteration's sum, not summed independent percentiles.

| Workload | Baseline total CPU | Candidate total CPU | Median paired change |
| --- | ---: | ---: | ---: |
| Idle HUD | 0.03225 / 0.04715 | 0.03370 / 0.03840 | +5.08% |
| Changing HUD | 0.23500 / 0.33150 | 0.20850 / 0.27475 | -10.60% |
| Quantity/highlights | 0.43448 / 0.60220 | 0.44295 / 0.52400 | +0.52% |
| Full inventory | 1.16400 / 1.53900 | 0.95348 / 1.18900 | -14.92% |

Full inventory improves in all four median and p95 pairs; its update stage has
a 22.14% median paired reduction and core update 25.46%. Upload median improves
4.83%. Changing HUD total improves in all four median pairs. Quantity-only and
idle medians remain mixed; tiny idle values are particularly sensitive to timer
granularity and scheduling. No sample or outlier is discarded. The table's
aggregate ratios differ from median paired changes and must not replace them.

These are Windows RTX 5080 editor CPU intervals for update, texture/mesh sync
and command recording. They exclude GPU submission/execution and frame pacing.
No builds or tests overlap timing. They establish neither an always-faster
claim nor a player FPS, cold-start or Chrome performance result.

An earlier partial batch was stopped when the own-edge-change guard needed
correction. It is preserved under `flex-proof-host-pairs/` and excluded. Only
`flex-proof-host-pairs-v2/` measures the final candidate.

## Agentic cross-host timing

Four alternating pairs of fresh Agentic diagnostic processes use the same
prepared fixtures, real font backend and twenty-field inventory selection
updates. Each case warms for 20 updates, then measures 200 changing and 200
retained updates at a requested 60 Hz. No build or test overlaps these runs.
Initial and final target bounds are exactly identical across all eight
processes. This is the leased Agentic audit branch, not a published SDK.

| Changing workload, ms/update | Baseline | Candidate | Median paired change |
| --- | ---: | ---: | ---: |
| HUD complete CPU path | 0.587 | 0.586 | +0.03% |
| Inventory style/boxes/layout | 0.477 | 0.341 | -27.38% |
| Inventory core paint | 0.413 | 0.347 | -14.43% |
| Inventory complete CPU path | 1.578 | 1.351 | -12.57% |

Values are medians of four per-process means. Inventory style/layout improves
in all four pairs; paint and complete CPU improve in three. HUD is mixed.
Complete CPU includes controller/update and host capture, resource preparation,
encoding and submission, but not GPU execution or presented-frame latency.
All runs, including the slower candidate pair, remain in the receipt. The
previous Chrome comparison is a different batch and must not be combined with
these values to infer a current speed ratio against Chrome.

## Integration and evidence

The qualified `check.sh --release` gate passes with no skipped stages:
15 GCC suites and 17 Clang ASan/UBSan suites, with 706,127 / 706,121 checks
in their main core suites; 341 Chrome captures (47 samples, 69 hand cases,
225 harvested) at the 1.5 px ceiling; and 66 browser behavior passes with
the existing IME exception. Godot passes 19,833 font checks, Unicode,
renderer comparisons, native debug/release/embedded exports and live scenes.
The final Godot render was inspected. Windows Unity and Godot binaries
are rebuilt from the same core.

Full Unity EditMode passes 255 tests with two existing environment-gated
inconclusive; the Windows Mono development player passes 7/7. All 16 player
PNGs match the prior direct-vertex baseline byte for byte. These checks do
not establish a Godot performance gain or IL2CPP/release-player coverage.
The change is private shared-core state, with no C ABI or host API change.
Unity and Godot receive the same logic.

The [JSON receipt](flex-row-allocation-20261031.json) preserves every Unity run,
paired aggregates and binary hashes. Local evidence is under
`.utmp/render-audit-20261030/`: `flex-proof-*` build/test logs,
`core-selection-profile/flex-proof-title-v2.log`, `flex-proof-host-pairs-v2/`,
`flex-proof-agentic/` and `flex-proof-gate/`. Initial failed tests and interrupted
timings remain alongside the accepted evidence. The source baseline is
`bcb337f1`; archived source snapshots and plugins identify the compared builds.
