# Paint cost audit and rejected property-ID experiment — October 31, 2026

Buffered core traces from three separate Agentic runtime processes locate the
inventory document cost in paint and incremental box/layout work. The processes
use the accepted accounting SDK and survival fixture, with 600 measured frames
after 120 warmups, CPU-only profiling, and no concurrent builds. Buffered traces
are written at shutdown and retain all 872/911/889 updates respectively.

For attribution, discard the first 20 initialization updates and average the
remaining records. These records also include updates outside the CLI's measured
window; they are not an exact frame-by-frame decomposition of its profile.
Median of three run means: cascade 0.0613 ms, box stage 0.1759 ms, paint
0.2055 ms, core total 0.4457 ms. The box stage **includes incremental layout**;
the near-zero later layout stage does not mean layout is free. Binding refresh
is outside this core-update trace.

A separate one-process `WEVA_PAINT_LOG` probe reports approximately 0.041 ms
glyph preparation, 0.033 ms text, 0.010 ms backgrounds and 0.142 ms remainder,
with 32 glyph subtrees reused in representative warmed frames. Submission
rounds to zero at the diagnostic's 0.01 ms resolution. This probe adds clock
reads and synchronous logging; it locates work, not a performance baseline.
The remainder includes traversal/state/reuse work and is not yet attributed
enough to claim a particular function is the bottleneck.

## Rejected experiment

The candidate cached 28 registered literal property IDs in the paint helpers,
avoiding repeated name lookups. `-webkit-background-clip` is not registered;
the cached-ID checker caught it and it retained its existing name-based read.
Dynamic property reads also remained name-based. The optimized diagnostic built
successfully and the corrected registered-ID check passed.

Four alternating before/after process pairs ran prepared 720p HUD and inventory,
20 warmups and 200 changing/200 retained frames, pacing at 60 Hz. Median of
per-run CPU means, milliseconds:

| Work | HUD before → candidate | Inventory before → candidate |
| --- | ---: | ---: |
| Paint | 0.0590 → 0.0525 | 0.2950 → 0.2890 |
| Core update | 0.1475 → 0.1370 | 0.7155 → 0.7365 |
| Complete CPU path | 0.4245 → 0.3820 | 1.3125 → 1.3920 |

The inventory complete path is 6.1% slower in this batch. Lower lookup work
does not establish a complete-path improvement, so the candidate was reverted.
Initial/final mutation-target geometry agrees across all eight runs. No full
correctness gate or host qualification was run for this rejected candidate;
Godot, Unity and published Agentic SDKs never received it. The diagnostic was
rebuilt successfully against restored production source.

All runs and binary hashes are in the [JSON receipt](paint-cost-audit-20261031.json).
Raw evidence remains in `.utmp/render-audit-20261030/inventory-core-trace/`,
`inventory-paint-trace/`, `paint-ids-paired/`, the `paint-ids-*.log` build logs and
`paint-ids-experiment.patch`. An initial build from the Weva working directory
selected the wrong Rust toolchain and failed; the retry in the leased Agentic
worktree succeeded. No toolchain or dependencies were changed.

The next useful target is the unexplained paint traversal/reuse remainder,
followed by incremental layout. This chunk retains evidence only and claims
no new production speedup.
