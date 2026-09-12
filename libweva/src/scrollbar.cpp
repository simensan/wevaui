#include "weva/scrollbar.h"

#include "weva/computed_style.h"
#include "weva/css_value.h"
#include "weva/positioning.h"

#include <algorithm>
#include <cmath>

namespace weva {

namespace {

std::string_view get(const ComputedStyle* s, std::string_view property) {
    return s ? s->get(property) : std::string_view();
}

bool iequals(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        const char ca = a[i] >= 'A' && a[i] <= 'Z' ? static_cast<char>(a[i] + 32) : a[i];
        const char cb = b[i] >= 'A' && b[i] <= 'Z' ? static_cast<char>(b[i] + 32) : b[i];
        if (ca != cb) return false;
    }
    return true;
}

// `scrollbar-color: <thumb> <track>` (Scrollbars L1 3.2). `auto` on either
// side leaves the default.
bool scrollbar_colors(const ComputedStyle* style, LinearColor* thumb, LinearColor* track) {
    const std::string_view raw = get(style, "scrollbar-color");
    if (raw.empty() || iequals(raw, "auto")) return false;
    CssParseError err;
    CssValuePtr v = parse_css_value(raw, &err);
    if (!v) return false;
    // Two colours, in that order. A list of one is not the property.
    if (v->kind() != CssValueKind::List) return false;
    const auto& list = static_cast<const CssValueList&>(*v);
    if (list.items.size() < 2) return false;
    const auto as_color = [](const CssValue& value, LinearColor* out) {
        if (value.kind() != CssValueKind::Color) return false;
        const auto& c = static_cast<const CssColor&>(value);
        *out = LinearColor::from_srgb(c.r, c.g, c.b, c.a);
        return true;
    };
    LinearColor t, k;
    if (!as_color(*list.items[0], &t) || !as_color(*list.items[1], &k)) return false;
    *thumb = t;
    *track = k;
    return true;
}

}   // namespace

Scrollbar scrollbar_of(const BoxTree& tree, BoxId box, bool vertical, double ox, double oy) {
    Scrollbar bar;
    if (!tree.valid(box)) return bar;
    const Box& b = tree[box];
    if (!scrollable_on_axis(b, vertical)) return bar;

    // `scrollbar-width` (Scrollbars L1 3.1). `none` is how a game hides the
    // bar without giving up the scrolling.
    const std::string_view width_keyword = get(b.style, "scrollbar-width");
    if (iequals(width_keyword, "none")) return bar;
    const double thickness = iequals(width_keyword, "thin") ? 6.0 : 9.0;

    double mx = 0, my = 0;
    max_scroll(tree, box, &mx, &my);
    const double most = vertical ? my : mx;
    if (most <= 0) return bar;

    const double px0 = ox + b.border_left, py0 = oy + b.border_top;
    const double client_w = b.width - b.border_left - b.border_right;
    const double client_h = b.height - b.border_top - b.border_bottom;
    // The other axis, so two bars do not overlap in the corner.
    const bool other = (vertical ? mx > 0 : my > 0) && scrollable_on_axis(b, !vertical);
    const double gutter = other ? thickness : 0;

    const double track_len = (vertical ? client_h : client_w) - gutter;
    if (track_len <= 0) return bar;
    bar.track = vertical ? Rect(px0 + client_w - thickness, py0, thickness, track_len)
                         : Rect(px0, py0 + client_h - thickness, track_len, thickness);

    // The thumb is as long a share of the track as the view is of the whole,
    // with a floor so a very long list still leaves something to grab.
    const double visible = vertical ? client_h : client_w;
    const double whole = visible + most;
    const double thumb_len = std::max(24.0, track_len * (visible / whole));
    const double travel = std::max(0.0, track_len - thumb_len);
    const double at = vertical ? b.scroll_y : b.scroll_x;
    const double offset = travel * std::clamp(at / most, 0.0, 1.0);

    // Inset by a pixel so the thumb does not sit flush against the edge.
    const double inset = 1.0;
    bar.thumb = vertical ? Rect(bar.track.x + inset, bar.track.y + offset,
                                thickness - inset * 2, thumb_len)
                         : Rect(bar.track.x + offset, bar.track.y + inset, thumb_len,
                                thickness - inset * 2);
    bar.scroll_per_pixel = travel > 0 ? most / travel : 0;
    bar.radius = (thickness - inset * 2) * 0.5;

    // A mid grey at three-quarter alpha reads on a light page and a dark one
    // alike, which a host cannot choose for us: the document does not know
    // what is behind it. `scrollbar-color` overrides both.
    bar.thumb_color = LinearColor::from_srgb(135, 135, 140, 0.75f);
    bar.track_color = LinearColor::transparent();
    scrollbar_colors(b.style, &bar.thumb_color, &bar.track_color);
    bar.visible = true;
    return bar;
}

}   // namespace weva
