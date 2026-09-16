# Weva for Godot

A native HTML/CSS UI addon for Godot 4.7. `WevaDocument` is a Control;
`WevaView` adds file loading, hot reload and signal-driven bindings.
The addon shares its C++ engine with the Unity host and needs no .NET runtime.

**Development preview.** Windows/Linux x64 builds have been tested. Read
[product readiness](../../docs/PRODUCT_READINESS.md) before choosing it for a
shipping project. Native Godot controls have a [text-shaping limitation](../../docs/GODOT_TEXT_SHAPING.md#stock-godot-472-limitation);
the verified fix uses a patched editor and matching export templates.

## Use the addon

Extract a packaged addon into your project root, open the project once to
import resources, then run `addons/weva/example/example.tscn`. The archive's
`build.json` identifies its native libraries and source inputs.

- [Addon guide](ADDON_README.md): installation, bindings, input and export setup.
- [Frontier Camp](../../examples/frontier_camp/README.md): a standalone game integration.
- [Western survival sample](project/samples/western_survival/README.md): an interactive HUD in the gallery.
- [Native API reference](REFERENCE.md#driving-a-document-from-gdscript).

## Building

From the repository root, with CMake, a C++20-capable compiler, Ninja and Python 3.9+
(the public core API remains C++17):

```sh
git clone https://github.com/godotengine/godot-cpp
git -C godot-cpp checkout 26fb7ab5821e6a1096f62c22f7462d1d70caa332
cmake -S hosts/godot -B build-godot -G Ninja \
  -DCMAKE_BUILD_TYPE=Release -DGODOT_CPP_DIR=$PWD/godot-cpp \
  -DGODOTCPP_API_VERSION=4.7
cmake --build build-godot --parallel 8
```

CMake fetches pinned ICU sources and embeds its runtime data. The extension
lands in `hosts/godot/project/addons/weva/bin/`. Open
`hosts/godot/project/project.godot` to run the gallery.

Use [Windows build instructions](REFERENCE.md#windows-msvc) for MSVC and
[build details](REFERENCE.md#building) for custom APIs and isolated output paths.
Close any editor holding the library before replacing it, or set
`WEVA_GODOT_BIN` to a separate test directory.

## Package and verify

Use the [packaging commands](REFERENCE.md#make-an-installable-preview) to make
an addon ZIP with a verified build manifest. Run the [release checks](../../docs/RELEASE.md)
with the editor and matching export templates for the target platform.
A passing editor example alone does not qualify an exported game.

The [host reference](REFERENCE.md) includes scene tests, render comparisons,
font integration and native APIs. [Current performance measurements](../../docs/RUNTIME_PERFORMANCE.md)
record their workload and hardware limits; old preview timings are historical.
