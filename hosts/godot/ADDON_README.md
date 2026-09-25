# Weva for Godot — development preview

Write a game's UI in HTML and CSS and connect it to GDScript with data bindings
and controller methods. This preview targets Godot 4.7 desktop builds. The
archive's `build.json` lists the platform libraries actually included.

**What has been tested.** Windows/Linux desktop builds, including sample
rendering and exports with recorded editor/template configurations, and one
desktop GPU (an RTX 5080) for timing profiles. macOS is not built.
Mouse, keyboard and gamepad input are verified with synthetic events; a
physical controller and touch screen still need acceptance testing. Linux IME
has limited physical evidence; Windows IME and lower-end hardware are unverified.
Weva documents work around the tested stock engine's long-text defect. Native
Godot controls require the patched editor and matching export templates for
that fix (see `GODOT_TEXT_SHAPING.md`).

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
Sorting the same unique explicit keys preserves the existing rows, including
focus, selection and undo history. Membership changes, duplicate/missing keys
and externally interleaved rows keep the reconstruction path.

`ui.load_files("res://ui/hud.html")` supports programmatic loading. It returns
a Godot Error and fills `last_load_error` if a file cannot be opened, preserving
the current UI. Files load at scene startup or on explicit calls, and are
reloaded when they change on disk while the game runs (see below).

For gameplay behind a HUD, set `pointer-events: none` on `html`, `body` and the
HUD root, then `pointer-events: auto` on panels/controls. Put game actions in
`_unhandled_input`. Use CSS width/height for image sizing in this preview.

For a complete settings panel with typed bindings, click callbacks and reset
handling, see [Form authoring](FORM_STATE.md#authoring-a-settings-panel).

## Edit while it runs

`WevaView` watches its HTML and CSS files while the game runs and reloads
them when they change on disk (`live_reload`, on in debug builds such as the
editor's F5 and off in exported releases; `live_reload_interval` sets the
poll, 0.5 s by default). Save the stylesheet and the change shows without a
restart, keeping the document, its state and bindings; save the markup and
the HTML reloads with the bound data re-applied. A file is read once its
modified time has stayed put for one interval, so a half-written save is
skipped. `files_reloaded(markup_changed, stylesheet_changed)` fires after
each reload.

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
layout and glyphs. Register a named face with `ui.register_font_family("Camp", font)`
and select it using CSS `font-family: Camp`. CSS still sets size and line height.

Stylesheets can also declare fonts the standard way:

```css
@font-face { font-family: "Camp Display"; src: url("fonts/display.ttf"); }
h1 { font-family: "Camp Display", sans-serif; }
```

Linked/imported stylesheets resolve `src` URLs beside the stylesheet; inline
CSS uses `base_path` (the markup's directory in `WevaView`). Loading uses the importer when the font
is an imported resource and from the raw file otherwise. The rule with normal
weight and style is the family's regular face; a rule with `font-weight: 700`
(or 800 and above) or `font-style: italic` is a real file for that weight or
slant, and text at that weight or slant draws it. The nearest file serves a
weight no rule covers, and one axis is synthesized over the other's file when
only that file exists; with no such rules bold and italic are synthesized.
`local()` tries an installed font; ordered sources fall through when unavailable.
The same files can be supplied from a script
with `ui.register_font_face("Camp", bold_font, 700, false)`. A family or face
the game registered itself keeps its font. A source that cannot load warns and
the family falls back. See `GODOT_TEXT_SHAPING.md` for details.

## Export

Add `*.html,*.css` to the export preset's non-resource include filter. Use
**Export all resources**, or explicitly include textures named only in markup.
Use `res://` and relative paths for shipped artwork. The example sets
`base_path` to its own directory. Godot's imported PNG/SVG resources are tested
from exported packs; `ui.get_missing_assets()` reports unresolved image paths.

Only export to platforms whose native library is present in `build.json`.
Install templates matching the chosen editor build. The latest complete native
text/export checks use a patched Linux editor and matching debug/release
templates; older stock-engine sample passes cover only their recorded fixtures.
See `GODOT_TEXT_SHAPING.md` for tested configurations. Godot copies the extension
library into the export; distribute the complete exported directory and verify
it on the target platform. This is a development preview.

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

A controller works without any script once the node holds Godot focus:
call `ui.grab_focus()` when a screen opens, or set `gamepad_wake = true` on
a menu screen so that, while no Control holds focus, the first pad press or
stick push wakes it on its first control and is spent doing so. Leave wake
off on a HUD that stays on screen during play, or the movement stick would
open it; while a document holds focus it consumes the pad, so release focus
(`ui.release_focus()`) when a menu closes and play resumes. Joypad events
then answer as the keyboard they stand in for. `ui_left/right/up/down` (the D-pad and left
stick by default) move focus by geometry, so a grid of buttons reads as a
grid; a slider, radio group or text caret takes left/right first, and a
`<select>`, `<textarea>` or number field takes up/down, so a settings screen
moves between rows vertically and adjusts a row horizontally. `ui_accept`
is Space on a control; on a text field it asks for text entry (below);
`ui_cancel` is Escape and is consumed only when it closed something, so your
own back action still fires.
Actions with no joypad binding fall back to A, B, the D-pad and the
shoulders; map them in the InputMap to change that. A held direction
repeats after 400 ms at ten steps a second, like a held arrow key. Consumed
presses are handled, releases pass through. `gamepad_navigation = false`
leaves every joypad event to the game; `focus_move(direction)` remains for
custom schemes.

A controller cannot type, so a pad accept on a focused text field opens
`WevaView`'s on-screen keyboard along the bottom of the view: itself an HTML
document the pad navigates (letters, digits, a symbol page, Shift, Space,
Back, Enter, Done), typing into the field through the same text path a
physical keyboard uses. The field keeps its focus and caret meanwhile, Enter
acts as the key would (a newline in a textarea, an implicit submission in a
form) and closes it, Done or cancel closes it, and the pad returns to the
view with the field still focused. `on_screen_keyboard_height` sets its
share of the view and `on_screen_keyboard_css` restyles it (`.key`,
`.key.wide`, `:focus-visible`). Set `on_screen_keyboard = false` and answer
the `text_entry_requested(id)` signal yourself for a platform keyboard, or
`gamepad_text_entry = false` to make accept plain Enter.

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

Stock Godot 4.7 has a text-shaping defect above 32 separate emoji runs (and
above 128 open brackets) that can corrupt positions or crash, including without
this addon. This addon shapes such text in pieces the engine handles, so
document text with unrestricted Unicode works on the stock editor and templates;
text in native Godot controls still goes through the engine unchanged. A
patched editor and matching export templates provide the verified fix for that.
Enable `internationalization/locale/include_text_server_data` in your project.
Godot's TextServer data is separate from Weva's embedded select-search ICU data.
Read `GODOT_TEXT_SHAPING.md` before using unrestricted Unicode text in a release.

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
`docs/PRODUCT_READINESS.md` for current readiness, requirements and limitations.

## Licenses

Weva and godot-cpp are MIT licensed. Their notices are included beside this
file. The sample uses Godot's theme font and a bundled SVG; it includes no
third-party font assets.
The native libraries contain Unicode character data under Unicode License V3;
see `UNICODE_LICENSE.txt` and `UNICODE_DATA.md`.
Select search embeds ICU 78.3 and its data; see `ICU_LICENSE.txt` and
`ICU_DATA.md`. No separate ICU installation is needed.

Unsupported CSS at-rules produce warnings when replacing stylesheets.
Use `get_css_diagnostics()` to inspect the current compiled rules without
updating the document; requires ABI minor 24. See [CSS diagnostics](CSS_DIAGNOSTICS.md).
