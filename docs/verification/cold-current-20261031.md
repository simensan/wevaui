# Current cold opening and retained reuse — October 31, 2026

The current Agentic audit SDK still hitches on first inventory opening.
Five fresh processes measured 77.231, 19.976, 21.169, 19.998 and 24.412 ms
of opening-frame main CPU. The median is 21.169 ms; the first sample is
retained, not discarded as warmup. This exceeds a 60 Hz frame budget.

In the 77.231 ms sample, document update accounts for 48.519 ms and snapshot
capture for 0.466 ms. Main UI totals 48.988 ms; the remaining main CPU is
not attributed by this UI section. Source inspection confirms screen creation
and `load_files` run through `apply_html_screen_queue`, outside document
update/capture. This identifies a measurement boundary, not proof that all
remaining time is loading or that OS scheduling caused the tail.

## Reuse verification

Three further fresh processes each open once, hide the same painted overlay,
then reopen twice. Median of three opening-frame observations, milliseconds:

| Measurement | First opening | First retained reopen | Second retained reopen |
| --- | ---: | ---: | ---: |
| Main CPU | 20.6215 | 0.5608 | 0.5590 |
| Main UI CPU | 17.7083 | 0.1376 | 0.1304 |
| Worker UI CPU | 1.2765 | 0.1107 | 0.1170 |
| Following frame interval | 20.6756 | 0.6170 | 0.6150 |

All six retained main-CPU observations are between 0.507 and 0.571 ms.
Main/worker work overlaps; do not sum these columns. These are same-content
reopenings, not a changed-data or new-glyph latency bound. This verifies
reuse in the current SDK; it does not claim a new renderer optimization or
a paired improvement over the older glyph-preparation receipt.

The practical loading-phase workflow remains: complete a first visible
update/paint, hide the overlay, and retain it for later visibility changes.
Loading and immediately hiding does not prewarm layout or glyphs. An
offscreen prewarm API or edit-time font bake remains unimplemented.

## Scope and evidence

Agentic `26fc09a7e`, shared core `d863b7d3`, SDK
`b0764fc224eab5f2c3e8fa1c247e7c8a99989582e19cdba98de2621f81f3c636`.
Windows/RTX 5080, 1280×720, no VSync, off-desktop synthetic 240-cube scene.
Each opening retains 6,000 CPU-only frames with its actual injected input
tick inside the profile. Across eight processes: 84,000 unmarked frames,
243 entities throughout, matching artifact hashes and successful session
stops. Inventory captures match within both batches; the final retained
capture was inspected and renders the expected panel, icons and text.
No concurrent build/test workload was run.

`run_agentic_cold_audit.py` now supports `--reopens N`, retains named CPU
sections and runtime hashes, and checks frame/entity counts and capture
hashes. The real three-process/two-reopen run exercises this path; Python
compilation and whitespace checks also pass.

[Per-opening statistics and hashes](cold-current-20261031.json).
Raw evidence: `.utmp/render-audit-20261030/cold-current-20261031/` and
`retained-current-20261031/`.

No engine code, ABI, font behavior or Godot/Unity integration changed in
this measurement chunk. Their host-owned font costs cannot be inferred
from Agentic. Next useful work is explicit load/setup attribution and
reducing or scheduling first-use glyph/layout preparation. Long lifecycle
testing, lower-end hardware, matched Chrome rendering and branch integration
remain open. This does not establish release readiness.
