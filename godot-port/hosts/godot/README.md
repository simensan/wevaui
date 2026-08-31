# The Godot host

A GDExtension that binds `libweva`'s C ABI and draws its geometry through a
`Node2D`. It talks to the core through `weva_c.h` only — no Godot type reaches
the core, and no core C++ type reaches Godot. That is the property that lets the
same core serve a Unity host later, and it is worth keeping loudly true.

## Building

`godot-cpp` is not vendored: the core builds and tests with no Godot dependency
at all, and vendoring would quietly end that.

```sh
git clone --depth 1 --branch 4.3 https://github.com/godotengine/godot-cpp
cmake -S godot-cpp -B godot-cpp/build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build godot-cpp/build -j

cmake -S hosts/godot -B build-godot -G Ninja \
      -DCMAKE_BUILD_TYPE=Release -DGODOT_CPP_DIR=$PWD/godot-cpp
cmake --build build-godot -j
```

The library lands in `project/addons/weva/bin/`, where `weva.gdextension`
expects it.

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

## What the node does, and does not

`WevaDocument` exposes `html`, `css` and `document_size` as properties, so a
scene can be authored in the editor. Setting any of them marks the document
dirty and the next frame runs the update — batching several changes into one
layout rather than one each.

Drawing expands the core's indexed triangles into the flat arrays
`draw_polygon` wants. The core keeps them indexed because a GPU backend wants
them that way; this host is the one that does not. Colour is converted from
linear to sRGB here for the same reason: the core is linear, and doing the
conversion inside it would be wrong for a backend that wants linear.

Not yet wired: input events, the animation tick, and registering Godot's
`RenderingServer` and `TextServer` as the core's backends through the
function-pointer tables. The node currently uses the core's built-in stub font,
so text renders with the 5x7 built-in face rather than a real one.
