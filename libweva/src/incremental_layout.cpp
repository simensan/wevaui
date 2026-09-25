#include "weva/incremental_layout.h"
#include "weva/inline_layout.h"
#include "weva/positioning.h"
#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <chrono>

namespace weva {
namespace {
// Aggregate opt-in modal diagnostics in memory; print once at thread teardown.
// No clock reads or retained trace storage are used by ordinary updates.
struct ModalTrace {
    double phases[8]{};
    double total = 0;
    size_t attempts = 0, accepted = 0;
    ~ModalTrace() {
        std::fprintf(stderr, "WEVA_MODAL_TRACE attempts=%zu accepted=%zu total_ms=%.6f\n", attempts, accepted, total);
        const char* names[] = {"safety", "eligibility", "build", "flow", "positioning_overflow", "validate", "splice", "index"};
        for (size_t i = 0; i < 8; ++i)
            std::fprintf(stderr, "WEVA_MODAL_PHASE %s %.6f\n", names[i], phases[i]);
    }
};
ModalTrace* modal_trace() {
    static const bool enabled = std::getenv("WEVA_MODAL_TRACE") != nullptr;
    if (!enabled) return nullptr;
    thread_local ModalTrace trace;
    return &trace;
}
struct ModalSample {
    using Clock = std::chrono::steady_clock;
    ModalTrace* trace = modal_trace();
    Clock::time_point start = trace ? Clock::now() : Clock::time_point{};
    Clock::time_point previous = start;
    bool accepted = false;
    void lap(size_t phase) {
        if (!trace) return;
        const auto now = Clock::now();
        trace->phases[phase] += std::chrono::duration<double, std::milli>(now - previous).count();
        previous = now;
    }
    ~ModalSample() {
        if (!trace) return;
        ++trace->attempts;
        trace->accepted += accepted;
        trace->total += std::chrono::duration<double, std::milli>(Clock::now() - start).count();
    }
};
bool names_anchor(const ComputedStyle* style) {
    static const int anchor_name = CssPropertyRegistry::instance().id_of("anchor-name");
    if (!style) return false;
    const auto name = style->get(anchor_name);
    return !name.empty() && name != "none";
}
bool ordinary(const Box& b) {
    return (b.display == DisplayKind::Block || b.display == DisplayKind::FlowRoot) &&
           !b.contains_inlines && !b.is_multicol && !b.is_inline_block;
}
bool within(const BoxTree& tree, BoxId child, BoxId ancestor) {
    for (; child != kNoBox; child = tree[child].parent) if (child == ancestor) return true;
    return false;
}
bool same_outer(const Box& a, const Box& b) {
    return a.width == b.width && a.height == b.height &&
           a.margin_top == b.margin_top && a.margin_right == b.margin_right &&
           a.margin_bottom == b.margin_bottom && a.margin_left == b.margin_left;
}
std::pair<double, double> baselines(const BoxTree& tree, BoxId id) {
    std::pair<double, double> out{tree[id].height, tree[id].height};
    bool first = true;
    for (BoxId c : tree.children(id)) {
        if (tree[c].kind != BoxKind::Line) continue;
        const double baseline = tree[c].y + tree[c].baseline;
        if (first) out.first = baseline;
        out.second = baseline;
        first = false;
    }
    return out;
}

bool independent_height(const BoxTree& tree, BoxId id, const LayoutContext& ctx) {
    const Box& b = tree[id];
    if (!b.style || b.kind != BoxKind::Block) return true;
    for (const char* property : {"height", "min-height", "max-height"}) {
        if (b.style->get(property).find('%') == std::string_view::npos) continue;
        // Percentages below a definite local parent (a 100%-high bar inside
        // a 6px track) are independent of the retained grid's incoming height.
        if (b.parent == kNoBox || !tree[b.parent].style) return false;
        const Box& parent = tree[b.parent];
        const auto basis = resolve_length(parent.style, "height", ctx,
                                           parent.font_size > 0 ? parent.font_size : ctx.root_font_size_px,
                                           std::nullopt);
        if (basis.kind != LengthKind::Length) return false;
    }
    return true;
}

class RetainedGridProbe final : public BoxBuildReuse, public LayoutReuse {
public:
    RetainedGridProbe(const BoxTree& source, StyleProvider* styles,
                      const std::unordered_map<const Element*, BoxId>& eligible)
        : source_(source), styles_(styles), eligible_(eligible) {}

    bool reuse_children(BoxTree* tree, BoxId id) override {
        Box& b = (*tree)[id];
        const auto found = eligible_.find(b.element);
        if (found == eligible_.end()) return false;
        b.retained_from = found->second;
        return true;
    }
    bool reuse_layout(BoxTree* tree, BoxId id) override {
        Box& b = (*tree)[id];
        if (b.retained_from == kNoBox) return false;
        const Box& cached = source_[b.retained_from];
        if (b.width == cached.width && !b.cross_size_imposed &&
            b.padding_top == cached.padding_top && b.padding_right == cached.padding_right &&
            b.padding_bottom == cached.padding_bottom && b.padding_left == cached.padding_left &&
            b.border_top == cached.border_top && b.border_right == cached.border_right &&
            b.border_bottom == cached.border_bottom && b.border_left == cached.border_left) {
            b.height = cached.height;
            b.grid_min_content = cached.grid_min_content;
            b.grid_max_content = cached.grid_max_content;
            b.vis_x0 = cached.vis_x0; b.vis_y0 = cached.vis_y0;
            b.vis_x1 = cached.vis_x1; b.vis_y1 = cached.vis_y1;
            b.scroll_x = cached.scroll_x; b.scroll_y = cached.scroll_y;
            return true;
        }
        b.retained_from = kNoBox;
        // The parent's new allocation changed the input. Rebuild now, before
        // ordinary layout observes the deferred root's empty child list.
        BoxBuilder builder(tree, styles_, nullptr, true);
        builder.materialize_children(id);
        return false;
    }
private:
    const BoxTree& source_;
    StyleProvider* styles_;
    const std::unordered_map<const Element*, BoxId>& eligible_;
};
}

void IncrementalLayout::invalidate_subtree(const BoxTree& tree, BoxId id) {
    input_versions_[id] = input_serial_;
    for (BoxId c : tree.children(id)) invalidate_subtree(tree, c);
}

void IncrementalLayout::refresh_grid(const BoxTree& tree, BoxId id) {
    const Box& b = tree[id];
    grid_versions_[id] = 0;
    if (!local_[id] || !height_independent_[id] || sizes_[id] < 32 ||
        !b.element || !b.style || b.display != DisplayKind::Grid || b.is_inline_block ||
        b.parent == kNoBox || !ordinary(tree[b.parent]) || b.style->get("height") != "auto" ||
        b.style->get("aspect-ratio") != "auto" ||
        b.style->get("grid-template-columns").find("subgrid") != std::string_view::npos ||
        b.style->get("grid-template-rows").find("subgrid") != std::string_view::npos) return;
    grid_versions_[id] = input_versions_[id];
    if (!paint_root_flags_[id]) { paint_root_flags_[id] = true; paint_roots_.push_back(id); }
    if (std::find(grid_roots_.begin(), grid_roots_.end(), id) == grid_roots_.end())
        grid_roots_.push_back(id);
}

int IncrementalLayout::index_subtree(const BoxTree& tree, BoxId id, const LayoutContext& ctx) {
    if (preserve_ && std::find(preserve_->begin(), preserve_->end(), id) != preserve_->end())
        return sizes_[id];
    const Box& b = tree[id];
    if (b.position == PositionType::Absolute || b.position == PositionType::Fixed)
        if (!paint_root_flags_[id]) { paint_root_flags_[id] = true; paint_roots_.push_back(id); }
    input_versions_[id] = input_serial_;
    if (b.style) {
        auto entry = by_style_.try_emplace(b.style, id);
        if (entry.first->second == kNoBox) entry.first->second = id;
    }
    if (b.element) {
        auto entry = by_element_.try_emplace(b.element, id);
        if (entry.first->second == kNoBox) entry.first->second = id;
    }
    if (b.is_float() || b.position == PositionType::Sticky || names_anchor(b.style))
        global_dependencies_ = true;
    bool local = !b.pseudo_host && b.display != DisplayKind::ListItem && !b.is_multicol &&
                 !is_table_display(b.display) && b.position == PositionType::Static && !b.is_float();
    int size = 1;
    bool independent = independent_height(tree, id, ctx);
    for (BoxId c : tree.children(id)) {
        size += index_subtree(tree, c, ctx);
        local = local && local_[c];
        independent = independent && height_independent_[c];
    }
    sizes_[id] = size;
    local_[id] = local;
    height_independent_[id] = independent;
    if (local && b.element && b.kind == BoxKind::Block)
        contributions_[id] = {block_intrinsic_contribution(tree, id, &ctx, true),
                              block_intrinsic_contribution(tree, id, &ctx, false)};
    refresh_grid(tree, id);
    return size;
}

void IncrementalLayout::index(const BoxTree& tree, BoxId root, const LayoutContext& ctx,
                              bool reset_identities) {
    retained_.clear();
    if (reset_identities) {
        by_style_.clear();
        by_element_.clear();
    } else {
        // Layout alone preserves style/element identities. Reuse map nodes,
        // while marking boxes removed by line clamping as absent this pass.
        for (auto& entry : by_style_) entry.second = kNoBox;
        for (auto& entry : by_element_) entry.second = kNoBox;
    }
    sizes_.assign(tree.size(), 0);
    local_.assign(tree.size(), false);
    contributions_.resize(tree.size());
    height_independent_.assign(tree.size(), false);
    input_versions_.assign(tree.size(), ++input_serial_);
    grid_versions_.assign(tree.size(), 0);
    grid_roots_.clear();
    paint_roots_.clear();
    paint_root_flags_.assign(tree.size(), false);
    preserve_ = nullptr;
    global_dependencies_ = false;
    if (tree.valid(root)) {
        const IntrinsicContributionScope memo(tree, &ctx);
        index_subtree(tree, root, ctx);
    }
}

void IncrementalLayout::unindex_subtree(const BoxTree& tree, BoxId id) {
    if (preserve_ && std::find(preserve_->begin(), preserve_->end(), id) != preserve_->end()) return;
    grid_versions_[id] = 0;
    const auto found = by_style_.find(tree[id].style);
    if (found != by_style_.end() && found->second == id) by_style_.erase(found);
    const auto element = by_element_.find(tree[id].element);
    if (element != by_element_.end() && element->second == id) by_element_.erase(element);
    for (BoxId c : tree.children(id)) unindex_subtree(tree, c);
}

bool IncrementalLayout::update(BoxTree* tree, BoxId root, StyleProvider* styles,
        const LayoutContext& ctx, const FontMetrics* metrics,
        const std::vector<std::pair<const ComputedStyle*, Invalidation>>& changes) {
    replaced_.clear();
    retained_.clear();
    retained_grids_ = 0;
    static const bool log = std::getenv("WEVA_LAYOUT_LOG") != nullptr;
    const auto reject = [&](const char* reason, BoxId id = kNoBox) {
        if (log) {
            std::string_view tag = "", classes = "";
            if (tree->valid(id) && (*tree)[id].element) {
                tag = (*tree)[id].element->tag_name();
                classes = (*tree)[id].element->get_attribute("class");
            }
            std::fprintf(stderr, "  layout probe: %s box=%u <%.*s class='%.*s'> changes=%zu\n",
                         reason, static_cast<unsigned>(id), static_cast<int>(tag.size()), tag.data(),
                         static_cast<int>(classes.size()), classes.data(), changes.size());
        }
        return false;
    };
    // An unchanged positioned ancestor/sibling is safe when this subtree's
    // exports stay equal. Positioned descendants are excluded by local_.
    // Floats, sticky constraints and named anchors can cross that boundary.
    if (global_dependencies_) return reject("float/sticky/anchor dependency");
    if (changes.empty() || changes.size() > 16 || !tree->valid(root))
        return reject("change count/root");
    ++input_serial_;
    for (const auto& change : changes) {
        const auto found = by_style_.find(change.first);
        if (found == by_style_.end() || !tree->valid(found->second)) continue;
        invalidate_subtree(*tree, found->second);
        for (BoxId p = (*tree)[found->second].parent; p != kNoBox; p = (*tree)[p].parent)
            input_versions_[p] = input_serial_;
    }
    std::unordered_map<const Element*, BoxId> eligible;
    for (BoxId id : grid_roots_) {
        if (!tree->valid(id) || !grid_versions_[id] || grid_versions_[id] != input_versions_[id]) continue;
        bool covered = false;
        for (BoxId p = (*tree)[id].parent; p != kNoBox; p = (*tree)[p].parent)
            if (grid_versions_[p] && grid_versions_[p] == input_versions_[p]) { covered = true; break; }
        if (!covered) eligible.emplace((*tree)[id].element, id);
    }
    std::vector<BoxId> candidates;
    for (const auto& change : changes) {
        if (change.second < Invalidation::Layout) continue;
        if (names_anchor(change.first)) return reject("new anchor dependency");
        const auto found = by_style_.find(change.first);
        if (found == by_style_.end()) return false;
        BoxId id = found->second;
        while (id != root && id != kNoBox) {
            const Box& b = (*tree)[id];
            if (b.element && b.kind == BoxKind::Block && !b.is_inline_block &&
                b.parent != kNoBox) break;
            id = b.parent;
        }
        if (id == root || id == kNoBox) return false;
        bool covered = false;
        for (BoxId c : candidates) if (within(*tree, id, c)) covered = true;
        if (!covered) {
            candidates.erase(std::remove_if(candidates.begin(), candidates.end(),
                [&](BoxId c) { return within(*tree, c, id); }), candidates.end());
            candidates.push_back(id);
        }
    }
    if (candidates.empty()) return false;

    // All candidates are proved before any splice. A failed probe cannot
    // leave half an update in the retained tree.
    struct Replacement { BoxId into, from; BoxTree tree; std::vector<BoxId> retained; };
    std::vector<Replacement> ready;
    for (BoxId candidate : candidates) {
        bool covered = false;
        for (const auto& r : ready) if (within(*tree, candidate, r.into)) covered = true;
        if (covered) continue;
        bool proved = false;
        for (BoxId id = candidate; id != root && id != kNoBox; id = (*tree)[id].parent) {
            const Box old = (*tree)[id];
            if (!old.element || old.kind != BoxKind::Block || old.is_inline_block ||
                old.parent == kNoBox) continue;
            const Box& outer = (*tree)[old.parent];
            const bool imposed_width = !ordinary(outer);
            if (imposed_width) {
                if (outer.display != DisplayKind::Flex && outer.display != DisplayKind::Grid)
                    continue;
                // An unchanged flex/grid item's allocated width is an input
                // to reflow. If its own sizing declarations changed, only its
                // parent can allocate that width, so promote to the parent.
                bool own_change = false;
                for (const auto& change : changes)
                    if (change.first == old.style && change.second >= Invalidation::Layout)
                        own_change = true;
                if (own_change) continue;
            }
            if (!local_[id]) return reject("nonlocal subtree", id);
            int work_size = sizes_[id];
            for (const auto& item : eligible)
                if (item.second != id && within(*tree, item.second, id)) work_size -= sizes_[item.second] - 1;
            if (work_size > sizes_[root] / 2) return reject("large subtree", id);
            Replacement r;
            r.into = id;
            // Preserve the actual containing-block chain for percentages and
            // inherited font sizes. Its siblings are not layout inputs here.
            std::vector<BoxId> ancestors;
            for (BoxId p = old.parent; p != kNoBox; p = (*tree)[p].parent) ancestors.push_back(p);
            BoxId parent = kNoBox;
            for (auto it = ancestors.rbegin(); it != ancestors.rend(); ++it) {
                const BoxId p = r.tree.create(BoxKind::Block);
                r.tree[p] = (*tree)[*it];
                r.tree[p].parent = r.tree[p].first_child = r.tree[p].last_child = kNoBox;
                r.tree[p].next_sibling = r.tree[p].prev_sibling = kNoBox;
                if (parent != kNoBox) r.tree.append_child(parent, p);
                parent = p;
            }
            RetainedGridProbe reuse(*tree, styles, eligible);
            BoxBuilder builder(&r.tree, styles, &reuse, true);
            r.from = builder.build(*old.element, old.style);
            if (r.from == kNoBox) return false;
            // The retained parent may have blockified an inline-* item.
            // Building that element alone does not perform the parent's step.
            r.tree[r.from].display = old.display;
            r.tree[r.from].is_inline_block = old.is_inline_block;
            r.tree.append_child(parent, r.from);
            BlockLayout block(&r.tree, ctx, metrics, &reuse);
            if (imposed_width) {
                // First measure the parent's sizing input. Imposing the old
                // width immediately can shrink a growing child back to its
                // old used width, hiding the change from the export check.
                // That made a content-box button keep 34px after 11px of
                // padding was added, leaving its centred flex siblings stale.
                block.layout_block(r.from, outer.content_width(), outer.style);
                if (old.parent_layout_input !=
                    measure_parent_layout_input(r.tree, r.from, outer.content_width(), ctx)) {
                    reject("natural intrinsic contribution", id);
                    continue;
                }
                block.relayout_at(r.from, old.width);
                r.tree[r.from].parent_layout_input = old.parent_layout_input;
            } else {
                block.layout_block(r.from, outer.content_width(), outer.style);
            }
            if (!same_outer(old, r.tree[r.from])) { reject("outer geometry", id); continue; }
            if (baselines(*tree, id) != baselines(r.tree, r.from)) { reject("baseline", id); continue; }
            // Dimensions alone do not prove stable intrinsic contributions to
            // a flex/grid ancestor. Compare the exports cached BEFORE styles
            // changed, rather than remeasuring old boxes with new styles.
            if (contributions_[id].first != block_intrinsic_contribution(r.tree, r.from, &ctx, true) ||
                contributions_[id].second != block_intrinsic_contribution(r.tree, r.from, &ctx, false))
                { reject("intrinsic contribution", id); continue; }
            bool safe = true;
            for (BoxId p = old.parent; p != kNoBox; p = (*tree)[p].parent) {
                const Box& a = (*tree)[p];
                if (a.is_inline_block || a.is_multicol || is_table_display(a.display)) {
                    safe = false; break;
                }
            }
            if (!safe) continue;
            r.tree[r.from].x = old.x;
            r.tree[r.from].y = old.y;
            stamp_offsets(&r.tree, r.from, ctx);
            compute_visual_overflow(&r.tree, r.from);
            const auto collect_retained = [&](const auto& self, BoxId b) -> void {
                if (r.tree[b].retained_from != kNoBox) r.retained.push_back(r.tree[b].retained_from);
                else for (BoxId c : r.tree.children(b)) self(self, c);
            };
            collect_retained(collect_retained, r.from);
            ready.erase(std::remove_if(ready.begin(), ready.end(),
                [&](const Replacement& prior) { return within(*tree, prior.into, id); }), ready.end());
            ready.push_back(std::move(r));
            if (log) reject("accepted", id);
            proved = true;
            break;
        }
        if (!proved) return false;
    }
    for (const auto& r : ready) {
        preserve_ = &r.retained;
        unindex_subtree(*tree, r.into);
        tree->replace_subtree(r.into, r.tree, r.from);
        sizes_.resize(tree->size());
        local_.resize(tree->size());
        contributions_.resize(tree->size());
        height_independent_.resize(tree->size());
        paint_root_flags_.resize(tree->size(), false);
        input_versions_.resize(tree->size(), input_serial_);
        grid_versions_.resize(tree->size());
        {
            const IntrinsicContributionScope memo(*tree, &ctx);
            index_subtree(*tree, r.into, ctx);
        }
        preserve_ = nullptr;
        retained_grids_ += r.retained.size();
        retained_.insert(retained_.end(), r.retained.begin(), r.retained.end());
        replaced_.push_back(r.into);
        for (BoxId p = (*tree)[r.into].parent; p != kNoBox; p = (*tree)[p].parent) {
            sizes_[p] = 1;
            for (BoxId c : tree->children(p)) sizes_[p] += sizes_[c];
            update_visual_overflow(tree, p);
            height_independent_[p] = independent_height(*tree, p, ctx);
            for (BoxId c : tree->children(p)) height_independent_[p] = height_independent_[p] && height_independent_[c];
            refresh_grid(*tree, p);
        }
    }
    return true;
}

namespace {
// Modal changes can leave large out-of-flow panels untouched. Keep their
// geometry only provisionally: every containing ancestor is proved below.
class ModalProbe final : public BoxBuildReuse, public LayoutReuse {
public:
    ModalProbe(const BoxTree& old, const std::unordered_map<const Element*, BoxId>& eligible)
        : old_(old), eligible_(eligible) {}
    bool reuse_children(BoxTree* tree, BoxId id) override {
        auto found = eligible_.find((*tree)[id].element);
        if (found == eligible_.end()) return false;
        const Box links = (*tree)[id];
        (*tree)[id] = old_[found->second];
        auto& b = (*tree)[id];
        b.parent = links.parent;
        b.next_sibling = links.next_sibling;
        b.prev_sibling = links.prev_sibling;
        b.first_child = b.last_child = kNoBox;
        b.retained_from = found->second;
        return true;
    }
    bool reuse_layout(BoxTree* tree, BoxId id) override {
        auto& b = (*tree)[id];
        if (b.retained_from == kNoBox) return false;
        const auto& old = old_[b.retained_from];
        if (b.width != old.width || (b.cross_size_imposed && b.height != old.height)) failed = true;
        b.height = old.height;
        return true;
    }
    bool failed = false;
private:
    const BoxTree& old_;
    const std::unordered_map<const Element*, BoxId>& eligible_;
};
bool modal_geometry_equal(const Box& a, const Box& b) {
    return same_outer(a,b) && a.x == b.x && a.y == b.y && a.font_size == b.font_size &&
        a.padding_top == b.padding_top && a.padding_right == b.padding_right &&
        a.padding_bottom == b.padding_bottom && a.padding_left == b.padding_left &&
        a.border_top == b.border_top && a.border_right == b.border_right &&
        a.border_bottom == b.border_bottom && a.border_left == b.border_left &&
        a.position == b.position && a.display == b.display;
}
}

bool IncrementalLayout::update_modal(BoxTree* tree, BoxId root, const Document& document,
        const Element& modal, StyleProvider* styles, const LayoutContext& ctx,
        const FontMetrics* metrics,
        const std::vector<std::pair<const ComputedStyle*, Invalidation>>& changes) {
    ModalSample sample;
    if (!tree->valid(root) || global_dependencies_) return false;
    const auto* modal_style = styles->style_of(modal);
    if (!modal_style) return false;
    const auto position = modal_style->get("position");
    if (position != "absolute" && position != "fixed") return false;
    const auto under_modal = [&](const Node* node) {
        for (; node; node = node->parent()) if (node == &modal) return true;
        return false;
    };
    // Counter/quote state crosses build boundaries even when styles are equal.
    // Unsupported dependencies retain the ordinary full construction path.
    bool safe = true;
    const auto inspect = [&](const auto& self, const Node& node) -> void {
        if (node.is_element()) {
            const auto& element = static_cast<const Element&>(node);
            const auto* style = styles->style_of(element);
            if (style) {
                for (const char* key : {"counter-reset", "counter-increment", "counter-set", "anchor-name"}) {
                    auto value = style->get(key);
                    if (!value.empty() && value != "none") safe = false;
                }
                if (style->get("float") != "none" || style->get("position") == "sticky") safe = false;
                for (const auto& change : changes)
                    if (change.first == style && change.second >= Invalidation::Layout && !under_modal(&node)) safe = false;
            }
            for (const char* pseudo : {"before", "after", "marker", "backdrop"}) {
                const auto* ps = styles->pseudo_style_of(element, pseudo);
                if (!ps) continue;
                if (std::string_view(pseudo) != "backdrop") safe = false;
                for (const auto& change : changes)
                    if (change.first == ps && change.second >= Invalidation::Layout && !under_modal(&node)) safe = false;
                if (&element == &modal && std::string_view(pseudo) == "backdrop" &&
                    ps->get("position") != "absolute" && ps->get("position") != "fixed") safe = false;
            }
        }
        for (const auto& child : node.children()) self(self, *child);
    };
    inspect(inspect, document);
    sample.lap(0);
    if (!safe) return false;
    ++input_serial_;
    for (const auto& change : changes) {
        const auto found = by_style_.find(change.first);
        if (found == by_style_.end() || !tree->valid(found->second)) continue;
        invalidate_subtree(*tree, found->second);
        for (BoxId p = (*tree)[found->second].parent; p != kNoBox; p = (*tree)[p].parent)
            input_versions_[p] = input_serial_;
    }
    std::unordered_map<const Element*, BoxId> eligible;
    for (const auto& entry : by_element_) {
        const BoxId id = entry.second;
        if (!tree->valid(id) || input_versions_[id] == input_serial_ || under_modal(entry.first)) continue;
        const Box& b = (*tree)[id];
        if (b.position != PositionType::Absolute && b.position != PositionType::Fixed) continue;
        // Only ordinary ancestor chains: no flex/grid static-position exports,
        // inline splitting, table fixups or multicolumn/counter side effects.
        bool chain = true;
        for (BoxId p = b.parent; p != kNoBox; p = (*tree)[p].parent)
            if (!ordinary((*tree)[p]) || ((*tree)[p].element && under_modal((*tree)[p].element))) chain = false;
        for (const Node* n = &modal; n; n = n->parent()) if (n == entry.first) chain = false;
        if (chain) eligible.emplace(entry.first,id);
    }
    if (eligible.empty()) return false;
    sample.lap(1);
    BoxTree scratch;
    ModalProbe reuse(*tree, eligible);
    BoxBuilder builder(&scratch, styles, &reuse);
    BoxId fresh = builder.build_document(document);
    sample.lap(2);
    BlockLayout block(&scratch, ctx, metrics, &reuse);
    block.layout_root(fresh, ctx.viewport_width_px, ctx.viewport_height_px);
    sample.lap(3);
    run_positioning(&scratch, fresh, ctx, &block);
    compute_visual_overflow(&scratch, fresh);
    sample.lap(4);
    if (reuse.failed) return false;
    std::vector<BoxId> keep;
    for (int id = 0; id < scratch.size(); ++id) {
        if (scratch[id].retained_from == kNoBox) continue;
        BoxId a = id, b = scratch[id].retained_from;
        while (a != kNoBox && b != kNoBox) {
            if (scratch[a].element != (*tree)[b].element || !modal_geometry_equal(scratch[a], (*tree)[b])) return false;
            a = scratch[a].parent; b = (*tree)[b].parent;
        }
        if (a != b) return false;
        keep.push_back(scratch[id].retained_from);
    }
    if (keep.empty()) return false;
    sample.lap(5);
    // The retained roots passed both input-version and ancestor-geometry
    // checks. Preserve their index entries as well as their boxes, exactly as
    // for an ordinary incremental replacement. Remove candidates belonging to
    // discarded boxes before their IDs can be recycled by the splice.
    preserve_ = &keep;
    unindex_subtree(*tree, root);
    const auto retained_box = [&](BoxId id) {
        for (; id != kNoBox; id = (*tree)[id].parent)
            if (std::find(keep.begin(), keep.end(), id) != keep.end()) return true;
        return false;
    };
    paint_roots_.erase(std::remove_if(paint_roots_.begin(), paint_roots_.end(), [&](BoxId id) {
        if (tree->valid(id) && retained_box(id)) return false;
        paint_root_flags_[id] = false;
        return true;
    }), paint_roots_.end());
    grid_roots_.erase(std::remove_if(grid_roots_.begin(), grid_roots_.end(), [&](BoxId id) {
        return !tree->valid(id) || !retained_box(id);
    }), grid_roots_.end());
    tree->replace_subtree(root, scratch, fresh);
    sample.lap(6);
    sizes_.resize(tree->size());
    local_.resize(tree->size());
    contributions_.resize(tree->size());
    height_independent_.resize(tree->size());
    paint_root_flags_.resize(tree->size(), false);
    input_versions_.resize(tree->size(), input_serial_);
    grid_versions_.resize(tree->size());
    {
        const IntrinsicContributionScope memo(*tree, &ctx);
        index_subtree(*tree, root, ctx);
    }
    preserve_ = nullptr;
    sample.lap(7);
    retained_ = std::move(keep);
    replaced_ = {root};
    retained_grids_ = 0;
    sample.accepted = true;
    return true;
}
} // namespace weva
