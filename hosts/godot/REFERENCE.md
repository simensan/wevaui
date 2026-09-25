# Godot host reference

For installation and ordinary UI authoring, start with the [addon guide](ADDON_README.md).
This reference covers build tooling, native APIs and implementation checks.
Dated preview/build results apply only to those recorded checkpoints; current
readiness and verification are linked from the [documentation index](../../docs/README.md).

For performance qualification with a custom Godot build, pass its matching
release template explicitly:

```sh
python hosts/godot/run_frontier_perf.py --godot /path/to/editor \
  --release-template /path/to/release-template --output /path/to/new-results \
  --budget examples/frontier_camp/tests/performance_budget_desktop.json
```

The runner records the template SHA256 and restores the saved export presets,
including when export fails. Selecting an editor alone does not select its
matching export template: Godot may use a cached template. Omitting the option
preserves Godot's selection and records its identity as unverified. With
`--executable`, export is skipped and the executable hash identifies the tested
artifact; establish its template provenance from the original export receipt.

For a standalone game integration, open
[`examples/frontier_camp`](../../examples/frontier_camp/README.md). It uses the
packaged addon, Inspector-selected HTML/CSS, signal-driven `WevaView` bindings,
keyed inventory actions and two-way settings. Its native-input test also runs
from an exported executable.

A GDExtension that binds `libweva`'s C ABI and draws its geometry through a
`Control`. It talks to the core through `weva_c.h` only — no Godot type reaches
the core, and no core C++ type reaches Godot. That is the property that lets the
same core serve both Godot and Unity, and it is worth keeping loudly true.

The Control's `font` theme item selects its native default font, including
parent themes, overrides and type variations. Resource changes refresh both
layout and glyphs. See [font integration](../../docs/GODOT_TEXT_SHAPING.md)
for authoring, test commands and the remaining CSS family-selection work.

`check_theme_fonts.py --godot /path/to/godot --library /path/to/library --render`
verifies theme changes and viewport-dependent font sizes in an isolated project.
Resized controls must match freshly loaded controls in geometry and pixels,
including empty elements whose `em` dimensions depend on `vw`/`vh` font sizes.

The `font_inheritance_tests.tscn` scene checks computed font-size inheritance,
`display:contents`, generated content and live ancestor/class changes through
both font backends. Run it headlessly for geometry or with a renderer for
incremental-versus-fresh pixel comparisons. Its headless checks are included
in `check.sh`; the corresponding browser fixture is
`Tools/oracle/check_font_inheritance_chrome.py`.

The `intrinsic_size_tests.tscn` scene checks shrink-to-fit and flex/grid widths
through both font backends, including preserved newlines and live whitespace
changes. Run `godot --headless --path hosts/godot/project intrinsic_size_tests.tscn`
from the repository root; omit `--headless` to also compare incremental and fresh
pixels. `check.sh` includes its headless checks in the host integration gate.

## Western survival sample

Select **western-survival** in the gallery, or run
`project/samples/western_survival/survival.tscn` directly. It provides a working
survival HUD, inventory, consumables, crafting, hotbar and ammunition over a
frontier backdrop. See the [sample guide](project/samples/western_survival/README.md)
for controls, screenshots, integration notes, interaction checks and measured
runtime costs.

## Building

Builds require Python 3.9 or newer. CMake downloads pinned ICU 78.3 sources
and embeds the select-search data in the native library. Installed addons
need no separate ICU runtime. See [the dependency profile](../../third_party/icu/README.md)
for the source hash, offline override and notices.

`godot-cpp` is not vendored: the core builds and tests with no Godot dependency
at all, and vendoring would quietly end that.

The host builds `godot-cpp` from an external source checkout in the same CMake
build. Its target supplies the generated headers and ABI-related compiler
definitions; a separately built archive is no longer selected by filename.
The current desktop builds target Godot API 4.7. From the repository root:

```sh
git clone https://github.com/godotengine/godot-cpp
git -C godot-cpp checkout 26fb7ab5821e6a1096f62c22f7462d1d70caa332
cmake -S hosts/godot -B build-godot -G Ninja \
      -DCMAKE_BUILD_TYPE=Release -DGODOT_CPP_DIR=$PWD/godot-cpp \
      -DGODOTCPP_API_VERSION=4.7
cmake --build build-godot -j 8
```

Set `GODOTCPP_API_VERSION` to the engine you target. If your engine is newer
than anything bundled, dump its own description and point at that instead —
this always matches, whatever the version:

```sh
godot --headless --dump-extension-api --dump-gdextension-interface
cmake -S hosts/godot -B build-godot -G Ninja -DCMAKE_BUILD_TYPE=Release \
      -DGODOT_CPP_DIR=$PWD/godot-cpp -DGODOTCPP_CUSTOM_API_FILE=$PWD/extension_api.json
```

The library lands in `project/addons/weva/bin/`, where `weva.gdextension`
expects it. `GODOT_CPP_BUILD_DIR` is obsolete. Set `WEVA_GODOT_BIN` to another
output directory when testing an isolated build while an editor has the current
library loaded.

The checked-in extension descriptor and package tool declare Godot 4.7 as the
minimum, matching these builds. Keep that declaration consistent when building
against a different or custom API.

The build pin is `godot-cpp` revision `26fb7ab5821e6a1096f62c22f7462d1d70caa332`
at API 4.7. The adapter works around the stock Godot 4.7 script-iterator
defect for document text; the tested stock builds retain the engine defect; see
[release verification](../../docs/RELEASE.md). An extension
built against an older `godot-cpp` does load in a newer engine — the 4.3 build
ran fine under 4.7.2 — but build against the version you ship on.

### First import

Open a new project in the editor once before launching it as a game. The editor
discovers the extension and imports images into `.godot/`. The development
project carries `extension_list.cfg` for its test runners, but an installed
addon must work without copying that cache. For an isolated headless import:

```sh
godot --headless --path /path/to/project --editor --quit-after 60
```

Immediate `--import` / `--quit` reproduced Godot's extension-documentation
initialization crash on Linux 4.7.2; letting the editor initialize for several
frames avoids it. See [Godot issue #111645](https://github.com/godotengine/godot/issues/111645).
`check_export.py` exercises this fresh-cache workflow on Linux and Windows.

## Make an installable preview

Build the libraries first, then package the platforms you intend to test:

```sh
python3 hosts/godot/package_addon.py \
    --linux-library hosts/godot/project/addons/weva/bin/libweva_godot.so \
    --windows-library /path/to/weva_godot.dll \
    --godot-cpp-dir /path/to/godot-cpp --version 0.1.0-preview.59 \
    --output /path/to/weva-preview.zip
python3 hosts/godot/check_export.py --godot /path/to/godot --addon /path/to/weva-preview.zip
# With matching desktop export templates installed:
python3 hosts/godot/check_export.py --godot /path/to/godot --addon /path/to/weva-preview.zip --native
```

Each library argument is optional; include at least one. Supply API 4.7 x86_64
builds. The archive contains only `addons/weva/`, with the selected libraries,
their SHA-256 checksums, Weva/godot-cpp licenses, installation instructions and
a standalone example. Extract it into a fresh project's root and run
`addons/weva/example/example.tscn`. The example edits a player name through
`data-model` and updates a bound score through button handlers.

CMake writes `<library>.build.json` after linking. Keep this file beside each
library: packaging verifies the binary, source and godot-cpp content hashes,
records the compiler/configuration and Git identity when available, and rejects
stale, instrumented, Debug, double-precision or custom-API builds. Source
archives without Git metadata retain their content fingerprint. Both libraries
in a combined ZIP must match the supplied source/dependency snapshot. The ZIP
is deterministic for identical inputs, and an existing output is never replaced.
Only explicit `X.Y.Z-preview.N` versions are accepted while the product release
requirements remain open. A successfully packaged preview is not release approval.

`check_export.py --addon` installs that archive into a fresh project and tests
both the resource fixture and the example before and after exporting a PCK.
`--native` also exports and launches debug, release and embedded-pack games.
`check.sh` packages and checks the Linux build with native exports, so it now
requires matching desktop export templates. Repeat the check on
Windows for a Windows release; a passing Linux gate does not validate a DLL.

## Render upload checks

Consecutive meshes with the same texture share a triangle upload, preserving
their order. Backdrop copies and SDF materials end a run; each combined upload
is limited to 65,536 vertices. A single larger mesh keeps its original path.
`WEVA_GODOT_DRAW_LOG=1` reports packing, submission time and upload counts.
`WEVA_GODOT_DISABLE_BATCHING=1` restores individual uploads for comparisons.

Unchanged command versions reuse the batch's packed vertex/color/UV/index
arrays. `WEVA_GODOT_DISABLE_PACK_CACHE=1` forces repacking for comparisons.
The draw log also reports packed vertices and reused batches. Upload counts
still include reused arrays: Godot receives the triangles on every redraw.

`layout_stress_probe.gd` can write per-frame CSV with
`WEVA_GALLERY_PROBE_TRACE=/path/frames.csv`. Its frame IDs match the draw log,
so core updates and uploads can be separated from waits elsewhere in a frame.
Viewport CPU/GPU timestamp collection is opt-in with
`WEVA_GALLERY_PROBE_GPU_TIMING=1`. See [the timing notes](../../docs/PERFORMANCE.md)
for observed Windows GPU waits and the limits of those measurements.

For normal in-game workloads, use `run_game_ui_bench.py`: retained HUDs,
batched state writes, data bindings, menu hover/fades, inventory scrolling and
chat typing. It includes all public API calls in the CPU measurement and can
compare frozen DLLs/SOs with repeated native runs and exact final pixels.
See [the runtime benchmark guide](../../docs/RUNTIME_PERFORMANCE.md).

Set `WEVA_GALLERY_PROBE_COLD_BUILDS=15` when running the same probe to measure
fresh document builds instead of animated frames. It reports the first build
and a series of fresh builds, including parsing, engine fonts and host texture
preparation. Godot itself stays running, so later documents can benefit from
its resource caches. `WEVA_STAGE_LOG=1` adds cascade storage counts and separate
flow, positioning, overflow and incremental-index times for full layout.
It also reports host-font calls, cache hits and time by operation.
Add `WEVA_CASCADE_LOG=1` to separate matching, declaration application,
value-resolution passes and pseudo queries, including metadata/value-page
allocation time. Computed styles allocate stable raw-value pages on demand.
`WEVA_FONT_LOG=1` breaks uncached Godot shaping into preparation, TextServer
shaping, glyph export and conversion, reported when a font backend clears.
`WEVA_INLINE_LOG=1` traces inline-result hits; `WEVA_DISABLE_INLINE_REUSE=1`
disables within-pass line reuse for comparisons. Per-call tracing adds
substantial overhead.
`WEVA_LAYOUT_LOG=1` reports inclusive box-model, inline and height-finalization
times inside full root layout, with collection, atom-sizing and line-building
subscopes inside inline work. `WEVA_GODOT_DISABLE_VARIANT_CACHE=1` disables
sharing of immutable synthetic fonts between documents; it keeps the corrected
per-document distinction between ordinary and heavier bold synthesis.
Use logging separately from timing comparisons.

After importing the test project, run the pixel regression with a display:

```sh
python3 hosts/godot/check_triangle_batching.py --godot /path/to/godot \
    --project hosts/godot/project --rendering-method gl_compatibility
python3 hosts/godot/check_packed_draws.py --godot /path/to/godot \
    --project hosts/godot/project --rendering-method gl_compatibility
```

Use `--rendering-method mobile` for Vulkan. This compares exact rendered
images with batching on/off, including overlapping transparency, clipped
gradients, texture/material boundaries, backdrop filters and large uploads.
`check.sh` includes the OpenGL check beside the other rendered gates.
The packed-array check compares 26 images with caching enabled/disabled and
requires actual reuse. It covers repeated redraws, removed/moved geometry,
gradients, font and viewport changes, native modulation/shader parameters,
SDF/backdrop transitions, visibility, empty documents and reloads.
Pass `--cache-kind glyphs` to compare the same images with the core's glyph
prepass reuse enabled/disabled. This skips unchanged text subtrees while their
glyph slots remain prepared. `WEVA_DISABLE_GLYPH_REUSE=1` disables it, and
`WEVA_PAINT_LOG=1` reports glyph timing and the number of skipped subtrees.
It also separates background, shadow and filter costs, with raster/blur/upload
sub-scopes for textures. `WEVA_GRADIENT_LOG=1` reports each background texture's
dimensions, sample count, reuse paths and raster duration. These diagnostic
timings include logging overhead; use uninstrumented runs for performance
comparisons.

`WEVA_BLUR_LOG=1` separates scratch-buffer preparation, horizontal and vertical
blur passes, and output conversion. The `weva_blur_variants` CTest target checks
the normal and forced-portable kernels against each other, including transparent
pixels, narrow textures and radii larger than the texture.

`WEVA_REUSE_LOG=1` identifies changed inputs at retained paint boundaries:
position, opacity, transform, scissor, geometric clip, color filter and canvas
owner. For example, layout-stress retains its grid in layout, but its animated
counter height changes the grid's fractional position and rounded clipping
boundary, requiring new paint geometry. Use this trace with `WEVA_STAGE_LOG=1`
to distinguish rejected paint reuse from a layout fallback.

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

`check.sh` runs every gate in the order that fails fastest, and
exits non-zero when any of them does:

    bash check.sh            # or --clean to rebuild from scratch

The gates include core unit tests, the mutation corpus against full
recomputation, ASan/UBSan, all three layout-oracle corpora, packed resources,
backend comparisons, interactive rendering, host tests and demo integration.
The three remaining harvest metric differences are tracked in
[product readiness](../../docs/PRODUCT_READINESS.md).

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

Use a private Xvfb display for automated comparisons on Linux, including on
an active desktop: window focus changes can clear a held pointer during readback
and compare different interaction states. Mesa's software path works without
a GPU:

```sh
LIBGL_ALWAYS_SOFTWARE=1 xvfb-run -a -s "-screen 0 800x600x24" \
    python3 hosts/godot/compare_render.py page.html page.css --size 300x220 \
    --godot /path/to/godot
```

The rasterizers may differ slightly at edges and in color rounding. The full
gate requires less than 1% structural disagreement across the static corpus.
For interactive states, it also requires less than 1% of pixels to exceed the
ordinary per-channel tolerance, so a missing hover or pressed fill fails even
when the geometry still matches.

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

The base class is now `Control`; earlier preview scripts typed as `Node2D`
must use `WevaDocument` or `Control`. `document_size` aliases native `size`.
An unsized node fills its parent using full-rect anchors, and Godot Containers
can size it directly. Resizing the Control reflows HTML without a viewport
resize callback in GDScript.

Assigning `css` replaces the previous author stylesheet; `css = ""` removes
it. The default browser styles remain. Replacement preserves the DOM, edited
form values, focus and selection, and keeps the clock of continuing animations.

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

The additive minor-11 `weva_document_set_font_shaper` callback carries each
shaped glyph's offsets across the C ABI. Godot's per-run offsets are separate
from the bitmap bearings above; both apply to combining marks. The adapter
converts TextServer character clusters to UTF-8 byte offsets and retains the
exact font RID chosen for every glyph, including automatic system fallbacks.
The original font table keeps its binary layout and legacy shaping callback.

Build the separate native adapter test extension with
`-DWEVA_GODOT_FONT_TESTS=ON`, then run:

```bash
python3 hosts/godot/check_font_shaping.py --godot /path/to/godot \
    --library hosts/godot/project/addons/weva/bin/libweva_font_tests.so
```

On Windows, pass the generated `weva_font_tests.dll`. The runner creates an
isolated project and checks glyph positions, source clusters, native metrics,
coverage pixels and the resulting document geometry against TextServer.
`check.sh` runs this gate when the test extension is present; a custom output
path can be supplied through `WEVA_GODOT_FONT_TEST_LIBRARY`. Test extensions
are excluded from the packaged addon. See [text shaping](../../docs/GODOT_TEXT_SHAPING.md)
for the stock-engine defect the adapter works around and remaining typography limits.

`run_host_suite.py --godot <editor> --project hosts/godot/project --out <new dir>`
runs the editor import, every test/smoke scene and the western_survival smoke
script headless, and writes `receipt.json` with per-entry checks and the
library digest: the "host entries" figure in the readiness documents. To test
an uninstalled library, copy the project to `<dir>/hosts/godot/project`, put
the library in its `addons/weva/bin`, and give `<dir>/tools` a junction or
symlink to `Tools/` so the gallery scenes find the sample corpus.

Input events and the animation clock run through the node. Drawing consumes
the core's collected draw list through Godot's rendering server.

## Windows (MSVC)

Verified with Visual Studio 2022 Build Tools and standard Godot 4.7.2 (win64):
245 host checks, 75 binding checks, 40 native input checks, 35 keyboard checks, 28 IME checks
and the isolated project/PCK smoke pass.
From a Developer-agnostic shell (CMake finds MSBuild itself):

```powershell
git clone --depth 1 https://github.com/godotengine/godot-cpp C:\Users\<you>\godot-cpp
cmake -S hosts\godot -B C:\Users\<you>\weva-build\godot-msvc `
      -G "Visual Studio 17 2022" -A x64 `
      -DGODOT_CPP_DIR=C:/Users/<you>/godot-cpp -DGODOTCPP_API_VERSION=4.7
cmake --build C:\Users\<you>\weva-build\godot-msvc --config Release -j 8
```

That lands `project/addons/weva/bin/weva_godot.dll`, the name
`weva.gdextension` expects.

**Rebuild it after pulling.** `check.sh` builds and gates the Linux `.so`
only, so a Windows DLL can sit a day behind every fix while every gate reads
green -- and the symptom is not a crash, it is a feature quietly behaving the
way it used to. It has already cost one round of "hover isn't working": the
DLL predated `a7e75b6b`, where hit testing started following paint order, so
the pointer was picking the wrong element. `Get-ChildItem` the two files in
`addons/weva/bin` and compare their dates before believing a bug report about
either platform. Three things the CMake files already take care
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

**Both ways: `data-model`**

Everything above pushes data at the markup. A control that a player edits has
to send it back, and `data-model` is the return path -- the same dotted path,
on an `<input>`, `<textarea>` or `<select>`:

    <input type="text"  data-model="Player.Name">
    <input type="range" data-model="Settings.Volume" min="0" max="100">
    <input type="checkbox" data-model="Settings.Music">

    doc.data = {"Player": {"Name": "Ada"}, "Settings": {"Volume": 40, "Music": true}}
    doc.data_changed.connect(func(path, value): _save(path, value))

The field starts filled from the data, and what the player types, drags or
ticks lands back in `doc.data` at that path. Anything else bound to the same
path follows it in the same frame -- a label beside a slider, a class that
turns on past a threshold -- so a settings panel needs no script at all beyond
the dictionary it started with.

Three things worth knowing:

  * **The type at the path wins.** A path holding an int gets an int back, not
    the `"62"` the control reports, so a script's own arithmetic keeps working
    after the first drag. Floats and bools likewise.
  * **A missing branch is created.** `data-model="Fresh.Field"` writes into a
    `Fresh` dictionary the data did not have.
  * **A row's alias is unwound.** Inside a `data-each`, markup writes the
    row's own name -- `data-model="quest.Note"` -- and the write lands at
    `Quests.1.Note`, in the item that row actually is. Nested repeats unwind
    the whole way up, and `data_changed` reports the resolved path. A path
    that names no alias is global, in a row as anywhere else.
  * **Player edits and form resets write back.** `set_element_value()` from a script
    raises no input event, here as in a browser, and neither does a value the
    data itself pushed in -- so the two directions cannot chase each other. And
    a `set_data_source()` resolver is read-only: a Callable can answer a path
    but has nowhere to put an answer, so the write-back stays out of its way.

`binding_tests.gd` is this, executable; `check.sh` runs it.

Controls retain markup defaults independently of their live values. A
`type="reset"` button or `doc.reset_form("#settings")` restores the owned
controls and their writable `data-model` paths before calling `on-reset`
and emitting `form_reset(id)`. Use `:checked` for live selection/checkedness;
`[checked]` and `[selected]` describe defaults. See
[FORM_STATE.md](../../docs/FORM_STATE.md) for default changes, sanitization,
external form owners, queued event semantics and remaining form limits.

**Building it from data**

A list whose length is the game's business cannot be written as markup in
advance -- an inventory, a quest log, a chat pane. Rows are addressed the way
CSS addresses them, so a script that can style a list can also fill it.

    doc.append_html("#log", "<div class='line'>found a key</div>")
    doc.set_element_html("#log", rows)             # replace the lot
    doc.remove_element("#log .line:nth-child(1)")  # trim the oldest
    doc.count_elements("#log .line")
    doc.query_text("#log .line:nth-child(2)")      # read one back by position

**Images**

`background-image: url(...)`, `<img src="...">` and other image properties
resolve through Godot, including inside an exported `.pck`:

    doc.base_path = "res://ui"                  # what a relative url() joins to
    <img src="icons/gem.png">                   # res://ui/icons/gem.png
    background-image: url(res://art/frame.png)  # a scheme is left alone

Relative paths resolve under `base_path`, or under `res://` if no base is set.
Project textures load through `ResourceLoader`, so the same imported texture
and import settings apply in the editor and an exported game. PNG and SVG are
covered by the export smoke test. Other Godot texture formats use the same
path when Godot can provide a readable image.

The host passes a PNG snapshot to the core's image cache. Raw images outside
the importer (`user://`, absolute paths, or Keep File) still need to be PNG,
8 bits per channel and non-interlaced. `get_missing_assets()` reports paths
that could not be loaded.

An `@import` in a stylesheet goes through the same path: the sheet is read
relative to `base_path` (or by its `res://` path), spliced in under its media,
supports and layer conditions, and one that cannot be read is named in the
CSS diagnostics.

`<img>` sizes as a replaced element: no width or height gives the image's own
size, one of them gives the other through the intrinsic ratio, and
`object-fit` and `object-position` place it in the content box.

**Exporting HTML, CSS and artwork**

HTML and CSS are plain files. Include `*.html,*.css` in the export preset's
non-resource filter so `FileAccess.get_file_as_string()` can read them in a
packed game. For images named only in markup, use **Export all resources** or
explicitly include their imported resources; Godot cannot infer dependencies
from HTML/CSS text. Use project-relative `res://` paths for shipped assets.
See Godot's [export filters](https://docs.godotengine.org/en/stable/tutorials/export/exporting_projects.html).

The isolated resource check creates a new project, imports it, exports a PCK,
then runs the pack from another directory with the native library beside it:

```sh
python3 hosts/godot/check_export.py --godot /path/to/godot
# For a binary built outside the sample project:
python3 hosts/godot/check_export.py --godot /path/to/godot --library /path/to/weva_godot.dll
```

It checks markup, stylesheet replacement, imported PNG/SVG dimensions and
missing-asset diagnostics, with the original PNG absent from the pack.
The default pack check uses the editor executable and needs no export templates.
Add `--native` to check actual debug/release executable exports using installed
templates. The check verifies that Godot copies the selected extension, hides
the source project, relocates the exported directories and launches them.
With `--addon`, the packaged example's controller and binding checks also run.
`--native --addon /path/to/addon.zip --render` additionally compares its rendered
pixels with the project; this needs a display and OpenGL 3 support.
See [desktop export verification](../../docs/DESKTOP_EXPORTS.md) for the tested
configuration and reproducible commands.

**Changing one property, and finding a box on screen**

    doc.set_element_style("#bar", "width", "62%")     # one declaration
    doc.set_element_style("#bar", "width", "")        # back to the stylesheet
    doc.get_element_style("#bar", "width")            # what it sets INLINE

The rest of the element's `style` is left alone, which is the point: changing
one property used to mean reading the attribute, splicing it and writing the
rest back, and a splice that goes wrong takes every other declaration with it.
An empty value REMOVES the declaration rather than setting it empty, so a
property can be handed back to the stylesheet without a script having to know
what the stylesheet said. `get_element_style` reports what the element sets
inline; `get_computed_style` reports what the cascade decided, and the two
differ whenever a stylesheet is involved at all.

    doc.get_element_screen_rect("#portrait")   # Rect2, in the parent's space

An element's box through this node's own transform, so a Node2D, a particle
emitter or a 3D viewport can be parented over it. `query_bounds` answers in
DOCUMENT space, which is not where anything else in the scene lives once the
document is scaled to fit or offset by a scroll -- composing that by hand is
the mistake this node itself already made once, for hit testing.

**Driving it with a gamepad**

A tab order cannot answer the question a stick asks. `focus_next` walks the
document in source order, which in a grid means the end of every row jumps to
the element above-right. `focus_move` asks about GEOMETRY instead:

    doc.focus_move(Vector2.LEFT)      # returns the id that took focus
    doc.focus_move(input_direction)   # only the sign is read

It scores each candidate by how far it is along the direction plus a heavy
penalty for drifting off it, which is what keeps a cursor travelling down a
column instead of diagonally to whatever happens to be nearest. At an edge it
STAYS rather than wrapping -- a menu that jumps from its last row to its first
under a held stick is worse than one that stops. With nothing focused, the
first press picks something up, so a freshly opened screen answers.

A script cannot reasonably do this itself: it would have to fetch every
focusable's rectangle and re-derive the heuristic each frame.

**Reading the document back**

Two questions a script could not previously ask.

What the STYLESHEET decided, which no other call reports -- a game that tints
a particle or a 3D material to match a panel has to get the colour from the
same place the panel did:

    doc.get_computed_style("#panel", "color")        # "rgb(200, 100, 50)"
    doc.get_computed_style("#tip", "display")        # "none"

It is the COMPUTED value: inheritance and the initial value are already
resolved, so a property the element never set still answers with the one it is
actually using. An unknown property answers empty rather than guessing.
Position and size are not here -- those are the layout's answer, and
`query_bounds` gives them.

And every match of a selector, rather than only the first:

    doc.query_all_text("#log .line")        # PackedStringArray, document order
    doc.query_all_bounds(".card")           # Array[Rect2]
    doc.query_all_ids(".card")              # "" where a row has no id

`count_elements` plus one `:nth-of-type` selector per row still works, and is
still the way to address ONE of them; these are for reading the lot.

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
    doc.form_reset.connect(func(id): ...)               # defaults and data restored
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
    doc.paste_text(DisplayServer.clipboard_get())  # paste as one undo step
    doc.set_element_selection("#name", 0, 5)
    doc.set_element_selection_without_focus("#name", 5, 0) # prepare a backward range
    doc.get_element_selection("#name")             # anchor first, so you know
                                                   # which way it runs
    doc.select_word_at(point)                      # what a double click does

Selection endpoints use UTF-8 byte offsets, with the anchor first. Programmatic
focus preserves a field's selection; untouched markup starts at zero. To append,
set both endpoints to `doc.get_element_value("#name").to_utf8_buffer().size()`.
Tab selects input text and preserves textarea selection. The non-focusing setter
(ABI minor 13) prepares selection without moving keyboard focus; the original
setter focuses the field. Both clamp endpoints to valid byte boundaries.

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

The first `<summary>` participates in Tab navigation and native keyboard
activation; Enter and Space share its pointer activation behavior.

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

**Engine counters**

`doc.get_stats()` is a Dictionary of the last update's stage timings
(`cascade_ms`, `animate_ms`, `boxes_ms`, `layout_ms`, `paint_ms`, `update_ms`),
the `elements`, `boxes`, `draws` and `textures` counts, the paint pass's
`texture_cache_hits` / `texture_cache_misses`, and the cascade's running totals
(`cascade_elements`, `cascade_pseudos`; diff them between frames) -- what a
debug overlay shows beside the frame time.

**Change notification**

`doc.get_changed_elements()` is an Array of `{element, kind}` for the elements
the last update restyled (`kind` is `"paint"`, `"layout"` or `"boxes"`), empty
after a settled update; `doc.get_structure_version()` moves whenever elements
come or go. A debug overlay flashes the first; a tool caching handles watches
the second.

**HTML parse diagnostics**

`doc.get_html_diagnostics()` is a PackedStringArray of what the last `html`
assignment or `reload_html()` recovered from (`"1:12: End tag 'div' closes
'span', which was left open"`), beside `get_css_diagnostics()`. The page still
loads; the lines say where it may not be what was meant.

**mix-blend-mode**

A blended element's draws go on a child canvas item with a `CanvasItemMaterial`:
`multiply` as MUL, `screen` / `lighten` / `color-dodge` as ADD (the closest
Godot has), everything else as MIX, i.e. normal. Child items draw after the
node's own commands, so a blended box overlaps its later siblings; the
retained-batch prototype and the backdrop-filter layered path leave blending
out.

**Hot reload**

`doc.reload_html(html)` diffs new markup onto the live document instead of
replacing it: an element the diff can match -- the same tag at the same place,
or the same `id` / `data-key` among its siblings -- keeps its identity, so
focus, scroll positions, typed values and running transitions survive an edit
to the file; `html = ...` still replaces everything.

**The box tree**

`doc.get_box_tree()` lists every layout box in tree order as a Dictionary
(`parent` index or -1, `kind`, `element` handle or -1, `rect` in document
coordinates with scroll not applied, `margin` / `border` / `padding` as
left-top-right-bottom Rect2s, `scroll`, and a text box's `text`) -- enough to
draw the four devtools outlines per box on a debug overlay.

**The mouse cursor**

The node shows the `cursor` the page asks for under the pointer: the hand for
`cursor: pointer` (and over a link), the I-beam over text and text fields, the
forbidden sign the UA sheet gives `:disabled` controls, resize and drag shapes
for their keywords, the arrow for anything Godot has no shape for. It answers
Godot's cursor-shape query (`get_cursor_shape(position)`) rather than setting
`default_cursor_shape`, which would make Godot re-dispatch a mouse motion; a
game that manages its own cursor turns it off:

    doc.follow_css_cursor = false
    print(doc.get_cursor())     # the keyword, e.g. "pointer", to map yourself

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

**Colour scheme**

`light-dark(a, b)` and `@media (prefers-color-scheme: dark)` follow the
document's own switch rather than the OS, so a game's theme toggle drives
them:

    doc.dark_color_scheme = true    # every light-dark() picks its second colour

An element that declares `color-scheme: dark` (or `light`, or `only ...`)
settles its own `light-dark()` regardless; `light dark` and `normal` leave
the choice to the switch. The default is light.

`env(safe-area-inset-top)` and its three siblings read the insets the node
holds -- zero until set -- so a page pads around a notch or a system bar
the way it would in a browser:

    doc.set_safe_area_insets(44, 0, 20, 0)   # top, right, bottom, left, in document pixels
    doc.follow_display_safe_area = true      # or take them from DisplayServer.get_display_safe_area()

`get_safe_area_insets()` reports them back as a Vector4 in that order.

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
pointer the node already forwards. The choice changes live selectedness,
so `:checked`, paint and `get_element_value` agree while the `selected`
attributes retain their reset defaults.

    doc.open_select("#quality")
    doc.close_select()
    doc.get_open_select()                          # the id, or ""

`size="1"` and invalid/zero sizes use dropdown behavior. `multiple` or a
parsed size greater than one makes a listbox. Plain clicks replace selection,
Ctrl/Meta clicks toggle, and Shift extends a range from the anchor. Dragging
updates rows immediately and commits input/change once on release. Native
keyboard navigation skips disabled rows and keeps its active row visible;
Ctrl+A selects enabled rows in a multiple select. The explicit pointer method
accepts an optional modifier mask as its third argument. See
[FORM_STATE.md](../../docs/FORM_STATE.md#select-interaction) for the complete
contract and remaining select limitations.

**Input**

The Control receives Godot's GUI input while `interactive` is on, the default.
Native focus, mouse filters, visibility, canvas transforms and overlapping
controls determine which document receives events. CSS `pointer-events: none`
lets native hit testing continue beneath the document; descendants can opt
back in. A small outside-press observer dismisses dropdowns and auto popovers
after native routing without synthesizing clicks. Version checks keep it from
closing a popup newly opened by a native event handler.

Tab/Shift+Tab traverse the HTML tab order and continue through native Controls
at either end. Keep sibling UI under a common Control (or configure explicit
native focus neighbors); separate viewport root Controls have separate Godot
focus roots. `set_focus` synchronizes Godot focus, and native focus loss clears
the HTML focus. Accepted edits stop before `_unhandled_input`; game actions
should use that path. Unicode key events, held edit keys, Ctrl/Cmd+A/C/X/V/Z,
Ctrl+Y and Shift+Ctrl/Cmd+Z are supported. Buttons use Enter down and Space up;
checkboxes, radio groups, ranges, summaries, popover triggers and implicit
submission have native keyboard actions. See [keyboard behavior and limits](../../docs/KEYBOARD_INPUT.md).
IME preedit, commit/cancel, composition signals and one-step undo are
implemented; see [IME evidence and compatibility limits](../../docs/IME.md).
Broader IME compatibility, remaining form behavior, touch-device integration and accessibility
remain release work.

A host routing its own -- a gamepad cursor, a touch surface, a menu that
decides who gets the keyboard -- calls these instead.

    doc.set_pointer(point, buttons) / doc.clear_pointer()
    doc.send_key(KEY_DOWN)                         # true when the document
                                                   # took it, so an unused key
                                                   # stays yours
    doc.send_text("x")
    doc.focus_next(false)                          # tab order

The explicit `focus_next` method wraps within HTML. Automatic native Tab
routing hands off at the document edge. Keys that the document does not use
remain available to the game's unhandled input handlers.

**Time**

`paused` stops CSS time: transitions and @keyframes hold where they are. The
document still updates when inputs change. Listbox and text-selection gestures
autoscroll on monotonic elapsed time, independently of `Engine.time_scale`.
Godot's normal scene-tree processing rules still apply. `update_document(dt)`
steps it by hand, which is what a test wants when it needs to see a transition
partway rather than wait for real frames.

Each document advances automatically once per Godot process frame. A parent
`_process` should not also call `update_document(delta)`; doing both advances
animations twice and runs the update twice. A host that owns time explicitly
can call `set_process(false)` and drive `update_document(dt)` itself.

Two things worth knowing, because both have caught someone out:

  * A transition means a change does NOT take effect immediately. Setting a
    width and measuring in the same breath reads the OLD width, correctly.
  * The animation belongs in the stylesheet. The demo's script sets a width and
    toggles a class; the easing and the low-health pulse are CSS. That is the
    reason to drive a UI this way rather than tweening from GDScript.


### Registering CSS font families

The current source provides `register_font_family(name, font)` on `WevaDocument`
(ABI minor 14, retained in installed preview107). Pass a Godot `Font`, such as
an imported `FontFile` or a `FontVariation`, and use that name in CSS:

```gdscript
@export var heading_font: Font

func configure_fonts(doc: WevaDocument) -> void:
    doc.register_font_family("Camp", heading_font)
    # CSS: h1 { font-family: Camp, sans-serif; }
```

Names are case-insensitive. Pass `null` to remove a registration; registering
the same resource again does nothing. Use an unquoted family name as the API
argument; comma-separated stacks belong in CSS. The method returns `false` for
an empty name or a name containing commas or quote characters.

Changes take effect on the next document update. The document retains the font
resource, watches its `changed` signal, and refreshes measurements and glyphs
when it changes. Normal Font resource setters emit that signal; low-level bitmap
cache edits require `emit_changed()`. HTML reload, theme replacement and disabling
then re-enabling engine fonts preserve the registrations. Families sharing a
resource share one change-signal connection. Registered families use their own
fallback chains; registration does not modify the resource's RID array.

This API supplies native resources to CSS family selection. A stylesheet's
`@font-face` rules load on their own (ABI minor 25; see "font face" in the
test scenes): each `src` entry is tried in the author's order, a `url()`
through the asset path and a `local("Name")` as an installed font found the
way Godot's `SystemFont` finds one (ABI minor 37); the game's own
`register_font_family` wins over a rule for the same family.

A family the page names with nothing behind it -- `font-family: "Segoe UI"`
and no `@font-face` or registration for it -- resolves to the installed font
of that name, as in a browser (ABI minor 41: the core lists the families the
styles name; the node loads the installed ones through
`OS.get_system_font_path`, with their bold and italic files as real
variants). The Unity host does the same, so a page draws with the same face
on both. `use_system_families` (default `true`) turns it off for output
identical on every machine; a name nobody has falls through the stack.


### Cancelable dialog close requests (current source)

ABI minor 15 adds `request_close_dialog(selector, result = null)`, `prevent_default()`,
`dialog_cancel_requested(id)` and `dialog_closed(id)`. These APIs are tested in a
native candidate and included in installed preview107.

```gdscript
func _ready() -> void:
    ui.dialog_cancel_requested.connect(_cancel_requested)

func _cancel_requested(id: String) -> void:
    if id == "settings" and has_unsaved_changes:
        ui.prevent_default()

func dismiss_settings() -> void:
    ui.request_close_dialog("#settings")
```

Alternatively use `on-cancel="handler_name"` on the dialog; the controller method
receives its ID. `on-close` handles completion. These markup handlers resolve on
the dialog itself, without ancestor fallback. Cancel handlers run while the
dialog is open; `prevent_default()` vetoes the request. It returns `false` outside
an active cancel event. `close_dialog()` remains an explicit, unconditional close.

The outer request drains handlers before returning. Nested UI updates do not
drain events again while a handler is running, so layout queries and updates do
not prematurely apply a close. A handler may close, reopen, remove or reload the
dialog; stale requests cannot close the replacement. Requests queued from inside
another handler wait for that handler to finish.

Escape dismisses auto/hint popovers first, then an open dropdown, then requests
closing the latest opened dialog. `closedby="none"` blocks dialog dismissal;
`any` and `closerequest` allow it. Missing/invalid values allow modal dismissal
and block non-modal dismissal. Cancel handlers can veto Escape too.

Both `close_dialog(selector, result = null)` and `request_close_dialog` accept
an optional result. Omitted/null preserves the current result; `""` clears it.
Use `get_dialog_return_value(selector)` to read the complete Unicode result,
including from a close handler, and `set_dialog_return_value(selector, value)`
to change the property without invalidating layout. A veto leaves the requested
result unapplied. Escape supplies an empty result. For example:

```gdscript
func accept_settings() -> void:
    ui.close_dialog("#settings", "accepted")

func _dialog_closed(id: String) -> void:
    if id == "settings":
        print(ui.get_dialog_return_value("#settings"))
```

Full browser task ordering remains incomplete. Form `method="dialog"` and
popover opening cancellation are supported; see [forms](../../docs/FORM_STATE.md).


The source queue preserves accepted close requests under notification overflow.
`request_close_dialog()` returns `false` if its bounded queue is already full of
pending requests; process queued events before retrying. Ordinary notifications
may be dropped under overflow. The rebuilt native candidate includes this policy.

### Custom validation (ABI minor 16; installed preview115)

`set_custom_validity(selector, message)` adds a game-specific error to an input,
textarea, select or button. An empty message clears it. A nonempty message blocks
normal form submission and emits `element_invalid(id)` / `on-invalid` on the
control. Set or clear it in a field-change or submit-button click handler, before
the submit event. For example, an `on-click="validate_name"` submit button can use:

```gdscript
func validate_name(_id: String) -> void:
    var reserved := ui.get_element_value("#name") == "World"
    ui.set_custom_validity("#name", "Choose another player name." if reserved else "")
```

`get_custom_validity(selector)` reads the complete stored Unicode message,
including while disabled. It does not return built-in or localized validation
messages. Reset preserves custom errors; cloning clears them. The candidate also
checks required values and number-input bounds/steps. Other constraints and full
browser validation APIs remain unfinished.


### Cumulative core timing (installed preview115)

`get_total_core_update_ms()` and `get_core_update_count()` are monotonic counters
for the lifetime of a `WevaDocument`. Subtract snapshots taken before work and
after the next `process_frame` to include synchronous and deferred core updates
in that interval. Reading the counters does not trigger an update. Reloading
markup does not reset the counters. `get_last_update_ms()` remains available for
inspecting a single update; it cannot account for multiple updates in one frame.

The timers cover the existing core-update interval. They exclude binding refresh,
font-backend setup, host texture synchronization, event handlers, canvas drawing,
and GPU execution. Do not label the difference as total UI cost or add it to API
timing, which already includes any synchronous core work. The sample benchmark
reports `changed_core_cpu` and `core_update_count` separately from API and whole-frame
measurements. This API is available in installed115.


Attribute reads (`has_element_attribute`, `get_element_attribute`) inspect current
DOM state without flushing layout. This allows a batch of style/DOM writes and
attribute checks to publish one final layout when it is needed. Geometry/style
queries retain synchronous update behavior. Missing elements still return false
or an empty string. The native timing tests verify this contract.


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
`required` still applies. Form-wide checking/reporting is described below;
validity pseudo-classes are supported. Localized validation messages remain
outside this API. Ordinary submission validation retains its existing behavior.
The C ABI equivalent is `weva_element_validity`, returning status plus an error
bitmask and a validation-candidate flag; both outputs are cleared on failure.


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
[Explicit validation evidence](../../docs/verification/explicit-validity121.json).

### Normalized control bindings (installed preview132)

Repeated state signals whose values are unchanged return zero from
`refresh_bindings()`, including booleans bound to checkboxes and numbers normalized
by range controls. Actual changes, validity changes and composition commits still
publish. Model values and paths are not truncated to the short-read buffer size.

The validity APIs also support fieldsets. A fieldset's own custom error is exposed
in its validity snapshot but does not block validation or change its CSS aggregate
`:valid`/`:invalid` result; its eligible descendant controls determine that result.
