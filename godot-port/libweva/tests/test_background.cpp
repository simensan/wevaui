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
    std::map<uint64_t, std::vector<uint8_t>> texture_bytes;
    TextureHandle generate_texture(const std::vector<uint8_t>& rgba, Vec2i size) override {
        textures[next] = size;
        texture_bytes[next] = rgba;
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

// The bounds of the SHAPE a mesh describes, discounting the coverage ramp an
// antialiased fill carries around it.
//
// A curved fill is emitted as the outline inset half a pixel at full alpha plus
// the same outline expanded half a pixel at zero, so neither ring is the shape:
// it lies exactly between them. Measuring the raw vertices would report every
// rounded box a pixel bigger than it is and turn "a 12px check mark" into 13.
Rect shape_bounds(const RecordingBackend::Geometry& g) {
    bool ramped = false;
    for (const Vertex& v : g.vertices) {
        if (v.color.a == 0.0f) ramped = true;
    }
    if (!ramped) return bounds_of(g);
    double x0 = 1e300, y0 = 1e300, x1 = -1e300, y1 = -1e300;
    for (const Vertex& v : g.vertices) {
        if (v.color.a == 0.0f) continue;
        x0 = std::min<double>(x0, v.position.x);
        y0 = std::min<double>(y0, v.position.y);
        x1 = std::max<double>(x1, v.position.x);
        y1 = std::max<double>(y1, v.position.y);
    }
    // The solid ring is the inset one, so the shape is half a pixel outside it
    // -- to within the arc approximation. On a curve the inset runs along the
    // mitre, which is 0.5/cos(half a segment) long rather than 0.5, so this
    // recovers the shape to a few thousandths of a pixel and not exactly.
    return Rect(x0 - 0.5, y0 - 0.5, (x1 - x0) + 1.0, (y1 - y0) + 1.0);
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

    int check = 0, tick = 0, red_dot = 0, thumb = 0, caret = 0;
    for (const RecordingBackend::Draw& d : backend.draws) {
        if (d.geometry.vertices.empty() || d.texture != 0) continue;
        // The marks are rounded, so they carry a coverage ramp; measure the
        // shape rather than the ramp, or every size below reads a pixel large.
        const Rect r = shape_bounds(d.geometry);
        const LinearColor c = d.geometry.vertices[0].color;
        // Sizes to a hundredth of a pixel, which is all shape_bounds can
        // recover through the arc approximation; the marks are all round.
        const double e = 0.01;
        // The checked box: the accent colour edge to edge, as Chrome fills it,
        // with the tick drawn over it in a contrasting colour.
        if (near(r.width, 16, e) && near(r.height, 16, e) && near(c.b, 0.671f)) ++check;
        // The tick itself: two angled strokes, lighter than the box under
        // them, and ONE draw -- both strokes go into the same mesh, or a
        // checkbox would cost two draws for the sake of a corner.
        if (r.width < 12 && r.height < 12 && c.r > 0.8f && c.g > 0.8f && c.b > 0.8f) ++tick;
        // radio dot in the author's accent colour: half the 16px box
        if (near(r.width, 8, e) && near(r.height, 8, e) && near(c.r, 1) && near(c.g, 0)) ++red_dot;
        // range thumb: content height 16 → a 14px knob
        if (near(r.width, 14, e) && near(r.height, 14, e)) ++thumb;
        // select caret: 6x3 grey bar
        if (near(r.width, 6, e) && near(r.height, 3, e) && near(c.r, 0.6f)) ++caret;
    }
    CHECK(check == 1);     // the unchecked box draws no mark
    CHECK(tick == 1);      // both strokes, in one mesh
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

// A transformed descendant is clipped where it lands on screen, not where
// it was laid out (level-select's rotated roads inside a round map).
void test_paint_clip_follows_descendant_transform() {
    Fixture f;
    CHECK(f.css("html, body { margin: 0 }"
                "#p { width: 100px; height: 100px; clip-path: circle(50px at 50px 50px) }"
                "#c { width: 100px; height: 100px; background: #00f; transform: translate(50px, 0) }"));
    CHECK(f.layout("<body><div id=p><div id=c></div></div></body>"));
    RecordingBackend backend;
    PaintContext paint;
    paint.backend = &backend;
    paint_tree(f.tree, f.root, f.ctx, paint);
    bool blue = false;
    for (const RecordingBackend::Draw& d : backend.draws) {
        if (d.geometry.vertices.empty()) continue;
        const LinearColor c = d.geometry.vertices[0].color;
        if (!(near(c.b, 1) && near(c.r, 0))) continue;
        blue = true;
        double area = 0;
        double minx = 1e9, maxx = -1e9;
        for (size_t i = 0; i + 2 < d.geometry.indices.size(); i += 3) {
            const auto& p = d.geometry.vertices[d.geometry.indices[i]].position;
            const auto& q = d.geometry.vertices[d.geometry.indices[i + 1]].position;
            const auto& r = d.geometry.vertices[d.geometry.indices[i + 2]].position;
            area += std::fabs((q.x - p.x) * (r.y - p.y) - (q.y - p.y) * (r.x - p.x)) * 0.5;
        }
        for (const Vertex& v : d.geometry.vertices) { minx = std::min<double>(minx, v.position.x); maxx = std::max<double>(maxx, v.position.x); }
        // The translated box covers x in [50, 150]; only the circle's right
        // half survives: pi * 50^2 / 2, and nothing left of x = 50.
        CHECK(area > 3750 && area < 4050);
        CHECK(minx > 49.9 && maxx < 100.1);
    }
    CHECK(blue);
}

// CSS 2.1 Appendix E: positioned children paint after in-flow ones, and
// z-index orders stacking contexts regardless of tree order.
void test_paint_stacking_order() {
    Fixture f;
    CHECK(f.css("html, body { margin: 0 }"
                "#p { position: relative; width: 200px; height: 100px }"
                "#red { position: absolute; left: 0; top: 0; width: 50px; height: 50px; background: #f00 }"
                "#grey { width: 50px; height: 50px; background: #808080 }"
                "#blue { position: absolute; z-index: 1; left: 0; top: 0; width: 50px; height: 50px; background: #00f }"
                "#green { position: absolute; left: 0; top: 0; width: 50px; height: 50px; background: #0f0 }"
                "#neg { position: absolute; z-index: -1; left: 0; top: 0; width: 50px; height: 50px; background: #ff0 }"));
    CHECK(f.layout("<body><div id=p><div id=red></div><div id=grey></div><div id=blue></div>"
                   "<div id=green></div><div id=neg></div></div></body>"));
    RecordingBackend backend;
    PaintContext paint;
    paint.backend = &backend;
    paint_tree(f.tree, f.root, f.ctx, paint);
    std::string order;
    for (const RecordingBackend::Draw& d : backend.draws) {
        if (d.geometry.vertices.empty()) continue;
        const LinearColor c = d.geometry.vertices[0].color;
        if (near(c.r, 1) && near(c.g, 0) && near(c.b, 0)) order += 'R';
        else if (near(c.g, 1) && near(c.r, 0) && near(c.b, 0)) order += 'G';
        else if (near(c.b, 1) && near(c.r, 0) && near(c.g, 0)) order += 'B';
        else if (near(c.r, 1) && near(c.g, 1) && near(c.b, 0)) order += 'Y';
        else if (c.r > 0.2f && c.r < 0.3f && near(c.g, c.r) && near(c.b, c.r)) order += 'g';
    }
    // yellow (z -1), grey (in flow), red then green (positioned, tree order), blue (z 1)
    CHECK_EQ(order, std::string("YgRGB"));
}

// A colour glyph keeps its texels in the atlas and is drawn white.
namespace {
struct ColorStubFont : FontInterface {
    FaceHandle load_face(const std::vector<uint8_t>&, int) override { return FaceHandle{1}; }
    bool face_metrics(FaceHandle, double px, FaceMetrics* out) override {
        out->ascent = px * 0.8; out->descent = px * 0.2; out->units_per_em = px; return true;
    }
    bool glyph_index(FaceHandle, uint32_t cp, uint32_t* out) override { *out = cp; return true; }
    bool glyph_metrics(FaceHandle, uint32_t, double px, GlyphMetrics* out) override {
        out->advance = px; out->bearing_x = 0; out->bearing_y = px; out->width = 2; out->height = 2; return true;
    }
    bool rasterize(FaceHandle, uint32_t glyph, double, RenderMode, Bitmap* out) override {
        out->width = out->height = 2;
        out->data = {255, 255, 255, 255};
        if (glyph == 7) {
            out->is_color = true;
            out->rgba = {255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 10, 20, 30, 128};
        }
        return true;
    }
    void shape(FaceHandle, std::string_view utf8, double px, std::vector<ShapedGlyph>* out) override {
        out->clear();
        for (unsigned char c : utf8) { ShapedGlyph g; g.glyph = c; g.x_advance = px; out->push_back(g); }
    }
};
} // namespace

void test_color_glyphs() {
    ColorStubFont font;
    GlyphAtlas atlas(64, 64);
    const GlyphSlot* mono = atlas.get(&font, FaceHandle{1}, 65, 16);
    const GlyphSlot* color = atlas.get(&font, FaceHandle{1}, 7, 16);
    CHECK(mono && !mono->is_color);
    CHECK(color && color->is_color);
    RecordingBackend backend;
    const TextureHandle t = atlas.texture(&backend);
    const std::vector<uint8_t>& px = backend.texture_bytes[t.id];
    const auto at = [&](const GlyphSlot* s, int x, int y, int c) {
        return px[((static_cast<size_t>(s->y + y)) * 64 + s->x + x) * 4 + c];
    };
    CHECK(at(mono, 0, 0, 0) == 255 && at(mono, 0, 0, 3) == 255);
    CHECK(at(color, 0, 0, 0) == 255 && at(color, 0, 0, 1) == 0);      // red texel kept
    CHECK(at(color, 1, 1, 0) == 10 && at(color, 1, 1, 3) == 128);
    // Paint draws the colour glyph's quad white with the text's alpha, and a
    // coverage glyph in the text colour.
    PaintContext paint;
    paint.font = &font;
    paint.atlas = &atlas;
    paint.face = FaceHandle{1};
    Mesh mesh;
    const std::string text = std::string("A") + static_cast<char>(7);
    build_text_geometry(text, 0, 16, 16, LinearColor(1, 0, 0, 0.5f), paint, &mesh, 0, nullptr);
    CHECK(mesh.vertices.size() == 8);
    CHECK(near(mesh.vertices[0].color.r, 1) && near(mesh.vertices[0].color.g, 0));
    CHECK(near(mesh.vertices[4].color.r, 1) && near(mesh.vertices[4].color.g, 1) &&
          near(mesh.vertices[4].color.b, 1) && near(mesh.vertices[4].color.a, 0.5f));
}

// Filter Effects §8: brightness / grayscale rewrite the colours painted under
// the box; drop-shadow paints an outer shadow.
void test_paint_color_filters() {
    Fixture f;
    CHECK(f.css("html, body { margin: 0 }"
                "#dim { width: 40px; height: 40px; background: #ff0000; filter: brightness(0.5) }"
                "#grey { width: 40px; height: 40px; background: #ff0000; filter: grayscale(1) }"
                "#nest { filter: brightness(0.5) } #nest div { width: 40px; height: 40px;"
                "        background: #ffffff; filter: brightness(0.5) }"
                "#ds { width: 40px; height: 40px; background: #0000ff;"
                "      filter: drop-shadow(0 4px 6px rgba(0, 255, 0, 0.5)) }"));
    CHECK(f.layout("<body><div id=dim></div><div id=grey></div><div id=nest><div></div></div>"
                   "<div id=ds></div></body>"));
    RecordingBackend backend;
    PaintContext paint;
    paint.backend = &backend;
    paint_tree(f.tree, f.root, f.ctx, paint);
    int dim = 0, grey = 0, quarter = 0, green_shadow = 0;
    const auto s2l = [](float v) { return v <= 0.04045f ? v / 12.92f : std::pow((v + 0.055f) / 1.055f, 2.4f); };
    const float half = s2l(0.5f);            // brightness(0.5) on #f00, in sRGB
    const float luma = s2l(0.2126f);         // grayscale(1) on #f00
    const float q = s2l(0.25f);              // two nested brightness(0.5)
    for (const RecordingBackend::Draw& d : backend.draws) {
        if (d.geometry.vertices.empty()) continue;
        const LinearColor c = d.geometry.vertices[0].color;
        if (std::fabs(c.r - half) < 0.01f && c.g == 0 && c.b == 0) ++dim;
        if (std::fabs(c.r - luma) < 0.01f && std::fabs(c.g - luma) < 0.01f && std::fabs(c.b - luma) < 0.01f) ++grey;
        if (std::fabs(c.r - q) < 0.01f && std::fabs(c.g - q) < 0.01f && std::fabs(c.b - q) < 0.01f) ++quarter;
        (void)c;
    }
    // The drop-shadow's colour used to ride on the vertices, one ring at a
    // time. It is a blurred texture now, so it is looked for where it went.
    for (const auto& kv : backend.texture_bytes) {
        const std::vector<uint8_t>& px = kv.second;
        for (size_t i = 0; i + 3 < px.size(); i += 4) {
            if (px[i + 3] == 0) continue;
            if (px[i] == 0 && px[i + 1] > 200 && px[i + 2] == 0) { ++green_shadow; break; }
        }
    }
    CHECK(dim == 1);
    CHECK(grey == 1);
    CHECK(quarter == 1);
    CHECK(green_shadow >= 1);
}

// A blurred box-shadow is ONE draw, and its falloff is a real Gaussian.
//
// It used to be a stack of up to 48 nested rings, and the test here counted
// them: enough steps that the banding did not show. There are no steps now --
// the shape is rasterized once, blurred, and drawn as a single textured quad --
// so what is checked is the property the ring count was standing in for. The
// alpha must fall away smoothly, through many distinct levels rather than a
// dozen, and it must be knocked out under the box, which is the one thing the
// ring path got for free by never drawing there.
void test_box_shadow_blur_falloff() {
    // The knockout rests on this, and a wrong answer here is silent -- the
    // shadow simply keeps its interior and washes across the box it belongs to.
    //
    // The last case is the one that actually went wrong. A radius LARGER than
    // the box is ordinary CSS (`border-radius: 999px` is how a pill is
    // written), and the corner test measures against an ellipse that big, so
    // the box's own centre reads as OUTSIDE it. Whoever calls this has to clamp
    // the radii to the box first, which is what the shadow now does.
    BorderRadii r20{};
    r20.top_left = r20.top_right = r20.bottom_right = r20.bottom_left = CornerRadius{20, 20};
    BorderRadii pill{};
    pill.top_left = pill.top_right = pill.bottom_right = pill.bottom_left = CornerRadius{999, 999};
    CHECK(near(rounded_rect_coverage(50, 20, 100, 40, nullptr), 1.0));
    CHECK(near(rounded_rect_coverage(50, 20, 100, 40, &r20), 1.0));
    CHECK(near(rounded_rect_coverage(-5, 20, 100, 40, nullptr), 0.0));
    CHECK(rounded_rect_coverage(50, 20, 100, 40, &pill) < 0.01);
    const BorderRadii clamped = clamp_radii_to_rect(pill, 100, 40);
    CHECK(near(rounded_rect_coverage(50, 20, 100, 40, &clamped), 1.0));

    Fixture f;
    CHECK(f.css("html, body { margin: 0 } #b { width: 100px; height: 40px; background: #fff;"
                " box-shadow: 0 8px 24px rgba(0, 0, 0, 0.5) }"));
    CHECK(f.layout("<body><div id=b></div></body>"));
    RecordingBackend backend;
    PaintContext paint;
    paint.backend = &backend;
    paint_tree(f.tree, f.root, f.ctx, paint);

    // One textured draw for the shadow, not a stack of translucent rings.
    int rings = 0;
    for (const RecordingBackend::Draw& d : backend.draws) {
        if (d.geometry.vertices.empty() || d.texture != 0) continue;
        const LinearColor c = d.geometry.vertices[0].color;
        if (c.r == 0 && c.g == 0 && c.b == 0 && c.a > 0 && c.a < 1) ++rings;
    }
    CHECK(rings == 0);

    // The shadow's texture: the one whose texels are black with a soft alpha.
    const std::vector<uint8_t>* shadow = nullptr;
    Vec2i size{0, 0};
    for (const auto& kv : backend.texture_bytes) {
        const std::vector<uint8_t>& px = kv.second;
        int soft = 0;
        for (size_t i = 0; i + 3 < px.size(); i += 4) {
            if (px[i] == 0 && px[i + 1] == 0 && px[i + 2] == 0 && px[i + 3] > 0 &&
                px[i + 3] < 255) {
                if (++soft > 64) break;
            }
        }
        if (soft > 64) {
            shadow = &px;
            size = backend.textures[kv.first];
        }
    }
    CHECK(shadow != nullptr);
    if (!shadow) return;

    // Down the middle: alpha rises into the shadow and is punched back to
    // nothing where the box sits over it.
    const int cx = size.x / 2;
    std::vector<int> column;
    for (int y = 0; y < size.y; ++y) {
        column.push_back((*shadow)[(static_cast<size_t>(y) * size.x + cx) * 4 + 3]);
    }
    int distinct = 0;
    for (size_t i = 1; i < column.size(); ++i) {
        if (column[i] != column[i - 1]) ++distinct;
    }
    // Twelve rings gave twelve steps down a column. A Gaussian gives a new
    // value at nearly every texel of its falloff.
    CHECK(distinct > 30);
    CHECK(column.front() == 0);          // outside the blur entirely
    CHECK(*std::max_element(column.begin(), column.end()) > 40);

    // The knockout: the box's own area takes none of the shadow. The box is
    // 100x40 and the shadow is offset 8px down, so a little above the middle
    // of the texture is inside it.
    const int knock_y = size.y / 2 - 4;
    if (knock_y > 0 && knock_y < size.y) {
        CHECK((*shadow)[(static_cast<size_t>(knock_y) * size.x + cx) * 4 + 3] == 0);
    }

    // The falloff is the one the spec names: a Gaussian of sigma = blur/2,
    // so the alpha outside a straight edge is A * 0.5 * erfc(d / (sigma * root
    // 2)). The ring path evaluated exactly that expression to pick each ring's
    // alpha; the texture has to land on the same curve, or the shadow is the
    // right shape and the wrong weight.
    {
        Fixture e;
        CHECK(e.css("html, body { margin: 0 } #b { width: 200px; height: 80px; background: #fff;"
                    " box-shadow: 0 0 20px rgba(0, 0, 0, 1) }"));
        CHECK(e.layout("<body><div id=b></div></body>"));
        RecordingBackend eb;
        PaintContext ep;
        ep.backend = &eb;
        paint_tree(e.tree, e.root, e.ctx, ep);
        const std::vector<uint8_t>* tex = nullptr;
        Vec2i tsize{0, 0};
        for (const auto& kv : eb.texture_bytes) {
            int soft = 0;
            for (size_t i = 3; i < kv.second.size(); i += 4) {
                if (kv.second[i] > 0 && kv.second[i] < 255 && ++soft > 64) break;
            }
            if (soft > 64) { tex = &kv.second; tsize = eb.textures[kv.first]; }
        }
        CHECK(tex != nullptr);
        if (tex) {
            const double sigma = 10.0;                  // blur 20 / 2
            const int pad = static_cast<int>(std::ceil(3 * sigma));
            const int cx = tsize.x / 2;
            // Straight down from the box's bottom edge, which sits `pad` texels
            // from the bottom of the texture.
            const int edge = tsize.y - pad;
            int worst = 0;
            for (int d = 2; d < static_cast<int>(3 * sigma) - 2; ++d) {
                const int y = edge + d;
                if (y < 0 || y >= tsize.y) break;
                const int got = (*tex)[(static_cast<size_t>(y) * tsize.x + cx) * 4 + 3];
                const double want =
                    255.0 * 0.5 * std::erfc(d / (sigma * 1.4142135623730951));
                const int err = static_cast<int>(std::fabs(got - want));
                if (err > worst) worst = err;
            }
            if (worst > 12) std::printf("  shadow falloff off by %d of 255\n", worst);
            CHECK(worst <= 12);
        }
    }

    // A spread with NO blur must still draw. That path keeps the ring, and it
    // regressed once: the layer loop evaluated edge coverage at exactly e == 0
    // and a strict `e < 0` test called it uncovered, so `0 0 0 20px` drew
    // nothing. Chrome renders a hard ring, 115/255 over white.
    Fixture g;
    CHECK(g.css("html, body { margin: 0 } #b { width: 100px; height: 40px; background: #fff;"
                " box-shadow: 0 0 0 20px rgba(0, 0, 0, 0.55) }"));
    CHECK(g.layout("<body><div id=b></div></body>"));
    RecordingBackend gb;
    PaintContext gp;
    gp.backend = &gb;
    paint_tree(g.tree, g.root, g.ctx, gp);
    int spread_only = 0;
    for (const RecordingBackend::Draw& d : gb.draws) {
        if (d.geometry.vertices.empty() || d.texture != 0) continue;
        const LinearColor c = d.geometry.vertices[0].color;
        if (c.r == 0 && c.g == 0 && c.b == 0 && c.a > 0 && c.a < 1) ++spread_only;
    }
    CHECK(spread_only >= 1);
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

// A border as thick as its own radius. Ordinary CSS, and it used to draw
// NOTHING: tessellate_border zips an outer and an inner outline, the inner
// radius collapses to zero when the width eats it, a zero radius emitted one
// point where a rounded corner emits `segments + 1`, and the mismatched counts
// made the function bail. The same shape is what an outer box-shadow's knockout
// ring asks for, so the two were broken together.
void test_border_as_thick_as_its_radius() {
    const auto border_draws = [](const char* css, const char* html) {
        Fixture f;
        CHECK(f.css(css));
        CHECK(f.layout(html));
        RecordingBackend backend;
        PaintContext paint;
        paint.backend = &backend;
        paint_tree(f.tree, f.root, f.ctx, paint);
        int n = 0;
        for (const RecordingBackend::Draw& d : backend.draws) {
            if (d.geometry.vertices.empty() || d.texture != 0) continue;
            const LinearColor c = d.geometry.vertices[0].color;
            // The border is the opaque red one.
            if (c.r > 0.4f && c.g == 0 && c.b == 0 && c.a == 1) ++n;
        }
        return n;
    };
    CHECK(border_draws(
              "html, body { margin: 0 } #b { width: 200px; height: 120px;"
              " border-radius: 20px; border: 20px solid #c00 }",
              "<body><div id=b></div></body>") >= 1);
    // And the ordinary case, where the radius is larger than the border, keeps
    // working — that one always zipped cleanly.
    CHECK(border_draws(
              "html, body { margin: 0 } #b { width: 200px; height: 120px;"
              " border-radius: 40px; border: 10px solid #c00 }",
              "<body><div id=b></div></body>") >= 1);
}
