# Architecture

Three layers, with the seam deliberately placed so a second host costs a shim
rather than a second engine.

```
┌──────────────────────────────────────────────────────────┐
│ libweva  (C++17, host-agnostic, -fno-exceptions)         │
│   parse → cascade → layout → paint display list          │
│   version-keyed invalidation; per-pass arenas            │
└───────────────┬──────────────────────────────────────────┘
                │ include/weva_c.h   (C linkage, POD, versioned)
        ┌───────┴────────┐
        │                │
┌───────▼──────┐  ┌──────▼──────────────┐
│ hosts/godot  │  │ hosts/unity         │
│ GDExtension  │  │ P/Invoke shim       │
│ (first)      │  │ (later)             │
└──────────────┘  └─────────────────────┘
```

`libweva` must not reference Godot or Unity types anywhere. That is enforceable
— the C# core proved the discipline works (47 of 682 files touched UnityEngine,
and the whole surface fitted in a 104-line stub) — but here it is structural:
the core links against neither engine.

## What carries over unchanged

These are the parts of the C# design worth preserving verbatim:

* **Version-keyed invalidation.** Cache keys are input versions, not heuristic
  dirty bits; clean subtrees skip every stage. This is why a `:hover` flip costs
  0.08 ms against 8.3 ms for a full cascade. Port the contract as written in
  `AGENTS.md` §"Layout / paint cache invariants" — it is the performance
  architecture.
* **The paint display list as a decoupling layer.** Layout emits commands; a
  backend consumes them. Keep this. Only its *altitude* changes (below).
* **The engine-neutral core boundary** itself — the set of files
  `Tools/BaselineGen` already compiles is the port's scope definition.

## What gets re-cut on the way over

### 1. The render backend drops to geometry + effects

Weva's `IRenderBackend` has **12 required methods at a semantic altitude** —
`FillRect`, `StrokeBorder`, `DrawText`, `DrawShadow`, `PushFilter`… so every
backend reimplements rounded-rect SDF coverage, gradient evaluation, shadow blur
and per-edge border styles. The cost is visible: the URP backend is 9,082 LOC
plus 3,904 shader lines, and `SoftwareRasterizer` is 1,592 LOC and still renders
glyphs as blocks and gradients as flat fills.

RmlUi decomposes everything to **indexed triangles** plus optional layer/filter/
shader ops, requires 8 methods, and ships 6 renderers × 6 platforms off it. That
is the altitude to target.

```cpp
struct RenderInterface {
    // Required
    virtual GeometryHandle compile_geometry(std::span<const Vertex>,
                                            std::span<const uint32_t>) = 0;
    virtual void render_geometry(GeometryHandle, Vec2 translation, TextureHandle) = 0;
    virtual void release_geometry(GeometryHandle) = 0;
    virtual TextureHandle load_texture(std::string_view path, Vec2i* out_size) = 0;
    virtual TextureHandle generate_texture(std::span<const uint8_t>, Vec2i size) = 0;
    virtual void release_texture(TextureHandle) = 0;
    virtual void set_scissor(const Recti*) = 0;   // nullptr = disable

    // Optional — default no-op, feature degrades rather than breaks
    virtual void set_transform(const Mat2x3*) {}
    virtual LayerHandle push_layer() { return {}; }
    virtual void composite_layers(LayerHandle src, LayerHandle dst, BlendMode,
                                  std::span<const FilterHandle>) {}
    virtual void pop_layer() {}
    virtual FilterHandle compile_filter(FilterKind, const FilterParams&) { return {}; }
    virtual void release_filter(FilterHandle) {}
};
```

The core takes on the tessellation and effect decomposition it currently pushes
onto backends: rounded-rect and border geometry, gradient meshing, shadow
expansion, clip-path shapes. That work moves once, into `libweva/src/paint/`,
instead of being written again per backend.

**Keep the batched über-shader as one backend, not as the interface shape.** The
57-float4 instance record and per-instance clip rects are a genuinely strong
design for a single fast target — they just shouldn't dictate what every backend
must implement. A batching backend can coalesce compiled geometry itself.

Exit test for this decision: the software backend should be a few hundred lines,
not 1,592, and should render gradients and real glyphs.

### 2. Fonts become a real pluggable interface

The C# rasterizer binds `FontEngine.TryRenderGlyphsToTexture` **by reflection
into undocumented internals** of `UnityEngine.TextCoreTextEngineModule.dll`,
with a TextMeshPro fallback ladder. The interfaces above it (`IFontMetrics`,
`IGlyphMetrics`, `FontLoader.IFaceLoader`) are the right shape; the
implementation underneath is not portable and never was.

```cpp
struct FontInterface {
    virtual FaceHandle load_face(std::span<const uint8_t> ttf, int index) = 0;
    virtual bool face_metrics(FaceHandle, double px, FaceMetrics* out) = 0;
    virtual bool glyph_index(FaceHandle, uint32_t codepoint, uint32_t* out) = 0;
    virtual bool glyph_metrics(FaceHandle, uint32_t glyph, double px, GlyphMetrics* out) = 0;
    virtual bool rasterize(FaceHandle, uint32_t glyph, double px, RenderMode,
                           Bitmap* out) = 0;
    virtual void shape(FaceHandle, std::string_view utf8, double px,
                       std::vector<ShapedGlyph>* out) = 0;
};
```

Two implementations to choose between (decide in Phase 5, not now):

* **FreeType + HarfBuzz directly** — `libweva` keeps owning its atlas, so
  `GlyphAtlasPacker` / `SdfGlyphAtlasAdapter` port with little change. Adds
  dependencies; gives full control and works identically on every host.
* **Godot `TextServer`** — HarfBuzz + ICU + FreeType already in the engine, MSDF
  support built in, no new dependencies. But Godot owns the atlas, so
  `glyph_rect` lookups source from its cache and the Unity host would need a
  different implementation — which weakens the single-core argument.

The interface above is deliberately compatible with either.

### 3. Data binding is not ported

All reflection in the C# core lives in `Runtime/Binding/` (5 files) and exists
to read C# object graphs. It does not translate, and it should not be
translated. In a GDScript world the same feature is Godot's own introspection —
`Object::get`/`set` over `Variant`, `Object::connect` for events — which is a
better fit and deletes the problem. `hosts/godot/` owns this; `libweva` has no
binding layer at all.

## The C ABI

Narrow by construction. Sketch, not final:

```c
uint32_t          weva_abi_version(void);
weva_document_t   weva_document_create(const weva_config*);
void              weva_document_destroy(weva_document_t);
weva_status       weva_document_load_html(weva_document_t, const char*, size_t);
weva_status       weva_document_add_css(weva_document_t, const char*, size_t);
void              weva_document_set_viewport(weva_document_t, int w, int h);
weva_status       weva_document_update(weva_document_t, double dt_seconds);
const weva_paint* weva_document_paint(weva_document_t, size_t* out_count);
weva_element_t    weva_document_query(weva_document_t, const char* selector);
```

Hosts register their `RenderInterface` and `FontInterface` through function-
pointer tables mirroring the C++ vtables. Everything crossing the boundary is
POD; nothing crossing it allocates without a paired release.

Do not design the Unity side of this yet — but do not design it *out*. The rule
for Phase 1–6 is simply that no core API takes or returns a Godot type.
