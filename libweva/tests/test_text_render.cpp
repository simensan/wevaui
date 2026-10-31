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

void test_glyph_atlas() {
    StubFont font;
    GlyphAtlas atlas(64, 64);
    const FaceHandle face = StubFont::builtin();
    uint32_t a = 0, b = 0, space = 0;
    font.glyph_index(face, 'A', &a);
    font.glyph_index(face, 'B', &b);
    font.glyph_index(face, ' ', &space);

    // No bitmap or upload for text-free documents, even after font/renderer
    // changes. Spaces, failed rasters and oversized glyphs do not allocate it.
    SoftwareRenderer unused_renderer(4, 4);
    CHECK(atlas.pixels().capacity() == 0);
    CHECK(!atlas.texture(&unused_renderer));
    atlas.clear();
    CHECK(atlas.pixels().capacity() == 0);
    CHECK(!atlas.texture(&unused_renderer));
    CHECK(atlas.get(&font, face, space, 16) == nullptr);
    CHECK(atlas.get(&font, face, a, 256) == nullptr);
    CHECK(atlas.pixels().capacity() == 0);
    atlas.release_texture(&unused_renderer);
    CHECK(!atlas.texture(&unused_renderer));
    const uint64_t slot_version = atlas.slot_version();

    const GlyphSlot* sa = atlas.get(&font, face, a, 16);
    CHECK(sa != nullptr && sa->width > 0);
    CHECK(atlas.pixels().size() == 64 * 64 * 4);
    // A second request is cached, not re-packed.
    CHECK(atlas.get(&font, face, a, 16) == sa);
    CHECK(atlas.slot_count() == 1);

    const GlyphSlot* sb = atlas.get(&font, face, b, 16);
    CHECK(sb != nullptr && sb != sa);
    // Packed side by side, with a pixel of padding so a filtering backend
    // cannot bleed one glyph into its neighbour.
    CHECK(sb->x > sa->x + sa->width - 1);
    CHECK(atlas.slot_count() == 2);

    // Small numerical noise shares a raster, but distinct fractional sizes
    // must never reuse metrics/coverage from the first requested size.
    CHECK(atlas.get(&font, face, a, 15.999) == sa);
    CHECK(atlas.get(&font, face, a, 15.6) != sa);
    CHECK(atlas.get(&font, face, a, 16.4) != sa);
    CHECK(atlas.get(&font, face, a, 24) != sa);
    const size_t gutter = (static_cast<size_t>(sa->y) * atlas.width() + sa->x - 1) * 4;
    CHECK(atlas.pixels()[gutter] == 255 && atlas.pixels()[gutter + 3] == 0);

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
    CHECK(atlas.slot_version() == slot_version);
    atlas.release_texture(&r);
    CHECK(atlas.slot_count() > 0 && atlas.slot_version() == slot_version);
    atlas.clear();
    CHECK(atlas.slot_count() == 0 && atlas.slot_version() != slot_version);
    CHECK(atlas.get(&font, face, a, 16) != nullptr);

    struct EmptyFont : StubFont {
        int rasters = 0, metrics = 0;
        bool fail = false, ink = false;
        bool rasterize(FaceHandle, uint32_t, double, RenderMode, Bitmap* out) override {
            ++rasters;
            *out = {};
            if (fail) return false;
            if (ink) { out->width = out->height = 1; out->data = {255}; }
            return true;
        }
        bool glyph_metrics(FaceHandle, uint32_t, double, GlyphMetrics* out) override {
            ++metrics;
            *out = {};
            return true;
        }
    } empty_font;
    GlyphAtlas empty_atlas(16, 16);
    CHECK(empty_atlas.get(&empty_font, face, space, 16) == nullptr);
    CHECK(empty_font.rasters == 1 && empty_font.metrics == 0);
    for (int i = 0; i < 20; ++i)
        CHECK(empty_atlas.get(&empty_font, face, space, 15.999) == nullptr);
    CHECK(empty_font.rasters == 1 && empty_atlas.slot_count() == 0);
    CHECK(empty_atlas.get(&empty_font, face, space, 16.4) == nullptr);
    CHECK(empty_atlas.get(&empty_font, FaceHandle{2}, space, 16) == nullptr);
    CHECK(empty_atlas.get(&empty_font, face, space + 1, 16) == nullptr);
    CHECK(empty_font.rasters == 4);
    // Failed rasterization is retryable, never a cached empty glyph.
    empty_font.fail = true;
    CHECK(empty_atlas.get(&empty_font, face, space + 2, 16) == nullptr);
    CHECK(empty_atlas.get(&empty_font, face, space + 2, 16) == nullptr);
    CHECK(empty_font.rasters == 6);
    CHECK(empty_atlas.pixels().capacity() == 0);
    empty_font.fail = false;
    empty_font.ink = true;
    CHECK(empty_atlas.get(&empty_font, face, space + 2, 16) != nullptr);
    CHECK(empty_font.rasters == 7 && empty_font.metrics == 1);
    // Resetting the font backend must discard successful empty results too.
    empty_atlas.clear();
    CHECK(empty_atlas.get(&empty_font, face, space, 16) != nullptr);
    CHECK(empty_font.rasters == 8 && empty_font.metrics == 2);
    // Many distinct empty requests exceed the bounded cache. No collision may
    // suppress a different glyph's ink, and no empty request occupies a slot.
    empty_atlas.clear();
    empty_font.ink = false;
    for (uint32_t glyph = 1000; glyph < 1256; ++glyph)
        CHECK(empty_atlas.get(&empty_font, face, glyph, 16) == nullptr);
    CHECK(empty_atlas.slot_count() == 0);
    empty_font.ink = true;
    CHECK(empty_atlas.get(&empty_font, face, 2000, 16) != nullptr);
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

    // Glyph quads land on WHOLE PIXELS, however fractional the origin.
    //
    // The atlas holds one bitmap per glyph, rasterised on the pixel grid, and
    // the quad spans exactly its texels. Start that quad at x.5 and every texel
    // column straddles two pixels; a backend sampling with nearest filtering --
    // which is what keeps text crisp, and what both of ours do -- then drops
    // some columns and doubles others. It looks like stems at different weights
    // inside one word, and it is what the Godot gallery was showing.
    Mesh frac;
    build_text_geometry("AB", 10.37, 20.62, 16, LinearColor::white(), p, &frac);
    CHECK(!frac.vertices.empty());
    for (const Vertex& v : frac.vertices) {
        CHECK(v.position.x == std::floor(v.position.x));
        CHECK(v.position.y == std::floor(v.position.y));
    }
    // Snapped, not truncated to the run's origin: the second glyph is still an
    // advance away, so the pen keeps its precision and only the bitmap moves.
    CHECK(near(frac.vertices[4].position.x - frac.vertices[0].position.x, 8, 1e-4));

    // A scaled UI must request the final pixel size and place texels on that
    // grid, even when its window has a fractional translation. Layout advances
    // remain logical so caret placement and wrapping do not change.
    for (float scale : {0.75f, 1.5f, -0.75f}) {
        const Transform2D xf(scale, 0, 0, scale, 3.37f, 7.62f);
        Mesh scaled;
        build_text_geometry("AB", 10.37, 20.62, 16, LinearColor::white(), p, &scaled, 0, nullptr, &xf);
        uint32_t a = 0;
        font.glyph_index(p.face, 'A', &a);
        const GlyphSlot* slot = atlas.get(&font, p.face, a, 16 * std::abs(scale));
        CHECK(slot && scaled.vertices.size() == 8);
        CHECK(near(std::abs((scaled.vertices[1].position.x - scaled.vertices[0].position.x) * scale), slot->width, 1e-4));
        for (const Vertex& v : scaled.vertices) {
            double x, y;
            xf.apply(v.position.x, v.position.y, &x, &y);
            CHECK(near(x, std::round(x), 1e-4) && near(y, std::round(y), 1e-4));
        }
    }
}

// A blurred text-shadow is BLURRED, not a stack of copies of the glyph.
//
// It used to be drawn 25 times on a 5x5 grid spaced one sigma apart. At a small
// blur that passes for a Gaussian; at a large one the copies stop overlapping
// and the glow becomes a visible lattice of ghost letters, which is what a
// 110px glyph with a 24px shadow looked like in the dialogue sample.
void test_blurred_text_shadow_is_one_blurred_run() {
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
    CHECK(parse_stylesheet("#a { display: block; font-size: 16px; color: #ffffff;"
                           "     text-shadow: 0 0 12px #ffffff }",
                           false, author.get(), &pe));
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
    bl.layout_root(root, 120, 60);

    // Counts draws rather than looking at pixels: the lattice and the blur can
    // both put ink in the same places, but one of them costs twenty-five draws.
    struct Counter : RenderInterface {
        int draws = 0;
        int textured = 0;
        uint64_t next = 1;
        std::map<uint64_t, int> pending;
        GeometryHandle compile_geometry(const std::vector<Vertex>&,
                                        const std::vector<uint32_t>&) override {
            return GeometryHandle{next++};
        }
        void render_geometry(GeometryHandle, Vec2, TextureHandle t) override {
            ++draws;
            if (t.id != 0) ++textured;
        }
        void release_geometry(GeometryHandle) override {}
        TextureHandle load_texture(std::string_view, Vec2i*) override { return {}; }
        TextureHandle generate_texture(const std::vector<uint8_t>&, Vec2i) override {
            return TextureHandle{next++};
        }
        void release_texture(TextureHandle) override {}
        void set_scissor(const Recti*) override {}
    } counter;

    StubFont font;
    GlyphAtlas atlas;
    PaintContext p;
    p.backend = &counter;
    p.font = &font;
    p.atlas = &atlas;
    p.face = StubFont::builtin();
    paint_tree(tree, root, ctx, p);

    // The shadow is one blurred image plus the glyphs themselves. The old stack
    // alone was twenty-five, so anything near that many is the lattice back.
    CHECK(counter.draws > 0);
    CHECK(counter.draws < 8);
    // And it is textured: the blurred run is an image, not geometry.
    CHECK(counter.textured > 0);
}
