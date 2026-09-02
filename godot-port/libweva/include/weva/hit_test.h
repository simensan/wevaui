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

// The topmost element whose border box contains (`x`, `y`), in document
// coordinates, or null when the point is over nothing.
//
// "Topmost" is the last box paint would have drawn, since paint walks the tree
// in document order and z-index is not ordered yet -- so the search runs the
// same walk backwards and takes the first hit.
//
// Boxes that generate no element of their own (anonymous boxes, line boxes,
// text) hand the hit to the nearest ancestor that has one, which is what makes
// hovering a word hover the paragraph. `visibility: hidden` and
// `pointer-events: none` subtrees are passed through, and a scroll container
// confines the search to what it does not clip away.
const Element* element_at_point(const BoxTree& tree, BoxId root, double x, double y);

}   // namespace weva
