# Chrome performance baseline — October 30, 2026

Added `Tools/performance/compare_chrome.py` and its browser driver. They run
deterministic geometry-only HUD, inventory and settings fixtures at 1280×720,
check initial geometry against Chrome, alternate engine order and retain raw
results. See [the methodology and command](../../Tools/performance/README.md).

## First verified run

Windows 11 x64, MSVC 19.44.35222.0 Release, Chrome 152.0.7977.82.
Core revision `2c27fe25d33554c8cee9242c042f302fb6247cc9`; only benchmark
tooling/docs were added in this change. Five rounds, 200 passes per workload
and engine. All initial div rectangles match exactly (maximum delta 0 px).

Milliseconds, median of five batch means:

| Fixture | Weva fresh document | Chrome DOM replacement + layout | Weva layout update | Chrome forced style/layout |
|---|---:|---:|---:|---:|
| HUD | 0.907 | 0.129 | 0.041 | 0.007 |
| Inventory | 1.665 | 0.343 | 0.538 | 0.045 |
| Settings | 1.020 | 0.138 | 0.105 | 0.020 |

**These are different timing boundaries, not an end-to-end speed ranking.**
Weva includes draw-list preparation; Chrome excludes paint, raster and
compositing. No real fonts, images, host rendering or input/binding dispatch
are measured. Fresh document measurements reuse warm processes and exclude
launch. Chrome replaces an existing DOM, whereas Weva creates a fresh document
and excludes destruction. Batch means are not frame-time p95. No shipping
performance conclusion follows from this small suite.

## Validation and evidence

- Current Release `weva_bench` and `weva_dump` builds succeed without warnings.
- Existing native benchmark CLI CTest passes.
- New runner regression tests: 5 pass, covering rejected measurements,
  deterministic fixtures, geometry mismatch and preservation of failure logs.
- Complete Python tooling discovery: 65 total, 56 pass, 9 Linux-only skips on
  Windows. Existing Linux and host integration receipts are unchanged.
- JavaScript syntax check and real browser/native runs pass.
- Raw final artifacts: `.utmp/chrome-comparison-20261030-verified/`;
  build/test logs: `.utmp/chrome-bench-*.log`. The unsuccessful attempt to use
  an obsolete build directory is preserved, as is the initial timing run.
- Compact machine-readable receipt: [JSON](chrome-performance-20261030.json).

Next meaningful extension: matched rendered host/browser traces with real
fonts and images, including painting and presentation, before publishing
comparative speedup claims. No core or host behavior changed here.
