#pragma once
#include "weva/box.h"
#include "weva/style_resolver.h"

// CSS Flexible Box Layout L1, §9.
//
// Direction, gaps, flex-grow/shrink/basis, justify-content and align-items
// including baseline, and `flex-wrap` — `wrap` and `wrap-reverse`, the latter
// swapping cross-start and cross-end per §5.2 — with align-content over the
// resulting lines. test_flex.cpp pins the wrapping cases.
//
// This comment used to say flex-wrap was not ported and that every container
// laid out as one line, and `flex_wrap_is_ported()` returned false to match,
// long after wrapping landed. A header a host can branch on is not the place
// for a stale note.

namespace weva {

class BlockLayout;

// Returns the container's content height. `content_width` is the container's
// inner width; `content_height` is its inner height when definite, or a
// negative value when it is not (which makes the cross size content-derived in
// a row container, and the main size indefinite in a column one).
double layout_flex(BoxTree* tree, BoxId container, double content_width, double content_height,
                   const LayoutContext& ctx, BlockLayout* block);

// `wrap` and `wrap-reverse` both lay out as multiple lines.
constexpr bool flex_wrap_is_ported() { return true; }

} // namespace weva
