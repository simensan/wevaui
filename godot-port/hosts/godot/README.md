# The Godot host

A GDExtension that binds `libweva`'s C ABI and draws its geometry through a
`Node2D`. It talks to the core through `weva_c.h` only — no Godot type reaches
the core, and no core C++ type reaches Godot. That is the property that lets the
same core serve a Unity host later, and it is worth keeping loudly true.

## Building

`godot-cpp` is not vendored: the core builds and tests with no Godot dependency
at all, and vendoring would quietly end that.

`godot-cpp` is version-coupled to the engine. It publishes a branch per release
(`4.5`, and older) but `master` also ships the bundled API descriptions for
newer versions, which is how a 4.7 build is produced today:

```sh
git clone --depth 1 https://github.com/godotengine/godot-cpp
cmake -S godot-cpp -B godot-cpp/build -G Ninja \
      -DCMAKE_BUILD_TYPE=Release -DGODOTCPP_API_VERSION=4.7
cmake --build godot-cpp/build -j
```

Set `GODOTCPP_API_VERSION` to the engine you target. If your engine is newer
than anything bundled, dump its own description and point at that instead —
this always matches, whatever the version:

```sh
godot --headless --dump-extension-api --dump-gdextension-interface
cmake -S godot-cpp -B godot-cpp/build -G Ninja -DCMAKE_BUILD_TYPE=Release \
      -DGODOTCPP_CUSTOM_API_FILE=$PWD/extension_api.json
```

Then the host:

```sh
cmake -S hosts/godot -B build-godot -G Ninja \
      -DCMAKE_BUILD_TYPE=Release -DGODOT_CPP_DIR=$PWD/godot-cpp
cmake --build build-godot -j
```

The library lands in `project/addons/weva/bin/`, where `weva.gdextension`
expects it. `GODOT_CPP_BUILD_DIR` overrides where the built `godot-cpp` archive
is looked for, since the SCons and CMake builds put it in different places under
different names.

Verified against Godot 4.7.2 with `godot-cpp` master at API 4.7. An extension
built against an older `godot-cpp` does load in a newer engine — the 4.3 build
ran fine under 4.7.2 — but build against the version you ship on.

### `.godot/extension_list.cfg`

It is checked in on purpose. The editor generates it on first import, and
without it the engine loads no GDExtension at all — a headless or CI run then
fails with `Could not find type "WevaDocument"` and no hint that an extension
was even meant to load.

## Rebuilding while Godot is open

On Windows a mapped DLL cannot be overwritten, so a rebuild fails with
`LNK1104: cannot open file` while anything has the extension loaded. Linux
never has this: replacing a mapped `.so` is legal, which is why the same
workflow only ever bites on Windows.

`weva.gdextension` sets `reloadable = true`, so the **editor** drops the
library when it loses focus. Alt-tab to a terminal, rebuild, alt-tab back, and
the editor picks up the new binary. That is the whole workflow.

A **running game** is different and no flag changes it: the process has the
library mapped until it exits, so `F5` or `godot --path project` has to be
closed before a rebuild. Which is fine, because that is also the thing you were
about to restart to see the change.

If a build fails and no Godot window is visible, something still has it open --
a project manager, or an editor for another project that once loaded this
extension. This finds it:

```powershell
Get-Process | Where-Object { $_.Modules.FileName -contains
    'C:/.../project/addons/weva/bin/weva_godot.dll' }
```

## Looking at the samples

```sh
godot --path hosts/godot/project
```

`gallery.tscn` is the project's main scene: a list of every page in the oracle's
sample corpus down the left, the selected one rendered live by the C++ engine on
the right. It reads the corpus in place rather than copying it in, so what you
see is the same file both gates measure.

| key | |
| --- | --- |
| `←` `→` | previous / next sample |
| `P` | full page (see below) |
| `F` | engine font or the core's stub face |
| wheel, `PgUp` / `PgDn` | scroll, in full-page mode |
| `Esc` | quit |

Pages are laid out at **1280×720**, which is what the corpus is captured at and
therefore what the gates measure. Painting stops at the viewport, so a page
taller than that is not merely scrolled off — it is never drawn, and 18 of the
35 samples reach past it (`audit-validation` gets to 2767px). `P` lays the page
out again in a viewport tall enough to hold it and fits it to the width so it
can be scrolled. That is deliberately a mode you ask for rather than the
default: a taller viewport is a *different document*, since percentage heights
and `vh` units all move with it.

The header shows the draw and triangle counts, which face is in use, and how far
the page reaches when that is past the viewport.

## Running everything

`godot-port/check.sh` runs every gate in the order that fails fastest, and
exits non-zero when any of them does:

    bash godot-port/check.sh            # or --clean to rebuild from scratch

    === unit tests ===            10124 checks, 0 failures
    === sanitizers ===            10124 checks, 0 failures
    === layout oracle ===         21/37 agree, 0 differ, 16 reference bugs
    === backend gate ===          37 samples, 0 over the structural gate
    === interactive gate ===      five states, all 0.00%
    === host tests ===            165 checks, 0 failures

A build directory it cannot find is SKIPPED with a line saying so rather than
passing quietly, and `WEVA_BUILD_GCC`, `WEVA_BUILD_CLANG`, `WEVA_BUILD_GODOT`
and `GODOT_BIN` point it at yours. Each gate catches what the others cannot,
which is why they are all here: the sanitizers alone caught a double free, a
premature free and a use-after-free in the element table in one session, none
of which the ordinary tests noticed.

## Running the render tests

```sh
godot --headless --path hosts/godot/project --scene res://test_scene.tscn --quit-after 2
```

`render_tests.gd` exits non-zero on failure, so it works in CI. The scene is
named explicitly because the main scene is the gallery.

These are deliberately **not** a re-test of the layout engine — libweva's own
suite covers that far better, and duplicating it here would mean two places to
update for one change. What only Godot can prove is that the binding works:
that geometry crosses the ABI intact, that element queries return what layout
computed, that a restyle round-trips, and that an empty or malformed document
does not take the host down.

Headless still validates every canvas command even though it rasterises
nothing, which is enough to have caught `draw_polygon` being handed a triangle
soup.

## Comparing the two backends

`compare_render.py` renders one document twice — once through libweva's own
software rasteriser, once through Godot — and compares the images:

```sh
python3 hosts/godot/compare_render.py page.html page.css --size 300x220
```

Both sides consume the *identical* draw list from the same libweva build, so a
difference is a difference between the two backends with the whole cascade,
layout and tessellation pipeline held fixed. That is the check
`ARCHITECTURE.md` §1 asks for, and it is worth more than either renderer's own
tests: it is the only thing that can tell you the render interface is at the
right altitude, because a wrong altitude shows up as one side being unable to
reproduce the other.

On a machine with no GPU, Mesa's software path works:

```sh
LIBGL_ALWAYS_SOFTWARE=1 xvfb-run -a -s "-screen 0 800x600x24" \
    python3 hosts/godot/compare_render.py page.html page.css --size 300x220 \
    --godot /path/to/godot
```

Blocks, borders, rounded corners and text currently come out **pixel-identical**
between the two. Exact equality is not the standing bar — two rasterisers are
entitled to disagree at edges — so only ink coverage gates, and channel
differences are reported rather than enforced.

This comparison has already earned its keep twice: it found that Godot's
default linear texture filtering both softens glyphs the core drew crisply and
samples across shelf boundaries in the packed atlas, and it found a real core
bug where the glyph atlas uploaded lazily per text run, releasing the texture an
earlier draw still referenced (`test_abi_texture_ids_are_all_published` now pins
that). Godot only looked correct there because it binds its single atlas
regardless of the id; a host that mapped ids faithfully drew solid blocks where
the text should be.

## What the node does, and does not

`WevaDocument` exposes `html`, `css` and `document_size` as properties, so a
scene can be authored in the editor. Setting any of them marks the document
dirty and the next frame runs the update — batching several changes into one
layout rather than one each.

Drawing goes through `RenderingServer::canvas_item_add_triangle_array`, which
takes the index buffer directly — the shape the core already produces. Not
`draw_polygon`: that takes a polygon *outline* and triangulates it, so handing
it triangles produces garbage where it does not fail outright. Colour is
converted from linear to sRGB here rather than in the core, which is linear on
purpose so a backend that wants linear is not fighting it.

`remove_element_attribute` is separate from `set_element_attribute` because
GDScript cannot pass the null the ABI reads as "remove", and an empty string
still satisfies a presence selector like `[data-hide]`.

## Fonts

`GodotFontBackend` fills the core's C font table over Godot's `TextServer`, so
text is measured and shaped by the same HarfBuzz the engine drives for every
other control. It adopts the theme's fallback face as a `TextServer` RID
directly rather than loading a font file, which means a document renders in the
project's own font by default and the host ships no font of its own.

`use_engine_font` turns it off and falls back to the core's built-in 5x7 face.
That is not a curiosity: the reference rasteriser has no access to the engine's
fonts, so `compare_render.py` sets it to hold the font fixed. Without that, the
two sides render different text and every glyph counts as a difference — which
is exactly what the comparison reported the first time the real font landed.

Two things the seam gets right and are easy to get wrong. Godot's glyph offset
is the quad's top-left below the baseline with y growing down, while the core's
`bearing_y` measures up to that edge — the sign flips. And `face_metrics`
reports a zero line gap, because `TextServer` exposes none and its own line
height is ascent + descent; inventing one would make `line-height: normal`
taller here than in any Godot control using the same face.

Not yet wired: input events, the animation tick, and registering Godot's
`RenderingServer` as the core's render backend through the function-pointer
table — drawing currently goes through the collected draw list rather than
straight into the engine.

## Windows (MSVC)

Verified with Visual Studio 2022 Build Tools, CMake 4.3 and Godot 4.7.1
(win64, mono): the host suite passes 23/23 and `capture.tscn` renders through
the GPU. From a Developer-agnostic shell (CMake finds MSBuild itself):

```powershell
git clone --depth 1 https://github.com/godotengine/godot-cpp C:\Users\<you>\godot-cpp
cmake -S C:\Users\<you>\godot-cpp -B C:\Users\<you>\godot-cpp\build-msvc `
      -G "Visual Studio 17 2022" -A x64 -DGODOTCPP_API_VERSION=4.7
cmake --build C:\Users\<you>\godot-cpp\build-msvc --config Release -j 8

cmake -S godot-port\hosts\godot -B C:\Users\<you>\weva-build\godot-msvc `
      -G "Visual Studio 17 2022" -A x64 `
      -DGODOT_CPP_DIR=C:/Users/<you>/godot-cpp -DGODOT_CPP_BUILD_DIR=C:/Users/<you>/godot-cpp/build-msvc
cmake --build C:\Users\<you>\weva-build\godot-msvc --config Release -j 8
```

That lands `project/addons/weva/bin/weva_godot.dll`, the name
`weva.gdextension` expects. Three things the CMake files already take care
of, recorded because each one cost a build: godot-cpp's `method_bind.hpp`
needs `/Zc:__cplusplus` (MSVC reports 199711L otherwise) and `/vmg`
(pointer-to-member casts across an incomplete class); godot-cpp links the
static CRT, so the host and the core are built `/MT` to match
(`CMAKE_MSVC_RUNTIME_LIBRARY`, set before `project()`); and a DLL is a
RUNTIME artefact under a multi-config generator, so the output directory is
pinned per configuration or the .dll lands in `bin/Release/`.

Keep build directories on a short path: MSBuild's tracker fails with
`DirectoryNotFoundException` on the paths a temp directory produces.

Then, from `hosts/godot/project`:

```powershell
Godot_v4.7.1-stable_mono_win64_console.exe --headless --path . `
    --scene res://test_scene.tscn --quit-after 2                     # the ABI suite
Godot_v4.7.1-stable_mono_win64_console.exe --path . --rendering-driver opengl3 `
    --scene res://capture.tscn -- --html C:/.../Assets/UI/leaderboard.html `
    --css C:/.../Assets/UI/leaderboard.css --size 1280x720 --png C:/tmp/leaderboard.png
```

or open `project/project.godot` in the editor and press Play, which opens the
gallery.


## Driving a document from GDScript

`demo.tscn` is a worked example, and it is written the way the rest of this
section recommends: the script holds game state and nothing else. The markup
says where each piece is shown, which class goes on when, what each button is
for, and how the log lays out -- so all of that moves without the script
hearing about it. Two lines connect them, `set_controller(self)` and one
`doc.data = {...}` per change. Run it:

    godot --path project --scene res://demo.tscn

The whole surface, and it is addressed by SELECTOR throughout, because that is
the name a script and a stylesheet already share.

**Reading and writing**

    doc.set_element_text("#label", "42 / 100")     # what it says
    doc.get_element_text("#label")
    doc.set_element_value("#name", "Vintner")      # form controls
    doc.get_element_value("#shield")               # "on" or "" for a checkbox
    doc.set_element_attribute("#bar", "style", "width: 40%")
    doc.get_element_attribute("#bar", "style")
    doc.toggle_element_class("#bar", "hurt", hp <= 50)
    doc.add_element_class(...) / remove_element_class(...)
    doc.has_element("#thing")
    doc.query_bounds("#thing")                     # where layout put it

**Binding it to data**

`{{ path }}` in the markup, filled from a Dictionary. The script says WHAT
changed; the markup says where it is shown, which is the reason to write a UI
in HTML rather than in setter calls.

    <p>{{ Player.Name }} — {{ Player.Gold }}g</p>
    <div class="bar" style="width: {{ Player.Hp }}%" data-class-hurt="Player.Hurt"></div>

    doc.data = {"Player": {"Name": "Vintner", "Gold": 120, "Hp": 40, "Hurt": true}}
    doc.refresh_bindings()                         # after mutating in place

A dotted path walks nested Dictionaries, Arrays (`Items.0.Name`) and Objects,
so a Resource or a Node can be bound as directly as a Dictionary.
`data-class-<name>` puts one class on or off and leaves the rest of the
element's `class` alone. A path the data does not have shows nothing.

    doc.set_data_source(func(path): return my_state.lookup(path))

takes over entirely when the state lives somewhere a Dictionary cannot reach.

A list binds with `data-each`, which makes one row per item beside the
template:

    <ul id="quests">
      <template data-each="Quests as quest" data-key="Id">
        <li class="row">{{ $index }}. {{ quest.Title }}</li>
      </template>
    </ul>

    doc.data = {"Quests": [{"Id": "a", "Title": "Find the key"}, ...]}

Inside a row, `quest.Title` reads that item, `$index` is its position, and the
controller's own paths still resolve, so a row can read a global beside its own
fields. `data-key` gives a row its identity: while the keys hold, rows are
refilled where they stand, so the focus, the scroll and the selection inside
one survive a value changing next to it. The rows are SIBLINGS of the template
-- `#quests > .row` addresses them, since the template keeps its own children
as the pattern.

**Building it from data**

A list whose length is the game's business cannot be written as markup in
advance -- an inventory, a quest log, a chat pane. Rows are addressed the way
CSS addresses them, so a script that can style a list can also fill it.

    doc.append_html("#log", "<div class='line'>found a key</div>")
    doc.set_element_html("#log", rows)             # replace the lot
    doc.remove_element("#log .line:nth-child(1)")  # trim the oldest
    doc.count_elements("#log .line")
    doc.query_text("#log .line:nth-child(2)")      # read one back by position

**Which row was clicked**

A repeated row usually has no id: the template writes one element and the data
decides how many there are. So a click inside one reports the button, and the
row it belongs to is a separate question -- which the engine answers.

    doc.row_activated.connect(func(handler, index, key): _open(key))
    doc.get_row("#list > .row:nth-of-type(2) > button")   # {"index": 1, "key": "b8"}

`key` is the value of the row's `data-key` field, so it follows the DATA rather
than the position -- sort the list and the key still names the same item.
Without `data-key` it falls back to the position, so a row is addressable
either way. Both are also on the row as `data-weva-index` and `data-weva-key`,
which a stylesheet can select on.

**Handlers the markup names**

`on-<event>="Method"` says what a control is FOR, so the script stops matching
on element ids and a designer can rename, move or wrap a button without
breaking it.

    <div on-click="OnAnything">
      <button on-click="OnStart">Start</button>
      <input type="text" on-input="OnRename">
    </div>

    doc.set_controller(self)                       # OnStart(id) gets called
    doc.handler_invoked.connect(func(handler, id): ...)   # or handle it here

The lookup runs towards the root, so a handler on a panel catches whatever
happens inside it. `on-click`, `on-pointerdown`, `on-pointerup`,
`on-pointerenter`, `on-pointerleave`, `on-input`, `on-change`, `on-submit`,
`on-scroll`, `on-toggle`, `on-contextmenu`, `on-keydown`, `on-keyup`,
`on-textinput`, `on-focus`, `on-blur`.

`on-input` and `on-change` are not the same event, and the difference is the
one that matters for anything expensive. `on-input` fires per keystroke;
`on-change` fires when the user is DONE -- when the focus leaves a text field
holding something other than what it held when the focus arrived. A search box
that hits the disk wants `on-change` and would run four times a word on
`on-input`. A checkbox, a radio and a `<select>` have no editing state to
leave, so for them both fire at the same moment.

`on-submit` is reported against the FORM, not against whatever was pressed --
which is what the handler is written for. Enter in a one-line field inside a
form submits it, and so does a `<button>` in one: HTML says a button in a form
submits unless its `type` says otherwise.

**Hearing about it**

    doc.element_clicked.connect(func(id): ...)     # press and release on one
    doc.element_pressed / element_released
    doc.element_entered / element_exited           # pointer in and out
    doc.element_focused / element_blurred
    doc.value_changed.connect(func(id, value): ...)     # every keystroke
    doc.value_committed.connect(func(id, value): ...)   # once, when done
    doc.form_submitted.connect(func(id): ...)           # the form's id
    doc.element_scrolled.connect(func(id, x, y): ...)   # where it landed
    doc.key_pressed.connect(func(id, key, mods): ...)
    doc.text_entered.connect(func(id, text): ...)

`element_scrolled` carries the offset it scrolled TO, however it got there --
a wheel, a bar, the keyboard, a finger, or the script itself -- so a list that
pages in more rows near its end never has to ask the document each frame.

An element is named by its `id`. One without an id reports "", which a script
can still compare against.

**Scrolling**

A box with `overflow` other than `visible` scrolls, and the wheel, the bar, the
keyboard and a finger drag all follow from that one declaration -- the node
routes them. What a script drives is where the view sits.

    doc.scroll_element("#log", Vector2(0, 40))     # by this much
    doc.set_element_scroll("#log", Vector2(0, 0))  # to here
    doc.get_element_scroll("#log")
    doc.get_element_scroll_max("#log")             # how far there is to go
    doc.scroll_into_view("#log .line:last-child")  # the least that shows it
    doc.scroll_at(point, delta)                    # what a wheel does

Focusing an element scrolls it into view by itself, so keyboard navigation
never moves the focus ring somewhere you cannot see.

**Text fields**

Typing, the caret, the editing keys and selection are the document's. What a
host has to drive is what it alone knows: the chords the ABI's key enum has no
letters for, and the clipboard, which belongs to the platform.

    doc.select_all()
    doc.undo()                                     # bind to Ctrl+Z
    doc.redo()                                     # Ctrl+Y, or Ctrl+Shift+Z
    doc.get_selected_text()                        # for DisplayServer.clipboard_set
    doc.send_text(DisplayServer.clipboard_get())   # paste
    doc.set_element_selection("#name", 0, 5)
    doc.get_element_selection("#name")             # anchor first, so you know
                                                   # which way it runs
    doc.select_word_at(point)                      # what a double click does

Ctrl on a motion key makes it word-sized, and the document handles that itself
because those keys ARE in the enum: Ctrl+Left and Ctrl+Right move by the word,
Ctrl+Backspace and Ctrl+Delete eat one, and Ctrl+Home and Ctrl+End reach the
ends of the whole field rather than of the line. Shift extends the selection
through any of them.

A word is what a browser calls a word, not what a byte test calls one: a run of
letters, digits and underscores; a run of punctuation as one unit; and each CJK
character on its own, so Ctrl+Right through Japanese stops at every character
instead of skipping the sentence. That classifier is also what a double click
selects by.

**Text that is not English**

Japanese and Chinese are written without spaces, so a line that can only break
at a space cannot break at all. Lines break between CJK characters instead,
with the kinsoku prohibitions that stop a line ending or starting on the wrong
one: a full stop or a closing bracket never starts a line, an opening bracket
never ends one, and `line-break: loose` lifts the relaxable half so a narrow
column can still set. A break needs CJK on both sides, so a Latin word inside a
Japanese sentence is never split between its letters.

Drawing those characters is a separate question. Godot's theme font has no CJK
glyphs, so the node reaches past it into the system fallback chain -- the same
chain that draws emoji -- and picks up Yu Gothic, Meiryo, Noto Sans CJK, PingFang,
Malgun Gothic or whatever the machine has. Without those names in the chain a
Japanese paragraph laid out correctly and drew nothing at all.

The software renderer is the exception: its built-in face is a 5x7 ASCII bitmap
with no glyphs for any script but Latin, by design, so `weva_render` lays CJK
out correctly and draws none of it. That is the renderer's scope, not a gap in
the layout.

Undo groups a run of typing into ONE step -- undoing a sentence a letter at a
time is not undo -- and anything that is not typing ends the run. Each field
keeps its own history, and a script writing a value clears that field's, since
the stack no longer describes what the field holds.

**Disclosure**

`<details>` opens and closes on a click on its own `<summary>` -- its own, so a
click in an open body does not collapse it and a nested one is worked by its
own summary rather than its parent's. The UA sheet does the showing and hiding;
the click just moves the attribute.

    doc.has_element_attribute("#section", "open")
    doc.element_toggled.connect(func(id, open): ...)   # or on-toggle="Method"

`has_element_attribute` is separate from `get_element_attribute` because HTML's
boolean attributes -- `open`, `checked`, `disabled`, `selected`, `required` --
are written with no value, so reading one back returns "" whether it is set or
not.

Like the reference, a `<summary>` is not in the tab order and Enter does not
work it; a browser does both, and neither engine does yet.

**Pointer buttons**

The node forwards all three as a mask, matching the web's
`MouseEvent.buttons`: 1 primary, 2 secondary, 4 middle. Only the PRIMARY
button activates anything -- a right-click does not toggle a checkbox, submit
a form, open a `<details>` or work a popover, exactly as it does not in a
browser.

The secondary button asks for a context menu instead:

    doc.context_menu_requested.connect(func(id, position): ...)

or `on-contextmenu="OnMenu"` in the markup. The signal names the element the
click landed ON, so a menu knows which row was right-clicked, while the
handler is found by walking towards the root as every handler is.

There is no context-menu widget, and that is deliberate: a menu is markup, and
the popover machinery above already opens, positions and light-dismisses one.

**Tooltips**

`title="..."` draws after the pointer has rested on the element for 0.6s, as a
`<div class="ui-tooltip">` beside the cursor. Style it with `.ui-tooltip`; it
is an ordinary element, marked `data-weva-tooltip` so a host walking the DOM
can tell it from its own content, and `pointer-events: none` so what is under
it stays hoverable. A press dismisses it, and moving to a different titled
element restarts the wait rather than showing the old text at the new place.

    doc.tooltip_delay = 0.3     # seconds
    doc.tooltip_delay = -1.0    # off, for a game that presents its own

The wait runs on the clock rather than on pointer events, because a pointer
that has stopped moving sends nothing more and the tooltip still has to appear
-- so this needs `update_document(dt)` to be called with real time, as an
animating document does.

**Labels**

`<label>` works the control it names, so the word beside a checkbox is
clickable and not just the 13px box. `for="id"` names one anywhere in the
document; without it the label owns the first control inside it. A click on the
control itself is the activation, so nothing is forwarded twice, and a disabled
control is not activated by its label.

    doc.get_focused_id()      # a label moves the focus too, so read it back

A slider is the exception: its value comes from where along the track the
pointer landed, and a label click landed somewhere else, so the label focuses
it and leaves the value alone -- what a browser does.

**Dialogs**

A `<dialog>` opens plainly or modally, and the difference is the dim behind it:
a modal one joins the top layer and gets a `::backdrop` -- a viewport-filling
half-transparent black box, styleable with `::backdrop { ... }`. An element
with `popover` and `data-popover-open` is the other top-layer shape and shares
the machinery.

    doc.show_dialog("#confirm")           # no backdrop
    doc.show_modal_dialog("#confirm")     # with one
    doc.close_dialog("#confirm")
    doc.has_element_attribute("#confirm", "open")

Escape is the host's to bind, as in the reference: which key cancels a dialog
is a platform question, so bind it and call `close_dialog`.

The top layer here is `position: fixed`, not the real CSS top layer, so a
transformed or filtered ancestor that establishes a containing block would
capture the dialog rather than letting it escape. The reference makes the same
simplification and says so.

**Popovers**

A `<button popovertarget="menu">` works its popover with no script at all, and
`popovertargetaction="show"` or `"hide"` pins the direction instead of
flipping. An `auto` popover light-dismisses -- a click outside it closes it, a
click inside does not -- and Escape closes the topmost one, one per press, so a
submenu goes before the menu it came from. `popover="manual"` opts out of both
and closes only when asked.

    doc.show_popover("#menu")
    doc.hide_popover("#menu")
    doc.toggle_popover("#menu")
    doc.has_element_attribute("#menu", "data-popover-open")
    doc.element_toggled.connect(func(id, open): ...)

**Dropdowns**

Clicking a `<select>` opens its list and clicking a row chooses it, through the
pointer the node already forwards. The choice lands in the DOM as `selected` on
the option, so `:checked`, the paint and `get_element_value` all agree.

    doc.open_select("#quality")
    doc.close_select()
    doc.get_open_select()                          # the id, or ""

**Input**

The node reads its own input while `interactive` is on, which is the default.
A host routing its own -- a gamepad cursor, a touch surface, a menu that
decides who gets the keyboard -- calls these instead.

    doc.set_pointer(point, buttons) / doc.clear_pointer()
    doc.send_key(KEY_DOWN)                         # true when the document
                                                   # took it, so an unused key
                                                   # stays yours
    doc.send_text("x")
    doc.focus_next(false)                          # tab order

Tab moves focus by itself and the document reports having consumed the key. A
key with nothing to scroll and no field to edit is NOT consumed, so a game
keeps its own arrows.

**Time**

`paused` stops the clock: transitions and @keyframes hold where they are, and
the document still updates when something else changes it. `update_document(dt)`
steps it by hand, which is what a test wants when it needs to see a transition
partway rather than wait for real frames.

Two things worth knowing, because both have caught someone out:

  * A transition means a change does NOT take effect immediately. Setting a
    width and measuring in the same breath reads the OLD width, correctly.
  * The animation belongs in the stylesheet. The demo's script sets a width and
    toggles a class; the easing and the low-health pulse are CSS. That is the
    reason to drive a UI this way rather than tweening from GDScript.
