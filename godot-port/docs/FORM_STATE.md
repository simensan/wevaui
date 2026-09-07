# Form values and reset

Inputs, textarea and selects keep live state separately from markup defaults.
Typing, paste, IME and `set_element_value` change the live value. For input,
`value` is its reset default; for textarea, the child text is its reset default.
Checkbox/radio `checked` and option `selected` attributes also remain defaults.
Use `get_element_value` and `:checked` to inspect live state. Attribute
selectors such as `[checked]` continue to describe the markup.

```html
<form id="settings" on-reset="settings_restored">
  <input id="name" value="Ada" data-model="Player.Name">
  <textarea id="bio" data-model="Player.Bio">New player</textarea>
  <button type="reset">Restore defaults</button>
</form>
```

```gdscript
ui.reset_form("#settings")
ui.form_reset.connect(func(id): print("Restored ", id))
```

Reset uses the **current** defaults, including changes made after parsing.
It also reaches controls outside the form with a matching `form="id"` and
restores disabled/readonly controls. It clears dirty flags and edit history
for the owned controls, discards their IME preedit, and preserves focus.
A changed focused value puts the selection at the end; an unchanged value
keeps its selection. Unowned controls and their history are preserved.

The host updates writable Dictionary/Array `data-model` paths together before
calling `on-reset` and emitting `form_reset(id)`. No `value_changed` or
`value_committed` signals are synthesized by reset. An IME session receives a
composition-end notification so the host can close it. A read-only
`data_source` must handle model restoration in its reset handler.

The C ABI provides `weva_document_reset_form`, `weva_element_form` and
`WEVA_EVENT_RESET` (minor version 7). Events are queued notifications after
the operation; this API does not provide browser-style cancelable reset
listeners. A script setter alone does not cause a later blur to emit change.

## Defaults and programmatic writes

Before a control is edited, changing its markup default changes its live
value. After an edit or a `set_element_value` call, default changes wait until
reset. Assigning the same live value still sets its dirty flag. Updating
textarea markup during composition leaves the live preedit intact.

Programmatic text input values strip CR/LF; textarea normalizes CRLF and CR
to LF. URL/email inputs also trim surrounding ASCII whitespace. Number
inputs reject invalid numeric strings when set programmatically. Range
values share clamping/step alignment across reads, painting and edits; the
step base remains the markup's `min`/`value`, independent of the live value.
`maxlength` applies to user edits rather than script setters or defaults.

Single selects share selectedness across painting, reads and `:checked`.
Reset chooses the last default-selected option, or the first enabled option
for a dropdown when there is no selected default. A listbox with `size > 1`
can have no selection. Setting a missing select value clears its selection;
an explicit empty option value stays empty. Native radio groups use their
name and form owner while retaining each member's default checkedness.

For compatibility with the host's existing binding API, a checkbox/radio
value is `"on"` or `""`, and a multiple select value is comma-separated.
These are host convenience representations, not `HTMLInputElement.value`
or the browser's single `HTMLSelectElement.value` getter. Values containing
commas need a future structured multiple-selection API.

## Select interaction

A select is a listbox when `multiple` is present or its parsed `size` is
greater than one. `size="1"`, zero and invalid values use a dropdown for
single selection. HTML integer parsing accepts leading whitespace, `+`,
leading zeroes and a numeric prefix. Changing these attributes updates the
retained box tree even when selectedness and computed CSS remain unchanged.
The package's existing control dimensions still apply; set an explicit CSS
height for a listbox's visible rows.

Plain clicks replace selection; Ctrl/Meta clicks toggle a row. Shift clicks
and Shift+arrows extend a range from the anchor, skipping disabled options and
disabled optgroups. Dragging updates selectedness as the pointer crosses rows
and emits input/change once on release. Choosing the same selection again
emits neither event. Defaults remain in `selected` attributes.

Moving inside a held listbox arms vertical autoscroll. The viewport continues
scrolling while the pointer stays near or beyond its top/bottom edge, including
outside the Godot Control. A stationary pointer does not extend selection;
moving onto another visible option does. Scroll events report changed offsets,
and input/change commit the selection on release. Capture is cancelled when
the Control loses window focus, hides, leaves the tree or becomes noninteractive;
reset, removal and a disabled/hidden list also stop the gesture.

Autoscroll uses frame time independently of CSS animation time, so Godot's
`paused` property leaves it responsive. C ABI minor 10 adds
`weva_document_update_with_input_time(doc, animation_seconds, input_seconds)` and
`weva_document_needs_input_tick`; the original update supplies its elapsed time
to both clocks. Nonpositive/nonfinite input time is ignored. Scroll speed
increases with distance from the edge, capped at 1,600 CSS pixels/second and
100 ms of travel per frame. This speed profile is an engine choice; exact
native browser/platform timing is not claimed. Input/textarea selection
autoscroll has its own behavior, described in [TEXT_EDITING.md](TEXT_EDITING.md).
Drag-and-drop remains unimplemented.

`check_select_autoscroll_chrome.cjs` records 23 browser behavior checks.
Core tests cover independent clocks, reverse scrolling, cancellation, deferred
events, unchanged draw serials at rest, and retained/full rendering in nested
scroll containers. `select_autoscroll_tests.tscn` adds 33 native Godot checks.

Up/Down, Home/End and PageUp/PageDown navigate listboxes. Ctrl/Meta+arrows move
the visible keyboard row without changing selection; Ctrl/Meta+Space toggles
that row in a multiple select. Ctrl+A selects all enabled rows. Navigation
brings its row into view. Enter and plain Space leave a listbox unchanged
unless Space continues an active typeahead prefix.
Dropdown keyboard navigation also skips disabled rows.

Godot forwards native mouse modifiers. For explicit input use
`ui.set_pointer(point, buttons, modifiers)`; its third argument defaults to
zero. Modifier bits are Shift=1, Ctrl=2, Alt=4 and Meta=8. The C ABI adds
`weva_document_set_pointer_modifiers` in minor 8 while preserving the original
entry point. Pointer events include these modifiers.

`tools/oracle/check_select_chrome.cjs` runs 69 Chrome checks corresponding to
the core selection, event and display-mode regressions. Godot's
`select_control_tests.tscn` runs 63 native GUI/binding checks. Retained row
painting is compared with forced complete layout across focus movement,
scrolling and removal, and unchanged updates preserve the draw serial.

## Verification and limits

Typing in a focused select searches visible option labels. A one-second pause
or focus change starts a new prefix; repeating the same character cycles
matching choices. Spaces continue an active prefix, so `blue bird` can find
a multiword label. Ctrl/Alt/Meta chords and paste do not perform typeahead.
The timeout follows native input time even when document animations are paused.
Disabled choices and group headings cannot be selected by typing.

Closed dropdown choices emit input/change only when the selection changes.
Listbox typeahead follows Chrome's change-only events for each successful
match, including refinement on the same row. An open popup changes its
highlight and commits when Enter is pressed. An option's nonempty `label`
overrides its displayed/searched text while preserving DOM text and its
submitted value. Optgroups have separate headings; label mutations update
the retained layout and an already open popup.

Search uses embedded ICU 78.3 primary-strength English collation rather than
ASCII folding. Accents, canonical equivalents and complete expansions such
as `ae`/`Æ` are supported. The locale is currently fixed, independent of the
desktop language. Full Unicode scalar input also works, including emoji;
Chrome 151's native select search currently narrows characters to UTF-16
units and does not match astral input in the same way. Dropdown popup rows
use uniform heights; arbitrary per-option layout remains limited.
See [the ICU profile](../third_party/icu/README.md) for build and data details.

`check_typeahead_chrome.cjs` passes 78 matching browser behavior checks.
`test_typeahead.cpp` covers Unicode search, timeout, events, labels and
retained/full-render equivalence; `typeahead_tests.tscn` drives native Godot
keys, modifiers, bindings and a paused document. C ABI minor 9 adds
`weva_document_try_text_input_modifiers`; its original entry point uses zero
modifiers. Existing text-field input behavior is preserved.

`tools/oracle/check_form_state_chrome.cjs` records Chrome behavior;
`test_form_state.cpp` covers the DOM/C ABI and reset edit sessions.
The incremental form test compares every draw, texture and caret against a
forced complete layout across live/default edits, reset and subtree reflow.
`form_state_tests.tscn` tests native activation, signals, binding refresh and
IME. The packaged example uses a real reset button and tests it after export.

The implementation remains a form subset. Constraint validation, submission
serialization, file/date/color pickers, complete number editing/stepping,
output reset behavior and accessibility remain work.
Control sizing and line metrics retain their documented browser differences.
The behavioral reference is the [HTML forms standard](https://html.spec.whatwg.org/multipage/forms.html)
and repeatable probes against Chrome 151.0.7922.174.
