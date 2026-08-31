#pragma once
#include "weva/block_layout.h"
#include "weva/box.h"
#include "weva/style_resolver.h"

namespace weva {

// Ports Runtime/Layout/Positioning — `position: relative | absolute | fixed`.
//
// Runs AFTER block layout, because an out-of-flow box is placed against its
// containing block's final geometry, and that is only known once the in-flow
// pass has sized everything.

struct ContainingBlock {
    BoxId box = kNoBox;
    double x = 0, y = 0, width = 0, height = 0;
    bool is_viewport = false;
};

// The containing block of an absolutely positioned box is the PADDING box of
// its nearest positioned ancestor — inside the border edge, so `inset: 0` on a
// child of a bordered box lands two border-widths smaller.
ContainingBlock resolve_absolute_containing_block(const BoxTree& tree, BoxId box,
                                                  const LayoutContext& ctx);
// `fixed` normally resolves against the viewport, but the same properties that
// capture an absolute box capture a fixed one too: a transform changes how
// viewport coordinates map to local ones, so a transformed ancestor becomes the
// containing block for both.
ContainingBlock resolve_fixed_containing_block(const BoxTree& tree, BoxId box,
                                               const LayoutContext& ctx);

// True when this box establishes a containing block for absolutely positioned
// descendants: it is positioned, OR it has a transform, filter, perspective,
// `will-change` naming one of those, or layout/paint containment. Missing the
// second group is how an `inset: 0` child of `transform: scale(1)` ends up
// filling the viewport instead of its parent.
bool establishes_absolute_containing_block(const Box& b);

// Root-relative origin, summing local offsets up the tree.
void absolute_position(const BoxTree& tree, BoxId box, double* x, double* y);

// Reads `top`/`right`/`bottom`/`left` and `z-index` onto every box. An absent
// offset stays absent — `auto` is not zero, and the two lead to different
// placement.
void stamp_offsets(BoxTree* tree, BoxId root, const LayoutContext& ctx);

// Places every positioned box in the tree. `block` is used to re-lay an
// out-of-flow box's content once its width is known.
void run_positioning(BoxTree* tree, BoxId root, const LayoutContext& ctx,
                     BlockLayout* block);

} // namespace weva
