# Godot product readiness

This is a development preview. The game-UI goal is not complete.

Current source avoids temporary list vectors during scalar interpolation. A
focused allocation test falls from 18,000 to 6,000 allocations across 3,000 width,
number and percentage samples with unchanged results. All 14 core suites, 49
native host entries (35,546 checks), 16 sanitizer suites, both renderer geometry
comparisons and sample/exports pass. Desktop timing passes 72/72. Automatic
1080p 3D passes 268/276: eight OpenGL whole-frame limits fail at 16.703–17.072 ms
p95, against 16.667 ms. All UI API/core CPU limits pass. The run stopped before
4K. The ten-minute lifecycle check below passes; it is not installed. The allocation
measurement covers interpolation, not whole-document allocation or frame time.
[Interpolation allocation evidence](verification/interpolation-allocations.json).

Candidate217's ten-minute Vulkan 1080p 3D lifecycle check passes 200 recreations
and 291,547 soak frames with mixed-script name churn. Prepared reuse with hidden
data updates is 0.723 ms CPU p95; through-draw reuse is 12.611 ms p95. Cold
construction is 87.014 ms CPU, and prepared/cold images match. Soak frame p95
is 12.89 ms, maximum 50.389 ms. Private-memory minute medians rise from
821.770 to 822.773 MiB and level off late in the run, peaking at 822.984 MiB.
Recreation teardown returns to 491 nodes/1,934 objects; final soak teardown is
491 nodes/1,937 objects. This finite result does not establish leak freedom or
clear the prior timing failures. [Lifecycle evidence](verification/lifecycle217.json).

A controller now drives the document without scripting. Joypad button and
axis events reaching the focused node answer as the keyboard their `ui_*`
actions stand in for: the pad and stick move focus by geometry (a slider,
radio group or caret takes left/right first; a `<select>`, `<textarea>` or
number field takes up/down), accept is Space on a control and Enter in a
field, cancel is Escape and is consumed only when something closed. Actions
without a joypad binding fall back to A, B, the D-pad and the shoulders.
A held direction repeats after 400 ms at ten steps a second, and when no
Control holds focus the first press wakes the document on its first control
without also moving. The core adds `weva_element_tag_name` for this.
Thirty-three host checks push real joypad events through a viewport,
including the repeat timing; physical controller acceptance remains open. [Gamepad evidence](verification/gamepad-navigation.json).

Stylesheets can now declare fonts with `@font-face { font-family; src: url() }`.
The core lists the rules (ABI minor 25, `weva_document_font_faces`) and the
Godot host loads each source, resolved against the base path like an image,
and registers the family, preferring the normal weight/style rule when several
faces are declared and never replacing a family the game registered itself. A
source that cannot load warns and falls back. Eleven host checks cover loading,
base-path resolution, a missing source, precedence, face choice and removal on
CSS replacement; the ABI test covers parsing, resolution and media gating.
Per-weight face matching, `unicode-range`, `font-display` and `local()` remain
unimplemented.

The Godot font adapter now shapes document text with more than 32 emoji
sub-runs or 128 open brackets in pieces the stock engine's script iterator can
hold, splitting only where the engine itself starts a sub-run or pushes a
bracket, with right-to-left pieces kept in visual order. The embedded ICU data
gains `uemoji.icu` (14,400 bytes), which the emoji properties need. On the
official stock Windows 4.7.1 editor the standalone reproduction still fails
five of six cases, while the adapter suite passes all 19,692 checks including
five new past-limit cases, and the Frontier Camp lifecycle harness with 60-unit
mixed-script names completes where the previous library aborted with a fatal
out-of-bounds index. The patched editor passes the same suite, all 49 host
entries (35,546 checks), the sample checks, all 14 core suites and all 16
sanitizer suites. Exports and timing profiles were not rerun, the candidate
is not installed, and text in native Godot controls still needs the engine
patch. [Shaping evidence](verification/stock-engine-shaping.json).

A three-round ownership isolation on candidate217 classifies the retained
objects as bounded, not accumulating. Five short Vulkan 1080p 3D editor arms
(everything enabled; settings open/close, name churn or sorting disabled; UI
disabled) each repeat the soak and teardown three times. Every arm returns to
the same object count after each round, with zero growth between rounds and
constant texture memory. Settings open/close accounts for three retained objects,
name churn for two, sorting for none, and the UI-disabled arm also retains two.
Their individual owners remain unidentified; this is a one-time cache-sized
effect, and no further time is planned on it without a normal-use symptom.
[Ownership classification](verification/lifecycle-ownership218.json).

Attributing candidate217's failed 1080p whole-frame checks: the UI-disabled
baseline of the same busy scene measures 14.2–16.0 ms p95 in five of six runs,
so the 16.667 ms limit leaves under 1 ms for the UI. Workloads with the UI active
stay within about ±1.5 ms of their run's own baseline, UI API CPU p95 never
exceeds 1.61 ms, and all eight failures are OpenGL runs over the limit by
0.04–0.41 ms, less than the baseline's run-to-run spread. The data shows no
UI-attributable regression; the failures measure scene plus presentation cost.
The gate is unchanged and remains failed. Making it pass would require either a
UI-attributable delta limit or a lighter test scene, which is a product decision.
[Frame attribution](verification/frame-attribution217.json).

The lifecycle harness now releases its retained soak model and waits for teardown
cleanup before sampling. Short rendered editor checks verify model destruction in
both UI-present and UI-disabled runs. The corrected checkpoints still show three
additional objects after the UI-present soak (2,033 to 2,036), versus no increase
with UI disabled. Their ownership remains unresolved; these short checks do not
replace the earlier release soak.
[Lifecycle ownership diagnostic](verification/lifecycle-ownership217.json).

Latest source shortens partially reversed transitions, so a halfway hover reversal
returns in half the original linear duration. Repeated reversals preserve the
shortening state; redirecting to a third value resets it. Positive delays stay
unchanged and negative delays shorten with the duration. Eight baseline
assertions fail; 14 core suites and four Chrome cases pass after the fix.
Eight additional nonlinear Chrome cases exposed four mismatched samples: negative
overshoot and reversal while a stepped value was still held. These now pass,
along with all 14 core suites. The unchanged-style early return now cancels a
transition whose new target already equals the displayed value. Negative
overshoot follows Chromium's clamp, differing from the draft's absolute-value
formula. The native candidate passes 49 host entries (35,546 checks), all 16
sanitizer suites, 32 geometry samples under each renderer and sample/exports.
Desktop timing passes 70/72: two OpenGL idle API p95 values are 0.145/0.242 ms
against 0.05 ms. Their core p95 is 0.0007 ms. A subsequent ABBA previous/current
comparison, 6,000 idle frames per run, reports 0.010 ms API p95 in all four runs
with similar means and p99. It does not reproduce the short-run failure and does
not replace that failed gate. 3D/4K were not run. Runtime allocation and lifecycle
qualification remain pending; it is not installed.
[Idle comparison](verification/idle216-paired.json).
[Nonlinear evidence](verification/eased-reversal.json).
[Reversal evidence](verification/transition-reversal.json).

The latest source fixes cancellation when an animated property is removed from
`transition-property`, including simultaneous target changes. Changing duration
alone preserves an existing transition; retargeting with zero duration cancels
it and displays the new target. Sixteen baseline assertions fail; all 14 core
suites and six Chrome cases pass after the fix. The native candidate passes
48 host entries (35,514 checks), 16 sanitizer suites, sample/exports and 30
rendered checks per renderer. The old library fails 11 checks per renderer;
the fixed library stops canceled transitions from changing values or redrawing.
Desktop timing passes 72/72. Automatic 1080p 3D passes 267/276; nine whole-frame
limits fail at 16.754–17.401 ms p95, including an idle Vulkan run. All UI API/core
CPU limits pass. Changed-frame core p95 ranges across six runs are 0.439–0.753 ms
for inventory sorting, 0.670–1.056 ms for settings toggles, 0.143–0.298 ms for
typing and 0.194–0.389 ms for 10 Hz vitals. These core timings exclude binding,
host submission and GPU work and are not total UI frame costs. The run stopped
before 4K. The candidate is not installed; lifecycle qualification remains open.
[Cancellation evidence](verification/transition-cancellation.json).

A paired Vulkan 1080p diagnostic excluded only `VK_LAYER_NV_present` in the
child process; loader logs confirm exclusion. UI-disabled whole-frame p95 was
15.387/15.196 ms in normal runs and 14.882/16.068 ms in excluded runs. All four
runs exceeded 20 ms maximum with zero UI core updates. This small comparison
shows no consistent improvement. Godot's fixed-fps path also bypasses its explicit
frame-delay sleep; driver/OS waits remain possible. No global settings changed.
[Presentation-layer diagnostic](verification/nv-present215-paired.json).

Current source additionally fixes zero-duration transitions with positive delay
and avoids starting transitions already exhausted by a negative delay. Waiting
and stepped transitions now skip output invalidation while the displayed value
is unchanged. Six baseline assertions fail; all 14 core suites and four Chrome
cases pass after the fix, with additional draw-serial coverage for stepped
timing. The native candidate passes 47 host entries (35,496 checks), 16 sanitizer
suites and 10 rendered checks per renderer. The previous library fails three
checks per renderer, including redraws while waiting; the fixed library produces
no redraws during the tested delay. Sample and export checks pass. Desktop
timing passes 72/72; 1080p 3D passes 272/276. One OpenGL run fails typing
whole-frame/changed-frame p95 at 16.859/17.261 ms and slider at
16.723/17.228 ms (16.667 ms limits). All UI API/core CPU checks pass.
The run stops before 4K. This candidate remains uninstalled; the cause of
whole-frame stalls remains unresolved.
[Delayed-transition evidence](verification/delayed-transitions.json).

An uninstalled candidate removes the 32-entry transition-property limit and
eight-entry animation-name limit. The former uses a linear allocation-free scan;
the latter sizes clock storage on name-list changes and preserves elapsed time
when a late effect moves to the front. Seven transition and eight animation
assertions fail before their fixes. All 14 core suites, 16 sanitizer suites,
46 host entries (35,490 checks), Chrome comparisons, both renderer geometry
smokes and sample/exports pass. Desktop timing passes 72/72 and 1080p 3D
passes 276/276. The 4K profile passes 272/276: one Vulkan run fails slider
whole-frame/changed-frame p95 at 18.204/20.346 ms and toggle at
17.687/18.338 ms (16.667 ms limits). All UI API/core CPU limits pass.
The candidate remains uninstalled; stall investigation and lifecycle qualification
remain open. [Long-list evidence](verification/long-effect-lists.json).

Both local addons now use build211 (ABI minor24), which adds incremental font
warmup and the sample loading path. All 44 native host entries (35,468 checks),
multilingual cold/partial/full render comparisons, sample/exports and 20 installed
smoke suites pass. Current timing passes 72/72 desktop, 276/276 automatic 1080p
3D and 276/276 automatic 4K 3D checks on Windows RTX 5080. Core and sanitizer
evidence is inherited from the unchanged core in build210; host changes were
tested natively. Current lifecycle and lower-end qualification remain open.
[Current installed qualification](verification/font-warmup.json).

A pending core fix cancels transitions in `display:none` panels, preserves
changes made while hidden and avoids transitions from hidden values when a
panel reopens. Eight assertions fail before the fix; all 14 core suites pass
afterwards, including normal visible transitions and `visibility:hidden`.
Chrome confirms both self/ancestor cases. All 45 host entries (35,474 checks),
10 rendered checks per renderer, 16 sanitizer gates and sample/exports pass.
Desktop passes 72/72 and automatic 1080p 3D passes 276/276 timing checks. The
4K profile passes 273/276: OpenGL clock changed-frame p95 is 17.383 ms, Vulkan
UI-disabled whole-frame p95 is 16.825 ms, and another Vulkan clock run is
18.09 ms, against 16.667 ms. All UI API/core CPU checks pass; the whole-frame
failure remains unresolved and this candidate is not installed.
[Hidden-transition evidence](verification/hidden-transitions.json).

A paired Vulkan 4K diagnostic excluded the two installed Overwolf layers only
in child test processes, with loader logs confirming their exclusion. Across
normal/disabled/disabled/normal runs (6,000 frames per workload), UI-disabled
p95 was 1.329/1.024/1.021/1.032 ms. An excluded run still had an 85.777 ms
UI-disabled frame with zero core updates. Layer exclusion therefore does not
eliminate stalls; these runs neither reproduce nor clear the earlier p95 gate
failure. No global settings changed.
[Paired overlay diagnostic](verification/overwolf212-paired.json).

The preceding installed build210 included animation composition,
hidden-panel cancellation/restart, redundant host redraw suppression and removal
of temporary attribute-name allocations during binding refreshes. All 14 core
suites, 16 sanitizer gates, 43 host entries (35,464 checks), 207 rendered animation
checks per renderer, sample/exports and 19 installed smoke suites pass.
The current 600-frame-per-workload profiles pass 72/72 desktop, 276/276 automatic
1080p 3D and 276/276 automatic 4K 3D timing checks across six runs each on the
Windows RTX 5080 machine. Lower-end qualification remains open; earlier
intermittent failures below are preserved, with causes unresolved.
[Build210 qualification](verification/binding-names-installed.json).

Build210's ten-minute Vulkan 1080p 3D lifecycle check passed 200 recreations
and 287,158 soak frames with mixed-script names. Cold construction is 92.684 ms
CPU (94.517 ms through draw); hidden preparation is 29.535 ms. Prepared reuse
with data updates is 0.767 ms CPU p95 and 12.962 ms through draw p95. Prepared
and cold images match. Soak frame p95 is 12.84 ms, maximum 31.265 ms. Private
memory minute medians rise from 821.680 to 823.152 MiB, peaking at 823.621 MiB.
Recreation teardown counts remain 491 nodes/1,934 objects; final teardown after
the soak is 491/1,939. This finite observation does not establish leak freedom
or explain presentation stalls. Cold construction remains too expensive to put
on an interactive frame; prepare during loading and reuse the existing UI.
[Build210 lifecycle evidence](verification/lifecycle210.json).

The current build adds incremental font warmup and a sample loading path.
It preserves the complete fallback chain and passes 44 host entries, sample and
exports. Cold/partial/full startup text captures match the installed build on
both renderers. A focused multilingual document constructs in about 22 ms after
warmup versus 64–67 ms without it; font work moves to earlier loading frames.
One font load can still exceed 16.67 ms. The runtime profiles above pass; this
does not remove total startup work. [Warmup evidence](verification/font-warmup.json).

The preceding installed build207 corrected zero-duration,
fractional-iteration and reversed animation endpoints, including zero-count
shorthand parsing. The native scene matches 112 Chrome endpoint measurements.
All 14 core suites, 16 sanitizer gates, 43 host entries (35,442 checks), the
182-check rendered animation scene, sample/export checks and 19 installed smoke
suites pass. [Current qualification](verification/animation-endpoints.json).

The 6,000-frame-per-workload desktop profile passes 72/72 timing checks.
Automatic 1080p and 4K 3D profiles each pass 276/276 checks across six focused
runs on Windows RTX 5080. The original 600-frame desktop profile failed one
Vulkan hover check (0.536 ms against 0.5 ms); the longer profile and an old/new
comparison did not reproduce it. Its cause remains unproven and the failure is
preserved. Current lifecycle and lower-end hardware qualification remain open.
[Hover comparison](verification/hover207-paired.json).

The preceding build206 passed full 4K timing. Its longer plain/traced comparison
did not reproduce earlier whole-frame stalls. This does not establish their
cause. [Build206 4K](verification/load206-4k.json),
[paired diagnostic](verification/frame206-paired.json).

The preceding
build205 failed 19/276 4K whole-frame limits, although UI API/core CPU budgets
passed. A short instrumented trace did not reproduce the stalls; their cause
remains unresolved. Its ten-minute Unicode lifecycle run passed 200 recreations
and 318,011 frames, with cold construction at 100.158 ms CPU and prepared reuse
at 0.384 ms CPU p95. Private-memory minute medians rose from 822.824 to
824.008 MiB. These are historical observations, not current binary or leak-free
certification. [4K](verification/load205-4k.json),
[lifecycle](verification/lifecycle205.json), [trace](verification/frame-phases205.json).

The table retains the full requirement scope. Passing a listed suite establishes
its tested subset, not all browser behavior or device support. Detailed earlier
claims and failures are preserved in the [build205 snapshot](PRODUCT_READINESS_THROUGH_205.md)
and [earlier history](PRODUCT_READINESS_HISTORY.md).

An earlier composition/redraw candidate fixed overlapping animations erasing each
other and suppresses redundant host redraws. Correctness, sanitizer, export and
desktop timing checks pass, but its 1080p whole-frame profile fails 46/276 checks.
An installed/candidate comparison reproduces stalls on both binaries with UI
disabled. Process-local sampling locates main-thread waits in NVIDIA OpenGL
presentation beneath `SwapBuffers` (453 of 1,922 sampled stacks). Sampling
perturbs timing and does not establish the condition causing variable waits.
The failed profile remains failed; that candidate was not installed.
[Composition evidence](verification/animation-composition.json),
[presentation stacks](verification/presentation-stack208.json).

A subsequent candidate cancelled CSS animations under `display:none`,
including descendants, and restarts them when the panel reopens. Hidden updates
retain the draw serial; `visibility:hidden` continues animating. Eight regression
assertions fail before the fix. All 14 core suites and the native scene pass
(204 headless checks; 207 each on OpenGL and Vulkan). All 43 host entries
(35,464 checks), 16 sanitizer gates and sample/export checks pass. A focused
64-element headless comparison reduces hidden update API p95 from 0.082–0.085 ms
to 0.001 ms. Visible animation core mean changes from 0.358–0.366 ms to
0.369–0.370 ms; this is a CPU microbenchmark, not rendered frame timing.

The candidate's desktop profile passes 70/72 checks. First-run OpenGL clock
updates reach 3.258 ms against 3 ms and redundant signals reach 0.321 ms p95
against 0.3 ms. The other five runs pass those checks. A separate 6,000-frame
old/new/new/old comparison does not reproduce either limit violation or show
a consistent slowdown; the full profile remains failed. Automatic 1080p/4K
profiles were not started after the desktop failure, and lifecycle is pending.
[Hidden-panel animation evidence](verification/hidden-animations.json).

A further source change removed temporary long attribute-name copies during
binding refreshes. The no-change allocation fixture falls from 3,000 allocations
to zero over 1,000 refreshes, preserving all 3,000 resolver reads and subsequent
data mutations. Native/export/timing qualification subsequently passed for the
current installation linked above. This does not explain the earlier desktop timing
failures above. [Binding allocation evidence](verification/binding-attribute-names.json).

## Requirement matrix

| Requirement | Current assessment and next evidence needed |
|---|---|
| Live stylesheet replacement | Verified subset: current core/host suites cover replacing/removing rules and retained control state. |
| Fresh-project import | Verified on the tested Windows engine in the current import and fresh consumer checks. Repeat for every declared native platform. |
| Exported artwork and markup | Verified Windows subset: package example and relocated exports cover HTML/CSS and imported artwork. |
| Reproducible native builds | Package/build metadata matches source and dependency content; toolchain configuration is recorded. Cross-toolchain bit-identical builds are not established. See RELEASE.md. |
| Installable addon | Current Windows package and fresh consumer pass. Historical Linux package results cannot certify the current preview. |
| Native desktop exports | Current patched Windows debug/release/embedded exports pass. Document text past the engine's shaping limits now works on the stock Windows 4.7.1 editor; stock exports, other platforms and native-control text remain unverified. |
| Replaced images in flex layouts | Current intrinsic-size suite and rendered example pass. Broader layout agreement is assessed separately below. |
| Native GUI integration | Current input suite covers Control focus, native overlays, document visibility and routed input. Joypad events now drive HTML focus, activation and dismissal through the project's ui_* actions with conventional button fallbacks (gamepad_navigation_tests: 33 checks with real joypad button and axis events, including wake on first press and held-direction repeat). Physical touch/gamepad acceptance is still needed. |
| Text editing and popup lifecycle | Current dialog suite: 1,199 checks; popover beforetoggle suite: 1,389 checks, including opening vetoes, ordered closing, mutation and queue-pressure cases. Form method=dialog is covered. Full browser task timing, remaining dialog lifecycle and physical input-method acceptance remain open. [Popover evidence](verification/popover-beforetoggle.json). |
| Keyboard form actions | Current keyboard suite passes. Shared activation exists; complete form validation, picker behavior and command lifecycle are not thereby verified. |
| Two-way input bindings | Current binding, form-state and sample checks pass, including boolean and Unicode changes. |
| IME composition | Current simulated composition checks pass on the patched engine. Physical Windows IME and broader input-method acceptance are missing. Historical Linux diagnostic patches are separate. |
| Long fields and Unicode editing | Current editing/autoscroll/maxlength/typeahead checks pass. Bidi caret behavior and broader language tailoring remain open. |
| Native font positioning and themes | Current headless theme/font/inheritance and native family registration paths pass. The detailed theme/font pixel qualification was on preview105 and remains historical. CSS `@font-face` now loads and registers one face per family on the Godot host (font_face_tests: loading, base-path resolution, missing source, precedence, face choice, removal); per-weight/style face matching, `unicode-range` and broader text layout remain open; unsupported at-rules produce diagnostics. |
| Viewport-relative font-size cache | Current font-size/line-height/inheritance suites pass. This does not establish every CSS text-layout mode. |
| Text length limits and paste | Current maxlength insertion, user-edit-aware length validity and submission checks pass. Public validity APIs are implemented; Unicode-v patterns and broader physical editing qualification remain open. |
| Native IME comparison | Historical physical Linux reproduction and private engine/input-method fixes are documented in IME.md. They do not certify the shipped Windows configuration or stock Linux. |
| Select interaction | Compact author widths and max-width override the themed 218px default. Closed controls also size automatically to option labels, including group indentation and spacing: 120 shared-font Chrome cases pass. All 14 core suites, 16 sanitizer gates, 39 host entries, sample/export checks and 15 post-install smoke suites pass. Broader language/picker behavior and exact native appearance remain partial. [Automatic sizing evidence](verification/select-intrinsic.json), [compact width evidence](verification/select-width.json). |
| Modal, inert and skipped panels | Current form-state/dialog tests cover focus, modal scope, inertness, hidden-content editing, cancellation, return values, Escape and form method=dialog. Popover beforetoggle is verified separately; broader dialog events and browser task timing remain open. |
| Ordinary runtime cost | The runner now accepts an explicit timing profile and reports functional/timing outcomes separately. Provisional development-desktop CPU targets require three runs per renderer and reject every overrun; legacy reports without a profile are behavioral evidence only. Current candidate budget evidence is tracked in PERFORMANCE.md. API CPU is not total UI/GPU cost or proof for other hardware. |
| Cold opening, reuse, busy 3D and memory | Current desktop and 1080p timing pass. Build205 historical lifecycle: 200 recreations, 600 seconds, 100.158 ms cold CPU and 0.384 ms prepared reuse CPU p95; private-memory minute medians rose 822.824 to 824.008 MiB. Its 4K timing failed 19/276 checks. Current 4K passes 276/276; current lifecycle and longer/device qualification remain open. [Lifecycle](verification/lifecycle205.json), [4K](verification/load205-4k.json). |
| Experimental retained renderer | Remains disabled by default. Existing pixel/material comparisons cover a subset; pre-draw synchronization, custom materials, GPU cost and broader lifetime qualification are still open. See [history](PRODUCT_READINESS_HISTORY.md#experimental-retained-renderer). |
| Hardware/platform acceptance | Lower-end hardware, physical touch/gamepad/IME and accessibility remain incomplete. Native exports must be verified for each platform actually declared supported. |
| Browser layout behavior | Build201 core against the frozen 309-case corpus: 285 agree, 24 differ, zero crashes. Direct spanning headings are corrected; paragraph fragmentation and vertical writing still produce large differences. Three select fixtures use outdated UA defaults in their frozen references; separate refreshed captures explain those width differences without changing the frozen report. This synthetic geometry profile does not establish native pixel parity. See [layout audit](LAYOUT_AUDIT.md) and [current evidence](verification/column-span.json); no finding is waived. |
| Remaining game form integration | CSS horizontal RTL and vertical-rl/vertical-lr range direction/orientation are now verified. Number arrow stepping is covered by 76 Chrome cases and 234 native checks. Sideways writing modes, picker values/controls and full validation remain partial or absent. Review implementations against concrete game requirements and browser probes before claiming completion. |
| Release verification | Build, package, compatibility declarations, docs and every required gate must refer to the same candidate. Current broad layout findings and external acceptance prevent an unconditional release-ready claim. |

## Next work

1. Diagnose the intermittent whole-frame stalls and verify the eventual release
   candidate at target resolutions and on lower-end hardware.
2. Resolve remaining game-relevant CSS/text/form behavior: animation timing and
   composition edge cases, font loading/face selection, multicolumn paragraph
   fragmentation, vertical writing, bidi caret behavior and dialog/input lifecycle.
3. Verify physical IME, touch/gamepad and accessibility, platform exports and
   longer memory/lifecycle behavior. Stock-engine Unicode safety is covered for
   document text on Windows; stock exports and native-control text remain open.
4. Keep release artifacts and all required evidence tied to the same candidate.
   Historical passes do not clear a current failure or missing requirement.
