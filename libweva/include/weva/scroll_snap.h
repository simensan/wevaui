#pragma once
#include "weva/box.h"
#include "weva/style_resolver.h"

// CSS Scroll Snap L1. Ports Runtime/Layout/Scrolling/Snap/*: the snap
// positions a scroll container's descendants define with
// `scroll-snap-align` (their `scroll-margin` and the container's
// `scroll-padding` included) and the choice of one for a scroll that has
// come to rest. The host-facing part -- when a wheel counts as settled, and
// the animation to the chosen position -- lives with the document.

namespace weva {

// Whether the container's `scroll-snap-type` names any axis.
bool scroll_snaps(const ComputedStyle* style);

// The snap position nearest `current` on one axis of `container`, for a
// scroll that moved there from `start`. A `scroll-snap-stop: always` area
// the movement passed over wins over the nearest; under `proximity` a
// position further than half the scrollport away is not taken. False when
// the container does not snap on that axis or nothing is chosen.
bool snap_target(const BoxTree& tree, BoxId container, const LayoutContext& ctx, bool vertical,
                 double start, double current, double* out);

} // namespace weva
