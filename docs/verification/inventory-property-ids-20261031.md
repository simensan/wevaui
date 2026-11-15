# Reuse matched declaration property IDs — 2026-10-31

The shared cascade retains stable registry IDs in cached matched declarations
and applies known properties through the ID-based setter. Previously it
resolved the same property name again for the setter and importance tracking.
Inline declarations now reuse the ID they already resolve. Unknown and custom
names keep their name-based lookup so later registration remains visible.
There is no C ABI change or separate Unity/Godot implementation.

## Provisional measurements

Baseline is `4522bd5b` (v20); candidate is v22. Three alternating
baseline/candidate/Chrome rounds use the same real-font inventory, twenty-field
mutations, 20 warmups and 200 measured updates at requested 60 Hz. The other
Host-engine game remained running as requested. No own builds overlapped timing.
These measurements remain **provisional**.

Median of run means, milliseconds per inventory update:

| Stage | Baseline | Candidate | Chrome 152 |
|---|---:|---:|---:|
| Cascade | 0.051 | 0.043 | Not separately equivalent |
| Style/layout | 0.162 | 0.150 | 0.140 |
| Paint recording | 0.195 | 0.194 | 0.193 |
| Complete native CPU | 0.759 | 0.753 | Not equivalent |

The median **within-pair** style/layout reduction is **4.3%**. The paired
complete native CPU reduction is only **0.3%**, effectively flat at this scale.
Ratios of independently selected medians above are not the paired result.
All three inventory pairs reduce cascade CPU. Box/layout and paint differences
are not attributed to this cascade change. HUD paired style/layout falls 2.9%
and complete CPU 0.6%; do not interpret these as established frame-time gains.

Candidate inventory style/layout remains about 7% above Chrome in this batch;
Chrome parity is not established. Chrome raster-worker CPU is 0.078 ms and
remains separate from paint recording. Complete native CPU includes controller,
update, preparation, encoding and submission. Initial/final sampled native
geometry matches across all six native runs. Earlier percentages do not add
to this result.

Raw timing evidence: `.utmp/inventory-chrome-20261031/comparison-v22/`.
Build and verification evidence: `.utmp/inventory-property-ids-20261031/`.

## Rejected line-height experiment

Candidate v21 deferred the fallback metrics lookup when CSS supplies an explicit
line height. All 17 GCC suites passed, but three paired inventory runs showed
20.9% higher style/layout and 21.3% higher complete CPU. Other stages also drifted,
so the batch does not establish that the change caused that slowdown. It did
not justify retention and was reverted. The ABI font adapter already caches
face metrics; this shortcut would mostly avoid cached lookups, not host calls.
Its patch and logs remain under `.utmp/inventory-lineheight-20261031/`, and
timings under `.utmp/inventory-chrome-20261031/comparison-v21/`.

## Correctness follow-up found during testing

The initial new test explicitly requested `::before { color: inherit }` and
expected the host's blue value from `compute_pseudo_element`. Both candidate
and restored baseline instead returned the literal `inherit`. Baseline and
candidate failed logs and the original test are preserved. This is an existing
pseudo keyword-resolution issue, not a regression introduced or fixed by this
optimization. A separate pinned Chrome 152 check returns `rgb(0, 0, 255)` for
both host and pseudo; its result is preserved in `pseudo-inherit-chrome.json`.
Fixing general pseudo CSS-wide keyword resolution is a correctness follow-up.
The retained optimization
test covers implicit inheritance, cached matches, custom variables, shorthand
importance, inline overrides, pseudo application and stylesheet replacement.

## Verification

The full qualified release gate passes with no skipped stages: 17 GCC suites,
19 Clang sanitizer suites, 341 tracked Chrome layout captures and 66 browser
behavior checks plus the existing known IME failure. Main-suite coverage is
807,129 checks on GCC and 807,123 under sanitizers. Both builds report no compiler
warnings. The new cascade regression adds 27 checks.

Windows Unity and Godot native plugins were rebuilt. Unity full EditMode passes
255 tests with two existing environment-gated inconclusive; the Windows Mono
development player passes 7/7. All 16 player images are pixel-identical to the
accepted bidi build. Godot passes its font, Unicode, export, rendering and live
scene gates, including 19,833 font checks. Its final render is pixel-identical
to the accepted build. The Godot render and Unity binding/font/image render
were inspected. This pass makes no new IL2CPP or hardware-qualification claim.

The final host-engine executable matches the measured candidate's SHA-256.
The [tracked receipt](inventory-property-ids-20261031.json) contains measurements,
source and evidence hashes, native build metadata and verification results.
