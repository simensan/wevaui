# Weva and Chrome performance baselines

Run identical, deterministic geometry-only HUD, inventory and settings fixtures
through the native core benchmark and headless Chrome on the **same OS**.
This is a starting point for comparing synchronous UI work, not a browser versus
game-host FPS contest. It deliberately prints no speedup ratios.

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
| `cold` | Document create, CSS/HTML parse, initial update and draw-list preparation. Shared raster cache disabled; destruction excluded. | Replace document HTML/CSS, force initial style/layout via a geometry read. Browser process/tab already exists; old DOM replacement/cleanup may contribute. |
| `layout` | Alternate target padding between 11 and 12 px, then time `weva_document_update`. Setter excluded. | Same attribute mutation, then time a forced geometry read. Setter excluded. |

Both use 1280×720 CSS pixels. Chrome device scale is 1. No web fonts, text,
images, scripts, animations or network resources are in these fixtures: Weva's
host-owned fonts and Chrome fonts would otherwise differ. These are simplified
game UI shapes, not full production-screen performance measurements.

Weva performs additional draw-list/texture work. Chrome's synchronous geometry
read excludes paint, GPU raster, compositing and presentation. Browser timing is
quantized by `performance.now()`; tiny results can round to zero. Native stdout
rounds batch means to 0.001 ms. Neither best times nor these means establish
frame-time p95. The summary uses the median of **batch means**, preserving all
batches in JSON. There is no discarded warmup: each runtime scenario gets an
initial untimed layout, then measures all requested passes; JIT/cache warmup
within those passes remains part of the result. Engine order alternates by round
to reduce order bias; workloads run sequentially, not concurrently.

Use these results to track each engine's changes on the same fixtures and to
locate large cost differences. Before making a claim that one is faster, extend
the suite with matched real font/image backends and rendered host/Chrome frame
traces covering equivalent paint and presentation work. Cold application startup,
typing, scrolling, binding dispatch and actual HUD text updates are not covered.

Runner checks: `python -m unittest discover -s Tools/tests -p test_chrome_performance.py`.
