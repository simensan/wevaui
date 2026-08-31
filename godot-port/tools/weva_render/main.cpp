// weva_render — rasterises a document through the C ABI and the built-in
// software backend, and writes the result as a binary PPM.
//
// The point is not the image on its own. This tool consumes exactly what the
// Godot host consumes — the same weva_draw list from the same weva_c.h entry
// points — so a pixel difference between its output and Godot's screenshot is
// a difference between the two BACKENDS, with the whole cascade, layout and
// tessellation pipeline held identical. That is what makes the comparison in
// hosts/godot/compare_render.py mean something.
//
//     weva_render <html-file> <css-file> <width> <height> <out.ppm>
//
// PPM rather than PNG so this stays dependency-free: no zlib, no image codec,
// and a comparison script can read it with the standard library alone.

#include "weva/software_renderer.h"
#include "weva_c.h"

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>

namespace {

bool read_file(const char* path, std::string* out) {
    std::ifstream in(path, std::ios::binary);
    if (!in) return false;
    std::ostringstream ss;
    ss << in.rdbuf();
    *out = ss.str();
    return true;
}

// PPM has no alpha, and the comparison wants what a viewer would see, so the
// document is composited over an opaque white page first. Godot's screenshot
// is of a window that has already been cleared, so this is the same operation
// on both sides rather than a convenience.
bool write_ppm(const char* path, const std::vector<uint8_t>& rgba, int w, int h) {
    std::ofstream out(path, std::ios::binary);
    if (!out) return false;
    out << "P6\n" << w << " " << h << "\n255\n";
    std::vector<uint8_t> rgb(static_cast<size_t>(w) * h * 3);
    for (size_t i = 0, n = static_cast<size_t>(w) * h; i < n; ++i) {
        const double a = rgba[i * 4 + 3] / 255.0;
        for (int c = 0; c < 3; ++c) {
            const double src = rgba[i * 4 + c];
            rgb[i * 3 + c] = static_cast<uint8_t>(src * a + 255.0 * (1.0 - a) + 0.5);
        }
    }
    out.write(reinterpret_cast<const char*>(rgb.data()), static_cast<std::streamsize>(rgb.size()));
    return static_cast<bool>(out);
}

} // namespace

int main(int argc, char** argv) {
    if (argc < 6) {
        std::fprintf(stderr, "usage: weva_render <html> <css> <width> <height> <out.ppm>\n");
        return 2;
    }
    std::string html, css;
    if (!read_file(argv[1], &html)) {
        std::fprintf(stderr, "weva_render: cannot read %s\n", argv[1]);
        return 2;
    }
    // An absent stylesheet is a document with no author CSS, not an error.
    if (std::strcmp(argv[2], "-") != 0 && !read_file(argv[2], &css)) {
        std::fprintf(stderr, "weva_render: cannot read %s\n", argv[2]);
        return 2;
    }
    const int width = std::atoi(argv[3]);
    const int height = std::atoi(argv[4]);
    if (width <= 0 || height <= 0) {
        std::fprintf(stderr, "weva_render: bad size %dx%d\n", width, height);
        return 2;
    }

    weva_config cfg{};
    cfg.viewport_width = width;
    cfg.viewport_height = height;
    cfg.use_user_agent_stylesheet = 1;
    weva_document_t doc = weva_document_create(&cfg);
    if (!doc) return 1;

    if (!css.empty() && weva_document_add_css(doc, css.data(), css.size()) != WEVA_OK) {
        std::fprintf(stderr, "weva_render: css rejected\n");
        weva_document_destroy(doc);
        return 1;
    }
    if (weva_document_load_html(doc, html.data(), html.size()) != WEVA_OK) {
        std::fprintf(stderr, "weva_render: html rejected\n");
        weva_document_destroy(doc);
        return 1;
    }
    if (weva_document_update(doc, 0.0) != WEVA_OK) {
        std::fprintf(stderr, "weva_render: update failed\n");
        weva_document_destroy(doc);
        return 1;
    }

    weva::SoftwareRenderer renderer(width, height);
    renderer.clear({0, 0, 0, 0});

    // Textures first: a draw may reference one, and the ABI's ids are the
    // document's, not the backend's, so the mapping has to be built up front.
    size_t texture_count = 0;
    const weva_texture* textures = weva_document_textures(doc, &texture_count);
    std::vector<std::pair<uint64_t, weva::TextureHandle>> texture_map;
    for (size_t i = 0; i < texture_count; ++i) {
        const weva_texture& t = textures[i];
        const size_t bytes = static_cast<size_t>(t.width) * t.height * 4;
        std::vector<uint8_t> rgba(t.rgba, t.rgba + bytes);
        texture_map.emplace_back(t.id, renderer.generate_texture(rgba, {t.width, t.height}));
    }

    size_t draw_count = 0;
    const weva_draw* draws = weva_document_draws(doc, &draw_count);
    for (size_t i = 0; i < draw_count; ++i) {
        const weva_draw& d = draws[i];
        if (d.vertex_count == 0 || d.index_count == 0) continue;

        std::vector<weva::Vertex> vertices(d.vertex_count);
        for (size_t k = 0; k < d.vertex_count; ++k) {
            const weva_vertex& v = d.vertices[k];
            vertices[k] = {{v.x, v.y}, {v.r, v.g, v.b, v.a}, {v.u, v.v}};
        }
        const std::vector<uint32_t> indices(d.indices, d.indices + d.index_count);

        weva::TextureHandle texture{};
        for (const auto& entry : texture_map) {
            if (entry.first == d.texture_id) {
                texture = entry.second;
                break;
            }
        }
        if (d.has_scissor) {
            const weva::Recti r{d.scissor_x, d.scissor_y, d.scissor_width, d.scissor_height};
            renderer.set_scissor(&r);
        } else {
            renderer.set_scissor(nullptr);
        }
        const weva::GeometryHandle geometry = renderer.compile_geometry(vertices, indices);
        renderer.render_geometry(geometry, {0, 0}, texture);
        renderer.release_geometry(geometry);
    }

    const bool ok = write_ppm(argv[5], renderer.to_srgb_rgba(), width, height);
    weva_document_destroy(doc);
    if (!ok) {
        std::fprintf(stderr, "weva_render: cannot write %s\n", argv[5]);
        return 1;
    }
    std::printf("weva_render: %zu draws, %zu textures -> %s\n", draw_count, texture_count, argv[5]);
    return 0;
}
