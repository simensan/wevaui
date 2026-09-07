#pragma once
#include "weva/block_layout.h"
#include "weva/box_builder.h"
#include "weva/invalidation.h"
#include <unordered_map>

namespace weva {

// Input changes come from the cascade/animation diff, including inherited and
// pseudo styles. Structure and external constraints use the full-layout path.
class IncrementalLayout {
public:
    void index(const BoxTree& tree, BoxId root, const LayoutContext& ctx,
               bool reset_identities = true);
    bool update(BoxTree* tree, BoxId root, StyleProvider* styles,
                const LayoutContext& ctx, const FontMetrics* metrics,
                const std::vector<std::pair<const ComputedStyle*, Invalidation>>& changes);
    const std::vector<BoxId>& replaced() const { return replaced_; }
    size_t retained_grids() const { return retained_grids_; }
    const std::vector<BoxId>& retained() const { return retained_; }
    const std::vector<BoxId>& paint_candidates() const { return grid_roots_; }
    const std::unordered_map<const ComputedStyle*, BoxId>& style_boxes() const { return by_style_; }
    BoxId box_of(const Element* element) const {
        const auto it = by_element_.find(element);
        return it == by_element_.end() ? kNoBox : it->second;
    }

private:
    std::unordered_map<const ComputedStyle*, BoxId> by_style_;
    std::unordered_map<const Element*, BoxId> by_element_;
    std::vector<int> sizes_;
    std::vector<bool> local_;
    std::vector<std::pair<double, double>> contributions_;
    std::vector<BoxId> replaced_;
    std::vector<BoxId> retained_;
    std::vector<bool> height_independent_;
    std::vector<uint64_t> input_versions_, grid_versions_;
    std::vector<BoxId> grid_roots_;
    uint64_t input_serial_ = 0;
    size_t retained_grids_ = 0;
    const std::vector<BoxId>* preserve_ = nullptr;
    bool global_dependencies_ = false;
    void invalidate_subtree(const BoxTree& tree, BoxId root);
    void refresh_grid(const BoxTree& tree, BoxId id);
    int index_subtree(const BoxTree& tree, BoxId root, const LayoutContext& ctx);
    void unindex_subtree(const BoxTree& tree, BoxId root);
};
} // namespace weva
