#include "check.h"
#include "weva/block_layout.h"
#include "weva/box_builder.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/font_metrics.h"
#include "weva/html.h"
#include "weva/paint.h"
#include "weva/tessellate.h"
#include "weva/user_agent_stylesheet.h"
#include <cmath>
#include <map>
#include <memory>
#include <string>

using namespace weva;

namespace {

bool near(double a, double b) { return std::fabs(a - b) < 1e-6; }

// Records every call, so the interface contract can be asserted directly.
struct RecordingBackend : RenderInterface {
    struct Draw {
        std::vector<Vertex> vertices;
        std::vector<uint32_t> indices;
        Vec2 translation;
    };
    std::vector<Draw> draws;
    std::vector<GeometryHandle> compiled;
    int released = 0;
    uint64_t next_id = 1;
    std::map<uint64_t, Draw> pending;

    GeometryHandle compile_geometry(const std::vector<Vertex>& v,
                                    const std::vector<uint32_t>& i) override {
        GeometryHandle h{next_id++};
        pending[h.id] = Draw{v, i, {}};
        compiled.push_back(h);
        return h;
    }
    void render_geometry(GeometryHandle g, Vec2 t, TextureHandle) override {
        auto it = pending.find(g.id);
        if (it == pending.end()) return;
        Draw d = it->second;
        d.translation = t;
        draws.push_back(std::move(d));
    }
    void release_geometry(GeometryHandle g) override {
        released += pending.erase(g.id) ? 1 : 0;
    }
    TextureHandle load_texture(std::string_view, Vec2i*) override { return {}; }
    TextureHandle generate_texture(const std::vector<uint8_t>&, Vec2i) override { return {}; }
    void release_texture(TextureHandle) override {}
    void set_scissor(const Recti*) override {}
};

struct CascadeStyles : StyleProvider {
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
            if (c->node_type() == NodeType::Element) {
                compute_tree(static_cast<const Element&>(*c), raw);
            }
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
    CascadeStyles styles;
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
    bool layout(std::string_view html, double vw = 1000, double vh = 600) {
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
        return true;
    }
    BoxId find(std::string_view id, BoxId from = -2) const {
        const BoxId start = from == -2 ? root : from;
        if (start == kNoBox) return kNoBox;
        const Box& b = tree[start];
        if (b.element && (b.element->get_attribute("id") == id || b.element->tag_name() == id)) {
            return start;
        }
        for (BoxId c : tree.children(start)) {
            const BoxId hit = find(id, c);
            if (hit != kNoBox) return hit;
        }
        return kNoBox;
    }
    Mesh decorations(std::string_view id) const {
        Mesh m;
        paint_box_decorations(tree, find(id), ctx, 0, 0, &m);
        return m;
    }
};

// The bounding box of a mesh, for checking coverage without pinning vertex
// order.
Rect bounds(const Mesh& m) {
    if (m.vertices.empty()) return Rect::empty();
    double x0 = m.vertices[0].position.x, y0 = m.vertices[0].position.y;
    double x1 = x0, y1 = y0;
    for (const Vertex& v : m.vertices) {
        x0 = std::min<double>(x0, v.position.x);
        y0 = std::min<double>(y0, v.position.y);
        x1 = std::max<double>(x1, v.position.x);
        y1 = std::max<double>(y1, v.position.y);
    }
    return Rect(x0, y0, x1 - x0, y1 - y0);
}

} // namespace

void test_tessellate_rect() {
    Mesh m;
    tessellate_rect(Rect(10, 20, 30, 40), LinearColor::white(), &m);
    // Two triangles from four corners — an indexed quad, not six vertices.
    CHECK(m.vertices.size() == 4);
    CHECK(m.indices.size() == 6);
    CHECK(bounds(m) == Rect(10, 20, 30, 40));

    // Nothing is emitted for an empty rect or a fully transparent colour, so a
    // backend never sees a degenerate draw.
    Mesh e;
    tessellate_rect(Rect(0, 0, 0, 10), LinearColor::white(), &e);
    tessellate_rect(Rect(0, 0, 10, 10), LinearColor::transparent(), &e);
    CHECK(e.empty());

    // append() shifts the second mesh's indices so two shapes become one draw.
    Mesh a, b;
    tessellate_rect(Rect(0, 0, 1, 1), LinearColor::white(), &a);
    tessellate_rect(Rect(5, 5, 1, 1), LinearColor::black(), &b);
    a.append(b);
    CHECK(a.vertices.size() == 8 && a.indices.size() == 12);
    CHECK(a.indices[6] >= 4);
}

void test_tessellate_rounded() {
    // A zero radius costs nothing extra: it falls through to the plain quad,
    // which is the common case for most boxes.
    Mesh sharp;
    tessellate_rounded_rect(Rect(0, 0, 100, 50), BorderRadii::zero(), LinearColor::white(),
                            &sharp);
    CHECK(sharp.vertices.size() == 4);

    // A radius adds arc vertices but never leaves the rect's bounds.
    Mesh round;
    tessellate_rounded_rect(Rect(0, 0, 100, 50), BorderRadii::uniform(10), LinearColor::white(),
                            &round, 4);
    CHECK(round.vertices.size() > 4);
    const Rect bb = bounds(round);
    CHECK(near(bb.x, 0) && near(bb.y, 0));
    CHECK(near(bb.width, 100) && near(bb.height, 50));
    // A fan: one centre vertex plus the outline, three indices per edge.
    CHECK(round.indices.size() == (round.vertices.size() - 1) * 3);
}

void test_radii_clamping() {
    // CSS Backgrounds §5.5: overlapping radii scale by ONE factor, so the
    // shape keeps its proportions instead of only the offending corner
    // shrinking.
    BorderRadii r = BorderRadii::uniform(80);
    BorderRadii c = clamp_radii_to_rect(r, 100, 100);
    CHECK(near(c.top_left.x_radius, 50));
    CHECK(near(c.bottom_right.y_radius, 50));

    // A lopsided pair scales by the tightest edge, all corners together.
    BorderRadii lop(CornerRadius(90), CornerRadius(10), CornerRadius(10), CornerRadius(10));
    BorderRadii cl = clamp_radii_to_rect(lop, 100, 100);
    CHECK(near(cl.top_left.x_radius, 90));
    CHECK(near(cl.top_right.x_radius, 10));
    // Radii that already fit are returned untouched.
    CHECK(clamp_radii_to_rect(BorderRadii::uniform(10), 100, 100) == BorderRadii::uniform(10));

    // The inner edge of a border curves less than the outer: each radius is
    // reduced by the border width on that side, never below zero.
    BorderRadii in = inset_radii(BorderRadii::uniform(10), 4, 4, 4, 4);
    CHECK(near(in.top_left.x_radius, 6));
    BorderRadii flat = inset_radii(BorderRadii::uniform(2), 10, 10, 10, 10);
    CHECK(flat.is_zero());
}

void test_tessellate_border() {
    LinearColor c[4] = {LinearColor::white(), LinearColor::white(), LinearColor::white(),
                        LinearColor::white()};
    Mesh m;
    tessellate_border(Rect(0, 0, 100, 50), BorderRadii::zero(), 5, 5, 5, 5, c, &m, 2);
    // A ring, not four separate quads: paired outer and inner vertices, so a
    // mitred corner between two colours cannot double-cover.
    CHECK(!m.empty());
    CHECK(m.vertices.size() % 2 == 0);
    CHECK(bounds(m) == Rect(0, 0, 100, 50));

    // A zero-width border emits nothing at all.
    Mesh none;
    tessellate_border(Rect(0, 0, 100, 50), BorderRadii::zero(), 0, 0, 0, 0, c, &none);
    CHECK(none.empty());
}

void test_paint_decorations() {
    {
        // Background and border become geometry in one mesh, and the background
        // extends to the BORDER box — so a semi-transparent border shows it
        // through.
        Fixture f;
        CHECK(f.css("#a { display: block; width: 100px; height: 50px;"
                    "     background-color: #ff0000;"
                    "     border-top-style: solid; border-top-width: 4px;"
                    "     border-left-style: solid; border-left-width: 4px }"));
        CHECK(f.layout("<body><div id=a></div></body>"));
        const Mesh m = f.decorations("a");
        CHECK(!m.empty());
        // Width is the border box: 100 content + 4 left border.
        CHECK(near(bounds(m).width, 104));
        CHECK(near(bounds(m).height, 54));
        // The first vertices are the background, in linear space.
        CHECK(m.vertices[0].color.a == 1.0f);
        CHECK(m.vertices[0].color.r > 0.9f && m.vertices[0].color.g == 0.0f);
    }
    {
        // A box with neither background nor border produces no geometry, so an
        // ordinary layout div costs nothing at paint time.
        Fixture f;
        CHECK(f.css("#a { display: block; width: 100px; height: 50px }"));
        CHECK(f.layout("<body><div id=a></div></body>"));
        CHECK(f.decorations("a").empty());
    }
    {
        // An unset border-color is currentColor, which is what makes a border
        // follow the text colour by default.
        Fixture f;
        CHECK(f.css("#a { display: block; width: 50px; height: 50px; color: #00ff00;"
                    "     border-top-style: solid; border-top-width: 2px }"));
        CHECK(f.layout("<body><div id=a></div></body>"));
        const Mesh m = f.decorations("a");
        CHECK(!m.empty());
        bool green = false;
        for (const Vertex& v : m.vertices) {
            if (v.color.g > 0.9f && v.color.r == 0.0f) green = true;
        }
        CHECK(green);
    }
    {
        // A percentage corner radius resolves against the border box, and each
        // axis against its own extent.
        Fixture f;
        CHECK(f.css("#a { display: block; width: 200px; height: 100px;"
                    "     background-color: #fff; border-radius: 50% }"));
        CHECK(f.layout("<body><div id=a></div></body>"));
        const BorderRadii r = resolve_border_radii(f.tree[f.find("a")].style, 200, 100, f.ctx, 16);
        CHECK(near(r.top_left.x_radius, 100));
        CHECK(near(r.top_left.y_radius, 50));
    }
}

void test_paint_tree_calls() {
    // The backend contract: compile, render, release — and every compiled
    // handle is released, so a backend can assume no leak.
    Fixture f;
    CHECK(f.css("#a, #b { display: block; height: 20px; background-color: #123456 }"
                "#plain { display: block; height: 20px }"));
    CHECK(f.layout("<body><div id=a></div><div id=plain></div><div id=b></div></body>"));

    RecordingBackend backend;
    paint_tree(f.tree, f.root, f.ctx, &backend);
    // Two painted boxes; the undecorated one issues nothing.
    CHECK(backend.draws.size() == 2);
    CHECK(backend.compiled.size() == 2);
    CHECK(backend.released == 2);
    for (const auto& d : backend.draws) {
        CHECK(!d.indices.empty());
        CHECK(d.indices.size() % 3 == 0);
        // Every index is in range — a backend uploading these must not fault.
        for (uint32_t i : d.indices) CHECK(i < d.vertices.size());
    }
    // Boxes are painted in tree order, so `a` precedes `b` on screen.
    CHECK(backend.draws[0].vertices[0].position.y < backend.draws[1].vertices[0].position.y);
}
