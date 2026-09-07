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
#include <algorithm>
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
    // Without antialiasing: two triangles from four corners — an indexed quad,
    // not six vertices.
    Mesh plain;
    tessellate_rect(Rect(10, 20, 30, 40), LinearColor::white(), &plain, false);
    CHECK(plain.vertices.size() == 4);
    CHECK(plain.indices.size() == 6);
    CHECK(bounds(plain) == Rect(10, 20, 30, 40));

    // And WITH it, a plain rect is still the same four vertices: its edges are
    // axis-aligned, so a pixel-centre rasterizer already resolves them exactly
    // and a coverage ramp would buy nothing. Only curves carry one.
    Mesh m;
    tessellate_rect(Rect(10, 20, 30, 40), LinearColor::white(), &m);
    CHECK(m.vertices.size() == 4);
    CHECK(bounds(m) == Rect(10, 20, 30, 40));

    // Nothing is emitted for an empty rect or a fully transparent colour, so a
    // backend never sees a degenerate draw.
    Mesh e;
    tessellate_rect(Rect(0, 0, 0, 10), LinearColor::white(), &e);
    tessellate_rect(Rect(0, 0, 10, 10), LinearColor::transparent(), &e);
    CHECK(e.empty());

    // append() shifts the second mesh's indices so two shapes become one draw.
    // A 1x1 rect is too thin to inset half a pixel from both sides without
    // turning inside out, so it keeps the plain four-vertex quad — which also
    // keeps this checking what it is about, the index shifting.
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
                            &sharp, 8, false);
    CHECK(sharp.vertices.size() == 4);

    // A radius adds arc vertices but never leaves the rect's bounds — beyond
    // the half-pixel coverage ramp, which every antialiased shape carries.
    Mesh round;
    tessellate_rounded_rect(Rect(0, 0, 100, 50), BorderRadii::uniform(10), LinearColor::white(),
                            &round, 4, false);
    CHECK(round.vertices.size() > 4);
    const Rect bb = bounds(round);
    CHECK(near(bb.x, 0) && near(bb.y, 0));
    CHECK(near(bb.width, 100) && near(bb.height, 50));
    // A fan: one centre vertex plus the outline, three indices per edge.
    CHECK(round.indices.size() == (round.vertices.size() - 1) * 3);

    // Antialiased, it is the same outline twice — inset and expanded — around
    // the same centre, so the vertex count doubles less the shared centre.
    Mesh aa;
    tessellate_rounded_rect(Rect(0, 0, 100, 50), BorderRadii::uniform(10), LinearColor::white(),
                            &aa, 4);
    CHECK(aa.vertices.size() == (round.vertices.size() - 1) * 2 + 1);
    const Rect ab = bounds(aa);
    CHECK(near(ab.x, -0.5) && near(ab.y, -0.5));
    CHECK(near(ab.width, 101) && near(ab.height, 51));
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

    // A corner that collapses on ONE axis is square, so both axes go to zero
    // together. `border-bottom: 20px; border-radius: 11px` shrinks the bottom
    // corners' y-radius to nothing while their x-radius, reduced by a zero
    // side width, stays 11 -- and a radius of (11, 0) is neither a curve nor a
    // corner. rounded_outline hands it to arc_points, which has no arc to
    // sweep and emits the arc's CENTRE, a point 11px in from where the corner
    // belongs. The border then came out a TRAPEZOID.
    BorderRadii one_axis = inset_radii(BorderRadii::uniform(11), 0, 0, 20, 0);
    CHECK(near(one_axis.bottom_right.x_radius, 0));
    CHECK(near(one_axis.bottom_right.y_radius, 0));
    CHECK(near(one_axis.bottom_left.x_radius, 0));
    CHECK(near(one_axis.bottom_left.y_radius, 0));
    // The corners the border does not touch keep their curve.
    CHECK(near(one_axis.top_left.x_radius, 11));
    CHECK(near(one_axis.top_right.y_radius, 11));
}

// The ring must not stray outside the box it is a border of.
//
// With a radius and unequal widths, the inner outline used to run diagonally
// from a corner that had collapsed to its arc centre, so the ring swallowed a
// wedge of the content area that widened along the whole edge. Checking the
// vertices stay within the band each side declares catches that without
// needing a picture.
void test_tessellate_border_uneven_widths() {
    LinearColor c[4] = {LinearColor::white(), LinearColor::white(), LinearColor::white(),
                        LinearColor::white()};
    const Rect box(0, 0, 300, 90);
    Mesh m;
    tessellate_border(box, BorderRadii::uniform(11), 0, 0, 20, 0, c, &m, 8, false);
    CHECK(!m.empty());
    // Only the bottom side has width, so nothing may sit more than 20px above
    // the bottom edge, and nothing may be inset horizontally at all.
    double min_y = 1e9, min_x = 1e9, max_x = -1e9;
    for (const Vertex& v : m.vertices) {
        min_y = std::min(min_y, static_cast<double>(v.position.y));
        min_x = std::min(min_x, static_cast<double>(v.position.x));
        max_x = std::max(max_x, static_cast<double>(v.position.x));
    }
    CHECK(near(min_x, 0));
    CHECK(near(max_x, 300));
    // 70 is the inner edge; the top corners' arcs are the only thing above it,
    // and with zero top and side widths they collapse onto the outer outline.
    CHECK(min_y >= 0);

    // The inner outline's right edge must be VERTICAL. The right border is
    // zero wide, so from the bottom of the top-right curve (y = 11) down to
    // the inner bottom edge (y = 70) the inner ring sits exactly on x = 300.
    //
    // This is the assertion that fails on the old code: the collapsed
    // bottom-right corner landed at its arc CENTRE, (289, 70), pulling the
    // inner edge 11px inward and turning the whole side into a wedge. Vertex
    // pairs are (outer, inner), so the odd indices are the inner ring.
    int inner_right = 0;
    for (size_t i = 1; i < m.vertices.size(); i += 2) {
        const double x = m.vertices[i].position.x;
        const double y = m.vertices[i].position.y;
        if (y >= 11 - 1e-6 && y <= 70 + 1e-6 && x > 150) {
            ++inner_right;
            CHECK(near(x, 300));
        }
    }
    CHECK(inner_right > 0);
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

    // ONE side drawn takes ONE colour, all the way along.
    //
    // Corners are assigned to whichever side they are nearest, and a corner
    // assigned to a side with no width used to hand its colour to the strip a
    // drawn side was making. An unset border-color is `currentColor`, so a
    // table cell with nothing but `border-bottom: 1px solid <faint>` drew its
    // rule fading into the TEXT colour along its length -- dark at one end,
    // bright at the other, stepping at every cell boundary.
    const LinearColor sides[4] = {LinearColor(1, 0, 0, 1), LinearColor(0, 1, 0, 1),
                                  LinearColor(0, 0, 1, 1), LinearColor(1, 1, 0, 1)};
    Mesh bottom_only;
    tessellate_border(Rect(0, 0, 100, 50), BorderRadii::zero(), 0, 0, 1, 0, sides, &bottom_only);
    CHECK(!bottom_only.empty());
    for (const Vertex& v : bottom_only.vertices) {
        // sides[2], the bottom: blue.
        CHECK(v.color.b == 1.0f && v.color.r == 0.0f && v.color.g == 0.0f);
    }

    // And two adjacent sides still each get their own, so this did not flatten
    // a real four-colour border into one.
    Mesh two;
    tessellate_border(Rect(0, 0, 100, 50), BorderRadii::zero(), 2, 0, 2, 0, sides, &two);
    bool saw_top = false, saw_bottom = false;
    for (const Vertex& v : two.vertices) {
        if (v.color.r == 1.0f && v.color.g == 0.0f) saw_top = true;
        if (v.color.b == 1.0f) saw_bottom = true;
        // Never the undrawn left or right.
        CHECK(!(v.color.g == 1.0f && v.color.r == 0.0f));
        CHECK(!(v.color.r == 1.0f && v.color.g == 1.0f));
    }
    CHECK(saw_top && saw_bottom);
}

void test_border_side_colors_and_bevels() {
    const auto draw = [](const Mesh& mesh) {
        SoftwareRenderer renderer(120, 80);
        const auto geometry = renderer.compile_geometry(mesh.vertices, mesh.indices);
        renderer.render_geometry(geometry, {0, 0}, {});
        renderer.release_geometry(geometry);
        return renderer;
    };
    const auto same = [](const LinearColor& a, const LinearColor& b) {
        return near(a.r, b.r) && near(a.g, b.g) && near(a.b, b.b) && near(a.a, b.a);
    };
    for (double radius : {0., 12., 20.}) for (float alpha : {1.f, .5f}) {
        const LinearColor colors[] = {{1,0,0,alpha},{0,1,0,alpha},{0,0,1,alpha},{1,1,0,alpha}};
        Mesh mesh;
        tessellate_border(Rect(10,10,100,60), BorderRadii::uniform(radius), 4,7,10,13, colors, &mesh);
        const auto renderer = draw(mesh);
        // Straight parts of each side keep one color all the way along.
        for (int x=32; x<88; ++x) {
            CHECK(same(renderer.pixel(x,12),colors[0]));
            CHECK(same(renderer.pixel(x,65),colors[2]));
        }
        for (int y=32; y<48; ++y) {
            CHECK(same(renderer.pixel(106,y),colors[1]));
            CHECK(same(renderer.pixel(16,y),colors[3]));
        }
        if (radius==0) {
            // Mitres tile the frame exactly once, including translucent seams.
            for (int y=10; y<70; ++y) for (int x=10; x<110; ++x) {
                const bool frame=x<23 || x>=103 || y<14 || y>=60;
                CHECK(near(renderer.pixel(x,y).a,frame ? alpha : 0));
            }
        }
    }
    // Chrome 152, opaque edge pixels from independent CSS inset/outset boxes.
    struct Sample { const char* color; uint8_t light[3],dark[3]; };
    const Sample samples[] = {
        {"#000",{168,168,168},{84,84,84}}, {"#101010",{184,184,184},{100,100,100}},
        {"#202020",{200,200,200},{116,116,116}}, {"#505050",{164,164,164},{0,0,0}},
        {"#767676",{202,202,202},{33,33,33}}, {"#808080",{212,212,212},{44,44,44}},
        {"#ebebeb",{255,255,255},{151,151,151}}, {"#fff",{255,255,255},{171,171,171}},
        {"#f00",{255,0,0},{171,0,0}}, {"#00f",{0,0,255},{0,0,171}},
        {"#008000",{0,212,0},{0,44,0}},
    };
    for (const auto& sample : samples) for (bool inset : {false,true}) {
        Fixture f;
        CHECK(f.css(std::string("#a{box-sizing:border-box;width:100px;height:60px;border:8px ")+
                    (inset ? "inset " : "outset ")+sample.color+"}"));
        CHECK(f.layout("<div id=a></div>"));
        const auto renderer=draw(f.decorations("a"));
        const auto light=LinearColor::from_srgb(sample.light[0],sample.light[1],sample.light[2],1);
        const auto dark=LinearColor::from_srgb(sample.dark[0],sample.dark[1],sample.dark[2],1);
        CHECK(same(renderer.pixel(50,3),inset ? dark : light));
        CHECK(same(renderer.pixel(3,30),inset ? dark : light));
        CHECK(same(renderer.pixel(50,56),inset ? light : dark));
        CHECK(same(renderer.pixel(96,30),inset ? light : dark));
    }
    // Mixed per-side styles, currentColor and alpha survive the shading step.
    Fixture mixed;
    CHECK(mixed.css("#a{box-sizing:border-box;width:100px;height:60px;border:8px solid;"
                    "color:rgba(0,0,0,.5);border-top-style:outset;border-left-style:inset;"
                    "border-right-style:none}"));
    CHECK(mixed.layout("<div id=a></div>"));
    const auto pixels=draw(mixed.decorations("a"));
    CHECK(same(pixels.pixel(50,3),LinearColor::from_srgb(168,168,168,.5f)));
    CHECK(same(pixels.pixel(3,30),LinearColor::from_srgb(84,84,84,.5f)));
    CHECK(same(pixels.pixel(50,56),LinearColor(0,0,0,.5f)));
    CHECK(pixels.pixel(96,30).a==0);
    Fixture keywords;
    CHECK(keywords.css("#a{box-sizing:border-box;width:100px;height:60px;border:8px OuTsEt CURRENTCOLOR;"
                       "color:black;border-top-style:/* bevel */InSeT}"));
    CHECK(keywords.layout("<div id=a></div>"));
    const auto keyword_pixels=draw(keywords.decorations("a"));
    CHECK(same(keyword_pixels.pixel(50,3),LinearColor::from_srgb(84,84,84,1)));
    CHECK(same(keyword_pixels.pixel(3,30),LinearColor::from_srgb(168,168,168,1)));

    Fixture button;
    CHECK(button.css("button{font-size:14px;line-height:16px}#plain{border:0;padding:0}"));
    CHECK(button.layout("<button id=a>Start</button><button id=plain>Reset</button>"));
    const auto& box=button.tree[button.find("a")];
    CHECK(near(box.height,22));
    CHECK(near(box.border_top,2) && near(box.border_right,2) && near(box.border_bottom,2) && near(box.border_left,2));
    CHECK(near(box.padding_top,1) && near(box.padding_bottom,1));
    CHECK(near(box.padding_left,6) && near(box.padding_right,6));
    CHECK(near(button.tree[button.find("plain")].height,16));
    Fixture control_font;
    CHECK(control_font.css("#root{font:italic bold 30px/60px serif}#inherit{font:inherit}"
                           "span{display:block;width:1em;height:1em}"));
    CHECK(control_font.layout("<div id=root><button id=small><span id=em></span></button>"
                              "<button id=inherit><span id=parentem></span></button></div>"));
    CHECK(near(control_font.tree[control_font.find("em")].width,40.0/3));
    CHECK(near(control_font.tree[control_font.find("parentem")].width,30));
}

void test_paint_color_resolution() {
    ComputedStyle parent, child, other;
    child.set_inherit_parent(&parent);
    parent.set("color", "#ff0000");
    CHECK(resolve_color(&child, "color") == LinearColor(1, 0, 0, 1));
    parent.set("color", "#00ff00");
    CHECK(resolve_color(&child, "color") == LinearColor(0, 1, 0, 1));
    child.set("color", "#0000ff");
    CHECK(resolve_color(&child, "color") == LinearColor(0, 0, 1, 1));
    child.unset(CssPropertyRegistry::instance().id_of("color"));
    CHECK(resolve_color(&child, "color") == LinearColor(0, 1, 0, 1));
    other.set("color", "#ff00ff");
    child.set_inherit_parent(&other);
    CHECK(resolve_color(&child, "color") == LinearColor(1, 0, 1, 1));

    // Non-inherited slots, invalid values and clearing a warmed style must
    // all produce the current answer as values change between types.
    CHECK(resolve_color(&child, "background-color").a == 0);
    child.set("background-color", "20px");
    CHECK(resolve_color(&child, "background-color").a == 0);
    child.set("background-color", "rgba(255,0,0,.25)");
    CHECK(resolve_color(&child, "background-color") == LinearColor(1, 0, 0, 0.25f));
    child.clear();
    CHECK(resolve_color(&child, "background-color").a == 0);
    CHECK(resolve_color(&child, "color") == LinearColor(0, 0, 0, 1));

    // The public resolver also accepts names without a registered parsed slot.
    parent.set("--ink", "#00ffff");
    child.set_inherit_parent(&parent);
    CHECK(resolve_color(&child, "--ink") == LinearColor(0, 1, 1, 1));
    parent.set("--ink", "#ff0000");
    CHECK(resolve_color(&child, "--ink") == LinearColor(1, 0, 0, 1));
    CHECK(resolve_color(&child, "--missing").a == 0);
    CHECK(resolve_color(nullptr, "color").a == 0);
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

namespace {
double mesh_area(const Mesh& m) {
    double a = 0;
    for (size_t i = 0; i + 2 < m.indices.size(); i += 3) {
        const auto& p = m.vertices[m.indices[i]].position;
        const auto& q = m.vertices[m.indices[i + 1]].position;
        const auto& r = m.vertices[m.indices[i + 2]].position;
        a += std::fabs((q.x - p.x) * (r.y - p.y) - (q.y - p.y) * (r.x - p.x)) * 0.5;
    }
    return a;
}
double polygon_area(const std::vector<ClipPoint>& p) {
    double a = 0;
    for (size_t i = 0; i < p.size(); ++i) {
        const ClipPoint& u = p[i];
        const ClipPoint& v = p[(i + 1) % p.size()];
        a += u.x * v.y - v.x * u.y;
    }
    return std::fabs(a) * 0.5;
}
} // namespace

// Polygon clipping covers exactly the intersection, convex or not.
void test_clip_triangles_polygon() {
    {
        // Clipping must retain the index sharing of interior geometry,
        // including when appending to a mesh or passing through nested clips.
        // Coincident vertices with different attributes remain distinct.
        for (bool rounded : {false, true}) for (bool reverse : {false, true}) {
            PreparedClip clip;
            clip.polygon = rounded_rect_outline(
                rounded ? Rect(0, 0, 200, 6) : Rect(-20, -20, 240, 60),
                rounded ? BorderRadii::uniform(3) : BorderRadii::zero());
            if (reverse) std::reverse(clip.polygon.begin(), clip.polygon.end());
            clip.prepare();
            Mesh input;
            tessellate_rect(Rect(3, 0.25, 121, 5.5), LinearColor::white(), &input);
            for (size_t i = 0; i < input.vertices.size(); ++i) {
                auto& v = input.vertices[i];
                v.tex_coord = {static_cast<float>(i) / 4, static_cast<float>(i) / 8};
                v.color = LinearColor(0.2f, static_cast<float>(i) / 4, 0.7f, 0.5f);
            }
            input.vertices.push_back(input.vertices.front());
            input.vertices.back().color.a = 0.25f;
            input.indices.insert(input.indices.end(), {4, 2, 3});
            Mesh out;
            tessellate_rect(Rect(-10, -10, 1, 1), LinearColor::black(), &out);
            const Mesh prefix = out;
            for (int pass = 0; pass < 2; ++pass) {
                const size_t vertex_base = out.vertices.size();
                const size_t index_base = out.indices.size();
                clip_triangles_polygon(input.vertices, input.indices, clip, &out);
                CHECK(out.vertices.size() == vertex_base + input.vertices.size());
                CHECK(out.indices.size() == index_base + input.indices.size());
                for (size_t i = 0; i < input.indices.size(); ++i) {
                    const auto& a = input.vertices[input.indices[i]];
                    const auto& b = out.vertices[out.indices[index_base + i]];
                    CHECK(a.position.x == b.position.x && a.position.y == b.position.y &&
                          a.tex_coord.x == b.tex_coord.x && a.tex_coord.y == b.tex_coord.y &&
                          a.color.r == b.color.r && a.color.g == b.color.g &&
                          a.color.b == b.color.b && a.color.a == b.color.a);
                }
            }
            CHECK(std::equal(prefix.indices.begin(), prefix.indices.end(), out.indices.begin()));
            for (size_t i = 0; i < prefix.vertices.size(); ++i) {
                CHECK(prefix.vertices[i].position.x == out.vertices[i].position.x &&
                      prefix.vertices[i].position.y == out.vertices[i].position.y &&
                      prefix.vertices[i].color.a == out.vertices[i].color.a);
            }
            Mesh first, second;
            clip_triangles_polygon(input.vertices, input.indices, clip, &first);
            clip_triangles_polygon(first.vertices, first.indices, clip, &second);
            CHECK(second.vertices.size() == input.vertices.size());
            CHECK(second.indices == first.indices);
        }
    }
    {
        // A solid triangle along a thin rounded bar lies outside the clip's
        // inscribed rectangle but entirely inside its curved boundary. It
        // should survive intact in either winding, including translucent fill.
        for (bool reverse : {false, true}) {
            PreparedClip clip;
            clip.polygon = rounded_rect_outline(Rect(0, 0, 200, 6), BorderRadii::uniform(3));
            if (reverse) std::reverse(clip.polygon.begin(), clip.polygon.end());
            clip.prepare();
            std::vector<Vertex> input(3);
            input[0].position = {3, 0.25f};
            input[1].position = {124, 0.25f};
            input[2].position = {60, 3};
            for (Vertex& v : input) v.color = LinearColor(0.2f, 0.7f, 0.4f, 0.5f);
            Mesh out;
            clip_triangles_polygon(input, {0, 1, 2}, clip, &out);
            CHECK(out.vertices.size() == 3);
            CHECK(out.indices == std::vector<uint32_t>({0, 1, 2}));
            CHECK(std::fabs(mesh_area(out) - 121 * 2.75 / 2) < 1e-6);
            // Containment does not depend on attributes. Only a boundary
            // crossing should generate interpolated vertices.
            for (int variant = 0; variant < 4; ++variant) {
                auto varied = input;
                if (variant == 0) varied[1].tex_coord.x = 1;
                if (variant == 1) varied[1].color.r = 1;
                if (variant == 2) varied[1].color.a = 0;
                if (variant == 3) varied[1].position.y = -1;
                PreparedClip control = clip;
                control.convex = false;
                Mesh actual, expected;
                clip_triangles_polygon(varied, {0, 1, 2}, clip, &actual);
                if (variant == 3) {
                    clip_triangles_polygon(varied, {0, 1, 2}, control, &expected);
                } else {
                    expected.vertices = varied;
                    expected.indices = {0, 1, 2};
                }
                CHECK(actual.indices == expected.indices);
                CHECK(actual.vertices.size() == expected.vertices.size());
                for (size_t i = 0; i < std::min(actual.vertices.size(), expected.vertices.size()); ++i) {
                    const Vertex& a = actual.vertices[i];
                    const Vertex& b = expected.vertices[i];
                    CHECK(a.position.x == b.position.x && a.position.y == b.position.y &&
                          a.tex_coord.x == b.tex_coord.x && a.tex_coord.y == b.tex_coord.y &&
                          a.color.r == b.color.r && a.color.g == b.color.g &&
                          a.color.b == b.color.b && a.color.a == b.color.a);
                }
            }
        }
    }
    Mesh square;
    tessellate_rect(Rect(0, 0, 100, 100), LinearColor::white(), &square);
    {
        // Regular hexagon inside the square (clip-path: polygon(50% 0, 100% 25%, ...)).
        const std::vector<ClipPoint> hex = {{50, 0}, {100, 25}, {100, 75}, {50, 100}, {0, 75}, {0, 25}};
        Mesh out;
        clip_triangles_polygon(square.vertices, square.indices, hex, &out);
        CHECK(std::fabs(mesh_area(out) - polygon_area(hex)) < 0.05);
        CHECK(std::fabs(polygon_area(hex) - 7500) < 1e-9);
    }
    {
        // Concave arrow, clockwise winding: still the exact intersection.
        const std::vector<ClipPoint> arrow = {{0, 0}, {100, 50}, {0, 100}, {30, 50}};
        Mesh out;
        clip_triangles_polygon(square.vertices, square.indices, arrow, &out);
        CHECK(std::fabs(mesh_area(out) - polygon_area(arrow)) < 0.05);
        // Nothing survives in the notch.
        for (const Vertex& v : out.vertices) {
            if (std::fabs(v.position.y - 50) < 1e-6) CHECK(v.position.x >= 30 - 1e-6);
        }
    }
    {
        // A polygon partly outside the mesh clips to the mesh's part of it.
        const std::vector<ClipPoint> tri = {{50, 50}, {150, 50}, {150, 150}};
        Mesh out;
        clip_triangles_polygon(square.vertices, square.indices, tri, &out);
        CHECK(std::fabs(mesh_area(out) - 1250) < 1e-6);   // the corner triangle 50..100
    }
    {
        // Rounded rectangle outline: arcs on rounded corners, a point on square ones.
        BorderRadii r;
        r.top_left = CornerRadius(20);
        const std::vector<ClipPoint> o = rounded_rect_outline(Rect(0, 0, 100, 50), r, 4);
        CHECK(o.size() == 5 + 3);
        CHECK(std::fabs(o.front().x - 0) < 1e-9 && std::fabs(o.front().y - 20) < 1e-9);
        CHECK(std::fabs(o[4].x - 20) < 1e-9 && std::fabs(o[4].y - 0) < 1e-9);
        for (const ClipPoint& p : o) CHECK(p.x >= -1e-9 && p.x <= 100 + 1e-9 && p.y >= -1e-9 && p.y <= 50 + 1e-9);
        // The corner itself is cut off: area is the rect minus the corner's
        // (square - quarter circle), approximately.
        const double a = polygon_area(o);
        CHECK(a < 5000 && a > 5000 - 400 * (1 - 3.14159 / 4) - 30);
    }
    {
        // Differential oracle for the triangle cutter: the original
        // dynamic polygon algorithm, including UV/coverage interpolation.
        // Exact vertex order and values matter; equivalent areas can still
        // produce different pixels along shared edges.
        const auto interpolate = [](const Vertex& a, const Vertex& b, double t) {
            Vertex v;
            const float f = static_cast<float>(t);
            v.position = {a.position.x + (b.position.x - a.position.x) * f,
                          a.position.y + (b.position.y - a.position.y) * f};
            v.tex_coord = {a.tex_coord.x + (b.tex_coord.x - a.tex_coord.x) * f,
                           a.tex_coord.y + (b.tex_coord.y - a.tex_coord.y) * f};
            v.color = LinearColor(a.color.r + (b.color.r - a.color.r) * f,
                                  a.color.g + (b.color.g - a.color.g) * f,
                                  a.color.b + (b.color.b - a.color.b) * f,
                                  a.color.a + (b.color.a - a.color.a) * f);
            return v;
        };
        uint32_t seed = 0x981731u;
        const auto sample = [&] {
            seed = seed * 1664525u + 1013904223u;
            return (static_cast<int>((seed >> 8) % 1400) - 350) / 7.f;
        };
        for (int trial = 0; trial < 2000; ++trial) {
            PreparedClip clip;
            clip.pieces.push_back({ClipPoint{sample(), sample()}, ClipPoint{sample(), sample()},
                                   ClipPoint{sample(), sample()}});
            clip.x0 = clip.y0 = -1000; clip.x1 = clip.y1 = 1000;
            std::vector<Vertex> input(3);
            for (Vertex& v : input) {
                v.position = {sample(), sample()};
                v.tex_coord = {sample(), sample()};
                v.color = LinearColor(sample(), sample(), sample(), sample());
            }
            if (trial % 7 == 0) input[1].position = input[0].position;
            if (trial % 11 == 0) input[0].position = {
                static_cast<float>(clip.pieces[0][0].x), static_cast<float>(clip.pieces[0][0].y)};
            std::vector<Vertex> expected = input;
            for (int edge = 0; edge < 3; ++edge) {
                const auto& p = clip.pieces[0][edge];
                const auto& q = clip.pieces[0][(edge + 1) % 3];
                const double ex = q.x - p.x, ey = q.y - p.y;
                const auto side = [&](const Vertex& v) {
                    return ex * (v.position.y - p.y) - ey * (v.position.x - p.x);
                };
                std::vector<Vertex> cut;
                for (size_t i = 0; i < expected.size(); ++i) {
                    const Vertex& cur = expected[i];
                    const Vertex& prev = expected[(i + expected.size() - 1) % expected.size()];
                    if (side(cur) >= 0) {
                        if (side(prev) < 0)
                            cut.push_back(interpolate(prev, cur, side(prev) / (side(prev) - side(cur))));
                        cut.push_back(cur);
                    } else if (side(prev) >= 0) {
                        cut.push_back(interpolate(prev, cur, side(prev) / (side(prev) - side(cur))));
                    }
                }
                expected = std::move(cut);
            }
            if (expected.size() < 3) expected.clear();
            Mesh out;
            clip_triangles_polygon(input, {0, 1, 2}, clip, &out);
            CHECK(out.vertices.size() == expected.size());
            std::vector<uint32_t> indices;
            for (uint32_t i = 1; i + 1 < expected.size(); ++i) indices.insert(indices.end(), {0, i, i + 1});
            CHECK(out.indices == indices);
            for (size_t i = 0; i < std::min(out.vertices.size(), expected.size()); ++i) {
                const Vertex& a = out.vertices[i];
                const Vertex& b = expected[i];
                CHECK(a.position.x == b.position.x && a.position.y == b.position.y);
                CHECK(a.tex_coord.x == b.tex_coord.x && a.tex_coord.y == b.tex_coord.y);
                CHECK(a.color.r == b.color.r && a.color.g == b.color.g &&
                      a.color.b == b.color.b && a.color.a == b.color.a);
            }
        }
    }
}

void test_clip_triangles() {
    // A quad straddling the rect's right edge is cut at it; one inside passes
    // through untouched; one outside vanishes. Colour and UV interpolate.
    Mesh quad;
    Vertex v[4];
    const float xs[4] = {0, 100, 100, 0}, ys[4] = {0, 0, 50, 50};
    for (int i = 0; i < 4; ++i) {
        v[i].position = {xs[i], ys[i]};
        v[i].color = LinearColor(xs[i] / 100.0f, 0, 0, 1);
        v[i].tex_coord = {xs[i] / 100.0f, ys[i] / 50.0f};
        quad.vertices.push_back(v[i]);
    }
    quad.indices = {0, 1, 2, 0, 2, 3};

    Mesh out;
    clip_triangles(quad.vertices, quad.indices, Rect(0, 0, 60, 100), &out);
    CHECK(!out.empty());
    double max_x = 0, max_u = 0;
    for (const Vertex& p : out.vertices) {
        max_x = std::max<double>(max_x, p.position.x);
        max_u = std::max<double>(max_u, p.tex_coord.x);
        CHECK(p.position.x <= 60 + 1e-3);   // float positions
        CHECK(std::fabs(p.color.r - p.position.x / 100.0) < 1e-5);
    }
    CHECK(std::fabs(max_x - 60) < 1e-3);
    CHECK(std::fabs(max_u - 0.6) < 1e-4);

    Mesh inside;
    clip_triangles(quad.vertices, quad.indices, Rect(-10, -10, 200, 200), &inside);
    CHECK(inside.vertices.size() == 6 && inside.indices.size() == 6);

    Mesh outside;
    clip_triangles(quad.vertices, quad.indices, Rect(200, 200, 10, 10), &outside);
    CHECK(outside.empty());
}
