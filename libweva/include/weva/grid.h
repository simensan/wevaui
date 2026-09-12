#pragma once
#include "weva/box.h"
#include "weva/style_resolver.h"

// CSS Grid Layout L1 — the EXPLICIT-GRID subset.
//
// Scope, stated plainly because a partial grid that pretends otherwise is worse
// than none:
//
//   Ported     `grid-template-columns` / `-rows` over <length>, <percentage>,
//              `auto` and `<n>fr`, with `repeat(<count>, <track>)`;
//              `grid-template-areas` with `grid-area: <name>`; row-major
//              auto-placement into the remaining cells; row and column gaps;
//              stretch placement within a cell.
//
//   NOT ported `minmax()`, `fit-content()`, `min-content`/`max-content` tracks,
//              `auto-fill` / `auto-fit`, numeric line placement
//              (`grid-column: 2 / 4`), spans, `grid-auto-flow: column` or
//              `dense`, implicit tracks beyond one row, subgrid, and the
//              `justify-*` / `align-*` families beyond the stretch default.
//
// An unported construct is not silently approximated: a track it cannot read is
// treated as `auto`, which is visible rather than subtly wrong, and
// `grid_is_fully_ported()` lets a caller ask.

namespace weva {

class BlockLayout;

// Returns the container's content height. `content_height` is negative when the
// container's own height is indefinite, which makes `fr` rows fall back to
// their content size.
double layout_grid(BoxTree* tree, BoxId container, double content_width, double content_height,
                   const LayoutContext& ctx, BlockLayout* block);

// False while the list above is non-empty.
constexpr bool grid_is_fully_ported() { return false; }

} // namespace weva
