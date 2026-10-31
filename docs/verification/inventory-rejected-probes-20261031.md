# Inventory follow-up experiments — 2026-10-31

Four additional candidates were measured against accepted commit `144d63e4`
(the v12 benchmark binary). None demonstrated an inventory improvement, so
all four were reverted. This pass makes **no new optimization claim**.

Each candidate used three alternating baseline/candidate/Chrome rounds,
the same real-font inventory and twenty-field mutations, 20 warmups and 200
measured updates at requested 60 Hz. The unrelated Agentic game remained
running as requested. No own builds overlapped these timing runs. All numbers
are provisional; positive changes below mean slower. These are medians of
within-round percentage changes, not ratios of independent run medians.

| Candidate | Inventory style/layout | Complete native CPU | Decision |
|---|---:|---:|---|
| v13: per-item inline vertical-metric vector | +1.56% | +0.46% | Reverted |
| v14: four-entry stack vertical-metric cache | +1.55% | +4.19% | Reverted |
| v15: direct box subtree import | +0.56% | +0.99% | Reverted |
| v16: stable heap ownership of scratch trees | +3.13% | +2.18% | Reverted |

The first two attempted to avoid repeated ascent/descent work within inline
layout. The third avoided blank box initialization immediately before copying
an imported box. The fourth avoided moving deque-backed scratch trees between
probe transactions and the reuse pool. Plausible reductions in local work did
not establish an end-to-end benefit in these runs.

The newest paired batch measured the **accepted baseline**, not a new accepted
candidate, at 0.192 ms inventory style/layout versus Chrome's 0.170 ms, and
0.211 ms paint recording versus Chrome's 0.214 ms. Complete native CPU was
0.870 ms and has no equivalent Chrome stage here. Chrome raster-worker CPU
was 0.084 ms and remains separate. The earlier accepted v12 receipt measured
0.173 ms style/layout and 0.797 ms complete CPU in its own batch. This drift
reinforces the provisional qualification. Chrome parity is still unproven.

Initial/final sampled native geometry matched across each candidate's six
native runs. The corrected v14 regression and all 16 GCC suites passed.
The initial v13 test had an incorrect collapsed-whitespace identity assumption;
its failed log is preserved. Neither that test nor candidate code remains.
The reverted v15/v16 candidates were not release-qualified.

All source changes were restored to the previously qualified commit. The
Agentic benchmark was then rebuilt from that source; every executable section
is byte-identical to the accepted v12 binary. Unity and Godot remain on the
previously qualified shared-core build. No full release gate was repeated for
this documentation-only result; the accepted core's gate evidence is in the
[intrinsic memo report](inventory-intrinsic-memo-20261031.md).

Patches and failed/passing diagnostic logs are preserved under
`.utmp/inventory-inline-20261031/`. Raw timing runs are under
`.utmp/inventory-chrome-20261031/comparison-v13/` through `comparison-v16/`.
The [tracked receipt](inventory-rejected-probes-20261031.json) records summaries,
benchmark manifests, patch hashes and restored executable section hashes.

The next useful investigation is attribution of remaining box/layout work in
the accepted build, especially repeated probe construction and parent-input
validation. Keep full-layout equivalence checks and use paired measurements
before retaining any change. Repeating the four experiments above without
new attribution evidence is unlikely to be useful.
