# Attribute mutation-result experiment — October 31, 2026

Rejected: no production change retained. The previous no-op experiment added
attribute lookups; this variant reads the existing DOM version around the write
and suppresses ABI restyle scheduling only when that version is unchanged.
Explicit composition/dialog handling remains before the early return.

Four alternating before/after process pairs used the corrected black-cleared
Agentic diagnostic and prepared fixtures at 1280x720, pacing at 60 Hz.
Each process warmed 20 updates and measured 200 moving/200 retained frames.
Builds did not overlap measurements. Medians of run means, ms:

| Workload | Before complete CPU | Experiment complete CPU |
| --- | ---: | ---: |
| HUD | 0.4325 | 0.4030 |
| Inventory selection | 1.5365 | 1.5750 |

Inventory core update also did not improve: 0.9175 to 0.9320ms. This does not
justify shipping the patch for the identified inventory bottleneck. GPU
execution is excluded. No speedup is claimed from the HUD difference alone.

The experiment compiled in the optimized Agentic diagnostic. Draft regressions
were prepared for repeated writes, empty/missing attributes, pending mutations
and IME commits, but were not built/run after the performance rejection.
They and the implementation were reverted together; the experiment diff is
preserved at `.utmp/render-audit-20261030/attribute-result-experiment.patch`.
The diagnostic was rebuilt against the restored core. Unity and Godot were
never rebuilt with this rejected experiment and retain their accepted core.

## Next actionable finding

The inventory CSS contains one sibling rule, `.satchel-footer kbd + kbd`.
The current global sibling-dependency flag widens text/src mutations to the
containing family even when the sibling rule targets unrelated footer labels.
An instrumented probe using the restored core visited 62 inventory elements
on 219 of 220 warmed/moving updates. Removing only that rule in a diagnostic
CSS copy reduced those visits to 41–43 on 219 updates; the first update was
54 versus 39. Initial/final mutation-target geometry was identical.

This is attribution evidence, not an optimization: removing authored CSS is
not an acceptable fix, and footer geometry/pixels are outside that comparison.
Instrumented timings are excluded. Both probes preserve their full logs under
`sibling-{original,removed}-stage.log`. The next core candidate is dependency-
specific sibling invalidation, with conservative handling for nested selectors,
filtered ranks, inheritance and structural edits. Keep the authored rule and
prove incremental output against full cascade and Chrome before accepting it.

[Raw paired results and probe counts](attribute-result-20261031.json) preserve
all run means and binary hashes. No ABI or host behavior changed. Existing host
gates remain prior evidence; no new full-gate result is claimed.
