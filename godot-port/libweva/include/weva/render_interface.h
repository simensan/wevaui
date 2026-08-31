#pragma once
#include "weva/color.h"
#include "weva/geometry.h"

#include <cstdint>
#include <string_view>
#include <vector>

// The backend seam, at the altitude ARCHITECTURE.md §1 argues for: indexed
// triangles plus optional layer and filter operations, not semantic drawing
// commands.
//
// The C# `IRenderBackend` has twelve methods at a SEMANTIC altitude —
// `FillRect`, `StrokeBorder`, `DrawText`, `DrawShadow`, `PushFilter` — so every
// backend reimplements rounded-rect coverage, gradient evaluation, shadow blur
// and per-edge border styles for itself. The cost shows: the URP backend is
// 9,082 lines plus 3,904 of shader, and the software rasterizer is 1,592 lines
// and still draws glyphs as blocks and gradients as flat fills.
//
// Here the core does the tessellation and effect decomposition once, and a
// backend uploads triangles. The exit test for the decision is that the
// software backend lands in the low hundreds of lines and draws gradients and
// real glyphs.

namespace weva {

// Opaque backend-owned handles. Zero is the null handle in each case, so a
// backend that does not implement an optional feature can return {} and have
// the feature degrade rather than break.
struct GeometryHandle { uint64_t id = 0; explicit operator bool() const { return id != 0; } };
struct TextureHandle { uint64_t id = 0; explicit operator bool() const { return id != 0; } };
struct LayerHandle { uint64_t id = 0; explicit operator bool() const { return id != 0; } };
struct FilterHandle { uint64_t id = 0; explicit operator bool() const { return id != 0; } };

struct Vec2 { float x = 0, y = 0; };
struct Vec2i { int x = 0, y = 0; };
struct Recti { int x = 0, y = 0, width = 0, height = 0; };

// Position, colour and one texture coordinate — enough for solid fills,
// gradients (baked per vertex) and glyph atlases, which is every draw the core
// produces.
struct Vertex {
    Vec2 position;
    LinearColor color;
    Vec2 tex_coord;
};

enum class BlendMode : uint8_t { Normal, Multiply, Screen, Overlay, Darken, Lighten };
enum class FilterKind : uint8_t { Blur, DropShadow, Brightness, Contrast, Grayscale, Opacity };

struct FilterParams {
    double amount = 0;      // radius for blur, factor for the colour filters
    double offset_x = 0, offset_y = 0;
    LinearColor color;
};

class RenderInterface {
public:
    virtual ~RenderInterface() = default;

    // ---- Required ---------------------------------------------------------
    virtual GeometryHandle compile_geometry(const std::vector<Vertex>& vertices,
                                            const std::vector<uint32_t>& indices) = 0;
    // `translation` is applied at draw time so identical geometry can be reused
    // at many positions without recompiling it.
    virtual void render_geometry(GeometryHandle geometry, Vec2 translation,
                                 TextureHandle texture) = 0;
    virtual void release_geometry(GeometryHandle geometry) = 0;

    virtual TextureHandle load_texture(std::string_view path, Vec2i* out_size) = 0;
    virtual TextureHandle generate_texture(const std::vector<uint8_t>& rgba, Vec2i size) = 0;
    virtual void release_texture(TextureHandle texture) = 0;

    // Null disables clipping.
    virtual void set_scissor(const Recti* rect) = 0;

    // ---- Optional: a backend that ignores these still renders --------------
    virtual void set_transform(const Transform2D* transform) { (void)transform; }
    virtual LayerHandle push_layer() { return {}; }
    virtual void composite_layers(LayerHandle source, LayerHandle destination, BlendMode mode,
                                  const std::vector<FilterHandle>& filters) {
        (void)source; (void)destination; (void)mode; (void)filters;
    }
    virtual void pop_layer() {}
    virtual FilterHandle compile_filter(FilterKind kind, const FilterParams& params) {
        (void)kind; (void)params;
        return {};
    }
    virtual void release_filter(FilterHandle filter) { (void)filter; }
};

} // namespace weva
