# Godot product readiness history

These entries preserve their original wording and measurement scope. They do
not describe the current installed build. See [current readiness](PRODUCT_READINESS.md).

## Current installed build: bounded text-cache payload (checkpoint 146)

The core now limits each document's retained shaping-cache payload to 4 MiB,
alongside the 4,096-entry limit. Incremental eviction retains hot labels; oversized
results still shape correctly without being cached. The native mixed-script
36,000-frame diagnostic peaks at 3.9998 MiB versus 13.32 MiB in the baseline.

All 14 core suites, 16 sanitizer gates, 27,827 native host checks, sample/exports
and 13 installed smoke suites pass. Both performance profiles pass unchanged:
72/72 regular desktop checks and 276/276 automatic 1080p 3D checks. The earlier
two failed 3D checks remain historical evidence; this does not establish a causal
speedup or certify other hardware. Both local addon copies now use this build,
at ABI minor 22. [Complete qualification](verification/cache146.json).

Cold construction remains a loading-time operation (83 ms in the preceding build).
Prepared reuse was 0.771 ms CPU p95. The cache now has a verified payload bound;
longer whole-process memory/lifecycle and device/platform coverage remain open.
Broader game-UI correctness work remains on the checklist below.

## Current sample: 4K qualification

The centred sample with improved event diagnostics passes fresh import, 93
headless and 99 rendered integration checks, including its release export.
All six automatic 3840×2160 3D runs pass all 276 timing checks on the RTX 5080.
Every whole-frame/changed-frame p95 stays at or below 3.942 ms in this run set.
These are combined-scene intervals, not isolated UI cost or a causal speedup.
[4K qualification](verification/load150-4k.json).

The earlier inventory/gameplay failure did not recur in either focused diagnostics
or this full qualification. Its cause is still unproven; the original failed
receipt remains linked in [load/lifecycle history](../examples/frontier_camp/LOAD_AND_LIFECYCLE.md).

## Installed preview143: reuse current input geometry

The native host now tracks pending paint separately from dirty geometry. Repeated
pointer routing reuses current geometry; ordinary updates, drawing and paused
processing still handle pending paint. The rendered regression fails on preview142
and passes on preview143. First-ever geometry-only initialization and destruction
before painting are also covered.

All 14 core suites (483,696 main checks), 16 sanitizer gates, 37 host entries
(27,827 checks), five rendered input scenes, sample/exports and 13 installed smoke
suites pass. The regular check script now includes the input-geometry scene.
An instrumented 600-frame trace records 850 core updates instead of 1,050 while
retaining 150 paint passes for 100 toggles.

All 72 desktop timing gates pass. Toggles measure 2.086–2.236 ms p95, inventory
1.076–1.379 ms and typing 0.580–0.771 ms. These separate runs do not prove a causal
whole-UI speedup or broader load/device readiness. Both local addon copies are
installed at ABI minor 22. [Preview143 evidence](verification/routing143.json).

## Installed preview142: defer painting between input events

The native host now resolves current geometry for pointer routing without
publishing an intermediate paint frame. The core's ABI minor 22 geometry update
propagates actual paint-input versions and retains the published draw buffers;
the ordinary update paints the accumulated changes. Existing update APIs retain
their behavior.

Ten style/layout cases verify unchanged intermediate draw buffers and final
full-rebuild equality. Another 360 modal steps now defer painting before their
full-rebuild comparisons. Native pointer tests verify that hover width and active
height affect the next hit test. All 14 core suites (483,681 main checks), 16
sanitizer gates, 37 host entries (27,825 checks), five rendered input scenes,
sample/exports and 13 installed smoke suites pass.

The instrumented toggle trace records 150 paint passes for 100 displayed frames,
down from 250. It also records extra geometry-only calls: host scheduling still
treats pending paint as dirty geometry. Separating those states is the next
optimization target. All 72 timing gates pass, with toggles at 1.808–2.940 ms p95.
These separate measurements do not establish a whole-UI speedup over preview141.
Both local addon copies are installed. [Preview142 evidence](verification/defer142.json).

## Installed preview141: avoid IME flushes for button focus

The native host now checks whether the focused DOM control can take text before
flushing geometry for IME synchronization. Buttons leave pending work for the
normal update; text controls still update and recheck CSS visibility/editability.
The candidate query is available in ABI minor 21. This also includes the long
binding-value correctness fix from candidate139.

A rendered Godot test reproduced the unwanted flush on preview138 and passes on
preview141. All 14 core suites (482,120 main checks), 16 sanitizer gates, 36 host
entries (27,822 checks), focused rendered input scenes, sample/exports and 12
installed smoke suites pass. Both local addon copies are updated.

All 72 desktop timing gates pass. API p95 ranges are 0.928–1.218 ms for inventory,
0.459–0.599 ms for typing and 1.717–2.126 ms for toggles. An instrumented trace
records 850 core updates rather than 900 across 600 frames/100 toggles, but still
250 paint passes. Intermediate paint reduction remains open. These separate runs
do not establish a causal whole-UI speedup or broader device/load readiness.
[Preview141 evidence](verification/ime141.json).

## Candidate139: changing long binding values fixed, timing gate failed

The long-value callback path now uses the length returned with each buffer fill,
so values that shrink no longer acquire trailing null bytes and growing values
are retried rather than truncated. Impossible lengths are diagnosed before size
arithmetic can overflow. The new core tests reproduced five failures before the
fix; native GDScript tests also cover changing multibyte text and disappearing values.

All 14 core suites (482,061 checks in the main runner), 16 sanitizer gates,
36 native host entries (27,817 checks), sample and export checks pass. Runtime
qualification passed 71/72 gates: settings toggles in Vulkan run 2 measured
3.332 ms p95 against 3.0 ms. That workload reports zero binding refreshes;
these measurements do not establish a cause for the overrun. The failed result
is preserved, candidate139 is not installed, and preview138 remains installed.
[Candidate139 evidence](verification/binding139.json).

A follow-up instrumented toggle trace recorded 250 paint passes for 100 displayed
frames in both preview138 and candidate139. The “boxes” interval includes modal
layout (candidate139 p95 0.469 ms); native drawing was 0.421 ms p95, mostly submission,
and occurs outside the manual API timing window. Intermediate input updates are
a concrete investigation target, but cannot simply be skipped: hover/active styles
may move the next hit target. [Diagnostic evidence](verification/toggle140-diagnostic.json).


## Installed preview138: fewer binding allocations, desktop timing gates pass

Preview138 removes unnecessary output reservation and stack-terminates common
binding paths at the C ABI boundary. The focused test reduces 11,000 allocations
to 2,000 across six sets of 1,000 unchanged refreshes. Paths up to 127 bytes need
no allocations for the short returned value; longer paths retain their heap
fallback. Every resolver read still runs, and value changes, missing data and
restoration are checked.

All 14 core suites, 16 sanitizer gates, 36 native host entries (27,811 checks),
271 incremental/full render comparisons and sample/export checks pass. All 72
unchanged timing gates pass across six runs. Both local addon copies are updated,
and all 12 installed smoke suites pass. This includes the fixes from candidates
135–137; their failed qualification receipts remain preserved.

API CPU p95 ranges are 0.335–0.676 ms for bound vitals, 1.212–1.705 ms for
inventory reordering, 0.708–0.937 ms for typing, 2.369–2.789 ms for toggles and
0.134–0.254 ms for redundant notifications. These runs pass the desktop targets;
they do not establish an overall speedup over preview134. Broader release work,
including layout gaps and realistic-load/device qualification, remains open.
[Preview138 evidence](verification/binding138.json).

## Candidate137: fewer update allocations, timing still failing

Candidate137 reuses the style-difference list and avoids temporary lists when a
CSS value contains one item. A representative headless C ABI hover diagnostic
measured 113,314 → 75,314 allocations across 1,000 updates, excluding construction
and reporting. This uses fallback fonts, not the full Godot sample. The new scalar
parser gate verifies 3,000 parses require only 3,000 returned-value allocations.

All 13 core suites, 15 sanitizer gates, 36 Godot host entries (27,811 checks),
271 incremental/full render comparisons and native sample/export checks pass.
The fresh broad audit remains 287/309 with no changed reports from candidate135.

The six-run timing profile passed 71/72 gates: redundant notifications reached
0.313 ms p95 against 0.300 ms in one Vulkan run. Candidate137 is not installed;
preview134 remains installed. Instrumented diagnostics show zero reported changes
in both binaries and put most refresh time in core binding work, rather than native
model application. These diagnostics do not clear the failed qualification.
[Candidate137 evidence](verification/alloc137.json).

## Candidate136: faster color conversion, timing still failing

Candidate136 adds an immutable 256-entry byte sRGB conversion table and includes
candidate135's alignment fixes. All 256 inputs match the former arithmetic exactly
(1,280 new checks). Five warmed isolated runs measured a median 153.860 ms before
and 7.189 ms after for four million RGB conversions. This is conversion throughput,
not a measured whole-UI speedup. Table payload is 1 KiB and alpha passes through.

All 12 core suites (482,021 checks), 14 sanitizer gates, 36 Godot host entries
(27,811 checks), 271 incremental/full render comparisons and native sample/export
checks pass. Runtime qualification passed 70/72 gates: the last Vulkan run measured
clock updates at 3.298 ms p95 and settings toggles at 3.426 ms, both against 3.000 ms
limits. Hover passed all six runs (0.219–0.307 ms p95). Candidate136 has not been
installed; preview134 remains installed. Both failed profiles are preserved.
[Candidate136 evidence](verification/color136.json).

## Candidate135: alignment fixed, timing qualification failed

Source and candidate135 fix normal-flow single auto margins and RTL overflow
alignment. All 96 Chrome cases and 30 live steps pass (1,009 native checks),
alongside 12 core suites, 14 sanitizer gates, 271 incremental/full render
comparisons and 36 host entries (27,811 checks). Native sample and export checks
pass. The broad audit remains 287/309 without changed fixture reports.

The six-run profile passed 71/72 timing gates. Vulkan hover reached 0.521 ms p95
against its 0.500 ms limit. Candidate135 has not been installed. An uninstrumented
134/135/135/134 hover comparison measured 0.504/0.420/0.259/0.486 ms p95;
instrumentation confirms no layout work during hover. This exposes variability
on the installed binary too, but does not establish a cause or clear the failed
qualification. Paint-phase attribution needs further investigation.
[Candidate and diagnostic evidence](verification/block-margins135.json).

## Installed preview134

Preview134 (ABI minor20) is installed in the host project and Frontier Camp,
with preview133 backed up. It fixes inherited RTL column order and right-edge
placement of fixed-width cards, preserving fractional allocation and margins.
All 28 focused Chrome cases and 16 live update steps pass (2,289 Godot checks).

All 12 core suites, 14 sanitizer gates, 241 incremental/full render comparisons,
35 host entries (26,802 checks), eleven installed smoke suites and native
sample/debug/release/embedded export checks pass. The broad audit remains
287/309, with no changed fixture reports from preview133.

All 72 unchanged timing gates pass across six ordinary UI runs. API p95:
vitals **0.275–0.432 ms**, inventory **0.765–0.905 ms**,
typing **0.332–0.608 ms**, toggles **1.625–2.034 ms**,
redundant notifications **0.188–0.229 ms**, idle **0.010–0.016 ms**.
Scope remains manual/static 1280×720 on the RTX5080 development desktop, API CPU only.
[Qualification evidence](verification/multicol-rtl134.json).

DLL SHA-256: `1b8a174f5c4446862c77de52c530836bc89c7638d58cd942b3db0ffa44e976f8`.
Source SHA-256: `4e5f29134ad03d53def799cbba59740c9719b013145c585c8a46ed0f88c8185f`.
Paragraph fragmentation, vertical writing and the broader requirements below remain open.

## Validation cost and remaining uncertainty

Explicit checks on12/48 mixed settings fields remain below0.16 ms p95 in six
renderer runs. Reporting with a text-input focus transition costs1.99–6.64 ms
for12 fields and2.45–2.83 ms for48. Canceling reporting is much cheaper.
These historical preview121 measurements exclude setup, frame drawing and GPU.
[Diagnostic122 evidence](verification/validation-performance122.json).

A follow-up diagnostic separates about0.52 ms median update work from
0.16–0.19 ms native IME activation during reporting. Disabling native input moves
update work into the follow-up phase, so it is not a fix. The earlier6.64 ms p95
was not reproduced in this run; intermittent cost and finer attribution remain
open. [Isolation evidence](verification/validation-ime-isolation123.json).

Preview124 removes a deterministic redundant update: invalid checks and canceled
reports now have only the diagnostic fixture’s forced follow-up update. Reports
that change focus retain their required update. A fresh run per renderer measured
reporting at 0.586–0.801 ms p95 for 12 fields and 1.143–1.260 ms for 48 fields.
These unpaired timings do not establish a speedup or resolve earlier spikes.

## Experimental retained renderer

The disabled-by-default retained canvas path now passes 62 exact property/shader
image comparisons and the survival sample's 98 interaction checks in all four
OpenGL/Vulkan and baseline/retained combinations. Instance shader parameters and
material dependency refresh fix reproduced transparent-UI failures. Repeated
draw-callback diagnostics retain the reduction in submissions and CPU time;
pre-draw synchronization, custom-material costs, GPU and broader lifetime/release
qualification remain open. The retained path remains disabled by default in preview121.
[Experimental118b evidence](verification/retained-materials118.json).

## Previous preview104 evidence (historical)

Windows preview104 is installed in the development project and Frontier Camp.
The DLL SHA-256 is `0bf8888c46bb27dbafad86f680048ebb52eb08ba2e4f45b3036c73dc963ecaf9`;
source-content SHA-256 is `5e8a49854ddab068ca8dc72008152325b042baaa386d0589296dd13353c462ee`.
This is a dirty-worktree build, not a committed release. Installation validated
metadata and both previous DLLs, backed up preview103, and verified the new hashes.
The actual installed binary passes 362 form-state and 165 binding checks.

A subsequent test-only extension passes 506 installed form-state checks: 48
short/empty/sanitized default-value cases expand the selection comparison from
32 to 80 cases. The native binary is unchanged. The latest full 27-entry run
below remains 9,024 checks; this follow-up is a targeted suite run.
See [additional selection evidence](verification/short-selection-preview104.json).

The tested engine remains the local patched Windows 4.7.2 editor and matching
patched export templates, with ICU embedded. These results do not certify stock
Godot, current Linux native binaries, other platforms or untested configurations.
See [release verification](RELEASE.md) and [engine text safety](GODOT_TEXT_SHAPING.md).

Preview104 corrects selection handling for pristine input/textarea default changes.
It retains preview103's checked binding reads and preview102's incremental shaping
cache. Explicit form mutation reasons distinguish defaults from current-value
writes and resets; ordinary unchanged HUD text keeps its no-op fast path.

Preview104 evidence under `.utmp/safe-engine71/`:

- `default-selection-after-tests.log`: all ten core suites pass. The new regression
  first reproduced eight mismatching cases, then all 32 Chrome-derived cases pass.
- `default-selection-asan-tests.log`: all twelve sanitizer gates pass. This is
  Linux core evidence, not a current Linux Godot addon/export certification.
- `default-selection-host-checks/verification.json`: **9,024 native checks across
  27 entries**, including the same 32 cases through Godot's public API, all passing.
- `frontier-default-selection/verification.json`: fresh import, integration and
  release-export checks pass in OpenGL and Vulkan.
- `default-selection-exports.log`: packaged debug, release and embedded exports
  pass relocation/startup and match project pixels.
- `default-selection-budget/summary.json`: **72/72 explicit timing checks pass**,
  three runs per renderer, 600 measured frames after 120 warmups, manual updates,
  static 1280×720 scene. Redundant-signal p95 ranges 0.190–0.268 ms. This profile
  applies to this development desktop, not every workload or hardware class.
- `installed104-form-state.log` and `installed104-binding-tests.log` verify the
  installed binary. [Portable evidence](verification/default-selection-preview104.json)
  preserves the 32 Chrome152 cases, native checks, profile and source/binary metadata.

Earlier evidence remains explicitly scoped:

- Preview103's checked binding reads passed a reproduced regression and all 72
  timing checks; [its evidence](verification/binding-access-preview103.json) remains.
- Preview102's first timing profile failed one redundant-signal check at 0.321 ms.
  Focused reverse-order measurements also showed an overrun on preview100; cause
  remains unproven. The old failure is retained in
  [timing evidence](verification/cache-lru-timing-preview102.json), not relabeled a pass.
- The broader Chrome form/input/paint comparison is preview100 (546 cases); the
  broad layout audit is preview98 (287/309 agree, 22 differences). Neither was rerun
  on preview104. The new 32-case comparison does not substitute for those audits.
- Load/lifecycle history includes preview100's 1080p automatic-update 3D cases and
  ten-minute Unicode soak, and preview102's fixed-frame cache-retention run.
  Preview104 has not repeated that entire matrix. See
  [load/lifecycle evidence](../examples/frontier_camp/LOAD_AND_LIFECYCLE.md).

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

## Windows preview92 dialog focus

Installed preview92 routes focus for both modal and nonmodal dialog opening,
skips unavailable delegates and preserves deliberately chosen outside focus on
nonmodal close. Rejected selection requests no longer alter another field's caret.
It passes 8,804 native checks, 435 Chrome152 comparisons, 10 core suites,
12 sanitizer gates, exported builds and all normal game workloads. Installed smoke
passes 157 checks; nine captures match preview91. See `FORM_STATE.md` for scope.
General selection persistence, broader browser events and physical/platform
acceptance remain open.



## Windows preview91 input update



Installed preview91 supports bound inert panels: focus, typing and pointer input

stop without changing values or hiding the panel. Modals escape inert ancestors;

popovers retain inertness. It passes 419 Chrome152 comparisons, 8,788 native checks,

10 core suites, 12 sanitizer gates, packaged exports and normal game workloads.

Installed smoke passes 141 checks, and nine captures match preview90. See

`FORM_STATE.md` for scope and `.utmp/safe-engine71/inert-*` for evidence.

Broader browser behavior, physical input and platform acceptance remain open.



The goal is an addon that a GDScript user can install in a fresh project,

author ordinary HTML/CSS, connect game state and input, and export a working

desktop game. Passing the development gallery is necessary but does not prove

that workflow. Other platforms need their own builds and export evidence.



## Game-UI candidate evidence (2026-09-08)



Installed Windows preview90 clears focus and stops typing into fields hidden by

data-driven panel changes. Visible descendant overrides and transparent panels

retain focus. Focus-dependent sibling styling settles in the same update. It

passes 407 Chrome152 comparisons, 8,776 native checks, 10 core suites, 12 sanitizer

gates, packaged/relocated exports and all normal survival workloads. Installed

smoke passes 129 checks; nine captures match preview89. Evidence: `FORM_STATE.md`

and `.utmp/safe-engine71/panel-focus-*`. Broader browser, physical input and

platform acceptance remain open.



The preceding Windows preview89 prevents dialog closure from restoring focus to hidden

or disabled controls, and releases focus in a hidden closing dialog on update.

It passes 392 Chrome152 comparisons, 8,761 native checks, 10 core suites,

12 sanitizer gates, packaged/relocated exports and normal survival workloads.

Installed smoke passes 114 checks; nine captures match preview88. Evidence:

`FORM_STATE.md` and `.utmp/safe-engine71/dialog-restore-*`. This is still a

Windows preview; broader focus lifecycle, browser and platform limits remain.



The preceding Windows preview88 fixes the modal input leakage found in preview87

and the reverse-opening overlapping-dialog hit bug. Promoted dialogs/popovers

escape ancestor transforms, clipping and opacity; painting and hit testing share

opening order. It passes 380 Chrome comparisons, 8,749 native checks, 10 core

suites, 12 sanitizer gates, packaged/relocated exports and all normal survival

workloads. Installed smoke passes 102 checks; nine captures match preview87.

Evidence: `FORM_STATE.md` and `.utmp/safe-engine71/top-order-*`.



The preceding Windows preview87 rejects invalid dialog mode changes without altering

the current UI, emits opening toggle notifications, and applies auto/hint popover

dismissal rules. It passes 368 Chrome comparisons, 8,738 native checks, 10 core

suites, 12 sanitizer gates, packaged exports and normal survival workloads.

Installed smoke passes 91 checks; nine captures match preview86. Evidence is in

`.utmp/safe-engine71/dialog-api-*` and `installed87-form-state.log`. This remains

a Windows candidate; these checks do not establish full browser parity or

release readiness on other hardware/platforms.



The preceding Windows preview86 fixes popover close notifications for binding and

attribute changes, and captures toggle state for native callbacks. It passes

333 Chrome comparisons, 8,727 native checks, 10 core suites, 12 sanitizer

gates, packaged exports and normal survival workloads. Installed smoke passes

82 checks; nine captures match preview85. Native callbacks deliver on update

or the next frame; browser task coalescing and beforetoggle cancellation remain

outside the current event API.



The preceding Windows preview85 adds modal/popover selectors backed by live state,

shared with rendering. Authored data markers no longer promote elements into

the top layer. It passes 314 Chrome comparisons, 8,721 native checks, 10 core

suites, 12 sanitizer gates, packaged exports and normal survival workloads.

Installed smoke passes 76 checks; nine captures match preview84. The focused

native fixture verifies Escape delivery; fullscreen modality and native dialog

Escape policy remain outside this change.



The preceding Windows preview84 fixes unintended submission by Auto command buttons.

It shares submit classification across pointer/keyboard activation, implicit

Enter and default styling. All 295 Chrome comparisons, 8,711 native checks,

10 core suites, 12 sanitizer gates, packaged exports and normal survival

workloads pass. Installed form-state smoke passes 66 checks; nine captures

match preview83. General command dispatch remains deferred.



The preceding Windows preview83 adds `:default` styling, including remote submit

ownership, type changes, removal and markup defaults. It passes 275 Chrome

comparisons, 8,706 native checks, 10 core suites, 12 sanitizer gates, packaged

exports and normal survival workloads. Installed form-state smoke passes

61 checks; nine captures match preview82. Preview84 aligns its older activation

helper with the new selector rules.



The preceding Windows preview82 adds `:read-only`/`:read-write` styling, verified

with live locking and native typing, disabled groups and input type changes.

It passes 253 Chrome form-state checks, 8,699 native checks, 10 core suites,

12 sanitizer gates, packaged exports and normal survival workloads. Installed

form-state smoke passes 54 checks; nine captures match preview81. The selector

supports contenteditable inheritance, but arbitrary DOM editing remains deferred.



The preceding Windows preview81 adds Chrome-compatible `:required`/`:optional`

styling with live attributes and bindings. It passes 154 Chrome form-state

checks, 8,691 native checks, 10 core suites, 12 sanitizer gates, packaged

exports and normal survival workloads. Installed form-state smoke passes

46 checks; nine captures match preview80. Constraint validation is still deferred.



The preceding Windows preview80 preserves disabled hover/pressed styling and title

tooltips while preventing activation and focus; disabling a slider cancels its

drag. It passes 101 Chrome form-state checks, 8,687 native checks, 10 core suites,

12 sanitizer gates, packaged exports and normal survival workloads.

Preview78 fixed disabled fieldset inheritance, the first

legend exception, enabled/disabled CSS and live focus handling. It passes

97 Chrome form-state checks, 8,682 native checks, 10 core suites and 12 sanitizer

gates, plus packaged exports and normal survival workloads. Preview77 added

Chrome-style Ctrl+Up/Down paragraph navigation

with Shift selection. It passes 177 Chrome comparisons, 8,675 host checks,

302 installed editing checks, 10 core suites and 12 sanitizer gates, plus

packaged exports and the normal survival UI workloads. It retains preview76's

removal of obsolete per-box mapping state and preview75's exact tab geometry,

including mixed inline fonts and text decorations. Normal UI performance

and remaining limits are recorded in the sample's PERFORMANCE.md. This evidence

does not establish full browser conformance or readiness on other platforms.



A local patched Windows Godot editor plus debug/release template bundle now

passes the Unicode crash reproductions in actual exports with ICU embedded.

It also passes the host/sample and relocated addon export checks. This provides

a verified Windows engine option for the text-safety blocker; stock Godot is

still affected. The engine bundle, required configuration and evidence are in

[GODOT_TEXT_SHAPING.md](GODOT_TEXT_SHAPING.md#windows-patched-engine-bundle-2026-09-08).



Windows preview70 additionally corrects synthetic-bold fallback

selection and combining-mark attachment. Its separate native adapter suite

passes 19,682 checks with caches enabled and disabled; 8,456 host checks and

packaged, relocated debug/release/embedded exports also pass. The current Linux

adapter/test sanitizer probe passes 19,816 checks in each cache mode. See the

[font evidence](GODOT_TEXT_SHAPING.md) and current binary identity in the

[parity report](../examples/frontier_camp/CHROME_PARITY.md).



Runtime68 source fixes grid placement/sizing, live size container queries,

inline continuation bounds, form text defaults, indentation, synthetic-bold

advances and duplicate slider decoration. The current Windows candidate passes

8,456 host checks and 1,276 Frontier geometry plus 33 interaction comparisons.

Core and sanitizer checks pass. See the [parity report](../examples/frontier_camp/CHROME_PARITY.md)

for binary identities and the [performance report](../examples/frontier_camp/PERFORMANCE.md)

for repeated release measurements. The corrected ten-minute memory fixture uses

fixed timing storage; its process-private memory ends below its initial soak

sample, with fixed node count. [Lifecycle evidence](../examples/frontier_camp/LOAD_AND_LIFECYCLE.md)

records the methodology and limits.



The earlier preview74 was installed after Windows packaging/export verification.

It retains preview71 transformed input and popup anchoring and adds visual

textarea keyboard navigation, Unicode-safe wrapping and multiword click fixes.

It also fixes source mapping for styled text and expanded tabs, preserves

trailing editable whitespace and avoids bytewise UTF-8 measurement during tab

expansion. The native editing gate now rejects Unicode parsing warnings.

The final source passes ten Release and twelve sanitizer gates, 8,611 host

checks, packaged exports and relocated sample checks; see the latest parity/performance

reports for verification and the isolated unresolved hover assertion. These

checks do not clear the stock-engine text-safety, physical-input, platform or

broader release requirements below. The older container-query and first-open

work items below have subsequent evidence in the linked reports.



## Current evidence (2026-09-07)



Runtime67 resolves the 274 Frontier Camp geometry findings from the runtime66

[Chrome audit](../examples/frontier_camp/CHROME_PARITY.md). The repeated live

check now passes all 1,276 geometry and 33 behavioral comparisons, without

changing tolerances or the sample CSS. Broad browser conformance remains

incomplete; container queries, vertical writing, column spanning and other

synthetic-corpus differences remain. This closes the sampled game UI regressions,

not all CSS or platform release requirements.



Runtime66 adds guarded retained layout for modal changes. In matched Windows

release runs, settings-toggle p95 falls 2.380→1.483 ms on OpenGL and

1.936→1.477 ms on Vulkan, with 84 identical workload captures. Shared layout

changes retain the full fallback. Core, sanitizer, Godot host and relocated

export checks pass; details and limits are in the

[modal report](../examples/frontier_camp/MODAL_PERFORMANCE.md). This does not

close the hardware/platform or other release requirements below.



Runtime65's [Frontier Camp measurements](../examples/frontier_camp/PERFORMANCE.md)

reduce keyed inventory sorting and typing by about half: 0.84–0.97 ms and

0.48–0.56 ms p95 respectively. Continuous two-meter updates are 0.35–0.49 ms;

modal settings transitions remain 2.39–2.63 ms. The new build passes 8,440 Godot

checks, 9/9 core CTest suites, 11/11 ASan/UBSan suites and 84 exact before/after

workload screenshots, plus fresh-project and relocated release-export checks.

[Load and lifecycle verification](../examples/frontier_camp/LOAD_AND_LIFECYCLE.md)

records 1080p/4K animated 3D workloads, cold opening, retained-screen reuse and

process memory. This does not close lower-end hardware, actual game-budget,

real IME/device or broader product-readiness requirements below.



### Earlier checkpoints





The subsequent runtime-performance pass installs **Windows runtime60** in the

development project with a verified preview56 backup. In three native pairs,

ordinary active-HUD API/update time falls 0.442→0.296 ms and redundant HUD

writes fall 1.344→0.020 ms. All 24 workload captures match, 25 host suites pass

8,337 checks, and core Release/ASan/UBSan mutation checks pass. Bound HUD,

inventory, menus and typing now have dedicated runtime measurements; see

[PERFORMANCE.md](PERFORMANCE.md) and [RUNTIME_PERFORMANCE.md](RUNTIME_PERFORMANCE.md).

This establishes ordinary-workload evidence on the tested desktop, not a full

game or lower-end hardware budget. The preview56 installation references in

the earlier checkpoints below are historical; the release59 ZIP is unchanged.



The release-engineering pass in [RELEASE.md](RELEASE.md) adds a root-level CI

workflow, versioned packaging tied to binary/source/dependency hashes and

process-exit/completeness checks for acceptance gates. Linux Release passes all

nine CTest targets; the complete Linux ASan/UBSan rerun passes all 11 targets

after fixing allocator mismatches in two test/benchmark executables. Newly built

Windows/Linux previews each pass 25 host suites / 8,319 checks and fresh-project

and relocated exports with exact example pixels. Stock Godot still fails five

of six text-safety cases on both platforms, including process aborts. An extra

Linux combined-ZIP check also hit an editor abort during fresh import; five

controlled imports and the next full export check passed, so the cause remains

unresolved. The installed development DLL remains preview56. Full product

requirements below remain open.



The development project now uses verified **Windows preview56**, with preview53

backed up. Current actual-project layout-stress runs average **2.489–2.730 ms**

per frame, with median run p95 **4.984 ms** and largest observed frame

**9.240 ms**. The stale DLL that previously produced 30ms-plus frames was

replaced at preview51. These are standalone gallery measurements with the editor

closed, not editor timings or worst-case bounds.



Preview53 simplified two hot gradient clamps. HUD headless cold builds improved

72.512→67.836 ms in five of five alternating pairs; native gallery timings

remain too variable to establish a speedup. Nine CTest targets, 864 frozen-source

raster comparisons, native host checks, four gallery pixel comparisons and

fresh-project/native export checks pass. The installed project also passes 245

host checks and an exact layout-stress capture. Linux and UBSan verification

after preview40 was pending at that checkpoint. Methods and limits are in

[PERFORMANCE.md](PERFORMANCE.md); earlier installation-pending notes below are

historical.



Windows preview56 adds input baselines, type-change layout invalidation,

active-font centering and visible carets/selections in short or empty fields.

It includes candidate54's flex/grid containment and candidate55's border/button

corrections. It passes 636,031 core checks, 25 native host suites / 8,319 checks,

1,944 focused Chrome checks, 2,180 rendered form checks and four unchanged

gallery captures. Standalone layout-stress run means range 2.090–2.145ms,

median 2.109ms, with median run p95 3.807ms; this is similar to candidate55.



The 19-file Windows ZIP passes fresh-project, packed-resource and relocated

native debug/release/embedded exports under Godot 4.7.2, including 17 example

checks in each configuration and exact exported example pixels. It is

**packaged and installed**, with a hash-checked preview53 backup. The actual

project also passes 245 host checks and an exact layout-stress capture.

Fresh Chrome arbitration now leaves five sample and five harvest findings.

Two harvest input failures are resolved; the newly flagged audit checkbox

row is closer to Chrome but retains about 0.1–0.2px residuals. Original captures

and acceptance gates remained unchanged at that checkpoint. Later Linux and

UBSan evidence is recorded above. See [ORACLE.md](ORACLE.md) and

[PERFORMANCE.md](PERFORMANCE.md) for the evidence and limits.



The current core and embedded ICU now also pass **Windows MSVC AddressSanitizer**:

636,031 core checks, including retained/full mutations of all 47 samples, and

all **10 CTest targets**. Allocation guards, blur variants and benchmark CLI

checks pass. The activation probe detects a deliberate heap overflow; a matched

uninstrumented probe is rejected by the gate. The new `WEVA_SANITIZERS` CMake

switch defaults to OFF, and the installed preview56 DLL is unchanged.

Reproduction and scope are in [SANITIZERS.md](SANITIZERS.md). This closes the

current Windows core ASan gap; the later Linux/UBSan core pass is recorded above.

Current native-adapter sanitizer verification remains pending.



### Historical release checklist (2026-09-07)



This checklist records the earlier checkpoint, not the current outstanding queue.

The Windows engine bundle, adapter sanitizer runs, sampled Chrome fixes and

preload/reuse evidence above supersede its corresponding requests. Current

remaining work is broader browser behavior, physical input/accessibility,

equivalent platform packages, lower-end hardware and final-game budgets. The

stock engine is still unsafe; use the verified editor/template pair and embedded

TextServer data described in [GODOT_TEXT_SHAPING.md](GODOT_TEXT_SHAPING.md).



1. **Text safety.** Resolve the stock-engine long-emoji corruption/crash in

   an engine configuration that can actually be distributed and supported.

   The local diagnostic patch is not part of the addon. On 2026-09-07 the

   latest Windows/Linux probes again found corrupt results in the 33, 65 and

   256-run cases and process aborts in both mixed-script cases. The 32-run

   control now passes cleanly, without the earlier certificate-store error. The main

   check script now includes the reproduction and preserves engine hashes,

   exit codes and unfiltered logs; this detects the blocker, not fixes it.

   With the frozen preview56 DLL, ordinary autoscroll passes 81 assertions;

   enabling its long-emoji stress input fails five selection/editing

   assertions. Both runs retain the same certificate-store diagnostic.

2. **Input and authoring correctness.** Verify real Windows IME and resolve

   the documented stock Linux IME failures. Finish the remaining font-family,

   bidi/editing, form-control and validation behavior, plus touch/gamepad and

   accessibility integration. The five sample and five harvest findings

   include engine differences and a component-capture defect; fix them using

   browser evidence without widening tolerances. Details below remain part

   of the release scope.

3. **Current platform and memory checks.** The current Linux core sanitizer,

   host-suite and relocated-export checks now pass alongside Windows checks.

   Complete current native-adapter instrumentation, the broader rendering and

   real-input matrix, and investigate the extra Linux fresh-import abort.

   A core sanitizer pass does not cover Godot's native adapter or engine. Every

   additional supported platform/configuration needs equivalent evidence against

   its actual packaged binary.

4. **First-open latency and game integration.** HUD cold build is still

   about 68ms headlessly. Verify a preload/reuse strategy or reduce that cost,

   and validate latency and lifecycle behavior in a representative game.

   The installed layout-stress improvement is established; it does not bound

   cold-open time, editor overhead or graphics stalls under shared load.

5. **Release engineering.** Pinned godot-cpp/ICU inputs, compiler/build metadata,

   versioned previews and root-level CI are now implemented. Finish the

   compatibility/API documentation and remaining acceptance matrix.

   `bash check.sh --release` rejects skipped gates, process failures,

   incomplete oracle runs and outstanding layout findings; text safety is

   required whenever Godot is available. A successful script alone cannot

   substitute for the unimplemented requirements and real-device checks.



The development host has working layout/paint, forms, animations, keyed data

binding, controller events and a sample gallery. Before the product work below,

the complete mutation corpus passed 576,944 checks in Release and ASan/UBSan;

47 backend comparisons and 12 interactive states passed. Layout-stress's

animated update measures 3.52 ms through Godot with its engine font. Detailed

methods and limits are in [PERFORMANCE.md](PERFORMANCE.md).



After native Control and keyboard integration, the complete mutation corpus

passes 577,673 checks in both Release and ASan/UBSan. All 47 backend comparisons

match the preceding build's results;

12 interactive render states have 0.00% structural and color differences.

Windows 4.7.1 and Linux 4.7.2 each pass 40 native input integration checks,

35 keyboard integration checks, 245 host checks, 75 binding checks, 10 hover

checks and five gallery hover checks, plus the demo and inventory suites.

Windows uses an isolated project with a rebuilt DLL. Fresh-project and packed-resource

smokes pass 8 and 10 checks on both Windows 4.7.1 and Linux 4.7.2, covering

imported PNG/SVG and markup. A packaged addon also passes six example checks

in the project and six more in its pack on both platforms, including native

keyboard activation through its controller and bindings.



Native debug, release and embedded-pack exports pass on Windows and Linux

x86_64 with standard Godot 4.7.2 templates. Each configuration passes 13

resource/runtime checks and six example checks after the source project is

hidden and the export relocated. The example's rendered pixels exactly match

its editor-project baseline within each platform. Details and limits are in

[DESKTOP_EXPORTS.md](DESKTOP_EXPORTS.md).



After the long-field and Unicode editing work, the complete mutation corpus

passes 582,194 checks in both Release and ASan/UBSan. Windows/Linux Godot 4.7.2

also pass the new 86-check text-editing suite alongside all preceding host

checks. The 47 backend comparisons retain their preceding results; all 12

interactive states remain at 0.00% structural and color differences. The new

addon libraries pass native debug, release and embedded-pack exports on both

platforms, with exported example pixels matching the project.



After maxlength and paste integration, the complete mutation corpus passes

582,689 checks in Release and ASan/UBSan. Chrome passes 150 matching behavior

checks; Windows/Linux Godot each pass 44 new GUI checks alongside all preceding

host suites. A separate Linux X11 run exercises the actual clipboard shortcut.

The packaged example now verifies its existing 40-unit name limit using pasted

emoji and undo. Its eight checks and the 13 native runtime/resource checks

pass in Windows/Linux debug, release and embedded-pack exports; rendered

example pixels still match the project within each platform.



After live/default form state and reset integration, the complete mutation

corpus passes **582,903 checks** in Release and ASan/UBSan. Chrome passes 80

form-state checks; Windows/Linux Godot each pass 30 new reset integration

checks alongside every preceding host suite. The 47 backend comparisons are

unchanged and all 12 interactive states have 0.00% structural/color differences.

Real Linux IME passes nine checks with the previously verified private

Godot/IBus fixes. The addon does not include those upstream fixes.



The packaged example now exercises a native reset button, default preservation

and model/controller restoration. Its ten checks and all 13 resource/runtime

checks pass in Windows/Linux debug, release and embedded-pack exports, with

example pixels matching the project on each platform. Focused idle inputs

still report zero allocations. See [FORM_STATE.md](FORM_STATE.md).



## Work in progress



After select interaction work, the complete mutation corpus passes **583,691

checks** in both Release and ASan/UBSan. Chrome passes 69 matching selection,

event and display-mode checks; Windows/Linux Godot each pass 63 new native

select checks alongside every preceding host suite. All 47 backend comparisons

retain their preceding results; all 12 interactive comparisons remain at

0.00% structural/color difference. A rendered listbox confirms the separate

keyboard-row cue, disabled rows and multiple selection. Fresh installation and

debug, release and embedded-pack exports pass on both platforms, with example

pixels unchanged from the preceding build. A 1,000-option idle listbox and a

focused 4,096-character input each allocate zero bytes over 500 updates.

See [FORM_STATE.md](FORM_STATE.md#select-interaction) for semantics and limits.



Unicode select typeahead and label rendering pass **584,432 core checks** in

Release and ASan/UBSan. Chrome passes 78 matching behavior checks; Windows/Linux Godot each

pass 48 native typeahead, label, popup and binding checks alongside all

preceding suites. A rendered 49-check run additionally verifies the captured

listbox and styled popup. All 47 backend comparisons retain their preceding

results and all 12 interactive comparisons remain at zero difference.

The installed example now exercises accented-label search through native

input. All 12 example checks and 13 runtime checks pass in debug, release and

embedded-pack exports on both platforms, with rendered pixels unchanged.

The ICU license/data notices and source pin accompany the 19-file addon ZIP.

See [PERFORMANCE.md](PERFORMANCE.md) for idle allocation and binary-size costs.

That stress benchmark exposed slow 1,000-option selection updates:

approximately 37–46 ms through Godot, with cascade the largest measured stage.

The subsequent scope/color change reduces the same back-to-back benchmark

from **38.044 ms to 2.066 ms**. Selection restyles two changed options, consumes

caption/keyboard-row versions independently, and repaints without layout.

Overlapping dirty scopes are coalesced, and HTML reload clears stale focus

ancestors before they can become dirty roots. Release and ASan/UBSan each pass

**584,528 checks**, including inherited colors, sibling rules, `:has()`, reset,

removal and reload compared with complete rendering. Windows/Linux host suites

and all native export modes pass; the example and rendered select fixture

retain their preceding pixels. The 47 backend and 12 interactive comparisons

are unchanged. Broad focus changes in

large controls remain an optimization opportunity; see the benchmark details

in [PERFORMANCE.md](PERFORMANCE.md).



Held listbox autoscroll passes **585,327 core checks** in Release and

ASan/UBSan, including retained/full comparisons while nested scroll geometry

moves and resizes. Chrome passes 23 matching autoscroll behavior checks.

Windows/Linux Godot each pass all 15 host suites, including 33 new native

capture, paused-clock and cancellation checks. A rendered 34-check run confirms

the scrolled selection, active row, disabled row and clipping. The 47 backend

comparisons retain their preceding results and all 12 interactive states

remain at zero difference.



Fresh installation and native debug, release and embedded-pack exports pass

on both platforms. The example now includes autoscroll and delayed binding

commit: all 14 example checks and 13 runtime checks pass, with exported pixels

matching the project on each platform. A 1,000-option autoscroll update averages

1.416 ms through Windows Godot with its engine font; idle allocation checks

remain at zero. See [FORM_STATE.md](FORM_STATE.md) and

[PERFORMANCE.md](PERFORMANCE.md) for clock semantics, reproduction and limits.



Continuous text selection now passes **588,811 core checks** in Release and

ASan/UBSan, plus 35 Chrome checks and 81 native Godot checks on each desktop

platform. All 16 host suites pass. Text inputs and passwords scroll

horizontally; textarea selection scrolls both axes, preserves Unicode source

anchors and creates no edit or undo events. Monotonic input time keeps held

selection responsive while CSS is paused or `Engine.time_scale` is zero.

A rendered 82-check run verifies the clipped selection. Retained/full frames

agree through wrapping, nested scrolling, resizing and font changes.



The export example exposed missing model initialization after HTML replacement.

The host now reapplies existing bindings after creating the new document,

including controls inside repeated rows and callable data sources. The binding

suite grows from 75 to 85 checks; the preceding DLL fails six reload assertions,

while the fix passes all 85 on Windows/Linux. Explicit empty data stays active

through reload, while unconfigured corpus markup remains literal. Reset defaults

and edit-event behavior remain covered.



Fresh install, packed resources and native debug/release/embedded exports pass

on Windows/Linux, including all 17 example and 13 native runtime checks.

Exported example pixels match each platform's preceding preview. Idle

allocation remains zero; active 4,096-byte field scrolling measures roughly

4 ms in the recorded Windows run. See [PERFORMANCE.md](PERFORMANCE.md).



**An upstream text-shaping defect remains a product release blocker for

unrestricted Unicode input.** Stock Godot 4.7.2 corrupts glyph ranges or crashes

above 32 separate emoji runs within one script run. An addon-free reproduction

fails five of six cases on Windows/Linux. A matched candidate engine patch

passes all six (18 checks) and the original 81-check Weva stress suite. The

patch is separate from the addon; regular interaction coverage stays below

the trigger and does not claim to fix it. See

[GODOT_TEXT_SHAPING.md](GODOT_TEXT_SHAPING.md).



Layout-stress's rendered Windows gallery now advances the document once per

frame. Its native clock test fails with doubled animation speed before the

fix and passes afterward. Three interleaved sweeps measure 23.327 ms mean

frame time with the repository's older DLL and duplicate update, versus

8.781 ms with the current DLL, one update and reused RGB conversion. See

[PERFORMANCE.md](PERFORMANCE.md) for the breakdown and measurement limits.

All 17 host suites, including the 85-check binding suite and gallery clock

regression, pass on Windows/Linux Godot 4.7.2 and the user's Windows Godot 4.7

executable. The 47 backend results and 12 interactive comparisons are unchanged.



Consecutive Godot triangle uploads further reduce layout-stress frame time

from **8.618 to 7.435 ms** on the user's Godot 4.7 executable, using five

interleaved comparisons of the same DLL with batching on/off. Core update

time stays about 4.5 ms. A separate scope reduces 281 submissions to 87 with

the same vertices. Six dedicated images are byte-identical across batching

modes on Windows Vulkan/OpenGL and Linux OpenGL; the 47 sample comparisons,

12 interactive states, all 17 host suites and desktop export checks retain

their passing results. Occasional long frame stalls remain. See

[PERFORMANCE.md](PERFORMANCE.md) for methods and limits.



Native sampling now attributes the observed long Windows frames to a Vulkan

GPU-fence wait after Weva's submission. The GPU was also at 99% utilization

from other work after the test exited. Shared GPU load therefore remains a

confounding factor for frame-latency claims; a controlled comparison without

that load is outstanding. Per-frame traces and optional GPU timing are now

available in the gallery probe. This investigation does not claim a runtime fix.



Passing contained solid triangles through rounded clips reduces the next

matched Windows comparison from **7.271 to 6.311 ms per frame**, with core

update time **4.410 to 3.899 ms**. It also removes a one-pixel hole inside a

solid progress bar in native rendering. Varying UV/color/coverage retains the

previous interpolation path. Release and ASan/UBSan each pass **589,083 checks**;

the 47 backend results, 12 interactive comparisons, 17 host suites and native

Windows/Linux export checks retain their passing results. Preview20 contains

this change and the frame tracing above. The open development editor still

uses the older repository DLL; the preview has been validated in isolated

projects. See [PERFORMANCE.md](PERFORMANCE.md) for matched measurements and

the remaining GPU-stall limitation.



Preview21 reuses parsed colors and the decoration values already resolved by

the paint walk. The next matched comparison improves **6.101 -> 5.632 ms per

frame**, with core updates **3.853 -> 3.365 ms**. A headless animated update

makes 31.9% fewer allocation calls. All static/animated sample pixels and eight

real-font Windows snapshots match preview20. Release and ASan/UBSan each pass

**589,097 checks**; backend, interactive, host and desktop export checks pass

with their preceding results. This build also exposes fill/border construction

and color resolution in the paint profile. The repository's Windows DLL has

not been replaced; the new package is tested in isolated projects.



Preview22 preserves native shaped-glyph offsets, converts TextServer clusters

to UTF-8 byte offsets and resolves automatic fallback glyphs against the exact

selected font. The additive minor-11 callback leaves the existing font table

unchanged. Native adapter/document geometry checks pass **547 checks on Windows

and 457 on Linux**. Release and ASan/UBSan each pass **589,134 checks** with the

full mutation corpus. All 17 host suites, the unchanged 47 backend comparisons,

12 interactive states and Windows/Linux native exports pass. Eight real-font

layout-stress snapshots are byte-identical to preview21. The matched gallery

comparison measures **6.236 -> 6.211 ms/frame**, within run variation, with

long GPU waits still present in both builds. The stock Godot shaping limitation

and broader bidi/font-family work remain open. See [GODOT_TEXT_SHAPING.md](GODOT_TEXT_SHAPING.md).



Preview23 preserves textured, colored and antialiased triangles wholly inside

convex clips. The matched Windows gallery comparison improves **5.709 ->

5.079 ms/frame**, with core updates **3.474 -> 3.200 ms** and 12% fewer uploaded

vertices in the profile. Forty-eight direct-render comparisons prove that an

irrelevant clip leaves the original triangle's pixels unchanged; the previous

clipper fails 21 of them. Native snapshots also show five formerly missing

gradient-bar pixels filled correctly. Small interpolation/sampling differences

are recorded in [PERFORMANCE.md](PERFORMANCE.md). Release and ASan/UBSan each

pass **588,998 checks**; backend, interactive, host, batching and desktop export

checks pass on Windows/Linux. The previously observed GPU waits remain open.



Preview24 keeps shared source vertices shared when interior triangles pass

through polygon clips, reducing native layout-stress uploads by **10.1%**.

The five-pair gallery comparison measures **5.073 -> 4.820 ms/frame**, with

core updates **3.206 -> 3.134 ms**; system-load variation limits that timing

comparison. Headless core time slightly increases while allocated bytes fall

3.9%; [PERFORMANCE.md](PERFORMANCE.md) records the tradeoff. All 47 software

samples at two animation times and eight native snapshots remain byte-identical.

Release and ASan/UBSan each pass **589,078 checks**. Backend, interactive, host,

batching and desktop export checks retain their preceding results on both

platforms. The repository Windows DLL has not been replaced.



Preview26 supports inherited Godot theme fonts, node overrides and type

variations, including live resource/fallback changes and paused documents.

It fixes stale glyph bitmaps when a font provider changes, atlas ownership

across renderer replacement/destruction, and a reproduced use-after-free in

published texture views. Both platforms pass 35 headless and 39 rendered theme

checks. Release and ASan/UBSan each pass **589,105 checks**; all 18 host suites,

unchanged backend/interactive comparisons and desktop exports pass. Eight

native layout-stress snapshots remain byte-identical to preview24. Typical

frames and core updates remain about 4.9 and 3.2 ms respectively; long frame

stalls recur and remain unresolved. See [PERFORMANCE.md](PERFORMANCE.md) for

the whole-frame measurements. Per-element CSS font-family selection remains

unfinished. The repository Windows DLL has not been replaced.



Preview27 gives the triangle cutter bounded stack scratch storage and appends

each output polygon's indices in one resize. Exact interpolation and geometry

remain unchanged. Five interleaved Windows comparisons measure **5.794 ->

5.553 ms mean frame**, **4.896 -> 4.734 ms median frame** and **3.195 -> 3.082 ms

core update**, as medians across runs. Headless time improves 6.6%, with 285

fewer allocations in the measured update. Candidate frames still reach 31 ms;

the stall issue remains open. Release and ASan/UBSan each pass **589,105 checks**,

including the existing exact differential clip oracle. Eight native snapshots,

47 backend comparison rows and 12 interactive states retain their previous

results; Windows/Linux host suites and desktop exports pass. The old DLL is

still loaded in the repository editor. Details are in [PERFORMANCE.md](PERFORMANCE.md).



Preview28 preserves draw buffers across ancestor layout when a retained grid's

incoming origin, transform, opacity, filter, scissor and clip inputs stay equal.

The Windows engine-font gallery replays that grid on 277 of 300 measured

frames. Five interleaved comparisons measure **5.305 -> 3.667 ms mean frame**,

**4.728 -> 2.867 ms median frame** and **3.045 -> 1.177 ms mean core update**,

as medians across runs. A frame still reaches 86.693 ms; the stall issue is

unresolved. Stub-font headless time slightly regresses, with unchanged allocation

counts/bytes. Release passes **589,873 checks**; the sanitizer corpus and all

768 expanded retention checks pass. Exact full-repaint comparisons cover

origin/clip/transform changes, and disabled-key controls reproduce stale output.

Native snapshots, backend comparisons, interactive states, Windows/Linux host

suites and desktop exports retain their preceding results. The repository's

Windows DLL remains unchanged. See [PERFORMANCE.md](PERFORMANCE.md).



Preview29 reuses Godot's packed vertex/color/UV/index arrays through immutable

command versions, added separately in ABI minor 12. Five interleaved Windows

comparisons measure **4.338 -> 3.685 ms mean frame** and **2.982 -> 2.553 ms

median frame**, with mean core update unchanged at **1.237 ms**. A separate

logged run reduces packing from 0.488 to 0.101 ms; GPU submissions/uploads

remain unchanged. Long stalls still reach 92.748 ms. Release and ASan/UBSan

each pass **589,995 checks**. All 26 cache enabled/disabled pixel comparisons

match on Windows/Linux, including native material/modulation and changing

batch inputs. Eight stress snapshots, 47 backend rows, 12 interactive states,

18 host suites per platform and desktop exports retain their prior results.

The repository editor still loads the old DLL. See [PERFORMANCE.md](PERFORMANCE.md).



Preview30 skips glyph preparation in unchanged subtrees while their atlas

slots remain valid. The engine-font stress grid skips this walk on all 300

profiled updates; glyph preparation drops from 0.167 to 0.015 ms. Five paired

native runs measure **1.347 -> 1.202 ms mean core update**, with whole-frame

means nearly unchanged at **2.925 -> 2.895 ms**. The headless measured update

makes 1,207 fewer allocations. Release and ASan/UBSan each pass **590,075

checks**; a control build fails the new skip assertions. Cache enabled/disabled

pixels, native snapshots, backend/interactive comparisons, host suites and

desktop exports retain their previous results. Previously observed long stalls

remain unresolved, and the editor still uses its old DLL. See [PERFORMANCE.md](PERFORMANCE.md).



Preview31 computes backdrops only for the box builder's eligible top-layer

hosts, eliminating 719 unused styles on layout-stress. Five interleaved

Windows engine-font comparisons reduce mean cold gallery build time from

**30.730 to 23.437 ms** (medians of run means); animated frame time is nearly

unchanged. Release and ASan/UBSan each pass **590,185 checks**, including

backdrop lifecycle and mutation regressions. Eight native snapshots, backend

comparisons, host suites and native exports preserve their previous results.

Normal layout flow remains the largest cold stage, and the repository editor

still loads the old DLL. See [PERFORMANCE.md](PERFORMANCE.md).



Preview32 reuses bounded plain-text line results across sizing probes within

each layout pass, skipping 1,256 of layout-stress's 1,774 inline-layout calls.

Five new interleaved Windows engine-font comparisons improve cold build time

**24.309 -> 22.063 ms** against preview31 (medians of run means). Animated

frame time is nearly unchanged; allocated bytes rise by 30,784 per measured

headless update, with allocation count unchanged. Release and ASan/UBSan each

pass **590,330 checks**, including changing widths and button/table alignment.

Native snapshots, backend comparisons, host suites and desktop exports retain

their previous results. Cold builds still exceed 16.7 ms, long rendering

stalls remain unresolved, and the editor still loads its older DLL.

See [PERFORMANCE.md](PERFORMANCE.md) for measurements and the preview32 ZIP.



Preview33 shares immutable synthetic fonts in a bounded native pool and fixes

a weight-cache error that made 700/800 depend on request order. Five final

paired comparisons improve median cold run means **22.750 -> 20.523 ms**

against preview32; four pairs improve, with substantial machine-load variation.

Core Release/ASan/UBSan retain **590,330 passing checks**. Native font tests

pass **797 Windows / 707 Linux checks**, including exact native weight/italic

coverage and ownership through eviction; an instrumented Linux adapter also

passes 707 checks. Cache-enabled/disabled native stress pixels match on both

platforms, and backend, host and export checks retain their preceding results.

Mixed-weight text intentionally changes from preview32's incorrect synthesis.

The editor still uses the old DLL, cold builds still exceed 16.7 ms, and long

rendering stalls remain open. See [PERFORMANCE.md](PERFORMANCE.md) for the

memory tradeoff, measurement limits and preview33 artifact.



Preview34 allocates raw computed-style values in stable pages instead of

constructing 334 strings per element. Five final interleaved Windows native

comparisons improve cold run means **19.354 -> 17.415 ms** against preview33;

all five pairs improve. Animated core timing is nearly flat and measured

steady allocations are unchanged. GCC headless timing remains flat within

variation. Release and ASan/UBSan each pass **590,698 checks**, including

growth, view lifetimes, move/clear/refill and insertion-order-independent diffs.

Native stress pixels match on both platforms, and backend, host and export

checks retain their previous results. Cold builds still exceed 16.7 ms,

rendering spikes remain open, and the editor still uses the old DLL.

See [PERFORMANCE.md](PERFORMANCE.md) for the tradeoffs and preview34 artifact.



Preview35 enumerates declarations through occupancy words and constructs

Godot glyph dictionary keys once per text run. Five final paired comparisons

improve native cold run means **19.182 -> 17.522 ms** against preview34; four

pairs improve. Animated core time is nearly flat and measured steady

allocations are unchanged. Release and ASan/UBSan each pass **591,033 checks**;

native font tests pass **797 Windows / 707 Linux checks**. Stress pixels,

backend comparisons and exports retain their previous results. Both platforms

pass 18 host suites, after one Linux fresh import exits without a diagnostic

and a new fixture passes. A 95.933 ms whole-frame stall recurs, and cold builds

remain above 16.7 ms. The independent viewport-font memo failure led to

preview36 below. The editor still uses the old DLL. See [PERFORMANCE.md](PERFORMANCE.md)

for the measurements, import limitation and preview35 artifact.



Preview36 fixes stale viewport-relative font sizes after resize by including

viewport dimensions, root metrics and DPI in the memo key. Explicit pixel

values retain a versioned fast path. Release and ASan/UBSan each pass

**591,870 checks**; Chrome passes 80 geometry checks, and both desktop platforms

pass 208 native geometry checks, 336 rendered checks and all 19 host suites.

Stress pixels, backend comparisons and native exports retain their preceding

results. Five paired native comparisons measure **18.175 -> 18.838 ms** cold

and **1.157 -> 1.201 ms** animated core time against preview35. This correctness

fix shows a small measured cost; a 94.249 ms whole-frame stall also recurs.

Performance remains work, as does nested relative-font inheritance. The editor

still uses the old DLL. See [PERFORMANCE.md](PERFORMANCE.md) for the protocol,

variation and preview36 artifact.



Preview37 reuses parsed, directly declared border widths while resolving their

relative units against current context inputs. It also removes locale-dependent

numeric parsing from the raw resolver. A reproduced registry-initial-value

cache error is guarded by keeping inherited/default widths on fresh resolution.

Release and ASan/UBSan each pass **593,497 checks**. Native stress pixels,

all 47 backend results, 12 live states, 19 host suites per platform and native

exports retain their preceding results. Five final native pairs measure

**19.205 -> 18.874 ms** cold and **1.069 -> 1.063 ms** animated core against

preview36. Whole-frame means regress **2.743 -> 3.853 ms**, and a 95.750 ms

stall recurs. Cold allocations rise by 1,236 / 36,256 bytes; measured animated

allocations stay unchanged. This is a small reduction in repeated parsing,

not a solution to cold-opening cost or frame stalls. The editor still uses

the older DLL. See [PERFORMANCE.md](PERFORMANCE.md) for the final preview37 ZIP,

measurement limits and rejected optimization experiments.



Preview38 combines min/max intrinsic-size walks during flex/grid parent input

capture. All 772,629 measurements checked against the previous routines agree

exactly; native pixels, host suites and exported examples retain their results.

Headless cold means improve **10.035 -> 8.914 ms** and animated core means

**2.216 -> 2.104 ms**, with unchanged allocations. Native cold means remain

effectively flat (**19.370 -> 19.292 ms**), and whole-frame means regress

**2.400 -> 2.688 ms** despite slightly lower native core time. A 213.181 ms

rendered stall and an intermittent Linux fresh-import abort remain unresolved.

The repository Windows DLL is unchanged. See [PERFORMANCE.md](PERFORMANCE.md)

for the measurements, final artifact and preformatted-newline sizing follow-up.



Preview39 stops native font adoption from modifying the default Font's shared

RID array. Each document previously appended another copy of the compatibility

fallbacks; the observed chain grew 9, 17, 25, 33. An owned list now keeps the

source resource unchanged across opening, font toggles and destruction.

Preview38 fails 20 new ownership checks on both platforms; preview39 passes

75 headless and 79 rendered theme checks. Native cold means remain mixed

(**17.249 -> 17.505 ms**), and rendered stalls persist under uncontrolled shared

graphics load. The font list bug is fixed; the broader performance goal remains

open. See [PERFORMANCE.md](PERFORMANCE.md) for evidence and the preview39 artifact.



Preview40 shares bounded shaped runs from immutable synthetic fonts and remaps

glyphs into each receiving document's handles. Later fresh stress documents

avoid 116 of 205 native shaping calls. Final paired fresh-document means improve

**19.335 -> 17.927 ms**, but first-open timing regresses and whole-frame means

remain flat (**15.683 -> 15.920 ms**). A 91.017 ms rendered stall remains under

uncontrolled shared graphics load. All 10,236 Windows / 10,226 Linux native

font checks pass, including Linux adapter ASan/UBSan; host suites, stress pixels

and native exports retain their results. A fresh Linux test-import failure

requires a successful fresh retry, and its cause remains open. The preview ZIP

is available; the running repository Windows editor still uses its previous DLL.

See [PERFORMANCE.md](PERFORMANCE.md) for the measured scope and artifact.



A resumed three-pair native comparison against the DLL still installed in the

development project measures **15.849 -> 2.561 ms** whole frames and

**10.094 -> 1.117 ms** core updates with preview40. Every pair improves; its

worst measured frame is 9.405 ms. This measures the accumulated improvements

since that installed binary, not preview40's shaping change alone. The updated

DLL still awaits installation/editor reload. Cold opening and the long stalls

seen in earlier workloads remain open; three runs establish no latency bound.



The complete core mutation suite now also builds and passes **593,614 checks**

with Windows MSVC Release. Its existing background test needed an explicit

`<algorithm>` include for `std::clamp`. A separately tested inline style-read

optimization regressed cold openings and was reverted; the full suite passes

again after restoring the runtime sources. Preview40 remains the latest addon.



Preview41 for Windows includes the preserved-newline intrinsic-sizing fix:

`pre` text `aa bbbb\ncc` with 8px advances measures 56px rather than joining

the forced lines into 72px. The complete Windows Release suite passes

**593,783 checks**, including flex/grid sizing and live whitespace changes;

24 local Chrome intrinsic-width assertions agree. All 20 native host suites

pass, including 336 new headless / 432 rendered intrinsic-sizing checks.

Four native corpus captures match preview40 exactly, and the Windows-only ZIP

passes fresh import, packed resources and debug/release/embedded native exports

with matching example pixels. Native layout-stress medians are **2.226 ms**

whole frames and **0.956 ms** core updates; cold fresh-document means remain

around **16.200 ms**, with **22.247 ms** first opens. Differences from preview40

are small and mixed; the broader latency work remains open. Linux and sanitizer

checks have not been repeated for this core change. Preview40 remains the latest

combined-platform ZIP, and the development editor still has its older DLL.

Details and the Windows artifact are in [PERFORMANCE.md](PERFORMANCE.md).



The default Windows core build now also builds every command-line tool;

the benchmark's unconditional POSIX headers previously broke that target.

Allocation attribution works in layout, full-update and cold modes, including

named Windows stacks with local PDBs. Its 15 CLI cases pass in Release and

RelWithDebInfo. This tooling change leaves preview41 as the runtime artifact;

the POSIX sampling path still needs its own validation. See the

[benchmark guide](../tools/weva_bench/README.md).



Preview42 for Windows removes temporary strings from name-based inherited style

reads. **593,800 core checks** pass, plus a guard proving 8,000 reads through a

32-level chain allocate nothing. Cold headless allocation counts drop **58,273

to 54,567**. Native timing is mixed in the short comparison; longer paired runs

improve median means **2.296 -> 2.256 ms** whole frames and **0.991 -> 0.953 ms**

core updates. All 20 host suites, four native page pixel comparisons and Windows

debug/release/embedded export checks pass. First opens still exceed a frame

budget, and earlier long stalls remain unresolved. Linux and sanitizer checks

remain pending for this change; preview40 is still the latest combined-platform

package. The development editor's older DLL has not been replaced. See

[PERFORMANCE.md](PERFORMANCE.md) for the measurements and preview42 ZIP.



Preview43 for Windows prepares gradient interpolation work once per texture.

The minimap texture benchmark improves **22.192 -> 20.892 ms** with exact pixels;

whole-HUD cold-open comparisons remain mixed, so this is not evidence of a

whole-document speedup. **614,330 core checks**, all 20 host suites, four native

page pixel comparisons and Windows import/pack/native export checks pass.

The span data adds 1,640 temporary requested bytes per cold HUD build with no

extra allocations. Linux/sanitizer validation and the editor reload remain

pending. Cold HUD and first-open latency remain product work; see

[PERFORMANCE.md](PERFORMANCE.md) for the preview43 artifact and full evidence.



Preview44 for Windows removes redundant blur-buffer clearing, interleaves shadow

rows and uses exact SSE2 channel arithmetic where available. The HUD blur kernel

improves **12.058 -> 10.267 ms**; five native cold-open pairs improve median run

means **72.526 -> 71.457 ms**. Cold opening remains over budget. **614,536 core

checks**, a **192-case portable/normal kernel comparison**, all 20 host suites,

four native page pixel comparisons and Windows import/pack/export checks pass.

Pixels remain unchanged. Linux/sanitizer validation and the editor reload are

still pending; the latest combined-platform package remains preview40. See

[PERFORMANCE.md](PERFORMANCE.md) for the preview44 artifact and measurement limits.



Preview45 for Windows fixes nested relative font-size inheritance, including

explicit inheritance, generated content and live changes with equal raw CSS

strings but different computed sizes. **615,403 core checks**, **134 Chrome

checks**, all **21 host suites / 2,035 headless checks**, and **896 rendered

inheritance checks** pass. Four gallery PNGs remain exact; Windows import,

pack and all three native export configurations pass. The paired layout-stress

timings are mixed: median run means are **2.705 -> 2.885 ms** whole frame and

**1.234 -> 1.283 ms** core, with three of five native pairs improving. This is

a correctness fix with no demonstrated speedup. Linux/sanitizer verification,

editor installation/reload, and cold-open latency work remain pending. See

[PERFORMANCE.md](PERFORMANCE.md) for the preview45 artifact and measured costs.



Preview46 for Windows reserves geometry buffers before emitting rounded shapes

and text, while preserving geometric growth for repeated appends and avoiding

empty-glyph reservations. Layout-stress's measured animated allocations fall

**14,448 -> 9,004**, and headless update means improve **3.021 -> 2.787 ms**.

Native frame means improve slightly, **2.232 -> 2.215 ms**, with four of five

pairs faster. The new boundary trace confirms the retained grid needs repaint

when its fractional position and rounded clip change. **615,403 core checks**,

all five CTest targets, 21 host suites and **1,664 rendered regression checks**

pass. Four native gallery captures are unchanged; Windows import/PCK and all

three native export configurations pass. Editor installation/reload and Linux/

sanitizer verification remain pending. See [PERFORMANCE.md](PERFORMANCE.md).



Preview47 for Windows removes temporary paint-order lists when the sibling

links already follow paint order, and shares the traversal with hit testing.

Median sampled animated allocations fall **8,969 -> 8,160**. Native frame

means improve **2.921 -> 2.484 ms** across five pairs, with substantial baseline

variation; headless animated latency is effectively flat. All six CTest

targets pass, including **113,803 ordering/allocation checks**, alongside all

21 host suites. Four native gallery captures and native exported example

captures are unchanged. Windows import/PCK/debug/release/embedded exports pass.

Editor installation/reload and Linux/sanitizer verification remain pending.

See [PERFORMANCE.md](PERFORMANCE.md) for the measurements and artifact.



Preview48 for Windows reuses the existing corner-radius parse cache while

resolving used radii from current geometry and length context. It also fixes

tabs/newlines/comments in radius pairs, verified by 52 Chrome comparisons and

a 6,823-check core guard. Sampled animated allocations fall **8,160 -> 4,776**;

headless updates improve **2.694 -> 2.456 ms** and native frames improve slightly

**2.186 -> 2.164 ms**, with all five pairs faster. All seven CTest targets and

21 Godot host suites pass. Four gallery captures are unchanged, and Windows

import/PCK/debug/release/embedded exports pass with matching example pixels.

Editor installation/reload and Linux/sanitizer verification remain pending.

See [PERFORMANCE.md](PERFORMANCE.md) for scope and artifacts.



Preview49 for Windows compacts clip-preparation scratch in place and reserves

outline/piece buffers without changing geometry. The 1,076-polygon guard

matches frozen preview48's geometry digest and removes 2,155 budget failures.

Sampled animated allocations fall **4,776 -> 3,796**. Focused clip preparation

improves, but overall latency evidence is mixed: the initial headless comparison

regresses, a same-CPU diagnostic has nearly flat wall means, and native pairs

mostly improve. No consistent overall speedup is claimed. All eight CTest

targets, 21 host suites and Windows import/PCK/native exports pass; four gallery

captures are unchanged. Editor installation/reload and Linux/sanitizer checks

remain pending. [PERFORMANCE.md](PERFORMANCE.md) records all timing results.



Preview50 for Windows transfers temporary meshes into paint submission and

the collecting backend without the previous vertex/index copies. Input text

decoration measurements finish before transfer. The 56-case ownership guard

matches frozen preview49's geometry digest and passes all allocation budgets.

Sampled animated allocations fall **3,796 -> 3,290**. Headless animated means

improve slightly, but cold and native frame means regress slightly; this is an

allocation reduction, not evidence of a reliable overall latency gain. All

nine CTest targets, 21 host suites and Windows import/PCK/native exports pass;

four gallery captures remain exact. The development project still contains

its older DLL. The reported 30ms editor case, installation/reload, and Linux/

sanitizer verification remain pending. Details are in

[PERFORMANCE.md](PERFORMANCE.md).



Preview51 for Windows fixes computed inheritance of percentage/em line heights,

including explicit inheritance, pseudo values and numeric math expressions.

Chrome passes **508 checks**, and the new Godot fixture passes **2,400 rendered

checks** against explicit-pixel controls; preview50 fails 960 headless checks

in the same fixture. All nine CTest targets / **616,389 core checks** and all

22 host suites / **4,035 checks** pass. Layout-stress mean timings are nearly

flat, with mixed pair outcomes; this is a fidelity fix. Four existing gallery

captures remain exact, and Windows fresh-project/PCK/native export checks pass.

The old development-project DLL, the reported 30ms

editor result, Linux/sanitizer coverage and historical harvest calibration

differences remain open. Parent-relative line-height units and initial/root

metrics are not addressed by the inheritance fix.



The verified preview51 DLL has now also been installed in the actual development

project after confirming Godot was closed. The prior DLL is preserved for

rollback. Its matched standalone gallery runs drop from 18.530 to 2.668 ms

median whole-frame means, with p95 falling from 26.362 to 5.005 ms. Actual-project

host/line-height checks and a capture comparison pass. This closes the stale

Windows development-DLL issue; editor-specific overhead, cold-path costs and

the remaining product requirements still need their own work and evidence.



| Requirement | Evidence / remaining work |

|---|---|

| Live stylesheet replacement | Fixed `doc.css` appending old rules and the core ignoring newly added stylesheets until another mutation. Core and host regressions cover removal, empty CSS, focus/value/selection preservation, keyframes and custom-property registrations. |

| Fresh-project import | Fixed a Windows crash caused by compiling the host without its `godot-cpp` target's definitions. Fresh-cache import succeeds with normal editor initialization. Linux 4.7.2 immediate headless import reproduces [Godot #111645](https://github.com/godotengine/godot/issues/111645); the smoke lets the editor initialize for 60 frames. |

| Exported artwork and markup | Fixed imported textures disappearing from PCKs when the original file is absent. Project textures now resolve through `ResourceLoader`; raw PNGs retain the file reader. The isolated fixture covers HTML/CSS export filters, PNG/SVG intrinsic size, draws, stylesheet replacement and missing-artwork diagnostics. |

| Reproducible native builds | CMake now builds and consumes the `godot-cpp` target, inheriting its generated headers and ABI definitions. Fixed MSVC rejecting the blur kernel's captured array-bound constant. The Windows build and fresh-project/PCK smoke pass. Pin dependency/toolchain revisions and verify configuration variants before release. |

| Installable addon | A local Windows/Linux x86_64 preview ZIP installs into a fresh project and exports its HTML/CSS/SVG example. Includes licenses, checksums and installation instructions. Both platforms pass button/controller and two-way binding checks in the installed project and PCK. `check.sh` now packages and checks Linux; repeat on each shipped platform. |

| Native desktop exports | Standard Godot 4.7.2 debug, release and embedded-pack executables pass on Windows and Linux x86_64. Godot exports the exact selected library without manual copying. Relocated builds run with the source project hidden; example rendering matches the project. `check.sh` now requires native export checks and matching desktop templates. Other platforms, renderers and toolchain configurations still need their own evidence. |

| Replaced images in flex layouts | Visual review of the standalone example exposed zero-size images despite successful loading. Fixed intrinsic contributions and aspect-ratio sizing during flex reflow, including frames and percentage widths. Nine regression cases agree with Chrome; the example's render now shows its SVG and unclipped input text. |

| Native GUI integration | `WevaDocument` now derives from `Control`. Fixed duplicate typing across documents/native fields, clicks through native overlays, hidden-document input, lost Unicode, held Backspace and pointer focus. Native tests cover Tab/Shift+Tab, CSS click-through, CanvasLayer transforms, container/anchor resize, dropdown rows, outside popup dismissal and scene cleanup. This is an API migration for scripts typed as `Node2D`. |

| Text editing and popup lifecycle | Added consumed-text and non-wrapping focus APIs; readonly fields reject edits and undo. Native shortcut routing supports select/copy/cut/paste/undo/redo. Outside-press observers use a transient input version so a native handler can open a new popup without an older dismissal closing it. Manual popovers remain open. |

| Keyboard form actions | Buttons, links, checkbox/radio Space, radio arrows and Tab groups, range keys, summaries, popover triggers and implicit submission share native activation with pointer input. Focus changes cancel held Space. A nested button in a summary does not toggle its details. Chrome 151 passes 134 browser oracle checks; matching core cases and 35 native Godot checks cover event timing, ownership, value normalization and callbacks. See [KEYBOARD_INPUT.md](KEYBOARD_INPUT.md) for the implemented subset. |

| Two-way input bindings | Fixed a checkbox bound to a false boolean immediately reverting when checked, and Unicode text-input signals being decoded as Latin-1. Native tests cover both checkbox directions and non-ASCII text reaching the signal and bound field. |

| IME composition | Added preedit rendering, selection, lifecycle signals, one-step undo, full queued Unicode text and candidate caret geometry. Chrome passes 40 oracle checks; Windows/Linux Godot 4.7.2 pass 28 integration checks. Real Linux X11 IBus Pinyin passes nine checks in asynchronous mode. Default synchronous IBus mode still fails commit/cancel; real Windows and other IME sessions remain unverified. The full mutation corpus now passes 577,867 checks in Release and ASan/UBSan. See [IME.md](IME.md). |

| Long fields and Unicode editing | Single-line fields scroll with the caret; password masking and clicks map Unicode clusters to source bytes. Carets count trailing spaces, and transformed inputs clip their overlays correctly. Unicode 17 passes all 766 official segmentation cases; Chrome passes 91 editing checks and Windows/Linux Godot pass 86 checks with both fonts. Idle focused values no longer allocate a copy each frame. See [TEXT_EDITING.md](TEXT_EDITING.md) for browser tailoring and remaining bidi, wrapped navigation and pointer limits. |

| Native font positioning and themes | Shaped placement offsets and UTF-8 source clusters survive the C ABI, and automatic fallback glyphs use their actual native font. The Control's theme/default Font resource, overrides, variations and live resource changes now reach layout and paint. Native checks cover glyph placement, bitmap refresh, fallback changes, shared-resource teardown and texture ownership. Full paragraph bidi, bidi carets, vertical text, per-element CSS font-family selection and the documented stock-engine long-emoji bug remain open. |

| Viewport-relative font-size cache | Fixed stale font sizes after viewport/context changes. An empty `div` with `font-size:10vw;width:1em;height:1em` now changes from 10x10 to 20x20 when width doubles. The memo includes viewport dimensions, root metrics and DPI. Direct resolver and C ABI regressions, 80 Chrome checks, and Windows/Linux native geometry and rendered comparisons cover repeated resize and return to the original size. Preview45 fixes the nested relative-font chain limit and computed inheritance. Preview51 fixes inherited relative line-height lengths and numeric math expressions with 508 Chrome checks and Windows native controls. Absolute size keywords, root rem context and parent-relative line-height units still need conformance work. |

| Text length limits and paste | `maxlength` counts UTF-16 units for supported text inputs and textarea, including selection replacement, normalized pasted line endings and composition commit/blur. Number inputs ignore it; existing script values and undo are not length-truncated. Rejected typing creates no edit signal or undo step and stays consumed. Added `paste_text` (C ABI minor 6) so native clipboard insertion accepts leading whitespace and forms its own undo step. Fixed textarea Enter inserting beside selected text. Chrome passes 150 checks, Windows/Linux Godot pass 44 new checks, and the installed/exported example verifies Unicode limits and binding undo. Full form validation and the remaining picker/number editing behavior remain work. |

| Native IME comparison | Isolated the intermittent loss to Godot 4.7.2's X11 key handler suppressing an XIM commit before preedit completion. A standalone LineEdit reproduces it without Weva; matched unpatched builds fail 3/3 delayed sessions, while a local engine patch passes 3/3 for each control. Synchronous IBus 1.5.29 also lacks preedit hide/show handlers; a private backport of the upstream fix makes both controls pass commit, cancel and undo. The stock engine remains affected. Patches, traces and reproduction instructions are in [the standalone fixture](../tools/godot-ime-repro/README.md); these are diagnostic builds, not a shipped engine fix. |

| Select interaction | Native listbox click/drag, Ctrl/Meta toggling, Shift ranges, navigation and select-all are implemented. Parsed `size`/`multiple` changes invalidate child boxes independently of selectedness; `size=1` uses a dropdown. Disabled rows are skipped and no-op pointer choices emit no value event. Unicode typeahead follows the documented English ICU profile and Chrome event timing. Labels and group headings render and update through input versions. Held listbox drags autoscroll beyond the Control, including while CSS time is paused, with selection extended by real pointer movement and committed on release. Language tailoring and arbitrary popup row layout remain work. |

| Remaining game integration | Verify broader IME compatibility, touch-device and gamepad behavior, and accessibility. Picker-specific value sanitization, number stepping, remaining select behavior, picker controls, range direction/orientation, `minlength`, validity reporting and submission constraints remain release work. Basic editing and activation are not complete browser form behavior. |

| Browser behavior | Chrome leads when the C# reference is wrong. Preview56's fresh arbitration leaves five sample and five harvest findings, including the component-capture defect, along with documented unsupported features. Keep these visible and fix against browser evidence. See [ORACLE.md](ORACLE.md). |

| Release verification | Exercise installation and export on each declared supported platform. Keep compatibility declarations, binaries, public docs and automated gates consistent. |



This is an evidence ledger, not a reduced definition of completion. The addon

is not release-ready while any required installation, authoring, interaction,

export or compatibility behavior remains unverified or broken.


## Archived checkpoint152 current-build statement

## Current installed build: slider direction and geometry

Horizontal RTL and vertical sliders now share their pointer and paint geometry.
Arrow keys follow the same orientation as Chrome; endpoint clicks account for
thumb size. The regression matches 66 Chrome-derived cases, repeated across fresh,
live-restyled and padded controls in 198 core cases. A transformed-slider probe
also agrees with Chrome using an explicitly matched 14px thumb.

All 14 core suites (484,844 checks), 16 sanitizer gates, 27,893 native host checks,
sample/exports and 14 installed smoke suites pass. All 624 timing checks pass:
72 regular desktop, 276 automatic 1080p 3D and 276 automatic 4K 3D. Both local
addon copies use this build (checkpoint 152, ABI minor 22).
[Complete qualification](verification/range152.json).

This retains the earlier 4 MiB shaped-cache payload limit and the centred sample
dialog. Cold construction remains a loading-time operation. The original 4K
inventory/gameplay failure did not recur but its cause remains unproven; its
receipt is preserved in [load/lifecycle history](../examples/frontier_camp/LOAD_AND_LIFECYCLE.md).
Other hardware, physical input, broader lifecycle and the correctness gaps below
remain open. Passing these measured profiles is not universal release acceptance.



## Archived checkpoint153 current-build statement

## Current installed build: numeric keyboard controls

Both local addons now support number-input arrow stepping, limits and step bases,
typed model updates, and input/change events without duplicate commits on blur.
The 76 Chrome cases pass through Godot viewport key routing. All 14 core suites
(485,452 main checks), 16 sanitizer gates, 28,127 native checks, package/sample/
exports and 15 installed smoke suites pass. Checkpoint153 retains ABI minor22.

This is a reversible local development update, not release approval. Regular
desktop timing passed 70/72 checks; one Vulkan run exceeded redundant-signal
(.468 ms vs .3 ms) and hover (.558 ms vs .5 ms) limits. Hover also exceeded its
target on the previous build in a focused comparison. All 276 automatic 4K 3D
checks passed. The first 1080p run failed gameplay/binding assertions during the
UI-disabled case, which injects no test input. Six traced follow-up runs did not
reproduce it; its cause remains unproven. The failed runs remain in the evidence.

The numeric branch is not exercised by those failed sample workloads. The local
update installs the verified correctness fix while retaining the unresolved
performance/input investigation. Benchmark-mode accepted gameplay inputs now log
event details to aid that investigation. Cold construction, broader lifecycle,
other hardware and the correctness gaps below remain open.
[Installed evidence](verification/number153-installed.json).


## Prior installed build: binding allocations and sample audio routing

Both local addons avoid rebuilding unchanged bound text and attribute strings.
The focused HUD regression drops 5,000 allocations to zero over 1,000 refreshes,
while preserving all 3,000 resolver calls. This is not a zero-allocation claim for
the complete UI. The sample also applies audio settings only when volume or mute
changes; editing a player name no longer calls AudioServer setters.

All 14 core suites (485,498 main checks), 16 sanitizer gates, 28,127 native checks,
sample/exports and 15 installed smoke suites pass. The sample now has 95 headless
and 101 rendered integration checks. Checkpoint155 retains ABI minor22.

The core build passed all 72 desktop timing checks. Its first full 1080p 3D
qualification passed functional checks and 275/276 timing checks, failing one
typing API limit at 1.207 ms against 1 ms. After correcting the sample's audio
handler, six focused typing runs passed all 24 unchanged typing limits, with
0.359–0.762 ms API p95 and no audio-setting updates. The original failed profile
is retained; a full 1080p run after that sample correction and 4K on this core
build have not been repeated. These are scoped development checks, not release
approval or proof of causal frame-time gains.

The earlier unexplained gameplay input and hover overruns remain historical
unresolved observations. Subsequent functional runs did not reproduce the input
failure. Cold construction, broader lifecycle, other hardware and the correctness
gaps below remain open. [Installed evidence](verification/binding155.json).



## Superseded installed binding build (157)

Current automatic 1080p 3D measurement: all six functional runs pass, but only
275/276 timing gates pass. OpenGL run 3's clock-update whole-frame p95 is
36.268 ms against 16.667 ms. That workload's maximum core update time is
0.967 ms; the cause of the remaining interval is not established. There are
only ten changed clock samples, so this p95 equals the worst changed interval.
The failed gate is retained. All UI API and core timing limits pass. Current
4K remains unmeasured. Full results are in the class157 verification record.


The addon remains a development preview. The active objective is to fix the
remaining issues that matter for game UI; passing the current survival sample
alone does not establish completion. This page is the current checklist.
The complete [historical evidence ledger](PRODUCT_READINESS_HISTORY.md) is preserved;
its old “remaining” lists and test counts are not current release conclusions.

## Current installed build: long class bindings

Both local addons now avoid copying long class-binding names and literal paths
on unchanged refreshes. The focused regression drops 2,000 allocations to zero
across 1,000 refreshes. Changed classes, authored classes and expanded paths remain
covered. The earlier bound-text optimization and sample audio fix are included.

All 14 Release core suites, 231 focused sanitized binding checks, 28,127 native
checks, sample/export verification and 15 installed smoke suites pass. The sample
passes 95 headless and 101 rendered checks. Checkpoint157 retains ABI minor22.
Sanitizer coverage for this change is focused, not a fresh full-suite run.
[Installed verification](verification/class157.json).

The current build passes all 72 manual desktop timing limits across three OpenGL
and three Vulkan runs (600 frames after 120 warmups per workload, 1280x720).
API p95 ranges: HUD updates 0.373–0.501 ms, inventory sorting 0.966–1.119 ms,
typing 0.439–0.559 ms, toggles 1.429–1.949 ms. No causal speedup is established.
The preceding automatic 1080p/4K profiles remain historical; the previous failed typing limit
and its focused follow-up are preserved. Cold construction, broader lifecycle,
other hardware, historical input/timing failures and correctness gaps below
remain open. [Previous timing evidence](verification/binding155.json).

## Qualification notes preceding opacity-fix installation

The following text preserves its original checkpoint-relative wording. It is
historical, not a description of the currently installed addon.


This remains a development preview; the game-UI goal is not complete.

The installed addon now includes positioned-table ordering, containing-block-aware
overflow clipping, rounded hit testing and cheaper rejection outside clipped panels.
All 14 Release suites, 16 sanitizer gates, 39 native host entries, sample/exports
and 15 installed smoke suites pass. All 72 manual desktop timing gates pass on
the explicit patched Godot template. API p95 across six runs: vitals 0.597–0.738 ms,
inventory sorting 1.332–1.617 ms, toggles 1.434–2.706 ms, typing 0.578–0.884 ms,
hover 0.309–0.360 ms. These are measured desktop CPU costs, not a causal speedup
or GPU/device certification. Automatic 3D, 4K and lifecycle must be requalified
for this installed build. [Installed qualification](verification/clip-rejection189.json).

The subsequent source correction passes 64/64 expanded table-stacking pixel and
pointer cases, plus all 14 Release suites, 16 sanitizer gates, native host, sample
and export checks. Desktop qualification fails two timing gates (inventory sort 2.020 ms against
2 ms; clock update 3.514 ms against 3 ms). All functional runs pass; it is not installed.
[Source correction](verification/table-stacking191.json),

The same native candidate passes all 276 automatic 1080p 3D timing checks.
The highest whole-frame/changed-frame p95 is 5.577 ms, including the scene.
This does not supersede its two failed manual desktop timing checks.

[retained failing baseline](verification/table-stacking190.json).

A focused instrumented comparison did not reproduce the two desktop overruns.
Candidate API p95 was 1.131 ms for clock updates and 1.227 ms for inventory sorting;
the installed baseline measured 1.338 and 1.176 ms. A candidate trace captured
0.831 ms elapsed with only 14,073 thread cycles, consistent with a scheduling
pause, but the original failures' cause is not proven. The sample contains no
table layout. These diagnostics do not replace the failed qualification.
[Instrumented evidence](verification/runtime-trace192.json).

The preceding native stacking candidate subsequently passed all 72 desktop gates
with 6,000 frames per workload, including 100 clock-change samples per run. Its
earlier failures remain recorded. A further source correction now gives painting
and stacking the same numeric opacity resolver: equivalent fully opaque forms
pass 12/12 Chrome pixel cases, and all 14 hit cases pass. All 14 Release suites
pass. The two apparent opacity .999 pixel discrepancies were a comparison error
(linear RGB versus sRGB): correct byte comparison passes all 14 cases. Native
host, sample, export and all 16 sanitizer gates pass; runtime timing qualification
is running.
[Opacity correction](verification/opacity193.json).

## Previous installed runtime qualification (historical)

The following measurements describe the preceding installed binary, not the
current addon.

A subsequent five-minute Unicode lifecycle run also passes: 200 recreations,
161,306 soak frames and constant soak counts of 496 nodes / 1,949 objects.
Median private memory by minute is 861.22, 861.88, 861.97, 862.11 and 862.11 MB;
growth largely settles in this observation. This does not prove hours-long
stability or attribute whole-process memory to the UI. Cold CPU is 83.260 ms
and prepared reuse CPU p95 is 0.404 ms in this run, but reuse through draw p95
is 11.341 ms. It is a lifecycle/memory result, not a new timing-budget pass.
[Five-minute lifecycle evidence](verification/lifecycle185.json).

The installed addon has now been measured with an explicitly selected and hashed
patched Godot release template. The normal desktop profile passes all 72 timing
checks across three OpenGL and three Vulkan runs. API p95 ranges: vitals at 10Hz
0.276–0.707 ms, inventory sorting 0.737–1.216 ms, settings toggles 1.391–1.808 ms,
typing 0.391–0.830 ms, hover 0.202–0.444 ms. The automatic 1080p deterministic
3D profile passes all 276 timing checks. These runs do not prove a causal speedup.

The same installed addon passes 200 UI recreations and a 120-second Unicode soak
(127,069 frames, 21,619 name updates) on the explicit patched runtime. Cold first
construction costs 70.620 ms CPU; hidden preparation costs 19.044 ms. Prepared
reuse with hidden data updates costs 0.658 ms CPU p95 and 1.580 ms through draw
p95. Prepared/cold screenshots match. Private process memory rises from 852.72 MB
at soak start to 861.59 MB at the last soak marker and ends at 859.48 MB after
teardown; this is not proof of long-term memory stability.

The earlier performance exporter selected a cached/default template despite
using the patched editor. Its manual and 3D measurements apply to that recorded
executable, not to the intended patched runtime. Its Unicode lifecycle run
crashed with Windows heap corruption (0xC0000374); the cause remains unproven.
The exporter now accepts `--release-template`, records its SHA256 and restores
export presets even on failure. Both renderer smoke exports and 23 harness
tests pass. Stock-engine compatibility, 4K requalification, longer memory runs
and other hardware remain open.

[Current runtime measurements](verification/template184.json). All 14 core suites,
16 sanitizer gates, 39 native host entries and 15 installed smoke checks also
pass; [native qualification](verification/rounded-native183.json).


## Superseded installed opacity build (193)

The installed addon now includes corrected table stacking/click order, overflow
clipping and shared numeric opacity resolution. Equivalent values such as `1`,
`1.0` and `100%` preserve the same stacking behavior. Both local addon copies
passed 15 post-install smoke suites. All 14 Release suites, 16 sanitizer gates,
39 native host entries and sample/render/export checks also pass.

The exact installed binary passes all 72 desktop timing gates across three
OpenGL and three Vulkan runs, with 6,000 frames per workload and 100 clock-change
samples per run. API p95 ranges: hover 0.244–0.261 ms, typing 0.544–0.844 ms,
inventory sorting 1.160–1.434 ms, toggles 2.044–2.479 ms, vitals at 10Hz
0.391–0.624 ms and clock at 1Hz 0.623–1.059 ms. These are measured API costs,
not isolated GPU costs, causal speedups or certification for other hardware.
[Installed qualification](verification/opacity193.json).

The preceding stacking binary passed the automatic 1080p 3D profile, but that
result does not qualify this installed binary. Current load failures and
supported-device verification remain open. Earlier failed timing runs and subsequent
longer measurements are retained in the [history](PRODUCT_READINESS_HISTORY.md)
and [stacking qualification](verification/table-stacking191.json).

The installed build's automatic 1080p 3D run passes functional checks but fails
five timing gates in its first OpenGL run. UI-disabled whole-frame p95 is
18.857 ms against 16.667 ms; idle/clock frame gates also fail, and typing API
p95 is 1.153 ms against 1 ms. This is a failed profile, while the UI-disabled
failure prevents assigning all frame overruns solely to UI work. The separate
4K run passes all whole-frame gates (maximum p95 9.031 ms), but fails five typing
and toggle API gates. Typing reaches 1.215 ms against 1 ms; toggles reach 3.601 ms
against 3 ms. All functional checks pass. [Recorded results](verification/opacity193.json).

The installed binary passes 200 recreations and a five-minute Unicode lifecycle
run: 208,642 frames and 35,214 name updates, with matching cold/prepared captures.
Cold construction costs 82.500 ms CPU; prepared reuse with hidden data updates
costs 0.383 ms CPU p95 and 3.496 ms through draw p95. Soak node/object counts
remain constant; private process memory settles near 863.77 MB. This is a
lifecycle observation, not a replacement for failed timing gates or proof of
hours-long memory stability. [Lifecycle evidence](verification/lifecycle193.json).
