#pragma once
#include "weva/box.h"
#include "weva/computed_style.h"
#include <algorithm>

namespace weva {

struct RangeOrientation {
    bool vertical = false;
    bool reversed = false;
    explicit RangeOrientation(const ComputedStyle* style) {
        if (!style) return;
        const auto mode = style->get("writing-mode");
        vertical = mode == "vertical-rl" || mode == "vertical-lr";
        reversed = style->get("direction") == "rtl";
    }
};

// The thumb's centre travels between the ends of the content-box rail. Share
// this geometry between painting and input so endpoint clicks agree visually.
struct RangeTrack : RangeOrientation {
    double left, top, width, height, length, cross, rail, thumb, travel;
    RangeTrack(const Box& b, double x, double y) : RangeOrientation(b.style),
        left(x + b.border_left + b.padding_left),
        top(y + b.border_top + b.padding_top),
        width(b.content_width()), height(b.content_height()) {
        length = vertical ? height : width;
        // Preserve the control's existing fallback when padding/borders consume
        // the cross axis. A zero-length track still has no usable travel.
        cross = vertical ? (width > 0 ? width : b.width)
                         : (height > 0 ? height : b.height);
        rail = std::min(cross, 6.0);
        thumb = std::max(rail, std::min(cross, 14.0));
        travel = std::max(0.0, length - thumb);
    }
    double fraction(double x, double y) const {
        const double at = vertical ? y - top : x - left;
        const double f = travel > 0 ? std::clamp((at - thumb * .5) / travel, 0.0, 1.0) : 0;
        return reversed ? 1 - f : f;
    }
};
} // namespace weva
