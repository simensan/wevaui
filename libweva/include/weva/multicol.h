#pragma once
#include "weva/box.h"
#include "weva/style_resolver.h"

// CSS Multi-column Layout L1 — the BALANCED-COLUMNS subset.
//
//   Ported     `column-count`, `column-width` (and the used count derived from
//              it), `column-gap`, and balancing: in-flow block children are
//              distributed column-major so the tallest column is as short as
//              the content allows. Direct `column-span: all` blocks separate
//              independently balanced sets; adjacent spanner margins collapse.
//
//   NOT ported nested spanner extraction, `column-rule`, `column-fill: auto`, breaking a
//              single child across a column boundary (a child taller than the
//              balanced height takes a column to itself and overflows rather
//              than fragmenting), orphans/widows, and fragmenting inline
//              content mid-line.
//
// The fragmentation gap is the significant one and is why
// `multicol_is_fully_ported()` returns false: real column layout can split a
// paragraph across columns, and this cannot.

namespace weva {

class BlockLayout;

// Returns the content height of the balanced sets and intervening spanners.
// font_size is the owning block's resolved size, including inherited/em sizing.
double layout_multicol(BoxTree* tree, BoxId container, double content_width, double font_size,
                       const LayoutContext& ctx, BlockLayout* block);

constexpr bool multicol_is_fully_ported() { return false; }

} // namespace weva
