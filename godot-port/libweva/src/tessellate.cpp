#include "weva/tessellate.h"

#include <algorithm>
#include <cmath>

namespace weva {

namespace {

constexpr double kPi = 3.14159265358979323846;

Vertex vert(double x, double y, const LinearColor& c) {
    Vertex v;
    v.position = {static_cast<float>(x), static_cast<float>(y)};
    v.color = c;
    return v;
}

// Points along one corner's arc, from `start_angle` sweeping 90 degrees.
// `cx, cy` is the ellipse centre and `rx, ry` its radii.
void arc_points(double cx, double cy, double rx, double ry, double start_angle, int segments,
                std::vector<std::pair<double, double>>* out) {
    if (rx <= 0 || ry <= 0) {
        // A square corner is a single point, so a zero radius costs nothing
        // extra — which matters because most boxes have no radius at all.
        out->emplace_back(cx, cy);
        return;
    }
    for (int i = 0; i <= segments; ++i) {
        const double t = start_angle + (kPi * 0.5) * (static_cast<double>(i) / segments);
        out->emplace_back(cx + rx * std::cos(t), cy + ry * std::sin(t));
    }
}

// The outline of a rounded rect, clockwise from the top-left corner in screen
// coordinates (y down).
std::vector<std::pair<double, double>> rounded_outline(const Rect& r, const BorderRadii& radii,
                                                       int segments) {
    const BorderRadii c = clamp_radii_to_rect(radii, r.width, r.height);
    std::vector<std::pair<double, double>> pts;
    pts.reserve(static_cast<size_t>(4 * (segments + 1)));
    // Angles run from pi (left) round to pi/2 (down) because y grows downward.
    arc_points(r.x + c.top_left.x_radius, r.y + c.top_left.y_radius, c.top_left.x_radius,
               c.top_left.y_radius, kPi, segments, &pts);
    arc_points(r.right() - c.top_right.x_radius, r.y + c.top_right.y_radius,
               c.top_right.x_radius, c.top_right.y_radius, -kPi * 0.5, segments, &pts);
    arc_points(r.right() - c.bottom_right.x_radius, r.bottom() - c.bottom_right.y_radius,
               c.bottom_right.x_radius, c.bottom_right.y_radius, 0, segments, &pts);
    arc_points(r.x + c.bottom_left.x_radius, r.bottom() - c.bottom_left.y_radius,
               c.bottom_left.x_radius, c.bottom_left.y_radius, kPi * 0.5, segments, &pts);
    return pts;
}

// Which of the four edges a point belongs to, for picking a border colour.
// Corners fall to whichever side they are nearer, which is a simple stand-in
// for a proper mitre and is invisible when adjacent colours match.
int edge_of(double x, double y, const Rect& r) {
    const double dt = y - r.y, db = r.bottom() - y, dl = x - r.x, dr = r.right() - x;
    const double m = std::min(std::min(dt, db), std::min(dl, dr));
    if (m == dt) return 0;
    if (m == dr) return 1;
    if (m == db) return 2;
    return 3;
}

} // namespace

void Mesh::append(const Mesh& other) {
    const uint32_t base = static_cast<uint32_t>(vertices.size());
    vertices.insert(vertices.end(), other.vertices.begin(), other.vertices.end());
    indices.reserve(indices.size() + other.indices.size());
    for (uint32_t i : other.indices) indices.push_back(base + i);
}

BorderRadii clamp_radii_to_rect(const BorderRadii& r, double width, double height) {
    // §5.5: find the tightest overflow across the four edges and scale EVERY
    // radius by that one factor. Scaling per corner would distort the shape.
    double f = 1.0;
    const auto limit = [&](double sum, double extent) {
        if (sum > 0 && sum > extent) f = std::min(f, extent / sum);
    };
    limit(r.top_left.x_radius + r.top_right.x_radius, width);
    limit(r.bottom_left.x_radius + r.bottom_right.x_radius, width);
    limit(r.top_left.y_radius + r.bottom_left.y_radius, height);
    limit(r.top_right.y_radius + r.bottom_right.y_radius, height);
    if (f >= 1.0) return r;
    const auto s = [&](const CornerRadius& c) {
        return CornerRadius(c.x_radius * f, c.y_radius * f);
    };
    return BorderRadii(s(r.top_left), s(r.top_right), s(r.bottom_right), s(r.bottom_left));
}

BorderRadii inset_radii(const BorderRadii& r, double top, double right, double bottom,
                        double left) {
    const auto in = [](double v, double by) { return std::max(0.0, v - by); };
    return BorderRadii(CornerRadius(in(r.top_left.x_radius, left), in(r.top_left.y_radius, top)),
                       CornerRadius(in(r.top_right.x_radius, right), in(r.top_right.y_radius, top)),
                       CornerRadius(in(r.bottom_right.x_radius, right),
                                    in(r.bottom_right.y_radius, bottom)),
                       CornerRadius(in(r.bottom_left.x_radius, left),
                                    in(r.bottom_left.y_radius, bottom)));
}

void tessellate_rect(const Rect& r, const LinearColor& color, Mesh* out) {
    if (r.is_empty() || color.a <= 0) return;
    const uint32_t base = static_cast<uint32_t>(out->vertices.size());
    out->vertices.push_back(vert(r.x, r.y, color));
    out->vertices.push_back(vert(r.right(), r.y, color));
    out->vertices.push_back(vert(r.right(), r.bottom(), color));
    out->vertices.push_back(vert(r.x, r.bottom(), color));
    for (uint32_t i : {0u, 1u, 2u, 0u, 2u, 3u}) out->indices.push_back(base + i);
}

void tessellate_rounded_rect(const Rect& r, const BorderRadii& radii, const LinearColor& color,
                             Mesh* out, int segments) {
    if (r.is_empty() || color.a <= 0) return;
    if (radii.is_zero()) {
        tessellate_rect(r, color, out);
        return;
    }
    const std::vector<std::pair<double, double>> pts = rounded_outline(r, radii, segments);
    const uint32_t base = static_cast<uint32_t>(out->vertices.size());
    // A centre vertex plus a fan. Correct for any convex outline, which a
    // rounded rect always is.
    out->vertices.push_back(vert(r.x + r.width * 0.5, r.y + r.height * 0.5, color));
    for (const auto& p : pts) out->vertices.push_back(vert(p.first, p.second, color));
    const uint32_t n = static_cast<uint32_t>(pts.size());
    for (uint32_t i = 0; i < n; ++i) {
        out->indices.push_back(base);
        out->indices.push_back(base + 1 + i);
        out->indices.push_back(base + 1 + ((i + 1) % n));
    }
}

void tessellate_border(const Rect& outer, const BorderRadii& outer_radii, double top,
                       double right, double bottom, double left, const LinearColor colors[4],
                       Mesh* out, int segments) {
    if (outer.is_empty()) return;
    if (top <= 0 && right <= 0 && bottom <= 0 && left <= 0) return;

    const Rect inner(outer.x + left, outer.y + top,
                     std::max(0.0, outer.width - left - right),
                     std::max(0.0, outer.height - top - bottom));
    const BorderRadii inner_radii = inset_radii(clamp_radii_to_rect(outer_radii, outer.width,
                                                                   outer.height),
                                                top, right, bottom, left);

    const std::vector<std::pair<double, double>> o = rounded_outline(outer, outer_radii, segments);
    const std::vector<std::pair<double, double>> i2 =
        rounded_outline(inner, inner_radii, segments);
    // Both outlines walk the same corners with the same segment count, so they
    // have matching vertex counts and can be zipped into a ring.
    if (o.size() != i2.size() || o.empty()) return;

    const uint32_t base = static_cast<uint32_t>(out->vertices.size());
    const uint32_t n = static_cast<uint32_t>(o.size());
    for (uint32_t k = 0; k < n; ++k) {
        const LinearColor& c = colors[edge_of(o[k].first, o[k].second, outer)];
        out->vertices.push_back(vert(o[k].first, o[k].second, c));
        out->vertices.push_back(vert(i2[k].first, i2[k].second, c));
    }
    for (uint32_t k = 0; k < n; ++k) {
        const uint32_t a = base + k * 2;
        const uint32_t b = a + 1;
        const uint32_t c = base + ((k + 1) % n) * 2;
        const uint32_t d = c + 1;
        for (uint32_t idx : {a, c, b}) out->indices.push_back(idx);
        for (uint32_t idx : {b, c, d}) out->indices.push_back(idx);
    }
}

} // namespace weva
