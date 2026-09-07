// CSS Backgrounds L3 / Images L3: the background shorthand, gradient parsing
// and sampling, layer rasterization, and the painted result (one textured
// draw per gradient box; the body's background on the canvas).
#include "check.h"
#include "weva_c.h"
#include <algorithm>
#include <array>
#include "weva/background.h"
#include "weva/border_image.h"
#include "weva/image_store.h"
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
#include <cstring>
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


// ---- background-image ---------------------------------------------------
//
// A 2x2 image with four unequal colours, so a flip or a transpose in the
// sampler shows up rather than cancelling out:
//     red   green
//     blue  white
const uint8_t k_quad_png[] = {
    137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82,
    0, 0, 0, 2, 0, 0, 0, 2, 8, 6, 0, 0, 0, 114, 182, 13,
    36, 0, 0, 0, 18, 73, 68, 65, 84, 120, 156, 99, 248, 207, 192, 240,
    31, 12, 129, 52, 24, 0, 0, 73, 200, 9, 247, 249, 171, 182, 13, 0,
    0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130,
};

// The store, fed from memory rather than from disk: the test is about
// sampling and sizing, and a fixture on disk would make it about paths too.
weva::ImageStore quad_store() {
    weva::ImageStore store;
    store.set_reader([](const std::string& path, std::vector<uint8_t>* out) {
        if (path != "quad.png") return false;
        out->assign(k_quad_png, k_quad_png + sizeof(k_quad_png));
        return true;
    });
    return store;
}

// The texel at (x, y) of a rasterized background, as RGBA.
struct Texel { int r, g, b, a; };
Texel texel_at(const std::vector<uint8_t>& rgba, int tex_w, int x, int y) {
    const size_t o = (static_cast<size_t>(y) * tex_w + x) * 4;
    return {rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]};
}

} // namespace

void test_replaced_image_cache() {
    ImageStore store;
    const auto reader = [](const std::string&, std::vector<uint8_t>* out) {
        out->assign(k_quad_png, k_quad_png + sizeof(k_quad_png));
        return true;
    };
    store.set_reader(reader);
    TextureCache cache;
    RecordingBackend retained;
    uint64_t previous = 0;
    const auto run = [&](std::string_view css, bool reuse, double viewport = 400,
                         double root_font = 16, double root_line = 19.2, double dpi = 96,
                         std::string_view src = "quad.png") {
        Fixture f;
        f.ctx.images = &store;
        f.ctx.root_font_size_px = root_font;
        f.ctx.root_line_height_px = root_line;
        f.ctx.dpi_pixels_per_inch = dpi;
        CHECK(f.css(std::string("html,body{margin:0}img{display:block;box-sizing:content-box;"
                                "width:40px;height:30px}") + std::string(css)));
        const std::string html = "<body><img src='" + std::string(src) + "'><img src='" +
                                  std::string(src) + "'></body>";
        CHECK(f.layout(html,viewport,300));
        retained.draws.clear();
        PaintContext p; p.images=&store; p.backend=&retained; p.texture_cache=&cache;
        cache.begin_pass();
        paint_tree(f.tree,f.root,f.ctx,p);
        cache.end_pass(&retained);
        RecordingBackend fresh;
        p.backend=&fresh; p.texture_cache=nullptr;
        paint_tree(f.tree,f.root,f.ctx,p);
        CHECK(retained.draws.size() == fresh.draws.size());
        size_t image_draws = 0;
        uint64_t image_texture = 0;
        for (size_t i=0; i<std::min(retained.draws.size(),fresh.draws.size()); ++i) {
            const auto& a=retained.draws[i]; const auto& b=fresh.draws[i];
            CHECK(a.geometry.indices == b.geometry.indices);
            CHECK(a.geometry.vertices.size() == b.geometry.vertices.size());
            for (size_t v=0; v<std::min(a.geometry.vertices.size(),b.geometry.vertices.size()); ++v) {
                const Vertex& av=a.geometry.vertices[v]; const Vertex& bv=b.geometry.vertices[v];
                CHECK(av.position.x==bv.position.x && av.position.y==bv.position.y &&
                      av.tex_coord.x==bv.tex_coord.x && av.tex_coord.y==bv.tex_coord.y &&
                      av.color.r==bv.color.r && av.color.g==bv.color.g &&
                      av.color.b==bv.color.b && av.color.a==bv.color.a);
            }
            CHECK((a.texture != 0) == (b.texture != 0));
            if (a.texture) {
                CHECK(retained.texture_bytes[a.texture] == fresh.texture_bytes[b.texture]);
                CHECK(retained.textures[a.texture].x == fresh.textures[b.texture].x);
                CHECK(retained.textures[a.texture].y == fresh.textures[b.texture].y);
                if (image_texture) CHECK(image_texture == a.texture); // identical images share
                image_texture = a.texture;
                ++image_draws;
            }
        }
        CHECK(image_draws == 2);
        CHECK(image_texture != 0);
        if (previous) CHECK((image_texture == previous) == reuse);
        previous = image_texture;
        CHECK(cache.size() == 1 && retained.textures.size() == 1);
    };
    run("",false);
    run("",true);
    run("img{opacity:.5;transform:translateX(7px);border-radius:8px}",true);
    run("img{padding:3px;border:2px solid red}",true);
    for (const char* css : {"img{object-fit:contain}","img{object-fit:cover}",
            "img{object-fit:none}","img{object-fit:scale-down}",
            "img{object-fit:none;object-position:0% 100%}",
            "img{object-fit:none;object-position:1em 1rem}",
            "img{object-fit:none;object-position:1em 1rem;font-size:20px}",
            "img{width:40.0001px}","img{width:40.0002px}",
            "img{height:31px}","img{filter:brightness(.7)}",
            "body{filter:brightness(.8)}img{filter:opacity(.6)}"}) {
        run(css,false); run(css,true);
    }
    const char* units="img{object-fit:none;object-position:calc(1vw + 1rem) calc(1rlh + 1in)}";
    run(units,false);
    run(units,false,420);
    run(units,false,420,18);
    run(units,false,420,18,22);
    run(units,false,420,18,22,120);
    run(units,false,420,18,22,120,"other.png");
    run(units,true,420,18,22,120,"other.png");
    run("",false);
    store.clear(); run("",false);
    store.set_base_path("different"); run("",false);
    store.set_base_path("different"); run("",true);
    store.set_reader(reader); run("",false);
    store=ImageStore{}; store.set_reader(reader); run("",false);
    const uint8_t alternative_png[] = {
        137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82,0,0,0,2,0,0,0,1,8,6,0,0,0,
        244,34,127,138,0,0,0,17,73,68,65,84,120,156,99,248,223,192,240,159,225,255,
        255,6,0,21,247,4,253,191,252,42,226,0,0,0,0,73,69,78,68,174,66,96,130
    };
    const auto original_pixels=retained.texture_bytes[previous];
    store.set_reader([&](const std::string&,std::vector<uint8_t>* out) {
        out->assign(alternative_png,alternative_png+sizeof(alternative_png)); return true;
    });
    run("",false);
    CHECK(retained.texture_bytes[previous] != original_pixels);
    // Cached failures do not keep the old texture alive. Reappearance has to
    // produce a new handle; the cache must also release everything when unused.
    store.set_reader([](const std::string&,std::vector<uint8_t>*) { return false; });
    Fixture missing; missing.ctx.images=&store;
    CHECK(missing.layout("<body><img src='quad.png' style='width:40px;height:30px'></body>"));
    PaintContext p; p.images=&store; p.backend=&retained; p.texture_cache=&cache;
    cache.begin_pass(); retained.draws.clear();
    paint_tree(missing.tree,missing.root,missing.ctx,p);
    cache.end_pass(&retained);
    CHECK(cache.size()==0 && retained.textures.empty());
    store.set_reader(reader); run("",false);
    cache.release_all(&retained);
    CHECK(retained.textures.empty());

    // Public resource resets must schedule an update even on an idle document.
    weva_config cfg{}; cfg.viewport_width=100; cfg.viewport_height=100; cfg.use_user_agent_stylesheet=1;
    const auto doc=weva_document_create(&cfg);
    struct Asset { const uint8_t* bytes; size_t size; } other{alternative_png,sizeof(alternative_png)};
    const auto asset_reader=[](void* user,const char* path,uint8_t* out,size_t capacity)->size_t {
        if (std::strstr(path,"missing")) return 0;
        const auto& alternate=*static_cast<Asset*>(user);
        const bool changed=std::strstr(path,"other")!=nullptr;
        const auto* bytes=changed ? alternate.bytes : k_quad_png;
        const size_t size=changed ? alternate.size : sizeof(k_quad_png);
        if (out && capacity>=size) std::memcpy(out,bytes,size);
        return size;
    };
    weva_document_set_asset_reader(doc,asset_reader,&other);
    const char* html="<body><img src='quad.png' style='display:block;width:40px;height:30px'></body>";
    CHECK(weva_document_load_html(doc,html,std::strlen(html))==WEVA_OK);
    CHECK(weva_document_update(doc,0)==WEVA_OK);
    size_t draw_count=0;
    weva_document_draws(doc,&draw_count);
    CHECK(draw_count>0);
    const auto pixels = [&]() {
        size_t count=0; const auto* draws=weva_document_draws(doc,&count);
        uint64_t texture=0;
        for (size_t i=0;i<count;++i) if (draws[i].texture_id) {texture=draws[i].texture_id;break;}
        const auto* textures=weva_document_textures(doc,&count);
        for (size_t i=0;i<count;++i) if (textures[i].id==texture)
            return std::vector<uint8_t>(textures[i].rgba,textures[i].rgba+textures[i].width*textures[i].height*4);
        return std::vector<uint8_t>();
    };
    const auto first_pixels=pixels();
    CHECK(!first_pixels.empty());
    const auto img=weva_document_query(doc,"img");
    weva_element_set_attribute(doc,img,"src","other.png");
    CHECK(weva_document_update(doc,0)==WEVA_OK);
    CHECK(!pixels().empty() && pixels()!=first_pixels);
    auto serial=weva_document_draw_serial(doc);
    weva_element_set_attribute(doc,img,"src","other.png");
    CHECK(weva_document_update(doc,0)==WEVA_OK);
    CHECK(weva_document_draw_serial(doc)==serial);
    weva_element_set_attribute(doc,img,"src",nullptr);
    CHECK(weva_document_update(doc,0)==WEVA_OK);
    CHECK(pixels().empty());
    weva_element_set_attribute(doc,img,"src","quad.png");
    CHECK(weva_document_update(doc,0)==WEVA_OK);
    CHECK(pixels()==first_pixels);
    serial=weva_document_draw_serial(doc);
    weva_document_set_base_path(doc,"missing");
    CHECK(weva_document_draw_serial(doc)==serial); // publication is deferred
    CHECK(weva_document_update(doc,0)==WEVA_OK);
    CHECK(weva_document_draw_serial(doc)!=serial);
    CHECK(weva_document_missing_assets(doc,nullptr,0)==1);
    weva_document_set_base_path(doc,"present");
    CHECK(weva_document_update(doc,0)==WEVA_OK);
    CHECK(weva_document_missing_assets(doc,nullptr,0)==0);
    serial=weva_document_draw_serial(doc);
    weva_document_set_base_path(doc,"present");
    CHECK(weva_document_update(doc,0)==WEVA_OK);
    CHECK(weva_document_draw_serial(doc)==serial);
    weva_document_set_asset_reader(doc,nullptr,nullptr);
    CHECK(weva_document_update(doc,0)==WEVA_OK);
    CHECK(weva_document_draw_serial(doc)!=serial);
    CHECK(weva_document_missing_assets(doc,nullptr,0)==1);
    weva_document_destroy(doc);

    const auto siblings=weva_document_create(&cfg);
    weva_document_set_asset_reader(siblings,asset_reader,&other);
    const char* sibling_html="<body><img id=first src=quad.png><img id=second src=quad.png></body>";
    const char* sibling_css="img{display:block;width:40px;height:30px}"
        "img:nth-last-child(1 of [src='quad.png']){width:50px}";
    weva_document_add_css(siblings,sibling_css,std::strlen(sibling_css));
    weva_document_load_html(siblings,sibling_html,std::strlen(sibling_html));
    CHECK(weva_document_update(siblings,0)==WEVA_OK);
    const auto first=weva_document_query(siblings,"#first"), second=weva_document_query(siblings,"#second");
    for (const char* source : {"other.png","quad.png",static_cast<const char*>(nullptr)}) {
        weva_element_set_attribute(siblings,second,"src",source);
        CHECK(weva_document_update(siblings,0)==WEVA_OK);
        char width[32]{};
        weva_element_computed_style(siblings,first,"width",width,sizeof(width));
        CHECK(std::strcmp(width,source && std::strcmp(source,"quad.png")==0 ? "40px" : "50px")==0);
    }
    weva_document_destroy(siblings);
}

void test_background_image() {
    weva::ImageStore store = quad_store();
    const weva::DecodedImage* image = store.get("quad.png");
    CHECK(image != nullptr);
    if (!image) return;
    CHECK(image->width == 2 && image->height == 2);

    // A miss is cached as a miss, and answers null rather than reopening.
    CHECK(store.get("missing.png") == nullptr);
    CHECK(store.get("missing.png") == nullptr);

    LayoutContext ctx;
    ctx.viewport_width_px = 400;
    ctx.viewport_height_px = 300;

    // ---- `background-size: cover` on a square box ----------------------
    //
    // The image is square and so is the box, so cover scales it to exactly
    // the box: each of the four texels is one quadrant, and which colour
    // lands in which corner is the whole assertion.
    {
        BackgroundLayer layer;
        layer.is_gradient = false;
        layer.image = image;
        layer.size_x = "cover";
        layer.repeat_x = layer.repeat_y = false;
        std::vector<uint8_t> rgba;
        rasterize_background({layer}, LinearColor{0, 0, 0, 0}, 40, 40, 40, 40, ctx, 16, &rgba);

        const Texel tl = texel_at(rgba, 40, 5, 5);
        const Texel tr = texel_at(rgba, 40, 35, 5);
        const Texel bl = texel_at(rgba, 40, 5, 35);
        const Texel br = texel_at(rgba, 40, 35, 35);
        CHECK(tl.r > 200 && tl.g < 50 && tl.b < 50);                 // red, top left
        CHECK(tr.g > 200 && tr.r < 50 && tr.b < 50);                 // green, top right
        CHECK(bl.b > 200 && bl.r < 50 && bl.g < 50);                 // blue, bottom left
        CHECK(br.r > 200 && br.g > 200 && br.b > 200);               // white, bottom right
    }

    // ---- the intrinsic size, when nothing says otherwise ---------------
    //
    // `auto` means the image's OWN size, so a 2x2 image in a 40x40 box
    // occupies four texels in the corner and leaves the rest transparent --
    // which is exactly what distinguishes it from a gradient, whose `auto` is
    // the painting area.
    {
        BackgroundLayer layer;
        layer.image = image;
        layer.repeat_x = layer.repeat_y = false;
        std::vector<uint8_t> rgba;
        rasterize_background({layer}, LinearColor{0, 0, 0, 0}, 40, 40, 40, 40, ctx, 16, &rgba);
        CHECK(texel_at(rgba, 40, 0, 0).r > 200);        // the image is here
        CHECK(texel_at(rgba, 40, 20, 20).a == 0);       // and nowhere else
    }

    // ---- repeat tiles it ------------------------------------------------
    {
        BackgroundLayer layer;
        layer.image = image;
        layer.repeat_x = layer.repeat_y = true;
        std::vector<uint8_t> rgba;
        rasterize_background({layer}, LinearColor{0, 0, 0, 0}, 40, 40, 40, 40, ctx, 16, &rgba);
        // Every texel is covered, and the pattern repeats every 2px.
        CHECK(texel_at(rgba, 40, 20, 20).a == 255);
        CHECK(texel_at(rgba, 40, 0, 0).r == texel_at(rgba, 40, 2, 2).r);
        CHECK(texel_at(rgba, 40, 1, 0).g == texel_at(rgba, 40, 3, 0).g);
    }

    // ---- one explicit axis sets the other from the aspect ratio ---------
    {
        BackgroundLayer layer;
        layer.image = image;
        layer.size_x = "20px";
        layer.repeat_x = layer.repeat_y = false;
        std::vector<uint8_t> rgba;
        rasterize_background({layer}, LinearColor{0, 0, 0, 0}, 40, 40, 40, 40, ctx, 16, &rgba);
        // Square image, so a 20px width is a 20px height: covered at (19,19),
        // clear just past it.
        CHECK(texel_at(rgba, 40, 19, 19).a == 255);
        CHECK(texel_at(rgba, 40, 21, 21).a == 0);
    }

    // ---- an image layer composites OVER the background colour ----------
    {
        BackgroundLayer layer;
        layer.image = image;
        layer.size_x = "cover";
        layer.repeat_x = layer.repeat_y = false;
        std::vector<uint8_t> rgba;
        // Opaque black underneath; the image is opaque, so it wins everywhere.
        rasterize_background({layer}, LinearColor{0, 0, 0, 1}, 40, 40, 40, 40, ctx, 16, &rgba);
        CHECK(texel_at(rgba, 40, 5, 5).r > 200);
    }

    // ---- a layer whose image failed to load paints nothing --------------
    //
    // The engine drew a flat fill for every image before this existed, and a
    // missing file has to keep doing exactly that rather than a black box.
    {
        BackgroundLayer layer;
        layer.image = nullptr;
        layer.url = "missing.png";
        std::vector<uint8_t> rgba;
        rasterize_background({layer}, LinearColor{0, 0, 0, 0}, 8, 8, 8, 8, ctx, 16, &rgba);
        CHECK(texel_at(rgba, 8, 4, 4).a == 0);
    }

    // ---- the base path ---------------------------------------------------
    weva::ImageStore paths;
    paths.set_base_path("/assets/ui");
    CHECK_EQ(paths.resolve("gem.png"), std::string("/assets/ui/gem.png"));
    CHECK_EQ(paths.resolve("res://icons/gem.png"), std::string("res://icons/gem.png"));
    CHECK_EQ(paths.resolve("/absolute/gem.png"), std::string("/absolute/gem.png"));
    paths.set_base_path("/assets/ui/");
    CHECK_EQ(paths.resolve("gem.png"), std::string("/assets/ui/gem.png"));
}


// ---- <img> as a replaced element ---------------------------------------
//
// A replaced element's `auto` width is its INTRINSIC width, and nothing else
// in layout can supply one -- so before the image store reached layout, every
// <img> laid out at zero and was invisible whatever its src said.
void test_replaced_img() {
    weva::ImageStore store = quad_store();   // the 2x2 red/green/blue/white PNG

    struct Sized { double w, h; };
    const auto lay_out = [&](const char* css, const char* html) -> Sized {
        Fixture f;
        f.ctx.images = &store;
        if (css[0] != 0) f.css(css);
        if (!f.layout(html, 400, 300)) return {-2, -2};
        for (BoxId i = 0; i < static_cast<BoxId>(f.tree.size()); ++i) {
            const Box& b = f.tree[i];
            if (b.element && b.element->tag_name() == "img") return {b.width, b.height};
        }
        return {-1, -1};
    };

    // No width or height: the image's own size.
    {
        const Sized s = lay_out("", "<body><img src='quad.png'></body>");
        CHECK(near(s.w, 2) && near(s.h, 2));
    }

    // A stated width, auto height: the height follows the intrinsic ratio.
    // Every `img { width: 100% }` in every stylesheet relies on this, and it
    // is the case most likely to be got wrong.
    {
        const Sized s = lay_out("img { width: 40px }", "<body><img src='quad.png'></body>");
        CHECK(near(s.w, 40));
        CHECK(near(s.h, 40));   // a square image, so the ratio is 1
    }

    // A stated height, auto width: the ratio supplies the width.
    {
        const Sized s = lay_out("img { height: 30px }", "<body><img src='quad.png'></body>");
        CHECK(near(s.w, 30) && near(s.h, 30));
    }

    // Both stated: neither is touched, ratio or no ratio.
    {
        const Sized s = lay_out("img { width: 50px; height: 10px }",
                                "<body><img src='quad.png'></body>");
        CHECK(near(s.w, 50) && near(s.h, 10));
    }

    // A src that resolves to nothing keeps the old behaviour rather than
    // inventing a size.
    {
        const Sized s = lay_out("", "<body><img src='missing.png'></body>");
        CHECK(near(s.w, 0) && near(s.h, 0));
    }
    {
        const Sized s = lay_out("", "<body><img></body>");
        CHECK(near(s.w, 0) && near(s.h, 0));
    }

    // Replaced content supplies intrinsic contributions even though an img
    // has no DOM children. Flex's content-size probe used to return zero,
    // then its reflow erased the height as well.
    struct FlexImage { const char* css; double width, height; };
    const FlexImage flex_images[] = {
        {"section{display:flex;align-items:center;width:100px}", 2, 2},
        {"section{display:flex;align-items:center;width:100px}img{height:30px}", 30, 30},
        {"section{display:flex;align-items:center;width:100px}img{flex:1}", 100, 100},
        {"section{display:flex;align-items:center;width:100px}img{flex-basis:20px}", 20, 20},
        {"section{display:flex;align-items:center;width:1px}", 2, 2},
        {"section{display:flex;align-items:center;width:1px}img{min-width:0}", 1, 1},
        {"section{display:flex;align-items:center;width:100px}img{padding:3px;border:2px solid}", 12, 12},
        {"section{display:flex;align-items:center;width:100px}img{width:50%}", 50, 50},
        {"section{display:flex;flex-direction:column;align-items:center;width:100px}", 2, 2},
    };
    for (const auto& row : flex_images) {
        const Sized s = lay_out(row.css, "<body><section><img src='quad.png'></section></body>");
        CHECK(near(s.w, row.width));
        CHECK(near(s.h, row.height));
    }
}


// ---- border-image -------------------------------------------------------
//
// A 9x9 source with a different colour in each of its nine 3x3 blocks, so a
// piece drawn from the wrong slice names itself instead of merely looking
// odd. Sliced 3, every piece is one block.
const uint8_t k_nine_png[] = {
    137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82,
    0, 0, 0, 9, 0, 0, 0, 9, 8, 6, 0, 0, 0, 224, 145, 6,
    16, 0, 0, 0, 51, 73, 68, 65, 84, 120, 156, 173, 202, 49, 1, 0,
    48, 8, 3, 193, 23, 134, 48, 36, 226, 138, 178, 164, 32, 32, 195, 45,
    201, 83, 208, 66, 29, 212, 50, 70, 51, 124, 51, 200, 28, 151, 43, 202,
    160, 37, 114, 17, 185, 108, 209, 3, 158, 234, 162, 244, 35, 248, 215, 209,
    0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130,
};

namespace {

weva::DecodedImage nine() { return weva::decode_png(k_nine_png, sizeof(k_nine_png)); }

// The colour at a texel of a rasterized border image.
struct Px { int r, g, b, a; };
Px at(const std::vector<uint8_t>& rgba, int w, int x, int y) {
    const size_t o = (static_cast<size_t>(y) * w + x) * 4;
    return {rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]};
}
bool is(const Px& p, int r, int g, int b) {
    return p.a == 255 && p.r == r && p.g == g && p.b == b;
}

} // namespace

void test_border_image() {
    const weva::DecodedImage image = nine();
    CHECK(image.valid());
    CHECK(image.width == 9 && image.height == 9);
    if (!image.valid()) return;

    // The nine pieces, into a 60x40 box with 12px borders. Each corner must
    // carry its own block's colour, each edge the block between them, and the
    // centre nothing at all without `fill`.
    BorderImage bi;
    bi.image = &image;
    bi.slice = {3, 3, 3, 3, false};
    bi.width = {12, 12, 12, 12};
    std::vector<uint8_t> rgba;
    rasterize_border_image(bi, 60, 40, 60, 40, &rgba);

    CHECK(is(at(rgba, 60, 5, 5), 200, 0, 0));       // top-left
    CHECK(is(at(rgba, 60, 30, 5), 0, 200, 0));      // top edge
    CHECK(is(at(rgba, 60, 54, 5), 0, 0, 200));      // top-right
    CHECK(is(at(rgba, 60, 5, 20), 200, 200, 0));    // left edge
    CHECK(is(at(rgba, 60, 54, 20), 0, 200, 200));   // right edge
    CHECK(is(at(rgba, 60, 5, 35), 120, 60, 0));     // bottom-left
    CHECK(is(at(rgba, 60, 30, 35), 60, 120, 0));    // bottom edge
    CHECK(is(at(rgba, 60, 54, 35), 0, 60, 120));    // bottom-right

    // The middle is left alone: a frame sits OVER whatever the box already
    // has, which is the whole point of not filling by default.
    CHECK(at(rgba, 60, 30, 20).a == 0);

    // With `fill`, the centre block is painted.
    bi.slice.fill = true;
    rasterize_border_image(bi, 60, 40, 60, 40, &rgba);
    CHECK(is(at(rgba, 60, 30, 20), 200, 0, 200));   // the centre block

    // A slice bigger than the source is clamped so opposing pairs cannot
    // overlap -- otherwise the two corners would read past each other.
    BorderImage big = bi;
    big.slice = {90, 90, 90, 90, false};
    rasterize_border_image(big, 60, 40, 60, 40, &rgba);
    CHECK(is(at(rgba, 60, 5, 5), 200, 0, 0));       // still the top-left block

    // Widths that would overlap are clamped the same way, and the result must
    // still be drawn rather than abandoned.
    BorderImage fat = bi;
    fat.width = {100, 100, 100, 100};
    rasterize_border_image(fat, 60, 40, 60, 40, &rgba);
    CHECK(at(rgba, 60, 5, 5).a == 255);

    // A repeating edge tiles the source at its natural size instead of
    // stretching it, so the same colour recurs along the run.
    BorderImage tiled = bi;
    tiled.repeat_x = BorderImageRepeat::Repeat;
    rasterize_border_image(tiled, 60, 40, 60, 40, &rgba);
    CHECK(is(at(rgba, 60, 30, 5), 0, 200, 0));      // the top edge is still its block

    // Nothing to draw is not a crash.
    BorderImage empty;
    rasterize_border_image(empty, 60, 40, 60, 40, &rgba);
    CHECK(rgba.size() == 60u * 40u * 4u);
    bool all_clear = true;
    for (uint8_t v : rgba) all_clear = all_clear && v == 0;
    CHECK(all_clear);
}

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

// Compare prepared interpolation against the original scalar arithmetic,
// including hint indices, transparent endpoints and repeated stop ranges.
void test_gradient_prepared_color_spans() {
    const char* cases[] = {
        "linear-gradient(90deg, rgba(17, 109, 213, .37) 0%, rgba(229, 71, 43, .83) 100%)",
        "linear-gradient(90deg, rgba(17, 109, 213, .37) 0%, transparent 100%)",
        "linear-gradient(90deg, transparent 0%, rgba(229, 71, 43, .83) 100%)",
        "linear-gradient(90deg, rgba(17, 109, 213, .000001) 0%, rgba(229, 71, 43, .000003) 100%)",
        "linear-gradient(90deg, rgba(17, 109, 213, .37) 0%, rgba(17, 109, 213, .37) 100%)",
        "linear-gradient(90deg, rgba(17, 109, 213, .37) 0%, 25%, rgba(17, 109, 213, .37) 100%)",
        "linear-gradient(90deg, rgba(17, 109, 213, .37) 0%, 25%, rgba(229, 71, 43, .83) 100%)",
        "linear-gradient(90deg, red 0%, rgba(17, 109, 213, .37) 50%, blue 100%)",
        "repeating-linear-gradient(90deg, rgba(17, 109, 213, .37) 0%, rgba(229, 71, 43, .83) 50%)",
        "repeating-linear-gradient(90deg, rgba(255,255,255,.025) 0% 25%, transparent 25% 50%)"
    };
    LayoutContext ctx;
    for (const char* css : cases) {
        Gradient gradient;
        CHECK(parse_gradient(css, LinearColor::black(), &gradient));
        std::vector<std::array<float, 4>> colors;
        for (const auto& stop : gradient.stops) {
            Gradient constant;
            constant.stops = {stop};
            std::array<float, 4> color;
            sample_gradient(constant, 0, 0, 1, 1, ctx, 16, color.data());
            colors.push_back(color);
        }
        for (int point = -128; point <= 384; ++point) {
            const double x = point / 256.0;
            double t = x; // A 1x1, 90-degree gradient at y=.5 has t=x.
            const double last = gradient.stops.back().position;
            if (gradient.repeating) t -= last * std::floor(t / last);
            std::array<float, 4> expected = colors.back();
            if (t <= 0) expected = colors.front();
            else if (t < last) {
                for (size_t i = 0; i + 1 < gradient.stops.size(); ++i) {
                    if (gradient.stops[i].is_hint) continue;
                    size_t next = i + 1;
                    const bool hint = gradient.stops[next].is_hint;
                    if (hint) ++next;
                    const double p0 = gradient.stops[i].position;
                    const double p1 = gradient.stops[next].position;
                    if (t < p0 || t > p1) continue;
                    double local = p1 > p0 ? (t - p0) * (1.0 / (p1 - p0)) : 0;
                    if (hint) {
                        const double h = (gradient.stops[i + 1].position - p0) / (p1 - p0);
                        local = std::pow(local, std::log(0.5) / std::log(h));
                    }
                    const float fraction = static_cast<float>(std::clamp(local, 0.0, 1.0));
                    const auto& a = colors[i];
                    const auto& b = colors[next];
                    const float alpha = a[3] + (b[3] - a[3]) * fraction;
                    expected[3] = alpha > 0 ? alpha : 0;
                    for (int channel = 0; channel < 3; ++channel) {
                        const float pa = a[channel] * a[3], pb = b[channel] * b[3];
                        expected[channel] = alpha > 0 ? (pa + (pb - pa) * fraction) / alpha : 0;
                    }
                    break;
                }
            }
            float actual[4];
            sample_gradient(gradient, x, 0.5, 1, 1, ctx, 16, actual);
            for (int channel = 0; channel < 4; ++channel) CHECK(actual[channel] == expected[channel]);
        }
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
    // A texel per pixel, up to the cap the layer's content earns. Both of
    // these gradients are smooth -- no conic, no repeated stop position -- so
    // both take the smooth cap, which the 1000x600 canvas hits and the 40x40
    // box is well under. See background_texture_detail().
    bool has_canvas_tex = false, has_small_tex = false;
    for (const auto& kv : backend.textures) {
        if (kv.second.x == kSmoothGradientTexels && kv.second.y == kSmoothGradientTexels) {
            has_canvas_tex = true;
        }
        if (kv.second.x == 40 && kv.second.y == 40) has_small_tex = true;
    }
    CHECK(has_canvas_tex && has_small_tex);

    // And a gradient WITH a discontinuity keeps every texel it asks for: a
    // conic closes on itself, and smearing that seam is the one thing the cap
    // must not do.
    CHECK(f.css("html, body { margin: 0; height: 100% }"
                "body { background: conic-gradient(from 0deg, #000 0%, #fff 100%) }"));
    CHECK(f.layout("<body></body>"));
    RecordingBackend hard;
    std::vector<TextureHandle> hard_owned;
    PaintContext hard_paint;
    hard_paint.backend = &hard;
    hard_paint.owned_textures = &hard_owned;
    paint_tree(f.tree, f.root, f.ctx, hard_paint);
    bool full_size = false;
    for (const auto& kv : hard.textures) {
        if (kv.second.x == 1000 && kv.second.y == 600) full_size = true;
    }
    CHECK(full_size);
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

    // Square corners copy the original raster bytes into the padded image.
    // Check both representations of no radius, and a narrow, downscaled box.
    const BorderRadii square = BorderRadii::zero();
    const LinearColor color{0.17f, 0.43f, 0.91f, 0.37f};
    for (const BorderRadii* radii : {static_cast<const BorderRadii*>(nullptr), &square}) {
        std::vector<uint8_t> inner, padded;
        rasterize_background({}, color, 13, 71, 3, 17, ctx, 16, &inner);
        rasterize_background_padded({}, color, 13, 71, 7, 21, 2, radii, ctx, 16, &padded);
        std::vector<uint8_t> expected(7 * 21 * 4, 0);
        for (int y = 0; y < 17; ++y)
            for (int x = 0; x < 3; ++x)
                for (int c = 0; c < 4; ++c)
                    expected[((y + 2) * 7 + x + 2) * 4 + c] = inner[(y * 3 + x) * 4 + c];
        CHECK(padded == expected);
    }
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
        // The select's arrow: a 9x5 triangle pointing down, in the control's
        // own text colour at three-quarter weight.
        if (near(r.width, 9, e) && near(r.height, 5, e) && c.a > 0.7f && c.a < 0.8f) ++caret;
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

// background_size_independent() promises that two boxes of DIFFERENT sizes
// rasterize to identical texels at the same texture size. That promise is what
// lets the paint cache drop width and height from its key, so breaking it puts
// the wrong picture on screen -- and no page-comparison gate would notice,
// because the captured documents never resize.
//
// So it is checked directly: every answer is rasterized at several aspect
// ratios and the bytes compared. `true` must mean byte-identical. `false` is
// allowed to be conservative -- a gradient that happens not to move is still a
// correct thing to refuse to share -- so it is checked the other way only for
// the forms whose dependence is the reason the predicate exists.
void test_background_size_independence() {
    struct Case {
        const char* css;
        bool independent;
        bool must_differ;   // and, for the dependent ones, that it really does
    };
    const Case cases[] = {
        // Fully normalised: percentages all the way down, and an ELLIPSE, whose
        // two radii divide the two axes back out independently.
        {"radial-gradient(ellipse 80% 60% at 50% 0%, #16223a 0%, rgba(22,34,58,0) 55%)", true,
         false},
        {"radial-gradient(ellipse closest-side at 30% 70%, #fff, #000)", true, false},
        {"radial-gradient(ellipse farthest-corner at 50% 50%, #fff, #000)", true, false},
        // Axis-aligned linear: the ramp runs along one axis and normalises.
        {"linear-gradient(180deg, #0b0e16 0%, #080a10 100%)", true, false},
        {"linear-gradient(90deg, #f00 0%, #00f 100%)", true, false},
        {"linear-gradient(270deg, #f00 20%, #00f 80%)", true, false},

        // A px radius is an absolute distance; the box growing around it moves
        // the falloff.
        {"radial-gradient(ellipse 400px 300px at 50% 0%, #16223a 0%, rgba(22,34,58,0) 55%)", false,
         true},
        // One radius over two axes: the circle stays round while the box
        // stretches, so the normalised picture cannot.
        {"radial-gradient(circle closest-side at 50% 50%, #fff, #000)", false, true},
        // Off the axes, the iso-lines are only at that angle on a square box.
        {"linear-gradient(135deg, #36c2ff, #5b8cff)", false, true},
        // The magic corners ARE an aspect-dependent angle.
        {"linear-gradient(to bottom right, #36c2ff, #5b8cff)", false, true},
        // Angles around a centre distort with the aspect ratio.
        {"conic-gradient(from 0deg at 50% 50%, #f00, #0f0, #00f, #f00)", false, true},
        // A px stop sits a different fraction along the line on every width.
        {"linear-gradient(90deg, #36c2ff 0px, #5b8cff 300px)", false, true},
    };

    // Same texture size throughout -- that is the premise, and it is what the
    // cache key now pins instead of the box.
    const int tw = 128, th = 128;
    const double sizes[][2] = {{1280, 1135}, {1269, 1135}, {640, 1135}, {1280, 400}};

    for (const Case& c : cases) {
        LayoutContext ctx;
        ctx.viewport_width_px = 1280;
        ctx.viewport_height_px = 720;
        ComputedStyle style;
        style.set(CssPropertyRegistry::instance().id_of("background-image"), c.css);
        const LinearColor base{0, 0, 0, 0};
        const std::vector<BackgroundLayer> layers = resolve_background_layers(&style, base);
        CHECK(!layers.empty());
        CHECK(background_size_independent(layers) == c.independent);

        std::vector<uint8_t> first;
        rasterize_background(layers, base, sizes[0][0], sizes[0][1], tw, th, ctx, 16, &first);
        bool any_differed = false;
        for (size_t i = 1; i < sizeof(sizes) / sizeof(sizes[0]); ++i) {
            std::vector<uint8_t> other;
            rasterize_background(layers, base, sizes[i][0], sizes[i][1], tw, th, ctx, 16, &other);
            const bool same = other == first;
            if (!same) any_differed = true;
            // The whole promise, in one line: an independent answer means the
            // bytes match at every size, with no tolerance.
            if (c.independent) CHECK(same);
        }
        // And the dependent ones are not merely being refused out of caution.
        if (c.must_differ) CHECK(any_differed);
    }
}

// blur_flat_rgba() claims to produce what blur_rgba() would, for a buffer whose
// colour never varies. It is worth a test rather than an argument: the fast path
// skips three quarters of the filter on the grounds that those planes are the
// alpha plane times a constant, and if that reasoning is off by a rounding step
// every shadow in the engine shifts colour.
//
// Wherever there is any coverage at all, the two agree byte for byte. Where
// there is NONE they need not, and do not: the four-channel path divides the
// blurred colour by the blurred alpha, and at a texel whose alpha has fallen to
// a rounding residue that divide amplifies the residue into an arbitrary
// colour -- 204 of 255 away from the real one, in the case below. It is
// invisible directly, being fully transparent, but a straight-alpha texture is
// sampled bilinearly and those texels DO bleed into their neighbours, so
// carrying the actual colour there is the better of the two. That is what the
// fast path does, and it is the one place the outputs differ.
void test_blur_flat_matches_full() {
    struct Shape { int w, h; double sigma; };
    const Shape shapes[] = {
        {5, 5, 1.0}, {32, 32, 4.0}, {64, 24, 9.0}, {17, 61, 2.5}, {120, 90, 30.0},
    };
    // Colours chosen to land awkwardly: one that divides 255 evenly, one that
    // does not, one at full white and one nearly black.
    const uint8_t colours[][3] = {{51, 102, 153}, {200, 37, 91}, {255, 255, 255}, {1, 2, 3}};

    for (const Shape& s : shapes) {
        for (const auto& c : colours) {
            std::vector<uint8_t> full(static_cast<size_t>(s.w) * s.h * 4, 0);
            // A coverage field with an edge, a ramp and a hole in it, so the
            // blur has something to do in every direction.
            for (int y = 0; y < s.h; ++y) {
                for (int x = 0; x < s.w; ++x) {
                    const bool inside = x > s.w / 5 && x < s.w - s.w / 5 &&
                                        y > s.h / 5 && y < s.h - s.h / 5;
                    const bool hole = x > s.w / 2 && x < s.w / 2 + 3 && y > s.h / 2;
                    int a = 0;
                    if (inside && !hole) a = 40 + (x * 211 + y * 97) % 216;
                    if (a == 0) continue;
                    uint8_t* d = full.data() + (static_cast<size_t>(y) * s.w + x) * 4;
                    d[0] = c[0];
                    d[1] = c[1];
                    d[2] = c[2];
                    d[3] = static_cast<uint8_t>(a);
                }
            }
            std::vector<uint8_t> flat = full;
            blur_rgba(&full, s.w, s.h, s.sigma);
            blur_flat_rgba(&flat, s.w, s.h, s.sigma);

            // The alpha plane -- the whole picture, as far as a shadow is
            // concerned -- must be identical everywhere.
            for (size_t i = 3; i < full.size(); i += 4) CHECK(flat[i] == full[i]);
            // And so must the colour, wherever any of it shows.
            for (size_t i = 0; i < full.size(); i += 4) {
                if (full[i + 3] == 0) continue;
                CHECK(flat[i + 0] == full[i + 0]);
                CHECK(flat[i + 1] == full[i + 1]);
                CHECK(flat[i + 2] == full[i + 2]);
            }
        }
    }

    // The transparent texels really are the only difference, and the fast path
    // really does put the colour there -- stated as its own case so that a
    // future change making them agree everywhere is noticed rather than
    // silently accepted.
    {
        std::vector<uint8_t> full(17 * 61 * 4, 0);
        for (int y = 12; y < 49; ++y) {
            for (int x = 3; x < 14; ++x) {
                uint8_t* d = full.data() + (static_cast<size_t>(y) * 17 + x) * 4;
                d[0] = 51; d[1] = 102; d[2] = 153;
                d[3] = static_cast<uint8_t>(40 + (x * 211 + y * 97) % 216);
            }
        }
        std::vector<uint8_t> flat = full;
        blur_rgba(&full, 17, 61, 2.5);
        blur_flat_rgba(&flat, 17, 61, 2.5);
        CHECK(flat != full);
        int differing_with_coverage = 0, transparent_carrying_colour = 0;
        for (size_t i = 0; i < full.size(); i += 4) {
            const bool same = flat[i] == full[i] && flat[i + 1] == full[i + 1] &&
                              flat[i + 2] == full[i + 2];
            if (!same && full[i + 3] != 0) ++differing_with_coverage;
            if (!same && full[i + 3] == 0 && flat[i] == 51 && flat[i + 1] == 102 &&
                flat[i + 2] == 153) {
                ++transparent_carrying_colour;
            }
        }
        CHECK(differing_with_coverage == 0);
        CHECK(transparent_carrying_colour > 0);
    }

    // And the degenerate inputs the shadow paths really do hand it: a buffer
    // with no coverage anywhere, and a sigma below the threshold that makes
    // the whole call a no-op.
    std::vector<uint8_t> empty(64 * 4, 0);
    std::vector<uint8_t> empty_flat = empty;
    blur_rgba(&empty, 8, 8, 3.0);
    blur_flat_rgba(&empty_flat, 8, 8, 3.0);
    CHECK(empty_flat == empty);

    std::vector<uint8_t> tiny(64 * 4, 200);
    std::vector<uint8_t> tiny_flat = tiny;
    blur_rgba(&tiny, 8, 8, 0.1);
    blur_flat_rgba(&tiny_flat, 8, 8, 0.1);
    CHECK(tiny_flat == tiny);
}

namespace {
// Frozen scalar reference for the four-channel blur before strip/rounding
// optimization. Each channel is visited independently, with the original
// float differences, double accumulators, clamped edges and lround tail.
// Compare all bytes, including invisible RGB that bilinear sampling can use.
void scalar_blur(std::vector<uint8_t>* rgba, int w, int h, double sigma) {
    if (sigma <= 0.3 || w <= 0 || h <= 0) return;
    const size_t n = static_cast<size_t>(w) * h;
    std::vector<float> p(n * 4), tmp(n * 4);
    for (size_t i = 0; i < n; ++i) {
        const float a = (*rgba)[i * 4 + 3] / 255.0f;
        for (int c = 0; c < 3; ++c) p[i * 4 + c] = (*rgba)[i * 4 + c] / 255.0f * a;
        p[i * 4 + 3] = a;
    }
    const int r = std::max(1, static_cast<int>(std::sqrt(12.0 * sigma * sigma / 3.0 + 1.0))) / 2;
    const double inv = 1.0 / (2 * r + 1);
    for (int pass = 0; pass < 3; ++pass) {
        for (int c = 0; c < 4; ++c) {
            for (int y = 0; y < h; ++y) {
                const auto at = [&](int x) { return (static_cast<size_t>(y) * w + std::clamp(x, 0, w-1)) * 4 + c; };
                double acc = 0;
                for (int k = -r; k <= r; ++k) acc += p[at(k)];
                for (int x = 0; x < w; ++x) {
                    tmp[at(x)] = static_cast<float>(acc * inv);
                    acc += p[at(x+r+1)] - p[at(x-r)];
                }
            }
            for (int x = 0; x < w; ++x) {
                const auto at = [&](int y) { return (static_cast<size_t>(std::clamp(y, 0, h-1)) * w + x) * 4 + c; };
                double acc = 0;
                for (int k = -r; k <= r; ++k) acc += tmp[at(k)];
                for (int y = 0; y < h; ++y) {
                    p[at(y)] = static_cast<float>(acc * inv);
                    acc += tmp[at(y+r+1)] - tmp[at(y-r)];
                }
            }
        }
    }
    const auto byte = [](float v) {
        return static_cast<uint8_t>(std::lround(std::clamp(v, 0.0f, 1.0f) * 255));
    };
    for (size_t i = 0; i < n; ++i) {
        const float a = p[i * 4 + 3];
        for (int c = 0; c < 3; ++c) (*rgba)[i * 4 + c] = a > 0 ? byte(p[i * 4 + c] / a) : 0;
        (*rgba)[i * 4 + 3] = byte(a);
    }
}
}

void test_blur_matches_scalar() {
    for (const auto& size : {std::pair<int,int>{0,0}, {1,1}, {1,41}, {41,1}, {3,5},
                           {17,2}, {41,6}, {17,61}, {127,29}, {128,31}, {129,33},
                           {513,257}, {1024,656}, {1024,664}}) {
        const int w = size.first, h = size.second;
        for (double sigma : {0.0, 0.3, 0.31, 0.5, 1.0, 2.5, 30.0, 90.0}) {
            if (w > 500 && sigma != 2.5 && sigma != 30) continue;
            std::vector<uint8_t> input(static_cast<size_t>(w) * h * 4);
            uint32_t seed = 17;
            for (auto& v : input) { seed = 1664525u * seed + 1013904223u; v = seed >> 24; }
            auto flat = input;
            for (size_t i = 0; i < flat.size(); i += 4) {
                flat[i] = 200; flat[i + 1] = 37; flat[i + 2] = 91;
            }
            auto expected = input;
            scalar_blur(&expected, w, h, sigma);
            blur_rgba(&input, w, h, sigma);
            if (input != expected) std::printf("  blur bytes differ: %dx%d sigma=%g\n", w, h, sigma);
            CHECK(input == expected);
            // Interleaving rows in the one-channel shadow path must preserve
            // the scalar alpha, including 1/2/3-row tails and radii larger
            // than either dimension. The color remains the constant input.
            blur_flat_rgba(&flat, w, h, sigma);
            bool same_alpha = true, same_color = true;
            for (size_t i = 0; i < flat.size(); i += 4) {
                same_alpha &= flat[i + 3] == expected[i + 3];
                if (flat[i + 3] > 0)
                    same_color &= flat[i] == 200 && flat[i + 1] == 37 && flat[i + 2] == 91;
            }
            CHECK(same_alpha);
            CHECK(same_color);
        }
    }
}
