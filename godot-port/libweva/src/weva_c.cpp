#include "weva_c.h"

#include "weva/components.h"
#include "weva/block_layout.h"
#include "weva/box_builder.h"
#include "weva/animation.h"
#include "weva/cascade.h"
#include "weva/font_interface.h"
#include "weva/font_metrics.h"
#include "weva/glyph_atlas.h"
#include "weva/tessellate.h"
#include "weva/binding.h"
#include "weva/hit_test.h"
#include "weva/html.h"
#include "weva/invalidation.h"
#include "weva/keyframes.h"
#include "weva/paint.h"
#include "weva/positioning.h"
#include "weva/scrollbar.h"
#include "weva/text_classes.h"
#include "weva/selector.h"
#include "weva/user_agent_stylesheet.h"

#include <algorithm>
#include <cctype>
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
        int32_t kind = WEVA_DRAW_GEOMETRY;
        BackdropEffect backdrop;
        RoundedRect rounded_rect;
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
        draws.push_back(std::move(d));
    }

    void render_rounded_rect(const RoundedRect& shape, const std::vector<Vertex>& v,
                             const std::vector<uint32_t>& i) override {
        // The tessellation still travels, so a host that does not know this
        // kind uploads it and draws the same shape.
        Draw d;
        d.kind = WEVA_DRAW_ROUNDED_RECT;
        d.rounded_rect = shape;
        d.vertices = v;
        d.indices = i;
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
    void set_scissor(const Recti* r) override {
        if (r) scissor_ = *r;
        else scissor_.reset();
    }

    std::vector<Draw> draws;
    std::map<uint64_t, std::pair<std::vector<uint8_t>, Vec2i>> textures;

private:
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
    std::string t(e.get_attribute("type"));
    for (char& c : t) c = static_cast<char>(c >= 'A' && c <= 'Z' ? c - 'A' + 'a' : c);
    if (e.tag_name() != "input") return std::string(e.tag_name());
    return t.empty() ? "text" : t;
}

// An element's text content, as one string. A <textarea> keeps what it holds
// HERE rather than in a `value` attribute -- the markup's content IS the
// value, as it is in a browser -- so editing one edits its text.
std::string text_content_of(const Element& e) {
    std::string out;
    for (const Ref<Node>& c : e.children()) {
        if (c->node_type() == NodeType::Text) out += static_cast<const TextNode&>(*c).data();
    }
    return out;
}

// Replaces an element's text, leaving its other children where they are. The
// new text goes back WHERE THE OLD TEXT WAS, not at the end: for
// `<div>label<span>*</span></div>` appending would put the label after the
// icon and quietly reorder the row.
void replace_text(Element& e, std::string_view text) {
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
    if (text.empty()) return;
    Ref<TextNode> node = make_ref<TextNode>(text);
    if (anchor) e.insert_before(node.get(), anchor);
    else e.append_child(node.get());
}

// Where the line holding `at` begins and ends, in bytes. Only a <textarea>
// has more than one, which is why Home and End mean something narrower there.
int line_start(const std::string& s, int at) {
    for (int i = std::min(at, static_cast<int>(s.size())) - 1; i >= 0; --i) {
        if (s[static_cast<size_t>(i)] == '\n') return i + 1;
    }
    return 0;
}

int line_end(const std::string& s, int at) {
    for (int i = std::max(0, at); i < static_cast<int>(s.size()); ++i) {
        if (s[static_cast<size_t>(i)] == '\n') return i;
    }
    return static_cast<int>(s.size());
}

// The disabled form control at or above an element, if any. A disabled control
// takes no pointer events at all -- no hover, no press, no click, no focus --
// and neither does anything inside it, which is why this looks UP: the label
// text inside a disabled button is not a live target either.
const Element* disabled_ancestor(const Element* e) {
    for (const Node* n = e; n; n = n->parent()) {
        if (n->node_type() != NodeType::Element) continue;
        const Element& candidate = static_cast<const Element&>(*n);
        const std::string_view tag = candidate.tag_name();
        const bool form_element = tag == "input" || tag == "button" || tag == "select" ||
                                  tag == "textarea" || tag == "option" || tag == "optgroup" ||
                                  tag == "fieldset";
        if (form_element && candidate.has_attribute("disabled")) return &candidate;
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

// What a field holds, wherever it keeps it. An <input> keeps it in `value`;
// a <textarea> keeps it as its content, which is also what gets laid out --
// so editing one has to write there or the typing goes somewhere invisible.
std::string field_value(const Element& e) {
    if (e.tag_name() == "textarea") return text_content_of(e);
    return std::string(e.get_attribute("value"));
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
    // The other end of the selection, or -1 when there is none. A selection is
    // a caret that remembers where it started: every move either drags this
    // along (unshifted) or leaves it where it was (shifted), which is the whole
    // of the behaviour.
    int anchor = -1;

    ElementState state_of(const Element& e) const override {
        uint32_t bits = 0;

        // Form state. The matcher reads :checked, :disabled and
        // :placeholder-shown off this provider -- it has no other channel --
        // and its own comment said they were waiting for a forms layer. They
        // are facts about the DOM rather than about the pointer, but this is
        // where the cascade asks for them, and the shape cache already folds
        // every attribute into its key so caching stays sound.
        const std::string_view tag = e.tag_name();
        const bool form_element = tag == "input" || tag == "button" || tag == "select" ||
                                  tag == "textarea" || tag == "option" || tag == "optgroup" ||
                                  tag == "fieldset";
        if (form_element && e.has_attribute("disabled")) {
            bits |= static_cast<uint32_t>(ElementState::Disabled);
        }
        if ((tag == "input" && e.has_attribute("checked")) ||
            (tag == "option" && e.has_attribute("selected"))) {
            bits |= static_cast<uint32_t>(ElementState::Checked);
        }
        // :placeholder-shown is true only while the field is EMPTY, which is
        // the whole point of it -- it is how a floating label knows to float.
        if ((tag == "input" || tag == "textarea") && !e.get_attribute("placeholder").empty() &&
            e.get_attribute("value").empty()) {
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
        // Half a second lit, half dark. Moving it resets the age, so the
        // cursor is never invisible at the moment you are steering it.
        c.visible = std::fmod(st.caret_age, 1.0) < 0.5;
        const std::string value = field_value(*st.focused);
        const Selection sel = selection_of(st, value.size());
        c.selection_from = static_cast<size_t>(sel.from);
        c.selection_to = static_cast<size_t>(sel.to);
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
    double elapsed = 0;
    double delay = 0;
    double duration = 0;
    Easing easing;

    // Where it is now. Before the delay it holds `from`; after the duration it
    // IS `to`, exactly -- a transition that lands near its declared value
    // rather than on it leaves the document subtly wrong for good.
    std::string value_now(bool* finished) const {
        const double t = elapsed - delay;
        if (t <= 0) { *finished = false; return from; }
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
    std::map<const Element*, ComputedStyle*> by_element;
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

    // Transitions in flight, by element. Cleared with the styles they belong
    // to, since both are keyed on elements of the current document.
    std::map<const Element*, std::vector<RunningTransition>> transitions;

    // @keyframes by name, collected from every sheet as it is added, and the
    // clock each element's animation is running against.
    std::map<std::string, KeyframeAnimation> keyframes;
    std::map<const Element*, double> animation_clock;
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
    std::map<const Element*, std::map<int, std::string>> animation_shown;

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
            pending = worst(pending, invalidation_for_property(kv.first));
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

    // Applies every @keyframes animation an element names.
    //
    // Animations sit ABOVE transitions in the cascade (CSS Cascade L5 §6.1),
    // so this runs after them and overwrites what they wrote -- an element
    // doing both shows the animation, which is what a browser does.
    void apply_animations(const Element& e, ComputedStyle* live, double dt) {
        const std::string_view names = live->get("animation-name");
        if (names.empty() || names == "none") {
            animation_clock.erase(&e);
            restore_animated(e, live);
            return;
        }
        double& clock = animation_clock[&e];
        clock += dt;

        for (size_t i = 0; i < 8; ++i) {
            const std::string_view name = nth(names, i);
            if (name.empty() || name == "none") break;
            auto it = keyframes.find(std::string(name));
            if (it == keyframes.end()) {
                if (nth(names, i + 1) == name) break;
                continue;
            }
            double duration = 0;
            if (!parse_time_seconds(nth(live->get("animation-duration"), i), &duration) ||
                duration <= 0) {
                if (nth(names, i + 1) == name) break;
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

            const double elapsed = clock - delay;
            double progress = 0;
            bool active = true;
            if (elapsed < 0) {
                // Before it starts: `backwards` and `both` show the first
                // frame, everything else leaves the cascaded value alone.
                if (fill != "backwards" && fill != "both") active = false;
                progress = 0;
            } else {
                const double cycles = elapsed / duration;
                if (cycles >= iterations) {
                    // After the last iteration: `forwards` and `both` hold the
                    // end, everything else snaps back to the cascaded value.
                    if (fill != "forwards" && fill != "both") active = false;
                    progress = 1;
                    // Which END, though, depends on where an alternating
                    // animation stopped.
                    const double whole = std::floor(iterations);
                    const bool odd = std::fmod(whole, 2.0) >= 1.0;
                    if ((direction == "alternate" && odd) ||
                        (direction == "alternate-reverse" && !odd) || direction == "reverse") {
                        progress = 0;
                    }
                } else {
                    progress = cycles - std::floor(cycles);
                    const bool odd_cycle = std::fmod(std::floor(cycles), 2.0) >= 1.0;
                    if (direction == "reverse") progress = 1 - progress;
                    else if (direction == "alternate" && odd_cycle) progress = 1 - progress;
                    else if (direction == "alternate-reverse" && !odd_cycle) progress = 1 - progress;
                }
            }
            if (active) {
                const double eased = easing(progress);
                std::map<int, std::string>& saved = animation_saved[&e];
                for (const std::string& property : it->second.properties) {
                    std::string value;
                    if (!keyframe_value_at(it->second, property, eased, &value)) continue;
                    const int id = CssPropertyRegistry::instance().id_of(property);
                    if (id < 0) continue;
                    // The declared value, kept the first time it is overwritten
                    // so the end of the animation has something to go back to.
                    if (saved.find(id) == saved.end()) saved[id] = std::string(live->get(id));
                    live->set(id, value);
                    // Invalidated only when it MOVES. A pass with no time in it
                    // -- a keystroke, a class toggle, a hover -- writes the
                    // same value it wrote last frame, and calling that a change
                    // made every interaction on an animating page cost a
                    // relayout of the whole document.
                    std::string& shown = animation_shown[&e][id];
                    if (shown != value) {
                        shown = value;
                        pending = worst(pending, invalidation_for_property(id));
                    }
                }
            } else {
                restore_animated(e, live);
            }
            any_animation = any_animation || (active && elapsed < iterations * duration);
            if (nth(names, i + 1) == name) break;
        }
    }

    // The nth entry of a comma-separated transition longhand, and the LAST
    // entry for anything past the end -- which is what lets one duration serve
    // three properties, as every stylesheet assumes it does.
    static std::string_view nth(std::string_view list, size_t index) {
        size_t start = 0, count = 0;
        int depth = 0;
        std::string_view last;
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
            last = piece;
            if (count == index) return piece;
            ++count;
            start = i + 1;
        }
        return last;
    }

    // How this property transitions on this style, or false when it does not.
    // The properties are read off the style being transitioned TO, which is
    // what CSS Transitions L1 §3 specifies -- so turning a transition on in a
    // :hover rule makes the way IN animate and the way out snap, exactly as it
    // does in a browser.
    static bool transition_for(const ComputedStyle& style, int id, double* duration,
                               double* delay, Easing* easing) {
        const std::string_view names = style.get("transition-property");
        if (names.empty() || names == "none") return false;
        const std::string_view want = CssPropertyRegistry::instance().name_of(id);
        if (want.empty()) return false;
        size_t index = 0;
        bool found = false;
        for (size_t i = 0; i < 32; ++i) {
            const std::string_view entry = nth(names, i);
            if (entry.empty()) break;
            if (entry == "all" || entry == want) { index = i; found = true; }
            if (nth(names, i + 1) == entry) break;   // past the end: nth repeats
        }
        if (!found) return false;
        if (!parse_time_seconds(nth(style.get("transition-duration"), index), duration)) return false;
        if (*duration <= 0) return false;
        if (!parse_time_seconds(nth(style.get("transition-delay"), index), delay)) *delay = 0;
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
        if (dt <= 0) return;
        for (auto& kv : transitions) {
            auto it = by_element.find(kv.first);
            if (it == by_element.end()) { kv.second.clear(); continue; }
            ComputedStyle* live = it->second;
            std::vector<RunningTransition>& list = kv.second;
            for (size_t i = 0; i < list.size();) {
                list[i].elapsed += dt;
                bool finished = false;
                const std::string now = list[i].value_now(&finished);
                live->set(list[i].property_id, now);
                pending = worst(pending, invalidation_for_property(list[i].property_id));
                if (finished) list.erase(list.begin() + static_cast<long>(i));
                else ++i;
            }
        }
    }

    void begin_pass() {
        pending = Invalidation::None;
        visited = 0;
    }

    // The element set changed, so which boxes exist did too.
    void note_structural() { pending = worst(pending, Invalidation::Boxes); }

    // Everything this map holds about one element, dropped. Called when an
    // element leaves the DOM: every one of these is keyed on the POINTER, and
    // the node is freed the moment its parent lets go of it, so an entry left
    // behind is a stale key that a later allocation at the same address would
    // silently inherit.
    void forget(const Element* e) {
        animation_shown.erase(e);
        by_element.erase(e);
        pseudo_by_element.erase({e, 0});
        pseudo_by_element.erase({e, 1});
        pseudo_by_element.erase({e, 2});
        pseudo_by_element.erase({e, 3});
        transitions.erase(e);
        animation_clock.erase(e);
        animated.erase(e);
        animation_saved.erase(e);
    }

    void clear() {
        owned.clear();
        by_element.clear();
        pseudo_by_element.clear();
        transitions.clear();
        animation_clock.clear();
        animated.clear();
        animation_saved.clear();
        animation_shown.clear();
        pending = Invalidation::Boxes;
    }

    // Computes into `into`, and reports what the difference forces.
    void compute_into(ComputedStyle* into, const Element& e, const ComputedStyle* parent) {
        engine.compute(e, state, parent, &scratch);
        merge(into, &scratch);
    }

    void merge(ComputedStyle* live, ComputedStyle* fresh, const Element* owner = nullptr) {
        std::vector<int> changed;
        bool unattributed = false;
        if (!live->differs_from(*fresh, &changed, &unattributed)) return;

        // A transition starts here, where the value the element is SHOWING and
        // the value the cascade just produced are both in hand. Nothing else
        // in the engine has both.
        std::vector<RunningTransition>* running = nullptr;
        if (owner && !changed.empty()) {
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
                    t.from = t.value_now(&finished);
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
            pending = worst(pending, Invalidation::Boxes);
            return;
        }
        for (const int id : changed) {
            pending = worst(pending, invalidation_for_property(id));
            if (pending == Invalidation::Boxes) return;
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
        const std::string_view animation = raw->get("animation-name");
        if (!animation.empty() && animation != "none") animated.insert(&e);
        else animated.erase(&e);

        // `backdrop` alongside the two content pseudos: it is computed the
        // same way, cached the same way, and the box builder asks for it by
        // the same call. Without it here the UA sheet's `::backdrop` rule
        // matched nothing and a modal dialog had no dim behind it.
        static constexpr std::string_view kPseudos[4] = {"before", "after", "backdrop", "marker"};
        for (int i = 0; i < 4; ++i) {
            auto pit = pseudo_by_element.find({&e, i});
            const bool had = pit != pseudo_by_element.end();
            const bool has = engine.compute_pseudo_element(e, kPseudos[i], state, *raw, &scratch);
            if (!has) {
                // A pseudo that stopped being generated takes its box with it.
                if (had) {
                    pseudo_by_element.erase(pit);
                    note_structural();
                }
                continue;
            }
            if (!had) {
                auto ps = std::make_unique<ComputedStyle>();
                std::swap(*ps, scratch);
                pseudo_by_element[{&e, i}] = ps.get();
                owned.push_back(std::move(ps));
                note_structural();
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
        const int i = name == "before"     ? 0
                      : name == "after"    ? 1
                      : name == "backdrop" ? 2
                      : name == "marker"   ? 3
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
        const uint64_t v = t_.variant(t_.user_data, face.id, weight, italic ? 1 : 0);
        return v ? FaceHandle{v} : face;
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
    // Metrics for the host's bold / italic variants of the default face, by
    // (face, weight, italic); handed to layout through ctx.variant_metrics.
    std::map<std::tuple<uint64_t, int, bool>, std::unique_ptr<FontInterfaceMetrics>> variant_metrics;
    FaceHandle face = StubFont::builtin();
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
    static constexpr size_t kUndoDepth = 100;
    std::unordered_map<const Element*, EditHistory> history;
    // The open popovers, innermost last. A stack rather than a flag because
    // popovers nest -- a menu opens a submenu -- and Escape closes one per
    // press rather than all of them. The open state itself lives in the
    // `data-popover-open` attribute, so a stylesheet can select on it and a
    // script can read it; this only remembers the ORDER.
    std::vector<const Element*> popovers;
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
    // The open dropdown, and which of its options the pointer or the keys are
    // on. Held here rather than in the DOM because being open is not a
    // property of the document -- reload the same markup and nothing is open.
    const Element* open_select = nullptr;
    int highlighted_option = -1;
    // The row the open list is scrolled to. A list longer than the cap has to
    // move, or its last options can be neither seen nor clicked.
    int select_first_row = 0;
    // What the focused field held when the focus arrived. A blur compares
    // against it to decide whether anything was committed: `on-change` fires
    // for an edit, and not for a visit.
    std::string value_at_focus;
    // Set when the cursor moved or the text under it changed, so the next
    // update scrolls it back into view. Not every frame: a reader who has
    // scrolled away from the cursor should stay there.
    bool caret_follow = false;
    // What caret the published draws were painted with. The blink is a change
    // no cascade can see, so it is compared against this to ask for a repaint.
    CaretState caret_painted;

    // What a host did that no element's style records: a stylesheet added, the
    // viewport resized, a backend swapped, the document loaded. Cleared by the
    // update that acts on it.
    Invalidation pending = Invalidation::Boxes;
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

    // Events waiting for the host to pump them. Bounded: a host that never
    // reads gets the oldest dropped rather than unbounded growth, because a
    // queue that can starve a process is worse than a lost click.
    std::deque<weva_event> events;
    static constexpr size_t kMaxEvents = 256;
    // The element a press started on, so a release on the SAME one is a click
    // and a release anywhere else is not.
    const Element* press_target = nullptr;

    weva_element_t handle_of(const Element* e) const {
        if (!e) return WEVA_ELEMENT_NONE;
        for (size_t i = 0; i < elements.size(); ++i) {
            if (elements[i] == e) return static_cast<weva_element_t>(i);
        }
        return WEVA_ELEMENT_NONE;
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
            case WEVA_EVENT_SCROLL: return "on-scroll";
            case WEVA_EVENT_TOGGLE: return "on-toggle";
            case WEVA_EVENT_CONTEXT_MENU: return "on-contextmenu";
            case WEVA_EVENT_KEY_DOWN: return "on-keydown";
            case WEVA_EVENT_KEY_UP: return "on-keyup";
            case WEVA_EVENT_TEXT_INPUT: return "on-textinput";
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
            if (named.empty()) continue;
            const size_t copy = named.size() < sizeof(e->handler) - 1 ? named.size()
                                                                     : sizeof(e->handler) - 1;
            std::memcpy(e->handler, named.data(), copy);
            e->handler[copy] = '\0';
            return;
        }
    }

    void queue_event(int32_t kind, const Element* target, double x, double y, uint32_t buttons) {
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
        fill_handler(&e, target);
        if (events.size() >= kMaxEvents) events.pop_front();
        events.push_back(e);
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
        std::set<const Element*> known(elements.begin(), elements.end());
        const std::function<void(Element&)> visit = [&](Element& e) {
            if (!known.count(&e)) elements.push_back(&e);
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

namespace {

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
    std::string_view source;
    for (const Ref<Node>& child : caret->element->children()) {
        if (child->node_type() == NodeType::Text) {
            source = static_cast<const TextNode&>(*child).data();
            break;
        }
    }
    if (source.empty()) return;
    caret->source = source;
    const size_t idx = static_cast<size_t>(std::max(0, caret->index));
    const char* base = source.data();
    BoxId last = kNoBox;
    size_t last_end = 0;
    for (int i = 0; i < doc->tree.size(); ++i) {
        const Box& b = doc->tree[i];
        if (b.kind != BoxKind::Text || b.text.empty()) continue;
        // The FRAGMENTS, not the run they were split from. Inline layout keeps
        // the whole unsplit run in the tree as well, and it spans the entire
        // value -- so it matches any cursor, and paint never draws it. Only a
        // fragment sits in a line box, which is also where its baseline comes
        // from.
        if (b.parent == kNoBox || doc->tree[b.parent].kind != BoxKind::Line) continue;
        const char* run = b.text.data();
        if (run < base || run + b.text.size() > base + source.size()) continue;
        const size_t off = static_cast<size_t>(run - base);
        if (idx >= off && idx <= off + b.text.size()) {
            caret->run = i;
            caret->run_offset = idx - off;
            return;
        }
        if (off + b.text.size() <= idx) {
            last = i;
            last_end = b.text.size();
        }
    }
    // Past every run -- the value ends in a newline, so the cursor is on an
    // empty last line. The end of the last run is the closest honest place for
    // it until empty lines carry a run of their own.
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
    for (int i = 0; i < doc->tree.size(); ++i) {
        if (doc->tree[i].element == e) return i;
    }
    return kNoBox;
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

// A popover is `auto` unless it says `manual`. The difference is what closes
// it: an auto one goes away when you click elsewhere or press Escape, a manual
// one only when something asks.
bool popover_is_auto(const Element& e) {
    return e.get_attribute("popover") != "manual";
}

// Opening and closing, which is only ever these three lines plus the stack.
void popover_show(weva_document* doc, Element& e) {
    if (!e.has_attribute("popover") || e.has_attribute("data-popover-open")) return;
    e.set_attribute("data-popover-open", "");
    doc->popovers.push_back(&e);
    // It joins the top layer, so a ::backdrop box appears: boxes, not paint.
    doc->pending = worst(doc->pending, Invalidation::Boxes);
    doc->queue_event(WEVA_EVENT_TOGGLE, &e, 0, 0, 0);
}

void popover_hide(weva_document* doc, Element& e) {
    if (!e.has_attribute("data-popover-open")) return;
    e.remove_attribute("data-popover-open");
    doc->popovers.erase(std::remove(doc->popovers.begin(), doc->popovers.end(), &e),
                        doc->popovers.end());
    doc->pending = worst(doc->pending, Invalidation::Boxes);
    doc->queue_event(WEVA_EVENT_TOGGLE, &e, 0, 0, 0);
}

// Escape closes ONE, the topmost auto one -- so a submenu closes before the
// menu it opened from, and a manual popover in between is stepped over rather
// than closed.
bool popover_hide_top_auto(weva_document* doc) {
    for (size_t i = doc->popovers.size(); i-- > 0;) {
        Element& e = const_cast<Element&>(*doc->popovers[i]);
        if (!popover_is_auto(e)) continue;
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

bool is_within(const Element* candidate, const Element* ancestor) {
    for (const Node* n = candidate; n; n = n->parent()) {
        if (n == ancestor) return true;
    }
    return false;
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
        return owner.tag_name() == "details" ? &owner : nullptr;
    }
    return nullptr;
}

// The <form> an element is inside, if any. A submit is reported against the
// form rather than against whatever was pressed, since that is what a handler
// is written for.
Element* form_of(const Element* e) {
    for (const Node* n = e; n; n = n->parent()) {
        if (n->node_type() != NodeType::Element) continue;
        Element& candidate = const_cast<Element&>(static_cast<const Element&>(*n));
        if (candidate.tag_name() == "form") return &candidate;
    }
    return nullptr;
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

void note_value_change(weva_document* doc, Element& e, std::string_view value) {
    doc->dom_touched = true;
    if (doc->touched.size() < 64) doc->touched.push_back(&e);
    weva_event ev{};
    ev.kind = WEVA_EVENT_VALUE_CHANGED;
    ev.target = doc->handle_of(&e);
    weva_document::fill_handler(&ev, &e);
    const size_t copy = value.size() < sizeof(ev.text) - 1 ? value.size() : sizeof(ev.text) - 1;
    std::memcpy(ev.text, value.data(), copy);
    ev.text[copy] = '\0';
    if (doc->events.size() >= weva_document::kMaxEvents) doc->events.pop_front();
    doc->events.push_back(ev);
    if (commits_immediately(e)) {
        weva_event committed = ev;
        committed.kind = WEVA_EVENT_CHANGE;
        weva_document::fill_handler(&committed, &e);
        if (doc->events.size() >= weva_document::kMaxEvents) doc->events.pop_front();
        doc->events.push_back(committed);
    }
}

std::string trimmed(std::string text) {
    static const char* kSpace = " \t\r\n";
    const size_t first = text.find_first_not_of(kSpace);
    if (first == std::string::npos) return std::string();
    const size_t last = text.find_last_not_of(kSpace);
    return text.substr(first, last - first + 1);
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
        if (doc->highlighted_option < first) first = doc->highlighted_option;
        else if (doc->highlighted_option >= first + g.rows) {
            first = doc->highlighted_option - g.rows + 1;
        }
    }
    doc->select_first_row = std::clamp(first, 0, last);
}

// What a list box holds: the chosen options' values, comma separated, since a
// `multiple` one can have several and a host needs all of them.
std::string list_box_value(const Element& select) {
    std::string out;
    for (const Element* option : select_options(select)) {
        if (!option->has_attribute("selected")) continue;
        std::string value(option->get_attribute("value"));
        if (value.empty()) value = trimmed(text_content_of(*option));
        if (!out.empty()) out += ",";
        out += value;
    }
    return out;
}

// The <select> a clicked <option> belongs to, when that select is a LIST box
// -- one with `size` or `multiple`, whose options are laid out in flow rather
// than hidden behind a closed control. A click on a row of one of those is a
// choice; a click on an option anywhere else cannot happen, because they are
// `display: none`.
Element* list_box_of(const Element* option) {
    if (!option || option->tag_name() != "option") return nullptr;
    for (const Node* n = option->parent(); n; n = n->parent()) {
        if (n->node_type() != NodeType::Element) continue;
        Element& e = const_cast<Element&>(static_cast<const Element&>(*n));
        if (e.tag_name() == "optgroup") continue;   // a group is not the control
        if (e.tag_name() != "select") return nullptr;
        const bool list = e.has_attribute("size") || e.has_attribute("multiple");
        return list ? &e : nullptr;
    }
    return nullptr;
}

// Chooses an option: `selected` moves onto it and off its siblings, so the DOM
// holds the answer and :checked, the paint and a script all read the same
// thing.
void choose_option(weva_document* doc, Element& select, int index) {
    const std::vector<const Element*> options = select_options(select);
    if (index < 0 || index >= static_cast<int>(options.size())) return;
    for (size_t i = 0; i < options.size(); ++i) {
        Element& option = const_cast<Element&>(*options[i]);
        if (static_cast<int>(i) == index) option.set_attribute("selected", "");
        else option.remove_attribute("selected");
    }
    doc->dom_touched = true;
    if (doc->touched.size() < 64) doc->touched.push_back(&select);
    doc->pending = worst(doc->pending, Invalidation::Boxes);
    const Element* chosen = options[static_cast<size_t>(index)];
    // An option's value is its `value` attribute, or its own text when it has
    // none -- which is how the markup for a plain list of choices works.
    std::string value(chosen->get_attribute("value"));
    if (value.empty()) value = trimmed(text_content_of(*chosen));
    note_value_change(doc, select, value);
}

// Which option is chosen now, or -1.
int chosen_index(const Element& select) {
    const std::vector<const Element*> options = select_options(select);
    for (size_t i = 0; i < options.size(); ++i) {
        if (options[i]->has_attribute("selected")) return static_cast<int>(i);
    }
    return options.empty() ? -1 : 0;
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
    const PaintContext paint = measuring_context(doc);
    if (e.tag_name() != "textarea") {
        return control_text_offset_at(doc->tree, box, doc->ctx, paint, x);
    }
    std::string_view source;
    for (const Ref<Node>& child : e.children()) {
        if (child->node_type() == NodeType::Text) {
            source = static_cast<const TextNode&>(*child).data();
            break;
        }
    }
    if (source.empty()) return 0;
    double ox = 0, oy = 0;
    visual_position(doc->tree, box, &ox, &oy);
    return run_text_offset_at(doc->tree, box, doc->ctx, paint, source, ox, oy, x, y);
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
    // Cached textures are handles the OLD backend issued, and the draws that
    // reference them went to it too. Both have to be given up here, while the
    // backend that owns them is still the one installed.
    doc->textures.release_all(doc->render_backend());
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
    // Only the default face has variants here; a registered family's
    // metrics object is not a host face.
    if (base && base != doc->host_metrics.get()) return base;
    const FaceHandle v = doc->host_font->variant(doc->face, weight, italic);
    if (v.id == doc->face.id) return base;
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
    // A zero face means "use whatever the backend's load_face returned", which
    // a host that has only one face can leave alone.
    doc->face = face ? FaceHandle{face} : StubFont::builtin();
    doc->host_metrics = std::make_unique<FontInterfaceMetrics>(doc->host_font.get(), doc->face);
    doc->ctx.variant_metrics = &document_variant_metrics;
    doc->ctx.variant_user = doc;
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

void weva_document_destroy(weva_document_t doc) {
    // Cached textures outlive a pass, so the host is told to drop them here
    // rather than leaking them for the life of the process.
    if (doc) {
        doc->textures.release_all(doc->render_backend());
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

    // Components (`<template id="card">` + `<card>` + `<slot>`) expand BEFORE
    // the cascade, as UIDocumentBuilder does, so selectors match the expanded
    // subtree and not the un-rendered host.
    expand_components(doc->doc.get());

    doc->elements.clear();
    for (const Ref<Node>& c : doc->doc->children()) {
        if (c->node_type() == NodeType::Element) {
            doc->index_elements(static_cast<Element&>(const_cast<Node&>(*c)));
        }
    }
    // Every style is keyed on an element of the old document, so none of them
    // can be reused and the walk would otherwise see a page of new elements.
    doc->styles.clear();
    doc->pending = Invalidation::Boxes;
    // Those point at elements of the document just replaced.
    doc->touched.clear();
    doc->dom_touched = false;
    doc->events.clear();
    doc->press_target = nullptr;
    doc->scroll.clear();   // keyed on elements of the document just replaced
    doc->open_select = nullptr;
    doc->highlighted_option = -1;
    doc->binding_templates.clear();
    doc->binding_repeats.clear();
    doc->caret_painted = CaretState{};
    doc->scroll_drag = weva_document::ScrollDrag{};
    doc->popovers.clear();
    doc->tooltip = weva_document::Tooltip{};
    doc->history.clear();
    doc->value_at_focus.clear();
    // The interaction state too, which is what the pointer is OVER and what
    // has the focus. StyleMap::clear() left it alone, so after a reload the
    // hover chain still named elements of the replaced document -- and the
    // next pointer move raised a leave event on one of them, reading a freed
    // Node's vtable. UBSan caught it the first time a test reloaded HTML into
    // a live document and then moved the pointer.
    InteractionState& st = doc->styles.state;
    st.hover_chain.clear();
    st.active_chain.clear();
    st.focused = nullptr;
    st.caret = 0;
    st.anchor = -1;
    st.caret_age = 0;
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
    collect_keyframes(*doc->sheets.back(), &doc->styles.keyframes);
    return WEVA_OK;
}

void weva_document_set_viewport(weva_document_t doc, int width, int height) {
    if (!doc || width <= 0 || height <= 0) return;
    if (width == doc->config.viewport_width && height == doc->config.viewport_height) return;
    doc->config.viewport_width = width;
    doc->config.viewport_height = height;
    doc->ctx.viewport_width_px = width;
    doc->ctx.viewport_height_px = height;
    // A cached texture is keyed by the box's own size and style, which does not
    // capture a viewport unit INSIDE a gradient -- a `50vw` stop on a
    // fixed-width box would survive a resize it should not. Dropping the cache
    // on a resize is exact and costs one pass.
    doc->textures.release_all(doc->render_backend());
    // Computed styles do not mention the viewport -- percentages and viewport
    // units are resolved at layout -- so the cascade would report no change at
    // all while every size on the page may be different.
    doc->pending = worst(doc->pending, Invalidation::Layout);
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
    return doc->styles.animating() ? 1 : 0;
}

namespace {

// Defined with the pointer handling below; the update loop drives the dwell,
// which is what makes a tooltip appear over a pointer that has stopped moving.
void tooltip_show(weva_document* doc);
void tooltip_hide(weva_document* doc);

}   // namespace

weva_status weva_document_update(weva_document_t doc, double dt_seconds) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    if (!doc->doc) return WEVA_ERR_NOT_FOUND;

    // WEVA_STAGE_LOG breaks an update into its four stages. A whole-update
    // number says a change is slow; it does not say which half to look at, and
    // the answer moved once the texture cache landed.
    static const bool stage_log = std::getenv("WEVA_STAGE_LOG") != nullptr;
    const auto now = [] { return std::chrono::steady_clock::now(); };
    auto t0 = now();
    const auto lap = [&](const char* what) {
        if (!stage_log) return;
        const auto t = now();
        std::fprintf(stderr, "  %-12s %7.3f ms\n", what,
                     std::chrono::duration<double, std::milli>(t - t0).count());
        t0 = t;
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
    CaretState caret = caret_for(doc->styles.state);
    if (caret.element != doc->caret_painted.element ||
        caret.index != doc->caret_painted.index ||
        caret.visible != doc->caret_painted.visible) {
        doc->pending = worst(doc->pending, Invalidation::Paint);
    }
    if (doc->pending == Invalidation::None && !doc->dom_touched &&
        !(dt_seconds > 0 && doc->styles.animating())) {
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
    const bool scoped = doc->pending == Invalidation::None && !doc->touched.empty() &&
                        doc->touched.size() < 64 && !doc->styles.engine.has_has_selectors();
    if (scoped) {
        const bool siblings_matter = doc->styles.engine.has_sibling_selectors();
        for (Element* e : doc->touched) {
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

    // Time passes after the cascade has set the targets, so a transition that
    // started this very pass gets its first step in the same frame rather than
    // showing its start value for one. Timed separately: charging it to the
    // cascade made an animating page look as though it were restyling, which
    // is exactly what it is NOT doing.
    doc->styles.advance(dt_seconds);
    lap("animate");
    Invalidation pending = doc->styles.pending;
    // Anything a host did that the cascade cannot see -- a new stylesheet, a
    // resized viewport, a backend swap, the first update of all -- is recorded
    // by the entry point that did it.
    pending = worst(pending, doc->pending);
    doc->pending = Invalidation::None;

    if (pending == Invalidation::None) {
        // Nothing an element can see is different, so the draws already
        // published are the right ones. The ABI says they stay valid until the
        // next update, and this IS the next update.
        lap("boxes");
        lap("layout");
        lap("paint");
        return WEVA_OK;
    }

    // Laying out is not something a tree can be put through twice: inline
    // layout CREATES boxes -- line boxes, the fragments an inline box is split
    // into -- so a second pass over an already laid-out tree does not reproduce
    // the first. (Tried: it published 18 draws where a fresh document published
    // 24.) So a change that moves anything rebuilds the tree, which is cheap
    // beside the layout it feeds: box building is 0.1 ms against 1-3 ms.
    //
    // That leaves the tiers that matter as None, Paint, and everything else.
    if (pending >= Invalidation::Layout) {
        doc->tree.reset();
        BoxBuilder builder(&doc->tree, &doc->styles);
        doc->root = builder.build_document(*doc->doc);
        if (doc->root == kNoBox) return WEVA_ERR_INTERNAL;
    }
    lap("boxes");

    if (pending >= Invalidation::Layout) {
        BlockLayout block(&doc->tree, doc->ctx, &doc->metrics_backend());
        block.layout_root(doc->root, doc->ctx.viewport_width_px, doc->ctx.viewport_height_px);
        run_positioning(&doc->tree, doc->root, doc->ctx, &block);
        // After positioning, because an absolutely positioned child is not
        // where layout first put it and the rect has to cover where it ended.
        compute_visual_overflow(&doc->tree, doc->root);
    }
    lap("layout");

    // Where the cursor ended up, now that there is a layout, and the scroll
    // that keeps it in view: typing at the bottom of a textarea has to move
    // the view, or the text goes on past what you can see. Before the offsets
    // are applied below, so the scroll this asks for lands in the same frame.
    resolve_caret_run(doc, &caret);
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
            if (!b.element) continue;
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

    for (TextureHandle t : doc->transient_textures) doc->render_backend()->release_texture(t);
    doc->transient_textures.clear();
    doc->backend.begin_frame();
    PaintContext paint;
    paint.backend = doc->render_backend();
    paint.font = doc->font_backend();
    paint.atlas = &doc->atlas;
    paint.face = doc->face;
    paint.owned_textures = &doc->transient_textures;
    paint.texture_cache = &doc->textures;
    paint.caret = caret;
    paint.popup.element = doc->open_select;
    paint.popup.highlighted = doc->highlighted_option;
    paint.popup.first_row = doc->select_first_row;
    doc->caret_painted = caret;
    doc->textures.begin_pass();
    paint_tree(doc->tree, doc->root, doc->ctx, paint);
    doc->textures.end_pass(doc->render_backend());
    lap("paint");

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
        // A removed element leaves a hole rather than renumbering the ones
        // after it, so every walk of this table steps over nulls.
        if (!doc->elements[i]) continue;
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
        // Where it is drawn, not where layout put it: a host placing a
        // tooltip beside an element in a scrolled list wants the position it
        // can see.
        double ax = 0, ay = 0;
        visual_position(doc->tree, i, &ax, &ay);
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

namespace {

// Marks a state change for the next update.
//
// The cascade's match cache already folds every element's state bits AND its
// ancestors' into the shape key, so a hover change lands on a different entry
// by itself and nothing has to be invalidated. What the UPDATE needs is the
// list of elements whose style may have moved, which is the union of the chain
// that was in the state and the chain that is -- and that is exactly the list
// the scoped restyle takes.
void note_state_change(weva_document* doc, const std::vector<const Element*>& before,
                       const std::vector<const Element*>& after) {
    if (before == after) return;
    ++doc->styles.state.version_;
    doc->dom_touched = true;
    const auto note = [&](const std::vector<const Element*>& chain) {
        for (const Element* e : chain) {
            if (doc->touched.size() >= 64) return;
            doc->touched.push_back(const_cast<Element*>(e));
        }
    };
    note(before);
    note(after);
}

}   // namespace

weva_element_t weva_document_element_at(weva_document_t doc, double x, double y) {
    if (!doc) return WEVA_ELEMENT_NONE;
    const Element* hit = element_at_point(doc->tree, doc->root, x, y);
    if (!hit) return WEVA_ELEMENT_NONE;
    for (size_t i = 0; i < doc->elements.size(); ++i) {
        if (doc->elements[i] == hit) return static_cast<weva_element_t>(i);
    }
    return WEVA_ELEMENT_NONE;
}

namespace {

// Writes what a field holds, to wherever that field keeps it. A <textarea>
// keeps it as content, so this rebuilds its text node -- which is a structural
// change, and the reason a keystroke in one costs a box rebuild.
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

void set_field_value(weva_document* doc, Element& e, std::string_view value) {
    if (e.tag_name() == "textarea") {
        replace_text(e, value);
        doc->pending = worst(doc->pending, Invalidation::Boxes);
        return;
    }
    e.set_attribute("value", value);
}

// A radio button turns its group off before turning itself on. The group is
// every radio with the same `name` in the document, which is what makes the
// exclusivity work at all.
void clear_radio_group(Node& root, std::string_view name, const Element* except) {
    for (const Ref<Node>& c : root.children()) {
        if (c->node_type() != NodeType::Element) continue;
        auto& e = static_cast<Element&>(const_cast<Node&>(*c));
        if (&e != except && e.tag_name() == "input" && input_type_of(e) == "radio" &&
            e.get_attribute("name") == name) {
            e.remove_attribute("checked");
        }
        clear_radio_group(e, name, except);
    }
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

// What a click does to a control. Returns true when it changed something.
bool activate_control(weva_document* doc, Element& e, double x) {
    const std::string type = input_type_of(e);
    if (e.tag_name() == "input" && (type == "checkbox" || type == "radio")) {
        if (type == "radio") {
            // A radio cannot be turned off by clicking it, only by another in
            // its group being turned on.
            if (e.has_attribute("checked")) return false;
            clear_radio_group(*doc->doc, e.get_attribute("name"), &e);
            e.set_attribute("checked", "");
        } else {
            if (e.has_attribute("checked")) e.remove_attribute("checked");
            else e.set_attribute("checked", "");
        }
        note_value_change(doc, e, e.has_attribute("checked") ? "on" : "");
        return true;
    }
    if (e.tag_name() == "input" && type == "range") {
        // The value comes from where in the track the pointer landed.
        double ex = 0, ey = 0, ew = 0, eh = 0;
        if (weva_element_bounds(doc, doc->handle_of(&e), &ex, &ey, &ew, &eh) != WEVA_OK) {
            return false;
        }
        if (ew <= 0) return false;
        const auto number = [&](const char* attr, double fallback) {
            const std::string raw(e.get_attribute(attr));
            if (raw.empty()) return fallback;
            char* end = nullptr;
            const double v = std::strtod(raw.c_str(), &end);
            return end == raw.c_str() ? fallback : v;
        };
        const double lo = number("min", 0);
        double hi = number("max", 100);
        if (hi <= lo) hi = lo + 1;
        const double step = number("step", 1);
        double frac = (x - ex) / ew;
        frac = frac < 0 ? 0 : frac > 1 ? 1 : frac;
        double value = lo + frac * (hi - lo);
        if (step > 0) value = lo + std::round((value - lo) / step) * step;
        if (value < lo) value = lo;
        if (value > hi) value = hi;
        char buf[32];
        std::snprintf(buf, sizeof(buf), "%g", value);
        if (e.get_attribute("value") == buf) return false;
        e.set_attribute("value", buf);
        note_value_change(doc, e, buf);
        return true;
    }
    return false;
}

}   // namespace

namespace {

// The scrollbar under a point, if any: from the innermost box there, up
// through the scroll containers that enclose it. The bar overlays the content,
// so the box AT the point is a row rather than the scroller -- the walk up is
// what finds the thing the bar belongs to.
bool scrollbar_under(const weva_document* doc, double x, double y, Scrollbar* out, BoxId* out_box,
                     bool* out_vertical, bool* out_on_thumb) {
    for (BoxId id = box_at_point(doc->tree, doc->root, x, y); id != kNoBox;
         id = doc->tree[id].parent) {
        if (!doc->tree[id].element || !clips_overflow(doc->tree[id])) continue;
        double ox = 0, oy = 0;
        visual_position(doc->tree, id, &ox, &oy);
        for (const bool vertical : {true, false}) {
            const Scrollbar bar = scrollbar_of(doc->tree, id, vertical, ox, oy);
            if (!bar.visible || !bar.track.contains(x, y)) continue;
            *out = bar;
            *out_box = id;
            *out_vertical = vertical;
            *out_on_thumb = bar.thumb.contains(x, y);
            return true;
        }
    }
    return false;
}

}   // namespace

void weva_document_set_pointer(weva_document_t doc, double x, double y, uint32_t buttons) {
    if (!doc) return;
    InteractionState& st = doc->styles.state;

    // An open list is above the document, so it takes the pointer before the
    // tree does: what is under a dropdown is not what you are pointing at.
    if (doc->open_select) {
        const int row = select_row_at(doc, x, y);
        if (row != doc->highlighted_option && row >= 0) {
            doc->highlighted_option = row;
            doc->pending = worst(doc->pending, Invalidation::Paint);
        }
        if ((buttons & WEVA_BUTTON_PRIMARY) && st.active_chain.empty()) {
            Element& select = const_cast<Element&>(*doc->open_select);
            if (row >= 0) choose_option(doc, select, row);
            // A press anywhere -- on a row or off the list -- closes it, which
            // is what makes clicking away cancel.
            doc->open_select = nullptr;
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
        if (buttons == 0) {
            doc->scroll_drag = weva_document::ScrollDrag{};
        } else {
            const double along = doc->scroll_drag.vertical ? y : x;
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
    if ((buttons & WEVA_BUTTON_PRIMARY) && st.active_chain.empty()) {
        Scrollbar bar;
        BoxId box = kNoBox;
        bool vertical = false, on_thumb = false;
        if (scrollbar_under(doc, x, y, &bar, &box, &vertical, &on_thumb)) {
            const Box& b = doc->tree[box];
            const double at = vertical ? b.scroll_y : b.scroll_x;
            if (on_thumb) {
                doc->scroll_drag.element = b.element;
                doc->scroll_drag.vertical = vertical;
                doc->scroll_drag.grab = vertical ? y : x;
                doc->scroll_drag.from = at;
                doc->scroll_drag.per_pixel = bar.scroll_per_pixel;
            } else {
                // A page in the direction clicked, which is what a track click
                // does everywhere.
                const double page = vertical ? b.height - b.border_top - b.border_bottom
                                             : b.width - b.border_left - b.border_right;
                const double point = vertical ? y : x;
                const double thumb_start = vertical ? bar.thumb.y : bar.thumb.x;
                const double to = std::max(0.0, at + (point < thumb_start ? -page : page));
                auto& offset = doc->scroll[b.element];
                offset = {vertical ? b.scroll_x : to, vertical ? to : b.scroll_y};
            }
            doc->pending = worst(doc->pending, Invalidation::Paint);
            return;
        }
    }

    const Element* hit = element_at_point(doc->tree, doc->root, x, y);
    // A disabled control is not a target: it still occupies its space, so what
    // is behind it is not hit either, but nothing about it responds. A greyed
    // out button that still reports clicks is worse than one that is not
    // greyed out at all.
    if (hit && disabled_ancestor(hit)) hit = nullptr;

    // Enter and leave are reported against the innermost element, which is
    // where the hover chain starts.
    const Element* was = st.hover_chain.empty() ? nullptr : st.hover_chain.front();
    if (was != hit) {
        if (was) doc->queue_event(WEVA_EVENT_POINTER_LEAVE, was, x, y, buttons);
        if (hit) doc->queue_event(WEVA_EVENT_POINTER_ENTER, hit, x, y, buttons);
    }
    // Only the PRIMARY button presses. Everything here keyed off "any button
    // held", so a right-click toggled checkboxes, submitted forms, opened
    // <details> and worked popovers -- none of which a right-click does in a
    // browser.
    const uint32_t primary = buttons & WEVA_BUTTON_PRIMARY;
    // The secondary button going down is a context-menu request and nothing
    // else. The engine has no menu of its own to show: a menu is markup, and
    // this says where the user asked for one.
    if ((buttons & WEVA_BUTTON_SECONDARY) && !(doc->buttons_last & WEVA_BUTTON_SECONDARY)) {
        doc->queue_event(WEVA_EVENT_CONTEXT_MENU, hit, x, y, buttons);
    }
    doc->buttons_last = buttons;

    const uint32_t was_down = st.active_chain.empty() ? 0u : 1u;
    if (primary != 0 && !was_down) {
        doc->press_target = hit;
        doc->queue_event(WEVA_EVENT_POINTER_DOWN, hit, x, y, buttons);
        // A range follows the pointer from the moment it goes down, and a
        // click on a field takes focus -- both are what makes a control feel
        // like one rather than like a picture of one.
        if (hit) {
            Element& e = const_cast<Element&>(*hit);
            // A row of a list box. `multiple` toggles, because the ABI carries
            // buttons and not modifiers -- there is no Ctrl+click to tell a
            // toggle from a replace, and toggling is what a settings list
            // wants. A single-selection list replaces, as it does everywhere.
            if (Element* list = list_box_of(&e)) {
                weva_document_set_focus(doc, doc->handle_of(list));
                if (list->has_attribute("multiple")) {
                    if (e.has_attribute("selected")) e.remove_attribute("selected");
                    else e.set_attribute("selected", "");
                    doc->dom_touched = true;
                    if (doc->touched.size() < 64) doc->touched.push_back(&e);
                    doc->pending = worst(doc->pending, Invalidation::Boxes);
                    note_value_change(doc, *list, list_box_value(*list));
                } else {
                    const std::vector<const Element*> options = select_options(*list);
                    for (size_t i = 0; i < options.size(); ++i) {
                        if (options[i] == &e) {
                            choose_option(doc, *list, static_cast<int>(i));
                            break;
                        }
                    }
                }
                return;
            }
            if (e.tag_name() == "select" && !e.has_attribute("multiple") &&
                !e.has_attribute("size") && !e.has_attribute("disabled")) {
                weva_document_set_focus(doc, doc->handle_of(hit));
                doc->open_select = &e;
                doc->highlighted_option = chosen_index(e);
                doc->select_first_row = 0;
                reveal_highlighted_option(doc);
                doc->pending = worst(doc->pending, Invalidation::Paint);
                return;
            }
            if (input_type_of(e) == "range") activate_control(doc, e, x);
            if (is_text_field(e)) {
                weva_document_set_focus(doc, doc->handle_of(hit));
                // The cursor goes where you pressed, not to the end of the
                // value: a field you can only ever click into at the end is
                // one you cannot edit the middle of.
                InteractionState& state = doc->styles.state;
                state.caret = static_cast<int>(field_offset_at(doc, e, x, y));
                state.anchor = -1;
                state.caret_age = 0;
                doc->pending = worst(doc->pending, Invalidation::Paint);
            }
        }
    } else if (primary != 0 && was_down && doc->press_target &&
               is_text_field(*doc->press_target)) {
        // Dragging from a press inside a field selects, and keeps selecting
        // once the pointer has left the field -- as it does everywhere.
        InteractionState& state = doc->styles.state;
        const int to = static_cast<int>(field_offset_at(doc, *doc->press_target, x, y));
        if (to != state.caret) {
            if (state.anchor < 0) state.anchor = state.caret;
            state.caret = to;
            state.caret_age = 0;
            doc->pending = worst(doc->pending, Invalidation::Paint);
        }
    } else if (primary != 0 && was_down && doc->press_target &&
               input_type_of(const_cast<Element&>(*doc->press_target)) == "range") {
        // Held and moving: the range keeps following, even once the pointer
        // has left it, which is how a slider behaves everywhere.
        activate_control(doc, const_cast<Element&>(*doc->press_target), x);
    } else if (primary == 0 && was_down) {
        doc->queue_event(WEVA_EVENT_POINTER_UP, hit, x, y, buttons);
        // A click is a press and a release on the SAME element. Releasing
        // somewhere else is a drag that ended, and is not a click -- which is
        // the behaviour every button in every toolkit has.
        if (hit && hit == doc->press_target) {
            doc->queue_event(WEVA_EVENT_CLICK, hit, x, y, buttons);
            // A submit button submits the form it is in. `type` decides:
            // a <button> in a form submits by default, as HTML says.
            {
                // `input_type_of` answers with the TAG for anything that is
                // not an <input>, so a <button>'s `type` has to be read
                // directly -- reading it through that helper said every
                // button was type="button" and submitted none of them.
                Element& pressed = const_cast<Element&>(*hit);
                const std::string_view tag = pressed.tag_name();
                bool submits = false;
                if (tag == "button") {
                    // HTML: a button in a form submits unless it says
                    // otherwise.
                    const std::string_view declared = pressed.get_attribute("type");
                    submits = declared.empty() || declared == "submit";
                } else if (tag == "input") {
                    submits = input_type_of(pressed) == "submit";
                }
                if (submits) {
                    if (Element* form = form_of(hit)) {
                        doc->queue_event(WEVA_EVENT_SUBMIT, form, x, y, buttons);
                    }
                }
            }
            // Popovers, before anything else a click might mean.
            //
            // Two passes, as the reference has them. First: was this a click
            // on a `popovertarget` trigger? Then it works that popover and
            // nothing else. Otherwise: light dismiss -- an open `auto`
            // popover closes when a click lands outside it, which is what
            // makes a menu behave like a menu.
            if (const Element* trigger = popover_trigger_at(hit)) {
                const std::string target_id(trigger->get_attribute("popovertarget"));
                if (Element* target = element_by_id(doc, target_id)) {
                    const std::string_view action =
                        trigger->get_attribute("popovertargetaction");
                    if (action == "show") popover_show(doc, *target);
                    else if (action == "hide") popover_hide(doc, *target);
                    else if (target->has_attribute("data-popover-open")) {
                        popover_hide(doc, *target);
                    } else {
                        popover_show(doc, *target);
                    }
                }
            } else if (!doc->popovers.empty()) {
                const Element* top = doc->popovers.back();
                if (popover_is_auto(*top) && !is_within(hit, top)) {
                    popover_hide(doc, const_cast<Element&>(*top));
                }
            }
            // A click on a <details>'s own <summary> opens or closes it. The
            // UA sheet already hides the body of a closed one and shows an
            // open one's, so toggling the attribute is the whole behaviour --
            // without it the port styled <details> perfectly and it never
            // opened.
            //
            // Only a <summary> that is a DIRECT child counts, so a click
            // inside the open body does not collapse it, and a nested
            // <details> is toggled by its own summary rather than its
            // parent's.
            if (Element* details = details_for_summary_click(hit)) {
                if (details->has_attribute("open")) details->remove_attribute("open");
                else details->set_attribute("open", "");
                // `display` changes on the children, so the boxes go, not
                // just the paint.
                doc->pending = worst(doc->pending, Invalidation::Boxes);
                doc->queue_event(WEVA_EVENT_TOGGLE, details, x, y, buttons);
            }
            // A checkbox toggles on the click, not the press, so dragging off
            // it and back changes nothing -- as it does not in any toolkit.
            //
            // A click on a <label> works the control it is for, which is why
            // the word beside a checkbox is clickable in every UI. The port
            // had no <label> handling at all, so `<label><input
            // type=checkbox> Shield</label>` -- the shape in this repo's own
            // demo -- only responded to a hit on the 13px box itself.
            if (Element* labelled = label_target_for_click(doc, hit)) {
                // Focus follows the click, as it does for a direct one, so
                // the keyboard lands on what the label named.
                weva_document_set_focus(doc, doc->handle_of(labelled));
                // A checkbox or radio toggles; a slider does NOT move. Its
                // value comes from where along its track the pointer landed,
                // and the pointer landed on the label -- a browser focuses the
                // slider and leaves the value alone, so this does too. (The
                // reference forwards a synthetic click with no coordinates,
                // which drives the slider to its minimum; that is a bug, and
                // matching it would be the wrong kind of parity.)
                if (input_type_of(*labelled) != "range") {
                    activate_control(doc, *labelled, x);
                }
            } else {
                activate_control(doc, const_cast<Element&>(*hit), x);
            }
        }
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
    note_state_change(doc, old_hover, hover);
    note_state_change(doc, old_active, active);
}

void weva_document_clear_pointer(weva_document_t doc) {
    if (!doc) return;
    InteractionState& st = doc->styles.state;
    const std::vector<const Element*> old_hover = st.hover_chain;
    const std::vector<const Element*> old_active = st.active_chain;
    st.hover_chain.clear();
    st.active_chain.clear();
    note_state_change(doc, old_hover, st.hover_chain);
    note_state_change(doc, old_active, st.active_chain);
}

namespace {

// Whether an element can take focus, and where it sits in tab order.
//
// HTML's rule, which is not the one most people expect: a POSITIVE tabindex
// comes first, in numeric order, and everything else follows in document
// order. `tabindex="-1"` is focusable by script but skipped by Tab.
bool focus_order_of(const Element& e, int* order) {
    const std::string_view raw = e.get_attribute("tabindex");
    if (!raw.empty()) {
        const std::string text(raw);
        char* end = nullptr;
        const long v = std::strtol(text.c_str(), &end, 10);
        if (end != text.c_str() && *end == '\0') {
            if (v < 0) return false;   // reachable by script, not by Tab
            *order = static_cast<int>(v);
            return true;
        }
    }
    // Focusable by nature. An <a> only counts with an href, as in HTML.
    const std::string_view tag = e.tag_name();
    if (tag == "button" || tag == "input" || tag == "select" || tag == "textarea") {
        *order = 0;
        return true;
    }
    if (tag == "a" && !e.get_attribute("href").empty()) {
        *order = 0;
        return true;
    }
    return false;
}

bool focus_disabled(const Element& e, const StyleMap& styles) {
    if (e.has_attribute("disabled")) return true;
    auto it = styles.by_element.find(&e);
    if (it == styles.by_element.end()) return true;   // no box, no focus
    const std::string_view display = it->second->get("display");
    if (display == "none") return true;
    return it->second->get("visibility") == "hidden";
}

void collect_focusables(const Node& n, const StyleMap& styles,
                        std::vector<std::pair<int, const Element*>>* out) {
    for (const Ref<Node>& c : n.children()) {
        if (c->node_type() != NodeType::Element) continue;
        const auto& e = static_cast<const Element&>(*c);
        int order = 0;
        if (focus_order_of(e, &order) && !focus_disabled(e, styles)) {
            out->emplace_back(order, &e);
        }
        collect_focusables(e, styles, out);
    }
}

}   // namespace

weva_element_t weva_document_focus_next(weva_document_t doc, int backwards) {
    if (!doc || !doc->doc) return WEVA_ELEMENT_NONE;
    std::vector<std::pair<int, const Element*>> found;
    collect_focusables(*doc->doc, doc->styles, &found);
    if (found.empty()) return WEVA_ELEMENT_NONE;
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
    const Element* current = doc->styles.state.focused;
    size_t index = 0;
    bool have = false;
    for (size_t i = 0; i < found.size(); ++i) {
        if (found[i].second == current) { index = i; have = true; break; }
    }
    size_t next = 0;
    if (!have) {
        next = backwards ? found.size() - 1 : 0;
    } else if (backwards) {
        next = index == 0 ? found.size() - 1 : index - 1;
    } else {
        next = index + 1 >= found.size() ? 0 : index + 1;
    }
    const weva_element_t handle = doc->handle_of(found[next].second);
    weva_document_set_focus(doc, handle);
    return handle;
}

int weva_document_key(weva_document_t doc, int key, uint32_t modifiers, int down) {
    if (!doc) return 0;
    weva_event e{};
    e.kind = down ? WEVA_EVENT_KEY_DOWN : WEVA_EVENT_KEY_UP;
    e.target = doc->handle_of(doc->styles.state.focused);
    e.key = key;
    e.modifiers = modifiers;
    weva_document::fill_handler(&e, doc->styles.state.focused);
    if (doc->events.size() >= weva_document::kMaxEvents) doc->events.pop_front();
    doc->events.push_back(e);

    // Editing keys in a focused field. No host can do these for itself: where
    // the text is kept and where the cursor sits are both the document's.
    if (down) {
        Element* focused = const_cast<Element*>(doc->styles.state.focused);
        if (focused && is_text_field(*focused)) {
            InteractionState& st = doc->styles.state;
            std::string value = field_value(*focused);
            const bool multiline = focused->tag_name() == "textarea";
            int caret = std::min(static_cast<int>(value.size()), std::max(0, st.caret));
            // Character boundaries, not byte ones: a continuation byte belongs
            // to the codepoint that owns it, and a cursor between them is not
            // a position at all.
            const auto prev = [&](int i) {
                if (i <= 0) return 0;
                int n = i - 1;
                while (n > 0 && (static_cast<unsigned char>(value[static_cast<size_t>(n)]) & 0xC0) ==
                                    0x80) {
                    --n;
                }
                return n;
            };
            const auto next = [&](int i) {
                const int end = static_cast<int>(value.size());
                if (i >= end) return end;
                int n = i + 1;
                while (n < end && (static_cast<unsigned char>(value[static_cast<size_t>(n)]) &
                                   0xC0) == 0x80) {
                    ++n;
                }
                return n;
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
                        const int from = by_word ? word_left(caret) : prev(caret);
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
                case WEVA_KEY_LEFT: caret = by_word ? word_left(caret) : prev(caret); break;
                case WEVA_KEY_RIGHT: caret = by_word ? word_right(caret) : next(caret); break;
                case WEVA_KEY_HOME:
                    // Ctrl+Home is the top of the whole field, not the start
                    // of the line the caret happens to be on.
                    caret = (multiline && !by_word) ? line_start(value, caret) : 0;
                    break;
                case WEVA_KEY_END:
                    caret = (multiline && !by_word) ? line_end(value, caret)
                                                    : static_cast<int>(value.size());
                    break;
                case WEVA_KEY_ENTER:
                    // The one key that means something different in a box you
                    // can write paragraphs in. In a one-line field it submits
                    // the form around it, if there is one -- which is what
                    // Enter has meant in a login box since forms existed.
                    if (!multiline) {
                        if (Element* form = form_of(focused)) {
                            doc->queue_event(WEVA_EVENT_SUBMIT, form, 0, 0, 0);
                            st.caret = caret;
                            return 1;
                        }
                        caret = -1;
                        break;
                    }
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
                    const int start = line_start(value, caret);
                    const int column = caret - start;
                    if (key == WEVA_KEY_UP) {
                        if (start == 0) { caret = 0; break; }
                        const int above = line_start(value, start - 1);
                        caret = std::min(above + column, start - 1);
                    } else {
                        const int end = line_end(value, caret);
                        if (end >= static_cast<int>(value.size())) {
                            caret = static_cast<int>(value.size());
                            break;
                        }
                        const int below = end + 1;
                        caret = std::min(below + column, line_end(value, below));
                    }
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

    // Escape closes the topmost auto popover, one per press -- so a submenu
    // closes before the menu it came from. Checked before the open <select>,
    // because a popover opened over a dropdown is what the user sees.
    if (down && key == WEVA_KEY_ESCAPE && !doc->popovers.empty()) {
        if (popover_hide_top_auto(doc)) return 1;
    }
    // An open list takes the keys before anything else: the arrows walk it,
    // Enter takes what is highlighted, Escape leaves it as it was.
    if (down && doc->open_select) {
        Element& select = const_cast<Element&>(*doc->open_select);
        const int count = static_cast<int>(select_options(select).size());
        switch (key) {
            case WEVA_KEY_UP:
            case WEVA_KEY_DOWN: {
                if (count == 0) return 1;
                const int step = key == WEVA_KEY_DOWN ? 1 : -1;
                const int from = doc->highlighted_option < 0 ? chosen_index(select)
                                                             : doc->highlighted_option;
                doc->highlighted_option = std::clamp(from + step, 0, count - 1);
                reveal_highlighted_option(doc);
                doc->pending = worst(doc->pending, Invalidation::Paint);
                return 1;
            }
            case WEVA_KEY_HOME:
                doc->highlighted_option = 0;
                reveal_highlighted_option(doc);
                doc->pending = worst(doc->pending, Invalidation::Paint);
                return 1;
            case WEVA_KEY_END:
                doc->highlighted_option = count - 1;
                reveal_highlighted_option(doc);
                doc->pending = worst(doc->pending, Invalidation::Paint);
                return 1;
            case WEVA_KEY_ENTER:
            case WEVA_KEY_SPACE:
                if (doc->highlighted_option >= 0) choose_option(doc, select, doc->highlighted_option);
                doc->open_select = nullptr;
                doc->highlighted_option = -1;
                doc->pending = worst(doc->pending, Invalidation::Boxes);
                return 1;
            case WEVA_KEY_ESCAPE:
            case WEVA_KEY_TAB:
                // Escape leaves the value alone, which is the difference
                // between cancelling and choosing.
                doc->open_select = nullptr;
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
        if (focused && !doc->open_select && focused->tag_name() == "select" &&
            !focused->has_attribute("multiple") && !focused->has_attribute("size")) {
            Element& select = const_cast<Element&>(*focused);
            const int count = static_cast<int>(select_options(select).size());
            if (key == WEVA_KEY_ENTER || key == WEVA_KEY_SPACE) {
                doc->open_select = &select;
                doc->highlighted_option = chosen_index(select);
                doc->select_first_row = 0;
                reveal_highlighted_option(doc);
                doc->pending = worst(doc->pending, Invalidation::Paint);
                return 1;
            }
            if ((key == WEVA_KEY_UP || key == WEVA_KEY_DOWN) && count > 0) {
                const int step = key == WEVA_KEY_DOWN ? 1 : -1;
                choose_option(doc, select, std::clamp(chosen_index(select) + step, 0, count - 1));
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
    if (!doc || !utf8 || !*utf8) return;
    // Typing into a focused field edits it. Control characters are not text,
    // whatever a platform hands over for Enter or Tab.
    const unsigned char lead = static_cast<unsigned char>(utf8[0]);
    Element* focused = const_cast<Element*>(doc->styles.state.focused);
    if (focused && lead >= 0x20 && lead != 0x7f && is_text_field(*focused)) {
        std::string value = field_value(*focused);
        InteractionState& st = doc->styles.state;
        // Typing over a selection replaces it, which is what every text box
        // does and the reason select-all-then-type works.
        const Selection sel = selection_of(st, value.size());
        // Plain typing coalesces; typing that replaces a selection does not,
        // since undoing it has to bring the replaced text back on its own.
        push_undo(doc, *focused, value, st.caret, st.anchor, sel.empty());
        if (!sel.empty()) {
            value.erase(static_cast<size_t>(sel.from), static_cast<size_t>(sel.to - sel.from));
            st.caret = sel.from;
        }
        st.anchor = -1;
        const size_t at = std::min(static_cast<size_t>(std::max(0, st.caret)), value.size());
        value.insert(at, utf8);
        st.caret = static_cast<int>(at + std::strlen(utf8));
        st.caret_age = 0;
        doc->caret_follow = true;
        set_field_value(doc, *focused, value);
        note_value_change(doc, *focused, value);
    }
    weva_event e{};
    e.kind = WEVA_EVENT_TEXT_INPUT;
    e.target = doc->handle_of(doc->styles.state.focused);
    weva_document::fill_handler(&e, doc->styles.state.focused);
    const size_t n = std::strlen(utf8);
    const size_t copy = n < sizeof(e.text) - 1 ? n : sizeof(e.text) - 1;
    std::memcpy(e.text, utf8, copy);
    e.text[copy] = '\0';
    if (doc->events.size() >= weva_document::kMaxEvents) doc->events.pop_front();
    doc->events.push_back(e);
}

int weva_document_open_select(weva_document_t doc, weva_element_t element) {
    if (!doc) return 0;
    if (element == WEVA_ELEMENT_NONE) {
        doc->open_select = nullptr;
        doc->highlighted_option = -1;
        doc->pending = worst(doc->pending, Invalidation::Paint);
        return 1;
    }
    Element* e = doc->element_at(element);
    if (!e || e->tag_name() != "select") return 0;
    doc->open_select = e;
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

// The host's callback, wearing the interface the substitution wants.
class AbiBindingResolver : public BindingResolver {
public:
    explicit AbiBindingResolver(const weva_binding_source& source) : source_(source) {}

    bool resolve(std::string_view path, std::string* out) const override {
        if (!source_.value) return false;
        const std::string key(path);
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
        std::vector<char> heap(n + 1, 0);
        source_.value(source_.user, key.c_str(), heap.data(), heap.size(), &found);
        out->assign(heap.data(), n);
        return found != 0;
    }

    int count(std::string_view path) const override {
        if (!source_.count) return -1;
        return source_.count(source_.user, std::string(path).c_str());
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
    const int changed = apply_bindings(*doc->doc, resolver, &doc->binding_templates,
                                      &doc->binding_repeats);
    if (changed > 0) {
        // A repeat makes elements AND destroys them -- a rebuilt list drops
        // every row it had. The new ones need handles, or a script cannot
        // address them; the gone ones must lose theirs and everything else
        // keyed on the pointer, or a later query walks into freed memory.
        // UBSan found exactly that, as an invalid vptr inside the selector
        // matcher.
        std::set<const Element*> live;
        doc->collect_live(&live);
        for (size_t i = 0; i < doc->elements.size(); ++i) {
            const Element* e = doc->elements[i];
            if (e && !live.count(e)) forget_element(doc, e);
        }
        doc->reindex_new_elements();
        // Text that changed changes what boxes exist; an attribute that
        // changed can change what matches. Both are the same tier a host's own
        // set_text and set_attribute ask for.
        doc->pending = worst(doc->pending, Invalidation::Boxes);
        doc->touched.clear();
        doc->dom_touched = false;
    }
    return changed;
}

int weva_document_select_word_at(weva_document_t doc, double x, double y) {
    if (!doc) return 0;
    const Element* hit = element_at_point(doc->tree, doc->root, x, y);
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
    doc->pending = worst(doc->pending, Invalidation::Paint);
    return 1;
}

namespace {

// Undo and redo are the same move in opposite directions: take the top of one
// stack, put what the field holds now on the other, and adopt what was taken.
int step_history(weva_document* doc, bool undoing) {
    if (!doc) return 0;
    InteractionState& st = doc->styles.state;
    if (!st.focused || !is_text_field(*st.focused)) return 0;
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
    doc->pending = worst(doc->pending, Invalidation::Paint);
    return 1;
}

}   // namespace

int weva_document_undo(weva_document_t doc) { return step_history(doc, true); }

int weva_document_redo(weva_document_t doc) { return step_history(doc, false); }

int weva_document_select_all(weva_document_t doc) {
    if (!doc) return 0;
    InteractionState& st = doc->styles.state;
    if (!st.focused || !is_text_field(*st.focused)) return 0;
    st.anchor = 0;
    st.caret = static_cast<int>(field_value(*st.focused).size());
    st.caret_age = 0;
    doc->caret_follow = true;
    doc->pending = worst(doc->pending, Invalidation::Paint);
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
    // Only the focused field has one: the cursor and its anchor belong to
    // whatever is being typed into, not to every field on the page.
    const bool mine = st.focused == e;
    const int caret = mine ? st.caret : 0;
    if (out_start) *out_start = mine && st.anchor >= 0 ? st.anchor : caret;
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
    if (st.focused != e) weva_document_set_focus(doc, element);
    const int length = static_cast<int>(field_value(*e).size());
    st.caret = std::clamp(end, 0, length);
    st.anchor = start == end ? -1 : std::clamp(start, 0, length);
    st.caret_age = 0;
    doc->caret_follow = true;
    doc->pending = worst(doc->pending, Invalidation::Paint);
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
        value = e->has_attribute("checked") ? "on" : "";
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
                value = std::string(chosen.get_attribute("value"));
                if (value.empty()) value = trimmed(text_content_of(chosen));
            }
        }
    } else {
        value = field_value(*e);
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

weva_status weva_element_set_value(weva_document_t doc, weva_element_t element,
                                   const char* value) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    Element* e = doc->element_at(element);
    if (!e) return WEVA_ERR_NOT_FOUND;
    const std::string_view v(value ? value : "");
    // A script replacing the value invalidates the undo stack: it describes a
    // field that no longer holds what it described, and putting one of its
    // snapshots back would silently discard what the script just wrote.
    doc->history.erase(e);
    const std::string type = input_type_of(*e);
    if (e->tag_name() == "input" && (type == "checkbox" || type == "radio")) {
        const bool on = !v.empty() && v != "0" && v != "off" && v != "false";
        if (on) e->set_attribute("checked", "");
        else e->remove_attribute("checked");
    } else {
        set_field_value(doc, *e, v);
    }
    doc->dom_touched = true;
    if (doc->touched.size() < 64) doc->touched.push_back(e);
    return WEVA_OK;
}

int weva_document_poll_event(weva_document_t doc, weva_event* out) {
    if (!doc || !out || doc->events.empty()) return 0;
    *out = doc->events.front();
    doc->events.pop_front();
    return 1;
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
    InteractionState& st = doc->styles.state;
    const Element* target = nullptr;
    if (element != WEVA_ELEMENT_NONE) {
        target = doc->element_at(element);
        if (!target) return WEVA_ERR_NOT_FOUND;
        // A disabled control cannot be focused, in a browser or here, so a
        // host cannot put the keyboard somewhere the user could not.
        if (disabled_ancestor(target)) return WEVA_ERR_INVALID_ARGUMENT;
    }
    if (st.focused == target) return WEVA_OK;
    const Element* previous_focus = st.focused;
    // A field you have just focused puts the cursor after what it holds, which
    // is where a user expects to carry on typing.
    // A text field that is losing the focus commits what it holds, if what it
    // holds has moved. Nothing else can tell an edit from a visit.
    if (previous_focus && is_text_field(*previous_focus) &&
        field_value(*previous_focus) != doc->value_at_focus) {
        doc->queue_event(WEVA_EVENT_CHANGE, previous_focus, 0, 0, 0);
    }
    doc->value_at_focus = target && is_text_field(*target) ? field_value(*target) : std::string();
    st.caret = target ? static_cast<int>(field_value(*target).size()) : 0;
    st.anchor = -1;
    st.caret_age = 0;
    doc->caret_follow = target != nullptr;
    const Element* previous = previous_focus;
    if (previous) doc->queue_event(WEVA_EVENT_BLUR, previous, 0, 0, 0);
    if (target) doc->queue_event(WEVA_EVENT_FOCUS, target, 0, 0, 0);
    const std::vector<const Element*> old_chain = st.focus_chain;
    st.focused = target;
    // :focus-within is the ancestors; the element itself carries :focus.
    InteractionState::chain_of(target, &st.focus_chain);
    std::vector<const Element*> changed = old_chain;
    if (previous) changed.push_back(previous);
    note_state_change(doc, changed, st.focus_chain);
    // The focused element's own bits changed even when the chain did not.
    if (previous || target) {
        ++st.version_;
        doc->dom_touched = true;
        for (const Element* e : {previous, target}) {
            if (e && doc->touched.size() < 64) doc->touched.push_back(const_cast<Element*>(e));
        }
    }
    // A tab that lands off screen brings its target into view, or keyboard
    // navigation walks into a list and appears to go nowhere.
    if (target) bring_box_into_view(doc, box_of(doc, target));
    return WEVA_OK;
}

int weva_document_scroll(weva_document_t doc, double x, double y, double dx, double dy) {
    if (!doc || (dx == 0 && dy == 0)) return 0;
    // A wheel over an open list moves the LIST. It sits above the document, so
    // scrolling the page under it would move the wrong thing.
    if (doc->open_select && select_row_at(doc, x, y) >= 0) {
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
    for (BoxId id = box_at_point(doc->tree, doc->root, x, y); id != kNoBox;
         id = doc->tree[id].parent) {
        const Box& b = doc->tree[id];
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
    for (int i = 0; i < doc->tree.size(); ++i) {
        const Box& b = doc->tree[i];
        if (b.element != e) continue;
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
    doc->styles.forget(e);
    doc->scroll.erase(e);
    if (doc->press_target == e) doc->press_target = nullptr;
    if (doc->styles.state.focused == e) {
        doc->styles.state.focused = nullptr;
        doc->styles.state.caret = 0;
    }
    if (doc->caret_painted.element == e) doc->caret_painted = CaretState{};
    if (doc->scroll_drag.element == e) doc->scroll_drag = weva_document::ScrollDrag{};
    if (doc->open_select == e) {
        doc->open_select = nullptr;
        doc->highlighted_option = -1;
    }
    doc->binding_templates.erase(e);
    doc->binding_repeats.erase(e);
    doc->history.erase(e);
    doc->popovers.erase(std::remove(doc->popovers.begin(), doc->popovers.end(), e),
                        doc->popovers.end());
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
    for (size_t i = 0; i < doc->elements.size(); ++i) {
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
    if (value) e->set_attribute(name, value);
    else e->remove_attribute(name);
    // What it reaches is the cascade's business; that it must run is this
    // function's. The selector match cache is keyed on element shape, so the
    // change lands on a different entry without being invalidated here.
    doc->dom_touched = true;
    if (doc->touched.size() < 64) doc->touched.push_back(e);
    return WEVA_OK;
}

weva_status weva_element_set_text(weva_document_t doc, weva_element_t element,
                                  const char* text) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    Element* e = doc->element_at(element);
    if (!e) return WEVA_ERR_NOT_FOUND;
    replace_text(*e, text ? text : "");
    // The DOM changed, so which boxes exist may have too -- a row that was
    // empty now has a line in it. The ELEMENTS did not change, though, so the
    // styles keyed on them stand: dropping them would also drop every
    // transition mid-flight, and a value bound to a label updating each frame
    // would cancel the animation next to it.
    doc->pending = Invalidation::Boxes;
    return WEVA_OK;
}

weva_status weva_element_show_popover(weva_document_t doc, weva_element_t element) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    Element* e = doc->element_at(element);
    if (!e || !e->has_attribute("popover")) return WEVA_ERR_NOT_FOUND;
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
    if (e->has_attribute("data-popover-open")) popover_hide(doc, *e);
    else popover_show(doc, *e);
    return WEVA_OK;
}

weva_status weva_element_show_dialog(weva_document_t doc, weva_element_t element, int modal) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    Element* e = doc->element_at(element);
    if (!e || e->tag_name() != "dialog") return WEVA_ERR_NOT_FOUND;
    e->set_attribute("open", "");
    // `data-modal` is what puts it in the top layer, so a dialog reopened
    // non-modally after a modal show must lose it -- otherwise the backdrop
    // outlives the modality that asked for it.
    if (modal) e->set_attribute("data-modal", "");
    else e->remove_attribute("data-modal");
    // The backdrop is a BOX, so this is a box-level change, not a repaint.
    doc->pending = worst(doc->pending, Invalidation::Boxes);
    return WEVA_OK;
}

weva_status weva_element_close_dialog(weva_document_t doc, weva_element_t element) {
    if (!doc) return WEVA_ERR_INVALID_ARGUMENT;
    Element* e = doc->element_at(element);
    if (!e || e->tag_name() != "dialog") return WEVA_ERR_NOT_FOUND;
    const bool was_open = e->has_attribute("open");
    e->remove_attribute("open");
    e->remove_attribute("data-modal");
    doc->pending = worst(doc->pending, Invalidation::Boxes);
    if (was_open) doc->queue_event(WEVA_EVENT_TOGGLE, e, 0, 0, 0);
    return WEVA_OK;
}

int weva_element_row(weva_document_t doc, weva_element_t element, int* out_index,
                     char* key_buffer, size_t key_capacity) {
    if (key_buffer && key_capacity > 0) key_buffer[0] = ' ';
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
            key_buffer[n_copy] = ' ';
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

} // extern "C"
