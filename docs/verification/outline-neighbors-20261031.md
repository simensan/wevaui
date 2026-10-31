# Rejected outline-neighbor experiment — 2026-10-31

Replacing repeated modulo operations while finding distinct outline neighbors
with conditional wraparound preserved geometry, but did not establish a useful
full-selection improvement. Six alternating fixed-font pairs split **3 wins /
3 losses**, with **-0.325% median paired mean-update change**. The two-line
candidate is removed; no speedup is claimed.

Baseline is `756eeb41`. The generated selection diagnostic is unchanged from
the shaping-buffer experiment: 120 warmups, 6,000 updates per fresh process,
twelve states and twenty mutations, six alternating pairs (72,000 updates).
All setter/final-value/attribute-presence and missing-asset checks pass.
Profiling/logging are disabled and no build/test overlaps timing. Setters are
outside timed core updates. These are fixed-font core intervals, not host frames.

| Pair | Baseline mean (ms) | Candidate mean (ms) |
| --- | ---: | ---: |
| 0 | 0.281790 | 0.274575 |
| 1 | 0.288870 | 0.294828 |
| 2 | 0.272742 | 0.312445 |
| 3 | 0.289569 | 0.295101 |
| 4 | 0.218708 | 0.200394 |
| 5 | 0.233726 | 0.202831 |

Absolute timing drift is visible; no pair is discarded. Allocations remain
604 per final update. The candidate passes all 176 geometry checks with the
same `e45be54e6504f2cd` geometry hash, including scalar-reference AA cases.
This verifies that geometry corpus, not universal pixel equivalence.

Saved original source bytes are restored, with no `libweva` diff. The restored
GCC build and all 15 CTest suites pass. No Unity, Godot or Agentic binary was
built or installed from the candidate. Their accepted rendering improvements
remain intact. The [JSON receipt](outline-neighbors-20261031.json) preserves
all timings and artifact hashes; source snapshots, binaries, logs and raw
results remain in `.utmp/render-audit-20261030/outline-neighbors/`.
