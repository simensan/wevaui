# Unity host: the native plugin

The Unity host runs the same C++ core as the Godot host. This directory builds
that core as `weva_core` (a shared library with nothing but the C ABI exported)
and generates the C# P/Invoke layer the Unity package calls it through. The
managed side of the host lives in the package:

| Piece | Where |
| --- | --- |
| Generated P/Invoke surface (internal, like all of `Weva.Native`; the editor and test assemblies see it through `InternalsVisibleTo`) | `Packages/com.wevaui/Runtime/Native/WevaNative.g.cs` |
| Hand-written wrapper (`NativeDocument`) | `Packages/com.wevaui/Runtime/Native/NativeDocument.cs` |
| Plugin binary and its import settings | `Packages/com.wevaui/Runtime/Native/Plugins/x86_64/` |
| EditMode round-trip tests | `Packages/com.wevaui/Tests/Editor/Native/` |

The C# engine that shared the package until 2026-09-13 is deleted (Phase 4.3);
the package is this host.

## Build the plugin

```
cmake -S hosts/unity -B build-unity -G Ninja -DCMAKE_BUILD_TYPE=Release \
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
python hosts/unity/gen_bindings.py \
    --header libweva/include/weva_c.h --header hosts/unity/src/weva_unity.h \
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
the core and registers them, as the Godot host's `sync_css_font_faces` does:
each `src` entry in the author's order, a `url()` through the asset reader
and a `local("Name")` as an installed font (`Font.GetOSInstalledFontNames`,
`CreateDynamicFontFromOSFont`), the first that loads serving the face (ABI
minor 37).

What FontEngine gives and does not give:

- Metrics come from the face at a whole-pixel size (the same rounding the
  Godot adapter uses), unhinted, so advances are the engine's own.
- There is no public shaper, so the backend carries one
  (`UnityFontBackend.Shaping.cs`): the core answers what Unicode says about
  a run (direction, mirroring, joining types, scripts -- ABI minor 40), the
  font's GSUB is read from its own bytes (single, multiple, ligature and
  coverage-based chaining lookups; extensions resolved), FontEngine answers
  the positioning. Pair kerning is read once per adjacent pair through
  `GetPairAdjustmentRecords` with a two-glyph list (the call TextMeshPro fills
  a font asset with; values are design units, and a pair covered by more than
  one subtable is listed once per subtable, the first being the one OpenType
  applies; a long glyph list answered the same pair in a different order,
  which is why the query is per pair); mark anchors through
  `GetMarkToBaseAdjustmentRecord` / `GetMarkToMarkAdjustmentRecord` (values
  scaled to the active face size -- measured -- and taken back to design
  units). NOT used: `GetPairAdjustmentRecord(first, second)`, which on 6000.4
  returns an uninitialised record for a pair the face does not kern and
  crashed the editor; and every GSUB record query -- `GetOpenTypeLayoutTable`
  lists no lookups and `GetSingleSubstitutionRecords` crashes the editor on
  an extension lookup, which is every Arabic lookup in Segoe UI. A face
  adopted as a `Font` asset has no bytes to read and gets no substitutions.
- Coverage bitmaps come from `TryAddGlyphToTexture` (reflection-bound) in
  SMOOTH mode; colour glyphs are not rasterized yet.
- FontEngine is one state machine for the process, so the adapter forgets its
  active face before every document update (`NativeDocument.BeforeUpdate`).

## Rendering: `NativeDocumentRenderer` and `WevaDocument`

The core publishes textured triangle lists in document pixels with clipping
and opacity resolved (scissored geometry is clipped before it is published).
The renderer mirrors the document's textures by id, merges consecutive draws
that share a texture into one mesh, and draws them with
`Hidden/Weva/NativeMesh`. Colour follows the Godot host: the core's vertex
colours are linear, its texels are sRGB bytes (white plus coverage for the
glyph atlas), and a page composites in gamma space, so the offscreen path
encodes vertex colours to sRGB and blends into a raw target; the in-pass path
into URP's linear colour buffer blends in linear space instead (edges differ
slightly from the gamma composite). `backdrop-filter` draws (the shape plus
the colour matrix the core composed) copy the target just before them --
into a texture the pass owns and imports, filled by the shader's own copy
pass with the target bound by identifier -- and draw the shape through
`Hidden/Weva/NativeBackdrop` (a 7x7 Gaussian, the same kernel as the Godot
host's, then the matrix in sRGB), replacing what is behind; runs of geometry
split around them so each sees what was drawn beneath.

`WevaDocument` (`Weva.WevaDocument`, at `Runtime/WevaDocument.cs`; it was
`Weva.Native.WevaNativeDocument` until 2026-09-13, when it took the name, the
script GUID and the serialized field names of the C# engine's component, since
deleted) is the MonoBehaviour:
a document from `DocumentAsset` + `StylesheetAssets` (the page's own `<link
rel="stylesheet">` sheets first, fetched next to the asset in the editor and
baked into the component for a player by `WevaDocumentLinkBaker`) or inline
markup, the package's UI face with its bold, italic and symbol faces,
registered with the URP pass as an `IUINativePaintSource`; `UIRenderGraphPass`
draws every registered document's meshes into the camera colour target in
`SortingOrder`, one pass per renderer. It feeds
the Input System through `NativeInputFeed`, drains the core's event queue
into C# events and dispatches `on-<event>` handler names to a controller.

## Comparing with the Godot host

`compare_hosts.py` applies the Godot host's render comparison
(`compare_render.py`'s metric: structural differences gate, edge differences
are reported) to `godot.ppm` and `unity.ppm` in a page directory. The Godot
image comes from the host's `capture.tscn`; the Unity image from the EditMode
test `Parity_RendersPagesForComparison`, which renders every directory named
by `WEVA_NATIVE_PARITY` (`;`-separated, each holding `<name>.html` and
`<name>.css`) at 1280x720:

```
godot --path hosts/godot/project --rendering-driver opengl3 --scene res://capture.tscn \
    -- --html <dir>/<name>.html --css <dir>/<name>.css --size 1280x720 --out <dir>/godot.ppm --png <dir>/godot.png
WEVA_NATIVE_PARITY=<dir> Unity.exe -batchmode -projectPath <repo> -runTests -testPlatform EditMode \
    -testFilter Weva.Tests.EditorTests.Native.NativeDocumentRenderTests
python hosts/unity/compare_hosts.py <dir>
```

Give both sides the same font bytes (an `@font-face` rule in the page's CSS)
or the comparison measures two fonts, not two hosts.

## Input, events, bindings

Every input decision the two hosts share, and the test that pins it on each,
is tabled in [`docs/INPUT_PARITY.md`](../../docs/INPUT_PARITY.md).

`NativeDocument` carries the interaction calls the Godot host's `_gui_input`
makes (pointer state, key edges, text input, paste, wheel, focus stepping,
IME composition) and drains the core's event queue into `NativeEvent`
records. `NativeInputFeed` reads the Input System each frame and applies the
rules the Godot host's `_gui_input` applies, each pinned by
`NativeInputFeedTests` through the Input System's test fixture: the pointer's
y flipped by the viewport height; the wheel as Chrome's 100px per notch; a Space the
button took not also arriving as text; the command chord being Ctrl, or Cmd
on macOS, for selection, clipboard and history; the pointer cleared when it
leaves the surface or the application loses focus; keys gated by
`AcceptsKeyboard` and by application focus; a touch tap as a click and a
touch drag as a pan; geometry flushed before each frame's hit tests. What
the Input System cannot supply because it reports edges only -- key
auto-repeat and double-click detection -- the feed turns on in the core
(ABI minor 39), where it is tested once for both hosts. A gamepad moves
focus by geometry through the d-pad or left stick with a held-direction
repeat, South accepts (or raises `TextEntryRequested` on a text field when
`GamepadTextEntry` is set, since a pad cannot type), East cancels and the
shoulders step the tab order -- the Godot host's `navigation_action` without
the InputMap. An OS IME composes into the focused field through
`Keyboard.onIMECompositionChange` and commits with the text the OS delivers
as it closes, which is never also typed; the feed enables the IME and places
its window at the caret only over a text control. `NativeBindings` serves
`{{ path }}`, `data-class`, `data-each` and `data-model` from a C# dictionary
graph (or a resolver) and writes control edits back keeping the model's
types; `WevaDocument.SetController(controller)` scans the controller's
`[UIBind]` members as binding roots (`UIBindResolver`: dotted paths walk
public members, dictionary keys and list indices; `data-model` writes back
keeping the member's type), polls a plain controller once a frame and an
`IBindingVersion` one only when it bumps, and dispatches `on-<event>` handler
names to its public methods; `Bind(model, controller)` adds a dictionary the
resolver falls through to. The Frontier Camp logic lives in
the tests (`FrontierCampState`, `FrontierCampController`) and drives the
example's own `camp.html`.

## `@import`

An `@import` in a stylesheet is fetched through the same `AssetReader` and
`BasePath` a `url()` image goes through, so a sheet split across files ships
the way its images do. A sheet the reader cannot return is dropped and named
in `CssDiagnostics`.

## Counters and the inspector's hit test (ABI minor 30)

`NativeDocument.Stats()` returns the engine counters for a profiler or stats
window: the last update's stage timings (cascade, animate, boxes, layout,
paint) and total in milliseconds, element/box/draw/texture counts, the paint
pass's texture-cache hits and misses, and the cascade's running totals (diff
them between frames). `ElementAtForDevTools(x, y)` is the hit test an Elements
panel wants: `pointer-events: none` and `visibility: hidden` hide nothing from it.

## Change notification (ABI minor 35)

`NativeDocument.ChangedElements()` lists the elements the last `Update` restyled,
each with how far the change reached (`WEVA_CHANGE_PAINT`, `_LAYOUT`, `_BOXES`),
and `StructureVersion` moves whenever elements come or go -- what an inspector
uses to highlight a change and to know when its handles need a re-walk.

## HTML parse diagnostics (ABI minor 34)

`NativeDocument.HtmlDiagnostics` lists what the last load or reload recovered
from -- a mismatched or stray end tag, an end tag on a void element, an element
still open at the end -- one `line:column: message` per line. Clean markup gives
an empty string; the document is loaded either way.

## mix-blend-mode (ABI minor 33)

Every draw carries `blend_mode`. `NativeDocumentRenderer` splits batches on it
and renders multiply, screen, darken and lighten as blend states of the mesh
shader (`_WevaSrcBlend` / `_WevaDstBlend` / `_WevaBlendOp`); overlay, the light
modes, difference/exclusion and the HSL modes need the backdrop in a shader and
draw normally.

## Hot reload (ABI minor 32)

`NativeDocument.ReloadHtml(html)` diffs new markup onto the live document:
elements it can match (same tag at the same position, or the same `id` /
`data-key` among their siblings) keep their handle, focus, scroll, form value
and running transitions; attributes and text update in place; the rest is
inserted or removed. `LoadHtml` still replaces everything.

## The box tree (ABI minor 31)

`NativeDocument.Boxes()` returns every box of the layout tree in tree order,
anonymous, line and text boxes included: parent index, kind, owning element,
the border box in document coordinates (scroll not applied), the margin,
border and padding edges, the box's own scroll offset and a text box's run
(`NativeDocument.TextOf`). It is what a Chrome-style outline overlay draws
from; `BoxOutlineRenderer` can be fed from it instead of the C# Box tree.

## The mouse cursor (ABI minor 29)

`NativeDocument.Cursor` is the CSS `cursor` under the pointer as the keyword the
core settled it to (`pointer`, `text`, `not-allowed`, `grab`, ... or `default`);
`CursorAt(x, y)` asks about any point. `WevaDocument` does not set the
Unity cursor itself, because `Cursor.SetCursor` wants a texture per shape the
project supplies; map the keywords you have textures for.

## Colour scheme (ABI minor 28)

`NativeDocument.SetColorScheme(dark)` is the switch `light-dark()` and
`@media (prefers-color-scheme)` follow; the core defaults to light and never
reads the OS. An element's own `color-scheme` declaration wins over it.

## Safe-area insets (ABI minor 36)

`NativeDocument.SetSafeAreaInsets(top, right, bottom, left)` is what
`env(safe-area-inset-*)` reads, zero until set. `WevaDocument.
FollowScreenSafeArea` feeds `Screen.safeArea` there each frame it changes,
scaled to the document's viewport, so a page pads around a notch or a
system bar as it would in a browser.

## The inspector surface (ABI minor 27)

`NativeDocument.Parent` / `Children`, `TryGetBoxModel` (margin, border and
padding edges plus the content box), `MatchedRules` (every declaration that
applies, sheet rules and the style attribute, in cascade order with the winner
per property marked) and `ComputedStyle` (every registered property resolved,
then the custom properties in scope) wrap the tooling calls the core gained
for editor panels. `NativeInspectorModel` turns them into what an Elements
panel shows (tree, search, rule blocks winners-first, computed style, box
model) and `Window/Weva/Elements` renders it for a `WevaDocument`
in the scene. `goldens_from_unity.py` renders the sample pages through the
core for the layout comparison against the Chrome captures.

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
python hosts/unity/oracle_from_unity.py Tools/oracle/corpus/samples \
    --weva-dump wsl:<build-gcc>/Tools/weva_dump/weva_dump --out .utmp/oracle-unity
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
