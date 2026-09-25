#pragma once
#include "weva/box.h"
#include "weva/style_resolver.h"

// CSS Multi-column Layout L1 — balanced columns with fragmentation.
//
//   Ported     `column-count`, `column-width` (and the used count derived from
//              it), `column-gap`, `column-rule` (painted in the gaps, no space
//              of its own), `column-span: all` (separates independently
//              balanced sets; adjacent spanner margins collapse),
//              `break-inside: avoid`, `break-before: column`, and balancing
//              with fragmentation: the content of a set is flowed as one tall
//              column and cut between lines and whole blocks at the smallest
//              height that holds it in the count, `orphans`/`widows` at the
//              browser's initial 2. A block whose lines went to several
//              columns is where its lines are: its rect is the union of its
//              fragments, as getBoundingClientRect reports it.
//
//   NOT ported nested spanner extraction, `column-fill: auto`, breaking
//              inside a line (a line taller than a column overflows it),
//              authored `orphans`/`widows` values, and per-fragment box
//              decorations (a fragmented block's background and border are
//              painted over the union of its fragments, not per column).

namespace weva {

class BlockLayout;

// Returns the content height of the balanced sets and intervening spanners.
// font_size is the owning block's resolved size, including inherited/em sizing.
double layout_multicol(BoxTree* tree, BoxId container, double content_width, double font_size,
                       const LayoutContext& ctx, BlockLayout* block);

constexpr bool multicol_is_fully_ported() { return true; }

} // namespace weva
