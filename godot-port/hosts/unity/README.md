# Unity host: the native plugin

The Unity host runs the same C++ core as the Godot host. This directory builds
that core as `weva_core` (a shared library with nothing but the C ABI exported)
and generates the C# P/Invoke layer the Unity package calls it through. The
managed side of the host lives in the package:

| Piece | Where |
| --- | --- |
| Generated P/Invoke surface | `Packages/com.wevaui/Runtime/Native/WevaNative.g.cs` |
| Hand-written wrapper (`NativeDocument`) | `Packages/com.wevaui/Runtime/Native/NativeDocument.cs` |
| Plugin binary and its import settings | `Packages/com.wevaui/Runtime/Native/Plugins/x86_64/` |
| EditMode round-trip tests | `Packages/com.wevaui/Tests/Editor/Native/` |

The C# engine in the same package is untouched by any of this; the two coexist
while the host over the core is built up.

## Build the plugin

```
cmake -S godot-port/hosts/unity -B build-unity -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DWEVA_UNITY_BIN=<repo>/Packages/com.wevaui/Runtime/Native/Plugins/x86_64
cmake --build build-unity
build-unity/weva_core_load_test build-unity/bin/weva_core.dll   # or .so
```

`WEVA_UNITY_BIN` is where the plugin lands; pointing it at the package's
`Plugins/x86_64` folder installs it for the editor in one step (the binary is
gitignored, its `.meta` is tracked). The build stamps `weva_core.dll.build.json`
beside it with the library digest, source digest, git state, compiler and ABI
version, the same fingerprint the Godot packager verifies.

The plugin statically links the C runtime and ICU (with the filtered data the
Godot extension uses, `uemoji.icu` included), so it carries no dependency.
Only `weva_*` functions are exported: `gen_exports.py` lists them from
`weva_c.h` and `src/weva_unity.h` into a `.def` (MSVC) or version script (GNU).

`src/weva_unity.h` is the plugin's own surface, kept to what a managed host
cannot find out by itself. Today that is `weva_unity_sizeof`, which reports a
struct's native size so the C# tests can catch a layout drift between the
header and the generated mirrors.

## Bindings

```
python godot-port/hosts/unity/gen_bindings.py \
    --header godot-port/libweva/include/weva_c.h --header godot-port/hosts/unity/src/weva_unity.h \
    --out Packages/com.wevaui/Runtime/Native/WevaNative.g.cs
```

Enums, structs and functions come out as blittable C#: unsafe pointers,
`nuint` for `size_t`, `fixed` buffers for arrays, `delegate* unmanaged[Cdecl]`
for callback fields, `[DllImport]` externs with the Cdecl convention. Nothing
is marshalled, so the same file serves Mono and IL2CPP. The generated file is
checked in; CI regenerates it with `--check` and fails on drift.

Callbacks a host implements (`weva_font_backend`, `weva_asset_reader`,
`weva_binding_source`) are static methods marked `[MonoPInvokeCallback]`,
reached through Cdecl delegates kept alive for the adapter's lifetime and
cast to the generated `delegate* unmanaged[Cdecl]` fields. That is the
IL2CPP-safe form Unity's class library allows: `UnmanagedCallersOnlyAttribute`
is not in it.

## Fonts: `UnityFontBackend`

The core's `weva_font_backend` callback table, filled over Unity's FontEngine
(TextCore) the way the Godot host fills it over TextServer; the core never sees
a Unity type. Faces are adopted from a `Font` asset, a file, or bytes (which is
how `@font-face` data arrives through the document's asset reader); fallback
faces answer per code point for glyphs the primary lacks, and a glyph id
carries which face it came from. Real bold and italic faces are registered as
variants. `SyncCssFontFaces` reads the stylesheet's `@font-face` rules from
the core and registers them, as the Godot host's `sync_css_font_faces` does.

What FontEngine gives and does not give:

- Metrics come from the face at a whole-pixel size (the same rounding the
  Godot adapter uses), unhinted, so advances are the engine's own.
- There is no public shaper. Shaping is one glyph per code point plus GPOS
  pair positioning, read once per adjacent pair through
  `GetPairAdjustmentRecords` with a two-glyph list (the call TextMeshPro fills
  a font asset with; values are design units, and a pair covered by more than
  one subtable is listed once per subtable, the first being the one OpenType
  applies; a long glyph list answered the same pair in a different order,
  which is why the query is per pair). `GetPairAdjustmentRecord(first,
  second)` is NOT used: on 6000.4 it returns an uninitialised record for a
  pair the face does not kern and crashed the editor. No ligatures or mark
  positioning until a real shaper is bound (the C# engine's ATG path is the
  candidate).
- Coverage bitmaps come from `TryAddGlyphToTexture` (reflection-bound, as the
  C# engine's SDF rasterizer binds it) in SMOOTH mode; colour glyphs are not
  rasterized yet.
- FontEngine is one state machine for the process, so the adapter forgets its
  active face before every document update (`NativeDocument.BeforeUpdate`).

## Rendering: `NativeDocumentRenderer` and `WevaNativeDocument`

The core publishes textured triangle lists in document pixels with clipping
and opacity resolved (scissored geometry is clipped before it is published).
The renderer mirrors the document's textures by id, merges consecutive draws
that share a texture into one mesh, and draws them with
`Hidden/Weva/NativeMesh`. Colour follows the Godot host: the core's vertex
colours are linear, its texels are sRGB bytes (white plus coverage for the
glyph atlas), and a page composites in gamma space, so the offscreen path
encodes vertex colours to sRGB and blends into a raw target; the in-pass path
into URP's linear colour buffer blends in linear space instead (edges differ
slightly, the way the C# engine's pre-sRGB-composite path did).

`WevaNativeDocument` is the MonoBehaviour: a document from a `TextAsset` or
inline markup, the package's UI face with its bold, italic and symbol faces,
registered with the URP pass as an `IUINativePaintSource` so its meshes are
drawn in the same pass as C# documents (beneath them). Input, events and
bindings are later steps of the plan.

## Comparing with the Godot host

`compare_hosts.py` applies the Godot host's render comparison
(`compare_render.py`'s metric: structural differences gate, edge differences
are reported) to `godot.ppm` and `unity.ppm` in a page directory. The Godot
image comes from the host's `capture.tscn`; the Unity image from the EditMode
test `Parity_RendersPagesForComparison`, which renders every directory named
by `WEVA_NATIVE_PARITY` (`;`-separated, each holding `<name>.html` and
`<name>.css`) at 1280x720:

```
godot --path godot-port/hosts/godot/project --rendering-driver opengl3 --scene res://capture.tscn \
    -- --html <dir>/<name>.html --css <dir>/<name>.css --size 1280x720 --out <dir>/godot.ppm --png <dir>/godot.png
WEVA_NATIVE_PARITY=<dir> Unity.exe -batchmode -projectPath <repo> -runTests -testPlatform EditMode \
    -testFilter Weva.Tests.EditorTests.Native.NativeDocumentRenderTests
python godot-port/hosts/unity/compare_hosts.py <dir>
```

Give both sides the same font bytes (an `@font-face` rule in the page's CSS)
or the comparison measures two fonts, not two hosts.

## Input, events, bindings

`NativeDocument` carries the interaction calls the Godot host's `_gui_input`
makes (pointer state, key edges, text input, paste, wheel, focus stepping,
IME composition) and drains the core's event queue into `NativeEvent`
records. `NativeInputFeed` reads the Input System each frame and applies the
same rules as the Godot host (a Space the button took is not also text; Ctrl
shortcuts go to selection, clipboard and history). `NativeBindings` serves
`{{ path }}`, `data-class`, `data-each` and `data-model` from a C# dictionary
graph (or a resolver) and writes control edits back keeping the model's
types; `WevaNativeDocument.Bind(model, controller)` dispatches `on-<event>`
handler names to the controller's methods. The Frontier Camp logic lives in
the tests (`FrontierCampState`, `FrontierCampController`) and drives the
example's own `camp.html`.

## `@import`

An `@import` in a stylesheet is fetched through the same `AssetReader` and
`BasePath` a `url()` image goes through, so a sheet split across files ships
the way its images do. A sheet the reader cannot return is dropped and named
in `CssDiagnostics`.

## The mouse cursor (ABI minor 29)

`NativeDocument.Cursor` is the CSS `cursor` under the pointer as the keyword the
core settled it to (`pointer`, `text`, `not-allowed`, `grab`, ... or `default`);
`CursorAt(x, y)` asks about any point. `WevaNativeDocument` does not set the
Unity cursor itself, because `Cursor.SetCursor` wants a texture per shape the
project supplies; map the keywords you have textures for.

## Colour scheme (ABI minor 28)

`NativeDocument.SetColorScheme(dark)` is the switch `light-dark()` and
`@media (prefers-color-scheme)` follow; the core defaults to light and never
reads the OS. An element's own `color-scheme` declaration wins over it.

## The inspector surface (ABI minor 27)

`NativeDocument.Parent` / `Children`, `TryGetBoxModel` (margin, border and
padding edges plus the content box), `MatchedRules` (every declaration that
applies, sheet rules and the style attribute, in cascade order with the winner
per property marked) and `ComputedStyle` (every registered property resolved,
then the custom properties in scope) wrap the tooling calls the core gained
for editor panels. `NativeInspectorModel` turns them into what an Elements
panel shows (tree, search, rule blocks winners-first, computed style, box
model) and `Window/Weva/Native Elements` renders it for a `WevaNativeDocument`
in the scene. The C# goldens through the core: `goldens_from_unity.py`
(layout against the Chrome captures, paint against the C# baselines).

## Layout dump and the oracle

`weva_document_layout_dump` (ABI minor 26) serves the dump the differential
oracle compares from the core's own box tree, the walk `weva_dump` shares, so
the Unity host's dump is the tool's by construction. `SyntheticFontBackend` is
the oracle's face (0.45em glyphs, 0.6em monospace, 0.85/0.293/1.143em lines,
fractional leading kept through `weva_document_set_font_leading_rounding`).
`oracle_from_unity.py` dumps a corpus through the Unity editor in one run (the
EditMode test `Manifest_DumpsEveryCaseForTheOracle` reads
`WEVA_NATIVE_DUMP_MANIFEST`) and compares each case with `weva_dump` using
`run_oracle.py`'s own comparison:

```
python godot-port/hosts/unity/oracle_from_unity.py godot-port/tools/oracle/corpus/samples \
    --weva-dump wsl:/root/weva/build-gcc/tools/weva_dump/weva_dump --out .utmp/oracle-unity
```

## Tests

The C++ load test (`tests/load_test.cpp`) opens the plugin through the dynamic
loader, checks the ABI version, the layout probe, and runs a document end to
end through the exported symbols alone. It is the gate for the plugin build and
runs in CI on Windows and Linux.

The EditMode tests run the same round trip from C# in the Unity editor, with
the struct-size comparison, the font adapter (checked against FontEngine's own
answers) and the render adapter (offscreen renders read back pixel by pixel,
which needs a graphics device: no `-nographics`):

```
Unity.exe -batchmode -projectPath <repo> -runTests -testPlatform EditMode \
    -testFilter Weva.Tests.EditorTests.Native -testResults out.xml -logFile out.log
```

The editor is not on the CI runners; run this where it is installed and keep
the result with the receipt (`docs/verification/unity-host-prototype.json`).
