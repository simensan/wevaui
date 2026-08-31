#include "weva/software_renderer.h"

#include <algorithm>
#include <cmath>

namespace weva {

namespace {

// Twice the signed area of the triangle abp. Positive on one winding, negative
// on the other; the sign is what tells the rasterizer which way the triangle
// is wound.
double edge(const Vec2& a, const Vec2& b, double px, double py) {
    return (static_cast<double>(b.x) - a.x) * (py - a.y) -
           (static_cast<double>(b.y) - a.y) * (px - a.x);
}

// The fill rule: a pixel exactly on a shared edge must be claimed by exactly
// one of the two triangles, or the seam is drawn twice. Drawing it twice is
// visible whenever the colour is translucent — and the core's meshes share
// edges everywhere, since a fan and a ring are both built from them.
bool is_top_left(const Vec2& a, const Vec2& b) {
    if (a.y == b.y) return b.x < a.x;   // a top edge, in a y-down space
    return b.y < a.y;                   // a left edge
}

float srgb_from_linear(float c) {
    if (c <= 0.0f) return 0.0f;
    if (c >= 1.0f) return 1.0f;
    return c <= 0.0031308f ? c * 12.92f
                           : 1.055f * std::pow(c, 1.0f / 2.4f) - 0.055f;
}

} // namespace

SoftwareRenderer::SoftwareRenderer(int width, int height)
    : width_(std::max(0, width)), height_(std::max(0, height)),
      pixels_(static_cast<size_t>(std::max(0, width) * std::max(0, height))) {}

void SoftwareRenderer::clear(const LinearColor& color) {
    std::fill(pixels_.begin(), pixels_.end(), color);
}

LinearColor SoftwareRenderer::pixel(int x, int y) const {
    if (x < 0 || y < 0 || x >= width_ || y >= height_) return LinearColor::transparent();
    return pixels_[static_cast<size_t>(y) * width_ + x];
}

std::vector<uint8_t> SoftwareRenderer::to_srgb_rgba() const {
    std::vector<uint8_t> out(pixels_.size() * 4);
    for (size_t i = 0; i < pixels_.size(); ++i) {
        const LinearColor& c = pixels_[i];
        const auto b = [](float v) {
            return static_cast<uint8_t>(std::lround(std::clamp(v, 0.0f, 1.0f) * 255.0f));
        };
        out[i * 4 + 0] = b(srgb_from_linear(c.r));
        out[i * 4 + 1] = b(srgb_from_linear(c.g));
        out[i * 4 + 2] = b(srgb_from_linear(c.b));
        out[i * 4 + 3] = b(c.a);
    }
    return out;
}

GeometryHandle SoftwareRenderer::compile_geometry(const std::vector<Vertex>& vertices,
                                                  const std::vector<uint32_t>& indices) {
    const GeometryHandle h{next_id_++};
    geometry_[h.id] = Geometry{vertices, indices};
    return h;
}

void SoftwareRenderer::release_geometry(GeometryHandle g) { geometry_.erase(g.id); }

TextureHandle SoftwareRenderer::load_texture(std::string_view, Vec2i* out_size) {
    // No image decoding here: a null handle means the draw falls back to its
    // vertex colours, so an unsupported image degrades to a flat fill rather
    // than to nothing.
    if (out_size) *out_size = {0, 0};
    return {};
}

TextureHandle SoftwareRenderer::generate_texture(const std::vector<uint8_t>& rgba, Vec2i size) {
    if (size.x <= 0 || size.y <= 0) return {};
    if (rgba.size() < static_cast<size_t>(size.x) * size.y * 4) return {};
    const TextureHandle h{next_id_++};
    textures_[h.id] = Texture{rgba, size};
    return h;
}

void SoftwareRenderer::release_texture(TextureHandle t) { textures_.erase(t.id); }

void SoftwareRenderer::set_scissor(const Recti* rect) {
    if (rect) scissor_ = *rect;
    else scissor_.reset();
}

void SoftwareRenderer::set_transform(const Transform2D* transform) {
    transform_ = transform ? *transform : Transform2D::identity();
}

void SoftwareRenderer::blend(int x, int y, const LinearColor& src) {
    if (x < 0 || y < 0 || x >= width_ || y >= height_) return;
    if (scissor_) {
        if (x < scissor_->x || y < scissor_->y || x >= scissor_->x + scissor_->width ||
            y >= scissor_->y + scissor_->height) {
            return;
        }
    }
    LinearColor& dst = pixels_[static_cast<size_t>(y) * width_ + x];
    // Source-over with straight alpha.
    const float inv = 1.0f - src.a;
    dst.r = src.r * src.a + dst.r * inv;
    dst.g = src.g * src.a + dst.g * inv;
    dst.b = src.b * src.a + dst.b * inv;
    dst.a = src.a + dst.a * inv;
}

void SoftwareRenderer::raster_triangle(const Vertex& v0, const Vertex& v1, const Vertex& v2,
                                       const Texture* tex) {
    const Vec2 a = v0.position, b = v1.position, c = v2.position;
    const double area = edge(a, b, c.x, c.y);
    if (area == 0) return;   // degenerate

    // Normalise to one winding so the fill rule below has a fixed sign to test
    // against. The core emits both, and flipping the vertex order is cheaper
    // than carrying the sign through every comparison.
    const Vertex& p0 = v0;
    const Vertex& p1 = area < 0 ? v2 : v1;
    const Vertex& p2 = area < 0 ? v1 : v2;
    const Vec2 q0 = p0.position, q1 = p1.position, q2 = p2.position;
    const double dbl_area = edge(q0, q1, q2.x, q2.y);

    int min_x = static_cast<int>(std::floor(std::min({q0.x, q1.x, q2.x})));
    int max_x = static_cast<int>(std::ceil(std::max({q0.x, q1.x, q2.x})));
    int min_y = static_cast<int>(std::floor(std::min({q0.y, q1.y, q2.y})));
    int max_y = static_cast<int>(std::ceil(std::max({q0.y, q1.y, q2.y})));
    min_x = std::max(min_x, 0);
    min_y = std::max(min_y, 0);
    max_x = std::min(max_x, width_ - 1);
    max_y = std::min(max_y, height_ - 1);

    const bool tl0 = is_top_left(q1, q2);
    const bool tl1 = is_top_left(q2, q0);
    const bool tl2 = is_top_left(q0, q1);

    for (int y = min_y; y <= max_y; ++y) {
        for (int x = min_x; x <= max_x; ++x) {
            // Sampled at the pixel CENTRE, which is what makes a rect from
            // (0,0) to (10,10) cover exactly ten pixels per side.
            const double px = x + 0.5, py = y + 0.5;
            const double w0 = edge(q1, q2, px, py);
            const double w1 = edge(q2, q0, px, py);
            const double w2 = edge(q0, q1, px, py);
            const auto inside = [](double w, bool top_left) {
                return w > 0 || (w == 0 && top_left);
            };
            if (!inside(w0, tl0) || !inside(w1, tl1) || !inside(w2, tl2)) continue;

            const double l0 = w0 / dbl_area, l1 = w1 / dbl_area, l2 = w2 / dbl_area;
            LinearColor col;
            col.r = static_cast<float>(l0 * p0.color.r + l1 * p1.color.r + l2 * p2.color.r);
            col.g = static_cast<float>(l0 * p0.color.g + l1 * p1.color.g + l2 * p2.color.g);
            col.b = static_cast<float>(l0 * p0.color.b + l1 * p1.color.b + l2 * p2.color.b);
            col.a = static_cast<float>(l0 * p0.color.a + l1 * p1.color.a + l2 * p2.color.a);

            if (tex) {
                const double u = l0 * p0.tex_coord.x + l1 * p1.tex_coord.x + l2 * p2.tex_coord.x;
                const double v = l0 * p0.tex_coord.y + l1 * p1.tex_coord.y + l2 * p2.tex_coord.y;
                const int tx = std::clamp(static_cast<int>(u * tex->size.x), 0, tex->size.x - 1);
                const int ty = std::clamp(static_cast<int>(v * tex->size.y), 0, tex->size.y - 1);
                const size_t o = (static_cast<size_t>(ty) * tex->size.x + tx) * 4;
                // The texture modulates the vertex colour, which is what lets
                // one path serve both a glyph mask and a tinted image.
                col.r *= srgb_byte_to_linear(tex->rgba[o + 0]);
                col.g *= srgb_byte_to_linear(tex->rgba[o + 1]);
                col.b *= srgb_byte_to_linear(tex->rgba[o + 2]);
                col.a *= tex->rgba[o + 3] / 255.0f;
            }
            blend(x, y, col);
        }
    }
}

void SoftwareRenderer::render_geometry(GeometryHandle g, Vec2 translation, TextureHandle t) {
    auto git = geometry_.find(g.id);
    if (git == geometry_.end()) return;
    const Geometry& geo = git->second;
    auto tit = textures_.find(t.id);
    const Texture* tex = tit == textures_.end() ? nullptr : &tit->second;

    for (size_t i = 0; i + 2 < geo.indices.size(); i += 3) {
        Vertex v[3];
        bool ok = true;
        for (int k = 0; k < 3; ++k) {
            const uint32_t idx = geo.indices[i + k];
            if (idx >= geo.vertices.size()) { ok = false; break; }
            v[k] = geo.vertices[idx];
            double tx = 0, ty = 0;
            transform_.apply(v[k].position.x + translation.x, v[k].position.y + translation.y,
                             &tx, &ty);
            v[k].position = {static_cast<float>(tx), static_cast<float>(ty)};
        }
        // An out-of-range index skips its triangle rather than reading past the
        // buffer: a backend must not fault on malformed geometry.
        if (ok) raster_triangle(v[0], v[1], v[2], tex);
    }
}

} // namespace weva
