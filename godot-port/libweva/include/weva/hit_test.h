#pragma once
#include "weva/box.h"

// Which element is under a point.
//
// The cascade has always been able to match :hover, :active and :focus -- the
// matcher takes an ElementStateProvider and ElementState carries the bits --
// but nothing could say WHERE the pointer was, so the provider was always the
// null one and those rules matched nothing. This is the missing half.

namespace weva {

class Element;
struct LayoutContext;

// The topmost element whose border box contains (`x`, `y`), in document
// coordinates, or null when the point is over nothing.
//
// "Topmost" follows reverse child paint order. CSS transforms are inverted
// before testing geometry; pass the document context for relative units.
//
// Boxes that generate no element of their own (anonymous boxes, line boxes,
// text) hand the hit to the nearest ancestor that has one, which is what makes
// hovering a word hover the paragraph. `visibility: hidden` and
// `pointer-events: none` subtrees are passed through, and a scroll container
// confines the search to what it does not clip away.
const Element* element_at_point(const BoxTree& tree, BoxId root, double x, double y,
                                const LayoutContext* context = nullptr);

// The same search, stopping at the box rather than the element that owns it.
// A wheel needs this: what it scrolls is the nearest scroll container ABOVE
// the point, which is a walk up the box tree from here.
BoxId box_at_point(const BoxTree& tree, BoxId root, double x, double y,
                   const LayoutContext* context = nullptr);

// Undo the box's and its ancestors' CSS transforms, preserving the document
// layout coordinate system (including scroll). False for a singular transform.
bool point_to_layout(const BoxTree& tree, BoxId box, const LayoutContext& context,
                     double* x, double* y);

}   // namespace weva
