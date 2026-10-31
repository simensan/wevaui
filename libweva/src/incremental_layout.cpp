#include "weva/incremental_layout.h"
#include "weva/inline_layout.h"
#include "weva/positioning.h"
#include "weva/anchor.h"
#include "weva/grid.h"
#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <chrono>

namespace weva {
namespace {
// Resolve fixed property names once, outside repeated layout/index walks.
const int kId_aspect_ratio = CssPropertyRegistry::instance().id_of("aspect-ratio");
const int kId_float = CssPropertyRegistry::instance().id_of("float");
const int kId_flex_basis = CssPropertyRegistry::instance().id_of("flex-basis");
const int kId_grid_template_columns = CssPropertyRegistry::instance().id_of("grid-template-columns");
const int kId_grid_template_rows = CssPropertyRegistry::instance().id_of("grid-template-rows");
const int kId_height = CssPropertyRegistry::instance().id_of("height");
const int kId_max_width = CssPropertyRegistry::instance().id_of("max-width");
const int kId_min_width = CssPropertyRegistry::instance().id_of("min-width");
const int kId_position = CssPropertyRegistry::instance().id_of("position");
const int kId_width = CssPropertyRegistry::instance().id_of("width");
const int kId_bottom = CssPropertyRegistry::instance().id_of("bottom");
const int kId_left = CssPropertyRegistry::instance().id_of("left");
const int kId_margin_bottom = CssPropertyRegistry::instance().id_of("margin-bottom");
const int kId_margin_left = CssPropertyRegistry::instance().id_of("margin-left");
const int kId_margin_right = CssPropertyRegistry::instance().id_of("margin-right");
const int kId_margin_top = CssPropertyRegistry::instance().id_of("margin-top");
const int kId_max_height = CssPropertyRegistry::instance().id_of("max-height");
const int kId_min_height = CssPropertyRegistry::instance().id_of("min-height");
const int kId_right = CssPropertyRegistry::instance().id_of("right");
const int kId_top = CssPropertyRegistry::instance().id_of("top");
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
bool fixed_inline_boundary(const BoxTree& tree, const Box& box, const LayoutContext& ctx) {
    if (!box.style || box.parent == kNoBox || !ordinary(tree[box.parent]) || box.is_inline_block)
        return false;
    const auto intrinsic_limit = [](std::string_view value) {
        return value.find("min-content") != std::string_view::npos ||
               value.find("max-content") != std::string_view::npos ||
               value.find("fit-content") != std::string_view::npos;
    };
    return box.style->get(kId_width).find('%') == std::string_view::npos &&
        !intrinsic_limit(box.style->get(kId_min_width)) && !intrinsic_limit(box.style->get(kId_max_width)) &&
        resolve_length(box.style, kId_width, ctx, box.font_size, std::nullopt).kind == LengthKind::Length;
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
    out.first = first_in_flow_baseline(tree, id).value_or(tree[id].height);
    return out;
}

bool independent_height(const BoxTree& tree, BoxId id, const LayoutContext& ctx) {
    const Box& b = tree[id];
    if (!b.style || b.kind != BoxKind::Block) return true;
    static const int kHeights[3] = {CssPropertyRegistry::instance().id_of("height"),
                                    CssPropertyRegistry::instance().id_of("min-height"),
                                    CssPropertyRegistry::instance().id_of("max-height")};
    for (const int property : kHeights) {
        if (b.style->get(property).find('%') == std::string_view::npos) continue;
        // Percentages below a definite local parent (a 100%-high bar inside
        // a 6px track) are independent of the retained grid's incoming height.
        if (b.parent == kNoBox || !tree[b.parent].style) return false;
        const Box& parent = tree[b.parent];
        const auto basis = resolve_length(parent.style, kId_height, ctx,
                                           parent.font_size > 0 ? parent.font_size : ctx.root_font_size_px,
                                           std::nullopt);
        if (basis.kind != LengthKind::Length) return false;
    }
    return true;
}

// Ordinary flow whose children each have a fixed inline constraint has
// width-independent natural height and intrinsic exports. Descendants still
// receive their actual fixed child width; no layout arithmetic is duplicated.
bool fixed_child_inline_constraints(const BoxTree& tree, BoxId id, const LayoutContext& ctx) {
    for (BoxId child : tree.children(id)) {
        const Box& b = tree[child];
        if (!b.style || b.kind != BoxKind::Block || b.is_inline_block || b.orthogonal_root ||
            b.vertical_text || b.is_multicol || b.position != PositionType::Static || b.is_float() ||
            (b.display != DisplayKind::Block && b.display != DisplayKind::FlowRoot &&
             b.display != DisplayKind::Flex && b.display != DisplayKind::Grid)) return false;
        const double fs = b.font_size > 0 ? b.font_size : ctx.root_font_size_px;
        if (resolve_length(b.style, kId_width, ctx, fs, std::nullopt).kind != LengthKind::Length)
            return false;
        for (int property : {kId_min_width, kId_max_width}) {
            const auto raw = b.style->get(property);
            if (raw == "auto" || raw == "none") continue;
            if (resolve_length(b.style, property, ctx, fs, std::nullopt).kind != LengthKind::Length)
                return false;
        }
        static const int edges[] = {
            CssPropertyRegistry::instance().id_of("padding"),
            CssPropertyRegistry::instance().id_of("padding-top"),
            CssPropertyRegistry::instance().id_of("padding-right"),
            CssPropertyRegistry::instance().id_of("padding-bottom"),
            CssPropertyRegistry::instance().id_of("padding-left"),
            CssPropertyRegistry::instance().id_of("margin"),
            kId_margin_top, kId_margin_right, kId_margin_bottom, kId_margin_left};
        for (int property : edges) {
            const auto raw = b.style->get(property);
            if (raw.find('%') != std::string_view::npos || raw.find("auto") != std::string_view::npos)
                return false;
        }
    }
    return true;
}

class RetainedGridProbe final : public BoxBuildReuse, public LayoutReuse {
public:
    RetainedGridProbe(const BoxTree& source, StyleProvider* styles,
                      const std::vector<std::pair<const Element*, BoxId>>& eligible,
                      const LayoutContext& ctx, const std::vector<double>& font_sizes,
                      const std::vector<bool>& width_independent)
        : source_(source), styles_(styles), eligible_(eligible), ctx_(ctx), font_sizes_(font_sizes),
          width_independent_(width_independent) {}

    bool reuse_children(BoxTree* tree, BoxId id) override {
        Box& b = (*tree)[id];
        const auto found = std::lower_bound(eligible_.begin(), eligible_.end(), b.element,
            [](const auto& entry, const Element* element) {
                return std::less<const Element*>{}(entry.first, element);
            });
        if (found == eligible_.end() || found->first != b.element) return false;
        b.retained_from = found->second;
        const Box& old = source_[found->second];
        if (old.display != DisplayKind::Grid) {
            b.retained_intrinsic_min = old.parent_layout_input.min_content;
            b.retained_intrinsic_max = old.parent_layout_input.max_content;
        }
        return true;
    }
    bool reuse_layout_input(BoxTree* tree, BoxId id, double available_width,
                            const ComputedStyle* parent_style) override {
        Box& b = (*tree)[id];
        if (b.retained_from == kNoBox) return false;
        const Box& cached = source_[b.retained_from];
        // Only ordinary items have the validated natural-input record. Their
        // eligibility excludes incoming percentage-height and imposed-height
        // dependencies; style/content versions and context are unchanged.
        if (cached.display == DisplayKind::Grid || cached.cross_size_imposed ||
            font_size_px(b.style, parent_style, ctx_) != font_sizes_[b.retained_from]) return false;
        // Parent alignment may have distributed auto margins after the natural
        // measurement. Re-resolve those through the existing box-model hook.
        for (const int property : {kId_margin_top, kId_margin_right, kId_margin_bottom, kId_margin_left})
            if (cached.style->get(property) == "auto") return false;
        double natural_width = cached.parent_layout_input.width;
        if (available_width != cached.parent_layout_input.available_width) {
            if (!width_independent_[b.retained_from]) return false;
            apply_box_model(tree, id, available_width, parent_style, ctx_);
            if (b.padding_top != cached.padding_top || b.padding_right != cached.padding_right ||
                b.padding_bottom != cached.padding_bottom || b.padding_left != cached.padding_left ||
                b.border_top != cached.border_top || b.border_right != cached.border_right ||
                b.border_bottom != cached.border_bottom || b.border_left != cached.border_left ||
                b.margin_top != cached.margin_top || b.margin_right != cached.margin_right ||
                b.margin_bottom != cached.margin_bottom || b.margin_left != cached.margin_left) return false;
            natural_width = b.width;
        }
        restore_cached(&b, cached, false);
        if (natural_width != cached.width) {
            b.width = natural_width;
            b.height = cached.parent_layout_input.height;
            b.retained_natural_measurement = true;
        }
        return true;
    }
    bool reuse_layout(BoxTree* tree, BoxId id) override {
        Box& b = (*tree)[id];
        if (b.retained_from == kNoBox) return false;
        const Box& cached = source_[b.retained_from];
        const bool ordinary_subtree = cached.display != DisplayKind::Grid;
        const ComputedStyle* parent_style = b.parent == kNoBox ? nullptr : (*tree)[b.parent].style;
        if (b.width == cached.width && !b.cross_size_imposed &&
            !cached.cross_size_imposed &&
            font_size_px(b.style, parent_style, ctx_) == font_sizes_[b.retained_from] &&
            (!ordinary_subtree ||
             (b.margin_top == cached.margin_top && b.margin_bottom == cached.margin_bottom &&
              b.margin_left == cached.margin_left && b.margin_right == cached.margin_right &&
              (get_height_is_auto(cached) || b.height == cached.height))) &&
            b.padding_top == cached.padding_top && b.padding_right == cached.padding_right &&
            b.padding_bottom == cached.padding_bottom && b.padding_left == cached.padding_left &&
            b.border_top == cached.border_top && b.border_right == cached.border_right &&
            b.border_bottom == cached.border_bottom && b.border_left == cached.border_left) {
            const auto measured_input = b.parent_layout_input;
            const bool independent_input = b.parent_inline_contribution_independent;
            restore_cached(&b, cached, true);
            if (measured_input.available_width >= 0) {
                b.parent_layout_input = measured_input;
                b.parent_inline_contribution_independent = independent_input;
            }
            return true;
        }
        b.retained_from = kNoBox;
        b.retained_natural_measurement = false;
        b.retained_intrinsic_min = b.retained_intrinsic_max = -1;
        // The parent's new allocation changed the input. Rebuild now, before
        // ordinary layout observes the deferred root's empty child list.
        BoxBuilder builder(tree, styles_, nullptr, true);
        builder.materialize_children(id);
        return false;
    }
private:
    static void restore_cached(Box* box, const Box& cached, bool keep_margins) {
        const BoxId parent = box->parent, previous = box->prev_sibling, next = box->next_sibling;
        const BoxId retained = box->retained_from;
        const double x = box->x, y = box->y;
        const double top = box->margin_top, right = box->margin_right;
        const double bottom = box->margin_bottom, left = box->margin_left;
        const double minimum = box->retained_intrinsic_min, maximum = box->retained_intrinsic_max;
        *box = cached;
        box->parent = parent; box->prev_sibling = previous; box->next_sibling = next;
        box->first_child = box->last_child = kNoBox;
        box->x = x; box->y = y;
        if (keep_margins) {
            box->margin_top = top; box->margin_right = right;
            box->margin_bottom = bottom; box->margin_left = left;
        }
        box->retained_from = retained;
        box->retained_intrinsic_min = minimum; box->retained_intrinsic_max = maximum;
    }
    static bool get_height_is_auto(const Box& b) {
        return b.style && b.style->get(kId_height) == "auto";
    }
    const BoxTree& source_;
    StyleProvider* styles_;
    const std::vector<std::pair<const Element*, BoxId>>& eligible_;
    const LayoutContext& ctx_;
    const std::vector<double>& font_sizes_;
    const std::vector<bool>& width_independent_;
};
}

void IncrementalLayout::invalidate_subtree(const BoxTree& tree, BoxId id) {
    input_versions_[id] = input_serial_;
    for (BoxId c : tree.children(id)) invalidate_subtree(tree, c);
}

void IncrementalLayout::refresh_grid(const BoxTree& tree, BoxId id, const LayoutContext& ctx) {
    const Box& b = tree[id];
    grid_versions_[id] = 0;
    natural_width_independent_[id] = false;
    if (!local_[id] || !height_independent_[id] ||
        !b.element || !b.style || b.is_inline_block || b.orthogonal_root || b.vertical_text ||
        b.cross_size_imposed || b.is_multicol || is_table_display(b.display)) return;
    const bool retained_grid = sizes_[id] >= 32 && b.display == DisplayKind::Grid &&
        b.parent != kNoBox && ordinary(tree[b.parent]) && b.style->get(kId_height) == "auto" &&
        b.style->get(kId_aspect_ratio) == "auto" &&
        b.style->get(kId_grid_template_columns).find("subgrid") == std::string_view::npos &&
        b.style->get(kId_grid_template_rows).find("subgrid") == std::string_view::npos;
    // A proxy for a single box costs more than resolving that empty box;
    // retain actual subtrees, leaving empty items on the normal layout path.
    bool retained_block = sizes_[id] > 1 && ordinary(b) && b.parent != kNoBox &&
        (tree[b.parent].display == DisplayKind::Grid || tree[b.parent].display == DisplayKind::Flex) &&
        b.intrinsic_width == 0 && b.intrinsic_height == 0 &&
        b.style->get(kId_aspect_ratio) == "auto" &&
        b.parent_layout_input.available_width >= 0 &&
        (b.parent_layout_input.width == b.width || tree[b.parent].display == DisplayKind::Flex) &&
        b.parent_layout_input.height == b.height;
    if (!retained_grid && !retained_block) return;
    resolved_font_sizes_[id] = font_size_px(b.style,
        b.parent == kNoBox ? nullptr : tree[b.parent].style, ctx);
    if (retained_block) {
        const auto height = resolve_length(b.style, kId_height, ctx, resolved_font_sizes_[id], std::nullopt);
        // A retained proxy is deliberately non-self-collapsing. Keep both
        // vertical edges closed, or a positive definite height, and prohibit
        // descendant margins from changing the root's external margin exports.
        retained_block = (height.kind == LengthKind::Length && height.pixels > 0) ||
            (b.padding_top + b.border_top > 0 && b.padding_bottom + b.border_bottom > 0);
        if (b.padding_top + b.border_top == 0 || b.padding_bottom + b.border_bottom == 0)
            for (BoxId child : tree.children(id))
                if (tree[child].margin_top != 0 || tree[child].margin_bottom != 0) retained_block = false;
        // Percent constraints on the root consume the incoming parent's height,
        // unlike percentages inside a fixed-height descendant. apply_box_model
        // has not applied auto-height min/max constraints at the reuse hook.
        for (const int property : {kId_height, kId_min_height, kId_max_height})
            if (b.style->get(property).find('%') != std::string_view::npos) retained_block = false;
    }
    if (!retained_grid && !retained_block) return;
    natural_width_independent_[id] = retained_block && tree[b.parent].display == DisplayKind::Flex &&
        fixed_child_inline_constraints(tree, id, ctx);
    // Grid and ordinary-item caches share version invalidation.
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
    // A relatively positioned box moves only itself, after flow, and an
    // absolute one is placed against its containing block: update() runs that
    // placement inside a replacement, so both are local to any subtree that
    // holds the containing block too. Fixed boxes, anchor functions and
    // absolute boxes placed against the viewport stay nonlocal.
    bool local = !b.pseudo_host && b.display != DisplayKind::ListItem && !b.is_multicol &&
                 !is_table_display(b.display) && !b.is_float() &&
                 b.position != PositionType::Fixed && b.position != PositionType::Sticky;
    // The outermost containing block an absolute box in this subtree is placed
    // against, while that block is still above this box.
    BoxId escapes = kNoBox;
    const auto escape_to = [&](BoxId cb) {
        if (escapes == kNoBox || within(tree, escapes, cb)) escapes = cb;
    };
    if (b.position == PositionType::Absolute) {
        BoxId cb = kNoBox;
        for (BoxId p = b.parent; p != kNoBox; p = tree[p].parent)
            if (establishes_absolute_containing_block(tree[p])) { cb = p; break; }
        bool anchored = false;
        for (const int property : {kId_left, kId_right, kId_top, kId_bottom, kId_width, kId_height})
            if (b.style && looks_like_anchor_function(b.style->get(property))) anchored = true;
        if (cb == kNoBox || anchored) local = false;
        else escape_to(cb);
    }
    int size = 1;
    bool independent = independent_height(tree, id, ctx);
    for (BoxId c : tree.children(id)) {
        size += index_subtree(tree, c, ctx);
        local = local && contained_[c];
        if (escapes_[c] != kNoBox && escapes_[c] != id) escape_to(escapes_[c]);
        independent = independent && height_independent_[c];
    }
    // Contained: nothing inside needs the full pass except, possibly, boxes
    // placed against a containing block further up. Local: not even those, so
    // the subtree can be replaced on its own.
    contained_[id] = local;
    escapes_[id] = escapes;
    if (escapes != kNoBox) local = false;
    sizes_[id] = size;
    local_[id] = local;
    height_independent_[id] = independent;
    if (local && b.element && b.kind == BoxKind::Block)
        contributions_[id] = block_intrinsic_contributions(tree, id, &ctx);
    refresh_grid(tree, id, ctx);
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
    escapes_.assign(tree.size(), kNoBox);
    contained_.assign(tree.size(), false);
    contributions_.resize(tree.size());
    resolved_font_sizes_.resize(tree.size());
    natural_width_independent_.resize(tree.size());
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

void IncrementalLayout::refresh_reflow_geometry(const BoxTree& tree, BoxId root,
                                                const LayoutContext& ctx) {
    // The reflow validator excludes anonymous/inline/replaced/positioned boxes
    // and preserves every link and identity. Refresh geometry-dependent caches
    // without reconstructing maps or rediscovering unchanged dependencies.
    retained_.clear();
    input_versions_.assign(tree.size(), ++input_serial_);
    grid_versions_.assign(tree.size(), 0);
    paint_root_flags_.assign(tree.size(), false);
    grid_roots_.clear();
    paint_roots_.clear();
    const IntrinsicContributionScope memo(tree, &ctx);
    const auto refresh = [&](const auto& self, BoxId id) -> void {
        for (BoxId child : tree.children(id)) self(self, child);
        const Box& b = tree[id];
        if (local_[id] && b.element && b.kind == BoxKind::Block)
            contributions_[id] = block_intrinsic_contributions(tree, id, &ctx);
        refresh_grid(tree, id, ctx);
    };
    refresh(refresh, root);
}

void IncrementalLayout::unindex_subtree(const BoxTree& tree, BoxId id, bool erase_identities) {
    if (preserve_ && std::find(preserve_->begin(), preserve_->end(), id) != preserve_->end()) return;
    grid_versions_[id] = 0;
    paint_root_flags_[id] = false;
    const auto found = by_style_.find(tree[id].style);
    if (found != by_style_.end() && found->second == id) {
        if (erase_identities) by_style_.erase(found);
        else found->second = kNoBox;
    }
    const auto element = by_element_.find(tree[id].element);
    if (element != by_element_.end() && element->second == id) {
        if (erase_identities) by_element_.erase(element);
        else element->second = kNoBox;
    }
    for (BoxId c : tree.children(id)) unindex_subtree(tree, c, erase_identities);
}

bool IncrementalLayout::update_grid_edges(BoxTree* tree, BoxId id, const LayoutContext& ctx,
                                          bool* attempted) {
    const Box& old = (*tree)[id];
    if (old.display != DisplayKind::Grid || !old.grid_sizing_inputs || !local_[id] ||
        !fixed_inline_boundary(*tree, old, ctx) || old.cross_size_imposed ||
        old.orthogonal_root || old.style->get(kId_aspect_ratio) != "auto") return false;
    for (BoxId child : tree->children(id))
        if (!local_[child]) return false;
    *attempted = true;
    edge_probe_.reset();
    struct ClearProbe { BoxTree& tree; ~ClearProbe() { tree.reset(); } } clear{edge_probe_};
    // Copy the containing-block chain for normal box-model resolution. The
    // probe owns no descendants and cannot change the live layout on a miss.
    std::vector<BoxId> ancestors;
    for (BoxId p = old.parent; p != kNoBox; p = (*tree)[p].parent) ancestors.push_back(p);
    BoxId parent = kNoBox;
    for (auto it = ancestors.rbegin(); it != ancestors.rend(); ++it) {
        const BoxId p = edge_probe_.create(BoxKind::Block);
        edge_probe_[p] = (*tree)[*it];
        edge_probe_[p].parent = edge_probe_[p].first_child = edge_probe_[p].last_child = kNoBox;
        edge_probe_[p].next_sibling = edge_probe_[p].prev_sibling = kNoBox;
        if (parent != kNoBox) edge_probe_.append_child(parent, p);
        parent = p;
    }
    const BoxId candidate_id = edge_probe_.create(BoxKind::Block);
    edge_probe_[candidate_id] = old;
    Box& candidate = edge_probe_[candidate_id];
    candidate.parent = candidate.first_child = candidate.last_child = kNoBox;
    candidate.next_sibling = candidate.prev_sibling = kNoBox;
    edge_probe_.append_child(parent, candidate_id);
    const Box& outer = (*tree)[old.parent];
    apply_box_model(&edge_probe_, candidate_id, outer.content_width(), outer.style, ctx);
    // Unchanged vertical edges and unchanged item areas prove the old auto
    // height below. An aspect ratio cannot couple it to the new content width.
    candidate.height = old.height;
    if (!same_outer(old, candidate)) return false;
    GridEdgeReuseProbe result;
    if (!probe_grid_edge_reuse(old, candidate, ctx, &result)) return false;
    for (const auto& item : result.inline_offsets) {
        const auto found = by_element_.find(item.first);
        if (found == by_element_.end() || !tree->valid(found->second) ||
            (*tree)[found->second].parent != id) return false;
    }
    // Commit only box edges and item translations. Every descendant keeps its
    // geometry, identity and intrinsic exports under the proven constraints.
    Box& live = (*tree)[id];
    live.padding_left = candidate.padding_left; live.padding_right = candidate.padding_right;
    live.border_left = candidate.border_left; live.border_right = candidate.border_right;
    live.grid_sizing_inputs = std::move(result.inputs);
    for (const auto& item : result.inline_offsets) {
        const BoxId child = by_element_.find(item.first)->second;
        (*tree)[child].x += item.second;
        retained_.push_back(child);
        if ((*tree)[child].display == DisplayKind::Grid) ++retained_grids_;
    }
    replaced_.push_back(id);
    for (BoxId p = id; p != kNoBox; p = (*tree)[p].parent) {
        update_visual_overflow(tree, p);
        refresh_grid(*tree, p, ctx);
    }
    return true;
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
        // Paint reads the current styles independently. A color/highlight
        // change must not invalidate retained geometry during a mixed update.
        if (change.second < Invalidation::Layout) continue;
        const auto found = by_style_.find(change.first);
        if (found == by_style_.end() || !tree->valid(found->second)) continue;
        if (styles->only_box_edges_changed(change.first))
            input_versions_[found->second] = input_serial_;
        else invalidate_subtree(*tree, found->second);
        for (BoxId p = (*tree)[found->second].parent; p != kNoBox; p = (*tree)[p].parent)
            input_versions_[p] = input_serial_;
    }
    if (changes.size() == 1 && styles->only_box_edges_changed(changes.front().first)) {
        const auto found = by_style_.find(changes.front().first);
        if (found != by_style_.end() && tree->valid(found->second)) {
            bool attempted = false;
            if (update_grid_edges(tree, found->second, ctx, &attempted)) return true;
            // Changed grid areas need ordinary layout. Do not build and then
            // materialize a transaction full of known constraint misses.
            if (attempted) return false;
        }
    }
    auto& eligible = eligible_scratch_;
    auto& candidates = candidate_scratch_;
    auto& ancestors = ancestor_scratch_;
    eligible.clear(); candidates.clear(); ancestors.clear();
    struct ClearScratch {
        std::vector<std::pair<const Element*, BoxId>>& eligible;
        std::vector<BoxId>& candidates;
        std::vector<BoxId>& ancestors;
        ~ClearScratch() { eligible.clear(); candidates.clear(); ancestors.clear(); }
    } clear_scratch{eligible, candidates, ancestors};
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
    struct Replacement {
        BoxId into, from;
        BoxTree tree;
        std::vector<BoxId> retained;
        BoxId grid_parent = kNoBox;
        GridItemExportProbe grid_exports;
    };
    std::vector<Replacement> ready;
    struct ReturnReadyArenas {
        std::vector<Replacement>& ready;
        std::vector<BoxTree>& pool;
        ~ReturnReadyArenas() {
            for (auto& replacement : ready) {
                replacement.tree.reset();
                pool.push_back(std::move(replacement.tree));
            }
        }
    } return_ready{ready, scratch_pool_};
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
                // Edge-only changes can be proved from the parent's measured
                // inputs below. Sizing, placement and alignment directives
                // still require the parent to allocate the item again.
                bool own_change = false;
                for (const auto& change : changes)
                    if (change.first == old.style && change.second >= Invalidation::Layout)
                        own_change = true;
                if (own_change && (old.cross_size_imposed || !styles->only_box_edges_changed(old.style))) continue;
                // Auto-basis row items export their edge changes to flex
                // allocation. Start at that owner instead of measuring a
                // speculative leaf that cannot prove intrinsic independence.
                if (own_change && outer.display == DisplayKind::Flex &&
                    !old.parent_inline_contribution_independent &&
                    old.style->get(kId_width) == "auto" && old.style->get(kId_flex_basis) == "auto") continue;
            }
            // Placed against a containing block further up: that ancestor can
            // still be replaced, with this box inside it.
            if (!local_[id] && contained_[id]) continue;
            if (!local_[id]) return reject("nonlocal subtree", id);
            // Build deferred children only for this probe. A local cell update
            // must not allocate cache entries for every unchanged sibling.
            eligible.clear();
            if (sizes_[id] > 1) for (BoxId cached : grid_roots_) {
                if (!tree->valid(cached) || cached == id || !grid_versions_[cached] ||
                    grid_versions_[cached] != input_versions_[cached] ||
                    !within(*tree, cached, id)) continue;
                bool covered = false;
                for (BoxId p = (*tree)[cached].parent; p != id && p != kNoBox; p = (*tree)[p].parent)
                    if (grid_versions_[p] && grid_versions_[p] == input_versions_[p]) {
                        covered = true; break;
                    }
                if (!covered) eligible.emplace_back((*tree)[cached].element, cached);
            }
            std::sort(eligible.begin(), eligible.end(), [](const auto& a, const auto& b) {
                return std::less<const Element*>{}(a.first, b.first);
            });
            int work_size = sizes_[id];
            for (const auto& item : eligible)
                work_size -= sizes_[item.second];
            if (work_size > sizes_[root] / 2) return reject("large subtree", id);
            Replacement r;
            if (!scratch_pool_.empty()) {
                r.tree = std::move(scratch_pool_.back());
                scratch_pool_.pop_back();
            }
            struct ReturnProbeArena {
                BoxTree& tree;
                std::vector<BoxTree>& pool;
                bool active = true;
                ~ReturnProbeArena() {
                    if (!active) return;
                    tree.reset();
                    pool.push_back(std::move(tree));
                }
            } return_probe{r.tree, scratch_pool_};
            r.into = id;
            // Preserve the actual containing-block chain for percentages and
            // inherited font sizes. Its siblings are not layout inputs here.
            ancestors.clear();
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
            RetainedGridProbe reuse(*tree, styles, eligible, ctx, resolved_font_sizes_, natural_width_independent_);
            BoxBuilder builder(&r.tree, styles, &reuse, true);
            r.from = builder.build(*old.element, old.style);
            if (r.from == kNoBox) return false;
            // The retained parent may have blockified an inline-* item.
            // Building that element alone does not perform the parent's step.
            r.tree[r.from].display = old.display;
            r.tree[r.from].is_inline_block = old.is_inline_block;
            r.tree.append_child(parent, r.from);
            BlockLayout block(&r.tree, ctx, metrics, &reuse);
            bool independent_inline_contribution = false;
            if (imposed_width) {
                // First measure the parent's sizing input. Imposing the old
                // width immediately can shrink a growing child back to its
                // old used width, hiding the change from the export check.
                // That made a content-box button keep 34px after 11px of
                // padding was added, leaving its centred flex siblings stale.
                // Fixed-track grids can measure directly in their final area;
                // flex and intrinsic-track grids measure at container width.
                // Reproduce the parent's actual input, not an assumed width.
                const double measured_width = old.parent_layout_input.available_width;
                if (measured_width < 0) continue;
                block.layout_block(r.from, measured_width, outer.style);
                const ParentLayoutInput natural = measure_parent_layout_input(
                    r.tree, r.from, measured_width, ctx);
                ParentLayoutInput measured = natural;
                const Box& measured_box = r.tree[r.from];
                const double old_frame = old.padding_left + old.padding_right + old.border_left + old.border_right;
                const double new_frame = measured_box.padding_left + measured_box.padding_right +
                                         measured_box.border_left + measured_box.border_right;
                independent_inline_contribution = old.parent_inline_contribution_independent &&
                    !old.cross_size_imposed &&
                    (outer.display == DisplayKind::Grid
                        ? old.width > old_frame && new_frame <= old.width
                        : outer.display == DisplayKind::Flex && old.parent_layout_input.width == old.width);
                if (!independent_inline_contribution && outer.display == DisplayKind::Grid &&
                    !old.cross_size_imposed && old.width > old_frame && new_frame <= old.width &&
                    styles->only_box_edges_changed(old.style)) {
                    // Column allocation may be independent while the grid's
                    // intrinsic exports still depend on this item. Refresh
                    // those exports without laying out unchanged siblings.
                    Box parent_inputs = outer;
                    for (const auto& prior : ready) {
                        if (prior.grid_parent != old.parent) continue;
                        parent_inputs.grid_sizing_inputs = prior.grid_exports.inputs;
                    }
                    r.tree[r.from].parent_layout_input = natural;
                    GridItemExportProbe exports;
                    if (probe_grid_item_exports(parent_inputs, r.tree[r.from], ctx, &exports)) {
                        const bool unchanged_exports = exports.minimum == outer.grid_min_content &&
                                                       exports.maximum == outer.grid_max_content;
                        // An authored length in an ordinary block parent
                        // exports its used width, not the changed track sizes.
                        // Percentage/intrinsic widths and flex/grid parents
                        // still need ancestor sizing and are not cut here.
                        const bool fixed_boundary = fixed_inline_boundary(*tree, outer, ctx);
                        if (unchanged_exports || fixed_boundary) {
                            independent_inline_contribution = true;
                            r.grid_parent = old.parent;
                            r.grid_exports = std::move(exports);
                        }
                    }
                }
                if (independent_inline_contribution) {
                    // The parent's constraint record proves it does not size
                    // tracks/lines from this inline contribution. Natural
                    // height and final outer geometry still prove placement.
                    measured.min_content = old.parent_layout_input.min_content;
                    measured.max_content = old.parent_layout_input.max_content;
                }
                if (old.parent_layout_input != measured) {
                    reject("natural intrinsic contribution", id);
                    continue;
                }
                block.relayout_at(r.from, old.width);
                r.tree[r.from].parent_layout_input = natural;
                r.tree[r.from].parent_inline_contribution_independent = old.parent_inline_contribution_independent;
            } else {
                block.layout_block(r.from, outer.content_width(), outer.style);
            }
            if (!same_outer(old, r.tree[r.from])) { reject("outer geometry", id); continue; }
            if (baselines(*tree, id) != baselines(r.tree, r.from)) { reject("baseline", id); continue; }
            // Dimensions alone do not prove stable intrinsic contributions to
            // a flex/grid ancestor. Compare the exports cached BEFORE styles
            // changed, rather than remeasuring old boxes with new styles.
            if (!independent_inline_contribution &&
                contributions_[id] != block_intrinsic_contributions(r.tree, r.from, &ctx))
                { reject("intrinsic contribution", id); continue; }
            bool safe = true;
            for (BoxId p = old.parent; p != kNoBox; p = (*tree)[p].parent) {
                const Box& a = (*tree)[p];
                if (a.is_inline_block || a.is_multicol || is_table_display(a.display)) {
                    safe = false; break;
                }
            }
            if (!safe) continue;
            stamp_offsets(&r.tree, r.from, ctx);
            // The retained position already includes the root's own relative
            // offset. Recovering its in-flow position by subtracting that
            // offset would round differently from a full layout, so a root
            // whose offset changed is promoted instead.
            double old_dx, old_dy, new_dx, new_dy;
            relative_offset(old, &old_dx, &old_dy);
            relative_offset(r.tree[r.from], &new_dx, &new_dy);
            if (old_dx != new_dx || old_dy != new_dy) { reject("relative offset", id); continue; }
            r.tree[r.from].x = old.x;
            r.tree[r.from].y = old.y;
            // What run_positioning does to the rest of the subtree: relative
            // offsets, and absolute boxes placed against containing blocks that
            // locality guarantees are inside it. The copied ancestor chain gives
            // those blocks the same root-relative origin as the retained tree.
            // A retained grid has no children here; its own position is the
            // scratch one.
            position_descendants(&r.tree, r.from, ctx, &block);
            compute_visual_overflow(&r.tree, r.from);
            r.retained.reserve(eligible.size());
            const auto collect_retained = [&](const auto& self, BoxId b) -> void {
                if (r.tree[b].retained_from != kNoBox) r.retained.push_back(r.tree[b].retained_from);
                else for (BoxId c : r.tree.children(b)) self(self, c);
            };
            collect_retained(collect_retained, r.from);
            ready.erase(std::remove_if(ready.begin(), ready.end(),
                [&](const Replacement& prior) { return within(*tree, prior.into, id); }), ready.end());
            ready.push_back(std::move(r));
            return_probe.active = false;
            if (log) reject("accepted", id);
            proved = true;
            break;
        }
        if (!proved) return false;
    }
    for (const auto& r : ready) {
        preserve_ = &r.retained;
        // A layout-only replacement preserves DOM and computed-style identities.
        // Reuse their map nodes when indexing the replacement instead of freeing
        // and reallocating two nodes per element. Structural rebuilds reset them.
        unindex_subtree(*tree, r.into, false);
        // These IDs are about to be recycled. A former leaf paint boundary
        // must not become a boundary on its replacement's unrelated ancestor:
        // that stale boundary would invalidate genuinely retained descendants.
        paint_roots_.erase(std::remove_if(paint_roots_.begin(), paint_roots_.end(),
            [&](BoxId id) { return !paint_root_flags_[id]; }), paint_roots_.end());
        tree->replace_subtree(r.into, r.tree, r.from);
        if (r.grid_parent != kNoBox) {
            Box& parent = (*tree)[r.grid_parent];
            parent.grid_sizing_inputs = r.grid_exports.inputs;
            parent.grid_min_content = r.grid_exports.minimum;
            parent.grid_max_content = r.grid_exports.maximum;
        }
        sizes_.resize(tree->size());
        local_.resize(tree->size());
        escapes_.resize(tree->size(), kNoBox);
        contained_.resize(tree->size());
        contributions_.resize(tree->size());
        resolved_font_sizes_.resize(tree->size());
        natural_width_independent_.resize(tree->size());
        height_independent_.resize(tree->size());
        paint_root_flags_.resize(tree->size(), false);
        input_versions_.resize(tree->size(), input_serial_);
        grid_versions_.resize(tree->size());
        {
            const IntrinsicContributionScope memo(*tree, &ctx);
            index_subtree(*tree, r.into, ctx);
        }
        preserve_ = nullptr;
        for (BoxId retained : r.retained)
            if ((*tree)[retained].display == DisplayKind::Grid) ++retained_grids_;
        retained_.insert(retained_.end(), r.retained.begin(), r.retained.end());
        replaced_.push_back(r.into);
        for (BoxId p = (*tree)[r.into].parent; p != kNoBox; p = (*tree)[p].parent) {
            sizes_[p] = 1;
            for (BoxId c : tree->children(p)) sizes_[p] += sizes_[c];
            update_visual_overflow(tree, p);
            height_independent_[p] = independent_height(*tree, p, ctx);
            for (BoxId c : tree->children(p)) height_independent_[p] = height_independent_[p] && height_independent_[c];
            refresh_grid(*tree, p, ctx);
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
    const auto position = modal_style->get(kId_position);
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
                if (style->get(kId_float) != "none" || style->get(kId_position) == "sticky") safe = false;
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
                    ps->get(kId_position) != "absolute" && ps->get(kId_position) != "fixed") safe = false;
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
    unindex_subtree(*tree, root, true);
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
    escapes_.resize(tree->size(), kNoBox);
    contained_.resize(tree->size());
    contributions_.resize(tree->size());
    resolved_font_sizes_.resize(tree->size());
    natural_width_independent_.resize(tree->size());
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
