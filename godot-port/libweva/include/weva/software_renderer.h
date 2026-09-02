#pragma once
#include "weva/render_interface.h"

#include <map>
#include <optional>
#include <vector>

// A reference backend over the triangle interface.
//
// It exists to answer ARCHITECTURE.md §1's exit test: with tessellation moved
// into the core, a backend should be a few hundred lines rather than the C#
// rasterizer's 1,592 — and should draw what it is given rather than
// approximating it.

namespace weva {

class SoftwareRenderer : public RenderInterface {
public:
    SoftwareRenderer(int width, int height);

    void clear(const LinearColor& color);
    int width() const { return width_; }
    int height() const { return height_; }
    // Out-of-range reads return transparent rather than faulting, so a test can
    // probe around an edge without bounds-checking every call.
    LinearColor pixel(int x, int y) const;
    // 8-bit sRGB RGBA, for writing an image out.
    std::vector<uint8_t> to_srgb_rgba() const;

    GeometryHandle compile_geometry(const std::vector<Vertex>& vertices,
                                    const std::vector<uint32_t>& indices) override;
    void render_geometry(GeometryHandle geometry, Vec2 translation,
                         TextureHandle texture) override;
    void release_geometry(GeometryHandle geometry) override;

    TextureHandle load_texture(std::string_view path, Vec2i* out_size) override;
    TextureHandle generate_texture(const std::vector<uint8_t>& rgba, Vec2i size) override;
    void release_texture(TextureHandle texture) override;

    void set_scissor(const Recti* rect) override;
    void set_transform(const Transform2D* transform) override;

private:
    struct Geometry {
        std::vector<Vertex> vertices;
        std::vector<uint32_t> indices;
    };
    struct Texture {
        std::vector<uint8_t> rgba;
        Vec2i size;
    };

    void raster_triangle(const Vertex& a, const Vertex& b, const Vertex& c, const Texture* tex);
    void blend(int x, int y, const LinearColor& src);

    int width_, height_;
    // sRGB-ENCODED components with straight alpha, despite the element type.
    //
    // Compositing happens in whatever space the framebuffer is in, and the
    // browsers composite in gamma sRGB — as does the Godot host, which feeds
    // Godot's canvas `linear_to_srgb()` colours (hosts/godot/src/weva_node.cpp).
    // Blending here in linear made this backend disagree with BOTH: on the neon
    // sample a pink blob over the dark page read #700f41 where Chrome and Godot
    // agreed on #2d0925, and the whole difference was the one pow().
    //
    // clear() encodes on the way in and pixel() decodes on the way out, so the
    // public interface still speaks LinearColor.
    std::vector<LinearColor> pixels_;
    std::map<uint64_t, Geometry> geometry_;
    std::map<uint64_t, Texture> textures_;
    uint64_t next_id_ = 1;
    std::optional<Recti> scissor_;
    Transform2D transform_;
};

} // namespace weva
