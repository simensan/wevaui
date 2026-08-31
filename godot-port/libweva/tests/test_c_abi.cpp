// Exercises the ABI the way a host does: through weva_c.h alone, with no C++
// type from the core in sight. If this file ever needs a libweva header, the
// seam has leaked.
#include "check.h"
#include "weva_c.h"

#include <cmath>
#include <cstring>
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
