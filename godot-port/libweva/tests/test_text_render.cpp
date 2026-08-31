#include "check.h"
#include "weva/block_layout.h"
#include "weva/box_builder.h"
#include "weva/cascade.h"
#include "weva/dom.h"
#include "weva/font_interface.h"
#include "weva/font_metrics.h"
#include "weva/glyph_atlas.h"
#include "weva/html.h"
#include "weva/paint.h"
#include "weva/software_renderer.h"
#include "weva/user_agent_stylesheet.h"
#include <algorithm>
#include <cmath>
#include <map>
#include <memory>

using namespace weva;

namespace {

bool near(double a, double b, double eps = 1e-6) { return std::fabs(a - b) < eps; }

int covered(const SoftwareRenderer& r) {
    int n = 0;
    for (int y = 0; y < r.height(); ++y) {
        for (int x = 0; x < r.width(); ++x) {
            if (r.pixel(x, y).a > 0) ++n;
        }
    }
    return n;
}

} // namespace

void test_stub_font() {
    StubFont font;
    const FaceHandle face = font.load_face({}, 0);
    CHECK(static_cast<bool>(face));

    FaceMetrics fm;
    CHECK(font.face_metrics(face, 16, &fm));
    // The stub's em box matches MonoFontMetrics, so measurement and rendering
    // agree — a mismatch would show as text drifting off the line boxes laid
    // out for it.
    CHECK(near(fm.ascent, 12.8) && near(fm.descent, 6.4));

    uint32_t g = 0;
    CHECK(font.glyph_index(face, 'A', &g) && g != 0);
    // An unmapped code point maps to glyph 0, the standard missing-glyph slot,
    // rather than failing — the caller draws nothing for it instead of dropping
    // the whole run.
    uint32_t missing = 999;
    CHECK(font.glyph_index(face, 0x4E2D, &missing) && missing == 0);

    GlyphMetrics gm;
    CHECK(font.glyph_metrics(face, g, 16, &gm));
    CHECK(near(gm.advance, 8));
    // The bearing is the ROUNDED cell height (13), not the exact ascent (12.8):
    // the bitmap is a whole number of pixels tall, so using the ascent would
    // leave the quad's bottom edge a fraction below the baseline.
    CHECK(near(gm.bearing_y, 13));
    CHECK(gm.width > 0 && gm.height > 0 && gm.height == 13);
    // The missing glyph, and a space, have an advance but NO bitmap — exactly
    // as in a real face, so the atlas never packs an empty rectangle.
    CHECK(font.glyph_metrics(face, 0, 16, &gm) && near(gm.advance, 8) && gm.width == 0);
    uint32_t sp_glyph = 0;
    font.glyph_index(face, ' ', &sp_glyph);
    CHECK(font.glyph_metrics(face, sp_glyph, 16, &gm) && near(gm.advance, 8) && gm.width == 0);

    Bitmap bmp;
    CHECK(font.rasterize(face, g, 16, RenderMode::Alpha8, &bmp));
    CHECK(bmp.width > 0 && bmp.height > 0);
    CHECK(bmp.data.size() == static_cast<size_t>(bmp.width) * bmp.height);
    // 'A' has ink; a space does not.
    bool ink = false;
    for (uint8_t v : bmp.data) {
        if (v > 0) ink = true;
    }
    CHECK(ink);
    uint32_t space = 0;
    font.glyph_index(face, ' ', &space);
    Bitmap sp;
    CHECK(font.rasterize(face, space, 16, RenderMode::Alpha8, &sp));
    CHECK(sp.data.empty());
    // SDF is refused rather than silently returning an alpha bitmap.
    CHECK(!font.rasterize(face, g, 16, RenderMode::Sdf, &bmp));
}

void test_stub_shaping() {
    StubFont font;
    const FaceHandle face = StubFont::builtin();
    std::vector<ShapedGlyph> out;
    font.shape(face, "ab c", 16, &out);
    CHECK(out.size() == 4);
    for (const ShapedGlyph& g : out) CHECK(near(g.x_advance, 8));
    // `cluster` carries the byte offset, so a caller mapping back to source
    // text works the same as it will with a real shaper.
    CHECK(out[0].cluster == 0 && out[3].cluster == 3);

    // Multi-byte input advances by code point, not by byte, and the cluster
    // still points at the byte where the character started.
    font.shape(face, "aéb", 16, &out);
    CHECK(out.size() == 3);
    CHECK(out[1].cluster == 1 && out[2].cluster == 3);
    // The unmapped middle character is the missing glyph but still advances.
    CHECK(out[1].glyph == 0 && near(out[1].x_advance, 8));
}

void test_glyph_atlas() {
    StubFont font;
    GlyphAtlas atlas(64, 64);
    const FaceHandle face = StubFont::builtin();
    uint32_t a = 0, b = 0, space = 0;
    font.glyph_index(face, 'A', &a);
    font.glyph_index(face, 'B', &b);
    font.glyph_index(face, ' ', &space);

    const GlyphSlot* sa = atlas.get(&font, face, a, 16);
    CHECK(sa != nullptr && sa->width > 0);
    // A second request is cached, not re-packed.
    CHECK(atlas.get(&font, face, a, 16) == sa);
    CHECK(atlas.slot_count() == 1);

    const GlyphSlot* sb = atlas.get(&font, face, b, 16);
    CHECK(sb != nullptr && sb != sa);
    // Packed side by side, with a pixel of padding so a filtering backend
    // cannot bleed one glyph into its neighbour.
    CHECK(sb->x > sa->x + sa->width - 1);
    CHECK(atlas.slot_count() == 2);

    // Sizes are quantised to whole pixels, so a fractional font-size does not
    // re-pack an identical raster.
    CHECK(atlas.get(&font, face, a, 15.99) == sa);
    CHECK(atlas.get(&font, face, a, 24) != sa);

    // A glyph with no bitmap gets no slot at all.
    CHECK(atlas.get(&font, face, space, 16) == nullptr);
    // Texture coordinates are normalised against the atlas size.
    CHECK(sa->u0 >= 0.0f && sa->u1 <= 1.0f);
    CHECK(near(sa->u1 - sa->u0, double(sa->width) / atlas.width(), 1e-5));

    // The atlas uploads once and reuses the handle until something changes.
    SoftwareRenderer r(4, 4);
    const TextureHandle t1 = atlas.texture(&r);
    CHECK(static_cast<bool>(t1));
    CHECK(atlas.texture(&r).id == t1.id);
    atlas.get(&font, face, b, 32);
    CHECK(atlas.texture(&r).id != t1.id);
}

void test_text_geometry() {
    StubFont font;
    GlyphAtlas atlas;
    PaintContext p;
    p.font = &font;
    p.atlas = &atlas;
    p.face = StubFont::builtin();

    Mesh m;
    build_text_geometry("AB", 10, 20, 16, LinearColor::white(), p, &m);
    // One textured quad per glyph, all sharing the atlas — so a run is a single
    // draw rather than one per character.
    CHECK(m.vertices.size() == 8);
    CHECK(m.indices.size() == 12);
    // Glyphs sit ABOVE the baseline — bearing_y measures up from it — with the
    // bottom edge resting exactly ON it.
    for (const Vertex& v : m.vertices) CHECK(v.position.y <= 20.0f);
    float lowest = 0;
    for (const Vertex& v : m.vertices) lowest = std::max(lowest, v.position.y);
    CHECK(near(lowest, 20));
    // The second glyph is one advance to the right of the first.
    CHECK(near(m.vertices[4].position.x - m.vertices[0].position.x, 8, 1e-4));

    // A space produces no quad but still advances the pen, or the gaps between
    // words would close up.
    Mesh sp;
    build_text_geometry("A B", 0, 20, 16, LinearColor::white(), p, &sp);
    CHECK(sp.vertices.size() == 8);
    CHECK(near(sp.vertices[4].position.x, 16, 1e-4));

    // Without a font or atlas nothing is emitted, and the caller still paints
    // the rest of the box.
    PaintContext none;
    Mesh empty;
    build_text_geometry("A", 0, 0, 16, LinearColor::white(), none, &empty);
    CHECK(empty.empty());
}

void test_text_end_to_end() {
    // Cascade, layout, shape, pack and rasterize — text on screen.
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
    CHECK(parse_stylesheet("#a { display: block; font-size: 16px; color: #ff0000 }", false,
                           author.get(), &pe));
    styles.engine.add_stylesheet(author.get(), DeclarationOrigin::Author);
    sheets.push_back(std::move(author));

    HtmlParseError he;
    ParseOptions o;
    o.strict = false;
    Ref<Document> doc = parse_html("<body><div id=a>Hi</div></body>", &symbols, o, &he);
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
    bl.layout_root(root, 80, 40);

    SoftwareRenderer r(80, 40);
    r.clear(LinearColor::transparent());
    StubFont font;
    GlyphAtlas atlas;
    PaintContext p;
    p.backend = &r;
    p.font = &font;
    p.atlas = &atlas;
    p.face = StubFont::builtin();
    paint_tree(tree, root, ctx, p);

    // Ink on screen, in the run's colour, and above the baseline.
    const int n = covered(r);
    CHECK(n > 0);
    bool red = false;
    for (int y = 0; y < r.height() && !red; ++y) {
        for (int x = 0; x < r.width(); ++x) {
            const LinearColor c = r.pixel(x, y);
            if (c.a > 0.5f && c.r > 0.9f && c.g < 0.1f) { red = true; break; }
        }
    }
    CHECK(red);
    // Two glyphs at 8px advance from x=0: nothing past x=16, and nothing below
    // the 12.8px baseline.
    for (int y = 0; y < r.height(); ++y) {
        for (int x = 16; x < r.width(); ++x) CHECK(r.pixel(x, y).a == 0.0f);
    }
    for (int y = 13; y < r.height(); ++y) {
        for (int x = 0; x < r.width(); ++x) CHECK(r.pixel(x, y).a == 0.0f);
    }
}
