# Closing the Chrome layout gap — October 30, 2026

This pass reduces all six original matched mutation-to-layout workloads by
42–93.5% in an interleaved native before/after run. It changes shared engine
behavior internally, without changing the C ABI or adding workers.

## Research and implementation

Blink distinguishes local style changes from inherited and descendant-affecting
changes, and validates layout reuse against constraints and cached results.
Primary references: [style invalidation design](https://chromium.googlesource.com/chromium/src/+/master/third_party/blink/renderer/core/css/style-invalidation.md),
[style propagation in Element](https://chromium.googlesource.com/chromium/src/+/500947f508e484696c809b5ea84568bfd51b320a/third_party/blink/renderer/core/dom/element.cc),
and [BlockNode layout caching](https://chromium.googlesource.com/chromium/src/+/17617a0cd9b88f98683e2357b75aefe85b209763/third_party/blink/renderer/core/layout/block_node.cc).
These explain architectural principles, not a claim that the cited revisions
are the exact source of the measured Chrome binary.

Profiling found an inventory-panel padding change recascaded 101 elements and
queried 303 pseudo styles. A one-cell change promoted to full-grid layout.
The fixes address those dependencies directly:

- Skip unaffected descendant cascade for pure local inline changes, preserving
  explicit inheritance, variable/rollback resolution, pseudos, container inputs,
  animation and selector-state dependencies.
- Reuse flex/grid item layout only after proving parent inputs and exported
  sizes/baselines remain valid. Fixed stretched grid slots and definite-width
  nonwrapping column flex items can prove intrinsic inline-size independence.
- Avoid formatting-context setup for empty imposed-width reflows.
- Resolve nongrowing fixed tracks directly; copy/sort contributions only when
  spanning items require span order.

Independent review identified non-stretch shrink-to-fit and imposed-height
hazards; those retain full parent layout. Tests also cover auto/fr tracks,
substantial padding overflow, percentage-height descendants, mixed directives,
wrapping and intrinsic-sized parents. No benchmark-specific CSS rules were added.

## Interleaved before/after

Six rounds, 1,000 operations per scenario, alternating executable order.
Milliseconds are medians of batch means. The saved baseline is the executable
from `2fe80342`, before these engine changes. Both time mutation through layout
and a bounds read, excluding paint.

| Scenario | Before | After | Reduction |
|---|---:|---:|---:|
| Hud panel | 0.028427 | 0.012495 | 56.0% |
| Hud cell | 0.014660 | 0.008533 | 41.8% |
| Inventory panel | 0.423943 | 0.145790 | 65.6% |
| Inventory cell | 0.144643 | 0.009418 | 93.5% |
| Settings panel | 0.081714 | 0.026160 | 68.0% |
| Settings cell | 0.027117 | 0.009212 | 66.0% |

## Chrome comparison and expanded fixtures

Separate ten-round run, 1,000 operations per scenario and engine; Windows x64
Release and pinned Chrome 152.0.7977.82. Both include the same attribute setter
and synchronous layout, and neither includes painting. Median batch means:

| Scenario | Weva ms | Chrome ms |
|---|---:|---:|
| hud panel | 0.012967 | 0.008550 |
| hud leaf | 0.009016 | 0.009100 |
| inventory panel | 0.151253 | 0.048800 |
| inventory leaf | 0.009314 | 0.050200 |
| settings panel | 0.026654 | 0.021350 |
| settings leaf | 0.009319 | 0.011800 |
| hud-content panel | 0.025597 | 0.009950 |
| hud-content leaf | 0.012240 | 0.009800 |
| autosize-flex panel | 0.020409 | 0.008200 |
| autosize-flex leaf | 0.025254 | 0.009000 |

The original six fixture hashes are pinned. Additional fixtures contain visible
nested bars and auto-sized children that move siblings. Schema 3 checks initial
geometry, both 11/12px mutation states, and every timed batch's final geometry.
The two-state check catches ignored mutations even when an even iteration count
ends at the original style. Every check stays within 0.1 px of Chrome.

These are layout microbenchmarks without fonts, images or host presentation,
not frame p95 or complete game UI frame cost. Chrome remains substantially
faster for whole-inventory reflow and several content-dependent cases. One
remaining source of work is preliminary grid-item layout at container width
before final track-width layout; removing it safely needs a stronger separation
of box-model inputs, intrinsic exports and final constraints.

## Evidence and validation

Raw artifacts: `.utmp/chrome-close-20261030/`. Timings: `matched-verified/`
and `paired-verified/`; earlier runs and failed regression evidence are retained.
[Compact timing receipt](chrome-close-20261030.json) records binary hashes,
machine identity, fixture hashes and per-round means.

- Windows Release core: 486,321 checks, zero failures; benchmark CLI passes.
- GCC: 15 suites and 698,598 core checks pass. Clang ASan/UBSan: 17 suites
  and 698,592 core checks pass (small-stack checks are excluded there).
- All 336 tracked Chrome layout captures pass the 1.5 px ceiling, without
  known-gap exemptions, on the final corrected build.
- Both Windows hosts rebuild without warnings. Full Unity EditMode: 244 pass,
  zero fail and two environment-gated inconclusive (Native subset: 243 pass).
- Three added grid reference fixtures: 13/13 assertions match live Chrome.
- Python tooling: 70 tests, 61 pass and nine Linux-only skips on Windows.
- Full local release gate passes (exit 0): `full-gate-final.log`; detailed
  Linux logs are in `/tmp/weva-gate.6GIcjH/`. This uses the qualified patched
  Godot editor/templates, pinned Windows Chrome bridge and private Xvfb display.
  Chrome behavior checks: 66 pass, no unexpected failures, one documented IME
  known failure. Godot Unicode, font, rendering, native export and scene gates pass.
- Inspected the final `forms-live` Godot render saved as `render-inspected.png`:
  inputs, wrapped notes, scrolling log, dropdown and buttons render correctly.

The first regression run caught descendant hover invalidation and a test fixture
missing block display. A preliminary paired run exposed a failed reuse-probe
regression for settings; the final constraint proof removes it. These failures
were addressed in the owning systems and remain in the artifact logs.
The full Chrome oracle additionally caught an empty subgrid with explicit rows:
the empty-content shortcut now applies only to ordinary block/flow-root boxes,
and seven checks pin the tracked Chrome fixture before and after relayout.
