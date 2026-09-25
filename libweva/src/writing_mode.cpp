#include "weva/writing_mode.h"

#include "weva/css_properties.h"

#include <algorithm>
#include <string>
#include <vector>

namespace weva {

namespace {

const int kId_writing_mode = CssPropertyRegistry::instance().id_of("writing-mode");
const int kId_width = CssPropertyRegistry::instance().id_of("width");
const int kId_height = CssPropertyRegistry::instance().id_of("height");
const int kId_min_width = CssPropertyRegistry::instance().id_of("min-width");
const int kId_min_height = CssPropertyRegistry::instance().id_of("min-height");
const int kId_max_width = CssPropertyRegistry::instance().id_of("max-width");
const int kId_max_height = CssPropertyRegistry::instance().id_of("max-height");
const int kId_overflow_x = CssPropertyRegistry::instance().id_of("overflow-x");
const int kId_overflow_y = CssPropertyRegistry::instance().id_of("overflow-y");

// The four physical sides, in the order the rotation tables below index.
enum Side { kTop = 0, kRight = 1, kBottom = 2, kLeft = 3 };

// Which ORIGINAL side lands on each rotated side. Under vertical-rl the block
// axis runs right-to-left, so the rotated top (block-start) is the original
// right and the rotated bottom is the original left; the inline axis runs
// top-to-bottom, so the rotated left (inline-start) is the original top.
// Under vertical-lr only the block axis flips.
constexpr Side kSourceRL[4] = {kRight, kBottom, kLeft, kTop};
constexpr Side kSourceLR[4] = {kLeft, kBottom, kRight, kTop};

const Side* source_sides(VerticalMode mode) { return mode == VerticalMode::LR ? kSourceLR : kSourceRL; }

// A property family with one longhand per side: margin-top .. margin-left.
struct SideFamily {
    int ids[4];
};

SideFamily family(const char* prefix, const char* suffix) {
    static const char* const names[4] = {"top", "right", "bottom", "left"};
    SideFamily f{};
    for (int i = 0; i < 4; ++i) {
        f.ids[i] = CssPropertyRegistry::instance().id_of(std::string(prefix) + names[i] + suffix);
    }
    return f;
}

const std::vector<SideFamily>& side_families() {
    static const std::vector<SideFamily> families = {
        family("margin-", ""),
        family("padding-", ""),
        family("border-", "-width"),
        family("border-", "-style"),
        family("border-", "-color"),
    };
    return families;
}

// The pairs that swap outright: a rotated width is the original height.
const std::vector<std::pair<int, int>>& swapped_pairs() {
    static const std::vector<std::pair<int, int>> pairs = {
        {kId_width, kId_height},
        {kId_min_width, kId_min_height},
        {kId_max_width, kId_max_height},
        {kId_overflow_x, kId_overflow_y},
    };
    return pairs;
}

void permute(ComputedStyle* copy, const ComputedStyle& original, VerticalMode mode) {
    const Side* src = source_sides(mode);
    for (const SideFamily& f : side_families()) {
        // Read all four from the ORIGINAL before writing any on the copy; an
        // unset side stays unset, so it falls through to the initial value
        // the way the original's did.
        std::string values[4];
        bool present[4];
        for (int i = 0; i < 4; ++i) {
            const int id = f.ids[src[i]];
            present[i] = id != kCustomPropertyId && original.contains(id);
            if (present[i]) values[i] = std::string(original.get(id));
        }
        for (int i = 0; i < 4; ++i) {
            if (f.ids[i] == kCustomPropertyId) continue;
            if (present[i]) copy->set(f.ids[i], values[i]);
            else copy->unset(f.ids[i]);
        }
    }
    for (const auto& [a, b] : swapped_pairs()) {
        if (a == kCustomPropertyId || b == kCustomPropertyId) continue;
        const bool has_a = original.contains(a), has_b = original.contains(b);
        const std::string va = has_a ? std::string(original.get(a)) : std::string();
        const std::string vb = has_b ? std::string(original.get(b)) : std::string();
        if (has_b) copy->set(a, vb); else copy->unset(a);
        if (has_a) copy->set(b, va); else copy->unset(b);
    }
    copy->set(kId_writing_mode, "horizontal-tb");
}

bool istarts(std::string_view s, std::string_view prefix) {
    if (s.size() < prefix.size()) return false;
    for (size_t i = 0; i < prefix.size(); ++i) {
        char c = s[i];
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
        if (c != prefix[i]) return false;
    }
    return true;
}

} // namespace

VerticalMode vertical_mode_of(const ComputedStyle* style) {
    if (!style) return VerticalMode::None;
    std::string_view v = style->get(kId_writing_mode);
    size_t b = 0, e = v.size();
    while (b < e && (v[b] == ' ' || v[b] == '\t')) ++b;
    while (e > b && (v[e - 1] == ' ' || v[e - 1] == '\t')) --e;
    v = v.substr(b, e - b);
    if (v.size() != 11) return VerticalMode::None;
    if (istarts(v, "vertical-rl")) return VerticalMode::RL;
    if (istarts(v, "vertical-lr")) return VerticalMode::LR;
    return VerticalMode::None;
}

const ComputedStyle* OrthogonalFlowStyles::original_of(const ComputedStyle* style) const {
    const auto it = originals_.find(style);
    return it == originals_.end() ? style : it->second;
}

const ComputedStyle* OrthogonalFlowStyles::copy_for(const ComputedStyle* original, VerticalMode mode) {
    const Key key{original, mode};
    const auto found = rotated_.find(key);
    if (found != rotated_.end()) return found->second;

    auto copy = std::make_unique<ComputedStyle>();
    // The copy inherits from the parent's copy when the parent is in the
    // rotated subtree, and from the original parent otherwise -- the root's
    // parent is the horizontal flow around it. Either way the copy states
    // horizontal-tb itself, so nothing below it reads the vertical mode.
    if (const ComputedStyle* parent = original->inherit_parent()) {
        const auto parent_copy = rotated_.find(Key{parent, mode});
        copy->set_inherit_parent(parent_copy != rotated_.end() ? parent_copy->second : parent);
    }
    std::vector<int> ids;
    original->copy_set_ids(ids);
    for (const int id : ids) copy->set(id, original->get(id));
    for (const auto& [name, value] : original->custom_properties()) copy->set(std::string_view(name), value);
    if (original->font_size_inherited()) copy->mark_font_size_inherited();
    if (original->line_height_inherited()) copy->mark_line_height_inherited();
    permute(copy.get(), *original, mode);

    const ComputedStyle* raw = copy.get();
    owned_.push_back(std::move(copy));
    rotated_[key] = raw;
    originals_[raw] = original;
    return raw;
}

void OrthogonalFlowStyles::rotate(BoxTree* tree, BoxId root, VerticalMode mode) {
    std::vector<BoxId> stack{root};
    while (!stack.empty()) {
        const BoxId id = stack.back();
        stack.pop_back();
        Box& b = (*tree)[id];
        if (b.style) b.style = copy_for(original_of(b.style), mode);
        // Children are pushed in reverse so the walk visits them in order:
        // a child's copy then finds its parent's copy already made.
        std::vector<BoxId> kids;
        for (BoxId c : tree->children(id)) kids.push_back(c);
        for (auto it = kids.rbegin(); it != kids.rend(); ++it) stack.push_back(*it);
    }
}

void OrthogonalFlowStyles::restore(BoxTree* tree, BoxId root) {
    std::vector<BoxId> stack{root};
    while (!stack.empty()) {
        const BoxId id = stack.back();
        stack.pop_back();
        Box& b = (*tree)[id];
        if (b.style) b.style = original_of(b.style);
        for (BoxId c : tree->children(id)) stack.push_back(c);
    }
}

namespace {

// Rotated edges back to physical: the inverse of the tables above. Under
// vertical-rl the rotated top was the original right, so the physical right
// takes the rotated top.
void edges_to_physical(Box& b, VerticalMode mode) {
    const Side* src = source_sides(mode);
    double m[4] = {b.margin_top, b.margin_right, b.margin_bottom, b.margin_left};
    double p[4] = {b.padding_top, b.padding_right, b.padding_bottom, b.padding_left};
    double bo[4] = {b.border_top, b.border_right, b.border_bottom, b.border_left};
    double pm[4], pp[4], pb[4];
    for (int rotated = 0; rotated < 4; ++rotated) {
        const int physical = src[rotated];
        pm[physical] = m[rotated];
        pp[physical] = p[rotated];
        pb[physical] = bo[rotated];
    }
    b.margin_top = pm[kTop]; b.margin_right = pm[kRight]; b.margin_bottom = pm[kBottom]; b.margin_left = pm[kLeft];
    b.padding_top = pp[kTop]; b.padding_right = pp[kRight]; b.padding_bottom = pp[kBottom]; b.padding_left = pp[kLeft];
    b.border_top = pb[kTop]; b.border_right = pb[kRight]; b.border_bottom = pb[kBottom]; b.border_left = pb[kLeft];
}

void transpose_children(BoxTree* tree, BoxId parent, VerticalMode mode) {
    const double parent_width = (*tree)[parent].width;
    for (BoxId c : tree->children(parent)) {
        Box& b = (*tree)[c];
        const double xr = b.x, yr = b.y, wr = b.width, hr = b.height;
        b.width = hr;
        b.height = wr;
        // The rotated inline axis is the physical top-to-bottom; the rotated
        // block axis runs from the parent's right edge leftwards (rl) or from
        // its left edge rightwards (lr).
        b.y = xr;
        b.x = mode == VerticalMode::RL ? parent_width - (yr + hr) : yr;
        edges_to_physical(b, mode);
        b.vertical_text = static_cast<uint8_t>(mode);
        transpose_children(tree, c, mode);
    }
}

} // namespace

void transpose_orthogonal_flow(BoxTree* tree, BoxId root, VerticalMode mode) {
    Box& r = (*tree)[root];
    std::swap(r.width, r.height);
    edges_to_physical(r, mode);
    r.vertical_text = static_cast<uint8_t>(mode);
    transpose_children(tree, root, mode);
}

} // namespace weva
