#include "weva_node.h"

#include <godot_cpp/classes/image.hpp>
#include <godot_cpp/classes/rendering_server.hpp>
#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/variant/packed_color_array.hpp>
#include <godot_cpp/variant/packed_int32_array.hpp>
#include <godot_cpp/variant/utility_functions.hpp>

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

WevaDocument::~WevaDocument() {
    if (doc_) weva_document_destroy(doc_);
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
    // Not an error to update an empty document: a scene may set css before
    // html, and the next update picks both up.
    weva_document_update(doc_, 0.0);
    dirty_ = false;

    // Republish the glyph atlas if the document has a new one. Rebuilt rather
    // than updated because the ABI publishes whole textures, and there are a
    // handful of them per document rather than one per frame.
    size_t texture_count = 0;
    const weva_texture* textures = weva_document_textures(doc_, &texture_count);
    if (texture_count > 0 && textures[0].id != atlas_id_) {
        const int w = textures[0].width, h = textures[0].height;
        PackedByteArray bytes;
        bytes.resize(static_cast<int64_t>(w) * h * 4);
        std::memcpy(bytes.ptrw(), textures[0].rgba, static_cast<size_t>(w) * h * 4);
        const Ref<Image> img = Image::create_from_data(w, h, false, Image::FORMAT_RGBA8, bytes);
        atlas_ = ImageTexture::create_from_image(img);
        atlas_id_ = textures[0].id;
    }
}

void WevaDocument::update_document() {
    dirty_ = true;
    ensure_updated();
    queue_redraw();
}

void WevaDocument::_draw() {
    ensure_updated();
    if (!doc_) return;

    size_t count = 0;
    const weva_draw* draws = weva_document_draws(doc_, &count);
    for (size_t i = 0; i < count; ++i) {
        const weva_draw& d = draws[i];
        if (d.vertex_count == 0 || d.index_count == 0) continue;

        // draw_polygon takes a polygon OUTLINE and triangulates it, so feeding
        // it a triangle soup produces garbage where it does not fail outright
        // ("Invalid polygon data, triangulation failed"). canvas_item_add_
        // triangle_array takes the index buffer directly, which is exactly the
        // shape the core already produces — no expansion, and no triangulator
        // second-guessing geometry that is already triangles.
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
            // The core works in linear space; Godot's canvas expects sRGB, so
            // the conversion happens here rather than in the core, where it
            // would be wrong for a backend that wants linear.
            cw[k] = Color(v.r, v.g, v.b, v.a).linear_to_srgb();
            uw[k] = Vector2(v.u, v.v);
        }

        PackedInt32Array indices;
        indices.resize(static_cast<int64_t>(d.index_count));
        int32_t* iw = indices.ptrw();
        for (size_t k = 0; k < d.index_count; ++k) {
            iw[k] = static_cast<int32_t>(d.indices[k]);
        }

        const RID texture =
            (d.texture_id != 0 && atlas_.is_valid()) ? atlas_->get_rid() : RID();
        RenderingServer::get_singleton()->canvas_item_add_triangle_array(
            get_canvas_item(), indices, points, colors, uvs, PackedInt32Array(),
            PackedFloat32Array(), texture);
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
