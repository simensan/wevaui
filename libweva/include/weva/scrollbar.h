#pragma once
#include "weva/box.h"
#include "weva/geometry.h"
#include "weva/color.h"

// Where a scroll container's scrollbar goes, and what colour it is.
//
// Shared by paint and by hit testing on purpose: a scrollbar you can see in
// one place and grab in another is worse than none, and the only way to be
// sure they agree is for both to ask the same function.
//
// These are OVERLAY scrollbars -- drawn on top of the content, taking no
// layout space. A classic scrollbar reserves a gutter, which would move every
// box inside a scroller and put the engine's layout at odds with the reference
// captures it is measured against. Overlay is also what a game wants: the UI
// keeps the size the designer gave it.

namespace weva {

struct Scrollbar {
    bool visible = false;
    // Both in document coordinates.
    Rect track;
    Rect thumb;
    // What one pixel of thumb travel is worth in scroll offset, for dragging.
    double scroll_per_pixel = 0;
    LinearColor thumb_color;
    LinearColor track_color;
    double radius = 0;
};

// The scrollbar for one axis of `box`, whose border-box origin sits at
// (`ox`, `oy`) in document coordinates. Not visible when the box does not
// clip, has nowhere to scroll in that axis, or asks for `scrollbar-width:
// none`.
Scrollbar scrollbar_of(const BoxTree& tree, BoxId box, bool vertical, double ox, double oy);

}   // namespace weva
