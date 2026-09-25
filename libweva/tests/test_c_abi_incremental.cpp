// The incremental update, checked the only way that means anything: against
// the same document built from scratch.
//
// An update used to run the whole pipeline every time -- cascade, box build,
// layout, paint -- so its output was correct by construction. Now it skips
// whatever the cascade's diff says did not change, and a wrong skip leaves a
// stale document on screen rather than failing. So every case here mutates a
// live document, and then asserts that its draw list is IDENTICAL, to the
// vertex, to a fresh document that was given the mutated state up front.
//
// The mutations are chosen to land on each tier the classifier can pick:
// nothing at all, paint-only, layout, and a change to which boxes exist.
#include "check.h"
#include "weva_c.h"
#include "weva/font_interface.h"
#include "select_autoscroll_fixture.h"
#include "text_autoscroll_fixture.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>
#include <map>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <system_error>
#include <iterator>

namespace {
struct IncrementalBindingData {
    std::map<std::string, std::string> values;
    int rows = 2;
    static int count(void* user, const char* path) {
        return std::strcmp(path, "Items") == 0 ? static_cast<IncrementalBindingData*>(user)->rows : -1;
    }
    static size_t read(void* user, const char* path, char* out, size_t capacity, int* found) {
        const auto& values = static_cast<IncrementalBindingData*>(user)->values;
        const auto it = values.find(path);
        *found = it != values.end();
        if (!*found) return 0;
        const auto& value = it->second;
        if (out && capacity) {
            const size_t n = std::min(value.size(), capacity - 1);
            std::memcpy(out, value.data(), n); out[n] = 0;
        }
        return value.size();
    }
};
}

namespace {

weva_config config(int w = 400, int h = 300) {
    weva_config c{};
    c.viewport_width = w;
    c.viewport_height = h;
    c.use_user_agent_stylesheet = 1;
    return c;
}

// The whole published frame, flattened, so two documents can be compared
// without caring how the draws were produced.
struct Frame {
    struct Draw {
        std::vector<float> vertices;
        std::vector<uint32_t> indices;
        std::vector<double> effect;
        int32_t kind = 0;
        int32_t scissor[5] = {0, 0, 0, 0, 0};
        // Texture IDS are deliberately absent: they are handles, and an
        // incremental pass legitimately reuses one where a fresh document
        // mints a new number. What must match is the PIXELS, compared below.
        std::vector<uint8_t> texels;
        int32_t tex_w = 0, tex_h = 0;
    };
    std::vector<Draw> draws;

    // Says WHERE two frames part company. A bare pass/fail here would only
    // report that an incremental update was wrong, which is the least useful
    // half of what a failure knows.
    std::string diff(const Frame& o) const {
        char buf[256];
        if (draws.size() != o.draws.size()) {
            std::snprintf(buf, sizeof(buf), "draw count %zu vs %zu", draws.size(), o.draws.size());
            return buf;
        }
        for (size_t i = 0; i < draws.size(); ++i) {
            const Draw& a = draws[i];
            const Draw& b = o.draws[i];
            const char* what = nullptr;
            if (a.kind != b.kind) what = "kind";
            else if (a.vertices.size() != b.vertices.size()) what = "vertex count";
            else if (a.vertices != b.vertices) {
                for (size_t v = 0; v < a.vertices.size(); ++v) {
                    if (a.vertices[v] == b.vertices[v]) continue;
                    std::snprintf(buf, sizeof(buf), "draw %zu of %zu: vertex field %zu differs: %.17g vs %.17g",
                        i, draws.size(), v, static_cast<double>(a.vertices[v]), static_cast<double>(b.vertices[v]));
                    return buf;
                }
                what = "vertex data";
            }
            else if (a.indices != b.indices) what = "indices";
            else if (a.effect != b.effect) what = "effect parameters";
            else if (a.tex_w != b.tex_w || a.tex_h != b.tex_h) what = "texture size";
            else if (a.texels != b.texels) what = "texture pixels";
            else {
                for (int k = 0; k < 5; ++k) {
                    if (a.scissor[k] != b.scissor[k]) what = "scissor";
                }
            }
            if (!what) continue;
            std::snprintf(buf, sizeof(buf), "draw %zu of %zu: %s differs", i, draws.size(), what);
            return buf;
        }
        return {};
    }

    bool operator!=(const Frame& o) const { return !(*this == o); }

    bool operator==(const Frame& o) const {
        if (draws.size() != o.draws.size()) return false;
        for (size_t i = 0; i < draws.size(); ++i) {
            const Draw& a = draws[i];
            const Draw& b = o.draws[i];
            if (a.vertices != b.vertices || a.indices != b.indices || a.kind != b.kind) return false;
            if (a.effect != b.effect) return false;
            for (int k = 0; k < 5; ++k) {
                if (a.scissor[k] != b.scissor[k]) return false;
            }
            if (a.tex_w != b.tex_w || a.tex_h != b.tex_h || a.texels != b.texels) return false;
        }
        return true;
    }
};

Frame capture(weva_document_t d) {
    Frame f;
    size_t texture_count = 0;
    const weva_texture* textures = weva_document_textures(d, &texture_count);
    size_t count = 0;
    const weva_draw* draws = weva_document_draws(d, &count);
    for (size_t i = 0; i < count; ++i) {
        const weva_draw& s = draws[i];
        Frame::Draw t;
        for (size_t v = 0; v < s.vertex_count; ++v) {
            const weva_vertex& vert = s.vertices[v];
            t.vertices.insert(t.vertices.end(),
                              {vert.x, vert.y, vert.u, vert.v, vert.r, vert.g, vert.b, vert.a});
        }
        t.indices.assign(s.indices, s.indices + s.index_count);
        t.kind = s.kind;
        if (s.kind == WEVA_DRAW_BACKDROP_FILTER) {
            t.effect.push_back(s.backdrop.blur_radius);
            for (float v : s.backdrop.color_matrix) t.effect.push_back(v);
            for (float v : s.backdrop.color_offset) t.effect.push_back(v);
            t.effect.push_back(s.backdrop.color_alpha);
        } else if (s.kind == WEVA_DRAW_ROUNDED_RECT) {
            const auto& r = s.rounded_rect;
            t.effect = {r.x, r.y, r.width, r.height, r.r, r.g, r.b, r.a};
            for (const auto& corner : r.radii) for (double v : corner) t.effect.push_back(v);
        }
        t.scissor[0] = s.scissor_x;
        t.scissor[1] = s.scissor_y;
        t.scissor[2] = s.scissor_width;
        t.scissor[3] = s.scissor_height;
        t.scissor[4] = s.has_scissor;
        if (s.texture_id != 0) {
            for (size_t k = 0; k < texture_count; ++k) {
                if (textures[k].id != s.texture_id) continue;
                t.tex_w = textures[k].width;
                t.tex_h = textures[k].height;
                const size_t n = static_cast<size_t>(t.tex_w) * t.tex_h * 4;
                t.texels.assign(textures[k].rgba, textures[k].rgba + n);
                break;
            }
        }
        f.draws.push_back(std::move(t));
    }
    return f;
}

// The control: the same document, computed from nothing.
//
// "From nothing" has one deliberate exception. The glyph atlas lives for the
// life of a document and packs each glyph where it first appears, so two
// documents that have SEEN different text pack the same glyph at different
// texels and their text quads carry different uvs -- both correct. So the
// control replays the base document first, putting the same glyphs in the
// atlas in the same order, and only then loads it again to force the whole
// pipeline. A reload resets the styles and the elements, so the update that
// follows computes everything except the atlas afresh.
template <typename F>
Frame build(const char* html, const char* css, F&& setup) {
    weva_config c = config();
    weva_document_t d = weva_document_create(&c);
    weva_document_add_css(d, css, std::strlen(css));
    weva_document_load_html(d, html, std::strlen(html));
    weva_document_update(d, 0);
    weva_document_load_html(d, html, std::strlen(html));
    setup(d);
    weva_document_update(d, 0);
    const Frame f = capture(d);
    weva_document_destroy(d);
    return f;
}

// Positions, colours and topology, with the uvs left out: for the cases where
// the two documents have legitimately seen different text, which is exactly
// where the atlas cannot be compared.
bool same_geometry(const Frame& a, const Frame& b) {
    if (a.draws.size() != b.draws.size()) return false;
    for (size_t i = 0; i < a.draws.size(); ++i) {
        const Frame::Draw& x = a.draws[i];
        const Frame::Draw& y = b.draws[i];
        if (x.indices != y.indices || x.kind != y.kind) return false;
        if (x.vertices.size() != y.vertices.size()) return false;
        // Eight floats a vertex: x, y, u, v, r, g, b, a. Skip u and v.
        for (size_t v = 0; v < x.vertices.size(); ++v) {
            if (v % 8 == 2 || v % 8 == 3) continue;
            if (x.vertices[v] != y.vertices[v]) return false;
        }
    }
    return true;
}

const char* kHtml =
    "<div id=a class=card>Alpha <b>bold</b> text</div>"
    "<div id=b class=card>Beta</div>"
    "<ul><li>one</li><li>two</li></ul>";

const char* kCss =
    ".card { background: #234; color: #eee; padding: 8px; margin: 4px;"
    "        border: 2px solid #567; border-radius: 6px;"
    "        box-shadow: 0 4px 12px rgba(0,0,0,0.4) }"
    ".hot { background: linear-gradient(180deg, #f80, #a40) }"
    ".wide { width: 300px }"
    ".gone { display: none }"
    "li { padding: 2px }";

// Mutates a live document, updates it, and checks the result against a
// document that was born that way.
void check_mutation(const char* label, const char* attr_name, const char* attr_value) {
    weva_config c = config();
    weva_document_t d = weva_document_create(&c);
    weva_document_add_css(d, kCss, std::strlen(kCss));
    weva_document_load_html(d, kHtml, std::strlen(kHtml));
    weva_document_update(d, 0);

    const weva_element_t a = weva_document_query(d, "#a");
    CHECK(a != WEVA_ELEMENT_NONE);
    CHECK(weva_element_set_attribute(d, a, attr_name, attr_value) == WEVA_OK);
    weva_document_update(d, 0);
    const Frame incremental = capture(d);

    const Frame fresh = build(kHtml, kCss, [&](weva_document_t f) {
        const weva_element_t e = weva_document_query(f, "#a");
        weva_element_set_attribute(f, e, attr_name, attr_value);
    });
    if (incremental != fresh) {
        std::printf("  incremental mismatch [%s]: %s\n", label, incremental.diff(fresh).c_str());
    }
    CHECK(incremental == fresh);
    CHECK(!incremental.draws.empty());
    weva_document_destroy(d);
}

// Mutates `selector` and checks the result against the same document built
// from nothing, with a stylesheet of the caller's choosing.
void check_with_sheet(const char* label, const char* html, const char* css, const char* selector,
                      const char* attr, const char* value) {
    weva_config c = config();
    weva_document_t d = weva_document_create(&c);
    weva_document_add_css(d, css, std::strlen(css));
    weva_document_load_html(d, html, std::strlen(html));
    weva_document_update(d, 0);
    const weva_element_t e = weva_document_query(d, selector);
    CHECK(e != WEVA_ELEMENT_NONE);
    weva_element_set_attribute(d, e, attr, value);
    weva_document_update(d, 0);
    const Frame got = capture(d);

    const Frame want = build(html, css, [&](weva_document_t f) {
        weva_element_set_attribute(f, weva_document_query(f, selector), attr, value);
    });
    if (got != want) std::printf("  reach mismatch [%s]: %s\n", label, got.diff(want).c_str());
    CHECK(got == want);
    CHECK(!got.draws.empty());
    weva_document_destroy(d);
}

}   // namespace

// How far an attribute change is allowed to reach.
//
// A restyle confined to the touched element's subtree is only sound while no
// selector can carry the change further. Two kinds can, and each gets a sheet
// here that would leave a visibly stale document if the scope were wrong: a
// sibling combinator, which reaches the elements AFTER the one that changed,
// and :has(), which lets a descendant decide an ancestor's match and so puts
// the whole document back in play.
void test_abi_incremental_selector_reach() {
    const char* html =
        "<div id=wrap>"
        "<p id=one>One</p><p id=two>Two</p><p id=three>Three</p>"
        "<div id=host><span id=inner>Inner</span></div>"
        "</div>";

    // ---- `+` and `~`: changing `one` restyles what comes after it
    {
        const char* css =
            "p { background: #222; color: #ddd; padding: 3px }"
            ".mark + p { background: #b30 }"
            ".mark ~ p { color: #ff0 }";
        check_with_sheet("adjacent sibling", html, css, "#one", "class", "mark");
        check_with_sheet("sibling from middle", html, css, "#two", "class", "mark");
    }

    // ---- :has(): changing a descendant restyles its ancestor
    {
        const char* css =
            "div { padding: 4px; background: #123 }"
            "span { background: #345; color: #eee }"
            "#wrap:has(.lit) { background: #703 }"
            "div:has(> .lit) { padding: 20px }";
        check_with_sheet("has, from a descendant", html, css, "#inner", "class", "lit");
    }

    // ---- both at once, and a change deep in the tree
    {
        const char* css =
            "p { padding: 2px } span { padding: 1px }"
            ".on + p { background: #0a0 }"
            "#wrap:has(.on) { border: 3px solid #f0f }";
        check_with_sheet("sibling and has together", html, css, "#three", "class", "on");
        check_with_sheet("deep element", html, css, "#inner", "class", "on");
    }

    // ---- several elements touched before one update
    {
        const char* css =
            "p { background: #222; color: #ddd }"
            ".a { background: #900 } .b { color: #0f0 }"
            ".a + p { padding: 9px }";
        weva_config c = config();
        weva_document_t d = weva_document_create(&c);
        weva_document_add_css(d, css, std::strlen(css));
        weva_document_load_html(d, html, std::strlen(html));
        weva_document_update(d, 0);
        weva_element_set_attribute(d, weva_document_query(d, "#one"), "class", "a");
        weva_element_set_attribute(d, weva_document_query(d, "#three"), "class", "b");
        weva_document_update(d, 0);
        const Frame got = capture(d);

        const Frame want = build(html, css, [&](weva_document_t f) {
            weva_element_set_attribute(f, weva_document_query(f, "#one"), "class", "a");
            weva_element_set_attribute(f, weva_document_query(f, "#three"), "class", "b");
        });
        if (got != want) std::printf("  multi mismatch: %s\n", got.diff(want).c_str());
        CHECK(got == want);
        weva_document_destroy(d);
    }
}

void test_abi_incremental_matches_fresh() {
    // ---- an update that changes nothing publishes the same frame
    {
        weva_config c = config();
        weva_document_t d = weva_document_create(&c);
        weva_document_add_css(d, kCss, std::strlen(kCss));
        weva_document_load_html(d, kHtml, std::strlen(kHtml));
        weva_document_update(d, 0);
        const Frame first = capture(d);
        CHECK(!first.draws.empty());
        // Three more updates with nothing touched. The draw list must still be
        // there and still be right -- an early return that dropped it would
        // leave a host with an empty frame.
        for (int i = 0; i < 3; ++i) weva_document_update(d, 0);
        CHECK(capture(d) == first);
        weva_document_destroy(d);
    }

    // ---- setting an attribute to a value that changes no style
    {
        weva_config c = config();
        weva_document_t d = weva_document_create(&c);
        weva_document_add_css(d, kCss, std::strlen(kCss));
        weva_document_load_html(d, kHtml, std::strlen(kHtml));
        weva_document_update(d, 0);
        const Frame before = capture(d);
        const weva_element_t a = weva_document_query(d, "#a");
        // `data-x` matches no selector, so nothing about the document differs.
        weva_element_set_attribute(d, a, "data-x", "1");
        weva_document_update(d, 0);
        CHECK(capture(d) == before);
        weva_document_destroy(d);
    }

    // ---- paint-only: a background that no layout reads
    check_mutation("paint", "class", "card hot");
    // ---- layout: a width, which moves everything after it
    check_mutation("layout", "class", "card wide");
    // ---- boxes: display:none removes the box and its subtree
    check_mutation("boxes", "class", "card gone");
    // ---- back again, from the tier that removed the boxes
    check_mutation("restored", "class", "card");
    // ---- an id change, which restyles through a different selector
    check_mutation("identity", "id", "c");

    // ---- a sequence, so each tier follows a different predecessor
    {
        weva_config c = config();
        weva_document_t d = weva_document_create(&c);
        weva_document_add_css(d, kCss, std::strlen(kCss));
        weva_document_load_html(d, kHtml, std::strlen(kHtml));
        weva_document_update(d, 0);
        const weva_element_t a = weva_document_query(d, "#a");
        const char* sequence[] = {"card hot", "card wide", "card gone", "card hot", "card"};
        for (const char* value : sequence) {
            weva_element_set_attribute(d, a, "class", value);
            weva_document_update(d, 0);
            const Frame got = capture(d);
            const Frame want = build(kHtml, kCss, [&](weva_document_t f) {
                weva_element_set_attribute(f, weva_document_query(f, "#a"), "class", value);
            });
            if (got != want) {
                std::printf("  sequence mismatch [%s]: %s\n", value, got.diff(want).c_str());
            }
            CHECK(got == want);
        }
        weva_document_destroy(d);
    }

    // ---- a resized viewport, which no computed style mentions
    {
        weva_config c = config();
        weva_document_t d = weva_document_create(&c);
        weva_document_add_css(d, kCss, std::strlen(kCss));
        weva_document_load_html(d, kHtml, std::strlen(kHtml));
        weva_document_update(d, 0);
        weva_document_set_viewport(d, 250, 500);
        weva_document_update(d, 0);
        const Frame got = capture(d);

        weva_config c2 = config(250, 500);
        weva_document_t f = weva_document_create(&c2);
        weva_document_add_css(f, kCss, std::strlen(kCss));
        weva_document_load_html(f, kHtml, std::strlen(kHtml));
        weva_document_update(f, 0);
        if (!(got == capture(f))) {
            std::printf("  viewport mismatch: %s\n", got.diff(capture(f)).c_str());
        }
        CHECK(got == capture(f));
        weva_document_destroy(f);
        weva_document_destroy(d);
    }

    // ---- viewport-relative font sizes on empty boxes: text layout can hide
    // a stale font-size memo by querying it with another parent-size key.
    for (const char* form : {"10vw", "10vh", "10vmin", "10vmax", "10dvw", "10svh",
                             "calc(1em + 2vw)", "clamp(8px, 10vw, 40px)"}) {
        const char* html = "<div id=sample></div>";
        const std::string css = std::string("html,body{margin:0;font-size:16px}#sample{") +
            "display:block;width:1em;height:1em;background:#09f;font-size:" + form + "}";
        weva_config initial = config(100, 200);
        weva_document_t resized = weva_document_create(&initial);
        CHECK(weva_document_add_css(resized, css.data(), css.size()) == WEVA_OK);
        CHECK(weva_document_load_html(resized, html, std::strlen(html)) == WEVA_OK);
        CHECK(weva_document_update(resized, 0) == WEVA_OK);
        const int sizes[][2] = {{200,200}, {200,400}, {400,200}, {100,200}};
        for (const auto& size : sizes) {
            weva_document_set_viewport(resized, size[0], size[1]);
            CHECK(weva_document_update(resized, 0) == WEVA_OK);
            weva_config target = config(size[0], size[1]);
            weva_document_t fresh = weva_document_create(&target);
            CHECK(weva_document_add_css(fresh, css.data(), css.size()) == WEVA_OK);
            CHECK(weva_document_load_html(fresh, html, std::strlen(html)) == WEVA_OK);
            CHECK(weva_document_update(fresh, 0) == WEVA_OK);
            double x, y, width, height, fx, fy, fw, fh;
            CHECK(weva_element_bounds(resized, weva_document_query(resized, "#sample"),
                                      &x, &y, &width, &height) == WEVA_OK);
            CHECK(weva_element_bounds(fresh, weva_document_query(fresh, "#sample"),
                                      &fx, &fy, &fw, &fh) == WEVA_OK);
            CHECK(x == fx && y == fy && width == fw && height == fh);
            const Frame actual = capture(resized), expected = capture(fresh);
            if (actual != expected)
                std::printf("  viewport font [%s, %dx%d]: %s\n", form, size[0], size[1],
                            actual.diff(expected).c_str());
            CHECK(actual == expected);
            CHECK(weva_document_update(resized, 0) == WEVA_OK);
            CHECK(capture(resized) == expected);
            weva_document_destroy(fresh);
        }
        weva_document_destroy(resized);
    }

    // ---- computed font inheritance, including equal raw strings with
    // different bases. Check authored dimensions as well as fresh parity.
    for (const char* display : {"block", "contents"}) {
        const char* html = "<div id=base><div id=a><div id=b><div id=sample></div></div></div></div>";
        const std::string css = std::string("html,body{margin:0;font-size:16px}") +
            "#base{font-size:16px}#a{font-size:2em;display:" + display + "}#b{font-size:2em}"
            "#sample{width:1em;height:1em;background:#09f}"
            "#b.inherit{font-size:inherit!important}#b.unset{font-size:unset}"
            "#b.initial{font-size:initial}#b.empty{font-size:var(--missing)}"
            "#b.relative{font-size:150%}#sample::before{content:'';display:block;"
            "width:0.5em;height:0.5em;background:#f80}";
        auto make = [&]() {
            weva_config c = config();
            auto d = weva_document_create(&c);
            CHECK(weva_document_add_css(d, css.data(), css.size()) == WEVA_OK);
            CHECK(weva_document_load_html(d, html, std::strlen(html)) == WEVA_OK);
            return d;
        };
        auto live = make();
        CHECK(weva_document_update(live, 0) == WEVA_OK);
        for (int base_px : {16, 20, 12, 16}) {
            for (const char* mode : {"inherit", "", "unset", "initial", "empty", "relative", ""}) {
                auto fresh = make();
                const std::string base_style = "font-size:" + std::to_string(base_px) + "px";
                for (auto d : {live, fresh}) {
                    CHECK(weva_element_set_attribute(d, weva_document_query(d, "#base"),
                                                      "style", base_style.c_str()) == WEVA_OK);
                    CHECK(weva_element_set_attribute(d, weva_document_query(d, "#b"), "class", mode) == WEVA_OK);
                    CHECK(weva_document_update(d, 0) == WEVA_OK);
                }
                const double expected = std::strcmp(mode, "initial") == 0 ? 16 :
                    std::strcmp(mode, "relative") == 0 ? base_px * 3 :
                    *mode ? base_px * 2 : base_px * 4;
                double x, y, w, h;
                CHECK(weva_element_bounds(live, weva_document_query(live, "#sample"), &x, &y, &w, &h) == WEVA_OK);
                if (w != expected || h != expected)
                    std::printf("  inherited font [%s,%d,%s]: %.9g x %.9g expected %.9g\n",
                                display, base_px, mode, w, h, expected);
                CHECK(w == expected && h == expected);
                const Frame actual = capture(live), control = capture(fresh);
                if (actual != control) std::printf("  inherited font frame: %s\n", actual.diff(control).c_str());
                CHECK(actual == control);
                CHECK(!actual.draws.empty());
                CHECK(weva_document_update(live, 0) == WEVA_OK);
                CHECK(capture(live) == control);
                weva_document_destroy(fresh);
            }
        }
        weva_document_destroy(live);
    }

    // ---- a second document loaded into the same handle
    {
        weva_config c = config();
        weva_document_t d = weva_document_create(&c);
        weva_document_add_css(d, kCss, std::strlen(kCss));
        weva_document_load_html(d, kHtml, std::strlen(kHtml));
        weva_document_update(d, 0);

        const char* other = "<div class=card>Only this</div>";
        weva_document_load_html(d, other, std::strlen(other));
        weva_document_update(d, 0);
        const Frame got = capture(d);
        // This document has painted text the control never has, so only the
        // geometry is comparable. What it proves is the thing that matters:
        // not one box of the replaced document survives.
        const Frame want = build(other, kCss, [](weva_document_t) {});
        if (!same_geometry(got, want)) std::printf("  reload mismatch: %s\n", got.diff(want).c_str());
        CHECK(same_geometry(got, want));
        weva_document_destroy(d);
    }
}


// Alternate changes on different branches and compare EVERY frame with a
// forced rebuild. This catches stale command ranges after preceding siblings
// gain/lose draws, freed texture handles, ancestor effects and arena growth.
void test_abi_incremental_subtree_sequences() {
    const char* html =
        "<div id=wrap><div id=a><span>Alpha beta gamma delta</span></div>"
        "<div id=b><span>Beta alpha gamma delta</span></div>"
        "<div id=c><span>Gamma delta beta alpha</span></div></div>"
        "<div id=tail>Outside the changed subtree</div>";
    const char* css =
        "#wrap { width:330px; padding:5px; background:#123; }"
        "#a,#b,#c { width:180px; height:60px; box-sizing:border-box; overflow:hidden;"
        " padding:4px; border:1px solid #369; border-radius:7px;"
        " background:linear-gradient(90deg,#234,#567); box-shadow:0 1px 4px #345; }"
        "#tail { background:#abc; padding:5px; }";
    struct Mutation { const char* selector; const char* style; };
    const Mutation steps[] = {
        {"#a", "padding:12px"}, {"#b", "background:#af4"},
        {"#a", "padding:7px"}, {"#c", "background:none;box-shadow:none;border:0"},
        {"#wrap", "opacity:.4"}, {"#b", "border-radius:20px"},
        {"#b", "border-radius:calc(10% + 1em) / calc(20% + 2px)"},
        {"#b", "border-radius:calc(10% + 1em) / calc(20% + 2px);width:220px;height:90px"},
        {"#b", "border-radius:calc(10% + 1em) / calc(20% + 2px);font-size:20px"},
        {"#b", "border-top-left-radius:12px\t8px;border-top-right-radius:10%\n20%"},
        {"#b", "border-radius:0"}, {"#b", "border-radius:20px"},
        {"#b", "border:8px outset currentColor;color:rgba(0,0,0,.5)"},
        {"#b", "border:8px inset currentColor;color:#767676;border-radius:12px"},
        {"#b", "border:8px solid;border-color:red green blue transparent;border-radius:12px"},
        {"#b", "border:8px solid;border-color:red green blue yellow;border-left-width:17px"},
        {"#b", "border:8px outset;border-top-style:solid;border-right-style:none"},
        {"#b", "border:8px outset currentColor"}, {"#wrap", "color:#0000ff"},
        {"#b", "border:0"}, {"#b", ""}, {"#wrap", ""},
        {"#a", "padding:4px;font-size:20px"}, {"#wrap", "opacity:.8;clip-path:inset(3px)"},
        {"#a", "padding:9px;font-size:14px"}, {"#wrap", ""},
        {"#wrap", "line-height:150%;font-size:20px"}, {"#a span", "font-size:10px"},
        {"#a span", "font-size:10px;line-height:150%"},
        {"#a span", "font-size:10px;line-height:inherit"},
        {"#wrap", "line-height:150%;font-size:24px"},
        {"#wrap", "line-height:calc(1 + .5);font-size:24px"},
        {"#a span", "font-size:10px;line-height:unset"},
        {"#wrap", "line-height:calc(1em + 50% + 2px);font-size:20px"},
        {"#a span", ""}, {"#wrap", ""},
        {"#c", "background:linear-gradient(90deg,#234,#567)"},
        {"#a", "height:auto;width:100px"}, {"#a", ""},
        {"#wrap", "display:flex;width:200px"}, {"#a", "padding:18px"},
        {"#wrap", "display:grid;grid-template-columns:1fr 1fr"}, {"#a", "width:230px"},
        {"#a", "padding:0;height:auto"}, {"#a span", "display:block;height:10px;margin:17px 0 13px"},
        {"#wrap", "display:flex;flex-direction:column"},
        {"#a span", "display:block;height:10px;margin:-7px 0 23px"},
        {"#a", "height:20px;padding:0"}, {"#wrap", "display:block"},
        {"#wrap", "display:grid;grid-template-columns:1fr 1fr"},
        {"#a span", ""}, {"#a", ""},
        {"#wrap", ""}, {"#b", "transform:translate(7px,3px)"},
        {"#b", "background:blue;filter:blur(2px)"}, {"#b", ""},
        {"#wrap", "color:red"}, {"#a", "padding:4px"},
        {"#b", "backdrop-filter:blur(3px) brightness(.6)"}, {"#a", "background:green"},
        {"#b", "backdrop-filter:blur(1px) brightness(.8)"}, {"#a", "background:blue"},
        {"#wrap", "display:flex"}, {"#a", "display:inline-flex;width:180px"},
        {"#a span", "padding-left:2px"}, {"#a span", "padding-left:3px"},
        {"#wrap", "position:relative;left:7px;top:9px"},
        {"#b", "position:absolute;right:0;bottom:0;width:60px;height:30px"},
        {"#a", "padding:12px"}, {"#a", "padding:8px"},
        {"#wrap", "position:fixed;left:15%;top:10%;height:200px"},
        {"#a", "padding:6px"}, {"#a", "padding:4px"},
        {"#a span", "anchor-name:--text"},
        {"#b", "position:fixed;left:anchor(--text right);top:anchor(--text bottom)"},
        {"#a", "padding:12px"}, {"#a span", ""},
        {"#a", "padding:6px"}, {"#b", "position:sticky;top:0"},
        {"#a", "padding:9px"}, {"#b", "float:left"},
        {"#a", "padding:4px"}, {"#b", ""}, {"#wrap", ""},
    };
    const weva_config cfg = config();
    weva_document_t live = weva_document_create(&cfg);
    weva_document_t full = weva_document_create(&cfg);
    for (weva_document_t doc : {live, full}) {
        CHECK(weva_document_add_css(doc, css, std::strlen(css)) == WEVA_OK);
        CHECK(weva_document_load_html(doc, html, std::strlen(html)) == WEVA_OK);
        CHECK(weva_document_update(doc, 0) == WEVA_OK);
    }
    std::map<std::string, std::string> attributes;
    for (int repeat = 0; repeat < 3; ++repeat) {
        for (const Mutation& step : steps) {
            attributes[step.selector] = step.style;
            weva_element_set_attribute(live, weva_document_query(live, step.selector), "style", step.style);
            weva_document_load_html(full, html, std::strlen(html));
            for (const auto& attr : attributes)
                weva_element_set_attribute(full, weva_document_query(full, attr.first.c_str()),
                                           "style", attr.second.c_str());
            CHECK(weva_document_update(live, 0) == WEVA_OK);
            CHECK(weva_document_update(full, 0) == WEVA_OK);
            const Frame a = capture(live), b = capture(full);
            if (a != b) std::printf("  subtree sequence %d %s %s: %s\n", repeat,
                                    step.selector, step.style, a.diff(b).c_str());
            CHECK(a == b);
            double lw=0, lh=0, fw=0, fh=0;
            weva_document_content_size(live, &lw, &lh);
            weva_document_content_size(full, &fw, &fh);
            CHECK(lw == fw && lh == fh);
        }
    }
    weva_document_destroy(live);
    weva_document_destroy(full);
}


static void check_retained_grid_paint(const char* overflow) {
    const std::string css =
        "*{box-sizing:border-box}html,body{margin:0}"
        "#app{display:flex;flex-direction:column;width:400px;height:300px;padding:6px;gap:7px}"
        "#live{width:50px;height:24px;background:#abc}#footer{height:10px;flex:none}"
        "#wrap{flex:1;min-height:0;padding:4px;overflow:hidden;border-radius:9px;background:#123}"
        "#grid{display:grid;grid-template-columns:repeat(4,1fr);gap:3px}"
        ".cell{height:35px;background:linear-gradient(90deg,#456,#acf);border-radius:5px}"
        ".cell span{font-size:11px;color:white}" + std::string("#wrap{") + overflow + "}";
    std::string html = "<div id=app><div id=live></div><div id=wrap><div id=grid>";
    for (int i = 0; i < 48; ++i)
        html += "<div class=cell id=c" + std::to_string(i) + "><span>cell</span></div>";
    html += "</div></div><div id=footer></div></div>";
    const auto cfg = config();
    auto live = weva_document_create(&cfg), full = weva_document_create(&cfg);
    for (auto doc : {live, full}) {
        CHECK(weva_document_add_css(doc, css.data(), css.size()) == WEVA_OK);
        CHECK(weva_document_load_html(doc, html.data(), html.size()) == WEVA_OK);
        CHECK(weva_document_update(doc, 0) == WEVA_OK);
    }
    const auto cell_geometry = [](weva_document_t doc) -> uintptr_t {
        // The first textured draw is the first cell's gradient: preceding
        // boxes have only solid fills. This also identifies it after rotation.
        size_t count = 0;
        const auto* draws = weva_document_draws(doc, &count);
        for (size_t i = 0; i < count; ++i) {
            const auto& d = draws[i];
            if (d.texture_id && d.vertex_count) return reinterpret_cast<uintptr_t>(d.vertices);
        }
        return 0;
    };
    struct Mutation { const char* selector; const char* style; };
    const Mutation changes[] = {
        {"#live", "width:100px"}, {"#live", "width:80px;background:none"},
        {"#live", "width:120px;background:#abc"},
        // Footer changes only the grid's incoming clip; header changes also
        // move its origin. Fractions exercise curved clips within a scissor.
        {"#footer", "height:10.125px"}, {"#footer", "height:31px"},
        {"#live", "height:40px"}, {"#live", "height:24px"},
        {"#wrap", "opacity:.6;filter:sepia(.3)"},
        {"#wrap", "transform:translate(.25px,.5px);clip-path:inset(2px round 4px)"},
        {"#c0", "background:#fa3"}, {"#grid", "color:red"},
        {"#wrap", ""}, {"#footer", ""}, {"#c0", ""}, {"#grid", ""}
    };
    for (int repeat = 0; repeat < 2; ++repeat) {
        for (size_t step = 0; step < sizeof(changes)/sizeof(changes[0]); ++step) {
            const uintptr_t previous = cell_geometry(live);
            CHECK(previous != 0);
            for (auto doc : {live, full}) {
                const auto& change = changes[step];
                CHECK(weva_element_set_attribute(doc, weva_document_query(doc, change.selector),
                                                 "style", change.style) == WEVA_OK);
            }
            weva_document_set_viewport(full, 401, 300);
            weva_document_set_viewport(full, 400, 300);
            CHECK(weva_document_update(live, 0) == WEVA_OK);
            CHECK(weva_document_update(full, 0) == WEVA_OK);
            const Frame a = capture(live), b = capture(full);
            if (a != b) std::printf("  retained paint %s %d/%zu: %s\n", overflow, repeat, step, a.diff(b).c_str());
            CHECK(a == b);
            // This checks that unchanged geometry was actually retained. Old
            // draw buffers are still alive while a fresh frame is constructed,
            // so a full repaint cannot reuse this allocation by coincidence.
            if (step < 3) CHECK(cell_geometry(live) == previous);
        }
    }
    weva_document_destroy(live);
    weva_document_destroy(full);
}

void test_abi_incremental_retained_grid_paint() {
    for (const char* overflow : {"", "border-radius:0", "overflow:visible;border-radius:0",
            "overflow:visible;border-radius:0;transform:rotate(3deg);transform-origin:50% 50%"})
        check_retained_grid_paint(overflow);
}

void test_abi_draw_versions() {
    size_t count = 99;
    CHECK(weva_document_draw_versions(nullptr, &count) == nullptr && count == 0);
    const auto cfg = config();
    auto doc = weva_document_create(&cfg);
    CHECK(weva_document_draw_versions(doc, &count) == nullptr && count == 0);
    const char* css = "body{margin:0}div{width:90px;height:40px;background:#acf}"
                      "#b{background:linear-gradient(90deg,#13a,#5bd)}";
    const char* html = "<div id=a></div><div id=b>text</div><div id=c></div>";
    CHECK(weva_document_add_css(doc, css, std::strlen(css)) == WEVA_OK);
    CHECK(weva_document_load_html(doc, html, std::strlen(html)) == WEVA_OK);
    CHECK(weva_document_update(doc, 0) == WEVA_OK);
    std::map<uint64_t, Frame::Draw> seen;
    std::vector<uint64_t> previous;
    for (int step = 0; step < 9; ++step) {
        if (step == 1 || step == 2)
            weva_element_set_attribute(doc, weva_document_query(doc, "#a"), "style",
                                       step == 1 ? "background:#bfa" : "background:none");
        if (step == 3)
            weva_element_set_attribute(doc, weva_document_query(doc, "#b"), "style",
                                       "background:linear-gradient(90deg,#f31,#dab)");
        if (step == 4) weva_document_set_font_backend(doc, nullptr, 0);
        if (step == 5) weva_document_set_render_backend(doc, nullptr);
        if (step == 6) weva_document_load_html(doc, "", 0);
        if (step == 7) weva_document_load_html(doc, html, std::strlen(html));
        CHECK(weva_document_update(doc, 0) == WEVA_OK);
        const Frame frame = capture(doc);
        const uint64_t* versions = weva_document_draw_versions(doc, &count);
        CHECK(count == frame.draws.size());
        CHECK((versions != nullptr) == (count != 0));
        size_t retained = 0, rebuilt = 0;
        std::map<uint64_t, bool> unique;
        for (size_t i = 0; i < count; ++i) {
            CHECK(versions[i] != 0 && unique.emplace(versions[i], true).second);
            const auto old = seen.find(versions[i]);
            if (old != seen.end()) {
                Frame a, b;
                a.draws.push_back(old->second); b.draws.push_back(frame.draws[i]);
                CHECK(a == b);
                ++retained;
            } else {
                CHECK(seen.empty() || versions[i] > seen.rbegin()->first);
                seen.emplace(versions[i], frame.draws[i]);
                ++rebuilt;
            }
        }
        if (step == 1) CHECK(retained > 0 && rebuilt > 0);
        if (step == 2) {
            CHECK(retained > 0 && count < previous.size());
            // The surviving draw moves earlier without changing its version.
            CHECK(count && versions[0] == previous[1]);
        }
        if (step == 4 || step == 5 || step == 7) CHECK(retained == 0 && rebuilt > 0);
        if (step == 8) CHECK(rebuilt == 0 && retained == count);
        previous.clear();
        if (count) previous.assign(versions, versions + count);
        const auto* published = versions;
        CHECK(weva_document_update(doc, 0) == WEVA_OK);
        CHECK(weva_document_draw_versions(doc, &count) == published);
        CHECK(count == previous.size());
    }
    weva_document_destroy(doc);
}

void test_abi_incremental_glyph_preparation() {
    struct FontState {
        weva::StubFont font;
        weva::Bitmap bitmap;
        int empty_rasters = 0;
    } state[2];
    weva_font_backend fonts[2]{};
    const auto cfg = config();
    weva_document_t docs[2]{};
    const char* html = "<div id=dirty></div><div id=text>A A</div>"
                       "<input id=field value='B B'><div id=clip><p>C C</p></div>";
    const char* css = "body{margin:0}#dirty{width:40px;height:20px;background:red}"
                      "#clip{height:1px;overflow:hidden}p{margin-top:30px}";
    for (int i = 0; i < 2; ++i) {
        fonts[i].user_data = &state[i];
        fonts[i].rasterize = [](void* data, uint64_t face, uint32_t glyph, double px,
                               weva_glyph_bitmap* out) -> int32_t {
            auto& s = *static_cast<FontState*>(data);
            s.bitmap = {};
            if (!s.font.rasterize(weva::FaceHandle{face}, glyph, px,
                                  weva::RenderMode::Alpha8, &s.bitmap)) return 0;
            if (s.bitmap.width == 0 || s.bitmap.height == 0) ++s.empty_rasters;
            *out = {s.bitmap.data.data(), s.bitmap.width, s.bitmap.height, nullptr};
            return 1;
        };
        docs[i] = weva_document_create(&cfg);
        weva_document_set_font_backend(docs[i], &fonts[i], 1);
        CHECK(weva_document_add_css(docs[i], css, std::strlen(css)) == WEVA_OK);
        CHECK(weva_document_load_html(docs[i], html, std::strlen(html)) == WEVA_OK);
        CHECK(weva_document_update(docs[i], 0) == WEVA_OK);
        CHECK(state[i].empty_rasters > 0);
    }
    int viewport_width = 400;
    for (int step = 0; step < 11; ++step) {
        for (int i = 0; i < 2; ++i) {
            const auto doc = docs[i];
            const auto attr = [&](const char* selector, const char* name, const char* value) {
                CHECK(weva_element_set_attribute(doc, weva_document_query(doc, selector),
                                                 name, value) == WEVA_OK);
            };
            state[i].empty_rasters = 0;
            if (step == 0 || step == 10) attr("#dirty", "style", "background:blue");
            if (step == 1) attr("#dirty", "style", "background:none");
            if (step == 2) {
                CHECK(weva_element_set_text(doc, weva_document_query(doc, "#text"), "Z Z") == WEVA_OK);
            }
            if (step == 3) attr("#field", "value", "Q Q");
            if (step == 4) attr("#text", "style", "font-size:22px");
            if (step == 5) weva_document_set_font_backend(doc, &fonts[i], 1);
            if (step == 6) { viewport_width = 380; weva_document_set_viewport(doc, viewport_width, 300); }
            if (step == 7) attr("#clip", "style", "height:90px");
            if (step == 8) CHECK(weva_document_load_html(doc, html, std::strlen(html)) == WEVA_OK);
            if (step == 9) weva_document_set_render_backend(doc, nullptr);
        }
        // Same final viewport, but a forced complete layout/paint in the control.
        weva_document_set_viewport(docs[1], viewport_width + 1, 300);
        weva_document_set_viewport(docs[1], viewport_width, 300);
        CHECK(weva_document_update(docs[0], 0) == WEVA_OK);
        CHECK(weva_document_update(docs[1], 0) == WEVA_OK);
        const Frame live = capture(docs[0]), full = capture(docs[1]);
        if (live != full) std::printf("  glyph preparation step %d: %s\n", step, live.diff(full).c_str());
        CHECK(live == full);
        // Spaces have no slot: visiting unchanged text would rasterize them
        // again even when the shaper memo and every visible glyph are cached.
        if (step == 0 || step == 1 || step == 10) CHECK(state[0].empty_rasters == 0);
        CHECK(state[1].empty_rasters > 0);
        if (step == 2 || step == 3 || step == 4 || step == 5 || step == 8)
            CHECK(state[0].empty_rasters > 0);
    }
    for (const auto doc : docs) weva_document_destroy(doc);
}

void test_abi_incremental_flex_intrinsics() {
    const char* html =
        "<main><nav><span class=hint><span class=btn>A</span>Load</span>"
        "<span class=hint><span class=btn id=button>B</span>Back</span></nav>"
        "<div>Unchanged content</div><div>More surrounding content</div></main>";
    const char* css =
        "main{position:relative;width:400px;height:150px}"
        "nav{position:absolute;bottom:0;left:0;right:0;display:flex;justify-content:center;gap:20px}"
        ".hint{display:flex;align-items:center;gap:12px}"
        ".btn{display:flex;width:34px;height:34px;align-items:center;justify-content:center;background:#abc}";
    check_with_sheet("natural flex width before imposing allocation", html, css, "#button",
                     "style", "padding-left:11px");
    check_with_sheet("natural flex width from border", html, css, "#button",
                     "style", "border:7px solid red");
    const std::string shrinking = std::string(css) +
        "nav{width:120px;right:auto}.hint{flex:1;min-width:0}.btn{min-width:0;padding-left:22px}";
    check_with_sheet("natural flex width after shrinking", html, shrinking.c_str(), "#button",
                     "style", "padding-left:0");

    // Switching between preserved and collapsed newlines changes intrinsic
    // contributions. Retained sibling lines must still match a fresh tree.
    const char* multiline = "<main><div id=text>aa bbbb\ncc</div>"
                            "<div>unchanged\nsibling</div></main>";
    for (const char* display : {"flex", "grid"}) {
        for (const char* before : {"normal", "pre", "pre-wrap", "pre-line"}) {
            const std::string sheet = std::string("main{display:") + display +
                ";width:140px;grid-template-columns:max-content max-content;gap:8px}"
                "main>div{font-size:16px;white-space:" + before + ";background:#abc}";
            for (const char* after : {"normal", "pre", "pre-wrap", "pre-line"}) {
                const std::string style = std::string("white-space:") + after;
                check_with_sheet("forced-break intrinsic width after restyle", multiline,
                                 sheet.c_str(), "#text", "style", style.c_str());
            }
        }
    }
}

// Reflow the surrounding flex allocation while a large grid stays clean.
// Advance far enough to cross animation reversals, then change the grid,
// viewport, and a width allocation so both retention and materialization run.
void test_abi_incremental_grid_animation() {
    const std::string css =
        "*{box-sizing:border-box}html,body{margin:0;height:100%}"
        ".app{display:flex;flex-direction:column;height:100vh;padding:6px;gap:7px}"
        ".live{display:flex;gap:4px}.counter{width:70px;animation:grow .9s linear infinite alternate}"
        "@keyframes grow{from{font-size:10px}to{font-size:24px}}"
        ".gridwrap{flex:1;min-width:0;padding:4px;overflow:auto;border-radius:5px;background:#123}"
        ".grid{display:grid;grid-template-columns:repeat(4,1fr);gap:3px}"
        ".cell{display:flex;flex-direction:column;padding:3px;gap:2px;background:#456}"
        ".label{font-size:11px;text-transform:uppercase}"
        ".bar{height:6px;overflow:hidden}.fill{height:100%;width:60%;background:#acf}";
    std::string html = "<div class=app><div class=live><span class=counter>61%</span>"
                       "<span class=counter>47%</span></div><div class=gridwrap><div class=grid>";
    for (int i = 0; i < 48; ++i)
        html += "<div class=cell><span class=label>cell " + std::to_string(i) +
                "</span><div class=bar><div class=fill></div></div></div>";
    html += "</div></div></div>";
    for (int scenario = 0; scenario < 3; ++scenario) {
        const bool row = scenario == 1;
        const weva_config cfg = config();
        const std::string sheet = css + (row ?
            ".app{flex-direction:row}.live{flex:none;width:80px;animation:wide 1s linear infinite alternate}"
            "@keyframes wide{from{width:80px}to{width:130px}}" : "") +
            (scenario == 2 ? ".bar{height:70%}" : ""); // auto-parent height dependency: reflow
        auto live = weva_document_create(&cfg), full = weva_document_create(&cfg);
        for (auto doc : {live, full}) {
            CHECK(weva_document_add_css(doc, sheet.data(), sheet.size()) == WEVA_OK);
            CHECK(weva_document_load_html(doc, html.data(), html.size()) == WEVA_OK);
            CHECK(weva_document_update(doc, 0) == WEVA_OK);
        }
        std::vector<weva_element_t> elements(512);
        const size_t count = weva_document_query_all(live, "*", elements.data(), elements.size());
        CHECK(count > 0 && count < elements.size());
        for (int frame = 0; frame < 36; ++frame) {
            const int width = frame < 18 ? 400 : 460;
            for (auto doc : {live, full}) {
                if (frame == 18) weva_document_set_viewport(doc, width, 300);
                if (frame == 10 || frame == 22)
                    weva_element_set_attribute(doc, weva_document_query(doc, ".grid"),
                                               "style", frame == 10 ? "padding:7px" : "padding:0");
                if (frame == 12 || frame == 24)
                    CHECK(weva_element_set_scroll(doc, weva_document_query(doc, ".gridwrap"),
                                                  0, frame == 12 ? 100 : 0) == WEVA_OK);
                if (frame % 9 == 8)
                    weva_element_set_attribute(doc, weva_document_query(doc, ".cell:last-child .label"),
                                               "style", frame % 2 ? "padding-left:3px" : "padding-left:8px");
            }
            weva_document_set_viewport(full, width + 1, 300);
            weva_document_set_viewport(full, width, 300);
            CHECK(weva_document_update(live, .137) == WEVA_OK);
            CHECK(weva_document_update(full, .137) == WEVA_OK);
            const Frame a = capture(live), b = capture(full);
            if (a != b) std::printf("  animated grid scenario=%d frame=%d: %s\n", scenario, frame, a.diff(b).c_str());
            CHECK(a == b);
            for (size_t i = 0; i < count; ++i) {
                double av[4] = {}, bv[4] = {};
                CHECK(weva_element_bounds(live, elements[i], &av[0], &av[1], &av[2], &av[3]) ==
                      weva_element_bounds(full, elements[i], &bv[0], &bv[1], &bv[2], &bv[3]));
                for (int k = 0; k < 4; ++k) CHECK(av[k] == bv[k]);
            }
        }
        weva_document_destroy(live); weva_document_destroy(full);
    }
}

// Optional integration gate: the shipped samples, with a whole-layout control
// whose DOM, animation clocks and glyph history remain identical. A viewport
// round trip forces recomputation without resetting any of those inputs.
void test_abi_incremental_corpus() {
    const char* corpus = std::getenv("WEVA_INCREMENTAL_CORPUS");
    if (!corpus) return;
    // The corpus is generated (Tools/oracle/harvest.py), not tracked, so the
    // path can be set and still not exist -- which is exactly what CI does.
    // directory_iterator throws, and this build has exceptions off, so an
    // absent corpus has to be checked rather than caught.
    std::error_code corpus_ec;
    if (!std::filesystem::is_directory(corpus, corpus_ec)) return;
    const auto read = [](const std::filesystem::path& p) {
        std::ifstream f(p, std::ios::binary);
        return std::string(std::istreambuf_iterator<char>(f), std::istreambuf_iterator<char>());
    };
    for (const auto& entry : std::filesystem::directory_iterator(corpus)) {
        if (entry.path().extension() != ".html") continue;
        const std::string html = read(entry.path());
        auto css_path = entry.path();
        css_path.replace_extension(".css");
        const std::string css = read(css_path);
        const weva_config cfg = config(1280, 720);
        weva_document_t live = weva_document_create(&cfg), full = weva_document_create(&cfg);
        for (weva_document_t doc : {live, full}) {
            if (!css.empty()) CHECK(weva_document_add_css(doc, css.data(), css.size()) == WEVA_OK);
            CHECK(weva_document_load_html(doc, html.data(), html.size()) == WEVA_OK);
            CHECK(weva_document_update(doc, 0) == WEVA_OK);
        }
        std::vector<weva_element_t> elements(16384);
        const size_t count = weva_document_query_all(live, "*", elements.data(), elements.size());
        CHECK(count > 0 && count <= elements.size());
        if (count == 0 || count > elements.size()) {
            weva_document_destroy(live); weva_document_destroy(full); continue;
        }
        // WEVA_INCREMENTAL_TARGETS=N adds N evenly spaced elements, each given
        // layout edits that reach relative and absolute boxes as well: insets
        // move a positioned box, a width resizes it, padding reflows it.
        std::vector<std::pair<weva_element_t, const char*>> steps;
        for (int step = 0; step < 9; ++step)
            steps.emplace_back(elements[step < 6 ? count - 1 : count / 2],
                               step < 3 ? (step % 2 ? "background:#123456" : "background:#123457")
                                        : (step % 2 ? "padding-left:11px" : "padding-left:12px"));
        const char* extra = std::getenv("WEVA_INCREMENTAL_TARGETS");
        const size_t targets = extra ? std::min(count, static_cast<size_t>(std::atoi(extra))) : 0;
        for (size_t t = 0; t < targets; ++t) {
            const weva_element_t target = elements[t * count / targets];
            for (const char* style : {"padding-left:11px", "top:3px;left:2px", "width:37px",
                                      "padding-left:12px;top:4px", ""})
                steps.emplace_back(target, style);
        }
        for (size_t step = 0; step < steps.size(); ++step) {
            const weva_element_t target = steps[step].first;
            const char* style = steps[step].second;
            for (weva_document_t doc : {live, full})
                CHECK(weva_element_set_attribute(doc, target, "style", style) == WEVA_OK);
            weva_document_set_viewport(full, 1281, 720);
            weva_document_set_viewport(full, 1280, 720);
            const double dt = step < 6 || step >= 9 ? 0 : 1.0 / 60;
            CHECK(weva_document_update(live, dt) == WEVA_OK);
            CHECK(weva_document_update(full, dt) == WEVA_OK);
            const Frame a = capture(live), b = capture(full);
            if (a != b) std::printf("  corpus %s step %zu (%s): %s\n", entry.path().stem().string().c_str(),
                                    step, style, a.diff(b).c_str());
            CHECK(a == b);
            for (size_t i = 0; i < count; ++i) {
                double av[4] = {}, bv[4] = {};
                CHECK(weva_element_bounds(live, elements[i], &av[0], &av[1], &av[2], &av[3]) ==
                      weva_element_bounds(full, elements[i], &bv[0], &bv[1], &bv[2], &bv[3]));
                for (int k = 0; k < 4; ++k) {
                    if (av[k] != bv[k]) std::printf("  corpus %s step %zu element %zu axis %d: %.17g vs %.17g\n",
                        entry.path().stem().string().c_str(), step, i, k, av[k], bv[k]);
                    CHECK(av[k] == bv[k]);
                }
            }
        }
        std::printf("  incremental corpus: %s\n", entry.path().stem().string().c_str());
        weva_document_destroy(live); weva_document_destroy(full);
    }
}


void test_abi_incremental_backdrop_lifecycle() {
    const char* html = "<section id=parent><dialog id=d>Dialog</dialog>"
        "<div id=p popover>Popover</div><div id=stable>Stable</div></section>";
    const char* css = "html,body{margin:0}#parent{--shade:rgba(20,40,80,.5)}"
        "dialog,[popover]{width:120px;height:60px}"
        "::backdrop{background:var(--shade)}"
        ".hot::backdrop{background:rgba(80,40,20,.7)}"
        "#stable{width:100px;height:40px;background:green}";
    const auto cfg = config();
    const auto live = weva_document_create(&cfg), full = weva_document_create(&cfg);
    for (const auto doc : {live, full}) {
        weva_document_add_css(doc, css, std::strlen(css));
        weva_document_load_html(doc, html, std::strlen(html));
        CHECK(weva_document_update(doc, 0) == WEVA_OK);
    }
    for (int round = 0; round < 3; ++round) for (int step = 0; step < 12; ++step) {
        for (const auto doc : {live, full}) {
            const auto d = weva_document_query(doc, "#d"), p = weva_document_query(doc, "#p");
            switch (step) {
                case 0: weva_element_show_dialog(doc, d, 1); break;
                case 1: weva_element_show_popover(doc, p); break;
                case 2: weva_element_set_attribute(doc, d, "class", "hot"); break;
                case 3: weva_element_show_dialog(doc, d, 0); break;
                case 4: weva_element_hide_popover(doc, p); break;
                case 5:
                    // Neither backdrop is generated now. Reopening must
                    // consume changes to both own and inherited declarations.
                    weva_element_set_attribute(doc, d, "class", "");
                    weva_element_set_attribute(doc, p, "class", "hot");
                    weva_element_set_attribute(doc, weva_document_query(doc, "#parent"),
                        "style", round % 2 ? "--shade:rgba(60,20,80,.3)" : "--shade:rgba(20,80,60,.8)");
                    break;
                case 6: weva_element_show_dialog(doc, d, 1); weva_element_show_popover(doc, p); break;
                case 7: weva_element_set_attribute(doc, p, "class", ""); break;
                case 8: weva_element_close_dialog(doc, d); weva_element_hide_popover(doc, p); break;
                case 9: weva_element_show_popover(doc, p); weva_element_show_dialog(doc, d, 1); break;
                case 10: weva_element_remove(doc, d); weva_element_remove(doc, p); break;
                case 11: weva_document_load_html(doc, html, std::strlen(html)); break;
            }
        }
        weva_document_set_viewport(full, 401, 300);
        weva_document_set_viewport(full, 400, 300);
        CHECK(weva_document_update(live, 0) == WEVA_OK);
        CHECK(weva_document_update(full, 0) == WEVA_OK);
        const auto a = capture(live), b = capture(full);
        if (a != b) std::printf("  backdrop lifecycle %d/%d: %s\n", round, step, a.diff(b).c_str());
        CHECK(a == b);
    }
    weva_document_destroy(live);
    weva_document_destroy(full);
}

void test_abi_incremental_form_state() {
    // Type is a layout input even when author rules suppress all UA style
    // differences. Compare the whole frame with a fresh document in both
    // directions, including type removal and invalid/mixed-case values.
    const char* baseline_css =
        "body{margin:0;padding:12px;font:16px sans-serif}"
        "#field{display:inline-block;box-sizing:border-box;width:90px;height:34px;"
        "margin:7px 0 2px;padding:3px 4px 5px;border:1px solid #555;"
        "border-radius:0;background:white;color:black;font:inherit;overflow:hidden}";
    for (const char* initial : {"text", "checkbox", "radio", "range", "image"}) {
        const std::string html = std::string("<input id=field type='") + initial +
            "' value='1'><span>Label</span>";
        for (const char* type : {"text", "number", "checkbox", "RADIO", "range", "image", "unknown", ""})
            check_with_sheet("input type baseline", html.c_str(), baseline_css, "#field", "type",
                             *type ? type : nullptr);
    }
    const char* html = "<form id=f><input id=t value=seed placeholder=hint>"
        "<textarea id=a placeholder=hint>seed</textarea><input id=c type=checkbox checked>"
        "<select id=s><option value=a selected>A</option><option value=b>B</option></select>"
        "<input id=r type=range value=2 step=3 max=20></form><div id=stable>stable content</div>";
    const char* css = "html,body{margin:0}input,textarea,select{display:block;width:150px;height:40px}"
        "textarea{height:60px;white-space:pre-wrap}input:checked+select{color:red}"
        "input[value=next]{border:2px solid green}textarea:placeholder-shown{padding:4px}"
        "#stable{background:linear-gradient(red,blue);width:100px;height:40px}";
    const weva_config cfg = config();
    const auto live = weva_document_create(&cfg), full = weva_document_create(&cfg);
    for (const auto doc : {live, full}) {
        weva_document_add_css(doc, css, std::strlen(css));
        weva_document_load_html(doc, html, std::strlen(html)); weva_document_update(doc, 0);
        weva_document_set_focus(doc, weva_document_query(doc, "#a")); weva_document_update(doc, 0);
    }
    for (int round = 0; round < 3; ++round) for (int step = 0; step < 12; ++step) {
        const std::string long_value = std::string(round ? 512 : 40, 'a') + "\nbeta gamma";
        for (const auto doc : {live, full}) {
            const auto a = weva_document_query(doc, "#a"), t = weva_document_query(doc, "#t");
            switch (step) {
                case 0: case 1:
                    weva_element_set_value(doc, t, long_value.c_str());
                    weva_element_set_value(doc, a, long_value.c_str()); break;
                case 2: weva_element_set_value(doc, weva_document_query(doc, "#s"), "b"); break;
                case 3: weva_element_set_value(doc, weva_document_query(doc, "#c"), ""); break;
                case 4:
                    weva_element_set_attribute(doc, t, "value", "next");
                    weva_element_set_text(doc, a, "new default\nsecond line"); break;
                case 5: case 9: case 11: weva_document_reset_form(doc, weva_document_query(doc, "#f")); break;
                case 6: weva_element_set_selection(doc, a, 2, 6); break;
                case 7: weva_element_set_value(doc, t, ""); weva_element_set_value(doc, a, ""); break;
                case 8: weva_element_set_value(doc, weva_document_query(doc, "#r"), "10"); break;
                case 10: weva_element_set_attribute(doc, a, "style", round % 2 ? "padding:2px" : "padding:8px"); break;
            }
        }
        weva_document_set_viewport(full, 401, 300); weva_document_set_viewport(full, 400, 300);
        weva_document_update(live, 0); weva_document_update(full, 0);
        const Frame a = capture(live), b = capture(full);
        if (a != b) std::printf("  form state %d/%d: %s\n", round, step, a.diff(b).c_str());
        CHECK(a == b);
    }
    weva_document_destroy(live); weva_document_destroy(full);
}

void test_abi_incremental_select_color_scopes() {
    const char* html = "<form id=f><select id=s multiple size=4>"
        "<option id=a value=a selected>Alpha</option><optgroup label=Group>"
        "<option id=b value=b>Beta</option><option id=c value=c>Charlie</option>"
        "</optgroup></select><div id=indicator>Inherited <span>inline <b>text</b></span></div>"
        "<div id=stable>stable <em>text</em></div><button id=other type=button>Other</button></form>";
    const char* base = "html,body{margin:0}select{display:block;width:230px;height:90px}"
        "option{height:25px}#indicator{color:red;border:2px solid currentColor;"
        "text-decoration:underline;text-shadow:1px 1px currentColor}"
        "#stable{color:purple}#f.tone #indicator{color:blue}";
    for (const char* dependency : {"", "option:checked + option{color:green;padding-left:14px}",
            "form:has(option[value=b]:checked) #indicator{color:green;padding:8px}"}) {
        const std::string css = std::string(base) + dependency;
        const auto cfg = config();
        const auto live = weva_document_create(&cfg), full = weva_document_create(&cfg);
        for (const auto doc : {live,full}) {
            weva_document_add_css(doc,css.data(),css.size());
            weva_document_load_html(doc,html,std::strlen(html));
            CHECK(weva_document_update(doc,0) == WEVA_OK);
        }
        for (int step=0;step<10;++step) {
            for (const auto doc : {live,full}) {
                const auto s = weva_document_query(doc,"#s");
                const auto f = weva_document_query(doc,"#f");
                switch (step) {
                    case 0: weva_element_set_value(doc,s,"b"); break;
                    case 1: weva_element_set_value(doc,s,"c"); break;
                    case 2: weva_element_set_attribute(doc,f,"class","tone"); break;
                    case 3: weva_element_set_value(doc,s,"a,b,c"); break;
                    case 4:
                        // Overlapping ancestor/descendant scopes, in reverse
                        // order, must see final inherited and sibling inputs.
                        weva_element_set_attribute(doc,weva_document_query(doc,"#indicator span"),"style","font-weight:bold");
                        weva_element_set_attribute(doc,f,"class","");
                        weva_element_set_value(doc,s,""); break;
                    case 5: weva_document_reset_form(doc,f); break;
                    case 6:
                        weva_document_set_focus(doc,s);
                        weva_document_try_text_input(doc,"b"); break;
                    case 7: weva_document_key(doc,WEVA_KEY_DOWN,WEVA_MOD_CTRL,1); break;
                    case 8:
                        // Input queues cannot retain an option removed before
                        // the frame that would consume its state version.
                        weva_element_set_value(doc,s,"b");
                        weva_element_remove(doc,weva_document_query(doc,"#b")); break;
                    case 9:
                        weva_document_load_html(doc,html,std::strlen(html));
                        weva_document_set_focus(doc,weva_document_query(doc,"#s")); break;
                }
            }
            weva_document_set_viewport(full,401,300); weva_document_set_viewport(full,400,300);
            CHECK(weva_document_update(live,0) == WEVA_OK); CHECK(weva_document_update(full,0) == WEVA_OK);
            const auto a=capture(live), b=capture(full);
            if (a != b) std::printf("  select color scope %s / %d: %s\n",dependency,step,a.diff(b).c_str());
            CHECK(a == b);
        }
        weva_document_destroy(live); weva_document_destroy(full);
    }
}

void test_abi_incremental_select_autoscroll() {
    for(bool nested : {false,true}) {
        AutoScrollDoc live,full;
        for(auto* d : {&live,&full}) {
            if(nested) {weva_element_set_scroll(d->doc,d->at("#outer"),0,30);d->update();}
            d->start();
        }
        for(int step=0;step<24;++step) {
            if(step==6) for(auto* d : {&live,&full})
                weva_element_set_attribute(d->doc,d->at("#outer"),"style","top:45px;width:230px");
            if(step==8) for(auto* d : {&live,&full})
                weva_element_set_attribute(d->doc,d->at("#s"),"style","height:80px");
            if(step==12) {
                for(auto* d : {&live,&full}) {
                    d->row(18);
                    const auto b=d->bounds("#s"); d->pointer(b.x+b.w/2,b.y-25);
                }
            }
            if(step==20) for(auto* d : {&live,&full}) weva_document_clear_pointer(d->doc);
            weva_document_set_viewport(full.doc,641,480);weva_document_set_viewport(full.doc,640,480);
            live.update(0.05);full.update(0.05);
            CHECK(live.scroll()==full.scroll()); CHECK(live.selected()==full.selected());
            const auto a=capture(live.doc),b=capture(full.doc);
            if(a!=b) std::printf("  autoscroll nested %d / %d: %s\n",nested,step,a.diff(b).c_str());
            CHECK(a==b);
        }
    }
}

void test_abi_incremental_text_autoscroll() {
    for(const char* kind : {"text","password","textarea"})for(bool nested : {false,true}){
        TextScrollDoc live(kind,nested),full(kind,nested);
        for(auto* d : {&live,&full}){
            if(nested){weva_element_set_scroll(d->doc,d->at("#outer"),0,20);d->update();}
            d->start(std::strcmp(kind,"textarea")==0?2:0);
        }
        for(int step=0;step<36;++step){
            for(auto* d : {&live,&full}){
                if(step==4)weva_element_set_attribute(d->doc,d->at("#outer"),"style","top:60px;width:240px");
                if(step==6)weva_element_set_attribute(d->doc,d->at("#f"),"style","width:140px;padding:3px;font-size:15px");
                if(step==18){const auto b=d->bounds();d->pointer(b.x-35,b.y-35);}
                if(step==30)weva_document_clear_pointer(d->doc);
            }
            weva_document_set_viewport(full.doc,641,480);weva_document_set_viewport(full.doc,640,480);
            live.update(0.05);full.update(0.05);
            CHECK(live.selection()==full.selection());CHECK(live.scroll()==full.scroll());
            const auto a=capture(live.doc),b=capture(full.doc);
            if(a!=b)std::printf("  text autoscroll %s nested %d / %d: %s\n",kind,nested,step,a.diff(b).c_str());
            CHECK(a==b);
        }
    }
}

void test_abi_incremental_direct_text() {
    // Text can change line structure, intrinsic sizing, :empty and selectors
    // outside its owner. Compare every step with a forced full layout, keeping
    // the same glyph history. Include old text buffers larger than SSO so an
    // unsafe retained view is visible under ASan.
    const char* html = "<main id=root><section id=area><div id=preceding>Before</div><div id=label>100</div>"
        "<span id=next>Following sibling</span><div id=mixed>prefix <b>ICON</b> suffix</div>"
        "<div id=hidden>hidden label</div><div id=bar></div></section>"
        "<aside>stable text <b>bold</b><span>more</span></aside>"
        "<aside>another stable region 0123456789 <b>bold</b><span>more</span></aside></main>";
    const std::string base = "html,body{margin:0}#area{width:240px}"
        "#label{width:45px;min-height:18px}#bar{width:80px;height:4px;background:red}"
        "#hidden{display:none}#mixed{width:150px}#next{color:blue}"
        "#label:empty{background:green}"
        "#mixed::before{content:'*'}";
    for (const char* layout : {"", "#area{display:flex;flex-wrap:wrap;gap:4px}",
            "#area{display:grid;grid-template-columns:auto 1fr}",
            "#area{display:inline-block;width:auto}#label{width:auto}",
            "#area{display:table}#label,#mixed{display:table-cell}",
            "#label{display:contents}", "#root:has(#label:empty) aside{color:purple}",
            "#area>div:nth-child(2 of :not(:empty)){color:purple}",
            "#area>div:nth-last-child(4 of :not(:empty)){color:green}"}) {
        // Filtered-rank cases must stand alone: an unrelated '+' rule would
        // already disable the match cache and mask their own classification.
        const std::string css = base + layout + (std::strstr(layout,"nth-") ? "" :
            "#label:empty + #next{font-size:22px}");
        const auto cfg = config();
        const auto live = weva_document_create(&cfg), full = weva_document_create(&cfg);
        for (const auto doc : {live,full}) {
            weva_document_add_css(doc,css.data(),css.size());
            weva_document_load_html(doc,html,std::strlen(html));
            CHECK(weva_document_update(doc,0) == WEVA_OK);
        }
        for (int step=0;step<15;++step) {
            size_t old_count = 0;
            const auto* old_versions = weva_document_draw_versions(live,&old_count);
            const std::vector<uint64_t> before(old_versions,old_versions+old_count);
            for (const auto doc : {live,full}) {
                const auto label = weva_document_query(doc,"#label");
                switch (step) {
                    case 0: weva_element_set_text(doc,label,"99"); break;
                    case 1: weva_element_set_text(doc,label,""); break;
                    case 2: weva_element_set_text(doc,label,"a long changing label that wraps onto several lines"); break;
                    case 3: weva_element_set_text(doc,label,"8"); break;
                    case 4:
                        weva_element_set_text(doc,label,"intermediate");
                        weva_element_set_text(doc,label,"7");
                        weva_element_set_style(doc,weva_document_query(doc,"#bar"),"width","37px"); break;
                    case 5:
                        weva_element_set_text(doc,weva_document_query(doc,"#mixed"),"replacement beside the preserved icon");
                        weva_element_set_attribute(doc,weva_document_query(doc,"#area"),"style","font-size:19px"); break;
                    case 6: weva_element_set_text(doc,weva_document_query(doc,"#hidden"),"new hidden text"); break;
                    case 7: weva_element_set_style(doc,weva_document_query(doc,"#hidden"),"display","block"); break;
                    case 8: weva_element_set_text(doc,weva_document_query(doc,"#mixed"),""); break;
                    case 9:
                        weva_element_set_text(doc,label,"queued then removed");
                        weva_element_remove(doc,label); break;
                    case 10:
                        weva_element_set_text(doc,weva_document_query(doc,"#mixed"),"queued then replaced");
                        weva_element_set_html(doc,weva_document_query(doc,"#area"),"<div id=label>new element</div>",
                                              std::strlen("<div id=label>new element</div>")); break;
                    case 11:
                        weva_element_set_text(doc,label,"queued before reload");
                        weva_document_load_html(doc,html,std::strlen(html)); break;
                    case 12:
                        weva_element_set_text(doc,label,"queued before viewport change");
                        weva_document_set_viewport(doc,420,320); break;
                    case 13: weva_element_set_text(doc,label,"100"); break;
                    case 14: weva_element_set_text(doc,label,"100"); break;
                }
            }
            const int width = step >= 12 ? 420 : 400, height = step >= 12 ? 320 : 300;
            weva_document_set_viewport(full,width+1,height); weva_document_set_viewport(full,width,height);
            CHECK(weva_document_update(live,0) == WEVA_OK); CHECK(weva_document_update(full,0) == WEVA_OK);
            const auto a=capture(live), b=capture(full);
            if (a != b) std::printf("  direct text %s / %d: %s\n",layout,step,a.diff(b).c_str());
            CHECK(a == b);
            if (step == 0 && !*layout) {
                size_t count = 0, reused = 0;
                const auto* versions = weva_document_draw_versions(live,&count);
                for (size_t i=0;i<count;++i)
                    if (std::find(before.begin(),before.end(),versions[i]) != before.end()) ++reused;
                CHECK(reused > 0 && reused < count);
            }
        }
        weva_document_destroy(live); weva_document_destroy(full);
    }
}


void test_abi_incremental_bindings() {
    const char* html = "<main id=root><section id=area><div id=before>before</div>"
        "<div id=label>{{ Label }}</div><span id=after>after</span>"
        "<div id=mixed>prefix <b>{{ Bold }}</b> {{ Suffix }}</div>"
        "<div id=hidden>{{ Hidden }}</div><div id=bar style='width:{{ Width }}px'></div>"
        "<button id=button disabled='{{ Disabled }}' data-class-hot='Hot'>Use</button></section>"
        "<div id=list><template data-each='Items as item' data-key='Id'>"
        "<div class=row><span>{{ item.Name }}</span><input value='{{ item.Name }}'></div>"
        "</template></div><aside>unchanged region <b>bold</b> more unchanged text</aside></main>";
    const std::string base = "html,body{margin:0}#area{width:240px}#label{width:45px;min-height:18px}"
        "#hidden{display:none}#mixed{width:150px}#bar{height:4px;background:red}"
        "#label:empty{background:green}button:disabled{color:gray}.hot{background:orange}";
    for (const char* layout : {"", "#area{display:flex;flex-wrap:wrap;gap:4px}",
            "#area{display:grid;grid-template-columns:auto 1fr}",
            "#area{display:inline-block;width:auto}#label{width:auto}",
            "#label{display:contents}", "#root:has(#label:empty) aside{color:purple}",
            "#area>div:nth-last-child(3 of :not(:empty)){color:green}",
            "#area>div:nth-last-child(3 of [style*='37']){color:purple}"}) {
        const std::string css = base + layout;
        const auto cfg = config();
        const auto live = weva_document_create(&cfg), full = weva_document_create(&cfg);
        IncrementalBindingData data;
        data.values = {{"Label","100"},{"Bold","ICON"},{"Suffix","suffix"},{"Hidden","hidden"},
            {"Width","80"},{"Disabled","false"},{"Hot","false"},
            {"Items.0.Id","a"},{"Items.0.Name","first"},{"Items.1.Id","b"},{"Items.1.Name","second"}};
        weva_binding_source source{&data, &IncrementalBindingData::read, &IncrementalBindingData::count};
        for (const auto doc : {live, full}) {
            weva_document_add_css(doc, css.data(), css.size());
            weva_document_load_html(doc, html, std::strlen(html));
            weva_document_set_binding_source(doc, &source);
            weva_document_refresh_bindings(doc);
            CHECK(weva_document_update(doc, 0) == WEVA_OK);
        }
        for (int step = 0; step < 19; ++step) {
            size_t old_count = 0;
            const auto* old = weva_document_draw_versions(live, &old_count);
            const std::vector<uint64_t> before(old, old + old_count);
            switch (step) {
                case 0: data.values["Label"] = "101"; break;
                case 1: data.values["Label"] = ""; break;
                case 2: data.values["Label"] = std::string(140, 'w'); break;
                case 3: data.values["Label"] = "8"; data.values["Width"] = "37"; break;
                case 4: data.values["Bold"] = "new icon"; data.values["Suffix"] = "new suffix"; break;
                case 5: data.values["Disabled"] = "true"; data.values["Hot"] = "true"; break;
                case 6: data.values["Hidden"] = "new hidden text"; break;
                case 7: data.values["Items.0.Name"] = "renamed row"; break;
                case 8: data.rows = 0; break;
                case 9: data.rows = 2; break;
                case 10: std::swap(data.values["Items.0.Id"], data.values["Items.1.Id"]); break;
                case 11: data.values["Label"] = "queued then removed"; break;
                case 12: data.values["Width"] = "38"; break;
                case 13: data.values["Bold"] = "queued before reload"; break;
                case 14: data.values["Width"] = "39"; break;
                case 15: data.values["Disabled"] = "false"; data.values["Hot"] = "false"; break;
                case 16: data.values["Items.0.Id"] = "replacement"; break;
                case 17: data.values["Label"] = "100"; break;
                case 18: break;
            }
            for (const auto doc : {live, full}) {
                const int changed = weva_document_refresh_bindings(doc);
                if (step == 18) CHECK(changed == 0);
                if (step == 6) weva_element_set_style(doc, weva_document_query(doc,"#hidden"), "display", "block");
                if (step == 11) weva_element_remove(doc, weva_document_query(doc,"#label"));
                if (step == 12) {
                    const char* fragment = "<div id=label>{{ Label }}</div><div id=bar style='width:{{ Width }}px'></div>";
                    weva_element_set_html(doc, weva_document_query(doc,"#area"), fragment, std::strlen(fragment));
                    weva_document_refresh_bindings(doc);
                }
                if (step == 13) {
                    weva_document_load_html(doc, html, std::strlen(html));
                    weva_document_refresh_bindings(doc);
                }
                if (step == 14) weva_document_set_viewport(doc, 420, 320);
                // Two structural refreshes before one update must also forget
                // removed controls/templates before allocator address reuse.
                if (step == 16) {
                    data.rows = 0; weva_document_refresh_bindings(doc);
                    data.rows = 2; weva_document_refresh_bindings(doc);
                }
            }
            const int width = step >= 14 ? 420 : 400, height = step >= 14 ? 320 : 300;
            weva_document_set_viewport(full, width+1, height); weva_document_set_viewport(full, width, height);
            CHECK(weva_document_update(live, 0) == WEVA_OK);
            CHECK(weva_document_update(full, 0) == WEVA_OK);
            const auto a = capture(live), b = capture(full);
            if (a != b) {
                std::printf("  binding %s / %d: %s\n", layout, step, a.diff(b).c_str());
                for (const char* sel : {"#area","#list","#list>.row", "#list>.row span"}) {
                    for (auto doc : {live, full}) { double x,y,w,h; weva_element_bounds(doc,weva_document_query(doc,sel),&x,&y,&w,&h);
                        std::printf("%s %s %g %g %g %g\n",doc == live ? "live" : "full",sel,x,y,w,h); }
                }
            }
            CHECK(a == b);
            if (step == 0 && !*layout) {
                size_t count = 0, reused = 0;
                const auto* versions = weva_document_draw_versions(live, &count);
                for (size_t i=0; i<count; ++i)
                    if (std::find(before.begin(), before.end(), versions[i]) != before.end()) ++reused;
                CHECK(reused > 0 && reused < count);
            }
            if (step == 18) {
                const auto serial = weva_document_draw_serial(live);
                CHECK(weva_document_refresh_bindings(live) == 0);
                CHECK(weva_document_update(live, 0) == WEVA_OK);
                CHECK(weva_document_draw_serial(live) == serial);
            }
        }
        weva_document_destroy(live); weva_document_destroy(full);
    }
}


void test_abi_incremental_caret_reuse() {
    const char* html = "<main><input id=a value='a long starting value'><input id=b value=second>"
        "<textarea id=t>first line\nsecond line\nthird line</textarea></main>"
        "<aside>unchanged HUD <strong>100 HEALTH</strong></aside>"
        "<aside>unchanged inventory <b>WOOD 24</b><span>STONE 12</span></aside>";
    const char* css = "html,body{margin:0}input{display:block;width:100px;height:24px}"
        "textarea{width:160px;height:40px}aside{background:#345;color:white}"
        "input:focus{border-color:red}textarea:focus{color:blue}";
    const auto cfg = config();
    const auto live = weva_document_create(&cfg), full = weva_document_create(&cfg);
    for (const auto doc : {live,full}) {
        weva_document_add_css(doc,css,std::strlen(css));
        weva_document_load_html(doc,html,std::strlen(html));
        weva_document_update(doc,0);
        weva_document_set_focus(doc,weva_document_query(doc,"#a"));
        weva_document_update(doc,0);
    }
    for (int step=0; step<13; ++step) {
        size_t old_count=0;
        const auto* old=weva_document_draw_versions(live,&old_count);
        const std::vector<uint64_t> before(old,old+old_count);
        for (const auto doc : {live,full}) {
            switch(step) {
                case 0: break; // blink without a DOM/style mutation
                case 1: weva_document_select_all(doc); break;
                case 2: weva_document_try_text_input(doc,"replacement"); break;
                case 3: weva_element_set_selection(doc,weva_document_query(doc,"#a"),2,6); break;
                case 4: weva_document_set_composition(doc,"composing",0,9); break;
                case 5: weva_document_set_composition(doc,"composing",2,5); break;
                case 6: weva_document_commit_composition(doc,"done"); break;
                case 7: weva_document_undo(doc); break;
                case 8: weva_document_redo(doc); break;
                case 9: weva_document_set_focus(doc,weva_document_query(doc,"#b")); break;
                case 10: weva_document_set_focus(doc,weva_document_query(doc,"#t")); break;
                case 11: weva_document_select_all(doc); break;
                case 12: weva_document_set_focus(doc,WEVA_ELEMENT_NONE); break;
            }
        }
        weva_document_set_viewport(full,401,300); weva_document_set_viewport(full,400,300);
        const double dt = step == 0 ? 0.55 : 0;
        weva_document_update(live,dt); weva_document_update(full,dt);
        const auto a=capture(live), b=capture(full);
        if (a != b) std::printf("  caret reuse %d: %s\n",step,a.diff(b).c_str());
        CHECK(a == b);
        if (step <= 1 || step == 9) {
            size_t count=0, reused=0;
            const auto* versions=weva_document_draw_versions(live,&count);
            for(size_t i=0;i<count;++i)
                if(std::find(before.begin(),before.end(),versions[i])!=before.end()) ++reused;
            CHECK(reused > 0 && reused < count);
        }
    }
    weva_document_destroy(live); weva_document_destroy(full);
}


void test_abi_incremental_keyed_reorder() {
    const char* html = "<main><div id=list><template data-each='Items as item' data-key='Id'>"
        "<section class=row id='row-{{ item.Id }}'><span>{{ item.Name }}</span>"
        "<input id='edit-{{ item.Id }}' value=original></section></template></div></main>"
        "<aside>unchanged HUD <b>health 100</b><p>mission one</p><p>mission two</p>"
        "<p>mission three</p><p>mission four</p><p>mission five</p><p>mission six</p></aside>";
    const std::string base = "html,body{margin:0}#list{height:80px;width:300px;overflow:auto}"
        ".row{height:40px;display:flex}input{width:100px}aside{background:#345;color:white}";
    for (const char* extra : {"", ".row:nth-child(2){color:red}",
            "#list:has(#row-b:first-of-type){color:green}",
            ".row:nth-last-child(1 of [data-weva-index='0']){color:purple}",
            "#list{display:grid;grid-template-columns:1fr 1fr}",
            "#list{height:auto}.row{height:auto}", ".row{position:relative}input{position:absolute}"}) {
        const auto cfg = config();
        const auto live=weva_document_create(&cfg), full=weva_document_create(&cfg);
        IncrementalBindingData data;
        data.values = {{"Items.0.Id","a"},{"Items.0.Name","first"},{"Items.1.Id","b"},{"Items.1.Name","second"}};
        weva_binding_source source{&data,&IncrementalBindingData::read,&IncrementalBindingData::count};
        const auto css=base+extra;
        for(auto doc : {live,full}) {
            weva_document_add_css(doc,css.data(),css.size());
            weva_document_load_html(doc,html,std::strlen(html));
            weva_document_set_binding_source(doc,&source);
            weva_document_refresh_bindings(doc); weva_document_update(doc,0);
            weva_document_set_focus(doc,weva_document_query(doc,"#edit-a"));
            weva_document_select_all(doc); weva_document_try_text_input(doc,"edited");
            weva_document_update(doc,0);
        }
        const auto handle=weva_document_query(live,"#edit-a");
        for(int step=0;step<20;++step) {
            size_t old_count=0; const auto* old=weva_document_draw_versions(live,&old_count);
            const std::vector<uint64_t> before(old,old+old_count);
            std::swap(data.values["Items.0.Id"],data.values["Items.1.Id"]);
            std::swap(data.values["Items.0.Name"],data.values["Items.1.Name"]);
            for(auto doc : {live,full}) {
                CHECK(weva_document_refresh_bindings(doc)>0);
                CHECK(weva_document_refresh_bindings(doc)==0);
            }
            weva_document_set_viewport(full,401,300); weva_document_set_viewport(full,400,300);
            weva_document_update(live,0); weva_document_update(full,0);
            weva_element_t ordered[2];
            CHECK(weva_document_query_all(live,"#list > .row",ordered,2)==2);
            const auto first=weva_document_query(live,("#row-"+data.values["Items.0.Id"]).c_str());
            CHECK(ordered[0]==first);
            CHECK(weva_document_query(live,"#list > .row")==first);
            CHECK(weva_document_query(live,"#edit-a")==handle);
            CHECK(weva_document_focus(live)==handle);
            char value[32]; weva_element_value(live,handle,value,sizeof(value));
            CHECK(std::strcmp(value,"edited")==0);
            const auto a=capture(live),b=capture(full);
            if(a!=b) std::printf("keyed reorder %s step %d: %s\n",extra,step,a.diff(b).c_str());
            CHECK(a==b);
            if(!*extra) {
                size_t count=0,reused=0; const auto* versions=weva_document_draw_versions(live,&count);
                for(size_t i=0;i<count;++i) if(std::find(before.begin(),before.end(),versions[i])!=before.end()) ++reused;
                CHECK(reused>0 && reused<count);
            }
        }
        CHECK(weva_document_undo(live)==1);
        char value[32]; weva_element_value(live,handle,value,sizeof(value));
        CHECK(std::strcmp(value,"original")==0);
        // Duplicate identities deliberately retain the conservative rebuild.
        data.values["Items.0.Id"]=data.values["Items.1.Id"]="duplicate";
        weva_document_refresh_bindings(live); weva_document_update(live,0);
        CHECK(weva_document_focus(live)==WEVA_ELEMENT_NONE);
        weva_document_destroy(live); weva_document_destroy(full);
    }
}

void test_abi_incremental_container_queries() {
    const char* html="<div id=outer style='width:{{ Width }}px'><div id=inner><div id=item>value</div></div></div>"
        "<aside>Stable HUD <b>100</b></aside>";
    const char* css="html,body{margin:0}#outer{container-type:inline-size;max-width:100%}"
        "#inner{container-type:inline-size;width:100px}#item{width:40px;height:10px;background:red}"
        "aside{background:blue;height:30px}"
        "@container (width>=300px){#inner{width:300px}#item{height:35px;background:green}"
        "#item::after{content:'';display:block;height:5px;background:yellow}}";
    const auto cfg=config();
    const auto live=weva_document_create(&cfg);
    IncrementalBindingData data; data.values={{"Width","400"}};
    weva_binding_source source{&data,&IncrementalBindingData::read,&IncrementalBindingData::count};
    CHECK(weva_document_add_css(live,css,std::strlen(css))==WEVA_OK);
    CHECK(weva_document_load_html(live,html,std::strlen(html))==WEVA_OK);
    weva_document_set_binding_source(live,&source);
    int viewport=400;
    for (int step=0;step<10;++step) {
        data.values["Width"]=step%2 ? "250" : "400";
        if (step==4) viewport=200;
        if (step==6) viewport=400;
        weva_document_set_viewport(live,viewport,300);
        CHECK(weva_document_refresh_bindings(live)>=0);
        CHECK(weva_document_update(live,0)==WEVA_OK);
        double x,y,w,h;
        CHECK(weva_element_bounds(live,weva_document_query(live,"#item"),&x,&y,&w,&h)==WEVA_OK);
        CHECK(std::fabs(h-((step%2==0 && viewport>=300) ? 35 : 10))<1e-6);
        const auto frame=capture(live);
        const auto fresh_cfg=config(viewport,300);
        const auto fresh=weva_document_create(&fresh_cfg);
        weva_document_add_css(fresh,css,std::strlen(css));
        weva_document_load_html(fresh,html,std::strlen(html));
        weva_document_set_binding_source(fresh,&source);
        weva_document_refresh_bindings(fresh);
        CHECK(weva_document_update(fresh,0)==WEVA_OK);
        const auto expected=capture(fresh);
        if (frame!=expected) std::printf("container step %d: %s\n",step,frame.diff(expected).c_str());
        CHECK(frame==expected);
        weva_document_destroy(fresh);
        const auto serial=weva_document_draw_serial(live);
        CHECK(weva_document_update(live,0)==WEVA_OK);
        CHECK(weva_document_draw_serial(live)==serial);
    }
    // Replacing the sheet changes query indices and must retire the old input set.
    const char* replacement="#outer{container-type:inline-size;width:400px}#item{height:11px}"
        "@container (width < 300px){#item{height:27px}}";
    CHECK(weva_document_set_css(live,replacement,std::strlen(replacement))==WEVA_OK);
    CHECK(weva_document_update(live,0)==WEVA_OK);
    double x,y,w,h;
    CHECK(weva_element_bounds(live,weva_document_query(live,"#item"),&x,&y,&w,&h)==WEVA_OK);
    CHECK(std::fabs(h-27)<1e-6); // Last bound Width is 250; inner no longer a container.
    const auto outer=weva_document_query(live,"#outer");
    for (int i=0;i<5;++i) {
        CHECK(weva_element_remove(live,weva_document_query(live,"#inner"))==WEVA_OK);
        CHECK(weva_document_update(live,0)==WEVA_OK);
        const char* child="<div id=inner><div id=item>replacement</div></div>";
        CHECK(weva_element_append_html(live,outer,child,std::strlen(child))!=WEVA_ELEMENT_NONE);
        CHECK(weva_document_update(live,0)==WEVA_OK);
        CHECK(weva_element_bounds(live,weva_document_query(live,"#item"),&x,&y,&w,&h)==WEVA_OK);
        CHECK(std::fabs(h-27)<1e-6);
    }
    const char* named="#outer{container-type:inline-size}#item{height:11px}"
        "@container Hud (width < 300px){#item{height:27px}}";
    CHECK(weva_document_set_css(live,named,std::strlen(named))==WEVA_OK);
    for (const char* name : {"none","Hud","hud","Hud"}) {
        CHECK(weva_element_set_style(live,outer,"container-name",name)==WEVA_OK);
        CHECK(weva_document_update(live,0)==WEVA_OK);
        CHECK(weva_element_bounds(live,weva_document_query(live,"#item"),&x,&y,&w,&h)==WEVA_OK);
        CHECK(std::fabs(h-(std::strcmp(name,"Hud")==0 ? 27 : 11))<1e-6);
    }
    const char* relative="html{font-size:20px}#outer{container-type:inline-size;font-size:25px}"
        "#item{height:11px}@container (width:10em) and (width:12.5rem){#item{height:31px}}";
    CHECK(weva_document_set_css(live,relative,std::strlen(relative))==WEVA_OK);
    CHECK(weva_document_update(live,0)==WEVA_OK);
    CHECK(weva_element_bounds(live,weva_document_query(live,"#item"),&x,&y,&w,&h)==WEVA_OK);
    CHECK(std::fabs(h-31)<1e-6);
    weva_document_destroy(live);
}


void test_abi_inline_fragment_bounds() {
    const auto cfg = config();
    auto doc = weva_document_create(&cfg);
    const char* html = "<div id=outer><span id=wrapper><span id=nested><span id=block>"
        "<span id=overflow></span></span></span></span></div>";
    const char* css = "html,body{margin:0}#outer{width:300px}#block{display:block;height:10px}"
        "#overflow{display:block;width:500px;height:80px}";
    CHECK(weva_document_add_css(doc, css, std::strlen(css)) == WEVA_OK);
    CHECK(weva_document_load_html(doc, html, std::strlen(html)) == WEVA_OK);
    for (int step = 0; step < 6; ++step) {
        const double expected = step % 2 ? 200 : 300;
        CHECK(weva_element_set_style(doc, weva_document_query(doc,"#outer"), "width",
            step % 2 ? "200px" : "300px") == WEVA_OK);
        CHECK(weva_document_update(doc, 0) == WEVA_OK);
        for (const char* selector : {"#wrapper", "#nested", "#block"}) {
            double x, y, w, h;
            CHECK(weva_element_bounds(doc, weva_document_query(doc,selector), &x,&y,&w,&h) == WEVA_OK);
            CHECK(std::fabs(x) < 1e-6 && std::fabs(y) < 1e-6);
            CHECK(std::fabs(w-expected) < 1e-6 && std::fabs(h-10) < 1e-6);
        }
    }
    CHECK(weva_element_set_style(doc, weva_document_query(doc,"#block"), "width", "100px") == WEVA_OK);
    CHECK(weva_element_set_style(doc, weva_document_query(doc,"#block"), "position", "relative") == WEVA_OK);
    CHECK(weva_element_set_style(doc, weva_document_query(doc,"#block"), "left", "20px") == WEVA_OK);
    CHECK(weva_element_set_style(doc, weva_document_query(doc,"#block"), "top", "15px") == WEVA_OK);
    CHECK(weva_document_update(doc, 0) == WEVA_OK);
    for (const char* selector : {"#wrapper", "#nested"}) {
        double x, y, w, h;
        CHECK(weva_element_bounds(doc,weva_document_query(doc,selector),&x,&y,&w,&h) == WEVA_OK);
        CHECK(std::fabs(x) < 1e-6 && std::fabs(y) < 1e-6);
        CHECK(std::fabs(w-200) < 1e-6 && std::fabs(h-10) < 1e-6);
    }
    CHECK(weva_element_set_style(doc,weva_document_query(doc,"#block"),"height","0px") == WEVA_OK);
    CHECK(weva_document_update(doc,0) == WEVA_OK);
    double x,y,w,h;
    CHECK(weva_element_bounds(doc,weva_document_query(doc,"#wrapper"),&x,&y,&w,&h) == WEVA_OK);
    CHECK(w == 0 && h == 0); // Empty continuations do not enlarge client bounds.
    weva_document_destroy(doc);
}

void test_abi_incremental_modal_layout() {
    {
        const auto cfg = config();
        const auto live = weva_document_create(&cfg), full = weva_document_create(&cfg);
        const char* markup = "<section id=panel><p id=text>Health</p><div id=popup popover=manual>Menu</div></section>"
            "<aside id=other>Other</aside><dialog id=modal><button>Confirm</button></dialog>";
        const char* sheet = "#panel{position:absolute;left:0;top:0;width:150px;height:200px;background:blue}"
            "#other{position:absolute;right:0;top:0;width:80px;height:90px;background:green}"
            "#popup{left:180px;top:10px;right:auto;bottom:auto;margin:0;width:70px;height:50px}"
            "dialog{left:180px;top:90px;width:150px;height:100px;margin:0}";
        for (auto d : {live, full}) {
            weva_document_add_css(d, sheet, std::strlen(sheet));
            weva_document_load_html(d, markup, std::strlen(markup));
            weva_document_update(d, 0);
            weva_element_show_popover(d, weva_document_query(d, "#popup"));
            weva_document_update(d, 0);
        }
        for (int step=0; step<6; ++step) {
            for (auto d : {live, full}) {
                const auto modal = weva_document_query(d, "#modal");
                if (step % 2 == 0) weva_element_show_dialog(d, modal, 1);
                else weva_element_close_dialog(d, modal);
                if (step == 2 || step == 4)
                    weva_element_set_style(d, weva_document_query(d, "#panel"), "content-visibility", step == 2 ? "hidden" : "visible");
                if (step == 4) weva_element_set_text(d, weva_document_query(d, "#text"), "Updated health");
            }
            weva_document_set_viewport(full, 401, 300); weva_document_set_viewport(full, 400, 300);
            weva_document_update(live, 0); weva_document_update(full, 0);
            CHECK(capture(live) == capture(full));
        }
        weva_document_destroy(live); weva_document_destroy(full);
    }
    const char* html = "<main><section id=panel><p>Retained health</p><p>Inventory</p>"
        "<div id=inner><b>supplies</b><p>quest one</p><p>quest two</p></div></section>"
        "<aside id=other>Other panel</aside><dialog id=modal><form><p>Settings</p>"
        "<input id=field value=original><button id=close>Close</button></form></dialog></main>";
    const std::string base = "html,body{margin:0;width:100%;height:100%}main{width:100%;height:100%}"
        "#panel{position:absolute;left:10px;top:10px;width:160px;height:160px;background:#235;color:white}"
        "#other{position:absolute;right:10px;bottom:10px;width:80px;height:30px;background:#654}"
        "dialog{position:absolute;left:180px;top:20px;width:180px;padding:10px}input{width:130px}";
    for (const char* extra : {"", "main{position:relative}", "#inner{position:relative;left:4px}",
            "main{transform:translate(3px,4px)}", "main{opacity:.7;overflow:hidden}",
            "main:has(dialog[open]){padding:12px}", "main:has(dialog[open]){opacity:.6}",
            "#panel{overflow:auto}#inner{height:280px}",
            "#panel:before{content:'prefix'}", "main{counter-reset:n}#panel{counter-increment:n}",
            "dialog::backdrop{position:static}", "#inner{position:sticky;top:0}",
            "main{display:flex}", "#panel{anchor-name:--panel}",
            "main{filter:opacity(.8)}"}) {
        const auto cfg=config(); const auto live=weva_document_create(&cfg), full=weva_document_create(&cfg);
        const auto css=base+extra;
        for(auto doc:{live,full}) {
            CHECK(weva_document_add_css(doc,css.data(),css.size())==WEVA_OK);
            CHECK(weva_document_load_html(doc,html,std::strlen(html))==WEVA_OK);
            CHECK(weva_document_update(doc,0)==WEVA_OK);
        }
        for(int step=0;step<24;++step) {
            size_t old_count=0; const auto* old=weva_document_draw_versions(live,&old_count);
            std::vector<uint64_t> previous(old,old+old_count);
            for(auto doc:{live,full}) {
                auto modal=weva_document_query(doc,"#modal");
                if(step%2==0) { weva_element_show_dialog(doc,modal,1); weva_document_set_focus(doc,weva_document_query(doc,"#field")); }
                else { weva_element_close_dialog(doc,modal); weva_document_set_focus(doc,WEVA_ELEMENT_NONE); }
                if(step==2 && std::strstr(extra,"overflow:auto"))
                    weva_element_set_scroll(doc,weva_document_query(doc,"#panel"),0,35);
                if(step==8) weva_element_set_style(doc,weva_document_query(doc,"#panel"),"color","#ffcc00");
                if(step==12) weva_element_set_style(doc,weva_document_query(doc,"#panel"),"width","170px");
                if(step==16) weva_element_set_text(doc,weva_document_query(doc,"#other"),"changed");
            }
            weva_document_set_viewport(full,401,300); weva_document_set_viewport(full,400,300);
            const auto serial_before_geometry = weva_document_draw_serial(live);
            const Frame before_geometry = capture(live);
            CHECK(weva_document_update_geometry(live)==WEVA_OK);
            CHECK(weva_document_update_geometry(live)==WEVA_OK);
            CHECK(weva_document_draw_serial(live)==serial_before_geometry);
            CHECK(capture(live)==before_geometry);
            CHECK(weva_document_update(live,0)==WEVA_OK); CHECK(weva_document_update(full,0)==WEVA_OK);
            auto a=capture(live),b=capture(full);
            if(a!=b) std::printf("modal layout %s step %d: %s\n",extra,step,a.diff(b).c_str());
            CHECK(a==b);
            if(!*extra && step>2 && step<8) {
                size_t count=0,reused=0; const auto* versions=weva_document_draw_versions(live,&count);
                for(size_t i=0;i<count;++i) if(std::find(previous.begin(),previous.end(),versions[i])!=previous.end()) ++reused;
                CHECK(reused>0);
                if(step%2==0) CHECK(reused<count);
            }
            if (step == 5 || step == 19) {
                // A retained HUD must remain addressable by later incremental
                // changes after the modal's boxes have been removed/recycled.
                for (auto doc : {live, full}) {
                    weva_element_set_style(doc, weva_document_query(doc, "#inner"), "width", step == 5 ? "65px" : "110px");
                    weva_element_set_text(doc, weva_document_query(doc, "#other"), step == 5 ? "HUD after close" : "HUD updated again");
                }
                weva_document_set_viewport(full,401,300); weva_document_set_viewport(full,400,300);
                weva_document_update(live,0); weva_document_update(full,0);
                CHECK(capture(live)==capture(full));
            }
        }
        weva_document_destroy(live); weva_document_destroy(full);
    }
}

void test_abi_incremental_has_range_scopes() {
    const char* html = "<form id=f><input id=n type=number min=1 max=10 value=5>"
        "<div id=label>Inherited <span id=child>inline <b>text</b></span></div>"
        "<div id=stable>Stable sibling</div></form><aside id=after>Following sibling</aside>";
    const char* base = "html,body{margin:0}form{--tone:green;color:green}"
        "input{display:block;width:180px;height:24px}#label{border:2px solid currentColor}"
        "#stable{color:purple}form.custom{--tone:purple}";
    for (const char* dependency : {
            "form:has(:out-of-range){background-color:red}",
            "form:has(:out-of-range){color:red}",
            "form:has(:out-of-range){--tone:red}#label{color:var(--tone)}",
            "form:has(:out-of-range) span{color:red;font-weight:bold}",
            "form:has(:out-of-range)+aside{color:red;padding:8px}",
            ":is(form:has(:out-of-range),aside) span{color:red}",
            "form:not(:has(:out-of-range)){color:red}",
            "form:invalid{background-color:red}",
            "form:invalid{color:red}",
            "form:invalid{--tone:red}#label{color:var(--tone)}",
            "form:invalid span{color:red;font-weight:bold}",
            "form:invalid+aside{color:red;padding:8px}",
            ":is(form:invalid,aside) span{color:red}",
            "form:valid{color:red}",
            "form:has(:invalid){color:red}"}) {
        const std::string original = std::string(base) + dependency;
        std::string current = original;
        const auto cfg = config();
        const auto live = weva_document_create(&cfg), full = weva_document_create(&cfg);
        for (const auto doc : {live, full}) {
            CHECK(weva_document_set_css(doc,current.data(),current.size()) == WEVA_OK);
            CHECK(weva_document_load_html(doc,html,std::strlen(html)) == WEVA_OK);
            CHECK(weva_document_update(doc,0) == WEVA_OK);
        }
        for (int step=0;step<15;++step) {
            if (step==10) current = original + " #child{font-size:20px}";
            if (step==11) current = base;
            if (step==12) current = original;
            for (const auto doc : {live,full}) {
                const auto n=weva_document_query(doc,"#n");
                switch (step) {
                    case 0: weva_element_set_value(doc,n,"6"); break;
                    case 1: weva_element_set_value(doc,n,"11"); break;
                    case 2: weva_element_set_value(doc,n,"12"); break;
                    case 3: weva_element_set_value(doc,n,"5"); break;
                    case 4: weva_element_set_attribute(doc,n,"min","6"); break;
                    case 5: weva_element_set_attribute(doc,n,"min","1"); break;
                    case 6: weva_element_set_value(doc,n,"11"); weva_element_set_attribute(doc,n,"disabled",""); break;
                    case 7: weva_element_set_attribute(doc,n,"disabled",nullptr); break;
                    case 8: weva_element_set_value(doc,n,"5"); weva_element_set_value(doc,n,"12"); break;
                    case 9: weva_element_set_attribute(doc,weva_document_query(doc,"#f"),"class","custom"); break;
                    case 10: case 11: case 12:
                        CHECK(weva_document_set_css(doc,current.data(),current.size()) == WEVA_OK); break;
                    case 13: weva_element_set_value(doc,n,"5"); weva_element_remove(doc,n); break;
                    case 14: CHECK(weva_document_load_html(doc,html,std::strlen(html)) == WEVA_OK); break;
                }
            }
            // Clear the reference's match cache and force a complete layout/
            // paint rebuild while preserving exactly the same live form state.
            CHECK(weva_document_set_css(full,current.data(),current.size()) == WEVA_OK);
            weva_document_set_viewport(full,401,300); weva_document_set_viewport(full,400,300);
            CHECK(weva_document_update(live,0) == WEVA_OK);
            CHECK(weva_document_update(full,0) == WEVA_OK);
            const auto a=capture(live), b=capture(full);
            if (a != b) std::printf("  has range scope %s / %d: %s\n",dependency,step,a.diff(b).c_str());
            CHECK(a == b);
        }
        weva_document_destroy(live); weva_document_destroy(full);
    }
}

void test_abi_incremental_multicol_sizing() {
    std::string html = "<div id=m>";
    for (int i=0;i<12;++i) html += i == 4 ? "<div id=heading class=item></div>" : "<div class=item></div>";
    html += "</div><div id=after>After columns</div>";
    const std::string css = "#m{width:632px;column-count:4;column-width:200px;background:#123}"
        ".item{height:20px;background:#456;break-inside:avoid}#after{background:#789}";
    auto cfg = config(); cfg.viewport_width = 1280; cfg.viewport_height = 720;
    const auto live = weva_document_create(&cfg), full = weva_document_create(&cfg);
    for (const auto doc : {live,full}) {
        CHECK(weva_document_load_html(doc,html.data(),html.size()) == WEVA_OK);
        CHECK(weva_document_set_css(doc,css.data(),css.size()) == WEVA_OK);
        CHECK(weva_document_update(doc,0) == WEVA_OK);
    }
    for (const char* style : {"width:1000px", "width:632px", "font-size:24px", "font-size:16px",
            "column-count:2", "column-count:4", "column-width:100px", "column-width:200px",
            "column-gap:0", "direction:rtl", "direction:ltr",
            "direction:rtl;column-gap:0", "direction:rtl;column-count:5;column-gap:0",
            static_cast<const char*>(nullptr),
            "width:8px;column-width:0;column-count:auto;column-gap:0", static_cast<const char*>(nullptr)}) {
        for (const auto doc : {live,full})
            CHECK(weva_element_set_attribute(doc,weva_document_query(doc,"#m"),"style",style) == WEVA_OK);
        CHECK(weva_document_set_css(full,css.data(),css.size()) == WEVA_OK);
        weva_document_set_viewport(full,1281,720); weva_document_set_viewport(full,1280,720);
        CHECK(weva_document_update(live,0) == WEVA_OK);
        CHECK(weva_document_update(full,0) == WEVA_OK);
        const auto a=capture(live), b=capture(full);
        if (a != b) std::printf("multicol sizing %s: %s\n",style ? style : "restore",a.diff(b).c_str());
        CHECK(a == b);
    }
    for (const char* style : {"column-span:all", "column-span:all;height:40px;margin:10px 0",
            "column-span:none", "column-span:all;height:0;margin:-5px 0", "display:none",
            "column-span:all;direction:rtl", static_cast<const char*>(nullptr)}) {
        for (const auto doc : {live, full})
            CHECK(weva_element_set_attribute(doc, weva_document_query(doc,"#heading"), "style", style) == WEVA_OK);
        CHECK(weva_document_set_css(full, css.data(), css.size()) == WEVA_OK);
        weva_document_set_viewport(full,1281,720); weva_document_set_viewport(full,1280,720);
        CHECK(weva_document_update(live,0) == WEVA_OK);
        CHECK(weva_document_update(full,0) == WEVA_OK);
        CHECK(capture(live) == capture(full));
    }
    weva_document_destroy(live); weva_document_destroy(full);
}

void test_abi_incremental_block_margins() {
    const std::string html = "<div id=p><div id=c></div></div><div id=after>After panel</div>";
    struct Step { const char* target; const char* style; };
    const Step steps[] = {{"#c","margin-left:0;margin-right:auto"},
        {"#c","margin-left:auto;margin-right:auto"},{"#p","direction:rtl"},
        {"#c","margin-left:auto;margin-right:11px"},{"#c","width:400px"},
        {"#p","width:80px;direction:rtl"},
        {"#c","width:auto;max-width:50px;margin-left:auto;margin-right:auto"},
        {"#p","direction:ltr"},{"#c",nullptr},{"#p",nullptr}};
    for (const char* context : {"", "#c{display:flow-root}", "#p{columns:2;column-gap:0}"}) {
        const std::string css = std::string("#p{width:300px;background:#123}#c{width:100px;"
            "height:20px;break-inside:avoid;margin-left:auto;margin-right:11px;background:#456}"
            "#after{background:#789}") + context;
        auto cfg=config();cfg.viewport_width=1280;cfg.viewport_height=720;
        const auto live=weva_document_create(&cfg), full=weva_document_create(&cfg);
        for (const auto doc : {live,full}) {
            CHECK(weva_document_load_html(doc,html.data(),html.size()) == WEVA_OK);
            CHECK(weva_document_set_css(doc,css.data(),css.size()) == WEVA_OK);
            CHECK(weva_document_update(doc,0) == WEVA_OK);
        }
        for (const auto& step : steps) {
            for (const auto doc : {live,full})
                CHECK(weva_element_set_attribute(doc,weva_document_query(doc,step.target),"style",step.style) == WEVA_OK);
            CHECK(weva_document_set_css(full,css.data(),css.size()) == WEVA_OK);
            weva_document_set_viewport(full,1281,720);weva_document_set_viewport(full,1280,720);
            CHECK(weva_document_update(live,0) == WEVA_OK);CHECK(weva_document_update(full,0) == WEVA_OK);
            const auto a=capture(live),b=capture(full);
            if (a != b) std::printf("block margin %s %s: %s\n",context,step.style ? step.style : "restore",a.diff(b).c_str());
            CHECK(a == b);
        }
        weva_document_destroy(live);weva_document_destroy(full);
    }
}

void test_abi_deferred_paint_geometry() {
    CHECK(weva_document_update_geometry(nullptr) == WEVA_ERR_INVALID_ARGUMENT);
    for (bool publish : {false, true}) {
        auto c = config();
        auto d = weva_document_create(&c);
        weva_document_add_css(d,kCss,std::strlen(kCss));
        weva_document_load_html(d,kHtml,std::strlen(kHtml));
        const auto serial = weva_document_draw_serial(d);
        CHECK(weva_document_update_geometry(d) == WEVA_OK);
        CHECK(weva_document_draw_serial(d) == serial);
        CHECK(capture(d).draws.empty());
        double x,y,w,h;
        CHECK(weva_element_bounds(d,weva_document_query(d,"#a"),&x,&y,&w,&h) == WEVA_OK);
        CHECK(w > 0 && h > 0);
        CHECK(weva_document_update_geometry(d) == WEVA_OK);
        if (publish) {
            CHECK(weva_document_update(d,0) == WEVA_OK);
            CHECK(capture(d) == build(kHtml,kCss,[](weva_document_t) {}));
            CHECK(weva_document_draw_serial(d) == serial + 1);
        }
        // Also exercises destruction with geometry but no published frame.
        weva_document_destroy(d);
    }
    for (const auto& change : std::vector<std::pair<const char*, const char*>>{
        {"background", "#f80"}, {"color", "#9cf"}, {"opacity", ".4"},
        {"transform", "translate(9px, 7px)"}, {"border-radius", "15px"},
        {"box-shadow", "0 2px 5px #000"}, {"padding", "15px"},
        {"font-size", "19px"}, {"display", "none"}, {"position", "relative"}}) {
        auto c = config();
        auto d = weva_document_create(&c);
        weva_document_add_css(d,kCss,std::strlen(kCss));
        weva_document_load_html(d,kHtml,std::strlen(kHtml));
        weva_document_update(d,0);
        const Frame old = capture(d);
        const auto serial = weva_document_draw_serial(d);
        const auto a = weva_document_query(d,"#a");
        weva_element_set_style(d,a,"width","177px");
        CHECK(weva_document_update_geometry(d) == WEVA_OK);
        CHECK(weva_document_draw_serial(d) == serial);
        CHECK(capture(d) == old);
        double x,y,w,h;
        CHECK(weva_element_bounds(d,a,&x,&y,&w,&h) == WEVA_OK);
        CHECK(w == 197);
        weva_element_set_style(d,a,change.first,change.second);
        CHECK(weva_document_update_geometry(d) == WEVA_OK);
        CHECK(weva_document_update_geometry(d) == WEVA_OK);
        CHECK(weva_document_draw_serial(d) == serial);
        CHECK(capture(d) == old);
        weva_document_update(d,0);
        CHECK(weva_document_draw_serial(d) == serial + 1);
        const Frame expected = build(kHtml,kCss,[&](weva_document_t fresh) {
            auto target = weva_document_query(fresh,"#a");
            weva_element_set_style(fresh,target,"width","177px");
            weva_element_set_style(fresh,target,change.first,change.second);
        });
        const Frame actual = capture(d);
        if (!(actual == expected)) std::fprintf(stderr,"deferred %s: %s\n",change.first,actual.diff(expected).c_str());
        CHECK(actual == expected);
        weva_document_update(d,0);
        CHECK(weva_document_draw_serial(d) == serial + 1);
        weva_document_destroy(d);
    }
}

void test_abi_incremental_collapsed_table_borders() {
    const std::string html = "<div id=container><table id=t><tbody id=g>"
        "<tr id=r1><td id=a><div id=overlay></div></td><td id=b><div></div></td></tr>"
        "<tr id=r2><td id=c><div></div></td><td id=d><div></div></td></tr>"
        "</tbody></table></div><div id=after>Following panel</div>";
    const std::string css = "#container{width:300px}#t{width:200px;border-collapse:collapse;table-layout:fixed}"
        "td{padding:0;border:4px solid red}td>div{height:20px}#after{height:20px;background:blue}"
        "#overlay{position:relative;width:160px;background:blue}";
    struct Step { const char* target; const char* attribute; const char* value; };
    const Step steps[] = {
        {"#t","style","position:relative"},
        {"#a","style","position:relative;background:cyan"},
        {"#overlay","style","z-index:-1"},
        {"#after","style","background:green"},
        {"#a","style","position:relative;z-index:0;background:cyan"},
        {"#after","style","background:purple"},
        {"#a","style","position:relative;z-index:-1;background:cyan"},
        {"#overlay","style","z-index:3"},
        {"#after","style","background:green"},
        {"#a","style","position:relative;background:cyan"},
        {"#overlay","style","z-index:-1"},
        {"#a","style",nullptr},{"#overlay","style",nullptr},{"#t","style",nullptr},

        {"#after","style","background:green"},{"#after","style",nullptr},
        {"#overlay","style","position:static"},{"#overlay","style",nullptr},
        {"#a","style","position:relative;background:cyan"},
        {"#overlay","style","position:static"},{"#after","style","background:green"},
        {"#overlay","style","position:absolute;left:10px;top:4px"},
        {"#container","style","transform:translate(8px,4px);overflow:hidden;width:120px"},
        {"#overlay","style","background:purple;z-index:3"},
        {"#container","style",nullptr},{"#overlay","style",nullptr},{"#a","style",nullptr},
        {"#overlay","style","position:absolute;left:80px;top:0"},
        {"#container","style","overflow:hidden;width:100px"},
        {"#after","style","background:purple"},
        {"#container","style","overflow:hidden;width:100px;position:relative"},
        {"#after","style","background:green"},
        {"#container","style","overflow:hidden;width:100px;position:relative;border-radius:20px"},
        {"#overlay","style","position:fixed;left:80px;top:0"},
        {"#container","style","overflow:hidden;width:100px;transform:translate(0,0);border-radius:20px"},
        {"#after","style","background:purple"},
        {"#container","style","overflow:hidden;width:100px"},
        {"#after","style","background:green"},
        {"#container","style",nullptr},{"#overlay","style",nullptr},
        {"#a","style","border-color:blue"},{"#a","style","border-color:rgba(0,0,255,.5)"},
        {"#b","style","border-left:8px solid green"},{"#b","style","border-left:2px solid green"},
        {"#a","style","border-right:hidden"},{"#a","style",nullptr},{"#b","style",nullptr},
        {"#r1","style","border:10px solid purple"},{"#r1","style",nullptr},
        {"#g","style","border:6px solid orange"},{"#g","style",nullptr},
        {"#t","style","border:8px solid green;padding:20px"},{"#t","style",nullptr},
        {"#t","style","border-collapse:separate;border-spacing:3px"},{"#t","style",nullptr},
        {"#a","rowspan","2"},{"#a","rowspan",nullptr},
        {"#a","colspan","2"},{"#a","colspan",nullptr},
        {"#r2","style","display:none"},{"#r2","style",nullptr},
        {"#container","style","transform:translate(8px,4px);overflow:hidden;width:120px"},
        {"#container","style",nullptr},
        {"#t","style","direction:rtl"},{"#t","style",nullptr}
    };
    auto cfg = config();
    const auto live = weva_document_create(&cfg), full = weva_document_create(&cfg);
    for (const auto doc : {live,full}) {
        CHECK(weva_document_load_html(doc,html.data(),html.size()) == WEVA_OK);
        CHECK(weva_document_set_css(doc,css.data(),css.size()) == WEVA_OK);
        CHECK(weva_document_update(doc,0) == WEVA_OK);
    }
    int step_index = -1;
    const auto compare = [&]() {
        ++step_index;
        CHECK(weva_document_set_css(full,css.data(),css.size()) == WEVA_OK);
        weva_document_set_viewport(full,401,300);weva_document_set_viewport(full,400,300);
        CHECK(weva_document_update(live,0) == WEVA_OK);
        CHECK(weva_document_update(full,0) == WEVA_OK);
        const auto a=capture(live),b=capture(full);
        if (a != b) std::printf("collapsed table incremental step %d: %s\n",step_index,a.diff(b).c_str());
        CHECK(a == b);
    };
    for (const auto& step : steps) {
        for (const auto doc : {live,full})
            CHECK(weva_element_set_attribute(doc,weva_document_query(doc,step.target),step.attribute,step.value) == WEVA_OK);
        compare();
    }
    for (const char* position : {"position:absolute;left:80px;top:0", "position:fixed;left:80px;top:0"}) {
        for (const auto doc : {live,full})
            CHECK(weva_element_set_attribute(doc,weva_document_query(doc,"#overlay"),"style",position) == WEVA_OK);
        for (const char* owner : {"overflow:hidden;width:100px;border-radius:20px",
                "overflow:hidden;width:100px;position:relative;border-radius:20px",
                "overflow:hidden;width:100px;transform:translate(0,0);border-radius:20px"}) {
            for (const auto doc : {live,full}) {
                CHECK(weva_element_set_attribute(doc,weva_document_query(doc,"#container"),"style",owner) == WEVA_OK);
                CHECK(weva_document_update(doc,0) == WEVA_OK);
            }
            for (double offset : {20.0,0.0}) {
                for (const auto doc : {live,full})
                    CHECK(weva_element_set_scroll(doc,weva_document_query(doc,"#container"),offset,0) == WEVA_OK);
                compare();
            }
        }
    }
    for (const auto doc : {live,full}) CHECK(weva_element_remove(doc,weva_document_query(doc,"#r2")) == WEVA_OK);
    compare();
    IncrementalBindingData data;
    data.values = {{"Rows","1"},{"Cols","1"},{"Tracks","1"}};
    weva_binding_source source{&data,&IncrementalBindingData::read,&IncrementalBindingData::count};
    const std::string bound_html = "<table id=t><colgroup><col style='width:40px' span='{{ Tracks }}'></colgroup>"
        "<tbody><tr><td rowspan='{{ Rows }}' colspan='{{ Cols }}'><div></div></td><td><div></div></td></tr>"
        "<tr><td><div></div></td><td><div></div></td></tr></tbody></table>";
    for (const auto doc : {live,full}) {
        CHECK(weva_document_load_html(doc,bound_html.data(),bound_html.size()) == WEVA_OK);
        weva_document_set_binding_source(doc,&source);
        weva_document_refresh_bindings(doc);
        CHECK(weva_document_update(doc,0) == WEVA_OK);
    }
    for (const char* path : {"Rows","Cols","Tracks"}) for (const char* value : {"2","1"}) {
        data.values[path] = value;
        for (const auto doc : {live,full}) CHECK(weva_document_refresh_bindings(doc) > 0);
        compare();
    }
    weva_document_destroy(live);weva_document_destroy(full);
}

void test_abi_hidden_subtree_restyle() {
    // A binding that keeps a closed panel current writes styles and attributes
    // into a `display: none` subtree. Nothing there has a box before or after,
    // so the frame is unchanged -- and publishing a new one rebuilt, laid out
    // and repainted the whole page for nothing. The writes must still land:
    // opening the panel shows exactly what a document born that way shows.
    const char* html =
        "<div id=hud class=card>HUD</div>"
        "<div id=panel class=gone><div id=row><span id=hp>100</span><i id=bar></i></div></div>";
    const char* css =
        ".card { background: #234; padding: 8px }"
        ".gone { display: none }"
        "#bar { display: block; height: 4px; background: #c33 }";
    const auto mutate = [](weva_document_t d) {
        weva_element_set_style(d, weva_document_query(d, "#bar"), "width", "37px");
        weva_element_set_attribute(d, weva_document_query(d, "#row"), "style", "padding-left: 9px");
        weva_element_set_attribute(d, weva_document_query(d, "#hp"), "class", "low");
    };
    auto c = config();
    auto d = weva_document_create(&c);
    weva_document_add_css(d, css, std::strlen(css));
    weva_document_load_html(d, html, std::strlen(html));
    weva_document_update(d, 0);
    const Frame before = capture(d);
    const auto serial = weva_document_draw_serial(d);
    for (int i = 0; i < 3; ++i) {
        mutate(d);
        CHECK(weva_document_update(d, 0) == WEVA_OK);
        CHECK(weva_document_draw_serial(d) == serial);
    }
    CHECK(capture(d) == before);

    // Opening it is the panel's own display change, which rebuilds as usual.
    weva_element_set_attribute(d, weva_document_query(d, "#panel"), "class", "");
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(weva_document_draw_serial(d) == serial + 1);
    const Frame expected = build(html, css, [&](weva_document_t fresh) {
        mutate(fresh);
        weva_element_set_attribute(fresh, weva_document_query(fresh, "#panel"), "class", "");
    });
    const Frame opened = capture(d);
    if (opened != expected) std::printf("hidden subtree: %s\n", opened.diff(expected).c_str());
    CHECK(opened == expected);
    CHECK(opened != before);

    // A visible element's restyle still publishes.
    weva_element_set_style(d, weva_document_query(d, "#hud"), "padding", "12px");
    CHECK(weva_document_update(d, 0) == WEVA_OK);
    CHECK(weva_document_draw_serial(d) == serial + 2);
    weva_document_destroy(d);
}

void test_abi_incremental_positioned_subtree() {
    // A relatively positioned card holding an absolutely positioned badge is
    // the everyday shape of a HUD slot. Incremental layout used to treat any
    // positioned box as nonlocal, so an edit inside one rebuilt, re-laid and
    // repainted the whole page. The replacement now re-applies the relative
    // offsets and places absolute boxes whose containing block it holds; one
    // placed against a block outside it still takes the full path.
    const char* html =
        "<header id=top>unchanged header text</header>"
        "<div id=bar><div class=slot id=s1><i class=icon>A</i><span class=key>1</span></div>"
        "<div class=slot id=s2><i class=icon>B</i><span class=key>2</span></div>"
        "<div class=slot id=s3 style='top:2px'><i class=icon id=leaf>C</i>"
        "<span class=key>3</span><b class=nested><em class=deep>x</em></b></div></div>"
        "<div id=outer><div id=inner><span id=escaped>out</span></div></div>"
        // The rest of the page. A replacement may cover at most half of it,
        // and a slot's content edit is promoted to the whole bar.
        "<ul id=log><li>one</li><li>two</li><li>three</li><li>four</li><li>five</li>"
        "<li>six</li><li>seven</li><li>eight</li><li>nine</li><li>ten</li><li>eleven</li>"
        "<li>twelve</li><li>thirteen</li><li>fourteen</li><li>fifteen</li><li>sixteen</li></ul>";
    const char* css =
        "body{margin:0;font-size:14px}#top{height:30px;background:#123}li{padding:1px}"
        "#bar{display:flex;gap:6px;padding:4px;background:#222}"
        ".slot{position:relative;left:1px;width:48px;height:48px;display:grid;place-items:center;"
        "      background:#345;border:1px solid #567}"
        ".key{position:absolute;top:2px;left:3px;font-size:9px;color:#ccc}"
        ".nested{position:relative;top:-3px}.deep{position:absolute;right:0;bottom:0}"
        "#outer{position:relative;height:40px}#inner{padding:3px}"
        "#escaped{position:absolute;right:4px;top:0}";
    struct Case { const char* target; const char* style; bool retains_header; };
    const Case cases[] = {
        {"#leaf", "padding-left:5px", true},        // inside a slot: local
        {"#leaf", "padding-left:7px", true},
        {".deep", "right:6px", true},               // absolute, placed inside the slot
        {"#s3 .nested", "top:-5px", true},          // nested relative offset changes
        {"#s3", "top:5px", true},                   // the replaced root's own offset
        {"#inner", "padding:6px", false},           // escaped box: full path
    };
    auto c = config(400, 300);
    auto live = weva_document_create(&c);
    weva_document_add_css(live, css, std::strlen(css));
    weva_document_load_html(live, html, std::strlen(html));
    weva_document_update(live, 0);
    std::vector<std::pair<const char*, const char*>> applied;
    for (const Case& k : cases) {
        size_t n = 0;
        const uint64_t* before = weva_document_draw_versions(live, &n);
        const uint64_t header_version = n ? before[1] : 0;   // [0] is the page, [1] the header
        CHECK(weva_element_set_attribute(live, weva_document_query(live, k.target), "style", k.style) ==
              WEVA_OK);
        applied.emplace_back(k.target, k.style);
        CHECK(weva_document_update(live, 0) == WEVA_OK);
        const Frame expected = build(html, css, [&](weva_document_t fresh) {
            for (const auto& a : applied)
                weva_element_set_attribute(fresh, weva_document_query(fresh, a.first), "style", a.second);
        });
        // build() uses the default 400x300 viewport too.
        const Frame actual = capture(live);
        if (actual != expected)
            std::printf("positioned subtree %s {%s}: %s\n", k.target, k.style, actual.diff(expected).c_str());
        CHECK(actual == expected);
        const uint64_t* after = weva_document_draw_versions(live, &n);
        if (k.retains_header) CHECK(n > 1 && after[1] == header_version);
        else CHECK(n > 1 && after[1] != header_version);
    }
    weva_document_destroy(live);
}
