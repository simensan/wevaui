#pragma once
#include "weva/box.h"
#include "weva/style_resolver.h"

// CSS Flexible Box Layout L1, §9 — the SINGLE-LINE subset.
//
// Scope, stated up front because a partial flex implementation that pretends
// otherwise is worse than none: `flex-wrap` is not ported, so every container
// lays out as one line. Everything else in the corpus is here — direction,
// gaps, flex-grow/shrink/basis, justify-content and align-items including
// baseline.
//
// A wrapping container therefore lays out as a single overflowing line rather
// than as several. That is a visible, checkable difference, not a silent
// approximation, and `flex_wrap_is_ported()` exists so a caller can tell.

namespace weva {

class BlockLayout;

// Returns the container's content height. `content_width` is the container's
// inner width; `content_height` is its inner height when definite, or a
// negative value when it is not (which makes the cross size content-derived in
// a row container, and the main size indefinite in a column one).
double layout_flex(BoxTree* tree, BoxId container, double content_width, double content_height,
                   const LayoutContext& ctx, BlockLayout* block);

// False while `flex-wrap` is unported, so a caller can refuse rather than
// silently laying a multi-line container out as one line.
constexpr bool flex_wrap_is_ported() { return false; }

} // namespace weva
