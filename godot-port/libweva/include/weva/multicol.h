#pragma once
#include "weva/box.h"
#include "weva/style_resolver.h"

// CSS Multi-column Layout L1 — the BALANCED-COLUMNS subset.
//
//   Ported     `column-count`, `column-width` (and the used count derived from
//              it), `column-gap`, and balancing: in-flow block children are
//              distributed column-major so the tallest column is as short as
//              the content allows.
//
//   NOT ported `column-span`, `column-rule`, `column-fill: auto`, breaking a
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

// Returns the container's content height — the tallest column.
double layout_multicol(BoxTree* tree, BoxId container, double content_width,
                       const LayoutContext& ctx, BlockLayout* block);

constexpr bool multicol_is_fully_ported() { return false; }

} // namespace weva
