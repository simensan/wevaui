# Historical readiness through build205

This snapshot preserves prior requirement wording and measurements. It is not current qualification.

# Godot product readiness

This remains a development preview; the game-UI goal is not complete.

Both local addons now include the animation fixes (build205, ABI minor24):
conditional keyframes, nested keyframe layer precedence, independent effect
clocks, CSS pause/resume and delayed starts. Duplicate-name clock matching
follows Chrome occurrence order; the CSS draft difference is recorded in the
evidence. All 14 core suites, 16 sanitizer gates, 43 host entries (35,325
assertions), the expanded 67-check headless/rendered animation scene,
sample/export checks and 19 installed smoke suites pass. Desktop and automatic
1080p busy-scene timing pass across six runs each. Current 4K, longer lifecycle,
lower-end hardware and broader CSS animation conformance remain open.
[Installed animation evidence](verification/conditional-keyframes.json).

Current 4K qualification fails 19 of 276 timing checks. All failures are
whole-frame limits; UI API/core CPU limits pass. This is not yet a 4K 60 FPS
qualification. Functional checks pass; the frame-time cause remains unresolved.
[Current 4K evidence](verification/load205-4k.json).

The current lifecycle test passes 200 recreations and a 600-second mixed-script
soak (318,011 frames). Cold construction is 100.158 ms CPU; prepared reuse is
0.384 ms CPU p95. Whole-process private-memory minute medians rise from
822.824 to 824.008 MiB; this finite observation does not establish leak freedom.
The short 4K frame-phase diagnostic did not reproduce the stalls, so the
failed 4K timing qualification remains unresolved.
[Lifecycle evidence](verification/lifecycle205.json).
[Frame-phase diagnostic](verification/frame-phases205.json).

The preceding build204 introduced unsupported at-rule diagnostics (ABI
minor24). Replacing CSS warns about unsupported rules such as `@font-face`;
`get_css_diagnostics()` queries them without triggering document updates.
All 14 core suites, 16 sanitizer gates, 35,260 host assertions, rendered checks,
sample/exports and 72 desktop timing checks pass. This does not implement font
loading. All 276 automatic 1080p busy-scene timing checks also pass across six
focused runs. Current 4K and lifecycle qualification remain open.
[API scope](CSS_DIAGNOSTICS.md). [Installed evidence](verification/css-diagnostics.json).

The preceding build203 introduced the at-rule containment fix. Unknown
at-rule blocks and keyframe selectors no longer leak into ordinary HUD styles.
Six Chrome cases, all 14 core suites, all 16 sanitizer gates, 35,234 host checks,
renderer checks and sample/exports pass. All 72 desktop timing checks pass at the
profile's declared 1280×720/manual-update scope. Current busy-scene, 4K and
lifecycle qualification remain open; prior build202 results are historical.
Font-face loading and unsupported-feature diagnostics remain open.
[Installed regression evidence](verification/unknown-at-rules.json).

The preceding build202 introduced popover transition handlers: opening
vetoes, ordered closing events, safe handler mutations and bounded replacement
processing under queue pressure. All 14 core suites, 16 sanitizer gates,
35,222 host checks, sample/export checks, both renderer runs and 16 installed
smoke suites pass. All 72 desktop timing gates pass across six focused runs.
The current 1080p busy-scene run passes 275/276 timing checks: one OpenGL
slider changed-frame p95 is 17.187 ms against 16.667 ms. All UI API/core limits
pass; the cause of the frame delay is unresolved. Four targeted onscreen/offscreen
runs did not establish a window-position cause; the original failure remains.
Current 4K qualification remains pending.

The current lifecycle run passes 200 recreations and five minutes of Unicode
updates (22,103 name changes). Node/object counts stay at 496/1,949; whole-process
private-memory minute medians range 859.349–859.845 MB. Cold construction takes
84.389 ms CPU; prepared reuse with data changes takes 0.544 ms CPU p95 and
12.447 ms through drawing. This verifies short-run reuse, not hours-long leak
freedom or lower-end hardware. [Lifecycle evidence](verification/lifecycle202.json).
[Frame diagnostic](verification/presentation202.json).
[Installed qualification](verification/popover-beforetoggle.json).

The preceding build201 introduced direct `column-span:all` headings (build201),
with independent column balancing before/after headings, collapsing margins
between consecutive headings, and runtime style updates. All 216 Chrome cases,
14 core suites, 16 sanitizer gates, 33,833 host checks, sample/export checks and
15 post-install smoke suites pass. All 72 desktop timing gates pass across six
runs, with focus retained throughout. All 276 timing gates also pass in the 1080p busy 3D scene across six runs,
with focus retained throughout. Current 4K and lifecycle qualification remains
open; the older build198 results below do not qualify this binary.
Nested spanners and paragraph fragmentation remain incomplete.
[Build201 qualification](verification/column-span.json).

The preceding build200 introduced automatic closed-select sizing.
`width:auto` fits option labels, including grouped-option indentation and text
spacing; block layout and width constraints are covered. All 120 shared-font
Chrome comparisons, 14 core suites, 16 sanitizer gates, 28,792 host checks,
sample/export checks and 15 post-install smoke suites pass. All 72 build200 desktop
timing gates pass across six runs, with focus retained throughout.
[Build200 qualification](verification/select-intrinsic.json).

The preceding build199 introduced compact select sizing. Dropdowns and
listboxes honor authored widths and maximum widths without an extra minimum-width
override. All 14 core suites, 28,792 host checks, both rendered form suites,
sample/export checks and 15 post-install smoke suites pass. All 72 desktop timing
gates pass across six runs with focus retained throughout. Changed API p95 ranges
are 1.097–1.280 ms for inventory sorting, 1.748–1.919 ms for settings toggles and
0.395–0.478 ms for vitals at 10Hz. These are separate measurements, not causal
speedups; the build198 load/lifecycle measurements below remain historical.
[Build199 qualification](verification/select-width.json).

Both local addons now include popover autofocus/restoration, sibling/parent dismissal, modal layout-index reuse and suppression of
duplicate model notifications on input commit, alongside the earlier stacking,
clipping and opacity corrections. All 15 post-install smoke suites pass,
including binding and Frontier sample integration. Native host checks and
packaged sample/debug/release/embedded export checks also pass. The current
gesture build passes 28,776 host checks, all 14 core suites and 16 sanitizer
gates. These counts and sanitizer results describe build198 before the select
stylesheet correction. [Previous qualification](verification/popover-outside.json).

The preceding gesture build (198) passes all 72 desktop timing gates across
six runs, with focus retained throughout. API p95 is 0.454–0.565 ms for typing,
0.963–1.167 ms for inventory sorting, 1.562–1.948 ms for settings toggles and
0.380–0.515 ms for vitals at 10Hz. Its 1080p 3D profile passes all 276 timing
gates. At 4K, 260/276 gates pass: all 16 failures are mobile-renderer whole-frame
or changed-frame intervals above the 16.667 ms limit, ranging from 16.698 to
21.408 ms. Two failures occur with UI disabled (17.301 and 16.770 ms), so these
results do not isolate UI overhead or establish the cause. Functional checks
pass in all profiles, with focus retained throughout. These are separate-run
measurements, not causal speedups.

Build198 also passes 200 recreations and a five-minute Unicode soak.
Prepared reuse with hidden updates costs 0.522 ms CPU p95 and 10.748 ms through
draw p95; cold construction costs 80.612 ms CPU. Cold/prepared captures match.
Sampled nodes stay at 496 and objects at 1,949; minute-median private process
memory settles at 862.831 MB. This is a five-minute whole-process observation,
not hours-long leak proof or a timing-budget pass.
[Build198 lifecycle evidence](verification/lifecycle198.json).

The preceding stack build (196) passes all 72 desktop timing gates across six runs,
with focus retained throughout. API p95 ranges are 0.484–0.621 ms for typing,
1.023–1.462 ms for inventory sorting, 1.421–2.086 ms for settings toggles and
0.453–0.558 ms for vitals at 10Hz. These runs use 600 frames per workload;
the 1Hz clock therefore has only ten changed samples per run. The current
automatic 1080p 3D profile also passes all 276 timing gates with focus retained
throughout. Its 4K profile also passes all 276 gates with focus retained.
These are measurements on the recorded Windows machine, not broader device
certification or evidence of a causal speedup. These remain historical results;
the installed gesture build (198) has the separate results above.

On the preceding build (195), focused automatic 1080p and 4K 3D profiles each pass all 276 timing gates on
the tested Windows machine. The full desktop profile remains 71/72: one
ten-change clock run measured 3.066 ms against 3 ms. A supplemental clock-only
measurement with 100 changes per run passes the same limit in all six runs,
at 0.506–0.922 ms p95. Every measured frame retains window focus. The earlier
failure is retained; the longer run does not regrade it or establish its cause.
[Previous build qualification](verification/binding-commit.json). These load
measurements remain historical until current profiles finish.

The preceding build passes 200 recreations and a five-minute Unicode soak
of 210,822 frames. Prepared reuse with hidden data updates costs 0.369 ms CPU
p95 and 1.947 ms through draw p95; cold construction costs 82.970 ms CPU.
Cold/prepared captures match. Sampled nodes remain at 496, objects range from
1,951 to 1,952, and private process memory settles near 862.74 MB. This is a
whole-process observation, not hours-long leak proof or device certification.
[Lifecycle evidence](verification/lifecycle195.json).

## Requirement matrix

The installed outside-dismissal change fixes six newly reproduced cases:
manual overlays no longer shield automatic menus from outside clicks, nested
menus close down to the clicked parent, and keyboard activation does not dismiss
menus. Dismissal now uses press/release menu ancestry: gestures entirely outside
close menus even when they end on a different element; crossing a menu boundary
does not dismiss it. All 1,199 native dialog checks and 14 core suites pass,
including blank-space and pointer-cancellation cases. All 16 sanitizer gates,
39 host entries and sample/render/export checks pass. Both addon copies pass
15 post-install smoke suites. [Evidence](verification/popover-outside.json).

The installed focus change applies popover autofocus and restores
focus when closing the first automatic/hint popover in a stack. All 1,171 native
dialog checks and 14 core suites pass. The 48 Chrome opening/settled-focus
comparisons agree; manual-popover blur happens earlier in Godot than in Chrome,
so immediate event timing remains a documented difference. Replacement now
suppresses restoration, matching all 144 settled-focus steps in 36 nested and
sibling scenarios. Core deletion/reload cases, all 16 sanitizer gates and broader
host/render/export checks pass. Both local addons pass 15 post-install smoke suites.
[Focus candidate](verification/popover-focus.json).

A new Chrome comparison exposed three popover stack failures in build 195:
opening a sibling auto popover left an existing auto or hint open,
and opening a hint leaves an existing hint open. The expanded native dialog
suite reports 601 checks with these three failures. The earlier post-install
smokes did not cover this behavior. A source/native candidate now fixes sibling
dismissal while preserving DOM-nested and trigger-linked parents. Its native
dialog suite passes 601 checks, and all 14 core suites pass, including 27 new
mode/relationship cases. The candidate now also closes automatic/hint submenus
when their parent is hidden or its popover mode changes, preserving manual
popovers. All 14 core suites pass with 54 additional Chrome-derived dismissal
cases. Three-level menu coverage exposed independently reopened hints being
closed with an unrelated earlier menu. The candidate now records opening
ancestry: all 979 native checks pass, including 378 steps across 54 Chrome
chain scenarios. All 14 core suites, 16 sanitizer gates and the full host,
sample/render/export checks pass. This fix is installed in both local addons,
with all 15 post-install smoke suites passing.
[Reproduction](verification/popover-stack.json).

“Verified subset” means the listed behavior has current evidence; it does not
extend to every browser feature or every device. “Historical” means the existing
measurement is useful but was not repeated on the current preview. No open item below is
silently waived by the passing checks above.

| Requirement | Current assessment and next evidence needed |
|---|---|
| Live stylesheet replacement | Verified subset: current core/host suites cover replacing/removing rules and retained control state. |
| Fresh-project import | Verified on the tested Windows engine in the current import and fresh consumer checks. Repeat for every declared native platform. |
| Exported artwork and markup | Verified Windows subset: package example and relocated exports cover HTML/CSS and imported artwork. |
| Reproducible native builds | Package/build metadata matches source and dependency content; toolchain configuration is recorded. Cross-toolchain bit-identical builds are not established. See RELEASE.md. |
| Installable addon | Current Windows package and fresh consumer pass. Historical Linux package results cannot certify the current preview. |
| Native desktop exports | Current patched Windows debug/release/embedded exports pass. Other platforms and stock-engine safety remain unverified. |
| Replaced images in flex layouts | Current intrinsic-size suite and rendered example pass. Broader layout agreement is assessed separately below. |
| Native GUI integration | Current input suite covers Control focus, native overlays, document visibility and routed input. Physical touch/gamepad acceptance is still needed. |
| Text editing and popup lifecycle | The native dialog suite now passes 1,199 checks, including return values, cancellation/veto, popup ordering, nested menu dismissal, autofocus/restoration and pointer boundaries. Form method=dialog passes the validation/dialog suites. Beforetoggle, full browser task timing and broader physical input-method acceptance remain open. See the installed [gesture](verification/popover-outside.json) and [focus](verification/popover-focus.json) evidence. |
| Keyboard form actions | Current keyboard suite passes. Shared activation exists; complete form validation, picker behavior and command lifecycle are not thereby verified. |
| Two-way input bindings | Current binding, form-state and sample checks pass, including boolean and Unicode changes. |
| IME composition | Current simulated composition checks pass on the patched engine. Physical Windows IME and broader input-method acceptance are missing. Historical Linux diagnostic patches are separate. |
| Long fields and Unicode editing | Current editing/autoscroll/maxlength/typeahead checks pass. Bidi caret behavior and broader language tailoring remain open. |
| Native font positioning and themes | Current theme/font/inheritance checks pass. Core source now routes registered native family metrics to the same backend face for painting, controls, popup labels and caret geometry, with a headless regression for family widths and rendered faces. ABI minor 14 source adds native family registration with replacement, removal, reload and shaper lifecycle tests. The installed preview retains resource registration for per-element CSS families; current headless host tests pass; the 127-per-renderer theme/font pixel qualification was performed on preview105. Packaging, exports, the existing desktop timing budget and installation pass. CSS @font-face remains open. |
| Viewport-relative font-size cache | Current font-size/line-height/inheritance suites pass. This does not establish every CSS text-layout mode. |
| Text length limits and paste | Current maxlength insertion, user-edit-aware length validity and submission checks pass. Public validity APIs are implemented; Unicode-v patterns and broader physical editing qualification remain open. |
| Native IME comparison | Historical physical Linux reproduction and private engine/input-method fixes are documented in IME.md. They do not certify the shipped Windows configuration or stock Linux. |
| Select interaction | Compact author widths and max-width override the themed 218px default. Closed controls also size automatically to option labels, including group indentation and spacing: 120 shared-font Chrome cases pass. All 14 core suites, 16 sanitizer gates, 39 host entries, sample/export checks and 15 post-install smoke suites pass. Broader language/picker behavior and exact native appearance remain partial. [Automatic sizing evidence](verification/select-intrinsic.json), [compact width evidence](verification/select-width.json). |
| Modal, inert and skipped panels | Current form-state tests cover opening/closing focus, modal scope, inertness, hidden-content editing and control paint. Dialog cancellation, results and Escape are implemented and tested; form method=dialog is implemented and tested. Beforetoggle and browser task timing remain open. |
| Ordinary runtime cost | The runner now accepts an explicit timing profile and reports functional/timing outcomes separately. Provisional development-desktop CPU targets require three runs per renderer and reject every overrun; legacy reports without a profile are behavioral evidence only. Current candidate budget evidence is tracked in PERFORMANCE.md. API CPU is not total UI/GPU cost or proof for other hardware. |
| Cold opening, reuse, busy 3D and memory | Historical build198 passes desktop and 1080p timing; 4K passes 260/276 with 16 mobile-renderer frame-budget failures, including UI-disabled runs. Its five-minute Unicode soak and 200 recreations pass, with 80.612 ms cold CPU, 0.522 ms prepared reuse CPU p95 and 10.748 ms through draw p95. Investigating frame delays, longer stability, target hardware and stock-engine safety remain open. See [build198 qualification](verification/popover-outside.json) and [build198 lifecycle](verification/lifecycle198.json). |
| Experimental retained renderer | Remains disabled by default. Existing pixel/material comparisons cover a subset; pre-draw synchronization, custom materials, GPU cost and broader lifetime qualification are still open. See [history](PRODUCT_READINESS_HISTORY.md#experimental-retained-renderer). |
| Hardware/platform acceptance | Lower-end hardware, physical touch/gamepad/IME and accessibility remain incomplete. Native exports must be verified for each platform actually declared supported. |
| Browser layout behavior | Build201 core against the frozen 309-case corpus: 285 agree, 24 differ, zero crashes. Direct spanning headings are corrected; paragraph fragmentation and vertical writing still produce large differences. Three select fixtures use outdated UA defaults in their frozen references; separate refreshed captures explain those width differences without changing the frozen report. This synthetic geometry profile does not establish native pixel parity. See [layout audit](LAYOUT_AUDIT.md) and [current evidence](verification/column-span.json); no finding is waived. |
| Remaining game form integration | CSS horizontal RTL and vertical-rl/vertical-lr range direction/orientation are now verified. Number arrow stepping is covered by 76 Chrome cases and 234 native checks. Sideways writing modes, picker values/controls and full validation remain partial or absent. Review implementations against concrete game requirements and browser probes before claiming completion. |
| Release verification | Build, package, compatibility declarations, docs and every required gate must refer to the same candidate. Current broad layout findings and external acceptance prevent an unconditional release-ready claim. |

## Next work

The modal-index change, now included in the installed addon, preserves layout indexes for HUD subtrees already
retained during modal transitions. It passes all 14 Release suites, including
30 new post-close HUD mutation comparisons, and all 28,127 native host checks.
Instrumented index time falls from 55.0 to 8.2 ms over 441 accepted transitions;
uninstrumented modal API p95 ranges overlap, so a full-input speedup is unproven.
All 16 sanitizer gates and packaged sample/debug/release export checks also
pass. The first 6,000-frame desktop run exceeded the runner's 180-second process
timeout, leaving timing ungraded. The extended repeat exited early after
unexpected mouse input reached gameplay; shutdown cause remains unproven.
That benchmark used no-focus and mouse-passthrough flags, and both
renderers pass a short functional run with injected input. The protected repeat
passes all 72 desktop timing gates across six runs with 6,000 frames per workload.
API p95 ranges are 1.297–2.660 ms for modal toggles, 0.349–0.741 ms for typing,
and 1.030–1.282 ms for inventory sorting. The 900-second process timeout changed
neither sample counts nor timing budgets. This separate modal-only build was superseded by the combined installed build. [Candidate evidence](verification/modal-index.json).

The protected performance harness requested a no-focus window flag, but did not
record actual focus. Later smoke tests show that this flag can retain existing
focus. Earlier IME coverage is therefore unknown, not proven absent. New reports
record focused frame counts; physical IME qualification remains separate.
These results do not establish a causal speedup over older runs.

The installed combined build also avoids duplicate model writes and binding refreshes
when an input event is followed by a commit with the same typed value. The
installed baseline fails two checkbox notification checks; the candidate passes
all 186 binding checks while preserving commit notifications. All 28,131 native
host checks and packaged sample/export checks pass. A 100-click probe records
100 model notifications instead of 200, and 101 binding refreshes instead of
201 including initialization, with all 100 commit events preserved. This is a
work-count reduction, not a measured frame-time speedup. The combined candidate's
focused 1080p 3D and 4K profiles each pass all 276 timing gates. Desktop passes
71 of 72: one Vulkan clock-update p95 is 3.066 ms against 3 ms, from ten changed
samples (so p95 equals the maximum). Every measured frame in all three profiles
records window focus. This preserves the earlier 1080p modal failure as historical
evidence and does not establish its cause or a causal speedup. A supplemental
clock-only measurement with 100 changes per run passes the original 3 ms limit
in all six runs: p95 is 0.506–0.922 ms, with focus retained throughout. This does
not regrade the failed full desktop profile. The combined build has since passed lifecycle and post-install checks, as recorded above.
[Binding evidence](verification/binding-commit.json).

The stacking and opacity corrections are installed. Next, investigate the
remaining 3D/4K typing and toggle timing overruns, while continuing the
remaining game-relevant behavior work below.

1. Finish broader table paint-order/filter/style combinations. Collapsed solid
   borders, span mutation, table sizing, captions and row/group corrections are
   now installed with focused Chrome and native evidence. Continue investigating
   the remaining vertical-writing, multicolumn and smaller layout
   findings. Float/BFC avoidance now passes the coverage fixture and twelve
   focused cases, including live updates; broader float combinations still need
   coverage. The height-dependent overlap and inline reflow isolation are fixed;
   the clear/margin matrix now agrees in all 64 cases. Broader collapse chains
   still need assessment. See [the full audit](LAYOUT_AUDIT.md).
2. Resolve game-relevant text/focus/lifecycle gaps, including remaining selection/value lifecycle combinations,
   CSS @font-face loading, dialog lifecycle and remaining form behavior.
3. Recheck the load/lifecycle and supported-device matrix on the eventual release
   candidate, and keep unsupported configurations explicit.

The [historical ledger](PRODUCT_READINESS_HISTORY.md) retains all previous numbered
plans, measurements, failures and requirement wording. The matrix above groups
those requirements without treating historical fixes as universal current proof.

## Focused frame-stall investigation

Six focused release runs retained paired measurements for clock and UI-disabled
workloads at automatic 1080p 3D. OpenGL captured 37.188 and 33.111 ms stalls on
frames with no clock change and only 0.0005/0.0006 ms measured UI core work.
Five UI-disabled runs also exceeded 16.667 ms at their worst interval, with zero
core updates. These observations justify investigating host/render/scheduling
costs; they do not identify a cause or isolate total UI rendering cost.
The original failed full-profile clock gate remains open. No timing acceptance
profile was applied to this focused diagnostic. [Paired evidence](verification/slowframe158.json).
