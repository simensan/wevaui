# Rejected transform/font preparation experiment — 2026-10-31

Deferring font-size resolution until a glyph-preparation box actually has a
transform did not establish a reliable complete-selection gain. The candidate
was reverted. No Unity or Godot binary was changed.

The candidate factored the existing transform/translate/rotate/scale reads into
a small input record. Glyph preparation checked those values before resolving
font size; transform parsing consumed the same values and preserved the existing
composition logic. It introduced no cache or invalidation behavior. Nevertheless,
less apparent work did not translate into a robust measured benefit.

## Six alternating pairs

Separate executables link the baseline and candidate GCC Release core, using
the [fixed-font selection diagnostic](core-selection-profile-20261031.md).
Each performs 120 warmups and 20,000 complete selection updates, with sampling
and allocation profiling disabled. The twelve processes total **240,000
measured updates**. Setter status checks, twenty final value/presence checks
and missing-asset checks passed in every process. Setter time is excluded from
the reported update interval. These are not host frame times or real-font
measurements. The machine is a Ryzen 7 9800X3D under WSL2; clocks and affinity
were not locked, so system variation remains a limitation.

| Pair | Baseline mean (ms) | Candidate mean (ms) | Candidate change |
|---|---:|---:|---:|
| 1 | 0.387670 | 0.369665 | -4.64% |
| 2 | 0.366857 | 0.359009 | -2.14% |
| 3 | 0.351196 | 0.391121 | +11.37% |
| 4 | 0.360048 | 0.397947 | +10.53% |
| 5 | 0.362803 | 0.354035 | -2.42% |
| 6 | 0.360003 | 0.356138 | -1.07% |

Median paired change is 1.61% faster, but the median of run means changes from
0.361426 to 0.364337 ms (0.81% slower). The conflicting aggregates and two large
losing pairs do not support shipping this refactor as an optimization. Neither
the slowdowns nor the apparent small gains are attributed solely to the changed
code. No p95 or host-speed claim is made.

## Restoration and next target

`paint.cpp` was restored byte-for-byte and the GCC library rebuilt. Its SHA-256
matches the pre-experiment library:
`9ee47cba558a6e81486d1a4b38ddae0d9709116f64b8ca32a73bc8570f72903a`.
The rejected candidate did not proceed to full correctness or host suites;
no passing result for those suites is claimed here.

Raw evidence is under `.utmp/render-audit-20261030/`: transform/font source
snapshots and build logs, plus `core-selection-profile/transform-font-pairs/`,
`compare_transform_font.py` and the candidate executable. The adjacent JSON
records hashes and paired measurements.

The accepted Unity direct-vertex upload remains in production. The next shared
core investigation should examine the amount of invalidated/rebuilt paint work,
rather than continuing to add lookup or preparation fast paths with uncertain
complete-workload value.
