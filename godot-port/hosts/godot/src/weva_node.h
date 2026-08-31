#pragma once

#include <godot_cpp/classes/canvas_item.hpp>
#include <godot_cpp/classes/image_texture.hpp>
#include <godot_cpp/classes/node2d.hpp>
#include <godot_cpp/variant/packed_int32_array.hpp>
#include <godot_cpp/variant/packed_vector2_array.hpp>

#include "godot_font.h"
#include "weva_c.h"

// The Godot host: a Node2D that owns a weva document and draws its geometry.
//
// It talks to libweva only through weva_c.h. No Godot type reaches the core and
// no core C++ type reaches Godot — which is the whole point of the ABI, and the
// property that lets the same core serve a Unity host later.

namespace weva_godot {

class WevaDocument : public godot::Node2D {
    GDCLASS(WevaDocument, godot::Node2D)

public:
    WevaDocument();
    ~WevaDocument() override;

    void _ready() override;
    void _draw() override;

    // Loading either of these marks the document dirty; the next frame runs
    // the update. Splitting load from update means a caller can set several
    // things without paying for several layouts.
    void set_html(const godot::String& html);
    godot::String get_html() const { return html_; }
    void set_css(const godot::String& css);
    godot::String get_css() const { return css_; }

    // The viewport the document lays out against. Defaults to the node's
    // canvas size on first draw.
    void set_document_size(const godot::Vector2& size);
    godot::Vector2 get_document_size() const { return size_; }

    // Runs cascade, layout and paint now, rather than waiting for the frame.
    void update_document();

    // Whether to draw with the engine's own font. Turning it off falls back to
    // the core's built-in 5x7 face, which is what the backend comparison needs:
    // holding the font fixed is the only way a pixel difference between this
    // host and the reference rasteriser means anything.
    void set_use_engine_font(bool use);
    bool get_use_engine_font() const { return use_engine_font_; }

    // The border box of the first element matching `selector`, in document
    // coordinates. A zero-size rect means no match — Godot has no natural
    // "absent rect", and a caller checking `size == 0` is the same test they
    // would write against `has_size()`.
    godot::Rect2 query_bounds(const godot::String& selector);
    godot::String query_text(const godot::String& selector);
    // Returns false when the element does not exist, so a caller can tell a
    // failed lookup from a successful no-op.
    bool set_element_attribute(const godot::String& selector, const godot::String& name,
                               const godot::String& value);
    // Removal is a distinct operation, not `set` with an empty value: an empty
    // string still satisfies a presence selector like [data-hide], so without
    // this there is no way from GDScript to make such a selector stop matching.
    bool remove_element_attribute(const godot::String& selector, const godot::String& name);

    // Diagnostics the render tests assert on.
    int get_draw_count() const;
    int get_triangle_count() const;
    // False when the engine gave us no usable face and the core's stub font is
    // still in play — worth being able to assert on, since text that renders
    // with the 5x7 stub looks like a font choice rather than a failure.
    bool has_engine_font() const { return font_face_ != 0; }

protected:
    static void _bind_methods();

private:
    // Idempotent: adopts the engine's fallback face the first time it can, and
    // is called from both the constructor and _ready because ThemeDB is not
    // guaranteed to be up at construction.
    void ensure_font_backend();

protected:

private:
    void ensure_updated();

    weva_document_t doc_ = nullptr;
    godot::String html_;
    godot::String css_;
    godot::Vector2 size_{0, 0};
    bool dirty_ = true;
    // The atlas texture, rebuilt when the document publishes a new one. Held
    // so it outlives the draw call that references it.
    godot::Ref<godot::ImageTexture> atlas_;
    // The font backend must outlive the document: the core holds the table by
    // pointer and calls into it on every update.
    GodotFontBackend font_backend_;
    weva_font_backend font_table_{};
    uint64_t font_face_ = 0;
    bool use_engine_font_ = true;
    uint64_t atlas_id_ = 0;
};

} // namespace weva_godot
