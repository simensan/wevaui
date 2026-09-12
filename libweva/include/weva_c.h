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
#define WEVA_ABI_VERSION_MINOR 37

uint32_t weva_abi_version(void);

/* Mirrors weva::Status. Zero is success, so `if (weva_...)` reads as failure. */
typedef enum weva_status {
    WEVA_OK = 0,
    WEVA_ERR_INVALID_ARGUMENT = 1,
    WEVA_ERR_PARSE = 2,
    WEVA_ERR_NOT_FOUND = 3,
    WEVA_ERR_UNSUPPORTED = 4,
    WEVA_ERR_INTERNAL = 5,
    WEVA_ERR_INVALID_STATE = 6
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

/* CSS Compositing 1 <blend-mode>, in the specification's order: how a draw
 * composites with what is already in the target (`mix-blend-mode`). A host
 * renders the modes it can -- multiply, screen, darken and lighten are plain
 * blend states; the rest need the backdrop in a shader -- and draws the
 * others normally. Available since ABI minor 33. */
typedef enum weva_blend_mode {
    WEVA_BLEND_NORMAL = 0,
    WEVA_BLEND_MULTIPLY = 1,
    WEVA_BLEND_SCREEN = 2,
    WEVA_BLEND_OVERLAY = 3,
    WEVA_BLEND_DARKEN = 4,
    WEVA_BLEND_LIGHTEN = 5,
    WEVA_BLEND_COLOR_DODGE = 6,
    WEVA_BLEND_COLOR_BURN = 7,
    WEVA_BLEND_HARD_LIGHT = 8,
    WEVA_BLEND_SOFT_LIGHT = 9,
    WEVA_BLEND_DIFFERENCE = 10,
    WEVA_BLEND_EXCLUSION = 11,
    WEVA_BLEND_HUE = 12,
    WEVA_BLEND_SATURATION = 13,
    WEVA_BLEND_COLOR = 14,
    WEVA_BLEND_LUMINOSITY = 15
} weva_blend_mode;

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
    /* One of weva_blend_mode: the `mix-blend-mode` in effect for this draw
     * (an element's mode applies to every draw of its subtree). Since ABI
     * minor 33; WEVA_BLEND_NORMAL for everything before it. */
    int32_t blend_mode;
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

/* Positioned shaping, added in minor 11 without extending the existing font
 * callback table. Coordinates are pixels: x is rightward, y is upward from
 * the baseline. A cluster is a UTF-8 BYTE offset into the source string.
 * Glyph IDs retain the installed font backend's opaque ID convention. */
typedef struct weva_shaped_glyph {
    uint32_t glyph, cluster;
    double x_advance, y_advance;
    double x_offset, y_offset;
} weva_shaped_glyph;

/* Same sizing protocol as shape: write at most capacity entries, return the
 * complete count. user_data comes from the installed weva_font_backend. */
typedef size_t (*weva_shape_glyphs_fn)(void* user_data, uint64_t face, const char* utf8,
                                    size_t length, double px, weva_shaped_glyph* out,
                                    size_t capacity);

/* Both copy the table, so the caller may free it on return. Passing null
 * restores the built-in stub. Registering after a document has been updated
 * takes effect on the NEXT update. Installing a font table refreshes cached
 * glyphs even when the table and face ID are unchanged. Changing renderers
 * releases the atlas through its old owner and reuploads its CPU pixels. */
/* HOST-OPTIONAL: neither shipped host installs a render backend. Both collect
 * the draw list themselves (weva_document_draws) and turn it into their own
 * engine's geometry, which is the path a game host wants. This hook is for a
 * host that would rather the core drive its rasteriser directly; Tools/
 * weva_render uses it for the software backend. Core tests cover it. */
void weva_document_set_render_backend(weva_document_t doc, const weva_render_backend* backend);
void weva_document_set_font_backend(weva_document_t doc, const weva_font_backend* backend,
                                    uint64_t face);

/* Overrides the installed font table's shape callback. Null restores its
 * legacy callback. Changing this invalidates shape/measurement/layout caches;
 * installing a font backend again clears the override. A font table must be
 * installed first (otherwise INVALID_ARGUMENT). Existing font tables and
 * their callers keep their original binary layout and behavior. */
weva_status weva_document_set_font_shaper(weva_document_t doc, weva_shape_glyphs_fn shape);

/* Whether a host font backend's half-leading is rounded down to whole
 * pixels, as real font layout does (the default, 1). A synthetic face that
 * exists to be compared with the oracle's arithmetic (the Unity host's
 * layout dump against weva_dump) passes 0 so fractional leading stays exact.
 * Applies to the installed backend and to ones installed later; the
 * built-in stub is unaffected. Takes effect on the next update. Available
 * since ABI minor 26. */
void weva_document_set_font_leading_rounding(weva_document_t doc, int rounds);

/* Register one CSS family name (not a comma-separated stack) with a face from
 * the installed font backend. Names are copied and matched case-insensitively.
 * A zero face removes the registration. Repeating the same mapping is a no-op.
 * Changes take effect on the next update. Registrations survive HTML reload and
 * shaper changes; installing/replacing the font backend clears them, since face
 * identities belong to that backend. The host keeps face resources alive. */
weva_status weva_document_register_font_family(weva_document_t doc, const char* family,
                                               uint64_t face);


weva_document_t weva_document_create(const weva_config* config);
void weva_document_destroy(weva_document_t doc);

/* Both take an explicit length so a host is never required to null-terminate.
 * The bytes are copied; the caller may free them on return.
 *
 * The markup's own stylesheets are read as well: every `<style>` outside a
 * `<template>` is an author sheet after the host's (its `media` attribute is
 * honoured against the viewport and colour scheme), and a `<style>` inside
 * `<template id="card">` is that component's scoped sheet -- its selectors
 * reach the component's rendering and `:host` its host element, never the
 * page or the light-dom slotted into it. Both follow the markup through
 * weva_document_reload_html and weva_element_append_html. */
weva_status weva_document_load_html(weva_document_t doc, const char* html, size_t length);
weva_status weva_document_add_css(weva_document_t doc, const char* css, size_t length);
/* Replaces all author stylesheets; the UA sheet and live DOM, form values,
 * focus, bindings and animation clocks remain. An empty string removes author
 * CSS. Both this and add_css schedule a restyle on the next update. */
weva_status weva_document_set_css(weva_document_t doc, const char* css, size_t length);

/* Loads new markup INTO the live document instead of replacing it: the new
 * tree is parsed and diffed onto the live one. An element the diff can match
 * -- the same tag at the same position among its siblings, or the same `id`
 * or `data-key` anywhere among them -- keeps its identity, so its handle,
 * focus, scroll position, form value and running transitions survive;
 * attributes and text are updated in place, elements only the new markup has
 * are inserted where it puts them, elements only the old one had are removed.
 * What a hot reload wants; ports HotReload/DomDiffer.cs. With no document
 * loaded yet this is load_html. Available since ABI minor 32. */
weva_status weva_document_reload_html(weva_document_t doc, const char* html, size_t length);

void weva_document_set_viewport(weva_document_t doc, int width, int height);

/* The host's colour-scheme preference: 1 for dark, 0 for light (the default).
 * Drives `@media (prefers-color-scheme)` and the branch `light-dark()` picks
 * where no `color-scheme` on the element settles it. A change recompiles the
 * conditional rules and restyles on the next update, as set_viewport does.
 * Available since ABI minor 28. */
void weva_document_set_color_scheme(weva_document_t doc, int dark);

/* The display's safe-area insets in CSS pixels, which `env(safe-area-inset-
 * top)` and its three siblings read (zero until set). A notch, a rounded
 * corner, a system bar: the host measures them (Godot's
 * DisplayServer.get_display_safe_area, Unity's Screen.safeArea) and the page
 * pads with `env()` as it would in a browser. A change recompiles and
 * restyles on the next update. Available since ABI minor 36. */
void weva_document_set_safe_area_insets(weva_document_t doc, double top, double right,
                                        double bottom, double left);

/* How far the laid-out document reaches, which is not the viewport: half of the
 * sample corpus is taller than the box it is laid out in. A host that wants to
 * scroll a document needs this to know whether there is anywhere to scroll to.
 * Never smaller than the viewport. Valid after weva_document_update. */
weva_status weva_document_content_size(weva_document_t doc, double* out_width,
                                       double* out_height);

/* Runs cascade, layout and paint. `dt_seconds` advances animation and timed
 * input gestures; pass 0 for a static document.
 *
 * An update that finds nothing changed -- no attribute set, no pointer moved,
 * nothing in flight -- returns having done nothing, and the draws already
 * published stay valid. A host may therefore call this every frame. */
weva_status weva_document_update(weva_document_t doc, double dt_seconds);

/* Resolve current styles, geometry and input state without advancing clocks
 * or publishing paint. Existing draw views/serial remain unchanged; the next
 * ordinary update paints accumulated input changes. For input hit testing
 * between events. Available since ABI minor 22. */
weva_status weva_document_update_geometry(weva_document_t doc);

/* Separate animation and input clocks. The original update passes the same
 * elapsed time to both. A host pausing CSS animations can continue timed
 * gestures with animation_seconds=0 and real frame time for input_seconds.
 * Nonpositive or nonfinite input_seconds is ignored. */
weva_status weva_document_update_with_input_time(weva_document_t doc,
                                                double animation_seconds, double input_seconds);
/* Whether a held gesture needs further input ticks. False on release/cancel. */
int weva_document_needs_input_tick(weva_document_t doc);

/* Changes when, and only when, weva_document_update publishes a NEW draw list.
 *
 * A settled document does no work in update -- no cascade, no layout, no paint
 * -- and so publishes nothing, and this does not move. A host driving update
 * from its frame loop, which is the documented way to use it, should compare
 * this against the value it last drew and skip re-submitting when they match.
 *
 * That is not a micro-optimisation. Re-submitting means walking every draw and
 * converting every vertex again, for a page that has not changed: on a sample
 * with 150 draws and 7,280 vertices it was several milliseconds a frame, every
 * frame, for nothing. */
uint64_t weva_document_draw_serial(weva_document_t doc);
/* Current interaction input version, for host update synchronization. Reading
 * does not update or mutate the document; zero for a null document. Compare
 * against the version consumed by the host's last update, not focus identity. */
uint64_t weva_document_interaction_version(weva_document_t doc);

/* Whether any transition is still running, so a host knows to keep handing
 * over time and redrawing. False for a document that has settled. */
int weva_document_is_animating(weva_document_t doc);

/* The draw list from the last update. Valid until the next update or destroy,
 * and NOT owned by the caller — this is the one place the "explicit free" rule
 * is relaxed, in exchange for a documented lifetime, because a per-frame copy
 * of the whole display list is exactly the allocation the port exists to
 * remove. */
const weva_draw* weva_document_draws(weva_document_t doc, size_t* out_count);

/* Minor 12: one nonzero version per published draw, in the same order as
 * weva_document_draws. Equal versions within one document identify the same
 * immutable command, including its geometry, texture handle and effect data.
 * Replayed commands keep their versions even when their list positions move;
 * rebuilt commands get new versions, never reused during the document's life.
 * The array has the same lifetime as the published draw views. The existing
 * weva_draw struct and its array stride are unchanged. */
const uint64_t* weva_document_draw_versions(weva_document_t doc, size_t* out_count);
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
    /* Pointer press/release on one element, or native keyboard activation.
     * Keyboard clicks carry zero coordinates and no pointer events. */
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
    WEVA_EVENT_CONTEXT_MENU,
    /* IME lifecycle. text carries the selected text at start, the preedit on
     * update, and the final text on end. Value-change events still describe
     * provisional edits; hosts may use the lifecycle to defer their work. */
    WEVA_EVENT_COMPOSITION_START,
    WEVA_EVENT_COMPOSITION_UPDATE,
    WEVA_EVENT_COMPOSITION_END,
    // Defaults have been restored. Queued notifications cannot cancel reset.
    WEVA_EVENT_RESET,
    /* A dialog finished closing. Non-bubbling, non-cancelable; on-close. */
    WEVA_EVENT_CLOSE,
    /* A requested dialog close. Non-bubbling; prevent_default may veto it. */
    WEVA_EVENT_CANCEL,
    /* A control failed validation. Non-bubbling; on-invalid. Preventing this
     * event suppresses default focus reporting, not the failed submission. */
    WEVA_EVENT_INVALID,
    /* Popover transition request. Non-bubbling; text is "open" or "closed".
     * Only opening requests are cancellable. */
    WEVA_EVENT_BEFORE_TOGGLE
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
    /* The text a key produced, UTF-8 and null-terminated. For TOGGLE, the
     * captured new state: "open" or "closed". Inline rather than a pointer
     * so an event stays copyable and outlives nothing. */
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

/* Full UTF-8 text of the last successfully polled event, including long paste
 * and composition payloads. Returns the required bytes excluding the terminator;
 * a null buffer queries the size. Read/copy before polling again. The original
 * event struct remains copyable and contains a short UTF-8 prefix. */
size_t weva_document_event_text(weva_document_t doc, char* buffer, size_t capacity);

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

/* The cursor the page asks for under the pointer the host last set
 * (weva_document_cursor) or at a point (weva_document_cursor_at): the hovered
 * element's `cursor` as the CSS keyword it computes to, `auto` settled the way
 * a browser settles it (`text` over text and text fields, `pointer` over a
 * link, `default` elsewhere) and a url() list reduced to its fallback keyword,
 * so a host maps the keywords it has shapes for and shows its arrow for the
 * rest. `default` with no pointer over the document. The usual two-call
 * buffer convention; valid after an update. Available since ABI minor 29. */
size_t weva_document_cursor(weva_document_t doc, char* buffer, size_t capacity);
size_t weva_document_cursor_at(weva_document_t doc, double x, double y, char* buffer,
                               size_t capacity);

/* A hit test for tooling: like element_at, but `pointer-events: none` and
 * `visibility: hidden` do not hide anything from it, so an inspector can
 * pick what is drawn under the pointer rather than what would receive a
 * click. Modal inertness is not applied either. Available since ABI minor
 * 30. */
weva_element_t weva_document_element_at_devtools(weva_document_t doc, double x, double y);

/* Engine counters for a host's profiler or stats window. Timings are the
 * last update's, in milliseconds, by stage (a stage that did not run is 0);
 * the cache figures are the last paint pass's; the cascade counts run since
 * the document was created, so a host reads the difference between frames.
 * Available since ABI minor 30. */
typedef struct weva_stats {
    double update_ms;
    double cascade_ms, animate_ms, boxes_ms, layout_ms, paint_ms;
    uint64_t updates;                 /* updates so far */
    uint32_t elements;                /* elements in the document */
    uint32_t boxes;                   /* boxes in the layout tree, anonymous, line and text boxes included */
    uint32_t draws;                   /* draw commands published */
    uint32_t textures;                /* textures the host holds for this document */
    uint32_t texture_cache_hits, texture_cache_misses;
    uint64_t cascade_elements, cascade_pseudos;   /* styles computed since creation */
} weva_stats;
void weva_document_stats(weva_document_t doc, weva_stats* out);

/* ---- The box tree (minor 31) ---------------------------------------------
 *
 * Every box of the layout tree in tree order (a parent before its children),
 * anonymous, line and text boxes included: what a devtools overlay draws its
 * outlines from and a box-tree view lists. Geometry is the border box in
 * document coordinates with scroll offsets NOT applied (the layout dump's
 * numbers); a box's own scroll offset comes along so a tool can apply it.
 * `text` is a text box's run, not NUL-terminated, valid until the next
 * update. Text runs and the inline boxes that cover them are siblings under
 * their line box, as the layout tree keeps them. Written into `out` up to
 * `capacity`; returns how many there ARE. Valid after an update. */
typedef enum weva_box_kind {
    WEVA_BOX_BLOCK = 0,
    WEVA_BOX_ANONYMOUS_BLOCK = 1,
    WEVA_BOX_INLINE = 2,
    WEVA_BOX_ANONYMOUS_INLINE = 3,
    WEVA_BOX_LINE = 4,
    WEVA_BOX_TEXT = 5,
} weva_box_kind;

#define WEVA_BOX_NONE ((uint32_t)0xFFFFFFFFu)

typedef struct weva_box {
    uint32_t parent;                 /* index into the same list; WEVA_BOX_NONE for the root */
    uint32_t kind;                   /* weva_box_kind */
    weva_element_t element;          /* the owner; a text box names the element whose text it is; WEVA_ELEMENT_NONE for anonymous and line boxes */
    double x, y, width, height;
    double margin_top, margin_right, margin_bottom, margin_left;
    double border_top, border_right, border_bottom, border_left;
    double padding_top, padding_right, padding_bottom, padding_left;
    double scroll_x, scroll_y;
    const char* text;
    size_t text_length;
} weva_box;
size_t weva_document_boxes(weva_document_t doc, weva_box* out, size_t capacity);

/* Whether document content or an open dropdown accepts this point. Unlike
 * element_at, this includes dropdown rows outside the DOM box tree. Honors
 * CSS pointer-events; call after updating layout. */
int weva_document_accepts_pointer(weva_document_t doc, double x, double y);

/* A nonzero input version while an auto popover or dropdown is open, else 0.
 * A host observing a press routed to another native control may dismiss after
 * GUI routing. Dismiss only if this version still matches, so a native handler
 * opening a new popup cannot have it closed by the older press. No pointer or
 * activation events are synthesized. Manual popovers and dialogs stay open. */
uint64_t weva_document_transient_version(weva_document_t doc);
int weva_document_dismiss_transients(weva_document_t doc, uint64_t version);

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
/* Modifier-aware pointer input (weva_key_modifier). The original entry point
 * forwards with no modifiers. Listboxes replace on plain click, toggle with
 * Ctrl/Meta, and extend from their anchor with Shift. */
void weva_document_set_pointer_modifiers(weva_document_t doc, double x, double y,
                                         uint32_t buttons, uint32_t modifiers);

/* The pointer left the surface: nothing is hovered or pressed. A host that
 * stops sending positions without this leaves the last element hovered. */
void weva_document_clear_pointer(weva_document_t doc);

/* A key went down or came up. `key` is a weva_key; pass WEVA_KEY_OTHER for
 * anything the engine has no meaning for and it still reaches the host as an
 * event. Returns 1 when consumed by editing, focus, activation or scrolling.
 * Send both edges: buttons activate on Enter down or Space up; focus loss
 * cancels a pending Space. A consumed key must not also be inserted as text. */
int weva_document_key(weva_document_t doc, int key, uint32_t modifiers, int down);

/* Text the user typed, UTF-8. Separate from the key events because they are
 * separate things: one key can produce no text, and one character can take
 * several keys. */
void weva_document_text_input(weva_document_t doc, const char* utf8);

/* Same delivery, returning 1 when a focused editable text field consumed it.
 * maxlength limits the inserted UTF-16 units without splitting a code point.
 * Rejected insertion is still consumed; an unchanged field emits no text or
 * value event and adds no undo entry. Hosts keep it out of game handlers. */
int weva_document_try_text_input(weva_document_t doc, const char* utf8);
/* Physical text input with modifiers. Select typeahead excludes Ctrl/Alt/Meta
 * chords; text-field editing retains AltGr text. Plain text_input uses zero.
 * UTF-8 may contain several characters; a select searches them in order. */
int weva_document_try_text_input_modifiers(weva_document_t doc, const char* utf8, uint32_t modifiers);

/* Insert clipboard text as one undo step. Accepts leading tabs/newlines and
 * normalizes CR/LF for input or textarea before applying maxlength. Returns
 * 1 when an editable field consumes it, including rejection at the limit. */
int weva_document_paste_text(weva_document_t doc, const char* utf8);

/* The focused editable text field, or NONE. Call after an update to include
 * CSS visibility. Useful for activating a platform IME only over text input. */
weva_element_t weva_document_text_input_target(weva_document_t doc);

/* The focused text-control candidate based only on current DOM tag/type.
 * Does not resolve CSS, publish layout or test editability. A host can avoid
 * flushing geometry for IME when this is NONE; otherwise update and query
 * text_input_target before activating IME. Available since ABI minor 21. */
weva_element_t weva_document_text_input_candidate(weva_document_t doc);

/* Replace the current preedit, starting a composition at the selection if
 * necessary. start/end are UTF-8 byte offsets within utf8, clamped to character
 * boundaries. Provisional text is visible in the value, as in HTML input.
 * Empty text cancels, removing the preedit (including text it replaced).
 * All updates form one undo step. Returns 1 when accepted. */
int weva_document_set_composition(weva_document_t doc, const char* utf8, int start, int end);

/* Finish a composition. Non-null utf8 replaces it with the final text; null
 * keeps the current preedit and selection. Finishing an editable composition,
 * including focus loss, enforces maxlength; trimming collapses the selection.
 * With no composition, nonempty text is delivered as ordinary text input.
 * Empty utf8 cancels. Returns 1 when a composition or edit was handled. */
int weva_document_commit_composition(weva_document_t doc, const char* utf8);

/* Active composition target and byte range in its value; NONE otherwise. */
weva_element_t weva_document_composition(weva_document_t doc, int* start, int* end);

/* Insertion caret in document coordinates, including CSS transforms and
 * scrolling. Valid after an update; returns 0 without an editable target.
 * Hosts transform this rectangle into window coordinates for IME candidates. */
int weva_document_caret_bounds(weva_document_t doc, double* x, double* y,
                               double* width, double* height);

/* Moves focus to the next focusable element in tab order, or the previous one
 * when `backwards`. Returns the element that now has focus.
 *
 * Focusable means `tabindex` that is not negative, or one of the elements that
 * is focusable by nature -- a, button, input, select, textarea, summary -- and not
 * disabled or hidden. Positive tabindex comes first in numeric order, then
 * everything else in document order, which is what HTML specifies and what
 * surprises people who expect one or the other alone. A named radio group
 * within one form contributes one Tab stop; arrows move within that group. */
weva_element_t weva_document_focus_next(weva_document_t doc, int backwards);

/* Like focus_next, but wrap=0 clears focus and returns NONE at a document's
 * edge so an embedding UI can continue to its next native control. */
weva_element_t weva_document_focus_step(weva_document_t doc, int backwards, int wrap);

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
 * the element and :focus-within on its ancestors. Disabled controls return
 * WEVA_ERR_INVALID_ARGUMENT; hidden or modal-blocked targets return
 * WEVA_ERR_INVALID_STATE without changing focus. Pending style mutations are
 * considered. Tab and directional navigation use the focus APIs above. */
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
 * choice changes the option's live selectedness. Stylesheets see it through
 * :checked and scripts read the control value. The selected attributes keep
 * their reset defaults.
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

/* Selects all text in the focused field, or all enabled options in a focused
 * multiple select. Returns 0 when the focused element supports neither. */
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
 * Both equal when there is only a cursor. Visited fields retain this state
 * across focus changes; replacing their value moves the saved cursor to its end. */
weva_status weva_element_selection(weva_document_t doc, weva_element_t element, int* out_start,
                                   int* out_end);

/* Sets it, for a host driving its own selection UI. `start` is the end that
 * stays put; passing the two equal leaves a plain cursor. This host helper
 * focuses the field; it is not the non-focusing DOM setSelectionRange method. */
weva_status weva_element_set_selection(weva_document_t doc, weva_element_t element, int start,
                                       int end);

/* Same byte-based anchor/caret convention, without changing document focus.
 * Can prepare a selection in a hidden or disabled field. Only a currently
 * focused field updates its visible caret or commits its own composition. */
weva_status weva_element_set_selection_without_focus(weva_document_t doc,
    weva_element_t element, int start, int end);

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
     * host having to invent a value for it. The callback may be called again
     * with a larger buffer. Each return length must describe the value written
     * by that call; changing values are retried until one fits. An impossible
     * buffer length is diagnosed and treated as an unavailable value. */
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

/* One element's scroll offset. Clamped on the next update. A container with
 * `scroll-behavior: smooth` eases there over a quarter second instead (the
 * document reports itself animating meanwhile); a wheel, thumb, track or key
 * scroll of that container ends the animation where it is. */
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
/* Each container with `scroll-behavior: smooth` on the way eases to its
 * offset, as weva_element_set_scroll does. */

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

/* Sets live state, preserving markup defaults. Text/range values are
 * sanitized; maxlength does not limit programmatic writes. Checkbox/radio
 * use "on"/"" and multiple selects use comma-separated values. No input or
 * change event is raised; this sets the control's dirty value/checked flag. */
/* Minor 20: live form-state input version, including validity/edit-source
 * changes that do not alter the public value. Missing document/element -> 0.
 * Compare only within one document lifetime and element identity. */
uint64_t weva_element_form_version(weva_document_t doc, weva_element_t element);

weva_status weva_element_set_value(weva_document_t doc, weva_element_t element,
                                   const char* value);

/* Restore current markup defaults, including controls outside the form with
 * form="id". Preserve focus, discard owned edit history/preedit, and queue one
 * RESET notification. No input/change events are synthesized. */
weva_status weva_document_reset_form(weva_document_t doc, weva_element_t form);
weva_element_t weva_element_form(weva_document_t doc, weva_element_t element);

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

/* The element's lowercase tag name ("input", "button"), same buffer
 * convention. What a host needs to decide how a gamepad's accept should read
 * for whatever has focus: Space on a control, Enter in a field. Available
 * since ABI minor 25. */
size_t weva_element_tag_name(weva_document_t doc, weva_element_t element, char* buffer, size_t capacity);

/* Opens a <dialog>. `modal` non-zero shows it MODALLY: it joins the top layer
 * and gets a `::backdrop` behind it, which is the whole visible difference and
 * the reason showModal exists. Returns WEVA_ERR_NOT_FOUND for anything that is
 * not a <dialog>. Repeating the same show mode is a no-op. Changing mode
 * while open, or showing a modal dialog currently shown as a popover,
 * returns WEVA_ERR_INVALID_STATE without mutation. Close before changing mode.
 * A successful opening queues TOGGLE with text="open".
 *
 * Keyboard Escape requests cancellation of the latest opened dialog after
 * open popovers/selects handle it, according to its closedby policy. Hosts drain
 * events and may prevent the cancel default. Markup/attribute openings also
 * register close order; the complete browser CloseWatcher model remains partial. */
weva_status weva_element_show_dialog(weva_document_t doc, weva_element_t element, int modal);

/* Closes it, modal or not, and takes it back out of the top layer.
 * Queues CLOSE after restoring focus. Closing an already closed dialog is a
 * no-op; directly removing the open attribute does not queue CLOSE. */
weva_status weva_element_close_dialog(weva_document_t doc, weva_element_t element);

/* Queue a cancelable close request for an open dialog. Hosts must drain events:
 * CANCEL is delivered while open; the next poll applies its default close unless
 * prevented. This keeps callbacks outside core mutation/layout code. A request
 * becomes stale if the dialog is closed, reopened, removed or the document reloads.
 * If the bounded queue contains only pending default actions, returns
 * WEVA_ERR_INVALID_STATE without queuing another request. Drain events and retry.
 * Ordinary event overflow cannot discard an accepted close request.
 * Unlike JavaScript requestClose(), this C queue API completes during event drain. */
weva_status weva_element_request_close_dialog(weva_document_t doc, weva_element_t element);
/* Veto the currently polled CANCEL, SUBMIT, INVALID or opening BEFORE_TOGGLE. Returns 1 if cancelable, else 0.
 * Call before polling the next event; other queued events are not cancelable. */
int weva_document_prevent_default(weva_document_t doc);

/* Game-specific validation error. Empty clears it; reset preserves it and a
 * clone starts without it. Supported on input, textarea, select, button and fieldset.
 * Getter returns the stored message even while the control is barred from
 * validation; it is not the browser's computed/localized validationMessage.
 * Returns full UTF-8 byte length, with a terminated prefix when capacity permits. */
weva_status weva_element_set_custom_validity(weva_document_t doc, weva_element_t element, const char* message);
size_t weva_element_custom_validity(weva_document_t doc, weva_element_t element, char* buffer, size_t capacity);

/* Read-only validity snapshot for input, textarea, select, button and fieldset. No layout,
 * focus changes or invalid events. Zero errors means ValidityState.valid, even
 * when will_validate is false. Both outputs are required and cleared on error.
 * Unsupported controls return NOT_FOUND. A nonempty input value with an
 * applicable pattern returns UNSUPPORTED rather than a misleading valid result;
 * pattern's bit is reserved until Unicode-v pattern validation is implemented. */
typedef enum weva_validity_error {
    WEVA_VALIDITY_VALUE_MISSING = 1u << 0,
    WEVA_VALIDITY_TYPE_MISMATCH = 1u << 1,
    WEVA_VALIDITY_PATTERN_MISMATCH = 1u << 2,
    WEVA_VALIDITY_TOO_LONG = 1u << 3,
    WEVA_VALIDITY_TOO_SHORT = 1u << 4,
    WEVA_VALIDITY_RANGE_UNDERFLOW = 1u << 5,
    WEVA_VALIDITY_RANGE_OVERFLOW = 1u << 6,
    WEVA_VALIDITY_STEP_MISMATCH = 1u << 7,
    WEVA_VALIDITY_BAD_INPUT = 1u << 8,
    WEVA_VALIDITY_CUSTOM_ERROR = 1u << 9
} weva_validity_error;
weva_status weva_element_validity(weva_document_t doc, weva_element_t element,
                                  uint32_t* errors, int* will_validate);
/* Explicit validation of a form or supported control; ignores novalidate and
 * formnovalidate. Queues cancelable INVALID events, drained through poll_event.
 * check never changes focus; report focuses the first unhandled invalid control
 * after handlers finish. No submission occurs. valid is required, cleared on
 * errors; cancellation does not turn false into true. Pattern-dependent checks
 * return UNSUPPORTED before queuing events. Calling during an active INVALID
 * handler returns INVALID_STATE (nested validation is not implemented). */
weva_status weva_element_check_validity(weva_document_t doc, weva_element_t element, int* valid);
weva_status weva_element_report_validity(weva_document_t doc, weva_element_t element, int* valid);

/* Dialog result property: initially empty; survives closing/reopening. These
 * property accesses do not mutate attributes or trigger layout. Getter returns
 * full UTF-8 byte length and copies a terminated prefix when capacity permits. */
size_t weva_element_dialog_return_value(weva_document_t doc, weva_element_t element,
                                      char* buffer, size_t capacity);
weva_status weva_element_set_dialog_return_value(weva_document_t doc, weva_element_t element,
                                                const char* value);
/* Null preserves the current result; a non-null empty string clears it.
 * Closing an already closed dialog does not change its result. A request's
 * result is copied and applied only when its cancel default actually closes. */
weva_status weva_element_close_dialog_with_value(weva_document_t doc, weva_element_t element,
                                                const char* value);
weva_status weva_element_request_close_dialog_with_value(weva_document_t doc, weva_element_t element,
                                                        const char* value);



/* Opens, closes or flips a popover -- an element with a `popover` attribute.
 * An open one joins the top layer and gets a `::backdrop`, exactly as a modal
 * dialog does.
 *
 * A host rarely needs these: a `<button popovertarget="menu">` works its
 * popover on its own, an `auto` popover closes when a click lands outside it,
 * and Escape closes the topmost one. They are here for a script that opens a
 * menu from something other than a click.
 *
 * Returns WEVA_ERR_NOT_FOUND for an element with no `popover` attribute.
 * Opening a modal dialog as a popover returns WEVA_ERR_INVALID_STATE. */
/* Queues BEFORE_TOGGLE while the popover is still closed. Drain poll_event;
 * prevent_default may veto opening. The next poll revalidates live element
 * state and opens only if it remains eligible. No core callback is invoked.
 * Immediate legacy operations below do not dispatch this request event.
 * Available since ABI minor 23. */
/* Opt in to queued opening requests for popovertarget activation. Off by
 * default for legacy C hosts; Godot enables it and drains input events. */
void weva_document_set_popover_request_events(weva_document_t doc, int enabled);
weva_status weva_element_request_show_popover(weva_document_t doc, weva_element_t element);
/* Closing dispatches non-cancellable BEFORE_TOGGLE while still open. */
weva_status weva_element_request_hide_popover(weva_document_t doc, weva_element_t element);
/* Selects the opening or closing request from the current state. */
weva_status weva_element_request_toggle_popover(weva_document_t doc, weva_element_t element);
/* The imperative half of the HTML popover API: open, close or flip a popover
 * now, without the request/BEFORE_TOGGLE round trip above.
 *
 * HOST-OPTIONAL, and deliberately so. Neither shipped host calls these --
 * both drive popovers declaratively through
 * weva_document_set_popover_request_events plus the popovertarget attribute
 * handler, which is what a page's own markup expects. They are here for a
 * host that wants to open a popover from game code (a tutorial step, a
 * controller shortcut) rather than from a click. Core tests cover all three.
 * Not calling them is a choice, not an omission. */
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

/* Unsupported at-rules in compiled stylesheet branches, one diagnostic
 * per unique name. Replaced on set_css; updated on viewport recompilation.
 * Returns required UTF-8 bytes excluding NUL. A provided nonempty buffer is
 * always terminated; truncation does not change the required size. No update,
 * layout or events are triggered. This is not a complete CSS support audit. */
size_t weva_document_css_diagnostics(weva_document_t doc, char* buffer, size_t capacity);

/* The HTML parse errors the last load_html / reload_html recovered from --
 * a stray or mismatched end tag, an end tag on a void element, an element
 * still open at the end of input -- one per line as "line:column: message",
 * positions 1-based in the markup as given. The document is still loaded;
 * these say where it may not be what the author meant. Same buffer
 * convention as css_diagnostics. Available since ABI minor 34. */
size_t weva_document_html_diagnostics(weva_document_t doc, char* buffer, size_t capacity);

/* ---- Change notification (minor 35) ------------------------------------
 *
 * What the last update changed, per element: every element whose computed
 * style came out different from the update before, with how far the change
 * reached -- its paint only, its layout, or which boxes exist. A tool that
 * highlights what a keystroke moved reads this after each update. An update
 * with nothing to do leaves the list of the last one that did work standing
 * (a host's own extra update between the change and the read loses
 * nothing); the tool tells the two apart by weva_document_draw_serial or by
 * remembering what it handled. Written into `out` up to `capacity`; returns
 * how many there are. */
typedef enum weva_change_kind {
    WEVA_CHANGE_PAINT = 1,
    WEVA_CHANGE_LAYOUT = 2,
    WEVA_CHANGE_BOXES = 3
} weva_change_kind;

typedef struct weva_element_change {
    weva_element_t element;
    int32_t kind;   /* weva_change_kind */
} weva_element_change;

size_t weva_document_changed_elements(weva_document_t doc, weva_element_change* out, size_t capacity);

/* A counter that moves whenever the element set changes -- a load or
 * reload, a mutation that adds or removes elements, a component expansion --
 * so a tool holding handles knows when to re-walk the tree. Handles that
 * survive a change keep their values. */
uint64_t weva_document_structure_version(weva_document_t doc);

/* `@font-face` rules from compiled stylesheet branches, one per line as
 * "family<TAB>source<TAB>font-weight<TAB>font-style<TAB>sources" in source
 * order; the weight and style fields are the descriptor texts and may be
 * empty. The source is the first url() entry resolved against the document
 * base path exactly like an image URL, so a host loads it through the same
 * asset convention, then calls weva_document_register_font_family with the
 * face it made; it is empty for a rule with only local() entries. The fifth
 * field (since ABI minor 37) is every src entry in the author's order,
 * separated by '|': "url:<resolved path>" or "local:<font name>" -- a host
 * tries them first to last and takes the first it can load, a local() name
 * being an installed font (CSS Fonts 4 §4.3). The core never loads fonts
 * itself. Same buffer convention as css_diagnostics; replaced on set_css and
 * viewport recompilation. Available since ABI minor 25. */
size_t weva_document_font_faces(weva_document_t doc, char* buffer, size_t capacity);

/* The layout dump the differential oracle compares (docs/ORACLE.md): the
 * same JSON the weva_dump tool writes, produced from this document's box
 * tree, so a host's dump is the tool's dump by construction. `source` is
 * the name recorded in the JSON. Valid after an update; the usual two-call
 * buffer convention (returns the required bytes excluding NUL). Available
 * since ABI minor 26. */
size_t weva_document_layout_dump(weva_document_t doc, const char* source, char* buffer,
                                 size_t capacity);

/* ---- Tooling (minor 27) -------------------------------------------------
 *
 * What an inspector needs beyond a selector query: the tree itself, the
 * rules that matched, every property an element resolved, and the box model
 * behind its border box. Added for the Unity host's editor panels; the Godot
 * host's inspector reads the same.
 */

/* The parent element, WEVA_ELEMENT_NONE for the root or an unknown handle. */
weva_element_t weva_element_parent(weva_document_t doc, weva_element_t element);

/* The element children in document order, written into `out` up to
 * `capacity`; returns how many there ARE (the two-call convention). Text
 * nodes are not listed: weva_element_text reads an element's text. */
size_t weva_element_children(weva_document_t doc, weva_element_t element, weva_element_t* out,
                             size_t capacity);

/* The four rectangles behind weva_element_bounds' border box, in document
 * pixels, into `out[16]`: margin edges top,right,bottom,left; border widths
 * top,right,bottom,left; padding top,right,bottom,left; then the content box
 * x,y,width,height. NOT_FOUND for an element without a box. */
weva_status weva_element_box_model(weva_document_t doc, weva_element_t element, double* out);

/* Every declaration that applies to the element -- the matched sheet rules
 * and the style attribute -- in cascade order (the LAST line for a property
 * is the one applied), one per line:
 *   origin<TAB>layer<TAB>specificity<TAB>source<TAB>inline<TAB>selector<TAB>property<TAB>value<TAB>important<TAB>applied
 * origin is ua|user|author, layer the ordinal (empty when unlayered),
 * specificity "a,b,c" (empty for the style attribute), source the rule order
 * within the document (-1 for the style attribute), inline 1 for the style
 * attribute, important 1 for !important, applied 1 when this is the winning
 * declaration for its property. Shorthands are listed expanded, as the
 * cascade applies them; selector and value are the source text. Same buffer
 * convention as css_diagnostics. */
size_t weva_element_matched_rules(weva_document_t doc, weva_element_t element, char* buffer,
                                  size_t capacity);

/* The element's whole computed style, one "property<TAB>value" line per
 * registered property in registry order (resolved through inheritance and
 * the initial-value table, as weva_element_computed_style resolves one),
 * then every custom property in scope. Same buffer convention. */
size_t weva_element_computed_style_all(weva_document_t doc, weva_element_t element, char* buffer,
                                       size_t capacity);

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
