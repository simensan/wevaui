#pragma once
#include "weva/box.h"
#include "weva/style_resolver.h"

// CSS Grid Layout L1.
//
// Scope, stated plainly because a partial grid that pretends otherwise is worse
// than none:
//
//   Ported     `grid-template-columns` / `-rows` over <length>, <percentage>,
//              `auto`, `<n>fr`, `min-content`, `max-content`, `minmax()` and
//              `fit-content()`, with `repeat()` including `auto-fill` and
//              `auto-fit`; `grid-template-areas` with `grid-area: <name>`;
//              numeric line placement and `span`; auto-placement in both
//              directions including `grid-auto-flow: column` and `dense`;
//              implicit tracks; subgrid; row and column gaps; and the
//              `justify-*` / `align-*` families.
//
//   Untested   `fit-content()` parses and resolves but no test pins it, so it
//              is the one construct here whose behaviour is unverified.
//
// An unported construct is not silently approximated: a track it cannot read is
// treated as `auto`, which is visible rather than subtly wrong.
//
// This comment used to list almost everything above as NOT ported, and
// `grid_is_fully_ported()` returned false to match — long after the features
// landed and were pinned by test_grid.cpp. It is a header a host could branch
// on, so it was not a stale comment but a public API lying about capability.

namespace weva {

class BlockLayout;

// Returns the container's content height. `content_height` is negative when the
// container's own height is indefinite, which makes `fr` rows fall back to
// their content size.
double layout_grid(BoxTree* tree, BoxId container, double content_width, double content_height,
                   const LayoutContext& ctx, BlockLayout* block);

// The explicit and implicit grid, placement, sizing and alignment are all
// present and pinned; see the scope note above for the single untested
// construct.
constexpr bool grid_is_fully_ported() { return true; }

} // namespace weva
