# Product readiness

This is a development preview. The game-UI goal is not complete. One core
(`libweva`, ABI minor 42 at this writing) drives two hosts; the Unity host's
standing is first, the Godot host's -- the longer record -- follows.

## Unity host readiness (2026-09-14)

**Where things stand.** `com.wevaui` 1.0.0 is the Unity host for the core,
on Windows x64. The C# engine that 0.1.x shipped was deleted on 2026-09-13;
what remains in C# is the component, the `[UIBind]` controller layer, the URP
pass that draws the core's draw list, `FontEngine` as the font backend, the
Input System feed and the editor tooling. Nothing has been pushed or tagged;
no consumer project has yet been opened on 1.0.

| Requirement | Current assessment and next evidence needed |
|---|---|
| The component and the controller model | `WevaDocument` keeps its 0.1.x name, script GUID and serialized fields, so a scene carries over; `[UIBind]`, `data-each`/`data-model`, `on-<event>`, `SetController` carry over. Verified by the Native EditMode suite (191 pass, 2 env-gated inconclusive; full EditMode 192 pass, 2 inconclusive) -- bindings, rows, typed write-back through nested collections, dictionary binding across disable/enable, dispatch, reload. Not yet verified: a real consumer project (TestWeva, Wavebound) opened on 1.0. |
| The element API | `WevaElement` / `WevaEvent` are the supported surface (`api-stability.md`); `Weva.Native` is internal. 17 `WevaElementTests`, including callbacks that disable/reload the document and recursive event pumping. Programmatic `Value` assignment raises no input/change events; bound values are changed through their model. |
| Rendering | `UIRenderGraphPass` draws the draw list into the camera target; `NativeRenderPassTests` (PlayMode, 5 cases) check the pixels: drawn, stacked by `SortingOrder`, gone when disabled, repainted on change, text, backdrop-filter (the frame is routed through URP's intermediate texture when a backdrop draw is due, asserted by the test; at `AfterRendering` the target is always the back buffer, which cannot be sampled). Not verified: any platform but Windows. |
| Layout conformance | All 334 Chrome captures pass the 1.5px ceiling (62 hand, 225 harvest, 47 samples; no case excused). Direct inline content and inline-block multi-column containers are covered by the new capture and core regressions. The core passes 14 gcc suites (506,310 checks) and 16 sanitizer suites. `goldens_from_unity.py` renders sample pages through the core on Unity; real text metrics remain the host font backend's. |
| Input | Mouse, keyboard with core-owned repeat and double-click, touch, gamepad navigation, IME; every row of `INPUT_PARITY.md` pinned by `NativeInputFeedTests` / `NativeInputTests` through the Input System's test fixture, including astral text, disposal/reload during input handoffs, and pending key releases. Not verified: a physical touchscreen, a physical gamepad, a physical IME. |
| Fonts and text | The package's Inter with real bold/italic, `@font-face` `url()`/`local()`, `RegisterFontFamily`. The package's own shaper over the font's OpenType tables (`UnityFontBackend.Shaping.cs`): visual order and mirrored brackets for right-to-left runs, Arabic joining forms and ligatures, contextual lookups in every format, cursive attachment, marks on their base, the nine main Indic scripts by syllable (`UnityFontBackend.Indic.cs`), pinned by `UnityFontBackendTests` against Segoe UI, Bahnschrift, Dubai and Nirmala UI and rendered against Chrome by eye (`.utmp/showcase/rtl`, `.utmp/showcase/indic`). Not done: Sinhala, Khmer, Myanmar, Tibetan; a `Font` asset gets no substitutions (no bytes); no colour emoji. These are gaps, not open questions. |
| Stylesheets and assets | `<link rel="stylesheet">` next to the asset in the editor, baked for players for scene documents (scene hook) and prefab documents (build preprocess); `BasePath` defaults to the asset's folder; `AssetReader` for a player without files. Verified by `NativeLinkedStylesheetTests`. Not verified: an actual player build's images and fonts end to end. |
| Editor tooling | Inspector with the URP-feature check and fix, HTML diagnostics and Reload; Elements window over the inspector ABI; hot reload of the document, its links and its sheets. The Designer / in-place editor is shelved from 1.0. |
| Build hygiene | Core, plugin and Godot extension at 0 warnings on gcc, clang and MSVC; `gen_bindings.py --check` in CI; the plugin's load test in `check.sh`. |
| Platforms | Windows x64 only. macOS, Linux, Android and iOS have CI jobs that have never run (nothing pushed); each is bundled when its load test passes. |
| Release verification | The 1.0.0 CHANGELOG carries the migration notes. Blocked on the push (first CI run), a consumer opened on 1.0, and platform binaries. |


## Godot host readiness

**Where things stand (2026-09-11).** Both local addons hold build222 (ABI
minor25), build221 plus quarter-pixel text positioning. For a game developer it adds, on top of build211: document text with
many emoji or brackets no longer corrupts or crashes on the stock engine;
`@font-face` loads a game's own fonts, with real bold and italic files;
a controller drives HTML menus without scripting (directional focus, accept,
cancel, held repeat, opt-in wake) and types into text fields through an
on-screen keyboard; and `WevaView` reloads an edited HTML or CSS file while
the game runs. Build221 passed the host suite (53 entries), the sample on the
patched editor, native exports and desktop timing
([qualification221.json](verification/qualification221.json)); build220,
which differs only by the keyboard, passed every profile on this machine:
core and sanitizer suites, the sample on the official stock 4.7.1 editor,
1080p and 4K timing, and the ten-minute lifecycle soak; details and receipts
follow. With the official 4.7.1 editor and its official export templates, a
fresh consumer project imports, passes the sample headless and rendered,
exports, and the exported game passes the same checks relocated
([qualification221.json](verification/qualification221.json)), so neither the
patched editor nor its templates are needed for a Weva game. A combined
Windows and Linux addon zip built from this source (`alpha_artifact` in the
same receipt) installs on stock Linux Godot and passes the packaged example
and pack-export smokes there. Still open:
lower-end hardware, a physical controller, physical IME and touch, macOS,
and the residual layout differences listed under browser layout behavior.
The paragraphs below are in reverse order of arrival; earlier candidates'
failures are preserved as recorded.

Candidate217 (superseded by build220 below) avoided temporary list vectors during scalar interpolation. A
focused allocation test falls from 18,000 to 6,000 allocations across 3,000 width,
number and percentage samples with unchanged results. All 14 core suites, 49
native host entries (35,546 checks), 16 sanitizer suites, both renderer geometry
comparisons and sample/exports pass. Desktop timing passes 72/72. Automatic
1080p 3D passes 268/276: eight OpenGL whole-frame limits fail at 16.703–17.072 ms
p95, against 16.667 ms. All UI API/core CPU limits pass. The run stopped before
4K. The ten-minute lifecycle check below passes; it was not installed. The allocation
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

Both local addons now use build220 (ABI minor25): the preview219 changes plus
`WevaView` live reload. On the same machine with the game closed, the desktop
timing profile passes 72/72 (settings typing 0.39-0.56 ms changed-API p95),
and the 52-entry host suite (35,610 checks), the rendered sample, the
fresh-consumer native export and the packaged export smokes pass on the
packaged library; the installed copies then pass the sample headless and
rendered and the host suite in place. The automatic 1080p and 4K 3D profiles
then pass 276/276 each. On the identical busy scene (1,126 OpenGL draws,
115,490 primitives, vsync off, same adapter) whole-frame p95 is 0.7-1.8 ms
with the UI disabled and 1-4 ms with it, where every earlier session measured
8-17 ms; Overwolf, the NVIDIA container and Steam were running as before, so
the earlier waits' cause stays unidentified, but they were never UI cost.
Build220's ten-minute Vulkan 1080p 3D lifecycle check passes 200 recreations
and 688,364 soak frames with 115,168 mixed-script name updates (2.3 times the
preview217 run): cold construction 85.3 ms CPU, prepared reuse with hidden data
updates 0.827 ms CPU p95, soak frame p95 2.08 ms and maximum 23.4 ms. Private
memory minute medians rise from 821.7 to 823.3 MiB and hold from minute six;
teardown returns to 491 nodes and 1,930 objects after recreation and 1,937
after the soak, the bounded pattern classified above. On the official stock
Windows 4.7.1 editor the installed build passes the full host suite (52
entries, 35,610 checks) and the sample headless and rendered, so the patched
editor is no longer needed to run or test document UI; it remains the choice
for text in native Godot controls, and exports still use its templates (stock
templates are not installed here). The Linux `check.sh` gate on this source
passes everything except the three live layout-oracle corpora, whose output is
identical at the handoff commit, and the two text-safety probes that document
the stock engine defect. Lower-end hardware and a physical controller remain
unrun for this build.
[Build220 qualification](verification/qualification220.json),
[lifecycle evidence](verification/lifecycle220.json).

The earlier preview219 candidate (shaping pieces, `@font-face`
with real variants, gamepad navigation) was qualified as far as that session's
machine allowed: 14 core and 16 sanitizer suites, 51 host entries (35,599
checks), the sample headless and rendered (101 checks each), the fresh-consumer
project with native export and relocation, and the packaged example's export
smokes all pass. The desktop timing profile fails: in the third run of each
renderer settings typing reaches 1.021/1.122 ms changed-API p95 against 1.0 ms.
A paired old/new/new/old comparison in the same session shows the preview217
export itself 30-40% slower than its own recorded profile, with new against old
mixed and inside that spread, while the machine sat at 65% CPU load with a game
and other applications running. The gate stands failed, the candidate is not
installed, and the profile must be rerun on a quiet machine; 1080p/4K, the
lifecycle soak and lower-end hardware were not run.
[Qualification receipt](verification/qualification219.json).

`WevaView` now reloads its HTML and CSS files when they change on disk while
the game runs (debug builds by default), reapplying a stylesheet edit without
touching the document or its bindings and reloading markup with the bound data
re-applied, after the file's modified time has been stable for one poll.
Eleven host checks edit files under `user://` and verify both paths, binding
survival and the off switch. [Live reload evidence](verification/live-reload.json).

Godot text is now positioned at quarter pixels on every font the adapter
owns, with a private copy of the theme font and `@font-face` files so the
game's resources keep their own mode. Measured with the sample font against
Chrome, the automatic mode was up to 0.95 px off per string above 20 px and
quarter-pixel positioning is within 0.09 px at every size. The Frontier Camp
parity harness now also measures the 25 px and 32 px headings: their 22 width
findings disappear and nothing else moves; 33 vertical findings on the same
headings remained. Those turned out to be one thing, not Blink's metrics-table
choice: a synthesized bold face measured through FreeType's ceiled pixel
metrics (35 + 10 at 32 px) while the regular face used the design-rounded
extents Blink uses (34 + 9). The derived face now carries the base's bytes
and rounds the same way; the harness reports 0 geometry findings
over 1408 checks after the fix. Adapter suites pass on Windows
(19,699) and stock Linux. [Evidence](verification/textserver-subpixel.json).

A controller can now type: a pad accept on a focused text field emits
`text_entry_requested` and `WevaView` opens an on-screen keyboard along the
bottom of the view, an HTML document of its own that the pad navigates and
whose keys type into the field through the ordinary text path, the field
keeping its focus and caret (`retain_html_focus`). Shift, a symbol page,
Space, Back, Enter and Done behave as on a phone keyboard; cancel closes it.
Games can restyle it, size it, or turn it off and answer the signal with a
platform keyboard. Twenty-four host checks type through it with pad events;
the sample's controller case types into the bound name field and captures
the keyboard. [Keyboard evidence](verification/onscreen-keyboard.json).

A controller now drives the document without scripting. Joypad button and
axis events reaching the focused node answer as the keyboard their `ui_*`
actions stand in for: the pad and stick move focus by geometry (a slider,
radio group or caret takes left/right first; a `<select>`, `<textarea>` or
number field takes up/down), accept is Space on a control and Enter in a
field, cancel is Escape and is consumed only when something closed. Actions
without a joypad binding fall back to A, B, the D-pad and the shoulders.
A held direction repeats after 400 ms at ten steps a second, and with the
opt-in `gamepad_wake` the first press on a screen nobody focused wakes the
document on its first control without also moving (off by default so a
permanent HUD cannot take the movement stick). The core adds `weva_element_tag_name` for this.
Thirty-three host checks push real joypad events through a viewport,
including the repeat timing; physical controller acceptance remains open. [Gamepad evidence](verification/gamepad-navigation.json).

Stylesheets can now declare fonts with `@font-face { font-family; src: url() }`.
The core lists the rules (ABI minor 25, `weva_document_font_faces`) and the
Godot host loads each source, resolved against the base path like an image,
and registers the family: the normal weight/style rule as the regular face,
bold, black and italic rules as real faces that text at that weight or slant
draws instead of synthesis, with the nearest file serving what no rule covers
and one axis synthesized over the other's file. A game's own
`register_font_family`/`register_font_face` is never replaced. A source that
cannot load warns and falls back. Nineteen host checks cover loading,
base-path resolution, a missing source, precedence, face choice, real variants,
nearest-weight matching, the native face API and removal on CSS replacement;
the ABI test covers parsing, resolution and media gating. `unicode-range`,
`font-display` and `local()` remain unimplemented.

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
patch. The same source built for Linux passes the adapter suite (19,826
checks), the autoscroll fixture and the font-face, diagnostics and gamepad
scenes headless on the official stock Linux 4.7.2 editor, which carries the
same engine defect. [Shaping evidence](verification/stock-engine-shaping.json).

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
| Installable addon | Current Windows package and fresh consumer pass, and a combined Windows and Linux preview221 zip installs on stock Linux Godot and passes the packaged example and pack-export smokes ([qualification221.json](verification/qualification221.json), `alpha_artifact`). Native Linux exports need Linux templates, not installed here. |
| Native desktop exports | Patched Windows debug/release/embedded exports pass, and with build221 the official 4.7.1 editor and official export templates also produce a passing export of the fresh consumer project (104 headless and 114 rendered checks in the relocated game). Other platforms and native-control text remain unverified. |
| Replaced images in flex layouts | Current intrinsic-size suite and rendered example pass. Broader layout agreement is assessed separately below. |
| Native GUI integration | Current input suite covers Control focus, native overlays, document visibility and routed input. Joypad events now drive HTML focus, activation and dismissal through the project's ui_* actions with conventional button fallbacks (gamepad_navigation_tests: 33 checks with real joypad button and axis events, including wake on first press and held-direction repeat). Physical touch/gamepad acceptance is still needed. |
| Text editing and popup lifecycle | Current dialog suite: 1,199 checks; popover beforetoggle suite: 1,389 checks, including opening vetoes, ordered closing, mutation and queue-pressure cases. Form method=dialog is covered. Controller text entry goes through `WevaView`'s on-screen keyboard (24 checks). Full browser task timing, remaining dialog lifecycle and physical input-method acceptance remain open. [Popover evidence](verification/popover-beforetoggle.json). |
| Keyboard form actions | Current keyboard suite passes. Shared activation exists; complete form validation, picker behavior and command lifecycle are not thereby verified. |
| Two-way input bindings | Current binding, form-state and sample checks pass, including boolean and Unicode changes. |
| IME composition | Current simulated composition checks pass on the patched engine. Physical Windows IME and broader input-method acceptance are missing. Historical Linux diagnostic patches are separate. |
| Long fields and Unicode editing | Current editing/autoscroll/maxlength/typeahead checks pass. Bidi caret behavior and broader language tailoring remain open. |
| Native font positioning and themes | Current headless theme/font/inheritance and native family registration paths pass. The detailed theme/font pixel qualification was on preview105 and remains historical. CSS `@font-face` now loads and registers families on the Godot host, with bold, black and italic rules as real faces and nearest-file matching (font_face_tests: 19 checks covering loading, base-path resolution, missing source, precedence, face choice, real variants, native `register_font_face`, removal); `unicode-range`, `font-display`, `local()` and broader text layout remain open; unsupported at-rules produce diagnostics. |
| Viewport-relative font-size cache | Current font-size/line-height/inheritance suites pass. This does not establish every CSS text-layout mode. |
| Text length limits and paste | Current maxlength insertion, user-edit-aware length validity and submission checks pass. Public validity APIs are implemented; Unicode-v patterns and broader physical editing qualification remain open. |
| Native IME comparison | Historical physical Linux reproduction and private engine/input-method fixes are documented in IME.md. They do not certify the shipped Windows configuration or stock Linux. |
| Select interaction | Compact author widths and max-width override the themed 218px default. Closed controls also size automatically to option labels, including group indentation and spacing: 120 shared-font Chrome cases pass. All 14 core suites, 16 sanitizer gates, 39 host entries, sample/export checks and 15 post-install smoke suites pass. Broader language/picker behavior and exact native appearance remain partial. [Automatic sizing evidence](verification/select-intrinsic.json), [compact width evidence](verification/select-width.json). |
| Modal, inert and skipped panels | Current form-state/dialog tests cover focus, modal scope, inertness, hidden-content editing, cancellation, return values, Escape and form method=dialog. Popover beforetoggle is verified separately; broader dialog events and browser task timing remain open. |
| Ordinary runtime cost | The runner now accepts an explicit timing profile and reports functional/timing outcomes separately. Provisional development-desktop CPU targets require three runs per renderer and reject every overrun; legacy reports without a profile are behavioral evidence only. Current candidate budget evidence is tracked in PERFORMANCE.md. API CPU is not total UI/GPU cost or proof for other hardware. |
| Cold opening, reuse, busy 3D and memory | Current desktop and 1080p timing pass. Build205 historical lifecycle: 200 recreations, 600 seconds, 100.158 ms cold CPU and 0.384 ms prepared reuse CPU p95; private-memory minute medians rose 822.824 to 824.008 MiB. Its 4K timing failed 19/276 checks. Current 4K passes 276/276; current lifecycle and longer/device qualification remain open. [Lifecycle](verification/lifecycle205.json), [4K](verification/load205-4k.json). |
| Experimental retained renderer | Remains disabled by default. Existing pixel/material comparisons cover a subset; pre-draw synchronization, custom materials, GPU cost and broader lifetime qualification are still open. See [history](PRODUCT_READINESS_HISTORY.md#experimental-retained-renderer). |
| Hardware/platform acceptance | Lower-end hardware, physical touch/gamepad/IME and accessibility remain incomplete. Native exports must be verified for each platform actually declared supported. |
| Browser layout behavior | The live `check.sh` layout oracle reports 20 of 35 sample pages differing with the build220 core, identically with the handoff commit 28bb9fe8 and with freshly rebuilt C# references, so this session changed no layout. The local Chrome captures dated from before the form-control UA changes in 28bb9fe8; re-capturing with the current sheet leaves 14 differing pages, all sub-pixel text widths or 1-4 px inline `code`/`kbd` offsets where Chrome agrees with neither engine and the port is usually the closer, while every form-control page now has Chrome siding with the port. With refreshed captures the hand corpus is 43/52 agreeing with one difference (the reference lays out `content-visibility: hidden` children; the port and Chrome do not) and the harvest corpus 178/210 with four (empty inline-block buttons 0.6 px below Chrome's integer-rounded baseline, where the reference is 1.6 px off and 2 px short), one over the development allowance of three ([qualification220.json](verification/qualification220.json), `linux_check_sh`). Build201 core against the frozen 309-case corpus: 285 agree, 24 differ, zero crashes. Direct spanning headings are corrected; paragraph fragmentation and vertical writing still produce large differences. Three select fixtures use outdated UA defaults in their frozen references; separate refreshed captures explain those width differences without changing the frozen report. This synthetic geometry profile does not establish native pixel parity. See [layout audit](LAYOUT_AUDIT.md) and [current evidence](verification/column-span.json); no finding is waived. |
| Remaining game form integration | CSS horizontal RTL and vertical-rl/vertical-lr range direction/orientation are now verified. Number arrow stepping is covered by 76 Chrome cases and 234 native checks. Sideways writing modes, picker values/controls and full validation remain partial or absent. Review implementations against concrete game requirements and browser probes before claiming completion. |
| Release verification | Build, package, compatibility declarations, docs and every required gate must refer to the same candidate. Current broad layout findings and external acceptance prevent an unconditional release-ready claim. |

## Shared-core port list, 2026-09-11

Thirteen items of the Phase 3 port list (PORT_PLAN.md, "Phase 3 -- feature
inventory") landed in the core with tests, each committed on its own:
`hue-rotate()`, `@supports selector()`, `caret-color`, `light-dark()` with a
colour-scheme switch (ABI minor 28), 3D transform projection, `::placeholder` /
`::selection`, `text-align: justify` with `text-align-last` and `text-justify`,
the Lab/OKLab/`color()` functions and modern colour syntax with `color-mix()`
in every space, `width: max-content` and friends, `position: sticky`,
`@import`, CSS scroll snap and soft hyphens. Evidence:
`docs/verification/unity-host-prototype.json` (`phase_3.step_1_port_list_progress`).
Gates after the last of them: core suite 505,596 checks / 0 failures, Unity Native
EditMode 82 pass (2 inconclusive by design), all 31 Godot host scenes green on the
rebuilt Linux host, the samples oracle unchanged from a pre-session build (its one
differing value predates the work) and hand at its known 43/52. Not changed by
this: the readiness requirements above, the whole-frame timing gates, and the
items the port list still holds (subgrid, rtl, View Transitions,
component-scoped stylesheets, `background-attachment`).

Later the same day: `@scope`, `mix-blend-mode` (ABI minor 33, per-draw, both
hosts render what a blend state can), `background-blend-mode` and `mask-image`
in the rasterizer, `list-style-image`; and the tooling surface grew by ABI
minors 29-32 (the CSS cursor under the pointer, engine counters, the
inspector's hit test, the box tree as a list, an identity-preserving hot
reload). Gates after the last of them: core 505,716 / 0, Unity Native EditMode
89 pass / 2 inconclusive, Godot hover/host/range scenes green on both
platforms, the full 31-scene Linux run green on the installed library.

And after that: HTML parse diagnostics with positions (ABI minor 34), change
notification per element with a structure version (minor 35), and
`background-attachment` (fixed against the viewport, local with scrolled
content). The step-1 port list now holds only subgrid, rtl, View Transitions,
component-scoped stylesheets and the text-stroke / font-variation pair (no
consumer in the C# either); the tooling list only source positions. Gates:
core 505,746 / 0 (gcc and ASan), Unity Native EditMode 91 pass / 2
inconclusive, Godot hover (26) / host (247) / range (66) / bindings (186) on
Windows, the check.sh scene list on the installed Linux library, the sample
oracle byte-for-byte the pre-session baseline's summary.

Then: component-scoped stylesheets from the markup (a `<style>` inside
`<template id>` styles that component's rendering and `:host`, never the
slotted light-dom; the selector rewrite is byte-identical to the C#'s) and,
found on the way, `<style>` blocks themselves -- the core had read none, every
host passed CSS through the ABI. Subgrid turned out to be a documentation
error, not a divergence. Gates after: core 505,796 / 0, Unity Native 91 pass /
2 inconclusive, the 45 check.sh scenes on Linux and five scenes on Windows all
0 failures, sample oracle unchanged.

After that, four more from the list: the individual `translate` / `rotate` /
`scale` properties, the intrinsic sizing keywords as `min-width` /
`max-width` / `flex-basis` (core ahead of the C#, which maps them to auto),
`scroll-behavior: smooth` for programmatic scrolls, and snapping after a
thumb drag. Gates after each: core 505,850 / 0 at the last, Unity Native 91
pass / 2 inconclusive, the 45 check.sh scenes on Linux and six on Windows all
0 failures, both oracles unchanged. What the list still holds is the large
or host-bound remainder: bidi reordering, View Transitions, font variation
axes and text stroke.

From the "expose through the ABI" list: safe-area insets (ABI minor 36). A
page pads with `env(safe-area-inset-*)` as it would in a browser; the Godot
node can follow the display's safe area and the Unity component the screen's.
Gates: core 505,861 / 0, Unity Native 95 cases, Godot scenes green on both
platforms on the rebuilt libraries.

And `local()` font sources (ABI minor 37): a `@font-face` `src` list is tried
in the author's order on both hosts, a `local("Name")` being an installed
font. Gates: core 505,861 / 0, 97 cases: 95 pass, Godot font-face and host
scenes green on both platforms.

Bidirectional text, the last large item on the port list that the core can own:
`direction: rtl` and `unicode-bidi` now place a line's runs in visual order (UAX
#9 through ICU, which the core's ICU build already carried), each run one
direction for the host to shape. Verified through TextServer with Hebrew spans
on both platforms and through the C# wrapper; what remains is the caret in mixed
runs, TextCore's glyph order inside a run, and a visual pass on real Arabic.
Gates: core 505,878 / 0, Unity Native 100 cases, the check.sh scenes green on
both platforms, the three oracles identical to the pre-session baseline.

Looking at the core in Unity, not just testing it: `Assets/UI/native-check.html`
exercises the week's work one block each, and `NativeGameViewCaptureTests`
draws it through the project's URP renderer in headless Play mode. That found
the Phase 2 caveat was a real gap: the active `UIBatchedRendererFeature` never
drew native documents (only the inactive legacy feature did), so a
`WevaNativeDocument` (now `WevaDocument`) in a scene rendered nothing. Fixed; the in-pass capture now
matches the offscreen render, safe-area insets pad live, and a smooth scroll
settles on its target. Hebrew glyphs are absent on Unity until a covering
fallback face is given (the bundled faces have none); the run order is right.

## Next work

1. The budget evaluator now annotates a whole-frame failure whose UI-disabled
   baseline already reaches 85% of the limit as presentation/scene cost and
   supports `ui_whole_frame_delta` limits (see RUNTIME_PERFORMANCE.md);
   decide whether the 3D budgets should carry such a limit next to the
   absolute one.
2. Diagnose the intermittent whole-frame stalls and verify the eventual release
   candidate at target resolutions and on lower-end hardware.
3. Resolve remaining game-relevant CSS/text/form behavior: animation timing and
   composition edge cases, `unicode-range` font sources (`local()` landed
   2026-09-11, ABI minor 37: a `src` list is tried in order on both hosts and
   a `local("Name")` is an installed font; multicolumn fragmentation landed
   2026-09-14, balanced the way Blink balances; vertical writing landed
   2026-09-14: `vertical-rl` / `vertical-lr` as orthogonal flows with rotated
   text on both hosts, `sideways-*` and upright CJK remain), bidi caret behavior (run reordering landed
   2026-09-11: `direction` and `unicode-bidi` place a line's runs in visual
   order on both hosts; the caret in mixed runs and TextCore's glyph order
   inside a run remain) and dialog/input lifecycle.
4. Verify physical IME, touch/gamepad and accessibility, platform exports and
   longer memory/lifecycle behavior. Stock-engine Unicode safety is covered for
   document text on Windows and Linux; stock exports and native-control text
   remain open.
5. Keep release artifacts and all required evidence tied to the same candidate.
   Historical passes do not clear a current failure or missing requirement.
