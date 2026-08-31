#pragma once
#include "weva/box.h"
#include "weva/font_metrics.h"
#include "weva/style_resolver.h"

#include <string>
#include <string_view>
#include <vector>

// Ports the core of Runtime/Layout/{InlineLayout,LineBreaker}.cs — turning a
// block container's inline content into line boxes.
//
// The shape of the result matters as much as the numbers: the container's
// children are replaced by LineBox children, each holding the text runs that
// landed on it. Paint and hit testing walk that structure; nothing downstream
// re-derives line membership.

namespace weva {

// One piece of inline content, flattened out of the inline box tree. The tree
// structure is not lost — each item remembers the inline box it came from — but
// line breaking works on a flat sequence, because a line break can fall
// anywhere in it regardless of nesting.
struct InlineItem {
    BoxId source_run = kNoBox;      // the text box this came from
    BoxId inline_parent = kNoBox;   // nearest enclosing inline box, or kNoBox
    std::string_view text;
    const ComputedStyle* style = nullptr;
    double font_size = 16;
    double line_height = 0;
    // `normal` collapses runs of whitespace and allows breaks; `nowrap`
    // collapses but forbids them; `pre` preserves both.
    bool collapse_whitespace = true;
    bool allow_wrap = true;
};

// Lays out `container`'s inline content into line boxes, replacing its children.
// Returns the content height.
//
// `available_width` is the container's content width. Text is measured through
// `metrics`, which is the only part of this that needs a font backend.
double layout_inline(BoxTree* tree, BoxId container, double available_width,
                     const LayoutContext& ctx, const FontMetrics& metrics);

// Collects the flattened inline sequence, exposed for tests: getting the
// whitespace handling right is most of the work, and it is far easier to check
// on the sequence than through the resulting geometry.
std::vector<InlineItem> collect_inline_items(const BoxTree& tree, BoxId container,
                                             const LayoutContext& ctx);

// CSS Text §7: resolves `start`/`end` against the direction, so layout only
// ever deals with left/right/center/justify.
std::string_view resolve_text_align(const ComputedStyle* style);

} // namespace weva
