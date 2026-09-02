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
