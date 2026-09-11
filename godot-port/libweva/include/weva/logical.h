#pragma once
#include "weva/cascade.h"
#include "weva/computed_style.h"

#include <string>
#include <string_view>
#include <vector>

// Ports Runtime/Css/Cascade/CascadeEngine.Logical.cs — CSS Logical Properties
// L1 flow-relative to physical mapping.
//
// `margin-inline-start` is not a distinct property at layout time; it is
// whichever of margin-left/right/top/bottom the element's writing-mode and
// direction put at the inline start. The mapping is resolved during the
// cascade, before var()/env()/attr() substitution, so everything downstream
// only ever sees physical properties.

namespace weva {

struct LogicalAxes {
    // Physical side names ("left"/"right"/"top"/"bottom") for each logical edge.
    std::string inline_start = "left";
    std::string inline_end = "right";
    std::string block_start = "top";
    std::string block_end = "bottom";
    // False in a vertical writing mode, where inline-size maps to height.
    bool inline_is_horizontal = true;

    static LogicalAxes from(std::string_view direction, std::string_view writing_mode);
};

// Rewrites every logical declaration in `style` onto its physical equivalent.
//
// `winners` is a table of `count` cascade keys indexed by property id; a slot
// counts as set only when its `generation` equals `generation` here, which is
// how the caller reuses one table across every element without clearing it.
//
// A logical alias does NOT unconditionally win: it synthesises a declaration
// carrying the LOGICAL winner's origin/layer/specificity/source order and runs
// the ordinary cascade comparison against the physical winner, so `margin-left`
// declared after `margin-inline-start` at equal specificity keeps the slot, and
// one declared before it loses. The table is updated in place so a later alias
// sees the synthetic declaration.
//
// Not applied to pseudo-elements: the C# engine aliases only on the element
// path, and diverging here would silently change which side a ::before margin
// lands on.
void apply_logical_properties(ComputedStyle* style, CascadeKey* winners, int count,
                              uint64_t generation);

} // namespace weva
