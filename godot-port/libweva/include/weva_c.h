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

/* Runs cascade, layout and paint. `dt_seconds` advances animations; pass 0 for
 * a static document. */
weva_status weva_document_update(weva_document_t doc, double dt_seconds);

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

/* Sets an attribute, which restyles on the next update. A null value removes
 * it. */
weva_status weva_element_set_attribute(weva_document_t doc, weva_element_t element,
                                       const char* name, const char* value);

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
