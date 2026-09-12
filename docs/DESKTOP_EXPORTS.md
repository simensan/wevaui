# Desktop export verification

The installed addon runs in native Godot desktop exports. The verification
uses a fresh project created from the addon ZIP, with imported PNG/SVG images,
HTML/CSS files and the packaged GDScript example. It does not depend on the
development gallery or a .NET runtime.

## Windows preview56 (2026-09-07)

The Windows-only preview56 ZIP is verified with Godot 4.7.2 and installed in
the development project. Fresh-project and packed-resource checks pass (8 and
10 checks), with 17 example/controller checks in each. Relocated debug,
release and embedded-pack executables each pass 13 runtime/resource checks
and 17 example checks with the source project unavailable. All three export
captures match the fresh project's PNG SHA-256:
`b823c94b655b6e68c7a8123841ff2e3431ace2a01dbbf9ea935f230181f28302`.

This build includes flex/grid item containment, border/button corrections,
input baselines and active-font centering, type-change layout invalidation,
and short/empty-field caret and selection fixes. Its 19-file ZIP contains the
exact tested DLL and a matching manifest. Logs and relocated exports are under
`.utmp/input-baseline56/exports/`; package and installation records are beside
them. Linux evidence remains preview40. Remaining conformance findings and
performance measurements are in [ORACLE.md](ORACLE.md) and
[PERFORMANCE.md](PERFORMANCE.md).

## Verified on 2026-09-06

The Windows column also passes on 2026-09-07 for the Windows-only preview53 ZIP
containing preserved-newline sizing, custom-property lookup and prepared gradient
interpolation changes, optimized blur kernels and scratch buffers, and computed
font-size inheritance, geometry buffer reservations, child paint-order traversal,
cached corner-radius parsing, clip-preparation buffer reservations and ownership
transfer of temporary paint meshes, computed line-height inheritance, and value
clamps in background interpolation and output conversion.
Linux evidence remains preview40; it has not been rerun for these core changes.
Preview53's three native example
captures match the project PNG SHA-256
`3e00c6d9821d8d88361fe607be7d988d411fafe519fe9e2f7825b9bb9c5b1f62`.
Its logs and relocated exports are under `.utmp/hud-raster53/exports/`;
artifact hashes and scope are in [PERFORMANCE.md](PERFORMANCE.md).

| Configuration | Windows x86_64 | Linux x86_64 |
|---|---|---|
| Godot editor and official export templates | 4.7.2 stable, standard | 4.7.2 stable, standard |
| Debug executable with separate PCK | Pass | Pass |
| Release executable with separate PCK | Pass | Pass |
| Release executable with embedded PCK | Pass | Pass |
| Exported extension matches selected library's SHA-256 | All three | All three |
| Resource/runtime checks per configuration | 13 pass | 13 pass |
| Example/controller/binding checks per configuration | 17 pass | 17 pass |
| Example pixels versus the project, OpenGL 3 | Exact in all three | Exact in all three |

The extension libraries use CMake Release builds with `godot-cpp`
`template_debug` bindings at API 4.7. Those same libraries load in both debug
and release Godot templates. The package's `build.json` records their hashes.

The check exports all configurations, renames the source project, relocates
the exported directories and launches their executables. Godot must copy the
extension itself; the native test never supplies a missing library after
export. Runtime assertions require a template without editor features and
the requested debug/release mode. The imported image originals are absent
from the pack, while the imported resources and markup must still load.

The example checks pointer and keyboard controller calls, text-field writeback,
Unicode length limits and undo, form reset, bound labels, and native select
typeahead against an accented option label, held listbox scrolling and continuous
textarea selection/undo after replacing bound markup. Typeahead exercises embedded ICU
without a source tree or external data file. Render captures use the engine font and are compared within
each platform; identical pixels across Windows and Linux are not assumed.
Both exported example images were also visually reviewed.

## Reproduce

Install desktop export templates matching the editor version through Godot's
Export Template Manager. The verification above uses the standard editor and
templates from the [official 4.7.2 release](https://github.com/godotengine/godot-builds/releases/tag/4.7.2-stable),
checked against its published SHA-512 sums. See Godot's
[export instructions](https://docs.godotengine.org/en/stable/tutorials/export/exporting_projects.html)
for template installation and export filters.

From `godot-port/`, on each platform being shipped:

```sh
python3 hosts/godot/check_export.py --godot /path/to/godot \
    --addon /path/to/weva-preview.zip --native --keep
```

Use the console editor executable and `python` on Windows. `--keep` prints
the isolated fixture directory and preserves export/runtime logs. Add
`--render` to capture the example from the project and every native export,
then require identical PNG hashes. That mode needs a display with OpenGL 3;
its windows are positioned outside the desktop. The ordinary native checks
are headless.

`check.sh` includes native exports in its addon gate. Matching desktop
templates are required; missing templates fail that gate. Set
`WEVA_EXPORT_RENDER=1` to include the display-dependent pixel comparison.
Without `--native`, `check_export.py` retains its template-free project/PCK
check, but this does not prove native executable export.

## Scope

This proves the tested desktop configurations and representative addon
workflow. Other Godot versions, CPU architectures, export targets, renderers
and native build configurations need their own checks. The addon remains a
development preview while the interaction and conformance work in
[PRODUCT_READINESS.md](PRODUCT_READINESS.md) is unfinished.
