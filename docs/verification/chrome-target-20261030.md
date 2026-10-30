# Repeated layout work against Chrome - October 30, 2026

This follows [the incremental pass](chrome-parity-20261030.md), with `ca1748c6` as the before-build. All fourteen fixture hashes and measurement boundaries are unchanged. The baseline binary hash matches that receipt.

## Shared-engine changes

- Memoize numeric box constraints against exact style/context/constraint keys. Allocate on reuse, rather than on every first layout. Height resolution retains its own percentage basis.
- Reuse natural flex measurements, then validate the final allocation before retaining a subtree. Changed incoming widths require fixed child constraints and unchanged root edges. Percentage constraints and auto margins keep ordinary layout.
- Refresh geometry-dependent indexes after validated topology-preserving edge reflow. Preserve identity maps and unchanged dependency information.
- Cache bounded inline declaration syntax; continue resolving variables, attributes and environment values on every cascade. Retain parsed owned syntax in scratch styles with separate initial/inherited semantics and packed two-bit state.
- Reuse compact eligibility and traversal scratch buffers. Clear borrowed DOM pointers on every return path. Resolve fixed grid/flex property names once and skip default inset/font work when unnecessary.

No C ABI change, additional worker threads, benchmark-specific rules or host-specific layout logic. Caches retain bounded storage; lower allocation counts do not imply lower retained memory.

## Matched runtime measurement

Ten alternating Chrome/Weva rounds, 1,000 mutations per workload, on Windows with pinned Chrome 152. Values are medians of batch means, not frame p95. Both include the attribute setter and layout; neither includes paint, rasterization or presentation. Initial geometry, both mutation states and timed final geometry agree within 0.1 px.

13/14 measured medians are no slower than Chrome. Autosizing-flex leaf updates remain 0.000971 ms (11.2%) slower: 0.009621 ms versus 0.008650 ms. The target of no slower workload is not fully met. This does not establish an always-faster guarantee across runs, pages or hardware. The separate paired native comparison uses six interleaved rounds with the same 1,000 mutations.

| Workload | Weva ms | Chrome ms | Paired reduction vs previous Weva |
|---|---:|---:|---:|
| hud panel | 0.006391 | 0.009950 | 38.2% |
| hud leaf | 0.005312 | 0.008500 | 27.2% |
| inventory panel | 0.006772 | 0.048500 | 5.1% |
| inventory leaf | 0.006242 | 0.050300 | 19.8% |
| settings panel | 0.012866 | 0.022150 | 48.4% |
| settings leaf | 0.005525 | 0.011050 | 25.0% |
| hud-content panel | 0.006795 | 0.009950 | 29.5% |
| hud-content leaf | 0.007622 | 0.009150 | 25.8% |
| autosize-flex panel | 0.007203 | 0.008100 | 53.3% |
| autosize-flex leaf | 0.009621 | 0.008650 | 53.2% |
| inventory-content panel | 0.007678 | 0.046350 | 8.9% |
| inventory-content leaf | 0.010094 | 0.050750 | 26.1% |
| inventory-fluid panel | 0.250801 | 0.258600 | 32.9% |
| inventory-fluid leaf | 0.011661 | 0.044150 | 21.5% |

Separate native measurements including paint preparation range from 1.9% to 37.0% reduction in median batch means. Small differences should not be treated as robust gains. These measurements still exclude host/GPU presentation and game load.

## Cold creation check

Ten interleaved native rounds, 100 independent document creations per fixture, with raster reuse disabled. These are the same simple benchmark fixtures, not host startup or the realistic Frontier screen. Milliseconds are medians of batch means; negative reductions are regressions and are retained below.

| Fixture | Previous ms | Current ms | Reduction |
|---|---:|---:|---:|
| hud | 1.050 | 1.035 | 1.3% |
| inventory | 1.938 | 1.942 | -0.3% |
| settings | 1.186 | 1.113 | 6.2% |
| hud-content | 1.129 | 1.138 | -0.8% |
| autosize-flex | 1.058 | 1.067 | -0.8% |
| inventory-content | 4.008 | 4.110 | -2.5% |
| inventory-fluid | 4.186 | 4.167 | 0.5% |

An earlier eager cache allocation added about 0.9 MB to the large grid fixtures; the accepted implementation defers allocation until reuse. An intermediate byte-per-property cache state was replaced by two-bit packing. Raw earlier runs remain under the evidence directory; this report uses only the final binary hashes.

## Verification

- Windows Release core: 492,088 checks, zero failures.
- GCC: 15 CTest suites; 704,365 core checks. Clang ASan/UBSan: 17 suites; 704,359 core checks. Zero build warnings.
- All 338 tracked Chrome captures satisfy the 1.5 px ceiling without known-gap exceptions. Browser behavior: 66 pass, zero unexpected failures, one existing IME exception.
- Full qualified `check.sh --release` passes, including native plugins, Godot font/Unicode checks, exports, rendering comparisons and live scenes. The resulting UI render was inspected.
- Both Windows hosts rebuilt. Full Unity EditMode: 244 passed, zero failed, two environment-gated inconclusive.
- Windows tooling: 70 tests, 61 passed and nine Linux-only skips.

New regressions exercise numeric context changes, memo eviction/moves, retained-vs-fresh flex geometry and paint, changed natural/final widths, variable child constraints, inline var/attr/env resolution and scratch-style ownership/packing boundaries.

## Evidence

- [Machine-readable measurements](chrome-target-20261030.json)
- Local evidence: `.utmp/chrome-target-20261030/`.
- Final timings: `accepted-matched/`, `paired-accepted/`, `cold-accepted/`.
- Final gate: `full-gate-verified.log`, `linux-gate-verified-logs/`, `chrome-checks-verified/`, `unity-verified.xml`, `accepted-core-rebuilt.log`, `render-verified.png`.
- Earlier provisional timings and failed build evidence were preserved.
