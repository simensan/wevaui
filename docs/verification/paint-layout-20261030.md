# Mixed paint/layout reuse — October 30, 2026

The shared core no longer invalidates layout-input versions for paint-only
style changes during an incremental layout update. Paint keeps its separate
invalidation path and reads the current styles. This applies to Agentic,
Godot and Unity without changing the C ABI or host implementations.

Previously a highlight change within a grid made its cached geometry
ineligible when another element needed layout in the same update. The
replacement rebuilt that grid even though its layout inputs were unchanged.

## Measurement

Eight alternating before/after process pairs per workload, 20 warmup updates
then 200 measured updates per process, at 1280x720. Builds and other tests
finished before these measurements. Values are medians of process means in
milliseconds, not frame p95. Complete CPU includes setters, update, snapshot,
upload preparation, encoding and queue submission; GPU completion and
presentation are outside this measurement.

| Workload | Before core | After core | Before complete CPU | After complete CPU |
|---|---:|---:|---:|---:|
| Targeted 48-cell mixed update | 0.471 | 0.046 | 0.5785 | 0.1240 |
| Survival inventory selection | 1.086 | 1.104 | 1.3980 | 1.4245 |
| Survival HUD (selection batch) | 0.075 | 0.0665 | 0.1870 | 0.1660 |

The targeted case reduces complete CPU by 78.6% and core update by 90.2%.
It resizes a neighboring flex item and changes one grid cell's background
and text color. It is a mechanism benchmark derived from the retained-grid
regression, not a claim that the whole survival inventory became 79% faster.
The survival selection result is 1.9% slower in this batch; no improvement
is claimed for it. HUD timings varied despite an unchanged reuse pattern,
so the HUD difference is not attributed to this patch.

The companion JSON preserves raw run means, executable hashes, inputs and
fixture hashes. Raw output and temporary diagnostic traces remain under
`.utmp/render-audit-20261030/paint-layout-*` and `grid-audit.log`.
An earlier four-pair exploratory run overlapped compilation and is excluded.

## Remaining inventory bottleneck

Temporary instrumentation found that the survival inventory grid is eligible
for retention but flex's natural measurement lays it out at 1022px before
assigning its final 720px width. The first width mismatch materializes its
children and loses the retained proxy. Both versions replace one inventory
subtree and retain zero grids on all 220 warmup/measured mutations. The next
optimization needs to preserve correct natural/intrinsic exports across those
two constraints, including text wrapping and flex sizing. Do not bypass this
proof with a game-specific fixed-size assumption. Temporary instrumentation
was removed from production.

## Verification

- Windows: 16/16 CTest suites. GCC: 15/15. Clang ASan/UBSan: 17/17.
  Main suite: 492,136 checks on Windows/GCC, 492,130 under sanitizers.
  No compiler warnings found in the captured build logs.
- The retained-grid regression now combines a paint-only change inside a
  grid with layout outside it, across four clip/transform configurations.
  It compares complete draw data with a forced full rebuild and verifies
  that unaffected gradient draw allocations remain retained.
- Chrome layout: 338 tracked captures (47 samples, 66 hand, 225 harvest),
  zero above 1.5px and no known-gap exemptions needed.
- Unity plugin rebuilt/deployed; Native EditMode: 243 pass, 2 inconclusive,
  zero failures.
- Godot extension rebuilt; timing/form/validity/IME/binding/dialog scenes:
  31/522/104/28/186/1199 checks respectively, zero failures.
- Agentic rebuilt against the current core; 12 focused renderer tests pass.
  Current survival inventory PNG rendered and inspected. The older PNG used
  different bound detail content, so pixel identity with it is not claimed.

This is affected-suite verification, not a new full-local-gate receipt.
No Chrome GPU parity, busy-world performance or cold-start gain is claimed.
Agentic's renderer branch remains unintegrated into engine-main.
