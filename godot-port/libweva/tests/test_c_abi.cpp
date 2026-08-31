// Exercises the ABI the way a host does: through weva_c.h alone, with no C++
// type from the core in sight. If this file ever needs a libweva header, the
// seam has leaked.
#include "check.h"
#include "weva_c.h"

#include <cmath>
#include <cstring>
#include <vector>
#include <string>

namespace {

bool near(double a, double b, double eps = 1e-6) { return std::fabs(a - b) < eps; }

weva_config default_config(int w = 200, int h = 100) {
    weva_config c{};
    c.viewport_width = w;
    c.viewport_height = h;
    c.use_user_agent_stylesheet = 1;
    return c;
}

weva_status load(weva_document_t d, const char* html) {
    return weva_document_load_html(d, html, std::strlen(html));
}
weva_status add_css(weva_document_t d, const char* css) {
    return weva_document_add_css(d, css, std::strlen(css));
}

} // namespace

void test_abi_version_and_lifecycle() {
    // Every host checks this at load; a different major must make it refuse.
    const uint32_t v = weva_abi_version();
    CHECK((v >> 16) == WEVA_ABI_VERSION_MAJOR);
    CHECK((v & 0xFFFF) == WEVA_ABI_VERSION_MINOR);

    // A null config is valid and takes the defaults, so a host can get going
    // with one call.
    weva_document_t d = weva_document_create(nullptr);
    CHECK(d != nullptr);
    weva_document_destroy(d);
    // Destroying null is a no-op rather than a crash.
    weva_document_destroy(nullptr);

    // Every entry point tolerates a null document rather than faulting: a host
    // that failed to create one must not take the process down.
    CHECK(load(nullptr, "<div/>") == WEVA_ERR_INVALID_ARGUMENT);
    CHECK(add_css(nullptr, "a{}") == WEVA_ERR_INVALID_ARGUMENT);
    CHECK(weva_document_update(nullptr, 0) == WEVA_ERR_INVALID_ARGUMENT);
    weva_document_set_viewport(nullptr, 10, 10);
    size_t n = 99;
    CHECK(weva_document_draws(nullptr, &n) == nullptr && n == 0);
    CHECK(weva_document_query(nullptr, "div") == WEVA_ELEMENT_NONE);
    CHECK(weva_element_text(nullptr, 0, nullptr, 0) == 0);
}

void test_abi_load_and_update() {
    weva_config cfg = default_config();
    weva_document_t d = weva_document_create(&cfg);

    // Updating before any HTML is loaded reports not-found rather than
    // producing an empty frame that looks like success.
    CHECK(weva_document_update(d, 0) == WEVA_ERR_NOT_FOUND);

    CHECK(load(d, "<body><div id=a>Hi</div></body>") == WEVA_OK);
    CHECK(add_css(d, "#a { display: block; width: 50px; height: 20px;"
                     "     background-color: #ff0000 }") == WEVA_OK);
    CHECK(weva_document_update(d, 0) == WEVA_OK);

    size_t count = 0;
    const weva_draw* draws = weva_document_draws(d, &count);
    CHECK(count > 0 && draws != nullptr);
    for (size_t i = 0; i < count; ++i) {
        CHECK(draws[i].vertex_count > 0);
        CHECK(draws[i].index_count > 0 && draws[i].index_count % 3 == 0);
        // Every index is in range: a host uploading these must not fault.
        for (size_t k = 0; k < draws[i].index_count; ++k) {
            CHECK(draws[i].indices[k] < draws[i].vertex_count);
        }
    }
    // The text run is drawn from the glyph atlas, so at least one draw is
    // textured and the texture is published alongside it.
    bool textured = false;
    for (size_t i = 0; i < count; ++i) {
        if (draws[i].texture_id != 0) textured = true;
    }
    CHECK(textured);
    size_t tex_count = 0;
    const weva_texture* textures = weva_document_textures(d, &tex_count);
    CHECK(tex_count > 0 && textures != nullptr);
    CHECK(textures[0].width > 0 && textures[0].height > 0 && textures[0].rgba != nullptr);

    // The length is explicit, so a host never has to null-terminate. A
    // non-terminated buffer with a length works.
    const char html[] = "<div/>EXTRA";
    CHECK(weva_document_load_html(d, html, 6) == WEVA_OK);
    CHECK(weva_document_update(d, 0) == WEVA_OK);

    weva_document_destroy(d);
}

void test_abi_query_and_bounds() {
    weva_config cfg = default_config();
    weva_document_t d = weva_document_create(&cfg);
    CHECK(load(d, "<body><div id=a></div><div id=b class=x></div></body>") == WEVA_OK);
    CHECK(add_css(d, "div { display: block; height: 30px }"
                     "#b { margin-left: 12px; width: 40px }") == WEVA_OK);
    CHECK(weva_document_update(d, 0) == WEVA_OK);

    const weva_element_t a = weva_document_query(d, "#a");
    const weva_element_t b = weva_document_query(d, ".x");
    CHECK(a != WEVA_ELEMENT_NONE && b != WEVA_ELEMENT_NONE && a != b);
    CHECK(weva_document_query(d, "#nope") == WEVA_ELEMENT_NONE);
    // A malformed selector is a miss, not a crash.
    CHECK(weva_document_query(d, "###") == WEVA_ELEMENT_NONE);

    double x = 0, y = 0, w = 0, h = 0;
    CHECK(weva_element_bounds(d, b, &x, &y, &w, &h) == WEVA_OK);
    CHECK(near(x, 12) && near(y, 30));
    CHECK(near(w, 40) && near(h, 30));
    // A stale or out-of-range handle is rejected — the reason handles are
    // indices rather than pointers.
    CHECK(weva_element_bounds(d, 99999, &x, &y, &w, &h) == WEVA_ERR_NOT_FOUND);
    CHECK(weva_element_bounds(d, WEVA_ELEMENT_NONE, &x, &y, &w, &h) == WEVA_ERR_NOT_FOUND);
    // Every out-parameter is optional.
    CHECK(weva_element_bounds(d, a, nullptr, nullptr, nullptr, nullptr) == WEVA_OK);

    weva_document_destroy(d);
}

void test_abi_attributes_and_text() {
    weva_config cfg = default_config();
    weva_document_t d = weva_document_create(&cfg);
    CHECK(load(d, "<body><div id=a>one <span>two</span></div></body>") == WEVA_OK);
    CHECK(add_css(d, "div { display: block; height: 10px }"
                     "[data-hide] { display: none }") == WEVA_OK);
    CHECK(weva_document_update(d, 0) == WEVA_OK);

    const weva_element_t a = weva_document_query(d, "#a");
    CHECK(a != WEVA_ELEMENT_NONE);

    // Text is gathered depth-first across descendants, in document order.
    const size_t len = weva_element_text(d, a, nullptr, 0);
    CHECK(len == 7);
    char buf[32];
    CHECK(weva_element_text(d, a, buf, sizeof(buf)) == len);
    CHECK(std::string(buf) == "one two");
    // A short buffer truncates and still null-terminates, and the return value
    // is the length that WOULD have been written — so one call sizes and a
    // second fills.
    char small[4];
    CHECK(weva_element_text(d, a, small, sizeof(small)) == len);
    CHECK(std::string(small) == "one");
    // A zero-capacity call is the sizing call.
    CHECK(weva_element_text(d, a, buf, 0) == len);

    // Setting an attribute restyles on the next update: the div had a box, and
    // after `data-hide` it has none.
    double x, y, w, h;
    CHECK(weva_element_bounds(d, a, &x, &y, &w, &h) == WEVA_OK);
    CHECK(weva_element_set_attribute(d, a, "data-hide", "1") == WEVA_OK);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(weva_element_bounds(d, a, &x, &y, &w, &h) == WEVA_ERR_NOT_FOUND);
    // Removing it brings the box back.
    CHECK(weva_element_set_attribute(d, a, "data-hide", nullptr) == WEVA_OK);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(weva_element_bounds(d, a, &x, &y, &w, &h) == WEVA_OK);

    CHECK(weva_element_set_attribute(d, 99999, "x", "y") == WEVA_ERR_NOT_FOUND);
    CHECK(weva_element_set_attribute(d, a, nullptr, "y") == WEVA_ERR_INVALID_ARGUMENT);

    weva_document_destroy(d);
}

void test_abi_viewport_and_restyle() {
    weva_config cfg = default_config(200, 100);
    weva_document_t d = weva_document_create(&cfg);
    CHECK(load(d, "<body><div id=a></div></body>") == WEVA_OK);
    CHECK(add_css(d, "#a { display: block; width: 50%; height: 10px }") == WEVA_OK);
    CHECK(weva_document_update(d, 0) == WEVA_OK);

    double x, y, w, h;
    CHECK(weva_element_bounds(d, weva_document_query(d, "#a"), &x, &y, &w, &h) == WEVA_OK);
    CHECK(near(w, 100));

    // A viewport change takes effect on the next update, not immediately —
    // a host resizing mid-frame must not see a half-updated document.
    weva_document_set_viewport(d, 400, 100);
    CHECK(weva_element_bounds(d, weva_document_query(d, "#a"), &x, &y, &w, &h) == WEVA_OK);
    CHECK(near(w, 100));
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(weva_element_bounds(d, weva_document_query(d, "#a"), &x, &y, &w, &h) == WEVA_OK);
    CHECK(near(w, 200));

    // A zero or negative viewport is ignored rather than producing a degenerate
    // layout.
    weva_document_set_viewport(d, 0, 0);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(weva_element_bounds(d, weva_document_query(d, "#a"), &x, &y, &w, &h) == WEVA_OK);
    CHECK(near(w, 200));

    weva_document_destroy(d);
}

// ---- Host-supplied backends ---------------------------------------------
//
// A host implements these tables. Written here in C style, through weva_c.h
// alone, because that is how a GDExtension will write them.

namespace {

struct HostRenderState {
    int compiles = 0, renders = 0, releases = 0, textures = 0, scissors = 0;
    size_t last_vertex_count = 0;
    float last_translate_x = 0;
    uint64_t next = 1;
    // Every handle the host issued must come back, or the host leaks.
    int live_geometry = 0;
};

uint64_t host_compile(void* ud, const weva_vertex* v, size_t vn, const uint32_t* i, size_t in) {
    auto* s = static_cast<HostRenderState*>(ud);
    ++s->compiles;
    ++s->live_geometry;
    s->last_vertex_count = vn;
    // The host reads the buffer directly: the vertex layout is part of the ABI.
    CHECK(v != nullptr && vn > 0);
    CHECK(i != nullptr && in > 0);
    return s->next++;
}
void host_render(void* ud, uint64_t, float tx, float, uint64_t) {
    auto* s = static_cast<HostRenderState*>(ud);
    ++s->renders;
    s->last_translate_x = tx;
}
void host_release(void* ud, uint64_t) {
    auto* s = static_cast<HostRenderState*>(ud);
    ++s->releases;
    --s->live_geometry;
}
uint64_t host_gen_texture(void* ud, const uint8_t*, int32_t, int32_t) {
    auto* s = static_cast<HostRenderState*>(ud);
    ++s->textures;
    return s->next++;
}
void host_set_scissor(void* ud, int32_t, int32_t, int32_t, int32_t, int32_t) {
    ++static_cast<HostRenderState*>(ud)->scissors;
}

struct HostFontState {
    int shapes = 0, rasterizes = 0;
};

int32_t host_face_metrics(void*, uint64_t, double px, double* asc, double* desc,
                          double* gap) {
    // Deliberately unlike the stub's 0.8/0.4/0.0, so a test can tell which
    // backend measurement went through.
    *asc = px * 0.9;
    *desc = px * 0.3;
    *gap = px * 0.1;
    return 1;
}

int32_t host_glyph_index(void*, uint64_t, uint32_t cp, uint32_t* out) {
    // Every code point maps to itself, so the host's own ids flow through.
    *out = cp;
    return 1;
}
int32_t host_glyph_metrics(void*, uint64_t, uint32_t, double px, double* adv, double* bx,
                           double* by, int32_t* w, int32_t* h) {
    *adv = px;            // a full em per glyph, distinct from the stub's half
    *bx = 0;
    *by = px;
    *w = static_cast<int32_t>(px);
    *h = static_cast<int32_t>(px);
    return 1;
}
int32_t host_rasterize(void* ud, uint64_t, uint32_t, double px, weva_glyph_bitmap* out) {
    static std::vector<uint8_t> pixels;
    ++static_cast<HostFontState*>(ud)->rasterizes;
    const int n = static_cast<int>(px);
    pixels.assign(static_cast<size_t>(n) * n, 200);
    out->alpha = pixels.data();
    out->width = n;
    out->height = n;
    return 1;
}
size_t host_shape(void* ud, uint64_t, const char* utf8, size_t len, double px, uint32_t* glyphs,
                  double* advances, uint32_t* clusters, size_t capacity) {
    ++static_cast<HostFontState*>(ud)->shapes;
    // One glyph per byte, which is enough to prove the two-call sizing works.
    if (glyphs && capacity >= len) {
        for (size_t i = 0; i < len; ++i) {
            glyphs[i] = static_cast<unsigned char>(utf8[i]);
            advances[i] = px;
            clusters[i] = static_cast<uint32_t>(i);
        }
    }
    return len;
}

} // namespace

void test_abi_host_render_backend() {
    weva_config cfg = default_config();
    weva_document_t d = weva_document_create(&cfg);

    HostRenderState state;
    weva_render_backend rb{};
    rb.user_data = &state;
    rb.compile_geometry = host_compile;
    rb.render_geometry = host_render;
    rb.release_geometry = host_release;
    rb.generate_texture = host_gen_texture;
    rb.set_scissor = host_set_scissor;
    weva_document_set_render_backend(d, &rb);

    CHECK(load(d, "<body><div id=a>x</div></body>") == WEVA_OK);
    CHECK(add_css(d, "#a { display: block; width: 20px; height: 10px;"
                     "     background-color: #00ff00 }") == WEVA_OK);
    CHECK(weva_document_update(d, 0) == WEVA_OK);

    // The host issued the draws, so the core's own collected list is empty —
    // exactly one of the two paths runs, never both.
    CHECK(state.compiles > 0);
    CHECK(state.renders == state.compiles);
    CHECK(state.last_vertex_count > 0);
    size_t n = 99;
    CHECK(weva_document_draws(d, &n) == nullptr && n == 0);
    // Every handle the host issued came back.
    CHECK(state.live_geometry == 0 && state.releases == state.compiles);
    // The glyph atlas went through the host's texture path too.
    CHECK(state.textures > 0);

    // Passing null restores the built-in, and the collected list reappears.
    weva_document_set_render_backend(d, nullptr);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(weva_document_draws(d, &n) != nullptr && n > 0);

    weva_document_destroy(d);
}

void test_abi_partial_backend_degrades() {
    // A table with only some functions filled in must fall back for the rest
    // rather than crashing — that is what lets a host adopt these one at a
    // time.
    weva_config cfg = default_config();
    weva_document_t d = weva_document_create(&cfg);

    HostRenderState state;
    weva_render_backend rb{};
    rb.user_data = &state;
    // Only render_geometry is supplied: compile, release, textures and scissor
    // all fall through to the built-in.
    rb.render_geometry = host_render;
    weva_document_set_render_backend(d, &rb);

    CHECK(load(d, "<body><div id=a></div></body>") == WEVA_OK);
    CHECK(add_css(d, "#a { display: block; width: 10px; height: 10px;"
                     "     background-color: #123456 }") == WEVA_OK);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(state.renders > 0);
    CHECK(state.compiles == 0);

    // An entirely empty table is also safe: every call falls back.
    weva_render_backend empty{};
    weva_document_set_render_backend(d, &empty);
    CHECK(weva_document_update(d, 0) == WEVA_OK);

    weva_document_destroy(d);
}

void test_abi_host_font_backend() {
    weva_config cfg = default_config(400, 100);
    weva_document_t d = weva_document_create(&cfg);

    HostFontState state;
    weva_font_backend fb{};
    fb.user_data = &state;
    fb.face_metrics = host_face_metrics;
    fb.glyph_index = host_glyph_index;
    fb.glyph_metrics = host_glyph_metrics;
    fb.rasterize = host_rasterize;
    fb.shape = host_shape;
    weva_document_set_font_backend(d, &fb, 7);

    CHECK(load(d, "<body><div id=a>abc</div></body>") == WEVA_OK);
    CHECK(add_css(d, "#a { display: block; font-size: 10px }") == WEVA_OK);
    CHECK(weva_document_update(d, 0) == WEVA_OK);

    // The host's shaper and rasterizer both ran, and the sizing call happened
    // before the filling one.
    CHECK(state.shapes >= 2);
    CHECK(state.rasterizes > 0);

    // MEASUREMENT follows the host's face too, not just rendering. The host
    // gives a full em per glyph and a 1.3em line where the stub gives half an
    // em and 1.2 — so "abc" at 10px is 30px wide on a 13px line, where the
    // stub would give 15px on 12px. Without this the text would be laid out to
    // one face's advances and drawn with another's.
    double x = 0, y = 0, w = 0, h = 0;
    CHECK(weva_element_bounds(d, weva_document_query(d, "#a"), &x, &y, &w, &h) == WEVA_OK);
    CHECK(near(h, 13));

    // Restoring the stub is a null table away, and measurement goes back with
    // it.
    weva_document_set_font_backend(d, nullptr, 0);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(weva_element_bounds(d, weva_document_query(d, "#a"), &x, &y, &w, &h) == WEVA_OK);
    CHECK(near(h, 12));

    weva_document_destroy(d);
}
