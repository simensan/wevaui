# Current-core prepared rendering comparison — 2026-10-31

The measured `bcb337f1` baseline still spends more CPU time on inventory style/layout than
Chrome in the prepared survival workload. HUD style/layout is smaller in this
batch. This refresh replaces the earlier Chrome comparison;
it is not a paired before/after optimization result or an overall speed ratio.

The subsequent [flex-row allocation change](flex-row-allocation-20261031.md)
has separate paired host measurements. This Chrome table predates that change.

| Warm work, milliseconds/update | HUD | Full inventory selection |
| --- | ---: | ---: |
| Weva cascade + box building + layout | 0.094 | 0.366 |
| Chrome style/layout | 0.119 | 0.167 |
| Weva core paint elapsed | 0.067 | 0.303 |
| Chrome prepaint/paint/layer recording CPU | 0.116 | 0.216 |
| Chrome raster-worker CPU | 0.026 | 0.096 |
| Agentic controller/update/capture/prepare/encode/submit CPU | 0.502 | 1.221 |

Values are medians of three per-run means. Weva style/layout is summed within
each run before aggregation; incremental layout is charged to the box stage.
The final row has no measured Chrome counterpart. Weva elapsed intervals and
Chrome summed worker CPU have different boundaries and parallelism, so do not
add the rows or derive a complete-path speed ratio. GPU execution, compositor
work and presentation latency are not covered by the selected Chrome slices.

## Workload and verification

Weva `43f59689`, Agentic diagnostic branch `f0a91651a`, Chrome
152.0.7977.82, Windows RTX 5080. The optimized diagnostic was rebuilt in its
existing leased worktree using explicit `WEVA_SOURCE_DIR` pointing at this
checkout. No engine source, SDK selection, host adapter or ABI changed.
Godot and Unity therefore retain the shared core verified by the preceding
[integration gate](render-audit-integration-20261031.md).

Three rounds use fresh native and browser processes, alternating engine order
(native/browser, browser/native, native/browser). Each process measures HUD
then inventory, with 20 warmups, 200 changing updates and 200 retained frames
per case. No builds or tests overlap timing. Both engines use identical
prepared HTML/CSS, Open Sans font bytes, PNGs, black background, 1280×720
viewport and text/attribute/removeAttribute mutations. Every fixture hash is
checked across browser rounds and against the native input directory.

The target cadence is 60 Hz. Actual per-run mean intervals are 17.002–17.038 ms
for native changing frames and 16.597–16.599 ms across browser phases. Cadence
is reported, not treated as work time or input latency. Native setters check
their return status; both runners reject missing mutation targets. All six
processes completed successfully. Initial/final target geometry differs by
at most **0.753662 px** across twelve comparisons. The current Chrome inventory
capture was inspected. This is not a full pixel-equivalence assertion or a
native final-value verification on every frame.

The browser resolves the fixture's unbound `Depth` field to empty, matching
native binding refresh. Native rendering clears the target before each draw.
The retained path continues host rendering even when the core publishes no
new work, unlike Chrome's retained compositor policy.

## What remains expensive

The current inventory breakdown is 0.099 ms cascade, 0.267 ms box/incremental
layout and 0.303 ms paint. Its core total is 0.674 ms. Controller plus update
is 0.828 ms; host work is 0.393 ms, including 0.043 ms snapshot capture,
0.095 ms resource/batch preparation, 0.104 ms encoding and 0.150 ms queue
submission CPU. These are medians of separate per-run stage means; independent
medians need not sum exactly to the median total. "Resource preparation" does
not mean GPU execution despite the diagnostic's historical `host prepare GPU`
label.

Native unchanged-frame CPU is 0.238 ms HUD and 0.278 ms inventory. Chrome has
no measured retained style/layout or raster work and about 0.010 ms recording
slices, but omitted compositor/GPU work prevents a complete idle comparison.

The next substantial shared target remains inventory incremental box/layout
and paint reconstruction. The [title invalidation audit](inventory-invalidation-20261031.md)
identifies overly broad replacement work, while the
[rejected probe shortcut](stretched-probe-host-20261031.md) demonstrates that
removing a speculative pass alone did not establish a host-level win. This
measurement does not justify changing CSS or removing effects to favor Weva.

## Evidence and reproduction

[Machine-readable receipt](current-render-comparison-20261031.json) includes
binary/revision/fixture hashes, every stage result, cadence and geometry checks.
Local raw evidence is `.utmp/render-audit-20261030/current-chrome-comparison/`;
`run_current_comparison.py`, `summarize_current_comparison.py`, and
`current-chrome-agentic-build.log` are beside it. No earlier run is overwritten.

The browser uses `Tools/performance/render_prepared_chrome.cjs` followed by
`Tools/performance/summarize_render_trace.py`. Native uses the rebuilt
`weva_cost` with the same five prepared files and
`WEVA_BENCH_INTERVAL_MS=16.6666666667`; diagnostic paint/font logging and optional
GPU timestamp passes are disabled. The existing trace-accounting tests pass.
