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

struct GridItemExportProbe {
    std::shared_ptr<const GridSizingInputs> inputs;
    double minimum = -1;
    double maximum = -1;
};

// Recompute the parent's intrinsic exports using a newly measured direct item,
// without laying out siblings or mutating either tree. Returns false unless
// resolved column allocation is contribution-independent and the item is an
// ordinary auto-width stretched item (no subgrid, ratio, min/max or auto margin).
// The caller must also prove unchanged placement/styles, natural block input,
// final outer geometry and definite-height context. Changed intrinsic exports
// must be propagated or rejected before installing the returned inputs.
bool probe_grid_item_exports(const Box& parent, const Box& measured_item,
                             const LayoutContext& ctx, GridItemExportProbe* result);

struct GridEdgeReuseProbe {
    std::shared_ptr<const GridSizingInputs> inputs;
    std::vector<std::pair<const Element*, double>> inline_offsets;
};

// Pure constraint proof for a grid whose own box edges changed. Candidate has
// its new box model but no child layout. Returns direct-item x deltas only when
// every resolved column size and item area width remain exactly unchanged.
// Snapshot eligibility excludes width-dependent track/gap definitions, dynamic
// repetition, out-of-flow items and subgrid/aspect-ratio hand-over. Caller must
// prove only this grid's edges changed, unchanged outer/block geometry and font,
// local descendants and a stable incoming sizing boundary before committing.
bool probe_grid_edge_reuse(const Box& old, const Box& candidate,
                           const LayoutContext& ctx, GridEdgeReuseProbe* result);

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
