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

// Interaction the renderer can be asked to put the document into, so the parts
// that only exist while a user is doing something -- a cursor, a selection, a
// hovered row, an open dropdown -- can be LOOKED at rather than only asserted
// about through the draw list.
struct Interaction {
    std::string focus;      // --focus=<selector>
    std::string open;       // --open=<selector>, a <select> to open
    std::string hover;      // --hover=<selector>, pointer over its middle
    int selection_from = -1;
    int selection_to = -1;  // --selection=A,B on the focused field
    double scroll = 0;      // --scroll=<px> on the focused element
    // The states this session added, none of which any gate had ever asked
    // either backend to DRAW. A modal dialog's backdrop, a popover in the top
    // layer and a tooltip are all geometry the Godot host had never been
    // compared on -- the same shape of gap that hid the hover bug.
    std::string press;      // --press=<selector>, pointer down on it (:active)
    std::string dialog;     // --dialog=<selector>, shown MODALLY (::backdrop)
    std::string popover;    // --popover=<selector>, opened
    std::string tooltip;    // --tooltip=<selector>, hovered until its title shows
    // --advance=<seconds>, run the clock before capturing.
    //
    // A page with an entrance animation renders at its START at t=0, and a
    // browser's screenshot is taken after it has settled -- so comparing the
    // two measures the clock rather than the renderer. match3-endgame's cards
    // begin at `opacity: 0`, and it read as the worst page in the corpus by
    // three times until this existed.
    double advance = 0;
};

int main(int argc, char** argv) {
    if (argc < 6) {
        std::fprintf(stderr,
                     "usage: weva_render <html> <css> <width> <height> <out.ppm> [flags]\n"
                     "  --focus=SEL --open=SEL --hover=SEL --selection=A,B --scroll=PX\n"
                     "  --press=SEL --dialog=SEL --popover=SEL --tooltip=SEL\n"
                     "  --advance=SECONDS (settle animations before capturing)\n");
        return 2;
    }
    Interaction act;
    for (int i = 6; i < argc; ++i) {
        const std::string a = argv[i];
        if (a.rfind("--focus=", 0) == 0) act.focus = a.substr(8);
        else if (a.rfind("--open=", 0) == 0) act.open = a.substr(7);
        else if (a.rfind("--hover=", 0) == 0) act.hover = a.substr(8);
        else if (a.rfind("--scroll=", 0) == 0) act.scroll = std::atof(a.c_str() + 9);
        else if (a.rfind("--press=", 0) == 0) act.press = a.substr(8);
        else if (a.rfind("--dialog=", 0) == 0) act.dialog = a.substr(9);
        else if (a.rfind("--popover=", 0) == 0) act.popover = a.substr(10);
        else if (a.rfind("--tooltip=", 0) == 0) act.tooltip = a.substr(10);
        else if (a.rfind("--advance=", 0) == 0) act.advance = std::atof(a.c_str() + 10);
        else if (a.rfind("--selection=", 0) == 0) {
            const std::string v = a.substr(12);
            const size_t comma = v.find(',');
            if (comma != std::string::npos) {
                act.selection_from = std::atoi(v.substr(0, comma).c_str());
                act.selection_to = std::atoi(v.substr(comma + 1).c_str());
            }
        }
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

    // Put it into the state that was asked for, then update again so the frame
    // holds it.
    if (!act.focus.empty()) {
        const weva_element_t e = weva_document_query(doc, act.focus.c_str());
        weva_document_set_focus(doc, e);
        if (act.selection_from >= 0) {
            weva_element_set_selection(doc, e, act.selection_from, act.selection_to);
        }
        if (act.scroll != 0) weva_element_set_scroll(doc, e, 0, act.scroll);
    }
    if (!act.hover.empty()) {
        double hx = 0, hy = 0, hw = 0, hh = 0;
        if (weva_element_bounds(doc, weva_document_query(doc, act.hover.c_str()), &hx, &hy, &hw,
                                &hh) == WEVA_OK) {
            weva_document_set_pointer(doc, hx + hw * 0.5, hy + hh * 0.5, 0);
        }
    }
    if (!act.open.empty()) {
        weva_document_open_select(doc, weva_document_query(doc, act.open.c_str()));
    }
    if (!act.dialog.empty()) {
        weva_element_show_dialog(doc, weva_document_query(doc, act.dialog.c_str()), 1);
    }
    if (!act.popover.empty()) {
        weva_element_show_popover(doc, weva_document_query(doc, act.popover.c_str()));
    }
    if (!act.press.empty()) {
        // Down and held, which is what `:active` means. Released would put the
        // document back where it started and draw nothing new.
        double px = 0, py = 0, pw = 0, ph = 0;
        if (weva_element_bounds(doc, weva_document_query(doc, act.press.c_str()), &px, &py, &pw,
                                &ph) == WEVA_OK) {
            weva_document_set_pointer(doc, px + pw * 0.5, py + ph * 0.5, 0);
            weva_document_set_pointer(doc, px + pw * 0.5, py + ph * 0.5, WEVA_BUTTON_PRIMARY);
        }
    }
    if (!act.tooltip.empty()) {
        // The pointer rests on it and the clock is advanced past the delay:
        // a tooltip is the one piece of this that time alone brings on.
        double tx = 0, ty = 0, tw = 0, th = 0;
        if (weva_element_bounds(doc, weva_document_query(doc, act.tooltip.c_str()), &tx, &ty, &tw,
                                &th) == WEVA_OK) {
            weva_document_set_pointer(doc, tx + tw * 0.5, ty + th * 0.5, 0);
            weva_document_update(doc, 0.0);
            weva_document_update(doc, 1.0);
        }
    }
    if (!act.focus.empty() || !act.open.empty() || !act.hover.empty() || !act.press.empty() ||
        !act.dialog.empty() || !act.popover.empty() || !act.tooltip.empty()) {
        weva_document_update(doc, 0.0);
    }
    if (act.advance > 0) {
        // In steps, not one jump: an animation is integrated per update, and a
        // single enormous delta is not the same journey.
        const double step = 1.0 / 60.0;
        for (double t = 0; t < act.advance; t += step) weva_document_update(doc, step);
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
        // WEVA_RENDER_TRACE=1 prints every draw with its scissor: the quickest
        // way to see what a host is handed when its clipping misbehaves.
        if (std::getenv("WEVA_RENDER_TRACE")) {
            std::fprintf(stderr, "draw %zu: kind %d, %zu verts, texture %llu, scissor %s %d,%d %dx%d\n",
                         i, static_cast<int>(d.kind), static_cast<size_t>(d.vertex_count),
                         static_cast<unsigned long long>(d.texture_id), d.has_scissor ? "on" : "off",
                         d.scissor_x, d.scissor_y, d.scissor_width, d.scissor_height);
        }
        if (d.has_scissor) {
            const weva::Recti r{d.scissor_x, d.scissor_y, d.scissor_width, d.scissor_height};
            renderer.set_scissor(&r);
        } else {
            renderer.set_scissor(nullptr);
        }
        if (d.kind == WEVA_DRAW_BACKDROP_FILTER) {
            // The vertices are the shape to filter inside, not geometry to paint.
            weva::BackdropEffect effect;
            effect.blur_radius = d.backdrop.blur_radius;
            for (int r = 0; r < 3; ++r) {
                for (int c = 0; c < 3; ++c) effect.color.m[r][c] = d.backdrop.color_matrix[r * 3 + c];
                effect.color.add[r] = d.backdrop.color_offset[r];
            }
            effect.color.alpha = d.backdrop.color_alpha;
            renderer.filter_backdrop(vertices, indices, effect);
            continue;
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
