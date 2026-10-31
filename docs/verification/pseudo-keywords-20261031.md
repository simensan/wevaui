# Shared CSS-wide keyword resolution — 2026-10-31

Standard properties on pseudo-elements now use the same post-substitution
CSS-wide keyword resolver as ordinary elements. Previously their separate path handled only font size
and line height, leaving values such as `color: inherit` unresolved. The fix
removes that partial implementation, records pseudo declaration winners for
origin/layer rollback, and preserves inherited font and line-height sources.

Chrome also disproved an old ordinary-element expectation: `color: revert`
with no lower-origin declaration inherits the parent's color; it does not
reset to black. The shared resolver now defaults both rollback keywords like
an absent declaration, and that test is recalibrated against Chrome.

There is no C ABI change or host-specific implementation. This is a correctness
fix, not a new runtime optimization claim.

## Chrome and pixel evidence

`Tools/oracle/check_pseudo_keywords_chrome.cjs` runs with the other live browser
checks. Its 12 scenarios cover all five keywords on pseudo-elements and ordinary
elements, inherited and non-inherited properties, layer rollback, and `var()`
fallbacks. Sixty new C++ checks pin the pseudo results and font-source flags.

A focused 200-by-168 render shows four border swatches: inherited blue,
initial black, reverted blue and lower-layer green. Core RGB pixels exactly
match pinned Chrome 152, and the result was inspected. Fixtures, screenshots
and comparison evidence are under `.utmp/pseudo-keywords-20261031/`.

The initial background-based fixture exposed a separate existing defect:
`background-color: currentColor` paints transparent in this path. Its CSS,
failed core render and Chrome render are preserved as `background-currentcolor.*`.
The border fixture isolates the corrected cascade behavior; it does not fix
or qualify that background behavior. General `currentColor` paint resolution
remains a correctness follow-up.

## Provisional inventory regression check

Baseline is `f861e360` (v22); candidate is v23. Three alternating
baseline/candidate/Chrome rounds use the same real-font fixtures, twenty-field
inventory mutations, 20 warmups and 200 measured updates at requested 60 Hz.
The other Agentic game remained running as requested; no own builds overlapped
timing. Initial/final sampled geometry matches across the six native runs.

Median of run means, milliseconds per inventory update:

| Stage | Baseline | Candidate | Chrome 152 |
|---|---:|---:|---:|
| Style/layout | 0.172 | 0.169 | 0.140 |
| Paint recording | 0.218 | 0.214 | 0.188 |
| Complete native CPU | 0.970 | 0.957 | Not equivalent |

Within-pair medians are 1.7% lower style/layout and 2.5% lower complete CPU;
HUD style/layout and complete CPU are about 1.4% higher. These small mixed
changes do not establish an optimization or a regression. The absolute drift
from the previous batch reinforces the provisional qualification. Chrome parity
is not established. Chrome raster-worker CPU is 0.083 ms, separate from paint
recording; complete native CPU includes controller/update and host submission.

Timing evidence is `.utmp/inventory-chrome-20261031/comparison-v23/`.
The remaining inventory cost is still dominated by box rebuilding and local
layout: 0.121 ms of the candidate's 0.169 ms combined stage in this batch.
Further optimization should attribute probe construction, inline layout and
post-splice indexing before changing their correctness-sensitive reuse rules.
An additional diagnostic run confirms steady updates replace three to six
detail subtrees (title, description, labels, detail rows and the primary button),
rather than rebuilding the full inventory. It retains 35–38 paint subtrees in
steady samples. This run overlapped gate work and contributes counters only,
not performance measurements; its log and counter summary are preserved.

## Verification

The full qualified release gate passes with no skipped stages: 17 GCC suites,
19 Clang sanitizer suites, 341 tracked Chrome layout captures, and 67 live
browser checks plus the existing known IME failure. Main-suite counts are
807,189 on GCC and 807,183 under sanitizers, with no compiler warnings.

Windows Unity and Godot native plugins were rebuilt. Unity full EditMode passes
255 tests with two existing environment-gated inconclusive; the Windows Mono
development player passes 7/7. All 16 player captures and the Godot gate render
are pixel-identical to the accepted property-ID build. Godot's font, Unicode,
export, rendering and scene checks pass, including 19,833 font checks. The
binding/font/image player render and Godot render were inspected. No new
IL2CPP or hardware qualification is claimed.

The final Agentic executable matches the measured v23 candidate's SHA-256.
The [tracked receipt](pseudo-keywords-20261031.json) records timings, counters,
visual fixtures, source/evidence hashes, native build metadata and gate results.
