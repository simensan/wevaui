#include "weva/background.h"
#include "weva/style_resolver.h"

#include "weva/css_value.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
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
// `inv_span` is prepare()'s table of 1/(p1 - p0) per stop pair, or null when
// the caller has not built one -- the parse-time callers sample a handful of
// points and do not need it.
Srgb sample_stops(const std::vector<GradientStop>& stops, const std::vector<Srgb>& srgb, double t,
                  bool repeating, const std::vector<double>* inv_span = nullptr) {
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
        // The reciprocal when prepare() has one: this is the last divide left
        // in the per-pixel path.
        double local = 0;
        if (inv_span && i < inv_span->size() && (*inv_span)[i] > 0) {
            local = (t - p0) * (*inv_span)[i];
        } else if (p1 > p0) {
            local = (t - p0) / (p1 - p0);
        }
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
    // Reciprocal of each span between consecutive colour stops, indexed by the
    // FIRST stop of the pair, for the same reason.
    std::vector<double> inv_span;
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
    p.inv_span.assign(p.stops.size(), 0.0);
    for (size_t i = 0; i + 1 < p.stops.size(); ++i) {
        if (p.stops[i].is_hint) continue;
        size_t next = i + 1;
        if (next < p.stops.size() && p.stops[next].is_hint) ++next;
        if (next >= p.stops.size()) continue;
        const double span = p.stops[next].position - p.stops[i].position;
        p.inv_span[i] = span > 0 ? 1.0 / span : 0.0;
    }
    return p;
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
            a = std::fmod(a, 2 * kPi);
            if (a < 0) a += 2 * kPi;
            t = a / (2 * kPi);
            break;
        }
    }
    return t;
}

Srgb sample_prepared(const PreparedGradient& p, double x, double y) {
    return sample_stops(p.stops, p.colors, gradient_t(p, x, y), p.g->repeating, &p.inv_span);
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
    };
    std::vector<Tile> tiles;
    for (const BackgroundLayer& l : layers) {
        if (!l.is_gradient && !l.image) continue;
        Tile t;
        t.image = l.is_gradient ? nullptr : l.image;
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
        t.ox = resolve_position(l.pos_x, width, t.tw, true, ctx, font_size);
        t.oy = resolve_position(l.pos_y, height, t.th, false, ctx, font_size);
        t.repeat_x = l.repeat_x;
        t.repeat_y = l.repeat_y;
        // Covered on an axis means every sample lands in the first tile, so
        // wrapping is the identity and the bounds test below always passes.
        t.wrap_x = l.repeat_x && !(t.ox <= 0 && t.ox + t.tw >= width);
        t.wrap_y = l.repeat_y && !(t.oy <= 0 && t.oy + t.th >= height);
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
    int samples = 1;
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
    const bool flat_x = constant_along(true);
    const bool flat_y = !flat_x && constant_along(false);

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
    struct EdgeTest {
        double half_width = 0;
        double span = 0;        // repeating period, 0 when not repeating
        std::vector<double> positions;
    };
    std::vector<EdgeTest> edges;
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

    // True when the texel's t-range reaches a stop, so the value across it is
    // not one straight ramp.
    const auto steps_here = [&](size_t i, double t) {
        const EdgeTest& e = edges[i];
        const double lo = t - e.half_width, hi = t + e.half_width;
        if (e.span > 0) {
            if (2 * e.half_width >= e.span) return true;
            const double base = e.positions.front();
            const double w = base + std::fmod(std::fmod(t - base, e.span) + e.span, e.span);
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

    static const bool gradient_log = std::getenv("WEVA_GRADIENT_LOG") != nullptr;
    if (gradient_log) {
        std::fprintf(stderr, "  [grad] %dx%d tex, %zu tiles, %d^2 samples, flat_x %d flat_y %d\n",
                     tex_w, tex_h, tiles.size(), samples, flat_x ? 1 : 0, flat_y ? 1 : 0);
    }
    const double sx = width / tex_w, sy = height / tex_h;
    for (int py = 0; py < tex_h; ++py) {
        if (flat_y && py > 0) {
            // Every row is the row above it.
            std::memcpy(out_rgba->data() + static_cast<size_t>(py) * tex_w * 4, out_rgba->data(),
                        static_cast<size_t>(tex_w) * 4);
            continue;
        }
        for (int px = 0; px < tex_w; ++px) {
            if (flat_x && px > 0) {
                std::memcpy(out_rgba->data() + (static_cast<size_t>(py) * tex_w + px) * 4,
                            out_rgba->data() + static_cast<size_t>(py) * tex_w * 4, 4);
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
            const double tinv = 1.0 / texel_samples;
            for (int oy = 0; oy < texel_samples; ++oy) {
                const double y = (py + (oy + 0.5) * tinv) * sy;
                for (int ox = 0; ox < texel_samples; ++ox) {
                    const double x = (px + (ox + 0.5) * tinv) * sx;
                    // Premultiplied source-over, bottom layer (the last) first.
                    float r = base.r * base.a, g = base.g * base.a, b = base.b * base.a;
                    float a = base.a;
                    for (size_t i = tiles.size(); i-- > 0;) {
                        const Tile& t = tiles[i];
                        double lx = x - t.ox, ly = y - t.oy;
                        if (t.wrap_x) lx = std::fmod(std::fmod(lx, t.tw) + t.tw, t.tw);
                        else if (!t.repeat_x && (lx < 0 || lx >= t.tw)) continue;
                        if (t.wrap_y) ly = std::fmod(std::fmod(ly, t.th) + t.th, t.th);
                        else if (!t.repeat_y && (ly < 0 || ly >= t.th)) continue;
                        const Srgb s = t.image ? sample_image(*t.image, lx, ly, t.tw, t.th)
                                               : sample_prepared(t.prepared, lx, ly);
                        const float sa = s.a;
                        r = s.r * sa + r * (1 - sa);
                        g = s.g * sa + g * (1 - sa);
                        b = s.b * sa + b * (1 - sa);
                        a = sa + a * (1 - sa);
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
                const float scaled = std::clamp(v, 0.0f, 1.0f) * 255.0f;
                return static_cast<uint8_t>(scaled + 0.5f);
            };
            o[0] = byte(r);
            o[1] = byte(g);
            o[2] = byte(b);
            o[3] = byte(a);
        }
    }
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

void rasterize_background_padded(const std::vector<BackgroundLayer>& layers,
                                 const LinearColor& color, double width, double height,
                                 int tex_w, int tex_h, int pad, const BorderRadii* radii,
                                 const LayoutContext& ctx, double font_size,
                                 std::vector<uint8_t>* out_rgba) {
    const int inner_w = std::max(1, tex_w - 2 * pad), inner_h = std::max(1, tex_h - 2 * pad);
    std::vector<uint8_t> inner;
    rasterize_background(layers, color, width, height, inner_w, inner_h, ctx, font_size, &inner);
    out_rgba->assign(static_cast<size_t>(tex_w) * tex_h * 4, 0);
    const double sx = width / inner_w, sy = height / inner_h;
    for (int y = 0; y < inner_h; ++y) {
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
}

void blur_rgba(std::vector<uint8_t>* rgba, int width, int height, double sigma) {
    if (sigma <= 0.3 || width <= 0 || height <= 0) return;
    const size_t n = static_cast<size_t>(width) * height;
    std::vector<float> p(n * 4);
    for (size_t i = 0; i < n; ++i) {
        const float a = (*rgba)[i * 4 + 3] / 255.0f;
        p[i * 4 + 0] = (*rgba)[i * 4 + 0] / 255.0f * a;
        p[i * 4 + 1] = (*rgba)[i * 4 + 1] / 255.0f * a;
        p[i * 4 + 2] = (*rgba)[i * 4 + 2] / 255.0f * a;
        p[i * 4 + 3] = a;
    }
    // Three box blurs of width w approximate a Gaussian of sigma:
    // w = sqrt(12 sigma^2 / 3 + 1).
    const int box = std::max(1, static_cast<int>(std::sqrt(12.0 * sigma * sigma / 3.0 + 1.0)));
    const int r = box / 2;
    std::vector<float> tmp(n * 4);
    // A box blur is a sliding window: moving one pixel adds one sample and
    // drops one, so a pass costs the same whatever the radius. Re-summing the
    // whole window per pixel instead made every blur O(width * radius), and a
    // page of large glows paid for it -- neon spent 1.1 s of its first update
    // blurring text shadows, at a box width of ~2 sigma per pass, three passes
    // each way.
    //
    // Edges extend the outermost pixel, which is what the window did when it
    // clamped its index, so the divisor stays 2r+1 everywhere.
    const int taps = 2 * r + 1;
    const auto pass_h = [&](const std::vector<float>& in, std::vector<float>* out) {
        for (int y = 0; y < height; ++y) {
            const float* row = in.data() + static_cast<size_t>(y) * width * 4;
            float* orow = out->data() + static_cast<size_t>(y) * width * 4;
            double acc[4] = {0, 0, 0, 0};
            for (int k = -r; k <= r; ++k) {
                const int xx = std::clamp(k, 0, width - 1);
                for (int c = 0; c < 4; ++c) acc[c] += row[xx * 4 + c];
            }
            for (int x = 0; x < width; ++x) {
                for (int c = 0; c < 4; ++c) orow[x * 4 + c] = static_cast<float>(acc[c] / taps);
                const int add = std::clamp(x + r + 1, 0, width - 1);
                const int drop = std::clamp(x - r, 0, width - 1);
                for (int c = 0; c < 4; ++c) acc[c] += row[add * 4 + c] - row[drop * 4 + c];
            }
        }
    };
    const auto pass_v = [&](const std::vector<float>& in, std::vector<float>* out) {
        // Column-major over a row-major buffer, so the window walks down one
        // column at a time and the stride is the row.
        const size_t stride = static_cast<size_t>(width) * 4;
        for (int x = 0; x < width; ++x) {
            const float* col = in.data() + static_cast<size_t>(x) * 4;
            float* ocol = out->data() + static_cast<size_t>(x) * 4;
            double acc[4] = {0, 0, 0, 0};
            for (int k = -r; k <= r; ++k) {
                const int yy = std::clamp(k, 0, height - 1);
                for (int c = 0; c < 4; ++c) acc[c] += col[yy * stride + c];
            }
            for (int y = 0; y < height; ++y) {
                for (int c = 0; c < 4; ++c) {
                    ocol[static_cast<size_t>(y) * stride + c] = static_cast<float>(acc[c] / taps);
                }
                const int add = std::clamp(y + r + 1, 0, height - 1);
                const int drop = std::clamp(y - r, 0, height - 1);
                for (int c = 0; c < 4; ++c) {
                    acc[c] += col[add * stride + c] - col[drop * stride + c];
                }
            }
        }
    };
    for (int i = 0; i < 3; ++i) {
        pass_h(p, &tmp);
        pass_v(tmp, &p);
    }
    for (size_t i = 0; i < n; ++i) {
        const float a = p[i * 4 + 3];
        const auto byte = [](float v) {
            return static_cast<uint8_t>(std::lround(std::clamp(v, 0.0f, 1.0f) * 255));
        };
        if (a > 0) {
            (*rgba)[i * 4 + 0] = byte(p[i * 4 + 0] / a);
            (*rgba)[i * 4 + 1] = byte(p[i * 4 + 1] / a);
            (*rgba)[i * 4 + 2] = byte(p[i * 4 + 2] / a);
        } else {
            (*rgba)[i * 4 + 0] = (*rgba)[i * 4 + 1] = (*rgba)[i * 4 + 2] = 0;
        }
        (*rgba)[i * 4 + 3] = byte(a);
    }
}

} // namespace weva
