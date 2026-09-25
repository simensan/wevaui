# Text editing

Text fields support caret movement, selection, undo/redo, paste and Unicode
editing. This page describes the shared behavior and Godot integration;
physical-device and language limits remain as documented in the relevant sections.
Dated preview results below are historical evidence, not current test totals.

Single-line inputs keep the insertion caret visible as their text grows,
selection moves or width changes. Painting, selection, click placement and the
IME candidate anchor share the text scroll offset. Shortening the value clamps
the offset; blur, removal and document reload discard it. Caret positions use
font advances, including trailing spaces, instead of glyph bitmap bounds.
Text overlays clip with the input's CSS transform.

Password inputs display one bullet per Unicode extended grapheme cluster.
Accented letters, surrogate-pair emoji, flags and joined family emoji each
produce one bullet. Selection and click positions map back to the original
UTF-8 value; the display string is never used as the editable value.

## Continuous selection scrolling

Dragging inside a text input, password input or textarea arms selection
autoscroll. Holding near or beyond an edge keeps exposing text and extends
the selection without another mousemove. Single-line inputs scroll
horizontally; textareas scroll both axes and retain the initial source anchor,
including across wrapped lines. Readonly fields remain selectable.

Selection follows the existing Unicode caret boundaries and password display
mapping. Scrolling and releasing do not edit the value, emit input/change,
write a binding or add undo steps. A subsequent edit replaces the selected
source range. Releasing midway preserves the viewport instead of snapping it
to the selection endpoint. Focus loss, hiding, removal, reload, reset and
disabled controls cancel the gesture.

The C ABI's separate input clock drives this behavior. Godot measures that
clock with monotonic elapsed time: `paused` and `Engine.time_scale` can stop
CSS animations while pointer selection remains responsive. Normal scene-tree
processing rules still apply. `update_document(dt)` explicitly steps both
clocks for deterministic hosts/tests. The edge speed profile matches listbox
autoscroll's engine settings, capped at 1,600 CSS pixels/second and 100 ms of
travel per frame; exact browser/platform timing is not claimed.

`check_text_autoscroll_chrome.cjs` records 35 matching Chrome checks. Core tests
compare source boundaries, cancellation, event/undo behavior and retained/full
rendering through nested scrolling, font changes and resizing. The native
Godot suite passes 81 checks on Windows/Linux using both fonts, outside-Control
capture, source-range editing and stopped simulation time. A rendered run adds
a selection/clip capture check. The complete mutation corpus passes 588,811
checks in both Release and ASan/UBSan.

Stock Godot 4.7 has a separate long-emoji shaping defect that can corrupt
positions or crash. The adapter now shapes affected runs in pieces, verified on
the stock editor; see [GODOT_TEXT_SHAPING.md](GODOT_TEXT_SHAPING.md).

## Segmentation and deletion

Preview75 fixed `tab-size:0`, `0px` and `0em`: tabs have zero
display advance while remaining editable source characters. The resolver no
longer replaces zero with the default eight spaces or divides by a zero stop.
It replaces rounded space expansion with separate
tab fragments carrying exact advances. Carets, pointer placement, vertical
navigation and selection use that advance. The same `tab-size:25px` fixture now
places its following span at x=25, matching Chrome (previously x=27).
Numeric tab stops also use the containing block's font and spacing when an
inline span changes those properties. Text decorations span the exact advance.
Ten Release suites pass (`tab-block-tests.log`, 19.76 seconds), as do 8,659
native host checks, including 286 editing checks, and 169 Chrome comparisons. Evidence is under
`.utmp/safe-engine71/`: `tab-exact-fractional.json`, `tab-size-probe.log` and
`tab-exact-host-checks/verification.json` and `tab-block-chrome2.log`.
All 12 sanitizer gates and debug/release/embedded export checks pass. The
survival consumer passes integration and all 12 performance workloads on both
OpenGL and Vulkan, with nine captures unchanged from preview74. Installed
native editing passes 286 checks without warnings. Both projects and their
manifests use preview75, library SHA-256
`57f07e16d8f56b8bb8df5263f3c1cf8ea9dc6688203b73ebb84a6c34309730b9`.

The preceding Windows preview74 added Chrome's textarea defaults:
`overflow-wrap:break-word`, with case-insensitive `wrap="off"` selecting
`white-space:pre;overflow-wrap:normal`. Live attribute changes and author CSS
overrides are covered alongside the styled-text mapping fix below.
Both projects have matching runtime/build manifests; the library SHA-256 is
`5a65b3165631e8f7332342294589e6b9c07d00d0390129981c0ad063472ebc00`.
The installed-copy smoke test passes 238 checks with no UTF-8 warnings.
The first preview73 smoke run exposed UTF-8 warnings despite passing assertions:
tab expansion in `pre` measured each byte of a multibyte character separately.
Preview74 measures complete UTF-8 spans. The editing gate now rejects
`Unicode parsing error` as well as ordinary errors; the retained preview73 log
is a negative control. The corrected native suite passes with a clean log.

The source follow-up also fixes byte-preserving styled-text mapping. A native
probe previously showed `text-transform:uppercase` on `abc\ndef` reporting the
caret at x=0 for source offset 1 and leaving Down at offset 1. Layout fragments
now carry explicit source offsets through pool reuse and subtree import. Caret,
click, selection and composition placement use those offsets rather than
assuming that display text points into the value's original buffer.
The complete mapping batch passes 238 native editing checks and 8,611 host
checks overall; Chrome passes 148 editing comparisons. The core tests compare
styled caret/selection/composition painting against literal displayed text
through resizing; all ten Release suites pass.
Ordinary and byte-preserving styled runs use a scalar offset with no mapping
allocation. Expanded tab runs own a source-to-display boundary map, shared
through scratch-tree import and released by box reset. Clicks and vertical
navigation search actual source grapheme boundaries rather than inserted spaces.
Trailing and consecutive tabs, both `pre` and `pre-wrap`, selection painting and
value replacement have regression coverage. The reproduced trailing-tab caret
now reports x=42.83 for both `a\t` and the position before `b` in `a\tb`; it
previously fell back to x=11.61 at the end of `a`.

Preserved trailing whitespace remains in the fragment tree for editing.
Alignment handles hanging whitespace separately, following
[CSS Text's line-end rules](https://www.w3.org/TR/css-text-3/#white-space-phase-2).
Right-aligned final and forced lines have core comparisons. Debug, release and
embedded exports pass; the survival sample passes 92 headless / 98 rendered
checks, and all nine sample captures match preview72 across GL/Vulkan/export.
No complete Unicode case conversion, exact tab-stop geometry or bidi claim
follows from these mapping checks.
All twelve final-source ASan/UBSan gates pass. All twelve normal sample
workloads pass on OpenGL/Vulkan in a release export; the performance report
records the timing distributions and limits. Evidence is retained under
`.utmp/safe-engine71/`: `tab-utf8-tests.log`, `tab-utf8-asan-tests.log`,
`tab-utf8-host-checks/verification.json`, `tab-map-chrome.log`,
`tab-utf8-exports.log`, `frontier-tab-utf8/verification.json` and
`tab-utf8-perf/summary.json`.

The keyboard follow-up makes unmodified Left/Right collapse an existing LTR
selection to its start/end, regardless of selection direction. Up/Down now use
the displayed textarea lines and measured horizontal position. They retain that
position across shorter and empty lines; horizontal movement, Home/End,
programmatic selection and pointer selection reset it. Shift preserves the
selection anchor. Home/End target the current displayed line; Ctrl+Home/End
target the value boundaries. Shared soft-wrap offsets retain their line affinity.

This fixes the reproduced `abc\n😀x` failure: Down from byte offset 2 formerly
landed inside the emoji at byte offset 6 and produced Godot UTF-8 errors.
Preserved whitespace wrapping now honors `word-break:break-all` and
`overflow-wrap:break-word`; word slicing keeps grapheme clusters intact even
when a cluster is wider than the field.
Textarea pointer placement chooses the nearest horizontal fragment within the
target line, so clicking later words or an empty line reaches that source
position instead of stopping at the first word.

Chrome 152 passes 126 editing comparisons. The private native candidate passes
164 editing checks and 8,537 host checks overall; ten Release core suites pass.
All twelve ASan/UBSan gates pass on the final source, including the mutation
corpus and both instrumentation controls. Debug, release and embedded-pack
exports pass relocation/startup and pixel checks. Frontier Camp passes 92
headless and 98 rendered integration checks; nine screenshots across OpenGL,
Vulkan and release export exactly match preview71.
The prior Windows preview72 build has library SHA-256
`06417570ca0f0ba33eeafd0bce089e24b454d0cb0fb6aa52a8ddaa0896c33166`.
The same patched Godot editor/templates used for preview71 remain required for
the previously documented Unicode engine safety issue. Evidence is retained
under `.utmp/safe-engine71/`: `fragment-tests.log`, `fragment-asan-tests.log`,
`fragment-chrome.log`, `vertical-host-checks/verification.json`,
`fragment-exports.log` and `frontier-fragment/verification.json`.

The allocation-free grapheme iterator implements Unicode 17.0 default extended
grapheme clusters and passes all 766 official conformance cases. Browser editing
uses two deliberate distinctions, checked against Chrome 151.0.7922.174 on
2026-09-06:

- Arrow movement and forward Delete use grapheme boundaries. Blink's caret
  profile omits UAX #29 rule GB9c, so an Indic conjunct such as `क्‍ष` has an
  extra caret stop before `ष`. Password masking includes GB9c.
- Backspace removes one code point for combining accents and decomposed Hangul
  jamo: `á` becomes `a`. Emoji presentation, modifier, flag, keycap, tag and
  valid ZWJ sequences are deleted together. This differs from forward Delete.

The C and Godot selection APIs use UTF-8 byte offsets and clamp offsets away
from the middle of a code point. Native editing supplies cluster boundaries;
an explicit API selection may still select part of a cluster. Composition
selection arguments in Godot use character positions, as described in IME.md.

## Length limits and paste

`maxlength` limits user insertion in text, search, password, email, URL and
telephone inputs, and in textarea. Number inputs ignore it. The count uses
UTF-16 units, as HTML does: `maxlength="2"` admits one `😀` or two `日`
characters. Truncation never splits a Unicode code point; a combining accent
can be trimmed independently. Selected text frees room for its replacement.
Zero rejects insertion, while an absent, negative or overflowing limit is
unlimited. Updating the attribute affects the next edit. Existing markup,
script values and undo snapshots are not truncated by the limit.

Preedit can exceed the limit. Commit or blur trims its insertion, and the
whole composition remains one undo step. Disabling the field retains its
current preedit. Composition signals carry the input method's original
result; `text_entered` carries the accepted insertion. An ordinary rejected
keystroke emits no value/text signal, adds no undo entry and stays consumed
so it cannot trigger game input.

Native Ctrl/Cmd+V and `ui.paste_text(text)` insert clipboard content as one
undo step, separate from adjacent typing. Paste accepts leading tabs and
newlines. CRLF and CR become LF in textarea; single-line inputs replace
interior line breaks with spaces and remove trailing line breaks before
counting. Textarea Enter replaces the selection only when the newline fits.
The C equivalent is `weva_document_paste_text`, added in ABI minor version 6.

This is input limiting, not a complete form-validation API. `minlength`,
validity state, validation messages and submission constraints remain work,
along with the remaining picker/number-editing behavior. Live/default values,
reset and supported programmatic sanitization are described in [FORM_STATE.md](FORM_STATE.md).

## Verification

Windows preview71 introduced pointer input through CSS transforms.
Translated/scaled text controls, rotated range dragging, transformed scrollbar
dragging and text selection autoscroll have core regression coverage. The
browser counterpart `check_transformed_input_chrome.cjs` passes 12 hit/caret
comparisons; the private Windows candidate passes 88 native text-editing checks
and 8,461 host checks overall. Dropdown overlays now anchor to the transformed
control bounds, with native opening and selection coverage. Ten core suites pass.
The preview71 candidate also passes a fresh Frontier Camp project and relocated
release export (92 headless / 98 rendered checks, matching project/export pixels).
All 12 current-source ASan/UBSan gates also pass, including both instrumentation
controls and the mutation corpus. Preview71 was installed in both projects, with
matching addon manifests and preview70 backups. The normal-workload comparison
passes with 72 identical capture pairs. Rendered clock p95 is higher in these
runs; headless paired clock p95 changes 0.236 → 0.243 ms. See the sample
performance report for the unresolved timing and isolated hover-assertion limits.

`test_grapheme.cpp` runs the Unicode fixture. `test_c_abi_text_editing.cpp`
covers browser deletion examples, undo/redo, long values, resizing, focus and
reload, spaces, password source mapping, transformed clipping and agreement
between painted textarea carets and their reported rectangles.
`text_editing_tests.tscn` runs 302 Godot GUI checks using both font backends;
`check.sh` includes it. The browser counterparts run from the repository root:

```powershell
node Tools/oracle/check_text_editing_chrome.cjs "C:/Program Files/Google/Chrome/Application/chrome.exe"
node Tools/oracle/check_maxlength_chrome.cjs "C:/Program Files/Google/Chrome/Application/chrome.exe"
```

The editing oracle passes 177 checks; the maxlength oracle passes 150.
`test_c_abi_maxlength.cpp` covers limits, Unicode, replacement, undo, paste,
Enter, composition and live attribute changes. `maxlength_tests.tscn` adds
44 Godot checks for GUI consumption, bindings, signals and both control types.
Headless runs use the explicit paste API; a separate Linux X11 run verifies
the real clipboard shortcut. The complete Release and ASan/UBSan mutation
suites pass 582,689 checks after this change.

These cases do not establish complete browser
editing. Bidi caret placement, full Unicode case conversion,
other platform-specific editing shortcuts,
selection in ordinary document text,
remaining form constraints and broader native IME
compatibility remain work. The current hit search assumes LTR text with ordered
advances; it is not a bidi implementation.

Unicode data and conformance fixtures use Unicode License V3; source hashes
and regeneration instructions are in `third_party/unicode/README.md` in the
source tree. The addon includes `UNICODE_LICENSE.txt` and `UNICODE_DATA.md`.

## Historical paragraph-navigation and storage checks

Preview77 added Windows Chrome-style Ctrl+Up/Down navigation:
move to the previous paragraph's last line or next paragraph's first line, retaining the
horizontal caret position, including wrapped and empty paragraphs. Ctrl+Shift
extends the selection from its original anchor. Ten Release suites, 177 Chrome
editing checks and 8,675 native checks pass (302 editing checks across both
font backends). Source evidence: `.utmp/safe-engine71/paragraph-tests.log`,
`paragraph-chrome.log` and `paragraph-host-checks/verification.json`.
All twelve sanitizer gates, packaged debug/release/embedded exports, the
survival consumer and all twelve normal performance workloads on OpenGL and
Vulkan pass. Nine survival captures match preview76; installed editing passes
302 checks without warnings. That build used library SHA-256
`97732f3c92f3f7c18493f1d7d8eae4e7c235b8b8c25ece65631bfc12edd7a182`.

The preceding preview76 removed the unused expanded-tab map from each
layout box. Current tab fragments and styled text use explicit byte-preserving
offsets, so the shared map and its dead search path are unnecessary. A Linux
x64 size probe measures `sizeof(Box)` falling from 608 to 592 bytes. Ten Release
suites, twelve sanitizer gates and 8,659 native checks pass; packaged exports
and the survival consumer also pass. Three paired performance runs pass with
72 identical captures; focused clock timings are nearly unchanged. Both
projects use library SHA-256
`ba31d5140fb34a03e08d7bb1d48cd06fa3d443957037b6a804ca8e011483f66a`.
The installed native editing gate passes 286 checks without warnings.
Evidence: `.utmp/safe-engine71/box-map-tests.log`,
`box-map-asan-tests.log`, `box-map-host-checks/verification.json`, and
`box-map-exports.log`.
