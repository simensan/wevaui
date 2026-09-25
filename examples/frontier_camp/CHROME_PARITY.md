# Frontier Camp Chrome parity

> **Historical sample checks.** “Current” and “installed” below refer to the
> named preview at the time. The latest shared-core gate passes all 334 tracked
> Chrome captures; see the [review report](../../docs/verification/review-three-days-20260915.md).
> Current authoring limits are in the [CSS reference](../../Packages/com.wevaui/Documentation~/supported-css.md).

## Broad layout audit at preview98

The latest broad audit (preview98) agrees with 287/309 Chrome152 layout captures, with 22 differences,
zero crashes and no new differing fixtures. The float coverage fixture now agrees;
vertical-writing, multicolumn and smaller findings remain open. Captures are the
unchanged fresh preview94 audit inputs. See [the full layout audit](../../docs/LAYOUT_AUDIT.md)
and [current product checklist](../../docs/PRODUCT_READINESS.md).

## Installed preview100 - initial and non-focusing selection

Programmatic first focus preserves the initial zero cursor; a first value assignment
updates an unfocused cursor. The new non-focusing setter prepares backward ranges
without moving focus. Tab selects input text while textarea navigation preserves
selection. 8,913 native checks, 546 Chrome152 checks, 10 core suites, 12 sanitizer
gates, Windows exports and normal-game workloads pass. Nine captures match preview99;
installed smoke passes 266 checks. Remaining value/reset/type and input-method
combinations are tracked in the current product checklist.

## Installed preview99 - retained field selections

Visited fields preserve caret/anchor positions when focus moves between fields or
buttons. Backward selections restore; changed blurred values move the retained
cursor to the end, while no-op assignments preserve it. Removal/reload cleanup is
included. 8,907 native checks, 538 Chrome152 checks, 10 core suites and 12 sanitizer
gates pass. Windows exports and normal game workloads pass; nine captures match
preview98. Installed smoke passes 260 checks. Initial/unvisited selection behavior
and a non-focusing selection API remain outside this verified subset.

## Installed preview98 - clearance and margins

Clearing blocks now absorb top margins, honor existing sufficient spacing and
keep the correct position when empty. Nonmatching floats do not introduce clearance.
All 64 focused Chrome layout cases agree, including parent/sibling collapse and
negative margins. Native live updates/restoration pass: 8,900 native checks,
531 Chrome152 checks, 10 core suites and 12 sanitizer gates pass overall.
Windows exports and normal game workloads pass; nine captures match preview97.
Installed smoke passes 253 checks. Historical clearance failures below are resolved
within this verified matrix; broader layout and release requirements remain.

## Installed preview97 - wrapped panel float avoidance

Panels recheck float exclusion when narrowing increases their height. Inline reflow
preserves the panel's independent float context and own style. Native/browser tests
verify panel and child coordinates after height changes and restoration. 8,868
native checks, 499 Chrome152 checks, 10 core suites and 12 sanitizer gates pass;
Windows exports and all normal game workloads pass. Nine captures match preview96.
Installed smoke passes 221 checks. Confirmed clear/margin discrepancies remain open.

## Installed preview96 - float and adjacent panel layout

Float placement respects source order and padding. Adjacent BFC panels avoid floats
with authored sizes, margins and aspect ratios, including live width updates.
8,862 native checks, 493 Chrome checks, 10 core suites and 12 sanitizer gates pass.
Patched Windows exports and normal OpenGL/Vulkan workloads pass; nine sample
captures match preview94. Installed smoke passes 215 checks. Historical counts below
remain evidence history, not current broad conformance claims.

## Installed preview94 - skipped control painting

Native control text and selection/caret overlays now honor skipped contents, and
hidden text avoids glyph preparation. Range thumbs hide while tracks, select arrows
and checkbox/radio marks remain. The native capture and Chrome probes verify these
distinctions. 8,838 native checks, 469 Chrome152 comparisons, 10 core suites,
12 sanitizer gates, exports and all normal game workloads pass. Installed smoke
passes 191 checks; nine captures match preview93. See form-state documentation
and `.utmp/safe-engine71/content-paint-*` for evidence and scope. Broader browser
appearance, selection persistence, lifecycle and platform work remain.

## Installed preview93 - skipped-content focus

Skipped block-panel contents now reject focus and text editing. The containing
box remains focusable; a field whose own contents are skipped keeps focus while
rejecting edits. Retained top-layer discovery respects the skipped boundary.
8,824 native checks, 455 Chrome152 comparisons, 10 core suites, 12 sanitizer
gates, exported builds and all normal game workloads pass. Installed smoke passes
177 checks; nine captures match preview92. See the form-state documentation for
scope and `.utmp/safe-engine71/content-focus-*` for evidence. Native form-control
overlay painting, selection persistence and broader lifecycle/platform work remain.

## Installed preview92 - dialog focus routing

Both projects use DLL SHA-256
`b581a99e4a2a36078fcd2953213a3140175ae7e34cc9ee711e7ad549f1859cd0`.
Nonmodal opening now focuses the dialog's delegate. Both modes skip inert or hidden
delegates, and nonmodal closing preserves focus deliberately moved outside.
Rejected selection requests cannot move another field's caret.

435 Chrome152 comparisons, 8,804 native checks, 10 core suites, 12 sanitizer gates,
packaged exports and all twelve normal OpenGL/Vulkan workloads pass. Nine captures
match preview91; installed smoke passes 157 checks. Evidence is documented in
`../../docs/FORM_STATE.md` and `.utmp/safe-engine71/dialog-focus-*`.
Full selection persistence, dialog event lifecycle and physical/platform validation
are not established by these checks.



## Installed preview91 — inert panel input



Both projects use Windows preview91, DLL SHA-256

`a823e713591e747d6b64c092425a5a5bc4b9310ef995ef501516a1d75ac985d3`.

Bound inert panels now reject focus, typing and pointer input. Modal dialogs escape

inert ancestors, while ordinary popovers do not; inert on the modal itself blocks

it. Re-enabling preserves values without restoring focus automatically.



419 Chrome152 comparisons, 8,788 native checks, 10 core suites, 12 sanitizer gates,

packaged exports and all normal OpenGL/Vulkan workloads pass. Nine captures match

preview90; installed smoke passes 141 checks. See `../../docs/FORM_STATE.md` and

`.utmp/safe-engine71/inert-*` for evidence. This covers the described input subset;

full Chrome parity, physical IME and other-platform acceptance remain open.



## Installed preview90 — hidden-panel focus



Both projects use Windows preview90, SHA-256

`b1cb6b45ce854f4caa6ac9ec0a20d90c372559990ec3f4870f22ca840db22643`.

Hiding a focused field through display, visibility or the hidden attribute clears

focus and blocks subsequent typing. A visible descendant override and opacity

zero preserve focus. Focus-within sibling styles settle in the same update.



All 407 Chrome152 comparisons, 8,776 native checks, 10 core suites, 12 sanitizer

gates, packaged/relocated exports and twelve normal OpenGL/Vulkan workloads pass.

Nine captures match preview89; installed smoke passes 129 checks. Evidence:

`.utmp/safe-engine71/panel-focus-*`, `panel-focus-final-host-checks/verification.json`

and `installed90-form-state.log`. These cases do not establish full browser parity.



## Previous preview89 — dialog focus restoration



Both projects use Windows preview89, SHA-256

`2224fb37858a74906deb26dc386deea075167f528ae5c58969b373068b2f775b`.

Closing stacked dialogs out of order, hiding a restoration target, or disabling

it no longer leaves keyboard input in a hidden field. Explicit focus validates

current ancestor styles; closing checks the retained focus on the next update.



All 392 Chrome152 comparisons, 8,761 native checks, 10 core suites, 12 sanitizer

gates, packaged/relocated exports and twelve normal OpenGL/Vulkan workloads pass.

Nine captures match preview88; installed smoke passes 114 checks. Evidence:

`.utmp/safe-engine71/dialog-restore-*`, `dialog-restore-fixed-host-checks/verification.json`

and `installed89-form-state.log`. Browser caret placement was explicitly controlled

for the typing test; broader focus lifecycle and browser limitations remain.



## Previous preview88 — modal input and top-layer ordering



Both projects use Windows preview88, SHA-256

`557bbf1e36f9771dd84114794972e0a29f4e2a20d71667deda55801430f8a9fc`.

Modal dialogs block background input and restore focus on close. Dialogs and

popovers render as document-root siblings in opening order, above author z-index;

ancestor transforms, clipping and opacity no longer interfere. Reverse-opening

overlapping dialogs keep their buttons clickable. Layout reuse preserves promoted

popovers inside cached DOM parents without duplicating them.



All 380 Chrome152 comparisons, 8,749 native checks, 10 core suites, 12 sanitizer

gates and packaged/relocated exports pass. All twelve normal OpenGL/Vulkan workloads

pass; nine captures match preview87. Installed smoke passes 102 checks.

Evidence: `.utmp/safe-engine71/top-order-*`, `top-order-final-host-checks/verification.json`

and `installed88-form-state.log`. Broader browser event and platform limitations

remain documented in `docs/FORM_STATE.md` and `PRODUCT_READINESS.md`.



## Previous preview87 — dialog API state checks



Both projects use Windows preview87, SHA-256

`c3ab5d03b1759a0b5ef24c127f890961426947b3b25672496e7270d8fb62dd02`.

Changing an open dialog's modality now fails without altering it; repeating

the same show mode succeeds without another event. Modal dialogs and visible

popovers reject incompatible opening calls. Dialog opening emits its toggle

notification and dismisses unrelated auto/hint popovers while preserving

ancestor and manual popovers.



All 368 Chrome comparisons, 8,738 native checks, 10 core suites, 12 sanitizer

gates and packaged/relocated exports pass. All twelve normal OpenGL/Vulkan

workloads pass; nine captures match preview86. Installed smoke passes 91 checks.

Evidence: `.utmp/safe-engine71/dialog-api-*` and `installed87-form-state.log`.

This verifies these API contracts, not full Chrome parity. Dialog Escape policy,

return values, cancellable beforetoggle and browser task coalescing remain outside

this change.



## Previous preview86 — popover notifications



Both projects use Windows preview86, SHA-256

`1a76fd91d93fe893dc06a191ebde913bfdc1ebdd7884e8bd7df22e510a084ff0`.

Changing/removing an open popover attribute now emits one close notification.

Godot's toggle signal receives captured open/closed state, including when a

handler closes a popup before the opening notification reaches subscribers.

All 333 Chrome comparisons, 8,727 native checks, 10 core suites, 12 sanitizer

gates and packaged/relocated exports pass. All twelve normal OpenGL/Vulkan

workloads pass; nine captures match preview85. Installed smoke passes 82 checks.

Evidence: `.utmp/safe-engine71/popover-event-*`,

`popover-event-verified-host-checks/verification.json` and `installed86-form-state.log`.

The C ABI retains its bounded queue; browser task coalescing and cancellable

beforetoggle are not implemented.



## Previous preview85 — modal and popover state



The previous Windows preview85 had SHA-256

`60a8e45b1713074d5a2a22f58a4f7077798fa83970ce8394d3e624623ce69934`.

`:modal`, `:popover-open` and top-layer rendering use live state. Authored

data markers cannot create that state. Opening, closing, Escape and outside

dismissal update popover CSS; equivalent keyword changes preserve visibility.

Manual and invalid popover keywords resist light dismissal.

All 314 Chrome comparisons, 8,721 native checks, 10 core suites, 12 sanitizer

gates and packaged/relocated exports pass. All twelve normal OpenGL/Vulkan

workloads pass; nine captures match preview84. Installed smoke passes 76 checks.

Evidence: `.utmp/safe-engine71/top-state-*`,

`top-state-fixed-host-checks/verification.json` and `installed85-form-state.log`.

The native Escape fixture requires keyboard focus on its Godot UI surface;

its initial unfocused failure is preserved. Fullscreen modality and native

dialog Escape policy are outside this change.



## Previous preview84 — command buttons do not submit accidentally



The previous Windows preview84 had SHA-256

`8b334b3123c2ad45ec34465a75f483e8b4f9bd393138aaa6c791bae2cf88422b`.

Pointer activation, keyboard activation, implicit Enter and `:default` now

share submit-button classification. Auto command/commandfor buttons do not

submit; explicit submit buttons still do. Enter selects the actual default,

and a disabled default blocks submission. General command dispatch is deferred.

All 295 Chrome comparisons, 8,711 native checks, 10 core suites, 12 sanitizer

gates and packaged/relocated exports pass. All twelve normal OpenGL/Vulkan

workloads pass; nine captures match preview83. Installed form-state smoke

passes 66 checks. Evidence: `.utmp/safe-engine71/submit-*` and

`installed84-form-state.log`.



## Previous preview83 — default controls



The previous Windows preview83 had SHA-256

`1ce77ed195ffe1820fc60a6119f6023fc49ffe01cfc902da4d28934b8be32521`.

`:default` identifies initial checked/selected controls and the first associated

submit button. Live ownership, type and removal changes refresh remote default

actions and descendant styles. All 275 Chrome comparisons, 8,706 native checks,

10 core suites, 12 sanitizer gates and packaged/relocated exports pass. All

twelve normal OpenGL/Vulkan survival workloads pass; nine captures match

preview82. Installed form-state smoke passes 61 checks without errors.

Evidence: `.utmp/safe-engine71/default-*` and `installed83-form-state.log`.

Chrome's stale descendant CSS after a live command-attribute change is recorded

in FORM_STATE.md. Preview84 fixes its remaining command-button submission mismatch.



## Previous preview82 — read-only and read-write styling



The previous Windows preview82 had SHA-256

`7a14d187d0e4e51c4ffaadde606da93ed7fa56fc5d44576281b27875ae9c4a94`.

Locked text fields now match `:read-only`, retain focus, and reject typing.

Unlocking through a binding restores both `:read-write` styling and editing.

Disabled fieldsets, their first legends and live input type changes are covered.

CSS also resolves contenteditable inheritance; arbitrary DOM editing remains

unsupported. All 253 Chrome form-state checks, 8,699 native checks, 10 core

suites, 12 sanitizer gates and packaged/relocated exports pass. All twelve

normal survival workloads pass on OpenGL/Vulkan, and nine captures match

preview81. Installed form-state smoke passes 54 checks without errors.

Evidence: `.utmp/safe-engine71/readwrite-*` and `installed82-form-state.log`.



## Previous preview81 — required and optional styling



The previous Windows preview81 had SHA-256

`9a332520cb5889df6ac4ed52e390f9c58e27b7c27a22df4ca68581aef490f5b3`.

`:required` and `:optional` now follow Chrome across input types, selects,

textareas and buttons, including live attributes and boolean bindings.

This implements selector styling; constraint validation remains deferred.

All 154 Chrome form-state checks, 8,691 native checks, 10 core suites,

12 sanitizer gates and packaged/relocated exports pass. All twelve normal

survival workloads pass on OpenGL and Vulkan, and nine captures match preview80.

Installed form-state smoke passes 46 checks without errors. Evidence is under

`.utmp/safe-engine71/required-*` and `installed81-form-state.log`.



## Previous preview80 — disabled hover and tooltips



The previous Windows preview80 had SHA-256

`c461f0bbc1b566376e09c661a2f0c4671a574b9e185562c3afe4fa4caba8f708`.

Disabled controls retain hover/pressed CSS, title tooltips and pointer events,

while click activation and focus remain blocked. Disabling a slider mid-drag

stops value changes. All 101 Chrome form-state checks, 8,687 native checks,

10 core suites, 12 sanitizer gates and packaged/relocated exports pass.

Nine survival captures match preview78; installed form-state smoke passes

42 checks. Evidence is under `.utmp/safe-engine71/disabled-hover-*` and

`installed80-form-state.log`. The corrected native fixture explicitly enters

its viewport before pointer motion; its initial two failures remain recorded.



## Previous preview78 — disabled form groups



The previous Windows preview78 had SHA-256

`f9b54493851a79d51e60420eeb0a0627b85764df510e8b1dee99edea4779a589`.

The first legend's controls remain usable inside a disabled fieldset. Other

controls inherit disabledness, including through nested fieldsets. Interaction

and CSS agree; disabling releases focus, and re-enabling refreshes styles.

Ordinary text no longer incorrectly matches `:enabled`.



All 97 Chrome form-state checks, 8,682 native checks, 10 core suites,

12 sanitizer gates and packaged/relocated exports pass. Nine survival captures

match preview77. Installed form-state smoke passes 37 checks. Evidence:

`.utmp/safe-engine71/fieldset-*` and `installed78-form-state.log`.



## Previous preview77 — paragraph navigation



The previous Windows preview77 had SHA-256

`97732f3c92f3f7c18493f1d7d8eae4e7c235b8b8c25ece65631bfc12edd7a182`.

Ctrl+Up/Down follows Chrome across wrapped and empty paragraphs, retains the

visual column, and supports Shift selection. The old behavior moved only one

displayed line. All 177 browser comparisons, 8,675 host checks, 302 installed

editing checks, 10 core suites, 12 sanitizer gates and packaged/relocated

exports pass. Nine survival captures match preview76. Evidence is under

`.utmp/safe-engine71/paragraph-*` and `installed77-text-editing.log`.



## Previous preview76 — smaller layout boxes



The previous Windows preview76 had SHA-256

`ba31d5140fb34a03e08d7bb1d48cd06fa3d443957037b6a804ca8e011483f66a`.

It removes the unused tab expansion map while retaining preview75 geometry and

editing behavior. Ten core suites, twelve sanitizer gates, 8,659 native checks

and packaged/relocated exports pass. All 72 paired workload captures match

preview75; installed editing passes 286 checks without warnings. The prior

169-check Chrome evidence covers the unchanged editing behavior.



## Previous preview75 — exact tab geometry



The previous Windows preview75 had SHA-256

`57f07e16d8f56b8bb8df5263f3c1cf8ea9dc6688203b73ebb84a6c34309730b9`.

Fractional and zero tab stops retain exact advances for layout, caret placement,

clicks and selection. Numeric tabs use the block font across differently styled

spans; underline, overline and strike-through span tabs without glyph ink.

The same 25 px fixture now agrees with Chrome at x=25 (previously x=27).



Verification: 169 Chrome editing comparisons, 286 installed native editing

checks without warnings, 8,659 host checks, 10 Release suites, and 12 sanitizer

gates pass. Debug/release/embedded exports pass relocation and pixel checks;

all nine survival captures match preview74. See `.utmp/safe-engine71/` logs

`tab-block-chrome2.log`, `tab-block-tests.log`, `tab-block-asan-tests.log`,

`tab-block-exports.log`, and `installed75-text-editing.log`.



## Previous preview74 — styled text and tabs



The previous Windows preview74 had SHA-256

`5a65b3165631e8f7332342294589e6b9c07d00d0390129981c0ad063472ebc00`.

It adds browser textarea wrapping defaults and explicit source mapping for

byte-preserving text transforms and expanded tabs. Trailing/consecutive tabs,

caret movement, clicks, selection and composition painting have coverage.

Tab expansion measures complete UTF-8 spans; the installed editing test passes

238 checks without warnings. The gate now rejects Unicode parsing warnings.



Chrome passes 148 editing checks, Godot passes 8,611 host checks, and ten core

plus twelve sanitizer gates pass. Debug/release/embedded exports and the

relocated survival sample pass. All nine sample captures match preview72;

all twelve normal workloads pass in the final export. See

[TEXT_EDITING.md](../../docs/TEXT_EDITING.md) and [PERFORMANCE.md](PERFORMANCE.md)

for evidence and limits. Full Unicode case conversion, precise tab-stop geometry,

bidi editing and platform-specific physical input remain outside this coverage.



## Preview72 — text interaction



The prior Windows preview72 build has SHA-256

`06417570ca0f0ba33eeafd0bce089e24b454d0cb0fb6aa52a8ddaa0896c33166`.

It fixes selection collapse, UTF-8-safe visual Up/Down movement, preferred

horizontal position across short/empty lines, wrapped Home/End, preserved-text

word breaking and clicks in later textarea words. Chrome passes 126 editing

comparisons; Godot passes 164 native editing checks and 8,537 host checks.

Ten Release and twelve ASan/UBSan gates pass. All nine sample captures match

preview71 across OpenGL, Vulkan and release export; the latest direct Frontier

geometry comparison remains the preview71 audit below.



Debug/release/embedded exports and relocated sample integration pass. All 12

normal workloads pass in the final export; [PERFORMANCE.md](PERFORMANCE.md)

records the single-run timings and their limits. Full bidi editing, expanded-tab

source mapping and platform-specific input remain outside this verified subset.

Details and evidence are in [TEXT_EDITING.md](../../docs/TEXT_EDITING.md).



## Preview71 — transformed input



The prior Windows preview71 build has SHA-256

`51146b13d875425fd0ca428ad601fd5248ebd1fab09f4093945d3e58f01e5633`.

Pointer hit testing, caret placement, range and scrollbar dragging follow CSS

transforms; dropdowns anchor to transformed control bounds. The direct Frontier

comparison still passes 1,276 geometry and 33 interaction checks. Twelve additional

Chrome hit/caret comparisons pass, alongside ten core suites, twelve ASan/UBSan

gates and 8,461 Godot host checks. Debug/release/embedded exports and relocated

consumer checks pass. Runtime manifests were updated with the binaries.



Evidence: `.utmp/safe-engine71/frontier-transform-chrome/verification.json`,

`transform-popup-host-checks/verification.json`, `transform-popup-asan-tests.log`

and `transform-exports.log`. Performance and the isolated unreproduced hover

assertion are recorded in [PERFORMANCE.md](PERFORMANCE.md); these checks do not

establish complete browser or platform conformance.



## Synthetic fallback correction — 2026-09-08



The separate native font-adapter suite exposed a regression in the initial

runtime68 bold correction: regular-font shaping also changed automatic Arabic

and emoji fallbacks, and heavy combining accents could move by one pixel.

The adapter now preserves styled fallback selection and mark attachment while

retaining regular primary advances and kerning. Repeated glyphs in the same

source cluster retain their own advance occurrences. Primary-only ordinary

labels still shape once; fallback/positioned-mark misses can shape twice.



Windows preview70, SHA-256

`7f37bcfa8497eb0b9834adf49a301d311836c74b21003ee88a78961caf94309f`,

is installed in both host and sample. Native dictionary keys are reused during

the synthesis check to avoid per-glyph string creation. It passes 19,682 native adapter checks

with caches enabled and again with both caches disabled, plus 8,456 host checks.

Fresh packaged debug/release/embedded exports pass relocation and exact

project/export pixel comparisons. All 1,276 Frontier geometry and 33 interaction

checks pass (rontier-fontkeys/verification.json). The previous installed DLL is backed up.

Evidence under `.utmp/parity68`: `adapter-final.log`,

`adapter-final-uncached.log`, `fontfinal-host-checks/verification.json`,

`fontfinal-package.log`, `fontfinal-export.log`, `fontfinal-installed.json`.

The stock-engine Unicode crash and other release requirements remain open.



## Slider appearance — 2026-09-08



The default range input no longer paints a full-height background and border

behind its native rail/thumb. Its interaction box stays available for clicks

and dragging, and author backgrounds/borders still paint normally. Core tests

verify the compact painted extent, clicks above the rail and author overrides.

The opened Frontier screenshot has been reviewed with the thin rail.

This fixes the extra decoration, not every platform's native widget appearance;

the engine's default slider footprint remains 200×18 px.



Current candidate `.utmp/parity68/range-native/bin`, SHA-256

`362354fb9229ebf9946775ca3ca511565a51845b989b724154a6b636717eea4a`, passes

10 core CTest targets, 12 ASan/UBSan targets, 8,456 native host checks and all

1,276 Frontier geometry plus 33 interaction checks. Evidence:

`range-tests3.log`, `asan-range-tests.log`, `range-host-checks/verification.json`,

`frontier-range/verification.json` under `.utmp/parity68`.

Windows preview68 packaging/export verification passes: fresh-project/pack,

debug/release/embedded exports, relocation and project/export pixel equality.

The verified DLL and matching sidecar are installed in the host and Frontier

sample. Runtime67 is backed up under `.utmp/parity68/installed-backup`.

Evidence: `package.log`, `package-export.log`, `installed.json`. A fresh check

using the installed library also passes all 1,276 geometry and 33 interaction

comparisons (`frontier-installed/verification.json`).

The separate full-release requirements remain open.



## Control text and synthetic bold — 2026-09-08



Form controls now reset inherited letter/word spacing, text transform, indent and

shadow through UA rules, with normal author overrides. This removes the 5 px map

travel-control and 2.9 px quest-filter discrepancies in the broad comparison.

The explicit-inheritance fixture also exposed doubled line insets and omitted

text-indent in intrinsic sizing; both are fixed. Five fresh browser fixtures

pass in `regressions/control-text`; broad agreements remain 281/304 with no newly

failing case. Remaining map/quest synthetic findings are below 1 px.



A separate check using identical real font bytes isolated synthetic bold's extra

advance. Godot now shapes synthetic bold through an immutable regular-font peer

and retains the bold glyphs for rasterization. It preserves regular advances and

kerning without mutating borrowed fonts. The peer shares the variant's lifetime

and immutable input key. Real-font map/quest button widths now differ from Chrome

by at most 0.03125 px (previously up to 1.82 px after the spacing reset).



Current native candidate: `.utmp/parity68/bold-native/bin`, SHA-256

`705e0278a006bbcbecf3dd949b1885ee0ee3fb1df5373fa2c30e603c18baad00`.

It passes the existing 8,440 host checks and all 1,276 Frontier geometry plus

33 interaction checks. Expanded theme-font checks pass 91 headless and 103

rendered assertions, including preserved advances, changed bold pixels, regular

restoration and cross-document cache reuse. The core passes 10 CTest targets;

ASan/UBSan passes 12 (`control-indent-tests2.log`, `asan-control-tests.log`).

The font backend is native Godot code and is covered by native tests, not the

headless core sanitizer suite. Six release performance runs pass; see [performance measurements](PERFORMANCE.md).



Evidence under `.utmp/parity68`: `direct-control-indent-summary.json`,

`bold-host-checks/verification.json`, `frontier-bold/verification.json`,

`bold-theme.log`, `bold-theme-rendered.log`, `font-probe-chrome.json`, and

`bold-host-checks/project/font-probe.json`. Runtime67 remains installed.



## Inline continuation follow-up — 2026-09-08



Inline bounds now union wrapped fragments and anonymous block continuations.

The continuation fills its containing block and excludes the promoted child's

relative/transform offset and descendant overflow. Empty decorated split

fragments now retain their line, border and padding. Bounds gathering runs on

inline queries, not on every frame; ordinary block queries keep their fast path.



All seven new `regressions/inline-bounds` fixtures and all 15 container-query

fixtures match Chrome. The broad synthetic comparison now agrees on 281/304:

28/47 samples, 47/47 hand fixtures, 206/210 harvested fixtures. `card-component`

is newly clean; no previously agreeing fixture regressed. Evidence:

`.utmp/parity68/direct-inline-summary.json`, `inline-fixtures5`, `inline-container3`.

Ten core CTest targets pass including the 47-page incremental corpus

(`.utmp/parity68/inline-verified-tests.log`). The final source passes all 12

ASan/UBSan targets (`asan-inline-final-tests.log`). Native candidate SHA-256

`58ab825f3570c5583a0eb2d506a639d6aa4567594801f76e860692bc60901a00`

passes 8,440 host checks and 1,276 Frontier geometry plus 33 interaction checks

(`inline-host-checks2/verification.json`, `frontier-inline2/verification.json`).

The candidate is isolated under `.utmp/parity68/inline-native/bin`.



Longer counterbalanced OpenGL runs did not reproduce the earlier sorting,

typing and modal slowdown; see [performance evidence](PERFORMANCE.md).

The installed runtime remains runtime67 pending the remaining release gates.



## Game UI follow-up — 2026-09-08 (in progress)



The source now fixes aspect-ratio grid feedback with unequal borders, a normal

inventory pattern where rarity adds a thicker side border. Row sizing feeds the

changed intrinsic widths back into column sizing once; the grid retains its

established auto height while tracks may overflow. This removes the accumulating

2–10 px slot, glyph and item-count placement differences in `inventory.html`.

Its remaining direct findings are font-sensitive differences below 1 px.



Evidence: all 12 focused browser cases pass; the permanent

`regressions/grid/48-grid-aspect-border-feedback` case matches a fresh Chrome capture;

nine core CTest targets pass including the 47-page incremental paint/layout

corpus. Logs: `.utmp/parity68/tests2.log`, browser fixtures and captures under

`.utmp/parity68/cases`, and `.utmp/parity68/direct-summary.json`.



Named grid placement now handles bracketed and repeated line names, occurrence

indices, forward/backward named spans, missing-name implicit lines, leading

implicit tracks, and area-generated names with explicit-line precedence. Grid

stretch also respects margins and min/max dimensions. `grid-playground.html`

now has zero direct geometry findings; the 18 named-line fixtures, area-edge

fixture and five stretch fixtures pass against fresh Chrome captures.

Versioned regression cases 48–56 live in `Tools/oracle/regressions/grid/`.

The additional cases cover implicit-grid extent and dense packing with fixed

rows or columns; all four placement cases match fresh Chrome captures.



The full core/incremental gate passes (`.utmp/parity68/tests-flow2.log`).

The candidate native build passes 8,440 host checks and all 1,276 Frontier

geometry plus 33 interaction checks (`.utmp/parity68/frontier-flow`).

The earlier grid-only candidate passes all 11 sanitizer targets

(`.utmp/parity68/asan-tests-flow.log`). Native candidate SHA-256:

`15b0635db19170a0607936cf3b422be3fa188c01b62f4b8647ee21a2ad0bae3c`.

The historical 304-case synthetic comparison remains 279 agreements:

26/47 samples, 47/47 hand cases, 206/210 harvested cases; the nine new

versioned regressions pass separately. This is not a stock-browser conformance

percentage. Snapshot: `.utmp/parity68/direct-flow-summary.json`.



Size container queries are now connected to layout in the C ABI and dump tool.

They select eligible named/unnamed ancestors, read content-box dimensions and

settle nested query changes in the same update. Normal idle frames skip query

refresh. Query results participate in cascade cache keys; changed subtrees flow

through the existing style diff and invalidation path. The `container` shorthand

also expands into its name/type longhands.



Live tests cover binding-driven width changes, viewport resizing, nested rules,

conditional pseudos, stylesheet replacement, case-sensitive names and repeated

removal/reinsertion. Each resize/binding frame matches a fresh document's complete

render output, and idle updates preserve the draw serial. All 10 core CTest targets

pass including the 47-page incremental corpus (`.utmp/parity68/tests-container-lifecycle.log`).



The 15 versioned container fixtures have 14 Chrome agreements. The remaining

`unboxed` fixture matches the query result but flags an inline wrapper's bounds;

that remains open. `combat-hud.html` has zero direct geometry findings; `menu.html`

now has only subpixel font differences. Related fixes preserve flex items'

content-based minimum under an explicit width, allow unbreakable shrink-to-fit

labels to overflow, and suppress contained intrinsic sizes in flex/grid tracks.

The historical 304-case comparison is now 280 agreements: 27/47 samples, 47/47

hand cases and 206/210 harvested cases. This remains a synthetic comparison,

not a stock-browser conformance percentage.



A focused query-input test performs 2,000 refreshes over 200 descendants with zero

warm allocations and about 0.036 ms per refresh on this machine. This measures the

query-input stage, not the full UI update. The candidate native DLL (`cdada6bd99d47ca1257e78f982fae60cf37f22c2059683e90786e65c3ec3a11e`)

passes all 8,440 host checks and Frontier's 1,276 geometry plus 33 interaction

checks (`.utmp/parity68/frontier-container-live2/verification.json`). The first

import attempt aborted while the frame-limited harness shut down; the harness now

uses Godot's import-completion option. The 11 existing sanitizer targets pass in

`asan-tests-container-live.log`; the new allocation target passes in

`asan-query-counter.log` after its test counter gained matching nothrow overloads.



Paired release exports use identical UI source hashes and three runs per runtime

on OpenGL and Vulkan, 600 frames plus 120 warmups. Both automatic and immediate

update modes pass functional assertions. The immediate-update measurements remain

an open performance concern: median changed-frame API p95 increases by 0.024–0.079

ms for HUD updates, about 0.20 ms for sorting, and 0.27–0.45 ms for typing. Modal

results vary by renderer (GL 1.577 to 1.541 ms; Vulkan 1.377 to 2.344 ms). Individual

runs show substantial variance, so this is evidence to investigate, not a causal

claim. See `.utmp/parity68/paired/comparison-manual.json`. Automatic mode's API timer

excludes deferred work and must not be compared directly with these timings.



A separate containment correction makes `contain: content` preserve content

sizing instead of treating it as `strict`. Both versioned browser fixtures in

`Tools/oracle/regressions/containment/` match fresh Chrome captures. The original

304-case comparison is unchanged (`.utmp/parity68/direct-container-summary.json`).



This is an ongoing source change. The installed and packaged

build remains runtime67 below. Remaining game UI work includes inline wrapper bounds, real-font versus

synthetic-font triage, and final performance/package verification; broader

document features are assessed separately.



## Runtime67 fixes — 2026-09-07



**The 274 geometry findings from the runtime66 Frontier audit are resolved.**

The same eleven states now pass **1,276/1,276 geometry comparisons and 33/33

behavior comparisons**, with unchanged tolerances and the exact Godot font bytes.

The check covers modal placement, responsive resizing through 4K, clicking,

Tab, typing, closing and preserved inventory scrolling. It is not full browser

conformance or a pixel equality claim.



The engine now supplies the viewport to media-query compilation, refreshes

conditional rules on resize, centers intrinsic modal sizes with auto margins,

and uses browser-compatible font extents and inline leading. Additional fixes

cover trailing letter spacing, decorated inline bounds, wrapping inline fragments,

inline-flex baselines, flex padding and fractional factors, empty-block margin

collapse, skipped hidden content, missing anchor-size fallback and control margins.

Wrapped inline paint changes invalidate their containing block so later lines

cannot retain stale backgrounds. Synthetic font arithmetic remains deterministic.



The verified DLL is installed in both the development host and Frontier Camp

addon directories, with runtime66 backups in `.utmp/parity67/installed-backup`.

The preview ZIP passes the fresh export checks.

[Paired performance results](PERFORMANCE.md) keep changed-frame UI costs in the

same range, with a maximum observed increase of 0.080 ms.



The repeatable runner accepts `--dll <candidate.dll>` to test an isolated build.

Verified Windows DLL SHA-256: `fe2afd612e731ad8b627580b1c390f6139a8a389201ce0be331c675ca7cd2133`.



Final live evidence is under `.utmp/parity67/frontier-installed/verification.json`.

Validation: nine core CTest targets pass, including the 47-page incremental

corpus; all eleven ASan/UBSan targets pass. The native host runs 8,440 checks,

and a rendered form run passes 2,180 checks. The packaged addon passes fresh

project, pack, relocated debug/release and embedded-pack exports, including

project/export pixel agreement. These export pixels are engine-to-engine checks.



The native host and rendered form checks are recorded in

`.utmp/parity67/host-checks/verification.json` and

`.utmp/parity67/form-baseline-installed.log`.



The broader synthetic corpus still has findings. It includes unsupported

container queries, vertical writing and column spanning, plus float/grid and

font-sensitive differences. Its font normalization and UA overlay prevent it

from establishing stock-browser conformance. The direct comparator now uses

DOM paths and reports unmatched visible elements. Captures skip descendants of

`content-visibility:hidden`; querying those descendants in Chrome can force layout.

The historical runtime66 numbers below remain a record of the original audit.



Current direct results: **25/47 samples, 47/47 hand cases and 206/210 harvested

cases agree (278/304 total)**. No unmatched visible elements remain. The 26 pages

with findings are retained in `.utmp/parity67/direct-verification.json`; these

are not 26 independently confirmed engine defects. Browser rounding and synthetic

font/control normalization still require individual triage.





## Historical runtime66 audit



**Runtime66 status: browser layout parity fails.** This is a fresh comparison with Chrome

152.0.7977.77 on Windows, using the installed runtime66 DLL. It does not change

the runtime or the sample's authored CSS.



## Live game UI



The isolated copy runs the actual main scene, state bindings, click handlers and

native viewport input through Godot 4.7.2/OpenGL. Chrome receives the same markup,

CSS, initial data and the exact font bytes exported from Godot's theme font.

The browser fixture expands the keyed template and implements the sample's

open/close and name-binding callbacks. It uses native `showModal()`, focus,

keyboard input and scrolling. No engine UA stylesheet or line-height

normalization is injected. Font rasterization and native widget paint remain

platform-specific; screenshots are inspection evidence, not a pixel equality gate.



Eleven states cover initial HUD, clicked modal opening, Tab to volume, replacing

the name with R, closing, overflowing inventory scrolled to 120px, reopening,

resizing the open modal to 1024×720, 1920×1080 and 3840×2160, and closing at 4K.

The overflow fixture adds twelve keyed rows. The fixture probes

29 selectors and four coordinates per selector in each state, giving **1,276

geometry comparisons**. **274 exceed the existing oracle tolerance** of

0.02px + 0.00025 × absolute native coordinate. These are repeated observations,

not 274 separate bugs. Hidden dialog descendants have zero rectangles on both sides.



**33/33 behavioral comparisons pass:** active element, edited name value and

inventory scroll offset in all eleven states. This does not cover full focus

trapping, Escape, IME, accessibility, gamepad navigation or arbitrary forms.



| Finding | Godot | Chrome | Implication |

|---|---:|---:|---|

| Modal top at 1280×720 | 146px | 217.5px | 71.5px positioning mismatch |

| Modal top at 1920×1080 | 326px | 487.5px | 161.5px mismatch |

| Modal top at 3840×2160 | 866px | 1297.5px | 431.5px mismatch |

| Modal height | 427px | 431px | Content/default metric difference |

| Reverse-order button x at 1024px | 495.6875px | 62px | Responsive rules fail to take effect |

| Initial identity panel height | 49px | 53px | Native text/line-height difference |



The modal's authored `top: calc(50% - 214px)` combines with the browser's other

insets and auto margins. The engine does not produce the browser's vertical

placement. The closed HUD also has 0.5–4px text-related geometry differences.

Native range-control painting visibly differs from Chrome even when its outer

rectangle is comparable.



The responsive finding has a concrete runtime wiring gap: `CascadeEngine` has

`set_media_context`, but runtime construction and `weva_document_set_viewport`

do not call it. The viewport setter updates layout dimensions and schedules

layout; media conditions keep their default context. This audit records the

failure without applying a CSS workaround or modifying engine behavior.



## Broad corpus



All **304 pages were captured afresh** with Chrome 152. The C++ `weva_dump` was

rebuilt from current runtime66 sources. Every resulting layout element dump is

unchanged from candidate56. Cached C# reference dumps were reused after verifying

that the HTML/CSS inputs are unchanged.



| Corpus | Cases | C#/C++ agree | Reference bugs or browser leans | Unresolved arbitration | Direct geometry leads |

|---|---:|---:|---:|---:|---:|

| Samples | 47 | 27 | 15 | 5 | 37 |

| Hand | 47 | 45 | 2 | 0 | 8 |

| Harvest | 210 | 186 | 19 | 5 | 22 |

| Total | 304 | 258 | 36 | 10 | 67 |



The three-way oracle only asks Chrome about differences **between the two

engines**. Shared browser bugs therefore pass that gate. Its ten unresolved

cases are `audit-validation`, `card-component`, `form-metrics`, `menu`,

`sample-menu`, `SnapshotMatcherTests-00`, and SpatialNavigator tests 01, 02, 05, 06.

The component case includes a browser capture/expansion defect.



A separate `chrome_sweep.py` pass flags **67 pages with direct geometry leads**;

237 have no differences among paired elements, and none crash. These are leads,

not a browser conformance percentage: the sweep uses identity/position matching,

compares only paired geometry, and does not treat all missing elements as failures.

Four candidate elements are unpaired in the samples. Hidden content, inline

fragments and component expansion also need individual triage. Examples include

~44px menu offsets, ~10px inventory offsets and ~1px layout-stress offsets.



Both broad passes use synthetic font metrics, normalize computed normal line

height and overlay the engine UA stylesheet on Chrome. They cannot establish

stock-browser default styles, widget paint, real-font behavior or interaction

parity. The live game comparison above deliberately avoids that overlay.



## Reproduce



Install Tools/Layout's existing Puppeteer dependency and the sample addon, then

run from the repository root with a new output directory:



```powershell

python hosts/godot/run_frontier_chrome.py --godot .utmp/forced-break/native/engine472/godot.exe --out .utmp/frontier-chrome-new

```



Set `PUPPETEER_EXECUTABLE_PATH` to choose another Chrome executable. The runner

writes an isolated project, native/browser PNGs, raw JSON, logs, binary/font

hashes and `verification.json`. **Exit 1 is the expected current result** because

geometry differs; process failures and script errors are reported separately.



Verified final run: `.utmp/chrome66/final-frontier/verification.json`.

Broad captures and logs: `.utmp/chrome66/captures/`, `broad-summary.json`,

`receipt.json`; the audit driver is `.utmp/chrome66/broad.py`.



Runtime66 DLL SHA-256:

`df1285043854ec8887da980ff94e8bae1fa5f2eba3710cf7b7cb571085a10b66`.

Rebuilt dump SHA-256:

`80c4a6906c25d7954df9a165373ab3657ecc252d39649d40dc344afdc5368e39`.



The runtime66 optimization preserves candidate56's broad layout output. That

is regression evidence, not Chrome parity. Modal positioning and runtime media

context need fixes before claiming that this sample matches Chrome.
