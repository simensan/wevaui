# Form values and reset

Bind editable controls with `data-model`; reset restores markup defaults.
The examples use the Godot `WevaView` helper.

## Authoring a settings panel

Add a `WevaView` child named `UI` to your Control scene and set its `html_file`
to the markup below. A same-name `.css` file loads automatically.

```html
<form id="settings">
  <label>Player name <input id="name" value="Ranger" data-model="Player.Name"></label>
  <label>Volume <input type="range" min="0" max="100" value="80"
    data-model="Settings.Volume"></label>
  <label><input type="checkbox" checked data-model="Settings.Music"> Music</label>
  <p>Health: {{ Player.Health }}</p>
  <button type="button" id="heal" on-click="heal">Heal</button>
  <button type="reset">Restore defaults</button>
</form>
```

Attach this script to the parent Control:

```gdscript
extends Control

signal state_changed
var model := {
    "Player": {"Name": "Ranger", "Health": 50},
    "Settings": {"Volume": 80, "Music": true}
}
@onready var ui: WevaView = $UI

func _ready() -> void:
    ui.bind_state(model, self, state_changed)
    ui.data_changed.connect(_on_data_changed)
    ui.form_reset.connect(_on_form_reset)
    _apply_audio()

func heal(_element_id: String) -> void:
    model.Player.Health = 100
    state_changed.emit()

func _on_data_changed(path: String, _text: String) -> void:
    if path == "Settings.Volume" or path == "Settings.Music":
        _apply_audio()

func _on_form_reset(form_id: String) -> void:
    if form_id == "settings":
        _apply_audio()

func _apply_audio() -> void:
    AudioServer.set_bus_volume_db(0, linear_to_db(maxf(0.001, model.Settings.Volume / 100.0)))
    AudioServer.set_bus_mute(0, not model.Settings.Music)
```

Game code changes the shared Dictionary and emits `state_changed`. Updates are
coalesced after the callback; idle frames do not poll the model. Call
`ui.flush_bindings()` only when you need the refreshed UI immediately, such as
before reading element geometry. Mutating a Dictionary does not emit a signal
by itself. Call `bind_state` again if you replace the entire Dictionary.

Player edits update the Dictionary before `data_changed(path, text)` runs.
Read the model for typed numbers and booleans; the signal's second argument is
text. Restrict native side effects to the paths they use: editing a player name
should not reapply audio settings.

Reset restores markup defaults (`value`, `checked`, `selected`, or textarea
content), not a saved game-state snapshot. It updates writable model paths
before `form_reset` runs. Handle that signal to reapply native settings; reset
does not emit normal input/change signals. The same operation is available as
`ui.reset_form("#settings")`.

Use `type="button"` for game actions inside forms. For conditional disabling,
use `disabled="{{ Player.Unavailable }}"`; literal `disabled="false"` still
disables the control under HTML rules. Use `data-model` for editable values,
not a templated `value` attribute. Authored classes can coexist with conditional
classes such as `data-class-low-health="Player.LowHealth"`.

For a working project with inventory rows, dialog focus and gameplay input,
see [Frontier Camp](../examples/frontier_camp/README.md). Its integration checks
cover click callbacks, typed model updates, name edits without audio updates,
and resetting settings. The detailed sections below preserve implementation
history and verification scope; old build numbers are not release guarantees.

## Popover transition handlers

Popover transition handlers require ABI minor 23 or newer.

Connect `WevaDocument.element_before_toggled(id, opening)` or use an
`on-beforetoggle="handler_name"` markup handler. Opening handlers run before
focus changes or replacement of existing menus. Call `prevent_default()` from
the handler to veto opening. Closing handlers see the still-open menu, but
closing cannot be vetoed. Both explicit methods and button activation use these
handlers, as do Escape, outside clicks and popover mode changes.

```gdscript
@onready var ui: WevaDocument = $UI
var inventory_locked := false

func _ready() -> void:
    ui.element_before_toggled.connect(_before_menu_toggle)

func _before_menu_toggle(id: String, opening: bool) -> void:
    if id == "inventory" and opening and inventory_locked:
        ui.prevent_default()
```

`show_popover()` returning `true` means the request was accepted; the handler
can still cancel it. Calls made inside another event handler are queued until
that handler returns. Browser task coalescing, nested synchronous dispatch and
synchronous DOM exception propagation are not provided by this event queue.
A mouse click outside an existing automatic menu dismisses it before attempting
the new opening, so vetoing the new opening does not undo that dismissal.

## Sizing a select

Set `width` directly for a compact control, or use `width:auto` to fit the
longest option label instead of a theme-supplied width:

```css
select.quality { width: auto; max-width: 100%; }
select.compact { width: 120px; }
```

Automatic closed-select width includes unselected, hidden and disabled options,
option `label` attributes, grouped-option indentation and text spacing. Changing
a label recomputes the width. `appearance:none` removes the arrow allowance;
normal minimum and maximum width constraints still apply. See the [automatic-sizing evidence](verification/select-intrinsic.json) for the
original verification scope.

<details>
<summary>Detailed control reference and historical verification</summary>

The sections below retain checkpoint-specific results and implementation notes.
Build numbers and installation claims describe those checkpoints, not the
current package. Use the [readiness summary](PRODUCT_READINESS.md) for current scope.

## Numeric arrow keys — installed development build

Up/down now step number inputs using decimal arithmetic, min/max and the step
base. Empty values, off-step values, readonly/disabled controls, `step=any`,
scientific notation and finite-magnitude boundaries follow 76 captured Chrome
cases. Changed values emit input followed by change; reaching a limit and then
leaving focus do not generate extra commits. The native test also verifies typed
integer model updates and the bound label after each edit.

Initial numeric-control qualification (checkpoint153): 14 core suites pass (485,452 main checks), all 16 sanitizer
gates pass, and the full native suite passes 28,127 checks, including 234 number-step
checks through viewport key routing, live constraints and model replacement.
Package, sample and exports pass. Performance acceptance is incomplete: regular
desktop passes 70/72 checks and 4K 3D passes 276/276; the 1080p run failed its
UI-disabled gameplay/binding assertions. Hover exceeded its target on both the
candidate and installed build in a focused comparison. No cause is established.
Six traced follow-up runs did not reproduce the gameplay failure. Both local
addons now contain the numeric fix and all 15 installed smoke suites pass. This
is a local development update; the failed performance qualifications remain open.
[Installed evidence](verification/number153-installed.json).

To refresh the browser expectations, run from the repository root:

```sh
node Tools/oracle/check_number_steps_chrome.cjs hosts/godot/project/number_step_cases.json
python Tools/oracle/generate_number_step_fixture.py
```

The Godot regression scene is `res://number_step_tests.tscn` in the host project.

## Slider direction

The installed build shares content-box rail and thumb-travel geometry between pointer
handling and painting. Horizontal `direction:rtl` reverses the track and left/right
keys. With `writing-mode:vertical-rl` or `vertical-lr`, pointer movement follows
the vertical axis; `direction` selects whether values increase down or up. Home,
End and page keys retain their numeric meaning. Native checks match 66 captured
Chrome cases; the preceding build differs in 17. The 14 core suites pass and a
rendered six-slider check confirms rail/fill/thumb orientation. Its original
checkpoint152 qualification passed native, sanitizer and 624 desktop/3D timing checks.
[Qualification](verification/range152.json). This does not establish
sideways writing modes or complete native slider appearance parity.

Current installed version: **preview157 (ABI minor22)**. Core, host, sample,
exports and installed smoke checks pass. See [current qualification](verification/class157.json)
for the latest performance scope; the numeric introduction results above are historical. Historical sections retain
the scope of their original checks. Remaining work is tracked in
[PRODUCT_READINESS.md](PRODUCT_READINESS.md).


## Numeric insertion filtering — installed preview113

The default numeric editor filters insertions against the text on both sides of
the selection. It suppresses duplicate decimal separators and exponents, handles
sign positions, and preserves incomplete editable values. Comma decimal input
stays visible as typed while the public value uses a dot. Full-width digits,
periods, and IME-style minus characters normalize to ASCII. Provisional composition
text remains editable; filtering runs when composition commits.

Public numeric values are cached against the existing form-state version.
Changing only the spelling/case of the same canonical input type no longer
sanitizes away an unfinished edit. Numeric public conversion also rejects `+-1`
and retains the different treatment of scientific notation observed in Chrome.

The [browser oracle](../Tools/oracle/check_number_editing_chrome.cjs) verifies
580 cases with the [retained fixture](../hosts/godot/project/form_validation_numbers.json):
start/end/middle insertion and full replacement around decimals, signs, and
exponents. Core tests compare both visible buffers and public values; the Godot
fixture checks readbacks and actual dialog submission rejection.
All ten core suites, twelve sanitizer gates, and 21,951 host checks pass, including
12,206 form-validation checks. Those validation checks also pass with OpenGL
and Vulkan active (behavior checks, not pixel parity). [Candidate evidence](verification/number-edit-candidate113.json).

This is default Western-number editing coverage, not all-locale or physical IME
certification. Fresh package/sample/export checks and all 72 unchanged desktop
performance gates pass. Both installed copies pass their smoke checks. See
[installed qualification](verification/number-edit-preview113.json).


## Incomplete numeric editing — source112, not installed

The core separates `Element::form_edit_value()` (the visible, caret-addressable
buffer) from `form_value()` (the public numeric value). User edits such as `-`,
`1e`, `1.`, and overflowing exponents expose an empty value and fail submission
through bad-input validation, including on optional controls. A leading user
`+` is omitted from a valid public value. Script assignments keep their existing
number sanitization. Value-change events carry the public value.

Layout, paint, retained text ownership, selection, history, and composition use
the editing buffer. Redundant edits preserve its storage and input version.
Changing bounds preserves an incomplete edit; reset and script clearing remove
it. Cloning and changing from number to text copy the public value. Core tests
cover conversion, submission/bypass, bounds, reset, clone/type changes, caret
completion, undo/redo, and composition cancellation.

[Chrome editing observations](verification/number-edit-browser.json) and
[lifecycle observations](verification/number-lifecycle-browser.json) are retained.
All ten core suites and twelve sanitizer gates pass; see
[core verification](verification/number-edit-core112.json).
Character filtering (including decimal separators), native host qualification,
and updated runtime measurements remain open. The installed DLL is still111.


## Default-value selection lifecycle — installed preview104

Default changes now carry a distinct mutation reason, so they are not mistaken
for current-value assignments. A fresh Chrome152 comparison covers 32 ASCII cases:
text input/textarea, pristine/edited value, focused/blurred field, and reset,
changed default, equal default or value-attribute assignment.

| Operation | Selection behavior |
|---|---|
| Changed pristine input default, focused | Collapses to the start |
| Changed pristine input default, blurred | Retains its saved selection |
| Pristine textarea default text replacement | Collapses to the start, including equal text |
| Default change after editing the current value | Preserves the current value and selection |
| Reset that changes the current value | Collapses to the restored value's end |
| Reset with an unchanged current value | Preserves selection |

All cases preserve the focused element. `set_element_text` on a textarea changes
its default child text; use `set_element_value` to assign its current control
value. Ordinary unchanged HUD text keeps its no-op fast path. Caret changes repaint
only the affected focused field; default mutations still use form input versions.

The new regression reproduced eight mismatching cases (16 before/after-update
assertions), then passed all 32 cases. Ten core suites, twelve sanitizer gates,
9,024 native checks (362 form-state), packaged exports and all 72 desktop timing
checks pass. [Portable evidence](verification/default-selection-preview104.json)
records the browser cases and verification. Raw evidence is
under `.utmp/safe-engine71/selection-lifecycle32-*` and `default-selection-*`.
This is not complete selection conformance: type changes, broader default-child
mutation combinations, Unicode offset conventions and IME restoration remain
separate coverage requirements. See PRODUCT_READINESS.md for candidate status.

A follow-up adds 48 short, empty and newline-sanitized default/attribute cases,
bringing this selection matrix to **80 cases**. All pass in the headless ABI suite
and through the unchanged installed preview104; its expanded form-state suite
passes 506 checks. No additional runtime fix was needed.
[Follow-up evidence](verification/short-selection-preview104.json) preserves the
Chrome results and exact binary hash. Broader child-mutation, type-transition and
IME cases remain outside this matrix.

## Skipped native control painting - installed preview94

Native control text, placeholder text, selection bands and carets are no longer
painted when the control has `content-visibility:hidden`. Hidden overlay text also
skips glyph shaping and atlas preparation. Range thumbs disappear while their
tracks remain; checkbox/radio marks and select arrows remain, matching the Chrome
probe. Revealing controls restores their drawing.

Validation: 10 core suites, 8,838 native checks (191 form-state), 469 Chrome152
comparisons, 12 sanitizer gates, packaged/relocated exports and all twelve normal
OpenGL/Vulkan workloads pass. Nine Frontier captures match preview93. Native
side-by-side capture: `.utmp/safe-engine71/content-paint-native.png`; its fixture
sets `min-width:0` to override the existing 218px select default and uses 20px
checkbox/radio boxes. It verifies the drawing distinction, not exact Chrome pixels.
The browser probe preserves per-control screenshots as `content-paint-*-visible.png`
and `content-paint-*-hidden.png` in the same evidence directory.

Other evidence there: `content-paint-tests.log`, `content-paint-host-checks/verification.json`,
`content-paint-chrome-suite.log`, `content-paint-asan-tests.log`, `content-paint-exports.log`,
`frontier-content-paint/verification.json`, `content-paint-perf/summary.json` and
`installed94-form-state.log`. This closes the overlay paint gap recorded in preview93;
full browser control appearance, automatic offscreen skipping and selection
persistence remain separate concerns.

## Skipped-content focus - installed preview93

A block panel with `content-visibility:hidden` now prevents descendant focus and
clears existing descendant focus on update. A child's `content-visibility:visible`
does not override that boundary. The panel itself remains focusable. `auto` does
not block focus. A text field with its own `content-visibility:hidden` keeps focus
but rejects typing and deletion, as in Chrome. The shared text-input target guard
also covers paste and composition eligibility.

Retained top-layer discovery now stops at skipped-content boundaries, matching
fresh box construction. The incremental comparison hides and reveals a panel
with an open popover, comparing each step to a forced full rebuild. Normal idle
processing adds no document scan; focus checks reuse existing ancestor traversal.

Validation: 10 core suites, 8,824 native checks (177 form-state), 455 Chrome152
comparisons, 12 sanitizer gates, packaged/relocated exports, fresh Frontier
integration and all twelve normal OpenGL/Vulkan workloads pass. Nine captures
match preview92. Evidence under `.utmp/safe-engine71/`: `content-focus-tests2.log`,
`content-focus-editing-host-checks/verification.json`, `content-focus-chrome-editing.log`,
`content-focus-asan-tests2.log`, `content-focus-exports.log`,
`frontier-content-focus/verification.json`, `content-focus-perf/summary.json`,
`installed93-form-state.log`. Baseline logs record five focus failures. The first
Chrome comparison also exposed the distinction between own-box focus and editing;
the corrected implementation and comparison include that distinction.

This verifies focus and editing, not complete content-visibility rendering.
The native form-control overlay paint path still needs an explicit skipped-content
audit. See [CSS Containment](https://www.w3.org/TR/css-contain-2/#content-visibility).

## Dialog focus routing - installed preview92

Both `show()` and `showModal()` now run dialog focusing and remember the previous
focus target. Delegates are checked against current styles and inertness, including
mutations made immediately before opening. An inert or hidden first control no
longer prevents a later usable control from receiving focus.

Closing a nonmodal dialog restores the previous target when focus is still inside;
if the user moved focus outside, that choice is preserved. Modal close attempts
restoration regardless, while existing availability checks reject hidden, disabled
or blocked targets. History is removed on close, element removal and document reload.
This follows the [HTML dialog focusing rules](https://html.spec.whatwg.org/multipage/interactive-elements.html#the-dialog-element).

The selection setter now propagates a failed focus request before changing the
active caret or committing its composition. This protects an unrelated focused
field. Preview99 retains visited fields' caret and anchor across focus changes,
including backward selections. A changed blurred value resets its retained cursor
to the end; an equal value preserves the selection. Stored entries are removed
with elements and document reload. Active typing does not copy text into this map
on each key; blur reuses the stored string capacity.

The host selection setter still focuses its field, as existing callers expect;
the C header now makes that contract explicit. It is not DOM setSelectionRange.
Preview100 starts untouched fields at position zero on programmatic focus. A
first explicit value assignment updates the unfocused cursor immediately. Tab
navigation selects input text and preserves textarea selection. The additive
`set_element_selection_without_focus(selector, start, end)` method prepares a
byte-based anchor/caret selection while preserving focus, including for a disabled
field. Bounds clamp to the field value. The existing focusing helper remains.

This API is available in ABI minor 13. Editing callers that need to append should
explicitly request the end position; focusing alone preserves the selection.
The preview104 section above adds default/reset combinations. Broader type/value
mutations and dialog/input-method restoration still need coverage. This is not
full browser selection conformance.

Preview100 passes 8,913 native checks (266 form), 546 Chrome152 checks, 10 core
suites, 12 sanitizer gates, Windows exports and normal-game performance. The new
initial-state regression reproduced two failures before the fix. Editing tests
whose scenario starts at the end now request that position explicitly. Evidence:
`.utmp/safe-engine71/selection-initial-*`.

Preview99 evidence: 8,907 native checks (260 form-state), 538 Chrome152 checks,
10 core suites, 12 sanitizer gates, Windows exports and normal-game performance
pass. The new core regression first reproduced six failures on preview98, then
passed after the implementation. Local evidence uses `selection-memory-*` under
`.utmp/safe-engine71/`.

Validation: 10 core suites, 8,804 native checks (157 form-state), 435 Chrome152
comparisons, 12 sanitizer gates, packaged/relocated exports and fresh Frontier
integration pass. All twelve normal OpenGL/Vulkan workloads pass; nine captures
match preview91. Evidence under `.utmp/safe-engine71/`: `dialog-focus-tests.log`,
`dialog-focus-final-host-checks/verification.json`, `dialog-focus-chrome-suite.log`,
`dialog-focus-asan-tests.log`, `dialog-focus-exports.log`,
`frontier-dialog-focus/verification.json`, `dialog-focus-perf/summary.json`,
`installed92-form-state.log`. The first host run had one geometry-fixture failure:
nonmodal focus now also paints an outline. The fixture explicitly disables outlines
to isolate backdrop triangles; its failed evidence remains in `dialog-focus-host-checks`.



## Inert panels — installed preview91



The boolean `inert` attribute blocks focus, typing, pointer targeting and user

scrolling throughout a DOM subtree. Binding `inert="{{ Blocked }}"` to a boolean

lets a game temporarily disable a panel without hiding it or changing form values.

A literal `inert="false"` is still present and therefore inert; remove the attribute

to re-enable it. Re-enabling does not automatically restore focus.



Modal dialogs escape ancestor inertness; an inert attribute on the modal itself

still blocks it. Promoted popovers retain their DOM ancestors' inertness. This

matches the [HTML inertness rules](https://html.spec.whatwg.org/multipage/interaction.html#inert-subtrees).

Inertness remains separate from form disabledness and its selectors. This does not

claim accessibility-tree, find-in-page, or full browser text-selection support.



Hit testing rejects inert candidates after bounds checks, including anonymous text

boxes, so eligible content underneath can receive input. Mutation-driven cleanup

clears focus and captured gestures, preserving physical button state to prevent

synthetic presses. No allocation or full-document inert scan is added to idle frames.

Select opening, pointer capture and wheel handling also check input availability.



Validation: 10 core suites (444,178 checks in the main suite), 8,788 native checks

including 141 form-state checks, 419 Chrome152 comparisons, 12 sanitizer gates,

packaged/relocated exports and fresh Frontier integration pass. All twelve normal

OpenGL/Vulkan workloads pass; nine captures match preview90. Evidence is under

`.utmp/safe-engine71/`: `inert-tests4.log`, `inert-host-checks/verification.json`,

`inert-chrome152-suite.log`, `inert-asan-tests.log`, `inert-exports.log`,

`frontier-inert/verification.json`, `inert-perf/summary.json` and

`installed91-form-state.log`. Initial hit-test fixtures lacked an explicit parent

stacking context; their failed logs are retained as `inert-tests[2].log`.



## Hidden panel focus — installed preview90



Installed preview90 clears focus when a style update hides the focused field through

`display:none`, inherited `visibility:hidden`, or the `hidden` attribute. A child

with `visibility:visible` remains focusable beneath a hidden-visibility ancestor;

opacity zero also preserves focus, as in Chrome. Typing no longer edits a field

after its panel is hidden.



Validation reads the cascade's updated styles only on changed passes. An actual

blur restyles the root scope affected by `:focus-within`, sibling selectors and

`:has()` before painting, preserving input-version match caches. Animation state

is reapplied with zero elapsed time. Clean-frame early returns remain intact.

The explicit-focus C ABI documentation now states its hidden/modal rejection rules.



Ten core suites, 407 Chrome152 comparisons, 8,776 native checks (129 form-state),

12 sanitizer gates, packaged exports and fresh Frontier integration pass.

Evidence under `.utmp/safe-engine71/`: `panel-focus-tests2.log`,

`panel-focus-chrome-suite.log`, `panel-focus-final-host-checks/verification.json`,

`panel-focus-asan-tests.log`, `panel-focus-exports.log` and

`frontier-panel-focus/verification.json`. All twelve normal OpenGL/Vulkan workloads

pass (`panel-focus-perf/summary.json`); nine captures match preview89. Both projects

use DLL SHA-256 `b1cb6b45ce854f4caa6ac9ec0a20d90c372559990ec3f4870f22ca840db22643`.

The installed smoke passes 129 checks (`installed90-form-state.log`).



Native binding cases use templates in inline style attributes. `WevaDocument.css`

is a CSS source string, not a template resolver. The separate `hidden` case adds

the attribute explicitly: `hidden="false"` still hides an element. Earlier fixture

failures from these assumptions and a GDScript type inference error are retained

in `panel-focus-host*`, `panel-focus-fixed-host*` and `panel-focus-verified-host*`;

the final host result above is authoritative.



## Dialog focus restoration — preview89



Installed preview89 rejects restoring focus to a hidden, disabled or modal-blocked

control, including controls in a dialog closed out of order. Explicit focus checks

resolve the target's ancestor styles when mutations are pending; they do not

overwrite retained styles. Closing schedules one visibility check on update, so

focus in the now-hidden dialog is released and typing cannot edit its fields.

Ordinary clean frames do not resolve a new ancestor chain.



The core regression checks immediate and post-update focus separately. Godot's

`get_focused_id()` flushes pending updates, so native assertions check the settled

state. Browser typing tests explicitly place the initial caret at the end; this

change does not claim identical default caret placement between hosts.



Evidence under `.utmp/safe-engine71/`: `dialog-restore-chrome.log` and

`dialog-restore-native.log` demonstrate the previous differences;

`dialog-restore-tests3.log` passes 10 core suites and

`dialog-restore-chrome-suite2.log` passes 392 Chrome152 checks.

All 8,761 native checks (114 form-state), 12 sanitizer gates, packaged/relocated

exports, fresh Frontier integration and twelve normal OpenGL/Vulkan workloads

pass. Nine integration captures match preview88. Evidence:

`dialog-restore-fixed-host-checks/verification.json`, `dialog-restore-asan-tests.log`,

`dialog-restore-exports.log`, `dialog-restore-perf/summary.json` and

`installed89-form-state.log`. Both projects use DLL SHA-256

`2224fb37858a74906deb26dc386deea075167f528ae5c58969b373068b2f775b`.

Preview90 adds the ordinary-panel visibility lifecycle checks described above.

The reference is the [HTML dialog closing algorithm](https://html.spec.whatwg.org/multipage/interactive-elements.html#close-the-dialog).



## Modal input and top-layer order — preview88



Preview87 permits background clicks and focus while a modal dialog is open.

Installed preview88 adds an opening-order modal stack and filters

pointer targets, focus navigation, explicit focus, select opening and user

scrolling through it. Opening places focus in the modal; closing restores the

previous control. Background presses are cancelled on opening, and backdrop

clicks target the dialog. Ordinary input with no modal does not scan the DOM.



Preview88 also promotes dialog/popover boxes and their backdrops to document-root

siblings. A live opening sequence drives the shared paint/hit order above author

z-index. Reverse opening order works, and ancestor transforms, clipping and

opacity no longer affect the promoted boxes. Default popovers use zero insets,

auto margins and fit-content dimensions. Top-layer position and display:contents

receive their computed-value adjustments.



Partial subtree rebuilds exclude descendants already promoted out of that subtree.

Modal layout reuse independently emits promoted hosts inside retained DOM parents;

an incremental-versus-full-render regression covers repeated modal toggling with

an existing manual popover and background text changes. No Box field was added.



The source passes 10 core suites, 8,749 native checks (102 form-state checks),

380 Chrome152 comparisons, packaged exports and fresh Frontier integration.

Evidence under `.utmp/safe-engine71/`: `top-order-tests7.log`,

`top-order-final-host-checks/verification.json`, `top-order-chrome.log`,

`top-order-exports.log` and `frontier-top-order/verification.json`.

All 12 sanitizer gates pass (`top-order-asan-tests8.log`), as do all twelve

normal OpenGL/Vulkan workloads (`top-order-perf/summary.json`). Nine integration

captures match preview87. Both projects are installed with DLL SHA-256

`557bbf1e36f9771dd84114794972e0a29f4e2a20d71667deda55801430f8a9fc`;

`installed88-form-state.log` passes 102 checks. This verifies the described

modal/top-layer behavior, not full browser parity or other-platform readiness.



Historical failed probes are retained: `modal-input-reverse-*.log` show the earlier

button hit bug; `top-order-tests6.log` shows the subsequently corrected retained

popover omission. The initial native backdrop assertion was corrected to isolate

geometry from the new focus outline. Native default-popover testing exposed missing

fit-content sizing; the production UA defaults were corrected before rerunning.

The reference is the [HTML modal inertness model](https://html.spec.whatwg.org/multipage/interaction.html#inert-subtrees).

Rendering follows the [CSS top-layer model](https://drafts.csswg.org/css-position-4/#top-layer).



## Dialog API state checks



Windows preview87 introduced rejection of changing an open dialog between modal

and nonmodal modes. The C ABI returns `WEVA_ERR_INVALID_STATE` (6), and Godot

returns false, without changing the existing dialog. Repeating the same show

mode remains a successful no-op. Close before opening in a different mode.

A visible popover cannot become modal, and a modal dialog cannot be opened

as a popover. Hiding an already-hidden popover remains a no-op.



Successful dialog opening now queues its opening toggle notification.

Opening dismisses unrelated auto/hint popovers, while ancestor and manual

popovers remain open. Notifications follow the existing captured-state queue.

These rules follow Chrome and the

[HTML dialog algorithms](https://html.spec.whatwg.org/multipage/interactive-elements.html#the-dialog-element).

Native dialog Escape policy, return values, beforetoggle cancellation and

browser task coalescing remain outside this API change.



Ten core suites and 368 browser comparisons pass. Evidence under

`.utmp/safe-engine71/`: `dialog-api-tests.log`, `dialog-api-chrome.log`,

`dialog-state-probe.log` and `dialog-popup-probe.log`.

All 8,738 native checks, 12 sanitizer gates, packaged/relocated exports and

fresh Frontier integration checks pass. All twelve normal OpenGL/Vulkan

workloads pass, and nine integration captures match preview86. The installed

form-state smoke passes 91 checks. Evidence: `dialog-api-host-checks/verification.json`,

`dialog-api-asan-tests.log`, `dialog-api-exports.log`, `dialog-api-perf/summary.json`

and `installed87-form-state.log`. Both projects use library SHA-256

`c3ab5d03b1759a0b5ef24c127f890961426947b3b25672496e7270d8fb62dd02`.



## Popover toggle notifications



Installed preview86 emits one close notification when a live popover

attribute is removed or changes mode. Equivalent mode changes do not notify;

repeated explicit hides still emit only one close. Dismissal bookkeeping and

notification now share the live-state observer, so API and binding changes

take the same path.



`weva_event.text` carries the captured new toggle state (`open` or `closed`)

without changing the ABI struct layout. Godot's `element_toggled` signal reads

that captured value. Previously it inspected the `open` attribute, making

every popover notification report false and allowing later mutations to alter

the meaning of a queued notification. The C ABI remains a bounded event queue;

browser task coalescing is not implemented. Build201 also lacks
cancellable `beforetoggle`; build202 adds it as described above.



Ten core suites and 333 Chrome comparisons pass. Evidence:

`.utmp/safe-engine71/popover-event-tests.log` and `popover-event-chrome.log`.

The focused native scene passes 82 checks, including bound mode changes,

attribute removal and reentrant close callbacks. Notifications are delivered

on update or the next frame. The initial fixtures checked before pumping

events, then incorrectly treated the enumerated `popover` attribute as boolean;

the corrected fixture explicitly removes/restores it. Failed runs remain

under `popover-event-host.log`, `popover-event-fixed-host.log` and

`popover-event-final-host.log`; the focused corrected result is

`popover-event-fixture4.log`. All 8,727 native checks, twelve sanitizer gates,

packaged/fresh survival exports and twelve normal OpenGL/Vulkan workloads pass.

Nine captures match preview85; installed form-state smoke passes 82 checks.

Evidence: `popover-event-verified-host-checks/verification.json`,

`popover-event-asan-tests.log`, `popover-event-exports.log`,

`popover-event-perf/summary.json`, `popover-event-pixel-comparison.json`

and `installed86-form-state.log`. Both projects use library SHA-256

`1a76fd91d93fe893dc06a191ebde913bfdc1ebdd7884e8bd7df22e510a084ff0`.



## Modal and popover selectors



The preceding preview85 implements `:modal` and `:popover-open` from

versioned live element state. Rendering uses that same state. Authoring

`data-modal` or `data-popover-open` does not open a menu or make it modal;

these attributes remain compatibility mirrors written by the runtime APIs.

Use dialog/popover APIs and normal selectors when authoring UI.



Popup state changes refresh descendant CSS and the top layer. Escape and

outside clicks clear auto-popover selector state. Removing `popover` closes

it, and restoring the attribute does not reopen it. Equivalent keywords such

as an empty value and `AUTO` preserve visibility. Manual and invalid popover

keywords resist light dismissal. Live popup state is not copied when cloning.



Chrome keeps `:modal` true when script removes a modal dialog's `open`

attribute; `close()` with no open attribute does not clear it either. The

runtime preserves this distinction: use the close API instead of removing

the attribute. Ordinary nonmodal `show()` does not match `:modal`.

Fullscreen modality and native dialog Escape policy remain outside this change.



Ten core suites and 314 Chrome comparisons pass. Evidence under

`.utmp/safe-engine71/`: `top-state-tests4.log`, `top-state-chrome.log`,

`top-state-probe.log` and `top-attribute-probe.log`. The first build was

interrupted at linking by an overlapping test process; its old-binary test

output is not verification. Builds and tests above ran sequentially.

All twelve sanitizer gates, packaged/relocated exports and fresh survival

integration checks pass. The initial native fixture sent Escape without

giving its new Godot Control keyboard focus; that check failed and remains

recorded in `top-state-host.log`. The corrected fixture explicitly focuses

the UI surface before sending the key. The corrected suite passes 8,721 native

checks, including 76 form-state checks (`top-state-fixed-host-checks/verification.json`).

All twelve normal OpenGL/Vulkan workloads pass, nine captures match preview84,

and installed form-state smoke passes 76 checks without errors. Evidence:

`top-state-perf/summary.json`, `top-state-pixel-comparison.json`,

`top-state-asan-tests.log`, `top-state-exports.log` and `installed85-form-state.log`.

That build used library SHA-256

`60a8e45b1713074d5a2a22f58a4f7077798fa83970ce8394d3e624623ce69934`.



## Command buttons and submission



The preceding preview84 uses the same allocation-free submit-button

classification for pointer activation, keyboard activation, implicit Enter

submission and `:default` styling. An Auto button with `command` or `commandfor`

does not submit; Enter in a text field skips it and activates the real default.

A disabled default still blocks implicit submission. Explicit `type=submit`

continues to submit even with command attributes. This fixes accidental

submission; general command dispatch remains unimplemented.



Ten core suites and 295 Chrome comparisons pass, including actual browser

clicks and Enter. Evidence: `.utmp/safe-engine71/submit-tests.log` and

`submit-chrome3.log`. The new browser interaction cases use an isolated page

to avoid carrying pointer state into the older disabled-hover fixture.

Native checks pass 8,711 cases, including 66 form-state checks with actual

pointer and keyboard activation. All twelve sanitizer gates, packaged/relocated

exports and fresh survival integration checks pass. Evidence:

`submit-host-checks/verification.json`, `submit-asan-tests.log`,

`submit-exports.log` and `frontier-submit/verification.json`.

All twelve normal OpenGL/Vulkan workloads pass, nine captures match preview83,

and the installed form-state smoke passes 66 checks without errors. Evidence:

`submit-perf/summary.json`, `submit-pixel-comparison.json` and

`installed84-form-state.log`. That build used library SHA-256

`8b334b3123c2ad45ec34465a75f483e8b4f9bd393138aaa6c791bae2cf88422b`.



## Default control styling



The preceding preview83 implements `:default`: checkboxes/radios with

`checked`, options with `selected`, and the first submit button associated

with each form in tree order. Live selected/checked values do not replace

markup defaults. Disabled submit buttons still count, including external

controls associated through `form`. Reset buttons and unowned actions do not.

Button Auto state excludes command/commandfor controls and select children;

an explicit submit type remains a submit button.



Default submit changes can affect remote elements. A mutation input flag

tracks structure and type/form/id/command attributes; only sheets using

`:default` consume it. The runtime retains previous default buttons and

invalidates changed defaults and their descendants. The cascade key includes

default state for the element and its ancestors. Normal value, text, hover

and clock updates do not trigger the default-button scan.



Evidence: `.utmp/safe-engine71/default-tests.log` (10 core suites) and

`default-chrome2.log` (275 browser comparisons). Chrome 152 changes

`matches(':default')` after adding `command`, but leaves descendant CSS stale;

`default-live-probe.log` preserves this discrepancy. The oracle reparses that

one case to verify intended styling; the runtime updates it immediately.

Rules follow the [HTML definition](https://html.spec.whatwg.org/multipage/semantics-other.html#selector-default).

Native checks pass 8,706 cases, including 61 form-state checks for remote

association changes, removal, type changes and markup defaults. All twelve

sanitizer gates, packaged/relocated exports and fresh survival integration

checks pass. Evidence: `default-tests2.log`, `default-host-checks/verification.json`,

`default-asan-tests.log`, `default-exports.log` and `frontier-default/verification.json`.

All twelve normal OpenGL/Vulkan workloads pass, nine captures match preview82,

and the installed form-state smoke passes 61 checks without errors. Evidence:

`default-perf/summary.json`, `default-pixel-comparison.json` and

`installed83-form-state.log`. That build used library SHA-256

`1ce77ed195ffe1820fc60a6119f6023fc49ffe01cfc902da4d28934b8be32521`.

That build's older activation helper still treated Auto command buttons as

submit buttons; preview84 above unifies these rules.



## Read-only and read-write styling



The preceding preview82 implements `:read-only` and `:read-write`.

Text-entry inputs and textareas match read-write when neither readonly nor

disabled, including fieldset inheritance and its first-legend exception.

Checkboxes, sliders, buttons and ordinary text match read-only by default;

the selector does not mean that every kind of interaction is blocked.

Readonly text retains focus and selection while rejecting edits.



The CSS matcher also resolves `contenteditable` inheritance, including false

islands, invalid values and `plaintext-only`. This is selector support only:

editing arbitrary contenteditable DOM is not implemented. Input/textarea

mutability remains independent of an ancestor editing host. These rules follow

the [HTML selector definitions](https://html.spec.whatwg.org/multipage/semantics-other.html#selector-read-write)

and 253 Chrome form-state comparisons. Ten core suites pass; live attribute,

type and inherited contenteditable changes refresh CSS through DOM invalidation.

Evidence: `.utmp/safe-engine71/readwrite-chrome.log` and `readwrite-tests.log`.

Native checks pass 8,699 cases, including 54 form-state checks with actual

locking/unlocking and typing. All twelve sanitizer gates, packaged/relocated

exports and fresh survival integration checks pass. Further evidence:

`readwrite-host-checks/verification.json`, `readwrite-asan-tests.log`,

`readwrite-exports.log` and `frontier-readwrite/verification.json`.

All twelve normal OpenGL/Vulkan survival workloads pass. Nine captures match

preview81, and the installed form-state smoke passes 54 checks without errors.

Evidence: `readwrite-perf/summary.json`, `readwrite-pixel-comparison.json`

and `installed82-form-state.log`. That build used library SHA-256

`7a14d187d0e4e51c4ffaadde606da93ed7fa56fc5d44576281b27875ae9c4a94`.



## Required and optional styling



The preceding preview81 implements `:required` and `:optional` for HTML

form controls. Required-capable inputs, selects and textareas match `:required`

when the boolean attribute is present, including `required="false"`. Disabled

and readonly do not change that selector state. Inputs whose type does not

support required (including range, color and hidden) remain optional. Buttons

also match `:optional`; fieldsets and ordinary elements match neither selector.

Live attributes, type changes and boolean bindings use the existing cascade

invalidation. This adds styling semantics, not constraint validation or blocked

submission.



Ten core suites, 154 Chrome form-state comparisons and 8,691 native checks pass,

including 46 native form-state checks. Evidence under `.utmp/safe-engine71/`:

`required-tests.log`, `required-chrome2.log`, `required-host-checks/verification.json`.

All twelve sanitizer gates, packaged/relocated exports and twelve normal

OpenGL/Vulkan survival workloads pass. Nine captures match preview80. The

installed form-state smoke passes 46 checks without errors. Further evidence:

`required-asan-tests.log`, `required-exports.log`,

`required-perf/summary.json`, `required-pixel-comparison.json` and

`installed81-form-state.log`. That build used library SHA-256

`9a332520cb5889df6ac4ed52e390f9c58e27b7c27a22df4ca68581aef490f5b3`.



## Disabled groups



The preceding preview80 keeps pointer enter/down/up, hover/pressed CSS and

title tooltips available on disabled actions, while preventing click activation

and focus. A slider disabled during a drag stops changing value. This lets a

disabled crafting button explain its missing requirement without becoming usable.

Ten core suites, 101 Chrome form-state checks and 8,687 native checks pass.

The 42 native form-state checks include actual pointer styling and tooltip

behavior. Its viewport explicitly receives a mouse-enter notification before

motion input; the initial fixture lacked that notification and failed two

checks. That failed run remains in `disabled-hover-host.log`.

Evidence under `.utmp/safe-engine71/`: `disabled-hover-tests2.log`,

`disabled-hover-chrome.log`, and `disabled-hover-fixed-host-checks/verification.json`.

All twelve sanitizer gates, packaged exports and twelve OpenGL/Vulkan survival

workloads pass. Nine captures match preview78. Installed form-state smoke

passes 42 checks. That build used library SHA-256

`c461f0bbc1b566376e09c661a2f0c4671a574b9e185562c3afe4fa4caba8f708`.



The preceding preview78 added HTML fieldset disabledness: controls in

the first direct `legend` remain usable, so a master enable checkbox can live

there. Other controls, including those in later legends or nested fieldsets,

inherit the disabled state. Ordinary links and text are not disabled by the

group. `:enabled`/`:disabled` apply only to supported form elements; options

inherit disabledness from their optgroup. CSS state and interaction use the

same rules. Disabling a focused control or its group releases focus during

the document update; re-enabling refreshes descendant styles.



Ten core suites and 97 Chrome form-state comparisons pass. Native tests cover

master-control activation, second-legend rejection and live CSS/focus changes.

Evidence: `.utmp/safe-engine71/fieldset-final-tests.log`,

`fieldset-final-chrome.log`, and `fieldset-host-checks/verification.json`.

All twelve sanitizer gates, 8,682 native host checks, packaged exports, the

survival consumer and twelve normal OpenGL/Vulkan workloads pass. Nine survival

captures match preview77. The installed form-state smoke passes 37 checks.

Both projects use library SHA-256

`f9b54493851a79d51e60420eeb0a0627b85764df510e8b1dee99edea4779a589`.



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



`Tools/oracle/check_select_chrome.cjs` runs 69 Chrome checks corresponding to

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



`Tools/oracle/check_form_state_chrome.cjs` records Chrome behavior;

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


### Dialog close notifications (current source, ABI minor 15)

Closing an open dialog now queues a non-cancelable `WEVA_EVENT_CLOSE` after
focus restoration. `on-close` resolves on the dialog itself, without searching
ancestors. Calling close again, or removing `open` directly, does not emit this
notification. Godot source adds a `dialog_closed(id)` signal. This change is not
in historical preview105; its Godot signal is now included in installed preview107.

The [27-case Chrome observation](verification/dialog-cancel-chrome.json) covers
closed, non-modal and modal dialogs with explicit close, requestClose and Escape,
and allowing, preventing or closing from the cancel handler. Cancellation occurs
while the dialog is open; a reentrant close must not be overwritten by the
original request. These observations and the
[HTML dialog algorithms](https://html.spec.whatwg.org/multipage/interactive-elements.html#the-dialog-element)
are the inputs for the cancellation implementation below. Beforetoggle and
browser task ordering/coalescing remain incomplete.


### Queued dialog cancellation (installed preview107, ABI minor 15)

`weva_element_request_close_dialog` queues a non-bubbling `CANCEL` for an open
dialog. A host drains events, runs the cancel handler while the dialog is open,
and may call `weva_document_prevent_default` before polling again. The next poll
applies the default close unless vetoed. Closing, removing, reloading, or
removing/re-adding `open` invalidates the old request. An already closed dialog
produces no request. Discarded queue entries release retained element references.

This is an explicit C event-queue protocol, not a synchronous JavaScript API.
A host must finish the current handler before polling again. Godot source now
provides a request-close wrapper, prevent-default method and an event-pump
reentrancy guard. Installed preview107 passes 554 native dialog checks, including modal
and non-modal requests, controller/signal vetoes, nested updates, lifecycle
changes, Escape input and return values. Beforetoggle/task ordering remain open.
The change is included in installed preview107.


The source event queue now preserves accepted dialog close requests during
ordinary notification overflow. All insertion paths use the same bounded policy:
evict an ordinary notification first; if the queue contains only close requests,
a new request returns `WEVA_ERR_INVALID_STATE` without enqueueing it. Hosts can
drain and retry. Ordinary notifications remain best-effort under overflow.
Core tests cover both veto and default close after 600 keyboard notifications,
and explicit rejection once 256 pending requests fill the queue. This follow-up
is included in installed preview107.


### Escape close requests (current core source)

Escape now requests cancellation of the latest opened dialog, including markup,
attribute changes and dialog APIs. `closedby="none"` disables it; `any` and `closerequest` enable it.
Missing/invalid values default to enabled for modal dialogs and disabled for
non-modal dialogs. Keyword matching is ASCII case-insensitive. A later disabled
non-modal dialog prevents Escape from reaching an earlier modal dialog. The
existing cancel handler can veto closing.

Composition handles Escape first, then auto/hint popovers and an open select,
then the dialog. This processing does not depend on the focused control having
fresh layout. Core coverage includes 56 combinations of mode, policy, stacking
and veto. The [browser observations](verification/dialog-escape-browser.json)
include 20 corresponding show-API cases and eight markup/attribute-open cases.
Those markup/attribute cases now pass core tests. A separate close-order list
tracks actual openings without changing show-API focus restoration. It also
covers inserted dialogs and bound `open` changes, clears removed/reloaded nodes,
and preserves order when the value of an already-present `open` attribute changes.
Candidate107 passes native Escape checks for modal, markup and attribute opening,
policy values and vetoes. A real Godot key-input regression confirms popover,
dropdown and modal dismissal order. Complete CloseWatcher behavior and broader
browser task ordering remain open.


The close-order follow-up also tests reversed attribute-opening order,
remove/re-add, insertion and reload, plus a binding-driven ordering regression.
These changes are included in native candidate107.


### Dialog return values (core source, ABI minor 15)

The C API now provides `weva_element_dialog_return_value` and
`weva_element_set_dialog_return_value`, plus close/request-close variants ending
in `_with_value`. Existing close/request calls preserve the current result.
For the new variants, null preserves the result and an explicit empty string
clears it. Closing an already closed dialog does not change its result.

A requested result is copied and applied only if cancellation permits closing.
An omitted result preserves property changes made by the handler; an explicit
result overrides those changes if the default close proceeds. A handler's own
close invalidates the original request, preserving the handler's result. Escape
requests an explicit empty result, matching the recorded Chrome behavior.

Results live independently of HTML attributes and survive close/reopen. Removal
and reload discard them. Property writes do not mark style/layout dirty; the
getter supports a full byte-length query and UTF-8-safe terminated copies.
The [24 browser cases](verification/dialog-result-browser.json) are included in
core and native Godot regression coverage. Godot close/request-close accept an
optional result (null preserves, empty clears), with `get_dialog_return_value`
and `set_dialog_return_value` property access. Native coverage includes full long
Unicode readback and Escape/veto result behavior. Form-method integration remains
pending. These APIs are included in installed preview107.

### Dialog form submission (core source, ABI minor 16; not installed)

Submit events now retain the form and submitter until the host finishes the
handler. `weva_document_prevent_default` can veto `SUBMIT` as well as `CANCEL`.
The next event poll resolves the current `method` / submitter `formmethod` and,
for `dialog`, closes the form's nearest ancestor dialog. This is the existing
explicit C event-drain protocol; it does not introduce synchronous JS callbacks.

The result is read after the handler. A submit button's absent `value` preserves
the current dialog result; an explicit empty value clears it. Image submitters
use selected coordinates (keyboard activation uses `0,0`). Implicit Enter with
no submit button clears the result, matching the recorded Chrome behavior.
Submission does not dispatch a dialog cancel event. Removed/reloaded forms do
not run a stale default action; recursive submission during the form's active
handler is suppressed. Accepted submissions are preserved under queue overflow.

The [browser observations](verification/dialog-submit-browser.json) cover 54
result/handler cases, six implicit keyboard cases and 24 method/image cases.
The result and method/image matrices pass core regression tests. Required-value
validation is now implemented below; other constraint rules remain incomplete.
This is not complete form submission support and has not replaced installed preview107. Native integration, click
default-action ordering and validation are still required before packaging.

[Core verification](verification/dialog-submit-core.json) records all ten release
suites and twelve sanitizer gates passing. A separate
[20-case validation observation](verification/dialog-validation-browser.json)
defines the next required checks: invalid fields suppress submission, while
`novalidate` and `formnovalidate` bypass validation.

The [eight handler-mutation observations](verification/dialog-submit-owner-browser.json)
also pass core tests. Chrome retains the original form action when the handler
changes the button's owner/type or removes the button, while changes to the
original form's method or the button's `formmethod` affect the default action.
Lifecycle regressions cover form removal and document reload before/during a
submit handler, recursive submission suppression, veto/default behavior after
600 ordinary notifications, and draining/reusing a queue filled by 300 attempts.
[Lifecycle verification](verification/dialog-submit-lifecycle-core.json) passes
all ten core suites and twelve sanitizer gates with these expanded tests.

### Required-value validation (core source, ABI minor 16; not installed)

Before delivering a queued submit event, the core checks required values on the
form's validation candidates. This observes value changes made by earlier click
handlers. Disabled/readonly controls and controls inside datalist are excluded;
fieldset disabledness includes the first-legend exception. Radio requirements
use the named group and form owner. For select, only a first, direct-child empty
option in a single-row dropdown is a placeholder; other selected empty options
can satisfy required. `novalidate` and `formnovalidate` bypass the check.

Failure aborts submission and queues `WEVA_EVENT_INVALID` for each accepted
invalid notification. `on-invalid` resolves on the control without ancestor
fallback. The host can prevent default reporting during an invalid event, but
that does not turn the failed submission into a successful one. After all
invalid handlers finish, the core focuses the first remaining unhandled
candidate. Recursive submission from an invalid handler is suppressed.
Godot source adds `element_invalid(id)`; native qualification is pending.

[Thirty browser cases](verification/required-validation-browser.json) cover
validation candidates and missing-value rules. The
[reporting probe](verification/invalid-report-browser.json) confirms handler
ordering, focus selection, cancellation and reentry behavior. This is required
validation only; number constraints are described below. Type mismatches,
patterns, length limits and public check/report validity APIs remain unfinished.
Custom validity is implemented below.
[Core verification](verification/required-validation-core.json) passes all ten
release suites and twelve sanitizer gates. Candidate108 now includes native
required/number/custom validation tests; exports and performance remain pending.

### Number-input constraints (core source, ABI minor 16; not installed)

Number inputs now check `min`, `max` and `step` before submission. The step base
uses a valid `min`, then the default `value` attribute, then zero. Non-positive
or invalid steps use the default step of one; `any` is case-insensitive.
Empty optional values do not fail these constraints. Existing validation bypass
attributes and invalid-event reporting apply to numeric failures too.

The engine uses the [adapted Chromium Decimal implementation](../third_party/decimal/README.md)
for parsing and arithmetic, retaining the browser's decimal precision and step
tolerance. This avoids rounding decimal input through binary floating point
before checking constraints. Number sanitization now also preserves valid tiny
values such as `1e-999`, matching the browser observation. The addon packager
includes the third-party license.

[49 Chrome cases](verification/number-validation-browser.json) cover bounds,
step bases, decimal tolerance, case-insensitive `any`, malformed attributes,
large values and tiny exponents. Core submission tests cover rejection and
bypass. This does not certify date/time constraints, number editing's bad-input
state or the pending native addon and performance checks.
[Core verification](verification/number-validation-core.json) passes all ten
release suites and twelve sanitizer gates with the numeric changes.

### Custom validity and invalid-event updates (ABI minor 16 candidate)

`weva_element_set_custom_validity` stores an error on input, textarea, select or
button; an empty string clears it. `weva_element_custom_validity` reads the full
stored UTF-8 message. Godot exposes `set_custom_validity` / `get_custom_validity`.
The getter is explicitly the stored custom message, not the browser's computed
`validationMessage`, which can be empty for barred controls.

Custom errors block submission only when the control is a validation candidate.
Reset preserves them; cloning clears them. Identical writes do not change the
core form version. [Twenty Chrome observations](verification/custom-validity-browser.json)
cover these rules; core tests cover the four clone cases and sixteen submission
cases, plus Unicode readback and invalid arguments.

An invalid handler can disable, fix or remove a later field. Pending invalid
events now recheck candidacy, form ownership and errors before delivery. Core
lifecycle tests cover those cases, removal of the active field, document reload
and notification overflow. The [browser lifecycle probe](verification/invalid-lifecycle-browser.json)
also records detached-form behavior that is not fully modeled: C document reload
and subtree removal invalidate queued handles, whereas browser handlers can retain
detached DOM objects. This remains part of the event-lifecycle parity gap.

Candidate108 passes 9,997 headless host checks across 29 entries, including 252
validation checks. Those 252 checks also pass on each of OpenGL and Vulkan.
It remains uninstalled; exports, performance and remaining validation features
are not qualified by these checks.

### Email type mismatch (source after candidate108)

Email inputs now reject nonempty malformed addresses during validated submission.
The implementation follows the [HTML email grammar](https://html.spec.whatwg.org/multipage/input.html#valid-e-mail-address):
ASCII local-part characters, nonempty domain labels with at most 63 characters,
and comma-separated addresses when `multiple` is present. Optional empty fields
remain valid. Existing value sanitization removes line breaks and trims ASCII
whitespace. Validation uses string views and adds no parsing allocation.

[Chrome evidence](verification/email-validation-browser.json) covers 56 value and
validity cases and 112 form submissions with and without `novalidate`. The same
cases run through the core helper and C API; failed submissions report invalid
and keep the dialog open. This includes Unicode rejection for programmatic
values, punycode acceptance, empty list members and domain label boundaries.
This does not qualify native keyboard/IME IDN conversion.

The email change is included in isolated candidate109; it remains uninstalled.
URL validation, pattern/length constraints and public validity APIs remain open.

### URL type mismatch (source candidate109)

URL inputs now require an absolute URL accepted by the [Ada parser](../third_party/ada/README.md),
with no base URL. Optional empty values remain valid. This covers special and
custom schemes, Unicode domains, IPv4/IPv6, ports and file URLs without issuing
network requests. The private parser target uses C++20; public core consumers
retain C++17. Parsing only occurs during validation of URL controls.

[Browser observations](verification/url-validation-browser.json) contain 54 values
and 108 submissions. Core tests cover all 54 values and 106 submissions (the C
string setter cannot express the embedded-NUL case). Two cases intentionally
follow the [URL Standard](https://url.spec.whatwg.org/#forbidden-domain-code-point)
where Chrome 152 differs: literal and percent-encoded spaces in a domain are
rejected. Chrome accepts both. These are recorded differences, not Chrome parity
passes. All other observed value cases agree.

The parser is vendored unchanged at Ada 4.0.0, with URLPattern disabled and license
files included by addon packaging. This dependency does not implement the HTML
`pattern` attribute. Pattern/length/date-time constraints and public validity APIs
remain unfinished. Candidate109 requires native, export and performance evidence
before replacing the installed preview107.

Candidate109 passes all 10 core suites and 12 sanitizer gates, plus 10,210 host
checks across 29 entries. Its 465 validation checks pass with OpenGL and Vulkan
backends (input/state checks, not pixel comparisons). See the
[candidate109 receipt](verification/url-validation-candidate109.json). Exports,
performance and installation remain unqualified for this candidate.

### Text length validity (core source after preview109)

The core now tracks whether a text value was last edited through native editing
or assigned by script. `minlength` and `maxlength` constrain user-edited input and
textarea values during submission; script-provided values remain exempt. Empty
optional values are not too short. Length counts UTF-16 code units, including two
units for supplementary emoji, rather than UTF-8 bytes or grapheme clusters.

[Value observations](verification/length-values-browser.json) cover 20 Chrome
cases, including limits changed after typing, emoji, combining marks and integer
parsing. Core and C submission tests cover those cases, user/script assignment
and `novalidate`. [Edit-origin observations](verification/length-edit-browser.json)
cover reset, cloning, default changes and same-value assignment: input assignment
clears edited validity even when text is unchanged, whereas textarea retains it
for an unchanged assignment. The version changes when this validity input changes;
ordinary redundant assignments remain no-ops. Cloning retains edit origin and
reset clears it. The core covers 16 origin cases; the two browser `setRangeText`
cases remain observations because that public API is not implemented here.

Native typing, deletion, composition edits and undo/redo use the user-edit path.
[Chrome undo observations](verification/length-undo-browser.json) confirm that
undo to a formerly scripted value still counts as a user edit. Full native/IME
qualification for this new validity state remains pending. Input maxlength
insertion filtering and validity now share the same limit parser.

This source change is not in the installed preview109. Pattern/date-time and
bad-input constraints, public validity APIs and validity selectors remain open.

### Native length-validation follow-up (candidate110)

The Godot validation suite now exercises typed and scripted values, same-value
assignment, reset, changed maximum length, undo/redo, composition commit, explicit
finish and empty composition commit. The composition cases start from an empty
field. [Ten Chrome CDP observations](verification/length-composition-browser.json)
record matching preedit/commit/script/cancel validity behavior for input and
textarea. These are simulated editing paths, not physical OS IME qualification.

Candidate110 is isolated from installed preview109. Its native results are
recorded separately; no new release, performance or installation claim is implied.

Candidate110 passes 10,474 checks across 29 headless entries, including 729
validation checks that also pass under OpenGL and Vulkan. The added 24 native
scenarios cover both input and textarea. [Native evidence](verification/length-validation-candidate110.json)
records the binary, successful retry after a test-script annotation fix and scope.
These results do not qualify physical OS IME, pixels, exports or performance.

### Temporal value sanitization (core source after candidate110)

Date, month, ISO week, time and local datetime inputs now sanitize assigned values.
Invalid calendar dates, malformed fields and out-of-range values become empty.
Local datetimes normalize the separator to `T`, use at least four year digits and
remove unnecessary seconds/fractional trailing zeros. Date/month/week/time retain
the spelling of otherwise valid values, as Chrome does. The parser is independent
of locale and time zone; numeric conversion uses milliseconds, except month
values which count months from January 1970.

[Seventy-one Chrome observations](verification/temporal-values-browser.json)
cover leap years, ISO week 53, leading year zeros, fractional seconds and the
browser's upper date boundary (275760-09-13). Core tests check parsed numbers,
sanitation, markup defaults, reset, type changes and C API assignment. This fixes
value representation; it does not add a native calendar picker or implement
`valueAsDate`/`valueAsNumber` public APIs.

Temporal min/max/step enforcement remains pending. The next
[80 browser observations](verification/temporal-constraints-browser.json) record
step behavior and overnight time ranges; they are evidence for follow-up work,
not passing implementation tests. Candidate110 and installed109 do not contain
this new sanitizer.

Temporal sanitization passes all 10 core suites and 12 sanitizer gates; see
[core evidence](verification/temporal-values-core.json).

### Temporal constraint validation (core source after candidate110)

Temporal inputs now enforce `min`, `max` and `step` during validated submission.
Calendar steps round to whole days/months/weeks; time and local-datetime steps
round after conversion to milliseconds. A valid minimum sets the step base,
otherwise the original value attribute does, otherwise the type's default base is
used (Monday 1969-12-29 for ISO weeks). Invalid/nonpositive step values use the
type default, and `any` disables step mismatch checking.

Time ranges may cross midnight: `min="22:00" max="05:00"` accepts midnight and
both endpoints, while noon is outside both bounds. Other temporal types retain
independent lower/upper checks. Numeric and temporal inputs share decimal step
arithmetic; integer temporal units use exact remainder comparisons.

The [initial 80 cases](verification/temporal-constraints-browser.json) and
[195 edge/base/range cases](verification/temporal-step-extra-browser.json) now run
as core validity tests. [550 Chrome submissions](verification/temporal-submissions-browser.json)
match C API submission tests, including invalid notification, dialog retention
and `novalidate` bypass. Earlier notes describing these observations as pending
refer to the sanitizer-only checkpoint.

Native rebuild, exports and performance qualification remain pending for this
source. Calendar editing, public numeric date APIs and bad-input reporting are
separate remaining work; these checks do not certify them.

Temporal constraints pass all 10 core suites and 12 sanitizer gates; see
[constraint evidence](verification/temporal-constraints-core.json).

### Native temporal qualification (candidate111)

The canonical Godot validation fixture includes all 71 browser value cases and
550 submissions. Candidate111 passes 15,762 checks across 29 headless entries;
its 6,017 validation checks also pass with OpenGL and Vulkan. Those checks cover
value readback, invalid signals/handlers, submit suppression, default reporting,
reentry suppression, dialog results and `novalidate`. They do not test a native
calendar picker or temporal widget pixel parity.
[Native receipt](verification/temporal-validation-candidate111.json).


### Saturated exponent sanitization (installed preview119)

Number values now reject non-digit suffixes after arbitrarily large exponents.
Previously, `0e9999junk` survived value sanitization because Decimal returned zero
before reading the suffix. The value path checks the complete exponent before
numeric conversion, including overflow/underflow, signs, whitespace, embedded NUL,
non-ASCII digits, and very long digit sequences. Ordinary numeric results and
constraint attribute parsing are unchanged.

Chrome152 rejects those malformed current/default values but still interprets
some saturated malformed `min`/`max` attributes as zero. The implementation keeps
that observed distinction; this is not a claim that Chrome's attribute edge cases
conform to every HTML parsing requirement. The vendored Decimal code is unchanged.

All 70 browser-derived cases run in core tests. The Godot fixture tests the 65
cases representable through its string API; five embedded-NUL cases remain core
only. Godot rejects embedded NUL strings and the current C ABI uses NUL-terminated
strings. Preview119 is installed after all72 desktop timing gates and installed-copy checks passed.
[Exponent evidence](verification/exponent-sanitization119.json).


### Validity snapshots (introduced in candidate120; installed in preview121)

`get_element_validity(selector)` returns a snapshot for an input, textarea,
select, or button. It reads pending DOM/value changes without updating layout,
moving focus, or firing `invalid`. The dictionary has `valid`, `will_validate`,
`value_missing`, `type_mismatch`, `pattern_mismatch`, `too_long`, `too_short`,
`range_underflow`, `range_overflow`, `step_mismatch`, `bad_input`, and `custom_error`.
Read it again after edits; a previously returned dictionary is not a live object.
A disabled control may retain errors even though `will_validate` is false.

```gdscript
func name_can_be_saved() -> bool:
    var state := ui.get_element_validity("#name")
    if state.is_empty():
        return false
    return not state.will_validate or state.valid
```

Missing selectors and unsupported element types return an empty dictionary.
A nonempty value with an applicable `pattern` also returns empty and emits a
warning, because Unicode-v pattern validation is not implemented. It must not
be treated as a valid result. Empty values do not require pattern matching;
`required` still applies. This API does not yet implement form-wide
`checkValidity()`/`reportValidity()`, localized validation messages, or validity
pseudo-classes. Ordinary submission validation retains its existing behavior.
The C ABI equivalent is `weva_element_validity`, returning status plus an error
bitmask and a validation-candidate flag; both outputs are cleared on failure.

[Snapshot verification](verification/validity-snapshot120.json).


### Explicit checking and reporting (installed preview121, ABI minor18)

`check_validity(selector)` and `report_validity(selector)` accept a form or an
input/textarea/select/button. They return whether eligible controls satisfy the
supported constraints, fire cancelable `element_invalid`/`on-invalid` events for
failures, and never submit. They ignore `novalidate` and `formnovalidate`; those
attributes govern submission, not an explicit check. Form checks include external
controls associated through `form="id"`, in document order. Disabled/barred
controls pass without invalid events.

`check_validity` preserves focus. `report_validity` focuses the first unhandled
invalid control after invalid handlers finish; canceling every invalid event
prevents that focus change without making the result true. Reporting currently
provides focus and handler hooks, not a built-in localized validation popup.

```gdscript
func save_settings(_id: String) -> void:
    if not ui.report_validity("#settings"):
        return
    save_settings_to_disk()
```

The C ABI equivalents return a status and a required `int* valid` output. Invalid
events are queued; the caller must drain `weva_document_poll_event` and may call
`weva_document_prevent_default` for the current event. Godot drains automatically.
When called inside a normal event handler, invalid handlers run after that handler
returns, within the same outer drain. This differs from browser recursive event
dispatch. Calling again from an active invalid handler is explicitly unsupported
(`WEVA_ERR_INVALID_STATE`; Godot warns and returns false). Pattern-dependent
checks likewise fail explicitly before events are queued. Missing/non-control
selectors return false in Godot. The bounded native event queue rejects requests
that cannot fit all protected invalid events; it does not emit a partial report.

Control mutation during earlier handlers is rechecked before each queued invalid
event: repaired, disabled, removed or reassociated later controls are skipped.
Tests cover these cases against Chrome, plus cancellation, valid forms, no
submission, Save-button callbacks, unsupported patterns and queue capacity.
[Explicit validation evidence](verification/explicit-validity121.json).


### Validation refresh and focus handoff (installed preview124)

Notification-only invalid events no longer mark the host dirty. The host compares
the core interaction input version with the version consumed by its last update,
so pending default focus changes still reach layout and paint after invalid
handlers finish. This also handles a focus round trip: a handler can move away,
flush, and reporting can return to the original field without losing that final
state. Comparing only starting/ending focus identity was insufficient.

Reporting from a native Godot button now transfers native keyboard focus to the
HTML UI as well as moving the internal focus target. Regression checks cover both
focus layers and a focus-dependent width change reaching the next frame. These
changes do not remove native IME activation costs or provide recursive validation.
The C ABI exposes `weva_document_interaction_version` in minor19 for host update
synchronization; it is read-only and compared as an input-version key.

### Range styling (installed preview125)

The core and Godot host now match `:in-range` and `:out-of-range` for number,
range, date, time, month, week and datetime-local inputs. Disabled, read-only and
non-candidate controls match neither. Empty number/temporal values match in-range;
nonempty values without a valid bound match neither. Range inputs are sanitized
into their bounds. Step mismatch and requiredness do not change range matching.
Reversed time bounds describe the interval crossing midnight.

The Chrome fixture covers 195 states. Live value, bound and disabled changes
restyle the field, including parent `:has()` rules; nested range selectors also
participate in cache keys. Form mutations request restyling only when the sheet
uses range selectors; sheets without them keep the existing fast path.
This does not implement `:valid`, `:invalid` or user-validity selectors.

### Stable range updates (installed preview126)

The host retains the range-selector result consumed by its last cascade.
A value update that remains in the same range state still updates the field text,
but does not restyle a parent `:has(:out-of-range)` rule unnecessarily. Crossing
a bound still requests cascade and any layout implied by authored CSS. The saved
state is updated by cascade, not by intervening paint-only work.

`hosts/godot/run_range_perf.py` measures single-field updates on 12/48-field forms,
with no range rules, field width rules, and parent `:has()` width rules. It checks
field and parent geometry each frame outside the timed mutation/update interval.
This is diagnostic API CPU evidence, with native input disabled; it excludes
setup, draw submission and GPU cost and has no timing acceptance gate.

Preview126 also removes the redundant public value-setter restyle request and
compares only bounds for range matching, avoiding step-validity arithmetic.
[Performance and qualification](verification/stable-range126.json).

The range performance runner now includes field and parent background highlighting
as well as width changes (20 scenarios per run). It checks distinct in/out colors,
current field values and stable geometry outside the timing interval. Direct
highlighting is substantially cheaper than a parent `:has()` rule on the tested
form; the latter still forces broad cascade work. See
[diagnostic127](verification/range-paint127.json).

### Cache scope for relational selectors (installed preview128)

A stylesheet containing `:has()` no longer disables selector-match caching for
unrelated subjects. The compiler records mandatory tag/class/id keys on the
rightmost subject of each dependent rule. Elements excluded by those keys retain
the ordinary input-keyed cache; possible subjects remain uncached. Rules with
unkeyed or nested subjects conservatively keep the previous behavior. This does
not narrow the full cascade walk or cache a relational match without its inputs.

[Cache-scope tests and measurements](verification/has-cache128.json) retain the
remaining full-cascade cost and the scoped timing limits.

The relational range regression matrix compares 105 incremental draw lists against
full rebuilds with cleared match caches, preserving the same live form state.
Coverage includes inherited colors/currentColor, CSS variables, descendant and
sibling effects, stylesheet replacement, disabled states, removal and reload.
[Verification129](verification/relational-render129.json) is core rendering evidence;
it does not claim GPU or browser equivalence.

### Popover stack dismissal

The installed popover implementation now dismisses incompatible sibling menus and closes
automatic/hint submenus with their parent. Manual popovers remain independent.
Opening ancestry includes an invoking button, so a submenu can live elsewhere
in the DOM; reopening it independently discards its former parent relationship.
The 54 Chrome chain scenarios cover middle-menu closing and reopening through
378 state comparisons. All 28,556 native host checks pass. All 16 sanitizer gates and package/export checks pass; qualification is tracked in
[the popover receipt](verification/popover-stack.json).

### Dialog initial focus

The installed popover focus implementation supports autofocus and restores focus
when explicitly closing the first automatic/hint popover in a stack. Opening a
replacement menu suppresses that restoration. A plain popover without autofocus
keeps current focus; a dialog used as a popover follows dialog focus selection.

```html
<button popovertarget="item-menu">Item actions</button>
<div id="item-menu" popover>
  <button autofocus>Equip</button>
  <button>Drop</button>
</div>
```

Here opening moves focus to Equip. Remove `autofocus` to leave focus on the
trigger. Explicitly closing the automatic menu restores its saved focus only
while focus is still inside it; it does not steal focus from another control.
Manual popovers do not restore saved focus. Godot clears focus from a hidden
manual field in the API call; Chrome clears it after rendering, so immediate
blur timing is not equivalent. The settled states and replacement/nested cases
are covered by [the focus comparison](verification/popover-focus.json).

Modal and nonmodal opening preserve Chrome's initial focus behavior for negative,
zero and positive `tabindex`, including explicit autofocus. Disabled and hidden
autofocus buttons are skipped. Initial input and textarea selections remain
collapsed at zero. Twenty additional assertions pass on the installed native
addon; the dialog suite now has 574 checks. These checks verify existing behavior
and do not establish beforetoggle or complete browser event timing conformance.
[Chrome and native evidence](verification/dialog-initial-focus.json).

### Cascade allocation reduction (installed preview130)

Cascade application borrows its collected declaration list instead of copying it.
The list stays valid through declaration application and keyword rollback; those
passes do not collect matches again. Property-resolution passes use an engine-owned
scratch vector for their set-property snapshots, retaining the original ascending
order and snapshot behavior when declarations change during resolution.

A standalone allocation counter measures zero allocations across 1,000 warmed
computations of a simple cached three-property rule, versus 4,000 before these
changes. This is a focused cascade budget, not a zero-allocation claim for all CSS
or for the complete UI pipeline. Full-versus-incremental rendering tests remain
required for broader cascade changes.

[Allocation and runtime qualification](verification/cascade-allocations130.json)
records the focused allocation budget and remaining cascade cost.

### Validity styling (introduced in preview131; installed preview132)

`:valid` and `:invalid` now use the existing required, type, length, numeric,
temporal, bad-input and custom-error checks. Barred controls match neither.
Forms aggregate controls by form ownership, including external `form=id`
controls. Fieldsets aggregate descendant controls regardless of form ownership.
Empty forms and fieldsets are valid. `novalidate` does not disable CSS validity.

```css
input:invalid { border-color: #c85143; }
form:invalid .save-hint { color: #c85143; }
fieldset:has(input:invalid) { background-color: #301d18; }
```

Fieldsets now accept custom-validity messages and expose their validity object.
Their own custom error does not affect `:valid`/`:invalid` aggregation, and they
remain barred from interactive validation. The UA stylesheet also gives
fieldsets their standard block display.

A nonempty applicable `pattern` remains unsupported. If it is the only unresolved
constraint, neither validity selector matches that control or an aggregate whose
validity depends on it; the matcher emits a diagnostic once per thread. A known
invalid constraint still makes the subject invalid. This is an explicit limitation,
not pattern conformance. `:user-valid` and `:user-invalid` remain unimplemented.

Validity results are cached against the tree's monotonic mutation version because
form ownership and radio groups can reach outside a control's subtree. Mutation
stamps are applied before observers, including to removed subtrees, so reentrant
queries and detach/reparent cannot reuse an old result. Match-cache keys include
validity for the subject and ancestors; changed validity takes the existing full
cascade walk without clearing unrelated match caches. Clean frames do no work,
and same-validity edits retain the control-content path.

The Chrome fixture has 105 scenarios (104 supported selector scenarios plus an
explicit unsupported-pattern case). The Godot suite has 658 checks, including live
external-owner and fieldset updates. Core coverage also compares 120 additional
incremental draw lists against full rebuilds. Run `run_range_perf.py` with
`--selector-family validity` to exercise stable and changing validity across
12/48-field settings forms; its timings remain diagnostic, not release gates.

### Normalized model refreshes (installed preview132, ABI minor20)

A model value can differ in spelling while describing unchanged control state:
checked checkboxes read back as `on`, and range values are rounded and clamped.
`refresh_bindings()` now compares the control's form-state input version when
applying a model. It does not report a change or redraw for an unchanged normalized
value. Validity/edit-source changes and active composition commits still publish.
`weva_element_form_version` exposes that input version to native hosts; compare
within one document lifetime and element identity, not across reloads.

Short control values and attributes are read into a stack buffer; longer values
retain the complete dynamic-buffer fallback. Unchanged `data-class-*` bindings
check membership before allocating deferred changes. A standalone allocation gate
measures zero allocations for 1,000 unchanged class refreshes, versus 2,000 before.
Resolver calls and changed-token application remain in order.

`WEVA_GODOT_BINDING_PROFILE=1` optionally logs core binding and model-update time.
It is off by default and diagnostic runs include its measurement/logging overhead.

</details>
