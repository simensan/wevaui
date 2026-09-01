#include "weva/background.h"

#include "weva/css_value.h"

#include <algorithm>
#include <cmath>
#include <cstdlib>

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

// A stop position: `%`, `px` (kept as px) or a bare number (fraction).
bool parse_stop_position(std::string_view s, GradientStop* stop) {
    s = trim(s);
    double n = 0;
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

// Interpolates in premultiplied sRGB (Images L4 §3.4.1), so a fade to
// `transparent` does not pass through grey.
Srgb mix(const Srgb& a, const Srgb& b, double t) {
    const float ft = static_cast<float>(std::clamp(t, 0.0, 1.0));
    const float pa_r = a.r * a.a, pa_g = a.g * a.a, pa_b = a.b * a.a;
    const float pb_r = b.r * b.a, pb_g = b.g * b.a, pb_b = b.b * b.a;
    const float alpha = a.a + (b.a - a.a) * ft;
    const float r = pa_r + (pb_r - pa_r) * ft;
    const float g = pa_g + (pb_g - pa_g) * ft;
    const float bb = pa_b + (pb_b - pa_b) * ft;
    if (alpha <= 0) return {0, 0, 0, 0};
    return {r / alpha, g / alpha, bb / alpha, alpha};
}

// The colour at `t` along normalized stops. `srgb` holds the stops' colours.
Srgb sample_stops(const std::vector<GradientStop>& stops, const std::vector<Srgb>& srgb, double t,
                  bool repeating) {
    if (stops.empty()) return {0, 0, 0, 0};
    if (stops.size() == 1) return srgb[0];
    const double first = stops.front().position;
    const double last = stops.back().position;
    if (repeating && last > first) {
        const double span = last - first;
        t = first + std::fmod(std::fmod(t - first, span) + span, span);
    }
    if (t <= first) return srgb.front();
    if (t >= last) return srgb.back();
    for (size_t i = 0; i + 1 < stops.size(); ++i) {
        // A hint sits between two colour stops and bends the interpolation.
        if (stops[i].is_hint) continue;
        size_t next = i + 1;
        const GradientStop* hint = nullptr;
        if (next < stops.size() && stops[next].is_hint) {
            hint = &stops[next];
            ++next;
        }
        if (next >= stops.size()) return srgb[i];
        const double p0 = stops[i].position, p1 = stops[next].position;
        if (t < p0 || t > p1) continue;
        double local = p1 > p0 ? (t - p0) / (p1 - p0) : 0;
        if (hint && p1 > p0) {
            const double h = std::clamp((hint->position - p0) / (p1 - p0), 1e-6, 1 - 1e-6);
            local = std::pow(local, std::log(0.5) / std::log(h));
        }
        return mix(srgb[i], srgb[next], local);
    }
    return srgb.back();
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
    return p;
}

Srgb sample_prepared(const PreparedGradient& p, double x, double y) {
    double t = 0;
    switch (p.g->kind) {
        case Gradient::Kind::Linear:
            t = ((x - p.cx) * p.dx + (y - p.cy) * p.dy) / p.line_length + 0.5;
            break;
        case Gradient::Kind::Radial: {
            const double ex = (x - p.radial.cx) / p.radial.rx;
            const double ey = (y - p.radial.cy) / p.radial.ry;
            t = std::sqrt(ex * ex + ey * ey);
            break;
        }
        case Gradient::Kind::Conic: {
            double a = std::atan2(x - p.cx, -(y - p.cy));   // 0 at top, clockwise
            a -= p.g->from_deg * kPi / 180.0;
            a = std::fmod(a, 2 * kPi);
            if (a < 0) a += 2 * kPi;
            t = a / (2 * kPi);
            break;
        }
    }
    return sample_stops(p.stops, p.colors, t, p.g->repeating);
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

std::vector<BackgroundLayer> resolve_background_layers(const ComputedStyle* style,
                                                       const LinearColor& current_color) {
    std::vector<BackgroundLayer> layers;
    if (!style) return layers;
    const std::string_view images = trim(style->get("background-image"));
    if (images.empty() || iequals(images, "none")) return layers;
    const std::vector<std::string_view> image_list = split_top_level(images, ',');
    const std::vector<std::string_view> positions = split_top_level(style->get("background-position"), ',');
    const std::vector<std::string_view> sizes = split_top_level(style->get("background-size"), ',');
    const std::vector<std::string_view> repeats = split_top_level(style->get("background-repeat"), ',');
    for (size_t i = 0; i < image_list.size(); ++i) {
        const std::string_view img = trim(image_list[i]);
        if (img.empty() || iequals(img, "none")) continue;
        BackgroundLayer layer;
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
        // Lists shorter than the image list repeat (§2.1).
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
        layers.push_back(std::move(layer));
    }
    return layers;
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

void rasterize_background(const std::vector<BackgroundLayer>& layers, const LinearColor& color,
                          double width, double height, int tex_w, int tex_h,
                          const LayoutContext& ctx, double font_size,
                          std::vector<uint8_t>* out_rgba) {
    tex_w = std::max(1, tex_w);
    tex_h = std::max(1, tex_h);
    out_rgba->assign(static_cast<size_t>(tex_w) * tex_h * 4, 0);
    const Srgb base = to_srgb(color);

    // Each layer's tile and origin; a gradient has no intrinsic size, so
    // `auto`, `cover` and `contain` all mean the painting area (§3.9).
    struct Tile {
        PreparedGradient prepared;
        double ox, oy, tw, th;
        bool repeat_x, repeat_y;
    };
    std::vector<Tile> tiles;
    for (const BackgroundLayer& l : layers) {
        if (!l.is_gradient) continue;
        Tile t;
        t.tw = width;
        t.th = height;
        const bool auto_x = iequals(l.size_x, "auto") || iequals(l.size_x, "cover") || iequals(l.size_x, "contain");
        const bool auto_y = iequals(l.size_y, "auto");
        if (!auto_x) t.tw = std::max(1e-6, resolve_size_component(l.size_x, width, ctx, font_size));
        if (!auto_y) t.th = std::max(1e-6, resolve_size_component(l.size_y, height, ctx, font_size));
        t.ox = resolve_position(l.pos_x, width, t.tw, true, ctx, font_size);
        t.oy = resolve_position(l.pos_y, height, t.th, false, ctx, font_size);
        t.repeat_x = l.repeat_x;
        t.repeat_y = l.repeat_y;
        t.prepared = prepare(l.gradient, t.tw, t.th, ctx, font_size);
        tiles.push_back(std::move(t));
    }

    const double sx = width / tex_w, sy = height / tex_h;
    for (int py = 0; py < tex_h; ++py) {
        const double y = (py + 0.5) * sy;
        for (int px = 0; px < tex_w; ++px) {
            const double x = (px + 0.5) * sx;
            // Premultiplied source-over, bottom layer (the last) first.
            float r = base.r * base.a, g = base.g * base.a, b = base.b * base.a, a = base.a;
            for (size_t i = tiles.size(); i-- > 0;) {
                const Tile& t = tiles[i];
                double lx = x - t.ox, ly = y - t.oy;
                if (t.repeat_x) lx = std::fmod(std::fmod(lx, t.tw) + t.tw, t.tw);
                else if (lx < 0 || lx >= t.tw) continue;
                if (t.repeat_y) ly = std::fmod(std::fmod(ly, t.th) + t.th, t.th);
                else if (ly < 0 || ly >= t.th) continue;
                const Srgb s = sample_prepared(t.prepared, lx, ly);
                const float sa = s.a;
                r = s.r * sa + r * (1 - sa);
                g = s.g * sa + g * (1 - sa);
                b = s.b * sa + b * (1 - sa);
                a = sa + a * (1 - sa);
            }
            uint8_t* o = out_rgba->data() + (static_cast<size_t>(py) * tex_w + px) * 4;
            if (a > 0) { r /= a; g /= a; b /= a; }
            o[0] = static_cast<uint8_t>(std::lround(std::clamp(r, 0.0f, 1.0f) * 255));
            o[1] = static_cast<uint8_t>(std::lround(std::clamp(g, 0.0f, 1.0f) * 255));
            o[2] = static_cast<uint8_t>(std::lround(std::clamp(b, 0.0f, 1.0f) * 255));
            o[3] = static_cast<uint8_t>(std::lround(std::clamp(a, 0.0f, 1.0f) * 255));
        }
    }
}

} // namespace weva
