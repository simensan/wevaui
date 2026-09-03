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
#define WEVA_ABI_VERSION_MINOR 2

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
    WEVA_EVENT_VALUE_CHANGED,
    /* The value COMMITTED, which is a different question from the value
     * changing. A text field commits when the focus leaves it and what it
     * holds is not what it held when the focus arrived, so a search box can
     * run once instead of once per keystroke. A checkbox, a radio and a
     * select have no editing state to leave, so for them the two are the same
     * moment. `on-change`. */
    WEVA_EVENT_CHANGE,
    /* A form submitted: Enter in a field inside one, or a click on a submit
     * button in one. The target is the FORM, not what was pressed, since that
     * is what a handler wants. `on-submit`. */
    WEVA_EVENT_SUBMIT,
    /* A scroll container moved, however it was moved -- wheel, bar, keyboard,
     * a finger, or a script. `on-scroll`. */
    WEVA_EVENT_SCROLL,
    /* A <details> opened or closed. The target is the <details>, and its
     * `open` attribute already says which way -- so a handler that loads the
     * contents on first open has somewhere to hang. `on-toggle`. */
    WEVA_EVENT_TOGGLE,
    /* The secondary button went down on an element -- what a right-click
     * means. The engine does nothing else with it: a context menu is markup,
     * and this is the signal to show it. `on-contextmenu`. */
    WEVA_EVENT_CONTEXT_MENU
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
    WEVA_KEY_END = 12,
    WEVA_KEY_PAGE_UP = 13,
    WEVA_KEY_PAGE_DOWN = 14
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
    /* What the markup called this: the value of `on-<event>` on the element or
     * the nearest ancestor that has one, empty when nobody named a handler.
     *
     *     <button on-click="OnStart">Start</button>
     *
     * Reporting the NAME lets a script dispatch by what the markup asked for
     * rather than by which element it happened on, so a designer can move a
     * button, rename its id, or wrap it in something without the script
     * hearing about it. Looked up towards the root, so `on-submit` on a form
     * catches a button inside it. */
    char handler[48];
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

/* Which pointer buttons are held, as a bitmask on `buttons`. Matching the
 * web's MouseEvent.buttons, so a host that already speaks that needs no
 * translation.
 *
 * Only the PRIMARY button activates anything: a right-click does not toggle a
 * checkbox, submit a form, open a <details> or work a popover, exactly as it
 * does not in a browser. Passing a bare 1 -- as every host did before these
 * names existed -- is the primary button, so nothing changes for one that has
 * not thought about it. */
typedef enum weva_pointer_button {
    WEVA_BUTTON_PRIMARY = 1u << 0,
    WEVA_BUTTON_SECONDARY = 1u << 1,
    WEVA_BUTTON_MIDDLE = 1u << 2
} weva_pointer_button;

/* Moves the pointer. `buttons` is a bitmask of weva_pointer_button, so a
 * nonzero value makes the element under the pointer :active; zero releases it.
 *
 * Pressing inside a text field puts the cursor at the character under the
 * pointer, and dragging from there selects -- so a field behaves like one
 * without a host doing anything about it.
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

/* Moves focus in a DIRECTION rather than along the tab order.
 *
 * What a gamepad or a D-pad needs, and what a tab order cannot express: the
 * next control to the left of this one is a question about geometry, not about
 * source order, and a menu laid out as a grid tabs through it in reading order
 * whatever the stick did. A script cannot reasonably do this itself either --
 * it would have to fetch every focusable's rectangle and re-derive the whole
 * heuristic each frame.
 *
 * `dx` and `dy` give the direction; only their signs matter, and exactly one
 * should be non-zero. Returns the element that took focus, or the current one
 * when there is nothing that way -- deliberately, because a menu should stay
 * where it is at its edge rather than wrapping to the far side under the
 * player's thumb. With nothing focused it takes the first focusable, so a
 * fresh screen answers the first press. */
weva_element_t weva_document_focus_move(weva_document_t doc, double dx, double dy);

/* Focus, or WEVA_ELEMENT_NONE to drop it. Sets :focus and :focus-visible on
 * the element and :focus-within on its ancestors. Focus is the host's to
 * decide -- the document has no notion of tab order yet. */
weva_status weva_document_set_focus(weva_document_t doc, weva_element_t element);

/* What has the focus, or WEVA_ELEMENT_NONE. The focus moves without a host
 * asking -- Tab walks it, a click moves it, a label moves it to the control it
 * names -- so a host that mirrors it (a Godot focus ring, a restore after a
 * rebuild) needs to be able to read it back and could not. */
weva_element_t weva_document_focus(weva_document_t doc);

/* How long the pointer must rest on an element carrying `title` before its
 * tooltip appears, in seconds. Default 0.6, as the reference has it.
 *
 * NEGATIVE turns tooltips off entirely and takes down any that is showing --
 * a game with its own tooltip presentation wants the `title` attribute as
 * data, not as a <div> the engine draws. Zero shows one on the next update. */
void weva_document_set_tooltip_delay(weva_document_t doc, double seconds);

/* ---- Dropdowns --------------------------------------------------------
 *
 * Clicking a <select> opens its list, clicking an option chooses it, and the
 * choice is written back to the DOM as `selected` on that option -- so a
 * stylesheet sees it through :checked and a script reads it as the element's
 * value, with no separate state to keep in step.
 *
 * The list is painted after everything else and hit tested before everything
 * else, because a dropdown covers whatever it opens over and is not in the box
 * tree at all. A host that routes its own input can drive it with these.
 */

/* Opens the list of the <select> at `element`, or closes whatever is open when
 * passed WEVA_ELEMENT_NONE. Returns 0 when the element is not a select. */
int weva_document_open_select(weva_document_t doc, weva_element_t element);

/* Which select is open, or WEVA_ELEMENT_NONE. */
weva_element_t weva_document_open_select_element(weva_document_t doc);

/* ---- Selection --------------------------------------------------------
 *
 * Shift with any of the movement keys extends a selection from where the
 * cursor was; an unshifted move drops it. Typing, Backspace and Delete replace
 * what is selected. These are the parts a host cannot do for itself, plus the
 * two a host DOES have to drive: select-all, because the ABI's key enum has no
 * letters and so cannot see Ctrl+A, and reading the selected text, because the
 * clipboard belongs to the platform and not to the document.
 */

/* Selects the word under a point, which is what a double click does. The
 * platform decides what counts as a double click -- the document is never told
 * the time -- so a host calls this when it sees one. Returns 0 when the point
 * is not over a field's text. */
int weva_document_select_word_at(weva_document_t doc, double x, double y);

/* Selects everything in the focused field. Returns 0 when nothing is focused
 * or what is focused takes no text. */
int weva_document_select_all(weva_document_t doc);

/* Undo and redo the focused field's edits. Returns 0 when there is nothing to
 * undo, nothing focused, or what is focused takes no text.
 *
 * The key enum has no letters, so the document never sees Ctrl+Z for itself:
 * a host binds the shortcut its platform uses and calls this, the same way it
 * does for select-all and the clipboard.
 *
 * A run of typing is ONE step -- undoing a sentence a letter at a time is not
 * undo -- and anything that is not typing ends the run. Setting the value from
 * a script clears the history, since the stack no longer describes the field. */
int weva_document_undo(weva_document_t doc);
int weva_document_redo(weva_document_t doc);

/* The selected text of the focused field, for a host putting it on the
 * clipboard. Follows the two-call pattern: returns the length, and fills
 * `buffer` when there is one. Zero when nothing is selected. */
size_t weva_document_selected_text(weva_document_t doc, char* buffer, size_t capacity);

/* Where the selection is, in BYTES into the field's value, with `start` the
 * end the user began from -- so a backwards selection reports start > end.
 * Both equal when there is only a cursor. */
weva_status weva_element_selection(weva_document_t doc, weva_element_t element, int* out_start,
                                   int* out_end);

/* Sets it, for a host driving its own selection UI. `start` is the end that
 * stays put; passing the two equal leaves a plain cursor. */
weva_status weva_element_set_selection(weva_document_t doc, weva_element_t element, int start,
                                       int end);

/* ---- Data binding ------------------------------------------------------
 *
 * `{{ path }}` in text or in an attribute, filled in from the host's data, and
 * `data-class-<name>="path"` to put one class on or off. It is the same markup
 * the Unity engine binds a C# controller to; a host here has no objects to
 * reflect over, so the document asks for a path and is handed text.
 *
 * The markup stays the template. A text node keeps what it was parsed with and
 * an attribute's template is remembered the first time it is filled, so the
 * same document can be refilled every time the data moves.
 */

typedef struct weva_binding_source {
    void* user;
    /* Fills `buffer` with the value at `path` and returns its length, the way
     * every other string accessor here does. Set `*found` to 0 for a path the
     * host does not know: the binding then shows nothing, rather than the
     * host having to invent a value for it. */
    size_t (*value)(void* user, const char* path, char* buffer, size_t capacity, int* found);
    /* How many items are in the list at `path`, or -1 when it is not a list.
     * Only `data-each` asks; a host with no lists can leave this null.
     *
     *     <template data-each="Quests as quest" data-key="Id">
     *       <li>{{ $index }}. {{ quest.Title }}</li>
     *     </template>
     *
     * makes one row per item beside the template, resolving `quest.Title`
     * against `Quests.<i>.Title` and `$index` against the row number. A row
     * whose KEY is unchanged is refilled where it stands, so the focus, the
     * scroll and the selection inside it survive a value changing. */
    int (*count)(void* user, const char* path);
} weva_binding_source;

/* Where the values come from. Null clears it, which leaves the document
 * showing whatever it last resolved. */
void weva_document_set_binding_source(weva_document_t doc, const weva_binding_source* source);

/* Re-reads every binding in the document and writes what changed into the DOM.
 * Returns how many nodes it changed, so a host can tell a refresh that did
 * something from one that did not. Call it when the data moves; nothing else
 * can know that it has. */
int weva_document_refresh_bindings(weva_document_t doc);

/* ---- Building the document from data ----------------------------------
 *
 * Setting text and attributes lets a host update a document; these let it
 * BUILD one. An inventory, a quest log, a chat pane are all a list whose
 * length is the game's business, and none of them can be expressed by editing
 * markup that was written in advance.
 *
 * Each takes effect on the next update, which rebuilds the box tree and lays
 * the document out again -- adding a row can move everything after it, so
 * there is nothing cheaper to be honest about. The cascade still walks the
 * whole document, but an element whose style comes out the same keeps the one
 * it had, address and all, so nothing downstream of it is disturbed.
 */

/* Replaces an element's children with `html` (what `innerHTML` does). Pass an
 * empty string to empty it. */
weva_status weva_element_set_html(weva_document_t doc, weva_element_t element, const char* html,
                                  size_t length);

/* Appends `html` as further children, and returns the FIRST element it
 * created -- so a caller can fill the row it just added without inventing a
 * selector to find it again. WEVA_ELEMENT_NONE when the html held no element. */
weva_element_t weva_element_append_html(weva_document_t doc, weva_element_t element,
                                        const char* html, size_t length);

/* Removes an element and everything under it. Handles for them stop resolving,
 * and every handle that is not in the removed subtree keeps working. */
weva_status weva_element_remove(weva_document_t doc, weva_element_t element);

/* Every element the selector matches, in document order, written into `out`
 * up to `capacity`. Returns how many there ARE, which may be more than were
 * written -- the two-call pattern the rest of the ABI uses. */
size_t weva_document_query_all(weva_document_t doc, const char* selector, weva_element_t* out,
                               size_t capacity);

/* ---- Scrolling --------------------------------------------------------
 *
 * A box with `overflow` other than `visible` clips what does not fit. These
 * move what it clips: an inventory, a quest log, a chat pane. The offset lives
 * on the element rather than on the box, so it survives the relayout that a
 * class change or a resize forces, and it is clamped to what there is to
 * scroll every time -- a list that shrinks under a scrolled view scrolls back
 * up by itself rather than showing empty space.
 */

/* Page Up, Page Down, the arrows, Home and End scroll the nearest scroll
 * container around whatever has focus -- or, with nothing focused, around
 * whatever the pointer is over. weva_document_key does it and reports the key
 * consumed, so a keyboard user can reach the bottom of a list. A text field
 * takes those keys first: in one, they move the caret.
 *
 * Nothing scrollable means the key is NOT consumed, so a host is free to use
 * the arrows for its own menu.
 */

/* Scrolls by (`dx`, `dy`) the nearest scroll container at a point in document
 * coordinates -- what a wheel does. Walks up from the point until it finds one
 * with room to move in the direction asked, so a wheel over a list inside a
 * scrolled page moves the list until it hits the end and then the page.
 *
 * Returns 1 when something scrolled, 0 when nothing there could -- which is
 * the answer a host needs to decide whether to handle the wheel itself. */
int weva_document_scroll(weva_document_t doc, double x, double y, double dx, double dy);

/* One element's scroll offset. Clamped on the next update. */
weva_status weva_element_set_scroll(weva_document_t doc, weva_element_t element, double x,
                                    double y);

/* Where an element is scrolled to and how far it can go. Any out pointer may
 * be null. Valid after an update; an element that generates no box, or one
 * that does not clip, reports zeroes. */
weva_status weva_element_scroll(weva_document_t doc, weva_element_t element, double* out_x,
                                double* out_y, double* out_max_x, double* out_max_y);

/* Scrolls every container between this element and the root by the least that
 * brings it into view -- what a chat pane does with a new message, and what a
 * list does when the keyboard moves the selection past its edge. Focusing an
 * element does this by itself, so a tab that lands off screen brings its
 * target with it. */
weva_status weva_element_scroll_into_view(weva_document_t doc, weva_element_t element);

/* Sets an attribute, which restyles on the next update. A null value removes
 * it. */
/* One declaration of an element's inline `style`, left alone otherwise.
 *
 * Setting the whole attribute is what a script had to do to change one
 * property, which means reading it, finding the declaration, splicing it and
 * writing the rest back -- and getting the splice wrong loses every other
 * declaration on the element. This does that once, here.
 *
 * A null or empty `value` REMOVES the declaration, so a script can put a
 * property back under the stylesheet's control rather than having to guess
 * what the stylesheet said. The declaration list is split at top level, so a
 * semicolon inside `url(...)` or a quoted string does not cut a value in
 * half. */
weva_status weva_element_set_style(weva_document_t doc, weva_element_t element,
                                   const char* property, const char* value);

/* That element's inline value for one property -- what its `style` attribute
 * says, NOT what the cascade decided. weva_element_computed_style answers the
 * second question, and they differ whenever a stylesheet is involved at all.
 * Returns 0 when the element does not set it inline. */
size_t weva_element_style(weva_document_t doc, weva_element_t element, const char* property,
                          char* buffer, size_t capacity);

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

/* Opens a <dialog>. `modal` non-zero shows it MODALLY: it joins the top layer
 * and gets a `::backdrop` behind it, which is the whole visible difference and
 * the reason showModal exists. Returns WEVA_ERR_NOT_FOUND for anything that is
 * not a <dialog>.
 *
 * Escape is deliberately NOT wired here, matching the reference: what key
 * cancels a dialog is a platform question, so a host binds it and calls
 * weva_element_close_dialog itself. */
weva_status weva_element_show_dialog(weva_document_t doc, weva_element_t element, int modal);

/* Closes it, modal or not, and takes it back out of the top layer. */
weva_status weva_element_close_dialog(weva_document_t doc, weva_element_t element);

/* Opens, closes or flips a popover -- an element with a `popover` attribute.
 * An open one joins the top layer and gets a `::backdrop`, exactly as a modal
 * dialog does.
 *
 * A host rarely needs these: a `<button popovertarget="menu">` works its
 * popover on its own, an `auto` popover closes when a click lands outside it,
 * and Escape closes the topmost one. They are here for a script that opens a
 * menu from something other than a click.
 *
 * Returns WEVA_ERR_NOT_FOUND for an element with no `popover` attribute. */
weva_status weva_element_show_popover(weva_document_t doc, weva_element_t element);
weva_status weva_element_hide_popover(weva_document_t doc, weva_element_t element);
weva_status weva_element_toggle_popover(weva_document_t doc, weva_element_t element);

/* The `data-each` row an element is in: its 0-based position in the list and
 * its `data-key` identity. Walks up from `element`, so a click on a button
 * deep inside a row still finds the row.
 *
 * A repeated row usually has no id -- the template wrote one element and the
 * data decides how many there are -- so an event from inside one arrives with
 * nothing to say WHICH row it was. This is that answer.
 *
 * Returns 1 when the element is inside a repeated row, 0 when it is not (and
 * then `out_index` is untouched and the key buffer gets an empty string). The
 * key follows the usual two-call convention: pass a null buffer to ask for the
 * length. */
/* Where a relative `url(...)` resolves from -- the document's own directory,
 * normally, so `background-image: url(icons/gem.png)` beside the HTML finds
 * the file the way a browser would.
 *
 * A path with a scheme (`res://`, `user://`, `http://`) or an absolute path is
 * left alone, because joining a base onto one produces something no host could
 * open. Changing this drops every decoded image, so set it before the first
 * update rather than per frame.
 *
 * Without it, only absolute paths load. With it, the built-in reader opens
 * ordinary files; a host whose assets are not files -- Godot's `res://` inside
 * an exported .pck -- wants weva_document_set_asset_reader instead, and still
 * gets the core's decoder, so both backends see identical pixels. */
weva_status weva_document_set_base_path(weva_document_t doc, const char* path);

/* Every asset the document asked for and could not load, newline-separated.
 *
 * Worth asking, because the failure mode is silence: a document whose images
 * do not load draws no backgrounds, no <img> and no border images, and looks
 * exactly like a page that has none. Three tools shipped without a base path
 * and each was found only when somebody eventually looked at a picture.
 *
 * Returns how many there are; the buffer follows the usual two-call
 * convention. Zero is the answer a working document gives. */
size_t weva_document_missing_assets(weva_document_t doc, char* buffer, size_t capacity);

/* How the core obtains an asset's bytes. Returns the number of bytes the asset
 * HAS, writing up to `capacity` of them -- the two-call convention the rest of
 * the ABI uses -- or 0 when there is no such asset. Passing a null function
 * restores the built-in filesystem reader. Changing it drops every decoded
 * image. */
typedef size_t (*weva_asset_reader)(void* user_data, const char* path, uint8_t* buffer,
                                    size_t capacity);
weva_status weva_document_set_asset_reader(weva_document_t doc, weva_asset_reader reader,
                                           void* user_data);

/* The value the cascade settled on for one property, as a string.
 *
 * What a script cannot otherwise find out: the stylesheet is the authority on
 * an element's colour, its font size, and whether it is displayed at all, and
 * a game that wants to tint a particle to match a panel has no way to ask.
 * This is that question. The answer is the COMPUTED value -- inheritance and
 * the initial value already resolved -- so a property the element never set
 * still answers with what it is actually using.
 *
 * Layout results are not here: an element's position and size come from
 * weva_element_bounds, because they are the layout's answer rather than the
 * cascade's. Returns 0 for an unknown property name. Two-call convention. */
size_t weva_element_computed_style(weva_document_t doc, weva_element_t element,
                                   const char* property, char* buffer, size_t capacity);

/* A binding path written INSIDE a repeated row, resolved against the whole
 * document.
 *
 * `data-each="Quests as quest"` gives the rows an alias, so markup inside one
 * says `quest.Done` -- which means nothing at the top of the data. This turns
 * it into `Quests.3.Done`. Nested repeats are unwound too, innermost first, so
 * a step inside a quest resolves the whole way up.
 *
 * A path that names no alias is copied through unchanged, and so is one on an
 * element that is not in a row: the caller can always use the result. Follows
 * the usual two-call convention. */
size_t weva_element_model_path(weva_document_t doc, weva_element_t element, const char* path,
                               char* buffer, size_t capacity);

int weva_element_row(weva_document_t doc, weva_element_t element, int* out_index, char* key_buffer,
                     size_t key_capacity);

/* Whether the attribute is THERE, which reading its value cannot tell you.
 * HTML's boolean attributes -- `open`, `checked`, `disabled`, `selected`,
 * `required`, `readonly` -- are usually written with no value at all, so
 * weva_element_attribute returns 0 for a present one exactly as it does for an
 * absent one. Returns 1 when present, 0 when absent or the element is gone. */
int weva_element_has_attribute(weva_document_t doc, weva_element_t element, const char* name);

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
