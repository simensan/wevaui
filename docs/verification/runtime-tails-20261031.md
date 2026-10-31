# Runtime tail attribution — October 31, 2026

The mixed inventory p95 result does not isolate a renderer resource-upload
regression. The slow frames in after-round 0 have higher combined UI, worker UI,
scene-render and render-wait intervals together. This is an association, not
proof of system interference or any specific engine cause. The regression
remains in the published results; no samples are removed.

`Tools/performance/analyze_agentic_runtime_tails.py` reads and hash-verifies the
30 original frame artifacts from the [runtime comparison](current-runtime-20261031.md).
It reports means for every frame, the middle 5% and slowest 5% ranked by wall
interval, plus chronological 100-frame blocks. For these 600-frame profiles,
each ranked group contains 30 frames. Sections overlap and must not be summed.
The recorded same-frame worker interval need not be the cause of that frame's
wall interval in a threaded renderer.

Inventory after-round 0, mean milliseconds within each group:

| Group | Frame interval | Combined UI | Worker UI | Scene render | Render wait |
| --- | ---: | ---: | ---: | ---: | ---: |
| Middle 5% | 1.2927 | 0.7481 | 0.1901 | 0.4112 | 0.3427 |
| Slowest 5% | 2.1013 | 1.1119 | 0.2835 | 0.6094 | 0.4915 |

After-round 1 has a different shape: middle/slowest frame intervals are
1.2457/1.5488 ms, while combined UI means are 0.7179/0.7334 and scene render means
0.4005/0.3982. No single stage explains every tail. In before-round 2, the
slowest group's frame interval averages 7.7136 ms, combined UI 1.6888 and render
wait 1.5456; CPU section timing alone does not account for that wall interval.

Correction: the historical UI section includes main and worker work, so it
cannot establish the largest main-thread stage. The follow-up instrumentation
[separates the costs and fixes the accounting](runtime-accounting-20261031.md).
The next optimization investigation should split its core update and snapshot
cost in the actual threaded runtime. The earlier standalone diagnostic already
has that split, but using its proportions as runtime evidence would be unsound.
Do not revive the rejected inactive-texture cache solely from these tails.

Verification: all 30 original frame hashes passed; synthetic 100-frame input
verified middle/slowest window selection and means, and modified payload was
rejected. The [JSON receipt](runtime-tails-20261031.json) retains all six
inventory analyses. The full 30-profile analysis remains locally at
`.utmp/render-audit-20261030/runtime-tail-analysis.json`. No production changes,
new performance claim, host rebuild or full-gate claim in this chunk.
