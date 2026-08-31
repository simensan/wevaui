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

} // namespace weva
