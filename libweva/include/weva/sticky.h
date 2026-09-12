#pragma once
#include "weva/box.h"
#include "weva/style_resolver.h"

namespace weva {

// CSS Positioned Layout L3 §6.3: writes Box::sticky_offset_x/y for every
// `position: sticky` box under `root` from the current scroll positions of
// their scroll containers. Layout keeps the natural positions; paint, hit
// testing and visual_position add the offsets. Returns how many sticky boxes
// there are, so a caller can skip the walk on a page that has none.
int resolve_sticky_offsets(BoxTree* tree, BoxId root, const LayoutContext& ctx);

} // namespace weva
