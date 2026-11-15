# Matched Chrome comparison after flex optimization — 2026-10-31

The optimized core still has an inventory style/layout gap in this prepared
survival workload: **0.336 ms versus Chrome's 0.242 ms**, about 39% more CPU
in these selected stages. HUD style/layout is lower: 0.128 versus 0.185 ms.
This does not establish an overall rendering speed ratio or universal parity.

| Warm CPU work, milliseconds/update | HUD | Full inventory selection |
| --- | ---: | ---: |
| Weva cascade + box building + layout | 0.128 | 0.336 |
| Chrome style/layout | 0.185 | 0.242 |
| Weva core paint elapsed | 0.093 | 0.345 |
| Chrome prepaint/paint/layer recording CPU | 0.165 | 0.286 |
| Chrome raster-worker CPU | 0.035 | 0.114 |
| Host-engine controller/update/capture/prepare/encode/submit CPU | 0.643 | 1.367 |

Values are medians of three per-process means. Style/layout sums its stages
within each run before aggregation; incremental layout is charged to boxes.
The final row has no measured Chrome counterpart. Weva elapsed intervals and
Chrome per-thread union CPU have different boundaries and parallelism. Do not
sum rows or infer GPU execution, compositor work or presentation latency.

## Reproduction and verification

Weva checkout `a22e5b63` contains production core change `a0943b78`; the host engine's
leased diagnostic branch is `f0a91651a`. The native executable hash exactly
matches the candidate used in the accepted paired flex-row measurements.
Chrome is pinned to 152.0.7977.82, on Windows RTX 5080. This measurement changes
no production code, host adapter, ABI or global SDK selection. Unity and Godot
keep the shared core already built and verified in the
[implementation receipt](flex-row-allocation-20261031.md).

Three fresh-process rounds alternate native/browser order. Each process uses
20 warmups, 200 changing updates and 200 retained frames for HUD and inventory.
No builds or tests overlap timing. Both engines consume the same prepared
HTML/CSS, Open Sans font bytes, PNGs and text/attribute/removeAttribute states,
at 1280×720 on black. All fixture hashes match across runs and native inputs.
The existing prepared runner and trace summarizer are unchanged.

Requested cadence is 60 Hz. Actual mean intervals are 16.998–17.054 ms for
native changing frames and 16.597–16.599 ms across browser phases. These are
cadence, not work time. Every process completes; mutation targets are required.
Initial/final target geometry differs by at most **0.753662 px** in all twelve
comparisons. The Chrome inventory render was inspected. This is not a full
pixel-equivalence or every-frame native final-value assertion.

The fixture's unbound Depth is empty in both engines. Native rendering clears
its target each draw; its retained path still submits host rendering even when
the core publishes no new work. Chrome uses its own retained compositor policy.

## Interpretation and next target

Inventory stage means are 0.124 ms cascade, 0.212 ms box/incremental layout,
0.345 ms core paint, and 0.687 ms complete core update. Controller/update is
0.877 ms. Host draw is 0.491 ms, including 0.062 ms snapshot capture, 0.125 ms
resource/batch preparation, 0.146 ms encoding and 0.154 ms queue submission CPU.
These are separately aggregated medians and need not add exactly. Resource
preparation is CPU work despite the diagnostic's historical `host prepare GPU`
label. Retained native CPU is 0.291 ms HUD and 0.357 ms inventory; omitted
Chrome compositor/GPU work prevents a complete retained-path comparison.

The previous batch has lower absolute times for both engines. It must not be
used as this candidate's baseline or to calculate a percentage improvement.
The paired [Unity/host-engine](flex-row-allocation-20261031.md) and
[Godot](flex-row-godot-20261031.md) runs provide that evidence separately.

Paint reconstruction now costs more than box/incremental layout in the selected
inventory workload. Profile its remaining rebuilt geometry and text work before
another optimization; do not remove CSS effects or change content to favor Weva.
The measured gap and missing GPU/presentation scope remain explicit.

The [JSON receipt](optimized-render-comparison-20261031.json) preserves every
run, binary/revision/fixture hashes, cadence and geometry comparison. Raw logs,
traces and screenshots are under
`.utmp/render-audit-20261030/optimized-chrome-comparison/`; the reproduction
scripts `run_optimized_comparison.py` and `summarize_optimized_comparison.py`
are alongside it. Earlier evidence remains intact.
