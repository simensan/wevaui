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
#include <godot_cpp/classes/control.hpp>
#include <godot_cpp/classes/system_font.hpp>
#include <godot_cpp/variant/packed_int32_array.hpp>
#include <godot_cpp/variant/packed_color_array.hpp>
#include <godot_cpp/variant/packed_byte_array.hpp>
#include <godot_cpp/variant/packed_string_array.hpp>
#include <godot_cpp/variant/packed_vector2_array.hpp>
#include <godot_cpp/variant/vector2i.hpp>
#include <godot_cpp/variant/dictionary.hpp>
#include <godot_cpp/variant/callable.hpp>
#include <godot_cpp/core/object_id.hpp>

#include "godot_font.h"
#include "weva_c.h"

// The Godot host: a Control that owns a weva document and draws its geometry.
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

class WevaDocument : public godot::Control {
    GDCLASS(WevaDocument, godot::Control)

public:
    // Main thread: loads at most one shared compatibility font. True when done.
    static bool warmup_fonts_step();
    WevaDocument();
    ~WevaDocument() override;

    void _ready() override;
    void _draw() override;
    void _gui_input(const godot::Ref<godot::InputEvent>& event) override;
    bool _has_point(const godot::Vector2& point) const override;
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

    // Alias for Control.size, the viewport the document lays out against.
    // Unsized documents use full-rect anchors when entering the scene.
    void set_document_size(const godot::Vector2& size);
    godot::Vector2 get_document_size() const { return size_; }

    // ---- Interaction ---------------------------------------------------
    //
    // The engine matches :hover, :active and :focus; what it cannot know is
    // where the pointer is. With `interactive` on, Godot routes GUI input
    // through this Control's focus, hit region, visibility and stacking.
    void set_interactive(bool on);
    bool get_interactive() const { return interactive_; }

    // A controller has no keys. With this on (the default), joypad events
    // that the project's ui_* actions describe become what a browser would
    // do with a keyboard: the pad and stick move HTML focus by direction,
    // ui_accept activates, ui_cancel dismisses. The Control must hold Godot
    // focus, as for typing: grab_focus() when the screen opens.
    void set_gamepad_navigation(bool on) { gamepad_navigation_ = on; }
    bool get_gamepad_navigation() const { return gamepad_navigation_; }
    // With this on, a joypad press or stick push while no Control holds
    // focus wakes this document on its first control and is spent doing so.
    // Off by default: a HUD that is always on screen must not take the
    // movement stick from the game. Turn it on for a menu screen, or call
    // grab_focus() when the screen opens.
    void set_gamepad_wake(bool on) { gamepad_wake_ = on; }
    bool get_gamepad_wake() const { return gamepad_wake_; }
    // With this on (the default), a pad accept on a focused text field emits
    // text_entry_requested(id) instead of Enter, so something can offer a
    // way to type: WevaView shows its on-screen keyboard, or a game answers
    // with a platform keyboard. Off, accept is Enter as on a keyboard.
    void set_gamepad_text_entry(bool on) { gamepad_text_entry_ = on; }
    bool get_gamepad_text_entry() const { return gamepad_text_entry_; }
    // Keeps the HTML focus (and its caret) while another Control holds Godot
    // focus, for a companion such as an on-screen keyboard that types into
    // the focused field through send_text/send_key. Normally losing Godot
    // focus clears the HTML focus.
    void set_retain_html_focus(bool on) { retain_html_focus_ = on; }
    bool get_retain_html_focus() const { return retain_html_focus_; }

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
    // The id of what has the focus, or "". The focus moves without the host
    // asking -- Tab walks it, a click moves it, a <label> moves it to the
    // control it names -- so a host mirroring it has to be able to read it.
    godot::String get_focused_id();

    // The `data-each` row an element is in, as {"index": int, "key": String},
    // or an empty Dictionary when it is not in one.
    godot::Dictionary get_row(const godot::String& selector);

    // `title="..."` draws as a tooltip after the pointer rests on an element.
    // Negative turns that off, for a game that presents its own.
    void set_tooltip_delay(double seconds);
    double get_tooltip_delay() const;

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
    bool reset_form(const godot::String& selector);

    // Drives the pointer directly, for a host routing its own input -- a
    // gamepad cursor, a touch surface, a test. `buttons` is a bitmask; the
    // primary button is bit 0 and is what makes an element :active.
    void set_pointer(const godot::Vector2& point, int buttons, int modifiers = 0);
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
    // <dialog>. `show_modal` is the one that dims what is behind it: it joins
    // the top layer and gets a ::backdrop. Escape is the host's to bind --
    // which key cancels a dialog is a platform question.
    bool show_dialog(const godot::String& selector);
    bool show_modal_dialog(const godot::String& selector);
    bool close_dialog(const godot::String& selector, const godot::Variant& result = godot::Variant());
    bool request_close_dialog(const godot::String& selector, const godot::Variant& result = godot::Variant());
    godot::String get_dialog_return_value(const godot::String& selector);
    bool set_custom_validity(const godot::String& selector, const godot::String& message);
    godot::String get_custom_validity(const godot::String& selector);
    godot::Dictionary get_element_validity(const godot::String& selector);
    bool check_validity(const godot::String& selector);
    bool report_validity(const godot::String& selector);
    bool run_validity(const godot::String& selector, bool report);
    bool set_dialog_return_value(const godot::String& selector, const godot::String& value);
    bool prevent_default();

    // Popovers. A `<button popovertarget="menu">` works its own with no script
    // at all, an `auto` one closes on a click outside it or on Escape -- these
    // are for opening a menu from something other than a click.
    bool show_popover(const godot::String& selector);
    bool hide_popover(const godot::String& selector);
    bool toggle_popover(const godot::String& selector);

    // HTML's boolean attributes carry no value, so reading one back cannot
    // say whether it is set. This can.
    bool has_element_attribute(const godot::String& selector, const godot::String& name);
    godot::String get_element_attribute(const godot::String& selector,
                                        const godot::String& name);
    void set_data(const godot::Dictionary& data);
    godot::Dictionary get_data() const;
    void set_data_source(const godot::Callable& resolver);
    int refresh_bindings();
    // Two-way binding. `data-model="Path"` on a control makes the traffic go
    // both ways: the data lands in the control, and what the user does to the
    // control lands back in the data.
    int apply_models();
    bool write_data_path(const godot::String& path, const godot::String& text);
    void write_back_model(uint32_t element);
    void write_back_form_models(uint32_t form);

    // `on-click="OnStart"` calls OnStart on the controller. The markup names
    // the method; the script supplies the object.
    void set_controller(godot::Object* controller);
    godot::Object* get_controller() const;

    // Resolves one path against whatever source is set. Public because the C
    // callback has to reach it.
    bool resolve_binding(const godot::String& path, godot::String* out) const;
    int resolve_binding_count(const godot::String& path) const;

    // For a host that routes input itself: a controller mapped onto the
    // document, or a scene that decides who gets the keyboard.
    bool send_key(int keycode, bool pressed = true, bool shift = false, bool ctrl = false);
    void send_text(const godot::String& text);
    bool paste_text(const godot::String& text);

    // Explicit IME routing uses Godot String character offsets (start/end).
    bool set_composition(const godot::String& text, int start, int end);
    bool commit_composition(const godot::String& text);
    bool finish_composition();
    bool has_composition() const;
    godot::Rect2 get_caret_bounds();
    godot::Rect2 get_caret_window_bounds();

    // Selection. The document does the selecting; these are the parts a host
    // has to drive -- Ctrl+A, which the key enum cannot express, and the
    // clipboard, which belongs to the platform.
    bool select_all();

    // Ctrl+Z is the host's to bind: the document's key enum has no letters,
    // and which chord means undo is a platform question.
    bool undo();
    bool redo();
    bool select_word_at(const godot::Vector2& point);

    // Dropdowns, for a host routing its own input.
    bool open_select(const godot::String& selector);
    void close_select();
    godot::String get_open_select();
    godot::String get_selected_text();
    bool set_element_selection(const godot::String& selector, int start, int end);
    bool set_element_selection_without_focus(const godot::String& selector, int start, int end);
    godot::Vector2i get_element_selection(const godot::String& selector);

    // Moves focus in tab order and returns the id that now has it, or "" when
    // the document has nothing focusable in it.
    godot::String focus_next(bool backwards);
    // Focus in a DIRECTION rather than along the tab order, which is what a
    // gamepad stick or a D-pad asks for.
    godot::String focus_move(const godot::Vector2& direction);

    // Runs cascade, layout and paint now, rather than waiting for the frame.
    // `dt` advances transitions and animations by that many seconds -- a game
    // stepping a paused UI, or a test that wants to see a transition partway
    // rather than wait for real frames to pass.
    void update_document(double dt = 0.0);

    // Whether to draw with the engine's own font. Turning it off falls back to
    // the core's built-in 5x7 face, which is what the backend comparison needs:
    // holding the font fixed is the only way a pixel difference between this
    // host and the reference rasteriser means anything.
    bool register_font_family(const godot::String& family, const godot::Ref<godot::Font>& font);
    // A real bold or italic file for a registered family: weight is the CSS
    // number (600-799 bold, 800 and above black), italic the slant. Text at
    // that weight or slant draws the file instead of a synthesized variant;
    // the nearest file serves what is not exact. A null font removes it.
    bool register_font_face(const godot::String& family, const godot::Ref<godot::Font>& font, int weight, bool italic);
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

    // How long the last engine update took, in milliseconds: the layout and
    // paint the core does inside weva_document_update, and nothing else. It
    // does NOT include Godot's own draw of the resulting triangles, which is
    // what the frame time covers -- the two answer different questions and a
    // stats window wants both.
    //
    // Zero until an update has actually run. A document that is not dirty and
    // has no animation does no work, so a static page reads zero rather than
    // some small idle number, which is the honest answer.
    double get_last_update_ms() const;
    double get_total_core_update_ms() const;
    int64_t get_core_update_count() const;
    // False when the engine gave us no usable face and the core's stub font is
    // still in play — worth being able to assert on, since text that renders
    // with the 5x7 stub looks like a font choice rather than a failure.
    bool has_engine_font() const { return font_face_ != 0; }

protected:
    static void _bind_methods();

private:
    void sync_control_size();
    void sync_gui_focus();
    void sync_ime();
    void close_ime();
    void receive_ime_update();
    void flush_ime_commit();
    void schedule_ime_end();
    int32_t ime_window_ = -1;
    uint64_t ime_draw_serial_ = UINT64_MAX;
    uint32_t ime_target_ = WEVA_ELEMENT_NONE;
    uint32_t ime_end_target_ = WEVA_ELEMENT_NONE;
    bool ime_end_pending_ = false;
    godot::String ime_commit_text_;
    godot::Vector2i ime_position_;
    uint64_t caret_bounds_serial_ = UINT64_MAX;
    godot::Rect2 caret_bounds_;
    // Idempotent: adopts the engine's fallback face the first time it can, and
    // is called from both the constructor and _ready because ThemeDB is not
    // guaranteed to be up at construction.
    void ensure_font_backend();

protected:

private:
    // `dt` advances transitions; zero means "only if something is dirty".
    void ensure_updated(double dt = 0, double input_dt = -1, bool geometry_only = false);

    // Adds one published draw's triangles to a canvas item.
    void add_triangles(const godot::RID& item, const weva_draw* draws, size_t count = 1,
                       const uint64_t* versions = nullptr, bool retain = false);
    void release_retained_batches(size_t from = 0);
    void sync_retained_state();
    void sync_retained_uniforms();
    godot::RID retained_material_;
    bool retained_parent_material_ = false;
    std::vector<std::pair<godot::StringName, godot::Variant>> retained_uniforms_;
    bool retained_sync_connected_ = false;
    godot::Callable retained_sync_callback_;
    godot::Color retained_self_modulate_{1, 1, 1, 1};
    uint32_t retained_light_mask_ = 1;
    struct PackedBatch {
        godot::RID retained_item, retained_texture;
        std::vector<uint64_t> versions;
        godot::PackedVector2Array points, uvs;
        godot::PackedColorArray colors;
        godot::PackedInt32Array indices;
    };
    std::vector<PackedBatch> packed_batches_;
    size_t packed_used_ = 0;
    // Draws a document that contains at least one backdrop-filter. Godot copies
    // to the back buffer ONCE per canvas item, before that item's commands, so
    // interleaving "copy what is behind me" with geometry means splitting the
    // draw list across items in z order. Documents without one keep the single
    // item, which is every sample but two.
    void draw_layered(const weva_draw* draws, size_t count, const uint64_t* versions);
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
    bool paint_pending_ = false;
    double last_update_ms_ = 0;
    double total_core_update_ms_ = 0;
    int64_t core_update_count_ = 0;
    uint64_t consumed_interaction_version_ = 0;
    uint64_t last_input_tick_usec_ = 0;
    godot::Dictionary data_;
    bool bindings_active_ = false;
    // Cache parsed paths, never data values: shared Dictionaries and Callable
    // sources must still be read on each refresh. Bound storage across reloads
    // and list churn, including applications that generate arbitrary paths.
    mutable std::map<godot::String, godot::PackedStringArray> binding_paths_;
    const godot::PackedStringArray& binding_parts(const godot::String& path) const;
    // The three popover calls differ only in which ABI entry they take.
    bool run_popover(const godot::String& selector,
                     weva_status (*fn)(weva_document_t, weva_element_t));

    double tooltip_delay_ = 0.6;
    godot::Callable data_source_;
    godot::ObjectID controller_;
    bool interactive_ = true;
    bool gamepad_navigation_ = true;
    bool gamepad_wake_ = false;
    bool gamepad_text_entry_ = true;
    bool retain_html_focus_ = false;
    bool navigation_action(const godot::Ref<godot::InputEvent>& event);
    bool navigate_direction(int index, const godot::String& tag, const godot::String& type);
    void repeat_navigation(uint64_t now_usec);
    godot::String focused_tag(godot::String* type) const;
    // A direction the pad is still holding repeats like a held key: the
    // index into the direction table, how to poll it, and when it fires next.
    int held_direction_ = -1;
    // The joypad event that woke an unfocused document: it is spent on
    // taking focus and must not also navigate once it reaches _gui_input.
    godot::Ref<godot::InputEvent> wake_event_;
    bool held_by_action_ = false;
    int held_device_ = 0;
    uint64_t repeat_at_usec_ = 0;
    bool paused_ = false;
    // The last position handed to the document, so a move that does not change
    // the element still costs nothing: the document already skips an update
    // that changes no style, and this skips the call.
    godot::Vector2 pointer_{-1, -1};
    uint32_t buttons_ = 0;
    uint32_t pointer_modifiers_ = 0;
    uint64_t outside_dismiss_version_ = 0;
    bool pointer_focus_entry_ = false;
    void dismiss_outside_transients();
    // Time owed to the document. An update consumes it; a frame in which
    // nothing is moving hands over nothing and costs nothing.
    double pending_dt_ = 0;

    // Drains the document's event queue into signals.
    void pump_events();
    bool pumping_events_ = false;
    godot::String id_of(uint32_t element);
    // By HANDLE, not by selector. A row a `data-each` produced has no id, so
    // "#" + id_of(e) finds nothing and reads back an empty value.
    godot::String value_of(uint32_t element);
    godot::String attribute_of(uint32_t element, const char* name);
    godot::String model_path_of(uint32_t element);

public:
    // Where a relative `url(...)` in the CSS resolves from. Set it to the
    // directory the markup came from and `url(icons/gem.png)` finds the file
    // beside it, the way a browser resolves one.
    void set_base_path(const godot::String& path);
    godot::String get_base_path() const;
    // Every asset the document asked for and could not load. The answer to
    // "why is my icon not showing", which is otherwise indistinguishable from
    // a page that simply has no icon.
    godot::PackedStringArray get_missing_assets();
    godot::PackedStringArray get_css_diagnostics() const;

    // One inline declaration, leaving the rest of the element's `style` alone.
    // An empty value removes it, handing the property back to the stylesheet.
    bool set_element_style(const godot::String& selector, const godot::String& property,
                           const godot::String& value);
    // What the element sets INLINE, which is not what the cascade decided --
    // get_computed_style answers that.
    godot::String get_element_style(const godot::String& selector,
                                    const godot::String& property);

    // An element's box in the same coordinates a sibling Node2D lives in, so
    // something can be parented over it.
    godot::Rect2 get_element_screen_rect(const godot::String& selector);

    // Which repeated row holds the focus, by index and data-key.
    //
    // get_focused_id answers with an ATTRIBUTE, and a row a data-each
    // produced has no id -- the template writes one element and the data
    // decides how many there are. So in the one place a data-driven UI most
    // needs to know what is focused, the id is always empty.
    godot::Dictionary get_focused_row();

private:
    godot::String base_path_;
    // One image in transit across the ABI's size/read pair. Released as soon
    // as the core owns the bytes; its ImageStore owns the decoded cache.
    godot::String asset_read_path_;
    godot::PackedByteArray asset_read_bytes_;
    // Handed to the core so an asset is read through Godot: res:// resolves,
    // and an exported .pck has no files for the core to open itself.
    static size_t read_asset(void* user_data, const char* path, uint8_t* buffer, size_t capacity);
    std::vector<uint32_t> matches(const godot::String& selector);

public:
    // What the CASCADE settled on, which a script has no other way to ask.
    godot::String get_computed_style(const godot::String& selector,
                                     const godot::String& property);
    // Every match, not just the first. The ABI has always had query_all; the
    // node only ever exposed the first hit, so a script iterating a list had
    // to count it and then build one :nth-of-type selector per row.
    godot::PackedStringArray query_all_text(const godot::String& selector);
    godot::Array query_all_bounds(const godot::String& selector);
    godot::PackedStringArray query_all_ids(const godot::String& selector);

private:
    // Set while a model is being pushed into its control, so the change that
    // causes does not bounce straight back into the data.
    bool applying_models_ = false;
    // Resolves a selector to a handle, updating the document first so the
    // answer reflects what a script has just changed.
    uint32_t resolve(const godot::String& selector, bool flush = true);
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
    godot::Ref<godot::Font> theme_font_;
    godot::Callable theme_font_changed_;
    std::map<godot::String, godot::Ref<godot::Font>> family_fonts_;
    std::vector<godot::Ref<godot::Font>> retired_family_fonts_;
    // Real weight/italic files per family key, by (strength 1|2, italic).
    std::map<godot::String, std::map<std::pair<int, bool>, godot::Ref<godot::Font>>> family_variants_;
    // Families registered from the stylesheet's `@font-face` rules, keyed like
    // family_fonts_, with the resolved source each was loaded from. A family
    // the game registered itself is never replaced by CSS.
    struct CssFontFace {
        godot::String path;
        godot::Ref<godot::Font> font;
        // Extra weight/italic files the stylesheet declared for the family.
        std::map<std::pair<int, bool>, std::pair<godot::String, godot::Ref<godot::Font>>> variants;
    };
    std::map<godot::String, CssFontFace> css_font_faces_;
    void sync_css_font_faces();
    godot::Callable family_font_changed_;
    void family_font_resource_changed();
    void disconnect_family_fonts();
    bool theme_font_dirty_ = true;
    bool font_resource_dirty_ = false;
    void font_resource_changed();
    void disconnect_theme_font();

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
