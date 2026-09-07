# Text editing

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

Stock Godot 4.7.2 has a separate long-emoji shaping defect that can corrupt
positions or crash. See [GODOT_TEXT_SHAPING.md](GODOT_TEXT_SHAPING.md). Ordinary
integration coverage stays below its trigger; a separate stress mode retains
the failure and passes with the candidate engine patch.

## Segmentation and deletion

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

`test_grapheme.cpp` runs the Unicode fixture. `test_c_abi_text_editing.cpp`
covers browser deletion examples, undo/redo, long values, resizing, focus and
reload, spaces, password source mapping, transformed clipping and agreement
between painted textarea carets and their reported rectangles.
`text_editing_tests.tscn` runs 86 Godot GUI checks using both font backends;
`check.sh` includes it. The browser counterparts run from the repository root:

```powershell
node godot-port/tools/oracle/check_text_editing_chrome.cjs "C:/Program Files/Google/Chrome/Application/chrome.exe"
node godot-port/tools/oracle/check_maxlength_chrome.cjs "C:/Program Files/Google/Chrome/Application/chrome.exe"
```

The editing oracle passes 91 checks; the maxlength oracle passes 150.
`test_c_abi_maxlength.cpp` covers limits, Unicode, replacement, undo, paste,
Enter, composition and live attribute changes. `maxlength_tests.tscn` adds
44 Godot checks for GUI consumption, bindings, signals and both control types.
Headless runs use the explicit paste API; a separate Linux X11 run verifies
the real clipboard shortcut. The complete Release and ASan/UBSan mutation
suites pass 582,689 checks after this change.

These cases do not establish complete browser
editing. Bidi caret placement, visual up/down navigation across wrapped text,
transformed pointer hit testing, selection in ordinary document text,
remaining form constraints and broader native IME
compatibility remain work. The current hit search assumes LTR text with ordered
advances; it is not a bidi implementation.

Unicode data and conformance fixtures use Unicode License V3; source hashes
and regeneration instructions are in `third_party/unicode/README.md` in the
source tree. The addon includes `UNICODE_LICENSE.txt` and `UNICODE_DATA.md`.
