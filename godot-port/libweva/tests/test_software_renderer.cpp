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
#include <array>
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

    // Read the ends from the OUTPUT, where the arithmetic is checkable: the
    // gradient interpolates in sRGB, the pixel centre at x=0 is 5% along the
    // 10px span, so red is 95% of the way and lands on 0.95*255 = 242.
    //
    // The same probe used to read pixel().r > 0.9 in linear. That was measuring
    // the interpolation SPACE, not the gradient: a 5%-inset sample is 0.887 in
    // linear once the ramp runs in sRGB, so the check went red for a renderer
    // that had just been corrected. Chrome ramps `linear-gradient(red, blue)`
    // in sRGB too.
    const std::vector<uint8_t> out = r.to_srgb_rgba();
    const auto at = [&](int x, int y) {
        const size_t i = (static_cast<size_t>(y) * r.width() + x) * 4;
        return std::array<int, 3>{out[i], out[i + 1], out[i + 2]};
    };
    CHECK(at(0, 1)[0] == 242 && at(0, 1)[2] == 13);
    CHECK(at(9, 1)[2] == 242 && at(9, 1)[0] == 13);
    // Monotonic across the span, which is what makes it a gradient rather than
    // two flat halves.
    for (int x = 1; x < 9; ++x) {
        CHECK(r.pixel(x, 1).r <= r.pixel(x - 1, 1).r + 1e-6f);
        CHECK(r.pixel(x, 1).b >= r.pixel(x - 1, 1).b - 1e-6f);
    }
}

void test_software_blending_and_scissor() {
    {
        // Source-over: a half-alpha white over opaque black lands halfway —
        // halfway in the space the compositing happens in, which is sRGB. So
        // the byte is 128, exactly what a browser paints for
        // `rgba(255,255,255,0.5)` over black.
        //
        // Compositing in linear instead gives 0.5 linear = 188 out of 255, a
        // visibly lighter grey and the reason this renderer used to disagree
        // with both Chrome and the Godot host on every translucent overlay.
        SoftwareRenderer r(4, 4);
        r.clear(LinearColor::black());
        Mesh m;
        tessellate_rect(Rect(0, 0, 4, 4), LinearColor(1, 1, 1, 0.5f), &m);
        draw(&r, m.vertices, m.indices);
        CHECK(r.to_srgb_rgba()[(1 * 4 + 1) * 4] == 128);
        // pixel() still speaks linear, and 128/255 sRGB decodes to 0.2140.
        CHECK(near(r.pixel(1, 1).r, 0.2140));
        CHECK(near(r.pixel(1, 1).a, 1.0));
    }
    {
        // What comes OUT is straight alpha, whatever the buffer keeps inside.
        //
        // Source-over produces a premultiplied destination — the blend writes
        // src.rgb * src.a — so handing that out unchanged makes any caller that
        // composites it over a page multiply by alpha a second time. The result
        // is a translucent pixel darkened towards nothing, and it cancels
        // wherever alpha has reached 1, which is why it hid on every opaque
        // page and showed up only in quests' glass margin: 216 against Chrome's
        // 239, from a single draw.
        //
        // Half-alpha RED on an empty page is the whole story in one pixel.
        SoftwareRenderer r(4, 4);
        r.clear(LinearColor::transparent());
        Mesh m;
        tessellate_rect(Rect(0, 0, 4, 4), LinearColor(1, 0, 0, 0.5f), &m);
        draw(&r, m.vertices, m.indices);

        // Full red at half coverage, NOT half-strength red at half coverage.
        const std::vector<uint8_t> out = r.to_srgb_rgba();
        const size_t i = (1 * 4 + 1) * 4;
        CHECK(out[i] == 255 && out[i + 1] == 0 && out[i + 2] == 0);
        CHECK(out[i + 3] == 128);
        CHECK(near(r.pixel(1, 1).r, 1.0) && near(r.pixel(1, 1).a, 0.5));

        // And composited over a white page the way weva_render does it, that
        // is the pink a browser paints for rgba(255,0,0,0.5) — one unit off,
        // because the alpha has been through a byte first: 0.5 becomes
        // 128/255 = 0.50196, so the exact 127.5 that Chrome rounds up to 128
        // arrives here as 127.0 and rounds down. That is the PPM round trip,
        // not the blend, and it is inside the comparison's tolerance.
        const auto over_white = [&](int c) {
            const double a = out[i + 3] / 255.0;
            return static_cast<int>(out[i + c] * a + 255.0 * (1.0 - a) + 0.5);
        };
        CHECK(over_white(0) == 255 && over_white(1) == 127 && over_white(2) == 127);
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
        // A white texel modulates the ALPHA and nothing else: the colour is
        // still pure red, with nothing in green or blue. (The old check here
        // read r > 0.4, which was really asserting that the blend ran in
        // linear — the same red over the same transparent ground is 0.214 once
        // it runs in sRGB, as a browser does it.)
        CHECK(r.pixel(1, 1).r > 0.0f);
        CHECK(r.pixel(1, 1).g == 0.0f && r.pixel(1, 1).b == 0.0f);
        r.release_texture(t);
    }
    {
        // And a COLOURED texel modulates the colour — in the same space the
        // result is composited in, so a mid-grey texel halves the BYTE rather
        // than halving the light. Modulating in linear and blending in sRGB
        // would put this at 188.
        SoftwareRenderer r(4, 4);
        r.clear(LinearColor::black());
        std::vector<uint8_t> texel = {128, 128, 128, 255};
        const TextureHandle t = r.generate_texture(texel, {1, 1});
        CHECK(static_cast<bool>(t));
        std::vector<Vertex> v = {vtx(0, 0, LinearColor::white()), vtx(4, 0, LinearColor::white()),
                                 vtx(4, 4, LinearColor::white()), vtx(0, 4, LinearColor::white())};
        draw(&r, v, {0, 1, 2, 0, 2, 3}, t);
        CHECK(r.to_srgb_rgba()[(1 * 4 + 1) * 4] == 128);
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
