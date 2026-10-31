# Rejected paint shaping-buffer experiment — 2026-10-31

Reusing temporary shaped-glyph vector capacity within a paint pass reduced
steady-state allocation count from **604 to 331 (45.2%)**, but increased the
core-update mean by **4.76% median paired**, losing five of six timing pairs.
The experiment is removed. It is not a runtime optimization in the current core.

The [paint caller audit](paint-callers-20261031.md) identified preparation and
text geometry work. Inspection also confirmed that slot-only selection already
rebuilds just seven draws in the earlier isolation trace, limiting the potential
benefit of a new descendant-invalidation scheme for that particular path.
The experiment instead tried to eliminate repeated shaping-vector allocations.

## Candidate and method

The candidate added pass-local scratch capacity to the paint context. Shaping
calls borrowed the buffer, cleared its contents, then returned its capacity
after consuming the run. Borrowing moved the vector out of the scratch slot,
so nested calls could not overwrite a live run. It cached no shaped results,
changed no font inputs, and kept capacity only for the duration of the pass.
Preparation, text geometry, shadows, controls and popup preparation used it.

Baseline is the current `5f2d12f8` source; only `paint.cpp` and `paint.h` differed.
Both executables use the same generated fixed-font selection diagnostic and GCC
Release configuration. Six pairs alternate fresh-process order. Each process
warms for 120 selections, then cycles all twenty mutations across twelve states
for 6,000 updates: **72,000 measured updates**. No builds or tests overlap timing.
Profiler/logging switches are disabled. Every setter and twenty final values,
attribute-presence states and missing assets pass. Timings cover core update,
excluding setters; allocation counts cover the final measured update only.

| Pair | Baseline mean (ms) | Candidate mean (ms) |
| --- | ---: | ---: |
| 0 | 0.247184 | 0.268277 |
| 1 | 0.276405 | 0.283560 |
| 2 | 0.235508 | 0.255865 |
| 3 | 0.261104 | 0.279209 |
| 4 | 0.256265 | 0.254915 |
| 5 | 0.262382 | 0.267537 |

These are synthetic-font core measurements, not host CPU/GPU or frame latency.
No reason is asserted for the slowdown without further evidence. The allocation
reduction alone does not justify adding the machinery. No host binary was built
or installed from the candidate, and no render-equivalence claim is made for it.

## Restoration and evidence

The exact saved baseline bytes are restored and `git diff -- libweva` is empty.
The restored GCC build passes, followed by all 15 CTest suites with zero failures.
Unity and Godot retain their already verified optimized core; no ABI/API change
survives. Candidate source, executable and all negative timing evidence remain
under `.utmp/render-audit-20261030/shaping-scratch/`.

The [JSON receipt](shaping-scratch-20261031.json) preserves all twelve timing
results and artifact hashes. Future paint work should target redundant geometry
or invalidation supported by traces, rather than repeat this allocation-only
change or the previously rejected glyph-map replacement without new evidence.
