# Avoid unused bidi mappings during layout — 2026-10-31

The shared core now checks whether a paragraph needs bidirectional resolution
before building its UTF-16 text and per-character mapping vectors. Previously
it allocated and filled those structures even for ordinary left-to-right text,
then discarded them when it found no directional content or CSS controls.
ASCII direction classification also avoids the ICU property call.

The detection uses the existing directional-character predicate and CSS
control rules. RTL paragraphs, Arabic numbers, explicit Unicode formatting
characters, and inline embeddings/overrides/isolates still enter the existing
ICU resolver. Non-text items retain their existing interpretation. No cache,
ABI change, host-specific layout path or sample simplification was added.
For paragraphs that do require bidi, the preliminary scan stops at the first
trigger and the existing UTF-16 construction follows; this adds a scan of that
prefix, a tradeoff for eliminating mapping work on ordinary LTR paragraphs.

## Provisional performance

Three rounds alternate baseline/candidate/Chrome order with real Open Sans,
the same inventory and twenty-field selection mutations, 20 warmups and 200
measured updates at requested 60 Hz. The other Agentic workspace stayed active
as requested; timings remain **provisional**. No own builds overlapped timing.
Baseline is commit `a4b6e31e` (candidate v18), including the ASCII grapheme change.

Median of run means, milliseconds per inventory update:

| Stage | Baseline | Candidate | Chrome 152 |
|---|---:|---:|---:|
| Style/layout | 0.182 | 0.168 | 0.139 |
| Paint recording | 0.212 | 0.207 | 0.187 |
| Complete native CPU | 0.831 | 0.800 | Not equivalent |

Paired median reductions are **7.7% style/layout** and **3.7% complete native
CPU**. HUD style/layout is unchanged in the paired median; complete CPU is
3.6% lower. Paint varies too; this is not evidence of a separate paint improvement.
Native initial/final sampled geometry matches in all six runs.

Chrome parity is not reached: candidate style/layout remains about 21% higher
in this batch. Chrome raster-worker CPU is 0.083 ms and stays separate from
paint recording. Complete native CPU includes controller/update, resource
preparation, encoding and submission and is not comparable to Chrome paint
recording alone. Do not combine percentages from earlier batches.

## Allocation regression

The new `weva_bidi_allocations` suite passes 792 checks. Ordinary ASCII, mixed
LTR Unicode and malformed UTF-8 paragraphs use **zero C++ allocations** for
bidi detection, with both one and 256 inline items. Their item storage, text
views and zero levels are unchanged. Separate checks cover RTL numeric levels,
inline ASCII overrides, Hebrew, Arabic numbers, explicit overrides and isolates,
and atom/break/end-marker handling. Existing layout/Chrome tests continue to
check the full rendered behavior.

Timing evidence: `.utmp/inventory-chrome-20261031/comparison-v20/`.
Build, test and render evidence: `.utmp/inventory-bidi-20261031/`.
[Tracked measurement and verification receipt](inventory-bidi-20261031.json).

## Verification

The complete qualified release gate passes with exit code 0: **17 GCC suites
and 19 Clang ASan/UBSan suites**. Main core counts remain 807,102 and 807,096;
the new bidi suite adds 792 separate checks on both configurations. All 341
tracked Chrome layout captures pass the 1.5 px ceiling, including 69 hand cases.
Browser behavior passes 66 scripts with the existing IME exception.

Windows Unity and Godot native libraries were rebuilt and installed. Unity full
EditMode passes 255 tests with two existing environment-gated inconclusive
results; its Windows Mono player passes 7/7. All sixteen player captures are
RGB-identical to the preceding grapheme build, and the packaged font/binding
capture was inspected. Godot font safety passes 19,833 checks; Unicode, native
exports, backend renders and live scenes pass. Its final render is also
RGB-identical to that build and was inspected. This adds no IL2CPP or hardware
qualification. The final Agentic executable hash matches the measured candidate.
