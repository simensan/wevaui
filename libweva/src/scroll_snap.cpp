#include "weva/scroll_snap.h"

#include "weva/positioning.h"

#include <algorithm>
#include <cmath>
#include <limits>
#include <string_view>
#include <vector>

namespace weva {

namespace {

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

std::string_view trim(std::string_view s) {
    while (!s.empty() && (s.front() == ' ' || s.front() == '\t')) s.remove_prefix(1);
    while (!s.empty() && (s.back() == ' ' || s.back() == '\t')) s.remove_suffix(1);
    return s;
}

// The space-separated words of a keyword value, in order.
std::vector<std::string_view> words_of(std::string_view raw) {
    std::vector<std::string_view> out;
    size_t i = 0;
    while (i < raw.size()) {
        while (i < raw.size() && (raw[i] == ' ' || raw[i] == '\t')) ++i;
        const size_t start = i;
        while (i < raw.size() && raw[i] != ' ' && raw[i] != '\t') ++i;
        if (i > start) out.push_back(raw.substr(start, i - start));
    }
    return out;
}

struct SnapType {
    bool mandatory = false;
    bool x = false, y = false;
};

// `scroll-snap-type: [ x | y | block | inline | both ] [ mandatory | proximity ]?`.
// An axis with no strictness is proximity, as the C# reads it.
SnapType snap_type_of(const ComputedStyle* style) {
    SnapType t;
    const std::string_view raw = trim(get(style, "scroll-snap-type"));
    if (raw.empty() || iequals(raw, "none")) return t;
    for (std::string_view w : words_of(raw)) {
        if (iequals(w, "x") || iequals(w, "inline")) { t.x = true; }
        else if (iequals(w, "y") || iequals(w, "block")) { t.y = true; }
        else if (iequals(w, "both")) { t.x = t.y = true; }
        else if (iequals(w, "mandatory")) t.mandatory = true;
        else if (iequals(w, "proximity")) t.mandatory = false;
    }
    if (!t.x && !t.y) t.x = t.y = true;   // a strictness alone means both
    return t;
}

enum class Align { None, Start, End, Center };

Align align_keyword(std::string_view w) {
    if (iequals(w, "start")) return Align::Start;
    if (iequals(w, "end")) return Align::End;
    if (iequals(w, "center")) return Align::Center;
    return Align::None;
}

// `scroll-snap-align: <block> <inline>?`: one keyword covers both axes.
Align align_of(const ComputedStyle* style, bool vertical) {
    const std::string_view raw = trim(get(style, "scroll-snap-align"));
    if (raw.empty() || iequals(raw, "none")) return Align::None;
    const std::vector<std::string_view> w = words_of(raw);
    if (w.empty()) return Align::None;
    if (w.size() == 1) return align_keyword(w[0]);
    return align_keyword(vertical ? w[0] : w[1]);
}

// A scroll-padding / scroll-margin side: the longhand when set, else the
// first value of the shorthand; `auto` and `none` are 0.
double side_length(const ComputedStyle* style, std::string_view longhand, std::string_view shorthand,
                   const LayoutContext& ctx, double font_size, double basis) {
    std::string_view raw = trim(get(style, longhand));
    if (raw.empty() || iequals(raw, "auto")) {
        const std::vector<std::string_view> w = words_of(get(style, shorthand));
        raw = w.empty() ? std::string_view() : w[0];
    }
    if (raw.empty() || iequals(raw, "auto") || iequals(raw, "none")) return 0;
    const ResolvedLength r = resolve_length(raw, ctx, font_size, basis);
    if (r.kind == LengthKind::Length) return r.pixels;
    if (r.kind == LengthKind::Percent) return basis * r.percent * 0.01;
    return 0;
}

struct Point {
    double pos;
    bool always;   // scroll-snap-stop: always
};

void collect(const BoxTree& tree, BoxId container, const LayoutContext& ctx, bool vertical,
             std::vector<Point>* out) {
    const Box& c = tree[container];
    double cax = 0, cay = 0;
    absolute_position(tree, container, &cax, &cay);
    const double port = std::max(0.0, vertical ? c.height - c.border_top - c.border_bottom
                                               : c.width - c.border_left - c.border_right);
    const ComputedStyle* cps = c.parent == kNoBox ? nullptr : tree[c.parent].style;
    const double cfs = font_size_px(c.style, cps, ctx);
    const double pad_start = side_length(c.style, vertical ? "scroll-padding-top" : "scroll-padding-left",
                                         "scroll-padding", ctx, cfs, port);
    const double pad_end = side_length(c.style, vertical ? "scroll-padding-bottom" : "scroll-padding-right",
                                       "scroll-padding", ctx, cfs, port);
    double mx = 0, my = 0;
    max_scroll(tree, container, &mx, &my);
    const double max = vertical ? my : mx;
    const double origin = vertical ? cay + c.border_top : cax + c.border_left;

    const auto visit = [&](auto&& self, BoxId id) -> void {
        for (BoxId ch = tree[id].first_child; ch != kNoBox; ch = tree[ch].next_sibling) {
            const Box& b = tree[ch];
            if (b.element && b.kind != BoxKind::Text && b.style) {
                const Align align = align_of(b.style, vertical);
                if (align != Align::None) {
                    double ax = 0, ay = 0;
                    absolute_position(tree, ch, &ax, &ay);
                    const double fs = font_size_px(b.style, tree[id].style, ctx);
                    const double m_start = side_length(b.style, vertical ? "scroll-margin-top" : "scroll-margin-left",
                                                       "scroll-margin", ctx, fs, port);
                    const double m_end = side_length(b.style, vertical ? "scroll-margin-bottom" : "scroll-margin-right",
                                                     "scroll-margin", ctx, fs, port);
                    const double local = (vertical ? ay : ax) - origin;
                    const double extent = vertical ? b.height : b.width;
                    const double top = local - m_start, bottom = local + extent + m_end;
                    double pos = 0;
                    switch (align) {
                        case Align::Start: pos = top - pad_start; break;
                        case Align::End: pos = bottom - port + pad_end; break;
                        case Align::Center: pos = (top + bottom) * 0.5 - port * 0.5; break;
                        default: break;
                    }
                    pos = std::clamp(pos, 0.0, std::max(0.0, max));
                    out->push_back({pos, iequals(trim(get(b.style, "scroll-snap-stop")), "always")});
                }
            }
            // A nested scroll container's areas snap in IT, not here.
            if (ch != container && clips_overflow(b)) continue;
            self(self, ch);
        }
    };
    visit(visit, container);
}

} // namespace

bool scroll_snaps(const ComputedStyle* style) {
    const SnapType t = snap_type_of(style);
    return t.x || t.y;
}

bool snap_target(const BoxTree& tree, BoxId container, const LayoutContext& ctx, bool vertical,
                 double start, double current, double* out) {
    if (!tree.valid(container)) return false;
    const Box& c = tree[container];
    const SnapType type = snap_type_of(c.style);
    if (!(vertical ? type.y : type.x)) return false;
    std::vector<Point> points;
    collect(tree, container, ctx, vertical, &points);
    if (points.empty()) return false;

    // §5.2: an area with `scroll-snap-stop: always` the movement passed over
    // must not be skipped; the first one from where the scroll began wins.
    if (start != current) {
        const double lo = std::min(start, current), hi = std::max(start, current);
        double best = std::numeric_limits<double>::infinity();
        bool found = false;
        for (const Point& p : points) {
            if (!p.always || p.pos <= lo || p.pos >= hi) continue;
            const double d = std::fabs(p.pos - start);
            if (d < best) { best = d; *out = p.pos; found = true; }
        }
        if (found) return true;
    }

    double best = std::numeric_limits<double>::infinity();
    double nearest = current;
    for (const Point& p : points) {
        const double d = std::fabs(p.pos - current);
        if (d < best) { best = d; nearest = p.pos; }
    }
    if (!type.mandatory) {
        const double port = std::max(0.0, vertical ? c.height - c.border_top - c.border_bottom
                                                   : c.width - c.border_left - c.border_right);
        if (port <= 0 || best > port * 0.5) return false;
    }
    *out = nearest;
    return true;
}

} // namespace weva
