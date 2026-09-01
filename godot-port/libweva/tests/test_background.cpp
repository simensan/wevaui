// CSS Backgrounds L3 / Images L3: the background shorthand, gradient parsing
// and sampling, layer rasterization, and the painted result (one textured
// draw per gradient box; the body's background on the canvas).
#include "check.h"
#include "weva/background.h"
#include "weva/block_layout.h"
#include "weva/box_builder.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/font_metrics.h"
#include "weva/html.h"
#include "weva/paint.h"
#include "weva/positioning.h"
#include "weva/shorthand.h"
#include "weva/user_agent_stylesheet.h"

#include <cmath>
#include <map>
#include <memory>
#include <string>
#include <vector>

using namespace weva;

namespace {

bool near(double a, double b, double eps = 1e-6) { return std::fabs(a - b) < eps; }

std::string longhand(const std::vector<ShorthandLonghand>& out, std::string_view name) {
    for (const ShorthandLonghand& l : out) {
        if (l.property == name) return l.value;
    }
    return "<missing>";
}

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

struct RecordingBackend : RenderInterface {
    struct Geometry {
        std::vector<Vertex> vertices;
        std::vector<uint32_t> indices;
    };
    struct Draw {
        Geometry geometry;
        uint64_t texture = 0;
    };
    std::map<uint64_t, Geometry> compiled;
    std::map<uint64_t, Vec2i> textures;
    std::vector<Draw> draws;
    uint64_t next = 1;

    GeometryHandle compile_geometry(const std::vector<Vertex>& v,
                                    const std::vector<uint32_t>& i) override {
        compiled[next] = {v, i};
        return GeometryHandle{next++};
    }
    void render_geometry(GeometryHandle g, Vec2, TextureHandle t) override {
        draws.push_back({compiled[g.id], t.id});
    }
    void release_geometry(GeometryHandle g) override { compiled.erase(g.id); }
    TextureHandle load_texture(std::string_view, Vec2i*) override { return {}; }
    TextureHandle generate_texture(const std::vector<uint8_t>&, Vec2i size) override {
        textures[next] = size;
        return TextureHandle{next++};
    }
    void release_texture(TextureHandle t) override { textures.erase(t.id); }
    void set_scissor(const Recti*) override {}
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

    BoxId find(std::string_view id, BoxId from = -2) const {
        const BoxId start = from == -2 ? root : from;
        if (start == kNoBox) return kNoBox;
        const Box& b = tree[start];
        if (b.kind == BoxKind::Block && b.element && b.element->get_attribute("id") == id) return start;
        for (BoxId c : tree.children(start)) {
            const BoxId hit = find(id, c);
            if (hit != kNoBox) return hit;
        }
        return kNoBox;
    }

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

Rect bounds_of(const RecordingBackend::Geometry& g) {
    double x0 = 1e300, y0 = 1e300, x1 = -1e300, y1 = -1e300;
    for (const Vertex& v : g.vertices) {
        x0 = std::min<double>(x0, v.position.x);
        y0 = std::min<double>(y0, v.position.y);
        x1 = std::max<double>(x1, v.position.x);
        y1 = std::max<double>(y1, v.position.y);
    }
    return Rect(x0, y0, x1 - x0, y1 - y0);
}

} // namespace

void test_background_shorthand() {
    {
        std::vector<ShorthandLonghand> out;
        CHECK(expand_shorthand("background", "linear-gradient(180deg, #000 0%, #fff 100%)", &out));
        CHECK(longhand(out, "background-image") == "linear-gradient(180deg, #000 0%, #fff 100%)");
        CHECK(longhand(out, "background-color") == "transparent");
        CHECK(longhand(out, "background-repeat") == "repeat");
        CHECK(longhand(out, "background-size") == "auto");
        CHECK(longhand(out, "background-position") == "0% 0%");
    }
    {
        std::vector<ShorthandLonghand> out;
        CHECK(expand_shorthand("background", "#123456 url(x.png) no-repeat center / cover", &out));
        CHECK(longhand(out, "background-color") == "#123456");
        CHECK(longhand(out, "background-image") == "url(x.png)");
        CHECK(longhand(out, "background-repeat") == "no-repeat");
        CHECK(longhand(out, "background-position") == "center");
        CHECK(longhand(out, "background-size") == "cover");
    }
    {
        // A bare colour: the image resets to none, which is what lets a rule
        // override an earlier gradient.
        std::vector<ShorthandLonghand> out;
        CHECK(expand_shorthand("background", "rgba(255, 255, 255, 0.04)", &out));
        CHECK(longhand(out, "background-color") == "rgba(255, 255, 255, 0.04)");
        CHECK(longhand(out, "background-image") == "none");
    }
    {
        // Two layers: two images, one colour, per-layer lists.
        std::vector<ShorthandLonghand> out;
        CHECK(expand_shorthand("background",
                               "radial-gradient(circle at 50% 35%, red, transparent 60%), "
                               "linear-gradient(180deg, #2a1c52 0%, #110a23 100%) #000",
                               &out));
        CHECK(longhand(out, "background-image") ==
              "radial-gradient(circle at 50% 35%, red, transparent 60%), "
              "linear-gradient(180deg, #2a1c52 0%, #110a23 100%)");
        CHECK(longhand(out, "background-color") == "#000");
        CHECK(longhand(out, "background-repeat") == "repeat, repeat");
    }
}

void test_gradient_parsing() {
    const LinearColor black = LinearColor::black();
    {
        Gradient g;
        CHECK(parse_gradient("linear-gradient(to right, red, blue)", black, &g));
        CHECK(g.kind == Gradient::Kind::Linear);
        CHECK(near(g.angle_deg, 90));
        CHECK(g.stops.size() == 2);
        CHECK(!g.stops[0].has_position && !g.stops[1].has_position);
    }
    {
        Gradient g;
        CHECK(parse_gradient("linear-gradient(45deg, #000 10%, #fff 90%)", black, &g));
        CHECK(near(g.angle_deg, 45));
        CHECK(near(g.stops[0].position, 0.1) && near(g.stops[1].position, 0.9));
    }
    {
        Gradient g;
        CHECK(parse_gradient("linear-gradient(to top right, red, blue)", black, &g));
        CHECK(g.corner == (2 | 4));
    }
    {
        Gradient g;
        CHECK(parse_gradient("radial-gradient(circle at 50% 35%, rgba(179, 136, 255, 0.35), transparent 60%)",
                             black, &g));
        CHECK(g.kind == Gradient::Kind::Radial);
        CHECK(g.circle);
        CHECK(g.pos_x_raw == "50%" && g.pos_y_raw == "35%");
        CHECK(g.stops.size() == 2 && near(g.stops[1].position, 0.6));
        CHECK(near(g.stops[1].color.a, 0));
    }
    {
        Gradient g;
        CHECK(parse_gradient("conic-gradient(from 90deg at 25% 75%, red, blue)", black, &g));
        CHECK(g.kind == Gradient::Kind::Conic);
        CHECK(near(g.from_deg, 90));
        CHECK(g.pos_x_raw == "25%" && g.pos_y_raw == "75%");
    }
    {
        // A colour hint between two stops, a double-position stop, and the
        // repeating form.
        Gradient g;
        CHECK(parse_gradient("repeating-linear-gradient(90deg, red 0px, 30%, blue 10px 20px)", black, &g));
        CHECK(g.repeating);
        CHECK(g.stops.size() == 4);
        CHECK(g.stops[1].is_hint);
        CHECK(g.stops[2].is_px && near(g.stops[2].position, 10));
        CHECK(near(g.stops[3].position, 20));
    }
    {
        Gradient g;
        CHECK(!parse_gradient("url(x.png)", black, &g));
        CHECK(!parse_gradient("linear-gradient(red)", black, &g));
    }
}

// A calc() stop position: `calc(72 * 1%)` is the 72% mark (the ring idiom).
void test_gradient_calc_stop_position() {
    Gradient g;
    CHECK(parse_gradient("conic-gradient(rgb(0, 0, 255) calc(72 * 1%), rgba(255, 255, 255, 0.08) 0)", LinearColor::white(), &g));
    CHECK(g.stops.size() == 2);
    CHECK(g.stops[0].has_position && !g.stops[0].is_px && std::fabs(g.stops[0].position - 0.72) < 1e-9);
    Gradient h;
    CHECK(parse_gradient("linear-gradient(red calc(10px + 5px), blue)", LinearColor::white(), &h));
    CHECK(h.stops[0].has_position && h.stops[0].is_px && std::fabs(h.stops[0].position - 15) < 1e-9);
}

void test_gradient_sampling() {
    const LinearColor black = LinearColor::black();
    LayoutContext ctx;
    float c[4];
    {
        Gradient g;
        CHECK(parse_gradient("linear-gradient(to right, red, blue)", black, &g));
        sample_gradient(g, 0.5, 5, 100, 10, ctx, 16, c);
        CHECK(near(c[0], 1, 0.02) && near(c[2], 0, 0.02));
        sample_gradient(g, 99.5, 5, 100, 10, ctx, 16, c);
        CHECK(near(c[0], 0, 0.02) && near(c[2], 1, 0.02));
        // Halfway, interpolated in sRGB: (0.5, 0, 0.5).
        sample_gradient(g, 50, 5, 100, 10, ctx, 16, c);
        CHECK(near(c[0], 0.5, 0.02) && near(c[2], 0.5, 0.02));
    }
    {
        // The default direction is `to bottom`.
        Gradient g;
        CHECK(parse_gradient("linear-gradient(red, blue)", black, &g));
        sample_gradient(g, 50, 0.5, 100, 100, ctx, 16, c);
        CHECK(near(c[0], 1, 0.02));
        sample_gradient(g, 50, 99.5, 100, 100, ctx, 16, c);
        CHECK(near(c[2], 1, 0.02));
    }
    {
        // A fade to transparent interpolates premultiplied: no grey halfway,
        // the colour stays red at half alpha.
        Gradient g;
        CHECK(parse_gradient("linear-gradient(to right, red, transparent)", black, &g));
        sample_gradient(g, 50, 5, 100, 10, ctx, 16, c);
        CHECK(near(c[0], 1, 0.02) && near(c[3], 0.5, 0.02));
    }
    {
        Gradient g;
        CHECK(parse_gradient("radial-gradient(circle, red, blue)", black, &g));
        sample_gradient(g, 50, 50, 100, 100, ctx, 16, c);
        CHECK(near(c[0], 1, 0.02));
        // farthest-corner circle: the corner is exactly the end colour.
        sample_gradient(g, 0, 0, 100, 100, ctx, 16, c);
        CHECK(near(c[2], 1, 0.02));
    }
    {
        Gradient g;
        CHECK(parse_gradient("repeating-linear-gradient(to right, red 0px, blue 10px)", black, &g));
        sample_gradient(g, 5, 5, 100, 10, ctx, 16, c);
        CHECK(near(c[0], 0.5, 0.02));
        sample_gradient(g, 15, 5, 100, 10, ctx, 16, c);
        CHECK(near(c[0], 0.5, 0.02));
        sample_gradient(g, 19.9, 5, 100, 10, ctx, 16, c);
        CHECK(near(c[2], 1, 0.03));
    }
    {
        // Conic: 0 at the top, clockwise; `from 90deg` turns the start to the right.
        Gradient g;
        CHECK(parse_gradient("conic-gradient(red, blue)", black, &g));
        sample_gradient(g, 50, 0.5, 100, 100, ctx, 16, c);
        CHECK(near(c[0], 1, 0.03));
        sample_gradient(g, 0.5, 50, 100, 100, ctx, 16, c);   // three quarters round
        CHECK(near(c[2], 0.75, 0.03));
    }
}

void test_background_rasterize_layers() {
    const LinearColor black = LinearColor::black();
    LayoutContext ctx;
    BackgroundLayer layer;
    CHECK(parse_gradient("linear-gradient(to right, red, blue)", black, &layer.gradient));
    layer.is_gradient = true;
    layer.size_x = "50%";
    layer.repeat_x = false;
    layer.repeat_y = false;
    std::vector<uint8_t> rgba;
    // Four texels across 100px: the tile covers the first two, the base colour
    // shows through the rest.
    rasterize_background({layer}, LinearColor::transparent(), 100, 10, 4, 1, ctx, 16, &rgba);
    CHECK(rgba.size() == 16);
    CHECK(rgba[0] > 180 && rgba[2] < 80 && rgba[3] == 255);   // x = 12.5, a quarter along: mostly red, opaque
    CHECK(rgba[12 + 3] == 0);                   // x = 87.5: outside the tile
    rasterize_background({layer}, black, 100, 10, 4, 1, ctx, 16, &rgba);
    CHECK(rgba[12] == 0 && rgba[12 + 3] == 255);   // black under the layer
    // Repeating the tile fills the row.
    layer.repeat_x = true;
    rasterize_background({layer}, LinearColor::transparent(), 100, 10, 4, 1, ctx, 16, &rgba);
    CHECK(rgba[12 + 3] == 255);
}

void test_paint_gradient_backgrounds_and_canvas() {
    // The body's gradient goes onto the canvas as one textured draw covering
    // the viewport (§14.2) and is not painted again on the body; a plain
    // `background:` colour reaches the box through the shorthand.
    Fixture f;
    CHECK(f.css("html, body { margin: 0; height: 100% }"
                "body { background: linear-gradient(180deg, #000, #fff) }"
                "#d { background: #f00; width: 100px; height: 50px }"
                "#g { background: radial-gradient(circle, red, blue); width: 40px; height: 40px;"
                "     border-radius: 8px }"));
    CHECK(f.layout("<body><div id=d></div><div id=g></div></body>"));
    RecordingBackend backend;
    std::vector<TextureHandle> owned;
    PaintContext paint;
    paint.backend = &backend;
    paint.owned_textures = &owned;
    paint_tree(f.tree, f.root, f.ctx, paint);

    int textured = 0, canvas = 0, red = 0;
    for (const RecordingBackend::Draw& d : backend.draws) {
        if (d.texture != 0) {
            ++textured;
            const Rect r = bounds_of(d.geometry);
            if (near(r.x, 0) && near(r.y, 0) && near(r.width, 1000) && near(r.height, 600)) ++canvas;
            continue;
        }
        if (!d.geometry.vertices.empty() && near(d.geometry.vertices[0].color.r, 1) &&
            near(d.geometry.vertices[0].color.g, 0)) {
            const Rect r = bounds_of(d.geometry);
            if (near(r.width, 100) && near(r.height, 50)) ++red;
        }
    }
    CHECK(textured == 2);
    CHECK(canvas == 1);
    CHECK(red == 1);
    CHECK(owned.size() == 2);
    CHECK(backend.textures.size() == 2);
    // The canvas texture is viewport-sized; the small box's is its own size.
    bool has_canvas_tex = false, has_small_tex = false;
    for (const auto& kv : backend.textures) {
        if (kv.second.x == 1000 && kv.second.y == 600) has_canvas_tex = true;
        if (kv.second.x == 40 && kv.second.y == 40) has_small_tex = true;
    }
    CHECK(has_canvas_tex && has_small_tex);
}

void test_blur_and_padded_rasterize() {
    // A lone opaque texel spreads over its neighbours and keeps its alpha
    // mass; the padded rasterizer leaves the pad transparent and masks the
    // rounded corners.
    std::vector<uint8_t> px(5 * 5 * 4, 0);
    px[(2 * 5 + 2) * 4 + 0] = 255;
    px[(2 * 5 + 2) * 4 + 3] = 255;
    blur_rgba(&px, 5, 5, 1.0);
    CHECK(px[(2 * 5 + 2) * 4 + 3] < 255);
    CHECK(px[(2 * 5 + 1) * 4 + 3] > 0);
    CHECK(px[(2 * 5 + 1) * 4 + 0] > 200);   // colour stays red, not darkened by transparency
    int mass = 0;
    for (int i = 0; i < 25; ++i) mass += px[i * 4 + 3];
    CHECK(mass > 200 && mass < 300);

    LayoutContext ctx;
    std::vector<uint8_t> tex;
    const BorderRadii round = BorderRadii::uniform(50);
    rasterize_background_padded({}, LinearColor::black(), 100, 100, 20, 20, 5, &round, ctx, 16, &tex);
    CHECK(tex.size() == 20 * 20 * 4);
    CHECK(tex[(0 * 20 + 0) * 4 + 3] == 0);          // in the pad
    CHECK(tex[(5 * 20 + 5) * 4 + 3] == 0);          // the box's corner, outside the circle
    CHECK(tex[(10 * 20 + 10) * 4 + 3] == 255);      // its centre
}

void test_paint_transform_rotates_geometry() {
    // A 100x50 box rotated 90deg about its centre paints as a 50x100 mesh
    // around the same centre; a translate(50%, 0) shifts it by half its width.
    Fixture f;
    CHECK(f.css("#r { width: 100px; height: 50px; background: #f00; transform: rotate(90deg) }"
                "#t { width: 100px; height: 50px; background: #0f0; transform: translate(50%, 0) }"));
    CHECK(f.layout("<body><div id=r></div><div id=t></div></body>"));
    RecordingBackend backend;
    PaintContext paint;
    paint.backend = &backend;
    paint_tree(f.tree, f.root, f.ctx, paint);
    bool rotated = false, shifted = false;
    for (const RecordingBackend::Draw& d : backend.draws) {
        if (d.geometry.vertices.empty()) continue;
        const Rect r = bounds_of(d.geometry);
        const LinearColor c = d.geometry.vertices[0].color;
        if (near(c.r, 1) && near(c.g, 0) && near(r.width, 50, 1e-3) && near(r.height, 100, 1e-3) &&
            near(r.x, 25, 1e-3) && near(r.y, -25, 1e-3)) {
            rotated = true;
        }
        if (near(c.g, 1) && near(c.r, 0) && near(r.x, 50, 1e-3) && near(r.y, 50, 1e-3)) shifted = true;
    }
    CHECK(rotated);
    CHECK(shifted);
}

// Runtime/Forms/InputRenderer.cs: the UA drawings on a control's box.
void test_paint_form_control_marks() {
    Fixture f;
    CHECK(f.css("html, body { margin: 0 }"
                "input { display: inline-block; width: 16px; height: 16px; padding: 0; border: 0 }"
                "#r { width: 100px; height: 18px; border: 1px solid #000 }"
                "select { display: inline-block; width: 100px; height: 30px; border: 0 }"
                "#a { accent-color: rgb(255, 0, 0) }"));
    CHECK(f.layout("<body><input id=c type=checkbox checked><input id=u type=checkbox>"
                   "<input id=a type=radio checked><input id=r type=range value=50>"
                   "<select id=s><option>A</option></select></body>"));
    RecordingBackend backend;
    PaintContext paint;
    paint.backend = &backend;
    paint_tree(f.tree, f.root, f.ctx, paint);

    int check = 0, red_dot = 0, thumb = 0, caret = 0;
    for (const RecordingBackend::Draw& d : backend.draws) {
        if (d.geometry.vertices.empty() || d.texture != 0) continue;
        const Rect r = bounds_of(d.geometry);
        const LinearColor c = d.geometry.vertices[0].color;
        // indigo check mark: 16 - 2*2 = 12 square
        if (near(r.width, 12) && near(r.height, 12) && near(c.b, 0.671f)) ++check;
        // radio dot in the author's accent colour: half the 16px box
        if (near(r.width, 8) && near(r.height, 8) && near(c.r, 1) && near(c.g, 0)) ++red_dot;
        // range thumb: content height 16 → a 14px knob
        if (near(r.width, 14) && near(r.height, 14)) ++thumb;
        // select caret: 6x3 grey bar
        if (near(r.width, 6) && near(r.height, 3) && near(c.r, 0.6f)) ++caret;
    }
    CHECK(check == 1);     // the unchecked box draws no mark
    CHECK(red_dot == 1);
    CHECK(thumb == 1);
    CHECK(caret == 1);
}

// clip-path and a rounded overflow:hidden cut the geometry, not just the
// scissor rectangle.
void test_paint_clip_path_and_rounded_overflow() {
    Fixture f;
    CHECK(f.css("html, body { margin: 0 }"
                "#t { width: 100px; height: 100px; background: #f00;"
                "     clip-path: polygon(0 0, 100% 0, 50% 100%) }"
                "#o { width: 100px; height: 100px; overflow: hidden; border-radius: 50px }"
                "#c { width: 100px; height: 100px; background: #00f }"
                "#s { width: 100px; height: 100px; overflow: hidden }"
                "#d { width: 100px; height: 100px; background: #0f0 }"));
    CHECK(f.layout("<body><div id=t></div><div id=o><div id=c></div></div>"
                   "<div id=s><div id=d></div></div></body>"));
    RecordingBackend backend;
    PaintContext paint;
    paint.backend = &backend;
    paint_tree(f.tree, f.root, f.ctx, paint);

    bool red = false, blue = false, green = false;
    for (const RecordingBackend::Draw& d : backend.draws) {
        if (d.geometry.vertices.empty()) continue;
        const LinearColor c = d.geometry.vertices[0].color;
        double area = 0;
        for (size_t i = 0; i + 2 < d.geometry.indices.size(); i += 3) {
            const auto& p = d.geometry.vertices[d.geometry.indices[i]].position;
            const auto& q = d.geometry.vertices[d.geometry.indices[i + 1]].position;
            const auto& r = d.geometry.vertices[d.geometry.indices[i + 2]].position;
            area += std::fabs((q.x - p.x) * (r.y - p.y) - (q.y - p.y) * (r.x - p.x)) * 0.5;
        }
        if (near(c.r, 1) && near(c.g, 0) && near(c.b, 0)) {
            red = true;
            CHECK(std::fabs(area - 5000) < 1e-3);   // the triangle is half the box
        } else if (near(c.b, 1) && near(c.r, 0)) {
            blue = true;
            // A circle of radius 50: pi * 2500, within the polygon approximation.
            CHECK(area < 7900 && area > 7700);
            for (const Vertex& v : d.geometry.vertices) {
                CHECK(!(near(v.position.x, 0) && near(v.position.y, 100)));   // no corner survives
            }
        } else if (near(c.g, 1) && near(c.r, 0)) {
            green = true;
            CHECK(std::fabs(area - 10000) < 1e-3);   // square overflow: untouched geometry
        }
    }
    CHECK(red && blue && green);
}

void test_font_weight_resolution() {
    // CSS Fonts L4 §2.2: keywords and numbers; bolder / lighter against the
    // 400 base; italic and oblique both count as italic.
    Fixture f;
    CHECK(f.css("#a { font-weight: bold } #b { font-weight: 600 } #c { font-weight: lighter }"
                "#d { font-style: italic } #e { font-style: oblique 10deg } #n { }"));
    CHECK(f.layout("<body><p id=a></p><p id=b></p><p id=c></p><p id=d></p><p id=e></p><p id=n></p></body>"));
    const auto style = [&](std::string_view id) { return f.tree[f.find(id)].style; };
    CHECK(resolve_font_weight(style("a")) == 700);
    CHECK(resolve_font_weight(style("b")) == 600);
    CHECK(resolve_font_weight(style("c")) == 300);
    CHECK(resolve_font_weight(style("n")) == 400);
    CHECK(resolve_font_italic(style("d")));
    CHECK(resolve_font_italic(style("e")));
    CHECK(!resolve_font_italic(style("n")));
}
