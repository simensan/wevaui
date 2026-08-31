#include "weva_c.h"

#include "weva/block_layout.h"
#include "weva/box_builder.h"
#include "weva/cascade.h"
#include "weva/font_interface.h"
#include "weva/font_metrics.h"
#include "weva/glyph_atlas.h"
#include "weva/html.h"
#include "weva/paint.h"
#include "weva/positioning.h"
#include "weva/selector.h"
#include "weva/user_agent_stylesheet.h"

#include <algorithm>
#include <cstring>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace {

using namespace weva;

// Collects the draw list instead of rasterizing it, so the host's own renderer
// issues the draws. This is the backend the ABI implies: the core still does
// every bit of the tessellation, and what crosses the boundary is triangles.
class CollectingBackend : public RenderInterface {
public:
    struct Draw {
        std::vector<Vertex> vertices;
        std::vector<uint32_t> indices;
        uint64_t texture = 0;
        std::optional<Recti> scissor;
    };

    void begin_frame() {
        draws.clear();
        geometry_.clear();
        next_ = 1;
    }

    GeometryHandle compile_geometry(const std::vector<Vertex>& v,
                                    const std::vector<uint32_t>& i) override {
        const GeometryHandle h{next_++};
        geometry_[h.id] = {v, i};
        return h;
    }
    void render_geometry(GeometryHandle g, Vec2 t, TextureHandle tex) override {
        auto it = geometry_.find(g.id);
        if (it == geometry_.end()) return;
        Draw d;
        d.vertices = it->second.first;
        // The translation is baked in here rather than passed through: the ABI
        // hands over geometry that is ready to upload, so a host is not made to
        // apply it.
        for (Vertex& v : d.vertices) {
            v.position.x += t.x;
            v.position.y += t.y;
        }
        d.indices = it->second.second;
        d.texture = tex.id;
        d.scissor = scissor_;
        draws.push_back(std::move(d));
    }
    void release_geometry(GeometryHandle g) override { geometry_.erase(g.id); }

    TextureHandle load_texture(std::string_view, Vec2i* out_size) override {
        if (out_size) *out_size = {0, 0};
        return {};
    }
    TextureHandle generate_texture(const std::vector<uint8_t>& rgba, Vec2i size) override {
        if (size.x <= 0 || size.y <= 0) return {};
        const TextureHandle h{next_++};
        textures[h.id] = {rgba, size};
        return h;
    }
    void release_texture(TextureHandle t) override { textures.erase(t.id); }
    void set_scissor(const Recti* r) override {
        if (r) scissor_ = *r;
        else scissor_.reset();
    }

    std::vector<Draw> draws;
    std::map<uint64_t, std::pair<std::vector<uint8_t>, Vec2i>> textures;

private:
    std::map<uint64_t, std::pair<std::vector<Vertex>, std::vector<uint32_t>>> geometry_;
    uint64_t next_ = 1;
    std::optional<Recti> scissor_;
};

struct StyleMap : StyleProvider {
    CascadeEngine engine;
    NullStateProvider state;
    std::vector<std::unique_ptr<ComputedStyle>> owned;
    std::map<const Element*, ComputedStyle*> by_element;

    void clear() {
        owned.clear();
        by_element.clear();
    }
    void walk(const Element& e, const ComputedStyle* parent) {
        auto cs = std::make_unique<ComputedStyle>();
        engine.compute(e, state, parent, cs.get());
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
};


// ---- Host backend adapters ----------------------------------------------
//
// Each forwards to a C function-pointer table, and each falls back to the
// built-in behaviour when the host left a function null. That is what lets a
// host adopt the tables incrementally: a partially filled table degrades
// rather than crashing, which is the same contract the optional methods on the
// C++ interface already have.

class HostRenderBackend : public RenderInterface {
public:
    HostRenderBackend(const weva_render_backend& table, RenderInterface* fallback)
        : t_(table), fallback_(fallback) {}

    GeometryHandle compile_geometry(const std::vector<Vertex>& v,
                                    const std::vector<uint32_t>& i) override {
        if (!t_.compile_geometry) return fallback_->compile_geometry(v, i);
        // Vertex and weva_vertex are the same eight floats in the same order,
        // which is what lets the host read the buffer with no conversion pass.
        static_assert(sizeof(Vertex) == sizeof(weva_vertex), "vertex layout must match the ABI");
        return GeometryHandle{t_.compile_geometry(t_.user_data,
                                                  reinterpret_cast<const weva_vertex*>(v.data()),
                                                  v.size(), i.data(), i.size())};
    }
    void render_geometry(GeometryHandle g, Vec2 tr, TextureHandle tex) override {
        if (!t_.render_geometry) { fallback_->render_geometry(g, tr, tex); return; }
        t_.render_geometry(t_.user_data, g.id, tr.x, tr.y, tex.id);
    }
    void release_geometry(GeometryHandle g) override {
        if (!t_.release_geometry) { fallback_->release_geometry(g); return; }
        t_.release_geometry(t_.user_data, g.id);
    }
    TextureHandle load_texture(std::string_view path, Vec2i* out_size) override {
        if (!t_.load_texture) return fallback_->load_texture(path, out_size);
        const std::string p(path);
        int32_t w = 0, h = 0;
        const uint64_t id = t_.load_texture(t_.user_data, p.c_str(), &w, &h);
        if (out_size) *out_size = {w, h};
        return TextureHandle{id};
    }
    TextureHandle generate_texture(const std::vector<uint8_t>& rgba, Vec2i size) override {
        if (!t_.generate_texture) return fallback_->generate_texture(rgba, size);
        return TextureHandle{t_.generate_texture(t_.user_data, rgba.data(), size.x, size.y)};
    }
    void release_texture(TextureHandle t) override {
        if (!t_.release_texture) { fallback_->release_texture(t); return; }
        t_.release_texture(t_.user_data, t.id);
    }
    void set_scissor(const Recti* r) override {
        if (!t_.set_scissor) { fallback_->set_scissor(r); return; }
        if (r) t_.set_scissor(t_.user_data, 1, r->x, r->y, r->width, r->height);
        else t_.set_scissor(t_.user_data, 0, 0, 0, 0, 0);
    }

private:
    weva_render_backend t_;
    RenderInterface* fallback_;
};

class HostFontBackend : public FontInterface {
public:
    HostFontBackend(const weva_font_backend& table, FontInterface* fallback)
        : t_(table), fallback_(fallback) {}

    FaceHandle load_face(const std::vector<uint8_t>& ttf, int index) override {
        if (!t_.load_face) return fallback_->load_face(ttf, index);
        return FaceHandle{t_.load_face(t_.user_data, ttf.data(), ttf.size(), index)};
    }
    bool face_metrics(FaceHandle face, double px, FaceMetrics* out) override {
        if (!t_.face_metrics) return fallback_->face_metrics(face, px, out);
        if (!out) return false;
        *out = {};
        const int32_t ok = t_.face_metrics(t_.user_data, face.id, px, &out->ascent,
                                           &out->descent, &out->line_gap);
        out->units_per_em = px;
        return ok != 0;
    }
    bool glyph_index(FaceHandle face, uint32_t cp, uint32_t* out) override {
        if (!t_.glyph_index) return fallback_->glyph_index(face, cp, out);
        return t_.glyph_index(t_.user_data, face.id, cp, out) != 0;
    }
    bool glyph_metrics(FaceHandle face, uint32_t glyph, double px, GlyphMetrics* out) override {
        if (!t_.glyph_metrics) return fallback_->glyph_metrics(face, glyph, px, out);
        if (!out) return false;
        *out = {};
        return t_.glyph_metrics(t_.user_data, face.id, glyph, px, &out->advance,
                                &out->bearing_x, &out->bearing_y, &out->width,
                                &out->height) != 0;
    }
    bool rasterize(FaceHandle face, uint32_t glyph, double px, RenderMode mode,
                   Bitmap* out) override {
        if (!t_.rasterize) return fallback_->rasterize(face, glyph, px, mode, out);
        // The table has no SDF entry point; a host wanting SDF supplies it
        // through the alpha path with its own convention, so asking for SDF
        // here is refused rather than silently answered with coverage.
        if (mode != RenderMode::Alpha8 || !out) return false;
        weva_glyph_bitmap bmp{};
        if (!t_.rasterize(t_.user_data, face.id, glyph, px, &bmp)) return false;
        out->width = bmp.width;
        out->height = bmp.height;
        const size_t n = static_cast<size_t>(std::max(0, bmp.width)) *
                         static_cast<size_t>(std::max(0, bmp.height));
        // Copied here, so the host's buffer need only live for the call.
        out->data.assign(bmp.alpha, bmp.alpha ? bmp.alpha + n : bmp.alpha);
        if (!bmp.alpha) out->data.assign(n, 0);
        return true;
    }
    void shape(FaceHandle face, std::string_view utf8, double px,
               std::vector<ShapedGlyph>* out) override {
        if (!t_.shape) { fallback_->shape(face, utf8, px, out); return; }
        if (!out) return;
        out->clear();
        // Sized with one call, filled with a second — the same two-call shape
        // the text accessor uses, so a host implements one pattern.
        const size_t n = t_.shape(t_.user_data, face.id, utf8.data(), utf8.size(), px, nullptr,
                                  nullptr, nullptr, 0);
        if (n == 0) return;
        std::vector<uint32_t> glyphs(n), clusters(n);
        std::vector<double> advances(n);
        const size_t got = t_.shape(t_.user_data, face.id, utf8.data(), utf8.size(), px,
                                    glyphs.data(), advances.data(), clusters.data(), n);
        const size_t count = got < n ? got : n;
        out->reserve(count);
        for (size_t i = 0; i < count; ++i) {
            ShapedGlyph g;
            g.glyph = glyphs[i];
            g.x_advance = advances[i];
            g.cluster = clusters[i];
            out->push_back(g);
        }
    }

private:
    weva_font_backend t_;
    FontInterface* fallback_;
};

} // namespace

// The one type the opaque handle points at.
struct weva_document {
    weva_config config{};
    SymbolTable symbols;
    Ref<Document> doc;
    std::vector<std::unique_ptr<Stylesheet>> sheets;
    StyleMap styles;
    BoxTree tree;
    LayoutContext ctx;
    MonoFontMetrics metrics;
    StubFont font;
    GlyphAtlas atlas;
    CollectingBackend backend;
    // Set when a host registered its own; the built-ins above stay as the
    // fallback each adapter defers to for any function the host left null.
    std::unique_ptr<HostRenderBackend> host_render;
    std::unique_ptr<HostFontBackend> host_font;
    // Measurement follows the host's face too. Without this, layout would
    // measure with the stub's advances while paint drew the host's glyphs, and
    // the text would drift off the line boxes laid out for it.
    std::unique_ptr<FontInterfaceMetrics> host_metrics;
    FaceHandle face = StubFont::builtin();
    BoxId root = kNoBox;

    RenderInterface* render_backend() {
        return host_render ? static_cast<RenderInterface*>(host_render.get()) : &backend;
    }
    FontInterface* font_backend() {
        return host_font ? static_cast<FontInterface*>(host_font.get()) : &font;
    }
    const FontMetrics& metrics_backend() const {
        return host_metrics ? static_cast<const FontMetrics&>(*host_metrics) : metrics;
    }

    // Element handles are indices into this, rebuilt on every load. A stale
    // handle indexes out of range and is rejected; a stale pointer would be
    // undefined behaviour.
    std::vector<Element*> elements;

    // The POD views handed across the boundary. Members, so they outlive the
    // call that returns them and are replaced wholesale on the next update.
    std::vector<weva_draw> draw_views;
    std::vector<weva_texture> texture_views;

    void index_elements(Element& e) {
        elements.push_back(&e);
        for (const Ref<Node>& c : e.children()) {
            if (c->node_type() == NodeType::Element) {
                index_elements(static_cast<Element&>(const_cast<Node&>(*c)));
            }
        }
    }
    Element* element_at(weva_element_t h) const {
        return h < elements.size() ? elements[h] : nullptr;
    }
};

extern "C" {

uint32_t weva_abi_version(void) {
    return (static_cast<uint32_t>(WEVA_ABI_VERSION_MAJOR) << 16) | WEVA_ABI_VERSION_MINOR;
}

void weva_document_set_render_backend(weva_document_t doc, const weva_render_backend* backend) {
    if (!doc) return;
    if (!backend) { doc->host_render.reset(); return; }
    doc->host_render = std::make_unique<HostRenderBackend>(*backend, &doc->backend);
}

void weva_document_set_font_backend(weva_document_t doc, const weva_font_backend* backend,
                                    uint64_t face) {
    if (!doc) return;
    if (!backend) {
        doc->host_font.reset();
        doc->host_metrics.reset();
        doc->face = StubFont::builtin();
        return;
    }
    doc->host_font = std::make_unique<HostFontBackend>(*backend, &doc->font);
    // A zero face means "use whatever the backend's load_face returned", which
    // a host that has only one face can leave alone.
    doc->face = face ? FaceHandle{face} : StubFont::builtin();
    doc->host_metrics = std::make_unique<FontInterfaceMetrics>(doc->host_font.get(), doc->face);
}

weva_document_t weva_document_create(const weva_config* config) {
    auto* d = new weva_document();
    if (config) d->config = *config;
    if (d->config.viewport_width <= 0) d->config.viewport_width = 1920;
    if (d->config.viewport_height <= 0) d->config.viewport_height = 1080;
    if (d->config.root_font_size <= 0) d->config.root_font_size = 16;
    d->ctx.viewport_width_px = d->config.viewport_width;
    d->ctx.viewport_height_px = d->config.viewport_height;
    d->ctx.root_font_size_px = d->config.root_font_size;

    if (d->config.use_user_agent_stylesheet) {
        auto ua = std::make_unique<Stylesheet>();
        CssParseError err;
        if (parse_stylesheet(user_agent_stylesheet_source(), false, ua.get(), &err)) {
            d->styles.engine.add_stylesheet(ua.get(), DeclarationOrigin::UserAgent);
            d->sheets.push_back(std::move(ua));
        }
    }
    return d;
}

void weva_document_destroy(weva_document_t doc) { delete doc; }

weva_status weva_document_load_html(weva_document_t doc, const char* html, size_t length) {
    if (!doc || (!html && length > 0)) return WEVA_ERR_INVALID_ARGUMENT;
    HtmlParseError err;
    ParseOptions opts;
    opts.strict = false;
    doc->doc = parse_html(std::string_view(html ? html : "", length), &doc->symbols, opts, &err);
    if (!doc->doc) return WEVA_ERR_PARSE;

    doc->elements.clear();
    for (const Ref<Node>& c : doc->doc->children()) {
        if (c->node_type() == NodeType::Element) {
            doc->index_elements(static_cast<Element&>(const_cast<Node&>(*c)));
        }
    }
    return WEVA_OK;
}

weva_status weva_document_add_css(weva_document_t doc, const char* css, size_t length) {
    if (!doc || (!css && length > 0)) return WEVA_ERR_INVALID_ARGUMENT;
    auto sheet = std::make_unique<Stylesheet>();
    CssParseError err;
    if (!parse_stylesheet(std::string_view(css ? css : "", length), false, sheet.get(), &err)) {
        return WEVA_ERR_PARSE;
    }
    doc->styles.engine.add_stylesheet(sheet.get(), DeclarationOrigin::Author);
    doc->sheets.push_back(std::move(sheet));
    return WEVA_OK;
}

void weva_document_set_viewport(weva_document_t doc, int width, int height) {
    if (!doc || width <= 0 || height <= 0) return;
    doc->config.viewport_width = width;
    doc->config.viewport_height = height;
    doc->ctx.viewport_width_px = width;
    doc->ctx.viewport_height_px = height;
}

weva_status weva_document_update(weva_document_t doc, double dt_seconds) {
    (void)dt_seconds;   // animations are not ported yet
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    if (!doc->doc) return WEVA_ERR_NOT_FOUND;

    doc->styles.clear();
    for (const Ref<Node>& c : doc->doc->children()) {
        if (c->node_type() == NodeType::Element) {
            doc->styles.walk(static_cast<const Element&>(*c), nullptr);
        }
    }

    doc->tree.reset();
    BoxBuilder builder(&doc->tree, &doc->styles);
    doc->root = builder.build_document(*doc->doc);
    if (doc->root == kNoBox) return WEVA_ERR_INTERNAL;

    BlockLayout block(&doc->tree, doc->ctx, &doc->metrics_backend());
    block.layout_root(doc->root, doc->ctx.viewport_width_px, doc->ctx.viewport_height_px);
    run_positioning(&doc->tree, doc->root, doc->ctx, &block);

    doc->backend.begin_frame();
    PaintContext paint;
    paint.backend = doc->render_backend();
    paint.font = doc->font_backend();
    paint.atlas = &doc->atlas;
    paint.face = doc->face;
    paint_tree(doc->tree, doc->root, doc->ctx, paint);

    // With a host backend registered the host issued its own draws, so there
    // is no collected list to publish and the accessors correctly report none.
    //
    // The POD views point straight at the collected buffers: nothing is copied
    // across the boundary, which is what the documented lifetime buys.
    doc->draw_views.clear();
    doc->draw_views.reserve(doc->backend.draws.size());
    for (const auto& d : doc->backend.draws) {
        weva_draw v{};
        v.vertices = reinterpret_cast<const weva_vertex*>(d.vertices.data());
        v.vertex_count = d.vertices.size();
        v.indices = d.indices.data();
        v.index_count = d.indices.size();
        v.texture_id = d.texture;
        if (d.scissor) {
            v.has_scissor = 1;
            v.scissor_x = d.scissor->x;
            v.scissor_y = d.scissor->y;
            v.scissor_width = d.scissor->width;
            v.scissor_height = d.scissor->height;
        }
        doc->draw_views.push_back(v);
    }
    doc->texture_views.clear();
    for (const auto& kv : doc->backend.textures) {
        weva_texture t{};
        t.id = kv.first;
        t.rgba = kv.second.first.data();
        t.width = kv.second.second.x;
        t.height = kv.second.second.y;
        doc->texture_views.push_back(t);
    }
    return WEVA_OK;
}

const weva_draw* weva_document_draws(weva_document_t doc, size_t* out_count) {
    if (out_count) *out_count = doc ? doc->draw_views.size() : 0;
    return doc && !doc->draw_views.empty() ? doc->draw_views.data() : nullptr;
}

const weva_texture* weva_document_textures(weva_document_t doc, size_t* out_count) {
    if (out_count) *out_count = doc ? doc->texture_views.size() : 0;
    return doc && !doc->texture_views.empty() ? doc->texture_views.data() : nullptr;
}

weva_element_t weva_document_query(weva_document_t doc, const char* selector) {
    if (!doc || !doc->doc || !selector) return WEVA_ELEMENT_NONE;
    CompiledSelector compiled;
    SelectorParseError err;
    if (!parse_selector(selector, &compiled, &err)) return WEVA_ELEMENT_NONE;
    NullStateProvider state;
    for (size_t i = 0; i < doc->elements.size(); ++i) {
        if (selector_matches(compiled, *doc->elements[i], state)) {
            return static_cast<weva_element_t>(i);
        }
    }
    return WEVA_ELEMENT_NONE;
}

weva_status weva_element_bounds(weva_document_t doc, weva_element_t element, double* out_x,
                                double* out_y, double* out_width, double* out_height) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    const Element* e = doc->element_at(element);
    if (!e) return WEVA_ERR_NOT_FOUND;
    for (int i = 0; i < doc->tree.size(); ++i) {
        const Box& b = doc->tree[i];
        if (b.element != e) continue;
        double ax = 0, ay = 0;
        absolute_position(doc->tree, i, &ax, &ay);
        if (out_x) *out_x = ax;
        if (out_y) *out_y = ay;
        if (out_width) *out_width = b.width;
        if (out_height) *out_height = b.height;
        return WEVA_OK;
    }
    // The element exists but generated no box — `display: none`, or the
    // document has not been updated yet.
    return WEVA_ERR_NOT_FOUND;
}

weva_status weva_element_set_attribute(weva_document_t doc, weva_element_t element,
                                       const char* name, const char* value) {
    if (!doc || !name) return WEVA_ERR_INVALID_ARGUMENT;
    Element* e = doc->element_at(element);
    if (!e) return WEVA_ERR_NOT_FOUND;
    if (value) e->set_attribute(name, value);
    else e->remove_attribute(name);
    return WEVA_OK;
}

size_t weva_element_text(weva_document_t doc, weva_element_t element, char* buffer,
                         size_t capacity) {
    if (!doc) return 0;
    const Element* e = doc->element_at(element);
    if (!e) return 0;
    std::string text;
    // Depth-first, in document order, so the result reads as the element does.
    struct Walk {
        static void go(const Node& n, std::string* out) {
            for (const Ref<Node>& c : n.children()) {
                if (c->node_type() == NodeType::Text) {
                    *out += static_cast<const TextNode&>(*c).data();
                } else if (c->node_type() == NodeType::Element) {
                    go(*c, out);
                }
            }
        }
    };
    Walk::go(*e, &text);
    if (buffer && capacity > 0) {
        const size_t n = text.size() < capacity - 1 ? text.size() : capacity - 1;
        std::memcpy(buffer, text.data(), n);
        buffer[n] = '\0';
    }
    // The length that WOULD have been written, so a host sizes with one call
    // and fills with a second.
    return text.size();
}

} // extern "C"
