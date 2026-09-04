#pragma once
#include "weva/box.h"
#include "weva/text_classes.h"
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
    // The face this item measures with: the context's registration for its
    // font-family stack, or null for the container's default face.
    const FontMetrics* metrics = nullptr;
    // CSS Text L3 §8.2, resolved to pixels. Applied the way the reference's
    // LineBreaker applies it: every measured piece of text is widened by
    // letter_spacing × (characters − 1), so a lone space or a single glyph
    // gains nothing and a word gains one gap per boundary inside it.
    double letter_spacing = 0;
    // CSS Text L3 8.1: extra space at each word separator, on top of the
    // space's own advance.
    double word_spacing = 0;
    // CSS Text L3 7.2 `tab-size`, as a count of spaces. Only preserved text
    // can contain a tab, so this is left at its initial 8 for everything else.
    double tab_spaces = 8;
    // `normal` collapses runs of whitespace and allows breaks; `nowrap`
    // collapses but forbids them; `pre` preserves both.
    bool collapse_whitespace = true;
    bool allow_wrap = true;
    // CSS Text L3 4.1.1: a *segment break* (a newline in the source) is
    // preserved by `pre`, `pre-wrap`, `pre-line` and `break-spaces`, and a
    // preserved segment break forces a line break. This is a third axis, not a
    // restatement of `collapse_whitespace`: `pre-line` collapses spaces and
    // tabs like `normal` while still breaking at every newline.
    bool preserve_newlines = false;
    // CSS Text L3 §5.2: `word-break: break-all` (and `overflow-wrap: anywhere`)
    // make every character boundary a break opportunity, so a word longer than
    // the line is split rather than left to overflow.
    bool break_anywhere = false;
    // CSS Text L3 5.5 `overflow-wrap: break-word`: unlike break-all, the word
    // is kept whole and moved to the next line as usual, and broken only when
    // it is alone on a line and still does not fit.
    bool break_word = false;
    // CSS Text L3 §5.3: which kinsoku prohibitions hold between two CJK
    // characters. Only `loose` and `anywhere` differ from the default.
    LineBreakLevel line_break = LineBreakLevel::Normal;

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

    // A `<br>`: a forced line break. It carries a box because the reference
    // emits one per break — zero width, the line's height — and layout, paint
    // and hit testing all expect to find it on the line rather than inferring
    // the break from a gap.
    BoxId break_box = kNoBox;

    // Marks where an inline box begins in the item stream. It carries no text
    // and no width, and exists so an inline box with NO items of its own still
    // gets a fragment at the right pen position — an `<a>` whose only child was
    // a block that block-in-inline splitting moved into a sibling box is empty
    // here but still occupies a point on the line.
    BoxId inline_box_start = kNoBox;
    // ...and where it ends. CSS 2.1 §10.6.1 / §9.4.2: an inline box's own
    // horizontal margin, border and padding sit on the line at its start and
    // end edges, advance the pen, and belong to its first and last fragments.
    // A `code { padding: 2px 6px }` badge is 12px wider than its text and
    // pushes what follows it by as much.
    BoxId inline_box_end = kNoBox;
    double margin_edge = 0;     // start: margin-left; end: margin-right
    double decoration = 0;      // start: border-left + padding-left; end: the right pair

    // A list marker under `list-style-position: outside`. flush_line lifts it
    // out of the inline flow: it keeps its place on the line and its width,
    // and stops advancing the pen for everything after it.
    bool is_list_marker_outside = false;

    bool is_atom() const { return atom_box != kNoBox; }
    bool is_break() const { return break_box != kNoBox; }
    bool is_inline_start() const { return inline_box_start != kNoBox; }
    bool is_inline_end() const { return inline_box_end != kNoBox; }
    bool is_marker() const { return is_inline_start() || is_inline_end(); }
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
// With a LayoutContext the gaps of a flex container and an item's min-/max-
// width resolve; without one they are read as zero / absent.
double max_content_width(const BoxTree& tree, BoxId id, const LayoutContext* ctx);
// The min-content inline size of a box's content: the widest unbreakable
// piece — a word, an atom, an explicit width — rather than the whole line.
// A flex row that may not wrap still sums its items.
double min_content_width(const BoxTree& tree, BoxId id, const LayoutContext* ctx);

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
