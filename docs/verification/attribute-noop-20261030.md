# Rejected attribute no-op experiment — October 30, 2026

No production engine change is retained. Agentic, Godot and Unity were rebuilt
after restoring the previous shared core. Agentic remains on its unintegrated
audit branch, as documented in the preceding rendering receipts.

The experiment checked attribute presence and equality at the C ABI before
scheduling selector invalidation. The attribute store already ignores equal
writes, but the ABI still schedules work. Explicit composition/dialog semantics
were preserved. Regressions covered equal writes, missing versus empty values,
pending real mutations, draw-list equivalence and IME composition commits.

## Measurements

Two independent batches of eight alternating before/after process pairs used
the authored survival fixtures and full inventory-selection mutations from
the [GPU audit](agentic-gpu-20261030.md). Each process warmed 20 updates and
measured 200. Values are medians of process means, in milliseconds. Complete
CPU includes controller setters, update and host draw submission, not GPU
completion or presentation.

| Batch | Workload | Before complete CPU | Experiment complete CPU |
|---|---|---:|---:|
| 1 | HUD | 0.3170 | 0.3345 |
| 1 | Inventory selection | 2.2995 | 2.2115 |
| 2 | HUD | 0.2890 | 0.3145 |
| 2 | Inventory selection | 2.1640 | 2.1645 |

The first batch's 3.8% inventory improvement did not repeat. HUD was slower
in both batches. Host draw timings also varied despite no renderer changes,
so these measurements do not isolate the extra lookup cost. They do not
justify accepting this implementation or claiming a performance gain.

The next implementation should return the actual mutation result from the
attribute store instead of adding presence/value lookups to every ABI write.
Keep explicit composition and dialog behavior intact.

## Verification of the rejected implementation

- Windows: 16/16 CTest suites; GCC: 15/15; Clang ASan/UBSan: 17/17.
- Main core checks: 492,170 Windows/GCC; 492,164 under sanitizers.
- Chrome tracked layout captures: 338, none above the 1.5px ceiling.
- Unity Native EditMode: 243 passed, 2 inconclusive, 0 failures.
- Godot timing/form/validity/IME/binding/dialog scenes: respectively
  31/522/104/28/186/1199 checks, zero failures.
- Agentic focused renderer tests: 12 passed.

These are experiment checks, not new production gate counts. The temporary
regressions were reverted with their implementation. Full local gate and
Chrome GPU parity are not claimed.

Raw logs remain in `.utmp/render-audit-20261030/noop-*`. The companion JSON
retains both timing batches, binary hashes and fixture arguments. The
comparison tool now retains controller and complete-CPU timings when present
and rejects inconsistent timing coverage between compared binaries.
