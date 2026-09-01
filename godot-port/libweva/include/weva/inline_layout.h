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

class FloatContext;

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

    // An inline-level block (inline-block, inline-flex, ...) embedded in the
    // line. An atom is placed whole: never split, broken, or tokenised. The
    // caller sizes it and fills these in before layout, because sizing it needs
    // the block layout engine.
    BoxId atom_box = kNoBox;
    double atom_outer_width = 0;   // border box plus horizontal margins
    // Distance from the atom's top edge up to the line baseline. Per spec an
    // inline-block's baseline is the bottom of its content, which for a box
    // with no inline content of its own is its bottom margin edge.
    double atom_baseline = 0;

    bool is_atom() const { return atom_box != kNoBox; }
};

// Lays out `container`'s inline content into line boxes, replacing its children.
// Returns the content height.
//
// `available_width` is the container's content width. Text is measured through
// `metrics`, which is the only part of this that needs a font backend.
double layout_inline(BoxTree* tree, BoxId container, double available_width,
                     const LayoutContext& ctx, const FontMetrics& metrics);

// Same, over an already-collected item list. Splitting the two is what lets the
// caller size the atoms in between — and what lets a shrink-to-fit probe run
// the layout twice, since the first run replaces the container's children with
// line boxes and the source runs can no longer be walked.
// The floats a line box has to avoid (CSS 2.1 §9.5: line boxes next to a float
// are shortened to make room for it). Absent when the container's formatting
// context holds no floats, which is the common case and costs nothing.
//
// The extents are measured from the BFC's content-left edge, and this treats
// the container's content box as aligned with it — the same assumption
// place_float already makes. That is exact when the container is the BFC root
// or shares its horizontal frame, and approximate when it is indented inside
// one; a container with its own left padding inside a float-bearing BFC will
// narrow by slightly too much.
struct InlineFloatEnv {
    const FloatContext* floats = nullptr;
    // BFC y of the container's content-top edge, so a line at container-local
    // y maps into the frame the float extents are recorded in.
    double bfc_content_top = 0;
};

double layout_inline_items(BoxTree* tree, BoxId container,
                           const std::vector<InlineItem>& items, double available_width,
                           const LayoutContext& ctx, const FontMetrics& metrics,
                           const InlineFloatEnv* floats = nullptr);

// CSS Sizing L3: the widest the content wants to be, given unlimited width.
// Floats and out-of-flow boxes are excluded — the containing block flows around
// them, so they do not contribute to its intrinsic inline size.
//
// Returns a CONTENT width: the caller adds the frame to reach a border box.
double max_content_width(const BoxTree& tree, BoxId id);

// Collects the flattened inline sequence, exposed for tests: getting the
// whitespace handling right is most of the work, and it is far easier to check
// on the sequence than through the resulting geometry.
std::vector<InlineItem> collect_inline_items(const BoxTree& tree, BoxId container,
                                             const LayoutContext& ctx,
                                             const FontMetrics* metrics = nullptr);

// CSS Text §7: resolves `start`/`end` against the direction, so layout only
// ever deals with left/right/center/justify.
std::string_view resolve_text_align(const ComputedStyle* style);

} // namespace weva
