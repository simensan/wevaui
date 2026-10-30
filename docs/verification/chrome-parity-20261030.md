# Incremental layout against Chrome — October 30, 2026

This follows [the grid pass](chrome-deeper-20261030.md), using `b3701972`
as the before-build. All fourteen matched fixtures and measurement boundaries
remain unchanged. These are geometry workloads, not a claim about complete
game frames, text rendering, GPU drawing or browser feature parity.

## Engine changes

- Keep immutable grid sizing inputs so an individual item's intrinsic exports
  can be updated without laying out its siblings. Changed exports propagate
  unless the grid's explicit width proves an independent block boundary.
- Reuse child layout for grid edge changes only when recomputed track and
  item-area widths are exactly unchanged. Percentage dependencies, ratio
  feedback and unsupported positioning fall back to ordinary layout.
- Retain unchanged ordinary grid/flex item subtrees, with checked incoming
  constraints, font size, height dependencies and intrinsic contributions.
- Pool incremental scratch arenas. Failed speculative layouts still leave the
  live tree untouched, and retained capacity holds no live box references.
  Capacity remains owned by the document; fewer allocations do not imply a
  lower retained-memory footprint.
- Reuse eligible block-tree topology during full edge-only reflow, resetting
  layout state before running the existing full layout pipeline. Inline,
  positioned, replaced, form and orthogonal content remains excluded.
- Walk occupied computed-style properties, avoid unnecessary attr/env string
  copies and skip zero-time transition setup when no transition is running.
  Resolve fixed property names once and reuse equal box-edge resolutions.
- Correct indefinite fractional-grid intrinsic sizing to include spanning
  max-content constraints. The new tracked Chrome hand case covers this fix.
- Compare stable DOM clip/containing-block owners in retained paint snapshots
  and remove obsolete paint-boundary registrations before box IDs are recycled.
  Empty-item reuse exposed both pre-existing dependencies on allocation order;
  the unchanged draw-buffer retention regression checks the corrected behavior.
- Honor line-relative `vertical-align: top/bottom` on inline atoms while
  preserving struts, margin boxes and baseline-aligned participants. The new
  grid oracle exposed this separate defect; its original capture was retained.
  Twelve additional pinned Chrome cases pin alignment expectations.

No C ABI change, added worker threads or benchmark-specific engine rules.

## Measurements

Ten alternating Chrome/Weva rounds and six interleaved native before/after
rounds, each with 1,000 mutations per workload. Reported milliseconds are
medians of batch means, not frame p95. Both native binaries are Windows
x64 Release. No build or test workload ran during these final timings.

The matched boundary includes the setter, synchronous style/layout and bounds
read, with no paint in either engine. Native-only full-update measurements
include paint preparation and exclude the setter in both builds. Neither
boundary measures GPU drawing, game frame pacing, font shaping or image decode.

[The machine-readable receipt](chrome-parity-20261030.json) preserves machine,
fixture and binary identity plus individual samples. The baseline hash matches
the previous receipt for `b3701972`. All fourteen HTML/CSS hashes and measurement
boundaries are unchanged. Initial geometry, both mutation states and final
geometry after every batch agree with Chrome within 0.1px.

**This does not reach Chrome parity across the suite.** Weva is faster in
7 of 14 workloads. Fixed-grid panel and fractional-grid cell updates improve
substantially. Full fractional-grid panel resizing is still about 1.5x Chrome;
autosizing flex is about 2.1–2.4x. Four other workloads are within 0.0013ms
of Chrome. All fourteen improve against the saved native baseline in the
paired layout run, from 3.0% to 98.1%; this is one machine and build pair.

### Matched Chrome layout

| Workload | Weva ms | Chrome ms |
|---|---:|---:|
| hud panel | 0.010197 | 0.008950 |
| hud leaf | 0.007150 | 0.009100 |
| inventory panel | 0.007440 | 0.048500 |
| inventory leaf | 0.007956 | 0.049050 |
| settings panel | 0.021409 | 0.020350 |
| settings leaf | 0.007355 | 0.010700 |
| hud-content panel | 0.010049 | 0.009250 |
| hud-content leaf | 0.010115 | 0.009450 |
| autosize-flex panel | 0.016822 | 0.007950 |
| autosize-flex leaf | 0.020740 | 0.008800 |
| inventory-content panel | 0.008307 | 0.042650 |
| inventory-content leaf | 0.013249 | 0.049750 |
| inventory-fluid panel | 0.391307 | 0.261000 |
| inventory-fluid leaf | 0.014182 | 0.047700 |

### Paired native layout

| Workload | Before ms | After ms | Reduction |
|---|---:|---:|---:|
| hud panel | 0.011876 | 0.010557 | 11.1% |
| hud leaf | 0.008779 | 0.007493 | 14.7% |
| inventory panel | 0.139272 | 0.007447 | 94.7% |
| inventory leaf | 0.009024 | 0.007909 | 12.4% |
| settings panel | 0.024980 | 0.020545 | 17.8% |
| settings leaf | 0.009155 | 0.007475 | 18.4% |
| hud-content panel | 0.022595 | 0.010301 | 54.4% |
| hud-content leaf | 0.011770 | 0.010537 | 10.5% |
| autosize-flex panel | 0.020309 | 0.017237 | 15.1% |
| autosize-flex leaf | 0.024482 | 0.020598 | 15.9% |
| inventory-content panel | 0.445303 | 0.008491 | 98.1% |
| inventory-content leaf | 0.013529 | 0.013117 | 3.0% |
| inventory-fluid panel | 0.449172 | 0.400263 | 10.9% |
| inventory-fluid leaf | 0.455116 | 0.014020 | 96.9% |

### Paired native update including paint preparation

| Workload | Before ms | After ms | Reduction |
|---|---:|---:|---:|
| hud panel | 0.027715 | 0.023720 | 14.4% |
| hud leaf | 0.014271 | 0.012914 | 9.5% |
| inventory panel | 0.300975 | 0.158141 | 47.5% |
| inventory leaf | 0.019446 | 0.017894 | 8.0% |
| settings panel | 0.056585 | 0.056339 | 0.4% |
| settings leaf | 0.013232 | 0.011904 | 10.0% |
| hud-content panel | 0.049164 | 0.035299 | 28.2% |
| hud-content leaf | 0.020270 | 0.018626 | 8.1% |
| autosize-flex panel | 0.039098 | 0.035582 | 9.0% |
| autosize-flex leaf | 0.045961 | 0.041289 | 10.2% |
| inventory-content panel | 0.944864 | 0.499456 | 47.1% |
| inventory-content leaf | 0.035216 | 0.034416 | 2.3% |
| inventory-fluid panel | 0.952410 | 0.888208 | 6.7% |
| inventory-fluid leaf | 0.943627 | 0.038884 | 95.9% |

Full fractional-grid panel update cost improves only 6.7% once paint preparation
is included. The much larger layout-only gains are not end-to-end frame claims.

Raw accepted measurements are `matched-scoped/` and `paired-scoped/`
under `.utmp/chrome-parity-20261030/`. Earlier candidates, failed retention
checks and the initially failing Chrome hand case remain preserved.

## Verification

Raw evidence is retained in `.utmp/chrome-parity-20261030/`.

- Windows Release core: 489,999 checks, zero failures.
- GCC: 15 suites / 702,276 core checks. Clang ASan/UBSan: 17 suites /
  702,270 core checks; six small-stack checks are excluded under sanitizers.
- Qualified full release gate: `full-gate-accepted.log`, exit zero. All 338
  tracked Chrome captures pass the 1.5px ceiling without exemptions: 66 hand,
  47 sample and 225 harvested cases. Browser behavior: 66 pass, zero unexpected
  failures, one existing expected IME failure across 67 scripts.
- The last change restricts ordinary retention to actual subtrees, avoiding
  single-box proxy overhead. Both complete core/corpus suites were repeated
  afterward (`gcc-corpus-final.log`, `clang-corpus-final.log`), along with both
  Windows host builds and Unity full EditMode. All final measurements use that
  restricted build; the matched and paired binary hashes agree.
- Both Windows hosts build without warnings. Unity full EditMode: 244 pass,
  zero failures and two environment-gated inconclusive (`unity-scoped.xml`);
  Native subset: 243 pass with the same two inconclusive.
- All fourteen benchmark workloads pass initial, both-mutation-state and
  post-batch geometry validation in ten final rounds. Maximum disagreement
  is 0.015625px, below the 0.1px threshold. Fixture hashes match the prior receipt.
- Tooling: 70 tests, with nine Linux-only skips on Windows. Qualified Godot
  font, Unicode, export, cache, rendering, input and scene gates pass.
- Inspected the Chrome and native software renders of hand72 and the real Godot
  rendering output (`hand72-native.png`, `render-accepted.png`). Geometry oracle
  metrics and software-renderer font metrics differ; this is not a claim of
  identical Chrome/software pixels.
- Regression coverage includes sparse style diffs, fractional intrinsic spans,
  edge-only grid reuse and rejection, sequential/multiple dirty items,
  repeated full reflow against fresh layouts, retained paint with recycled
  wrapper IDs, and inline atom top/bottom alignment with margins and wrapping.

No new known-gap exemption, ABI change, worker thread or benchmark modification.

## Remaining performance work

Full fractional-grid panel resizing still lays out width-dependent descendants
and rebuilds layout indexes. Autosizing flex still remeasures natural intrinsic
inputs and propagates changed allocation to its parent. Improving these paths
needs reusable dependency/constraint caching with retained-versus-fresh checks;
the current measurements do not establish a safe shortcut for either case.
