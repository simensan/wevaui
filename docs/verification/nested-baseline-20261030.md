# Nested first-baseline correction — October 30, 2026

The shared core now obtains a flex item's first baseline from nested in-flow
content instead of checking only direct line children and otherwise using
the bottom edge. This resolves the up-to-47px discrepancy preserved by the
[flex audit](flex-baseline-20261030.json).

Grid layout exports the first grid item's baseline in row-major placement
order. Flex exports its first line's participating item baseline, or its
first item's fallback. A shared query walks ordinary in-flow descendants,
skips floats and positioned children, and consumes those exports. Inline-flex
rendering uses the same query, replacing its separate descendant walk.
Incremental layout compares the nested first baseline as an external export,
so equal outer dimensions alone cannot hide a changed baseline.

This follows the distinction between a contributing baseline and an
alignment-context fallback in [CSS Box Alignment](https://www.w3.org/TR/css-align-3/#baseline-export)
and [Grid container baselines](https://www.w3.org/TR/css-grid-1/#grid-baselines).
Chrome remains the behavioral reference. This is not complete baseline
conformance: all writing modes, last-baseline alignment and grid baseline
sharing groups are outside the demonstrated coverage.

## Evidence

- The original failing Chrome 152 capture now agrees, without an exemption.
  New tracked `74-nested-first-baseline` also covers reordered/explicit grid
  placement, an absolutely positioned child and reversed/reordered nested flex.
- All 340 tracked captures pass the 1.5px ceiling: 47 samples, 68 hand,
  225 harvest. No known-gap exemptions were needed.
- Two additional incremental variants exercise fixed-height grid and column
  flex items under baseline alignment while fonts, padding and constraints
  change. Their draws and layout dumps match a forced full rebuild.
- Windows 16/16 CTest suites, GCC 15/15, Clang ASan/UBSan 17/17.
  Main checks: 492,906 Windows/GCC, 492,900 sanitized. No compiler warnings
  found in captured builds.
- Unity plugin rebuilt/deployed; Native EditMode 243 pass, 2 environment-gated
  inconclusive, zero failures. Godot rebuilt; timing/form/validity/IME/binding/
  dialog scenes pass 31/522/104/28/186/1199 checks.
- Host-engine rebuilt; 12 focused renderer tests pass. The new fixture was rendered
  and inspected with a white background added for visibility. This visual
  inspection uses the standalone font backend, while the numeric Chrome
  comparison uses the oracle's matched synthetic font metrics.

No C ABI change. These are affected-suite checks, not a complete local-gate
rerun or a general production-readiness claim.

## Performance cost

This is a correctness fix. Two independent batches of eight alternating
before/after process pairs measured the same 720p survival mutations. Each
process warmed 20 updates and measured 200. Complete CPU includes setters,
update, paint snapshot/upload preparation, encoding and queue submission;
GPU completion and presentation are excluded. Values are medians of run
means, not per-frame p95.

| Batch | Inventory before → after | HUD before → after |
|---|---:|---:|
| 1 | 0.934 → 0.964 ms | 0.1750 → 0.1855 ms |
| 2 | 0.872 → 0.881 ms | 0.1660 → 0.1735 ms |

Inventory complete CPU increased by 1.0–3.2% in these batches. The correctness
fix is retained with that cost disclosed; it is not an additional speedup.
The companion JSON includes these batches and a fresh combined comparison
against the pre-deferred-flex build, with binary hashes and every run mean.
That fresh combined comparison measures inventory complete CPU at
1.331→0.901ms, a 32.3% reduction with the correctness fix included.

Raw logs, renders and paired results remain under
`.utmp/render-audit-20261030/baseline-*`. Prior failing evidence is preserved.
The broader audit still needs host-engine SDK/runtime validation, equivalent
Chrome rendering comparisons, cold-opening, lifecycle and busy-world checks.
`engine-main` is managed by the SDK refresh tool and follows `origin/master`;
it has not been silently switched to the audit branch.
