#pragma once
#include "weva/box.h"
#include "weva/computed_style.h"
#include "weva/style_resolver.h"

#include <cstdint>
#include <memory>
#include <unordered_map>
#include <vector>

// CSS Writing Modes L3, the vertical modes: `writing-mode: vertical-rl` and
// `vertical-lr`.
//
// A vertical box inside a horizontal flow is an orthogonal flow root
// (§7.3). The engine lays every formatting context out horizontally, so a
// vertical subtree is laid out as the horizontal subtree it would be if the
// page were turned on its side: every style in the subtree is replaced by a
// ROTATED copy (margin-right becomes margin-top under vertical-rl, width
// becomes height, writing-mode becomes horizontal-tb), the ordinary block,
// inline, flex, grid and table code runs over the copies, and the geometry
// it produced is transposed back into physical coordinates. The originals
// go back on the boxes afterwards, so paint -- which reads physical border
// colours, radii and backgrounds -- sees what the author wrote.
//
// Text runs are painted rotated a quarter turn clockwise: the line's over
// side is the right in both vertical modes (§6.1, `text-orientation: mixed`
// with no upright CJK yet), so glyph tops face right and the lines stack
// right-to-left (rl) or left-to-right (lr).
//
// Not covered: a horizontal box nested inside a vertical one (it is laid
// out vertical), `sideways-*`, upright orientation of CJK, replaced
// elements' intrinsic sizes in a vertical flow, floats inside a vertical
// flow (they float on rotated sides), text decoration, selection and caret
// on vertical runs.

namespace weva {

enum class VerticalMode : uint8_t { None = 0, RL = 1, LR = 2 };

// Reads `writing-mode` through the inherit chain. Every style in a rotated
// subtree says horizontal-tb, so inside an orthogonal flow this is None.
VerticalMode vertical_mode_of(const ComputedStyle* style);

// The rotated copies for one layout pass. Copies are kept for the pass: a
// text run detached from the tree while its container is between two
// layouts still points at its copy, and must not point at freed memory.
class OrthogonalFlowStyles {
public:
    // Replaces the style of every box under `root` (root included) with its
    // rotated copy for `mode`. A box already on a copy is mapped from the
    // original, so re-laying a root within a pass reuses the copies.
    void rotate(BoxTree* tree, BoxId root, VerticalMode mode);
    // Puts the originals back on every box under `root`, including the line
    // boxes and runs that layout created on the copies.
    void restore(BoxTree* tree, BoxId root);

private:
    struct Key {
        const ComputedStyle* original;
        VerticalMode mode;
        bool operator==(const Key& o) const { return original == o.original && mode == o.mode; }
    };
    struct KeyHash {
        size_t operator()(const Key& k) const {
            return std::hash<const void*>()(k.original) ^ (static_cast<size_t>(k.mode) << 1);
        }
    };
    const ComputedStyle* copy_for(const ComputedStyle* original, VerticalMode mode);
    const ComputedStyle* original_of(const ComputedStyle* style) const;

    std::unordered_map<Key, const ComputedStyle*, KeyHash> rotated_;
    std::unordered_map<const ComputedStyle*, const ComputedStyle*> originals_;
    std::vector<std::unique_ptr<ComputedStyle>> owned_;
};

// Turns the subtree under `root` from the rotated frame it was laid out in
// into physical coordinates: sizes and edges swap, children move to the
// rotated frame's image. The root's own x/y are left to its container. Sets
// `vertical_text` on every box for paint.
void transpose_orthogonal_flow(BoxTree* tree, BoxId root, VerticalMode mode);

} // namespace weva
