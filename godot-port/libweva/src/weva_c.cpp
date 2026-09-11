#include "diagnostic_cycles.h"
#include "weva/image_store.h"
#include "weva/layout_dump.h"
#include "weva/shorthand.h"
#include "weva_c.h"
#include "weva/grapheme.h"
#include "weva/typeahead.h"

#include "weva/components.h"
#include "weva/block_layout.h"
#include "weva/box_builder.h"
#include "weva/animation.h"
#include "weva/cascade.h"
#include "weva/container_query_state.h"
#include "weva/font_interface.h"
#include "weva/font_metrics.h"
#include "weva/form_values.h"
#include "weva/form_state.h"
#include "weva/range_track.h"
#include "weva/glyph_atlas.h"
#include "weva/tessellate.h"
#include "weva/binding.h"
#include "weva/hit_test.h"
#include "weva/html.h"
#include "weva/invalidation.h"
#include "weva/incremental_layout.h"
#include "weva/keyframes.h"
#include "weva/paint.h"
#include "weva/positioning.h"
#include "weva/scrollbar.h"
#include "weva/text_classes.h"
#include "weva/selector.h"
#include "weva/user_agent_stylesheet.h"

#include <algorithm>
#include <cctype>
#include <memory>
#include <unordered_map>

#include <cstring>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <deque>
#include <map>
#include <functional>
#include <set>
#include <tuple>
#include <memory>
#include <optional>
#include <limits>
#include <string>
#include <vector>

namespace {

using namespace weva;

// Opt-in bounded diagnostics: no per-update I/O, and no storage in normal runs.
// Each thread retains its last 4096 updates and writes them only on teardown.
struct BufferedUpdateTrace {
    struct Entry { double stages[5]{}; double total = 0; uint64_t cycles = 0; };
    Entry entries[4096]{};
    uint64_t count = 0;
    void append(const Entry& entry) { entries[count++ % 4096] = entry; }
    ~BufferedUpdateTrace() {
        std::fprintf(stderr, "WEVA_STAGE_TRACE_BEGIN %llu\n", static_cast<unsigned long long>(count));
        const uint64_t begin = count > 4096 ? count - 4096 : 0;
        for (uint64_t i = begin; i < count; ++i) {
            const auto& e = entries[i % 4096];
            std::fprintf(stderr, "WEVA_STAGE_TRACE %llu %.6f %.6f %.6f %.6f %.6f %.6f\n",
                static_cast<unsigned long long>(i), e.stages[0], e.stages[1], e.stages[2],
                e.stages[3], e.stages[4], e.total);
            std::fprintf(stderr, "WEVA_STAGE_CYCLES %llu %llu\n",
                static_cast<unsigned long long>(i), static_cast<unsigned long long>(e.cycles));
        }
        std::fprintf(stderr, "WEVA_STAGE_TRACE_END\n");
    }
};
struct BufferedUpdateSample {
    using Clock = std::chrono::steady_clock;
    BufferedUpdateTrace* trace;
    BufferedUpdateTrace::Entry entry;
    Clock::time_point start;
    uint64_t start_cycles;
    BufferedUpdateSample(BufferedUpdateTrace* t, Clock::time_point time)
        : trace(t), start(time), start_cycles(t ? diagnostic_thread_cycles() : 0) {}
    ~BufferedUpdateSample() {
        if (!trace) return;
        entry.total = std::chrono::duration<double, std::milli>(Clock::now() - start).count();
        const uint64_t end_cycles = diagnostic_thread_cycles();
        if (start_cycles && end_cycles >= start_cycles) entry.cycles = end_cycles - start_cycles;
        trace->append(entry);
    }
};
BufferedUpdateTrace* buffered_update_trace() {
    static const bool enabled = std::getenv("WEVA_STAGE_TRACE") != nullptr;
    if (!enabled) return nullptr;
    thread_local std::unique_ptr<BufferedUpdateTrace> trace(new BufferedUpdateTrace);
    return trace.get();
}

// Collects the draw list instead of rasterizing it, so the host's own renderer
// issues the draws. This is the backend the ABI implies: the core still does
// every bit of the tessellation, and what crosses the boundary is triangles.
class CollectingBackend : public RenderInterface, public PaintReuse {
public:
    struct Draw {
        uint64_t version = 0;
        std::vector<Vertex> vertices;
        std::vector<uint32_t> indices;
        uint64_t texture = 0;
        std::optional<Recti> scissor;
        int32_t kind = WEVA_DRAW_GEOMETRY;
        BackdropEffect backdrop;
        RoundedRect rounded_rect;
    };

    void begin_frame() {
        if (!retired_textures_.empty()) retired_textures_.clear();
        previous_draws_.swap(draws);
        draws.clear();
        previous_ranges_.swap(ranges_);
        ranges_.assign(input_versions_.size(), Range{});
        geometry_.clear();
        next_ = 1;
        reused_boxes = 0;
    }

    // Input versions are propagated from the lifecycle's changed style/box
    // origins. Ancestors depend on child paint; descendants depend on parent
    // opacity, transforms, clips and inherited values.
    void prepare_reuse(const BoxTree& tree, TextureCache* textures,
                       const std::vector<BoxId>& changed, bool reset,
                       const IncrementalLayout& layout, bool subtree_layout) {
        tree_ = &tree;
        texture_cache_ = textures;
        ++input_serial_;
        track_inputs_.assign(tree.size(), false);
        for (BoxId id : layout.paint_candidates())
            if (tree.valid(id)) track_inputs_[id] = true;
        if (reset) {
            input_versions_.assign(tree.size(), input_serial_);
            previous_ranges_.clear();
            ranges_.clear();
            boundary_inputs_.clear();
        } else {
            input_versions_.resize(tree.size(), input_serial_);
            // Retained layout children keep their own input versions. Their
            // incoming position/effects/clip are validated when paint reaches
            // the boundary. Actual style and scroll inputs below still
            // invalidate descendants normally.
            if (subtree_layout) for (BoxId id : layout.replaced()) {
                invalidate_subtree(id, &layout.retained());
                for (BoxId p = tree[id].parent; p != kNoBox; p = tree[p].parent)
                    input_versions_[p] = input_serial_;
            }
            for (BoxId id : changed) {
                invalidate_subtree(id);
                for (BoxId p = tree[id].parent; p != kNoBox; p = tree[p].parent)
                    input_versions_[p] = input_serial_;
            }
        }
    }

    void begin_glyphs(const GlyphAtlas& atlas) override {
        same_glyph_slots_ = prepared_atlas_ == &atlas &&
                            prepared_slot_version_ == atlas.slot_version();
        prepared_atlas_ = &atlas;
        prepared_slot_version_ = atlas.slot_version();
    }
    bool reuse_glyphs(BoxId id) const override {
        if (!same_glyph_slots_ || static_cast<size_t>(id) >= previous_ranges_.size()) return false;
        const Range& r = previous_ranges_[id];
        return r.version && r.version == input_versions_[id];
    }
    void begin_paint(TextureHandle atlas) override {
        same_atlas_ = atlas.id == atlas_id_;
        atlas_id_ = atlas.id;
    }
    bool tracks_inputs(BoxId id) const override {
        return static_cast<size_t>(id) < track_inputs_.size() && track_inputs_[id];
    }
    bool replay(BoxId id, const PaintReplayInputs& inputs) override {
        auto entry = boundary_inputs_.try_emplace(id, inputs);
        if (entry.second || !(entry.first->second == inputs)) {
            static const bool log = std::getenv("WEVA_REUSE_LOG") != nullptr;
            if (log) {
                const auto& old = entry.first->second;
                const auto same_field = [&](auto field) {
                    PaintReplayInputs a, b;
                    a.*field = old.*field;
                    b.*field = inputs.*field;
                    return a == b;
                };
                const auto* element = (*tree_)[id].element;
                const auto classes = element ? element->get_attribute("class") : std::string_view();
                std::fprintf(stderr, "  paint boundary: box=%u class='%.*s' fresh=%d "
                    "position=(%.17g,%.17g)->(%.17g,%.17g) opacity=%d transform=%d "
                    "scissor=%d clip=%d filter=%d overflow=%d cb=%u/%u->%u/%u canvas=%u->%u\n", static_cast<unsigned>(id),
                    static_cast<int>(classes.size()), classes.data(), entry.second,
                    old.x, old.y, inputs.x, inputs.y, old.opacity != inputs.opacity,
                    old.transformed != inputs.transformed || (old.transformed && old.xform != inputs.xform),
                    !same_field(&PaintReplayInputs::scissor), !same_field(&PaintReplayInputs::clip),
                    !same_field(&PaintReplayInputs::filter), !same_field(&PaintReplayInputs::overflow),
                    static_cast<unsigned>(old.absolute_cb), static_cast<unsigned>(old.fixed_cb),
                    static_cast<unsigned>(inputs.absolute_cb), static_cast<unsigned>(inputs.fixed_cb),
                    static_cast<unsigned>(old.canvas_owner),
                    static_cast<unsigned>(inputs.canvas_owner));
            }
            invalidate_subtree(id);
            entry.first->second = inputs;
        }
        return replay(id);
    }
    bool replay(BoxId id) override {
        if (!same_atlas_ || static_cast<size_t>(id) >= previous_ranges_.size()) return false;
        const Range& r = previous_ranges_[id];
        if (!r.version || r.version != input_versions_[id]) return false;
        // A transient texture may have been released since the last frame.
        // Check before moving anything, so a miss has no observable output.
        for (size_t i = r.begin; i < r.end; ++i) {
            const uint64_t tex = previous_draws_[i].texture;
            if (tex && textures.find(tex) == textures.end()) return false;
        }
        const size_t start = draws.size();
        for (size_t i = r.begin; i < r.end; ++i) {
            if (texture_cache_) texture_cache_->retain(TextureHandle{previous_draws_[i].texture});
            draws.push_back(std::move(previous_draws_[i]));
        }
        relocate_ranges(id, r.begin, start);
        ++reused_boxes;
        return true;
    }
    void begin_box(BoxId id) override {
        ranges_[id] = Range{draws.size(), draws.size(), input_versions_[id]};
    }
    void end_box(BoxId id) override { ranges_[id].end = draws.size(); }
    size_t reused_boxes = 0;

    GeometryHandle compile_geometry(const std::vector<Vertex>& v,
                                    const std::vector<uint32_t>& i) override {
        const GeometryHandle h{next_++};
        geometry_[h.id] = {v, i};
        return h;
    }
    // Delegates, so the two entry points cannot drift apart: what a draw
    // looks like is decided in one place, below.
    void render_geometry(GeometryHandle g, Vec2 t, TextureHandle tex) override {
        auto it = geometry_.find(g.id);
        if (it == geometry_.end()) return;
        render_mesh(it->second.first, it->second.second, t, tex);
    }
    void release_geometry(GeometryHandle g) override { geometry_.erase(g.id); }

    // The whole of what render_geometry does, minus the map.
    void render_mesh(std::vector<Vertex> vertices, std::vector<uint32_t> indices, Vec2 t,
                     TextureHandle tex) override {
        Draw d;
        d.vertices = std::move(vertices);
        d.indices = std::move(indices);
        // Baked in here rather than passed through, as render_geometry does:
        // the ABI hands over geometry that is ready to upload.
        if (t.x != 0 || t.y != 0) {
            for (Vertex& v : d.vertices) {
                v.position.x += t.x;
                v.position.y += t.y;
            }
        }
        if (scissor_ && !inside_scissor(d.vertices)) {
            Mesh clipped;
            clip_triangles(d.vertices, d.indices,
                           Rect(scissor_->x, scissor_->y, scissor_->width, scissor_->height),
                           &clipped);
            if (clipped.empty()) return;
            d.vertices = std::move(clipped.vertices);
            d.indices = std::move(clipped.indices);
        }
        d.texture = tex.id;
        d.scissor = scissor_;
        d.version = next_draw_version_++;
        draws.push_back(std::move(d));
    }

    void render_rounded_rect(const RoundedRect& shape, const std::vector<Vertex>& v,
                             const std::vector<uint32_t>& i) override {
        render_rounded_rect_owned(shape, v, i);
    }
    void render_rounded_rect_owned(const RoundedRect& shape, std::vector<Vertex> v,
                                   std::vector<uint32_t> i) override {
        // The tessellation still travels, so a host that does not know this
        // kind uploads it and draws the same shape.
        Draw d;
        d.kind = WEVA_DRAW_ROUNDED_RECT;
        d.rounded_rect = shape;
        d.vertices = std::move(v);
        d.indices = std::move(i);
        if (scissor_) {
            // Only a shape that actually CROSSES the scissor needs cutting. A
            // scissor is in force for very nearly every box -- the viewport is
            // one -- so clipping unconditionally would downgrade every shape to
            // triangles and the description would never survive to a host.
            const double sx0 = scissor_->x, sy0 = scissor_->y;
            const double sx1 = sx0 + scissor_->width, sy1 = sy0 + scissor_->height;
            // A pixel of slack for the coverage ramp the tessellation carries.
            const bool inside = shape.x - 1 >= sx0 && shape.y - 1 >= sy0 &&
                                shape.x + shape.width + 1 <= sx1 &&
                                shape.y + shape.height + 1 <= sy1;
            if (!inside) {
                Mesh clipped;
                clip_triangles(d.vertices, d.indices, Rect(sx0, sy0, scissor_->width,
                                                           scissor_->height), &clipped);
                if (clipped.empty()) return;
                d.vertices = std::move(clipped.vertices);
                d.indices = std::move(clipped.indices);
                // Cut triangles no longer match the description, so the host
                // must use them rather than the shape.
                d.kind = WEVA_DRAW_GEOMETRY;
            }
        }
        d.scissor = scissor_;
        d.version = next_draw_version_++;
        draws.push_back(std::move(d));
    }

    void filter_backdrop(const std::vector<Vertex>& v, const std::vector<uint32_t>& i,
                         const BackdropEffect& effect) override {
        Draw d;
        d.kind = WEVA_DRAW_BACKDROP_FILTER;
        d.vertices = v;
        d.indices = i;
        d.backdrop = effect;
        // Clipped into the shape the same way geometry is, so a host that
        // cannot scissor still confines the effect correctly.
        if (scissor_ && !inside_scissor(d.vertices)) {
            Mesh clipped;
            clip_triangles(d.vertices, d.indices,
                           Rect(scissor_->x, scissor_->y, scissor_->width, scissor_->height),
                           &clipped);
            if (clipped.empty()) return;
            d.vertices = std::move(clipped.vertices);
            d.indices = std::move(clipped.indices);
        }
        d.scissor = scissor_;
        d.version = next_draw_version_++;
        draws.push_back(std::move(d));
    }

    TextureHandle load_texture(std::string_view, Vec2i* out_size) override {
        if (out_size) *out_size = {0, 0};
        return {};
    }
    TextureHandle generate_texture(const std::vector<uint8_t>& rgba, Vec2i size) override {
        if (size.x <= 0 || size.y <= 0) return {};
        // Texture ids are never reused within a document: the host caches
        // uploads by id across frames, and the atlas outlives any one frame.
        // Geometry ids restart with every frame; textures must not.
        const TextureHandle h{next_texture_++};
        textures[h.id] = {rgba, size};
        return h;
    }
    void release_texture(TextureHandle t) override { textures.erase(t.id); }
    // C ABI pixel views remain valid until the next update, including across
    // repeated renderer registrations. Transfer nodes without copying pixels
    // before the old renderer releases its handles; begin_frame retires them.
    void retain_published_textures() { retired_textures_.merge(textures); }
    void set_scissor(const Recti* r) override {
        if (r) scissor_ = *r;
        else scissor_.reset();
    }

    std::vector<Draw> draws;
    std::map<uint64_t, std::pair<std::vector<uint8_t>, Vec2i>> textures;

private:
    decltype(textures) retired_textures_;
    struct Range { size_t begin = 0, end = 0; uint64_t version = 0; };
    const BoxTree* tree_ = nullptr;
    TextureCache* texture_cache_ = nullptr;
    uint64_t input_serial_ = 0, atlas_id_ = 0;
    uint64_t next_draw_version_ = 1;
    bool same_atlas_ = false;
    const GlyphAtlas* prepared_atlas_ = nullptr;
    uint64_t prepared_slot_version_ = 0;
    bool same_glyph_slots_ = false;
    std::vector<uint64_t> input_versions_;
    std::vector<bool> track_inputs_;
    std::unordered_map<BoxId, PaintReplayInputs> boundary_inputs_;
    std::vector<Range> ranges_, previous_ranges_;
    std::vector<Draw> previous_draws_;
    void invalidate_subtree(BoxId id, const std::vector<BoxId>* retained = nullptr) {
        if (retained && std::find(retained->begin(), retained->end(), id) != retained->end()) return;
        input_versions_[id] = input_serial_;
        for (BoxId c : tree_->children(id)) invalidate_subtree(c, retained);
    }
    void relocate_ranges(BoxId id, size_t old_start, size_t new_start) {
        if (static_cast<size_t>(id) < previous_ranges_.size()) {
            const Range& old = previous_ranges_[id];
            if (old.version) ranges_[id] = {old.begin - old_start + new_start,
                                           old.end - old_start + new_start, old.version};
        }
        for (BoxId c : tree_->children(id)) relocate_ranges(c, old_start, new_start);
    }
    // True when every vertex is within the scissor, so clipping would return
    // the triangles unchanged. The margin covers the coverage ramp a feathered
    // edge carries past its nominal bounds.
    bool inside_scissor(const std::vector<Vertex>& v) const {
        if (!scissor_) return true;
        const float x0 = static_cast<float>(scissor_->x) - 1;
        const float y0 = static_cast<float>(scissor_->y) - 1;
        const float x1 = static_cast<float>(scissor_->x + scissor_->width) + 1;
        const float y1 = static_cast<float>(scissor_->y + scissor_->height) + 1;
        for (const Vertex& vert : v) {
            if (vert.position.x < x0 || vert.position.x > x1 || vert.position.y < y0 ||
                vert.position.y > y1) {
                return false;
            }
        }
        return true;
    }

    std::map<uint64_t, std::pair<std::vector<Vertex>, std::vector<uint32_t>>> geometry_;
    uint64_t next_ = 1;
    uint64_t next_texture_ = 1;
    std::optional<Recti> scissor_;
};

// Which elements are hovered, pressed and focused.
//
// The cascade could always MATCH :hover and its relatives -- ElementState
// carries the bits and the matcher takes a provider -- but the document handed
// it NullStateProvider, so every one of those rules matched nothing. This is
// what the host's pointer drives.
//
// The states apply to a CHAIN, not an element: CSS 2.1 §5.11.3 puts :hover on
// the element under the pointer and on every ancestor of it, which is what
// makes `.card:hover .title` work when the pointer is over the title. :active
// and :focus-within behave the same way; :focus does not.
std::string input_type_of(const Element& e) {
    if (e.tag_name() != "input") return std::string(e.tag_name());
    return std::string(form_input_type(e));
}

// Replaces an element's text, leaving its other children where they are. The
// new text goes back WHERE THE OLD TEXT WAS, not at the end: for
// `<div>label<span>*</span></div>` appending would put the label after the
// icon and quietly reorder the row.
bool replace_text(Element& e, std::string_view text) {
    // Games often push a HUD model every frame, including unchanged labels.
    // Match the direct text this API replaces, not descendant text (icons and
    // other element children must stay intact). A binding source is different
    // input even when its current displayed text happens to match the write.
    const TextNode* only_text = nullptr;
    size_t text_count = 0;
    for (const Ref<Node>& child : e.children()) {
        if (child->node_type() != NodeType::Text) continue;
        only_text = static_cast<const TextNode*>(child.get());
        ++text_count;
    }
    if ((!text_count && text.empty()) ||
        (text_count == 1 && !text.empty() && only_text->data() == text && only_text->source() == text))
        return false;
    // Collected first: removing while iterating the child list steps off it.
    std::vector<Node*> stale;
    Node* anchor = nullptr;
    bool seen_text = false;
    for (const Ref<Node>& c : e.children()) {
        if (c->node_type() == NodeType::Text) {
            stale.push_back(c.get());
            seen_text = true;
        } else if (seen_text && !anchor) {
            anchor = c.get();
        }
    }
    for (Node* n : stale) e.remove_child(n);
    if (text.empty()) return true;
    Ref<TextNode> node = make_ref<TextNode>(text);
    if (anchor) e.insert_before(node.get(), anchor);
    else e.append_child(node.get());
    return true;
}

// The disabled form control at or above an element, if any. Descendant content
// must not activate or focus its disabled control; hover remains observable.
const Element* disabled_ancestor(const Element* e) {
    for (const Node* n = e; n; n = n->parent()) {
        if (n->node_type() != NodeType::Element) continue;
        const Element& candidate = static_cast<const Element&>(*n);
        const std::string_view tag = candidate.tag_name();
        // Fieldsets disable controls, not arbitrary links/content; the control
        // helper also honors each ancestor fieldset's first legend exception.
        if (tag != "fieldset" && form_is_disabled(candidate)) return &candidate;
    }
    return nullptr;
}

// The selected range of a field's value, low end first. Empty when there is no
// selection, which is the usual case.
struct Selection {
    int from = 0, to = 0;
    bool empty() const { return from >= to; }
};

// Whether typing into this control edits it. Buttons and marks do not take
// text however focused they are.
bool is_text_field(const Element& e) {
    const std::string_view tag = e.tag_name();
    if (tag == "textarea") return true;
    if (tag != "input") return false;
    const std::string type = input_type_of(e);
    return type == "text" || type == "password" || type == "search" || type == "email" ||
           type == "url" || type == "tel" || type == "number";
}

// HTML accepts an initial signed integer, including leading ASCII space and
// a plus sign. Negative values and values outside the signed IDL range mean
// no limit. Number inputs do not support maxlength.
int text_max_length(const Element& field) {
    return form_text_length_limit(field, "maxlength");
}

size_t utf16_length(std::string_view text) {
    size_t units = 0;
    for (size_t at = 0, bytes = 0; at < text.size(); at += bytes)
        units += utf8_at(text, at, &bytes) > 0xffff ? 2 : 1;
    return units;
}

// Only user insertion is limited; markup, script values and undo snapshots
// can exceed maxlength. The returned view uses incoming or caller-owned
// scratch storage, so ordinary typing requires no additional allocation.
std::string_view user_text(const Element& field, std::string_view current, size_t from, size_t to,
                           std::string_view incoming, std::string* scratch) {
    if (field.tag_name() == "input" && form_input_type(field) == "number")
        return form_number_edit_text(current, from, to, incoming, *scratch);
    const bool multiline = field.tag_name() == "textarea";
    if (incoming.find_first_of("\r\n") != std::string_view::npos) {
        if (!multiline)
            while (!incoming.empty() && (incoming.back() == '\r' || incoming.back() == '\n')) incoming.remove_suffix(1);
        scratch->clear();
        scratch->reserve(incoming.size());
        for (size_t i = 0; i < incoming.size(); ++i) {
            const char c = incoming[i];
            if (c == '\r' && i + 1 < incoming.size() && incoming[i + 1] == '\n') ++i;
            scratch->push_back(c == '\r' || c == '\n' ? (multiline ? '\n' : ' ') : c);
        }
        incoming = *scratch;
    }
    const int limit = text_max_length(field);
    if (limit < 0) return incoming;
    const size_t base = utf16_length(current.substr(0, from)) + utf16_length(current.substr(to));
    size_t room = static_cast<size_t>(limit) > base ? static_cast<size_t>(limit) - base : 0;
    size_t at = 0;
    while (at < incoming.size()) {
        size_t bytes = 0;
        const size_t units = utf8_at(incoming, at, &bytes) > 0xffff ? 2 : 1;
        if (units > room) break;
        room -= units;
        at += bytes;
    }
    return incoming.substr(0, at);
}

std::string field_value(const Element& e) {
    return std::string(e.form_edit_value());
}

size_t field_value_size(const Element& e) {
    return e.form_edit_value().size();
}

bool field_value_equals(const Element& e, std::string_view value) {
    return e.form_edit_value() == value;
}

struct InteractionState : ElementStateProvider {
    std::vector<const Element*> hover_chain;
    std::vector<const Element*> active_chain;
    const Element* focused = nullptr;
    std::vector<const Element*> focus_chain;   // the focused element's ancestors
    int64_t version_ = 0;
    // Where the cursor sits in the focused field, in BYTES, and how long since
    // it last changed -- the blink restarts on every edit and every move, which
    // is what makes a caret readable while you type.
    int caret = 0;
    double caret_age = 0;
    double text_scroll_x = 0;
    bool caret_downstream = false;
    const Element* vertical_owner = nullptr;
    uint64_t vertical_version = 0;
    int vertical_index = -1;
    double vertical_x = 0;
    // The other end of the selection, or -1 when there is none. A selection is
    // a caret that remembers where it started: every move either drags this
    // along (unshifted) or leaves it where it was (shifted), which is the whole
    // of the behaviour.
    int anchor = -1;
    bool composing = false;
    int composition_from = 0, composition_to = 0;

    ElementState state_of(const Element& e) const override {
        uint32_t bits = 0;

        // Form state. The matcher reads :checked, :disabled and
        // :placeholder-shown off this provider -- it has no other channel --
        // and its own comment said they were waiting for a forms layer. They
        // are facts about the DOM rather than about the pointer, but this is
        // where the cascade asks for them, and the shape cache already folds
        // every attribute into its key so caching stays sound.
        const std::string_view tag = e.tag_name();
        if (e.is_modal()) bits |= static_cast<uint32_t>(ElementState::Modal);
        if (e.is_popover_open()) bits |= static_cast<uint32_t>(ElementState::PopoverOpen);
        if (form_is_disabled(e)) {
            bits |= static_cast<uint32_t>(ElementState::Disabled);
        }
        if ((tag == "input" && (form_input_type(e) == "checkbox" || form_input_type(e) == "radio") && e.form_checked()) ||
            (tag == "option" && e.form_selected())) {
            bits |= static_cast<uint32_t>(ElementState::Checked);
        }
        // :placeholder-shown is true only while the field is EMPTY, which is
        // the whole point of it -- it is how a floating label knows to float.
        if ((tag == "input" || tag == "textarea") && !e.get_attribute("placeholder").empty() &&
            e.form_edit_value().empty()) {
            bits |= static_cast<uint32_t>(ElementState::PlaceholderShown);
        }

        for (const Element* h : hover_chain) {
            if (h == &e) { bits |= static_cast<uint32_t>(ElementState::Hover); break; }
        }
        for (const Element* a : active_chain) {
            if (a == &e) { bits |= static_cast<uint32_t>(ElementState::Active); break; }
        }
        if (focused == &e) {
            // A host that moves focus is doing so deliberately; there is no
            // heuristic here about whether the ring should show.
            bits |= static_cast<uint32_t>(ElementState::Focus) |
                    static_cast<uint32_t>(ElementState::FocusVisible);
        }
        for (const Element* f : focus_chain) {
            if (f == &e) { bits |= static_cast<uint32_t>(ElementState::FocusWithin); break; }
        }
        return static_cast<ElementState>(bits);
    }
    int64_t version() const override { return version_; }

    // The element and its ancestors, innermost first.
    static void chain_of(const Element* e, std::vector<const Element*>* out) {
        out->clear();
        for (const Node* n = e; n; n = n->parent()) {
            if (n->node_type() != NodeType::Element) continue;
            out->push_back(static_cast<const Element*>(n));
        }
    }
};

// The selected range of a value of this length, low end first.
Selection selection_of(const InteractionState& st, size_t length) {
    Selection sel;
    if (st.anchor < 0) return sel;
    const int a = std::clamp(st.anchor, 0, static_cast<int>(length));
    const int b = std::clamp(st.caret, 0, static_cast<int>(length));
    sel.from = std::min(a, b);
    sel.to = std::max(a, b);
    return sel;
}

CaretState caret_for(const InteractionState& st) {
    CaretState c;
    if (st.focused && is_text_field(*st.focused)) {
        c.element = st.focused;
        c.index = std::max(0, st.caret);
        c.downstream = st.caret_downstream;
        c.text_scroll_x = st.text_scroll_x;
        // Half a second lit, half dark. Moving it resets the age, so the
        // cursor is never invisible at the moment you are steering it.
        c.visible = std::fmod(st.caret_age, 1.0) < 0.5;
        // The idle path needs only the length; copying a long value here
        // allocated on every frame while a text field had focus.
        const Selection sel = selection_of(st, field_value_size(*st.focused));
        c.selection_from = static_cast<size_t>(sel.from);
        c.selection_to = static_cast<size_t>(sel.to);
        if (st.composing) {
            c.composition_from = static_cast<size_t>(st.composition_from);
            c.composition_to = static_cast<size_t>(st.composition_to);
            c.visible = true;
        }
    } else {
        c.visible = false;
    }
    return c;
}


// One property on its way from one value to another.
//
// CSS Transitions L1. The engine already knows precisely which properties
// changed on which element -- ComputedStyle::differs_from reports the ids, and
// the incremental update holds both the old style and the new one at the
// moment of the swap. That is exactly the event a transition starts on, so
// this hangs off the machinery already there rather than watching for changes
// of its own.
struct RunningTransition {
    int property_id = -1;
    std::string from;      // the displayed value when it started, mid-flight or not
    std::string to;        // the cascaded value it is heading for
    std::string reversing_start;
    double reversing_factor = 1;
    double elapsed = 0;
    double delay = 0;
    double duration = 0;
    Easing easing;

    // Where it is now. Before the delay it holds `from`; after the duration it
    // IS `to`, exactly -- a transition that lands near its declared value
    // rather than on it leaves the document subtly wrong for good.
    std::string value_now(bool* finished) const {
        const double t = elapsed - delay;
        if (t < 0) { *finished = false; return from; }
        if (t >= duration) { *finished = true; return to; }
        std::string out;
        if (!interpolate_css(from, to, easing(t / duration), &out)) { *finished = true; return to; }
        *finished = false;
        return out;
    }
};

struct StyleMap : StyleProvider {
    CascadeEngine engine;
    InteractionState state;
    std::vector<std::unique_ptr<ComputedStyle>> owned;
    std::set<const ComputedStyle*> retired;
    void release_retired() {
        if (retired.empty()) return;
        owned.erase(std::remove_if(owned.begin(), owned.end(), [&](const auto& style) {
            return retired.count(style.get()) != 0;
        }), owned.end());
        retired.clear();
    }
    std::map<const Element*, ComputedStyle*> by_element;
    struct ControlInput {
        int64_t version = -1;
        bool listbox = false;
        int64_t label_version = 0;
        ElementState styled_state = ElementState::None;
        int styled_validity = -2; // Validity input consumed by the last cascade.
        int styled_range = -1; // Range input consumed by the last cascade, not paint.
        std::string_view input_type; // Canonical static string, independent of DOM storage.
    };
    std::map<const Element*, ControlInput> form_input_versions;
    std::set<const Element*> pending_control_inputs;
    // Direct text and image-source writes are layout inputs to their owner, even when its
    // computed declarations stay equal. Retain the queue's storage across
    // frames; entries have the same DOM lifetime as by_element.
    std::vector<const Element*> pending_content_inputs;
    std::vector<const Element*> pending_visual_inputs;
    std::vector<const Element*> pending_box_inputs;
    void repaint(const Element* e) {
        if (e && std::find(pending_visual_inputs.begin(), pending_visual_inputs.end(), e) == pending_visual_inputs.end())
            pending_visual_inputs.push_back(e);
    }
    // Current/published keyboard-row versions. This visual input has no DOM
    // pseudo-class: focus remains on the select, including during Ctrl+arrows.
    std::map<const Element*, std::pair<uint64_t, uint64_t>> list_input_versions;
    // ::before at index 0, ::after at 1 — only for hosts some rule targets.
    std::map<std::pair<const Element*, int>, ComputedStyle*> pseudo_by_element;

    // ---- incremental state -------------------------------------------------
    //
    // Styles are kept ACROSS updates, and each element's style object keeps its
    // address, because the box tree points at it. A pass computes into
    // `scratch` and swaps only when the result actually differs, so an element
    // nothing touched keeps its style, its version and its parsed-value cache.
    //
    // What the pass learned is left in `pending`, which is how the update
    // decides whether to rebuild the box tree, lay out again, or only repaint.
    ComputedStyle scratch;
    Invalidation pending = Invalidation::Boxes;
    int visited = 0;
    std::vector<std::pair<const ComputedStyle*, Invalidation>> changes;
    void note_change(const ComputedStyle* style, Invalidation kind) {
        pending = worst(pending, kind);
        for (auto& change : changes) {
            if (change.first == style) { change.second = worst(change.second, kind); return; }
        }
        changes.emplace_back(style, kind);
    }

    // Transitions in flight, by element. Cleared with the styles they belong
    // to, since both are keyed on elements of the current document.
    std::map<const Element*, std::vector<RunningTransition>> transitions;
    // Before-change rendering state survives parent-first cascade updates.
    // Hidden elements have no before-change style from which to transition.
    std::set<const Element*> transition_hidden;

    // @keyframes by name, collected from every sheet as it is added, and the
    // clock each element's animation is running against.
    std::map<std::string, KeyframeAnimation> keyframes;
    struct AnimationClock {
        std::string name;
        double elapsed = 0;
    };
    struct AnimationClocks {
        std::string names;
        std::vector<AnimationClock> entries;
    };
    std::map<const Element*, AnimationClocks> animation_clock;
    // Only the elements that actually name an animation. Walking every element
    // every frame to ask would give a document with nothing moving a per-frame
    // cost again, which is the whole thing the incremental work removed.
    std::set<const Element*> animated;
    // What the cascade said, for every property an animation has overwritten.
    //
    // An animating document does not restyle -- time cannot change what the
    // cascade would produce -- so when an animation stops holding a property
    // there is nothing to put the declared value back. This is that. Cleared
    // for an element whenever the cascade DOES run for it, since the values
    // are then freshly correct in the style itself.
    std::map<const Element*, std::map<int, std::string>> animation_saved;

    // What each animation last PUT ON SCREEN, which is a different question
    // from what the cascade last said. An animating element that is
    // re-cascaded -- because a key was pressed, a class toggled, a pointer
    // moved -- has its animated properties unwound to the declared values and
    // written back a moment later, and comparing the write against the
    // declared value calls that a change. It is not one: nothing moved. This
    // is what the write is compared against instead.
    struct AnimationValue {
        std::string shown;
        std::string pending;
        bool active = false;
    };
    std::map<const Element*, std::map<int, AnimationValue>> animation_shown;

    // Puts back what an animation overwrote, WITHOUT calling it a change.
    //
    // The live style holds the animation's current value; the cascade is about
    // to produce the declared one. Diffing those two reports a change that is
    // not one -- the animation will write its value straight back -- and the
    // report costs a relayout. Putting the declared value back first makes the
    // diff compare the cascade's last answer with the cascade's new one, which
    // is the only comparison that means anything.
    void unwind_animated(const Element& e, ComputedStyle* live) {
        auto it = animation_saved.find(&e);
        if (it == animation_saved.end()) return;
        for (const auto& kv : it->second) live->set(kv.first, kv.second);
        animation_saved.erase(it);
    }

    void restore_animated(const Element& e, ComputedStyle* live) {
        animation_shown.erase(&e);
        auto it = animation_saved.find(&e);
        if (it == animation_saved.end()) return;
        for (const auto& kv : it->second) {
            live->set(kv.first, kv.second);
            note_change(live, invalidation_for_property(kv.first));
        }
        animation_saved.erase(it);
    }
    bool any_animation = false;   // whether any element named one this pass

    bool animating() const {
        if (any_animation) return true;
        for (const auto& kv : transitions) {
            if (!kv.second.empty()) return true;
        }
        return false;
    }

    bool transition_ancestor_hidden(const Element& e) const {
        // walk() updates the parent's state before descending. Scoped walks
        // reuse that parent's last cascaded state, avoiding a full ancestor scan.
        for (const Node* node = e.parent(); node; node = node->parent()) {
            if (node->node_type() == NodeType::Element)
                return transition_hidden.count(static_cast<const Element*>(node)) != 0;
        }
        return false;
    }

    bool animation_displayed(const Element& e) const {
        static const int display_id = CssPropertyRegistry::instance().id_of("display");
        for (const Node* node = &e; node; node = node->parent()) {
            if (node->node_type() != NodeType::Element) continue;
            const auto* element = static_cast<const Element*>(node);
            const auto style = by_element.find(element);
            if (style == by_element.end()) continue;
            std::string_view display = style->second->get(display_id);
            // Cancellation uses the underlying display, independent of animated
            // output and of the order in which ancestor effects are sampled.
            const auto saved = animation_saved.find(element);
            if (saved != animation_saved.end()) {
                const auto base = saved->second.find(display_id);
                if (base != saved->second.end()) display = base->second;
            }
            if (display == "none") return false;
        }
        return true;
    }

    // Applies every @keyframes animation an element names.
    //
    // Animations sit ABOVE transitions in the cascade (CSS Cascade L5 §6.1),
    // so this runs after them and overwrites what they wrote -- an element
    // doing both shows the animation, which is what a browser does.
    void apply_animations(const Element& e, ComputedStyle* live, double dt) {
        const std::string_view requested_names = live->get("animation-name");
        if (requested_names.empty() || requested_names == "none" || !animation_displayed(e)) {
            animation_clock.erase(&e);
            restore_animated(e, live);
            return;
        }
        auto& clocks = animation_clock[&e];
        if (clocks.names != requested_names) {
            size_t count = 0;
            while (!nth(requested_names, count, false).empty()) ++count;
            std::vector<AnimationClock> next(count);
            std::vector<bool> consumed(clocks.entries.size(), false);
            // Chromium identifies animations by name and occurrence from the
            // start of the list. Match that browser behavior for duplicates;
            // it differs from the reverse matching in the CSS Animations draft.
            for (size_t i = 0; i < next.size(); ++i) {
                const auto name = nth(requested_names, i, false);
                if (name.empty() || name == "none") continue;
                for (size_t j = 0; j < clocks.entries.size(); ++j) {
                    if (!consumed[j] && clocks.entries[j].name == name) {
                        next[i] = std::move(clocks.entries[j]);
                        consumed[j] = true;
                        break;
                    }
                }
            }
            clocks.entries = std::move(next);
            clocks.names = requested_names;
        }
        const std::string_view names = clocks.names;
        bool has_definition = false;
        auto& composed = animation_shown[&e];
        for (auto& entry : composed) entry.second.active = false;

        for (size_t i = 0; i < clocks.entries.size(); ++i) {
            const std::string_view name = nth(names, i, false);
            if (name.empty()) break;
            if (name == "none") continue;
            auto& clock = clocks.entries[i];
            auto it = keyframes.find(std::string(name));
            if (it == keyframes.end()) {
                clock.name.clear();
                clock.elapsed = 0;
                continue;
            }
            has_definition = true;
            if (clock.name != name) {
                clock.name = name;
                clock.elapsed = 0;
            }
            const bool paused = nth(live->get("animation-play-state"), i) == "paused";
            if (!paused) clock.elapsed += dt;
            double duration = 0;
            if (!parse_time_seconds(nth(live->get("animation-duration"), i), &duration) ||
                duration < 0) {
                continue;
            }
            double delay = 0;
            parse_time_seconds(nth(live->get("animation-delay"), i), &delay);
            Easing easing;
            if (!parse_easing(nth(live->get("animation-timing-function"), i), &easing)) {
                parse_easing("ease", &easing);
            }
            const std::string_view direction = nth(live->get("animation-direction"), i);
            const std::string_view fill = nth(live->get("animation-fill-mode"), i);
            const std::string_view count_raw = nth(live->get("animation-iteration-count"), i);
            const double iterations =
                count_raw == "infinite" ? 1e30 : std::max(0.0, std::atof(std::string(count_raw).c_str()));

            const double elapsed = clock.elapsed - delay;
            double progress = 0;
            double cycle_index = 0;
            bool active = true;
            if (elapsed < 0) {
                if (fill != "backwards" && fill != "both") active = false;
            } else {
                const bool finished = duration == 0 || elapsed / duration >= iterations;
                if (finished) {
                    if (fill != "forwards" && fill != "both") active = false;
                    // The endpoint is the final fraction of the final cycle.
                    // An exact boundary belongs to the preceding cycle, except
                    // for zero iterations, which stays at the initial endpoint.
                    const double end_cycles = duration == 0 && count_raw == "infinite" ? 1 : iterations;
                    cycle_index = std::floor(end_cycles);
                    progress = end_cycles - cycle_index;
                    if (end_cycles > 0 && progress == 0) {
                        progress = 1;
                        cycle_index -= 1;
                    }
                } else {
                    const double cycles = elapsed / duration;
                    cycle_index = std::floor(cycles);
                    progress = cycles - cycle_index;
                }
            }
            // Direction also applies to backwards fill and the final fraction.
            const bool odd_cycle = std::fmod(cycle_index, 2.0) >= 1.0;
            if (direction == "reverse" ||
                (direction == "alternate" && odd_cycle) ||
                (direction == "alternate-reverse" && !odd_cycle)) {
                progress = 1 - progress;
            }
            if (active) {
                const double eased = easing(progress);
                for (const std::string& property : it->second.properties) {
                    const int id = CssPropertyRegistry::instance().id_of(property);
                    if (id < 0) continue;
                    std::string value;
                    if (!keyframe_value_at(it->second, property, eased, &value)) continue;
                    auto& result = composed[id];
                    result.pending = std::move(value);
                    result.active = true;
                }
            }
            // A delay without backwards fill still needs ticks to reach its
            // active interval. Paused effects retain their value without ticks.
            any_animation = any_animation || (!paused && elapsed < iterations * duration);
        }
        if (!has_definition) {
            // A name with no matching keyframes creates no CSS animation.
            // Removing its definition cancels it; later reappearance starts
            // a fresh timeline instead of resuming this element's old clock.
            animation_clock.erase(&e);
            restore_animated(e, live);
            return;
        }
        auto& saved = animation_saved[&e];
        for (auto it = composed.begin(); it != composed.end();) {
            const int id = it->first;
            auto& result = it->second;
            if (result.active) {
                if (saved.find(id) == saved.end()) saved[id] = std::string(live->get(id));
                live->set(id, result.pending);
                if (result.shown != result.pending) {
                    result.shown = result.pending;
                    note_change(live, invalidation_for_property(id));
                }
                ++it;
            } else {
                // Restore only properties no remaining effect supplies. Cascade
                // may already have refreshed the base value and cleared saved.
                const auto base = saved.find(id);
                if (base != saved.end()) {
                    live->set(id, base->second);
                    saved.erase(base);
                }
                if (result.shown != live->get(id)) {
                    note_change(live, invalidation_for_property(id));
                }
                it = composed.erase(it);
            }
        }
    }

    // Timing longhands repeat their entire list to match the number of effects.
    // Name/property lists use strict indexing so duplicate entries remain valid.
    static std::string_view nth(std::string_view list, size_t index, bool repeat_list = true) {
        size_t start = 0, count = 0;
        int depth = 0;
        for (size_t i = 0; i <= list.size(); ++i) {
            if (i < list.size()) {
                if (list[i] == '(') ++depth;
                else if (list[i] == ')') { if (depth > 0) --depth; }
                if (list[i] != ',' || depth != 0) continue;
            }
            std::string_view piece = list.substr(start, i - start);
            while (!piece.empty() && (piece.front() == ' ' || piece.front() == '	')) {
                piece.remove_prefix(1);
            }
            while (!piece.empty() && (piece.back() == ' ' || piece.back() == '	')) {
                piece.remove_suffix(1);
            }
            if (count == index) return piece;
            ++count;
            start = i + 1;
        }
        return repeat_list && count ? nth(list, index % count, false) : std::string_view{};
    }

    // How this property transitions on this style, or false when it does not.
    // The properties are read off the style being transitioned TO, which is
    // what CSS Transitions L1 §3 specifies -- so turning a transition on in a
    // :hover rule makes the way IN animate and the way out snap, exactly as it
    // does in a browser.
    static bool transition_index(const ComputedStyle& style, int id, size_t* matched) {
        const std::string_view names = style.get("transition-property");
        if (names.empty() || names == "none") return false;
        const std::string_view want = CssPropertyRegistry::instance().name_of(id);
        if (want.empty()) return false;
        size_t index = 0;
        bool found = false;
        // Property names contain no function arguments. Scan once rather than
        // repeatedly indexing from the start, without truncating long lists.
        for (size_t i = 0, start = 0; start < names.size(); ++i) {
            const size_t comma = names.find(',', start);
            const size_t end = comma == std::string_view::npos ? names.size() : comma;
            std::string_view entry = names.substr(start, end - start);
            const size_t first = entry.find_first_not_of(" \t\r\n\f");
            if (first != std::string_view::npos) {
                const size_t last = entry.find_last_not_of(" \t\r\n\f");
                entry = entry.substr(first, last - first + 1);
            }
            if (entry == "all" || entry == want) { index = i; found = true; }
            if (comma == std::string_view::npos) break;
            start = comma + 1;
        }
        if (found) *matched = index;
        return found;
    }

    static bool transition_for(const ComputedStyle& style, int id, double* duration,
                               double* delay, Easing* easing) {
        size_t index = 0;
        if (!transition_index(style, id, &index)) return false;
        if (!parse_time_seconds(nth(style.get("transition-duration"), index), duration)) return false;
        if (*duration < 0) return false;
        if (!parse_time_seconds(nth(style.get("transition-delay"), index), delay)) *delay = 0;
        if (*duration + *delay <= 0) return false;
        if (!parse_easing(nth(style.get("transition-timing-function"), index), easing)) {
            parse_easing("ease", easing);
        }
        return true;
    }

    // Advances every transition and writes what it currently shows into the
    // live style, so layout and paint read the animated value rather than the
    // cascaded one it is travelling towards.
    void advance(double dt) {
        // Animations run whether or not the clock moved: a paused frame must
        // still SHOW the value its animation is holding, and a fill mode holds
        // one before the animation has even started.
        any_animation = false;
        for (const Element* e : animated) {
            auto it = by_element.find(e);
            if (it != by_element.end()) apply_animations(*e, it->second, dt);
        }
        for (auto& kv : transitions) {
            auto it = by_element.find(kv.first);
            if (it == by_element.end()) { kv.second.clear(); continue; }
            ComputedStyle* live = it->second;
            std::vector<RunningTransition>& list = kv.second;
            if (list.empty()) continue;
            if (!animation_displayed(*kv.first)) {
                for (const RunningTransition& transition : list) {
                    if (live->get(transition.property_id) != transition.to) {
                        live->set(transition.property_id, transition.to);
                        note_change(live, invalidation_for_property(transition.property_id));
                    }
                }
                list.clear();
                continue;
            }
            if (dt <= 0) continue;
            for (size_t i = 0; i < list.size();) {
                list[i].elapsed += dt;
                bool finished = false;
                const std::string now = list[i].value_now(&finished);
                // Delays and stepped timing can hold an unchanged value for
                // many frames. Advance the clock without invalidating output.
                if (live->get(list[i].property_id) != now) {
                    live->set(list[i].property_id, now);
                    note_change(live, invalidation_for_property(list[i].property_id));
                }
                if (finished) list.erase(list.begin() + static_cast<long>(i));
                else ++i;
            }
        }
    }

    void begin_pass() {
        pending = Invalidation::None;
        visited = 0;
        changes.clear();
    }

    // The element set changed, so which boxes exist did too.
    void note_structural() { pending = worst(pending, Invalidation::Boxes); }

    // Everything this map holds about one element, dropped. Called when an
    // element leaves the DOM: every one of these is keyed on the POINTER, and
    // the node is freed the moment its parent lets go of it, so an entry left
    // behind is a stale key that a later allocation at the same address would
    // silently inherit.
    void forget(const Element* e) {
        pending_box_inputs.erase(std::remove(pending_box_inputs.begin(), pending_box_inputs.end(), e),
                                 pending_box_inputs.end());
        pending_visual_inputs.erase(std::remove(pending_visual_inputs.begin(), pending_visual_inputs.end(), e),
                                    pending_visual_inputs.end());
        pending_content_inputs.erase(std::remove(pending_content_inputs.begin(), pending_content_inputs.end(), e),
                                     pending_content_inputs.end());
        pending_control_inputs.erase(e);
        form_input_versions.erase(e);
        list_input_versions.erase(e);
        animation_shown.erase(e);
        const auto current = by_element.find(e);
        if (current != by_element.end()) retired.insert(current->second);
        for (int i = 0; i < 4; ++i) {
            const auto pseudo = pseudo_by_element.find({e, i});
            if (pseudo != pseudo_by_element.end()) retired.insert(pseudo->second);
        }
        by_element.erase(e);
        pseudo_by_element.erase({e, 0});
        pseudo_by_element.erase({e, 1});
        pseudo_by_element.erase({e, 2});
        pseudo_by_element.erase({e, 3});
        transitions.erase(e);
        transition_hidden.erase(e);
        animation_clock.erase(e);
        animated.erase(e);
        animation_saved.erase(e);
    }

    void clear() {
        pending_box_inputs.clear();
        pending_visual_inputs.clear();
        pending_content_inputs.clear();
        pending_control_inputs.clear();
        form_input_versions.clear();
        list_input_versions.clear();
        owned.clear();
        retired.clear();
        by_element.clear();
        pseudo_by_element.clear();
        transitions.clear();
        transition_hidden.clear();
        animation_clock.clear();
        animated.clear();
        animation_saved.clear();
        animation_shown.clear();
        changes.clear();
        pending = Invalidation::Boxes;
    }

    // Computes into `into`, and reports what the difference forces.
    void compute_into(ComputedStyle* into, const Element& e, const ComputedStyle* parent) {
        engine.compute(e, state, parent, &scratch);
        merge(into, &scratch);
    }

    std::vector<int> changed_property_ids;
    void merge(ComputedStyle* live, ComputedStyle* fresh, const Element* owner = nullptr) {
        // Reused only within this non-reentrant merge; no callback observes it.
        auto& changed = changed_property_ids;
        changed.clear();
        bool unattributed = false;
        if (!live->differs_from(*fresh, &changed, &unattributed)) {
            // The new target can equal a held step's displayed value, even
            // though the running transition still targets another value.
            if (owner && !transitions.empty()) {
                const auto existing = transitions.find(owner);
                if (existing != transitions.end()) {
                    auto& list = existing->second;
                    list.erase(std::remove_if(list.begin(), list.end(), [&](const RunningTransition& t) {
                        return fresh->get(t.property_id) != t.to;
                    }), list.end());
                }
            }
            return;
        }
        static const bool style_log = std::getenv("WEVA_STYLE_LOG") != nullptr;
        if (style_log && owner) {
            std::fprintf(stderr, "style %.*s#%.*s:", static_cast<int>(owner->tag_name().size()), owner->tag_name().data(),
                         static_cast<int>(owner->id().size()), owner->id().data());
            for (const int id : changed) std::fprintf(stderr, " %d(%.*s -> %.*s)", id,
                static_cast<int>(live->get(id).size()), live->get(id).data(),
                static_cast<int>(fresh->get(id).size()), fresh->get(id).data());
            std::fprintf(stderr, " unattributed=%d\n", unattributed);
        }

        // A transition starts here, where the value the element is SHOWING and
        // the value the cascade just produced are both in hand. Nothing else
        // in the engine has both.
        std::vector<RunningTransition>* running = nullptr;
        const bool can_transition = owner && transition_hidden.count(owner) == 0 &&
            fresh->get("display") != "none" && !transition_ancestor_hidden(*owner);
        // A fresh hidden style owns its latest target. Do not restore an older
        // running target over it when cancellation occurs later in advance().
        if (owner && !can_transition) transitions.erase(owner);
        if (can_transition) {
            const auto existing = transitions.find(owner);
            if (existing != transitions.end() && !existing->second.empty()) {
                running = &existing->second;
                running->erase(std::remove_if(running->begin(), running->end(), [&](const RunningTransition& t) {
                    size_t index = 0;
                    if (!transition_index(*fresh, t.property_id, &index)) return true;
                    // A duration change alone does not interrupt an existing
                    // transition. A new target must still qualify to animate.
                    if (fresh->get(t.property_id) == t.to) return false;
                    // A step can still display the new target while the old
                    // transition points elsewhere. Cancel instead of letting
                    // its next step overwrite the requested value.
                    if (fresh->get(t.property_id) == live->get(t.property_id)) return true;
                    double duration = 0, delay = 0;
                    Easing easing;
                    return !transition_for(*fresh, t.property_id, &duration, &delay, &easing);
                }), running->end());
            }
        }
        if (can_transition && !changed.empty()) {
            for (const int id : changed) {
                double duration = 0, delay = 0;
                Easing easing;
                if (!transition_for(*fresh, id, &duration, &delay, &easing)) continue;
                const std::string_view target = fresh->get(id);
                if (!running) running = &transitions[owner];
                // A property already travelling to this exact value is not a
                // new change -- it is the one in flight, seen from the outside.
                // Without this every frame would restart it and it would crawl
                // towards its target and never arrive.
                bool retarget = true;
                for (RunningTransition& t : *running) {
                    if (t.property_id != id) continue;
                    if (t.to == target) { retarget = false; break; }
                    // Reversed or redirected mid-flight: it carries on from
                    // where it is, which is what stops a hover flicker from
                    // snapping.
                    bool finished = false;
                    const std::string current = t.value_now(&finished);
                    if (target == t.reversing_start) {
                        const double progress = t.duration > 0 ?
                            std::clamp((t.elapsed - t.delay) / t.duration, 0.0, 1.0) : 1.0;
                        const double eased = t.elapsed < t.delay ? 0.0 : t.easing(progress);
                        // Match Chromium's clamping of negative overshoot;
                        // the draft's absolute-value rule differs here.
                        t.reversing_factor = std::clamp(eased * t.reversing_factor +
                            (1.0 - t.reversing_factor), 0.0, 1.0);
                        t.reversing_start = t.to;
                        duration *= t.reversing_factor;
                        if (delay < 0) delay *= t.reversing_factor;
                    } else {
                        t.reversing_start = current;
                        t.reversing_factor = 1;
                    }
                    t.from = current;
                    t.to = std::string(target);
                    t.elapsed = 0;
                    t.delay = delay;
                    t.duration = duration;
                    t.easing = easing;
                    retarget = false;
                    break;
                }
                if (!retarget) continue;
                RunningTransition t;
                t.property_id = id;
                t.from = std::string(live->get(id));
                t.reversing_start = t.from;
                t.to = std::string(target);
                t.delay = delay;
                t.duration = duration;
                t.easing = easing;
                running->push_back(std::move(t));
            }
        }

        // The address has to survive -- every box holds it -- so the contents
        // move rather than the object.
        std::swap(*live, *fresh);
        // The style now holds what the cascade just said, so anything an
        // animation had put aside describes an older answer.
        if (owner) animation_saved.erase(owner);

        // And the cascaded value is put back to what is actually on screen for
        // anything still in flight, or the first frame of a transition would
        // show its destination.
        if (running) {
            for (const RunningTransition& t : *running) {
                bool finished = false;
                live->set(t.property_id, t.value_now(&finished));
            }
        }

        if (unattributed) {
            note_change(live, Invalidation::Boxes);
            return;
        }
        for (const int id : changed) {
            note_change(live, invalidation_for_property(id));
            if (pending == Invalidation::Boxes) return;
        }
    }

    void consume_control_inputs(const Element& e, const ComputedStyle* raw) {
        if (e.form_version() != 0 || e.tag_name() == "select") {
            auto& seen = form_input_versions[&e];
            const bool listbox = e.tag_name() == "select" && select_is_listbox(e);
            if (seen.version != e.form_version()) {
                const auto input_type = e.tag_name() == "input" ? form_input_type(e) : std::string_view();
                // Authored CSS can keep every declaration identical across a
                // type change, while the control's exposed baseline changes.
                note_change(raw, seen.listbox != listbox ? Invalidation::Boxes :
                            e.tag_name() == "textarea" || seen.label_version != e.form_label_version() ||
                                seen.input_type != input_type
                                ? Invalidation::Layout : Invalidation::Paint);
                seen.version = e.form_version();
                seen.listbox = listbox;
                seen.label_version = e.form_label_version();
                seen.input_type = input_type;
            }
        }
        const auto list_input = list_input_versions.find(&e);
        if (list_input != list_input_versions.end() && list_input->second.first != list_input->second.second) {
            list_input->second.second = list_input->second.first;
            note_change(raw, Invalidation::Paint);
        }
    }

    void walk(const Element& e, const ComputedStyle* parent) {
        ++visited;
        ComputedStyle* raw = nullptr;
        auto it = by_element.find(&e);
        if (it == by_element.end()) {
            auto cs = std::make_unique<ComputedStyle>();
            raw = cs.get();
            owned.push_back(std::move(cs));
            by_element[&e] = raw;
            note_structural();   // an element that was not here before
            engine.compute(e, state, parent, raw);
        } else {
            raw = it->second;
            // Before the diff, not after: see unwind_animated.
            unwind_animated(e, raw);
            engine.compute(e, state, parent, &scratch);
            merge(raw, &scratch, &e);
        }
        if (raw->get("display") == "none" || transition_ancestor_hidden(e))
            transition_hidden.insert(&e);
        else transition_hidden.erase(&e);
        const std::string_view animation = raw->get("animation-name");
        // Live form state does not alter attributes or computed declarations.
        // Its own input version drives layout/paint, including clean controls
        // whose defaults were changed by a binding or a script.
        consume_control_inputs(e, raw);
        auto form_input = form_input_versions.find(&e);
        if (form_input != form_input_versions.end()) {
            form_input->second.styled_state = state.state_of(e);
            form_input->second.styled_validity = engine.has_validity_selectors() ? form_validity_selector_state(e) : -2;
            form_input->second.styled_range = engine.has_range_selectors() ? form_range_selector_state(e) : -1;
        }
        if (!animation.empty() && animation != "none") animated.insert(&e);
        else animated.erase(&e);

        // `backdrop` alongside the two content pseudos: it is computed the
        // same way, cached the same way, and the box builder asks for it by
        // the same call. Without it here the UA sheet's `::backdrop` rule
        // matched nothing and a modal dialog had no dim behind it.
        static constexpr std::string_view kPseudos[6] = {"before", "after", "backdrop", "marker",
                                                         "placeholder", "selection"};
        for (int i = 0; i < 6; ++i) {
            // The universal UA ::backdrop rule otherwise materialises a full
            // style for every element, although only top-layer hosts use it.
            // Keep a previously used style dormant while closed; reopening
            // recomputes it here before the box builder can read it. The DOM
            // attribute versions already make opening/closing rebuild boxes.
            if (i == 2 && !top_layer_host(e)) continue;
            // ::placeholder and ::selection are paint hooks on text fields:
            // the one place the engine draws a placeholder or a selection
            // band. An author's `::selection { ... }` is universal, so
            // without the gate every element would carry a style for it.
            const bool paint_pseudo = i >= 4;
            if (paint_pseudo && !text_field_host(e)) continue;
            auto pit = pseudo_by_element.find({&e, i});
            const bool had = pit != pseudo_by_element.end();
            const bool has = engine.compute_pseudo_element(e, kPseudos[i], state, *raw, &scratch);
            if (!has) {
                // A pseudo that stopped being generated takes its box with it;
                // a paint hook that went away only changes colours.
                if (had) {
                    retired.insert(pit->second);
                    pseudo_by_element.erase(pit);
                    if (paint_pseudo) pending = worst(pending, Invalidation::Paint);
                    else note_structural();
                }
                continue;
            }
            if (!had) {
                auto ps = std::make_unique<ComputedStyle>();
                std::swap(*ps, scratch);
                pseudo_by_element[{&e, i}] = ps.get();
                owned.push_back(std::move(ps));
                if (paint_pseudo) pending = worst(pending, Invalidation::Paint);
                else note_structural();
                continue;
            }
            merge(pit->second, &scratch);
        }
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
    const ComputedStyle* pseudo_style_of(const Element& e, std::string_view name) override {
        const int i = name == "before"        ? 0
                      : name == "after"       ? 1
                      : name == "backdrop"    ? 2
                      : name == "marker"      ? 3
                      : name == "placeholder" ? 4
                      : name == "selection"   ? 5
                                              : -1;
        if (i < 0) return nullptr;
        auto it = pseudo_by_element.find({&e, i});
        return it == pseudo_by_element.end() ? nullptr : it->second;
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
    struct ProfileBucket { double ms = 0; size_t calls = 0, hits = 0; };
    struct ProfileScope {
        using Clock = std::chrono::steady_clock;
        ProfileBucket* bucket;
        Clock::time_point start;
        ProfileScope(ProfileBucket& b, bool enabled) : bucket(enabled ? &b : nullptr) {
            if (bucket) { ++bucket->calls; start = Clock::now(); }
        }
        ~ProfileScope() {
            if (bucket) bucket->ms += std::chrono::duration<double, std::milli>(Clock::now() - start).count();
        }
        void hit() { if (bucket) ++bucket->hits; }
    };
public:
    HostFontBackend(const weva_font_backend& table, FontInterface* fallback)
        : t_(table), fallback_(fallback) {}
    ~HostFontBackend() override { log_shape_cache("destroy"); }
    // weva_document_set_font_leading_rounding: a synthetic face compared with
    // the oracle's arithmetic keeps its fractional half-leading.
    bool rounds_line_leading() const override { return rounds_leading_; }
    void set_rounds_leading(bool rounds) { rounds_leading_ = rounds; }
    bool rounds_leading_ = true;

    void profile_lap(const char* stage) {
        if (!profile_enabled_) return;
        static constexpr const char* names[] = {"face", "glyph-index", "glyph-metrics", "raster", "variant", "shape"};
        for (size_t i = 0; i < 6; ++i) {
            auto& b = profile_[i];
            if (b.calls) std::fprintf(stderr, "    font part: %s %-13s %.3f ms; %zu calls, %zu cache hits\n",
                stage, names[i], b.ms, b.calls, b.hits);
            b = {};
        }
    }

    bool set_shaper(weva_shape_glyphs_fn shape) {
        if (shape == positioned_shape_) return false;
        positioned_shape_ = shape;
        log_shape_cache("before_shaper_reset");
        newest_shape_ = oldest_shape_ = nullptr;
        shaped_.clear();
        shaped_bytes_ = 0;
        log_shape_cache("after_shaper_reset");
        return true;
    }

    FaceHandle load_face(const std::vector<uint8_t>& ttf, int index) override {
        if (!t_.load_face) return fallback_->load_face(ttf, index);
        return FaceHandle{t_.load_face(t_.user_data, ttf.data(), ttf.size(), index)};
    }
    bool face_metrics(FaceHandle face, double px, FaceMetrics* out) override {
        if (!t_.face_metrics) return fallback_->face_metrics(face, px, out);
        if (!out) return false;
        ProfileScope profile(profile_[0], profile_enabled_);

        // Memoised on (face, size), which is a handful of pairs for a whole
        // page -- a document uses a few font sizes, not a few thousand.
        //
        // Layout asks constantly: line_height, ascent and descent are three
        // separate entry points and each one calls this, so every box that
        // needs a line height costs a round trip into the host. On
        // layout-stress that is 6,926 boxes an update, every update, for a
        // value that depends on nothing but the face and the size.
        const uint64_t key = (face.id << 20) ^ static_cast<uint64_t>(px_key(px));
        const auto hit = face_metrics_.find(key);
        if (hit != face_metrics_.end() && hit->second.face == face.id &&
            hit->second.px_key == px_key(px)) {
            profile.hit();
            *out = hit->second.metrics;
            return hit->second.ok;
        }

        *out = {};
        const int32_t ok = t_.face_metrics(t_.user_data, face.id, px, &out->ascent,
                                           &out->descent, &out->line_gap);
        out->units_per_em = px;
        if (face_metrics_.size() >= kFaceMetricsCacheMax) face_metrics_.clear();
        face_metrics_[key] = FaceEntry{face.id, px_key(px), *out, ok != 0};
        return ok != 0;
    }
    bool glyph_index(FaceHandle face, uint32_t cp, uint32_t* out) override {
        if (!t_.glyph_index) return fallback_->glyph_index(face, cp, out);
        ProfileScope profile(profile_[1], profile_enabled_);
        return t_.glyph_index(t_.user_data, face.id, cp, out) != 0;
    }
    bool glyph_metrics(FaceHandle face, uint32_t glyph, double px, GlyphMetrics* out) override {
        if (!t_.glyph_metrics) return fallback_->glyph_metrics(face, glyph, px, out);
        if (!out) return false;
        ProfileScope profile(profile_[2], profile_enabled_);
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
        ProfileScope profile(profile_[3], profile_enabled_);
        weva_glyph_bitmap bmp{};
        if (!t_.rasterize(t_.user_data, face.id, glyph, px, &bmp)) return false;
        out->width = bmp.width;
        out->height = bmp.height;
        const size_t n = static_cast<size_t>(std::max(0, bmp.width)) *
                         static_cast<size_t>(std::max(0, bmp.height));
        // Copied here, so the host's buffer need only live for the call.
        out->data.assign(bmp.alpha, bmp.alpha ? bmp.alpha + n : bmp.alpha);
        if (!bmp.alpha) out->data.assign(n, 0);
        out->is_color = false;
        out->rgba.clear();
        if (bmp.rgba) {
            out->is_color = true;
            out->rgba.assign(bmp.rgba, bmp.rgba + 4 * n);
            if (!bmp.alpha) {
                for (size_t i = 0; i < n; ++i) out->data[i] = bmp.rgba[4 * i + 3];
            }
        }
        return true;
    }
    FaceHandle variant(FaceHandle face, int weight, bool italic) override {
        if (!t_.variant) return face;
        ProfileScope profile(profile_[4], profile_enabled_);
        const uint64_t v = t_.variant(t_.user_data, face.id, weight, italic ? 1 : 0);
        return v ? FaceHandle{v} : face;
    }
    void shape(FaceHandle face, std::string_view utf8, double px,
               std::vector<ShapedGlyph>* out) override {
        if (!positioned_shape_ && !t_.shape) { fallback_->shape(face, utf8, px, out); return; }
        if (!out) return;
        ProfileScope profile(profile_[5], profile_enabled_);
        out->clear();

        // Memoised, because the same run is shaped over and over.
        //
        // Layout shapes it to measure, paint shapes it again to place the
        // glyphs, and an ANIMATED page does both on every frame for text that
        // has not changed. On the hud sample that was paint spending 1.5ms a
        // frame inside shape() alone, plus whatever layout asked for.
        //
        // Every call also crosses the host boundary TWICE -- once to size the
        // result, once to fill it -- and in the Godot host each of those
        // builds a TextServer buffer and reads a Dictionary per glyph back
        // out. That is the cost being avoided, not the arithmetic.
        //
        // Sound because a shaper is a pure function of (face, text, size): the
        // face id identifies an immutable face, and nothing else in the call
        // can change the answer.
        const uint64_t key = shape_key(face.id, utf8, px);
        const auto hit = shaped_.find(key);
        if (hit != shaped_.end() && hit->second.px_key == px_key(px) &&
            hit->second.face == face.id && hit->second.text == utf8) {
            profile.hit();
            touch_shape(hit->second);
            *out = hit->second.glyphs;
            return;
        }
        // Sized with one call, filled with a second — the same two-call shape
        // the text accessor uses, so a host implements one pattern.
        if (positioned_shape_) {
            const size_t n = positioned_shape_(t_.user_data, face.id, utf8.data(), utf8.size(),
                                                px, nullptr, 0);
            if (n == 0) return;
            std::vector<weva_shaped_glyph> glyphs(n);
            const size_t got = positioned_shape_(t_.user_data, face.id, utf8.data(), utf8.size(),
                                                  px, glyphs.data(), glyphs.size());
            out->reserve(std::min(n, got));
            for (size_t i = 0; i < std::min(n, got); ++i) {
                const auto& src = glyphs[i];
                ShapedGlyph g;
                g.glyph = src.glyph;
                g.cluster = src.cluster;
                g.x_advance = src.x_advance;
                g.y_advance = src.y_advance;
                g.x_offset = src.x_offset;
                g.y_offset = src.y_offset;
                out->push_back(g);
            }
        } else {
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

        // A changing counter/name must not flush the hot labels around it.
        // References to unordered_map values survive rehash, so intrusive LRU
        // links need no separate allocation or scan on a cache hit/insertion.
        if (utf8.size() >= kShapeCacheBytes ||
            out->size() > kShapeCacheBytes / sizeof(ShapedGlyph)) return;
        const auto collision = shaped_.find(key);
        if (collision != shaped_.end()) {
            shaped_bytes_ -= shape_bytes(collision->second);
            unlink_shape(collision->second);
            shaped_.erase(collision);
        }
        Shaped entry;
        entry.key = key;
        entry.face = face.id;
        entry.px_key = px_key(px);
        entry.text.assign(utf8);
        entry.glyphs = *out;
        // A count limit alone lets long changing labels retain many megabytes.
        // Oversized runs still shape correctly, but do not evict the entire
        // working set just to retain a result larger than the payload budget.
        if (entry.text.capacity() >= kShapeCacheBytes ||
            entry.glyphs.capacity() > kShapeCacheBytes / sizeof(ShapedGlyph)) return;
        const size_t bytes = shape_bytes(entry);
        if (bytes > kShapeCacheBytes) return;
        while (!shaped_.empty() && (shaped_.size() >= kShapeCacheMax ||
               shaped_bytes_ > kShapeCacheBytes - bytes)) {
            const bool log_eviction = memory_log_enabled_ && shape_insertions_ % 256 == 255;
            if (log_eviction) log_shape_cache("before_lru_eviction");
            const auto oldest_key = oldest_shape_->key;
            shaped_bytes_ -= shape_bytes(*oldest_shape_);
            unlink_shape(*oldest_shape_);
            shaped_.erase(oldest_key);
            if (log_eviction) log_shape_cache("after_lru_eviction");
        }
        auto inserted = shaped_.emplace(key, std::move(entry));
        shaped_bytes_ += bytes;
        touch_shape(inserted.first->second);
        if (memory_log_enabled_ && ++shape_insertions_ % 256 == 0) log_shape_cache("checkpoint");
    }

private:
    // Size is part of the key: the same word at 11px and 14px shapes to
    // different advances, and folding them together would be a subtle,
    // size-dependent wrongness rather than an obvious one.
    static int64_t px_key(double px) { return static_cast<int64_t>(std::llround(px * 1024.0)); }

    static uint64_t shape_key(uint64_t face, std::string_view text, double px) {
        uint64_t h = 1469598103934665603ULL;
        const auto mix = [&h](uint64_t v) { h = (h ^ v) * 1099511628211ULL; };
        mix(face);
        for (const char c : text) mix(static_cast<unsigned char>(c));
        mix(static_cast<uint64_t>(px_key(px)));
        return h;
    }

    struct Shaped {
        uint64_t key = 0;
        Shaped* newer = nullptr;
        Shaped* older = nullptr;
        uint64_t face = 0;
        int64_t px_key = 0;
        std::string text;
        std::vector<ShapedGlyph> glyphs;
    };
    static constexpr size_t kShapeCacheMax = 4096;
    // Per document payload bound; map nodes/buckets and allocator overhead are
    // separately bounded by the entry limit. This is not a process-memory cap.
    static constexpr size_t kShapeCacheBytes = 4 * 1024 * 1024;
    static size_t shape_bytes(const Shaped& entry) {
        return sizeof(Shaped) + entry.text.capacity() + 1 +
               entry.glyphs.capacity() * sizeof(ShapedGlyph);
    }
    size_t shaped_bytes_ = 0;
    std::unordered_map<uint64_t, Shaped> shaped_;
    Shaped* newest_shape_ = nullptr;
    Shaped* oldest_shape_ = nullptr;
    size_t shape_insertions_ = 0; // Diagnostic cadence only, not a cache key.

    void unlink_shape(Shaped& entry) {
        if (entry.newer) entry.newer->older = entry.older;
        else if (newest_shape_ == &entry) newest_shape_ = entry.older;
        if (entry.older) entry.older->newer = entry.newer;
        else if (oldest_shape_ == &entry) oldest_shape_ = entry.newer;
        entry.newer = entry.older = nullptr;
    }
    void touch_shape(Shaped& entry) {
        if (newest_shape_ == &entry) return;
        unlink_shape(entry);
        entry.older = newest_shape_;
        if (newest_shape_) newest_shape_->newer = &entry;
        else oldest_shape_ = &entry;
        newest_shape_ = &entry;
    }

    // Explicit diagnostic only: never scan retained runs on ordinary frames.
    // Capacities describe cache payload, not allocator commitment or RSS. String
    // capacity includes inline storage; map nodes and allocator overhead are not
    // included. Backend identity distinguishes concurrently live documents.
    void log_shape_cache(const char* event) const {
        if (!memory_log_enabled_) return;
        size_t text_capacity = 0, glyph_capacity = 0;
        for (const auto& item : shaped_) {
            text_capacity += item.second.text.capacity() + 1;
            glyph_capacity += item.second.glyphs.capacity() * sizeof(ShapedGlyph);
        }
        std::fprintf(stderr, "WEVA_SHAPE_CACHE {\"event\":\"%s\",\"backend\":\"%p\","
            "\"entries\":%zu,\"text_capacity_bytes\":%zu,\"glyph_capacity_bytes\":%zu,"
            "\"entry_storage_bytes\":%zu,\"bucket_count\":%zu}\n",
            event, static_cast<const void*>(this), shaped_.size(), text_capacity,
            glyph_capacity, shaped_.size() * sizeof(Shaped), shaped_.bucket_count());
    }

    struct FaceEntry {
        uint64_t face = 0;
        int64_t px_key = 0;
        FaceMetrics metrics;
        bool ok = false;
    };
    // Small on purpose: a page's distinct (face, size) pairs number in the
    // tens. If this ever fills, something is generating sizes rather than
    // using them.
    static constexpr size_t kFaceMetricsCacheMax = 512;
    std::unordered_map<uint64_t, FaceEntry> face_metrics_;

private:
    weva_font_backend t_;
    const bool profile_enabled_ = std::getenv("WEVA_STAGE_LOG") != nullptr;
    const bool memory_log_enabled_ = std::getenv("WEVA_FONT_CACHE_LOG") != nullptr;
    ProfileBucket profile_[6];
    weva_shape_glyphs_fn positioned_shape_ = nullptr;
    FontInterface* fallback_;
};

} // namespace

// The one type the opaque handle points at.
struct weva_document {
    weva_config config{};
    SymbolTable symbols;
    Ref<Document> doc;
    std::unique_ptr<Stylesheet> ua_sheet;
    std::vector<std::unique_ptr<Stylesheet>> sheets; // author sheets only
    StyleMap styles;
    ContainerQueryState container_queries;
    BoxTree tree;
    IncrementalLayout incremental_layout;
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
    std::map<std::string, std::unique_ptr<FontInterfaceMetrics>> family_metrics;
    // Metrics for the host's bold / italic variants of the default face, by
    // (face, weight, italic); handed to layout through ctx.variant_metrics.
    std::map<std::tuple<uint64_t, int, bool>, std::unique_ptr<FontInterfaceMetrics>> variant_metrics;
    FaceHandle face = StubFont::builtin();
    bool font_leading_rounding = true;
    BoxId root = kNoBox;
    // Textures paint generated for the last published draws (gradient
    // layers). Released at the start of the next update, once the host has
    // had the frame.
    std::vector<TextureHandle> transient_textures;
    // Rasterized gradient and blur textures, kept across updates and released
    // when a pass stops asking for them. Without this a document costs its
    // whole background rasterization on every change, which is most of what an
    // update costs at all.
    TextureCache textures;
    // Decoded background images, kept across updates. Rasterizing a
    // background re-samples the image every time its box changes, so decoding
    // per update would be the most expensive thing in the engine.
    ImageStore images;
    // A scrollbar thumb being dragged. Held by element, like the offsets, so
    // a relayout mid-drag does not drop the grab.
    struct ScrollDrag {
        const Element* element = nullptr;
        bool vertical = false;
        double grab = 0;        // where along the axis the pointer took hold
        double from = 0;        // the offset at that moment
        double per_pixel = 0;   // what a pixel of thumb travel is worth
    } scroll_drag;
    // Where each scroll container is scrolled to. Kept per ELEMENT, not per
    // box: the box tree is thrown away and rebuilt whenever anything moves, so
    // an offset kept on a box would be lost by every class change.
    std::unordered_map<const Element*, std::pair<double, double>> scroll;
    // What a field held before each edit, so Ctrl+Z can put it back. Snapshots
    // rather than a log of operations: a text field is small, and a snapshot
    // cannot disagree with the field the way a replayed operation can.
    //
    // Consecutive typing coalesces into ONE entry -- undoing a sentence a
    // letter at a time is not undo -- and anything that is not typing breaks
    // the run, which is the grouping a browser uses.
    struct EditSnapshot {
        std::string text;
        int caret = 0;
        int anchor = -1;
    };
    struct EditHistory {
        std::vector<EditSnapshot> undo;
        std::vector<EditSnapshot> redo;
        bool last_was_typing = false;
    };
    EditSnapshot composition_before;
    std::string composition_value;
    static constexpr size_t kUndoDepth = 100;
    std::unordered_map<const Element*, EditHistory> history;
    struct FieldSelection {
        int caret = 0;
        int anchor = -1;
        std::string value;
    };
    // Only fields that have lost focus need a saved selection. Active editing
    // still uses InteractionState, without copying its value on every keystroke.
    std::unordered_map<const Element*, FieldSelection> field_selections;
    // Open popovers, innermost last, for dismissal order. Live visibility
    // belongs to the element. The data attribute is a compatibility mirror.
    std::vector<const Element*> popovers;
    // Keep the opening mode until dismissal: attribute mutation has already
    // changed the current mode when the live-state observer sees a close.
    std::unordered_set<const Element*> light_dismiss_popovers;
    std::unordered_map<const Element*, const Element*> popover_parents;
    std::unordered_map<const Element*, const Element*> popover_previous_focus;
    struct DialogEntry { const Element* dialog; const Element* previous_focus; };
    // Opening order, not DOM order. Entries are removed with their elements.
    std::vector<DialogEntry> dialog_focus_history;
    // Open order also includes markup and attribute-created dialogs, which do
    // not have show() focus-restoration entries.
    std::vector<const Element*> dialog_close_order;
    void sync_dialog_open(const Element* e) {
        if (e->tag_name() != "dialog") return;
        auto it = std::find(dialog_close_order.begin(), dialog_close_order.end(), e);
        if (e->has_attribute("open")) {
            if (it == dialog_close_order.end()) dialog_close_order.push_back(e);
        } else {
            if (it != dialog_close_order.end()) dialog_close_order.erase(it);
            dialog_close_generations.erase(e);
        }
    }

    bool check_focus_after_dialog_close = false;
    // `title="..."` renders as a tooltip after the pointer has rested on the
    // element for a moment. The UA sheet has styled `.ui-tooltip` all along
    // and nothing ever made one, so `title` was inert.
    //
    // The delay is why this needs the clock and not just pointer events: a
    // pointer that stops moving sends nothing more, and the tooltip has to
    // appear anyway.
    struct Tooltip {
        const Element* host = nullptr;   // whose `title` would be shown
        double dwell = 0;                // how long the pointer has rested there
        Element* shown = nullptr;        // the injected <div>, while it is up
        double x = 0;
        double y = 0;
    } tooltip;
    double tooltip_delay = 0.6;
    // What was held at the last set_pointer, so the SECONDARY button's press
    // edge can be told from it being kept down while the pointer moves.
    uint32_t buttons_last = 0;
    // Where `{{ path }}` gets its values, and the attribute templates the
    // substitution would otherwise have eaten.
    weva_binding_source binding_source{};
    bool has_binding_source = false;
    BindingTemplates binding_templates;
    BindingRepeats binding_repeats;
    bool refreshing_bindings = false;
    bool binding_structure_changed = false;
    // The open dropdown, and which of its options the pointer or the keys are
    // on. Held here rather than in the DOM because being open is not a
    // property of the document -- reload the same markup and nothing is open.
    const Element* open_select = nullptr;
    const Element* space_press_target = nullptr;
    uint32_t activation_keys_down = 0;
    std::vector<const Element*> radio_focus_memory;
    struct ListSelection {
        const Element* active = nullptr;
        const Element* anchor = nullptr;
        int64_t version = -1;
        bool dragging = false, toggle_drag = false, drag_selected = true;
        std::vector<const Element*> before_drag;
    };
    std::map<const Element*, ListSelection> list_selections;
    const Element* list_follow = nullptr;
    const Element* list_drag = nullptr;
    bool list_scroll_armed = false;
    const Element* text_drag = nullptr;
    bool text_scroll_armed = false;
    double pointer_x = 0, pointer_y = 0;
    TypeAheadSession typeahead;
    const Element* typeahead_target = nullptr;
    // A host can observe an outside press before native GUI routing and
    // dismiss afterward. Do not let that deferred request close a new popup.
    uint64_t transient_version = 1;
    void set_open_select(const Element* element) {
        open_select = element;
        ++transient_version;
    }
    int highlighted_option = -1;
    // The row the open list is scrolled to. A list longer than the cap has to
    // move, or its last options can be neither seen nor clicked.
    int select_first_row = 0;
    // What the focused field held when the focus arrived. A blur compares
    // against it to decide whether anything was committed: `on-change` fires
    // for an edit, and not for a visit.
    std::string value_at_focus;
    bool user_edited_since_focus = false;
    // Set when the cursor moved or the text under it changed, so the next
    // update scrolls it back into view. Not every frame: a reader who has
    // scrolled away from the cursor should stay there.
    bool caret_follow = false;
    const Element* focus_follow = nullptr;
    // What caret the published draws were painted with. The blink is a change
    // no cascade can see, so it is compared against this to ask for a repaint.
    CaretState caret_painted;

    // What a host did that no element's style records: a stylesheet added, the
    // viewport resized, a backend swapped, the document loaded. Cleared by the
    // update that acts on it.
    Invalidation pending = Invalidation::Boxes;
    // Geometry-only passes already propagate paint input versions. Keep the
    // publication request separate from structural/layout invalidation.
    bool paint_pending = false;
    // Whether an attribute was set since the last update. Only the cascade can
    // tell what an attribute did, but nothing can have changed if none was
    // touched -- and then even running the cascade is waste.
    bool dom_touched = false;
    // The elements whose attributes were set since the last update. An
    // attribute change reaches its own subtree, and -- when the sheets contain
    // a sibling combinator -- the siblings after it. Nothing else, unless a
    // :has() is about, which lets a descendant decide an ancestor's match and
    // puts the whole document back in play.
    std::vector<Element*> touched;
    bool default_inputs_dirty = true;
    std::vector<Ref<Element>> default_buttons;

    // Events waiting for the host to pump them. Bounded: a host that never
    // reads gets the oldest dropped rather than unbounded growth, because a
    // queue that can starve a process is worse than a lost click.
    struct ValidationReport {
        size_t pending = 0;
        Ref<Element> form;
        std::vector<Ref<Element>> unhandled;
        bool interactive = true;
        bool standalone_control = false;
    };
    struct QueuedEvent : weva_event {
        std::string full_text;
        std::vector<Ref<Element>> popover_pending_closes;
        int popover_open_mode = 0;
        bool popover_resume_open = false;
        bool popover_restore_focus = true;
        Ref<Element> popover_hide_target;
        Ref<Element> popover_open_target;
        Ref<Element> popover_open_source;
        Ref<Element> close_target;
        uint64_t close_generation = 0;
        bool has_close_value = false;
        std::string close_value;
        Ref<Element> submit_form;
        Ref<Element> submitter;
        std::string image_coordinates;
        Ref<Element> invalid_target;
        std::shared_ptr<ValidationReport> validation_report;
        QueuedEvent(const weva_event& event) : weva_event(event) {}
        QueuedEvent(const weva_event& event, std::string_view text) : weva_event(event), full_text(text) {}
    };
    std::deque<QueuedEvent> events;
    std::string polled_event_text;
    int polled_popover_open_mode = 0;
    bool popover_request_events = false;
    Ref<Element> polled_popover_open_source;
    bool polled_popover_restore_focus = true;
    Ref<Element> polled_popover_hide_target;
    Ref<Element> polled_popover_open_target;
    Ref<Element> polled_close_target;
    uint64_t polled_close_generation = 0;
    bool polled_close_prevented = false;
    bool polled_has_close_value = false;
    std::string polled_close_value;
    Ref<Element> polled_submit_form;
    Ref<Element> polled_submitter;
    std::string polled_image_coordinates;
    Ref<Element> polled_invalid_target;
    std::shared_ptr<ValidationReport> polled_validation_report;
    std::unordered_map<const Element*, std::string> dialog_return_values;
    uint64_t next_close_generation = 0;
    std::unordered_map<const Element*, uint64_t> dialog_close_generations;
    static constexpr size_t kMaxEvents = 256;
    bool push_event(QueuedEvent event) {
        if (events.size() >= kMaxEvents) {
            // Notifications may be dropped under pressure. CANCEL, SUBMIT and
            // INVALID and popover requests own default actions; preserve them.
            const auto discard = std::find_if(events.begin(), events.end(),
                [](const auto& queued) { return queued.kind != WEVA_EVENT_CANCEL && !queued.submit_form && !queued.invalid_target && !queued.popover_open_target && !queued.popover_hide_target; });
            if (discard == events.end()) return false;
            events.erase(discard);
        }
        events.push_back(std::move(event));
        return true;
    }

    // The element a press started on, so a release on the SAME one is a click
    // and a release anywhere else is not.
    const Element* press_target = nullptr;
    const Element* popover_press_target = nullptr;
    bool popover_press_active = false;

    std::unordered_map<const Element*, weva_element_t> element_handles;
    std::vector<weva_element_t> query_order;
    uint64_t structure_version = 1, query_order_version = 0;

    weva_element_t handle_of(const Element* e) const {
        const auto found = element_handles.find(e);
        return found == element_handles.end() ? WEVA_ELEMENT_NONE : found->second;
    }

    const std::vector<weva_element_t>& document_order() {
        if (query_order_version == structure_version) return query_order;
        query_order.clear();
        const auto visit = [&](const auto& self, const Node& node) -> void {
            if (node.is_element()) {
                const auto handle = handle_of(static_cast<const Element*>(&node));
                if (handle != WEVA_ELEMENT_NONE) query_order.push_back(handle);
            }
            for (const auto& child : node.children()) self(self, *child);
        };
        if (doc) visit(visit, *doc);
        query_order_version = structure_version;
        return query_order;
    }

    // `on-click` for a click, `on-input` for a value change, and so on. The
    // names are the ones the Unity engine's markup already uses, since the
    // whole point is that the same document works on both.
    static std::string_view handler_attribute_for(int32_t kind) {
        switch (kind) {
            case WEVA_EVENT_CLICK: return "on-click";
            case WEVA_EVENT_POINTER_DOWN: return "on-pointerdown";
            case WEVA_EVENT_POINTER_UP: return "on-pointerup";
            case WEVA_EVENT_POINTER_ENTER: return "on-pointerenter";
            case WEVA_EVENT_POINTER_LEAVE: return "on-pointerleave";
            case WEVA_EVENT_VALUE_CHANGED: return "on-input";
            case WEVA_EVENT_CHANGE: return "on-change";
            case WEVA_EVENT_SUBMIT: return "on-submit";
            case WEVA_EVENT_INVALID: return "on-invalid";
            case WEVA_EVENT_RESET: return "on-reset";
            case WEVA_EVENT_CLOSE: return "on-close";
            case WEVA_EVENT_CANCEL: return "on-cancel";
            case WEVA_EVENT_SCROLL: return "on-scroll";
            case WEVA_EVENT_TOGGLE: return "on-toggle";
            case WEVA_EVENT_BEFORE_TOGGLE: return "on-beforetoggle";
            case WEVA_EVENT_CONTEXT_MENU: return "on-contextmenu";
            case WEVA_EVENT_KEY_DOWN: return "on-keydown";
            case WEVA_EVENT_KEY_UP: return "on-keyup";
            case WEVA_EVENT_TEXT_INPUT: return "on-textinput";
            case WEVA_EVENT_COMPOSITION_START: return "on-compositionstart";
            case WEVA_EVENT_COMPOSITION_UPDATE: return "on-compositionupdate";
            case WEVA_EVENT_COMPOSITION_END: return "on-compositionend";
            case WEVA_EVENT_FOCUS: return "on-focus";
            case WEVA_EVENT_BLUR: return "on-blur";
            default: return {};
        }
    }

    // Towards the root, so `on-submit` on a form catches a button inside it
    // and a row's `on-click` catches whatever the click actually landed on.
    static void fill_handler(weva_event* e, const Element* target) {
        e->handler[0] = '\0';
        const std::string_view attribute = handler_attribute_for(e->kind);
        if (attribute.empty()) return;
        for (const Node* n = target; n; n = n->parent()) {
            if (n->node_type() != NodeType::Element) continue;
            const std::string_view named =
                static_cast<const Element&>(*n).get_attribute(attribute);
            if (named.empty()) {
                if (e->kind == WEVA_EVENT_CLOSE || e->kind == WEVA_EVENT_CANCEL || e->kind == WEVA_EVENT_INVALID || e->kind == WEVA_EVENT_BEFORE_TOGGLE) return;
                continue;
            }
            const size_t copy = named.size() < sizeof(e->handler) - 1 ? named.size()
                                                                     : sizeof(e->handler) - 1;
            std::memcpy(e->handler, named.data(), copy);
            e->handler[copy] = '\0';
            return;
        }
    }

    bool queue_event(int32_t kind, const Element* target, double x, double y, uint32_t buttons,
                     uint32_t modifiers = 0, const char* toggle_state = nullptr) {
        weva_event e{};
        e.kind = kind;
        e.target = WEVA_ELEMENT_NONE;
        if (target) {
            for (size_t i = 0; i < elements.size(); ++i) {
                if (elements[i] == target) { e.target = static_cast<weva_element_t>(i); break; }
            }
        }
        e.x = x;
        e.y = y;
        e.buttons = buttons;
        e.modifiers = modifiers;
        if (kind == WEVA_EVENT_TOGGLE) {
            if (!toggle_state) toggle_state = target && target->has_attribute("open") ? "open" : "closed";
            std::memcpy(e.text, toggle_state, std::strlen(toggle_state) + 1);
        }
        fill_handler(&e, target);
        return push_event(e);
    }

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
    std::vector<uint64_t> draw_versions;
    // Bumped only where draw_views is rebuilt, which the settled-document
    // early-out above never reaches. A host compares it to decide whether it
    // has anything new to submit -- see weva_document_draw_serial.
    uint64_t draw_serial = 0;
    std::vector<weva_texture> texture_views;

    // Every element still in the document. A binding repeat is the one thing
    // that makes and destroys elements without going through the ABI, so a
    // refresh has to work out for itself what is still there.
    void collect_live(std::set<const Element*>* out) const {
        const std::function<void(const Element&)> visit = [&](const Element& e) {
            out->insert(&e);
            for (const Ref<Node>& c : e.children()) {
                if (c->node_type() == NodeType::Element) {
                    visit(static_cast<const Element&>(*c));
                }
            }
        };
        for (const Ref<Node>& c : doc->children()) {
            if (c->node_type() == NodeType::Element) visit(static_cast<const Element&>(*c));
        }
    }

    // Every element in the document that has no handle yet gets one, keeping
    // the handles already handed out. A binding repeat is the one thing that
    // makes elements without going through the ABI.
    void reindex_new_elements() {
        const std::function<void(Element&)> visit = [&](Element& e) {
            if (!element_handles.count(&e)) {
                ++structure_version;
                element_handles.emplace(&e, static_cast<weva_element_t>(elements.size()));
                elements.push_back(&e);
                sync_dialog_open(&e);
            }
            for (const Ref<Node>& c : e.children()) {
                if (c->node_type() == NodeType::Element) {
                    visit(static_cast<Element&>(const_cast<Node&>(*c)));
                }
            }
        };
        for (const Ref<Node>& c : doc->children()) {
            if (c->node_type() == NodeType::Element) {
                visit(static_cast<Element&>(const_cast<Node&>(*c)));
            }
        }
    }

    void index_elements(Element& e) {
        ++structure_version;
        element_handles.emplace(&e, static_cast<weva_element_t>(elements.size()));
        elements.push_back(&e);
        sync_dialog_open(&e);
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

namespace {

extern "C" void forget_subtree(weva_document* doc, const Element& e);

bool is_table_span_attribute(std::string_view name) {
    return name == "rowspan" || name == "colspan" || name == "span";
}

void note_box_input(weva_document* doc, Element* e) {
    auto& inputs = doc->styles.pending_box_inputs;
    if (std::find(inputs.begin(), inputs.end(), e) == inputs.end()) inputs.push_back(e);
    // The box tree changes, but the cascade still has a precise DOM origin.
    // Opening a dialog cannot restyle unrelated HUD elements unless selectors
    // actually carry that input there.
    doc->dom_touched = true;
    if (doc->styles.engine.has_sibling_selectors() && e->parent() && e->parent()->is_element())
        e = static_cast<Element*>(e->parent());
    if (doc->touched.size() < 64 && std::find(doc->touched.begin(), doc->touched.end(), e) == doc->touched.end())
        doc->touched.push_back(e);
}

// Bindings mutate the real DOM. Observe those input changes while refreshing
// so text/attributes take the same scoped path as ordinary host writes.
void note_binding_mutation(weva_document* doc, const DomMutation& mutation) {
    if (mutation.kind == MutationKind::FormStateChanged) return;
    if (mutation.kind == MutationKind::ChildAdded || mutation.kind == MutationKind::ChildRemoved) {
        // Same-parent moves emit ChildAdded without a removal. Existing style
        // identity proves that this is a retained row, not a new clone. Its
        // parent's content/order is the layout input; selector scope still
        // includes every sibling whose rank or combinator match can change.
        if (mutation.kind == MutationKind::ChildAdded && mutation.target && mutation.target->is_element() &&
            mutation.related && mutation.related->is_element() &&
            doc->styles.by_element.count(static_cast<const Element*>(mutation.related))) {
            auto* parent = static_cast<Element*>(mutation.target);
            auto& inputs = doc->styles.pending_content_inputs;
            if (std::find(inputs.begin(), inputs.end(), parent) == inputs.end()) inputs.push_back(parent);
            doc->dom_touched = true;
            if (doc->styles.engine.has_sibling_selectors() && parent->parent() && parent->parent()->is_element())
                parent = static_cast<Element*>(parent->parent());
            if (doc->touched.size() < 64 && std::find(doc->touched.begin(), doc->touched.end(), parent) == doc->touched.end())
                doc->touched.push_back(parent);
            return;
        }
        doc->binding_structure_changed = true;
        doc->pending = worst(doc->pending, Invalidation::Boxes);
        // Drop pointer-keyed state before a removed row can be freed and its
        // address reused by a replacement in this very refresh.
        if (mutation.kind == MutationKind::ChildRemoved && mutation.related && mutation.related->is_element())
            forget_subtree(doc, static_cast<const Element&>(*mutation.related));
        return;
    }
    Node* owner = mutation.target;
    if (mutation.kind == MutationKind::TextChanged) owner = owner->parent();
    if (!owner || !owner->is_element()) {
        doc->pending = worst(doc->pending, Invalidation::Boxes);
        return;
    }
    auto* e = static_cast<Element*>(owner);
    if (e->tag_name() == "dialog" && mutation.name == "open")
        doc->sync_dialog_open(e);
    // Table placement reads span attributes directly, independent of computed
    // CSS. Their input version must change even when no selector result does.
    const bool table_span = is_table_span_attribute(mutation.name);
    if (mutation.kind == MutationKind::TextChanged || table_span || (e->tag_name() == "img" && mutation.name == "src")) {
        auto& inputs = doc->styles.pending_content_inputs;
        if (std::find(inputs.begin(), inputs.end(), e) == inputs.end()) inputs.push_back(e);
    }
    // Top-layer membership changes boxes even when authored CSS keeps display
    // unchanged. These attributes have the same contract as show/close APIs.
    if ((e->tag_name() == "dialog" && (mutation.name == "open" || mutation.name == "data-modal")) ||
        mutation.name == "data-popover-open") note_box_input(doc, e);
    doc->dom_touched = true;
    // Filtered nth-last-child ranks can affect preceding siblings too.
    if (doc->styles.engine.has_sibling_selectors() && e->parent() && e->parent()->is_element())
        e = static_cast<Element*>(e->parent());
    if (doc->touched.size() < 64 && std::find(doc->touched.begin(), doc->touched.end(), e) == doc->touched.end())
        doc->touched.push_back(e);
}

// Finds the run the cursor sits in, in document order, and how far into it.
//
// The runs of a textarea's value each VIEW INTO the value's own buffer, so the
// difference of the pointers says where in the value a run starts. The first
// run whose span reaches the cursor wins: a cursor at the end of a line is at
// the end of that line's run, and one at the start of the next is at the start
// of the next run -- and settling it here rather than per run is what keeps a
// line end from having no cursor at all, since the newline belongs to no run.
void resolve_caret_run(weva_document* doc, CaretState* caret) {
    caret->run = kNoBox;
    caret->run_offset = 0;
    if (!caret->element || caret->element->tag_name() != "textarea") return;
    const std::string_view source = caret->element->form_edit_value();
    if (source.empty()) return;
    caret->source = source;
    const size_t idx = static_cast<size_t>(std::max(0, caret->index));
    BoxId last = kNoBox;
    size_t last_end = 0;
    const auto visit = [&](auto&& self, BoxId i) -> bool {
        for (BoxId c : doc->tree.children(i)) if (self(self, c)) return true;
        const Box& b = doc->tree[i];
        if (b.kind != BoxKind::Text || (b.text.empty() && b.source_control != caret->element)) return false;
        // The FRAGMENTS, not the run they were split from. Inline layout keeps
        // the whole unsplit run in the tree as well, and it spans the entire
        // value -- so it matches any cursor, and paint never draws it. Only a
        // fragment sits in a line box, which is also where its baseline comes
        // from.
        if (b.parent == kNoBox || doc->tree[b.parent].kind != BoxKind::Line) return false;
        if (b.source_control != caret->element) return false;
        const auto source_at = text_source_offset(b, source);
        if (!source_at) return false;
        const size_t off = *source_at;
        if (idx >= off && idx <= off + text_source_length(b)) {
            caret->run = i;
            caret->run_offset = source_to_display(b, idx - off);
            return !caret->downstream;
        }
        if (off + text_source_length(b) <= idx) {
            last = i;
            last_end = b.text.size();
        }
        return false;
    };
    if (doc->tree.valid(doc->root)) visit(visit, doc->root);
    if (caret->run != kNoBox) return;
    // Fall back to the preceding fragment if no source-backed run matches.
    // Preserved empty lines carry their own zero-width matching fragments.
    caret->run = last;
    caret->run_offset = last_end;
}

// Scrolls every container above `target` by the least that brings its box into
// view. Shared by the entry point and by focus, which does it by itself.
void bring_box_into_view(weva_document* doc, BoxId target) {
    const BoxTree& tree = doc->tree;
    if (!tree.valid(target)) return;
    for (BoxId p = tree[target].parent; p != kNoBox; p = tree[p].parent) {
        if (!tree[p].element || !clips_overflow(tree[p])) continue;
        // Where the target sits in this container's padding box, with the
        // container's own scroll left out -- that is the thing being solved
        // for -- but with every scroll BETWEEN them taken off, since those
        // have already moved it.
        double rx = 0, ry = 0;
        for (BoxId b = target; b != p && b != kNoBox; b = tree[b].parent) {
            rx += tree[b].x;
            ry += tree[b].y;
            const BoxId parent = tree[b].parent;
            if (parent != p && parent != kNoBox) {
                rx -= tree[parent].scroll_x;
                ry -= tree[parent].scroll_y;
            }
        }
        const Box& c = tree[p];
        rx -= c.border_left;
        ry -= c.border_top;
        const double client_w = c.width - c.border_left - c.border_right;
        const double client_h = c.height - c.border_top - c.border_bottom;
        double mx = 0, my = 0;
        max_scroll(tree, p, &mx, &my);
        // The nearest edge, not the top: an element already in view does not
        // move, and one below the fold comes up only far enough to be seen.
        double sx = c.scroll_x, sy = c.scroll_y;
        const double w = tree[target].width, h = tree[target].height;
        if (rx < sx) sx = rx;
        else if (rx + w > sx + client_w) sx = rx + w - client_w;
        if (ry < sy) sy = ry;
        else if (ry + h > sy + client_h) sy = ry + h - client_h;
        sx = std::clamp(sx, 0.0, mx);
        sy = std::clamp(sy, 0.0, my);
        if (sx == c.scroll_x && sy == c.scroll_y) continue;
        doc->scroll[c.element] = {sx, sy};
        doc->pending = worst(doc->pending, Invalidation::Paint);
    }
}

// The box an element generated, or kNoBox. A linear scan: the tree is small,
// and the alternative is a map that every rebuild would have to refill.
BoxId box_of(const weva_document* doc, const Element* e) {
    if (!e) return kNoBox;   // every anonymous box has a null element
    const BoxId id = doc->incremental_layout.box_of(e);
    return doc->tree.valid(id) && doc->tree[id].element == e ? id : kNoBox;
}

// The element with this id. `popovertarget` names its popover by id, the way
// `for` names a label's control, so this is the lookup that turns one into the
// other.
Element* element_by_id(weva_document* doc, const std::string& id) {
    if (id.empty()) return nullptr;
    for (Element* e : doc->elements) {
        if (e && e->get_attribute("id") == id) return e;
    }
    return nullptr;
}

// Auto/hint popovers close on outside clicks and Escape. Invalid keywords
// use manual behavior; HTML keywords are ASCII case-insensitive.
bool popover_is_auto(const Element& e) {
    return form_popover_light_dismiss(e);
}

bool is_within(const Element* candidate, const Element* ancestor);
void popover_hide(weva_document* doc, Element& e, bool restore_focus = true);
extern "C" void focus_popover(weva_document* doc, const Element& e);

bool popover_is_hint(const Element& e) {
    const auto value = e.get_attribute("popover");
    return value.size() == 4 && (value[0] == 'h' || value[0] == 'H') &&
        (value[1] == 'i' || value[1] == 'I') && (value[2] == 'n' || value[2] == 'N') &&
        (value[3] == 't' || value[3] == 'T');
}

int popover_mode_id(const Element& e) {
    return popover_is_hint(e) ? 2 : (popover_is_auto(e) ? 1 : 3);
}

void popover_show(weva_document* doc, Element& e, const Element* source = nullptr) {
    if (!e.has_attribute("popover") || e.is_popover_open() || e.is_modal()) return;
    const Element* parent = nullptr;
    if (popover_is_auto(e)) {
        // Keep the topmost ancestor and its existing stack. The invoker can
        // establish that ancestry even when the submenu is elsewhere in DOM.
        // Closing through the normal live-state path preserves notifications
        // and invalidation; it also removes the entry from this vector.
        const bool hint = popover_is_hint(e);
        for (size_t i = doc->popovers.size(); i-- > 0;) {
            auto* open = const_cast<Element*>(doc->popovers[i]);
            if (!popover_is_auto(*open)) continue;
            if (is_within(&e, open) || is_within(source, open)) { parent = open; break; }
            if (!hint || popover_is_hint(*open)) popover_hide(doc, *open, false);
        }
    }
    const bool restore_focus = popover_is_auto(e) && doc->light_dismiss_popovers.empty();
    const Element* previous_focus = doc->styles.state.focused;
    e.set_popover_open(true);
    e.set_attribute("data-popover-open", "");
    doc->popovers.push_back(&e);
    if (popover_is_auto(e)) doc->light_dismiss_popovers.insert(&e);
    if (parent) doc->popover_parents[&e] = parent;
    ++doc->transient_version;
    // It joins the top layer, so a ::backdrop box appears: boxes, not paint.
    doc->pending = worst(doc->pending, Invalidation::Boxes);
    focus_popover(doc, e);
    if (restore_focus && previous_focus) doc->popover_previous_focus[&e] = previous_focus;
    doc->queue_event(WEVA_EVENT_TOGGLE, &e, 0, 0, 0, 0, "open");
}

weva_status request_popover_open(weva_document* doc, weva_element_t element, const Element* source = nullptr) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    Element* e = doc->element_at(element);
    if (!e || !e->has_attribute("popover")) return WEVA_ERR_NOT_FOUND;
    if (e->is_modal()) return WEVA_ERR_INVALID_STATE;
    if (e->is_popover_open() || doc->polled_popover_open_target.get() == e) return WEVA_OK;
    for (const auto& pending : doc->events)
        if (pending.popover_open_target.get() == e) return WEVA_OK;
    weva_event event{};
    event.kind = WEVA_EVENT_BEFORE_TOGGLE;
    event.target = element;
    std::strcpy(event.text, "open");
    doc->fill_handler(&event, e);
    weva_document::QueuedEvent queued(event);
    queued.popover_open_target = Ref<Element>::retain(e);
    if (source) queued.popover_open_source = Ref<Element>::retain(const_cast<Element*>(source));
    return doc->push_event(std::move(queued)) ? WEVA_OK : WEVA_ERR_INVALID_STATE;
}

weva_status request_popover_hide(weva_document* doc, weva_element_t element, bool restore_focus = true, bool allow_missing_attribute = false) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    Element* e = doc->element_at(element);
    if (!e || (!allow_missing_attribute && !e->has_attribute("popover"))) return WEVA_ERR_NOT_FOUND;
    if (!e->is_popover_open() || doc->polled_popover_hide_target.get() == e) return WEVA_OK;
    for (const auto& pending : doc->events)
        if (pending.popover_hide_target.get() == e) return WEVA_OK;
    // A parent close is one operation even though it dispatches several
    // events. Reserve the whole subtree before accepting any child request.
    const auto already_pending = [&](const Element* candidate) {
        if (doc->polled_popover_hide_target.get() == candidate) return true;
        return std::any_of(doc->events.begin(), doc->events.end(),
            [candidate](const auto& event) { return event.popover_hide_target.get() == candidate; });
    };
    size_t needed = 1;
    if (doc->light_dismiss_popovers.count(e)) {
        for (const Element* candidate : doc->popovers) {
            if (candidate == e || !doc->light_dismiss_popovers.count(candidate) || already_pending(candidate)) continue;
            const Element* ancestor = candidate;
            while (ancestor) {
                const auto parent = doc->popover_parents.find(ancestor);
                ancestor = parent == doc->popover_parents.end() ? nullptr : parent->second;
                if (ancestor == e) { ++needed; break; }
            }
        }
    }
    const size_t protected_events = std::count_if(doc->events.begin(), doc->events.end(),
        [](const auto& queued) { return queued.kind == WEVA_EVENT_CANCEL || queued.submit_form ||
            queued.invalid_target || queued.popover_open_target || queued.popover_hide_target; });
    if (protected_events >= weva_document::kMaxEvents || needed > weva_document::kMaxEvents - protected_events) return WEVA_ERR_INVALID_STATE;
    // Descendants leave the stack before the parent's closing event, as in
    // the browser hide-all-popovers-until algorithm. Reverse opening order
    // visits deeper menus first; already queued requests are deduplicated.
    if (doc->light_dismiss_popovers.count(e)) {
        for (size_t i = doc->popovers.size(); i-- > 0;) {
            const Element* child = doc->popovers[i];
            if (child == e || !doc->light_dismiss_popovers.count(child)) continue;
            const Element* ancestor = child;
            while (ancestor) {
                const auto parent = doc->popover_parents.find(ancestor);
                ancestor = parent == doc->popover_parents.end() ? nullptr : parent->second;
                if (ancestor == e) {
                    const auto status = request_popover_hide(doc, doc->handle_of(child), false);
                    if (status != WEVA_OK) return status;
                    break;
                }
            }
        }
    }
    weva_event event{};
    event.kind = WEVA_EVENT_BEFORE_TOGGLE;
    event.target = element;
    std::strcpy(event.text, "closed");
    doc->fill_handler(&event, e);
    weva_document::QueuedEvent queued(event);
    queued.popover_restore_focus = restore_focus;
    queued.popover_hide_target = Ref<Element>::retain(e);
    return doc->push_event(std::move(queued)) ? WEVA_OK : WEVA_ERR_INVALID_STATE;
}

void complete_popover_open(weva_document* doc, Ref<Element> target, Ref<Element> source, int expected_mode,
                           std::vector<Ref<Element>> pending_closes = {}, bool resuming = false) {
    if (!resuming || pending_closes.empty()) {
        if (!target || doc->handle_of(target.get()) == WEVA_ELEMENT_NONE ||
            !target->has_attribute("popover") || target->is_popover_open() || target->is_modal() ||
            popover_mode_id(*target) != expected_mode) return;
        if (source && doc->handle_of(source.get()) == WEVA_ELEMENT_NONE) source = {};
        std::vector<const Element*> roots;
        if (popover_is_auto(*target)) {
            const bool hint = popover_is_hint(*target);
            for (auto it = doc->popovers.rbegin(); it != doc->popovers.rend(); ++it) {
                const Element* open = *it;
                if (!popover_is_auto(*open)) continue;
                if (is_within(target.get(), open) || is_within(source.get(), open)) break;
                if (!hint || popover_is_hint(*open)) roots.push_back(open);
            }
        }
        for (const Element* candidate : doc->popovers) {
            if (!doc->light_dismiss_popovers.count(candidate)) continue;
            const Element* ancestor = candidate;
            while (ancestor) {
                if (std::find(roots.begin(), roots.end(), ancestor) != roots.end()) {
                    pending_closes.push_back(Ref<Element>::retain(const_cast<Element*>(candidate)));
                    break;
                }
                const auto parent = doc->popover_parents.find(ancestor);
                ancestor = parent == doc->popover_parents.end() ? nullptr : parent->second;
            }
        }
        if (pending_closes.empty()) {
            popover_show(doc, *target, source.get());
            return;
        }
    }
    // Finish the captured old stack even if a closing handler invalidates the
    // replacement. Browser opening eligibility is rechecked after that work.
    Ref<Element> closing;
    while (!pending_closes.empty()) {
        closing = std::move(pending_closes.back());
        pending_closes.pop_back();
        if (doc->handle_of(closing.get()) != WEVA_ELEMENT_NONE && closing->is_popover_open()) break;
        closing = {};
    }
    if (!closing) {
        complete_popover_open(doc, std::move(target), std::move(source), expected_mode);
        return;
    }
    // An accepted opening owns this continuation. Process one close at a time
    // ahead of unrelated queued requests; its two internal slots cannot be
    // consumed by a handler filling the public queue. At most kMaxEvents + 2
    // entries exist: this pair is consumed before another pair is scheduled.
    weva_event event{};
    weva_document::QueuedEvent continuation(event);
    continuation.popover_pending_closes = std::move(pending_closes);
    continuation.popover_resume_open = true;
    continuation.popover_open_mode = expected_mode;
    continuation.popover_open_target = std::move(target);
    continuation.popover_open_source = std::move(source);
    doc->events.push_front(std::move(continuation));
    weva_event close_event{};
    close_event.kind = WEVA_EVENT_BEFORE_TOGGLE;
    close_event.target = doc->handle_of(closing.get());
    std::strcpy(close_event.text, "closed");
    doc->fill_handler(&close_event, closing.get());
    weva_document::QueuedEvent close(close_event);
    close.popover_restore_focus = false;
    close.popover_hide_target = std::move(closing);
    doc->events.push_front(std::move(close));
}

void popover_hide(weva_document* doc, Element& e, bool restore_focus) {
    if (!e.is_popover_open()) return;
    // Stack replacement suppresses restoration; an explicit close can return
    // focus to the saved control. Discard before the live-state notification.
    if (!restore_focus) doc->popover_previous_focus.erase(&e);
    // The live-state observer performs dismissal bookkeeping and queues one
    // notification, including closes caused by changing the popover attribute.
    e.set_popover_open(false);
    e.remove_attribute("data-popover-open");
    doc->pending = worst(doc->pending, Invalidation::Boxes);
}

void popover_hide_above(weva_document* doc, const Element* e) {
    if (!doc->light_dismiss_popovers.count(e)) return;
    const auto found = std::find(doc->popovers.begin(), doc->popovers.end(), e);
    if (found == doc->popovers.end()) return;
    const size_t index = static_cast<size_t>(found - doc->popovers.begin());
    for (size_t i = doc->popovers.size(); i-- > index + 1;) {
        auto* child = const_cast<Element*>(doc->popovers[i]);
        const Element* ancestor = child;
        while (ancestor) {
            const auto entry = doc->popover_parents.find(ancestor);
            ancestor = entry == doc->popover_parents.end() ? nullptr : entry->second;
            if (ancestor == e) { popover_hide(doc, *child, false); break; }
        }
    }
}

// Escape closes ONE, the topmost auto one -- so a submenu closes before the
// menu it opened from, and a manual popover in between is stepped over rather
// than closed.
bool popover_hide_top_auto(weva_document* doc) {
    for (size_t i = doc->popovers.size(); i-- > 0;) {
        Element& e = const_cast<Element&>(*doc->popovers[i]);
        if (!popover_is_auto(e)) continue;
        if (doc->popover_request_events)
            return weva_element_request_hide_popover(doc, doc->handle_of(&e)) == WEVA_OK;
        popover_hide(doc, e);
        return true;
    }
    return false;
}

// The nearest ancestor (or self) carrying `popovertarget`, so a click on the
// label or the icon inside a trigger button still counts as the trigger.
const Element* popover_trigger_at(const Element* target) {
    for (const Node* n = target; n; n = n->parent()) {
        if (n->node_type() != NodeType::Element) continue;
        const Element& e = static_cast<const Element&>(*n);
        if (e.has_attribute("popovertarget")) return &e;
    }
    return nullptr;
}

const Element* popover_pointer_ancestor(weva_document* doc, const Element* hit) {
    const Element* trigger = popover_trigger_at(hit);
    const Element* target = trigger ? element_by_id(doc, std::string(trigger->get_attribute("popovertarget"))) : nullptr;
    for (auto entry = doc->popovers.rbegin(); entry != doc->popovers.rend(); ++entry)
        if (popover_is_auto(**entry) && (is_within(hit, *entry) || target == *entry)) return *entry;
    return nullptr;
}

void popover_pointer_release(weva_document* doc, const Element* hit) {
    if (!doc->popover_press_active) return;
    const Element* endpoint = popover_pointer_ancestor(doc, hit);
    const bool same_endpoint = endpoint == doc->popover_press_target;
    doc->popover_press_active = false;
    doc->popover_press_target = nullptr;
    if (!same_endpoint) return;
    for (size_t i = doc->popovers.size(); i-- > 0;) {
        auto* open = const_cast<Element*>(doc->popovers[i]);
        if (open == endpoint) break;
        if (popover_is_auto(*open)) {
            if (doc->popover_request_events) weva_element_request_hide_popover(doc, doc->handle_of(open));
            else popover_hide(doc, *open);
        }
    }
}

bool is_within(const Element* candidate, const Element* ancestor) {
    for (const Node* n = candidate; n; n = n->parent()) {
        if (n == ancestor) return true;
    }
    return false;
}

const Element* active_modal(const weva_document* doc) {
    for (auto entry = doc->dialog_focus_history.rbegin(); entry != doc->dialog_focus_history.rend(); ++entry)
        if (entry->dialog->is_modal()) return entry->dialog;
    return nullptr;
}

bool input_blocked(const weva_document* doc, const Element* target) {
    const Element* modal = active_modal(doc);
    return (target && form_is_inert(*target)) || (modal && !is_within(target, modal));
}
extern "C" bool focus_unavailable_now(weva_document* doc, const Element& target);
extern "C" bool focus_disabled(const Element& e, const weva_document* doc);

const Element* input_element_at(const weva_document* doc, double x, double y) {
    if (active_modal(doc) && (x < 0 || y < 0 || x >= doc->ctx.viewport_width_px || y >= doc->ctx.viewport_height_px)) return nullptr;
    const Element* hit = element_at_point(doc->tree, doc->root, x, y, &doc->ctx);
    // The modal backdrop targets its dialog, never a control underneath it.
    const Element* modal = active_modal(doc);
    if (modal && !is_within(hit, modal)) return form_is_inert(*modal) ? nullptr : modal;
    return hit;
}

// The <details> a click should toggle: the one whose own <summary> was hit.
// Walks up from the target, so a click on text or an icon inside the summary
// counts as a click on the summary.
Element* details_for_summary_click(const Element* target) {
    for (const Node* n = target; n; n = n->parent()) {
        if (n->node_type() != NodeType::Element) continue;
        const Element& e = static_cast<const Element&>(*n);
        if (e.tag_name() != "summary") continue;
        const Node* parent = e.parent();
        if (!parent || parent->node_type() != NodeType::Element) return nullptr;
        Element& owner = const_cast<Element&>(static_cast<const Element&>(*parent));
        if (owner.tag_name() != "details") return nullptr;
        for (const auto& child : owner.children()) {
            if (child->node_type() != NodeType::Element) continue;
            const auto& first = static_cast<const Element&>(*child);
            if (first.tag_name() == "summary") return &first == &e ? &owner : nullptr;
        }
        return nullptr;
    }
    return nullptr;
}

// The <form> an element is inside, if any. A submit is reported against the
// form rather than against whatever was pressed, since that is what a handler
// is written for.
Element* form_of(const Element* e, weva_document* = nullptr) {
    return e ? form_owner(*e) : nullptr;
}

// True for a control whose value is chosen rather than typed: there is no
// editing state to leave, so `input` and `change` are the same moment.
bool commits_immediately(const Element& e) {
    const std::string_view tag = e.tag_name();
    if (tag == "select") return true;
    if (tag != "input") return false;
    const std::string type = input_type_of(const_cast<Element&>(e));
    return type == "checkbox" || type == "radio" || type == "range" || type == "color" ||
           type == "file";
}

size_t text_boundary(std::string_view text, int offset) {
    size_t at = static_cast<size_t>(std::clamp(offset, 0, static_cast<int>(text.size())));
    while (at > 0 && at < text.size() && (static_cast<unsigned char>(text[at]) & 0xc0) == 0x80) --at;
    return at;
}

void note_value_change(weva_document* doc, Element& e, std::string_view value, bool input = true) {
    if (e.tag_name() == "input" && form_input_type(e) == "number") value = e.form_value();
    if (&e == doc->styles.state.focused && is_text_field(e)) doc->user_edited_since_focus = true;
    // A select's caption/row inputs and its options' :checked states are
    // already versioned by the DOM observer. Selection cannot restyle every
    // descendant of the select just because it also emits a value event.
    if (e.tag_name() != "select") {
        doc->dom_touched = true;
        if (doc->touched.size() < 64) doc->touched.push_back(&e);
    }
    weva_event ev{};
    ev.kind = input ? WEVA_EVENT_VALUE_CHANGED : WEVA_EVENT_CHANGE;
    ev.target = doc->handle_of(&e);
    weva_document::fill_handler(&ev, &e);
    const size_t copy = text_boundary(value, static_cast<int>(std::min(value.size(), sizeof(ev.text) - 1)));
    std::memcpy(ev.text, value.data(), copy);
    ev.text[copy] = '\0';
    doc->push_event(weva_document::QueuedEvent(ev, value));
    if (input && commits_immediately(e)) {
        weva_event committed = ev;
        committed.kind = WEVA_EVENT_CHANGE;
        weva_document::fill_handler(&committed, &e);
        doc->push_event(weva_document::QueuedEvent(committed, value));
    }
}

// The row of the open list under a point, or -1 when the point is not on the
// list at all. The geometry comes from the same function paint uses, so a row
// you can see is a row you can click.
int select_row_at(weva_document* doc, double x, double y) {
    if (!doc->open_select) return -1;
    const BoxId box = box_of(doc, doc->open_select);
    if (box == kNoBox) return -1;
    const SelectListGeometry g =
        select_list_geometry(doc->tree, box, doc->ctx, *doc->open_select);
    if (!g.visible || !g.box.contains(x, y)) return -1;
    const int row = doc->select_first_row + static_cast<int>((y - g.box.y) / g.row_height);
    return row >= 0 && row < g.count ? row : -1;
}

// Scrolls the open list the least that shows the highlighted row -- the same
// nearest-edge rule the document uses for scrolling anything else into view.
void reveal_highlighted_option(weva_document* doc) {
    if (!doc->open_select) return;
    const BoxId box = box_of(doc, doc->open_select);
    if (box == kNoBox) return;
    const SelectListGeometry g =
        select_list_geometry(doc->tree, box, doc->ctx, *doc->open_select);
    if (!g.visible || g.rows <= 0) return;
    const int last = std::max(0, g.count - g.rows);
    int first = std::clamp(doc->select_first_row, 0, last);
    if (doc->highlighted_option >= 0) {
        const auto options = select_options(*doc->open_select);
        const auto rows = select_rows(*doc->open_select);
        const auto* highlighted = doc->highlighted_option < static_cast<int>(options.size())
            ? options[static_cast<size_t>(doc->highlighted_option)] : nullptr;
        const auto it = std::find(rows.begin(), rows.end(), highlighted);
        const int row = it == rows.end() ? -1 : static_cast<int>(it - rows.begin());
        if (row >= 0 && row < first) first = row;
        else if (row >= first + g.rows) {
            first = row - g.rows + 1;
        }
    }
    doc->select_first_row = std::clamp(first, 0, last);
}

// What a list box holds: the chosen options' values, comma separated, since a
// `multiple` one can have several and a host needs all of them.
std::string list_box_value(const Element& select) {
    std::string out;
    for (const Element* option : select_options(select)) {
        if (!option->form_selected()) continue;
        std::string value = option_value(*option);
        if (!out.empty()) out += ",";
        out += value;
    }
    return out;
}

// The <select> a clicked <option> belongs to, when that select is a LIST box
// -- one with parsed size > 1 or `multiple`, whose options are laid out in flow rather
// than hidden behind a closed control. A click on a row of one of those is a
// choice; a click on an option anywhere else cannot happen, because they are
// omitted by box building.
Element* list_box_of(const Element* option) {
    if (!option || option->tag_name() != "option") return nullptr;
    for (const Node* n = option->parent(); n; n = n->parent()) {
        if (n->node_type() != NodeType::Element) continue;
        Element& e = const_cast<Element&>(static_cast<const Element&>(*n));
        if (e.tag_name() == "optgroup") continue;   // a group is not the control
        if (e.tag_name() != "select") return nullptr;
        const bool list = select_is_listbox(e);
        return list ? &e : nullptr;
    }
    return nullptr;
}

// Live selectedness changes; selected attributes remain the reset defaults.
void choose_option(weva_document* doc, Element& select, int index) {
    const std::vector<const Element*> options = select_options(select);
    if (index < 0 || index >= static_cast<int>(options.size())) return;
    if (option_disabled(*options[static_cast<size_t>(index)]) ||
        options[static_cast<size_t>(index)]->form_selected()) return;
    const_cast<Element*>(options[static_cast<size_t>(index)])->set_form_selected(true);
    const Element* chosen = options[static_cast<size_t>(index)];
    // An option's value is its `value` attribute, or its own text when it has
    // none -- which is how the markup for a plain list of choices works.
    std::string value = option_value(*chosen);
    note_value_change(doc, select, value);
}

// Which option is chosen now, or -1.
int chosen_index(const Element& select) {
    const std::vector<const Element*> options = select_options(select);
    for (size_t i = 0; i < options.size(); ++i) {
        if (options[i]->form_selected()) return static_cast<int>(i);
    }
    return -1;
}

std::vector<const Element*> selected_options(const Element& select) {
    auto options = select_options(select);
    options.erase(std::remove_if(options.begin(), options.end(),
                  [](const Element* e) { return !e->form_selected(); }), options.end());
    return options;
}
void invalidate_list_row(weva_document* doc, const Element* select) {
    if (!select) return;
    ++doc->styles.list_input_versions[select].first;
    doc->styles.pending_control_inputs.insert(select);
}
weva_document::ListSelection& list_selection(weva_document* doc, const Element& select) {
    auto& state = doc->list_selections[&select];
    if (state.version != select.form_version()) {
        // Script/default mutations establish a new starting point. Comparing
        // versions also avoids keeping an anchor after its option was removed.
        state = {};
        const auto options = selected_options(select);
        state.active = state.anchor = options.empty() ? nullptr : options.front();
        state.version = select.form_version();
    }
    return state;
}
int option_index(const std::vector<const Element*>& options, const Element* option) {
    const auto it = std::find(options.begin(), options.end(), option);
    return it == options.end() ? -1 : static_cast<int>(it - options.begin());
}
bool navigable_option(weva_document* doc, const Element* option) {
    if (option_disabled(*option)) return false;
    for (const Node* n = option; n && n->is_element(); n = n->parent()) {
        const auto* e = static_cast<const Element*>(n);
        const auto it = doc->styles.by_element.find(e);
        if (it != doc->styles.by_element.end() && (it->second->get("display") == "none" ||
            it->second->get("visibility") == "hidden" || it->second->get("visibility") == "collapse")) return false;
        if (e->tag_name() == "select") break;
    }
    return true;
}
int next_option(weva_document* doc, const std::vector<const Element*>& options, int from, int step) {
    for (int i = from + step; i >= 0 && i < static_cast<int>(options.size()); i += step)
        if (navigable_option(doc, options[static_cast<size_t>(i)])) return i;
    return from >= 0 && from < static_cast<int>(options.size()) &&
           navigable_option(doc, options[static_cast<size_t>(from)]) ? from : -1;
}
void set_list_active(weva_document* doc, const Element& select,
                     weva_document::ListSelection& state, const Element* active) {
    if (state.active != active) {
        state.active = active;
        invalidate_list_row(doc, &select);
    }
    doc->list_follow = active;
    bring_box_into_view(doc, box_of(doc, active));
}
void select_list_range(weva_document* doc, Element& select, weva_document::ListSelection& state,
                       const std::vector<const Element*>& options, int to, bool preserve = false,
                       bool selected = true) {
    const int anchor = option_index(options, state.anchor);
    const int first = std::min(anchor < 0 ? to : anchor, to), last = std::max(anchor < 0 ? to : anchor, to);
    for (int i = 0; i < static_cast<int>(options.size()); ++i) {
        auto* option = const_cast<Element*>(options[static_cast<size_t>(i)]);
        const bool in_range = i >= first && i <= last && navigable_option(doc, option);
        const bool next = in_range ? selected : preserve &&
            std::find(state.before_drag.begin(), state.before_drag.end(), option) != state.before_drag.end();
        option->set_form_selected(next);
    }
    state.version = select.form_version();
}
void list_pointer_press(weva_document* doc, Element& select, Element& option, uint32_t modifiers) {
    auto& state = list_selection(doc, select);
    const auto options = select_options(select);
    const int index = option_index(options, &option);
    if (index < 0 || !navigable_option(doc, &option)) return;
    state.before_drag = selected_options(select);
    state.dragging = true;
    doc->list_drag = &select;
    doc->list_scroll_armed = false;
    const bool multiple = select.has_attribute("multiple");
    const bool shift = multiple && (modifiers & WEVA_MOD_SHIFT);
    state.toggle_drag = multiple && !shift && (modifiers & (WEVA_MOD_CTRL | WEVA_MOD_META));
    state.drag_selected = !state.toggle_drag || !option.form_selected();
    if (!shift || !state.anchor) state.anchor = &option;
    if (multiple) select_list_range(doc, select, state, options, index, state.toggle_drag, state.drag_selected);
    else option.set_form_selected(true);
    set_list_active(doc, select, state, &option);
    state.version = select.form_version();
}
bool control_drag_viewport(weva_document* doc, const Element* element, Rect* viewport) {
    if (!element || disabled_ancestor(element)) return false;
    const auto box = box_of(doc, element);
    if (box == kNoBox) return false;
    const auto& b = doc->tree[box];
    if (b.style && (b.style->get("display") == "none" || b.style->get("visibility") == "hidden" ||
                    b.style->get("visibility") == "collapse")) return false;
    double x=0, y=0;
    visual_position(doc->tree,box,&x,&y);
    *viewport = Rect(x + b.border_left, y + b.border_top,
                     b.width - b.border_left - b.border_right, b.height - b.border_top - b.border_bottom);
    return viewport->width > 0 && viewport->height > 0;
}
bool list_drag_viewport(weva_document* doc, const Element* select, Rect* viewport) {
    if (!select || !select_is_listbox(*select)) return false;
    const auto found = doc->list_selections.find(select);
    return found != doc->list_selections.end() && found->second.dragging &&
        control_drag_viewport(doc,select,viewport);
}
void list_pointer_drag(weva_document* doc, const Element* hit) {
    if (!doc->press_target) return;
    Element* select = list_box_of(doc->press_target);
    Rect viewport;
    // Moving inside the captured list arms autoscroll. Subsequent moves may
    // leave it; the capture continues to own the held gesture.
    if (list_drag_viewport(doc, select, &viewport)) {
        double x = doc->pointer_x, y = doc->pointer_y;
        if (point_to_layout(doc->tree, box_of(doc, select), doc->ctx, &x, &y) && viewport.contains(x, y))
            doc->list_scroll_armed = true;
    }
    if (!select || list_box_of(hit) != select || !navigable_option(doc, hit)) return;
    auto& state = list_selection(doc, *select);
    if (!state.dragging || state.active == hit) return;
    const auto options = select_options(*select);
    if (select->has_attribute("multiple"))
        select_list_range(doc, *select, state, options, option_index(options, hit), state.toggle_drag, state.drag_selected);
    else const_cast<Element*>(hit)->set_form_selected(true);
    set_list_active(doc, *select, state, hit);
    state.version = select->form_version();
}
void finish_list_drag(weva_document* doc) {
    doc->list_drag = nullptr;
    doc->list_scroll_armed = false;
    for (auto& entry : doc->list_selections) {
        auto& state = entry.second;
        if (!state.dragging) continue;
        state.dragging = false;
        if (state.before_drag != selected_options(*entry.first))
            note_value_change(doc, *const_cast<Element*>(entry.first), list_box_value(*entry.first));
        state.before_drag.clear();
    }
}
BoxId advance_list_autoscroll(weva_document* doc, double seconds) {
    if (!doc->list_drag || !doc->list_scroll_armed || !(seconds > 0) || !std::isfinite(seconds)) return kNoBox;
    Rect viewport;
    if (!(doc->buttons_last & WEVA_BUTTON_PRIMARY) || !list_drag_viewport(doc,doc->list_drag,&viewport)) {
        finish_list_drag(doc);
        return kNoBox;
    }
    const auto box = box_of(doc,doc->list_drag);
    double pointer_x = doc->pointer_x, pointer_y = doc->pointer_y;
    if (!point_to_layout(doc->tree, box, doc->ctx, &pointer_x, &pointer_y)) return kNoBox;
    const auto& b = doc->tree[box];
    const auto overflow = b.style ? b.style->get("overflow-y") : std::string_view();
    if (overflow == "hidden" || overflow == "clip") return kNoBox;
    const double edge = std::min(20.0, viewport.height * 0.25);
    const double above = viewport.y + edge - pointer_y;
    const double below = pointer_y - (viewport.bottom() - edge);
    const double distance = above > 0 ? -above : below > 0 ? below : 0;
    if (distance == 0) return kNoBox;
    double mx=0, my=0;
    max_scroll(doc->tree,box,&mx,&my);
    const auto held = doc->scroll.find(doc->list_drag);
    const double from = held == doc->scroll.end() ? b.scroll_y : held->second.second;
    const double speed = std::min(1600.0, 120.0 + std::abs(distance) * 30.0);
    // Limit one delayed frame's travel while keeping speed independent of FPS.
    const double to = std::clamp(from + (distance < 0 ? -1 : 1) * speed * std::min(seconds,0.1), 0.0, my);
    if (to == from) return kNoBox;
    const double x = held == doc->scroll.end() ? b.scroll_x : held->second.first;
    auto& offset = doc->scroll[doc->list_drag];
    offset = {x,to};
    doc->tree[box].scroll_y = to;
    doc->pending = worst(doc->pending,Invalidation::Paint);
    doc->queue_event(WEVA_EVENT_SCROLL,doc->list_drag,offset.first,to,0);
    // Selection follows real pointer moves over options, as in Chrome. A
    // timed scroll itself neither chooses a row nor commits input/change.
    return box;
}
bool list_key(weva_document* doc, Element& select, int key, uint32_t modifiers) {
    const bool multiple = select.has_attribute("multiple");
    const bool control = (modifiers & (WEVA_MOD_CTRL | WEVA_MOD_META)) != 0;
    const bool shift = (modifiers & WEVA_MOD_SHIFT) != 0;
    auto& state = list_selection(doc, select);
    const auto options = select_options(select);
    const int count = static_cast<int>(options.size());
    const int from = option_index(options, state.active);
    if (key == WEVA_KEY_SPACE) {
        if (multiple && control && from >= 0 && navigable_option(doc, state.active)) {
            auto* option = const_cast<Element*>(state.active);
            option->set_form_selected(!option->form_selected());
            state.anchor = option;
            state.version = select.form_version();
            note_value_change(doc, select, list_box_value(select));
        }
        return true;
    }
    if (key == WEVA_KEY_ENTER) return true;
    if (key != WEVA_KEY_UP && key != WEVA_KEY_DOWN && key != WEVA_KEY_HOME && key != WEVA_KEY_END &&
        key != WEVA_KEY_PAGE_UP && key != WEVA_KEY_PAGE_DOWN) return false;
    int to = from;
    if (key == WEVA_KEY_HOME || key == WEVA_KEY_END) {
        to = next_option(doc, options, key == WEVA_KEY_HOME ? -1 : count, key == WEVA_KEY_HOME ? 1 : -1);
    } else {
        const int step = key == WEVA_KEY_DOWN || key == WEVA_KEY_PAGE_DOWN ? 1 : -1;
        if (from < 0) to = next_option(doc, options, step > 0 ? -1 : count, step);
        else if (key == WEVA_KEY_PAGE_UP || key == WEVA_KEY_PAGE_DOWN) {
            // Page distances count disabled rows and optgroup headings too.
            std::vector<const Element*> rows;
            const Node* group = nullptr;
            for (const auto* option : options) {
                const Node* parent = option->parent();
                if (parent != &select && parent != group && parent && parent->is_element())
                    rows.push_back(static_cast<const Element*>(parent));
                group = parent;
                rows.push_back(option);
            }
            const int row = option_index(rows, state.active);
            const int distance = std::min(std::max(1, select_display_size(select) - 1), static_cast<int>(rows.size()));
            const int dest = std::clamp(row + step * distance, 0, static_cast<int>(rows.size()) - 1);
            for (int i = dest; i >= 0 && i < static_cast<int>(rows.size()); i += step) {
                const int candidate = option_index(options, rows[static_cast<size_t>(i)]);
                if (candidate >= 0 && navigable_option(doc, options[static_cast<size_t>(candidate)])) { to = candidate; break; }
            }
        } else to = next_option(doc, options, from, step);
    }
    if (to < 0 || to == from) return true;
    const auto before = selected_options(select);
    if (multiple) {
        if (shift) {
            if (!state.anchor) state.anchor = from < 0 ? options[static_cast<size_t>(to)] : state.active;
            select_list_range(doc, select, state, options, to);
        } else if (!control) {
            state.anchor = options[static_cast<size_t>(to)];
            select_list_range(doc, select, state, options, to);
        }
    } else const_cast<Element*>(options[static_cast<size_t>(to)])->set_form_selected(true);
    set_list_active(doc, select, state, options[static_cast<size_t>(to)]);
    state.version = select.form_version();
    if (before != selected_options(select)) note_value_change(doc, select, list_box_value(select));
    return true;
}

double input_time() {
    return std::chrono::duration<double>(std::chrono::steady_clock::now().time_since_epoch()).count();
}
bool select_typeahead(weva_document* doc, Element& select, std::string_view character) {
    if (!typeahead_printable(character)) return false;
    if (doc->typeahead_target != &select) {
        doc->typeahead.reset();
        doc->typeahead_target = &select;
    }
    const bool advance = doc->typeahead.append(character, input_time());
    UnicodePrefixSearch search(doc->typeahead.prefix());
    if (!search.valid()) {
        std::fprintf(stderr, "weva: select typeahead could not initialize its embedded Unicode search data\n");
        return true;
    }
    const auto rows = select_rows(select);
    if (rows.empty()) return true;
    const auto options = select_options(select);
    int current = chosen_index(select);
    if (doc->open_select == &select) current = doc->highlighted_option;
    const auto* selected = current >= 0 && current < static_cast<int>(options.size()) ? options[static_cast<size_t>(current)] : nullptr;
    const int start = std::max(0, option_index(rows, selected)) + (advance ? 1 : 0);
    for (size_t n = 0; n < rows.size(); ++n) {
        const auto* option = rows[(static_cast<size_t>(start) + n) % rows.size()];
        if (option->tag_name() != "option" || option_disabled(*option)) continue;
        if (doc->open_select == &select && !navigable_option(doc, option)) continue;
        if (!search.matches(option_label(*option))) continue;
        const int index = option_index(options, option);
        if (doc->open_select == &select) {
            doc->highlighted_option = index;
            reveal_highlighted_option(doc);
            doc->pending = worst(doc->pending, Invalidation::Paint);
        } else if (select_is_listbox(select)) {
            auto& state = list_selection(doc, select);
            state.anchor = option;
            if (select.has_attribute("multiple")) {
                for (const auto* candidate : options)
                    const_cast<Element*>(candidate)->set_form_selected(candidate == option);
            }
            else const_cast<Element*>(option)->set_form_selected(true);
            set_list_active(doc, select, state, option);
            state.version = select.form_version();
            // Chrome's native listbox typeahead dispatches change for every
            // successful match, including prefix refinement on the same row.
            note_value_change(doc, select, list_box_value(select), false);
        } else choose_option(doc, select, index);
        return true;
    }
    return true; // Printable select input remains owned even without a match.
}

// A paint context with only what MEASURING needs. Text is mapped back to
// characters with the same shaping, face and letter-spacing it was drawn with;
// anything less and a click lands a character off.
PaintContext measuring_context(weva_document* doc) {
    PaintContext paint;
    paint.font = doc->font_backend();
    paint.atlas = &doc->atlas;
    paint.face = doc->face;
    return paint;
}

size_t field_offset_at(weva_document* doc, const Element& e, double x, double y) {
    const BoxId box = box_of(doc, &e);
    if (box == kNoBox) return 0;
    if (!point_to_layout(doc->tree, box, doc->ctx, &x, &y)) return 0;
    PaintContext paint = measuring_context(doc);
    paint.caret = caret_for(doc->styles.state);
    if (e.tag_name() != "textarea") {
        return control_text_offset_at(doc->tree, box, doc->ctx, paint, x);
    }
    const std::string_view source = e.form_edit_value();
    if (source.empty()) return 0;
    double ox = 0, oy = 0;
    visual_position(doc->tree, box, &ox, &oy);
    return run_text_offset_at(doc->tree, box, doc->ctx, paint, source, ox, oy, x, y);
}

void clear_text_drag(weva_document* doc) {
    doc->text_drag = nullptr;
    doc->text_scroll_armed = false;
}
bool text_drag_viewport(weva_document* doc, Rect* viewport) {
    return doc->text_drag && doc->text_drag == doc->styles.state.focused &&
        !doc->styles.state.composing &&
        is_text_field(*doc->text_drag) && control_drag_viewport(doc,doc->text_drag,viewport);
}
double text_autoscroll_step(double pointer, double start, double extent, double seconds) {
    const double edge = std::min(20.0,extent * 0.25);
    const double before = start + edge - pointer;
    const double after = pointer - (start + extent - edge);
    const double distance = before > 0 ? -before : after > 0 ? after : 0;
    if (distance == 0) return 0;
    const double speed = std::min(1600.0,120.0 + std::abs(distance) * 30.0);
    return (distance < 0 ? -1 : 1) * speed * std::min(seconds,0.1);
}
BoxId advance_text_autoscroll(weva_document* doc, double seconds) {
    if (!doc->text_drag || !doc->text_scroll_armed || !(seconds > 0) || !std::isfinite(seconds)) return kNoBox;
    Rect viewport;
    if (!(doc->buttons_last & WEVA_BUTTON_PRIMARY) || !text_drag_viewport(doc,&viewport)) {
        clear_text_drag(doc);
        return kNoBox;
    }
    const auto box = box_of(doc,doc->text_drag);
    double pointer_x = doc->pointer_x, pointer_y = doc->pointer_y;
    if (!point_to_layout(doc->tree, box, doc->ctx, &pointer_x, &pointer_y)) return kNoBox;
    auto& state = doc->styles.state;
    auto& b = doc->tree[box];
    const bool multiline = doc->text_drag->tag_name() == "textarea";
    double mx=0,my=0,from_x=0,from_y=0;
    if (multiline) {
        max_scroll(doc->tree,box,&mx,&my);
        const auto held = doc->scroll.find(doc->text_drag);
        from_x = held == doc->scroll.end() ? b.scroll_x : held->second.first;
        from_y = held == doc->scroll.end() ? b.scroll_y : held->second.second;
    } else {
        auto measure = measuring_context(doc);
        measure.caret = caret_for(state);
        from_x = input_text_scroll(doc->tree,box,doc->ctx,measure,false,&mx);
    }
    const double x = std::clamp(from_x + text_autoscroll_step(pointer_x,viewport.x,viewport.width,seconds),0.0,mx);
    const double y = multiline ? std::clamp(from_y + text_autoscroll_step(pointer_y,viewport.y,viewport.height,seconds),0.0,my) : 0;
    if (x == from_x && y == from_y) return kNoBox;
    if (multiline) {
        doc->scroll[doc->text_drag] = {x,y};
        b.scroll_x = x; b.scroll_y = y;
    } else state.text_scroll_x = x;
    // Selection, unlike listbox selectedness, follows newly exposed text
    // without another mousemove. Keep the initial anchor and source offsets.
    const int to = static_cast<int>(field_offset_at(doc,*doc->text_drag,doc->pointer_x,doc->pointer_y));
    if (state.anchor < 0) state.anchor = state.caret;
    state.caret = to;
    state.caret_age = 0;
    doc->pending = worst(doc->pending,Invalidation::Paint);
    doc->queue_event(WEVA_EVENT_SCROLL,doc->text_drag,x,y,0);
    return box;
}
BoxId advance_input_autoscroll(weva_document* doc, double seconds) {
    return doc->text_drag ? advance_text_autoscroll(doc,seconds) : advance_list_autoscroll(doc,seconds);
}

// Where the word around `at` begins and ends. A word is a run of letters,
// digits and underscores; anything else is a separator, and double-clicking
// one of those takes just it -- which is what a browser does.
void word_around(const std::string& value, size_t at, size_t* from, size_t* to) {
    // The classifier, not a byte test: every byte above ASCII used to count as
    // a word character here, which made a double click over Japanese select
    // the entire run instead of one character.
    word_range_at(value, at, from, to);
}


}   // namespace

extern "C" {

uint32_t weva_abi_version(void) {
    return (static_cast<uint32_t>(WEVA_ABI_VERSION_MAJOR) << 16) | WEVA_ABI_VERSION_MINOR;
}

void weva_document_set_render_backend(weva_document_t doc, const weva_render_backend* backend) {
    if (!doc) return;
    doc->backend.retain_published_textures();
    // Cached textures are handles the OLD backend issued, and the draws that
    // reference them went to it too. Both have to be given up here, while the
    // backend that owns them is still the one installed.
    doc->textures.release_all(doc->render_backend());
    doc->atlas.release_texture(doc->render_backend());
    for (TextureHandle t : doc->transient_textures) doc->render_backend()->release_texture(t);
    doc->transient_textures.clear();
    doc->pending = Invalidation::Boxes;
    if (!backend) { doc->host_render.reset(); return; }
    doc->host_render = std::make_unique<HostRenderBackend>(*backend, &doc->backend);
}

namespace {

// ctx.variant_metrics for a document: the host's variant of the default
// face, measured through the same FontInterface paint draws with.
const FontMetrics* document_variant_metrics(void* user, const FontMetrics* base, int weight,
                                            bool italic) {
    auto* doc = static_cast<weva_document*>(user);
    if (!doc || !doc->host_font) return base;
    const FaceHandle source = base ? base->rendering_face(doc->host_font.get()) : doc->face;
    if (!source.id) return base;
    const FaceHandle v = doc->host_font->variant(source, weight, italic);
    if (v.id == source.id) return base;
    const auto key = std::make_tuple(v.id, weight, italic);
    auto it = doc->variant_metrics.find(key);
    if (it == doc->variant_metrics.end()) {
        it = doc->variant_metrics
                 .emplace(key, std::make_unique<FontInterfaceMetrics>(doc->host_font.get(), v))
                 .first;
    }
    return it->second.get();
}

} // namespace

void weva_document_set_font_backend(weva_document_t doc, const weva_font_backend* backend,
                                    uint64_t face) {
    if (!doc) return;
    doc->atlas.clear();
    doc->ctx.fonts.clear();
    doc->family_metrics.clear();
    doc->variant_metrics.clear();
    // A different face measures differently, so every line box is suspect.
    doc->pending = Invalidation::Boxes;
    if (!backend) {
        doc->host_font.reset();
        doc->host_metrics.reset();
        doc->face = StubFont::builtin();
        doc->ctx.variant_metrics = nullptr;
        doc->ctx.variant_user = nullptr;
        return;
    }
    doc->host_font = std::make_unique<HostFontBackend>(*backend, &doc->font);
    doc->host_font->set_rounds_leading(doc->font_leading_rounding);
    // A zero face means "use whatever the backend's load_face returned", which
    // a host that has only one face can leave alone.
    doc->face = face ? FaceHandle{face} : StubFont::builtin();
    doc->host_metrics = std::make_unique<FontInterfaceMetrics>(doc->host_font.get(), doc->face);
    doc->ctx.variant_metrics = &document_variant_metrics;
    doc->ctx.variant_user = doc;
}

weva_status weva_document_set_font_shaper(weva_document_t doc, weva_shape_glyphs_fn shape) {
    if (!doc || !doc->host_font) return WEVA_ERR_INVALID_ARGUMENT;
    if (!doc->host_font->set_shaper(shape)) return WEVA_OK;
    // The glyph metrics/bitmaps still belong to the same font table, but
    // changed positioning/advances invalidate every measured run and line.
    doc->variant_metrics.clear();
    doc->host_metrics = std::make_unique<FontInterfaceMetrics>(doc->host_font.get(), doc->face);
    for (auto& entry : doc->family_metrics) {
        const auto face = entry.second->rendering_face(doc->host_font.get());
        entry.second = std::make_unique<FontInterfaceMetrics>(doc->host_font.get(), face);
        doc->ctx.register_font(entry.first, entry.second.get());
    }
    doc->pending = Invalidation::Boxes;
    return WEVA_OK;
}

weva_status weva_document_register_font_family(weva_document_t doc, const char* family,
                                               uint64_t face) {
    if (!doc || !family || (face && !doc->host_font)) return WEVA_ERR_INVALID_ARGUMENT;
    LayoutContext normalized;
    normalized.register_font(family, &doc->metrics);
    if (normalized.fonts.empty() || normalized.fonts.front().first.empty())
        return WEVA_ERR_INVALID_ARGUMENT;
    const std::string& key = normalized.fonts.front().first;
    auto it = doc->family_metrics.find(key);
    if (!face) {
        if (it == doc->family_metrics.end()) return WEVA_OK;
        auto& fonts = doc->ctx.fonts;
        fonts.erase(std::remove_if(fonts.begin(), fonts.end(),
            [&](const auto& entry) { return entry.first == key; }), fonts.end());
        doc->family_metrics.erase(it);
    } else {
        if (it != doc->family_metrics.end() &&
            it->second->rendering_face(doc->host_font.get()).id == face) return WEVA_OK;
        auto metrics = std::make_unique<FontInterfaceMetrics>(doc->host_font.get(), FaceHandle{face});
        doc->ctx.register_font(key, metrics.get());
        doc->family_metrics[key] = std::move(metrics);
    }
    doc->variant_metrics.clear();
    doc->pending = Invalidation::Boxes;
    return WEVA_OK;
}

weva_document_t weva_document_create(const weva_config* config) {
    auto* d = new weva_document();
    d->container_queries.attach(&d->styles.engine);
    if (config) d->config = *config;
    if (d->config.viewport_width <= 0) d->config.viewport_width = 1920;
    if (d->config.viewport_height <= 0) d->config.viewport_height = 1080;
    if (d->config.root_font_size <= 0) d->config.root_font_size = 16;
    // Layout needs it too, not just paint: a replaced element's `auto` width
    // is its INTRINSIC width, and only the store can say what that is.
    d->ctx.images = &d->images;
    d->ctx.viewport_width_px = d->config.viewport_width;
    d->ctx.viewport_height_px = d->config.viewport_height;
    d->ctx.root_font_size_px = d->config.root_font_size;
    auto media = d->styles.engine.media_context();
    media.viewport_width_px = d->config.viewport_width;
    media.viewport_height_px = d->config.viewport_height;
    d->styles.engine.set_media_context(media);

    if (d->config.use_user_agent_stylesheet) {
        auto ua = std::make_unique<Stylesheet>();
        CssParseError err;
        if (parse_stylesheet(user_agent_stylesheet_source(), false, ua.get(), &err)) {
            d->styles.engine.add_stylesheet(ua.get(), DeclarationOrigin::UserAgent);
            d->ua_sheet = std::move(ua);
        }
    }
    return d;
}

void weva_document_destroy(weva_document_t doc) {
    // Cached textures outlive a pass, so the host is told to drop them here
    // rather than leaking them for the life of the process.
    if (doc) {
        doc->textures.release_all(doc->render_backend());
        doc->atlas.release_texture(doc->render_backend());
        for (TextureHandle t : doc->transient_textures) doc->render_backend()->release_texture(t);
    }
    delete doc;
}

weva_status weva_document_load_html(weva_document_t doc, const char* html, size_t length) {
    if (!doc || (!html && length > 0)) return WEVA_ERR_INVALID_ARGUMENT;
    HtmlParseError err;
    ParseOptions opts;
    opts.strict = false;
    doc->doc = parse_html(std::string_view(html ? html : "", length), &doc->symbols, opts, &err);
    if (!doc->doc) return WEVA_ERR_PARSE;
    doc->doc->set_popover_attribute_close_handler([doc](Element& element) {
        return doc->popover_request_events &&
            request_popover_hide(doc, doc->handle_of(&element), true, true) == WEVA_OK;
    });
    doc->doc->add_observer([doc](const DomMutation& mutation) {
        if (mutation.kind == MutationKind::ChildAdded || mutation.kind == MutationKind::ChildRemoved ||
            mutation.name == "type" || mutation.name == "form" || mutation.name == "id" ||
            mutation.name == "command" || mutation.name == "commandfor") doc->default_inputs_dirty = true;
        if (mutation.kind == MutationKind::ChildAdded || mutation.kind == MutationKind::ChildRemoved)
            ++doc->structure_version;
        if (doc->refreshing_bindings) note_binding_mutation(doc, mutation);
        if (mutation.kind != MutationKind::FormStateChanged || !mutation.target->is_element()) return;
        auto* e = static_cast<Element*>(mutation.target);
        const bool input_default = mutation.form_value == FormValueMutation::InputDefault;
        const bool textarea_default = mutation.form_value == FormValueMutation::TextareaDefault;
        if ((input_default || textarea_default) && e == doc->styles.state.focused && is_text_field(*e)) {
            auto& state = doc->styles.state;
            state.caret = 0;
            state.anchor = -1;
            state.vertical_owner = nullptr;
            state.caret_downstream = true;
            state.caret_age = 0;
            doc->caret_follow = true;
            doc->styles.repaint(e);
        }
        if (e != doc->styles.state.focused) {
            const auto saved = doc->field_selections.find(e);
            if (saved != doc->field_selections.end()) {
                const auto value = e->form_edit_value();
                if (textarea_default || saved->second.value != value) {
                    saved->second.value.assign(value);
                    if (!input_default) {
                        saved->second.caret = textarea_default ? 0 : static_cast<int>(value.size());
                        saved->second.anchor = -1;
                    }
                }
            }
        }
        doc->styles.pending_control_inputs.insert(e);
        const auto seen = doc->styles.form_input_versions.find(e);
        const auto current = doc->styles.state.state_of(*e);
        const uint32_t top_mask = static_cast<uint32_t>(ElementState::Modal) | static_cast<uint32_t>(ElementState::PopoverOpen);
        const uint32_t previous = seen == doc->styles.form_input_versions.end() ? 0 : static_cast<uint32_t>(seen->second.styled_state);
        if (((previous ^ static_cast<uint32_t>(current)) & top_mask) != 0) note_box_input(doc, e);
        if (!e->is_popover_open()) {
            popover_hide_above(doc, e);
            doc->light_dismiss_popovers.erase(e);
            doc->popover_parents.erase(e);
            const auto it = std::find(doc->popovers.begin(), doc->popovers.end(), e);
            if (it != doc->popovers.end()) {
                doc->popovers.erase(it);
                ++doc->transient_version;
                e->remove_attribute("data-popover-open");
                doc->queue_event(WEVA_EVENT_TOGGLE, e, 0, 0, 0, 0, "closed");
                const auto previous = doc->popover_previous_focus.find(e);
                if (previous != doc->popover_previous_focus.end()) {
                    const Element* restore = previous->second;
                    doc->popover_previous_focus.erase(previous);
                    if (is_within(doc->styles.state.focused, e))
                        weva_document_set_focus(doc, doc->handle_of(restore));
                }
            }
        }
        // Form mutations still update the control's content. Only changed
        // selector inputs need cascade: comparing against the last cascade
        // also preserves changes queued across intervening paint-only updates.
        if (seen != doc->styles.form_input_versions.end() &&
            seen->second.styled_state == current &&
            (!doc->styles.engine.has_validity_selectors() ||
             seen->second.styled_validity == form_validity_selector_state(*e)) &&
            (!doc->styles.engine.has_range_selectors() ||
             seen->second.styled_range == form_range_selector_state(*e))) return;
        doc->dom_touched = true;
        if (doc->touched.size() < 64 && std::find(doc->touched.begin(), doc->touched.end(), e) == doc->touched.end())
            doc->touched.push_back(e);
    });

    // Components (`<template id="card">` + `<card>` + `<slot>`) expand BEFORE
    // the cascade, as UIDocumentBuilder does, so selectors match the expanded
    // subtree and not the un-rendered host.
    expand_components(doc->doc.get());

    doc->elements.clear();
    doc->dialog_close_order.clear();
    doc->element_handles.clear();
    doc->query_order.clear();
    ++doc->structure_version;
    for (const Ref<Node>& c : doc->doc->children()) {
        if (c->node_type() == NodeType::Element) {
            doc->index_elements(static_cast<Element&>(const_cast<Node&>(*c)));
        }
    }
    // Every style is keyed on an element of the old document, so none of them
    // can be reused and the walk would otherwise see a page of new elements.
    doc->styles.clear();
    doc->container_queries.attach(&doc->styles.engine);
    doc->pending = Invalidation::Boxes;
    // Those point at elements of the document just replaced.
    doc->touched.clear();
    doc->default_buttons.clear();
    doc->default_inputs_dirty = true;
    doc->dom_touched = false;
    doc->events.clear();
    doc->polled_event_text.clear();
    doc->polled_popover_open_source = {};
    doc->polled_popover_hide_target = {};
    doc->polled_popover_open_target = {};
    doc->polled_close_target = {};
    doc->polled_close_value.clear();
    doc->polled_submit_form = {};
    doc->polled_submitter = {};
    doc->polled_image_coordinates.clear();
    doc->polled_invalid_target = {};
    doc->polled_validation_report.reset();
    doc->dialog_return_values.clear();
    doc->dialog_close_generations.clear();
    doc->press_target = nullptr;
    doc->space_press_target = nullptr;
    doc->popover_press_target = nullptr;
    doc->popover_press_active = false;
    doc->activation_keys_down = 0;
    doc->radio_focus_memory.clear();
    doc->list_selections.clear();
    doc->list_follow = nullptr;
    doc->typeahead.reset();
    doc->list_drag = nullptr;
    doc->list_scroll_armed = false;
    doc->typeahead_target = nullptr;
    clear_text_drag(doc);
    doc->scroll.clear();   // keyed on elements of the document just replaced
    doc->set_open_select(nullptr);
    doc->highlighted_option = -1;
    doc->binding_templates.clear();
    doc->binding_repeats.clear();
    doc->focus_follow = nullptr;
    doc->caret_painted = CaretState{};
    doc->scroll_drag = weva_document::ScrollDrag{};
    doc->popovers.clear();
    doc->light_dismiss_popovers.clear();
    doc->popover_parents.clear();
    doc->popover_previous_focus.clear();
    doc->dialog_focus_history.clear();
    doc->check_focus_after_dialog_close = false;
    doc->tooltip = weva_document::Tooltip{};
    doc->history.clear();
    doc->field_selections.clear();
    doc->composition_before = {};
    doc->composition_value.clear();
    doc->value_at_focus.clear();
    doc->user_edited_since_focus = false;
    // The interaction state too, which is what the pointer is OVER and what
    // has the focus. StyleMap::clear() left it alone, so after a reload the
    // hover chain still named elements of the replaced document -- and the
    // next pointer move raised a leave event on one of them, reading a freed
    // Node's vtable. UBSan caught it the first time a test reloaded HTML into
    // a live document and then moved the pointer.
    InteractionState& st = doc->styles.state;
    st.hover_chain.clear();
    st.active_chain.clear();
    st.focus_chain.clear();
    st.focused = nullptr;
    st.caret = 0;
    st.text_scroll_x = 0;
    st.anchor = -1;
    st.caret_age = 0;
    st.composing = false;
    st.composition_from = st.composition_to = 0;
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
    // The cascade caches selector matches by element shape, and the shapes did
    // not change -- the rules did.
    doc->styles.engine.invalidate_cache();
    doc->styles.keyframes = doc->styles.engine.keyframes();
    // New selectors can create/remove pseudo boxes even without a DOM
    // mutation. The lifecycle must observe the changed stylesheet input.
    doc->pending = Invalidation::Boxes;
    return WEVA_OK;
}

weva_status weva_document_set_css(weva_document_t doc, const char* css, size_t length) {
    if (!doc || (!css && length > 0)) return WEVA_ERR_INVALID_ARGUMENT;
    auto sheet = std::make_unique<Stylesheet>();
    CssParseError err;
    if (!parse_stylesheet(std::string_view(css ? css : "", length), false, sheet.get(), &err))
        return WEVA_ERR_PARSE;

    // Compile from the new set so removed selectors, layers, registrations
    // and keyframes disappear. Keep live styles until the next cascade can
    // compare them, preserving interaction state and existing animation time.
    doc->styles.engine.clear();
    doc->styles.keyframes.clear();
    doc->sheets.clear();
    if (doc->ua_sheet)
        doc->styles.engine.add_stylesheet(doc->ua_sheet.get(), DeclarationOrigin::UserAgent);
    doc->styles.engine.add_stylesheet(sheet.get(), DeclarationOrigin::Author);
    doc->styles.keyframes = doc->styles.engine.keyframes();
    doc->sheets.push_back(std::move(sheet));
    doc->pending = Invalidation::Boxes;
    return WEVA_OK;
}

void weva_document_set_viewport(weva_document_t doc, int width, int height) {
    if (!doc || width <= 0 || height <= 0) return;
    if (width == doc->config.viewport_width && height == doc->config.viewport_height) return;
    doc->config.viewport_width = width;
    doc->config.viewport_height = height;
    doc->ctx.viewport_width_px = width;
    doc->ctx.viewport_height_px = height;
    // Conditional rules are compiled against viewport inputs. Recompile on
    // that input change before the normal style diff propagates invalidation.
    auto media = doc->styles.engine.media_context();
    media.viewport_width_px = width;
    media.viewport_height_px = height;
    doc->styles.engine.set_media_context(media);
    doc->styles.engine.clear();
    if (doc->ua_sheet)
        doc->styles.engine.add_stylesheet(doc->ua_sheet.get(), DeclarationOrigin::UserAgent);
    for (const auto& sheet : doc->sheets)
        doc->styles.engine.add_stylesheet(sheet.get(), DeclarationOrigin::Author);
    doc->styles.keyframes = doc->styles.engine.keyframes();
    // A cached texture is keyed by the box's own size and style, which does not
    // capture a viewport unit INSIDE a gradient -- a `50vw` stop on a
    // fixed-width box would survive a resize it should not. Dropping the cache
    // on a resize is exact and costs one pass.
    doc->textures.release_all(doc->render_backend());
    // Computed styles do not mention the viewport -- percentages and viewport
    // units are resolved at layout -- so the cascade would report no change at
    // all while every size on the page may be different.
    doc->pending = worst(doc->pending, Invalidation::Boxes);
}

void weva_document_set_color_scheme(weva_document_t doc, int dark) {
    if (!doc) return;
    auto media = doc->styles.engine.media_context();
    const ColorScheme scheme = dark ? ColorScheme::Dark : ColorScheme::Light;
    if (media.color_scheme == scheme) return;
    media.color_scheme = scheme;
    doc->styles.engine.set_media_context(media);
    // `@media (prefers-color-scheme)` branches are compiled against the
    // context, so the sheets go through the compiler again, as on a resize.
    doc->styles.engine.clear();
    if (doc->ua_sheet)
        doc->styles.engine.add_stylesheet(doc->ua_sheet.get(), DeclarationOrigin::UserAgent);
    for (const auto& sheet : doc->sheets)
        doc->styles.engine.add_stylesheet(sheet.get(), DeclarationOrigin::Author);
    doc->styles.keyframes = doc->styles.engine.keyframes();
    // A scheme change is a restyle: light-dark() values move without any rule
    // changing, and a cached gradient texture keyed by style text would not
    // see a colour that changed underneath it.
    doc->textures.release_all(doc->render_backend());
    doc->pending = worst(doc->pending, Invalidation::Boxes);
}

weva_status weva_document_content_size(weva_document_t doc, double* out_width,
                                       double* out_height) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    content_size(doc->tree, doc->root, doc->ctx, out_width, out_height);
    return WEVA_OK;
}

int weva_document_is_animating(weva_document_t doc) {
    if (!doc) return 0;
    // A blinking caret is a moving document: a host that stops handing over
    // time because "nothing is animating" freezes the cursor mid-blink.
    const InteractionState& st = doc->styles.state;
    if (st.focused && is_text_field(*st.focused)) return 1;
    if (weva_document_needs_input_tick(doc)) return 1;
    return doc->styles.animating() ? 1 : 0;
}
int weva_document_needs_input_tick(weva_document_t doc) {
    return doc && ((doc->list_drag && doc->list_scroll_armed) || (doc->text_drag && doc->text_scroll_armed)) ? 1 : 0;
}

namespace {

// Defined with the pointer handling below; the update loop drives the dwell,
// which is what makes a tooltip appear over a pointer that has stopped moving.
void tooltip_show(weva_document* doc);
void tooltip_hide(weva_document* doc);

}   // namespace

static weva_status update_document(weva_document_t doc, double dt_seconds,
                                   double input_seconds, bool publish_paint);

weva_status weva_document_update_geometry(weva_document_t doc) {
    return update_document(doc, 0, 0, false);
}

weva_status weva_document_update(weva_document_t doc, double dt_seconds) {
    return weva_document_update_with_input_time(doc,dt_seconds,dt_seconds);
}
weva_status weva_document_update_with_input_time(weva_document_t doc, double dt_seconds, double input_seconds) {
    return update_document(doc, dt_seconds, input_seconds, true);
}

static weva_status update_document(weva_document_t doc, double dt_seconds,
                                   double input_seconds, bool publish_paint) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    if (!doc->doc) return WEVA_ERR_NOT_FOUND;

    // WEVA_STAGE_LOG breaks an update into its four stages. A whole-update
    // number says a change is slow; it does not say which half to look at, and
    // the answer moved once the texture cache landed.
    static const bool stage_log = std::getenv("WEVA_STAGE_LOG") != nullptr;
    const auto now = [] { return std::chrono::steady_clock::now(); };
    auto t0 = now();
    BufferedUpdateSample sample(buffered_update_trace(), t0);
    const auto lap = [&](const char* what) {
        if (!stage_log && !sample.trace) return;
        const auto t = now();
        const double elapsed = std::chrono::duration<double, std::milli>(t - t0).count();
        if (sample.trace) {
            const char* names[] = {"cascade", "animate", "boxes", "layout", "paint"};
            for (size_t i = 0; i < 5; ++i) if (std::strcmp(what, names[i]) == 0) sample.entry.stages[i] += elapsed;
        }
        if (stage_log) {
            std::fprintf(stderr, "  %-12s %7.3f ms\n", what, elapsed);
            if (doc->host_font) doc->host_font->profile_lap(what);
            t0 = now();
        } else t0 = t;
    };

    // Nothing has been touched since the last update, so there is nothing for
    // the cascade to find. A host that drives update() from its frame loop
    // pays this and no more for a screen that is merely being looked at --
    // unless something is moving, in which case time itself is the change.
    // The caret runs on the clock alone. A blink changes nothing an element
    // can see, so it is settled here -- against what was last painted -- and
    // asks for its own repaint before anything decides the pass is a no-op.
    if (dt_seconds > 0) doc->styles.state.caret_age += dt_seconds;
    // The tooltip's wait runs on the clock alone, like the caret's blink: a
    // pointer that has stopped moving sends no more events, and the tooltip
    // still has to appear. Before the settled-document early-out, or a still
    // pointer would never reach the delay.
    if (dt_seconds > 0 && doc->tooltip_delay >= 0 && doc->tooltip.host && !doc->tooltip.shown) {
        doc->tooltip.dwell += dt_seconds;
        if (doc->tooltip.dwell >= doc->tooltip_delay) tooltip_show(doc);
    }
    if (doc->styles.engine.has_default_selectors() && doc->default_inputs_dirty) {
        std::vector<Ref<Element>> next;
        const auto collect = [&](const auto& self, Node& node) -> void {
            if (node.is_element()) {
                auto& e = static_cast<Element&>(node);
                if (form_is_submit_button(e) && form_is_default(e)) next.push_back(Ref<Element>::retain(&e));
            }
            for (const auto& child : node.children()) self(self, *child);
        };
        collect(collect, *doc->doc);
        const auto touch = [&](const Ref<Element>& element) {
            const Node* root = element.get();
            while (root->parent()) root = root->parent();
            if (root != doc->doc.get()) return;
            doc->dom_touched = true;
            if (doc->touched.size() < 64 && std::find(doc->touched.begin(), doc->touched.end(), element.get()) == doc->touched.end())
                doc->touched.push_back(element.get());
        };
        for (const auto& e : doc->default_buttons) if (std::find(next.begin(), next.end(), e) == next.end()) touch(e);
        for (const auto& e : next) if (std::find(doc->default_buttons.begin(), doc->default_buttons.end(), e) == doc->default_buttons.end()) touch(e);
        doc->default_buttons = std::move(next);
        doc->default_inputs_dirty = false;
    }
    if (doc->check_focus_after_dialog_close) {
        doc->check_focus_after_dialog_close = false;
        if (doc->styles.state.focused && focus_unavailable_now(doc, *doc->styles.state.focused))
            weva_document_set_focus(doc, WEVA_ELEMENT_NONE);
    }
    if (doc->dom_touched && doc->styles.state.focused &&
        (form_is_disabled(*doc->styles.state.focused) || form_is_inert(*doc->styles.state.focused)))
        weva_document_set_focus(doc, WEVA_ELEMENT_NONE);
    if (doc->dom_touched &&
        ((doc->press_target && form_is_inert(*doc->press_target)) ||
         (doc->scroll_drag.element && form_is_inert(*doc->scroll_drag.element)) ||
         (!doc->styles.state.hover_chain.empty() && form_is_inert(*doc->styles.state.hover_chain.front())))) {
        // Cancel captured gestures even when the mouse is stationary. Preserve
        // physical button state so the next move cannot synthesize a new press.
        const auto buttons = doc->buttons_last;
        weva_document_clear_pointer(doc);
        doc->buttons_last = buttons;
    }
    if (doc->styles.state.composing && !field_value_equals(*doc->styles.state.focused, doc->composition_value))
        weva_document_commit_composition(doc, nullptr);
    if (doc->dom_touched && doc->open_select &&
        (select_is_listbox(*doc->open_select) || disabled_ancestor(doc->open_select) || form_is_inert(*doc->open_select))) {
        doc->set_open_select(nullptr);
        doc->highlighted_option = -1;
        doc->pending = worst(doc->pending, Invalidation::Paint);
    }
    CaretState caret = caret_for(doc->styles.state);
    if (caret.element != doc->caret_painted.element ||
        caret.index != doc->caret_painted.index ||
        caret.downstream != doc->caret_painted.downstream ||
        caret.visible != doc->caret_painted.visible ||
        caret.selection_from != doc->caret_painted.selection_from ||
        caret.selection_to != doc->caret_painted.selection_to ||
        caret.composition_from != doc->caret_painted.composition_from ||
        caret.composition_to != doc->caret_painted.composition_to) {
        // Caret/selection inputs belong to the old and new text controls.
        // Invalidating the document here discarded every HUD paint cache on
        // each keystroke, focus change and blink.
        doc->styles.repaint(doc->caret_painted.element);
        doc->styles.repaint(caret.element);
    }
    if ((!publish_paint || !doc->paint_pending) &&
        doc->pending == Invalidation::None && !doc->dom_touched && doc->styles.pending_control_inputs.empty() &&
        doc->styles.pending_visual_inputs.empty() &&
        doc->styles.pending_box_inputs.empty() &&
        !(dt_seconds > 0 && doc->styles.animating()) &&
        !(input_seconds > 0 && weva_document_needs_input_tick(doc))) {
        lap("cascade");
        lap("boxes");
        lap("layout");
        lap("paint");
        return WEVA_OK;
    }
    // Captured before the flag is cleared: whether anything other than the
    // clock moved. Time on its own cannot change what the cascade would
    // produce, so an animating document does not restyle to be told so.
    const bool touched_anything = doc->dom_touched;
    const bool structural_pending = doc->pending != Invalidation::None;
    const Invalidation pending_at_entry = doc->pending;
    doc->dom_touched = false;

    // The cascade runs whatever changed -- it is the only thing that can tell
    // what did -- but it now reports its findings rather than being assumed to
    // have changed everything. `pending` comes back as the most invalidating
    // difference it found across the document.
    doc->styles.begin_pass();
    if (stage_log) doc->styles.engine.reset_cache_stats();
    const Element* modal_input = doc->styles.pending_box_inputs.size() == 1 &&
        doc->styles.pending_box_inputs.front()->tag_name() == "dialog" ? doc->styles.pending_box_inputs.front() : nullptr;
    for (const Element* e : doc->styles.pending_box_inputs) {
        const auto found = doc->styles.by_element.find(e);
        if (found == doc->styles.by_element.end()) doc->styles.note_structural();
        else doc->styles.note_change(found->second, Invalidation::Boxes);
    }
    doc->styles.pending_box_inputs.clear();
    for (const Element* e : doc->styles.pending_visual_inputs) {
        const auto found = doc->styles.by_element.find(e);
        if (found != doc->styles.by_element.end()) doc->styles.note_change(found->second, Invalidation::Paint);
    }
    doc->styles.pending_visual_inputs.clear();
    for (const Element* e : doc->styles.pending_content_inputs) {
        const auto found = doc->styles.by_element.find(e);
        if (found == doc->styles.by_element.end()) doc->styles.note_structural();
        else doc->styles.note_change(found->second, Invalidation::Layout);
    }
    doc->styles.pending_content_inputs.clear();
    // Consume visual form inputs even when no selector-visible state changed.
    // Keys remain form/list versions; this work is absent on unchanged frames.
    for (const auto* e : doc->styles.pending_control_inputs) {
        const auto found = doc->styles.by_element.find(e);
        if (found != doc->styles.by_element.end()) doc->styles.consume_control_inputs(*e, found->second);
    }
    doc->styles.pending_control_inputs.clear();

    // Time passing cannot change what the cascade would produce, so a document
    // that is merely animating does not restyle to be told so. Without this an
    // animating page paid a full walk every frame -- 5.2 ms of layout-stress's
    // 16.5, to discover nothing.
    //
    // Nor can a REPAINT. Scrolling, a blinking caret and a tooltip sliding
    // along all ask for Invalidation::Paint, and none of them can change a
    // computed style: what could -- an attribute, a class, hover, focus, the
    // active chain -- goes through note_state_change or the DOM and arrives as
    // `touched` instead. Without this a scroll restyled the whole document,
    // and scrolling a 2,000-row list cost 80 ms a frame in the cascade alone
    // to discover that nothing had changed.
    const bool only_time = !touched_anything &&
                           (!structural_pending || pending_at_entry == Invalidation::Paint);

    // When the only thing that happened is that a host set some attributes,
    // the walk can be confined to what those attributes can reach. Whether
    // they can reach past their own subtree is a property of the SHEETS, and
    // the cascade already knows it: it works the same fact out to decide
    // whether its match cache is sound.
    const bool scoped = doc->pending <= Invalidation::Paint && !doc->touched.empty() &&
                        doc->touched.size() < 64 && !doc->styles.engine.has_has_selectors() &&
                        !doc->styles.engine.has_validity_selectors();
    if (scoped) {
        const bool siblings_matter = doc->styles.engine.has_sibling_selectors();
        // Fold overlapping selector scopes before walking. A parent's style
        // must be applied before its descendants, and a sibling combinator
        // also covers every following sibling's descendants.
        const auto covers = [&](const Element* root, const Element* element) {
            for (const Node* n = element; n; n = n->parent()) {
                if (n == root) return true;
                if (!siblings_matter || !root->parent() || n->parent() != root->parent()) continue;
                bool after = false;
                for (const auto& child : root->parent()->children()) {
                    if (child.get() == root) after = true;
                    if (child.get() == n) return after;
                }
            }
            return false;
        };
        std::vector<Element*> roots;
        for (Element* e : doc->touched) {
            if (std::any_of(roots.begin(), roots.end(), [&](const Element* root) { return covers(root, e); })) continue;
            roots.erase(std::remove_if(roots.begin(), roots.end(), [&](const Element* root) { return covers(e, root); }), roots.end());
            roots.push_back(e);
        }
        for (Element* e : roots) {
            // The subtree, and from it whatever the sheets can carry forward.
            const Node* from = e;
            const Node* parent = e->parent();
            const ComputedStyle* parent_style = nullptr;
            if (parent && parent->node_type() == NodeType::Element) {
                auto it = doc->styles.by_element.find(static_cast<const Element*>(parent));
                if (it != doc->styles.by_element.end()) parent_style = it->second;
            }
            if (!parent) {
                doc->styles.note_structural();   // detached: fall back
                break;
            }
            bool started = false;
            for (const Ref<Node>& c : parent->children()) {
                if (c.get() == from) started = true;
                if (!started) continue;
                if (c->node_type() != NodeType::Element) continue;
                doc->styles.walk(static_cast<const Element&>(*c), parent_style);
                // Without a sibling combinator in the sheets, nothing after the
                // touched element can have changed.
                if (!siblings_matter) break;
            }
        }
    } else if (!only_time) {
        for (const Ref<Node>& c : doc->doc->children()) {
            if (c->node_type() == NodeType::Element) {
                doc->styles.walk(static_cast<const Element&>(*c), nullptr);
            }
        }
        // An element that was styled last pass and was not visited this one is
        // gone from the DOM, which the walk cannot see from the inside.
        if (doc->styles.visited != static_cast<int>(doc->styles.by_element.size())) {
            doc->styles.note_structural();
        }
    }
    doc->touched.clear();
    lap("cascade");
    if (stage_log) {
        const auto& stats = doc->styles.engine.cache_stats();
        doc->styles.engine.report_work_profile();
        ComputedStyle::report_storage_profile();
        std::fprintf(stderr, "  cascade visited %d; matches %lld hits %lld misses %lld uncached; scoped %d\n",
                     doc->styles.visited, static_cast<long long>(stats.hits), static_cast<long long>(stats.misses),
                     static_cast<long long>(stats.skipped), scoped);
        if (doc->styles.visited) {
            size_t set_values = 0;
            for (const auto& style : doc->styles.owned) set_values += style->set_count();
            std::fprintf(stderr, "    style storage: %zu styles, %zu own values, %d registered properties\n",
                         doc->styles.owned.size(), set_values, CssPropertyRegistry::instance().count());
        }
    }

    // Time passes after the cascade has set the targets, so a transition that
    // started this very pass gets its first step in the same frame rather than
    // showing its start value for one. Timed separately: charging it to the
    // cascade made an animating page look as though it were restyling, which
    // is exactly what it is NOT doing.
    doc->styles.advance(dt_seconds);
    if (doc->styles.pending != Invalidation::None && doc->styles.state.focused &&
        focus_disabled(*doc->styles.state.focused, doc)) {
        // The cascade has already resolved visibility, including ancestor rules
        // and visible descendants. Do not re-resolve it on clean HUD frames.
        const Element* previous = doc->styles.state.focused;
        weva_document_set_focus(doc, WEVA_ELEMENT_NONE);
        // Clearing focus flips :focus-within on every ancestor. Restyle that
        // root scope now so siblings and :has() see the blur this frame. Match
        // caches retain their versioned inputs; no global cache is invalidated.
        for (const auto& child : doc->doc->children())
            if (child->is_element()) doc->styles.walk(static_cast<const Element&>(*child), nullptr);
        doc->styles.advance(0);
        doc->styles.repaint(previous);
        doc->touched.clear();
        doc->dom_touched = false;
        caret = caret_for(doc->styles.state);
    }
    lap("animate");
    // Use restored geometry on layout frames and let explicit row/caret
    // reveal finish before the held gesture advances the viewport.
    const bool defer_input_scroll = doc->pending >= Invalidation::Layout ||
        doc->styles.pending >= Invalidation::Layout || doc->list_follow || doc->caret_follow;
    BoxId input_scroll_change = defer_input_scroll ? kNoBox : advance_input_autoscroll(doc,input_seconds);
    if (input_scroll_change != kNoBox) caret = caret_for(doc->styles.state);
    Invalidation pending = doc->styles.pending;
    // Anything a host did that the cascade cannot see -- a new stylesheet, a
    // resized viewport, a backend swap, the first update of all -- is recorded
    // by the entry point that did it.
    pending = worst(pending, doc->pending);
    doc->pending = Invalidation::None;
    if (publish_paint && doc->paint_pending) pending = worst(pending, Invalidation::Paint);

    if (pending == Invalidation::None) {
        // Nothing an element can see is different, so the draws already
        // published are the right ones. The ABI says they stay valid until the
        // next update, and this IS the next update.
        lap("boxes");
        lap("layout");
        lap("paint");
        return WEVA_OK;
    }

    // Inline layout replaces source children with line boxes. Reflow uses a
    // fresh subtree, and splices only when its dimensions, baselines and
    // intrinsic contributions prove that surrounding layout stays valid.
    // Structure, external constraints and unsupported dependencies rebuild.
    const bool modal_layout = pending == Invalidation::Boxes && modal_input &&
        pending_at_entry == Invalidation::None && doc->incremental_layout.update_modal(
            &doc->tree, doc->root, *doc->doc, *modal_input, &doc->styles, doc->ctx,
            &doc->metrics_backend(), doc->styles.changes);
    bool subtree_layout = modal_layout || (pending == Invalidation::Layout &&
        pending_at_entry < Invalidation::Layout &&
        doc->incremental_layout.update(&doc->tree, doc->root, &doc->styles, doc->ctx,
                                      &doc->metrics_backend(), doc->styles.changes));
    if (pending >= Invalidation::Layout && !subtree_layout) {
        doc->tree.reset();
        BoxBuilder builder(&doc->tree, &doc->styles);
        doc->root = builder.build_document(*doc->doc);
        if (doc->root == kNoBox) return WEVA_ERR_INTERNAL;
    }
    lap("boxes");

    if (pending >= Invalidation::Layout && !subtree_layout) {
        BlockLayout block(&doc->tree, doc->ctx, &doc->metrics_backend());
        auto layout_phase = stage_log ? now() : decltype(now()){};
        const auto layout_lap = [&](const char* name) {
            if (!stage_log) return;
            const double ms = std::chrono::duration<double, std::milli>(now() - layout_phase).count();
            std::fprintf(stderr, "    layout part: %-12s %.3f ms\n", name, ms);
            layout_phase = now();
        };
        block.layout_root(doc->root, doc->ctx.viewport_width_px, doc->ctx.viewport_height_px);
        layout_lap("flow");
        run_positioning(&doc->tree, doc->root, doc->ctx, &block);
        layout_lap("positioning");
        // After positioning, because an absolutely positioned child is not
        // where layout first put it and the rect has to cover where it ended.
        compute_visual_overflow(&doc->tree, doc->root);
        layout_lap("overflow");
        doc->incremental_layout.index(doc->tree, doc->root, doc->ctx, pending == Invalidation::Boxes);
        layout_lap("index");
    }
    if (stage_log && subtree_layout)
        std::fprintf(stderr, "  layout reused: %zu subtrees replaced, %zu grids retained\n",
                     doc->incremental_layout.replaced().size(), doc->incremental_layout.retained_grids());
    if (pending >= Invalidation::Layout && !doc->styles.engine.container_queries().empty()) {
        // Size containment makes query dependencies flow from ancestors to
        // descendants. Settle nested containers before publishing this frame;
        // the DOM depth cannot exceed the number of styled elements.
        size_t remaining=doc->styles.by_element.size()+1;
        while (doc->container_queries.refresh(*doc->doc,doc->tree,doc->root,doc->styles,doc->ctx)) {
            if (!remaining--) {
                std::fprintf(stderr,"WEVA: container size queries did not settle\n");
                return WEVA_ERR_INTERNAL;
            }
            const auto previous=doc->styles.pending;
            doc->styles.pending=Invalidation::None;
            for (const Element* element:doc->container_queries.changed_roots()) {
                const auto* parent=element->parent();
                const auto* parent_style=parent && parent->is_element()
                    ? doc->styles.style_of(static_cast<const Element&>(*parent)) : nullptr;
                doc->styles.walk(*element,parent_style);
            }
            const auto query_pending=doc->styles.pending;
            doc->styles.pending=worst(previous,query_pending);
            pending=worst(pending,query_pending);
            if (query_pending < Invalidation::Layout) break;
            doc->tree.reset();
            BoxBuilder builder(&doc->tree,&doc->styles);
            doc->root=builder.build_document(*doc->doc);
            if (doc->root==kNoBox) return WEVA_ERR_INTERNAL;
            BlockLayout block(&doc->tree,doc->ctx,&doc->metrics_backend());
            block.layout_root(doc->root,doc->ctx.viewport_width_px,doc->ctx.viewport_height_px);
            run_positioning(&doc->tree,doc->root,doc->ctx,&block);
            compute_visual_overflow(&doc->tree,doc->root);
            doc->incremental_layout.index(doc->tree,doc->root,doc->ctx,true);
            subtree_layout=false;
        }
    }
    lap("layout");

    // Where the cursor ended up, now that there is a layout, and the scroll
    // that keeps it in view: typing at the bottom of a textarea has to move
    // the view, or the text goes on past what you can see. Before the offsets
    // are applied below, so the scroll this asks for lands in the same frame.
    resolve_caret_run(doc, &caret);
    if (doc->focus_follow) {
        bring_box_into_view(doc, box_of(doc, doc->focus_follow));
        doc->focus_follow = nullptr;
    }
    if (doc->list_follow) {
        bring_box_into_view(doc, box_of(doc, doc->list_follow));
        doc->list_follow = nullptr;
    }
    {
        PaintContext measure = measuring_context(doc);
        measure.caret = caret;
        caret.text_scroll_x = input_text_scroll(doc->tree, box_of(doc, caret.element), doc->ctx, measure,
                                                doc->caret_follow || doc->styles.state.anchor < 0);
        doc->styles.state.text_scroll_x = caret.text_scroll_x;
    }
    if (doc->caret_follow) {
        // The LINE, not the run inside it: a run is only as tall as its
        // glyphs, so scrolling to one leaves the line's leading hanging off
        // the edge and the last line never quite reaches the bottom.
        BoxId show = caret.run;
        if (show != kNoBox && doc->tree[show].parent != kNoBox &&
            doc->tree[doc->tree[show].parent].kind == BoxKind::Line) {
            show = doc->tree[show].parent;
        }
        if (show != kNoBox) bring_box_into_view(doc, show);
        doc->caret_follow = false;
    }

    // The offsets go back onto the boxes paint and hit testing read, clamped
    // to what there is to scroll NOW: a list that shrank under a scrolled view
    // scrolls back up by itself rather than showing the empty space past its
    // end. An element scrolled to zero is forgotten, so the map stays the size
    // of what is actually scrolled rather than of what was ever touched.
    if (!doc->scroll.empty()) {
        for (int i = 0; i < doc->tree.size(); ++i) {
            Box& b = doc->tree[i];
            if (!b.element || box_of(doc, b.element) != i) continue;
            const auto it = doc->scroll.find(b.element);
            if (it == doc->scroll.end()) continue;
            // Only a box that CLIPS can be scrolled: moving the contents of
            // one that does not would slide them out from under it in plain
            // view. A host that scrolls the wrong element gets nothing, which
            // is the honest answer.
            if (!clips_overflow(b)) {
                b.scroll_x = b.scroll_y = 0;
                it->second = {0, 0};
                continue;
            }
            double mx = 0, my = 0;
            max_scroll(doc->tree, i, &mx, &my);
            b.scroll_x = std::clamp(it->second.first, 0.0, mx);
            b.scroll_y = std::clamp(it->second.second, 0.0, my);
            it->second = {b.scroll_x, b.scroll_y};
        }
        for (auto it = doc->scroll.begin(); it != doc->scroll.end();) {
            it = (it->second.first == 0 && it->second.second == 0) ? doc->scroll.erase(it)
                                                                  : std::next(it);
        }
    }

    if (defer_input_scroll) {
        input_scroll_change = advance_input_autoscroll(doc,input_seconds);
        if (input_scroll_change != kNoBox) {
            caret = caret_for(doc->styles.state);
            resolve_caret_run(doc,&caret);
        }
        pending = worst(pending,doc->pending);
        doc->pending = Invalidation::None;
    }

    std::vector<BoxId> paint_changes;
    // Scroll changes the viewport and every descendant's paint coordinates.
    // Feed that input through the existing subtree version propagation.
    if (input_scroll_change != kNoBox) paint_changes.push_back(input_scroll_change);
    for (const auto& change : doc->styles.changes) {
        const auto& boxes = doc->incremental_layout.style_boxes();
        const auto it = boxes.find(change.first);
        if (it != boxes.end() && it->second != kNoBox) {
            BoxId changed = it->second;
            // Inline styles can own fragments on several sibling lines. Their
            // containing block is the smallest retained boundary covering all
            // fragments; invalidating only the first leaves later lines stale.
            if (doc->tree[changed].kind == BoxKind::Inline || doc->tree[changed].kind == BoxKind::Text) {
                while (doc->tree[changed].parent != kNoBox &&
                       doc->tree[changed].kind != BoxKind::Block &&
                       doc->tree[changed].kind != BoxKind::AnonymousBlock)
                    changed = doc->tree[changed].parent;
            }
            paint_changes.push_back(changed);
        }
    }
    const bool reset_paint = (pending >= Invalidation::Layout && !subtree_layout) ||
                            pending_at_entry != Invalidation::None ||
                            (subtree_layout && !doc->scroll.empty());
    if (pending == Invalidation::Paint) {
        for (BoxId id : paint_changes) {
            compute_visual_overflow(&doc->tree, id);
            for (BoxId p = doc->tree[id].parent; p != kNoBox; p = doc->tree[p].parent)
                update_visual_overflow(&doc->tree, p);
        }
    }
    doc->backend.prepare_reuse(doc->tree, &doc->textures, paint_changes, reset_paint,
                               doc->incremental_layout, subtree_layout);
    if (!publish_paint) {
        // Retain every changed cache input across event geometry updates while
        // leaving the published draw buffers and their textures untouched.
        doc->paint_pending = true;
        return WEVA_OK;
    }
    doc->paint_pending = false;
    for (TextureHandle t : doc->transient_textures) doc->render_backend()->release_texture(t);
    doc->transient_textures.clear();
    doc->backend.begin_frame();
    PaintContext paint;
    if (!doc->host_render) paint.reuse = &doc->backend;
    paint.styles = &doc->styles;
    paint.backend = doc->render_backend();
    paint.font = doc->font_backend();
    paint.atlas = &doc->atlas;
    paint.face = doc->face;
    paint.owned_textures = &doc->transient_textures;
    paint.texture_cache = &doc->textures;
    paint.images = &doc->images;
    paint.caret = caret;
    paint.popup.element = doc->open_select;
    paint.popup.highlighted = doc->highlighted_option;
    paint.popup.first_row = doc->select_first_row;
    const auto list_state = doc->list_selections.find(doc->styles.state.focused);
    if (list_state != doc->list_selections.end() && select_is_listbox(*list_state->first) &&
        list_state->second.version == list_state->first->form_version())
        paint.active_option = list_state->second.active;
    doc->caret_painted = caret;
    doc->textures.begin_pass();
    paint_tree(doc->tree, doc->root, doc->ctx, paint);
    if (stage_log) std::fprintf(stderr, "  paint reused: %zu subtrees\n", doc->backend.reused_boxes);
    doc->textures.end_pass(doc->render_backend());
    if (stage_log) {
        std::fprintf(stderr, "  %-12s %d hits %d misses, %zu held\n", "textures",
                     doc->textures.hits(), doc->textures.misses(), doc->textures.size());
    }
    lap("paint");

    // With a host backend registered the host issued its own draws, so there
    // is no collected list to publish and the accessors correctly report none.
    //
    // The POD views point straight at the collected buffers: nothing is copied
    // across the boundary, which is what the documented lifetime buys.
    ++doc->draw_serial;
    doc->draw_views.clear();
    doc->draw_views.reserve(doc->backend.draws.size());
    doc->draw_versions.clear();
    doc->draw_versions.reserve(doc->backend.draws.size());
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
        v.kind = d.kind;
        if (d.kind == WEVA_DRAW_ROUNDED_RECT) {
            v.rounded_rect.x = d.rounded_rect.x;
            v.rounded_rect.y = d.rounded_rect.y;
            v.rounded_rect.width = d.rounded_rect.width;
            v.rounded_rect.height = d.rounded_rect.height;
            for (int r = 0; r < 4; ++r) {
                v.rounded_rect.radii[r][0] = d.rounded_rect.radii[r][0];
                v.rounded_rect.radii[r][1] = d.rounded_rect.radii[r][1];
            }
            v.rounded_rect.r = d.rounded_rect.color.r;
            v.rounded_rect.g = d.rounded_rect.color.g;
            v.rounded_rect.b = d.rounded_rect.color.b;
            v.rounded_rect.a = d.rounded_rect.color.a;
        }
        if (d.kind == WEVA_DRAW_BACKDROP_FILTER) {
            v.backdrop.blur_radius = d.backdrop.blur_radius;
            for (int r = 0; r < 3; ++r) {
                for (int c = 0; c < 3; ++c) v.backdrop.color_matrix[r * 3 + c] = d.backdrop.color.m[r][c];
                v.backdrop.color_offset[r] = d.backdrop.color.add[r];
            }
            v.backdrop.color_alpha = d.backdrop.color.alpha;
        }
        doc->draw_views.push_back(v);
        doc->draw_versions.push_back(d.version);
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
    // Removed rows/pseudos can be referenced by the previous box/paint pass
    // until it is replaced. Retire their styles afterwards, keeping repeated
    // inventory refreshes from accumulating dead styles for the session.
    doc->styles.release_retired();
    return WEVA_OK;
}

uint64_t weva_document_draw_serial(weva_document_t doc) {
    return doc ? doc->draw_serial : 0;
}

uint64_t weva_document_interaction_version(weva_document_t doc) {
    return doc ? static_cast<uint64_t>(doc->styles.state.version_) : 0;
}

const weva_draw* weva_document_draws(weva_document_t doc, size_t* out_count) {
    if (out_count) *out_count = doc ? doc->draw_views.size() : 0;
    return doc && !doc->draw_views.empty() ? doc->draw_views.data() : nullptr;
}

const uint64_t* weva_document_draw_versions(weva_document_t doc, size_t* out_count) {
    if (out_count) *out_count = doc ? doc->draw_versions.size() : 0;
    return doc && !doc->draw_versions.empty() ? doc->draw_versions.data() : nullptr;
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
    for (weva_element_t i : doc->document_order()) {
        // The document's OWN state, not a null one.
        //
        // With a null provider no state pseudo-class could ever match here --
        // `:focus`, `:hover`, `:checked`, `:active` all silently found
        // nothing -- while query_all, three thousand lines away, used the real
        // state and found them. So `.cell:focus` counted one element and
        // resolved to none, and every selector-taking method on the node
        // (query_bounds, query_text, get_row, set_element_style, set_focus)
        // inherited the hole, since all of them resolve through here.
        if (selector_matches(compiled, *doc->elements[i], doc->styles.state)) {
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
    const BoxId i = box_of(doc, e);
    if (i != kNoBox) {
        const Box& b = doc->tree[i];
        // Where it is drawn, not where layout put it: a host placing a
        // tooltip beside an element in a scrolled list wants the position it
        // can see.
        double ax = 0, ay = 0;
        visual_position(doc->tree, i, &ax, &ay);
        double width = b.width, height = b.height;
        if (b.kind == BoxKind::Inline) {
            // Query-only walk: wrapped/split inline elements have several
            // principal fragments. The index deliberately retains just one.
            bool nonempty = false;
            const auto visit = [&](const auto& self, BoxId id) -> void {
                const Box& fragment = doc->tree[id];
                if ((fragment.kind == BoxKind::Inline && fragment.element == e) ||
                    is_promoted_inline_fragment(fragment, e)) {
                    if ((fragment.width != 0 && fragment.height != 0) || fragment.split_inline_owner) {
                        double x, y, fw = fragment.width, fh = fragment.height;
                        if (is_promoted_inline_fragment(fragment, e)) {
                            promoted_inline_rect(doc->tree, id, &x, &y, &fw, &fh);
                            double px, py; visual_position(doc->tree, fragment.parent, &px, &py);
                            x += px - doc->tree[fragment.parent].scroll_x;
                            y += py - doc->tree[fragment.parent].scroll_y;
                        } else visual_position(doc->tree, id, &x, &y);
                        if (fw == 0 || fh == 0) {
                            // Empty client rectangles do not enlarge a union.
                        } else if (!nonempty) {
                            ax = x; ay = y; width = fw; height = fh;
                            nonempty = true;
                        } else {
                            const double right = std::max(ax + width, x + fw);
                            const double bottom = std::max(ay + height, y + fh);
                            ax = std::min(ax, x); ay = std::min(ay, y);
                            width = right - ax; height = bottom - ay;
                        }
                    }
                }
                for (BoxId child : doc->tree.children(id)) self(self, child);
            };
            BoxId containing_block = i;
            while (doc->tree[containing_block].kind != BoxKind::Block &&
                   doc->tree[containing_block].parent != kNoBox)
                containing_block = doc->tree[containing_block].parent;
            visit(visit, containing_block);
        }
        if (out_x) *out_x = ax;
        if (out_y) *out_y = ay;
        if (out_width) *out_width = width;
        if (out_height) *out_height = height;
        return WEVA_OK;
    }
    // The element exists but generated no box — `display: none`, or the
    // document has not been updated yet.
    return WEVA_ERR_NOT_FOUND;
}

namespace {

// Marks a state change for the next update.
//
// The cascade's match cache already folds every element's state bits AND its
// ancestors' into the shape key, so a hover change lands on a different entry
// by itself and nothing has to be invalidated. What the UPDATE needs is the
// list of elements whose style may have moved, which is the union of the chain
// that was in the state and the chain that is -- and that is exactly the list
// the scoped restyle takes.
// `reach` is what the pseudo carried by these chains can match; null means the
// change is observable on every element regardless (focus, which moves the
// caret and the scroll position whether or not a rule ever says `:focus`).
void note_state_change(weva_document* doc, const std::vector<const Element*>& before,
                       const std::vector<const Element*>& after, const StateReach* reach) {
    if (before == after) return;
    // No rule can match this element differently, so no computed value can
    // move, and marking it would be pure waste: the cascade re-walks the
    // SUBTREE of anything marked, so a pointer crossing a panel re-cascaded
    // most of the page to discover nothing had changed. `stats.html`, whose
    // sheet contains no `:hover` at all, was paying 0.5 to 8 ms a frame for it.
    //
    // Per element rather than per document, because the user-agent sheet has
    // one `:hover` rule -- `.ui-menu-item:hover` -- and a document-wide test
    // would therefore always say yes. A page WITH hover rules still gets the
    // win for every element those rules cannot reach.
    //
    // The chains themselves are still updated by the caller: hit testing, the
    // cursor and the tooltip all read them. This only decides whether the
    // CASCADE is told.
    const auto observable = [&](const Element* e) {
        return !reach || doc->styles.engine.state_observable(*reach, *e);
    };
    // Only the elements whose state actually FLIPPED.
    //
    // A hover chain is the element and every ancestor up to <body>, because
    // `:hover` applies to all of them. Marking both chains whole therefore
    // marked <body> on every pointer move, and the cascade's touched-subtree
    // walk starting at <body> is a walk of the entire document -- so moving
    // the mouse one pixel within a panel restyled the page. On layout-stress
    // that was 66 ms a frame.
    //
    // An element in BOTH chains kept its state and needs nothing. What is
    // left is the two tails: the elements that stopped being hovered and the
    // ones that started. A rule reaching from one of those into its subtree
    // still works, because the walk covers the subtree of whatever flipped.
    const auto in = [](const std::vector<const Element*>& chain, const Element* e) {
        return std::find(chain.begin(), chain.end(), e) != chain.end();
    };
    bool any = false;
    const auto note_changed = [&](const std::vector<const Element*>& chain,
                                  const std::vector<const Element*>& other) {
        for (const Element* e : chain) {
            if (in(other, e)) continue;
            if (!observable(e)) continue;
            if (doc->touched.size() >= 64) return;
            doc->touched.push_back(const_cast<Element*>(e));
            any = true;
        }
    };
    note_changed(before, after);
    note_changed(after, before);
    // Bumped only when something was actually marked. The version is what the
    // shape cache keys on, so bumping it for a flip nothing can observe throws
    // away every cached match set for no reason -- which is most of the cost
    // this is here to avoid.
    if (!any) return;
    ++doc->styles.state.version_;
    doc->dom_touched = true;
}

}   // namespace

weva_element_t weva_document_element_at(weva_document_t doc, double x, double y) {
    if (!doc) return WEVA_ELEMENT_NONE;
    const Element* hit = input_element_at(doc, x, y);
    if (!hit) return WEVA_ELEMENT_NONE;
    for (size_t i = 0; i < doc->elements.size(); ++i) {
        if (doc->elements[i] == hit) return static_cast<weva_element_t>(i);
    }
    return WEVA_ELEMENT_NONE;
}

namespace {

// Remember what a field holds before an edit changes it. `typing` marks the
// edits that coalesce: a run of plain characters is one undo step, and
// anything else -- a delete, a paste, a move -- ends the run.
void push_undo(weva_document* doc, const Element& e, const std::string& before, int caret,
               int anchor, bool typing) {
    weva_document::EditHistory& h = doc->history[&e];
    if (!(typing && h.last_was_typing && !h.undo.empty())) {
        h.undo.push_back({before, caret, anchor});
        if (h.undo.size() > weva_document::kUndoDepth) h.undo.erase(h.undo.begin());
    }
    h.last_was_typing = typing;
    // A new edit is a new future: whatever was undone is no longer reachable.
    h.redo.clear();
}

void set_field_value(weva_document*, Element& e, std::string_view value) {
    // Editor snapshots/preedit may contain incomplete number input. The
    // public programmatic setter applies the value sanitization algorithm.
    e.set_form_value(value, false, true);
}

void composition_event(weva_document* doc, int kind, std::string_view text) {
    weva_event event{};
    event.kind = kind;
    event.target = doc->handle_of(doc->styles.state.focused);
    weva_document::fill_handler(&event, doc->styles.state.focused);
    const size_t count = text_boundary(text, static_cast<int>(std::min(text.size(), sizeof(event.text) - 1)));
    std::memcpy(event.text, text.data(), count);
    doc->push_event(weva_document::QueuedEvent(event, text));
}

bool same_radio_group(weva_document* doc, const Element* a, const Element* b) {
    if (!a || !b || a->tag_name() != "input" || b->tag_name() != "input" ||
        input_type_of(*a) != "radio" || input_type_of(*b) != "radio") return false;
    if (a == b) return true;
    const auto name = a->get_attribute("name");
    return !name.empty() && name == b->get_attribute("name") && form_of(a, doc) == form_of(b, doc);
}

void forget_element(weva_document* doc, const Element* e);

// The nearest element with a non-empty `title`, so a tooltip on a panel covers
// what is inside it and the closest one wins.
const Element* title_host_at(const Element* target) {
    for (const Node* n = target; n; n = n->parent()) {
        if (n->node_type() != NodeType::Element) continue;
        const Element& e = static_cast<const Element&>(*n);
        if (!e.get_attribute("title").empty()) return &e;
    }
    return nullptr;
}

// Where a tooltip sits: beside the cursor, not under it, or the pointer would
// be inside the thing it just summoned.
std::string tooltip_style(double x, double y) {
    char buf[160];
    std::snprintf(buf, sizeof(buf),
                  "position:fixed;left:%gpx;top:%gpx;pointer-events:none;z-index:99999", x + 12,
                  y + 18);
    return buf;
}

void tooltip_hide(weva_document* doc) {
    if (!doc->tooltip.shown) return;
    Element* shown = doc->tooltip.shown;
    doc->tooltip.shown = nullptr;
    if (Node* parent = shown->parent()) parent->remove_child(shown);
    forget_element(doc, shown);
    doc->pending = worst(doc->pending, Invalidation::Boxes);
    doc->dom_touched = true;
}

void tooltip_show(weva_document* doc) {
    if (doc->tooltip.shown || !doc->tooltip.host) return;
    const std::string text(doc->tooltip.host->get_attribute("title"));
    if (text.empty()) return;
    // Into the <body> rather than beside <html>: a second root element is not
    // a shape the box builder is asked to handle anywhere else, and
    // `position: fixed` makes the parent irrelevant to where it lands.
    Element* into = nullptr;
    const std::function<void(Element&)> find_body = [&](Element& e) {
        if (into) return;
        if (e.tag_name() == "body") {
            into = &e;
            return;
        }
        for (const Ref<Node>& c : e.children()) {
            if (c->node_type() == NodeType::Element) {
                find_body(static_cast<Element&>(const_cast<Node&>(*c)));
            }
        }
    };
    for (const Ref<Node>& c : doc->doc->children()) {
        if (c->node_type() == NodeType::Element) {
            find_body(static_cast<Element&>(const_cast<Node&>(*c)));
            if (!into) into = &static_cast<Element&>(const_cast<Node&>(*c));
        }
    }
    if (!into) return;

    Ref<Element> tip = make_ref<Element>("div");
    tip->set_attribute("class", "ui-tooltip");
    tip->set_attribute("style", tooltip_style(doc->tooltip.x, doc->tooltip.y));
    // Marked so a host walking the DOM can tell it apart from its own content.
    tip->set_attribute("data-weva-tooltip", "");
    Ref<Node> label(new TextNode(text));
    tip->append_child(label.get());
    into->append_child(tip.get());
    doc->tooltip.shown = tip.get();
    doc->reindex_new_elements();
    doc->pending = worst(doc->pending, Invalidation::Boxes);
    doc->dom_touched = true;
}

// True for the things a <label> can be for.
bool is_labelable(const Element& e) {
    const std::string_view tag = e.tag_name();
    return tag == "input" || tag == "textarea" || tag == "select" || tag == "button" ||
           tag == "meter" || tag == "progress";
}

// The first form control inside a subtree, in document order.
Element* first_control_in(const Node& n) {
    for (const Ref<Node>& c : n.children()) {
        if (c->node_type() != NodeType::Element) continue;
        Element& e = static_cast<Element&>(const_cast<Node&>(*c));
        if (is_labelable(e)) return &e;
        if (Element* nested = first_control_in(e)) return nested;
    }
    return nullptr;
}

// The control a <label> works, if the click was on the label rather than on a
// control itself. `for` names one by id; without it the label owns the first
// control inside it.
//
// Returns null when the click already landed ON a control -- clicking the
// checkbox inside a label is the activation, and forwarding a second one would
// toggle it back off.
Element* label_target_for_click(weva_document* doc, const Element* hit) {
    if (!hit || is_labelable(*hit)) return nullptr;
    const Element* label = nullptr;
    for (const Node* n = hit; n; n = n->parent()) {
        if (n->node_type() != NodeType::Element) continue;
        const Element& e = static_cast<const Element&>(*n);
        if (e.tag_name() == "label") {
            label = &e;
            break;
        }
    }
    if (!label) return nullptr;
    const std::string named(label->get_attribute("for"));
    Element* target = named.empty() ? first_control_in(*label) : element_by_id(doc, named);
    // A disabled control is not activated by its label either.
    if (target && target->has_attribute("disabled")) return nullptr;
    return target;
}

bool set_range_value(weva_document* doc, Element& e, const RangeValue& range, double to) {
    const double value = range.normalize(to);
    if (value == range.value) return false;
    const std::string text = form_number_text(value);
    e.set_form_value(text);
    note_value_change(doc, e, text);
    return true;
}

// What a click does to a control. Returns true when it changed something.
bool activate_control(weva_document* doc, Element& e, double x, double y) {
    const std::string type = input_type_of(e);
    if (e.tag_name() == "input" && (type == "checkbox" || type == "radio")) {
        if (type == "radio") {
            // A radio cannot be turned off by clicking it, only by another in
            // its group being turned on.
            if (e.form_checked()) return false;
            e.set_form_checked(true);
        } else {
            e.set_form_checked(!e.form_checked());
        }
        note_value_change(doc, e, e.form_checked() ? "on" : "");
        return true;
    }
    if (e.tag_name() == "input" && type == "range") {
        // The value comes from where in the track the pointer landed.
        double ex = 0, ey = 0, ew = 0, eh = 0;
        if (weva_element_bounds(doc, doc->handle_of(&e), &ex, &ey, &ew, &eh) != WEVA_OK) {
            return false;
        }
        const auto box = box_of(doc, &e);
        if (box == kNoBox || !point_to_layout(doc->tree, box, doc->ctx, &x, &y)) return false;
        const RangeTrack track(doc->tree[box], ex, ey);
        if (track.length <= 0 || track.cross <= 0) return false;
        const RangeValue range(e);
        const double frac = track.fraction(x, y);
        return set_range_value(doc, e, range, range.min + frac * (range.max - range.min));
    }
    return false;
}

// The native element whose activation behavior follows a click. The click
// itself keeps its original target (for example the span inside a button).
const Element* activation_target(const Element* hit) {
    for (const Node* n = hit; n; n = n->parent()) {
        if (n->node_type() != NodeType::Element) continue;
        const auto& e = static_cast<const Element&>(*n);
        const auto tag = e.tag_name();
        if (tag == "button" || tag == "input" || (tag == "a" && e.has_attribute("href")) ||
            (tag == "summary" && details_for_summary_click(&e))) return &e;
    }
    return hit;
}

void queue_form_submission(weva_document* doc, Element* form, const Element* submitter,
                           double x, double y, uint32_t buttons, bool pointer = false) {
    // A form cannot recursively submit while its submit handler is running.
    if (!form || doc->polled_submit_form.get() == form ||
        (doc->polled_validation_report && doc->polled_validation_report->form.get() == form)) return;
    weva_event event{};
    event.kind = WEVA_EVENT_SUBMIT;
    event.target = doc->handle_of(form);
    event.x = x;
    event.y = y;
    event.buttons = buttons;
    doc->fill_handler(&event, form);
    weva_document::QueuedEvent queued(event);
    queued.submit_form = Ref<Element>::retain(form);
    if (submitter) queued.submitter = Ref<Element>::retain(const_cast<Element*>(submitter));
    if (submitter && submitter->tag_name() == "input" && form_input_type(*submitter) == "image") {
        double local_x = 0, local_y = 0;
        const BoxId id = box_of(doc, submitter);
        if (pointer && id != kNoBox && point_to_layout(doc->tree, id, doc->ctx, &x, &y)) {
            double origin_x = 0, origin_y = 0;
            visual_position(doc->tree, id, &origin_x, &origin_y);
            local_x = std::max(0.0, x - origin_x - doc->tree[id].border_left);
            local_y = std::max(0.0, y - origin_y - doc->tree[id].border_top);
        }
        queued.image_coordinates = std::to_string(static_cast<int64_t>(std::round(local_x))) + "," +
                                   std::to_string(static_cast<int64_t>(std::round(local_y)));
    }
    doc->push_event(std::move(queued));
}

// Pointer and keyboard activation share event routing and default actions.
// Keyboard clicks use zero coordinates and never synthesize pointer events.
void activate_element(weva_document* doc, const Element* hit, double x, double y, uint32_t buttons,
                      uint32_t modifiers = 0, bool pointer = false) {
    if (!hit || disabled_ancestor(hit) || input_blocked(doc, hit)) return;
    doc->queue_event(WEVA_EVENT_CLICK, hit, x, y, buttons, modifiers);
    const Element* target = activation_target(hit);
    if (target && (target->tag_name() == "button" || target->tag_name() == "input") && form_input_type(*target) == "reset") {
        if (Element* form = form_of(target, doc)) weva_document_reset_form(doc, doc->handle_of(form));
    }
    if (target && form_is_submit_button(*target)) {
        if (Element* form = form_of(target, doc))
            queue_form_submission(doc, form, target, x, y, buttons, pointer);
    }
    if (const Element* trigger = popover_trigger_at(hit)) {
        if (Element* popover = element_by_id(doc, std::string(trigger->get_attribute("popovertarget")))) {
            const auto action = trigger->get_attribute("popovertargetaction");
            if (action == "hide" || (action != "show" && popover->is_popover_open())) {
                if (doc->popover_request_events) weva_element_request_hide_popover(doc, doc->handle_of(popover));
                else popover_hide(doc, *popover);
            }
            else if (doc->popover_request_events) request_popover_open(doc, doc->handle_of(popover), trigger);
            else popover_show(doc, *popover, trigger);
        }
    }
    if (target && target->tag_name() == "summary") {
        Element* details = details_for_summary_click(target);
        if (!details) return;
        if (details->has_attribute("open")) details->remove_attribute("open");
        else details->set_attribute("open", "");
        doc->pending = worst(doc->pending, Invalidation::Boxes);
        doc->queue_event(WEVA_EVENT_TOGGLE, details, x, y, buttons);
    }
    if (Element* labelled = label_target_for_click(doc, hit)) {
        if (disabled_ancestor(labelled) || input_blocked(doc, labelled)) return;
        weva_document_set_focus(doc, doc->handle_of(labelled));
        // A label focuses a slider without moving its thumb.
        if (input_type_of(*labelled) != "range") activate_control(doc, *labelled, x, y);
    } else if (target) {
        activate_control(doc, const_cast<Element&>(*target), x, y);
    }
}

bool implicit_submit(weva_document* doc, const Element* field) {
    Element* form = form_of(field, doc);
    if (!form) return false;
    int blocking_fields = 0;
    // Tree order matters: insertion may leave the handle table in a different
    // order. The first submit button remains the default even when disabled.
    const auto visit = [&](const auto& self, const Node& n) -> const Element* {
        for (const auto& child : n.children()) {
            if (child->node_type() != NodeType::Element) continue;
            const auto& e = static_cast<const Element&>(*child);
            if (form_of(&e, doc) == form) {
                if (form_is_submit_button(e)) return &e;
                if (e.tag_name() == "input" && is_text_field(e)) ++blocking_fields;
            }
            if (const Element* found = self(self, e)) return found;
        }
        return nullptr;
    };
    if (const Element* button = visit(visit, *doc->doc)) {
        if (!disabled_ancestor(button)) activate_element(doc, button, 0, 0, 0);
    } else if (is_text_field(*field) && blocking_fields <= 1) {
        queue_form_submission(doc, form, nullptr, 0, 0, 0);
    }
    return true;
}

}   // namespace

namespace {

// The scrollbar under a point, if any: from the innermost box there, up
// through the scroll containers that enclose it. The bar overlays the content,
// so the box AT the point is a row rather than the scroller -- the walk up is
// what finds the thing the bar belongs to.
bool scrollbar_under(const weva_document* doc, double x, double y, Scrollbar* out, BoxId* out_box,
                     bool* out_vertical, bool* out_on_thumb) {
    for (BoxId id = box_at_point(doc->tree, doc->root, x, y, &doc->ctx); id != kNoBox;
         id = doc->tree[id].parent) {
        if (doc->tree[id].element && input_blocked(doc, doc->tree[id].element)) break;
        if (!doc->tree[id].element || !clips_overflow(doc->tree[id])) continue;
        double ox = 0, oy = 0;
        visual_position(doc->tree, id, &ox, &oy);
        double local_x = x, local_y = y;
        if (!point_to_layout(doc->tree, id, doc->ctx, &local_x, &local_y)) continue;
        for (const bool vertical : {true, false}) {
            const Scrollbar bar = scrollbar_of(doc->tree, id, vertical, ox, oy);
            if (!bar.visible || !bar.track.contains(local_x, local_y)) continue;
            *out = bar;
            *out_box = id;
            *out_vertical = vertical;
            *out_on_thumb = bar.thumb.contains(local_x, local_y);
            return true;
        }
    }
    return false;
}

}   // namespace

namespace {
bool focus_order_of(const Element& e, int* order, bool include_negative = false);
}

int weva_document_accepts_pointer(weva_document_t doc, double x, double y) {
    if (!doc) return 0;
    if (doc->open_select && !input_blocked(doc, doc->open_select) && select_row_at(doc, x, y) >= 0) return 1;
    return input_element_at(doc, x, y) != nullptr;
}

uint64_t weva_document_transient_version(weva_document_t doc) {
    if (!doc) return 0;
    if (doc->open_select) return doc->transient_version;
    for (const Element* e : doc->popovers)
        if (popover_is_auto(*e)) return doc->transient_version;
    return 0;
}

int weva_document_dismiss_transients(weva_document_t doc, uint64_t version) {
    if (!doc || !version || version != weva_document_transient_version(doc)) return 0;
    if (doc->open_select) weva_document_open_select(doc, WEVA_ELEMENT_NONE);
    // Queued closes leave the live stack unchanged until the host polls.
    // Traverse it once rather than repeatedly selecting the same top item.
    if (doc->popover_request_events) {
        for (size_t i = doc->popovers.size(); i-- > 0;) {
            const auto* popup = doc->popovers[i];
            if (popover_is_auto(*popup))
                weva_element_request_hide_popover(doc, doc->handle_of(popup));
        }
    } else while (popover_hide_top_auto(doc)) {}
    return 1;
}

void weva_document_set_pointer(weva_document_t doc, double x, double y, uint32_t buttons) {
    weva_document_set_pointer_modifiers(doc, x, y, buttons, 0);
}
void weva_document_set_pointer_modifiers(weva_document_t doc, double x, double y,
                                         uint32_t buttons, uint32_t modifiers) {
    if (!doc) return;
    const uint32_t previous_buttons = doc->buttons_last;
    doc->buttons_last = buttons;
    doc->pointer_x = x;
    doc->pointer_y = y;
    const bool pressed = (buttons & WEVA_BUTTON_PRIMARY) && !(previous_buttons & WEVA_BUTTON_PRIMARY);
    InteractionState& st = doc->styles.state;
    if (buttons & WEVA_BUTTON_PRIMARY) st.vertical_owner = nullptr;
    if (pressed) {
        doc->popover_press_active = false;
        doc->popover_press_target = nullptr;
        weva_document_commit_composition(doc, nullptr);
    }

    if (doc->open_select && input_blocked(doc, doc->open_select)) {
        doc->set_open_select(nullptr);
        doc->highlighted_option = -1;
        doc->pending = worst(doc->pending, Invalidation::Paint);
    }

    // An open list is above the document, so it takes the pointer before the
    // tree does: what is under a dropdown is not what you are pointing at.
    if (doc->open_select) {
        const int row = select_row_at(doc, x, y);
        const auto rows = select_rows(*doc->open_select);
        const auto options = select_options(*doc->open_select);
        const auto* entry = row >= 0 && row < static_cast<int>(rows.size()) ? rows[static_cast<size_t>(row)] : nullptr;
        const int option = option_index(options, entry);
        if (option != doc->highlighted_option && option >= 0 && navigable_option(doc, entry)) {
            doc->highlighted_option = option;
            doc->pending = worst(doc->pending, Invalidation::Paint);
        }
        if (pressed) {
            Element& select = const_cast<Element&>(*doc->open_select);
            if (row >= 0 && (option < 0 || !navigable_option(doc, entry))) return;
            if (option >= 0) choose_option(doc, select, option);
            // A press anywhere -- on a row or off the list -- closes it, which
            // is what makes clicking away cancel.
            doc->set_open_select(nullptr);
            doc->highlighted_option = -1;
            doc->pending = worst(doc->pending, Invalidation::Boxes);
            return;
        }
        if (row >= 0) return;   // over the list: the page beneath is not hovered
    }

    // A thumb being dragged owns the pointer until it is let go: the content
    // slides under it, and whatever the pointer happens to be over takes
    // neither :hover nor a click.
    if (doc->scroll_drag.element) {
        if (buttons == 0 || input_blocked(doc, doc->scroll_drag.element)) {
            doc->scroll_drag = weva_document::ScrollDrag{};
        } else {
            double local_x = x, local_y = y;
            if (!point_to_layout(doc->tree, box_of(doc, doc->scroll_drag.element), doc->ctx, &local_x, &local_y)) {
                doc->scroll_drag = weva_document::ScrollDrag{};
                return;
            }
            const double along = doc->scroll_drag.vertical ? local_y : local_x;
            const double moved = (along - doc->scroll_drag.grab) * doc->scroll_drag.per_pixel;
            auto& at = doc->scroll[doc->scroll_drag.element];
            const double to = std::max(0.0, doc->scroll_drag.from + moved);
            if (doc->scroll_drag.vertical) at.second = to;
            else at.first = to;
            doc->pending = worst(doc->pending, Invalidation::Paint);
            return;
        }
    }

    // Taking hold of one, or clicking the track beside it to page along.
    if (pressed) {
        Scrollbar bar;
        BoxId box = kNoBox;
        bool vertical = false, on_thumb = false;
        if (scrollbar_under(doc, x, y, &bar, &box, &vertical, &on_thumb)) {
            double local_x = x, local_y = y;
            if (!point_to_layout(doc->tree, box, doc->ctx, &local_x, &local_y)) return;
            const Box& b = doc->tree[box];
            const double at = vertical ? b.scroll_y : b.scroll_x;
            if (on_thumb) {
                doc->scroll_drag.element = b.element;
                doc->scroll_drag.vertical = vertical;
                doc->scroll_drag.grab = vertical ? local_y : local_x;
                doc->scroll_drag.from = at;
                doc->scroll_drag.per_pixel = bar.scroll_per_pixel;
            } else {
                // A page in the direction clicked, which is what a track click
                // does everywhere.
                const double page = vertical ? b.height - b.border_top - b.border_bottom
                                             : b.width - b.border_left - b.border_right;
                const double point = vertical ? local_y : local_x;
                const double thumb_start = vertical ? bar.thumb.y : bar.thumb.x;
                const double to = std::max(0.0, at + (point < thumb_start ? -page : page));
                auto& offset = doc->scroll[b.element];
                offset = {vertical ? b.scroll_x : to, vertical ? to : b.scroll_y};
            }
            doc->pending = worst(doc->pending, Invalidation::Paint);
            return;
        }
    }

    const Element* hit = input_element_at(doc, x, y);
    // Disabled controls still receive pointer events and hover/active styling.
    if (pressed) {
        doc->popover_press_active = !doc->light_dismiss_popovers.empty();
        doc->popover_press_target = doc->popover_press_active ? popover_pointer_ancestor(doc, hit) : nullptr;
    }
    // Keep the hit for those and title tooltips, but never latch activation.
    const bool disabled_hit = hit && disabled_ancestor(hit);
    if (doc->press_target && (disabled_ancestor(doc->press_target) || input_blocked(doc, doc->press_target))) {
        doc->press_target = nullptr;
        clear_text_drag(doc);
        finish_list_drag(doc);
    }

    // Enter and leave are reported against the innermost element, which is
    // where the hover chain starts.
    const Element* was = st.hover_chain.empty() ? nullptr : st.hover_chain.front();
    if (was != hit) {
        if (was) doc->queue_event(WEVA_EVENT_POINTER_LEAVE, was, x, y, buttons, modifiers);
        if (hit) doc->queue_event(WEVA_EVENT_POINTER_ENTER, hit, x, y, buttons, modifiers);
    }
    // Only the PRIMARY button presses. Everything here keyed off "any button
    // held", so a right-click toggled checkboxes, submitted forms, opened
    // <details> and worked popovers -- none of which a right-click does in a
    // browser.
    const uint32_t primary = buttons & WEVA_BUTTON_PRIMARY;
    // The secondary button going down is a context-menu request and nothing
    // else. The engine has no menu of its own to show: a menu is markup, and
    // this says where the user asked for one.
    if ((buttons & WEVA_BUTTON_SECONDARY) && !(previous_buttons & WEVA_BUTTON_SECONDARY)) {
        doc->queue_event(WEVA_EVENT_CONTEXT_MENU, hit, x, y, buttons, modifiers);
    }

    const uint32_t was_down = previous_buttons & WEVA_BUTTON_PRIMARY;
    if (primary != 0 && !was_down) {
        clear_text_drag(doc);
        doc->press_target = disabled_hit ? nullptr : hit;
        doc->queue_event(WEVA_EVENT_POINTER_DOWN, hit, x, y, buttons, modifiers);
        // A range follows the pointer from the moment it goes down, and a
        // click on a field takes focus -- both are what makes a control feel
        // like one rather than like a picture of one.
        if (hit && !disabled_hit) {
            // Click focus includes negative tabindex and the nearest focusable
            // ancestor (for example the button around a span). It is distinct
            // from sequential focus, which deliberately skips tabindex=-1.
            const Element* focus = nullptr;
            for (const Node* n = hit; n; n = n->parent()) {
                if (n->node_type() != NodeType::Element) continue;
                const auto& candidate = static_cast<const Element&>(*n);
                int order = 0;
                if (focus_order_of(candidate, &order, true)) { focus = &candidate; break; }
            }
            weva_document_set_focus(doc, doc->handle_of(focus));
            Element& e = const_cast<Element&>(*hit);
            // Selectedness updates during the gesture; input/change wait for
            // release, so dragging over several rows commits once.
            if (Element* list = list_box_of(&e)) {
                weva_document_set_focus(doc, doc->handle_of(list));
                list_pointer_press(doc, *list, e, modifiers);
            }
            if (e.tag_name() == "select" && !select_is_listbox(e) && !e.has_attribute("disabled")) {
                weva_document_set_focus(doc, doc->handle_of(hit));
                doc->set_open_select(&e);
                doc->highlighted_option = chosen_index(e);
                doc->select_first_row = 0;
                reveal_highlighted_option(doc);
                doc->pending = worst(doc->pending, Invalidation::Paint);
                return;
            }
            if (input_type_of(e) == "range") activate_control(doc, e, x, y);
            if (is_text_field(e)) {
                weva_document_set_focus(doc, doc->handle_of(hit));
                // The cursor goes where you pressed, not to the end of the
                // value: a field you can only ever click into at the end is
                // one you cannot edit the middle of.
                InteractionState& state = doc->styles.state;
                state.caret = static_cast<int>(field_offset_at(doc, e, x, y));
                doc->text_drag = &e;
                state.anchor = -1;
                state.caret_age = 0;
                doc->pending = worst(doc->pending, Invalidation::Paint);
            }
        }
    } else if (primary != 0 && was_down && list_box_of(doc->press_target)) {
        list_pointer_drag(doc, hit);
    } else if (primary != 0 && was_down && doc->press_target &&
               is_text_field(*doc->press_target)) {
        // Dragging from a press inside a field selects, and keeps selecting
        // once the pointer has left the field -- as it does everywhere.
        InteractionState& state = doc->styles.state;
        Rect viewport;
        if (!text_drag_viewport(doc,&viewport)) { clear_text_drag(doc); return; }
        double local_x = x, local_y = y;
        if (point_to_layout(doc->tree, box_of(doc, doc->press_target), doc->ctx, &local_x, &local_y) &&
            viewport.contains(local_x,local_y)) doc->text_scroll_armed = true;
        const int to = static_cast<int>(field_offset_at(doc, *doc->press_target, x, y));
        if (to != state.caret) {
            if (state.anchor < 0) state.anchor = state.caret;
            state.caret = to;
            state.caret_age = 0;
            doc->caret_follow = doc->text_scroll_armed;
            doc->pending = worst(doc->pending, Invalidation::Paint);
        }
    } else if (primary != 0 && was_down && doc->press_target &&
               input_type_of(const_cast<Element&>(*doc->press_target)) == "range") {
        // Held and moving: the range keeps following, even once the pointer
        // has left it, which is how a slider behaves everywhere.
        activate_control(doc, const_cast<Element&>(*doc->press_target), x, y);
    } else if (primary == 0 && was_down) {
        clear_text_drag(doc);
        doc->queue_event(WEVA_EVENT_POINTER_UP, hit, x, y, buttons, modifiers);
        finish_list_drag(doc);
        // A click is a press and a release on the SAME element. Releasing
        // somewhere else is a drag that ended, and is not a click -- which is
        // the behaviour every button in every toolkit has.
        popover_pointer_release(doc, hit);
        if (hit && hit == doc->press_target) activate_element(doc, hit, x, y, buttons, modifiers, true);
        doc->press_target = nullptr;
    }

    // The tooltip follows the pointer and resets whenever the element under
    // it changes, so moving from one titled thing to another restarts the
    // wait rather than showing the old text at the new place.
    {
        weva_document::Tooltip& tip = doc->tooltip;
        tip.x = x;
        tip.y = y;
        const Element* host = title_host_at(hit);
        if (host != tip.host) {
            tip.host = host;
            tip.dwell = 0;
            tooltip_hide(doc);
        } else if (tip.shown) {
            tip.shown->set_attribute("style", tooltip_style(x, y));
            doc->pending = worst(doc->pending, Invalidation::Layout);
        }
        // Pressing anything dismisses it: a tooltip over the button you are
        // clicking is in the way of what you came to do.
        if (buttons != 0) {
            tip.dwell = 0;
            tooltip_hide(doc);
        }
    }

    std::vector<const Element*> hover;
    InteractionState::chain_of(hit, &hover);
    // A press latches onto what was under the pointer; dragging off an element
    // keeps it pressed, which is what a button does.
    std::vector<const Element*> active;
    // :active follows the primary button only, as it does in a browser.
    if (primary != 0) {
        active = st.active_chain.empty() ? hover : st.active_chain;
    }

    const std::vector<const Element*> old_hover = st.hover_chain;
    const std::vector<const Element*> old_active = st.active_chain;
    st.hover_chain = hover;
    st.active_chain = active;
    note_state_change(doc, old_hover, hover, &doc->styles.engine.hover_reach());
    note_state_change(doc, old_active, active, &doc->styles.engine.active_reach());
}

void weva_document_clear_pointer(weva_document_t doc) {
    if (!doc) return;
    clear_text_drag(doc);
    finish_list_drag(doc);
    doc->press_target = nullptr;
    doc->buttons_last = 0;
    doc->popover_press_target = nullptr;
    doc->popover_press_active = false;
    doc->scroll_drag = weva_document::ScrollDrag{};
    InteractionState& st = doc->styles.state;
    const std::vector<const Element*> old_hover = st.hover_chain;
    const std::vector<const Element*> old_active = st.active_chain;
    st.hover_chain.clear();
    st.active_chain.clear();
    note_state_change(doc, old_hover, st.hover_chain, &doc->styles.engine.hover_reach());
    note_state_change(doc, old_active, st.active_chain, &doc->styles.engine.active_reach());
}

namespace {

// Whether an element can take focus, and where it sits in tab order.
//
// HTML's rule, which is not the one most people expect: a POSITIVE tabindex
// comes first, in numeric order, and everything else follows in document
// order. `tabindex="-1"` is focusable by script but skipped by Tab.
bool focus_order_of(const Element& e, int* order, bool include_negative) {
    const std::string_view raw = e.get_attribute("tabindex");
    if (!raw.empty()) {
        const std::string text(raw);
        char* end = nullptr;
        const long v = std::strtol(text.c_str(), &end, 10);
        if (end != text.c_str() && *end == '\0') {
            if (v < 0 && !include_negative) return false;
            *order = static_cast<int>(v);
            return true;
        }
    }
    // Focusable by nature. An <a> only counts with an href, as in HTML.
    const std::string_view tag = e.tag_name();
    if (tag == "dialog" && e.has_attribute("open") && include_negative) {
        *order = -1;
        return true;
    }
    if (tag == "button" || tag == "input" || tag == "select" || tag == "textarea") {
        *order = 0;
        return true;
    }
    if ((tag == "a" && e.has_attribute("href")) ||
        (tag == "summary" && details_for_summary_click(&e))) {
        *order = 0;
        return true;
    }
    return false;
}

bool text_contents_hidden(const weva_document* doc, const Element& e) {
    const auto style = doc->styles.by_element.find(&e);
    return style != doc->styles.by_element.end() && style->second->get("content-visibility") == "hidden";
}

bool focus_disabled(const Element& e, const weva_document* doc) {
    const StyleMap& styles = doc->styles;
    if (input_blocked(doc, &e)) return true;
    if (disabled_ancestor(&e) || (e.tag_name() == "input" && input_type_of(e) == "hidden")) return true;
    auto it = styles.by_element.find(&e);
    if (it == styles.by_element.end()) return true;   // no box, no focus
    const auto visibility = it->second->get("visibility");
    if (visibility == "hidden" || visibility == "collapse") return true;
    for (const Node* n = &e; n; n = n->parent()) {
        if (n->node_type() != NodeType::Element) continue;
        const auto ancestor = styles.by_element.find(static_cast<const Element*>(n));
        if (ancestor != styles.by_element.end() &&
            (ancestor->second->get("display") == "none" ||
             (n != &e && ancestor->second->get("content-visibility") == "hidden"))) return true;
    }
    return false;
}

// Explicit focus can follow a mutation before the next layout pass. Resolve
// only the target's ancestor chain, without changing retained style objects.
bool focus_unavailable_now(weva_document* doc, const Element& target) {
    if (input_blocked(doc, &target) || disabled_ancestor(&target) ||
        (target.tag_name() == "input" && input_type_of(target) == "hidden")) return true;
    if (!doc->dom_touched && doc->pending < Invalidation::Layout &&
        doc->styles.pending < Invalidation::Layout && doc->styles.pending_box_inputs.empty())
        return focus_disabled(target, doc);
    std::vector<const Element*> chain;
    const Node* root = &target;
    for (const Node* node = &target; node; node = node->parent()) {
        root = node;
        if (node->is_element()) chain.push_back(static_cast<const Element*>(node));
    }
    if (root != doc->doc.get()) return true;
    std::vector<ComputedStyle> resolved(chain.size());
    const ComputedStyle* parent = nullptr;
    for (size_t i = 0; i < chain.size(); ++i) {
        doc->styles.engine.compute(*chain[chain.size()-1-i], doc->styles.state, parent, &resolved[i]);
        if (resolved[i].get("display") == "none") return true;
        if (i + 1 < chain.size() && resolved[i].get("content-visibility") == "hidden") return true;
        parent = &resolved[i];
    }
    return parent && (parent->get("visibility") == "hidden" || parent->get("visibility") == "collapse");
}

void collect_focusables(const Node& n, const weva_document* doc,
                        std::vector<std::pair<int, const Element*>>* out, bool include_negative = false) {
    for (const Ref<Node>& c : n.children()) {
        if (c->node_type() != NodeType::Element) continue;
        const auto& e = static_cast<const Element&>(*c);
        int order = 0;
        if (focus_order_of(e, &order, include_negative) && !focus_disabled(e, doc)) {
            out->emplace_back(order, &e);
        }
        collect_focusables(e, doc, out, include_negative);
    }
}

}   // namespace

weva_element_t weva_document_focus_next(weva_document_t doc, int backwards) {
    return weva_document_focus_step(doc, backwards, 1);
}

weva_element_t weva_document_focus_step(weva_document_t doc, int backwards, int wrap) {
    if (!doc || !doc->doc) return WEVA_ELEMENT_NONE;
    std::vector<std::pair<int, const Element*>> found;
    collect_focusables(*doc->doc, doc, &found);
    if (found.empty()) {
        if (!wrap) weva_document_set_focus(doc, WEVA_ELEMENT_NONE);
        return WEVA_ELEMENT_NONE;
    }
    // Positive tabindex first in numeric order, then the rest in document
    // order. stable_sort keeps document order within each group, which is what
    // makes the second half work at all.
    std::stable_sort(found.begin(), found.end(),
                     [](const auto& a, const auto& b) {
                         const bool pa = a.first > 0, pb = b.first > 0;
                         if (pa != pb) return pa;
                         if (pa && a.first != b.first) return a.first < b.first;
                         return false;
                     });
    // A radio group occupies one Tab stop: the checked enabled member, then
    // the last visited member, then the group's first/last for entry direction.
    for (size_t i = found.size(); i-- > 0;) {
        const Element* candidate = found[i].second;
        if (candidate->tag_name() != "input" || input_type_of(*candidate) != "radio") continue;
        const Element* picked = nullptr;
        for (const auto& entry : found)
            if (same_radio_group(doc, candidate, entry.second) && entry.second->form_checked()) picked = entry.second;
        if (!picked) {
            for (const Element* remembered : doc->radio_focus_memory)
                for (const auto& entry : found)
                    if (entry.second == remembered && same_radio_group(doc, candidate, remembered)) picked = remembered;
        }
        if (!picked) {
            for (const auto& entry : found) {
                if (!same_radio_group(doc, candidate, entry.second)) continue;
                picked = entry.second;
                if (!backwards) break;
            }
        }
        if (picked != candidate) found.erase(found.begin() + static_cast<std::ptrdiff_t>(i));
    }
    const Element* current = doc->styles.state.focused;
    size_t index = 0;
    bool have = false;
    for (size_t i = 0; i < found.size(); ++i) {
        if (found[i].second == current || same_radio_group(doc, found[i].second, current)) { index = i; have = true; break; }
    }
    size_t next = 0;
    if (have && !wrap && (backwards ? index == 0 : index + 1 == found.size())) {
        weva_document_set_focus(doc, WEVA_ELEMENT_NONE);
        return WEVA_ELEMENT_NONE;
    }
    if (!have) {
        next = backwards ? found.size() - 1 : 0;
    } else if (backwards) {
        next = index == 0 ? found.size() - 1 : index - 1;
    } else {
        next = index + 1 >= found.size() ? 0 : index + 1;
    }
    const weva_element_t handle = doc->handle_of(found[next].second);
    weva_document_set_focus(doc, handle);
    const Element* target = found[next].second;
    if (doc->styles.state.focused == target && target->tag_name() == "input" && is_text_field(*target)) {
        // Sequential keyboard focus selects input text, unlike programmatic
        // focus or textarea navigation, which preserve the field's selection.
        auto& state = doc->styles.state;
        state.caret = static_cast<int>(target->form_edit_value().size());
        state.anchor = state.caret ? 0 : -1;
        state.caret_age = 0;
        doc->caret_follow = true;
        doc->styles.repaint(target);
    }
    return handle;
}

weva_element_t weva_document_focus_move(weva_document_t doc, double dx, double dy) {
    if (!doc || !doc->doc) return WEVA_ELEMENT_NONE;
    if (dx == 0 && dy == 0) return doc->handle_of(doc->styles.state.focused);
    std::vector<std::pair<int, const Element*>> found;
    collect_focusables(*doc->doc, doc, &found);
    if (found.empty()) return WEVA_ELEMENT_NONE;

    const Element* current = doc->styles.state.focused;
    if (!current) {
        // Nothing focused: the first press picks something up rather than
        // doing nothing, which is what a player expects opening a screen.
        const weva_element_t first = doc->handle_of(found.front().second);
        weva_document_set_focus(doc, first);
        return first;
    }

    const auto rect_of = [&](const Element* e, double* x, double* y, double* w, double* h) {
        return weva_element_bounds(doc, doc->handle_of(e), x, y, w, h) == WEVA_OK;
    };
    double cx = 0, cy = 0, cw = 0, ch = 0;
    if (!rect_of(current, &cx, &cy, &cw, &ch)) return doc->handle_of(current);
    const double from_x = cx + cw * 0.5, from_y = cy + ch * 0.5;

    const bool horizontal = dx != 0;
    const double sign = horizontal ? (dx > 0 ? 1.0 : -1.0) : (dy > 0 ? 1.0 : -1.0);

    const Element* best = nullptr;
    double best_score = 0;
    for (const auto& entry : found) {
        const Element* e = entry.second;
        if (e == current) continue;
        double ex = 0, ey = 0, ew = 0, eh = 0;
        if (!rect_of(e, &ex, &ey, &ew, &eh)) continue;
        if (ew <= 0 || eh <= 0) continue;

        // The candidate must lie past this one's EDGE on the travelled axis,
        // not merely past its centre: two controls side by side in a row have
        // centres a few pixels apart vertically, and a centre test would let
        // "up" land on a neighbour that is really beside you.
        const double near_edge = horizontal ? (sign > 0 ? ex : -(ex + ew))
                                            : (sign > 0 ? ey : -(ey + eh));
        const double own_edge = horizontal ? (sign > 0 ? cx + cw : -cx)
                                           : (sign > 0 ? cy + ch : -cy);
        if (near_edge < own_edge - 0.5) continue;

        const double to_x = ex + ew * 0.5, to_y = ey + eh * 0.5;
        const double along = horizontal ? std::abs(to_x - from_x) : std::abs(to_y - from_y);
        // How far off the travelled line it sits, measured to the candidate's
        // nearest edge so a wide element counts as aligned anywhere along it.
        double off = 0;
        if (horizontal) {
            if (from_y < ey) off = ey - from_y;
            else if (from_y > ey + eh) off = from_y - (ey + eh);
        } else {
            if (from_x < ex) off = ex - from_x;
            else if (from_x > ex + ew) off = from_x - (ex + ew);
        }

        // Distance along the direction, plus a heavy penalty for drifting off
        // it. The weight is what makes a grid feel right: without it, a
        // slightly nearer element one column over beats the one directly
        // ahead, and the cursor walks diagonally.
        const double score = along + off * 4.0;
        if (!best || score < best_score) {
            best = e;
            best_score = score;
        }
    }

    // Nothing that way: stay put. A menu that wraps from its last row to its
    // first under a held stick is worse than one that stops.
    if (!best) return doc->handle_of(current);
    const weva_element_t handle = doc->handle_of(best);
    weva_document_set_focus(doc, handle);
    return handle;
}

int weva_document_key(weva_document_t doc, int key, uint32_t modifiers, int down) {
    if (!doc) return 0;
    if (down && key != WEVA_KEY_UP && key != WEVA_KEY_DOWN) doc->styles.state.vertical_owner = nullptr;
    if (doc->styles.state.composing && key != WEVA_KEY_OTHER) {
        if (down && key == WEVA_KEY_ESCAPE) weva_document_commit_composition(doc, "");
        else if (down && key == WEVA_KEY_ENTER) weva_document_commit_composition(doc, nullptr);
        else if (down && key == WEVA_KEY_TAB) weva_document_commit_composition(doc, nullptr);
        else return 1;
        if (key != WEVA_KEY_TAB) return 1;
    }
    weva_event e{};
    e.kind = down ? WEVA_EVENT_KEY_DOWN : WEVA_EVENT_KEY_UP;
    e.target = doc->handle_of(doc->styles.state.focused);
    e.key = key;
    e.modifiers = modifiers;
    weva_document::fill_handler(&e, doc->styles.state.focused);
    doc->push_event(e);

    // Close requests are independent of whether the focused control still has
    // usable layout. Composition handled Escape above; popovers and selects
    // receive it before their containing dialog.
    if (down && key == WEVA_KEY_ESCAPE) {
        if (popover_hide_top_auto(doc)) return 1;
        if (doc->open_select) {
            doc->set_open_select(nullptr);
            doc->highlighted_option = -1;
            doc->pending = worst(doc->pending, Invalidation::Paint);
            return 1;
        }
    }
    // The latest shown dialog owns the close request, including a non-modal
    // dialog whose disabled close policy shields an underlying modal.
    if (down && key == WEVA_KEY_ESCAPE) {
        for (auto entry = doc->dialog_close_order.rbegin(); entry != doc->dialog_close_order.rend(); ++entry) {
            const Element* dialog = *entry;
            if (!dialog->has_attribute("open")) continue;
            const auto closedby = dialog->get_attribute("closedby");
            const auto keyword = [&](std::string_view name) {
                if (closedby.size() != name.size()) return false;
                for (size_t i = 0; i < name.size(); ++i) {
                    char c = closedby[i];
                    if (c >= 'A' && c <= 'Z') c += 'a' - 'A';
                    if (c != name[i]) return false;
                }
                return true;
            };
            const bool enabled = keyword("any") || keyword("closerequest") ||
                (!keyword("none") && dialog->is_modal());
            return enabled && weva_element_request_close_dialog_with_value(doc, doc->handle_of(dialog), "") == WEVA_OK ? 1 : 0;
        }
    }

    const uint32_t key_bit = key == WEVA_KEY_SPACE ? 1u : key == WEVA_KEY_ENTER ? 2u : 0u;
    const bool repeat = (doc->activation_keys_down & key_bit) != 0;
    if (down) doc->activation_keys_down |= key_bit;
    else doc->activation_keys_down &= ~key_bit;
    const Element* focused_control = doc->styles.state.focused;
    if (focused_control && !focus_disabled(*focused_control, doc)) {
        const auto tag = focused_control->tag_name();
        const auto type = input_type_of(*focused_control);
        const bool button = tag == "button" || (tag == "input" &&
            (type == "button" || type == "submit" || type == "reset" || type == "image"));
        const bool summary = tag == "summary" && details_for_summary_click(focused_control);
        const bool mark = tag == "input" && (type == "checkbox" || type == "radio");
        const bool link = tag == "a" && focused_control->has_attribute("href");
        if (tag == "input" && type == "number" && (key == WEVA_KEY_UP || key == WEVA_KEY_DOWN) &&
            !text_contents_hidden(doc, *focused_control)) {
            if (down) {
                std::string next;
                if (form_number_step(*focused_control, key == WEVA_KEY_UP, next)) {
                    auto& field = const_cast<Element&>(*focused_control);
                    auto& state = doc->styles.state;
                    push_undo(doc, field, field_value(field), state.caret, state.anchor, false);
                    set_field_value(doc, field, next);
                    state.caret = static_cast<int>(next.size());
                    state.anchor = -1;
                    note_value_change(doc, field, next);
                    note_value_change(doc, field, next, false);
                    doc->value_at_focus = field_value(field);
                    doc->user_edited_since_focus = false;
                }
            }
            return 1;
        }
        if (tag == "input" && type == "range") {
            const RangeValue range(*focused_control);
            const double step = range.any ? (range.max - range.min) / 100 : range.step;
            const double page = std::max(step, (range.max - range.min) / 10);
            const RangeOrientation orientation(doc->styles.style_of(*focused_control));
            const double right = !orientation.vertical && orientation.reversed ? -step : step;
            const double up = orientation.vertical && !orientation.reversed ? -step : step;
            double to = range.value;
            switch (key) {
                case WEVA_KEY_RIGHT: to += right; break;
                case WEVA_KEY_UP: to += up; break;
                case WEVA_KEY_LEFT: to -= right; break;
                case WEVA_KEY_DOWN: to -= up; break;
                case WEVA_KEY_PAGE_UP: to += page; break;
                case WEVA_KEY_PAGE_DOWN: to -= page; break;
                case WEVA_KEY_HOME: to = range.min; break;
                case WEVA_KEY_END: to = range.max; break;
                default: break;
            }
            if (key == WEVA_KEY_LEFT || key == WEVA_KEY_RIGHT || key == WEVA_KEY_UP ||
                key == WEVA_KEY_DOWN || key == WEVA_KEY_PAGE_UP || key == WEVA_KEY_PAGE_DOWN ||
                key == WEVA_KEY_HOME || key == WEVA_KEY_END) {
                if (down) set_range_value(doc, const_cast<Element&>(*focused_control), range, to);
                return 1;
            }
        }
        if (down && type == "radio" && tag == "input" &&
            !(modifiers & (WEVA_MOD_CTRL | WEVA_MOD_ALT | WEVA_MOD_META)) &&
            (key == WEVA_KEY_LEFT || key == WEVA_KEY_RIGHT || key == WEVA_KEY_UP || key == WEVA_KEY_DOWN)) {
            std::vector<std::pair<int, const Element*>> all;
            collect_focusables(*doc->doc, doc, &all, true);
            std::vector<const Element*> group;
            size_t at = 0;
            for (const auto& entry : all) {
                if (!same_radio_group(doc, focused_control, entry.second)) continue;
                if (entry.second == focused_control) at = group.size();
                group.push_back(entry.second);
            }
            if (!group.empty()) {
                const bool previous = key == WEVA_KEY_LEFT || key == WEVA_KEY_UP;
                const Element* next = group[(at + (previous ? group.size() - 1 : 1)) % group.size()];
                weva_document_set_focus(doc, doc->handle_of(next));
                activate_element(doc, next, 0, 0, 0);
            }
            return 1;
        }
        if (key == WEVA_KEY_ENTER && (button || summary || link)) {
            if (down && (!link || !repeat)) activate_element(doc, focused_control, 0, 0, 0);
            return 1;
        }
        if (key == WEVA_KEY_SPACE && (button || summary || mark)) {
            if (down) {
                if (!repeat) doc->space_press_target = focused_control;
            } else {
                const Element* pressed = doc->space_press_target;
                doc->space_press_target = nullptr;
                if (pressed == focused_control) activate_element(doc, pressed, 0, 0, 0);
            }
            return 1;
        }
        if (down && key == WEVA_KEY_ENTER && mark && implicit_submit(doc, focused_control)) return 1;
    } else if (focused_control) {
        doc->space_press_target = nullptr;
        return 0;
    }
    if (!down && key == WEVA_KEY_SPACE) doc->space_press_target = nullptr;

    // Editing keys in a focused field. No host can do these for itself: where
    // the text is kept and where the cursor sits are both the document's.
    if (down) {
        Element* focused = const_cast<Element*>(doc->styles.state.focused);
        if (focused && is_text_field(*focused) && !text_contents_hidden(doc, *focused)) {
            InteractionState& st = doc->styles.state;
            std::string value = field_value(*focused);
            const bool multiline = focused->tag_name() == "textarea";
            if ((focused->has_attribute("readonly") || disabled_ancestor(focused)) &&
                (key == WEVA_KEY_BACKSPACE || key == WEVA_KEY_DELETE ||
                 (multiline && key == WEVA_KEY_ENTER))) return 1;
            int caret = std::min(static_cast<int>(value.size()), std::max(0, st.caret));
            // User-perceived characters include combining marks and joined
            // emoji. Navigation and forward deletion keep a grapheme intact;
            // Backspace has separate browser tailoring for accents and emoji.
            const auto prev = [&](int i) {
                return static_cast<int>(previous_grapheme(value, static_cast<size_t>(std::max(0, i)), GraphemeProfile::BrowserCaret));
            };
            const auto next = [&](int i) {
                return static_cast<int>(next_grapheme(value, static_cast<size_t>(std::max(0, i)), GraphemeProfile::BrowserCaret));
            };
            bool edited = false;
            // A shifted move extends the selection from where it started; an
            // unshifted one drops it. Ctrl+A takes the lot.
            const bool extend = (modifiers & WEVA_MOD_SHIFT) != 0;
            // Ctrl turns every motion key into its word- or document-sized
            // version: the difference between Left and Ctrl+Left is a
            // character and a word.
            const bool by_word = (modifiers & WEVA_MOD_CTRL) != 0;
            const auto word_left = [&](int i) {
                return static_cast<int>(previous_word_boundary(value, static_cast<size_t>(i)));
            };
            const auto word_right = [&](int i) {
                return static_cast<int>(next_word_boundary(value, static_cast<size_t>(i)));
            };
            const Selection sel = selection_of(st, value.size());
            const int anchor_before = st.anchor;
            const std::string before = value;
            const int caret_before = caret;
            const auto visual_move = [&](int direction) {
                // Text mutations may be awaiting layout when a host sends a key.
                weva_document_update(doc, 0);
                const bool continuing = st.vertical_owner == focused && st.vertical_index == caret &&
                    st.vertical_version == focused->form_version();
                double x = continuing ? st.vertical_x : std::numeric_limits<double>::quiet_NaN();
                PaintContext measure = measuring_context(doc);
                measure.caret = caret_for(st);
                size_t to = static_cast<size_t>(caret);
                bool downstream = st.caret_downstream;
                if (navigate_text_line(doc->tree, box_of(doc, focused), doc->ctx, measure,
                                       direction, &x, &to, &downstream)) {
                    st.caret_downstream = downstream;
                    st.vertical_owner = (direction == -1 || direction == 1 ||
                                         direction == -3 || direction == 3) ? focused : nullptr;
                    st.vertical_version = focused->form_version();
                    st.vertical_index = static_cast<int>(to);
                    st.vertical_x = x;
                }
                return static_cast<int>(to);
            };
            // Deleting the selection, wherever a key would have deleted one
            // character: that is what Backspace and Delete mean while
            // something is selected, and what typing does before it inserts.
            const auto erase_selection = [&]() {
                value.erase(static_cast<size_t>(sel.from), static_cast<size_t>(sel.to - sel.from));
                caret = sel.from;
                edited = true;
            };
            switch (key) {
                case WEVA_KEY_BACKSPACE:
                    if (!sel.empty()) {
                        erase_selection();
                    } else if (caret > 0) {
                        // Ctrl+Backspace eats the word, which is the only way
                        // to fix a mistyped one without holding the key.
                        const int from = by_word ? word_left(caret) :
                            static_cast<int>(backward_delete_boundary(value, static_cast<size_t>(caret)));
                        value.erase(static_cast<size_t>(from), static_cast<size_t>(caret - from));
                        caret = from;
                        edited = true;
                    }
                    break;
                case WEVA_KEY_DELETE:
                    if (!sel.empty()) {
                        erase_selection();
                    } else if (caret < static_cast<int>(value.size())) {
                        const int to = by_word ? word_right(caret) : next(caret);
                        value.erase(static_cast<size_t>(caret), static_cast<size_t>(to - caret));
                        edited = true;
                    }
                    break;
                case WEVA_KEY_LEFT:
                    st.caret_downstream = false;
                    caret = !extend && !by_word && !sel.empty() ? sel.from :
                        by_word ? word_left(caret) : prev(caret);
                    break;
                case WEVA_KEY_RIGHT:
                    st.caret_downstream = true;
                    caret = !extend && !by_word && !sel.empty() ? sel.to :
                        by_word ? word_right(caret) : next(caret);
                    break;
                case WEVA_KEY_HOME:
                    // Ctrl+Home is the top of the whole field, not the start
                    // of the line the caret happens to be on.
                    caret = (multiline && !by_word) ? visual_move(-2) : 0;
                    break;
                case WEVA_KEY_END:
                    caret = (multiline && !by_word) ? visual_move(2)
                                                    : static_cast<int>(value.size());
                    break;
                case WEVA_KEY_ENTER:
                    // The one key that means something different in a box you
                    // can write paragraphs in. In a one-line field it submits
                    // the form around it, if there is one -- which is what
                    // Enter has meant in a login box since forms existed.
                    if (!multiline) {
                        if (implicit_submit(doc, focused)) {
                            st.caret = caret;
                            return 1;
                        }
                        caret = -1;
                        break;
                    }
                    {
                        std::string scratch;
                        const size_t from = sel.empty() ? static_cast<size_t>(caret) : static_cast<size_t>(sel.from);
                        const size_t to = sel.empty() ? from : static_cast<size_t>(sel.to);
                        if (user_text(*focused, value, from, to, "\n", &scratch).empty()) return 1;
                    }
                    if (!sel.empty()) erase_selection();
                    value.insert(static_cast<size_t>(caret), 1, '\n');
                    ++caret;
                    edited = true;
                    break;
                case WEVA_KEY_UP:
                case WEVA_KEY_DOWN: {
                    // By line, keeping the column: in a textarea these move the
                    // caret rather than scrolling, and scrolling follows from
                    // the caret being brought back into view.
                    if (!multiline) { caret = -1; break; }
                    caret = visual_move((key == WEVA_KEY_UP ? -1 : 1) * (by_word ? 3 : 1));
                    break;
                }
                default: caret = -1; break;   // not ours
            }
            if (caret >= 0) {
                // An unshifted move puts the cursor down and lets go of what
                // was selected; a shifted one keeps hold of where it started.
                // An edit always collapses: the text that was selected is gone.
                if (edited || !extend) {
                    st.anchor = -1;
                } else if (anchor_before < 0) {
                    // The selection starts where the cursor WAS.
                    st.anchor = std::min(static_cast<int>(value.size()), std::max(0, st.caret));
                }
                st.caret = caret;
                st.caret_age = 0;   // a caret that blinks while you move it is unreadable
                doc->caret_follow = true;
                if (edited) {
                    // A key edit is its own undo step: Ctrl+Z after a deleted
                    // word puts the word back, not the whole sentence.
                    push_undo(doc, *focused, before, caret_before, anchor_before, false);
                    // Through the field, not into an attribute: a <textarea>
                    // keeps what it holds as its CONTENT, and writing the
                    // attribute put every backspace and newline somewhere
                    // nothing displays.
                    set_field_value(doc, *focused, value);
                    note_value_change(doc, *focused, value);
                }
                return 1;
            }
        }
    }

    // Space continues an active search, including a dropdown's multiword
    // label. With no search it retains its ordinary activation behavior.
    if (down && key == WEVA_KEY_SPACE && !(modifiers & (WEVA_MOD_CTRL | WEVA_MOD_ALT | WEVA_MOD_META)) &&
        doc->typeahead_target && doc->typeahead_target == doc->styles.state.focused &&
        doc->typeahead.active(input_time()))
        return weva_document_try_text_input_modifiers(doc, " ", modifiers);
    // An open list takes the keys before anything else: the arrows walk it,
    // Enter takes what is highlighted, Escape leaves it as it was.
    if (down && doc->open_select) {
        Element& select = const_cast<Element&>(*doc->open_select);
        const auto options = select_options(select);
        const int count = static_cast<int>(options.size());
        switch (key) {
            case WEVA_KEY_UP:
            case WEVA_KEY_DOWN: {
                if (count == 0) return 1;
                const int step = key == WEVA_KEY_DOWN ? 1 : -1;
                const int from = doc->highlighted_option < 0 ? chosen_index(select)
                                                             : doc->highlighted_option;
                doc->highlighted_option = next_option(doc, options, from < 0 && step < 0 ? count : from, step);
                reveal_highlighted_option(doc);
                doc->pending = worst(doc->pending, Invalidation::Paint);
                return 1;
            }
            case WEVA_KEY_HOME:
                doc->highlighted_option = next_option(doc, options, -1, 1);
                reveal_highlighted_option(doc);
                doc->pending = worst(doc->pending, Invalidation::Paint);
                return 1;
            case WEVA_KEY_END:
                doc->highlighted_option = next_option(doc, options, count, -1);
                reveal_highlighted_option(doc);
                doc->pending = worst(doc->pending, Invalidation::Paint);
                return 1;
            case WEVA_KEY_ENTER:
            case WEVA_KEY_SPACE:
                if (doc->highlighted_option >= 0) choose_option(doc, select, doc->highlighted_option);
                doc->set_open_select(nullptr);
                doc->highlighted_option = -1;
                doc->pending = worst(doc->pending, Invalidation::Boxes);
                return 1;
            case WEVA_KEY_ESCAPE:
            case WEVA_KEY_TAB:
                // Escape leaves the value alone, which is the difference
                // between cancelling and choosing.
                doc->set_open_select(nullptr);
                doc->highlighted_option = -1;
                doc->pending = worst(doc->pending, Invalidation::Paint);
                return key == WEVA_KEY_ESCAPE ? 1 : 0;
            default: break;
        }
    }

    // A focused select that is CLOSED opens on Enter or Space, and changes
    // value with the arrows without opening -- both as a browser does.
    if (down) {
        const Element* focused = doc->styles.state.focused;
        if (focused && focused->tag_name() == "select" && select_is_listbox(*focused) &&
            !disabled_ancestor(focused) && list_key(doc, *const_cast<Element*>(focused), key, modifiers)) return 1;
        if (focused && !doc->open_select && focused->tag_name() == "select" &&
            !select_is_listbox(*focused) && !disabled_ancestor(focused)) {
            Element& select = const_cast<Element&>(*focused);
            const int count = static_cast<int>(select_options(select).size());
            if (key == WEVA_KEY_ENTER || key == WEVA_KEY_SPACE) {
                doc->set_open_select(&select);
                doc->highlighted_option = chosen_index(select);
                doc->select_first_row = 0;
                reveal_highlighted_option(doc);
                doc->pending = worst(doc->pending, Invalidation::Paint);
                return 1;
            }
            if ((key == WEVA_KEY_UP || key == WEVA_KEY_DOWN) && count > 0) {
                const int step = key == WEVA_KEY_DOWN ? 1 : -1;
                const int from = chosen_index(select);
                choose_option(doc, select, next_option(doc, select_options(select),
                              from < 0 && step < 0 ? count : from, step));
                return 1;
            }
        }
    }

    // Scrolling from the keyboard. Reached only when a text field did not want
    // the key, because in one they move the caret instead.
    if (down) {
        const auto target = [&](bool vertical) -> BoxId {
            const InteractionState& st = doc->styles.state;
            // What has focus, or failing that what the pointer is over --
            // which is what a browser scrolls when you have clicked nothing.
            const Element* from = st.focused;
            if (!from && !st.hover_chain.empty()) from = st.hover_chain.front();
            if (!from) return kNoBox;
            for (BoxId id = box_of(doc, from); id != kNoBox; id = doc->tree[id].parent) {
                const Box& b = doc->tree[id];
                if (!b.element || !scrollable_on_axis(b, vertical)) continue;
                double mx = 0, my = 0;
                max_scroll(doc->tree, id, &mx, &my);
                if ((vertical ? my : mx) > 0) return id;
            }
            return kNoBox;
        };
        // A line is 40px, as it is for the wheel; a page is what you can see,
        // less a line of overlap so the eye keeps its place.
        const bool vertical = key != WEVA_KEY_LEFT && key != WEVA_KEY_RIGHT;
        BoxId id = kNoBox;
        switch (key) {
            case WEVA_KEY_PAGE_UP:
            case WEVA_KEY_PAGE_DOWN:
            case WEVA_KEY_UP:
            case WEVA_KEY_DOWN:
            case WEVA_KEY_LEFT:
            case WEVA_KEY_RIGHT:
            case WEVA_KEY_HOME:
            case WEVA_KEY_END: id = target(vertical); break;
            default: break;
        }
        if (id != kNoBox) {
            const Box& b = doc->tree[id];
            double mx = 0, my = 0;
            max_scroll(doc->tree, id, &mx, &my);
            const double page =
                std::max(40.0, (vertical ? b.height - b.border_top - b.border_bottom
                                         : b.width - b.border_left - b.border_right) - 40.0);
            double to = vertical ? b.scroll_y : b.scroll_x;
            switch (key) {
                case WEVA_KEY_PAGE_UP: to -= page; break;
                case WEVA_KEY_PAGE_DOWN: to += page; break;
                case WEVA_KEY_UP: to -= 40; break;
                case WEVA_KEY_DOWN: to += 40; break;
                case WEVA_KEY_LEFT: to -= 40; break;
                case WEVA_KEY_RIGHT: to += 40; break;
                case WEVA_KEY_HOME: to = 0; break;
                case WEVA_KEY_END: to = vertical ? my : mx; break;
                default: break;
            }
            to = std::clamp(to, 0.0, vertical ? my : mx);
            auto& at = doc->scroll[b.element];
            at = {vertical ? b.scroll_x : to, vertical ? to : b.scroll_y};
            doc->pending = worst(doc->pending, Invalidation::Paint);
            return 1;
        }
    }

    // Tab is the one key the engine acts on itself, because focus order is
    // something only the document knows. Everything else is the host's.
    if (down && key == WEVA_KEY_TAB && !(modifiers & WEVA_MOD_CTRL)) {
        weva_document_focus_next(doc, (modifiers & WEVA_MOD_SHIFT) ? 1 : 0);
        return 1;
    }
    return 0;
}

void weva_document_text_input(weva_document_t doc, const char* utf8) {
    weva_document_try_text_input(doc, utf8);
}

weva_element_t weva_document_text_input_candidate(weva_document_t doc) {
    if (!doc) return WEVA_ELEMENT_NONE;
    const Element* field = doc->styles.state.focused;
    return field && is_text_field(*field) ? doc->handle_of(field) : WEVA_ELEMENT_NONE;
}

weva_element_t weva_document_text_input_target(weva_document_t doc) {
    if (!doc) return WEVA_ELEMENT_NONE;
    const Element* field = doc->styles.state.focused;
    return field && is_text_field(*field) && !field->has_attribute("readonly") &&
           !focus_disabled(*field, doc) && !text_contents_hidden(doc, *field) ? doc->handle_of(field) : WEVA_ELEMENT_NONE;
}

weva_element_t weva_document_composition(weva_document_t doc, int* start, int* end) {
    if (start) *start = 0;
    if (end) *end = 0;
    if (!doc || !doc->styles.state.composing) return WEVA_ELEMENT_NONE;
    const auto& st = doc->styles.state;
    if (start) *start = st.composition_from;
    if (end) *end = st.composition_to;
    return doc->handle_of(st.focused);
}

int weva_document_caret_bounds(weva_document_t doc, double* x, double* y, double* width, double* height) {
    if (x) *x = 0;
    if (y) *y = 0;
    if (width) *width = 0;
    if (height) *height = 0;
    if (weva_document_text_input_target(doc) == WEVA_ELEMENT_NONE) return 0;
    PaintContext paint = measuring_context(doc);
    paint.caret = caret_for(doc->styles.state);
    resolve_caret_run(doc, &paint.caret);
    Rect bounds;
    if (!text_caret_bounds(doc->tree, box_of(doc, doc->styles.state.focused), doc->ctx, paint, &bounds)) return 0;
    if (x) *x = bounds.x;
    if (y) *y = bounds.y;
    if (width) *width = bounds.width;
    if (height) *height = bounds.height;
    return 1;
}

int weva_document_set_composition(weva_document_t doc, const char* utf8, int start, int end) {
    if (!doc || !utf8) return 0;
    if (!*utf8) return weva_document_commit_composition(doc, "");
    if (weva_document_text_input_target(doc) == WEVA_ELEMENT_NONE) return 0;
    auto& st = doc->styles.state;
    Element& field = *const_cast<Element*>(st.focused);
    std::string value = field_value(field);
    if (st.composing && value != doc->composition_value) {
        weva_document_commit_composition(doc, nullptr);
        return 0;
    }
    const bool beginning = !st.composing;
    if (beginning) {
        const Selection selection = selection_of(st, value.size());
        st.composition_from = static_cast<int>(text_boundary(value, selection.empty() ? st.caret : selection.from));
        st.composition_to = static_cast<int>(text_boundary(value, selection.empty() ? st.caret : selection.to));
        doc->composition_before = {value, st.caret, st.anchor};
        st.composing = true;
        composition_event(doc, WEVA_EVENT_COMPOSITION_START,
                          std::string_view(value).substr(st.composition_from, st.composition_to - st.composition_from));
    }
    const std::string text(utf8);
    const bool changed = beginning || std::string_view(value).substr(st.composition_from, st.composition_to - st.composition_from) != text;
    const int caret = st.composition_from + static_cast<int>(text_boundary(text, end));
    const int selected = st.composition_from + static_cast<int>(text_boundary(text, start));
    const int anchor = selected == caret ? -1 : selected;
    if (!changed && st.caret == caret && st.anchor == anchor) return 1;
    if (changed) composition_event(doc, WEVA_EVENT_COMPOSITION_UPDATE, text);
    value.replace(st.composition_from, st.composition_to - st.composition_from, text);
    st.composition_to = st.composition_from + static_cast<int>(text.size());
    st.caret = caret;
    st.anchor = anchor;
    st.caret_age = 0;
    doc->caret_follow = true;
    if (changed) {
        set_field_value(doc, field, value);
        note_value_change(doc, field, value);
    }
    doc->composition_value = value;
    doc->styles.repaint(doc->styles.state.focused);
    return 1;
}

int weva_document_commit_composition(weva_document_t doc, const char* utf8) {
    if (!doc) return 0;
    auto& st = doc->styles.state;
    if (!st.composing) return utf8 && *utf8 ? weva_document_try_text_input(doc, utf8) : 0;
    if (utf8 && weva_document_text_input_target(doc) == WEVA_ELEMENT_NONE) {
        weva_document_commit_composition(doc, nullptr);
        return 0;
    }
    Element& field = *const_cast<Element*>(st.focused);
    std::string value = field_value(field);
    const std::string previous = doc->composition_value.substr(st.composition_from, st.composition_to - st.composition_from);
    if (value != doc->composition_value) {
        st.composing = false;
        st.composition_from = st.composition_to = 0;
        doc->composition_before = {};
        doc->composition_value.clear();
        doc->styles.repaint(doc->styles.state.focused);
        composition_event(doc, WEVA_EVENT_COMPOSITION_END, previous);
        return 0;
    }
    const std::string final_text = utf8 ? std::string(utf8) : previous;
    std::string scratch;
    const std::string_view accepted = weva_document_text_input_target(doc) != WEVA_ELEMENT_NONE
        ? user_text(field, value, st.composition_from, st.composition_to, final_text, &scratch)
        : std::string_view(final_text);
    if (utf8 || accepted != previous) {
        if (final_text != previous || accepted != previous)
            composition_event(doc, WEVA_EVENT_COMPOSITION_UPDATE, final_text);
        if (accepted != previous) {
            value.replace(st.composition_from, st.composition_to - st.composition_from, accepted);
            set_field_value(doc, field, value);
            note_value_change(doc, field, value);
        }
        st.caret = st.composition_from + static_cast<int>(accepted.size());
        st.anchor = -1;
    }
    st.composing = false;
    st.composition_from = st.composition_to = 0;
    st.caret_age = 0;
    doc->caret_follow = true;
    const auto& before = doc->composition_before;
    if (value != before.text) push_undo(doc, field, before.text, before.caret, before.anchor, false);
    doc->composition_before = {};
    doc->composition_value.clear();
    if (auto history = doc->history.find(&field); history != doc->history.end()) history->second.last_was_typing = false;
    doc->styles.repaint(doc->styles.state.focused);
    if (utf8 && *utf8) composition_event(doc, WEVA_EVENT_TEXT_INPUT, accepted);
    composition_event(doc, WEVA_EVENT_COMPOSITION_END, final_text);
    return 1;
}

static int insert_user_text(weva_document_t doc, const char* utf8, bool paste, uint32_t modifiers = 0) {
    if (!doc || !utf8 || !*utf8) return 0;
    if (doc->styles.state.composing) return weva_document_commit_composition(doc, utf8);
    // Paste may start with a tab or newline; those characters delivered by a
    // physical key belong to the separate keyboard handler.
    const unsigned char lead = static_cast<unsigned char>(utf8[0]);
    Element* focused = const_cast<Element*>(doc->styles.state.focused);
    const bool printable = (lead >= 0x20 && lead != 0x7f) ||
                           (paste && (lead == '\n' || lead == '\r' || lead == '\t'));
    const bool edits = printable && weva_document_text_input_target(doc) != WEVA_ELEMENT_NONE;
    bool consumed = edits;
    std::string scratch;
    std::string_view accepted(utf8);
    if (!paste && focused && focused->tag_name() == "select" && !disabled_ancestor(focused) &&
        !(modifiers & (WEVA_MOD_CTRL | WEVA_MOD_ALT | WEVA_MOD_META))) {
        for (size_t at = 0; at < accepted.size();) {
            size_t length = 0;
            utf8_at(accepted, at, &length);
            if (!length) break;
            consumed = select_typeahead(doc, *focused, accepted.substr(at, length)) || consumed;
            at += length;
        }
    }
    if (edits) {
        std::string value = field_value(*focused);
        InteractionState& st = doc->styles.state;
        // Typing over a selection replaces it, which is what every text box
        // does and the reason select-all-then-type works.
        const Selection sel = selection_of(st, value.size());
        const size_t at = sel.empty() ? text_boundary(value, st.caret) : static_cast<size_t>(sel.from);
        const size_t to = sel.empty() ? at : static_cast<size_t>(sel.to);
        accepted = user_text(*focused, value, at, to, accepted, &scratch);
        if (accepted.empty() && sel.empty()) return 1;
        // Plain typing coalesces; typing that replaces a selection does not,
        // since undoing it has to bring the replaced text back on its own.
        push_undo(doc, *focused, value, st.caret, st.anchor, sel.empty() && !paste);
        if (!sel.empty()) {
            value.erase(static_cast<size_t>(sel.from), static_cast<size_t>(sel.to - sel.from));
            st.caret = sel.from;
        }
        st.anchor = -1;
        value.insert(at, accepted);
        st.caret = static_cast<int>(at + accepted.size());
        st.caret_age = 0;
        doc->caret_follow = true;
        set_field_value(doc, *focused, value);
        note_value_change(doc, *focused, value);
    }
    if (paste && !consumed) return 0;
    weva_event e{};
    e.kind = WEVA_EVENT_TEXT_INPUT;
    e.target = doc->handle_of(doc->styles.state.focused);
    weva_document::fill_handler(&e, doc->styles.state.focused);
    const size_t copy = text_boundary(accepted, static_cast<int>(std::min(accepted.size(), sizeof(e.text) - 1)));
    std::memcpy(e.text, accepted.data(), copy);
    e.text[copy] = '\0';
    doc->push_event(weva_document::QueuedEvent(e, accepted));
    return consumed ? 1 : 0;
}

int weva_document_try_text_input(weva_document_t doc, const char* utf8) {
    return insert_user_text(doc, utf8, false);
}
int weva_document_try_text_input_modifiers(weva_document_t doc, const char* utf8, uint32_t modifiers) {
    return insert_user_text(doc, utf8, false, modifiers);
}

int weva_document_paste_text(weva_document_t doc, const char* utf8) {
    return insert_user_text(doc, utf8, true);
}

int weva_document_open_select(weva_document_t doc, weva_element_t element) {
    if (!doc) return 0;
    if (element == WEVA_ELEMENT_NONE) {
        doc->set_open_select(nullptr);
        doc->highlighted_option = -1;
        doc->pending = worst(doc->pending, Invalidation::Paint);
        return 1;
    }
    Element* e = doc->element_at(element);
    if (!e || e->tag_name() != "select" || select_is_listbox(*e) || disabled_ancestor(e) || input_blocked(doc, e)) return 0;
    doc->set_open_select(e);
    doc->highlighted_option = chosen_index(*e);
    doc->select_first_row = 0;
    reveal_highlighted_option(doc);
    doc->pending = worst(doc->pending, Invalidation::Paint);
    return 1;
}

weva_element_t weva_document_open_select_element(weva_document_t doc) {
    if (!doc || !doc->open_select) return WEVA_ELEMENT_NONE;
    return doc->handle_of(doc->open_select);
}

namespace {

void forget_element(weva_document* doc, const Element* e);

// The ABI needs a terminated path, while substitution passes a string view.
// Common paths fit on the stack; long paths retain the same owning fallback.
class BindingPath {
public:
    explicit BindingPath(std::string_view path) {
        if (path.size() < sizeof(stack_)) {
            if (!path.empty()) std::memcpy(stack_, path.data(), path.size());
            stack_[path.size()] = '\0';
        } else {
            owned_.assign(path);
            data_ = owned_.c_str();
        }
    }
    BindingPath(const BindingPath&) = delete;
    BindingPath& operator=(const BindingPath&) = delete;
    const char* c_str() const { return data_; }
private:
    char stack_[128];
    std::string owned_;
    const char* data_ = stack_;
};

// The host's callback, wearing the interface the substitution wants.
class AbiBindingResolver : public BindingResolver {
public:
    explicit AbiBindingResolver(const weva_binding_source& source) : source_(source) {}

    bool resolve(std::string_view path, std::string* out) const override {
        if (!source_.value) return false;
        const BindingPath key(path);
        int found = 0;
        // The two-call pattern the rest of the ABI uses: ask for the length,
        // then fill. Most values are short, so the first call usually answers
        // both questions.
        char stack[128];
        const size_t n = source_.value(source_.user, key.c_str(), stack, sizeof(stack), &found);
        if (!found) return false;
        if (n < sizeof(stack)) {
            out->assign(stack, n);
            return true;
        }
        std::vector<char> heap;
        size_t required = n;
        for (;;) {
            // Include the terminator without allowing a callback's length to
            // wrap. Invalid lengths use the unavailable-value behavior.
            if (required >= heap.max_size()) {
                std::fprintf(stderr, "weva: binding callback returned an unrepresentable value length.\n");
                return false;
            }
            const size_t doubled = heap.size() <= heap.max_size() / 2 ? heap.size() * 2 : heap.max_size();
            heap.resize(std::max(required + 1, doubled));
            required = source_.value(source_.user, key.c_str(), heap.data(), heap.size(), &found);
            if (!found) return false;
            if (required < heap.size()) {
                // This call owns both the bytes and their length. A getter may
                // have changed since the earlier capacity probe.
                out->assign(heap.data(), required);
                return true;
            }
        }
    }

    int count(std::string_view path) const override {
        if (!source_.count) return -1;
        const BindingPath key(path);
        return source_.count(source_.user, key.c_str());
    }

private:
    const weva_binding_source& source_;
};

}   // namespace

void weva_document_set_binding_source(weva_document_t doc, const weva_binding_source* source) {
    if (!doc) return;
    doc->has_binding_source = source != nullptr && source->value != nullptr;
    doc->binding_source = doc->has_binding_source ? *source : weva_binding_source{};
}

int weva_document_refresh_bindings(weva_document_t doc) {
    if (!doc || !doc->doc || !doc->has_binding_source) return 0;
    const AbiBindingResolver resolver(doc->binding_source);
    doc->refreshing_bindings = true;
    doc->binding_structure_changed = false;
    const int changed = apply_bindings(*doc->doc, resolver, &doc->binding_templates,
                                      &doc->binding_repeats);
    doc->refreshing_bindings = false;
    if (doc->binding_structure_changed) doc->reindex_new_elements();
    return changed;
}

int weva_document_select_word_at(weva_document_t doc, double x, double y) {
    if (!doc) return 0;
    const Element* hit = input_element_at(doc, x, y);
    if (!hit || !is_text_field(*hit)) return 0;
    weva_document_set_focus(doc, doc->handle_of(hit));
    const std::string value = field_value(*hit);
    if (value.empty()) return 0;
    size_t from = 0, to = 0;
    word_around(value, field_offset_at(doc, *hit, x, y), &from, &to);
    if (from >= to) return 0;
    InteractionState& st = doc->styles.state;
    st.anchor = static_cast<int>(from);
    st.caret = static_cast<int>(to);
    st.caret_age = 0;
    doc->styles.repaint(doc->styles.state.focused);
    return 1;
}

namespace {

// Undo and redo are the same move in opposite directions: take the top of one
// stack, put what the field holds now on the other, and adopt what was taken.
int step_history(weva_document* doc, bool undoing) {
    if (!doc) return 0;
    InteractionState& st = doc->styles.state;
    if (!st.focused || !is_text_field(*st.focused) || st.focused->has_attribute("readonly") ||
        disabled_ancestor(st.focused)) return 0;
    const auto found = doc->history.find(st.focused);
    if (found == doc->history.end()) return 0;
    weva_document::EditHistory& h = found->second;
    std::vector<weva_document::EditSnapshot>& from = undoing ? h.undo : h.redo;
    std::vector<weva_document::EditSnapshot>& to = undoing ? h.redo : h.undo;
    if (from.empty()) return 0;

    Element& field = const_cast<Element&>(*st.focused);
    const std::string current = field_value(field);
    to.push_back({current, st.caret, st.anchor});
    const weva_document::EditSnapshot entry = from.back();
    from.pop_back();

    set_field_value(doc, field, entry.text);
    note_value_change(doc, field, entry.text);
    // The cursor goes back where it was, which is the half of undo that makes
    // it usable: landing at the end of the field after every Ctrl+Z is not it.
    st.caret = std::min(static_cast<int>(entry.text.size()), std::max(0, entry.caret));
    st.anchor = entry.anchor < 0
                    ? -1
                    : std::min(static_cast<int>(entry.text.size()), entry.anchor);
    st.caret_age = 0;
    // A step is never typing, so the next character typed starts a new run
    // instead of joining whatever the stack last held.
    h.last_was_typing = false;
    doc->caret_follow = true;
    doc->styles.repaint(doc->styles.state.focused);
    return 1;
}

}   // namespace

int weva_document_undo(weva_document_t doc) {
    weva_document_commit_composition(doc, nullptr);
    return step_history(doc, true);
}

int weva_document_redo(weva_document_t doc) {
    weva_document_commit_composition(doc, nullptr);
    return step_history(doc, false);
}

int weva_document_select_all(weva_document_t doc) {
    if (!doc) return 0;
    weva_document_commit_composition(doc, nullptr);
    InteractionState& st = doc->styles.state;
    if (st.focused && st.focused->tag_name() == "select" && st.focused->has_attribute("multiple") &&
        !disabled_ancestor(st.focused)) {
        auto& select = *const_cast<Element*>(st.focused);
        auto& state = list_selection(doc, select);
        const auto before = selected_options(select);
        for (const auto* option : select_options(select))
            const_cast<Element*>(option)->set_form_selected(navigable_option(doc, option));
        state.version = select.form_version();
        if (before != selected_options(select)) note_value_change(doc, select, list_box_value(select));
        return 1;
    }
    if (!st.focused || !is_text_field(*st.focused)) return 0;
    st.anchor = 0;
    st.caret = static_cast<int>(field_value(*st.focused).size());
    st.caret_age = 0;
    doc->caret_follow = true;
    doc->styles.repaint(doc->styles.state.focused);
    return 1;
}

size_t weva_document_selected_text(weva_document_t doc, char* buffer, size_t capacity) {
    if (!doc) return 0;
    const InteractionState& st = doc->styles.state;
    if (!st.focused || !is_text_field(*st.focused)) return 0;
    const std::string value = field_value(*st.focused);
    const Selection sel = selection_of(st, value.size());
    if (sel.empty()) return 0;
    const std::string text = value.substr(static_cast<size_t>(sel.from),
                                          static_cast<size_t>(sel.to - sel.from));
    if (buffer && capacity > 0) {
        const size_t n = text.size() < capacity - 1 ? text.size() : capacity - 1;
        if (n > 0) std::memcpy(buffer, text.data(), n);
        buffer[n] = '\0';
    }
    return text.size();
}

weva_status weva_element_selection(weva_document_t doc, weva_element_t element, int* out_start,
                                   int* out_end) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    const Element* e = doc->element_at(element);
    if (!e) return WEVA_ERR_NOT_FOUND;
    const InteractionState& st = doc->styles.state;
    const bool mine = st.focused == e;
    const auto saved = doc->field_selections.find(e);
    const int caret = mine ? st.caret : saved != doc->field_selections.end() ? saved->second.caret : 0;
    const int anchor = mine ? st.anchor : saved != doc->field_selections.end() ? saved->second.anchor : -1;
    if (out_start) *out_start = anchor >= 0 ? anchor : caret;
    if (out_end) *out_end = caret;
    return WEVA_OK;
}

weva_status weva_element_set_selection(weva_document_t doc, weva_element_t element, int start,
                                       int end) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    Element* e = doc->element_at(element);
    if (!e) return WEVA_ERR_NOT_FOUND;
    if (!is_text_field(*e)) return WEVA_ERR_INVALID_ARGUMENT;
    InteractionState& st = doc->styles.state;
    if (st.focused != e) {
        const auto status = weva_document_set_focus(doc, element);
        if (status != WEVA_OK) return status;
    }
    weva_document_commit_composition(doc, nullptr);
    st.vertical_owner = nullptr;
    st.caret_downstream = true;
    const std::string value = field_value(*e);
    st.caret = static_cast<int>(text_boundary(value, end));
    const int anchor = static_cast<int>(text_boundary(value, start));
    st.anchor = anchor == st.caret ? -1 : anchor;
    st.caret_age = 0;
    doc->caret_follow = true;
    doc->styles.repaint(doc->styles.state.focused);
    return WEVA_OK;
}

weva_status weva_element_set_selection_without_focus(weva_document_t doc,
    weva_element_t element, int start, int end) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    Element* e = doc->element_at(element);
    if (!e) return WEVA_ERR_NOT_FOUND;
    if (!is_text_field(*e)) return WEVA_ERR_INVALID_ARGUMENT;
    if (doc->styles.state.focused == e)
        return weva_element_set_selection(doc, element, start, end);
    auto& saved = doc->field_selections[e];
    saved.value.assign(e->form_edit_value());
    saved.caret = static_cast<int>(text_boundary(saved.value, end));
    const int anchor = static_cast<int>(text_boundary(saved.value, start));
    saved.anchor = anchor == saved.caret ? -1 : anchor;
    return WEVA_OK;
}

size_t weva_element_value(weva_document_t doc, weva_element_t element, char* buffer,
                          size_t capacity) {
    if (!doc) return 0;
    const Element* e = doc->element_at(element);
    if (!e) return 0;
    std::string value;
    const std::string type = input_type_of(*e);
    if (e->tag_name() == "input" && (type == "checkbox" || type == "radio")) {
        value = e->form_checked() ? "on" : "";
    } else if (e->tag_name() == "input" && type == "range") {
        value = form_number_text(RangeValue(*e).value);
    } else if (e->tag_name() == "select") {
        // A select's value is the chosen option's, which is where the DOM
        // keeps it: there is no `value` attribute on the select itself. A
        // `multiple` one can have several, and reports them all.
        if (e->has_attribute("multiple")) {
            value = list_box_value(*e);
        } else {
            const std::vector<const Element*> options = select_options(*e);
            const int index = chosen_index(*e);
            if (index >= 0 && index < static_cast<int>(options.size())) {
                const Element& chosen = *options[static_cast<size_t>(index)];
                value = option_value(chosen);
            }
        }
    } else {
        value = std::string(e->form_value());
    }
    if (buffer && capacity > 0) {
        const size_t n = value.size() < capacity - 1 ? value.size() : capacity - 1;
        // Guarded: an absent attribute is an empty string_view whose data() is
        // null, and memcpy from null is undefined even for zero bytes.
        if (n > 0) std::memcpy(buffer, value.data(), n);
        buffer[n] = '\0';
    }
    return value.size();
}

uint64_t weva_element_form_version(weva_document_t doc, weva_element_t element) {
    const auto* control = doc ? doc->element_at(element) : nullptr;
    return control ? static_cast<uint64_t>(control->form_version()) : 0;
}

weva_status weva_element_set_value(weva_document_t doc, weva_element_t element,
                                   const char* value) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    Element* e = doc->element_at(element);
    if (!e) return WEVA_ERR_NOT_FOUND;
    if (e == doc->styles.state.focused) weva_document_commit_composition(doc, nullptr);
    const std::string_view v(value ? value : "");
    const bool text_focused = e == doc->styles.state.focused && is_text_field(*e);
    const bool first_text_value = is_text_field(*e) && !text_focused &&
        doc->field_selections.find(e) == doc->field_selections.end();
    const std::string previous = text_focused || first_text_value ? field_value(*e) : std::string();
    // A script replacing the value invalidates the undo stack: it describes a
    // field that no longer holds what it described, and putting one of its
    // snapshots back would silently discard what the script just wrote.
    doc->history.erase(e);
    const std::string type = input_type_of(*e);
    if (e->tag_name() == "input" && (type == "checkbox" || type == "radio")) {
        const bool on = !v.empty() && v != "0" && v != "off" && v != "false";
        e->set_form_checked(on);
    } else if (e->tag_name() == "select") {
        set_select_value(*e, v);
    } else {
        e->set_form_value(v);
    }
    if (first_text_value && e->form_edit_value() != previous) {
        auto& saved = doc->field_selections[e];
        saved.value.assign(e->form_edit_value());
        saved.caret = static_cast<int>(saved.value.size());
        saved.anchor = -1;
    }
    if (text_focused) {
        const auto current = e->form_edit_value();
        if (current != previous) {
            doc->styles.state.caret = static_cast<int>(current.size());
            doc->styles.state.anchor = -1;
            doc->styles.state.caret_age = 0;
            doc->caret_follow = true;
        }
        if (!doc->user_edited_since_focus) doc->value_at_focus = current;
    }
    // Attribute and form-state mutation observers already schedule the exact
    // work. Forcing cascade here defeats stable selector-state updates.
    return WEVA_OK;
}

weva_element_t weva_element_form(weva_document_t doc, weva_element_t element) {
    const Element* e = doc ? doc->element_at(element) : nullptr;
    return e ? doc->handle_of(form_owner(*e)) : WEVA_ELEMENT_NONE;
}

weva_status weva_document_reset_form(weva_document_t doc, weva_element_t form) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    Element* owner = doc->element_at(form);
    if (!owner) return WEVA_ERR_NOT_FOUND;
    if (owner->tag_name() != "form") return WEVA_ERR_INVALID_ARGUMENT;
    auto& state = doc->styles.state;
    const bool focused = state.focused && form_owner(*state.focused) == owner;
    if (doc->text_drag && form_owner(*doc->text_drag) == owner) clear_text_drag(doc);
    const std::string previous = focused && is_text_field(*state.focused) ? field_value(*state.focused) : std::string();
    if (focused && state.composing) {
        // Reset discards the preedit rather than committing an edit that is
        // immediately replaced. The host still needs to end its IME session.
        composition_event(doc, WEVA_EVENT_COMPOSITION_END, "");
        state.composing = false;
        state.composition_from = state.composition_to = 0;
        doc->composition_before = {};
        doc->composition_value.clear();
    }
    reset_form(*owner);
    for (auto it = doc->history.begin(); it != doc->history.end();) {
        if (form_owner(*it->first) == owner) it = doc->history.erase(it);
        else ++it;
    }
    if (focused && is_text_field(*state.focused)) {
        doc->value_at_focus = field_value(*state.focused);
        doc->user_edited_since_focus = false;
        if (doc->value_at_focus != previous) {
            state.caret = static_cast<int>(doc->value_at_focus.size());
            state.anchor = -1;
        }
        state.caret_age = 0;
        state.text_scroll_x = 0;
        doc->caret_follow = true;
    }
    if (doc->open_select && form_owner(*doc->open_select) == owner) doc->set_open_select(nullptr);
    for (auto it = doc->list_selections.begin(); it != doc->list_selections.end();) {
        if (form_owner(*it->first) == owner) {
            if (doc->list_drag == it->first) { doc->list_drag = nullptr; doc->list_scroll_armed = false; }
            invalidate_list_row(doc, it->first);
            it = doc->list_selections.erase(it);
        } else ++it;
    }
    doc->list_follow = nullptr;
    doc->queue_event(WEVA_EVENT_RESET, owner, 0, 0, 0);
    return WEVA_OK;
}

namespace {
bool supports_custom_validity(const Element* element) {
    if (!element) return false;
    const auto tag = element->tag_name();
    return tag == "input" || tag == "textarea" || tag == "select" || tag == "button" || tag == "fieldset";
}
}

weva_status weva_element_set_custom_validity(weva_document_t doc, weva_element_t element, const char* message) {
    if (!doc || !message) return WEVA_ERR_INVALID_ARGUMENT;
    auto* control = const_cast<Element*>(doc->element_at(element));
    if (!supports_custom_validity(control)) return WEVA_ERR_NOT_FOUND;
    control->set_custom_validity(message);
    return WEVA_OK;
}

size_t weva_element_custom_validity(weva_document_t doc, weva_element_t element, char* buffer, size_t capacity) {
    const auto* control = doc ? doc->element_at(element) : nullptr;
    const auto message = supports_custom_validity(control) ? control->custom_validity() : std::string_view();
    if (buffer && capacity) {
        const auto n = text_boundary(message, static_cast<int>(std::min(message.size(), capacity - 1)));
        if (n) std::memcpy(buffer, message.data(), n);
        buffer[n] = 0;
    }
    return message.size();
}

weva_status weva_element_validity(weva_document_t doc, weva_element_t element,
                                  uint32_t* errors, int* will_validate) {
    if (errors) *errors = 0;
    if (will_validate) *will_validate = 0;
    if (!doc || !errors || !will_validate) return WEVA_ERR_INVALID_ARGUMENT;
    const auto* field = doc->element_at(element);
    if (!supports_custom_validity(field)) return WEVA_ERR_NOT_FOUND;
    if (field->tag_name() == "input" && field->has_attribute("pattern") && !field->form_value().empty()) {
        const auto type = form_input_type(*field);
        if (type == "text" || type == "search" || type == "tel" || type == "url" || type == "email" || type == "password")
            return WEVA_ERR_UNSUPPORTED;
    }
    *will_validate = form_is_validation_candidate(*field) ? 1 : 0;
    const auto length = form_text_length_validity(*field);
    const auto number = form_number_validity(*field);
    const auto temporal = form_temporal_validity(*field);
    if (form_required_value_missing(*field)) *errors |= WEVA_VALIDITY_VALUE_MISSING;
    if (form_email_type_mismatch(*field) || form_url_type_mismatch(*field)) *errors |= WEVA_VALIDITY_TYPE_MISMATCH;
    if (length.too_long) *errors |= WEVA_VALIDITY_TOO_LONG;
    if (length.too_short) *errors |= WEVA_VALIDITY_TOO_SHORT;
    if (number.range_underflow || temporal.range_underflow) *errors |= WEVA_VALIDITY_RANGE_UNDERFLOW;
    if (number.range_overflow || temporal.range_overflow) *errors |= WEVA_VALIDITY_RANGE_OVERFLOW;
    if (number.step_mismatch || temporal.step_mismatch) *errors |= WEVA_VALIDITY_STEP_MISMATCH;
    if (field->form_bad_input()) *errors |= WEVA_VALIDITY_BAD_INPUT;
    if (!field->custom_validity().empty()) *errors |= WEVA_VALIDITY_CUSTOM_ERROR;
    return WEVA_OK;
}

namespace {
bool has_validation_error(const Element& field) {
    return field.form_bad_input() || !field.custom_validity().empty() || form_required_value_missing(field) ||
        form_email_type_mismatch(field) || form_url_type_mismatch(field) ||
        !form_text_length_validity(field).valid() || !form_number_validity(field).valid() ||
        !form_temporal_validity(field).valid();
}
bool validate_form_constraints(weva_document* doc, const Ref<Element>& form, const Ref<Element>& submitter) {
    if (form->has_attribute("novalidate") || (submitter && submitter->has_attribute("formnovalidate"))) return true;
    std::shared_ptr<weva_document::ValidationReport> report;
    for (const auto handle : doc->document_order()) {
        auto* field = const_cast<Element*>(doc->element_at(handle));
        if (!field || !form_is_validation_candidate(*field) || form_owner(*field) != form.get() ||
            !has_validation_error(*field)) continue;
        if (!report) {
            report = std::make_shared<weva_document::ValidationReport>();
            report->form = form;
        }
        weva_event event{};
        event.kind = WEVA_EVENT_INVALID;
        event.target = handle;
        doc->fill_handler(&event, field);
        weva_document::QueuedEvent queued(event);
        queued.invalid_target = Ref<Element>::retain(field);
        queued.validation_report = report;
        if (doc->push_event(std::move(queued))) ++report->pending;
    }
    return !report;
}

void finish_validation_event(weva_document* doc, const std::shared_ptr<weva_document::ValidationReport>& report,
                             const Ref<Element>& field, bool prevented) {
    if (!report) return;
    if (!prevented && doc->handle_of(field.get()) != WEVA_ELEMENT_NONE) report->unhandled.push_back(field);
    if (--report->pending != 0 || !report->interactive) return;
    // Reporting follows all invalid handlers, so later handlers can change
    // focusability or remove an earlier invalid control before reporting.
    for (const auto& candidate : report->unhandled) {
        const auto handle = doc->handle_of(candidate.get());
        if (handle == WEVA_ELEMENT_NONE || !form_is_validation_candidate(*candidate)) continue;
        weva_document_set_focus(doc, handle);
        if (weva_document_focus(doc) == handle) break;
    }
}

void complete_form_submission(weva_document* doc, const Ref<Element>& form,
                              const Ref<Element>& submitter, const std::string& image_coordinates) {
    if (!form || doc->handle_of(form.get()) == WEVA_ELEMENT_NONE) return;
    const auto method = submitter && submitter->has_attribute("formmethod")
        ? submitter->get_attribute("formmethod") : form->get_attribute("method");
    constexpr std::string_view dialog_method = "dialog";
    if (method.size() != dialog_method.size()) return;
    for (size_t i = 0; i < method.size(); ++i) {
        char c = method[i];
        if (c >= 'A' && c <= 'Z') c += 'a' - 'A';
        if (c != dialog_method[i]) return;
    }
    for (Node* node = form->parent(); node; node = node->parent()) {
        if (!node->is_element()) continue;
        auto* dialog = static_cast<Element*>(node);
        if (dialog->tag_name() != "dialog") continue;
        if (submitter && submitter->tag_name() == "input" && form_input_type(*submitter) == "image") {
            weva_element_close_dialog_with_value(doc, doc->handle_of(dialog),
                image_coordinates.empty() ? "0,0" : image_coordinates.c_str());
            return;
        }
        // Evaluate the value after submission handlers, not at activation time.
        // An absent button value is different from an explicitly empty value.
        const std::string value = submitter ? std::string(submitter->get_attribute("value")) : "";
        weva_element_close_dialog_with_value(doc, doc->handle_of(dialog),
            !submitter || submitter->has_attribute("value") ? value.c_str() : nullptr);
        return;
    }
}

bool valid_close_request(weva_document* doc, const Ref<Element>& target, uint64_t generation) {
    if (!target || !target->has_attribute("open") || doc->handle_of(target.get()) == WEVA_ELEMENT_NONE)
        return false;
    const auto it = doc->dialog_close_generations.find(target.get());
    return it != doc->dialog_close_generations.end() && it->second == generation;
}
}

namespace {
weva_status explicit_validity(weva_document_t doc, weva_element_t element, int* valid, bool interactive) {
    if (valid) *valid = 0;
    if (!doc || !valid) return WEVA_ERR_INVALID_ARGUMENT;
    auto* target = const_cast<Element*>(doc->element_at(element));
    if (!target) return WEVA_ERR_NOT_FOUND;
    const bool is_form = target->tag_name() == "form";
    if (!is_form && !supports_custom_validity(target)) return WEVA_ERR_NOT_FOUND;
    if (doc->polled_invalid_target) return WEVA_ERR_INVALID_STATE;
    std::vector<Ref<Element>> invalid;
    const auto collect = [&](weva_element_t handle) -> weva_status {
        auto* field = const_cast<Element*>(doc->element_at(handle));
        if (!field || !form_is_validation_candidate(*field)) return WEVA_OK;
        if (is_form && form_owner(*field) != target) return WEVA_OK;
        uint32_t errors = 0;
        int will = 0;
        const auto status = weva_element_validity(doc, handle, &errors, &will);
        if (status != WEVA_OK) return status;
        if (errors) invalid.push_back(Ref<Element>::retain(field));
        return WEVA_OK;
    };
    if (is_form) {
        for (const auto handle : doc->document_order()) {
            const auto status = collect(handle);
            if (status != WEVA_OK) return status;
        }
    } else {
        const auto status = collect(element);
        if (status != WEVA_OK) return status;
    }
    *valid = invalid.empty() ? 1 : 0;
    if (invalid.empty()) return WEVA_OK;
    const size_t protected_events = std::count_if(doc->events.begin(), doc->events.end(),
        [](const auto& queued) { return queued.kind == WEVA_EVENT_CANCEL || queued.submit_form || queued.invalid_target || queued.popover_open_target || queued.popover_hide_target; });
    if (protected_events >= weva_document::kMaxEvents || invalid.size() > weva_document::kMaxEvents - protected_events) return WEVA_ERR_INTERNAL;
    auto report = std::make_shared<weva_document::ValidationReport>();
    if (is_form) report->form = Ref<Element>::retain(target);
    report->standalone_control = !is_form;
    report->interactive = interactive;
    for (const auto& field : invalid) {
        weva_event event{};
        event.kind = WEVA_EVENT_INVALID;
        event.target = doc->handle_of(field.get());
        doc->fill_handler(&event, field.get());
        weva_document::QueuedEvent queued(event);
        queued.invalid_target = field;
        queued.validation_report = report;
        if (doc->push_event(std::move(queued))) ++report->pending;
        else return WEVA_ERR_INTERNAL;
    }
    return WEVA_OK;
}
}

weva_status weva_element_check_validity(weva_document_t doc, weva_element_t element, int* valid) {
    return explicit_validity(doc, element, valid, false);
}
weva_status weva_element_report_validity(weva_document_t doc, weva_element_t element, int* valid) {
    return explicit_validity(doc, element, valid, true);
}

int weva_document_prevent_default(weva_document_t doc) {
    if (!doc || (!doc->polled_close_target && !doc->polled_submit_form && !doc->polled_invalid_target && !doc->polled_popover_open_target)) return 0;
    doc->polled_close_prevented = true;
    return 1;
}

int weva_document_poll_event(weva_document_t doc, weva_event* out) {
    if (!doc || !out) return 0;
    // Finish the previous default action only after the host's handler returned.
    // Clear the active slot before closing, so nested event drains cannot replay it.
    const int previous_popover_open_mode = doc->polled_popover_open_mode;
    const bool previous_popover_restore_focus = doc->polled_popover_restore_focus;
    Ref<Element> previous_popover_hide = std::move(doc->polled_popover_hide_target);
    Ref<Element> previous_popover_source = std::move(doc->polled_popover_open_source);
    Ref<Element> previous_popover = std::move(doc->polled_popover_open_target);
    Ref<Element> previous = std::move(doc->polled_close_target);
    std::string previous_value = std::move(doc->polled_close_value);
    Ref<Element> previous_form = std::move(doc->polled_submit_form);
    Ref<Element> previous_submitter = std::move(doc->polled_submitter);
    std::string previous_coordinates = std::move(doc->polled_image_coordinates);
    Ref<Element> previous_invalid = std::move(doc->polled_invalid_target);
    auto previous_report = std::move(doc->polled_validation_report);
    if (previous_invalid) finish_validation_event(doc, previous_report, previous_invalid, doc->polled_close_prevented);
    if (previous && !doc->polled_close_prevented &&
        valid_close_request(doc, previous, doc->polled_close_generation))
        weva_element_close_dialog_with_value(doc, doc->handle_of(previous.get()),
            doc->polled_has_close_value ? previous_value.c_str() : nullptr);
    if (previous_form && !doc->polled_close_prevented)
        complete_form_submission(doc, previous_form, previous_submitter, previous_coordinates);
    if (previous_popover && !doc->polled_close_prevented)
        complete_popover_open(doc, std::move(previous_popover), std::move(previous_popover_source), previous_popover_open_mode);
    if (previous_popover_hide && doc->handle_of(previous_popover_hide.get()) != WEVA_ELEMENT_NONE)
        popover_hide(doc, *previous_popover_hide, previous_popover_restore_focus);
    doc->polled_close_prevented = false;
    while (!doc->events.empty()) {
        auto event = std::move(doc->events.front());
        doc->events.pop_front();
        if (event.popover_resume_open) {
            complete_popover_open(doc, std::move(event.popover_open_target), std::move(event.popover_open_source), event.popover_open_mode, std::move(event.popover_pending_closes), true);
            continue;
        }
        if (event.kind == WEVA_EVENT_CANCEL &&
            !valid_close_request(doc, event.close_target, event.close_generation)) continue;
        if (event.popover_hide_target &&
            (doc->handle_of(event.popover_hide_target.get()) == WEVA_ELEMENT_NONE ||
             !event.popover_hide_target->is_popover_open())) continue;
        if (event.popover_open_target &&
            (doc->handle_of(event.popover_open_target.get()) == WEVA_ELEMENT_NONE ||
             !event.popover_open_target->has_attribute("popover") ||
             event.popover_open_target->is_popover_open() || event.popover_open_target->is_modal())) continue;
        if (event.submit_form && doc->handle_of(event.submit_form.get()) == WEVA_ELEMENT_NONE) continue;
        if (event.invalid_target && (doc->handle_of(event.invalid_target.get()) == WEVA_ELEMENT_NONE ||
            !form_is_validation_candidate(*event.invalid_target) ||
            (!event.validation_report->standalone_control && form_owner(*event.invalid_target) != event.validation_report->form.get()) ||
            !has_validation_error(*event.invalid_target))) {
            finish_validation_event(doc, event.validation_report, event.invalid_target, true);
            continue;
        }
        if (event.submit_form && !validate_form_constraints(doc, event.submit_form, event.submitter)) continue;
        *out = event;
        doc->polled_event_text = std::move(event.full_text);
        if (doc->polled_event_text.empty()) doc->polled_event_text = out->text;
        doc->polled_popover_restore_focus = event.popover_restore_focus;
        doc->polled_popover_hide_target = std::move(event.popover_hide_target);
        doc->polled_popover_open_source = std::move(event.popover_open_source);
        doc->polled_popover_open_mode = event.popover_open_target ? popover_mode_id(*event.popover_open_target) : 0;
        doc->polled_popover_open_target = std::move(event.popover_open_target);
        doc->polled_close_target = std::move(event.close_target);
        doc->polled_close_generation = event.close_generation;
        doc->polled_has_close_value = event.has_close_value;
        doc->polled_close_value = std::move(event.close_value);
        doc->polled_submit_form = std::move(event.submit_form);
        doc->polled_submitter = std::move(event.submitter);
        doc->polled_image_coordinates = std::move(event.image_coordinates);
        doc->polled_invalid_target = std::move(event.invalid_target);
        doc->polled_validation_report = std::move(event.validation_report);
        return 1;
    }
    return 0;
}

size_t weva_document_event_text(weva_document_t doc, char* buffer, size_t capacity) {
    if (!doc) return 0;
    const auto& text = doc->polled_event_text;
    if (buffer && capacity > 0) {
        const size_t count = text_boundary(text, static_cast<int>(std::min(text.size(), capacity - 1)));
        std::memcpy(buffer, text.data(), count);
        buffer[count] = 0;
    }
    return text.size();
}

int weva_element_contains(weva_document_t doc, weva_element_t ancestor,
                          weva_element_t descendant) {
    if (!doc) return 0;
    const Element* a = doc->element_at(ancestor);
    const Element* d = doc->element_at(descendant);
    if (!a || !d) return 0;
    for (const Node* n = d; n; n = n->parent()) {
        if (n == a) return 1;
    }
    return 0;
}

void weva_document_set_tooltip_delay(weva_document_t doc, double seconds) {
    if (!doc) return;
    doc->tooltip_delay = seconds;
    if (seconds < 0) tooltip_hide(doc);
}

weva_element_t weva_document_focus(weva_document_t doc) {
    if (!doc) return WEVA_ELEMENT_NONE;
    const Element* focused = doc->styles.state.focused;
    return focused ? doc->handle_of(focused) : WEVA_ELEMENT_NONE;
}

weva_status weva_document_set_focus(weva_document_t doc, weva_element_t element) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    const bool geometry_pending = doc->pending >= Invalidation::Layout || doc->dom_touched ||
                                  !doc->styles.pending_box_inputs.empty();
    InteractionState& st = doc->styles.state;
    const Element* target = nullptr;
    if (element != WEVA_ELEMENT_NONE) {
        target = doc->element_at(element);
        if (!target) return WEVA_ERR_NOT_FOUND;
        // A disabled control cannot be focused, in a browser or here, so a
        // host cannot put the keyboard somewhere the user could not.
        if (disabled_ancestor(target)) return WEVA_ERR_INVALID_ARGUMENT;
        if (input_blocked(doc, target)) return WEVA_ERR_INVALID_STATE;
        if (focus_unavailable_now(doc, *target)) return WEVA_ERR_INVALID_STATE;
    }
    if (st.focused == target) return WEVA_OK;
    clear_text_drag(doc);
    weva_document_commit_composition(doc, nullptr);
    doc->space_press_target = nullptr;
    doc->activation_keys_down = 0;
    if (target && target->tag_name() == "input" && input_type_of(*target) == "radio") {
        auto& memory = doc->radio_focus_memory;
        memory.erase(std::remove_if(memory.begin(), memory.end(),
            [&](const Element* e) { return same_radio_group(doc, e, target); }), memory.end());
        memory.push_back(target);
    }
    if (doc->open_select && doc->open_select != target)
        weva_document_open_select(doc, WEVA_ELEMENT_NONE);
    const Element* previous_focus = st.focused;
    if (previous_focus && is_text_field(*previous_focus)) {
        auto& saved = doc->field_selections[previous_focus];
        saved.caret = st.caret;
        saved.anchor = st.anchor;
        saved.value.assign(previous_focus->form_edit_value());
    }
    // A text field that is losing the focus commits what it holds, if what it
    // holds has moved. Nothing else can tell an edit from a visit.
    if (previous_focus && is_text_field(*previous_focus) && doc->user_edited_since_focus &&
        field_value(*previous_focus) != doc->value_at_focus) {
        doc->queue_event(WEVA_EVENT_CHANGE, previous_focus, 0, 0, 0);
    }
    doc->value_at_focus = target && is_text_field(*target) ? field_value(*target) : std::string();
    doc->user_edited_since_focus = false;
    st.caret = 0;
    st.text_scroll_x = 0;
    st.anchor = -1;
    if (target) {
        const auto saved = doc->field_selections.find(target);
        if (saved != doc->field_selections.end()) {
            st.caret = saved->second.caret;
            st.anchor = saved->second.anchor;
        }
    }
    st.caret_age = 0;
    doc->caret_follow = target != nullptr;
    const Element* previous = previous_focus;
    if (previous) doc->queue_event(WEVA_EVENT_BLUR, previous, 0, 0, 0);
    if (target) doc->queue_event(WEVA_EVENT_FOCUS, target, 0, 0, 0);
    const std::vector<const Element*> old_chain = st.focus_chain;
    st.focused = target;
    st.vertical_owner = nullptr;
    st.caret_downstream = false;
    // :focus-within is the ancestors; the element itself carries :focus.
    InteractionState::chain_of(target, &st.focus_chain);
    std::vector<const Element*> changed = old_chain;
    if (previous) changed.push_back(previous);
    // Always observable: unlike :hover, focus also moves the caret and the
    // scroll position, and a text field repaints on it whether or not a rule
    // ever says `:focus`.
    note_state_change(doc, changed, st.focus_chain, nullptr);
    // The focused element's own bits changed even when the chain did not.
    if (previous || target) {
        doc->typeahead.reset();
        doc->typeahead_target = nullptr;
        ++st.version_;
        doc->dom_touched = true;
        for (const Element* e : {previous, target}) {
            if (e && doc->touched.size() < 64) doc->touched.push_back(const_cast<Element*>(e));
            if (e && e->tag_name() == "select" && select_is_listbox(*e)) invalidate_list_row(doc, e);
        }
    }
    // A tab that lands off screen brings its target into view, or keyboard
    // navigation walks into a list and appears to go nowhere.
    // Focus may follow a dialog/DOM mutation before its next layout. Reveal
    // against the final geometry; settled keyboard navigation stays immediate.
    if (target) {
        if (geometry_pending) doc->focus_follow = target;
        else bring_box_into_view(doc, box_of(doc, target));
    } else doc->focus_follow = nullptr;
    return WEVA_OK;
}

int weva_document_scroll(weva_document_t doc, double x, double y, double dx, double dy) {
    if (!doc || (dx == 0 && dy == 0)) return 0;
    // A wheel over an open list moves the LIST. It sits above the document, so
    // scrolling the page under it would move the wrong thing.
    if (doc->open_select && !input_blocked(doc, doc->open_select) && select_row_at(doc, x, y) >= 0) {
        const BoxId box = box_of(doc, doc->open_select);
        const SelectListGeometry g =
            select_list_geometry(doc->tree, box, doc->ctx, *doc->open_select);
        const int last = std::max(0, g.count - g.rows);
        const int step = dy > 0 ? 1 : -1;
        const int to = std::clamp(doc->select_first_row + step * 3, 0, last);
        if (to == doc->select_first_row) return 0;
        doc->select_first_row = to;
        doc->pending = worst(doc->pending, Invalidation::Paint);
        return 1;
    }
    // Up from the point, not down from the root: the wheel belongs to the
    // innermost thing under it that can move. One that has hit its end passes
    // the wheel on, which is what makes a scrolled list inside a page stop
    // catching it once it is at the bottom.
    for (BoxId id = box_at_point(doc->tree, doc->root, x, y, &doc->ctx); id != kNoBox;
         id = doc->tree[id].parent) {
        const Box& b = doc->tree[id];
        if (b.element && input_blocked(doc, b.element)) break;
        if (!b.element || !clips_overflow(b)) continue;
        double mx = 0, my = 0;
        max_scroll(doc->tree, id, &mx, &my);
        // `hidden` clips and a script can still scroll it, but it is not a
        // scroller to the user: the wheel goes past it to whatever is.
        if (!scrollable_on_axis(b, true)) my = 0;
        if (!scrollable_on_axis(b, false)) mx = 0;
        const double cx = b.scroll_x, cy = b.scroll_y;
        const double nx = std::clamp(cx + dx, 0.0, mx), ny = std::clamp(cy + dy, 0.0, my);
        if (nx == cx && ny == cy) continue;   // no room this way: the next one up
        doc->scroll[b.element] = {nx, ny};
        doc->queue_event(WEVA_EVENT_SCROLL, b.element, nx, ny, 0);
        doc->pending = worst(doc->pending, Invalidation::Paint);
        return 1;
    }
    return 0;
}

weva_status weva_element_set_scroll(weva_document_t doc, weva_element_t element, double x,
                                    double y) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    const Element* e = doc->element_at(element);
    if (!e) return WEVA_ERR_NOT_FOUND;
    // Clamped by the update, which is the only thing that knows how far there
    // is to go -- and it may not have laid this element out yet.
    doc->scroll[e] = {std::max(0.0, x), std::max(0.0, y)};
    doc->queue_event(WEVA_EVENT_SCROLL, e, std::max(0.0, x), std::max(0.0, y), 0);
    doc->pending = worst(doc->pending, Invalidation::Paint);
    return WEVA_OK;
}

weva_status weva_element_scroll(weva_document_t doc, weva_element_t element, double* out_x,
                                double* out_y, double* out_max_x, double* out_max_y) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    const Element* e = doc->element_at(element);
    if (!e) return WEVA_ERR_NOT_FOUND;
    if (out_x) *out_x = 0;
    if (out_y) *out_y = 0;
    if (out_max_x) *out_max_x = 0;
    if (out_max_y) *out_max_y = 0;
    const BoxId i = box_of(doc, e);
    if (i != kNoBox) {
        const Box& b = doc->tree[i];
        if (out_x) *out_x = b.scroll_x;
        if (out_y) *out_y = b.scroll_y;
        if (clips_overflow(b)) max_scroll(doc->tree, i, out_max_x, out_max_y);
        return WEVA_OK;
    }
    return WEVA_ERR_NOT_FOUND;
}

weva_status weva_element_scroll_into_view(weva_document_t doc, weva_element_t element) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    const Element* e = doc->element_at(element);
    if (!e) return WEVA_ERR_NOT_FOUND;
    const BoxId id = box_of(doc, e);
    if (id == kNoBox) return WEVA_ERR_NOT_FOUND;
    bring_box_into_view(doc, id);
    return WEVA_OK;
}

namespace {

// Everything the document holds ABOUT an element, dropped: its style, its
// scroll offset, its place in the hover, press and focus state. All of it is
// keyed on the pointer, and the node dies with its last reference.
void forget_element(weva_document* doc, const Element* e) {
    doc->field_selections.erase(e);
    doc->dialog_return_values.erase(e);
    auto& close_order = doc->dialog_close_order;
    close_order.erase(std::remove(close_order.begin(), close_order.end(), e), close_order.end());
    doc->dialog_close_generations.erase(e);
    doc->dialog_focus_history.erase(std::remove_if(doc->dialog_focus_history.begin(), doc->dialog_focus_history.end(),
        [e](const auto& entry) { return entry.dialog == e; }), doc->dialog_focus_history.end());
    for (auto& entry : doc->dialog_focus_history) if (entry.previous_focus == e) entry.previous_focus = nullptr;
    if (doc->styles.state.vertical_owner == e) doc->styles.state.vertical_owner = nullptr;
    doc->element_handles.erase(e);
    ++doc->structure_version;
    if (doc->focus_follow == e) doc->focus_follow = nullptr;
    if (doc->text_drag == e) clear_text_drag(doc);
    if (doc->list_drag == e || doc->press_target == e) { doc->list_drag = nullptr; doc->list_scroll_armed = false; }
    doc->touched.erase(std::remove(doc->touched.begin(), doc->touched.end(), e), doc->touched.end());
    if (doc->typeahead_target == e) { doc->typeahead.reset(); doc->typeahead_target = nullptr; }
    doc->list_selections.erase(e);
    for (auto& entry : doc->list_selections) {
        if (entry.second.active == e || entry.second.anchor == e) {
            entry.second = {};
            invalidate_list_row(doc, entry.first);
        }
        auto& before = entry.second.before_drag;
        before.erase(std::remove(before.begin(), before.end(), e), before.end());
    }
    if (doc->list_follow == e) doc->list_follow = nullptr;
    doc->styles.forget(e);
    doc->scroll.erase(e);
    if (doc->press_target == e) doc->press_target = nullptr;
    if (doc->popover_press_target == e) { doc->popover_press_target = nullptr; doc->popover_press_active = false; }
    if (doc->space_press_target == e) doc->space_press_target = nullptr;
    auto& radio_memory = doc->radio_focus_memory;
    radio_memory.erase(std::remove(radio_memory.begin(), radio_memory.end(), e), radio_memory.end());
    if (doc->styles.state.focused == e) {
        doc->styles.state.composing = false;
        doc->styles.state.composition_from = doc->styles.state.composition_to = 0;
        doc->composition_before = {};
        doc->composition_value.clear();
        doc->styles.state.focused = nullptr;
        doc->styles.state.caret = 0;
        doc->styles.state.text_scroll_x = 0;
    }
    if (doc->caret_painted.element == e) doc->caret_painted = CaretState{};
    if (doc->scroll_drag.element == e) doc->scroll_drag = weva_document::ScrollDrag{};
    if (doc->open_select == e) {
        doc->set_open_select(nullptr);
        doc->highlighted_option = -1;
    }
    doc->binding_templates.erase(e);
    doc->binding_repeats.erase(e);
    doc->history.erase(e);
    const size_t popovers_before = doc->popovers.size();
    doc->light_dismiss_popovers.erase(e);
    doc->popover_parents.erase(e);
    for (auto& entry : doc->popover_parents) if (entry.second == e) entry.second = nullptr;
    doc->popover_previous_focus.erase(e);
    for (auto& entry : doc->popover_previous_focus) if (entry.second == e) entry.second = nullptr;
    doc->popovers.erase(std::remove(doc->popovers.begin(), doc->popovers.end(), e),
                        doc->popovers.end());
    if (doc->popovers.size() != popovers_before) ++doc->transient_version;
    if (doc->tooltip.host == e) {
        doc->tooltip.host = nullptr;
        doc->tooltip.dwell = 0;
    }
    if (doc->tooltip.shown == e) doc->tooltip.shown = nullptr;
    const auto drop = [e](std::vector<const Element*>* chain) {
        chain->erase(std::remove(chain->begin(), chain->end(), e), chain->end());
    };
    drop(&doc->styles.state.hover_chain);
    drop(&doc->styles.state.active_chain);
    drop(&doc->styles.state.focus_chain);
    // Tombstoned rather than erased: a handle is an INDEX, and erasing would
    // renumber every element after it under a host that is still holding
    // handles for the ones it did not touch.
    for (Element*& slot : doc->elements) {
        if (slot == e) slot = nullptr;
    }
}

void forget_subtree(weva_document* doc, const Element& e) {
    for (const Ref<Node>& c : e.children()) {
        if (c->node_type() == NodeType::Element) {
            forget_subtree(doc, static_cast<const Element&>(*c));
        }
    }
    forget_element(doc, &e);
}

// Parses a fragment and hands back the nodes to put in the document. The
// parser builds a whole document, html and body included, so the fragment is
// what ends up under the body it made.
std::vector<Ref<Node>> parse_fragment(weva_document* doc, const char* html, size_t length) {
    std::vector<Ref<Node>> out;
    if (!html && length > 0) return out;
    HtmlParseError err;
    ParseOptions opts;
    opts.strict = false;
    Ref<Document> parsed =
        parse_html(std::string_view(html ? html : "", length), &doc->symbols, opts, &err);
    if (!parsed) return out;
    // Components expand before the cascade sees them, exactly as they do on
    // load, so appended markup gets the expansion the same markup would have
    // got in the page source.
    expand_components(parsed.get());
    Node* body = nullptr;
    for (const Ref<Node>& top : parsed->children()) {
        if (top->node_type() != NodeType::Element) continue;
        const Element& e = static_cast<const Element&>(*top);
        if (e.tag_name() != "html") continue;
        for (const Ref<Node>& c : e.children()) {
            if (c->node_type() == NodeType::Element &&
                static_cast<const Element&>(*c).tag_name() == "body") {
                body = c.get();
            }
        }
    }
    Node* from = body ? body : static_cast<Node*>(parsed.get());
    // Copied out before detaching: removing a child mutates the vector the
    // loop would otherwise be walking.
    std::vector<Ref<Node>> taken(from->children().begin(), from->children().end());
    for (const Ref<Node>& c : taken) {
        from->remove_child(c.get());
        out.push_back(c);
    }
    return out;
}

// Indexes what was just added and marks the document structurally changed.
weva_element_t adopt(weva_document* doc, const std::vector<Ref<Node>>& added) {
    weva_element_t first = WEVA_ELEMENT_NONE;
    for (const Ref<Node>& c : added) {
        if (c->node_type() != NodeType::Element) continue;
        Element& e = static_cast<Element&>(const_cast<Node&>(*c));
        if (first == WEVA_ELEMENT_NONE) first = static_cast<weva_element_t>(doc->elements.size());
        doc->index_elements(e);
    }
    // A new element has no style yet, so the cascade has to reach it and boxes
    // have to be built for it. The scoped restyle is not the path: it starts
    // from an element that already has one.
    doc->pending = worst(doc->pending, Invalidation::Boxes);
    doc->touched.clear();
    doc->dom_touched = false;
    return first;
}

}   // namespace

weva_status weva_element_set_html(weva_document_t doc, weva_element_t element, const char* html,
                                  size_t length) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    Element* e = doc->element_at(element);
    if (!e) return WEVA_ERR_NOT_FOUND;
    const std::vector<Ref<Node>> old(e->children().begin(), e->children().end());
    for (const Ref<Node>& c : old) {
        if (c->node_type() == NodeType::Element) {
            forget_subtree(doc, static_cast<const Element&>(*c));
        }
        e->remove_child(c.get());
    }
    const std::vector<Ref<Node>> added = parse_fragment(doc, html, length);
    for (const Ref<Node>& c : added) e->append_child(c.get());
    adopt(doc, added);
    return WEVA_OK;
}

weva_element_t weva_element_append_html(weva_document_t doc, weva_element_t element,
                                        const char* html, size_t length) {
    if (!doc) return WEVA_ELEMENT_NONE;
    Element* e = doc->element_at(element);
    if (!e) return WEVA_ELEMENT_NONE;
    const std::vector<Ref<Node>> added = parse_fragment(doc, html, length);
    for (const Ref<Node>& c : added) e->append_child(c.get());
    return adopt(doc, added);
}

weva_status weva_element_remove(weva_document_t doc, weva_element_t element) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    Element* e = doc->element_at(element);
    if (!e) return WEVA_ERR_NOT_FOUND;
    Node* parent = e->parent();
    if (!parent) return WEVA_ERR_INVALID_ARGUMENT;   // the root is not removable
    // A reference held across the detach, so the subtree is still alive while
    // the caches pointing into it are emptied. `retain`, not the constructor:
    // that one ADOPTS a reference the caller owns, and this caller owns none,
    // so it released one too many and freed the node early.
    const Ref<Node> keep = Ref<Node>::retain(e);
    forget_subtree(doc, *e);
    parent->remove_child(e);
    doc->pending = worst(doc->pending, Invalidation::Boxes);
    doc->touched.clear();
    doc->dom_touched = false;
    return WEVA_OK;
}

size_t weva_document_query_all(weva_document_t doc, const char* selector, weva_element_t* out,
                               size_t capacity) {
    if (!doc || !doc->doc || !selector) return 0;
    CompiledSelector compiled;
    SelectorParseError err;
    if (!parse_selector(selector, &compiled, &err)) return 0;
    size_t found = 0;
    for (weva_element_t i : doc->document_order()) {
        const Element* e = doc->elements[i];
        if (!e || !selector_matches(compiled, *e, doc->styles.state)) continue;
        if (out && found < capacity) out[found] = static_cast<weva_element_t>(i);
        ++found;
    }
    return found;
}

weva_status weva_element_set_attribute(weva_document_t doc, weva_element_t element,
                                       const char* name, const char* value) {
    if (!doc || !name) return WEVA_ERR_INVALID_ARGUMENT;
    Element* e = doc->element_at(element);
    if (!e) return WEVA_ERR_NOT_FOUND;
    if (e->tag_name() == "dialog" && std::strcmp(name, "open") == 0 &&
        e->has_attribute("open") != (value != nullptr)) doc->dialog_close_generations.erase(e);
    const bool finish = e == doc->styles.state.focused && (std::strcmp(name, "readonly") == 0 ||
        std::strcmp(name, "disabled") == 0 || std::strcmp(name, "type") == 0);
    const bool disabling = value && (std::strcmp(name, "readonly") == 0 || std::strcmp(name, "disabled") == 0);
    const bool image_source_changed = e->tag_name() == "img" && std::strcmp(name,"src") == 0 &&
                                     e->get_attribute("src") != std::string_view(value ? value : "");
    const bool table_span_changed = is_table_span_attribute(name) &&
        e->get_attribute(name) != std::string_view(value ? value : "");
    if (finish && !disabling) weva_document_commit_composition(doc, nullptr);
    if (value) e->set_attribute(name, value);
    else e->remove_attribute(name);
    if (std::strcmp(name, "open") == 0) doc->sync_dialog_open(e);
    if (finish && disabling) weva_document_commit_composition(doc, nullptr);
    if (image_source_changed || table_span_changed) {
        auto& inputs = doc->styles.pending_content_inputs;
        if (std::find(inputs.begin(),inputs.end(),e) == inputs.end()) inputs.push_back(e);
    }
    // What it reaches is the cascade's business; that it must run is this
    // function's. The selector match cache is keyed on element shape, so the
    // change lands on a different entry without being invalidated here.
    doc->dom_touched = true;
    Element* scope = e;
    // A filtered sibling rank can move in either direction after a src swap.
    if (image_source_changed && doc->styles.engine.has_sibling_selectors() &&
        e->parent() && e->parent()->is_element()) scope = static_cast<Element*>(e->parent());
    if (doc->touched.size() < 64) doc->touched.push_back(scope);
    return WEVA_OK;
}

weva_status weva_element_set_text(weva_document_t doc, weva_element_t element,
                                  const char* text) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    Element* e = doc->element_at(element);
    if (!e) return WEVA_ERR_NOT_FOUND;
    if (!replace_text(*e, text ? text : "")) {
        // Setting textarea default text replaces its selection even when the
        // text is equal. Keep ordinary HUD text no-ops allocation-free.
        if (e->tag_name() == "textarea") form_children_changed(*e);
        return WEVA_OK;
    }
    // No elements were added or removed. Re-evaluate selector scopes (notably
    // :empty and any :has dependencies) and reflow the text owner's subtree.
    // The existing incremental layout proof builds fresh inline/line boxes
    // and falls back to a full rebuild if size, baseline or intrinsic exports
    // change. Keep unrelated pending structure/viewport/font work intact.
    auto& inputs = doc->styles.pending_content_inputs;
    if (std::find(inputs.begin(), inputs.end(), e) == inputs.end()) inputs.push_back(e);
    doc->dom_touched = true;
    Element* scope = e;
    // :nth-last-child(... of :empty) can reach preceding siblings too.
    // With sibling-dependent sheets, text restyles the containing family.
    if (doc->styles.engine.has_sibling_selectors() && e->parent() && e->parent()->is_element())
        scope = static_cast<Element*>(e->parent());
    if (doc->touched.size() < 64 && std::find(doc->touched.begin(), doc->touched.end(), scope) == doc->touched.end())
        doc->touched.push_back(scope);
    return WEVA_OK;
}

void weva_document_set_popover_request_events(weva_document_t doc, int enabled) {
    if (doc) doc->popover_request_events = enabled != 0;
}

weva_status weva_element_request_show_popover(weva_document_t doc, weva_element_t element) {
    return request_popover_open(doc, element);
}

weva_status weva_element_request_hide_popover(weva_document_t doc, weva_element_t element) {
    return request_popover_hide(doc, element);
}

weva_status weva_element_request_toggle_popover(weva_document_t doc, weva_element_t element) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    Element* e = doc->element_at(element);
    if (!e || !e->has_attribute("popover")) return WEVA_ERR_NOT_FOUND;
    if (e->is_popover_open()) return weva_element_request_hide_popover(doc, element);
    return request_popover_open(doc, element);
}

weva_status weva_element_show_popover(weva_document_t doc, weva_element_t element) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    Element* e = doc->element_at(element);
    if (!e || !e->has_attribute("popover")) return WEVA_ERR_NOT_FOUND;
    if (!e->is_popover_open() && e->is_modal()) return WEVA_ERR_INVALID_STATE;
    popover_show(doc, *e);
    return WEVA_OK;
}

weva_status weva_element_hide_popover(weva_document_t doc, weva_element_t element) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    Element* e = doc->element_at(element);
    if (!e || !e->has_attribute("popover")) return WEVA_ERR_NOT_FOUND;
    popover_hide(doc, *e);
    return WEVA_OK;
}

weva_status weva_element_toggle_popover(weva_document_t doc, weva_element_t element) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    Element* e = doc->element_at(element);
    if (!e || !e->has_attribute("popover")) return WEVA_ERR_NOT_FOUND;
    if (!e->is_popover_open() && e->is_modal()) return WEVA_ERR_INVALID_STATE;
    if (e->is_popover_open()) popover_hide(doc, *e);
    else popover_show(doc, *e);
    return WEVA_OK;
}

namespace {
const Element* dialog_focus_candidate(weva_document* doc, const Node& parent, bool autofocus) {
    for (const auto& child : parent.children()) {
        if (!child->is_element()) continue;
        const auto* e = static_cast<const Element*>(child.get());
        int order = 0;
        // Opening and author mutations may not have reached retained styles yet.
        // Check the current cascade, including inertness, before choosing a delegate.
        if ((!autofocus || e->has_attribute("autofocus")) &&
            focus_order_of(*e, &order, true) && !focus_unavailable_now(doc, *e)) return e;
        if (const Element* found = dialog_focus_candidate(doc, *e, autofocus)) return found;
    }
    return nullptr;
}
void focus_popover(weva_document* doc, const Element& e) {
    const Element* focus = e.has_attribute("autofocus") ? &e : dialog_focus_candidate(doc, e, true);
    if (!focus && e.tag_name() == "dialog") {
        focus = dialog_focus_candidate(doc, e, false);
        if (!focus) focus = &e;
    }
    if (focus) weva_document_set_focus(doc, doc->handle_of(focus));
}
}

weva_status weva_element_show_dialog(weva_document_t doc, weva_element_t element, int modal) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    Element* e = doc->element_at(element);
    if (!e || e->tag_name() != "dialog") return WEVA_ERR_NOT_FOUND;
    if (e->has_attribute("open"))
        return e->is_modal() == (modal != 0) ? WEVA_OK : WEVA_ERR_INVALID_STATE;
    if (modal && e->is_popover_open()) return WEVA_ERR_INVALID_STATE;
    // Showing a dialog dismisses unrelated auto/hint popovers. Ancestors
    // and manual popovers remain; all closures use the normal event path.
    for (size_t i = doc->popovers.size(); i-- > 0;) {
        Element* popup = const_cast<Element*>(doc->popovers[i]);
        if (popover_is_auto(*popup) && (popup == e || !is_within(e, popup))) popover_hide(doc, *popup);
    }
    const Element* previous_focus = doc->styles.state.focused;
    e->set_attribute("open", "");
    e->set_modal(modal != 0);
    doc->sync_dialog_open(e);
    doc->dialog_focus_history.erase(std::remove_if(doc->dialog_focus_history.begin(), doc->dialog_focus_history.end(),
        [e](const auto& entry) { return entry.dialog == e; }), doc->dialog_focus_history.end());
    doc->dialog_focus_history.push_back({e, previous_focus});
    if (modal) {
        weva_document_clear_pointer(doc);
        if (doc->open_select) weva_document_open_select(doc, WEVA_ELEMENT_NONE);
    }
    const Element* focus = e->has_attribute("autofocus") ? e : dialog_focus_candidate(doc, *e, true);
    if (!focus) focus = dialog_focus_candidate(doc, *e, false);
    weva_document_set_focus(doc, doc->handle_of(focus ? focus : e));
    // Keep the legacy data marker as a readable mirror. Live modality above
    // controls the top layer and cannot be changed by author data attributes.
    if (modal) e->set_attribute("data-modal", "");
    else e->remove_attribute("data-modal");
    // The backdrop is a BOX, so this is a box-level change, not a repaint.
    note_box_input(doc, e);
    doc->queue_event(WEVA_EVENT_TOGGLE, e, 0, 0, 0, 0, "open");
    return WEVA_OK;
}

weva_status weva_element_request_close_dialog(weva_document_t doc, weva_element_t element) {
    return weva_element_request_close_dialog_with_value(doc, element, nullptr);
}

weva_status weva_element_request_close_dialog_with_value(weva_document_t doc, weva_element_t element,
                                                        const char* value) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    Element* e = doc->element_at(element);
    if (!e || e->tag_name() != "dialog") return WEVA_ERR_NOT_FOUND;
    if (!e->has_attribute("open")) return WEVA_OK;
    auto it = doc->dialog_close_generations.find(e);
    if (it == doc->dialog_close_generations.end())
        it = doc->dialog_close_generations.emplace(e, ++doc->next_close_generation).first;
    if (!doc->queue_event(WEVA_EVENT_CANCEL, e, 0, 0, 0)) return WEVA_ERR_INVALID_STATE;
    doc->events.back().close_target = Ref<Element>::retain(e);
    doc->events.back().close_generation = it->second;
    doc->events.back().has_close_value = value != nullptr;
    if (value) doc->events.back().close_value = value;
    return WEVA_OK;
}

size_t weva_element_dialog_return_value(weva_document_t doc, weva_element_t element,
                                      char* buffer, size_t capacity) {
    std::string_view value;
    if (doc) {
        const auto* e = doc->element_at(element);
        const auto it = doc->dialog_return_values.find(e);
        if (e && e->tag_name() == "dialog" && it != doc->dialog_return_values.end()) value = it->second;
    }
    if (buffer && capacity) {
        const size_t n = text_boundary(value, static_cast<int>(std::min(value.size(), capacity - 1)));
        if (n) std::memcpy(buffer, value.data(), n);
        buffer[n] = 0;
    }
    return value.size();
}

weva_status weva_element_set_dialog_return_value(weva_document_t doc, weva_element_t element,
                                                const char* value) {
    if (!doc || !value) return WEVA_ERR_INVALID_ARGUMENT;
    const auto* e = doc->element_at(element);
    if (!e || e->tag_name() != "dialog") return WEVA_ERR_NOT_FOUND;
    if (!*value) doc->dialog_return_values.erase(e);
    else doc->dialog_return_values[e] = value;
    return WEVA_OK;
}

weva_status weva_element_close_dialog(weva_document_t doc, weva_element_t element) {
    return weva_element_close_dialog_with_value(doc, element, nullptr);
}

weva_status weva_element_close_dialog_with_value(weva_document_t doc, weva_element_t element,
                                                const char* value) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    Element* e = doc->element_at(element);
    if (!e || e->tag_name() != "dialog") return WEVA_ERR_NOT_FOUND;
    const bool was_open = e->has_attribute("open");
    if (!was_open) return WEVA_OK;
    if (value) weva_element_set_dialog_return_value(doc, element, value);
    doc->dialog_close_generations.erase(e);
    const Element* restore = nullptr;
    const bool restore_focus = e->is_modal() || is_within(doc->styles.state.focused, e);
    for (const auto& entry : doc->dialog_focus_history) if (entry.dialog == e) restore = entry.previous_focus;
    doc->dialog_focus_history.erase(std::remove_if(doc->dialog_focus_history.begin(), doc->dialog_focus_history.end(),
        [e](const auto& entry) { return entry.dialog == e; }), doc->dialog_focus_history.end());
    e->set_modal(false);
    e->remove_attribute("open");
    doc->sync_dialog_open(e);
    if (restore_focus) weva_document_set_focus(doc, doc->handle_of(restore));
    doc->check_focus_after_dialog_close = true;
    e->remove_attribute("data-modal");
    note_box_input(doc, e);
    if (was_open) {
        doc->queue_event(WEVA_EVENT_TOGGLE, e, 0, 0, 0);
        doc->queue_event(WEVA_EVENT_CLOSE, e, 0, 0, 0);
    }
    return WEVA_OK;
}

weva_status weva_document_set_base_path(weva_document_t doc, const char* path) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    const uint64_t before = doc->images.content_version();
    doc->images.set_base_path(path ? path : "");
    if (before != doc->images.content_version()) doc->pending = Invalidation::Boxes;
    return WEVA_OK;
}

size_t weva_document_css_diagnostics(weva_document_t doc, char* buffer, size_t capacity) {
    if (buffer && capacity) buffer[0] = '\0';
    if (!doc) return 0;
    std::string text;
    for (const auto& name : doc->styles.engine.unsupported_at_rules()) {
        if (!text.empty()) text += '\n';
        text += "Ignored @" + name + ": unsupported stylesheet rule.";
    }
    if (buffer && capacity) {
        const size_t n = std::min(text.size(), capacity - 1);
        if (n) std::memcpy(buffer, text.data(), n);
        buffer[n] = '\0';
    }
    return text.size();
}

size_t weva_document_font_faces(weva_document_t doc, char* buffer, size_t capacity) {
    if (buffer && capacity) buffer[0] = '\0';
    if (!doc) return 0;
    std::string text;
    for (const auto& face : doc->styles.engine.font_faces()) {
        if (!text.empty()) text += '\n';
        text += face.family + '\t' + doc->images.resolve(face.src) + '\t' + face.weight + '\t' + face.style;
    }
    if (buffer && capacity) {
        const size_t n = std::min(text.size(), capacity - 1);
        if (n) std::memcpy(buffer, text.data(), n);
        buffer[n] = '\0';
    }
    return text.size();
}

size_t weva_document_missing_assets(weva_document_t doc, char* buffer, size_t capacity) {
    if (buffer && capacity > 0) buffer[0] = '\0';
    if (!doc) return 0;
    const std::vector<std::string> missing = doc->images.missing();
    if (buffer && capacity > 0) {
        std::string joined;
        for (const std::string& m : missing) {
            if (!joined.empty()) joined += '\n';
            joined += m;
        }
        const size_t n = joined.size() < capacity - 1 ? joined.size() : capacity - 1;
        if (n > 0) std::memcpy(buffer, joined.data(), n);
        buffer[n] = '\0';
    }
    return missing.size();
}

weva_status weva_document_set_asset_reader(weva_document_t doc, weva_asset_reader reader,
                                           void* user_data) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    // New resource inputs may change intrinsic image sizes as well as pixels.
    // Rebuild at the next update; published texture views remain valid until it.
    doc->pending = Invalidation::Boxes;
    if (!reader) {
        doc->images.set_reader({});
        return WEVA_OK;
    }
    doc->images.set_reader([reader, user_data](const std::string& path,
                                               std::vector<uint8_t>* out) {
        // Two calls: the size, then the bytes. A host that cannot answer the
        // first returns 0 and the image is a miss, cached as one.
        const size_t size = reader(user_data, path.c_str(), nullptr, 0);
        if (size == 0) return false;
        out->resize(size);
        return reader(user_data, path.c_str(), out->data(), out->size()) == size;
    });
    return WEVA_OK;
}

namespace {

// An inline style's declarations, split at TOP LEVEL: a semicolon inside
// url(...) or inside a quoted string is part of a value, not a separator, and
// cutting there would truncate the value and leave a fragment behind as a
// declaration of its own.
std::vector<std::string> split_declarations(std::string_view style) {
    std::vector<std::string> out;
    int depth = 0;
    char quote = 0;
    size_t start = 0;
    for (size_t i = 0; i < style.size(); ++i) {
        const char c = style[i];
        if (quote) {
            if (c == '\\' && i + 1 < style.size()) ++i;
            else if (c == quote) quote = 0;
            continue;
        }
        if (c == '"' || c == '\'') { quote = c; continue; }
        if (c == '(') ++depth;
        else if (c == ')') { if (depth > 0) --depth; }
        else if (c == ';' && depth == 0) {
            out.emplace_back(style.substr(start, i - start));
            start = i + 1;
        }
    }
    out.emplace_back(style.substr(start));
    return out;
}

std::string_view trim_decl(std::string_view s) {
    const auto ws = [](char c) { return c == ' ' || c == '\t' || c == '\n' || c == '\r'; };
    while (!s.empty() && ws(s.front())) s.remove_prefix(1);
    while (!s.empty() && ws(s.back())) s.remove_suffix(1);
    return s;
}

// The property a declaration sets, lowercased, or empty when it is not one.
std::string declaration_property(std::string_view decl) {
    const size_t colon = decl.find(':');
    if (colon == std::string_view::npos) return {};
    std::string name(trim_decl(decl.substr(0, colon)));
    for (char& c : name) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return name;
}

} // namespace

weva_status weva_element_set_style(weva_document_t doc, weva_element_t element,
                                   const char* property, const char* value) {
    if (!doc || !property || !*property) return WEVA_ERR_INVALID_ARGUMENT;
    Element* e = doc->element_at(element);
    if (!e) return WEVA_ERR_NOT_FOUND;

    std::string wanted(property);
    for (char& c : wanted) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    const bool removing = !value || !*value;

    std::string rebuilt;
    bool replaced = false;
    for (const std::string& decl : split_declarations(e->get_attribute("style"))) {
        const std::string_view trimmed = trim_decl(decl);
        if (trimmed.empty()) continue;
        if (declaration_property(trimmed) == wanted) {
            // Replaced IN PLACE rather than removed and appended, so a script
            // setting a property twice does not shuffle the declaration order
            // under it -- and order decides the winner between two that set
            // the same longhand through different shorthands.
            replaced = true;
            if (removing) continue;
            rebuilt += wanted;
            rebuilt += ": ";
            rebuilt += value;
            rebuilt += "; ";
            continue;
        }
        rebuilt.append(trimmed);
        rebuilt += "; ";
    }
    if (!replaced && !removing) {
        rebuilt += wanted;
        rebuilt += ": ";
        rebuilt += value;
        rebuilt += "; ";
    }
    while (!rebuilt.empty() && (rebuilt.back() == ' ' || rebuilt.back() == ';')) rebuilt.pop_back();

    // Compare the final serialized input: this preserves declaration order,
    // duplicate handling and normalization while skipping an unchanged write.
    if (rebuilt.empty() ? !e->has_attribute("style") : rebuilt == e->get_attribute("style"))
        return WEVA_OK;
    if (rebuilt.empty()) e->remove_attribute("style");
    else e->set_attribute("style", rebuilt);
    // The same invalidation an attribute write does: the cascade must re-run,
    // and the selector match cache is keyed on element shape so the change
    // lands on a different entry without being invalidated here.
    doc->dom_touched = true;
    if (doc->touched.size() < 64) doc->touched.push_back(e);
    return WEVA_OK;
}

size_t weva_element_style(weva_document_t doc, weva_element_t element, const char* property,
                          char* buffer, size_t capacity) {
    if (buffer && capacity > 0) buffer[0] = '\0';
    if (!doc || !property) return 0;
    const Element* e = doc->element_at(element);
    if (!e) return 0;
    std::string wanted(property);
    for (char& c : wanted) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    for (const std::string& decl : split_declarations(e->get_attribute("style"))) {
        const std::string_view trimmed = trim_decl(decl);
        if (declaration_property(trimmed) != wanted) continue;
        const std::string_view v = trim_decl(trimmed.substr(trimmed.find(':') + 1));
        if (buffer && capacity > 0) {
            const size_t n = v.size() < capacity - 1 ? v.size() : capacity - 1;
            if (n > 0) std::memcpy(buffer, v.data(), n);
            buffer[n] = '\0';
        }
        return v.size();
    }
    return 0;
}

size_t weva_element_computed_style(weva_document_t doc, weva_element_t element,
                                   const char* property, char* buffer, size_t capacity) {
    if (buffer && capacity > 0) buffer[0] = '\0';
    if (!doc || !property) return 0;
    const Element* e = doc->element_at(element);
    if (!e) return 0;
    const int id = CssPropertyRegistry::instance().id_of(property);
    if (id < 0) return 0;
    const ComputedStyle* style = doc->styles.style_of(*e);
    if (!style) return 0;
    // get() resolves through the inherit chain and then the initial-value
    // table, so an element that never set the property still answers with the
    // value it is actually using -- which is what "computed" means and what a
    // script asking the question wants.
    const std::string_view value = style->get(id);
    if (buffer && capacity > 0) {
        const size_t n = value.size() < capacity - 1 ? value.size() : capacity - 1;
        if (n > 0) std::memcpy(buffer, value.data(), n);
        buffer[n] = '\0';
    }
    return value.size();
}

size_t weva_element_model_path(weva_document_t doc, weva_element_t element, const char* path,
                               char* buffer, size_t capacity) {
    std::string resolved(path ? path : "");
    const Element* e = doc ? doc->element_at(element) : nullptr;

    // Outward from the element. Each repeated row on the way up may own the
    // alias the path currently starts with, and rewriting innermost-first is
    // what makes a nested repeat unwind: once `step.Done` has become
    // `quest.Steps.2.Done`, the row above it owns `quest`.
    for (const Node* n = e; n; n = n->parent()) {
        if (n->node_type() != NodeType::Element) continue;
        const Element& row = static_cast<const Element&>(*n);
        if (!row.has_attribute("data-weva-row")) continue;

        // `data-weva-row` holds the `data-each` the row was made from.
        const std::string each(row.get_attribute("data-weva-row"));
        const std::size_t as = each.find(" as ");
        if (as == std::string::npos) continue;
        std::string list = each.substr(0, as);
        std::string alias = each.substr(as + 4);
        const auto trim_ws = [](std::string& v) {
            while (!v.empty() && std::isspace(static_cast<unsigned char>(v.front()))) v.erase(v.begin());
            while (!v.empty() && std::isspace(static_cast<unsigned char>(v.back()))) v.pop_back();
        };
        trim_ws(list);
        trim_ws(alias);
        if (alias.empty() || list.empty()) continue;

        // Only when the path actually names this alias. `Player.Name` inside a
        // quest row is a global path the author meant globally.
        std::string rest;
        if (resolved == alias) {
            rest.clear();
        } else if (resolved.size() > alias.size() && resolved.compare(0, alias.size(), alias) == 0 &&
                   resolved[alias.size()] == '.') {
            rest = resolved.substr(alias.size());
        } else {
            continue;
        }
        const std::string index(row.get_attribute("data-weva-index"));
        resolved = list + "." + (index.empty() ? std::string("0") : index) + rest;
    }

    if (buffer && capacity > 0) {
        const size_t n = resolved.size() < capacity - 1 ? resolved.size() : capacity - 1;
        if (n > 0) std::memcpy(buffer, resolved.data(), n);
        buffer[n] = '\0';
    }
    return resolved.size();
}

int weva_element_row(weva_document_t doc, weva_element_t element, int* out_index,
                     char* key_buffer, size_t key_capacity) {
    if (key_buffer && key_capacity > 0) key_buffer[0] = '\0';
    if (!doc) return 0;
    const Element* e = doc->element_at(element);
    for (const Node* n = e; n; n = n->parent()) {
        if (n->node_type() != NodeType::Element) continue;
        const Element& candidate = static_cast<const Element&>(*n);
        if (!candidate.has_attribute("data-weva-row")) continue;
        if (out_index) {
            const std::string raw(candidate.get_attribute("data-weva-index"));
            *out_index = raw.empty() ? 0 : std::atoi(raw.c_str());
        }
        const std::string_view key = candidate.get_attribute("data-weva-key");
        if (key_buffer && key_capacity > 0) {
            const size_t n_copy = key.size() < key_capacity - 1 ? key.size() : key_capacity - 1;
            if (n_copy > 0) std::memcpy(key_buffer, key.data(), n_copy);
            key_buffer[n_copy] = '\0';
        }
        return 1;
    }
    return 0;
}

int weva_element_has_attribute(weva_document_t doc, weva_element_t element, const char* name) {
    if (!doc || !name) return 0;
    const Element* e = doc->element_at(element);
    return e && e->has_attribute(name) ? 1 : 0;
}

size_t weva_element_tag_name(weva_document_t doc, weva_element_t element, char* buffer, size_t capacity) {
    if (buffer && capacity > 0) buffer[0] = '\0';
    if (!doc) return 0;
    const Element* e = doc->element_at(element);
    if (!e) return 0;
    const std::string& value = e->tag_name();
    if (buffer && capacity > 0) {
        const size_t n = value.size() < capacity - 1 ? value.size() : capacity - 1;
        if (n > 0) std::memcpy(buffer, value.data(), n);
        buffer[n] = '\0';
    }
    return value.size();
}

size_t weva_element_attribute(weva_document_t doc, weva_element_t element, const char* name,
                              char* buffer, size_t capacity) {
    if (!doc || !name) return 0;
    const Element* e = doc->element_at(element);
    if (!e) return 0;
    const std::string_view value = e->get_attribute(name);
    if (buffer && capacity > 0) {
        const size_t n = value.size() < capacity - 1 ? value.size() : capacity - 1;
        // Guarded: an absent attribute is an empty string_view whose data() is
        // null, and memcpy from null is undefined even for zero bytes.
        if (n > 0) std::memcpy(buffer, value.data(), n);
        buffer[n] = '\0';
    }
    return value.size();
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

void weva_document_set_font_leading_rounding(weva_document_t doc, int rounds) {
    if (!doc) return;
    doc->font_leading_rounding = rounds != 0;
    if (doc->host_font) doc->host_font->set_rounds_leading(doc->font_leading_rounding);
    // Line boxes were built with the old rule: relayout on the next update.
    doc->pending = Invalidation::Boxes;
}

size_t weva_document_layout_dump(weva_document_t doc, const char* source, char* buffer,
                                 size_t capacity) {
    if (buffer && capacity) buffer[0] = '\0';
    if (!doc) return 0;
    std::vector<ElementRect> boxes;
    std::set<const Element*> seen;
    if (doc->tree.valid(doc->root)) collect_layout_dump(doc->tree, doc->root, 0, 0, 0, &boxes, &seen);
    const std::string text = layout_dump_json(source ? source : "", doc->config.viewport_width,
                                              doc->config.viewport_height, boxes);
    if (buffer && capacity) {
        const size_t n = std::min(text.size(), capacity - 1);
        if (n) std::memcpy(buffer, text.data(), n);
        buffer[n] = '\0';
    }
    return text.size();
}

namespace {
size_t write_text_out(const std::string& text, char* buffer, size_t capacity) {
    if (buffer && capacity) {
        const size_t n = std::min(text.size(), capacity - 1);
        if (n) std::memcpy(buffer, text.data(), n);
        buffer[n] = '\0';
    }
    return text.size();
}
}  // namespace

weva_element_t weva_element_parent(weva_document_t doc, weva_element_t element) {
    if (!doc) return WEVA_ELEMENT_NONE;
    const Element* e = doc->element_at(element);
    if (!e) return WEVA_ELEMENT_NONE;
    const Node* parent = e->parent();
    if (!parent || parent->node_type() != NodeType::Element) return WEVA_ELEMENT_NONE;
    return doc->handle_of(static_cast<const Element*>(parent));
}

size_t weva_element_children(weva_document_t doc, weva_element_t element, weva_element_t* out,
                             size_t capacity) {
    if (!doc) return 0;
    const Element* e = doc->element_at(element);
    if (!e) return 0;
    size_t count = 0;
    for (const Ref<Node>& child : e->children()) {
        if (child->node_type() != NodeType::Element) continue;
        const weva_element_t handle = doc->handle_of(static_cast<const Element*>(child.get()));
        if (handle == WEVA_ELEMENT_NONE) continue;
        if (out && count < capacity) out[count] = handle;
        ++count;
    }
    return count;
}

weva_status weva_element_box_model(weva_document_t doc, weva_element_t element, double* out) {
    if (!doc || !out) return WEVA_ERR_INVALID_ARGUMENT;
    const Element* e = doc->element_at(element);
    if (!e) return WEVA_ERR_NOT_FOUND;
    const BoxId i = box_of(doc, e);
    if (i == kNoBox) return WEVA_ERR_NOT_FOUND;
    const Box& b = doc->tree[i];
    double ax = 0, ay = 0;
    visual_position(doc->tree, i, &ax, &ay);
    out[0] = b.margin_top; out[1] = b.margin_right; out[2] = b.margin_bottom; out[3] = b.margin_left;
    out[4] = b.border_top; out[5] = b.border_right; out[6] = b.border_bottom; out[7] = b.border_left;
    out[8] = b.padding_top; out[9] = b.padding_right; out[10] = b.padding_bottom; out[11] = b.padding_left;
    out[12] = ax + b.border_left + b.padding_left;
    out[13] = ay + b.border_top + b.padding_top;
    out[14] = b.width - b.border_left - b.border_right - b.padding_left - b.padding_right;
    out[15] = b.height - b.border_top - b.border_bottom - b.padding_top - b.padding_bottom;
    return WEVA_OK;
}

size_t weva_element_matched_rules(weva_document_t doc, weva_element_t element, char* buffer,
                                  size_t capacity) {
    if (buffer && capacity) buffer[0] = '\0';
    if (!doc) return 0;
    const Element* e = doc->element_at(element);
    if (!e) return 0;
    struct Line {
        std::string origin, layer, specificity, selector, property, value;
        int source = 0;
        bool is_inline = false, important = false;
    };
    std::vector<Line> lines;
    const std::vector<MatchedDeclaration>& matches =
        doc->styles.engine.collect_matches(*e, doc->styles.state);
    for (const MatchedDeclaration& m : matches) {
        if (!m.declaration) continue;
        Line l;
        l.origin = m.origin == DeclarationOrigin::UserAgent ? "ua" : m.origin == DeclarationOrigin::User ? "user" : "author";
        if (m.layer_ordinal != kUnlayeredOrdinal) l.layer = std::to_string(m.layer_ordinal);
        l.specificity = std::to_string(m.specificity.a) + "," + std::to_string(m.specificity.b) + "," +
                        std::to_string(m.specificity.c);
        l.selector = m.selector_text;
        l.property = m.declaration->property;
        l.value = m.declaration->value_text;
        l.source = m.source_index;
        l.is_inline = m.is_inline;
        l.important = m.declaration->important;
        lines.push_back(std::move(l));
    }
    // The style attribute is applied by the cascade after the sheets, not
    // collected with them: normal inline declarations outrank every normal
    // sheet declaration, important ones outrank important sheet ones.
    // Shorthands expand the way the cascade expands them.
    std::vector<Line> inline_lines;
    if (e->has_attribute("style")) {
        std::vector<Declaration> decls;
        CssParseError perr;
        if (parse_inline_declarations(e->get_attribute("style"), /*strict=*/false, &decls, &perr)) {
            std::vector<ShorthandLonghand> longhands;
            for (const Declaration& d : decls) {
                longhands.clear();
                if (expand_shorthand(d.property, d.value_text, &longhands)) {
                    for (const ShorthandLonghand& lh : longhands) {
                        inline_lines.push_back(Line{"author", "", "", "", std::string(lh.property), std::string(lh.value), -1, true, d.important});
                    }
                } else {
                    inline_lines.push_back(Line{"author", "", "", "", d.property, d.value_text, -1, true, d.important});
                }
            }
        }
    }
    // Final order, last wins: normal sheet, normal inline, important sheet, important inline.
    std::vector<Line> ordered;
    for (const Line& l : lines) if (!l.important) ordered.push_back(l);
    for (const Line& l : inline_lines) if (!l.important) ordered.push_back(l);
    for (const Line& l : lines) if (l.important) ordered.push_back(l);
    for (const Line& l : inline_lines) if (l.important) ordered.push_back(l);
    std::vector<bool> applied(ordered.size(), false);
    std::set<std::string_view> seen;
    for (size_t i = ordered.size(); i-- > 0;) {
        if (seen.insert(ordered[i].property).second) applied[i] = true;
    }
    std::string text;
    for (size_t i = 0; i < ordered.size(); ++i) {
        const Line& l = ordered[i];
        if (!text.empty()) text += '\n';
        text += l.origin + '\t' + l.layer + '\t' + l.specificity + '\t' + std::to_string(l.source) + '\t';
        text += l.is_inline ? '1' : '0';
        text += '\t' + l.selector + '\t' + l.property + '\t' + l.value + '\t';
        text += l.important ? '1' : '0';
        text += '\t';
        text += applied[i] ? '1' : '0';
    }
    return write_text_out(text, buffer, capacity);
}

size_t weva_element_computed_style_all(weva_document_t doc, weva_element_t element, char* buffer,
                                       size_t capacity) {
    if (buffer && capacity) buffer[0] = '\0';
    if (!doc) return 0;
    const Element* e = doc->element_at(element);
    if (!e) return 0;
    const ComputedStyle* style = doc->styles.style_of(*e);
    if (!style) return 0;
    const CssPropertyRegistry& registry = CssPropertyRegistry::instance();
    std::string text;
    // Every registered property, resolved the way computed_style resolves one
    // (through inheritance and the initial-value table), in registry order.
    for (int id = 0; registry.by_id(id) != nullptr; ++id) {
        const std::string_view name = registry.name_of(id);
        if (name.empty()) continue;
        if (!text.empty()) text += '\n';
        text += std::string(name);
        text += '\t';
        text += std::string(style->get(id));
    }
    // Custom properties inherit: the nearest definition up the chain wins.
    std::map<std::string, std::string, std::less<>> customs;
    for (const ComputedStyle* s = style; s; s = s->inherit_parent()) {
        for (const auto& custom : s->custom_properties()) customs.emplace(custom.first, custom.second);
    }
    for (const auto& custom : customs) {
        if (!text.empty()) text += '\n';
        text += custom.first + '\t' + custom.second;
    }
    return write_text_out(text, buffer, capacity);
}

} // extern "C"
