// Exercises the ABI the way a host does: through weva_c.h alone, with no C++
// type from the core in sight. If this file ever needs a libweva header, the
// seam has leaked.
#include "check.h"
#include "weva_c.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <set>
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

void test_abi_field_selection_memory() {
    auto config = default_config();
    auto d = weva_document_create(&config);
    CHECK(load(d,"<input id=a value=abcdef><input id=b value=second><button id=c>Apply</button>") == WEVA_OK);
    CHECK(weva_document_update(d,0) == WEVA_OK);
    const auto a=weva_document_query(d,"#a"), b=weva_document_query(d,"#b"), c=weva_document_query(d,"#c");
    const auto selection = [&](weva_element_t e,int anchor,int caret) {
        int start=-1,end=-1;
        CHECK(weva_element_selection(d,e,&start,&end) == WEVA_OK);
        CHECK(start == anchor && end == caret);
    };
    CHECK(weva_document_set_focus(d,a) == WEVA_OK);
    selection(a,0,0);
    CHECK(weva_element_set_value(d,b,"replacement") == WEVA_OK);
    selection(b,11,11);
    CHECK(weva_element_set_selection(d,a,4,1) == WEVA_OK);
    CHECK(weva_document_set_focus(d,b) == WEVA_OK);
    selection(a,4,1);
    CHECK(weva_document_set_focus(d,a) == WEVA_OK);
    selection(a,4,1);
    CHECK(weva_document_set_focus(d,c) == WEVA_OK);
    selection(a,4,1);
    CHECK(weva_document_set_focus(d,a) == WEVA_OK);
    selection(a,4,1);
    CHECK(weva_document_set_focus(d,b) == WEVA_OK);
    CHECK(weva_element_set_value(d,a,"xy") == WEVA_OK);
    selection(a,2,2);
    CHECK(weva_document_set_focus(d,a) == WEVA_OK);
    selection(a,2,2);
    CHECK(weva_element_set_selection(d,a,0,1) == WEVA_OK);
    CHECK(weva_document_set_focus(d,b) == WEVA_OK);
    CHECK(weva_element_set_value(d,a,"xy") == WEVA_OK);
    selection(a,0,1); // No-op value assignments preserve selection.
    CHECK(weva_element_set_selection_without_focus(d,a,2,0) == WEVA_OK);
    CHECK(weva_document_focus(d) == b);
    selection(a,2,0);
    CHECK(weva_document_set_focus(d,a) == WEVA_OK);
    selection(a,2,0);
    CHECK(weva_document_set_focus(d,b) == WEVA_OK);
    CHECK(weva_element_set_attribute(d,a,"disabled","") == WEVA_OK);
    CHECK(weva_element_set_selection_without_focus(d,a,-20,200) == WEVA_OK);
    CHECK(weva_document_focus(d) == b);
    selection(a,0,2);
    CHECK(weva_element_set_selection_without_focus(d,c,0,1) == WEVA_ERR_INVALID_ARGUMENT);
    CHECK(load(d,"<input id=a value=abcdef>") == WEVA_OK);
    selection(weva_document_query(d,"#a"),0,0);
    CHECK(load(d,"<button id=start>Start</button><input id=a value=abcdef><textarea id=t>abcdef</textarea>") == WEVA_OK);
    CHECK(weva_document_update(d,0) == WEVA_OK);
    CHECK(weva_document_set_focus(d,weva_document_query(d,"#start")) == WEVA_OK);
    CHECK(weva_document_focus_next(d,0) == weva_document_query(d,"#a"));
    selection(weva_document_query(d,"#a"),0,6);
    CHECK(weva_document_focus_next(d,0) == weva_document_query(d,"#t"));
    selection(weva_document_query(d,"#t"),0,0);
    weva_document_destroy(d);
}

void test_abi_selection_default_lifecycle() {
    // Chrome 152: text inputs and textareas, pristine/dirty, focused/blurred.
    struct Case { bool textarea, dirty, focused; const char* action; const char* value; int anchor, caret; const char* replacement = nullptr; };
    const Case cases[] = {
        {false, false, false, "reset", "abcdef", 4, 1},
        {false, false, false, "default", "new default", 4, 1},
        {false, false, false, "same-default", "abcdef", 4, 1},
        {false, false, false, "attribute", "new attribute", 4, 1},
        {false, false, true, "reset", "abcdef", 4, 1},
        {false, false, true, "default", "new default", 0, 0},
        {false, false, true, "same-default", "abcdef", 4, 1},
        {false, false, true, "attribute", "new attribute", 0, 0},
        {false, true, false, "reset", "abcdef", 6, 6},
        {false, true, false, "default", "edited", 4, 1},
        {false, true, false, "same-default", "edited", 4, 1},
        {false, true, false, "attribute", "edited", 4, 1},
        {false, true, true, "reset", "abcdef", 6, 6},
        {false, true, true, "default", "edited", 4, 1},
        {false, true, true, "same-default", "edited", 4, 1},
        {false, true, true, "attribute", "edited", 4, 1},
        {true, false, false, "reset", "abcdef", 4, 1},
        {true, false, false, "default", "new default", 0, 0},
        {true, false, false, "same-default", "abcdef", 0, 0},
        {true, false, false, "attribute", "abcdef", 4, 1},
        {true, false, true, "reset", "abcdef", 4, 1},
        {true, false, true, "default", "new default", 0, 0},
        {true, false, true, "same-default", "abcdef", 0, 0},
        {true, false, true, "attribute", "abcdef", 4, 1},
        {true, true, false, "reset", "abcdef", 6, 6},
        {true, true, false, "default", "edited", 4, 1},
        {true, true, false, "same-default", "edited", 4, 1},
        {true, true, false, "attribute", "edited", 4, 1},
        {true, true, true, "reset", "abcdef", 6, 6},
        {true, true, true, "default", "edited", 4, 1},
        {true, true, true, "same-default", "edited", 4, 1},
        {true, true, true, "attribute", "edited", 4, 1},
        {false, false, false, "default", "x", 4, 1, "x"},
        {false, false, false, "attribute", "x", 4, 1, "x"},
        {false, false, true, "default", "x", 0, 0, "x"},
        {false, false, true, "attribute", "x", 0, 0, "x"},
        {false, true, false, "default", "edited", 4, 1, "x"},
        {false, true, false, "attribute", "edited", 4, 1, "x"},
        {false, true, true, "default", "edited", 4, 1, "x"},
        {false, true, true, "attribute", "edited", 4, 1, "x"},
        {true, false, false, "default", "x", 0, 0, "x"},
        {true, false, false, "attribute", "abcdef", 4, 1, "x"},
        {true, false, true, "default", "x", 0, 0, "x"},
        {true, false, true, "attribute", "abcdef", 4, 1, "x"},
        {true, true, false, "default", "edited", 4, 1, "x"},
        {true, true, false, "attribute", "edited", 4, 1, "x"},
        {true, true, true, "default", "edited", 4, 1, "x"},
        {true, true, true, "attribute", "edited", 4, 1, "x"},
        {false, false, false, "default", "", 4, 1, ""},
        {false, false, false, "attribute", "", 4, 1, ""},
        {false, false, true, "default", "", 0, 0, ""},
        {false, false, true, "attribute", "", 0, 0, ""},
        {false, true, false, "default", "edited", 4, 1, ""},
        {false, true, false, "attribute", "edited", 4, 1, ""},
        {false, true, true, "default", "edited", 4, 1, ""},
        {false, true, true, "attribute", "edited", 4, 1, ""},
        {true, false, false, "default", "", 0, 0, ""},
        {true, false, false, "attribute", "abcdef", 4, 1, ""},
        {true, false, true, "default", "", 0, 0, ""},
        {true, false, true, "attribute", "abcdef", 4, 1, ""},
        {true, true, false, "default", "edited", 4, 1, ""},
        {true, true, false, "attribute", "edited", 4, 1, ""},
        {true, true, true, "default", "edited", 4, 1, ""},
        {true, true, true, "attribute", "edited", 4, 1, ""},
        {false, false, false, "default", "abcdef", 4, 1, "abcdef\n"},
        {false, false, false, "attribute", "abcdef", 4, 1, "abcdef\n"},
        {false, false, true, "default", "abcdef", 4, 1, "abcdef\n"},
        {false, false, true, "attribute", "abcdef", 4, 1, "abcdef\n"},
        {false, true, false, "default", "edited", 4, 1, "abcdef\n"},
        {false, true, false, "attribute", "edited", 4, 1, "abcdef\n"},
        {false, true, true, "default", "edited", 4, 1, "abcdef\n"},
        {false, true, true, "attribute", "edited", 4, 1, "abcdef\n"},
        {true, false, false, "default", "abcdef\n", 0, 0, "abcdef\n"},
        {true, false, false, "attribute", "abcdef", 4, 1, "abcdef\n"},
        {true, false, true, "default", "abcdef\n", 0, 0, "abcdef\n"},
        {true, false, true, "attribute", "abcdef", 4, 1, "abcdef\n"},
        {true, true, false, "default", "edited", 4, 1, "abcdef\n"},
        {true, true, false, "attribute", "edited", 4, 1, "abcdef\n"},
        {true, true, true, "default", "edited", 4, 1, "abcdef\n"},
        {true, true, true, "attribute", "edited", 4, 1, "abcdef\n"},
    };
    for (const auto& c : cases) {
        auto config = default_config();
        auto d = weva_document_create(&config);
        CHECK(load(d, c.textarea ? "<form id=f><textarea id=a>abcdef</textarea><button id=b type=button>Apply</button></form>"
                                : "<form id=f><input id=a value=abcdef><button id=b type=button>Apply</button></form>") == WEVA_OK);
        CHECK(weva_document_update(d, 0) == WEVA_OK);
        const auto a = weva_document_query(d, "#a"), b = weva_document_query(d, "#b");
        if (c.dirty) CHECK(weva_element_set_value(d, a, "edited") == WEVA_OK);
        CHECK(weva_element_set_selection(d, a, 4, 1) == WEVA_OK);
        if (!c.focused) CHECK(weva_document_set_focus(d, b) == WEVA_OK);
        if (std::strcmp(c.action, "reset") == 0) {
            CHECK(weva_document_reset_form(d, weva_document_query(d, "#f")) == WEVA_OK);
        } else if (std::strcmp(c.action, "attribute") == 0) {
            CHECK(weva_element_set_attribute(d, a, "value", c.replacement ? c.replacement : "new attribute") == WEVA_OK);
        } else {
            const char* value = std::strcmp(c.action, "same-default") == 0 ? "abcdef" : (c.replacement ? c.replacement : "new default");
            if (c.textarea) CHECK(weva_element_set_text(d, a, value) == WEVA_OK);
            else CHECK(weva_element_set_attribute(d, a, "value", value) == WEVA_OK);
        }
        char value[128]{};
        weva_element_value(d, a, value, sizeof(value));
        int anchor = -1, caret = -1;
        CHECK(weva_element_selection(d, a, &anchor, &caret) == WEVA_OK);
        if (std::strcmp(value, c.value) != 0 || anchor != c.anchor || caret != c.caret)
            std::fprintf(stderr, "selection lifecycle textarea=%d dirty=%d focused=%d action=%s value=%s selection=%d,%d expected=%d,%d\n", c.textarea,c.dirty,c.focused,c.action,value,anchor,caret,c.anchor,c.caret);
        CHECK(std::strcmp(value, c.value) == 0);
        CHECK(anchor == c.anchor && caret == c.caret);
        CHECK(weva_document_focus(d) == (c.focused ? a : b));
        CHECK(weva_document_update(d, 0) == WEVA_OK);
        CHECK(weva_element_selection(d, a, &anchor, &caret) == WEVA_OK);
        CHECK(anchor == c.anchor && caret == c.caret);
        weva_document_destroy(d);
    }
}

void test_abi_stylesheet_replacement() {
    {
        const auto config = default_config();
        auto document = weva_document_create(&config);
        const char* css = "@page {margin:0}"
                          "@page :first {margin:1cm}"
                          "@media (min-width: 4000px) {@future {div{color:red}}}"
                          "@keyframes fade {from{opacity:0}to{opacity:1}}";
        CHECK(weva_document_set_css(document, css, std::strlen(css)) == WEVA_OK);
        const std::string expected = "Ignored @page: unsupported stylesheet rule.";
        CHECK(weva_document_css_diagnostics(document, nullptr, 0) == expected.size());
        std::vector<char> text(expected.size() + 1);
        CHECK(weva_document_css_diagnostics(document, text.data(), text.size()) == expected.size());
        CHECK(std::string(text.data()) == expected);
        char small[4] = {'x','x','x','x'};
        CHECK(weva_document_css_diagnostics(document, small, sizeof(small)) == expected.size());
        CHECK(std::string(small) == "Ign");
        CHECK(weva_document_css_diagnostics(nullptr, small, sizeof(small)) == 0);
        CHECK(small[0] == '\0');
        weva_document_set_viewport(document, 4096, 720);
        const size_t expanded = weva_document_css_diagnostics(document, nullptr, 0);
        text.resize(expanded + 1);
        weva_document_css_diagnostics(document, text.data(), text.size());
        CHECK(std::string(text.data()) == expected + "\nIgnored @future: unsupported stylesheet rule.");
        CHECK(weva_document_set_css(document, nullptr, 0) == WEVA_OK);
        CHECK(weva_document_css_diagnostics(document, small, sizeof(small)) == 0);
        CHECK(small[0] == '\0');
        weva_document_destroy(document);
    }
    {
        // Tag names come back through the same buffer protocol as attributes.
        const auto config = default_config();
        auto document = weva_document_create(&config);
        const char* html = "<body><input id=f type=text><button id=b>Go</button></body>";
        CHECK(weva_document_load_html(document, html, std::strlen(html)) == WEVA_OK);
        CHECK(weva_document_update(document, 0) == WEVA_OK);
        const weva_element_t field = weva_document_focus_next(document, 0);
        CHECK(field != WEVA_ELEMENT_NONE);
        char tag[8] = {'x', 'x', 'x', 'x', 'x', 'x', 'x', 'x'};
        CHECK(weva_element_tag_name(document, field, tag, sizeof(tag)) == 5);
        CHECK(std::string(tag) == "input");
        const weva_element_t button = weva_document_focus_next(document, 0);
        CHECK(weva_element_tag_name(document, button, nullptr, 0) == 6);
        char small[3] = {'x', 'x', 'x'};
        CHECK(weva_element_tag_name(document, button, small, sizeof(small)) == 6);
        CHECK(std::string(small) == "bu");
        CHECK(weva_element_tag_name(document, WEVA_ELEMENT_NONE, small, sizeof(small)) == 0);
        CHECK(small[0] == '\0');
        CHECK(weva_element_tag_name(nullptr, button, small, sizeof(small)) == 0);
        weva_document_destroy(document);
    }
    {
        // @font-face is parsed, not ignored: the host loads what it lists.
        const auto config = default_config();
        auto document = weva_document_create(&config);
        CHECK(weva_document_set_base_path(document, "ui/") == WEVA_OK);
        const char* css = "@font-face { font-family: \"Camp Display\"; src: local(Camp), url('fonts/camp.ttf') format('truetype'), url(camp.woff2); font-weight: 700; font-style: italic }"
                          "@font-face{font-family:Mono;src:url(mono.ttf)}"
                          "@font-face{font-family:Mono;src:url(mono.ttf)}"
                          "@font-face{font-family:NoSource;src:local(Nope)}"
                          "@media (min-width: 4000px) {@font-face{font-family:Wide;src:url(/abs/wide.ttf)}}"
                          "@font-face{font-family:Rel;src:url(../shared/rel.ttf)}";
        CHECK(weva_document_set_css(document, css, std::strlen(css)) == WEVA_OK);
        CHECK(weva_document_css_diagnostics(document, nullptr, 0) == 0);
        const std::string expected = "Camp Display\tui/fonts/camp.ttf\t700\titalic\nMono\tui/mono.ttf\t\t\nRel\tui/../shared/rel.ttf\t\t";
        CHECK(weva_document_font_faces(document, nullptr, 0) == expected.size());
        std::vector<char> text(expected.size() + 1);
        CHECK(weva_document_font_faces(document, text.data(), text.size()) == expected.size());
        CHECK(std::string(text.data()) == expected);
        char small[4] = {'x','x','x','x'};
        CHECK(weva_document_font_faces(document, small, sizeof(small)) == expected.size());
        CHECK(std::string(small) == "Cam");
        CHECK(weva_document_font_faces(nullptr, small, sizeof(small)) == 0);
        CHECK(small[0] == '\0');
        weva_document_set_viewport(document, 4096, 720);
        const size_t expanded = weva_document_font_faces(document, nullptr, 0);
        text.resize(expanded + 1);
        weva_document_font_faces(document, text.data(), text.size());
        CHECK(std::string(text.data()) == "Camp Display\tui/fonts/camp.ttf\t700\titalic\nMono\tui/mono.ttf\t\t\nWide\t/abs/wide.ttf\t\t\nRel\tui/../shared/rel.ttf\t\t");
        CHECK(weva_document_set_css(document, nullptr, 0) == WEVA_OK);
        CHECK(weva_document_font_faces(document, small, sizeof(small)) == 0);
        weva_document_destroy(document);
    }
    const auto cfg = default_config();
    auto d = weva_document_create(&cfg);
    const auto set_css = [&](const char* css) {
        CHECK(weva_document_set_css(d, css, std::strlen(css)) == WEVA_OK);
        CHECK(weva_document_update(d, 0) == WEVA_OK);
    };
    const auto width = [&](weva_element_t box) {
        double w = 0;
        CHECK(weva_element_bounds(d, box, nullptr, nullptr, &w, nullptr) == WEVA_OK);
        return w;
    };
    const auto height = [&](weva_element_t box) {
        double h = 0;
        CHECK(weva_element_bounds(d, box, nullptr, nullptr, nullptr, &h) == WEVA_OK);
        return h;
    };
    CHECK(load(d, "<div id=box></div><input id=name value=Ada>") == WEVA_OK);
    CHECK(add_css(d, "#box{width:80px;height:30px}") == WEVA_OK);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    const auto box = weva_document_query(d, "#box");
    const auto field = weva_document_query(d, "#name");
    CHECK(near(width(box), 80));
    // Appending a sheet to a settled document must trigger the lifecycle,
    // while retaining declarations from previous author sheets.
    CHECK(add_css(d, "#box{width:60px}") == WEVA_OK);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(near(width(box), 60) && near(height(box), 30));
    CHECK(weva_document_set_focus(d, field) == WEVA_OK);
    CHECK(weva_element_set_value(d, field, "Grace") == WEVA_OK);
    CHECK(weva_element_set_selection(d, field, 1, 4) == WEVA_OK);
    set_css("#box{width:40px}");
    CHECK(near(width(box), 40) && near(height(box), 0));
    CHECK(weva_document_query(d, "#name") == field);
    CHECK(weva_document_focus(d) == field);
    char value[32] = {};
    weva_element_value(d, field, value, sizeof(value));
    CHECK(std::string(value) == "Grace");
    int start = 0, end = 0;
    CHECK(weva_element_selection(d, field, &start, &end) == WEVA_OK);
    CHECK(start == 1 && end == 4);
    CHECK(weva_document_set_css(d, nullptr, 1) == WEVA_ERR_INVALID_ARGUMENT);
    CHECK(weva_document_set_css(nullptr, "", 0) == WEVA_ERR_INVALID_ARGUMENT);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(near(width(box), 40));
    CHECK(weva_document_set_css(d, nullptr, 0) == WEVA_OK);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(width(box) > 100); // UA block rule survives, author width disappears

    // Old registrations and generated content must leave with their sheet.
    set_css("@property --measure{syntax:'<length>';initial-value:30px;inherits:false}"
            "#box{width:var(--measure,70px)}#box::before{content:'label';display:block;height:20px}");
    CHECK(near(width(box), 30) && near(height(box), 20));
    set_css("#box{width:var(--measure,70px)}");
    CHECK(near(width(box), 70) && near(height(box), 0));

    set_css("#box{width:7px;animation:grow 1s linear infinite}"
            "@keyframes grow{from{width:10px}to{width:110px}}");
    CHECK(weva_document_update(d, .25) == WEVA_OK);
    CHECK(near(width(box), 35));
    set_css("#box{width:7px;animation:grow 1s linear infinite}"
            "@keyframes grow{from{width:50px}to{width:150px}}");
    CHECK(near(width(box), 75)); // same animation's clock survives the new rules
    set_css("#box{width:7px;animation:grow 1s linear infinite}");
    CHECK(near(width(box), 7)); // removed keyframes cannot keep animating
    for (int i = 0; i < 40; ++i) {
        set_css(i % 2 ? "#box{width:22px}" : "#box{width:11px;height:9px}");
        CHECK(near(width(box), i % 2 ? 22 : 11));
        CHECK(near(height(box), i % 2 ? 0 : 9));
    }
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

    CHECK(add_css(d, "@media(max-width:250px){#a{width:73px}}"
                     "@media(orientation:portrait){#a{height:27px}}") == WEVA_OK);
    for (int pass = 0; pass < 3; ++pass) {
        weva_document_set_viewport(d, 200, 300);
        CHECK(weva_document_update(d, 0) == WEVA_OK);
        CHECK(weva_element_bounds(d, weva_document_query(d, "#a"), &x, &y, &w, &h) == WEVA_OK);
        CHECK(near(w, 73) && near(h, 27));
        weva_document_set_viewport(d, 400, 100);
        CHECK(weva_document_update(d, 0) == WEVA_OK);
        CHECK(weva_element_bounds(d, weva_document_query(d, "#a"), &x, &y, &w, &h) == WEVA_OK);
        CHECK(near(w, 200) && near(h, 10));
    }

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
    int positioned_shapes = 0;
    uint8_t coverage = 200;
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
    pixels.assign(static_cast<size_t>(n) * n, static_cast<HostFontState*>(ud)->coverage);
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

size_t host_positioned_shape(void* ud, uint64_t, const char* utf8, size_t len, double px,
                             weva_shaped_glyph* out, size_t capacity) {
    ++static_cast<HostFontState*>(ud)->positioned_shapes;
    for (size_t i = 0; out && i < len && i < capacity; ++i)
        out[i] = {static_cast<uint32_t>(static_cast<unsigned char>(utf8[i])),
                  static_cast<uint32_t>(i), px * 2, 0, 3, 4};
    return len;
}

} // namespace

// Every texture a draw names must be in the published texture list.
//
// This is the contract weva_c.h states ("a texture the host must create before
// issuing the draws that reference it"), and it was quietly violated: the glyph
// atlas uploaded lazily at the first text run, so a second run that added a
// glyph re-uploaded it and released the texture the first run's draw still
// pointed at. A host that maps ids faithfully drew that run untextured — solid
// quads where the text should be — and a GPU backend would have freed a
// texture with a queued draw still referencing it. Found by comparing the
// software backend against Godot's, which is the whole reason that comparison
// exists.
void test_abi_texture_ids_are_all_published() {
    weva_config cfg = default_config(300, 200);
    weva_document_t d = weva_document_create(&cfg);
    // Two words, so inline layout makes two text boxes and thus two draws, and
    // the second introduces glyphs the first did not use.
    CHECK(load(d, "<body><div id=a>Hello Weva</div></body>") == WEVA_OK);
    CHECK(add_css(d, "#a { display: block; font-size: 16px; color: #202020 }") == WEVA_OK);
    CHECK(weva_document_update(d, 0) == WEVA_OK);

    size_t texture_count = 0;
    const weva_texture* textures = weva_document_textures(d, &texture_count);
    size_t draw_count = 0;
    const weva_draw* draws = weva_document_draws(d, &draw_count);

    // The document has to actually produce textured draws, or this passes for
    // the wrong reason.
    int textured = 0;
    for (size_t i = 0; i < draw_count; ++i) {
        if (draws[i].texture_id == 0) continue;
        ++textured;
        bool published = false;
        for (size_t t = 0; t < texture_count; ++t) {
            if (textures[t].id == draws[i].texture_id) { published = true; break; }
        }
        CHECK(published);
    }
    CHECK(textured >= 2);
    // And one atlas upload per frame, not one per run.
    CHECK(texture_count == 1);

    weva_document_destroy(d);
}

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

void test_abi_font_and_renderer_replacement() {
    weva_config cfg = default_config(400, 150);
    weva_document_t d = weva_document_create(&cfg);
    HostFontState a, b;
    b.coverage = 80;
    weva_font_backend fb{};
    fb.user_data = &a;
    fb.face_metrics = host_face_metrics;
    fb.glyph_index = host_glyph_index;
    fb.glyph_metrics = host_glyph_metrics;
    fb.rasterize = host_rasterize;
    fb.shape = host_shape;
    CHECK(load(d, "<body><span id=a>AB</span></body>") == WEVA_OK);
    CHECK(add_css(d, "body{margin:30px}#a{display:inline-block;font-size:16px;line-height:40px}") == WEVA_OK);
    weva_document_set_font_backend(d, &fb, 7);
    const auto alpha = [&] {
        size_t count = 0;
        const weva_texture* textures = weva_document_textures(d, &count);
        CHECK(count == 1);
        uint8_t maximum = 0;
        for (size_t t = 0; t < count; ++t)
            for (int64_t i = 0; i < int64_t(textures[t].width) * textures[t].height; ++i)
                maximum = std::max(maximum, textures[t].rgba[i * 4 + 3]);
        return maximum;
    };
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(alpha() == 200);
    fb.user_data = &b;
    // Same face and glyph IDs, different provider and rasterized coverage.
    weva_document_set_font_backend(d, &fb, 7);
    CHECK(alpha() == 200); // The preceding frame stays published until update.
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(b.rasterizes > 0 && alpha() == 80);
    b.coverage = 120;
    // Reinstalling the same table also refreshes a mutated font resource.
    weva_document_set_font_backend(d, &fb, 7);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(alpha() == 120);

    struct Textures {
        uint64_t next = 100;
        std::vector<uint64_t> live;
        int uploads = 0, releases = 0;
    } first, second;
    second.next = 1000;
    weva_render_backend rb{};
    rb.generate_texture = [](void* user, const uint8_t*, int32_t, int32_t) -> uint64_t {
        auto& s = *static_cast<Textures*>(user);
        ++s.uploads;
        s.live.push_back(s.next);
        return s.next++;
    };
    rb.release_texture = [](void* user, uint64_t texture) {
        auto& s = *static_cast<Textures*>(user);
        auto it = std::find(s.live.begin(), s.live.end(), texture);
        CHECK(it != s.live.end());
        if (it != s.live.end()) s.live.erase(it);
        ++s.releases;
    };
    const int rasterizes = b.rasterizes;
    rb.user_data = &first;
    weva_document_set_render_backend(d, &rb);
    CHECK(alpha() == 120); // Published pixels live until the next update.
    weva_document_set_render_backend(d, &rb);
    CHECK(alpha() == 120); // Repeated replacement must retain that same frame.
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(first.uploads == 1 && first.live.size() == 1);
    CHECK(b.rasterizes == rasterizes); // Reuse CPU glyphs with a new texture owner.
    rb.user_data = &second;
    weva_document_set_render_backend(d, &rb);
    CHECK(first.live.empty() && first.releases == 1);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(second.uploads == 1 && second.live.size() == 1);
    CHECK(b.rasterizes == rasterizes);
    weva_document_destroy(d);
    CHECK(second.live.empty() && second.releases == 1);
}

void test_abi_positioned_font_shaping() {
    weva_config cfg = default_config(400, 150);
    weva_document_t d = weva_document_create(&cfg);
    CHECK(weva_document_set_font_shaper(nullptr, host_positioned_shape) == WEVA_ERR_INVALID_ARGUMENT);
    CHECK(weva_document_set_font_shaper(d, host_positioned_shape) == WEVA_ERR_INVALID_ARGUMENT);
    HostFontState state;
    weva_font_backend fb{};
    fb.user_data = &state;
    fb.face_metrics = host_face_metrics;
    fb.glyph_index = host_glyph_index;
    fb.glyph_metrics = host_glyph_metrics;
    fb.rasterize = host_rasterize;
    fb.shape = host_shape;
    weva_document_set_font_backend(d, &fb, 7);
    CHECK(load(d, "<body><span id=a>AB</span></body>") == WEVA_OK);
    // Leave room above the glyphs so the upward offset is observable without
    // clipping and re-triangulating the first quad at the UA body's top edge.
    CHECK(add_css(d, "body{margin:30px}#a{display:inline-block;font-size:16px;line-height:40px}") == WEVA_OK);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    const auto vertices = [&] {
        size_t count = 0;
        const weva_draw* draws = weva_document_draws(d, &count);
        std::vector<weva_vertex> out;
        for (size_t i = 0; i < count; ++i)
            if (draws[i].texture_id)
                out.insert(out.end(), draws[i].vertices, draws[i].vertices + draws[i].vertex_count);
        return out;
    };
    const auto width = [&] {
        double w = 0;
        CHECK(weva_element_bounds(d, weva_document_query(d, "#a"), nullptr, nullptr, &w, nullptr) == WEVA_OK);
        return w;
    };
    const auto legacy = vertices();
    CHECK(!legacy.empty());
    CHECK(near(width(), 32));
    const int legacy_calls = state.shapes;
    CHECK(weva_document_set_font_shaper(d, host_positioned_shape) == WEVA_OK);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(state.positioned_shapes >= 2 && state.shapes == legacy_calls);
    CHECK(near(width(), 64));
    const auto positioned = vertices();
    CHECK(positioned.size() == legacy.size());
    if (!legacy.empty() && !positioned.empty()) {
        CHECK(near(positioned.front().x - legacy.front().x, 3));
        CHECK(near(positioned.front().y - legacy.front().y, -4));
    }
    // A no-op registration preserves the warm shaping cache and clean frame.
    const int positioned_calls = state.positioned_shapes;
    CHECK(weva_document_set_font_shaper(d, host_positioned_shape) == WEVA_OK);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(state.positioned_shapes == positioned_calls);
    // Removing the override restores legacy shaping and measured widths.
    CHECK(weva_document_set_font_shaper(d, nullptr) == WEVA_OK);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(near(width(), 32));
    const auto restored = vertices();
    CHECK(restored.size() == legacy.size());
    for (size_t i = 0; i < std::min(restored.size(), legacy.size()); ++i)
        CHECK(restored[i].x == legacy[i].x && restored[i].y == legacy[i].y);
    // Installing a font table clears the override even for the same face ID.
    CHECK(weva_document_set_font_shaper(d, host_positioned_shape) == WEVA_OK);
    weva_document_set_font_backend(d, &fb, 7);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(near(width(), 32));
    weva_document_set_font_backend(d, nullptr, 0);
    CHECK(weva_document_set_font_shaper(d, nullptr) == WEVA_ERR_INVALID_ARGUMENT);
    weva_document_destroy(d);
}

void test_abi_shape_cache_retains_hot_labels() {
    struct State { int hot_calls = 0; int calls = 0; } state;
    weva_config cfg = default_config(400, 150);
    auto d = weva_document_create(&cfg);
    weva_font_backend fb{};
    fb.user_data = &state;
    fb.shape = [](void* user, uint64_t, const char* text, size_t len, double px,
                  uint32_t* glyphs, double* advances, uint32_t* clusters, size_t capacity) -> size_t {
        ++static_cast<State*>(user)->calls;
        if (std::string_view(text, len) == "HUD") ++static_cast<State*>(user)->hot_calls;
        for (size_t i = 0; glyphs && i < std::min(len, capacity); ++i) {
            glyphs[i] = static_cast<unsigned char>(text[i]);
            advances[i] = px;
            clusters[i] = static_cast<uint32_t>(i);
        }
        return len;
    };
    weva_document_set_font_backend(d, &fb, 7);
    CHECK(load(d, "<span id=a>HUD</span>") == WEVA_OK);
    CHECK(add_css(d, "#a{display:inline-block;font-size:16px}") == WEVA_OK);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    const auto a = weva_document_query(d, "#a");
    const int warm_calls = state.hot_calls;
    CHECK(warm_calls > 0);
    for (int i = 0; i < 4500; ++i) {
        const auto label = "Label" + std::to_string(i);
        CHECK(weva_element_set_text(d, a, label.c_str()) == WEVA_OK);
        CHECK(weva_document_update(d, 0) == WEVA_OK);
        CHECK(weva_element_set_text(d, a, "HUD") == WEVA_OK);
        CHECK(weva_document_update(d, 0) == WEVA_OK);
    }
    CHECK(state.hot_calls == warm_calls);
    double width = 0;
    CHECK(weva_element_bounds(d, a, nullptr, nullptr, &width, nullptr) == WEVA_OK);
    CHECK(near(width, 48));
    const int calls = state.calls;
    CHECK(weva_element_set_text(d, a, "Label4499") == WEVA_OK);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(state.calls == calls); // The most recent changing label remains hot too.
    CHECK(weva_element_set_text(d, a, "Label0") == WEVA_OK);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(state.calls > calls); // Cold runs are actually evicted; storage is bounded.
    CHECK(weva_element_bounds(d, a, nullptr, nullptr, &width, nullptr) == WEVA_OK);
    CHECK(near(width, 96));
    weva_document_destroy(d);
}

void test_abi_shape_cache_bounds_long_labels() {
    struct State { int hot_calls = 0; int calls = 0; } state;
    auto d = weva_document_create(nullptr);
    weva_font_backend fb{};
    fb.user_data = &state;
    fb.shape = [](void* user, uint64_t, const char* text, size_t len, double,
                  uint32_t* glyphs, double* advances, uint32_t* clusters, size_t capacity) -> size_t {
        auto& s = *static_cast<State*>(user);
        ++s.calls;
        if (std::string_view(text, len) == "HUD") ++s.hot_calls;
        const bool oversized = std::string_view(text, len) == "oversized";
        const size_t count = oversized ? 120000 : len;
        for (size_t i = 0; glyphs && i < std::min(count, capacity); ++i) {
            glyphs[i] = static_cast<unsigned char>(text[oversized ? 0 : i]);
            advances[i] = 1;
            clusters[i] = oversized ? 0 : static_cast<uint32_t>(i);
        }
        return count;
    };
    weva_document_set_font_backend(d, &fb, 7);
    CHECK(load(d, "<span id=a>HUD</span>") == WEVA_OK);
    CHECK(add_css(d, "#a{display:inline-block;white-space:nowrap}") == WEVA_OK);
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    const auto a = weva_document_query(d, "#a");
    const auto set = [&](const std::string& label) {
        CHECK(weva_element_set_text(d, a, label.c_str()) == WEVA_OK);
        CHECK(weva_document_update(d, 0) == WEVA_OK);
    };
    const int warm = state.hot_calls;
    // Far fewer than 4096 entries, but over 4 MiB of shaped payload. The
    // changing labels must evict cold data without displacing a hot HUD label.
    for (int i = 0; i < 100; ++i) {
        set(std::string(2048, 'x') + std::to_string(i));
        set("HUD");
    }
    CHECK(state.hot_calls == warm);
    int before = state.calls;
    set(std::string(2048, 'x') + "99");
    CHECK(state.calls == before);
    before = state.calls;
    set(std::string(2048, 'x') + "0");
    CHECK(state.calls > before);
    double width = 0;
    CHECK(weva_element_bounds(d, a, nullptr, nullptr, &width, nullptr) == WEVA_OK);
    CHECK(near(width, 2049)); // Eviction does not alter the shaped result.
    set("oversized");
    CHECK(weva_element_bounds(d, a, nullptr, nullptr, &width, nullptr) == WEVA_OK);
    CHECK(near(width, 120000));
    set("HUD");
    CHECK(state.hot_calls == warm); // A huge result cannot evict the working set.
    before = state.calls;
    set("oversized");
    CHECK(state.calls > before); // Oversized results are shaped but not retained.
    weva_document_destroy(d);
}

void test_abi_content_size() {
    // The viewport is the floor: a page that fits reports the box it was laid
    // out in, so a host asking "is there anything to scroll to" gets no.
    {
        weva_config cfg = default_config(200, 100);
        weva_document_t d = weva_document_create(&cfg);
        CHECK(load(d, "<body><div id=a></div></body>") == WEVA_OK);
        CHECK(add_css(d, "html, body { margin: 0 } #a { display: block; height: 30px }") == WEVA_OK);
        CHECK(weva_document_update(d, 0) == WEVA_OK);
        double w = 0, h = 0;
        CHECK(weva_document_content_size(d, &w, &h) == WEVA_OK);
        CHECK(near(w, 200) && near(h, 100));
        weva_document_destroy(d);
    }
    {
        // And a page that overflows reports how far it actually reaches, which
        // is the number a scrollbar is made of. Three 80px blocks in a 100px
        // viewport reach 240.
        weva_config cfg = default_config(200, 100);
        weva_document_t d = weva_document_create(&cfg);
        CHECK(load(d, "<body><div></div><div></div><div></div></body>") == WEVA_OK);
        CHECK(add_css(d, "html, body { margin: 0 } div { display: block; height: 80px }") == WEVA_OK);
        CHECK(weva_document_update(d, 0) == WEVA_OK);
        double w = 0, h = 0;
        CHECK(weva_document_content_size(d, &w, &h) == WEVA_OK);
        CHECK(near(h, 240));
        // Width did not overflow, so it stays the viewport's.
        CHECK(near(w, 200));
        weva_document_destroy(d);
    }
    {
        // An absolutely positioned box counts too: it is painted, so it is
        // reachable, and a host that scrolled only to the in-flow extent would
        // cut it off.
        weva_config cfg = default_config(200, 100);
        weva_document_t d = weva_document_create(&cfg);
        CHECK(load(d, "<body><div id=a></div></body>") == WEVA_OK);
        CHECK(add_css(d, "html, body { margin: 0 }"
                         "#a { position: absolute; top: 400px; left: 0;"
                         "     width: 50px; height: 50px }") == WEVA_OK);
        CHECK(weva_document_update(d, 0) == WEVA_OK);
        double w = 0, h = 0;
        CHECK(weva_document_content_size(d, &w, &h) == WEVA_OK);
        CHECK(near(h, 450));
        weva_document_destroy(d);
    }
    {
        // Null document is rejected rather than crashing, and the
        // out-parameters are optional, like every other query here.
        double w = 0;
        CHECK(weva_document_content_size(nullptr, &w, nullptr) == WEVA_ERR_INVALID_ARGUMENT);
        weva_config cfg = default_config();
        weva_document_t d = weva_document_create(&cfg);
        CHECK(weva_document_content_size(d, nullptr, nullptr) == WEVA_OK);
        weva_document_destroy(d);
    }
}

void test_abi_rounded_rect_primitive() {
    // A plain rounded box travels as a SHAPE as well as triangles, so a backend
    // that can evaluate a rounded box per pixel gets exact coverage from it and
    // one that cannot still uploads a correct tessellation.
    {
        weva_config cfg = default_config(200, 100);
        weva_document_t d = weva_document_create(&cfg);
        // Given a margin so it is not flush against the viewport: a shape whose
        // coverage ramp would spill past the clip in force is cut into
        // triangles instead, which is the conservative half of the contract.
        CHECK(add_css(d, "html, body { margin: 0 }"
                         "#a { display: block; margin: 10px; width: 80px; height: 40px;"
                         "     border-radius: 10px; background: #ff0000 }") == WEVA_OK);
        CHECK(load(d, "<body><div id=a></div></body>") == WEVA_OK);
        CHECK(weva_document_update(d, 0) == WEVA_OK);

        size_t count = 0;
        const weva_draw* draws = weva_document_draws(d, &count);
        int shapes = 0;
        for (size_t i = 0; i < count; ++i) {
            if (draws[i].kind != WEVA_DRAW_ROUNDED_RECT) continue;
            ++shapes;
            const weva_rounded_rect& s = draws[i].rounded_rect;
            CHECK(near(s.width, 80) && near(s.height, 40));
            CHECK(near(s.radii[0][0], 10) && near(s.radii[0][1], 10));
            CHECK(s.r > 0.9f && s.g == 0.0f);
            // The tessellation travels with it, or a host that ignores the kind
            // would draw nothing at all.
            CHECK(draws[i].vertex_count > 0 && draws[i].index_count > 0);
        }
        CHECK(shapes == 1);
        weva_document_destroy(d);
    }
    {
        // A square box is not offered as a shape: it has no curve, so a
        // per-pixel evaluation would buy nothing over two triangles.
        weva_config cfg = default_config(200, 100);
        weva_document_t d = weva_document_create(&cfg);
        CHECK(load(d, "<body><div id=a></div></body>") == WEVA_OK);
        CHECK(add_css(d, "html, body { margin: 0 }"
                         "#a { display: block; width: 80px; height: 40px;"
                         "     background: #ff0000 }") == WEVA_OK);
        CHECK(weva_document_update(d, 0) == WEVA_OK);
        size_t count = 0;
        const weva_draw* draws = weva_document_draws(d, &count);
        int shapes = 0;
        for (size_t i = 0; i < count; ++i) {
            if (draws[i].kind == WEVA_DRAW_ROUNDED_RECT) ++shapes;
        }
        CHECK(shapes == 0);
        weva_document_destroy(d);
    }
    {
        // Nor is a box with a border: the background and the border are one
        // mesh, and describing only half of it would be a lie.
        weva_config cfg = default_config(200, 100);
        weva_document_t d = weva_document_create(&cfg);
        CHECK(load(d, "<body><div id=a></div></body>") == WEVA_OK);
        CHECK(add_css(d, "html, body { margin: 0 }"
                         "#a { display: block; width: 80px; height: 40px; border-radius: 10px;"
                         "     border: 2px solid #00f; background: #ff0000 }") == WEVA_OK);
        CHECK(weva_document_update(d, 0) == WEVA_OK);
        size_t count = 0;
        const weva_draw* draws = weva_document_draws(d, &count);
        int shapes = 0;
        for (size_t i = 0; i < count; ++i) {
            if (draws[i].kind == WEVA_DRAW_ROUNDED_RECT) ++shapes;
        }
        CHECK(shapes == 0);
        weva_document_destroy(d);
    }
}

namespace {

// Every solid (untextured) rect the document draws, as (x, y, w, h, r, g, b).
// Decorations are rects, so this is how a test sees one.
struct SolidRect {
    double x, y, w, h;
    float r, g, b;
};

std::vector<SolidRect> solid_rects(weva_document_t d) {
    std::vector<SolidRect> out;
    size_t count = 0;
    const weva_draw* draws = weva_document_draws(d, &count);
    for (size_t i = 0; i < count; ++i) {
        if (draws[i].texture_id != 0 || draws[i].vertex_count == 0) continue;
        double x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9;
        for (size_t v = 0; v < draws[i].vertex_count; ++v) {
            const weva_vertex& vt = draws[i].vertices[v];
            x0 = std::min(x0, static_cast<double>(vt.x));
            y0 = std::min(y0, static_cast<double>(vt.y));
            x1 = std::max(x1, static_cast<double>(vt.x));
            y1 = std::max(y1, static_cast<double>(vt.y));
        }
        const weva_vertex& first = draws[i].vertices[0];
        out.push_back({x0, y0, x1 - x0, y1 - y0, first.r, first.g, first.b});
    }
    return out;
}

}   // namespace

// The user-agent stylesheet has asked for `text-decoration: underline` on <a>
// and <u> since the beginning and nothing drew it, so every link in every
// document was plain.
void test_abi_text_decorations_are_drawn() {
    weva_config c{};
    c.viewport_width = 400;
    c.viewport_height = 300;
    c.use_user_agent_stylesheet = 1;
    weva_document_t d = weva_document_create(&c);
    const char* css = "html, body { margin: 0; background: #fff }"
                      " div { display: block; font-size: 20px; color: #000 }";
    const char* html = "<div id=plain>Plain</div>";
    weva_document_add_css(d, css, std::strlen(css));
    weva_document_load_html(d, html, std::strlen(html));
    weva_document_update(d, 0);
    const size_t undecorated = solid_rects(d).size();

    // An <a> takes its underline from the UA sheet alone -- no author rule.
    const char* linked = "<a href=x>Link</a>";
    weva_document_load_html(d, linked, std::strlen(linked));
    weva_document_update(d, 0);
    const std::vector<SolidRect> with_link = solid_rects(d);
    CHECK(with_link.size() == undecorated + 1);

    // Under the baseline and no taller than the text: a rule, not a block.
    double bx = 0, by = 0, bw = 0, bh = 0;
    weva_element_bounds(d, weva_document_query(d, "a"), &bx, &by, &bw, &bh);
    bool found = false;
    for (const SolidRect& r : with_link) {
        if (r.h > 4 || r.w < 4) continue;   // not a rule
        found = true;
        CHECK(r.y > by);            // below the top of the line
        CHECK(r.y < by + bh + 4);   // and not far below it
        CHECK(r.w > 8);             // as wide as the word, near enough
    }
    CHECK(found);
    weva_document_destroy(d);
}

// Which line, what colour, how thick.
void test_abi_text_decoration_variants() {
    weva_config c{};
    c.viewport_width = 400;
    c.viewport_height = 300;
    c.use_user_agent_stylesheet = 1;
    weva_document_t d = weva_document_create(&c);
    const char* css = "html, body { margin: 0; background: #fff }"
                      " div { display: block; font-size: 20px; color: #000 }";
    weva_document_add_css(d, css, std::strlen(css));

    const auto rules_of = [&](const char* html) {
        weva_document_load_html(d, html, std::strlen(html));
        weva_document_update(d, 0);
        // A rule is wide and thin. The bound has to allow a DECLARED
        // thickness -- at 4 it excluded the 5px case below and took the
        // binary down on an empty result.
        std::vector<SolidRect> rules;
        for (const SolidRect& r : solid_rects(d)) {
            if (r.h <= 8 && r.w > 4) rules.push_back(r);
        }
        return rules;
    };

    CHECK(rules_of("<div>Plain</div>").empty());
    CHECK(rules_of("<div style='text-decoration: underline'>x</div>").size() == 1);
    CHECK(rules_of("<div style='text-decoration: line-through'>x</div>").size() == 1);
    CHECK(rules_of("<div style='text-decoration: overline'>x</div>").size() == 1);
    // Two lines at once, from one declaration.
    CHECK(rules_of("<div style='text-decoration: underline overline'>x</div>").size() == 2);
    // `none` on the element beats the UA sheet's underline on <a>.
    CHECK(rules_of("<a href=x style='text-decoration: none'>x</a>").empty());

    // The three sit in different places: overline above the text, underline
    // below it, line-through between.
    // Guarded: indexing an empty result would take the whole binary down
    // before a single CHECK had printed, which is how this first showed up.
    const auto y_of = [&](const char* html) {
        const std::vector<SolidRect> r = rules_of(html);
        CHECK(!r.empty());
        return r.empty() ? 0.0 : r[0].y;
    };
    const double over = y_of("<div style='text-decoration: overline'>x</div>");
    const double strike = y_of("<div style='text-decoration: line-through'>x</div>");
    const double under = y_of("<div style='text-decoration: underline'>x</div>");
    CHECK(over < strike);
    CHECK(strike < under);

    // `text-decoration-color` overrides the text colour; without it the rule
    // takes the text's.
    const std::vector<SolidRect> red =
        rules_of("<div style='text-decoration: underline; text-decoration-color: #ff0000'>x</div>");
    CHECK(red.size() == 1);
    if (!red.empty()) CHECK(red[0].r > 0.5f && red[0].g < 0.1f);
    const std::vector<SolidRect> black = rules_of("<div style='text-decoration: underline'>x</div>");
    CHECK(!black.empty());
    if (!black.empty()) CHECK(black[0].r < 0.1f);

    // And a declared thickness is used.
    const std::vector<SolidRect> thick = rules_of(
        "<div style='text-decoration: underline; text-decoration-thickness: 5px'>x</div>");
    CHECK(thick.size() == 1);
    if (!thick.empty()) CHECK(thick[0].h > 4);
    weva_document_destroy(d);
}

// `text-decoration-style`. Solid was all the port drew; dashed, dotted and
// double all came out as one unbroken rule, so the three ways of marking text
// apart were one way.
//
// A decoration is drawn per text RUN, and the collapsing tokeniser makes one
// run per word and one per space -- so even a solid underline arrives as
// several contiguous pieces. These assertions are about the SHAPE the pieces
// make, not about how many the tokeniser happened to produce.
void test_abi_text_decoration_styles() {
    weva_config c{};
    c.viewport_width = 400;
    c.viewport_height = 300;
    c.use_user_agent_stylesheet = 1;
    weva_document_t d = weva_document_create(&c);
    const char* css = "html, body { margin: 0; background: #fff }"
                      " div { display: block; font-size: 20px; color: #000; width: 300px }";
    weva_document_add_css(d, css, std::strlen(css));

    // The rules are given a colour nothing else uses, so a glyph drawn as a
    // solid rect -- which the built-in 5x7 face does -- cannot be mistaken for
    // one. A size filter alone counted glyphs as rules.
    const auto rules_of = [&](const char* decoration) {
        const std::string html = std::string("<div style='text-decoration: underline; ") +
                                 "text-decoration-color: #ff0000; " + decoration +
                                 "'>a word or two</div>";
        weva_document_load_html(d, html.c_str(), html.size());
        weva_document_update(d, 0);
        std::vector<SolidRect> rules;
        for (const SolidRect& r : solid_rects(d)) {
            if (r.r > 0.5f && r.g < 0.1f && r.b < 0.1f) rules.push_back(r);
        }
        return rules;
    };
    const auto reach = [](const std::vector<SolidRect>& rs) {
        double far = 0;
        for (const SolidRect& r : rs) far = std::max<double>(far, r.x + r.w);
        return far;
    };
    const auto ink = [](const std::vector<SolidRect>& rs) {
        double total = 0;
        for (const SolidRect& r : rs) total += r.w;
        return total;
    };
    const auto rows = [](const std::vector<SolidRect>& rs) {
        std::set<int> ys;
        for (const SolidRect& r : rs) ys.insert(static_cast<int>(r.y * 4));
        return ys.size();
    };

    const std::vector<SolidRect> solid = rules_of("");
    CHECK(!solid.empty());
    CHECK(rows(solid) == 1);   // one rule, at one height

    // Two parallel rules: twice the pieces, at two heights, and twice the ink.
    const std::vector<SolidRect> dbl = rules_of("text-decoration-style: double");
    CHECK(dbl.size() == solid.size() * 2);
    CHECK(rows(dbl) == 2);
    CHECK(std::abs(ink(dbl) - ink(solid) * 2) < 0.5);

    // Broken rules: more pieces than solid, less ink than solid, and dotted
    // is finer than dashed.
    const std::vector<SolidRect> dotted = rules_of("text-decoration-style: dotted");
    const std::vector<SolidRect> dashed = rules_of("text-decoration-style: dashed");
    CHECK(dotted.size() > solid.size());
    CHECK(dashed.size() > solid.size());
    CHECK(dotted.size() > dashed.size());
    CHECK(ink(dotted) < ink(solid));
    CHECK(ink(dashed) < ink(solid));
    CHECK(rows(dotted) == 1);

    // No piece runs past where the solid rule ends: the last is cut to the
    // run rather than overhanging it.
    CHECK(reach(dashed) <= reach(solid) + 0.01);
    CHECK(reach(dotted) <= reach(solid) + 0.01);

    // `wavy` has no curve primitive in either renderer, so both draw it as
    // dashed. Asserted so the shared simplification is a decision on record.
    CHECK(rules_of("text-decoration-style: wavy").size() == dashed.size());
    weva_document_destroy(d);
}

// The shorthand has to reach PAINT, not just the expander: three longhands
// produced correctly and then read from the wrong place is the same bug from
// the author's side.
void test_abi_text_decoration_shorthand_paints() {
    weva_config c{};
    c.viewport_width = 400;
    c.viewport_height = 300;
    c.use_user_agent_stylesheet = 1;
    weva_document_t d = weva_document_create(&c);
    const char* css = "html, body { margin: 0; background: #fff }"
                      " div { display: block; font-size: 20px; color: #000; width: 300px }";
    weva_document_add_css(d, css, std::strlen(css));

    const auto red_rules = [&](const char* decoration) {
        const std::string html =
            std::string("<div style='text-decoration: ") + decoration + "'>a word or two</div>";
        weva_document_load_html(d, html.c_str(), html.size());
        weva_document_update(d, 0);
        std::vector<SolidRect> rules;
        for (const SolidRect& r : solid_rects(d)) {
            if (r.r > 0.5f && r.g < 0.1f && r.b < 0.1f) rules.push_back(r);
        }
        return rules;
    };

    // One declaration carrying all three: the line appears, in red, broken
    // into dots. Every part of that comes from the shorthand alone.
    const std::vector<SolidRect> dotted = red_rules("underline dotted red");
    CHECK(!dotted.empty());

    const std::vector<SolidRect> solid = red_rules("underline red");
    CHECK(!solid.empty());
    // Dotted lays down less ink than solid over the same run, which is what
    // says the STYLE arrived and not just the line and the colour.
    double dotted_ink = 0, solid_ink = 0;
    for (const SolidRect& r : dotted) dotted_ink += r.w;
    for (const SolidRect& r : solid) solid_ink += r.w;
    CHECK(dotted_ink < solid_ink);

    // And with no colour in the shorthand the rule is NOT red: it takes the
    // text's colour, so the red filter finds nothing.
    CHECK(red_rules("underline dotted").empty());
    weva_document_destroy(d);
}

void test_abi_registered_font_families() {
    auto cfg = default_config(800, 200);
    auto d = weva_document_create(&cfg);
    HostFontState state;
    weva_font_backend fb{};
    fb.user_data = &state;
    fb.face_metrics = host_face_metrics;
    fb.glyph_index = host_glyph_index;
    fb.glyph_metrics = host_glyph_metrics;
    fb.rasterize = host_rasterize;
    fb.shape = [](void* ud, uint64_t face, const char* text, size_t len, double px,
                  uint32_t* glyphs, double* advances, uint32_t* clusters, size_t cap) -> size_t {
        return host_shape(ud, face, text, len, px * face, glyphs, advances, clusters, cap);
    };
    fb.variant = [](void*, uint64_t face, int32_t weight, int32_t italic) -> uint64_t {
        return face + ((weight >= 600 || italic) ? 1 : 0);
    };
    CHECK(weva_document_register_font_family(nullptr, "Camp", 2) == WEVA_ERR_INVALID_ARGUMENT);
    CHECK(weva_document_register_font_family(d, "Camp", 2) == WEVA_ERR_INVALID_ARGUMENT);
    weva_document_set_font_backend(d, &fb, 1);
    CHECK(weva_document_register_font_family(d, nullptr, 2) == WEVA_ERR_INVALID_ARGUMENT);
    CHECK(weva_document_register_font_family(d, "   ", 2) == WEVA_ERR_INVALID_ARGUMENT);
    CHECK(weva_document_register_font_family(d, "Camp", 2) == WEVA_OK);
    const auto setup = [&] {
        CHECK(load(d, "<body><span id=a>abc</span><span id=b>abc</span></body>") == WEVA_OK);
        CHECK(add_css(d, "span{display:inline-block;font:10px Camp}#b{font-weight:bold}") == WEVA_OK);
    };
    const auto widths = [&](double a, double b) {
        CHECK(weva_document_update(d, 0) == WEVA_OK);
        double x, y, w, h;
        CHECK(weva_element_bounds(d, weva_document_query(d, "#a"), &x, &y, &w, &h) == WEVA_OK);
        CHECK(near(w, a));
        CHECK(weva_element_bounds(d, weva_document_query(d, "#b"), &x, &y, &w, &h) == WEVA_OK);
        CHECK(near(w, b));
    };
    setup();
    widths(60, 90);
    int shapes = state.shapes;
    CHECK(weva_document_register_font_family(d, "cAMP", 2) == WEVA_OK);
    widths(60, 90);
    CHECK(state.shapes == shapes);
    CHECK(weva_document_register_font_family(d, "Camp", 4) == WEVA_OK);
    widths(120, 150);
    const weva_shape_glyphs_fn positioned = [](void* ud, uint64_t face, const char* text,
                                              size_t len, double px, weva_shaped_glyph* out,
                                              size_t cap) -> size_t {
        ++static_cast<HostFontState*>(ud)->positioned_shapes;
        for (size_t i = 0; out && i < len && i < cap; ++i)
            out[i] = {static_cast<uint32_t>(text[i]), static_cast<uint32_t>(i), px * face * .5, 0, 0, 0};
        return len;
    };
    CHECK(weva_document_set_font_shaper(d, positioned) == WEVA_OK);
    widths(60, 75);
    setup(); // Family resources survive document replacement.
    widths(60, 75);
    CHECK(weva_document_set_font_shaper(d, nullptr) == WEVA_OK);
    widths(120, 150);
    CHECK(weva_document_register_font_family(d, "CAMP", 0) == WEVA_OK);
    widths(30, 60);
    CHECK(weva_document_register_font_family(d, "Camp", 2) == WEVA_OK);
    widths(60, 90);
    weva_document_set_font_backend(d, &fb, 1);
    widths(30, 60); // Backend replacement must not retain old face identities.
    CHECK(weva_document_register_font_family(d, "Camp", 2) == WEVA_OK);
    weva_document_set_font_backend(d, nullptr, 0);
    widths(15, 15);
    weva_document_destroy(d);
}
