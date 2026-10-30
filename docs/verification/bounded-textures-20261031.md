# Rejected bounded inactive textures — October 31, 2026

A bounded version of the texture-retention probe did not produce a complete
inventory CPU improvement. No production texture-cache change is retained.

The candidate kept at most 1 MiB of inactive RGBA payload and 128 inactive
entries per document, preferring recent entries. Active textures were never
evicted. Unknown-size and oversized inactive entries were released immediately.
Reload, backend replacement and document destruction used release_all.

Four alternating before/after process pairs used prepared 720p survival UI,
20 warmups, 200 mutating/200 retained frames and 60 Hz pacing. Builds/tests did
not overlap measurements. Median complete CPU means:

| Workload | Before | Bounded cache |
| --- | ---: | ---: |
| HUD | 0.3495ms | 0.3465ms |
| Inventory | 1.0780ms | 1.1065ms |

Inventory was 2.6% slower, with reduced resource-preparation work offset
elsewhere. The added memory is not justified by these results. This also shows
why the earlier temporary probe's 3.1% result was insufficient for adoption.

Initial GCC verification passed all 15 suites, including byte-budget eviction,
LRU touches through get/retain, active oversized entries, unknown sizes,
release_all, stale retains and the entry-count cap. A subsequent 300-selection
identity regression was drafted but not run after rejection. All candidate
source/tests were reverted together. Godot/Unity were not rebuilt with it.

[Raw paired timings and binary hashes](bounded-textures-20261031.json) retain
every result. Evidence and the full reverted diff remain under
`.utmp/render-audit-20261030/bounded-texture-*`. The next experiment revisits
unchanged attribute writes specifically after sibling-subject pruning; the
earlier no-op experiments predated that enabling optimization.
