#pragma once
#include "weva/geometry.h"
#include "weva/render_interface.h"

#include <vector>

// The geometry the core builds so backends do not have to. Everything here
// produces indexed triangles in the render interface's vertex format.

namespace weva {

struct Mesh {
    std::vector<Vertex> vertices;
    std::vector<uint32_t> indices;

    bool empty() const { return indices.empty(); }
    // Appends `other`, shifting its indices — how a border's four edges or a
    // box's background and border become one draw call.
    void append(const Mesh& other);
};

// A solid rectangle: two triangles.
void tessellate_rect(const Rect& r, const LinearColor& color, Mesh* out);

// A rounded rectangle, approximating each corner with an arc. `segments` is
// per corner; a zero radius emits the sharp corner with no extra vertices, so
// the common case costs the same as a plain rect.
//
// Fanned from the centre rather than strip-triangulated: a fan is correct for
// any convex outline, and a rounded rect always is.
void tessellate_rounded_rect(const Rect& r, const BorderRadii& radii, const LinearColor& color,
                             Mesh* out, int segments = 8);

// The ring between an outer and an inner rounded rect — the shape of a border.
// Emitted as one mesh rather than four edges so a mitred corner between two
// different colours does not double-cover, and so a uniform border is one draw.
//
// `colors` are top, right, bottom and left; each vertex takes the colour of the
// edge it belongs to, and a corner blends between its two.
void tessellate_border(const Rect& outer, const BorderRadii& outer_radii, double top,
                       double right, double bottom, double left,
                       const LinearColor colors[4], Mesh* out, int segments = 8);

// Shrinks a rounded rect's radii inward by the border widths, which is what
// gives the inner edge of a border its correct curvature. A radius never goes
// below zero.
BorderRadii inset_radii(const BorderRadii& r, double top, double right, double bottom,
                        double left);

// CSS Backgrounds §5.5: when adjacent radii overlap along an edge, every radius
// is scaled by the same factor until they fit. Scaling per corner instead would
// change the shape's proportions.
BorderRadii clamp_radii_to_rect(const BorderRadii& r, double width, double height);

} // namespace weva
