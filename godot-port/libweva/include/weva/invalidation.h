#pragma once
#include <vector>

// How much of the pipeline a change forces to run again.
//
// An update ran the whole of it every time: cascade, box build, layout, paint,
// from nothing, because nothing recorded what had actually changed. A host
// that moves a health bar and a host that recolours a focus ring paid the same
// price, and so did a host that changed nothing an element could see.

namespace weva {

enum class Invalidation {
    None = 0,
    // Colours and effects. The boxes stand, and so does their geometry.
    Paint = 1,
    // Sizes and positions change; which boxes exist does not.
    Layout = 2,
    // Which boxes exist changes: `display`, `content`, or the DOM itself.
    Boxes = 3,
};

inline Invalidation worst(Invalidation a, Invalidation b) { return a > b ? a : b; }

// What a change to this property forces.
//
// Only properties KNOWN to be read by nothing but paint are Paint; everything
// else, including every property this table has not heard of, is Boxes. The
// asymmetry is the point -- being wrong in the Paint direction leaves a stale
// document on screen, and being wrong the other way only costs time.
//
// The list is not a guess about what "sounds visual". Each entry was checked
// against every layout source for a read of that property name. Text color
// is read from the retained style during paint; layout holds no color copy.
// `filter`, `transform`, `z-index` and `visibility` remain outside the list
// because positioning and table layout read them.
Invalidation invalidation_for_property(int property_id);

// The property ids this build treats as paint-only, for tests.
const std::vector<int>& paint_only_property_ids();

}   // namespace weva
