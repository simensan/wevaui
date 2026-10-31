# Stretched-item probe: real-font host follow-up — 2026-10-31

The speculative-probe optimization from `9f9b2040` is **reverted**. The
fixed-font core result did not establish a useful end-to-end improvement in
Unity: full inventory total CPU worsened in two of three pairs, and changing
HUD total CPU worsened in all three. The generic incremental-layout regression
tests remain. Both Unity and Godot are rebuilt from the restored shared core.

## Measurement

Three fresh-process pairs ran in baseline/candidate, candidate/baseline,
baseline/candidate order, with separately built native plugins and identical
managed renderer code. Each process measured four workloads, 128 warmups and
2,048 samples per workload: **49,152 measured iterations**. No builds or tests
overlapped timing. Unity 6000.4.1f1, Windows, RTX 5080, OpenSans font through
Unity's font backend, 1280×720. This measures update, texture/mesh synchronization
and command recording; GPU submission, execution and frame pacing are excluded.

Full inventory applies all twenty changes across twelve selections through
bindings. Every target is verified outside timing. Final retained renders
match fresh renderers, and every workload's PNG is identical across all six
processes. Both candidates and all losing measurements are preserved.

The table shows medians of three per-run median / p95 values, in milliseconds.
Total percentiles come from each sample's summed stages.

| Workload | Baseline update | Candidate update | Baseline total CPU | Candidate total CPU |
| --- | ---: | ---: | ---: | ---: |
| Idle HUD | 0.00030 / 0.0005 | 0.00030 / 0.0022 | 0.03390 / 0.0394 | 0.03130 / 0.0453 |
| Changing HUD | 0.12325 / 0.1594 | 0.12905 / 0.1674 | 0.20790 / 0.2656 | 0.21875 / 0.2747 |
| Quantity/highlights | 0.23250 / 0.2782 | 0.23015 / 0.2758 | 0.42460 / 0.5160 | 0.42370 / 0.5141 |
| Full inventory | 0.69360 / 0.7848 | 0.67020 / 0.7463 | 1.11435 / 1.3153 | 1.10855 / 1.3530 |

Full inventory has a **1.39% median paired update reduction**, but a **1.53%
median paired total CPU increase**, with only one total-CPU winning pair.
Changing HUD has a **5.22% median paired total CPU increase**. Comparing only
the two aggregate full-inventory medians would misleadingly suggest an overall
gain. Tiny idle timings are especially sensitive to timer granularity/noise.

These results do not prove the branch caused every slowdown: unchanged upload
and recording stages also varied. They do show that the synthetic-font win is
insufficient evidence for accepting this candidate as a host optimization.
No Godot performance gain is claimed or inferred from Unity's results.

## Restoration and evidence

Restoration passes 15 GCC and 17 Clang ASan/UBSan CTest suites, with no build
warnings. The restored GCC core library exactly matches its earlier baseline
SHA-256 `9ee47cba558a6e81486d1a4b38ddae0d9709116f64b8ca32a73bc8570f72903a`.
The new regression tests run against that restored core.

The rebuilt and installed Unity plugin passes its load test and Native EditMode
(254 pass, two existing environment-gated inconclusive). Both Godot Windows
and Linux extensions were rebuilt; qualified Linux host checks (247), font-size
(208), font-inheritance (560), and texture lifetime (48 headless, 96 rendered)
pass. The rendered check uses Xvfb/OpenGL with explicit Dummy audio after an
initial run reported an unavailable audio device; both logs are retained.

This follow-up changes no host adapter or ABI and does not undo the accepted
Unity direct-vertex upload. This is scoped restoration verification, not a
repeat of the full local gate or a new player/export qualification.

The [JSON receipt](stretched-probe-host-20261031.json) contains all per-process
summaries, paired aggregates and measured plugin hashes. Raw samples, render
PNGs, build logs and plugin snapshots remain under
`.utmp/render-audit-20261030/stretched-host-pairs/`; the driver and summarizer
are beside that directory. The original synthetic-font result remains in the
[superseded receipt](stretched-probe-20261031.md).
