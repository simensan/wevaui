#include "weva/tessellate.h"

#include <array>

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
                std::vector<std::pair<double, double>>* out, bool uniform_corner_points = false) {
    if (rx <= 0 || ry <= 0) {
        // A square corner is a single point, so a zero radius costs nothing
        // extra — which matters because most boxes have no radius at all.
        //
        // `uniform_corner_points` overrides that for a caller zipping two
        // outlines together: it needs both to have the SAME point count, and a
        // square corner opposite a rounded one otherwise breaks the pairing.
        // The extra points are coincident, so the shape is identical.
        if (!uniform_corner_points) {
            out->emplace_back(cx, cy);
            return;
        }
        for (int i = 0; i <= segments; ++i) out->emplace_back(cx, cy);
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
                                                       int segments,
                                                       bool uniform_corner_points = false) {
    const BorderRadii c = clamp_radii_to_rect(radii, r.width, r.height);
    std::vector<std::pair<double, double>> pts;
    pts.reserve(static_cast<size_t>(4 * (segments + 1)));
    const bool u = uniform_corner_points;
    // Angles run from pi (left) round to pi/2 (down) because y grows downward.
    arc_points(r.x + c.top_left.x_radius, r.y + c.top_left.y_radius, c.top_left.x_radius,
               c.top_left.y_radius, kPi, segments, &pts, u);
    arc_points(r.right() - c.top_right.x_radius, r.y + c.top_right.y_radius,
               c.top_right.x_radius, c.top_right.y_radius, -kPi * 0.5, segments, &pts, u);
    arc_points(r.right() - c.bottom_right.x_radius, r.bottom() - c.bottom_right.y_radius,
               c.bottom_right.x_radius, c.bottom_right.y_radius, 0, segments, &pts, u);
    arc_points(r.x + c.bottom_left.x_radius, r.bottom() - c.bottom_left.y_radius,
               c.bottom_left.x_radius, c.bottom_left.y_radius, kPi * 0.5, segments, &pts, u);
    return pts;
}

// Which of the four edges a point belongs to, for picking a border colour.
// Corners fall to whichever side they are nearer, which is a simple stand-in
// for a proper mitre and is invisible when adjacent colours match.
int edge_of(double x, double y, const Rect& r, const double widths[4]) {
    const double dt = y - r.y, db = r.bottom() - y, dl = x - r.x, dr = r.right() - x;
    // A side that is not DRAWN cannot own a corner.
    //
    // Only the sides with width contribute geometry, so a corner assigned to a
    // zero-width side hands its colour to the strip a drawn side is making --
    // and an unset side's colour is `currentColor`, the text colour. A table
    // cell with nothing but `border-bottom: 1px solid <faint>` therefore drew
    // its rule fading into the text colour along its length: dark at one end
    // and bright at the other, with a visible step at every cell boundary.
    const double d[4] = {dt, dr, db, dl};
    double m = 0;
    int best = -1;
    for (int i = 0; i < 4; ++i) {
        if (widths[i] <= 0) continue;
        if (best < 0 || d[i] < m) {
            m = d[i];
            best = i;
        }
    }
    // Every side zero is not a border at all, but the caller decides that; fall
    // back to the old nearest-edge answer rather than reading past the array.
    if (best < 0) {
        const double n = std::min(std::min(dt, db), std::min(dl, dr));
        if (n == dt) return 0;
        if (n == dr) return 1;
        if (n == db) return 2;
        return 3;
    }
    return best;
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

// ---- antialiasing ---------------------------------------------------------
//
// A rasterizer that tests the pixel CENTRE against a triangle produces coverage
// of 0 or 1 and nothing between, so every non-axis-aligned edge comes out as a
// staircase. Chrome does not have this problem because Skia computes each
// pixel's exact fractional coverage into a mask; we cannot, because the seam
// with a backend is triangles.
//
// So the coverage is carried IN the geometry: the shape is drawn inset by half
// a pixel at full alpha, with a one-pixel band around it whose outer edge is
// alpha zero. The rasterizer interpolates that band, which gives 256 levels of
// coverage on any backend, with no multisampling and nothing for a host to
// implement — and both of ours stay pixel-identical, so the render gate keeps
// meaning what it meant.
//
// A band centred on the edge is exact for the case that matters most: an edge
// lying on a pixel boundary. Alpha runs 1 at boundary-0.5 to 0 at boundary+0.5,
// and the two pixel centres either side land on exactly 1 and exactly 0. So an
// axis-aligned rect on whole pixels stays as crisp as it was.
constexpr double kAaHalfWidth = 0.5;

// Offsets a convex outline by `d`, measured PERPENDICULAR TO EACH EDGE —
// outward for positive. The displacement at a corner is longer than `d` (it
// runs along the mitre), which is what keeps both of that corner's edges
// exactly `d` away rather than only the corner point.
std::vector<std::pair<double, double>> offset_outline(
    const std::vector<std::pair<double, double>>& pts, double d) {
    const size_t n = pts.size();
    std::vector<std::pair<double, double>> out(n);
    for (size_t i = 0; i < n; ++i) {
        // The neighbours have to be DISTINCT points: a square corner walked
        // with a uniform point count repeats itself, and a zero-length edge has
        // no normal.
        size_t prev = i;
        for (size_t k = 1; k <= n; ++k) {
            prev = (i + n - k) % n;
            if (pts[prev] != pts[i]) break;
        }
        size_t next = i;
        for (size_t k = 1; k <= n; ++k) {
            next = (i + k) % n;
            if (pts[next] != pts[i]) break;
        }
        const double ax = pts[i].first - pts[prev].first, ay = pts[i].second - pts[prev].second;
        const double bx = pts[next].first - pts[i].first, by = pts[next].second - pts[i].second;
        const double la = std::sqrt(ax * ax + ay * ay), lb = std::sqrt(bx * bx + by * by);
        if (la <= 0 || lb <= 0) {
            out[i] = pts[i];
            continue;
        }
        // Outward normal of each edge, for an outline wound so that the
        // interior is to the left.
        const double n1x = ay / la, n1y = -ax / la;
        const double n2x = by / lb, n2y = -bx / lb;
        double mx = n1x + n2x, my = n1y + n2y;
        const double lm = std::sqrt(mx * mx + my * my);
        if (lm <= 1e-9) {
            out[i] = pts[i];
            continue;
        }
        mx /= lm;
        my /= lm;
        // Scale along the mitre so each edge moves by exactly `d`. Capped
        // because a very sharp corner sends the mitre off to infinity.
        const double cos_half = mx * n1x + my * n1y;
        const double scale = cos_half > 0.2 ? d / cos_half : d * 5.0;
        out[i] = {pts[i].first + mx * scale, pts[i].second + my * scale};
    }
    return out;
}

// Fills a convex outline with a one-pixel coverage ramp around it.
void fill_outline_aa(const std::vector<std::pair<double, double>>& pts, double cx, double cy,
                     const LinearColor& color, bool antialias, Mesh* out) {
    if (pts.size() < 3) return;
    const uint32_t n = static_cast<uint32_t>(pts.size());

    if (!antialias) {
        const uint32_t base = static_cast<uint32_t>(out->vertices.size());
        out->vertices.push_back(vert(cx, cy, color));
        for (const auto& p : pts) out->vertices.push_back(vert(p.first, p.second, color));
        for (uint32_t i = 0; i < n; ++i) {
            out->indices.push_back(base);
            out->indices.push_back(base + 1 + i);
            out->indices.push_back(base + 1 + ((i + 1) % n));
        }
        return;
    }

    const std::vector<std::pair<double, double>> in = offset_outline(pts, -kAaHalfWidth);
    const std::vector<std::pair<double, double>> ex = offset_outline(pts, kAaHalfWidth);
    LinearColor clear = color;
    clear.a = 0;

    // Centre, then the inset ring, then the expanded ring.
    const uint32_t base = static_cast<uint32_t>(out->vertices.size());
    out->vertices.push_back(vert(cx, cy, color));
    for (const auto& p : in) out->vertices.push_back(vert(p.first, p.second, color));
    for (const auto& p : ex) out->vertices.push_back(vert(p.first, p.second, clear));

    const uint32_t inner = base + 1;
    const uint32_t outer = inner + n;
    for (uint32_t i = 0; i < n; ++i) {
        const uint32_t j = (i + 1) % n;
        out->indices.push_back(base);
        out->indices.push_back(inner + i);
        out->indices.push_back(inner + j);
        // The ramp.
        for (uint32_t idx : {inner + i, outer + i, inner + j}) out->indices.push_back(idx);
        for (uint32_t idx : {inner + j, outer + i, outer + j}) out->indices.push_back(idx);
    }
}

// Too small to inset half a pixel from both sides without turning inside out.
bool too_thin_to_feather(const Rect& r) {
    return r.width < 2 * kAaHalfWidth + 0.5 || r.height < 2 * kAaHalfWidth + 0.5;
}

void tessellate_rect(const Rect& r, const LinearColor& color, Mesh* out, bool antialias) {
    if (r.is_empty() || color.a <= 0) return;
    // A plain rect is never feathered. Its edges are axis-aligned, so a
    // pixel-centre rasterizer already gets their coverage exactly right when
    // they sit on whole pixels — which is the overwhelmingly common case, since
    // layout rounds to them — and a ramp would only cost vertices to reproduce
    // the same result. The staircase this whole mechanism exists for comes from
    // edges that are NOT axis-aligned: arcs, and anything under a rotation.
    (void)antialias;
    {
        // Two triangles from four corners, with no centre vertex — this is the
        // commonest shape in any document and it should not pay for a fan.
        const uint32_t base = static_cast<uint32_t>(out->vertices.size());
        out->vertices.push_back(vert(r.x, r.y, color));
        out->vertices.push_back(vert(r.right(), r.y, color));
        out->vertices.push_back(vert(r.right(), r.bottom(), color));
        out->vertices.push_back(vert(r.x, r.bottom(), color));
        for (uint32_t i : {0u, 1u, 2u, 0u, 2u, 3u}) out->indices.push_back(base + i);
        return;
    }
    const std::vector<std::pair<double, double>> pts = {
        {r.x, r.y}, {r.right(), r.y}, {r.right(), r.bottom()}, {r.x, r.bottom()}};
    fill_outline_aa(pts, r.x + r.width * 0.5, r.y + r.height * 0.5, color, true, out);
}

void tessellate_rounded_rect(const Rect& r, const BorderRadii& radii, const LinearColor& color,
                             Mesh* out, int segments, bool antialias) {
    if (r.is_empty() || color.a <= 0) return;
    if (radii.is_zero()) {
        tessellate_rect(r, color, out, antialias);
        return;
    }
    // A centre vertex plus a fan. Correct for any convex outline, which a
    // rounded rect always is — and the coverage ramp rides the same fan.
    const std::vector<std::pair<double, double>> pts = rounded_outline(r, radii, segments);
    fill_outline_aa(pts, r.x + r.width * 0.5, r.y + r.height * 0.5, color,
                    antialias && !too_thin_to_feather(r), out);
}

void tessellate_border(const Rect& outer, const BorderRadii& outer_radii, double top,
                       double right, double bottom, double left, const LinearColor colors[4],
                       Mesh* out, int segments, bool antialias) {
    if (outer.is_empty()) return;
    if (top <= 0 && right <= 0 && bottom <= 0 && left <= 0) return;

    // Indexed like `colors`: top, right, bottom, left.
    const double widths[4] = {top, right, bottom, left};
    const Rect inner(outer.x + left, outer.y + top,
                     std::max(0.0, outer.width - left - right),
                     std::max(0.0, outer.height - top - bottom));
    const BorderRadii inner_radii = inset_radii(clamp_radii_to_rect(outer_radii, outer.width,
                                                                   outer.height),
                                                top, right, bottom, left);

    std::vector<std::pair<double, double>> o = rounded_outline(outer, outer_radii, segments);
    std::vector<std::pair<double, double>> i2 = rounded_outline(inner, inner_radii, segments);
    // Both outlines walk the same corners with the same segment count, so they
    // normally have matching vertex counts and zip straight into a ring.
    //
    // They do NOT match when a corner is rounded on one outline and square on
    // the other, because a zero radius collapses to a single point. That
    // happens whenever the border is as thick as the radius — `border-radius:
    // 20px; border: 20px` is ordinary CSS — and the mismatch used to return
    // here, drawing NO BORDER AT ALL. Re-walk both with a uniform point count
    // instead; the extra points on a square corner are coincident, so the shape
    // is unchanged and only this path pays for them.
    if (o.size() != i2.size()) {
        o = rounded_outline(outer, outer_radii, segments, true);
        i2 = rounded_outline(inner, inner_radii, segments, true);
    }
    if (o.size() != i2.size() || o.empty()) return;

    const uint32_t n = static_cast<uint32_t>(o.size());
    // A ring is TWO boundaries, so it gets two coverage ramps: one outside the
    // outer outline and one inside the inner. The solid part is what is left
    // between them. Skipped when the ring is thinner than the two half-pixel
    // insets, which would turn it inside out.
    // Square corners need no ramp, for the same reason a plain rect does not:
    // every edge is axis-aligned and a pixel-centre rasterizer already resolves
    // it exactly. Only a radius puts a curve in the outline.
    const bool feather = antialias && !outer_radii.is_zero() && !too_thin_to_feather(outer) &&
                         std::min(std::min(top, right), std::min(bottom, left)) > 2 * kAaHalfWidth;

    const std::vector<std::pair<double, double>> o_solid =
        feather ? offset_outline(o, -kAaHalfWidth) : o;
    const std::vector<std::pair<double, double>> i_solid =
        feather ? offset_outline(i2, kAaHalfWidth) : i2;

    const uint32_t base = static_cast<uint32_t>(out->vertices.size());
    for (uint32_t k = 0; k < n; ++k) {
        const LinearColor& c = colors[edge_of(o[k].first, o[k].second, outer, widths)];
        out->vertices.push_back(vert(o_solid[k].first, o_solid[k].second, c));
        out->vertices.push_back(vert(i_solid[k].first, i_solid[k].second, c));
    }
    for (uint32_t k = 0; k < n; ++k) {
        const uint32_t a = base + k * 2;
        const uint32_t b = a + 1;
        const uint32_t c = base + ((k + 1) % n) * 2;
        const uint32_t d = c + 1;
        for (uint32_t idx : {a, c, b}) out->indices.push_back(idx);
        for (uint32_t idx : {b, c, d}) out->indices.push_back(idx);
    }
    if (!feather) return;

    const std::vector<std::pair<double, double>> o_edge = offset_outline(o, kAaHalfWidth);
    const std::vector<std::pair<double, double>> i_edge = offset_outline(i2, -kAaHalfWidth);
    const uint32_t ramp = static_cast<uint32_t>(out->vertices.size());
    for (uint32_t k = 0; k < n; ++k) {
        LinearColor c = colors[edge_of(o[k].first, o[k].second, outer, widths)];
        c.a = 0;
        out->vertices.push_back(vert(o_edge[k].first, o_edge[k].second, c));
        out->vertices.push_back(vert(i_edge[k].first, i_edge[k].second, c));
    }
    for (uint32_t k = 0; k < n; ++k) {
        const uint32_t k2 = (k + 1) % n;
        // Outside the outer boundary: solid outer -> transparent.
        for (uint32_t idx : {base + k * 2, ramp + k * 2, base + k2 * 2}) out->indices.push_back(idx);
        for (uint32_t idx : {base + k2 * 2, ramp + k * 2, ramp + k2 * 2}) out->indices.push_back(idx);
        // Inside the inner boundary: solid inner -> transparent.
        for (uint32_t idx : {base + k * 2 + 1, base + k2 * 2 + 1, ramp + k * 2 + 1}) {
            out->indices.push_back(idx);
        }
        for (uint32_t idx : {base + k2 * 2 + 1, ramp + k2 * 2 + 1, ramp + k * 2 + 1}) {
            out->indices.push_back(idx);
        }
    }
}

} // namespace weva

namespace weva {

namespace {

Vertex lerp_vertex(const Vertex& a, const Vertex& b, double t) {
    Vertex v;
    const float ft = static_cast<float>(t);
    v.position = {a.position.x + (b.position.x - a.position.x) * ft,
                  a.position.y + (b.position.y - a.position.y) * ft};
    v.color = LinearColor(a.color.r + (b.color.r - a.color.r) * ft,
                          a.color.g + (b.color.g - a.color.g) * ft,
                          a.color.b + (b.color.b - a.color.b) * ft,
                          a.color.a + (b.color.a - a.color.a) * ft);
    v.tex_coord = {a.tex_coord.x + (b.tex_coord.x - a.tex_coord.x) * ft,
                   a.tex_coord.y + (b.tex_coord.y - a.tex_coord.y) * ft};
    return v;
}

// Clips a convex polygon against one half-plane: keep where `side(p) >= 0`.
template <typename Side, typename At>
void clip_edge(const std::vector<Vertex>& in, std::vector<Vertex>* out, Side side, At at) {
    out->clear();
    const size_t n = in.size();
    for (size_t i = 0; i < n; ++i) {
        const Vertex& cur = in[i];
        const Vertex& prev = in[(i + n - 1) % n];
        const double dc = side(cur), dp = side(prev);
        if (dc >= 0) {
            if (dp < 0) out->push_back(lerp_vertex(prev, cur, at(prev, cur)));
            out->push_back(cur);
        } else if (dp >= 0) {
            out->push_back(lerp_vertex(prev, cur, at(prev, cur)));
        }
    }
}

} // namespace

namespace {

double cross2(const ClipPoint& o, const ClipPoint& a, const ClipPoint& b) {
    return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
}

bool point_in_triangle(const ClipPoint& p, const ClipPoint& a, const ClipPoint& b,
                       const ClipPoint& c) {
    // Triangle is CCW (positive area); strictly inside or on an edge counts.
    return cross2(a, b, p) >= 0 && cross2(b, c, p) >= 0 && cross2(c, a, p) >= 0;
}

// Ear clipping. O(n^2) per polygon, and polygons here have a handful of points.
void triangulate(std::vector<ClipPoint> pts, std::vector<std::array<ClipPoint, 3>>* out) {
    // Drop consecutive duplicates (a closing point equal to the first, or
    // percentages that resolved onto each other).
    std::vector<ClipPoint> clean;
    for (const ClipPoint& p : pts) {
        if (clean.empty() || std::fabs(clean.back().x - p.x) > 1e-9 || std::fabs(clean.back().y - p.y) > 1e-9) {
            clean.push_back(p);
        }
    }
    while (clean.size() > 1 && std::fabs(clean.front().x - clean.back().x) < 1e-9 &&
           std::fabs(clean.front().y - clean.back().y) < 1e-9) {
        clean.pop_back();
    }
    if (clean.size() < 3) return;
    double area = 0;
    for (size_t i = 0; i < clean.size(); ++i) {
        const ClipPoint& a = clean[i];
        const ClipPoint& b = clean[(i + 1) % clean.size()];
        area += a.x * b.y - b.x * a.y;
    }
    if (std::fabs(area) < 1e-12) return;
    if (area < 0) std::reverse(clean.begin(), clean.end());

    std::vector<size_t> idx(clean.size());
    for (size_t i = 0; i < idx.size(); ++i) idx[i] = i;
    size_t guard = 0;
    while (idx.size() > 3 && guard++ < 10000) {
        bool clipped = false;
        for (size_t i = 0; i < idx.size(); ++i) {
            const size_t ip = idx[(i + idx.size() - 1) % idx.size()];
            const size_t ic = idx[i];
            const size_t in = idx[(i + 1) % idx.size()];
            const ClipPoint &a = clean[ip], &b = clean[ic], &c = clean[in];
            if (cross2(a, b, c) <= 1e-12) continue;   // reflex or degenerate
            bool empty = true;
            for (size_t k : idx) {
                if (k == ip || k == ic || k == in) continue;
                if (point_in_triangle(clean[k], a, b, c)) { empty = false; break; }
            }
            if (!empty) continue;
            out->push_back({a, b, c});
            idx.erase(idx.begin() + static_cast<std::ptrdiff_t>(i));
            clipped = true;
            break;
        }
        if (!clipped) break;   // no ear: self-intersecting input; fan the rest
    }
    for (size_t i = 1; i + 1 < idx.size(); ++i) {
        const ClipPoint &a = clean[idx[0]], &b = clean[idx[i]], &c = clean[idx[i + 1]];
        if (cross2(a, b, c) > 1e-12) out->push_back({a, b, c});
        else if (cross2(a, b, c) < -1e-12) out->push_back({a, c, b});
    }
}

// Sutherland-Hodgman of a convex polygon of vertices against one CCW triangle.
void clip_to_triangle(const std::vector<Vertex>& in, const std::array<ClipPoint, 3>& t,
                      std::vector<Vertex>* poly, std::vector<Vertex>* scratch) {
    *poly = in;
    for (int e = 0; e < 3 && !poly->empty(); ++e) {
        const ClipPoint& p = t[static_cast<size_t>(e)];
        const ClipPoint& q = t[static_cast<size_t>((e + 1) % 3)];
        const double ex = q.x - p.x, ey = q.y - p.y;
        clip_edge(*poly, scratch,
                  [&](const Vertex& v) { return ex * (v.position.y - p.y) - ey * (v.position.x - p.x); },
                  [&](const Vertex& a, const Vertex& b) {
                      const double da = ex * (a.position.y - p.y) - ey * (a.position.x - p.x);
                      const double db = ex * (b.position.y - p.y) - ey * (b.position.x - p.x);
                      return da / (da - db);
                  });
        poly->swap(*scratch);
    }
}

} // namespace

void PreparedClip::prepare() {
    pieces.clear();
    triangulate(polygon, &pieces);
    piece_bounds.clear();
    piece_bounds.reserve(pieces.size());
    for (const std::array<ClipPoint, 3>& t : pieces) {
        piece_bounds.push_back({std::min({t[0].x, t[1].x, t[2].x}),
                                std::min({t[0].y, t[1].y, t[2].y}),
                                std::max({t[0].x, t[1].x, t[2].x}),
                                std::max({t[0].y, t[1].y, t[2].y})});
    }
    x0 = y0 = 1e300;
    x1 = y1 = -1e300;
    for (const ClipPoint& p : polygon) {
        x0 = std::min(x0, p.x); y0 = std::min(y0, p.y);
        x1 = std::max(x1, p.x); y1 = std::max(y1, p.y);
    }
    ix0 = iy0 = 0;
    ix1 = iy1 = -1;
    convex = polygon.size() >= 3;
    if (polygon.size() < 3) return;

    double area2 = 0;
    for (size_t k = 0; k < polygon.size(); ++k) {
        const ClipPoint& a = polygon[k];
        const ClipPoint& b = polygon[(k + 1) % polygon.size()];
        area2 += a.x * b.y - b.x * a.y;
    }
    const double orient = area2 >= 0 ? 1.0 : -1.0;
    // CONVEX only. For a convex polygon the intersection of the inward
    // half-planes IS the polygon, and a triangulation of it covers it exactly
    // -- so a triangle inside the half-planes is one the clip would have
    // reassembled from pieces. Neither holds for a shape with a reflex vertex,
    // and a `clip-path` may well have one.
    for (size_t k = 0; k < polygon.size() && convex; ++k) {
        const ClipPoint& a = polygon[k];
        const ClipPoint& b = polygon[(k + 1) % polygon.size()];
        const ClipPoint& c = polygon[(k + 2) % polygon.size()];
        const double cross = (b.x - a.x) * (c.y - b.y) - (b.y - a.y) * (c.x - b.x);
        if (cross * orient < -1e-9) convex = false;
    }
    if (!convex) return;

    // The largest axis-aligned rectangle that fits inside, found by shrinking
    // the bounding box about its centre until every corner is on the inward
    // side of every edge. A triangle whose bounds sit in it is emitted as it
    // is, which is what the clipping below is mostly able to skip.
    const double cx = 0.5 * (x0 + x1), cy = 0.5 * (y0 + y1);
    const double hw = 0.5 * (x1 - x0), hh = 0.5 * (y1 - y0);
    const auto fits = [&](double s) {
        const double qx[4] = {cx - hw * s, cx + hw * s, cx + hw * s, cx - hw * s};
        const double qy[4] = {cy - hh * s, cy - hh * s, cy + hh * s, cy + hh * s};
        for (size_t k = 0; k < polygon.size(); ++k) {
            const ClipPoint& a = polygon[k];
            const ClipPoint& b = polygon[(k + 1) % polygon.size()];
            const double dx = b.x - a.x, dy = b.y - a.y;
            for (int c = 0; c < 4; ++c) {
                if ((dx * (qy[c] - a.y) - dy * (qx[c] - a.x)) * orient < 0) return false;
            }
        }
        return true;
    };
    double lo = 0, hi = 1;
    if (fits(hi)) {
        lo = hi;
    } else {
        // Twelve halvings settle the largest fitting scale to a part in 4096,
        // finer than the boundary it approximates.
        for (int it = 0; it < 12; ++it) {
            const double mid = 0.5 * (lo + hi);
            if (fits(mid)) lo = mid;
            else hi = mid;
        }
    }
    if (lo > 0) {
        // A pixel of margin. The search lands on a rectangle whose corners are
        // only just inside, and a triangle sharing that boundary is exactly the
        // one whose coverage the clip would have altered.
        ix0 = cx - hw * lo + 1;
        ix1 = cx + hw * lo - 1;
        iy0 = cy - hh * lo + 1;
        iy1 = cy + hh * lo - 1;
    }
}

// Where the time actually goes here, sampled on layout-stress at 1 kHz with
// the update dominated by a repaint (2026-09-03), so the next person does not
// have to rediscover it:
//
//   ~28%  copying the emitted triangles out -- the per-triangle inserts, NOT
//         reallocation. Recycling the output buffers across calls (a
//         thread_local scratch, swapped rather than moved) changed nothing
//         measurable: 9.75 ms against 9.66. Reverted.
//   ~45%  the cutting itself: clip_edge's two half-plane tests and lerp_vertex.
//   rest  the per-triangle bounds and the piece walk.
//
// Cutting a straddling triangle against the clip polygon's EDGES rather than
// its triangulated pieces is faster still -- 8.6 ms against 9.7, since a
// rounded rectangle is convex -- but it moves pixels: eight corpus samples
// changed, one by 6,324 of them. Thirty-two sequential clips do not accumulate
// the same interpolation error as three, so it is not the same picture. If it
// is ever wanted, it needs the error accounted for, not just the speed.
void clip_triangles_polygon(const std::vector<Vertex>& vertices,
                            const std::vector<uint32_t>& indices, const PreparedClip& clip,
                            Mesh* out) {
    if (clip.pieces.empty()) return;
    out->vertices.reserve(out->vertices.size() + vertices.size());
    out->indices.reserve(out->indices.size() + indices.size());
    std::vector<Vertex> tri(3), poly, scratch;
    for (size_t i = 0; i + 2 < indices.size(); i += 3) {
        tri[0] = vertices[indices[i]];
        tri[1] = vertices[indices[i + 1]];
        tri[2] = vertices[indices[i + 2]];
        const double minx = std::min({tri[0].position.x, tri[1].position.x, tri[2].position.x});
        const double maxx = std::max({tri[0].position.x, tri[1].position.x, tri[2].position.x});
        const double miny = std::min({tri[0].position.y, tri[1].position.y, tri[2].position.y});
        const double maxy = std::max({tri[0].position.y, tri[1].position.y, tri[2].position.y});
        if (maxx <= clip.x0 || minx >= clip.x1 || maxy <= clip.y0 || miny >= clip.y1) continue;
        if (minx >= clip.ix0 && maxx <= clip.ix1 && miny >= clip.iy0 && maxy <= clip.iy1) {
            const uint32_t base = static_cast<uint32_t>(out->vertices.size());
            out->vertices.insert(out->vertices.end(), tri.begin(), tri.end());
            out->indices.insert(out->indices.end(), {base, base + 1, base + 2});
            continue;
        }
        for (size_t p = 0; p < clip.pieces.size(); ++p) {
            // A piece the triangle cannot reach contributes nothing, and
            // finding that out is four comparisons against the cost of cutting
            // a polygon against three half-planes. Clipping was three quarters
            // of paint on layout-stress, and most of it was this loop grinding
            // through the far side of a rounded rectangle's fan.
            if (p < clip.piece_bounds.size()) {
                const std::array<double, 4>& b = clip.piece_bounds[p];
                if (maxx <= b[0] || minx >= b[2] || maxy <= b[1] || miny >= b[3]) continue;
            }
            const auto& piece = clip.pieces[p];
            clip_to_triangle(tri, piece, &poly, &scratch);
            if (poly.size() < 3) continue;
            const uint32_t base = static_cast<uint32_t>(out->vertices.size());
            out->vertices.insert(out->vertices.end(), poly.begin(), poly.end());
            for (uint32_t k = 1; k + 1 < poly.size(); ++k) {
                out->indices.insert(out->indices.end(), {base, base + k, base + k + 1});
            }
        }
    }
}

void clip_triangles_polygon(const std::vector<Vertex>& vertices,
                            const std::vector<uint32_t>& indices,
                            const std::vector<ClipPoint>& polygon, Mesh* out) {
    // The one-off form: prepares a clip and throws it away. Anything clipping
    // more than one mesh against the same polygon should keep a PreparedClip.
    PreparedClip clip;
    clip.polygon = polygon;
    clip.prepare();
    clip_triangles_polygon(vertices, indices, clip, out);
}

std::vector<ClipPoint> rounded_rect_outline(const Rect& r, const BorderRadii& radii, int segments) {
    const BorderRadii c = clamp_radii_to_rect(radii, r.width, r.height);
    std::vector<ClipPoint> out;
    const double x0 = r.x, y0 = r.y, x1 = r.x + r.width, y1 = r.y + r.height;
    const double kPi = 3.14159265358979323846;
    // Corner centre, radii, start angle; angles run clockwise on a y-down page.
    const auto arc = [&](double cx, double cy, double rx, double ry, double a0) {
        if (rx <= 0 || ry <= 0) return false;
        for (int i = 0; i <= segments; ++i) {
            const double a = a0 + (kPi / 2) * i / segments;
            out.push_back({cx + rx * std::cos(a), cy + ry * std::sin(a)});
        }
        return true;
    };
    if (!arc(x0 + c.top_left.x_radius, y0 + c.top_left.y_radius, c.top_left.x_radius,
             c.top_left.y_radius, kPi)) out.push_back({x0, y0});
    if (!arc(x1 - c.top_right.x_radius, y0 + c.top_right.y_radius, c.top_right.x_radius,
             c.top_right.y_radius, 1.5 * kPi)) out.push_back({x1, y0});
    if (!arc(x1 - c.bottom_right.x_radius, y1 - c.bottom_right.y_radius, c.bottom_right.x_radius,
             c.bottom_right.y_radius, 0)) out.push_back({x1, y1});
    if (!arc(x0 + c.bottom_left.x_radius, y1 - c.bottom_left.y_radius, c.bottom_left.x_radius,
             c.bottom_left.y_radius, 0.5 * kPi)) out.push_back({x0, y1});
    return out;
}

void clip_triangles(const std::vector<Vertex>& vertices, const std::vector<uint32_t>& indices,
                    const Rect& rect, Mesh* out) {
    const double x0 = rect.x, y0 = rect.y, x1 = rect.x + rect.width, y1 = rect.y + rect.height;
    std::vector<Vertex> poly, scratch;
    for (size_t i = 0; i + 2 < indices.size(); i += 3) {
        const Vertex& a = vertices[indices[i]];
        const Vertex& b = vertices[indices[i + 1]];
        const Vertex& c = vertices[indices[i + 2]];
        const double minx = std::min({a.position.x, b.position.x, c.position.x});
        const double maxx = std::max({a.position.x, b.position.x, c.position.x});
        const double miny = std::min({a.position.y, b.position.y, c.position.y});
        const double maxy = std::max({a.position.y, b.position.y, c.position.y});
        if (maxx <= x0 || minx >= x1 || maxy <= y0 || miny >= y1) continue;
        if (minx >= x0 && maxx <= x1 && miny >= y0 && maxy <= y1) {
            const uint32_t base = static_cast<uint32_t>(out->vertices.size());
            out->vertices.push_back(a);
            out->vertices.push_back(b);
            out->vertices.push_back(c);
            out->indices.insert(out->indices.end(), {base, base + 1, base + 2});
            continue;
        }
        poly = {a, b, c};
        // Left, right, top, bottom.
        clip_edge(poly, &scratch, [x0](const Vertex& v) { return v.position.x - x0; },
                  [x0](const Vertex& p, const Vertex& q) { return (x0 - p.position.x) / (q.position.x - p.position.x); });
        poly.swap(scratch);
        if (poly.empty()) continue;
        clip_edge(poly, &scratch, [x1](const Vertex& v) { return x1 - v.position.x; },
                  [x1](const Vertex& p, const Vertex& q) { return (x1 - p.position.x) / (q.position.x - p.position.x); });
        poly.swap(scratch);
        if (poly.empty()) continue;
        clip_edge(poly, &scratch, [y0](const Vertex& v) { return v.position.y - y0; },
                  [y0](const Vertex& p, const Vertex& q) { return (y0 - p.position.y) / (q.position.y - p.position.y); });
        poly.swap(scratch);
        if (poly.empty()) continue;
        clip_edge(poly, &scratch, [y1](const Vertex& v) { return y1 - v.position.y; },
                  [y1](const Vertex& p, const Vertex& q) { return (y1 - p.position.y) / (q.position.y - p.position.y); });
        poly.swap(scratch);
        if (poly.size() < 3) continue;
        const uint32_t base = static_cast<uint32_t>(out->vertices.size());
        for (const Vertex& v : poly) out->vertices.push_back(v);
        for (size_t k = 1; k + 1 < poly.size(); ++k) {
            out->indices.insert(out->indices.end(),
                                {base, base + static_cast<uint32_t>(k), base + static_cast<uint32_t>(k + 1)});
        }
    }
}

} // namespace weva
