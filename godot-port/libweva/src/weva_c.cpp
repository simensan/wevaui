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
    BoxId root = kNoBox;

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

    BlockLayout block(&doc->tree, doc->ctx, &doc->metrics);
    block.layout_root(doc->root, doc->ctx.viewport_width_px, doc->ctx.viewport_height_px);
    run_positioning(&doc->tree, doc->root, doc->ctx, &block);

    doc->backend.begin_frame();
    PaintContext paint;
    paint.backend = &doc->backend;
    paint.font = &doc->font;
    paint.atlas = &doc->atlas;
    paint.face = StubFont::builtin();
    paint_tree(doc->tree, doc->root, doc->ctx, paint);

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
