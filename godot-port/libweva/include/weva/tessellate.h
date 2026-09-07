#pragma once
#include "weva/geometry.h"
#include "weva/render_interface.h"

#include <array>
#include <vector>

// The geometry the core builds so backends do not have to. Everything here
// produces indexed triangles in the render interface's vertex format.

namespace weva {

struct Mesh {
    std::vector<Vertex> vertices;
    std::vector<uint32_t> indices;

    bool empty() const { return indices.empty(); }
    // Reserve a known append before emitting individual vertices/indices.
    // Repeated appends keep geometric growth instead of reallocating for
    // every shape. Existing geometry and ordering are unchanged.
    void reserve_append(size_t vertex_count, size_t index_count);
    // Appends `other`, shifting its indices — how a border's four edges or a
    // box's background and border become one draw call.
    void append(const Mesh& other);
};

// A solid rectangle: two triangles.
void tessellate_rect(const Rect& r, const LinearColor& color, Mesh* out,
                     bool antialias = true);

// A rounded rectangle, approximating each corner with an arc. `segments` is
// per corner; a zero radius emits the sharp corner with no extra vertices, so
// the common case costs the same as a plain rect.
//
// Fanned from the centre rather than strip-triangulated: a fan is correct for
// any convex outline, and a rounded rect always is.
void tessellate_rounded_rect(const Rect& r, const BorderRadii& radii, const LinearColor& color,
                             Mesh* out, int segments = 8, bool antialias = true);

// The ring between an outer and an inner rounded rect — the shape of a border.
// Emitted as one mesh rather than four edges so a mitred corner between two
// different colours does not double-cover, and so a uniform border is one draw.
//
// `colors` are top, right, bottom and left. Different colors meet at mitres
// proportional to the adjacent border widths; straight edges stay one color.
void tessellate_border(const Rect& outer, const BorderRadii& outer_radii, double top,
                       double right, double bottom, double left,
                       const LinearColor colors[4], Mesh* out, int segments = 8,
                       bool antialias = true);

// Shrinks a rounded rect's radii inward by the border widths, which is what
// gives the inner edge of a border its correct curvature. A radius never goes
// below zero.
BorderRadii inset_radii(const BorderRadii& r, double top, double right, double bottom,
                        double left);

// Clips a triangle list to `rect` (Sutherland–Hodgman per triangle, colour and
// texture coordinates interpolated), appending the pieces to `out`. Triangles
// wholly inside pass through; wholly outside vanish. This is how a scissor
// reaches a host whose canvas cannot clip per draw: the geometry arrives
// already cut.
struct ClipPoint {
    double x = 0, y = 0;
};

// Clips triangles to a simple polygon — convex or not, either winding. The
// polygon is triangulated by ear clipping and every mesh triangle is clipped
// (Sutherland-Hodgman) against each piece; the pieces tile the polygon, so the
// output covers exactly the intersection. Attributes interpolate as for the
// rectangle form. This is what `clip-path` and a rounded `overflow: hidden`
// resolve to, since the backends know only rectangular scissors.
// A clip polygon with everything about it that does not depend on what is
// being clipped, worked out once.
//
// clip_triangles_polygon needs the polygon triangulated, its bounds, and the
// largest axis-aligned rectangle that fits inside it. All three are properties
// of the POLYGON, and computing them per draw meant a rounded scroller paid
// for them once for every box inside it -- 268 times on layout-stress, which
// put the function back at the top of the profile even though the clipping
// itself had already been skipped for most triangles.
struct PreparedClip {
    std::vector<ClipPoint> polygon;
    std::vector<std::array<ClipPoint, 3>> pieces;
    // One bounding box per piece, so a triangle that straddles the clip is cut
    // against the two or three pieces it can actually reach rather than all
    // thirty-odd of a rounded rectangle's fan. Same output, less of it.
    std::vector<std::array<double, 4>> piece_bounds;   // x0, y0, x1, y1
    double x0 = 0, y0 = 0, x1 = 0, y1 = 0;          // bounds
    double ix0 = 0, iy0 = 0, ix1 = -1, iy1 = -1;    // the rectangle that fits inside
    bool convex = false;

    void prepare();
};

// Clips against a polygon prepared in advance. Triangles wholly inside a
// convex clip retain their shared vertices and original UV/color/coverage. Crossing
// triangles retain the existing interpolation path. The overload taking a
// bare polygon prepares one and throws it away, which is right for a one-off.
void clip_triangles_polygon(const std::vector<Vertex>& vertices,
                            const std::vector<uint32_t>& indices, const PreparedClip& clip,
                            Mesh* out);

void clip_triangles_polygon(const std::vector<Vertex>& vertices,
                            const std::vector<uint32_t>& indices,
                            const std::vector<ClipPoint>& polygon, Mesh* out);

// The outline of a rounded rectangle as a polygon: `segments` points along
// each rounded corner's arc, a single point at a square one. Radii are clamped
// to the rectangle as CSS Backgrounds §5.5 does.
std::vector<ClipPoint> rounded_rect_outline(const Rect& r, const BorderRadii& radii,
                                            int segments = 8);

void clip_triangles(const std::vector<Vertex>& vertices, const std::vector<uint32_t>& indices,
                    const Rect& rect, Mesh* out);

// CSS Backgrounds §5.5: when adjacent radii overlap along an edge, every radius
// is scaled by the same factor until they fit. Scaling per corner instead would
// change the shape's proportions.
BorderRadii clamp_radii_to_rect(const BorderRadii& r, double width, double height);

} // namespace weva
