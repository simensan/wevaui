#include "check.h"
#include "weva/block_layout.h"
#include "weva/box_builder.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/font_metrics.h"
#include "weva/html.h"
#include "weva/paint.h"
#include "weva/software_renderer.h"
#include "weva/tessellate.h"
#include "weva/user_agent_stylesheet.h"
#include <cmath>
#include <map>
#include <memory>

using namespace weva;

namespace {

bool near(double a, double b, double eps = 1e-4) { return std::fabs(a - b) < eps; }

Vertex vtx(float x, float y, const LinearColor& c, float u = 0, float v = 0) {
    Vertex out;
    out.position = {x, y};
    out.color = c;
    out.tex_coord = {u, v};
    return out;
}

// Draws one mesh and returns the renderer, so a test can probe pixels.
void draw(SoftwareRenderer* r, const std::vector<Vertex>& v, const std::vector<uint32_t>& i,
          TextureHandle tex = {}) {
    const GeometryHandle g = r->compile_geometry(v, i);
    r->render_geometry(g, {0, 0}, tex);
    r->release_geometry(g);
}

int covered_pixels(const SoftwareRenderer& r) {
    int n = 0;
    for (int y = 0; y < r.height(); ++y) {
        for (int x = 0; x < r.width(); ++x) {
            if (r.pixel(x, y).a > 0) ++n;
        }
    }
    return n;
}

} // namespace

void test_software_raster_coverage() {
    SoftwareRenderer r(20, 20);
    r.clear(LinearColor::transparent());
    Mesh m;
    tessellate_rect(Rect(2, 3, 5, 4), LinearColor::white(), &m);
    draw(&r, m.vertices, m.indices);

    // Pixels are sampled at their CENTRE, so a rect from 2 to 7 covers columns
    // 2..6 — exactly five, not six.
    CHECK(covered_pixels(r) == 20);
    CHECK(r.pixel(2, 3).a == 1.0f);
    CHECK(r.pixel(6, 6).a == 1.0f);
    CHECK(r.pixel(1, 3).a == 0.0f);
    CHECK(r.pixel(7, 3).a == 0.0f);
    CHECK(r.pixel(2, 7).a == 0.0f);
    // Reads outside the framebuffer are transparent, not a fault.
    CHECK(r.pixel(-1, 0).a == 0.0f && r.pixel(0, 999).a == 0.0f);
}

void test_software_fill_rule() {
    // The seam between two triangles sharing an edge must be drawn EXACTLY
    // once. Drawn twice it is visible wherever the colour is translucent — and
    // the core's meshes share edges everywhere, since a quad, a fan and a ring
    // are all built from them.
    SoftwareRenderer r(16, 16);
    r.clear(LinearColor::transparent());
    const LinearColor half(1, 1, 1, 0.5f);
    Mesh m;
    tessellate_rect(Rect(1, 1, 10, 10), half, &m);
    draw(&r, m.vertices, m.indices);

    // Every covered pixel has the single-draw alpha; a doubled seam would read
    // 0.75 along the diagonal.
    for (int y = 1; y < 11; ++y) {
        for (int x = 1; x < 11; ++x) CHECK(near(r.pixel(x, y).a, 0.5));
    }
}

void test_software_gradient() {
    // Per-vertex colour interpolates across the triangle. This is the thing the
    // C# software rasterizer could not do — it drew gradients as flat fills —
    // and it comes for free once the interface is triangles.
    SoftwareRenderer r(11, 4);
    r.clear(LinearColor::transparent());
    const LinearColor left(1, 0, 0, 1), right(0, 0, 1, 1);
    std::vector<Vertex> v = {vtx(0, 0, left), vtx(10, 0, right), vtx(10, 4, right),
                             vtx(0, 4, left)};
    draw(&r, v, {0, 1, 2, 0, 2, 3});

    CHECK(r.pixel(0, 1).r > 0.9f && r.pixel(0, 1).b < 0.1f);
    CHECK(r.pixel(9, 1).b > 0.8f && r.pixel(9, 1).r < 0.2f);
    // Monotonic across the span, which is what makes it a gradient rather than
    // two flat halves.
    for (int x = 1; x < 9; ++x) {
        CHECK(r.pixel(x, 1).r <= r.pixel(x - 1, 1).r + 1e-6f);
        CHECK(r.pixel(x, 1).b >= r.pixel(x - 1, 1).b - 1e-6f);
    }
}

void test_software_blending_and_scissor() {
    {
        // Source-over: a half-alpha white over opaque black lands halfway.
        SoftwareRenderer r(4, 4);
        r.clear(LinearColor::black());
        Mesh m;
        tessellate_rect(Rect(0, 0, 4, 4), LinearColor(1, 1, 1, 0.5f), &m);
        draw(&r, m.vertices, m.indices);
        CHECK(near(r.pixel(1, 1).r, 0.5));
        CHECK(near(r.pixel(1, 1).a, 1.0));
    }
    {
        // The scissor clips, and clearing it restores full drawing.
        SoftwareRenderer r(10, 10);
        r.clear(LinearColor::transparent());
        const Recti clip{2, 2, 3, 3};
        r.set_scissor(&clip);
        Mesh m;
        tessellate_rect(Rect(0, 0, 10, 10), LinearColor::white(), &m);
        draw(&r, m.vertices, m.indices);
        CHECK(covered_pixels(r) == 9);
        CHECK(r.pixel(2, 2).a == 1.0f && r.pixel(4, 4).a == 1.0f);
        CHECK(r.pixel(1, 2).a == 0.0f && r.pixel(5, 2).a == 0.0f);

        r.set_scissor(nullptr);
        draw(&r, m.vertices, m.indices);
        CHECK(covered_pixels(r) == 100);
    }
}

void test_software_texture_and_robustness() {
    {
        // A texture MODULATES the vertex colour, which is what lets one path
        // serve both a glyph mask and a tinted image.
        SoftwareRenderer r(4, 4);
        r.clear(LinearColor::transparent());
        // A 1x1 half-alpha white texel.
        std::vector<uint8_t> texel = {255, 255, 255, 128};
        const TextureHandle t = r.generate_texture(texel, {1, 1});
        CHECK(static_cast<bool>(t));
        std::vector<Vertex> v = {vtx(0, 0, LinearColor(1, 0, 0, 1)),
                                 vtx(4, 0, LinearColor(1, 0, 0, 1)),
                                 vtx(4, 4, LinearColor(1, 0, 0, 1)),
                                 vtx(0, 4, LinearColor(1, 0, 0, 1))};
        draw(&r, v, {0, 1, 2, 0, 2, 3}, t);
        CHECK(near(r.pixel(1, 1).a, 128.0 / 255.0));
        CHECK(r.pixel(1, 1).r > 0.4f);
        r.release_texture(t);
    }
    {
        // A malformed texture is refused rather than half-accepted.
        SoftwareRenderer r(4, 4);
        CHECK(!static_cast<bool>(r.generate_texture({1, 2, 3}, {4, 4})));
        CHECK(!static_cast<bool>(r.generate_texture({}, {0, 0})));
        // An unsupported image path degrades to the null handle, so the draw
        // falls back to vertex colours instead of vanishing.
        Vec2i size{9, 9};
        CHECK(!static_cast<bool>(r.load_texture("nope.png", &size)));
        CHECK(size.x == 0 && size.y == 0);
    }
    {
        // A backend must not fault on malformed geometry: an out-of-range index
        // skips its triangle, and an unknown handle draws nothing.
        SoftwareRenderer r(8, 8);
        r.clear(LinearColor::transparent());
        std::vector<Vertex> v = {vtx(0, 0, LinearColor::white()), vtx(8, 0, LinearColor::white()),
                                 vtx(8, 8, LinearColor::white())};
        draw(&r, v, {0, 1, 99});
        CHECK(covered_pixels(r) == 0);
        r.render_geometry(GeometryHandle{12345}, {0, 0}, {});
        CHECK(covered_pixels(r) == 0);
        // A degenerate (zero-area) triangle draws nothing rather than dividing
        // by zero.
        draw(&r, {vtx(0, 0, LinearColor::white()), vtx(4, 0, LinearColor::white()),
                  vtx(8, 0, LinearColor::white())}, {0, 1, 2});
        CHECK(covered_pixels(r) == 0);
    }
}

void test_software_translation_and_output() {
    {
        // render_geometry's translation lets identical geometry be reused at
        // many positions without recompiling it.
        SoftwareRenderer r(20, 20);
        r.clear(LinearColor::transparent());
        Mesh m;
        tessellate_rect(Rect(0, 0, 4, 4), LinearColor::white(), &m);
        const GeometryHandle g = r.compile_geometry(m.vertices, m.indices);
        r.render_geometry(g, {0, 0}, {});
        r.render_geometry(g, {10, 10}, {});
        r.release_geometry(g);
        CHECK(r.pixel(1, 1).a == 1.0f);
        CHECK(r.pixel(11, 11).a == 1.0f);
        CHECK(r.pixel(5, 5).a == 0.0f);
        CHECK(covered_pixels(r) == 32);
    }
    {
        // The framebuffer is linear; the output converts to 8-bit sRGB. A
        // mid-grey linear value must NOT come out as 128 — that is the whole
        // reason for keeping the buffer linear.
        SoftwareRenderer r(1, 1);
        r.clear(LinearColor(0.5f, 0.5f, 0.5f, 1.0f));
        const std::vector<uint8_t> out = r.to_srgb_rgba();
        CHECK(out.size() == 4);
        CHECK(out[0] > 180 && out[0] < 195);
        CHECK(out[3] == 255);
    }
}

void test_end_to_end_render() {
    // Cascade, layout and paint a document into pixels — the first time the
    // whole pipeline runs end to end.
    SymbolTable symbols;
    std::vector<std::unique_ptr<Stylesheet>> sheets;
    struct Styles : StyleProvider {
        CascadeEngine engine;
        NullStateProvider state;
        std::vector<std::unique_ptr<ComputedStyle>> owned;
        std::map<const Element*, ComputedStyle*> by_element;
        void walk(const Element& e, const ComputedStyle* p) {
            auto cs = std::make_unique<ComputedStyle>();
            engine.compute(e, state, p, cs.get());
            ComputedStyle* raw = cs.get();
            owned.push_back(std::move(cs));
            by_element[&e] = raw;
            for (const Ref<Node>& c : e.children()) {
                if (c->node_type() == NodeType::Element) {
                    walk(static_cast<const Element&>(*c), raw);
                }
            }
        }
        const ComputedStyle* style_of(const Element& e) override {
            auto it = by_element.find(&e);
            return it == by_element.end() ? nullptr : it->second;
        }
    } styles;

    auto ua = std::make_unique<Stylesheet>();
    CssParseError pe;
    parse_stylesheet(user_agent_stylesheet_source(), false, ua.get(), &pe);
    styles.engine.add_stylesheet(ua.get(), DeclarationOrigin::UserAgent);
    sheets.push_back(std::move(ua));

    auto author = std::make_unique<Stylesheet>();
    CHECK(parse_stylesheet("#a { display: block; width: 40px; height: 20px;"
                           "     background-color: #ff0000; margin-left: 10px;"
                           "     margin-top: 5px }",
                           false, author.get(), &pe));
    styles.engine.add_stylesheet(author.get(), DeclarationOrigin::Author);
    sheets.push_back(std::move(author));

    HtmlParseError he;
    ParseOptions o;
    o.strict = false;
    Ref<Document> doc = parse_html("<body><div id=a></div></body>", &symbols, o, &he);
    CHECK(static_cast<bool>(doc));
    for (const Ref<Node>& c : doc->children()) {
        if (c->node_type() == NodeType::Element) {
            styles.walk(static_cast<const Element&>(*c), nullptr);
        }
    }

    BoxTree tree;
    BoxBuilder builder(&tree, &styles);
    const BoxId root = builder.build_document(*doc);
    LayoutContext ctx;
    MonoFontMetrics metrics;
    BlockLayout bl(&tree, ctx, &metrics);
    bl.layout_root(root, 100, 60);

    SoftwareRenderer r(100, 60);
    r.clear(LinearColor::transparent());
    paint_tree(tree, root, ctx, &r);

    // The div lands at its margin offset and paints its background there.
    CHECK(r.pixel(10, 5).a == 1.0f);
    CHECK(r.pixel(49, 24).a == 1.0f);
    CHECK(r.pixel(9, 5).a == 0.0f);
    CHECK(r.pixel(50, 5).a == 0.0f);
    CHECK(r.pixel(10, 25).a == 0.0f);
    // Red in linear space, and 40x20 pixels of it.
    CHECK(r.pixel(20, 10).r > 0.9f && r.pixel(20, 10).g == 0.0f);
    CHECK(covered_pixels(r) == 800);
}
