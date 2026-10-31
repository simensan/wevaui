# Threaded runtime UI accounting — October 31, 2026

The rendering audit found and fixed an Agentic profiling defect: completed
render-worker UI work was added to the main-thread `ui` section. This contradicted
the CLI documentation, mislabeled combined work as main-thread cost, and inflated
the CSV main-thread section sum. Wall frame intervals were unaffected.

Agentic `527241509` adds document/snapshot timers; `4c5cf4ecd` moves worker UI
work to `ui_worker_cpu` and excludes it from main-thread trace accounting.
Nested `ui_document` and `ui_capture` remain inside `ui` and are also excluded
from the sum. The raw `worker_ms.ui` interval is retained; it can include waits,
whereas `ui_worker_cpu` excludes the UI join wait and includes preparation work.
Neither measures GPU execution. Do not add overlapping thread intervals.

Historical [runtime](current-runtime-20261031.md) and
[tail](runtime-tails-20261031.md) receipts now label old `ui` values as combined
work. Their raw artifacts remain unchanged. The older 14.9% result describes
combined UI work, not main-thread UI work; the frame-median result remains valid.

## Measured phases

Three fresh processes each profile five modes for 600 measured frames after
120 warmups, with normal CPU-only scheduling: 9,000 measured frames in the same
720p 240-cube world. Median of three per-run means, milliseconds:

| Mode | Main UI | Document/bindings | Paint capture | Worker UI work |
| --- | ---: | ---: | ---: | ---: |
| Hidden UI | 0.00054 | 0 | 0 | 0.02354 |
| Static HUD | 0.00206 | 0.00128 | 0.00014 | 0.07803 |
| Updating HUD | 0.14093 | 0.13119 | 0.00901 | 0.10136 |
| Cycling inventory | 0.53311 | 0.51061 | 0.02165 | 0.18615 |
| Static inventory | 0.00184 | 0.00120 | 0.00013 | 0.09230 |

Document update, including binding refresh, dominates main-thread inventory
cost. Snapshot copying accounts for about 4% of that main-thread interval.
The formerly unexplained ~0.18 ms is worker work, not missing main-thread work.
Worker UI remains nonzero when the HTML overlay is hidden; the worker interval
covers all UI work, so this alone does not prove hidden HTML is being drawn.
That worker path and the document-update breakdown are the next useful targets.

These are attribution results from one machine. Instrumentation adds three
clock reads to visible snapshots. No new speedup, lower-end result, Chrome
parity, GPU timing or full release gate is claimed.

## Validation and provenance

- Snapshot test passes: ordinary/measured idle snapshots share the same Arc,
  and hiding resets both timers to zero without creating a local renderer.
- Trace regression passes with main UI 1 ms, nested phases 0.7/0.2 ms and worker
  UI 5 ms: nested/worker fields are retained but excluded from the main sum.
- Runtime SDK builds and publishes successfully. In all 9,000 measured frames,
  document plus capture does not exceed main UI beyond 0.001 ms rounding
  tolerance; all hidden document/capture values are zero.
- All three runs pass selection, use/close and post-reload clicks, with 355
  reloads. All owned sessions stop. The three inventory captures have the same
  hash as the previously inspected image, preserving pixel output exactly.

SDK `733f49a0c786cdc9ef5884aa140a5e93bf140e0d11ffa99d92112a62cd06dfc8`
uses Agentic `4c5cf4ecd` and clean Weva `a7253f9f` (core unchanged since
`d863b7d3`). [Compact results and provenance](runtime-accounting-20261031.json).
Raw evidence: `.utmp/render-audit-20261030/runtime-accounting/` and
`runtime-accounting-sdk.log`. The intermediate instrumented run is retained
under `runtime-phases/`; it still has old combined accounting and is excluded
from this table. Initial planner-selection and clean-checkout build rejections
are retained; subsequent checks/builds pass.

Agentic focused check receipts: `builds/verify/20261031-005930` (snapshot),
`20261031-010109` (nested trace phases), `20261031-010703` (worker exclusion).
No shared core or Godot/Unity runtime behavior changed, so no host rebuild is
required for this accounting fix. Their accepted core improvements remain in
sync. Agentic stays on its audit branch; no upstream merge or push occurred.
