#ifndef WEVA_C_H
#define WEVA_C_H

/*
 * The C ABI: the seam a Godot GDExtension binds through, and the one a Unity
 * host would bind through later.
 *
 * This is the narrowest part of the system and the hardest to change once
 * anything depends on it, so the rules are strict:
 *
 *   - C linkage, POD only. No STL types, no C++ classes, no exceptions.
 *   - Opaque handles, never a struct a host can reach into.
 *   - Caller-allocates or explicit free. Every allocating call has a paired
 *     release; nothing is handed back with implicit lifetime.
 *   - Versioned. weva_abi_version() is checked by every host at load.
 *   - Additive changes only, once the first host ships against it.
 *
 * A host supplies its renderer and font backend through function-pointer
 * tables that mirror the C++ interfaces. No core entry point takes or returns
 * a host type.
 */

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Bumped on any incompatible change. A host that sees a different major value
 * must refuse to load rather than guess. */
#define WEVA_ABI_VERSION_MAJOR 0
#define WEVA_ABI_VERSION_MINOR 1

uint32_t weva_abi_version(void);

/* Mirrors weva::Status. Zero is success, so `if (weva_...)` reads as failure. */
typedef enum weva_status {
    WEVA_OK = 0,
    WEVA_ERR_INVALID_ARGUMENT = 1,
    WEVA_ERR_PARSE = 2,
    WEVA_ERR_NOT_FOUND = 3,
    WEVA_ERR_UNSUPPORTED = 4,
    WEVA_ERR_INTERNAL = 5
} weva_status;

typedef struct weva_document* weva_document_t;
/* An element handle is an index into the document, not a pointer: the DOM is
 * refcounted and may move between calls, and a stale index is detectable
 * where a stale pointer is not. */
typedef uint32_t weva_element_t;
#define WEVA_ELEMENT_NONE ((weva_element_t)0xFFFFFFFFu)

typedef struct weva_config {
    int viewport_width;
    int viewport_height;
    double device_pixel_ratio;
    /* Zero means the built-in default (16). */
    double root_font_size;
    /* Non-zero loads the built-in user-agent stylesheet, which is what makes
     * `display` anything other than `inline`. A host almost always wants it. */
    int use_user_agent_stylesheet;
} weva_config;

/* One draw the host must issue. POD by construction: the arrays point into
 * memory the document owns until the next update, so a host copies or uploads
 * them before calling anything else. */
typedef struct weva_vertex {
    float x, y;
    float r, g, b, a;
    float u, v;
} weva_vertex;

/* What a draw asks the host to do. A host that only handles GEOMETRY still
 * renders: skipping the others loses an effect, not the page. */
typedef enum weva_draw_kind {
    WEVA_DRAW_GEOMETRY = 0,
    /* `backdrop-filter`. The vertices and indices are the SHAPE to filter
     * inside rather than geometry to paint, and `backdrop` says what to do to
     * what is already in the target there. See RenderInterface::filter_backdrop
     * for why this cannot be expressed as triangles. */
    WEVA_DRAW_BACKDROP_FILTER = 1,
    /* A rounded rectangle, described as well as tessellated. The vertices are
     * still a complete tessellation, so a host that ignores this kind draws
     * the shape correctly; one that can evaluate a rounded box per pixel uses
     * `rounded_rect` instead and gets exact coverage off two triangles. */
    WEVA_DRAW_ROUNDED_RECT = 2
} weva_draw_kind;

/* Corner radii run clockwise from top-left, x then y. */
typedef struct weva_rounded_rect {
    double x, y, width, height;
    double radii[4][2];
    float r, g, b, a;
} weva_rounded_rect;

/* The colour functions of a filter list composed into one affine transform in
 * sRGB, so a host never parses CSS. Row-major 3x3, then the offsets. */
typedef struct weva_backdrop_effect {
    /* A CSS blur RADIUS, not a sigma; the sigma is half of it. */
    double blur_radius;
    float color_matrix[9];
    float color_offset[3];
    float color_alpha;
} weva_backdrop_effect;

typedef struct weva_draw {
    const weva_vertex* vertices;
    size_t vertex_count;
    const uint32_t* indices;
    size_t index_count;
    /* Zero when the draw is untextured. */
    uint64_t texture_id;
    /* Set when this draw is clipped; all four are zero otherwise. */
    int32_t scissor_x, scissor_y, scissor_width, scissor_height;
    int32_t has_scissor;
    /* One of weva_draw_kind. A host SHOULD branch on it; one that does not is
     * safe anyway, because a non-geometry draw carries a transparent shape and
     * uploading it paints nothing. (It is worth saying because getting this
     * wrong is not subtle: with an opaque shape, a host that ignored the field
     * painted twenty white rectangles over the glass sample.) */
    int32_t kind;
    /* Only meaningful when kind is WEVA_DRAW_BACKDROP_FILTER. */
    weva_backdrop_effect backdrop;
    /* Only meaningful when kind is WEVA_DRAW_ROUNDED_RECT. */
    weva_rounded_rect rounded_rect;
} weva_draw;

/* A texture the host must create before issuing the draws that reference it.
 * Pixels are 8-bit RGBA and live until the next update. */
typedef struct weva_texture {
    uint64_t id;
    const uint8_t* rgba;
    int32_t width, height;
} weva_texture;

/* ---- Host-supplied backends -------------------------------------------
 *
 * A host implements one or both of these tables and registers it before the
 * first update. Each mirrors the corresponding C++ interface, with `user_data`
 * carried through every call so a host can hold its own state without a global.
 *
 * A null table, or a null function within one, falls back to the built-in
 * stub for that operation — so a host can adopt these incrementally and a
 * partially implemented backend degrades rather than crashes.
 */

typedef struct weva_render_backend {
    void* user_data;

    /* Required for anything to appear. Returns a handle the host chooses; zero
     * is reserved for "no geometry". */
    uint64_t (*compile_geometry)(void* user_data, const weva_vertex* vertices,
                                 size_t vertex_count, const uint32_t* indices,
                                 size_t index_count);
    void (*render_geometry)(void* user_data, uint64_t geometry, float translate_x,
                            float translate_y, uint64_t texture);
    void (*release_geometry)(void* user_data, uint64_t geometry);

    /* Textures. `generate_texture` receives 8-bit RGBA. */
    uint64_t (*generate_texture)(void* user_data, const uint8_t* rgba, int32_t width,
                                 int32_t height);
    void (*release_texture)(void* user_data, uint64_t texture);
    /* Returns zero when the path cannot be loaded; the draw then falls back to
     * its vertex colours rather than vanishing. */
    uint64_t (*load_texture)(void* user_data, const char* path, int32_t* out_width,
                             int32_t* out_height);

    /* `enable` of zero disables clipping and the rect is ignored. */
    void (*set_scissor)(void* user_data, int32_t enable, int32_t x, int32_t y, int32_t width,
                        int32_t height);
} weva_render_backend;

typedef struct weva_glyph_bitmap {
    /* One byte of coverage per pixel. Owned by the host; copied before the
     * call returns. */
    const uint8_t* alpha;
    int32_t width, height;
    /* Optional, for a colour glyph: straight-alpha RGBA8, four bytes per
     * pixel, same size. Null (the zero-initialised default) means the glyph
     * is coverage only and draws in the text colour. */
    const uint8_t* rgba;
} weva_glyph_bitmap;

typedef struct weva_font_backend {
    void* user_data;

    /* Returns zero on failure. `index` selects a face within a collection. */
    uint64_t (*load_face)(void* user_data, const uint8_t* data, size_t length, int32_t index);
    /* All of these return non-zero on success. */
    int32_t (*face_metrics)(void* user_data, uint64_t face, double px, double* out_ascent,
                            double* out_descent, double* out_line_gap);
    int32_t (*glyph_index)(void* user_data, uint64_t face, uint32_t codepoint,
                           uint32_t* out_glyph);
    int32_t (*glyph_metrics)(void* user_data, uint64_t face, uint32_t glyph, double px,
                             double* out_advance, double* out_bearing_x, double* out_bearing_y,
                             int32_t* out_width, int32_t* out_height);
    int32_t (*rasterize)(void* user_data, uint64_t face, uint32_t glyph, double px,
                         weva_glyph_bitmap* out);
    /* Writes at most `capacity` glyphs and returns how many the text produced,
     * so a host sizes with one call and fills with a second. */
    size_t (*shape)(void* user_data, uint64_t face, const char* utf8, size_t length, double px,
                    uint32_t* out_glyphs, double* out_advances, uint32_t* out_clusters,
                    size_t capacity);
    /* Optional. The face to use for a CSS font-weight (100-900) and italic
     * flag: a real bold or italic face when the host has one, a synthesized
     * one otherwise. Returning `face` (or leaving this null) means the
     * regular face serves every weight. */
    uint64_t (*variant)(void* user_data, uint64_t face, int32_t weight, int32_t italic);
} weva_font_backend;

/* Both copy the table, so the caller may free it on return. Passing null
 * restores the built-in stub. Registering after a document has been updated
 * takes effect on the NEXT update. */
void weva_document_set_render_backend(weva_document_t doc, const weva_render_backend* backend);
void weva_document_set_font_backend(weva_document_t doc, const weva_font_backend* backend,
                                    uint64_t face);

weva_document_t weva_document_create(const weva_config* config);
void weva_document_destroy(weva_document_t doc);

/* Both take an explicit length so a host is never required to null-terminate.
 * The bytes are copied; the caller may free them on return. */
weva_status weva_document_load_html(weva_document_t doc, const char* html, size_t length);
weva_status weva_document_add_css(weva_document_t doc, const char* css, size_t length);

void weva_document_set_viewport(weva_document_t doc, int width, int height);

/* How far the laid-out document reaches, which is not the viewport: half of the
 * sample corpus is taller than the box it is laid out in. A host that wants to
 * scroll a document needs this to know whether there is anywhere to scroll to.
 * Never smaller than the viewport. Valid after weva_document_update. */
weva_status weva_document_content_size(weva_document_t doc, double* out_width,
                                       double* out_height);

/* Runs cascade, layout and paint. `dt_seconds` advances transitions; pass 0
 * for a static document.
 *
 * An update that finds nothing changed -- no attribute set, no pointer moved,
 * nothing in flight -- returns having done nothing, and the draws already
 * published stay valid. A host may therefore call this every frame. */
weva_status weva_document_update(weva_document_t doc, double dt_seconds);

/* Whether any transition is still running, so a host knows to keep handing
 * over time and redrawing. False for a document that has settled. */
int weva_document_is_animating(weva_document_t doc);

/* The draw list from the last update. Valid until the next update or destroy,
 * and NOT owned by the caller — this is the one place the "explicit free" rule
 * is relaxed, in exchange for a documented lifetime, because a per-frame copy
 * of the whole display list is exactly the allocation the port exists to
 * remove. */
const weva_draw* weva_document_draws(weva_document_t doc, size_t* out_count);
const weva_texture* weva_document_textures(weva_document_t doc, size_t* out_count);

/* Returns WEVA_ELEMENT_NONE when nothing matches. */
weva_element_t weva_document_query(weva_document_t doc, const char* selector);

/* Geometry of an element's border box, in document coordinates. Returns
 * WEVA_ERR_NOT_FOUND for a stale or unknown handle. */
weva_status weva_element_bounds(weva_document_t doc, weva_element_t element, double* out_x,
                                double* out_y, double* out_width, double* out_height);

/* ---- Events ------------------------------------------------------------
 *
 * What a host reads back after driving the pointer, so a script can act on a
 * click without re-implementing hit testing or press tracking.
 *
 * Events are QUEUED, not called back. A callback across the C boundary would
 * have to run while the document is mid-update, and a handler that mutated the
 * document there would be doing so under the pass that is reading it. Polling
 * puts the host in charge of when that happens.
 */
typedef enum weva_event_kind {
    WEVA_EVENT_NONE = 0,
    WEVA_EVENT_POINTER_DOWN,
    WEVA_EVENT_POINTER_UP,
    /* A press and a release on the same element, which is what a script
     * actually wants and what neither of the two above is on its own. */
    WEVA_EVENT_CLICK,
    WEVA_EVENT_POINTER_ENTER,
    WEVA_EVENT_POINTER_LEAVE,
    /* Keyboard. `key` carries the code, `text` the characters it produced --
     * which are different things: Shift+1 is one key and the text "!", and a
     * dead key produces no text at all. */
    WEVA_EVENT_KEY_DOWN,
    WEVA_EVENT_KEY_UP,
    WEVA_EVENT_TEXT_INPUT,
    /* Focus moved. `target` is the element that now has it, or
     * WEVA_ELEMENT_NONE when it was dropped. */
    WEVA_EVENT_FOCUS,
    WEVA_EVENT_BLUR,
    /* A form control's value changed because the user changed it. `text`
     * carries the new value when it is short enough; read it back with
     * weva_element_value for anything longer. */
    WEVA_EVENT_VALUE_CHANGED
} weva_event_kind;

/* Held modifiers, as a bitmask on weva_event.modifiers. */
typedef enum weva_key_modifier {
    WEVA_MOD_SHIFT = 1u << 0,
    WEVA_MOD_CTRL = 1u << 1,
    WEVA_MOD_ALT = 1u << 2,
    WEVA_MOD_META = 1u << 3
} weva_key_modifier;

/* The keys the engine itself acts on. A host passes its own codes through for
 * everything else; these are the ones with meaning here. */
typedef enum weva_key {
    WEVA_KEY_OTHER = 0,
    WEVA_KEY_TAB = 1,
    WEVA_KEY_ENTER = 2,
    WEVA_KEY_SPACE = 3,
    WEVA_KEY_ESCAPE = 4,
    WEVA_KEY_BACKSPACE = 5,
    WEVA_KEY_DELETE = 6,
    WEVA_KEY_LEFT = 7,
    WEVA_KEY_RIGHT = 8,
    WEVA_KEY_UP = 9,
    WEVA_KEY_DOWN = 10,
    WEVA_KEY_HOME = 11,
    WEVA_KEY_END = 12
} weva_key;

typedef struct weva_event {
    int32_t kind;              /* one of weva_event_kind */
    weva_element_t target;     /* the element it happened on */
    double x, y;               /* document coordinates */
    uint32_t buttons;          /* buttons held at the time */
    int32_t key;               /* one of weva_key, for the key events */
    uint32_t modifiers;        /* weva_key_modifier bitmask */
    /* The text a key produced, UTF-8 and null-terminated. Inline rather than a
     * pointer so an event stays copyable and outlives nothing. */
    char text[8];
} weva_event;

/* Takes the oldest queued event, returning 0 when the queue is empty. A host
 * pumps this in a loop after each update:
 *
 *     weva_event e;
 *     while (weva_document_poll_event(doc, &e)) { ... }
 *
 * The queue is bounded; if a host never pumps it, the oldest events are
 * dropped rather than the memory growing without limit. */
int weva_document_poll_event(weva_document_t doc, weva_event* out);

/* Whether an element is `target` or a descendant of it. What a host needs to
 * answer "was this click inside my panel?" without walking the tree itself. */
int weva_element_contains(weva_document_t doc, weva_element_t ancestor,
                          weva_element_t descendant);

/* ---- Interaction ------------------------------------------------------
 *
 * What drives :hover, :active, :focus, :focus-visible and :focus-within. The
 * cascade has always matched those; until now nothing told it where the
 * pointer was, so they matched nothing.
 *
 * All of these take effect on the next update, like an attribute does.
 */

/* The topmost element at a point in document coordinates, or
 * WEVA_ELEMENT_NONE. Valid after an update. Useful on its own, for a host
 * routing its own clicks. */
weva_element_t weva_document_element_at(weva_document_t doc, double x, double y);

/* Moves the pointer. `buttons` is a bitmask of the buttons held, so a nonzero
 * value makes the element under the pointer :active; zero releases it.
 *
 * :hover applies to the element AND its ancestors (CSS 2.1 §5.11.3), which is
 * what makes `.card:hover .title` work with the pointer over the title. The
 * document works that chain out; a host passes a position. */
void weva_document_set_pointer(weva_document_t doc, double x, double y, uint32_t buttons);

/* The pointer left the surface: nothing is hovered or pressed. A host that
 * stops sending positions without this leaves the last element hovered. */
void weva_document_clear_pointer(weva_document_t doc);

/* A key went down or came up. `key` is a weva_key; pass WEVA_KEY_OTHER for
 * anything the engine has no meaning for and it still reaches the host as an
 * event. Returns 1 when the ENGINE consumed it -- Tab moving focus is the case
 * that matters -- so a host knows not to act on it as well. */
int weva_document_key(weva_document_t doc, int key, uint32_t modifiers, int down);

/* Text the user typed, UTF-8. Separate from the key events because they are
 * separate things: one key can produce no text, and one character can take
 * several keys. */
void weva_document_text_input(weva_document_t doc, const char* utf8);

/* Moves focus to the next focusable element in tab order, or the previous one
 * when `backwards`. Returns the element that now has focus.
 *
 * Focusable means `tabindex` that is not negative, or one of the elements that
 * is focusable by nature -- a, button, input, select, textarea -- and not
 * disabled or hidden. Positive tabindex comes first in numeric order, then
 * everything else in document order, which is what HTML specifies and what
 * surprises people who expect one or the other alone. */
weva_element_t weva_document_focus_next(weva_document_t doc, int backwards);

/* Focus, or WEVA_ELEMENT_NONE to drop it. Sets :focus and :focus-visible on
 * the element and :focus-within on its ancestors. Focus is the host's to
 * decide -- the document has no notion of tab order yet. */
weva_status weva_document_set_focus(weva_document_t doc, weva_element_t element);

/* Sets an attribute, which restyles on the next update. A null value removes
 * it. */
weva_status weva_element_set_attribute(weva_document_t doc, weva_element_t element,
                                       const char* name, const char* value);

/* ---- Form controls ----------------------------------------------------
 *
 * The engine already PAINTS these from their attributes -- a checkbox from
 * `checked`, a range from `value` -- so what these add is the part a user
 * does: clicking a checkbox toggles it, typing into a focused field edits it,
 * dragging a range moves it. The state stays in the attributes, so a script
 * can set it the same way it reads it, and a stylesheet can select on it.
 */

/* The current value of a form control, with the same buffer convention as
 * weva_element_text. A checkbox reports "on" or "", which is what a form
 * submission would carry. */
size_t weva_element_value(weva_document_t doc, weva_element_t element, char* buffer,
                          size_t capacity);

/* Sets it, as the user would. Raises no event: a host that just set the value
 * already knows. */
weva_status weva_element_set_value(weva_document_t doc, weva_element_t element,
                                   const char* value);

/* Replaces an element's text with `text`.
 *
 * Every text node under the element goes, and one carrying `text` takes their
 * place; child ELEMENTS are left where they are, so setting the text of a row
 * does not throw away the icon inside it. This is what data binding is made
 * of, and until now the ABI had no way to do it at all -- a host could style a
 * document but never change what it said. */
weva_status weva_element_set_text(weva_document_t doc, weva_element_t element, const char* text);

/* Copies one attribute's value into `buffer`, with the same convention as
 * weva_element_text: always null-terminated when capacity allows, and the
 * length that WOULD have been written is returned. Zero when the attribute is
 * absent, which is indistinguishable from an empty value -- as it is in HTML.
 *
 * A host that hit-tests gets a handle back, and a handle is an index; this is
 * how it turns that into something its own code can name. */
size_t weva_element_attribute(weva_document_t doc, weva_element_t element, const char* name,
                              char* buffer, size_t capacity);

/* Copies the text of an element's descendants into `buffer`, always
 * null-terminating when capacity allows, and returns the length that WOULD
 * have been written — so a host can size a buffer with one call and fill it
 * with a second. */
size_t weva_element_text(weva_document_t doc, weva_element_t element, char* buffer,
                         size_t capacity);

#ifdef __cplusplus
}
#endif

#endif /* WEVA_C_H */
