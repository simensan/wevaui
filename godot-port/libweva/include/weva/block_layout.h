#pragma once
#include "weva/box.h"
#include "weva/computed_style.h"
#include "weva/style_resolver.h"

#include <string_view>

// Ports the box-model half of Runtime/Layout/BlockLayout.cs — resolving an
// element's padding, border, margin and width against its containing block.
// Block flow itself (child stacking, margin collapsing, floats) is the next
// slice.

namespace weva {

struct ResolvedSides {
    double top = 0, right = 0, bottom = 0, left = 0;
    // The authored text of the inline edges, kept because `auto` margins are a
    // keyword the resolved pixel value cannot represent.
    std::string_view right_raw = "0";
    std::string_view left_raw = "0";
};

// Percentage padding and margin resolve against the containing block's WIDTH on
// every edge, top and bottom included (CSS 2.1 §8.3, §8.4).
ResolvedSides resolve_box_sides_px(const ComputedStyle* style, std::string_view shorthand,
                                   const LayoutContext& ctx, double font_size,
                                   double containing_block_width, double line_height);

// A border edge whose style is `none` or `hidden` has zero width regardless of
// the declared border-width (CSS Backgrounds §4.3).
ResolvedSides resolve_border_edges(const ComputedStyle* style, const LayoutContext& ctx,
                                   double font_size);

// CSS Basic UI §4.1. Initial is content-box; only `border-box` flips it.
bool is_border_box(const ComputedStyle* style);

PositionType parse_position_type(std::string_view raw);

// Resolves the box model onto `box`: padding, border and margin edges, the
// used width, and a definite height when one is available. Returns the
// element's resolved font size, which the caller reuses for its content.
//
// `box.width` and `box.height` are always BORDER-box values — paint and hit
// testing treat them as the outer rect — so under the default content-box
// sizing the frame is added before stamping.
double apply_box_model(BoxTree* tree, BoxId id, double containing_block_width,
                       const ComputedStyle* parent_style, const LayoutContext& ctx);

// ---- Margin collapsing (CSS Box Model L3 §8.3.1) -------------------------

// Two adjoining margins collapse to max() when both are positive, min() when
// both are negative, and their algebraic sum when the signs differ. A NaN input
// is treated as absent rather than propagated — one bad calc() would otherwise
// corrupt every block below it.
double collapse_margins(double a, double b);

// True for boxes whose vertical margins collapse with adjacent block siblings.
// Inline-blocks, floats and out-of-flow boxes are excluded and act as barriers.
bool participates_in_flow(const Box& b);
bool is_out_of_flow(const Box& b);

// CSS 2.1 §9.4.1: overflow other than visible, a display that is flow-root /
// flex / grid / inline-block / table-ish, an absolute or fixed position, or a
// float. A BFC root's own margins never collapse with its in-flow children.
bool establishes_new_bfc(const Box& b);

// The parent's top (bottom) edge is "open" when nothing — padding, border, or a
// BFC — sits between it and its first (last) in-flow child. An explicit height
// does NOT close the top; it only blocks bottom collapsing.
bool parent_top_open(const Box& b);
bool parent_bottom_open(const Box& b);
bool parent_height_auto(const Box& b, const LayoutContext& ctx, double font_size);

// A block with no padding, border, height, min-height or in-flow content: its
// own top and bottom margins collapse with each other and it contributes no
// height to the flow.
bool is_self_collapsing(const BoxTree& tree, BoxId id, const LayoutContext& ctx,
                        double font_size);

// ---- Block flow ----------------------------------------------------------

// Lays out a block container and its in-flow block descendants.
//
// Ported: the box model, the margin-collapse chain, and auto-height
// finalisation. NOT ported yet, so the corresponding boxes are laid out as
// ordinary in-flow blocks: floats and `clear`, out-of-flow positioning, inline
// formatting, and the flex / grid / table / multicol modes.
class BlockLayout {
public:
    BlockLayout(BoxTree* tree, const LayoutContext& ctx) : tree_(tree), ctx_(ctx) {}

    // Seeds the synthetic root with the viewport box. The height seed matters:
    // it is what lets `html, body { height: 100% }` chain down, since a
    // percentage height needs a definite basis to resolve against.
    void layout_root(BoxId root, double viewport_width, double viewport_height);
    void layout_block(BoxId id, double available_width, const ComputedStyle* parent_style);

private:
    void layout_content(BoxId id, double font_size, double containing_block_width,
                        const ComputedStyle* parent_style);
    void finalize_block_size(BoxId id, double font_size, double content_bottom_y);

    BoxTree* tree_;
    LayoutContext ctx_;
};

} // namespace weva
