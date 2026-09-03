#include "weva_node.h"

#include <godot_cpp/classes/viewport.hpp>

#include <godot_cpp/classes/font.hpp>
#include <godot_cpp/classes/font_file.hpp>
#include <godot_cpp/classes/image.hpp>
#include <godot_cpp/classes/rendering_server.hpp>
#include <godot_cpp/classes/os.hpp>
#include <godot_cpp/classes/theme_db.hpp>
#include <godot_cpp/core/class_db.hpp>
#include <godot_cpp/variant/packed_color_array.hpp>
#include <godot_cpp/variant/typed_array.hpp>
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

// The system symbol faces, loaded ONCE for the whole extension.
//
// They used to be per document, and that quietly corrupted any application
// that shows more than one. A SystemFont hands out TextServer RIDs that do not
// belong to it alone, so releasing one document's faces invalidated RIDs a
// LATER document was still drawing with: the errors start at the second
// document, and by the fourth a page came out with no text at all. Cycling the
// 35 samples produced 59,664 "font is null" errors and several empty pages;
// shared, it produces none.
//
// Loading eight system fonts per document was also simply wasteful — the faces
// are immutable and identical every time.
static std::vector<Ref<SystemFont>>& symbol_font_cache() {
    static std::vector<Ref<SystemFont>> cache;
    return cache;
}

const std::vector<Ref<SystemFont>>& shared_symbol_fonts() {
    std::vector<Ref<SystemFont>>& cache = symbol_font_cache();
    static bool built = false;
    if (built) return cache;
    built = true;
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
        cache.push_back(sf);
    }
    return cache;
}

// Dropped at module shutdown rather than by a static destructor, which would
// run after the engine has gone and free RIDs into nothing.
void release_shared_symbol_fonts() { symbol_font_cache().clear(); }

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

void WevaDocument::set_use_sdf_rects(bool use) {
    if (use == use_sdf_rects_) return;
    use_sdf_rects_ = use;
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
    for (const Ref<SystemFont>& sf : shared_symbol_fonts()) {
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
    RenderingServer* rs_ = RenderingServer::get_singleton();
    if (backdrop_shader_.is_valid()) rs_->free_rid(backdrop_shader_);
    for (const RID& r : rounded_materials_) {
        if (r.is_valid()) rs_->free_rid(r);
    }
    if (rounded_shader_.is_valid()) rs_->free_rid(rounded_shader_);
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
    ClassDB::bind_method(D_METHOD("update_document", "dt"), &WevaDocument::update_document,
                         DEFVAL(0.0));
    ClassDB::bind_method(D_METHOD("get_content_size"), &WevaDocument::get_content_size);
    ClassDB::bind_method(D_METHOD("query_bounds", "selector"), &WevaDocument::query_bounds);
    ClassDB::bind_method(D_METHOD("query_text", "selector"), &WevaDocument::query_text);
    ClassDB::bind_method(D_METHOD("set_element_attribute", "selector", "name", "value"),
                         &WevaDocument::set_element_attribute);
    ClassDB::bind_method(D_METHOD("remove_element_attribute", "selector", "name"),
                         &WevaDocument::remove_element_attribute);
    ClassDB::bind_method(D_METHOD("set_use_engine_font", "use"),
                         &WevaDocument::set_use_engine_font);
    ClassDB::bind_method(D_METHOD("get_use_engine_font"), &WevaDocument::get_use_engine_font);
    ClassDB::bind_method(D_METHOD("set_use_sdf_rects", "use"), &WevaDocument::set_use_sdf_rects);
    ClassDB::bind_method(D_METHOD("get_use_sdf_rects"), &WevaDocument::get_use_sdf_rects);
    ClassDB::bind_method(D_METHOD("has_engine_font"), &WevaDocument::has_engine_font);
    ClassDB::bind_method(D_METHOD("set_interactive", "on"), &WevaDocument::set_interactive);
    ClassDB::bind_method(D_METHOD("get_interactive"), &WevaDocument::get_interactive);
    ClassDB::bind_method(D_METHOD("element_id_at", "point"), &WevaDocument::element_id_at);
    ClassDB::bind_method(D_METHOD("set_focus", "selector"), &WevaDocument::set_focus);
    ADD_PROPERTY(PropertyInfo(Variant::BOOL, "interactive"), "set_interactive", "get_interactive");
    ClassDB::bind_method(D_METHOD("set_paused", "on"), &WevaDocument::set_paused);
    ClassDB::bind_method(D_METHOD("get_paused"), &WevaDocument::get_paused);
    ADD_PROPERTY(PropertyInfo(Variant::BOOL, "paused"), "set_paused", "get_paused");

    ClassDB::bind_method(D_METHOD("set_element_text", "selector", "text"),
                         &WevaDocument::set_element_text);
    ClassDB::bind_method(D_METHOD("get_element_text", "selector"),
                         &WevaDocument::get_element_text);
    ClassDB::bind_method(D_METHOD("add_element_class", "selector", "name"),
                         &WevaDocument::add_element_class);
    ClassDB::bind_method(D_METHOD("remove_element_class", "selector", "name"),
                         &WevaDocument::remove_element_class);
    ClassDB::bind_method(D_METHOD("toggle_element_class", "selector", "name", "on"),
                         &WevaDocument::toggle_element_class);
    ClassDB::bind_method(D_METHOD("has_element", "selector"), &WevaDocument::has_element);
    ClassDB::bind_method(D_METHOD("get_element_value", "selector"),
                         &WevaDocument::get_element_value);
    ClassDB::bind_method(D_METHOD("set_element_value", "selector", "value"),
                         &WevaDocument::set_element_value);
    ClassDB::bind_method(D_METHOD("set_pointer", "point", "buttons"), &WevaDocument::set_pointer);
    ClassDB::bind_method(D_METHOD("clear_pointer"), &WevaDocument::clear_pointer);
    ClassDB::bind_method(D_METHOD("scroll_at", "point", "delta"), &WevaDocument::scroll_at);
    ClassDB::bind_method(D_METHOD("scroll_element", "selector", "delta"),
                         &WevaDocument::scroll_element);
    ClassDB::bind_method(D_METHOD("set_element_scroll", "selector", "offset"),
                         &WevaDocument::set_element_scroll);
    ClassDB::bind_method(D_METHOD("get_element_scroll", "selector"),
                         &WevaDocument::get_element_scroll);
    ClassDB::bind_method(D_METHOD("get_element_scroll_max", "selector"),
                         &WevaDocument::get_element_scroll_max);
    ClassDB::bind_method(D_METHOD("scroll_into_view", "selector"),
                         &WevaDocument::scroll_into_view);
    ClassDB::bind_method(D_METHOD("set_element_html", "selector", "html"),
                         &WevaDocument::set_element_html);
    ClassDB::bind_method(D_METHOD("append_html", "selector", "html"),
                         &WevaDocument::append_html);
    ClassDB::bind_method(D_METHOD("remove_element", "selector"), &WevaDocument::remove_element);
    ClassDB::bind_method(D_METHOD("count_elements", "selector"), &WevaDocument::count_elements);
    ClassDB::bind_method(D_METHOD("get_element_attribute", "selector", "name"),
                         &WevaDocument::get_element_attribute);
    ClassDB::bind_method(D_METHOD("set_data", "data"), &WevaDocument::set_data);
    ClassDB::bind_method(D_METHOD("get_data"), &WevaDocument::get_data);
    ClassDB::bind_method(D_METHOD("set_data_source", "resolver"),
                         &WevaDocument::set_data_source);
    ClassDB::bind_method(D_METHOD("refresh_bindings"), &WevaDocument::refresh_bindings);
    ClassDB::bind_method(D_METHOD("set_controller", "controller"),
                         &WevaDocument::set_controller);
    ClassDB::bind_method(D_METHOD("get_controller"), &WevaDocument::get_controller);
    ADD_PROPERTY(PropertyInfo(Variant::DICTIONARY, "data"), "set_data", "get_data");
    ClassDB::bind_method(D_METHOD("send_key", "keycode", "pressed", "shift", "ctrl"),
                         &WevaDocument::send_key, DEFVAL(true), DEFVAL(false), DEFVAL(false));
    ClassDB::bind_method(D_METHOD("send_text", "text"), &WevaDocument::send_text);
    ClassDB::bind_method(D_METHOD("select_all"), &WevaDocument::select_all);
    ClassDB::bind_method(D_METHOD("select_word_at", "point"), &WevaDocument::select_word_at);
    ClassDB::bind_method(D_METHOD("open_select", "selector"), &WevaDocument::open_select);
    ClassDB::bind_method(D_METHOD("close_select"), &WevaDocument::close_select);
    ClassDB::bind_method(D_METHOD("get_open_select"), &WevaDocument::get_open_select);
    ClassDB::bind_method(D_METHOD("get_selected_text"), &WevaDocument::get_selected_text);
    ClassDB::bind_method(D_METHOD("set_element_selection", "selector", "start", "end"),
                         &WevaDocument::set_element_selection);
    ClassDB::bind_method(D_METHOD("get_element_selection", "selector"),
                         &WevaDocument::get_element_selection);

    // The element is named by its `id`, because that is the handle a script
    // and a stylesheet already share. An element with no id reports an empty
    // string, which a script can still compare against.
    ADD_SIGNAL(MethodInfo("handler_invoked", PropertyInfo(Variant::STRING, "handler"),
                          PropertyInfo(Variant::STRING, "id")));
    ADD_SIGNAL(MethodInfo("element_clicked", PropertyInfo(Variant::STRING, "id")));
    ADD_SIGNAL(MethodInfo("element_pressed", PropertyInfo(Variant::STRING, "id")));
    ADD_SIGNAL(MethodInfo("element_released", PropertyInfo(Variant::STRING, "id")));
    ADD_SIGNAL(MethodInfo("element_entered", PropertyInfo(Variant::STRING, "id")));
    ADD_SIGNAL(MethodInfo("element_exited", PropertyInfo(Variant::STRING, "id")));
    ADD_SIGNAL(MethodInfo("element_focused", PropertyInfo(Variant::STRING, "id")));
    ADD_SIGNAL(MethodInfo("element_blurred", PropertyInfo(Variant::STRING, "id")));
    ADD_SIGNAL(MethodInfo("value_changed", PropertyInfo(Variant::STRING, "id"),
                          PropertyInfo(Variant::STRING, "value")));
    ADD_SIGNAL(MethodInfo("key_pressed", PropertyInfo(Variant::STRING, "id"),
                          PropertyInfo(Variant::INT, "key"),
                          PropertyInfo(Variant::INT, "modifiers")));
    ADD_SIGNAL(MethodInfo("text_entered", PropertyInfo(Variant::STRING, "id"),
                          PropertyInfo(Variant::STRING, "text")));
    ClassDB::bind_method(D_METHOD("focus_next", "backwards"), &WevaDocument::focus_next);
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
    ADD_PROPERTY(PropertyInfo(Variant::BOOL, "use_sdf_rects"), "set_use_sdf_rects",
                 "get_use_sdf_rects");
}

void WevaDocument::_ready() {

    // The glyph atlas is shelf-packed with no gutter, and the core emits UVs
    // that address texels exactly. Godot's canvas default is linear filtering,
    // which both softens glyph edges the core drew crisply and samples across
    // shelf boundaries into whatever glyph was packed next door.
    set_texture_filter(TEXTURE_FILTER_NEAREST);

    // Without this the default `interactive` does nothing: the flag is set but
    // no input arrives, so :hover would be wired up and still never fire.
    set_process_input(interactive_);

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

void WevaDocument::set_interactive(bool on) {
    interactive_ = on;
    set_process_input(on);
    if (!on && doc_) {
        weva_document_clear_pointer(doc_);
        dirty_ = true;
        queue_redraw();
    }
}

// Pointer position in the document's own coordinates, which are the node's
// local ones: the document is laid out from this node's origin.
// Godot's key codes, translated to the handful the engine has meaning for.
// Everything else travels as WEVA_KEY_OTHER and reaches a script unchanged --
// the engine has no business deciding what `J` means in someone's game.
static int weva_key_from_godot(Key code) {
    switch (code) {
        case KEY_TAB: return WEVA_KEY_TAB;
        case KEY_ENTER:
        case KEY_KP_ENTER: return WEVA_KEY_ENTER;
        case KEY_SPACE: return WEVA_KEY_SPACE;
        case KEY_ESCAPE: return WEVA_KEY_ESCAPE;
        case KEY_BACKSPACE: return WEVA_KEY_BACKSPACE;
        case KEY_DELETE: return WEVA_KEY_DELETE;
        case KEY_LEFT: return WEVA_KEY_LEFT;
        case KEY_RIGHT: return WEVA_KEY_RIGHT;
        case KEY_UP: return WEVA_KEY_UP;
        case KEY_DOWN: return WEVA_KEY_DOWN;
        case KEY_HOME: return WEVA_KEY_HOME;
        case KEY_END: return WEVA_KEY_END;
        case KEY_PAGEUP: return WEVA_KEY_PAGE_UP;
        case KEY_PAGEDOWN: return WEVA_KEY_PAGE_DOWN;
        default: return WEVA_KEY_OTHER;
    }
}

void WevaDocument::_input(const Ref<InputEvent>& event) {
    if (!interactive_ || !doc_ || event.is_null()) return;

    const Ref<InputEventKey> key = event;
    if (key.is_valid() && !key->is_echo()) {
        uint32_t modifiers = 0;
        if (key->is_shift_pressed()) modifiers |= WEVA_MOD_SHIFT;
        if (key->is_ctrl_pressed()) modifiers |= WEVA_MOD_CTRL;
        if (key->is_alt_pressed()) modifiers |= WEVA_MOD_ALT;
        if (key->is_meta_pressed()) modifiers |= WEVA_MOD_META;
        const int code = weva_key_from_godot(key->get_keycode());
        const bool consumed =
            weva_document_key(doc_, code, modifiers, key->is_pressed() ? 1 : 0) != 0;
        // The unicode a key produced is a separate thing from the key: Shift+1
        // is one key and the text "!", and a dead key produces no text at all.
        if (key->is_pressed() && key->get_unicode() != 0) {
            const String character = String::chr(key->get_unicode());
            const CharString utf8 = character.utf8();
            weva_document_text_input(doc_, utf8.get_data());
        }
        dirty_ = true;
        queue_redraw();
        if (consumed) get_viewport()->set_input_as_handled();
        return;
    }

    // A finger dragging pans what is under it. There is no mouse equivalent --
    // a browser does not pan on drag, and doing so would fight every button
    // and slider in the document -- but on a touchscreen it is the only way to
    // scroll at all.
    const Ref<InputEventScreenDrag> touch = event;
    if (touch.is_valid()) {
        const Vector2 at = get_global_transform().affine_inverse().xform(touch->get_position());
        const Vector2 by = touch->get_relative();
        ensure_updated();
        // Negated: the content follows the finger, so dragging UP moves the
        // list down through the view.
        if (weva_document_scroll(doc_, at.x, at.y, -by.x, -by.y)) {
            dirty_ = true;
            queue_redraw();
            get_viewport()->set_input_as_handled();
        }
        return;
    }

    const Ref<InputEventMouseMotion> motion = event;
    const Ref<InputEventMouseButton> button = event;
    if (motion.is_null() && button.is_null()) return;

    const Vector2 local = get_global_transform().affine_inverse().xform(
        motion.is_valid() ? motion->get_global_position() : button->get_global_position());
    // The wheel, before the pointer bookkeeping: a wheel event carries no
    // movement, so the "nothing moved" early return below would swallow it.
    if (button.is_valid() && button->is_pressed()) {
        // Godot reports a wheel notch as a button press with a factor; a line
        // is the 40px a browser scrolls per notch.
        const double factor = button->get_factor() > 0 ? button->get_factor() : 1.0;
        const double step = 40.0 * factor;
        double dx = 0, dy = 0;
        switch (button->get_button_index()) {
            case MOUSE_BUTTON_WHEEL_UP: dy = -step; break;
            case MOUSE_BUTTON_WHEEL_DOWN: dy = step; break;
            case MOUSE_BUTTON_WHEEL_LEFT: dx = -step; break;
            case MOUSE_BUTTON_WHEEL_RIGHT: dx = step; break;
            default: break;
        }
        if (dx != 0 || dy != 0) {
            ensure_updated();
            // Handled only when something actually scrolled, so a wheel over a
            // document with nowhere to go still reaches the game behind it.
            if (weva_document_scroll(doc_, local.x, local.y, dx, dy)) {
                dirty_ = true;
                queue_redraw();
                get_viewport()->set_input_as_handled();
            }
            return;
        }
    }

    // A double click takes the word under it. The platform decides what counts
    // as one -- the document is never told the time -- so this is where that
    // knowledge enters.
    if (button.is_valid() && button->is_pressed() && button->is_double_click() &&
        button->get_button_index() == MOUSE_BUTTON_LEFT) {
        ensure_updated();
        if (weva_document_select_word_at(doc_, local.x, local.y)) {
            dirty_ = true;
            queue_redraw();
            pump_events();
            get_viewport()->set_input_as_handled();
            return;
        }
    }

    uint32_t buttons = buttons_;
    if (button.is_valid()) {
        // Only the primary button drives :active, which is what the pseudo
        // class means; the others are the host's to route.
        if (button->get_button_index() == MOUSE_BUTTON_LEFT) {
            buttons = button->is_pressed() ? 1u : 0u;
        }
    }
    if (local == pointer_ && buttons == buttons_) return;
    pointer_ = local;
    buttons_ = buttons;
    weva_document_set_pointer(doc_, local.x, local.y, buttons);
    // The document decides whether anything actually changed; an update that
    // finds no style different publishes the frame it already had.
    dirty_ = true;
    queue_redraw();
}

void WevaDocument::_notification(int what) {
    if (what == NOTIFICATION_EXIT_TREE && doc_) {
        weva_document_clear_pointer(doc_);
    }
}

void WevaDocument::set_pointer(const Vector2& point, int buttons) {
    if (!doc_) return;
    ensure_updated();
    pointer_ = point;
    buttons_ = static_cast<uint32_t>(buttons);
    weva_document_set_pointer(doc_, point.x, point.y, buttons_);
    dirty_ = true;
    queue_redraw();
    pump_events();
}

godot::String WevaDocument::focus_next(bool backwards) {
    if (!doc_) return String();
    ensure_updated();
    const weva_element_t e = weva_document_focus_next(doc_, backwards ? 1 : 0);
    dirty_ = true;
    queue_redraw();
    // Delivered here rather than next frame: a script that moves focus and
    // then looks at what happened should not have to wait for a redraw.
    pump_events();
    return id_of(e);
}

bool WevaDocument::scroll_at(const Vector2& point, const Vector2& delta) {
    if (!doc_) return false;
    ensure_updated();
    if (!weva_document_scroll(doc_, point.x, point.y, delta.x, delta.y)) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

bool WevaDocument::scroll_element(const String& selector, const Vector2& delta) {
    if (!doc_) return false;
    ensure_updated();
    const Vector2 at = get_element_scroll(selector);
    const Vector2 most = get_element_scroll_max(selector);
    const Vector2 to(CLAMP(at.x + delta.x, 0.0f, most.x), CLAMP(at.y + delta.y, 0.0f, most.y));
    if (to == at) return false;
    set_element_scroll(selector, to);
    return true;
}

void WevaDocument::set_element_scroll(const String& selector, const Vector2& offset) {
    if (!doc_) return;
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return;
    weva_element_set_scroll(doc_, e, offset.x, offset.y);
    dirty_ = true;
    queue_redraw();
}

Vector2 WevaDocument::get_element_scroll(const String& selector) {
    if (!doc_) return Vector2();
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return Vector2();
    double x = 0, y = 0;
    if (weva_element_scroll(doc_, e, &x, &y, nullptr, nullptr) != WEVA_OK) return Vector2();
    return Vector2(static_cast<real_t>(x), static_cast<real_t>(y));
}

Vector2 WevaDocument::get_element_scroll_max(const String& selector) {
    if (!doc_) return Vector2();
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return Vector2();
    double mx = 0, my = 0;
    if (weva_element_scroll(doc_, e, nullptr, nullptr, &mx, &my) != WEVA_OK) return Vector2();
    return Vector2(static_cast<real_t>(mx), static_cast<real_t>(my));
}

// ---- Building the document from data ------------------------------------
//
// Setting text and classes lets a script update a panel; these let it build
// one. Rows are addressed the way CSS addresses them -- `#list .row:nth-child(2)`
// -- so a script that can style a list can also fill it, without inventing a
// second naming scheme for the same elements.

// ---- Input a host routes itself ----------------------------------------
//
// `interactive` makes the document read the mouse and keyboard straight from
// the viewport, which is what most scenes want. A game with its own input map
// -- a controller whose d-pad should scroll a list, a pause menu that decides
// who gets the keys -- wants to hand events over one at a time instead. These
// are that: the same path _input takes, reachable from a script.

bool WevaDocument::send_key(int keycode, bool pressed, bool shift, bool ctrl) {
    if (!doc_) return false;
    ensure_updated();
    uint32_t modifiers = 0;
    if (shift) modifiers |= WEVA_MOD_SHIFT;
    if (ctrl) modifiers |= WEVA_MOD_CTRL;
    const int code = weva_key_from_godot(static_cast<Key>(keycode));
    const bool consumed = weva_document_key(doc_, code, modifiers, pressed ? 1 : 0) != 0;
    dirty_ = true;
    queue_redraw();
    pump_events();
    return consumed;
}

void WevaDocument::send_text(const String& text) {
    if (!doc_ || text.is_empty()) return;
    ensure_updated();
    const CharString utf8 = text.utf8();
    weva_document_text_input(doc_, utf8.get_data());
    dirty_ = true;
    queue_redraw();
    pump_events();
}

// ---- Selection -----------------------------------------------------------
//
// Shift with the movement keys selects, and typing replaces what is selected;
// those the document does by itself. These are the two it cannot: select-all,
// because the ABI's key enum has no letters and so never sees Ctrl+A, and
// reading the selected text, because the clipboard belongs to the platform.

bool WevaDocument::select_word_at(const Vector2& point) {
    if (!doc_) return false;
    ensure_updated();
    if (!weva_document_select_word_at(doc_, point.x, point.y)) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

// ---- Dropdowns -----------------------------------------------------------
//
// Clicking a <select> opens it and clicking an option chooses it, all through
// the pointer the node already forwards. These are for a host that routes its
// own input -- a controller opening the list, a menu closing it.

bool WevaDocument::open_select(const String& selector) {
    if (!doc_) return false;
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return false;
    if (!weva_document_open_select(doc_, e)) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

void WevaDocument::close_select() {
    if (!doc_) return;
    weva_document_open_select(doc_, WEVA_ELEMENT_NONE);
    dirty_ = true;
    queue_redraw();
}

String WevaDocument::get_open_select() {
    if (!doc_) return String();
    return id_of(weva_document_open_select_element(doc_));
}

bool WevaDocument::select_all() {
    if (!doc_) return false;
    ensure_updated();
    if (!weva_document_select_all(doc_)) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

String WevaDocument::get_selected_text() {
    if (!doc_) return String();
    ensure_updated();
    const size_t n = weva_document_selected_text(doc_, nullptr, 0);
    if (n == 0) return String();
    std::vector<char> buffer(n + 1, 0);
    weva_document_selected_text(doc_, buffer.data(), buffer.size());
    return String::utf8(buffer.data());
}

bool WevaDocument::set_element_selection(const String& selector, int start, int end) {
    if (!doc_) return false;
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return false;
    if (weva_element_set_selection(doc_, e, start, end) != WEVA_OK) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

Vector2i WevaDocument::get_element_selection(const String& selector) {
    if (!doc_) return Vector2i();
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return Vector2i();
    int start = 0, end = 0;
    if (weva_element_selection(doc_, e, &start, &end) != WEVA_OK) return Vector2i();
    return Vector2i(start, end);
}

// ---- Data binding --------------------------------------------------------
//
// `{{ Player.Gold }}` in the markup and a Dictionary in the script. A dotted
// path walks nested Dictionaries and Objects, so `{{ Player.Gold }}` reads
// data["Player"]["Gold"] whether Player is a Dictionary, a Resource or a Node.
// A Callable takes over entirely when a game keeps its state somewhere this
// cannot reach.

namespace {

// One step of a dotted path.
bool step(const Variant& from, const String& key, Variant* out) {
    switch (from.get_type()) {
        case Variant::DICTIONARY: {
            const Dictionary d = from;
            if (!d.has(key)) return false;
            *out = d[key];
            return true;
        }
        case Variant::OBJECT: {
            Object* o = from;
            if (o == nullptr) return false;
            // has_method is not the question -- a property is what a binding
            // path names -- so the property list is what decides.
            bool has = false;
            const TypedArray<Dictionary> properties = o->get_property_list();
            for (int i = 0; i < properties.size() && !has; ++i) {
                const Dictionary p = properties[i];
                has = String(p.get("name", "")) == key;
            }
            if (!has) return false;
            *out = o->get(key);
            return true;
        }
        case Variant::ARRAY: {
            // `Items.0` indexes a list, which is what a repeat will want.
            if (!key.is_valid_int()) return false;
            const Array a = from;
            const int i = key.to_int();
            if (i < 0 || i >= a.size()) return false;
            *out = a[i];
            return true;
        }
        default: return false;
    }
}

}   // namespace

bool WevaDocument::resolve_binding(const String& path, String* out) const {
    if (data_source_.is_valid()) {
        const Variant v = data_source_.call(path);
        if (v.get_type() == Variant::NIL) return false;
        *out = v.stringify();
        return true;
    }
    Variant current = data_;
    const PackedStringArray parts = path.split(".");
    for (int i = 0; i < parts.size(); ++i) {
        Variant next;
        if (!step(current, parts[i], &next)) return false;
        current = next;
    }
    if (current.get_type() == Variant::NIL) return false;
    // A bool arrives as "true"/"false", which `data-class-` and an attribute
    // selector both read the way they read the "True"/"False" the Unity engine
    // produces.
    *out = current.stringify();
    return true;
}

// How long the list at `path` is, or -1 when it is not one. `data-each` is the
// only caller: an Array answers, and so does anything with a size() a script
// exposed, but a String is deliberately NOT a list of characters.
static int weva_binding_count(void* user, const char* path) {
    WevaDocument* node = static_cast<WevaDocument*>(user);
    return node->resolve_binding_count(String::utf8(path));
}

static size_t weva_binding_read(void* user, const char* path, char* buffer, size_t capacity,
                                int* found) {
    WevaDocument* node = static_cast<WevaDocument*>(user);
    String value;
    if (!node->resolve_binding(String::utf8(path), &value)) {
        *found = 0;
        return 0;
    }
    *found = 1;
    const CharString utf8 = value.utf8();
    const size_t length = static_cast<size_t>(utf8.length());
    if (buffer && capacity > 0) {
        const size_t n = length < capacity - 1 ? length : capacity - 1;
        if (n > 0) memcpy(buffer, utf8.get_data(), n);
        buffer[n] = 0;
    }
    return length;
}

String WevaDocument::get_element_attribute(const String& selector, const String& name) {
    if (!doc_) return String();
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return String();
    const CharString key = name.utf8();
    const size_t n = weva_element_attribute(doc_, e, key.get_data(), nullptr, 0);
    if (n == 0) return String();
    std::vector<char> buffer(n + 1, 0);
    weva_element_attribute(doc_, e, key.get_data(), buffer.data(), buffer.size());
    return String::utf8(buffer.data());
}

void WevaDocument::set_controller(Object* controller) {
    controller_ = controller ? controller->get_instance_id() : ObjectID();
}

Object* WevaDocument::get_controller() const {
    return controller_.is_valid() ? ObjectDB::get_instance(controller_) : nullptr;
}

int WevaDocument::resolve_binding_count(const String& path) const {
    if (data_source_.is_valid()) {
        const Variant v = data_source_.call(path);
        return v.get_type() == Variant::ARRAY ? static_cast<int>(Array(v).size()) : -1;
    }
    Variant current = data_;
    const PackedStringArray parts = path.split(".");
    for (int i = 0; i < parts.size(); ++i) {
        Variant next;
        if (!step(current, parts[i], &next)) return -1;
        current = next;
    }
    return current.get_type() == Variant::ARRAY ? static_cast<int>(Array(current).size()) : -1;
}

void WevaDocument::set_data(const Dictionary& data) {
    data_ = data;
    refresh_bindings();
}

Dictionary WevaDocument::get_data() const { return data_; }

void WevaDocument::set_data_source(const Callable& resolver) {
    data_source_ = resolver;
    refresh_bindings();
}

int WevaDocument::refresh_bindings() {
    if (!doc_) return 0;
    weva_binding_source source{};
    source.user = this;
    source.value = &weva_binding_read;
    source.count = &weva_binding_count;
    weva_document_set_binding_source(doc_, &source);
    const int changed = weva_document_refresh_bindings(doc_);
    if (changed > 0) {
        dirty_ = true;
        queue_redraw();
    }
    return changed;
}

bool WevaDocument::set_element_html(const String& selector, const String& html) {
    if (!doc_) return false;
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return false;
    const CharString body = html.utf8();
    if (weva_element_set_html(doc_, e, body.get_data(),
                              static_cast<size_t>(body.length())) != WEVA_OK) {
        return false;
    }
    dirty_ = true;
    queue_redraw();
    return true;
}

bool WevaDocument::append_html(const String& selector, const String& html) {
    if (!doc_) return false;
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return false;
    const CharString body = html.utf8();
    const weva_element_t added =
        weva_element_append_html(doc_, e, body.get_data(), static_cast<size_t>(body.length()));
    dirty_ = true;
    queue_redraw();
    return added != WEVA_ELEMENT_NONE;
}

bool WevaDocument::remove_element(const String& selector) {
    if (!doc_) return false;
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return false;
    if (weva_element_remove(doc_, e) != WEVA_OK) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

int WevaDocument::count_elements(const String& selector) {
    if (!doc_) return 0;
    ensure_updated();
    const CharString sel = selector.utf8();
    return static_cast<int>(weva_document_query_all(doc_, sel.get_data(), nullptr, 0));
}

bool WevaDocument::scroll_into_view(const String& selector) {
    if (!doc_) return false;
    ensure_updated();
    const CharString sel = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, sel.get_data());
    if (e == WEVA_ELEMENT_NONE) return false;
    if (weva_element_scroll_into_view(doc_, e) != WEVA_OK) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

void WevaDocument::clear_pointer() {
    if (!doc_) return;
    weva_document_clear_pointer(doc_);
    pointer_ = Vector2(-1, -1);
    buttons_ = 0;
    dirty_ = true;
    queue_redraw();
    pump_events();
}

godot::String WevaDocument::id_of(uint32_t element) {
    if (!doc_ || element == WEVA_ELEMENT_NONE) return String();
    char buffer[128];
    const size_t n = weva_element_attribute(doc_, element, "id", buffer, sizeof(buffer));
    if (n == 0 || n >= sizeof(buffer)) return String();
    return String(buffer);
}

uint32_t WevaDocument::resolve(const godot::String& selector) {
    if (!doc_ || selector.is_empty()) return WEVA_ELEMENT_NONE;
    ensure_updated();
    const CharString s = selector.utf8();
    return weva_document_query(doc_, s.get_data());
}

void WevaDocument::pump_events() {
    if (!doc_) return;
    weva_event e{};
    while (weva_document_poll_event(doc_, &e)) {
        const String id = id_of(e.target);
        // What the MARKUP called it, before what the element is called. A
        // controller with that method gets it, and `handler_invoked` carries
        // it either way -- so a script can dispatch by the name the designer
        // wrote rather than by which element it happened on.
        if (e.handler[0] != 0) {
            const String handler = String::utf8(e.handler);
            Object* controller = get_controller();
            if (controller && controller->has_method(handler)) {
                controller->call(handler, id);
            }
            emit_signal("handler_invoked", handler, id);
        }
        switch (e.kind) {
            case WEVA_EVENT_CLICK: emit_signal("element_clicked", id); break;
            case WEVA_EVENT_POINTER_DOWN: emit_signal("element_pressed", id); break;
            case WEVA_EVENT_POINTER_UP: emit_signal("element_released", id); break;
            case WEVA_EVENT_POINTER_ENTER: emit_signal("element_entered", id); break;
            case WEVA_EVENT_POINTER_LEAVE: emit_signal("element_exited", id); break;
            case WEVA_EVENT_KEY_DOWN:
                emit_signal("key_pressed", id, e.key, static_cast<int>(e.modifiers));
                break;
            case WEVA_EVENT_TEXT_INPUT: emit_signal("text_entered", id, String(e.text)); break;
            case WEVA_EVENT_FOCUS: emit_signal("element_focused", id); break;
            case WEVA_EVENT_BLUR: emit_signal("element_blurred", id); break;
            case WEVA_EVENT_VALUE_CHANGED:
                // The value rides in the event when it is short; anything
                // longer is read back, so a script never sees a truncated one.
                emit_signal("value_changed", id, get_element_value("#" + id));
                break;
            default: break;
        }
    }
}

bool WevaDocument::set_element_text(const godot::String& selector, const godot::String& text) {
    const uint32_t e = resolve(selector);
    if (e == WEVA_ELEMENT_NONE) return false;
    const CharString t = text.utf8();
    if (weva_element_set_text(doc_, e, t.get_data()) != WEVA_OK) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

godot::String WevaDocument::get_element_text(const godot::String& selector) {
    const uint32_t e = resolve(selector);
    if (e == WEVA_ELEMENT_NONE) return String();
    const size_t needed = weva_element_text(doc_, e, nullptr, 0);
    std::vector<char> buffer(needed + 1, '\0');
    weva_element_text(doc_, e, buffer.data(), buffer.size());
    return String(buffer.data());
}

godot::String WevaDocument::get_element_value(const godot::String& selector) {
    const uint32_t e = resolve(selector);
    if (e == WEVA_ELEMENT_NONE) return String();
    const size_t needed = weva_element_value(doc_, e, nullptr, 0);
    std::vector<char> buffer(needed + 1, '\0');
    weva_element_value(doc_, e, buffer.data(), buffer.size());
    return String(buffer.data());
}

bool WevaDocument::set_element_value(const godot::String& selector, const godot::String& value) {
    const uint32_t e = resolve(selector);
    if (e == WEVA_ELEMENT_NONE) return false;
    const CharString v = value.utf8();
    if (weva_element_set_value(doc_, e, v.get_data()) != WEVA_OK) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

bool WevaDocument::has_element(const godot::String& selector) {
    return resolve(selector) != WEVA_ELEMENT_NONE;
}

// The class list, read-modify-write. A script toggling a state class is the
// ordinary way to drive a :hover-style rule from game logic, and doing it by
// hand through set_attribute means every caller reimplements the split.
bool WevaDocument::toggle_element_class(const godot::String& selector, const godot::String& name,
                                        bool on) {
    const uint32_t e = resolve(selector);
    if (e == WEVA_ELEMENT_NONE || name.is_empty()) return false;
    char buffer[512];
    const size_t n = weva_element_attribute(doc_, e, "class", buffer, sizeof(buffer));
    if (n >= sizeof(buffer)) return false;
    PackedStringArray tokens = String(buffer).split(" ", false);
    PackedStringArray kept;
    bool present = false;
    for (int i = 0; i < tokens.size(); ++i) {
        if (tokens[i] == name) { present = true; continue; }
        kept.push_back(tokens[i]);
    }
    if (on) kept.push_back(name);
    if (present == on) return true;   // already as asked
    const String joined = String(" ").join(kept);
    const CharString value = joined.utf8();
    if (weva_element_set_attribute(doc_, e, "class", value.get_data()) != WEVA_OK) return false;
    dirty_ = true;
    queue_redraw();
    return true;
}

bool WevaDocument::add_element_class(const godot::String& selector, const godot::String& name) {
    return toggle_element_class(selector, name, true);
}

bool WevaDocument::remove_element_class(const godot::String& selector, const godot::String& name) {
    return toggle_element_class(selector, name, false);
}

godot::String WevaDocument::element_id_at(const Vector2& point) {
    if (!doc_) return String();
    ensure_updated();
    const weva_element_t e = weva_document_element_at(doc_, point.x, point.y);
    if (e == WEVA_ELEMENT_NONE) return String();
    // The handle is an index; a script wants something it can act on, and an
    // id is the one stable name the document has.
    char buffer[128];
    const size_t n = weva_element_attribute(doc_, e, "id", buffer, sizeof(buffer));
    if (n == 0 || n >= sizeof(buffer)) return String();
    return String(buffer);
}

bool WevaDocument::set_focus(const godot::String& selector) {
    if (!doc_) return false;
    ensure_updated();
    if (selector.is_empty()) {
        return weva_document_set_focus(doc_, WEVA_ELEMENT_NONE) == WEVA_OK;
    }
    const CharString s = selector.utf8();
    const weva_element_t e = weva_document_query(doc_, s.get_data());
    if (e == WEVA_ELEMENT_NONE) return false;
    if (weva_document_set_focus(doc_, e) != WEVA_OK) return false;
    dirty_ = true;
    queue_redraw();
    pump_events();
    return true;
}

// The clock, which transitions need and nothing else does.
//
// A transition is the one thing that changes a document without anyone
// touching it, so the node has to keep asking -- but only while something is
// actually running. weva_document_update returns immediately when no
// transition is in flight, so the cost of asking is a branch.
void WevaDocument::_process(double delta) {
    if (!doc_) return;
    if (paused_) {
        // Still update if something else made the document dirty; just do not
        // let time be the thing that moved.
        if (dirty_) ensure_updated(0);
        pump_events();
        return;
    }
    pending_dt_ += delta;
    if (!dirty_ && pending_dt_ <= 0) return;
    const double dt = pending_dt_;
    pending_dt_ = 0;
    ensure_updated(dt);
    pump_events();
    // A document with something in flight has to be drawn again next frame;
    // the draw list changed under it.
    if (weva_document_is_animating(doc_)) queue_redraw();
}

void WevaDocument::ensure_updated(double dt) {
    if (!doc_ || (!dirty_ && dt <= 0)) return;
    ensure_font_backend();
    // Not an error to update an empty document: a scene may set css before
    // html, and the next update picks both up.
    weva_document_update(doc_, dt);
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

void WevaDocument::update_document(double dt) {
    dirty_ = true;
    ensure_updated(dt);
    // Events raised by whatever the caller just did are delivered here too, so
    // a script that drives the pointer and then updates does not have to wait
    // for a frame to hear about it.
    pump_events();
    queue_redraw();
}

// Evaluating a rounded box per pixel, which is what a rasterizer that only
// takes triangles cannot do.
//
// The core's coverage ramp approximates the edge with a one-pixel band of
// interpolated alpha. This computes the real thing: the signed distance to the
// rounded box, divided by its own screen-space derivative, which is the exact
// fractional coverage to well under a percent -- the same quantity Skia builds
// its coverage mask from. It is also correct under any scale, where a baked
// ramp is only correct at the scale it was baked for.
static const char* kRoundedShader = R"(shader_type canvas_item;
render_mode unshaded;

uniform vec2 half_size;
uniform vec4 radii;        // top-left, top-right, bottom-right, bottom-left
uniform vec4 fill;

varying vec2 local;

void vertex() {
    // UV carries the position within the quad, so the fragment stage can work
    // in the shape's own coordinates whatever the canvas transform is.
    local = (UV - vec2(0.5)) * (half_size * 2.0);
}

float sd_round_box(vec2 p, vec2 b, vec4 r) {
    r.xy = (p.x > 0.0) ? r.xy : r.zw;
    r.x = (p.y > 0.0) ? r.x : r.y;
    vec2 q = abs(p) - b + r.x;
    return min(max(q.x, q.y), 0.0) + length(max(q, vec2(0.0))) - r.x;
}

void fragment() {
    float d = sd_round_box(local, half_size, radii);
    // fwidth is how far d moves across one pixel, so d/fwidth(d) is the
    // distance to the edge measured IN pixels however the quad is scaled.
    float w = fwidth(d);
    float cov = w > 0.0 ? clamp(0.5 - d / w, 0.0, 1.0) : (d <= 0.0 ? 1.0 : 0.0);
    COLOR = vec4(fill.rgb, fill.a * cov);
}
)";

RID WevaDocument::rounded_rect_material(const weva_rounded_rect& s) {
    RenderingServer* rs = RenderingServer::get_singleton();
    if (!rounded_shader_.is_valid()) {
        rounded_shader_ = rs->shader_create();
        rs->shader_set_code(rounded_shader_, String(kRoundedShader));
    }
    // Pooled across frames: a material per rounded box per frame would churn
    // hundreds of RIDs on a dashboard.
    if (rounded_used_ >= rounded_materials_.size()) {
        const RID m = rs->material_create();
        rs->material_set_shader(m, rounded_shader_);
        rounded_materials_.push_back(m);
    }
    const RID m = rounded_materials_[rounded_used_++];

    rs->material_set_param(m, "half_size",
                           Vector2(static_cast<real_t>(s.width * 0.5),
                                   static_cast<real_t>(s.height * 0.5)));
    // One radius per corner: the SDF takes a circular corner, so an elliptical
    // one is approximated by its smaller axis. CSS rarely asks for an ellipse
    // and this path declines the shape when it does (see draw_rounded_rect).
    rs->material_set_param(m, "radii",
                           Color(static_cast<real_t>(s.radii[0][0]), static_cast<real_t>(s.radii[1][0]),
                                 static_cast<real_t>(s.radii[2][0]), static_cast<real_t>(s.radii[3][0])));
    rs->material_set_param(m, "fill", Color(s.r, s.g, s.b, s.a).linear_to_srgb());
    return m;
}

bool WevaDocument::draw_rounded_rect(const RID& item, const weva_draw& d) {
    if (!use_sdf_rects_) return false;
    const weva_rounded_rect& s = d.rounded_rect;
    if (s.width <= 0 || s.height <= 0) return false;
    // Elliptical corners are not what the SDF computes; hand those back so the
    // tessellation draws them correctly rather than nearly.
    for (int i = 0; i < 4; ++i) {
        if (std::fabs(s.radii[i][0] - s.radii[i][1]) > 0.01) return false;
    }

    // One pixel of margin so the coverage ramp has somewhere to land.
    const real_t pad = 1.0f;
    const real_t x0 = static_cast<real_t>(s.x) - pad, y0 = static_cast<real_t>(s.y) - pad;
    const real_t x1 = static_cast<real_t>(s.x + s.width) + pad;
    const real_t y1 = static_cast<real_t>(s.y + s.height) + pad;
    // UV runs 0..1 over the PADDED quad, and the shader maps it back through
    // half_size, so the padding has to be part of the size it is told.
    const real_t hx = (x1 - x0) * 0.5f, hy = (y1 - y0) * 0.5f;

    RenderingServer* rs = RenderingServer::get_singleton();
    const RID m = rounded_rect_material(s);
    rs->material_set_param(m, "half_size", Vector2(hx - pad, hy - pad));

    PackedVector2Array points;
    PackedColorArray colors;
    PackedVector2Array uvs;
    points.resize(4);
    colors.resize(4);
    uvs.resize(4);
    Vector2* pw = points.ptrw();
    Color* cw = colors.ptrw();
    Vector2* uw = uvs.ptrw();
    const Vector2 corners[4] = {Vector2(x0, y0), Vector2(x1, y0), Vector2(x1, y1), Vector2(x0, y1)};
    const Vector2 uv[4] = {Vector2(0, 0), Vector2(1, 0), Vector2(1, 1), Vector2(0, 1)};
    for (int i = 0; i < 4; ++i) {
        pw[i] = corners[i];
        cw[i] = Color(1, 1, 1, 1);
        uw[i] = uv[i];
    }
    PackedInt32Array idx;
    idx.resize(6);
    int32_t* iw = idx.ptrw();
    const int32_t order[6] = {0, 1, 2, 0, 2, 3};
    for (int i = 0; i < 6; ++i) iw[i] = order[i];

    // Its own canvas item, because a material is per item and the document is
    // otherwise one item. That is the cost of this path and what the
    // measurement has to weigh against the triangles it saves.
    const RID quad = rs->canvas_item_create();
    rs->canvas_item_set_parent(quad, item);
    rs->canvas_item_set_material(quad, m);
    rs->canvas_item_set_draw_index(quad, static_cast<int32_t>(layer_items_.size()));
    layer_items_.push_back(quad);
    rs->canvas_item_add_triangle_array(quad, idx, points, colors, uvs, PackedInt32Array(),
                                       PackedFloat32Array(), RID());
    return true;
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

    rounded_used_ = 0;
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
        if (d.kind == WEVA_DRAW_ROUNDED_RECT && draw_rounded_rect(get_canvas_item(), d)) continue;
        add_triangles(get_canvas_item(), d);
    }
}

Vector2 WevaDocument::get_content_size() {
    if (!doc_) return Vector2();
    ensure_updated();
    double w = 0, h = 0;
    if (weva_document_content_size(doc_, &w, &h) != WEVA_OK) return Vector2();
    return Vector2(static_cast<real_t>(w), static_cast<real_t>(h));
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
