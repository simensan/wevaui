// Filter Effects L2 §2 — `backdrop-filter`.
//
// The only effect the core cannot decompose into triangles, because it reads
// the destination. So the tests here run against a real SoftwareRenderer and
// look at pixels: what the backdrop became, where the effect stopped, and what
// a backend that ignores the operation is left with.
#include "check.h"
#include "weva/block_layout.h"
#include "weva/box_builder.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/font_metrics.h"
#include "weva/html.h"
#include "weva/paint.h"
#include "weva/positioning.h"
#include "weva/software_renderer.h"
#include "weva/user_agent_stylesheet.h"

#include <cmath>
#include <map>
#include <memory>
#include <string>
#include <vector>

using namespace weva;

namespace {

struct Styles : StyleProvider {
    CascadeEngine engine;
    NullStateProvider state;
    std::vector<std::unique_ptr<ComputedStyle>> owned;
    std::map<const Element*, ComputedStyle*> by_element;

    void compute_tree(const Element& e, const ComputedStyle* parent) {
        auto cs = std::make_unique<ComputedStyle>();
        engine.compute(e, state, parent, cs.get());
        ComputedStyle* raw = cs.get();
        owned.push_back(std::move(cs));
        by_element[&e] = raw;
        for (const Ref<Node>& c : e.children()) {
            if (c->node_type() == NodeType::Element) compute_tree(static_cast<const Element&>(*c), raw);
        }
    }
    const ComputedStyle* style_of(const Element& e) override {
        auto it = by_element.find(&e);
        return it == by_element.end() ? nullptr : it->second;
    }
};

struct Fixture {
    SymbolTable symbols;
    Ref<Document> doc;
    std::vector<std::unique_ptr<Stylesheet>> sheets;
    Styles styles;
    BoxTree tree;
    LayoutContext ctx;
    MonoFontMetrics metrics;
    BoxId root = kNoBox;

    Fixture() {
        auto ua = std::make_unique<Stylesheet>();
        CssParseError e;
        parse_stylesheet(user_agent_stylesheet_source(), false, ua.get(), &e);
        styles.engine.add_stylesheet(ua.get(), DeclarationOrigin::UserAgent);
        sheets.push_back(std::move(ua));
    }
    bool css(std::string_view c) {
        auto s = std::make_unique<Stylesheet>();
        CssParseError e;
        if (!parse_stylesheet(c, false, s.get(), &e)) return false;
        styles.engine.add_stylesheet(s.get(), DeclarationOrigin::Author);
        sheets.push_back(std::move(s));
        return true;
    }
    bool layout(std::string_view html, double vw, double vh) {
        ctx.viewport_width_px = vw;
        ctx.viewport_height_px = vh;
        HtmlParseError he;
        ParseOptions o;
        o.strict = false;
        doc = parse_html(html, &symbols, o, &he);
        if (!doc) return false;
        for (const Ref<Node>& c : doc->children()) {
            if (c->node_type() == NodeType::Element) {
                styles.compute_tree(static_cast<const Element&>(*c), nullptr);
            }
        }
        BoxBuilder builder(&tree, &styles);
        root = builder.build_document(*doc);
        if (root == kNoBox) return false;
        BlockLayout bl(&tree, ctx, &metrics);
        bl.layout_root(root, vw, vh);
        run_positioning(&tree, root, ctx, &bl);
        return true;
    }
};

// Paints a document into a real framebuffer, so the assertions can be about
// colour rather than about draw calls.
struct Rendered {
    Fixture f;
    SoftwareRenderer r;

    Rendered(std::string_view css, std::string_view html, int w = 200, int h = 120) : r(w, h) {
        CHECK(f.css(css));
        CHECK(f.layout(html, w, h));
        r.clear(LinearColor::transparent());
        paint_tree(f.tree, f.root, f.ctx, &r);
    }
    // The 8-bit sRGB the page would be seen as.
    void at(int x, int y, int* out) const {
        const LinearColor c = r.pixel(x, y);
        const auto b = [](float v) {
            if (v <= 0) return 0;
            if (v >= 1) return 255;
            const float s = v <= 0.0031308f ? v * 12.92f
                                            : 1.055f * std::pow(v, 1.0f / 2.4f) - 0.055f;
            return static_cast<int>(std::lround(s * 255.0f));
        };
        out[0] = b(c.r);
        out[1] = b(c.g);
        out[2] = b(c.b);
    }
};

// Records whether the operation reached the backend, and with what, so the
// core's half can be asserted without a rasterizer in the way.
struct BackdropSpy : RenderInterface {
    struct Call {
        std::vector<Vertex> vertices;
        BackdropEffect effect;
    };
    std::vector<Call> calls;
    int geometry_draws = 0;
    uint64_t next_id = 1;
    std::map<uint64_t, std::pair<std::vector<Vertex>, std::vector<uint32_t>>> pending;

    GeometryHandle compile_geometry(const std::vector<Vertex>& v,
                                    const std::vector<uint32_t>& i) override {
        GeometryHandle h{next_id++};
        pending[h.id] = {v, i};
        return h;
    }
    void render_geometry(GeometryHandle, Vec2, TextureHandle) override { ++geometry_draws; }
    void release_geometry(GeometryHandle g) override { pending.erase(g.id); }
    TextureHandle load_texture(std::string_view, Vec2i*) override { return {}; }
    TextureHandle generate_texture(const std::vector<uint8_t>&, Vec2i) override { return {}; }
    void release_texture(TextureHandle) override {}
    void set_scissor(const Recti*) override {}
    void filter_backdrop(const std::vector<Vertex>& v, const std::vector<uint32_t>&,
                         const BackdropEffect& e) override {
        calls.push_back({v, e});
    }
};

Rect bounds_of(const std::vector<Vertex>& v) {
    double x0 = 1e300, y0 = 1e300, x1 = -1e300, y1 = -1e300;
    for (const Vertex& p : v) {
        x0 = std::min<double>(x0, p.position.x);
        y0 = std::min<double>(y0, p.position.y);
        x1 = std::max<double>(x1, p.position.x);
        y1 = std::max<double>(y1, p.position.y);
    }
    return Rect(x0, y0, x1 - x0, y1 - y0);
}

const char* kPage = "html, body { margin: 0; height: 100% }";

} // namespace

// The colour half. A saturating backdrop-filter changes what is BEHIND the
// element, which nothing else in the engine can do: every other effect only
// changes what the element itself paints.
void test_backdrop_filter_colour() {
    // A mid grey page, and a panel over it that saturates. Grey has no
    // saturation to stretch, so it must come out grey -- this is the control,
    // and it fails loudly if the matrix is transposed or the luminance weights
    // are wrong.
    {
        Rendered p(std::string(kPage) +
                       "body { background: #808080 }"
                       "#g { position: absolute; left: 40px; top: 20px; width: 80px; height: 60px;"
                       "     backdrop-filter: saturate(3) }",
                   "<body><div id=g></div></body>");
        int in[3], out[3];
        p.at(80, 50, in);
        p.at(10, 50, out);
        CHECK(in[0] == in[1] && in[1] == in[2]);
        CHECK(std::abs(in[0] - out[0]) <= 1);
    }
    {
        // A coloured page: saturate(2) pushes it away from its own luminance.
        // L = 0.2126*192 + 0.7152*64 + 0.0722*128 = 95.9, and each channel
        // moves to L + 2*(c - L): red 288.2 clamps to 255, green 32.2, blue
        // 160.2.
        Rendered p(std::string(kPage) +
                       "body { background: rgb(192, 64, 128) }"
                       "#g { position: absolute; left: 40px; top: 20px; width: 80px; height: 60px;"
                       "     backdrop-filter: saturate(2) }",
                   "<body><div id=g></div></body>");
        int in[3], out[3];
        p.at(80, 50, in);
        p.at(10, 50, out);
        CHECK(out[0] == 192 && out[1] == 64 && out[2] == 128);
        CHECK(in[0] == 255);
        CHECK(std::abs(in[1] - 32) <= 1);
        CHECK(std::abs(in[2] - 160) <= 1);
    }
    {
        // brightness() halves it, and only inside.
        Rendered p(std::string(kPage) +
                       "body { background: #808080 }"
                       "#g { position: absolute; left: 40px; top: 20px; width: 80px; height: 60px;"
                       "     backdrop-filter: brightness(0.5) }",
                   "<body><div id=g></div></body>");
        int in[3], out[3];
        p.at(80, 50, in);
        p.at(10, 50, out);
        CHECK(out[0] == 128);
        CHECK(std::abs(in[0] - 64) <= 1);
    }
}

// Where the effect stops. A backdrop-filter is confined to the element's
// border box, corners included.
void test_backdrop_filter_extent() {
    Rendered p(std::string(kPage) +
                   "body { background: #808080 }"
                   "#g { position: absolute; left: 40px; top: 20px; width: 80px; height: 60px;"
                   "     border-radius: 20px; backdrop-filter: brightness(0.5) }",
               "<body><div id=g></div></body>");
    int c[3];
    // Inside.
    p.at(80, 50, c);
    CHECK(std::abs(c[0] - 64) <= 1);
    // Just outside each edge.
    p.at(38, 50, c);
    CHECK(c[0] == 128);
    p.at(122, 50, c);
    CHECK(c[0] == 128);
    p.at(80, 18, c);
    CHECK(c[0] == 128);
    p.at(80, 82, c);
    CHECK(c[0] == 128);
    // And the corner the radius cuts away: (42,22) is inside the border box but
    // outside a 20px round, so it must be untouched.
    p.at(42, 22, c);
    CHECK(c[0] == 128);
}

// The blur half. A hard edge in the backdrop becomes a ramp inside the
// filtered box and stays hard outside it.
void test_backdrop_filter_blur() {
    Rendered p(std::string(kPage) +
                   "body { background: #000 }"
                   "#half { position: absolute; left: 0; top: 0; width: 100px; height: 120px;"
                   "        background: #fff }"
                   "#g { position: absolute; left: 40px; top: 20px; width: 120px; height: 80px;"
                   "     backdrop-filter: blur(16px) }",
               "<body><div id=half></div><div id=g></div></body>", 200, 120);

    int a[3], b[3], c[3];
    // Outside the filtered box the edge is still a step: black beside white.
    p.at(98, 110, a);
    p.at(102, 110, b);
    CHECK(a[0] > 200 && b[0] < 55);

    // Inside it the same edge is a ramp, so the two sides have moved towards
    // each other and the midpoint sits between them.
    p.at(88, 60, a);
    p.at(100, 60, b);
    p.at(112, 60, c);
    CHECK(a[0] < 250);          // the white side has darkened
    CHECK(c[0] > 5);            // the black side has lightened
    CHECK(a[0] > b[0] && b[0] > c[0]);   // and it is monotonic across the edge

    // Far from the edge the blur has nothing to mix in, so a flat region keeps
    // its colour -- the check that catches a blur that darkens everything by
    // sampling outside the source.
    p.at(45, 60, a);
    CHECK(a[0] > 245);
}

// The core's half, asserted without a rasterizer: what reaches the backend,
// and what does not.
void test_backdrop_filter_reaches_the_backend() {
    {
        BackdropSpy spy;
        Fixture f;
        CHECK(f.css(std::string(kPage) +
                    "#g { position: absolute; left: 40px; top: 20px; width: 80px; height: 60px;"
                    "     backdrop-filter: blur(10px) brightness(2) }"));
        CHECK(f.layout("<body><div id=g></div></body>", 200, 120));
        paint_tree(f.tree, f.root, f.ctx, &spy);

        CHECK(spy.calls.size() == 1);
        // A CSS radius, not a sigma.
        CHECK(std::fabs(spy.calls[0].effect.blur_radius - 10.0) < 1e-6);
        CHECK(std::fabs(spy.calls[0].effect.color.m[0][0] - 2.0f) < 1e-6);
        CHECK(!spy.calls[0].effect.color.is_identity());
        // The shape is the border box, in absolute coordinates.
        const Rect r = bounds_of(spy.calls[0].vertices);
        CHECK(std::fabs(r.x - 40) < 0.5 && std::fabs(r.y - 20) < 0.5);
        CHECK(std::fabs(r.width - 80) < 0.5 && std::fabs(r.height - 60) < 0.5);
    }
    {
        // `none` is the initial value and must not cost a call. The box is
        // given a background so the draw count means something: without one an
        // undecorated div over a transparent page paints nothing at all, and
        // the check would pass for the wrong reason.
        BackdropSpy spy;
        Fixture f;
        CHECK(f.css(std::string(kPage) +
                    "#g { position: absolute; left: 40px; top: 20px; width: 80px; height: 60px;"
                    "     background: #123456 }"));
        CHECK(f.layout("<body><div id=g></div></body>", 200, 120));
        paint_tree(f.tree, f.root, f.ctx, &spy);
        CHECK(spy.calls.empty());
        CHECK(spy.geometry_draws > 0);
    }
    {
        // Neither does a filter list that resolves to no change. `saturate(1)`
        // is a valid list and an identity matrix, and asking a host to read
        // back its framebuffer to multiply by one is the expensive kind of
        // no-op.
        BackdropSpy spy;
        Fixture f;
        CHECK(f.css(std::string(kPage) +
                    "#g { position: absolute; left: 40px; top: 20px; width: 80px; height: 60px;"
                    "     backdrop-filter: saturate(1) }"));
        CHECK(f.layout("<body><div id=g></div></body>", 200, 120));
        paint_tree(f.tree, f.root, f.ctx, &spy);
        CHECK(spy.calls.empty());
    }
    {
        // A backend that ignores the operation still gets every ordinary draw:
        // the element renders without its material rather than not at all.
        // This is the contract the Godot host relies on until it implements it.
        BackdropSpy spy;
        Fixture f;
        CHECK(f.css(std::string(kPage) +
                    "#g { position: absolute; left: 40px; top: 20px; width: 80px; height: 60px;"
                    "     background: #123456; backdrop-filter: blur(10px) }"));
        CHECK(f.layout("<body><div id=g></div></body>", 200, 120));
        paint_tree(f.tree, f.root, f.ctx, &spy);
        CHECK(spy.calls.size() == 1);
        CHECK(spy.geometry_draws > 0);
    }
}

// The element paints ON the filtered backdrop, which is the whole point of a
// glass panel: a translucent background over a blurred, saturated copy of what
// was behind it.
void test_backdrop_filter_is_under_the_background() {
    Rendered p(std::string(kPage) +
                   "body { background: #808080 }"
                   "#g { position: absolute; left: 40px; top: 20px; width: 80px; height: 60px;"
                   "     background: rgba(255, 255, 255, 0.5);"
                   "     backdrop-filter: brightness(0.5) }",
               "<body><div id=g></div></body>");
    int c[3];
    p.at(80, 50, c);
    // The backdrop is halved to 64, then half-alpha white over it in sRGB:
    // 0.5*255 + 0.5*64 = 159.5.
    CHECK(std::abs(c[0] - 160) <= 2);
}
