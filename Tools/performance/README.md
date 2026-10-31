# Matched Weva and Chrome layout benchmarks

For the opt-in Unity Windows player CPU/GPU frame diagnostic, use
`run_unity_player_timing.py --unity EDITOR --fixtures FIXTURES --output NEW_OUTPUT --fps 60`.
It records whole-player presented frames with a background control, GPU sample
availability and post-timing captures. It is separate from the matched layout
comparison below; see the [timing receipt](../../docs/verification/unity-player-timing-20261031.md)
for boundaries, measured results and retained failed probes.

Run identical, deterministic geometry-only HUD, inventory and settings fixtures
through the native core benchmark and headless Chrome on the **same OS**.
Both time an attribute mutation followed by completed synchronous layout and a
target-bounds read. Neither includes painting. This compares layout operations,
not game-host FPS. Schema 2 replaced the unfair schema 1 full-update comparison.
Schema 3 keeps the six schema 2 workloads and timing boundaries unchanged,
adds content-bearing fixtures and checks geometry after mutation. Original
fixtures are byte-identical, with baseline hashes pinned by tests.

Build a current Release `weva_bench` using [the core benchmark instructions](../weva_bench/README.md).
Install the pinned root Node dependencies with `npm ci` if needed. Then:

```powershell
$env:WEVA_CHROME = 'C:/path/to/qualified/chrome.exe'
python Tools/performance/compare_chrome.py --weva-bench build-core/Tools/weva_bench/Release/weva_bench.exe --weva-dump build-core/Tools/weva_dump/Release/weva_dump.exe --out .utmp/chrome-comparison-01 --runs 5 --passes 200
```

The output directory must be new. Failures stop the run and retain logs; a failed
or partial run does not produce a summary. The runner saves generated HTML/CSS,
SHA-256 hashes, native binary hash, source revision/dirty state, machine identity,
Chrome version, raster worker overrides, individual batch results and a Markdown
summary. The source revision identifies the checkout; it does **not** prove that
an arbitrary supplied binary was built from it. Rebuild before measuring and
retain that build log. Run on an otherwise idle machine with stable power settings.
The required `weva_dump` must be from the same build. All div rectangles are
checked against Chrome with a 0.1 px tolerance: initial geometry, both alternating
mutation states, and the final state of every measured batch. A mismatch rejects
the comparison. These checks are outside timing. `weva_bench` must support
`--geometry-dump`; a missing dump fails rather than weakening validation.
Native one- and two-pass preflight runs verify both mutation states. This matters
because an even pass count ends at 12px, matching the original panel padding:
checking only the final state could accept a mutation that did nothing.

## Fixtures

The original HUD, inventory and settings fixtures retain empty fixed-size
cells so previous timings remain comparable. Their leaf cases exercise layout
bookkeeping even when no visible content moves. Additional fixtures avoid
mistaking that case for normal content-bearing UI:

- `hud-content`: each fixed-size cell contains a visible track and percentage-width
  fill. Cell padding moves and resizes the nested bars.
- `autosize-flex`: centered, naturally sized flex items contain fixed-size icons.
  Cell padding changes the item's intrinsic size and moves neighboring items;
  retaining the parent's old allocation would be wrong.

- `inventory-content`: 100 fixed-track slots contain icon shapes and percentage
  durability bars; changing padding moves real descendants.
- `inventory-fluid`: the same content uses fractional tracks, so panel padding
  also changes allocated slot widths.

Each fixture runs panel and leaf mutations, for fourteen workloads total. The
post-timing Chrome snapshots and native preflight runs do not change the initial
style/cache state of a measured batch.

## Boundaries

| Scenario | Weva | Chrome |
|---|---|---|
| `panel` | Time `set_attribute(style)` + `weva_document_update_geometry` + bounds read on the panel. | Time `setAttribute(style)` + `getBoundingClientRect()` on the panel. |
| `leaf` | Same operation on one final cell. | Same operation on the same cell. |

Both alternate exactly the strings `padding-left:11px` and `padding-left:12px`.
String construction and target lookup are outside timing; attribute parsing,
invalidation and layout are inside. Weva's existing geometry-only API performs
no paint publication. The benchmark checks that the draw serial, draw count
and texture count remain zero; the runner rejects old binaries without the
matched-boundary marker. Cold creation is intentionally removed: replacing a
browser document and creating a native document have different lifecycle costs.

Both use 1280×720 CSS pixels. Chrome device scale is 1. No web fonts, text,
images, scripts, animations or network resources are in these fixtures: Weva's
host-owned fonts and Chrome fonts would otherwise differ. These are simplified
game UI shapes, not full production-screen performance measurements.

Both exclude draw-list preparation, paint, GPU raster, compositing and
presentation. Chrome includes the JavaScript-to-DOM call; Weva includes its C
API call. Browser timing is quantized by `performance.now()`; tiny individual
results can round to zero. Native stdout rounds batch means to 0.000001 ms.
Neither best times nor these means establish
frame-time p95. The summary uses the median of **batch means**, preserving all
batches in JSON. There is no discarded warmup: each runtime scenario gets an
initial untimed layout, then measures all requested passes; JIT/cache warmup
within those passes remains part of the result. Engine order alternates by round
to reduce order bias; workloads run sequentially, not concurrently.

Use these results to compare mutation-to-layout latency on these fixtures and
locate large cost differences. Before making an overall rendering/FPS claim, extend
the suite with matched real font/image backends and rendered host/Chrome frame
traces covering equivalent paint and presentation work. Cold application startup,
typing, scrolling, binding dispatch and actual HUD text updates are not covered.

Runner checks: `python -m unittest discover -s Tools/tests -p test_chrome_performance.py`.

## Rendered Godot frames

The survival sample has a separate real-render benchmark:
[`render_bench.gd`](../../hosts/godot/project/samples/western_survival/render_bench.gd).
It measures controller/update CPU, renderer CPU setup, viewport render CPU/GPU,
draw calls and wall-frame intervals. It keeps the native background visible
while hiding Weva for a background-only control.

```powershell
$env:WEVA_RENDER_BENCH_OUT = "$PWD/.utmp/render-results.json"
godot --path hosts/godot/project --script res://samples/western_survival/render_bench.gd --rendering-method mobile --resolution 1280x720 --audio-driver Dummy --log-file "$PWD/.utmp/render-engine.log"
```

Create the output directory first. The JSON includes raw samples and frame IDs;
PNG captures are saved beside it outside measurement. Defaults: three rounds,
600 measured frames per case after 120 warmups; reverse case order every other
round. Override with `WEVA_RENDER_BENCH_FRAMES` and `WEVA_RENDER_BENCH_ROUNDS`.
Cases are background-only, idle HUD, a meter/text change every frame, idle
inventory, and an inventory selection every frame (an input stress case).
Automatic sample/document processing and user interaction are disabled; the
probe applies exactly one controlled update per rendered frame.

Use a real visible rendering window, not headless, minimized or occluded output.
The probe disables VSync and the frame cap. Repeat with
`WEVA_RENDER_BENCH_GPU=0` to measure without timestamp collection overhead.
Unavailable GPU timestamps are explicitly flagged, not interpreted as free
rendering. Timings are asynchronous phase distributions: do not add CPU and
GPU medians or subtract p95 values to derive frame cost.

For separate CPU draw-packing/submission diagnostics, set
`WEVA_GODOT_DRAW_LOG=1` and correlate its frame IDs with the JSON. This logging
perturbs timing, so keep it out of the main latency run. A retained idle canvas
may have no Weva draw callback while still being drawn by the GPU every frame.
Viewport CPU excludes global render setup and document/controller processing;
the JSON records those boundaries separately. See the
[Godot timing API](https://docs.godotengine.org/en/stable/classes/class_renderingserver.html#class-renderingserver-method-viewport-get-measured-render-time-cpu)
and [measured receipt](../../docs/verification/rendering-20261030.md).

This is a warm 2D sample measurement on the reported GPU/driver, not a busy 3D
game, cold shader compilation, or a matched Chrome rendering comparison.

## Same-scene Chrome rendering diagnostic

Export the live sample state and actual fallback font with Godot:

```powershell
$env:WEVA_RENDER_EXPORT_OUT = "$PWD/.utmp/fixture.json"
godot --headless --path hosts/godot/project --script res://samples/western_survival/render_export.gd
node Tools/performance/render_chrome.cjs .utmp/fixture.json .utmp/chrome-render-new 180 3
python Tools/performance/summarize_render_trace.py .utmp/chrome-render-new
python -m unittest discover -s Tools/performance -p test_render_trace.py
```

Use `WEVA_CHROME` to select the reference executable. The browser output directory
must not already exist. Keep the exported JSON and adjacent `.ttf` together.
Chrome uses the sample HTML/CSS/images, exported controller states and font, at
1280x720 with 60 scheduled mutations per second. It saves real screenshots,
element rectangles, GPU configuration and raw traces. Raster CPU is summed
across worker threads; nested intervals within each thread are counted once.
Only the marked measurement window and target renderer process are counted.
GPU-process CPU slices are **not GPU hardware time**; GPU execution is reported
unavailable. Tracing perturbs CPU work, so this is a stage diagnostic, not a
maximum-throughput or input-to-photon benchmark.

For the corresponding Godot run, set `WEVA_RENDER_BENCH_FPS=60`,
`WEVA_RENDER_BENCH_FRAMES=180`, and `WEVA_RENDER_BENCH_ROUNDS=3`.
The default frame limit remains zero (uncapped). An idle Chrome page can reuse
composited content without raster work; Godot continues rendering its retained
canvas. Compare stage boundaries explicitly, never a Chrome setter timer against
a full Weva update or CPU raster timings against GPU timestamps.
See [the comparison receipt](../../docs/verification/rendering-chrome-20261030.md).

## Agentic CPU submission comparisons

`compare_agentic_cost.py BEFORE_EXE AFTER_EXE NEW_OUTPUT_DIR` alternates eight
runs of Agentic's `weva_cost` diagnostic and preserves raw stdout/stderr and
binary hashes. Use `--rounds N` to change the count; append `--args` followed by
arguments passed identically to both executables. The authored fixture mode
accepts four paths: HUD HTML/CSS and inventory HTML/CSS. Assets and fonts must
resolve completely; bind a changing number to `Depth`. These are number-update
workloads, not selection, GPU completion, FPS or Chrome-equivalent rendering.
See [the Agentic receipt](../../docs/verification/agentic-packed-20261030.md)
for exact boundaries, asset conversion, rejected runs and remaining work.

Agentic’s optional fifth authored argument is a JSON mutation sequence. Set
`WEVA_GPU_TIMING=1` for separate retained GPU timestamps and
`WEVA_BENCH_WIDTH`/`WEVA_BENCH_HEIGHT` for resolution. The
[GPU/selection receipt](../../docs/verification/agentic-gpu-20261030.md) records
actual hardware intervals and distinguishes them from changing-frame CPU cost.
