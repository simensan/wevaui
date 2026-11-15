# Inventory update work — 2026-10-31

The shared core now avoids work that cannot affect a normal inventory selection:

- Nonempty text replacements retain selector results when emptiness, direction
  and form inputs are unchanged. Both direct setters and bindings use the proof.
- Changed intrinsic widths can stop at an unchanged definite-width block or a
  flex allocation already proven independent of intrinsic widths. Outer size,
  first baseline and locality checks remain; intrinsic exports are refreshed
  for later parent allocation. Inline-block ancestors retain the ordinary path.
- Swapping already-decoded images with equal natural dimensions repaints without
  layout. Unknown, failed, pending and differently sized resources do not qualify.
- Class changes that cannot affect descendant selectors restyle their owner;
  inherited changes still propagate. Nested selectors, class-attribute selectors,
  sibling dependencies, `:has()` and scope boundaries retain conservative behavior.

The new class tests also exposed and fixed an existing cache-key defect: duplicate
class tokens previously cancelled under XOR, allowing `panel chosen chosen` to
reuse the matches for `panel`. The key now treats duplicate tokens as a set.

These changes live in `libweva`, with no ABI or authoring API change. Unity,
Godot and explicit-source host-engine builds receive the same behavior. No sample
markup, selection values, font assets or benchmark workload was simplified.

## Provisional measurements

Three rounds alternate baseline/candidate/Chrome order, using the same survival
fixtures, real Open Sans font, twenty-field selection updates, 20 warmups and
200 measured changes per case, requested at 60 Hz. Another workspace remains
active at the user's request. Its renderer/build processes are recorded for
each run; these measurements are **provisional**, not isolated-hardware proof.

Median of run means, milliseconds per inventory selection:

| Stage | Baseline | Candidate | Chrome 152 |
| --- | ---: | ---: | ---: |
| Style + layout | 0.291 | 0.207 | 0.149 |
| Paint recording | 0.258 | 0.229 | 0.203 |
| Native complete CPU path | 1.178 | 1.024 | Not equivalent |

Median paired changes are −30.0% style/layout, −17.1% paint recording and
−13.1% complete native CPU. These paired percentages differ from ratios of
the independent medians in the table. HUD complete CPU is approximately flat
(−0.8% paired); its paint result is slightly higher (+1.8%). Initial/final
target geometry is identical across all baseline/candidate runs.

**Chrome speed is not reached:** style/layout remains about 39% higher and
paint recording about 13% higher in this batch. Chrome raster-worker CPU is
reported separately (0.085 ms); adding it to selected timings does not turn
this into an equivalent end-to-end latency comparison. Native complete CPU
includes controller, update, resource preparation, encode and submission.

An earlier strict-guard attempt stopped when another workspace began a build;
its partial evidence remains under `comparison-v3/`. Experiments and diagnostic
logs remain under `.utmp/inventory-chrome-20261031/`; the accepted candidate
is `candidate-v7.exe`, measured in `comparison-v7/`. Diagnostic logging runs
overlap builds and are attribution evidence only.

A further experiment batched ancestor bookkeeping after disjoint replacements
and checked fixed-width boundaries before measuring intrinsic contributions.
It passed all 15 GCC suites, including simultaneous sibling text changes, but
was **rejected**: three new paired rounds against accepted v7 measured identical
inventory style/layout medians (0.202 ms), +2.6% paint and +4.6% complete CPU.
Chrome in that batch measured 0.164 ms style/layout and 0.212 ms paint. HUD
style/layout also increased. These results remain provisional with the other
workspace running; they provide no evidence for retaining the added complexity.
The patch, binaries and complete measurements remain in `rejected-v8.patch`
and `comparison-v8/` under the evidence directory. The final source and host-engine
benchmark build use the accepted v7 implementation.

## Verification

GCC passes 15 suites (707,079 checks in the main suite); Clang ASan/UBSan passes
17 suites (707,073 main checks). Regression coverage includes 18 layout
configurations with repeated text edits and later parent reallocations, selector
dependency changes, duplicate classes, unchanged selector work counts, and image
source swaps. All tracked Chrome layout captures pass the 1.5 px ceiling;
browser behavior checks pass 66 scripts with the existing IME exception.

Windows Unity and Godot native libraries are rebuilt from this core. Full Unity
EditMode passes 255 tests with two existing inconclusive results. The Windows
Mono player passes 7/7 rendering/asset/binding tests; all sixteen captures are
RGB-identical to the prior player, and the packaged-font/binding render was
inspected. Godot font safety passes 19,833 checks, with Unicode and native
debug/release/embedded export checks passing as well. These are correctness
checks, not new per-host timing claims or fresh IL2CPP qualification.

The initial full gate finished all engine/host stages but returned one failure:
the new Unity cleanup tests imported `psutil`, absent in the Linux gate. Cleanup
now imports that runtime dependency only when used; mocked tests inject their
process API explicitly. The repaired Linux tooling suite passes **72 tests**,
and both ownership tests pass on Windows. The original failed gate log is
preserved; this is a repaired-stage receipt, not a claim that its exit code was 0.
The final Godot render was inspected.

After rejecting v8 and restoring the accepted core, the complete qualified
release gate was rerun and **passed with exit code 0**. The fresh log, copied
per-stage evidence and inspected Godot render are under `gate-final/`.
This rerun includes the repaired tooling stage, both core configurations,
Chrome, native plugin/extension checks and live Godot scenes; it supersedes
the repaired-stage-only qualification above without deleting the original
failure evidence. Unity's editor/player results above remain for this same
accepted implementation.

Raw evidence is under `.utmp/inventory-chrome-20261031/`, including the failed
duplicate-class regression run, diagnostic logs, native build logs, Unity XML,
captures and the qualified full-gate log. [Measurement receipt](inventory-update-20261031.json).
