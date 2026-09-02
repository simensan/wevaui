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

#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

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
            else if (a.vertices != b.vertices) what = "vertex data";
            else if (a.indices != b.indices) what = "indices";
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

}   // namespace

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
