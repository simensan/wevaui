#pragma once

#include <map>
#include <vector>

#include <godot_cpp/classes/canvas_item.hpp>
#include <godot_cpp/classes/image_texture.hpp>
#include <godot_cpp/classes/input_event.hpp>
#include <godot_cpp/classes/input_event_mouse_button.hpp>
#include <godot_cpp/classes/input_event_key.hpp>
#include <godot_cpp/classes/input_event_mouse_motion.hpp>
#include <godot_cpp/classes/input_event_screen_drag.hpp>
#include <godot_cpp/classes/node2d.hpp>
#include <godot_cpp/classes/system_font.hpp>
#include <godot_cpp/variant/packed_int32_array.hpp>
#include <godot_cpp/variant/packed_vector2_array.hpp>
#include <godot_cpp/variant/vector2i.hpp>
#include <godot_cpp/variant/dictionary.hpp>
#include <godot_cpp/variant/callable.hpp>

#include "godot_font.h"
#include "weva_c.h"

// The Godot host: a Node2D that owns a weva document and draws its geometry.
//
// It talks to libweva only through weva_c.h. No Godot type reaches the core and
// no core C++ type reaches Godot — which is the whole point of the ABI, and the
// property that lets the same core serve a Unity host later.

namespace weva_godot {

// The system faces behind the theme font, for the symbols and emoji it lacks
// (★, ⚔, 🛡). Shared by every document: a SystemFont's TextServer RIDs are not
// its alone, so per-document copies invalidated each other's fonts as
// documents came and went. Released at module shutdown.
const std::vector<godot::Ref<godot::SystemFont>>& shared_symbol_fonts();
void release_shared_symbol_fonts();

class WevaDocument : public godot::Node2D {
    GDCLASS(WevaDocument, godot::Node2D)

public:
    WevaDocument();
    ~WevaDocument() override;

    void _ready() override;
    void _draw() override;
    void _input(const godot::Ref<godot::InputEvent>& event) override;
    void _process(double delta) override;
    void _notification(int what);

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

    // ---- Interaction ---------------------------------------------------
    //
    // The engine matches :hover, :active and :focus; what it cannot know is
    // where the pointer is. With `interactive` on, the node reads its own
    // input and tells the document, which is all those rules need.
    void set_interactive(bool on);
    bool get_interactive() const { return interactive_; }

    // Stops the clock. Transitions and @keyframes hold where they are, which
    // a game wants when it pauses and a CAPTURE requires: comparing two
    // backends means comparing them at the same instant, and a renderer that
    // waits two frames for the viewport to be readable has otherwise let the
    // animation run on while the other side stayed at zero.
    void set_paused(bool on) { paused_ = on; }
    bool get_paused() const { return paused_; }

    // The element under a point, as a selector-free handle a script can pass
    // back. Returns an empty string when the point is over nothing named.
    godot::String element_id_at(const godot::Vector2& point);

    // Focus, by selector; an empty selector drops it.
    bool set_focus(const godot::String& selector);

    // ---- Data binding ---------------------------------------------------
    //
    // What a script needs to drive a document: change what it says, change how
    // it looks, and hear when it is used. Everything is addressed by SELECTOR,
    // because that is the name a script and a stylesheet already share.
    bool set_element_text(const godot::String& selector, const godot::String& text);
    godot::String get_element_text(const godot::String& selector);
    bool add_element_class(const godot::String& selector, const godot::String& name);
    bool remove_element_class(const godot::String& selector, const godot::String& name);
    bool toggle_element_class(const godot::String& selector, const godot::String& name,
                              bool on);
    bool has_element(const godot::String& selector);

    // A form control's value. A checkbox reports "on" or "", which is what a
    // form submission would carry, and takes the same on the way in.
    godot::String get_element_value(const godot::String& selector);
    bool set_element_value(const godot::String& selector, const godot::String& value);

    // Drives the pointer directly, for a host routing its own input -- a
    // gamepad cursor, a touch surface, a test. `buttons` is a bitmask; the
    // primary button is bit 0 and is what makes an element :active.
    void set_pointer(const godot::Vector2& point, int buttons);
    void clear_pointer();

    // Scrolling. `scroll_at` is what a wheel does -- it finds the innermost
    // scroll container under the point -- while the rest name one directly.
    bool scroll_at(const godot::Vector2& point, const godot::Vector2& delta);
    bool scroll_element(const godot::String& selector, const godot::Vector2& delta);
    void set_element_scroll(const godot::String& selector, const godot::Vector2& offset);
    godot::Vector2 get_element_scroll(const godot::String& selector);
    godot::Vector2 get_element_scroll_max(const godot::String& selector);
    bool scroll_into_view(const godot::String& selector);

    // Building the document from data. Rows are addressed the way CSS
    // addresses them, so a script that can style a list can also fill it.
    bool set_element_html(const godot::String& selector, const godot::String& html);
    bool append_html(const godot::String& selector, const godot::String& html);
    bool remove_element(const godot::String& selector);
    int count_elements(const godot::String& selector);

    // Data binding: `{{ path }}` in the markup, filled from a Dictionary, an
    // Object's properties, or anything a Callable can look up.
    godot::String get_element_attribute(const godot::String& selector,
                                        const godot::String& name);
    void set_data(const godot::Dictionary& data);
    godot::Dictionary get_data() const;
    void set_data_source(const godot::Callable& resolver);
    int refresh_bindings();

    // Resolves one path against whatever source is set. Public because the C
    // callback has to reach it.
    bool resolve_binding(const godot::String& path, godot::String* out) const;

    // For a host that routes input itself: a controller mapped onto the
    // document, or a scene that decides who gets the keyboard.
    bool send_key(int keycode, bool pressed = true, bool shift = false, bool ctrl = false);
    void send_text(const godot::String& text);

    // Selection. The document does the selecting; these are the parts a host
    // has to drive -- Ctrl+A, which the key enum cannot express, and the
    // clipboard, which belongs to the platform.
    bool select_all();
    bool select_word_at(const godot::Vector2& point);

    // Dropdowns, for a host routing its own input.
    bool open_select(const godot::String& selector);
    void close_select();
    godot::String get_open_select();
    godot::String get_selected_text();
    bool set_element_selection(const godot::String& selector, int start, int end);
    godot::Vector2i get_element_selection(const godot::String& selector);

    // Moves focus in tab order and returns the id that now has it, or "" when
    // the document has nothing focusable in it.
    godot::String focus_next(bool backwards);

    // Runs cascade, layout and paint now, rather than waiting for the frame.
    // `dt` advances transitions and animations by that many seconds -- a game
    // stepping a paused UI, or a test that wants to see a transition partway
    // rather than wait for real frames to pass.
    void update_document(double dt = 0.0);

    // Whether to draw with the engine's own font. Turning it off falls back to
    // the core's built-in 5x7 face, which is what the backend comparison needs:
    // holding the font fixed is the only way a pixel difference between this
    // host and the reference rasteriser means anything.
    void set_use_engine_font(bool use);
    bool get_use_engine_font() const { return use_engine_font_; }

    // Whether to evaluate rounded rectangles in a shader instead of uploading
    // the core's tessellation.
    //
    // OFF by default, on the measurement rather than the theory. Per-pixel
    // evaluation does give a better edge -- 39 distinct shades along a circle
    // against the tessellation's 30 -- but two things stop that being a win
    // here. A material is per canvas item in Godot, so every shape drawn this
    // way needs its own item and stops batching with its neighbours; and the
    // core only offers the shape for a SOLID fill with no border, which on the
    // sample corpus is 5 draws out of 191 on advanced-dashboard and 0 out of 75
    // on todo. Real boxes have borders and gradients.
    //
    // Kept because it is the seam the argument needs: making it pay means
    // describing the border and the gradient too, and passing the parameters
    // per vertex so one material can serve every shape.
    void set_use_sdf_rects(bool use);
    bool get_use_sdf_rects() const { return use_sdf_rects_; }

    // How far the document reaches, which is not the viewport: half the sample
    // corpus lays out taller than the box it is given. A host scrolling a
    // document needs this to know whether there is anywhere to scroll to.
    godot::Vector2 get_content_size();

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
    // `dt` advances transitions; zero means "only if something is dirty".
    void ensure_updated(double dt = 0);

    // Adds one published draw's triangles to a canvas item.
    void add_triangles(const godot::RID& item, const weva_draw& d);
    // Draws a document that contains at least one backdrop-filter. Godot copies
    // to the back buffer ONCE per canvas item, before that item's commands, so
    // interleaving "copy what is behind me" with geometry means splitting the
    // draw list across items in z order. Documents without one keep the single
    // item, which is every sample but two.
    void draw_layered(const weva_draw* draws, size_t count);
    godot::RID backdrop_material();
    // Draws a rounded rect by EVALUATING it per pixel rather than uploading its
    // tessellation: exact coverage instead of the core's half-pixel ramp, off
    // two triangles instead of a fan and a ring. Returns false when the shape
    // is not one this path handles, and the caller uploads the triangles.
    bool draw_rounded_rect(const godot::RID& item, const weva_draw& d);
    godot::RID rounded_rect_material(const weva_rounded_rect& s);
    void release_layers();

    weva_document_t doc_ = nullptr;
    godot::String html_;
    godot::String css_;
    godot::Vector2 size_{0, 0};
    bool dirty_ = true;
    godot::Dictionary data_;
    godot::Callable data_source_;
    bool interactive_ = true;
    bool paused_ = false;
    // The last position handed to the document, so a move that does not change
    // the element still costs nothing: the document already skips an update
    // that changes no style, and this skips the call.
    godot::Vector2 pointer_{-1, -1};
    uint32_t buttons_ = 0;
    // Time owed to the document. An update consumes it; a frame in which
    // nothing is moving hands over nothing and costs nothing.
    double pending_dt_ = 0;

    // Drains the document's event queue into signals.
    void pump_events();
    godot::String id_of(uint32_t element);
    // Resolves a selector to a handle, updating the document first so the
    // answer reflects what a script has just changed.
    uint32_t resolve(const godot::String& selector);
    // The atlas texture, rebuilt when the document publishes a new one. Held
    // so it outlives the draw call that references it.
    // Every texture the document published, by the id its draws name: the
    // glyph atlas and the rasterized background layers alike.
    std::map<uint64_t, godot::Ref<godot::ImageTexture>> textures_;
    // The font backend must outlive the document: the core holds the table by
    // pointer and calls into it on every update.
    GodotFontBackend font_backend_;
    weva_font_backend font_table_{};
    uint64_t font_face_ = 0;
    bool use_engine_font_ = true;

    // Only allocated for a document that uses backdrop-filter.
    std::vector<godot::RID> layer_items_;
    std::vector<godot::RID> layer_materials_;
    godot::RID backdrop_shader_;
    // The SDF path. Materials are pooled per frame, like the backdrop ones.
    godot::RID rounded_shader_;
    std::vector<godot::RID> rounded_materials_;
    size_t rounded_used_ = 0;
    bool use_sdf_rects_ = false;
};

} // namespace weva_godot
