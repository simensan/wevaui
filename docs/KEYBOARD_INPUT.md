# Keyboard input in the Godot host

(The Unity host applies the same rules; [INPUT_PARITY.md](INPUT_PARITY.md)
tables every shared input decision with the test that pins it on each host.)

Godot routes GUI events to the focused `WevaDocument` Control. Native HTML
actions run in the core and produce the same click, input, change and submit
events as pointer actions. Keyboard clicks have zero coordinates and do not
synthesize pointer-down/up events. Hosts sending keys explicitly must send
both key edges and avoid inserting text for a consumed key.

| Control | Implemented behavior |
|---|---|
| Button / input button | Enter clicks on key-down and repeats while held. Space clicks once on release. Moving focus away cancels a pending Space. |
| Link with `href` | Enter clicks once per press. Space does not activate it. |
| Checkbox | Space toggles on release, then emits input and change. Enter can activate its form's default submit button. |
| Radio | Space selects. Arrows select the next enabled member, wrapping within the same named group and form owner. Unnamed radios are independent. Tab visits one member per group, preferring its checked or last visited member. |
| Horizontal range | Arrows step; Home/End select the endpoints; PageUp/PageDown move by a page. Values clamp and align to the permitted step. `step=any` uses one percent of the range for arrows. Painting, reads and edits share value normalization. |
| Number input | Up/Down steps with min/max and step constraints; disabled/readonly fields reject editing. |
| Select listbox | Arrows, Home/End and PageUp/PageDown navigate enabled rows. Shift extends a multiple-selection range. Ctrl/Meta+arrows move the visible keyboard row; Ctrl/Meta+Space toggles it. Ctrl+A selects enabled options. Typing searches labels; Space continues an active prefix. Enter and otherwise plain Space preserve selection. |
| Select dropdown | Enter/Space opens, Up/Down skip disabled options, Enter/Space commits the highlighted option and Escape cancels. Typing searches labels, with Space continuing an active prefix. `size=1` uses this behavior. |
| First summary in details | Enter or Space toggles the details element. It participates in native HTML tab order. Activating a nested button does not toggle the details. |
| Popover trigger | Keyboard clicks use its `popovertarget` action. Escape dismisses an auto popover. |
| Single-line field in a form | Enter clicks the first submit button, then emits submit. A disabled default button blocks this. With no submit button, more than one blocking field prevents implicit submission. Explicit `form=id` ownership is supported. |

`role=button` alone does not add native HTML keyboard behavior. Authors should
use `<button>` for native activation. The event queue remains the host API;
this is not a JavaScript DOM event dispatcher with `preventDefault`.

The action timing follows [HTML activation](https://html.spec.whatwg.org/multipage/interaction.html#activation-behavior)
and [implicit submission](https://html.spec.whatwg.org/multipage/form-control-infrastructure.html#implicit-submission),
with Chrome 151.0.7922.174 measurements taken on 2026-09-06. The standalone
browser check runs 134 assertions and is reproducible from the repository root:

```powershell
node Tools/oracle/check_keyboard_chrome.cjs "C:/Program Files/Google/Chrome/Application/chrome.exe"
```

`test_c_abi_keyboard.cpp` verifies event timing/order, cancellation, radio
ownership and Tab navigation, and 40 range-key cases against captured Chrome
values. `keyboard_integration_tests.tscn` exercises actual Godot GUI events,
including controller handlers and two-way bindings. `check.sh` runs it.

Known remaining work includes picker controls, complete form constraints,
broader [IME compatibility](IME.md), physical-device acceptance and accessibility.
Horizontal RTL and vertical-rl/lr range direction are covered; sideways writing
modes remain incomplete.
These are release work; the implemented controls do not establish complete
browser form behavior.

Text fields support scrolling and Unicode-aware navigation, deletion and
password masking. See [TEXT_EDITING.md](TEXT_EDITING.md) for the exact profiles,
browser evidence and remaining editing limits.
