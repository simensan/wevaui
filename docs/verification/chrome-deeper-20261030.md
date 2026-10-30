# Grid constraints and allocation costs — October 30, 2026

This follows [the first layout pass](chrome-close-20261030.md), using commit
`62245b99` as the before-build. The original ten benchmark workloads retain
their inputs and timing boundaries. Four new cases add 100 inventory slots
with nested icon shapes and percentage durability bars, using fixed or
fractional columns. These remain geometry fixtures without font/image backends.

## Engine changes

- Resolve content-independent grid columns before item layout, then lay out each item at its
  actual grid-area width. This avoids a preliminary subtree layout at container
  width and correctly resolves percentage edges against the grid area.
  Fixed minima with fixed or fractional maxima qualify at a definite available
  width. Intrinsic columns and subgrid handover retain their existing sizing paths.
- Record that actual input width for incremental layout proofs. Parent intrinsic
  contributions, outer geometry and baseline checks still guard reuse.
- Calculate minimum and maximum intrinsic contributions together when indexing
  layout, preserving the pre-mutation values needed to validate later reuse.
- Reuse identity-map nodes for layout-only replacements. Structural/modal
  replacements and document resets still remove obsolete identities.
- Walk ordinary block children through stable sibling IDs, avoiding a temporary
  vector allocation per container while preserving source order across recursion.
- Query descendant height dependencies without a heap or recursive traversal
  stack, using cached property IDs; a 4,096-level regression checks the walk.

No C ABI change, extra worker threads or benchmark-specific engine rules.

## Measurements

Six interleaved native before/after rounds and ten alternating Chrome/Weva
rounds, each with 1,000 operations per workload. Milliseconds are medians of
batch means, not frame p95. The saved baseline hash matches the previous
receipt for `62245b99`. Both executables use Windows x64 Release.

Both Chrome and Weva include the attribute setter, synchronous style/layout
and bounds read, excluding paint. Native-only full updates include paint
preparation and exclude the setter in both versions. Neither measures GPU
rendering or game frame latency.

[Compact receipt](chrome-deeper-20261030.json) preserves binary hashes,
machine/fixture identity and individual timing samples. Raw final runs are
`matched-accepted/` and `paired-accepted/`; earlier candidates remain available.

### Paired native layout

| Workload | Before ms | After ms | Reduction |
|---|---:|---:|---:|
| hud panel | 0.011960 | 0.012035 | -0.6% |
| hud leaf | 0.009280 | 0.008971 | 3.3% |
| inventory panel | 0.156039 | 0.137404 | 11.9% |
| inventory leaf | 0.009355 | 0.009715 | -3.9% |
| settings panel | 0.027751 | 0.026125 | 5.9% |
| settings leaf | 0.008851 | 0.008633 | 2.5% |
| hud-content panel | 0.025812 | 0.023610 | 8.5% |
| hud-content leaf | 0.012394 | 0.013224 | -6.7% |
| autosize-flex panel | 0.020420 | 0.021156 | -3.6% |
| autosize-flex leaf | 0.025494 | 0.025161 | 1.3% |
| inventory-content panel | 0.652357 | 0.454916 | 30.3% |
| inventory-content leaf | 0.015059 | 0.015186 | -0.8% |
| inventory-fluid panel | 0.623568 | 0.455473 | 27.0% |
| inventory-fluid leaf | 0.643404 | 0.467662 | 27.3% |

### Matched Chrome layout

| Workload | Weva ms | Chrome ms |
|---|---:|---:|
| hud panel | 0.012175 | 0.009350 |
| hud leaf | 0.008556 | 0.009550 |
| inventory panel | 0.141946 | 0.052200 |
| inventory leaf | 0.009815 | 0.049950 |
| settings panel | 0.023609 | 0.022000 |
| settings leaf | 0.009449 | 0.012700 |
| hud-content panel | 0.023860 | 0.009800 |
| hud-content leaf | 0.011834 | 0.009600 |
| autosize-flex panel | 0.019557 | 0.008200 |
| autosize-flex leaf | 0.023564 | 0.008700 |
| inventory-content panel | 0.448599 | 0.045100 |
| inventory-content leaf | 0.013653 | 0.054200 |
| inventory-fluid panel | 0.448805 | 0.260700 |
| inventory-fluid leaf | 0.455082 | 0.051050 |

### Paired native update including paint preparation

| Workload | Before ms | After ms | Reduction |
|---|---:|---:|---:|
| hud panel | 0.025811 | 0.024098 | 6.6% |
| hud leaf | 0.014534 | 0.014720 | -1.3% |
| inventory panel | 0.306808 | 0.292516 | 4.7% |
| inventory leaf | 0.019395 | 0.020194 | -4.1% |
| settings panel | 0.058069 | 0.057576 | 0.8% |
| settings leaf | 0.013245 | 0.012689 | 4.2% |
| hud-content panel | 0.053248 | 0.046413 | 12.8% |
| hud-content leaf | 0.020326 | 0.019880 | 2.2% |
| autosize-flex panel | 0.040390 | 0.039462 | 2.3% |
| autosize-flex leaf | 0.047840 | 0.046101 | 3.6% |
| inventory-content panel | 1.126784 | 0.945864 | 16.1% |
| inventory-content leaf | 0.034205 | 0.034187 | 0.1% |
| inventory-fluid panel | 1.134598 | 0.962311 | 15.2% |
| inventory-fluid leaf | 1.137517 | 0.957333 | 15.8% |

Some small workloads report modest slowdowns (up to 0.000830 ms in the layout
table); these runs do not establish a universal improvement. All negative
results are retained. Content-dependent whole-grid work remains slower than
Chrome. Fractional-grid leaf updates still require parent layout; safe local
reuse needs separate tracking and propagation of intrinsic-size exports.

The populated fixed-grid panel's final-pass allocation count falls from 726 to
122 (83% fewer calls), with requested bytes falling from 70,938 to 66,362.
These are C++ allocation requests, not process memory or GPU allocations.

## Verification

Raw evidence is retained in `.utmp/chrome-deeper-20261030/`.

- Windows Release core: 488,168 checks, zero failures.
- GCC: 15 suites / 700,445 core checks. Clang ASan/UBSan: 17 suites /
  700,439 core checks (six small-stack checks are excluded under sanitizers).
- All 337 tracked Chrome captures pass the 1.5px ceiling without exemptions,
  including new `hand/71-grid-area-percentage` (65 hand cases).
- Both Windows hosts build without warnings. Unity full EditMode: 244 pass,
  zero failures, two environment-gated inconclusive; Native subset: 243 pass
  with the same two inconclusive.
- All fourteen benchmark workloads pass initial, both-mutation-state and final
  geometry checks against pinned Chrome 152.0.7977.82 in all ten final rounds.
  Maximum disagreement is 0.015625px (1/64px), below the 0.1px threshold.
- Twenty-five Chrome reference cases cover content/border-box percentage edges,
  fixed/fractional tracks, explicit percentage widths, auto margins, nested
  percentage sizes, minimum freezing and overflowing content.
- Regression coverage also checks fused intrinsic results, identity reuse/reset,
  repeated block layout with arena growth, floats, positioned children and clearance.
- Inspected Chrome and native software renders of the new tracked grid fixture;
  percentage bars, slot edges and the deliberately overflowing child agree visually.
- Full qualified local release gate passes (exit 0), with pinned Windows Chrome
  and the patched Linux Godot editor/templates under private Xvfb. Browser
  behavior: 66 pass, no unexpected failures, one documented IME known failure.
  Godot Unicode/font, rendering, exported-library/pixel and scene checks pass.
  The final `forms-live` Godot render was inspected, with correctly placed inputs,
  wrapped notes, scrolling log, dropdown, button and tooltip.
- Windows tooling: 70 tests, 61 pass and nine Linux-only skips.
- Main receipt: `full-gate-final.log` / `full-gate-final.exit`; detailed Linux
  gate and CTest logs are preserved in `linux-gate-logs/` alongside Windows
  build logs and Unity XML, so evidence survives temporary-directory cleanup.

The first new block traversal fixture lacked the explicit block display rule
needed by its standalone cascade (which has no user-agent stylesheet). Its
incorrect inline-layout expectations failed and led to an invalid test lookup;
the fixture now establishes the intended display and checks existence before
accessing geometry. Failed logs are retained. A separate Chrome capture also
records an auto-width/auto-margin grid case for later comparison; this pass
makes no additional parity claim for that case.
