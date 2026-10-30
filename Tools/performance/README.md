# Matched Weva and Chrome layout benchmarks

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
