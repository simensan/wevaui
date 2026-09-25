#include "weva/background.h"
#include "parallel.h"
#include "weva/box.h"
#include "weva/style_resolver.h"

#include "weva/css_value.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <cstdlib>
#include <memory>
#include <type_traits>

#if (defined(__SSE2__) || defined(_M_X64)) && !defined(WEVA_BLUR_FORCE_PORTABLE)
#include <emmintrin.h>
#define WEVA_BLUR_SSE2 1
#else
#define WEVA_BLUR_SSE2 0
#endif

namespace weva {

namespace {

constexpr double kPi = 3.14159265358979323846;

bool is_space(char c) { return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f'; }

std::string_view trim(std::string_view s) {
    while (!s.empty() && is_space(s.front())) s.remove_prefix(1);
    while (!s.empty() && is_space(s.back())) s.remove_suffix(1);
    return s;
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

bool istarts_with(std::string_view s, std::string_view p) {
    return s.size() >= p.size() && iequals(s.substr(0, p.size()), p);
}

// Splits on a separator at paren depth 0, outside quotes.
std::vector<std::string_view> split_top_level(std::string_view s, char sep) {
    std::vector<std::string_view> out;
    int depth = 0;
    size_t start = 0;
    char quote = 0;
    for (size_t i = 0; i < s.size(); ++i) {
        const char c = s[i];
        if (quote) {
            if (c == '\\' && i + 1 < s.size()) ++i;
            else if (c == quote) quote = 0;
            continue;
        }
        if (c == '"' || c == '\'') { quote = c; continue; }
        if (c == '(') ++depth;
        else if (c == ')') { if (depth > 0) --depth; }
        else if (depth == 0 && (sep == ' ' ? is_space(c) : c == sep)) {
            const std::string_view piece = trim(s.substr(start, i - start));
            if (!piece.empty()) out.push_back(piece);
            start = i + 1;
        }
    }
    const std::string_view last = trim(s.substr(start));
    if (!last.empty()) out.push_back(last);
    return out;
}

bool parse_number(std::string_view s, double* out) {
    if (s.empty()) return false;
    const std::string tmp(s);
    char* end = nullptr;
    const double v = std::strtod(tmp.c_str(), &end);
    if (end == tmp.c_str() || *end != '\0') return false;
    *out = v;
    return true;
}

bool parse_angle_deg(std::string_view s, double* out) {
    s = trim(s);
    static const struct { const char* unit; double to_deg; } units[] = {
        {"deg", 1.0}, {"grad", 0.9}, {"rad", 180.0 / kPi}, {"turn", 360.0}};
    for (const auto& u : units) {
        const std::string_view unit(u.unit);
        if (s.size() > unit.size() && iequals(s.substr(s.size() - unit.size()), unit)) {
            double n = 0;
            if (!parse_number(s.substr(0, s.size() - unit.size()), &n)) return false;
            *out = n * u.to_deg;
            return true;
        }
    }
    double n = 0;
    if (parse_number(s, &n) && n == 0) { *out = 0; return true; }   // unitless zero
    return false;
}

bool parse_color_token(std::string_view s, const LinearColor& current, LinearColor* out) {
    s = trim(s);
    if (s.empty()) return false;
    if (iequals(s, "currentcolor")) { *out = current; return true; }
    CssParseError err;
    CssValuePtr v = parse_css_value(s, &err);
    if (!v || v->kind() != CssValueKind::Color) return false;
    const auto& c = static_cast<const CssColor&>(*v);
    *out = LinearColor::from_srgb(c.r, c.g, c.b, c.a);
    return true;
}

bool looks_like_color(std::string_view s, const LinearColor& current) {
    LinearColor c;
    return parse_color_token(s, current, &c);
}

// A stop position: `%`, `px` (kept as px), a bare number (fraction), or a
// calc() of either — `calc(var(--pct) * 1%)` after substitution is how a
// conic progress ring states its angle. A calc mentioning `%` resolves as a
// percentage (against 100, so the pixels ARE the percent), otherwise as px.
bool parse_stop_position(std::string_view s, GradientStop* stop) {
    s = trim(s);
    double n = 0;
    if (s.size() > 5 && iequals(s.substr(0, 5), "calc(")) {
        const bool pct = s.find('%') != std::string_view::npos;
        LayoutContext ctx;
        const ResolvedLength r =
            resolve_length(s, ctx, 16, pct ? std::optional<double>(100.0) : std::nullopt);
        if (r.kind == LengthKind::Length) {
            stop->position = pct ? r.pixels * 0.01 : r.pixels;
            stop->has_position = true;
            stop->is_px = !pct;
            return true;
        }
        return false;
    }
    if (!s.empty() && s.back() == '%' && parse_number(s.substr(0, s.size() - 1), &n)) {
        stop->position = n * 0.01;
        stop->has_position = true;
        stop->is_px = false;
        return true;
    }
    if (s.size() > 2 && iequals(s.substr(s.size() - 2), "px") &&
        parse_number(s.substr(0, s.size() - 2), &n)) {
        stop->position = n;
        stop->has_position = true;
        stop->is_px = true;
        return true;
    }
    if (parse_number(s, &n)) {
        stop->position = n;
        stop->has_position = true;
        stop->is_px = false;
        return true;
    }
    return false;
}

// A conic stop position is an angle or a percentage, as a fraction of a turn.
bool parse_conic_position(std::string_view s, GradientStop* stop) {
    s = trim(s);
    double n = 0;
    // calc() of a percentage — `calc(var(--pct) * 1%)` is the progress-ring
    // idiom; a calc of angles is not resolved here.
    if (s.size() > 5 && iequals(s.substr(0, 5), "calc(") && s.find('%') != std::string_view::npos) {
        LayoutContext ctx;
        const ResolvedLength r = resolve_length(s, ctx, 16, 100.0);
        if (r.kind != LengthKind::Length) return false;
        stop->position = r.pixels * 0.01;
        stop->has_position = true;
        return true;
    }
    if (!s.empty() && s.back() == '%' && parse_number(s.substr(0, s.size() - 1), &n)) {
        stop->position = n * 0.01;
        stop->has_position = true;
        return true;
    }
    double deg = 0;
    if (parse_angle_deg(s, &deg)) {
        stop->position = deg / 360.0;
        stop->has_position = true;
        return true;
    }
    if (parse_number(s, &n)) {
        stop->position = n;
        stop->has_position = true;
        return true;
    }
    return false;
}

// `<color> [<pos> [<pos>]]?` or a bare `<pos>` hint (§3.4.3, L4 §3.4.2).
void append_stops(std::string_view arg, const LinearColor& current, bool conic,
                  std::vector<GradientStop>* stops) {
    const std::vector<std::string_view> parts = split_top_level(arg, ' ');
    if (parts.empty()) return;
    LinearColor color;
    int color_idx = -1;
    for (size_t i = 0; i < parts.size(); ++i) {
        if (parse_color_token(parts[i], current, &color)) { color_idx = static_cast<int>(i); break; }
    }
    // A colour function with spaces inside its parentheses arrives as one
    // token thanks to split_top_level, but `rgb(0 0 0 / 50%)` still parses.
    if (color_idx < 0) {
        GradientStop hint;
        const bool ok = conic ? parse_conic_position(parts[0], &hint) : parse_stop_position(parts[0], &hint);
        if (ok && parts.size() == 1 && !stops->empty()) {
            hint.is_hint = true;
            stops->push_back(hint);
        }
        return;
    }
    std::vector<GradientStop> positions;
    for (size_t i = 0; i < parts.size(); ++i) {
        if (static_cast<int>(i) == color_idx) continue;
        GradientStop p;
        const bool ok = conic ? parse_conic_position(parts[i], &p) : parse_stop_position(parts[i], &p);
        if (ok) positions.push_back(p);
    }
    if (positions.empty()) {
        GradientStop s;
        s.color = color;
        stops->push_back(s);
        return;
    }
    for (GradientStop p : positions) {
        p.color = color;
        stops->push_back(p);
    }
}

// Which stops are hints is decided when parsing; here the positions are
// made concrete: px against the line length, missing ones spread evenly,
// and each no earlier than the one before (§3.4.3).
void normalize_stops(std::vector<GradientStop>* stops, double line_length) {
    if (stops->empty()) return;
    for (GradientStop& s : *stops) {
        if (s.has_position && s.is_px) {
            s.position = line_length > 0 ? s.position / line_length : 0;
            s.is_px = false;
        }
    }
    GradientStop& first = stops->front();
    if (!first.has_position) { first.position = 0; first.has_position = true; }
    GradientStop& last = stops->back();
    if (!last.has_position) { last.position = 1; last.has_position = true; }
    double running = -1e300;
    for (GradientStop& s : *stops) {
        if (s.has_position) {
            if (s.position < running) s.position = running;
            running = s.position;
        }
    }
    size_t i = 0;
    while (i < stops->size()) {
        if ((*stops)[i].has_position) { ++i; continue; }
        size_t j = i;
        while (j < stops->size() && !(*stops)[j].has_position) ++j;
        const double a = (*stops)[i - 1].position;
        const double b = (*stops)[j].position;
        const size_t n = j - i + 1;
        for (size_t k = i; k < j; ++k) {
            (*stops)[k].position = a + (b - a) * static_cast<double>(k - i + 1) / static_cast<double>(n);
            (*stops)[k].has_position = true;
        }
        i = j;
    }
}

float linear_to_srgb(float v) {
    if (v <= 0) return 0;
    if (v >= 1) return 1;
    return v <= 0.0031308f ? v * 12.92f : 1.055f * std::pow(v, 1.0f / 2.4f) - 0.055f;
}

struct Srgb { float r, g, b, a; };

Srgb to_srgb(const LinearColor& c) {
    return {linear_to_srgb(c.r), linear_to_srgb(c.g), linear_to_srgb(c.b), std::clamp(c.a, 0.0f, 1.0f)};
}

// The endpoints and their difference in premultiplied sRGB. These depend
// only on the stops, so prepare them once instead of multiplying both
// endpoints and subtracting them again for every raster sample.
struct ColorSpan {
    Srgb start;
    Srgb delta;
    double inverse_length = 0;
    bool constant = false;
};

ColorSpan color_span(const Srgb& a, const Srgb& b) {
    const Srgb pa{a.r * a.a, a.g * a.a, a.b * a.a, a.a};
    const Srgb pb{b.r * b.a, b.g * b.a, b.b * b.a, b.a};
    ColorSpan span{pa, {pb.r - pa.r, pb.g - pa.g, pb.b - pa.b, pb.a - pa.a}};
    span.constant = span.delta.r == 0 && span.delta.g == 0 &&
                    span.delta.b == 0 && span.delta.a == 0;
    if (span.constant) {
        // Store the exact scalar result, including its multiply/divide
        // rounding. Returning the original unpremultiplied stop can differ.
        span.start = pa.a > 0 ? Srgb{pa.r / pa.a, pa.g / pa.a, pa.b / pa.a, pa.a}
                             : Srgb{0, 0, 0, 0};
    }
    return span;
}

// Interpolates in premultiplied sRGB (Images L4 §3.4.1), so a fade to
// `transparent` does not pass through grey. Keep the arithmetic order and
// unpremultiplication used by the scalar sampler.
Srgb mix(const ColorSpan& span, double t) {
    // Clamp by value. MSVC's reference-returning std::clamp spills these hot
    // operands and selects a stack address before loading the result.
    const float ft = static_cast<float>(t < 0.0 ? 0.0 : t > 1.0 ? 1.0 : t);
    const float alpha = span.start.a + span.delta.a * ft;
    const float r = span.start.r + span.delta.r * ft;
    const float g = span.start.g + span.delta.g * ft;
    const float bb = span.start.b + span.delta.b * ft;
    if (alpha <= 0) return {0, 0, 0, 0};
    return {r / alpha, g / alpha, bb / alpha, alpha};
}

// `x` folded into [0, m), without calling libm.
//
// The idiom this replaces -- fmod(fmod(x, m) + m, m) -- is TWO calls into libm
// per use, and the rasterizer's inner loop can reach four of them per sample:
// two to wrap a repeating tile on each axis, two more inside a repeating
// gradient's stop lookup. On map.html, whose page background is a pair of
// repeating 80px grid-line gradients across the whole viewport, that came to
// some six million fmod calls and 88 of its 89 ms of paint.
//
// floor() is a single instruction where fmod is a call with argument reduction
// in it. The clamps at the end are not decoration: the multiply-and-subtract
// can land a hair below zero or a hair on m where fmod would have returned
// exactly one end or the other, and every caller relies on the half-open range.
inline double wrap_positive(double x, double m) {
    if (!(m > 0)) return 0;
    const double r = x - m * std::floor(x / m);
    if (r < 0) return 0;
    if (r >= m) return 0;   // rounded up onto the period end, which IS zero
    return r;
}

// A <position> component: keyword, percentage or length, as an offset in px
// for a `tile` inside an `area`.
double resolve_position(std::string_view raw, double area, double tile, bool horizontal,
                        const LayoutContext& ctx, double font_size) {
    raw = trim(raw);
    double pct = -1;
    if (iequals(raw, "center")) pct = 50;
    else if (iequals(raw, horizontal ? "left" : "top")) pct = 0;
    else if (iequals(raw, horizontal ? "right" : "bottom")) pct = 100;
    if (pct >= 0) return (area - tile) * pct * 0.01;

    // Asked WITHOUT a basis first, so a percentage comes back as one.
    //
    // A background position's percentage aligns the same point of the tile
    // with that point of the area -- 50% centres it -- which is
    // (area - tile) * pct, not area * pct. Resolving it against `area` up
    // front made every percentage arrive as a plain length and take the
    // branch below, so the tile was pushed half the area to the right instead
    // of being centred. It stayed invisible for as long as backgrounds were
    // only gradients: a gradient sizes to the area, and (area - tile) is then
    // zero, so both readings agree at every position. An image has a size of
    // its own and does not.
    const ResolvedLength percent = resolve_length(raw, ctx, font_size, std::nullopt);
    if (percent.kind == LengthKind::Percent) return (area - tile) * percent.percent * 0.01;

    // Anything else -- a length, a calc() that needs the basis to become one
    // at all -- resolves against the area as before.
    const ResolvedLength r = resolve_length(raw, ctx, font_size, area);
    if (r.kind == LengthKind::Percent) return (area - tile) * r.percent * 0.01;
    if (r.kind == LengthKind::Length) return r.pixels;
    return 0;
}

// A radial centre coordinate resolves against the box, no tile involved.
double resolve_center(std::string_view raw, double extent, bool horizontal, const LayoutContext& ctx,
                      double font_size) {
    return resolve_position(raw, extent, 0, horizontal, ctx, font_size);
}

double resolve_size_component(std::string_view raw, double extent, const LayoutContext& ctx,
                              double font_size) {
    const ResolvedLength r = resolve_length(trim(raw), ctx, font_size, extent);
    if (r.kind == LengthKind::Percent) return extent * r.percent * 0.01;
    if (r.kind == LengthKind::Length) return r.pixels;
    return extent;
}

// `<position>` as two raw components; one value means the other is centred,
// and a vertical keyword first swaps the axes.
void split_position(const std::vector<std::string_view>& toks, size_t from, size_t to, std::string* x,
                    std::string* y) {
    std::vector<std::string_view> p(toks.begin() + static_cast<long>(from),
                                    toks.begin() + static_cast<long>(to));
    if (p.empty()) { *x = "50%"; *y = "50%"; return; }
    if (p.size() == 1) {
        if (iequals(p[0], "top") || iequals(p[0], "bottom")) { *x = "50%"; *y = std::string(p[0]); }
        else { *x = std::string(p[0]); *y = "50%"; }
        return;
    }
    std::string_view a = p[0], b = p[1];
    if (iequals(a, "top") || iequals(a, "bottom") || iequals(b, "left") || iequals(b, "right")) {
        std::swap(a, b);
    }
    *x = std::string(a);
    *y = std::string(b);
}

bool parse_linear_prefix(std::string_view arg, Gradient* g) {
    const std::vector<std::string_view> toks = split_top_level(arg, ' ');
    if (toks.empty()) return false;
    // `in <space> [<method> hue]` is parsed away: interpolation stays sRGB.
    std::vector<std::string_view> rest;
    for (size_t i = 0; i < toks.size(); ++i) {
        if (iequals(toks[i], "in")) {
            ++i;   // the colour space
            if (i + 2 < toks.size() && iequals(toks[i + 2], "hue")) i += 2;
            continue;
        }
        rest.push_back(toks[i]);
    }
    if (rest.empty()) return true;   // interpolation only
    double deg = 0;
    if (rest.size() == 1 && parse_angle_deg(rest[0], &deg)) {
        g->angle_deg = deg;
        return true;
    }
    if (!iequals(rest[0], "to")) return false;
    int corner = 0;
    for (size_t i = 1; i < rest.size(); ++i) {
        if (iequals(rest[i], "left")) corner |= 1;
        else if (iequals(rest[i], "right")) corner |= 2;
        else if (iequals(rest[i], "top")) corner |= 4;
        else if (iequals(rest[i], "bottom")) corner |= 8;
        else return false;
    }
    switch (corner) {
        case 4: g->angle_deg = 0; break;
        case 2: g->angle_deg = 90; break;
        case 8: g->angle_deg = 180; break;
        case 1: g->angle_deg = 270; break;
        default: g->corner = corner; break;   // a corner: resolved per box
    }
    return true;
}

bool looks_like_radial_prefix(std::string_view arg) {
    const std::vector<std::string_view> toks = split_top_level(arg, ' ');
    if (toks.empty()) return false;
    static const char* words[] = {"circle", "ellipse", "closest-side", "farthest-side",
                                  "closest-corner", "farthest-corner", "at", "in"};
    for (std::string_view t : toks) {
        for (const char* w : words) if (iequals(t, w)) return true;
    }
    // Explicit radii: every token is a length/percentage and nothing is a colour.
    for (std::string_view t : toks) {
        const ResolvedLength r = resolve_length(t, LayoutContext{}, 16, 100);
        if (r.kind != LengthKind::Length && r.kind != LengthKind::Percent) return false;
    }
    return true;
}

void parse_radial_prefix(std::string_view arg, Gradient* g) {
    const std::vector<std::string_view> toks = split_top_level(arg, ' ');
    std::vector<std::string_view> sizes;
    for (size_t i = 0; i < toks.size(); ++i) {
        const std::string_view t = toks[i];
        if (iequals(t, "circle")) g->circle = true;
        else if (iequals(t, "ellipse")) g->circle = false;
        else if (iequals(t, "closest-side")) g->sizing = Gradient::Sizing::ClosestSide;
        else if (iequals(t, "farthest-side")) g->sizing = Gradient::Sizing::FarthestSide;
        else if (iequals(t, "closest-corner")) g->sizing = Gradient::Sizing::ClosestCorner;
        else if (iequals(t, "farthest-corner")) g->sizing = Gradient::Sizing::FarthestCorner;
        else if (iequals(t, "at")) {
            split_position(toks, i + 1, toks.size(), &g->pos_x_raw, &g->pos_y_raw);
            break;
        } else if (iequals(t, "in")) {
            ++i;
            if (i + 2 < toks.size() && iequals(toks[i + 2], "hue")) i += 2;
        } else {
            sizes.push_back(t);
        }
    }
    if (!sizes.empty()) {
        g->sizing = Gradient::Sizing::Explicit;
        g->radius_x_raw = std::string(sizes[0]);
        g->radius_y_raw = std::string(sizes.size() > 1 ? sizes[1] : sizes[0]);
        if (sizes.size() == 1) g->circle = true;
    }
}

bool looks_like_conic_prefix(std::string_view arg) {
    return istarts_with(trim(arg), "from ") || istarts_with(trim(arg), "at ") ||
           istarts_with(trim(arg), "in ");
}

void parse_conic_prefix(std::string_view arg, Gradient* g) {
    const std::vector<std::string_view> toks = split_top_level(arg, ' ');
    for (size_t i = 0; i < toks.size(); ++i) {
        if (iequals(toks[i], "from") && i + 1 < toks.size()) {
            double deg = 0;
            if (parse_angle_deg(toks[i + 1], &deg)) g->from_deg = deg;
            ++i;
        } else if (iequals(toks[i], "at")) {
            split_position(toks, i + 1, toks.size(), &g->pos_x_raw, &g->pos_y_raw);
            break;
        } else if (iequals(toks[i], "in")) {
            ++i;
            if (i + 2 < toks.size() && iequals(toks[i + 2], "hue")) i += 2;
        }
    }
}

// The gradient line's angle for a box (§3.1.1): a corner keyword points the
// line so that its perpendicular through the centre passes through the two
// neighbouring corners.
double linear_angle_for(const Gradient& g, double w, double h) {
    if (!g.corner) return g.angle_deg;
    const double a = std::atan2(w, h) * 180.0 / kPi;   // "to top right"
    switch (g.corner) {
        case 2 | 4: return a;              // top right
        case 2 | 8: return 180 - a;        // bottom right
        case 1 | 8: return 180 + a;        // bottom left
        case 1 | 4: return 360 - a;        // top left
        default: return g.angle_deg;
    }
}

struct RadialGeometry {
    double cx, cy, rx, ry;
};

RadialGeometry radial_geometry(const Gradient& g, double w, double h, const LayoutContext& ctx,
                               double font_size) {
    RadialGeometry r;
    r.cx = resolve_center(g.pos_x_raw, w, true, ctx, font_size);
    r.cy = resolve_center(g.pos_y_raw, h, false, ctx, font_size);
    const double near_x = std::min(r.cx, w - r.cx), far_x = std::max(r.cx, w - r.cx);
    const double near_y = std::min(r.cy, h - r.cy), far_y = std::max(r.cy, h - r.cy);
    const double sqrt2 = 1.4142135623730951;
    switch (g.sizing) {
        case Gradient::Sizing::Explicit:
            r.rx = resolve_size_component(g.radius_x_raw, w, ctx, font_size);
            r.ry = resolve_size_component(g.radius_y_raw, h, ctx, font_size);
            if (g.circle) r.ry = r.rx;
            break;
        case Gradient::Sizing::ClosestSide:
            if (g.circle) r.rx = r.ry = std::min(near_x, near_y);
            else { r.rx = near_x; r.ry = near_y; }
            break;
        case Gradient::Sizing::FarthestSide:
            if (g.circle) r.rx = r.ry = std::max(far_x, far_y);
            else { r.rx = far_x; r.ry = far_y; }
            break;
        case Gradient::Sizing::ClosestCorner:
            if (g.circle) r.rx = r.ry = std::sqrt(near_x * near_x + near_y * near_y);
            else { r.rx = near_x * sqrt2; r.ry = near_y * sqrt2; }
            break;
        case Gradient::Sizing::FarthestCorner:
        default:
            if (g.circle) r.rx = r.ry = std::sqrt(far_x * far_x + far_y * far_y);
            else { r.rx = far_x * sqrt2; r.ry = far_y * sqrt2; }
            break;
    }
    if (r.rx <= 0) r.rx = 1e-6;
    if (r.ry <= 0) r.ry = 1e-6;
    return r;
}

// Everything about one gradient that does not change per pixel.
struct PreparedGradient {
    const Gradient* g = nullptr;
    std::vector<GradientStop> stops;
    std::vector<Srgb> colors;
    double angle_rad = 0, dx = 0, dy = 0, line_length = 1, cx = 0, cy = 0;
    RadialGeometry radial{};
    // Reciprocals of the three divisors gradient_t would otherwise divide by
    // ONCE PER PIXEL. A full-page background is a million pixels and a divide
    // is four or five times a multiply, so these are worth precomputing even
    // though the arithmetic is trivial.
    double inv_line_length = 1, inv_rx = 1, inv_ry = 1;
    // Premultiplied color endpoints and reciprocal length for each span,
    // indexed by the FIRST stop of the pair.
    std::vector<ColorSpan> color_spans;
    // The pairs sample_stops visits, in visiting order, with everything about
    // a pair that does not depend on `t` resolved up front. The walk it
    // replaces re-derived each pair from the stop list per SAMPLE -- skipping
    // hints, finding `next`, reading positions out of 40-byte stops -- and
    // took two logs for a colour hint's exponent every time. sample_stops is
    // a fifth of hud's cold load; this is the part of it that is not the
    // interpolation itself.
    struct Segment {
        double p0 = 0, p1 = 0;
        double inverse_length = 0;   // as ColorSpan::inverse_length
        double hint_exponent = 0;    // pow() exponent, when `hinted`
        int first = 0;               // index of the pair's first stop
        bool hinted = false;         // a hint between, over a nonzero span
        bool terminal = false;       // no colour stop after `first`
    };
    std::vector<Segment> segments;
    // Whether this gradient has a discontinuity, which decides whether the
    // texel needs more than one sample. A ramp antialiases itself; an edge
    // does not.
    bool has_hard_edge = false;
};

PreparedGradient prepare(const Gradient& g, double w, double h, const LayoutContext& ctx,
                         double font_size) {
    PreparedGradient p;
    p.g = &g;
    p.stops = g.stops;
    p.cx = w * 0.5;
    p.cy = h * 0.5;
    double length = 1;
    if (g.kind == Gradient::Kind::Linear) {
        const double deg = linear_angle_for(g, w, h);
        p.angle_rad = deg * kPi / 180.0;
        p.dx = std::sin(p.angle_rad);
        p.dy = -std::cos(p.angle_rad);
        length = std::fabs(w * p.dx) + std::fabs(h * p.dy);
        p.line_length = length > 0 ? length : 1;
    } else if (g.kind == Gradient::Kind::Radial) {
        p.radial = radial_geometry(g, w, h, ctx, font_size);
        // A px stop position on a radial gradient is measured along the
        // horizontal radius.
        length = p.radial.rx;
    } else {
        p.cx = resolve_center(g.pos_x_raw, w, true, ctx, font_size);
        p.cy = resolve_center(g.pos_y_raw, h, false, ctx, font_size);
        length = 1;
    }
    normalize_stops(&p.stops, length);
    for (const GradientStop& s : p.stops) p.colors.push_back(to_srgb(s.color));

    // A conic gradient always closes on itself, so its first and last stops
    // meet along a radius whatever they are; the others only have an edge
    // where two stops share a position, which is how `#a 72%, #b 0` states a
    // hard band.
    if (g.kind == Gradient::Kind::Conic) {
        p.has_hard_edge = true;
    } else {
        for (size_t i = 1; i < p.stops.size(); ++i) {
            if (std::fabs(p.stops[i].position - p.stops[i - 1].position) < 1e-6) {
                p.has_hard_edge = true;
                break;
            }
        }
    }

    // The reciprocals gradient_t and sample_stops would otherwise recompute
    // for every pixel. Guarded: a zero divisor kept its old behaviour of
    // producing an infinity or a nan, and 1 here keeps the same shape without
    // one -- the callers already treat a degenerate gradient as flat.
    p.inv_line_length = p.line_length != 0 ? 1.0 / p.line_length : 1.0;
    p.inv_rx = p.radial.rx != 0 ? 1.0 / p.radial.rx : 1.0;
    p.inv_ry = p.radial.ry != 0 ? 1.0 / p.radial.ry : 1.0;
    // Indexed by the FIRST stop of each pair, and resolving `next` exactly as
    // sample_stops does -- a hint sits between two colour stops, so the pair is
    // not always (i, i+1) and a table that assumed so would divide by the wrong
    // span wherever a hint appeared.
    p.color_spans.resize(p.stops.size());
    for (size_t i = 0; i + 1 < p.stops.size(); ++i) {
        if (p.stops[i].is_hint) continue;
        size_t next = i + 1;
        if (next < p.stops.size() && p.stops[next].is_hint) ++next;
        if (next >= p.stops.size()) continue;
        p.color_spans[i] = color_span(p.colors[i], p.colors[next]);
        const double span = p.stops[next].position - p.stops[i].position;
        p.color_spans[i].inverse_length = span > 0 ? 1.0 / span : 0.0;
    }
    // The same walk, once. A pair whose hint is the last stop has no colour
    // after it: the sample stops there with the pair's first colour, whatever
    // `t` is, so it ends the table.
    for (size_t i = 0; i + 1 < p.stops.size(); ++i) {
        if (p.stops[i].is_hint) continue;
        size_t next = i + 1;
        const GradientStop* hint = nullptr;
        if (p.stops[next].is_hint) {
            hint = &p.stops[next];
            ++next;
        }
        PreparedGradient::Segment seg;
        seg.first = static_cast<int>(i);
        if (next >= p.stops.size()) {
            seg.terminal = true;
            p.segments.push_back(seg);
            break;
        }
        seg.p0 = p.stops[i].position;
        seg.p1 = p.stops[next].position;
        seg.inverse_length = p.color_spans[i].inverse_length;
        if (hint && seg.p1 > seg.p0) {
            const double h = std::clamp((hint->position - seg.p0) / (seg.p1 - seg.p0), 1e-6, 1 - 1e-6);
            seg.hinted = true;
            seg.hint_exponent = std::log(0.5) / std::log(h);
        }
        p.segments.push_back(seg);
    }
    return p;
}

// The colour at `t` along normalized stops. Endpoints retain their original
// sRGB values; interior samples use the prepared premultiplied spans. The
// first segment containing `t` wins, as it did when this walked the stops:
// at a hard edge two segments share the position and the earlier one owns it.
Srgb sample_stops(const PreparedGradient& p, double t) {
    const std::vector<GradientStop>& stops = p.stops;
    const size_t count = stops.size();
    if (count == 0) return {0, 0, 0, 0};
    const Srgb* colors = p.colors.data();
    if (count == 1) return colors[0];
    const double first = stops.front().position;
    const double last = stops.back().position;
    if (p.g->repeating && last > first) {
        const double span = last - first;
        t = first + wrap_positive(t - first, span);
    }
    if (t <= first) return colors[0];
    if (t >= last) return colors[count - 1];
    for (const PreparedGradient::Segment& seg : p.segments) {
        if (seg.terminal) return colors[seg.first];
        if (t < seg.p0 || t > seg.p1) continue;
        const ColorSpan& span = p.color_spans[static_cast<size_t>(seg.first)];
        if (span.constant) return span.start;
        double local = 0;
        if (seg.inverse_length > 0) {
            local = (t - seg.p0) * seg.inverse_length;
        } else if (seg.p1 > seg.p0) {
            local = (t - seg.p0) / (seg.p1 - seg.p0);
        }
        if (seg.hinted) local = std::pow(local, seg.hint_exponent);
        return mix(span, local);
    }
    return colors[count - 1];
}

// The gradient's parameter at a point, before any stop is consulted.
double gradient_t(const PreparedGradient& p, double x, double y) {
    double t = 0;
    switch (p.g->kind) {
        case Gradient::Kind::Linear:
            t = ((x - p.cx) * p.dx + (y - p.cy) * p.dy) * p.inv_line_length + 0.5;
            break;
        case Gradient::Kind::Radial: {
            const double ex = (x - p.radial.cx) * p.inv_rx;
            const double ey = (y - p.radial.cy) * p.inv_ry;
            t = std::sqrt(ex * ex + ey * ey);
            break;
        }
        case Gradient::Kind::Conic: {
            double a = std::atan2(x - p.cx, -(y - p.cy));   // 0 at top, clockwise
            a -= p.g->from_deg * kPi / 180.0;
            t = wrap_positive(a, 2 * kPi) / (2 * kPi);
            break;
        }
    }
    return t;
}

Srgb sample_prepared(const PreparedGradient& p, double x, double y) {
    return sample_stops(p, gradient_t(p, x, y));
}

} // namespace

bool parse_gradient(std::string_view raw, const LinearColor& current_color, Gradient* out) {
    raw = trim(raw);
    const size_t open = raw.find('(');
    if (open == std::string_view::npos || raw.back() != ')') return false;
    std::string name(trim(raw.substr(0, open)));
    for (char& c : name) c = static_cast<char>(c >= 'A' && c <= 'Z' ? c - 'A' + 'a' : c);
    Gradient g;
    if (name == "linear-gradient" || name == "repeating-linear-gradient") {
        g.kind = Gradient::Kind::Linear;
    } else if (name == "radial-gradient" || name == "repeating-radial-gradient") {
        g.kind = Gradient::Kind::Radial;
    } else if (name == "conic-gradient" || name == "repeating-conic-gradient") {
        g.kind = Gradient::Kind::Conic;
    } else {
        return false;
    }
    g.repeating = name.rfind("repeating-", 0) == 0;
    const std::string_view body = raw.substr(open + 1, raw.size() - open - 2);
    const std::vector<std::string_view> args = split_top_level(body, ',');
    if (args.empty()) return false;
    size_t first_stop = 0;
    const std::string_view first = args[0];
    const bool first_is_color = looks_like_color(split_top_level(first, ' ').empty()
                                                     ? first
                                                     : split_top_level(first, ' ')[0],
                                                 current_color);
    if (!first_is_color) {
        if (g.kind == Gradient::Kind::Linear) {
            if (parse_linear_prefix(first, &g)) first_stop = 1;
        } else if (g.kind == Gradient::Kind::Radial) {
            if (looks_like_radial_prefix(first)) { parse_radial_prefix(first, &g); first_stop = 1; }
        } else if (looks_like_conic_prefix(first)) {
            parse_conic_prefix(first, &g);
            first_stop = 1;
        }
    }
    for (size_t i = first_stop; i < args.size(); ++i) {
        append_stops(args[i], current_color, g.kind == Gradient::Kind::Conic, &g.stops);
    }
    size_t colored = 0;
    for (const GradientStop& s : g.stops) if (!s.is_hint) ++colored;
    if (colored < 2) return false;
    *out = std::move(g);
    return true;
}

namespace {

// One image-list property with its position/size/repeat companions, read
// the way background-image and mask-image share a grammar (CSS Backgrounds
// L3 §2.1: the companion lists repeat when shorter than the image list).
void read_image_layers(const ComputedStyle* style, const char* image_prop, const char* pos_prop,
                       const char* size_prop, const char* repeat_prop, const LinearColor& current_color,
                       bool as_mask, std::vector<BackgroundLayer>* layers) {
    const std::string_view images = trim(style->get(image_prop));
    if (images.empty() || iequals(images, "none")) return;
    const std::vector<std::string_view> image_list = split_top_level(images, ',');
    const std::vector<std::string_view> positions = split_top_level(style->get(pos_prop), ',');
    const std::vector<std::string_view> sizes = split_top_level(style->get(size_prop), ',');
    const std::vector<std::string_view> repeats = split_top_level(style->get(repeat_prop), ',');
    const std::vector<std::string_view> blends =
        as_mask ? std::vector<std::string_view>() : split_top_level(style->get("background-blend-mode"), ',');
    const std::vector<std::string_view> modes =
        as_mask ? split_top_level(style->get("mask-mode"), ',') : std::vector<std::string_view>();
    const std::vector<std::string_view> attachments =
        as_mask ? std::vector<std::string_view>() : split_top_level(style->get("background-attachment"), ',');
    for (size_t i = 0; i < image_list.size(); ++i) {
        const std::string_view img = trim(image_list[i]);
        if (img.empty() || iequals(img, "none")) continue;
        BackgroundLayer layer;
        layer.is_mask = as_mask;
        if (istarts_with(img, "url(")) {
            std::string_view inner = trim(img.substr(4, img.size() - 5));
            if (inner.size() >= 2 && (inner.front() == '"' || inner.front() == '\'')) {
                inner = inner.substr(1, inner.size() - 2);
            }
            layer.url = std::string(inner);
        } else if (parse_gradient(img, current_color, &layer.gradient)) {
            layer.is_gradient = true;
        } else {
            continue;
        }
        if (!positions.empty()) {
            const std::vector<std::string_view> toks =
                split_top_level(positions[i % positions.size()], ' ');
            split_position(toks, 0, toks.size(), &layer.pos_x, &layer.pos_y);
            if (toks.size() == 1 && !(iequals(toks[0], "top") || iequals(toks[0], "bottom"))) {
                layer.pos_y = "50%";
            }
        }
        if (!sizes.empty()) {
            const std::vector<std::string_view> toks = split_top_level(sizes[i % sizes.size()], ' ');
            if (!toks.empty()) {
                layer.size_x = std::string(toks[0]);
                layer.size_y = std::string(toks.size() > 1 ? toks[1] : "auto");
            }
        }
        if (!repeats.empty()) {
            const std::vector<std::string_view> toks = split_top_level(repeats[i % repeats.size()], ' ');
            if (!toks.empty()) {
                const std::string_view a = toks[0];
                const std::string_view b = toks.size() > 1 ? toks[1] : a;
                if (iequals(a, "repeat-x")) { layer.repeat_x = true; layer.repeat_y = false; }
                else if (iequals(a, "repeat-y")) { layer.repeat_x = false; layer.repeat_y = true; }
                else {
                    layer.repeat_x = !iequals(a, "no-repeat");
                    layer.repeat_y = !iequals(b, "no-repeat");
                }
            }
        }
        if (!blends.empty()) layer.blend = blend_mode_from_keyword(blends[i % blends.size()]);
        if (!attachments.empty()) {
            const std::string_view att = trim(attachments[i % attachments.size()]);
            layer.attachment_fixed = iequals(att, "fixed");
            layer.attachment_local = iequals(att, "local");
        }
        // mask-mode: `luminance` reads the image's brightness; `alpha` and
        // `match-source` (an alpha mask for anything the engine draws) its alpha.
        if (!modes.empty()) layer.mask_luminance = iequals(trim(modes[i % modes.size()]), "luminance");
        layers->push_back(std::move(layer));
    }
}

} // namespace

void apply_background_attachment(std::vector<BackgroundLayer>* layers, const Box& box,
                                 const LayoutContext& ctx, double x, double y) {
    for (BackgroundLayer& l : *layers) {
        if (l.attachment_fixed) {
            // The viewport is the positioning area; the box sees the part of
            // it that lies under its own origin.
            l.area_w = ctx.viewport_width_px;
            l.area_h = ctx.viewport_height_px;
            l.shift_x = -x;
            l.shift_y = -y;
        } else if (l.attachment_local && (box.scroll_x != 0 || box.scroll_y != 0)) {
            // The layer moves with the content this box scrolls.
            l.shift_x = -box.scroll_x;
            l.shift_y = -box.scroll_y;
        }
    }
}

std::vector<BackgroundLayer> resolve_background_layers(const ComputedStyle* style,
                                                       const LinearColor& current_color) {
    std::vector<BackgroundLayer> layers;
    if (!style) return layers;
    read_image_layers(style, "background-image", "background-position", "background-size",
                      "background-repeat", current_color, false, &layers);
    read_image_layers(style, "mask-image", "mask-position", "mask-size", "mask-repeat",
                      current_color, true, &layers);
    return layers;
}

namespace {

// A raw <position> or <length-percentage> that resolves proportionally to the
// box: a percentage, or one of the keywords, which are percentages by another
// name. Anything absolute -- px, em, a calc() mixing the two -- does not.
bool proportional(std::string_view raw) {
    raw = trim(raw);
    if (raw.empty()) return true;                 // omitted: the default, which is a %
    if (iequals(raw, "auto") || iequals(raw, "cover") || iequals(raw, "contain")) return true;
    if (iequals(raw, "left") || iequals(raw, "right") || iequals(raw, "top") ||
        iequals(raw, "bottom") || iequals(raw, "center")) {
        return true;
    }
    return raw.back() == '%';
}

// Whether the gradient's normalised form survives a change of ASPECT RATIO, not
// merely of scale.
//
// This is the part that is easy to get wrong. A percentage-only radial ellipse
// is fully normalised -- rx = 0.8w and cx = 0.5w give ex = ((u - 0.5) / 0.8)
// with no w left in it -- so it is the same picture on a 1280-wide box and a
// 1269-wide one. A CIRCLE is not: one radius covers both axes, so w/r appears
// on one and h/r on the other and the two move apart as the box stretches.
// Neither is a linear gradient at any angle off the axes, whose iso-lines are
// only at that angle when the box is square, nor a conic, whose angles distort
// the same way.
bool aspect_invariant(const Gradient& g) {
    switch (g.kind) {
        case Gradient::Kind::Linear: {
            // A corner keyword IS an aspect-dependent angle -- that is the
            // whole point of the magic corners (Images L3 3.1.1).
            if (g.corner != 0) return false;
            const double a = std::fmod(std::fmod(g.angle_deg, 90.0) + 90.0, 90.0);
            return a < 1e-9;
        }
        case Gradient::Kind::Radial:
            return !g.circle;
        case Gradient::Kind::Conic:
            return false;
    }
    return false;
}

}   // namespace

int background_texture_detail(const std::vector<BackgroundLayer>& layers) {
    for (const BackgroundLayer& l : layers) {
        // An image carries whatever detail it carries.
        if (!l.is_gradient || l.image || !l.url.empty()) return 0;
        // A tiled layer repeats its picture across the box, so the texture
        // needs the resolution of the whole strip, not of one tile.
        if (l.repeat_x || l.repeat_y) {
            if (!iequals(trim(l.size_x), "auto") || !iequals(trim(l.size_y), "auto")) return 0;
        }
        const Gradient& g = l.gradient;
        if (g.repeating) return 0;
        // A conic gradient always closes on itself, and any two stops sharing a
        // position are a hard band boundary. Both are discontinuities, and a
        // discontinuity is exactly the thing a coarse texture turns to mush.
        if (g.kind == Gradient::Kind::Conic) return 0;
        for (size_t i = 1; i < g.stops.size(); ++i) {
            if (!g.stops[i].has_position || !g.stops[i - 1].has_position) continue;
            if (g.stops[i].is_px != g.stops[i - 1].is_px) continue;
            if (std::fabs(g.stops[i].position - g.stops[i - 1].position) < 1e-6) return 0;
        }
    }
    return layers.empty() ? 0 : kSmoothGradientTexels;
}

bool background_size_independent(const std::vector<BackgroundLayer>& layers) {
    for (const BackgroundLayer& l : layers) {
        if (l.attachment_fixed || l.attachment_local) return false;
        // A decoded image has intrinsic pixel dimensions, so how much of the
        // box it covers is a matter of absolute size.
        if (!l.is_gradient || l.image || !l.url.empty()) return false;
        if (!proportional(l.pos_x) || !proportional(l.pos_y)) return false;
        if (!proportional(l.size_x) || !proportional(l.size_y)) return false;
        const Gradient& g = l.gradient;
        if (!aspect_invariant(g)) return false;
        if (!proportional(g.pos_x_raw) || !proportional(g.pos_y_raw)) return false;
        if (g.sizing == Gradient::Sizing::Explicit &&
            (!proportional(g.radius_x_raw) || !proportional(g.radius_y_raw))) {
            return false;
        }
        // A stop at `300px` sits a different fraction along the gradient line
        // on every box width.
        for (const GradientStop& st : g.stops) {
            if (st.is_px) return false;
        }
    }
    return true;
}

void sample_gradient(const Gradient& g, double x, double y, double width, double height,
                     const LayoutContext& ctx, double font_size, float out_srgb[4]) {
    const PreparedGradient p = prepare(g, width, height, ctx, font_size);
    const Srgb c = sample_prepared(p, x, y);
    out_srgb[0] = c.r;
    out_srgb[1] = c.g;
    out_srgb[2] = c.b;
    out_srgb[3] = c.a;
}

namespace {

// CSS Compositing 1 §10.1 separable blend modes, one channel, sRGB values
// 0..1 as the layers are composited. The non-separable four fall back to
// normal here; they need the whole colour and are rare on a background.
float blend_channel(BlendMode mode, float cb, float cs) {
    const auto hard_light = [](float b, float s) {
        return s <= 0.5f ? b * 2 * s : (b + (2 * s - 1) - b * (2 * s - 1));
    };
    switch (mode) {
        case BlendMode::Multiply: return cb * cs;
        case BlendMode::Screen: return cb + cs - cb * cs;
        case BlendMode::Overlay: return hard_light(cs, cb);
        case BlendMode::Darken: return std::min(cb, cs);
        case BlendMode::Lighten: return std::max(cb, cs);
        case BlendMode::ColorDodge: return cb == 0 ? 0.0f : cs >= 1 ? 1.0f : std::min(1.0f, cb / (1 - cs));
        case BlendMode::ColorBurn: return cb >= 1 ? 1.0f : cs <= 0 ? 0.0f : 1 - std::min(1.0f, (1 - cb) / cs);
        case BlendMode::HardLight: return hard_light(cb, cs);
        case BlendMode::SoftLight: {
            if (cs <= 0.5f) return cb - (1 - 2 * cs) * cb * (1 - cb);
            const float d = cb <= 0.25f ? ((16 * cb - 12) * cb + 4) * cb : std::sqrt(cb);
            return cb + (2 * cs - 1) * (d - cb);
        }
        case BlendMode::Difference: return std::fabs(cb - cs);
        case BlendMode::Exclusion: return cb + cs - 2 * cb * cs;
        default: return cs;
    }
}

// CSS Masking 1 §6.4 mask-mode: the coverage a mask sample contributes --
// its alpha, or its luminance (of the linear-light colour) times its alpha.
float mask_coverage(const Srgb& s, bool luminance) {
    if (!luminance) return s.a;
    const auto lin = [](float c) {
        return c <= 0.04045f ? c / 12.92f : std::pow((c + 0.055f) / 1.055f, 2.4f);
    };
    const float l = 0.2125f * lin(s.r) + 0.7154f * lin(s.g) + 0.0721f * lin(s.b);
    return std::min(1.0f, std::max(0.0f, l)) * s.a;
}

} // namespace

// One texel of a background image, nearest.
//
// Nearest and not bilinear, deliberately and to match the rest of the engine:
// the software renderer samples its textures nearest and the Godot host sets
// TEXTURE_FILTER_NEAREST for the glyph atlas, so smoothing here would be the
// only smooth sampling in the pipeline -- and would put the two backends on
// different footing the moment one of them changed. `image-rendering` is where
// that choice belongs, and it is not implemented yet either.
Srgb sample_image(const DecodedImage& image, double lx, double ly, double tw, double th) {
    const int ix = std::clamp(static_cast<int>(lx / tw * image.width), 0, image.width - 1);
    const int iy = std::clamp(static_cast<int>(ly / th * image.height), 0, image.height - 1);
    const uint8_t* p = image.rgba.data() + (static_cast<size_t>(iy) * image.width + ix) * 4;
    Srgb s;
    s.r = p[0] / 255.0f;
    s.g = p[1] / 255.0f;
    s.b = p[2] / 255.0f;
    s.a = p[3] / 255.0f;
    return s;
}

namespace {

struct Tile {
    PreparedGradient prepared;
    // Set instead of `prepared` for a url layer. Unlike a gradient an
    // image has an INTRINSIC size, which is what `auto`, `cover` and
    // `contain` are all measured against.
    const DecodedImage* image = nullptr;
    double ox, oy, tw, th;
    bool repeat_x, repeat_y;
    // Whether the tile's repetition can actually be reached inside the
    // painting area. `background-repeat` is `repeat` unless a sheet says
    // otherwise, so nearly every layer sets repeat_x and repeat_y -- and
    // nearly every layer is also a gradient sized to the area it fills,
    // which puts every sample inside the first tile. The wrap is then two
    // fmods that cannot change their argument, run for every texel of
    // every layer: eight of them per texel of a two-layer background.
    bool wrap_x, wrap_y;
    BlendMode blend = BlendMode::Normal;
    bool is_mask = false, luminance = false;
};
struct EdgeTest {
    double half_width = 0;
    double span = 0;        // repeating period, 0 when not repeating
    std::vector<double> positions;
};
// Everything rasterize_background resolves from CSS text, resolved: the
// tiles with their prepared gradients, and which shortcuts apply. See
// prepare_background in background.h.
struct PreparedBackground final : BackgroundPlan {
    // Owned, because each tile's prepared gradient points into its layer.
    std::vector<BackgroundLayer> layers;
    std::vector<Tile> tiles;
    std::vector<EdgeTest> edges;
    Srgb base{};
    bool any_mask = false;
    int samples = 1;
    bool flat_x = false, flat_y = false;
    int period_x = 0, period_y = 0;
    double width = 0, height = 0;
    int tex_w = 1, tex_h = 1;
};

} // namespace

std::shared_ptr<const BackgroundPlan> prepare_background(
    const std::vector<BackgroundLayer>& layers, const LinearColor& color, double width,
    double height, int tex_w, int tex_h, const LayoutContext& ctx, double font_size) {
    auto plan = std::make_shared<PreparedBackground>();
    tex_w = std::max(1, tex_w);
    tex_h = std::max(1, tex_h);
    plan->layers = layers;
    plan->base = to_srgb(color);
    plan->width = width;
    plan->height = height;
    plan->tex_w = tex_w;
    plan->tex_h = tex_h;

    // Each layer's tile and origin; a gradient has no intrinsic size, so
    // `auto`, `cover` and `contain` all mean the painting area (§3.9).
    std::vector<Tile>& tiles = plan->tiles;
    bool& any_mask = plan->any_mask;
    const double outer_width = width, outer_height = height;
    for (const BackgroundLayer& l : plan->layers) {
        if (!l.is_gradient && !l.image) continue;
        Tile t;
        t.image = l.is_gradient ? nullptr : l.image;
        t.blend = l.blend;
        t.is_mask = l.is_mask;
        t.luminance = l.mask_luminance;
        if (l.is_mask) any_mask = true;
        // A fixed layer measures against the viewport rather than the box.
        const double width = l.area_w > 0 ? l.area_w : outer_width;
        const double height = l.area_h > 0 ? l.area_h : outer_height;
        t.tw = width;
        t.th = height;
        const bool cover = iequals(l.size_x, "cover");
        const bool contain = iequals(l.size_x, "contain");
        const bool auto_x = iequals(l.size_x, "auto") || cover || contain;
        const bool auto_y = iequals(l.size_y, "auto");
        if (t.image) {
            // CSS Backgrounds L3 s3.9, the half a gradient never reaches: an
            // image has a size of its own, so `auto` is that size, `cover` and
            // `contain` scale it to the area keeping its aspect ratio, and one
            // explicit axis sets the other from the same ratio.
            const double iw = std::max(1, t.image->width);
            const double ih = std::max(1, t.image->height);
            if (cover || contain) {
                const double sx_scale = width / iw, sy_scale = height / ih;
                const double scale = cover ? std::max(sx_scale, sy_scale)
                                           : std::min(sx_scale, sy_scale);
                t.tw = iw * scale;
                t.th = ih * scale;
            } else if (auto_x && auto_y) {
                t.tw = iw;
                t.th = ih;
            } else if (auto_x) {
                t.th = std::max(1e-6, resolve_size_component(l.size_y, height, ctx, font_size));
                t.tw = t.th * (iw / ih);
            } else if (auto_y) {
                t.tw = std::max(1e-6, resolve_size_component(l.size_x, width, ctx, font_size));
                t.th = t.tw * (ih / iw);
            } else {
                t.tw = std::max(1e-6, resolve_size_component(l.size_x, width, ctx, font_size));
                t.th = std::max(1e-6, resolve_size_component(l.size_y, height, ctx, font_size));
            }
        } else {
        if (!auto_x) t.tw = std::max(1e-6, resolve_size_component(l.size_x, width, ctx, font_size));
        if (!auto_y) t.th = std::max(1e-6, resolve_size_component(l.size_y, height, ctx, font_size));
        }
        t.ox = resolve_position(l.pos_x, width, t.tw, true, ctx, font_size) + l.shift_x;
        t.oy = resolve_position(l.pos_y, height, t.th, false, ctx, font_size) + l.shift_y;
        t.repeat_x = l.repeat_x;
        t.repeat_y = l.repeat_y;
        // Covered on an axis means every sample lands in the first tile, so
        // wrapping is the identity and the bounds test below always passes.
        // (Against the box's own area: that is what the samples span.)
        t.wrap_x = l.repeat_x && !(t.ox <= 0 && t.ox + t.tw >= outer_width);
        t.wrap_y = l.repeat_y && !(t.oy <= 0 && t.oy + t.th >= outer_height);
        if (t.image && std::getenv("WEVA_IMAGE_LOG")) {
            std::fprintf(stderr,
                         "  [img] area %.1fx%.1f  intrinsic %dx%d  tile %.1fx%.1f  at %.1f,%.1f"
                         "  size_x '%s' size_y '%s'  pos '%s','%s'\n",
                         width, height, t.image->width, t.image->height, t.tw, t.th, t.ox, t.oy,
                         std::string(l.size_x).c_str(), std::string(l.size_y).c_str(),
                         std::string(l.pos_x).c_str(), std::string(l.pos_y).c_str());
        }
        if (!t.image) t.prepared = prepare(l.gradient, t.tw, t.th, ctx, font_size);
        tiles.push_back(std::move(t));
    }

    // One sample per texel is enough for a gradient that only ever ramps: the
    // ramp IS the antialiasing. It is not enough where a gradient has an edge
    // -- a conic one always does, since its start and end meet, and any stop
    // that repeats a position is a hard band boundary. Those come out as a
    // staircase, which on a conic progress ring is the step across the arc.
    //
    // So the sample count is chosen from the content rather than turned up
    // everywhere: this runs on every update, and a viewport-sized gradient is
    // already the most expensive thing here.
    int& samples = plan->samples;
    for (const Tile& t : tiles) {
        if (t.prepared.has_hard_edge) samples = 3;
    }

    // A `linear-gradient(180deg, ...)` has the same colour all the way across a
    // row, so all but one texel of that row is a copy. Rasterizing it anyway is
    // the whole cost of opening a page: a viewport-sized gradient with a hard
    // edge is 1024x1024x9 samples, and episode-stats spent 581 ms of its first
    // update in here. 180deg and 90deg are 105 of the 186 linear gradients in
    // the sample corpus.
    //
    // A tile is constant along an axis when its gradient does not vary along
    // it AND it covers the whole area on it -- an edge where the tile stops
    // varies whatever the gradient does. The colour underneath is flat, so the
    // composite is constant exactly when every tile is.
    const auto constant_along = [&](bool horizontal) {
        for (const Tile& t : tiles) {
            const PreparedGradient& g = t.prepared;
            if (!g.g || g.g->kind != Gradient::Kind::Linear) return false;
            if (std::abs(horizontal ? g.dx : g.dy) > 1e-9) return false;
            const bool repeats = horizontal ? t.repeat_x : t.repeat_y;
            if (repeats) continue;
            const double o = horizontal ? t.ox : t.oy;
            const double span = horizontal ? t.tw : t.th;
            const double extent = horizontal ? width : height;
            if (o > 0 || o + span < extent) return false;
        }
        return !tiles.empty();
    };
    const bool flat_x = plan->flat_x = constant_along(true);
    plan->flat_y = !flat_x && constant_along(false);

    // How many texels before the picture REPEATS, on each axis, or 0 for never.
    //
    // flat_x and flat_y above are this with a period of one: a `180deg` ramp
    // has the same colour all the way across a row, so the row is one texel and
    // 1023 copies. A tiled background is the same idea one step out -- map's
    // page background is a pair of 80px grid-line gradients across the whole
    // viewport, which is 64 texels of picture and 737,216 texels of copying,
    // and rasterizing all of it cost 59 ms of a 67 ms first paint.
    //
    // Every tile has to repeat on the axis for the composite to. A tile that
    // covers the area rather than repeating across it (`wrap_x` false) is one
    // gradient stretched over the whole width and has no period at all, so one
    // of those is enough to rule the axis out.
    //
    // The period is taken in TEXELS and must land on a whole number of them: a
    // tile 80px wide on a texel grid of 1.25px steps is 64 texels exactly, and
    // if it were not, copying would shift the pattern by a fraction of a texel
    // every period and the seams would show.
    const auto period_along = [&](bool horizontal) -> int {
        if (tiles.empty()) return 0;
        const int tex_extent = horizontal ? tex_w : tex_h;
        const double step = horizontal ? width / tex_w : height / tex_h;
        long lcm = 1;
        for (const Tile& t : tiles) {
            // Two different things repeat, and both count.
            //
            // A tiled background repeats because `background-size` is smaller
            // than the box, and the period is the tile. A
            // repeating-linear-gradient repeats inside a tile that covers the
            // whole box, and the period is its stop span projected onto this
            // axis -- map's grid is the second kind, so a test that only knew
            // about the first found nothing.
            double period_px = 0;
            if (horizontal ? t.wrap_x : t.wrap_y) {
                period_px = horizontal ? t.tw : t.th;
            } else {
                // Not tiled on this axis: it must be a covering tile, or its
                // own edge is a feature and there is no period.
                const double o = horizontal ? t.ox : t.oy;
                const double extent = horizontal ? width : height;
                const double own = horizontal ? t.tw : t.th;
                if (o > 0 || o + own < extent) return 0;
                const PreparedGradient& g = t.prepared;
                // A radial or conic gradient has no period along an axis, and
                // an image that is not tiled is just an image.
                if (t.image || !g.g || g.g->kind != Gradient::Kind::Linear) return 0;
                const double d = std::fabs(horizontal ? g.dx : g.dy);
                if (d < 1e-9) {
                    // The gradient does not vary along this axis at all, so one
                    // texel is the whole story -- which is what flat_x and
                    // flat_y say when EVERY tile is like this.
                    period_px = step;
                } else {
                    if (!g.g->repeating || g.stops.size() < 2) return 0;
                    const double stop_span = g.stops.back().position - g.stops.front().position;
                    if (!(stop_span > 0)) return 0;
                    // t advances by d / line_length per pixel, and repeats every
                    // stop_span of t.
                    period_px = stop_span * g.line_length / d;
                }
            }
            if (!(period_px > 0)) return 0;
            const double in_texels = period_px / step;
            const long rounded = std::lround(in_texels);
            if (rounded < 1 || std::fabs(in_texels - rounded) > 1e-9) return 0;
            // The least common multiple, spelled out to keep the overflow check
            // in view: two coprime periods multiply, and a pair of odd tile
            // sizes would take this past the texture in a hurry.
            long a = lcm, b = rounded;
            while (b) { const long r = a % b; a = b; b = r; }
            const long g = a > 0 ? a : 1;
            if (lcm / g > tex_extent) return 0;
            lcm = lcm / g * rounded;
            if (lcm >= tex_extent) return 0;
        }
        return static_cast<int>(lcm);
    };
    // Only where the cheaper flat path does not already cover the axis.
    plan->period_x = flat_x ? 0 : period_along(true);
    plan->period_y = plan->flat_y ? 0 : period_along(false);

    // A texel only needs more than one sample where the gradient actually
    // steps. Everywhere else the ramp is linear across the texel, so the nine
    // samples average to the one at the centre and the extra eight are waste
    // -- and it is nearly all of it: episode-stats' page background is a
    // 1024x720 texture of three layers, 20 million samples, 600 ms of its first
    // update, of which the banded `repeating-linear-gradient(118deg, ...)`
    // steps on a few percent.
    //
    // A linear gradient's parameter is affine, so the half-width of a texel's
    // t-range is the same everywhere and comes out of prepare(). Radial and
    // conic have no such constant -- their gradient of t blows up at the centre
    // -- so they keep sampling as before.
    std::vector<EdgeTest>& edges = plan->edges;
    bool adaptive = samples > 1;
    for (const Tile& t : tiles) {
        const PreparedGradient& g = t.prepared;
        if (!g.g || g.g->kind != Gradient::Kind::Linear || g.stops.size() < 2) {
            adaptive = false;
            break;
        }
        // A tile that stops short of the area has a hard edge at its own
        // boundary, which this test does not model.
        if (!t.repeat_x && (t.ox > 0 || t.ox + t.tw < width)) { adaptive = false; break; }
        if (!t.repeat_y && (t.oy > 0 || t.oy + t.th < height)) { adaptive = false; break; }
        EdgeTest e;
        e.half_width = 0.5 * (std::abs(g.dx) * (width / tex_w) + std::abs(g.dy) * (height / tex_h)) /
                       g.line_length;
        const double first = g.stops.front().position, last = g.stops.back().position;
        if (g.g->repeating && last > first) e.span = last - first;
        for (const GradientStop& st : g.stops) e.positions.push_back(st.position);
        edges.push_back(std::move(e));
    }
    if (!adaptive) edges.clear();

    return plan;
}

void rasterize_background(const BackgroundPlan& prepared, std::vector<uint8_t>* out_rgba) {
    const auto& plan = static_cast<const PreparedBackground&>(prepared);
    static const bool gradient_log = std::getenv("WEVA_GRADIENT_LOG") != nullptr;
    const auto raster_start = gradient_log ? std::chrono::steady_clock::now()
                                           : std::chrono::steady_clock::time_point{};
    const std::vector<Tile>& tiles = plan.tiles;
    const std::vector<EdgeTest>& edges = plan.edges;
    const Srgb base = plan.base;
    const bool any_mask = plan.any_mask;
    const int samples = plan.samples;
    const bool flat_x = plan.flat_x, flat_y = plan.flat_y;
    const int period_x = plan.period_x, period_y = plan.period_y;
    const double width = plan.width, height = plan.height;
    const int tex_w = plan.tex_w, tex_h = plan.tex_h;
    out_rgba->assign(static_cast<size_t>(tex_w) * tex_h * 4, 0);

    // True when the texel's t-range reaches a stop, so the value across it is
    // not one straight ramp.
    const auto steps_here = [&](size_t i, double t) {
        const EdgeTest& e = edges[i];
        const double lo = t - e.half_width, hi = t + e.half_width;
        if (e.span > 0) {
            if (2 * e.half_width >= e.span) return true;
            const double base = e.positions.front();
            const double w = base + wrap_positive(t - base, e.span);
            for (const double pos : e.positions) {
                for (int k = -1; k <= 1; ++k) {
                    const double q = pos + k * e.span;
                    if (q >= w - e.half_width && q <= w + e.half_width) return true;
                }
            }
            return false;
        }
        // Outside the first and last stop the colour is flat, but the knee at
        // each end is itself a corner in the ramp.
        for (const double pos : e.positions) {
            if (pos >= lo && pos <= hi) return true;
        }
        return false;
    };

    // How much of the adaptive path actually pays off, which the sample count
    // alone does not say: `3^2 samples` is the ceiling, not the bill.
    long supersampled = 0;
    if (gradient_log) {
        std::fprintf(stderr, "  [grad] %dx%d tex, %zu tiles, %d^2 samples, flat_x %d flat_y %d\n",
                     tex_w, tex_h, tiles.size(), samples, flat_x ? 1 : 0, flat_y ? 1 : 0);
    }
    const double sx = width / tex_w, sy = height / tex_h;
    // The rows that are rasterized; the rest copy one of them. Each is
    // independent of every other, so they split across threads (parallel.h)
    // with byte-identical results, and the copies follow on this thread.
    const int computed_rows = flat_y ? std::min(1, tex_h)
                            : period_y > 0 ? std::min(period_y, tex_h) : tex_h;
    std::atomic<long> supersampled_total{0};
    const auto raster_rows = [&](int row_begin, int row_end) {
        long local_supersampled = 0;
        for (int py = row_begin; py < row_end; ++py) {
            for (int px = 0; px < tex_w; ++px) {
                if (flat_x && px > 0) {
                    std::memcpy(out_rgba->data() + (static_cast<size_t>(py) * tex_w + px) * 4,
                                out_rgba->data() + static_cast<size_t>(py) * tex_w * 4, 4);
                    continue;
                }
                if (period_x > 0 && px >= period_x) {
                    // The rest of the row is this row's first period, repeated. One
                    // memcpy of the whole tail would be wrong: the source overlaps
                    // the destination whenever the tail is longer than the period.
                    const size_t row = static_cast<size_t>(py) * tex_w * 4;
                    const int run = std::min(period_x, tex_w - px);
                    std::memcpy(out_rgba->data() + row + static_cast<size_t>(px) * 4,
                                out_rgba->data() + row + static_cast<size_t>(px - period_x) * 4,
                                static_cast<size_t>(run) * 4);
                    px += run - 1;
                    continue;
                }
                float ar = 0, ag = 0, ab = 0, aa = 0;
                int texel_samples = samples;
                if (!edges.empty()) {
                    const double cx = (px + 0.5) * sx, cy = (py + 0.5) * sy;
                    texel_samples = 1;
                    for (size_t i = 0; i < edges.size(); ++i) {
                        if (steps_here(i, gradient_t(tiles[i].prepared, cx - tiles[i].ox,
                                                     cy - tiles[i].oy))) {
                            texel_samples = samples;
                            break;
                        }
                    }
                }
                if (gradient_log && texel_samples > 1) ++local_supersampled;
                const double tinv = 1.0 / texel_samples;
                for (int oy = 0; oy < texel_samples; ++oy) {
                    const double y = (py + (oy + 0.5) * tinv) * sy;
                    for (int ox = 0; ox < texel_samples; ++ox) {
                        const double x = (px + (ox + 0.5) * tinv) * sx;
                        // Premultiplied source-over, bottom layer (the last) first.
                        float r = base.r * base.a, g = base.g * base.a, b = base.b * base.a;
                        float a = base.a;
                        // A mask's coverage; the layers add (1 - the product of what
                        // each leaves uncovered).
                        float uncovered = 1;
                        for (size_t i = tiles.size(); i-- > 0;) {
                            const Tile& t = tiles[i];
                            double lx = x - t.ox, ly = y - t.oy;
                            if (t.wrap_x) lx = wrap_positive(lx, t.tw);
                            else if (!t.repeat_x && (lx < 0 || lx >= t.tw)) continue;
                            if (t.wrap_y) ly = wrap_positive(ly, t.th);
                            else if (!t.repeat_y && (ly < 0 || ly >= t.th)) continue;
                            const Srgb s = t.image ? sample_image(*t.image, lx, ly, t.tw, t.th)
                                                   : sample_prepared(t.prepared, lx, ly);
                            const float sa = s.a;
                            if (t.is_mask) {
                                uncovered *= 1 - mask_coverage(s, t.luminance);
                                continue;
                            }
                            if (t.blend == BlendMode::Normal) {
                                r = s.r * sa + r * (1 - sa);
                                g = s.g * sa + g * (1 - sa);
                                b = s.b * sa + b * (1 - sa);
                            } else {
                                // CSS Compositing 1 §5.1: the source blended with
                                // the backdrop where there is one, then source-over.
                                const float cb_r = a > 0 ? r / a : 0, cb_g = a > 0 ? g / a : 0, cb_b = a > 0 ? b / a : 0;
                                const float co_r = (1 - a) * s.r + a * blend_channel(t.blend, cb_r, s.r);
                                const float co_g = (1 - a) * s.g + a * blend_channel(t.blend, cb_g, s.g);
                                const float co_b = (1 - a) * s.b + a * blend_channel(t.blend, cb_b, s.b);
                                r = co_r * sa + r * (1 - sa);
                                g = co_g * sa + g * (1 - sa);
                                b = co_b * sa + b * (1 - sa);
                            }
                            a = sa + a * (1 - sa);
                        }
                        if (any_mask) {
                            const float m = 1 - uncovered;
                            r *= m; g *= m; b *= m; a *= m;
                        }
                        // Accumulated PREMULTIPLIED, or a sample that is barely
                        // covered drags the colour of a fully covered neighbour
                        // towards whatever its own undefined colour happens to be.
                        ar += r;
                        ag += g;
                        ab += b;
                        aa += a;
                    }
                }
                const float n = static_cast<float>(texel_samples * texel_samples);
                float r = ar / n, g = ag / n, b = ab / n;
                const float a = aa / n;
                uint8_t* o = out_rgba->data() + (static_cast<size_t>(py) * tex_w + px) * 4;
                if (a > 0) { r /= a; g /= a; b /= a; }
                // std::lround is a libm CALL, and this runs four times for every
                // texel of every background: 4% of a viewport-sized one. The value
                // is clamped to [0, 255] first, and for a non-negative float
                // lround is floor(v + 0.5), which is what the cast does.
                const auto byte = [](float v) {
                    // The product is taken first and kept, so the compiler cannot
                    // fuse it with the +0.5 into an FMA and round the pair at
                    // higher precision than lround did -- which moved eighteen of
                    // hud's texels by one when it could.
                    // As in mix(), keep this a value rather than a selected reference.
                    const float scaled = (v < 0.0f ? 0.0f : v > 1.0f ? 1.0f : v) * 255.0f;
                    return static_cast<uint8_t>(scaled + 0.5f);
                };
                o[0] = byte(r);
                o[1] = byte(g);
                o[2] = byte(b);
                o[3] = byte(a);
            }
        }
        if (local_supersampled) supersampled_total += local_supersampled;
    };
    const long long samples_per_texel = static_cast<long long>(samples) * samples;
    const long long work = static_cast<long long>(computed_rows) * tex_w * samples_per_texel *
                           static_cast<long long>(std::max<size_t>(1, tiles.size()));
    // About a millisecond of sampling; below it, starting threads costs more.
    constexpr long long kParallelSamples = 60000;
    parallel_ranges(computed_rows, 1, work, kParallelSamples, raster_rows);
    supersampled = supersampled_total.load();
    for (int py = computed_rows; py < tex_h; ++py) {
        if (flat_y) {
            // Every row is the row above it.
            std::memcpy(out_rgba->data() + static_cast<size_t>(py) * tex_w * 4, out_rgba->data(),
                        static_cast<size_t>(tex_w) * 4);
            continue;
        }
        // A whole row, already drawn one period up.
        std::memcpy(out_rgba->data() + static_cast<size_t>(py) * tex_w * 4,
                    out_rgba->data() + static_cast<size_t>(py - period_y) * tex_w * 4,
                    static_cast<size_t>(tex_w) * 4);
    }
    if (gradient_log && (period_x > 0 || period_y > 0)) {
        std::fprintf(stderr, "  [grad]   period %d x %d of %d x %d texels\n",
                     period_x ? period_x : tex_w, period_y ? period_y : tex_h, tex_w, tex_h);
    }
    if (gradient_log && samples > 1) {
        const long texels = static_cast<long>(tex_w) * tex_h;
        std::fprintf(stderr, "  [grad]   adaptive: %ld of %ld texels supersampled (%.1f%%)\n",
                     supersampled, texels, texels ? 100.0 * supersampled / texels : 0.0);
    }
    if (gradient_log) {
        const double elapsed = std::chrono::duration<double, std::milli>(
            std::chrono::steady_clock::now() - raster_start).count();
        std::fprintf(stderr, "  [grad]   raster %.3f ms\n", elapsed);
    }
}

long long raster_cost(const BackgroundPlan& prepared) {
    const auto& plan = static_cast<const PreparedBackground&>(prepared);
    // The rows and texels actually sampled, as rasterize_background decides
    // them; the rest are copies. A hard edge is charged every texel's full
    // supersampling, which it can reach: menu's progress bar is 100%.
    const long long rows = plan.flat_y ? 1 : plan.period_y > 0 ? std::min(plan.period_y, plan.tex_h) : plan.tex_h;
    const long long cols = plan.flat_x ? 1 : plan.period_x > 0 ? std::min(plan.period_x, plan.tex_w) : plan.tex_w;
    const long long sampled = rows * cols;
    const long long samples = static_cast<long long>(plan.samples) * plan.samples;
    const long long texels = static_cast<long long>(plan.tex_w) * plan.tex_h;
    return sampled * (8 + samples * static_cast<long long>(plan.tiles.size()) * 30) + texels;
}

void rasterize_background(const std::vector<BackgroundLayer>& layers, const LinearColor& color,
                          double width, double height, int tex_w, int tex_h,
                          const LayoutContext& ctx, double font_size,
                          std::vector<uint8_t>* out_rgba) {
    rasterize_background(*prepare_background(layers, color, width, height, tex_w, tex_h, ctx,
                                             font_size),
                         out_rgba);
}

} // namespace weva

namespace weva {

namespace {

// Coverage of a rounded rectangle (origin 0,0, size w x h) at a texel centre.
double rounded_coverage(double px, double py, double w, double h, const BorderRadii* radii) {
    if (px < 0 || py < 0 || px > w || py > h) return 0;
    if (!radii) return 1;
    const auto corner = [&](const CornerRadius& r, double cx, double cy, double dx, double dy) {
        // Inside the corner's ellipse region only when within its quarter.
        if (r.x_radius <= 0 || r.y_radius <= 0) return 1.0;
        if (dx < 0 || dy < 0) return 1.0;
        const double ex = dx / r.x_radius, ey = dy / r.y_radius;
        (void)cx; (void)cy;
        // How much of the PIXEL the corner covers, not whether its centre is
        // inside. This returned 1 or 0, which is why every gradient-filled
        // rounded box came out with a staircase edge — the alpha mask baked
        // into the layer had no intermediate values at all.
        //
        // s is 1 exactly on the ellipse, so (s - 1) divided by the length of
        // its gradient is the distance to the edge in pixels. Coverage is half
        // a pixel either side of that, which is exact for a circle and within
        // a percent for the eccentricities a border-radius produces.
        const double s = std::sqrt(ex * ex + ey * ey);
        if (s <= 0) return 1.0;
        const double gx = ex / (s * r.x_radius), gy = ey / (s * r.y_radius);
        const double grad = std::sqrt(gx * gx + gy * gy);
        if (grad <= 0) return 1.0;
        return std::clamp(0.5 - (s - 1.0) / grad, 0.0, 1.0);
    };
    double c = 1;
    c = std::min(c, corner(radii->top_left, radii->top_left.x_radius, radii->top_left.y_radius,
                           radii->top_left.x_radius - px, radii->top_left.y_radius - py));
    c = std::min(c, corner(radii->top_right, w - radii->top_right.x_radius, radii->top_right.y_radius,
                           px - (w - radii->top_right.x_radius), radii->top_right.y_radius - py));
    c = std::min(c, corner(radii->bottom_right, w - radii->bottom_right.x_radius,
                           h - radii->bottom_right.y_radius, px - (w - radii->bottom_right.x_radius),
                           py - (h - radii->bottom_right.y_radius)));
    c = std::min(c, corner(radii->bottom_left, radii->bottom_left.x_radius,
                           h - radii->bottom_left.y_radius, radii->bottom_left.x_radius - px,
                           py - (h - radii->bottom_left.y_radius)));
    return c;
}

} // namespace

double rounded_rect_coverage(double px, double py, double w, double h,
                             const BorderRadii* radii) {
    return rounded_coverage(px, py, w, h, radii);
}

std::shared_ptr<const BackgroundPlan> prepare_background_padded(
    const std::vector<BackgroundLayer>& layers, const LinearColor& color, double width,
    double height, int tex_w, int tex_h, int pad, const LayoutContext& ctx, double font_size) {
    const int inner_w = std::max(1, tex_w - 2 * pad), inner_h = std::max(1, tex_h - 2 * pad);
    return prepare_background(layers, color, width, height, inner_w, inner_h, ctx, font_size);
}

void rasterize_background_padded(const BackgroundPlan& prepared, int tex_w, int tex_h, int pad,
                                 const BorderRadii* radii, std::vector<uint8_t>* out_rgba) {
    const auto& plan = static_cast<const PreparedBackground&>(prepared);
    const double width = plan.width, height = plan.height;
    const int inner_w = plan.tex_w, inner_h = plan.tex_h;
    std::vector<uint8_t> inner;
    rasterize_background(prepared, &inner);
    out_rgba->assign(static_cast<size_t>(tex_w) * tex_h * 4, 0);
    if (!radii || radii->is_zero()) {
        // Without rounded corners every inner texel has full coverage.
        // Preserve the raster bytes directly, including transparent colours.
        for (int y = 0; y < inner_h; ++y) {
            std::memcpy(out_rgba->data() + (static_cast<size_t>(y + pad) * tex_w + pad) * 4,
                        inner.data() + static_cast<size_t>(y) * inner_w * 4,
                        static_cast<size_t>(inner_w) * 4);
        }
        return;
    }
    const double sx = width / inner_w, sy = height / inner_h;
    // Rows are independent; see parallel.h.
    parallel_ranges(inner_h, 1, static_cast<long long>(inner_w) * inner_h, 1 << 16,
                    [&](int y_begin, int y_end) {
        for (int y = y_begin; y < y_end; ++y) {
            for (int x = 0; x < inner_w; ++x) {
                const uint8_t* src = inner.data() + (static_cast<size_t>(y) * inner_w + x) * 4;
                uint8_t* dst = out_rgba->data() + (static_cast<size_t>(y + pad) * tex_w + (x + pad)) * 4;
                const double cov = rounded_coverage((x + 0.5) * sx, (y + 0.5) * sy, width, height, radii);
                dst[0] = src[0];
                dst[1] = src[1];
                dst[2] = src[2];
                dst[3] = static_cast<uint8_t>(std::lround(src[3] * cov));
            }
        }
    });
}

void rasterize_background_padded(const std::vector<BackgroundLayer>& layers,
                                 const LinearColor& color, double width, double height,
                                 int tex_w, int tex_h, int pad, const BorderRadii* radii,
                                 const LayoutContext& ctx, double font_size,
                                 std::vector<uint8_t>* out_rgba) {
    rasterize_background_padded(*prepare_background_padded(layers, color, width, height, tex_w,
                                                           tex_h, pad, ctx, font_size),
                                tex_w, tex_h, pad, radii, out_rgba);
}

// The blur, over one channel or four.
//
// `flat` says the colour is the same everywhere and only the coverage varies,
// which is true of every box-shadow and every text-shadow: the rasterizer was
// handed a single colour and drew nothing but its alpha. Blurring is linear and
// the buffer is premultiplied, so p = a * (r, g, b, 1) -- the three colour
// planes are the alpha plane times a constant, and blurring them reproduces
// that constant at four times the cost in both arithmetic and memory traffic.
//
// CH is a template parameter and not an argument, which is not a style choice:
// the first version passed the channel count as an int, and the one-channel path
// came out no faster than the four-channel one it replaced. A runtime trip count
// of 1 is a loop the compiler cannot unroll or vectorise, and it gave back
// everything the missing three channels saved.
template <int CH>
void blur_planes(float* p, float* tmp, int width, int height, int r,
                 double* horizontal_ms, double* vertical_ms) {
    const int taps = 2 * r + 1;
    // The reciprocal, not the divisor. Six passes over four channels is
    // TWENTY-FOUR divides per texel, and a divide is the one arithmetic
    // instruction a modern core cannot pipeline.
    const double inv_taps = 1.0 / taps;
    // A box blur is a sliding window: moving one pixel adds one sample and
    // drops one, so a pass costs the same whatever the radius. Re-summing the
    // whole window per pixel instead made every blur O(width * radius), and a
    // page of large glows paid for it -- neon spent 1.1 s of its first update
    // blurring text shadows, at a box width of ~2 sigma per pass, three passes
    // each way.
    //
    // Edges extend the outermost pixel, which is what the window did when it
    // clamped its index, so the divisor stays 2r+1 everywhere.
    // Rows (horizontal) and column strips (vertical) are independent, so each
    // pass splits across threads (parallel.h); a pass over fewer floats than
    // this stays on the calling thread.
    const long long pass_work = static_cast<long long>(width) * height * CH;
    constexpr long long kParallelFloats = 1 << 17;
    const auto pass_h = [&](const float* in, float* out) {
        if constexpr (CH == 4) {
            parallel_ranges(height, 1, pass_work, kParallelFloats, [&](int y_begin, int y_end) {
            for (int y = y_begin; y < y_end; ++y) {
                const float* row = in + static_cast<size_t>(y) * width * CH;
                float* orow = out + static_cast<size_t>(y) * width * CH;
#if WEVA_BLUR_SSE2
                // Preserve the scalar float differences and double running
                // sums. SSE2 groups independent channels, without reassociating
                // samples or using approximate reciprocals/conversions.
                __m128d low = _mm_setzero_pd(), high = _mm_setzero_pd();
                const __m128d scale = _mm_set1_pd(inv_taps);
                for (int k = -r; k <= r; ++k) {
                    const int xx = std::clamp(k, 0, width - 1);
                    const __m128 sample = _mm_loadu_ps(row + xx * CH);
                    low = _mm_add_pd(low, _mm_cvtps_pd(sample));
                    high = _mm_add_pd(high, _mm_cvtps_pd(_mm_movehl_ps(sample, sample)));
                }
                for (int x = 0; x < width; ++x) {
                    const __m128 lo = _mm_cvtpd_ps(_mm_mul_pd(low, scale));
                    const __m128 hi = _mm_cvtpd_ps(_mm_mul_pd(high, scale));
                    _mm_storeu_ps(orow + x * CH, _mm_movelh_ps(lo, hi));
                    const int add = std::clamp(x + r + 1, 0, width - 1);
                    const int drop = std::clamp(x - r, 0, width - 1);
                    const __m128 difference = _mm_sub_ps(_mm_loadu_ps(row + add * CH),
                                                        _mm_loadu_ps(row + drop * CH));
                    low = _mm_add_pd(low, _mm_cvtps_pd(difference));
                    high = _mm_add_pd(high, _mm_cvtps_pd(_mm_movehl_ps(difference, difference)));
                }
#else
                double acc[CH] = {};
                for (int k = -r; k <= r; ++k) {
                    const int xx = std::clamp(k, 0, width - 1);
                    for (int c = 0; c < CH; ++c) acc[c] += row[xx * CH + c];
                }
                for (int x = 0; x < width; ++x) {
                    for (int c = 0; c < CH; ++c) orow[x * CH + c] = static_cast<float>(acc[c] * inv_taps);
                    const int add = std::clamp(x + r + 1, 0, width - 1);
                    const int drop = std::clamp(x - r, 0, width - 1);
                    for (int c = 0; c < CH; ++c) acc[c] += row[add * CH + c] - row[drop * CH + c];
                }
#endif
            }
            });
            return;
        }
        // Independent row sums hide the running sum's dependency latency and
        // share clamped indices. A channel still accumulates in its original
        // left-to-right order, including the initial repeated edge samples.
        const size_t stride = static_cast<size_t>(width) * CH;
        const auto rows_h = [&](auto row_count, int y) {
            constexpr int kRows = decltype(row_count)::value;
            const float* base = in + static_cast<size_t>(y) * stride;
            float* obase = out + static_cast<size_t>(y) * stride;
            double acc[kRows][CH] = {};
            for (int k = -r; k <= r; ++k) {
                const int xx = std::clamp(k, 0, width - 1);
                for (int row_index = 0; row_index < kRows; ++row_index) {
                    const float* row = base + static_cast<size_t>(row_index) * stride;
                    for (int c = 0; c < CH; ++c) acc[row_index][c] += row[xx * CH + c];
                }
            }
            for (int x = 0; x < width; ++x) {
                const int add = std::clamp(x + r + 1, 0, width - 1);
                const int drop = std::clamp(x - r, 0, width - 1);
                for (int row_index = 0; row_index < kRows; ++row_index) {
                    const float* row = base + static_cast<size_t>(row_index) * stride;
                    float* orow = obase + static_cast<size_t>(row_index) * stride;
                    for (int c = 0; c < CH; ++c) {
                        orow[x * CH + c] = static_cast<float>(acc[row_index][c] * inv_taps);
                        acc[row_index][c] += row[add * CH + c] - row[drop * CH + c];
                    }
                }
            }
        };
        // Ranges start on multiples of four, so the groups are the serial ones.
        parallel_ranges(height, 4, pass_work, kParallelFloats, [&](int y_begin, int y_end) {
            int y = y_begin;
            for (; y + 4 <= y_end; y += 4) rows_h(std::integral_constant<int, 4>{}, y);
            for (; y < y_end; ++y) rows_h(std::integral_constant<int, 1>{}, y);
        });
    };
    // The vertical pass walks DOWN a row-major buffer, so consecutive reads are
    // a row apart and every one of them is a cache miss. Taken one column at a
    // time, each of those misses fetches a 64-byte line and uses CH floats of
    // it -- four bytes, in the one-channel case, of every sixty-four.
    //
    // Carry a strip of columns down together. Four-channel filter textures
    // are large enough that a single cache line per strip still revisits the
    // whole image hundreds of times. Wider strips stream contiguous reads
    // and writes; the one-channel shadow kernel retains its smaller block.
    // Neither strip width nor channel grouping changes accumulation order.
    const auto pass_v = [&](const float* in, float* out) {
        constexpr int kBlockFloats = CH == 4 ? 512 : 16;
        constexpr int kBlockCols = kBlockFloats / CH > 0 ? kBlockFloats / CH : 1;
        const size_t stride = static_cast<size_t>(width) * CH;
        const int strips = (width + kBlockCols - 1) / kBlockCols;
        parallel_ranges(strips, 1, pass_work, kParallelFloats, [&](int strip_begin, int strip_end) {
        for (int x0 = strip_begin * kBlockCols; x0 < std::min(width, strip_end * kBlockCols); x0 += kBlockCols) {
            const int cols = std::min(kBlockCols, width - x0);
            const int lanes = cols * CH;
            const float* base = in + static_cast<size_t>(x0) * CH;
            float* obase = out + static_cast<size_t>(x0) * CH;
            double acc[kBlockCols * CH] = {};
            for (int k = -r; k <= r; ++k) {
                const int yy = std::clamp(k, 0, height - 1);
                const float* row = base + static_cast<size_t>(yy) * stride;
                for (int i = 0; i < lanes; ++i) acc[i] += row[i];
            }
            for (int y = 0; y < height; ++y) {
                float* orow = obase + static_cast<size_t>(y) * stride;
                const int add = std::clamp(y + r + 1, 0, height - 1);
                const int drop = std::clamp(y - r, 0, height - 1);
                const float* arow = base + static_cast<size_t>(add) * stride;
                const float* drow = base + static_cast<size_t>(drop) * stride;
#if WEVA_BLUR_SSE2
                if constexpr (CH == 4) {
                    const __m128d scale = _mm_set1_pd(inv_taps);
                    // lanes is a multiple of four, even in the last strip.
                    // Keep each loaded sum for both its output and update.
                    for (int i = 0; i < lanes; i += 4) {
                        const __m128d low = _mm_loadu_pd(acc + i);
                        const __m128d high = _mm_loadu_pd(acc + i + 2);
                        const __m128 lo = _mm_cvtpd_ps(_mm_mul_pd(low, scale));
                        const __m128 hi = _mm_cvtpd_ps(_mm_mul_pd(high, scale));
                        _mm_storeu_ps(orow + i, _mm_movelh_ps(lo, hi));
                        const __m128 difference = _mm_sub_ps(_mm_loadu_ps(arow + i),
                                                            _mm_loadu_ps(drow + i));
                        _mm_storeu_pd(acc + i, _mm_add_pd(low, _mm_cvtps_pd(difference)));
                        _mm_storeu_pd(acc + i + 2,
                            _mm_add_pd(high, _mm_cvtps_pd(_mm_movehl_ps(difference, difference))));
                    }
                } else
#endif
                {
                    for (int i = 0; i < lanes; ++i) orow[i] = static_cast<float>(acc[i] * inv_taps);
                    for (int i = 0; i < lanes; ++i) acc[i] += arow[i] - drow[i];
                }
            }
        }
        });
    };
    for (int i = 0; i < 3; ++i) {
        const auto start = horizontal_ms ? std::chrono::steady_clock::now()
                                         : std::chrono::steady_clock::time_point{};
        pass_h(p, tmp);
        const auto middle = horizontal_ms ? std::chrono::steady_clock::now()
                                          : std::chrono::steady_clock::time_point{};
        pass_v(tmp, p);
        if (horizontal_ms) {
            *horizontal_ms += std::chrono::duration<double, std::milli>(middle - start).count();
            *vertical_ms += std::chrono::duration<double, std::milli>(
                std::chrono::steady_clock::now() - middle).count();
        }
    }
}

void blur_impl(std::vector<uint8_t>* rgba, int width, int height, double sigma, bool flat) {
    if (sigma <= 0.3 || width <= 0 || height <= 0) return;
    static const bool blur_log = std::getenv("WEVA_BLUR_LOG") != nullptr;
    const auto start = blur_log ? std::chrono::steady_clock::now()
                                : std::chrono::steady_clock::time_point{};
    const size_t n = static_cast<size_t>(width) * height;
    // One float per texel when only the coverage moves, four when the colour
    // does.
    const int ch = flat ? 1 : 4;
    // The colour to write back. Taken from the first covered texel, since by
    // assumption every covered texel carries it.
    uint8_t fr = 0, fg = 0, fb = 0;
    if (flat) {
        for (size_t i = 0; i < n; ++i) {
            if ((*rgba)[i * 4 + 3] == 0) continue;
            fr = (*rgba)[i * 4 + 0];
            fg = (*rgba)[i * 4 + 1];
            fb = (*rgba)[i * 4 + 2];
            break;
        }
    }
    // Every element is written before its first read: conversion fills p,
    // then each horizontal pass fills tmp before the vertical pass reads it.
    // Value-initialized vectors clear both large buffers unnecessarily.
    std::unique_ptr<float[]> p(new float[n * ch]);
    // Per texel, so the conversions split by rows like the passes.
    const long long texels = static_cast<long long>(n) * ch;
    constexpr long long kParallelTexels = 1 << 17;
    uint8_t* bytes = rgba->data();
    float* planes = p.get();
    const auto rows_of = [width](int row_begin, int row_end) {
        return std::pair<size_t, size_t>(static_cast<size_t>(row_begin) * width,
                                         static_cast<size_t>(row_end) * width);
    };
    parallel_ranges(height, 1, texels, kParallelTexels, [&](int row_begin, int row_end) {
        const auto [first, last] = rows_of(row_begin, row_end);
        if (flat) {
            for (size_t i = first; i < last; ++i) planes[i] = bytes[i * 4 + 3] / 255.0f;
        } else {
            for (size_t i = first; i < last; ++i) {
                const float a = bytes[i * 4 + 3] / 255.0f;
                planes[i * 4 + 0] = bytes[i * 4 + 0] / 255.0f * a;
                planes[i * 4 + 1] = bytes[i * 4 + 1] / 255.0f * a;
                planes[i * 4 + 2] = bytes[i * 4 + 2] / 255.0f * a;
                planes[i * 4 + 3] = a;
            }
        }
    });
    // Three box blurs of width w approximate a Gaussian of sigma:
    // w = sqrt(12 sigma^2 / 3 + 1).
    const int box = std::max(1, static_cast<int>(std::sqrt(12.0 * sigma * sigma / 3.0 + 1.0)));
    const int r = box / 2;
    std::unique_ptr<float[]> tmp(new float[n * ch]);
    const auto prepared = blur_log ? std::chrono::steady_clock::now()
                                   : std::chrono::steady_clock::time_point{};
    double horizontal_ms = 0, vertical_ms = 0;
    if (flat) blur_planes<1>(p.get(), tmp.get(), width, height, r,
                             blur_log ? &horizontal_ms : nullptr, blur_log ? &vertical_ms : nullptr);
    else blur_planes<4>(p.get(), tmp.get(), width, height, r,
                        blur_log ? &horizontal_ms : nullptr, blur_log ? &vertical_ms : nullptr);
    const auto filtered = blur_log ? std::chrono::steady_clock::now()
                                   : std::chrono::steady_clock::time_point{};
    const auto report = [&] {
        if (!blur_log) return;
        const auto end = std::chrono::steady_clock::now();
        std::fprintf(stderr, "  [blur] %dx%d sigma %.3f channels %d: prepare %.3f horizontal %.3f"
            " vertical %.3f finish %.3f total %.3f ms\n", width, height, sigma, ch,
            std::chrono::duration<double, std::milli>(prepared - start).count(),
            horizontal_ms, vertical_ms,
            std::chrono::duration<double, std::milli>(end - filtered).count(),
            std::chrono::duration<double, std::milli>(end - start).count());
    };
    const auto byte = [](float v) {
        const float scaled = std::clamp(v, 0.0f, 1.0f) * 255;
        // Nonnegative and bounded: truncating after +0.5 is lround. Add in
        // double so a float immediately below a half does not round up to it.
        return static_cast<uint8_t>(static_cast<double>(scaled) + 0.5);
    };
    if (flat) {
        // The same shape the four-channel tail has: a texel the blur left with
        // no coverage at all keeps black, so bilinear filtering across the
        // texture's transparent edge behaves exactly as it did before.
        parallel_ranges(height, 1, texels, kParallelTexels, [&](int row_begin, int row_end) {
            const auto [first, last] = rows_of(row_begin, row_end);
            for (size_t i = first; i < last; ++i) {
                const float a = planes[i];
                const bool covered = a > 0;
                bytes[i * 4 + 0] = covered ? fr : 0;
                bytes[i * 4 + 1] = covered ? fg : 0;
                bytes[i * 4 + 2] = covered ? fb : 0;
                bytes[i * 4 + 3] = byte(a);
            }
        });
        report();
        return;
    }
    parallel_ranges(height, 1, texels, kParallelTexels, [&](int row_begin, int row_end) {
        const auto [first, last] = rows_of(row_begin, row_end);
        for (size_t i = first; i < last; ++i) {
            const float a = planes[i * 4 + 3];
            if (a > 0) {
                bytes[i * 4 + 0] = byte(planes[i * 4 + 0] / a);
                bytes[i * 4 + 1] = byte(planes[i * 4 + 1] / a);
                bytes[i * 4 + 2] = byte(planes[i * 4 + 2] / a);
            } else {
                bytes[i * 4 + 0] = bytes[i * 4 + 1] = bytes[i * 4 + 2] = 0;
            }
            bytes[i * 4 + 3] = byte(a);
        }
    });
    report();
}

void blur_flat_rgba(std::vector<uint8_t>* rgba, int width, int height, double sigma) {
    blur_impl(rgba, width, height, sigma, true);
}

void blur_rgba(std::vector<uint8_t>* rgba, int width, int height, double sigma) {
    blur_impl(rgba, width, height, sigma, false);
}

} // namespace weva
