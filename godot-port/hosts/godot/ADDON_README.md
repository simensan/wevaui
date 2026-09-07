# Weva for Godot — development preview

Write a game's UI in HTML and CSS and connect it to GDScript with data bindings
and controller methods. This preview targets Godot 4.7 desktop builds. The
archive's `build.json` lists the platform libraries actually included.

## Install and run

1. Extract the archive into your Godot project's root. It adds `addons/weva/`.
2. Open the project in Godot so it discovers the native extension and imports artwork.
3. Open `addons/weva/example/example.tscn` and run the current scene (F6).

The example lets you edit a player name and change a score. Its HTML, CSS and
GDScript are all in `addons/weva/example/`; no files outside your project are
needed. No plugin needs enabling and no .NET runtime is required.

## Connect a game in three steps

1. Add a **WevaView** node under your game's Control and choose `html_file` in
   the Inspector. It loads the same-name `.css` automatically and resolves
   artwork beside the HTML. Set `css_file` for a different stylesheet.
2. Keep a shared Dictionary and a zero-argument `changed` signal in game state.
   Bind once in the parent scene's `_ready()`:

   ```gdscript
   $UI.bind_state(state.model, self, state.changed)
   ```

3. Name actions and data in the HTML:

   ```html
   <p>Health: {{ Player.Health }}</p>
   <input data-model="Player.Name" maxlength="24">
   <button id="heal" on-click="heal" disabled="{{ View.HealDisabled }}">Heal</button>
   ```

   ```gdscript
   func heal(_element_id: String) -> void:
       state.model.Player.Health = 100
       state.changed.emit()
   ```

`WevaView` is an optional GDScript helper over `WevaDocument`; native methods
still work. Game changes refresh once after the callback, coalescing multiple
notifications. Idle frames do not poll data. Use `ui.flush_bindings()` before
an immediate geometry read, or `ui.request_refresh()` without a change signal.
Rebinding detaches the previous signal; destruction disconnects automatically.

Player edits write into the shared Dictionary before `data_changed(path, text)`
fires. Read the Dictionary for typed ints/bools; the signal carries control text.
Connect it to apply changes to native Godot systems. Dictionary mutations alone
do not emit a signal: game code must emit `changed` or request a refresh.

Templated boolean attributes such as `disabled="{{ Locked }}"` bind presence:
false, 0, empty or missing values remove the attribute; true adds it. Literal
`disabled="false"` still disables a button under HTML rules. String attributes
such as `aria-disabled` stay strings. Use `data-model` for writable controls;
`checked` and `selected` supply defaults.

Use `<template data-each="Items as item" data-key="Id">` for repeated rows.
Inside, `id="use-{{ item.Id }}"` identifies a button (use CSS-safe item keys).
`ui.get_row("#" + id)` returns its stable `key` and current `index`. Select live
rows with `#inventory > .item`; `.item` also matches retained template contents.
Sorting currently rebuilds rows, so focus/selection need not survive a reorder.

`ui.load_files("res://ui/hud.html")` supports programmatic loading. It returns
a Godot Error and fills `last_load_error` if a file cannot be opened, preserving
the current UI. Files load at scene startup or on explicit calls; there is no
file watcher in this helper.

For gameplay behind a HUD, set `pointer-events: none` on `html`, `body` and the
HUD root, then `pointer-events: auto` on panels/controls. Put game actions in
`_unhandled_input`. Use CSS width/height for image sizing in this preview.

## Native document API

Create a `WevaDocument` Control from GDScript, set `html` and `css`, then add
it to the scene. Unsized documents fill their parent with anchors; `size`
(also exposed as `document_size`) sets an explicit HTML viewport. Godot
Containers can size it directly. Assigning `css` replaces the previous
stylesheet while preserving form values and focus. `ui.data` accepts a
Dictionary; `{{ Player.Name }}` reads it and `data-model="Player.Name"` writes
player edits back. `ui.set_controller(self)` routes an HTML
`on-click="award_point"` to `award_point(_element_id: String)` on your script.

The document uses its inherited Godot theme font. To select a project font,
use `ui.add_theme_font_override("font", preload("res://fonts/interface.ttf"))`
or assign a Theme with a default font. Live Font resource changes refresh
layout and glyphs. CSS still sets font size and line height; per-element CSS
family selection remains unfinished. See `GODOT_TEXT_SHAPING.md` for details.

## Export

Add `*.html,*.css` to the export preset's non-resource include filter. Use
**Export all resources**, or explicitly include textures named only in markup.
Use `res://` and relative paths for shipped artwork. The example sets
`base_path` to its own directory. Godot's imported PNG/SVG resources are tested
from exported packs; `ui.get_missing_assets()` reports unresolved image paths.

Only export to platforms whose native library is present in `build.json`.
Install Godot's export templates matching your editor version. Native x86_64
debug, release and embedded-pack exports pass on Windows and Linux with the
standard Godot 4.7.2 build, including the example's rendering, controller
actions and bindings. Godot copies the extension library into the export;
distribute the complete exported directory. This is a development preview.

## Current integration limits

`WevaDocument` now derives from `Control`. Projects using an earlier preview
should change any `Node2D` type annotations for it to `WevaDocument` or
`Control`. Pointer input respects native overlays, visibility, canvas
transforms and CSS `pointer-events`. Typing reaches the focused document;
Tab and Shift+Tab walk HTML controls, then continue through Godot's focus
chain. Put sibling UI beneath a common Control, or configure native focus
neighbors explicitly. Use `interactive = false` and explicit input methods
when your game owns routing. Game actions belong in `_unhandled_input` so
accepted GUI events do not also trigger gameplay.

Held edit keys, Unicode key events, Ctrl/Cmd+A/C/X/V/Z and redo are routed.
Buttons activate on Enter down or Space release. Checkbox/radio Space,
radio arrows, slider arrows/Home/End/PageUp/PageDown, summaries and popover
triggers work through the same HTML handlers as pointer clicks. Enter in a
form field activates its default submit button before submitting.

IME preedit, commit/cancel, one-step undo and composition signals are
implemented. Windows/Linux Godot integration tests pass; real Linux X11
Pinyin has passing runs with `IBUS_ENABLE_SYNC_MODE=0`, but stock Godot 4.7.2
has an intermittent X11 commit loss. IBus 1.5.29 also misses synchronous
preedit completion. Local engine/input-method fixes pass repeated native
tests; these fixes are not part of this addon. Real Windows IME is unverified.
See `IME.md` for the API, evidence and compatibility limits.

Long text fields scroll to keep the caret visible. Unicode navigation and
deletion preserve emoji sequences, and passwords mask one grapheme per bullet.
Dragging and holding near or beyond a field edge scrolls and extends the
selection, including both textarea axes. This stays responsive when CSS or
game simulation time is stopped.
Text inputs and textarea enforce HTML `maxlength` during editing, including
paste and composition commit. Script values may exceed it. Native paste and
`ui.paste_text(text)` form a separate undo step and normalize line endings.
See `TEXT_EDITING.md` for the browser profiles and remaining editing limits.

Stock Godot 4.7.2 has a text-shaping defect above 32 separate emoji runs that
can corrupt positions or crash, including without this addon. A candidate
engine fix passes isolated tests but is not included here. Read
`GODOT_TEXT_SHAPING.md` before using unrestricted Unicode text in a release.

Form values retain their markup defaults. A `type="reset"` button or
`ui.reset_form("#settings")` restores the form and its writable `data-model`
paths, then emits `form_reset`. Use `value`, textarea text, `checked` and
option `selected` to supply defaults. See `FORM_STATE.md` for details.

Select listboxes support native click/drag selection, Ctrl/Meta toggling,
Shift ranges and keyboard navigation. `size="1"` opens a dropdown. Explicit
input accepts `ui.set_pointer(point, buttons, modifiers)`; the optional third
argument uses Shift=1, Ctrl=2, Alt=4 and Meta=8. See `FORM_STATE.md` for the
event timing, default sizing and remaining select limitations. Type to search
option labels, repeat a letter to cycle choices, or pause one second to start
a new search. Accents and Unicode collation equivalents are supported.

Broader IME compatibility, touch-device behavior, remaining form controls
and constraints, and accessibility still need work.

The HTML/CSS engine implements a web subset, not a browser runtime: there is
no JavaScript engine or network resource loader. Form and font metrics still
have documented browser differences. See the source repository's
`godot-port/docs/PRODUCT_READINESS.md` for the complete release ledger.

## Licenses

Weva and godot-cpp are MIT licensed. Their notices are included beside this
file. The sample uses Godot's theme font and a bundled SVG; it includes no
third-party font assets.
The native libraries contain Unicode character data under Unicode License V3;
see `UNICODE_LICENSE.txt` and `UNICODE_DATA.md`.
Select search embeds ICU 78.3 and its data; see `ICU_LICENSE.txt` and
`ICU_DATA.md`. No separate ICU installation is needed.
