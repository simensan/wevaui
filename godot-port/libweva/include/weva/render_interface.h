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

// The colour functions of a `filter` list composed into one affine transform in
// sRGB — brightness, contrast, grayscale, sepia, saturate, invert and opacity
// all reduce to this (Filter Effects L1 §8), and the core composes them so a
// backend never parses a filter list.
struct ColorMatrix {
    float m[3][3] = {{1, 0, 0}, {0, 1, 0}, {0, 0, 1}};
    float add[3] = {0, 0, 0};
    float alpha = 1;

    bool is_identity() const {
        for (int r = 0; r < 3; ++r) {
            for (int c = 0; c < 3; ++c) {
                if (m[r][c] != (r == c ? 1.0f : 0.0f)) return false;
            }
            if (add[r] != 0) return false;
        }
        return alpha == 1;
    }
};

// What `backdrop-filter` asks of a backend.
struct BackdropEffect {
    // A CSS blur RADIUS, not a sigma; the sigma is half of it, as for a shadow.
    double blur_radius = 0;
    ColorMatrix color;
};

// A rounded rectangle, described rather than tessellated.
//
// The core normally hands a backend triangles, which is what keeps every host
// small and keeps two backends comparable. But a rounded rect is the single
// commonest shape in a document, and a backend that can evaluate it per pixel
// does strictly better than triangles can: exact coverage instead of the
// half-pixel ramp, correct under any transform, and two triangles instead of a
// fan plus a ring.
//
// So it is offered BOTH ways. render_rounded_rect() carries the description
// and the tessellation; a backend that cannot use the first falls through to
// the second and nothing changes for it.
struct RoundedRect {
    double x = 0, y = 0, width = 0, height = 0;
    // Per corner, clockwise from top-left, x then y radius.
    double radii[4][2] = {{0, 0}, {0, 0}, {0, 0}, {0, 0}};
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

    // Draws a mesh once, without compiling it first.
    //
    // Every caller in the engine compiles a mesh, draws it once at the origin,
    // and releases it -- so the handle round trip buys nothing and costs two
    // full copies of the geometry and a map node per draw. A blurred box
    // shadow alone is up to 48 of them. Taking the vectors BY VALUE lets a
    // caller hand over a temporary it is finished with, so a backend that
    // stores the geometry can move rather than copy it.
    //
    // The default is the round trip, so a backend that has not heard of this
    // -- a host's, across the C ABI -- behaves exactly as it did.
    virtual void render_mesh(std::vector<Vertex> vertices, std::vector<uint32_t> indices,
                             Vec2 translation, TextureHandle texture) {
        const GeometryHandle g = compile_geometry(vertices, indices);
        render_geometry(g, translation, texture);
        release_geometry(g);
    }

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

    // Filters what has ALREADY been painted, within a shape.
    //
    // The one operation here that cannot be reduced to triangles. Everything
    // else the core decomposes — a blur becomes a padded rasterize, a shadow
    // becomes layers, a gradient becomes vertex colours — because it knows the
    // shape being drawn. `backdrop-filter` reads the DESTINATION, which only
    // the backend has, so the core can only say what to do and where.
    //
    // The where is a mesh in absolute coordinates, transformed and clipped by
    // the core exactly like the mesh of any other draw, so a backend confines
    // the effect to it the same way it fills it — rounded corners included,
    // since they are in the tessellation. It is a REGION, not something to
    // paint, and its vertices are transparent so that a backend which routes
    // it to its rasterizer by mistake draws nothing.
    //
    // A backend that does not implement this leaves the backdrop alone: the
    // element then renders without its material rather than not at all, which
    // is what every backend did before this existed.
    virtual void filter_backdrop(const std::vector<Vertex>& vertices,
                                 const std::vector<uint32_t>& indices,
                                 const BackdropEffect& effect) {
        (void)vertices; (void)indices; (void)effect;
    }

    // Draws a rounded rectangle. The default uploads the tessellation, so a
    // backend gets the shape whether or not it overrides this; one that can
    // evaluate the rounded box per pixel overrides it and gets exact coverage.
    virtual void render_rounded_rect(const RoundedRect& shape,
                                     const std::vector<Vertex>& vertices,
                                     const std::vector<uint32_t>& indices) {
        (void)shape;
        if (vertices.empty() || indices.empty()) return;
        const GeometryHandle g = compile_geometry(vertices, indices);
        render_geometry(g, {0, 0}, {});
        release_geometry(g);
    }
};

} // namespace weva
