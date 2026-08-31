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

## Running the render tests

```sh
godot --headless --path hosts/godot/project
```

`render_tests.gd` exits non-zero on failure, so it works in CI.

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

Not yet wired: input events, the animation tick, and registering Godot's
`RenderingServer` and `TextServer` as the core's backends through the
function-pointer tables. The node currently uses the core's built-in stub font,
so text renders with the 5x7 built-in face rather than a real one.
