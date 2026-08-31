# Godot port feasibility

**Question:** could Weva be converted to Godot?

**Answer:** yes, and it is unusually well-positioned for it. ~82% of the runtime
is already engine-neutral C# that compiles and runs outside Unity today. The port
is not a rewrite — it is writing a new backend layer of roughly 24k LOC against
interfaces that already exist.

The reason this is cheap is not luck. The engine was built headless-first
(`AGENTS.md`: "The whole engine is **headless-testable**"), and three plain
`net8.0` console projects — `Tools/BaselineGen`, `Tools/PerfBench`,
`Tools/TestVerifyAll` — already compile the core with zero Unity assemblies.
Godot 4's .NET builds run C# on .NET 8, so that same core is a straight
recompile.

---

## 1. Where the Unity coupling actually is

Measured over `Packages/com.wevaui/Runtime` (682 files, 132,467 LOC):

| | files | LOC |
|---|---:|---:|
| Reference `UnityEngine` at all | 47 | 18,716 |
| Do not | 635 | 113,751 |

Excluding the directories the headless csproj files already exclude, the
engine-neutral core is **108,151 LOC (82%)**.

The whole Unity API surface the core needs is stubbed by
`Tools/TestVerifyAll/UnityEngineStub.cs` — **104 lines**. It provides
`[SerializeField]`, `[Tooltip]`, `Debug.Log*`, `Profiler.Begin/EndSample`,
`Application.isPlaying` and a couple of enums. That file is the honest measure
of how deep Unity reaches into layout, cascade, paint, events, and forms:
barely at all.

Coupling by directory:

```
Runtime/Rendering/URP        12 files    Runtime/Text/Tmp              2
Runtime/Paint/Images          7          Runtime/Rendering             2
Runtime/Forms/Bridge          7          Runtime/Paint/Conversion      2
Runtime/Text/TextCore         6          ...and 10 dirs with 1 each
Runtime/Rendering/Backend     6             (mostly Debug.LogWarning
Runtime/Text/Sdf              5              or a comment mentioning Unity)
```

Only **9 files** in the entire runtime touch `MonoBehaviour`/`ScriptableObject`.
Burst is used in exactly one method, behind `WEVA_BURST`. There is no
`NativeArray`, no Jobs, no `Unity.Mathematics` in the core — paint carries its
own `Rect`, `Transform2D`, `LinearColor`.

## 2. What ports for free

Everything the three headless projects already compile:

- HTML/CSS parsing, the cascade, selectors, media/container queries
- The entire layout engine — flex, grid, subgrid, floats, tables, multicol,
  positioning, anchor positioning, scrolling, inline/text layout, incremental
  relayout
- Paint: the `PaintCommand` display list, `BoxToPaintConverter`, gradients,
  filters, clip paths, masks, blend modes
- DOM, events + manipulators, forms logic, components, binding, reactivity,
  animations, view transitions, hot-reload coordination, the designer model
- **The tests.** 183,194 LOC of NUnit already run outside Unity via
  `TestVerifyAll`. They are the port's safety net and they come along unchanged.

There is also already a second, fully engine-independent `IRenderBackend`
implementation — `Runtime/Testing/Goldens/SoftwareRasterizer.cs`, 1,592 LOC with
zero Unity references. It is deliberately low-fidelity (glyphs are solid blocks,
gradients collapse to their first stop), so it is not shippable output, but it
proves the paint-command contract is genuinely engine-neutral and gives a Godot
port something that draws pixels on day one.

## 3. What has to be rewritten

~24,300 LOC, all of it behind interfaces that already exist:

| Area | LOC | Difficulty | Notes |
|---|---:|---|---|
| `Runtime/Rendering` (URP backend, batcher, render passes) | 11,465 | **Hard** | The main job |
| `Runtime/Text` (font load, glyph raster, SDF atlas) | 8,773 | **Hardest** | See §3.2 |
| `Runtime/DevTools` | 2,340 | Easy | Pure logic + an overlay renderer |
| `Runtime/WevaDocument.cs` | 890 | Easy | Thin `MonoBehaviour` over headless `UIDocumentState` |
| `Runtime/Forms/Bridge` | 832 | Easy | Implements `IUIPointerSource` etc. |
| `Runtime/Paint/Images/*.Unity.cs` | ~600 | Easy | Implements `IImageSource`/`IImageRegistry` |
| `Runtime/Document/UnityClock.cs` | 16 | Trivial | |
| Shaders (7 `.shader` + 1 `.hlsl`) | 3,904 | **Hard** | HLSL → Godot shading language |
| `Editor/` tooling | 5,910 | Medium | Rewrite as a Godot `EditorPlugin` |

The seams are already named and used: `IRenderBackend`, `IUICommandBuffer`,
`IUIPaintSource`, `IImageSource`, `IImageRegistry`, `IRawPixelImageSource`,
`IFontMetrics`, `IGlyphMetrics`, `ITextCoreBackend`, `FontLoader.IFaceLoader`,
`IUIPointerSource`, `IUIClock`, plus the `*.Unity.cs` file-suffix convention for
platform-bound partials. A Godot port writes `*.Godot.cs` siblings.

### 3.1 Rendering — hard but tractable

`BatchedURPRenderBackend` uploads a fat per-instance record
(`UIQuadInstance`, ~57 `float4` slots: rect, per-corner radii, color, brush
params, per-edge border widths/colors/styles, a 2×3 transform, a clip rect,
gradient stops, clip-path shape params, and four mask layers) and expands it in
the vertex shader from a `StructuredBuffer<float4>`.

Godot's shading language has no `StructuredBuffer`. Two routes:

1. **`MultiMeshInstance2D` + a data texture.** Pack the instance array into an
   `RGBAF` `ImageTexture` and `texelFetch` it in the vertex shader off
   `INSTANCE_ID`. This is the standard Godot workaround, keeps the
   one-draw-call-per-batch property, and is the pragmatic first target.
2. **`RenderingDevice` with GLSL.** Real SSBOs, a genuinely custom pipeline —
   the closest structural match to the URP `ScriptableRendererFeature`. More
   power, more plumbing to composite back into the 2D canvas.

Two things make this easier than it looks:

- **Clipping already left stencil behind.** The batched backend clips via a
  per-instance `clipRect` in slot `[13]` and clip-path SDF shapes in
  `[16..20]` — the comment in `UIQuadInstance.cs` records that this "replac[ed]
  the fragile FF-stencil path". `StencilClipManager` is no longer wired into
  `BatchedURPRenderBackend`. Godot 2D exposes no stencil for canvas items, so
  the codebase already sidestepped the feature it would have been blocked on.
- The shaders are self-contained SDF math (rounded-rect coverage, gradient
  brushes, blur). HLSL → Godot shading language is transliteration, not
  redesign. Budget it as real work — 3,904 lines — but not research.

Backdrop filters and `mix-blend-mode`, which need the page backdrop as a
texture, will be the fiddliest part; Godot's `BackBufferCopy` covers the
2D case.

### 3.2 Text — the one genuine risk

`SdfGlyphRasterizer` binds `FontEngine.TryRenderGlyphsToTexture` and
`TryAddGlyphToTexture` **by reflection into `UnityEngine.TextCoreTextEngineModule.dll`** —
undocumented Unity internals. `FontLoader.Unity.cs` similarly resolves faces
through `Font` + `FontEngine.LoadFontFace`, and there is a TextMeshPro adapter
alongside.

Godot has no equivalent that hands you a raw rasterized glyph bitmap for your
own atlas. The options, in order of preference:

1. **Godot `TextServer` + `FontFile`.** HarfBuzz + ICU + FreeType, and
   `FontFile` supports MSDF natively. Best fidelity and shaping; the work is
   adapting Weva's atlas ownership model to one where Godot owns the atlas —
   `IGlyphMetrics.TryGetGlyphRect` would need to source UVs from
   `TextServer`'s cache.
2. **A managed rasterizer** (FreeTypeSharp, SixLabors.Fonts) — Weva keeps
   owning its atlas exactly as it does now, and `SdfGlyphAtlasAdapter` /
   `GlyphAtlasPacker` port with minimal change. Costs a dependency and gives up
   Godot's shaping.

Note the existing seam quality: `FontLoader.cs` (neutral cache + warmup) is
already split from `FontLoader.Unity.cs` (face creation), and `GlyphAtlas.cs`
from `GlyphAtlas.Unity.cs`. The split is in the right place. Expect this to be
the largest single chunk of design work regardless.

### 3.3 Everything else is mechanical

- **Input**: `Godot.Input`/`InputEvent` behind the existing `IUIPointerSource`.
- **Clipboard / IME**: `DisplayServer.ClipboardGet/Set`,
  `DisplayServer.WindowSetImeActive/Position` — direct analogues of
  `GUIUtility.systemCopyBuffer` and `Input.imeCompositionMode`.
- **Images**: `Image`/`ImageTexture` with built-in PNG/JPG/WebP decode, behind
  `IImageSource`. Addressables has no Godot equivalent; `ResourceLoader` +
  `res://` paths replace it.
- **Host node**: `WevaDocument : MonoBehaviour` becomes
  `WevaDocument : Control` (or `Node2D`). It is 890 lines of lifecycle over the
  headless `UIDocumentState`; the property surface (`DocumentAsset`,
  `SortingOrder`, `ViewportOverride`, `Rebuild()`, `GetController<T>()`) maps
  onto `[Export]` almost one-for-one.

## 4. Platform caveats worth deciding up front

These are Godot-side constraints, not codebase problems — verify against the
Godot release you would target:

- **Godot's .NET builds do not support web export.** If HTML5 is on the roadmap,
  a Godot C# port does not get you there; you would be looking at GDExtension or
  a language port, which changes the entire calculus.
- Mobile support in Godot .NET is newer and less battle-tested than Unity's
  IL2CPP path. If Android/iOS matter, prototype export early.
- `TestVerifyAll` pins `LangVersion 9.0` to match Unity's compiler. Godot's .NET 8
  allows much newer C#; that constraint relaxes, but keep it if the Unity package
  is to stay buildable from the same sources.

## 5. Recommended shape

If this is worth doing, do it as a **third backend, not a fork**. The repo
already supports multiple `IRenderBackend`s (`NullBackend`, `RecordingBackend`,
`SoftwareRasterizer`, `SoftwarePainter`, `IMGUIDocumentRenderer`,
`URPRenderBackend`, `BatchedURPRenderBackend`). One shared core, two host
packages.

Suggested order:

1. **Compile the core under Godot.** Add a `Weva.Core` csproj mirroring
   `BaselineGen`'s include/exclude lists and reference it from a Godot .NET
   project. Low risk, and it immediately tells you whether anything has drifted
   Unity-ward since those excludes were written. *(Note: `BaselineGen.csproj`
   and `PerfBench.csproj` still exclude a stale `Runtime/UIDocument.cs`; the file
   is now `WevaDocument.cs`, which only `TestVerifyAll.csproj` excludes. Worth
   fixing regardless of any port.)*
2. **Run `TestVerifyAll` against it.** ~180k LOC of tests green before a single
   pixel is drawn.
3. **`GodotSoftwareBackend`.** Blit `SoftwareRasterizer` output into an
   `ImageTexture`. Ugly, slow, but end-to-end: HTML in, pixels on screen.
4. **`GodotTextBackend`.** The real decision from §3.2. Do it before the GPU
   backend — glyph atlas ownership determines the text path in the shader.
5. **`GodotBatchedBackend` + ported shaders.** MultiMesh2D + data texture first;
   `RenderingDevice` only if profiling demands it.
6. **Editor plugin.** Preview panel, DOM/style inspector, importers.

## 6. Bottom line

| | |
|---|---|
| Reusable as-is | ~108k LOC runtime + ~183k LOC tests |
| To rewrite | ~24k LOC C# + ~4k lines shaders + ~6k LOC editor tooling |
| Hardest problem | Glyph rasterization / atlas ownership |
| Second hardest | Instance-data upload without `StructuredBuffer` |
| Biggest external risk | No web export from Godot .NET |

The architecture was clearly designed with backend substitution in mind, and the
headless tooling means that design is continuously verified rather than
aspirational. A Godot port is a backend project, not a rewrite.
