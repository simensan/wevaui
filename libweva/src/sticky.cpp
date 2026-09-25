#include "weva/sticky.h"

#include "weva/positioning.h"
#include "weva/style_resolver.h"

#include <algorithm>
#include <optional>
#include <string_view>

// CSS Positioned Layout L3 §3.4 / §6.3: `position: sticky`. Layout leaves a
// sticky box in flow; this pass, run after layout and again whenever a
// scroll position moves, works out how far the box has to shift to stay
// inside its scroll container's scrollport -- pinned to the edge its inset
// names once it would scroll out, and released again when its containing
// block can carry it no further. Paint, hit testing and the visual position
// add the offsets; nothing else reads them, so the layout tree keeps the
// natural positions and a resize or restyle starts from truth.

namespace weva {

namespace {

// Each translation unit keeps its own copies of these two, as positioning.cpp
// and paint.cpp do.
std::string_view get(const ComputedStyle* s, std::string_view property) {
    return s ? s->get(property) : std::string_view();
}

bool iequals(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        char x = a[i], y = b[i];
        if (x >= 'A' && x <= 'Z') x = static_cast<char>(x - 'A' + 'a');
        if (y >= 'A' && y <= 'Z') y = static_cast<char>(y - 'A' + 'a');
        if (x != y) return false;
    }
    return true;
}

std::optional<double> inset(const ComputedStyle* style, std::string_view property,
                            const LayoutContext& ctx, double font_size, double basis) {
    const std::string_view raw = get(style, property);
    if (raw.empty() || iequals(raw, "auto")) return std::nullopt;
    const ResolvedLength r = resolve_length(style, property, ctx, font_size, basis);
    if (r.kind == LengthKind::Length) return r.pixels;
    if (r.kind == LengthKind::Percent) return basis * r.percent * 0.01;
    return std::nullopt;
}

struct Scroller {
    BoxId id;
    double ax, ay;   // absolute origin of its border box, natural coordinates
};

int walk(BoxTree* tree, BoxId id, double ax, double ay, const Scroller& scroller,
         const LayoutContext& ctx) {
    Box& b = (*tree)[id];
    int count = 0;
    b.sticky_offset_x = 0;
    b.sticky_offset_y = 0;
    if (b.position == PositionType::Sticky && b.style && b.parent != kNoBox) {
        ++count;
        const Box& a = (*tree)[scroller.id];
        // The scrollport: the scroll container's padding box, or the
        // viewport when the container is the document itself.
        double port_x = a.border_left, port_y = a.border_top;
        double port_w = a.width - a.border_left - a.border_right;
        double port_h = a.height - a.border_top - a.border_bottom;
        if (a.parent == kNoBox) {
            port_x = 0; port_y = 0;
            port_w = ctx.viewport_width_px;
            port_h = ctx.viewport_height_px;
        }
        port_w = std::max(0.0, port_w);
        port_h = std::max(0.0, port_h);
        const double view_left = port_x + a.scroll_x, view_top = port_y + a.scroll_y;
        const double view_right = view_left + port_w, view_bottom = view_top + port_h;

        // The box's natural border box and its containing block's content
        // box, both in the scroll container's coordinates.
        const double nx = ax - scroller.ax, ny = ay - scroller.ay;
        const Box& p = (*tree)[b.parent];
        double pax = ax - b.x, pay = ay - b.y;
        const double cb_left = pax - scroller.ax + p.border_left + p.padding_left;
        const double cb_top = pay - scroller.ay + p.border_top + p.padding_top;
        const double cb_right = cb_left + std::max(0.0, p.width - p.border_left - p.border_right -
                                                            p.padding_left - p.padding_right);
        const double cb_bottom = cb_top + std::max(0.0, p.height - p.border_top - p.border_bottom -
                                                            p.padding_top - p.padding_bottom);

        const ComputedStyle* ps = p.style;
        const double fs = font_size_px(b.style, ps, ctx);
        const std::optional<double> top = inset(b.style, "top", ctx, fs, port_h);
        const std::optional<double> bottom = inset(b.style, "bottom", ctx, fs, port_h);
        const std::optional<double> left = inset(b.style, "left", ctx, fs, port_w);
        const std::optional<double> right = inset(b.style, "right", ctx, fs, port_w);

        double dy = 0;
        if (top) {
            const double pinned = view_top + *top;
            if (pinned > ny) {
                const double max_y = cb_bottom - b.height - b.margin_bottom;
                dy = std::max(ny, std::min(pinned, max_y)) - ny;
            }
        }
        if (bottom && dy == 0) {
            const double pinned = view_bottom - *bottom - b.height;
            if (pinned < ny) {
                const double min_y = cb_top + b.margin_top;
                dy = std::min(ny, std::max(pinned, min_y)) - ny;
            }
        }
        double dx = 0;
        if (left) {
            const double pinned = view_left + *left;
            if (pinned > nx) {
                const double max_x = cb_right - b.width - b.margin_right;
                dx = std::max(nx, std::min(pinned, max_x)) - nx;
            }
        }
        if (right && dx == 0) {
            const double pinned = view_right - *right - b.width;
            if (pinned < nx) {
                const double min_x = cb_left + b.margin_left;
                dx = std::min(nx, std::max(pinned, min_x)) - nx;
            }
        }
        b.sticky_offset_x = dx;
        b.sticky_offset_y = dy;
    }
    // A descendant of a sticky box moves with it: its scroll container is
    // still the same one, and its natural position is what the offsets
    // above are measured from, so nothing changes for it here.
    Scroller next = scroller;
    if (id != scroller.id && establishes_scroll_container(b)) next = Scroller{id, ax, ay};
    for (BoxId c = b.first_child; c != kNoBox; c = (*tree)[c].next_sibling) {
        const Box& cb = (*tree)[c];
        count += walk(tree, c, ax + cb.x, ay + cb.y, next, ctx);
    }
    return count;
}

} // namespace

int resolve_sticky_offsets(BoxTree* tree, BoxId root, const LayoutContext& ctx) {
    if (!tree || !tree->valid(root)) return 0;
    const Box& r = (*tree)[root];
    return walk(tree, root, r.x, r.y, Scroller{root, r.x, r.y}, ctx);
}

} // namespace weva
