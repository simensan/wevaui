# ASCII grapheme iteration — 2026-10-31

The shared text core now resolves ASCII grapheme properties without searching
the Unicode range table, and emits boundaries between adjacent printable ASCII
characters directly. Non-ASCII lookahead still uses the full segmentation
algorithm: an ASCII base followed by an accent, variation selector or ZWJ must
not be split. CR/LF and control characters retain their existing rules.
There is no new cache, ABI change or host-specific implementation.

This reduces work in letter-spacing measurement and other grapheme consumers.
It does **not establish an inventory frame-time improvement or Chrome parity**.
The user-requested unrelated Agentic runtime remained running throughout;
all timings remain provisional.

## Investigation

The first candidate skipped a redundant layout pass when natural and retained
widths matched. Its paired inventory style/layout change was only -0.54%.
Temporary attribution then showed only one imposed-width probe among 1,319
probes across the diagnostic HUD/inventory run. It was not the relevant steady
inventory path. That candidate and all diagnostic instrumentation were removed.
The existing plain-text inline cache also already avoids much of that work.

The retained change instead removes Unicode table searches from the ASCII path
used by text layout. Both original and optimized implementations are measured
directly by the preserved kernel harness, separately from host frame timings.

## Provisional inventory comparison

Three alternating baseline/candidate/Chrome rounds use the same real-font
inventory and twenty-field mutations, 20 warmups and 200 measured updates at
requested 60 Hz. No own builds overlapped timing. Baseline source is `5c16ee26`
(the executable sections match accepted candidate v12).

Median of run means, milliseconds per inventory update:

| Stage | Baseline | Candidate | Chrome 152 |
|---|---:|---:|---:|
| Style/layout | 0.190 | 0.188 | 0.152 |
| Paint recording | 0.210 | 0.212 | 0.196 |
| Complete native CPU | 0.846 | 0.846 | Not equivalent |

The within-round paired median changes were **+2.73% style/layout** and
**+2.42% complete CPU**, despite the independent medians above. That disagreement
and the background workload preclude a measured end-to-end speedup claim.
HUD paired complete CPU was -0.28%, also not a meaningful gain. Chrome raster
worker CPU was 0.082 ms and remains separate from paint recording. The native
complete path includes controller/update, resource preparation, encoding and
submission; it must not be compared to Chrome paint recording alone.

Initial/final sampled native geometry matched across all six native runs.
The first batch's style/layout result remains approximately 23% above Chrome;
this pass has not closed that gap.

A second three-round batch reverses native ordering. It measures 0.191 ms
baseline versus 0.192 ms candidate style/layout, with Chrome at 0.155 ms;
candidate complete CPU is 0.847 ms. Across all six pairs, the median change is
**+3.47% inventory style/layout**, **+4.14% paint** and **+2.99% complete CPU**.
HUD complete CPU is +2.62%. A brief Unity process was also observed in the first
repeat pair; its process observations remain in the manifest. These results
do not demonstrate an end-to-end improvement and do not rule out a small
regression. The change is retained for the directly measured segmentation
benefit, not as a completed inventory optimization. Quiet-machine qualification
remains necessary before making a normal-frame performance claim.

## Evidence

Raw paired timing evidence is under
`.utmp/inventory-chrome-20261031/comparison-v18/` and `comparison-v19/`;
the rejected candidate is v17.
Attribution sources/logs, kernel harness and build/gate evidence are preserved
under `.utmp/inventory-reflow-20261031/`. Diagnostic builds and their timing
overhead are excluded from the host comparison.
[Tracked receipt](inventory-graphemes-20261031.json) includes both batches,
all six paired changes, kernel source/results, source hashes and gate receipts.

## Direct text-operation measurement

GCC `-O3 -DNDEBUG`, three alternating baseline/candidate rounds, 200,000
iterations per sample. Each implementation returns identical boundary checksums.
The baseline is the original `grapheme.cpp` from `5c16ee26`; these timings do
not include shaping, layout, painting or host submission and must not be
presented as their speedup. No own builds or gate work overlap the measured
loops; the harness compiles both binaries before measuring either.

| Sample | Baseline ms | Candidate ms | Paired change |
|---|---:|---:|---:|
| ASCII inventory label | 136.811 | 17.138 | -87.4% |
| ASCII HUD label | 116.768 | 14.675 | -87.4% |
| ASCII description | 151.439 | 18.712 | -87.6% |
| Mixed ASCII/Unicode | 47.924 | 14.817 | -68.5% |
| CJK | 60.008 | 57.763 | -5.3% |
| Arabic/Indic | 70.988 | 61.721 | -6.9% |
| Emoji sequences | 36.733 | 37.035 | +0.8% |

The small non-ASCII differences do not establish a benefit or regression under
the background load. The large ASCII reduction is the demonstrated benefit
of this change. It introduces no persistent storage or extra font callbacks.
The preserved `run-kernel.py` records the compiler, harness/source hashes,
individual rounds and checksums in `kernel-summary.json`.

## Verification

The complete qualified release gate passes with exit code 0. GCC passes all
16 suites and Clang ASan/UBSan passes all 18. Main core counts are **807,102**
and **807,096**. New coverage checks every ASCII pair in both Unicode and browser
caret profiles, including embedded NUL and CR/LF, and every printable ASCII base
with an accent, variation selector and ZWJ suffix. The existing 766 Unicode
conformance cases remain passing.

All 341 tracked Chrome layout captures pass the 1.5 px ceiling, including 69
hand cases. Browser behavior passes 66 scripts with the existing IME exception.
Windows Unity and Godot native libraries were rebuilt and installed. Unity full
EditMode passes 255 tests with two existing environment-gated inconclusive
results; its Windows Mono player passes 7/7. All sixteen player captures are
RGB-identical to the accepted intrinsic-memo build, and the packaged font/binding
capture was inspected. Godot font safety passes 19,833 checks; Unicode, native
exports, backend renders and live scenes pass. Its final render is RGB-identical
to that build and was inspected. This does not add IL2CPP or hardware qualification.
