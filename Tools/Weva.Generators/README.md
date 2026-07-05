# Weva Roslyn Source Generators

Implements PLAN.md Section 9 row 5: "Data binding uses C# source generators."

The generator at `UIBindGenerator.cs` emits an
`Weva.Binding.Generated.IBindingAccessor` implementation for every type
that declares `[UIBind]` fields/properties or `[UIElement(id)]` fields. The
runtime side (`BindingResolver.GetAccessor`) prefers this generated path and
falls back to reflection when the generator hasn't run.

## Building the analyzer DLL

This `.csproj` is **not** compiled by Unity's Asset Pipeline. Unity loads
the compiled `.dll` (with the `RoslynAnalyzer` asset label) at edit time.
You must build the DLL out-of-band.

From a developer shell:

```pwsh
dotnet build Packages/com.wevaui/Editor/Generators/Weva.Generators.csproj -c Release
copy Packages/com.wevaui/Editor/Generators/bin/Release/netstandard2.0/Weva.Generators.dll `
     Packages/com.wevaui/Editor/Generators/Weva.Generators.dll
```

The accompanying `Weva.Generators.dll.meta` (checked in) carries the
`RoslynAnalyzer` label. Unity then auto-runs the generator over every
assembly that references `Weva.Runtime`.

If the DLL is absent the runtime reflection path still produces correct
results; everything just runs slower and allocates a touch more.

## Required Unity / Roslyn versions

The .csproj targets `netstandard2.0` and Roslyn 4.4.0. Unity 6 / 6000.4
ships Roslyn 4.5+, so the analyzer loads cleanly. Older Unity LTS streams
(2022.3 LTS and prior) bundle Roslyn 4.1 and will refuse to load this
analyzer. The reflection fallback in `BindingResolver` keeps those Unity
versions working.

## Edge cases (mirrored from `UIBindGenerator.cs`)

- **Sealed but not partial**: emits a separate `T_BindingAccessor` class and
  registers it via `[ModuleInitializer]` -> `BindingResolver.RegisterAccessor`.
  Diagnostic `UIB001` is raised at `Info` severity. Mark the type `partial`
  to embed the accessor directly.
- **Generic type with `[UIBind]`**: skipped with diagnostic `UIB002`. v2.
- **Nested type**: emitted using the fully-qualified containing-type chain,
  but only if every containing type is also `partial`. Otherwise the
  standalone path is used.
- **Inheritance**: only the type that *declares* the `[UIBind]` member gets
  generated code. Members inherited from a base whose accessor wasn't
  generated still resolve via reflection.
- **Static `[UIBind]` member**: skipped with diagnostic `UIB003`.
- **Property without getter**: skipped with diagnostic `UIB004`.
- **Indexer marked `[UIBind]`**: skipped with diagnostic `UIB005`.

## Files

- `Weva.Generators.csproj` — analyzer project. NOT compiled by Unity.
- `UIBindGenerator.cs` — the generator itself.
- `AssemblyInfo.cs` — assembly metadata.
- `Weva.Generators.dll` — produced by `dotnet build`. Place here for Unity to load.
- `Weva.Generators.dll.meta` — Unity asset metadata with the `RoslynAnalyzer` label (committed once the DLL exists).
