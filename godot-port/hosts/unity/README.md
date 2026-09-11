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

Callbacks a host implements (`weva_font_backend`, `weva_render_backend`,
`weva_binding_source`) take `[UnmanagedCallersOnly(CallConvs = new[] {
typeof(CallConvCdecl) })]` static methods, which IL2CPP compiles to plain C
function pointers.

## Tests

The C++ load test (`tests/load_test.cpp`) opens the plugin through the dynamic
loader, checks the ABI version, the layout probe, and runs a document end to
end through the exported symbols alone. It is the gate for the plugin build and
runs in CI on Windows and Linux.

The EditMode tests run the same round trip from C# in the Unity editor, with
the struct-size comparison on top:

```
Unity.exe -batchmode -nographics -projectPath <repo> -runTests -testPlatform EditMode \
    -testFilter Weva.Tests.EditorTests.Native -testResults out.xml -logFile out.log
```

The editor is not on the CI runners; run this where it is installed and keep
the result with the receipt (`docs/verification/unity-host-prototype.json`).
