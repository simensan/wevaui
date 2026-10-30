# Matched Weva and Chrome layout benchmarks

Run identical, deterministic geometry-only HUD, inventory and settings fixtures
through the native core benchmark and headless Chrome on the **same OS**.
Both time an attribute mutation followed by completed synchronous layout and a
target-bounds read. Neither includes painting. This compares layout operations,
not game-host FPS. Schema 2 replaces the unfair schema 1 full-update comparison;
old receipts are retained as historical evidence, not comparable results.

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
The required `weva_dump` must be from the same build. Initial fixture geometry
is checked against Chrome (all div rectangles, 0.1 px tolerance); a mismatch
rejects the comparison. This check is outside the timed region.

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
