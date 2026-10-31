# Inventory box copies — 2026-10-31

This follows the [inventory update pass](inventory-update-20261031.md). The
shared core removes unnecessary `Box` copies without changing layout rules:

- Construct new boxes directly in their arena slot; initialize recycled slots
  before setting their kind, element and style.
- Save only child/sibling/parent links when replacing a subtree. Copying a full
  box also copied unrelated storage such as multi-column sets.
- Read the original box by const reference while proving incremental layout.
  The live tree remains unchanged until all probes succeed and splicing begins.

Unity, Godot and explicit-source Agentic builds receive the same changes. There
is no C ABI or authoring API change. The inventory fixture and mutations remain
unchanged. This is a modest optimization, not Chrome parity.

## Provisional measurements

The other Agentic workspace remains active at the user's request. These runs
are **provisional**. Each batch uses three alternating native/Chrome rounds,
real Open Sans, twenty-field inventory selections, 20 warmups, 200 measured
changes and requested 60 Hz cadence. Our own builds do not overlap timings.
Initial/final sampled target geometry matches across all native runs.

The construction/link changes (`v10`) versus accepted `v7` measured paired
median changes of **−5.4% style/layout** and **−4.1% complete native CPU** for
inventory. HUD complete CPU was +2.1%, with paint unchanged. The further const
reference change (`v11`) versus `v10` measured −4.9% inventory style/layout and
−1.2% complete CPU. These separate percentages must not be added into a claim
about the combined change; background load and unrelated stages vary too.

Latest batch, median of run means in milliseconds per inventory selection:

| Stage | v10 baseline | v11 candidate | Chrome 152 |
|---|---:|---:|---:|
| Style/layout | 0.204 | 0.194 | 0.159 |
| Paint recording | 0.222 | 0.214 | 0.203 |
| Complete native CPU | 0.883 | 0.872 | Not equivalent |

Style/layout is still about **22% above Chrome**, and paint about **5% above**.
Chrome raster-worker CPU was 0.084 ms and is kept separate. Complete native
CPU includes controller/update, resource preparation, encode and submission.
There is no new isolated-hardware, cold-start or per-host speed qualification.

## Rejected work and diagnostic findings

A borrowed shaping-vector experiment (`v9`) avoided cached glyph-vector
copies during immediate paint consumers. Its first three paired runs showed
small improvements, but six additional alternating native pairs did not
confirm them: inventory paint +0.7% and complete CPU +10.0% paired medians,
with wide variation. The experiment and its proposed tests were reverted.
No borrowed-shaping API or extra cache remains in the final implementation.

Temporary phase instrumentation found that changed inventory labels use
ordinary block layout, not the imposed flex/grid probe path. Building/layout
of changed text and post-splice indexing remain the main layout targets.
The paint diagnostic reused 35 glyph subtrees with no new shadow/filter raster
jobs. Diagnostic runs include clock/logging overhead and use 1 ms cadence;
they are attribution evidence, not speed comparisons. Instrumentation was removed.

Raw evidence is under `.utmp/inventory-next-20261031/` and
`.utmp/inventory-chrome-20261031/comparison-v9{,-repeat}/`, `comparison-v10/`
and `comparison-v11/`. The rejected patch and instrumented source are preserved.
[Measurement receipt](inventory-box-copies-20261031.json).

## Verification

The complete qualified release gate passes with **exit code 0**. GCC passes
15 suites (707,090 main-suite checks); Clang ASan/UBSan passes 17 suites
(707,084 main checks, with the existing small-stack exclusion). The new test
checks retained child links and text, sibling order, multi-column storage and
default initialization of recycled anonymous boxes.

All 341 tracked Chrome captures pass the 1.5 px ceiling, including all 69 hand
cases. Browser behavior passes 66 scripts with the existing IME exception.
Godot font safety passes 19,833 checks; Unicode, native debug/release/embedded
exports, render comparisons and live scenes pass. The final render was inspected.

Windows Unity and Godot native libraries were rebuilt and installed. Unity full
EditMode passes 255 tests with two existing environment-gated inconclusive
results; the Windows Mono player passes 7/7. All sixteen player captures are
RGB-identical to the preceding inventory update build. The packaged font/image
binding capture was inspected. No new IL2CPP qualification is claimed here.

Gate, native build, Unity XML, player and capture evidence is under
`.utmp/inventory-next-20261031/`. A manual Unity metadata copy initially used
the wrong filename; the correct `.dll.build.json` was installed before Unity
tests, and the native plugin load check passed.
