# Prepared rendering comparison — October 31, 2026

The matched prepared fixtures expose a remaining inventory style/layout gap.
This is a measurement correction, not a production optimization claim.

| Warm mutation work, ms/update | HUD | Inventory |
| --- | ---: | ---: |
| Weva cascade + boxes + layout | 0.103 | 0.634 |
| Chrome style/layout | 0.075 | 0.136 |
| Weva core paint elapsed | 0.059 | 0.257 |
| Chrome paint recording CPU | 0.084 | 0.182 |
| Chrome raster worker CPU | 0.019 | 0.073 |
| Agentic controller/update/prepare/submit CPU | 0.415 | 1.494 |

Values are medians of three per-run means; Weva style/layout sums the three
stage medians. Partial layout is charged to boxes. Chrome CPU slices exclude
parts of compositor/GPU-process work, so the final row has no Chrome total
counterpart. Core paint elapsed and Chrome worker CPU are also different
boundaries; do not sum these into an overall speed ratio. GPU execution and
presentation latency remain unmeasured by this Chrome trace.

## Workload and corrections

Both use the prepared survival HTML/CSS, PNG icons, Exported font, 1280x720,
black background, and identical query/setAttribute/removeAttribute/textContent
mutations. There are 20 warmups, 200 mutations and 200 retained frames per run.
Target cadence is 60 Hz; actual Agentic cadence is recorded in the JSON.
Chrome is pinned 152.0.7977.82; Agentic uses Vulkan on RTX 5080, driver 617.42.
Runs are sequential, not concurrent. This is one machine and two workloads.

The browser adapter resolves the fixture's unbound `{{ Depth }}` to empty,
matching Agentic binding refresh; unsupported binding expressions fail.
Absolute fixture asset paths become local HTTP paths. No game-world image,
extra focus state or alternate SVG assets are injected.

The native diagnostic now clears its target before every draw: its previous
CPU loop accumulated transparent UI over prior frames. The clear is included
in native submission CPU time and remains outside optional UI GPU timestamps.
Production rendering is unchanged. Both initial and final mutation-target
bounds agree within 0.754px across all rounds. Chrome HUD/inventory captures
were inspected; this is not a full pixel-equivalence assertion.

Unchanged Agentic frames cost 0.189/0.201ms CPU (HUD/inventory), because the
game host still submits the UI. Chrome recorded no retained style/layout or
raster work, with 0.010/0.009ms paint-recording slices. Different redraw
policies prevent an equivalent idle total comparison.

## Evidence and verification

[Machine-readable results](prepared-render-comparison-20261031.json) retain
every round, input hashes, cadence and geometry differences. Local evidence:
`.utmp/render-audit-20261030/chrome-prepared-v2/`,
`prepared-agentic-v2-{0,1,2}.log`, `matched-render-clear-build.log`.
Earlier `chrome-prepared/` and `prepared-agentic-{0,1,2}.log` are preserved
but excluded because they precede binding/background/clear corrections.

The diagnostic builds successfully; three trace-summary tests pass and the
browser runner passes syntax validation and all twelve measured phases.
No core, ABI, Unity or Godot behavior changed, so their prior suite results
are not represented as fresh full-gate runs.

Run the browser side with `node Tools/performance/render_prepared_chrome.cjs
FIXTURES NEW_OUTPUT 3`, then `python Tools/performance/summarize_render_trace.py
NEW_OUTPUT`. Run Agentic `weva_cost` with the same five prepared files and
`WEVA_BENCH_INTERVAL_MS=16.6666666667`.

The next shared optimization target is inventory cascade/box reconstruction.
Any correction belongs in libweva and must be checked through both hosts;
these measurements do not justify an Agentic-only CSS workaround.
