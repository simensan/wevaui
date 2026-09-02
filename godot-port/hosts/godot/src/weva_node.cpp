#include "weva_node.h"

#include <godot_cpp/classes/font.hpp>
#include <godot_cpp/classes/font_file.hpp>
#include <godot_cpp/classes/image.hpp>
#include <godot_cpp/classes/rendering_server.hpp>
#include <godot_cpp/classes/os.hpp>
#include <godot_cpp/classes/theme_db.hpp>
#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/variant/packed_color_array.hpp>
#include <godot_cpp/variant/packed_int32_array.hpp>
#include <godot_cpp/variant/rect2.hpp>
#include <godot_cpp/variant/vector3.hpp>
#include <godot_cpp/variant/utility_functions.hpp>

#include <algorithm>
#include <cstring>
#include <vector>

using namespace godot;

namespace weva_godot {

WevaDocument::WevaDocument() {
    // The ABI is versioned so a host can refuse a library it was not built
    // against. Checking the major here rather than at first use means a
    // mismatch is a loud failure at construction, not a subtle one later.
    const uint32_t v = weva_abi_version();
    if ((v >> 16) != WEVA_ABI_VERSION_MAJOR) {
        UtilityFunctions::push_error("libweva ABI major mismatch: host expects ",
                                     WEVA_ABI_VERSION_MAJOR, ", library reports ", (v >> 16));
        return;
    }
    weva_config cfg{};
    cfg.viewport_width = 1920;
    cfg.viewport_height = 1080;
    cfg.use_user_agent_stylesheet = 1;
    doc_ = weva_document_create(&cfg);
}

void WevaDocument::set_use_engine_font(bool use) {
    if (use == use_engine_font_) return;
    use_engine_font_ = use;
    if (!use && doc_ && font_face_ != 0) {
        // A null table is what the ABI reads as "back to the built-in".
        weva_document_set_font_backend(doc_, nullptr, 0);
        font_face_ = 0;
    }
    dirty_ = true;
    queue_redraw();
}

void WevaDocument::ensure_font_backend() {
    // Deliberately lazy rather than done in the constructor: ThemeDB may not be
    // up that early, and use_engine_font has to be settable before the first
    // update, which is the last moment the ABI allows a backend to be
    // registered.
    if (!doc_ || font_face_ != 0 || !use_engine_font_) return;

    // The engine's own fallback font, adopted directly as a TextServer RID.
    // Loading a font file would work too, but this way the document renders in
    // the same face as every other control in the project by default, and the
    // host ships no font of its own.
    ThemeDB* theme = ThemeDB::get_singleton();
    if (!theme) return;
    const Ref<Font> fallback = theme->get_fallback_font();
    if (fallback.is_null()) return;
    TypedArray<RID> rids = fallback->get_rids();
    if (rids.is_empty()) return;

    // Behind it, whatever the system has for symbols and emoji: the theme
    // font covers Latin and little else, and a sample's ★ or 🛡 would draw
    // nothing. One SystemFont PER installed name — a single SystemFont with a
    // name list resolves to the first match only, so with "Segoe UI Symbol"
    // present the emoji face after it was never reached. Names the system
    // lacks are skipped rather than left to fall back to the default face,
    // which would shadow every name behind them.
    if (symbol_fonts_.empty()) {
        PackedStringArray installed;
        if (OS* os = OS::get_singleton()) installed = os->get_system_fonts();
        for (const char* n : {"Segoe UI Symbol", "Segoe UI Emoji", "Apple Color Emoji",
                              "Noto Color Emoji", "Noto Sans Symbols2", "Noto Sans Symbols",
                              "DejaVu Sans", "Symbola"}) {
            if (installed.size() > 0 && !installed.has(String(n))) continue;
            Ref<SystemFont> sf;
            sf.instantiate();
            PackedStringArray names;
            names.push_back(n);
            sf->set_font_names(names);
            symbol_fonts_.push_back(sf);
        }
    }
    for (const Ref<SystemFont>& sf : symbol_fonts_) {
        const TypedArray<RID> symbol_rids = sf->get_rids();
        for (int64_t i = 0; i < symbol_rids.size(); ++i) rids.push_back(symbol_rids[i]);
    }

    // The theme font's file data lets the backend build bold and italic
    // variants as fonts of their own (a variation would share its glyphs).
    PackedByteArray primary_data;
    const Ref<FontFile> file = fallback;
    if (file.is_valid()) primary_data = file->get_data();
    font_face_ = font_backend_.adopt(rids, primary_data);
    if (font_face_ == 0) return;
    font_backend_.fill(&font_table_);
    weva_document_set_font_backend(doc_, &font_table_, font_face_);
    dirty_ = true;
}

WevaDocument::~WevaDocument() {
    if (doc_) weva_document_destroy(doc_);
    release_layers();
    if (backdrop_shader_.is_valid()) RenderingServer::get_singleton()->free_rid(backdrop_shader_);
}

void WevaDocument::release_layers() {
    RenderingServer* rs = RenderingServer::get_singleton();
    for (const RID& r : layer_items_) {
        if (r.is_valid()) rs->free_rid(r);
    }
    layer_items_.clear();
    for (const RID& r : layer_materials_) {
        if (r.is_valid()) rs->free_rid(r);
    }
    layer_materials_.clear();
}

void WevaDocument::_bind_methods() {
    ClassDB::bind_method(D_METHOD("set_html", "html"), &WevaDocument::set_html);
    ClassDB::bind_method(D_METHOD("get_html"), &WevaDocument::get_html);
    ClassDB::bind_method(D_METHOD("set_css", "css"), &WevaDocument::set_css);
    ClassDB::bind_method(D_METHOD("get_css"), &WevaDocument::get_css);
    ClassDB::bind_method(D_METHOD("set_document_size", "size"), &WevaDocument::set_document_size);
    ClassDB::bind_method(D_METHOD("get_document_size"), &WevaDocument::get_document_size);
    ClassDB::bind_method(D_METHOD("update_document"), &WevaDocument::update_document);
    ClassDB::bind_method(D_METHOD("query_bounds", "selector"), &WevaDocument::query_bounds);
    ClassDB::bind_method(D_METHOD("query_text", "selector"), &WevaDocument::query_text);
    ClassDB::bind_method(D_METHOD("set_element_attribute", "selector", "name", "value"),
                         &WevaDocument::set_element_attribute);
    ClassDB::bind_method(D_METHOD("remove_element_attribute", "selector", "name"),
                         &WevaDocument::remove_element_attribute);
    ClassDB::bind_method(D_METHOD("set_use_engine_font", "use"),
                         &WevaDocument::set_use_engine_font);
    ClassDB::bind_method(D_METHOD("get_use_engine_font"), &WevaDocument::get_use_engine_font);
    ClassDB::bind_method(D_METHOD("has_engine_font"), &WevaDocument::has_engine_font);
    ClassDB::bind_method(D_METHOD("get_draw_count"), &WevaDocument::get_draw_count);
    ClassDB::bind_method(D_METHOD("get_triangle_count"), &WevaDocument::get_triangle_count);

    // Multiline so the editor gives a usable box for markup rather than a
    // single-line field.
    ADD_PROPERTY(PropertyInfo(Variant::STRING, "html", PROPERTY_HINT_MULTILINE_TEXT),
                 "set_html", "get_html");
    ADD_PROPERTY(PropertyInfo(Variant::STRING, "css", PROPERTY_HINT_MULTILINE_TEXT), "set_css",
                 "get_css");
    ADD_PROPERTY(PropertyInfo(Variant::VECTOR2, "document_size"), "set_document_size",
                 "get_document_size");
    ADD_PROPERTY(PropertyInfo(Variant::BOOL, "use_engine_font"), "set_use_engine_font",
                 "get_use_engine_font");
}

void WevaDocument::_ready() {

    // The glyph atlas is shelf-packed with no gutter, and the core emits UVs
    // that address texels exactly. Godot's canvas default is linear filtering,
    // which both softens glyph edges the core drew crisply and samples across
    // shelf boundaries into whatever glyph was packed next door.
    set_texture_filter(TEXTURE_FILTER_NEAREST);

    if (size_ == Vector2(0, 0)) {
        // No explicit size: take the viewport's, so a document dropped into a
        // scene fills it rather than laying out against a default nobody chose.
        const Vector2 vp = get_viewport_rect().size;
        if (vp.x > 0 && vp.y > 0) set_document_size(vp);
    }
    ensure_updated();
}

void WevaDocument::set_html(const String& html) {
    html_ = html;
    if (!doc_) return;
    const CharString utf8 = html.utf8();
    weva_document_load_html(doc_, utf8.get_data(), static_cast<size_t>(utf8.length()));
    dirty_ = true;
    queue_redraw();
}

void WevaDocument::set_css(const String& css) {
    css_ = css;
    if (!doc_) return;
    const CharString utf8 = css.utf8();
    weva_document_add_css(doc_, utf8.get_data(), static_cast<size_t>(utf8.length()));
    dirty_ = true;
    queue_redraw();
}

void WevaDocument::set_document_size(const Vector2& size) {
    size_ = size;
    if (!doc_) return;
    weva_document_set_viewport(doc_, static_cast<int>(size.x), static_cast<int>(size.y));
    dirty_ = true;
    queue_redraw();
}

void WevaDocument::ensure_updated() {
    if (!doc_ || !dirty_) return;
    ensure_font_backend();
    // Not an error to update an empty document: a scene may set css before
    // html, and the next update picks both up.
    weva_document_update(doc_, 0.0);
    dirty_ = false;

    // Mirror the document's textures by id: a texture already held is kept
    // (ids are never reused within a document), a new one is uploaded, one
    // the document dropped is dropped here. The ABI publishes whole textures,
    // and there are a handful per document rather than one per frame.
    size_t texture_count = 0;
    const weva_texture* textures = weva_document_textures(doc_, &texture_count);
    std::map<uint64_t, Ref<ImageTexture>> next;
    for (size_t i = 0; i < texture_count; ++i) {
        const weva_texture& t = textures[i];
        const auto held = textures_.find(t.id);
        if (held != textures_.end()) {
            next[t.id] = held->second;
            continue;
        }
        const int w = t.width, h = t.height;
        if (w <= 0 || h <= 0 || !t.rgba) continue;
        PackedByteArray bytes;
        bytes.resize(static_cast<int64_t>(w) * h * 4);
        std::memcpy(bytes.ptrw(), t.rgba, static_cast<size_t>(w) * h * 4);
        const Ref<Image> img = Image::create_from_data(w, h, false, Image::FORMAT_RGBA8, bytes);
        next[t.id] = ImageTexture::create_from_image(img);
    }
    textures_.swap(next);
}

void WevaDocument::update_document() {
    dirty_ = true;
    ensure_updated();
    queue_redraw();
}

void WevaDocument::add_triangles(const RID& item, const weva_draw& d) {
    // draw_polygon takes a polygon OUTLINE and triangulates it, so feeding it a
    // triangle soup produces garbage where it does not fail outright ("Invalid
    // polygon data, triangulation failed"). canvas_item_add_triangle_array
    // takes the index buffer directly, which is exactly the shape the core
    // already produces — no expansion, and no triangulator second-guessing
    // geometry that is already triangles.
    PackedVector2Array points;
    PackedColorArray colors;
    PackedVector2Array uvs;
    points.resize(static_cast<int64_t>(d.vertex_count));
    colors.resize(static_cast<int64_t>(d.vertex_count));
    uvs.resize(static_cast<int64_t>(d.vertex_count));
    Vector2* pw = points.ptrw();
    Color* cw = colors.ptrw();
    Vector2* uw = uvs.ptrw();
    for (size_t k = 0; k < d.vertex_count; ++k) {
        const weva_vertex& v = d.vertices[k];
        pw[k] = Vector2(v.x, v.y);
        // The core works in linear space; Godot's canvas expects sRGB, so the
        // conversion happens here rather than in the core, where it would be
        // wrong for a backend that wants linear.
        cw[k] = Color(v.r, v.g, v.b, v.a).linear_to_srgb();
        uw[k] = Vector2(v.u, v.v);
    }

    PackedInt32Array indices;
    indices.resize(static_cast<int64_t>(d.index_count));
    int32_t* iw = indices.ptrw();
    for (size_t k = 0; k < d.index_count; ++k) {
        iw[k] = static_cast<int32_t>(d.indices[k]);
    }

    RID texture;
    if (d.texture_id != 0) {
        const auto it = textures_.find(d.texture_id);
        if (it != textures_.end() && it->second.is_valid()) texture = it->second->get_rid();
    }
    RenderingServer::get_singleton()->canvas_item_add_triangle_array(
        item, indices, points, colors, uvs, PackedInt32Array(), PackedFloat32Array(), texture);
}

// The shader behind `backdrop-filter`. It reads the back buffer, which is what
// the core cannot do, blurs it and applies the colour matrix the core composed
// — so nothing here parses CSS, and the matrix arrives as three row vectors
// rather than a mat3 because Godot's mat3 uniform binding leaves the row/column
// convention ambiguous and a transposed saturate is not obviously wrong on
// screen.
//
// `blend_disabled` because a backdrop filter REPLACES what is behind the
// element rather than drawing over it; the element's own background is a
// separate draw that lands on top.
static const char* kBackdropShader = R"(shader_type canvas_item;
render_mode blend_disabled, unshaded;

uniform sampler2D screen_tex : hint_screen_texture, repeat_disable, filter_linear;
uniform float sigma = 0.0;
uniform vec3 mat_r = vec3(1.0, 0.0, 0.0);
uniform vec3 mat_g = vec3(0.0, 1.0, 0.0);
uniform vec3 mat_b = vec3(0.0, 0.0, 1.0);
uniform vec3 mat_add = vec3(0.0);
uniform float out_alpha = 1.0;

void fragment() {
    vec3 c;
    if (sigma <= 0.0) {
        c = texture(screen_tex, SCREEN_UV).rgb;
    } else {
        // A 7x7 Gaussian, spaced so the taps span about two sigma: enough of
        // the kernel to look like a blur rather than a smear, in one pass.
        // The reference rasteriser runs three box passes instead, so the two
        // agree on shape and not to the last few units.
        float d = sigma * 0.66;
        vec3 acc = vec3(0.0);
        float wsum = 0.0;
        for (int y = -3; y <= 3; y++) {
            for (int x = -3; x <= 3; x++) {
                vec2 o = vec2(float(x), float(y)) * d;
                float w = exp(-dot(o, o) / (2.0 * sigma * sigma));
                acc += texture(screen_tex, SCREEN_UV + o * SCREEN_PIXEL_SIZE).rgb * w;
                wsum += w;
            }
        }
        c = acc / wsum;
    }
    c = vec3(dot(mat_r, c), dot(mat_g, c), dot(mat_b, c)) + mat_add;
    COLOR = vec4(clamp(c, vec3(0.0), vec3(1.0)), out_alpha);
}
)";

RID WevaDocument::backdrop_material() {
    RenderingServer* rs = RenderingServer::get_singleton();
    if (!backdrop_shader_.is_valid()) {
        backdrop_shader_ = rs->shader_create();
        rs->shader_set_code(backdrop_shader_, String(kBackdropShader));
    }
    const RID m = rs->material_create();
    rs->material_set_shader(m, backdrop_shader_);
    layer_materials_.push_back(m);
    return m;
}

void WevaDocument::draw_layered(const weva_draw* draws, size_t count) {
    RenderingServer* rs = RenderingServer::get_singleton();
    release_layers();

    // One item per run of ordinary geometry, and one per backdrop filter, in z
    // order. The node's own item draws nothing: children render after their
    // parent's commands, so anything left on it would land under everything.
    const auto new_layer = [&](bool backdrop) {
        const RID item = rs->canvas_item_create();
        rs->canvas_item_set_parent(item, get_canvas_item());
        rs->canvas_item_set_z_index(item, static_cast<int32_t>(layer_items_.size()));
        rs->canvas_item_set_draw_index(item, static_cast<int32_t>(layer_items_.size()));
        (void)backdrop;
        layer_items_.push_back(item);
        return item;
    };

    RID current = new_layer(false);
    for (size_t i = 0; i < count; ++i) {
        const weva_draw& d = draws[i];
        if (d.vertex_count == 0 || d.index_count == 0) continue;

        if (d.kind != WEVA_DRAW_BACKDROP_FILTER) {
            add_triangles(current, d);
            continue;
        }

        // The region to copy: the shape, grown by the blur's reach, because a
        // pixel at the shape's edge is blurred against its neighbours OUTSIDE
        // it. Copying only the shape would blur it against nothing and rim the
        // panel with a dark halo.
        double lo_x = d.vertices[0].x, hi_x = lo_x;
        double lo_y = d.vertices[0].y, hi_y = lo_y;
        for (size_t k = 1; k < d.vertex_count; ++k) {
            lo_x = std::min<double>(lo_x, d.vertices[k].x);
            hi_x = std::max<double>(hi_x, d.vertices[k].x);
            lo_y = std::min<double>(lo_y, d.vertices[k].y);
            hi_y = std::max<double>(hi_y, d.vertices[k].y);
        }
        const double sigma = d.backdrop.blur_radius / 2.0;
        const double pad = sigma > 0 ? sigma * 3.0 + 1.0 : 0.0;
        const Rect2 region(static_cast<real_t>(lo_x - pad), static_cast<real_t>(lo_y - pad),
                           static_cast<real_t>(hi_x - lo_x + 2 * pad),
                           static_cast<real_t>(hi_y - lo_y + 2 * pad));

        const RID item = new_layer(true);
        rs->canvas_item_set_copy_to_backbuffer(item, true, region);
        const RID mat = backdrop_material();
        rs->material_set_param(mat, "sigma", sigma);
        rs->material_set_param(mat, "mat_r",
                               Vector3(d.backdrop.color_matrix[0], d.backdrop.color_matrix[1],
                                       d.backdrop.color_matrix[2]));
        rs->material_set_param(mat, "mat_g",
                               Vector3(d.backdrop.color_matrix[3], d.backdrop.color_matrix[4],
                                       d.backdrop.color_matrix[5]));
        rs->material_set_param(mat, "mat_b",
                               Vector3(d.backdrop.color_matrix[6], d.backdrop.color_matrix[7],
                                       d.backdrop.color_matrix[8]));
        rs->material_set_param(mat, "mat_add",
                               Vector3(d.backdrop.color_offset[0], d.backdrop.color_offset[1],
                                       d.backdrop.color_offset[2]));
        rs->material_set_param(mat, "out_alpha", d.backdrop.color_alpha);
        rs->canvas_item_set_material(item, mat);
        add_triangles(item, d);

        current = new_layer(false);
    }
}

void WevaDocument::_draw() {
    ensure_updated();
    if (!doc_) return;

    size_t count = 0;
    const weva_draw* draws = weva_document_draws(doc_, &count);

    bool any_backdrop = false;
    for (size_t i = 0; i < count && !any_backdrop; ++i) {
        any_backdrop = draws[i].kind == WEVA_DRAW_BACKDROP_FILTER;
    }
    if (any_backdrop) {
        draw_layered(draws, count);
        return;
    }
    release_layers();

    // The core clips scissored geometry before publishing it, so every draw
    // goes on this one canvas item in order. (Per-item clipping via
    // canvas_item_set_clip was tried: the compatibility renderer dropped every
    // draw after a clipped sibling item.)
    for (size_t i = 0; i < count; ++i) {
        const weva_draw& d = draws[i];
        if (d.vertex_count == 0 || d.index_count == 0) continue;
        add_triangles(get_canvas_item(), d);
    }
}

Rect2 WevaDocument::query_bounds(const String& selector) {
    if (!doc_) return Rect2();
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return Rect2();
    double x = 0, y = 0, w = 0, h = 0;
    if (weva_element_bounds(doc_, e, &x, &y, &w, &h) != WEVA_OK) return Rect2();
    return Rect2(static_cast<real_t>(x), static_cast<real_t>(y), static_cast<real_t>(w),
                 static_cast<real_t>(h));
}

String WevaDocument::query_text(const String& selector) {
    if (!doc_) return String();
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return String();
    // The two-call pattern the ABI is built for: size, then fill.
    const size_t needed = weva_element_text(doc_, e, nullptr, 0);
    std::vector<char> buffer(needed + 1);
    weva_element_text(doc_, e, buffer.data(), buffer.size());
    return String::utf8(buffer.data());
}

bool WevaDocument::set_element_attribute(const String& selector, const String& name,
                                         const String& value) {
    if (!doc_) return false;
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return false;
    const CharString n = name.utf8();
    const CharString v = value.utf8();
    if (weva_element_set_attribute(doc_, e, n.get_data(), v.get_data()) != WEVA_OK) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

bool WevaDocument::remove_element_attribute(const String& selector, const String& name) {
    if (!doc_) return false;
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return false;
    const CharString n = name.utf8();
    // A null value is what the ABI reads as "remove"; GDScript has no way to
    // express one, which is why this is its own method.
    if (weva_element_set_attribute(doc_, e, n.get_data(), nullptr) != WEVA_OK) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

int WevaDocument::get_draw_count() const {
    if (!doc_) return 0;
    size_t count = 0;
    weva_document_draws(doc_, &count);
    return static_cast<int>(count);
}

int WevaDocument::get_triangle_count() const {
    if (!doc_) return 0;
    size_t count = 0;
    const weva_draw* draws = weva_document_draws(doc_, &count);
    size_t total = 0;
    for (size_t i = 0; i < count; ++i) total += draws[i].index_count / 3;
    return static_cast<int>(total);
}

} // namespace weva_godot
